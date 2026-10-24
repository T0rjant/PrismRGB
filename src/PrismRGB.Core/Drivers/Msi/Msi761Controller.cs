using PrismRGB.Core.Config;
using PrismRGB.Core.Devices;
using PrismRGB.Core.Hid;
using PrismRGB.Core.Native;

namespace PrismRGB.Core.Drivers.Msi;

/// <summary>
/// Cartes MSI Mystic Light "761 octets" (AM5 B850/X870, Intel Z890).
/// Porté depuis OpenRGB MSIMotherboard761Controller (GPL-2.0-or-later), basé sur le travail de SignalRGB.
///
/// Principe : un report 0x50 (290 o) décrit les connecteurs actifs et leur nombre de LEDs,
/// puis chaque connecteur reçoit ses couleurs dans un report 0x51 :
///   51 09 [type] [n° connecteur] 00 00 [nb LEDs] + 240 couleurs RGB.
/// type : 0x04 = JARGB, 0x08 = JAF (ventilos MSI), 0x06 = LEDs de la carte (cache I/O).
/// </summary>
public sealed class Msi761Controller : INativeController
{
    public const int SetupLength = 290;
    public const int PerLedLength = 7 + 240 * 3;
    public const int MaxLeds = 240;
    private const int DefaultLeds = 30;

    // Tableau d'initialisation recopié du plugin SignalRGB via OpenRGB : 1 octet d'en-tête + 18 lignes de 16 octets.
    private static readonly byte[] InitialSetup = BuildInitialSetup();

    // Ordre des lignes du tableau de configuration.
    private static readonly MsiZone[] SetupMap =
    [
        MsiZone.JArgb1, MsiZone.JArgb2, MsiZone.JArgb3, MsiZone.Jaf, MsiZone.JPipe1,
    ];

    private readonly IHidDevice _dev;
    private readonly Msi761Config _cfg;
    private readonly string _keyBase;
    private readonly string _name;
    private readonly byte[] _setup;
    private readonly Dictionary<MsiZone, byte[]> _packets = [];
    private readonly Dictionary<MsiZone, int> _sizes = [];
    private bool _setupDirty = true;

    public Msi761Controller(IHidDevice dev, Msi761Config cfg, string boardName)
    {
        _dev = dev;
        _cfg = cfg;
        _name = boardName;
        _keyBase = $"msi761:{dev.Info.VidPid}:{dev.Info.Serial}";
        _setup = (byte[])InitialSetup.Clone();

        // Tout éteindre (lignes 4 à 16), puis réactiver les zones gérées au-delà des 4 premières (toujours actives).
        for (int i = 4; i < 17; i++)
            foreach (int col in new[] { 1, 2, 6, 10, 11, 12, 13, 14, 15 })
                _setup[Row(i, col)] = 0x00;
        foreach (var z in cfg.Zones)
        {
            int idx = SetupIndex(z);
            if (idx > 3)
            {
                _setup[Row(idx, 1)] = 0x09;
                _setup[Row(idx, 2)] = 0xFF;
                _setup[Row(idx, 6)] = 0xFF;
                _setup[Row(idx, 10)] = 0xFF;
                _setup[Row(idx, 11)] = 0xFF;
                _setup[Row(idx, 12)] = 0xFF;
                _setup[Row(idx, 13)] = 0xFF;
                _setup[Row(idx, 14)] = 0x03;
                _setup[Row(idx, 15)] = 0x95;
            }
        }

        foreach (var z in cfg.Zones)
        {
            var p = new byte[PerLedLength];
            p[0] = 0x51;
            p[1] = 0x09;
            p[2] = z switch { MsiZone.Jaf => 0x08, MsiZone.JPipe1 => 0x06, _ => 0x04 };
            p[3] = z switch { MsiZone.JArgb2 => 0x01, MsiZone.JArgb3 => 0x02, _ => 0x00 };
            p[6] = z == MsiZone.JPipe1 ? (byte)cfg.Pipe1Leds : (byte)0;
            _packets[z] = p;
            _sizes[z] = z == MsiZone.JPipe1 ? cfg.Pipe1Leds : 0;
        }
        Device = BuildDevice();
    }

    public RgbDevice Device { get; private set; }
    internal byte[] SetupPacket => _setup;
    internal byte[] Packet(MsiZone z) => _packets[z];

    private static int Row(int index, int col) => index * 16 + col;
    private static int SetupIndex(MsiZone z) => Array.IndexOf(SetupMap, z);

