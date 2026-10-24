// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

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

    private readonly ZoneLook _look;

    public ZoneViewModel(MainViewModel main, RgbDevice device, RgbZone zone, ZoneConfig cfg, int? desiredSize, ZoneLook look)
    {
        _main = main;
        _cfg = cfg;
        _look = look;
        Device = device;
        Zone = zone;
        Key = ZoneKeys.Zone(device.Key, zone.Index, zone.Name);
        _enabled = cfg.Enabled;
        _reverse = cfg.Reverse;
        _desiredSize = desiredSize ?? zone.LedCount;
        _selectedLook = Looks.First(l => l.Kind == look.Kind);
        _fans = look.Fans;
        _loading = false;
    }

    public static IReadOnlyList<LookChoice> Looks { get; } =
    [
        new(LookKind.Auto, "Automatique"),
        new(LookKind.Strip, "Bande LED"),
        new(LookKind.Fans, "Ventilateur(s)"),
    ];

    [ObservableProperty] private LookChoice _selectedLook;
    [ObservableProperty] private decimal? _fans;

    public bool IsFans => SelectedLook.Kind == LookKind.Fans;
    public bool CanChooseLook => Zone.LedCount > 1 || Zone.IsResizable;

    partial void OnSelectedLookChanged(LookChoice value)
    {
        OnPropertyChanged(nameof(IsFans));
        if (_loading) return;
        _look.Kind = value.Kind;
        _main.LookEdited(Key, _look);
    }

    partial void OnFansChanged(decimal? value)
    {
        if (_loading || value is null) return;
        _look.Fans = Math.Clamp((int)value, 1, 12);
        _main.LookEdited(Key, _look);
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

public sealed record LookChoice(LookKind Kind, string Label);

public sealed record NavItem(string Label, string Icon);

public sealed class ScanEntryViewModel(Core.Platform.ScanEntry entry)
{
    public Core.Platform.ScanEntry Entry { get; } = entry;
    public string Category => Entry.Category;
    public string Name => Entry.Name;
    public string Detail => Entry.Detail;
    public string Advice => Entry.Advice;
    public bool HasAdvice => !string.IsNullOrWhiteSpace(Entry.Advice);

    public string StatusText => Entry.Status switch
    {
        Core.Platform.ScanStatus.Controlled => "Piloté",
        Core.Platform.ScanStatus.NotYet => "Pilote à venir",
        Core.Platform.ScanStatus.NeedsLowLevel => "Accès bas niveau requis",
        Core.Platform.ScanStatus.Problem => "Problème",
        _ => "Info",
    };

    public Avalonia.Media.IBrush StatusBrush => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(Entry.Status switch
    {
        Core.Platform.ScanStatus.Controlled => "#2FD27A",
        Core.Platform.ScanStatus.Problem => "#FF5468",
        Core.Platform.ScanStatus.Info => "#8A90A0",
        _ => "#FFB020",
    }));

    public Avalonia.Media.IBrush StatusBackground => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(Entry.Status switch
    {
        Core.Platform.ScanStatus.Controlled => "#152C21",
        Core.Platform.ScanStatus.Problem => "#331820",
        Core.Platform.ScanStatus.Info => "#1E2129",
        _ => "#2E2412",
    }));

    public string Icon => Entry.Category switch
    {
        "Carte mère" => Icons.Board,
        "RAM" => Icons.Ram,
        "Carte graphique" => Icons.Gpu,
        _ => Icons.Usb,
    };
}

/// <summary>Icônes vectorielles (24x24) de l'interface.</summary>
public static class Icons
{
    public const string Lighting = "M12 2 L14.2 9.8 L22 12 L14.2 14.2 L12 22 L9.8 14.2 L2 12 L9.8 9.8 Z";
    public const string Devices = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18zm0 2a7 7 0 1 1 0 14a7 7 0 1 1 0-14zm0 4.5a2.5 2.5 0 1 0 0 5a2.5 2.5 0 1 0 0-5zM11 5.6c-1.8.3-3.6 1.6-4.2 3.4l3 1.2c.3-.7.8-1.2 1.5-1.5zm6.4 4.6l-3 .9c.2.7.1 1.5-.3 2.1l2.7 1.6c.9-1.4 1.1-3.1.6-4.6zM8.3 15.2l1.9-2.4c.5.5 1.2.8 1.9.8l-.1 3.2c-1.4-.1-2.7-.6-3.7-1.6z";
    public const string Scan = "M10 2a8 8 0 1 0 4.9 14.3l5.4 5.4 1.4-1.4-5.4-5.4A8 8 0 0 0 10 2zm0 2a6 6 0 1 1 0 12a6 6 0 0 1 0-12z";
    public const string Settings = "M3 5h9v2H3zM16 5h5v2h-5zM12 3h3v6h-3zM3 11h3v2H3zM10 11h11v2H10zM6 9h3v6H6zM3 17h11v2H3zM18 17h3v2h-3zM14 15h3v6h-3z";
    public const string Board = "M4 3h16v18H4zM6 5v14h12V5zM8 7h4v4H8zM14 7h2v2h-2zM14 11h2v6h-2zM8 13h4v4H8z";
    public const string Ram = "M3 7h18v8H3zM5 9v4h2V9zm4 0v4h2V9zm4 0v4h2V9zm4 0v4h2V9zM4 16h2v2H4zm4 0h2v2H8zm4 0h2v2h-2zm4 0h2v2h-2z";
    public const string Gpu = "M2 6h20v11H2zM4 8v7h16V8zM8.5 9a2.5 2.5 0 1 0 0 5a2.5 2.5 0 1 0 0-5zm7 0a2.5 2.5 0 1 0 0 5a2.5 2.5 0 1 0 0-5zM4 18h3v2H4z";
    public const string Usb = "M11 2h2v12.3l3-2.3V9h-1V6h4v3h-1v4l-5 3.8V18a2 2 0 1 1-2 0v-2.2L6 13V10.7A1.5 1.5 0 1 1 8 10.7v1.3l3 2.3z";
    public const string Power = "M11 2h2v10h-2zM6.3 5.3l1.4 1.4A6 6 0 1 0 16.3 6.7l1.4-1.4A8 8 0 1 1 6.3 5.3z";
    public const string Sun = "M12 7a5 5 0 1 0 0 10a5 5 0 1 0 0-10zM11 1h2v3h-2zM11 20h2v3h-2zM1 11h3v2H1zM20 11h3v2h-3zM4.2 5.6l1.4-1.4 2.1 2.1-1.4 1.4zM16.3 17.7l1.4-1.4 2.1 2.1-1.4 1.4zM4.2 18.4l2.1-2.1 1.4 1.4-2.1 2.1zM16.3 6.3l2.1-2.1 1.4 1.4-2.1 2.1z";
    public const string Plus = "M11 4h2v16h-2zM4 11h16v2H4z";
}

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
