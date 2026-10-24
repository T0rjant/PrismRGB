using PrismRGB.Core.Effects;

namespace PrismRGB.Core.Config;

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public OpenRgbSettings OpenRgb { get; set; } = new();
    public int TargetFps { get; set; } = 30;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    /// <summary>Appareils simulés : pour essayer les effets sans matériel (ou sur Mac).</summary>
    public bool DemoMode { get; set; }
    public string? ActiveProfile { get; set; }
    public List<Profile> Profiles { get; set; } = [];
    /// <summary>Nombre de LEDs voulu pour les zones réglables (headers ARGB). Clé : <see cref="ZoneKeys.Zone"/>.</summary>
    public Dictionary<string, int> ZoneSizes { get; set; } = [];

    public Profile GetActiveProfile()
    {
        var p = Profiles.FirstOrDefault(x => x.Name == ActiveProfile) ?? Profiles.FirstOrDefault();
        if (p is null)
        {
            Profiles.AddRange(Profile.Defaults());
            p = Profiles[0];
        }
        ActiveProfile = p.Name;
        return p;
    }
}

public sealed class OpenRgbSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6742;
    /// <summary>Lance OpenRGB en mode serveur invisible si aucun serveur ne répond.</summary>
    public bool AutoLaunch { get; set; } = true;
    public string? ExePath { get; set; }
    public bool StopServerOnExit { get; set; } = true;
}

public sealed class Profile
{
    public string Name { get; set; } = "Profil";
    public EffectSettings Global { get; set; } = new();
    /// <summary>
    /// true : toutes les zones sont mises bout à bout et l'effet circule d'un appareil à l'autre.
    /// false : chaque zone joue l'effet de son côté.
    /// </summary>
    public bool SyncAcrossDevices { get; set; } = true;
    public Dictionary<string, ZoneConfig> Zones { get; set; } = [];

    public Profile Clone() => new()
    {
        Name = Name,
        Global = Global.Clone(),
        SyncAcrossDevices = SyncAcrossDevices,
        Zones = Zones.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
    };

    public ZoneConfig GetZone(string key)
    {
        if (!Zones.TryGetValue(key, out var z))
        {
            z = new ZoneConfig { Order = Zones.Count == 0 ? 0 : Zones.Values.Max(v => v.Order) + 1 };
            Zones[key] = z;
        }
        return z;
    }

    public static List<Profile> Defaults() =>
    [
        new() { Name = "Arc-en-ciel", Global = new EffectSettings { Effect = "rainbow", Speed = 45, Size = 40 } },
        new() { Name = "Météorite bleue", Global = new EffectSettings { Effect = "meteor", Speed = 55, Size = 45, Colors = [Rgb.FromHex("#00A2FF"), Rgb.FromHex("#7A00FF")] } },
        new() { Name = "Statique violet/cyan", Global = new EffectSettings { Effect = "multistatic", Smooth = true, Colors = [Rgb.FromHex("#8000FF"), Rgb.FromHex("#00E5FF")] } },
        new() { Name = "Respiration", Global = new EffectSettings { Effect = "breathing", Speed = 35, Colors = [Rgb.FromHex("#FF0040"), Rgb.FromHex("#0040FF")] } },
        new() { Name = "Feu", Global = new EffectSettings { Effect = "fire", Speed = 55, Size = 50, Colors = [Rgb.FromHex("#B40000"), Rgb.FromHex("#FF5A00"), Rgb.FromHex("#FFC850")] }, SyncAcrossDevices = false },
        new() { Name = "Blanc", Global = new EffectSettings { Effect = "static", Colors = [Rgb.White] } },
        new() { Name = "Éteint", Global = new EffectSettings { Effect = "static", Colors = [Rgb.Black] } },
    ];
}

public sealed class ZoneConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Inverse le sens de la zone (ventilo monté à l'envers, barrette dans l'autre sens...).</summary>
    public bool Reverse { get; set; }
    /// <summary>Position dans la chaîne quand l'effet est synchronisé sur tous les appareils.</summary>
    public int Order { get; set; }
    /// <summary>Effet propre à cette zone ; null = suit l'effet du profil.</summary>
    public EffectSettings? Override { get; set; }

    public ZoneConfig Clone() => new() { Enabled = Enabled, Reverse = Reverse, Order = Order, Override = Override?.Clone() };
}

public static class ZoneKeys
{
    public static string Zone(string deviceKey, int zoneIndex, string zoneName) => $"{deviceKey}::{zoneIndex}:{zoneName}";
}
