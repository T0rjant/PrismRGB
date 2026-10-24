// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using PrismRGB.Core;
using PrismRGB.Core.Config;
using PrismRGB.Core.Devices;
using PrismRGB.Core.Drivers.Msi;
using PrismRGB.Core.Native;
using PrismRGB.Core.Platform;

namespace PrismRGB.Tests;

public class MsiTests
{
    private static readonly SystemInfo B650 = new() { BoardManufacturer = "Micro-Star International Co., Ltd.", BoardProduct = "MAG B650 TOMAHAWK WIFI (MS-7D75)" };
    private static readonly SystemInfo X870 = new() { BoardManufacturer = "Micro-Star International Co., Ltd.", BoardProduct = "MAG X870 TOMAHAWK WIFI (MS-7E51)" };

    private static (FakeHidProvider hid, FakeMsiBoard board) Board185(ushort vid = 0x1462, ushort pid = 0x7D75, string serial = "A02024")
    {
        var hid = new FakeHidProvider();
        var b = new FakeMsiBoard(vid, pid, serial, 185, 725);
        hid.Boards.Add(b);
        return (hid, b);
    }

    private static (FakeHidProvider hid, FakeMsiBoard board) Board761()
    {
        var hid = new FakeHidProvider();
        var b = new FakeMsiBoard(0x0DB0, 0x0076, "7E51X", 64, 761);
        hid.Boards.Add(b);
        return (hid, b);
    }

    private static Dictionary<string, int> NoSizes() => [];

    [Fact]
    public void Detect185_B650Tomahawk_ZonesAndEnablePacket()
    {
        var (hid, board) = Board185();
        var r = MsiDetector.Detect(hid, board.Info, B650, allowUntested: false);
        Assert.Null(r.Problem);
        var c = Assert.IsType<Msi185Controller>(r.Controller);
        c.Configure(NoSizes());

        Assert.Equal("MSI MAG B650 TOMAHAWK WIFI (MS-7D75)", c.Device.Name);
        Assert.Equal(["JRGB1", "JRGB2", "JRAINBOW1", "JRAINBOW2"], c.Device.Zones.Select(z => z.Name));
        Assert.Equal(1 + 1 + 40 + 40, c.Device.LedCount);
        Assert.True(c.Device.Zones[2].IsResizable);
        Assert.True(c.SyncMode);

        var enable = board.SentWithId(0x52).Last();
        Assert.Equal(185, enable.Length);
        Assert.Equal(0x25, enable[74]);          // on_board_led.effect = mode direct
        Assert.Equal(0xA9, enable[78]);          // 0x29 | sync JRGB
        Assert.Equal(0xBF, enable[82]);          // synchro complète
        Assert.Equal(1, enable[31]);             // JRAINBOW1 statique en mode synchronisé
        Assert.Equal(40, enable[41]);            // 40 LEDs
        Assert.Equal(40, enable[52]);
        Assert.Equal(0x29, enable[57]);          // JCORSAIR fan_flags
        Assert.Equal(0x82, enable[61]);
        Assert.Equal(120, enable[63]);
        Assert.Equal(0x08, enable[5]);           // JRGB1 speed/brightness
        Assert.Equal(0x2A, enable[178]);         // JRGB2
        Assert.Equal(0, enable[184]);            // jamais de sauvegarde en flash
    }

