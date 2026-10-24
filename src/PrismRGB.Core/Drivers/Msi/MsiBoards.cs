// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace PrismRGB.Core.Drivers.Msi;

/*
 * Tables des cartes MSI Mystic Light.
 * Portées depuis OpenRGB (Controllers/MSIMotherboardController, GPL-2.0-or-later) :
 * seules les cartes testées par la communauté y figurent, pour éviter tout risque sur des firmwares inconnus.
 */

public enum MsiZone
{
    None = 0,
    JRgb1 = 1, JRgb2 = 2,
    JPipe1 = 3, JPipe2 = 4,
    JRainbow1 = 8, JRainbow2 = 9, JRainbow3 = 10,
    JCorsair = 11,
    OnBoard = 13,
    Jaf = 24, JArgb1 = 25, JArgb2 = 26, JArgb3 = 27,
}

public enum MsiDirectMode { Disabled, PerLed, ZoneBased }

public sealed record Msi185Config(ushort Pid, int OnboardLeds, int Pipe1Leds, int Pipe2Leds, int JRgbCount, MsiZone[] Zones, MsiDirectMode Mode, string Comment);

public sealed record Msi761Config(string BoardName, int Pipe1Leds, MsiZone[] Zones);

public static class MsiBoards
{
    public const ushort Vid = 0x1462;
    public const ushort VidCommon = 0x0DB0;
    public const ushort PidCommon = 0x0076;

    /// <summary>PID déclarés par OpenRGB (détecteurs actifs, hors liste "non testée").</summary>
    public static readonly HashSet<ushort> KnownPids =
    [
        0x1720, 0x7B12, 0x7B16, 0x7B17, 0x7B18, 0x7B50, 0x7B85, 0x7B92, 0x7B93, 0x7C02, 0x7C34, 0x7C35, 0x7C36, 0x7C37,
        0x7C56, 0x7C59, 0x7C60, 0x7C67, 0x7C71, 0x7C73, 0x7C75, 0x7C76, 0x7C77, 0x7C79, 0x7C80, 0x7C81, 0x7C82, 0x7C83,
        0x7C84, 0x7C86, 0x7C87, 0x7C90, 0x7C91, 0x7C92, 0x7C94, 0x7C95, 0x7C98, 0x7D03, 0x7D04, 0x7D06, 0x7D07, 0x7D08,
        0x7D09, 0x7D13, 0x7D14, 0x7D15, 0x7D17, 0x7D18, 0x7D19, 0x7D20, 0x7D25, 0x7D27, 0x7D28, 0x7D29, 0x7D30, 0x7D31,
        0x7D32, 0x7D33, 0x7D36, 0x7D37, 0x7D38, 0x7D40, 0x7D41, 0x7D42, 0x7D43, 0x7D46, 0x7D50, 0x7D51, 0x7D52, 0x7D53,
        0x7D54, 0x7D59, 0x7D67, 0x7D69, 0x7D70, 0x7D73, 0x7D74, 0x7D75, 0x7D76, 0x7D77, 0x7D78, 0x7D86, 0x7D88, 0x7D89,
        0x7D90, 0x7D91, 0x7D93, 0x7D96, 0x7D97, 0x7D98, 0x7D99, 0x7E01, 0x7E03, 0x7E06, 0x7E07, 0x7E09, 0x7E10, 0x7E20,
        0x7E30, 0x7E32, 0x7E34, 0x7E59, 0x7E66, 0x7E70, 0x7E76, 0x7E80, 0x7E81, 0x7E86, 0xB926,
        0x7E12, 0x7E16, 0x7E24, 0x7E26, 0x7E27, 0x7E49,
    ];

