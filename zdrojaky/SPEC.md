# Kočky vs. vysavače — shared contract

A tower-defense game for Windows. The player places **cats** (towers) on a living-room floor; **robot vacuums** drive along a carpet runner from a charging dock (left edge) to the cats' bed (top right). Mechanics in the spirit of classic "pop the layers" tower defense: a hit pops one layer of a vacuum and it turns into its weaker children; cats have **three upgrade paths with five tiers** each (at most two paths per cat, only one of them above tier 2 — `Defs.CanRaise`), every cat has its own attack range shown as a circle; special vacuums (stealth, armored, bosses). **Own look, names and music — do not copy any existing game** (no monkeys, balloons, their names or art).

All user-facing text is **Czech with diacritics**. The player is addressed informally ("ty"), neutral wording.

## Technology (hard rules)

- C# **5** compiled by the Windows built-in compiler: `$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (.NET Framework 4.8). Not available: string interpolation `$"..."`, `?.`, `nameof`, expression-bodied members `=>` on methods/properties, tuples, `out var`, pattern matching, `using static`, default literal. Lambdas, LINQ, `var`, object initializers are fine.
- WinForms + GDI+ (`System.Drawing`) only. No NuGet, no downloads, no installs.
- Namespace `Kocky`. Sources are UTF-8 (no BOM is fine).
- Virtual canvas **1280 × 800**; the playing field is `Defs.Map` = (0, 0, 960, 800); the side panel is x 960..1280. Angles in **radians**, 0 = facing +x, y grows downwards.
- Shared helpers: `Kit.cs` (palette `Kit.Ink/Fg/Sub/Amber/Cream/Bg/Panel...`, `Kit.C(0xRRGGBB)`, `Kit.A(color, alpha)`, `Kit.Mix`, cached `Kit.B(color)` brushes, `Kit.P(color, width)` pens (dispose them), fonts `Kit.Bold/Reg/Light/Cond/Ital(px)`, `Kit.Text/TextBox/Halo`, `Kit.Round/Round4/FillRound/StrokeRound/Circle/Ring/Shadow`). Do not edit Kit.cs; add private helpers in your own file.
- Static data: `Defs.cs` (map, path, obstacles, cat and vacuum definitions, difficulty). Only the Core builder may change **numbers** in Defs.cs for balance (prices, stats, upgrade stat fields) and, when such a number also appears in a `Desc` text, the number in that text so it stays true; nobody renames or removes anything there. `Defs.CanRaise(up, path)` is the one crosspath rule everybody uses.
- Never kill processes by name (a guard blocks `Stop-Process -Name`, `taskkill /IM`); stop only a process you started, by PID (`taskkill /PID <pid> /T /F`).
- Build of the whole game: `powershell -File kocky\build.ps1` (needs every file below).

## Files and owners

| File | Owner | Content |
|---|---|---|
| Kit.cs, Defs.cs, build.ps1, SPEC.md | supervisor | shared helpers, data, build, this contract |
| Core.cs | core builder | simulation (`Game`, `Vac`, `Cat`, `Shot`, `Fx`), rounds |
| Bot.cs | core builder | `Bot` (automatic player) and `Sim` (headless balance runs) |
| Art.cs | art builder | every drawing of the map, cats, vacuums, shots, effects, icons |
| Audio.cs | audio builder | synthesized sound effects + music, one waveOut mixer |
| Ui.cs, Program.cs | UI builder | window, screens, HUD, input, settings, test switches |

## Core API (Core.cs, Bot.cs) — exact

