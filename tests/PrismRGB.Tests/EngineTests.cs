// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using PrismRGB.Core;
using PrismRGB.Core.Config;
using PrismRGB.Core.Demo;
using PrismRGB.Core.Devices;
using PrismRGB.Core.Effects;
using PrismRGB.Core.Engine;

namespace PrismRGB.Tests;

public class EngineTests
{
    private static async Task<(LightingEngine engine, DemoBackend demo)> Setup(Profile p)
    {
        var demo = new DemoBackend();
        await demo.StartAsync();
        var engine = new LightingEngine();
        engine.SetBackends([demo]);
        engine.SetProfile(p);
        return (engine, demo);
    }

    public static TheoryData<string, EffectDirection, bool> AllCombos()
    {
        var d = new TheoryData<string, EffectDirection, bool>();
        foreach (var e in EffectRegistry.All)
            foreach (var dir in Enum.GetValues<EffectDirection>())
                foreach (var smooth in new[] { true, false })
                    d.Add(e.Id, dir, smooth);
        return d;
    }

    [Theory]
    [MemberData(nameof(AllCombos))]
    public void EveryEffect_RendersOnAnyLength(string id, EffectDirection dir, bool smooth)
    {
        var info = EffectRegistry.Get(id);
        var s = new EffectSettings { Effect = id, Direction = dir, Smooth = smooth, Colors = [Rgb.FromHex("#FF0000"), Rgb.FromHex("#00FF00"), Rgb.FromHex("#0000FF")] };
        foreach (int n in new[] { 1, 2, 3, 7, 10, 18, 61, 300 })
        {
            var fx = info.Create();
            var leds = new Rgb[n];
            bool lit = false;
            for (int f = 0; f < 200; f++)
            {
                fx.Render(leds, new EffectContext(f / 30.0, 1 / 30.0, s, new Random(f)));
                lit |= leds.Any(c => !c.IsBlack);
            }
            Assert.True(lit, $"{id} n={n} ne s'allume jamais");
        }
    }

    [Fact]
    public void SpeedAndSizeExtremes_DoNotThrow()
    {
        foreach (var e in EffectRegistry.All)
            foreach (int v in new[] { 0, 1, 100, 1000, -5 })
            {
                var s = new EffectSettings { Effect = e.Id, Speed = v, Size = v, Brightness = v, Colors = [] };
                var fx = e.Create();
                var leds = new Rgb[24];
                for (int f = 0; f < 50; f++) fx.Render(leds, new EffectContext(f, 0.1, s, new Random(1)));
            }
    }

    [Fact]
    public async Task StaticRed_FillsEveryLed_AndBrightnessScales()
    {
        var p = new Profile { Global = new EffectSettings { Effect = "static", Colors = [Rgb.FromHex("#FF0000")], Brightness = 50 } };
        var (engine, demo) = await Setup(p);
        engine.RenderFrame(0, 0.03);
        var frames = engine.GetPreview();
        Assert.Equal(demo.Devices.Count, frames.Count);
        Assert.All(frames.SelectMany(f => f.Colors), c => Assert.Equal(new Rgb(128, 0, 0), c));
    }

    [Fact]
    public async Task DisabledZone_IsBlack_AndOverrideApplies()
    {
        var demo = new DemoBackend();
        await demo.StartAsync();
        var board = demo.Devices[0];
        var p = new Profile { Global = new EffectSettings { Effect = "static", Colors = [Rgb.White] } };
        p.Zones[ZoneKeys.Zone(board.Key, 0, board.Zones[0].Name)] = new ZoneConfig { Enabled = false };
        p.Zones[ZoneKeys.Zone(board.Key, 1, board.Zones[1].Name)] = new ZoneConfig
        {
            Override = new EffectSettings { Effect = "static", Colors = [Rgb.FromHex("#00FF00")] },
        };
        var engine = new LightingEngine();
        engine.SetBackends([demo]);
        engine.SetProfile(p);
        engine.RenderFrame(0, 0.03);
        var colors = engine.GetPreview().First(f => f.Device.Key == board.Key).Colors;
        var z0 = board.Zones[0];
        var z1 = board.Zones[1];
        var z2 = board.Zones[2];
        Assert.All(colors.Skip(z0.StartIndex).Take(z0.LedCount), c => Assert.Equal(Rgb.Black, c));
        Assert.All(colors.Skip(z1.StartIndex).Take(z1.LedCount), c => Assert.Equal(new Rgb(0, 255, 0), c));
        Assert.All(colors.Skip(z2.StartIndex).Take(z2.LedCount), c => Assert.Equal(Rgb.White, c));
    }

