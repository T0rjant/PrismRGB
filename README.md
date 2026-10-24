# PrismRGB

Logiciel Windows pour piloter le RGB du PC depuis une seule interface, sans aucun autre logiciel : PrismRGB parle directement aux contrôleurs USB.

**À lancer : [PrismRGB.exe](PrismRGB.exe)** (Windows x64, autonome, aucune installation, pas de droits administrateur). Mode d'emploi : [LISEZ-MOI.txt](LISEZ-MOI.txt).

## Ce qui est piloté aujourd'hui

| Matériel | État |
|---|---|
| Cartes mères MSI Mystic Light, protocole 185 (B550, B650, X670, Intel 400-700) | Natif, LED par LED |
| Cartes mères MSI Mystic Light, protocole 761 (B850, X870, Z890) | Natif, LED par LED |
| Tout ce qui est branché sur leurs connecteurs JRGB / JRAINBOW / JARGB / JAF (ventilos, bandes, water cooling) | Natif |
| RAM RGB | Détectée ; nécessite un accès SMBus (pilote système) |
| Carte graphique | Détectée (marque comprise) ; protocole I2C propre à chaque marque |
| Contrôleurs USB (Corsair, NZXT, Lian Li, ASUS, Gigabyte...) | Détectés ; pilotes à ajouter selon le modèle |

L'onglet **Matériel** de l'application montre exactement ce qui est présent dans le PC et produit un rapport (`PrismRGB-rapport.txt`) qui sert de base pour écrire les pilotes suivants.

## Effets

Statique, Statique multicolore, Respiration, Flash, Arc-en-ciel, Cycle spectre, Vague, Météorite, Scanner gauche/droite, Étoiles, Feu, Police, Chenillard, Remplissage, Ondes. Couleurs (jusqu'à 8, palettes), vitesse, luminosité, taille, direction (droite, gauche, centre vers bords, bords vers centre), transitions douces ou nettes.

- **Traverser tout le PC** : les zones sont mises bout à bout (ordre réglable), l'effet passe d'un appareil à l'autre.
- **Effet propre** par zone, zones désactivables ou inversées, luminosité générale, bouton marche/arrêt.
- Aperçu en direct du PC (ventilos en anneaux, barrettes, carte graphique, bandes) et miniatures animées des effets.

## Fiabilité

- Un thread d'envoi par contrôleur, « la plus récente image gagne » : un contrôleur lent ne bloque rien.
- Contrôleur perdu (veille, débranchement) : fermé puis redétecté automatiquement.
- Sortie de veille détectée : les contrôleurs sont reconfigurés.
- Seules les cartes validées par la communauté sont pilotées par défaut ; aucune écriture en mémoire flash.
- Détection des logiciels RGB concurrents (cause n°1 des « ça marche un coup sur deux »).

## Architecture

```
src/PrismRGB.Core
  Hid/        accès HID natif Windows (setupapi + hid.dll), sans dépendance
  Drivers/    pilotes natifs (MSI Mystic Light 185 et 761)
  Native/     backend natif : détection, threads d'envoi, reprise sur erreur
  Effects/    15 effets
  Engine/     boucle d'animation (envoi si changement + maintien périodique)
  Platform/   inventaire WMI, scanner matériel, logiciels concurrents, démarrage auto
src/PrismRGB.App      interface Avalonia
tests/PrismRGB.Tests  138 tests (octets envoyés aux cartes MSI simulées, effets, moteur, config)
tools/UiSnapshot      captures de l'interface hors écran
```

## Compiler

Prérequis : SDK .NET 10. `./build-windows.sh` (macOS/Linux) ou `build-windows.ps1` (Windows) : lance les tests, compile et place `PrismRGB.exe` à la racine.

Essayer sans matériel : `dotnet run --project src/PrismRGB.App -- --demo`.

## Licence et crédits

GPL-2.0-or-later. Les protocoles MSI Mystic Light sont portés depuis le travail de rétro-ingénierie de la communauté (projet OpenRGB, mystic-why, SignalRGB). Effet Feu : algorithme Fire2012 de Mark Kriegsman (FastLED).