    [Fact]
    public void Write185_SyncLayout()
    {
        var (hid, board) = Board185();
        var c = (Msi185Controller)MsiDetector.Detect(hid, board.Info, B650, false).Controller!;
        c.Configure(NoSizes());
        var colors = new Rgb[c.Device.LedCount];
        colors[0] = new Rgb(1, 2, 3);       // JRGB1
        colors[1] = new Rgb(4, 5, 6);       // JRGB2
        colors[2] = new Rgb(7, 8, 9);       // JRAINBOW1 LED 0
        colors[2 + 40] = new Rgb(10, 11, 12); // JRAINBOW2 LED 0
        c.Write(colors);

        var p = board.SentWithId(0x53).Last();
        Assert.Equal(725, p.Length);
        Assert.Equal(new byte[] { 0x53, 0x25, 0x06, 0x00, 0x00 }, p[..5]);
        // B650 Tomahawk : 0 LED intégrée, 0 pipe, 2 JRGB -> JRGB à 0 et 1, JRAINBOW1 à 2, JRAINBOW2 à 42.
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, p[5..14]);
        Assert.Equal(new byte[] { 10, 11, 12 }, p[(5 + 42 * 3)..(5 + 43 * 3)]);
    }

    [Fact]
    public void Write185_LongStripSwitchesToSeparatePackets()
    {
        var (hid, board) = Board185();
        var c = (Msi185Controller)MsiDetector.Detect(hid, board.Info, B650, false).Controller!;
        var key = ZoneKeys.Zone(c.Device.Key, 2, "JRAINBOW1");
        c.Configure(new Dictionary<string, int> { [key] = 60 });
        Assert.False(c.SyncMode);
        Assert.Equal(60, c.Device.Zones[2].LedCount);

        var enable = board.SentWithId(0x52).Last();
        Assert.Equal(0x25, enable[31]);
        Assert.Equal(200, enable[41]);
        Assert.Equal(0x25, enable[42]);
        Assert.Equal(240, enable[52]);
        Assert.Equal(0xB1, enable[82]);

        var colors = new Rgb[c.Device.LedCount];
        colors[2 + 59] = new Rgb(9, 9, 9);
        board.Sent.Clear();
        c.Write(colors);
        var sent = board.SentWithId(0x53).ToList();
        Assert.Equal(3, sent.Count);                          // JRAINBOW1, JRAINBOW2, puis LEDs de la carte
        Assert.Equal(new byte[] { 0x53, 0x25, 0x04, 0x00 }, sent[0][..4]);
        Assert.Equal(new byte[] { 0x53, 0x25, 0x04, 0x01 }, sent[1][..4]);
        Assert.Equal(new byte[] { 0x53, 0x25, 0x06, 0x00 }, sent[2][..4]);
        Assert.Equal(9, sent[0][5 + 59 * 3]);
    }

    [Fact]
    public void Detect185_CommonPidUsesSerial()
    {
        var (hid, board) = Board185(0x0DB0, 0x0076, "7D75ABCDEF");
        var r = MsiDetector.Detect(hid, board.Info, B650, false);
        var c = Assert.IsType<Msi185Controller>(r.Controller);
        Assert.Equal((ushort)0x7D75, c.Config.Pid);
    }

    [Fact]
    public void Detect185_UnknownPidIsRefused()
    {
        var (hid, board) = Board185(0x1462, 0x7B12);
        var r = MsiDetector.Detect(hid, board.Info, B650, true);
        Assert.Null(r.Controller);
        Assert.NotNull(r.Problem);
        Assert.Empty(board.SentWithId(0x52)); // rien n'a été écrit sur une carte inconnue
    }

    [Fact]
    public void Detect761_X870Tomahawk_SetupAndPackets()
    {
        var (hid, board) = Board761();
        var r = MsiDetector.Detect(hid, board.Info, X870, false);
        Assert.Null(r.Problem);
        var c = Assert.IsType<Msi761Controller>(r.Controller);
        Assert.Contains(board.Sent, p => p[0] == 0x51 && p.Length == 761 && p.Skip(1).All(b => b == 0)); // sonde d'activation

        var argb2 = c.Device.Zones.First(z => z.Name == "JARGB 2");
        c.Configure(new Dictionary<string, int> { [ZoneKeys.Zone(c.Device.Key, argb2.Index, argb2.Name)] = 54 });
        Assert.Equal(["JAF (ventilos MSI)", "JARGB 1", "JARGB 2", "JARGB 3"], c.Device.Zones.Select(z => z.Name));
        Assert.Equal(30 + 30 + 54 + 30, c.Device.LedCount);

        var setup = board.SentWithId(0x50).Last();
        Assert.Equal(290, setup.Length);
        Assert.Equal(0x09, setup[1]);
        Assert.Equal(30, setup[16]);     // JARGB 1 : dernier octet de la ligne 0
        Assert.Equal(54, setup[32]);     // JARGB 2
        Assert.Equal(30, setup[48]);     // JARGB 3
        Assert.Equal(30, setup[64]);     // JAF
        Assert.Equal(0x00, setup[65]);   // JPIPE1 désactivé sur cette carte
        Assert.Equal(0x00, setup[81]);   // lignes 4 à 16 éteintes (comme OpenRGB)...
        Assert.Equal(0x00, setup[95]);
        Assert.Equal(0x1E, setup[96]);   // ...sauf leur dernier octet

        var colors = new Rgb[c.Device.LedCount];
        colors[c.Device.Zones.First(z => z.Name == "JARGB 2").StartIndex + 53] = new Rgb(0xAA, 0xBB, 0xCC);
        board.Sent.Clear();
        c.Write(colors);
        var pk = board.SentWithId(0x51).ToList();
        Assert.Equal(4, pk.Count);
        Assert.Equal(new byte[] { 0x51, 0x09, 0x08, 0x00, 0x00, 0x00, 30 }, pk[0][..7]); // JAF
        Assert.Equal(new byte[] { 0x51, 0x09, 0x04, 0x00, 0x00, 0x00, 30 }, pk[1][..7]); // JARGB 1
        Assert.Equal(new byte[] { 0x51, 0x09, 0x04, 0x01, 0x00, 0x00, 54 }, pk[2][..7]); // JARGB 2
        Assert.Equal(new byte[] { 0x51, 0x09, 0x04, 0x02, 0x00, 0x00, 30 }, pk[3][..7]); // JARGB 3
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, pk[2][(7 + 53 * 3)..(7 + 54 * 3)]);
        Assert.Equal(727, pk[0].Length);
    }

    [Fact]
    public void Detect761_UntestedBoardNeedsOptIn()
    {
        var sys = new SystemInfo { BoardManufacturer = "Micro-Star International Co., Ltd.", BoardProduct = "MPG X970 FUTURE (MS-7F00)" };
        var (hid, board) = Board761();
        var refused = MsiDetector.Detect(hid, board.Info, sys, false);
        Assert.Null(refused.Controller);
        Assert.Contains("non testées", refused.Problem);

        var (hid2, board2) = Board761();
        var ok = MsiDetector.Detect(hid2, board2.Info, sys, true);
        Assert.NotNull(ok.Controller);
    }

    [Fact]
    public void Board761Table_EdgeTiHasIoShield()
    {
        var cfg = MsiBoards.Find761("MSI MPG X870E EDGE TI WIFI (MS-7E59)");
        Assert.NotNull(cfg);
        Assert.Equal(10, cfg!.Pipe1Leds);
        Assert.Contains(MsiZone.JPipe1, cfg.Zones);
    }

    private static async Task WaitFor(Func<bool> cond, int ms = 6000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("Condition non atteinte");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task NativeBackend_DetectsStreamsAndRecoversAfterUnplug()
    {
        var (hid, board) = Board185();
        hid.Others.Add(new Core.Hid.HidDeviceInfo("kbd", 0x046D, 0xC33C, 1, 6, "Logitech", "G512", "", 9, 2, 0));
        await using var backend = new NativeBackend(hid, () => B650, () => new Dictionary<string, int>(), () => false);
        await backend.StartAsync();
        await WaitFor(() => backend.Devices.Count == 1);
        Assert.Equal(BackendState.Connected, backend.State);
        var dev = backend.Devices[0];

        backend.SubmitFrame(dev, Enumerable.Repeat(new Rgb(50, 60, 70), dev.LedCount).ToArray());
        await WaitFor(() => board.SentWithId(0x53).Any(p => p[5] == 50));

        // Débranchement : le contrôleur est perdu puis retrouvé au rebranchement.
        board.Unplugged = true;
        backend.SubmitFrame(dev, new Rgb[dev.LedCount]);
        await WaitFor(() => backend.Devices.Count == 0, 10000);
        board.Unplugged = false;
        await WaitFor(() => backend.Devices.Count == 1, 10000);
        Assert.True(board.Opens >= 2);
    }

    [Fact]
    public void Scanner_ReportsBoardRamGpuAndUsb()
    {
        var sys = new SystemInfo
        {
            BoardManufacturer = "Micro-Star International Co., Ltd.",
            BoardProduct = "MAG B650 TOMAHAWK WIFI (MS-7D75)",
            Ram = [new RamModule("DIMM A2", "Kingston", "KF560C36BBEAK2-32", 17179869184, 6000)],
            Gpus = [new GpuInfo("NVIDIA GeForce RTX 5070", "PCI\\VEN_10DE&DEV_2F04&SUBSYS_13041462", "MSI")],
        };
        var hid = new List<Core.Hid.HidDeviceInfo>
        {
            new("p", 0x1B1C, 0x0C1C, 0xFF42, 1, "Corsair", "iCUE COMMANDER CORE", "", 0, 0, 0),
        };
        var entries = HardwareScanner.Scan(sys, hid, [("MSI MAG B650 TOMAHAWK WIFI (MS-7D75)", null)]);
        Assert.Contains(entries, e => e.Category == "Carte mère" && e.Status == ScanStatus.Controlled);
        Assert.Contains(entries, e => e.Category == "RAM" && e.Status == ScanStatus.NeedsLowLevel);
        Assert.Contains(entries, e => e.Category == "Carte graphique" && e.Detail.Contains("MSI"));
        Assert.Contains(entries, e => e.Category == "USB" && e.Name.Contains("COMMANDER"));
    }
}
