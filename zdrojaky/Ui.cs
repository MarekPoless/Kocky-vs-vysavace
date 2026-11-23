using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SA = System.Drawing.StringAlignment;

namespace Kocky
{
    public enum UiScreen { Title, Difficulty, Game }

    /// <summary>Screens, side panel, HUD, overlays, input and settings. The form only forwards events, ticks and paints.</summary>
    public class Ui
    {
        public const float VW = 1280, VH = 800;
        const double StepDt = 1.0 / 60;

        static readonly Color Wood = Kit.C(0x7A4A28), WoodDark = Kit.C(0x4E2D16), WoodLight = Kit.C(0x8F5B34),
            Paper = Kit.Cream, Paper2 = Kit.C(0xF3E7CF), PaperDim = Kit.C(0xE2D5BD), Edge = Kit.C(0xD8C29E),
            Ink = Kit.Ink, Sub = Kit.C(0x7D6650), Orange = Kit.C(0xE8590C), Green = Kit.C(0x2F9E44), Red = Kit.C(0xC92A2A),
            Gold = Kit.Amber, Night = Kit.C(0x24160C), LegendC = Kit.C(0xB07A00);

        public Game G;
        public UiScreen Screen = UiScreen.Title;
        public bool RulesOpen, Paused;
        public bool Persist;   // read/write the settings file (off for tests and shots)
        public bool Silent;    // no sounds at all (tests and shots)
        public int Speed = 1;
        public int PlaceKind = -1;
        public Cat Sel;
        public PointF Mouse = new PointF(-999, -999);
        public double Now;
        public readonly int[] Best = new int[3];
        public int Wins;
        public Action OnQuit;
        public float Scale = 1, OffX, OffY;
        public string HoverBtn;
        public int Frames;     // renders so far (the test driver acts only on a painted state)

        bool wonDismissed, lostSeen, wonSeen, lastActive, bossSeen, record;
        int lastRound;
        double acc;
        string toast;
        double toastAt = -99;
        string armed;
        double armedUntil;
        int hoverShop = -1, hoverUp = -1;
        Cat hoverCat;
        RectangleF tipRect;          // the upgrade tooltip last drawn (the fit harness reads it)

        // purchase float: the bought tier's name and gains rising from the cat, in real UI time
        const double FloatLife = 1.5;
        string floatName;
        List<Gain> floatGains;
        float floatX, floatY;
        double floatAt = -99;
        static readonly int[] NoUp = new int[Defs.PathCount];
        static readonly string[] UpKeys = { "Z", "X", "C" };

        public int TreeKind = -1;   // upgrade tree open for this cat kind; -1 = closed
        Cat treeCat;                // cat whose bought / next / blocked tiers the tree shows (null: planning from the shop)
        int treePath, treeTier = 1; // the clicked tile
        public bool TreeOpen { get { return TreeKind >= 0; } }

        class Btn { public string Id; public RectangleF R; public int Layer; }
        readonly List<Btn> buttons = new List<Btn>();
        int layer, topLayer;

        public bool ShowLost { get { return Screen == UiScreen.Game && G != null && G.Lost; } }
        public bool ShowWon { get { return Screen == UiScreen.Game && G != null && G.Won && !wonDismissed && !G.Lost; } }
        // the open tree stops the game without touching Paused, so closing it returns to the earlier pause state
        public bool Modal { get { return RulesOpen || Paused || ShowLost || ShowWon || TreeOpen; } }
        public bool WantsHand { get { return HoverBtn != null || hoverCat != null; } }
        int Survived { get { return G == null ? 0 : G.RoundsSurvived; } }

        // ---------- game flow ----------

        public void NewGame(Difficulty d) { NewGame(d, Environment.TickCount); }

        public void NewGame(Difficulty d, int seed)
        {
            G = new Game(d, seed);
            if (!Silent) G.Sound = Audio.Play;
            Screen = UiScreen.Game;
            Paused = RulesOpen = false;
            TreeKind = -1;
            Speed = 1;
            PlaceKind = -1;
            Sel = null;
            acc = 0;
            wonDismissed = lostSeen = wonSeen = lastActive = bossSeen = record = false;
            lastRound = 0;
            armed = null;
            floatName = null;
            Toast("Polož první kočky a pusť vysavače!");
        }

        public void ToMenu()
        {
            Screen = UiScreen.Title;
            G = null;
            Sel = null;
            PlaceKind = -1;
            Paused = false;
            TreeKind = -1;
            armed = null;
        }

        /// <summary>Advances the simulation in fixed steps; dt is real seconds.</summary>
        public void Tick(double dt)
        {
            Now += dt;
            if (Screen != UiScreen.Game || G == null) return;
            if (Modal) acc = 0;
            else
            {
                acc += dt * Speed;
                int n = 0;
                while (acc >= StepDt && n < 60) { G.Step(StepDt); acc -= StepDt; n++; }
                // shortcut: a machine too slow to keep up drops time instead of spiralling
                if (n >= 60) acc = 0;
            }
            Watch();
        }

        /// <summary>Notices round starts and ends, bosses, loss and win; call after stepping the game.</summary>
        public void Watch()
        {
            if (G == null) return;
            if (Sel != null && !G.Cats.Contains(Sel)) Sel = null;
            if (G.Round != lastRound)
            {
                lastRound = G.Round;
                if (G.RoundActive) Toast("Kolo " + G.Round);
                NoteBest();
            }
            if (lastActive && !G.RoundActive && !G.Lost)
            {
                Toast("Kolo " + G.Round + " zvládnuto!");
                NoteBest();
            }
            lastActive = G.RoundActive;
            bool boss = false;
            foreach (var v in G.Vacs) if (Defs.Vac(v.Kind).Boss) { boss = true; break; }
            if (boss && !bossSeen) Toast("Pozor, obří vysavač!");
            bossSeen = boss;
            if (G.Lost && !lostSeen)
            {
                lostSeen = true;
                PlaceKind = -1; Sel = null; Paused = false;
                NoteBest();
            }
            if (G.Won && !wonSeen)
            {
                wonSeen = true;
                PlaceKind = -1; Sel = null; Paused = false;
                Wins++;
                NoteBest();
                Save();
            }
        }

        void NoteBest()
        {
            int d = (int)G.Diff, s = Survived;
            if (s > Best[d]) { Best[d] = s; record = true; Save(); }
        }

        void Toast(string s) { toast = s; toastAt = Now; }

        void Sfx(string name) { if (!Silent) Audio.Play(name); }

        bool Confirm(string id)
        {
            if (armed == id && Now < armedUntil) { armed = null; return true; }
            armed = id;
            armedUntil = Now + 3;
            return false;
        }

        bool Armed(string id) { return armed == id && Now < armedUntil; }

        static string Plural(int n, string one, string few, string many)
        {
            int a = Math.Abs(n);
            return n + " " + (a == 1 ? one : a >= 2 && a <= 4 ? few : many);
        }

        static string Treats(int n) { return Plural(n, "pamlsek", "pamlsky", "pamlsků"); }

        void Shop(int i)
        {
            if (G == null) return;
            Sel = null;
            if (PlaceKind == i) { PlaceKind = -1; return; }
            int price = G.PriceOf((TowerKind)i);
            if (G.Money < price) { Toast("Chybí ti " + Treats(price - G.Money) + "."); return; }
            PlaceKind = i;
        }

        void TryPlace(bool shift)
        {
            var k = (TowerKind)PlaceKind;
            int price = G.PriceOf(k);
            if (G.Money < price) { Toast("Chybí ti " + Treats(price - G.Money) + "."); PlaceKind = -1; return; }
            if (!G.CanPlace(k, Mouse.X, Mouse.Y)) { Toast("Sem kočku položit nejde."); return; }
            if (G.Place(k, Mouse.X, Mouse.Y) == null) return;
            if (!shift || G.Money < G.PriceOf(k)) PlaceKind = -1;
        }

        void Upgrade(int path)
        {
            if (Sel == null) return;
            int p = G.UpgradePrice(Sel, path);
            if (p < 0)
            {
                var block = G.UpgradeBlock(Sel, path);
                Toast(block == UpBlock.Maxed ? "Tahle cesta je už hotová." : block == UpBlock.LegendTaken ? LegendText(Sel.Kind)
                    : "Zamčeno: nejvýš dvě cesty, jen jedna nad 2. stupeň.");
                return;
            }
            if (G.Money < p) { Toast("Chybí ti " + Treats(p - G.Money) + "."); return; }
            string name = Defs.Tower(Sel.Kind).Paths[path][Sel.Up[path]].Name;
            var preview = G.Preview(Sel, path);
            var gains = preview != null ? Gains(Sel, preview) : new List<Gain>();
            if (!G.Upgrade(Sel, path)) return;
            // a quick second purchase replaces the float, so two never overlap
            floatName = name; floatGains = gains; floatX = Sel.X; floatY = Sel.Y; floatAt = Now;
        }

        static bool Aims(TowerKind k) { return k != TowerKind.Spac && k != TowerKind.Pamlskarna; }

        // grammatical gender of each kind's name (TowerKind order) for "jiná Drápka" / "jiný Chlupáč"
        static readonly string[] Another = { "jiná", "jiný", "jiná", "jiná", "jiný", "jiná" };

        static string LegendText(TowerKind k) { return "Legendu už má " + Another[(int)k] + " " + Defs.Tower(k).Name + ": jen jedna na každou cestu."; }

        const string CrossText = "Zamčeno: vylepšovat jde nejvýš dvě cesty, jen jednu nad 2. stupeň.";

        void OpenTree(int kind)
        {
            if (G == null || kind < 0 || kind >= Defs.TowerCount) return;
            treeCat = Sel != null && (int)Sel.Kind == kind ? Sel : null;
            PlaceKind = -1;
            armed = null;
            TreeKind = kind;
            PickTile();
        }

        void TreeTab(int kind)
        {
            TreeKind = (kind % Defs.TowerCount + Defs.TowerCount) % Defs.TowerCount;
            PickTile();
        }

        /// <summary>The selected cat while its own kind's tab is shown; null when planning.</summary>
        Cat TreeCat { get { return treeCat != null && (int)treeCat.Kind == TreeKind ? treeCat : null; } }

        /// <summary>Starts on the cat's next buyable tier (first path), otherwise on the first tile.</summary>
        void PickTile()
        {
            treePath = 0; treeTier = 1;
            var c = TreeCat;
            if (c == null) return;
            for (int p = 0; p < Defs.PathCount; p++)
                if (G.UpgradeBlock(c, p) == UpBlock.None) { treePath = p; treeTier = c.Up[p] + 1; return; }
        }

        void TreeKey(Keys k)
        {
            int tab = k >= Keys.D1 && k <= Keys.D6 ? k - Keys.D1 : k >= Keys.NumPad1 && k <= Keys.NumPad6 ? k - Keys.NumPad1 : -1;
            if (tab >= 0) TreeTab(tab);
            else if (k == Keys.Escape || k == Keys.I) TreeKind = -1;
            else if (k == Keys.Left) TreeTab(TreeKind - 1);
            else if (k == Keys.Right) TreeTab(TreeKind + 1);
        }

        void CycleTarget()
        {
            if (Sel != null && Aims(Sel.Kind)) Sel.Target = (Targeting)(((int)Sel.Target + 1) % 4);
        }

        void SellSelected()
        {
            if (Sel == null) return;
            G.Sell(Sel);
            Sel = null;
        }

        void Go()
        {
            if (G == null || G.Lost) return;
            if (!G.RoundActive) { if (G.StartRound()) Watch(); }
            else Speed = Speed % 3 + 1;
        }

        void ToggleSound() { Audio.Muted = !Audio.Muted; Save(); }
        void ToggleMusic() { Audio.MusicOff = !Audio.MusicOff; Save(); }

        // ---------- input ----------

        public PointF ToVirtual(Point p) { return new PointF((p.X - OffX) / Scale, (p.Y - OffY) / Scale); }

        public void MouseMove(PointF p)
        {
            Mouse = p;
            UpdateHover();
        }

        void UpdateHover()
        {
            HoverBtn = null;
            hoverShop = -1;
            hoverCat = null;
            hoverUp = -1;
            for (int i = buttons.Count - 1; i >= 0; i--)
                if (buttons[i].Layer == topLayer && buttons[i].R.Contains(Mouse)) { HoverBtn = buttons[i].Id; break; }
            if (HoverBtn != null && HoverBtn.StartsWith("shop")) hoverShop = HoverBtn[4] - '0';
            if (HoverBtn != null && HoverBtn.Length == 3 && HoverBtn.StartsWith("up")) hoverUp = HoverBtn[2] - '0';
            if (HoverBtn == null && Screen == UiScreen.Game && G != null && !Modal && PlaceKind < 0 && Defs.Map.Contains(Mouse))
                hoverCat = G.CatAt(Mouse.X, Mouse.Y);
        }