    private static readonly MsiZone[] Set0 = [MsiZone.JRgb1, MsiZone.JRgb2, MsiZone.JRainbow1, MsiZone.JRainbow2, MsiZone.OnBoard];
    private static readonly MsiZone[] Set1 = [MsiZone.JRgb1, MsiZone.JRainbow1, MsiZone.JRainbow2, MsiZone.JCorsair];
    private static readonly MsiZone[] Set2 = [MsiZone.JRgb1, MsiZone.JRainbow1, MsiZone.JRainbow2, MsiZone.OnBoard];
    private static readonly MsiZone[] Set3 = [MsiZone.JRgb1, MsiZone.JRainbow1, MsiZone.JRainbow2, MsiZone.JCorsair, MsiZone.JPipe1, MsiZone.OnBoard];
    private static readonly MsiZone[] Set5 = [MsiZone.JRgb1, MsiZone.JRainbow1];
    private static readonly MsiZone[] Set6 = [MsiZone.JRainbow1];
    private static readonly MsiZone[] Set7 = [MsiZone.JRgb1, MsiZone.JRgb2, MsiZone.JRainbow1, MsiZone.JRainbow2, MsiZone.JPipe1, MsiZone.OnBoard];
    private static readonly MsiZone[] Set8 = [MsiZone.JRgb1, MsiZone.JRainbow1, MsiZone.JRainbow2, MsiZone.JCorsair, MsiZone.JPipe1, MsiZone.JPipe2];
    private static readonly MsiZone[] Set9 = [MsiZone.JRgb1, MsiZone.JRainbow1, MsiZone.JRainbow2, MsiZone.JCorsair, MsiZone.JPipe1];
    private static readonly MsiZone[] Set10 = [MsiZone.JRgb1, MsiZone.JRainbow1, MsiZone.JRainbow2, MsiZone.JRainbow3];
    private static readonly MsiZone[] Set11 = [MsiZone.JRgb1, MsiZone.JRainbow1, MsiZone.JRainbow2];
    private static readonly MsiZone[] Set12 = [MsiZone.JRgb1, MsiZone.JRainbow1, MsiZone.JRainbow2, MsiZone.JRainbow3, MsiZone.JPipe1, MsiZone.OnBoard];
    private static readonly MsiZone[] Set13 = [MsiZone.JRgb1, MsiZone.JRgb2, MsiZone.JRainbow1, MsiZone.JRainbow2];
    private static readonly MsiZone[] Set14 = [MsiZone.JRainbow1, MsiZone.JRainbow2];
    private static readonly MsiZone[] Set15 = [MsiZone.JRgb1, MsiZone.JRainbow1, MsiZone.JRainbow2, MsiZone.JRainbow3, MsiZone.JPipe1, MsiZone.JPipe2, MsiZone.OnBoard];
    private static readonly MsiZone[] Set16 = [MsiZone.JRgb1, MsiZone.JRainbow1, MsiZone.JRainbow2, MsiZone.JRainbow3, MsiZone.JPipe1, MsiZone.JPipe2];
    private static readonly MsiZone[] Set17 = [MsiZone.JRgb1, MsiZone.JRgb2, MsiZone.JRainbow1, MsiZone.JRainbow2, MsiZone.JPipe1];
    private static readonly MsiZone[] Set18 = [MsiZone.JRgb1, MsiZone.JRainbow1, MsiZone.JRainbow2, MsiZone.JRainbow3, MsiZone.JPipe1];
    private static readonly MsiZone[] Set19 = [MsiZone.JRainbow1, MsiZone.JRainbow2, MsiZone.JRainbow3];
    private static readonly MsiZone[] Set20 = [MsiZone.JRgb1];
    private static readonly MsiZone[] Set21 = [MsiZone.JRgb1, MsiZone.JRgb2, MsiZone.JRainbow1, MsiZone.JRainbow2, MsiZone.JCorsair, MsiZone.JPipe1, MsiZone.JPipe2, MsiZone.OnBoard];

    private const MsiDirectMode P = MsiDirectMode.PerLed, Z = MsiDirectMode.ZoneBased, D = MsiDirectMode.Disabled;

