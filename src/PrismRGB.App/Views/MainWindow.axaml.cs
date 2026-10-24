// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using Avalonia;
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == IsVisibleProperty) UpdateBackground();
    }

    private bool _inBackground;

    /// <summary>
    /// Fenêtre cachée ou réduite (en jeu) : plus d'aperçu ni de rendu d'interface, cadence réduite,
    /// priorité basse et mémoire rendue au système. Les effets continuent sur le matériel.
    /// </summary>
    private void UpdateBackground()
    {
        if (DataContext is not MainViewModel vm) return;
        bool bg = !IsVisible || WindowState == WindowState.Minimized;
        if (bg == _inBackground) return;
        _inBackground = bg;
        vm.InBackground = bg;
        vm.Host.Engine.Background = bg;
        if (bg) _frameTimer.Stop(); else _frameTimer.Start();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var me = Process.GetCurrentProcess();
                me.PriorityClass = bg && vm.LowPriorityInBackground ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
            }
            catch { }
        }
        if (bg)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            if (OperatingSystem.IsWindows())
            {
                try { SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1); } catch { }
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

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
