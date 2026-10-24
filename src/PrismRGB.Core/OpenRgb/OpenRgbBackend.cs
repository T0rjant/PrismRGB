using System.Diagnostics;
using System.Net.Sockets;
using PrismRGB.Core.Config;
using PrismRGB.Core.Devices;

namespace PrismRGB.Core.OpenRgb;

/// <summary>
/// Backend OpenRGB : OpenRGB ne sert que de couche de détection/pilotes matériels (mode serveur, sans interface).
/// Toute la logique (effets, profils, reprise sur erreur) est faite ici.
///
/// Robustesse :
///  - lance le serveur s'il ne répond pas, le relance s'il plante ;
///  - se reconnecte automatiquement avec un délai croissant ;
///  - recharge la liste du matériel quand OpenRGB signale un changement ;
///  - réapplique les tailles de zones et le mode direct à chaque (re)connexion ;
///  - envoi des images sur un thread dédié, "la plus récente gagne" pour ne jamais saturer le serveur.
/// </summary>
public sealed class OpenRgbBackend : IRgbBackend
{
    private readonly OpenRgbSettings _settings;
    private readonly Func<IReadOnlyDictionary<string, int>> _zoneSizes;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _frameSignal = new(0, 1);
    private readonly object _frameLock = new();
    private readonly Dictionary<int, Rgb[]> _pendingFrames = [];
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private OpenRgbClient? _client;
    private Process? _server;
    private int _serverLaunches;
    private DateTime _serverLaunchedAt;
    private Task? _mainLoop, _sendLoop;
    private volatile IReadOnlyList<RgbDevice> _devices = [];
    private int _refreshQueued;

    public OpenRgbBackend(OpenRgbSettings settings, Func<IReadOnlyDictionary<string, int>> zoneSizes, Action<string>? log = null)
    {
        _settings = settings;
        _zoneSizes = zoneSizes;
        _log = log;
    }

    public string Name => "OpenRGB";
    public BackendState State { get; private set; } = BackendState.Stopped;
    public string StatusText { get; private set; } = "Arrêté";
    public IReadOnlyList<RgbDevice> Devices => _devices;
    public uint Protocol => _client?.Protocol ?? 0;
    public bool OwnsServer => _server is { HasExited: false };

    public event EventHandler? DevicesChanged;
    public event EventHandler? StateChanged;

