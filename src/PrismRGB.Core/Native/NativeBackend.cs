// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using PrismRGB.Core.Devices;
using PrismRGB.Core.Drivers.Dram;
using PrismRGB.Core.Hid;
using PrismRGB.Core.Platform;
using PrismRGB.Core.Smbus;

namespace PrismRGB.Core.Native;

public enum RamAccess { Disabled, Unsupported, PawnIoMissing, NeedsAdmin, BusError, Scanning, NoRgb, Ok }

/// <summary>État du contrôle RGB de la RAM, pour l'interface et le rapport.</summary>
public sealed record RamState(RamAccess Access, string Message, IReadOnlyList<DramSlot> Slots, IReadOnlyList<string> Controlled)
{
    public static RamState Initial { get; } = new(RamAccess.Disabled, "Contrôle de la RAM désactivé.", [], []);
}

/// <summary>
/// Backend natif : PrismRGB parle directement aux contrôleurs (USB HID, et bus SMBus pour la RAM via PawnIO),
/// sans aucun logiciel tiers.
///
/// Fiabilité :
///  - un thread d'envoi par contrôleur, "la plus récente image gagne" (un contrôleur lent ne bloque rien) ;
///  - un contrôleur qui ne répond plus (veille, débranchement) est fermé puis redétecté automatiquement ;
///  - nouvelle détection USB périodique pour les appareils branchés à chaud ;
///  - "Réappliquer" renvoie la configuration (utile après une sortie de veille).
/// </summary>
public sealed class NativeBackend : IRgbBackend
{
    private readonly IHidProvider _hid;
    private readonly Func<IReadOnlyDictionary<string, int>> _zoneSizes;
    private readonly Func<bool> _allowUntested;
    private readonly Func<bool> _ramEnabled;
    private readonly Action<string>? _log;
    private readonly Func<SystemInfo> _sysProvider;
    private readonly Func<ISmbus?>? _smbusFactory;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly List<Slot> _slots = [];
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _rescanRequested = true;
    private volatile bool _ramScanRequested = true;
    private volatile IReadOnlyList<RgbDevice> _devices = [];
    private ISmbus? _smbus;
    private Task? _loop;

    public NativeBackend(IHidProvider hid, Func<SystemInfo> systemInfo, Func<IReadOnlyDictionary<string, int>> zoneSizes,
        Func<bool> allowUntested, Action<string>? log = null, Func<bool>? ramEnabled = null, Func<ISmbus?>? smbusFactory = null)
    {
        _hid = hid;
        _sysProvider = systemInfo;
        _zoneSizes = zoneSizes;
        _allowUntested = allowUntested;
        _log = log;
        _ramEnabled = ramEnabled ?? (() => false);
        _smbusFactory = smbusFactory;
    }

    public string Name => "Pilotes natifs";
    public BackendState State { get; private set; } = BackendState.Stopped;
    public string StatusText { get; private set; } = "Arrêté";
    public IReadOnlyList<RgbDevice> Devices => _devices;

    /// <summary>Dernier inventaire (pour l'écran Matériel).</summary>
    public SystemInfo SysInfo { get; private set; } = SystemInfo.Empty;
    public IReadOnlyList<HidDeviceInfo> HidDevices { get; private set; } = [];
    public IReadOnlyList<(string name, string? problem)> DetectionResults { get; private set; } = [];
    public RamState Ram { get; private set; } = RamState.Initial;

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
                var candidates = DriverRegistry.Candidates(all).ToList();
                var paths = candidates.Select(c => c.info.Path).ToHashSet();
                bool changed = !paths.SetEquals(lastCandidates);

                List<Slot> lostSlots;
                lock (_lock)
                {
                    lostSlots = _slots.Where(s => s.Lost).ToList();
                    _slots.RemoveAll(s => s.Lost);
                }
                foreach (var s in lostSlots)
                {
                    if (s.IsRam) _ramScanRequested = true;
                    s.Dispose();
                }

