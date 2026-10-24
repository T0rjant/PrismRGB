using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrismRGB.Core;
using PrismRGB.Core.Effects;

namespace PrismRGB.App.ViewModels;

public sealed record DirectionChoice(EffectDirection Value, string Label, string Symbol);

public sealed record PalettePreset(string Name, Rgb[] Colors)
{
    public IBrush Preview
    {
        get
        {
            var stops = new GradientStops();
            for (int i = 0; i < Colors.Length; i++)
                stops.Add(new GradientStop(ColorSlotViewModel.ToColor(Colors[i]), Colors.Length == 1 ? 0 : (double)i / (Colors.Length - 1)));
            return new LinearGradientBrush
            {
                StartPoint = new Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative),
                EndPoint = new Avalonia.RelativePoint(1, 0.5, Avalonia.RelativeUnit.Relative),
                GradientStops = stops,
            };
        }
    }
}

public sealed partial class ColorSlotViewModel : ObservableObject
{
    private readonly EffectEditorViewModel _owner;

    [ObservableProperty] private Color _color;

    public ColorSlotViewModel(EffectEditorViewModel owner, Rgb c)
    {
        _owner = owner;
        _color = ToColor(c);
    }

    public Rgb Rgb => new(Color.R, Color.G, Color.B);

    partial void OnColorChanged(Color value) => _owner.ColorsEdited();

    [RelayCommand]
    private void Remove() => _owner.RemoveColor(this);

    public static Color ToColor(Rgb c) => Color.FromRgb(c.R, c.G, c.B);
}

/// <summary>Édite un <see cref="EffectSettings"/> (celui du profil ou celui d'une zone).</summary>
public sealed partial class EffectEditorViewModel : ObservableObject
{
    private readonly Action _changed;
    private EffectSettings _settings = new();
    private bool _loading;

    public EffectEditorViewModel(Action changed)
    {
        _changed = changed;
    }

    public IReadOnlyList<EffectInfo> Effects => EffectRegistry.All;

    public IReadOnlyList<DirectionChoice> Directions { get; } =
    [
        new(EffectDirection.Right, "Vers la droite", "→"),
        new(EffectDirection.Left, "Vers la gauche", "←"),
        new(EffectDirection.CenterOut, "Du centre vers les bords", "←  →"),
        new(EffectDirection.EdgesIn, "Des bords vers le centre", "→  ←"),
    ];

    public IReadOnlyList<PalettePreset> Palettes { get; } =
    [
        new("Cyberpunk", [Rgb.FromHex("#FF00A0"), Rgb.FromHex("#00E5FF")]),
        new("Océan", [Rgb.FromHex("#0011FF"), Rgb.FromHex("#00A2FF"), Rgb.FromHex("#00FFC8")]),
        new("Coucher de soleil", [Rgb.FromHex("#FF1E00"), Rgb.FromHex("#FF7A00"), Rgb.FromHex("#FF00B4")]),
        new("Forêt", [Rgb.FromHex("#00FF40"), Rgb.FromHex("#9DFF00"), Rgb.FromHex("#006B1E")]),
        new("Violet", [Rgb.FromHex("#7A00FF"), Rgb.FromHex("#E000FF")]),
        new("Glace", [Rgb.FromHex("#FFFFFF"), Rgb.FromHex("#7FD4FF"), Rgb.FromHex("#0050FF")]),
        new("Feu", [Rgb.FromHex("#B40000"), Rgb.FromHex("#FF5A00"), Rgb.FromHex("#FFC850")]),
        new("RGB", [Rgb.FromHex("#FF0000"), Rgb.FromHex("#00FF00"), Rgb.FromHex("#0000FF")]),
        new("Blanc", [Rgb.FromHex("#FFFFFF")]),
    ];

    public ObservableCollection<ColorSlotViewModel> Colors { get; } = [];

    [ObservableProperty] private EffectInfo _selectedEffect = EffectRegistry.All[0];
    [ObservableProperty] private int _speed;
    [ObservableProperty] private int _brightness;
    [ObservableProperty] private int _size;
    [ObservableProperty] private DirectionChoice? _direction;
    [ObservableProperty] private bool _smooth;

    public bool UsesColors => SelectedEffect.UsesColors;
    public bool UsesSpeed => SelectedEffect.UsesSpeed;
    public bool UsesSize => SelectedEffect.UsesSize;
    public bool UsesDirection => SelectedEffect.UsesDirection;
    public bool UsesSmooth => SelectedEffect.UsesSmooth;
    public string SizeLabel => SelectedEffect.SizeLabel ?? "Taille";
    public string SmoothLabel => SelectedEffect.SmoothLabel ?? "";
    public bool CanAddColor => Colors.Count < SelectedEffect.MaxColors;
    public bool HasSuggestedColors => SelectedEffect.SuggestedColors is { Length: > 0 };
    public string ColorsHint => SelectedEffect.MaxColors == 1
        ? "Une couleur."
        : $"De {SelectedEffect.MinColors} à {SelectedEffect.MaxColors} couleurs.";

