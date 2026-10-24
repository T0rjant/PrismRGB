// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace PrismRGB.Core.Effects;

public readonly struct EffectContext
{
    public EffectContext(double time, double delta, EffectSettings settings, Random rng)
    {
        Time = time;
        Delta = delta;
        Settings = settings;
        Rng = rng;
    }

    /// <summary>Temps écoulé en secondes.</summary>
    public double Time { get; }
    /// <summary>Durée de l'image précédente en secondes.</summary>
    public double Delta { get; }
    public EffectSettings Settings { get; }
    public Random Rng { get; }

    /// <summary>Vitesse 1..100 convertie en multiplicateur exponentiel (~0.2x à ~6x, 1x vers 47).</summary>
    public double SpeedFactor => 0.2 * Math.Pow(2, Math.Clamp(Settings.Speed, 1, 100) / 20.0);

    /// <summary>Taille 1..100 ramenée sur 0..1.</summary>
    public double Size01 => Math.Clamp(Settings.Size, 1, 100) / 100.0;

    public IReadOnlyList<Rgb> Colors => Settings.Colors.Count > 0 ? Settings.Colors : DefaultColors;

    public Rgb Color(int i)
    {
        var c = Colors;
        return c[((i % c.Count) + c.Count) % c.Count];
    }

    /// <summary>Couleur de la palette en position cyclique 0..1 (dégradé ou aplats selon "Smooth").</summary>
    public Rgb Palette(double t, bool? smooth = null)
    {
        var c = Colors;
        if (c.Count == 1) return c[0];
        t -= Math.Floor(t);
        double x = t * c.Count;
        int i = (int)x;
        if (!(smooth ?? Settings.Smooth)) return c[i % c.Count];
        return Rgb.Lerp(c[i % c.Count], c[(i + 1) % c.Count], x - i);
    }

    private static readonly Rgb[] DefaultColors = [Rgb.White];
}

/// <summary>
/// Un effet dessine une bande "logique" où le mouvement va toujours vers les indices croissants.
/// La classe de base applique ensuite la direction (gauche, droite, centre...) en recopiant
/// la bande logique sur les LEDs physiques. Une instance par bande : l'effet peut garder un état.
/// </summary>
public abstract class Effect
{
    private Rgb[] _logical = [];

    /// <summary>false pour les effets fixes : le moteur peut alors dormir entre deux renvois de maintien.</summary>
    public virtual bool IsAnimated => true;

    public void Render(Span<Rgb> leds, in EffectContext ctx)
    {
        int n = leds.Length;
        if (n == 0) return;
        var dir = ctx.Settings.Direction;
        bool mirrored = dir is EffectDirection.CenterOut or EffectDirection.EdgesIn;
        int len = mirrored ? (n + 1) / 2 : n;
        if (_logical.Length != len)
        {
            _logical = new Rgb[len];
            OnResize(len);
        }

        RenderLogical(_logical, ctx);

        switch (dir)
        {
            case EffectDirection.Right:
                _logical.CopyTo(leds);
                break;
            case EffectDirection.Left:
                for (int i = 0; i < n; i++) leds[i] = _logical[n - 1 - i];
                break;
            case EffectDirection.CenterOut:
            case EffectDirection.EdgesIn:
                // Moitié droite : index logique = distance au centre ; moitié gauche en miroir.
                for (int i = 0; i < n; i++)
                {
                    int d = Math.Clamp(i >= n / 2 ? i - n / 2 : (n - 1) / 2 - i, 0, len - 1);
                    leds[i] = dir == EffectDirection.CenterOut ? _logical[d] : _logical[len - 1 - d];
                }
                break;
        }

        double b = Math.Clamp(ctx.Settings.Brightness, 0, 100) / 100.0;
        if (b < 1)
            for (int i = 0; i < n; i++) leds[i] = leds[i].Scale(b);
    }

    protected virtual void OnResize(int length) { }

    protected abstract void RenderLogical(Span<Rgb> leds, in EffectContext ctx);

    /// <summary>Position normalisée 0..1 d'une LED.</summary>
    protected static double Pos(int i, int n) => n <= 1 ? 0 : (double)i / (n - 1);
}
