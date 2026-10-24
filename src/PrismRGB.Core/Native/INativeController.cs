// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using PrismRGB.Core.Devices;

namespace PrismRGB.Core.Native;

/// <summary>Contrôleur matériel piloté directement par PrismRGB (pilote natif).</summary>
public interface INativeController : IDisposable
{
    /// <summary>Description de l'appareil avec les tailles de zones actuelles.</summary>
    RgbDevice Device { get; }

    /// <summary>
    /// Applique les tailles des zones réglables (clé de zone -> nombre de LEDs), passe le contrôleur
    /// en mode contrôle logiciel et reconstruit <see cref="Device"/>.
    /// </summary>
    void Configure(IReadOnlyDictionary<string, int> zoneSizes);

    /// <summary>Envoie une image (une couleur par LED de <see cref="Device"/>). Lève IOException si l'appareil a disparu.</summary>
    void Write(ReadOnlySpan<Rgb> colors);
}

/// <summary>Résultat d'une détection : contrôleur prêt, ou raison pour laquelle il n'est pas piloté.</summary>
public sealed record DetectionResult(INativeController? Controller, string Summary, string? Problem = null);
