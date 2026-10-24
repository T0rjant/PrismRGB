// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using PrismRGB.Core.Smbus;

namespace PrismRGB.Tests;

/// <summary>Appareil simulé sur le bus SMBus.</summary>
public abstract class FakeSmbusDevice
{
    public virtual int ReadByte() => 0;
    public virtual int ReadByteData(byte cmd) => 0;
    public virtual int ReadWordData(byte cmd) => 0;
    public virtual bool WriteByte(byte value) => true;
    public virtual bool WriteByteData(byte cmd, byte value) => true;
    public virtual bool WriteWordData(byte cmd, ushort value) => true;
    public virtual bool WriteBlock(byte cmd, byte[] data) => true;
}

/// <summary>Bus SMBus simulé : routage par adresse et journal de toutes les écritures.</summary>
public sealed class FakeSmbus : ISmbus
{
    public Dictionary<byte, FakeSmbusDevice> Devices { get; } = [];
    public List<(byte addr, string op, int cmd, int value)> Writes { get; } = [];
    /// <summary>Résolution dynamique (appareils qui changent d'adresse, comme les barrettes ENE).</summary>
    public Func<byte, FakeSmbusDevice?>? Resolver { get; set; }
    public int Ddr4Page { get; private set; }

    public string Name => "SMBus simulé";

    private FakeSmbusDevice? At(byte a) => Resolver?.Invoke(a) ?? Devices.GetValueOrDefault(a);

    public int ReadByte(byte address) => At(address)?.ReadByte() ?? -1;
    public int ReadByteData(byte address, byte command) => At(address)?.ReadByteData(command) ?? -1;
    public int ReadWordData(byte address, byte command) => At(address)?.ReadWordData(command) ?? -1;

    public bool WriteByte(byte address, byte value)
    {
        Writes.Add((address, "byte", value, 0));
        if (address is 0x36 or 0x37) { Ddr4Page = address - 0x36; return true; }
        return At(address)?.WriteByte(value) ?? false;
    }

    public bool WriteByteData(byte address, byte command, byte value)
    {
        Writes.Add((address, "byteData", command, value));
        return At(address)?.WriteByteData(command, value) ?? false;
    }

    public bool WriteWordData(byte address, byte command, ushort value)
    {
        Writes.Add((address, "word", command, value));
        return At(address)?.WriteWordData(command, value) ?? false;
    }

    public bool WriteBlockData(byte address, byte command, ReadOnlySpan<byte> data)
    {
        Writes.Add((address, "block", command, data.Length));
        return At(address)?.WriteBlock(command, data.ToArray()) ?? false;
    }

    public void Dispose() { }
}

/// <summary>Hub SPD DDR5 (SPD5118) : registres 0x00/0x01, page MR11, EEPROM paginée lue en 0x80+.</summary>
public sealed class FakeDdr5Hub : FakeSmbusDevice
{
    public byte[] Eeprom { get; } = new byte[1024];
    public int Page { get; private set; }
    public int EepromWrites { get; private set; }

    public FakeDdr5Hub(ushort jedec, string part, byte variant = 0, byte model = 0)
    {
        Eeprom[2] = 18; // DDR5
        // jedec = (b0 << 8) + (b1 & 0x7F) - 1
        int v = jedec + 1;
        Eeprom[0x200] = (byte)(v >> 8);
        Eeprom[0x201] = (byte)(0x80 | (v & 0x7F));
        for (int i = 0; i < part.Length && i < 30; i++) Eeprom[0x209 + i] = (byte)part[i];
        Eeprom[0x22B + 0x18] = variant;
        Eeprom[0x22B + 0x19] = model;
    }

    public override int ReadByteData(byte cmd) => cmd switch
    {
        0x00 => 0x51,
        0x01 => 0x08,
        0x0B => Page,
        >= 0x80 => Eeprom[Page * 128 + (cmd & 0x7F)],
        _ => 0,
    };

    public override bool WriteByteData(byte cmd, byte value)
    {
        if (cmd == 0x0B) { Page = value; return true; }
        if (cmd >= 0x80) EepromWrites++;
        return true;
    }
}