    private static byte[] BuildInitialSetup()
    {
        byte[] active = [0x09, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x03];
        var b = new List<byte> { 0x50 };
        void RowOn(byte s1, byte s2) { b.AddRange(active); b.Add(s1); b.Add(s2); }
        void RowOff() { b.Add(0x09); b.AddRange(new byte[13]); b.Add(0x94); b.Add(0x1E); }
        RowOn(0x15, 0x78); // JARGB 1
        RowOn(0x15, 0x78); // JARGB 2
        RowOn(0x15, 0x78); // JARGB 3
        RowOn(0x15, 0x78); // JAF
        RowOn(0x95, 0x1E); // JPIPE1
        RowOff();          // JPIPE2
        RowOn(0x95, 0x1E); // JPIPE3
        RowOff();          // JPIPE4
        RowOff();          // JPIPE5
        RowOn(0x95, 0x1E); // JRGB1
        RowOff();          // JRGB2
        for (int i = 0; i < 6; i++) RowOff(); // LEDs intégrées 1 à 6
        RowOn(0x95, 0x1E); // "tout sélectionner"
        b.Add(0x00);
        if (b.Count != SetupLength) throw new InvalidOperationException("Tableau MSI 761 invalide");
        return [.. b];
    }

    private static readonly MsiZone[] Order = [MsiZone.Jaf, MsiZone.JArgb1, MsiZone.JArgb2, MsiZone.JArgb3, MsiZone.JPipe1];

    private string ZoneLabel(MsiZone z) => z == MsiZone.JPipe1 ? "Cache I/O" : z == MsiZone.Jaf ? "JAF (ventilos MSI)" : MsiBoards.ZoneName(z);

    private RgbDevice BuildDevice()
    {
        var zones = new List<RgbZone>();
        int start = 0, idx = 0;
        foreach (var z in Order)
        {
            if (!_cfg.Zones.Contains(z)) continue;
            bool fixedZone = z == MsiZone.JPipe1;
            if (fixedZone && _cfg.Pipe1Leds == 0) continue;
            int count = _sizes[z];
            zones.Add(new RgbZone
            {
                Index = idx++,
                Name = ZoneLabel(z),
                Kind = ZoneKind.Linear,
                StartIndex = start,
                LedCount = count,
                MinLeds = fixedZone ? count : 0,
                MaxLeds = fixedZone ? count : MaxLeds,
                Tag = (int)z,
            });
            start += count;
        }
        return new RgbDevice
        {
            Key = _keyBase,
            BackendIndex = _dev.Info.ProductId,
            Name = _name,
            Vendor = "MSI",
            Description = "MSI Mystic Light (761 octets)",
            Location = _dev.Info.Path,
            Serial = _dev.Info.Serial,
            Kind = DeviceKind.Motherboard,
            Zones = zones,
            LedCount = start,
            HasDirectMode = true,
        };
    }

    public void Configure(IReadOnlyDictionary<string, int> zoneSizes)
    {
        foreach (var zone in Device.Zones)
        {
            var z = (MsiZone)zone.Tag;
            if (z == MsiZone.JPipe1) continue;
            int n = zoneSizes.TryGetValue(ZoneKeys.Zone(_keyBase, zone.Index, zone.Name), out int v) ? Math.Clamp(v, 0, MaxLeds) : DefaultLeds;
            _sizes[z] = n;
            _packets[z][6] = (byte)n;
            // Le nombre de LEDs est le dernier octet de la ligne du connecteur.
            int row = SetupIndex(z);
            if (_setup[Row(row, 16)] != (byte)n)
            {
                _setup[Row(row, 16)] = (byte)n;
                _setupDirty = true;
            }
        }
        Device = BuildDevice();
        _setupDirty = true;
        SendSetup();
    }

    private void SendSetup()
    {
        if (!_setupDirty) return;
        if (_dev.SetFeature(_setup)) _setupDirty = false;
    }

    public void Write(ReadOnlySpan<Rgb> colors)
    {
        SendSetup();
        foreach (var zone in Device.Zones)
        {
            var p = _packets[(MsiZone)zone.Tag];
            for (int i = 0; i < zone.LedCount && i < MaxLeds; i++)
            {
                int src = zone.StartIndex + i;
                if (src >= colors.Length) break;
                int o = 7 + i * 3;
                p[o] = colors[src].R;
                p[o + 1] = colors[src].G;
                p[o + 2] = colors[src].B;
            }
        }
        foreach (var z in Order)
        {
            if (!_packets.TryGetValue(z, out var p)) continue;
            if (z == MsiZone.JPipe1 && _cfg.Pipe1Leds == 0) continue;
            _dev.SetFeature(p);
        }
    }

    public void Dispose() => _dev.Dispose();
}
