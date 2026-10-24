// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using PrismRGB.Core.Drivers.Msi;
using PrismRGB.Core.Hid;
using PrismRGB.Core.Smbus;

namespace PrismRGB.Core.Platform;

public enum ScanStatus
{
    /// <summary>Piloté par PrismRGB.</summary>
    Controlled,
    /// <summary>Détecté, pilote pas encore écrit.</summary>
    NotYet,
    /// <summary>Nécessite un accès bas niveau (bus SMBus/I2C).</summary>
    NeedsLowLevel,
    /// <summary>Simple information.</summary>
    Info,
    /// <summary>Problème (bloqué, erreur...).</summary>
    Problem,
}

public sealed record ScanEntry(string Category, string Name, string Detail, ScanStatus Status, string Advice = "");

/// <summary>
/// Inventaire de tout ce qui peut avoir du RGB dans le PC : carte mère, RAM, carte graphique,
/// contrôleurs USB connus (ventilos, water cooling, hubs). Indique ce qui est piloté et ce qui ne l'est pas encore.
/// </summary>
public static class HardwareScanner
{
    /// <summary>Fabricants USB connus pour leurs contrôleurs RGB.</summary>
    private static readonly Dictionary<ushort, string> RgbVendors = new()
    {
        [0x1462] = "MSI",
        [0x0DB0] = "MSI",
        [0x0B05] = "ASUS (Aura)",
        [0x1B1C] = "Corsair",
        [0x1E71] = "NZXT",
        [0x0CF2] = "Lian Li",
        [0x048D] = "ITE (souvent Gigabyte RGB Fusion)",
        [0x26CE] = "ASRock",
        [0x1532] = "Razer",
        [0x046D] = "Logitech",
        [0x1038] = "SteelSeries",
        [0x2516] = "Cooler Master",
        [0x264A] = "Thermaltake",
        [0x3633] = "DeepCool",
        [0x0951] = "HyperX",
        [0x03F0] = "HyperX / HP",
        [0x1E7D] = "Roccat",
        [0x2433] = "Asetek (water cooling)",
    };

    public static IReadOnlyList<ScanEntry> Scan(SystemInfo sys, IReadOnlyList<HidDeviceInfo> hid,
        IReadOnlyList<(string name, string? problem)> nativeResults, Native.RamState? ram = null)
    {
        var list = new List<ScanEntry>();
        ram ??= Native.RamState.Initial;

        // Carte mère (toutes marques : le pilote qui l'a reconnue est indiqué)
        string board = string.Join(" ", new[] { BrandOf(sys.BoardManufacturer), sys.BoardProduct }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var ok = nativeResults.Where(r => r.problem is null).ToList();
        var ko = nativeResults.Where(r => r.problem is not null).ToList();
        if (!string.IsNullOrWhiteSpace(board) || nativeResults.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(board)) board = "Carte mère";
            if (ok.Count > 0)
                list.Add(new("Carte mère", board, $"{string.Join(", ", ok.Select(r => r.name))} : connecteurs RGB/ARGB (ventilos, bandes, water cooling branchés dessus)", ScanStatus.Controlled));
            else if (ko.Count > 0)
                list.Add(new("Carte mère", board, ko[0].problem!, ScanStatus.Problem,
                    "Vérifie que l'éclairage RGB est activé dans le BIOS et qu'aucun autre logiciel RGB ne tourne (voir Réglages)."));
            else if (BrandOf(sys.BoardManufacturer) is "MSI" or "ASUS" or "ASRock" or "Gigabyte")
                list.Add(new("Carte mère", board, "Aucun contrôleur RGB de cette carte trouvé sur l'USB.", ScanStatus.Problem,
                    "Certaines cartes pilotent leur RGB par le bus SMBus ou un modèle de contrôleur pas encore pris en charge. Envoie le rapport pour l'ajouter."));
            else
                list.Add(new("Carte mère", board, "Fabricant pas encore pris en charge.", ScanStatus.NotYet));
        }

        // RAM
        AddRam(list, sys, ram);

        // Carte graphique
        foreach (var g in sys.Gpus)
        {
            if (g.Name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new("Carte graphique", g.Name, string.IsNullOrEmpty(g.SubsystemVendor) ? g.PnpId : $"Modèle {g.SubsystemVendor}", ScanStatus.NotYet,
                "Le RGB des cartes graphiques passe par le bus I2C du pilote NVIDIA/AMD, avec un protocole propre à chaque marque. À ajouter selon le modèle."));
        }

        // Contrôleurs USB RGB connus non pris en charge (un par appareil physique)
        foreach (var grp in hid.Where(d => RgbVendors.ContainsKey(d.VendorId)).GroupBy(d => (d.VendorId, d.ProductId)))
        {
            var d = grp.First();
            if (MsiBoards.LooksLikeMysticLight(d)) continue;
            if (grp.Any(x => Native.DriverRegistry.Hid.Any(dr => dr.IsCandidate(x)))) continue; // déjà couvert par un pilote
            string vendor = RgbVendors[d.VendorId];
            string name = string.IsNullOrWhiteSpace(d.Product) ? $"Appareil {vendor}" : d.Product;
            list.Add(new("USB", name, $"{vendor}  ·  {d.VidPid}", ScanStatus.NotYet,
                "Contrôleur détecté. Son pilote peut être ajouté : envoie le rapport (bouton ci-dessus)."));
        }

        if (list.Count == 0)
            list.Add(new("Système", "Inventaire indisponible", "Le scan matériel ne fonctionne que sous Windows.", ScanStatus.Info));
        return list;
    }

