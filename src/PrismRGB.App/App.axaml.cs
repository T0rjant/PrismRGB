// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using PrismRGB.App.ViewModels;
using PrismRGB.App.Views;

namespace PrismRGB.App;

public partial class App : Application
{
    private AppHost? _host;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = new AppHost(Program.ForceDemo);
            _vm = new MainViewModel(_host);
            _vm.RestartElevatedRequested += (_, _) =>
            {
                if (Program.TryRelaunchElevated(Environment.GetCommandLineArgs().Skip(1))) Quit();
                else _vm.Message = "Redémarrage en administrateur annulé.";
            };
            if (Program.ElevationDeclined)
                _vm.Message = "Droits administrateur refusés : le RGB de la RAM ne sera pas piloté pendant cette session.";
            _vm.Start();
            _ = _host.StartAsync();

            SetupTray();

            bool minimized = Program.StartMinimized || _host.Config.StartMinimized;
            if (!minimized || !_host.Config.MinimizeToTray)
            {
                ShowMainWindow();
                if (minimized && _window is not null) _window.WindowState = WindowState.Minimized;
            }

            desktop.ShutdownRequested += (_, _) => Shutdown();
        }
        base.OnFrameworkInitializationCompleted();
    }

    public void ShowMainWindow()
    {
        if (_vm is null) return;
        if (_window is null)
        {
            _window = new MainWindow { DataContext = _vm };
            _window.Closing += (_, e) =>
            {
                if (_quitting) return;
                if (_host!.Config.MinimizeToTray)
                {
                    // Fermer la fenêtre = rester dans la zone de notification, les effets continuent.
                    e.Cancel = true;
                    _window!.Hide();
                }
                else
                {
                    Quit();
                }
            };
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void SetupTray()
    {
        _tray = new TrayIcon
        {
            ToolTipText = $"PrismRGB by {Core.AppInfo.Author}",
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PrismRGB/Assets/icon.ico"))),
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowMainWindow();
        RebuildTrayMenu();
        _vm!.Profiles.CollectionChanged += (_, _) => RebuildTrayMenu();
        TrayIcon.SetIcons(this, [_tray]);
    }

    private void RebuildTrayMenu()
    {
        if (_tray is null || _vm is null) return;
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Ouvrir PrismRGB");
        open.Click += (_, _) => ShowMainWindow();
        menu.Add(open);
        menu.Add(new NativeMenuItemSeparator());

        var profiles = new NativeMenuItem("Profils") { Menu = new NativeMenu() };
        foreach (var p in _vm.Profiles)
        {
            var item = new NativeMenuItem(p.Name);
            string name = p.Name;
            item.Click += (_, _) => _vm.SelectProfileByName(name);
            profiles.Menu.Add(item);
        }
        menu.Add(profiles);

        var reapply = new NativeMenuItem("Réappliquer");
        reapply.Click += (_, _) => _vm.ReapplyCommand.Execute(null);
        menu.Add(reapply);
        menu.Add(new NativeMenuItemSeparator());

        var quit = new NativeMenuItem("Quitter");
        quit.Click += (_, _) => Quit();
        menu.Add(quit);
        _tray.Menu = menu;
    }

    private void Quit()
    {
        _quitting = true;
        Shutdown();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
    }

    private void Shutdown()
    {
        if (_host is null) return;
        var host = _host;
        _host = null;
        try { _tray?.Dispose(); } catch { }
        host.PrepareShutdown();
        // Arrêt propre : sauvegarde, fin du moteur, arrêt du serveur OpenRGB lancé par nous.
        Task.Run(async () => await host.DisposeAsync()).Wait(TimeSpan.FromSeconds(4));
    }
}
