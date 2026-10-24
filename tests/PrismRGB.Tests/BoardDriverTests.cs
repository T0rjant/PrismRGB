// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using PrismRGB.Core;
using PrismRGB.Core.Config;
using PrismRGB.Core.Drivers.AsRock;
using PrismRGB.Core.Drivers.Asus;
using PrismRGB.Core.Drivers.Gigabyte;
using PrismRGB.Core.Hid;
using PrismRGB.Core.Native;
using PrismRGB.Core.Platform;

namespace PrismRGB.Tests;

/// <summary>Contrôleur USB scripté : répond aux écritures par des rapports d'entrée, journalise tout.</summary>
public sealed class ScriptedHid : IHidProvider, IHidDevice
{
    public ScriptedHid(HidDeviceInfo info) => Info = info;

    public HidDeviceInfo Info { get; }
    public List<byte[]> Writes { get; } = [];
    public List<byte[]> Features { get; } = [];
    public Queue<byte[]> Inputs { get; } = new();
    public Func<byte[], byte[]?>? OnWrite { get; set; }
    public Func<byte[], byte[]?>? OnGetFeature { get; set; }

    public IReadOnlyList<HidDeviceInfo> Enumerate() => [Info];
    public IHidDevice? Open(HidDeviceInfo info) => this;

    public int GetFeature(byte[] buffer, int length)
    {
        var r = OnGetFeature?.Invoke(buffer);
        if (r is null) return -1;
        r.CopyTo(buffer, 0);
        return r.Length;
    }

    public bool SetFeature(ReadOnlySpan<byte> data)
    {
        Features.Add(data.ToArray());
        return true;
    }

    public bool Write(ReadOnlySpan<byte> data)
    {
        var d = data.ToArray();
        Writes.Add(d);
        var resp = OnWrite?.Invoke(d);
        if (resp is not null) Inputs.Enqueue(resp);
        return true;
    }

    public int Read(byte[] buffer, int timeoutMs)
    {
        if (Inputs.Count == 0) return 0;
        var r = Inputs.Dequeue();
        r.CopyTo(buffer, 0);
        return r.Length;
    }

    public void Dispose() { }
}

public class BoardDriverTests
{
    private static readonly SystemInfo NoInfo = SystemInfo.Empty;

    private static byte[] Arr(int len, params (int idx, byte val)[] set)
    {
        var b = new byte[len];
        foreach (var (i, v) in set) b[i] = v;
        return b;
    }

    // ---------- ASUS Aura USB ----------

    [Fact]
    public void AsusAura_Mainboard_ConfigTableAndDirectPackets()
    {
        var info = new HidDeviceInfo("asus", 0x0B05, 0x19AF, 0xFF72, 0xA1, "AsusTek", "AURA LED Controller", "9876", 65, 65, 0);
        Assert.True(AsusAuraUsbController.IsCandidate(info));
        var hid = new ScriptedHid(info)
        {
            OnWrite = w => w[1] switch
            {
                0x82 => Arr(65, (0, 0xEC), (1, 0x02), (2, (byte)'A')),
                0xB0 => Arr(65, (0, 0xEC), (1, 0x30), (4 + 0x02, 2), (4 + 0x1B, 3)), // 2 connecteurs ARGB, 3 LEDs carte
                _ => null,
            },
        };
        var r = AsusAuraUsbController.Detect(hid, info, new SystemInfo { BoardManufacturer = "ASUSTeK COMPUTER INC.", BoardProduct = "ROG STRIX B650E-F GAMING WIFI" });
        Assert.Null(r.Problem);
        var c = r.Controller!;
        Assert.Equal("ASUS ROG STRIX B650E-F GAMING WIFI", c.Device.Name);
        Assert.Equal(["Carte mère", "ARGB 1", "ARGB 2"], c.Device.Zones.Select(z => z.Name));
        Assert.Contains(hid.Writes, w => w[0] == 0xEC && w[1] == 0x52 && w[2] == 0x53); // protocole Gen1

        var argb1 = c.Device.Zones[1];
        c.Configure(new Dictionary<string, int> { [ZoneKeys.Zone(c.Device.Key, 1, "ARGB 1")] = 45 });
        Assert.Equal(45, c.Device.Zones[1].LedCount);
        Assert.Contains(hid.Writes, w => w[1] == 0x35 && w[2] == 0 && w[5] == 0xFF); // mode direct carte (canal effet 0)
        Assert.Contains(hid.Writes, w => w[1] == 0x35 && w[2] == 1 && w[5] == 0xFF); // ARGB 1 (canal effet 1)

        hid.Writes.Clear();
        var colors = new Rgb[c.Device.LedCount];
        int start = c.Device.Zones[1].StartIndex;
        colors[start + 44] = new Rgb(7, 8, 9);
        c.Write(colors);
        var argbPackets = hid.Writes.Where(w => w[1] == 0x40 && (w[2] & 0x0F) == 0).ToList(); // canal direct 0 = ARGB 1
        Assert.Equal(3, argbPackets.Count);                       // 45 LEDs = 20 + 20 + 5
        Assert.Equal(0x80, argbPackets[2][2] & 0x80);             // dernier paquet : appliquer
        Assert.Equal(40, argbPackets[2][3]);                      // offset
        Assert.Equal(5, argbPackets[2][4]);
        Assert.Equal(new byte[] { 7, 8, 9 }, argbPackets[2][(5 + 4 * 3)..(5 + 5 * 3)]);
        Assert.Contains(hid.Writes, w => w[1] == 0x40 && (w[2] & 0x7F) == 0x04); // LEDs de la carte (canal 4)
    }

