using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using PrismRGB.Core;
using PrismRGB.Core.Devices;
using PrismRGB.Core.Engine;

namespace PrismRGB.App.Controls;

/// <summary>Aperçu en direct : une ligne par appareil, une pastille lumineuse par LED, zones séparées.</summary>
public sealed class LedPreview : Control
{
    private const double LabelWidth = 200;
    private const double Dot = 11;
    private const double Gap = 4;
    private const double ZoneGap = 12;
    private const double RowPad = 10;
    private const double LineHeight = Dot + Gap + 2;

    private static readonly Typeface Face = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.Parse("#D6D9E0"));
    private static readonly IBrush SubBrush = new ImmutableSolidColorBrush(Color.Parse("#6F7585"));
    private static readonly IBrush OffBrush = new ImmutableSolidColorBrush(Color.Parse("#23262F"));
    private static readonly IBrush Separator = new ImmutableSolidColorBrush(Color.Parse("#1E2129"));

    private IReadOnlyList<DeviceFrame> _frames = [];

    public IReadOnlyList<DeviceFrame> Frames
    {
        get => _frames;
        set
        {
            bool layoutChanged = value.Count != _frames.Count || value.Where((f, i) => f.Device != _frames[i].Device).Any();
            _frames = value;
            if (layoutChanged) InvalidateMeasure();
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width;
        double h = 0;
        foreach (var f in _frames) h += RowHeight(f.Device, w);
        return new Size(w, Math.Max(h, 60));
    }

    private static int DotsPerLine(double width) => Math.Max(8, (int)((width - LabelWidth - 16) / (Dot + Gap)));

    private static double RowHeight(RgbDevice d, double width)
    {
        int perLine = DotsPerLine(width);
        int slots = d.LedCount + Math.Max(0, d.Zones.Count(z => z.LedCount > 0) - 1) * 2; // les écarts entre zones prennent ~2 places
        int lines = Math.Max(1, (int)Math.Ceiling(slots / (double)perLine));
        return Math.Max(38, lines * LineHeight + RowPad * 2);
    }

    public override void Render(DrawingContext ctx)
    {
        double width = Bounds.Width;
        if (_frames.Count == 0)
        {
            var t = new FormattedText("Aucun appareil pour l'instant. Vérifie l'état de la connexion en bas à gauche.",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 13, SubBrush);
            ctx.DrawText(t, new Point(8, 20));
            return;
        }

        double y = 0;
        int perLine = DotsPerLine(width);
        double dotsLeft = LabelWidth;
        for (int r = 0; r < _frames.Count; r++)
        {
            var f = _frames[r];
            double rowH = RowHeight(f.Device, width);
            if (r > 0) ctx.FillRectangle(Separator, new Rect(0, y, width, 1));

            var name = new FormattedText(Ellipsize(f.Device.Name, 28), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 13, LabelBrush);
            var sub = new FormattedText($"{RgbDevice.KindLabel(f.Device.Kind)} · {f.Device.LedCount} LEDs", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 11, SubBrush);
            ctx.DrawText(name, new Point(8, y + RowPad - 1));
            ctx.DrawText(sub, new Point(8, y + RowPad + 16));

            int slot = 0;
            double top = y + RowPad + 2;
            bool firstZone = true;
            foreach (var z in f.Device.Zones)
            {
                if (z.LedCount <= 0) continue;
                if (!firstZone)
                {
                    // Petit espace entre zones, sauf en début de ligne.
                    if (slot % perLine != 0) slot += 2;
                }
                firstZone = false;
                for (int i = 0; i < z.LedCount; i++)
                {
                    int idx = z.StartIndex + i;
                    var c = idx < f.Colors.Length ? f.Colors[idx] : Rgb.Black;
                    int line = slot / perLine;
                    int col = slot % perLine;
                    double cx = dotsLeft + col * (Dot + Gap) + Dot / 2;
                    double cy = top + line * LineHeight + Dot / 2;
                    DrawLed(ctx, c, new Point(cx, cy));
                    slot++;
                }
            }
            y += rowH;
        }
    }

    private static void DrawLed(DrawingContext ctx, Rgb c, Point center)
    {
        int lum = Math.Max(c.R, Math.Max(c.G, c.B));
        if (lum < 8)
        {
            ctx.DrawEllipse(OffBrush, null, center, Dot / 2, Dot / 2);
            return;
        }
        // Halo proportionnel à la luminosité, puis cœur plein.
        var glow = new ImmutableSolidColorBrush(Color.FromArgb((byte)(lum * 0.28), c.R, c.G, c.B));
        ctx.DrawEllipse(glow, null, center, Dot * 0.95, Dot * 0.95);
        ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), null, center, Dot / 2, Dot / 2);
    }

    private static string Ellipsize(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
