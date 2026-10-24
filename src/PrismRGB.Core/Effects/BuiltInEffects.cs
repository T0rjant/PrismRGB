// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace PrismRGB.Core.Effects;

/// <summary>Une couleur fixe.</summary>
public sealed class StaticEffect : Effect
{
    public override bool IsAnimated => false;
    protected override void RenderLogical(Span<Rgb> leds, in EffectContext ctx) => leds.Fill(ctx.Color(0));
}

/// <summary>Plusieurs couleurs fixes : dégradé (doux) ou blocs nets.</summary>
public sealed class MultiStaticEffect : Effect
{
    public override bool IsAnimated => false;
    protected override void RenderLogical(Span<Rgb> leds, in EffectContext ctx)
    {
        var c = ctx.Colors;
        int n = leds.Length;
        for (int i = 0; i < n; i++)
        {
            double p = Pos(i, n);
            if (c.Count == 1) { leds[i] = c[0]; continue; }
            if (ctx.Settings.Smooth)
            {
                double x = p * (c.Count - 1);
                int k = Math.Min((int)x, c.Count - 2);
                leds[i] = Rgb.Lerp(c[k], c[k + 1], x - k);
            }
            else
            {
                int k = Math.Min((int)(n <= 1 ? 0 : (double)i / n * c.Count), c.Count - 1);
                leds[i] = c[k];
            }
        }
    }
}

/// <summary>Respiration : montée/descente douce, une couleur de la palette par respiration.</summary>
public sealed class BreathingEffect : Effect
{
    private double _phase;

    protected override void RenderLogical(Span<Rgb> leds, in EffectContext ctx)
    {
        _phase += ctx.Delta * ctx.SpeedFactor / 4.0;
        int ci = (int)Math.Floor(_phase);
        double f = _phase - ci;
        double intensity = (1 - Math.Cos(2 * Math.PI * f)) / 2;
        intensity = Math.Pow(intensity, 1.8); // l'œil perçoit mieux une courbe non linéaire
        double floor = ctx.Settings.Smooth ? 0.04 : 0;
        leds.Fill(ctx.Color(ci).Scale(floor + (1 - floor) * intensity));
    }
}

/// <summary>Flash / stroboscope : éclairs brefs, la couleur change à chaque éclair.</summary>
public sealed class FlashEffect : Effect
{
    private double _phase;

    protected override void RenderLogical(Span<Rgb> leds, in EffectContext ctx)
    {
        _phase += ctx.Delta * ctx.SpeedFactor / 1.2;
        int ci = (int)Math.Floor(_phase);
        double f = _phase - ci;
        double duty = 0.04 + ctx.Size01 * 0.46;
        double intensity = f < duty ? 1 : ctx.Settings.Smooth ? Math.Exp(-(f - duty) * 14) : 0;
        leds.Fill(ctx.Color(ci).Scale(intensity));
    }
}

/// <summary>Arc-en-ciel qui défile.</summary>
public sealed class RainbowEffect : Effect
{
    private double _offset;

    protected override void RenderLogical(Span<Rgb> leds, in EffectContext ctx)
    {
        double waves = 0.3 + ctx.Size01 * 3;
        _offset += ctx.Delta * ctx.SpeedFactor * 0.3;
        int n = leds.Length;
        for (int i = 0; i < n; i++)
            leds[i] = Rgb.FromHsv(360 * (waves * Pos(i, n) - _offset), 1, 1);
    }
}

/// <summary>Toutes les LEDs parcourent ensemble le spectre.</summary>
public sealed class SpectrumEffect : Effect
{
    private double _hue;

    protected override void RenderLogical(Span<Rgb> leds, in EffectContext ctx)
    {
        _hue += ctx.Delta * ctx.SpeedFactor * 40;
        leds.Fill(Rgb.FromHsv(_hue, 1, 1));
    }
}

/// <summary>Vague des couleurs choisies qui défile.</summary>
public sealed class ColorWaveEffect : Effect
{
    private double _offset;

