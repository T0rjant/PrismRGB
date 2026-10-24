// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;

namespace PrismRGB.Core.Smbus;

/// <summary>
/// SMBus via les modules PawnIO officiels : SmbusPIIX4 (AMD) ou SmbusI801 (Intel).
/// Chaque transfert prend le mutex système partagé "Access_SMBUS.HTP.Method" utilisé par les outils
/// de monitoring (HWiNFO, LibreHardwareMonitor...) pour ne jamais se marcher dessus sur le bus.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PawnIoSmbus : ISmbus
{
    private const int Quick = 0, Byte = 1, ByteData = 2, WordData = 3, BlockData = 5;
    private const ulong Read = 1, Write = 0;

    private readonly PawnIo _io;
    private readonly Mutex _busMutex;

    private PawnIoSmbus(PawnIo io, string name)
    {
        _io = io;
        Name = name;
        _busMutex = new Mutex(false, @"Global\Access_SMBUS.HTP.Method");
    }

    public string Name { get; }

    public static PawnIoSmbus? Open(out string? error)
    {
        error = null;
        foreach (var (module, label) in new[] { ("SmbusPIIX4.bin", "AMD (PIIX4)"), ("SmbusI801.bin", "Intel (i801)") })
        {
            var io = PawnIo.Load(module, out var e);
            if (io is not null) return new PawnIoSmbus(io, $"SMBus {label}");
            error = e;
        }
        return null;
    }

    private bool Xfer(byte address, ulong rw, byte command, int protocol, ReadOnlySpan<ulong> data, Span<ulong> output)
    {
        Span<ulong> input = stackalloc ulong[4 + data.Length];
        input[0] = address;
        input[1] = rw;
        input[2] = command;
        input[3] = (ulong)protocol;
        data.CopyTo(input[4..]);

        bool locked = false;
        try
        {
            try { locked = _busMutex.WaitOne(200); }
            catch (AbandonedMutexException) { locked = true; }
            if (!locked) return false;
            return _io.Execute("ioctl_smbus_xfer", input, output, out _);
        }
        finally
        {
            if (locked) _busMutex.ReleaseMutex();
        }
    }

    public int ReadByte(byte address)
    {
        Span<ulong> o = stackalloc ulong[1];
        return Xfer(address, Read, 0, Byte, [], o) ? (int)(o[0] & 0xFF) : -1;
    }

    public int ReadByteData(byte address, byte command)
    {
        Span<ulong> o = stackalloc ulong[1];
        return Xfer(address, Read, command, ByteData, [], o) ? (int)(o[0] & 0xFF) : -1;
    }

    public int ReadWordData(byte address, byte command)
    {
        Span<ulong> o = stackalloc ulong[1];
        return Xfer(address, Read, command, WordData, [], o) ? (int)(o[0] & 0xFFFF) : -1;
    }

    public bool WriteByte(byte address, byte value) =>
        Xfer(address, Write, value, Byte, [0], []);

    public bool WriteByteData(byte address, byte command, byte value) =>
        Xfer(address, Write, command, ByteData, [value], []);

    public bool WriteWordData(byte address, byte command, ushort value) =>
        Xfer(address, Write, command, WordData, [value], []);

    public bool WriteBlockData(byte address, byte command, ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > 32) return false;
        // Le module attend 5 cellules de 64 bits : longueur puis données, octets empaquetés en little-endian.
        Span<ulong> cells = stackalloc ulong[5];
        Span<byte> packed = stackalloc byte[40];
        packed[0] = (byte)data.Length;
        data.CopyTo(packed[1..]);
        for (int i = 0; i < 5; i++)
            cells[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(packed.Slice(i * 8, 8));
        return Xfer(address, Write, command, BlockData, cells, []);
    }

    public void Dispose()
    {
        _io.Dispose();
        _busMutex.Dispose();
    }
}
