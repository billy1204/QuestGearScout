// QuestGearScout Companion - drawing helpers and small custom controls for
// the "Scout's Notebook" window. C# 5 / .NET Framework 4.x WinForms.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace QGSCompanion
{
    // The palette from the design spec (light theme).
    static class Pal
    {
        public static readonly Color Window = Hex("#F6F7F9"), Card = Color.White, CardBorder = Hex("#E3E6EA"), FooterBg = Hex("#FAFBFC");
        public static readonly Color Text = Hex("#1F2328"), Sub = Hex("#5F6B7A"), Muted = Hex("#98A2AE");
        public static readonly Color Accent = Hex("#2F6FED"), AccentBg = Hex("#EEF4FF"), AccentBorder = Hex("#C9DAFB");
        public static readonly Color Added = Hex("#C26A12"), AddedBg = Hex("#FDF1E4"), AddedBorder = Hex("#F2C79B");
        public static readonly Color Uncommon = Hex("#128C2B"), Rare = Hex("#1A5FD1"), Epic = Hex("#8A3FD1"), Legendary = Hex("#C25E00");
        public static readonly Color Dungeon = Hex("#6B4FBB"), DungeonBg = Hex("#F3F0FB"), DungeonBorder = Hex("#D9CFF2");
        public static readonly Color SuccessBg = Hex("#EAF7EE"), SuccessBorder = Hex("#BFE5CB"), Success = Hex("#1E7B3A"), SuccessDot = Hex("#2DA44E");
        public static readonly Color WarnBg = Hex("#FFF6E5"), WarnBorder = Hex("#F2D19B"), Warn = Hex("#8A5300");
        public static readonly Color Fresh = Hex("#FFF8D6");

        public static Color Hex(string h)
        {
            h = h.TrimStart('#');
            return Color.FromArgb(Convert.ToInt32(h.Substring(0, 2), 16), Convert.ToInt32(h.Substring(2, 2), 16), Convert.ToInt32(h.Substring(4, 2), 16));
        }

        public static Color Quality(int q)
        {
            return q >= 5 ? Legendary : q == 4 ? Epic : q == 3 ? Rare : Uncommon;
        }
    }

    static class Draw
    {
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Card(Graphics g, RectangleF r, Color fill, Color border, float radius)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), radius))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var p = new Pen(border)) g.DrawPath(p, path);
            }
        }

        // A rounded "pill" with text; returns its width.
        public static int Pill(Graphics g, string text, Font f, Point at, Color fill, Color border, Color fore, int padX, int height, bool rightAlign)
        {
            var sz = TextRenderer.MeasureText(g, text, f, Size.Empty, TextFormatFlags.NoPadding);
            int w = sz.Width + padX * 2;
            int x = rightAlign ? at.X - w : at.X;
            var r = new Rectangle(x, at.Y, w, height);
            Card(g, r, fill, border, height / 2f);
            TextRenderer.DrawText(g, text, f, r, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return w;
        }

        public static void Text(Graphics g, string text, Font f, Rectangle r, Color c, TextFormatFlags extra)
        {
            TextRenderer.DrawText(g, text, f, r, c, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | extra);
        }

        public static int Width(Graphics g, string text, Font f)
        {
            return TextRenderer.MeasureText(g, text, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        }

        // Tiny code-drawn icons (no image files needed).
        public static void Icon(Graphics g, string kind, RectangleF r, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = Math.Max(1.4f, r.Width / 10f);
            using (var p = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var b = new SolidBrush(c))
            {
                float x = r.X, y = r.Y, s = r.Width;
                switch (kind)
                {
                    case "sword":
                        g.DrawLine(p, x + s * 0.22f, y + s * 0.78f, x + s * 0.82f, y + s * 0.18f);
                        g.DrawLine(p, x + s * 0.30f, y + s * 0.52f, x + s * 0.48f, y + s * 0.70f);
                        g.DrawLine(p, x + s * 0.12f, y + s * 0.88f, x + s * 0.26f, y + s * 0.74f);
                        break;
                    case "castle":
                        g.DrawRectangle(p, x + s * 0.18f, y + s * 0.40f, s * 0.64f, s * 0.46f);
                        g.DrawLine(p, x + s * 0.18f, y + s * 0.40f, x + s * 0.18f, y + s * 0.20f);
                        g.DrawLine(p, x + s * 0.50f, y + s * 0.40f, x + s * 0.50f, y + s * 0.20f);
                        g.DrawLine(p, x + s * 0.82f, y + s * 0.40f, x + s * 0.82f, y + s * 0.20f);
                        g.DrawLine(p, x + s * 0.42f, y + s * 0.86f, x + s * 0.42f, y + s * 0.64f);
                        g.DrawLine(p, x + s * 0.58f, y + s * 0.86f, x + s * 0.58f, y + s * 0.64f);
                        g.DrawLine(p, x + s * 0.42f, y + s * 0.64f, x + s * 0.58f, y + s * 0.64f);
                        break;
                    case "scroll":
                        g.DrawRectangle(p, x + s * 0.22f, y + s * 0.16f, s * 0.56f, s * 0.68f);
                        g.DrawLine(p, x + s * 0.34f, y + s * 0.36f, x + s * 0.66f, y + s * 0.36f);
                        g.DrawLine(p, x + s * 0.34f, y + s * 0.52f, x + s * 0.66f, y + s * 0.52f);
                        g.DrawLine(p, x + s * 0.34f, y + s * 0.68f, x + s * 0.56f, y + s * 0.68f);
                        break;
                    case "pin":
                        g.DrawEllipse(p, x + s * 0.30f, y + s * 0.14f, s * 0.40f, s * 0.40f);
                        g.DrawLine(p, x + s * 0.50f, y + s * 0.54f, x + s * 0.50f, y + s * 0.88f);
                        break;
                    case "check":
                        g.DrawLines(p, new[] { new PointF(x + s * 0.20f, y + s * 0.52f), new PointF(x + s * 0.42f, y + s * 0.74f), new PointF(x + s * 0.82f, y + s * 0.28f) });
                        break;
                    case "sparkle":
                        g.DrawLine(p, x + s * 0.50f, y + s * 0.10f, x + s * 0.50f, y + s * 0.90f);
                        g.DrawLine(p, x + s * 0.10f, y + s * 0.50f, x + s * 0.90f, y + s * 0.50f);
                        g.DrawLine(p, x + s * 0.24f, y + s * 0.24f, x + s * 0.76f, y + s * 0.76f);
                        g.DrawLine(p, x + s * 0.76f, y + s * 0.24f, x + s * 0.24f, y + s * 0.76f);
                        break;
                    case "map":
                        g.DrawLines(p, new[] { new PointF(x + s * 0.12f, y + s * 0.24f), new PointF(x + s * 0.38f, y + s * 0.14f), new PointF(x + s * 0.62f, y + s * 0.24f),
                            new PointF(x + s * 0.88f, y + s * 0.14f), new PointF(x + s * 0.88f, y + s * 0.76f), new PointF(x + s * 0.62f, y + s * 0.86f),
                            new PointF(x + s * 0.38f, y + s * 0.76f), new PointF(x + s * 0.12f, y + s * 0.86f), new PointF(x + s * 0.12f, y + s * 0.24f) });
                        g.DrawLine(p, x + s * 0.38f, y + s * 0.14f, x + s * 0.38f, y + s * 0.76f);
                        g.DrawLine(p, x + s * 0.62f, y + s * 0.24f, x + s * 0.62f, y + s * 0.86f);
                        break;
                    case "warn":
                        g.DrawPolygon(p, new[] { new PointF(x + s * 0.50f, y + s * 0.12f), new PointF(x + s * 0.90f, y + s * 0.84f), new PointF(x + s * 0.10f, y + s * 0.84f) });
                        g.DrawLine(p, x + s * 0.50f, y + s * 0.38f, x + s * 0.50f, y + s * 0.58f);
                        g.FillEllipse(b, x + s * 0.46f, y + s * 0.66f, s * 0.08f, s * 0.08f);
                        break;
                    case "info":
                        g.DrawEllipse(p, x + s * 0.10f, y + s * 0.10f, s * 0.80f, s * 0.80f);
                        g.DrawLine(p, x + s * 0.50f, y + s * 0.44f, x + s * 0.50f, y + s * 0.70f);
                        g.FillEllipse(b, x + s * 0.45f, y + s * 0.26f, s * 0.10f, s * 0.10f);
                        break;
                    case "lock":
                        g.DrawRectangle(p, x + s * 0.22f, y + s * 0.46f, s * 0.56f, s * 0.40f);
                        g.DrawArc(p, x + s * 0.32f, y + s * 0.14f, s * 0.36f, s * 0.48f, 180, 180);
                        break;
                    case "compass":
                        using (var gold = new Pen(Pal.Hex("#C9971C"), w * 1.2f)) g.DrawEllipse(gold, x + s * 0.08f, y + s * 0.08f, s * 0.84f, s * 0.84f);
                        g.FillPolygon(b, new[] { new PointF(x + s * 0.50f, y + s * 0.18f), new PointF(x + s * 0.60f, y + s * 0.50f), new PointF(x + s * 0.40f, y + s * 0.50f) });
                        using (var grey = new SolidBrush(Pal.Muted))
                            g.FillPolygon(grey, new[] { new PointF(x + s * 0.50f, y + s * 0.82f), new PointF(x + s * 0.60f, y + s * 0.50f), new PointF(x + s * 0.40f, y + s * 0.50f) });
                        break;
                    case "search":
                        g.DrawEllipse(p, x + s * 0.14f, y + s * 0.14f, s * 0.50f, s * 0.50f);
                        g.DrawLine(p, x + s * 0.56f, y + s * 0.56f, x + s * 0.86f, y + s * 0.86f);
                        break;
                    case "undo":
                        g.DrawArc(p, x + s * 0.22f, y + s * 0.26f, s * 0.60f, s * 0.52f, 200, 250);
                        g.DrawLines(p, new[] { new PointF(x + s * 0.14f, y + s * 0.30f), new PointF(x + s * 0.26f, y + s * 0.50f), new PointF(x + s * 0.44f, y + s * 0.38f) });
                        break;
                    case "folder":
                        g.DrawLines(p, new[] { new PointF(x + s * 0.12f, y + s * 0.78f), new PointF(x + s * 0.12f, y + s * 0.24f), new PointF(x + s * 0.40f, y + s * 0.24f),
                            new PointF(x + s * 0.48f, y + s * 0.34f), new PointF(x + s * 0.88f, y + s * 0.34f), new PointF(x + s * 0.88f, y + s * 0.78f), new PointF(x + s * 0.12f, y + s * 0.78f) });
                        break;
                }
            }
        }

        public static Bitmap IconBitmap(string kind, int size, Color c)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp)) Icon(g, kind, new RectangleF(0, 0, size, size), c);
            return bmp;
        }
    }

    // A flat button with rounded corners, in "primary" (filled), "secondary"
    // (outlined) or "ghost" style. Built on Button so keyboard focus and Enter
    // work as usual.
    class FlatBtn : Button
    {
        public string Kind = "secondary";
        public Color Tint = Pal.Accent;
        public string IconName;

        public FlatBtn()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        bool hover;
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Pal.Window);
            var r = new RectangleF(0, 0, Width, Height);
            Color fill, border, fore;
            if (!Enabled) { fill = Pal.Window; border = Pal.CardBorder; fore = Pal.Muted; }
            else if (Kind == "primary") { fill = hover ? ControlPaint.Dark(Tint, 0.05f) : Tint; border = fill; fore = Color.White; }
            else if (Kind == "ghost") { fill = hover ? Pal.Window : (Parent != null ? Parent.BackColor : Pal.Window); border = hover ? Pal.CardBorder : fill; fore = Pal.Text; }
            else { fill = hover ? Pal.AccentBg : Color.White; border = Kind == "secondary" && Tint != Pal.Accent ? Tint : Pal.AccentBorder; fore = Tint; }
            Draw.Card(g, r, fill, border, 5f);
            int iconW = 0;
            int ih = (int)(Height * 0.46f);
            int tw = Draw.Width(g, Text, Font);
            if (IconName != null) iconW = ih + (Text.Length > 0 ? 5 : 0);
            int x = (Width - tw - iconW) / 2;
            if (IconName != null) Draw.Icon(g, IconName, new RectangleF(x, (Height - ih) / 2f, ih, ih), fore);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x + iconW, 0, Width - x - iconW, Height), fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.Left);
            if (Focused && ShowFocusCues)
                using (var p = new Pen(Pal.Accent) { DashStyle = DashStyle.Dot }) g.DrawRectangle(p, 2, 2, Width - 5, Height - 5);
        }

        public void FitWidth(int padX, int minW)
        {
            using (var g = CreateGraphics())
            {
                int w = Draw.Width(g, Text, Font) + padX * 2 + (IconName != null ? (int)(Height * 0.46f) + 5 : 0);
                Width = Math.Max(minW, w);
            }
        }
    }

    // A pill toggle for the filter row and the view switch.
    class PillToggle : Control
    {
        public bool Checked;
        public bool Dot;
        public event EventHandler Picked;

        public PillToggle()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        bool hover;
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e) { Focus(); if (Picked != null) Picked(this, e); base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { e.Handled = true; if (Picked != null) Picked(this, e); }
            base.OnKeyDown(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnTextChanged(EventArgs e) { FitWidth(); Invalidate(); base.OnTextChanged(e); }

        public void FitWidth()
        {
            if (!IsHandleCreated && Parent == null) { }
            using (var g = CreateGraphics()) Width = Draw.Width(g, Text, Font) + Height + (Dot ? Height / 2 : 0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Pal.Window);
            Color fill = Checked ? Pal.Accent : hover ? Pal.AccentBg : Color.White;
            Color border = Checked ? Pal.Accent : Pal.CardBorder;
            Color fore = Checked ? Color.White : Pal.Text;
            Draw.Card(g, new RectangleF(0, 0, Width, Height), fill, border, Height / 2f);
            int x = Height / 2;
            if (Dot)
            {
                float d = Height * 0.26f;
                using (var b = new SolidBrush(Checked ? Color.White : Pal.Accent)) g.FillEllipse(b, x, (Height - d) / 2f, d, d);
                x += (int)(d + Height * 0.2f);
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues)
                using (var p = new Pen(Checked ? Color.White : Pal.Accent) { DashStyle = DashStyle.Dot }) g.DrawRectangle(p, 3, 3, Width - 7, Height - 7);
        }
    }

    // Panel that paints itself as a rounded card.
    class CardPanel : Panel
    {
        public Color Fill = Color.White, Border = Pal.CardBorder;
        public float Radius = 6f;
        public CardPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Pal.Window);
            Draw.Card(e.Graphics, new RectangleF(0, 0, Width, Height), Fill, Border, Radius);
        }
    }

    class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
    }

    // A painted panel that can take focus, so the mouse wheel scrolls it.
    class ScrollPanel : BufferedPanel
    {
        public ScrollPanel() { SetStyle(ControlStyles.Selectable, true); TabStop = false; }
        protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
    }

    // Group header line in the Notebook list ("New finds (1)", "Ironforge (3)").
    class GroupHeader
    {
        public string Text;
        public bool IsNew;
        public override string ToString() { return Text; }
    }
}