    public void Load(EffectSettings settings)
    {
        _loading = true;
        try
        {
            _settings = settings;
            SelectedEffect = EffectRegistry.Get(settings.Effect);
            Speed = settings.Speed;
            Brightness = settings.Brightness;
            Size = settings.Size;
            Smooth = settings.Smooth;
            Direction = Directions.First(d => d.Value == settings.Direction);
            Colors.Clear();
            foreach (var c in settings.Colors) Colors.Add(new ColorSlotViewModel(this, c));
            EnsureColorCount();
            RefreshFlags();
        }
        finally { _loading = false; }
    }

    partial void OnSelectedEffectChanged(EffectInfo value)
    {
        if (_loading) return;
        _settings.Effect = value.Id;
        EnsureColorCount();
        RefreshFlags();
        Push();
    }

    partial void OnSpeedChanged(int value) { if (!_loading) { _settings.Speed = value; Push(); } }
    partial void OnBrightnessChanged(int value) { if (!_loading) { _settings.Brightness = value; Push(); } }
    partial void OnSizeChanged(int value) { if (!_loading) { _settings.Size = value; Push(); } }
    partial void OnSmoothChanged(bool value) { if (!_loading) { _settings.Smooth = value; Push(); } }
    partial void OnDirectionChanged(DirectionChoice? value)
    {
        if (!_loading && value is not null) { _settings.Direction = value.Value; Push(); }
    }

    /// <summary>Complète la palette jusqu'au minimum requis par l'effet, sans jamais supprimer de couleur.</summary>
    private void EnsureColorCount()
    {
        var info = SelectedEffect;
        if (!info.UsesColors) return;
        var defaults = new[] { "#FF0000", "#0000FF", "#00FF00", "#FFFFFF" };
        while (Colors.Count < info.MinColors)
            Colors.Add(new ColorSlotViewModel(this, Rgb.FromHex(defaults[Colors.Count % defaults.Length])));
        SyncColors();
    }

    private void RefreshFlags()
    {
        OnPropertyChanged(nameof(UsesColors));
        OnPropertyChanged(nameof(UsesSpeed));
        OnPropertyChanged(nameof(UsesSize));
        OnPropertyChanged(nameof(UsesDirection));
        OnPropertyChanged(nameof(UsesSmooth));
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(SmoothLabel));
        OnPropertyChanged(nameof(CanAddColor));
        OnPropertyChanged(nameof(HasSuggestedColors));
        OnPropertyChanged(nameof(ColorsHint));
    }

    internal void ColorsEdited()
    {
        if (_loading) return;
        SyncColors();
        Push();
    }

    private void SyncColors() => _settings.Colors = Colors.Select(c => c.Rgb).ToList();

    [RelayCommand]
    private void AddColor()
    {
        if (!CanAddColor) return;
        var last = Colors.Count > 0 ? Colors[^1].Rgb : Rgb.FromHex("#FF0000");
        // Nouvelle couleur : teinte décalée de la précédente pour un ajout utile du premier coup.
        var (h, s, v) = ToHsv(last);
        Colors.Add(new ColorSlotViewModel(this, Rgb.FromHsv(h + 60, Math.Max(s, 0.6), Math.Max(v, 0.8))));
        OnPropertyChanged(nameof(CanAddColor));
        ColorsEdited();
    }

    internal void RemoveColor(ColorSlotViewModel slot)
    {
        if (Colors.Count <= Math.Max(1, SelectedEffect.MinColors)) return;
        Colors.Remove(slot);
        OnPropertyChanged(nameof(CanAddColor));
        ColorsEdited();
    }

    [RelayCommand]
    private void ApplyPalette(PalettePreset preset) => SetColors(preset.Colors);

    [RelayCommand]
    private void ApplySuggested()
    {
        if (SelectedEffect.SuggestedColors is { } s) SetColors(s.Select(Rgb.FromHex).ToArray());
    }

    private void SetColors(Rgb[] colors)
    {
        _loading = true;
        Colors.Clear();
        foreach (var c in colors.Take(Math.Max(1, SelectedEffect.MaxColors))) Colors.Add(new ColorSlotViewModel(this, c));
        _loading = false;
        EnsureColorCount();
        OnPropertyChanged(nameof(CanAddColor));
        ColorsEdited();
    }

    private void Push() => _changed();

    private static (double h, double s, double v) ToHsv(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h < 0 ? h + 360 : h, max == 0 ? 0 : d / max, max);
    }
}
