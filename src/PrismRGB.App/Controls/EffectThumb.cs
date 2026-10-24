// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using PrismRGB.Core;
using PrismRGB.Core.Effects;

namespace PrismRGB.App.Controls;

/// <summary>Miniature animée d'un effet : un petit anneau de LEDs qui joue l'effet avec les couleurs en cours.</summary>
public sealed class EffectThumb : Control
{
    private const int LedCount = 18;
    private static readonly HashSet<EffectThumb> Live = [];
    private static readonly Random Rng = new();
    private static readonly IPen Track = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#1E2129")), 6);
    private static readonly IBrush Off = new ImmutableSolidColorBrush(Color.Parse("#2A2D37"));
    private static double _time;

    /// <summary>Réglages en cours dans l'éditeur (couleurs, vitesse...) appliqués à toutes les miniatures.</summary>
    public static EffectSettings? Shared { get; set; }

    public static readonly StyledProperty<EffectInfo?> InfoProperty =
        AvaloniaProperty.Register<EffectThumb, EffectInfo?>(nameof(Info));

    public EffectInfo? Info
    {
        get => GetValue(InfoProperty);
        set => SetValue(InfoProperty, value);
    }

    private readonly Rgb[] _leds = new Rgb[LedCount];
    private readonly EffectSettings _settings = new();
    private PrismRGB.Core.Effects.Effect? _effect;
    private string? _effectId;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Live.Add(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Live.Remove(this);
    }

    public static void TickAll(double dt)
    {
        _time += dt;
        foreach (var t in Live) t.Advance(dt);
    }

    private void Advance(double dt)
    {
        var info = Info;
        if (info is null || !IsEffectivelyVisible) return;
        if (_effect is null || _effectId != info.Id)
        {
            _effect = info.Create();
            _effectId = info.Id;
        }
        var s = Shared;
        _settings.Effect = info.Id;
        _settings.Colors = s?.Colors is { Count: > 0 } c ? c : [Rgb.FromHex("#FF2E88"), Rgb.FromHex("#3A7BFF")];
        _settings.Speed = s?.Speed ?? 50;
        _settings.Size = s?.Size ?? 50;
        _settings.Smooth = s?.Smooth ?? true;
        _settings.Brightness = 100;
        _settings.Direction = EffectDirection.Right;
        _effect.Render(_leds, new EffectContext(_time, dt, _settings, Rng));
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double r = size / 2 - 7;
        ctx.DrawEllipse(null, Track, c, r, r);
        for (int i = 0; i < LedCount; i++)
        {
            double a = -Math.PI / 2 + i * Math.PI * 2 / LedCount;
            var p = new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r);
            var col = _leds[i];
            int lum = Math.Max(col.R, Math.Max(col.G, col.B));
            if (lum < 8)
            {
                ctx.DrawEllipse(Off, null, p, 2.2, 2.2);
                continue;
            }
            ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromArgb((byte)(lum * 0.35), col.R, col.G, col.B)), null, p, 5.5, 5.5);
            ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromRgb(col.R, col.G, col.B)), null, p, 2.6, 2.6);
        }
    }
}
