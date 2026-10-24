using Avalonia;

namespace PrismRGB.App;

internal static class Program
{
    public static bool StartMinimized { get; private set; }
    public static bool ForceDemo { get; private set; }

    private const string MutexName = "Local\\PrismRGB_SingleInstance";
    private const string ShowEventName = "Local\\PrismRGB_Show";

    [STAThread]
    public static int Main(string[] args)
    {
        StartMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        ForceDemo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);

        // Une seule instance : deux PrismRGB enverraient des couleurs concurrentes au matériel.
        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                    ev.Set();
                }
                catch { }
            }
            return 0;
        }

        if (OperatingSystem.IsWindows())
        {
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var t = new Thread(() =>
            {
                while (showEvent.WaitOne())
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => (Application.Current as App)?.ShowMainWindow());
            }) { IsBackground = true, Name = "PrismRGB show listener" };
            t.Start();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
