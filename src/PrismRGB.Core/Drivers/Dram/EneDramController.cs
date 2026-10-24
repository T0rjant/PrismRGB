// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using PrismRGB.Core.Devices;
using PrismRGB.Core.Native;
using PrismRGB.Core.Smbus;

namespace PrismRGB.Core.Drivers.Dram;

/// <summary>
/// Barrettes à puce ENE (G.Skill Trident Z, TeamGroup T-Force, ADATA XPG, Thermaltake...).
/// Porté depuis OpenRGB ENESMBusController (GPL-2.0-or-later).
/// Les registres 16 bits sont adressés via un pointeur (écriture mot sur la commande 0x00),
/// lus sur 0x81 et écrits sur 0x01 (octet) ou 0x03 (bloc).
/// </summary>
public sealed class EneDramController : INativeController
{
    private const ushort RegDeviceName = 0x1000, RegMicronCheck = 0x1030, RegConfigTable = 0x1C00;
    private const ushort RegColorsDirect = 0x8000, RegDirect = 0x8020, RegApply = 0x80A0;
    private const ushort RegSlotIndex = 0x80F8, RegI2cAddress = 0x80F9, RegColorsDirectV2 = 0x8100;
    private const int MaxBlock = 3;

    /// <summary>Adresses utilisées par OpenRGB pour réadresser les barrettes (toutes démarrent en 0x77).</summary>
    public static readonly byte[] RamAddresses = [0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x4F, 0x66, 0x67, 0x39, 0x3A, 0x3B, 0x3C, 0x3D];

    private readonly ISmbus _bus;
    private readonly byte _addr;
    private readonly ushort _directReg;
    private readonly int _ledCount;
    private readonly byte[] _buffer;

    private EneDramController(ISmbus bus, byte addr, string version, ushort directReg, int ledCount, int index)
    {
        _bus = bus;
        _addr = addr;
        _directReg = directReg;
        _ledCount = ledCount;
        _buffer = new byte[ledCount * 3];
        Device = new RgbDevice
        {
            Key = $"enedram:{addr:X2}",
            BackendIndex = addr,
            Name = $"RAM RGB (ENE) n°{index}",
            Vendor = "ENE",
            Description = $"Contrôleur ENE {version} (SMBus)",
            Location = $"{bus.Name}, 0x{addr:X2}",
            Firmware = version,
            Kind = DeviceKind.Dram,
            Zones = [new RgbZone { Index = 0, Name = "Barrette", Kind = ZoneKind.Linear, StartIndex = 0, LedCount = ledCount, MinLeds = ledCount, MaxLeds = ledCount }],
            LedCount = ledCount,
            HasDirectMode = true,
        };
    }

    public RgbDevice Device { get; }

    private static int RegRead(ISmbus bus, byte dev, ushort reg)
    {
        bus.WriteWordData(dev, 0x00, (ushort)(((reg << 8) & 0xFF00) | ((reg >> 8) & 0x00FF)));
        return bus.ReadByteData(dev, 0x81);
    }

    private static void RegWrite(ISmbus bus, byte dev, ushort reg, byte val)
    {
        bus.WriteWordData(dev, 0x00, (ushort)(((reg << 8) & 0xFF00) | ((reg >> 8) & 0x00FF)));
        bus.WriteByteData(dev, 0x01, val);
    }

    /// <summary>Signature ENE : les registres 0xA0 à 0xAF renvoient 0x00 à 0x0F (lecture seule).</summary>
    public static bool HasEneSignature(ISmbus bus, byte addr)
    {
        int res = bus.ReadByte(addr);
        if (res < 0) res = bus.ReadByteData(addr, 0x00);
        if (res < 0) return false;
        for (int i = 0xA0; i < 0xB0; i++)
            if (bus.ReadByteData(addr, (byte)i) != i - 0xA0) return false;
        return true;
    }

