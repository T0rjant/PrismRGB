// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace PrismRGB.Core.Devices;

public enum DeviceKind
{
    Motherboard, Dram, Gpu, Cooler, LedStrip, Keyboard, Mouse, Mousemat, Headset, HeadsetStand,
    Gamepad, Light, Speaker, Virtual, Storage, Case, Microphone, Accessory, Keypad, Unknown,
}

public enum ZoneKind { Single, Linear, Matrix }

public sealed class RgbZone
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required ZoneKind Kind { get; init; }
    /// <summary>Index de la première LED de la zone dans le tableau de couleurs de l'appareil.</summary>
    public required int StartIndex { get; init; }
    public required int LedCount { get; init; }
    public int MinLeds { get; init; }
    public int MaxLeds { get; init; }
    /// <summary>Donnée libre du pilote (identifiant interne de la zone).</summary>
    public int Tag { get; init; }

    /// <summary>Zone dont le nombre de LEDs est réglable (typiquement un header ARGB de carte mère).</summary>
    public bool IsResizable => MaxLeds > MinLeds;
}

public sealed class RgbDevice
{
    /// <summary>Identifiant stable entre deux sessions (utilisé dans les profils).</summary>
    public required string Key { get; init; }
    /// <summary>Index propre au backend.</summary>
    public required int BackendIndex { get; init; }
    public required string Name { get; init; }
    public string Vendor { get; init; } = "";
    public string Description { get; init; } = "";
    public string Location { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Firmware { get; init; } = "";
    public DeviceKind Kind { get; init; } = DeviceKind.Unknown;
    public required IReadOnlyList<RgbZone> Zones { get; init; }
    public required int LedCount { get; init; }
    public IReadOnlyList<string> HardwareModes { get; init; } = [];
    public bool HasDirectMode { get; init; }

    public override string ToString() => $"{Name} ({LedCount} LEDs)";

    public static string KindLabel(DeviceKind k) => k switch
    {
        DeviceKind.Motherboard => "Carte mère",
        DeviceKind.Dram => "RAM",
        DeviceKind.Gpu => "Carte graphique",
        DeviceKind.Cooler => "Refroidissement",
        DeviceKind.LedStrip => "Bande LED",
        DeviceKind.Keyboard => "Clavier",
        DeviceKind.Mouse => "Souris",
        DeviceKind.Mousemat => "Tapis",
        DeviceKind.Headset => "Casque",
        DeviceKind.HeadsetStand => "Support casque",
        DeviceKind.Gamepad => "Manette",
        DeviceKind.Light => "Lumière",
        DeviceKind.Speaker => "Enceinte",
        DeviceKind.Virtual => "Virtuel",
        DeviceKind.Storage => "Stockage",
        DeviceKind.Case => "Boîtier",
        DeviceKind.Microphone => "Micro",
        DeviceKind.Accessory => "Accessoire",
        DeviceKind.Keypad => "Pavé",
        _ => "Autre",
    };
}
