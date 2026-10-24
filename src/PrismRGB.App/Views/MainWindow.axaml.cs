using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PrismRGB.App.Controls;
using PrismRGB.App.ViewModels;

namespace PrismRGB.App.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _frameTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;

    public MainWindow()
    {
        InitializeComponent();

        // Aperçus et miniatures : ~30 img/s, uniquement quand la fenêtre est visible.
        _frameTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => OnFrame());
        _frameTimer.Start();

        TitleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is Border or Grid or StackPanel or TextBlock or Image)
                BeginMoveDrag(e);
        };

        // Changer d'effet ne doit pas faire défiler la page (la vitrine reste visible).
        Tiles.AddHandler(RequestBringIntoViewEvent, (_, e) => e.Handled = true);

        RenameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is MainViewModel vm) vm.CommitRenameCommand.Execute(null);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm)
        {
            Rig.LookFor = vm.LookFor;
            RigDevices.LookFor = vm.LookFor;
            vm.LooksChanged += (_, _) =>
            {
                Rig.Refresh();
                RigDevices.Refresh();
            };
        }
    }

    /// <summary>Une image d'aperçu (public pour l'outil de captures).</summary>
    public void OnFrame()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || DataContext is not MainViewModel vm) return;
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _last, 0, 0.1);
        _last = now;

        var frames = vm.Host.Engine.GetPreview();
        if (vm.IsLightingPage)
        {
            Rig.Frames = frames;
            EffectThumb.TickAll(dt);
        }
        else if (vm.IsDevicesPage)
        {
            RigDevices.Frames = frames;
        }
    }
}
