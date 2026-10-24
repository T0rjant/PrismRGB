// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using PrismRGB.Core.Devices;
using PrismRGB.Core.Native;
using PrismRGB.Core.Smbus;

namespace PrismRGB.Core.Drivers.Dram;

/// <summary>
/// Kingston FURY Beast / Renegade RGB (DDR4 et DDR5).
/// Porté depuis OpenRGB KingstonFuryDRAMController (GPL-2.0-or-later).
/// Un contrôleur par barrette à l'adresse base + n° de slot (0x58 en DDR4, 0x60 en DDR5).
/// Écritures octet par octet encadrées par "début" (0x53) et "appliquer" (0x44) sur le registre 0x08.
/// </summary>
public sealed class KingstonFuryController : INativeController
{
    public const byte BaseDdr4 = 0x58, BaseDdr5 = 0x60;
    private const byte RegModel = 0x06, RegApply = 0x08, RegMode = 0x09, RegIndex = 0x0B, RegBrightness = 0x20;
    private const byte RegNumSlots = 0x27, RegBaseRed = 0x50, RegBaseGreen = 0x51, RegBaseBlue = 0x52;
    private const byte Begin = 0x53, End = 0x44, ModeDirect = 0x10;
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(10);

    private static readonly byte[] Ddr5Models = [0x10, 0x11, 0x12, 0x15];
    private static readonly byte[] Ddr4Models = [0x21, 0x23];

    private readonly ISmbus _bus;
    private readonly byte _base;
    private readonly int[] _slots;
    private readonly Dictionary<byte, byte>[] _cache;
    private readonly int _ledsPerDimm;

    public KingstonFuryController(ISmbus bus, byte baseAddr, IReadOnlyList<int> slots, string name)
    {
        _bus = bus;
        _base = baseAddr;
        _slots = [.. slots];
        _cache = _slots.Select(_ => new Dictionary<byte, byte>()).ToArray();
        _ledsPerDimm = baseAddr == BaseDdr5 ? 12 : 10;
        var zones = new List<RgbZone>();
        for (int i = 0; i < _slots.Length; i++)
            zones.Add(new RgbZone
            {
                Index = i, Name = $"Barrette slot {_slots[i] + 1}", Kind = ZoneKind.Linear,
                StartIndex = i * _ledsPerDimm, LedCount = _ledsPerDimm, MinLeds = _ledsPerDimm, MaxLeds = _ledsPerDimm,
            });
        Device = new RgbDevice
        {
            Key = $"kingstonfury:{baseAddr:X2}:{string.Join(",", _slots)}",
            BackendIndex = baseAddr,
            Name = name,
            Vendor = "Kingston",
            Description = "Kingston FURY RGB (SMBus)",
            Location = $"{bus.Name}, " + string.Join(", ", _slots.Select(s => $"0x{baseAddr + s:X2}")),
            Kind = DeviceKind.Dram,
            Zones = zones,
            LedCount = _ledsPerDimm * _slots.Length,
            HasDirectMode = true,
        };
    }

    public RgbDevice Device { get; }

    // ---------- Détection ----------

    /// <summary>Contrôle d'après les octets fabricant du SPD DDR5 (évite de lire la signature).</summary>
    public static bool Ddr5SpdMatches(byte variant, byte model) => variant switch
    {
        0x00 => model is 0x11 or 0x13 or 0x15,
        0x01 => model is 0x10 or 0x13 or 0x15,
        0x02 or 0x03 => model is 0x12 or 0x13 or 0x15,
        _ => false,
    };

    private enum Test { Pass, Fail, Error }

    private static Test TestSignature(ISmbus bus, byte addr, bool ddr5, bool spdIdentified)
    {
        if (!bus.WriteByteData(addr, RegApply, Begin)) return Test.Error;
        Thread.Sleep(Delay);
        var result = Test.Pass;
        const string signature = "FURY";
        for (int i = 1; !spdIdentified && i <= 4; i++)
        {
            int res = -1;
            for (int retry = 0; retry < 3; retry++)
            {
                res = bus.ReadWordData(addr, (byte)i);
                Thread.Sleep(Delay);
                if (res is >= 0 and < 0xFFFF) break;
            }
            if (res < 0 || res == 0xFFFF) { result = Test.Error; break; }
            if ((char)((res >> 8) & 0xFF) != signature[i - 1]) { result = Test.Fail; break; }
        }
        if (!spdIdentified && result == Test.Pass)
        {
            int res = bus.ReadWordData(addr, RegModel);
            Thread.Sleep(Delay);
            if (res < 0 || res == 0xFFFF) result = Test.Error;
            else if (!(ddr5 ? Ddr5Models : Ddr4Models).Contains((byte)(res >> 8))) result = Test.Fail;
        }
        if (!bus.WriteByteData(addr, RegApply, End)) return Test.Error;
        Thread.Sleep(Delay);
        return result;
    }

