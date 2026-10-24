// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Threading;
using PrismRGB.Core.Config;
using PrismRGB.Core.Demo;
using PrismRGB.Core.Devices;
using PrismRGB.Core.Engine;
using PrismRGB.Core.Hid;
using PrismRGB.Core.Native;
using PrismRGB.Core.Platform;

namespace PrismRGB.App;

/// <summary>
/// Orchestration hors interface : configuration, pilotes natifs, moteur d'effets, sauvegarde différée.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly object _logLock = new();
    private readonly List<string> _log = [];
    private readonly bool _forceDemo;
    private readonly Lazy<SystemInfo> _sysInfo = new(SystemInfo.Query);
    private DispatcherTimer? _saveTimer;
    private IRgbBackend? _backend;

    public ConfigStore Store { get; } = new();
    public AppConfig Config { get; }
    public LightingEngine Engine { get; } = new();
    public IHidProvider Hid { get; } = HidProviders.Default();
    public IRgbBackend? Backend => _backend;
    public bool IsDemo => Config.DemoMode || _forceDemo;
    public SystemInfo SysInfo => _sysInfo.Value;

    public event EventHandler? BackendChanged;
    public event EventHandler? DevicesChanged;
    public event EventHandler? StatusChanged;

    public AppHost(bool forceDemo)
    {
        Config = Store.Load();
        _forceDemo = forceDemo; // --demo : pour cette session seulement, non enregistré
        Engine.TargetFps = Config.TargetFps;
        Engine.BackgroundFps = Config.BackgroundFps;
        Engine.MasterBrightness = Config.MasterBrightness / 100.0;
        Engine.LightsOn = Config.LightsOn;
        Engine.ResumeDetected += (_, _) =>
        {
            Log("Sortie de veille détectée : reconfiguration des contrôleurs.");
            _ = _backend?.ReapplyAsync();
        };
    }

    public void Log(string line)
    {
        lock (_logLock)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss} {line}");
            if (_log.Count > 400) _log.RemoveAt(0);
        }
    }

    public string LogText()
    {
        lock (_logLock) return string.Join(Environment.NewLine, _log);
    }

    public async Task StartAsync()
    {
        Engine.SetProfile(Config.GetActiveProfile());
        Engine.Start();
        await StartBackendAsync();
    }

    public async Task StartBackendAsync()
    {
        if (_backend is not null)
        {
            _backend.DevicesChanged -= OnDevicesChanged;
            _backend.StateChanged -= OnStateChanged;
            Engine.SetBackends([]);
            await _backend.DisposeAsync();
        }

        _backend = IsDemo
            ? new DemoBackend()
            : new NativeBackend(Hid, () => SysInfo, ZoneSizesSnapshot, () => Config.AllowUntestedBoards, Log,
                ramEnabled: () => Config.RamControl);
        _backend.DevicesChanged += OnDevicesChanged;
        _backend.StateChanged += OnStateChanged;
        Engine.SetBackends([_backend]);
        BackendChanged?.Invoke(this, EventArgs.Empty);
        await _backend.StartAsync();
    }

    private IReadOnlyDictionary<string, int> ZoneSizesSnapshot()
    {
        lock (Config.ZoneSizes) return new Dictionary<string, int>(Config.ZoneSizes);
    }

    public void SetZoneSize(string key, int size)
    {
        lock (Config.ZoneSizes) Config.ZoneSizes[key] = size;
        SaveSoon();
    }

    /// <summary>Inventaire complet du PC (lent : WMI + USB), à appeler hors du thread UI.</summary>
    public (IReadOnlyList<ScanEntry> entries, string report) ScanHardware()
    {
        var sys = SysInfo;
        IReadOnlyList<HidDeviceInfo> hid;
        try { hid = Hid.Enumerate(); } catch { hid = []; }
        var nb = _backend as NativeBackend;
        var results = nb?.DetectionResults ?? [];
        var ram = nb?.Ram;
        var entries = HardwareScanner.Scan(sys, hid, results, ram);
        var report = HardwareScanner.BuildReport(sys, hid, entries, LogText(), ram);
        return (entries, report);
    }

    public RamState Ram => (_backend as NativeBackend)?.Ram ?? RamState.Initial;

    public void RescanRam()
    {
        if (_backend is NativeBackend nb) nb.RescanRam();
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        Engine.Resend();
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStateChanged(object? sender, EventArgs e) => StatusChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>À appeler après toute modification du profil actif : met à jour le moteur et planifie la sauvegarde.</summary>
    public void ProfileChanged()
    {
        Engine.SetProfile(Config.GetActiveProfile());
        SaveSoon();
    }

    public void SaveSoon()
    {
        // Sur le thread UI : la config n'est modifiée que là, pas de sérialisation concurrente.
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            SaveNow();
        });
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        try { Store.Save(Config); }
        catch (Exception ex) { Log($"Sauvegarde impossible : {ex.Message}"); }
    }

    /// <summary>Partie de l'arrêt qui doit se faire sur le thread UI.</summary>
    public void PrepareShutdown()
    {
        _saveTimer?.Stop();
        SaveNow();
    }

    public async ValueTask DisposeAsync()
    {
        SaveNow();
        Engine.Dispose();
        if (_backend is not null) await _backend.DisposeAsync();
    }
}
