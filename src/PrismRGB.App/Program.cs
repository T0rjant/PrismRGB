// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using PrismRGB.Core.Config;

namespace PrismRGB.App;

internal static class Program
{
    public static bool StartMinimized { get; private set; }
    public static bool ForceDemo { get; private set; }
    /// <summary>L'utilisateur a refusé l'élévation : on tourne sans contrôle de la RAM.</summary>
    public static bool ElevationDeclined { get; private set; }

    private const string MutexName = "Local\\PrismRGB_SingleInstance";
    private const string ShowEventName = "Local\\PrismRGB_Show";

    [STAThread]
    public static int Main(string[] args)
    {
        StartMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        ForceDemo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        bool relaunched = args.Contains("--elevated", StringComparer.OrdinalIgnoreCase);

        // Contrôle de la RAM activé : il faut les droits administrateur (pilote PawnIO). On se relance une fois.
        if (OperatingSystem.IsWindows() && !relaunched && !ForceDemo && !SystemInfoHelpers.IsElevated())
        {
            bool ram;
            try { ram = new ConfigStore().Load().RamControl; } catch { ram = false; }
            // Seulement si PawnIO est installé : sinon la fenêtre UAC ne servirait à rien.
            if (ram && Core.Smbus.PawnIo.Probe() == Core.Smbus.PawnIoStatus.NeedsAdmin)
            {
                if (TryRelaunchElevated(args)) return 0;
                ElevationDeclined = true;
            }
        }

        // Une seule instance : deux PrismRGB enverraient des couleurs concurrentes au matériel.
        Mutex mutex;
        bool owned;
        try
        {
            mutex = new Mutex(false, MutexName);
            // Après une relance en administrateur, l'ancienne instance met quelques instants à se fermer.
            try { owned = mutex.WaitOne(relaunched ? TimeSpan.FromSeconds(10) : TimeSpan.Zero); }
            catch (AbandonedMutexException) { owned = true; }
        }
        catch (UnauthorizedAccessException)
        {
            return 0; // une instance administrateur tourne déjà
        }

        if (!owned)
        {
            SignalExistingInstance();
            mutex.Dispose();
            return 0;
        }

        try
        {
            if (OperatingSystem.IsWindows()) StartShowListener();
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    /// <summary>Relance PrismRGB en administrateur (fenêtre UAC). Retourne false si l'utilisateur refuse.</summary>
    public static bool TryRelaunchElevated(IEnumerable<string> args)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return false;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            foreach (var a in args.Where(a => !a.Equals("--elevated", StringComparison.OrdinalIgnoreCase))) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--elevated");
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception)
        {
            return false; // 1223 : refusé par l'utilisateur
        }
    }

    private static void SignalExistingInstance()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ShowEventName);
            ev.Set();
        }
        catch { }
    }

    private static void StartShowListener()
    {
        EventWaitHandle showEvent;
        try { showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName); }
        catch { return; }
        var t = new Thread(() =>
        {
            while (showEvent.WaitOne())
                Avalonia.Threading.Dispatcher.UIThread.Post(() => (Application.Current as App)?.ShowMainWindow());
        }) { IsBackground = true, Name = "PrismRGB show listener" };
        t.Start();
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

internal static class SystemInfoHelpers
{
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
}
