using System.Diagnostics;
using PrismRGB.Core.Config;
using PrismRGB.Core.Devices;
using PrismRGB.Core.Effects;

namespace PrismRGB.Core.Engine;

public sealed record DeviceFrame(IRgbBackend Backend, RgbDevice Device, Rgb[] Colors);

/// <summary>
/// Boucle d'animation : calcule chaque image à partir du profil actif et l'envoie aux backends.
/// Tourne sur son propre thread, à cadence fixe. Le profil est remplacé en bloc (copie) pour éviter
/// tout partage d'état avec l'interface.
/// </summary>
public sealed class LightingEngine : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (string id, Effect effect)> _effects = [];
    private readonly Dictionary<string, Rgb[]> _buffers = [];
    private readonly Dictionary<string, Rgb[]> _lastSent = [];
    private readonly Dictionary<string, long> _lastSentAt = [];
    private readonly Random _rng = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private volatile Profile _profile = new();
    private volatile IReadOnlyList<IRgbBackend> _backends = [];
    private List<DeviceFrame> _preview = [];
    private int _fps = 30;
    private volatile bool _forceSend;

    public LightingEngine()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "PrismRGB engine", Priority = ThreadPriority.AboveNormal };
    }

    public int TargetFps
    {
        get => _fps;
        set => _fps = Math.Clamp(value, 5, 120);
    }

    /// <summary>Renvoi périodique de l'image même si rien ne change : corrige les appareils qui se réinitialisent seuls.</summary>
    public TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(1.5);

    public double MeasuredFps { get; private set; }

    public void Start() => _thread.Start();

    public void SetBackends(IReadOnlyList<IRgbBackend> backends)
    {
        _backends = backends;
        _forceSend = true;
    }

    public void SetProfile(Profile profile)
    {
        _profile = profile.Clone();
        _forceSend = true;
    }

    /// <summary>Force l'envoi de l'image complète à la prochaine frame.</summary>
    public void Resend() => _forceSend = true;

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
            double dt = Math.Clamp(now - last, 0, 0.1);
            last = now;
            try { RenderFrame(now, dt); }
            catch (Exception ex) { Debug.WriteLine($"Engine frame error: {ex}"); }

            frames++;
            if (now - fpsWindowStart >= 1)
            {
                MeasuredFps = frames / (now - fpsWindowStart);
                frames = 0;
                fpsWindowStart = now;
            }

            double frameTime = 1.0 / _fps;
            double sleep = frameTime - (sw.Elapsed.TotalSeconds - now);
            if (sleep > 0) _stop.Wait(TimeSpan.FromSeconds(sleep));
        }
    }

    /// <summary>Calcule une image. Public pour les tests.</summary>
    public void RenderFrame(double time, double dt)
    {
        var profile = _profile;
        var backends = _backends;
        var used = new HashSet<string>();

        var devices = new List<(IRgbBackend backend, RgbDevice device)>();
        foreach (var b in backends)
            foreach (var d in b.Devices)
                if (d.LedCount > 0) devices.Add((b, d));

        foreach (var (_, d) in devices)
        {
            if (!_buffers.TryGetValue(d.Key, out var buf) || buf.Length != d.LedCount)
                _buffers[d.Key] = buf = new Rgb[d.LedCount];
            Array.Clear(buf);
        }

        // Répartition des zones : chaîne synchronisée, zones indépendantes, zones avec effet propre.
        var chain = new List<(RgbDevice d, RgbZone z, ZoneConfig cfg, int order)>();
        int fallbackOrder = 100_000;
        foreach (var (_, d) in devices)
        {
            foreach (var z in d.Zones)
            {
                if (z.LedCount <= 0) continue;
                string key = ZoneKeys.Zone(d.Key, z.Index, z.Name);
                var cfg = profile.Zones.TryGetValue(key, out var c) ? c : null;
                int order = cfg?.Order ?? fallbackOrder++;
                cfg ??= new ZoneConfig();
                if (!cfg.Enabled) continue; // laissée noire

                if (cfg.Override is { } ovr)
                {
                    RenderStrip("ovr:" + key, ovr, [(d, z, cfg.Reverse)], time, dt, used);
                }
                else if (!profile.SyncAcrossDevices)
                {
                    RenderStrip("zone:" + key, profile.Global, [(d, z, cfg.Reverse)], time, dt, used);
                }
                else
                {
                    chain.Add((d, z, cfg, order));
                }
            }
        }

        if (chain.Count > 0)
        {
            var parts = chain.OrderBy(c => c.order).Select(c => (c.d, c.z, c.cfg.Reverse)).ToList();
            RenderStrip("global", profile.Global, parts, time, dt, used);
        }

        foreach (var k in _effects.Keys.Where(k => !used.Contains(k)).ToList()) _effects.Remove(k);

        // Envoi : seulement si l'image a changé, ou périodiquement pour le maintien.
        bool force = _forceSend;
        _forceSend = false;
        long nowTicks = Stopwatch.GetTimestamp();
        long keepAliveTicks = (long)(KeepAlive.TotalSeconds * Stopwatch.Frequency);
        var preview = new List<DeviceFrame>(devices.Count);
        foreach (var (b, d) in devices)
        {
            var buf = _buffers[d.Key];
            bool changed = !_lastSent.TryGetValue(d.Key, out var prev) || prev.Length != buf.Length || !prev.AsSpan().SequenceEqual(buf);
            bool stale = !_lastSentAt.TryGetValue(d.Key, out long at) || nowTicks - at > keepAliveTicks;
            if (changed || stale || force)
            {
                b.SubmitFrame(d, buf);
                _lastSent[d.Key] = (Rgb[])buf.Clone();
                _lastSentAt[d.Key] = nowTicks;
            }
            preview.Add(new DeviceFrame(b, d, (Rgb[])buf.Clone()));
        }
        lock (_lock) _preview = preview;
    }

    private void RenderStrip(string key, EffectSettings settings, List<(RgbDevice d, RgbZone z, bool reverse)> parts,
        double time, double dt, HashSet<string> used)
    {
        used.Add(key);
        if (!_effects.TryGetValue(key, out var e) || e.id != settings.Effect)
        {
            e = (settings.Effect, EffectRegistry.Get(settings.Effect).Create());
            _effects[key] = e;
        }

        int total = parts.Sum(p => p.z.LedCount);
        var strip = new Rgb[total];
        e.effect.Render(strip, new EffectContext(time, dt, settings, _rng));

        int o = 0;
        foreach (var (d, z, reverse) in parts)
        {
            var buf = _buffers[d.Key];
            for (int i = 0; i < z.LedCount; i++)
            {
                int dst = z.StartIndex + (reverse ? z.LedCount - 1 - i : i);
                if ((uint)dst < (uint)buf.Length) buf[dst] = strip[o + i];
            }
            o += z.LedCount;
        }
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }
}