    /// <summary>Configurations des cartes 185 octets (PID, LEDs intégrées, pipes, JRGB, zones, mode direct).</summary>
    public static readonly Msi185Config[] Configs185 =
    [
        new(0x7B93, 6, 1, 0, 1, Set3, Z, "MPG X570 GAMING PRO CARBON WIFI"),
        new(0x7C34, 0, 1, 1, 1, Set8, Z, "MEG X570 GODLIKE"),
        new(0x7C35, 0, 1, 0, 1, Set9, Z, "MEG X570 ACE"),
        new(0x7C36, 6, 1, 0, 1, Set3, Z, "PRESTIGE X570 CREATION"),
        new(0x7C37, 6, 0, 0, 2, Set0, Z, "MPG X570 GAMING PLUS"),
        new(0x7C56, 6, 0, 0, 1, Set2, P, "MPG B550 GAMING PLUS"),
        new(0x7C59, 0, 8, 0, 1, Set9, D, "CREATOR TRX40"),
        new(0x7C60, 6, 0, 0, 1, Set1, P, "TRX40-A PRO"),
        new(0x7C67, 6, 0, 0, 1, Set5, P, "MAG B365M MORTAR"),
        new(0x7C71, 6, 6, 0, 1, Set3, P, "MEG Z490 ACE"),
        new(0x7C73, 6, 4, 0, 1, Set3, P, "MPG Z490 GAMING CARBON WIFI"),
        new(0x7C75, 6, 0, 0, 1, Set2, P, "MPG Z490 GAMING PLUS"),
        new(0x7C76, 6, 0, 0, 2, Set0, D, "MPG Z490M GAMING EDGE"),
        new(0x7C77, 0, 0, 0, 0, Set14, P, "MEG Z490I UNIFY"),
        new(0x7C79, 6, 0, 0, 2, Set0, P, "MPG Z490 GAMING EDGE WIFI"),
        new(0x7C80, 6, 0, 0, 2, Set0, P, "MAG Z490 TOMAHAWK"),
        new(0x7C02, 6, 0, 0, 2, Set0, P, "MAG B450 TOMAHAWK MAX"),
        new(0x7C81, 6, 0, 0, 2, Set0, P, "MAG B460 TOMAHAWK"),
        new(0x7C82, 6, 0, 0, 1, Set5, P, "MAG B460M MORTAR WIFI"),
        new(0x7C83, 6, 0, 0, 1, Set5, D, "B460M PRO-VDH WIFI"),
        new(0x7C84, 6, 0, 0, 2, Set0, Z, "MAG X570 TOMAHAWK WIFI"),
        new(0x7C86, 6, 0, 0, 1, Set5, D, "MPG B460I GAMING EDGE"),
        new(0x7C87, 6, 0, 0, 2, Set13, P, "B450M BAZOOKA MAX WIFI"),
        new(0x7C90, 6, 4, 0, 1, Set3, P, "MPG B550 GAMING CARBON WIFI"),
        new(0x7C91, 6, 0, 0, 2, Set0, P, "MAG B550 TOMAHAWK"),
        new(0x7C92, 6, 0, 0, 0, Set6, P, "MPG B550I GAMING EDGE WIFI"),
        new(0x7C94, 6, 0, 0, 1, Set11, P, "MAG B550M MORTAR"),
        new(0x7C95, 6, 0, 0, 2, Set0, P, "B550M PRO-VDH WIFI"),
        new(0x7C98, 6, 0, 0, 1, Set5, P, "Z490 PLUS"),
        new(0x7D03, 0, 15, 18, 1, Set8, P, "MPG Z590 GODLIKE"),
        new(0x7D06, 4, 4, 0, 1, Set3, P, "MPG Z590 GAMING FORCE"),
        new(0x7D07, 4, 5, 0, 2, Set7, P, "MPG Z590 GAMING EDGE WIFI"),
        new(0x7D08, 6, 0, 0, 2, Set0, P, "MAG Z590 TOMAHAWK"),
        new(0x7D09, 6, 0, 0, 2, Set11, P, "Z590-A PRO WIFI"),
        new(0x7D13, 6, 0, 0, 1, Set1, P, "MEG B550 UNIFY"),
        new(0x7D14, 6, 0, 0, 1, Set5, P, "A520M PRO"),
        new(0x7D15, 6, 0, 0, 2, Set0, P, "MAG B560 TOMAHAWK WIFI"),
        new(0x7D17, 6, 0, 0, 2, Set11, P, "MAG B560M MORTAR"),
        new(0x7D18, 6, 0, 0, 2, Set5, P, "MAG B560M PRO-VDH"),
        new(0x7D19, 6, 0, 0, 2, Set5, P, "MPG B560I GAMING EDGE WIFI"),
        new(0x7D20, 6, 0, 0, 2, Set5, P, "MAG B560M PRO"),
        new(0x7D25, 6, 0, 0, 2, Set11, P, "PRO Z690-A WIFI DDR4"),
        new(0x7D27, 6, 0, 0, 2, Set1, P, "MEG Z690 ACE"),
        new(0x7D28, 6, 0, 0, 1, Set1, P, "MEG Z690 UNIFY-X"),
        new(0x7D29, 6, 0, 0, 0, Set6, P, "MEG Z690I UNIFY"),
        new(0x7D30, 6, 6, 0, 2, Set3, P, "MPG Z690 CARBON WIFI"),
        new(0x7D31, 4, 8, 0, 2, Set12, P, "MPG EDGE WIFI DDR4"),
        new(0x7D32, 1, 0, 0, 1, Set10, P, "MAG Z690 TOMAHAWK WIFI DDR4"),
        new(0x7D33, 6, 0, 0, 2, Set5, P, "PRO Z790-VC WIFI"),
        new(0x7D36, 6, 0, 0, 2, Set5, P, "PRO Z690-P DDR4"),
        new(0x7D37, 6, 0, 0, 2, Set11, P, "PRO B760M-VC WIFI"),
        new(0x7D38, 0, 0, 0, 1, Set1, P, "MEG Z590 UNIFY-X"),
        new(0x7D40, 0, 0, 0, 1, Set5, P, "MPG B760I EDGE WIFI DDR4"),
        new(0x7D41, 6, 0, 0, 2, Set13, P, "MAG B660M TOMAHAWK WIFI DDR4"),
        new(0x7D42, 6, 0, 0, 2, Set11, P, "MAG B660 MORTAR WIFI DDR4"),
        new(0x7D43, 0, 0, 0, 2, Set11, P, "PRO B660M-A WIFI DDR4"),
        new(0x7D46, 0, 1, 1, 0, Set11, P, "PRO H610M-G DDR4"),
        new(0x7D50, 6, 12, 0, 1, Set3, P, "MEG X570S ACE MAX"),
        new(0x7D51, 6, 0, 0, 2, Set1, P, "MEG X570S UNIFY-X MAX"),
        new(0x7D52, 6, 14, 0, 1, Set3, P, "MPG X570S CARBON EK X"),
        new(0x7D53, 6, 0, 0, 2, Set0, P, "MPG X570S EDGE MAX WIFI"),
        new(0x7D54, 6, 0, 0, 2, Set0, P, "MAG X570S TOMAHAWK MAX WIFI"),
        new(0x7D59, 0, 0, 0, 2, Set11, P, "PRO B660-A DDR4"),
        new(0x7D67, 0, 0, 0, 1, Set11, P, "PRO X670-P WIFI"),
        new(0x7D69, 9, 2, 4, 1, Set15, P, "MEG X670E ACE"),
        new(0x7D70, 0, 6, 0, 1, Set9, P, "MPG X670E CARBON WIFI"),
        new(0x7D73, 1, 0, 0, 0, Set6, P, "MPG B650I EDGE WIFI"),
        new(0x7D74, 0, 6, 0, 1, Set18, P, "MPG B650 CARBON WIFI"),
        new(0x7D75, 0, 0, 0, 2, Set13, P, "MAG B650 TOMAHAWK WIFI"),
        new(0x7D76, 0, 0, 0, 2, Set13, P, "MAG B650M MORTAR WIFI"),
        new(0x7D77, 0, 0, 0, 2, Set13, P, "PRO B650M-A WIFI"),
        new(0x7D78, 0, 0, 0, 2, Set13, P, "PRO B650-P WIFI"),
        new(0x7D86, 0, 18, 4, 1, Set16, P, "MEG Z790 ACE"),
        new(0x7D88, 0, 0, 0, 1, Set20, P, "MPG Z790-S WIFI"),
        new(0x7D89, 0, 6, 0, 1, Set18, P, "MPG Z790 CARBON WIFI"),
        new(0x7D90, 0, 0, 0, 1, Set11, P, "B760M BOMBER DDR4"),
        new(0x7D91, 1, 0, 0, 1, Set10, P, "MAG Z790 TOMAHAWK WIFI"),
        new(0x7D93, 6, 0, 0, 1, Set2, P, "Z790 GAMING PRO WIFI"),
        new(0x7D96, 0, 0, 0, 2, Set13, P, "MAG B760 TOMAHAWK WIFI DDR5"),
        new(0x7D97, 6, 0, 0, 2, Set11, P, "MAG B660 MORTAR MAX WIFI DDR4"),
        new(0x7D98, 0, 0, 0, 1, Set11, P, "PRO B760-P WIFI DDR4"),
        new(0x7D99, 6, 0, 0, 1, Set11, P, "PRO B760M-A WIFI DDR4"),
        new(0x7E01, 0, 0, 0, 1, Set11, P, "MAG B760M MORTAR MAX"),
        new(0x7E03, 6, 0, 0, 0, Set6, P, "MPG Z790I EDGE WIFI"),
        new(0x7E06, 0, 0, 0, 2, Set11, P, "PRO Z790-P WIFI DDR4"),
        new(0x7E07, 0, 0, 0, 2, Set10, P, "PRO Z790-A WIFI DDR4"),
        new(0x7E09, 0, 0, 0, 0, Set19, P, "B650M PROJECT ZERO"),
        new(0x7E10, 0, 6, 0, 2, Set17, P, "MPG B650 EDGE WIFI"),
        new(0x7E12, 0, 0, 0, 2, Set13, P, "MAG X670E TOMAHAWK WIFI"),
        new(0x0076, 0, 0, 0, 2, Set13, P, "MAG X670E TOMAHAWK WIFI (PID commun)"),
        new(0x7E16, 0, 0, 0, 2, Set13, P, "X670E GAMING PLUS WIFI"),
        new(0x7E24, 0, 0, 0, 2, Set13, P, "B650M GAMING PLUS WIFI"),
        new(0x7E26, 0, 0, 0, 2, Set13, P, "B650 GAMING PLUS WIFI"),
        new(0x7E27, 0, 0, 0, 2, Set13, P, "PRO B650M-P"),
        new(0x7E28, 6, 0, 0, 2, Set21, Z, "PRO A620M-B"),
        new(0x7E30, 0, 0, 0, 2, Set13, P, "B650M GAMING WIFI"),
    ];

