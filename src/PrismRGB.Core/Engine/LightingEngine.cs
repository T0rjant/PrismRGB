// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using PrismRGB.Core.Config;
using PrismRGB.Core.Devices;
using PrismRGB.Core.Effects;

namespace PrismRGB.Core.Engine;

public sealed record DeviceFrame(IRgbBackend Backend, RgbDevice Device, Rgb[] Colors);

/// <summary>
/// Boucle d'animation : calcule chaque image à partir du profil actif et l'envoie aux backends.
///
/// Conçue pour ne rien coûter en jeu :
///  - aucune allocation mémoire en régime établi (plan de rendu et tampons réutilisés) ;
///  - effets statiques : une image est envoyée puis le thread dort (réveil sur changement ou maintien périodique) ;
///  - l'aperçu pour l'interface n'est calculé que si une fenêtre l'affiche ;
///  - thread en priorité basse et cadence réglable quand la fenêtre est réduite.
/// Le profil est remplacé en bloc (copie) pour éviter tout partage d'état avec l'interface.
/// </summary>
public sealed class LightingEngine : IDisposable
{
    private sealed class Part
    {
        public required RgbDevice Device;
        public required RgbZone Zone;
        public required bool Reverse;
        public required Rgb[] Buffer;
    }

    private sealed class Strip
    {
        public required string Key;
        public required EffectSettings Settings;
        public required Part[] Parts;
        public required Rgb[] Pixels;
        public Effect? Effect;
        public string? EffectId;
    }

    private sealed class Target
    {
        public required IRgbBackend Backend;
        public required RgbDevice Device;
        public required Rgb[] Buffer;
        public required Rgb[] LastSent;
        public long LastSentAt;
        public bool EverSent;
    }

    private readonly object _lock = new();
    private readonly Random _rng = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly AutoResetEvent _wake = new(false);
    private readonly Dictionary<string, (string id, Effect effect)> _effectCache = [];
    private volatile Profile _profile = new();
    private volatile IReadOnlyList<IRgbBackend> _backends = [];
    private IReadOnlyList<DeviceFrame> _preview = [];

    // Plan de rendu, reconstruit seulement quand le profil ou la liste des appareils change.
    private Profile? _planProfile;
    private readonly List<RgbDevice> _planDevices = [];
    private Target[] _targets = [];
    private Strip[] _strips = [];

    private int _fps = 30;
    private int _backgroundFps = 20;
    private volatile bool _forceSend;
    private volatile bool _previewEnabled = true;
    private volatile bool _background;
    private double _master = 1;
    private bool _lightsOn = true;

