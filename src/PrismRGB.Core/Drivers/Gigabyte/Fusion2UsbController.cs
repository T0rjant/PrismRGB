// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using PrismRGB.Core.Config;
using PrismRGB.Core.Devices;
using PrismRGB.Core.Hid;
using PrismRGB.Core.Native;
using PrismRGB.Core.Platform;

namespace PrismRGB.Core.Drivers.Gigabyte;

/// <summary>
/// Gigabyte RGB Fusion 2 USB (puces ITE IT8297 / IT5702 / IT5711 des cartes Gigabyte / AORUS).
/// Porté depuis OpenRGB RGBFusion2USBController (GPL-2.0-or-later), disposition "générique" d'OpenRGB :
/// connecteurs D_LED (LED par LED, ordre des couleurs lu dans la calibration du contrôleur)
/// et LEDs de la carte (une couleur chacune via un effet statique).
/// Feature reports de 64 octets, identifiant 0xCC.
/// </summary>
public sealed class Fusion2UsbController : INativeController
{
    public const ushort Vid = 0x048D;
    public static readonly ushort[] Pids = [0x8297, 0x5702, 0x5711];
    private const byte ReportId = 0xCC;
    private const int Len = 64;
    private const int DledMax = 1024;
    private const int LedsPerPacket = 19;

    private sealed record Strip(string Name, byte Header, int Mask, int CountSlot, int CalSlot, int DefaultLeds);
    private sealed record Single(string Name, int Led);

    private readonly IHidDevice _dev;
    private readonly ushort _pid;
    private readonly uint[] _cal = new uint[6];
    private readonly List<Strip> _strips = [];
    private readonly List<Single> _singles = [];
    private readonly Dictionary<int, int> _sizes = [];
    private readonly Dictionary<int, Rgb> _singleCache = [];
    private readonly string _keyBase;
    private readonly string _name;
    private int _effectDisabled = -1;
    private byte[] _ledCountBytes = [0xFF, 0xFF, 0xFF];

