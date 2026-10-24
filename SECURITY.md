# Sécurité / Security

## FR

**Ce que PrismRGB fait :**
- il parle uniquement aux contrôleurs RGB qu'il reconnaît (liste de modèles connus) via l'USB (HID) et, si vous l'activez, via le bus SMBus de la carte mère pour la RAM ;
- il enregistre sa configuration dans `%APPDATA%\PrismRGB\config.json` ;
- il peut créer une entrée de démarrage automatique (clé « Run » de l'utilisateur, ou tâche planifiée si le contrôle de la RAM est activé) ;
- il peut fermer un logiciel RGB concurrent, uniquement quand vous cliquez sur « Fermer ».

**Ce que PrismRGB ne fait jamais :**
- aucune connexion réseau, aucune télémétrie, aucune publicité, aucune collecte de données ;
- aucun pilote noyau embarqué : l'accès à la RAM passe par [PawnIO](https://pawnio.eu/), pilote signé et open source installé séparément, avec ses modules officiels signés ;
- aucune écriture dans la mémoire de configuration (SPD) de vos barrettes : une couche de protection (`SafeSmbus`) refuse toute écriture vers les adresses 0x50-0x57, hormis la sélection de page standard JEDEC ;
- aucune écriture en mémoire flash des contrôleurs (les réglages d'usine restent intacts après un redémarrage).

**Exécutables :** chaque version publiée est compilée par GitHub Actions depuis ce dépôt, avec son empreinte SHA-256 et une attestation de provenance (`gh attestation verify PrismRGB.exe -R T0rjant/PrismRGB`). L'exécutable n'est pas signé par un certificat commercial : Windows SmartScreen peut afficher un avertissement au premier lancement.

**Signaler une faille :** ouvrez un rapport privé via l'onglet « Security > Report a vulnerability » du dépôt GitHub. Merci de ne pas publier de détails avant correctif.

## EN

PrismRGB only talks to RGB controllers it recognizes (USB HID, and the motherboard SMBus for RAM when you enable it). No network access, no telemetry. No bundled kernel driver: RAM access uses the separately installed, signed, open-source [PawnIO](https://pawnio.eu/) driver with its official signed modules. Writes to the RAM configuration EEPROM (SPD, addresses 0x50-0x57) are blocked in software except the standard JEDEC page select. Controllers' flash memory is never written.

Releases are built by GitHub Actions from this repository, with a SHA-256 checksum and build provenance attestation. Please report vulnerabilities privately through GitHub's "Report a vulnerability".
