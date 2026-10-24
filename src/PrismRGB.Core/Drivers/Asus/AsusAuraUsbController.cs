// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using PrismRGB.Core.Config;
using PrismRGB.Core.Devices;
using PrismRGB.Core.Hid;
using PrismRGB.Core.Native;
using PrismRGB.Core.Platform;

namespace PrismRGB.Core.Drivers.Asus;

/// <summary>
/// ASUS Aura USB : contrôleurs Aura des cartes mères ASUS récentes et contrôleurs "Addressable".
/// Porté depuis OpenRGB AsusAuraUSBController / AuraMainboardController / AuraAddressableController (GPL-2.0-or-later).
/// Rapports de sortie de 65 octets commençant par 0xEC ; mode direct : 0xEC 0x40 [canal|0x80 dernier] [offset] [nb] RGB...
/// </summary>
public sealed class AsusAuraUsbController : INativeController
{
    public const ushort Vid = 0x0B05;
    public static readonly ushort[] MainboardPids = [0x18F3, 0x1939, 0x19AF, 0x1AA6, 0x1BED];
    public static readonly ushort[] AddressablePids = [0x1867, 0x1872, 0x18A3, 0x18A5];
    private const int ReportLength = 65;
    private const int LedsPerPacket = 20;
    private const int AddressableMax = 500;
    private const int DefaultArgbLeds = 30;

    private sealed record Channel(string Name, bool Fixed, byte EffectChannel, byte DirectChannel, int FixedLeds);

    private readonly IHidDevice _dev;
    private readonly bool _mainboard;
    private readonly List<Channel> _channels = [];
    private readonly string _keyBase;
    private readonly string _name;
    private readonly Dictionary<int, int> _sizes = [];

    private AsusAuraUsbController(IHidDevice dev, bool mainboard, byte[] config, string name)
    {
        _dev = dev;
        _mainboard = mainboard;
        _name = name;
        _keyBase = $"asusaura:{dev.Info.VidPid}:{dev.Info.Serial}";
        if (mainboard)
        {
            int fixedLeds = config[0x1B];
            if (fixedLeds > 0) _channels.Add(new("Carte mère", true, 0, 0x04, fixedLeds));
            byte effect = (byte)(fixedLeds > 0 ? 1 : 0);
            for (int i = 0; i < config[0x02]; i++)
                _channels.Add(new($"ARGB {i + 1}", false, effect++, (byte)i, 0));
        }
        else
        {
            for (int i = 0; i < config[0x02]; i++)
                _channels.Add(new($"ARGB {i + 1}", false, (byte)i, (byte)i, 0));
        }
        Device = BuildDevice();
    }

    public RgbDevice Device { get; private set; }

    public static bool IsCandidate(HidDeviceInfo d)
    {
        if (d.VendorId != Vid || d.OutputReportLength < ReportLength) return false;
        if (d.ProductId == 0x19AF) return d.UsagePage == 0xFF72 && d.Usage == 0xA1;
        return MainboardPids.Contains(d.ProductId) || AddressablePids.Contains(d.ProductId);
    }

    private static byte[] Cmd(params byte[] head)
    {
        var b = new byte[ReportLength];
        head.CopyTo(b, 0);
        return b;
    }

    /// <summary>Envoie une requête et attend la réponse dont l'octet 1 vaut <paramref name="expect"/>.</summary>
    private static byte[]? Request(IHidDevice dev, byte request, byte expect)
    {
        if (!dev.Write(Cmd(0xEC, request))) return null;
        var buf = new byte[ReportLength];
        for (int i = 0; i < 5; i++)
        {
            int n = dev.Read(buf, 300);
            if (n <= 0) return null;
            if (buf[0] == 0xEC && buf[1] == expect) return buf;
        }
        return null;
    }

