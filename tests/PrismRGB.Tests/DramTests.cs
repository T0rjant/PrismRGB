// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using PrismRGB.Core;
using PrismRGB.Core.Drivers.Dram;
using PrismRGB.Core.Smbus;

namespace PrismRGB.Tests;

public class DramTests
{
    [Fact]
    public void PawnIoModules_AreEmbedded()
    {
        var names = typeof(SpdReader).Assembly.GetManifestResourceNames();
        Assert.Contains(names, n => n.EndsWith(".SmbusPIIX4.bin"));
        Assert.Contains(names, n => n.EndsWith(".SmbusI801.bin"));
    }

    // ---------- Kingston FURY ----------

    private sealed class FakeFury : FakeSmbusDevice
    {
        public readonly Dictionary<byte, byte> Regs = [];
        public int Begins, Ends;
        private readonly byte _model;
        public FakeFury(byte model) => _model = model;

        public override int ReadWordData(byte cmd) => cmd switch
        {
            1 => 'F' << 8, 2 => 'U' << 8, 3 => 'R' << 8, 4 => 'Y' << 8,
            0x06 => _model << 8,
            _ => Regs.GetValueOrDefault(cmd) << 8,
        };

        public override bool WriteByteData(byte cmd, byte value)
        {
            if (cmd == 0x08 && value == 0x53) Begins++;
            if (cmd == 0x08 && value == 0x44) Ends++;
            Regs[cmd] = value;
            return true;
        }
    }

    [Fact]
    public void SpdReader_ReadsDdr5KingstonAndRestoresPage0()
    {
        var bus = new FakeSmbus();
        var hub = new FakeDdr5Hub(Jedec.Kingston, "KF560C36BBEAK2-32", 0x00, 0x15);
        bus.Devices[0x51] = hub;
        var slots = SpdReader.ReadAll(new SafeSmbus(bus));
        var slot = Assert.Single(slots);
        Assert.Equal(1, slot.Index);
        Assert.Equal(DramType.Ddr5, slot.Type);
        Assert.Equal(Jedec.Kingston, slot.JedecId);
        Assert.Equal("KF560C36BBEAK2-32", slot.PartNumber);
        Assert.Equal(0x15, slot.ManufacturerData[0x19]);
        Assert.Equal(0, hub.Page);           // page 0 restaurée
        Assert.Equal(0, hub.EepromWrites);   // jamais d'écriture dans l'EEPROM
    }

    [Fact]
    public void SafeSmbus_BlocksSpdWritesExceptPageSelect()
    {
        var bus = new FakeSmbus();
        bus.Devices[0x50] = new FakeDdr5Hub(Jedec.Kingston, "X");
        var safe = new SafeSmbus(bus);
        Assert.True(safe.WriteByteData(0x50, 0x0B, 2));        // MR11 : autorisé
        Assert.False(safe.WriteByteData(0x50, 0x0B, 0x40));    // valeur de page aberrante : refusé
        Assert.False(safe.WriteByteData(0x50, 0x80, 0xFF));    // EEPROM : refusé
        Assert.False(safe.WriteWordData(0x53, 0x00, 1));
        Assert.False(safe.WriteBlockData(0x57, 0x00, [1, 2]));
        Assert.False(safe.WriteByte(0x52, 0));
        Assert.DoesNotContain(bus.Writes, w => w.addr == 0x50 && w.cmd == 0x80);
    }

    [Fact]
    public void KingstonFury_DetectsAndWritesDirectColors()
    {
        var bus = new FakeSmbus();
        bus.Devices[0x50] = new FakeDdr5Hub(Jedec.Kingston, "KF560C36BBEA-16", 0x00, 0x15);
        bus.Devices[0x52] = new FakeDdr5Hub(Jedec.Kingston, "KF560C36BBEA-16", 0x00, 0x15);
        var s0 = new FakeFury(0x15);
        var s2 = new FakeFury(0x15);
        bus.Devices[0x60] = s0;
        bus.Devices[0x62] = s2;
        var safe = new SafeSmbus(bus);

        var found = DramDetector.Detect(safe, SpdReader.ReadAll(safe), []);
        var k = Assert.IsType<KingstonFuryController>(Assert.Single(found));
        Assert.Equal(24, k.Device.LedCount);                 // 2 barrettes x 12 LEDs DDR5
        Assert.Equal(2, k.Device.Zones.Count);

        k.Configure(new Dictionary<string, int>());
        var colors = new Rgb[24];
        colors[0] = new Rgb(10, 20, 30);                     // barrette 1, LED 1
        colors[12 + 11] = new Rgb(40, 50, 60);               // barrette 2, LED 12
        k.Write(colors);

        Assert.Equal(0x10, s0.Regs[0x09]);                   // mode direct
        Assert.Equal(10, s0.Regs[0x50]);
        Assert.Equal(20, s0.Regs[0x51]);
        Assert.Equal(30, s0.Regs[0x52]);
        Assert.Equal(40, s2.Regs[(byte)(0x50 + 33)]);
        Assert.Equal(60, s2.Regs[(byte)(0x52 + 33)]);
        Assert.Equal(2, s2.Regs[0x27]);                      // nombre de barrettes
        Assert.True(s0.Ends >= s0.Begins - 0 && s0.Ends > 0);
    }

