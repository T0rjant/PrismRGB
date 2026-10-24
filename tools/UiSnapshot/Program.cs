using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using PrismRGB.App;
using PrismRGB.App.Controls;
using PrismRGB.App.ViewModels;
using PrismRGB.App.Views;

var outDir = args.Length > 0 ? args[0] : "snapshots";
Directory.CreateDirectory(outDir);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var host = new AppHost(forceDemo: true);
var vm = new MainViewModel(host);
await host.StartAsync();
var win = new MainWindow { DataContext = vm, Width = 1240, Height = 1500 };
win.Show();

void Pump(int ms)
{
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end)
    {
        Dispatcher.UIThread.RunJobs();
        win.FindControl<LedPreview>("Preview")!.Frames = host.Engine.GetPreview();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(15);
    }
}

void Snap(string name)
{
    Pump(400);
    win.CaptureRenderedFrame()!.Save(Path.Combine(outDir, name + ".png"));
    Console.WriteLine("saved " + name);
}

vm.SelectedProfile = vm.Profiles.First(p => p.Name == "Météorite bleue");
Pump(1500);
Snap("1-effets-meteorite");
vm.SelectedProfile = vm.Profiles.First(p => p.Name == "Arc-en-ciel");
Snap("2-effets-arcenciel");
vm.SelectedProfile = vm.Profiles.First(p => p.Name == "Statique violet/cyan");
Snap("3-effets-multistatique");
vm.SelectedTab = 1;
Snap("4-appareils");
vm.SelectedTab = 2;
Snap("5-parametres");
host.PrepareShutdown();
await host.DisposeAsync();