```csharp
public class Vac
{
    public int Id;
    public VacKind Kind;
    public bool Stealth;            // "tichý": only detecting cats can target it (blasts hit it anyway)
    public double Dist;             // distance travelled along Defs.Path in px
    public float X, Y, Angle;       // position and heading, updated every step
    public int Hp;                  // hit points left in the current layer
    public float HpFrac;            // Hp / Defs.Vac(Kind).Hp (art damage states)
    public float Slow;              // 0..1 current slow (0 = full speed), for the art tint
    public bool Frozen;             // currently frozen or stunned
    public bool Revealed;           // stealth uncovered by a purring cat (Spac B2) this step
    public int Tough;               // 0..4 free-play reinforcement for the art, from the layer Hp multiplier it was made with
                                    // (Keramik: Keramik ×, Průmyslák/Obr: boss ×, others 0): 0 below ×2, 1 from ×2, 2 from ×4, 3 from ×8, 4 from ×16
}

public class Cat
{
    public int Id;
    public TowerKind Kind;
    public float X, Y, Angle;       // Angle = facing (towards the last target)
    public int[] Up = new int[3];   // upgrade tier 0..5 of each path (Defs.PathCount paths)
    public Targeting Target;        // default First
    public int Spent;               // treats spent on it (price + upgrades)
    public int Pops;                // layers it popped
    public double LastAttack;       // Game.Time of its last attack (art: attack animation); -999 before the first
    // current stats with upgrades and support buffs (the UI shows them):
    public float Range;             // >= Defs.MapWide: reaches the whole floor
    public bool Detects;            // can target stealth vacuums
    public double Interval;         // seconds between attacks (Spac: snore tick)
    public int Damage, Pierce;
    public float Splash;
    public bool Buffed;             // inside a support cat's aura (Spac path 2)
}

public class Shot { public ProjKind Kind; public float X, Y, Angle; }   // plus any private fields Core needs

public class Fx
{
    public FxKind Kind;
    public float X, Y, X2, Y2, R, Angle;   // X2/Y2: beam end; R: radius (blast, freeze, spin)
    public double Born, Life;              // age for the art = (Game.Time - Born) / Life, 0..1
    public int Color;                      // 0xRRGGBB (pop: colour of the popped layer)
    public int Tier;                       // strength for the art (beam width, blast size...)
}

public class Game
{
    public Game(Difficulty diff, int seed);
    public readonly Difficulty Diff;
    public int Money, Lives, Round;        // Round = number of the last started round, 0 before the first
    public bool RoundActive, Lost, Won;    // Won becomes true when round Defs.FinalRound ends with lives left
    public int RoundsSurvived { get; }     // rounds finished: neither the round in progress nor the lost one counts
    public bool AutoStart;                 // start the next round by itself when one ends
    public double Time;                    // simulated seconds
    public readonly List<Vac> Vacs;
    public readonly List<Cat> Cats;
    public readonly List<Shot> Shots;
    public readonly List<Fx> Effects;
    public int Leaked, PoppedTotal;        // statistics
    public Action<string> Sound;           // optional; called with a sound name (list below)

    public int PriceOf(TowerKind k);                 // difficulty multiplier applied, rounded to 5
    public int UpgradePrice(Cat c, int path);        // -1 when UpgradeBlock(c, path) != UpBlock.None
    public UpBlock UpgradeBlock(Cat c, int path);    // None | Maxed (path already tier 5) | Crosspath (!Defs.CanRaise) | LegendTaken (see "Legend rule")
    public Cat LegendOwner(TowerKind k, int path, Cat except);  // the cat of kind k (not `except`) holding tier 5 on that path, or null
    public Cat StatsFor(TowerKind k, int[] up);      // detached cat with these tiers and base stats (no support buff, not in Cats), for the upgrade tree
    public bool CanPlace(TowerKind k, float x, float y);   // inside Defs.Map, off the runner, off obstacles, not overlapping a cat
    public Cat Place(TowerKind k, float x, float y);       // null when not allowed or not affordable; pays
    public bool Upgrade(Cat c, int path);                  // pays; false when not possible
    public Cat Preview(Cat c, int path);                   // detached copy with that path one tier higher and all stats recalculated
                                                           // (current support buff included); null when not allowed. Not added to Cats.
    public string Info(Cat c);                             // special numbers in Czech, at most 2 lines ('\n') of <= 38 chars, "" when none:
                                                           // e.g. "Za kolo 450 pamlsků\nRozvoz 50 každých 6 s", "Zpomalí o 50 %\nMráz každé 3 s na 1 s",
                                                           // "Kousne každý 2. útok za 10", "Hoří 3 za 0,5 s · žár každé 3 s"
    public int SellValue(Cat c);                           // 70 % of Spent
    public void Sell(Cat c);
    public Cat CatAt(float x, float y);                    // cat whose footprint contains the point, or null
    public bool StartRound();                              // false while a round runs or after Lost
    public void Step(double dt);                           // the UI calls it with 1/60 s (several times per frame at 2×/3×)
    public string NextRoundPreview();                      // short Czech line, e.g. "Kolo 12: 30× Turbo, 8× Sprint (tiché)"
    public static int LeakCost(VacKind k);                 // lives lost when it reaches the bed = 1 + sum of its children's costs
}

public static class Bot { public static void Act(Game g, Random rnd); }  // between rounds (and during them): places and upgrades cats like a sensible player
public static class Sim { public static void Run(int games, string file); } // headless games by Bot on every difficulty; writes a Czech/ASCII report
```

