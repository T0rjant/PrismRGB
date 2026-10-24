using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrismRGB.App.Controls;
using PrismRGB.Core.Config;
using PrismRGB.Core.Devices;
using PrismRGB.Core.Effects;
using PrismRGB.Core.Platform;

namespace PrismRGB.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _statusTimer;
    private bool _loading;
    private int _tick;

    public MainViewModel(AppHost host)
    {
        _host = host;
        Editor = new EffectEditorViewModel(EffectEdited);

        foreach (var p in host.Config.Profiles) Profiles.Add(new ProfileItem(p));

        _loading = true;
        var cfg = host.Config;
        _targetFps = cfg.TargetFps;
        _minimizeToTray = cfg.MinimizeToTray;
        _startMinimized = cfg.StartMinimized;
        _demoMode = cfg.DemoMode;
        _allowUntestedBoards = cfg.AllowUntestedBoards;
        _masterBrightness = cfg.MasterBrightness;
        _lightsOn = cfg.LightsOn;
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
    public ObservableCollection<ScanEntryViewModel> ScanEntries { get; } = [];

    public IReadOnlyList<NavItem> Nav { get; } =
    [
        new("Éclairage", Icons.Lighting),
        new("Appareils", Icons.Devices),
        new("Matériel", Icons.Scan),
        new("Réglages", Icons.Settings),
    ];

    [ObservableProperty] private int _selectedPage;
    [ObservableProperty] private ProfileItem? _selectedProfile;
    [ObservableProperty] private EditTarget? _selectedTarget;
    [ObservableProperty] private string _statusText = "Démarrage...";
    [ObservableProperty] private IBrush _statusBrush = Brushes.Gray;
    [ObservableProperty] private string _fpsText = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _syncAcrossDevices;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";
    [ObservableProperty] private bool _scanning;
    [ObservableProperty] private string _scanReport = "";

    // Barre du haut
    [ObservableProperty] private int _masterBrightness;
    [ObservableProperty] private bool _lightsOn;

    // Réglages
    [ObservableProperty] private int _targetFps;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _demoMode;
    [ObservableProperty] private bool _allowUntestedBoards;
    [ObservableProperty] private bool _startWithWindows;

    public bool IsLightingPage => SelectedPage == 0;
    public bool IsDevicesPage => SelectedPage == 1;
    public bool IsHardwarePage => SelectedPage == 2;
    public bool IsSettingsPage => SelectedPage == 3;
    public bool IsGlobalTarget => SelectedTarget?.ZoneKey is null;
    public bool HasOverrides => Targets.Count > 1;
    public bool HasNoDevices => Devices.Count == 0;
    public bool HasConflicts => Conflicts.Count > 0;
    public bool AutostartSupported => Autostart.IsSupported;
    public string ConfigPath => _host.Store.FilePath;
    public string Version => $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}";
    public string SelectedEffectName => Editor.SelectedEffect.Name;
    public string SelectedEffectDescription => Editor.SelectedEffect.Description;
    public string DeviceSummary
    {
        get
        {
            int n = Devices.Count, leds = Devices.Sum(d => d.Device.LedCount);
            return n == 0 ? "Aucun appareil piloté" : $"{n} appareil{(n > 1 ? "s" : "")} · {leds} LEDs";
        }
    }

    private Profile Profile => _host.Config.GetActiveProfile();

    partial void OnSelectedPageChanged(int value)
    {
        OnPropertyChanged(nameof(IsLightingPage));
        OnPropertyChanged(nameof(IsDevicesPage));
        OnPropertyChanged(nameof(IsHardwarePage));
        OnPropertyChanged(nameof(IsSettingsPage));
        if (value == 2 && ScanEntries.Count == 0) _ = ScanHardware();
    }

    // ---------- Profils ----------

    partial void OnSelectedProfileChanged(ProfileItem? value)
    {
        if (value is null) return;
        IsRenaming = false;
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

    [RelayCommand]
    private void StartRename()
    {
        if (SelectedProfile is null) return;
        RenameText = SelectedProfile.Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        IsRenaming = false;
        if (SelectedProfile is null) return;
        var name = RenameText.Trim();
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
    private void NewProfile() => AddProfile(new Profile { Name = UniqueName("Nouveau profil") });

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
        Targets.Add(new EditTarget("Tout le PC", null));
        foreach (var d in Devices)
            foreach (var z in d.Zones.Where(z => z.HasOverride))
                Targets.Add(new EditTarget($"{z.Name}  ({d.Name})", z.Key));
        SelectedTarget = Targets.FirstOrDefault(t => t.ZoneKey == select) ?? Targets[0];
        OnPropertyChanged(nameof(HasOverrides));
    }

    partial void OnSelectedTargetChanged(EditTarget? value)
    {
        OnPropertyChanged(nameof(IsGlobalTarget));
        if (value is null) return;
        var settings = value.ZoneKey is { } key && Profile.Zones.TryGetValue(key, out var z) && z.Override is { } o
            ? o
            : Profile.Global;
        Editor.Load(settings);
        EffectThumb.Shared = settings;
        OnPropertyChanged(nameof(SelectedEffectName));
        OnPropertyChanged(nameof(SelectedEffectDescription));
    }

    private void EffectEdited()
    {
        _host.ProfileChanged();
        OnPropertyChanged(nameof(SelectedEffectName));
        OnPropertyChanged(nameof(SelectedEffectDescription));
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
                    if (!_host.Config.ZoneLooks.TryGetValue(key, out var look)) look = new ZoneLook();
                    return new ZoneViewModel(this, d, z, profile.GetZone(key), size ?? z.LedCount, look);
                }).ToList();
                Devices.Add(new DeviceViewModel(d, zones));
            }
        }
        RefreshPositions();
        OnPropertyChanged(nameof(HasNoDevices));
        OnPropertyChanged(nameof(DeviceSummary));
        _host.ProfileChanged();
        RebuildTargets(SelectedTarget?.ZoneKey);
    }

    public ZoneLook? LookFor(string key) => _host.Config.ZoneLooks.GetValueOrDefault(key);

    /// <summary>Levé quand l'apparence d'une zone change (l'aperçu doit se redessiner).</summary>
    public event EventHandler? LooksChanged;

    internal void LookEdited(string key, ZoneLook look)
    {
        _host.Config.ZoneLooks[key] = look;
        _host.SaveSoon();
        LooksChanged?.Invoke(this, EventArgs.Empty);
    }

    private List<ZoneViewModel> OrderedZones() =>
        Devices.SelectMany(d => d.Zones).OrderBy(z => z.Order).ToList();

    private void RefreshPositions()
    {
        var all = OrderedZones();
        for (int i = 0; i < all.Count; i++)
        {
            all[i].SetOrder(i);
            all[i].Position = $"{i + 1}";
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
        SelectedPage = 0;
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
            try
            {
                await b.ResizeZoneAsync(zone.Device, zone.Zone.Index, size);
                Message = $"{zone.Name} : {size} LEDs.";
            }
            catch (Exception ex) { Message = $"Réglage impossible : {ex.Message}"; }
        }
    }

    [RelayCommand]
    private void EnableAllZones()
    {
        foreach (var d in Devices)
            foreach (var z in d.Zones) z.Enabled = true;
    }

    // ---------- Barre du haut ----------

    partial void OnMasterBrightnessChanged(int value)
    {
        if (_loading) return;
        _host.Config.MasterBrightness = value;
        _host.Engine.MasterBrightness = value / 100.0;
        _host.SaveSoon();
    }

    partial void OnLightsOnChanged(bool value)
    {
        if (_loading) return;
        _host.Config.LightsOn = value;
        _host.Engine.LightsOn = value;
        _host.Engine.Resend();
        _host.SaveSoon();
    }

    // ---------- État / maintenance ----------

    private void Tick()
    {
        FpsText = $"{_host.Engine.MeasuredFps:0} img/s";
        if (++_tick % 15 == 0) RefreshConflicts();
    }

    private void RefreshStatus()
    {
        var b = _host.Backend;
        if (b is null) return;
        StatusText = b.StatusText;
        StatusBrush = new SolidColorBrush(Color.Parse(b.State switch
        {
            BackendState.Connected when b.Devices.Count > 0 => "#2FD27A",
            BackendState.Error => "#FF5468",
            BackendState.Stopped => "#6B7180",
            _ => "#FFB020",
        }));
        OnPropertyChanged(nameof(DeviceSummary));
    }

    [RelayCommand]
    private async Task Reapply()
    {
        if (_host.Backend is { } b)
        {
            try { await b.ReapplyAsync(); } catch (Exception ex) { Message = ex.Message; }
        }
        _host.Engine.Resend();
        Message = "Contrôleurs reconfigurés et couleurs renvoyées.";
    }

    [RelayCommand]
    private async Task Rescan()
    {
        if (_host.Backend is { } b)
        {
            await b.RescanAsync();
            Message = "Nouvelle détection du matériel lancée.";
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
    private async Task ScanHardware()
    {
        if (Scanning) return;
        Scanning = true;
        try
        {
            var (entries, report) = await Task.Run(_host.ScanHardware);
            ScanEntries.Clear();
            foreach (var e in entries) ScanEntries.Add(new ScanEntryViewModel(e));
            ScanReport = report;
        }
        catch (Exception ex) { Message = $"Analyse impossible : {ex.Message}"; }
        finally { Scanning = false; }
    }

    [RelayCommand]
    private void SaveReport()
    {
        try
        {
            if (string.IsNullOrEmpty(ScanReport)) ScanReport = _host.ScanHardware().report;
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PrismRGB-rapport.txt");
            File.WriteAllText(path, ScanReport);
            Shell.Open(path);
            Message = $"Rapport enregistré sur le bureau : {Path.GetFileName(path)}";
        }
        catch (Exception ex) { Message = $"Enregistrement impossible : {ex.Message}"; }
    }

    [RelayCommand]
    private void OpenConfigFolder() => Shell.Open(_host.Store.Directory);

    [RelayCommand]
    private void DismissMessage() => Message = "";

    // ---------- Réglages ----------

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

    partial void OnAllowUntestedBoardsChanged(bool value)
    {
        if (_loading) return;
        _host.Config.AllowUntestedBoards = value;
        _host.SaveSoon();
        _ = _host.Backend?.RescanAsync();
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        var exe = Environment.ProcessPath ?? "";
        bool ok = value ? Autostart.Enable(exe, out var err) : Autostart.Disable(out err);
        Message = ok
            ? (value ? "PrismRGB démarrera avec Windows." : "Démarrage automatique désactivé.")
            : $"Échec : {err}";
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
