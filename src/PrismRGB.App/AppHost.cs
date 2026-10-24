using Avalonia.Threading;
using PrismRGB.Core.Config;
using PrismRGB.Core.Demo;
using PrismRGB.Core.Devices;
using PrismRGB.Core.Engine;
using PrismRGB.Core.OpenRgb;

namespace PrismRGB.App;

/// <summary>
/// Orchestration hors interface : configuration, backends, moteur d'effets, sauvegarde différée.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly object _logLock = new();
    private readonly List<string> _log = [];
    private DispatcherTimer? _saveTimer;
    private IRgbBackend? _backend;
    private readonly bool _forceDemo;

    public ConfigStore Store { get; } = new();
    public AppConfig Config { get; }
    public LightingEngine Engine { get; } = new();
    public IRgbBackend? Backend => _backend;

    public event EventHandler? BackendChanged;
    public event EventHandler? DevicesChanged;
    public event EventHandler? StatusChanged;

    public AppHost(bool forceDemo)
    {
        Config = Store.Load();
        _forceDemo = forceDemo; // --demo : pour cette session seulement, non enregistré
        Engine.TargetFps = Config.TargetFps;
    }

    public void Log(string line)
    {
        lock (_logLock)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss} {line}");
            if (_log.Count > 300) _log.RemoveAt(0);
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

        _backend = Config.DemoMode || _forceDemo
            ? new DemoBackend()
            : new OpenRgbBackend(Config.OpenRgb, ZoneSizesSnapshot, Log);
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
