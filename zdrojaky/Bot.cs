using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Kocky
{
    /// <summary>Automatic player: follows one of a few build orders, then keeps upgrading. Uses only the public Game API.</summary>
    public static class Bot
    {
        // Build orders. "@N" = from round N on; a letter buys a cat (D Drápka, C Chlupáč, K Klubíčkářka, L Laserovka,
        // S Předoucí kocour, P Pamlskárna); "D02^3" = raise path 2 of the first Drápka (index 0) to tier 3, one tier at a time.
        // A plan = the shared opening and middle game + two focus paths taken to tier 5 (Focus below).
        // The opening fixes at most two paths per cat and leaves the focus path free, so the crosspath rule always allows it.
        const string Opening =
            "@1 D D D01^1 @2 C @3 D00^1 D11^1 @4 C01^1 C00^1 @5 D @6 D10^1 D21^1 @8 D01^2 @9 C @10 C11^1 C00^2 " +
            "@12 K K01^1 @14 K00^1 S S01^1 @18 K K11^1 S01^2 @21 L L01^2 L00^1";

        // focus pairs: kind letter + path; together they cover all 18 paths, each pair in both orders
        // (the first focus usually reaches tier 5 by round 40, the second one rarely)
        static readonly string[][] Focus = Both(new[]
        {
            new[] { "D0", "P2" }, new[] { "D1", "K2" }, new[] { "L2", "S0" }, new[] { "S2", "C0" }, new[] { "D2", "L1" },
            new[] { "K0", "C1" }, new[] { "C2", "L0" }, new[] { "K1", "S1" }, new[] { "P0", "K0" }, new[] { "P1", "L0" },
        });

        static string[][] Both(string[][] pairs)
        {
            var all = new List<string[]>(pairs);
            foreach (var p in pairs) all.Add(new[] { p[1], p[0] });
            return all.ToArray();
        }

        // which existing cat (index) carries a focus path; -1 = buy a fresh cat for it
        static int FocusCat(char kind, int path)
        {
            switch (kind)
            {
                case 'D': return path == 2 ? 2 : path;      // D0 1-2-0, D1 1-1-0, D2 0-1-x
                case 'C': return path == 2 ? 1 : 0;         // C0 2-1-0, C1 0-1-x
                case 'K': return path == 2 ? 1 : 0;         // K0 2-1-0, K1 0-2-x
                case 'L': return path == 2 ? -1 : 0;        // L0 1-2-0
                case 'S': return 0;                         // S0 0-2-0
                default: return -1;
            }
        }

        static readonly string[] Plans = BuildPlans();

        static string[] BuildPlans()
        {
            // the opening as (round, token), and the round each cat of the opening is bought
            var opening = new List<KeyValuePair<int, string>>();
            var bought = new Dictionary<string, int>();
            var count = new Dictionary<char, int>();
            foreach (char k in Letters) count[k] = 0;
            int cur = 1;
            foreach (var tok in Opening.Split(' '))
            {
                if (tok[0] == '@') { cur = int.Parse(tok.Substring(1)); continue; }
                opening.Add(new KeyValuePair<int, string>(cur, tok));
                if (tok.Length == 1) bought[tok + count[tok[0]]++] = cur;
            }
            var plans = new string[Focus.Length];
            for (int v = 0; v < Focus.Length; v++)
            {
                var steps = new List<KeyValuePair<int, string>>(opening);
                var n = new Dictionary<char, int>(count);
                for (int f = 0; f < 2; f++)
                {
                    char k = Focus[v][f][0];
                    int path = Focus[v][f][1] - '0', idx = FocusCat(k, path);
                    // the first focus starts earlier than the second; a treat stash comes once the opening stands
                    bool stash = k == 'P';
                    int start = stash ? (f == 0 ? 12 : 14) : (f == 0 ? 13 : 17);
                    if (idx < 0)
                    {
                        // a fresh cat comes after the opening's cats of its kind, so it gets the next index
                        foreach (var b in bought) if (b.Key[0] == k) start = Math.Max(start, b.Value);
                        idx = n[k]++;
                        steps.Add(new KeyValuePair<int, string>(start, k.ToString()));
                    }
                    else start = Math.Max(start, bought[k.ToString() + idx]);
                    string id = k.ToString() + idx + path;
                    int t3 = start + (stash ? 7 : 5), t4 = Math.Max(f == 0 ? (stash ? 24 : 20) : 28, t3 + 2), t5 = Math.Max(f == 0 ? 31 : 35, t4 + 4);
                    steps.Add(new KeyValuePair<int, string>(start, id + "^2"));
                    steps.Add(new KeyValuePair<int, string>(t3, id + "^3"));
                    steps.Add(new KeyValuePair<int, string>(t4, id + "^4"));
                    steps.Add(new KeyValuePair<int, string>(t5, id + "^5"));
                }
                var sb = new StringBuilder();
                int last = 0;
                foreach (var s in steps.Select((s, i) => new { s, i }).OrderBy(x => x.s.Key).ThenBy(x => x.s.Value[0] == 'P' ? 2 : x.i < opening.Count ? 1 : 0).ThenBy(x => x.i).Select(x => x.s))
                {
                    if (s.Key != last) { sb.Append("@" + s.Key + " "); last = s.Key; }
                    sb.Append(s.Value + " ");
                }
                plans[v] = sb.ToString().Trim();
            }
            return plans;
        }

        /// <summary>The build order of a variant as text, and its two focus paths (e.g. "D0+L0").</summary>
        public static string PlanText(int v) { return Plans[v]; }
        public static string PlanName(int v) { return Focus[v][0] + "+" + Focus[v][1]; }

        class Step { public int MinRound, Index, Path, Need; public TowerKind Kind; }

        class State
        {
            public int Variant, Lives = int.MaxValue;
            public readonly HashSet<int> Refused = new HashSet<int>();   // cat id * 10 + path the legend rule kept below tier 5
            public List<Step> Plan;
            public readonly HashSet<Step> Skipped = new HashSet<Step>();
            public readonly Dictionary<int, int> Main = new Dictionary<int, int>();   // kind * 10 + index -> path the plan takes above tier 2
        }

        static readonly ConditionalWeakTable<Game, State> states = new ConditionalWeakTable<Game, State>();
        const string Letters = "DCKLSP";
        // main / side path a cat follows when the plan did not start one (Extra spending)
        static readonly int[] defMain = { 0, 0, 0, 0, 1, 0 }, defSide = { 1, 1, 1, 1, 0, 1 };

        static List<Step> ParsePlan(string s)
        {
            var steps = new List<Step>();
            var counts = new Dictionary<char, int>();
            int min = 1;
            foreach (var tok in s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok[0] == '@') { min = int.Parse(tok.Substring(1)); continue; }
                var st = new Step { MinRound = min, Kind = (TowerKind)Letters.IndexOf(tok[0]), Index = -1 };
                if (st.Kind < 0) throw new FormatException(tok);
                if (tok.Length > 1)
                {
                    st.Index = tok[1] - '0';
                    st.Path = tok[2] - '0';
                    st.Need = int.Parse(tok.Substring(4));
                }
                else
                {
                    int n;
                    counts.TryGetValue(tok[0], out n);
                    counts[tok[0]] = ++n;
                    st.Need = n;    // buy: need n cats of the kind
                }
                steps.Add(st);
            }
            return steps;
        }

        public static int PlanCount { get { return Plans.Length; } }

        /// <summary>Which build order this game's bot follows, or -1 before the bot first acted.</summary>
        public static int VariantOf(Game g)
        {
            State st;
            return states.TryGetValue(g, out st) ? st.Variant : -1;
        }

        /// <summary>How many cat paths of this game's bot the legend rule kept from tier 5.</summary>
        public static int LegendBlocksOf(Game g)
        {
            State st;
            return states.TryGetValue(g, out st) ? st.Refused.Count : 0;
        }

        // true (and remembered) when the legend rule is what stops this path
        static bool Refused(Game g, State st, Cat c, int path)
        {
            if (g.UpgradeBlock(c, path) != UpBlock.LegendTaken) return false;
            st.Refused.Add(c.Id * 10 + path);
            return true;
        }

        public static void Act(Game g, Random rnd)
        {
            if (g.Lost) return;
            var st = states.GetValue(g, k =>
            {
                int v = rnd.Next(Plans.Length);
                var ns = new State { Variant = v, Plan = ParsePlan(Plans[v]) };
                foreach (var p in ns.Plan) if (p.Index >= 0 && p.Need > 2) ns.Main[(int)p.Kind * 10 + p.Index] = p.Path;
                return ns;
            });
            int round = g.RoundActive ? g.Round : g.Round + 1;
            bool last = round >= Defs.FinalRound;   // nothing to save for any more: spend everything
            for (int guard = 0; guard < 40; guard++)
            {
                Step s = null;
                foreach (var p in st.Plan)
                    if (!st.Skipped.Contains(p) && !Done(g, p)) { s = p; break; }
                if (s == null || s.MinRound > round)
                {
                    // nothing scripted now: spend spare treats, keeping enough for the next scripted step
                    int reserve = s == null || last ? 0 : Price(g, s);
                    if (!Extra(g, st, rnd, reserve)) break;
                    continue;
                }
                int price = Price(g, s);
                if (price < 0)
                {
                    var c = s.Index >= 0 ? Nth(g, s.Kind, s.Index) : null;
                    if (c != null) Refused(g, st, c, s.Path);
                    st.Skipped.Add(s);
                    continue;
                }
                if (g.Money < price)
                {
                    // close to losing and still leaking: stop saving and strengthen what is there
                    if ((last || g.RoundActive && g.Lives < st.Lives && g.Lives * 3 < Defs.StartLives(g.Diff)) && Extra(g, st, rnd, 0)) continue;
                    break;
                }
                if (!Perform(g, rnd, s)) st.Skipped.Add(s);
            }
            st.Lives = g.Lives;
        }

        static List<Cat> OfKind(Game g, TowerKind k)
        {
            var list = new List<Cat>();
            foreach (var c in g.Cats) if (c.Kind == k) list.Add(c);
            return list;
        }

        static Cat Nth(Game g, TowerKind k, int i)
        {
            var list = OfKind(g, k);
            return i < list.Count ? list[i] : null;
        }

        static bool Done(Game g, Step s)
        {
            if (s.Index < 0) return OfKind(g, s.Kind).Count >= s.Need;
            var c = Nth(g, s.Kind, s.Index);
            return c != null && c.Up[s.Path] >= s.Need;
        }

        static int Price(Game g, Step s)
        {
            if (s.Index < 0) return g.PriceOf(s.Kind);
            var c = Nth(g, s.Kind, s.Index);
            return c == null ? -1 : g.UpgradePrice(c, s.Path);
        }

        static bool Perform(Game g, Random rnd, Step s)
        {
            if (s.Index < 0) return Buy(g, rnd, s.Kind) != null;
            return g.Upgrade(Nth(g, s.Kind, s.Index), s.Path);
        }

        // Spare money: the cheapest next upgrade along each cat's main path (up to tier 4, in free play up to 5 where
        // the legend rule allows) or its side path (up to 2), or another yarn / laser cat.
        // The main path is the one the plan takes above tier 2, else the highest one.
        static bool Extra(Game g, State st, Random rnd, int reserve)
        {
            Cat best = null;
            int bestPath = 0, bestPrice = int.MaxValue;
            int cap = g.Won ? Defs.TierCount : 4;
            var seen = new int[Defs.TowerCount];
            foreach (var c in g.Cats)
            {
                int k = (int)c.Kind, idx = seen[k]++, main = defMain[k];
                if (c.Kind == TowerKind.Pamlskarna) continue;
                if (!st.Main.TryGetValue(k * 10 + idx, out main))
                {
                    main = defMain[k];
                    for (int p = 0; p < Defs.PathCount; p++)
                        if (c.Up[p] > c.Up[main]) main = p;
                }
                int side = -1;
                for (int p = 0; p < Defs.PathCount; p++)
                    if (p != main && c.Up[p] > 0) side = p;
                if (side < 0) side = defSide[k] != main ? defSide[k] : defMain[k];
                int path;
                if (c.Up[main] < cap && !Refused(g, st, c, main)) path = main;
                else if (c.Up[side] < 2) path = side;
                else continue;
                int price = g.UpgradePrice(c, path);
                if (price >= 0 && price < bestPrice) { best = c; bestPath = path; bestPrice = price; }
            }
            TowerKind buyKind = OfKind(g, TowerKind.Klubickarka).Count < 3 ? TowerKind.Klubickarka : TowerKind.Laserovka;
            int buyPrice = OfKind(g, buyKind).Count < 10 ? g.PriceOf(buyKind) : int.MaxValue;
            if (buyPrice < bestPrice)
            {
                if (g.Money - reserve < buyPrice) return false;
                return Buy(g, rnd, buyKind) != null;
            }
            if (best == null || g.Money - reserve < bestPrice) return false;
            return g.Upgrade(best, bestPath);
        }

        // ---------- spots ----------

        class Spot { public float X, Y; public int Cover; }

        static readonly Dictionary<int, Spot[]> spotCache = new Dictionary<int, Spot[]>();

        /// <summary>Every buildable grid point, best path coverage for the range first.</summary>
        static Spot[] SpotsFor(float range)
        {
            int key = (int)range;
            lock (spotCache)
            {
                Spot[] found;
                if (spotCache.TryGetValue(key, out found)) return found;
                var probe = new Game(Difficulty.Easy, 0);
                var samples = new List<float[]>();
                for (float d = 0; d < Game.PathLength; d += 8)
                {
                    float x, y, a;
                    Game.PathPos(d, out x, out y, out a);
                    if (x >= 0) samples.Add(new[] { x, y });
                }
                var list = new List<Spot>();
                for (float y = 24; y <= Defs.Map.Bottom - 24; y += 12)
                    for (float x = 24; x <= Defs.Map.Right - 24; x += 12)
                    {
                        if (!probe.CanPlace(TowerKind.Drapka, x, y)) continue;
                        int cover = 0;
                        foreach (var s in samples)
                            if ((s[0] - x) * (s[0] - x) + (s[1] - y) * (s[1] - y) <= range * range) cover++;
                        list.Add(new Spot { X = x, Y = y, Cover = cover });
                    }
                list.Sort((a, b) => a.Cover != b.Cover ? b.Cover.CompareTo(a.Cover) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
                found = list.ToArray();
                spotCache[key] = found;
                return found;
            }
        }

        static Cat Buy(Game g, Random rnd, TowerKind k)
        {
            if (g.Money < g.PriceOf(k)) return null;
            if (k == TowerKind.Pamlskarna)
            {
                // the stash needs no path: take the spot that covers the least runner
                var all = SpotsFor(120);
                for (int i = all.Length - 1; i >= 0; i--)
                    if (g.CanPlace(k, all[i].X, all[i].Y)) return g.Place(k, all[i].X, all[i].Y);
                return null;
            }
            float range = Defs.Tower(k).Range + (k == TowerKind.Chlupac ? 60 : 0);
            var spots = SpotsFor(range);
            // inside the aura of a supporting Spac (Pohoda) if a good spot there is free
            Cat aura = null;
            foreach (var c in g.Cats)
                if (c.Kind == TowerKind.Spac && c.Up[2] > 0 && (aura == null || c.Up[2] > aura.Up[2])) aura = c;
            if (aura != null)
                foreach (var s in spots)
                {
                    if (s.Cover < spots[0].Cover * 0.6f) break;
                    if ((s.X - aura.X) * (s.X - aura.X) + (s.Y - aura.Y) * (s.Y - aura.Y) <= (aura.Range - 4) * (aura.Range - 4) && g.CanPlace(k, s.X, s.Y))
                        return g.Place(k, s.X, s.Y);
                }
            var pick = new List<Spot>();
            foreach (var s in spots)
            {
                if (pick.Count > 0 && (s.Cover < pick[0].Cover * 0.92f || pick.Count >= 5)) break;
                if (g.CanPlace(k, s.X, s.Y)) pick.Add(s);
            }
            if (pick.Count == 0) return null;
            var p = pick[rnd.Next(pick.Count)];
            return g.Place(k, p.X, p.Y);
        }
    }

    /// <summary>Headless balance runs: the bot plays every difficulty and a plain-ASCII report is written.</summary>
    public static class Sim
    {
        class Result
        {
            public bool Won, Lost;
            public int Round, Money, Lives, Variant, FirstT4, FirstT5, LegendBlocks;
            public int[] Cats = new int[Defs.TowerCount];
            public int[,] Tiers = new int[Defs.TowerCount, Defs.PathCount];
            public int[,] T4 = new int[Defs.TowerCount, Defs.PathCount], T5 = new int[Defs.TowerCount, Defs.PathCount];
            public int[] LivesLost = new int[Defs.FinalRound + 1];
            public string Error;
        }

        public static void Run(int games, string file)
        {
            var diffs = (Difficulty[])Enum.GetValues(typeof(Difficulty));
            var res = new Result[diffs.Length * games];
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Parallel.For(0, res.Length, i => { res[i] = Play(diffs[i / games], i % games, int.MaxValue, Defs.FinalRound); });
            watch.Stop();
            // free play on Medium: the bot keeps playing, or stops spending from round 50 / 70 on (same seeds as the main games)
            var fp = new Result[FreeModes.Length * games];
            var watch2 = System.Diagnostics.Stopwatch.StartNew();
            Parallel.For(0, fp.Length, i => { fp[i] = Play(Difficulty.Medium, i % games, FreeModes[i / games], FreeCap); });
            watch2.Stop();

            int plans = Bot.PlanCount;
            var sb = new StringBuilder();
            sb.AppendLine("Kocky vs. vysavace - balance report (Bot)");
            sb.AppendLine("games per difficulty: " + games + ", final round " + Defs.FinalRound + ", wall time " + (watch.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s");
            int errors = 0;
            for (int d = 0; d < diffs.Length; d++)
            {
                int wins = 0, rounds = 0, money = 0, lives = 0, err = 0, withT5 = 0, blocks = 0;
                var cats = new double[Defs.TowerCount];
                var tiers = new double[Defs.TowerCount, Defs.PathCount];
                var t4 = new int[Defs.TowerCount, Defs.PathCount];
                var t5 = new int[Defs.TowerCount, Defs.PathCount];
                var lossRounds = new List<int>();
                var firstT4 = new List<int>();
                var firstT5 = new List<int>();
                var varGames = new int[plans];
                var varWins = new int[plans];
                for (int i = 0; i < games; i++)
                {
                    var r = res[d * games + i];
                    if (r.Won) wins++; else lossRounds.Add(r.Round);
                    if (r.Error != null) { err++; sb.AppendLine("  EXCEPTION " + diffs[d] + " game " + i + ": " + r.Error); }
                    rounds += r.Round; money += r.Money; lives += r.Lives; blocks += r.LegendBlocks;
                    if (r.Variant >= 0) { varGames[r.Variant]++; if (r.Won) varWins[r.Variant]++; }
                    if (r.FirstT4 > 0) firstT4.Add(r.FirstT4);
                    if (r.FirstT5 > 0) { firstT5.Add(r.FirstT5); if (r.FirstT5 <= Defs.FinalRound) withT5++; }
                    for (int k = 0; k < Defs.TowerCount; k++)
                    {
                        cats[k] += r.Cats[k];
                        for (int p = 0; p < Defs.PathCount; p++) { tiers[k, p] += r.Tiers[k, p]; t4[k, p] += r.T4[k, p]; t5[k, p] += r.T5[k, p]; }
                    }
                }
                errors += err;
                lossRounds.Sort();
                firstT4.Sort();
                firstT5.Sort();
                sb.AppendLine();
                sb.AppendLine(diffs[d] + ": wins " + wins + "/" + games + " (" + (100.0 * wins / games).ToString("0.0") + " %), exceptions " + err);
                sb.AppendLine("  avg round reached " + ((double)rounds / games).ToString("0.0") + ", avg money left " + money / games + ", avg lives left " + lives / games +
                    ", tier 5 refused by the legend rule: " + blocks + "x");
                sb.AppendLine("  losses at rounds: " + (lossRounds.Count == 0 ? "-" : string.Join(" ", lossRounds)));
                sb.AppendLine("  first tier 4 at round: median " + Median(firstT4) + ", all: " + (firstT4.Count == 0 ? "-" : string.Join(" ", firstT4)));
                sb.AppendLine("  first tier 5 at round: median " + Median(firstT5) + ", games with a T5 by round " + Defs.FinalRound + ": " + withT5 + "/" + games +
                    ", all: " + (firstT5.Count == 0 ? "-" : string.Join(" ", firstT5)));
                sb.Append("  avg lives lost per round (round:lives):");
                for (int rd = 1; rd <= Defs.FinalRound; rd++)
                {
                    double sum = 0;
                    for (int i = 0; i < games; i++) sum += res[d * games + i].LivesLost[rd];
                    if (sum > 0) sb.Append(" " + rd + ":" + (sum / games).ToString("0.0"));
                }
                sb.AppendLine();
                sb.Append("  build orders (games/wins):");
                for (int v = 0; v < plans; v++) sb.Append(" " + Bot.PlanName(v) + " " + varGames[v] + "/" + varWins[v]);
                sb.AppendLine();
                sb.AppendLine("  cats per game (avg count; per path: avg tier, T4 bought, T5 bought - totals over all games):");
                for (int k = 0; k < Defs.TowerCount; k++)
                {
                    sb.Append("    " + ((TowerKind)k).ToString().PadRight(12) + (cats[k] / games).ToString("0.0").PadLeft(5));
                    for (int p = 0; p < Defs.PathCount; p++)
                        sb.Append("   p" + p + " " + (tiers[k, p] / Math.Max(1, cats[k])).ToString("0.0") + " T4 " + t4[k, p].ToString().PadLeft(3) + " T5 " + t5[k, p].ToString().PadLeft(3));
                    sb.AppendLine();
                }
            }
            sb.AppendLine();
            sb.AppendLine("Free play (Medium, " + games + " games per mode, stopped after round " + FreeCap + ", wall time " + (watch2.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s)");
            for (int m = 0; m < FreeModes.Length; m++)
            {
                int stop = FreeModes[m], reached = 0, lostBy = 0, survived = 0, err = 0, blocks = 0;
                int limit = stop == int.MaxValue ? 0 : stop + (stop >= 70 ? 10 : 12);
                var ends = new List<int>();
                for (int i = 0; i < games; i++)
                {
                    var r = fp[m * games + i];
                    blocks += r.LegendBlocks;
                    if (r.Error != null) { err++; sb.AppendLine("  EXCEPTION free play game " + i + ": " + r.Error); }
                    if (!r.Won || (stop != int.MaxValue && r.Round < stop)) continue;   // lost the main game / lost before the stop
                    reached++;
                    if (!r.Lost) survived++;
                    else { ends.Add(r.Round); if (r.Round <= limit) lostBy++; }
                }
                errors += err;
                ends.Sort();
                sb.Append(stop == int.MaxValue ? "  bot keeps playing: games in free play " + reached
                    : "  bot stops spending at round " + stop + ": games reaching it " + reached + ", lost by round " + limit + ": " + lostBy +
                      " (" + (reached == 0 ? 0 : 100.0 * lostBy / reached).ToString("0.0") + " %)");
                sb.AppendLine(", median end " + Median(ends) + ", survived round " + FreeCap + ": " + survived + ", legend refusals " + blocks + "x, exceptions " + err);
                sb.AppendLine("    ends: " + (ends.Count == 0 ? "-" : string.Join(" ", ends)));
            }
            sb.AppendLine();
            sb.AppendLine("total exceptions: " + errors);
            File.WriteAllText(file, sb.ToString());
        }

        // free-play modes: the round from which the bot stops spending (MaxValue = never); games end after FreeCap rounds
        static readonly int[] FreeModes = { int.MaxValue, 50, 70 };
        const int FreeCap = 120;

        static string Median(List<int> sorted) { return sorted.Count == 0 ? "-" : sorted[sorted.Count / 2].ToString(); }

        // legend rule: at most one cat per kind holds tier 5 on a path (an exception counts in the report)
        static void CheckLegends(Game g)
        {
            var held = new HashSet<int>();
            foreach (var c in g.Cats)
                for (int p = 0; p < Defs.PathCount; p++)
                    if (c.Up[p] >= Defs.TierCount && !held.Add((int)c.Kind * 10 + p))
                        throw new Exception("legend rule broken: two " + c.Kind + " with tier 5 on path " + p + " after round " + g.Round);
        }

        // records tier-4 and tier-5 purchases (the bot only uses the public API, so watch the cats after it acts)
        static void Watch(Game g, Result r, Dictionary<Cat, int[]> seen)
        {
            int round = g.RoundActive ? g.Round : g.Round + 1;
            foreach (var c in g.Cats)
            {
                int[] s;
                if (!seen.TryGetValue(c, out s)) { s = new int[Defs.PathCount]; seen[c] = s; }
                for (int p = 0; p < Defs.PathCount; p++)
                {
                    if (c.Up[p] >= 4 && s[p] < 4) { r.T4[(int)c.Kind, p]++; if (r.FirstT4 == 0) r.FirstT4 = round; }
                    if (c.Up[p] >= 5 && s[p] < 5) { r.T5[(int)c.Kind, p]++; if (r.FirstT5 == 0) r.FirstT5 = round; }
                    s[p] = c.Up[p];
                }
            }
        }

        // plays until lost or round maxRound ended; the bot acts only while g.Round < stopAt (its last spend is before round stopAt)
        static Result Play(Difficulty diff, int i, int stopAt, int maxRound)
        {
            var r = new Result { Variant = -1 };
            var g = new Game(diff, 1000 + i * 7919);
            var rnd = new Random(77 + i * 131 + (int)diff * 17);
            var seen = new Dictionary<Cat, int[]>();
            try
            {
                while (!g.Lost && g.Round < maxRound)
                {
                    if (g.Round < stopAt) { Bot.Act(g, rnd); Watch(g, r, seen); }
                    int lives = g.Lives;
                    g.StartRound();
                    int steps = 0;
                    while (g.RoundActive)
                    {
                        g.Step(1 / 60.0);
                        if (++steps % 30 == 0 && g.Round < stopAt) { Bot.Act(g, rnd); Watch(g, r, seen); }
                        if (steps > 60 * 60 * 30) throw new Exception("round " + g.Round + " never ended");
                    }
                    if (g.Round <= Defs.FinalRound) r.LivesLost[g.Round] = Math.Max(0, lives - g.Lives);
                    CheckLegends(g);
                }
            }
            catch (Exception e) { r.Error = e.GetType().Name + ": " + e.Message; }
            r.LegendBlocks = Bot.LegendBlocksOf(g);
            r.Won = g.Won;
            r.Lost = g.Lost;
            r.Round = g.Round;
            r.Money = g.Money;
            r.Lives = g.Lives;
            r.Variant = Bot.VariantOf(g);
            foreach (var c in g.Cats)
            {
                r.Cats[(int)c.Kind]++;
                for (int p = 0; p < Defs.PathCount; p++) r.Tiers[(int)c.Kind, p] += c.Up[p];
            }
            return r;
        }
    }
}