    private static bool IsMicron(ISmbus bus, byte addr)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 16; i++)
        {
            int c = RegRead(bus, addr, (ushort)(RegMicronCheck + i));
            if (c <= 0) break;
            sb.Append((char)c);
        }
        return sb.ToString() == "Micron";
    }

    /// <summary>
    /// Détection DRAM ENE. Les barrettes non configurées répondent toutes en 0x77 : on les réadresse une par une
    /// (comme OpenRGB), mais seulement si l'appareil en 0x77 porte bien la signature ENE.
    /// </summary>
    public static List<EneDramController> Detect(ISmbus bus)
    {
        if (bus.ReadByte(0x77) >= 0 && HasEneSignature(bus, 0x77))
        {
            int listIdx = -1;
            for (int slot = 0; slot < 8; slot++)
            {
                if (bus.ReadByte(0x77) < 0) break;
                int res;
                do
                {
                    listIdx++;
                    if (listIdx >= RamAddresses.Length) break;
                    res = bus.ReadByte(RamAddresses[listIdx]);
                } while (res >= 0);
                if (listIdx >= RamAddresses.Length) break;
                RegWrite(bus, 0x77, RegSlotIndex, (byte)slot);
                RegWrite(bus, 0x77, RegI2cAddress, (byte)(RamAddresses[listIdx] << 1));
            }
        }

        var found = new List<EneDramController>();
        foreach (var addr in RamAddresses)
        {
            if (!HasEneSignature(bus, addr) || IsMicron(bus, addr)) continue;
            var c = Create(bus, addr, found.Count + 1);
            if (c is not null) found.Add(c);
            Thread.Sleep(1);
        }
        return found;
    }

    private static EneDramController? Create(ISmbus bus, byte addr, int index)
    {
        var name = new StringBuilder();
        for (int i = 0; i < 15; i++)
        {
            int c = RegRead(bus, addr, (ushort)(RegDeviceName + i));
            if (c <= 0) break;
            name.Append((char)c);
        }
        var cfg = new byte[64];
        for (int i = 0; i < 64; i++) cfg[i] = (byte)Math.Max(0, RegRead(bus, addr, (ushort)(RegConfigTable + i)));

        string version = name.ToString();
        int ledCount = cfg[0x02];
        ushort direct;
        switch (version)
        {
            case "LED-0116" or "DIMM_LED-0102" or "DIMM_LED-0103" or "AUMA0-E8K4-0101":
                direct = RegColorsDirect;
                break;
            case "AUDA0-E6K5-0101" or "AUMA0-E6K5-0106" or "AUMA0-E6K5-0105" or "AUMA0-E6K5-0104":
                direct = RegColorsDirectV2;
                break;
            case "AUMA0-E6K5-0107" or "AUMA0-E6K5-1110" or "AUMA0-E6K5-1111" or "AUMA0-E6K5-1107"
                or "AUMA0-E6K5-0008" or "AUMA0-E6K5-1113" or "AUMA0-E6K5-1114":
                direct = RegColorsDirectV2;
                ledCount = cfg[0x03];
                break;
            default:
                return null; // version inconnue : OpenRGB l'ignore aussi (0 LED)
        }
        if (ledCount is <= 0 or > 64) return null;
        return new EneDramController(bus, addr, version, direct, ledCount, index);
    }

    public void Configure(IReadOnlyDictionary<string, int> zoneSizes)
    {
        RegWrite(_bus, _addr, RegDirect, 1);
        RegWrite(_bus, _addr, RegApply, 1);
    }

    public void Write(ReadOnlySpan<Rgb> colors)
    {
        if (colors.Length != _ledCount) return;
        // Ordre des octets propre à ENE : rouge, bleu, vert.
        for (int i = 0; i < _ledCount; i++)
        {
            _buffer[i * 3] = colors[i].R;
            _buffer[i * 3 + 1] = colors[i].B;
            _buffer[i * 3 + 2] = colors[i].G;
        }
        for (int sent = 0; sent < _buffer.Length; sent += MaxBlock)
        {
            int n = Math.Min(MaxBlock, _buffer.Length - sent);
            ushort reg = (ushort)(_directReg + sent);
            if (!_bus.WriteWordData(_addr, 0x00, (ushort)(((reg << 8) & 0xFF00) | ((reg >> 8) & 0x00FF))))
                throw new IOException($"Barrette ENE 0x{_addr:X2} ne répond plus");
            if (!_bus.WriteBlockData(_addr, 0x03, _buffer.AsSpan(sent, n)))
                for (int b = 0; b < n; b++) _bus.WriteByteData(_addr, 0x01, _buffer[sent + b]);
        }
    }

    public void Dispose() { }
}
