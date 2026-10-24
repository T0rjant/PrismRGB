// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace PrismRGB.Core.Platform;

public sealed record ConflictingApp(string ProcessName, string Label, int Pid);

/// <summary>
/// Repère les logiciels RGB des constructeurs qui se battent pour le contrôle du matériel.
/// C'est la cause n°1 des "ça marche un coup sur deux" : deux programmes écrivent sur le même contrôleur.
/// Liste indicative de noms de processus connus, à compléter au besoin.
/// </summary>
public static class ConflictDetector
{
    private static readonly (string process, string label)[] Known =
    [
        ("LEDKeeper2", "MSI Mystic Light (LEDKeeper2)"),
        ("MSI.CentralServer", "MSI Center"),
        ("iCUE", "Corsair iCUE"),
        ("Corsair.Service", "Corsair iCUE (service)"),
        ("LightingService", "ASUS Aura (LightingService)"),
        ("ArmouryCrate", "ASUS Armoury Crate"),
        ("SignalRgb", "SignalRGB"),
        ("SignalRgbLauncher", "SignalRGB"),
        ("NZXT CAM", "NZXT CAM"),
        ("L-Connect 3", "Lian Li L-Connect 3"),
        ("RGBFusion", "Gigabyte RGB Fusion"),
        ("lghub", "Logitech G HUB"),
        ("SteelSeriesGG", "SteelSeries GG"),
        ("RazerAppEngine", "Razer Synapse"),
    ];

    public static IReadOnlyList<ConflictingApp> Scan()
    {
        var found = new List<ConflictingApp>();
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { return found; }

        foreach (var p in procs)
        {
            using (p)
            {
                string name;
                try { name = p.ProcessName; } catch { continue; }
                foreach (var (process, label) in Known)
                {
                    if (string.Equals(name, process, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(new ConflictingApp(name, label, p.Id));
                        break;
                    }
                }
            }
        }
        return found;
    }

    public static bool TryKill(ConflictingApp app, out string? error)
    {
        error = null;
        try
        {
            using var p = Process.GetProcessById(app.Pid);
            p.Kill(entireProcessTree: true);
            return true;
        }
        catch (ArgumentException) { return true; } // déjà fermé
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
