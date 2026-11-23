using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Kocky
{
    /// <summary>Drawing helpers shared by the whole game: palette, cached fonts and brushes, text, rounded shapes (taken over from the train game).</summary>
    public static class Kit
    {

        public static readonly Color Bg = C(0x0B1726), Panel = C(0x10233A), Panel2 = C(0x16304E), Line = C(0x23425F),
            Fg = C(0xE6EDF5), Sub = C(0x93A8C0), Amber = C(0xFFC53D), Ink = C(0x1F2A37), Cream = C(0xFBF6EA);

        public static Color C(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }
        public static Color A(Color c, int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B); }
        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // ---------- fonts ----------

        static FontFamily Family(params string[] names)
        {
            foreach (var n in names)
            {
                try { return new FontFamily(n); }
                catch (ArgumentException) { }
            }
            return FontFamily.GenericSansSerif;
        }

        public static readonly FontFamily SB = Family("Bahnschrift SemiBold", "Segoe UI Semibold", "Arial");
        public static readonly FontFamily RG = Family("Bahnschrift", "Segoe UI", "Arial");
        public static readonly FontFamily LT = Family("Bahnschrift Light", "Segoe UI Light", "Arial");
        public static readonly FontFamily SBC = Family("Bahnschrift SemiBold Condensed", "Bahnschrift SemiBold", "Segoe UI Semibold", "Arial");
        public static readonly FontFamily UI = Family("Segoe UI", "Arial");

        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        public static Font F(FontFamily fam, float px, FontStyle st)
        {
            string key = fam.Name + "|" + px + "|" + (int)st;
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                f = new Font(fam, px, st, GraphicsUnit.Pixel);
                fonts[key] = f;
            }
            return f;
        }

        public static Font Bold(float px) { return F(SB, px, FontStyle.Regular); }
        public static Font Reg(float px) { return F(RG, px, FontStyle.Regular); }
        public static Font Light(float px) { return F(LT, px, FontStyle.Regular); }
        public static Font Cond(float px) { return F(SBC, px, FontStyle.Regular); }
        public static Font Ital(float px) { return F(UI, px, FontStyle.Italic); }

        // ---------- text ----------

        static readonly Dictionary<int, StringFormat> formats = new Dictionary<int, StringFormat>();

        public static StringFormat Fmt(StringAlignment h, StringAlignment v, bool wrap)
        {
            int key = (int)h * 10 + (int)v * 100 + (wrap ? 1 : 0);
            StringFormat f;
            if (!formats.TryGetValue(key, out f))
            {
                f = new StringFormat(StringFormat.GenericTypographic);
                f.Alignment = h;
                f.LineAlignment = v;
                f.Trimming = StringTrimming.EllipsisCharacter;
                f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                if (!wrap) f.FormatFlags |= StringFormatFlags.NoWrap;
                formats[key] = f;
            }
            return f;
        }

        static readonly Graphics measurer = MakeMeasurer();
        static Graphics MakeMeasurer()
        {
            var g = Graphics.FromImage(new Bitmap(1, 1));
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            return g;
        }

        public static SizeF Measure(string s, Font f)
        {
            return measurer.MeasureString(s, f, new PointF(0, 0), Fmt(StringAlignment.Near, StringAlignment.Near, false));
        }

        static readonly Dictionary<int, SolidBrush> brushes = new Dictionary<int, SolidBrush>();
        public static SolidBrush B(Color c)
        {
            SolidBrush b;
            int key = c.ToArgb();
            if (!brushes.TryGetValue(key, out b)) { b = new SolidBrush(c); brushes[key] = b; }
            return b;
        }

        public static void Text(Graphics g, string s, Font f, Color c, float x, float y)
        {
            g.DrawString(s, f, B(c), x, y, Fmt(StringAlignment.Near, StringAlignment.Near, false));
        }

        public static void Text(Graphics g, string s, Font f, Color c, float x, float y, StringAlignment h, StringAlignment v)
        {
            g.DrawString(s, f, B(c), new PointF(x, y), Fmt(h, v, false));
        }

        public static void TextBox(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h, StringAlignment v)
        {
            g.DrawString(s, f, B(c), r, Fmt(h, v, true));
        }

        public static void TextLine(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h)
        {
            g.DrawString(s, f, B(c), r, Fmt(h, StringAlignment.Center, false));
        }

        public static void Halo(Graphics g, string s, Font f, Color c, Color halo, float x, float y, StringAlignment h, StringAlignment v)
        {
            var fmt = Fmt(h, v, false);
            var hb = B(halo);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0) g.DrawString(s, f, hb, new PointF(x + dx * 1.4f, y + dy * 1.4f), fmt);
            g.DrawString(s, f, B(c), new PointF(x, y), fmt);
        }

        // ---------- shapes ----------

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static GraphicsPath Round4(RectangleF r, float tl, float tr, float br, float bl)
        {
            var p = new GraphicsPath();
            Action<float, float, float, float> arc = (x, y, rad, start) =>
            {
                if (rad <= 0.5f) p.AddLine(x, y, x, y);
                else p.AddArc(x, y, rad * 2, rad * 2, start, 90);
            };
            arc(r.X, r.Y, tl, 180);
            arc(r.Right - tr * 2, r.Y, tr, 270);
            arc(r.Right - br * 2, r.Bottom - br * 2, br, 0);
            arc(r.X, r.Bottom - bl * 2, bl, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float rad)
        {
            using (var p = Round(r, rad)) g.FillPath(B(c), p);
        }

        public static void StrokeRound(Graphics g, Color c, float w, RectangleF r, float rad)
        {
            using (var p = Round(r, rad))
            using (var pen = new Pen(c, w)) g.DrawPath(pen, p);
        }

        public static Pen P(Color c, float w)
        {
            var pen = new Pen(c, w);
            pen.LineJoin = LineJoin.Round;
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            return pen;
        }

        public static void Circle(Graphics g, Color fill, float cx, float cy, float r)
        {
            g.FillEllipse(B(fill), cx - r, cy - r, r * 2, r * 2);
        }

        public static void Ring(Graphics g, Color c, float w, float cx, float cy, float r)
        {
            using (var pen = new Pen(c, w)) g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
        }

        public static void Shadow(Graphics g, RectangleF r, float rad, int strength)
        {
            FillRound(g, Color.FromArgb(strength / 2, 0, 0, 0), new RectangleF(r.X + 2, r.Y + 7, r.Width - 4, r.Height - 2), rad + 2);
            FillRound(g, Color.FromArgb(strength / 2, 0, 0, 0), new RectangleF(r.X, r.Y + 2, r.Width, r.Height + 1), rad);
        }
    }
}
