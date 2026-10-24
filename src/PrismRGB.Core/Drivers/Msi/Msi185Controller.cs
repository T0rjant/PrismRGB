// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using PrismRGB.Core.Config;
using PrismRGB.Core.Devices;
using PrismRGB.Core.Hid;
using PrismRGB.Core.Native;

namespace PrismRGB.Core.Drivers.Msi;

/// <summary>
/// Cartes MSI Mystic Light "185 octets" (AM4/AM5 B550-B650-X670, Intel 400-700).
/// Mode direct par LED porté depuis OpenRGB MSIMotherboard185Controller (GPL-2.0-or-later),
/// lui-même basé sur mystic-why (A. Garashchenko).
///
/// Principe : un report 0x52 (185 o) bascule la carte en mode "par LED", puis chaque image est
/// envoyée dans un report 0x53 (725 o : en-tête 5 o + 240 couleurs RGB).
/// Les octets de sauvegarde en flash ne sont jamais activés.
/// </summary>
public sealed class Msi185Controller : INativeController
{
    public const int ConfigLength = 185;
    public const int PerLedLength = 5 + 240 * 3;
    private const int SyncJRainbowLeds = 40;
    private const int SyncCorsairLeds = 120;
    private const int JRainbow1Max = 200;
    private const int JRainbow2Max = 240;
    private const int JCorsairMax = 240;
    private const byte DirectEffect = 0x25;
    private const byte BasicSync = 0x80 | 0x01 | 0x10 | 0x20;
    private const byte FullSync = BasicSync | 0x02 | 0x04 | 0x08;

    // Décalages des zones dans le report 0x52 (voir FeaturePacket_185).
    private const int OffJRgb1 = 1, OffJPipe1 = 11, OffJPipe2 = 21, OffJRainbow1 = 31, OffJRainbow2 = 42, OffJCorsair = 53,
        OffOuterLl = 64, OffOnBoard = 74, OffOnBoard1 = 84, OffJRgb2 = 174;

    private readonly IHidDevice _dev;
    private readonly Msi185Config _cfg;
    private readonly string _keyBase;
    private readonly string _name;
    private readonly byte[] _sync = new byte[PerLedLength];
    private readonly byte[] _jr1 = new byte[PerLedLength];
    private readonly byte[] _jr2 = new byte[PerLedLength];
    private readonly byte[] _jcor = new byte[PerLedLength];
    private int _sizeJr1, _sizeJr2, _sizeJr3;
    private bool _syncMode = true;
    private bool _directConfigured;

    public Msi185Controller(IHidDevice dev, Msi185Config cfg, string boardName)
    {
        _dev = dev;
        _cfg = cfg;
        _name = boardName;
        _keyBase = $"msi185:{cfg.Pid:X4}:{dev.Info.Serial}";
        InitPerLed(_sync, 0x06, 0x00);
        InitPerLed(_jr1, 0x04, 0x00);
        InitPerLed(_jr2, 0x04, 0x01);
        InitPerLed(_jcor, 0x05, 0x00);
        Device = BuildDevice();
    }

    public RgbDevice Device { get; private set; }
    public Msi185Config Config => _cfg;
    public bool SyncMode => _syncMode;

    private static void InitPerLed(byte[] p, byte hdr1, byte hdr2)
    {
        p[0] = 0x53;
        p[1] = 0x25;
        p[2] = hdr1;
        p[3] = hdr2;
        p[4] = 0x00;
    }

    private bool Has(MsiZone z) => _cfg.Zones.Contains(z);

    private string ZoneKey(int index, string name) => ZoneKeys.Zone(_keyBase, index, name);

    // Ordre des zones identique à OpenRGB.
    private static readonly MsiZone[] Order =
        [MsiZone.JRgb1, MsiZone.JRgb2, MsiZone.JRainbow1, MsiZone.JRainbow2, MsiZone.JRainbow3, MsiZone.JCorsair, MsiZone.JPipe1, MsiZone.JPipe2, MsiZone.OnBoard];

    private (int count, int min, int max) ZoneSize(MsiZone z) => z switch
    {
        MsiZone.JRgb1 or MsiZone.JRgb2 => (1, 1, 1),
        MsiZone.JPipe1 => (_cfg.Pipe1Leds, _cfg.Pipe1Leds, _cfg.Pipe1Leds),
        MsiZone.JPipe2 => (_cfg.Pipe2Leds, _cfg.Pipe2Leds, _cfg.Pipe2Leds),
        MsiZone.OnBoard => (_cfg.OnboardLeds, _cfg.OnboardLeds, _cfg.OnboardLeds),
        MsiZone.JRainbow1 => (_sizeJr1, 0, JRainbow1Max),
        MsiZone.JRainbow2 => (_sizeJr2, 0, JRainbow2Max),
        MsiZone.JRainbow3 or MsiZone.JCorsair => (_sizeJr3, 0, JCorsairMax),
        _ => (0, 0, 0),
    };