    [Fact]
    public async Task SyncChain_FollowsOrder_AndReverse()
    {
        var demo = new DemoBackend();
        await demo.StartAsync();
        var p = new Profile
        {
            Global = new EffectSettings { Effect = "multistatic", Smooth = false, Colors = [Rgb.FromHex("#FF0000"), Rgb.FromHex("#0000FF")] },
            SyncAcrossDevices = true,
        };
        // Seulement deux zones actives : RAM A2 (en 2e) puis RAM B2 (en 1er, inversée).
        foreach (var d in demo.Devices)
            foreach (var z in d.Zones)
                p.Zones[ZoneKeys.Zone(d.Key, z.Index, z.Name)] = new ZoneConfig { Enabled = false };
        var ramA = demo.Devices[1];
        var ramB = demo.Devices[2];
        p.Zones[ZoneKeys.Zone(ramA.Key, 0, ramA.Zones[0].Name)] = new ZoneConfig { Order = 2 };
        p.Zones[ZoneKeys.Zone(ramB.Key, 0, ramB.Zones[0].Name)] = new ZoneConfig { Order = 1, Reverse = true };

        var engine = new LightingEngine();
        engine.SetBackends([demo]);
        engine.SetProfile(p);
        engine.RenderFrame(0, 0.03);
        var prev = engine.GetPreview();
        var a = prev.First(f => f.Device.Key == ramA.Key).Colors;
        var b = prev.First(f => f.Device.Key == ramB.Key).Colors;
        // Chaîne de 20 LEDs : 10 rouges (RAM B) puis 10 bleues (RAM A).
        Assert.All(a, c => Assert.Equal(new Rgb(0, 0, 255), c));
        Assert.All(b, c => Assert.Equal(new Rgb(255, 0, 0), c));
    }

    [Fact]
    public void Direction_LeftIsMirrorOfRight()
    {
        var right = new Rgb[9];
        var left = new Rgb[9];
        var colors = new List<Rgb> { Rgb.FromHex("#FF0000"), Rgb.FromHex("#0000FF") };
        new MultiStaticEffect().Render(right, new EffectContext(0, 0, new EffectSettings { Effect = "multistatic", Colors = colors }, new Random()));
        new MultiStaticEffect().Render(left, new EffectContext(0, 0, new EffectSettings { Effect = "multistatic", Colors = colors, Direction = EffectDirection.Left }, new Random()));
        Assert.Equal(right.Reverse(), left);

        var center = new Rgb[9];
        new MultiStaticEffect().Render(center, new EffectContext(0, 0, new EffectSettings { Effect = "multistatic", Colors = colors, Direction = EffectDirection.CenterOut }, new Random()));
        Assert.Equal(Rgb.FromHex("#FF0000"), center[4]);
        Assert.Equal(center[0], center[8]);
        Assert.Equal(Rgb.FromHex("#0000FF"), center[0]);
    }

    [Fact]
    public void Rgb_HexAndHsv()
    {
        Assert.Equal(new Rgb(0x12, 0xAB, 0xEF), Rgb.FromHex("#12abef"));
        Assert.Equal(new Rgb(255, 255, 0), Rgb.FromHex("ff0"));
        Assert.False(Rgb.TryParseHex("zzz", out _));
        Assert.Equal(new Rgb(255, 0, 0), Rgb.FromHsv(0, 1, 1));
        Assert.Equal(new Rgb(0, 255, 0), Rgb.FromHsv(120, 1, 1));
        Assert.Equal(new Rgb(0, 0, 255), Rgb.FromHsv(-120, 1, 1));
    }