    public static DetectionResult Detect(IHidProvider provider, HidDeviceInfo info, SystemInfo sys)
    {
        bool mainboard = MainboardPids.Contains(info.ProductId);
        string board = string.IsNullOrWhiteSpace(sys.BoardProduct) ? "ASUS" : $"ASUS {sys.BoardProduct}";
        string name = mainboard ? board : $"{board} (ARGB)";
        var dev = provider.Open(info);
        if (dev is null) return new(null, name, "Impossible d'ouvrir le contrôleur Aura (Armoury Crate le bloque peut-être).");
        bool keep = false;
        try
        {
            Request(dev, 0x82, 0x02); // version du firmware (lecture préalable, comme Armoury Crate)
            var table = Request(dev, 0xB0, 0x30);
            if (table is null) return new(null, name, "Le contrôleur Aura ne répond pas à la lecture de sa configuration.");
            var config = table.AsSpan(4, 60).ToArray();
            var c = new AsusAuraUsbController(dev, mainboard, config, name);
            if (c.Device.Zones.Count == 0) return new(null, name, "Aucune zone RGB déclarée par le contrôleur Aura.");
            if (mainboard) dev.Write(Cmd(0xEC, 0x52, 0x53, 0x00, 0x01)); // passage en protocole "Gen1"
            keep = true;
            return new(c, $"{name} (Aura USB)");
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

    private int Size(int index, Channel ch) => ch.Fixed ? ch.FixedLeds : _sizes.GetValueOrDefault(index, DefaultArgbLeds);

    private RgbDevice BuildDevice()
    {
        var zones = new List<RgbZone>();
        int start = 0;
        for (int i = 0; i < _channels.Count; i++)
        {
            var ch = _channels[i];
            int n = Size(i, ch);
            zones.Add(new RgbZone
            {
                Index = i, Name = ch.Name, Kind = ZoneKind.Linear, StartIndex = start, LedCount = n,
                MinLeds = ch.Fixed ? n : 0, MaxLeds = ch.Fixed ? n : AddressableMax, Tag = i,
            });
            start += n;
        }
        return new RgbDevice
        {
            Key = _keyBase, BackendIndex = _dev.Info.ProductId, Name = _name, Vendor = "ASUS",
            Description = _mainboard ? "ASUS Aura USB (carte mère)" : "ASUS Aura USB (ARGB)",
            Location = _dev.Info.Path, Serial = _dev.Info.Serial,
            Kind = _mainboard ? DeviceKind.Motherboard : DeviceKind.LedStrip,
            Zones = zones, LedCount = start, HasDirectMode = true,
        };
    }

    public void Configure(IReadOnlyDictionary<string, int> zoneSizes)
    {
        for (int i = 0; i < _channels.Count; i++)
        {
            if (_channels[i].Fixed) continue;
            var key = ZoneKeys.Zone(_keyBase, i, _channels[i].Name);
            _sizes[i] = zoneSizes.TryGetValue(key, out int v) ? Math.Clamp(v, 0, AddressableMax) : DefaultArgbLeds;
        }
        Device = BuildDevice();
        // Mode direct sur chaque canal utilisé.
        for (int i = 0; i < _channels.Count; i++)
        {
            if (Device.Zones[i].LedCount == 0) continue;
            var ch = _channels[i];
            if (_mainboard) _dev.Write(Cmd(0xEC, 0x35, ch.EffectChannel, 0x00, 0x00, 0xFF));
            else _dev.Write(Cmd(0xEC, 0x3B, (byte)i, 0x00, 0xFF, 0, 0, 0));
        }
    }

    public void Write(ReadOnlySpan<Rgb> colors)
    {
        foreach (var z in Device.Zones)
        {
            if (z.LedCount == 0) continue;
            SendDirect(_channels[z.Tag].DirectChannel, colors.Slice(z.StartIndex, z.LedCount));
        }
    }

    /// <summary>Paquets de 20 LEDs ; le dernier porte le drapeau "appliquer" (0x80).</summary>
    private void SendDirect(byte channel, ReadOnlySpan<Rgb> colors)
    {
        byte command = channel;
        int offset = 0, configured = 0, count = colors.Length;
        while (configured < count)
        {
            int inPacket = LedsPerPacket;
            if (configured + inPacket < count) configured += inPacket;
            else
            {
                command |= 0x80;
                inPacket = count - configured;
                configured = count;
            }
            var buf = new byte[ReportLength];
            buf[0] = 0xEC;
            buf[1] = 0x40;
            buf[2] = command;
            buf[3] = (byte)offset;
            buf[4] = (byte)inPacket;
            for (int i = 0; i < inPacket; i++)
            {
                var c = colors[configured - inPacket + i];
                buf[5 + i * 3] = c.R;
                buf[6 + i * 3] = c.G;
                buf[7 + i * 3] = c.B;
            }
            if (!_dev.Write(buf)) throw new IOException("Contrôleur Aura injoignable");
            offset += inPacket;
            if (configured > 255)
            {
                command |= 0x10; // dépassement de l'offset 8 bits
                offset = configured - 256;
            }
        }
    }

    public void Dispose() => _dev.Dispose();
}