    private RgbDevice BuildDevice()
    {
        var zones = new List<RgbZone>();
        int start = 0, idx = 0;
        foreach (var z in Order)
        {
            if (!Has(z)) continue;
            if (z == MsiZone.JRgb2 && _cfg.JRgbCount < 2) continue;
            var (count, min, max) = ZoneSize(z);
            if (max == 0) continue;
            zones.Add(new RgbZone
            {
                Index = idx++,
                Name = MsiBoards.ZoneName(z),
                Kind = count == 1 && max == 1 ? ZoneKind.Single : ZoneKind.Linear,
                StartIndex = start,
                LedCount = count,
                MinLeds = min,
                MaxLeds = max,
                Tag = (int)z,
            });
            start += count;
        }
        return new RgbDevice
        {
            Key = _keyBase,
            BackendIndex = _cfg.Pid,
            Name = _name,
            Vendor = "MSI",
            Description = $"MSI Mystic Light (185 octets) - {_cfg.Comment}",
            Location = _dev.Info.Path,
            Serial = _dev.Info.Serial,
            Kind = DeviceKind.Motherboard,
            Zones = zones,
            LedCount = start,
            HasDirectMode = _cfg.Mode == MsiDirectMode.PerLed,
        };
    }

    public void Configure(IReadOnlyDictionary<string, int> zoneSizes)
    {
        int Get(MsiZone z, int max, int def)
        {
            var current = Device.Zones.FirstOrDefault(x => x.Tag == (int)z);
            if (current is not null && zoneSizes.TryGetValue(ZoneKey(current.Index, current.Name), out int v))
                return Math.Clamp(v, 0, max);
            return def;
        }

        // Par défaut 40 LEDs par connecteur ARGB : couvre la plupart des ventilos/bandes en mode synchronisé.
        _sizeJr1 = Has(MsiZone.JRainbow1) ? Get(MsiZone.JRainbow1, JRainbow1Max, SyncJRainbowLeds) : 0;
        _sizeJr2 = Has(MsiZone.JRainbow2) ? Get(MsiZone.JRainbow2, JRainbow2Max, SyncJRainbowLeds) : 0;
        _sizeJr3 = Has(MsiZone.JRainbow3) ? Get(MsiZone.JRainbow3, JCorsairMax, SyncJRainbowLeds)
                 : Has(MsiZone.JCorsair) ? Get(MsiZone.JCorsair, JCorsairMax, SyncJRainbowLeds) : 0;
        Device = BuildDevice();
        SelectPerLedProtocol();
    }

    /// <summary>Report 0x52 qui passe la carte en mode direct par LED (enable_per_led_msg d'OpenRGB).</summary>
    internal byte[] BuildEnablePacket()
    {
        var p = new byte[ConfigLength];
        p[0] = 0x52;
        // Valeurs par défaut des structures : effet statique partout, cycle 100 sur JRAINBOW, fan_flags 40 sur JCORSAIR.
        foreach (int off in new[] { OffJRgb1, OffJPipe1, OffJPipe2, OffJRainbow1, OffJRainbow2, OffJCorsair, OffOuterLl, OffOnBoard })
            p[off] = 1;
        for (int i = 0; i < 9; i++) p[OffOnBoard1 + i * 10] = 1;
        p[OffJRgb2] = 1;
        p[OffJRainbow1 + 10] = 100;
        p[OffJRainbow2 + 10] = 100;
        p[OffJCorsair + 4] = 40;

        void Zone(int off, byte sb, byte cf) { p[off + 4] = sb; p[off + 8] = cf; }
        Zone(OffJRgb1, 0x08, 0x80);
        Zone(OffJPipe1, 0x2A, 0x80);
        Zone(OffJPipe2, 0x2A, 0x80);
        Zone(OffJRainbow1, 0x29, 0x80);
        p[OffJRainbow1 + 10] = 0x28;
        Zone(OffJRainbow2, 0x29, 0x80);
        p[OffJRainbow2 + 10] = 0x28;
        p[OffJCorsair + 4] = 0x29;      // fan_flags
        p[OffJCorsair + 5] = 0x00;      // corsair_quantity
        p[OffJCorsair + 6 + 2] = 0x82;  // padding[2]
        p[OffJCorsair + 10] = 0x78;     // is_individual
        Zone(OffOuterLl, 0x28, 0x80);
        p[OffOnBoard] = DirectEffect;
        Zone(OffOnBoard, 0x29 | 0x80, FullSync);
        for (int i = 0; i < 9; i++) Zone(OffOnBoard1 + i * 10, 0x28, 0x80);
        Zone(OffJRgb2, 0x2A, 0x80);

        if (_syncMode)
        {
            p[OffJRainbow1] = 1;
            p[OffJRainbow1 + 10] = SyncJRainbowLeds;
            p[OffJRainbow2] = 1;
            p[OffJRainbow2 + 10] = SyncJRainbowLeds;
            p[OffJCorsair] = 1;
            p[OffJCorsair + 10] = SyncCorsairLeds;
            p[OffOnBoard + 8] = FullSync;
        }
        else
        {
            if (Has(MsiZone.JRainbow1)) { p[OffJRainbow1] = DirectEffect; p[OffJRainbow1 + 10] = JRainbow1Max; }
            if (Has(MsiZone.JRainbow2)) { p[OffJRainbow2] = DirectEffect; p[OffJRainbow2 + 10] = JRainbow2Max; }
            if (Has(MsiZone.JCorsair) || Has(MsiZone.JRainbow3)) { p[OffJCorsair] = DirectEffect; p[OffJCorsair + 10] = JCorsairMax; }
            p[OffOnBoard + 8] = BasicSync;
        }
        p[ConfigLength - 1] = 0; // save_data : jamais d'écriture en flash
        return p;
    }