Sound names passed to `Game.Sound` and `Audio.Play`: `pop, clank, swipe, spit, throw, blast, laser, freeze, zzz, place, upgrade, sell, round, coins, leak, boss, win, lose, meow, click, bite, snipe, web, legend`.

Fx fields per kind (Core fills them, Art draws them; Life is seconds):
- Pop: X,Y at the vacuum, R = its radius, Angle random, Color = layer colour, Tier 2 for bosses. Life 0.35.
- Swipe: X,Y the cat, X2,Y2 the target, R = cat range, Angle, Color = Fur, Tier = Up[0]. Spin: X,Y the cat, R = range, Tier = Up[1] (5 = storm with knockback).
- Blast: X,Y centre, R radius, Color (0 = yarn colours, 0xFF6B00 = laser overheat), Tier = Up[0] (yarn) or Up[2] (overheat).
- Beam: X,Y → X2,Y2, Color 0xFF3B3B, Tier = Up[0] (5 = wide sun beam). Trace (sniper streak): X,Y → X2,Y2, Tier = Up[2], Life ~0.12.
- Freeze: X,Y the cat, R = range, Tier = Up[0]. Zzz: X,Y the cat, R = range (0 = small idle Zzz), Tier = Up[1].
- Coins: X,Y the stash, Tier = Up[0] (round end) or Up[2] (delivery during a round, small), Life 1.0 / 0.8.
- Leak: X,Y the bed, R = vacuum radius, Color = its colour.
- Bite: X,Y the target, R = its radius, Tier = Up[2], Life 0.25.
- Web: X,Y on the runner centre line, R radius, Angle = runner heading there, Life = how long the web lasts (4 or 8 s), Tier = Up[2] (4 = thicker, 5 = frosty). **Drawn under the vacuums.**
- Burn: X,Y the vacuum, R = its radius, Tier = Up[2], Life 0.5.
- Upgrade: X,Y the cat, R = Defs.CatRadius, Color = Fur, Tier = the new tier 1..5 (5 = big golden burst), Life 0.7.

### Rules the simulation implements

