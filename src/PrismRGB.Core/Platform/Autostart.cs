using Microsoft.Win32;

namespace PrismRGB.Core.Platform;

/// <summary>
/// Démarrage avec Windows via la clé "Run" de l'utilisateur (pas de droits administrateur nécessaires :
/// les contrôleurs USB HID sont accessibles en session normale).
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PrismRGB";

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    public static bool Enable(string exePath, out string error)
    {
        error = "";
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            k.SetValue(ValueName, $"\"{exePath}\" --minimized");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool Disable(out string error)
    {
        error = "";
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            k?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
