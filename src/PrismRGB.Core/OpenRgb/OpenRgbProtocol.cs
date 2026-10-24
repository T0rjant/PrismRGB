using System.Buffers.Binary;
using System.Text;
using PrismRGB.Core.Devices;

namespace PrismRGB.Core.OpenRgb;

/// <summary>
/// Protocole réseau du SDK OpenRGB (port 6742 par défaut).
/// Référence : Documentation/OpenRGBSDK.md du dépôt OpenRGB, recoupée avec openrgb-python.
/// Tout est en little-endian ; les chaînes sont préfixées par un uint16 qui inclut le zéro terminal.
/// </summary>
public static class OpenRgbProtocol
{
    /// <summary>
    /// Version la plus haute que l'on sait décoder. La 4 correspond à OpenRGB 0.9 ; les serveurs 1.0
    /// restent compatibles en négociant à la baisse. La 6 (non publiée) change la sémantique de pkt_dev_id.
    /// </summary>
    public const uint MaxSupportedProtocol = 4;

    public const int HeaderSize = 16;
    public static ReadOnlySpan<byte> Magic => "ORGB"u8;

    public const uint RequestControllerCount = 0;
    public const uint RequestControllerData = 1;
    public const uint RequestProtocolVersion = 40;
    public const uint SetClientName = 50;
    public const uint DeviceListUpdated = 100;
    public const uint RequestRescanDevices = 140;
    public const uint ResizeZone = 1000;
    public const uint UpdateLeds = 1050;
    public const uint UpdateZoneLeds = 1051;
    public const uint SetCustomMode = 1100;

