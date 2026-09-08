//  TrayIcon.cs  (v1.1.0 - Wave 5, agent A17)
//  ------------------------------------------
//  Session-only tray icon (D4): a NotifyIcon created entirely in code (the
//  icon is drawn into a 16x16 Bitmap - rounded square in the suite's accent
//  green with a "G" glyph; no asset files) that lives only while this
//  process runs. No autostart, no persistence, nothing written to disk; the
//  host shows it after GO applies and hides it on restore/exit (Hide() and
//  Dispose() both remove the icon from the tray).
//
//  Compiled into both exe targets like every src\*.cs file - no #if is used
//  or needed: the class is inert until instantiated, and only Go Time's
//  Wave 6 code (A18) instantiates it, so its fixed strings are Go
//  Time-branded ("Open Go Time"). Eco Mode never creates one.
//
//  Fully decoupled from the UI: the five host callbacks are injected through
//  the constructor (openWindow, applyEco, toggleOverlay, statusText,
//  exitApp). A null callback only disables its menu item - nothing throws.
//  This file references no Forms.cs types (only Log, UiShapes and the
//  framework).
//
//  Menu: Open Go Time / - / Restore (Eco Mode) / Toggle overlay / Status
//  (balloon tip showing statusText()) / - / Exit. Double-click on the icon
//  = openWindow. The ContextMenuStrip is dark-themed with the same inline
//  palette as the rest of the suite.
//
//  Log contract (channel TRAY): "tray: shown" / "tray: hidden" /
//  "tray: status balloon" / "tray: exit requested", plus one
//  "tray: <action> requested" line per menu invocation.
//
//  C# 5 only (see docs\HANDBOOK.md section 2). New in v1.1.0 Wave 5 per
//  docs\HANDBOOK.md section 4 (D4) and section 8.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GpuModeSwitch
{
    // -----------------------------------------------------------------
    // Session tray: NotifyIcon + dark ContextMenuStrip, callbacks injected.
    // Every callback invocation is logged and try/caught - a throwing
    // callback becomes a Log.Error, never an app crash.
    // -----------------------------------------------------------------
    public class SessionTray : IDisposable
    {
        // Dark palette - same inline values as the rest of the UI (Theme.cs
        // keeps colors at the call sites). Accent = the suite-wide green
        // used by ShimmerBar/MonitorPanel; the icon square uses it.
        private static readonly Color TextMain = Color.FromArgb(220, 220, 226);
        private static readonly Color Accent = Color.FromArgb(76, 195, 138);

        private const string BalloonTitle = "Go Time";
        private const string DefaultTip = "Go Time - GPU mode switch";

        private readonly Action _openWindow;
        private readonly Action _applyEco;
        private readonly Action _toggleOverlay;
        private readonly Func<string> _statusText;
        private readonly Action _exitApp;
        private readonly NotifyIcon _notify = new NotifyIcon();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();

        private Icon _icon;          // wrapper around _iconHandle (or a system fallback)
        private IntPtr _iconHandle;  // HICON from Bitmap.GetHicon - freed in Dispose
        private bool _ownsIcon;
        private bool _disposed;

        public SessionTray(Action openWindow, Action applyEco, Action toggleOverlay,
                           Func<string> statusText, Action exitApp)
        {
            _openWindow = openWindow;
            _applyEco = applyEco;
            _toggleOverlay = toggleOverlay;
            _statusText = statusText;
            _exitApp = exitApp;

            BuildMenu();

            _icon = BuildIcon();
            if (_icon != null)
            {
                _ownsIcon = true;
            }
            else
            {
                _icon = SystemIcons.Application;   // shell fallback - never disposed
                _iconHandle = IntPtr.Zero;
            }

            _notify.Icon = _icon;
            _notify.Text = DefaultTip;
            _notify.ContextMenuStrip = _menu;
            _notify.Visible = false;               // nothing in the tray until Show()
            _notify.MouseDoubleClick += delegate { RunCallback("open (double-click)", _openWindow); };
        }

        // ---- menu ------------------------------------------------------------

        private void BuildMenu()
        {
            _menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            _menu.ShowImageMargin = false;
            _menu.Items.Add(MakeItem("Open GPU Mode Switch", _openWindow, "open"));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(MakeItem("Go Eco (switch + restore)", _applyEco, "go eco"));
            _menu.Items.Add(MakeItem("Toggle overlay", _toggleOverlay, "overlay toggle"));
            _menu.Items.Add(MakeStatusItem());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(MakeItem("Exit", _exitApp, "exit"));
        }

        // A null callback -> disabled item, no throw.
        private ToolStripMenuItem MakeItem(string text, Action callback, string logTag)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.ForeColor = TextMain;
            it.Enabled = callback != null;
            if (callback != null)
            {
                it.Click += delegate { RunCallback(logTag, callback); };
            }
            return it;
        }

        private ToolStripMenuItem MakeStatusItem()
        {
            ToolStripMenuItem it = new ToolStripMenuItem("Status");
            it.ForeColor = TextMain;
            it.Enabled = _statusText != null;
            if (it.Enabled)
            {
                it.Click += delegate { ShowStatusBalloon(); };
            }
            return it;
        }

        private void RunCallback(string logTag, Action callback)
        {
            Log.Chan("TRAY", "tray: " + logTag + " requested");
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                Log.Error("tray: " + logTag + " callback failed", ex);
            }
        }

        private void ShowStatusBalloon()
        {
            string text;
            try
            {
                text = _statusText();
            }
            catch (Exception ex)
            {
                text = "status unavailable: " + ex.Message;
                Log.Error("tray: status callback failed", ex);
            }
            if (string.IsNullOrEmpty(text))
            {
                text = "(no status text)";
            }
            SetTooltip(text);
            Log.Chan("TRAY", "tray: status balloon");
            ShowBalloon(text);
        }

        // ---- public API ------------------------------------------------------

        // Puts the icon in the tray; a non-empty initialTip also pops one
        // balloon (e.g. "Go Time applied - Standard mode").
        public void Show(string initialTip)
        {
            _notify.Visible = true;
            Log.Chan("TRAY", "tray: shown");
            if (!string.IsNullOrEmpty(initialTip))
            {
                SetTooltip(initialTip);
                ShowBalloon(initialTip);
            }
        }

        public void Hide()
        {
            _notify.Visible = false;
            Log.Chan("TRAY", "tray: hidden");
        }

        // Pops a balloon with the given status and keeps the hover tooltip
        // in sync. Empty text is a logged no-op.
        public void SetStatus(string status)
        {
            if (string.IsNullOrEmpty(status))
            {
                return;
            }
            SetTooltip(status);
            Log.Chan("TRAY", "tray: status balloon");
            ShowBalloon(status);
        }

        // Removes the icon and frees the menu + drawn icon. Safe to call
        // twice; the host calls this on restore/exit (D4).
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try { _notify.Visible = false; } catch { }
            try { _notify.Dispose(); } catch { }     // removes the tray icon
            try { _menu.Dispose(); } catch { }
            if (_ownsIcon)
            {
                try { _icon.Dispose(); } catch { }   // wrapper only - does not free the HICON
                if (_iconHandle != IntPtr.Zero)
                {
                    try { DestroyIcon(_iconHandle); } catch { }
                }
            }
            _icon = null;
            _iconHandle = IntPtr.Zero;
            GC.SuppressFinalize(this);
            Log.Chan("TRAY", "tray: disposed");
        }

        // ---- internals -------------------------------------------------------

        private void ShowBalloon(string text)
        {
            try
            {
                _notify.ShowBalloonTip(4000, BalloonTitle, text, ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Log.Warn("tray: balloon failed (" + ex.Message + ")");
            }
        }

        // NotifyIcon.Text throws past 63 chars on .NET 4.x - truncate.
        private void SetTooltip(string text)
        {
            try
            {
                string t = text.Trim();
                if (t.Length == 0)
                {
                    return;
                }
                if (t.Length > 63)
                {
                    t = t.Substring(0, 63);
                }
                _notify.Text = t;
            }
            catch
            {
                // tooltip is cosmetic
            }
        }

        // Draws the icon in code: accent-green rounded square + white "G".
        // GetHicon yields an independent HICON, so the Bitmap can go away
        // immediately; Icon.FromHandle only wraps it - Dispose destroys the
        // handle with DestroyIcon (the Icon wrapper does not own it).
        private Icon BuildIcon()
        {
            try
            {
                using (Bitmap bmp = new Bitmap(16, 16))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                        using (GraphicsPath rr = UiShapes.RoundRect(0.5f, 0.5f, 15f, 15f, 3.5f))
                        using (SolidBrush fill = new SolidBrush(Accent))
                        {
                            g.FillPath(fill, rr);
                        }

                        using (Font f = new Font("Segoe UI", 9f, FontStyle.Bold))
                        using (StringFormat sf = new StringFormat())
                        {
                            sf.Alignment = StringAlignment.Center;
                            sf.LineAlignment = StringAlignment.Center;
                            g.DrawString("G", f, Brushes.White, new RectangleF(0f, -1f, 16f, 17f), sf);
                        }
                    }

                    _iconHandle = bmp.GetHicon();
                }

                return Icon.FromHandle(_iconHandle);
            }
            catch
            {
                if (_iconHandle != IntPtr.Zero)
                {
                    try { DestroyIcon(_iconHandle); } catch { }
                    _iconHandle = IntPtr.Zero;
                }
                return null;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // Dark ContextMenuStrip colors - same inline palette as the forms.
        // Only the surfaces the drop-down actually paints are overridden.
        private sealed class DarkMenuColors : ProfessionalColorTable
        {
            private static readonly Color Back = Color.FromArgb(24, 24, 28);
            private static readonly Color Border = Color.FromArgb(90, 90, 98);
            private static readonly Color Hover = Color.FromArgb(42, 42, 49);

            public override Color ToolStripDropDownBackground { get { return Back; } }
            public override Color ToolStripBorder { get { return Back; } }
            public override Color ImageMarginGradientBegin { get { return Back; } }
            public override Color ImageMarginGradientMiddle { get { return Back; } }
            public override Color ImageMarginGradientEnd { get { return Back; } }
            public override Color MenuBorder { get { return Border; } }
            public override Color MenuItemBorder { get { return Back; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Color SeparatorDark { get { return Border; } }
            public override Color SeparatorLight { get { return Back; } }
        }
    }
}