    private void SelectPerLedProtocol()
    {
        // Mode synchronisé (un seul report par image) tant que les connecteurs ARGB restent sous 40/120 LEDs.
        _syncMode = _sizeJr1 <= SyncJRainbowLeds && _sizeJr2 <= SyncJRainbowLeds && _sizeJr3 <= SyncCorsairLeds;
        _directConfigured = false;
        if (_cfg.Mode != MsiDirectMode.PerLed) return;
        _directConfigured = _dev.SetFeature(BuildEnablePacket());
    }

    // Position des LEDs dans le report synchronisé.
    private int OnboardBase => 0;
    private int Pipe1Base => _cfg.OnboardLeds;
    private int Pipe2Base => Pipe1Base + _cfg.Pipe1Leds;
    private int JRgbBase => Pipe2Base + _cfg.Pipe2Leds;
    private int Jr1SyncBase => JRgbBase + _cfg.JRgbCount;
    private int Jr2SyncBase => Jr1SyncBase + SyncJRainbowLeds;
    private int JCorSyncBase => Jr2SyncBase + SyncJRainbowLeds;

    private static void Put(byte[] packet, int led, Rgb c)
    {
        if (led < 0 || led >= 240) return;
        int o = 5 + led * 3;
        packet[o] = c.R;
        packet[o + 1] = c.G;
        packet[o + 2] = c.B;
    }

    public void Write(ReadOnlySpan<Rgb> colors)
    {
        if (_cfg.Mode != MsiDirectMode.PerLed) return;
        if (!_directConfigured)
        {
            _directConfigured = _dev.SetFeature(BuildEnablePacket());
            if (!_directConfigured) return;
        }

        foreach (var z in Device.Zones)
        {
            var zone = (MsiZone)z.Tag;
            for (int i = 0; i < z.LedCount; i++)
            {
                int src = z.StartIndex + i;
                if (src >= colors.Length) break;
                var c = colors[src];
                switch (zone)
                {
                    case MsiZone.OnBoard: Put(_sync, OnboardBase + i, c); break;
                    case MsiZone.JPipe1: Put(_sync, Pipe1Base + i, c); break;
                    case MsiZone.JPipe2: Put(_sync, Pipe2Base + i, c); break;
                    case MsiZone.JRgb1: Put(_sync, JRgbBase, c); break;
                    case MsiZone.JRgb2: Put(_sync, JRgbBase + 1, c); break;
                    case MsiZone.JRainbow1:
                        if (_syncMode) { if (i < SyncJRainbowLeds) Put(_sync, Jr1SyncBase + i, c); }
                        else Put(_jr1, i, c);
                        break;
                    case MsiZone.JRainbow2:
                        if (_syncMode) { if (i < SyncJRainbowLeds) Put(_sync, Jr2SyncBase + i, c); }
                        else Put(_jr2, i, c);
                        break;
                    case MsiZone.JRainbow3:
                    case MsiZone.JCorsair:
                        if (_syncMode) { if (i < SyncCorsairLeds) Put(_sync, JCorSyncBase + i, c); }
                        else Put(_jcor, i, c);
                        break;
                }
            }
        }

        if (!_syncMode)
        {
            // Le firmware a besoin d'un petit délai entre deux reports consécutifs.
            if (Has(MsiZone.JRainbow1)) { _dev.SetFeature(_jr1); Thread.Sleep(13); }
            if (Has(MsiZone.JRainbow2)) { _dev.SetFeature(_jr2); Thread.Sleep(13); }
            if (Has(MsiZone.JCorsair) || Has(MsiZone.JRainbow3)) { _dev.SetFeature(_jcor); Thread.Sleep(13); }
        }
        _dev.SetFeature(_sync);
    }

    /// <summary>Accès aux buffers pour les tests.</summary>
    internal byte[] SyncPacket => _sync;
    internal byte[] JRainbow1Packet => _jr1;

    public void Dispose() => _dev.Dispose();
}