    protected override void RenderLogical(Span<Rgb> leds, in EffectContext ctx)
    {
        double waves = 0.3 + ctx.Size01 * 2;
        _offset += ctx.Delta * ctx.SpeedFactor * 0.3;
        int n = leds.Length;
        for (int i = 0; i < n; i++)
            leds[i] = ctx.Palette(waves * Pos(i, n) - _offset);
    }
}

/// <summary>
/// Météorite : une tête lumineuse et une traînée qui s'estompe (scintillante en mode net).
/// Sur une longue chaîne (tout le PC synchronisé), plusieurs météorites se suivent à intervalle régulier.
/// </summary>
public sealed class MeteorEffect : Effect
{
    private RgbF[] _trail = [];
    private double _pos;

    protected override void OnResize(int length)
    {
        _trail = new RgbF[length];
        _pos = 0;
    }

    protected override void RenderLogical(Span<Rgb> leds, in EffectContext ctx)
    {
        int n = leds.Length;
        double speed = (8 + Math.Min(n, 60) * 0.6) * ctx.SpeedFactor;        // LEDs par seconde
        double decay = 1.5 + (1 - ctx.Size01) * 10;                           // estompage par seconde
        double head = Math.Max(1, 1 + ctx.Size01 * Math.Min(n, 60) / 10.0);  // longueur de la tête
        double tail = speed * 3 / decay;                                      // longueur visible de la traînée
        int heads = Math.Max(1, (int)(n / Math.Max(45.0, tail * 1.6)));
        double spacing = Math.Max(n / (double)heads, head + 2);
        double period = n + tail * 0.5;                                       // une météorite seule repart après sa traînée
        if (heads > 1) period = spacing;

        for (int i = 0; i < n; i++)
        {
            double jitter = ctx.Settings.Smooth ? 1 : 0.3 + ctx.Rng.NextDouble() * 1.4;
            _trail[i].Mul((float)Math.Exp(-ctx.Delta * decay * jitter));
        }

        _pos += speed * ctx.Delta;
        int cycle = (int)Math.Floor(_pos / period);
        double phase = _pos - cycle * period;

        for (int k = 0; k <= heads; k++)
        {
            double p = phase + (k - 1) * period;   // une tête de plus pour couvrir l'entrée en douceur
            if (p - head > n || p < -1) continue;
            var c = ctx.Color(cycle - k + 1 + heads * 1000);
            for (int i = (int)Math.Floor(p - head); i <= (int)Math.Ceiling(p); i++)
            {
                if (i < 0 || i >= n) continue;
                // Antialiasing de la pointe pour un mouvement fluide même lent.
                double cover = Math.Clamp(p - i + 1, 0, 1) * Math.Clamp(i - (p - head) + 1, 0, 1);
                _trail[i].AddMax(c, (float)cover);
            }
        }

        for (int i = 0; i < n; i++) leds[i] = _trail[i].ToRgb();
    }
}

/// <summary>Scanner aller-retour (façon K2000).</summary>
public sealed class ScannerEffect : Effect
{
    private RgbF[] _trail = [];
    private double _phase;

    protected override void OnResize(int length) => _trail = new RgbF[length];

    protected override void RenderLogical(Span<Rgb> leds, in EffectContext ctx)
    {
        int n = leds.Length;
        _phase += ctx.Delta * ctx.SpeedFactor * 0.7;
        int pass = (int)Math.Floor(_phase);
        double f = _phase - pass;
        double tri = pass % 2 == 0 ? f : 1 - f;
        double pos = tri * (n - 1);
        double width = 0.6 + ctx.Size01 * Math.Max(1, n / 8.0);
        double decay = 3 + (1 - ctx.Size01) * 12;
        var c = ctx.Color(ctx.Settings.Smooth ? pass : pass / 2);

        for (int i = 0; i < n; i++)
        {
            _trail[i].Mul((float)Math.Exp(-ctx.Delta * decay));
            double d = Math.Abs(i - pos);
            if (d < width + 1) _trail[i].AddMax(c, (float)Math.Clamp(width + 1 - d, 0, 1));
        }
        for (int i = 0; i < n; i++) leds[i] = _trail[i].ToRgb();
    }
}

