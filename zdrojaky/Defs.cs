using System;
using System.Drawing;

namespace Kocky
{
    public enum Difficulty { Easy, Medium, Hard }

    /// <summary>The cats the player places (the towers).</summary>
    public enum TowerKind { Drapka, Chlupac, Klubickarka, Laserovka, Spac, Pamlskarna }

    /// <summary>Robot vacuums, weakest first. Popping a layer turns a vacuum into its children.</summary>
    public enum VacKind { Robik, Plus, Turbo, Sprint, Raketa, Dvojce, Pancerak, Disko, Keramik, Prumyslak, Obr }

    public enum DmgType { Sharp, Blast, Energy, None }
    public enum ProjKind { Hairball, Yarn, MiniYarn }
    public enum Targeting { First, Last, Strong, Close }
    public enum FxKind { Pop, Swipe, Blast, Beam, Freeze, Coins, Leak, Zzz, Spin, Bite, Trace, Web, Burn, Upgrade }

    /// <summary>
    /// One upgrade tier. The stat fields are added (multiplied) on top of the base values for every tier bought,
    /// in every path; behaviour that numbers cannot express is described per cat in SPEC.md.
    /// </summary>
    public class UpgradeDef
    {
        public readonly string Name, Desc;
        public readonly int Price;
        public float Range;              // + px
        public double IntervalMul = 1;   // attack interval × (0.8 = 25 % more attacks)
        public int Damage, Pierce;       // + per attack
        public float Splash;             // + blast radius px
        public float ShotSpeedMul = 1;   // projectile speed ×
        public bool Detects;             // sees stealth vacuums
        public bool Armor;               // this cat's Sharp damage also pops armored vacuums
        public float BossMul;            // damage × against bosses; the highest bought value counts, 0 = none
        public UpgradeDef(string name, int price, string desc) { Name = name; Price = price; Desc = desc; }
    }

    public class TowerDef
    {
        public TowerKind Kind;
        public string Name, Desc;
        public int Price;
        public float Range;            // px; 0 for the treat stash
        public double Interval;        // seconds between attacks (beam: between damage ticks; Spac: snore tick)
        public int Damage, Pierce;     // pierce = how many vacuums one attack can hit
        public DmgType Dmg;
        public bool Detects;           // sees stealth vacuums without upgrades
        public float Splash;           // blast radius (yarn), 0 otherwise
        public float ShotSpeed;        // projectile px/s, 0 = instant
        public int Fur, Fur2;          // main and secondary fur colour for the art (0xRRGGBB)
        public string[] PathNames;     // Defs.PathCount names
        public UpgradeDef[][] Paths;   // [path][tier-1], Defs.PathCount × Defs.TierCount
    }

    public class VacDef
    {
        public VacKind Kind;
        public string Name;
        public float Speed;            // px/s along the path
        public int Hp;                 // hit points of this layer
        public VacKind[] Children;     // what is left after the layer pops
        public float Radius;           // body radius in px (art and hit tests)
        public bool Armored;           // immune to Sharp damage
        public bool Boss;              // big vacuum: slows and stuns affect it less
        public int Color;              // body colour (0xRRGGBB)
    }

    /// <summary>A no-build zone drawn as furniture by the art.</summary>
    public class Obstacle
    {
        public readonly string Kind;   // "pohovka", "stolek", "kreslo", "kytka", "knihovna", "pelisek"
        public readonly RectangleF R;
        public Obstacle(string kind, RectangleF r) { Kind = kind; R = r; }
    }

    /// <summary>
    /// Static game data: map, path, cats, vacuums, difficulty. Names and enum members are fixed;
    /// numbers (prices, speeds, hit points, ranges, upgrade values) may be tuned for balance.
    /// </summary>
    public static class Defs
    {
        public const float W = 1280, H = 800;
        /// <summary>Playing field (living-room floor); the side panel is to the right of it.</summary>
        public static readonly RectangleF Map = new RectangleF(0, 0, 960, 800);
        public const float PathWidth = 56;     // width of the carpet runner the vacuums follow
        public const float CatRadius = 22;     // footprint of a placed cat
        public const int FinalRound = 40;
        public const int PathCount = 3, TierCount = 5;
        /// <summary>A cat's Range at or above this reaches the whole floor (the sniper path).</summary>
        public const float MapWide = 2000;

