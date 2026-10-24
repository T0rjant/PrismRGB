using System.Diagnostics;

namespace PrismRGB.Core.OpenRgb;

/// <summary>Recherche d'OpenRGB.exe aux emplacements habituels.</summary>
public static class OpenRgbLocator
{
    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        foreach (var c in Candidates())
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    public static IEnumerable<string> Candidates()
    {
        string appDir = AppContext.BaseDirectory;
        yield return Path.Combine(appDir, "OpenRGB", "OpenRGB.exe");
        yield return Path.Combine(appDir, "OpenRGB Windows 64-bit", "OpenRGB.exe");
        yield return Path.Combine(appDir, "OpenRGB.exe");

        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } l ? Path.Combine(l, "Programs") : "",
        })
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return Path.Combine(root, "OpenRGB", "OpenRGB.exe");
        }

        // Archive dézippée sur le bureau ou dans Téléchargements (cas fréquent).
        foreach (var dir in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        })
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            string[] found;
            try
            {
                found = Directory.GetFiles(dir, "OpenRGB.exe", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 3,
                    IgnoreInaccessible = true,
                });
            }
            catch { continue; }
            foreach (var f in found) yield return f;
        }
    }

    /// <summary>Un OpenRGB qui n'a pas été lancé par nous tourne déjà (souvent l'interface, qui monopolise le matériel).</summary>
    public static bool IsOtherInstanceRunning(string exePath)
    {
        try
        {
            return Process.GetProcessesByName("OpenRGB").Any(p =>
            {
                using (p) return p.Id != Environment.ProcessId;
            });
        }
        catch { return false; }
    }
}
