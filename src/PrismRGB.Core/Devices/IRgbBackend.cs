// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace PrismRGB.Core.Devices;

public enum BackendState { Stopped, Starting, Connecting, Detecting, Connected, Reconnecting, Error }

/// <summary>
/// Source d'appareils RGB. Un backend découvre les appareils et leur envoie des images (une couleur par LED).
/// Les implémentations doivent être tolérantes : SubmitFrame ne bloque jamais et ne lève jamais d'exception.
/// </summary>
public interface IRgbBackend : IAsyncDisposable
{
    string Name { get; }
    BackendState State { get; }
    string StatusText { get; }
    IReadOnlyList<RgbDevice> Devices { get; }

    event EventHandler? DevicesChanged;
    event EventHandler? StateChanged;

    Task StartAsync(CancellationToken ct = default);

    /// <summary>Dépose la dernière image d'un appareil. La plus récente gagne si l'envoi est en retard.</summary>
    void SubmitFrame(RgbDevice device, ReadOnlySpan<Rgb> colors);

    /// <summary>Redemande la liste du matériel.</summary>
    Task RescanAsync();

    /// <summary>Remet tous les appareils en mode "direct" (contrôle logiciel).</summary>
    Task ReapplyAsync();

    Task ResizeZoneAsync(RgbDevice device, int zoneIndex, int ledCount);
}
