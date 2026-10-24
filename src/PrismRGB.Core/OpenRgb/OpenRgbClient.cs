using System.Buffers.Binary;
using System.Net.Sockets;

namespace PrismRGB.Core.OpenRgb;

/// <summary>
/// Connexion TCP bas niveau à un serveur OpenRGB. Une boucle de lecture répartit les réponses
/// vers les requêtes en attente et remonte les notifications DEVICE_LIST_UPDATED.
/// </summary>
public sealed class OpenRgbClient : IAsyncDisposable
{
    private readonly TcpClient _tcp = new() { NoDelay = true };
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly object _pendingLock = new();
    private (uint pktId, TaskCompletionSource<byte[]> tcs)? _pending;
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;
    private byte[] _ledBuffer = new byte[1024];

    public uint Protocol { get; private set; }
    public bool IsConnected => _tcp.Connected && !_cts.IsCancellationRequested && _readLoop is { IsCompleted: false };

    public event EventHandler? DeviceListUpdated;
    public event EventHandler<Exception?>? Disconnected;

    public async Task ConnectAsync(string host, int port, string clientName, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await _tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        _stream = _tcp.GetStream();
        _readLoop = Task.Run(ReadLoopAsync);

        await SendAsync(OpenRgbProtocol.BuildClientName(clientName), ct).ConfigureAwait(false);

        // Un serveur en protocole 0 ne répond pas à cette requête : on considère alors la version 0.
        try
        {
            var resp = await RequestAsync(
                OpenRgbProtocol.BuildUInt32Packet(0, OpenRgbProtocol.RequestProtocolVersion, OpenRgbProtocol.MaxSupportedProtocol),
                OpenRgbProtocol.RequestProtocolVersion, TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
            uint server = BinaryPrimitives.ReadUInt32LittleEndian(resp);
            Protocol = Math.Min(server, OpenRgbProtocol.MaxSupportedProtocol);
        }
        catch (TimeoutException)
        {
            Protocol = 0;
        }
    }

    public async Task<int> GetControllerCountAsync(CancellationToken ct)
    {
        var resp = await RequestAsync(
            OpenRgbProtocol.BuildPacket(0, OpenRgbProtocol.RequestControllerCount, ReadOnlySpan<byte>.Empty),
            OpenRgbProtocol.RequestControllerCount, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        return (int)BinaryPrimitives.ReadUInt32LittleEndian(resp);
    }

    public async Task<ControllerData> GetControllerAsync(int index, CancellationToken ct)
    {
        byte[] pkt = Protocol >= 1
            ? OpenRgbProtocol.BuildUInt32Packet((uint)index, OpenRgbProtocol.RequestControllerData, Protocol)
            : OpenRgbProtocol.BuildPacket((uint)index, OpenRgbProtocol.RequestControllerData, ReadOnlySpan<byte>.Empty);
        var resp = await RequestAsync(pkt, OpenRgbProtocol.RequestControllerData, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return OpenRgbProtocol.ParseController(resp, Protocol);
    }

    public Task SetCustomModeAsync(int index, CancellationToken ct) =>
        SendAsync(OpenRgbProtocol.BuildPacket((uint)index, OpenRgbProtocol.SetCustomMode, ReadOnlySpan<byte>.Empty), ct);

    public Task ResizeZoneAsync(int index, int zone, int size, CancellationToken ct) =>
        SendAsync(OpenRgbProtocol.BuildResizeZone((uint)index, zone, size), ct);

    public Task RequestRescanAsync(CancellationToken ct) =>
        SendAsync(OpenRgbProtocol.BuildPacket(0, OpenRgbProtocol.RequestRescanDevices, ReadOnlySpan<byte>.Empty), ct);

    /// <summary>Envoie les couleurs de toutes les LEDs d'un appareil. Appelé depuis un seul thread d'envoi.</summary>
    public async Task UpdateLedsAsync(int index, ReadOnlyMemory<Rgb> colors, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int len = OpenRgbProtocol.BuildUpdateLeds(ref _ledBuffer, (uint)index, colors.Span);
            await _stream!.WriteAsync(_ledBuffer.AsMemory(0, len), ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task SendAsync(byte[] packet, CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("Non connecté");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _stream.WriteAsync(packet, ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Une seule requête avec réponse à la fois : le protocole ne porte pas d'identifiant de requête.</summary>
    private async Task<byte[]> RequestAsync(byte[] packet, uint expectedId, TimeSpan timeout, CancellationToken ct)
    {
        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingLock) _pending = (expectedId, tcs);
            await SendAsync(packet, ct).ConfigureAwait(false);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (done != tcs.Task)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"Pas de réponse du serveur OpenRGB (paquet {expectedId}).");
            }
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_pendingLock) _pending = null;
            _requestLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[OpenRgbProtocol.HeaderSize];
        Exception? error = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _stream!.ReadExactlyAsync(header, _cts.Token).ConfigureAwait(false);
                if (!header.AsSpan(0, 4).SequenceEqual(OpenRgbProtocol.Magic))
                    throw new InvalidDataException("En-tête OpenRGB invalide (désynchronisation).");
                uint pktId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                if (size > 64 * 1024 * 1024) throw new InvalidDataException($"Paquet OpenRGB trop grand ({size} octets).");
                var payload = new byte[size];
                if (size > 0) await _stream.ReadExactlyAsync(payload, _cts.Token).ConfigureAwait(false);

                if (pktId == OpenRgbProtocol.DeviceListUpdated)
                {
                    DeviceListUpdated?.Invoke(this, EventArgs.Empty);
                    continue;
                }

                TaskCompletionSource<byte[]>? target = null;
                lock (_pendingLock)
                {
                    if (_pending is { } p && p.pktId == pktId) target = p.tcs;
                }
                target?.TrySetResult(payload);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex; }
        finally
        {
            lock (_pendingLock) _pending?.tcs.TrySetException(error ?? new IOException("Connexion fermée"));
            Disconnected?.Invoke(this, error);
        }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        try { _tcp.Close(); } catch { }
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { }
        }
        _tcp.Dispose();
        _cts.Dispose();
    }
}