        public void MouseDown(bool right, bool shift)
        {
            UpdateHover();
            if (HoverBtn != null && !right)
            {
                Sfx("click");
                Button(HoverBtn);
                UpdateHover();
                return;
            }
            if (right && TreeOpen) { TreeKind = -1; return; }
            if (Screen != UiScreen.Game || G == null || Modal) return;
            if (right) { if (PlaceKind >= 0) PlaceKind = -1; else Sel = null; return; }
            if (!Defs.Map.Contains(Mouse)) return;
            if (PlaceKind >= 0) { TryPlace(shift); return; }
            var c = G.CatAt(Mouse.X, Mouse.Y);
            if (c != null && c != Sel) Sfx("meow");
            Sel = c;
            UpdateHover();
        }

        public void Key(Keys k)
        {
            if (k == Keys.M) { ToggleSound(); return; }
            if (k == Keys.N) { ToggleMusic(); return; }
            if (RulesOpen) { if (k == Keys.Escape || k == Keys.Enter) RulesOpen = false; return; }
            if (Screen == UiScreen.Title) { if (k == Keys.Enter || k == Keys.Space) Screen = UiScreen.Difficulty; return; }
            if (Screen == UiScreen.Difficulty)
            {
                if (k == Keys.Escape) Screen = UiScreen.Title;
                else if (k >= Keys.D1 && k <= Keys.D3) NewGame((Difficulty)(k - Keys.D1));
                return;
            }
            if (G == null || ShowLost || ShowWon) return;
            if (TreeOpen) { TreeKey(k); return; }
            if (k == Keys.P || (k == Keys.Escape && Paused)) { Paused = !Paused; armed = null; return; }
            if (Paused) return;
            int shop = k >= Keys.D1 && k <= Keys.D6 ? k - Keys.D1 : k >= Keys.NumPad1 && k <= Keys.NumPad6 ? k - Keys.NumPad1 : -1;
            if (shop >= 0) { Shop(shop); return; }
            switch (k)
            {
                case Keys.Escape:
                    if (PlaceKind >= 0) PlaceKind = -1;
                    else if (Sel != null) Sel = null;
                    else Paused = true;
                    break;
                case Keys.Space: Go(); break;
                case Keys.Z: case Keys.Oemcomma: Upgrade(0); break;
                case Keys.X: case Keys.OemPeriod: Upgrade(1); break;
                case Keys.C: case Keys.OemMinus: Upgrade(2); break;
                case Keys.Tab: CycleTarget(); break;
                case Keys.I: OpenTree(Sel != null ? (int)Sel.Kind : hoverShop >= 0 ? hoverShop : Math.Max(0, PlaceKind)); break;
                case Keys.Delete:
                    if (Sel != null && Confirm("sell" + Sel.Id)) SellSelected();
                    break;
            }
        }

        void Button(string id)
        {
            switch (id)
            {
                case "play": Screen = UiScreen.Difficulty; break;
                case "rules": RulesOpen = true; break;
                case "rules-close": RulesOpen = false; break;
                case "mute": ToggleSound(); break;
                case "music": ToggleMusic(); break;
                case "quit": if (OnQuit != null) OnQuit(); break;
                case "diff0": case "diff1": case "diff2": NewGame((Difficulty)(id[4] - '0')); break;
                case "back": Screen = UiScreen.Title; break;
                case "up0": case "up1": case "up2": Upgrade(id[2] - '0'); break;
                case "target": CycleTarget(); break;
                case "sell": SellSelected(); break;
                case "desel": Sel = null; break;
                case "go": Go(); break;
                case "auto": if (G != null) G.AutoStart = !G.AutoStart; break;
                case "pause": Paused = true; armed = null; break;
                case "resume": Paused = false; break;
                case "menu": if (ShowLost || ShowWon || Confirm("menu")) ToMenu(); break;
                case "again": if (G != null) NewGame(G.Diff); break;
                case "free": wonDismissed = true; break;
                case "tree": if (Sel != null) OpenTree((int)Sel.Kind); break;
                case "tree-close": TreeKind = -1; break;
                default:
                    if (id.StartsWith("shop")) Shop(id[4] - '0');
                    else if (id.Length == 5 && id.StartsWith("tree")) { Sel = null; OpenTree(id[4] - '0'); }
                    else if (id.Length == 4 && id.StartsWith("tab")) TreeTab(id[3] - '0');
                    else if (id.Length == 6 && id.StartsWith("tile")) { treePath = id[4] - '0'; treeTier = id[5] - '0'; }
                    break;
            }
        }

        public RectangleF? ButtonRect(string id)
        {
            for (int i = buttons.Count - 1; i >= 0; i--)
                if (buttons[i].Layer == topLayer && buttons[i].Id == id) return buttons[i].R;
            return null;
        }

        // ---------- settings ----------

        static string SettingsPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KockyVsVysavace", "nastaveni.txt"); }
        }