    [Fact]
    public void KingstonFury_NotProbedWithoutKingstonModules()
    {
        var bus = new FakeSmbus();
        bus.Devices[0x50] = new FakeDdr5Hub(Jedec.Corsair, "CMH32GX5M2B6000C30");
        bus.Devices[0x60] = new FakeFury(0x15);
        var safe = new SafeSmbus(bus);
        DramDetector.Detect(safe, SpdReader.ReadAll(safe), []);
        Assert.DoesNotContain(bus.Writes, w => w.addr == 0x60); // aucune écriture "à l'aveugle"
    }

    [Fact]
    public void KingstonFury_WindowsFallbackWhenSpdVendorUnreadable()
    {
        var bus = new FakeSmbus();
        bus.Devices[0x50] = new FakeDdr5Hub(0x0000, "");     // SPD protégé : fabricant illisible
        var fury = new FakeFury(0x15);
        bus.Devices[0x60] = fury;
        var safe = new SafeSmbus(bus);
        var found = DramDetector.Detect(safe, SpdReader.ReadAll(safe), ["Kingston"]);
        Assert.IsType<KingstonFuryController>(Assert.Single(found));
    }

    // ---------- ENE (G.Skill, TeamGroup...) ----------

    private sealed class EneStick : FakeSmbusDevice
    {
        public int Slot;
        public byte Address = 0x77;
        public static int SelectedSlot = -1;
        private ushort _ptr;
        public readonly Dictionary<ushort, byte> Regs = [];
        public readonly List<byte> DirectBytes = [];

        public EneStick(int slot, string version, byte leds)
        {
            Slot = slot;
            for (int i = 0; i < version.Length; i++) Regs[(ushort)(0x1000 + i)] = (byte)version[i];
            Regs[0x1C02] = leds;
        }

        public override int ReadByteData(byte cmd)
        {
            if (cmd is >= 0xA0 and < 0xB0) return cmd - 0xA0;
            if (cmd == 0x81) return Regs.GetValueOrDefault(_ptr);
            return 0;
        }

        public override bool WriteWordData(byte cmd, ushort value)
        {
            if (cmd == 0x00) _ptr = (ushort)(((value << 8) & 0xFF00) | ((value >> 8) & 0xFF));
            return true;
        }

        public override bool WriteByteData(byte cmd, byte value)
        {
            if (cmd != 0x01) return true;
            if (_ptr == 0x80F8) SelectedSlot = value;
            else if (_ptr == 0x80F9 && SelectedSlot == Slot && Address == 0x77) Address = (byte)(value >> 1);
            else Regs[_ptr] = value;
            return true;
        }

        public override bool WriteBlock(byte cmd, byte[] data)
        {
            if (cmd == 0x03 && _ptr >= 0x8000 && _ptr < 0x8100) DirectBytes.AddRange(data);
            return true;
        }
    }

    [Fact]
    public void Ene_RemapsSticksFrom0x77AndWritesRbgOrder()
    {
        var sticks = new[] { new EneStick(1, "DIMM_LED-0103", 8), new EneStick(3, "DIMM_LED-0103", 8) };
        EneStick.SelectedSlot = -1;
        var bus = new FakeSmbus { Resolver = a => sticks.FirstOrDefault(s => s.Address == a) };
        var found = EneDramController.Detect(bus);
        Assert.Equal(2, found.Count);
        Assert.Equal(new byte[] { 0x71, 0x73 }, sticks.Select(s => s.Address).ToArray());

        var c = found[0];
        c.Configure(new Dictionary<string, int>());
        Assert.Equal(1, sticks[0].Regs[0x8020]);   // accès direct activé
        var colors = new Rgb[8];
        colors[0] = new Rgb(1, 2, 3);
        c.Write(colors);
        Assert.Equal(24, sticks[0].DirectBytes.Count);
        Assert.Equal(new byte[] { 1, 3, 2 }, sticks[0].DirectBytes.Take(3).ToArray()); // R, B, G
    }

