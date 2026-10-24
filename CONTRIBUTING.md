# Contribuer / Contributing

## FR

**Votre matériel n'est pas reconnu ?** Lancez PrismRGB, onglet **Matériel**, puis **Enregistrer le rapport**, et ouvrez une issue « Nouveau matériel » avec le fichier `PrismRGB-rapport.txt`. Il contient les identifiants (VID:PID, modèle de carte mère, références des barrettes) nécessaires pour ajouter un pilote.

**Un pilote fonctionne chez vous ?** Dites-le dans une issue : les pilotes marqués « expérimental » deviennent « testé » grâce à vos retours.

**Code :**
- C# / .NET 10, interface Avalonia. `./build-windows.sh` lance les tests puis produit `PrismRGB.exe`.
- Un pilote USB = une classe `INativeController` + une entrée dans `DriverRegistry`. Un pilote de RAM = une détection dans `DramDetector`, toujours conditionnée à la marque détectée.
- Ajoutez des tests avec les faux appareils (`ScriptedHid`, `FakeSmbus`) qui vérifient les octets envoyés.
- Jamais d'écriture en flash, jamais d'écriture SPD, jamais de sondage « à l'aveugle » d'adresses SMBus.
- En contribuant, vous acceptez que votre code soit publié sous GPL-3.0-or-later.

## EN

Hardware not recognized? Open PrismRGB > **Hardware** > **Save report** and attach `PrismRGB-rapport.txt` to a "New hardware" issue. Driver works for you? Tell us so it can move from "experimental" to "tested". Code contributions: one `INativeController` per controller, registered in `DriverRegistry`, with byte-level tests using the fake devices. Contributions are accepted under GPL-3.0-or-later.