    public LightingEngine()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "PrismRGB engine", Priority = ThreadPriority.BelowNormal };
    }

    public int TargetFps
    {
        get => _fps;
        set => _fps = Math.Clamp(value, 5, 120);
    }

    /// <summary>Cadence quand l'interface est réduite (jeu en cours) : moins de calcul, animations toujours fluides.</summary>
    public int BackgroundFps
    {
        get => _backgroundFps;
        set => _backgroundFps = Math.Clamp(value, 5, 120);
    }

    /// <summary>true quand la fenêtre est cachée/réduite : cadence réduite et pas d'aperçu.</summary>
    public bool Background
    {
        get => _background;
        set
        {
            _background = value;
            _previewEnabled = !value;
            _wake.Set();
        }
    }

    /// <summary>Luminosité générale 0..1.</summary>
    public double MasterBrightness
    {
        get => _master;
        set { _master = value; _forceSend = true; _wake.Set(); }
    }

    /// <summary>false = tout éteint (noir envoyé à tous les appareils).</summary>
    public bool LightsOn
    {
        get => _lightsOn;
        set { _lightsOn = value; _forceSend = true; _wake.Set(); }
    }

    /// <summary>Renvoi périodique de l'image même si rien ne change : corrige les appareils qui se réinitialisent seuls.</summary>
    public TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(1.5);

    public double MeasuredFps { get; private set; }

    /// <summary>true si la dernière image contenait une animation (sinon le moteur dort).</summary>
    public bool IsAnimating { get; private set; }

    /// <summary>
    /// Levé quand la boucle a été suspendue plusieurs secondes (sortie de veille) :
    /// les contrôleurs ont souvent perdu leur mode direct et doivent être reconfigurés.
    /// </summary>
    public event EventHandler? ResumeDetected;

    public void Start() => _thread.Start();

    public void SetBackends(IReadOnlyList<IRgbBackend> backends)
    {
        _backends = backends;
        _forceSend = true;
        _wake.Set();
    }

    public void SetProfile(Profile profile)
    {
        _profile = profile.Clone();
        _forceSend = true;
        _wake.Set();
    }

    /// <summary>Force l'envoi de l'image complète à la prochaine frame.</summary>
    public void Resend()
    {
        _forceSend = true;
        _wake.Set();
    }

    public IReadOnlyList<DeviceFrame> GetPreview()
    {
        lock (_lock) return _preview;
    }

    private void Run()
    {
        var sw = Stopwatch.StartNew();
        double last = 0, fpsWindowStart = 0;
        int frames = 0;
        while (!_stop.IsSet)
        {
            double now = sw.Elapsed.TotalSeconds;
            double gap = now - last;
            double dt = Math.Clamp(gap, 0, 0.1);
            last = now;
            if (gap > 4 && now > 5)
            {
                try { ResumeDetected?.Invoke(this, EventArgs.Empty); } catch { }
            }

            bool animated = true;
            try { animated = RenderFrame(now, dt); }
            catch (Exception ex) { Debug.WriteLine($"Engine frame error: {ex}"); }
            IsAnimating = animated;

            frames++;
            if (now - fpsWindowStart >= 1)
            {
                MeasuredFps = frames / (now - fpsWindowStart);
                frames = 0;
                fpsWindowStart = now;
            }

            if (!animated)
            {
                // Rien ne bouge : on dort jusqu'au prochain changement ou au renvoi de maintien.
                WaitHandle.WaitAny([_stop.WaitHandle, _wake], KeepAlive);
                continue;
            }

            double frameTime = 1.0 / (_background ? Math.Min(_fps, _backgroundFps) : _fps);
            double sleep = frameTime - (sw.Elapsed.TotalSeconds - now);
            if (sleep > 0) _stop.Wait(TimeSpan.FromSeconds(sleep));
        }
    }

    private bool DevicesChanged(IReadOnlyList<IRgbBackend> backends)
    {
        // Boucles indexées : un foreach sur IReadOnlyList allouerait un énumérateur à chaque image.
        int i = 0;
        for (int b = 0; b < backends.Count; b++)
        {
            var devices = backends[b].Devices;
            for (int k = 0; k < devices.Count; k++)
            {
                var d = devices[k];
                if (d.LedCount <= 0) continue;
                if (i >= _planDevices.Count || !ReferenceEquals(_planDevices[i], d)) return true;
                i++;
            }
        }
        return i != _planDevices.Count;
    }

    private void BuildPlan(Profile profile, IReadOnlyList<IRgbBackend> backends)
    {
        _planProfile = profile;
        _planDevices.Clear();
        var targets = new List<Target>();
        var byKey = new Dictionary<string, Target>();
        foreach (var b in backends)
            foreach (var d in b.Devices)
            {
                if (d.LedCount <= 0) continue;
                _planDevices.Add(d);
                var t = new Target { Backend = b, Device = d, Buffer = new Rgb[d.LedCount], LastSent = new Rgb[d.LedCount] };
                targets.Add(t);
                byKey[d.Key] = t;
            }

        var strips = new List<Strip>();
        var chain = new List<(Part part, int order)>();
        int fallbackOrder = 100_000;
        foreach (var t in targets)
        {
            foreach (var z in t.Device.Zones)
            {
                if (z.LedCount <= 0) continue;
                string key = ZoneKeys.Zone(t.Device.Key, z.Index, z.Name);
                var cfg = profile.Zones.TryGetValue(key, out var c) ? c : null;
                int order = cfg?.Order ?? fallbackOrder++;
                cfg ??= new ZoneConfig();
                if (!cfg.Enabled) continue; // reste noire
                var part = new Part { Device = t.Device, Zone = z, Reverse = cfg.Reverse, Buffer = t.Buffer };
                if (cfg.Override is { } ovr) strips.Add(NewStrip("ovr:" + key, ovr, [part]));
                else if (!profile.SyncAcrossDevices) strips.Add(NewStrip("zone:" + key, profile.Global, [part]));
                else chain.Add((part, order));
            }
        }
        if (chain.Count > 0)
            strips.Add(NewStrip("global", profile.Global, chain.OrderBy(c => c.order).Select(c => c.part).ToArray()));

        // Conserve l'état des effets (traînées, particules...) quand c'est le même effet sur la même bande.
        foreach (var s in strips)
            if (_effectCache.TryGetValue(s.Key, out var e) && e.id == s.Settings.Effect)
            {
                s.Effect = e.effect;
                s.EffectId = e.id;
            }
        _effectCache.Clear();
        _targets = [.. targets];
        _strips = [.. strips];
    }

    private static Strip NewStrip(string key, EffectSettings settings, Part[] parts) => new()
    {
        Key = key,
        Settings = settings,
        Parts = parts,
        Pixels = new Rgb[parts.Sum(p => p.Zone.LedCount)],
    };

    /// <summary>Calcule et envoie une image. Retourne false si rien n'est animé (le moteur peut dormir). Public pour les tests.</summary>
    public bool RenderFrame(double time, double dt)
    {
        var profile = _profile;
        var backends = _backends;
        if (!ReferenceEquals(profile, _planProfile) || DevicesChanged(backends)) BuildPlan(profile, backends);

        foreach (var t in _targets) Array.Clear(t.Buffer);

        bool animated = false;
        foreach (var s in _strips)
        {
            if (s.Effect is null || s.EffectId != s.Settings.Effect)
            {
                s.Effect = EffectRegistry.Get(s.Settings.Effect).Create();
                s.EffectId = s.Settings.Effect;
            }
            _effectCache[s.Key] = (s.EffectId, s.Effect);
            animated |= s.Effect.IsAnimated;
            s.Effect.Render(s.Pixels, new EffectContext(time, dt, s.Settings, _rng));

            int o = 0;
            foreach (var p in s.Parts)
            {
                var z = p.Zone;
                for (int i = 0; i < z.LedCount; i++)
                {
                    int dst = z.StartIndex + (p.Reverse ? z.LedCount - 1 - i : i);
                    if ((uint)dst < (uint)p.Buffer.Length) p.Buffer[dst] = s.Pixels[o + i];
                }
                o += z.LedCount;
            }
        }

        double master = _lightsOn ? Math.Clamp(_master, 0, 1) : 0;
        if (master < 1)
        {
            animated &= master > 0;
            foreach (var t in _targets)
                for (int i = 0; i < t.Buffer.Length; i++) t.Buffer[i] = t.Buffer[i].Scale(master);
        }

        // Envoi : seulement si l'image a changé, ou périodiquement pour le maintien.
        bool force = _forceSend;
        _forceSend = false;
        long nowTicks = Stopwatch.GetTimestamp();
        long keepAliveTicks = (long)(KeepAlive.TotalSeconds * Stopwatch.Frequency);
        foreach (var t in _targets)
        {
            bool changed = !t.EverSent || !t.LastSent.AsSpan().SequenceEqual(t.Buffer);
            bool stale = nowTicks - t.LastSentAt > keepAliveTicks;
            if (changed || stale || force)
            {
                t.Backend.SubmitFrame(t.Device, t.Buffer);
                t.Buffer.CopyTo(t.LastSent, 0);
                t.LastSentAt = nowTicks;
                t.EverSent = true;
            }
        }

        if (_previewEnabled)
        {
            var preview = new DeviceFrame[_targets.Length];
            for (int i = 0; i < _targets.Length; i++)
                preview[i] = new DeviceFrame(_targets[i].Backend, _targets[i].Device, (Rgb[])_targets[i].Buffer.Clone());
            lock (_lock) _preview = preview;
        }
        return animated;
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
        _wake.Dispose();
    }
}