    public static void WriteHeader(Span<byte> dst, uint devId, uint pktId, uint size)
    {
        Magic.CopyTo(dst);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], devId);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[8..], pktId);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[12..], size);
    }

    public static byte[] BuildPacket(uint devId, uint pktId, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[HeaderSize + payload.Length];
        WriteHeader(buf, devId, pktId, (uint)payload.Length);
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    public static byte[] BuildUInt32Packet(uint devId, uint pktId, uint value)
    {
        Span<byte> p = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(p, value);
        return BuildPacket(devId, pktId, p);
    }

    public static byte[] BuildClientName(string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        return BuildPacket(0, SetClientName, bytes);
    }

    public static byte[] BuildResizeZone(uint devId, int zone, int size)
    {
        Span<byte> p = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(p, zone);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], size);
        return BuildPacket(devId, ResizeZone, p);
    }

    /// <summary>
    /// UPDATELEDS : data_size (uint32, inclut lui-même) + num_colors (uint16) + couleurs (R,G,B,0).
    /// Écrit dans <paramref name="buffer"/> (réutilisé entre deux images) et retourne la longueur totale.
    /// </summary>
    public static int BuildUpdateLeds(ref byte[] buffer, uint devId, ReadOnlySpan<Rgb> colors)
    {
        int payload = 4 + 2 + colors.Length * 4;
        int total = HeaderSize + payload;
        if (buffer.Length < total) buffer = new byte[total];
        var span = buffer.AsSpan(0, total);
        WriteHeader(span, devId, UpdateLeds, (uint)payload);
        var p = span[HeaderSize..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, (uint)payload);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], (ushort)colors.Length);
        int o = 6;
        foreach (var c in colors)
        {
            p[o] = c.R; p[o + 1] = c.G; p[o + 2] = c.B; p[o + 3] = 0;
            o += 4;
        }
        return total;
    }

    /// <summary>Décode la réponse à REQUEST_CONTROLLER_DATA.</summary>
    public static ControllerData ParseController(ReadOnlySpan<byte> data, uint protocol)
    {
        var r = new SpanReader(data);
        uint dataSize = r.U32();
        var c = new ControllerData
        {
            Type = r.I32(),
            Name = r.Str(),
            Vendor = protocol >= 1 ? r.Str() : "",
            Description = r.Str(),
            Version = r.Str(),
            Serial = r.Str(),
            Location = r.Str(),
        };

        int numModes = r.U16();
        c.ActiveMode = r.I32();
        for (int i = 0; i < numModes; i++)
        {
            var m = new ModeData { Name = r.Str(), Value = r.I32(), Flags = r.U32() };
            m.SpeedMin = r.U32();
            m.SpeedMax = r.U32();
            if (protocol >= 3) { m.BrightnessMin = r.U32(); m.BrightnessMax = r.U32(); }
            m.ColorsMin = r.U32();
            m.ColorsMax = r.U32();
            m.Speed = r.U32();
            if (protocol >= 3) m.Brightness = r.U32();
            m.Direction = r.U32();
            m.ColorMode = r.U32();
            int nc = r.U16();
            r.Skip(nc * 4);
            c.Modes.Add(m);
        }

        int numZones = r.U16();
        for (int i = 0; i < numZones; i++)
        {
            var z = new ZoneData
            {
                Name = r.Str(),
                Type = r.I32(),
                LedsMin = r.U32(),
                LedsMax = r.U32(),
                LedsCount = r.U32(),
            };
            int matrixLen = r.U16();
            if (matrixLen > 0)
            {
                z.MatrixHeight = r.U32();
                z.MatrixWidth = r.U32();
                r.Skip(checked((int)(z.MatrixHeight * z.MatrixWidth * 4)));
            }
            if (protocol >= 4)
            {
                int numSegments = r.U16();
                for (int s = 0; s < numSegments; s++)
                {
                    r.Str(); r.I32(); r.U32(); r.U32();
                }
            }
            if (protocol >= 5) r.U32(); // zone_flags
            c.Zones.Add(z);
        }

        int numLeds = r.U16();
        for (int i = 0; i < numLeds; i++)
        {
            c.LedNames.Add(r.Str());
            r.U32(); // led_value
        }

        int numColors = r.U16();
        c.ColorCount = numColors;
        // Les champs suivants (protocole 5+) sont ignorés : data_size permet de s'arrêter proprement.
        _ = dataSize;
        return c;
    }

    public static DeviceKind MapType(int t) =>
        t >= 0 && t < (int)DeviceKind.Unknown ? (DeviceKind)t : DeviceKind.Unknown;

    public static ZoneKind MapZone(int t) => t switch
    {
        0 => ZoneKind.Single,
        2 or 4 or 5 => ZoneKind.Matrix,
        _ => ZoneKind.Linear,
    };

    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _d;
        private int _o;

        public SpanReader(ReadOnlySpan<byte> d) { _d = d; _o = 0; }

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || _o + n > _d.Length)
                throw new InvalidDataException($"Paquet OpenRGB tronqué (offset {_o}, besoin {n}, taille {_d.Length}).");
            var s = _d.Slice(_o, n);
            _o += n;
            return s;
        }

        public void Skip(int n) => Take(n);
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public string Str()
        {
            int len = U16();
            var s = Take(len);
            int nul = s.IndexOf((byte)0);
            if (nul >= 0) s = s[..nul];
            return Encoding.UTF8.GetString(s);
        }
    }
}

public sealed class ControllerData
{
    public int Type;
    public string Name = "", Vendor = "", Description = "", Version = "", Serial = "", Location = "";
    public int ActiveMode;
    public List<ModeData> Modes { get; } = [];
    public List<ZoneData> Zones { get; } = [];
    public List<string> LedNames { get; } = [];
    public int ColorCount;
}

public sealed class ModeData
{
    public string Name = "";
    public int Value;
    public uint Flags, SpeedMin, SpeedMax, BrightnessMin, BrightnessMax, ColorsMin, ColorsMax, Speed, Brightness, Direction, ColorMode;

    /// <summary>MODE_FLAG_HAS_PER_LED_COLOR : le mode accepte une couleur par LED.</summary>
    public bool HasPerLedColor => (Flags & (1 << 5)) != 0;
}

public sealed class ZoneData
{
    public string Name = "";
    public int Type;
    public uint LedsMin, LedsMax, LedsCount, MatrixHeight, MatrixWidth;
}