        public void Load()
        {
            if (!Persist) return;
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    int eq = line.IndexOf('=');
                    int v;
                    if (eq <= 0 || !int.TryParse(line.Substring(eq + 1).Trim(), out v)) continue;
                    string key = line.Substring(0, eq).Trim();
                    if (key == "zvuk") Audio.Muted = v == 0;
                    else if (key == "hudba") Audio.MusicOff = v == 0;
                    else if (key == "vyhry") Wins = Math.Max(0, v);
                    else if (key.Length == 9 && key.StartsWith("nejlepsi") && key[8] >= '0' && key[8] <= '2') Best[key[8] - '0'] = Math.Max(0, v);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Save()
        {
            if (!Persist) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllLines(SettingsPath, new[]
                {
                    "zvuk=" + (Audio.Muted ? 0 : 1), "hudba=" + (Audio.MusicOff ? 0 : 1),
                    "nejlepsi0=" + Best[0], "nejlepsi1=" + Best[1], "nejlepsi2=" + Best[2], "vyhry=" + Wins
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ---------- placement helper (test driver and shots) ----------

        static List<PointF> pathSamples;

        /// <summary>Best free spot for a cat: the most runner inside its range (the stash: furthest from the runner).</summary>
        public static PointF? FindSpot(Game g, TowerKind k, Random rnd)
        {
            if (pathSamples == null)
            {
                pathSamples = new List<PointF>();
                for (int i = 1; i < Defs.Path.Length; i++)
                {
                    var a = Defs.Path[i - 1]; var b = Defs.Path[i];
                    float len = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                    for (float d = 0; d < len; d += 10)
                    {
                        var p = new PointF(a.X + (b.X - a.X) * d / len, a.Y + (b.Y - a.Y) * d / len);
                        if (Defs.Map.Contains(p)) pathSamples.Add(p);
                    }
                }
            }
            float r = Defs.Tower(k).Range * 0.9f;
            PointF? best = null;
            double bestScore = -1;
            for (float y = 24; y < Defs.Map.Bottom - 10; y += 12)
                for (float x = 24; x < Defs.Map.Right - 10; x += 12)
                {
                    if (!g.CanPlace(k, x, y)) continue;
                    double score = 0;
                    if (r <= 0)
                    {
                        double m = 1e9;
                        foreach (var p in pathSamples) m = Math.Min(m, (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));
                        score = Math.Sqrt(m);
                    }
                    else foreach (var p in pathSamples) if ((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y) <= r * r) score++;
                    score += rnd.NextDouble() * 0.9;
                    if (score > bestScore) { bestScore = score; best = new PointF(x, y); }
                }
            return best;
        }

        // ---------- rendering ----------

        static Bitmap mapBmp;
        Bitmap bgGame, bgTitle;

        static void Quality(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        static Bitmap MapBmp
        {
            get
            {
                if (mapBmp == null)
                {
                    mapBmp = new Bitmap(960, 800, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(mapBmp)) { Quality(g); Art.Map(g); }
                }
                return mapBmp;
            }
        }

        Bitmap Background(int w, int h, bool title)
        {
            var bmp = title ? bgTitle : bgGame;
            if (bmp != null && bmp.Width == w && bmp.Height == h) return bmp;
            if (bmp != null) bmp.Dispose();
            bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Night);
                Quality(g);
                g.TranslateTransform(OffX, OffY);
                g.ScaleTransform(Scale, Scale);
                g.SetClip(new RectangleF(0, 0, VW, VH));
                if (title)
                {
                    g.DrawImage(MapBmp, new RectangleF(0, -133.3f, 1280, 1066.7f));
                    g.FillRectangle(Kit.B(Kit.A(Night, 150)), 0, 0, VW, VH);
                }
                else
                {
                    g.DrawImage(MapBmp, Defs.Map);
                    DrawPanelBg(g);
                }
            }
            if (title) bgTitle = bmp; else bgGame = bmp;
            return bmp;
        }

        static void DrawPanelBg(Graphics g)
        {
            var rnd = new Random(5);
            for (int i = 0; i < 16; i++)
            {
                float y = i * 50;
                g.FillRectangle(Kit.B(Kit.Mix(Wood, WoodLight, (float)rnd.NextDouble() * 0.6f)), 960, y, 320, 50);
                using (var grain = new Pen(Kit.A(WoodDark, 45), 1))
                    for (int j = 0; j < 4; j++)
                    {
                        float gy = y + 8 + j * 10 + (float)rnd.NextDouble() * 4, gx = 960 + (float)rnd.NextDouble() * 160;
                        g.DrawLine(grain, gx, gy, gx + 60 + (float)rnd.NextDouble() * 140, gy + (float)rnd.NextDouble() * 2 - 1);
                    }
                using (var seam = new Pen(Kit.A(WoodDark, 160), 2)) g.DrawLine(seam, 960, y, 1280, y);
            }
            g.FillRectangle(Kit.B(Kit.A(Color.Black, 70)), 960, 0, 8, 800);
            g.FillRectangle(Kit.B(Kit.A(Color.Black, 40)), 968, 0, 6, 800);
        }

        public void Render(Graphics g, int w, int h)
        {
            w = Math.Max(1, w); h = Math.Max(1, h);
            Scale = Math.Min(w / VW, h / VH);
            OffX = (w - VW * Scale) / 2;
            OffY = (h - VH * Scale) / 2;
            bool inGame = Screen == UiScreen.Game && G != null;
            var bg = Background(w, h, !inGame);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(bg, new Rectangle(0, 0, w, h));
            g.CompositingMode = CompositingMode.SourceOver;
            Quality(g);
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.TranslateTransform(OffX, OffY);
            g.ScaleTransform(Scale, Scale);

            buttons.Clear();
            layer = topLayer = 0;
            if (inGame) DrawGame(g);
            else if (Screen == UiScreen.Difficulty) DrawDifficulty(g);
            else DrawTitle(g);
            if (RulesOpen) { layer = 2; DrawRules(g); }
            UpdateHover();
            Frames++;
        }

        void Reg(string id, RectangleF r)
        {
            buttons.Add(new Btn { Id = id, R = r, Layer = layer });
            if (layer > topLayer) topLayer = layer;
        }

        static void Card(Graphics g, RectangleF r, Color fill, int shadow)
        {
            Kit.Shadow(g, r, 12, shadow);
            Kit.FillRound(g, fill, r, 12);
            Kit.StrokeRound(g, Edge, 1.2f, r, 12);
        }

        void Button(Graphics g, string id, RectangleF r, string text, Color fill, Color fg, Font f, bool enabled)
        {
            if (enabled) Reg(id, r);
            bool hot = enabled && HoverBtn == id;
            Kit.Shadow(g, r, 10, hot ? 80 : 50);
            Kit.FillRound(g, !enabled ? PaperDim : hot ? Kit.Mix(fill, Color.White, 0.18f) : fill, r, 10);
            Kit.StrokeRound(g, Kit.A(Ink, hot ? 200 : 120), hot ? 2f : 1.4f, r, 10);
            Kit.TextLine(g, text, f, enabled ? fg : Sub, new RectangleF(r.X + 8, r.Y + 1, r.Width - 16, r.Height), SA.Center);
        }

        void IconButton(Graphics g, string id, RectangleF r, string glyph, bool off)
        {
            Reg(id, r);
            bool hot = HoverBtn == id;
            Kit.FillRound(g, hot ? Edge : Paper2, r, 8);
            Kit.StrokeRound(g, Kit.A(Ink, 110), 1.2f, r, 8);
            Glyph(g, glyph, r.X + r.Width / 2, r.Y + r.Height / 2, off);
        }

        static void Glyph(Graphics g, string kind, float cx, float cy, bool off)
        {
            var ink = Kit.B(Ink);
            using (var pen = Kit.P(Ink, 2))
            {
                if (kind == "pause")
                {
                    g.FillRectangle(ink, cx - 6, cy - 7, 4, 14);
                    g.FillRectangle(ink, cx + 2, cy - 7, 4, 14);
                }
                else if (kind == "sound")
                {
                    g.FillPolygon(ink, new[] { new PointF(cx - 9, cy - 3), new PointF(cx - 5, cy - 3), new PointF(cx, cy - 8), new PointF(cx, cy + 8), new PointF(cx - 5, cy + 3), new PointF(cx - 9, cy + 3) });
                    if (off)
                    {
                        using (var red = Kit.P(Red, 2.2f)) { g.DrawLine(red, cx + 3, cy - 4, cx + 9, cy + 4); g.DrawLine(red, cx + 9, cy - 4, cx + 3, cy + 4); }
                    }
                    else
                    {
                        g.DrawArc(pen, cx - 4, cy - 5, 10, 10, -50, 100);
                        g.DrawArc(pen, cx - 6, cy - 9, 16, 18, -50, 100);
                    }
                }
                else if (kind == "music")
                {
                    g.FillEllipse(ink, cx - 7, cy + 2, 8, 6);
                    g.DrawLine(pen, cx, cy + 4, cx, cy - 8);
                    g.DrawLine(pen, cx, cy - 8, cx + 6, cy - 4);
                    if (off) using (var red = Kit.P(Red, 2.2f)) g.DrawLine(red, cx - 9, cy + 9, cx + 9, cy - 9);
                }
            }
        }

        /// <summary>Tier dots centred on cx: bought ones orange (gold when the path is full).</summary>
        static void Pips(Graphics g, float cx, float cy, int tier, float rad, float step)
        {
            int n = Defs.TierCount;
            for (int i = 0; i < n; i++)
            {
                float x = cx + (i - (n - 1) / 2f) * step;
                Kit.Circle(g, i >= tier ? PaperDim : tier >= n ? Gold : Orange, x, cy, rad);
                Kit.Ring(g, Ink, 1.1f, x, cy, rad);
            }
        }

        static void Lock(Graphics g, float cx, float cy, Color c)
        {
            using (var pen = Kit.P(c, 1.8f)) g.DrawArc(pen, cx - 4, cy - 8, 8, 9, 180, 180);
            Kit.FillRound(g, c, new RectangleF(cx - 6, cy - 3, 12, 9), 2);
        }

        // texts checked for width / still too wide at the smallest size (read by the fit harness)
        static int fitChecks, fitClipped;
        static string lastClipped;

        static void CheckFit(string s, float need, float room)
        {
            fitChecks++;
            if (need > room + 0.01f) { fitClipped++; lastClipped = s; }
        }

        /// <summary>One line in the largest size up to px that fits the width (never an ellipsis).</summary>
        static void FitLine(Graphics g, string s, float px, Color c, RectangleF r, SA align = SA.Near)
        {
            var f = Kit.Bold(px);
            float w = Kit.Measure(s, f).Width;
            while (px > 11 && w > r.Width) w = Kit.Measure(s, f = Kit.Bold(--px)).Width;
            CheckFit(s, w, r.Width);
            Kit.TextLine(g, s, f, c, r, align);
        }

        // ---------- gain chips ----------

        const float ChipH = 18, ChipGap = 4, ChipRowH = 21;
        static readonly Color ChipFill = Kit.C(0xD3EFC4), ChipInk = Kit.C(0x1F6B33), ChipDownFill = Kit.C(0xFBDCD6),
            ChipGreyFill = Kit.C(0xEDE3D0);

        static float ChipWidth(Gain gn) { return 19 + Kit.Measure(gn.Short, Kit.Bold(12)).Width + 6; }

        static float ChipsWidth(List<Gain> gains)
        {
            float w = 0;
            foreach (var gn in gains) w += ChipWidth(gn) + ChipGap;
            return gains.Count > 0 ? w - ChipGap : 0;
        }

        /// <summary>A row of pills, icon and short value, from x centred on cy; grey for a locked row, alpha for the purchase float.</summary>
        static void Chips(Graphics g, List<Gain> gains, float x, float cy, float room, bool grey, int alpha)
        {
            if (gains.Count == 0) return;
            float need = ChipsWidth(gains);
            // the label is only built for a row that does not fit (this runs every frame)
            CheckFit(need > room + 0.01f ? string.Join(" ", gains.Select(gn => gn.Short)) : null, need, room);
            alpha &= ~7;   // quantized like Art.Al: a bounded brush and pen cache while fading
            var f = Kit.Bold(12);
            foreach (var gn in gains)
            {
                float w = ChipWidth(gn);
                var r = new RectangleF(x, cy - ChipH / 2, w, ChipH);
                Color fill = grey ? ChipGreyFill : gn.Down ? ChipDownFill : ChipFill, ink = grey ? Sub : gn.Down ? Red : ChipInk;
                Kit.FillRound(g, Kit.A(fill, alpha), r, ChipH / 2);
                Kit.StrokeRound(g, Kit.A(ink, alpha * 60 / 255), 1, r, ChipH / 2);
                Art.StatIcon(g, gn.Kind, x + 10.5f, cy, 12, Kit.A(ink, alpha));
                Kit.Text(g, gn.Short, f, Kit.A(ink, alpha), x + 19, cy + 1, SA.Near, SA.Center);
                x += w + ChipGap;
            }
        }

        /// <summary>Joins the parts with " · " into as few lines as fit the width.</summary>
        static List<string> Wrap(List<string> parts, Font f, float w)
        {
            var lines = new List<string>();
            string cur = "";
            foreach (var p in parts)
            {
                string t = cur.Length == 0 ? p : cur + " · " + p;
                if (cur.Length > 0 && Kit.Measure(t, f).Width > w) { lines.Add(cur); cur = p; }
                else cur = t;
            }
            if (cur.Length > 0) lines.Add(cur);
            return lines;
        }

        static string Num(double v) { return v.ToString(v >= 10 ? "0" : "0.#", CultureInfo.InvariantCulture).Replace('.', ','); }

        static string RangeValue(float r) { return r >= Defs.MapWide ? "celý obývák" : ((int)Math.Round(r)).ToString(); }

        static string RangeText(float r) { return r >= Defs.MapWide ? "Dosah: celý obývák" : "Dosah " + RangeValue(r); }

        /// <summary>The cat hits vacuums itself (Spac only once it snores).</summary>
        static bool Hits(Cat c) { return c.Interval > 0 && (Aims(c.Kind) || c.Damage > 0); }

        static List<string> StatParts(Cat c)
        {
            var parts = new List<string>();
            if (c.Range > 0) parts.Add(RangeText(c.Range));
            if (Hits(c))
            {
                parts.Add("Útoky/s " + Num(1 / c.Interval));
                parts.Add("Poškození " + c.Damage);
                if (Aims(c.Kind)) parts.Add("Průraz " + c.Pierce);
            }
            if (c.Detects) parts.Add("vidí tiché");
            if (c.Buffed) parts.Add("posílený");
            return parts;
        }

        static string Signed(int d) { return d < 0 ? "−" + -d : "+" + d; }

        static Gain NewGain(StatKind k, string old, string now, string chip, bool down)
        {
            return new Gain { Kind = k, Old = old, New = now, Short = down && !chip.StartsWith("−") ? "−" + chip : chip, Down = down };
        }

        /// <summary>
        /// The one comparison of a cat with its upgraded copy: per changed stat the tooltip's "old → " and new value
        /// and the chip's short value. Changes (tooltip, tree) and the chips all read it.
        /// </summary>
        static List<Gain> Gains(Cat c, Cat p)
        {
            var list = new List<Gain>();
            if (Math.Abs(p.Range - c.Range) > 0.5f)
            {
                // the chip is the difference of the two shown numbers, so it always agrees with them
                string chip = p.Range >= Defs.MapWide ? RangeValue(p.Range) : Signed((int)Math.Round(p.Range) - (int)Math.Round(c.Range));
                list.Add(NewGain(StatKind.Range, "Dosah " + RangeValue(c.Range) + " → ", RangeValue(p.Range), chip, p.Range < c.Range));
            }
            if (Hits(p))
            {
                double a = Hits(c) ? 1 / c.Interval : 0, b = 1 / p.Interval;
                if (Num(a) != Num(b))
                {
                    double r = a > 0 ? b / a : 0;
                    string chip = a <= 0 ? Num(b) + "/s" : r >= 1.95 ? "×" + Num(r) : Signed((int)Math.Round((r - 1) * 100)) + " %";
                    list.Add(NewGain(StatKind.Speed, "Útoky/s " + Num(a) + " → ", Num(b), chip, b < a));
                }
                if (p.Damage != c.Damage) list.Add(NewGain(StatKind.Damage, "Poškození " + c.Damage + " → ", p.Damage.ToString(), Signed(p.Damage - c.Damage), p.Damage < c.Damage));
                if (Aims(p.Kind) && p.Pierce != c.Pierce) list.Add(NewGain(StatKind.Pierce, "Průraz " + c.Pierce + " → ", p.Pierce.ToString(), Signed(p.Pierce - c.Pierce), p.Pierce < c.Pierce));
            }
            if (Math.Abs(p.Splash - c.Splash) > 0.5f)
                list.Add(NewGain(StatKind.Blast, "Výbuch " + (int)c.Splash + " → ", ((int)p.Splash).ToString(), Signed((int)p.Splash - (int)c.Splash), p.Splash < c.Splash));
            if (p.Detects != c.Detects)
                list.Add(NewGain(StatKind.Stealth, "Tiché vysavače: " + (c.Detects ? "vidí" : "nevidí") + " → ", p.Detects ? "vidí" : "nevidí", "tiché", !p.Detects));
            bool ca = ArmorOf(c), pa = ArmorOf(p);
            if (pa != ca) list.Add(NewGain(StatKind.Armor, "Pancéřáky: " + (ca ? "prorazí" : "neprorazí") + " → ", pa ? "prorazí" : "neprorazí", "pancíř", !pa));
            float cb = BossMulOf(c), pb = BossMulOf(p);
            if (pb != cb) list.Add(NewGain(StatKind.Boss, "Obřím vysavačům ×" + Num(cb) + " → ", "×" + Num(pb), "obří ×" + Num(pb), pb < cb));
            return list;
        }

        /// <summary>Stat changes an upgrade brings: {"old → ", new value}; special lines from Game.Info that are new come as {"", line}.</summary>
        static List<string[]> Changes(Game g, Cat c, Cat p)
        {
            var list = new List<string[]>();
            foreach (var gn in Gains(c, p)) list.Add(new[] { gn.Old, gn.New });
            string before = g.Info(c) ?? "", after = g.Info(p) ?? "";
            if (after != before)
                foreach (var line in after.Split('\n'))
                    if (line.Length > 0 && Array.IndexOf(before.Split('\n'), line) < 0) list.Add(new[] { "", line });
            return list;
        }

        // Armor and boss multiplier come straight from the bought tiers' stat fields (Core keeps its copies internal)
        static bool ArmorOf(Cat c)
        {
            var d = Defs.Tower(c.Kind);
            for (int p = 0; p < Defs.PathCount; p++)
                for (int t = 0; t < c.Up[p]; t++) if (d.Paths[p][t].Armor) return true;
            return false;
        }

        static float BossMulOf(Cat c)
        {
            var d = Defs.Tower(c.Kind);
            float m = 1;
            for (int p = 0; p < Defs.PathCount; p++)
                for (int t = 0; t < c.Up[p]; t++) m = Math.Max(m, d.Paths[p][t].BossMul);
            return m;
        }

        /// <summary>What one tile of the upgrade tree says: up holds the tile's tier on path, compared with one tier lower.</summary>
        public static TierInfo TierDetail(Game g, TowerKind k, int[] up, int path)
        {
            var def = Defs.Tower(k);
            int tier = Math.Max(1, Math.Min(Defs.TierCount, up[path]));
            var now = (int[])up.Clone();
            now[path] = tier;
            var prev = (int[])now.Clone();
            prev[path] = tier - 1;
            var u = def.Paths[path][tier - 1];
            var d = new TierInfo { Name = u.Name, Desc = u.Desc, Before = g.StatsFor(k, prev), After = g.StatsFor(k, now) };
            d.Gains.AddRange(Gains(d.Before, d.After));
            foreach (var gn in d.Gains) d.Stats.Add(new[] { gn.Old, gn.New });
            var old = (g.Info(d.Before) ?? "").Split('\n');
            foreach (var line in (g.Info(d.After) ?? "").Split('\n'))
                if (line.Length > 0) { d.Special.Add(line); d.Fresh.Add(Array.IndexOf(old, line) < 0); }
            return d;
        }

        /// <summary>What one tier of a path adds over the tier before, computed like the tree: with the cat's other paths when the crosspath rule allows, else without them.</summary>
        List<Gain> TierGains(Cat c, int path, int tier)
        {
            var up = (int[])c.Up.Clone();
            up[path] = tier - 1;
            if (!Defs.CanRaise(up, path)) up = new int[Defs.PathCount];
            up[path] = tier;
            return TierDetail(G, c.Kind, up, path).Gains;
        }

        enum TierState { Plan, Bought, Next, Later, Cross, Legend }

        /// <summary>State of one tier for cat c (null: planning, no state).</summary>
        TierState StateOf(Cat c, int path, int tier)
        {
            if (c == null) return TierState.Plan;
            if (tier <= c.Up[path]) return TierState.Bought;
            var up = (int[])c.Up.Clone();
            up[path] = tier - 1;
            if (!Defs.CanRaise(up, path)) return TierState.Cross;
            if (tier == Defs.TierCount && G.LegendOwner(c.Kind, path, c) != null) return TierState.Legend;
            return tier == c.Up[path] + 1 ? TierState.Next : TierState.Later;
        }

        /// <summary>Price of any tier on this difficulty (Game's rounding: multiplier, then to 5).</summary>
        int TierPrice(TowerKind k, int path, int tier)
        {
            double v = Defs.Tower(k).Paths[path][tier - 1].Price * Defs.PriceMul(G.Diff);
            return (int)(Math.Round(v / 5.0, MidpointRounding.AwayFromZero) * 5);
        }

        static string Tiers(int[] up) { return string.Join("-", Array.ConvertAll(up, t => t.ToString())); }

        static void CheckMark(Graphics g, float cx, float cy, Color c, float s)
        {
            using (var pen = Kit.P(c, 2.4f))
                g.DrawLines(pen, new[] { new PointF(cx - s, cy), new PointF(cx - s * 0.3f, cy + s * 0.7f), new PointF(cx + s, cy - s * 0.7f) });
        }

        /// <summary>A cat sprite in a rounded box (Art caches sprites per device scale, so the scale stays crisp).</summary>
        void CatBox(Graphics g, TowerKind k, int[] up, RectangleF r, float scale, string label)
        {
            Kit.FillRound(g, Paper2, r, 10);
            Kit.StrokeRound(g, Edge, 1.2f, r, 10);
            var st = g.Save();
            using (var clip = Kit.Round(r, 10)) g.SetClip(clip, CombineMode.Intersect);
            g.TranslateTransform(r.X + r.Width / 2, label != null ? r.Y + (r.Height - 18) / 2 + 2 : r.Y + r.Height / 2);
            g.ScaleTransform(scale, scale);
            Art.Cat(g, k, up, 0, 0, (float)(-Math.PI / 2), 0, Now);
            g.Restore(st);
            if (label != null) Kit.TextLine(g, label, Kit.Reg(13), Sub, new RectangleF(r.X, r.Bottom - 19, r.Width, 16), SA.Center);
        }

        static void KeyCap(Graphics g, string key, float x, float cy)
        {
            var f = Kit.Bold(12);
            float w = Math.Max(20, Kit.Measure(key, f).Width + 10);
            var r = new RectangleF(x, cy - 10, w, 20);
            Kit.FillRound(g, Paper, r, 5);
            Kit.StrokeRound(g, Sub, 1.2f, r, 5);
            Kit.TextLine(g, key, f, Ink, r, SA.Center);
        }

        static float KeyCapWidth(string key) { return Math.Max(20, Kit.Measure(key, Kit.Bold(12)).Width + 10); }

        /// <summary>Wrapped paragraph; returns the y below it.</summary>
        static float Para(Graphics g, string s, Font f, Color c, float x, float y, float w)
        {
            var size = g.MeasureString(s, f, new SizeF(w, 1000), Kit.Fmt(SA.Near, SA.Near, true));
            Kit.TextBox(g, s, f, c, new RectangleF(x, y, w, size.Height + 4), SA.Near, SA.Near);
            return y + size.Height;
        }

        void Price(Graphics g, int price, float cx, float cy, Color c, float size)
        {
            var f = Kit.Bold(size);
            string s = price.ToString();
            float tw = Kit.Measure(s, f).Width, total = size + 4 + tw, x = cx - total / 2;
            Art.TreatIcon(g, x + size / 2, cy, size);
            Kit.Text(g, s, f, c, x + size + 4, cy + 1, SA.Near, SA.Center);
        }

        // ---------- game screen ----------

        void DrawGame(Graphics g)
        {
            var st = g.Save();
            g.SetClip(Defs.Map);
            // webs lie on the carpet, under everything else
            foreach (var fx in G.Effects) if (fx.Kind == FxKind.Web) DrawFx(g, fx);
            if (hoverCat != null && hoverCat != Sel && hoverCat.Range > 0) FaintRange(g, hoverCat);
            if (Sel != null)
            {
                if (Sel.Range >= Defs.MapWide) FloorFrame(g, Gold, 3, false);
                else if (Sel.Range > 0) Art.Range(g, Sel.X, Sel.Y, Sel.Range, true);
                var p = hoverUp >= 0 ? G.Preview(Sel, hoverUp) : null;
                if (p != null && Math.Abs(p.Range - Sel.Range) > 0.5f)
                {
                    if (p.Range >= Defs.MapWide) FloorFrame(g, Color.White, 2.5f, true);
                    else Art.RangeGhost(g, Sel.X, Sel.Y, p.Range);
                }
            }
            foreach (var c in G.Cats)
            {
                if (c == Sel) Kit.Ring(g, Gold, 3, c.X, c.Y, Defs.CatRadius + 5);
                else if (c == hoverCat) Kit.Ring(g, Kit.A(Color.White, 170), 2, c.X, c.Y, Defs.CatRadius + 4);
                float attack = (float)Math.Max(0, 1 - (G.Time - c.LastAttack) / 0.25);
                Art.Cat(g, c.Kind, c.Up, c.X, c.Y, c.Angle, attack, Now + c.Id * 0.37);
            }
            for (int pass = 0; pass < 2; pass++)
                foreach (var v in G.Vacs)
                    if (Defs.Vac(v.Kind).Boss == (pass == 0))
                        Art.Vacuum(g, v.Kind, v.X, v.Y, v.Angle, v.Stealth, v.Slow, v.Frozen, v.HpFrac, v.Tough, G.Time);
            foreach (var s in G.Shots) Art.Shot(g, s.Kind, s.X, s.Y, s.Angle, G.Time);
            foreach (var fx in G.Effects) if (fx.Kind != FxKind.Web) DrawFx(g, fx);
            DrawFloat(g);
            if (PlaceKind >= 0 && !Modal && Defs.Map.Contains(Mouse))
            {
                var k = (TowerKind)PlaceKind;
                bool ok = G.CanPlace(k, Mouse.X, Mouse.Y) && G.Money >= G.PriceOf(k);
                float r = Defs.Tower(k).Range;
                Art.Range(g, Mouse.X, Mouse.Y, r > 0 ? r : Defs.CatRadius + 8, ok);
                Art.Cat(g, k, NoUp, Mouse.X, Mouse.Y, (float)(-Math.PI / 2), 0, Now);
            }
            DrawToast(g);
            g.Restore(st);

            DrawPanel(g);

            if (TreeOpen)
            {
                layer = 1;
                DrawTree(g);
                layer = 0;
            }
            else if (Paused || ShowLost || ShowWon)
            {
                layer = 1;
                g.FillRectangle(Kit.B(Kit.A(Night, 160)), 0, 0, VW, VH);
                if (ShowLost) DrawEnd(g, false);
                else if (ShowWon) DrawEnd(g, true);
                else DrawPause(g);
                layer = 0;
            }
        }

        // ---------- upgrade tree ----------

        static RectangleF TileRect(int path, int tier) { return new RectangleF(32 + (tier - 1) * 114, 164 + path * 152, 106, 120); }

        static readonly string[] StateNames = { "", "Koupeno", "Další na řadě", "Později", "Zamčeno", "Legenda obsazená" };

        /// <summary>The classic upgrade screen over the map: tabs of the six kinds, 3 × 5 tiles, the detail of the hovered or clicked tile.</summary>
        void DrawTree(Graphics g)
        {
            g.FillRectangle(Kit.B(Kit.A(Night, 150)), 0, 0, VW, VH);
            var card = new RectangleF(16, 16, 928, 768);
            Card(g, card, Paper, 120);
            var k = (TowerKind)TreeKind;
            var def = Defs.Tower(k);
            var c = TreeCat;
            Kit.Text(g, "Všechna vylepšení", Kit.Bold(26), Ink, 32, 44, SA.Near, SA.Center);
            string sub = c != null ? "Tvoje kočka " + Tiers(c.Up) + " · " + Treats(G.Money) : "Plánování: vylepšení bez kočky na podlaze";
            Kit.Text(g, sub, Kit.Reg(15), Sub, 32 + Kit.Measure("Všechna vylepšení", Kit.Bold(26)).Width + 16, 46, SA.Near, SA.Center);
            Button(g, "tree-close", new RectangleF(788, 26, 140, 38), "Zavřít (Esc)", Paper2, Ink, Kit.Bold(16), true);

            for (int i = 0; i < Defs.TowerCount; i++)
            {
                var r = new RectangleF(32 + i * 150, 76, 144, 50);
                string id = "tab" + i;
                Reg(id, r);
                bool on = i == TreeKind, hot = HoverBtn == id;
                Kit.FillRound(g, on ? Kit.C(0xFFFDF7) : hot ? Paper2 : PaperDim, r, 10);
                Kit.StrokeRound(g, on ? Orange : hot ? Gold : Edge, on ? 2.6f : hot ? 2 : 1.2f, r, 10);
                Art.CatIcon(g, (TowerKind)i, new RectangleF(r.X + 6, r.Y + 4, 42, 42));
                Kit.Circle(g, Wood, r.X + 9, r.Y + 10, 8);
                Kit.Text(g, (i + 1).ToString(), Kit.Bold(12), Paper, r.X + 9, r.Y + 11, SA.Center, SA.Center);
                Kit.TextBox(g, Defs.Tower((TowerKind)i).Name, Kit.Bold(15), on ? Ink : Sub, new RectangleF(r.X + 52, r.Y + 2, r.Width - 56, r.Height - 4), SA.Near, SA.Center);
            }

            int hp = -1, ht = -1;
            if (HoverBtn != null && HoverBtn.Length == 6 && HoverBtn.StartsWith("tile")) { hp = HoverBtn[4] - '0'; ht = HoverBtn[5] - '0'; }
            int dp = hp >= 0 ? hp : treePath, dt = hp >= 0 ? ht : treeTier;

            for (int p = 0; p < Defs.PathCount; p++)
            {
                float ly = TileRect(p, 1).Y - 14;
                Kit.Text(g, def.PathNames[p], Kit.Bold(18), Ink, 32, ly, SA.Near, SA.Center);
                Kit.Text(g, "cesta " + (p + 1) + " · klávesa " + UpKeys[p], Kit.Reg(13), Sub, 40 + Kit.Measure(def.PathNames[p], Kit.Bold(18)).Width, ly + 1, SA.Near, SA.Center);
                for (int t = 1; t <= Defs.TierCount; t++) DrawTile(g, k, c, p, t, p == dp && t == dt);
            }

            // state key and the two rules
            float ky = 632;
            var swatches = c != null
                ? new[] { TierState.Bought, TierState.Next, TierState.Later, TierState.Cross }
                : new TierState[0];
            float kx = 32;
            foreach (var s in swatches)
            {
                var sr = new RectangleF(kx, ky - 8, 16, 16);
                Kit.FillRound(g, TileFill(s), sr, 4);
                Kit.StrokeRound(g, TileEdge(s), s == TierState.Next ? 2.2f : 1.2f, sr, 4);
                string name = s == TierState.Cross ? "zamčeno / obsazená" : StateNames[(int)s].ToLowerInvariant();
                Kit.Text(g, name, Kit.Reg(13), Sub, kx + 22, ky + 1, SA.Near, SA.Center);
                kx += 22 + Kit.Measure(name, Kit.Reg(13)).Width + 22;
            }
            if (c == null) Kit.Text(g, "Ceny platí pro tuhle obtížnost. Klikni na dlaždici a uvidíš podrobnosti.", Kit.Reg(13), Sub, 32, ky + 1, SA.Near, SA.Center);
            var rf = Kit.Reg(14);
            float ry = Para(g, "• Vylepšovat jde nejvýš dvě cesty, jen jedna z nich nad 2. stupeň (třeba 5-2-0).", rf, Ink, 32, ky + 18, 566) + 4;
            ry = Para(g, "• Legendu (5. stupeň) má na každé cestě jen jedna kočka stejného druhu. Prodej ji a místo se uvolní.", rf, Ink, 32, ry, 566) + 4;
            Kit.Text(g, "← → nebo 1–6 přepíná kočky · Esc zavře · hra mezitím stojí", Kit.Reg(13), Sub, 32, Math.Max(ry + 10, 756), SA.Near, SA.Center);

            DrawTreeDetail(g, k, c, dp, dt, new RectangleF(612, 140, 316, 628));
        }

        Color TileFill(TierState s)
        {
            return s == TierState.Bought ? Kit.C(0xE3F1D6) : s == TierState.Next ? Kit.C(0xFFFDF7) : s == TierState.Cross || s == TierState.Legend ? PaperDim : Paper2;
        }

        Color TileEdge(TierState s)
        {
            return s == TierState.Bought ? Green : s == TierState.Next ? Orange : s == TierState.Legend ? LegendC : Edge;
        }

        void DrawTile(Graphics g, TowerKind k, Cat c, int p, int t, bool shown)
        {
            var r = TileRect(p, t);
            string id = "tile" + p + t;
            Reg(id, r);
            var s = StateOf(c, p, t);
            bool blocked = s == TierState.Cross || s == TierState.Legend;
            Kit.Shadow(g, r, 10, shown ? 70 : 35);
            Kit.FillRound(g, TileFill(s), r, 10);
            Kit.StrokeRound(g, shown ? Gold : TileEdge(s), shown ? 3.2f : s == TierState.Next ? 2.4f : 1.2f, r, 10);
            var icon = new RectangleF(r.X + (r.Width - 52) / 2, r.Y + 7, 52, 52);
            Art.UpgradeIcon(g, k, p, t, icon);
            if (blocked) Kit.FillRound(g, Kit.A(PaperDim, 140), icon, 8);
            Kit.Circle(g, Wood, r.X + 13, r.Y + 13, 9);
            Kit.Text(g, t.ToString(), Kit.Bold(12), Paper, r.X + 13, r.Y + 14, SA.Center, SA.Center);
            if (s == TierState.Bought)
            {
                Kit.Circle(g, Green, r.Right - 13, r.Y + 13, 9);
                CheckMark(g, r.Right - 13, r.Y + 13, Paper, 4);
            }
            Kit.TextBox(g, Defs.Tower(k).Paths[p][t - 1].Name, Kit.Bold(13), blocked ? Sub : Ink, new RectangleF(r.X + 4, r.Y + 61, r.Width - 8, 36), SA.Center, SA.Center);
            float by = r.Bottom - 12;
            if (s == TierState.Bought) Kit.Text(g, "koupeno", Kit.Bold(13), Green, r.X + r.Width / 2, by, SA.Center, SA.Center);
            else if (blocked)
            {
                string tag = s == TierState.Legend ? "obsazená" : "zamčeno";
                var col = s == TierState.Legend ? LegendC : Sub;
                float tw = Kit.Measure(tag, Kit.Bold(13)).Width;
                float x0 = r.X + (r.Width - tw - 16) / 2;
                Lock(g, x0 + 6, by + 1, col);
                Kit.Text(g, tag, Kit.Bold(13), col, x0 + 16, by, SA.Near, SA.Center);
            }
            else
            {
                int price = TierPrice(k, p, t);
                Price(g, price, r.X + r.Width / 2, by, s == TierState.Next && G.Money < price ? Red : Ink, 15);
            }
        }

        /// <summary>Side detail of one tile: big picture, description, stats against the tier before (Game.StatsFor), special numbers (Game.Info).</summary>
        void DrawTreeDetail(Graphics g, TowerKind k, Cat c, int path, int tier, RectangleF box)
        {
            var def = Defs.Tower(k);
            var s = StateOf(c, path, tier);
            // a selected cat's other paths join in when the crosspath rule allows that combination
            bool combined = c != null && s != TierState.Cross;
            var up = combined ? (int[])c.Up.Clone() : new int[Defs.PathCount];
            up[path] = tier;
            var d = TierDetail(G, k, up, path);

            Kit.FillRound(g, Paper2, box, 12);
            Kit.StrokeRound(g, Edge, 1.2f, box, 12);
            float x = box.X + 12, w = box.Width - 24, y = box.Y + 12;
            CatBox(g, k, up, new RectangleF(x, y, w, 168), 2.3f, null);
            Kit.TextLine(g, Tiers(up), Kit.Bold(13), Sub, new RectangleF(x + 10, y + 6, w - 20, 18), SA.Far);
            y += 176;
            Kit.TextLine(g, def.PathNames[path] + " · " + tier + ". stupeň z " + Defs.TierCount, Kit.Cond(15), Sub, new RectangleF(x, y, w, 20), SA.Near);
            FitLine(g, d.Name, 21, Ink, new RectangleF(x, y + 20, w, 28));
            y += 50;
            int price = TierPrice(k, path, tier);
            Price(g, price, x + 8 + (Kit.Measure(price.ToString(), Kit.Bold(16)).Width + 20) / 2, y + 10, Ink, 16);
            if (s != TierState.Plan)
            {
                string st = StateNames[(int)s];
                var sf = Kit.Bold(13);
                float sw = Kit.Measure(st, sf).Width + 18;
                var chip = new RectangleF(box.Right - 12 - sw, y, sw, 22);
                Kit.FillRound(g, TileFill(s), chip, 11);
                Kit.StrokeRound(g, s == TierState.Later ? Sub : TileEdge(s), 1.4f, chip, 11);
                Kit.TextLine(g, st, sf, s == TierState.Cross ? Sub : s == TierState.Legend ? LegendC : s == TierState.Bought ? Green : Ink, chip, SA.Center);
            }
            y += 30;
            y = Para(g, d.Desc, Kit.Reg(14), Ink, x, y, w) + 10;

            Kit.TextLine(g, tier == 1 ? "Oproti kočce bez téhle cesty:" : "Oproti " + (tier - 1) + ". stupni:", Kit.Cond(14), Sub, new RectangleF(x, y, w, 18), SA.Near);
            y += 20;
            if (d.Stats.Count == 0) { Kit.TextLine(g, "Základní čísla se nemění.", Kit.Reg(14), Sub, new RectangleF(x, y, w, 20), SA.Near); y += 20; }
            DrawChanges(g, d.Stats, x, y, w);
            y += d.Stats.Count * 20 + 6;
            if (d.Special.Count > 0)
            {
                Kit.TextLine(g, "Zvláštní schopnosti:", Kit.Cond(14), Sub, new RectangleF(x, y, w, 18), SA.Near);
                y += 20;
                for (int i = 0; i < d.Special.Count; i++, y += 19)
                    Kit.TextLine(g, d.Special[i], d.Fresh[i] ? Kit.Bold(13) : Kit.Reg(13), d.Fresh[i] ? Green : Ink, new RectangleF(x, y, w, 19), SA.Near);
                y += 6;
            }
            string note = null;
            Color noteC = Sub;
            if (s == TierState.Legend) { note = LegendText(k); noteC = LegendC; }
            else if (s == TierState.Cross) { note = CrossText + " Obrázek a čísla jsou bez tvých ostatních cest."; noteC = Red; }
            else if (combined && up.Where((t, i) => i != path && t > 0).Any()) note = "Spolu s tvými dalšími cestami (" + Tiers(up) + ").";
            if (note != null) Para(g, note, Kit.Bold(13), noteC, x, y, w);
        }

        void DrawFx(Graphics g, Fx fx)
        {
            float age = fx.Life > 0 ? (float)Math.Max(0, Math.Min(1, (G.Time - fx.Born) / fx.Life)) : 1;
            Art.Effect(g, fx.Kind, fx.X, fx.Y, fx.X2, fx.Y2, fx.R, fx.Angle, age, fx.Color, fx.Tier);
        }

        /// <summary>Range of a hovered (not selected) cat, quieter than Art.Range.</summary>
        static void FaintRange(Graphics g, Cat c)
        {
            if (c.Range >= Defs.MapWide) { FloorFrame(g, Kit.A(Color.White, 130), 2, false); return; }
            Kit.Circle(g, Kit.A(Color.White, 30), c.X, c.Y, c.Range);
            Kit.Ring(g, Kit.A(Color.White, 130), 1.5f, c.X, c.Y, c.Range);
        }

        /// <summary>Thin frame around the whole floor: the range of a whole-floor sniper.</summary>
        static void FloorFrame(Graphics g, Color c, float w, bool dashed)
        {
            var m = Defs.Map;
            using (var pen = new Pen(c, w))
            {
                if (dashed) pen.DashPattern = new[] { 4f, 3f };
                g.DrawRectangle(pen, m.X + 3, m.Y + 3, m.Width - 6, m.Height - 6);
            }
        }

        void DrawToast(Graphics g)
        {
            double age = Now - toastAt;
            if (toast == null || age > 2.6 || age < 0) return;
            int a = (int)(255 * Math.Min(1, (2.6 - age) / 0.6));
            var f = Kit.Bold(19);
            float w = Kit.Measure(toast, f).Width + 44;
            var r = new RectangleF(480 - w / 2, 18, w, 40);
            Kit.FillRound(g, Kit.A(Color.Black, a / 5), new RectangleF(r.X + 1, r.Y + 3, r.Width, r.Height), 20);
            Kit.FillRound(g, Kit.A(Paper, a * 240 / 255), r, 20);
            using (var pen = new Pen(Kit.A(Orange, a), 2)) using (var p = Kit.Round(r, 20)) g.DrawPath(pen, p);
            Kit.TextLine(g, toast, f, Kit.A(Ink, a), r, SA.Center);
        }

        /// <summary>The bought tier's name and gain chips rising about 30 px from the cat (ease-out), then fading; Now is real time, so pause and speed do not matter.</summary>
        void DrawFloat(Graphics g)
        {
            double age = Now - floatAt;
            if (floatName == null || age < 0 || age > FloatLife) return;
            float t = (float)(age / FloatLife), rise = 30 * (1 - (1 - t) * (1 - t) * (1 - t));
            int a = (int)(255 * Math.Max(0, Math.Min(1, Math.Min(age / 0.12, (FloatLife - age) / 0.5))));
            var nf = Kit.Bold(17);
            float nw = Kit.Measure(floatName, nf).Width, cw = ChipsWidth(floatGains), w = Math.Max(nw, cw);
            float h = 22 + (floatGains.Count > 0 ? ChipH + 4 : 0);
            // above the cat; a cat at the top edge gets it below, still rising
            float gap = Defs.CatRadius + 10, top = floatY - gap - h - 30 >= Defs.Map.Top + 6 ? floatY - gap - h : floatY + gap + 30;
            top -= rise;
            float cx = Math.Max(Defs.Map.Left + w / 2 + 8, Math.Min(Defs.Map.Right - w / 2 - 8, floatX));
            // a click buys with the row hovered: keep clear of last frame's tooltip, on the cat's side of it
            if (!tipRect.IsEmpty && tipRect.IntersectsWith(new RectangleF(cx - w / 2, top, w, h)))
            {
                if (floatX < tipRect.X) cx = Math.Max(Defs.Map.Left + w / 2 + 4, Math.Min(cx, tipRect.X - w / 2 - 6));
                else if (floatX > tipRect.Right) cx = Math.Min(Defs.Map.Right - w / 2 - 4, Math.Max(cx, tipRect.Right + w / 2 + 6));
            }
            Kit.Halo(g, floatName, nf, Kit.A(Paper, a & ~7), Kit.A(Night, (a * 210 / 255) & ~7), cx, top + 11, SA.Center, SA.Center);
            Chips(g, floatGains, cx - cw / 2, top + 22 + 4 + ChipH / 2, cw, false, a);
        }

        // ---------- side panel ----------

        void DrawPanel(Graphics g)
        {
            tipRect = RectangleF.Empty;
            DrawStatus(g);
            if (Sel != null) DrawSelected(g);
            else { DrawShop(g); DrawHint(g); }
            DrawBottom(g);
        }

        void DrawStatus(Graphics g)
        {
            var r = new RectangleF(972, 12, 296, 92);
            Card(g, r, Paper, 60);
            Art.LifeIcon(g, 998, 38, 28);
            Kit.Text(g, G.Lives.ToString(), Kit.Bold(28), Ink, 1018, 39, SA.Near, SA.Center);
            Art.TreatIcon(g, 1122, 38, 30);
            Kit.Text(g, G.Money.ToString(), Kit.Bold(28), Ink, 1143, 39, SA.Near, SA.Center);
            using (var pen = new Pen(Edge, 1)) g.DrawLine(pen, 984, 62, 1256, 62);
            bool free = G.Round > Defs.FinalRound;
            string round = free ? "Kolo " + G.Round + " · volná hra" : "Kolo " + Math.Max(1, G.Round) + " / " + Defs.FinalRound;
            var rf = Kit.Bold(17);
            Kit.Text(g, round, rf, Ink, 984, 84, SA.Near, SA.Center);
            if (!free) Kit.Text(g, Defs.DifficultyNames[(int)G.Diff], Kit.Reg(13), Sub, 984 + Kit.Measure(round, rf).Width + 8, 85, SA.Near, SA.Center);
            IconButton(g, "pause", new RectangleF(1170, 70, 28, 28), "pause", false);
            IconButton(g, "mute", new RectangleF(1204, 70, 28, 28), "sound", Audio.Muted);
            IconButton(g, "music", new RectangleF(1238, 70, 28, 28), "music", Audio.MusicOff);
        }

        void DrawShop(Graphics g)
        {
            Kit.Text(g, "Kočky", Kit.Bold(17), Paper, 976, 125, SA.Near, SA.Center);
            Kit.Text(g, "klávesy 1–6", Kit.Reg(13), Kit.A(Paper, 190), 1266, 126, SA.Far, SA.Center);
            for (int i = 0; i < Defs.TowerCount; i++)
            {
                var k = (TowerKind)i;
                var def = Defs.Tower(k);
                var r = new RectangleF(972 + (i % 2) * 152, 140 + (i / 2) * 124, 144, 116);
                int price = G.PriceOf(k);
                bool afford = G.Money >= price, chosen = PlaceKind == i;
                string id = "shop" + i;
                Reg(id, r);
                bool hot = HoverBtn == id;
                Kit.Shadow(g, r, 12, hot ? 80 : 50);
                Kit.FillRound(g, !afford ? PaperDim : hot ? Kit.C(0xFFFDF7) : Paper, r, 12);
                if (chosen) Kit.StrokeRound(g, Orange, 3, r, 12);
                else Kit.StrokeRound(g, hot ? Gold : Edge, hot ? 2.2f : 1.2f, r, 12);
                var icon = new RectangleF(r.X + 38, r.Y + 6, 68, 64);
                Art.CatIcon(g, k, icon);
                if (!afford) Kit.FillRound(g, Kit.A(PaperDim, 140), icon, 8);
                Kit.TextLine(g, def.Name, Kit.Cond(16), afford ? Ink : Sub, new RectangleF(r.X + 4, r.Y + 72, r.Width - 8, 20), SA.Center);
                Price(g, price, r.X + r.Width / 2, r.Y + 100, afford ? Ink : Red, 17);
                Kit.Circle(g, Wood, r.X + 15, r.Y + 15, 10);
                Kit.Text(g, (i + 1).ToString(), Kit.Bold(13), Paper, r.X + 15, r.Y + 16, SA.Center, SA.Center);

                // "i" badge: the upgrade tree of this kind, to plan before buying
                string tid = "tree" + i;
                float bx = r.Right - 16, by = r.Y + 16;
                Reg(tid, new RectangleF(bx - 14, by - 14, 28, 28));
                bool ihot = HoverBtn == tid;
                Kit.Circle(g, ihot ? Orange : Paper2, bx, by, 11);
                Kit.Ring(g, Kit.A(Ink, ihot ? 220 : 140), 1.4f, bx, by, 11);
                Kit.Text(g, "i", Kit.Bold(15), ihot ? Paper : Ink, bx, by + 1, SA.Center, SA.Center);
            }
        }

        void DrawHint(Graphics g)
        {
            var r = new RectangleF(972, 514, 296, 86);
            Card(g, r, Paper, 50);
            string title, text;
            if (hoverShop >= 0)
            {
                var d = Defs.Tower((TowerKind)hoverShop);
                var parts = new List<string>();
                if (d.Range > 0) parts.Add("Dosah " + (int)d.Range);
                if (d.Interval > 0 && d.Damage > 0) parts.Add("Útoky/s " + Num(1 / d.Interval));
                Kit.TextLine(g, d.Name, Kit.Bold(16), Ink, new RectangleF(r.X + 12, r.Y + 6, r.Width - 24, 24), SA.Near);
                Kit.TextLine(g, parts.Count > 0 ? string.Join(" · ", parts) : "Neútočí", Kit.Bold(13), Orange, new RectangleF(r.X + 12, r.Y + 30, r.Width - 24, 18), SA.Near);
                Kit.TextBox(g, d.Desc, Kit.Reg(13), Sub, new RectangleF(r.X + 12, r.Y + 48, r.Width - 24, r.Height - 50), SA.Near, SA.Near);
                return;
            }
            else if (HoverBtn != null && HoverBtn.Length == 5 && HoverBtn.StartsWith("tree"))
            {
                title = "Vylepšení: " + Defs.Tower((TowerKind)(HoverBtn[4] - '0')).Name;
                text = "Ukáže všech 15 vylepšení téhle kočky, ještě než ji koupíš.";
            }
            else if (PlaceKind >= 0)
            {
                title = "Pokládání: " + Defs.Tower((TowerKind)PlaceKind).Name;
                text = "Klikni na podlahu vedle koberce. Se Shiftem pokládáš dál, pravé tlačítko ruší.";
            }
            else
            {
                title = "Jak na to";
                text = "Polož kočku vedle koberce, kliknutím ji pak vylepšíš. Ikonka „i“ nebo klávesa I ukáže všechna vylepšení.";
            }
            Kit.TextLine(g, title, Kit.Bold(16), Ink, new RectangleF(r.X + 12, r.Y + 6, r.Width - 24, 24), SA.Near);
            Kit.TextBox(g, text, Kit.Reg(13), Sub, new RectangleF(r.X + 12, r.Y + 30, r.Width - 24, r.Height - 32), SA.Near, SA.Near);
        }

        static RectangleF RowRect(int path) { return new RectangleF(972, 260 + path * 92, 296, 88); }

        /// <summary>Text column of an upgrade row, right of the icon.</summary>
        static RectangleF RowText(RectangleF r) { return new RectangleF(r.X + 58, r.Y, r.Width - 68, r.Height); }

        /// <summary>A panel button with its text centred left of the key cap, shrunk (never cut) to fit.</summary>
        void PanelButton(Graphics g, string id, RectangleF r, string text, Color fill, Color fg, string key)
        {
            Button(g, id, r, "", fill, fg, Kit.Bold(14), true);
            float right = r.Right - 8;
            if (key != null) { right -= KeyCapWidth(key); KeyCap(g, key, right, r.Y + r.Height / 2); right -= 4; }
            FitLine(g, text, 14, fg, new RectangleF(r.X + 8, r.Y + 1, right - r.X - 8, r.Height), SA.Center);
        }

        void DrawSelected(Graphics g)
        {
            var c = Sel;
            var def = Defs.Tower(c.Kind);
            Card(g, new RectangleF(972, 112, 296, 142), Paper, 60);
            Art.CatIcon(g, c.Kind, new RectangleF(980, 118, 60, 56));
            FitLine(g, def.Name, 20, Ink, new RectangleF(1048, 118, 210, 28));
            string sub = c.Kind == TowerKind.Pamlskarna ? "Sype pamlsky, neútočí." : "Rozbité vrstvy: " + c.Pops;
            Kit.TextLine(g, sub, Kit.Reg(14), Sub, new RectangleF(1048, 146, 210, 22), SA.Near);

            var f = Kit.Reg(13);
            var lines = Wrap(StatParts(c), f, 272);
            int stats = lines.Count;
            string info = G.Info(c);
            if (!string.IsNullOrEmpty(info)) lines.AddRange(info.Split('\n'));
            float lh = lines.Count > 4 ? 15 : 17, y = 178;
            for (int i = 0; i < lines.Count; i++, y += lh)
                Kit.TextLine(g, lines[i], i < stats ? Kit.Bold(13) : f, i < stats ? Ink : Sub, new RectangleF(984, y, 272, lh), SA.Near);

            for (int p = 0; p < Defs.PathCount; p++) DrawUpgrade(g, c, p, RowRect(p));

            // two pairs of half buttons under the rows; a cat that aims at nothing gives its half to the tree button
            const float y1 = 538, y2 = 574, bh = 30, half = (296 - 6) / 2f, xr = 972 + half + 6;
            if (Aims(c.Kind))
            {
                PanelButton(g, "target", new RectangleF(972, y1, half, bh), "Cíl: " + Defs.TargetingNames[(int)c.Target], Paper, Ink, "Tab");
                PanelButton(g, "tree", new RectangleF(xr, y1, half, bh), "Vylepšení", Paper, Ink, "I");
            }
            else PanelButton(g, "tree", new RectangleF(972, y1, 296, bh), "Všechna vylepšení", Paper, Ink, "I");
            int value = G.SellValue(c);
            PanelButton(g, "sell", new RectangleF(972, y2, half, bh), Armed("sell" + c.Id) ? "Prodat? Znovu Delete" : "Prodat za " + value, Red, Paper, null);
            PanelButton(g, "desel", new RectangleF(xr, y2, half, bh), "Do obchodu", Paper2, Ink, "Esc");

            if (hoverUp >= 0) DrawTooltip(g, c, hoverUp, RowRect(hoverUp));
        }

        /// <summary>
        /// One upgrade button: name, price and key on top, and right under them what the next tier gives,
        /// so nobody has to hover to see the bonus before buying (keys Z/X/C buy without any hover).
        /// </summary>
        void DrawUpgrade(Graphics g, Cat c, int path, RectangleF r)
        {
            var def = Defs.Tower(c.Kind);
            int tier = c.Up[path];
            int price = G.UpgradePrice(c, path);
            bool done = tier >= Defs.TierCount, locked = !done && price < 0, afford = price >= 0 && G.Money >= price;
            bool dim = locked || (!done && !afford);
            string id = "up" + path;
            Reg(id, r);
            bool hot = HoverBtn == id;
            Kit.Shadow(g, r, 12, hot ? 80 : 50);
            Kit.FillRound(g, done ? Paper2 : dim ? PaperDim : hot ? Kit.C(0xFFFDF7) : Paper, r, 12);
            Kit.StrokeRound(g, hot ? Gold : afford ? Green : Edge, hot || afford ? 2.2f : 1.2f, r, 12);

            var icon = new RectangleF(r.X + 5, r.Y + 8, 48, 48);
            Art.UpgradeIcon(g, c.Kind, path, Math.Min(tier + 1, Defs.TierCount), icon);
            if (dim) Kit.FillRound(g, Kit.A(PaperDim, 130), icon, 8);
            // the pips share the chips' line at the foot of the row
            Pips(g, icon.X + icon.Width / 2, r.Bottom - 16, tier, 3.4f, 9);

            // top line: the name on the left, price and key (or the state) on the right
            var text = RowText(r);
            float x = text.X, right = text.Right, cy = r.Y + 13, w = text.Width, nameRight;
            var u = def.Paths[path][Math.Min(tier, Defs.TierCount - 1)];
            if (done)
            {
                var f = Kit.Bold(14);
                Kit.Text(g, "Hotovo", f, Kit.C(0xB07A00), right, cy, SA.Far, SA.Center);
                nameRight = right - Kit.Measure("Hotovo", f).Width - 8;
            }
            else if (locked)
            {
                bool legend = G.UpgradeBlock(c, path) == UpBlock.LegendTaken;
                string tag = legend ? "Obsazená" : "Zamčeno";
                var f = Kit.Bold(13);
                float tw = Kit.Measure(tag, f).Width;
                Kit.Text(g, tag, f, legend ? LegendC : Sub, right, cy, SA.Far, SA.Center);
                Lock(g, right - tw - 11, cy, legend ? LegendC : Sub);
                nameRight = right - tw - 24;
            }
            else
            {
                var pf = Kit.Bold(15);
                float pw = Kit.Measure(price.ToString(), pf).Width, kx = right;
                string miss = "chybí " + (price - G.Money);
                float mw = Kit.Measure(miss, Kit.Reg(12)).Width;
                if (afford) { kx -= KeyCapWidth(UpKeys[path]); KeyCap(g, UpKeys[path], kx, cy); }
                // the missing amount only while a long name still fits beside it; the red price and the tooltip say it anyway
                else if (w - mw - 34 - pw >= Kit.Measure(u.Name, Kit.Bold(12)).Width)
                {
                    kx -= mw;
                    Kit.Text(g, miss, Kit.Reg(12), Red, kx, cy + 1, SA.Near, SA.Center);
                }
                Kit.Text(g, price.ToString(), pf, afford ? Green : Red, kx - 6, cy + 1, SA.Far, SA.Center);
                Art.TreatIcon(g, kx - 20 - pw, cy, 16);
                nameRight = kx - 34 - pw;
            }
            FitLine(g, u.Name, 15, dim ? Sub : Ink, new RectangleF(x, cy - 10, nameRight - x, 20));

            // what it gives: the tier's description in up to two lines, then every stat it raises as chips
            if (done) { Kit.TextLine(g, "Cesta je vylepšená naplno.", Kit.Reg(13), Sub, new RectangleF(x, r.Y + 25, w, 18), SA.Near); return; }
            var df = Kit.Reg(13);
            int lines = Lines(g, u.Desc, df, w);
            for (float px = 12; px >= 11 && lines > 2; px--) lines = Lines(g, u.Desc, df = Kit.Reg(px), w);
            Kit.TextBox(g, u.Desc, df, locked ? Sub : Ink, new RectangleF(x, r.Y + 25, w, 36), SA.Near, SA.Near);
            // the same numbers as the tooltip; a crosspath-locked tier is shown like the tree does, without the other paths
            var p = G.Preview(c, path);
            Chips(g, p != null ? Gains(c, p) : TierGains(c, path, tier + 1), x, r.Bottom - 16, w, locked, 255);
        }

        /// <summary>How many lines s takes when wrapped to width w.</summary>
        static int Lines(Graphics g, string s, Font f, float w)
        {
            int chars, lines;
            g.MeasureString(s, f, new SizeF(w, 1000), Kit.Fmt(SA.Near, SA.Near, true), out chars, out lines);
            return lines;
        }

        /// <summary>Hovered upgrade row: before → after picture, description, stat changes from Game.Preview, the whole path and the price, on the floor next to the panel.</summary>
        void DrawTooltip(Graphics g, Cat c, int path, RectangleF row)
        {
            var def = Defs.Tower(c.Kind);
            int tier = c.Up[path], price = G.UpgradePrice(c, path);
            var block = G.UpgradeBlock(c, path);
            bool done = block == UpBlock.Maxed;
            var u = def.Paths[path][Math.Min(tier, Defs.TierCount - 1)];
            var p = done ? null : G.Preview(c, path);
            var changes = p != null ? Changes(G, c, p) : new List<string[]>();
            string foot;
            Color footC = Red;
            if (done) { foot = "Cesta je vylepšená naplno."; footC = Green; }
            else if (block == UpBlock.LegendTaken) { foot = LegendText(c.Kind); footC = LegendC; }
            else if (price < 0) foot = CrossText;
            else if (G.Money < price) foot = "Cena " + Treats(price) + ", chybí ti " + (price - G.Money) + ".";
            else { foot = "Cena " + Treats(price) + " · klávesa " + UpKeys[path]; footC = Green; }

            const float w = 340, pad = 14, tw = w - 2 * pad, picH = 106, lineH = 24;
            var body = Kit.Reg(14);
            var fmt = Kit.Fmt(SA.Near, SA.Near, true);
            float descH = g.MeasureString(u.Desc, body, new SizeF(tw, 1000), fmt).Height;
            float footH = g.MeasureString(foot, Kit.Bold(14), new SizeF(tw, 1000), fmt).Height;
            // under each tier of the whole path, what it adds over the tier before
            var tierGains = new List<Gain>[Defs.TierCount];
            float listH = 24 + Defs.TierCount * lineH + 6;
            for (int t = 1; t <= Defs.TierCount; t++)
                if ((tierGains[t - 1] = TierGains(c, path, t)).Count > 0) listH += ChipRowH;
            float h = pad + 44 + (p != null ? picH : 0) + descH + 8 + changes.Count * 20 + (changes.Count > 0 ? 6 : 0) + listH + footH + pad;
            float x = Defs.Map.Right - 10 - w;
            float y = Math.Max(10, Math.Min(Defs.Map.Bottom - 10 - h, row.Y + row.Height / 2 - h / 2));
            // keep the selected cat visible
            float cr = Defs.CatRadius + 10;
            if (new RectangleF(x, y, w, h).IntersectsWith(new RectangleF(c.X - cr, c.Y - cr, cr * 2, cr * 2))) x = 10;
            tipRect = new RectangleF(x, y, w, h);
            Card(g, tipRect, Paper, 110);

            float ty = y + pad;
            FitLine(g, u.Name, 18, Ink, new RectangleF(x + pad, ty, tw, 24));
            Kit.TextLine(g, def.PathNames[path] + " · " + Math.Min(tier + 1, Defs.TierCount) + ". stupeň z " + Defs.TierCount, Kit.Cond(14), Sub, new RectangleF(x + pad, ty + 24, tw, 20), SA.Near);
            ty += 44;
            if (p != null)
            {
                float bw = (tw - 34) / 2;
                CatBox(g, c.Kind, c.Up, new RectangleF(x + pad, ty, bw, picH - 8), 0.85f, "teď " + Tiers(c.Up));
                Kit.Text(g, "→", Kit.Bold(26), Orange, x + pad + bw + 17, ty + (picH - 8) / 2, SA.Center, SA.Center);
                CatBox(g, c.Kind, p.Up, new RectangleF(x + pad + bw + 34, ty, bw, picH - 8), 0.85f, "potom " + Tiers(p.Up));
                ty += picH;
            }
            ty = Para(g, u.Desc, body, Ink, x + pad, ty, tw) + 8;
            DrawChanges(g, changes, x + pad, ty, tw);
            ty += changes.Count * 20 + (changes.Count > 0 ? 6 : 0);
            Kit.TextLine(g, "Celá cesta " + def.PathNames[path], Kit.Cond(14), Sub, new RectangleF(x + pad, ty, tw, 20), SA.Near);
            ty += 24;
            for (int t = 1; t <= Defs.TierCount; t++)
            {
                PathLine(g, c, path, t, new RectangleF(x + pad, ty, tw, lineH - 2));
                ty += lineH;
                if (tierGains[t - 1].Count == 0) continue;
                var st = StateOf(c, path, t);
                // indented under the name, past PathLine's icon
                Chips(g, tierGains[t - 1], x + pad + lineH + 5, ty + ChipH / 2 - 1, tw - lineH - 5, st == TierState.Cross || st == TierState.Legend, 255);
                ty += ChipRowH;
            }
            ty += 6;
            Kit.TextBox(g, foot, Kit.Bold(14), footC, new RectangleF(x + pad, ty, tw, footH + 4), SA.Near, SA.Near);
        }

        /// <summary>"old → " in ink, the new value in green bold; special lines alone in green.</summary>
        static void DrawChanges(Graphics g, List<string[]> changes, float x, float y, float w)
        {
            var body = Kit.Reg(14);
            foreach (var ch in changes)
            {
                float pw = ch[0].Length > 0 ? Kit.Measure(ch[0], body).Width : 0;
                if (pw > 0) Kit.TextLine(g, ch[0], body, Ink, new RectangleF(x, y, w, 20), SA.Near);
                Kit.TextLine(g, ch[1], Kit.Bold(pw > 0 ? 14 : 13), Green, new RectangleF(x + pw, y, w - pw, 20), SA.Near);
                y += 20;
            }
        }

        /// <summary>One tier of a path in the tooltip list: icon, name, then price / bought / blocked.</summary>
        void PathLine(Graphics g, Cat c, int path, int t, RectangleF r)
        {
            var st = StateOf(c, path, t);
            bool blocked = st == TierState.Cross || st == TierState.Legend;
            if (st == TierState.Next)
            {
                Kit.FillRound(g, Kit.A(Orange, 40), r, 6);
                Kit.StrokeRound(g, Orange, 1.4f, r, 6);
            }
            var icon = new RectangleF(r.X + 3, r.Y + 1, r.Height - 2, r.Height - 2);
            Art.UpgradeIcon(g, c.Kind, path, t, icon);
            if (blocked) Kit.FillRound(g, Kit.A(Paper, 150), icon, 4);
            float right = r.Right - 6, cy = r.Y + r.Height / 2;
            var sf = Kit.Bold(13);
            string tag;
            Color tagC;
            if (st == TierState.Bought) { tag = "koupeno"; tagC = Green; }
            else if (st == TierState.Cross) { tag = "zamčeno"; tagC = Sub; }
            else if (st == TierState.Legend) { tag = "obsazená"; tagC = LegendC; }
            else
            {
                int price = TierPrice(c.Kind, path, t);
                tag = price.ToString();
                tagC = st == TierState.Next && G.Money < price ? Red : Ink;
            }
            float tagW = Kit.Measure(tag, sf).Width;
            Kit.Text(g, tag, sf, tagC, right, cy + 1, SA.Far, SA.Center);
            float gx = right - tagW - 10;
            if (st == TierState.Bought) CheckMark(g, gx, cy, Green, 5);
            else if (blocked) Lock(g, gx, cy + 1, tagC);
            else Art.TreatIcon(g, gx, cy, 14);
            float nx = icon.Right + 6;
            var nf = st == TierState.Next ? Kit.Bold(13) : Kit.Reg(13);
            Kit.TextLine(g, t + ". " + Defs.Tower(c.Kind).Paths[path][t - 1].Name, nf, st == TierState.Bought || blocked ? Sub : Ink,
                new RectangleF(nx, r.Y, gx - 12 - nx, r.Height), SA.Near);
        }

        void DrawBottom(Graphics g)
        {
            var pr = new RectangleF(972, 612, 296, 62);
            Card(g, pr, Paper, 50);
            Kit.Text(g, "Další kolo", Kit.Cond(14), Sub, pr.X + 12, pr.Y + 13, SA.Near, SA.Center);
            Kit.TextBox(g, G.NextRoundPreview() ?? "", Kit.Reg(13), Ink, new RectangleF(pr.X + 12, pr.Y + 24, pr.Width - 24, 36), SA.Near, SA.Near);

            var ar = new RectangleF(972, 682, 296, 28);
            Reg("auto", ar);
            bool hot = HoverBtn == "auto";
            var box = new RectangleF(976, 686, 20, 20);
            Kit.FillRound(g, Paper, box, 5);
            Kit.StrokeRound(g, hot ? Gold : Ink, 1.6f, box, 5);
            if (G.AutoStart)
                using (var pen = Kit.P(Green, 3)) g.DrawLines(pen, new[] { new PointF(980, 696), new PointF(985, 701), new PointF(992, 690) });
            Kit.Text(g, "Další kolo pustit samo", Kit.Reg(15), hot ? Gold : Paper, 1004, 697, SA.Near, SA.Center);

            var gr = new RectangleF(972, 718, 296, 70);
            bool running = G.RoundActive;
            bool enabled = !G.Lost;
            if (enabled) Reg("go", gr);
            bool ghot = enabled && HoverBtn == "go";
            var fill = running ? Orange : Green;
            Kit.Shadow(g, gr, 12, ghot ? 90 : 60);
            Kit.FillRound(g, ghot ? Kit.Mix(fill, Color.White, 0.15f) : fill, gr, 12);
            Kit.StrokeRound(g, Kit.A(Ink, 160), 1.6f, gr, 12);
            int tri = running ? Speed : 1;
            for (int i = 0; i < tri; i++)
            {
                float x = gr.X + 20 + i * 11;
                g.FillPolygon(Kit.B(Paper), new[] { new PointF(x, gr.Y + 21), new PointF(x + 13, gr.Y + 30), new PointF(x, gr.Y + 39) });
            }
            float tx = gr.X + 28 + tri * 11;
            string main = running ? "Rychlost " + Speed + "×" : "Pustit vysavače";
            string sub = running ? "Mezerník přepne 1× / 2× / 3×" : "Kolo " + (G.Round + 1) + " · mezerník";
            Kit.Text(g, main, Kit.Bold(24), Paper, tx, gr.Y + 30, SA.Near, SA.Center);
            Kit.Text(g, sub, Kit.Reg(13), Kit.A(Paper, 225), tx, gr.Y + 54, SA.Near, SA.Center);
        }

        // ---------- overlays ----------

        void DrawPause(Graphics g)
        {
            var r = new RectangleF(440, 180, 400, 440);
            Card(g, r, Paper, 120);
            Kit.Text(g, "Pauza", Kit.Bold(40), Ink, 640, r.Y + 50, SA.Center, SA.Center);
            Kit.Text(g, "P nebo Esc pokračuje", Kit.Reg(15), Sub, 640, r.Y + 88, SA.Center, SA.Center);
            float y = r.Y + 116;
            var f = Kit.Bold(19);
            Button(g, "resume", new RectangleF(480, y, 320, 50), "Pokračovat", Green, Paper, f, true);
            Button(g, "mute", new RectangleF(480, y + 60, 320, 50), Audio.Muted ? "Zvuk: vypnutý" : "Zvuk: zapnutý", Paper2, Ink, f, true);
            Button(g, "music", new RectangleF(480, y + 120, 320, 50), Audio.MusicOff ? "Hudba: vypnutá" : "Hudba: zapnutá", Paper2, Ink, f, true);
            Button(g, "rules", new RectangleF(480, y + 180, 320, 50), "Jak hrát", Paper2, Ink, f, true);
            bool sure = Armed("menu");
            Button(g, "menu", new RectangleF(480, y + 240, 320, 50), sure ? "Opravdu? Hra se neuloží" : "Do menu", sure ? Red : Paper2, sure ? Paper : Ink, f, true);
        }

        static readonly int[] WinnerUp = { 5, 2, 0 };

        void DrawEnd(Graphics g, bool won)
        {
            var r = new RectangleF(370, 150, 540, 480);
            Card(g, r, Paper, 120);
            var st = g.Save();
            g.TranslateTransform(640, r.Y + 72);
            g.ScaleTransform(1.9f, 1.9f);
            if (won) Art.Cat(g, TowerKind.Drapka, WinnerUp, 0, 0, (float)(-Math.PI / 2), 0, Now);
            else Art.Vacuum(g, VacKind.Raketa, 0, 0, (float)(Math.PI / 2), false, 0, false, 1, Now);
            g.Restore(st);
            Kit.Text(g, won ? "Kočky ubránily pelíšek!" : "Vysavače vysály pelíšek!", Kit.Bold(34), won ? Green : Red, 640, r.Y + 160, SA.Center, SA.Center);
            string sub = won ? "Všech " + Defs.FinalRound + " kol zvládnuto. Gratulujeme!" : "Obtížnost " + Defs.DifficultyNames[(int)G.Diff] + ". Zkus jiné rozestavění koček.";
            Kit.Text(g, sub, Kit.Reg(17), Sub, 640, r.Y + 198, SA.Center, SA.Center);

            int d = (int)G.Diff;
            string[] labels = { "Přežitá kola", "Rozbité vrstvy", "Nejlepší výsledek (" + Defs.DifficultyNames[d] + ")" };
            string[] values = { Survived.ToString(), G.PoppedTotal.ToString(), Best[d] > 0 ? "kolo " + Best[d] : "–" };
            for (int i = 0; i < 3; i++)
            {
                float y = r.Y + 246 + i * 36;
                Kit.Text(g, labels[i], Kit.Reg(17), Sub, r.X + 70, y, SA.Near, SA.Center);
                Kit.Text(g, values[i], Kit.Bold(19), Ink, r.Right - 70, y, SA.Far, SA.Center);
                if (i < 2) using (var pen = new Pen(Edge, 1)) g.DrawLine(pen, r.X + 70, y + 18, r.Right - 70, y + 18);
            }
            if (record)
            {
                var pill = new RectangleF(640 - 70, r.Y + 352, 140, 28);
                Kit.FillRound(g, Gold, pill, 14);
                Kit.TextLine(g, "Nový rekord!", Kit.Bold(15), Ink, pill, SA.Center);
            }
            var f = Kit.Bold(19);
            if (won) Button(g, "free", new RectangleF(r.X + 40, r.Bottom - 80, 260, 52), "Hrát dál (volná hra)", Green, Paper, f, true);
            else Button(g, "again", new RectangleF(r.X + 40, r.Bottom - 80, 260, 52), "Zkusit znovu", Green, Paper, f, true);
            Button(g, "menu", new RectangleF(r.Right - 220, r.Bottom - 80, 180, 52), "Do menu", Paper2, Ink, f, true);
        }

        // ---------- title, difficulty, rules ----------

        void DrawTitle(Graphics g)
        {
            var card = new RectangleF(70, 60, 500, 680);
            Card(g, card, Paper, 120);
            Kit.Text(g, "Kočky", Kit.Bold(76), Orange, 106, 140, SA.Near, SA.Center);
            Kit.Text(g, "vs. vysavače", Kit.Bold(46), Ink, 110, 208, SA.Near, SA.Center);
            Kit.TextBox(g, "Rozmísti kočky kolem koberce a nepusť robotické vysavače k pelíšku.", Kit.Reg(18), Sub, new RectangleF(112, 240, 420, 56), SA.Near, SA.Near);
            float y = 316;
            var f = Kit.Bold(21);
            Button(g, "play", new RectangleF(112, y, 416, 58), "Hrát", Orange, Paper, Kit.Bold(26), true);
            Button(g, "rules", new RectangleF(112, y + 70, 416, 50), "Jak hrát", Paper2, Ink, f, true);
            Button(g, "mute", new RectangleF(112, y + 130, 416, 50), Audio.Muted ? "Zvuk: vypnutý" : "Zvuk: zapnutý", Paper2, Ink, f, true);
            Button(g, "music", new RectangleF(112, y + 190, 416, 50), Audio.MusicOff ? "Hudba: vypnutá" : "Hudba: zapnutá", Paper2, Ink, f, true);
            Button(g, "quit", new RectangleF(112, y + 250, 416, 50), "Konec", Paper2, Ink, f, true);

            using (var pen = new Pen(Edge, 1)) g.DrawLine(pen, 112, 634, 528, 634);
            Kit.Text(g, "Nejlepší kola", Kit.Cond(16), Sub, 112, 656, SA.Near, SA.Center);
            Kit.Text(g, "Výhry: " + Wins, Kit.Cond(16), Sub, 528, 656, SA.Far, SA.Center);
            for (int d = 0; d < 3; d++)
            {
                float x = 112 + d * 140;
                Kit.Text(g, Defs.DifficultyNames[d], Kit.Reg(15), Sub, x, 684, SA.Near, SA.Center);
                Kit.Text(g, Best[d] > 0 ? Best[d] + ". kolo" : "–", Kit.Bold(20), Ink, x, 710, SA.Near, SA.Center);
            }

            var hero = new RectangleF(610, 90, 600, 560);
            Card(g, hero, Paper, 120);
            var st = g.Save();
            var inner = new RectangleF(hero.X + 12, hero.Y + 12, hero.Width - 24, hero.Height - 24);
            using (var clip = Kit.Round(inner, 8)) g.SetClip(clip, CombineMode.Intersect);
            Art.Hero(g, inner, Now);
            g.Restore(st);
            Kit.Halo(g, "Kočky hlídají obývák. Vysavače mají jiný plán.", Kit.Reg(18), Paper, Kit.A(Night, 200), 910, 686, SA.Center, SA.Center);
        }

        void DrawDifficulty(Graphics g)
        {
            var card = new RectangleF(160, 100, 960, 600);
            Card(g, card, Paper, 120);
            Kit.Text(g, "Vyber obtížnost", Kit.Bold(40), Ink, 640, 160, SA.Center, SA.Center);
            Kit.Text(g, "Čím těžší, tím méně životů a pamlsků a dražší kočky.", Kit.Reg(17), Sub, 640, 202, SA.Center, SA.Center);
            Color[] accent = { Green, Gold, Red };
            for (int i = 0; i < 3; i++)
            {
                var d = (Difficulty)i;
                var r = new RectangleF(200 + i * 300, 240, 280, 320);
                string id = "diff" + i;
                Reg(id, r);
                bool hot = HoverBtn == id;
                Kit.Shadow(g, r, 14, hot ? 90 : 50);
                Kit.FillRound(g, hot ? Kit.C(0xFFFDF7) : Paper2, r, 14);
                using (var band = Kit.Round4(new RectangleF(r.X, r.Y, r.Width, 16), 14, 14, 0, 0)) g.FillPath(Kit.B(accent[i]), band);
                Kit.StrokeRound(g, hot ? Orange : Edge, hot ? 3 : 1.4f, r, 14);
                Kit.Text(g, Defs.DifficultyNames[i], Kit.Bold(32), Ink, r.X + r.Width / 2, r.Y + 58, SA.Center, SA.Center);
                KeyCap(g, (i + 1).ToString(), r.X + r.Width / 2 - 10, r.Y + 92);
                Art.LifeIcon(g, r.X + 64, r.Y + 140, 30);
                Kit.Text(g, Plural(Defs.StartLives(d), "život", "životy", "životů"), Kit.Bold(21), Ink, r.X + 88, r.Y + 141, SA.Near, SA.Center);
                Art.TreatIcon(g, r.X + 64, r.Y + 186, 30);
                Kit.Text(g, Treats(Defs.StartMoney(d)), Kit.Bold(21), Ink, r.X + 88, r.Y + 187, SA.Near, SA.Center);
                string mul = Defs.PriceMul(d).ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',');
                Kit.Text(g, "Ceny koček ×" + mul, Kit.Reg(17), Sub, r.X + r.Width / 2, r.Y + 236, SA.Center, SA.Center);
                using (var pen = new Pen(Edge, 1)) g.DrawLine(pen, r.X + 30, r.Y + 262, r.Right - 30, r.Y + 262);
                Kit.Text(g, Best[i] > 0 ? "Nejlepší: " + Best[i] + ". kolo" : "Zatím nehráno", Kit.Reg(16), Sub, r.X + r.Width / 2, r.Y + 290, SA.Center, SA.Center);
            }
            Button(g, "back", new RectangleF(540, 590, 200, 50), "Zpět", Paper2, Ink, Kit.Bold(19), true);
        }

        static readonly string[][] Controls =
        {
            new[] { "Klik", "vyber a polož kočku, vyber kočku na podlaze" },
            new[] { "Shift", "s kliknutím pokládáš další stejnou" },
            new[] { "Pravé", "zruší pokládání nebo výběr" },
            new[] { "1–6", "kočka z obchodu" },
            new[] { "Mezerník", "pustit kolo, pak rychlost 1× / 2× / 3×" },
            new[] { "Z  X  C", "vylepšit cestu 1 / 2 / 3 (i , . -)" },
            new[] { "I", "všechna vylepšení (strom)" },
            new[] { "Tab", "na koho kočka míří" },
            new[] { "Delete", "2× za sebou prodá kočku" },
            new[] { "Esc", "zrušit, zavřít výběr, pauza" },
            new[] { "P", "pauza" },
            new[] { "M  N", "zvuk, hudba" },
        };

        void DrawRules(Graphics g)
        {
            g.FillRectangle(Kit.B(Kit.A(Night, 150)), 0, 0, VW, VH);
            var card = new RectangleF(50, 36, 1180, 728);
            Card(g, card, Paper, 120);
            Kit.Text(g, "Jak hrát", Kit.Bold(36), Ink, 90, 82, SA.Near, SA.Center);
            Button(g, "rules-close", new RectangleF(1040, 58, 160, 46), "Zpět (Esc)", Paper2, Ink, Kit.Bold(18), true);

            var h = Kit.Bold(19);
            var body = Kit.Reg(15);
            float x = 90, w = 330, y = 126;
            Kit.Text(g, "Cíl hry", h, Orange, x, y, SA.Near, SA.Near);
            y = Para(g, "Robotické vysavače vyjíždějí z nabíječky vlevo a jedou po koberci k pelíšku vpravo nahoře. Každý, který dojede, ti vezme životy. Přežij " + Defs.FinalRound + " kol a pelíšek je zachráněný.", body, Ink, x, y + 28, w) + 16;
            Kit.Text(g, "Vrstvy", h, Orange, x, y, SA.Near, SA.Near);
            y = Para(g, "Zásah odloupne jednu vrstvu: silnější vysavač se změní ve slabší (Raketa → Sprint → Turbo → Plus → Robík). Za každou vrstvu dostaneš pamlsek, mezi koly ještě odměnu za kolo.", body, Ink, x, y + 28, w) + 16;
            Kit.Text(g, "Zvláštní vysavače", h, Orange, x, y, SA.Near, SA.Near);
            y = Para(g, "Tiché (průsvitné) vidí jen kočky s nočním viděním nebo ty v dosahu kocoura s odhalením. Pancéřák odolá drápům i chlupům, zabírají na něj klubíčka a laser. Průmyslák a Obr jsou obří a vydrží hodně ran.", body, Ink, x, y + 28, w) + 16;
            Kit.Text(g, "Vylepšení", h, Orange, x, y, SA.Near, SA.Near);
            Para(g, "Každá kočka má tři cesty vylepšení po pěti stupních. Vylepšovat můžeš nejvýš dvě cesty, jen jedna z nich jde nad 2. stupeň (třeba 5-2-0 nebo 0-2-5). Legendu (5. stupeň) má na každé cestě jen jedna kočka stejného druhu. Klávesa I ukáže všechna vylepšení.", body, Ink, x, y + 28, w);

            x = 460; y = 126;
            Kit.Text(g, "Kočky", h, Orange, x, y, SA.Near, SA.Near);
            for (int i = 0; i < Defs.TowerCount; i++)
            {
                var d = Defs.Tower((TowerKind)i);
                float ry = 160 + i * 96;
                Kit.FillRound(g, Paper2, new RectangleF(x, ry, 60, 60), 10);
                Art.CatIcon(g, d.Kind, new RectangleF(x + 2, ry + 2, 56, 56));
                Kit.Text(g, d.Name, Kit.Bold(17), Ink, x + 72, ry + 10, SA.Near, SA.Center);
                Art.TreatIcon(g, x + 72 + Kit.Measure(d.Name, Kit.Bold(17)).Width + 18, ry + 10, 16);
                Kit.Text(g, d.Price.ToString(), Kit.Reg(15), Sub, x + 72 + Kit.Measure(d.Name, Kit.Bold(17)).Width + 30, ry + 11, SA.Near, SA.Center);
                Kit.TextBox(g, d.Desc, Kit.Reg(14), Ink, new RectangleF(x + 72, ry + 24, 300, 60), SA.Near, SA.Near);
            }

            x = 880; y = 126;
            Kit.Text(g, "Ovládání", h, Orange, x, y, SA.Near, SA.Near);
            for (int i = 0; i < Controls.Length; i++)
            {
                float ry = 172 + i * 40;
                KeyCap(g, Controls[i][0], x, ry);
                Kit.TextBox(g, Controls[i][1], Kit.Reg(14), Ink, new RectangleF(x + 86, ry - 18, 230, 38), SA.Near, SA.Center);
            }
            Kit.TextBox(g, "Ceny v návodu jsou základní, obtížnost je ještě upraví.", Kit.Ital(13), Sub, new RectangleF(x, 652, 320, 40), SA.Near, SA.Near);
        }
    }

    /// <summary>One stat an upgrade changes (Ui.Gains): the tooltip's "old → " and new value, and the chip's short value.</summary>
    public class Gain
    {
        public StatKind Kind;
        public string Old, New, Short;   // "Dosah 170 → ", "230", "+60"
        public bool Down;                // a decrease: red chip with "−"
    }

    /// <summary>One tile's detail in the upgrade tree (Ui.TierDetail).</summary>
    public class TierInfo
    {
        public string Name, Desc;
        public Cat Before, After;                                   // StatsFor one tier lower and at this tier
        public readonly List<Gain> Gains = new List<Gain>();         // the stat changes against the tier before
        public readonly List<string[]> Stats = new List<string[]>(); // the same as {"Dosah 170 → ", "210"}
        public readonly List<string> Special = new List<string>();  // Game.Info lines at this tier
        public readonly List<bool> Fresh = new List<bool>();        // that special line is new at this tier
    }
}