    [Fact]
    public void Config_RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prismrgb-test-" + Guid.NewGuid());
        try
        {
            var store = new ConfigStore(dir);
            var cfg = store.Load();
            Assert.NotEmpty(cfg.Profiles);
            cfg.Profiles[0].Global.Colors = [Rgb.FromHex("#123456")];
            cfg.Profiles[0].Zones["k::0:Z"] = new ZoneConfig { Reverse = true, Override = new EffectSettings { Effect = "fire" } };
            cfg.ZoneSizes["k::1:JRAINBOW1"] = 42;
            store.Save(cfg);
            var back = new ConfigStore(dir).Load();
            Assert.Equal(Rgb.FromHex("#123456"), back.Profiles[0].Global.Colors[0]);
            Assert.True(back.Profiles[0].Zones["k::0:Z"].Reverse);
            Assert.Equal("fire", back.Profiles[0].Zones["k::0:Z"].Override!.Effect);
            Assert.Equal(42, back.ZoneSizes["k::1:JRAINBOW1"]);
            Assert.Contains("\"#123456\"", File.ReadAllText(store.FilePath));

            File.WriteAllText(store.FilePath, "{ pas du json");
            var recovered = new ConfigStore(dir).Load();
            Assert.NotEmpty(recovered.Profiles);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task EngineThread_RunsAndSubmits()
    {
        var counting = new CountingBackend();
        await counting.StartAsync();
        using var engine = new LightingEngine { TargetFps = 60 };
        engine.SetBackends([counting]);
        engine.SetProfile(new Profile { Global = new EffectSettings { Effect = "rainbow" } });
        engine.Start();
        await Task.Delay(500);
        Assert.InRange(counting.Frames, 15, 40);
    }

    private sealed class CountingBackend : IRgbBackend
    {
        public int Frames;
        public string Name => "count";
        public BackendState State => BackendState.Connected;
        public string StatusText => "";
        public IReadOnlyList<RgbDevice> Devices { get; } =
        [
            new RgbDevice
            {
                Key = "c", BackendIndex = 0, Name = "c", LedCount = 10,
                Zones = [new RgbZone { Index = 0, Name = "z", Kind = ZoneKind.Linear, StartIndex = 0, LedCount = 10 }],
            },
        ];
        public event EventHandler? DevicesChanged { add { } remove { } }
        public event EventHandler? StateChanged { add { } remove { } }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void SubmitFrame(RgbDevice device, ReadOnlySpan<Rgb> colors) => Interlocked.Increment(ref Frames);
        public Task RescanAsync() => Task.CompletedTask;
        public Task ReapplyAsync() => Task.CompletedTask;
        public Task ResizeZoneAsync(RgbDevice device, int zoneIndex, int ledCount) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public class EnginePerformanceTests
{
    [Fact]
    public async Task SteadyState_AllocatesNothingWithoutPreview()
    {
        var demo = new DemoBackend();
        await demo.StartAsync();
        var engine = new LightingEngine { Background = true }; // fenêtre réduite : pas d'aperçu
        engine.SetBackends([demo]);
        engine.SetProfile(new Profile { Global = new EffectSettings { Effect = "meteor" } });
        for (int i = 0; i < 50; i++) engine.RenderFrame(i / 30.0, 1 / 30.0);   // préchauffage

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 50; i < 350; i++) engine.RenderFrame(i / 30.0, 1 / 30.0);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 4096, $"{allocated} octets alloués pour 300 images");
    }

    [Fact]
    public async Task StaticEffect_LetsEngineSleep()
    {
        var demo = new DemoBackend();
        await demo.StartAsync();
        var engine = new LightingEngine();
        engine.SetBackends([demo]);
        engine.SetProfile(new Profile { Global = new EffectSettings { Effect = "static" } });
        Assert.False(engine.RenderFrame(0, 0.03));
        engine.SetProfile(new Profile { Global = new EffectSettings { Effect = "rainbow" } });
        Assert.True(engine.RenderFrame(0.03, 0.03));
        engine.LightsOn = false;
        Assert.False(engine.RenderFrame(0.06, 0.03));                 // tout éteint : rien à animer
    }

    [Fact]
    public async Task IdleEngineThread_UsesAlmostNoFrames()
    {
        var demo = new DemoBackend();
        await demo.StartAsync();
        using var engine = new LightingEngine { TargetFps = 60 };
        engine.SetBackends([demo]);
        engine.SetProfile(new Profile { Global = new EffectSettings { Effect = "static" } });
        engine.Start();
        await Task.Delay(700);
        Assert.False(engine.IsAnimating);
        Assert.True(engine.MeasuredFps < 5, $"{engine.MeasuredFps} img/s au repos");
    }
}