- Movement: `Dist += speed * (1 - Slow) * dt`, zero while Frozen. Position and heading from the `Defs.Path` polyline. Reaching the end: `Lives -= LeakCost(kind)` (sound `leak`, Fx Leak at the bed), vacuum removed; `Lives <= 0` → `Lost` (sound `lose`).
- Hit: damage reduces the current layer; `Hp <= 0` pops it: +1 treat per layer (Zlatá miska +25 %, keep the fraction), `PoppedTotal++`, the cat's `Pops++`, Fx Pop in the layer colour, children appear at the same spot spread slightly back along the path, inherit `Stealth`; leftover damage carries into the first child. Bosses' children spread out more.
- Armored (Pancéřák): Sharp damage does nothing (the hit still uses up pierce, sound `clank`). Blast and Energy work. Drápka's Tygří spár pierces armor.
- Stealth: a cat may target it only if `Detects` or the vacuum is `Revealed`. Blasts and aura damage hit stealth vacuums in their area anyway.
- Targeting: First = furthest along the path, Last = least far, Strong = highest LeakCost (ties: furthest), Close = nearest to the cat.
- **Upgrades — 3 paths × 5 tiers.** `Cat.Up[p]` is 0..5 for p = 0, 1, 2 (`TowerDef.Paths[p][tier-1]`, names in `PathNames`). A path may rise only when `Defs.CanRaise(c.Up, p)` (at most two paths upgraded, only one above tier 2: 5-2-0, 2-0-5, 0-3-2...). Price = upgrade Price × difficulty multiplier, rounded to 5.
- **Stats.** Start from the TowerDef base; for every path and every tier bought apply the UpgradeDef stat fields: Range +=, Interval ×= IntervalMul, Damage +=, Pierce +=, Splash +=, ShotSpeed ×= ShotSpeedMul, Detects |=, Armor |=, BossMul = highest bought (base 1). Then the specials below. Then the support buff (Spac path 2). Recompute stats and buffs when a cat is placed, sold or upgraded (not every step).
- **BossMul** multiplies every damage the cat deals to a Boss vacuum, unless a special below gives its own multiplier. **Armor**: this cat's Sharp damage also pops Armored vacuums.
- **Support buff (Spac path "Pohoda")**: an attacking cat (Drápka, Chlupáč, Klubíčkářka, Laserovka) whose centre is within a Spac's Range gets that Spac's buff; buffs never stack — it takes the one Spac with the highest Pohoda tier (ties: nearest). Tier 1: interval ×0.9. T2: ×0.9, pierce +1. T3: ×0.9, pierce +1, damage +1. T4: ×0.8, pierce +1, damage +1, Detects. T5: ×0.7, pierce +2, damage +2, Detects, range ×1.15. `Cat.Buffed` = true while buffed.
- **Lulled**: vacuums inside a Spac with Spánek ≥ 3 take +1 from every hit (+3 when that Spac has Spánek 5; the strongest lulling counts).
- Every upgrade: Fx Upgrade + sound `upgrade`; buying a tier 5 plays `legend` instead.
- Cats (base values and stat fields in Defs; the specials here):
  - **Drápka** — instant swipe: the main target + nearest other visible vacuums in range, up to Pierce; Fx Swipe; sound `swipe`.
    - Drápy (path 0): T2 two swipes per attack (the second picks its target again), T5 three swipes.
    - Rychlost (1): T3+ every swipe hits every visible vacuum in range (Fx Spin instead of Swipe). T5 the spin knocks non-boss vacuums back 35 px along the runner (Dist −35, not below 0), each vacuum at most once per 1.5 s per cat.
    - Zuby (2): an extra bite on the main target, Sharp that pierces armor, Fx Bite + sound `bite`: T2 every 3rd attack for 3; T3 every 2nd attack for 10, ×2 vs bosses; T4 every attack for 20 and stuns a non-boss 0.4 s; T5 every attack for 60, ×4 vs bosses, stun 0.4 s.
  - **Chlupáč** — hairball projectile (ShotSpeed), straight line aimed with lead, each vacuum hit once per ball, gone after Pierce hits or 1.3 × Range of travel; sound `spit`.
    - Kuličky (0): T2 hit vacuums slowed 30 % for 1.5 s (bosses 15 %), also by sniper hits; T3 three balls in a ±12° fan; T4 five balls 10° apart; T5 five balls and every 3rd attack also 8 balls evenly all around.
    - Mířidla (1): stat fields only.
    - Ostřelování (2): T3+ sniper — Range = `Defs.MapWide` (the whole floor; targeting rules still apply), no projectile: the target is hit at once (one vacuum, Pierce not used), Fx Trace, sound `snipe`. T5 every hit stuns a non-boss 0.5 s and slows a boss 50 % for 1 s.
  - **Klubíčkářka** — yarn ball flies to the target and explodes: Splash radius, up to Pierce vacuums, Blast type (hits stealth too), Fx Blast; sounds `throw`, `blast`.
    - Výbuch (0): T2 stuns non-boss vacuums 0.6 s (bosses slowed 30 % for 1 s); T5 stun 1.2 s.
    - Hod (1): T2 four mini yarns fly out of each explosion (radius 30, damage 1, pierce 6); T4 eight mini yarns with damage 2; T5 every attack throws a yarn at up to three different targets.
    - Pletení (2): T1 the explosion slows hit vacuums 25 % for 2 s (bosses half), T2 40 %. T3 it also leaves a web on the runner: centre = nearest point of the runner centre line to the blast, radius 0.8 × Splash, lasts 4 s, Fx Web (sound `web`); a vacuum whose centre is inside a web is slowed 50 % (bosses 25 %); at most 10 webs per cat (oldest go first). T4 webs last 8 s and deal 1 Blast damage every 0.5 s to every vacuum inside. T5 a web freezes a non-boss vacuum for 1 s the first time it enters that web, deals 3 every 0.5 s, bosses slowed 50 %.
  - **Laserovka** — beam: every Interval it damages its target (Energy), Fx Beam (life ~0.13 s); sound `laser`.
    - Paprsek (0): T2 two targets; T4 three targets; T5 each beam goes from the cat through its target to the edge of range and hits every vacuum within 14 px of that line.
    - Optika (1): T3+ damages every vacuum in range each tick (draw at most 8 beams).
    - Žár (2): T2 hit vacuums burn for 2 s: 1 Energy damage every 0.5 s (a new hit refreshes, no stacking), Fx Burn per tick; T4 burn 3, T5 burn 6. T3 overheat: while it has a target, every 5 s a heat blast at the target: 10 Energy damage to every vacuum within 60 px (stealth too), Fx Blast Color 0xFF6B00, sound `blast`; T4 every 3 s, 25 damage, radius 70; T5 every 2 s, 70 damage, radius 100, ×3 vs bosses.
  - **Předoucí kocour (Spac)** — no direct attack; aura: vacuums in Range get Slow 0.35, bosses half.
    - Spánek (0): T1 slow 0.5; T2 every 3 s freezes non-boss vacuums in range for 1 s (Fx Freeze, sound `freeze`), bosses slowed 0.5 for 1 s; T3 lulled (+1); T4 slow 0.6, freeze every 2 s for 1.5 s; T5 freeze every 1.5 s and bosses freeze for 0.4 s too, lulled +3.
    - Pelíšek (1): T2 stealth vacuums in range become Revealed; T3+ snore: every Interval it deals Damage (Energy, BossMul applies) to every vacuum in range — Damage, Interval, Range come from the stat fields (1 / 0.5 s at T3, 3 / 0.4 s at T4, 10 / 0.4 s at T5); Fx Zzz, sound `zzz`. Draw a small Fx Zzz now and then even without snoring.
    - Pohoda (2): the support buff above (Range +30 at T5 from the stat field).
  - **Pamlskárna** — no attack, no range.
    - Zásoby (0): at the end of each round 70 treats (by Up[0]: 110, 200, 450, 900, 1800), Fx Coins + sound `coins`.
    - Péče (1): T1 +2 lives per round; T2 +5 % of the treats held at round end (max 200); T3 golden bowl: every pop reward +25 %; T4 interest 10 % (max 500) and +4 lives instead of +2; T5 pop rewards +50 %, interest max 1200. The pop-reward bonus does not stack (the best bowl counts); lives and interest are per stash.
    - Rozvoz (2): while a round runs, every N s it gives X treats: T1 8 s / 10, T2 8 s / 22, T3 6 s / 50, T4 5 s / 100, T5 4 s / 240; small Fx Coins at the stash, sound `coins`.
