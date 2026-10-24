// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrismRGB.Core;

/// <summary>Couleur 8 bits par canal. Sérialisée en JSON sous forme "#RRGGBB".</summary>
[JsonConverter(typeof(RgbJsonConverter))]
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);

    public bool IsBlack => R == 0 && G == 0 && B == 0;

    public static Rgb FromHex(string hex)
    {
        if (!TryParseHex(hex, out var c))
            throw new FormatException($"Couleur invalide : '{hex}'");
        return c;
    }

    public static bool TryParseHex(string? hex, out Rgb color)
    {
        color = Black;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6) return false;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public override string ToString() => ToHex();

    public Rgb Scale(double f)
    {
        if (f >= 1) return this;
        if (f <= 0) return Black;
        return new Rgb((byte)(R * f + 0.5), (byte)(G * f + 0.5), (byte)(B * f + 0.5));
    }

    public static Rgb Lerp(Rgb a, Rgb b, double t)
    {
        if (t <= 0) return a;
        if (t >= 1) return b;
        return new Rgb(
            (byte)(a.R + (b.R - a.R) * t + 0.5),
            (byte)(a.G + (b.G - a.G) * t + 0.5),
            (byte)(a.B + (b.B - a.B) * t + 0.5));
    }

    /// <summary>Addition saturée (utile pour superposer des particules).</summary>
    public static Rgb AddClamped(Rgb a, Rgb b) => new(
        (byte)Math.Min(255, a.R + b.R),
        (byte)Math.Min(255, a.G + b.G),
        (byte)Math.Min(255, a.B + b.B));

    public static Rgb Max(Rgb a, Rgb b) => new(Math.Max(a.R, b.R), Math.Max(a.G, b.G), Math.Max(a.B, b.B));

    /// <param name="h">Teinte en degrés (toute valeur, repliée sur 0..360).</param>
    /// <param name="s">Saturation 0..1.</param>
    /// <param name="v">Valeur 0..1.</param>
    public static Rgb FromHsv(double h, double s, double v)
    {
        h %= 360;
        if (h < 0) h += 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new Rgb((byte)((r + m) * 255 + 0.5), (byte)((g + m) * 255 + 0.5), (byte)((b + m) * 255 + 0.5));
    }
}

/// <summary>Couleur en virgule flottante (0..255) pour les effets qui accumulent/estompent.</summary>
public struct RgbF
{
    public float R, G, B;

    public RgbF(float r, float g, float b) { R = r; G = g; B = b; }

    public static RgbF From(Rgb c) => new(c.R, c.G, c.B);

    public readonly Rgb ToRgb() => new(
        (byte)Math.Clamp(R + 0.5f, 0, 255),
        (byte)Math.Clamp(G + 0.5f, 0, 255),
        (byte)Math.Clamp(B + 0.5f, 0, 255));

    public void Mul(float f) { R *= f; G *= f; B *= f; }

    public void AddMax(Rgb c, float intensity)
    {
        R = Math.Max(R, c.R * intensity);
        G = Math.Max(G, c.G * intensity);
        B = Math.Max(B, c.B * intensity);
    }
}

public sealed class RgbJsonConverter : JsonConverter<Rgb>
{
    public override Rgb Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Rgb.TryParseHex(reader.GetString(), out var c) ? c : Rgb.Black;

    public override void Write(Utf8JsonWriter writer, Rgb value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToHex());
}
