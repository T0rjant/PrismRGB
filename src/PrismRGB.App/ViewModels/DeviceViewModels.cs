using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrismRGB.Core.Config;
using PrismRGB.Core.Devices;

namespace PrismRGB.App.ViewModels;

public sealed class DeviceViewModel
{
    public DeviceViewModel(RgbDevice device, IEnumerable<ZoneViewModel> zones)
    {
        Device = device;
        Zones = new ObservableCollection<ZoneViewModel>(zones);
    }

    public RgbDevice Device { get; }
    public ObservableCollection<ZoneViewModel> Zones { get; }
    public string Name => Device.Name;
    public string Subtitle => string.Join("  ·  ", new[]
    {
        RgbDevice.KindLabel(Device.Kind),
        $"{Device.LedCount} LEDs",
        Device.Vendor,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public bool NoDirectMode => !Device.HasDirectMode;
}

public sealed partial class ZoneViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ZoneConfig _cfg;
    private bool _loading = true;

    public ZoneViewModel(MainViewModel main, RgbDevice device, RgbZone zone, ZoneConfig cfg, int? desiredSize)
    {
        _main = main;
        _cfg = cfg;
        Device = device;
        Zone = zone;
        Key = ZoneKeys.Zone(device.Key, zone.Index, zone.Name);
        _enabled = cfg.Enabled;
        _reverse = cfg.Reverse;
        _desiredSize = desiredSize ?? zone.LedCount;
        _loading = false;
    }

    public RgbDevice Device { get; }
    public RgbZone Zone { get; }
    public string Key { get; }
    public string Name => Zone.Name;
    public string LedText => Zone.LedCount == 1 ? "1 LED" : $"{Zone.LedCount} LEDs";
    public bool IsResizable => Zone.IsResizable;
    public decimal MinLeds => Zone.MinLeds;
    public decimal MaxLeds => Zone.MaxLeds;
    public bool HasOverride => _cfg.Override is not null;
    public string OverrideText => _cfg.Override is { } o ? $"Effet propre : {Core.Effects.EffectRegistry.Get(o.Effect).Name}" : "";
    public int Order => _cfg.Order;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _reverse;
    [ObservableProperty] private decimal? _desiredSize;
    [ObservableProperty] private string _position = "";

    partial void OnEnabledChanged(bool value)
    {
        if (_loading) return;
        _cfg.Enabled = value;
        _main.ZoneEdited();
    }

    partial void OnReverseChanged(bool value)
    {
        if (_loading) return;
        _cfg.Reverse = value;
        _main.ZoneEdited();
    }

    [RelayCommand]
    private Task ApplySize() => _main.ResizeZoneAsync(this, (int)(DesiredSize ?? Zone.LedCount));

    [RelayCommand]
    private void MoveUp() => _main.MoveZone(this, -1);

    [RelayCommand]
    private void MoveDown() => _main.MoveZone(this, +1);

    [RelayCommand]
    private void Customize() => _main.CustomizeZone(this);

    [RelayCommand]
    private void ClearOverride() => _main.ClearZoneOverride(this);

    internal void SetOrder(int order) => _cfg.Order = order;

    internal void RefreshOverride()
    {
        OnPropertyChanged(nameof(HasOverride));
        OnPropertyChanged(nameof(OverrideText));
    }
}

public sealed record EditTarget(string Label, string? ZoneKey);

public sealed partial class ProfileItem : ObservableObject
{
    public ProfileItem(Profile profile) => Profile = profile;
    public Profile Profile { get; }

    public string Name
    {
        get => Profile.Name;
        set
        {
            if (Profile.Name == value) return;
            Profile.Name = value;
            OnPropertyChanged();
        }
    }
}

public sealed partial class ConflictItem : ObservableObject
{
    private readonly MainViewModel _main;
    public ConflictItem(MainViewModel main, Core.Platform.ConflictingApp app)
    {
        _main = main;
        App = app;
    }
    public Core.Platform.ConflictingApp App { get; }
    public string Label => $"{App.Label}  ({App.ProcessName}.exe)";

    [RelayCommand]
    private void Kill() => _main.KillConflict(this);
}
