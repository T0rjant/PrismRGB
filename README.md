# PrismRGB

Logiciel Windows pour piloter tout le RGB du PC (carte mère, RAM, GPU, water cooling, ventilos, bandes LED) depuis une seule interface, avec des effets animés synchronisés d'un appareil à l'autre.

Le `.exe` prêt à lancer : `dist/PrismRGB/PrismRGB.exe` (Windows x64, autonome, aucun .NET à installer). Mode d'emploi : [LISEZ-MOI.txt](LISEZ-MOI.txt).

## Effets

Statique, Statique multicolore (dégradé ou blocs), Respiration, Flash/stroboscope, Arc-en-ciel, Cycle spectre, Vague, Météorite, Scanner gauche/droite (K2000), Étoiles, Feu, Police, Chenillard, Remplissage, Ondes.

Pour chaque effet : couleurs (jusqu'à 8, palettes rapides), vitesse, luminosité, taille/longueur, direction (droite, gauche, centre vers bords, bords vers centre), transitions douces ou nettes.

- **Synchronisé** : toutes les zones sont mises bout à bout dans l'ordre choisi (onglet Appareils), la vague ou la météorite traverse tout le PC.
- **Séparé** : chaque zone joue l'effet de son côté.
- **Effet propre** : une zone peut avoir son effet à elle (ex. RAM en arc-en-ciel, reste en statique).
- Zones désactivables, inversables (ventilo monté à l'envers), headers ARGB redimensionnables.
- Profils illimités, accessibles aussi depuis l'icône de la zone de notification.

## Architecture

```
src/PrismRGB.Core     moteur, effets, protocole OpenRGB, config (aucune dépendance UI, testé)
  Effects/            15 effets ; chaque effet dessine une bande "logique", la direction est appliquée après
  Engine/             boucle d'animation à cadence fixe, envoi "seulement si changé" + maintien périodique
  OpenRgb/            client du SDK OpenRGB (TCP 6742) + gestion du serveur
  Demo/               appareils simulés
  Platform/           logiciels concurrents, diagnostic matériel (WMI), démarrage auto
src/PrismRGB.App      interface Avalonia (fenêtre, aperçu LED en direct, zone de notification)
tests/PrismRGB.Tests  138 tests : protocole contre un faux serveur OpenRGB, effets, moteur, config
tools/UiSnapshot      rend l'interface hors écran en PNG (vérification visuelle sans Windows)
```

### Pourquoi OpenRGB en coulisses

Écrire de zéro les pilotes de chaque contrôleur (SMBus de la RAM, Mystic Light, contrôleurs de ventilos, GPU...) demande de connaître le matériel exact et de le tester sur la machine. OpenRGB a déjà ces pilotes ; PrismRGB ne garde que cette couche, lancée en serveur invisible (`--server --noautoconnect`), et remplace tout le reste : effets, profils, interface et surtout la fiabilité :

- lancement automatique du serveur, relance s'il plante ou se bloque ;
- reconnexion automatique, relecture du matériel quand OpenRGB signale un changement ;
- tailles des zones ARGB et mode « direct » réappliqués à chaque connexion ;
- images renvoyées périodiquement (les contrôleurs qui se réinitialisent seuls reprennent leurs couleurs) ;
- envoi sur un thread dédié, « la plus récente gagne » : un contrôleur lent (RAM) ne fait jamais saturer le reste ;
- détection des logiciels RGB concurrents, première cause des « ça marche un coup sur deux ».

L'interface `IRgbBackend` permet d'ajouter des pilotes natifs (HID direct, Windows Dynamic Lighting...) appareil par appareil. Le rapport de diagnostic (Paramètres) liste la carte mère, la RAM, le GPU et tous les VID:PID USB : c'est la base pour les écrire.

## Compiler

Prérequis : SDK .NET 10.

```bash
./build-windows.sh
```

Depuis Windows : `build-windows.ps1`. Les deux lancent les tests puis produisent `dist/PrismRGB/PrismRGB.exe`.

Essayer l'interface sans matériel (Mac ou PC) : `dotnet run --project src/PrismRGB.App -- --demo`.

## Fichiers

- Configuration et profils : `%APPDATA%\PrismRGB\config.json`
- Rapport de diagnostic : `PrismRGB-diagnostic.txt` sur le bureau

## Crédits

- Protocole : documentation du SDK OpenRGB (`Documentation/OpenRGBSDK.md`), recoupée avec openrgb-python.
- Effet Feu : algorithme « Fire2012 » de Mark Kriegsman (FastLED).
