// QuestGearScout Companion - drawing helpers and small custom controls for
// the "Scout's Notebook" window. C# 5 / .NET Framework 4.x WinForms.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace QGSCompanion
{
    // The "Ember Forge" palette: dark iron, ember-orange accent, gold trim.
    static class Pal
    {
        public static readonly Color Window = Hex("#15110E"), Card = Hex("#221C17"), CardBorder = Hex("#3A3029"), FooterBg = Hex("#0E0B09");
        public static readonly Color Text = Hex("#EFE5D4"), Sub = Hex("#B9A88E"), Muted = Hex("#978871");
        public static readonly Color Accent = Hex("#E07A2E"), AccentBg = Hex("#3A2618"), AccentBorder = Hex("#7A4522"), OnAccent = Hex("#1A110A");
        public static readonly Color Trim = Hex("#C9A24E"), Hover = Hex("#2B231D");
        public static readonly Color Input = Hex("#120E0B"), InputBorder = Hex("#3F342B");
        public static readonly Color Added = Hex("#E8C25A"), AddedBg = Hex("#3A2C14"), AddedBorder = Hex("#7A5E22");
        public static readonly Color Uncommon = Hex("#5BD06E"), Rare = Hex("#5AA2FF"), Epic = Hex("#BC86FF"), Legendary = Hex("#FF9A3C");
        public static readonly Color Dungeon = Hex("#B9A0FF"), DungeonBg = Hex("#262038"), DungeonBorder = Hex("#4A3D73");
        public static readonly Color SuccessBg = Hex("#16261B"), SuccessBorder = Hex("#2F5A3A"), Success = Hex("#6FD08A"), SuccessDot = Hex("#4CC764");
        public static readonly Color WarnBg = Hex("#33280F"), WarnBorder = Hex("#6E5520"), Warn = Hex("#F0C76A");
        public static readonly Color Error = Hex("#FF8A7A"), ErrorBg = Hex("#3A1C18"), ErrorBorder = Hex("#7A2E26");
        public static readonly Color Fresh = Hex("#2E2614");
        public static readonly Color Gold = Hex("#D9A932"), GoldBorder = Hex("#9A7212");

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
                        using (var gold = new Pen(Pal.Trim, w * 1.2f)) g.DrawEllipse(gold, x + s * 0.08f, y + s * 0.08f, s * 0.84f, s * 0.84f);
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
                    case "group":
                        g.DrawEllipse(p, x + s * 0.20f, y + s * 0.18f, s * 0.26f, s * 0.26f);
                        g.DrawArc(p, x + s * 0.08f, y + s * 0.52f, s * 0.50f, s * 0.50f, 180, 180);
                        g.DrawEllipse(p, x + s * 0.56f, y + s * 0.26f, s * 0.22f, s * 0.22f);
                        g.DrawArc(p, x + s * 0.48f, y + s * 0.56f, s * 0.42f, s * 0.42f, 200, 140);
                        break;
                    case "chevron-right":
                        g.DrawLines(p, new[] { new PointF(x + s * 0.38f, y + s * 0.22f), new PointF(x + s * 0.66f, y + s * 0.50f), new PointF(x + s * 0.38f, y + s * 0.78f) });
                        break;
                    case "chevron-left":
                        g.DrawLines(p, new[] { new PointF(x + s * 0.62f, y + s * 0.22f), new PointF(x + s * 0.34f, y + s * 0.50f), new PointF(x + s * 0.62f, y + s * 0.78f) });
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
            else if (Kind == "primary") { fill = hover ? ControlPaint.Light(Tint, 0.15f) : Tint; border = fill; fore = Pal.OnAccent; }
            else if (Kind == "ghost") { fill = hover ? Pal.Hover : (Parent != null ? Parent.BackColor : Pal.Window); border = hover ? Pal.CardBorder : fill; fore = Pal.Text; }
            else { fill = hover ? Pal.AccentBg : Pal.Card; border = Kind == "secondary" && Tint != Pal.Accent ? Tint : Pal.AccentBorder; fore = Tint; }
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
                using (var p = new Pen(Kind == "primary" && Enabled ? Pal.OnAccent : Pal.Accent) { DashStyle = DashStyle.Dot }) g.DrawRectangle(p, 2, 2, Width - 5, Height - 5);
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
            Color fill = Checked ? Pal.Accent : hover ? Pal.AccentBg : Pal.Card;
            Color border = Checked ? Pal.Accent : Pal.CardBorder;
            Color fore = Checked ? Pal.OnAccent : Pal.Text;
            Draw.Card(g, new RectangleF(0, 0, Width, Height), fill, border, Height / 2f);
            int x = Height / 2;
            if (Dot)
            {
                float d = Height * 0.26f;
                using (var b = new SolidBrush(Checked ? Pal.OnAccent : Pal.Accent)) g.FillEllipse(b, x, (Height - d) / 2f, d, d);
                x += (int)(d + Height * 0.2f);
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues)
                using (var p = new Pen(Checked ? Pal.OnAccent : Pal.Accent) { DashStyle = DashStyle.Dot }) g.DrawRectangle(p, 3, 3, Width - 7, Height - 7);
        }
    }

    // Applies the dark look to the parts Windows draws itself: the title bar,
    // scrollbars, menus and standard input controls.
    static class Theme
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        // Dark title bar on Windows 10 (1809+) and 11; silently ignored elsewhere.
        public static void DarkTitle(Form f)
        {
            EventHandler apply = delegate
            {
                try
                {
                    int on = 1;
                    if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
                    int caption = ColorTranslator.ToWin32(Pal.FooterBg), text = ColorTranslator.ToWin32(Pal.Text), border = ColorTranslator.ToWin32(Pal.CardBorder);
                    DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);
                    DwmSetWindowAttribute(f.Handle, 36, ref text, 4);
                    DwmSetWindowAttribute(f.Handle, 34, ref border, 4);
                }
                catch (Exception) { }
            };
            if (f.IsHandleCreated) apply(f, EventArgs.Empty); else f.HandleCreated += apply;
        }

        // Dark scrollbars for a scrollable control.
        public static void DarkScroll(Control c)
        {
            string theme = c is ComboBox ? "DarkMode_CFD" : "DarkMode_Explorer";
            EventHandler apply = delegate { try { SetWindowTheme(c.Handle, theme, null); } catch (Exception) { } };
            if (c.IsHandleCreated) apply(c, EventArgs.Empty); else c.HandleCreated += apply;
        }

        // A form or dialog: dark title bar, colours, and every standard child control.
        public static void Form(Form f)
        {
            f.BackColor = Pal.Card;
            f.ForeColor = Pal.Text;
            DarkTitle(f);
            Tree(f);
            f.ControlAdded += delegate(object s, ControlEventArgs e) { Tree(e.Control); };
        }

        // Colours the standard WinForms controls in a tree (custom-painted ones are left alone).
        public static void Tree(Control root)
        {
            Fix(root);
            foreach (Control c in root.Controls) Tree(c);
        }

        static void Fix(Control c)
        {
            if (c is TextBoxBase)
            {
                var tb = (TextBoxBase)c;
                c.BackColor = Pal.Input; c.ForeColor = Pal.Text;
                if (tb.BorderStyle == BorderStyle.Fixed3D) tb.BorderStyle = BorderStyle.FixedSingle;
                if (tb.Multiline) DarkScroll(c);
            }
            else if (c is ListView)
            {
                DarkList((ListView)c);
            }
            else if (c is ListBox && ((ListBox)c).DrawMode != DrawMode.Normal)
            {
                DarkScroll(c);   // owner-drawn lists paint their own rows
            }
            else if (c is ListBox || c is ComboBox || c is NumericUpDown || c is TreeView)
            {
                c.BackColor = Pal.Input; c.ForeColor = Pal.Text; DarkScroll(c);
                var cb = c as ComboBox;
                if (cb != null && cb.DropDownStyle == ComboBoxStyle.DropDownList)
                {
                    // Standard style lets the dark "CFD" visual style draw the frame and arrow
                    // (Flat style paints them light on .NET Framework).
                    cb.FlatStyle = FlatStyle.Standard;
                    if (cb.DrawMode == DrawMode.Normal)
                    {
                        cb.DrawMode = DrawMode.OwnerDrawFixed;
                        cb.DrawItem += DrawComboItem;
                    }
                }
            }
            else if (c is LinkLabel)
            {
                var l = (LinkLabel)c;
                l.LinkColor = Pal.Accent; l.ActiveLinkColor = Pal.Trim; l.VisitedLinkColor = Pal.Accent; l.DisabledLinkColor = Pal.Muted;
            }
            else if (c is CheckBox || c is RadioButton)
            {
                var b = (ButtonBase)c;
                if (b.FlatStyle == FlatStyle.Standard || b.FlatStyle == FlatStyle.System) b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = Pal.InputBorder;
                b.FlatAppearance.CheckedBackColor = Pal.Input;
                b.FlatAppearance.MouseOverBackColor = Pal.Hover;
                b.ForeColor = Pal.Text;
                b.BackColor = Color.Transparent;
                var cbx = c as CheckBox;
                if (cbx != null && cbx.Appearance == Appearance.Normal) cbx.Paint += PaintCheckGlyph;
            }
            else if (c is ScrollableControl && ((ScrollableControl)c).AutoScroll) DarkScroll(c);
            var ts = c as ToolStrip;
            if (ts != null) Strip(ts);
        }

        // Repaints a flat CheckBox's square (WinForms always fills it white) in the dark colours.
        static void PaintCheckGlyph(object sender, PaintEventArgs e)
        {
            var cb = (CheckBox)sender;
            var g = e.Graphics;
            // WinForms' flat square is 11px scaled by the DPI; cover it fully.
            int native = (int)(11 * g.DpiX / 96f);
            int sz = native + 2, y = (cb.Height - sz) / 2;
            using (var clear = new SolidBrush(cb.Parent != null ? cb.Parent.BackColor : Pal.Card)) g.FillRectangle(clear, 0, y - 2, sz + 3, sz + 4);
            var box = new RectangleF(0, y, sz, sz);
            bool on = cb.Checked;
            Draw.Card(g, box, on ? Pal.Accent : Pal.Input, on ? Pal.Accent : (cb.Focused ? Pal.Accent : Pal.InputBorder), 3f);
            if (on) Draw.Icon(g, "check", new RectangleF(box.X + 1, box.Y + 1, sz - 2, sz - 2), Pal.OnAccent);
        }

        static void DrawComboItem(object sender, DrawItemEventArgs e)
        {
            var cb = (ComboBox)sender;
            bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var b = new SolidBrush(sel ? Pal.AccentBg : Pal.Input)) e.Graphics.FillRectangle(b, e.Bounds);
            if (e.Index < 0) return;
            var r = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height);
            Draw.Text(e.Graphics, cb.GetItemText(cb.Items[e.Index]), cb.Font, r, Pal.Text, TextFormatFlags.VerticalCenter);
        }

        // Stretches the last column so the header has no unpainted gap on its right. The
        // column's own width is kept in its Tag (DesignedWidth), so it shrinks back when
        // the list gets narrower and that's the width that gets saved.
        static readonly System.Collections.Generic.HashSet<ListView> filling = new System.Collections.Generic.HashSet<ListView>();
        static void FillLastColumn(ListView lv)
        {
            if (lv.Columns.Count == 0 || !lv.IsHandleCreated || filling.Contains(lv)) return;
            filling.Add(lv);
            try
            {
                int used = 0;
                for (int i = 0; i < lv.Columns.Count - 1; i++) used += lv.Columns[i].Width;
                var last = lv.Columns[lv.Columns.Count - 1];
                int designed = DesignedWidth(last);
                int want = Math.Max(designed, lv.ClientSize.Width - used);
                if (last.Width != want) last.Width = want;
            }
            finally { filling.Remove(lv); }
        }

        // A column's width as set by the program or the user, ignoring the stretch above.
        public static int DesignedWidth(ColumnHeader c)
        {
            if (!(c.Tag is int)) c.Tag = c.Width;
            return (int)c.Tag;
        }

        // Sets a column's real width (e.g. restored from settings) and remembers it.
        public static void SetDesignedWidth(ColumnHeader c, int w) { c.Tag = w; c.Width = w; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

        // Message box in the app's colours, with the same arguments as MessageBox.Show.
        public static DialogResult Box(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            using (var f = BuildBox(text, caption, buttons, icon))
            {
                f.StartPosition = owner != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
                return owner != null ? f.ShowDialog(owner) : f.ShowDialog();
            }
        }

        public static Form BuildBox(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            float k;
            using (var tmp = new Form()) using (var g = tmp.CreateGraphics()) k = g.DpiX / 96f;
            Func<int, int> S = v => (int)Math.Round(v * k);
            var f = new Form { Text = caption, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
                AutoScaleMode = AutoScaleMode.None, Font = new Font("Segoe UI", 9f) };
            {
                string iconName = icon == MessageBoxIcon.Error || icon == MessageBoxIcon.Warning ? "warn" : icon == MessageBoxIcon.Question ? "info" : "info";
                Color tint = icon == MessageBoxIcon.Error ? Pal.Error : icon == MessageBoxIcon.Warning ? Pal.Warn : Pal.Accent;
                int textX = S(64), maxW = S(420);
                var lbl = new Label { Text = text, AutoSize = true, MaximumSize = new Size(maxW, 0), Location = new Point(textX, S(24)), ForeColor = Pal.Text, UseMnemonic = false };
                f.Controls.Add(lbl);
                var pic = new PictureBox { Image = Draw.IconBitmap(iconName, S(28), tint), Size = new Size(S(28), S(28)), Location = new Point(S(22), S(22)) };
                f.Controls.Add(pic);
                var labels = buttons == MessageBoxButtons.OKCancel ? new[] { "OK", "Cancel" } : buttons == MessageBoxButtons.YesNo ? new[] { "Yes", "No" } : new[] { "OK" };
                var results = buttons == MessageBoxButtons.OKCancel ? new[] { DialogResult.OK, DialogResult.Cancel } : buttons == MessageBoxButtons.YesNo ? new[] { DialogResult.Yes, DialogResult.No } : new[] { DialogResult.OK };
                int textH = Math.Max(S(28), lbl.PreferredHeight);
                int w = Math.Max(S(320), textX + Math.Min(maxW, lbl.PreferredWidth) + S(28));
                int by = S(24) + textH + S(22);
                f.ClientSize = new Size(w, by + S(30) + S(18));
                var btns = new FlatBtn[labels.Length];
                int x = w - S(20);
                for (int i = labels.Length - 1; i >= 0; i--)
                {
                    var b = new FlatBtn { Kind = i == 0 ? "primary" : "secondary", Text = labels[i], Height = S(30), Font = i == 0 ? new Font("Segoe UI Semibold", 9f) : f.Font, DialogResult = results[i] };
                    b.Width = S(84); x -= b.Width; b.Location = new Point(x, by); x -= S(8);
                    f.Controls.Add(b); btns[i] = b;
                }
                f.AcceptButton = btns[0];
                f.CancelButton = btns[btns.Length - 1];
                Form(f);
            }
            return f;
        }

        // A details ListView with dark rows, an ember selection and dark column headers.
        public static void DarkList(ListView lv) { DarkList(lv, Pal.Input); }

        public static void DarkList(ListView lv, Color rowBg)
        {
            if (lv.OwnerDraw) return;
            lv.BackColor = rowBg; lv.ForeColor = Pal.Text; lv.OwnerDraw = true;
            DarkScroll(lv);
            lv.HandleCreated += delegate
            {
                FillLastColumn(lv);
                // The list's own item tooltips: dark too.
                try { var tip = SendMessage(lv.Handle, 0x104E /* LVM_GETTOOLTIPS */, IntPtr.Zero, IntPtr.Zero); if (tip != IntPtr.Zero) SetWindowTheme(tip, "DarkMode_Explorer", null); } catch (Exception) { }
            };
            lv.ClientSizeChanged += delegate { FillLastColumn(lv); };
            lv.ColumnWidthChanged += delegate(object s, ColumnWidthChangedEventArgs e)
            {
                if (filling.Contains(lv)) return;
                // The user (or the program) set this width: remember it as the column's own.
                lv.Columns[e.ColumnIndex].Tag = lv.Columns[e.ColumnIndex].Width;
                FillLastColumn(lv);
            };
            FillLastColumn(lv);
            lv.DrawColumnHeader += delegate(object s, DrawListViewColumnHeaderEventArgs e)
            {
                using (var b = new SolidBrush(Pal.Card)) e.Graphics.FillRectangle(b, e.Bounds);
                using (var p = new Pen(Pal.CardBorder))
                {
                    e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                    e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
                }
                var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
                Draw.Text(e.Graphics, e.Header.Text, lv.Font, r, Pal.Sub, TextFormatFlags.VerticalCenter | Align(e.Header.TextAlign));
            };
            lv.DrawItem += delegate(object s, DrawListViewItemEventArgs e) { e.DrawDefault = false; };
            lv.DrawSubItem += delegate(object s, DrawListViewSubItemEventArgs e)
            {
                bool sel = e.Item.Selected;
                Color bg = e.SubItem.BackColor, fg = e.SubItem.ForeColor;
                if (bg == SystemColors.Window || bg.IsEmpty) bg = lv.BackColor;
                if (fg == SystemColors.WindowText || fg.IsEmpty) fg = Pal.Text;
                using (var b = new SolidBrush(sel ? Pal.AccentBg : bg)) e.Graphics.FillRectangle(b, e.Bounds);
                if (sel && e.ColumnIndex == 0)
                    using (var bar = new SolidBrush(Pal.Accent)) e.Graphics.FillRectangle(bar, e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height);
                var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
                Draw.Text(e.Graphics, e.SubItem.Text, e.SubItem.Font ?? lv.Font, r, fg, TextFormatFlags.VerticalCenter | Align(e.Header.TextAlign));
            };
        }

        static TextFormatFlags Align(HorizontalAlignment a)
        {
            return a == HorizontalAlignment.Right ? TextFormatFlags.Right : a == HorizontalAlignment.Center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left;
        }

        public static void Strip(ToolStrip ts)
        {
            ts.RenderMode = ToolStripRenderMode.Professional;
            ts.Renderer = Renderer;
            ts.BackColor = ts is StatusStrip ? Pal.FooterBg : Pal.Card;
            ts.ForeColor = Pal.Sub;
        }

        public static readonly ToolStripProfessionalRenderer Renderer = new EmberRenderer();

        class EmberColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Pal.Card; } }
            public override Color MenuBorder { get { return Pal.CardBorder; } }
            public override Color MenuItemBorder { get { return Pal.AccentBorder; } }
            public override Color MenuItemSelected { get { return Pal.AccentBg; } }
            public override Color MenuItemSelectedGradientBegin { get { return Pal.AccentBg; } }
            public override Color MenuItemSelectedGradientEnd { get { return Pal.AccentBg; } }
            public override Color MenuItemPressedGradientBegin { get { return Pal.AccentBg; } }
            public override Color MenuItemPressedGradientEnd { get { return Pal.AccentBg; } }
            public override Color ImageMarginGradientBegin { get { return Pal.Card; } }
            public override Color ImageMarginGradientMiddle { get { return Pal.Card; } }
            public override Color ImageMarginGradientEnd { get { return Pal.Card; } }
            public override Color SeparatorDark { get { return Pal.CardBorder; } }
            public override Color SeparatorLight { get { return Pal.CardBorder; } }
            public override Color StatusStripGradientBegin { get { return Pal.FooterBg; } }
            public override Color StatusStripGradientEnd { get { return Pal.FooterBg; } }
            public override Color ToolStripBorder { get { return Pal.CardBorder; } }
            public override Color ToolStripGradientBegin { get { return Pal.Card; } }
            public override Color ToolStripGradientMiddle { get { return Pal.Card; } }
            public override Color ToolStripGradientEnd { get { return Pal.Card; } }
            public override Color CheckBackground { get { return Pal.AccentBg; } }
            public override Color CheckSelectedBackground { get { return Pal.AccentBg; } }
            public override Color CheckPressedBackground { get { return Pal.AccentBg; } }
            public override Color GripDark { get { return Pal.CardBorder; } }
            public override Color GripLight { get { return Pal.FooterBg; } }
        }

        class EmberRenderer : ToolStripProfessionalRenderer
        {
            public EmberRenderer() : base(new EmberColors()) { RoundedEdges = false; }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (e.Item.Owner is ToolStripDropDown)
                    e.TextColor = e.Item.Enabled ? Pal.Text : Pal.Muted;
                else if (e.Item.ForeColor == SystemColors.ControlText)
                    e.TextColor = Pal.Sub;
                base.OnRenderItemText(e);
            }
            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Pal.Sub; base.OnRenderArrow(e); }
            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                var r = e.ImageRectangle;
                Draw.Icon(e.Graphics, "check", new RectangleF(r.X, r.Y, r.Width, r.Height), Pal.Accent);
            }
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (e.ToolStrip is StatusStrip)
                    using (var p = new Pen(Pal.CardBorder)) e.Graphics.DrawLine(p, 0, 0, e.ToolStrip.Width, 0);
                else base.OnRenderToolStripBorder(e);
            }
        }

        public static ToolTip Tip(ToolTip t)
        {
            t.OwnerDraw = true;
            t.BackColor = Pal.Card;
            t.ForeColor = Pal.Text;
            t.Draw += delegate(object s, DrawToolTipEventArgs e)
            {
                using (var b = new SolidBrush(Pal.Card)) e.Graphics.FillRectangle(b, e.Bounds);
                using (var p = new Pen(Pal.CardBorder)) e.Graphics.DrawRectangle(p, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, e.Font, e.Bounds, Pal.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            };
            return t;
        }
    }

    // Panel that paints itself as a rounded card.
    class CardPanel : Panel
    {
        public Color Fill = Pal.Card, Border = Pal.CardBorder;
        public float Radius = 6f;
        public CardPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Pal.Card;   // child buttons clear their corners to this
            ForeColor = Pal.Text;
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
