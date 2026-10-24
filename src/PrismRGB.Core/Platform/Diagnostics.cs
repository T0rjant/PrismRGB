using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using PrismRGB.Core.Devices;

namespace PrismRGB.Core.Platform;

/// <summary>
/// Rapport texte décrivant le PC : carte mère, RAM, GPU, tous les périphériques USB/HID (VID:PID)
/// et ce qu'OpenRGB a détecté. Sert à écrire des pilotes natifs pour le matériel exact.
/// </summary>
public static partial class Diagnostics
{
    public static string BuildReport(IEnumerable<IRgbBackend> backends, string? configPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== PrismRGB - rapport de diagnostic ===");
        sb.AppendLine($"Date : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Système : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"PrismRGB : {typeof(Diagnostics).Assembly.GetName().Version}");
        sb.AppendLine($"Admin : {(IsElevated() ? "oui" : "non")}");
        sb.AppendLine($"Config : {configPath}");
        sb.AppendLine();

        if (OperatingSystem.IsWindows())
        {
            AppendWmi(sb);
        }
        else
        {
            sb.AppendLine("(Inventaire matériel disponible uniquement sous Windows)");
        }

        sb.AppendLine();
        sb.AppendLine("--- Logiciels RGB concurrents détectés ---");
        var conflicts = ConflictDetector.Scan();
        if (conflicts.Count == 0) sb.AppendLine("aucun");
        foreach (var c in conflicts) sb.AppendLine($"{c.Label}  [{c.ProcessName}.exe, pid {c.Pid}]");

        foreach (var b in backends)
        {
            sb.AppendLine();
            sb.AppendLine($"--- Backend {b.Name} : {b.State} - {b.StatusText} ---");
            foreach (var d in b.Devices)
            {
                sb.AppendLine($"[{d.BackendIndex}] {d.Name}");
                sb.AppendLine($"    Type : {RgbDevice.KindLabel(d.Kind)} | Vendeur : {d.Vendor} | LEDs : {d.LedCount} | Mode direct : {(d.HasDirectMode ? "oui" : "NON")}");
                if (!string.IsNullOrEmpty(d.Description)) sb.AppendLine($"    Description : {d.Description}");
                if (!string.IsNullOrEmpty(d.Location)) sb.AppendLine($"    Emplacement : {d.Location}");
                if (!string.IsNullOrEmpty(d.Firmware)) sb.AppendLine($"    Version : {d.Firmware}");
                if (d.HardwareModes.Count > 0) sb.AppendLine($"    Modes matériels : {string.Join(", ", d.HardwareModes)}");
                foreach (var z in d.Zones)
                    sb.AppendLine($"    Zone {z.Index} \"{z.Name}\" : {z.LedCount} LEDs ({z.Kind}{(z.IsResizable ? $", réglable {z.MinLeds}-{z.MaxLeds}" : "")})");
            }
        }
        return sb.ToString();
    }

    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    [SupportedOSPlatform("windows")]
    private static void AppendWmi(StringBuilder sb)
    {
        Section(sb, "Processeur", "SELECT Name FROM Win32_Processor", "Name");
        Section(sb, "Carte mère", "SELECT Manufacturer, Product, Version FROM Win32_BaseBoard", "Manufacturer", "Product", "Version");
        Section(sb, "BIOS", "SELECT Manufacturer, SMBIOSBIOSVersion FROM Win32_BIOS", "Manufacturer", "SMBIOSBIOSVersion");
        Section(sb, "Barrettes de RAM", "SELECT DeviceLocator, Manufacturer, PartNumber, Capacity, ConfiguredClockSpeed FROM Win32_PhysicalMemory",
            "DeviceLocator", "Manufacturer", "PartNumber", "Capacity", "ConfiguredClockSpeed");
        Section(sb, "Carte graphique", "SELECT Name, PNPDeviceID, DriverVersion FROM Win32_VideoController", "Name", "PNPDeviceID", "DriverVersion");
        Section(sb, "Contrôleurs SMBus", "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%SMBus%'", "Name", "PNPDeviceID");

        sb.AppendLine("--- Périphériques USB / HID (VID:PID) ---");
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, Manufacturer, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID%' OR PNPDeviceID LIKE 'HID\\\\VID%'");
            var rows = new SortedSet<string>();
            foreach (var o in s.Get())
            {
                string id = o["PNPDeviceID"]?.ToString() ?? "";
                var m = VidPid().Match(id);
                string vp = m.Success ? $"{m.Groups[1].Value}:{m.Groups[2].Value}" : "????:????";
                rows.Add($"{vp}  {o["Name"]}  ({o["Manufacturer"]})  {id}");
            }
            foreach (var r in rows) sb.AppendLine(r);
        }
        catch (Exception ex) { sb.AppendLine($"(erreur WMI : {ex.Message})"); }
    }

    [SupportedOSPlatform("windows")]
    private static void Section(StringBuilder sb, string title, string query, params string[] props)
    {
        sb.AppendLine($"--- {title} ---");
        try
        {
            using var s = new ManagementObjectSearcher(query);
            foreach (var o in s.Get())
                sb.AppendLine(string.Join(" | ", props.Select(p => $"{p}={o[p]}")));
        }
        catch (Exception ex) { sb.AppendLine($"(erreur WMI : {ex.Message})"); }
    }

    [GeneratedRegex(@"VID_([0-9A-Fa-f]{4}).*PID_([0-9A-Fa-f]{4})")]
    private static partial Regex VidPid();
}