    private static readonly MsiZone[] Zones761Set1 = [MsiZone.Jaf, MsiZone.JArgb1, MsiZone.JArgb2, MsiZone.JArgb3];
    private static readonly MsiZone[] Zones761Set2 = [MsiZone.Jaf, MsiZone.JArgb1, MsiZone.JArgb2, MsiZone.JArgb3, MsiZone.JPipe1];

    /// <summary>Cartes 761 octets (B850 / X870 / Z890), identifiées par leur nom DMI.</summary>
    public static readonly Msi761Config[] Configs761 =
    [
        .. new[]
        {
            "MSI MAG X870 TOMAHAWK WIFI (MS-7E51)", "MSI MAG B850M MORTAR WIFI (MS-7E61)", "MSI MPG B850I EDGE TI WIFI (MS-7E79)",
            "MSI X870 GAMING PLUS WIFI (MS-7E47)", "MSI B850 GAMING PLUS WIFI6E (MS-7E80)", "MSI B850M GAMING PLUS WIFI6E (MS-7E81)",
            "MSI MPG X870E CARBON WIFI (MS-7E49)", "MSI Z890 GAMING PLUS WIFI (MS-7E34)", "MSI X870E GAMING PLUS WIFI (MS-7E70)",
            "MSI MAG X870E TOMAHAWK WIFI (MS-7E59)", "MSI PRO B850-P WIFI (MS-7E56)", "MSI B850M GAMING PLUS WIFI (MS-7E66)",
            "MSI PRO X870E-P WIFI (MS-7E70)", "MSI MPG X870I EDGE TI EVO WIFI (MS-7E50)", "MSI B850 GAMING PLUS WIFI (MS-7E56)",
            "MSI PRO X870-P WIFI (MS-7E47)", "MSI MAG B850 TOMAHAWK MAX WIFI (MS-7E62)", "MSI PRO B850M-P WIFI (MS-7E71)",
            "MSI MAG Z890 TOMAHAWK WIFI (MS-7E32)", "MSI MPG B850 EDGE TI WIFI (MS-7E62)", "MSI PRO B850M-VC WIFI6E (MS-7E71)",
            "MSI MAG B850 TOMAHAWK WIFI (MS-7E53)", "MSI MEG Z890 UNIFY-X (MS-7E20)", "MSI PRO X870E-S EVO WIFI (MS-7E86)",
            "MSI MAG X870E GAMING MAX WIFI (MS-7E86)", "MSI PRO Z890-P WIFI (MS-7E34)", "MSI B850 GAMING PLUS WIFI PZ (MS-7E75)",
            "MSI MAG X870E TOMAHAWK MAX WIFI PZ (MS-7E84)", "MSI PRO B850M-A WIFI PZ (MS-7E78)", "MSI B850MPOWER (MS-7E83)",
            "MSI B840M GAMING WIFI6E (MS-7E76)", "MSI PRO B850M-A WIFI (MS-7E66)", "MSI PRO B850-S WIFI6E (MS-7E80)",
            "MSI B850M GAMING WIFI (MS-7E82)",
        }.Select(n => new Msi761Config(n, 0, Zones761Set1)),
        new("MSI MPG X870E EDGE TI WIFI (MS-7E59)", 10, Zones761Set2),
    ];

