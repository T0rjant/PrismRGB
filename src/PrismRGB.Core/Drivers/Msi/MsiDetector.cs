// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using PrismRGB.Core.Hid;
using PrismRGB.Core.Native;
using PrismRGB.Core.Platform;

namespace PrismRGB.Core.Drivers.Msi;

/// <summary>
/// Détection des contrôleurs MSI Mystic Light, même séquence qu'OpenRGB (MSIMotherboardControllerDetect) :
/// la taille du report 0x52 indique la génération du protocole.
/// </summary>
public static class MsiDetector
{
    public static DetectionResult Detect(IHidProvider provider, HidDeviceInfo info, SystemInfo sys, bool allowUntested)
    {
        string boardName = !string.IsNullOrWhiteSpace(sys.BoardProduct) ? sys.MsiBoardName
            : string.IsNullOrWhiteSpace(info.Product) ? "Carte mère MSI" : $"MSI {info.Product}";

        var dev = provider.Open(info);
        if (dev is null)
            return new(null, boardName, "Impossible d'ouvrir le contrôleur (un autre logiciel RGB le bloque peut-être).");

        bool keep = false;
        try
        {
            var buf = new byte[200];
            buf[0] = 0x52;
            int len = dev.GetFeature(buf, buf.Length);

            if (len is >= Msi185Controller.ConfigLength and <= Msi185Controller.ConfigLength + 1)
            {
                ushort pid = info.ProductId;
                // Les cartes à PID commun indiquent leur vrai PID dans les 4 premiers caractères du numéro de série.
                if (pid == MsiBoards.PidCommon && info.Serial.Length >= 4 &&
                    ushort.TryParse(info.Serial[..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var real))
                    pid = real;

                var cfg = MsiBoards.Find185(pid);
                if (cfg is null)
                    return new(null, boardName, $"Carte MSI (protocole 185, PID {pid:X4}) pas encore répertoriée.");
                if (cfg.Mode != MsiDirectMode.PerLed)
                    return new(null, boardName, "Cette carte ne permet pas le contrôle LED par LED (modes matériels uniquement).");

                keep = true;
                return new(new Msi185Controller(dev, cfg, boardName), $"{boardName} (Mystic Light 185)");
            }

            if (len is >= 162 and <= 163 or >= 112 and <= 113)
                return new(null, boardName, "Ancienne génération Mystic Light (avant 2019) : pas encore prise en charge.");

            // Génération 761 octets (B850 / X870 / Z890) : séquence d'activation puis vérification du modèle.
            var enable = new byte[290];
            enable[0] = 0x50;
            dev.GetFeature(enable, enable.Length);
            var probe = new byte[761];
            probe[0] = 0x51;
            if (!dev.SetFeature(probe))
                return new(null, boardName, $"Contrôleur MSI qui ne répond pas (taille de report {len}).");

            var cfg761 = MsiBoards.Find761(sys.MsiBoardName);
            bool untested = cfg761 is null;
            if (untested && !allowUntested)
                return new(null, boardName, "Carte MSI récente pas encore testée par la communauté. Active « Cartes MSI non testées » dans Paramètres pour l'essayer.");
            cfg761 ??= MsiBoards.Default761(sys.MsiBoardName);

            var settings = new byte[500];
            settings[0] = 0x51;
            dev.GetFeature(settings, settings.Length);

            keep = true;
            return new(new Msi761Controller(dev, cfg761, boardName), $"{boardName} (Mystic Light 761{(untested ? ", non testée" : "")})");
        }
        catch (Exception ex)
        {
            return new(null, boardName, $"Erreur de communication : {ex.Message}");
        }
        finally
        {
            if (!keep) dev.Dispose();
        }
    }
}
