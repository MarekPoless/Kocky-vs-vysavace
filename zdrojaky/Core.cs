using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Kocky
{
    /// <summary>Why a path cannot be raised: Maxed = already tier 5, Crosspath = the 2-paths / one-above-2 rule,
    /// LegendTaken = another cat of the same kind already has tier 5 on this path (one per path and kind).</summary>
    public enum UpBlock { None, Maxed, Crosspath, LegendTaken }

    public class Vac
    {
        public int Id;
        public VacKind Kind;
        public bool Stealth;            // "tichý": only detecting cats can target it (blasts hit it anyway)
        public double Dist;             // distance travelled along Defs.Path in px
        public float X, Y, Angle;       // position and heading, updated every step
        public int Hp;                  // hit points left in the current layer
        public float HpFrac;            // Hp / MaxHp (the layer Hp with the free-play ramp)
        public float Slow;              // 0..1 current slow (0 = full speed)
        public bool Frozen;             // currently frozen or stunned
        public bool Revealed;           // stealth uncovered by a purring cat (Spac path 1, tier 2) this step
        public int Tough;               // 0..4 free-play reinforcement drawn on it: layer Hp × below 2 / from 2 / 4 / 8 / 16

        internal bool Dead, Boss;
        internal int Lulled, Stamp, BurnDmg, BurnTier, MaxHp;
        internal float Radius, HitSlow, Speed;
        internal double FrozenUntil = -1, SlowUntil = -1, BurnUntil = -1, BurnNext, ThawUntil = -1;
        internal Cat BurnSrc;
    }

    public class Cat
    {
        public int Id;
        public TowerKind Kind;
        public float X, Y, Angle;
        public int[] Up = new int[3];
        public Targeting Target;
        public int Spent;
        public int Pops;
        public double LastAttack = -999;
        public float Range;
        public bool Detects;
        public double Interval;
        public int Damage, Pierce;
        public float Splash;
        public bool Buffed;

        internal double Timer, Timer2, Timer3;
        internal int Count;                 // attacks made (every n-th attack specials)
        internal float ShotSpeed, BossMul = 1;
        internal bool Armor;
        internal Dictionary<int, double> Knocked;   // vacuum id -> time of its last knockback by this cat
    }

    public class Shot
    {
        public ProjKind Kind;
        public float X, Y, Angle;

        internal Cat Owner;
        internal Vac Target;
        internal float Tx, Ty, Speed, Travel, MaxTravel, Splash;
        internal int Pierce, Damage;
        internal bool Gone;
        internal List<int> HitIds;
    }

    public class Fx
    {
        public FxKind Kind;
        public float X, Y, X2, Y2, R, Angle;
        public double Born, Life;
        public int Color;
        public int Tier;
    }

    public class Game
    {
        public readonly Difficulty Diff;
        public int Money, Lives, Round;
        public bool RoundActive, Lost, Won;
        // rounds finished: the round in progress does not count, and neither does the one that was lost (a loss ends the round)
        public int RoundsSurvived { get { return RoundActive || Lost ? Round - 1 : Round; } }
        public bool AutoStart;
        public double Time;
        public readonly List<Vac> Vacs = new List<Vac>();
        public readonly List<Cat> Cats = new List<Cat>();
        public readonly List<Shot> Shots = new List<Shot>();
        public readonly List<Fx> Effects = new List<Fx>();
        public int Leaked, PoppedTotal;
        public Action<string> Sound;

        const int MaxFx = 400, MaxWebs = 10;
        const float KnockBack = 35, LineWidth = 14;
        static readonly int[] stashIncome = { 70, 110, 200, 450, 900, 1800 };
        static readonly double[] deliveryEvery = { 0, 8, 8, 6, 5, 4 };
        static readonly int[] deliveryTreats = { 0, 10, 22, 50, 100, 240 };

        class Web
        {
            public Cat Owner;
            public float X, Y, R;
            public double Until, NextTick;
            public int Tier;
            public Fx Fx;
            public HashSet<int> Entered;    // tier 5: vacuums already frozen by this web
        }

        readonly Random rnd;
        readonly List<Vac> born = new List<Vac>();
        readonly List<Web> webs = new List<Web>();
        readonly List<KeyValuePair<float, Vac>> buf = new List<KeyValuePair<float, Vac>>();
        readonly Predicate<Fx> fxExpired;
        List<Spawn> spawns = new List<Spawn>();
        int spawnIdx, nextId, stamp;
        double roundTime, moneyFrac, popMul = 1;
        double hpMul = 1, bossHpMul = 1, payMul = 1, speedMul = 1;   // free-play ramp of the running round

        public Game(Difficulty diff, int seed)
        {
            Diff = diff;
            rnd = new Random(seed);
            Money = Defs.StartMoney(diff);
            Lives = Defs.StartLives(diff);
            fxExpired = f => Time - f.Born > f.Life;
        }

        // ---------- path ----------

        static readonly float[] cum;
        /// <summary>Length of Defs.Path in px.</summary>
        public static readonly float PathLength;

        static Game()
        {
            var p = Defs.Path;
            cum = new float[p.Length];
            for (int i = 1; i < p.Length; i++)
                cum[i] = cum[i - 1] + (float)Math.Sqrt(Sq(p[i].X - p[i - 1].X) + Sq(p[i].Y - p[i - 1].Y));
            PathLength = cum[p.Length - 1];
            leakCost = new int[Enum.GetValues(typeof(VacKind)).Length];
            for (int k = 0; k < leakCost.Length; k++) leakCost[k] = CalcLeak((VacKind)k);
            table = new List<Group>[Defs.FinalRound + 1];
            for (int r = 1; r <= Defs.FinalRound; r++) table[r] = Parse(Rounds[r - 1]);
        }

        static float Sq(float v) { return v * v; }

        /// <summary>Position and heading at a distance along Defs.Path (clamped to its ends).</summary>
        public static void PathPos(double dist, out float x, out float y, out float angle)
        {
            var p = Defs.Path;
            int i = 1;
            while (i < p.Length - 1 && cum[i] < dist) i++;
            float seg = cum[i] - cum[i - 1];
            float t = seg > 0 ? (float)((dist - cum[i - 1]) / seg) : 0;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            x = p[i - 1].X + (p[i].X - p[i - 1].X) * t;
            y = p[i - 1].Y + (p[i].Y - p[i - 1].Y) * t;
            angle = (float)Math.Atan2(p[i].Y - p[i - 1].Y, p[i].X - p[i - 1].X);
        }

        /// <summary>Distance from a point to the runner's centre line.</summary>
        public static float DistToPath(float x, float y)
        {
            float px, py, a;
            return NearestOnPath(x, y, out px, out py, out a);
        }

        /// <summary>Nearest point of the runner's centre line and the heading there; returns the distance.</summary>
        public static float NearestOnPath(float x, float y, out float px, out float py, out float angle)
        {
            var p = Defs.Path;
            float best = float.MaxValue;
            px = py = angle = 0;
            for (int i = 1; i < p.Length; i++)
            {
                float ax = p[i - 1].X, ay = p[i - 1].Y, dx = p[i].X - ax, dy = p[i].Y - ay;
                float len2 = dx * dx + dy * dy;
                float t = len2 > 0 ? ((x - ax) * dx + (y - ay) * dy) / len2 : 0;
                t = Math.Max(0, Math.Min(1, t));
                float d = Sq(x - ax - dx * t) + Sq(y - ay - dy * t);
                if (d < best) { best = d; px = ax + dx * t; py = ay + dy * t; angle = (float)Math.Atan2(dy, dx); }
            }
            return (float)Math.Sqrt(best);
        }

        // ---------- prices, placement, upgrades ----------

        int Round5(double v) { return (int)(Math.Round(v * Defs.PriceMul(Diff) / 5.0, MidpointRounding.AwayFromZero) * 5); }

        public int PriceOf(TowerKind k) { return Round5(Defs.Tower(k).Price); }

        public int UpgradePrice(Cat c, int path)
        {
            if (c == null || UpgradeBlock(c, path) != UpBlock.None) return -1;
            return Round5(Defs.Tower(c.Kind).Paths[path][c.Up[path]].Price);
        }

        /// <summary>Why this path cannot rise right now; None = it can (money aside).</summary>
        public UpBlock UpgradeBlock(Cat c, int path)
        {
            if (path < 0 || path >= Defs.PathCount) return UpBlock.Crosspath;
            if (c.Up[path] >= Defs.TierCount) return UpBlock.Maxed;
            if (!Defs.CanRaise(c.Up, path)) return UpBlock.Crosspath;
            if (c.Up[path] == Defs.TierCount - 1 && LegendOwner(c.Kind, path, c) != null) return UpBlock.LegendTaken;
            return UpBlock.None;
        }

        /// <summary>The cat of this kind (other than except) that holds tier 5 on this path, or null: only one per path and kind.</summary>
        public Cat LegendOwner(TowerKind k, int path, Cat except)
        {
            foreach (var o in Cats) if (o != except && o.Kind == k && o.Up[path] >= Defs.TierCount) return o;
            return null;
        }

        /// <summary>Detached cat of kind k with these tiers and base stats (no support buff), for upgrade previews.</summary>
        public Cat StatsFor(TowerKind k, int[] up)
        {
            var p = new Cat { Kind = k, Up = (int[])up.Clone(), X = Defs.Map.X + Defs.Map.Width / 2, Y = Defs.Map.Y + Defs.Map.Height / 2 };
            BaseStats(p);
            return p;
        }

        public bool CanPlace(TowerKind k, float x, float y)
        {
            const float r = Defs.CatRadius;
            var m = Defs.Map;
            if (x - r < m.Left || x + r > m.Right || y - r < m.Top || y + r > m.Bottom) return false;
            if (DistToPath(x, y) < Defs.PathWidth / 2 + r - 4) return false;
            foreach (var o in Defs.Obstacles)
            {
                float cx = Math.Max(o.R.Left, Math.Min(o.R.Right, x)), cy = Math.Max(o.R.Top, Math.Min(o.R.Bottom, y));
                if (Sq(cx - x) + Sq(cy - y) < Sq(r - 2)) return false;
            }
            foreach (var c in Cats)
                if (Sq(c.X - x) + Sq(c.Y - y) < Sq(2 * r - 2)) return false;
            return true;
        }

        public Cat Place(TowerKind k, float x, float y)
        {
            int price = PriceOf(k);
            if (!CanPlace(k, x, y) || Money < price) return null;
            var c = new Cat { Id = ++nextId, Kind = k, X = x, Y = y, Angle = (float)(-Math.PI / 2), Spent = price };
            Cats.Add(c);
            Money -= price;
            RecalcAll();
            Play("place");
            return c;
        }

        public bool Upgrade(Cat c, int path)
        {
            int price = UpgradePrice(c, path);
            if (price < 0 || Money < price || !Cats.Contains(c)) return false;
            Money -= price;
            c.Spent += price;
            int tier = ++c.Up[path];
            RecalcAll();
            AddFx(FxKind.Upgrade, c.X, c.Y, 0, 0, Defs.CatRadius, 0, 0.7, Defs.Tower(c.Kind).Fur, tier);
            Play(tier >= Defs.TierCount ? "legend" : "upgrade");
            return true;
        }

        public Cat Preview(Cat c, int path)
        {
            if (c == null || !Defs.CanRaise(c.Up, path)) return null;
            var p = new Cat
            {
                Id = c.Id, Kind = c.Kind, X = c.X, Y = c.Y, Angle = c.Angle, Up = (int[])c.Up.Clone(), Target = c.Target,
                Spent = c.Spent + Math.Max(0, UpgradePrice(c, path)), Pops = c.Pops, LastAttack = c.LastAttack
            };
            p.Up[path]++;
            BaseStats(p);
            ApplyBuff(p);
            return p;
        }

        public int SellValue(Cat c) { return c.Spent * 7 / 10; }

        public void Sell(Cat c)
        {
            if (!Cats.Remove(c)) return;
            Money += SellValue(c);
            RecalcAll();
            Play("sell");
        }

        public Cat CatAt(float x, float y)
        {
            foreach (var c in Cats)
                if (Sq(c.X - x) + Sq(c.Y - y) <= Sq(Defs.CatRadius)) return c;
            return null;
        }

        static bool Attacker(TowerKind k) { return k != TowerKind.Spac && k != TowerKind.Pamlskarna; }

        // stats and support buffs only change when a cat is placed, sold or upgraded
        void RecalcAll()
        {
            foreach (var c in Cats) BaseStats(c);
            foreach (var c in Cats) ApplyBuff(c);
            popMul = 1;
            foreach (var c in Cats)
                if (c.Kind == TowerKind.Pamlskarna && c.Up[1] >= 3) popMul = Math.Max(popMul, c.Up[1] >= 5 ? 1.5 : 1.25);
        }

        static void BaseStats(Cat c)
        {
            var d = Defs.Tower(c.Kind);
            c.Range = d.Range; c.Detects = d.Detects; c.Interval = d.Interval; c.Damage = d.Damage; c.Pierce = d.Pierce;
            c.Splash = d.Splash; c.ShotSpeed = d.ShotSpeed; c.Armor = false; c.BossMul = 1; c.Buffed = false;
            for (int p = 0; p < Defs.PathCount; p++)
                for (int t = 0; t < c.Up[p]; t++)
                {
                    var u = d.Paths[p][t];
                    c.Range += u.Range; c.Interval *= u.IntervalMul; c.Damage += u.Damage; c.Pierce += u.Pierce; c.Splash += u.Splash;
                    c.ShotSpeed *= u.ShotSpeedMul; c.Detects |= u.Detects; c.Armor |= u.Armor;
                    if (u.BossMul > c.BossMul) c.BossMul = u.BossMul;
                }
            if (c.Kind == TowerKind.Chlupac && c.Up[2] >= 3) c.Range = Defs.MapWide;
        }

        /// <summary>Spac path 2 aura: the strongest Spac (ties: nearest) whose range holds the cat's centre.</summary>
        void ApplyBuff(Cat c)
        {
            if (!Attacker(c.Kind)) return;
            Cat best = null;
            float bestD = 0;
            foreach (var s in Cats)
            {
                if (s.Kind != TowerKind.Spac || s.Up[2] == 0) continue;
                float d2 = Sq(s.X - c.X) + Sq(s.Y - c.Y);
                if (d2 > s.Range * s.Range) continue;
                if (best == null || s.Up[2] > best.Up[2] || (s.Up[2] == best.Up[2] && d2 < bestD)) { best = s; bestD = d2; }
            }
            if (best == null) return;
            int t = best.Up[2];
            c.Buffed = true;
            c.Interval *= BuffInterval(t);
            if (t >= 2) c.Pierce += t >= 5 ? 2 : 1;
            if (t >= 3) c.Damage += t >= 5 ? 2 : 1;
            if (t >= 4) c.Detects = true;
            if (t >= 5) c.Range *= 1.15f;
        }

        static double BuffInterval(int t) { return t >= 5 ? 0.7 : t >= 4 ? 0.8 : 0.9; }

        // ---------- info ----------

        static string N(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ','); }
        static string Every(double s) { return (s < 5 && s == Math.Floor(s) ? "každé " : "každých ") + N(s) + " s"; }

        public string Info(Cat c)
        {
            if (c == null) return "";
            var lines = new List<string>();
            var order = new List<int> { 0, 1, 2 };
            order.Sort((a, b) => c.Up[a] != c.Up[b] ? c.Up[b].CompareTo(c.Up[a]) : a.CompareTo(b));
            foreach (int p in order)
                if (c.Up[p] > 0) PathInfo(c, p, c.Up[p], lines);
            if (c.Kind == TowerKind.Spac && c.Up[0] == 0) lines.Add("Zpomalí o 35 %");
            if (c.Kind == TowerKind.Pamlskarna && c.Up[0] == 0) lines.Add("Za kolo " + stashIncome[0] + " pamlsků");
            if (lines.Count > 2) lines.RemoveRange(2, lines.Count - 2);
            return string.Join("\n", lines);
        }

        void PathInfo(Cat c, int p, int t, List<string> l)
        {
            switch (c.Kind)
            {
                case TowerKind.Drapka:
                    if (p == 0 && t >= 2) l.Add((t >= 5 ? 3 : 2) + " švihy za útok");
                    if (p == 1 && t >= 3) l.Add(t >= 5 ? "Vír odhodí vysavače o " + KnockBack + " px" : "Vír zasáhne vše v dosahu");
                    if (p == 2 && t >= 2)
                        l.Add(t == 2 ? "Kousne každý 3. útok za " + BiteDmg(t) : t == 3 ? "Kousne každý 2. útok za " + BiteDmg(t) + ", obřím ×2"
                            : t == 4 ? "Kousne každý útok za " + BiteDmg(t) + ", omráčí 0,4 s" : "Kousne každý útok za " + BiteDmg(t) + ", obřím ×4");
                    break;
                case TowerKind.Chlupac:
                    if (p == 0 && t >= 2)
                        l.Add(t == 2 ? "Zpomalí o 30 % na 1,5 s" : t == 3 ? "3 kuličky, zpomalí o 30 %" : t == 4 ? "5 kuliček, zpomalí o 30 %" : "5 kuliček, každý 3. útok 8 dokola");
                    if (p == 2 && t >= 3) l.Add(t >= 5 ? "Omráčí 0,5 s, obří zpomalí o 50 %" : "Trefí vysavač kdekoli v obýváku");
                    break;
                case TowerKind.Klubickarka:
                    if (p == 0 && t >= 2) l.Add("Omráčí na " + N(t >= 5 ? 1.2 : 0.6) + " s (obří zpomalí)");
                    if (p == 1 && t >= 2) l.Add((t >= 5 ? "3 cíle naráz, " : "") + (t >= 4 ? "8 mini klubek za 2" : "4 mini klubka za 1"));
                    if (p == 2)
                        l.Add(t <= 2 ? "Zpomalí o " + (t >= 2 ? 40 : 25) + " % na 2 s" : t == 3 ? "Pavučina 4 s, brzdí o 50 %"
                            : t == 4 ? "Pavučina 8 s, 1 poškození za 0,5 s" : "Pavučina mrazí 1 s, 3 za 0,5 s");
                    break;
                case TowerKind.Laserovka:
                    if (p == 0 && t >= 2) l.Add(t >= 5 ? "3 paprsky propálí celou řadu" : "Míří na " + (t >= 4 ? 3 : 2) + " vysavače");
                    if (p == 1 && t >= 3) l.Add("Zasáhne vše v dosahu");
                    if (p == 2 && t >= 2)
                        l.Add("Hoří " + BurnDmgOf(t) + " za 0,5 s" + (t >= 3 ? " · žár " + HeatDmg(t) + " " + Every(HeatEvery(t)) : " po 2 s"));
                    break;
                case TowerKind.Spac:
                    if (p == 0)
                    {
                        l.Add("Zpomalí o " + (int)Math.Round(SpacSlow(t) * 100) + " %" + (t >= 3 ? ", každý zásah +" + (t >= 5 ? 3 : 1) : ""));
                        if (t >= 2) l.Add("Mráz " + Every(FreezeEvery(t)) + " na " + N(FreezeLen(t)) + " s" + (t >= 5 ? " (i obří)" : ""));
                    }
                    if (p == 1 && t >= 2)
                        l.Add(t >= 3 ? "Chrápe za " + c.Damage + " " + Every(c.Interval) + ", odhalí" : "Odhalí tiché vysavače v dosahu");
                    if (p == 2)
                    {
                        l.Add("Kočkám: interval ×" + N(BuffInterval(t)) + (t >= 3 ? ", +" + (t >= 5 ? 2 : 1) + " poškození" : ""));
                        if (t >= 2)
                            l.Add("+" + (t >= 5 ? 2 : 1) + " průraz" + (t >= 4 ? ", vidí tiché" : "") + (t >= 5 ? ", dosah ×1,15" : ""));
                    }
                    break;
                case TowerKind.Pamlskarna:
                    if (p == 0) l.Add("Za kolo " + stashIncome[t] + " pamlsků");
                    if (p == 1)
                    {
                        if (t >= 3) l.Add("Odměny +" + (t >= 5 ? 50 : 25) + " %, úrok " + (t >= 4 ? 10 : 5) + " % (max " + InterestMax(t) + ")");
                        l.Add("+" + (t >= 4 ? 4 : 2) + " životy za kolo" + (t == 2 ? ", úrok 5 % (max 200)" : ""));
                    }
                    if (p == 2) l.Add("Rozvoz " + deliveryTreats[t] + " " + Every(deliveryEvery[t]));
                    break;
            }
        }

        static int BiteDmg(int t) { return t >= 5 ? 60 : t == 4 ? 20 : t == 3 ? 10 : 3; }
        static int BurnDmgOf(int t) { return t >= 5 ? 6 : t >= 4 ? 3 : 1; }
        static int HeatDmg(int t) { return t >= 5 ? 70 : t >= 4 ? 25 : 10; }
        static double HeatEvery(int t) { return t >= 5 ? 2 : t >= 4 ? 3 : 5; }
        static float HeatRadius(int t) { return t >= 5 ? 100 : t >= 4 ? 70 : 60; }
        static float SpacSlow(int t) { return t >= 4 ? 0.6f : t >= 1 ? 0.5f : 0.35f; }
        static double FreezeEvery(int t) { return t >= 5 ? 1.5 : t >= 4 ? 2 : 3; }
        static double FreezeLen(int t) { return t >= 4 ? 1.5 : 1; }
        static int InterestMax(int t) { return t >= 5 ? 1200 : t >= 4 ? 500 : 200; }

        // ---------- rounds ----------

        class Group { public VacKind Kind; public int Count; public double Spacing, Delay; public bool Stealth; }
        struct Spawn { public double T; public VacKind Kind; public bool Stealth; }

        // "count Kind spacing delay [s]" per group; delay = start time within the round, s = stealth.
        static readonly string[] Rounds =
        {
            "20 Robik 1 0",
            "30 Robik .75 0",
            "20 Robik .8 0, 8 Plus .9 6",
            "30 Robik .5 0, 15 Plus .8 4",
            "10 Robik .6 0, 25 Plus .6 3",
            "15 Plus .6 0, 6 Turbo 1.2 4",
            "25 Robik .4 0, 20 Plus .5 3, 10 Turbo .9 8",
            "20 Plus .5 0, 15 Turbo .7 4",
            "30 Turbo .55 0",
            "40 Plus .3 0, 4 Sprint 1.5 6",
            "15 Turbo .5 0, 12 Sprint .8 4",
            "20 Turbo .5 0, 8 Plus .7 5 s",
            "25 Sprint .5 0, 15 Turbo .5 6",
            "30 Sprint .45 0, 4 Raketa 1.2 10",
            "15 Raketa .7 0, 20 Turbo .4 3, 10 Sprint .7 8 s",
            "40 Sprint .3 0, 8 Raketa .8 6",
            "20 Raketa .5 0, 12 Turbo .6 4 s",
            "3 Dvojce 3 0, 30 Sprint .35 2",
            "6 Dvojce 2 0, 15 Raketa .5 3",
            "10 Dvojce 1.4 0, 20 Sprint .4 4 s",
            "2 Pancerak 4 0, 30 Raketa .35 2",
            "4 Pancerak 3 0, 10 Dvojce 1.2 2",
            "15 Dvojce .9 0, 15 Raketa .5 3 s, 3 Pancerak 3 8",
            "3 Disko 3 0, 20 Raketa .4 2",
            "8 Disko 1.6 0, 6 Pancerak 2 5",
            "12 Dvojce .8 0 s, 10 Disko 1.2 4",
            "15 Disko 1 0, 8 Pancerak 1.6 5, 40 Raketa .2 10",
            "2 Keramik 4 0, 15 Disko .9 3",
            "6 Keramik 2 0, 20 Dvojce .6 3",
            "1 Prumyslak 1 0, 10 Disko 1 6",
            "10 Keramik 1.2 0, 10 Disko 1 4 s",
            "15 Pancerak 1 0, 20 Disko .8 4",
            "12 Keramik 1 0, 30 Raketa .3 4 s",
            "20 Keramik .8 0, 10 Pancerak 1 5",
            "2 Prumyslak 8 0, 15 Keramik .8 4",
            "25 Keramik .6 0, 20 Disko .6 5 s",
            "30 Keramik .5 0, 15 Pancerak .8 6 s",
            "3 Prumyslak 6 0, 25 Keramik .6 3",
            "40 Keramik .4 0, 2 Prumyslak 8 6, 20 Disko .6 10 s",
            "1 Obr 1 0, 20 Keramik .8 20",
        };
        static readonly List<Group>[] table;

        static List<Group> Parse(string s)
        {
            var list = new List<Group>();
            foreach (var part in s.Split(','))
            {
                var w = part.Trim().Split(' ');
                list.Add(new Group
                {
                    Count = int.Parse(w[0]), Kind = (VacKind)Enum.Parse(typeof(VacKind), w[1]),
                    Spacing = double.Parse(w[2], CultureInfo.InvariantCulture), Delay = double.Parse(w[3], CultureInfo.InvariantCulture),
                    Stealth = w.Length > 4 && w[4] == "s"
                });
            }
            return list;
        }

        // ---------- free play ramp: every curve of the rounds after Defs.FinalRound is here (n = round - 40) ----------
        // Exponential toughness, so a defense that stops investing breaks within ~10 rounds wherever it stopped
        // (Sim, Medium: the bot keeps playing → median end 64, max 76; stops at 50 → lost by 62 in 99 %; stops at 70 → lost by 76).

        static int FreeN(int round) { return Math.Max(0, round - Defs.FinalRound); }
        /// <summary>Layer Hp × of Keramik: 1.08^n (×2,2 at n 10, ×10 at n 30).</summary>
        static double HpMulFor(int round) { return Math.Pow(1.08, FreeN(round)); }
        /// <summary>Layer Hp × of bosses (Průmyslák, Obr), the fastest: 1.10^n (×2,6 at n 10, ×17 at n 30).</summary>
        static double BossMulFor(int round) { return Math.Pow(1.10, FreeN(round)); }
        /// <summary>Speed × of every vacuum: +2 % a round, at most ×1,6.</summary>
        static double SpeedMulFor(int round) { return Math.Min(1.6, 1 + 0.02 * FreeN(round)); }
        /// <summary>Pop reward × (classic late-game rule): 1 / (1 + n/10), half at n 10.</summary>
        static double PayMulFor(int round) { return 1 / (1 + 0.1 * FreeN(round)); }

        static List<Group> GroupsFor(int round)
        {
            if (round <= Defs.FinalRound) return table[round];
            // free play: denser every round; an Obr every 5th round, every 4th from n 15, 3rd from 30, 2nd from 45, each round from 60
            int n = FreeN(round);
            var list = new List<Group>();
            list.Add(new Group { Kind = VacKind.Keramik, Count = 20 + 4 * n, Spacing = Math.Max(0.2, 0.6 - 0.015 * n) });
            list.Add(new Group { Kind = VacKind.Prumyslak, Count = 2 + n / 2, Spacing = Math.Max(2, 6 - 0.2 * n), Delay = 4 });
            list.Add(new Group { Kind = VacKind.Disko, Count = 15 + 2 * n, Spacing = 0.5, Delay = 8, Stealth = true });
            if (n % Math.Max(1, 5 - n / 15) == 0) list.Add(new Group { Kind = VacKind.Obr, Count = 1 + n / 20, Spacing = 10, Delay = 12 });
            return list;
        }

        public bool StartRound()
        {
            if (RoundActive || Lost) return false;
            Round++;
            hpMul = HpMulFor(Round);
            bossHpMul = BossMulFor(Round);
            speedMul = SpeedMulFor(Round);
            payMul = PayMulFor(Round);
            spawns = new List<Spawn>();
            foreach (var gr in GroupsFor(Round))
                for (int i = 0; i < gr.Count; i++)
                    spawns.Add(new Spawn { T = gr.Delay + i * gr.Spacing, Kind = gr.Kind, Stealth = gr.Stealth });
            spawns.Sort((a, b) => a.T.CompareTo(b.T));
            spawnIdx = 0;
            roundTime = 0;
            RoundActive = true;
            return true;
        }

        public string NextRoundPreview()
        {
            int r = Round + 1;
            var keys = new List<KeyValuePair<VacKind, bool>>();
            var counts = new List<int>();
            foreach (var gr in GroupsFor(r))
            {
                var key = new KeyValuePair<VacKind, bool>(gr.Kind, gr.Stealth);
                int i = keys.IndexOf(key);
                if (i < 0) { keys.Add(key); counts.Add(gr.Count); }
                else counts[i] += gr.Count;
            }
            var sb = new StringBuilder("Kolo " + r + ": ");
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(counts[i]).Append("× ").Append(Defs.Vac(keys[i].Key).Name);
                if (keys[i].Value) sb.Append(" (tiché)");
            }
            return sb.ToString();
        }

        static readonly int[] leakCost;
        static int CalcLeak(VacKind k)
        {
            int sum = 1;
            foreach (var ch in Defs.Vac(k).Children) sum += CalcLeak(ch);
            return sum;
        }
        public static int LeakCost(VacKind k) { return leakCost[(int)k]; }

        void EndRound()
        {
            RoundActive = false;
            Money += 100 + Round;
            int held = Money;
            bool stash = false;
            foreach (var c in Cats)
            {
                if (c.Kind != TowerKind.Pamlskarna) continue;
                stash = true;
                int b = c.Up[1];
                Money += stashIncome[c.Up[0]];
                if (b >= 1) Lives += b >= 4 ? 4 : 2;
                if (b >= 2) Money += Math.Min(InterestMax(b), held * (b >= 4 ? 10 : 5) / 100);
                c.LastAttack = Time;
                AddFx(FxKind.Coins, c.X, c.Y, 0, 0, 0, 0, 1.0, 0xFAB005, c.Up[0]);
            }
            if (stash) Play("coins");
            Play("round");
            if (Round >= Defs.FinalRound && !Won)
            {
                Won = true;
                Play("win");
            }
            else if (AutoStart) StartRound();
        }

        // ---------- simulation ----------

        public void Step(double dt)
        {
            Time += dt;
            if (!Lost)
            {
                if (RoundActive) { SpawnDue(dt); Deliveries(dt); }
                Statuses();
                Auras(dt);
                Webs();
                Burns();
                Move(dt);
                if (!Lost)
                {
                    Attacks(dt);
                    MoveShots(dt);
                }
                Vacs.RemoveAll(v => v.Dead);
                Vacs.AddRange(born);
                born.Clear();
                if (RoundActive && !Lost && spawnIdx >= spawns.Count && Vacs.Count == 0) EndRound();
            }
            Effects.RemoveAll(fxExpired);
            if (Effects.Count > MaxFx)
            {
                // drop the oldest effects, but never a web: it is still slowing vacuums
                int drop = Effects.Count - MaxFx, w = 0;
                for (int i = 0; i < Effects.Count; i++)
                {
                    var f = Effects[i];
                    if (drop > 0 && f.Kind != FxKind.Web) { drop--; continue; }
                    Effects[w++] = f;
                }
                Effects.RemoveRange(w, Effects.Count - w);
            }
        }

        void SpawnDue(double dt)
        {
            roundTime += dt;
            while (spawnIdx < spawns.Count && spawns[spawnIdx].T <= roundTime)
            {
                var s = spawns[spawnIdx++];
                Vacs.Add(MakeVac(s.Kind, s.Stealth, 0));
                if (Defs.Vac(s.Kind).Boss) Play("boss");
            }
        }

        void Deliveries(double dt)
        {
            foreach (var c in Cats)
            {
                int t = c.Kind == TowerKind.Pamlskarna ? c.Up[2] : 0;
                if (t == 0) continue;
                c.Timer += dt;
                if (c.Timer < deliveryEvery[t]) continue;
                c.Timer -= deliveryEvery[t];
                Money += deliveryTreats[t];
                c.LastAttack = Time;
                AddFx(FxKind.Coins, c.X, c.Y, 0, 0, 0, 0, 0.8, 0xFAB005, t);
                Play("coins");
            }
        }

        /// <summary>Puts a vacuum on the runner at once (tests and screenshots); it does not belong to any round.</summary>
        public Vac AddVac(VacKind k, bool stealth, double dist)
        {
            var v = MakeVac(k, stealth, dist);
            Vacs.Add(v);
            return v;
        }

        Vac MakeVac(VacKind k, bool stealth, double dist)
        {
            var v = new Vac { Id = ++nextId, Stealth = stealth, Dist = dist };
            SetKind(v, k);
            PathPos(dist, out v.X, out v.Y, out v.Angle);
            return v;
        }

        void SetKind(Vac v, VacKind k)
        {
            var d = Defs.Vac(k);
            double mul = d.Boss ? bossHpMul : k == VacKind.Keramik ? hpMul : 1;
            v.Kind = k;
            // capped so a very long free play cannot overflow int (1.10^n passes 2^31 / 2500 near round 200)
            v.MaxHp = (int)Math.Min(1e9, Math.Round(d.Hp * mul));
            v.Hp = v.MaxHp;
            v.HpFrac = 1;
            v.Tough = mul >= 16 ? 4 : mul >= 8 ? 3 : mul >= 4 ? 2 : mul >= 2 ? 1 : 0;
            v.Speed = (float)(d.Speed * speedMul);
            v.Radius = d.Radius;
            v.Boss = d.Boss;
        }

        void Statuses()
        {
            foreach (var v in Vacs)
            {
                v.Frozen = v.FrozenUntil > Time;
                v.Slow = v.SlowUntil > Time ? v.HitSlow : 0;
                v.Revealed = false;
                v.Lulled = 0;
            }
        }

        void Freeze(Vac v, double secs)
        {
            if (Time + secs > v.FrozenUntil) v.FrozenUntil = Time + secs;
            v.Frozen = true;
        }

        void Auras(double dt)
        {
            foreach (var c in Cats)
            {
                if (c.Kind != TowerKind.Spac) continue;
                int a = c.Up[0], b = c.Up[1];
                bool any = false;
                foreach (var v in Vacs) if (!v.Dead && InRange(c, v)) { any = true; break; }
                if (a >= 2 && c.Timer2 > 0) c.Timer2 -= dt;
                if (b >= 3 && c.Timer > 0) c.Timer -= dt;
                if (c.Timer3 > 0) c.Timer3 -= dt;
                bool pulse = any && a >= 2 && c.Timer2 <= 0;
                bool snore = any && b >= 3 && c.Timer <= 0;
                if (pulse) c.Timer2 += FreezeEvery(a);
                if (snore) c.Timer += c.Interval;
                float slow = SpacSlow(a);
                int lull = a >= 5 ? 3 : a >= 3 ? 1 : 0;
                if (any)
                {
                    foreach (var v in Vacs)
                    {
                        if (v.Dead || !InRange(c, v)) continue;
                        float s = v.Boss ? slow * 0.5f : slow;
                        // after a frost freeze a vacuum thaws for 0.5 s first, or a tier-5 Spac (1.5 s every 1.5 s) would hold it forever
                        if (pulse && (!v.Boss || a >= 5) && Time >= v.ThawUntil)
                        {
                            double len = v.Boss ? 0.4 : FreezeLen(a);
                            Freeze(v, len);
                            v.ThawUntil = Time + len + 0.5;
                        }
                        else if (pulse && v.Boss && a < 5) SlowHit(v, 0.5f, 1);
                        if (s > v.Slow) v.Slow = s;
                        if (lull > v.Lulled) v.Lulled = lull;
                        if (b >= 2) v.Revealed = true;
                        if (snore) Hit(v, Dmg(c, v, c.Damage), DmgType.Energy, c, false);
                    }
                }
                if (pulse)
                {
                    c.LastAttack = Time;
                    AddFx(FxKind.Freeze, c.X, c.Y, 0, 0, c.Range, 0, 0.5, 0xA5D8FF, a);
                    Play("freeze");
                }
                if (snore)
                {
                    c.LastAttack = Time;
                    AddFx(FxKind.Zzz, c.X, c.Y, 0, 0, c.Range, 0, 1.2, 0xFFF4E6, b);
                    Play("zzz");
                }
                else if (c.Timer3 <= 0)
                {
                    c.Timer3 = 2 + rnd.NextDouble() * 2;
                    AddFx(FxKind.Zzz, c.X, c.Y, 0, 0, 0, 0, 1.2, 0xFFF4E6, b);
                }
            }
        }

        void Webs()
        {
            for (int i = webs.Count - 1; i >= 0; i--)
                if (webs[i].Until <= Time) webs.RemoveAt(i);
            foreach (var w in webs)
            {
                bool tick = w.Tier >= 4 && Time >= w.NextTick;
                if (tick) w.NextTick += 0.5;
                int dmg = w.Tier >= 5 ? 3 : 1;
                float r2 = w.R * w.R;
                foreach (var v in Vacs)
                {
                    if (v.Dead || Sq(v.X - w.X) + Sq(v.Y - w.Y) > r2) continue;
                    float s = v.Boss ? (w.Tier >= 5 ? 0.5f : 0.25f) : 0.5f;
                    if (s > v.Slow) v.Slow = s;
                    if (w.Tier >= 5 && !v.Boss && w.Entered.Add(v.Id)) Freeze(v, 1);
                    if (tick) Hit(v, Dmg(w.Owner, v, dmg), DmgType.Blast, w.Owner, false);
                }
            }
        }

        void MakeWeb(Cat c, float x, float y, float r)
        {
            int n = 0, first = -1;
            for (int i = 0; i < webs.Count; i++)
                if (webs[i].Owner == c) { if (first < 0) first = i; n++; }
            if (n >= MaxWebs)
            {
                webs[first].Fx.Life = -1;   // expires at once
                webs.RemoveAt(first);
            }
            float px, py, ang;
            NearestOnPath(x, y, out px, out py, out ang);
            int t = c.Up[2];
            double life = t >= 4 ? 8 : 4;
            var fx = AddFx(FxKind.Web, px, py, 0, 0, r, ang, life, 0, t);
            webs.Add(new Web
            {
                Owner = c, X = px, Y = py, R = r, Until = Time + life, NextTick = Time + 0.5, Tier = t, Fx = fx,
                Entered = t >= 5 ? new HashSet<int>() : null
            });
            Play("web");
        }

        void Burns()
        {
            foreach (var v in Vacs)
            {
                while (!v.Dead && v.BurnNext <= v.BurnUntil && v.BurnNext <= Time)
                {
                    v.BurnNext += 0.5;
                    AddFx(FxKind.Burn, v.X, v.Y, 0, 0, v.Radius, 0, 0.5, 0xFF6B00, v.BurnTier);
                    Hit(v, Dmg(v.BurnSrc, v, v.BurnDmg), DmgType.Energy, v.BurnSrc, false);
                }
            }
        }

        // a weaker slow never shortens or weakens a stronger one still running
        void SlowHit(Vac v, float amount, double secs)
        {
            bool active = v.SlowUntil > Time;
            if (!active || amount > v.HitSlow) { v.HitSlow = amount; v.SlowUntil = Time + secs; }
            else if (amount == v.HitSlow) v.SlowUntil = Math.Max(v.SlowUntil, Time + secs);
            if (amount > v.Slow) v.Slow = amount;
        }

        void Move(double dt)
        {
            foreach (var v in Vacs)
            {
                if (v.Dead) continue;
                if (!v.Frozen) v.Dist += v.Speed * (1 - v.Slow) * dt;
                if (v.Dist >= PathLength)
                {
                    v.Dead = true;
                    Leaked++;
                    Lives = Math.Max(0, Lives - LeakCost(v.Kind));   // several leaks in one step never go below 0
                    var end = Defs.Path[Defs.Path.Length - 1];
                    AddFx(FxKind.Leak, end.X, end.Y, 0, 0, v.Radius, 0, 0.6, Defs.Vac(v.Kind).Color, 0);
                    Play("leak");
                    if (Lives <= 0 && !Lost)
                    {
                        Lives = 0;
                        Lost = true;
                        RoundActive = false;
                        Play("lose");
                    }
                    continue;
                }
                PathPos(v.Dist, out v.X, out v.Y, out v.Angle);
            }
        }

        bool InRange(Cat c, Vac v)
        {
            float r = c.Range + v.Radius * 0.5f;
            return Sq(v.X - c.X) + Sq(v.Y - c.Y) <= r * r;
        }

        bool Sees(Cat c, Vac v) { return !v.Stealth || c.Detects || v.Revealed; }

        Vac FindTarget(Cat c, Vac ex1, Vac ex2)
        {
            Vac best = null;
            double bestKey = 0;
            foreach (var v in Vacs)
            {
                if (v.Dead || v == ex1 || v == ex2 || !Sees(c, v) || !InRange(c, v)) continue;
                double key;
                switch (c.Target)
                {
                    case Targeting.Last: key = -v.Dist; break;
                    case Targeting.Strong: key = LeakCost(v.Kind) * 100000.0 + v.Dist; break;
                    case Targeting.Close: key = -(Sq(v.X - c.X) + Sq(v.Y - c.Y)); break;
                    default: key = v.Dist; break;
                }
                if (best == null || key > bestKey) { best = v; bestKey = key; }
            }
            return best;
        }

        // the cat's BossMul against bosses (specials with their own multiplier do not call this)
        static int Dmg(Cat c, Vac v, int dmg)
        {
            return v.Boss && c != null && c.BossMul > 1 ? (int)Math.Round(dmg * c.BossMul) : dmg;
        }

        // returns layers popped
        int Hit(Vac v, int dmg, DmgType type, Cat src, bool pierceArmor)
        {
            if (v.Dead || dmg <= 0) return 0;
            if (type == DmgType.Sharp && !pierceArmor && Defs.Vac(v.Kind).Armored) { Play("clank"); return 0; }
            dmg += v.Lulled;
            v.Hp -= dmg;
            int popped = 0;
            while (v.Hp <= 0)
            {
                int left = -v.Hp;
                var d = Defs.Vac(v.Kind);
                popped++;
                PoppedTotal++;
                moneyFrac += popMul * payMul;
                int whole = (int)moneyFrac;
                Money += whole;
                moneyFrac -= whole;
                AddFx(FxKind.Pop, v.X, v.Y, 0, 0, d.Radius, (float)(rnd.NextDouble() * Math.PI * 2), 0.35, d.Color, d.Boss ? 2 : 0);
                if (d.Children.Length == 0) { v.Dead = true; break; }
                float spread = d.Boss ? 26 : 7;
                for (int i = 1; i < d.Children.Length; i++)
                {
                    var ch = MakeVac(d.Children[i], v.Stealth, Math.Max(0, v.Dist - spread * i));
                    ch.FrozenUntil = v.FrozenUntil;
                    born.Add(ch);
                }
                SetKind(v, d.Children[0]);
                v.Hp -= left;
            }
            if (!v.Dead) v.HpFrac = (float)v.Hp / v.MaxHp;
            if (src != null) src.Pops += popped;
            if (popped > 0) Play("pop");
            return popped;
        }

        void Attacks(double dt)
        {
            foreach (var c in Cats)
            {
                if (!Attacker(c.Kind)) continue;
                bool heat = c.Kind == TowerKind.Laserovka && c.Up[2] >= 3;
                if (heat && c.Timer2 > 0) c.Timer2 -= dt;
                if (c.Timer > 0) c.Timer -= dt;
                bool heatReady = heat && c.Timer2 <= 0;
                if (c.Timer > 0 && !heatReady) continue;
                var t = FindTarget(c, null, null);
                if (t == null) { if (c.Timer < 0) c.Timer = 0; continue; }
                if (heatReady)
                {
                    c.Timer2 += HeatEvery(c.Up[2]);
                    HeatBlast(c, t);
                    if (t.Dead) continue;
                }
                if (c.Timer > 0) continue;
                c.Timer += c.Interval;
                c.LastAttack = Time;
                c.Count++;
                c.Angle = (float)Math.Atan2(t.Y - c.Y, t.X - c.X);
                switch (c.Kind)
                {
                    case TowerKind.Drapka: Swipe(c, t); break;
                    case TowerKind.Chlupac: Spit(c, t); break;
                    case TowerKind.Klubickarka: Throw(c, t); break;
                    case TowerKind.Laserovka: Beam(c, t); break;
                }
            }
        }

        void Swipe(Cat c, Vac t)
        {
            int z = c.Up[2];
            if (z >= 2 && (z >= 4 || c.Count % (z == 3 ? 2 : 3) == 0))
            {
                int dmg = BiteDmg(z);
                if (t.Boss) dmg = z >= 5 ? dmg * 4 : z >= 3 ? dmg * 2 : Dmg(c, t, dmg);
                else if (z >= 4) Freeze(t, 0.4);
                AddFx(FxKind.Bite, t.X, t.Y, 0, 0, t.Radius, c.Angle, 0.25, 0, z);
                Play("bite");
                Hit(t, dmg, DmgType.Sharp, c, true);
            }
            int swipes = c.Up[0] >= 5 ? 3 : c.Up[0] >= 2 ? 2 : 1;
            bool spin = c.Up[1] >= 3, knock = c.Up[1] >= 5;
            int fur = Defs.Tower(c.Kind).Fur;
            for (int s = 0; s < swipes; s++)
            {
                if (s > 0 || t.Dead) t = FindTarget(c, null, null);
                if (t == null) break;
                c.Angle = (float)Math.Atan2(t.Y - c.Y, t.X - c.X);
                if (spin)
                {
                    foreach (var v in Vacs)
                    {
                        if (v.Dead || !Sees(c, v) || !InRange(c, v)) continue;
                        Hit(v, Dmg(c, v, c.Damage), DmgType.Sharp, c, c.Armor);
                        // only a vacuum the claws can hurt is knocked back (an untouched armored one would be held forever)
                        if (knock && !v.Dead && !v.Boss && (c.Armor || !Defs.Vac(v.Kind).Armored)) Knock(c, v);
                    }
                    AddFx(FxKind.Spin, c.X, c.Y, 0, 0, c.Range, c.Angle, 0.3, fur, c.Up[1]);
                }
                else
                {
                    buf.Clear();
                    foreach (var v in Vacs)
                        if (v != t && !v.Dead && Sees(c, v) && InRange(c, v))
                            buf.Add(new KeyValuePair<float, Vac>(Sq(v.X - t.X) + Sq(v.Y - t.Y), v));
                    if (buf.Count > c.Pierce - 1) buf.Sort(byKey);
                    Hit(t, Dmg(c, t, c.Damage), DmgType.Sharp, c, c.Armor);
                    for (int i = 0; i < buf.Count && i < c.Pierce - 1; i++) Hit(buf[i].Value, Dmg(c, buf[i].Value, c.Damage), DmgType.Sharp, c, c.Armor);
                    AddFx(FxKind.Swipe, c.X, c.Y, t.X, t.Y, c.Range, c.Angle, 0.2, fur, c.Up[0]);
                }
            }
            Play("swipe");
        }

        void Knock(Cat c, Vac v)
        {
            if (c.Knocked == null) c.Knocked = new Dictionary<int, double>();
            double last;
            if (c.Knocked.TryGetValue(v.Id, out last) && Time - last < 1.5) return;
            if (c.Knocked.Count > 400) c.Knocked.Clear();   // shortcut: forgets old cooldowns, only ids older than 1.5 s matter
            c.Knocked[v.Id] = Time;
            v.Dist = Math.Max(0, v.Dist - KnockBack);
        }

        static readonly Comparison<KeyValuePair<float, Vac>> byKey = (a, b) => a.Key.CompareTo(b.Key);

        void Spit(Cat c, Vac t)
        {
            if (c.Up[2] >= 3) { Snipe(c, t); return; }
            // lead the target: aim where it will be when the ball arrives
            float dist = (float)Math.Sqrt(Sq(t.X - c.X) + Sq(t.Y - c.Y));
            float ax, ay, aa;
            double lead = t.Frozen ? 0 : t.Speed * (1 - t.Slow) * dist / c.ShotSpeed;
            PathPos(t.Dist + lead, out ax, out ay, out aa);
            float ang = (float)Math.Atan2(ay - c.Y, ax - c.X);
            c.Angle = ang;
            int a = c.Up[0];
            int balls = a >= 4 ? 5 : a >= 3 ? 3 : 1;
            float gap = (float)((a >= 4 ? 10 : 12) * Math.PI / 180);
            for (int i = 0; i < balls; i++) Ball(c, ang + (i - (balls - 1) / 2f) * gap);
            if (a >= 5 && c.Count % 3 == 0)
                for (int i = 0; i < 8; i++) Ball(c, ang + (float)(Math.PI / 8 + i * Math.PI / 4));
            Play("spit");
        }

        void Ball(Cat c, float angle)
        {
            Shots.Add(new Shot
            {
                Kind = ProjKind.Hairball, X = c.X, Y = c.Y, Angle = angle, Owner = c, Speed = c.ShotSpeed,
                Pierce = c.Pierce, Damage = c.Damage, MaxTravel = c.Range * 1.3f, HitIds = new List<int>(4)
            });
        }

        void Snipe(Cat c, Vac t)
        {
            AddFx(FxKind.Trace, c.X, c.Y, t.X, t.Y, 0, c.Angle, 0.12, 0, c.Up[2]);
            if (c.Up[2] >= 5)
            {
                if (t.Boss) SlowHit(t, 0.5f, 1);
                else Freeze(t, 0.5);
            }
            Hit(t, Dmg(c, t, c.Damage), DmgType.Sharp, c, c.Armor);
            if (c.Up[0] >= 2 && !t.Dead) SlowHit(t, t.Boss ? 0.15f : 0.3f, 1.5);
            Play("snipe");
        }

        void Throw(Cat c, Vac t)
        {
            Yarn(c, t);
            if (c.Up[1] >= 5)
            {
                var t2 = FindTarget(c, t, null);
                if (t2 != null)
                {
                    Yarn(c, t2);
                    var t3 = FindTarget(c, t, t2);
                    if (t3 != null) Yarn(c, t3);
                }
            }
            Play("throw");
        }

        void Yarn(Cat c, Vac t)
        {
            Shots.Add(new Shot
            {
                Kind = ProjKind.Yarn, X = c.X, Y = c.Y, Angle = (float)Math.Atan2(t.Y - c.Y, t.X - c.X), Owner = c, Target = t, Tx = t.X, Ty = t.Y,
                Speed = c.ShotSpeed, Splash = c.Splash, Pierce = c.Pierce, Damage = c.Damage, MaxTravel = 2000
            });
        }

        void Beam(Cat c, Vac t)
        {
            stamp++;
            if (c.Up[1] >= 3)
            {
                int beams = 0;
                foreach (var v in Vacs)
                {
                    if (v.Dead || !Sees(c, v) || !InRange(c, v)) continue;
                    if (beams++ < 8) AddFx(FxKind.Beam, c.X, c.Y, v.X, v.Y, 0, 0, 0.13, 0xFF3B3B, c.Up[0]);
                    LaserHit(c, v);
                }
            }
            else
            {
                int n = c.Up[0] >= 4 ? 3 : c.Up[0] >= 2 ? 2 : 1;
                var t2 = n >= 2 ? FindTarget(c, t, null) : null;
                var t3 = n >= 3 && t2 != null ? FindTarget(c, t, t2) : null;
                BeamAt(c, t);
                if (t2 != null) BeamAt(c, t2);
                if (t3 != null) BeamAt(c, t3);
            }
            Play("laser");
        }

        void BeamAt(Cat c, Vac t)
        {
            if (c.Up[0] < 5)
            {
                AddFx(FxKind.Beam, c.X, c.Y, t.X, t.Y, 0, 0, 0.13, 0xFF3B3B, c.Up[0]);
                LaserHit(c, t);
                return;
            }
            // sun beam: from the cat through the target to the edge of range, hits everything near the line
            float dx = t.X - c.X, dy = t.Y - c.Y, len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) { dx = 1; dy = 0; len = 1; }
            dx /= len; dy /= len;
            float ex = c.X + dx * c.Range, ey = c.Y + dy * c.Range;
            AddFx(FxKind.Beam, c.X, c.Y, ex, ey, 0, (float)Math.Atan2(dy, dx), 0.13, 0xFF3B3B, c.Up[0]);
            LaserHit(c, t);
            foreach (var v in Vacs)
            {
                if (v.Dead || v.Stamp == stamp) continue;
                float along = (v.X - c.X) * dx + (v.Y - c.Y) * dy;
                if (along < 0 || along > c.Range) continue;
                float side = (v.X - c.X) * dy - (v.Y - c.Y) * dx;
                if (side * side <= LineWidth * LineWidth) LaserHit(c, v);
            }
        }

        void LaserHit(Cat c, Vac v)
        {
            if (v.Stamp == stamp) return;
            v.Stamp = stamp;
            Hit(v, Dmg(c, v, c.Damage), DmgType.Energy, c, false);
            int z = c.Up[2];
            if (z < 2 || v.Dead) return;
            int burn = BurnDmgOf(z);
            bool burning = v.BurnUntil >= v.BurnNext;
            if (!burning) v.BurnNext = Time + 0.5;
            if (!burning || burn >= v.BurnDmg) { v.BurnDmg = burn; v.BurnSrc = c; v.BurnTier = z; }
            v.BurnUntil = Time + 2;
        }

        void HeatBlast(Cat c, Vac t)
        {
            int z = c.Up[2], dmg = HeatDmg(z);
            float r = HeatRadius(z);
            float x = t.X, y = t.Y;
            foreach (var v in Vacs)
            {
                if (v.Dead || Sq(v.X - x) + Sq(v.Y - y) > Sq(r + v.Radius * 0.5f)) continue;
                Hit(v, v.Boss && z >= 5 ? dmg * 3 : Dmg(c, v, dmg), DmgType.Energy, c, false);
            }
            c.LastAttack = Time;
            AddFx(FxKind.Blast, x, y, 0, 0, r, 0, 0.4, 0xFF6B00, z);
            Play("blast");
        }

        void MoveShots(double dt)
        {
            for (int i = 0; i < Shots.Count; i++)
            {
                var s = Shots[i];
                if (s.Gone) continue;
                float step = (float)(s.Speed * dt);
                if (s.Kind == ProjKind.Yarn)
                {
                    if (s.Target != null && s.Target.Dead) s.Target = null;
                    if (s.Target != null) { s.Tx = s.Target.X; s.Ty = s.Target.Y; }
                    float dx = s.Tx - s.X, dy = s.Ty - s.Y, d = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (d <= step + 2) { s.X = s.Tx; s.Y = s.Ty; Explode(s); s.Gone = true; continue; }
                    s.Angle = (float)Math.Atan2(dy, dx);
                    s.X += dx / d * step;
                    s.Y += dy / d * step;
                    s.Travel += step;
                    if (s.Travel > s.MaxTravel) s.Gone = true;
                    continue;
                }
                s.X += (float)Math.Cos(s.Angle) * step;
                s.Y += (float)Math.Sin(s.Angle) * step;
                s.Travel += step;
                if (s.Kind == ProjKind.MiniYarn)
                {
                    bool touch = s.Travel >= s.MaxTravel;
                    if (!touch)
                        foreach (var v in Vacs)
                            if (!v.Dead && Sq(v.X - s.X) + Sq(v.Y - s.Y) <= Sq(v.Radius + 4)) { touch = true; break; }
                    if (touch) { Explode(s); s.Gone = true; }
                    continue;
                }
                // hairball
                var c = s.Owner;
                foreach (var v in Vacs)
                {
                    if (v.Dead || (v.Stealth && !c.Detects && !v.Revealed)) continue;
                    if (Sq(v.X - s.X) + Sq(v.Y - s.Y) > Sq(v.Radius + 6) || s.HitIds.Contains(v.Id)) continue;
                    s.HitIds.Add(v.Id);
                    Hit(v, Dmg(c, v, s.Damage), DmgType.Sharp, c, c.Armor);
                    if (c.Up[0] >= 2 && !v.Dead) SlowHit(v, v.Boss ? 0.15f : 0.3f, 1.5);
                    if (--s.Pierce <= 0) break;
                }
                if (s.Pierce <= 0 || s.Travel >= s.MaxTravel) s.Gone = true;
            }
            Shots.RemoveAll(s => s.Gone);
        }

        void Explode(Shot s)
        {
            var c = s.Owner;
            float radius = s.Splash;
            bool big = s.Kind == ProjKind.Yarn;
            int a = c.Up[0], b = c.Up[1], z = c.Up[2];
            buf.Clear();
            foreach (var v in Vacs)
            {
                if (v.Dead) continue;
                float d2 = Sq(v.X - s.X) + Sq(v.Y - s.Y);
                if (d2 <= Sq(radius + v.Radius * 0.5f)) buf.Add(new KeyValuePair<float, Vac>(d2, v));
            }
            if (buf.Count > s.Pierce) buf.Sort(byKey);
            for (int i = 0; i < buf.Count && i < s.Pierce; i++)
            {
                var v = buf[i].Value;
                if (big && a >= 2)
                {
                    if (v.Boss) SlowHit(v, 0.3f, 1);
                    else Freeze(v, a >= 5 ? 1.2 : 0.6);
                }
                if (big && z >= 1)
                {
                    float slow = z >= 2 ? 0.4f : 0.25f;
                    SlowHit(v, v.Boss ? slow * 0.5f : slow, 2);
                }
                Hit(v, Dmg(c, v, s.Damage), DmgType.Blast, c, false);
            }
            AddFx(FxKind.Blast, s.X, s.Y, 0, 0, radius, 0, 0.35, 0, a);   // mini yarns: same tier, R 30
            Play("blast");
            if (!big) return;
            if (z >= 3) MakeWeb(c, s.X, s.Y, 0.8f * radius);
            if (b >= 2)
            {
                int n = b >= 4 ? 8 : 4;
                for (int k = 0; k < n; k++)
                    Shots.Add(new Shot
                    {
                        Kind = ProjKind.MiniYarn, X = s.X, Y = s.Y, Angle = (float)(Math.PI / n + k * 2 * Math.PI / n), Owner = c,
                        Speed = 300, Splash = 30, Pierce = 6, Damage = b >= 4 ? 2 : 1, MaxTravel = 60
                    });
            }
        }

        Fx AddFx(FxKind k, float x, float y, float x2, float y2, float r, float angle, double life, int color, int tier)
        {
            var f = new Fx { Kind = k, X = x, Y = y, X2 = x2, Y2 = y2, R = r, Angle = angle, Born = Time, Life = life, Color = color, Tier = tier };
            Effects.Add(f);
            return f;
        }

        void Play(string name)
        {
            var s = Sound;
            if (s != null) s(name);
        }
    }
}