    [Fact]
    public void AsusAura_SilentDeviceIsRejected()
    {
        var info = new HidDeviceInfo("asus", 0x0B05, 0x1AA6, 1, 0, "", "", "", 65, 65, 0);
        var hid = new ScriptedHid(info);
        var r = AsusAuraUsbController.Detect(hid, info, NoInfo);
        Assert.Null(r.Controller);
        Assert.NotNull(r.Problem);
    }

    // ---------- ASRock Polychrome USB ----------

    [Fact]
    public void Polychrome_ZonesFromConfigAndChunkedDirect()
    {
        var info = new HidDeviceInfo("asrock", 0x26CE, 0x01A2, 0xFF00, 1, "ASRock", "Polychrome", "1", 65, 65, 0);
        // Zones présentes (bit à 1) : RGB1 (bit0), ARGB1 (bit2), IO (bit5).
        var hid = new ScriptedHid(info)
        {
            OnWrite = w => (w[1], w[3]) switch
            {
                (0x14, 0x02) => Arr(64, (4, 1), (5, 0x1E), (6, 20), (7, 0x1E), (8, 0x1E), (9, 6), (10, 0x1E), (11, 0x1E)),
                (0x14, 0x03) => Arr(64, (4, 0)),
                (0x14, 0x01) => Arr(64, (4, 0b0010_0101)),
                _ => new byte[64],
            },
        };
        var r = PolychromeUsbController.Detect(hid, info, NoInfo);
        Assert.Null(r.Problem);
        var c = r.Controller!;
        Assert.Equal(["RGB 1", "ARGB 1", "Cache I/O"], c.Device.Zones.Select(z => z.Name));
        Assert.Equal(1 + 20 + 6, c.Device.LedCount);
        Assert.True(c.Device.Zones[1].IsResizable);

        c.Configure(new Dictionary<string, int>());
        Assert.Contains(hid.Writes, w => w[1] == 0x15 && w[3] == 0x03 && w[4] == 0);           // RGSwap coupé
        Assert.Contains(hid.Writes, w => w[1] == 0x10 && w[3] == 2 && w[4] == 0x0F);            // ARGB1 en direct

        hid.Writes.Clear();
        var colors = new Rgb[c.Device.LedCount];
        colors[0] = new Rgb(1, 2, 3);
        c.Write(colors);
        Assert.Equal(0xE3, hid.Writes[0][4]);
        Assert.Equal(new byte[] { 2, 1, 3 }, hid.Writes[0][9..12]); // G, R, B
        Assert.All(hid.Writes.Skip(1), w => Assert.Equal(0xE4, w[4]));
        Assert.All(hid.Writes, w => Assert.Equal(0x65, w[0x40]));
        // (1 + 100 + 6 + 8) * 3 = 345 octets : 54 + 6 x 57 = 396 -> 7 paquets
        Assert.Equal(7, hid.Writes.Count);
    }

