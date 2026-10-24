// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Microsoft.Win32;

namespace PrismRGB.Core.Platform;

/// <summary>
/// Démarrage avec Windows.
///  - Sans contrôle de la RAM : clé "Run" de l'utilisateur, aucun droit administrateur.
///  - Avec contrôle de la RAM : tâche planifiée "au démarrage de session, privilèges les plus élevés",
///    pour démarrer en administrateur sans fenêtre de confirmation (UAC) à chaque fois.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "PrismRGB";

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return RunKeyExists() || Schtasks(["/Query", "/TN", Name], out _) == 0;
    }

    private static bool RunKeyExists()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string;
        }
        catch { return false; }
    }

    /// <param name="elevated">true : tâche planifiée en administrateur (nécessite d'être déjà administrateur).</param>
    public static bool Enable(string exePath, bool elevated, out string error)
    {
        error = "";
        if (!OperatingSystem.IsWindows()) return false;
        Disable(out _);
        try
        {
            if (elevated)
            {
                int code = Schtasks(["/Create", "/F", "/TN", Name, "/SC", "ONLOGON", "/RL", "HIGHEST", "/TR", $"\"{exePath}\" --minimized"], out var output);
                if (code != 0) error = output.Trim();
                return code == 0;
            }
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            k.SetValue(Name, $"\"{exePath}\" --minimized");
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
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                k?.DeleteValue(Name, throwOnMissingValue: false);
            if (Schtasks(["/Query", "/TN", Name], out _) == 0) Schtasks(["/Delete", "/F", "/TN", Name], out _);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static int Schtasks(string[] args, out string output)
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
