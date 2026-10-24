using PrismRGB.Core.Devices;
using PrismRGB.Core.Drivers.Msi;
using PrismRGB.Core.Hid;
using PrismRGB.Core.Platform;

namespace PrismRGB.Core.Native;

/// <summary>
/// Backend natif : PrismRGB parle directement aux contrôleurs USB, sans aucun logiciel tiers.
///
/// Fiabilité :
///  - un thread d'envoi par contrôleur, "la plus récente image gagne" (un contrôleur lent ne bloque rien) ;
///  - un contrôleur qui ne répond plus (veille, débranchement) est fermé puis redétecté automatiquement ;
///  - nouvelle détection périodique pour les appareils branchés à chaud ;
///  - "Réappliquer" renvoie la configuration (utile après une sortie de veille).
/// </summary>
public sealed class NativeBackend : IRgbBackend
{
    private readonly IHidProvider _hid;
    private readonly Func<IReadOnlyDictionary<string, int>> _zoneSizes;
    private readonly Func<bool> _allowUntested;
    private readonly Action<string>? _log;
    private readonly Func<SystemInfo> _sysProvider;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly List<Slot> _slots = [];
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _rescanRequested = true;
    private volatile IReadOnlyList<RgbDevice> _devices = [];
    private Task? _loop;

    public NativeBackend(IHidProvider hid, Func<SystemInfo> systemInfo, Func<IReadOnlyDictionary<string, int>> zoneSizes,
        Func<bool> allowUntested, Action<string>? log = null)
    {
        _hid = hid;
        _sysProvider = systemInfo;
        _zoneSizes = zoneSizes;
        _allowUntested = allowUntested;
        _log = log;
    }

    public string Name => "Pilotes natifs";
    public BackendState State { get; private set; } = BackendState.Stopped;
    public string StatusText { get; private set; } = "Arrêté";
    public IReadOnlyList<RgbDevice> Devices => _devices;

    /// <summary>Dernier inventaire (pour l'écran Matériel).</summary>
    public SystemInfo SysInfo { get; private set; } = SystemInfo.Empty;
    public IReadOnlyList<HidDeviceInfo> HidDevices { get; private set; } = [];
    public IReadOnlyList<(string name, string? problem)> DetectionResults { get; private set; } = [];

    public event EventHandler? DevicesChanged;
    public event EventHandler? StateChanged;

    public Task StartAsync(CancellationToken ct = default)
    {
        _loop ??= Task.Run(() => MainLoop(_cts.Token));
        return Task.CompletedTask;
    }

