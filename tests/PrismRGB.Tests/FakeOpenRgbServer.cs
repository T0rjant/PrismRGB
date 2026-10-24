using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PrismRGB.Tests;

/// <summary>
/// Faux serveur OpenRGB écrit d'après la documentation du SDK (indépendamment du décodeur testé).
/// </summary>
public sealed class FakeOpenRgbServer : IAsyncDisposable
{
    public sealed class FakeZone
    {
        public string Name = "";
        public int Type = 1;
        public uint Min, Max, Count;
        public uint MatrixH, MatrixW;
    }

    public sealed class FakeDevice
    {
        public int Type;
        public string Name = "", Vendor = "Vendor", Description = "Desc", Version = "1.0", Serial = "SN", Location = "HID: /dev/x";
        public List<FakeZone> Zones = [];
        public string[] Modes = ["Direct", "Static"];
        public int Leds => Zones.Sum(z => (int)z.Count);
    }

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _clients = [];
    private Task? _accept;

    public uint ServerProtocol { get; set; } = 4;
    public List<FakeDevice> Devices { get; } = [];
    public ConcurrentQueue<(uint dev, uint[] colors)> LedUpdates { get; } = new();
    public ConcurrentBag<uint> CustomModeCalls { get; } = [];
    public ConcurrentBag<(uint dev, int zone, int size)> Resizes { get; } = [];
    public string? ClientName { get; private set; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _accept = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            lock (_clients) _clients.Add(c);
            _ = Task.Run(() => Serve(c));
        }
    }

    public async Task SendDeviceListUpdatedAsync()
    {
        TcpClient[] cs;
        lock (_clients) cs = [.. _clients];
        foreach (var c in cs)
        {
            try { await c.GetStream().WriteAsync(Packet(0, 100, [])); } catch { }
        }
    }

    public void DropClients()
    {
        lock (_clients)
        {
            foreach (var c in _clients) c.Close();
            _clients.Clear();
        }
    }

    private async Task Serve(TcpClient c)
    {
        var s = c.GetStream();
        var hdr = new byte[16];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await s.ReadExactlyAsync(hdr, _cts.Token);
                if (Encoding.ASCII.GetString(hdr, 0, 4) != "ORGB") throw new InvalidDataException("magic");
                uint dev = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4));
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(8));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(12));
                var body = new byte[size];
                if (size > 0) await s.ReadExactlyAsync(body, _cts.Token);

                switch (id)
                {
                    case 50:
                        ClientName = Encoding.ASCII.GetString(body).TrimEnd('\0');
                        break;
                    case 40:
                        await s.WriteAsync(Packet(0, 40, U32(ServerProtocol)));
                        break;
                    case 0:
                        await s.WriteAsync(Packet(0, 0, U32((uint)Devices.Count)));
                        break;
                    case 1:
                        uint proto = size >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(body) : 0;
                        await s.WriteAsync(Packet(dev, 1, EncodeController(Devices[(int)dev], proto)));
                        break;
                    case 1000:
                        int zone = BinaryPrimitives.ReadInt32LittleEndian(body);
                        int ns = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4));
                        Devices[(int)dev].Zones[zone].Count = (uint)ns;
                        Resizes.Add((dev, zone, ns));
                        break;
                    case 1050:
                        uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(body);
                        if (dataSize != size) throw new InvalidDataException($"data_size {dataSize} != {size}");
                        int n = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4));
                        var cols = new uint[n];
                        for (int i = 0; i < n; i++) cols[i] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(6 + i * 4));
                        LedUpdates.Enqueue((dev, cols));
                        break;
                    case 1100:
                        CustomModeCalls.Add(dev);
                        break;
                }
            }
        }
        catch { }
        finally { c.Close(); }
    }

    private static byte[] Packet(uint dev, uint id, byte[] payload)
    {
        var b = new byte[16 + payload.Length];
        Encoding.ASCII.GetBytes("ORGB").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), dev);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), id);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(b, 16);
        return b;
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    /// <summary>Encodage du bloc "Controller Data" selon les tableaux de OpenRGBSDK.md.</summary>
    public static byte[] EncodeController(FakeDevice d, uint proto)
    {
        var w = new BinaryWriter(new MemoryStream());
        void Str(string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            w.Write((ushort)(bytes.Length + 1));
            w.Write(bytes);
            w.Write((byte)0);
        }

        w.Write(0u); // data_size, rempli à la fin
        w.Write(d.Type);
        Str(d.Name);
        if (proto >= 1) Str(d.Vendor);
        Str(d.Description);
        Str(d.Version);
        Str(d.Serial);
        Str(d.Location);

        w.Write((ushort)d.Modes.Length);
        w.Write(0); // active_mode
        for (int i = 0; i < d.Modes.Length; i++)
        {
            Str(d.Modes[i]);
            w.Write(i);          // value
            w.Write(1u << 5);    // flags : per-LED color
            w.Write(0u); w.Write(100u); // speed min/max
            if (proto >= 3) { w.Write(0u); w.Write(100u); }
            w.Write(1u); w.Write(4u); // colors min/max
            w.Write(50u); // speed
            if (proto >= 3) w.Write(100u);
            w.Write(0u); // direction
            w.Write(1u); // color mode
            w.Write((ushort)2);
            w.Write(0x0000FFu); w.Write(0x00FF00u);
        }

        w.Write((ushort)d.Zones.Count);
        foreach (var z in d.Zones)
        {
            Str(z.Name);
            w.Write(z.Type);
            w.Write(z.Min); w.Write(z.Max); w.Write(z.Count);
            if (z.MatrixH > 0)
            {
                w.Write((ushort)((z.MatrixH * z.MatrixW + 2) * 4));
                w.Write(z.MatrixH); w.Write(z.MatrixW);
                for (int i = 0; i < z.MatrixH * z.MatrixW; i++) w.Write(i < z.Count ? (uint)i : 0xFFFFFFFF);
            }
            else w.Write((ushort)0);
            if (proto >= 4)
            {
                w.Write((ushort)1);
                Str("Seg");
                w.Write(1);
                w.Write(0u);
                w.Write(z.Count);
            }
            if (proto >= 5) w.Write(0u);
        }

        int leds = d.Leds;
        w.Write((ushort)leds);
        for (int i = 0; i < leds; i++) { Str($"LED {i}"); w.Write((uint)i); }
        w.Write((ushort)leds);
        for (int i = 0; i < leds; i++) w.Write(0u);
        if (proto >= 5)
        {
            w.Write((ushort)0); // led alt names
            w.Write(0u); // flags
        }

        var bytes = ((MemoryStream)w.BaseStream).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length);
        return bytes;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        DropClients();
        if (_accept is not null) try { await _accept; } catch { }
    }
}