- `Info(c)` states the numbers a player cannot see in the stats (bite, burn, overheat, slow, freeze, buff given, incomes). Keep its numbers equal to the code.
- Round end (all spawned, none alive): `RoundActive = false`, `Money += 100 + Round`, stash incomes, sound `round`. Finishing round `Defs.FinalRound` with lives → `Won = true` (sound `win`); the player may continue (free play: rounds after 40 are generated and keep getting harder).
- Rounds 1–40 are a hand-made table of groups (kind, count, spacing, delay, stealth). New things arrive gradually: Plus ~3, Turbo ~6, Sprint ~10, first stealth ~12, Raketa ~14, Dvojče ~18, Pancéřák ~21, Disko ~24, Keramik ~28, first Průmyslák (boss) 30, two Průmysláci 35, Obr (final boss) 40. Sound `boss` when a boss spawns.
- Balance target (Bot playing): Easy wins almost always, Medium wins most games (70–90 %), Hard wins roughly a third to a half. Bot must be a reasonable player, not a cheater (same API as the UI). Upgrades must be worth buying: on Medium the Bot buys its first tier 4 around rounds 22–30 and at least one tier 5 in most games by round 40; every one of the 18 paths should be a sensible choice (no path that is never worth it, no single path that wins alone).
- Determinism: all randomness from `new Random(seed)` inside Game.

## Art API (Art.cs) — exact

```csharp
public static class Art
{
    public static void Map(Graphics g);    // whole 960×800 floor: parquet, carpet runner along Defs.Path (Defs.PathWidth), furniture for Defs.Obstacles, charging dock at the path start, cat bed + food bowl at the end; static, the UI caches it in a bitmap
    public static void Cat(Graphics g, TowerKind k, int[] up, float x, float y, float angle, float attack, double t);  // up: tier 0..5 per path; attack: 1 right after an attack, falling to 0; t: seconds for idle motion
    public static void CatIcon(Graphics g, TowerKind k, RectangleF r);      // shop icon filling r (no upgrades)
    public static void UpgradeIcon(Graphics g, TowerKind k, int path, int tier, RectangleF r);  // picture of one upgrade (tier 1..5) for the upgrade buttons, readable at 44 px
    public static void RangeGhost(Graphics g, float x, float y, float r);   // dashed ring: the range a hovered upgrade would give
    public static void Vacuum(Graphics g, VacKind k, float x, float y, float angle, bool stealth, float slow, bool frozen, float hpFrac, double t);  // = tough 0
    public static void Vacuum(Graphics g, VacKind k, float x, float y, float angle, bool stealth, float slow, bool frozen, float hpFrac, int tough, double t);
        // tough = Vac.Tough: collar on the rim, open at the bumper — 1 riveted steel band, 2 thicker band + inner band + bolts,
        // 3 five heavy dark plates, 4 plates with glowing hot seams; damage cracks stay on top, bands snap and plates fall off as hpFrac drops
    public static void Shot(Graphics g, ProjKind k, float x, float y, float angle, double t);
    public static void Effect(Graphics g, FxKind k, float x, float y, float x2, float y2, float r, float angle, float age, int color, int tier);  // age 0..1
    public static void Range(Graphics g, float x, float y, float r, bool ok);  // placement/selection range circle; red when !ok
    public static void TreatIcon(Graphics g, float x, float y, float size);    // currency "pamlsky" (fish-shaped treat), centred at x,y
    public static void LifeIcon(Graphics g, float x, float y, float size);     // lives (heart with a paw print)
    public static void Hero(Graphics g, RectangleF r, double t);               // title-screen illustration: a brave cat facing a robot vacuum
    public static void AppIcon(Graphics g, float size);                        // square app icon drawn at (0,0,size,size)
}
```