    public Task StartAsync(CancellationToken ct = default)
    {
        _mainLoop ??= Task.Run(() => MainLoopAsync(_cts.Token));
        _sendLoop ??= Task.Run(() => SendLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private void SetState(BackendState s, string text)
    {
        if (State == s && StatusText == text) return;
        State = s;
        StatusText = text;
        _log?.Invoke($"[OpenRGB] {text}");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task MainLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        SetState(BackendState.Connecting, "Connexion au serveur OpenRGB...");
        while (!ct.IsCancellationRequested)
        {
            var client = new OpenRgbClient();
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += (_, ex) => disconnected.TrySetResult();
            client.DeviceListUpdated += (_, _) => QueueRefresh();
            try
            {
                await client.ConnectAsync(_settings.Host, _settings.Port, "PrismRGB", ct).ConfigureAwait(false);
                _client = client;
                backoff = TimeSpan.FromSeconds(1);
                SetState(BackendState.Detecting, $"Connecté (protocole {client.Protocol}), lecture du matériel...");
                await RefreshDevicesAsync(ct).ConfigureAwait(false);
                await disconnected.Task.WaitAsync(ct).ConfigureAwait(false);
                SetState(BackendState.Reconnecting, "Connexion perdue, reconnexion...");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException or IOException)
            {
                bool launched = TryEnsureServer(out var why);
                SetState(BackendState.Reconnecting, launched
                    ? "Démarrage du serveur OpenRGB (détection du matériel, ~10-30 s)..."
                    : why ?? $"Serveur OpenRGB injoignable sur {_settings.Host}:{_settings.Port}");
            }
            catch (Exception ex)
            {
                SetState(BackendState.Error, $"Erreur OpenRGB : {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_client, client)) _client = null;
                await client.DisposeAsync().ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(5, backoff.TotalSeconds * 1.5));
        }
        SetState(BackendState.Stopped, "Arrêté");
    }

    /// <summary>Retourne true si un serveur vient d'être lancé (ou est en cours de démarrage).</summary>
    private bool TryEnsureServer(out string? reason)
    {
        reason = null;
        if (!_settings.AutoLaunch || !IsLocalHost(_settings.Host)) return false;

        if (_server is { HasExited: false })
        {
            // Laisse le temps à la détection ; au-delà de 90 s sans port ouvert, on considère qu'il est bloqué.
            if (DateTime.UtcNow - _serverLaunchedAt < TimeSpan.FromSeconds(90)) return true;
            _log?.Invoke("[OpenRGB] Le serveur ne répond pas depuis 90 s, redémarrage.");
            KillServer();
        }

        if (_serverLaunches >= 5)
        {
            reason = "Le serveur OpenRGB plante en boucle. Voir l'onglet Paramètres > Diagnostic.";
            return false;
        }

        var exe = OpenRgbLocator.Find(_settings.ExePath);
        if (exe is null)
        {
            reason = "OpenRGB.exe introuvable : indique son emplacement dans Paramètres.";
            return false;
        }

        if (OpenRgbLocator.IsOtherInstanceRunning(exe))
        {
            reason = "OpenRGB est déjà ouvert sans son serveur SDK. Ferme-le (ou active Settings > SDK Server dans OpenRGB).";
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add("--server");
            psi.ArgumentList.Add("--server-port");
            psi.ArgumentList.Add(_settings.Port.ToString());
            psi.ArgumentList.Add("--noautoconnect");
            _server = Process.Start(psi);
            _serverLaunchedAt = DateTime.UtcNow;
            _serverLaunches++;
            _log?.Invoke($"[OpenRGB] Serveur lancé : {exe} (pid {_server?.Id})");
            return _server is not null;
        }
        catch (Exception ex)
        {
            reason = $"Impossible de lancer OpenRGB : {ex.Message}";
            return false;
        }
    }

    public void RestartServer()
    {
        // La connexion tombe d'elle-même quand le serveur meurt ; la boucle principale le relance.
        KillServer();
        _serverLaunches = 0;
    }

    private void KillServer()
    {
        try
        {
            if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true);
        }
        catch { }
        _server?.Dispose();
        _server = null;
    }

    private static bool IsLocalHost(string host) =>
        host is "127.0.0.1" or "localhost" or "::1" or "";

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            // Regroupe les rafales de notifications pendant la détection.
            await Task.Delay(600).ConfigureAwait(false);
            Interlocked.Exchange(ref _refreshQueued, 0);
            try { await RefreshDevicesAsync(_cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { _log?.Invoke($"[OpenRGB] Rafraîchissement échoué : {ex.Message}"); }
        });
    }

    private async Task RefreshDevicesAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = _client ?? throw new IOException("Non connecté");
            var list = await ReadDevicesAsync(client, ct).ConfigureAwait(false);

            // Applique les tailles de zones mémorisées (headers ARGB), puis relit les appareils modifiés.
            var sizes = _zoneSizes();
            bool resized = false;
            foreach (var d in list)
            {
                foreach (var z in d.Zones)
                {
                    if (!z.IsResizable) continue;
                    if (sizes.TryGetValue(ZoneKeys.Zone(d.Key, z.Index, z.Name), out int want)
                        && want != z.LedCount && want >= z.MinLeds && want <= z.MaxLeds)
                    {
                        await client.ResizeZoneAsync(d.BackendIndex, z.Index, want, ct).ConfigureAwait(false);
                        resized = true;
                    }
                }
            }
            if (resized)
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
                list = await ReadDevicesAsync(client, ct).ConfigureAwait(false);
            }

            foreach (var d in list)
                await client.SetCustomModeAsync(d.BackendIndex, ct).ConfigureAwait(false);