/// <summary>Étoiles : des LEDs s'allument et s'éteignent au hasard.</summary>
public sealed class TwinkleEffect : Effect
{
    private float[] _level = [];
    private bool[] _rising = [];
    private Rgb[] _color = [];

    protected override void OnResize(int length)
    {
        _level = new float[length];
        _rising = new bool[length];
        _color = new Rgb[length];
    }

    protected override void RenderLogical(Span<Rgb> leds, in EffectContext ctx)
    {
        int n = leds.Length;
        double spawn = (0.03 + ctx.Size01 * 0.9) * ctx.Delta * Math.Sqrt(ctx.SpeedFactor);
        float up = (float)(ctx.Delta * 4 * ctx.SpeedFactor);
        float down = (float)(ctx.Delta * 1.3 * ctx.SpeedFactor);
        for (int i = 0; i < n; i++)
        {
            if (_level[i] <= 0 && !_rising[i] && ctx.Rng.NextDouble() < spawn)
            {
                _rising[i] = true;
                _color[i] = ctx.Settings.Smooth ? ctx.Palette(ctx.Rng.NextDouble(), true) : ctx.Color(ctx.Rng.Next(ctx.Colors.Count));
            }
            if (_rising[i])
            {
                _level[i] += up;
                if (_level[i] >= 1) { _level[i] = 1; _rising[i] = false; }
            }
            else if (_level[i] > 0)
            {
                _level[i] = Math.Max(0, _level[i] - down);
            }
            leds[i] = _color[i].Scale(_level[i] * _level[i]);
        }
    }
}

/// <summary>Feu (algorithme "Fire2012" de Mark Kriegsman / FastLED) : la chaleur monte et refroidit.</summary>
public sealed class FireEffect : Effect
{
    private byte[] _heat = [];
    private double _acc;

    protected override void OnResize(int length) => _heat = new byte[length];

    protected override void RenderLogical(Span<Rgb> leds, in EffectContext ctx)
    {
        int n = leds.Length;
        var rng = ctx.Rng;
        int cooling = (int)(25 + (1 - ctx.Size01) * 75);
        const int sparking = 120;
        double step = 1.0 / (40 * ctx.SpeedFactor);
        _acc += ctx.Delta;
        int guard = 0;
        while (_acc >= step && guard++ < 8)
        {
            _acc -= step;
            for (int i = 0; i < n; i++)
                _heat[i] = (byte)Math.Max(0, _heat[i] - rng.Next(0, cooling * 10 / Math.Max(n, 1) + 3));
            for (int k = n - 1; k >= 2; k--)
                _heat[k] = (byte)((_heat[k - 1] + _heat[k - 2] + _heat[k - 2]) / 3);
            if (rng.Next(255) < sparking)
            {
                int y = rng.Next(Math.Min(4, n));
                _heat[y] = (byte)Math.Min(255, _heat[y] + rng.Next(160, 255));
            }
        }
        if (_acc > step) _acc = 0;

        var pal = ctx.Colors;
        for (int i = 0; i < n; i++)
        {
            // Palette : noir -> couleur 1 -> couleur 2 -> ... (par défaut rouge, orange, jaune pâle).
            double h = _heat[i] / 255.0 * pal.Count;
            int k = (int)h;
            double f = h - k;
            Rgb from = k == 0 ? Rgb.Black : pal[Math.Min(k - 1, pal.Count - 1)];
            Rgb to = pal[Math.Min(k, pal.Count - 1)];
            leds[i] = Rgb.Lerp(from, to, f);
        }
    }
}

/// <summary>Gyrophare de police : deux moitiés qui flashent en alternance.</summary>
public sealed class PoliceEffect : Effect
{
    private double _phase;

