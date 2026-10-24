# Third-party notices / Composants tiers

PrismRGB is licensed under the GNU General Public License v3.0 or later (see `LICENSE`).
It contains or depends on the following third-party work.

| Component | Usage in PrismRGB | License |
|---|---|---|
| [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) | Hardware protocols ported to C# (MSI Mystic Light 185/761, ASUS Aura USB, ASRock Polychrome USB, Gigabyte RGB Fusion 2 USB, Kingston FURY, ENE, Corsair DRAM, SPD accessors). Each ported file says so in its header comment. | GPL-2.0-or-later (used under GPL-3.0) |
| [mystic-why](https://github.com/garashchenko/mystic-why) | Origin of the MSI 185-byte direct mode, via OpenRGB | see project |
| [PawnIO modules](https://github.com/namazso/PawnIO.Modules) | Official signed `SmbusPIIX4.bin` and `SmbusI801.bin` modules embedded unmodified (`src/PrismRGB.Core/Smbus/Modules`) | LGPL-2.1-or-later (`COPYING.LGPL-2.1`) |
| [PawnIO](https://pawnio.eu/) | Signed kernel driver installed separately by the user for RAM RGB. Not redistributed. | GPL-2.0 |
| [Avalonia UI](https://avaloniaui.net/) | User interface framework | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM helpers | MIT |
| [System.Management](https://www.nuget.org/packages/System.Management) | Windows hardware inventory (WMI) | MIT |
| [FastLED Fire2012](https://github.com/FastLED/FastLED) | Algorithm of the "Fire" effect (Mark Kriegsman), reimplemented | MIT |
| .NET runtime | Bundled in the self-contained executable | MIT |

The full source code of PrismRGB, including all ported drivers, is available in this repository.
