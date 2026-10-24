using System.Text;
using PrismRGB.Core.Drivers.Msi;
using PrismRGB.Core.Hid;

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

    public static IReadOnlyList<ScanEntry> Scan(SystemInfo sys, IReadOnlyList<HidDeviceInfo> hid, IReadOnlyList<(string name, string? problem)> nativeResults)
    {
        var list = new List<ScanEntry>();

        // Carte mère
        if (!string.IsNullOrWhiteSpace(sys.BoardProduct))
        {
            var msi = nativeResults.FirstOrDefault(r => r.name.StartsWith("MSI", StringComparison.OrdinalIgnoreCase));
            if (sys.IsMsiBoard && msi.name is not null && msi.problem is null)
                list.Add(new("Carte mère", sys.MsiBoardName, "Mystic Light : connecteurs RGB/ARGB (ventilos, bandes, water cooling branchés dessus)", ScanStatus.Controlled));
            else if (sys.IsMsiBoard)
                list.Add(new("Carte mère", sys.MsiBoardName, msi.problem ?? "Contrôleur Mystic Light introuvable sur l'USB.", ScanStatus.Problem,
                    "Vérifie que Mystic Light est activé dans le BIOS (« EZ LED Control ») et qu'aucun autre logiciel RGB ne tourne."));
            else
                list.Add(new("Carte mère", $"{sys.BoardManufacturer} {sys.BoardProduct}", "Fabricant non encore pris en charge.", ScanStatus.NotYet));
        }

        // RAM
        foreach (var m in sys.Ram)
        {
            string name = string.Join(" ", new[] { m.Manufacturer, m.PartNumber }.Where(s => !string.IsNullOrWhiteSpace(s)));
            list.Add(new("RAM", $"{m.Slot} : {name}", $"{m.CapacityBytes / (1024 * 1024 * 1024)} Go, {m.SpeedMhz} MHz", ScanStatus.NeedsLowLevel,
                "Le RGB des barrettes passe par le bus SMBus de la carte mère : il faut un petit pilote système signé pour y accéder. Prochaine étape, une fois le modèle exact connu."));
        }

        // Carte graphique
        foreach (var g in sys.Gpus)
        {
            if (g.Name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new("Carte graphique", g.Name, string.IsNullOrEmpty(g.SubsystemVendor) ? g.PnpId : $"Modèle {g.SubsystemVendor}", ScanStatus.NotYet,
                "Le RGB des cartes graphiques se pilote via le bus I2C du pilote NVIDIA/AMD, avec un protocole propre à chaque marque. À ajouter selon le modèle."));
        }

        // Contrôleurs USB RGB connus (un par appareil physique)
        foreach (var grp in hid.Where(d => RgbVendors.ContainsKey(d.VendorId)).GroupBy(d => (d.VendorId, d.ProductId)))
        {
            var d = grp.First();
            if (MsiBoards.LooksLikeMysticLight(d)) continue; // déjà couvert par la ligne carte mère
            string vendor = RgbVendors[d.VendorId];
            string name = string.IsNullOrWhiteSpace(d.Product) ? $"Appareil {vendor}" : d.Product;
            list.Add(new("USB", name, $"{vendor}  ·  {d.VidPid}", ScanStatus.NotYet,
                "Contrôleur détecté. Son pilote peut être ajouté : envoie le rapport (bouton ci-dessous)."));
        }

        if (list.Count == 0)
            list.Add(new("Système", "Inventaire indisponible", "Le scan matériel ne fonctionne que sous Windows.", ScanStatus.Info));
        return list;
    }

    public static string BuildReport(SystemInfo sys, IReadOnlyList<HidDeviceInfo> hid, IReadOnlyList<ScanEntry> entries, string log)
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