    private static void AddRam(List<ScanEntry> list, SystemInfo sys, Native.RamState ram)
    {
        string Describe(RamModule m) => string.Join(" ", new[] { m.Manufacturer, m.PartNumber }.Where(x => !string.IsNullOrWhiteSpace(x)));
        switch (ram.Access)
        {
            case Native.RamAccess.Ok:
                foreach (var c in ram.Controlled) list.Add(new("RAM", c, ram.Message, ScanStatus.Controlled));
                return;
            case Native.RamAccess.NoRgb:
                foreach (var m in sys.Ram) list.Add(new("RAM", $"{m.Slot} : {Describe(m)}", ram.Message, ScanStatus.Info,
                    "Si tes barrettes ont du RGB, envoie le rapport : leur contrôleur pourra être ajouté."));
                return;
            case Native.RamAccess.BusError:
                list.Add(new("RAM", "Bus SMBus", ram.Message, ScanStatus.Problem));
                return;
        }
        string advice = ram.Access switch
        {
            Native.RamAccess.PawnIoMissing => "Installe le pilote gratuit PawnIO (bouton dans Réglages > RAM), puis relance l'analyse.",
            Native.RamAccess.NeedsAdmin => "Clique sur « Redémarrer en administrateur » dans Réglages > RAM.",
            Native.RamAccess.Unsupported => ram.Message,
            Native.RamAccess.Scanning => "Analyse en cours...",
            _ => "Active « Contrôle du RGB de la RAM » dans Réglages : PrismRGB lira tes barrettes et pilotera leur RGB.",
        };
        foreach (var m in sys.Ram)
            list.Add(new("RAM", $"{m.Slot} : {Describe(m)}", $"{m.CapacityBytes / (1024 * 1024 * 1024)} Go, {m.SpeedMhz} MHz", ScanStatus.NeedsLowLevel, advice));
        if (sys.Ram.Count == 0 && ram.Access != Native.RamAccess.Disabled)
            list.Add(new("RAM", "Barrettes", ram.Message, ScanStatus.NeedsLowLevel, advice));
    }

    public static string BrandOf(string manufacturer)
    {
        string m = manufacturer.ToLowerInvariant();
        if (m.Contains("micro-star") || m.StartsWith("msi")) return "MSI";
        if (m.Contains("asustek") || m.StartsWith("asus")) return "ASUS";
        if (m.Contains("asrock")) return "ASRock";
        if (m.Contains("gigabyte")) return "Gigabyte";
        return manufacturer.Trim();
    }

    public static string BuildReport(SystemInfo sys, IReadOnlyList<HidDeviceInfo> hid, IReadOnlyList<ScanEntry> entries, string log, Native.RamState? ram = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== PrismRGB - rapport matériel ===");
        sb.AppendLine($"Date : {DateTime.Now:yyyy-MM-dd HH:mm:ss}  |  PrismRGB {typeof(HardwareScanner).Assembly.GetName().Version}");
        sb.AppendLine($"Système : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        sb.AppendLine($"Processeur : {sys.Cpu}");
        sb.AppendLine($"Carte mère : {sys.BoardManufacturer} | {sys.BoardProduct}");
        foreach (var g in sys.Gpus) sb.AppendLine($"GPU : {g.Name} | {g.SubsystemVendor} | {g.PnpId}");
        foreach (var m in sys.Ram) sb.AppendLine($"RAM : {m.Slot} | {m.Manufacturer} | {m.PartNumber} | {m.CapacityBytes} | {m.SpeedMhz} MHz");
        sb.AppendLine();
        sb.AppendLine("--- Résultat du scan ---");
        foreach (var e in entries) sb.AppendLine($"[{e.Status}] {e.Category} | {e.Name} | {e.Detail}");
        if (ram is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"--- RAM (SMBus) : {ram.Access} - {ram.Message} ---");
            foreach (var slot in ram.Slots)
                sb.AppendLine($"Slot {slot.Index} (0x{slot.SpdAddress:X2}) : {slot.Type} | JEDEC {slot.JedecId:X4} ({slot.Vendor}) | {slot.PartNumber}");
        }
        sb.AppendLine();
        sb.AppendLine("--- Périphériques HID (VID:PID page/usage | fabricant | produit | tailles in/out/feature) ---");
        foreach (var d in hid.OrderBy(d => d.VendorId).ThenBy(d => d.ProductId))
            sb.AppendLine($"{d.VidPid}  {d.UsagePage:X4}/{d.Usage:X2}  | {d.Manufacturer} | {d.Product} | {d.InputReportLength}/{d.OutputReportLength}/{d.FeatureReportLength}");
        sb.AppendLine();
        sb.AppendLine("--- Journal ---");
        sb.AppendLine(log);
        return sb.ToString();
    }
}
