// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using PrismRGB.App;
using PrismRGB.App.ViewModels;
using PrismRGB.App.Views;
using PrismRGB.Core.Config;
using PrismRGB.Core.Platform;

var outDir = args.Length > 0 ? args[0] : "snapshots";
Directory.CreateDirectory(outDir);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var host = new AppHost(forceDemo: true);
var vm = new MainViewModel(host);
await host.StartAsync();
var win = new MainWindow { DataContext = vm, Width = 1440, Height = 940 };
win.Show();

// Apparences réalistes pour la démo : ventilos chaînés sur les connecteurs.
foreach (var d in host.Backend!.Devices)
    foreach (var z in d.Zones)
        if (z.Name.StartsWith("JRAINBOW1") || z.Name.Contains("Façade") || z.Name.Contains("Arrière") || z.Name.Contains("radiateur") || z.Name == "Pompe")
            host.Config.ZoneLooks[ZoneKeys.Zone(d.Key, z.Index, z.Name)] = new ZoneLook { Kind = LookKind.Fans, Fans = z.Name.StartsWith("JRAINBOW1") ? 2 : 1 };

void Pump(int ms)
{
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end)
    {
        Dispatcher.UIThread.RunJobs();
        win.OnFrame();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(15);
    }
}

void Snap(string name, int ms = 500)
{
    Pump(ms);
    win.CaptureRenderedFrame()!.Save(Path.Combine(outDir, name + ".png"));
    Console.WriteLine("saved " + name);
}

vm.SelectedProfile = vm.Profiles.First(p => p.Name == "Arc-en-ciel");
Pump(1500);
Snap("1-eclairage-arcenciel");
vm.SelectedProfile = vm.Profiles.First(p => p.Name == "Météorite bleue");
Snap("2-eclairage-meteorite", 900);
vm.SelectedProfile = vm.Profiles.First(p => p.Name == "Statique violet/cyan");
Snap("3-eclairage-multistatique");
vm.SelectedPage = 1;
Snap("4-appareils");
vm.SelectedPage = 2;
Pump(300);
vm.ScanEntries.Clear();
foreach (var e in new[]
{
    new ScanEntry("Carte mère", "MSI MAG B650 TOMAHAWK WIFI (MS-7D75)", "MSI Mystic Light : connecteurs RGB/ARGB (ventilos, bandes, water cooling branchés dessus)", ScanStatus.Controlled),
    new ScanEntry("RAM", "Kingston FURY DDR5 RGB", "1 contrôleur(s) RGB de RAM piloté(s) via SMBus AMD (PIIX4).", ScanStatus.Controlled),
    new ScanEntry("Carte graphique", "NVIDIA GeForce RTX 5070", "Modèle MSI", ScanStatus.NotYet, "Le RGB des cartes graphiques passe par le bus I2C du pilote NVIDIA/AMD, avec un protocole propre à chaque marque."),
    new ScanEntry("USB", "iCUE COMMANDER CORE", "Corsair  ·  1B1C:0C1C", ScanStatus.NotYet, "Contrôleur détecté. Son pilote peut être ajouté : envoie le rapport (bouton ci-dessus)."),
})
    vm.ScanEntries.Add(new ScanEntryViewModel(e));
Snap("5-materiel");
vm.SelectedPage = 3;
Snap("6-reglages");
host.PrepareShutdown();
await host.DisposeAsync();
