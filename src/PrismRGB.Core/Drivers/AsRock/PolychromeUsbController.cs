// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using PrismRGB.Core.Config;
using PrismRGB.Core.Devices;
using PrismRGB.Core.Hid;
using PrismRGB.Core.Native;
using PrismRGB.Core.Platform;

namespace PrismRGB.Core.Drivers.AsRock;

/// <summary>
/// ASRock Polychrome USB (cartes mères ASRock récentes, ruban LED Deskmini).
/// Porté depuis OpenRGB PolychromeUSBController (GPL-2.0-or-later).
/// Rapports de 64 octets sans identifiant ; chaque écriture est suivie d'une lecture d'acquittement.
/// Mode direct ("Chroma Connect", 0x0F) : toutes les zones sont envoyées bout à bout en morceaux de 54/57 octets.
/// </summary>
public sealed class PolychromeUsbController : INativeController
{
    public const ushort Vid = 0x26CE;
    public static readonly ushort[] Pids = [0x01A2, 0x01A6];
    private const byte ReadZoneConfig = 0x11, ReadHeader = 0x14, WriteHeader = 0x15, SetZone = 0x10;
    private const byte InitialChunk = 0xE3, SendChunk = 0xE4, ModeDirect = 0x0F, SpeedDefault = 0xE0;
    private const byte CfgLedCount = 0x02, CfgRgSwap = 0x03, ZoneUnavailable = 0x1E;
    private const int AddressableMax = 100;

    private static readonly string[] ZoneNames =
        ["RGB 1", "RGB 2", "ARGB 1", "ARGB 2", "Chipset (PCH)", "Cache I/O", "Circuit (PCB)", "ARGB 3 / Audio"];

    private sealed record Zone(int Type, bool Addressable);

    private readonly IHidDevice _dev;
    private readonly byte[] _config;
    private readonly List<Zone> _zones = [];
    private readonly string _keyBase;
    private readonly string _name;
    private readonly byte[] _ack = new byte[64];

    private PolychromeUsbController(IHidDevice dev, byte[] config, string name)
    {
        _dev = dev;
        _config = config;
        _name = name;
        _keyBase = $"asrock:{dev.Info.VidPid}:{dev.Info.Serial}";
        for (int z = 0; z < 8; z++)
        {
            if (config[z] == ZoneUnavailable || ((config[9] >> z) & 1) == 0) continue;
            _zones.Add(new Zone(z, z is 2 or 3 or 7));
        }
        Device = BuildDevice();
    }

    public RgbDevice Device { get; private set; }

    public static bool IsCandidate(HidDeviceInfo d) => d.VendorId == Vid && Pids.Contains(d.ProductId);

    private byte[]? Transact(byte[] request) => Transact(_dev, request);

    private static byte[]? Transact(IHidDevice dev, byte[] request)
    {
        if (!dev.Write(request)) return null;
        var resp = new byte[64];
        return dev.Read(resp, 300) > 0 ? resp : null;
    }

    private static byte[] Packet(params byte[] head)
    {
        var b = new byte[65];
        head.CopyTo(b, 0);
        return b;
    }

    public static DetectionResult Detect(IHidProvider provider, HidDeviceInfo info, SystemInfo sys)
    {
        string name = string.IsNullOrWhiteSpace(sys.BoardProduct) ? "ASRock Polychrome" : $"ASRock {sys.BoardProduct}";
        var dev = provider.Open(info);
        if (dev is null) return new(null, name, "Impossible d'ouvrir le contrôleur Polychrome (ASRock Polychrome Sync le bloque peut-être).");
        bool keep = false;
        try
        {
            var counts = Transact(dev, Packet(0, ReadHeader, 0, CfgLedCount));
            var swap = Transact(dev, Packet(0, ReadHeader, 0, CfgRgSwap));
            var header1 = Transact(dev, Packet(0, ReadHeader, 0, 0x01));
            if (counts is null || swap is null || header1 is null)
                return new(null, name, "Le contrôleur Polychrome ne répond pas.");
            var config = new byte[10];
            Array.Copy(counts, 4, config, 0, 8);
            config[8] = swap[4];
            config[9] = header1[4];
            var c = new PolychromeUsbController(dev, config, name);
            if (c.Device.Zones.Count == 0) return new(null, name, "Aucune zone RGB déclarée par le contrôleur.");
            keep = true;
            return new(c, $"{name} (Polychrome USB)");
        }
        catch (Exception ex)
        {
            return new(null, name, $"Erreur de communication : {ex.Message}");
        }
        finally
        {
            if (!keep) dev.Dispose();
        }
    }

