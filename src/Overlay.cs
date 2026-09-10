//  Overlay.cs  (v1.1.0 - Wave 5, agent A17)
//  -----------------------------------------
//  Compact gaming overlay over the monitor engine (D6): a small frameless,
//  always-on-top, semi-transparent window showing CPU / RAM / Disk / dGPU /
//  dGPU temp readings from MonitorEngine (src\SystemMonitor.cs), plus an
//  iGPU row that appears only once the engine proves the iGPU phys mapping
//  (v1.2.3 - the old "GPU" rows are labeled "dGPU" now that the two
//  adapters are separate metrics). Per D6 the monitor
//  only samples while the host wants it - the overlay never starts or stops
//  the engine itself; while the engine is off the overlay shows a "monitor
//  off" hint and keeps the last painted values.
//
//  Contract for the Wave 6 host (A18): construct once, call Attach() when
//  the monitor starts and Detach() when it stops (both idempotent; Dispose
//  detaches on its own), Toggle() to flip visibility. Attach() paints
//  MonitorEngine.LastSample immediately so a re-attach never shows blanks.
//  Position persistence is deliberately not implemented (keep it simple);
//  the first show lands at the bottom-right of the primary working area and
//  the window is click-draggable from anywhere on its surface.
//
//  MonitorEngine samples on a System.Windows.Forms.Timer, so SampleReady
//  normally arrives on the UI thread; the handler still guards with
//  InvokeRequired so a manual RunOnce from a worker thread cannot cross
//  threads (same pattern as MonitorPanel).
//
//  Compiled into both exe targets; wired by A18 (Wave 6). Log contract
//  (channel MONITOR): "overlay: shown" / "overlay: hidden" on any visibility
//  change, "overlay: toggled (...)" from Toggle(), and "overlay: attached to
//  monitor engine" / "overlay: detached from monitor engine" on attach and
//  detach.
//
//  C# 5 only (see docs\HANDBOOK.md section 2). New in v1.1.0 Wave 5 per
//  docs\HANDBOOK.md section 4 (D4/D6) and section 8.

