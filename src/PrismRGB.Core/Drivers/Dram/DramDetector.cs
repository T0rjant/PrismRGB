// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using PrismRGB.Core.Native;
using PrismRGB.Core.Smbus;

namespace PrismRGB.Core.Drivers.Dram;

/// <summary>
/// Détection du RGB des barrettes. Chaque pilote n'interroge le bus que si une barrette de sa marque
/// est présente (identifiée par le SPD, ou à défaut par Windows) : on ne parle jamais "à l'aveugle"
/// à des adresses inconnues.
/// </summary>
public static class DramDetector
{
    private static readonly ushort[] EneVendors =
        [Jedec.GSkill, Jedec.TeamGroup, Jedec.Adata, Jedec.Thermaltake, Jedec.Mushkin, Jedec.Gigabyte, Jedec.Patriot];

    private static readonly ushort[] KnownVendors =
        [Jedec.Kingston, Jedec.Kingston2, Jedec.Kingston3, Jedec.Corsair, .. EneVendors];

    /// <summary>Fabricant d'après le nom renvoyé par Windows (WMI), quand le SPD n'est pas lisible.</summary>
    public static ushort VendorFromName(string manufacturer)
    {
        string m = manufacturer.ToLowerInvariant().Replace(" ", "").Replace(".", "").Replace("-", "");
        if (m.Contains("kingston") || m.Contains("hyperx")) return Jedec.Kingston;
        if (m.Contains("corsair")) return Jedec.Corsair;
        if (m.Contains("gskill")) return Jedec.GSkill;
        if (m.Contains("teamgroup") || m.StartsWith("team")) return Jedec.TeamGroup;
        if (m.Contains("adata") || m.Contains("xpg")) return Jedec.Adata;
        if (m.Contains("thermaltake")) return Jedec.Thermaltake;
        if (m.Contains("mushkin")) return Jedec.Mushkin;
        if (m.Contains("gigabyte") || m.Contains("aorus")) return Jedec.Gigabyte;
        if (m.Contains("patriot")) return Jedec.Patriot;
        return 0;
    }

    public static List<INativeController> Detect(ISmbus bus, IReadOnlyList<DramSlot> slots, IEnumerable<string> windowsManufacturers)
    {
        var vendors = new HashSet<ushort>(slots.Select(s => s.JedecId).Where(KnownVendors.Contains));
        foreach (var name in windowsManufacturers)
        {
            var v = VendorFromName(name);
            if (v != 0) vendors.Add(v);
        }

        // Barrettes dont le SPD n'a pas donné de fabricant connu : on leur attribue celui annoncé par Windows.
        ushort fallback = vendors.Count == 1 ? vendors.First() : (ushort)0;
        var effective = slots
            .Select(s => !KnownVendors.Contains(s.JedecId) && fallback != 0 ? s with { JedecId = fallback } : s)
            .ToList();

        var found = new List<INativeController>();
        if (vendors.Any(Jedec.IsKingston))
        {
            var k = KingstonFuryController.Detect(bus, effective);
            if (k is not null) found.Add(k);
        }
        if (vendors.Contains(Jedec.Corsair)) found.AddRange(CorsairDramController.Detect(bus));
        if (vendors.Overlaps(EneVendors)) found.AddRange(EneDramController.Detect(bus));
        return found;
    }
}