        /// <summary>
        /// Crosspath rule: at most two paths may have upgrades and only one of them may go above tier 2
        /// (e.g. 5-2-0, 0-3-1, 2-0-5). True when path can rise by one tier.
        /// </summary>
        public static bool CanRaise(int[] up, int path)
        {
            if (up == null || up.Length != PathCount || path < 0 || path >= PathCount || up[path] >= TierCount) return false;
            int used = 0, high = 0;
            for (int i = 0; i < PathCount; i++)
            {
                int t = i == path ? up[i] + 1 : up[i];
                if (t > 0) used++;
                if (t > 2) high++;
            }
            return used <= 2 && high <= 1;
        }

        /// <summary>The vacuums' route: from the charging dock off the left edge to the cat bed at the top right.</summary>
        public static readonly PointF[] Path =
        {
            new PointF(-40, 130), new PointF(210, 130), new PointF(210, 650), new PointF(440, 650), new PointF(440, 300),
            new PointF(680, 300), new PointF(680, 620), new PointF(860, 620), new PointF(860, 170)
        };

        public static readonly Obstacle[] Obstacles =
        {
            new Obstacle("knihovna", new RectangleF(300, 14, 220, 56)),
            new Obstacle("kytka", new RectangleF(59, 294, 72, 72)),
            new Obstacle("stolek", new RectangleF(495, 400, 130, 150)),
            new Obstacle("kreslo", new RectangleF(735, 360, 90, 100)),
            new Obstacle("pohovka", new RectangleF(20, 700, 170, 80)),
            new Obstacle("pelisek", new RectangleF(805, 62, 110, 100)),
        };

        public static readonly string[] DifficultyNames = { "Lehká", "Střední", "Těžká" };
        public static readonly string[] TargetingNames = { "První", "Poslední", "Nejsilnější", "Nejbližší" };

        public static int StartMoney(Difficulty d) { return d == Difficulty.Easy ? 700 : d == Difficulty.Medium ? 650 : 600; }
        public static int StartLives(Difficulty d) { return d == Difficulty.Easy ? 150 : d == Difficulty.Medium ? 100 : 50; }
        public static float PriceMul(Difficulty d) { return d == Difficulty.Easy ? 0.85f : d == Difficulty.Medium ? 1.1f : 1.2f; }

