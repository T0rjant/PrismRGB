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
