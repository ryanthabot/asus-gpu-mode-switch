//  Theme.cs  (v1.2.0 / v1.2.3)
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
//
//  v1.2.3: the palette is runtime-mutable - every Ui color is a plain
//  static field the Theme page rewrites (ThemeState.ApplyUi) before the
//  next repaint. ThemeState (also here) holds the persisted theme
//  (%LOCALAPPDATA%\GpuModeSwitch\theme.txt, the same plain-text style as
//  the monitor interval), ThemeSwatch is the clickable swatch chip of the
//  Theme page and HeaderStripPanel paints the optional header gradient.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace GpuModeSwitch
{
    // The shared palette + small drawing helpers for the unified deck.
    // v1.2.3: every entry is a mutable static field - ThemeState.ApplyUi
    // rewrites them and every owner-drawn control picks the new values up on
    // repaint. The defaults below ARE the shipped Midnight look, so an
    // unthemed run renders exactly like v1.2.
    internal static class Ui
    {
        public static Color Bg = Color.FromArgb(13, 15, 21);          // window
        public static Color BgSide = Color.FromArgb(9, 11, 16);       // sidebar rail
        public static Color NavBack = Color.FromArgb(9, 11, 16);      // rail background (Theme page drives it; same as BgSide by default)
        public static Color Card = Color.FromArgb(20, 24, 34);        // card surface
        public static Color CardBorder = Color.FromArgb(41, 48, 66);
        public static Color TextHi = Color.FromArgb(238, 241, 248);
        public static Color Text = Color.FromArgb(190, 196, 210);
        public static Color TextDim = Color.FromArgb(122, 130, 150);
        public static Color Go = Color.FromArgb(255, 106, 61);        // Go Time orange
        public static Color Eco = Color.FromArgb(55, 214, 122);       // Eco green
        public static Color Cyan = Color.FromArgb(86, 199, 255);      // general accent
        public static Color Amber = Color.FromArgb(255, 184, 82);     // locked / warning
        public static Color Red = Color.FromArgb(255, 108, 108);

        // Profile bar surface (Optimize deck) - kept here so every surface
        // follows the active theme once its host reads them.
        public static Color ProfileBarBack = Color.FromArgb(24, 24, 28);
        public static Color ProfileBarText = Color.FromArgb(210, 210, 216);
        public static Color ProfileBarDim = Color.FromArgb(140, 140, 148);

        // Monitor deck surface (v1.2.3 sensor wave) - MonitorPanel reads
        // these; the defaults are the exact v1.2 hard-coded look.
        public static Color MonPanelBack = Color.FromArgb(24, 24, 28);
        public static Color MonBarTrack = Color.FromArgb(40, 40, 47);
        public static Color MonBarFill = Color.FromArgb(76, 195, 138);
        public static Color MonTextMain = Color.FromArgb(220, 220, 226);
        public static Color MonTextMuted = Color.FromArgb(150, 150, 158);

        // Header backdrop: GradEnabled=false paints the solid Ui.Bg (the
        // default - no visible gradient); true paints HeaderGradA ->
        // HeaderGradB, diagonally when GradDiagonal.
        public static Color HeaderGradA = Color.FromArgb(13, 15, 21);
        public static Color HeaderGradB = Color.FromArgb(13, 15, 21);
        public static bool GradEnabled;
        public static bool GradDiagonal;

        // Mode-card surface gradient hook (defaults = today's solid card).
        public static Color CardGradA = Color.FromArgb(20, 24, 34);
        public static Color CardGradB = Color.FromArgb(20, 24, 34);
        public static bool CardGradEnabled = false;

        // Popup surfaces (v1.2.3 popup restyle): the Session History / Log
        // Browser windows, the profile bar and their lists. Defaults are the
        // exact v1.1-v1.2 inline values, so an unthemed run renders identical.
        public static Color PopupBack = Color.FromArgb(24, 24, 28);
        public static Color PopupListBack = Color.FromArgb(14, 14, 16);
        public static Color PopupListText = Color.FromArgb(205, 205, 210);
        public static Color PopupListSelBack = Color.FromArgb(42, 42, 49);
        public static Color PopupTextDim = Color.FromArgb(140, 140, 148);
        public static Color PopupBorder = Color.FromArgb(90, 90, 98);
        public static Color ToolBtnFace = Color.FromArgb(45, 45, 52);

        // The one shared recipe for every small flat button in the popups
        // (session history, log browser, profile bar and their dialogs).
        // Visual only - callers keep their own Text/AutoSize/Click wiring.
        // Reads Ui live so a freshly constructed popup follows the theme.
        public static void StyleToolButton(Button b, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = primary ? Ui.Cyan : Ui.CardBorder;
            b.ForeColor = primary ? Ui.TextHi : Ui.ProfileBarText;
            b.BackColor = Ui.ToolBtnFace;
            b.Font = new Font("Segoe UI Semibold", 9f);
            b.Padding = new Padding(10, 5, 10, 5);
            b.Cursor = Cursors.Hand;
        }

        // ---- shared owner-drawn ListView painting (popup restyle) ---------
        // Wired as DrawColumnHeader / DrawItem / DrawSubItem handlers; the
        // header gets the profile-bar surface + semibold text, the rows keep
        // the dark list colors (selection = the subtle hover tone).

        public static void DrawListHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (SolidBrush back = new SolidBrush(Ui.ProfileBarBack))
            {
                e.Graphics.FillRectangle(back, e.Bounds);
            }
            using (Pen sep = new Pen(Ui.PopupBorder))
            {
                e.Graphics.DrawLine(sep, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom - 1);
                e.Graphics.DrawLine(sep, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right - 1, e.Bounds.Bottom - 1);
            }
            using (Font f = new Font("Segoe UI Semibold", 9f))
            {
                Rectangle textRect = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, f, textRect, Ui.ProfileBarText,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                    AlignFlags(e.Header.TextAlign));
            }
            e.DrawDefault = false;
        }

        public static void DrawListItem(object sender, DrawListViewItemEventArgs e)
        {
            using (SolidBrush back = new SolidBrush(
                (e.State & ListViewItemStates.Selected) != 0 ? Ui.PopupListSelBack : Ui.PopupListBack))
            {
                e.Graphics.FillRectangle(back, e.Bounds);
            }
            e.DrawDefault = false;
        }

        public static void DrawListCell(object sender, DrawListViewSubItemEventArgs e)
        {
            bool sel = (e.ItemState & ListViewItemStates.Selected) != 0;
            using (SolidBrush back = new SolidBrush(sel ? Ui.PopupListSelBack : Ui.PopupListBack))
            {
                e.Graphics.FillRectangle(back, e.Bounds);
            }
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            if (e.Header != null) flags |= AlignFlags(e.Header.TextAlign);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text,
                e.SubItem.Font != null ? e.SubItem.Font : e.Item.ListView.Font,
                new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height),
                Ui.PopupListText, flags);
            e.DrawDefault = false;
        }

        private static TextFormatFlags AlignFlags(HorizontalAlignment align)
        {
            switch (align)
            {
                case HorizontalAlignment.Center: return TextFormatFlags.HorizontalCenter;
                case HorizontalAlignment.Right: return TextFormatFlags.Right;
                default: return TextFormatFlags.Left;
            }
        }

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

    // Dark native chrome for the popup forms (v1.2.3 restyle): flips the
    // DWM immersive-dark-mode attribute so the native title bar matches the
    // dark body. Attribute 20 is the current number; Windows builds older
    // than ~2004 only accept 19, so that is the fallback. Dark scrollbars
    // have no documented registry-free path on .NET 4, so they stay native.
    internal static class DarkChrome
    {
        private const int AttrDarkModeOld = 19;
        private const int AttrDarkMode = 20;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Best-effort: call in the form constructor (before the handle
        // exists it hooks Load instead) - a failure just keeps the light bar.
        public static void Apply(Form form)
        {
            if (form == null || form.IsDisposed) return;
            if (form.IsHandleCreated)
            {
                TrySetDark(form.Handle);
            }
            else
            {
                form.Load += delegate { if (!form.IsDisposed) TrySetDark(form.Handle); };
            }
        }

        private static void TrySetDark(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, AttrDarkMode, ref on, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, AttrDarkModeOld, ref on, sizeof(int));
                }
            }
            catch
            {
                // DWM unavailable (older Windows / session state) - cosmetic only
            }
        }
    }

    // Borderless chrome for the popup forms (v1.2.3 popup upgrade): the
    // exact pattern the main window has used since v1.2.0, factored out
    // so the Session History / Log Browser windows can share it instead
    // of duplicating the P/Invokes and hit-test math per form.
    internal static class PopupChrome
    {
        private const int WmNcHitTest = 0x84;
        private const int HtClient = 1;
        private const int HtLeft = 10;
        private const int HtRight = 11;
        private const int HtTop = 12;
        private const int HtTopLeft = 13;
        private const int HtTopRight = 14;
        private const int HtBottom = 15;
        private const int HtBottomLeft = 16;
        private const int HtBottomRight = 17;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // Wires nothing by itself - this is the recipe in one place
        // (the popup forms follow it in their constructors):
        //   ctor:     FormBorderStyle.None + ShowInTaskbar + BackColor,
        //             WindowIcons.Apply, PopupChrome.Round(this)
        //   WndProc:  base.WndProc(ref m);
        //             PopupChrome.HitTestEdge(this, ref m);
        //   OnResize: base.OnResize(e); PopupChrome.Round(this);
        //   header:   PopupChrome.MakeDraggable(headerPanel, this) +
        //             MakeCaptionButton pairs, positioned Width-76/Width-40
        public static void Enable(Form f)
        {
            // intentionally empty: every form wires its own pieces above
        }

        // WM_NCHITTEST edge mapping for a borderless form (the main
        // window's WndProc logic). Call from the form's WndProc AFTER
        // base.WndProc(ref m): when the message is WM_NCHITTEST and the
        // base hit test said "client" while the point sits in one of the
        // 8px edge/corner bands, the result is rewritten to the matching
        // HT* resize zone. Skipped while maximized - a maximized window
        // does not edge-resize, like the native caption the popups used
        // to have. Returns true when the message was handled.
        public static bool HitTestEdge(Form f, ref Message m)
        {
            if (f == null || m.Msg != WmNcHitTest) return false;
            if (f.WindowState == FormWindowState.Maximized) return true;
            if ((int)m.Result == HtClient)
            {
                int lp = m.LParam.ToInt32();
                Point pt = f.PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int e = 8;
                bool l = pt.X <= e, r = pt.X >= f.ClientSize.Width - e;
                bool t = pt.Y <= e, b = pt.Y >= f.ClientSize.Height - e;
                if (t && l) m.Result = (IntPtr)HtTopLeft;
                else if (t && r) m.Result = (IntPtr)HtTopRight;
                else if (b && l) m.Result = (IntPtr)HtBottomLeft;
                else if (b && r) m.Result = (IntPtr)HtBottomRight;
                else if (l) m.Result = (IntPtr)HtLeft;
                else if (r) m.Result = (IntPtr)HtRight;
                else if (t) m.Result = (IntPtr)HtTop;
                else if (b) m.Result = (IntPtr)HtBottom;
            }
            return true;
        }

        // Applies the 22px rounded window region for the CURRENT client
        // size (ctor + OnResize) - the main window's exact corner radius.
        public static void Round(Form f)
        {
            if (f == null) return;
            Region old = f.Region;
            f.Region = new Region(UiShapes.RoundRect(0, 0, f.ClientSize.Width, f.ClientSize.Height, 22));
            if (old != null) old.Dispose();
        }

        // Bare-panel drag handle (the main window's MakeDraggable):
        // press with the left button anywhere on the chrome surface and
        // the window follows, exactly like a native title bar.
        public static void MakeDraggable(Control c, Form f)
        {
            if (c == null || f == null) return;
            c.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(f.Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero);   // WM_NCLBUTTONDOWN, HTCAPTION
                }
            };
        }

        // One — / ✕ caption button, styled like the main window's pair:
        // flat, borderless, dim Segoe UI 10f glyph on the header surface.
        // Visual only - the caller wires its own Click action.
        public static void MakeCaptionButton(Button b, string glyph)
        {
            b.Text = glyph;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.ForeColor = Ui.TextDim;
            b.BackColor = Ui.Bg;
            b.Font = new Font("Segoe UI", 10f);
            b.Size = new Size(34, 28);
            b.TabStop = false;
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

        // Raised after Checked flipped (click or Space/Enter) - the Theme
        // page's gradient switch applies the new state live through it.
        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                StartAnim();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
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
            BackColor = Ui.NavBack;   // the rail paints itself; corners must blend
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

            // full rail-color base so a theme change lands on repaint
            using (SolidBrush rail = new SolidBrush(Ui.NavBack))
            {
                g.FillRectangle(rail, 0, 0, Width, Height);
            }
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

    // ---------------------------------------------------------------------
    // v1.2.3 theme system
    // ---------------------------------------------------------------------

    // Clickable swatch chip (Theme page): rounded preview of one color with
    // a caption and an accent underline; a 2px accent border marks the
    // selected preset. Raises Click through the normal Control event.
    internal class ThemeSwatch : Control
    {
        private bool _hover;
        private bool _selected;

        public Color Preview = Color.FromArgb(20, 24, 34);
        public Color SwatchAccent = Ui.Cyan;

        public ThemeSwatch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            Size = new Size(96, 54);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public bool Selected
        {
            get { return _selected; }
            set { _selected = value; Invalidate(); }
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
            using (GraphicsPath p = UiShapes.RoundRect(0.5f, 0.5f, Width - 1, Height - 1, 10))
            {
                using (SolidBrush bg = new SolidBrush(_hover ? Ui.Shift(Preview, 0.08f) : Preview))
                {
                    g.FillPath(bg, p);
                }
                using (Pen border = new Pen(_selected ? SwatchAccent : Ui.CardBorder, _selected ? 2f : 1.2f))
                {
                    g.DrawPath(border, p);
                }
            }
            using (SolidBrush bar = new SolidBrush(SwatchAccent))
            {
                g.FillRectangle(bar, 12, Height - 13, Width - 24, 3);
            }
            // caption contrast follows the preview luminance (light swatches
            // like Frost's ice blue need dark text)
            int lum = (Preview.R * 30 + Preview.G * 59 + Preview.B * 11) / 100;
            Color cap = lum >= 128 ? Color.FromArgb(20, 24, 34) : Ui.TextHi;
            TextRenderer.DrawText(g, Text, new Font("Segoe UI", 8.5f, FontStyle.Bold),
                new Rectangle(2, 0, Width - 4, Height - 16), cap,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // Header backdrop (v1.2.3): the strip behind the title/chips paints the
    // user's two-color gradient when Ui.GradEnabled is on, else the solid
    // Ui.Bg. Fully UserPaint (like Card / NavButton) so BOTH the direct
    // paints and the transparent-label composites go through this code -
    // a stock Panel's native background fill would patch solid BackColor
    // over the gradient behind the header labels (see the GradientLabel
    // note about opaque compositing).
    internal class HeaderStripPanel : Panel
    {
        public HeaderStripPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // full custom fill; the base fill would flood the BackColor
            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height));
            if (Ui.GradEnabled)
            {
                using (LinearGradientBrush lgb = new LinearGradientBrush(r, Ui.HeaderGradA, Ui.HeaderGradB,
                    Ui.GradDiagonal ? LinearGradientMode.ForwardDiagonal : LinearGradientMode.Horizontal))
                {
                    e.Graphics.FillRectangle(lgb, r);
                }
            }
            else
            {
                using (SolidBrush b = new SolidBrush(Ui.Bg)) e.Graphics.FillRectangle(b, r);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // the background is all there is to paint
        }
    }

    // One named palette. Midnight is the exact v1.2 default; the others are
    // the built-in alternates the Theme page offers as swatches. Go / Eco /
    // Amber / Red keep their semantic meaning in every preset - only the
    // accent (preset Cyan) and the neutrals change identity.
    internal sealed class ThemePreset
    {
        public string Name;
        public Color Bg, BgSide, Card, CardBorder, TextHi, Text, TextDim, Go, Eco, Cyan, Amber, Red;
    }

    // Persisted theme state (v1.2.3): the active palette (from a preset or
    // custom), the accent, the header gradient settings and the nav rail
    // color. Saved as plain "key=value" lines in
    // %LOCALAPPDATA%\GpuModeSwitch\theme.txt (the same best-effort style as
    // the monitor interval) and applied to Ui BEFORE any UI is constructed,
    // so a themed run never flashes the default palette first.
    internal static class ThemeState
    {
        private static readonly string ThemeFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GpuModeSwitch", "theme.txt");

        public static readonly ThemePreset[] Presets = new ThemePreset[]
        {
            new ThemePreset
            {
                Name = "Midnight", Bg = Color.FromArgb(13, 15, 21), BgSide = Color.FromArgb(9, 11, 16),
                Card = Color.FromArgb(20, 24, 34), CardBorder = Color.FromArgb(41, 48, 66),
                TextHi = Color.FromArgb(238, 241, 248), Text = Color.FromArgb(190, 196, 210), TextDim = Color.FromArgb(122, 130, 150),
                Go = Color.FromArgb(255, 106, 61), Eco = Color.FromArgb(55, 214, 122),
                Cyan = Color.FromArgb(86, 199, 255), Amber = Color.FromArgb(255, 184, 82), Red = Color.FromArgb(255, 108, 108)
            },
            new ThemePreset
            {
                Name = "Carbon", Bg = Color.FromArgb(22, 21, 20), BgSide = Color.FromArgb(17, 16, 15),
                Card = Color.FromArgb(30, 29, 27), CardBorder = Color.FromArgb(58, 55, 50),
                TextHi = Color.FromArgb(240, 238, 233), Text = Color.FromArgb(198, 195, 188), TextDim = Color.FromArgb(130, 127, 120),
                Go = Color.FromArgb(245, 130, 70), Eco = Color.FromArgb(74, 200, 116),
                Cyan = Color.FromArgb(98, 206, 128), Amber = Color.FromArgb(255, 192, 104), Red = Color.FromArgb(255, 112, 106)
            },
            new ThemePreset
            {
                Name = "Ocean", Bg = Color.FromArgb(8, 19, 32), BgSide = Color.FromArgb(6, 15, 26),
                Card = Color.FromArgb(13, 29, 47), CardBorder = Color.FromArgb(30, 56, 82),
                TextHi = Color.FromArgb(234, 244, 252), Text = Color.FromArgb(184, 203, 222), TextDim = Color.FromArgb(116, 139, 163),
                Go = Color.FromArgb(255, 148, 92), Eco = Color.FromArgb(74, 212, 150),
                Cyan = Color.FromArgb(64, 222, 208), Amber = Color.FromArgb(255, 196, 112), Red = Color.FromArgb(255, 120, 120)
            },
            new ThemePreset
            {
                Name = "Ember", Bg = Color.FromArgb(25, 20, 17), BgSide = Color.FromArgb(19, 15, 12),
                Card = Color.FromArgb(35, 28, 23), CardBorder = Color.FromArgb(64, 51, 42),
                TextHi = Color.FromArgb(250, 242, 233), Text = Color.FromArgb(209, 197, 185), TextDim = Color.FromArgb(139, 127, 116),
                Go = Color.FromArgb(255, 122, 64), Eco = Color.FromArgb(98, 206, 134),
                Cyan = Color.FromArgb(255, 166, 74), Amber = Color.FromArgb(255, 190, 100), Red = Color.FromArgb(255, 110, 104)
            },
            new ThemePreset
            {
                Name = "Frost", Bg = Color.FromArgb(31, 37, 47), BgSide = Color.FromArgb(25, 30, 39),
                Card = Color.FromArgb(41, 48, 60), CardBorder = Color.FromArgb(70, 80, 96),
                TextHi = Color.FromArgb(245, 249, 255), Text = Color.FromArgb(205, 213, 225), TextDim = Color.FromArgb(134, 144, 160),
                Go = Color.FromArgb(255, 128, 86), Eco = Color.FromArgb(98, 214, 146),
                Cyan = Color.FromArgb(158, 216, 255), Amber = Color.FromArgb(255, 198, 114), Red = Color.FromArgb(255, 118, 118)
            }
        };

        public static string PresetName = "Midnight";
        public static Color Bg, BgSide, Card, CardBorder, TextHi, Text, TextDim, Go, Eco, Amber, Red;
        public static Color Accent;        // the general accent (applies to Ui.Cyan)
        public static Color NavColor;      // rail background
        public static bool GradOn;
        public static bool GradDiagonal;
        public static Color GradFrom, GradTo;

        static ThemeState()
        {
            ResetToDefault();
        }

        public static ThemePreset FindPreset(string name)
        {
            foreach (ThemePreset p in Presets)
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        // Copies a built-in palette into the state; the accent and the nav
        // rail color reset with it (a preset is a full look). Gradient
        // settings are the user's own and stay untouched.
        public static void ApplyPreset(string name)
        {
            ThemePreset p = FindPreset(name);
            if (p == null) return;
            PresetName = p.Name;
            Bg = p.Bg; BgSide = p.BgSide; Card = p.Card; CardBorder = p.CardBorder;
            TextHi = p.TextHi; Text = p.Text; TextDim = p.TextDim;
            Go = p.Go; Eco = p.Eco; Amber = p.Amber; Red = p.Red;
            Accent = p.Cyan;
            NavColor = p.BgSide;
        }

        // Full reset: the shipped Midnight look with no gradient.
        public static void ResetToDefault()
        {
            ApplyPreset("Midnight");
            GradOn = false;
            GradDiagonal = false;
            GradFrom = Bg;
            GradTo = Bg;
        }

        // Pushes the state into the live palette. Called at startup (before
        // any UI exists) and by MainForm.ApplyTheme on every live change.
        public static void ApplyUi()
        {
            Ui.Bg = Bg;
            Ui.BgSide = NavColor;      // the rail is one surface: BgSide follows NavBack
            Ui.NavBack = NavColor;
            Ui.Card = Card; Ui.CardBorder = CardBorder;
            Ui.TextHi = TextHi; Ui.Text = Text; Ui.TextDim = TextDim;
            Ui.Go = Go; Ui.Eco = Eco; Ui.Cyan = Accent; Ui.Amber = Amber; Ui.Red = Red;
            Ui.HeaderGradA = GradFrom; Ui.HeaderGradB = GradTo;
            Ui.GradEnabled = GradOn; Ui.GradDiagonal = GradDiagonal;
        }

        // ---- persistence (plain key=value lines, best-effort like the
        // monitor interval file) ------------------------------------------

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ThemeFile));
                StringBuilder sb = new StringBuilder();
                sb.Append("preset=").Append(PresetName).AppendLine();
                AppendColor(sb, "bg", Bg);
                AppendColor(sb, "bgside", BgSide);
                AppendColor(sb, "card", Card);
                AppendColor(sb, "cardborder", CardBorder);
                AppendColor(sb, "texthi", TextHi);
                AppendColor(sb, "text", Text);
                AppendColor(sb, "textdim", TextDim);
                AppendColor(sb, "go", Go);
                AppendColor(sb, "eco", Eco);
                AppendColor(sb, "cyan", Accent);
                AppendColor(sb, "amber", Amber);
                AppendColor(sb, "red", Red);
                AppendColor(sb, "nav", NavColor);
                AppendColor(sb, "gradfrom", GradFrom);
                AppendColor(sb, "gradto", GradTo);
                sb.Append("grad=").Append(GradOn ? 1 : 0).AppendLine();
                sb.Append("graddiag=").Append(GradDiagonal ? 1 : 0).AppendLine();
                File.WriteAllText(ThemeFile, sb.ToString());
            }
            catch { }   // persistence is best-effort; the live theme still applies
        }

        // Reads theme.txt into the state and applies it to Ui. A missing or
        // unreadable file leaves the shipped defaults. A known preset name
        // seeds the palette first; the stored palette keys then win (they are
        // the full truth for a "Custom" theme).
        public static void Load()
        {
            try
            {
                if (File.Exists(ThemeFile))
                {
                    Dictionary<string, string> kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string raw in File.ReadAllLines(ThemeFile))
                    {
                        string line = raw == null ? "" : raw.Trim();
                        if (line.Length == 0) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }

                    string preset;
                    if (!kv.TryGetValue("preset", out preset)) preset = "Midnight";
                    PresetName = preset;                       // "Custom" (or unknown) survives the round trip
                    if (FindPreset(preset) != null) ApplyPreset(preset);

                    Color c;
                    if (TryReadColor(kv, "bg", out c)) Bg = c;
                    if (TryReadColor(kv, "bgside", out c)) BgSide = c;
                    if (TryReadColor(kv, "card", out c)) Card = c;
                    if (TryReadColor(kv, "cardborder", out c)) CardBorder = c;
                    if (TryReadColor(kv, "texthi", out c)) TextHi = c;
                    if (TryReadColor(kv, "text", out c)) Text = c;
                    if (TryReadColor(kv, "textdim", out c)) TextDim = c;
                    if (TryReadColor(kv, "go", out c)) Go = c;
                    if (TryReadColor(kv, "eco", out c)) Eco = c;
                    if (TryReadColor(kv, "cyan", out c)) Accent = c;
                    if (TryReadColor(kv, "amber", out c)) Amber = c;
                    if (TryReadColor(kv, "red", out c)) Red = c;
                    if (TryReadColor(kv, "nav", out c)) NavColor = c;
                    if (TryReadColor(kv, "gradfrom", out c)) GradFrom = c;
                    if (TryReadColor(kv, "gradto", out c)) GradTo = c;
                    int flag;
                    if (TryReadFlag(kv, "grad", out flag)) GradOn = flag != 0;
                    if (TryReadFlag(kv, "graddiag", out flag)) GradDiagonal = flag != 0;
                }
            }
            catch { }   // corrupt file -> shipped defaults
            ApplyUi();
        }

        private static void AppendColor(StringBuilder sb, string key, Color c)
        {
            sb.Append(key).Append('=').Append(c.R).Append(',').Append(c.G).Append(',').Append(c.B).AppendLine();
        }

        // "R,G,B" (the format AppendColor writes); false on anything else.
        private static bool TryReadColor(Dictionary<string, string> kv, string key, out Color c)
        {
            c = Color.Black;
            string v;
            if (!kv.TryGetValue(key, out v)) return false;
            string[] parts = v.Split(',');
            if (parts.Length != 3) return false;
            int r, g, b;
            if (!int.TryParse(parts[0].Trim(), out r)) return false;
            if (!int.TryParse(parts[1].Trim(), out g)) return false;
            if (!int.TryParse(parts[2].Trim(), out b)) return false;
            c = Color.FromArgb(Clamp255(r), Clamp255(g), Clamp255(b));
            return true;
        }

        private static bool TryReadFlag(Dictionary<string, string> kv, string key, out int v)
        {
            v = 0;
            string s;
            return kv.TryGetValue(key, out s) && int.TryParse(s, out v);
        }

        private static int Clamp255(int v)
        {
            return Math.Max(0, Math.Min(255, v));
        }
    }
}