        static readonly TowerDef[] towers =
        {
            new TowerDef
            {
                Kind = TowerKind.Drapka, Name = "Drápka", Price = 150, Range = 95, Interval = 0.6, Damage = 1, Pierce = 2, Dmg = DmgType.Sharp,
                Fur = 0xF08C2E, Fur2 = 0xFFE8CC,
                Desc = "Rychle švihne tlapkou po vysavačích, které jedou kolem.",
                PathNames = new[] { "Drápy", "Rychlost", "Zuby" },
                Paths = new[]
                {
                    new[]
                    {
                        new UpgradeDef("Ostré drápy", 90, "Švih zasáhne o 2 vysavače víc.") { Pierce = 2 },
                        new UpgradeDef("Dvojitý švih", 210, "Švihá oběma tlapkami: dva útoky naráz."),
                        new UpgradeDef("Tygří spár", 680, "+2 poškození a drápy prorazí i pancéřáky.") { Damage = 2, Armor = true },
                        new UpgradeDef("Lví tlapa", 1550, "+3 poškození, zasáhne o 4 víc, obřím vysavačům dvojnásob.") { Damage = 3, Pierce = 4, BossMul = 2 },
                        new UpgradeDef("Šavlozubá legenda", 4550, "Tři švihy za útok, +8 poškození, obřím trojnásob.") { Damage = 8, BossMul = 3 },
                    },
                    new[]
                    {
                        new UpgradeDef("Kofeinová granule", 110, "Útočí o 30 % rychleji.") { IntervalMul = 0.77 },
                        new UpgradeDef("Noční vidění", 175, "Vidí tiché vysavače a dosáhne dál.") { Detects = true, Range = 15 },
                        new UpgradeDef("Hurikán", 900, "Točí se dokola a zasáhne všechny vysavače v dosahu.") { Damage = 1 },
                        new UpgradeDef("Tornádo", 1800, "Točí se o 65 % rychleji a dosáhne dál.") { IntervalMul = 0.6, Range = 25, Damage = 1 },
                        new UpgradeDef("Kočičí uragán", 4900, "Vír odhazuje vysavače kus zpátky (obří ne), +2 poškození.") { IntervalMul = 0.6, Damage = 2 },
                    },
                    new[]
                    {
                        new UpgradeDef("Dlouhé fousky", 95, "Dosáhne dál.") { Range = 20 },
                        new UpgradeDef("Kousnutí", 240, "Každým třetím útokem i kousne za 3, i do pancíře."),
                        new UpgradeDef("Krokodýlí stisk", 750, "Kouše každým druhým útokem za 10, obřím dvojnásob."),
                        new UpgradeDef("Žraločí čelisti", 1700, "Kouše při každém útoku za 20 a vysavač na chvíli omráčí."),
                        new UpgradeDef("Dračí tesáky", 4550, "Kousnutí za 60, obřím čtyřnásob, a útočí rychleji.") { IntervalMul = 0.8 },
                    },
                },
            },
            new TowerDef
            {
                Kind = TowerKind.Chlupac, Name = "Chlupáč", Price = 260, Range = 170, Interval = 0.95, Damage = 1, Pierce = 3, Dmg = DmgType.Sharp,
                ShotSpeed = 520, Fur = 0x868E96, Fur2 = 0xF1F3F5,
                Desc = "Plive chlupové kuličky daleko a prorazí jimi několik vysavačů.",
                PathNames = new[] { "Kuličky", "Mířidla", "Ostřelování" },
                Paths = new[]
                {
                    new[]
                    {
                        new UpgradeDef("Větší kuličky", 120, "Kulička prorazí o 3 vysavače víc.") { Pierce = 3 },
                        new UpgradeDef("Lepkavé chlupy", 240, "Zasažené vysavače na chvíli zpomalí."),
                        new UpgradeDef("Chlupová lavina", 750, "Plive tři kuličky najednou.") { Damage = 1 },
                        new UpgradeDef("Chlupatá smršť", 1700, "Plive pět kuliček a každá prorazí o 4 víc.") { Pierce = 4 },
                        new UpgradeDef("Žmolková bouře", 4900, "+3 poškození a každým třetím útokem kuličky na všechny strany.") { Damage = 3, Pierce = 6 },
                    },
                    new[]
                    {
                        new UpgradeDef("Dalekohled", 95, "Větší dosah.") { Range = 60 },
                        new UpgradeDef("Noční vidění", 200, "Vidí tiché vysavače.") { Detects = true },
                        new UpgradeDef("Kulometné předení", 1050, "Plive třikrát rychleji.") { IntervalMul = 0.33 },
                        new UpgradeDef("Předoucí kulomet", 1950, "Ještě rychlejší palba a +1 poškození.") { IntervalMul = 0.6, Damage = 1 },
                        new UpgradeDef("Chlupomet", 5250, "Palba dvakrát rychleji, +2 poškození, prorazí i pancíř.") { IntervalMul = 0.5, Damage = 2, Armor = true, ShotSpeedMul = 1.5f },
                    },
                    new[]
                    {
                        new UpgradeDef("Tvrdé kuličky", 145, "+1 poškození.") { Damage = 1 },
                        new UpgradeDef("Bystré oko", 280, "Větší dosah a rychlejší kuličky.") { Range = 40, ShotSpeedMul = 1.4f },
                        new UpgradeDef("Ostrostřelkyně", 980, "Trefí vysavač kdekoli v obýváku, hned a za +4.") { Damage = 4, IntervalMul = 0.6 },
                        new UpgradeDef("Průrazná střela", 1800, "+6 poškození, prorazí pancíř, obřím dvojnásob.") { Damage = 6, Armor = true, BossMul = 2, IntervalMul = 0.8 },
                        new UpgradeDef("Kočičí odstřelovač", 4900, "+20 poškození, každá rána omráčí, obřím trojnásob.") { Damage = 20, BossMul = 3, IntervalMul = 0.7 },
                    },
                },
            },
            new TowerDef
            {
                Kind = TowerKind.Klubickarka, Name = "Klubíčkářka", Price = 420, Range = 150, Interval = 1.3, Damage = 1, Pierce = 18, Dmg = DmgType.Blast,
                Splash = 50, ShotSpeed = 380, Fur = 0x212529, Fur2 = 0xFFFFFF,
                Desc = "Hází klubíčka, která bouchnou a zasáhnou celou skupinu. Platí i na pancéřáky.",
                PathNames = new[] { "Výbuch", "Hod", "Pletení" },
                Paths = new[]
                {
                    new[]
                    {
                        new UpgradeDef("Větší klubko", 160, "Větší výbuch.") { Splash = 25 },
                        new UpgradeDef("Zamotání", 335, "Výbuch na chvíli zastaví vysavače (obří jen zpomalí)."),
                        new UpgradeDef("Klubko zkázy", 1200, "+3 poškození, obřím vysavačům trojnásob.") { Damage = 3, BossMul = 3 },
                        new UpgradeDef("Vlněná bomba", 2100, "Mohutný výbuch: +4 poškození, obřím čtyřnásob.") { Damage = 4, Splash = 25, Pierce = 20, BossMul = 4 },
                        new UpgradeDef("Klubíčková apokalypsa", 5600, "+12 poškození, delší zastavení, obřím pětinásob.") { Damage = 12, Splash = 30, Pierce = 30, BossMul = 5 },
                    },
                    new[]
                    {
                        new UpgradeDef("Rychlé pacičky", 210, "Hází o 40 % rychleji.") { IntervalMul = 0.71 },
                        new UpgradeDef("Tříštivé klubko", 420, "Klubko se po výbuchu rozletí na čtyři menší."),
                        new UpgradeDef("Dlouhý hod", 600, "Mnohem větší dosah a rychlejší let.") { Range = 80, ShotSpeedMul = 1.6f },
                        new UpgradeDef("Kanonáda", 1800, "Hází rychleji a rozletí se na osm silnějších klubíček.") { IntervalMul = 0.6 },
                        new UpgradeDef("Klubkomet", 5250, "Hází tři klubka naráz na různé vysavače.") { IntervalMul = 0.6 },
                    },
                    new[]
                    {
                        new UpgradeDef("Vlněná stopa", 145, "Výbuch zpomalí zasažené vysavače."),
                        new UpgradeDef("Lepivá příze", 305, "Zpomalí víc a hází dál.") { Range = 20 },
                        new UpgradeDef("Pavučina", 980, "Výbuch nechá na koberci lepkavou pavučinu, která brzdí.") { Damage = 1 },
                        new UpgradeDef("Svetr pro vysavač", 1950, "Pavučiny vydrží dvakrát déle a poškozují."),
                        new UpgradeDef("Pletená past", 5250, "Pavučiny zmrazí každý vysavač, který do nich vjede, a víc poškozují."),
                    },
                },
            },
            new TowerDef
            {
                Kind = TowerKind.Laserovka, Name = "Laserovka", Price = 640, Range = 190, Interval = 0.14, Damage = 1, Pierce = 1, Dmg = DmgType.Energy,
                Fur = 0xE9ECEF, Fur2 = 0x495057,
                Desc = "Míří laserovým ukazovátkem a paprsek vysavače rozpéká.",
                PathNames = new[] { "Paprsek", "Optika", "Žár" },
                Paths = new[]
                {
                    new[]
                    {
                        new UpgradeDef("Silnější laser", 280, "+1 poškození.") { Damage = 1 },
                        new UpgradeDef("Rozdvojený paprsek", 520, "Míří na dva vysavače naráz."),
                        new UpgradeDef("Fotonové dělo", 1800, "+4 poškození, obřím vysavačům dvojnásob.") { Damage = 4, BossMul = 2 },
                        new UpgradeDef("Plazmový proud", 2650, "+5 poškození a tři paprsky naráz.") { Damage = 5 },
                        new UpgradeDef("Sluneční paprsek", 6300, "Propálí všechno v řadě, +12 poškození, obřím trojnásob.") { Damage = 12, BossMul = 3 },
                    },
                    new[]
                    {
                        new UpgradeDef("Rychlé blikání", 240, "Paprsek pálí rychleji.") { IntervalMul = 0.65 },
                        new UpgradeDef("Termovize", 240, "Vidí tiché vysavače.") { Detects = true },
                        new UpgradeDef("Disko koule", 1950, "Zasáhne všechny vysavače v dosahu najednou."),
                        new UpgradeDef("Zrcadlový sál", 2650, "Větší dosah a rychlejší pálení.") { Range = 50, IntervalMul = 0.8 },
                        new UpgradeDef("Laserová show", 6300, "Ještě větší dosah a +3 poškození.") { Range = 50, Damage = 3 },
                    },
                    new[]
                    {
                        new UpgradeDef("Teplý paprsek", 200, "Dosáhne dál.") { Range = 25 },
                        new UpgradeDef("Pálení", 400, "Zasažené vysavače chvíli hoří.") { Damage = 1 },
                        new UpgradeDef("Přetížení", 1200, "Každých 5 s vypálí výbuch žáru do skupiny vysavačů.") { IntervalMul = 0.8 },
                        new UpgradeDef("Laserová pec", 2250, "Silnější hoření a výbuch žáru každé 3 s."),
                        new UpgradeDef("Supernova", 5950, "Obří výbuch žáru každé 2 s, obřím trojnásob."),
                    },
                },
            },
            new TowerDef
            {
                Kind = TowerKind.Spac, Name = "Předoucí kocour", Price = 320, Range = 105, Interval = 0.5, Damage = 0, Pierce = 0, Dmg = DmgType.None,
                Fur = 0xD9480F, Fur2 = 0xFFF4E6,
                Desc = "Spí u trati a jeho předení vysavače zpomaluje.",
                PathNames = new[] { "Spánek", "Pelíšek", "Pohoda" },
                Paths = new[]
                {
                    new[]
                    {
                        new UpgradeDef("Hlubší předení", 175, "Zpomalí víc."),
                        new UpgradeDef("Ledová deka", 480, "Každé 3 s na chvíli zmrazí vysavače v dosahu."),
                        new UpgradeDef("Ukolébavka", 900, "Zpomalené vysavače dostanou od všech koček +1 poškození."),
                        new UpgradeDef("Zimní spánek", 1800, "Zpomalí ještě víc, mrazí častěji a déle."),
                        new UpgradeDef("Věčná zima", 5250, "Mrazí každou chvíli i obří vysavače, od všech koček +3 poškození."),
                    },
                    new[]
                    {
                        new UpgradeDef("Větší pelíšek", 145, "Větší dosah.") { Range = 40 },
                        new UpgradeDef("Odhalení", 280, "Tiché vysavače v dosahu vidí všechny kočky."),
                        new UpgradeDef("Chrápání", 820, "Chrápe tak, že poškozuje všechny vysavače v dosahu.") { Damage = 1 },
                        new UpgradeDef("Hromové chrápání", 1800, "Silnější a častější chrápání, větší dosah.") { Damage = 2, IntervalMul = 0.8, Range = 20 },
                        new UpgradeDef("Zemětřesení", 4900, "Chrápání otřásá domem: velké poškození, obřím dvojnásob.") { Damage = 7, Range = 30, BossMul = 2 },
                    },
                    new[]
                    {
                        new UpgradeDef("Hřejivé předení", 200, "Kočky v dosahu útočí o 10 % rychleji."),
                        new UpgradeDef("Kočičí masáž", 400, "Kočky v dosahu zasáhnou o 1 vysavač víc.") { Range = 15 },
                        new UpgradeDef("Mňaukavý koncert", 1050, "Kočky v dosahu mají +1 poškození.") { Range = 15 },
                        new UpgradeDef("Kočičí terapie", 1950, "Kočky v dosahu útočí o 25 % rychleji a vidí tiché vysavače.") { Range = 20 },
                        new UpgradeDef("Předoucí guru", 5600, "Kočky v dosahu: o 40 % rychlejší, +2 poškození, +2 průraz, větší dosah.") { Range = 30 },
                    },
                },
            },
            new TowerDef
            {
                Kind = TowerKind.Pamlskarna, Name = "Pamlskárna", Price = 800, Range = 0, Interval = 0, Damage = 0, Pierce = 0, Dmg = DmgType.None,
                Fur = 0xFAB005, Fur2 = 0xFFF9DB,
                Desc = "Na konci každého kola vydá pamlsky navíc.",
                PathNames = new[] { "Zásoby", "Péče", "Rozvoz" },
                Paths = new[]
                {
                    new[]
                    {
                        new UpgradeDef("Větší miska", 360, "Za kolo 110 pamlsků místo 70."),
                        new UpgradeDef("Spíž", 810, "Za kolo 200 pamlsků."),
                        new UpgradeDef("Konzervárna", 1450, "Za kolo 450 pamlsků."),
                        new UpgradeDef("Rybí trh", 2900, "Za kolo 900 pamlsků."),
                        new UpgradeDef("Pamlskové impérium", 6800, "Za kolo 1800 pamlsků."),
                    },
                    new[]
                    {
                        new UpgradeDef("Kočičí pojišťovna", 270, "+2 životy za kolo."),
                        new UpgradeDef("Úroky", 720, "Za kolo +5 % ušetřených pamlsků (nejvýš 200)."),
                        new UpgradeDef("Zlatá miska", 1600, "Všechny odměny za vysavače +25 %."),
                        new UpgradeDef("Kočičí banka", 2700, "Úroky 10 % (nejvýš 500) a +4 životy za kolo."),
                        new UpgradeDef("Kočičí burza", 6800, "Odměny za vysavače +50 % a úroky až 1200."),
                    },
                    new[]
                    {
                        new UpgradeDef("Rychlá zásilka", 225, "Během kola přinese každých 8 s 10 pamlsků."),
                        new UpgradeDef("Pytlík granulí", 500, "Během kola každých 8 s 22 pamlsků."),
                        new UpgradeDef("Kočičí kurýr", 1300, "Během kola každých 6 s 50 pamlsků."),
                        new UpgradeDef("Rozvozová služba", 2550, "Během kola každých 5 s 100 pamlsků."),
                        new UpgradeDef("Továrna na pamlsky", 6800, "Během kola každé 4 s 240 pamlsků."),
                    },
                },
            },
        };