            lock (_frameLock) _pendingFrames.Clear();
            _devices = list;
            int leds = list.Sum(d => d.LedCount);
            SetState(BackendState.Connected, list.Count == 0
                ? "Connecté, aucun appareil détecté pour l'instant (détection en cours ?)"
                : $"Connecté : {list.Count} appareil(s), {leds} LEDs");
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _refreshLock.Release(); }
    }

    private static async Task<List<RgbDevice>> ReadDevicesAsync(OpenRgbClient client, CancellationToken ct)
    {
        int count = await client.GetControllerCountAsync(ct).ConfigureAwait(false);
        var list = new List<RgbDevice>(count);
        var seen = new Dictionary<string, int>();
        for (int i = 0; i < count; i++)
        {
            var c = await client.GetControllerAsync(i, ct).ConfigureAwait(false);
            var zones = new List<RgbZone>();
            int start = 0;
            for (int z = 0; z < c.Zones.Count; z++)
            {
                var zd = c.Zones[z];
                zones.Add(new RgbZone
                {
                    Index = z,
                    Name = string.IsNullOrWhiteSpace(zd.Name) ? $"Zone {z + 1}" : zd.Name,
                    Kind = OpenRgbProtocol.MapZone(zd.Type),
                    StartIndex = start,
                    LedCount = (int)zd.LedsCount,
                    MinLeds = (int)zd.LedsMin,
                    MaxLeds = (int)zd.LedsMax,
                });
                start += (int)zd.LedsCount;
            }

            // Clé stable : deux barrettes de RAM identiques ne diffèrent souvent que par leur emplacement.
            string baseKey = $"orgb:{c.Name}|{c.Location}|{c.Serial}";
            seen[baseKey] = seen.GetValueOrDefault(baseKey) + 1;
            string key = seen[baseKey] == 1 ? baseKey : $"{baseKey}#{seen[baseKey]}";

            list.Add(new RgbDevice
            {
                Key = key,
                BackendIndex = i,
                Name = c.Name,
                Vendor = c.Vendor,
                Description = c.Description,
                Location = c.Location,
                Serial = c.Serial,
                Firmware = c.Version,
                Kind = OpenRgbProtocol.MapType(c.Type),
                Zones = zones,
                LedCount = c.ColorCount,
                HardwareModes = c.Modes.Select(m => m.Name).ToList(),
                HasDirectMode = c.Modes.Any(m => m.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase)),
            });
        }
        return list;
    }

    public void SubmitFrame(RgbDevice device, ReadOnlySpan<Rgb> colors)
    {
        if (device.LedCount == 0 || colors.Length != device.LedCount) return;
        lock (_frameLock)
        {
            if (!_pendingFrames.TryGetValue(device.BackendIndex, out var buf) || buf.Length != colors.Length)
                _pendingFrames[device.BackendIndex] = buf = new Rgb[colors.Length];
            colors.CopyTo(buf);
            _dirty.Add(device.BackendIndex);
        }
        if (_frameSignal.CurrentCount == 0)
        {
            try { _frameSignal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    private readonly HashSet<int> _dirty = [];

    private async Task SendLoopAsync(CancellationToken ct)
    {
        var work = new List<(int idx, Rgb[] colors)>();
        while (!ct.IsCancellationRequested)
        {
            try { await _frameSignal.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            work.Clear();
            lock (_frameLock)
            {
                foreach (int idx in _dirty)
                    if (_pendingFrames.TryGetValue(idx, out var buf)) work.Add((idx, (Rgb[])buf.Clone()));
                _dirty.Clear();
            }

            var client = _client;
            if (client is null || !client.IsConnected || State != BackendState.Connected) continue;
            foreach (var (idx, colors) in work)
            {
                try { await client.UpdateLedsAsync(idx, colors, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception) { break; } // la boucle principale gère la reconnexion
            }
        }
    }

    public Task RescanAsync()
    {
        var c = _client;
        if (c is null) return Task.CompletedTask;
        // Le rescan matériel complet n'existe qu'à partir du protocole 5 ; sinon on relit simplement la liste.
        return c.Protocol >= 5 ? c.RequestRescanAsync(_cts.Token) : RefreshDevicesAsync(_cts.Token);
    }

    public async Task ReapplyAsync()
    {
        var c = _client;
        if (c is null) return;
        foreach (var d in _devices)
            await c.SetCustomModeAsync(d.BackendIndex, _cts.Token).ConfigureAwait(false);
    }

    public async Task ResizeZoneAsync(RgbDevice device, int zoneIndex, int ledCount)
    {
        var c = _client;
        if (c is null) return;
        await c.ResizeZoneAsync(device.BackendIndex, zoneIndex, ledCount, _cts.Token).ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false);
        await RefreshDevicesAsync(_cts.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_client is { } c) await c.DisposeAsync().ConfigureAwait(false);
        foreach (var t in new[] { _mainLoop, _sendLoop })
        {
            if (t is null) continue;
            try { await t.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        if (_settings.StopServerOnExit) KillServer();
        _cts.Dispose();
    }
}
