// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using PrismRGB.Core.Devices;

namespace PrismRGB.Core.Demo;

/// <summary>Appareils virtuels pour tester les effets sans matériel (aperçu seulement).</summary>
public sealed class DemoBackend : IRgbBackend
{
    private IReadOnlyList<RgbDevice> _devices = [];

    public string Name => "Démo";
    public BackendState State { get; private set; } = BackendState.Stopped;
    public string StatusText => State == BackendState.Connected ? $"Mode démo : {_devices.Count} appareils simulés" : "Arrêté";
    public IReadOnlyList<RgbDevice> Devices => _devices;

    public event EventHandler? DevicesChanged;
    public event EventHandler? StateChanged;

    public Task StartAsync(CancellationToken ct = default)
    {
        _devices =
        [
            Make(0, "Carte mère MSI (démo)", DeviceKind.Motherboard, ("Logo / chipset", 6), ("JRAINBOW1", 24), ("JRAINBOW2", 12)),
            Make(1, "RAM DDR5 slot A2 (démo)", DeviceKind.Dram, ("Barrette", 10)),
            Make(2, "RAM DDR5 slot B2 (démo)", DeviceKind.Dram, ("Barrette", 10)),
            Make(3, "RTX 5070 (démo)", DeviceKind.Gpu, ("Bandeau", 12)),
            Make(4, "Water cooling (démo)", DeviceKind.Cooler, ("Pompe", 16), ("Ventilo radiateur 1", 18), ("Ventilo radiateur 2", 18), ("Ventilo radiateur 3", 18)),
            Make(5, "Ventilos boîtier (démo)", DeviceKind.Cooler, ("Arrière", 18), ("Façade 1", 18), ("Façade 2", 18)),
            Make(6, "Bande LED boîtier (démo)", DeviceKind.LedStrip, ("Bande", 40)),
        ];
        State = BackendState.Connected;
        StateChanged?.Invoke(this, EventArgs.Empty);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private static RgbDevice Make(int idx, string name, DeviceKind kind, params (string name, int leds)[] zones)
    {
        var list = new List<RgbZone>();
        int start = 0;
        for (int i = 0; i < zones.Length; i++)
        {
            list.Add(new RgbZone
            {
                Index = i,
                Name = zones[i].name,
                Kind = ZoneKind.Linear,
                StartIndex = start,
                LedCount = zones[i].leds,
                MinLeds = zones[i].leds,
                MaxLeds = zones[i].leds,
            });
            start += zones[i].leds;
        }
        return new RgbDevice
        {
            Key = $"demo:{name}",
            BackendIndex = idx,
            Name = name,
            Vendor = "PrismRGB",
            Kind = kind,
            Zones = list,
            LedCount = start,
            HasDirectMode = true,
        };
    }

    public void SubmitFrame(RgbDevice device, ReadOnlySpan<Rgb> colors) { }
    public Task RescanAsync() => Task.CompletedTask;
    public Task ReapplyAsync() => Task.CompletedTask;
    public Task ResizeZoneAsync(RgbDevice device, int zoneIndex, int ledCount) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        State = BackendState.Stopped;
        return ValueTask.CompletedTask;
    }
}
