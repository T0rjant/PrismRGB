namespace PrismRGB.Core.Effects;

public sealed record EffectInfo(
    string Id,
    string Name,
    string Description,
    Func<Effect> Create,
    int MinColors = 1,
    int MaxColors = 8,
    bool UsesSpeed = true,
    bool UsesDirection = true,
    string? SizeLabel = null,
    string? SmoothLabel = null,
    string[]? SuggestedColors = null)
{
    public bool UsesColors => MaxColors > 0;
    public bool UsesSize => SizeLabel is not null;
    public bool UsesSmooth => SmoothLabel is not null;
}

public static class EffectRegistry
{
    public static IReadOnlyList<EffectInfo> All { get; } =
    [
        new("static", "Statique", "Une seule couleur fixe.", () => new StaticEffect(),
            MaxColors: 1, UsesSpeed: false, UsesDirection: false),
        new("multistatic", "Statique multicolore", "Plusieurs couleurs fixes, en dégradé ou en blocs.", () => new MultiStaticEffect(),
            MinColors: 2, UsesSpeed: false, SmoothLabel: "Dégradé (sinon blocs)"),
        new("breathing", "Respiration", "Montée et descente douce, une couleur par cycle.", () => new BreathingEffect(),
            UsesDirection: false, SmoothLabel: "Ne jamais s'éteindre complètement"),
        new("flash", "Flash", "Éclairs / stroboscope, la couleur change à chaque flash.", () => new FlashEffect(),
            UsesDirection: false, SizeLabel: "Durée du flash", SmoothLabel: "Extinction progressive"),
        new("rainbow", "Arc-en-ciel", "Arc-en-ciel qui défile.", () => new RainbowEffect(),
            MaxColors: 0, SizeLabel: "Largeur des vagues"),
        new("spectrum", "Cycle spectre", "Tout change de couleur ensemble sur tout le spectre.", () => new SpectrumEffect(),
            MaxColors: 0, UsesDirection: false),
        new("colorwave", "Vague", "Vague des couleurs choisies qui défile.", () => new ColorWaveEffect(),
            MinColors: 2, SizeLabel: "Largeur des vagues", SmoothLabel: "Transitions douces"),
        new("meteor", "Météorite", "Une tête lumineuse suivie d'une traînée qui s'estompe.", () => new MeteorEffect(),
            SizeLabel: "Longueur de la traînée", SmoothLabel: "Traînée lisse (sinon scintillante)"),
        new("scanner", "Scanner gauche/droite", "Un œil qui fait l'aller-retour (façon K2000).", () => new ScannerEffect(),
            SizeLabel: "Largeur", SmoothLabel: "Changer de couleur à chaque passage"),
        new("twinkle", "Étoiles", "Des LEDs scintillent au hasard.", () => new TwinkleEffect(),
            UsesDirection: false, SizeLabel: "Densité", SmoothLabel: "Mélanger les couleurs"),
        new("fire", "Feu", "Flammes animées. Couleurs = du plus froid au plus chaud.", () => new FireEffect(),
            SizeLabel: "Hauteur des flammes", SuggestedColors: ["#B40000", "#FF5A00", "#FFC850"]),
        new("police", "Police", "Gyrophare : les deux moitiés flashent en alternance.", () => new PoliceEffect(),
            MinColors: 2, MaxColors: 2, UsesDirection: false, SuggestedColors: ["#FF0000", "#0030FF"]),
        new("chase", "Chenillard", "Des points espacés qui avancent.", () => new ChaseEffect(),
            SizeLabel: "Espacement", SmoothLabel: "Mouvement fluide"),
        new("wipe", "Remplissage", "La bande se remplit d'une couleur, puis de la suivante.", () => new WipeEffect(),
            SmoothLabel: "Bord adouci"),
        new("ripple", "Ondes", "Des ondes de couleur naissent au hasard et s'étendent.", () => new RippleEffect(),
            UsesDirection: false, SizeLabel: "Fréquence"),
    ];

    public static EffectInfo Get(string? id) => All.FirstOrDefault(e => e.Id == id) ?? All[0];
}