Art direction: top-down camera over a cosy living room; cute flat illustration, dark outline (`Kit.Ink`, ~2 px), soft shadows, warm palette, readable at a glance. Every cat kind has its own silhouette and colours (`Fur`, `Fur2` in Defs); higher upgrade tiers visibly change the cat: each of the 3 paths has its own visual motif and grows with the tier (T1–2 small touches, T3 a clear new feature, T4 bigger and brighter, T5 a dramatic "legend" look — larger cat, glow ring in the path colour, unmistakable silhouette); a cat with two upgraded paths shows both motifs. Upgrade icons: the same path's five icons share its motif and grow richer with the tier (e.g. frame bronze → silver → gold → glowing). Vacuums are round robot vacuums seen from above with a front bumper (heading = angle), sensor dot, spinning side brushes; per-kind colour and features (Dvojče two-tone, Pancéřák riveted steel, Disko cycling lights, Keramik cracks as hpFrac drops, Průmyslák big industrial unit with hose, Obr huge red with warning stripes and damage). Stealth = translucent with a dashed outline; slow = bluish tint; frozen = ice overlay. Performance matters: 40 cats + 300 vacuums + effects must draw well under 16 ms — cache brushes, avoid per-sprite gradients and large allocations.

## Audio API (Audio.cs) — exact

```csharp
public static class Audio
{
    public static volatile bool Muted, MusicOff, Hidden;   // Muted: silence everything; MusicOff: no music; Hidden: window minimized → silence
    public static void Start();            // opens waveOut (22050 Hz, mono, 16 bit) and starts the mixer thread; safe if no audio device
    public static void Stop();
    public static void Play(string name);  // names from the list above; unknown names ignored; thread-safe; throttled (pop >= 35 ms apart, laser >= 90 ms, others >= 50 ms)
    public static void Export(string dir); // writes every sound and the music loop as WAV files for checking
}
```

Sounds added with the 3 × 5 upgrades: `bite` (short crunchy chomp), `snipe` (sharp dry crack with a tiny whistle), `web` (soft stretchy twang), `legend` (bright ~1.2 s fanfare-like jingle in F major for buying a tier 5, clearly bigger than `upgrade`). They follow the existing throttling (`bite`, `snipe` >= 50 ms apart).

One mixer: the music loop plus up to 16 overlapping sound voices, soft limiter. Music: one original cosy "cat café" loop (40–60 s, seamless), swing/lo-fi feel, quieter than the effects. Reuse the synthesis code of the train game in `..\hra\Music.cs` and `..\hra\Program.cs` (class `Sfx`).

## UI and Program (Ui.cs, Program.cs)

