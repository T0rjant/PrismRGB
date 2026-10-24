// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace PrismRGB.Core;

/// <summary>Identité du logiciel (affichée dans l'interface et le rapport).</summary>
public static class AppInfo
{
    public const string Name = "PrismRGB";
    public const string Author = "Torjant";
    /// <summary>Dépôt GitHub officiel (lien « by Torjant »).</summary>
    public const string GitHubUrl = "https://github.com/Torjant/PrismRGB";
    public const string License = "GPL-3.0-or-later";
    /// <summary>Site officiel du pilote PawnIO (accès à la RAM).</summary>
    public const string PawnIoUrl = "https://pawnio.eu/";

    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