    protected override void RenderLogical(Span<Rgb> leds, in EffectContext ctx)
    {
        _phase += ctx.Delta * ctx.SpeedFactor * 0.9;
        double f = _phase - Math.Floor(_phase);
        bool firstHalf = f < 0.5;
        double g = (f % 0.5) * 2;
        bool on = g < 0.18 || (g > 0.3 && g < 0.48);
        int n = leds.Length;
        var c0 = ctx.Color(0);
        var c1 = ctx.Colors.Count > 1 ? ctx.Color(1) : Rgb.FromHex("#0030FF");
        for (int i = 0; i < n; i++)
        {
            bool left = i < (n + 1) / 2;
            leds[i] = on && left == firstHalf ? (left ? c0 : c1) : Rgb.Black;
        }
    }
}

/// <summary>Chenillard (théâtre) : des points espacés qui avancent.</summary>
public sealed class ChaseEffect : Effect
{
    private double _offset;

    protected override void RenderLogical(Span<Rgb> leds, in EffectContext ctx)
    {
        int spacing = 2 + (int)Math.Round(ctx.Size01 * 6);
        _offset += ctx.Delta * ctx.SpeedFactor * 8;
        int n = leds.Length;
        for (int i = 0; i < n; i++)
        {
            double x = i - _offset;
            double m = x - spacing * Math.Floor(x / spacing); // modulo positif
            int group = (int)Math.Floor(x / spacing);
            double intensity = ctx.Settings.Smooth
                ? Math.Max(0, 1 - Math.Min(m, spacing - m))
                : (m < 1 ? 1 : 0);
            leds[i] = ctx.Color(group).Scale(intensity);
        }
    }
}

/// <summary>Remplissage : la bande se remplit d'une couleur puis de la suivante.</summary>
public sealed class WipeEffect : Effect
{
    private double _phase;

    protected override void RenderLogical(Span<Rgb> leds, in EffectContext ctx)
    {
        _phase += ctx.Delta * ctx.SpeedFactor * 0.5;
        int ci = (int)Math.Floor(_phase);
        double f = _phase - ci;
        int n = leds.Length;
        double edge = f * (n + 1);
        var bg = ctx.Color(ci);
        var fg = ctx.Color(ci + 1);
        if (ctx.Colors.Count == 1) bg = Rgb.Black;
        for (int i = 0; i < n; i++)
        {
            double t = ctx.Settings.Smooth ? Math.Clamp(edge - i, 0, 1) : (i < edge - 0.5 ? 1 : 0);
            leds[i] = Rgb.Lerp(bg, fg, t);
        }
    }
}

/// <summary>Ondes : des cercles de couleur naissent au hasard et s'étendent.</summary>
public sealed class RippleEffect : Effect
{
    private readonly List<(double center, double age, Rgb color)> _ripples = [];

    protected override void RenderLogical(Span<Rgb> leds, in EffectContext ctx)
    {
        int n = leds.Length;
        double sf = ctx.SpeedFactor;
        double life = 1.6 / Math.Sqrt(sf);
        double rate = (0.3 + ctx.Size01 * 2.5) * Math.Sqrt(sf) * Math.Max(1, n / 30.0);
        if (ctx.Rng.NextDouble() < rate * ctx.Delta && _ripples.Count < 32)
            _ripples.Add((ctx.Rng.NextDouble() * (n - 1), 0, ctx.Color(ctx.Rng.Next(ctx.Colors.Count))));

        for (int r = _ripples.Count - 1; r >= 0; r--)
        {
            var (c, a, col) = _ripples[r];
            a += ctx.Delta;
            if (a > life) _ripples.RemoveAt(r);
            else _ripples[r] = (c, a, col);
        }

        double speed = (6 + n * 0.4) * sf;
        for (int i = 0; i < n; i++)
        {
            var px = new RgbF();
            foreach (var (c, a, col) in _ripples)
            {
                double radius = a * speed;
                double d = Math.Abs(Math.Abs(i - c) - radius);
                double fade = 1 - a / life;
                if (d < 2) px.AddMax(col, (float)((1 - d / 2) * fade * fade));
            }
            leds[i] = px.ToRgb();
        }
    }
}