    private RgbDevice BuildDevice()
    {
        var zones = new List<RgbZone>();
        int start = 0;
        for (int i = 0; i < _zones.Count; i++)
        {
            var z = _zones[i];
            int n = _config[z.Type];
            zones.Add(new RgbZone
            {
                Index = i, Name = ZoneNames[z.Type], Kind = n == 1 ? ZoneKind.Single : ZoneKind.Linear, StartIndex = start,
                LedCount = n, MinLeds = z.Addressable ? 0 : n, MaxLeds = z.Addressable ? AddressableMax : n, Tag = i,
            });
            start += n;
        }
        return new RgbDevice
        {
            Key = _keyBase, BackendIndex = _dev.Info.ProductId, Name = _name, Vendor = "ASRock",
            Description = "ASRock Polychrome USB", Location = _dev.Info.Path, Serial = _dev.Info.Serial,
            Kind = DeviceKind.Motherboard, Zones = zones, LedCount = start, HasDirectMode = true,
        };
    }

    public void Configure(IReadOnlyDictionary<string, int> zoneSizes)
    {
        // Tailles des connecteurs ARGB (mémorisées par le contrôleur).
        bool resized = false;
        for (int i = 0; i < _zones.Count; i++)
        {
            var z = _zones[i];
            if (!z.Addressable) continue;
            if (zoneSizes.TryGetValue(ZoneKeys.Zone(_keyBase, i, ZoneNames[z.Type]), out int v))
            {
                byte n = (byte)Math.Clamp(v, 0, AddressableMax);
                if (_config[z.Type] != n)
                {
                    _config[z.Type] = n;
                    resized = true;
                }
            }
        }
        if (resized)
        {
            var p = Packet(0, WriteHeader, 0, CfgLedCount);
            Array.Copy(_config, 0, p, 4, 8);
            Transact(p);
        }
        Device = BuildDevice();

        // RGSwap désactivé en direct (sinon clignotements à chaque image), puis mode direct sur chaque zone.
        Transact(Packet(0, WriteHeader, 0, CfgRgSwap, 0));
        for (int i = 0; i < _zones.Count; i++)
        {
            if (Device.Zones[i].LedCount == 0) continue;
            var p = Packet(0, SetZone, 0, (byte)_zones[i].Type, ModeDirect, 0, 0, 0, SpeedDefault, 0xFF);
            Transact(p);
        }
    }

    public void Write(ReadOnlySpan<Rgb> colors)
    {
        // Données de toutes les zones bout à bout (ordre G, R, B), complétées jusqu'à la capacité totale.
        int capacity = 0;
        foreach (var z in Device.Zones) capacity += z.MaxLeds;
        var data = new byte[(capacity + 8) * 3];
        int o = 0;
        foreach (var z in Device.Zones)
        {
            for (int i = 0; i < z.LedCount; i++)
            {
                var c = colors[z.StartIndex + i];
                data[o++] = c.G;
                data[o++] = c.R;
                data[o++] = c.B;
            }
        }

        int idx = 0;
        var buf = Packet(0, SetZone, 0, 0xFF, InitialChunk, 0, 0, 0xFF);
        buf[0x40] = 0x65;
        int offset = 9, cap = 54;
        while (idx < data.Length)
        {
            for (int b = 0; b < cap && idx < data.Length; b++) buf[offset + b] = data[idx++];
            if (!_dev.Write(buf)) throw new IOException("Contrôleur Polychrome injoignable");
            _dev.Read(_ack, 100); // acquittement (ignoré s'il tarde)
            buf = Packet(0, SetZone, 0, 0xFF, SendChunk);
            buf[0x40] = 0x65;
            offset = 5;
            cap = 57;
        }
    }

    public void Dispose() => _dev.Dispose();
}