using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace GpuModeSwitch
{
    // -----------------------------------------------------------------
    // Frameless always-on-top overlay: a compact grid of labels (Consolas
    // 9f) bound to MonitorEngine.SampleReady / LastSample. Click-drag
    // anywhere moves it (classic MouseDown/MouseMove with mouse capture;
    // every child label forwards its MouseDown so the whole surface drags).
    // -----------------------------------------------------------------
    public class MonitorOverlayForm : Form
    {
        // Dark palette - same inline values as the rest of the UI (Theme.cs
        // keeps colors at the call sites; MainForm / MonitorPanel values).
        private static readonly Color Back = Color.FromArgb(22, 22, 26);
        private static readonly Color TextMain = Color.FromArgb(220, 220, 226);
        private static readonly Color TextMuted = Color.FromArgb(150, 150, 158);

        private readonly TableLayoutPanel _grid = new TableLayoutPanel();
        private readonly Label[] _names = new Label[6];
        private readonly Label[] _values = new Label[6];
        private readonly Label _footer = new Label();
        private readonly Label _hint = new Label();
        private readonly System.Windows.Forms.Timer _hintTimer = new System.Windows.Forms.Timer();
        private readonly Font _mono = new Font("Consolas", 9f);
        private bool _attached;

        // Click-drag state (screen coordinates, captured on MouseDown).
        private bool _dragging;
        private Point _dragCursor;
        private Point _dragFormPos;

        public MonitorOverlayForm()
        {
            Text = "GPU monitor";
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Back;
            ClientSize = new Size(260, 120);
            Region = new Region(UiShapes.RoundRect(0, 0, ClientSize.Width, ClientSize.Height, 10));
            PlaceDefault();

            // Deterministic TableLayoutPanel grid (LogForm pattern): metric
            // name + value pairs, two per row, over a near-black background.
            _grid.Dock = DockStyle.Fill;
            _grid.ColumnCount = 5;
            _grid.RowCount = 4;
            _grid.Padding = new Padding(10, 8, 10, 2);
            _grid.BackColor = Back;
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // name
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // value
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // name
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // value
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // filler
            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));       // filler

            // _values index order: 0 CPU, 1 RAM, 2 Disk, 3 dGPU util, 4 dGPU
            // temp, 5 iGPU util. The iGPU pair is only added to the grid once
            // a sample proves HasIgpu (the footer slides into the filler row
            // meanwhile) - the overlay stays compact either way.
            string[] names = { "CPU", "RAM", "Disk", "dGPU", "dGPU temp", "iGPU" };
            for (int i = 0; i < 6; i++)
            {
                _names[i] = MakeLabel(names[i], TextMuted, 4);
                _values[i] = MakeLabel("--", TextMain, 12);
            }

            // Row 0: CPU + RAM. Row 1: Disk + dGPU. Row 2: dGPU temp + (iGPU
            // or footer). Row 3 (filler): the footer while iGPU is shown.
            _grid.Controls.Add(_names[0], 0, 0);
            _grid.Controls.Add(_values[0], 1, 0);
            _grid.Controls.Add(_names[1], 2, 0);
            _grid.Controls.Add(_values[1], 3, 0);
            _grid.Controls.Add(_names[2], 0, 1);
            _grid.Controls.Add(_values[2], 1, 1);
            _grid.Controls.Add(_names[3], 2, 1);
            _grid.Controls.Add(_values[3], 3, 1);
            _grid.Controls.Add(_names[4], 0, 2);
            _grid.Controls.Add(_values[4], 1, 2);

            _footer.Text = "updated --:--:--";
            _footer.AutoSize = true;
            _footer.ForeColor = TextMuted;
            _footer.BackColor = Back;
            _footer.Font = _mono;
            _footer.TextAlign = ContentAlignment.MiddleLeft;
            _footer.Margin = new Padding(0, 4, 0, 0);
            _grid.Controls.Add(_footer, 2, 2);
            _grid.SetColumnSpan(_footer, 2);

            // "monitor off" hint strip: visible whenever the engine is not
            // sampling (checked on attach, on every sample and by a light
            // 2 s poll so an engine Stop() while shown is noticed too).
            _hint.Text = "monitor off";
            _hint.ForeColor = TextMuted;
            _hint.BackColor = Back;
            _hint.Font = new Font("Segoe UI", 8.5f);
            _hint.AutoSize = false;
            _hint.Size = new Size(ClientSize.Width, 20);
            _hint.Dock = DockStyle.Bottom;
            _hint.TextAlign = ContentAlignment.MiddleLeft;
            _hint.Padding = new Padding(10, 0, 0, 2);

            Controls.Add(_grid);   // Dock.Fill - laid out last
            Controls.Add(_hint);   // Dock.Bottom - laid out first

            // The whole surface drags: form, grid and every label.
            MouseMove += OnDragMove;   // wired once here; OnDragStart only captures
            MouseUp += OnDragEnd;
            WireDrag(this);
            WireDrag(_grid);
            for (int i = 0; i < 6; i++)
            {
                WireDrag(_names[i]);
                WireDrag(_values[i]);
            }
            WireDrag(_footer);
            WireDrag(_hint);

            _hintTimer.Interval = 2000;
            _hintTimer.Tick += delegate { UpdateHint(); };
            _hintTimer.Start();
            UpdateHint();
        }

        // Showing the overlay never steals focus from the game.
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        // ---- public API -----------------------------------------------------

        // Subscribes to the engine (idempotent) and immediately paints the
        // engine's last sample, if any, so a re-attach never shows blanks.
        public void Attach()
        {
            if (_attached)
            {
                return;
            }
            MonitorEngine.SampleReady += OnSampleReady;
            _attached = true;
            Log.Chan("MONITOR", "overlay: attached to monitor engine");
            MonitorSample last = MonitorEngine.LastSample;
            if (last != null)
            {
                OnSampleReady(last);
            }
            UpdateHint();
        }

        public void Detach()
        {
            if (!_attached)
            {
                return;
            }
            MonitorEngine.SampleReady -= OnSampleReady;
            _attached = false;
            Log.Chan("MONITOR", "overlay: detached from monitor engine");
        }

        // Flips visibility. The resulting shown/hidden is logged by
        // OnVisibleChanged; this logs the user action itself.
        public void Toggle()
        {
            Visible = !Visible;
            Log.Chan("MONITOR", "overlay: toggled (" + (Visible ? "shown" : "hidden") + ")");
        }

        // ---- internals ------------------------------------------------------

        // Bottom-right of the primary working area (out of the game's way,
        // above the tray). Best-effort - an odd session just gets the
        // Windows default spot. Persistence is not required (D6).
        private void PlaceDefault()
        {
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
            }
            catch
            {
                // no primary screen info - keep the Windows default position
            }
        }

        private Label MakeLabel(string text, Color color, int rightMargin)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = color;
            l.BackColor = Back;
            l.Font = _mono;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = new Padding(0, 4, rightMargin, 0);
            return l;
        }

        // Reference guard: Dispose always detaches, so the engine can never
        // keep this (possibly collected) form alive through the event.
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Detach();
                _hintTimer.Stop();
                try { _hintTimer.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        private void OnSampleReady(MonitorSample s)
        {
            if (s == null)
            {
                return;
            }
            // The engine's Forms.Timer fires on the UI thread, so this is
            // usually a no-op check; a manual RunOnce from a worker thread
            // (tests) is marshaled instead of crossing threads.
            if (InvokeRequired)
            {
                if (!IsHandleCreated || IsDisposed)
                {
                    return;                               // next tick repaints
                }
                BeginInvoke(new Action<MonitorSample>(OnSampleReady), s);
                return;
            }
            ApplySample(s);
        }

        private void ApplySample(MonitorSample s)
        {
            try
            {
                _values[0].Text = FmtPct1(s.CpuPercent);
                _values[1].Text = FmtRam(s);
                _values[2].Text = FmtPct0(s.DiskActivePercent);
                if (s.HasDgpu)
                {
                    _values[3].Text = FmtPct0(s.DgpuPercent);
                    _values[3].ForeColor = TextMain;
                }
                else
                {
                    _values[3].Text = "N/A";
                    _values[3].ForeColor = TextMuted;
                }
                if (s.HasDgpuTemp)
                {
                    _values[4].Text = FmtTemp(s.DgpuTempC);
                    _values[4].ForeColor = TextMain;
                }
                else
                {
                    _values[4].Text = "N/A";
                    _values[4].ForeColor = TextMuted;
                }
                if (s.HasIgpu)
                {
                    ShowIgpuRow();
                    _values[5].Text = FmtPct0(s.IgpuPercent);
                    _values[5].ForeColor = TextMain;
                }
                else
                {
                    HideIgpuRow();
                }
                _footer.Text = "updated " + s.Timestamp.ToString("HH:mm:ss");
            }
            catch
            {
                // Cosmetic UI path only - a bad sample must never break the app.
            }
        }

        // The iGPU pair joins the grid only once the engine proved the phys
        // mapping; the footer trades places with it so the overlay never
        // grows a row. Both directions are idempotent.
        private void ShowIgpuRow()
        {
            if (_values[5].Parent == _grid)
            {
                return;
            }
            _grid.Controls.Remove(_footer);
            _grid.Controls.Add(_names[5], 2, 2);
            _grid.Controls.Add(_values[5], 3, 2);
            _grid.Controls.Add(_footer, 0, 3);
            _grid.SetColumnSpan(_footer, 2);
        }

        private void HideIgpuRow()
        {
            if (_values[5].Parent != _grid)
            {
                return;
            }
            _grid.Controls.Remove(_names[5]);
            _grid.Controls.Remove(_values[5]);
            _grid.Controls.Remove(_footer);
            _grid.Controls.Add(_footer, 2, 2);
            _grid.SetColumnSpan(_footer, 2);
        }

        // Hint strip: visible exactly while the engine is not sampling. The
        // host decides when to Start/Stop - the overlay only observes.
        private void UpdateHint()
        {
            if (IsDisposed)
            {
                return;
            }
            _hint.Visible = !MonitorEngine.Running;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!IsDisposed)
            {
                Log.Chan("MONITOR", Visible ? "overlay: shown" : "overlay: hidden");
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Region = new Region(UiShapes.RoundRect(0, 0, ClientSize.Width, ClientSize.Height, 10));
        }

        // ---- click-drag -----------------------------------------------------

        // MouseDown on any child only records the anchor; the form then
        // captures the mouse so MouseMove/MouseUp keep arriving even when the
        // cursor outruns this small window.
        private void WireDrag(Control c)
        {
            c.MouseDown += OnDragStart;
        }

        private void OnDragStart(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }
            _dragging = true;
            _dragCursor = Control.MousePosition;
            _dragFormPos = Location;
            Capture = true;
        }

        private void OnDragMove(object sender, MouseEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }
            Point now = Control.MousePosition;
            Location = new Point(
                _dragFormPos.X + (now.X - _dragCursor.X),
                _dragFormPos.Y + (now.Y - _dragCursor.Y));
        }

        private void OnDragEnd(object sender, MouseEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }
            _dragging = false;
            Capture = false;
        }

        // ---- formatting (invariant, like MonitorPanel) ----------------------

        private static string FmtPct1(float v)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0}%", v);
        }

        private static string FmtPct0(float v)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0}%", v);
        }

        private static string FmtTemp(float celsius)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0}\u00B0C", celsius);
        }

        private static string FmtRam(MonitorSample s)
        {
            double used = s.RamUsedBytes / 1073741824.0;
            double total = s.RamTotalBytes / 1073741824.0;
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0}/{1:0.0} GB", used, total);
        }
    }
}
