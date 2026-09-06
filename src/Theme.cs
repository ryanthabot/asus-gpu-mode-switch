//  Theme.cs  (v1.0.22)
//  -------------------
//  Shared UI primitives for the themed dark look: WindowIcons (applies the
//  exe's own icon to a form), UiShapes (rounded-rect GraphicsPath helper)
//  and ShimmerBar (indeterminate animated shimmer progress bar). Colors
//  stay inline at the call sites, exactly as in v1.0.22.
//
//  Split out of GpuModeSwitch.cs (Wave 2, zero behavior change).

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GpuModeSwitch
{
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
}