                bool any = false;
                if (_rescanRequested || changed || lostSlots.Count > 0)
                {
                    _rescanRequested = false;
                    HidDevices = all;
                    DetectHid(candidates);
                    lastCandidates = paths;
                    any = true;
                }
                if (_ramScanRequested)
                {
                    _ramScanRequested = false;
                    DetectRam();
                    any = true;
                }
                if (any) PublishDevices();
            }
            catch (Exception ex)
            {
                SetState(BackendState.Error, $"Erreur de détection : {ex.Message}");
            }
            _wake.WaitOne(TimeSpan.FromSeconds(4));
        }
    }

    private void DetectHid(List<(HidDriver driver, HidDeviceInfo info)> candidates)
    {
        var results = new List<(string, string?)>();
        foreach (var (driver, info) in candidates)
        {
            bool active;
            lock (_lock) active = _slots.Any(s => s.Path == info.Path && !s.Lost);
            if (active)
            {
                results.Add((driver.Name, null));
                continue;
            }

            var r = driver.Detect(_hid, info, SysInfo, _allowUntested());
            results.Add((r.Summary, r.Problem));
            if (r.Controller is null)
            {
                _log?.Invoke($"[Natif] {r.Summary} : {r.Problem}");
                continue;
            }
            if (TryAdd(r.Controller, info.Path, isRam: false, out var error))
                _log?.Invoke($"[Natif] Piloté : {r.Summary}");
            else
                results[^1] = (r.Summary, error);
        }
        DetectionResults = results;
    }

    private bool TryAdd(INativeController controller, string path, bool isRam, out string? error)
    {
        error = null;
        try
        {
            controller.Configure(_zoneSizes());
            var slot = new Slot(controller, path, isRam, _log);
            lock (_lock) _slots.Add(slot);
            return true;
        }
        catch (Exception ex)
        {
            controller.Dispose();
            error = $"Échec de configuration : {ex.Message}";
            return false;
        }
    }

    private void DetectRam()
    {
        // Retire les barrettes déjà pilotées : une nouvelle détection complète repart de zéro.
        List<Slot> old;
        lock (_lock)
        {
            old = _slots.Where(s => s.IsRam).ToList();
            _slots.RemoveAll(s => s.IsRam);
        }
        foreach (var s in old) s.Dispose();

        if (!_ramEnabled())
        {
            Ram = RamState.Initial;
            return;
        }

        ISmbus? bus = _smbus;
        if (bus is null && _smbusFactory is not null)
        {
            bus = _smbusFactory();
            if (bus is null)
            {
                Ram = new(RamAccess.BusError, "Bus SMBus inaccessible.", [], []);
                return;
            }
        }
        else if (bus is null)
        {
            if (!OperatingSystem.IsWindows())
            {
                Ram = new(RamAccess.Unsupported, "Le contrôle de la RAM n'est disponible que sous Windows.", [], []);
                return;
            }
            switch (PawnIo.Probe())
            {
                case PawnIoStatus.NotInstalled:
                    Ram = new(RamAccess.PawnIoMissing, "Le pilote PawnIO (gratuit, signé, open source) n'est pas installé.", [], []);
                    return;
                case PawnIoStatus.NeedsAdmin:
                    Ram = new(RamAccess.NeedsAdmin, "PrismRGB doit être lancé en administrateur pour accéder à la RAM.", [], []);
                    return;
            }
            var opened = PawnIoSmbus.Open(out var err);
            if (opened is null)
            {
                Ram = new(RamAccess.BusError, $"Bus SMBus de la carte mère inaccessible ({err}).", [], []);
                return;
            }
            bus = new SafeSmbus(opened);
        }
        _smbus = bus;

        Ram = new(RamAccess.Scanning, "Analyse des barrettes...", [], []);
        try
        {
            var slots = SpdReader.ReadAll(bus);
            var controllers = DramDetector.Detect(bus, slots, SysInfo.Ram.Select(r => r.Manufacturer));
            var controlled = new List<string>();
            foreach (var c in controllers)
                if (TryAdd(c, $"smbus:{c.Device.Key}", isRam: true, out _)) controlled.Add(c.Device.Name);
            Ram = controlled.Count > 0
                ? new(RamAccess.Ok, $"{controlled.Count} contrôleur(s) RGB de RAM piloté(s) via {bus.Name}.", slots, controlled)
                : new(RamAccess.NoRgb, slots.Count == 0
                    ? "Aucune barrette lisible sur le bus SMBus."
                    : $"{slots.Count} barrette(s) trouvée(s) ({string.Join(", ", slots.Select(s => s.Vendor).Distinct())}), sans contrôleur RGB reconnu.", slots, controlled);
            _log?.Invoke($"[RAM] {Ram.Message}");
        }
        catch (Exception ex)
        {
            Ram = new(RamAccess.BusError, $"Erreur pendant l'analyse de la RAM : {ex.Message}", [], []);
        }
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
        // Boucle simple (sans LINQ) : appelé à chaque image, ne doit rien allouer.
        Slot? slot = null;
        lock (_lock)
        {
            foreach (var s in _slots)
                if (!s.Lost && ReferenceEquals(s.Controller.Device, device)) { slot = s; break; }
            if (slot is null)
                foreach (var s in _slots)
                    if (!s.Lost && s.Controller.Device.Key == device.Key) { slot = s; break; }
        }
        slot?.Submit(colors);
    }

    public Task RescanAsync()
    {
        List<Slot> all;
        lock (_lock)
        {
            all = [.. _slots];
            _slots.Clear();
        }
        foreach (var s in all) s.Dispose();
        _rescanRequested = true;
        _ramScanRequested = true;
        _wake.Set();
        return Task.CompletedTask;
    }

    /// <summary>Relance uniquement la détection de la RAM (option activée/désactivée).</summary>
    public void RescanRam()
    {
        _ramScanRequested = true;
        _wake.Set();
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
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        }
        List<Slot> all;
        lock (_lock)
        {
            all = [.. _slots];
            _slots.Clear();
        }
        foreach (var s in all) s.Dispose();
        _smbus?.Dispose();
        State = BackendState.Stopped;
    }

    /// <summary>Un contrôleur et son thread d'envoi.</summary>
    private sealed class Slot : IDisposable
    {
        private readonly object _io = new();
        private readonly object _hw = new();   // sérialise les accès au contrôleur (écriture / reconfiguration)
        private readonly AutoResetEvent _signal = new(false);
        private readonly Thread _thread;
        private readonly Action<string>? _log;
        private Rgb[] _frame = [];
        private bool _hasFrame;
        private volatile bool _stop;
        private int _failures;

        public Slot(INativeController controller, string path, bool isRam, Action<string>? log)
        {
            Controller = controller;
            Path = path;
            IsRam = isRam;
            _log = log;
            // Priorité basse : en jeu, l'éclairage passe après tout le reste.
            _thread = new Thread(Run) { IsBackground = true, Name = $"PrismRGB {controller.Device.Name}", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public INativeController Controller { get; }
        public string Path { get; }
        public bool IsRam { get; }
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
            lock (_hw)
            {
                try { Controller.Configure(sizes); }
                catch (Exception ex) { _log?.Invoke($"[Natif] Reconfiguration échouée : {ex.Message}"); }
            }
            lock (_io) _hasFrame = false;
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
                }
                try
                {
                    // Hors du verrou : un contrôleur lent (RAM) ne bloque pas le dépôt des images suivantes.
                    lock (_hw)
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

        public void Dispose()
        {
            _stop = true;
            Lost = true;
            _signal.Set();
            if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
            lock (_hw) Controller.Dispose();
        }
    }
}
