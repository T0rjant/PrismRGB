// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace PrismRGB.Core.Hid;

/// <summary>Description d'une collection HID (un périphérique USB peut en exposer plusieurs).</summary>
public sealed record HidDeviceInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    string Manufacturer,
    string Product,
    string Serial,
    int InputReportLength,
    int OutputReportLength,
    int FeatureReportLength)
{
    public string VidPid => $"{VendorId:X4}:{ProductId:X4}";
}

public interface IHidDevice : IDisposable
{
    HidDeviceInfo Info { get; }

    /// <summary>
    /// Lit un "feature report". buffer[0] doit contenir l'identifiant du report.
    /// Retourne le nombre d'octets réellement renvoyés par l'appareil (identifiant compris), ou -1.
    /// </summary>
    int GetFeature(byte[] buffer, int length);

    /// <summary>Envoie un "feature report" (data[0] = identifiant). Complété par des zéros si nécessaire.</summary>
    bool SetFeature(ReadOnlySpan<byte> data);

    /// <summary>Envoie un "output report" (data[0] = identifiant, 0 si aucun). Complété par des zéros.</summary>
    bool Write(ReadOnlySpan<byte> data);

    /// <summary>
    /// Lit un "input report", comme hidapi : identifiant en buffer[0], sauf s'il vaut 0 (alors retiré).
    /// Retourne le nombre d'octets lus, 0 si le délai est dépassé, -1 en cas d'erreur.
    /// </summary>
    int Read(byte[] buffer, int timeoutMs);
}

public interface IHidProvider
{
    IReadOnlyList<HidDeviceInfo> Enumerate();
    IHidDevice? Open(HidDeviceInfo info);
}

/// <summary>Fournisseur vide (hors Windows) : aucun appareil.</summary>
public sealed class NullHidProvider : IHidProvider
{
    public IReadOnlyList<HidDeviceInfo> Enumerate() => [];
    public IHidDevice? Open(HidDeviceInfo info) => null;
}

public static class HidProviders
{
    public static IHidProvider Default() => OperatingSystem.IsWindows() ? new WinHidProvider() : new NullHidProvider();
}