        static readonly VacDef[] vacs =
        {
            new VacDef { Kind = VacKind.Robik, Name = "Robík", Speed = 55, Hp = 1, Children = new VacKind[0], Radius = 15, Color = 0xB8C2CC },
            new VacDef { Kind = VacKind.Plus, Name = "Robík Plus", Speed = 70, Hp = 1, Children = new[] { VacKind.Robik }, Radius = 16, Color = 0x4DABF7 },
            new VacDef { Kind = VacKind.Turbo, Name = "Turbo", Speed = 90, Hp = 1, Children = new[] { VacKind.Plus }, Radius = 16, Color = 0x51CF66 },
            new VacDef { Kind = VacKind.Sprint, Name = "Sprint", Speed = 125, Hp = 1, Children = new[] { VacKind.Turbo }, Radius = 17, Color = 0xFCC419 },
            new VacDef { Kind = VacKind.Raketa, Name = "Raketa", Speed = 160, Hp = 1, Children = new[] { VacKind.Sprint }, Radius = 17, Color = 0xF06595 },
            new VacDef { Kind = VacKind.Dvojce, Name = "Dvojče", Speed = 95, Hp = 1, Children = new[] { VacKind.Raketa, VacKind.Raketa }, Radius = 18, Color = 0x343A40 },
            new VacDef { Kind = VacKind.Pancerak, Name = "Pancéřák", Speed = 50, Hp = 1, Children = new[] { VacKind.Dvojce, VacKind.Dvojce }, Radius = 19, Armored = true, Color = 0x868E96 },
            new VacDef { Kind = VacKind.Disko, Name = "Disko", Speed = 140, Hp = 1, Children = new[] { VacKind.Dvojce, VacKind.Dvojce }, Radius = 19, Color = 0x9C36B5 },
            new VacDef { Kind = VacKind.Keramik, Name = "Keramik", Speed = 160, Hp = 22, Children = new[] { VacKind.Disko, VacKind.Disko }, Radius = 20, Color = 0xC26A3D },
            new VacDef { Kind = VacKind.Prumyslak, Name = "Průmyslák", Speed = 32, Hp = 300, Children = new[] { VacKind.Keramik, VacKind.Keramik, VacKind.Keramik, VacKind.Keramik }, Radius = 38, Boss = true, Color = 0x1971C2 },
            new VacDef { Kind = VacKind.Obr, Name = "Obr", Speed = 22, Hp = 2500, Children = new[] { VacKind.Prumyslak, VacKind.Prumyslak, VacKind.Prumyslak, VacKind.Prumyslak }, Radius = 52, Boss = true, Color = 0xC92A2A },
        };

        public static TowerDef Tower(TowerKind k) { return towers[(int)k]; }
        public static VacDef Vac(VacKind k) { return vacs[(int)k]; }
        public static int TowerCount { get { return towers.Length; } }
    }
}
