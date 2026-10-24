// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using PrismRGB.Core.Drivers.AsRock;
using PrismRGB.Core.Drivers.Asus;
using PrismRGB.Core.Drivers.Gigabyte;
using PrismRGB.Core.Drivers.Msi;
using PrismRGB.Core.Hid;
using PrismRGB.Core.Platform;

namespace PrismRGB.Core.Native;

/// <summary>Pilote USB : reconnaît ses appareils parmi les collections HID, puis les initialise.</summary>
public sealed record HidDriver(
    string Name,
    Func<HidDeviceInfo, bool> IsCandidate,
    Func<IHidProvider, HidDeviceInfo, SystemInfo, bool, DetectionResult> Detect);

public static class DriverRegistry
{
    /// <summary>Tous les pilotes USB natifs, essayés automatiquement au démarrage et à chaque branchement.</summary>
    public static IReadOnlyList<HidDriver> Hid { get; } =
    [
        new("MSI Mystic Light", MsiBoards.IsCandidate, (p, i, s, untested) => MsiDetector.Detect(p, i, s, untested)),
        new("ASUS Aura USB", AsusAuraUsbController.IsCandidate, (p, i, s, _) => AsusAuraUsbController.Detect(p, i, s)),
        new("ASRock Polychrome USB", PolychromeUsbController.IsCandidate, (p, i, s, _) => PolychromeUsbController.Detect(p, i, s)),
        new("Gigabyte RGB Fusion 2", Fusion2UsbController.IsCandidate, (p, i, s, _) => Fusion2UsbController.Detect(p, i, s)),
    ];

    /// <summary>Une collection par appareil physique et par pilote.</summary>
    public static IEnumerable<(HidDriver driver, HidDeviceInfo info)> Candidates(IReadOnlyList<HidDeviceInfo> all)
    {
        var seen = new HashSet<(string, ushort, ushort, string)>();
        foreach (var d in all)
            foreach (var driver in Hid)
                if (driver.IsCandidate(d) && seen.Add((driver.Name, d.VendorId, d.ProductId, d.Serial)))
                    yield return (driver, d);
    }
}