    /// <summary>Recherche les barrettes FURY parmi les slots Kingston identifiés par le SPD.</summary>
    public static KingstonFuryController? Detect(ISmbus bus, IReadOnlyList<DramSlot> slots)
    {
        foreach (var type in new[] { DramType.Ddr5, DramType.Ddr4 })
        {
            bool ddr5 = type == DramType.Ddr5;
            byte baseAddr = ddr5 ? BaseDdr5 : BaseDdr4;
            var found = new List<int>();
            foreach (var slot in slots.Where(s => s.Type == type && Jedec.IsKingston(s.JedecId)))
            {
                bool spdOk = ddr5 && slot.ManufacturerData.Length > 0x19 && Ddr5SpdMatches(slot.ManufacturerData[0x18], slot.ManufacturerData[0x19]);
                var r = Test.Error;
                for (int attempt = 0; attempt < 3 && r == Test.Error; attempt++)
                {
                    r = TestSignature(bus, (byte)(baseAddr + slot.Index), ddr5, spdOk);
                    if (r == Test.Error) Thread.Sleep(Delay);
                }
                if (r == Test.Pass) found.Add(slot.Index);
            }
            if (found.Count > 0)
                return new KingstonFuryController(bus, baseAddr, found, ddr5 ? "Kingston FURY DDR5 RGB" : "Kingston FURY DDR4 RGB");
        }
        return null;
    }

    // ---------- Écriture ----------

    private byte Addr(int i) => (byte)(_base + _slots[i]);

    private bool RawWrite(int i, byte reg, byte val)
    {
        for (int retry = 1; retry <= 3; retry++)
        {
            if (_bus.WriteByteData(Addr(i), reg, val)) return true;
            Thread.Sleep(3 * retry * 10);
        }
        return false;
    }

    private bool CachedWrite(int i, byte reg, byte val)
    {
        if (_cache[i].TryGetValue(reg, out var cur) && cur == val) return false;
        if (!RawWrite(i, reg, val)) throw new IOException($"Barrette 0x{Addr(i):X2} ne répond plus");
        _cache[i][reg] = val;
        return true;
    }

    private void SetRegister(byte reg, byte val)
    {
        for (int i = 0; i < _slots.Length; i++) CachedWrite(i, reg, val);
    }

    private void SendAll(byte reg, byte val)
    {
        for (int i = 0; i < _slots.Length; i++) RawWrite(i, reg, val);
        Thread.Sleep(Delay);
    }

    public void Configure(IReadOnlyDictionary<string, int> zoneSizes)
    {
        foreach (var c in _cache) c.Clear();
        // Préambule de changement de mode : index 0 sur chaque barrette (pas de synchro matérielle en mode direct).
        SendAll(RegApply, Begin);
        for (int i = 0; i < _slots.Length; i++) RawWrite(i, RegIndex, 0);
        Thread.Sleep(Delay);
        SendAll(RegApply, End);
    }

    public void Write(ReadOnlySpan<Rgb> colors)
    {
        if (colors.Length != Device.LedCount) return;
        SendAll(RegApply, Begin);
        SetRegister(RegMode, ModeDirect);
        for (int led = 0; led < _ledsPerDimm; led++)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                var c = colors[i * _ledsPerDimm + led];
                CachedWrite(i, (byte)(RegBaseRed + 3 * led), c.R);
                CachedWrite(i, (byte)(RegBaseBlue + 3 * led), c.B);
                CachedWrite(i, (byte)(RegBaseGreen + 3 * led), c.G);
            }
        }
        SetRegister(RegBrightness, 100);
        SetRegister(RegNumSlots, (byte)Math.Min(_slots.Length, 4));
        SendAll(RegApply, End);
    }

    public void Dispose() { }
}