- `Program.Main(string[] args)` [STAThread]: `--icon file`, `--shots dir`, `--sim N file` (→ `Sim.Run`), `--uitest N file`, `--audio dir` (→ `Audio.Export`); otherwise `Audio.Start()`, run the main form, `Audio.Stop()`.
- Window "Kočky vs. vysavače", resizable, canvas scaled to fit with letterbox, timer ~15 ms, fixed step 1/60 s with speed 1×/2×/3× and pause.
- Screens: title (Art.Hero, Hrát → choose difficulty Lehká/Střední/Těžká with lives and treats, Jak hrát, Zvuk, Hudba, Konec, best round per difficulty), game (map + right panel), pause overlay, lost and won overlays (won offers free play).
- Right panel: lives, treats, "Kolo N / 40"; shop of six cats (icon, name, price, greyed when unaffordable; on hover: description, base range, attack speed). **Selected cat** (the cat panel replaces the shop while a cat is selected, like a classic upgrade screen; Esc or a "Do obchodu" button returns): icon, name, pops, current stats in one or two compact lines — "Dosah 170" (or "Dosah: celý obývák" when Range >= Defs.MapWide), attacks per second, damage, pierce, "vidí tiché" when Detects, "posílený" when Buffed — plus `Game.Info(c)`; then **three upgrade rows**, one per path: `Art.UpgradeIcon` of the next upgrade with five small tier pips under it; top line its name, price and key; under it **what that tier gives, without hovering** (Michal 2026-11-12: "nevidím, co to dopředu dá za bonus"): its description in up to two lines, then **every stat gain of that tier as a chip** (Michal: "udělej to ještě promakanější"): a small light-green pill with a one-colour stat icon (`Art.StatIcon`, `StatKind` Range / Speed / Damage / Pierce / Blast / Stealth / Armor / Boss) and a short value — "+60" or "celý obývák", "+30 %" or "×3" (from 1/Interval; "2/s" when the cat did not attack before), "+2", "tiché", "pancíř", "obří ×3"; a decrease would be red with "−"; chips on a locked row are grey (a crosspath-locked tier counted without the other paths, like the tree). New special abilities are not chips: the description says them and the tooltip lists them. The pips sit on the chips' line at the foot of the row. All of it comes from one comparison, `Ui.Gains(cat, upgraded copy)`, which also gives the tooltip's and the tree's "old → new" lines. States: buyable (green), too expensive (price red, "chybí N" while a long name still fits, row dimmed), locked by the crosspath rule ("Zamčeno" + small lock, description grey), legend taken ("Obsazená"), finished ("Hotovo", gold pips). Hovering a row shows a tooltip with the upgrade's description and the stat changes it brings (from `Game.Preview`: e.g. "Dosah 170 → 230", "Útoky/s 1,1 → 3,2"), the whole path with each tier's chips (what it adds over the tier before, computed like the tree), and, if the range changes, `Art.RangeGhost` of the new range on the map. Under the rows two pairs of half buttons: "Cíl: …" (Tab) | "Vylepšení" (I), then "Prodat za X" (armed by Delete: "Prodat? Znovu Delete") | "Do obchodu" (Esc); for Spac and Pamlskárna the first line is one full-width "Všechna vylepšení" (I). Labels shrink to fit, never cut. **Buying an upgrade** (click or Z/X/C) floats the tier's name and its chips up from the cat: about 1,5 s, an ease-out rise of 30 px, then a fade, on the floor layer clipped to the map, in real UI time (works while paused and at 3×); a new purchase replaces it, and it steps aside from an open row tooltip. Start button "Pustit vysavače" / speed 1×/2×/3×, auto-start toggle, next-round preview. Everything fits 320 × 800 without overlaps, text never clipped.
- Range: every cat has its own range. Placement: ghost cat + Art.Range under the mouse, left click places (Shift keeps placing), right click / Esc cancels. Hovering a placed cat shows its range faintly; a selected cat shows it fully; a whole-floor sniper shows no circle but a thin frame around the floor. Click a cat to select, empty floor to deselect. No boss health bar (Michal, 2026-11-12: "nechci, aby byl nahoře HP bar"): a boss shows its damage on its own body (Art damage stages from HpFrac).
- Draw order on the map: floor, Fx Web, range circles, cats, vacuums, shots, all other Fx, HUD.
- Keys: 1–6 shop, Space start round / change speed, Esc cancel / deselect / pause, **Z, X, C** upgrade path 1 / 2 / 3 of the selected cat (also `,` `.` `-`), Tab targeting, Delete sell (press twice), P pause, M sound, N music.
- "Jak hrát" explains the 3 paths × 5 tiers and the crosspath rule in one short paragraph.
- Settings in `%APPDATA%\KockyVsVysavace\nastaveni.txt` (key=value): zvuk, hudba, nejlepsi0..2 (best round per difficulty), vyhry.
- `--shots dir` writes 1280×800 PNGs: 00-title, 01-difficulty, 02-early, 03-place-ok, 04-place-bad, 05-upgrades (selected cat, a row hovered: tooltip + range ghost), 06-late, 07-boss, 08-lost, 09-won, 10-rules, 11-stealth-armored, 12-legends (all six cats with a tier-5 path each, in action with effects: webs, bite, sniper trace, heat blast, spin), 13-locked (panel of a 5-2-0 cat: one row finished, one locked, one partly bought), 14-hover-range (hovered placed cat with its faint range, a whole-floor sniper selected), 19-upgrade-bought (a yarn cat's tier 4 just bought by clicking its row: the name and four chips floating up, the next tier's tooltip open), 20-panels (contact sheet of the selected-cat panel: all six kinds in four states — 0-0-0, 2-1-0 with something too dear, 4-2-0 with a crosspath lock and a taken legend, 1-0-2 with no treats).
- `--uitest N file` runs the real window and plays N games on Medium by simulated mouse clicks (Bot chooses, the driver clicks), time scale 3×, then writes `OK` (or the exception), actions clicked, games finished, rounds played, frames, fps, average and max frame draw time; exit code 0 / 1.
- `--icon file` writes a multi-size .ico (16/32/48/256) from `Art.AppIcon` (a working encoder is in `..\hra\Program.cs`: `Ico`, `Png`, `Dib`).

