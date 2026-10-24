// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using PrismRGB.Core;
using PrismRGB.Core.Config;
using PrismRGB.Core.Devices;
using PrismRGB.Core.Engine;

namespace PrismRGB.App.Controls;

/// <summary>
/// Vue "vitrine" du PC en direct : chaque zone est dessinée selon ce qu'elle éclaire
/// (anneau de ventilateur, barrette de RAM, carte graphique, bande LED, point lumineux) avec halo.
/// </summary>
public sealed class RigView : Control
{
    private enum PartKind { Ring, Stick, Gpu, Strip, Blob }

    private readonly record struct Part(PartKind Kind, string Label, Rgb[] Leds, double W, double H);

    private static readonly Typeface LabelFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.Medium);
    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.Parse("#7D8394"));
    private static readonly IBrush Body = new ImmutableSolidColorBrush(Color.Parse("#14161C"));
    private static readonly IBrush BodyLight = new ImmutableSolidColorBrush(Color.Parse("#1B1E26"));
    private static readonly IPen BodyPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#2A2E38")), 1);
    private static readonly IPen TrackPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#1F222A")), 9);
    private static readonly IPen BladePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#1C1F27")), 7, lineCap: PenLineCap.Round);
    private static readonly IBrush Off = new ImmutableSolidColorBrush(Color.Parse("#262933"));

    private const double Gap = 34;
    private const double LabelH = 22;

    private IReadOnlyList<DeviceFrame> _frames = [];
    private List<Part> _parts = [];
    private string _layoutSignature = "";

    public static readonly StyledProperty<double> ScaleFactorProperty =
        AvaloniaProperty.Register<RigView, double>(nameof(ScaleFactor), 1.0);

    public double ScaleFactor
    {
        get => GetValue(ScaleFactorProperty);
        set => SetValue(ScaleFactorProperty, value);
    }

    /// <summary>Apparence choisie par l'utilisateur pour une zone (bande, ventilos...).</summary>
    public Func<string, ZoneLook?>? LookFor { get; set; }

    public IReadOnlyList<DeviceFrame> Frames
    {
        get => _frames;
        set
        {
            _frames = value;
            _parts = BuildParts(value);
            string sig = string.Join("|", _parts.Select(p => $"{p.Kind}{p.Leds.Length}{p.Label}"));
            if (sig != _layoutSignature)
            {
                _layoutSignature = sig;
                InvalidateMeasure();
            }
            InvalidateVisual();
        }
    }

    /// <summary>Force la reconstruction (apparence modifiée).</summary>
    public void Refresh() => Frames = _frames;

    private List<Part> BuildParts(IReadOnlyList<DeviceFrame> frames)
    {
        double s = ScaleFactor;
        var parts = new List<Part>();
        int ramIndex = 0;
        foreach (var f in frames)
        {
            var d = f.Device;
            if (d.Kind == DeviceKind.Dram)
            {
                // Une barrette par zone (un contrôleur Kingston pilote plusieurs barrettes).
                foreach (var z in d.Zones)
                {
                    if (z.LedCount <= 0) continue;
                    int st = Math.Min(z.StartIndex, f.Colors.Length);
                    var leds = f.Colors.AsSpan(st, Math.Min(z.LedCount, f.Colors.Length - st)).ToArray();
                    parts.Add(new Part(PartKind.Stick, $"RAM {++ramIndex}", leds, 30 * s, 136 * s));
                }
                continue;
            }
            if (d.Kind == DeviceKind.Gpu)
            {
                parts.Add(new Part(PartKind.Gpu, "Carte graphique", f.Colors, 300 * s, 92 * s));
                continue;
            }
            foreach (var z in d.Zones)
            {
                if (z.LedCount <= 0) continue;
                var leds = f.Colors.AsSpan(Math.Min(z.StartIndex, f.Colors.Length), Math.Min(z.LedCount, Math.Max(0, f.Colors.Length - z.StartIndex))).ToArray();
                var look = LookFor?.Invoke(ZoneKeys.Zone(d.Key, z.Index, z.Name));
                var kind = look?.Kind ?? LookKind.Auto;
                if (kind == LookKind.Auto) kind = Guess(d, z);

                if (kind == LookKind.Fans && z.LedCount >= 3)
                {
                    int fans = Math.Clamp(look?.Fans ?? 1, 1, Math.Max(1, z.LedCount / 3));
                    int per = z.LedCount / fans;
                    for (int i = 0; i < fans; i++)
                    {
                        int count = i == fans - 1 ? z.LedCount - per * i : per;
                        var slice = leds.AsSpan(per * i, Math.Min(count, leds.Length - per * i)).ToArray();
                        parts.Add(new Part(PartKind.Ring, fans > 1 ? $"{z.Name} · {i + 1}" : z.Name, slice, 108 * s, 108 * s));
                    }
                }
                else if (z.LedCount == 1)
                {
                    parts.Add(new Part(PartKind.Blob, z.Name, leds, 46 * s, 46 * s));
                }
                else
                {
                    double w = Math.Clamp(z.LedCount * 8 * s, 150 * s, 520 * s);
                    parts.Add(new Part(PartKind.Strip, z.Name, leds, w, 26 * s));
                }
            }
        }
        return parts;
    }

    private static LookKind Guess(RgbDevice d, RgbZone z)
    {
        string n = z.Name.ToLowerInvariant();
        if (n.Contains("fan") || n.Contains("ventil") || n.Contains("pomp") || n.Contains("pump") || n.Contains("ring") || n.StartsWith("jaf"))
            return LookKind.Fans;
        if (d.Kind == DeviceKind.Cooler && z.LedCount is >= 6 and <= 64) return LookKind.Fans;
        return LookKind.Strip;
    }

    private List<(double y, double h, List<(Part p, double x)> items)> Layout(double width)
    {
        var rows = new List<(double, double, List<(Part, double)>)>();
        var cur = new List<(Part, double)>();
        double x = 0, rowH = 0, y = 0;
        void Flush()
        {
            if (cur.Count == 0) return;
            double used = x - Gap;
            double offset = Math.Max(0, (width - used) / 2);
            rows.Add((y, rowH, cur.Select(t => (t.Item1, t.Item2 + offset)).ToList()));
            y += rowH + LabelH + Gap * 0.6;
            cur = [];
            x = 0;
            rowH = 0;
        }
        foreach (var p in _parts)
        {
            if (x > 0 && x + p.W > width) Flush();
            cur.Add((p, x));
            x += p.W + Gap;
            rowH = Math.Max(rowH, p.H);
        }
        Flush();
        return rows;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 1000 : availableSize.Width;
        var rows = Layout(w);
        double h = rows.Count == 0 ? 120 : rows[^1].y + rows[^1].h + LabelH + 8;
        return new Size(w, h);
    }

    public override void Render(DrawingContext ctx)
    {
        double width = Bounds.Width;
        if (_parts.Count == 0)
        {
            var t = Text("Aucun éclairage détecté pour l'instant", 14, LabelBrush);
            ctx.DrawText(t, new Point((width - t.Width) / 2, 50));
            return;
        }

        foreach (var (y, h, items) in Layout(width))
        {
            foreach (var (p, x) in items)
            {
                double top = y + (h - p.H) / 2;
                var r = new Rect(x, top, p.W, p.H);
                DrawAmbient(ctx, p, r);
                switch (p.Kind)
                {
                    case PartKind.Ring: DrawRing(ctx, p, r); break;
                    case PartKind.Stick: DrawStick(ctx, p, r); break;
                    case PartKind.Gpu: DrawGpu(ctx, p, r); break;
                    case PartKind.Strip: DrawStrip(ctx, p, r); break;
                    case PartKind.Blob: DrawBlob(ctx, p, r); break;
                }
                var label = Text(p.Label, 11, LabelBrush);
                ctx.DrawText(label, new Point(x + (p.W - label.Width) / 2, y + h + 6));
            }
        }
    }

    private static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, size, brush);

    private static (Rgb avg, double lum) Average(Rgb[] leds)
    {
        if (leds.Length == 0) return (Rgb.Black, 0);
        long r = 0, g = 0, b = 0;
        foreach (var c in leds) { r += c.R; g += c.G; b += c.B; }
        var avg = new Rgb((byte)(r / leds.Length), (byte)(g / leds.Length), (byte)(b / leds.Length));
        return (avg, Math.Max(avg.R, Math.Max(avg.G, avg.B)) / 255.0);
    }

    private static Color C(Rgb c, double alpha = 1) => Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B);

    /// <summary>Halo coloré diffus sous l'élément, comme la lumière qui se reflète dans le boîtier.</summary>
    private static void DrawAmbient(DrawingContext ctx, Part p, Rect r)
    {
        var (avg, lum) = Average(p.Leds);
        if (lum < 0.03) return;
        // Normalise la teinte pour un halo coloré même quand l'effet est sombre.
        double k = 255.0 / Math.Max(1, (int)Math.Max(avg.R, Math.Max(avg.G, avg.B)));
        var tint = new Rgb((byte)Math.Min(255, avg.R * k), (byte)Math.Min(255, avg.G * k), (byte)Math.Min(255, avg.B * k));
        double radius = Math.Max(r.Width, r.Height) * 0.85;
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(C(tint, 0.30 * lum), 0),
                new GradientStop(C(tint, 0.10 * lum), 0.55),
                new GradientStop(C(tint, 0), 1),
            },
        };
        ctx.DrawEllipse(brush, null, r.Center, radius, radius * (p.Kind == PartKind.Strip ? 0.45 : 0.9));
    }

    private static void DrawLed(DrawingContext ctx, Rgb c, Point center, double size)
    {
        int lum = Math.Max(c.R, Math.Max(c.G, c.B));
        if (lum < 6)
        {
            ctx.DrawEllipse(Off, null, center, size * 0.5, size * 0.5);
            return;
        }
        ctx.DrawEllipse(new ImmutableSolidColorBrush(C(c, lum / 255.0 * 0.32)), null, center, size * 1.25, size * 1.25);
        ctx.DrawEllipse(new ImmutableSolidColorBrush(C(c)), null, center, size * 0.55, size * 0.55);
        if (lum > 160)
            ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromArgb((byte)((lum - 160) * 1.2), 255, 255, 255)), null, center, size * 0.22, size * 0.22);
    }

    private static void DrawRing(DrawingContext ctx, Part p, Rect r)
    {
        var c = r.Center;
        double R = r.Width / 2;
        ctx.DrawEllipse(Body, BodyPen, c, R, R);
        // Pales stylisées
        for (int i = 0; i < 7; i++)
        {
            double a = i * Math.PI * 2 / 7;
            var p1 = new Point(c.X + Math.Cos(a) * R * 0.22, c.Y + Math.Sin(a) * R * 0.22);
            var p2 = new Point(c.X + Math.Cos(a + 0.55) * R * 0.66, c.Y + Math.Sin(a + 0.55) * R * 0.66);
            ctx.DrawLine(BladePen, p1, p2);
        }
        ctx.DrawEllipse(BodyLight, BodyPen, c, R * 0.22, R * 0.22);

        double tr = R * 0.84;
        ctx.DrawEllipse(null, TrackPen, c, tr, tr);
        var (avg, lum) = Average(p.Leds);
        if (lum > 0.03)
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(C(avg, 0.55)), 7), c, tr, tr); // diffuseur

        int n = p.Leds.Length;
        double size = n > 40 ? 4 : n > 24 ? 5.5 : 7;
        for (int i = 0; i < n; i++)
        {
            double a = -Math.PI / 2 + i * Math.PI * 2 / n;
            DrawLed(ctx, p.Leds[i], new Point(c.X + Math.Cos(a) * tr, c.Y + Math.Sin(a) * tr), size);
        }
    }

    private static LinearGradientBrush Gradient(Rgb[] leds, bool vertical, double alpha = 1)
    {
        var stops = new GradientStops();
        int n = Math.Max(1, leds.Length);
        for (int i = 0; i < leds.Length; i++)
            stops.Add(new GradientStop(C(leds[i], alpha), n == 1 ? 0.5 : (double)i / (n - 1)));
        if (leds.Length == 0) stops.Add(new GradientStop(Colors.Black, 0));
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = vertical ? new RelativePoint(0, 1, RelativeUnit.Relative) : new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = stops,
        };
    }

    private static void DrawStick(DrawingContext ctx, Part p, Rect r)
    {
        ctx.DrawRectangle(Body, BodyPen, r, 6, 6);
        var bar = new Rect(r.X + 5, r.Y + 5, r.Width - 10, r.Height * 0.62);
        ctx.DrawRectangle(Gradient(p.Leds, vertical: true), null, bar, 4, 4);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(40, 255, 255, 255)), null, new Rect(bar.X + 2, bar.Y + 2, 3, bar.Height - 4), 2, 2);
        // Ailettes du dissipateur
        var fin = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#232731")), 2);
        for (double y = bar.Bottom + 8; y < r.Bottom - 6; y += 7)
            ctx.DrawLine(fin, new Point(r.X + 6, y), new Point(r.Right - 6, y));
    }

    private static void DrawGpu(DrawingContext ctx, Part p, Rect r)
    {
        ctx.DrawRectangle(Body, BodyPen, r, 10, 10);
        // Deux ventilateurs de la carte
        double fr = r.Height * 0.32;
        foreach (double fx in new[] { r.X + r.Width * 0.27, r.X + r.Width * 0.73 })
        {
            var c = new Point(fx, r.Y + r.Height * 0.58);
            ctx.DrawEllipse(BodyLight, BodyPen, c, fr, fr);
            for (int i = 0; i < 6; i++)
            {
                double a = i * Math.PI / 3;
                ctx.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#14161C")), 3),
                    c, new Point(c.X + Math.Cos(a) * fr * 0.9, c.Y + Math.Sin(a) * fr * 0.9));
            }
        }
        var bar = new Rect(r.X + 14, r.Y + 8, r.Width - 28, 9);
        ctx.DrawRectangle(Gradient(p.Leds, vertical: false), null, bar, 4, 4);
    }

    private static void DrawStrip(DrawingContext ctx, Part p, Rect r)
    {
        var bar = new Rect(r.X, r.Y + (r.Height - 14) / 2, r.Width, 14);
        ctx.DrawRectangle(Body, BodyPen, bar.Inflate(4), 9, 9);
        ctx.DrawRectangle(Gradient(p.Leds, vertical: false), null, bar, 7, 7);
        // Points des LEDs par-dessus le diffuseur
        int n = p.Leds.Length;
        if (n <= 90)
        {
            var dot = new ImmutableSolidColorBrush(Color.FromArgb(70, 255, 255, 255));
            for (int i = 0; i < n; i++)
            {
                double x = bar.X + 7 + (bar.Width - 14) * (n == 1 ? 0.5 : (double)i / (n - 1));
                if (Math.Max(p.Leds[i].R, Math.Max(p.Leds[i].G, p.Leds[i].B)) > 40)
                    ctx.DrawEllipse(dot, null, new Point(x, bar.Center.Y), 1.6, 1.6);
            }
        }
    }

    private static void DrawBlob(DrawingContext ctx, Part p, Rect r)
    {
        ctx.DrawEllipse(Body, BodyPen, r.Center, r.Width / 2, r.Width / 2);
        DrawLed(ctx, p.Leds.Length > 0 ? p.Leds[0] : Rgb.Black, r.Center, r.Width * 0.42);
    }
}
