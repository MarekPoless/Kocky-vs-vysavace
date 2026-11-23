using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Kocky
{
    /// <summary>What an upgrade raises, for the gain chips (Art.StatIcon, Ui.Gains).</summary>
    public enum StatKind { Range, Speed, Damage, Pierce, Blast, Stealth, Armor, Boss }

    /// <summary>
    /// Every drawing of the game: map, cats, vacuums, shots, effects, icons. Cats and vacuums are painted as vectors
    /// once per (kind, state, angle bucket, animation frame) into small cached bitmaps and blitted at whole pixels.
    /// </summary>
    public static class Art
    {
        static readonly Color Ink = Kit.Ink, White = Color.White;
        const float O = 1.7f;                       // outline thickness of sprites
        const float Deg = 180f / (float)Math.PI;

        static Color C(int rgb) { return Kit.C(rgb); }
        static Color Dk(Color c, float t) { return Kit.Mix(c, Ink, t); }
        static Color Lt(Color c, float t) { return Kit.Mix(c, White, t); }
        static Color Al(Color c, float a) { return Kit.A(c, ((int)(Math.Max(0, Math.Min(1, a)) * 255)) & ~7); } // alpha quantized: bounded brush cache

        // ---------- cached pens (never disposed, like Kit.B brushes) ----------

        static readonly Dictionary<long, Pen> pens = new Dictionary<long, Pen>();
        static Pen Pn(Color c, float w)
        {
            long key = ((long)(uint)c.ToArgb() << 20) + (int)(w * 8 + 0.5f);
            Pen p;
            if (!pens.TryGetValue(key, out p)) { p = Kit.P(c, (int)(w * 8 + 0.5f) / 8f); pens[key] = p; }
            return p;
        }

        // ---------- primitive helpers ----------

        static void E(Graphics g, Color c, float cx, float cy, float rx, float ry) { g.FillEllipse(Kit.B(c), cx - rx, cy - ry, rx * 2, ry * 2); }
        static void D(Graphics g, Color c, float cx, float cy, float r) { Kit.Circle(g, c, cx, cy, r); }
        static Color oc = Kit.Ink;                  // outline colour of the O* helpers (translucent while painting a stealth vacuum)
        static void OE(Graphics g, Color c, float cx, float cy, float rx, float ry) { E(g, oc, cx, cy, rx + O, ry + O); E(g, c, cx, cy, rx, ry); }
        static void L(Graphics g, Color c, float w, float x1, float y1, float x2, float y2) { g.DrawLine(Pn(c, w), x1, y1, x2, y2); }
        static void OL(Graphics g, Color c, float w, float x1, float y1, float x2, float y2) { L(g, oc, w + O * 2, x1, y1, x2, y2); L(g, c, w, x1, y1, x2, y2); }
        static void Poly(Graphics g, Color c, params PointF[] p) { g.FillPolygon(Kit.B(c), p); }
        static void OPoly(Graphics g, Color c, params PointF[] p) { g.DrawPolygon(Pn(oc, O * 2), p); g.FillPolygon(Kit.B(oc), p); g.FillPolygon(Kit.B(c), p); }
        static PointF P(float x, float y) { return new PointF(x, y); }
        static void ORect(Graphics g, Color c, float x, float y, float w, float h, float rad)
        {
            Kit.FillRound(g, oc, new RectangleF(x - O, y - O, w + O * 2, h + O * 2), rad + O);
            Kit.FillRound(g, c, new RectangleF(x, y, w, h), rad);
        }
        static void Bez(Graphics g, Color c, float w, PointF a, PointF b, PointF cc, PointF d) { g.DrawBezier(Pn(c, w), a, b, cc, d); }

        // silhouette pieces: pass 1 (ink) draws them grown by the outline, pass 2 in colour -> one clean outline around the whole body
        static void SE(Graphics g, bool ink, Color c, float cx, float cy, float rx, float ry)
        {
            if (ink) E(g, Ink, cx, cy, rx + O, ry + O); else E(g, c, cx, cy, rx, ry);
        }
        static void SPoly(Graphics g, bool ink, Color c, params PointF[] p)
        {
            if (ink) { g.DrawPolygon(Pn(Ink, O * 2), p); g.FillPolygon(Kit.B(Ink), p); } else g.FillPolygon(Kit.B(c), p);
        }
        static void SBez(Graphics g, bool ink, Color c, float w, PointF a, PointF b, PointF cc, PointF d)
        {
            Bez(g, ink ? Ink : c, ink ? w + O * 2 : w, a, b, cc, d);
        }

        static void Star(Graphics g, Color c, float cx, float cy, float r)
        {
            float q = r * 0.28f;
            Poly(g, c, P(cx, cy - r), P(cx + q, cy - q), P(cx + r, cy), P(cx + q, cy + q), P(cx, cy + r), P(cx - q, cy + q), P(cx - r, cy), P(cx - q, cy - q));
        }

        static float Rf(Random r, float a, float b) { return a + (float)r.NextDouble() * (b - a); }

        // ---------- sprite cache ----------

        class Spr { public Bitmap Bmp; public int Half; public int Used; }
        static readonly Dictionary<long, Spr> sprites = new Dictionary<long, Spr>();
        static int clock;
        static long cacheBytes;
        const long CacheLimit = 64L << 20;          // least recently used sprites go when the cache passes 64 MB (64 tier combos per cat, bigger tier-5 sprites)

        static Spr Find(long key)
        {
            Spr s;
            if (sprites.TryGetValue(key, out s)) s.Used = ++clock;
            return s;
        }

        // key = sprite type (4 bits) | device scale * 32 (10 bits) | 49 bits of type-specific state
        static long Key(int type, int sq, long payload) { return ((long)type << 59) + ((long)sq << 49) + payload; }

        // Where the caller's transform puts (x, y) on the device. Sprites are painted at the device scale and blitted
        // in device pixels, so a canvas ScaleTransform neither blurs them nor sends DrawImage down its slow resampling path.
        static float devX, devY;
        static bool devMapped;
        static int Dev(Graphics g, float x, float y)
        {
            devMapped = false; devX = x; devY = y;
            using (var m = g.Transform)
            {
                if (m.IsIdentity) return 32;
                var e = m.Elements;
                if (Math.Abs(e[1]) > 1e-4f || Math.Abs(e[2]) > 1e-4f || e[0] <= 0 || Math.Abs(e[0] - e[3]) > 1e-3f * e[0] || e[0] * 32 > 1023)
                    return 32;                      // rotated or sheared: draw the 1x sprite through the transform
                devMapped = true;
                devX = e[0] * x + e[4]; devY = e[3] * y + e[5];
                return Math.Max(1, (int)Math.Round(e[0] * 32));
            }
        }

        static Graphics Begin(long key, int half, int sq, out Spr s)
        {
            if (cacheBytes >= CacheLimit) Trim();
            float sc = sq / 32f;
            int hd = Math.Max(1, (int)Math.Ceiling(half * sc));
            s = new Spr { Bmp = new Bitmap(hd * 2, hd * 2, PixelFormat.Format32bppPArgb), Half = hd, Used = ++clock };
            sprites[key] = s;
            cacheBytes += 16L * hd * hd;
            var g = Graphics.FromImage(s.Bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(hd, hd);
            if (sq != 32) g.ScaleTransform(sc, sc);
            return g;
        }

        static void Trim()
        {
            var all = new List<KeyValuePair<long, Spr>>(sprites);
            all.Sort((a, b) => a.Value.Used.CompareTo(b.Value.Used));
            for (int i = 0; i < all.Count && cacheBytes > CacheLimit * 3 / 4; i++)
            {
                var s = all[i].Value;
                cacheBytes -= 16L * s.Half * s.Half;
                s.Bmp.Dispose();
                sprites.Remove(all[i].Key);
            }
        }

        // at most a few new cat sprites per ~1/60 s; beyond that a neighbouring cached angle stands in for a frame or two
        static long budgetWindow;
        static int budgetUsed;
        static bool OverBudget()
        {
            long w = System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 60);
            if (w != budgetWindow) { budgetWindow = w; budgetUsed = 0; }
            return budgetUsed >= 3;
        }

        // draws a sprite centred on the point last passed to Dev
        static void Blit(Graphics g, Spr s)
        {
            BlitAt(g, s.Bmp, (int)Math.Round(devX) - s.Half, (int)Math.Round(devY) - s.Half, devMapped);
        }

        static void BlitAt(Graphics g, Bitmap b, int px, int py, bool mapped)
        {
            if (!mapped) { g.DrawImage(b, px, py, b.Width, b.Height); return; }
            var st = g.Save();
            g.ResetTransform();
            // 1:1 at whole device pixels: nearest neighbour gives the same pixels as the caller's HQ filter, faster
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(b, px, py, b.Width, b.Height);
            g.Restore(st);
        }

        static int AngleBucket(float angle, int n)
        {
            double a = angle / (2 * Math.PI);
            a -= Math.Floor(a);
            return (int)Math.Round(a * n) % n;
        }

        // =====================================================================
        // MAP
        // =====================================================================

        static readonly Color RugField = C(0x3F7378), RugCream = C(0xEFDDBA), RugRust = C(0xC2603F), RugEdge = C(0x2B4F53);

        public static void Map(Graphics g)
        {
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.SetClip(Defs.Map);
            Floor(g, 960, 800, 7);
            Walls(g);
            Runner(g);
            Light(g);
            Dock(g);
            foreach (var o in Defs.Obstacles) Furniture(g, o);
            Vignette(g);
            g.Restore(st);
        }

        static readonly Color[] woodTones = { C(0xDDB98C), C(0xD6B083), C(0xE1BF94), C(0xD2AA7C), C(0xD9B488), C(0xE4C59C) };

        static void Floor(Graphics g, float w, float h, int seed)
        {
            var rnd = new Random(seed);
            const float ph = 34;
            g.FillRectangle(Kit.B(C(0xAF8558)), 0, 0, w, h);
            var grain = Pn(Kit.A(C(0x9A6E43), 46), 1f);
            var grain2 = Pn(Kit.A(C(0xF6E3C6), 50), 1f);
            for (float y = 0; y < h; y += ph)
            {
                float x = -rnd.Next(0, 220);
                while (x < w)
                {
                    float len = 160 + rnd.Next(0, 180);
                    var col = woodTones[rnd.Next(woodTones.Length)];
                    var r = new RectangleF(x + 1, y + 1, len - 2, ph - 2);
                    g.FillRectangle(Kit.B(col), r);
                    g.FillRectangle(Kit.B(Kit.A(White, 34)), r.X, r.Y, r.Width, 2);
                    g.FillRectangle(Kit.B(Kit.A(C(0x7A5530), 26)), r.X, r.Bottom - 2, r.Width, 2);
                    int n = 2 + rnd.Next(3);
                    for (int i = 0; i < n; i++)
                    {
                        float gy = y + 6 + Rf(rnd, 0, ph - 12), d1 = Rf(rnd, -3, 3), d2 = Rf(rnd, -3, 3);
                        float x0 = r.X + Rf(rnd, 4, 30), x1 = r.Right - Rf(rnd, 4, 30);
                        g.DrawBezier(i % 2 == 0 ? grain : grain2, x0, gy, x0 + (x1 - x0) * 0.33f, gy + d1, x0 + (x1 - x0) * 0.66f, gy + d2, x1, gy + (d1 - d2) * 0.3f);
                    }
                    if (rnd.Next(7) == 0)
                    {
                        float kx = r.X + Rf(rnd, 20, Math.Max(21, r.Width - 20)), ky = y + ph / 2 + Rf(rnd, -5, 5);
                        E(g, Kit.A(C(0x8F643B), 60), kx, ky, 5, 2.6f);
                        g.DrawEllipse(grain, kx - 9, ky - 5, 18, 10);
                    }
                    x += len;
                }
            }
        }

        static void Walls(Graphics g)
        {
            // skirting boards along the top and bottom wall
            using (var lg = new LinearGradientBrush(new RectangleF(0, 8, 960, 18), Kit.A(Color.Black, 60), Kit.A(Color.Black, 0), 90f))
                g.FillRectangle(lg, 0, 9, 960, 16);
            g.FillRectangle(Kit.B(C(0xF3E9D8)), 0, 0, 960, 9);
            g.FillRectangle(Kit.B(C(0xD8C7AA)), 0, 7, 960, 2);
            using (var lg = new LinearGradientBrush(new RectangleF(0, 776, 960, 16), Kit.A(Color.Black, 0), Kit.A(Color.Black, 45), 90f))
                g.FillRectangle(lg, 0, 777, 960, 14);
            g.FillRectangle(Kit.B(C(0xF3E9D8)), 0, 791, 960, 9);
            g.FillRectangle(Kit.B(C(0xD8C7AA)), 0, 791, 960, 2);
        }

        static void StrokePath(Graphics g, GraphicsPath p, Color c, float w)
        {
            using (var pen = new Pen(c, w)) { pen.LineJoin = LineJoin.Round; g.DrawPath(pen, p); }
        }

        static void Runner(Graphics g)
        {
            var pts = Defs.Path;
            float w = Defs.PathWidth;
            using (var path = new GraphicsPath())
            {
                path.AddLines(pts);
                var st = g.Save();
                g.TranslateTransform(2, 5);
                StrokePath(g, path, Kit.A(Color.Black, 26), w + 8);
                StrokePath(g, path, Kit.A(Color.Black, 30), w + 2);
                g.Restore(st);
                StrokePath(g, path, RugEdge, w + 2);
                StrokePath(g, path, RugCream, w - 3);
                StrokePath(g, path, RugRust, w - 13);
                StrokePath(g, path, RugCream, w - 17);
                StrokePath(g, path, RugField, w - 21);
                using (var dash = new Pen(Kit.A(RugCream, 120), 1.2f)) { dash.DashPattern = new float[] { 2, 3 }; dash.LineJoin = LineJoin.Round; g.DrawPath(dash, path); }
            }
            // woven motifs along the centre line; rosettes on the corners
            var dia = Kit.B(RugCream);
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                float ax = pts[i].X, ay = pts[i].Y, bx = pts[i + 1].X, by = pts[i + 1].Y;
                float len = (float)Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                float ux = (bx - ax) / len, uy = (by - ay) / len;
                int n = (int)((len - 44) / 34);
                if (n < 1) continue;
                float step = (len - 44) / n;
                for (int j = 0; j <= n; j++)
                {
                    float d = 22 + j * step, cx = ax + ux * d, cy = ay + uy * d;
                    if (j % 2 == 0)
                    {
                        g.FillPolygon(dia, new[] { P(cx + ux * 6, cy + uy * 6), P(cx - uy * 6, cy + ux * 6), P(cx - ux * 6, cy - uy * 6), P(cx + uy * 6, cy - ux * 6) });
                        D(g, RugRust, cx, cy, 2.2f);
                    }
                    else
                    {
                        D(g, Kit.A(RugCream, 200), cx - uy * 7, cy + ux * 7, 1.6f);
                        D(g, Kit.A(RugCream, 200), cx + uy * 7, cy - ux * 7, 1.6f);
                        D(g, Kit.A(RugCream, 200), cx, cy, 1.6f);
                    }
                }
            }
            for (int i = 1; i + 1 < pts.Length; i++)
            {
                D(g, RugCream, pts[i].X, pts[i].Y, 9);
                D(g, RugField, pts[i].X, pts[i].Y, 6.5f);
                Star(g, RugCream, pts[i].X, pts[i].Y, 6);
                D(g, RugRust, pts[i].X, pts[i].Y, 2.4f);
            }
            // fringe at the bed end
            var end = pts[pts.Length - 1];
            var fr = Pn(C(0xF6EAD0), 1.6f);
            for (float fx = end.X - w / 2 + 3; fx <= end.X + w / 2 - 3; fx += 4)
                g.DrawLine(fr, fx, end.Y - 1, fx + ((int)fx % 3 - 1) * 0.8f, end.Y - 8);
        }

        static void Light(Graphics g)
        {
            // warm window light falling across the floor
            var b = Kit.B(Kit.A(C(0xFFF4D6), 26));
            g.FillPolygon(b, new[] { P(250, 190), P(400, 190), P(560, 560), P(410, 560) });
            g.FillPolygon(b, new[] { P(430, 190), P(520, 190), P(680, 560), P(590, 560) });
        }

        static void Vignette(Graphics g)
        {
            using (var p = new GraphicsPath())
            {
                p.AddEllipse(-260, -230, 1480, 1260);
                using (var pg = new PathGradientBrush(p))
                {
                    pg.CenterColor = Kit.A(Color.Black, 0);
                    pg.SurroundColors = new[] { Kit.A(C(0x3A2412), 90) };
                    pg.FocusScales = new PointF(0.62f, 0.62f);
                    g.FillRectangle(pg, 0, 0, 960, 800);
                }
            }
        }

        static void Dock(Graphics g)
        {
            float y = Defs.Path[0].Y;
            // docking plate on the runner
            Kit.FillRound(g, Kit.A(Color.Black, 40), new RectangleF(-6, y - 27, 62, 58), 8);
            Kit.FillRound(g, C(0xADB5BD), new RectangleF(-6, y - 29, 60, 58), 8);
            Kit.FillRound(g, C(0xCED4DA), new RectangleF(-6, y - 27, 58, 52), 7);
            for (int i = 0; i < 3; i++)
            {
                float cx = 26 + i * 9;
                g.DrawLines(Pn(Kit.A(C(0x868E96), 230), 2.4f), new[] { P(cx - 3, y - 7), P(cx + 2, y), P(cx - 3, y + 7) });
            }
            // the station itself
            Kit.Shadow(g, new RectangleF(-20, y - 40, 40, 80), 12, 70);
            ORect(g, C(0x3B4450), -20, y - 40, 40, 80, 12);
            Kit.FillRound(g, C(0x56616E), new RectangleF(-16, y - 35, 30, 70), 9);
            Kit.FillRound(g, Kit.A(White, 40), new RectangleF(-14, y - 33, 26, 8), 4);
            Poly(g, Kit.Amber, P(1, y - 16), P(-7, y + 2), P(-1, y + 2), P(-3, y + 16), P(6, y - 3), P(0, y - 3), P(3, y - 16));
            D(g, Kit.A(C(0x69DB7C), 90), 7, y - 26, 5);
            D(g, C(0x8CE99A), 7, y - 26, 2.4f);
            ORect(g, C(0xFFD43B), 19, y - 22, 5, 9, 2);
            ORect(g, C(0xFFD43B), 19, y + 13, 5, 9, 2);
        }

        static void Furniture(Graphics g, Obstacle o)
        {
            var r = o.R;
            switch (o.Kind)
            {
                case "knihovna": Bookshelf(g, r); break;
                case "kytka": Plant(g, r); break;
                case "stolek": Table(g, r); break;
                case "kreslo": Armchair(g, r); break;
                case "pohovka": Sofa(g, r); break;
                case "pelisek": Bed(g, r); break;
                default: Kit.Shadow(g, r, 8, 60); ORect(g, C(0xA0693F), r.X, r.Y, r.Width, r.Height, 8); break;
            }
        }

        static void Bookshelf(Graphics g, RectangleF r)
        {
            Kit.Shadow(g, r, 6, 90);
            ORect(g, C(0x7A4B2E), r.X, r.Y, r.Width, r.Height, 6);
            Kit.FillRound(g, C(0x93603C), new RectangleF(r.X + 4, r.Y + 4, r.Width - 8, r.Height - 10), 4);
            var gr = Pn(Kit.A(C(0x6B3F24), 120), 1);
            for (float gy = r.Y + 12; gy < r.Bottom - 8; gy += 9) g.DrawLine(gr, r.X + 8, gy, r.Right - 8, gy + 1);
            // stack of books
            Color[] bk = { C(0x4C6EF5), C(0xE8590C), C(0x2B8A3E), C(0xF2C14E) };
            for (int i = 0; i < 4; i++)
            {
                var st = g.Save();
                g.TranslateTransform(r.X + 40, r.Y + 28);
                g.RotateTransform(-14 + i * 9);
                ORect(g, bk[i], -24 + i * 1.5f, -14 + i * 2.5f, 44 - i * 4, 24 - i * 3, 2);
                g.FillRectangle(Kit.B(C(0xFBF6EA)), 16 - i * 2.5f, -12 + i * 2.5f, 3, 20 - i * 3);
                g.Restore(st);
            }
            // reading lamp
            OE(g, C(0xF8EDD8), r.X + 104, r.Y + 28, 17, 17);
            D(g, C(0xFFE8A3), r.X + 104, r.Y + 28, 11);
            D(g, C(0xFFF6D6), r.X + 104, r.Y + 28, 5);
            // succulent
            OE(g, C(0xD9825B), r.X + 146, r.Y + 28, 13, 13);
            for (int i = 0; i < 7; i++)
            {
                double a = i * Math.PI * 2 / 7;
                E(g, C(0x6FAE6A), r.X + 146 + (float)Math.Cos(a) * 6, r.Y + 28 + (float)Math.Sin(a) * 6, 5, 5);
            }
            D(g, C(0x8FCB86), r.X + 146, r.Y + 28, 4);
            // goldfish bowl
            OE(g, C(0xBFE4F5), r.X + 191, r.Y + 28, 19, 19);
            D(g, C(0x8FD0EE), r.X + 191, r.Y + 30, 14);
            E(g, C(0xFF922B), r.X + 188, r.Y + 31, 5, 3);
            Poly(g, C(0xFF922B), P(r.X + 193, r.Y + 31), P(r.X + 198, r.Y + 27), P(r.X + 198, r.Y + 35));
            D(g, Ink, r.X + 185.5f, r.Y + 30.5f, 0.9f);
            E(g, Kit.A(White, 150), r.X + 184, r.Y + 20, 6, 3);
        }

        static void Plant(Graphics g, RectangleF r)
        {
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            E(g, Kit.A(Color.Black, 50), cx + 3, cy + 7, 34, 32);
            OE(g, C(0xC8643C), cx, cy, 25, 25);
            D(g, C(0xE07B50), cx, cy, 22);
            D(g, C(0x5C3A23), cx, cy, 18);
            var rnd = new Random(3);
            for (int i = 0; i < 8; i++)
            {
                float a = i * 45 + Rf(rnd, -12, 12), len = Rf(rnd, 26, 34);
                var st = g.Save();
                g.TranslateTransform(cx, cy);
                g.RotateTransform(a);
                var leaf = i % 2 == 0 ? C(0x4E9A51) : C(0x5FAE5C);
                OE(g, leaf, len * 0.55f + 4, 0, len * 0.55f, 9.5f);
                L(g, Lt(leaf, 0.35f), 1.3f, 6, 0, len + 2, 0);
                for (int k = 1; k < 4; k++)
                {
                    float vx = 6 + k * len * 0.25f;
                    L(g, Kit.A(Lt(leaf, 0.3f), 200), 1, vx, 0, vx + 4, -5);
                    L(g, Kit.A(Lt(leaf, 0.3f), 200), 1, vx, 0, vx + 4, 5);
                }
                g.Restore(st);
            }
            D(g, C(0x6DBE67), cx, cy, 7);
        }

        static void Table(Graphics g, RectangleF r)
        {
            Kit.Shadow(g, r, 16, 90);
            ORect(g, C(0x9A6136), r.X, r.Y, r.Width, r.Height, 16);
            Kit.FillRound(g, C(0xB9804D), new RectangleF(r.X + 5, r.Y + 5, r.Width - 10, r.Height - 12), 12);
            var gr = Pn(Kit.A(C(0x8E5A30), 110), 1);
            for (float gx = r.X + 16; gx < r.Right - 12; gx += 11) g.DrawBezier(gr, gx, r.Y + 10, gx + 3, r.Y + 50, gx - 3, r.Y + 100, gx + 1, r.Bottom - 14);
            // open book
            var st = g.Save();
            g.TranslateTransform(r.X + 46, r.Y + 52);
            g.RotateTransform(-12);
            ORect(g, C(0x9C3D3D), -27, -19, 54, 38, 3);
            Kit.FillRound(g, C(0xFBF3E4), new RectangleF(-25, -17, 24, 34), 2);
            Kit.FillRound(g, C(0xF4EAD6), new RectangleF(1, -17, 24, 34), 2);
            for (int i = 0; i < 5; i++) { L(g, C(0xC9BCA4), 1, -21, -11 + i * 6, -5, -11 + i * 6); L(g, C(0xC9BCA4), 1, 5, -11 + i * 6, 21, -11 + i * 6); }
            g.Restore(st);
            // mug of cocoa
            float mx = r.X + 96, my = r.Y + 40;
            OE(g, C(0xF1F3F5), mx, my, 14, 14);
            OL(g, C(0xF1F3F5), 3.5f, mx + 13, my - 5, mx + 19, my + 3);
            D(g, C(0x7B4A2A), mx, my, 10.5f);
            E(g, Kit.A(C(0xC08552), 200), mx - 2, my - 2, 5, 3);
            // plate with cookies
            float px = r.X + 82, py = r.Y + 108;
            OE(g, C(0xF8F9FA), px, py, 24, 24);
            Kit.Ring(g, C(0xDEE2E6), 1.5f, px, py, 18);
            for (int i = 0; i < 3; i++)
            {
                float a = i * 2.1f + 0.4f, ckx = px + (float)Math.Cos(a) * 9, cky = py + (float)Math.Sin(a) * 9;
                OE(g, C(0xD9A35B), ckx, cky, 7, 7);
                D(g, C(0x5C3A23), ckx - 2, cky - 1, 1.3f); D(g, C(0x5C3A23), ckx + 2, cky + 2, 1.2f); D(g, C(0x5C3A23), ckx + 2.5f, cky - 2.5f, 1);
            }
            // a ball of yarn somebody forgot
            YarnBall(g, r.X + 30, r.Y + 118, 10, C(0x7048E8));
        }

        static void YarnBall(Graphics g, float x, float y, float r, Color c)
        {
            OE(g, c, x, y, r, r);
            var p = Pn(Lt(c, 0.35f), Math.Max(1f, r * 0.13f));
            g.DrawArc(p, x - r * 0.8f, y - r * 1.1f, r * 1.6f, r * 1.8f, 100, 140);
            g.DrawArc(p, x - r * 0.3f, y - r * 0.9f, r * 1.4f, r * 1.6f, 120, 130);
            g.DrawArc(p, x - r * 1.0f, y - r * 0.4f, r * 1.7f, r * 1.2f, 250, 120);
            var dk = Pn(Dk(c, 0.3f), Math.Max(1f, r * 0.11f));
            g.DrawArc(dk, x - r * 0.9f, y - r * 0.2f, r * 1.4f, r * 1.0f, 10, 110);
        }

        static void Armchair(Graphics g, RectangleF r)
        {
            Color c = C(0xD99A34), c2 = C(0xEDB651), cd = C(0xB97C22);
            Kit.Shadow(g, r, 14, 90);
            ORect(g, cd, r.X, r.Y, r.Width, r.Height, 14);
            Kit.FillRound(g, c, new RectangleF(r.X + 3, r.Y + 3, r.Width - 6, 24), 11);
            ORect(g, c, r.X + 3, r.Y + 20, 17, r.Height - 25, 8);
            ORect(g, c, r.Right - 20, r.Y + 20, 17, r.Height - 25, 8);
            ORect(g, c2, r.X + 22, r.Y + 27, r.Width - 44, r.Height - 33, 9);
            Kit.FillRound(g, Kit.A(White, 45), new RectangleF(r.X + 26, r.Y + 30, r.Width - 52, 10), 5);
            D(g, cd, r.X + r.Width / 2, r.Y + 13, 2); D(g, cd, r.X + r.Width / 2 - 18, r.Y + 13, 2); D(g, cd, r.X + r.Width / 2 + 18, r.Y + 13, 2);
            // knitted cushion
            var st = g.Save();
            g.TranslateTransform(r.X + r.Width / 2 + 2, r.Y + 62);
            g.RotateTransform(18);
            ORect(g, C(0xC2603F), -14, -11, 28, 22, 7);
            for (int i = -1; i <= 1; i++) L(g, C(0xE07B5B), 1.4f, i * 7, -8, i * 7, 8);
            g.Restore(st);
        }

        static void Sofa(Graphics g, RectangleF r)
        {
            Color c = C(0xB9654A), c2 = C(0xCF7A5D), cd = C(0x94503A);
            Kit.Shadow(g, r, 14, 90);
            ORect(g, cd, r.X, r.Y, r.Width, r.Height, 14);
            Kit.FillRound(g, c, new RectangleF(r.X + 3, r.Bottom - 26, r.Width - 6, 23), 10);
            ORect(g, c, r.X + 3, r.Y + 3, 20, r.Height - 6, 9);
            ORect(g, c, r.Right - 23, r.Y + 3, 20, r.Height - 6, 9);
            float sw = (r.Width - 52) / 2;
            ORect(g, c2, r.X + 26, r.Y + 6, sw - 2, r.Height - 33, 8);
            ORect(g, c2, r.X + 28 + sw, r.Y + 6, sw - 2, r.Height - 33, 8);
            Kit.FillRound(g, Kit.A(White, 40), new RectangleF(r.X + 30, r.Y + 9, sw - 10, 8), 4);
            Kit.FillRound(g, Kit.A(White, 40), new RectangleF(r.X + 32 + sw, r.Y + 9, sw - 10, 8), 4);
            // pillows leaning on the back
            var st = g.Save();
            g.TranslateTransform(r.X + 44, r.Bottom - 26);
            g.RotateTransform(-10);
            ORect(g, C(0xF3E3C3), -15, -10, 30, 20, 8);
            D(g, C(0xD8C39A), 0, 0, 2);
            g.Restore(st);
            st = g.Save();
            g.TranslateTransform(r.Right - 46, r.Bottom - 26);
            g.RotateTransform(12);
            ORect(g, RugField, -15, -10, 30, 20, 8);
            Star(g, RugCream, 0, 0, 5);
            g.Restore(st);
        }

        static void Bed(Graphics g, RectangleF r)
        {
            float cx = r.X + 43, cy = r.Y + 50;
            E(g, Kit.A(Color.Black, 55), cx + 3, cy + 7, 46, 45);
            OE(g, C(0xC97B6B), cx, cy, 43, 43);
            D(g, C(0xE2958A), cx, cy - 1, 40);
            E(g, Kit.A(White, 45), cx - 12, cy - 24, 18, 8);
            OE(g, C(0xFFF1E0), cx, cy + 2, 27, 26);
            // paw prints on the cushion
            for (int i = 0; i < 3; i++)
            {
                float px = cx - 12 + i * 12, py = cy + 4 + (i == 1 ? -9 : 0);
                Paw(g, Kit.A(C(0xE2958A), 170), px, py, 3.2f);
            }
            // toy mouse
            E(g, C(0xADB5BD), cx + 14, cy + 15, 6, 4);
            D(g, C(0xF7A1B5), cx + 18, cy + 12.5f, 1.6f);
            L(g, C(0x868E96), 1, cx + 8, cy + 16, cx + 3, cy + 20);
            // fish-shaped food bowl
            FishBowl(g, r.Right - 10, r.Bottom - 16);
        }

        static void Paw(Graphics g, Color c, float x, float y, float s)
        {
            E(g, c, x, y + s * 0.5f, s * 1.1f, s * 0.9f);
            D(g, c, x - s * 1.15f, y - s * 0.6f, s * 0.45f);
            D(g, c, x - s * 0.4f, y - s * 1.2f, s * 0.45f);
            D(g, c, x + s * 0.4f, y - s * 1.2f, s * 0.45f);
            D(g, c, x + s * 1.15f, y - s * 0.6f, s * 0.45f);
        }

        static void FishBowl(Graphics g, float x, float y)
        {
            E(g, Kit.A(Color.Black, 50), x + 2, y + 4, 24, 12);
            Color c = C(0x4D9DE0);
            OPoly(g, c, P(x + 12, y), P(x + 24, y - 10), P(x + 21, y), P(x + 24, y + 10));
            OE(g, c, x - 2, y, 17, 11);
            E(g, C(0x2F6FA8), x - 3, y, 12, 7);
            var rnd = new Random(5);
            for (int i = 0; i < 9; i++) OE(g, C(0xC27C3A), x - 3 + Rf(rnd, -8, 8), y + Rf(rnd, -3.5f, 3.5f), 2.2f, 1.6f);
            D(g, White, x - 14, y - 2, 2); D(g, Ink, x - 14, y - 2, 1);
        }

        // =====================================================================
        // CATS (local space: facing +x, footprint ~ Defs.CatRadius)
        // =====================================================================

        static readonly Color Pink = C(0xF59FB0), Nose = C(0xE8778F), Gold = C(0xFFD43B), GoldDk = C(0xC98A0E);

        // motif colour of every upgrade path [kind, path]: legend glow ring and upgrade icon background
        static readonly int[,] pathCols =
        {
            { 0xF76707, 0x4DABF7, 0x40C057 },   // Drápka: claws, speed, teeth
            { 0x94D82D, 0xFAB005, 0xF03E3E },   // Chlupáč: hairballs, sights, sniper
            { 0xE64980, 0xFD7E14, 0x74C0FC },   // Klubíčkářka: blast, throw, knitting
            { 0xFF3B3B, 0xCC5DE8, 0xFF8C1A },   // Laserovka: beam, optics, heat
            { 0x74C0FC, 0x9775FA, 0xF783AC },   // Spac: sleep, cushion, comfort
            { 0xFCC419, 0xFF8787, 0x4DABF7 },   // Pamlskárna: stock, care, delivery
        };

        static Color PathCol(TowerKind k, int p) { return C(pathCols[(int)k, p]); }
        static int Tier(int[] up, int p) { return up == null || p >= up.Length ? 0 : Math.Max(0, Math.Min(Defs.TierCount, up[p])); }

        public static void Cat(Graphics g, TowerKind k, int[] up, float x, float y, float angle, float attack, double t)
        {
            bool rot = k != TowerKind.Spac && k != TowerKind.Pamlskarna;
            int ab = rot ? AngleBucket(angle, CatAngles) : 0;
            // pose: 0 still, 1 / 2 tail swung left / right, 3 / 4 attack half / full
            int pose = attack > 0.55f ? 4 : attack > 0.15f ? 3 : 0;
            if (pose == 0)
            {
                int fr = (int)(((long)Math.Floor(t * 2.5 + x * 0.031 + y * 0.017) % 4 + 4) % 4);
                pose = fr == 1 ? 1 : fr == 3 ? 2 : 0;
            }
            int a = Tier(up, 0), b = Tier(up, 1), c = Tier(up, 2), top = Math.Max(a, Math.Max(b, c));
            int sq = Dev(g, x, y);
            long baseKey = Key(1, sq, ((long)k << 44) + ((long)a << 40) + ((long)b << 36) + ((long)c << 32));
            long key = baseKey + (ab << 8) + pose;
            var s = Find(key);
            if (s == null && OverBudget())
            {
                for (int d = 1; d <= 3 && s == null; d++)
                    for (int sg = -1; sg <= 1 && s == null; sg += 2)
                        s = Find(baseKey + ((((ab + sg * d) % CatAngles + CatAngles) % CatAngles) << 8) + pose);
                if (s == null) s = Find(baseKey + (ab << 8));
            }
            if (s == null)
            {
                budgetUsed += top >= 4 ? 2 : 1;   // tier 4-5 sprites cost about twice as much to paint
                using (var cg = Begin(key, CatHalf(top), sq, out s))
                    PaintCat(cg, k, a, b, c, (float)(ab * 2 * Math.PI / CatAngles), pose == 1 ? -0.9f : pose == 2 ? 0.9f : 0, pose == 4 ? 1 : pose == 3 ? 0.5f : 0);
            }
            Blit(g, s);
        }

        const int CatAngles = 32;
        // sprite half-size by the highest tier: measured painted extent + 1 px (claws in attack, T4 manes, T5 glow rings)
        static int CatHalf(int top) { return top >= 5 ? 49 : top >= 4 ? 43 : top >= 2 ? 39 : 36; }

        public static void CatIcon(Graphics g, TowerKind k, RectangleF r)
        {
            int side = Math.Max(8, (int)Math.Round(Math.Min(r.Width, r.Height)));
            int sq = Dev(g, r.X + r.Width / 2, r.Y + r.Height / 2);
            long key = Key(2, sq, ((long)k << 40) + side);
            var s = Find(key);
            if (s == null)
            {
                using (var cg = Begin(key, side / 2 + 1, sq, out s))
                {
                    float sc = side / 56f;
                    cg.ScaleTransform(sc, sc);
                    bool rot = k != TowerKind.Spac && k != TowerKind.Pamlskarna;
                    if (rot) cg.TranslateTransform(0, 3);
                    PaintCat(cg, k, 0, 0, 0, rot ? -(float)Math.PI / 2 : 0, 0.5f, 0);
                }
            }
            Blit(g, s);
        }

        static PointF Pol(float cx, float cy, float r, float deg)
        {
            double a = deg / Deg;
            return new PointF(cx + r * (float)Math.Cos(a), cy + r * (float)Math.Sin(a));
        }

        static void PaintCat(Graphics g, TowerKind k, int a, int b, int c, float ang, float sway, float atk)
        {
            var d = Defs.Tower(k);
            Color fur = C(d.Fur), fur2 = C(d.Fur2);
            int top = Math.Max(a, Math.Max(b, c));
            int legend = a >= 5 ? 0 : b >= 5 ? 1 : c >= 5 ? 2 : -1;
            if (legend >= 0) LegendRing(g, PathCol(k, legend), sway);
            var st = g.Save();
            if (top >= 4) { float s = top >= 5 ? 1.14f : 1.06f; g.ScaleTransform(s, s); }
            if (k == TowerKind.Spac)
            {
                E(g, Kit.A(Color.Black, 30), 2, 6, 25, 22); E(g, Kit.A(Color.Black, 40), 2, 5, 21, 18);
                Spac(g, fur, fur2, a, b, c, sway);
            }
            else if (k == TowerKind.Pamlskarna)
            {
                E(g, Kit.A(Color.Black, 30), 3, 17, 29, 11); E(g, Kit.A(Color.Black, 40), 3, 17, 24, 8);
                Pamlskarna(g, fur, fur2, a, b, c, sway);
            }
            else
            {
                E(g, Kit.A(Color.Black, 28), 2.5f, 5, 23, 20);
                E(g, Kit.A(Color.Black, 38), 2, 4, 18, 15);
                g.RotateTransform(ang * Deg);
                switch (k)
                {
                    case TowerKind.Drapka: Drapka(g, fur, fur2, a, b, c, sway, atk); break;
                    case TowerKind.Chlupac: Chlupac(g, fur, fur2, a, b, c, sway, atk); break;
                    case TowerKind.Klubickarka: Klubickarka(g, fur, fur2, a, b, c, sway, atk); break;
                    default: Laserovka(g, fur, fur2, a, b, c, sway, atk); break;
                }
            }
            g.Restore(st);
        }

        // tier 5 "legend": soft glow in the path colour under the cat, a bright ring and four sparkles
        static void LegendRing(Graphics g, Color c, float sway)
        {
            D(g, Al(c, 0.3f), 0, 1, 30);
            g.DrawEllipse(Pn(Al(Dk(c, 0.3f), 0.9f), 3.6f), -28.5f, -27.5f, 57, 57);
            g.DrawEllipse(Pn(Lt(c, 0.5f), 1.6f), -28.5f, -27.5f, 57, 57);
            for (int i = 0; i < 4; i++)
            {
                var p = Pol(0, 1, 28.5f, 45 + i * 90 + sway * 10);
                Star(g, Ink, p.X, p.Y, 6.2f);
                Star(g, Lt(c, 0.75f), p.X, p.Y, 4.6f);
            }
        }

        // --- shared top-down parts ---

        static void Tail(Graphics g, bool ink, Color c, float w, float x0, float len, float sway)
        {
            SBez(g, ink, c, w, P(x0, 0), P(x0 - len * 0.55f, 0), P(x0 - len * 0.95f, len * 0.35f * sway), P(x0 - len * 0.8f, len * 0.7f * sway + len * 0.12f));
        }

        static PointF TailTip(float x0, float len, float sway) { return P(x0 - len * 0.8f, len * 0.7f * sway + len * 0.12f); }

        static void Ears(Graphics g, bool ink, Color fur, Color inner, float hx, float hr, float size)
        {
            for (int s = -1; s <= 1; s += 2)
            {
                PointF p1 = Pol(hx, 0, hr * 0.9f, s * 60), p2 = Pol(hx, 0, hr * 0.9f, s * 132), tip = Pol(hx, 0, hr + size, s * 100);
                SPoly(g, ink, fur, p1, tip, p2);
                if (!ink && inner.A > 0)
                {
                    float cx = (p1.X + p2.X + tip.X) / 3, cy = (p1.Y + p2.Y + tip.Y) / 3;
                    Poly(g, inner, Lerp(p1, cx, cy, 0.45f), Lerp(tip, cx, cy, 0.35f), Lerp(p2, cx, cy, 0.45f));
                }
            }
        }

        static PointF Lerp(PointF p, float cx, float cy, float t) { return P(p.X + (cx - p.X) * t, p.Y + (cy - p.Y) * t); }

        static void Face(Graphics g, Color muzzle, float hx, float hr, Color iris)
        {
            E(g, muzzle, hx + hr * 0.6f, 0, hr * 0.38f, hr * 0.5f);
            float ex = hx + hr * 0.42f, ey = hr * 0.43f, er = hr * 0.21f;
            for (int s = -1; s <= 1; s += 2)
            {
                if (iris.A > 0) { D(g, Ink, ex, s * ey, er + 0.5f); D(g, iris, ex, s * ey, er); D(g, Ink, ex + 0.3f, s * ey, er * 0.5f); }
                else D(g, Ink, ex, s * ey, er);
                D(g, White, ex - er * 0.35f, s * ey - er * 0.35f, er * 0.38f);
                var wp = Pn(Kit.A(Ink, 170), 0.7f);
                g.DrawLine(wp, hx + hr * 0.78f, s * hr * 0.28f, hx + hr * 1.2f, s * hr * 0.9f);
                g.DrawLine(wp, hx + hr * 0.82f, s * hr * 0.2f, hx + hr * 1.32f, s * hr * 0.55f);
            }
            Poly(g, Nose, P(hx + hr * 0.8f, -hr * 0.14f), P(hx + hr * 0.8f, hr * 0.14f), P(hx + hr * 0.97f, 0));
        }

        static void Goggles(Graphics g, float hx, float hr, Color lens)
        {
            float ex = hx + hr * 0.42f, ey = hr * 0.45f, er = hr * 0.34f;
            OL(g, C(0x343A40), 1.6f, hx - hr * 0.1f, -hr * 0.98f, hx - hr * 0.1f, hr * 0.98f);
            for (int s = -1; s <= 1; s += 2)
            {
                OE(g, C(0x495057), ex, s * ey, er, er);
                D(g, lens, ex, s * ey, er * 0.68f);
                D(g, Kit.A(White, 200), ex - er * 0.25f, s * ey - er * 0.25f, er * 0.25f);
            }
            L(g, C(0x495057), 1.6f, ex, -ey + er * 0.5f, ex, ey - er * 0.5f);
        }

        static void Claws(Graphics g, float px, float py, float pr, float len, bool outline, Color c)
        {
            for (int i = -1; i <= 1; i++)
            {
                float cy = py + i * pr * 0.5f;
                var pts = new[] { P(px + pr * 0.7f, cy - 0.9f), P(px + pr * 0.7f + len, cy + i * 0.6f), P(px + pr * 0.7f, cy + 0.9f) };
                if (outline) OPoly(g, c, pts); else Poly(g, c, pts);
            }
        }

        // teardrop flame: base at (x, y), tip towards deg; a < 1 fades it (effects)
        static void Flame(Graphics g, float x, float y, float s, float deg, bool outline, float a)
        {
            var st = g.Save();
            g.TranslateTransform(x, y);
            g.RotateTransform(deg);
            var outer = new[] { P(-s * 0.75f, 0), P(-s * 0.45f, -s * 0.58f), P(s * 0.35f, -s * 0.5f), P(s * 1.6f, 0), P(s * 0.35f, s * 0.5f), P(-s * 0.45f, s * 0.58f) };
            if (outline) g.DrawPolygon(Pn(Al(Ink, a), O * 2), outer);
            g.FillPolygon(Kit.B(Al(C(0xFF6B00), a)), outer);
            g.FillPolygon(Kit.B(Al(C(0xFFA94D), a)), new[] { P(-s * 0.5f, 0), P(-s * 0.25f, -s * 0.36f), P(s * 0.3f, -s * 0.3f), P(s * 1.15f, 0), P(s * 0.3f, s * 0.3f), P(-s * 0.25f, s * 0.36f) });
            g.FillPolygon(Kit.B(Al(C(0xFFE066), a)), new[] { P(-s * 0.3f, 0), P(0, -s * 0.2f), P(s * 0.65f, 0), P(0, s * 0.2f) });
            g.Restore(st);
        }

        // radial web: 8 spokes and sagging rings on an ellipse rx × ry
        static void WebShape(Graphics g, float cx, float cy, float rx, float ry, Color c, float w, int rings)
        {
            var pen = Pn(c, w);
            for (int i = 0; i < 8; i++)
            {
                double a = (i * 45 + 22.5) / Deg;
                g.DrawLine(pen, cx, cy, cx + rx * (float)Math.Cos(a), cy + ry * (float)Math.Sin(a));
            }
            for (int k = 1; k <= rings; k++)
            {
                float f = k / (float)rings * 0.92f;
                var pts = new PointF[16];
                for (int i = 0; i < 16; i++)
                {
                    double a = (i * 22.5 + 22.5) / Deg;
                    float sag = i % 2 == 0 ? 1 : 0.86f;
                    pts[i] = P(cx + rx * f * sag * (float)Math.Cos(a), cy + ry * f * sag * (float)Math.Sin(a));
                }
                g.DrawPolygon(pen, pts);
            }
        }

        static void Snowflake(Graphics g, Color c, float x, float y, float r, float w)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var pen = pass == 0 ? Pn(Ink, w + O * 1.6f) : Pn(c, w);
                for (int i = 0; i < 6; i++)
                {
                    var tip = Pol(x, y, r, i * 60 - 90);
                    g.DrawLine(pen, x, y, tip.X, tip.Y);
                    if (r < 6) continue;   // branches would not show at this size
                    var m = Pol(x, y, r * 0.58f, i * 60 - 90);
                    var b1 = Pol(m.X, m.Y, r * 0.32f, i * 60 - 90 - 50);
                    var b2 = Pol(m.X, m.Y, r * 0.32f, i * 60 - 90 + 50);
                    g.DrawLine(pen, m.X, m.Y, b1.X, b1.Y);
                    g.DrawLine(pen, m.X, m.Y, b2.X, b2.Y);
                }
            }
        }

        static void DiscoBall(Graphics g, float x, float y, float r)
        {
            OE(g, C(0xCED4DA), x, y, r, r);
            var gp = Pn(C(0x868E96), Math.Max(0.7f, r * 0.1f));
            for (int i = -2; i <= 2; i++)
            {
                float d = i * r * 0.38f, h = (float)Math.Sqrt(Math.Max(0, r * r - d * d));
                g.DrawLine(gp, x + d, y - h, x + d, y + h);
                g.DrawLine(gp, x - h, y + d, x + h, y + d);
            }
            g.FillRectangle(Kit.B(C(0xA5D8FF)), x + r * 0.05f, y + r * 0.05f, r * 0.33f, r * 0.33f);
            g.FillRectangle(Kit.B(C(0xF783AC)), x - r * 0.72f, y + r * 0.42f, r * 0.33f, r * 0.3f);
            E(g, Kit.A(White, 190), x - r * 0.35f, y - r * 0.38f, r * 0.3f, r * 0.2f);
        }

        static void SunDisc(Graphics g, float x, float y, float r, float spin)
        {
            for (int i = 0; i < 12; i++)
            {
                float a = i * 30 + spin * 8;
                OPoly(g, i % 2 == 0 ? Gold : C(0xFFA94D), Pol(x, y, r * 0.85f, a - 9), Pol(x, y, r * (i % 2 == 0 ? 1.5f : 1.32f), a), Pol(x, y, r * 0.85f, a + 9));
            }
            OE(g, Gold, x, y, r, r);
            D(g, C(0xFFF3BF), x - r * 0.15f, y - r * 0.15f, r * 0.6f);
        }

        static void Lightning(Graphics g, Color c, float x, float y, float s)
        {
            OPoly(g, c, P(x + 0.06f * s, y - s), P(x - 0.44f * s, y + 0.12f * s), P(x - 0.06f * s, y + 0.12f * s), P(x - 0.19f * s, y + s),
                P(x + 0.38f * s, y - 0.19f * s), P(x, y - 0.19f * s), P(x + 0.19f * s, y - s));
        }

        // --- Drápka: sleek orange tabby, paws out ready to swipe. Paths: claws (mane, sabre) / speed (wind) / teeth (to a dragon) ---
        static void Drapka(Graphics g, Color fur, Color fur2, int a, int b, int c, float sway, float atk)
        {
            float hx = 6.5f, hr = 10.5f;
            float pr = a >= 4 ? 6.2f : a >= 3 ? 5.6f : a >= 2 ? 4.8f : 4.2f;
            float extR = atk * 9, extL = a >= 2 ? atk * 6 : 0;
            Color stripe = a >= 3 ? C(0x4A2511) : C(0xC0621B);
            Color mane = a >= 5 ? C(0xF59F00) : C(0xD9480F);
            if (b >= 3) Whirl(g, b, sway);
            if (c >= 5) Wings(g, C(0x2B8A3E), C(0x8CE99A), sway);
            for (int pass = 0; pass < 2; pass++)
            {
                bool ink = pass == 0;
                Tail(g, ink, fur, 4.6f, -16, 16, sway);
                SE(g, ink, fur, -6, 0, 13.5f, 10.5f);
                SE(g, ink, fur, 14 + extL, -8.5f, pr, pr * 0.85f);
                SE(g, ink, fur, 14 + extR, 8.5f, pr, pr * 0.85f);
                if (a >= 4)
                    for (int i = 0; i < 9; i++) { var p = Pol(hx - 1.5f, 0, hr + 0.5f, 100 + i * 20); SE(g, ink, i % 2 == 0 ? mane : Dk(mane, 0.18f), p.X, p.Y, 4.9f, 4.9f); }
                if (c >= 5)
                    for (int s = -1; s <= 1; s += 2) SPoly(g, ink, Gold, Pol(hx, 0, hr * 0.7f, s * 128), Pol(hx - 2, 0, hr + 9, s * 152), Pol(hx, 0, hr * 0.7f, s * 162));
                SE(g, ink, fur, hx, 0, hr, hr);
                Ears(g, ink, fur, Pink, hx, hr, 6.5f);
            }
            var tp = TailTip(-16, 16, sway);
            D(g, a >= 5 ? Gold : stripe, tp.X, tp.Y, 2.3f);
            int ns = a >= 3 ? 4 : 3;
            for (int i = 0; i < ns; i++)
            {
                float sx = -14 + i * (a >= 3 ? 4.2f : 5.2f);
                L(g, stripe, a >= 3 ? 2.6f : 2.2f, sx, -8.5f, sx + 1.8f, -3.5f);
                L(g, stripe, a >= 3 ? 2.6f : 2.2f, sx, 8.5f, sx + 1.8f, 3.5f);
            }
            L(g, stripe, 1.8f, hx - 6.5f, -2.2f, hx - 2.5f, -1.5f);
            L(g, stripe, 1.8f, hx - 7f, 0, hx - 2.5f, 0);
            L(g, stripe, 1.8f, hx - 6.5f, 2.2f, hx - 2.5f, 1.5f);
            if (c == 3 || c >= 5)
                for (int i = 0; i < 5; i++) { float sx = -18 + i * 3.7f; OPoly(g, c >= 5 ? Gold : C(0x66A80F), P(sx - 1.7f, 0), P(sx + 0.4f, -2.4f), P(sx + 2.3f, 0), P(sx + 0.4f, 2.4f)); }
            if (c == 4)
            {
                OL(g, C(0x343A40), 2.4f, -2.5f, -9.6f, -2.5f, 9.6f);
                for (int i = -2; i <= 2; i++) OPoly(g, White, P(-3.4f, i * 3.8f - 1.6f), P(-8.6f, i * 3.8f), P(-3.4f, i * 3.8f + 1.6f));
            }
            D(g, fur2, 14 + extL + pr * 0.35f, -8.5f, pr * 0.5f);
            D(g, fur2, 14 + extR + pr * 0.35f, 8.5f, pr * 0.5f);
            if (a >= 1)
            {
                float len = a >= 5 ? 9 : a >= 3 ? 6.5f : a >= 2 ? 3.6f : 2.6f;
                Color cl = a >= 5 ? Gold : C(0xF8F9FA);
                Claws(g, 14 + extL, -8.5f, pr, len, a >= 3, cl);
                Claws(g, 14 + extR, 8.5f, pr, len, a >= 3, cl);
            }
            if (a >= 2)
            {
                Color band = a >= 5 ? Gold : C(0xE03131);
                OE(g, band, 14 + extL - pr * 0.8f, -8.5f, 1.6f, pr * 0.75f);
                OE(g, band, 14 + extR - pr * 0.8f, 8.5f, 1.6f, pr * 0.75f);
            }
            Face(g, fur2, hx, hr, c >= 5 ? Gold : Color.Empty);
            if (c >= 1)
            {
                Color wc = c >= 5 ? Gold : White;
                for (int s = -1; s <= 1; s += 2)
                {
                    OL(g, wc, 0.9f, hx + hr * 0.8f, s * hr * 0.3f, hx + hr * 1.95f, s * hr * 1.25f);
                    OL(g, wc, 0.9f, hx + hr * 0.85f, s * hr * 0.18f, hx + hr * 2.15f, s * hr * 0.62f);
                }
            }
            if (a >= 5)
                for (int s = -1; s <= 1; s += 2) OPoly(g, White, P(hx + hr * 0.78f, s * 1.0f), P(hx + hr + 8, s * 3.4f), P(hx + hr * 0.84f, s * 3.8f));
            else if (c >= 2)
            {
                float fl = c >= 4 ? 4 : 2.4f;
                for (int s = -1; s <= 1; s += 2) OPoly(g, White, P(hx + hr * 0.86f, s * 1.3f), P(hx + hr + fl, s * 2.4f), P(hx + hr * 0.88f, s * 3.4f));
            }
            if (b >= 2) Goggles(g, hx, hr, C(0x8CE99A));
            if (b >= 1)
            {
                Color hb = b >= 4 ? C(0x1C7ED6) : C(0xE03131);
                OL(g, hb, 2.6f, hx - 3.2f, -hr + 0.6f, hx - 3.2f, hr - 0.6f);
                OL(g, hb, 1.8f, hx - 4, hr - 1, hx - 10, hr + 3.5f);
                OL(g, hb, 1.8f, hx - 4, hr - 1, hx - 11, hr - 0.5f);
            }
        }

        static void Whirl(Graphics g, int b, float sway)
        {
            int n = b >= 5 ? 5 : b >= 4 ? 4 : 3;
            float w = b >= 4 ? 3.2f : 2.6f;
            for (int i = 0; i < n; i++)
            {
                float st = sway * 40 + i * 360f / n, R = b >= 5 ? 21 + i * 2.4f : 27;
                g.DrawArc(Pn(Kit.A(White, 170), w), -R, -R, R * 2, R * 2, st, 75);
                g.DrawArc(Pn(Kit.A(C(0xA5D8FF), 180), w * 0.6f), -R + 4, -R + 4, R * 2 - 8, R * 2 - 8, st + 40, 55);
            }
            if (b >= 4)
                for (int i = 0; i < 3; i++)
                {
                    g.DrawArc(Pn(Kit.A(Ink, 120), 4.2f), -31.5f, -31.5f, 63, 63, sway * -30 + i * 120 + 60, 50);
                    g.DrawArc(Pn(C(0x339AF0), 2.4f), -31.5f, -31.5f, 63, 63, sway * -30 + i * 120 + 60, 50);
                }
            if (b >= 5)
                for (int i = 0; i < 5; i++) { var p = Pol(0, 0, 25 + (i % 2) * 4, i * 72 + 20 + sway * 25); OE(g, i % 2 == 0 ? C(0x8CE99A) : C(0xE9ECEF), p.X, p.Y, 1.7f, 1.7f); }
        }

        static void Wings(Graphics g, Color bone, Color membrane, float sway)
        {
            float f = 1 + sway * 0.06f;
            for (int s = -1; s <= 1; s += 2)
            {
                OPoly(g, membrane, P(-1, s * 6), P(-5, s * 21 * f), P(-10, s * 26 * f), P(-12, s * 19 * f), P(-17, s * 22 * f), P(-18, s * 14 * f), P(-23, s * 15 * f), P(-15, s * 5));
                L(g, bone, 2.4f, -1, s * 6, -10, s * 26 * f);
                L(g, bone, 1.6f, -4, s * 6, -17, s * 22 * f);
                L(g, bone, 1.6f, -7, s * 6, -23, s * 15 * f);
            }
        }

        // --- Chlupáč: fluffy grey long-hair that spits hairballs. Paths: hairballs / sights (gatling) / sniper (rifle) ---
        static void Chlupac(Graphics g, Color fur, Color fur2, int a, int b, int c, float sway, float atk)
        {
            float hx = 6 + atk * 2, hr = 11;
            Color light = a >= 5 ? Kit.Mix(Lt(fur, 0.35f), C(0xC0EB75), 0.45f) : Lt(fur, 0.35f);
            Color ball = a >= 2 ? C(0x94D82D) : C(0xDEE2E6);
            if (a >= 5)
                for (int i = 0; i < 8; i++) { var p = Pol(0, 0, 27, i * 45 + 10 + sway * 12); OE(g, i % 2 == 0 ? ball : C(0xC0EB75), p.X, p.Y, 3.6f, 3.6f); D(g, Kit.A(White, 150), p.X - 1.2f, p.Y - 1.2f, 1.1f); }
            else if (a >= 1)
            {
                for (int s = -1; s <= (a >= 4 ? 1 : -1); s += 2)
                {
                    OE(g, ball, -9, s * 15.5f, 3.4f, 3.4f);
                    OE(g, ball, -2.5f, s * 16.5f, 3.6f, 3.6f);
                    if (a >= 2) { OE(g, ball, -6, s * 20.5f, 3, 3); E(g, C(0xC0EB75), -2, s * 19.5f, 1.6f, 2.2f); D(g, C(0xC0EB75), 0.5f, s * 12.5f, 1.2f); }
                }
            }
            for (int pass = 0; pass < 2; pass++)
            {
                bool ink = pass == 0;
                Tail(g, ink, fur, 7.5f, -16, 15, sway);
                SE(g, ink, fur, -6, 0, 13, 11);
                for (int i = 0; i < 9; i++)
                {
                    var p = Pol(-6, 0, 1, 75 + i * 26.25f);
                    SE(g, ink, fur, -6 + (p.X + 6) * 13.2f, p.Y * 11.2f, 3.6f, 3.6f);
                }
                if (a >= 3)
                {
                    int n = a >= 4 ? 14 : 12;
                    for (int i = 0; i < n; i++) { var p = Pol(hx, 0, hr + (a >= 4 ? 1.6f : 0.5f), i * 360f / n + 15); SE(g, ink, light, p.X, p.Y, a >= 4 ? 5 : 4.4f, a >= 4 ? 5 : 4.4f); }
                }
                SE(g, ink, fur, hx, 0, hr, hr);
                SE(g, ink, fur, hx + 1.5f, -hr + 1.2f, 4, 3.4f);
                SE(g, ink, fur, hx + 1.5f, hr - 1.2f, 4, 3.4f);
                Ears(g, ink, fur, Pink, hx, hr, 5);
            }
            var tp = TailTip(-16, 15, sway);
            D(g, light, tp.X, tp.Y, 3);
            E(g, light, -8, 0, 7, 5.5f);
            D(g, light, hx - 5, 0, 3); D(g, light, hx - 7, -2.5f, 2.2f); D(g, light, hx - 7, 2.5f, 2.2f);
            if (c >= 5)
                for (int i = 0; i < 3; i++)
                {
                    var st = g.Save(); g.TranslateTransform(-14 + i * 5, i == 1 ? -5 : 4); g.RotateTransform(i * 50 - 40);
                    OE(g, i == 1 ? C(0x5C940D) : C(0x74B816), 0, 0, 4.2f, 2.2f); L(g, C(0x2B8A3E), 0.9f, -3.5f, 0, 3.5f, 0);
                    g.Restore(st);
                }
            Face(g, fur2, hx, hr, Color.Empty);
            if (atk > 0 && b < 4)
            {
                OE(g, C(0x862E3B), hx + hr * 0.82f, 0, 1.6f + atk * 1.8f, 2 + atk * 1.6f);
                D(g, Pink, hx + hr * 0.82f, 0.6f, 1 + atk);
            }
            if (c >= 4)
            {
                OE(g, C(0x5C6B2E), hx - 4, -1.5f, 7.4f, 8.4f);
                E(g, C(0x76883C), hx - 5.5f, -3.5f, 3.6f, 2.6f);
                OE(g, c >= 5 ? Gold : C(0xE03131), hx - 2.5f, -6, 1.8f, 1.8f);
            }
            else if (c >= 1)
            {
                OE(g, C(0x6B7F3A), hx - 3.5f, 0, 6.8f, 8.8f);
                E(g, C(0x8FA35A), hx - 5.5f, -2.5f, 2.8f, 3.6f);
            }
            if (b >= 2) Goggles(g, hx, hr, C(0x8CE99A));
            if (c >= 2)
            {
                float ex = hx + hr * 0.42f, ey = hr * 0.43f;
                OL(g, C(0x343A40), 1.6f, ex - 3, ey + 0.6f, ex - 8, ey + 2);
                OE(g, C(0x343A40), ex, ey, 3.8f, 3.8f);
                D(g, C(0xFF6B6B), ex, ey, 2.4f);
                D(g, White, ex - 0.8f, ey - 0.8f, 0.8f);
            }
            if (b >= 4)
            {
                float gx = hx + hr - 3, len = b >= 5 ? 12 : 9;
                Color gc = b >= 5 ? Gold : C(0x5C6670);
                ORect(g, Dk(gc, 0.25f), gx - 3, -4.8f, 5, 9.6f, 2);
                for (int i = -1; i <= 1; i++) OL(g, gc, 1.9f, gx, i * 2.8f, gx + len, i * 2.8f);
                ORect(g, Dk(gc, 0.1f), gx + len - 3.5f, -4.6f, 2, 9.2f, 1);
            }
            else if (b >= 1)
            {
                OL(g, C(0xE0A43A), 3.2f, hx - 7, 0, hx + 3, 0);
                L(g, C(0xA86F18), 1.2f, hx - 4, -1.6f, hx - 4, 1.6f);
                L(g, C(0xA86F18), 1.2f, hx, -1.6f, hx, 1.6f);
                OE(g, C(0xA5D8FF), hx + 4, 0, 1.2f, 2.2f);
            }
            if (b >= 3)
            {
                Color bc = b >= 5 ? Gold : Dk(fur, 0.1f);
                OL(g, C(0x8B5A2B), 2.8f, -16, -8, 1, 9.5f);
                for (int i = 0; i < 4; i++) OE(g, bc, -13 + i * 4.2f, -5 + i * 4.3f, 1.9f, 1.9f);
                if (b >= 5)
                {
                    OL(g, C(0x8B5A2B), 2.8f, -16, 8, 1, -9.5f);
                    for (int i = 0; i < 4; i++) if (i != 2) OE(g, bc, -13 + i * 4.2f, 5 - i * 4.3f, 1.9f, 1.9f);
                }
            }
            if (c >= 3)
            {
                float ry = 13, tip = c >= 4 ? 30 : 27;
                ORect(g, C(0x8B5A2B), -17, ry - 2.6f, 10, 5.2f, 2.2f);
                OL(g, C(0x343A40), 2.4f, -8, ry, tip, ry);
                if (c >= 5) L(g, Gold, 1, -16, ry, -8, ry);
                ORect(g, C(0x495057), -4, ry - 2.3f, c >= 4 ? 13 : 10, 4.6f, 2);
                D(g, C(0x66D9E8), c >= 4 ? 8.5f : 5.5f, ry, 1.3f);
                if (c >= 4) ORect(g, C(0x212529), tip - 3, ry - 2.1f, 4, 4.2f, 1);
                if (c >= 5)
                {
                    L(g, Kit.A(C(0xFF3B3B), 200), 1.2f, tip + 1, ry, tip + 8, ry);
                    D(g, Kit.A(C(0xFF3B3B), 110), tip + 8, ry, 3.2f);
                    D(g, C(0xFF8787), tip + 8, ry, 1.5f);
                }
            }
        }

        // --- Klubíčkářka: black-and-white tuxedo holding a yarn ball. Paths: blast (yarn bomb) / throw (cap, cannon) / knitting (web, sweater) ---
        static void Klubickarka(Graphics g, Color fur, Color fur2, int a, int b, int c, float sway, float atk)
        {
            float hx = 6, hr = 10.5f;
            Color yarn = a >= 5 ? C(0xF03E3E) : a >= 4 ? C(0x3B2A8C) : a >= 3 ? C(0x5F3DC4) : C(0xE64980);
            float br = a >= 5 ? 9.4f : a >= 4 ? 8.6f : a >= 1 ? 7.6f : 6.2f;
            float pext = atk * 4;
            bool holding = atk < 0.3f;
            if (c >= 5)
                for (int i = 0; i < 6; i++) { var p = Pol(-3, 0, 25, i * 60 + 30 + sway * 10); Snowflake(g, C(0xE7F5FF), p.X, p.Y, 3.6f, 1.1f); }
            if (b == 2 || b == 3)
            {
                OE(g, C(0xA9733F), -11, -15, 7.5f, 6.5f);
                E(g, C(0x7A4B25), -11, -15, 5.6f, 4.6f);
                YarnBall(g, -13.5f, -16, 2.6f, C(0x4DABF7));
                YarnBall(g, -9, -13.5f, 2.6f, C(0xFFD43B));
                YarnBall(g, -9.5f, -17.8f, 2.4f, C(0x69DB7C));
            }
            if (b >= 4)
            {
                Color cc = b >= 5 ? Gold : C(0x5C6670);
                ORect(g, cc, -14, -17.5f, 27, 7.5f, 3.4f);
                L(g, Lt(cc, 0.4f), 1.2f, -11, -16, 9, -16);
                ORect(g, C(0xFD7E14), 11, -18.2f, 3.8f, 8.9f, 1.6f);
                OE(g, Dk(cc, 0.35f), -9, -11.5f, 3, 3);
            }
            for (int pass = 0; pass < 2; pass++)
            {
                bool ink = pass == 0;
                Tail(g, ink, fur, 4.4f, -16, 16, sway);
                SE(g, ink, fur, -6, 0, 13, 10.5f);
                if (!ink && c >= 4) Sweater(g, c >= 5 ? C(0xA5D8FF) : C(0x12B886), c >= 5);
                SE(g, ink, fur, 15 + pext, -6.5f, 3.8f, 3.4f);
                SE(g, ink, fur, 15 + pext, 6.5f, 3.8f, 3.4f);
                SE(g, ink, fur, hx, 0, hr, hr);
                Ears(g, ink, fur, C(0x8E5A6B), hx, hr, 6.5f);
            }
            var tp = TailTip(-16, 16, sway);
            D(g, fur2, tp.X, tp.Y, 2.4f);
            if (c < 4) E(g, Kit.A(White, 38), -9, -4, 6, 2.6f);
            if (c == 3) { WebShape(g, -7, 0, 9.5f, 8, Kit.A(Ink, 140), 2.2f, 2); WebShape(g, -7, 0, 9.5f, 8, Kit.A(White, 230), 1.1f, 2); }
            if (c >= 1 && c <= 3)
            {
                OL(g, C(0xE9C46A), 1.3f, -18, -5, -6, 5);
                OL(g, C(0xE9C46A), 1.3f, -18, 5, -6, -5);
                OE(g, C(0xE76F51), -18, -5, 1.4f, 1.4f); OE(g, C(0xE76F51), -18, 5, 1.4f, 1.4f);
                YarnBall(g, -10, 0, 3.6f, C(0x20C997));
            }
            Poly(g, fur2, P(hx - 6, 0), P(hx + 3, -1.8f), P(hx + 3, 1.8f));
            Face(g, fur2, hx, hr, C(0x94D82D));
            D(g, fur2, 15 + pext + 1, -6.5f, 2.6f);
            D(g, fur2, 15 + pext + 1, 6.5f, 2.6f);
            if (c >= 2)
            {
                Color sc = c >= 5 ? C(0x74C0FC) : C(0xE03131);
                OL(g, sc, 3.4f, hx - 9.5f, -8.2f, hx - 9.5f, 8.2f);
                for (int i = -1; i <= 1; i++) L(g, White, 1.2f, hx - 11.1f, i * 4.5f, hx - 7.9f, i * 4.5f);
                OPoly(g, sc, P(hx - 11, 6), P(hx - 21, 13 + sway * 2), P(hx - 19, 15.5f + sway * 2), P(hx - 8, 8.5f));
                L(g, White, 1.1f, hx - 15, 10.2f + sway, hx - 13.6f, 12.4f + sway);
            }
            if (b >= 1)
            {
                OE(g, C(0x339AF0), 15 + pext - 2.6f, -6.5f, 1.3f, 3);
                OE(g, C(0x339AF0), 15 + pext - 2.6f, 6.5f, 1.3f, 3);
            }
            if (a >= 2)
            {
                Bez(g, Ink, 2.6f, P(18, 3), P(6, 15), P(-16, 13), P(-13, -2));
                Bez(g, yarn, 1.3f, P(18, 3), P(6, 15), P(-16, 13), P(-13, -2));
                Bez(g, yarn, 1.3f, P(-13, -2), P(-11, -10), P(-2, -12), P(2, -9));
            }
            if (holding)
            {
                float bx = 19.5f + br * 0.55f;
                if (b >= 5) { YarnBall(g, bx - 4, -11, 4.4f, C(0x4DABF7)); YarnBall(g, bx - 4, 11, 4.4f, C(0xFFD43B)); }
                if (a >= 5)
                    for (int i = 0; i < 6; i++) { var p = Pol(bx, 0, br * 0.9f, i * 60 + 30 + sway * 20); Flame(g, p.X, p.Y, 3.2f, i * 60 + 30 + sway * 20, true, 1); }
                YarnBall(g, bx, 0, br, yarn);
                if (a >= 4) { L(g, Gold, 1.6f, bx - br * 0.2f, -br * 0.98f, bx - br * 0.2f, br * 0.98f); }
                if (a >= 3)
                {
                    OL(g, C(0xC9A26B), 1.2f, bx + br * 0.6f, -br * 0.6f, bx + br * 0.6f + 3, -br - 2.5f);
                    Star(g, C(0xFFD43B), bx + br * 0.6f + 3, -br - 2.5f, a >= 4 ? 4.2f : 3.4f);
                    Star(g, C(0xFF8787), bx + br * 0.6f + 3, -br - 2.5f, a >= 4 ? 2.2f : 1.8f);
                }
            }
            if (b >= 3)
            {
                Color cap = b >= 5 ? C(0xFD7E14) : C(0x1C7ED6);
                OE(g, cap, hx - 1.5f, 0, 7.4f, 7.6f);
                OE(g, cap, hx - 9.5f, 0, 3.6f, 6.4f);
                L(g, Lt(cap, 0.45f), 1.2f, hx - 1.5f, -7, hx - 1.5f, 7);
                D(g, White, hx - 1.5f, 0, 1.6f);
            }
        }

        static void Sweater(Graphics g, Color c, bool frost)
        {
            E(g, c, -6, 0, 12.8f, 10);
            var kp = Pn(Lt(c, 0.35f), 1.1f);
            for (int i = 0; i < 4; i++)
            {
                float x = -16 + i * 4.5f;
                g.DrawLines(kp, new[] { P(x, -7.5f), P(x + 1.6f, -5), P(x, -2.5f), P(x + 1.6f, 0), P(x, 2.5f), P(x + 1.6f, 5), P(x, 7.5f) });
            }
            E(g, Dk(c, 0.25f), 4.5f, 0, 2.6f, 8.6f);
            if (frost) Snowflake(g, White, -8, 0, 5.5f, 1.2f);
            else { WebShape(g, -8, 0, 5.5f, 5.5f, Kit.A(Ink, 120), 2, 2); WebShape(g, -8, 0, 5.5f, 5.5f, White, 1, 2); }
        }

        // --- Laserovka: slim siamese with a laser pointer. Paths: beam (cannon, sun) / optics (battery, disco) / heat (flames) ---
        static void Laserovka(Graphics g, Color fur, Color pt, int a, int b, int c, float sway, float atk)
        {
            float hx = 6.5f, hr = 10;
            if (a >= 5) SunDisc(g, hx, 0, 14.5f, sway);
            if (c >= 5)
                for (int i = 0; i < 10; i++) { float dg = i * 36 + 18 + sway * 10; var p = Pol(-4, 0, 18, dg); Flame(g, p.X, p.Y, 3.8f, dg, true, 1); }
            if (b >= 5)
            {
                Color[] rb = { C(0xFF6B6B), C(0xFFD43B), C(0x69DB7C), C(0x4DABF7), C(0xCC5DE8), C(0xFF922B) };
                for (int i = 0; i < 6; i++) { var p = Pol(-9, 0, 31, i * 60 + 30 + sway * 20); L(g, Kit.A(rb[i], 210), 2.2f, -9, 0, p.X, p.Y); D(g, rb[i], p.X, p.Y, 1.8f); }
            }
            if (b >= 1 && b <= 2)
            {
                ORect(g, C(0xFAB005), -15, -16, 10, 6.5f, 2);
                ORect(g, C(0x868E96), -5.5f, -14.2f, 2, 3, 0.8f);
                Poly(g, Ink, P(-9.3f, -15), P(-12, -12.4f), P(-10.2f, -12.4f), P(-10.8f, -10.4f), P(-8, -13.2f), P(-9.8f, -13.2f));
            }
            for (int pass = 0; pass < 2; pass++)
            {
                bool ink = pass == 0;
                Tail(g, ink, pt, 3.8f, -16, 17, sway);
                SE(g, ink, fur, -6, 0, 13.5f, 9.5f);
                SE(g, ink, pt, 14.5f, -6.8f, 3.6f, 3.2f);
                SE(g, ink, pt, 14.5f, 6.8f, 3.6f, 3.2f);
                SE(g, ink, fur, hx, 0, hr, hr);
                Ears(g, ink, pt, C(0xC08A96), hx, hr, 8.5f);
            }
            E(g, Kit.A(pt, 70), -14, 0, 5, 7);
            E(g, Lt(pt, 0.15f), hx + hr * 0.5f, 0, hr * 0.5f, hr * 0.62f);
            Face(g, Lt(pt, 0.3f), hx, hr, C(0x4DABF7));
            if (c >= 1)
            {
                var tp = TailTip(-16, 17, sway);
                Flame(g, tp.X + 2.5f, tp.Y, c >= 2 ? 4 : 3.2f, 180, true, 1);
                if (c >= 2) { D(g, C(0xFFA94D), tp.X + 1, tp.Y - 6, 1); D(g, C(0xFFD43B), tp.X - 3, tp.Y + 5.5f, 0.9f); }
            }
            if (c >= 3)
            {
                ORect(g, C(0x495057), -16, -5.5f, 13, 11, 2.5f);
                Color vent = c >= 5 ? C(0xFFF3BF) : c >= 4 ? C(0xFFD43B) : C(0xFF922B);
                for (int i = 0; i < 3; i++) Kit.FillRound(g, vent, new RectangleF(-14 + i * 3.8f, -3.8f, 1.9f, 7.6f), 0.9f);
                if (c >= 4) for (int i = 0; i < 3; i++) Flame(g, -13 + i * 3.8f, -5.5f, 2.4f + (i == 1 ? 0.8f : 0), -90 + (i - 1) * 25, false, 1);
            }
            if (b >= 3)
            {
                float dr = b >= 5 ? 9 : b >= 4 ? 8.4f : 7.6f;
                if (b >= 4)
                    for (int s = -1; s <= 1; s += 2) OPoly(g, C(0xE9ECEF), P(-3, s * 8), P(0.5f, s * 11.5f), P(-3, s * 15), P(-6.5f, s * 11.5f));
                DiscoBall(g, -9, 0, dr);
                Color[] sp = { C(0xFF6B6B), C(0xFFD43B), C(0x69DB7C), C(0x4DABF7) };
                for (int i = 0; i < 4; i++) { var p = Pol(-9, 0, dr + 3.4f, sway * 30 + i * 90); Star(g, sp[i], p.X, p.Y, 2.6f); }
            }
            if (b >= 2)
            {
                OL(g, C(0x343A40), 1.4f, hx - 2, -hr + 0.5f, hx - 2, hr - 0.5f);
                ORect(g, C(0xFF6B6B), hx + 3, -7.5f, 3.6f, 15, 1.8f);
                L(g, Kit.A(White, 200), 1, hx + 4.3f, -5.5f, hx + 4.3f, -1);
            }
            if (a >= 3)
            {
                Color body = a >= 5 ? GoldDk : a >= 4 ? C(0x5F3DC4) : C(0x5C6670);
                Color coil = a >= 5 ? C(0xFFF3BF) : a >= 4 ? C(0xE599F7) : C(0x66D9E8);
                float len = a >= 4 ? 30 : 28, h = a >= 4 ? 9.5f : 8;
                ORect(g, body, -8, 8.5f, len, h, h * 0.45f);
                for (int i = 0; i < (a >= 4 ? 4 : 3); i++) g.FillRectangle(Kit.B(coil), -1 + i * 5.6f, 8.5f, 1.8f, h);
                ORect(g, C(0x343A40), len - 9, 7.5f, 4, h + 2, 1.5f);
                float mx = len - 3.5f, my = 8.5f + h / 2;
                D(g, Kit.A(coil, 110 + (int)(atk * 140)), mx, my, 4 + atk * 3 + (a >= 4 ? 1.2f : 0));
                D(g, a >= 5 ? White : C(0xE3FAFC), mx - 0.5f, my, 2);
            }
            else
            {
                float w = a >= 1 ? 3.2f : 2.4f;
                int n = a >= 2 ? 2 : 1;
                for (int i = 0; i < n; i++)
                {
                    float py = n == 2 ? (i == 0 ? -4.8f : 4.8f) : 4.2f;
                    OL(g, C(0x343A40), w, 13, py, 24, py);
                    if (a >= 1) L(g, C(0xFF6B6B), w * 0.8f, 18.5f, py, 20, py);
                    if (atk > 0) D(g, Kit.A(C(0xFF4040), 150), 25.5f, py, 2.5f + atk * 3);
                    D(g, C(0xFF4040), 25, py, w * 0.55f);
                }
            }
        }

        // --- Předoucí kocour: curled asleep on a cushion, never rotates. Paths: sleep (blanket, ice) / cushion (snore) / comfort (hearts, lotus) ---
        static void Spac(Graphics g, Color fur, Color fur2, int a, int b, int c, float sway)
        {
            float rc = b >= 4 ? 23.5f : b >= 1 ? 22.5f : 19.5f;
            if (b >= 5)
            {
                var cp = Pn(Kit.A(Ink, 190), 1.8f);
                var cl = Pn(Kit.A(C(0xD0BFFF), 200), 0.9f);
                for (int i = 0; i < 6; i++)
                {
                    float dg = i * 60 + 20;
                    var pts = new[] { Pol(0, 2, rc - 2, dg), Pol(0, 2, rc + 4, dg + 7), Pol(0, 2, rc + 8, dg - 4), Pol(0, 2, rc + 12, dg + 4) };
                    g.DrawLines(cp, pts); g.DrawLines(cl, pts);
                }
            }
            if (a >= 5)
                for (int i = 0; i < 8; i++)
                {
                    float dg = i * 45 + 22, len = i % 2 == 0 ? 10 : 6.5f;
                    OPoly(g, i % 2 == 0 ? C(0xD0EBFF) : C(0xA5D8FF), Pol(0, 2, rc - 3, dg - 7), Pol(0, 2, rc + len, dg), Pol(0, 2, rc - 3, dg + 7));
                }
            if (c >= 4)
                for (int i = 0; i < 8; i++)
                {
                    float dg = i * 45 + 22;
                    OPoly(g, i % 2 == 0 ? C(0xF783AC) : C(0xFCC2D7), Pol(0, 2, rc - 4, dg - 14), Pol(0, 2, rc + 3, dg - 9), Pol(0, 2, rc + 8, dg), Pol(0, 2, rc + 3, dg + 9), Pol(0, 2, rc - 4, dg + 14));
                }
            Color cush = b >= 4 ? C(0x9775FA) : C(0x8FA3D1);
            if (a >= 4) cush = Kit.Mix(cush, C(0xD0EBFF), 0.45f);
            OE(g, b >= 1 ? Dk(cush, 0.12f) : cush, 0, 2, rc, rc * 0.92f);
            if (b >= 1) E(g, cush, 0, 1.5f, rc - 4, rc * 0.92f - 4);
            E(g, Kit.A(White, 50), -6, -10, rc * 0.5f, rc * 0.22f);
            for (int i = 0; i < 4; i++) { var p = Pol(0, 2, rc * 0.72f, 45 + i * 90); D(g, Dk(cush, 0.25f), p.X, p.Y, 1.3f); }
            if (b >= 4) for (int i = 0; i < 4; i++) { var p = Pol(0, 2, rc * 0.97f, 45 + i * 90); OE(g, Gold, p.X, p.Y, 2.2f, 2.2f); }
            if (a >= 4)
                for (int i = 0; i < 7; i++)
                {
                    float dg = 35 + i * 18.5f;
                    var p = Pol(0, 2, rc * 0.96f, dg);
                    OPoly(g, C(0xE7F5FF), P(p.X - 1.8f, p.Y - 1), P(p.X + 1.8f, p.Y - 1), P(p.X, p.Y + (i % 2 == 0 ? 6 : 4)));
                }
            if (b == 2)
            {
                D(g, Kit.A(C(0xFFE066), 80), -17, 15, 8);
                OPoly(g, C(0xFFD43B), P(-17, 9.5f), P(-15.6f, 13.6f), P(-11.5f, 15), P(-15.6f, 16.4f), P(-17, 20.5f), P(-18.4f, 16.4f), P(-22.5f, 15), P(-18.4f, 13.6f));
            }
            float br = 1 + sway * 0.035f;
            Color stripe = Dk(fur, 0.25f);
            for (int pass = 0; pass < 2; pass++)
            {
                bool ink = pass == 0;
                SE(g, ink, fur, 2, 0.5f, 14 * br, 12.5f * br);
                SBez(g, ink, fur, 5, P(14, 3), P(16, 15), P(-4, 17), P(-13, 11));
                SE(g, ink, fur, -7, 3, 8.5f, 8);
                SPoly(g, ink, fur, Pol(-7, 3, 7.5f, 190), Pol(-7, 3, 14, 212), Pol(-7, 3, 7.5f, 238));
                SPoly(g, ink, fur, Pol(-7, 3, 7.5f, 262), Pol(-7, 3, 14, 288), Pol(-7, 3, 7.5f, 312));
            }
            Poly(g, Pink, Pol(-7, 3, 8, 202), Pol(-7, 3, 12, 212), Pol(-7, 3, 8, 226));
            Poly(g, Pink, Pol(-7, 3, 8, 275), Pol(-7, 3, 12, 288), Pol(-7, 3, 8, 300));
            var sp = Pn(stripe, 2);
            g.DrawArc(sp, -6, -8, 16, 15, 200, 120);
            g.DrawArc(sp, -9, -11, 22, 21, 215, 100);
            E(g, fur2, 5, 5, 6.5f * br, 5 * br);
            E(g, fur2, -6.5f, 6.5f, 4.5f, 3.2f);
            var ep = Pn(Ink, 1.2f);
            g.DrawArc(ep, -12.2f, 1.2f, 3.6f, 2.6f, 10, 160);
            g.DrawArc(ep, -6.4f, 2.6f, 3.6f, 2.6f, 10, 160);
            Poly(g, Nose, P(-8.8f, 6.6f), P(-6.8f, 7.2f), P(-8.2f, 8.4f));
            OE(g, fur2, -1, 10, 3, 2.4f);
            if (a >= 1)
            {
                var st = g.Save();
                g.TranslateTransform(7, 8);
                g.RotateTransform(-24);
                Color bl = a >= 4 ? C(0xE7F5FF) : a >= 2 ? C(0xC5F6FA) : C(0x8CB369);
                ORect(g, bl, -11, -8, 23, 17, 5);
                if (a >= 2)
                {
                    var fp = Pn(a >= 4 ? C(0x74C0FC) : White, 1.1f);
                    for (int i = 0; i < 3; i++)
                    {
                        float fx = -6 + i * 6.5f, fy = i == 1 ? 3 : -2;
                        g.DrawLine(fp, fx - 2.4f, fy, fx + 2.4f, fy); g.DrawLine(fp, fx - 1.2f, fy - 2.1f, fx + 1.2f, fy + 2.1f); g.DrawLine(fp, fx - 1.2f, fy + 2.1f, fx + 1.2f, fy - 2.1f);
                    }
                    g.FillRectangle(Kit.B(C(0x74C0FC)), -11, 5.5f, 23, 2.2f);
                }
                else
                {
                    g.FillRectangle(Kit.B(C(0x5E8C3A)), -11, -3, 23, 2);
                    g.FillRectangle(Kit.B(C(0xF3E3C3)), -11, 3, 23, 2);
                }
                g.Restore(st);
            }
            if (a >= 3)
            {
                Color capc = a >= 5 ? C(0x74C0FC) : C(0x4263EB);
                OPoly(g, capc, Pol(-7, 3, 8.5f, 200), P(-21, -13), Pol(-7, 3, 8.5f, 275));
                L(g, a >= 4 ? White : C(0xBAC8FF), a >= 4 ? 2.2f : 1.4f, Pol(-7, 3, 8.5f, 206).X, Pol(-7, 3, 8.5f, 206).Y, Pol(-7, 3, 8.5f, 268).X, Pol(-7, 3, 8.5f, 268).Y);
                OE(g, White, -21, -13, 2.6f, 2.6f);
                if (a >= 5) { Snowflake(g, White, 15, -18, 4.2f, 1.2f); Snowflake(g, C(0xD0EBFF), 22, -6, 3.2f, 1); }
                else { Note(g, 13 + sway * 1.5f, -21); Note(g, 21, -10 - sway * 1.5f); }
            }
            if (b >= 3)
            {
                float bs = (b >= 4 ? 3.6f : 2.6f) + (sway + 1) * 2.2f;
                float bx = -14 - bs * 0.5f, by = 11 + bs * 0.4f;
                D(g, Kit.A(C(0xD0EBFF), 170), bx, by, bs);
                Kit.Ring(g, Kit.A(C(0x4DABF7), 220), 1.1f, bx, by, bs);
                D(g, Kit.A(White, 220), bx - bs * 0.35f, by - bs * 0.4f, bs * 0.3f);
                if (b >= 4)
                    for (int i = 0; i < 2; i++) g.DrawArc(Pn(C(0x9775FA), 1.6f), bx - bs - 4 - i * 3.5f, by - bs - 4 - i * 3.5f, (bs + 4 + i * 3.5f) * 2, (bs + 4 + i * 3.5f) * 2, 110, 70);
            }
            if (c >= 1 && c <= 2)
            {
                Heart(g, C(0xF06595), 4, -15 - sway, 3);
                if (c >= 2) Heart(g, C(0xFF8787), -1, -21 + sway, 2.3f);
            }
            if (c >= 3)
            {
                g.DrawArc(Pn(Ink, 4.6f), -17, -7, 20, 20, 165, 170);
                g.DrawArc(Pn(C(0xF783AC), 2.6f), -17, -7, 20, 20, 165, 170);
                var e1 = Pol(-7, 3, 10, 165); var e2 = Pol(-7, 3, 10, 335);
                OE(g, C(0xE64980), e1.X, e1.Y, 3.2f, 3.2f);
                OE(g, C(0xE64980), e2.X, e2.Y, 3.2f, 3.2f);
            }
            if (c >= 5)
            {
                g.DrawEllipse(Pn(Ink, 4), -17, -15, 18, 7);
                g.DrawEllipse(Pn(Gold, 2.2f), -17, -15, 18, 7);
            }
        }

        static void Note(Graphics g, float x, float y)
        {
            L(g, C(0x5F3DC4), 1.3f, x + 1.6f, y, x + 1.6f, y - 6);
            L(g, C(0x5F3DC4), 1.3f, x + 1.6f, y - 6, x + 4.2f, y - 4.6f);
            E(g, C(0x5F3DC4), x, y, 2.2f, 1.7f);
        }

        // --- Pamlskárna: golden cat beside a big treat jar, never rotates. Paths: stock (crates, crown) / care (collar, coins, top hat) / delivery (parcels, factory) ---
        static void Pamlskarna(Graphics g, Color fur, Color fur2, int a, int b, int c, float sway)
        {
            if (c >= 5)
            {
                ORect(g, C(0xC2603F), -31, -27, 8, 19, 1.5f);
                for (int i = 0; i < 4; i++) L(g, C(0x9C4A2E), 1, -31, -23 + i * 4.2f, -23, -23 + i * 4.2f);
                ORect(g, C(0x495057), -32, -28.5f, 10, 3, 1);
                OE(g, C(0xDEE2E6), -24 + sway * 1.5f, -33, 4.2f, 3.4f);
                OE(g, C(0xF1F3F5), -18 + sway * 2, -36, 3.2f, 2.6f);
            }
            if (a == 2)
            {
                ORect(g, C(0xB07A45), 9, -24, 22, 18, 2.5f);
                L(g, C(0x8A5A2E), 1.2f, 10, -18, 30, -18); L(g, C(0x8A5A2E), 1.2f, 10, -12, 30, -12);
                TreatShape(g, 20, -21, 7);
            }
            if (a == 3) { Can(g, 15, -16); Can(g, 26, -16); Can(g, 20.5f, -27); }
            if (a == 4)
            {
                Fish(g, 13, -26, -60); Fish(g, 20, -28, -90); Fish(g, 27, -26, -120);
                ORect(g, C(0x8F5E34), 8, -22, 25, 15, 2.5f);
                L(g, C(0x6B4423), 1.2f, 9, -15, 32, -15);
                Kit.FillRound(g, C(0xF8F0E3), new RectangleF(15, -20, 11, 4.5f), 1);
            }
            // jar
            Color glass = a >= 5 ? C(0xFFEC99) : C(0xD7EEFC);
            ORect(g, glass, 0, -10, 21, 27, 6);
            var rnd = new Random(11);
            for (int i = 0; i < 6; i++) TreatShape(g, 5 + (i % 3) * 5.5f + Rf(rnd, -1, 1), 13 - (i / 3) * 6 + Rf(rnd, -1, 1), 6.5f);
            g.FillRectangle(Kit.B(Kit.A(White, 170)), 3, -6, 2.2f, 17);
            Kit.FillRound(g, a >= 5 ? C(0xFFF9DB) : C(0xFFF3BF), new RectangleF(4, -6, 13, 6), 2);
            TreatShape(g, 10.5f, -3, 6);
            ORect(g, a >= 5 ? Gold : C(0xE03131), -1, -15, 23, 6, 2.5f);
            g.FillRectangle(Kit.B(a >= 5 ? C(0xFFF3BF) : C(0xFF8787)), 1, -13.8f, 19, 1.5f);
            if (a >= 5)
            {
                for (int i = 0; i < 5; i++) TreatShape(g, 3 + i * 4, -17 - (i % 2) * 2.5f - (i == 2 ? 3 : 0), 7);
                Star(g, White, 22, -20, 3); Star(g, White, 1, -21, 2.2f);
            }
            // the cat
            Color stripe = Dk(fur, 0.22f);
            for (int pass = 0; pass < 2; pass++)
            {
                bool ink = pass == 0;
                SBez(g, ink, fur, 4.4f, P(-4, 16), P(-22, 22), P(-27, 8), P(-21 + sway * 3, -1));
                SE(g, ink, fur, -10, 7, 10, 11);
                SE(g, ink, fur, -13.5f, 16.5f, 3.6f, 2.8f);
                SE(g, ink, fur, -6.5f, 16.5f, 3.6f, 2.8f);
                SPoly(g, ink, fur, P(-19, -9), P(-18, -20), P(-12.5f, -15));
                SPoly(g, ink, fur, P(-1, -9), P(-2, -20), P(-7.5f, -15));
                SE(g, ink, fur, -10, -8, 9.6f, 8.6f);
                SE(g, ink, fur, 0.5f, 3, 3.6f, 3.2f);
            }
            Poly(g, Pink, P(-17.5f, -11.5f), P(-17.2f, -17.5f), P(-14, -14.5f));
            Poly(g, Pink, P(-2.5f, -11.5f), P(-2.8f, -17.5f), P(-6, -14.5f));
            E(g, fur2, -10, 9.5f, 6, 7.5f);
            E(g, fur2, -10, -4.6f, 5, 3.4f);
            L(g, stripe, 1.6f, -10, -16, -10, -12.5f); L(g, stripe, 1.6f, -13, -15.5f, -12.6f, -12.8f); L(g, stripe, 1.6f, -7, -15.5f, -7.4f, -12.8f);
            D(g, Ink, -13.5f, -8.5f, 2); D(g, Ink, -6.5f, -8.5f, 2);
            D(g, White, -14.1f, -9.2f, 0.8f); D(g, White, -7.1f, -9.2f, 0.8f);
            E(g, Kit.A(Nose, 90), -16, -5.5f, 2, 1.2f); E(g, Kit.A(Nose, 90), -4, -5.5f, 2, 1.2f);
            Poly(g, Nose, P(-11.4f, -6.2f), P(-8.6f, -6.2f), P(-10, -4.8f));
            var mp = Pn(Ink, 0.9f);
            g.DrawArc(mp, -12, -5.4f, 2, 2, 0, 160); g.DrawArc(mp, -10, -5.4f, 2, 2, 20, 160);
            var wp = Pn(Kit.A(Ink, 160), 0.7f);
            g.DrawLine(wp, -14, -5, -22, -6.5f); g.DrawLine(wp, -14, -4, -22, -3); g.DrawLine(wp, -6, -5, 2, -6.5f); g.DrawLine(wp, -6, -4, 2, -3);
            if (a >= 1 || b >= 3)
            {
                Color bowl = b >= 3 ? Gold : C(0x4DABF7);
                OE(g, bowl, 12, 21.5f, 10, 4.6f);
                E(g, Dk(bowl, 0.25f), 12, 20.5f, 7.5f, 2.8f);
                for (int i = 0; i < 5; i++) OE(g, C(0xC27C3A), 7.5f + i * 2.3f, 19.6f + (i % 2) * 0.8f, 1.6f, 1.2f);
                if (b >= 3) { Star(g, White, 20, 17, 3); Star(g, White, 4, 23, 2.2f); }
            }
            if (b >= 1)
            {
                g.DrawArc(Pn(C(0xE03131), 2), -17, -6, 14, 8, 20, 140);
                Heart(g, C(0xE03131), -10, 1.5f, 3.4f);
            }
            if (b >= 2)
            {
                int n = b >= 4 ? 5 : 3;
                for (int i = 0; i < n; i++) OE(g, C(0xFCC419), -24, 18 - i * 3.2f, 5, 2.3f);
                L(g, C(0xE67700), 1, -26, 18 - (n - 1) * 3.2f, -22, 18 - (n - 1) * 3.2f);
                if (b >= 4) for (int i = 0; i < 3; i++) OE(g, C(0xFCC419), -27.5f, 25 - i * 3.2f, 4, 2);
            }
            if (b >= 4)
            {
                Kit.Ring(g, Ink, 2.6f, -6.5f, -8.5f, 3.4f);
                Kit.Ring(g, Gold, 1.3f, -6.5f, -8.5f, 3.4f);
                L(g, Gold, 0.8f, -3.3f, -7.5f, -1.5f, 0);
            }
            if (b >= 5)
            {
                ORect(g, C(0x343A40), -19.5f, -20.5f, 19, 3.4f, 1.5f);
                ORect(g, C(0x343A40), -15.5f, -31, 11, 11.5f, 1.5f);
                g.FillRectangle(Kit.B(Gold), -15.5f, -23.4f, 11, 2.2f);
                E(g, Kit.A(White, 60), -13, -28, 1.6f, 2.6f);
            }
            if (a >= 5)
            {
                OPoly(g, Gold, P(-16, -14), P(-17, -22), P(-13, -18.5f), P(-10, -24), P(-7, -18.5f), P(-3, -22), P(-4, -14));
                D(g, C(0xE03131), -10, -16.5f, 1.4f);
                D(g, C(0x4DABF7), -14.5f, -15.8f, 0.9f); D(g, C(0x4DABF7), -5.5f, -15.8f, 0.9f);
            }
            if (c >= 3)
            {
                Color cap = C(0x1C7ED6);
                ORect(g, cap, -17, -20, 14, 6.5f, 3.2f);
                ORect(g, Dk(cap, 0.3f), -18.5f, -14.6f, 17, 2.4f, 1.2f);
                OE(g, Gold, -10, -17, 1.6f, 1.6f);
            }
            if (c >= 1 && c <= 4)
            {
                float px = -27, py = -14;
                if (c == 2 || c == 3)
                {
                    OPoly(g, C(0xE9D8A6), P(px - 5.5f, py + 6), P(px - 4.5f, py - 3), P(px - 2, py - 6), P(px + 2, py - 6), P(px + 4.5f, py - 3), P(px + 5.5f, py + 6));
                    OL(g, C(0xA9733F), 1, px - 2.4f, py - 5.5f, px + 2.4f, py - 5.5f);
                    TreatShape(g, px, py + 1.5f, 6);
                }
                else
                {
                    if (c >= 4) for (int s = -1; s <= 1; s += 2) OPoly(g, White, P(px + s * 3.5f, py - 1), P(px + s * 10, py - 6 + sway), P(px + s * 9, py - 1 + sway), P(px + s * 10, py + 2), P(px + s * 3.5f, py + 2));
                    ORect(g, C(0xC8915A), px - 4.5f, py - 4, 9, 8, 1.2f);
                    L(g, C(0xF1E3C8), 1.4f, px, py - 4, px, py + 4);
                }
            }
        }

        static void Fish(Graphics g, float x, float y, float deg)
        {
            var st = g.Save();
            g.TranslateTransform(x, y);
            g.RotateTransform(deg);
            OPoly(g, C(0x74C0FC), P(-5, 0), P(-9, -3.4f), P(-9, 3.4f));
            OE(g, C(0x74C0FC), 0, 0, 6, 3.2f);
            D(g, Ink, 3, -0.6f, 0.8f);
            g.Restore(st);
        }

        static void Can(Graphics g, float x, float y)
        {
            ORect(g, C(0xCED4DA), x - 5, y - 5.5f, 10, 11, 2);
            g.FillRectangle(Kit.B(C(0x4DABF7)), x - 5, y - 2.5f, 10, 5);
            TreatShape(g, x, y, 5);
            E(g, C(0xE9ECEF), x, y - 5.2f, 4.6f, 1.2f);
        }

        static void TreatShape(Graphics g, float x, float y, float s)
        {
            Color c = C(0xE8913A);
            Poly(g, Ink, P(x + s * 0.22f, y), P(x + s * 0.62f, y - s * 0.34f), P(x + s * 0.62f, y + s * 0.34f));
            E(g, Ink, x - s * 0.1f, y, s * 0.42f, s * 0.27f);
            Poly(g, c, P(x + s * 0.25f, y), P(x + s * 0.55f, y - s * 0.24f), P(x + s * 0.55f, y + s * 0.24f));
            E(g, c, x - s * 0.1f, y, s * 0.34f, s * 0.19f);
        }

        static void Heart(Graphics g, Color c, float x, float y, float s)
        {
            var pts = new[] { P(x - s, y - s * 0.2f), P(x, y + s * 1.05f), P(x + s, y - s * 0.2f) };
            D(g, Ink, x - s * 0.5f, y - s * 0.35f, s * 0.62f); D(g, Ink, x + s * 0.5f, y - s * 0.35f, s * 0.62f);
            g.DrawPolygon(Pn(Ink, 1.6f), pts); g.FillPolygon(Kit.B(Ink), pts);
            D(g, c, x - s * 0.5f, y - s * 0.35f, s * 0.5f); D(g, c, x + s * 0.5f, y - s * 0.35f, s * 0.5f);
            Poly(g, c, P(x - s * 0.95f, y - s * 0.25f), P(x, y + s * 0.85f), P(x + s * 0.95f, y - s * 0.25f), P(x, y - s * 0.3f));
        }

        // =====================================================================
        // UPGRADE ICONS (drawn in a 64-unit box centred on 0,0; frame metal: bronze, silver, gold, gold + gems, glowing gold)
        // =====================================================================

        public static void UpgradeIcon(Graphics g, TowerKind k, int path, int tier, RectangleF r)
        {
            path = Math.Max(0, Math.Min(Defs.PathCount - 1, path));
            tier = Math.Max(1, Math.Min(Defs.TierCount, tier));
            int side = Math.Max(8, (int)Math.Round(Math.Min(r.Width, r.Height)));
            int sq = Dev(g, r.X + r.Width / 2, r.Y + r.Height / 2);
            long key = Key(6, sq, ((long)k << 40) + ((long)path << 36) + ((long)tier << 32) + side);
            var s = Find(key);
            if (s == null)
                using (var ig = Begin(key, side / 2 + 1, sq, out s))
                {
                    float sc = side / 64f;
                    ig.ScaleTransform(sc, sc);
                    IconFrame(ig, PathCol(k, path), tier);
                    Pict(ig, k, path, tier);
                    if (tier >= 5) { Star(ig, Ink, 20, -20, 7.5f); Star(ig, C(0xFFF3BF), 20, -20, 5.6f); }
                }
            Blit(g, s);
        }

        static Color iconBg;
        static RectangleF Inf(RectangleF r, float d) { return new RectangleF(r.X - d, r.Y - d, r.Width + d * 2, r.Height + d * 2); }

        static void IconFrame(Graphics g, Color pc, int tier)
        {
            Color metal = tier >= 3 ? Gold : tier == 2 ? C(0xDEE2E6) : C(0xD9894A);
            Color shade = tier >= 3 ? GoldDk : tier == 2 ? C(0x8C959F) : C(0x8F4F22);
            if (tier >= 5)
            {
                Kit.FillRound(g, Al(pc, 0.45f), new RectangleF(-32, -32, 64, 64), 18);
                Kit.FillRound(g, Al(C(0xFFF3BF), 0.5f), new RectangleF(-31, -31, 62, 62), 17);
            }
            var R = new RectangleF(-28.5f, -28.5f, 57, 57);
            Kit.FillRound(g, Ink, Inf(R, 1.8f), 15);
            Kit.FillRound(g, shade, R, 13);
            Kit.FillRound(g, metal, new RectangleF(R.X, R.Y, R.Width, R.Height - 2.5f), 13);
            Kit.FillRound(g, Al(White, 0.4f), new RectangleF(R.X + 6, R.Y + 1.2f, R.Width - 12, 2.2f), 1.1f);
            var inner = new RectangleF(-24, -24, 48, 48);
            Kit.FillRound(g, Ink, Inf(inner, 1.4f), 10.5f);
            iconBg = Dk(pc, 0.6f);
            Kit.FillRound(g, iconBg, inner, 9.5f);
            D(g, Dk(pc, 0.42f), 0, -1, 20);
            if (tier >= 5)
                for (int i = 0; i < 12; i++) Poly(g, Al(Gold, 0.35f), Pol(0, 0, 4, i * 30 - 6), Pol(0, 0, 23, i * 30), Pol(0, 0, 4, i * 30 + 6));
            if (tier >= 4)
            {
                var gem = Lt(pc, 0.25f);
                for (int i = 0; i < 4; i++)
                {
                    var p = Pol(0, 0, 26.3f, i * 90);
                    OPoly(g, gem, P(p.X, p.Y - 3), P(p.X + 3, p.Y), P(p.X, p.Y + 3), P(p.X - 3, p.Y));
                    D(g, White, p.X - 0.8f, p.Y - 0.8f, 0.8f);
                }
            }
        }

        static void Pict(Graphics g, TowerKind k, int p, int t)
        {
            switch ((int)k * 3 + p)
            {
                case 0: IcClaws(g, t); break;
                case 1: IcSpeed(g, t); break;
                case 2: IcTeeth(g, t); break;
                case 3: IcBalls(g, t); break;
                case 4: IcSights(g, t); break;
                case 5: IcSniper(g, t); break;
                case 6: IcBoom(g, t); break;
                case 7: IcThrow(g, t); break;
                case 8: IcKnit(g, t); break;
                case 9: IcBeam(g, t); break;
                case 10: IcOptics(g, t); break;
                case 11: IcHeat(g, t); break;
                case 12: IcSleep(g, t); break;
                case 13: IcCushion(g, t); break;
                case 14: IcComfort(g, t); break;
                case 15: IcStock(g, t); break;
                case 16: IcCare(g, t); break;
                default: IcDelivery(g, t); break;
            }
        }

        static readonly float[,] toes = { { -1.2f, -0.65f }, { -0.42f, -1.25f }, { 0.42f, -1.25f }, { 1.2f, -0.65f } };

        static void OPaw(Graphics g, Color c, Color pad, float x, float y, float s)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                bool ink = pass == 0;
                SE(g, ink, c, x, y + s * 0.45f, s * 1.15f, s * 0.95f);
                for (int i = 0; i < 4; i++) SE(g, ink, c, x + toes[i, 0] * s, y + toes[i, 1] * s, s * 0.48f, s * 0.53f);
            }
            if (pad.A == 0) return;
            E(g, pad, x, y + s * 0.55f, s * 0.68f, s * 0.52f);
            for (int i = 0; i < 4; i++) E(g, pad, x + toes[i, 0] * s, y + toes[i, 1] * s + s * 0.05f, s * 0.26f, s * 0.3f);
        }

        static void SpeedLines(Graphics g, float x, float y, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float yy = y + (i - (n - 1) / 2f) * 7;
                OL(g, White, 2.2f, x - 8 + Math.Abs(i - (n - 1) / 2f) * 3, yy, x, yy);
            }
        }

        static void ZLetter(Graphics g, Color c, float x, float y, float s)
        {
            var pts = new[] { P(x - s, y - s), P(x + s, y - s), P(x - s, y + s), P(x + s, y + s) };
            g.DrawLines(Pn(Ink, s * 0.5f + 3), pts);
            g.DrawLines(Pn(c, s * 0.5f), pts);
        }

        static void Fuzz(Graphics g, Color c, float x, float y, float r)
        {
            for (int i = 0; i < 10; i++) { var p = Pol(x, y, r, i * 36); D(g, Ink, p.X, p.Y, r * 0.3f + O); }
            D(g, Ink, x, y, r + O);
            for (int i = 0; i < 10; i++) { var p = Pol(x, y, r, i * 36); D(g, c, p.X, p.Y, r * 0.3f); }
            D(g, c, x, y, r);
            for (int i = 0; i < 5; i++) { var p = Pol(x, y, r * 0.6f, i * 72 + 20); D(g, Dk(c, 0.15f), p.X, p.Y, r * 0.12f); }
            D(g, Lt(c, 0.45f), x - r * 0.32f, y - r * 0.32f, r * 0.28f);
        }

        static void Burst(Graphics g, Color c, float x, float y, float ro, float ri, int n, float rot)
        {
            var pts = new PointF[n * 2];
            for (int i = 0; i < n * 2; i++) pts[i] = Pol(x, y, i % 2 == 0 ? ro : ri, rot + i * 180f / n);
            OPoly(g, c, pts);
        }

        static void Crescent(Graphics g, Color c, float x, float y, float r)
        {
            var pts = new PointF[26];
            for (int i = 0; i <= 12; i++) pts[i] = Pol(x, y, r, 40 + i * 280f / 12);
            for (int i = 0; i <= 12; i++) pts[13 + i] = Pol(x + r * 0.45f, y, r * 0.716f, 296.2f - i * 232.4f / 12);
            OPoly(g, c, pts);
        }

        static void Parcel(Graphics g, float x, float y, float w, float h)
        {
            ORect(g, C(0xC8915A), x - w / 2, y - h / 2, w, h, 2);
            g.FillRectangle(Kit.B(C(0xDBA672)), x - w / 2, y - h / 2, w, h * 0.22f);
            g.FillRectangle(Kit.B(C(0xF1E3C8)), x - w * 0.08f, y - h / 2, w * 0.16f, h);
            L(g, Kit.A(Ink, 120), 1, x - w / 2 + 1, y - h * 0.28f, x + w / 2 - 1, y - h * 0.28f);
        }

        static void Coin(Graphics g, float x, float y, float rx, float ry)
        {
            OE(g, GoldDk, x, y + ry * 0.35f, rx, ry);
            E(g, Gold, x, y, rx, ry);
        }

        // --- Drápka ---
        static void IcClaws(Graphics g, int t)
        {
            Color fur = t >= 5 ? Gold : C(0xF08C2E), cl = t >= 5 ? C(0xFFF9DB) : White;
            if (t >= 4)
                for (int i = 0; i < 12; i++) { var q = Pol(2, 3, 15.5f, i * 30 + 15); OE(g, i % 2 == 0 ? C(0xA8521B) : C(0xC9661F), q.X, q.Y, 6, 6); }
            if (t >= 2) OPaw(g, Dk(fur, 0.2f), Color.Empty, -12, -8, 6.4f);
            float s = t >= 3 ? 9.4f : 8.4f, x = 2, y = 5;
            OPaw(g, fur, C(0xFFE8CC), x, y, s);
            if (t >= 3)
            {
                Color st = t >= 5 ? GoldDk : C(0x4A2511);
                for (int sd = -1; sd <= 1; sd += 2)
                {
                    L(g, st, 1.8f, x + sd * s * 1.12f, y + s * 0.15f, x + sd * s * 0.78f, y + s * 0.35f);
                    L(g, st, 1.8f, x + sd * s * 1.1f, y + s * 0.65f, x + sd * s * 0.8f, y + s * 0.75f);
                }
            }
            float len = 2.5f + t * 1.5f;
            for (int i = 0; i < 4; i++)
            {
                float tx = x + toes[i, 0] * s, ty = y + toes[i, 1] * s - s * 0.45f;
                OPoly(g, cl, P(tx - 1.5f, ty + 1), P(tx + toes[i, 0] * 1.5f, ty - len), P(tx + 1.5f, ty + 1));
            }
            if (t >= 5) { Star(g, White, -16, 15, 3.5f); Star(g, White, 17, 13, 2.8f); }
        }

        static void IcSpeed(Graphics g, int t)
        {
            SpeedLines(g, -13, 2, 3);
            switch (t)
            {
                case 1:
                    {
                        var st = g.Save(); g.TranslateTransform(5, 2); g.RotateTransform(-25);
                        OE(g, C(0x8B5A2B), 0, 0, 9, 12.5f);
                        Bez(g, C(0x4A2E14), 2.2f, P(0, -10), P(-4, -4), P(4, 4), P(0, 10));
                        E(g, Kit.A(White, 90), -3.5f, -5, 2, 4);
                        g.Restore(st);
                        Bez(g, Al(White, 0.8f), 1.6f, P(12, -12), P(9, -15), P(15, -17), P(12, -21));
                        break;
                    }
                case 2:
                    OL(g, C(0x343A40), 3, -6, 2, 22, 2);
                    for (int s = -1; s <= 1; s += 2) { OE(g, C(0x495057), 7 + s * 7.5f, 2, 7.5f, 7.5f); D(g, C(0x8CE99A), 7 + s * 7.5f, 2, 5.2f); D(g, White, 5 + s * 7.5f, 0, 1.6f); }
                    break;
                case 3:
                    for (int i = 0; i < 3; i++)
                    {
                        float R = 5 + i * 5.5f;
                        g.DrawArc(Pn(Ink, 5), 5 - R, 2 - R, R * 2, R * 2, i * 110 + 20, 250);
                        g.DrawArc(Pn(i % 2 == 0 ? White : C(0xA5D8FF), 2.8f), 5 - R, 2 - R, R * 2, R * 2, i * 110 + 20, 250);
                    }
                    break;
                default:
                    {
                        float sc = t >= 5 ? 1.1f : 1;
                        for (int i = 0; i < 6; i++)
                        {
                            float yy = (-16 + i * 6.4f) * sc + 2, w = (17 - i * 2.6f) * sc;
                            OE(g, i % 2 == 0 ? C(0xE7F5FF) : C(0xA5D8FF), 5 + (float)Math.Sin(i * 1.3) * 2, yy, w, 3.3f);
                        }
                        if (t >= 5) { Lightning(g, Gold, -9, -12, 9); Star(g, White, 19, 15, 3); }
                        break;
                    }
            }
        }

        static void IcTeeth(Graphics g, int t)
        {
            if (t == 1)
            {
                for (int s = -1; s <= 1; s += 2)
                {
                    OL(g, White, 1.5f, s * 7, 4, s * 22, -1);
                    OL(g, White, 1.5f, s * 7, 7, s * 22, 8);
                    OL(g, White, 1.5f, s * 7, 1, s * 20, -9);
                }
                OE(g, C(0xFFE8CC), -5.5f, 5, 7.5f, 6.5f);
                OE(g, C(0xFFE8CC), 5.5f, 5, 7.5f, 6.5f);
                E(g, C(0xFFE8CC), 0, 4, 5, 4);
                OPoly(g, Nose, P(-4, -3), P(4, -3), P(0, 1.5f));
                for (int s = -1; s <= 1; s += 2) D(g, Kit.A(Ink, 160), s * 4.5f, 4.5f, 0.9f);
                return;
            }
            Color jaw = t >= 5 ? C(0xC92A2A) : t == 4 ? C(0x748FA5) : t == 3 ? C(0x5C940D) : C(0xF08C2E);
            Color tooth = t >= 5 ? C(0xFFF3BF) : White;
            float rx = t == 3 ? 21 : 18, ry = t == 3 ? 9 : 11, gap = 4.5f;
            OE(g, C(0x862E3B), 0, 0, rx * 0.82f, gap + 2.5f);
            if (t >= 5)
                for (int s = -1; s <= 1; s += 2) OPoly(g, Gold, P(s * 7, -gap - ry + 3), P(s * 17, -gap - ry - 7), P(s * 12, -gap - ry + 4));
            g.FillPie(Kit.B(Ink), -rx - O, -gap - ry - O, (rx + O) * 2, (ry + O) * 2, 180, 180);
            g.FillPie(Kit.B(jaw), -rx, -gap - ry, rx * 2, ry * 2, 180, 180);
            float ly = ry * 0.75f;
            g.FillPie(Kit.B(Ink), -rx * 0.9f - O, gap - ly - O, (rx * 0.9f + O) * 2, (ly + O) * 2, 0, 180);
            g.FillPie(Kit.B(Dk(jaw, 0.1f)), -rx * 0.9f, gap - ly, rx * 0.9f * 2, ly * 2, 0, 180);
            L(g, Ink, 1.8f, -rx, -gap, rx, -gap);
            L(g, Ink, 1.8f, -rx * 0.9f, gap, rx * 0.9f, gap);
            int n = t == 2 ? 2 : t == 3 ? 7 : 6;
            float tl = t == 2 ? 7 : t == 3 ? 4 : 5.5f;
            for (int i = 0; i < n; i++)
            {
                float tx = n == 2 ? (i == 0 ? -rx * 0.45f : rx * 0.45f) : -rx * 0.72f + i * rx * 1.44f / (n - 1), w = n == 2 ? 2.6f : t == 3 ? 1.6f : 2.2f;
                OPoly(g, tooth, P(tx - w, -gap), P(tx, -gap + tl), P(tx + w, -gap));
                if (n > 2 || i == 0) { float bx = n == 2 ? 0 : tx + rx * 0.06f; OPoly(g, tooth, P(bx - w * 0.85f, gap), P(bx, gap - tl * 0.8f), P(bx + w * 0.85f, gap)); }
            }
            float ey = -gap - ry * 0.55f;
            for (int s = -1; s <= 1; s += 2)
            {
                OE(g, t >= 5 ? Gold : t == 3 ? C(0xC0EB75) : White, s * rx * 0.42f, ey, 2.8f, 2.8f);
                D(g, Ink, s * rx * 0.42f + 0.4f, ey, 1.4f);
            }
            if (t == 4) OPoly(g, C(0x748FA5), P(-4, -gap - ry - 1), P(4, -gap - ry - 9), P(6, -gap - ry - 1));
            if (t >= 5) { Flame(g, rx * 0.95f, 0, 3.6f, 0, true, 1); Flame(g, -rx * 0.95f, 0, 3.6f, 180, true, 1); }
        }

        // --- Chlupáč ---
        static void IcBalls(Graphics g, int t)
        {
            Color c = t >= 2 ? C(0x94D82D) : C(0x9AA1A9);
            switch (t)
            {
                case 1: SpeedLines(g, -12, 1, 3); Fuzz(g, c, 4, 1, 11.5f); break;
                case 2:
                    Fuzz(g, c, 1, -2, 11.5f);
                    OE(g, c, -5, 12.5f, 2.2f, 3.6f); OE(g, c, 4, 14, 1.8f, 3);
                    E(g, C(0xC0EB75), -3, -7, 3, 1.8f);
                    break;
                case 3:
                    SpeedLines(g, -15, 0, 3);
                    Fuzz(g, c, -4, -11, 6.5f); Fuzz(g, c, -4, 11, 6.5f); Fuzz(g, c, 8, 0, 8.5f);
                    break;
                case 4:
                    for (int i = 0; i < 5; i++) { var p = Pol(-20, 0, 27, -44 + i * 22); Fuzz(g, c, p.X, p.Y, i == 2 ? 6.5f : 5.4f); }
                    Fuzz(g, Lt(c, 0.2f), -12, 0, 5);
                    break;
                default:
                    for (int i = 0; i < 3; i++) g.DrawArc(Pn(Al(White, 0.6f), 2), -18 + i * 3, -18 + i * 3, 36 - i * 6, 36 - i * 6, i * 120, 90);
                    for (int i = 0; i < 8; i++) { var p = Pol(0, 0, 16.5f, i * 45 + 22); Fuzz(g, i % 2 == 0 ? c : C(0xC0EB75), p.X, p.Y, 4.4f); }
                    Fuzz(g, C(0xD8F5A2), 0, 0, 7.5f);
                    break;
            }
        }

        static void IcSights(Graphics g, int t)
        {
            Color brass = C(0xE0A43A);
            if (t == 1)
            {
                var st = g.Save(); g.RotateTransform(-30);
                ORect(g, brass, -20, -4, 14, 8, 2);
                ORect(g, Lt(brass, 0.2f), -7, -5.5f, 13, 11, 2);
                ORect(g, brass, 5, -7, 13, 14, 2.5f);
                OE(g, C(0xA5D8FF), 18.5f, 0, 2.6f, 6);
                L(g, C(0xA86F18), 1.4f, -1, -5, -1, 5);
                g.Restore(st);
                return;
            }
            if (t == 2)
            {
                OL(g, C(0x343A40), 3, -22, 0, 22, 0);
                for (int s = -1; s <= 1; s += 2) { OE(g, C(0x495057), s * 8.5f, 0, 8, 8); D(g, C(0x8CE99A), s * 8.5f, 0, 5.6f); D(g, White, s * 8.5f - 2, -2, 1.7f); }
                return;
            }
            if (t == 3)
            {
                OL(g, C(0x8B5A2B), 6.5f, -21, 15, 21, -15);
                for (int i = 0; i < 5; i++) Fuzz(g, C(0xADB5BD), -14 + i * 7, 10 - i * 5, 3.6f);
                return;
            }
            Color gc = t >= 5 ? Gold : C(0x5C6670);
            if (t >= 5)
            {
                OL(g, C(0x8B5A2B), 4, -14, 9, -6, 20);
                for (int i = 0; i < 3; i++) OE(g, Gold, -12 + i * 3, 12 + i * 3.4f, 2.2f, 2.2f);
                Burst(g, C(0xFFE066), 21, 0, 7, 3.5f, 6, 0);
            }
            ORect(g, Dk(gc, 0.3f), -20, -9, 13, 18, 3.5f);
            for (int i = -1; i <= 1; i++) OL(g, gc, 3.6f, -7, i * 5.2f, 16, i * 5.2f);
            ORect(g, Dk(gc, 0.12f), 3, -9, 3.5f, 18, 1.5f);
            ORect(g, Dk(gc, 0.12f), 13, -8.5f, 3, 17, 1.2f);
            E(g, Kit.A(White, 80), -15, -5, 3, 1.6f);
        }

        static void IcSniper(Graphics g, int t)
        {
            Color ch = t >= 5 ? Gold : t >= 3 ? C(0xFF4D4D) : C(0xDEE2E6);
            float w = t >= 3 ? 2.6f : 1.7f, R = 16.5f;
            if (t >= 3) g.DrawEllipse(Pn(Ink, w + 2.6f), -R, -R, R * 2, R * 2);
            g.DrawEllipse(Pn(ch, w), -R, -R, R * 2, R * 2);
            if (t >= 5) g.DrawEllipse(Pn(Gold, 1.4f), -R - 4, -R - 4, R * 2 + 8, R * 2 + 8);
            for (int i = 0; i < 4; i++)
            {
                var a = Pol(0, 0, 8, i * 90); var b = Pol(0, 0, 21.5f, i * 90);
                if (t >= 3) L(g, Ink, w + 2.6f, a.X, a.Y, b.X, b.Y);
                L(g, ch, w, a.X, a.Y, b.X, b.Y);
            }
            switch (t)
            {
                case 1: OE(g, C(0xADB5BD), 0, 0, 7.5f, 7.5f); D(g, C(0xDEE2E6), -2.5f, -2.5f, 2.6f); D(g, White, -3, -3, 1.2f); break;
                case 2: OE(g, White, 0, 0, 10.5f, 6.5f); D(g, C(0x51CF66), 0, 0, 4.8f); D(g, Ink, 0, 0, 2.4f); D(g, White, -1.4f, -1.4f, 1.1f); break;
                case 3: OE(g, C(0xFF4D4D), 0, 0, 3.4f, 3.4f); break;
                case 4:
                    {
                        var st = g.Save(); g.RotateTransform(-30);
                        L(g, Al(White, 0.7f), 2, -22, -4, -14, -4); L(g, Al(White, 0.7f), 2, -22, 4, -14, 4);
                        ORect(g, C(0xADB5BD), -13, -3.6f, 17, 7.2f, 3.6f);
                        OPoly(g, C(0xF1F3F5), P(3, -5), P(14, 0), P(3, 5));
                        g.Restore(st);
                        break;
                    }
                default: Star(g, Ink, 0, 0, 8.5f); Star(g, C(0xFFF3BF), 0, 0, 6.6f); break;
            }
        }

        // --- Klubíčkářka ---
        static void IcBoom(Graphics g, int t)
        {
            switch (t)
            {
                case 1: YarnBall(g, 0, 1, 14.5f, C(0xE64980)); break;
                case 2:
                    YarnBall(g, 0, 1, 12, C(0xE64980));
                    Bez(g, Ink, 3.8f, P(-6, 10), P(-26, 18), P(-24, -14), P(-8, -10));
                    Bez(g, C(0xE64980), 2, P(-6, 10), P(-26, 18), P(-24, -14), P(-8, -10));
                    Bez(g, Ink, 3.8f, P(8, -8), P(26, -18), P(26, 16), P(10, 9));
                    Bez(g, C(0xE64980), 2, P(8, -8), P(26, -18), P(26, 16), P(10, 9));
                    break;
                default:
                    {
                        if (t >= 5) { Burst(g, C(0xFF6B00), 0, 1, 24, 13, 12, 8); Burst(g, C(0xFFD43B), 0, 1, 16, 9, 10, 0); }
                        else if (t >= 4) Burst(g, C(0xFFA94D), 0, 2, 22, 13, 12, 0);
                        Color y = t >= 5 ? C(0xF03E3E) : t >= 4 ? C(0x3B2A8C) : C(0x5F3DC4);
                        float r = t >= 5 ? 8.5f : 11.5f;
                        YarnBall(g, -2, 3, r, y);
                        if (t <= 4)
                        {
                            Bez(g, Ink, 3.6f, P(-2 + r * 0.6f, 3 - r * 0.75f), P(6, -12), P(10, -12), P(14, -17));
                            Bez(g, C(0xC9A26B), 1.8f, P(-2 + r * 0.6f, 3 - r * 0.75f), P(6, -12), P(10, -12), P(14, -17));
                            Star(g, Ink, 15, -18, 7); Star(g, Gold, 15, -18, 5.4f); Star(g, C(0xFF6B6B), 15, -18, 2.4f);
                        }
                        else for (int i = 0; i < 6; i++) { var p = Pol(0, 1, 21, i * 60 + 30); OE(g, i % 2 == 0 ? C(0xE64980) : Gold, p.X, p.Y, 2.2f, 2.2f); }
                        break;
                    }
            }
        }

        static void IcThrow(Graphics g, int t)
        {
            switch (t)
            {
                case 1:
                    SpeedLines(g, -13, -2, 3);
                    OPaw(g, C(0x212529), C(0xF1F3F5), 4, 0, 8.5f);
                    ORect(g, C(0x339AF0), -5, 13, 18, 5.5f, 2);
                    break;
                case 2:
                    for (int i = 0; i < 4; i++)
                    {
                        var p = Pol(-1, 1, 15, 45 + i * 90); var q = Pol(-1, 1, 8, 45 + i * 90);
                        L(g, Al(White, 0.6f), 2, q.X, q.Y, p.X, p.Y);
                        YarnBall(g, p.X, p.Y, 4.6f, i % 2 == 0 ? C(0x4DABF7) : C(0xFFD43B));
                    }
                    YarnBall(g, -1, 1, 8, C(0xE64980));
                    break;
                case 3:
                    using (var dp = new Pen(Al(White, 0.85f), 2.2f))
                    {
                        dp.DashPattern = new float[] { 2, 1.6f };
                        g.DrawBezier(dp, -20, 18, -14, -14, 4, -18, 14, -6);
                    }
                    OE(g, C(0xF1F3F5), -20, 18, 3.5f, 1.8f);
                    YarnBall(g, 15, -5, 6.8f, C(0xE64980));
                    break;
                case 4:
                    {
                        OE(g, Al(White, 0.75f), 6, -5, 6, 5);
                        YarnBall(g, 15, -12, 6, C(0xE64980));
                        var st = g.Save(); g.RotateTransform(-28);
                        ORect(g, C(0x5C6670), -18, -6.5f, 24, 13, 5.5f);
                        ORect(g, C(0xFD7E14), 3, -8, 4.5f, 16, 2);
                        g.Restore(st);
                        OE(g, C(0x8B5A2B), -8, 10, 5.5f, 5.5f); D(g, C(0x5C3A1E), -8, 10, 1.8f);
                        break;
                    }
                default:
                    {
                        Color[] yc = { C(0xE64980), C(0x4DABF7), C(0xFFD43B) };
                        for (int i = 0; i < 3; i++)
                        {
                            var p = Pol(-14, 4, 26, -48 + i * 34);
                            L(g, Al(yc[i], 0.55f), 5, -14 + (p.X + 14) * 0.35f, 4 + (p.Y - 4) * 0.35f, p.X, p.Y);
                            YarnBall(g, p.X, p.Y, 6, yc[i]);
                        }
                        Star(g, Gold, -16, -14, 3.5f); Star(g, Gold, -10, 17, 2.6f);
                        break;
                    }
            }
        }

        static void IcKnit(Graphics g, int t)
        {
            switch (t)
            {
                case 1:
                    Bez(g, Ink, 5.4f, P(-21, 14), P(-12, -4), P(-2, 18), P(7, -1));
                    Bez(g, C(0x20C997), 3, P(-21, 14), P(-12, -4), P(-2, 18), P(7, -1));
                    YarnBall(g, 11, -7, 8, C(0x20C997));
                    break;
                case 2:
                    YarnBall(g, 0, 3, 12, C(0x20C997));
                    for (int s = -1; s <= 1; s += 2)
                    {
                        OL(g, C(0xE9C46A), 2.4f, s * -15, -17, s * 13, 16);
                        OE(g, C(0xE76F51), s * -15, -17, 2.6f, 2.6f);
                    }
                    break;
                case 3:
                    WebShape(g, 0, 0, 20, 20, Ink, 3.4f, 3);
                    WebShape(g, 0, 0, 20, 20, White, 1.7f, 3);
                    break;
                case 4:
                    OPoly(g, C(0x12B886), P(-8, -16), P(8, -16), P(20, -7), P(15, 1), P(11, -2), P(11, 17), P(-11, 17), P(-11, -2), P(-15, 1), P(-20, -7));
                    E(g, C(0x0C8F6A), 0, -16, 5, 2.4f);
                    for (int i = 0; i < 5; i++) L(g, C(0x63E6BE), 1.2f, -9 + i * 4.5f, 13, -9 + i * 4.5f, 16.5f);
                    WebShape(g, 0, 3, 7.5f, 7.5f, Kit.A(Ink, 140), 2.6f, 2);
                    WebShape(g, 0, 3, 7.5f, 7.5f, White, 1.3f, 2);
                    break;
                default:
                    E(g, Al(C(0xA5D8FF), 0.35f), 0, 0, 21, 21);
                    WebShape(g, 0, 0, 21, 21, Ink, 4, 3);
                    WebShape(g, 0, 0, 21, 21, C(0xD0EBFF), 2.2f, 3);
                    Snowflake(g, White, 0, 0, 8, 2);
                    Star(g, White, 14, -14, 3); Star(g, White, -15, 13, 2.6f);
                    break;
            }
        }

        // --- Laserovka ---
        static void IcBeam(Graphics g, int t)
        {
            Color red = C(0xFF3B3B);
            switch (t)
            {
                case 1:
                case 2:
                    {
                        L(g, Al(red, 0.4f), 6, -4, 4, 16, -8);
                        L(g, red, 2.4f, -4, 4, 16, -8);
                        D(g, Al(red, 0.4f), 16, -8, 6); D(g, C(0xFFC9C9), 16, -8, 2.6f);
                        if (t == 2)
                        {
                            L(g, Al(red, 0.4f), 6, 4, -1, 18, 10); L(g, red, 2.4f, 4, -1, 18, 10);
                            D(g, Al(red, 0.4f), 18, 10, 6); D(g, C(0xFFC9C9), 18, 10, 2.6f);
                        }
                        OL(g, C(0x343A40), 6, -19, 13, -5, 5);
                        L(g, C(0xFF6B6B), 3, -12, 9, -10, 8);
                        break;
                    }
                case 3:
                    {
                        L(g, Al(C(0x66D9E8), 0.45f), 9, 6, -3, 22, -12);
                        L(g, C(0xE3FAFC), 3.4f, 6, -3, 22, -12);
                        var st = g.Save(); g.RotateTransform(-30);
                        ORect(g, C(0x5C6670), -20, -6, 24, 12, 5);
                        for (int i = 0; i < 3; i++) g.FillRectangle(Kit.B(C(0x66D9E8)), -13 + i * 5.5f, -6, 2, 12);
                        ORect(g, C(0x343A40), 2, -7.5f, 4.5f, 15, 1.5f);
                        g.Restore(st);
                        break;
                    }
                case 4:
                    {
                        Color[] bc = { C(0xE599F7), C(0x66D9E8), C(0xE599F7) };
                        for (int i = 0; i < 3; i++)
                        {
                            var p = Pol(-12, 6, 30, -50 + i * 32);
                            L(g, Al(bc[i], 0.45f), 6, -12, 6, p.X, p.Y); L(g, bc[i], 2.4f, -12, 6, p.X, p.Y);
                            D(g, White, p.X, p.Y, 2.4f);
                        }
                        OE(g, C(0x5F3DC4), -12, 6, 7.5f, 7.5f);
                        D(g, C(0xE599F7), -12, 6, 3.5f);
                        break;
                    }
                default:
                    L(g, Al(Gold, 0.5f), 14, -2, 2, 24, -10);
                    L(g, C(0xFFF3BF), 5, -2, 2, 24, -10);
                    SunDisc(g, -6, 4, 10.5f, 0);
                    break;
            }
        }

        static void IcOptics(Graphics g, int t)
        {
            switch (t)
            {
                case 1:
                    ORect(g, C(0xFAB005), -15, -9, 27, 18, 3.5f);
                    ORect(g, C(0x868E96), 12, -4, 4, 8, 1.2f);
                    Lightning(g, Ink, -1.5f, 0, 7.5f);
                    break;
                case 2:
                    OL(g, C(0x343A40), 3, -22, 0, 22, 0);
                    ORect(g, C(0x343A40), -19, -8, 38, 16, 7);
                    ORect(g, C(0xFF6B6B), -16, -4.5f, 32, 9, 4.5f);
                    L(g, Al(White, 0.8f), 1.6f, -12, -2, -4, -2);
                    break;
                case 3:
                    L(g, C(0xADB5BD), 1.4f, 0, -22, 0, -10);
                    DiscoBall(g, 0, 3, 13);
                    break;
                default:
                    {
                        if (t >= 5)
                        {
                            Color[] rb = { C(0xFF6B6B), C(0xFFD43B), C(0x69DB7C), C(0x4DABF7), C(0xCC5DE8) };
                            for (int i = 0; i < 5; i++) { var p = Pol(0, 1, 24, 30 + i * 30); L(g, Al(rb[i], 0.8f), 3, 0, 1, p.X, p.Y); }
                        }
                        else
                            for (int s = -1; s <= 1; s += 2) { L(g, Al(C(0xFF6B6B), 0.85f), 2, 0, 1, s * 16, -13); L(g, Al(C(0xFF6B6B), 0.85f), 2, s * 16, -13, s * 20, 14); }
                        if (t == 4)
                            for (int s = -1; s <= 1; s += 2) OPoly(g, C(0xE9ECEF), P(s * 16, -19), P(s * 21, -13), P(s * 16, -7), P(s * 11, -13));
                        L(g, C(0xADB5BD), 1.4f, 0, -22, 0, -10);
                        DiscoBall(g, 0, 1, t >= 5 ? 11.5f : 10.5f);
                        if (t >= 5) { Star(g, White, -17, -15, 3.2f); Star(g, White, 17, -15, 3.2f); }
                        break;
                    }
            }
        }

        static void IcHeat(Graphics g, int t)
        {
            switch (t)
            {
                case 1:
                    for (int i = 0; i < 3; i++)
                    {
                        float x = -9 + i * 9;
                        Bez(g, Ink, 4.4f, P(x, 16), P(x - 6, 8), P(x + 6, 0), P(x, -14));
                        Bez(g, i == 1 ? C(0xFF6B00) : C(0xFFA94D), 2.4f, P(x, 16), P(x - 6, 8), P(x + 6, 0), P(x, -14));
                    }
                    break;
                case 2: Flame(g, 0, 10, 11, -90, true, 1); break;
                case 3:
                    Flame(g, -6, 11, 9.5f, -90, true, 1);
                    ORect(g, White, 8, -19, 7, 26, 3.5f);
                    g.FillRectangle(Kit.B(C(0xF03E3E)), 10.5f, -10, 2, 17);
                    OE(g, C(0xF03E3E), 11.5f, 11, 5.5f, 5.5f);
                    break;
                case 4:
                    Flame(g, -11, 13, 7, -110, true, 1); Flame(g, 11, 13, 7, -70, true, 1);
                    Flame(g, 0, 11, 11.5f, -90, true, 1);
                    D(g, C(0xFFA94D), -16, -10, 1.4f); D(g, C(0xFFD43B), 15, -14, 1.2f);
                    break;
                default:
                    Burst(g, C(0xFF6B00), 0, 0, 23, 11, 12, 0);
                    Burst(g, Gold, 0, 0, 15, 7, 10, 18);
                    OE(g, C(0xFFF9DB), 0, 0, 5.5f, 5.5f);
                    break;
            }
        }

        // --- Spac ---
        static void IcSleep(Graphics g, int t)
        {
            switch (t)
            {
                case 1: Crescent(g, Gold, -4, 3, 13.5f); ZLetter(g, C(0xE7F5FF), 13, -12, 4.5f); break;
                case 2: Crescent(g, Gold, -8, 6, 10.5f); Snowflake(g, C(0xD0EBFF), 10, -8, 9.5f, 2.2f); break;
                case 3:
                    OPoly(g, C(0x4263EB), P(-17, 13), P(11, 13), P(-2, -11), P(-16, -17));
                    ORect(g, White, -19, 10, 32, 6, 3);
                    OE(g, White, -17, -17, 4, 4);
                    L(g, C(0x5F3DC4), 2, 15, 6, 15, -8); L(g, C(0x5F3DC4), 2, 15, -8, 20, -5);
                    OE(g, C(0xD0BFFF), 12.5f, 6, 3.4f, 2.6f);
                    break;
                case 4:
                    Crescent(g, Gold, -13, -11, 7);
                    Snowflake(g, C(0xD0EBFF), 3, 3, 17, 3);
                    break;
                default:
                    for (int i = 0; i < 6; i++) OPoly(g, i % 2 == 0 ? C(0xD0EBFF) : C(0xA5D8FF), Pol(0, 0, 9, i * 60), Pol(0, 0, 22, i * 60 + 30), Pol(0, 0, 9, i * 60 + 60));
                    Snowflake(g, White, 0, 0, 13, 2.8f);
                    Star(g, White, 15, -15, 3);
                    break;
            }
        }

        static void IcCushion(Graphics g, int t)
        {
            Color cu = C(0xB197FC);
            if (t == 1 || t == 2)
            {
                float cy = t == 1 ? 3 : 9, rx = t == 1 ? 19 : 16, ry = t == 1 ? 14 : 10;
                OE(g, Dk(cu, 0.15f), 0, cy, rx, ry);
                E(g, cu, 0, cy - 1, rx - 3.5f, ry - 3.5f);
                for (int i = 0; i < 4; i++) { var p = Pol(0, cy, 1, 45 + i * 90); D(g, Dk(cu, 0.3f), p.X * rx * 0.6f, cy + (p.Y - cy) * ry * 0.6f, 1.6f); }
                E(g, Kit.A(White, 70), -6, cy - ry * 0.5f, rx * 0.4f, 2);
                if (t == 2)
                {
                    for (int i = 0; i < 5; i++) { var a = Pol(0, -9, 13, 200 + i * 35); var b = Pol(0, -9, 17, 200 + i * 35); L(g, Gold, 1.8f, a.X, a.Y, b.X, b.Y); }
                    OE(g, White, 0, -9, 11, 6.5f);
                    D(g, C(0x845EF7), 0, -9, 4.6f); D(g, Ink, 0, -9, 2.2f); D(g, White, -1.4f, -10.4f, 1);
                }
                return;
            }
            if (t >= 5)
            {
                for (int i = 0; i < 6; i++)
                {
                    float dg = i * 60 + 10;
                    var pts = new[] { Pol(0, 4, 6, dg), Pol(0, 4, 12, dg + 10), Pol(0, 4, 17, dg - 6), Pol(0, 4, 22, dg + 4) };
                    g.DrawLines(Pn(Ink, 3.4f), pts); g.DrawLines(Pn(C(0xD0BFFF), 1.4f), pts);
                }
                g.DrawArc(Pn(C(0x9775FA), 2.4f), -20, -16, 40, 40, 200, 140);
            }
            float bx = t >= 4 ? -4 : 0, bs = t >= 4 ? 12 : 15;
            OE(g, Al(C(0xD0EBFF), 0.9f), bx, 0, bs, bs);
            g.DrawEllipse(Pn(C(0x4DABF7), 1.4f), bx - bs, -bs, bs * 2, bs * 2);
            D(g, Al(White, 0.85f), bx - bs * 0.4f, -bs * 0.45f, bs * 0.25f);
            ZLetter(g, C(0x5F3DC4), bx + 1, 1, bs * 0.38f);
            if (t == 4)
                for (int i = 0; i < 3; i++) { float rr = 15 + i * 4.5f; g.DrawArc(Pn(Ink, 4), bx - rr, -rr, rr * 2, rr * 2, -40, 80); g.DrawArc(Pn(C(0xB197FC), 2), bx - rr, -rr, rr * 2, rr * 2, -40, 80); }
        }

        static void IcComfort(Graphics g, int t)
        {
            switch (t)
            {
                case 1: Heart(g, C(0xF06595), 0, -1, 12.5f); break;
                case 2: Heart(g, C(0xF06595), -5, -5, 10); OPaw(g, C(0xD9480F), C(0xFFD8A8), 10, 11, 5.4f); break;
                case 3:
                    g.DrawArc(Pn(Ink, 6.5f), -15, -16, 30, 30, 180, 180);
                    g.DrawArc(Pn(C(0xF783AC), 3.6f), -15, -16, 30, 30, 180, 180);
                    for (int s = -1; s <= 1; s += 2) ORect(g, C(0xE64980), s * 15 - 4.5f, -4, 9, 14, 4);
                    Heart(g, C(0xFFF0F6), 0, 4, 4.5f);
                    break;
                default:
                    {
                        if (t >= 5) { g.DrawEllipse(Pn(Ink, 5.4f), -12, -22, 24, 9); g.DrawEllipse(Pn(Gold, 3), -12, -22, 24, 9); }
                        float cy = t >= 5 ? 6 : 3;
                        for (int i = 0; i < 5; i++)
                        {
                            float dg = -90 + (i - 2) * 38;
                            OPoly(g, i % 2 == 0 ? C(0xF783AC) : C(0xFCC2D7), P(0, cy + 6), Pol(0, cy + 6, 13, dg - 16), Pol(0, cy + 6, 21, dg), Pol(0, cy + 6, 13, dg + 16));
                        }
                        OE(g, Gold, 0, cy + 5, 4, 3);
                        if (t >= 5) { Star(g, White, -17, -10, 3); Star(g, White, 17, -8, 2.6f); }
                        break;
                    }
            }
        }

        // --- Pamlskárna ---
        static void IcStock(Graphics g, int t)
        {
            switch (t)
            {
                case 1:
                    for (int i = 0; i < 7; i++) OE(g, C(0xC27C3A), -10 + i * 3.3f, -1 - (i % 2) * 2.5f - (i == 3 ? 3 : 0), 3, 2.2f);
                    OE(g, C(0x4DABF7), 0, 6, 18, 9);
                    E(g, C(0x1C7ED6), 0, 3.5f, 13, 4);
                    for (int i = 0; i < 5; i++) OE(g, C(0xC27C3A), -8 + i * 4, 3 - (i % 2), 2.4f, 1.8f);
                    break;
                case 2:
                    ORect(g, C(0xB07A45), -17, -12, 34, 26, 3);
                    L(g, C(0x8A5A2E), 1.4f, -16, -3, 16, -3); L(g, C(0x8A5A2E), 1.4f, -16, 6, 16, 6);
                    TreatShape(g, 0, 1, 22);
                    break;
                case 3:
                    {
                        var st = g.Save(); g.ScaleTransform(1.55f, 1.55f);
                        Can(g, -6, 5); Can(g, 6, 5); Can(g, 0, -7);
                        g.Restore(st);
                        break;
                    }
                case 4:
                    Fish(g, -8, -9, -70); Fish(g, 1, -12, -95); Fish(g, 10, -9, -115);
                    ORect(g, C(0x8F5E34), -18, -6, 36, 22, 3);
                    L(g, C(0x6B4423), 1.4f, -17, 4, 17, 4);
                    Kit.FillRound(g, C(0xF8F0E3), new RectangleF(-7, -2, 14, 5), 1);
                    break;
                default:
                    for (int i = 0; i < 6; i++) TreatShape(g, -12 + (i % 3) * 12 + (i / 3) * 6, 14 - (i / 3) * 8, 14);
                    TreatShape(g, 0, -2, 14);
                    OPoly(g, Gold, P(-13, -6), P(-15, -20), P(-7, -13), P(0, -23), P(7, -13), P(15, -20), P(13, -6));
                    D(g, C(0xE03131), 0, -10, 2.2f);
                    break;
            }
        }

        static void IcCare(Graphics g, int t)
        {
            switch (t)
            {
                case 1:
                    Heart(g, C(0xF03E3E), 0, -1, 12.5f);
                    g.FillRectangle(Kit.B(White), -2, -7, 4, 11); g.FillRectangle(Kit.B(White), -5.5f, -3.5f, 11, 4);
                    break;
                case 2:
                    for (int i = 0; i < 4; i++) Coin(g, -6, 14 - i * 5, 11, 4.5f);
                    OE(g, White, 9, -14, 3, 3); OE(g, White, 18, -4, 3, 3); OL(g, White, 2, 19, -16, 8, -2);
                    break;
                case 3:
                    for (int i = 0; i < 7; i++) OE(g, C(0xC27C3A), -10 + i * 3.3f, -1 - (i % 2) * 2.5f - (i == 3 ? 3 : 0), 3, 2.2f);
                    OE(g, Gold, 0, 6, 18, 9);
                    E(g, GoldDk, 0, 3.5f, 13, 4);
                    Star(g, White, 15, -10, 4); Star(g, White, -16, -7, 3);
                    break;
                case 4:
                    Coin(g, 2, -18, 5.5f, 2.6f);
                    for (int s = -1; s <= 1; s += 2) ORect(g, C(0xF783AC), s * 7 - 2.5f, 10, 5, 7, 2);
                    OPoly(g, C(0xF783AC), P(-9, -8), P(-12, -16), P(-4, -10));
                    OE(g, C(0xFCC2D7), -1, 3, 16, 11.5f);
                    OE(g, C(0xF783AC), 15, 4, 3.6f, 4.6f);
                    D(g, C(0xC2255C), 14.5f, 3, 0.9f); D(g, C(0xC2255C), 14.5f, 5.5f, 0.9f);
                    D(g, Ink, 8, -1, 1.4f);
                    ORect(g, C(0xC2255C), -5, -8.2f, 9, 2.4f, 1.2f);
                    break;
                default:
                    for (int i = 0; i < 3; i++) Coin(g, -11, 16 - i * 4.5f, 8, 3.4f);
                    OL(g, C(0x40C057), 3.2f, -18, 6, -6, -2);
                    OL(g, C(0x40C057), 3.2f, -6, -2, 0, 2);
                    OL(g, C(0x40C057), 3.2f, 0, 2, 13, -11);
                    OPoly(g, C(0x40C057), P(7, -13), P(17, -16), P(14, -6));
                    ORect(g, C(0x343A40), 0, 10, 20, 4, 1.5f);
                    ORect(g, C(0x343A40), 3.5f, -1, 13, 12, 1.5f);
                    g.FillRectangle(Kit.B(Gold), 3.5f, 6.5f, 13, 2.4f);
                    break;
            }
        }

        static void IcDelivery(Graphics g, int t)
        {
            switch (t)
            {
                case 1: Parcel(g, 0, 2, 28, 22); break;
                case 2:
                    OPoly(g, C(0xE9D8A6), P(-15, 17), P(-13, -4), P(-6, -13), P(6, -13), P(13, -4), P(15, 17));
                    OL(g, C(0xA9733F), 2, -6, -12, 6, -12);
                    TreatShape(g, 0, 5, 16);
                    break;
                case 3:
                    Parcel(g, 10, 10, 16, 13);
                    OE(g, C(0x1C7ED6), -4, -2, 14, 11);
                    ORect(g, C(0x1864AB), -20, 4, 26, 5, 2.5f);
                    OE(g, Gold, -4, -6, 3, 3);
                    break;
                case 4:
                    SpeedLines(g, -15, 4, 3);
                    for (int s = -1; s <= 1; s += 2) OPoly(g, White, P(s * 8, -4), P(s * 23, -15), P(s * 21, -7), P(s * 24, -2), P(s * 8, 3));
                    Parcel(g, 0, 4, 20, 16);
                    break;
                default:
                    OE(g, C(0xDEE2E6), 12, -19, 5.5f, 4.5f); OE(g, C(0xF1F3F5), 4, -21, 4, 3.4f);
                    ORect(g, C(0xC2603F), 9, -15, 7, 16, 1.5f);
                    OPoly(g, C(0xC2603F), P(-20, 18), P(-20, -2), P(-12, -8), P(-12, -2), P(-4, -8), P(-4, -2), P(4, -8), P(4, -2), P(20, -2), P(20, 18));
                    for (int i = 0; i < 3; i++) Kit.FillRound(g, C(0xFFE066), new RectangleF(-16 + i * 9, 3, 6, 5), 1);
                    ORect(g, C(0x1C7ED6), 10, 8, 7, 10, 1.5f);
                    TreatShape(g, -6, 13, 10);
                    break;
            }
        }

        // =====================================================================
        // VACUUMS (round robots seen from above, front bumper towards +x)
        // =====================================================================

        public static void Vacuum(Graphics g, VacKind k, float x, float y, float angle, bool stealth, float slow, bool frozen, float hpFrac, double t)
        {
            Vacuum(g, k, x, y, angle, stealth, slow, frozen, hpFrac, 0, t);
        }

        /// <summary>tough 0..4 (Vac.Tough): free-play reinforcement drawn on the rim, damage cracks on top of it.</summary>
        public static void Vacuum(Graphics g, VacKind k, float x, float y, float angle, bool stealth, float slow, bool frozen, float hpFrac, int tough, double t)
        {
            var d = Defs.Vac(k);
            tough = Math.Max(0, Math.Min(4, tough));
            int ab = AngleBucket(angle, 64);
            int tint = slow > 0.45f ? 2 : slow > 0.04f ? 1 : 0;
            int dmg = 0;
            if (k == VacKind.Keramik || d.Boss) dmg = hpFrac > 0.75f ? 0 : hpFrac > 0.5f ? 1 : hpFrac > 0.25f ? 2 : 3;
            int frame = frozen ? 0 : (int)(((long)Math.Floor(t * 16 * (1 - Math.Min(0.8f, slow))) % 4 + 4) % 4);
            int sq = Dev(g, x, y);
            long key = Key(3, sq, ((long)k << 40) + ((stealth ? 1L : 0L) << 36) + ((long)tint << 32) + ((frozen ? 1L : 0L) << 30)
                + (tough << 26) + (dmg << 24) + (frame << 12) + ab);
            var s = Find(key);
            if (s == null)
            {
                int half = (int)Math.Ceiling(k == VacKind.Raketa ? d.Radius * 1.75f + 4 : d.Boss ? d.Radius * 1.3f + 8 : d.Radius * 1.32f + 6);
                using (var vg = Begin(key, half, sq, out s))
                    PaintVac(vg, k, (float)(ab * 2 * Math.PI / 64), stealth, tint, frozen, dmg, tough, frame * 30f, frame);
            }
            Blit(g, s);
        }

        static float vAlpha = 1, vTint;
        static readonly Color SlowBlue = C(0x5C9DFF);

        static Color V(Color c)
        {
            if (vTint > 0) c = Kit.Mix(c, Kit.A(SlowBlue, c.A), vTint);
            return vAlpha < 1 ? Kit.A(c, (int)(c.A * vAlpha)) : c;
        }

        static void VD(Graphics g, Color c, float x, float y, float r) { D(g, V(c), x, y, r); }

        static void PaintVac(Graphics g, VacKind k, float ang, bool stealth, int tint, bool frozen, int dmg, int tough, float brushDeg, int frame)
        {
            var d = Defs.Vac(k);
            float r = d.Radius;
            Color body = C(d.Color), rim = Kit.Mix(Dk(body, 0.5f), C(0x2B3440), 0.45f);
            vAlpha = stealth ? 0.45f : 1;
            vTint = tint * 0.24f;
            oc = stealth ? Kit.A(Ink, 110) : Ink;
            float ca = (float)Math.Cos(ang), sa = (float)Math.Sin(ang);
            if (!stealth)
            {
                E(g, Kit.A(Color.Black, 26), 2, 4.5f, r + 3, r + 2.5f);
                E(g, Kit.A(Color.Black, 42), 1.5f, 3, r + 1, r + 1);
            }
            var st = g.Save();
            g.RotateTransform(ang * Deg);

            // side brushes peeking out at the front corners
            float rb = r * 0.42f;
            var bp = Pn(V(C(0x495057)), Math.Max(1.1f, r * 0.075f));
            for (int s = -1; s <= 1; s += 2)
            {
                float bx = r * 0.5f, by = s * r * 0.72f;
                D(g, V(Kit.A(C(0x495057), 80)), bx, by, rb);
                for (int i = 0; i < 3; i++)
                {
                    var tip = Pol(bx, by, rb, s * brushDeg + i * 120);
                    g.DrawLine(bp, bx, by, tip.X, tip.Y);
                }
            }
            if (k == VacKind.Raketa)
            {
                float fl = r * (0.6f + 0.25f * (frame % 2));
                Poly(g, V(C(0xFF922B)), P(-r * 0.8f, -r * 0.34f), P(-r * 0.85f - fl, 0), P(-r * 0.8f, r * 0.34f));
                Poly(g, V(C(0xFFE066)), P(-r * 0.8f, -r * 0.18f), P(-r * 0.85f - fl * 0.6f, 0), P(-r * 0.8f, r * 0.18f));
                for (int s = -1; s <= 1; s += 2)
                    OPoly(g, V(Dk(body, 0.25f)), P(-r * 0.45f, s * r * 0.8f), P(-r * 1.2f, s * r * 1.08f), P(-r * 0.95f, s * r * 0.3f));
            }
            if (k == VacKind.Prumyslak)
            {
                OL(g, V(C(0x495057)), r * 0.16f, r * 0.2f, r * 0.55f, r * 1.05f, r * 0.62f);
                ORect(g, V(C(0xFCC419)), r * 0.98f, r * 0.42f, r * 0.24f, r * 0.42f, 2);
            }

            // body: outline, bumper rim, lid offset back so the bumper shows as a front crescent
            if (!stealth) D(g, Ink, 0, 0, r + O);
            VD(g, rim, 0, 0, r);
            if (k == VacKind.Obr)
            {
                using (var sp = new Pen(V(C(0xFCC419)), r * 0.2f))
                    for (int i = 0; i < 18; i++) g.DrawArc(sp, -r * 0.9f, -r * 0.9f, r * 1.8f, r * 1.8f, i * 20, 10);
            }
            float lx = -r * 0.14f, lr = r * 0.8f;
            if (k == VacKind.Pancerak) lr = r * 0.77f;
            g.DrawArc(Pn(V(Lt(rim, 0.3f)), Math.Max(1f, r * 0.07f)), -r * 0.83f, -r * 0.83f, r * 1.66f, r * 1.66f, -58, 116);
            VD(g, Dk(body, 0.2f), lx, 0, lr + 1.1f);
            VD(g, body, lx, 0, lr);
            Color eye = d.Boss ? C(0xFF6B6B) : C(0xE3FAFC);
            float er = Math.Max(1.5f, r * 0.11f);
            var e1 = Pol(0, 0, r * 0.83f, -30); var e2 = Pol(0, 0, r * 0.83f, 30);
            VD(g, eye, e1.X, e1.Y, er); VD(g, eye, e2.X, e2.Y, er);

            VacFeatures(g, k, r, lx, lr, body, frame);
            Reinforce(g, r, lx, lr, d.Boss, tough, dmg, frame);
            if (k == VacKind.Keramik) KeramikCracks(g, r, lx, lr, body, dmg);
            else if (d.Boss) BossDamage(g, r, lx, lr, body, dmg, frame);
            g.Restore(st);

            // gloss in world space (light from the top left)
            float hx = lx * ca, hy = lx * sa;
            E(g, Kit.A(White, (int)(70 * vAlpha)), hx - r * 0.3f, hy - r * 0.36f, r * 0.32f, r * 0.17f);
            if (stealth)
            {
                using (var dp = new Pen(Kit.A(Ink, 220), d.Boss ? 2.6f : 1.8f))
                {
                    dp.DashPattern = new float[] { 2.2f, 1.6f };
                    g.DrawEllipse(dp, -r - 0.6f, -r - 0.6f, r * 2 + 1.2f, r * 2 + 1.2f);
                }
                D(g, Kit.A(White, 60), 0, 0, r * 0.25f);
            }
            if (frozen)
            {
                float fr = r + 2.5f;
                D(g, Kit.A(C(0xC5F6FA), 140), 0, 0, fr);
                var shard = Kit.B(Kit.A(White, 170));
                g.FillPolygon(shard, new[] { P(-fr * 0.7f, -fr * 0.2f), P(-fr * 0.35f, -fr * 0.75f), P(-fr * 0.15f, -fr * 0.55f), P(-fr * 0.5f, -fr * 0.05f) });
                g.FillPolygon(shard, new[] { P(fr * 0.25f, fr * 0.35f), P(fr * 0.65f, fr * 0.05f), P(fr * 0.6f, fr * 0.35f), P(fr * 0.35f, fr * 0.6f) });
                Kit.Ring(g, Kit.A(C(0x74C0FC), 235), d.Boss ? 2.4f : 1.6f, 0, 0, fr);
                Star(g, White, fr * 0.45f, -fr * 0.5f, Math.Max(2.5f, r * 0.2f));
            }
            vAlpha = 1; vTint = 0; oc = Ink;
        }

        static void VacFeatures(Graphics g, VacKind k, float r, float lx, float lr, Color body, int frame)
        {
            switch (k)
            {
                case VacKind.Robik:
                    g.DrawArc(Pn(V(Dk(body, 0.25f)), 1), lx - lr * 0.6f, -lr * 0.6f, lr * 1.2f, lr * 1.2f, 130, 100);
                    break;
                case VacKind.Plus:
                    g.FillRectangle(Kit.B(V(White)), -r * 0.62f, -r * 0.09f, r * 0.56f, r * 0.18f);
                    g.FillRectangle(Kit.B(V(White)), -r * 0.43f, -r * 0.28f, r * 0.18f, r * 0.56f);
                    break;
                case VacKind.Turbo:
                    g.FillRectangle(Kit.B(V(White)), -r * 0.85f, -r * 0.32f, r * 0.95f, r * 0.13f);
                    g.FillRectangle(Kit.B(V(White)), -r * 0.85f, r * 0.19f, r * 0.95f, r * 0.13f);
                    break;
                case VacKind.Sprint:
                    Poly(g, V(Ink), P(-r * 0.62f, -r * 0.05f), P(-r * 0.12f, -r * 0.42f), P(-r * 0.3f, -r * 0.08f), P(-r * 0.02f, r * 0.02f), P(-r * 0.52f, r * 0.42f), P(-r * 0.34f, r * 0.08f));
                    break;
                case VacKind.Raketa:
                    VD(g, Ink, -r * 0.32f, 0, r * 0.3f);
                    VD(g, White, -r * 0.32f, 0, r * 0.24f);
                    VD(g, C(0x74C0FC), -r * 0.32f, 0, r * 0.17f);
                    break;
                case VacKind.Dvojce:
                    g.FillPie(Kit.B(V(C(0xF1F3F5))), lx - lr, -lr, lr * 2, lr * 2, 0, 180);
                    L(g, V(Ink), 1.2f, lx - lr, 0, lx + lr, 0);
                    VD(g, Ink, r * 0.25f, -r * 0.34f, r * 0.17f); VD(g, C(0xFFD43B), r * 0.25f, -r * 0.34f, r * 0.08f);
                    VD(g, Ink, r * 0.25f, r * 0.34f, r * 0.17f); VD(g, C(0x66D9E8), r * 0.25f, r * 0.34f, r * 0.08f);
                    return;
                case VacKind.Pancerak:
                    VD(g, Lt(body, 0.15f), lx, 0, lr * 0.62f);
                    g.DrawEllipse(Pn(V(Dk(body, 0.35f)), 1.1f), lx - lr * 0.62f, -lr * 0.62f, lr * 1.24f, lr * 1.24f);
                    L(g, V(Dk(body, 0.35f)), 1.1f, lx - lr * 0.62f, 0, lx + lr * 0.62f, 0);
                    for (int i = 0; i < 8; i++)
                    {
                        var p = Pol(lx, 0, lr * 0.82f, i * 45 + 22.5f);
                        VD(g, Dk(body, 0.45f), p.X + 0.4f, p.Y + 0.4f, r * 0.075f);
                        VD(g, Lt(body, 0.55f), p.X, p.Y, r * 0.065f);
                    }
                    break;
                case VacKind.Disko:
                    {
                        Color[] lights = { C(0xFF6B6B), C(0xFFD43B), C(0x69DB7C), C(0x4DABF7), C(0xDA77F2), C(0xFFA94D) };
                        for (int i = 0; i < 6; i++)
                        {
                            var p = Pol(lx, 0, lr * 0.68f, i * 60 + 30);
                            bool on = (i + frame) % 2 == 0;
                            VD(g, Dk(body, 0.4f), p.X, p.Y, r * 0.15f);
                            VD(g, on ? lights[(i + frame) % 6] : Dk(lights[(i + frame) % 6], 0.35f), p.X, p.Y, r * 0.11f);
                        }
                        VD(g, Ink, r * 0.05f, 0, r * 0.3f);
                        VD(g, C(0xE9ECEF), r * 0.05f, 0, r * 0.25f);
                        g.DrawLine(Pn(V(C(0x868E96)), 0.8f), r * 0.05f, -r * 0.25f, r * 0.05f, r * 0.25f);
                        g.DrawLine(Pn(V(C(0x868E96)), 0.8f), -r * 0.2f, 0, r * 0.3f, 0);
                        VD(g, White, -r * 0.02f, -r * 0.08f, r * 0.07f);
                        return;
                    }
                case VacKind.Keramik:
                    VD(g, C(0xF3D9B1), lx, 0, lr * 0.6f);
                    VD(g, body, lx, 0, lr * 0.46f);
                    for (int i = 0; i < 10; i++) { var p = Pol(lx, 0, lr * 0.8f, i * 36); VD(g, C(0xF3D9B1), p.X, p.Y, r * 0.07f); }
                    break;
                case VacKind.Prumyslak:
                    {
                        var hose = Pn(V(C(0x343A40)), r * 0.15f);
                        var hose2 = Pn(V(C(0x5C6670)), r * 0.08f);
                        g.DrawArc(hose, lx - r * 0.45f - r * 0.12f, -r * 0.45f, r * 0.9f, r * 0.9f, 20, 300);
                        g.DrawArc(hose2, lx - r * 0.45f - r * 0.12f, -r * 0.45f, r * 0.9f, r * 0.9f, 20, 300);
                        g.DrawArc(hose, lx - r * 0.24f - r * 0.12f, -r * 0.24f, r * 0.48f, r * 0.48f, 200, 260);
                        VD(g, C(0xFCC419), lx - r * 0.12f, 0, r * 0.12f);
                        for (int i = -1; i <= 1; i++) ORect(g, V(Dk(body, 0.45f)), -r * 0.86f, i * r * 0.2f - r * 0.05f, r * 0.2f, r * 0.1f, 1.5f);
                        var h1 = Pol(0, 0, r * 0.93f, -45); var h2 = Pol(0, 0, r * 0.93f, 45);
                        VD(g, C(0xFFE066), h1.X, h1.Y, r * 0.09f); VD(g, C(0xFFE066), h2.X, h2.Y, r * 0.09f);
                        return;
                    }
                case VacKind.Obr:
                    {
                        ORect(g, V(C(0x343A40)), r * 0.15f, -r * 0.42f, r * 0.42f, r * 0.84f, r * 0.12f);
                        var sl = Pn(V(C(0x868E96)), 1.6f);
                        for (int i = 0; i < 5; i++) g.DrawLine(sl, r * 0.21f + i * r * 0.075f, -r * 0.34f, r * 0.21f + i * r * 0.075f, r * 0.34f);
                        for (int i = 0; i < 6; i++) { var p = Pol(lx, 0, lr * 0.86f, i * 60); VD(g, Dk(body, 0.4f), p.X, p.Y, r * 0.05f); }
                        g.DrawLine(Pn(V(Dk(body, 0.3f)), 1.4f), lx - lr * 0.75f, -lr * 0.5f, lx - lr * 0.75f, lr * 0.5f);
                        // rotating warning beacon
                        float bx = lx - r * 0.3f;
                        g.FillPie(Kit.B(Kit.A(C(0xFFE066), (int)(110 * vAlpha))), bx - r * 0.55f, -r * 0.55f, r * 1.1f, r * 1.1f, frame * 90 - 20, 40);
                        g.FillPie(Kit.B(Kit.A(C(0xFFE066), (int)(110 * vAlpha))), bx - r * 0.55f, -r * 0.55f, r * 1.1f, r * 1.1f, frame * 90 + 160, 40);
                        VD(g, Ink, bx, 0, r * 0.17f);
                        VD(g, C(0xFF922B), bx, 0, r * 0.14f);
                        VD(g, C(0xFFE066), bx - r * 0.03f, -r * 0.03f, r * 0.06f);
                        return;
                    }
            }
            VD(g, Dk(body, 0.55f), r * 0.3f, 0, r * 0.25f);
            VD(g, C(0x66D9E8), r * 0.3f, 0, r * 0.1f);
        }

        static void KeramikCracks(Graphics g, float r, float lx, float lr, Color body, int dmg)
        {
            // away from the front sensor dot, which hid the first crack at game size
            if (dmg >= 1) Crack(g, r, lx, 0, 200, 0.95f);
            if (dmg >= 2) { Crack(g, r, lx, 0, 60, 0.85f); Crack(g, r, lx, 0, 315, 0.8f); }
            if (dmg >= 3) { Crack(g, r, lx, 0, 130, 0.9f); Crack(g, r, lx, 0, 255, 0.75f); VD(g, Dk(body, 0.55f), lx - lr * 0.5f, lr * 0.45f, r * 0.16f); }
        }

        // ---------- free-play reinforcement (Vac.Tough): a collar round the rim, open at the front bumper ----------
        const float CollarGap = 52;   // half the open front, degrees (keeps the eyes and boss lamps free)

        static void Arc(Graphics g, Color c, float w, float cx, float rad, float a0, float sweep)
        {
            g.DrawArc(Pn(c, w), cx - rad, -rad, rad * 2, rad * 2, a0, sweep);
        }

        /// <summary>An outlined band piece on the circle (cx, 0, rad) from a0 over sweep degrees.</summary>
        static void Collar(Graphics g, float cx, float rad, float w, Color c, float a0, float sweep)
        {
            Arc(g, oc, w + O * 2, cx, rad, a0, sweep);   // round caps (Kit.P) outline the ends too
            Arc(g, V(c), w, cx, rad, a0, sweep);
        }

        static void Bolt(Graphics g, float x, float y, float br, Color c)
        {
            D(g, oc, x, y, br + 0.9f);
            VD(g, c, x, y, br);
            VD(g, Dk(c, 0.5f), x, y, br * 0.4f);
        }

        /// <summary>1 riveted steel band, 2 thicker band + inner band + bolts, 3 heavy dark plates, 4 plates with glowing hot seams.
        /// Damage (dmg 0..3) wears it too: bands snap, plates get scratched and fall off, so the cracks stay readable at every tier.</summary>
        static void Reinforce(Graphics g, float r, float lx, float lr, bool boss, int tough, int dmg, int frame)
        {
            if (tough <= 0) return;
            Color steel = C(0xC3CAD1), plate = C(0x3B424A);
            float rc = r * (boss ? 1.03f : 0.98f);
            float w = tough <= 2 ? Math.Max(2.6f, r * (tough == 1 ? 0.14f : 0.18f)) : Math.Max(4.2f, r * 0.27f);
            // the round end caps stay out of the open front
            float start = CollarGap + (w / 2 + O) / rc * Deg, sweep = 360 - 2 * start;
            const float innerGap = 72;
            if (tough >= 2) Collar(g, lx, lr * 0.96f, Math.Max(1.6f, r * 0.08f), steel, innerGap, 360 - 2 * innerGap);
            if (tough <= 2)
            {
                // the band snaps at two fifths of its length from dmg 2, at four fifths too from dmg 3
                float brk = 16, a = start;
                foreach (int cut in new[] { 2, 4, 5 })
                {
                    float end = start + sweep * cut / 5;
                    bool snapped = cut == 2 && dmg >= 2 || cut == 4 && dmg >= 3;
                    if (!snapped && cut < 5) continue;
                    Collar(g, 0, rc, w, steel, a, end - a - (snapped ? brk / 2 : 0));
                    a = end + brk / 2;
                }
                int n = tough == 1 ? 8 : 4;
                for (int i = 0; i < n; i++)
                {
                    var p = Pol(0, 0, rc, start + sweep * (i + 0.5f) / n);
                    if (tough == 1) VD(g, C(0x495057), p.X, p.Y, Math.Max(0.8f, r * 0.045f));
                    else Bolt(g, p.X, p.Y, Math.Max(1.6f, r * 0.075f), steel);
                }
                return;
            }
            // five plates; from dmg 2 the second one is gone, from dmg 3 the fourth too, dmg 1+ scratches the third
            float ps = sweep / 5;
            bool hot = tough >= 4;
            Color glow = frame % 2 == 0 ? C(0xFFD43B) : C(0xFFA94D), hotEdge = C(0xFF6B00);
            for (int i = 0; i < 5; i++)
            {
                if (i == 1 && dmg >= 2 || i == 3 && dmg >= 3) continue;
                float a0 = start + ps * i;
                Collar(g, 0, rc, w, plate, a0, ps);
                if (hot) Arc(g, V(hotEdge), Math.Max(1f, r * 0.045f), 0, rc - w * 0.28f, a0 + 3, ps - 6);
                var p = Pol(0, 0, rc, a0 + ps / 2);
                float br = Math.Max(1.4f, r * 0.07f);
                D(g, oc, p.X, p.Y, br + 0.9f);
                VD(g, hot ? glow : steel, p.X, p.Y, br);
                if (i == 2 && dmg >= 1)
                {
                    // light scratch: ink cracks would vanish on the dark plate
                    var s0 = Pol(0, 0, rc + w * 0.45f, a0 + ps * 0.62f); var s1 = Pol(0, 0, rc + w * 0.05f, a0 + ps * 0.74f);
                    var s2 = Pol(0, 0, rc - w * 0.1f, a0 + ps * 0.84f); var s3 = Pol(0, 0, rc - w * 0.45f, a0 + ps * 0.95f);
                    g.DrawLines(Pn(V(Lt(plate, 0.6f)), Math.Max(1f, r * 0.05f)), new[] { s0, s1, s2, s3 });
                }
            }
            if (!hot) return;
            for (int i = 1; i < 5; i++)
            {
                if ((i == 1 || i == 2) && dmg >= 2 || (i == 3 || i == 4) && dmg >= 3) continue;   // no seam next to a lost plate
                float deg = start + ps * i;
                var a = Pol(0, 0, rc - w * 0.3f, deg); var b = Pol(0, 0, rc + w * 0.3f, deg);
                L(g, V(hotEdge), Math.Max(2.4f, r * 0.11f), a.X, a.Y, b.X, b.Y);
                L(g, V(glow), Math.Max(1f, r * 0.05f), a.X, a.Y, b.X, b.Y);
            }
        }

        static void Crack(Graphics g, float r, float cx, float cy, float deg, float len)
        {
            var p0 = Pol(cx, cy, r * 0.15f, deg); var p1 = Pol(cx, cy, r * 0.45f * len, deg + 18);
            var p2 = Pol(cx, cy, r * 0.65f * len, deg - 8); var p3 = Pol(cx, cy, r * 0.9f * len, deg + 10);
            g.DrawLines(Pn(V(Ink), Math.Max(1.5f, r * 0.06f)), new[] { p0, p1, p2, p3 });
            var b = Pol(cx, cy, r * 0.5f * len, deg + 40);
            g.DrawLine(Pn(V(Ink), Math.Max(0.8f, r * 0.04f)), p1, b);
        }

        static void BossDamage(Graphics g, float r, float lx, float lr, Color body, int dmg, int frame)
        {
            if (dmg >= 1) { Crack(g, r, lx, 0, 200, 0.8f); E(g, V(Dk(body, 0.3f)), lx + lr * 0.3f, -lr * 0.55f, r * 0.12f, r * 0.08f); }
            if (dmg >= 2) { Crack(g, r, lx, 0, 330, 0.9f); Crack(g, r, lx, 0, 110, 0.7f); VD(g, Ink, lx - lr * 0.2f, lr * 0.6f, r * 0.1f); }
            if (dmg >= 3)
            {
                Crack(g, r, lx, 0, 40, 0.95f);
                VD(g, Kit.A(C(0x495057), 170), -r * 0.7f - frame * r * 0.05f, -r * 0.2f, r * (0.22f + frame * 0.03f));
                VD(g, Kit.A(C(0x868E96), 140), -r * 0.95f - frame * r * 0.05f, r * 0.1f, r * (0.18f + frame * 0.03f));
                Star(g, V(C(0xFFE066)), lx + lr * 0.4f, lr * 0.3f, r * (frame % 2 == 0 ? 0.16f : 0.1f));
            }
        }

        // =====================================================================
        // SHOTS, EFFECTS, RANGE
        // =====================================================================

        static readonly Color YarnPink = C(0xE64980);

        public static void Shot(Graphics g, ProjKind k, float x, float y, float angle, double t)
        {
            int ab = AngleBucket(angle, 16);
            int frame = (int)(((long)Math.Floor(t * 12) % 8 + 8) % 8);
            if (k == ProjKind.Hairball) frame = 0;
            int sq = Dev(g, x, y);
            long key = Key(4, sq, ((long)k << 40) + (frame << 8) + ab);
            var s = Find(key);
            if (s == null)
            {
                using (var sg = Begin(key, 16, sq, out s))
                {
                    float a = ab * 360f / 16;
                    if (k == ProjKind.Hairball)
                    {
                        sg.RotateTransform(a);
                        L(sg, Kit.A(White, 110), 4, -12, 0, -4, 0);
                        D(sg, Kit.A(Color.Black, 40), 1, 2.5f, 5.5f);
                        for (int i = 0; i < 8; i++) { var p = Pol(0, 0, 5.4f, i * 45); D(sg, Ink, p.X, p.Y, 1.6f); }
                        OE(sg, C(0x9AA1A9), 0, 0, 4.8f, 4.8f);
                        for (int i = 0; i < 8; i++) { var p = Pol(0, 0, 5.2f, i * 45 + 10); D(sg, C(0xADB5BD), p.X, p.Y, 1.1f); }
                        D(sg, C(0xDEE2E6), -1.3f, -1.3f, 1.6f);
                    }
                    else
                    {
                        float br = k == ProjKind.Yarn ? 6.5f : 3.8f;
                        var st = sg.Save();
                        sg.RotateTransform(a);
                        L(sg, Kit.A(White, 90), br * 0.7f, -br * 2.2f, 0, -br * 0.6f, 0);
                        sg.Restore(st);
                        D(sg, Kit.A(Color.Black, 40), 1.5f, 3, br);
                        sg.RotateTransform(frame * 45);
                        YarnBall(sg, 0, 0, br, k == ProjKind.Yarn ? YarnPink : C(0xF783AC));
                    }
                }
            }
            Blit(g, s);
        }

        static float Wq(float w) { return Math.Max(0.5f, (float)Math.Round(w * 2) / 2); }
        static float Ease(float a) { return 1 - (1 - a) * (1 - a); }
        const int Heat = 0xFF6B00;                  // Blast colour of the laser overheat

        public static void Effect(Graphics g, FxKind k, float x, float y, float x2, float y2, float r, float angle, float age, int color, int tier)
        {
            age = Math.Max(0, Math.Min(1, age));
            tier = Math.Max(0, Math.Min(Defs.TierCount, tier));
            // Core gives a swipe the cat as X/Y, the target as X2/Y2 and the cat's range as R: claw marks go on the target, own size
            if (k == FxKind.Swipe) { if (x2 != 0 || y2 != 0) { x = x2; y = y2; } r = 0; }
            if (k == FxKind.Zzz && r > 0) SnoreRing(g, x, y, r, age, tier);
            int rq = Math.Max(0, Math.Min(1023, (int)Math.Round(r)));
            int half = FxHalf(k, rq, tier);
            // coins and Zzz rise above their spot: their sprites are centred on what they draw, not on x, y
            float ox = k == FxKind.Zzz ? 35 : 0, oy = k == FxKind.Coins ? -40 : k == FxKind.Zzz ? -52 : 0;
            int sq = half > 0 ? Dev(g, x + ox, y + oy) : 32;
            if (half <= 0 || half * sq > 112 * 32)
            {
                if (age < 0.94f || k == FxKind.Web) PaintFx(g, k, x, y, x2, y2, r, angle, age, color, tier);   // the last frames are all but invisible
                return;
            }
            // small effects: one cached sprite per 1/16 of their life; only the parameters each look uses enter the key
            int kc = 0, kr = 0;
            switch (k)
            {
                case FxKind.Pop: kc = color & 0xFFFFFF; kr = rq; tier = tier >= 2 ? 2 : 0; break;
                case FxKind.Blast: kc = color & 0xFFFFFF; kr = rq; break;
                case FxKind.Bite: kr = rq; tier = Math.Max(2, tier); break;
                case FxKind.Burn: kr = rq; break;
                case FxKind.Upgrade: kc = color & 0xFFFFFF; break;
                case FxKind.Web: kr = rq; kc = AngleBucket(angle, 8) % 4; tier = Math.Max(3, tier); break;
                case FxKind.Zzz: kr = r > 0 ? 1 : 0; tier = kr == 1 && tier >= 4 ? 4 : 0; break;
                default: tier = Math.Min(tier, 5); break;
            }
            int aq = Math.Min(15, (int)(age * 16));
            if (k == FxKind.Web && aq >= 2 && aq <= 12) aq = 8;   // a lying web looks the same for most of its life
            if (k == FxKind.Blast) aq &= ~1;                      // big and short: 8 frames are enough
            long key = Key(5, sq, ((long)k << 44) + ((long)kc << 20) + ((long)kr << 10) + (aq << 4) + tier);
            var s = Find(key);
            if (s == null)
                using (var fg = Begin(key, half, sq, out s))
                    PaintFx(fg, k, -ox, -oy, 0, 0, kr, k == FxKind.Web ? kc * (float)Math.PI / 4 : 0, (aq + (k == FxKind.Blast ? 1 : 0.5f)) / 16, k == FxKind.Web ? 0 : kc, tier);
            Blit(g, s);
        }

        // sprite half-size for a cached effect; 0 = always vectors (beams, swipes, traces, big areas)
        static int FxHalf(FxKind k, int r, int tier)
        {
            switch (k)
            {
                case FxKind.Pop: return (int)((r > 0 ? r : 16) * (tier >= 2 ? 2.1f : 1.75f) + 8);
                case FxKind.Blast: return (int)((r > 0 ? r : 50) * 1.2f + 10);
                case FxKind.Coins: return 46;
                case FxKind.Leak: return 64;
                case FxKind.Zzz: return 48;
                case FxKind.Bite: return (int)((r > 0 ? r : 16) * 1.4f + 14);
                case FxKind.Burn: return (int)((r > 0 ? r : 16) * 1.2f + 16);
                case FxKind.Upgrade: return tier >= 5 ? 72 : 46;
                case FxKind.Web: return (int)((r > 0 ? r : 40) * 1.06f + 8);
                default: return 0;
            }
        }

        // a snoring Spac: soft sound ring out to its range; tier 5 shakes the floor with a jagged quake ring
        static void SnoreRing(Graphics g, float x, float y, float r, float age, int tier)
        {
            if (age >= 0.7f) return;   // big rings are costly; this one has faded by then
            float f = (0.7f - age) / 0.7f, rad = r * (0.25f + 0.75f * Ease(age / 0.7f));
            if (tier >= 5)
            {
                var pts = new PointF[24];
                for (int i = 0; i < 24; i++) pts[i] = Pol(x, y, rad * (i % 2 == 0 ? 1 : 0.9f), i * 15 + age * 40);
                g.DrawPolygon(Pn(Al(C(0x7048E8), 0.65f * f), 3), pts);
            }
            else g.DrawEllipse(Pn(Al(C(0xB197FC), 0.55f * f), Wq(2 + tier * 0.4f)), x - rad, y - rad, rad * 2, rad * 2);
        }

        static void PaintFx(Graphics g, FxKind k, float x, float y, float x2, float y2, float r, float angle, float age, int color, int tier)
        {
            float f = 1 - age, e = Ease(age);
            Color c = C(color);
            switch (k)
            {
                case FxKind.Pop:
                    {
                        float rr = (r > 0 ? r : 16) * (tier >= 2 ? 1.2f : 1);
                        if (age < 0.3f) D(g, Al(White, (0.3f - age) / 0.3f * 0.85f), x, y, rr * (0.55f + age * 1.6f));
                        float rad = rr * (0.6f + 0.9f * e);
                        g.DrawEllipse(Pn(Al(c, f * 0.95f), Wq(3 * f + 0.6f)), x - rad, y - rad, rad * 2, rad * 2);
                        float off = (color & 63) * 5.7f;
                        int n = tier >= 2 ? 10 : 6;
                        for (int i = 0; i < n; i++)
                        {
                            var p = Pol(x, y, rr * (0.45f + 1.25f * e), off + i * 360f / n);
                            D(g, Al(i % 2 == 0 ? c : Kit.Ink, f), p.X, p.Y, (i % 2 == 0 ? 3.2f : 1.8f) * f + 0.6f);
                        }
                        break;
                    }
                case FxKind.Swipe:
                    {
                        float len = (r > 0 ? r : 24) * (1 + tier * 0.12f), grow = Math.Min(1, 0.35f + age / 0.3f);
                        float ca = (float)Math.Cos(angle), sa = (float)Math.Sin(angle);
                        int n = tier >= 5 ? 5 : tier >= 3 ? 4 : 3;
                        Color mark = tier >= 5 ? C(0xFFF3BF) : White;
                        for (int s = 0; s < n; s++)
                        {
                            float side = (s - (n - 1) / 2f) * 6;
                            var pts = new PointF[14];
                            for (int j = 0; j < 7; j++)
                            {
                                float tt = j / 6f * grow, along = (tt - 0.5f) * len, bulge = (float)Math.Sin(tt * Math.PI) * 4;
                                float wdt = (float)Math.Sin(tt * Math.PI) * (2.2f + tier * 0.4f) * (0.4f + 0.6f * f);
                                float u = side + bulge;
                                pts[j] = P(x + ca * (u + wdt) - sa * along, y + sa * (u + wdt) + ca * along);
                                pts[13 - j] = P(x + ca * (u - wdt) - sa * along, y + sa * (u - wdt) + ca * along);
                            }
                            g.FillPolygon(Kit.B(Al(tier >= 5 ? C(0xC92A2A) : Kit.Ink, f * 0.55f)), Offset(pts, 1, 1.5f));
                            g.FillPolygon(Kit.B(Al(mark, f)), pts);
                        }
                        break;
                    }
                case FxKind.Blast:
                    {
                        float rr = r > 0 ? r : 50;
                        if (color == Heat)
                        {
                            if (age < 0.3f) D(g, Al(C(0xFFF9DB), (0.3f - age) * 3), x, y, rr * 0.7f);
                            if (f > 0.3f) D(g, Al(C(0xFF6B00), 0.42f * f), x, y, rr * (0.45f + 0.6f * e));
                            if (f > 0.4f) D(g, Al(C(0xFFD43B), 0.5f * f), x, y, rr * (0.2f + 0.35f * e));
                            float rad = rr * (0.55f + 0.5f * e);
                            g.DrawEllipse(Pn(Al(C(0xFFE066), f), Wq(4 * f + 1)), x - rad, y - rad, rad * 2, rad * 2);
                            int n = 7 + tier;
                            for (int i = 0; i < n; i++)
                            {
                                float dg = i * 360f / n + 7;
                                var p = Pol(x, y, rr * (0.35f + 0.6f * e) * (i % 2 == 0 ? 1 : 0.8f), dg);
                                Flame(g, p.X, p.Y, (2.6f + tier * 0.5f) * (0.4f + 0.6f * f), dg, false, f);
                            }
                            break;
                        }
                        if (color == 0) c = YarnPink;
                        if (age < 0.25f) D(g, Al(Kit.Mix(c, C(0xFFF3BF), (0.25f - age) * 4), 0.32f + (0.25f - age) * 2.4f), x, y, rr * (0.4f + 0.6f * e));
                        else if (f > 0.3f) D(g, Al(c, 0.32f * f), x, y, rr * (0.4f + 0.6f * e));
                        float rad2 = rr * (0.5f + 0.55f * e);
                        g.DrawEllipse(Pn(Al(White, f * 0.9f), Wq(3.5f * f + 1)), x - rad2, y - rad2, rad2 * 2, rad2 * 2);
                        int m = 10 + tier * 2;
                        for (int i = 0; i < m; i++)
                        {
                            var p = Pol(x, y, rr * (0.25f + 0.85f * e) * (i % 2 == 0 ? 1 : 0.75f), i * 360f / m + 11);
                            D(g, Al(c, f), p.X, p.Y, 2.6f * f + 1);
                            if (rr <= 60) D(g, Al(Lt(c, 0.5f), f), p.X - 0.7f, p.Y - 0.7f, 1.1f * f + 0.4f);   // big blasts are drawn as vectors every frame: skip the glints
                        }
                        if (tier >= 5)
                            for (int i = 0; i < 4; i++)
                            {
                                float rr2 = rr * (0.35f + 0.5f * e);
                                g.DrawArc(Pn(Al(i % 2 == 0 ? c : C(0xFFD43B), f), Wq(2.5f * f + 0.5f)), x - rr2, y - rr2, rr2 * 2, rr2 * 2, i * 90 + age * 200, 55);
                            }
                        break;
                    }
                case FxKind.Beam:
                    {
                        if (color == 0) c = C(0xFF3B3B);
                        float fa = 1 - age * 0.7f;
                        if (tier >= 5)
                        {
                            L(g, Al(Gold, 0.3f * fa), 30, x, y, x2, y2);
                            L(g, Al(C(0xFFE066), 0.7f * fa), 15, x, y, x2, y2);
                            L(g, Al(C(0xFFF9DB), fa), 6, x, y, x2, y2);
                            D(g, Al(Gold, 0.5f * fa), x2, y2, 14 + 3 * f);
                            Star(g, Al(White, fa), x2, y2, 8);
                            break;
                        }
                        float w = 2.4f + tier * 1.2f;
                        L(g, Al(c, 0.3f * fa), Wq(w * 2.2f), x, y, x2, y2);
                        L(g, Al(c, 0.9f * fa), Wq(w), x, y, x2, y2);
                        L(g, Al(White, 0.9f * fa), Wq(Math.Max(1, w * 0.38f)), x, y, x2, y2);
                        D(g, Al(c, 0.45f * fa), x2, y2, 4 + tier * 1.6f + 2 * f);
                        D(g, Al(White, fa), x2, y2, 1.8f + tier * 0.6f);
                        break;
                    }
                case FxKind.Trace:
                    {
                        Color tc = tier >= 5 ? Gold : C(0xFF8787);
                        L(g, Al(tc, 0.4f * f), Wq(8 * f + 1), x, y, x2, y2);
                        L(g, Al(White, f), Wq(2.4f * f + 0.5f), x, y, x2, y2);
                        D(g, Al(C(0xFFE066), f), x, y, 4 * f + 1.5f);
                        D(g, Al(tc, 0.55f * f), x2, y2, 7 + tier);
                        Star(g, Al(White, f), x2, y2, 6 * f + 2);
                        break;
                    }
                case FxKind.Freeze:
                    {
                        float rr = r > 0 ? r : 100;
                        // a big translucent disc is the costliest thing GDI+ draws here: only while the frost burst is fresh
                        if (age < 0.25f) D(g, Al(C(0xC5F6FA), (tier >= 4 ? 0.4f : 0.32f) * (0.25f - age) / 0.25f), x, y, rr * (0.3f + 0.7f * e));
                        float rad = rr * (0.35f + 0.65f * e);
                        g.DrawEllipse(Pn(Al(C(0x74C0FC), 0.9f * f), tier >= 4 ? 4 : 3), x - rad, y - rad, rad * 2, rad * 2);
                        int n = tier >= 5 ? 9 : 6;
                        for (int i = 0; i < n; i++)
                        {
                            var p = Pol(x, y, rr * (0.25f + 0.55f * e), i * 360f / n + 30);
                            Star(g, Al(White, f), p.X, p.Y, 5.5f);
                            Star(g, Al(C(0xA5D8FF), f), p.X, p.Y, 2.4f);
                        }
                        if (tier >= 5)
                            for (int i = 0; i < 12; i++)
                            {
                                float dg = i * 30 + 15;
                                Poly(g, Al(i % 2 == 0 ? White : C(0xA5D8FF), 0.85f * f), Pol(x, y, rad - 6, dg - 3), Pol(x, y, rad + 7, dg), Pol(x, y, rad - 6, dg + 3));
                            }
                        break;
                    }
                case FxKind.Coins:
                    {
                        float sc = age < 0.75f ? 1 : (1 - age) / 0.25f;
                        int n = Math.Min(8, 3 + tier);
                        for (int i = 0; i < n; i++)
                        {
                            float px = x + (i - (n - 1) / 2f) * 8 + (float)Math.Sin(i * 1.7 + age * 6) * 3;
                            float py = y - 8 - age * (40 + i % 2 * 12) - (i % 3) * 5;
                            if (sc > 0.05f) TreatIcon(g, px, py, 15 * sc);
                        }
                        Star(g, Al(C(0xFFE066), f), x - 16, y - 20 - age * 30, 5 * f + 1);
                        Star(g, Al(C(0xFFE066), f), x + 18, y - 30 - age * 26, 4 * f + 1);
                        break;
                    }
                case FxKind.Leak:
                    {
                        float rad = 12 + 30 * e;
                        g.DrawEllipse(Pn(Al(C(0xFF6B6B), f), Wq(4 * f + 1)), x - rad, y - rad, rad * 2, rad * 2);
                        float sc = age < 0.7f ? 1 : (1 - age) / 0.3f;
                        if (sc > 0.05f)
                        {
                            float hy = y - 10 - age * 34;
                            LifeIcon(g, x, hy, 22 * sc);
                            g.DrawLines(Pn(Kit.Ink, 1.6f * sc), new[] { P(x, hy - 7 * sc), P(x - 2.5f * sc, hy - 2 * sc), P(x + 2 * sc, hy + 2 * sc), P(x - 1 * sc, hy + 8 * sc) });
                        }
                        break;
                    }
                case FxKind.Zzz:
                    {
                        // r > 0: a real snore (bigger letters, violet from tier 4); r = 0: the small idle Zzz
                        float big = r > 0 ? 1.45f : 1;
                        Color zc = r > 0 && tier >= 4 ? C(0xD0BFFF) : C(0xE7F5FF);
                        for (int i = 0; i < 3; i++)
                        {
                            float a = age * 1.2f + 0.08f - i * 0.2f;
                            if (a <= 0 || a >= 1) continue;
                            float s = (3 + a * 4 + i * 1.5f) * big, px = x + (8 + a * 16 + i * 5) * big, py = y - (12 + a * 28 + i * 7) * big;
                            var pts = new[] { P(px - s, py - s), P(px + s, py - s), P(px - s, py + s), P(px + s, py + s) };
                            g.DrawLines(Pn(Al(Kit.Ink, (1 - a) * 0.9f), Wq(s * 0.5f + 2.5f)), pts);
                            g.DrawLines(Pn(Al(zc, 1 - a), Wq(s * 0.45f)), pts);
                        }
                        break;
                    }
                case FxKind.Spin:
                    {
                        float rr = r > 0 ? r : 95;
                        if (tier >= 5)
                        {
                            Color[] sc = { C(0x5C7CFA), White, C(0x748FA5), C(0xA5D8FF) };
                            for (int i = 0; i < 5; i++)
                            {
                                float rad = rr * (0.32f + 0.16f * i) * (0.85f + 0.15f * e);
                                g.DrawArc(Pn(Al(sc[i % 4], 0.85f * f), Wq(5.5f - i * 0.6f)), x - rad, y - rad, rad * 2, rad * 2, age * 800 + i * 77, 95);
                            }
                            for (int i = 0; i < 8; i++)
                            {
                                var p = Pol(x, y, rr * (0.35f + 0.08f * i), age * 900 + i * 51);
                                D(g, Al(i % 2 == 0 ? C(0x8CE99A) : C(0xCED4DA), f), p.X, p.Y, 2.4f);
                            }
                            break;
                        }
                        for (int i = 0; i < 4; i++)
                        {
                            float rad = rr * (0.45f + 0.15f * i) * (0.85f + 0.15f * e);
                            g.DrawArc(Pn(Al(i % 2 == 0 ? White : C(0xA5D8FF), 0.75f * f), Wq(4 - i * 0.6f + (tier >= 4 ? 1 : 0))), x - rad, y - rad, rad * 2, rad * 2, age * 600 + i * 95, 70);
                        }
                        break;
                    }
                case FxKind.Bite:
                    {
                        float rr = r > 0 ? r : 16, R = rr * 0.85f + 5;
                        float close = Math.Min(1, age / 0.28f), gap = (1 - close) * R * 0.6f, fa = age < 0.6f ? 1 : (1 - age) / 0.4f;
                        Color jaw = tier >= 5 ? C(0xC92A2A) : tier == 4 ? C(0x748FA5) : tier == 3 ? C(0x66A80F) : C(0xF59FB0);
                        Color tooth = tier >= 5 ? Gold : White;
                        int n = tier >= 4 ? 6 : 5;
                        for (int s = -1; s <= 1; s += 2)
                        {
                            float cy = y + s * gap, a0 = s < 0 ? 205 : 25;
                            var band = new PointF[18];
                            for (int i = 0; i < 9; i++)
                            {
                                band[i] = Pol(x, cy, R, a0 + i * 130f / 8);
                                band[17 - i] = Pol(x, cy, R * 0.74f, a0 + i * 130f / 8);
                            }
                            g.DrawPolygon(Pn(Al(Ink, fa), O * 2), band);
                            g.FillPolygon(Kit.B(Al(jaw, fa)), band);
                            for (int i = 0; i < n; i++)
                            {
                                float dg = a0 + 10 + i * 110f / (n - 1);
                                var tp = new[] { Pol(x, cy, R * 0.76f, dg - 5), Pol(x, cy, R * 0.4f, dg), Pol(x, cy, R * 0.76f, dg + 5) };
                                g.DrawPolygon(Pn(Al(Ink, fa), O * 1.4f), tp);
                                g.FillPolygon(Kit.B(Al(tooth, fa)), tp);
                            }
                        }
                        if (age > 0.22f && age < 0.6f)
                        {
                            float k2 = (age - 0.22f) / 0.38f;
                            for (int i = 0; i < 4; i++) Star(g, Al(i % 2 == 0 ? White : Gold, 1 - k2), x + (i < 2 ? -1 : 1) * R * (1 + k2 * 0.3f), y + (i % 2 == 0 ? -4 : 4), 4 * (1 - k2) + 1.5f);
                        }
                        break;
                    }
                case FxKind.Burn:
                    {
                        float rr = r > 0 ? r : 16;
                        int n = tier >= 5 ? 5 : tier >= 4 ? 4 : 3;
                        float fl = age < 0.15f ? age / 0.15f : age > 0.7f ? (1 - age) / 0.3f : 1;
                        for (int i = 0; i < n; i++)
                        {
                            var p = Pol(x, y, rr * 0.6f, 200 + i * 140f / (n - 1));
                            float s = (3.8f + tier * 0.55f) * (0.85f + 0.3f * (float)Math.Sin(age * 25 + i * 2)) * (0.3f + 0.7f * fl);
                            Flame(g, p.X, p.Y + 2, s, -90 + (i - (n - 1) / 2f) * 12, true, Math.Max(0.15f, fl));
                        }
                        for (int i = 0; i < 3; i++) D(g, Al(i % 2 == 0 ? C(0xFFD43B) : C(0xFF922B), f), x + (i - 1) * rr * 0.5f, y - rr * 0.6f - age * 18 - i * 3, 1.3f);
                        break;
                    }
                case FxKind.Web:
                    {
                        float rr = r > 0 ? r : 40;
                        float ap = Math.Min(1, age / 0.1f), fade = age > 0.8f ? (1 - age) / 0.2f : 1, A = Math.Max(0, ap * fade);
                        float along = rr * (0.7f + 0.3f * ap), across = Math.Min(rr, Defs.PathWidth * 0.52f) * (0.7f + 0.3f * ap);
                        bool frost = tier >= 5;
                        float w = tier >= 4 ? 2.4f : 1.5f;
                        var st = g.Save();
                        g.TranslateTransform(x, y);
                        g.RotateTransform(angle * Deg);
                        E(g, Al(frost ? C(0xA5D8FF) : C(0xFFE8CC), (frost ? 0.32f : 0.14f) * A), 0, 0, along, across);
                        g.TranslateTransform(0.8f, 1.4f);
                        WebShape(g, 0, 0, along, across, Al(Ink, 0.4f * A), w + 0.6f, 3);
                        g.TranslateTransform(-0.8f, -1.4f);
                        WebShape(g, 0, 0, along, across, Al(frost ? C(0xE7F5FF) : C(0xFFF4E0), 0.95f * A), w, 3);
                        if (tier >= 4 && !frost) D(g, Al(C(0xE64980), A), 0, 0, 2.6f);
                        if (frost)
                        {
                            for (int i = 0; i < 8; i++) { var p = Pol(0, 0, 1, i * 45 + 22.5f); D(g, Al(White, A), p.X * along * 0.6f, p.Y * across * 0.6f, 1.5f); }
                            var sp = Pn(Al(White, A), 1.4f);
                            for (int i = 0; i < 3; i++) { var q = Pol(0, 0, 6, i * 60); g.DrawLine(sp, -q.X, -q.Y, q.X, q.Y); }
                        }
                        g.Restore(st);
                        break;
                    }
                case FxKind.Upgrade:
                    {
                        float rr = r > 0 ? r : 22;
                        if (color == 0) c = Gold;
                        if (tier >= 5)
                        {
                            if (age < 0.25f) D(g, Al(C(0xFFF9DB), (0.25f - age) * 3.4f), x, y, rr * 1.4f);
                            for (int i = 0; i < 12; i++)
                            {
                                float dg = i * 30 + age * 40;
                                Poly(g, Al(i % 2 == 0 ? Gold : C(0xFFF3BF), 0.75f * f), Pol(x, y, rr * 0.5f, dg - 6), Pol(x, y, rr * (1.3f + 1.7f * e) * (i % 2 == 0 ? 1 : 0.75f), dg), Pol(x, y, rr * 0.5f, dg + 6));
                            }
                            float rad = rr * (0.6f + 1.9f * e);
                            g.DrawEllipse(Pn(Al(GoldDk, f), Wq(6 * f + 1)), x - rad, y - rad, rad * 2, rad * 2);
                            g.DrawEllipse(Pn(Al(Gold, f), Wq(3 * f + 0.5f)), x - rad, y - rad, rad * 2, rad * 2);
                            for (int i = 0; i < 10; i++)
                            {
                                var p = Pol(x, y, rr * (0.5f + 2.1f * e) * (i % 2 == 0 ? 1 : 0.8f), i * 36 + 18);
                                Star(g, Al(i % 2 == 0 ? White : Gold, f), p.X, p.Y, 6 * f + 1.5f);
                            }
                            break;
                        }
                        float rad1 = rr * (0.5f + 1.1f * e);
                        g.DrawEllipse(Pn(Al(Lt(c, 0.3f), f), Wq(3.5f * f + 0.6f)), x - rad1, y - rad1, rad1 * 2, rad1 * 2);
                        int n = 5 + tier;
                        for (int i = 0; i < n; i++)
                        {
                            var p = Pol(x, y, rr * (0.4f + 1.25f * e), i * 360f / n + 10);
                            Star(g, Al(i % 2 == 0 ? White : Lt(c, 0.5f), f), p.X, p.Y, (3 + tier * 0.4f) * f + 1);
                        }
                        for (int i = 0; i < Math.Max(1, Math.Min(tier, 4)); i++)
                        {
                            float cy = y - 6 - e * 16 - i * 5, cx = x;
                            var pts = new[] { P(cx - 6, cy + 4), P(cx, cy - 2), P(cx + 6, cy + 4) };
                            g.DrawLines(Pn(Al(Ink, 0.8f * f), 5.5f), pts);
                            g.DrawLines(Pn(Al(Gold, f), 3), pts);
                        }
                        break;
                    }
            }
        }

        static PointF[] Offset(PointF[] p, float dx, float dy)
        {
            var q = new PointF[p.Length];
            for (int i = 0; i < p.Length; i++) q[i] = P(p[i].X + dx, p[i].Y + dy);
            return q;
        }

        public static void Range(Graphics g, float x, float y, float r, bool ok)
        {
            Color c = ok ? White : C(0xFF4D4D);
            D(g, Kit.A(c, ok ? 42 : 60), x, y, r);
            g.DrawEllipse(Pn(Kit.A(Kit.Ink, 70), 3.5f), x - r, y - r, r * 2, r * 2);
            g.DrawEllipse(Pn(Kit.A(c, ok ? 200 : 230), 2), x - r, y - r, r * 2, r * 2);
        }

        static Pen ghostPen;

        public static void RangeGhost(Graphics g, float x, float y, float r)
        {
            Color c = C(0x8CE99A);
            D(g, Kit.A(c, 30), x, y, r);
            g.DrawEllipse(Pn(Kit.A(Kit.Ink, 80), 4), x - r, y - r, r * 2, r * 2);
            if (ghostPen == null) { ghostPen = new Pen(Kit.A(c, 240), 2.4f); ghostPen.DashPattern = new float[] { 3, 2 }; }
            g.DrawEllipse(ghostPen, x - r, y - r, r * 2, r * 2);
        }

        // =====================================================================
        // ICONS, TITLE ART, APP ICON
        // =====================================================================

        public static void TreatIcon(Graphics g, float x, float y, float size)
        {
            float s = size, o = Math.Max(1f, s * 0.08f);
            Color c = C(0xF08C3A), lt = C(0xFFC078);
            var tail = new[] { P(x + s * 0.16f, y), P(x + s * 0.5f, y - s * 0.26f), P(x + s * 0.44f, y), P(x + s * 0.5f, y + s * 0.26f) };
            g.DrawPolygon(Pn(Kit.Ink, o * 2), tail);
            E(g, Kit.Ink, x - s * 0.1f, y, s * 0.36f + o, s * 0.25f + o);
            g.FillPolygon(Kit.B(c), tail);
            E(g, c, x - s * 0.1f, y, s * 0.36f, s * 0.25f);
            E(g, lt, x - s * 0.14f, y + s * 0.07f, s * 0.24f, s * 0.09f);
            g.DrawArc(Pn(Dk(c, 0.3f), Math.Max(0.8f, s * 0.05f)), x - s * 0.06f, y - s * 0.17f, s * 0.2f, s * 0.34f, -70, 140);
            D(g, Kit.Ink, x - s * 0.3f, y - s * 0.05f, Math.Max(0.8f, s * 0.055f));
        }

        /// <summary>One-colour stat symbol for the upgrade chips, centred on (cx, cy), about size px across.</summary>
        public static void StatIcon(Graphics g, StatKind k, float cx, float cy, float size, Color c)
        {
            float s = size / 2;
            var pen = Pn(c, Math.Max(1.2f, size * 0.15f));
            switch (k)
            {
                case StatKind.Range:     // the cat in the middle of its reach
                    g.DrawEllipse(pen, cx - s * 0.84f, cy - s * 0.84f, s * 1.68f, s * 1.68f);
                    D(g, c, cx, cy, s * 0.34f);
                    break;
                case StatKind.Speed:     // two rounded chevrons
                    for (int i = 0; i < 2; i++)
                    {
                        float x = cx - s * 0.42f + i * s * 0.62f;
                        g.DrawLines(pen, new[] { P(x - s * 0.3f, cy - s * 0.66f), P(x + s * 0.3f, cy), P(x - s * 0.3f, cy + s * 0.66f) });
                    }
                    break;
                case StatKind.Damage:    // three claw scratches
                    for (int i = -1; i <= 1; i++)
                    {
                        float o = i * s * 0.55f;
                        g.DrawBezier(pen, P(cx + o + s * 0.4f, cy - s * 0.82f), P(cx + o + s * 0.12f, cy - s * 0.3f),
                            P(cx + o + s * 0.02f, cy + s * 0.2f), P(cx + o - s * 0.32f, cy + s * 0.82f));
                    }
                    break;
                case StatKind.Pierce:    // an arrow through two vacuums
                    using (var thin = Kit.P(Kit.A(c, c.A * 150 / 255), Math.Max(1f, size * 0.11f)))
                    {
                        g.DrawLine(thin, cx - s * 0.5f, cy - s * 0.78f, cx - s * 0.5f, cy + s * 0.78f);
                        g.DrawLine(thin, cx - s * 0.02f, cy - s * 0.78f, cx - s * 0.02f, cy + s * 0.78f);
                    }
                    g.DrawLine(pen, cx - s * 0.92f, cy, cx + s * 0.78f, cy);
                    g.DrawLines(pen, new[] { P(cx + s * 0.32f, cy - s * 0.46f), P(cx + s * 0.82f, cy), P(cx + s * 0.32f, cy + s * 0.46f) });
                    break;
                case StatKind.Blast:     // a soft eight-point burst
                {
                    var pts = new PointF[16];
                    for (int i = 0; i < 16; i++)
                    {
                        double a = i * Math.PI / 8 - Math.PI / 2;
                        float r = i % 2 == 0 ? s * 0.92f : s * 0.48f;
                        pts[i] = P(cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r);
                    }
                    g.FillPolygon(Kit.B(c), pts);
                    g.DrawPolygon(Pn(c, Math.Max(1f, size * 0.08f)), pts);
                    break;
                }
                case StatKind.Stealth:   // a cat's eye with a slit pupil
                    using (var p = new GraphicsPath())
                    {
                        p.AddBezier(P(cx - s * 0.95f, cy), P(cx - s * 0.42f, cy - s * 0.78f), P(cx + s * 0.42f, cy - s * 0.78f), P(cx + s * 0.95f, cy));
                        p.AddBezier(P(cx + s * 0.95f, cy), P(cx + s * 0.42f, cy + s * 0.78f), P(cx - s * 0.42f, cy + s * 0.78f), P(cx - s * 0.95f, cy));
                        g.DrawPath(pen, p);
                    }
                    E(g, c, cx, cy, s * 0.18f, s * 0.44f);
                    break;
                case StatKind.Armor:     // a shield with a crack
                    using (var p = new GraphicsPath())
                    {
                        p.AddLine(cx - s * 0.72f, cy - s * 0.66f, cx, cy - s * 0.88f);
                        p.AddLine(cx, cy - s * 0.88f, cx + s * 0.72f, cy - s * 0.66f);
                        p.AddBezier(P(cx + s * 0.72f, cy - s * 0.66f), P(cx + s * 0.72f, cy + s * 0.2f), P(cx + s * 0.35f, cy + s * 0.62f), P(cx, cy + s * 0.9f));
                        p.AddBezier(P(cx, cy + s * 0.9f), P(cx - s * 0.35f, cy + s * 0.62f), P(cx - s * 0.72f, cy + s * 0.2f), P(cx - s * 0.72f, cy - s * 0.66f));
                        p.CloseFigure();
                        g.DrawPath(pen, p);
                    }
                    g.DrawLines(Pn(c, Math.Max(1f, size * 0.1f)), new[] { P(cx + s * 0.12f, cy - s * 0.62f), P(cx - s * 0.16f, cy - s * 0.08f), P(cx + s * 0.14f, cy + s * 0.12f), P(cx - s * 0.04f, cy + s * 0.56f) });
                    break;
                case StatKind.Boss:      // a big robot vacuum seen from above, bumper in front
                    D(g, c, cx, cy + s * 0.14f, s * 0.7f);
                    g.DrawArc(pen, cx - s * 0.92f, cy - s * 0.78f, s * 1.84f, s * 1.84f, 205, 130);
                    D(g, Kit.A(White, c.A * 200 / 255), cx, cy + s * 0.14f, s * 0.2f);
                    break;
            }
        }

        public static void LifeIcon(Graphics g, float x, float y, float size)
        {
            float w = size * 0.5f, h = size * 0.5f;
            using (var p = new GraphicsPath())
            {
                p.AddBezier(x, y + h * 0.95f, x - w * 0.3f, y + h * 0.6f, x - w * 1.05f, y + h * 0.15f, x - w, y - h * 0.35f);
                p.AddBezier(x - w, y - h * 0.35f, x - w * 0.95f, y - h * 1.05f, x - w * 0.1f, y - h * 1.05f, x, y - h * 0.5f);
                p.AddBezier(x, y - h * 0.5f, x + w * 0.1f, y - h * 1.05f, x + w * 0.95f, y - h * 1.05f, x + w, y - h * 0.35f);
                p.AddBezier(x + w, y - h * 0.35f, x + w * 1.05f, y + h * 0.15f, x + w * 0.3f, y + h * 0.6f, x, y + h * 0.95f);
                p.CloseFigure();
                g.DrawPath(Pn(Kit.Ink, Math.Max(1.5f, size * 0.13f)), p);
                g.FillPath(Kit.B(C(0xF03E3E)), p);
            }
            E(g, Kit.A(White, 120), x - w * 0.5f, y - h * 0.55f, w * 0.22f, h * 0.13f);
            Paw(g, C(0xFFE3E3), x, y - size * 0.02f, size * 0.12f);
        }

        static Bitmap heroBack, heroFront;          // static layers of the title art, rebuilt only when its size changes

        public static void Hero(Graphics g, RectangleF r, double t)
        {
            float W = Math.Max(1, r.Width), H = Math.Max(1, r.Height);
            float sc = Dev(g, r.X, r.Y) / 32f;
            int px = (int)Math.Round(devX), py = (int)Math.Round(devY);
            bool mapped = devMapped;
            int bw = Math.Max(1, (int)Math.Round(W * sc)), bh = Math.Max(1, (int)Math.Round(H * sc));
            if (heroBack == null || heroBack.Width != bw || heroBack.Height != bh)
            {
                if (heroBack != null) { heroBack.Dispose(); heroFront.Dispose(); }
                heroBack = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb);
                heroFront = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb);
                using (var bg = Graphics.FromImage(heroBack)) { bg.SmoothingMode = SmoothingMode.AntiAlias; bg.ScaleTransform(sc, sc); HeroBack(bg, W, H); }
                using (var fg = Graphics.FromImage(heroFront)) { fg.SmoothingMode = SmoothingMode.AntiAlias; fg.ScaleTransform(sc, sc); HeroFront(fg, W, H); }
            }
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.IntersectClip(r);
            BlitAt(g, heroBack, px, py, mapped);
            var s1 = g.Save();
            g.TranslateTransform(r.X, r.Y);
            HeroActors(g, W, H, t);
            g.Restore(s1);
            BlitAt(g, heroFront, px, py, mapped);
            g.Restore(st);
        }

        static void HeroBack(Graphics g, float W, float H)
        {
            float k = H / 420f;
            var s2 = g.Save();
            g.ScaleTransform(1.6f * k, 1.6f * k);
            Floor(g, W / (1.6f * k), H / (1.6f * k), 4);
            g.Restore(s2);
            // runner across the scene
            using (var path = new GraphicsPath())
            {
                path.AddLine(-60, H * 0.66f, W + 60, H * 0.5f);
                float rw = 130 * k;
                var s3 = g.Save(); g.TranslateTransform(4 * k, 10 * k);
                StrokePath(g, path, Kit.A(Color.Black, 40), rw + 10 * k);
                g.Restore(s3);
                StrokePath(g, path, RugEdge, rw + 4 * k);
                StrokePath(g, path, RugCream, rw - 6 * k);
                StrokePath(g, path, RugRust, rw - 28 * k);
                StrokePath(g, path, RugCream, rw - 36 * k);
                StrokePath(g, path, RugField, rw - 46 * k);
            }
            // sun patch, a house plant in the corner, warm glow around the hero
            g.FillPolygon(Kit.B(Kit.A(C(0xFFF4D6), 30)), new[] { P(W * 0.18f, 0), P(W * 0.42f, 0), P(W * 0.62f, H), P(W * 0.38f, H) });
            var sp = g.Save(); g.TranslateTransform(W * 0.05f, H * 0.08f); g.ScaleTransform(2.3f * k, 2.3f * k);
            Plant(g, new RectangleF(-36, -36, 72, 72));
            g.Restore(sp);
            using (var p = new GraphicsPath())
            {
                p.AddEllipse(W * 0.3f - 190 * k, H * 0.62f - 150 * k, 380 * k, 300 * k);
                using (var pg = new PathGradientBrush(p))
                {
                    pg.CenterColor = Kit.A(C(0xFFE8A8), 90);
                    pg.SurroundColors = new[] { Kit.A(C(0xFFE8A8), 0) };
                    g.FillPath(pg, p);
                }
            }
        }

        static void HeroActors(Graphics g, float W, float H, double t)
        {
            float k = H / 420f;
            // a little vacuum queue in the back
            for (int i = 0; i < 3; i++)
            {
                float vx = W * 0.95f + i * 70 * k, vy = H * 0.66f + (H * 0.5f - H * 0.66f) * (vx + 60) / (W + 120);
                var s4 = g.Save(); g.TranslateTransform(vx, vy); g.ScaleTransform(2.2f * k, 2.2f * k);
                PaintVac(g, i == 0 ? VacKind.Turbo : i == 1 ? VacKind.Plus : VacKind.Robik, (float)Math.PI, false, 0, false, 0, 0, (float)(t * 500), (int)(t * 8) % 4);
                g.Restore(s4);
            }
            // the big vacuum
            float bxv = W * 0.7f, byv = H * 0.555f + (float)Math.Sin(t * 2) * 2 * k;
            var s5 = g.Save(); g.TranslateTransform(bxv, byv); g.ScaleTransform(2.1f * k, 2.1f * k);
            PaintVac(g, VacKind.Prumyslak, (float)Math.PI + 0.16f, false, 0, false, 0, 0, (float)(t * 500), (int)(t * 8) % 4);
            g.Restore(s5);
            // the brave cat
            float atk = (float)Math.Pow(Math.Max(0, Math.Sin(t * 2.4)), 6);
            float cx = W * 0.3f, cy = H * 0.62f;
            var s6 = g.Save(); g.TranslateTransform(cx, cy); g.ScaleTransform(4.2f * k, 4.2f * k);
            PaintCat(g, TowerKind.Drapka, 2, 0, 0, -0.16f, (float)Math.Sin(t * 2), atk);
            g.Restore(s6);
            if (atk > 0.1f) Effect(g, FxKind.Swipe, W * 0.52f, H * 0.57f, 0, 0, 70 * k, -0.16f, 1 - atk, 0, 2);
            // dust motes in the light
            var rnd = new Random(9);
            for (int i = 0; i < 14; i++)
            {
                float mx = W * Rf(rnd, 0.2f, 0.6f) + (float)Math.Sin(t * 0.7 + i) * 8 * k, my = (float)((Rf(rnd, 0, 1) * H - t * 10 * k * (1 + i % 3)) % H);
                if (my < 0) my += H;
                D(g, Kit.A(C(0xFFF4D6), 120), mx, my, (1.2f + i % 3) * k);
            }
        }

        static void HeroFront(Graphics g, float W, float H)
        {
            float k = H / 420f;
            YarnBall(g, W * 0.1f, H * 0.86f, 18 * k, C(0x7048E8));
            TreatIcon(g, W * 0.18f, H * 0.93f, 26 * k);
            TreatIcon(g, W * 0.9f, H * 0.9f, 22 * k);
            using (var p = new GraphicsPath())
            {
                p.AddEllipse(-W * 0.25f, -H * 0.3f, W * 1.5f, H * 1.6f);
                using (var pg = new PathGradientBrush(p))
                {
                    pg.CenterColor = Kit.A(Color.Black, 0);
                    pg.SurroundColors = new[] { Kit.A(C(0x2A1808), 150) };
                    pg.FocusScales = new PointF(0.55f, 0.5f);
                    g.FillRectangle(pg, 0, 0, W, H);
                }
            }
        }

        public static void AppIcon(Graphics g, float size)
        {
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = size;
            Kit.FillRound(g, RugEdge, new RectangleF(0, 0, s, s), s * 0.22f);
            Kit.FillRound(g, RugField, new RectangleF(s * 0.045f, s * 0.045f, s * 0.91f, s * 0.91f), s * 0.18f);
            if (s >= 40) Kit.StrokeRound(g, RugCream, s * 0.025f, new RectangleF(s * 0.1f, s * 0.1f, s * 0.8f, s * 0.8f), s * 0.13f);
            g.TranslateTransform(s * 0.5f, s * 0.56f);
            float sc = s / 30f;
            g.ScaleTransform(sc, sc);
            FrontHead(g, C(0xF08C2E), C(0xFFE8CC), s >= 40);
            g.Restore(st);
        }

        static void FrontHead(Graphics g, Color fur, Color fur2, bool detail)
        {
            Color stripe = C(0xC0621B);
            for (int pass = 0; pass < 2; pass++)
            {
                bool ink = pass == 0;
                SPoly(g, ink, fur, P(-10.5f, -2), P(-9.5f, -14), P(-2.5f, -8.5f));
                SPoly(g, ink, fur, P(10.5f, -2), P(9.5f, -14), P(2.5f, -8.5f));
                SE(g, ink, fur, 0, 0, 11.5f, 9.8f);
            }
            Poly(g, Pink, P(-8.8f, -4.5f), P(-8.3f, -11.5f), P(-4.2f, -8.4f));
            Poly(g, Pink, P(8.8f, -4.5f), P(8.3f, -11.5f), P(4.2f, -8.4f));
            L(g, stripe, 1.5f, 0, -9.3f, 0, -6); L(g, stripe, 1.5f, -2.8f, -8.8f, -2.4f, -6.2f); L(g, stripe, 1.5f, 2.8f, -8.8f, 2.4f, -6.2f);
            E(g, fur2, -2.7f, 3.6f, 3.9f, 3);
            E(g, fur2, 2.7f, 3.6f, 3.9f, 3);
            for (int s = -1; s <= 1; s += 2)
            {
                D(g, Ink, s * 4.6f, -1.2f, 2.6f);
                D(g, C(0x94D82D), s * 4.6f, -1.2f, 2.0f);
                E(g, Ink, s * 4.6f, -1.2f, 0.8f, 1.7f);
                D(g, White, s * 4.6f - 0.8f, -2, 0.7f);
                E(g, Kit.A(Nose, 110), s * 7.4f, 3, 1.9f, 1.1f);
                if (detail)
                {
                    var wp = Pn(Kit.A(Ink, 190), 0.45f);
                    g.DrawLine(wp, s * 5.5f, 3.4f, s * 13, 2.2f); g.DrawLine(wp, s * 5.5f, 4.4f, s * 13, 5.4f);
                }
            }
            Poly(g, Nose, P(-1.6f, 1.6f), P(1.6f, 1.6f), P(0, 3.2f));
            var mp = Pn(Ink, 0.6f);
            g.DrawArc(mp, -2.2f, 2.6f, 2.2f, 2.2f, 0, 160); g.DrawArc(mp, 0, 2.6f, 2.2f, 2.2f, 20, 160);
        }
    }
}
