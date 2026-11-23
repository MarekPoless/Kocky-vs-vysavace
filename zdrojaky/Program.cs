using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Kocky
{
    public static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        public static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch (EntryPointNotFoundException) { }

            if (args.Length >= 2)
            {
                switch (args[0])
                {
                    case "--icon": File.WriteAllBytes(args[1], AppIcon.Ico()); return 0;
                    case "--shots": return Shots.Run(args[1]);
                    case "--sim": Sim.Run(int.Parse(args[1]), args.Length > 2 ? args[2] : "sim.txt"); return 0;
                    case "--audio": Audio.Export(args[1]); return 0;
                    case "--uitest":
                        Application.EnableVisualStyles();
                        var test = new MainForm(int.Parse(args[1]), args.Length > 2 ? args[2] : "uitest.txt");
                        Application.Run(test);
                        return test.ExitCode;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Report(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception);
            Audio.Start();
            Application.Run(new MainForm(0, null));
            Audio.Stop();
            return 0;
        }

        static bool reported;

        public static void Report(Exception ex)
        {
            if (reported) return;
            reported = true;
            string path = Path.Combine(Path.GetTempPath(), "kocky-chyba.txt");
            try { File.WriteAllText(path, DateTime.Now + Environment.NewLine + ex); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Audio.Stop();
            MessageBox.Show("Hra narazila na chybu a musí skončit.\n\n" + (ex != null ? ex.Message : "") + "\n\nPodrobnosti: " + path,
                "Kočky vs. vysavače", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.Exit(1);
        }
    }

    public class MainForm : Form
    {
        readonly Ui ui = new Ui();
        readonly Timer timer;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly UiDriver driver;
        readonly string report;
        readonly double timeScale = 1;
        double last;
        int frames;
        double drawTotal, drawMax;
        bool finished;
        public int ExitCode;

        const double TestLimit = 45 * 60;   // seconds of real time before the uitest gives up

        public MainForm(int testGames, string report)
        {
            this.report = report;
            Text = "Kočky vs. vysavače";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Kit.C(0x24160C);
            KeyPreview = true;
            var wa = Screen.PrimaryScreen.WorkingArea;
            float s = Math.Min(wa.Width * 0.9f / Ui.VW, wa.Height * 0.9f / Ui.VH);
            ClientSize = new Size((int)(Ui.VW * s), (int)(Ui.VH * s));
            MinimumSize = new Size(820, 560);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (ArgumentException) { }
            ui.OnQuit = Close;
            if (testGames > 0)
            {
                ui.Silent = true;
                timeScale = 3;
                driver = new UiDriver(ui, testGames);
            }
            else
            {
                ui.Persist = true;
                ui.Load();
            }
            timer = new Timer { Interval = 15 };
            timer.Tick += (o, e) => OnTick();
            timer.Start();
        }

        void OnTick()
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = Math.Min(0.1, now - last);
            last = now;
            if (driver == null)
            {
                ui.Tick(dt * timeScale);
            }
            else
            {
                try
                {
                    // the driver first, so it sees the state that was just painted
                    string err = driver.Tick();
                    if (err != null) { Finish("CHYBA: " + err, 1); return; }
                    ui.Tick(dt * timeScale);
                    if (driver.Done) { Finish("OK", 0); return; }
                    if (now > TestLimit) { Finish("CHYBA: casovy limit " + TestLimit + " s vyprsel", 1); return; }
                }
                catch (Exception ex) { Finish("CHYBA: " + ex, 1); return; }
            }
            if (WindowState != FormWindowState.Minimized) Invalidate();
        }

        void Finish(string status, int code)
        {
            if (finished) return;
            finished = true;
            timer.Stop();
            ExitCode = code;
            WriteReport(status);
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (finished) return;
            long t0 = Stopwatch.GetTimestamp();
            if (driver == null) ui.Render(e.Graphics, ClientSize.Width, ClientSize.Height);
            else
            {
                try { ui.Render(e.Graphics, ClientSize.Width, ClientSize.Height); }
                catch (Exception ex) { Finish("CHYBA pri kresleni: " + ex, 1); return; }
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            frames++;
            if (frames > 30) { drawTotal += ms; drawMax = Math.Max(drawMax, ms); }
        }

        void WriteReport(string status)
        {
            if (report == null) return;
            double secs = clock.Elapsed.TotalSeconds;
            int counted = Math.Max(1, frames - 30);
            File.WriteAllText(report, status + Environment.NewLine +
                "akce: kliknuti " + driver.Clicks + ", klavesy " + driver.KeyPresses + Environment.NewLine +
                "her dohrano: " + driver.GamesDone + ", kol odehrano: " + driver.Rounds + ", kocek polozeno: " + driver.Placed + ", vylepseni: " + driver.Upgrades +
                " (cesty 1/2/3: " + driver.PathUpgrades[0] + "/" + driver.PathUpgrades[1] + "/" + driver.PathUpgrades[2] + ")" +
                ", strom vylepseni otevren a zavren: " + driver.TreeVisits + "x" + Environment.NewLine +
                "snimku: " + frames + ", cas behu: " + secs.ToString("0.0") + " s, fps: " + (frames / Math.Max(0.001, secs)).ToString("0") + Environment.NewLine +
                "vykresleni snimku: prumer " + (drawTotal / counted).ToString("0.00") + " ms, max " + drawMax.ToString("0.00") + " ms" + Environment.NewLine +
                "okno: " + ClientSize.Width + "x" + ClientSize.Height + Environment.NewLine);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            ui.MouseMove(ui.ToVirtual(e.Location));
            Cursor = ui.WantsHand ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            ui.MouseMove(new PointF(-999, -999));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            ui.MouseMove(ui.ToVirtual(e.Location));
            ui.MouseDown(e.Button == MouseButtons.Right, (ModifierKeys & Keys.Shift) != 0);
            Cursor = ui.WantsHand ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            ui.Key(e.KeyCode);
            e.Handled = true;
        }

        protected override bool IsInputKey(Keys keyData) { return true; }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Audio.Hidden = WindowState == FormWindowState.Minimized;
        }
    }

    /// <summary>uitest: plays Medium games through the same mouse and key handlers the window uses.</summary>
    public class UiDriver
    {
        readonly Ui u;
        readonly int games;
        readonly Random rng = new Random(5);
        readonly Queue<Func<string>> steps = new Queue<Func<string>>();
        int wait, stuck, seenRound, plan, lastFrame = -1;
        double lastTime = -1;
        Game seen, counted;
        bool pausedOnce, rulesOnce, autoOnce, treeOnce;
        public int Clicks, KeyPresses, GamesDone, Rounds, Placed, Upgrades, TreeVisits;
        public readonly int[] PathUpgrades = new int[Defs.PathCount];
        readonly int[] started = new int[Defs.PathCount];
        public bool Done;

        static readonly TowerKind[] Plan =
        {
            TowerKind.Drapka, TowerKind.Chlupac, TowerKind.Drapka, TowerKind.Klubickarka, TowerKind.Chlupac, TowerKind.Spac,
            TowerKind.Laserovka, TowerKind.Klubickarka, TowerKind.Pamlskarna, TowerKind.Chlupac, TowerKind.Laserovka, TowerKind.Drapka
        };

        public UiDriver(Ui u, int games) { this.u = u; this.games = games; }

        /// <summary>One decision per few frames; returns an error text when the game did not react as expected.</summary>
        public string Tick()
        {
            if (u.Frames == lastFrame) return null;
            lastFrame = u.Frames;
            if (wait-- > 0) return null;
            // a loss (or win) in the middle of a click sequence makes the rest of it meaningless
            if (steps.Count > 0 && (u.ShowLost || u.ShowWon)) steps.Clear();
            if (steps.Count > 0) return steps.Dequeue()();
            var g = u.G;
            if (g != null && g != seen) { seen = g; seenRound = 0; lastTime = -1; stuck = 0; plan = 0; pausedOnce = autoOnce = treeOnce = false; }
            if (g != null && g.Round > seenRound) { Rounds += g.Round - seenRound; seenRound = g.Round; }

            if (u.RulesOpen) return Click("rules-close");
            if (u.Screen == UiScreen.Title)
            {
                if (GamesDone >= games) { Done = true; return null; }
                return Click("play");
            }
            if (u.Screen == UiScreen.Difficulty) return Click("diff1");
            if (g == null) return "herni obrazovka bez hry";
            if (u.ShowLost || u.ShowWon)
            {
                if (counted != g) { counted = g; GamesDone++; }
                // between games alternate "again" and the menu route; the last game ends through the menu
                return Click(u.ShowLost && GamesDone < games && GamesDone % 2 == 1 ? "again" : "menu");
            }
            if (u.Paused)
            {
                if (!rulesOnce)
                {
                    rulesOnce = true;
                    steps.Enqueue(() => u.RulesOpen ? Click("rules-close") : "pravidla se z pauzy neotevrela");
                    return Click("rules");
                }
                return Click("resume");
            }
            if (g.RoundActive)
            {
                if (g.Time == lastTime) { if (++stuck > 900) return "simulace stoji v kole " + g.Round; }
                else stuck = 0;
                lastTime = g.Time;
            }
            if (u.TreeOpen || u.Sel != null || u.PlaceKind >= 0) return Key(Keys.Escape);
            if (g.Round == 2 && !pausedOnce) { pausedOnce = true; return Key(Keys.P); }
            if (!autoOnce && g.Round >= 3) { autoOnce = true; return Click("auto"); }
            if (!treeOnce && g.Round >= 4 && g.Cats.Count > 0) { treeOnce = true; VisitTree(g); return null; }

            var kind = Plan[plan % Plan.Length];
            int price = g.PriceOf(kind);
            bool wantBuy = g.Cats.Count < 3 + g.Round / 4;
            if (wantBuy && g.Money >= price)
            {
                var spot = Ui.FindSpot(g, kind, rng);
                if (spot != null) { Buy(g, kind, spot.Value); return null; }
                plan++;   // no room for this one, try the next kind later
            }
            else if (!wantBuy)
            {
                Cat best = null;
                int bestPath = 0, bestPrice = int.MaxValue;
                foreach (var c in g.Cats)
                {
                    int path = PathFor(c);
                    int p = path < 0 ? -1 : g.UpgradePrice(c, path);
                    if (p >= 0 && p <= g.Money && p < bestPrice) { best = c; bestPath = path; bestPrice = p; }
                }
                if (best != null) { Upgrade(best, bestPath); return null; }
            }
            if (!g.RoundActive && !g.AutoStart) return Click("go");
            if (g.RoundActive && u.Speed < 3) return Click("go");
            return null;
        }

        void Buy(Game g, TowerKind kind, PointF spot)
        {
            int i = (int)kind, before = g.Cats.Count;
            bool byKey = plan % 3 == 2;
            plan++;
            steps.Enqueue(() => byKey ? Key((Keys)((int)Keys.D1 + i)) : Click("shop" + i));
            steps.Enqueue(() => u.PlaceKind != i ? "obchod nevybral kocku " + kind : ClickAt(spot));
            steps.Enqueue(() =>
            {
                if (g.Cats.Count != before + 1) return "kocka " + kind + " se klikem nepolozila na " + spot;
                Placed++;
                return null;
            });
        }

        /// <summary>The path the crosspath rule still allows that this test has bought least so far (so all three get clicked).</summary>
        int PathFor(Cat c)
        {
            int best = -1;
            for (int i = 0; i < Defs.PathCount; i++)
            {
                int p = (c.Id + i) % Defs.PathCount;
                if (Defs.CanRaise(c.Up, p) && (best < 0 || started[p] < started[best])) best = p;
            }
            return best;
        }

        static readonly Keys[] PathKeys = { Keys.Z, Keys.X, Keys.C }, AltKeys = { Keys.Oemcomma, Keys.OemPeriod, Keys.OemMinus };

        void Upgrade(Cat c, int path)
        {
            int before = c.Up[path], n = started[path]++;
            // per path: the first upgrade by Z / X / C, the second by its row button, later ones mostly by the row
            Keys key = n == 0 || rng.Next(2) == 0 ? PathKeys[path] : AltKeys[path];
            bool byKey = n == 0 || (n > 1 && rng.Next(4) == 0);
            steps.Enqueue(() => ClickAt(new PointF(c.X, c.Y)));
            steps.Enqueue(() => u.Sel != c ? "klik na kocku ji nevybral" : byKey ? Key(key) : Click("up" + path));
            steps.Enqueue(() =>
            {
                if (c.Up[path] != before + 1) return "vylepseni cesty " + (path + 1) + " se neprovedlo";
                Upgrades++;
                PathUpgrades[path]++;
                return rng.Next(4) == 0 ? Key(Keys.Tab) : null;
            });
            // back to the shop by Esc or by the button
            steps.Enqueue(() => rng.Next(2) == 0 ? Key(Keys.Escape) : Click("desel"));
        }

        /// <summary>Opens the upgrade tree (from the shop "i" badge, or for a selected cat by button or key I), switches a tab or clicks a tile,
        /// checks the game stands still while it is open, closes it and checks Esc closed only the tree.</summary>
        void VisitTree(Game g)
        {
            bool fromShop = GamesDone % 2 == 0;
            var cat = g.Cats[rng.Next(g.Cats.Count)];
            int kind = rng.Next(Defs.TowerCount), next = (kind + 1) % Defs.TowerCount;
            string tile = "tile" + rng.Next(Defs.PathCount) + (1 + rng.Next(Defs.TierCount));
            double t0 = 0;
            if (fromShop)
            {
                steps.Enqueue(() => Click("tree" + kind));
                steps.Enqueue(() => u.TreeKind != kind ? "strom se z obchodu neotevrel" : Key(Keys.Right));
                steps.Enqueue(() => { if (u.TreeKind != next) return "sipka neprepnula kocku ve stromu"; t0 = g.Time; return Click(tile); });
            }
            else
            {
                bool byKey = rng.Next(2) == 0;
                steps.Enqueue(() => ClickAt(new PointF(cat.X, cat.Y)));
                steps.Enqueue(() => u.Sel != cat ? "klik na kocku ji nevybral" : byKey ? Key(Keys.I) : Click("tree"));
                steps.Enqueue(() => { if (u.TreeKind != (int)cat.Kind) return "strom se pro vybranou kocku neotevrel"; t0 = g.Time; return Click(tile); });
            }
            steps.Enqueue(() =>
            {
                if (g.Time != t0) return "hra bezi, i kdyz je strom otevreny";
                TreeVisits++;
                return fromShop ? Click("tree-close") : Key(Keys.Escape);
            });
            steps.Enqueue(() =>
            {
                if (u.TreeOpen) return "strom se nezavrel";
                if (fromShop) return null;
                return u.Sel != cat ? "Esc nezavrel jen strom" : Key(Keys.Escape);
            });
        }

        string ClickAt(PointF p)
        {
            u.MouseMove(p);
            u.MouseDown(false, false);
            Clicks++;
            wait = 3;
            return null;
        }

        string Click(string id)
        {
            var r = u.ButtonRect(id);
            if (r == null) return "chybi tlacitko " + id;
            return ClickAt(new PointF(r.Value.X + r.Value.Width / 2, r.Value.Y + r.Value.Height / 2));
        }

        string Key(Keys k)
        {
            u.Key(k);
            KeyPresses++;
            wait = 3;
            return null;
        }
    }

    /// <summary>Renders the key screens to 1280×800 PNGs so the look can be checked without playing.</summary>
    public static class Shots
    {
        public static int Run(string dir)
        {
            Directory.CreateDirectory(dir);
            var u = new Ui { Silent = true };
            u.Now = 3.2;
            Save(u, dir, "00-title");
            u.Screen = UiScreen.Difficulty;
            u.MouseMove(new PointF(640, 400));
            Save(u, dir, "01-difficulty");

            var rnd = new Random(3);
            u.NewGame(Difficulty.Medium, 7);
            var g = u.G;
            Place(g, TowerKind.Drapka, rnd);
            Place(g, TowerKind.Chlupac, rnd);
            g.StartRound();
            Run(u, 400);
            Place(g, TowerKind.Drapka, rnd);
            if (!g.RoundActive) g.StartRound();
            Run(u, 9);
            u.MouseMove(new PointF(1196, 202));
            Save(u, dir, "02-early");

            g.Money = Math.Max(g.Money, 900);
            u.PlaceKind = (int)TowerKind.Klubickarka;
            var spot = Ui.FindSpot(g, TowerKind.Klubickarka, rnd);
            u.MouseMove(spot ?? new PointF(300, 400));
            Save(u, dir, "03-place-ok");
            u.MouseMove(new PointF(214, 420));
            Save(u, dir, "04-place-bad");

            // a spitter one tier up the first path; the range row hovered, the second path's next tier too dear
            u.PlaceKind = -1;
            var spit = g.Cats.Find(x => x.Kind == TowerKind.Chlupac) ?? g.Cats[0];
            g.Money += 5000;
            g.Upgrade(spit, 0);
            g.Money = 200;
            u.Sel = spit;
            Hover(u, "up1");
            Save(u, dir, "05-upgrades");

            var claw = g.Cats.Find(x => x.Kind == TowerKind.Drapka) ?? g.Cats[0];
            g.Money = 1000000;
            Build(g, claw, new[] { 5, 2, 0 });
            g.Money = 3000;
            u.Sel = claw;
            Hover(u, "up2");
            Save(u, dir, "13-locked");

            g.Money = 1000000;
            var snipe = g.Place(TowerKind.Chlupac, 330, 210) ?? Place(g, TowerKind.Chlupac, rnd);
            if (snipe != null) Build(g, snipe, new[] { 0, 2, 3 });
            g.Money = 1200;
            u.Sel = snipe;
            u.MouseMove(new PointF(claw.X, claw.Y));
            Save(u, dir, "14-hover-range");
            u.Sel = null;

            // a second claw at 2-0-1 next to the 5-2-0 one: bought, next, later, crosspath-locked and legend-taken tiles
            g.Money = 1000000;
            var claw2 = Place(g, TowerKind.Drapka, new Random(9));
            if (claw2 != null)
            {
                Build(g, claw2, new[] { 2, 0, 1 });
                g.Money = 1500;
                u.Sel = claw2;
                u.Key(Keys.I);
                Hover(u, "tile03");
                Save(u, dir, "15-tree");
                u.Key(Keys.Escape);

                // the same claw at 4-0-1: its first path's tier 5 is taken by the other claw
                g.Money = 1000000;
                Build(g, claw2, new[] { 2, 0, 0 });
                g.Money = 1500;
                Hover(u, "up0");
                Save(u, dir, "17-legend-taken");
                u.Sel = null;
            }

            // planning from the shop for a kind that is not on the floor
            u.MouseMove(new PointF(-999, -999));
            Hover(u, "tree" + (int)TowerKind.Laserovka);
            u.MouseDown(false, false);
            Hover(u, "tile23");
            Save(u, dir, "16-tree-shop");
            u.Key(Keys.Escape);

            g.Lives = 0;
            g.Lost = true;
            u.Watch();
            u.MouseMove(new PointF(-999, -999));
            Save(u, dir, "08-lost");

            // every cat with a tier-5 path, in the middle of a busy round
            u.NewGame(Difficulty.Medium, 21);
            g = u.G;
            g.Money = 10000000;
            var legends = new[] { new[] { 0, 5, 2 }, new[] { 0, 2, 5 }, new[] { 2, 0, 5 }, new[] { 0, 2, 5 }, new[] { 5, 0, 2 }, new[] { 2, 0, 5 } };
            for (int k = 0; k < Defs.TowerCount; k++)
            {
                var cat = Place(g, (TowerKind)k, rnd);
                if (cat != null) Build(g, cat, legends[k]);
            }
            g.Money = 4800;
            g.Lives = 1000;
            g.Round = 26;
            g.StartRound();
            Run(u, 16);
            Save(u, dir, "12-legends");

            // free play round 64: reinforced Keramik (tier 2) and bosses (tier 3) getting cracked on the runner
            u.NewGame(Difficulty.Medium, 23);
            g = u.G;
            g.Money = 10000000;
            // damage dealers mid-runner, nothing that freezes or slows, so the vacuums stay spread and untinted
            var tk = new[] { TowerKind.Laserovka, TowerKind.Laserovka, TowerKind.Drapka, TowerKind.Chlupac, TowerKind.Laserovka, TowerKind.Laserovka };
            var tu = new[] { new[] { 0, 2, 5 }, new[] { 4, 0, 2 }, new[] { 3, 0, 2 }, new[] { 0, 4, 2 }, new[] { 0, 2, 4 }, new[] { 4, 2, 0 } };
            var near = new[] { new PointF(560, 330), new PointF(370, 470), new PointF(760, 470), new PointF(560, 600), new PointF(300, 250), new PointF(300, 560) };
            for (int i = 0; i < tk.Length; i++)
            {
                var cat = PlaceNear(g, tk[i], near[i]);
                if (cat != null) Build(g, cat, tu[i]);
            }
            g.Money = 2400;
            g.Lives = 100000;
            g.Round = 63;
            g.StartRound();
            for (int q = 0; q < 100 && g.RoundActive && !g.Vacs.Exists(v => v.Kind == VacKind.Prumyslak && v.HpFrac < 0.5f); q++) Run(u, 0.25);
            Save(u, dir, "18-tough");

            // a long game played by the bot gives the late, boss and stealth scenes
            u.NewGame(Difficulty.Easy, 11);
            g = u.G;
            var brnd = new Random(11);
            bool got06 = false, got07 = false, got11 = false;
            for (int sec = 0; sec < 6000 && !g.Lost && g.Round <= 34 && !(got06 && got07 && got11); sec++)
            {
                Bot.Act(g, brnd);
                // shots only: keep the bot's game alive so the late scenes always exist
                if (g.Lives < 100) g.Lives = Defs.StartLives(g.Diff);
                if (!g.RoundActive && !g.StartRound()) break;
                Run(u, 1);
                bool stealth = false, armored = false, boss = false;
                foreach (var v in g.Vacs)
                {
                    if (v.Stealth) stealth = true;
                    if (Defs.Vac(v.Kind).Armored) armored = true;
                    if (Defs.Vac(v.Kind).Boss && v.Dist > 260) boss = true;
                }
                if (!got11 && stealth && armored) { got11 = true; Save(u, dir, "11-stealth-armored"); }
                if (!got06 && g.Round >= 26 && g.Vacs.Count >= 25) { got06 = true; Save(u, dir, "06-late"); }
                if (!got07 && boss) { got07 = true; Save(u, dir, "07-boss"); }
            }
            if (!got11) Save(u, dir, "11-stealth-armored");
            if (!got06) Save(u, dir, "06-late");
            if (!got07) Save(u, dir, "07-boss");

            g.Vacs.Clear();
            g.Shots.Clear();
            g.Lost = false;
            g.RoundActive = false;
            g.Round = Math.Max(g.Round, Defs.FinalRound);
            g.Won = true;
            u.Watch();
            Save(u, dir, "09-won");

            u.ToMenu();
            u.RulesOpen = true;
            Save(u, dir, "10-rules");
            u.RulesOpen = false;

            // a yarn cat at 3-0-0 buys tier 4 (four stat gains) by clicking its row, so the next tier's tooltip is up too; the float a moment later
            u.NewGame(Difficulty.Medium, 31);
            g = u.G;
            u.Now += 3;   // past the start toast
            g.Money = 1000000;
            var ball = PlaceNear(g, TowerKind.Klubickarka, new PointF(560, 330));
            if (ball != null)
            {
                Build(g, ball, new[] { 3, 0, 0 });
                g.Money = 9000;
                u.Sel = ball;
                Hover(u, "up0");
                u.MouseDown(false, false);
                u.Now += 0.4;
                Save(u, dir, "19-upgrade-bought");
            }

            Panels(u, dir);
            return 0;
        }

        /// <summary>Contact sheet of the selected-cat panel: every kind (columns) in four states (rows).</summary>
        static void Panels(Ui u, string dir)
        {
            u.NewGame(Difficulty.Medium, 41);
            var g = u.G;
            u.Now += 3;
            u.MouseMove(new PointF(-999, -999));
            var rnd = new Random(5);
            string[] states = { "0-0-0", "2-1-0, něco nedosáhne", "4-2-0, zamčeno + obsazená", "1-0-2, bez pamlsků" };
            int[][] ups = { new[] { 0, 0, 0 }, new[] { 2, 1, 0 }, new[] { 4, 2, 0 }, new[] { 1, 0, 2 } };
            int[] money = { 5000, 700, 3000, 0 };
            const int pw = 304, ph = 500, gap = 12, head = 26;
            var crop = new Rectangle(968, 108, pw, ph);
            using (var sheet = new Bitmap(gap + Defs.TowerCount * (pw + gap), gap + states.Length * (ph + head + gap), PixelFormat.Format32bppArgb))
            using (var sg = Graphics.FromImage(sheet))
            using (var frame = new Bitmap(1280, 800, PixelFormat.Format32bppArgb))
            {
                sg.Clear(Kit.C(0x24160C));
                sg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                for (int k = 0; k < Defs.TowerCount; k++)
                {
                    var kind = (TowerKind)k;
                    g.Money = 100000000;
                    // holds the first path's legend, so the 4-2-0 cat shows "Obsazená"
                    var owner = Place(g, kind, rnd);
                    if (owner != null) Build(g, owner, new[] { 5, 0, 0 });
                    for (int s = 0; s < states.Length; s++)
                    {
                        g.Money = 100000000;
                        var c = Place(g, kind, rnd);
                        if (c == null) continue;
                        Build(g, c, ups[s]);
                        g.Money = money[s];
                        u.Sel = c;
                        using (var fg = Graphics.FromImage(frame)) u.Render(fg, 1280, 800);
                        float x = gap + k * (pw + gap), y = gap + s * (ph + head + gap);
                        Kit.Text(sg, Defs.Tower(kind).Name + " · " + states[s], Kit.Bold(15), Kit.Cream, x + 2, y + 2);
                        sg.DrawImage(frame, new RectangleF(x, y + head, pw, ph), crop, GraphicsUnit.Pixel);
                        u.Sel = null;
                        g.Sell(c);
                    }
                    if (owner != null) g.Sell(owner);
                }
                sheet.Save(Path.Combine(dir, "20-panels.png"), ImageFormat.Png);
            }
        }

        static Cat Place(Game g, TowerKind k, Random rnd)
        {
            var p = Ui.FindSpot(g, k, rnd);
            return p == null ? null : g.Place(k, p.Value.X, p.Value.Y);
        }

        /// <summary>Places a cat on the nearest free spot around p (rings of 6 px), or null.</summary>
        static Cat PlaceNear(Game g, TowerKind k, PointF p)
        {
            for (int rad = 0; rad < 240; rad += 6)
                for (int a = 0; a < 16; a++)
                {
                    float x = p.X + rad * (float)Math.Cos(a * Math.PI / 8), y = p.Y + rad * (float)Math.Sin(a * Math.PI / 8);
                    if (g.CanPlace(k, x, y)) return g.Place(k, x, y);
                }
            return null;
        }

        /// <summary>Buys the given tiers path by path.</summary>
        static void Build(Game g, Cat c, int[] up)
        {
            for (int p = 0; p < up.Length; p++)
                for (int i = 0; i < up[p]; i++) g.Upgrade(c, p);
        }

        /// <summary>Lays the buttons out once, then puts the mouse on the given one.</summary>
        static void Hover(Ui u, string id)
        {
            using (var bmp = new Bitmap(1280, 800))
            using (var g = Graphics.FromImage(bmp)) u.Render(g, 1280, 800);
            var r = u.ButtonRect(id);
            if (r != null) u.MouseMove(new PointF(r.Value.X + r.Value.Width / 2, r.Value.Y + r.Value.Height / 2));
        }

        /// <summary>Steps the game for the given simulated seconds (stops early when a round ends).</summary>
        static void Run(Ui u, double seconds)
        {
            var g = u.G;
            for (int i = 0; i < seconds * 60 && !g.Lost; i++)
            {
                bool active = g.RoundActive;
                g.Step(1.0 / 60);
                u.Now += 1.0 / 60;
                u.Watch();
                if (active && !g.RoundActive) break;
            }
        }

        static void Save(Ui u, string dir, string name)
        {
            using (var bmp = new Bitmap(1280, 800, PixelFormat.Format32bppArgb))
            {
                // the first pass lays out the buttons, the second draws the hover state
                using (var g = Graphics.FromImage(bmp)) u.Render(g, 1280, 800);
                using (var g = Graphics.FromImage(bmp)) u.Render(g, 1280, 800);
                bmp.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
            }
        }
    }

    /// <summary>Multi-size .ico from Art.AppIcon (DIB frames for small sizes, PNG for 256 px).</summary>
    public static class AppIcon
    {
        static Bitmap Draw(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                Art.AppIcon(g, size);
            }
            return bmp;
        }

        public static byte[] Ico()
        {
            int[] sizes = { 16, 32, 48, 256 };
            var frames = new List<byte[]>();
            foreach (int s in sizes)
                using (var bmp = Draw(s)) frames.Add(s == 256 ? Png(bmp) : Dib(bmp));
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(frames[i].Length); w.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var f in frames) w.Write(f);
            w.Flush();
            return ms.ToArray();
        }

        static byte[] Png(Bitmap bmp)
        {
            using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
        }

        static byte[] Dib(Bitmap bmp)
        {
            int n = bmp.Width;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(40); w.Write(n); w.Write(n * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = n - 1; y >= 0; y--)
                for (int x = 0; x < n; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                }
            int maskRow = ((n + 31) / 32) * 4;
            w.Write(new byte[maskRow * n]);
            w.Flush();
            return ms.ToArray();
        }
    }
}
