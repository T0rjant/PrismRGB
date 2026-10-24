// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using PrismRGB.Core.Devices;
using PrismRGB.Core.Native;
using PrismRGB.Core.Smbus;

namespace PrismRGB.Core.Drivers.Dram;

/// <summary>
/// Corsair Vengeance RGB / Dominator Platinum / Titanium (DDR4 et DDR5).
/// Porté depuis OpenRGB CorsairDRAMController (GPL-2.0-or-later).
/// Protocole 4+ : paquet direct [nb LEDs, R, G, B..., CRC-8] envoyé en blocs sur 0x31 puis 0x32.
/// Protocoles plus anciens : tampon binaire + somme de contrôle vérifiée par la barrette, puis application.
/// </summary>
public sealed class CorsairDramController : INativeController
{
    private const byte RegResetBuffer = 0x0B, RegSetBinaryData = 0x20, RegBinaryStart = 0x21, RegStatus = 0x30;
    private const byte RegColorBlock1 = 0x31, RegColorBlock2 = 0x32, RegGetBinaryData = 0x40, RegGetChecksum = 0x42;
    private const byte RegGetDeviceInfo = 0x61, RegWriteConfiguration = 0x82, IdColorData = 2;

    private sealed record Model(string Name, ushort[] Pids, int Leds, bool Reverse);

    private static readonly Model[] Models =
    [
        new("Corsair Vengeance RGB Pro DDR4", [0x0100, 0x0101], 10, false),
        new("Corsair Dominator Platinum RGB DDR4", [0x0200, 0x0201], 12, true),
        new("Corsair Vengeance RGB Pro SL DDR4", [0x0300, 0x0301], 10, false),
        new("Corsair Vengeance RGB RS DDR4", [0x0400, 0x0401], 6, false),
        new("Corsair Dominator Platinum RGB DDR5", [0x0600, 0x0601], 12, true),
        new("Corsair Dominator Titanium RGB DDR5", [0x0800, 0x0801, 0x0810, 0x0811], 12, true),
        new("Corsair Vengeance RGB DDR5", [0x0700, 0x0701, 0x0900, 0x0901, 0x0910, 0x0911], 10, false),
        new("Corsair Vengeance Shugo Series DDR5", [0x0A00, 0x0A01, 0x0A10, 0x0A11], 10, false),
        new("Corsair Vengeance RGB RS DDR5", [0x0B00, 0x0B01], 6, false),
    ];

    private readonly ISmbus _bus;
    private readonly byte _addr;
    private readonly Model _model;
    private readonly int _protocol;

    private CorsairDramController(ISmbus bus, byte addr, Model model, int protocol, string firmware)
    {
        _bus = bus;
        _addr = addr;
        _model = model;
        _protocol = protocol;
        Device = new RgbDevice
        {
            Key = $"corsairdram:{addr:X2}",
            BackendIndex = addr,
            Name = model.Name,
            Vendor = "Corsair",
            Description = $"Corsair DRAM, protocole {protocol} (SMBus)",
            Location = $"{bus.Name}, 0x{addr:X2}",
            Firmware = firmware,
            Kind = DeviceKind.Dram,
            Zones = [new RgbZone { Index = 0, Name = "Barrette", Kind = ZoneKind.Linear, StartIndex = 0, LedCount = model.Leds, MinLeds = model.Leds, MaxLeds = model.Leds }],
            LedCount = model.Leds,
            HasDirectMode = true,
        };
    }

    public RgbDevice Device { get; }

    /// <summary>CRC-8 (polynôme 0x07, init 0), identique à CRCpp CRC_8 utilisé par OpenRGB.</summary>
    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
        }
        return crc;
    }

    private static bool HasSignature(ISmbus bus, byte addr)
    {
        int r = bus.ReadByteData(addr, 0x43);
        if (r is not (0x1A or 0x1B or 0x1C)) return false;
        r = bus.ReadByteData(addr, 0x44);
        return r is 0x01 or 0x03 or 0x04;
    }

    public static List<CorsairDramController> Detect(ISmbus bus)
    {
        var found = new List<CorsairDramController>();
        var addresses = Enumerable.Range(0x58, 8).Concat(Enumerable.Range(0x18, 8)).Select(a => (byte)a);
        foreach (var addr in addresses)
        {
            if (HasSignature(bus, addr))
            {
                var c = Create(bus, addr);
                if (c is not null) found.Add(c);
            }
            Thread.Sleep(10);
        }
        return found;
    }

    private static CorsairDramController? Create(ISmbus bus, byte addr)
    {
        var info = new byte[32];
        bus.WriteByteData(addr, RegGetDeviceInfo, 0x00);
        bus.WriteByteData(addr, RegBinaryStart, 0x00);
        for (int i = 0; i < 32; i++) info[i] = (byte)Math.Max(0, bus.ReadByteData(addr, RegGetBinaryData));
        if (bus.ReadByteData(addr, RegGetChecksum) != Crc8(info)) return null;
        ushort pid = (ushort)((info[3] << 8) | info[2]);
        var model = Models.FirstOrDefault(m => m.Pids.Contains(pid));
        if (model is null) return null; // modèle inconnu : on ne devine pas son nombre de LEDs
        string fw = $"{info[9]}.{info[8]}.{(info[11] << 8) | info[10]}";
        return new CorsairDramController(bus, addr, model, info[28], fw);
    }

    public void Configure(IReadOnlyDictionary<string, int> zoneSizes) { }

    public void Write(ReadOnlySpan<Rgb> colors)
    {
        int n = _model.Leds;
        if (colors.Length != n) return;
        var ordered = new Rgb[n];
        for (int i = 0; i < n; i++) ordered[i] = colors[_model.Reverse ? n - 1 - i : i];
        Rgb At(int i) => ordered[i];

        if (_protocol >= 4)
        {
            var packet = new byte[n * 3 + 2];
            packet[0] = (byte)n;
            for (int i = 0; i < n; i++)
            {
                var c = At(i);
                packet[1 + i * 3] = c.R;
                packet[2 + i * 3] = c.G;
                packet[3 + i * 3] = c.B;
            }
            packet[^1] = Crc8(packet.AsSpan(0, packet.Length - 1));
            if (!_bus.WriteBlockData(_addr, RegColorBlock1, packet.AsSpan(0, Math.Min(32, packet.Length))))
                throw new IOException($"Barrette Corsair 0x{_addr:X2} ne répond plus");
            if (packet.Length > 32) _bus.WriteBlockData(_addr, RegColorBlock2, packet.AsSpan(32));
            return;
        }

        // Ancien protocole : tampon binaire vérifié par CRC avant application.
        var data = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            var c = At(i);
            data[i * 4] = c.R;
            data[i * 4 + 1] = c.G;
            data[i * 4 + 2] = c.B;
            data[i * 4 + 3] = 0xFF;
        }
        _bus.WriteByteData(_addr, RegResetBuffer, 0x00);
        _bus.WriteByteData(_addr, RegBinaryStart, 0x00);
        foreach (var b in data) _bus.WriteByteData(_addr, RegSetBinaryData, b);
        if (_bus.ReadByteData(_addr, RegGetChecksum) == Crc8(data))
        {
            _bus.WriteByteData(_addr, RegWriteConfiguration, IdColorData);
            for (int retry = 0; retry < 5; retry++)
            {
                int status = _bus.ReadByteData(_addr, RegStatus);
                if (status >= 0 && (status & 0x08) == 0) break;
                Thread.Sleep(10);
            }
        }
    }

    public void Dispose() { }
}
