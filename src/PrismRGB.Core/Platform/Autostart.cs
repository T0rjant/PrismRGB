using System.Diagnostics;

namespace PrismRGB.Core.Platform;

/// <summary>
/// Démarrage avec Windows via le Planificateur de tâches, avec les droits administrateur
/// (nécessaires à OpenRGB pour la RAM et certains contrôleurs SMBus) et sans invite UAC.
/// </summary>
public static class Autostart
{
    private const string TaskName = "PrismRGB";

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool IsEnabled()
    {
        if (!IsSupported) return false;
        return RunSchtasks(["/Query", "/TN", TaskName], out _) == 0;
    }

    public static bool Enable(string exePath, out string output)
    {
        output = "";
        if (!IsSupported) return false;
        // /TR attend une seule chaîne : chemin entre guillemets + arguments.
        string tr = $"\"{exePath}\" --minimized";
        return RunSchtasks(["/Create", "/F", "/TN", TaskName, "/SC", "ONLOGON", "/RL", "HIGHEST", "/TR", tr], out output) == 0;
    }

    public static bool Disable(out string output)
    {
        output = "";
        if (!IsSupported) return false;
        return RunSchtasks(["/Delete", "/F", "/TN", TaskName], out output) == 0;
    }

    private static int RunSchtasks(string[] args, out string output)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }
}