    // ---------- Gigabyte RGB Fusion 2 ----------

    [Fact]
    public void Fusion2_CalibratedStripOrderAndStaticBoardLeds()
    {
        var info = new HidDeviceInfo("gb", 0x048D, 0x5702, 0xFF89, 0xCC, "ITE", "GIGABYTE", "", 0, 0, 64);
        var report = new byte[64];
        report[0] = 0xCC;
        report[11] = 0x00;
        BitConverter.GetBytes(0x00010002u).CopyTo(report, 44);  // D_LED1 calibré GRB
        var hid = new ScriptedHid(info) { OnGetFeature = b => b[0] == 0xCC ? report : null };

        var r = Fusion2UsbController.Detect(hid, info, new SystemInfo { BoardManufacturer = "Gigabyte Technology Co., Ltd.", BoardProduct = "B550 AORUS ELITE" });
        Assert.Null(r.Problem);
        var c = r.Controller!;
        Assert.Equal("Gigabyte B550 AORUS ELITE", c.Device.Name);
        Assert.Contains(hid.Features, f => f[1] == 0x31 && f[2] == 0);   // "beat" désactivé
        Assert.Contains(hid.Features, f => f[1] == 0x28 && f[2] == 0xFF); // application rapide après remise à zéro

        c.Configure(new Dictionary<string, int>());
        Assert.Contains(hid.Features, f => f[1] == 0x34);                 // nombre de LEDs
        Assert.Contains(hid.Features, f => f[1] == 0x32 && f[2] == 0x03); // effets intégrés D_LED1/2 coupés

        hid.Features.Clear();
        var colors = new Rgb[c.Device.LedCount];
        colors[0] = new Rgb(1, 2, 3);                                      // D_LED1, LED 1
        int board1 = c.Device.Zones.First(z => z.Name == "LED carte 1").StartIndex;
        colors[board1] = new Rgb(0xAA, 0xBB, 0xCC);
        c.Write(colors);

        var strip = hid.Features.First(f => f[1] == 0x58);
        Assert.Equal(new byte[] { 2, 1, 3 }, strip[5..8]);                // ordre GRB
        Assert.Equal(19 * 3, strip[4]);
        var eff = hid.Features.First(f => f[1] == 0x20);
        Assert.Equal(1, eff[2]);                                           // zone0 = 1 << 0
        Assert.Equal(new byte[] { 0xCC, 0xBB, 0xAA }, eff[14..17]);       // couleur BGR
        Assert.Contains(hid.Features, f => f[1] == 0x28 && (f[2] & 1) == 1); // application de la zone

        hid.Features.Clear();
        c.Write(colors);
        Assert.DoesNotContain(hid.Features, f => f[1] == 0x20);           // LED carte inchangée : rien n'est renvoyé
    }

    [Fact]
    public void Registry_FindsEachBrandOncePerDevice()
    {
        var devices = new List<HidDeviceInfo>
        {
            new("a", 0x0B05, 0x18F3, 0xFF72, 0xA1, "", "", "1", 65, 65, 0),
            new("a2", 0x0B05, 0x18F3, 0x0001, 0x00, "", "", "1", 65, 65, 0),
            new("b", 0x26CE, 0x01A2, 0xFF00, 1, "", "", "2", 65, 65, 0),
            new("c", 0x048D, 0x8297, 0xFF89, 0xCC, "", "", "3", 0, 0, 64),
            new("d", 0x1462, 0x7D75, 0x0001, 0x00, "", "", "4", 0, 0, 185),
            new("kbd", 0x046D, 0xC33C, 1, 6, "", "", "", 9, 2, 0),
        };
        var found = DriverRegistry.Candidates(devices).Select(c => c.driver.Name).ToList();
        Assert.Equal(["ASUS Aura USB", "ASRock Polychrome USB", "Gigabyte RGB Fusion 2", "MSI Mystic Light"], found);
    }
}
