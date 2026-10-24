using PrismRGB.Core;
using PrismRGB.Core.Config;
using PrismRGB.Core.Devices;
using PrismRGB.Core.OpenRgb;
using static PrismRGB.Tests.FakeOpenRgbServer;

namespace PrismRGB.Tests;

public class OpenRgbTests
{
    private static FakeDevice Board() => new()
    {
        Type = 0,
        Name = "MSI MAG X870 TOMAHAWK WIFI (MS-7E51)",
        Zones =
        [
            new FakeZone { Name = "JRGB1", Type = 0, Min = 1, Max = 1, Count = 1 },
            new FakeZone { Name = "JRAINBOW1", Type = 1, Min = 0, Max = 120, Count = 0 },
            new FakeZone { Name = "Matrix", Type = 2, Min = 6, Max = 6, Count = 6, MatrixH = 2, MatrixW = 3 },
        ],
    };

    private static FakeDevice Ram(string loc) => new()
    {
        Type = 1,
        Name = "Kingston Fury DDR5 RGB",
        Location = loc,
        Serial = "",
        Zones = [new FakeZone { Name = "DRAM", Type = 1, Min = 12, Max = 12, Count = 12 }],
    };

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    public void ParseController_AllProtocolVersions(uint proto)
    {
        var bytes = EncodeController(Board(), proto);
        var c = OpenRgbProtocol.ParseController(bytes, proto);
        Assert.Equal("MSI MAG X870 TOMAHAWK WIFI (MS-7E51)", c.Name);
        Assert.Equal(proto >= 1 ? "Vendor" : "", c.Vendor);
        Assert.Equal("HID: /dev/x", c.Location);
        Assert.Equal(2, c.Modes.Count);
        Assert.Equal("Direct", c.Modes[0].Name);
        Assert.True(c.Modes[0].HasPerLedColor);
        Assert.Equal(3, c.Zones.Count);
        Assert.Equal("JRAINBOW1", c.Zones[1].Name);
        Assert.Equal(120u, c.Zones[1].LedsMax);
        Assert.Equal(2u, c.Zones[2].MatrixHeight);
        Assert.Equal(7, c.ColorCount);
        Assert.Equal(7, c.LedNames.Count);
    }

    [Fact]
    public void ParseController_TruncatedThrowsCleanly()
    {
        var bytes = EncodeController(Board(), 4);
        Assert.Throws<InvalidDataException>(() => OpenRgbProtocol.ParseController(bytes.AsSpan(0, 60), 4));
    }

    [Fact]
    public void UpdateLedsPacket_Layout()
    {
        var buf = Array.Empty<byte>();
        int len = OpenRgbProtocol.BuildUpdateLeds(ref buf, 3, [new Rgb(1, 2, 3), new Rgb(4, 5, 6)]);
        Assert.Equal(16 + 4 + 2 + 8, len);
        Assert.Equal("ORGB"u8.ToArray(), buf[..4]);
        Assert.Equal(3, BitConverter.ToInt32(buf, 4));
        Assert.Equal(1050, BitConverter.ToInt32(buf, 8));
        Assert.Equal(14, BitConverter.ToInt32(buf, 12));
        Assert.Equal(14, BitConverter.ToInt32(buf, 16));
        Assert.Equal(2, BitConverter.ToInt16(buf, 20));
        Assert.Equal(new byte[] { 1, 2, 3, 0, 4, 5, 6, 0 }, buf[22..30]);
    }

    private static async Task WaitFor(Func<bool> cond, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("Condition non atteinte");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Backend_ConnectsResizesStreamsAndReconnects()
    {
        await using var server = new FakeOpenRgbServer();
        server.Devices.Add(Board());
        server.Devices.Add(Ram("I2C: PIIX4, address 0x61"));
        server.Devices.Add(Ram("I2C: PIIX4, address 0x63"));
        server.Start();

        var settings = new OpenRgbSettings { Port = server.Port, AutoLaunch = false };
        var sizes = new Dictionary<string, int>();
        await using var backend = new OpenRgbBackend(settings, () => sizes);

        // Taille mémorisée pour le header ARGB (clé construite comme le ferait l'interface).
        var boardKey = "orgb:MSI MAG X870 TOMAHAWK WIFI (MS-7E51)|HID: /dev/x|SN";
        sizes[ZoneKeys.Zone(boardKey, 1, "JRAINBOW1")] = 30;

        await backend.StartAsync();
        await WaitFor(() => backend.State == BackendState.Connected);

        Assert.Equal("PrismRGB", server.ClientName);
        Assert.Equal(4u, backend.Protocol);
        Assert.Equal(3, backend.Devices.Count);
        Assert.Equal(boardKey, backend.Devices[0].Key);
        Assert.Contains(server.Resizes, r => r is { dev: 0, zone: 1, size: 30 });
        Assert.Equal(1 + 30 + 6, backend.Devices[0].LedCount);
        Assert.Equal(30, backend.Devices[0].Zones[1].LedCount);
        Assert.Equal(1, backend.Devices[0].Zones[1].StartIndex);
        Assert.Equal(DeviceKind.Dram, backend.Devices[1].Kind);
        Assert.NotEqual(backend.Devices[1].Key, backend.Devices[2].Key);
        await WaitFor(() => server.CustomModeCalls.Distinct().Count() == 3);

        var ram = backend.Devices[1];
        var frame = Enumerable.Repeat(new Rgb(0x11, 0x22, 0x33), ram.LedCount).ToArray();
        backend.SubmitFrame(ram, frame);
        await WaitFor(() => server.LedUpdates.Any(u => u.dev == 1));
        var upd = server.LedUpdates.First(u => u.dev == 1);
        Assert.Equal(12, upd.colors.Length);
        Assert.All(upd.colors, c => Assert.Equal(0x00332211u, c)); // RGBColor = 0x00BBGGRR

        // Nouvel appareil signalé par le serveur.
        server.Devices.Add(new FakeDevice { Type = 4, Name = "Strip", Zones = [new FakeZone { Name = "S", Count = 20, Min = 20, Max = 20 }] });
        await server.SendDeviceListUpdatedAsync();
        await WaitFor(() => backend.Devices.Count == 4);

        // Coupure réseau : reconnexion automatique.
        server.DropClients();
        await WaitFor(() => backend.State != BackendState.Connected);
        await WaitFor(() => backend.State == BackendState.Connected, 10000);
        Assert.Equal(4, backend.Devices.Count);
    }

    [Fact]
    public async Task Backend_OldProtocolServer()
    {
        await using var server = new FakeOpenRgbServer { ServerProtocol = 1 };
        server.Devices.Add(Board());
        server.Start();
        await using var backend = new OpenRgbBackend(new OpenRgbSettings { Port = server.Port, AutoLaunch = false }, () => new Dictionary<string, int>());
        await backend.StartAsync();
        await WaitFor(() => backend.State == BackendState.Connected);
        Assert.Equal(1u, backend.Protocol);
        Assert.Equal(7, backend.Devices[0].LedCount);
    }

    [Fact]
    public async Task Backend_NoServer_ReportsUnreachable()
    {
        await using var backend = new OpenRgbBackend(new OpenRgbSettings { Port = 1, AutoLaunch = false }, () => new Dictionary<string, int>());
        await backend.StartAsync();
        await WaitFor(() => backend.State == BackendState.Reconnecting);
        Assert.Contains("injoignable", backend.StatusText);
    }
}
