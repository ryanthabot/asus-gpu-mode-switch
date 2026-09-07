//  Forms.cs  (v1.1.0 - Wave 6)
//  ---------------------------
//  The windows of both apps: the themed borderless main window with its
//  phase machine and selection stage (MainForm + UiPhase), the log viewer
//  (LogForm), the Go Time tray-app picker data (TrayAppInfo / TrayApps,
//  MODE_STANDARD only), the eco-safe session restore helper (SessionSafety)
//  and the freeze-list editor (FreezeListEditorForm, MODE_STANDARD only).
//  Per-class comments below.
//
//  Split out of GpuModeSwitch.cs (Wave 2, zero behavior change). Wave 4
//  upgraded LogForm into a full log viewer (history over past per-run logs,
//  severity filter, find-next, open-folder).
//
//  Wave 6 (A18) wired the finished module APIs into the apps:
//    - Go Time's selection stage grew the "Performance" group (background
//      process freezer with an editable freeze list, Ultimate Performance
//      plan, session-scoped Windows Update pause) and the "Storage cleanup"
//      group (WU cache purge, DISM component store, deep clean, GPU shader
//      caches, per-app caches - measured in the background, disabled with
//      the reasons when the D7 safety gates block), plus named profiles
//      (ProfileBar), a collapsible live system monitor (MonitorPanel) and
//      result-stage "Log History" / "Session History" buttons. The whole
//      stage lives in one scrollable panel; the form grows to make room.
//    - GO now also runs the ticked session features (freeze -> power plan
//      -> WU pause -> cleanup chain), records a SessionRecord and, on
//      success, creates the session tray (with the overlay). --auto still
//      applies ONLY the v1.0.22 set - the new groups never run unattended.
//    - SessionSafety.RestoreAll() is the single eco-safe restore (frozen
//      processes resumed, previous power plan back, Windows Update resumed,
//      tray disposed), called on Eco Mode apply, the tray restore path,
//      background errors, unhandled exceptions and window close.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // Log viewer (v1.1.0, Wave 4): read-only monospace view of the live log
    // buffer or of any older per-run log file. Kept from v1.0.x: the path
    // strip, the pre-selected viewer text (Ctrl+C works immediately), the
    // Copy log button and the deterministic TableLayoutPanel shell (buttons
    // never vanish on resize). Added: a history dropdown over past logs
    // (newest first, live entry on top, with Refresh), an "Open folder"
    // button (selects the viewed log in Explorer), a severity filter
    // (All/Info/Warn/Error, parsed from the [LEVEL] line prefix) and a
    // case-insensitive Find next. The viewer logs its own actions through
    // Log.Info.
    // ---------------------------------------------------------------------
    internal class LogForm : Form
    {
        private readonly string _appName;
        private readonly TextBox _box = new TextBox();
        private readonly Label _pathLabel = new Label();
        private readonly ComboBox _history = new ComboBox();
        private readonly ComboBox _filter = new ComboBox();
        private readonly TextBox _search = new TextBox();
        private readonly Button _copyBtn = new Button();
        private readonly Button _copyAllBtn;
        private bool _viewingLive = true;
        private string _currentViewPath = "";      // "" while the live buffer is shown
        private string _fullText = "";             // unfiltered text of the current source
        private bool _suppressHistory;

        public LogForm(string appName)
        {
            _appName = appName == null ? "" : appName;

            Text = "Diagnostic log - " + _appName + " v" + Program.Version;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(680, 480);
            BackColor = Color.FromArgb(24, 24, 28);
            WindowIcons.Apply(this);

            // Viewer box - same look and behavior as v1.0.x (monospace, the
            // selection stays visible without focus, content pre-selected).
            _box.Multiline = true;
            _box.ReadOnly = true;
            _box.ScrollBars = ScrollBars.Vertical;
            _box.WordWrap = false;
            _box.BackColor = Color.FromArgb(14, 14, 16);
            _box.ForeColor = Color.FromArgb(205, 205, 210);
            _box.BorderStyle = BorderStyle.FixedSingle;
            _box.Font = new Font("Consolas", 9f);
            _box.Dock = DockStyle.Fill;
            _box.HideSelection = false;      // keep the selection visible without focus

            // ---- history row: session dropdown + refresh + open folder ----
            _history.DropDownStyle = ComboBoxStyle.DropDownList;
            _history.Width = 300;
            _history.FlatStyle = FlatStyle.Flat;
            _history.BackColor = Color.FromArgb(45, 45, 52);
            _history.ForeColor = Color.FromArgb(220, 220, 226);
            _history.Margin = new Padding(4, 6, 4, 6);
            _history.SelectedIndexChanged += OnHistoryChanged;

            Button refresh = MakeToolButton("Refresh");
            refresh.Click += delegate { OnRefresh(); };

            Button openFolder = MakeToolButton("Open folder");
            openFolder.Click += delegate { OnOpenFolder(); };

            FlowLayoutPanel historyBar = MakeBar();
            historyBar.Controls.Add(MakeFieldLabel("History:"));
            historyBar.Controls.Add(_history);
            historyBar.Controls.Add(refresh);
            historyBar.Controls.Add(openFolder);

            // ---- filter / find row: All/Info/Warn/Error + find-next ----
            _filter.DropDownStyle = ComboBoxStyle.DropDownList;
            _filter.Width = 84;
            _filter.FlatStyle = FlatStyle.Flat;
            _filter.BackColor = Color.FromArgb(45, 45, 52);
            _filter.ForeColor = Color.FromArgb(220, 220, 226);
            _filter.Margin = new Padding(4, 6, 4, 6);
            _filter.Items.Add("All");
            _filter.Items.Add("Info");
            _filter.Items.Add("Warn");
            _filter.Items.Add("Error");
            _filter.SelectedIndex = 0;
            _filter.SelectedIndexChanged += OnFilterChanged;

            _search.BackColor = Color.FromArgb(14, 14, 16);
            _search.ForeColor = Color.FromArgb(205, 205, 210);
            _search.BorderStyle = BorderStyle.FixedSingle;
            _search.Font = new Font("Consolas", 9f);
            _search.Width = 170;
            _search.Margin = new Padding(4, 6, 4, 6);
            _search.KeyDown += OnSearchKeyDown;

            Button findNext = MakeToolButton("Find next");
            findNext.Click += delegate { OnFindNext(); };

            FlowLayoutPanel filterBar = MakeBar();
            filterBar.Controls.Add(MakeFieldLabel("Show:"));
            filterBar.Controls.Add(_filter);
            filterBar.Controls.Add(MakeFieldLabel("Find:"));
            filterBar.Controls.Add(_search);
            filterBar.Controls.Add(findNext);

            // ---- bottom row: copy displayed text / copy whole log / close ----
            _copyBtn.Text = "Copy log";
            _copyBtn.AutoSize = true;
            _copyBtn.FlatStyle = FlatStyle.Flat;
            _copyBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
            _copyBtn.ForeColor = Color.White;
            _copyBtn.BackColor = Color.FromArgb(45, 45, 52);
            _copyBtn.Padding = new Padding(6, 4, 6, 4);
            _copyBtn.Margin = new Padding(4, 6, 4, 6);
            _copyBtn.Click += delegate { OnCopyDisplayed(); };

            _copyAllBtn = MakeToolButton("Copy all");
            _copyAllBtn.Click += delegate { OnCopyAll(); };

            Button close = new Button();
            close.Text = "Close";
            close.AutoSize = true;
            close.FlatStyle = FlatStyle.Flat;
            close.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
            close.ForeColor = Color.FromArgb(210, 210, 216);
            close.BackColor = Color.FromArgb(45, 45, 52);
            close.Padding = new Padding(6, 4, 6, 4);
            close.Margin = new Padding(4, 6, 12, 6);
            close.Click += delegate { Close(); };

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.BackColor = Color.FromArgb(24, 24, 28);
            buttons.Controls.Add(_copyBtn);
            buttons.Controls.Add(_copyAllBtn);
            buttons.Controls.Add(close);

            // Path strip - shows the log currently being viewed.
            _pathLabel.Text = "Log file: " + Log.CurrentLogPath;
            _pathLabel.ForeColor = Color.FromArgb(140, 140, 148);
            _pathLabel.AutoEllipsis = true;
            _pathLabel.Dock = DockStyle.Top;
            _pathLabel.Height = 26;
            _pathLabel.TextAlign = ContentAlignment.MiddleLeft;
            _pathLabel.Padding = new Padding(12, 4, 12, 0);
            _pathLabel.BackColor = Color.FromArgb(24, 24, 28);

            // TableLayoutPanel shell: deterministic at any DPI and window
            // size (v1.0.19 lesson) - now five rows, all AutoSize except the
            // viewer row.
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 5;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // path strip
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // history
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // filter + find
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // viewer
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // buttons
            layout.BackColor = Color.FromArgb(24, 24, 28);
            layout.Controls.Add(_pathLabel, 0, 0);
            layout.Controls.Add(historyBar, 0, 1);
            layout.Controls.Add(filterBar, 0, 2);
            layout.Controls.Add(_box, 0, 3);
            layout.Controls.Add(buttons, 0, 4);
            Controls.Add(layout);

            // Initial view: the live buffer, pre-selected (v1.0 behavior:
            // Ctrl+C copies right away; focus the box once shown).
            PopulateHistory();
            LoadSelected();
            Shown += delegate { _box.Focus(); };
        }

        // ---- shared dark-theme helpers -------------------------------------

        // Flat toolbar button in the LogForm button style.
        private static Button MakeToolButton(string text)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
            b.ForeColor = Color.FromArgb(210, 210, 216);
            b.BackColor = Color.FromArgb(45, 45, 52);
            b.Padding = new Padding(6, 4, 6, 4);
            b.Margin = new Padding(4, 6, 4, 6);
            return b;
        }

        // One AutoSize FlowLayoutPanel bar for a TableLayoutPanel row (same
        // proven pattern as the bottom button row, which never vanishes).
        private static FlowLayoutPanel MakeBar()
        {
            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Top;
            bar.AutoSize = true;
            bar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bar.FlowDirection = FlowDirection.LeftToRight;
            bar.WrapContents = true;
            bar.BackColor = Color.FromArgb(24, 24, 28);
            return bar;
        }

        // Small muted caption in front of a combo/text field.
        private static Label MakeFieldLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = Color.FromArgb(140, 140, 148);
            l.BackColor = Color.FromArgb(24, 24, 28);
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = new Padding(12, 9, 2, 3);
            return l;
        }

        // ---- history -------------------------------------------------------

        // One entry of the history dropdown. FilePath "" is the live buffer.
        private class HistoryItem
        {
            public string FilePath;
            public string Caption;

            public override string ToString()
            {
                return Caption == null ? "" : Caption;
            }
        }

        // Rebuilds the dropdown: live entry first, then Log.ListLogs(appName)
        // newest first (the current run's file is tagged "(current)"). The
        // previous selection is kept when still present, else falls back to
        // the live entry.
        private void PopulateHistory()
        {
            HistoryItem previous = _history.SelectedItem as HistoryItem;
            string keepPath = previous == null ? "" : previous.FilePath;

            _suppressHistory = true;
            _history.Items.Clear();

            HistoryItem live = new HistoryItem();
            live.FilePath = "";
            live.Caption = "Current session (live)";
            _history.Items.Add(live);

            string[] logs = Log.ListLogs(_appName);     // newest first
            foreach (string path in logs)
            {
                HistoryItem it = new HistoryItem();
                it.FilePath = path;
                it.Caption = Path.GetFileName(path);
                if (SamePath(path, Log.CurrentLogPath))
                {
                    it.Caption = it.Caption + "  (current)";
                }
                _history.Items.Add(it);
            }

            int select = 0;
            for (int i = 0; i < _history.Items.Count; i++)
            {
                HistoryItem it = (HistoryItem)_history.Items[i];
                if (keepPath.Length == 0 ? it.FilePath.Length == 0 : SamePath(keepPath, it.FilePath))
                {
                    select = i;
                    break;
                }
            }
            _history.SelectedIndex = select;
            _suppressHistory = false;
        }

        // Loads whatever the dropdown currently has selected: the live buffer
        // (Log.Snapshot()) or the selected older file. Newly loaded content is
        // pre-selected so Ctrl+C works immediately (v1.0 behavior).
        private void LoadSelected()
        {
            HistoryItem it = _history.SelectedItem as HistoryItem;
            if (it == null) return;

            if (it.FilePath.Length == 0 || SamePath(it.FilePath, Log.CurrentLogPath))
            {
                string current = Log.CurrentLogPath;
                Log.Info("log viewer: opened " +
                    (current.Length > 0 ? Path.GetFileName(current) : "(no file yet)") + " (live)");
                _viewingLive = true;
                _currentViewPath = "";
                SetViewText(Log.Snapshot());
                _pathLabel.Text = "Log file: " + current;
            }
            else
            {
                Log.Info("log viewer: opened " + Path.GetFileName(it.FilePath));
                _viewingLive = false;
                _currentViewPath = it.FilePath;
                string text;
                try
                {
                    text = File.ReadAllText(it.FilePath);
                }
                catch (Exception ex)
                {
                    Log.Warn("log viewer: could not read " + it.FilePath + " - " + ex.Message);
                    text = "(could not read file: " + ex.Message + ")";
                }
                SetViewText(text);
                _pathLabel.Text = "Log file: " + it.FilePath;
            }
            _box.SelectAll();
        }

        private void OnHistoryChanged(object sender, EventArgs e)
        {
            if (_suppressHistory) return;
            LoadSelected();
        }

        // Re-lists the saved logs and reloads the current selection (a live
        // view also picks up lines written since it was loaded).
        private void OnRefresh()
        {
            PopulateHistory();
            Log.Info("log viewer: history refreshed (" + (_history.Items.Count - 1) + " saved log(s))");
            LoadSelected();
        }

        // ---- filter / find ---------------------------------------------------

        private void OnFilterChanged(object sender, EventArgs e)
        {
            Log.Info("log viewer: filter=" + FilterLabel());
            if (_viewingLive)
            {
                SetViewText(Log.Snapshot());    // fresh buffer, re-filtered
            }
            else
            {
                SetViewText(_fullText);         // same file, re-filtered
            }
        }

        // Selected severity: INFO/WARN/ERROR, or null for "All".
        private string CurrentFilterLevel()
        {
            string label = FilterLabel();
            if (label == "Info") return "INFO";
            if (label == "Warn") return "WARN";
            if (label == "Error") return "ERROR";
            return null;
        }

        private string FilterLabel()
        {
            object sel = _filter.SelectedItem;
            return sel == null ? "All" : sel.ToString();
        }

        // Level tag of a log line ("INFO"/"WARN"/"ERROR") or null for lines
        // that are not level-tagged (e.g. exception continuation lines, which
        // stay attached to the entry above them while filtering).
        private static string LineLevel(string line)
        {
            if (line == null || line.Length < 6) return null;
            if (line[0] == ' ') return null;
            int open = line.IndexOf('[');
            if (open < 0 || open > 24) return null;     // tag sits right after the timestamp
            int close = line.IndexOf(']', open + 1);
            if (close < 0 || close > open + 7) return null;
            string tag = line.Substring(open + 1, close - open - 1);
            if (tag == "INFO") return "INFO";
            if (tag == "WARN") return "WARN";
            if (tag == "ERROR") return "ERROR";
            return null;
        }

        // Keeps only lines tagged with the given level (plus their exception
        // continuations). level must be INFO, WARN or ERROR.
        private static string FilterLines(string text, string level)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            StringBuilder sb = new StringBuilder();
            bool keepBlock = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string lineLevel = LineLevel(lines[i]);
                if (lineLevel != null) keepBlock = (lineLevel == level);
                if (!keepBlock) continue;
                sb.Append(lines[i]);
                if (i < lines.Length - 1) sb.Append("\r\n");
            }
            return sb.ToString();
        }

        // Applies the current filter; the full text is kept in _fullText for
        // Copy all and for re-filtering.
        private void SetViewText(string fullText)
        {
            _fullText = fullText == null ? "" : fullText;
            string level = CurrentFilterLevel();
            _box.Text = level == null ? _fullText : FilterLines(_fullText, level);
        }

        // Case-insensitive find-next over the displayed text; wraps around at
        // the end. Enter in the search box repeats it.
        private void OnFindNext()
        {
            string needle = _search.Text;
            if (needle.Length == 0) return;
            string hay = _box.Text;
            if (hay.Length == 0) return;

            int start = _box.SelectionStart + _box.SelectionLength;
            if (start > hay.Length) start = hay.Length;
            int idx = hay.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0 && start > 0)
            {
                idx = hay.IndexOf(needle, 0, StringComparison.OrdinalIgnoreCase);   // wrap
            }
            if (idx < 0)
            {
                Log.Info("log viewer: find \"" + needle + "\" - no match");
                return;
            }
            _box.Select(idx, needle.Length);
            _box.ScrollToCaret();
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;      // no ding, no default-button click
                e.Handled = true;
                OnFindNext();
            }
        }

        // ---- copy / open folder ----------------------------------------------

        // Full path of the log currently being viewed ("" if none).
        private string ViewedLogPath()
        {
            return _viewingLive ? Log.CurrentLogPath : _currentViewPath;
        }

        // Copies what is displayed right now (filtered view or selected file).
        private void OnCopyDisplayed()
        {
            try
            {
                string text = _box.Text;
                Clipboard.SetText(text);
                Log.Info("log viewer: copied displayed text (" + text.Length + " chars)");
                _copyBtn.Text = "Copied!";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Clipboard copy failed: " + ex.Message +
                    "\n\nThe log file is at:\n" + ViewedLogPath(),
                    "Diagnostic log", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Copies the whole unfiltered source (live buffer or full file).
        private void OnCopyAll()
        {
            try
            {
                string text = _viewingLive ? Log.Snapshot() : _fullText;
                Clipboard.SetText(text);
                Log.Info("log viewer: copied full log (" + text.Length + " chars)");
                _copyAllBtn.Text = "Copied!";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Clipboard copy failed: " + ex.Message +
                    "\n\nThe log file is at:\n" + ViewedLogPath(),
                    "Diagnostic log", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Opens Explorer with the viewed log pre-selected.
        private void OnOpenFolder()
        {
            string path = ViewedLogPath();
            if (path.Length == 0)
            {
                MessageBox.Show("No log file is associated with this view yet.",
                    "Diagnostic log", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
                Log.Info("log viewer: open folder " + path);
            }
            catch (Exception ex)
            {
                Log.Info("log viewer: open folder failed - " + ex.Message);
                MessageBox.Show("Could not open the log folder: " + ex.Message +
                    "\n\nThe log file is at:\n" + path,
                    "Diagnostic log", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------------
    // Eco-safe session restore (v1.1, Wave 6): one helper both exes call so
    // no Go Time session ever leaves a frozen process, a paused Windows
    // Update or a foreign power plan behind. Called on: Eco Mode apply, Go
    // Time's restore-to-eco tray path, abnormal background errors, unhandled
    // exceptions and window close. Every step is individually try/caught and
    // a no-op when nothing is active - safe on any exit path.
    // ActiveTray holds Go Time's session tray (null in Eco Mode / before a
    // successful GO) so this helper can tear it down from anywhere.
    // ---------------------------------------------------------------------
    internal static class SessionSafety
    {
        public static SessionTray ActiveTray;

        public static void RestoreAll()
        {
            try { ProcessFreezer.ResumeAllSafe(); }
            catch (Exception ex) { Log.Warn("restore: resume failed - " + ex.Message); }

            try { PowerPlans.RestorePrevious(); }
            catch (Exception ex) { Log.Warn("restore: power plan restore failed - " + ex.Message); }

            try { WuPause.ResumeUpdates(); }
            catch (Exception ex) { Log.Warn("restore: Windows Update resume failed - " + ex.Message); }

            SessionTray tray = ActiveTray;
            if (tray != null)
            {
                ActiveTray = null;
                try { tray.Hide(); }
                catch (Exception ex) { Log.Warn("restore: tray hide failed - " + ex.Message); }
                try { tray.Dispose(); }
                catch (Exception ex) { Log.Warn("restore: tray dispose failed - " + ex.Message); }
            }
            Log.Info("restore: eco-safe session restore done");
        }
    }

#if MODE_STANDARD
    // ---------------------------------------------------------------------
    // Known tray applications for the Go Time post-switch picker. Candidates
    // are matched case-insensitively against running process names: contains
    // for long names, exact for short ones (so "vgc" can't match randomly).
    // ---------------------------------------------------------------------
    internal class TrayAppInfo
    {
        public string Label;
        public string[] Candidates;
        public bool Running;
    }

    internal static class TrayApps
    {
        public static TrayAppInfo[] Known = new TrayAppInfo[]
        {
            new TrayAppInfo { Label = "Parsec",         Candidates = new string[] { "parsec" } },
            new TrayAppInfo { Label = "Google Drive",   Candidates = new string[] { "googledrivefs", "googledrivesync" } },
            new TrayAppInfo { Label = "Jellyfin",       Candidates = new string[] { "jellyfin" } },
            new TrayAppInfo { Label = "Riot Client",    Candidates = new string[] { "riotclient" } },
            new TrayAppInfo { Label = "Riot Vanguard",  Candidates = new string[] { "vgtray", "vgc" } },
        };

        private static bool Matches(string processName, TrayAppInfo app)
        {
            foreach (string cand in app.Candidates)
            {
                if (cand.Length <= 4)
                {
                    if (processName == cand) return true;
                }
                else if (processName.Contains(cand))
                {
                    return true;
                }
            }
            return false;
        }

        public static void Detect()
        {
            List<string> names = new List<string>();
            foreach (Process p in Process.GetProcesses())
            {
                try { names.Add(p.ProcessName.ToLowerInvariant()); } catch { }
                try { p.Dispose(); } catch { }
            }
            foreach (TrayAppInfo a in Known)
            {
                a.Running = false;
                foreach (string pn in names)
                {
                    foreach (string cand in a.Candidates)
                    {
                        if (cand.Length <= 4 ? pn == cand : pn.Contains(cand))
                        {
                            a.Running = true;
                            break;
                        }
                    }
                    if (a.Running) break;
                }
                Log.Chan("TRAY", "TrayApps: " + a.Label + " " + (a.Running ? "running" : "not running"));
            }
        }

        // Full close: stop any watchdog service whose binary matches the app
        // (Parsec's pservice relaunches parsecd on kill), kill the processes,
        // and re-check up to 3 rounds - refreshing the running state each time.
        public static void Close(TrayAppInfo app)
        {
            List<string> services = FindMatchingServices(app);

            for (int round = 1; round <= 3; round++)
            {
                Log.Chan("TRAY", "TrayApps: close attempt " + round + "/3 for " + app.Label);

                foreach (string s in services)
                {
                    try
                    {
                        using (ServiceController sc = new ServiceController(s))
                        {
                            if (sc.Status == ServiceControllerStatus.Running ||
                                sc.Status == ServiceControllerStatus.StartPending)
                            {
                                sc.Stop();
                                try
                                {
                                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                                    Log.Chan("TRAY", "TrayApps: stopped service " + s);
                                }
                                catch (System.ServiceProcess.TimeoutException)
                                {
                                    Log.Chan("TRAY", "TrayApps: service " + s + " stop timed out");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Chan("TRAY", "TrayApps: service " + s + " stop failed - " + ex.Message);
                    }
                }

                KillMatching(app);
                Thread.Sleep(1500);
                Detect();
                if (!app.Running)
                {
                    Log.Chan("TRAY", "TrayApps: " + app.Label + " fully closed");
                    return;
                }
                Log.Chan("TRAY", "TrayApps: " + app.Label + " still running - retrying");
            }

            Log.Chan("TRAY", "TrayApps: " + app.Label + " could not be fully closed - its watchdog may keep restarting it");
        }

        // Scan the Services registry for services whose ImagePath contains one
        // of the app's process-name candidates (e.g. Parsec's pservice.exe).
        private static List<string> FindMatchingServices(TrayAppInfo app)
        {
            List<string> found = new List<string>();
            try
            {
                using (RegistryKey services = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services"))
                {
                    if (services == null) return found;
                    foreach (string name in services.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey k = services.OpenSubKey(name))
                            {
                                if (k == null) continue;
                                object ip = k.GetValue("ImagePath");
                                if (ip == null) continue;
                                string path = ip.ToString().ToLowerInvariant();
                                foreach (string cand in app.Candidates)
                                {
                                    if (cand.Length > 4 && path.Contains(cand))
                                    {
                                        found.Add(name);
                                        Log.Chan("TRAY", "TrayApps: matched service '" + name + "' (" + path.Trim() + ")");
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Chan("TRAY", "TrayApps: service scan failed - " + ex.Message);
            }
            return found;
        }

        private static void KillMatching(TrayAppInfo app)
        {
            // graceful close first
            foreach (Process p in Process.GetProcesses())
            {
                string pn = "";
                try { pn = p.ProcessName.ToLowerInvariant(); } catch { continue; }
                if (!Matches(pn, app)) continue;
                try { p.CloseMainWindow(); } catch { }
            }
            Thread.Sleep(800);

            // then kill survivors
            foreach (Process p in Process.GetProcesses())
            {
                string pn = "";
                try { pn = p.ProcessName.ToLowerInvariant(); } catch { continue; }
                if (!Matches(pn, app)) continue;
                try
                {
                    p.Kill();
                    if (p.WaitForExit(3000)) Log.Chan("TRAY", "TrayApps: closed " + p.ProcessName);
                    else Log.Chan("TRAY", "TrayApps: " + p.ProcessName + " did not exit in time");
                }
                catch (Exception ex)
                {
                    Log.Chan("TRAY", "TrayApps: could not close " + p.ProcessName + " - " + ex.Message);
                }
            }
        }
    }

    // ---------------------------------------------------------------------
    // Freeze list editor (v1.1, Wave 6): simple dark modal over
    // ProcessFreezer's freezelist.txt - one process name per line. On save,
    // names are normalized (a trailing ".exe" is stripped), blank lines are
    // skipped and guarded (never-freeze) names are refused with a message
    // (ProcessFreezer.IsGuarded), mirroring the freezer's safety boundary.
    // ---------------------------------------------------------------------
    internal class FreezeListEditorForm : Form
    {
        private readonly TextBox _list = new TextBox();

        public FreezeListEditorForm()
        {
            Text = "Freeze list - background apps";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(430, 390);
            BackColor = Color.FromArgb(24, 24, 28);
            WindowIcons.Apply(this);

            Label caption = new Label();
            caption.Text = "One process name per line (no .exe). Ticking \"Freeze background apps\" suspends these apps for the session and resumes them on restore.";
            caption.ForeColor = Color.FromArgb(165, 165, 172);
            caption.BackColor = Color.FromArgb(24, 24, 28);
            caption.Dock = DockStyle.Top;
            caption.Height = 48;
            caption.Padding = new Padding(12, 8, 12, 0);

            _list.Multiline = true;
            _list.ScrollBars = ScrollBars.Vertical;
            _list.WordWrap = false;
            _list.BackColor = Color.FromArgb(14, 14, 16);
            _list.ForeColor = Color.FromArgb(205, 205, 210);
            _list.BorderStyle = BorderStyle.FixedSingle;
            _list.Font = new Font("Consolas", 9f);
            _list.Dock = DockStyle.Fill;
            _list.HideSelection = false;

            Button save = new Button();
            save.Text = "Save";
            save.AutoSize = true;
            save.FlatStyle = FlatStyle.Flat;
            save.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
            save.ForeColor = Color.White;
            save.BackColor = Color.FromArgb(45, 45, 52);
            save.Padding = new Padding(6, 4, 6, 4);
            save.Margin = new Padding(4, 6, 4, 6);
            save.Click += delegate { OnSave(); };

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.AutoSize = true;
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
            cancel.ForeColor = Color.FromArgb(210, 210, 216);
            cancel.BackColor = Color.FromArgb(45, 45, 52);
            cancel.Padding = new Padding(6, 4, 6, 4);
            cancel.Margin = new Padding(4, 6, 12, 6);
            cancel.Click += delegate { Close(); };

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.BackColor = Color.FromArgb(24, 24, 28);
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);

            Controls.Add(_list);
            Controls.Add(buttons);
            Controls.Add(caption);

            // Seed the defaults on a fresh machine, then load the user list.
            ProcessFreezer.SeedDefaultListIfMissing();
            List<string> names = ProcessFreezer.GetUserList();
            _list.Text = string.Join("\r\n", names.ToArray());
        }

        private void OnSave()
        {
            string[] lines = _list.Text.Replace("\r\n", "\n").Split('\n');
            List<string> clean = new List<string>();
            List<string> refused = new List<string>();
            foreach (string raw in lines)
            {
                string name = raw == null ? "" : raw.Trim();
                if (name.ToLowerInvariant().EndsWith(".exe"))
                {
                    name = name.Substring(0, name.Length - 4).Trim();
                }
                if (name.Length == 0) continue;              // blank lines are simply skipped
                if (ProcessFreezer.IsGuarded(name))
                {
                    refused.Add(name);                        // never-freeze guard: refuse with feedback
                    continue;
                }
                bool dup = false;
                foreach (string have in clean)
                {
                    if (string.Equals(have, name, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                }
                if (!dup) clean.Add(name);
            }
            if (refused.Count > 0)
            {
                MessageBox.Show("These names are protected and cannot be frozen:\n  " +
                    string.Join("\n  ", refused.ToArray()) +
                    "\n\nRemove them to save the list.",
                    "Freeze list", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ProcessFreezer.SaveUserList(clean);
            Log.Chan("FREEZE", "freeze list editor: saved " + clean.Count + " name(s)");
            Close();
        }
    }
#endif

    internal enum UiPhase
    {
        Probe,      // contacting hardware, shimmer on
        Confirm,    // waiting for the user to press Apply
        Applying,   // switch running on the background thread
        Result      // done or failed
    }

    internal class MainForm : Form
    {
        private readonly PictureBox _logo = new PictureBox();
        private readonly Label _title = new Label();
        private readonly Label _subtitle = new Label();
        private readonly Label _status = new Label();
        private readonly Label _detail = new Label();
        private readonly ShimmerBar _bar = new ShimmerBar();
        private readonly Button _apply = new Button();
        private readonly Button _cancel = new Button();
        private readonly Button _restart = new Button();
        private readonly Button _logBtn = new Button();
        private readonly Button _close = new Button();
        private readonly Button _x = new Button();
        private readonly System.Windows.Forms.Timer _clock = new System.Windows.Forms.Timer();
        private readonly bool _confirmMode;
        private readonly bool _autoMode;
        private UiPhase _phase = UiPhase.Probe;
        private SwitchOutcome _last;
#if MODE_STANDARD
        private readonly Label _trayTitle = new Label();
        private readonly Label _optTitle = new Label();
        private readonly List<CheckBox> _trayBoxes = new List<CheckBox>();
        private readonly List<CheckBox> _optBoxes = new List<CheckBox>();
        private readonly Button _closeTray = new Button();
        private bool _trayBusy;

        // ---- Wave 6 (A18) selection-stage + session fields ------------------
        private readonly Panel _selectPanel = new Panel();        // scrollable host for the whole selection stage
        private readonly Label _precheckLabel = new Label();      // precheck text (was _detail in v1.0.22)
        private readonly ProfileBar _profileBar = new ProfileBar();          // named profiles (A14)
        private readonly Label _perfTitle = new Label();
        private readonly CheckBox _freezeBox = new CheckBox();    // ProcessFreezer (A12)
        private readonly Button _editFreezeList = new Button();
        private readonly CheckBox _planBox = new CheckBox();      // PowerPlans (A13)
        private readonly CheckBox _wuPauseBox = new CheckBox();   // WuPause (A13)
        private readonly Label _cleanTitle = new Label();
        private readonly CheckBox _cleanWuBox = new CheckBox();   // StorageCleaner (A7), WU-kind categories
        private readonly CheckBox _cleanDismBox = new CheckBox(); // ComponentStore (A8)
        private readonly CheckBox _cleanDeepBox = new CheckBox(); // tier-1 temp/WER/dumps + DeepClean (A10)
        private readonly CheckBox _cleanGpuBox = new CheckBox();  // GpuTools (A11), shader caches only
        private readonly List<CheckBox> _appCacheBoxes = new List<CheckBox>(); // one per installed app-cache target (A9)
        private readonly Label _gateLabel = new Label();          // D7 safety-gate warning
        private readonly Label _oldLabel = new Label();           // "previous Windows installations" report-only note
        private readonly Button _monitorToggle = new Button();
        private readonly MonitorPanel _monitorPanel = new MonitorPanel();    // live system monitor (A16)
        private readonly Button _histBtn = new Button();          // result stage: Log History (A5)
        private readonly Button _sessBtn = new Button();          // result stage: Session History (A15)
        private SessionTray _tray;                                // session tray (A17), created after a successful GO
        private MonitorOverlayForm _overlay;                      // gaming overlay (A17), created on first toggle
        private List<CleanCategory> _tier1Measured;               // StorageAnalyzer.MeasureAll() snapshot, tier-1 kinds
        private List<string> _gateReasons;                        // StorageAnalyzer.CheckGates() snapshot (null = unknown)
        private bool _measureStarted;
        private bool _selectLaidOut;
        private int _cleanGroupEndY;                              // panel y after the cleanup group (for the info labels)
        private string _cleanupSummary = "";                      // result-stage cleanup summary block
#endif

#if MODE_ECO
        private const bool TargetEco = true;
        private const string Title = "ECO MODE";
        private const string Subtitle = "Eco GPU mode  |  dGPU powered off  |  battery friendly";
        private readonly Color _accent = Color.FromArgb(76, 195, 138);
#else
        private const bool TargetEco = false;
        private const string Title = "GO TIME";
        private const string Subtitle = "Standard GPU mode  |  dGPU on  |  hybrid display path";
        private readonly Color _accent = Color.FromArgb(255, 70, 85);
#endif

        public MainForm(bool confirmMode, bool autoMode)
        {
            _confirmMode = confirmMode;
            _autoMode = autoMode;

            Text = TargetEco ? "Eco Mode" : "Go Time";
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
#if MODE_STANDARD
            ClientSize = new Size(560, 640);
            MinimumSize = new Size(560, 640);
#else
            ClientSize = new Size(560, 400);
            MinimumSize = new Size(560, 400);
#endif
            BackColor = Color.FromArgb(22, 22, 26);
            Font = new Font("Segoe UI", 9.5f);
            Opacity = 0;
            WindowIcons.Apply(this);
            Region = new Region(UiShapes.RoundRect(0, 0, ClientSize.Width, ClientSize.Height, 26));

            Bitmap logo = LoadLogo();
            if (logo != null)
            {
                _logo.Image = logo;
                _logo.SizeMode = PictureBoxSizeMode.Zoom;
                _logo.Location = new Point(24, 18);
                _logo.Size = new Size(56, 56);
                _logo.TabStop = false;
                Controls.Add(_logo);
            }

            _title.Text = Title;
            _title.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
            _title.ForeColor = _accent;
            _title.AutoSize = true;
            _title.Location = new Point(94, logo != null ? 24 : 24);
            _title.BackColor = Color.Transparent;

            _subtitle.Text = Subtitle + "   v" + Program.Version;
            _subtitle.ForeColor = Color.FromArgb(150, 150, 158);
            _subtitle.AutoSize = true;
            _subtitle.Location = new Point(96, logo != null ? 56 : 56);
            _subtitle.BackColor = Color.Transparent;

            _x.Text = "X";
            _x.FlatStyle = FlatStyle.Flat;
            _x.FlatAppearance.BorderSize = 0;
            _x.ForeColor = Color.FromArgb(140, 140, 148);
            _x.BackColor = Color.FromArgb(22, 22, 26);
            _x.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            _x.Size = new Size(32, 26);
            _x.Location = new Point(ClientSize.Width - 42, 10);
            _x.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _x.TabStop = false;
            _x.Click += delegate
            {
                if (_phase == UiPhase.Applying) return;   // never abandon a mid-flight write
                Close();
            };

            _bar.Accent = _accent;
            _bar.Location = new Point(24, 92);
            _bar.Size = new Size(ClientSize.Width - 48, 8);
            _bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _status.Text = "Starting...";
            _status.ForeColor = Color.FromArgb(235, 235, 240);
            _status.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
            _status.AutoSize = false;
            _status.Size = new Size(ClientSize.Width - 48, 28);
            _status.Location = new Point(24, 118);
            _status.BackColor = Color.Transparent;
            _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _detail.ForeColor = Color.FromArgb(165, 165, 172);
            _detail.AutoSize = false;
#if MODE_STANDARD
            _detail.Size = new Size(ClientSize.Width - 48, 170);
            _detail.Location = new Point(24, 368);
#else
            _detail.Size = new Size(ClientSize.Width - 48, 140);
            _detail.Location = new Point(24, 148);
#endif
            _detail.BackColor = Color.Transparent;
            _detail.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            InitButton(_apply, "Apply", 150, 24, 36);
            _apply.BackColor = _accent;
            _apply.ForeColor = Color.White;
            _apply.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            _apply.FlatAppearance.BorderSize = 0;
            _apply.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _apply.Click += delegate { BeginApply(); };

            InitButton(_cancel, "Cancel", 90, 182, 36);
            _cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _cancel.Click += delegate { Close(); };

            InitButton(_restart, "Restart now", 120, 24, 36);
            _restart.Visible = false;
            _restart.FlatAppearance.BorderColor = _accent;
            _restart.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _restart.Click += OnRestart;

            InitButton(_logBtn, "View log", 90, 152, 36);
            _logBtn.Visible = false;
            _logBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _logBtn.Click += delegate
            {
                using (LogForm lf = new LogForm(TargetEco ? "Eco Mode" : "Go Time")) lf.ShowDialog(this);
            };

            InitButton(_close, "Close", 80, 250, 36);
            _close.Visible = false;
            _close.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _close.Click += delegate { Close(); };

#if MODE_STANDARD
            // Result stage, second row (v1.1): log history browser (A5) and
            // session history viewer (A15).
            InitButton(_histBtn, "Log History", 110, 24, 36);
            _histBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _histBtn.Click += delegate { LogBrowserForm.ShowBrowser(this); };
            InitButton(_sessBtn, "Session History", 120, 142, 36);
            _sessBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _sessBtn.Click += delegate { SessionHistoryForm.ShowHistory(this); };
            _histBtn.Top = ClientSize.Height - 36 - 54;      // second button row
            _sessBtn.Top = _histBtn.Top;
#endif

#if MODE_STANDARD
            // ---- selection stage (v1.1): everything lives in one scrollable
            // panel so the growing feature set fits the 560-wide window. The
            // panel is shown by EnterSelect and reused in tray-only mode by
            // the result stage (v1.0.22 kept the tray picker visible there).
            _selectPanel.AutoScroll = true;
            _selectPanel.BackColor = BackColor;
            _selectPanel.Visible = false;
            _selectPanel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            _precheckLabel.ForeColor = Color.FromArgb(165, 165, 172);
            _precheckLabel.BackColor = Color.Transparent;
            _precheckLabel.Visible = false;

            _optTitle.Text = "System optimizations";
            _optTitle.ForeColor = Color.FromArgb(165, 165, 172);
            _optTitle.AutoSize = true;
            _optTitle.BackColor = Color.Transparent;
            _optTitle.Visible = false;

            string[] optLabels = { "Game Mode", "Do not disturb", "Game DVR recording off", "Network throttling off", "Pause background services" };
            string[] optKeys = { "opt.gamemode", "opt.dnd", "opt.dvr", "opt.throttle", "opt.services" };
            for (int i = 0; i < optLabels.Length; i++)
            {
                CheckBox cb = MakeSelectCheckBox(optLabels[i], true);
                cb.Tag = optKeys[i];                    // stable profile key
                _optBoxes.Add(cb);
            }

            _trayTitle.Text = "Tray apps detected:";
            _trayTitle.ForeColor = Color.FromArgb(165, 165, 172);
            _trayTitle.AutoSize = true;
            _trayTitle.BackColor = Color.Transparent;
            _trayTitle.Visible = false;

            for (int i = 0; i < TrayApps.Known.Length; i++)
            {
                CheckBox cb = MakeSelectCheckBox(TrayApps.Known[i].Label, false);
                cb.Enabled = false;
                cb.Tag = "tray." + TrayApps.Known[i].Label;   // stable profile key
                _trayBoxes.Add(cb);
            }

            _closeTray.Text = "Close selected";
            _closeTray.FlatStyle = FlatStyle.Flat;
            _closeTray.FlatAppearance.BorderColor = _accent;
            _closeTray.ForeColor = Color.White;
            _closeTray.BackColor = Color.FromArgb(42, 42, 49);
            _closeTray.Size = new Size(150, 26);
            _closeTray.TabStop = false;
            _closeTray.Visible = false;
            _closeTray.Click += delegate { BeginCloseTrayApps(); };

            // Performance group: session-scoped, fully reversible features.
            _perfTitle.Text = "Performance";
            _perfTitle.ForeColor = Color.FromArgb(165, 165, 172);
            _perfTitle.AutoSize = true;
            _perfTitle.BackColor = Color.Transparent;
            _perfTitle.Visible = false;

            _freezeBox = MakeSelectCheckBox("Freeze background apps", true);
            _freezeBox.Tag = "perf.freeze";
            _editFreezeList.Text = "Edit list...";
            _editFreezeList.FlatStyle = FlatStyle.Flat;
            _editFreezeList.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
            _editFreezeList.ForeColor = Color.FromArgb(210, 210, 216);
            _editFreezeList.BackColor = Color.FromArgb(42, 42, 49);
            _editFreezeList.Size = new Size(110, 25);
            _editFreezeList.TabStop = false;
            _editFreezeList.Visible = false;
            _editFreezeList.Click += delegate { ShowFreezeListEditor(); };

            _planBox = MakeSelectCheckBox("Ultimate Performance plan", true);
            _planBox.Tag = "perf.plan";
            _wuPauseBox = MakeSelectCheckBox("Pause Windows Update", true);
            _wuPauseBox.Tag = "perf.wupause";

            // Storage cleanup group: deleting actions, so every box starts
            // UNticked - cleanup only ever runs when explicitly chosen for
            // this run (and never via --auto).
            _cleanTitle.Text = "Storage cleanup";
            _cleanTitle.ForeColor = Color.FromArgb(165, 165, 172);
            _cleanTitle.AutoSize = true;
            _cleanTitle.BackColor = Color.Transparent;
            _cleanTitle.Visible = false;

            _cleanWuBox = MakeSelectCheckBox("Windows Update cache purge", false);
            _cleanWuBox.Tag = "clean.wu";
            _cleanDismBox = MakeSelectCheckBox("Component store cleanup (DISM)", false);
            _cleanDismBox.Tag = "clean.dism";
            _cleanDeepBox = MakeSelectCheckBox("Deep clean", false);
            _cleanDeepBox.Tag = "clean.deep";
            _cleanGpuBox = MakeSelectCheckBox("GPU shader caches", false);
            _cleanGpuBox.Tag = "clean.gpu";

            _gateLabel.ForeColor = Color.FromArgb(255, 170, 110);
            _gateLabel.BackColor = Color.Transparent;
            _gateLabel.Visible = false;

            _oldLabel.ForeColor = Color.FromArgb(150, 150, 158);
            _oldLabel.BackColor = Color.Transparent;
            _oldLabel.Visible = false;

            _monitorToggle.Text = "Show system monitor";
            _monitorToggle.FlatStyle = FlatStyle.Flat;
            _monitorToggle.FlatAppearance.BorderSize = 0;
            _monitorToggle.ForeColor = Color.FromArgb(165, 165, 172);
            _monitorToggle.BackColor = BackColor;
            _monitorToggle.TextAlign = ContentAlignment.MiddleLeft;
            _monitorToggle.Size = new Size(180, 22);
            _monitorToggle.TabStop = false;
            _monitorToggle.Visible = false;
            _monitorToggle.Click += delegate { OnMonitorToggle(); };

            _monitorPanel.Visible = false;

            // Named profiles (A14): collect/apply every checkbox by its
            // stable Tag key.
            _profileBar.CollectSelections += CollectAllSelections;
            _profileBar.ApplyRequested += ApplyProfileSelections;

            _selectPanel.Controls.Add(_profileBar);
            _selectPanel.Controls.Add(_precheckLabel);
            _selectPanel.Controls.Add(_optTitle);
            foreach (CheckBox cb in _optBoxes) _selectPanel.Controls.Add(cb);
            _selectPanel.Controls.Add(_trayTitle);
            foreach (CheckBox cb in _trayBoxes) _selectPanel.Controls.Add(cb);
            _selectPanel.Controls.Add(_closeTray);
            _selectPanel.Controls.Add(_perfTitle);
            _selectPanel.Controls.Add(_freezeBox);
            _selectPanel.Controls.Add(_editFreezeList);
            _selectPanel.Controls.Add(_planBox);
            _selectPanel.Controls.Add(_wuPauseBox);
            _selectPanel.Controls.Add(_cleanTitle);
            _selectPanel.Controls.Add(_cleanWuBox);
            _selectPanel.Controls.Add(_cleanDismBox);
            _selectPanel.Controls.Add(_cleanDeepBox);
            _selectPanel.Controls.Add(_cleanGpuBox);
            _selectPanel.Controls.Add(_gateLabel);
            _selectPanel.Controls.Add(_oldLabel);
            _selectPanel.Controls.Add(_monitorToggle);
            _selectPanel.Controls.Add(_monitorPanel);
            Controls.Add(_selectPanel);
#endif

            Controls.AddRange(new Control[]
            {
                _title, _subtitle, _x, _bar, _status, _detail,
                _apply, _cancel, _restart, _logBtn, _close
            });

            Shown += delegate
            {
                EnterProbe();
            };

            _clock.Interval = 30;
            _clock.Tick += OnClock;
            _clock.Start();

            FormClosed += delegate
            {
                _clock.Dispose();
                // Eco-safe restore on any exit (v1.1): never leave a frozen
                // process, a paused Windows Update or a foreign power plan
                // behind. All steps are no-ops when nothing is active.
                SessionSafety.RestoreAll();
#if MODE_STANDARD
                DisposeOverlay();
                try { if (MonitorEngine.Running) MonitorEngine.Stop(); } catch { }
#endif
                AsusControl.Shutdown();
            };
        }

#if MODE_STANDARD
        // ---- selection stage helpers (v1.1, Wave 6) -------------------------

        private static CheckBox MakeSelectCheckBox(string text, bool ticked)
        {
            CheckBox cb = new CheckBox();
            cb.Text = text;
            cb.AutoSize = true;
            cb.ForeColor = Color.FromArgb(200, 200, 210);
            cb.BackColor = Color.Transparent;
            cb.Checked = ticked;
            cb.Visible = false;
            return cb;
        }

        private void ShowFreezeListEditor()
        {
            Log.Info("UI: freeze list editor opened");
            using (FreezeListEditorForm f = new FreezeListEditorForm())
            {
                f.ShowDialog(this);
            }
        }

        private void OnMonitorToggle()
        {
            bool show = !_monitorPanel.Visible;
            _monitorPanel.Visible = show;
            _monitorToggle.Text = show ? "Hide system monitor" : "Show system monitor";
            if (show) _monitorPanel.AttachToEngine();
            else _monitorPanel.DetachFromEngine();
            Log.Chan("MONITOR", "UI: system monitor section " + (show ? "expanded" : "collapsed"));
        }

        // Positions the tray group at the given panel-relative y. Used at the
        // top of the panel in result (tray-only) mode and inline in select mode.
        private void LayoutTrayGroup(int topY)
        {
            _trayTitle.SetBounds(0, topY, 0, 0, BoundsSpecified.Location);
            _trayBoxes[0].SetBounds(0, topY + 24, 0, 0, BoundsSpecified.Location);
            _trayBoxes[1].SetBounds(276, topY + 24, 0, 0, BoundsSpecified.Location);
            _trayBoxes[2].SetBounds(0, topY + 50, 0, 0, BoundsSpecified.Location);
            _trayBoxes[3].SetBounds(276, topY + 50, 0, 0, BoundsSpecified.Location);
            _trayBoxes[4].SetBounds(0, topY + 76, 0, 0, BoundsSpecified.Location);
            _closeTray.SetBounds(276, topY + 74, 150, 26);
        }

        // Starts the tray detection round: rows show "Scanning...", the
        // background thread detects, the rows populate. Used by both the
        // select stage and the result stage's tray-only section.
        private void BeginTrayDetect()
        {
            _trayTitle.Visible = true;
            _closeTray.Visible = true;
            foreach (CheckBox cb in _trayBoxes)
            {
                cb.Visible = true;
                cb.Text = "Scanning...";
                cb.Checked = false;
                cb.Enabled = false;
            }
            RunBg(delegate
            {
                TrayApps.Detect();
                SafeInvoke(delegate { PopulateTrayRows(); });
            });
        }

        private void PopulateTrayRows()
        {
            for (int i = 0; i < TrayApps.Known.Length && i < _trayBoxes.Count; i++)
            {
                TrayAppInfo a = TrayApps.Known[i];
                _trayBoxes[i].Visible = true;
                _trayBoxes[i].Text = a.Label + (a.Running ? "  (running)" : "  (not running)");
                _trayBoxes[i].Checked = a.Running;
                _trayBoxes[i].Enabled = a.Running;
            }
        }

        // Shows (select mode) or hides (result tray-only mode) every non-tray
        // part of the selection panel.
        private void SetSelectGroupsVisible(bool on)
        {
            _profileBar.Visible = on;
            _precheckLabel.Visible = on;
            _optTitle.Visible = on;
            foreach (CheckBox cb in _optBoxes) cb.Visible = on;
            _perfTitle.Visible = on;
            _freezeBox.Visible = on;
            _editFreezeList.Visible = on;
            _planBox.Visible = on;
            _wuPauseBox.Visible = on;
            _cleanTitle.Visible = on;
            _cleanWuBox.Visible = on;
            _cleanDismBox.Visible = on;
            _cleanDeepBox.Visible = on;
            _cleanGpuBox.Visible = on;
            foreach (CheckBox cb in _appCacheBoxes) cb.Visible = on;
            _gateLabel.Visible = on && _gateLabel.Text.Length > 0;
            _oldLabel.Visible = on && _oldLabel.Text.Length > 0;
            _monitorToggle.Visible = on;
            if (!on) _monitorPanel.Visible = false;
        }

        // Result stage, v1.0.22 behavior kept: the tray picker stays available
        // after the switch. It now lives at the top of the selection panel.
        private void EnterResultTraySection()
        {
            _selectPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _selectPanel.Location = new Point(24, 252);
            _selectPanel.Size = new Size(ClientSize.Width - 48, 112);
            _selectPanel.Visible = true;
            SetSelectGroupsVisible(false);
            LayoutTrayGroup(2);
            BeginTrayDetect();
        }

        private void BeginCloseTrayApps()
        {
            if (_trayBusy) return;
            List<TrayAppInfo> selected = new List<TrayAppInfo>();
            for (int i = 0; i < TrayApps.Known.Length && i < _trayBoxes.Count; i++)
            {
                if (_trayBoxes[i].Checked && TrayApps.Known[i].Running) selected.Add(TrayApps.Known[i]);
            }
            if (selected.Count == 0) return;

            _trayBusy = true;
            _closeTray.Enabled = false;
            Log.Chan("TRAY", "TrayApps: closing " + selected.Count + " selected app(s)");
            RunBg(delegate
            {
                foreach (TrayAppInfo a in selected) TrayApps.Close(a);
                TrayApps.Detect();
                SafeInvoke(delegate
                {
                    for (int i = 0; i < TrayApps.Known.Length && i < _trayBoxes.Count; i++)
                    {
                        TrayAppInfo a = TrayApps.Known[i];
                        _trayBoxes[i].Text = a.Label + (a.Running ? "  (running)" : "  (not running)");
                        _trayBoxes[i].Checked = a.Running;
                        _trayBoxes[i].Enabled = a.Running;
                    }
                    _trayBusy = false;
                    _closeTray.Enabled = true;
                });
            });
        }
#endif

        private void InitButton(Button b, string text, int width, int x, int height)
        {
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
            b.ForeColor = Color.FromArgb(220, 220, 226);
            b.BackColor = Color.FromArgb(42, 42, 49);
            b.Size = new Size(width, height);
            b.Location = new Point(x, ClientSize.Height - height - 18);
            b.TabStop = false;
            b.Visible = false;
        }

        // The exe's 256px icon artwork is embedded per-build as resource
        // "GpuModeSwitch.appicon.png" (see build.cmd).
        private static Bitmap LoadLogo()
        {
            try
            {
                Stream s = typeof(Program).Assembly.GetManifestResourceStream("GpuModeSwitch.appicon.png");
                if (s == null) return null;
                using (Bitmap tmp = new Bitmap(s))
                {
                    return new Bitmap(tmp);   // detached copy; the stream can go away
                }
            }
            catch
            {
                return null;
            }
        }

        private void OnClock(object sender, EventArgs e)
        {
            if (Opacity < 1) Opacity = Math.Min(1, Opacity + 0.07);
            _bar.Advance();
        }

        // Borderless window: edges resize, any bare interior area drags.
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            const int HTCLIENT = 1;
            const int HTCAPTION = 2;
            const int HTLEFT = 10;
            const int HTRIGHT = 11;
            const int HTTOP = 12;
            const int HTTOPLEFT = 13;
            const int HTTOPRIGHT = 14;
            const int HTBOTTOM = 15;
            const int HTBOTTOMLEFT = 16;
            const int HTBOTTOMRIGHT = 17;

            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if ((int)m.Result == HTCLIENT)
                {
                    int lp = m.LParam.ToInt32();
                    Point pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                    int e = 10;
                    bool l = pt.X <= e, r = pt.X >= ClientSize.Width - e;
                    bool t = pt.Y <= e, b = pt.Y >= ClientSize.Height - e;
                    if (t && l) m.Result = (IntPtr)HTTOPLEFT;
                    else if (t && r) m.Result = (IntPtr)HTTOPRIGHT;
                    else if (b && l) m.Result = (IntPtr)HTBOTTOMLEFT;
                    else if (b && r) m.Result = (IntPtr)HTBOTTOMRIGHT;
                    else if (l) m.Result = (IntPtr)HTLEFT;
                    else if (r) m.Result = (IntPtr)HTRIGHT;
                    else if (t) m.Result = (IntPtr)HTTOP;
                    else if (b) m.Result = (IntPtr)HTBOTTOM;
                    else m.Result = (IntPtr)HTCAPTION;
                }
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Region = new Region(UiShapes.RoundRect(0, 0, ClientSize.Width, ClientSize.Height, 26));
        }

        // ---- phase helpers -------------------------------------------------

        private void HideAllButtons()
        {
            _apply.Visible = _cancel.Visible = _restart.Visible = false;
            _logBtn.Visible = _close.Visible = false;
#if MODE_STANDARD
            _histBtn.Visible = _sessBtn.Visible = false;
#endif
        }

        private void EnterProbe()
        {
            _phase = UiPhase.Probe;
            HideAllButtons();
            _bar.Active = true;
            _bar.Visible = true;
            _status.ForeColor = Color.FromArgb(235, 235, 240);
            _status.Text = "Contacting ASUS hardware...";
            _detail.Text = "";
            Log.Info("UI: probing");

            RunBg(delegate
            {
                bool ok;
                string msg = AsusControl.Precheck(TargetEco, out ok);
                SafeInvoke(delegate
                {
                    if (!ok)
                    {
                        EnterResult(false, "ASUS hardware interface not found", msg);
                        return;
                    }
#if MODE_STANDARD
                    if (_autoMode)
                    {
                        // --auto: apply everything (all optimizations on) with
                        // no tray app closing and no selection stage.
                        Log.Info("UI: --auto given, applying right away");
                        BeginApply();
                        return;
                    }
                    EnterSelect(msg);
#else
                    if (!_confirmMode)
                    {
                        // One-click: flow straight from probing into applying.
                        Log.Info("UI: one-click mode, applying right away");
                        BeginApply();
                        return;
                    }
                    EnterConfirm(msg);
#endif
                });
            });
        }

        private void EnterConfirm(string precheckText)
        {
            _phase = UiPhase.Confirm;
            _bar.Active = false;
            HideAllButtons();
            _apply.Text = "Apply";
            _apply.Visible = true;
            _cancel.Visible = true;
            _status.ForeColor = Color.FromArgb(235, 235, 240);
            _status.Text = "Ready to apply " + (TargetEco ? "Eco Mode" : "Standard mode");
            _detail.Text = precheckText + "\n\n" +
                "Switching applies immediately and is reversible -\n" +
                "run the other app to switch back.";
            Log.Info("UI: waiting for Apply");
        }

#if MODE_STANDARD
        // Selection stage: every toggle group is shown at launch and the user
        // decides what GO applies. The v1.1 stage adds the performance and
        // storage-cleanup groups, named profiles and the live monitor, all
        // inside one scrollable panel (the form grows to make room and
        // AutoScroll covers short screens).
        private void EnterSelect(string precheckText)
        {
            _phase = UiPhase.Confirm;
            _bar.Active = false;
            HideAllButtons();
            GrowForSelection();
            _detail.Visible = false;
            _detail.Text = "";
            _apply.Text = "GO";
            _apply.Visible = true;
            _cancel.Visible = true;
            _status.ForeColor = Color.FromArgb(235, 235, 240);
            _status.Text = "Ready - choose optimizations, then press GO";
            LayoutSelectContent(precheckText);
            SetSelectGroupsVisible(true);
            _selectPanel.Visible = true;   // v1.1.1: created hidden in the ctor and never shown here - the whole stage was invisible, so GO was unreachable
            EnsureMonitorEngine();
            _monitorPanel.AttachToEngine();
            StartMeasureOnce();
            BeginTrayDetect();
            Log.Info("UI: selection stage");
        }

        // The 640-high window cannot fit the v1.1 selection stage; grow once
        // (clamped to the working area - the panel scrolls if still short).
        // The width grows too: the ProfileBar strip needs ~530 px, more than
        // the 512 px the 560-wide window offered.
        private void GrowForSelection()
        {
            int wantH = 880;
            int wantW = 600;
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            if (wantH > wa.Height - 40) wantH = wa.Height - 40;
            if (wantH < ClientSize.Height) wantH = ClientSize.Height;   // never shrink
            if (wantW > wa.Width - 40) wantW = ClientSize.Width;        // narrow screens keep the old width
            ClientSize = new Size(wantW, wantH);
            _detail.Size = new Size(ClientSize.Width - 48, Math.Max(170, ClientSize.Height - 368 - 96));
            Location = new Point(
                wa.Left + Math.Max(0, (wa.Width - Width) / 2),
                wa.Top + Math.Max(0, (wa.Height - Height) / 2));
        }

        // Lays the selection panel out top-to-bottom (once per run; the
        // app-cache checkboxes are created here because the installed-target
        // list is cheap to resolve, while the measured sizes arrive
        // asynchronously via ApplyMeasurements).
        private void LayoutSelectContent(string precheckText)
        {
            if (_selectLaidOut) return;
            _selectLaidOut = true;

            const int x2 = 276;      // right checkbox column (mirrors the v1.0.22 spots)
            int pw = ClientSize.Width - 48;

            _selectPanel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _selectPanel.Location = new Point(24, 150);
            _selectPanel.Size = new Size(pw, ClientSize.Height - 150 - 62);

            int y = 2;
            _profileBar.Size = new Size(pw, 44);
            _profileBar.Location = new Point(0, y);
            y += 50;

            _precheckLabel.Text = precheckText == null ? "" : precheckText;
            int ph = MeasureWrappedHeight(_precheckLabel.Text, pw);
            _precheckLabel.SetBounds(0, y, pw, ph);
            y += ph + 10;

            // System optimizations (v1.0.22 group, ticked by default).
            _optTitle.Location = new Point(0, y); y += 24;
            y = PlacePair(_optBoxes[0], _optBoxes[1], y);
            y = PlacePair(_optBoxes[2], _optBoxes[3], y);
            _optBoxes[4].SetBounds(0, y, 0, 0, BoundsSpecified.Location);
            y += 30;

            // Tray apps detected (v1.0.22 group).
            _trayTitle.Location = new Point(0, y); y += 24;
            y = PlacePair(_trayBoxes[0], _trayBoxes[1], y);
            y = PlacePair(_trayBoxes[2], _trayBoxes[3], y);
            _trayBoxes[4].SetBounds(0, y, 0, 0, BoundsSpecified.Location);
            _closeTray.SetBounds(x2, y - 2, 150, 26);
            y += 32;

            // Performance (v1.1): freeze / plan / WU pause, "Edit list..." on
            // the freeze row.
            _perfTitle.Location = new Point(0, y); y += 24;
            _freezeBox.SetBounds(0, y, 0, 0, BoundsSpecified.Location);
            _editFreezeList.SetBounds(x2, y - 2, 110, 25);
            y += 28;
            y = PlacePair(_planBox, _wuPauseBox, y);
            y += 6;

            // Storage cleanup (v1.1): fixed boxes, then one checkbox per
            // installed app-cache target whose cache dirs resolved non-empty.
            _cleanTitle.Location = new Point(0, y); y += 24;
            y = PlacePair(_cleanWuBox, _cleanDismBox, y);
            y = PlacePair(_cleanDeepBox, _cleanGpuBox, y);

            _appCacheBoxes.Clear();
            List<AppCacheTarget> targets = AppCacheCleaner.Targets();
            for (int i = 0; i < targets.Count; i++)
            {
                AppCacheTarget t = targets[i];
                if (t.CacheDirs == null || t.CacheDirs.Length == 0) continue;
                CheckBox cb = MakeSelectCheckBox("Clean " + t.Name + " cache", false);
                cb.Tag = "clean.appcache." + t.Name;    // stable profile key
                cb.Name = t.Name;                       // target name for the clean step
                _appCacheBoxes.Add(cb);
                _selectPanel.Controls.Add(cb);
            }
            for (int i = 0; i < _appCacheBoxes.Count; i++)
            {
                int row = i / 2, col = i % 2;
                _appCacheBoxes[i].SetBounds(col == 0 ? 0 : x2, y + row * 26, 0, 0, BoundsSpecified.Location);
            }
            y += ((_appCacheBoxes.Count + 1) / 2) * 26 + 4;

            // Gate warning + report-only note go right under the cleanup
            // group; their text arrives with the background measurement.
            _cleanGroupEndY = y;
            LayoutMonitorSectionAt(y);
        }

        private static int PlacePair(CheckBox left, CheckBox right, int y)
        {
            left.SetBounds(0, y, 0, 0, BoundsSpecified.Location);
            if (right != null) right.SetBounds(276, y, 0, 0, BoundsSpecified.Location);
            return y + 26;
        }

        private int MeasureWrappedHeight(string text, int width)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            Size s = TextRenderer.MeasureText(text, Font, new Size(width, 100000), TextFormatFlags.WordBreak);
            return Math.Max(18, s.Height + 2);
        }

        // The monitor section is the last thing in the panel; it moves down
        // whenever a gate warning / report-only note appears under the
        // cleanup group.
        private void LayoutMonitorSectionAt(int y)
        {
            int pw = _selectPanel.Width;
            _monitorToggle.SetBounds(0, y, 180, 22);
            _monitorPanel.SetBounds(0, y + 26, Math.Min(pw, 400), 195);
        }

        private void ReflowInfoLabels()
        {
            int pw = _selectPanel.Width;
            int yy = _cleanGroupEndY;
            if (_oldLabel.Text.Length > 0)
            {
                int h = MeasureWrappedHeight(_oldLabel.Text, pw);
                _oldLabel.SetBounds(0, yy, pw, h);
                yy += h + 2;
            }
            if (_gateLabel.Text.Length > 0)
            {
                int h = MeasureWrappedHeight(_gateLabel.Text, pw);
                _gateLabel.SetBounds(0, yy, pw, h);
                yy += h + 4;
            }
            LayoutMonitorSectionAt(yy);
        }

        private static string SizeSuffix(long bytes)
        {
            return bytes > 0 ? " (" + StorageCleaner.FormatBytes(bytes) + ")" : "";
        }

        private static long SumBytes(List<CleanCategory> cats, string kind)
        {
            long sum = 0;
            if (cats == null) return 0;
            foreach (CleanCategory c in cats)
            {
                if (c.Kind == kind) sum += c.Bytes;
            }
            return sum;
        }

        // Background measurement (the RunBg pattern) - strictly read-only.
        // When it lands: checkbox captions get their measured sizes, gate
        // reasons disable the whole cleanup group with the reasons shown,
        // and the report-only previous-installations note appears (D3 Tier 3).
        private void StartMeasureOnce()
        {
            if (_measureStarted) return;
            _measureStarted = true;
            RunBg(delegate
            {
                List<CleanCategory> all = StorageAnalyzer.MeasureAll();
                List<CleanCategory> tier1 = new List<CleanCategory>();
                foreach (CleanCategory c in all)
                {
                    // GPU shader-cache paths are measured again by their
                    // owner (GpuTools, D9) - keep the tier-1 kinds here.
                    if (c.Kind != CleanCategory.KindGpu) tier1.Add(c);
                }
                List<CleanCategory> deep = DeepClean.Measure();
                List<CleanCategory> gpu = GpuTools.Measure();
                List<CleanCategory> app = AppCacheCleaner.Measure();

                List<string> runningTargets = new List<string>();
                try
                {
                    List<string> running = AppCacheCleaner.RunningApps();
                    foreach (AppCacheTarget t in AppCacheCleaner.Targets())
                    {
                        foreach (string pn in t.ProcessNames)
                        {
                            foreach (string rn in running)
                            {
                                if (string.Equals(rn, pn, StringComparison.OrdinalIgnoreCase))
                                {
                                    runningTargets.Add(t.Name);
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("measure: running-app scan failed - " + ex.Message);
                }

                List<string> gates = StorageAnalyzer.CheckGates();
                SafeInvoke(delegate { ApplyMeasurements(tier1, deep, gpu, app, runningTargets, gates); });
            });
        }

        private void ApplyMeasurements(List<CleanCategory> tier1, List<CleanCategory> deep,
            List<CleanCategory> gpu, List<CleanCategory> app, List<string> runningTargets,
            List<string> gates)
        {
            _tier1Measured = tier1;
            _gateReasons = gates;

            _cleanWuBox.Text = "Windows Update cache purge" + SizeSuffix(SumBytes(tier1, CleanCategory.KindWu));

            long deepBytes = SumBytes(tier1, CleanCategory.KindDeepClean);
            foreach (CleanCategory c in deep)
            {
                if (c.Name == DeepClean.CatPerUserErrorReports ||
                    c.Name == DeepClean.CatSetupUpgradeLogs) deepBytes += c.Bytes;
            }
            _cleanDeepBox.Text = "Deep clean" + SizeSuffix(deepBytes);

            long shaderBytes = 0;
            foreach (CleanCategory c in gpu)
            {
                // Driver installer leftovers are confirm-flagged and not part
                // of this checkbox - only the shader-cache paths count here.
                if (c.Name.IndexOf("shader cache", StringComparison.OrdinalIgnoreCase) >= 0) shaderBytes += c.Bytes;
            }
            _cleanGpuBox.Text = "GPU shader caches" + SizeSuffix(shaderBytes);

            foreach (CheckBox cb in _appCacheBoxes)
            {
                string name = cb.Name;
                long bytes = 0;
                bool running = false;
                foreach (CleanCategory c in app)
                {
                    if (c.Name == name) bytes = c.Bytes;
                }
                foreach (string rn in runningTargets)
                {
                    if (string.Equals(rn, name, StringComparison.OrdinalIgnoreCase)) running = true;
                }
                cb.Text = "Clean " + name + " cache" + SizeSuffix(bytes) + (running ? " (app running)" : "");
                if (running) cb.Checked = false;    // a running app starts UNchecked
            }

            // Report-only previous Windows installations (D3 Tier 3): shown
            // as information, never offered for deletion.
            foreach (CleanCategory c in deep)
            {
                if (c.Name == DeepClean.CatPreviousInstallations && c.Bytes > 0)
                {
                    _oldLabel.Text = "also found: previous Windows installations " +
                        StorageCleaner.FormatBytes(c.Bytes) + " (report only - never deleted)";
                }
            }

            // D7 gates: any block reason disables the whole cleanup group and
            // shows why. The cleaners re-check the gates themselves at GO
            // time (belt and braces).
            if (gates != null && gates.Count > 0)
            {
                _gateLabel.Text = "Cleanup unavailable: " + string.Join("; ", gates.ToArray());
                _cleanWuBox.Enabled = false;
                _cleanDismBox.Enabled = false;
                _cleanDeepBox.Enabled = false;
                _cleanGpuBox.Enabled = false;
                foreach (CheckBox cb in _appCacheBoxes) cb.Enabled = false;
                foreach (string reason in gates) Log.Warn("cleanup gate: " + reason);
            }

            ReflowInfoLabels();
        }
#endif

        private void BeginApply()
        {
            if (_phase != UiPhase.Confirm && _phase != UiPhase.Probe) return;
            _phase = UiPhase.Applying;
            HideAllButtons();
            _bar.Active = true;
            _bar.Visible = true;
            _status.ForeColor = Color.FromArgb(235, 235, 240);
            _status.Text = "Applying " + (TargetEco ? "Eco Mode" : "Standard mode") + "...";
            _detail.Text = "";
            Log.Info("UI: applying");
            System.Diagnostics.Stopwatch goWatch = System.Diagnostics.Stopwatch.StartNew();

#if MODE_STANDARD
            // Capture the launch-time selections (UI thread).
            //
            // --auto semantics (unchanged since v1.0.22): --auto applies ONLY
            // the system optimizations + tray-app closing + GPU switch. The
            // v1.1 performance and storage-cleanup groups are NEVER applied
            // via --auto - freezing other apps' processes, switching power
            // plans, pausing Windows Update and deleting files must never
            // happen unattended (documented in README.md).
            bool sessionFeatures = !_autoMode;
            List<TrayAppInfo> toClose = GatherSelectedTrayApps();
            bool fGameMode = _optBoxes[0].Checked;
            bool fDnd = _optBoxes[1].Checked;
            bool fDvr = _optBoxes[2].Checked;
            bool fThrottle = _optBoxes[3].Checked;
            bool fServices = _optBoxes[4].Checked;
            bool gatesClear = _gateReasons == null || _gateReasons.Count == 0;
            bool pFreeze = sessionFeatures && _freezeBox.Checked;
            bool pPlan = sessionFeatures && _planBox.Checked;
            bool pWuPause = sessionFeatures && _wuPauseBox.Checked;
            bool cWu = sessionFeatures && gatesClear && _cleanWuBox.Checked;
            bool cDism = sessionFeatures && gatesClear && _cleanDismBox.Checked;
            bool cDeep = sessionFeatures && gatesClear && _cleanDeepBox.Checked;
            bool cGpu = sessionFeatures && gatesClear && _cleanGpuBox.Checked;
            List<string> cleanApps = new List<string>();
            if (sessionFeatures && gatesClear)
            {
                foreach (CheckBox cb in _appCacheBoxes)
                {
                    if (cb.Checked) cleanApps.Add(cb.Name);
                }
            }
            _selectPanel.Visible = false;
            _detail.Visible = false;
            Log.Info("UI: selections - GameMode=" + fGameMode + " DND=" + fDnd + " DVR=" + fDvr +
                        " Throttle=" + fThrottle + " Services=" + fServices + " TrayToClose=" + toClose.Count +
                        " Freeze=" + pFreeze + " Plan=" + pPlan + " WUPause=" + pWuPause +
                        " Cleanup(WU=" + cWu + " DISM=" + cDism + " Deep=" + cDeep + " GPU=" + cGpu +
                        " AppCaches=" + cleanApps.Count + ")");
#endif

            RunBg(delegate
            {
#if MODE_STANDARD
                foreach (TrayAppInfo a in toClose) TrayApps.Close(a);
#endif
                SwitchOutcome r;
                try
                {
                    r = AsusControl.SwitchTo(TargetEco);
                }
                catch (Exception ex)
                {
                    Log.Error("UNEXPECTED ERROR", ex);
                    r = new SwitchOutcome();
                    r.Headline = "Unexpected error";
                    r.Detail = ex.Message + "\n\nFull details: View log.";
                }
#if MODE_ECO
                // Eco-safe restore (belt and braces, v1.1): make sure no
                // earlier Go Time session leaves frozen processes, a foreign
                // power plan or a paused Windows Update behind.
                SessionSafety.RestoreAll();
#endif
#if MODE_STANDARD
                string prep = "";
                string cleanupSummary = "";
                if (r.Ok)
                {
                    prep = GamePrep.ApplyForGaming(fGameMode, fDnd, fDvr, fThrottle, fServices);
                    if (prep.Length > 0) r.Detail += "\n" + prep;
                }
                if (r.Ok && sessionFeatures)
                {
                    GoExtraResult extra = RunGoSession(pFreeze, pPlan, pWuPause,
                        cWu, cDism, cDeep, cGpu, cleanApps);
                    if (extra.DetailLines.Length > 0) r.Detail += extra.DetailLines;
                    cleanupSummary = extra.CleanupBlock;

                    // Session history (A15): record the whole GO run.
                    SessionRecord rec = new SessionRecord();
                    rec.App = "Go Time";
                    rec.Mode = "Standard";
                    rec.ActionsApplied = new List<string>();
                    rec.ActionsApplied.Add("GPU switch: " + r.Headline);
                    foreach (TrayAppInfo a in toClose) rec.ActionsApplied.Add("Closed tray app: " + a.Label);
                    if (prep.Length > 0) rec.ActionsApplied.Add("Optimizations: " + prep);
                    foreach (string act in extra.Actions) rec.ActionsApplied.Add(act);
                    rec.SpaceFreedByCategory = extra.Freed;
                    rec.DurationSec = Math.Round(goWatch.Elapsed.TotalSeconds, 1);
                    rec.ErrorCount = extra.Errors;
                    rec.Result = r.Headline + (extra.TotalFreed > 0
                        ? " (free: " + StorageCleaner.FormatBytes(extra.TotalFreed) + ")"
                        : "");
                    SessionHistory.Append(rec);
                }
#endif
                SafeInvoke(delegate
                {
#if MODE_STANDARD
                    _cleanupSummary = cleanupSummary;
                    if (r.Ok && sessionFeatures) AfterGoSuccess();
#endif
                    EnterResult(r.Ok, r.Headline, r.Detail, r);
                });
            });
        }

#if MODE_STANDARD
        // Everything RunGoSession collected for the session record and the
        // result UI.
        private sealed class GoExtraResult
        {
            public List<string> Actions = new List<string>();
            public Dictionary<string, long> Freed = new Dictionary<string, long>();
            public List<string> CleanupLines = new List<string>();
            public int Errors;
            public long TotalFreed;
            public string DetailLines = "";
            public string CleanupBlock = "";
        }

        // Runs the v1.1 session features after the GPU switch + optimizations
        // succeeded, in this order: freeze -> power plan -> WU pause ->
        // cleanup (Tier 1 -> DISM -> deep -> GPU caches -> app caches). Every
        // step is individually try/caught, logged, and never aborts the run;
        // the GPU switch result stays the headline. Unticked reversible
        // features are actively restored (the v1.0.22 "unticked is restored"
        // semantic). Runs on the GO background thread; the cleaners re-check
        // the D7 gates themselves.
        private GoExtraResult RunGoSession(bool pFreeze, bool pPlan, bool pWuPause,
            bool cWu, bool cDism, bool cDeep, bool cGpu, List<string> cleanApps)
        {
            GoExtraResult outc = new GoExtraResult();

            // a) background process freezer (A12)
            if (pFreeze)
            {
                try
                {
                    FreezeResult fr = ProcessFreezer.FreezeSelected();
                    outc.Actions.Add("Background apps frozen: " + fr.Summary);
                    outc.DetailLines += "\nBackground apps: " + fr.Summary;
                    if (fr.Failed > 0) outc.Errors++;
                }
                catch (Exception ex)
                {
                    Log.Error("freeze: session freeze failed", ex);
                    outc.Errors++;
                    outc.DetailLines += "\nBackground apps: freeze failed (see log)";
                }
            }
            else
            {
                ProcessFreezer.ResumeAllSafe();     // unticked = actively restored
            }

            // b) Ultimate Performance plan (A13)
            if (pPlan)
            {
                try
                {
                    if (PowerPlans.SetUltimate())
                    {
                        outc.Actions.Add("Power plan: Ultimate Performance activated");
                        outc.DetailLines += "\nPower plan: Ultimate Performance active";
                    }
                    else
                    {
                        outc.Errors++;
                        outc.DetailLines += "\nPower plan: switch failed (see log)";
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("power: ultimate plan switch failed", ex);
                    outc.Errors++;
                    outc.DetailLines += "\nPower plan: switch failed (see log)";
                }
            }
            else
            {
                PowerPlans.RestorePrevious();       // unticked = actively restored
            }

            // c) session-scoped Windows Update pause (A13)
            if (pWuPause)
            {
                try
                {
                    WuPause.PauseUpdates();
                    outc.Actions.Add("Windows Update: paused for this session");
                    outc.DetailLines += "\nWindows Update: paused for this session";
                }
                catch (Exception ex)
                {
                    Log.Error("wu-pause: session pause failed", ex);
                    outc.Errors++;
                    outc.DetailLines += "\nWindows Update: pause failed (see log)";
                }
            }
            else
            {
                WuPause.ResumeUpdates();            // unticked = actively restored
            }

            // d) cleanup chain (A7/A8/A9/A10/A11)
            bool anyCleanup = cWu || cDism || cDeep || cGpu ||
                (cleanApps != null && cleanApps.Count > 0);
            if (!anyCleanup) return outc;

            List<string> gateReasons = StorageAnalyzer.CheckGates();   // GO-time re-check
            if (gateReasons.Count > 0)
            {
                outc.Errors++;
                outc.DetailLines += "\nCleanup skipped - safety gates blocked it (see log)";
                return outc;
            }

            // d1) Tier 1 (StorageCleaner, A7): the WU-kind categories when the
            // WU box is ticked, the tier-1 deepclean-kind categories (temp,
            // WER, user temp, crash dumps, thumbnails) with the deep box.
            if (cWu || cDeep)
            {
                List<CleanCategory> sel = new List<CleanCategory>();
                if (_tier1Measured != null)
                {
                    foreach (CleanCategory c in _tier1Measured)
                    {
                        if (c.Kind == CleanCategory.KindWu && cWu) sel.Add(c);
                        else if (c.Kind == CleanCategory.KindDeepClean && cDeep) sel.Add(c);
                    }
                }
                if (sel.Count > 0)
                {
                    try
                    {
                        List<CleanResult> res;
                        long total;
                        bool ok = StorageCleaner.Clean(sel, out res, out total);
                        CollectCleanResults(outc, res);
                        outc.TotalFreed += total;
                        if (!ok) outc.Errors++;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("clean: tier 1 cleanup failed", ex);
                        outc.Errors++;
                    }
                }
            }

            // d2) DISM component store (A8) - analyzes first itself (D3).
            if (cDism)
            {
                try
                {
                    string tail;
                    bool ok = ComponentStore.RunCleanup(out tail);
                    if (ok)
                    {
                        outc.Actions.Add("DISM component store cleanup: completed");
                        outc.CleanupLines.Add("Component store (DISM): cleanup finished");
                    }
                    else
                    {
                        outc.Errors++;
                        outc.Actions.Add("DISM component store cleanup: failed (see log)");
                        outc.CleanupLines.Add("Component store (DISM): failed (see log)");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("dism: cleanup failed", ex);
                    outc.Errors++;
                    outc.CleanupLines.Add("Component store (DISM): failed (see log)");
                }
            }

            // d3) deep clean (A10) - fresh measure, the report-only category
            // is never selected (D3 Tier 3).
            if (cDeep)
            {
                try
                {
                    List<CleanCategory> meas = DeepClean.Measure();
                    List<CleanCategory> sel = new List<CleanCategory>();
                    foreach (CleanCategory c in meas)
                    {
                        if (c.Name == DeepClean.CatPerUserErrorReports ||
                            c.Name == DeepClean.CatSetupUpgradeLogs) sel.Add(c);
                    }
                    if (sel.Count > 0)
                    {
                        List<CleanResult> res;
                        long total;
                        bool ok = DeepClean.Clean(sel, out res, out total);
                        CollectCleanResults(outc, res);
                        outc.TotalFreed += total;
                        if (!ok) outc.Errors++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("deepclean: cleanup failed", ex);
                    outc.Errors++;
                }
            }

            // d4) GPU shader caches (A11) - driver leftovers stay (the
            // confirm flag is deliberately not offered in the default flow).
            if (cGpu)
            {
                try
                {
                    List<CleanCategory> meas = GpuTools.Measure();
                    List<CleanResult> res;
                    long total;
                    bool ok = GpuTools.Clean(meas, false, out res, out total);
                    CollectCleanResults(outc, res);
                    outc.TotalFreed += total;
                    if (!ok) outc.Errors++;
                }
                catch (Exception ex)
                {
                    Log.Error("gputools: cleanup failed", ex);
                    outc.Errors++;
                }
            }

            // d5) per-app caches (A9, CACHE-ONLY per D5). The cleaner skips
            // and warns for any target whose app is running.
            if (cleanApps != null && cleanApps.Count > 0)
            {
                try
                {
                    long total;
                    List<AppCacheCleanResult> res = AppCacheCleaner.Clean(cleanApps, out total);
                    foreach (AppCacheCleanResult cr in res)
                    {
                        outc.Freed[cr.Name + " cache"] = cr.BytesFreed;
                        outc.CleanupLines.Add(cr.Name + " cache: " +
                            StorageCleaner.FormatBytes(cr.BytesFreed) + " freed, " +
                            cr.FilesDeleted + " files deleted" +
                            (cr.FilesSkipped > 0 ? ", " + cr.FilesSkipped + " skipped" : ""));
                    }
                    outc.TotalFreed += total;
                    outc.Actions.Add("App caches cleaned: " + cleanApps.Count + " target(s), " +
                        StorageCleaner.FormatBytes(total) + " freed");
                }
                catch (Exception ex)
                {
                    Log.Error("appcache: cleanup failed", ex);
                    outc.Errors++;
                }
            }

            // Result-stage summary block (compact: capped category lines).
            StringBuilder sb = new StringBuilder();
            sb.Append("Storage cleanup: ").Append(outc.TotalFreed > 0
                ? StorageCleaner.FormatBytes(outc.TotalFreed) + " freed"
                : "nothing to free");
            int shown = 0;
            foreach (string line in outc.CleanupLines)
            {
                if (shown == 12)
                {
                    sb.Append("\n... and ").Append(outc.CleanupLines.Count - shown)
                      .Append(" more (View log)");
                    break;
                }
                sb.Append("\n - ").Append(line);
                shown++;
            }
            outc.CleanupBlock = sb.ToString();
            return outc;
        }

        // Merges one cleaner's results into the record + summary block.
        private static void CollectCleanResults(GoExtraResult outc, List<CleanResult> res)
        {
            if (res == null) return;
            foreach (CleanResult cr in res)
            {
                if (cr.BytesFreed <= 0 && cr.FilesDeleted <= 0 &&
                    (cr.Notes == null || cr.Notes.Length == 0)) continue;
                outc.Freed[cr.CategoryName] = cr.BytesFreed;
                outc.CleanupLines.Add(cr.CategoryName + ": " + cr.Summary);
            }
        }

        // The GO session is active: this form owns the one MonitorEngine for
        // the whole session (D6) and the session tray (D4) is created with
        // its five callbacks.
        private void AfterGoSuccess()
        {
            EnsureMonitorEngine();
            if (_tray != null) return;
            _tray = new SessionTray(
                delegate { Show(); Activate(); },           // open window
                BeginEcoRestore,                            // restore (eco-safe)
                ToggleOverlay,                              // overlay toggle
                SessionStatusText,                          // status balloon text
                BeginExitApp);                              // clean shutdown
            SessionSafety.ActiveTray = _tray;
            _tray.Show("Go Time session active");
            Log.Chan("TRAY", "session tray created (Go Time session active)");
        }

        private void EnsureMonitorEngine()
        {
            if (!MonitorEngine.Running) MonitorEngine.Start(2000);
        }

        // Tray "Restore (Eco Mode)": the eco-safe session restore (see
        // SessionSafety) - background apps resumed, previous power plan back,
        // Windows Update resumed, tray gone. Switching the GPU to Eco itself
        // stays Eco Mode.exe's one-click job (D4: the suite stays two exes).
        private void BeginEcoRestore()
        {
            RunBg(delegate
            {
                SessionSafety.RestoreAll();
                SafeInvoke(delegate
                {
                    _tray = null;
                    DisposeOverlay();
                    try { if (MonitorEngine.Running) MonitorEngine.Stop(); } catch { }
                    _phase = UiPhase.Result;
                    HideAllButtons();
                    _bar.Active = false;
                    _bar.Visible = false;
                    _status.Text = "Session restored";
                    _status.ForeColor = Color.FromArgb(76, 195, 138);
                    _detail.Visible = true;
                    _detail.Text = "Background apps resumed, previous power plan restored, " +
                        "Windows Update resumed.\n\nTo switch the GPU to Eco, run Eco Mode.exe.";
                    _logBtn.Visible = true;
                    _histBtn.Visible = true;
                    _sessBtn.Visible = true;
                    _close.Visible = true;
                    Log.Info("UI: session restored to the eco-safe state");
                });
            });
        }

        // Tray "Exit": eco-safe restore first, then close for good.
        private void BeginExitApp()
        {
            RunBg(delegate
            {
                SessionSafety.RestoreAll();
                SafeInvoke(delegate
                {
                    _tray = null;
                    DisposeOverlay();
                    try { if (MonitorEngine.Running) MonitorEngine.Stop(); } catch { }
                    Close();
                });
            });
        }

        private void ToggleOverlay()
        {
            if (_overlay == null || _overlay.IsDisposed)
            {
                _overlay = new MonitorOverlayForm();
                _overlay.Attach();
                _overlay.Show();
                EnsureMonitorEngine();   // exactly one engine owner: this form
            }
            else
            {
                _overlay.Toggle();
            }
        }

        private void DisposeOverlay()
        {
            try
            {
                if (_overlay != null && !_overlay.IsDisposed) _overlay.Dispose();
            }
            catch { }
            _overlay = null;
        }

        // One-line status for the tray balloon: GPU state (from
        // AsusControl.DescribeState) + the latest live monitor sample.
        private string SessionStatusText()
        {
            string gpuLine = "";
            try
            {
                string[] stateLines = AsusControl.DescribeState().Replace("\r\n", "\n").Split('\n');
                foreach (string l in stateLines)
                {
                    string t = l.Trim();
                    if (t.StartsWith("dGPU power:", StringComparison.Ordinal)) { gpuLine = t; break; }
                }
            }
            catch { }
            string extra = "";
            MonitorSample s = MonitorEngine.LastSample;
            if (s != null)
            {
                extra = "CPU " + s.CpuPercent.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%" +
                        " | RAM " + s.RamText;
                if (s.HasGpu) extra += " | GPU " + s.GpuPercent.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
            }
            string status = "Standard mode - " + (gpuLine.Length > 0 ? gpuLine : "dGPU state unknown");
            if (extra.Length > 0) status += " | " + extra;
            return status;
        }

        // ProfileBar (A14): gather every keyed checkbox. Keys are the stable
        // Tag values set at construction (opt.*, tray.*, perf.*, clean.*).
        private Dictionary<string, bool> CollectAllSelections()
        {
            Dictionary<string, bool> map = new Dictionary<string, bool>();
            foreach (Control c in _selectPanel.Controls)
            {
                CheckBox cb = c as CheckBox;
                if (cb == null || cb.Tag == null) continue;
                map[(string)cb.Tag] = cb.Checked;
            }
            return map;
        }

        // ProfileBar Apply: set every checkbox by key. Unknown keys are
        // ignored with a log line; disabled (gate-blocked) cleanup boxes are
        // left alone - a saved profile must not resurrect a cleanup the
        // safety gates refused.
        private void ApplyProfileSelections(string name, Dictionary<string, bool> selections)
        {
            if (selections == null) return;
            foreach (KeyValuePair<string, bool> kv in selections)
            {
                CheckBox target = null;
                foreach (Control c in _selectPanel.Controls)
                {
                    CheckBox cb = c as CheckBox;
                    if (cb != null && cb.Tag != null &&
                        string.Equals((string)cb.Tag, kv.Key, StringComparison.Ordinal))
                    {
                        target = cb;
                        break;
                    }
                }
                if (target == null)
                {
                    Log.Chan("PROFILE", "profile apply: unknown key '" + kv.Key + "' ignored");
                    continue;
                }
                if (!target.Enabled)
                {
                    Log.Chan("PROFILE", "profile apply: '" + kv.Key + "' is disabled (safety gates) - left unchanged");
                    continue;
                }
                target.Checked = kv.Value;
            }
            Log.Chan("PROFILE", "profile applied: " + name);
        }

        private List<TrayAppInfo> GatherSelectedTrayApps()
        {
            List<TrayAppInfo> list = new List<TrayAppInfo>();
            for (int i = 0; i < TrayApps.Known.Length && i < _trayBoxes.Count; i++)
            {
                if (_trayBoxes[i].Checked && TrayApps.Known[i].Running) list.Add(TrayApps.Known[i]);
            }
            return list;
        }
#endif

        private void EnterResult(bool ok, string headline, string detail)
        {
            EnterResult(ok, headline, detail, null);
        }

        private void EnterResult(bool ok, string headline, string detail, SwitchOutcome r)
        {
            _phase = UiPhase.Result;
            _last = r;
            HideAllButtons();
            _bar.Active = false;
            _bar.Visible = false;
            _detail.Visible = true;
            _status.Text = (ok ? "OK - " : "Failed - ") + headline;
            _status.ForeColor = ok ? _accent : Color.FromArgb(255, 120, 120);
            _detail.Text = detail;
#if MODE_STANDARD
            if (_cleanupSummary.Length > 0)
            {
                _detail.Text = detail + "\n\n" + _cleanupSummary;
                _cleanupSummary = "";
            }
#endif
            _logBtn.Visible = true;
            _logBtn.FlatAppearance.BorderColor = ok ? Color.FromArgb(90, 90, 98) : _accent;
            _close.Visible = true;
#if MODE_STANDARD
            _histBtn.Visible = true;
            _sessBtn.Visible = true;
#endif
            if (r != null && r.Ok && r.NeedsRestart) _restart.Visible = true;
#if MODE_STANDARD
            EnterResultTraySection();
#endif
        }

        // ---- plumbing ------------------------------------------------------

        private void RunBg(ThreadStart work)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { work(); }
                catch (Exception ex)
                {
                    Log.Error("BACKGROUND ERROR", ex);
#if MODE_STANDARD
                    // Abnormal end of a background step (v1.1): eco-safe
                    // restore so a half-applied session is never left behind.
                    try { SessionSafety.RestoreAll(); } catch { }
#endif
                    SafeInvoke(delegate
                    {
                        EnterResult(false, "Unexpected error",
                            ex.Message + "\n\nFull details: View log.");
                    });
                }
            });
        }

        private void SafeInvoke(MethodInvoker mi)
        {
            try { Invoke(mi); }
            catch { }   // form gone while a background step finished - nothing to do
        }

        private void OnRestart(object sender, EventArgs e)
        {
            DialogResult dr = MessageBox.Show(
                "Restart the laptop now?\n\nMake sure your work is saved - Windows will restart in a few seconds.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (dr != DialogResult.Yes) return;

            try
            {
                Process.Start("shutdown.exe", "/r /t 5 /c \"Applying GPU mode change\"");
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not start a restart: " + ex.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private void TryDarkTitleBar()
        {
            try
            {
                int on = 1;
                DwmSetWindowAttribute(Handle, 20, ref on, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
            }
            catch
            {
                // older Windows without dark title bars - not a problem
            }
        }
    }
}