    [Fact]
    public void Ene_NoRemapWhen0x77IsNotEne()
    {
        var other = new GenericDevice();
        var bus = new FakeSmbus();
        bus.Devices[0x77] = other;
        var found = EneDramController.Detect(bus);
        Assert.Empty(found);
        Assert.DoesNotContain(bus.Writes, w => w.addr == 0x77);
    }

    private sealed class GenericDevice : FakeSmbusDevice
    {
        public override int ReadByteData(byte cmd) => 0x42;
    }

    // ---------- Corsair ----------

    private sealed class FakeCorsair : FakeSmbusDevice
    {
        private readonly byte[] _info = new byte[32];
        private int _read;
        public readonly List<byte[]> Blocks = [];

        public FakeCorsair(ushort pid, byte protocol)
        {
            _info[2] = (byte)pid;
            _info[3] = (byte)(pid >> 8);
            _info[28] = protocol;
        }

        public override int ReadByteData(byte cmd) => cmd switch
        {
            0x43 => 0x1B,
            0x44 => 0x04,
            0x40 => _info[_read++ % 32],
            0x42 => CorsairDramController.Crc8(_info),
            0x30 => 0,
            _ => 0,
        };

        public override bool WriteByteData(byte cmd, byte value)
        {
            if (cmd == 0x21) _read = 0;
            return true;
        }

        public override bool WriteBlock(byte cmd, byte[] data)
        {
            Blocks.Add([cmd, .. data]);
            return true;
        }
    }

    [Fact]
    public void Crc8_MatchesCrc8Smbus()
    {
        Assert.Equal(0xF4, CorsairDramController.Crc8("123456789"u8));
    }

    [Fact]
    public void Corsair_VengeanceDdr5_DirectPacketWithCrc()
    {
        var bus = new FakeSmbus();
        var stick = new FakeCorsair(0x0700, 4);
        bus.Devices[0x5A] = stick;
        var c = Assert.Single(CorsairDramController.Detect(bus));
        Assert.Equal("Corsair Vengeance RGB DDR5", c.Device.Name);
        Assert.Equal(10, c.Device.LedCount);

        var colors = new Rgb[10];
        colors[0] = new Rgb(9, 8, 7);
        c.Write(colors);
        var b1 = Assert.Single(stick.Blocks);        // 10 LEDs : 32 octets, un seul bloc
        Assert.Equal(0x31, b1[0]);
        var packet = b1[1..];
        Assert.Equal(32, packet.Length);
        Assert.Equal(10, packet[0]);                 // nombre de LEDs
        Assert.Equal(new byte[] { 9, 8, 7 }, packet[1..4]);
        Assert.Equal(CorsairDramController.Crc8(packet.AsSpan(0, 31)), packet[31]);
    }

    [Fact]
    public void Corsair_DominatorDdr5_TwoBlocksAndReversedOrder()
    {
        var bus = new FakeSmbus();
        var stick = new FakeCorsair(0x0600, 4);
        bus.Devices[0x18] = stick;
        var c = Assert.Single(CorsairDramController.Detect(bus));
        Assert.Equal(12, c.Device.LedCount);
        var colors = new Rgb[12];
        colors[0] = new Rgb(1, 2, 3);
        c.Write(colors);
        Assert.Equal(2, stick.Blocks.Count);
        Assert.Equal(0x32, stick.Blocks[1][0]);
        var packet = stick.Blocks[0][1..].Concat(stick.Blocks[1][1..]).ToArray();
        Assert.Equal(38, packet.Length);
        Assert.Equal(new byte[] { 1, 2, 3 }, packet[(1 + 11 * 3)..(1 + 12 * 3)]); // LED 0 envoyée en dernier
        Assert.Equal(CorsairDramController.Crc8(packet.AsSpan(0, 37)), packet[37]);
    }

    [Fact]
    public void Corsair_UnknownModelIsIgnored()
    {
        var bus = new FakeSmbus();
        bus.Devices[0x58] = new FakeCorsair(0x7777, 4);
        Assert.Empty(CorsairDramController.Detect(bus));
    }
}
