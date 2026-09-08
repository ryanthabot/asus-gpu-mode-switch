//  Theme.cs  (v1.2.0)
//  -------------------
//  The visual system of the unified app. Kept from v1.0.x-1.1: WindowIcons,
//  UiShapes, ShimmerBar. Added in v1.2.0 (the redesign): Ui (the shared
//  palette + glyph-font resolver), ToggleSwitch (animated pill switch that
//  replaces every CheckBox in the app), NavButton (sidebar rail button),
//  ModeCard (the big Go Time / Eco Mode cards on Home), Card (rounded
//  group container), AccentButton (gradient call-to-action button),
//  GradientLabel (two-color heading), StatusChip (pill with a state dot)
//  and SpinGlyph (busy spinner). Everything is owner-drawn GDI+ with
//  double buffering; Segoe Fluent Icons / MDL2 glyphs are used when the
//  font family exists and degrade to plain text otherwise.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GpuModeSwitch
{
    // The shared palette + small drawing helpers for the unified deck.
    internal static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(13, 15, 21);          // window
        public static readonly Color BgSide = Color.FromArgb(9, 11, 16);       // sidebar rail
        public static readonly Color Card = Color.FromArgb(20, 24, 34);        // card surface
        public static readonly Color CardBorder = Color.FromArgb(41, 48, 66);
        public static readonly Color TextHi = Color.FromArgb(238, 241, 248);
        public static readonly Color Text = Color.FromArgb(190, 196, 210);
        public static readonly Color TextDim = Color.FromArgb(122, 130, 150);
        public static readonly Color Go = Color.FromArgb(255, 106, 61);        // Go Time orange
        public static readonly Color Eco = Color.FromArgb(55, 214, 122);       // Eco green
        public static readonly Color Cyan = Color.FromArgb(86, 199, 255);      // general accent
        public static readonly Color Amber = Color.FromArgb(255, 184, 82);     // locked / warning
        public static readonly Color Red = Color.FromArgb(255, 108, 108);

        // Lightens/darkens a color by a 0..1 fraction (positive = lighter).
        public static Color Shift(Color c, float amount)
        {
            int r = c.R, g = c.G, b = c.B;
            if (amount >= 0)
            {
                r = (int)(r + (255 - r) * amount);
                g = (int)(g + (255 - g) * amount);
                b = (int)(b + (255 - b) * amount);
            }
            else
            {
                r = (int)(r * (1 + amount));
                g = (int)(g * (1 + amount));
                b = (int)(b * (1 + amount));
            }
            return Color.FromArgb(Math.Max(0, Math.Min(255, r)),
                                  Math.Max(0, Math.Min(255, g)),
                                  Math.Max(0, Math.Min(255, b)));
        }

        private static string _glyphFamily;     // resolved once

        // "Segoe Fluent Icons" (Win11) -> "Segoe MDL2 Assets" (Win10) -> null.
        public static string GlyphFamily()
        {
            if (_glyphFamily != null) return _glyphFamily == "" ? null : _glyphFamily;
            string[] candidates = { "Segoe Fluent Icons", "Segoe MDL2 Assets" };
            foreach (string f in candidates)
            {
                try
                {
                    using (FontFamily fam = new FontFamily(f)) { }
                    _glyphFamily = f;
                    return f;
                }
                catch { }
            }
            _glyphFamily = "";
            return null;
        }

        public static Font Glyph(float size)
        {
            string f = GlyphFamily();
            return new Font(string.IsNullOrEmpty(f) ? "Segoe UI" : f, size, FontStyle.Regular);
        }

        public static bool HasGlyphs { get { return GlyphFamily() != null; } }
    }

    // Gives a form the executable's own icon (title bar / taskbar button).
    internal static class WindowIcons
    {
        public static void Apply(Form form)
        {
            try
            {
                form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                // exe icon unavailable for some reason - the generic one is fine
            }
        }
    }

    // Small shared drawing helpers.
    internal static class UiShapes
    {
        public static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            GraphicsPath p = new GraphicsPath();
            float d = r * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Indeterminate shimmer progress bar (themed, animated via Advance()).
    internal class ShimmerBar : Control
    {
        private float _pos = -0.35f;
        private bool _active;

        public ShimmerBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            Height = 8;
            TabStop = false;
        }

        public Color Accent { get; set; }

        public bool Active
        {
            get { return _active; }
            set
            {
                if (_active != value)
                {
                    _active = value;
                    if (value) _pos = -0.35f;
                    Invalidate();
                }
            }
        }

        public void Advance()
        {
            if (!_active) return;
            _pos += 0.03f;
            if (_pos > 1.35f) _pos = -0.35f;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (GraphicsPath track = UiShapes.RoundRect(0, 0, Width, Height, 4))
            {
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(40, 40, 47)))
                {
                    g.FillPath(bg, track);
                }

                if (_active)
                {
                    GraphicsState saved = g.Save();
                    g.SetClip(track);
                    float w = Math.Max(70, Width * 0.30f);
                    float x = _pos * Width - w / 2;
                    RectangleF seg = new RectangleF(x, -2, w, Height + 4);
                    using (LinearGradientBrush lgb = new LinearGradientBrush(
                        new RectangleF(x, -2, Math.Max(w, 1), Height + 4),
                        Color.FromArgb(0, Accent), Color.FromArgb(230, Accent), 0f))
                    {
                        lgb.SetBlendTriangularShape(0.5f, 1f);
                        g.FillRectangle(lgb, seg);
                    }
                    g.Restore(saved);
                }
            }
        }
    }

    // ---------------------------------------------------------------------
    // v1.2.0 controls
    // ---------------------------------------------------------------------

    // Animated pill switch (replaces every CheckBox). Shows the row label on
    // the left and a sliding switch on the right; disabled rows dim and show
    // a lock glyph so "why can't I tick this" is answered on the spot.
    internal class ToggleSwitch : Control
    {
        private bool _checked;
        private float _t;                        // 0 = off, 1 = on (animated)
        private Timer _anim;
        private Color _accent = Ui.Cyan;
        private bool _hover;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Height = 30;
            Cursor = Cursors.Hand;
            TabStop = true;
            BackColor = Color.Transparent;   // parents (Card / busy panel) paint their own bg
        }

        public Color Accent
        {
            get { return _accent; }
            set { _accent = value; Invalidate(); }
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                StartAnim();
            }
        }

        private void StartAnim()
        {
            if (_anim == null)
            {
                _anim = new Timer();
                _anim.Interval = 15;
                _anim.Tick += delegate
                {
                    float target = _checked ? 1f : 0f;
                    if (_t < target) _t = Math.Min(target, _t + 0.2f);
                    else if (_t > target) _t = Math.Max(target, _t - 0.2f);
                    Invalidate();
                    if (_t == target) _anim.Stop();
                };
            }
            _anim.Start();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnClick(EventArgs e)
        {
            if (Enabled) Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && Enabled)
            {
                e.SuppressKeyPress = true;
                Checked = !Checked;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            const int pillW = 46, pillH = 22, pad = 8;
            int pillX = Width - pillW - pad;
            int pillY = (Height - pillH) / 2;

            Color labelColor = !Enabled ? Ui.TextDim
                : _checked ? Ui.TextHi
                : _hover ? Ui.Text : Ui.Text;

            int textX = 2;
            if (!Enabled && Ui.HasGlyphs)
            {
                // locked row: the lock glyph says "you can't tick this"
                using (Font gf = Ui.Glyph(10.5f))
                {
                    TextRenderer.DrawText(g, "\uE72E", gf, new Rectangle(2, 0, 20, Height),
                        Ui.Amber, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                }
                textX = 22;
            }
            Rectangle textRect = new Rectangle(textX, 0, pillX - textX - 8, Height);
            TextRenderer.DrawText(g, Text, Font, textRect, labelColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // track + thumb
            Color track = !Enabled ? Color.FromArgb(38, 42, 54)
                : _checked ? _accent
                : Color.FromArgb(52, 58, 74);
            using (GraphicsPath trackPath = UiShapes.RoundRect(pillX, pillY, pillW, pillH, 11))
            {
                using (SolidBrush tb = new SolidBrush(track)) g.FillPath(tb, trackPath);
            }
            if (Enabled && _checked)
            {
                // glow under the active pill
                using (GraphicsPath glow = UiShapes.RoundRect(pillX - 2, pillY - 2, pillW + 4, pillH + 4, 13))
                {
                    using (Pen gp = new Pen(Color.FromArgb(70, _accent), 3)) g.DrawPath(gp, glow);
                }
            }
            int thumbD = 16;
            float thumbX = pillX + 3 + _t * (pillW - 6 - thumbD);
            int thumbY = pillY + (pillH - thumbD) / 2;
            using (SolidBrush thumb = new SolidBrush(Enabled ? Color.FromArgb(240, 242, 248) : Color.FromArgb(120, 124, 136)))
            {
                g.FillEllipse(thumb, thumbX, thumbY, thumbD, thumbD);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _anim != null) _anim.Dispose();
            base.Dispose(disposing);
        }
    }

    // Sidebar rail button: glyph on top, small label under, accent bar when
    // active. Raises Click through the normal Control event.
    internal class NavButton : Control
    {
        private readonly string _glyph;
        private readonly string _label;
        private bool _active;
        private bool _hover;

        public NavButton(string glyph, string label)
        {
            _glyph = glyph;
            _label = label;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            Size = new Size(86, 58);
            Cursor = Cursors.Hand;
            TabStop = true;
            BackColor = Ui.BgSide;   // the rail paints itself; corners must blend
        }

        public bool Active
        {
            get { return _active; }
            set { _active = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                OnClick(EventArgs.Empty);
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_active || _hover)
            {
                using (SolidBrush bg = new SolidBrush(_active ? Color.FromArgb(27, 32, 46) : Color.FromArgb(18, 22, 32)))
                {
                    g.FillRectangle(bg, 0, 0, Width, Height);
                }
            }
            if (_active)
            {
                using (SolidBrush bar = new SolidBrush(Ui.Cyan))
                {
                    g.FillRectangle(bar, 0, 12, 3, Height - 24);
                }
            }

            Color glyphColor = _active ? Ui.Cyan : _hover ? Ui.TextHi : Ui.Text;
            if (Ui.HasGlyphs)
            {
                using (Font gf = Ui.Glyph(19f))
                {
                    TextRenderer.DrawText(g, _glyph, gf, new Rectangle(0, 7, Width, 26),
                        glyphColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
            TextRenderer.DrawText(g, _label.ToUpperInvariant(), new Font(Font, FontStyle.Bold),
                new Rectangle(0, 34, Width, 16),
                _active ? Ui.TextHi : Ui.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // The big Go Time / Eco Mode cards on Home: emblem, title, tagline, an
    // action hint pill, and - when this mode is the current one - a glowing
    // border with a pulsing ACTIVE badge.
    internal class ModeCard : Control
    {
        private bool _hover;
        private bool _active;
        private float _pulse;
        private readonly Timer _pulseTimer;

        public Image Emblem;
        public string CardTitle = "";
        public string Tagline = "";
        public string Hint = "";
        public Color Accent = Ui.Cyan;

        public ModeCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(330, 316);
            Cursor = Cursors.Hand;
            TabStop = true;
            BackColor = Ui.Bg;       // rounded-corner pixels blend into Home
            _pulseTimer = new Timer();
            _pulseTimer.Interval = 40;
            _pulseTimer.Tick += delegate
            {
                if (!_active || !Visible) { _pulseTimer.Stop(); return; }
                _pulse += 0.07f;
                if (_pulse > 1f) _pulse -= 1f;
                Invalidate();
            };
        }

        public bool CardActive
        {
            get { return _active; }
            set
            {
                _active = value;
                if (_active) _pulseTimer.Start(); else _pulseTimer.Stop();
                Invalidate();
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (_active && Visible) _pulseTimer.Start();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                OnClick(EventArgs.Empty);
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (GraphicsPath card = UiShapes.RoundRect(1, 1, Width - 2, Height - 2, 18))
            {
                using (SolidBrush bg = new SolidBrush(_hover ? Ui.Shift(Ui.Card, 0.05f) : Ui.Card))
                {
                    g.FillPath(bg, card);
                }

                if (_active)
                {
                    // pulsing outer glow + solid accent border
                    int alpha = 60 + (int)(50 * Math.Sin(_pulse * 2 * Math.PI));
                    alpha = Math.Max(30, Math.Min(160, alpha + 60));
                    using (Pen glow = new Pen(Color.FromArgb(alpha / 2, Accent), 6))
                    {
                        g.DrawPath(glow, UiShapes.RoundRect(4, 4, Width - 8, Height - 8, 15));
                    }
                    using (Pen border = new Pen(Accent, 1.8f)) g.DrawPath(border, card);
                }
                else
                {
                    using (Pen border = new Pen(_hover ? Ui.Shift(Ui.CardBorder, 0.25f) : Ui.CardBorder, 1.4f))
                    {
                        g.DrawPath(border, card);
                    }
                }
            }

            // emblem
            if (Emblem != null)
            {
                int es = 128;
                g.DrawImage(Emblem, new Rectangle((Width - es) / 2, 22, es, es));
            }

            // title + accent underline
            using (Font tf = new Font("Segoe UI", 16.5f, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, CardTitle, tf, new Rectangle(0, 158, Width, 34),
                    Ui.TextHi, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            using (SolidBrush bar = new SolidBrush(Accent))
            {
                g.FillRectangle(bar, Width / 2 - 20, 194, 40, 3);
            }
            if (Tagline.Length > 0)
            {
                TextRenderer.DrawText(g, Tagline, new Font("Segoe UI", 9f),
                    new Rectangle(18, 202, Width - 36, 40), Ui.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            }

            // action pill
            if (Hint.Length > 0)
            {
                using (Font hf = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                {
                    Size ts = TextRenderer.MeasureText(g, Hint, hf);
                    int pw = ts.Width + 26, ph = 24;
                    Rectangle pill = new Rectangle((Width - pw) / 2, Height - 44, pw, ph);
                    using (GraphicsPath pp = UiShapes.RoundRect(pill.X, pill.Y, pw, ph, 12))
                    {
                        using (SolidBrush pb = new SolidBrush(Color.FromArgb(38, Accent))) g.FillPath(pb, pp);
                        using (Pen ppen = new Pen(Color.FromArgb(130, Accent))) g.DrawPath(ppen, pp);
                    }
                    TextRenderer.DrawText(g, Hint, hf, pill, Ui.Shift(Accent, 0.45f),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }

            // ACTIVE badge
            if (_active)
            {
                using (Font bf = new Font("Segoe UI", 8f, FontStyle.Bold))
                {
                    string badge = "ACTIVE";
                    Size bs = TextRenderer.MeasureText(g, badge, bf);
                    int bw = bs.Width + 30, bh = 22;
                    Rectangle r = new Rectangle(Width - bw - 14, 14, bw, bh);
                    using (GraphicsPath bp = UiShapes.RoundRect(r.X, r.Y, bw, bh, 11))
                    {
                        using (SolidBrush bb = new SolidBrush(Color.FromArgb(46, Accent))) g.FillPath(bb, bp);
                    }
                    int dotAlpha = 140 + (int)(110 * Math.Sin(_pulse * 2 * Math.PI));
                    dotAlpha = Math.Max(80, Math.Min(255, dotAlpha));
                    using (SolidBrush dot = new SolidBrush(Color.FromArgb(dotAlpha, Accent)))
                    {
                        g.FillEllipse(dot, r.X + 9, r.Y + (bh - 8) / 2, 8, 8);
                    }
                    TextRenderer.DrawText(g, badge, bf,
                        new Rectangle(r.X + 16, r.Y, bw - 16, bh), Ui.TextHi,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _pulseTimer != null) _pulseTimer.Dispose();
            base.Dispose(disposing);
        }
    }

    // Rounded group container with a small accent-dot section title.
    internal class Card : Panel
    {
        public string CardTitle = "";
        public Color TitleAccent = Ui.Cyan;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;      // children blend onto the window bg; the card paints itself
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = UiShapes.RoundRect(0.5f, 0.5f, Width - 1, Height - 1, 16))
            {
                using (SolidBrush bg = new SolidBrush(Ui.Card)) g.FillPath(bg, p);
                using (Pen border = new Pen(Ui.CardBorder, 1.2f)) g.DrawPath(border, p);
            }
            if (CardTitle.Length > 0)
            {
                using (SolidBrush dot = new SolidBrush(TitleAccent))
                {
                    g.FillEllipse(dot, 22, 19, 7, 7);
                }
                TextRenderer.DrawText(g, CardTitle.ToUpperInvariant(),
                    new Font("Segoe UI", 9f, FontStyle.Bold),
                    new Rectangle(36, 10, Width - 48, 24), Ui.TextDim,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            }
        }
    }

    // Gradient call-to-action button (the GO button and friends).
    internal class AccentButton : Button
    {
        public Color From = Ui.Go;
        public Color To = Color.FromArgb(255, 61, 145);
        private bool _hover;
        private bool _down;

        public AccentButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(1, 1, Width - 2, Height - 2);
            using (GraphicsPath p = UiShapes.RoundRect(r.X, r.Y, r.Width, r.Height, 12))
            {
                Color c1 = _hover ? Ui.Shift(From, 0.10f) : From;
                Color c2 = _hover ? Ui.Shift(To, 0.10f) : To;
                if (_down) { c1 = Ui.Shift(c1, -0.15f); c2 = Ui.Shift(c2, -0.15f); }
                using (LinearGradientBrush lgb = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), c1, c2, 28f))
                {
                    g.FillPath(lgb, p);
                }
                if (_hover && Enabled)
                {
                    using (Pen glow = new Pen(Color.FromArgb(70, From), 4)) g.DrawPath(glow, p);
                }
            }
            Color tc = Enabled ? ForeColor : Color.FromArgb(170, 172, 180);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), tc,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // Two-color gradient heading text.
    internal class GradientLabel : Control
    {
        public Color From = Ui.Cyan;
        public Color To = Ui.Go;

        public GradientLabel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            // Opaque (the window base color): transparent-backed header
            // controls do not composite over opaque sibling panels.
            BackColor = Ui.Bg;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height));
            using (LinearGradientBrush lgb = new LinearGradientBrush(r, From, To, 0f))
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.Alignment = StringAlignment.Near;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                e.Graphics.DrawString(Text, Font, lgb, r, sf);
            }
        }
    }

    // Pill with a state dot + text (the live status chips in the header).
    internal class StatusChip : Control
    {
        private Color _dot = Ui.TextDim;
        private string _chipText = "";

        public StatusChip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;      // opaque - see GradientLabel note
            Size = new Size(150, 24);
            TabStop = false;
        }

        public void SetState(string text, Color dot)
        {
            _chipText = text == null ? "" : text;
            _dot = dot;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Size ts = TextRenderer.MeasureText(g, _chipText, Font);
            int w = ts.Width + 34, h = 24;
            using (GraphicsPath p = UiShapes.RoundRect(0.5f, 0.5f, w - 1, h - 1, 12))
            {
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(34, 38, 52))) g.FillPath(bg, p);
                using (Pen border = new Pen(Ui.CardBorder, 1f)) g.DrawPath(border, p);
            }
            using (SolidBrush dot = new SolidBrush(_dot)) g.FillEllipse(dot, 9, 8, 8, 8);
            TextRenderer.DrawText(g, _chipText, Font, new Rectangle(22, 0, w - 22, h),
                Ui.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }
    }

    // Rotating-arc busy spinner for the applying overlay.
    internal class SpinGlyph : Control
    {
        private float _angle;
        private Color _accent = Ui.Cyan;

        public SpinGlyph()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            Size = new Size(56, 56);
            TabStop = false;
        }

        public Color Accent { get { return _accent; } set { _accent = value; Invalidate(); } }

        public void Advance()
        {
            _angle += 21f;
            if (_angle >= 360f) _angle -= 360f;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(6, 6, Width - 12, Height - 12);
            using (Pen track = new Pen(Color.FromArgb(40, 46, 62), 5f))
            {
                g.DrawArc(track, r, 0, 360);
            }
            using (Pen arc = new Pen(_accent, 5f))
            {
                arc.StartCap = LineCap.Round;
                arc.EndCap = LineCap.Round;
                g.DrawArc(arc, r, _angle, 110f);
            }
        }
    }
}