    private Fusion2UsbController(IHidDevice dev, ushort pid, byte[] report, byte[]? cal2, string name)
    {
        _dev = dev;
        _pid = pid;
        _name = name;
        _keyBase = $"fusion2:{dev.Info.VidPid}:{dev.Info.Serial}";
        _cal[0] = BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(44));
        _cal[1] = BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(48));
        if (cal2 is not null)
            for (int i = 0; i < 4; i++) _cal[2 + i] = BinaryPrimitives.ReadUInt32LittleEndian(cal2.AsSpan(4 + i * 4));

        _strips.Add(new("D_LED1", 0x58, 0x01, 0, 0, 30));
        _strips.Add(new("D_LED2", 0x59, 0x02, 1, 1, 30));
        if (pid == 0x5711)
        {
            // Connecteurs supplémentaires de l'IT5711 : désactivés tant que le nombre de LEDs n'est pas indiqué.
            _strips.Add(new("D_LED3", 0x62, 0x08, 2, 2, 0));
            _strips.Add(new("D_LED4", 0x63, 0x10, 3, 3, 0));
            foreach (int led in new[] { 0, 1, 2, 3, 4, 8, 9, 10 }) _singles.Add(new($"LED carte {led + 1}", led));
        }
        else
        {
            foreach (int led in new[] { 0, 1, 2, 3, 4, 7 }) _singles.Add(new($"LED carte {led + 1}", led));
        }
        Device = BuildDevice();
    }

    public RgbDevice Device { get; private set; }

    public static bool IsCandidate(HidDeviceInfo d) =>
        d.VendorId == Vid && Pids.Contains(d.ProductId) && d.UsagePage == 0xFF89 && d.Usage == 0xCC;

    private static byte[] Cc(byte a, byte b = 0, byte c = 0, byte d = 0)
    {
        var p = new byte[Len];
        p[0] = ReportId;
        p[1] = a;
        p[2] = b;
        p[3] = c;
        p[4] = d;
        return p;
    }

    public static DetectionResult Detect(IHidProvider provider, HidDeviceInfo info, SystemInfo sys)
    {
        string name = string.IsNullOrWhiteSpace(sys.BoardProduct) ? "Gigabyte RGB Fusion 2" : $"Gigabyte {sys.BoardProduct}";
        var dev = provider.Open(info);
        if (dev is null) return new(null, name, "Impossible d'ouvrir le contrôleur RGB Fusion (Gigabyte Control Center le bloque peut-être).");
        bool keep = false;
        try
        {
            dev.SetFeature(Cc(0x60));
            var report = new byte[Len];
            report[0] = ReportId;
            if (dev.GetFeature(report, Len) < Len) return new(null, name, "Le contrôleur RGB Fusion ne répond pas.");

            byte[]? cal2 = null;
            if (info.ProductId == 0x5711)
            {
                dev.SetFeature(Cc(0x61));
                var b = new byte[Len];
                b[0] = ReportId;
                if (dev.GetFeature(b, Len) >= Len) cal2 = b;
            }

            if ((report[11] & 0x02) != 0) dev.SetFeature(Cc(0x48, 0)); // désactive Windows Dynamic Lighting interne
            // Remise à zéro des effets matériels puis application rapide.
            for (byte reg = 0x20; reg <= 0x27; reg++) dev.SetFeature(Cc(reg));
            if (info.ProductId == 0x5711)
                for (byte reg = 0x90; reg <= 0x92; reg++) dev.SetFeature(Cc(reg));
            dev.SetFeature(Cc(0x28, 0xFF, info.ProductId == 0x5711 ? (byte)0x07 : (byte)0x00));
            dev.SetFeature(Cc(0x31, 0)); // désactive le mode "beat" (audio)

            keep = true;
            return new(new Fusion2UsbController(dev, info.ProductId, report, cal2, name), $"{name} (RGB Fusion 2)");
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
        int start = 0, idx = 0;
        for (int i = 0; i < _strips.Count; i++)
        {
            int n = _sizes.GetValueOrDefault(i, _strips[i].DefaultLeds);
            zones.Add(new RgbZone { Index = idx++, Name = _strips[i].Name, Kind = ZoneKind.Linear, StartIndex = start, LedCount = n, MinLeds = 0, MaxLeds = DledMax, Tag = i });
            start += n;
        }
        for (int i = 0; i < _singles.Count; i++)
        {
            zones.Add(new RgbZone { Index = idx++, Name = _singles[i].Name, Kind = ZoneKind.Single, StartIndex = start, LedCount = 1, MinLeds = 1, MaxLeds = 1, Tag = 100 + i });
            start += 1;
        }
        return new RgbDevice
        {
            Key = _keyBase, BackendIndex = _pid, Name = _name, Vendor = "Gigabyte",
            Description = $"RGB Fusion 2 (IT{_pid:X4})", Location = _dev.Info.Path, Serial = _dev.Info.Serial,
            Kind = DeviceKind.Motherboard, Zones = zones, LedCount = start, HasDirectMode = true,
        };
    }

    private static byte CountEnum(int c) => c <= 32 ? (byte)0 : c <= 64 ? (byte)1 : c <= 256 ? (byte)2 : c <= 512 ? (byte)3 : (byte)4;

    public void Configure(IReadOnlyDictionary<string, int> zoneSizes)
    {
        for (int i = 0; i < _strips.Count; i++)
        {
            var key = ZoneKeys.Zone(_keyBase, i, _strips[i].Name);
            _sizes[i] = zoneSizes.TryGetValue(key, out int v) ? Math.Clamp(v, 0, DledMax) : _strips[i].DefaultLeds;
        }
        Device = BuildDevice();

        var counts = new int[6];
        for (int i = 0; i < _strips.Count; i++) counts[_strips[i].CountSlot] = _sizes[i];
        byte[] bytes = [(byte)((CountEnum(counts[1]) << 4) | CountEnum(counts[0])), (byte)((CountEnum(counts[3]) << 4) | CountEnum(counts[2])), (byte)((CountEnum(counts[5]) << 4) | CountEnum(counts[4]))];
        if (!bytes.AsSpan().SequenceEqual(_ledCountBytes))
        {
            _ledCountBytes = bytes;
            _dev.SetFeature(Cc(0x34, bytes[0], bytes[1], bytes[2]));
        }
        // Désactive les effets intégrés des connecteurs D_LED : on les pilote LED par LED.
        int mask = _pid == 0x5711 ? 0x01 | 0x02 | 0x08 | 0x10 | 0x20 | 0x40 : 0x01 | 0x02;
        SetEffectDisabled(mask);
        _singleCache.Clear();
    }

    private void SetEffectDisabled(int mask)
    {
        if (_effectDisabled == mask) return;
        _effectDisabled = mask;
        _dev.SetFeature(Cc(0x32, (byte)mask));
        Thread.Sleep(50);
    }

    public void Write(ReadOnlySpan<Rgb> colors)
    {
        uint zoneMask = 0;
        foreach (var z in Device.Zones)
        {
            if (z.LedCount == 0) continue;
            if (z.Tag < 100)
            {
                SendStrip(_strips[z.Tag], colors.Slice(z.StartIndex, z.LedCount));
            }
            else
            {
                var s = _singles[z.Tag - 100];
                var c = colors[z.StartIndex];
                if (_singleCache.TryGetValue(s.Led, out var prev) && prev == c) continue;
                _singleCache[s.Led] = c;
                SendStaticEffect(s.Led, c);
                zoneMask |= 1u << s.Led;
            }
        }
        if (zoneMask != 0)
        {
            var apply = new byte[Len];
            apply[0] = ReportId;
            apply[1] = 0x28;
            BinaryPrimitives.WriteUInt32LittleEndian(apply.AsSpan(2), zoneMask);
            _dev.SetFeature(apply);
        }
    }

    /// <summary>Paquets de 19 LEDs ; position de R/G/B dans chaque triplet selon la calibration d'usine.</summary>
    private void SendStrip(Strip strip, ReadOnlySpan<Rgb> colors)
    {
        uint order = _cal[strip.CalSlot];
        int bo_r = (int)((order >> 16) & 0xFF), bo_g = (int)((order >> 8) & 0xFF), bo_b = (int)(order & 0xFF);
        bool valid = (order >> 24) == 0 && bo_r < 3 && bo_g < 3 && bo_b < 3 && bo_r != bo_g && bo_r != bo_b && bo_g != bo_b;
        if (!valid) (bo_r, bo_g, bo_b) = (0, 1, 2);

        int sent = 0, k = 0, left = colors.Length;
        while (left > 0)
        {
            int n = Math.Min(LedsPerPacket, left);
            left -= n;
            var p = new byte[Len];
            p[0] = ReportId;
            p[1] = strip.Header;
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), (ushort)sent);
            p[4] = (byte)(n * 3);
            sent += n * 3;
            for (int i = 0; i < n; i++)
            {
                var c = colors[k++];
                int o = 5 + i * 3;
                p[o + bo_r] = c.R;
                p[o + bo_g] = c.G;
                p[o + bo_b] = c.B;
            }
            if (!_dev.SetFeature(p)) throw new IOException("Contrôleur RGB Fusion injoignable");
        }
    }

    /// <summary>Effet statique sur une LED de la carte (les LEDs de la carte n'ont pas de mode direct).</summary>
    private void SendStaticEffect(int led, Rgb c)
    {
        var p = new byte[Len];
        p[0] = ReportId;
        uint zone = 1u << led;
        p[1] = (byte)(led < 8 ? 0x20 + led : 0x90 + (led - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(2), zone);
        p[11] = 1;      // effet statique
        p[12] = 0xFF;   // luminosité max
        p[13] = 0x00;   // luminosité min
        p[14] = c.B;    // couleur 0x00RRGGBB en little-endian
        p[15] = c.G;
        p[16] = c.R;
        _dev.SetFeature(p);
    }

    public void Dispose() => _dev.Dispose();
}