    private void SetState(BackendState s, string text)
    {
        State = s;
        StatusText = text;
        _log?.Invoke($"[Natif] {text}");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MainLoop(CancellationToken ct)
    {
        SetState(BackendState.Detecting, "Recherche du matériel RGB...");
        try { SysInfo = _sysProvider(); } catch { SysInfo = SystemInfo.Empty; }

        var lastCandidates = new HashSet<string>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var all = _hid.Enumerate();
                var candidates = all.Where(MsiBoards.IsCandidate).ToList();
                var paths = candidates.Select(c => c.Path).ToHashSet();
                bool changed = !paths.SetEquals(lastCandidates);
                List<Slot> lostSlots;
                lock (_lock)
                {
                    lostSlots = _slots.Where(s => s.Lost).ToList();
                    _slots.RemoveAll(s => s.Lost);
                }
                foreach (var s in lostSlots) s.Dispose();
                bool lost = lostSlots.Count > 0;

                if (_rescanRequested || changed || lost)
                {
                    _rescanRequested = false;
                    HidDevices = all;
                    Detect(candidates);
                    lastCandidates = paths;
                }
            }
            catch (Exception ex)
            {
                SetState(BackendState.Error, $"Erreur de détection : {ex.Message}");
            }
            _wake.WaitOne(TimeSpan.FromSeconds(4));
        }
    }

    private void Detect(List<HidDeviceInfo> candidates)
    {
        var results = new List<(string, string?)>();
        // Une seule collection par appareil physique.
        foreach (var info in candidates.GroupBy(c => (c.VendorId, c.ProductId, c.Serial)).Select(g => g.First()))
        {
            bool active;
            lock (_lock) active = _slots.Any(s => s.Path == info.Path && !s.Lost);
            if (active)
            {
                results.Add((SysInfo.MsiBoardName, null));
                continue;
            }

            var r = MsiDetector.Detect(_hid, info, SysInfo, _allowUntested());
            results.Add((r.Summary, r.Problem));
            if (r.Controller is null)
            {
                _log?.Invoke($"[Natif] {r.Summary} : {r.Problem}");
                continue;
            }
            try
            {
                r.Controller.Configure(_zoneSizes());
                var slot = new Slot(r.Controller, info.Path, _log);
                lock (_lock) _slots.Add(slot);
                _log?.Invoke($"[Natif] Piloté : {r.Summary}");
            }
            catch (Exception ex)
            {
                r.Controller.Dispose();
                results[^1] = (r.Summary, $"Échec de configuration : {ex.Message}");
            }
        }
        DetectionResults = results;
        PublishDevices();
    }

    private void PublishDevices()
    {
        List<RgbDevice> list;
        lock (_lock) list = _slots.Where(s => !s.Lost).Select(s => s.Controller.Device).ToList();
        _devices = list;
        int leds = list.Sum(d => d.LedCount);
        if (list.Count > 0)
            SetState(BackendState.Connected, $"{list.Count} contrôleur(s) piloté(s), {leds} LEDs");
        else
            SetState(BackendState.Connected, DetectionResults.FirstOrDefault(r => r.problem is not null).problem
                ?? "Aucun contrôleur RGB compatible détecté pour l'instant.");
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SubmitFrame(RgbDevice device, ReadOnlySpan<Rgb> colors)
    {
        Slot? slot;
        lock (_lock) slot = _slots.FirstOrDefault(s => s.Controller.Device.Key == device.Key && !s.Lost);
        slot?.Submit(colors);
    }

    public Task RescanAsync()
    {
        lock (_lock)
        {
            foreach (var s in _slots) s.Dispose();
            _slots.Clear();
        }
        _rescanRequested = true;
        _wake.Set();
        return Task.CompletedTask;
    }

    public Task ReapplyAsync() => Task.Run(() =>
    {
        List<Slot> slots;
        lock (_lock) slots = [.. _slots];
        foreach (var s in slots) s.Reconfigure(_zoneSizes());
        PublishDevices();
    });

    public Task ResizeZoneAsync(RgbDevice device, int zoneIndex, int ledCount) => Task.Run(() =>
    {
        Slot? slot;
        lock (_lock) slot = _slots.FirstOrDefault(s => s.Controller.Device.Key == device.Key);
        slot?.Reconfigure(_zoneSizes());
        PublishDevices();
    });

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _wake.Set();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        lock (_lock)
        {
            foreach (var s in _slots) s.Dispose();
            _slots.Clear();
        }
        State = BackendState.Stopped;
    }

    /// <summary>Un contrôleur et son thread d'envoi.</summary>
    private sealed class Slot : IDisposable
    {
        private readonly object _io = new();
        private readonly AutoResetEvent _signal = new(false);
        private readonly Thread _thread;
        private readonly Action<string>? _log;
        private Rgb[] _frame = [];
        private bool _hasFrame;
        private volatile bool _stop;
        private int _failures;

        public Slot(INativeController controller, string path, Action<string>? log)
        {
            Controller = controller;
            Path = path;
            _log = log;
            _thread = new Thread(Run) { IsBackground = true, Name = $"PrismRGB {controller.Device.Name}" };
            _thread.Start();
        }

        public INativeController Controller { get; }
        public string Path { get; }
        public volatile bool Lost;

        public void Submit(ReadOnlySpan<Rgb> colors)
        {
            lock (_io)
            {
                if (_frame.Length != colors.Length) _frame = new Rgb[colors.Length];
                colors.CopyTo(_frame);
                _hasFrame = true;
            }
            _signal.Set();
        }

        public void Reconfigure(IReadOnlyDictionary<string, int> sizes)
        {
            lock (_io)
            {
                try { Controller.Configure(sizes); }
                catch (Exception ex) { _log?.Invoke($"[Natif] Reconfiguration échouée : {ex.Message}"); }
                _hasFrame = false;
            }
        }

        private void Run()
        {
            var local = Array.Empty<Rgb>();
            while (!_stop)
            {
                _signal.WaitOne(TimeSpan.FromSeconds(2));
                if (_stop) break;
                lock (_io)
                {
                    if (!_hasFrame) continue;
                    if (local.Length != _frame.Length) local = new Rgb[_frame.Length];
                    _frame.CopyTo(local, 0);
                    _hasFrame = false;
                    try
                    {
                        if (local.Length == Controller.Device.LedCount) Controller.Write(local);
                        _failures = 0;
                    }
                    catch (Exception ex)
                    {
                        if (++_failures >= 3 || ex is IOException)
                        {
                            _log?.Invoke($"[Natif] {Controller.Device.Name} ne répond plus ({ex.Message}), nouvelle détection.");
                            Lost = true;
                            return;
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            _stop = true;
            Lost = true;
            _signal.Set();
            if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(1));
            lock (_io) Controller.Dispose();
        }
    }
}
