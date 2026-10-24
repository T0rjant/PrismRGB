using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrismRGB.Core.Config;
using PrismRGB.Core.Devices;
using PrismRGB.Core.OpenRgb;
using PrismRGB.Core.Platform;

namespace PrismRGB.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _statusTimer;
    private bool _loading;
    private int _conflictTick;

    public MainViewModel(AppHost host)
    {
        _host = host;
        Editor = new EffectEditorViewModel(EffectEdited);

        foreach (var p in host.Config.Profiles) Profiles.Add(new ProfileItem(p));

        _loading = true;
        var cfg = host.Config;
        _openRgbPath = cfg.OpenRgb.ExePath ?? OpenRgbLocator.Find(null) ?? "";
        _autoLaunch = cfg.OpenRgb.AutoLaunch;
        _serverHost = cfg.OpenRgb.Host;
        _port = cfg.OpenRgb.Port;
        _stopServerOnExit = cfg.OpenRgb.StopServerOnExit;
        _targetFps = cfg.TargetFps;
        _minimizeToTray = cfg.MinimizeToTray;
        _startMinimized = cfg.StartMinimized;
        _demoMode = cfg.DemoMode;
        _startWithWindows = Autostart.IsEnabled();
        _loading = false;

        SelectedProfile = Profiles.FirstOrDefault(p => p.Name == cfg.ActiveProfile) ?? Profiles.FirstOrDefault();

        host.DevicesChanged += (_, _) => Dispatcher.UIThread.Post(RebuildDevices);
        host.StatusChanged += (_, _) => Dispatcher.UIThread.Post(RefreshStatus);
        host.BackendChanged += (_, _) => Dispatcher.UIThread.Post(() => { RebuildDevices(); RefreshStatus(); });

        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        RefreshConflicts();
    }

    public AppHost Host => _host;
    public EffectEditorViewModel Editor { get; }
    public ObservableCollection<ProfileItem> Profiles { get; } = [];
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<EditTarget> Targets { get; } = [];
    public ObservableCollection<ConflictItem> Conflicts { get; } = [];

    [ObservableProperty] private ProfileItem? _selectedProfile;
    [ObservableProperty] private EditTarget? _selectedTarget;
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string _statusText = "Démarrage...";
    [ObservableProperty] private IBrush _statusBrush = Brushes.Gray;
    [ObservableProperty] private string _backendName = "";
    [ObservableProperty] private string _fpsText = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _syncAcrossDevices;

    // Paramètres
    [ObservableProperty] private string _openRgbPath;
    [ObservableProperty] private bool _autoLaunch;
    [ObservableProperty] private string _serverHost;
    [ObservableProperty] private decimal? _port;
    [ObservableProperty] private bool _stopServerOnExit;
    [ObservableProperty] private int _targetFps;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _demoMode;
    [ObservableProperty] private bool _startWithWindows;

    public bool IsGlobalTarget => SelectedTarget?.ZoneKey is null;
    public bool HasNoDevices => Devices.Count == 0;
    public bool HasConflicts => Conflicts.Count > 0;
    public bool AutostartSupported => Autostart.IsSupported;
    public string ConfigPath => _host.Store.FilePath;
    public string Version => $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}";

    private Profile Profile => _host.Config.GetActiveProfile();

    // ---------- Profils ----------

    partial void OnSelectedProfileChanged(ProfileItem? value)
    {
        if (value is null) return;
        _host.Config.ActiveProfile = value.Profile.Name;
        _loading = true;
        SyncAcrossDevices = value.Profile.SyncAcrossDevices;
        _loading = false;
        _host.ProfileChanged();
        RebuildDevices();
        RebuildTargets(null);
    }

    partial void OnSyncAcrossDevicesChanged(bool value)
    {
        if (_loading) return;
        Profile.SyncAcrossDevices = value;
        _host.ProfileChanged();
    }

    public void RenameSelectedProfile(string name)
    {
        if (SelectedProfile is null) return;
        name = name.Trim();
        if (name.Length == 0 || Profiles.Any(p => p != SelectedProfile && p.Name == name)) return;
        SelectedProfile.Name = name;
        _host.Config.ActiveProfile = name;
        _host.SaveSoon();
    }

    private string UniqueName(string baseName)
    {
        string name = baseName;
        for (int i = 2; Profiles.Any(p => p.Name == name); i++) name = $"{baseName} {i}";
        return name;
    }

    [RelayCommand]
    private void NewProfile()
    {
        var p = new Profile { Name = UniqueName("Nouveau profil") };
        AddProfile(p);
    }

    [RelayCommand]
    private void DuplicateProfile()
    {
        if (SelectedProfile is null) return;
        var p = SelectedProfile.Profile.Clone();
        p.Name = UniqueName(SelectedProfile.Name + " (copie)");
        AddProfile(p);
    }

    private void AddProfile(Profile p)
    {
        _host.Config.Profiles.Add(p);
        var item = new ProfileItem(p);
        Profiles.Add(item);
        SelectedProfile = item;
        _host.SaveSoon();
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is null || Profiles.Count <= 1) return;
        var item = SelectedProfile;
        int idx = Profiles.IndexOf(item);
        _host.Config.Profiles.Remove(item.Profile);
        Profiles.Remove(item);
        SelectedProfile = Profiles[Math.Clamp(idx, 0, Profiles.Count - 1)];
        _host.SaveSoon();
    }

    public void SelectProfileByName(string name)
    {
        var p = Profiles.FirstOrDefault(x => x.Name == name);
        if (p is not null) SelectedProfile = p;
    }

    // ---------- Cible d'édition (global ou zone avec effet propre) ----------

    private void RebuildTargets(string? select)
    {
        Targets.Clear();
        Targets.Add(new EditTarget("Tous les appareils (effet du profil)", null));
        foreach (var d in Devices)
            foreach (var z in d.Zones.Where(z => z.HasOverride))
                Targets.Add(new EditTarget($"{d.Name}  ›  {z.Name}", z.Key));
        SelectedTarget = Targets.FirstOrDefault(t => t.ZoneKey == select) ?? Targets[0];
    }

    partial void OnSelectedTargetChanged(EditTarget? value)
    {
        OnPropertyChanged(nameof(IsGlobalTarget));
        if (value is null) return;
        var settings = value.ZoneKey is { } key && Profile.Zones.TryGetValue(key, out var z) && z.Override is { } o
            ? o
            : Profile.Global;
        Editor.Load(settings);
    }

    private void EffectEdited()
    {
        _host.ProfileChanged();
        foreach (var d in Devices)
            foreach (var z in d.Zones) z.RefreshOverride();
    }

    // ---------- Appareils ----------

    private void RebuildDevices()
    {
        var profile = Profile;
        var backend = _host.Backend;
        Devices.Clear();
        if (backend is not null)
        {
            foreach (var d in backend.Devices)
            {
                var zones = d.Zones.Select(z =>
                {
                    string key = ZoneKeys.Zone(d.Key, z.Index, z.Name);
                    int? size;
                    lock (_host.Config.ZoneSizes) size = _host.Config.ZoneSizes.TryGetValue(key, out var s) ? s : null;
                    return new ZoneViewModel(this, d, z, profile.GetZone(key), size);
                }).ToList();
                Devices.Add(new DeviceViewModel(d, zones));
            }
        }
        RefreshPositions();
        OnPropertyChanged(nameof(HasNoDevices));
        _host.ProfileChanged();
        RebuildTargets(SelectedTarget?.ZoneKey);
    }

    private List<ZoneViewModel> OrderedZones() =>
        Devices.SelectMany(d => d.Zones).OrderBy(z => z.Order).ToList();

    private void RefreshPositions()
    {
        var all = OrderedZones();
        for (int i = 0; i < all.Count; i++)
        {
            all[i].SetOrder(i);
            all[i].Position = $"n°{i + 1}";
        }
    }

    internal void ZoneEdited() => _host.ProfileChanged();

    internal void MoveZone(ZoneViewModel zone, int delta)
    {
        var all = OrderedZones();
        int i = all.IndexOf(zone);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= all.Count) return;
        (all[i], all[j]) = (all[j], all[i]);
        for (int k = 0; k < all.Count; k++) all[k].SetOrder(k);
        RefreshPositions();
        _host.ProfileChanged();
    }

    internal void CustomizeZone(ZoneViewModel zone)
    {
        var cfg = Profile.GetZone(zone.Key);
        cfg.Override ??= Profile.Global.Clone();
        zone.RefreshOverride();
        _host.ProfileChanged();
        RebuildTargets(zone.Key);
        SelectedTab = 0;
    }

    internal void ClearZoneOverride(ZoneViewModel zone)
    {
        Profile.GetZone(zone.Key).Override = null;
        zone.RefreshOverride();
        _host.ProfileChanged();
        RebuildTargets(null);
    }

    internal async Task ResizeZoneAsync(ZoneViewModel zone, int size)
    {
        size = Math.Clamp(size, zone.Zone.MinLeds, zone.Zone.MaxLeds);
        _host.SetZoneSize(zone.Key, size);
        if (_host.Backend is { } b)
        {
            try { await b.ResizeZoneAsync(zone.Device, zone.Zone.Index, size); }
            catch (Exception ex) { Message = $"Redimensionnement impossible : {ex.Message}"; }
        }
    }

    [RelayCommand]
    private void EnableAllZones()
    {
        foreach (var d in Devices)
            foreach (var z in d.Zones) z.Enabled = true;
    }

    // ---------- État / maintenance ----------

    private void Tick()
    {
        FpsText = $"{_host.Engine.MeasuredFps:0} images/s";
        if (++_conflictTick % 15 == 0) RefreshConflicts();
    }

    private void RefreshStatus()
    {
        var b = _host.Backend;
        if (b is null) return;
        BackendName = b.Name;
        StatusText = b.StatusText;
        StatusBrush = b.State switch
        {
            BackendState.Connected => new SolidColorBrush(Color.Parse("#3DDC84")),
            BackendState.Error => new SolidColorBrush(Color.Parse("#FF5468")),
            BackendState.Stopped => Brushes.Gray,
            _ => new SolidColorBrush(Color.Parse("#FFB020")),
        };
    }

    [RelayCommand]
    private async Task Reapply()
    {
        if (_host.Backend is { } b)
        {
            try { await b.ReapplyAsync(); } catch (Exception ex) { Message = ex.Message; }
        }
        _host.Engine.Resend();
        Message = "Mode direct réappliqué sur tous les appareils.";
    }

    [RelayCommand]
    private async Task Rescan()
    {
        if (_host.Backend is { } b)
        {
            try { await b.RescanAsync(); Message = "Liste du matériel relue."; }
            catch (Exception ex) { Message = $"Rescan impossible : {ex.Message}"; }
        }
    }

    [RelayCommand]
    private async Task RestartServer()
    {
        if (_host.Backend is OpenRgbBackend o)
        {
            o.RestartServer();
            Message = "Serveur OpenRGB relancé (détection en cours).";
        }
        else
        {
            await _host.StartBackendAsync();
        }
    }

    [RelayCommand]
    private void RefreshConflicts()
    {
        var found = ConflictDetector.Scan();
        Conflicts.Clear();
        foreach (var c in found) Conflicts.Add(new ConflictItem(this, c));
        OnPropertyChanged(nameof(HasConflicts));
    }

    internal void KillConflict(ConflictItem item)
    {
        Message = ConflictDetector.TryKill(item.App, out var err)
            ? $"{item.App.Label} fermé. Pense à le retirer du démarrage de Windows."
            : $"Impossible de fermer {item.App.Label} : {err}";
        RefreshConflicts();
        _ = Reapply();
    }

    [RelayCommand]
    private void Diagnostic()
    {
        try
        {
            var report = Diagnostics.BuildReport(_host.Backend is { } b ? [b] : [], _host.Store.FilePath)
                + Environment.NewLine + "--- Journal ---" + Environment.NewLine + _host.LogText();
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PrismRGB-diagnostic.txt");
            File.WriteAllText(path, report);
            Shell.Open(path);
            Message = $"Rapport enregistré : {path}";
        }
        catch (Exception ex) { Message = $"Diagnostic impossible : {ex.Message}"; }
    }

    [RelayCommand]
    private void OpenConfigFolder() => Shell.Open(_host.Store.Directory);

    [RelayCommand]
    private void OpenOpenRgbSite() => Shell.Open("https://openrgb.org/releases.html");

    // ---------- Paramètres ----------

    partial void OnOpenRgbPathChanged(string value)
    {
        if (_loading) return;
        _host.Config.OpenRgb.ExePath = string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
        _host.SaveSoon();
    }

    partial void OnAutoLaunchChanged(bool value)
    {
        if (_loading) return;
        _host.Config.OpenRgb.AutoLaunch = value;
        _host.SaveSoon();
    }

    partial void OnServerHostChanged(string value)
    {
        if (_loading) return;
        _host.Config.OpenRgb.Host = value.Trim();
        _host.SaveSoon();
    }

    partial void OnPortChanged(decimal? value)
    {
        if (_loading || value is null) return;
        _host.Config.OpenRgb.Port = (int)value;
        _host.SaveSoon();
    }

    partial void OnStopServerOnExitChanged(bool value)
    {
        if (_loading) return;
        _host.Config.OpenRgb.StopServerOnExit = value;
        _host.SaveSoon();
    }

    partial void OnTargetFpsChanged(int value)
    {
        if (_loading) return;
        _host.Config.TargetFps = value;
        _host.Engine.TargetFps = value;
        _host.SaveSoon();
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (_loading) return;
        _host.Config.MinimizeToTray = value;
        _host.SaveSoon();
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (_loading) return;
        _host.Config.StartMinimized = value;
        _host.SaveSoon();
    }

    partial void OnDemoModeChanged(bool value)
    {
        if (_loading) return;
        _host.Config.DemoMode = value;
        _host.SaveSoon();
        _ = _host.StartBackendAsync();
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        var exe = Environment.ProcessPath ?? "";
        bool ok = value ? Autostart.Enable(exe, out var output) : Autostart.Disable(out output);
        Message = ok
            ? (value ? "PrismRGB démarrera avec Windows (droits admin, sans invite)." : "Démarrage automatique désactivé.")
            : $"Échec : {output.Trim()}";
    }

    [RelayCommand]
    private async Task ApplyConnection()
    {
        await _host.StartBackendAsync();
        Message = "Connexion relancée avec les nouveaux paramètres.";
    }

    public void Start() => _statusTimer.Start();
}

internal static class Shell
{
    public static void Open(string target)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start("open", target);
            else Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch { }
    }
}