## Round 3 (2026-11-12): legend rule, harder free play, see the whole upgrade path

Michal played into free play: from about round 70 he stopped upgrading and the cats still held. He also wants to see exactly what an upgrade does and what comes later on a path, like the upgrade screen of classic tower-defense games ("now it is blind").

### Legend rule (implemented by the supervisor in Core.cs; core builder keeps it and tests it)

- Only **one cat of each kind may hold tier 5 on a given path** at a time. A kind has 3 paths, so at most 3 tier-5 cats per kind (one per path). `Game.UpgradeBlock` returns `LegendTaken` for the 4 → 5 step while another cat of that kind already holds that path's tier 5; `UpgradePrice` is then -1 and `Upgrade` refuses. Selling the holder frees the slot. `Preview` ignores this rule (it still shows what the upgrade would do).
- Bot respects it through the same API (no special cases).

### Free play scaling (core builder: Core.cs, Bot.cs; numbers in Defs.cs if needed)

- Rounds 1–40 stay as they are (balance targets above still hold, with the legend rule in force).
- Rounds after 40 must keep getting harder **fast enough that a defense that stops investing breaks**: grow vacuum toughness (layer Hp multiplier, at least for Keramik and bosses; bosses the most), speed (capped), density/counts and boss frequency; free-play pop rewards may fall (classic late-game rule). Pick the curves yourself; keep them simple formulas in one place.
- Show the ramp to the player on the vacuums, not as numbers (Michal: "nedávej tam, kolik má HP, ať to jde vidět na něm"): `NextRoundPreview()` lists only counts and kinds, and `Vac.Tough` 0..4 draws a reinforcement collar on Keramik and bosses (see the Art API), with the damage cracks on top. `Vac.HpFrac` stays Hp / (scaled layer Hp) so the cracks stay right.
- Targets, measured by `Sim` (extend the report; `--sim N file` keeps working, Program.cs is not yours): on Medium a Bot that keeps playing well loses in free play with a median end between rounds 55 and 85 and no game passes round 120; a Bot that **stops spending at round 50** loses by round 62 in ≥ 90 % of games that reach 50, and one that stops at 70 (in games that reach 70) loses by round 80. Report these.

### Upgrade visibility (UI builder: Ui.cs, Program.cs)

- **Row tooltip** (hovering an upgrade row) keeps the current content and adds: (1) a small before → after picture of the cat (`Art.Cat` with the current tiers and with the next tier); (2) the **whole path**: all 5 tiers as a compact list — `Art.UpgradeIcon` (small), name, price; bought ones ticked, the next one highlighted, later ones plainly visible (not hidden), blocked ones marked with the reason (crosspath lock / "už ji má jiná <kočka>" for the legend rule); (3) stat changes always as "old → new" with both numbers.
- **Upgrade tree** (the classic upgrade screen): a panel over the map opened by a button in the cat panel ("Vylepšení", for Spac and Pamlskárna "Všechna vylepšení", key **I**) and from the shop for any cat kind (so a player can plan before buying). Shows the cat kind's 3 × 5 upgrades as tiles (icon, name, price; for a selected cat: bought / next / later / blocked). Clicking or hovering a tile shows on the side: a large picture of the cat with that upgrade (`Art.Cat` with that path at that tier — for a selected cat combined with its other paths when allowed), the full description, the stats at that tier compared with the tier before (`Game.StatsFor`; whole-floor range as "celý obývák"), `Info`-like special numbers where the code has them, and the rules in one line each (two paths, one above 2; one legend per path and kind). Tabs or arrows switch between the six cat kinds. Esc / close button closes it. The game is **paused while it is open** and resumes on close (unless it was paused before). Buying from the tree is optional; if you add it, use `Game.Upgrade` and the same states.
- `UpBlock.LegendTaken` everywhere a row or tile can be blocked: text "Legenda už je: jen jedna <název> na každou cestu" (or similar, short, true), not the crosspath text.
- New shots: `15-tree` (tree open for a selected cat with mixed states), `16-tree-shop` (tree opened from the shop for a kind not on the map), `17-legend-taken` (a row blocked by the legend rule with its tooltip). Existing shots and `--uitest` keep working; the uitest driver may open and close the tree once per game.
