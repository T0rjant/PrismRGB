// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace PrismRGB.Core.Effects;

public enum EffectDirection
{
    /// <summary>Vers la droite (sens des LEDs).</summary>
    Right,
    /// <summary>Vers la gauche (sens inverse).</summary>
    Left,
    /// <summary>Du centre vers les bords.</summary>
    CenterOut,
    /// <summary>Des bords vers le centre.</summary>
    EdgesIn,
}

public sealed class EffectSettings
{
    public string Effect { get; set; } = "rainbow";
    public List<Rgb> Colors { get; set; } = [Rgb.FromHex("#FF0000"), Rgb.FromHex("#0000FF")];
    /// <summary>1..100</summary>
    public int Speed { get; set; } = 50;
    /// <summary>0..100</summary>
    public int Brightness { get; set; } = 100;
    public EffectDirection Direction { get; set; } = EffectDirection.Right;
    /// <summary>1..100 : taille, longueur de traînée, densité... selon l'effet.</summary>
    public int Size { get; set; } = 50;
    /// <summary>Transitions douces (dégradé) ou nettes.</summary>
    public bool Smooth { get; set; } = true;

    public EffectSettings Clone() => new()
    {
        Effect = Effect,
        Colors = [.. Colors],
        Speed = Speed,
        Brightness = Brightness,
        Direction = Direction,
        Size = Size,
        Smooth = Smooth,
    };

    /// <summary>Signature des réglages qui imposent de recréer l'état interne de l'effet.</summary>
    internal string StructuralKey => Effect;
}