    /// <summary>Configuration par défaut pour une carte 761 non listée (identique à presque toutes les cartes listées).</summary>
    public static Msi761Config Default761(string name) => new(name, 0, Zones761Set1);

    public static Msi185Config? Find185(ushort pid) => Configs185.FirstOrDefault(c => c.Pid == pid);

    public static Msi761Config? Find761(string boardName) =>
        Configs761.FirstOrDefault(c => string.Equals(c.BoardName, boardName.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsCandidate(Hid.HidDeviceInfo d) =>
        (d.VendorId == Vid && KnownPids.Contains(d.ProductId) && d.UsagePage == 0x0001 && d.Usage == 0x00)
        || (d.VendorId == VidCommon && d.ProductId == PidCommon && ((d.UsagePage == 0x0001 && d.Usage == 0x00) || (d.UsagePage == 0xFF00 && d.Usage == 0x01)));

    /// <summary>Tout appareil USB MSI qui ressemble à un contrôleur Mystic Light (pour le diagnostic).</summary>
    public static bool LooksLikeMysticLight(Hid.HidDeviceInfo d) =>
        d.VendorId == VidCommon || (d.VendorId == Vid && (d.ProductId >> 12) == 0x7);

    public static string ZoneName(MsiZone z) => z switch
    {
        MsiZone.JRgb1 => "JRGB1",
        MsiZone.JRgb2 => "JRGB2",
        MsiZone.JRainbow1 => "JRAINBOW1",
        MsiZone.JRainbow2 => "JRAINBOW2",
        MsiZone.JRainbow3 => "JRAINBOW3",
        MsiZone.JCorsair => "JCORSAIR",
        MsiZone.JPipe1 => "PIPE1",
        MsiZone.JPipe2 => "PIPE2",
        MsiZone.OnBoard => "LEDs intégrées",
        MsiZone.Jaf => "JAF",
        MsiZone.JArgb1 => "JARGB 1",
        MsiZone.JArgb2 => "JARGB 2",
        MsiZone.JArgb3 => "JARGB 3",
        _ => z.ToString(),
    };
}
