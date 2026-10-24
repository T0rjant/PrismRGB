// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace PrismRGB.Core.Platform;

public sealed record RamModule(string Slot, string Manufacturer, string PartNumber, long CapacityBytes, int SpeedMhz);

public sealed record GpuInfo(string Name, string PnpId, string SubsystemVendor);

/// <summary>Inventaire matériel via WMI (Windows). Vide ailleurs.</summary>
public sealed partial class SystemInfo
{
    public string BoardManufacturer { get; init; } = "";
    public string BoardProduct { get; init; } = "";
    public string Cpu { get; init; } = "";
    public IReadOnlyList<RamModule> Ram { get; init; } = [];
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];

    /// <summary>Nom de carte au format OpenRGB/MSI : "MSI " + produit, ex. "MSI MAG X870 TOMAHAWK WIFI (MS-7E51)".</summary>
    public string MsiBoardName => "MSI " + BoardProduct;

    public bool IsMsiBoard =>
        BoardManufacturer.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) ||
        BoardManufacturer.StartsWith("MSI", StringComparison.OrdinalIgnoreCase);

    public static SystemInfo Empty { get; } = new();

    public static SystemInfo Query()
    {
        if (!OperatingSystem.IsWindows()) return Empty;
        try { return QueryWindows(); }
        catch { return Empty; }
    }

    [SupportedOSPlatform("windows")]
    private static SystemInfo QueryWindows()
    {
        string boardMan = "", boardProd = "", cpu = "";
        foreach (var o in Wmi("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
        {
            boardMan = Str(o, "Manufacturer");
            boardProd = Str(o, "Product");
        }
        foreach (var o in Wmi("SELECT Name FROM Win32_Processor")) cpu = Str(o, "Name");

        var ram = new List<RamModule>();
        foreach (var o in Wmi("SELECT DeviceLocator, Manufacturer, PartNumber, Capacity, ConfiguredClockSpeed FROM Win32_PhysicalMemory"))
        {
            long.TryParse(Str(o, "Capacity"), out long cap);
            int.TryParse(Str(o, "ConfiguredClockSpeed"), out int mhz);
            ram.Add(new RamModule(Str(o, "DeviceLocator"), Str(o, "Manufacturer"), Str(o, "PartNumber"), cap, mhz));
        }

        var gpus = new List<GpuInfo>();
        foreach (var o in Wmi("SELECT Name, PNPDeviceID FROM Win32_VideoController"))
        {
            string pnp = Str(o, "PNPDeviceID");
            var m = Subsys().Match(pnp);
            string vendor = m.Success ? PciVendorName(m.Groups[1].Value) : "";
            gpus.Add(new GpuInfo(Str(o, "Name"), pnp, vendor));
        }

        return new SystemInfo { BoardManufacturer = boardMan, BoardProduct = boardProd, Cpu = cpu, Ram = ram, Gpus = gpus };
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<ManagementBaseObject> Wmi(string query)
    {
        using var s = new ManagementObjectSearcher(query);
        foreach (var o in s.Get()) yield return o;
    }

    [SupportedOSPlatform("windows")]
    private static string Str(ManagementBaseObject o, string p) => o[p]?.ToString()?.Trim() ?? "";

    // SUBSYS_ssssvvvv : les 4 derniers chiffres hexadécimaux = fabricant de la carte (sous-système PCI).
    [GeneratedRegex(@"SUBSYS_[0-9A-Fa-f]{4}([0-9A-Fa-f]{4})")]
    private static partial Regex Subsys();

    public static string PciVendorName(string hex) => hex.ToUpperInvariant() switch
    {
        "1043" => "ASUS",
        "1462" => "MSI",
        "1458" => "Gigabyte",
        "10DE" => "NVIDIA (Founders Edition)",
        "19DA" => "Zotac",
        "1569" => "Palit",
        "196E" => "PNY",
        "1B4C" => "Galax / KFA2",
        "3842" => "EVGA",
        "7377" => "Colorful",
        "1DA2" => "Sapphire",
        "148C" => "PowerColor",
        "1682" => "XFX",
        "1849" => "ASRock",
        _ => $"fabricant {hex}",
    };
}
