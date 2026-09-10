//  Forms.cs  (v1.2.0 - unified app)
//  ---------------------------
//  The windows of the unified GPU Mode Switch app. MainForm (v1.2.0
//  redesign): ONE borderless window with a sidebar rail (Home / Optimize /
//  Monitor / History / Theme - the v1.3.0 palette page), a gradient header
//  with live status chips, big mode
//  cards on Home (GO TIME / ECO MODE - the active mode glows), the Go Time
//  selection deck with animated ToggleSwitches in rounded cards (gate-
//  locked cleanup rows show a lock banner with the reasons - the v1.1.1
//  field report), a monitor page over the shared MonitorPanel, a history
//  page (log viewer / log browser / session history) and a busy/result
//  overlay shared by probe/apply/result. The apply chain, GO session
//  features, session tray, overlay and eco-safe restore are the proven
//  v1.1 logic with the MODE_STANDARD/MODE_ECO compile-time split removed:
//  the mode is chosen at runtime (cards, --gotime/--eco flags or the
//  session tray's eco item).
//
//  Unchanged from v1.1: the log viewer (LogForm), the eco-safe session
//  restore helper (SessionSafety), the tray-app picker data (TrayAppInfo /
//  TrayApps) and the freeze-list editor (FreezeListEditorForm).

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
    internal enum UiPhase
    {
        Probe,      // contacting hardware (busy overlay, spinner)
        Home,       // mode cards
        Optimize,   // Go Time selection deck
        Confirm,    // Eco review stage (opt-in via --confirm)
        Applying,   // switch running on the background thread (busy overlay)
        Result,     // done or failed (result overlay)
        Monitor,    // live system monitor page
        History,    // logs + session history page
        Theme       // palette + header gradient page (v1.3.0)
    }

    // ---------------------------------------------------------------------
    // MainForm (v1.2.0, unified): ONE window, both modes. Sidebar rail
    // (Home / Optimize / Monitor / History), a gradient header with live
    // status chips, two big mode cards on Home (GO TIME / ECO MODE with the
    // active mode glowing), the Go Time selection deck rebuilt with animated
    // ToggleSwitches inside rounded cards (gate-locked cleanup rows show a
    // lock glyph + reasons), a monitor page over the existing MonitorPanel,
    // a history page (log viewer / log browser / session history) and a
    // busy/result overlay shared by probe/apply/result. The apply chain,
    // GO session features, session tray, overlay and eco-safe restore are
    // the proven v1.1 logic with the MODE_STANDARD/MODE_ECO split removed:
    // the mode is chosen at runtime (cards, --gotime/--eco flags or the
    // session tray's "Go Eco").
    // ---------------------------------------------------------------------
    internal class MainForm : Form
    {
        // ---- launch flags / state -------------------------------------------
        private readonly bool _confirmMode;
        private readonly bool _autoMode;
        private readonly bool _startGo;
        private readonly bool _startEco;
        private UiPhase _phase = UiPhase.Probe;
        private SwitchOutcome _last;
        private bool _currentEco = true;          // last known dGPU state (probe / switch)
        private bool _esOn = true;                // last known Energy Saver state
        private string _transport = "";

        // ---- shell chrome ----------------------------------------------------
        private readonly System.Windows.Forms.Timer _clock = new System.Windows.Forms.Timer();
        private readonly Button _x = new Button();
        private readonly Button _min = new Button();              // minimize, left of X
        private readonly Panel _side = new Panel();
        private readonly PictureBox _logo = new PictureBox();
        private readonly NavButton _navHome = new NavButton("\uE80F", "Home");
        private readonly NavButton _navOpt = new NavButton("\uE945", "Optimize");
        private readonly NavButton _navMon = new NavButton("\uE9D9", "Monitor");
        private readonly NavButton _navHist = new NavButton("\uE823", "History");
        private readonly NavButton _navTheme = new NavButton("\uE790", "Theme");   // v1.3.0 palette page
        private readonly Label _verLbl = new Label();
        private readonly Label _title = new Label();   // plain Label: the GradientLabel never received WM_PAINT in the strip (see HANDBOOK v1.2.0 notes)
        private readonly Label _subtitle = new Label();
        private readonly Label _chipGpu = new Label();
        private readonly Label _chipEs = new Label();
        private readonly Label _chipBus = new Label();
        private readonly ShimmerBar _bar = new ShimmerBar();
        private readonly Label _status = new Label();
        private readonly Panel _content = new Panel();
        private readonly HeaderStripPanel _headerStrip = new HeaderStripPanel();   // always-topmost header (title/chips/status/X); paints the optional gradient

        // ---- home section ----------------------------------------------------
        private readonly Panel _homeSection = new Panel();
        private readonly Label _homeCaption = new Label();
        private readonly Label _homeStateBig = new Label();
        private readonly Label _homeTransport = new Label();
        private readonly ModeCard _goCard = new ModeCard();
        private readonly ModeCard _ecoCard = new ModeCard();

        // ---- optimize section ------------------------------------------------
        private readonly Panel _optSection = new Panel();
        private readonly Panel _optScroll = new Panel();          // AutoScroll host
        private readonly Panel _optBottom = new Panel();          // GO action bar
        private readonly AccentButton _goBtn = new AccentButton();
        private readonly Button _optCancel = new Button();
        private readonly ProfileBar _profileBar = new ProfileBar();
        private readonly Card _sysCard = new Card();
        private readonly List<ToggleSwitch> _optBoxes = new List<ToggleSwitch>();
        private readonly Card _trayCard = new Card();
        private readonly List<ToggleSwitch> _trayBoxes = new List<ToggleSwitch>();
        private readonly Button _closeTray = new Button();
        private bool _trayBusy;
        private readonly Card _perfCard = new Card();
        private readonly ToggleSwitch _freezeBox = new ToggleSwitch();
        private readonly Button _editFreezeList = new Button();
        private readonly ToggleSwitch _planBox = new ToggleSwitch();
        private readonly ToggleSwitch _wuPauseBox = new ToggleSwitch();
        private readonly Card _cleanCard = new Card();
        private readonly ToggleSwitch _cleanWuBox = new ToggleSwitch();
        private readonly ToggleSwitch _cleanDismBox = new ToggleSwitch();
        private readonly ToggleSwitch _cleanDeepBox = new ToggleSwitch();
        private readonly ToggleSwitch _cleanGpuBox = new ToggleSwitch();
        private readonly List<ToggleSwitch> _appCacheBoxes = new List<ToggleSwitch>();
        private readonly Label _gateLabel = new Label();          // amber lock banner
        private readonly Label _oldLabel = new Label();           // Windows.old report-only note

        // ---- monitor section -------------------------------------------------
        private readonly Panel _monSection = new Panel();
        private readonly MonitorPanel _monitorPanel = new MonitorPanel();
        private readonly Button _overlayBtn = new Button();
        private readonly Label _monHint = new Label();
        private readonly Label _refreshLabel = new Label();          // v1.2.2 refresh-rate picker
        private readonly ComboBox _refreshBox = new ComboBox();

        // ---- history section -------------------------------------------------
        private readonly Panel _histSection = new Panel();
        private readonly Button _histViewLog = new Button();
        private readonly Button _histBrowser = new Button();
        private readonly Button _histSessions = new Button();
        private readonly Label _histPath = new Label();

        // ---- theme section (v1.3.0) -------------------------------------------
        private readonly Panel _themeSection = new Panel();
        private readonly Card _presetCard = new Card();
        private readonly List<ThemeSwatch> _presetSwatches = new List<ThemeSwatch>();
        private readonly Card _accentCard = new Card();
        private readonly ThemeSwatch _accentSwatch = new ThemeSwatch();
        private readonly Button _accentPick = new Button();
        private readonly Card _gradCard = new Card();
        private readonly ThemeSwatch _gradFromSwatch = new ThemeSwatch();
        private readonly Button _gradFromPick = new Button();
        private readonly ThemeSwatch _gradToSwatch = new ThemeSwatch();
        private readonly Button _gradToPick = new Button();
        private readonly Label _gradDirLabel = new Label();
        private readonly ComboBox _gradDirBox = new ComboBox();
        private readonly ToggleSwitch _gradToggle = new ToggleSwitch();
        private readonly Card _navCard = new Card();
        private readonly ThemeSwatch _navSwatch = new ThemeSwatch();
        private readonly Button _navPick = new Button();
        private readonly AccentButton _resetThemeBtn = new AccentButton();
        private bool _syncingThemeUi;                 // guards SyncThemeUi against its own events

        // ---- busy / result overlay -------------------------------------------
        private readonly Panel _busy = new Panel();
        private readonly SpinGlyph _spin = new SpinGlyph();
        private readonly Label _busyGlyph = new Label();          // result check / warning glyph
        private readonly Label _busyTitle = new Label();
        private readonly TextBox _busyText = new TextBox();
        private readonly Label _resTrayTitle = new Label();
        private readonly Button _resTrayClose = new Button();
        private readonly Button _resLogBtn = new Button();
        private readonly Button _resHistBtn = new Button();
        private readonly Button _resSessBtn = new Button();
        private readonly Button _resRestartBtn = new Button();
        private readonly AccentButton _resHomeBtn = new AccentButton();
        private readonly Button _confirmApplyBtn = new Button();
        private readonly Button _confirmCancelBtn = new Button();

        // ---- session (GO) state ----------------------------------------------
        private SessionTray _tray;
        private MonitorOverlayForm _overlay;
        private List<CleanCategory> _tier1Measured;
        private List<string> _gateReasons;                        // null = unknown yet
        private List<string> _gateReasonsSvc;                     // servicing-scoped (WU/DISM rows)
        private bool _measureStarted;
        private bool _optLaidOut;
        private string _cleanupSummary = "";
        private bool _resultTrayAvailable;

        public MainForm(bool autoMode, bool confirmMode, bool startGo, bool startEco)
        {
            _autoMode = autoMode;
            _confirmMode = confirmMode;
            _startGo = startGo;
            _startEco = startEco;

            Text = "GPU Mode Switch";
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(980, 660);
            MinimumSize = new Size(880, 560);
            BackColor = Ui.Bg;
            Font = new Font("Segoe UI", 9.75f);
            Opacity = 0;
            WindowIcons.Apply(this);
            Region = new Region(UiShapes.RoundRect(0, 0, ClientSize.Width, ClientSize.Height, 22));

            BuildSide();
            BuildHeader();
            BuildHome();
            BuildOptimize();
            BuildMonitor();
            BuildHistory();
            BuildTheme();
            BuildBusyOverlay();

            _content.Controls.AddRange(new Control[] { _homeSection, _optSection, _monSection, _histSection, _themeSection, _busy });
            _busy.BringToFront();                    // above the sections, below the header strip
            _content.Controls.Add(_headerStrip);
            // (v1.2.2) WinForms z-order gotcha: Controls.Add appends to the
            // END of the collection and index 0 is TOPMOST - so "added last"
            // is actually the BOTTOM, and the full-size home section covered
            // the whole header strip (title/chips/X painted nowhere, though
            // UIA still saw them). BringToFront puts the strip at index 0 =
            // genuinely topmost, permanently - ShowBusy's BringToFront can
            // never cover it.
            _headerStrip.BringToFront();
            Controls.Add(_content);
            Controls.Add(_side);

            Shown += delegate { LayoutChrome(); EnterProbe(); };
            Resize += delegate { LayoutChrome(); };
            _clock.Interval = 30;
            _clock.Tick += OnClock;
            _clock.Start();

            FormClosed += delegate
            {
                _clock.Dispose();
                // Eco-safe restore on any exit (v1.1 rule): never leave a frozen
                // process, a paused Windows Update or a foreign power plan behind.
                SessionSafety.RestoreAll();
                DisposeOverlay();
                try { if (MonitorEngine.Running) MonitorEngine.Stop(); } catch { }
                AsusControl.Shutdown();
            };
        }

        // ---- construction: shell ---------------------------------------------

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // Bare panel background = drag handle (the window is borderless).
        private void MakeDraggable(Control c)
        {
            c.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero);   // WM_NCLBUTTONDOWN, HTCAPTION
                }
            };
        }

        private void BuildSide()
        {
            _side.BackColor = Ui.NavBack;
            MakeDraggable(_side);

            Bitmap logo = LoadResourcePng("GpuModeSwitch.appicon.png");
            if (logo != null)
            {
                _logo.Image = logo;
                _logo.SizeMode = PictureBoxSizeMode.Zoom;
                _logo.Size = new Size(42, 42);
                _logo.Location = new Point(22, 20);
                _logo.TabStop = false;
                _side.Controls.Add(_logo);
            }

            _navHome.Click += delegate { ShowSection(UiPhase.Home); };
            _navOpt.Click += delegate { EnterOptimize(); };
            _navMon.Click += delegate { ShowMonitor(); };
            _navHist.Click += delegate { ShowHistorySection(); };
            _navTheme.Click += delegate { ShowThemeSection(); };
            _side.Controls.Add(_navHome);
            _side.Controls.Add(_navOpt);
            _side.Controls.Add(_navMon);
            _side.Controls.Add(_navHist);
            _side.Controls.Add(_navTheme);

            _verLbl.Text = "v" + Program.Version;
            _verLbl.ForeColor = Ui.TextDim;
            _verLbl.BackColor = Color.Transparent;
            _verLbl.AutoSize = true;
            _verLbl.Font = new Font("Segoe UI", 8f);
            _side.Controls.Add(_verLbl);
        }

        private void BuildHeader()
        {
            _title.Text = "GPU MODE SWITCH";
            _title.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
            _title.ForeColor = Ui.Cyan;
            _title.BackColor = HeaderChildBack();
            _title.AutoSize = false;
            _title.Size = new Size(310, 30);
            _title.TextAlign = ContentAlignment.MiddleLeft;
            _title.Location = new Point(0, 0);    // placed by LayoutChrome

            _subtitle.Text = "unified command deck  \u2022  both modes, one app";
            _subtitle.ForeColor = Ui.TextDim;
            _subtitle.BackColor = HeaderChildBack();
            _subtitle.AutoSize = true;
            _subtitle.Font = new Font("Segoe UI", 9f);

            InitChip(_chipBus, "transport ?");
            InitChip(_chipGpu, "GPU probe...");
            InitChip(_chipEs, "Energy Saver ?");

            _bar.Accent = Ui.Cyan;
            _bar.Visible = false;

            _status.Text = "Starting...";
            _status.ForeColor = Ui.TextHi;
            _status.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            _status.BackColor = HeaderChildBack();
            _status.AutoSize = false;
            _status.Height = 26;
            MakeDraggable(_status);

            _x.Text = "\u2715";                     // ✕
            _x.FlatStyle = FlatStyle.Flat;
            _x.FlatAppearance.BorderSize = 0;
            _x.ForeColor = Ui.TextDim;
            _x.BackColor = HeaderChildBack();
            _x.Font = new Font("Segoe UI", 10f);
            _x.Size = new Size(34, 28);
            _x.TabStop = false;
            _x.Click += delegate
            {
                if (_phase == UiPhase.Applying) return;   // never abandon a mid-flight write
                Close();
            };

            // Minimize (v1.2.1): borderless window, so the button drives
            // WindowState directly. Safe during any phase - the background
            // worker keeps running and the taskbar icon restores the window.
            _min.Text = "\u2014";                     // —
            _min.FlatStyle = FlatStyle.Flat;
            _min.FlatAppearance.BorderSize = 0;
            _min.ForeColor = Ui.TextDim;
            _min.BackColor = HeaderChildBack();
            _min.Font = new Font("Segoe UI", 10f);
            _min.Size = new Size(34, 28);
            _min.TabStop = false;
            _min.Click += delegate { WindowState = FormWindowState.Minimized; };

            // The header lives on its own strip (added to _content last, so
            // it is permanently topmost - see the ctor note). Add order is
            // deliberately REVERSED (x/status/bar first, title last): in the
            // first v1.2.0 build only the last-added children of the strip
            // ever received paint, so the marquee controls are added last.
            // The strip itself (v1.3.0 HeaderStripPanel) paints the solid
            // Ui.Bg or the user's gradient in OnPaintBackground.
            _headerStrip.BackColor = Ui.Bg;
            // Add order (v1.2.2): the strip's children are laid out without
            // overlaps (title/subtitle left, chips right, bar/status bottom),
            // so sibling z-order does not matter - except the chrome buttons,
            // which are explicitly brought to front (index 0 = topmost in
            // WinForms) so nothing can ever cover them.
            _headerStrip.Controls.Add(_bar);
            _headerStrip.Controls.Add(_status);
            _headerStrip.Controls.Add(_chipBus);
            _headerStrip.Controls.Add(_chipGpu);
            _headerStrip.Controls.Add(_chipEs);
            _headerStrip.Controls.Add(_subtitle);
            _headerStrip.Controls.Add(_title);
            _headerStrip.Controls.Add(_min);
            _headerStrip.Controls.Add(_x);
            _min.BringToFront();
            _x.BringToFront();
            MakeDraggable(_headerStrip);
            _content.BackColor = Ui.Bg;
        }

        // Live status chip: a plain colored label (proven to paint) - text
        // carries the state, the color carries the signal.
        private void InitChip(Label chip, string text)
        {
            chip.Font = new Font("Segoe UI", 8.75f);
            chip.BackColor = HeaderChildBack();
            chip.AutoSize = false;
            chip.Size = new Size(170, 22);
            chip.TextAlign = ContentAlignment.MiddleLeft;
            SetChip(chip, text, Ui.TextDim);
        }

        // Background for the header's direct children: solid Ui.Bg, or
        // transparent while the header gradient is on (a solid BackColor
        // would patch over the gradient - managed transparency composites
        // over the parent, which the strip paints itself).
        private static Color HeaderChildBack()
        {
            return Ui.GradEnabled ? Color.Transparent : Ui.Bg;
        }

        private static void SetChip(Label chip, string text, Color color)
        {
            chip.Text = text;
            chip.ForeColor = color;
        }

        // ---- construction: home ----------------------------------------------

        private void BuildHome()
        {
            _homeSection.BackColor = Ui.Bg;

            _homeCaption.Text = "CURRENT MODE";
            _homeCaption.ForeColor = Ui.TextDim;
            _homeCaption.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _homeCaption.AutoSize = true;
            _homeCaption.BackColor = Color.Transparent;

            _homeStateBig.Text = "probing...";
            _homeStateBig.ForeColor = Ui.TextHi;
            _homeStateBig.Font = new Font("Segoe UI", 26f, FontStyle.Bold);
            _homeStateBig.AutoSize = true;
            _homeStateBig.BackColor = Color.Transparent;

            _homeTransport.Text = "";
            _homeTransport.ForeColor = Ui.TextDim;
            _homeTransport.Font = new Font("Segoe UI", 9f);
            _homeTransport.AutoSize = true;
            _homeTransport.BackColor = Color.Transparent;

            _goCard.CardTitle = "GO TIME";
            _goCard.Tagline = "dGPU on \u2022 hybrid display path\nfull performance for the session";
            _goCard.Hint = "CONFIGURE & LAUNCH";
            _goCard.Accent = Ui.Go;
            _goCard.Emblem = LoadResourcePng("GpuModeSwitch.gotime.png");
            _goCard.Click += delegate { EnterOptimize(); };

            _ecoCard.CardTitle = "ECO MODE";
            _ecoCard.Tagline = "dGPU powered off \u2022 battery friendly\nEnergy Saver engages silently";
            _ecoCard.Hint = "ONE CLICK - SWITCH NOW";
            _ecoCard.Accent = Ui.Eco;
            _ecoCard.Emblem = LoadResourcePng("GpuModeSwitch.ecomode.png");
            _ecoCard.Click += delegate
            {
                if (_confirmMode)
                {
                    EnterEcoConfirm();
                }
                else
                {
                    BeginEcoApply();
                }
            };

            MakeDraggable(_homeSection);
            _homeSection.Controls.Add(_homeCaption);
            _homeSection.Controls.Add(_homeStateBig);
            _homeSection.Controls.Add(_homeTransport);
            _homeSection.Controls.Add(_goCard);
            _homeSection.Controls.Add(_ecoCard);
        }

        // ---- construction: optimize -------------------------------------------

        private void BuildOptimize()
        {
            _optSection.BackColor = Ui.Bg;

            _optScroll.AutoScroll = true;
            _optScroll.BackColor = Ui.Bg;
            MakeDraggable(_optScroll);

            _sysCard.CardTitle = "System optimizations";
            _sysCard.TitleAccent = Ui.Go;
            string[] optLabels = { "Game Mode", "Do not disturb", "Game DVR recording off", "Network throttling off", "Pause background services" };
            string[] optKeys = { "opt.gamemode", "opt.dnd", "opt.dvr", "opt.throttle", "opt.services" };
            for (int i = 0; i < optLabels.Length; i++)
            {
                ToggleSwitch sw = MakeSwitch(optLabels[i], true, Ui.Go);
                sw.Tag = optKeys[i];
                _optBoxes.Add(sw);
                _sysCard.Controls.Add(sw);
            }

            _trayCard.CardTitle = "Tray apps detected - tick to close for the session";
            _trayCard.TitleAccent = Ui.Cyan;
            for (int i = 0; i < TrayApps.Known.Length; i++)
            {
                ToggleSwitch sw = MakeSwitch(TrayApps.Known[i].Label, false, Ui.Cyan);
                sw.Enabled = false;
                sw.Tag = "tray." + TrayApps.Known[i].Label;
                _trayBoxes.Add(sw);
                _trayCard.Controls.Add(sw);
            }
            _closeTray.Text = "Close selected";
            StyleSecondaryButton(_closeTray, 130, 28);
            _closeTray.Click += delegate { BeginCloseTrayApps(); };
            _trayCard.Controls.Add(_closeTray);

            _perfCard.CardTitle = "Performance - session scoped, fully reversible";
            _perfCard.TitleAccent = Ui.Go;
            _freezeBox.Text = "Freeze background apps";
            _freezeBox.Checked = true;
            _freezeBox.Accent = Ui.Go;
            _freezeBox.Tag = "perf.freeze";
            _editFreezeList.Text = "Edit list...";
            StyleSecondaryButton(_editFreezeList, 100, 26);
            _editFreezeList.Click += delegate { ShowFreezeListEditor(); };
            _planBox.Text = "Ultimate Performance plan";
            _planBox.Checked = true;
            _planBox.Accent = Ui.Go;
            _planBox.Tag = "perf.plan";
            _wuPauseBox.Text = "Pause Windows Update";
            _wuPauseBox.Checked = true;
            _wuPauseBox.Accent = Ui.Go;
            _wuPauseBox.Tag = "perf.wupause";
            _perfCard.Controls.Add(_freezeBox);
            _perfCard.Controls.Add(_editFreezeList);
            _perfCard.Controls.Add(_planBox);
            _perfCard.Controls.Add(_wuPauseBox);

            _cleanCard.CardTitle = "Storage cleanup - analyze-first, cache-only";
            _cleanCard.TitleAccent = Ui.Cyan;
            _cleanWuBox.Text = "Windows Update cache purge";
            _cleanWuBox.Accent = Ui.Cyan;
            _cleanWuBox.Tag = "clean.wu";
            _cleanDismBox.Text = "Component store cleanup (DISM)";
            _cleanDismBox.Accent = Ui.Cyan;
            _cleanDismBox.Tag = "clean.dism";
            _cleanDeepBox.Text = "Deep clean";
            _cleanDeepBox.Accent = Ui.Cyan;
            _cleanDeepBox.Tag = "clean.deep";
            _cleanGpuBox.Text = "GPU shader caches";
            _cleanGpuBox.Accent = Ui.Cyan;
            _cleanGpuBox.Tag = "clean.gpu";
            _gateLabel.ForeColor = Ui.Amber;
            _gateLabel.BackColor = Color.Transparent;
            _gateLabel.Font = new Font("Segoe UI", 8.75f);
            _gateLabel.Visible = false;
            _oldLabel.ForeColor = Ui.TextDim;
            _oldLabel.BackColor = Color.Transparent;
            _oldLabel.Font = new Font("Segoe UI", 8.75f);
            _oldLabel.Visible = false;
            _cleanCard.Controls.Add(_cleanWuBox);
            _cleanCard.Controls.Add(_cleanDismBox);
            _cleanCard.Controls.Add(_cleanDeepBox);
            _cleanCard.Controls.Add(_cleanGpuBox);
            _cleanCard.Controls.Add(_gateLabel);
            _cleanCard.Controls.Add(_oldLabel);

            // Named profiles (A14) collect/apply every switch by its stable Tag.
            _profileBar.CollectSelections += CollectAllSelections;
            _profileBar.ApplyRequested += ApplyProfileSelections;

            _optScroll.Controls.Add(_profileBar);
            _optScroll.Controls.Add(_sysCard);
            _optScroll.Controls.Add(_trayCard);
            _optScroll.Controls.Add(_perfCard);
            _optScroll.Controls.Add(_cleanCard);

            _optBottom.BackColor = Ui.Bg;
            _goBtn.Text = "GO  \u25B8";
            _goBtn.Size = new Size(150, 44);
            _goBtn.From = Ui.Go;
            _goBtn.To = Color.FromArgb(255, 140, 46);
            _goBtn.Click += delegate { BeginGoApply(); };
            _optCancel.Text = "Close";
            StyleSecondaryButton(_optCancel, 90, 34);
            _optCancel.Click += delegate { Close(); };
            _optBottom.Controls.Add(_goBtn);
            _optBottom.Controls.Add(_optCancel);

            _optSection.Controls.Add(_optScroll);
            _optSection.Controls.Add(_optBottom);
        }

        private static ToggleSwitch MakeSwitch(string text, bool on, Color accent)
        {
            ToggleSwitch sw = new ToggleSwitch();
            sw.Text = text;
            sw.Checked = on;
            sw.Accent = accent;
            return sw;
        }

        private void StyleSecondaryButton(Button b, int w, int h)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Ui.CardBorder;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(38, 44, 60);
            b.ForeColor = Ui.Text;
            b.BackColor = Color.FromArgb(28, 33, 46);
            b.Size = new Size(w, h);
            b.Font = new Font("Segoe UI", 9f);
            b.TabStop = false;
            b.Cursor = Cursors.Hand;
        }

        // ---- construction: monitor + history ---------------------------------

        private void BuildMonitor()
        {
            _monSection.BackColor = Ui.Bg;
            MakeDraggable(_monSection);

            _monitorPanel.Visible = true;
            _overlayBtn.Text = "Show overlay over the game";
            _overlayBtn.TextAlign = ContentAlignment.MiddleCenter;
            StyleSecondaryButton(_overlayBtn, 240, 40);
            _overlayBtn.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            _overlayBtn.Click += delegate { ToggleOverlay(); _overlayBtn.Text = "Toggle overlay over the game"; };

            _monHint.Text = "The overlay is a compact always-on-top frame you can drag anywhere.\nIt only appears while a monitor engine is sampling (this page or an active GO session).";
            _monHint.ForeColor = Ui.TextDim;
            _monHint.Font = new Font("Segoe UI", 9f);
            _monHint.BackColor = Color.Transparent;
            _monHint.AutoSize = true;

            // Refresh-rate picker (v1.2.2): the dropdown drives the sampling
            // interval live (MonitorEngine.Start re-targets a running timer)
            // and persists the choice for the next session.
            _refreshLabel.Text = "Refresh rate";
            _refreshLabel.ForeColor = Ui.TextDim;
            _refreshLabel.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _refreshLabel.BackColor = Color.Transparent;
            _refreshLabel.AutoSize = true;

            _refreshBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _refreshBox.Font = new Font("Segoe UI", 9.5f);
            _refreshBox.Width = 90;
            _refreshBox.FlatStyle = FlatStyle.Flat;
            _refreshBox.BackColor = Color.FromArgb(42, 42, 49);
            _refreshBox.ForeColor = Color.FromArgb(220, 220, 226);
            int savedMs = MonitorEngine.SavedIntervalMs;
            foreach (int ms in MonitorEngine.IntervalChoicesMs)
            {
                _refreshBox.Items.Add(ms >= 1000 ? (ms / 1000) + " s" : ms + " ms");
                if (ms == savedMs) _refreshBox.SelectedIndex = _refreshBox.Items.Count - 1;
            }
            if (_refreshBox.SelectedIndex < 0) _refreshBox.SelectedIndex = 1;   // 2 s default
            _refreshBox.SelectedIndexChanged += delegate
            {
                int idx = _refreshBox.SelectedIndex;
                if (idx < 0 || idx >= MonitorEngine.IntervalChoicesMs.Length) return;
                int ms = MonitorEngine.IntervalChoicesMs[idx];
                MonitorEngine.SavedIntervalMs = ms;
                MonitorEngine.Start(ms);          // live re-target when sampling
                Log.Chan("MONITOR", "refresh rate set to " + ms + " ms (saved)");
            };

            _monSection.Controls.Add(_monitorPanel);
            _monSection.Controls.Add(_refreshLabel);
            _monSection.Controls.Add(_refreshBox);
            _monSection.Controls.Add(_overlayBtn);
            _monSection.Controls.Add(_monHint);
        }

        private void BuildHistory()
        {
            _histSection.BackColor = Ui.Bg;
            MakeDraggable(_histSection);

            MakeHistoryButton(_histViewLog, "VIEW CURRENT LOG", "the live session log with filter + find");
            _histViewLog.Click += delegate { using (LogForm lf = new LogForm("GPU Mode Switch")) lf.ShowDialog(this); };
            MakeHistoryButton(_histBrowser, "LOG HISTORY", "every run of every app, searchable");
            _histBrowser.Click += delegate { LogBrowserForm.ShowBrowser(this); };
            MakeHistoryButton(_histSessions, "SESSION HISTORY", "what each GO run did and how much it freed");
            _histSessions.Click += delegate { SessionHistoryForm.ShowHistory(this); };

            _histPath.Text = "";
            _histPath.ForeColor = Ui.TextDim;
            _histPath.Font = new Font("Consolas", 8.5f);
            _histPath.BackColor = Color.Transparent;
            _histPath.AutoSize = true;

            _histSection.Controls.Add(_histViewLog);
            _histSection.Controls.Add(_histBrowser);
            _histSection.Controls.Add(_histSessions);
            _histSection.Controls.Add(_histPath);
        }

        private void MakeHistoryButton(Button b, string title, string caption)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Ui.CardBorder;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 36, 50);
            b.ForeColor = Ui.TextHi;
            b.BackColor = Ui.Card;
            b.TextAlign = ContentAlignment.MiddleLeft;
            b.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
            b.Size = new Size(420, 64);
            b.Text = title + "\n" + caption;
            b.TabStop = false;
            b.Cursor = Cursors.Hand;
        }

        // ---- construction: theme (v1.3.0) -------------------------------------

        // The Theme page: preset swatches, an accent picker, the header
        // gradient group, the nav rail color and a reset - every change
        // applies live (ApplyTheme) and persists (ThemeState.Save).
        private void BuildTheme()
        {
            _themeSection.BackColor = Ui.Bg;
            MakeDraggable(_themeSection);

            _presetCard.CardTitle = "Presets - one click, applied live";
            _presetCard.TitleAccent = Ui.Cyan;
            for (int i = 0; i < ThemeState.Presets.Length; i++)
            {
                ThemePreset p = ThemeState.Presets[i];
                ThemeSwatch sw = new ThemeSwatch();
                sw.Text = p.Name;
                sw.Preview = p.Bg;
                sw.SwatchAccent = p.Cyan;
                sw.Click += delegate { OnThemePresetPicked(p.Name); };
                _presetSwatches.Add(sw);
                _presetCard.Controls.Add(sw);
            }

            _accentCard.CardTitle = "Accent color - titles, active states, general highlights";
            _accentCard.TitleAccent = Ui.Cyan;
            _accentSwatch.Text = "accent";
            _accentSwatch.Size = new Size(150, 34);
            _accentPick.Text = "Pick...";
            StyleSecondaryButton(_accentPick, 100, 28);
            _accentPick.Click += delegate { OnPickAccentColor(); };
            _accentCard.Controls.Add(_accentSwatch);
            _accentCard.Controls.Add(_accentPick);

            _gradCard.CardTitle = "Header gradient";
            _gradCard.TitleAccent = Ui.Cyan;
            _gradFromSwatch.Text = "from";
            _gradFromSwatch.Size = new Size(120, 34);
            _gradFromPick.Text = "Pick...";
            StyleSecondaryButton(_gradFromPick, 100, 28);
            _gradFromPick.Click += delegate { OnPickGradColor(true); };
            _gradToSwatch.Text = "to";
            _gradToSwatch.Size = new Size(120, 34);
            _gradToPick.Text = "Pick...";
            StyleSecondaryButton(_gradToPick, 100, 28);
            _gradToPick.Click += delegate { OnPickGradColor(false); };
            _gradDirLabel.Text = "Direction";
            _gradDirLabel.ForeColor = Ui.TextDim;
            _gradDirLabel.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _gradDirLabel.BackColor = Color.Transparent;
            _gradDirLabel.AutoSize = true;
            _gradDirBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _gradDirBox.Font = new Font("Segoe UI", 9.5f);
            _gradDirBox.Width = 110;
            _gradDirBox.FlatStyle = FlatStyle.Flat;
            _gradDirBox.BackColor = Color.FromArgb(42, 42, 49);
            _gradDirBox.ForeColor = Color.FromArgb(220, 220, 226);
            _gradDirBox.Items.Add("Horizontal");
            _gradDirBox.Items.Add("Diagonal");
            _gradDirBox.SelectedIndex = ThemeState.GradDiagonal ? 1 : 0;
            _gradDirBox.SelectedIndexChanged += delegate
            {
                if (_syncingThemeUi) return;
                ThemeState.GradDiagonal = _gradDirBox.SelectedIndex == 1;
                ThemeState.Save();
                ApplyTheme();
            };
            _gradToggle.Text = "Gradient on";
            _gradToggle.Accent = Ui.Cyan;
            _gradToggle.Checked = ThemeState.GradOn;
            _gradToggle.CheckedChanged += delegate
            {
                if (_syncingThemeUi) return;
                ThemeState.GradOn = _gradToggle.Checked;
                ThemeState.Save();
                ApplyTheme();
            };
            _gradCard.Controls.Add(_gradFromSwatch);
            _gradCard.Controls.Add(_gradFromPick);
            _gradCard.Controls.Add(_gradToSwatch);
            _gradCard.Controls.Add(_gradToPick);
            _gradCard.Controls.Add(_gradDirLabel);
            _gradCard.Controls.Add(_gradDirBox);
            _gradCard.Controls.Add(_gradToggle);

            _navCard.CardTitle = "Navigation bar color";
            _navCard.TitleAccent = Ui.Cyan;
            _navSwatch.Text = "rail";
            _navSwatch.Size = new Size(150, 34);
            _navPick.Text = "Pick...";
            StyleSecondaryButton(_navPick, 100, 28);
            _navPick.Click += delegate { OnPickNavColor(); };
            _navCard.Controls.Add(_navSwatch);
            _navCard.Controls.Add(_navPick);

            _resetThemeBtn.Text = "RESET TO DEFAULT";
            _resetThemeBtn.Size = new Size(190, 36);
            _resetThemeBtn.From = Ui.Cyan;
            _resetThemeBtn.To = Ui.Go;
            _resetThemeBtn.Click += delegate { OnThemeReset(); };

            _themeSection.Controls.Add(_presetCard);
            _themeSection.Controls.Add(_accentCard);
            _themeSection.Controls.Add(_gradCard);
            _themeSection.Controls.Add(_navCard);
            _themeSection.Controls.Add(_resetThemeBtn);

            SyncThemeUi();
        }

        // ---- theme interaction ------------------------------------------------

        // Opens the native color picker seeded with the current color;
        // null when the dialog was canceled.
        private Color? PickThemeDialog(Color current)
        {
            using (ColorDialog cd = new ColorDialog())
            {
                cd.Color = current;
                cd.FullOpen = true;
                if (cd.ShowDialog(this) != DialogResult.OK) return null;
                return cd.Color;
            }
        }

        private void OnThemePresetPicked(string name)
        {
            ThemeState.ApplyPreset(name);
            ThemeState.Save();
            ApplyTheme();
            Log.Chan("THEME", "preset applied: " + name);
        }

        private void OnPickAccentColor()
        {
            Color? c = PickThemeDialog(ThemeState.Accent);
            if (c == null) return;
            ThemeState.Accent = c.Value;
            ThemeState.PresetName = "Custom";
            ThemeState.Save();
            ApplyTheme();
            Log.Chan("THEME", "accent color set (custom)");
        }

        private void OnPickGradColor(bool from)
        {
            Color? c = PickThemeDialog(from ? ThemeState.GradFrom : ThemeState.GradTo);
            if (c == null) return;
            if (from) ThemeState.GradFrom = c.Value; else ThemeState.GradTo = c.Value;
            ThemeState.Save();
            ApplyTheme();
            Log.Chan("THEME", "gradient " + (from ? "from" : "to") + " color set");
        }

        private void OnPickNavColor()
        {
            Color? c = PickThemeDialog(ThemeState.NavColor);
            if (c == null) return;
            ThemeState.NavColor = c.Value;
            ThemeState.PresetName = "Custom";
            ThemeState.Save();
            ApplyTheme();
            Log.Chan("THEME", "navigation bar color set (custom)");
        }

        private void OnThemeReset()
        {
            ThemeState.ResetToDefault();
            ThemeState.Save();
            ApplyTheme();
            Log.Chan("THEME", "theme reset to default");
        }

        // Re-points the Theme page controls at ThemeState (used at
        // construction and after every ApplyTheme).
        private void SyncThemeUi()
        {
            _syncingThemeUi = true;
            for (int i = 0; i < _presetSwatches.Count && i < ThemeState.Presets.Length; i++)
            {
                _presetSwatches[i].Selected = string.Equals(
                    ThemeState.Presets[i].Name, ThemeState.PresetName, StringComparison.OrdinalIgnoreCase);
            }
            _accentSwatch.Preview = ThemeState.Accent;
            _accentSwatch.SwatchAccent = ThemeState.Accent;
            _gradFromSwatch.Preview = ThemeState.GradFrom;
            _gradFromSwatch.SwatchAccent = ThemeState.Accent;
            _gradToSwatch.Preview = ThemeState.GradTo;
            _gradToSwatch.SwatchAccent = ThemeState.Accent;
            _navSwatch.Preview = ThemeState.NavColor;
            _navSwatch.SwatchAccent = ThemeState.Accent;
            _gradToggle.Checked = ThemeState.GradOn;
            _gradDirBox.SelectedIndex = ThemeState.GradDiagonal ? 1 : 0;
            _syncingThemeUi = false;
        }

        // Pushes ThemeState into Ui and re-colors every static surface; the
        // owner-drawn controls (nav buttons, cards, switches, swatches, mode
        // cards) read Ui inside OnPaint and pick the new palette up on the
        // final Invalidate. Popups (log/session windows) are NOT touched
        // here - they follow the palette constants in their own wave.
        private void ApplyTheme()
        {
            ThemeState.ApplyUi();

            // shell + sections
            BackColor = Ui.Bg;
            _content.BackColor = Ui.Bg;
            _side.BackColor = Ui.NavBack;
            foreach (Control c in new Control[] { _navHome, _navOpt, _navMon, _navHist, _navTheme })
            {
                c.BackColor = Ui.NavBack;
            }
            foreach (Control c in new Control[] { _homeSection, _optSection, _monSection, _histSection,
                                                  _themeSection, _busy, _optScroll, _optBottom })
            {
                c.BackColor = Ui.Bg;
            }
            foreach (Card card in new Card[] { _sysCard, _trayCard, _perfCard, _cleanCard,
                                               _presetCard, _accentCard, _gradCard, _navCard })
            {
                card.BackColor = Ui.Bg;      // corner pixels outside the rounded paint
            }
            _goCard.BackColor = Ui.Bg;
            _ecoCard.BackColor = Ui.Bg;

            // header: the strip paints the gradient itself; its children go
            // transparent while it is on so no solid patch covers it
            _headerStrip.BackColor = Ui.Bg;
            _headerStrip.Invalidate();
            Color headerBack = HeaderChildBack();
            _title.ForeColor = Ui.Cyan;
            _title.BackColor = headerBack;
            _subtitle.ForeColor = Ui.TextDim;
            _subtitle.BackColor = headerBack;
            foreach (Label chip in new Label[] { _chipGpu, _chipEs, _chipBus }) chip.BackColor = headerBack;
            _status.ForeColor = Ui.TextHi;
            _status.BackColor = headerBack;
            _min.ForeColor = Ui.TextDim; _min.BackColor = headerBack;
            _x.ForeColor = Ui.TextDim; _x.BackColor = headerBack;
            _verLbl.ForeColor = Ui.TextDim;

            // accents captured at construction - refresh them here
            _sysCard.TitleAccent = Ui.Go; _perfCard.TitleAccent = Ui.Go;
            _trayCard.TitleAccent = Ui.Cyan; _cleanCard.TitleAccent = Ui.Cyan;
            _presetCard.TitleAccent = Ui.Cyan; _accentCard.TitleAccent = Ui.Cyan;
            _gradCard.TitleAccent = Ui.Cyan; _navCard.TitleAccent = Ui.Cyan;
            foreach (ToggleSwitch sw in _optBoxes) sw.Accent = Ui.Go;
            _freezeBox.Accent = Ui.Go; _planBox.Accent = Ui.Go; _wuPauseBox.Accent = Ui.Go;
            foreach (ToggleSwitch sw in _trayBoxes) sw.Accent = Ui.Cyan;
            _cleanWuBox.Accent = Ui.Cyan; _cleanDismBox.Accent = Ui.Cyan;
            _cleanDeepBox.Accent = Ui.Cyan; _cleanGpuBox.Accent = Ui.Cyan;
            foreach (ToggleSwitch sw in _appCacheBoxes) sw.Accent = Ui.Cyan;
            _gradToggle.Accent = Ui.Cyan;
            _goCard.Accent = Ui.Go;
            _ecoCard.Accent = Ui.Eco;
            _bar.Accent = Ui.Cyan;
            _spin.Accent = Ui.Cyan;
            _goBtn.From = Ui.Go;
            _resHomeBtn.From = Ui.Eco; _resHomeBtn.To = Ui.Cyan;
            _resetThemeBtn.From = Ui.Cyan; _resetThemeBtn.To = Ui.Go;

            // history cards + the secondary buttons follow border/text colors
            foreach (Button b in new Button[] { _histViewLog, _histBrowser, _histSessions })
            {
                b.BackColor = Ui.Card;
                b.FlatAppearance.BorderColor = Ui.CardBorder;
                b.ForeColor = Ui.TextHi;
            }
            foreach (Button b in new Button[] { _optCancel, _closeTray, _editFreezeList, _overlayBtn,
                                                _resLogBtn, _resHistBtn, _resSessBtn, _resRestartBtn, _resTrayClose,
                                                _accentPick, _gradFromPick, _gradToPick, _navPick })
            {
                b.FlatAppearance.BorderColor = Ui.CardBorder;
                b.ForeColor = Ui.Text;
            }

            SyncThemeUi();
            Invalidate(true);   // children included - owner-drawn controls repaint with the new Ui
        }

        // ---- construction: busy / result overlay -------------------------------

        private void BuildBusyOverlay()
        {
            _busy.BackColor = Ui.Bg;
            _busy.Visible = false;

            _spin.Accent = Ui.Cyan;
            _spin.Size = new Size(56, 56);

            _busyGlyph.Text = "";
            _busyGlyph.Font = Ui.HasGlyphs ? Ui.Glyph(44f) : new Font("Segoe UI", 30f, FontStyle.Bold);
            _busyGlyph.BackColor = Color.Transparent;
            _busyGlyph.Size = new Size(72, 64);
            _busyGlyph.TextAlign = ContentAlignment.MiddleCenter;
            _busyGlyph.Visible = false;

            _busyTitle.Text = "";
            _busyTitle.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
            _busyTitle.ForeColor = Ui.TextHi;
            _busyTitle.BackColor = Color.Transparent;
            _busyTitle.AutoSize = false;
            _busyTitle.Height = 32;
            _busyTitle.TextAlign = ContentAlignment.MiddleCenter;

            _busyText.Multiline = true;
            _busyText.ReadOnly = true;
            _busyText.ScrollBars = ScrollBars.Vertical;
            _busyText.WordWrap = true;
            _busyText.BackColor = Color.FromArgb(10, 12, 17);
            _busyText.ForeColor = Color.FromArgb(198, 204, 216);
            _busyText.BorderStyle = BorderStyle.None;
            _busyText.Font = new Font("Consolas", 9f);

            _resTrayTitle.Text = "TRAY APPS - tick what should close now";
            _resTrayTitle.ForeColor = Ui.TextDim;
            _resTrayTitle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _resTrayTitle.BackColor = Color.Transparent;
            _resTrayTitle.AutoSize = true;
            _resTrayTitle.Visible = false;
            _resTrayClose.Text = "Close selected";
            StyleSecondaryButton(_resTrayClose, 120, 26);
            _resTrayClose.Visible = false;
            _resTrayClose.Click += delegate { BeginCloseTrayApps(); };

            _resLogBtn.Text = "View log";
            _resHistBtn.Text = "Log History";
            _resSessBtn.Text = "Session History";
            _resRestartBtn.Text = "Restart now";
            StyleSecondaryButton(_resLogBtn, 96, 34);
            StyleSecondaryButton(_resHistBtn, 110, 34);
            StyleSecondaryButton(_resSessBtn, 130, 34);
            StyleSecondaryButton(_resRestartBtn, 120, 34);
            _resLogBtn.Click += delegate { using (LogForm lf = new LogForm("GPU Mode Switch")) lf.ShowDialog(this); };
            _resHistBtn.Click += delegate { LogBrowserForm.ShowBrowser(this); };
            _resSessBtn.Click += delegate { SessionHistoryForm.ShowHistory(this); };
            _resRestartBtn.Click += OnRestart;
            _resHomeBtn.Text = "DONE - BACK TO HOME";
            _resHomeBtn.Size = new Size(190, 38);
            _resHomeBtn.From = Ui.Eco;
            _resHomeBtn.To = Ui.Cyan;
            _resHomeBtn.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            _resHomeBtn.Click += delegate { ShowSection(UiPhase.Home); };
            _confirmApplyBtn.Text = "Apply";
            _confirmApplyBtn.Size = new Size(110, 38);
            _confirmApplyBtn.Click += delegate { BeginEcoApply(); };
            _confirmCancelBtn.Text = "Cancel";
            _confirmCancelBtn.Size = new Size(90, 38);
            _confirmCancelBtn.Click += delegate { ShowSection(UiPhase.Home); };

            _busy.Controls.Add(_spin);
            _busy.Controls.Add(_busyGlyph);
            _busy.Controls.Add(_busyTitle);
            _busy.Controls.Add(_busyText);
            _busy.Controls.Add(_resTrayTitle);
            _busy.Controls.Add(_resTrayClose);
            _busy.Controls.Add(_resLogBtn);
            _busy.Controls.Add(_resHistBtn);
            _busy.Controls.Add(_resSessBtn);
            _busy.Controls.Add(_resRestartBtn);
            _busy.Controls.Add(_resHomeBtn);
            _busy.Controls.Add(_confirmApplyBtn);
            _busy.Controls.Add(_confirmCancelBtn);
        }

        // ---- layout -------------------------------------------------------------

        private void LayoutChrome()
        {
            int W = ClientSize.Width, H = ClientSize.Height;

            // A 6px bare frame keeps the form's edge-resize hit area.
            _side.SetBounds(6, 6, 86, H - 12);
            _content.SetBounds(92, 6, W - 98, H - 12);

            _logo.Location = new Point(22, 20);
            int navY = 76;
            _navHome.Location = new Point(0, navY);
            _navOpt.Location = new Point(0, navY + 58);
            _navMon.Location = new Point(0, navY + 116);
            _navHist.Location = new Point(0, navY + 174);
            _navTheme.Location = new Point(0, navY + 232);
            _verLbl.Location = new Point(10, H - 40);

            _title.Location = new Point(18, 16);
            _subtitle.Location = new Point(20, 46);
            int chipW = 170;
            _chipBus.SetBounds(_content.Width - 3 * (chipW + 8) - 24, 16, chipW, 24);
            _chipGpu.SetBounds(_content.Width - 2 * (chipW + 8) - 24, 16, chipW, 24);
            _chipEs.SetBounds(_content.Width - (chipW + 8) - 24, 16, chipW, 24);
            _x.Location = new Point(_content.Width - 40, 8);
            _min.Location = new Point(_content.Width - 76, 8);
            _bar.SetBounds(18, 74, _content.Width - 36 - 180, 6);
            _status.SetBounds(18, 84, _content.Width - 40, 26);

            int cw = _content.Width, ch = _content.Height;
            _headerStrip.SetBounds(0, 0, cw, 114);
            _homeSection.SetBounds(0, 0, cw, ch);
            _optSection.SetBounds(0, 0, cw, ch);
            _monSection.SetBounds(0, 0, cw, ch);
            _histSection.SetBounds(0, 0, cw, ch);
            _themeSection.SetBounds(0, 0, cw, ch);
            _busy.SetBounds(0, 0, cw, ch);

            LayoutHome();
            LayoutOptimize();
            LayoutMonitor();
            LayoutHistory();
            LayoutTheme();
            LayoutBusy();
        }

        private void LayoutHome()
        {
            int W = _homeSection.Width;
            _homeCaption.Location = new Point(18, 120);
            _homeStateBig.Location = new Point(16, 138);
            _homeTransport.Location = new Point(20, 186);

            int cardW = _goCard.Width, gap = 18;
            int total = cardW * 2 + gap;
            int x = Math.Max(16, (W - total) / 2);
            _goCard.Location = new Point(x, 216);
            _ecoCard.Location = new Point(x + cardW + gap, 216);
        }

        private void LayoutOptimize()
        {
            int W = _optSection.Width, H = _optSection.Height;
            _optBottom.SetBounds(0, H - 58, W, 58);
            _optScroll.SetBounds(0, 114, W, H - 114 - 58);

            int inner = Math.Min(W - 36, 780);
            int x = Math.Max(8, (W - inner) / 2);
            int y = 0;
            int colW = (inner - 48 - 18) / 2;

            _profileBar.SetBounds(x, y, inner, 44);
            y += 54;

            // system optimizations: 5 switches, two columns
            _sysCard.SetBounds(x, y, inner, 40 + 3 * 34 + 14);
            PositionPair(_optBoxes[0], _optBoxes[1], _sysCard, colW, 44, 0);
            PositionPair(_optBoxes[2], _optBoxes[3], _sysCard, colW, 44, 1);
            _optBoxes[4].SetBounds(24, 44 + 2 * 34, colW, 30);
            y += _sysCard.Height + 12;

            // tray apps: 5 switches + close button
            _trayCard.SetBounds(x, y, inner, 40 + 3 * 34 + 40);
            for (int i = 0; i < _trayBoxes.Count; i++)
            {
                int row = i / 2, col = i % 2;
                _trayBoxes[i].SetBounds(24 + col * (colW + 18), 44 + row * 34, colW, 30);
            }
            _closeTray.Location = new Point(inner - 24 - _closeTray.Width, 44 + 3 * 34 + 4);
            y += _trayCard.Height + 12;

            // performance: freeze + edit-list, plan | wu-pause
            _perfCard.SetBounds(x, y, inner, 40 + 2 * 34 + 14);
            _freezeBox.SetBounds(24, 44, colW, 30);
            _editFreezeList.Location = new Point(inner - 24 - _editFreezeList.Width, 46);
            _planBox.SetBounds(24, 44 + 34, colW, 30);
            _wuPauseBox.SetBounds(24 + colW + 18, 44 + 34, colW, 30);
            y += _perfCard.Height + 12;

            // storage cleanup: fixed 4 + app-cache rows + dynamic notes
            EnsureAppCacheSwitches();
            int rows = 2 + (_appCacheBoxes.Count + 1) / 2;
            int notesH = (_gateLabel.Visible ? MeasureCardText(_gateLabel) + 6 : 0) +
                         (_oldLabel.Visible ? MeasureCardText(_oldLabel) + 6 : 0);
            _cleanCard.SetBounds(x, y, inner, 40 + rows * 34 + 14 + notesH);
            PositionPair(_cleanWuBox, _cleanDismBox, _cleanCard, colW, 44, 0);
            PositionPair(_cleanDeepBox, _cleanGpuBox, _cleanCard, colW, 44, 1);
            for (int i = 0; i < _appCacheBoxes.Count; i++)
            {
                int row = 2 + i / 2, col = i % 2;
                _appCacheBoxes[i].SetBounds(24 + col * (colW + 18), 44 + row * 34, colW, 30);
            }
            int noteY = 44 + rows * 34 + 4;
            if (_oldLabel.Visible)
            {
                _oldLabel.SetBounds(24, noteY, inner - 48, MeasureCardText(_oldLabel));
                noteY += _oldLabel.Height + 6;
            }
            if (_gateLabel.Visible)
            {
                _gateLabel.SetBounds(24, noteY, inner - 48, MeasureCardText(_gateLabel));
            }
            y += _cleanCard.Height + 12;

            _optScroll.AutoScrollMinSize = new Size(0, y + 8);

            _goBtn.Location = new Point(_optBottom.Width - _goBtn.Width - 28, 8);
            _optCancel.Location = new Point(_goBtn.Left - _optCancel.Width - 10, 13);
        }

        private static void PositionPair(ToggleSwitch left, ToggleSwitch right, Card card, int colW, int top, int row)
        {
            left.SetBounds(24, top + row * 34, colW, 30);
            if (right != null) right.SetBounds(24 + colW + 18, top + row * 34, colW, 30);
        }

        private int MeasureCardText(Label l)
        {
            Size s = TextRenderer.MeasureText(l.Text, l.Font, new Size(Math.Max(60, _cleanCard.Width - 48), 4000),
                TextFormatFlags.WordBreak);
            return Math.Max(18, s.Height + 2);
        }

        private void LayoutMonitor()
        {
            int W = _monSection.Width;
            int mw = Math.Min(W - 48, 720);
            _monitorPanel.SetBounds((W - mw) / 2, 120, mw, 240);
            _refreshLabel.Location = new Point((W - 260) / 2, 372);
            _refreshBox.Location = new Point((W - 260) / 2 + 170, 368);
            _overlayBtn.Location = new Point((W - _overlayBtn.Width) / 2, 412);
            _monHint.Location = new Point((W - 420) / 2, _overlayBtn.Bottom + 12);
        }

        private void LayoutHistory()
        {
            int W = _histSection.Width;
            int x = Math.Max(16, (W - _histViewLog.Width) / 2);
            _histViewLog.Location = new Point(x, 130);
            _histBrowser.Location = new Point(x, 130 + 76);
            _histSessions.Location = new Point(x, 130 + 152);
            _histPath.Location = new Point(x + 4, 130 + 152 + 76);
            _histPath.Text = Log.CurrentLogPath.Length > 0 ? "this run: " + Log.CurrentLogPath : "";
        }

        private void LayoutTheme()
        {
            int W = _themeSection.Width;
            int inner = 600;
            int x = Math.Max(16, (W - inner) / 2);
            int y = 130;

            _presetCard.SetBounds(x, y, inner, 116);
            int sw = 96, gap = 12;
            int total = _presetSwatches.Count * sw + (_presetSwatches.Count - 1) * gap;
            int sx = (inner - total) / 2;
            for (int i = 0; i < _presetSwatches.Count; i++)
            {
                _presetSwatches[i].SetBounds(sx + i * (sw + gap), 46, sw, 54);
            }
            y += 116 + 12;

            _accentCard.SetBounds(x, y, inner, 98);
            _accentSwatch.Location = new Point(24, 46);
            _accentPick.Location = new Point(190, 49);
            y += 98 + 12;

            _gradCard.SetBounds(x, y, inner, 152);
            _gradFromSwatch.Location = new Point(24, 46);
            _gradFromPick.Location = new Point(154, 49);
            _gradToSwatch.Location = new Point(300, 46);
            _gradToPick.Location = new Point(430, 49);
            _gradDirLabel.Location = new Point(24, 104);
            _gradDirBox.Location = new Point(110, 100);
            _gradToggle.SetBounds(300, 98, 260, 30);
            y += 152 + 12;

            _navCard.SetBounds(x, y, inner, 98);
            _navSwatch.Location = new Point(24, 46);
            _navPick.Location = new Point(190, 49);
            y += 98 + 16;

            _resetThemeBtn.Location = new Point(x + (inner - _resetThemeBtn.Width) / 2, y);
        }

        private void LayoutBusy()
        {
            int W = _busy.Width, H = _busy.Height;
            _spin.Location = new Point((W - _spin.Width) / 2, 64);
            _busyGlyph.Location = new Point((W - _busyGlyph.Width) / 2, 60);
            _busyTitle.SetBounds(24, 136, W - 48, 32);

            int trayH = _resultTrayAvailable ? 118 : 0;
            _busyText.SetBounds(40, 176, W - 80, Math.Max(60, H - 176 - 70 - trayH));

            if (_resultTrayAvailable)
            {
                _resTrayTitle.Location = new Point(40, _busyText.Bottom + 10);
                for (int i = 0; i < _trayBoxes.Count; i++)
                {
                    int row = i / 2, col = i % 2;
                    _trayBoxes[i].SetBounds(40 + col * ((W - 220) / 2), _resTrayTitle.Bottom + 2 + row * 32,
                        (W - 220) / 2, 28);
                }
                for (int i = 0; i < _trayBoxes.Count; i++) _busy.Controls.Add(_trayBoxes[i]);
                _resTrayClose.Location = new Point(W - 40 - _resTrayClose.Width, _resTrayTitle.Bottom + 6);
            }

            _resHomeBtn.Location = new Point(W - _resHomeBtn.Width - 36, H - 50);
            _resRestartBtn.Location = new Point(_resHomeBtn.Left - _resRestartBtn.Width - 10, H - 50);
            _resSessBtn.Location = new Point(_resRestartBtn.Left - _resSessBtn.Width - 10, H - 50);
            _resHistBtn.Location = new Point(_resSessBtn.Left - _resHistBtn.Width - 10, H - 50);
            _resLogBtn.Location = new Point(_resHistBtn.Left - _resLogBtn.Width - 10, H - 50);
            _confirmApplyBtn.Location = new Point(W / 2 - 110, H - 60);
            _confirmCancelBtn.Location = new Point(W / 2 + 10, H - 60);
        }

        // ---- section navigation ---------------------------------------------

        private void ShowSection(UiPhase section)
        {
            if (_phase == UiPhase.Applying) return;    // never navigate mid-flight
            _phase = section;
            _busy.Visible = false;
            _homeSection.Visible = section == UiPhase.Home;
            _optSection.Visible = section == UiPhase.Optimize;
            _monSection.Visible = section == UiPhase.Monitor;
            _histSection.Visible = section == UiPhase.History;
            _themeSection.Visible = section == UiPhase.Theme;
            _navHome.Active = section == UiPhase.Home;
            _navOpt.Active = section == UiPhase.Optimize;
            _navMon.Active = section == UiPhase.Monitor;
            _navHist.Active = section == UiPhase.History;
            _navTheme.Active = section == UiPhase.Theme;
            _bar.Visible = false;
            _bar.Active = false;
            if (section == UiPhase.Home) { _status.Text = "Choose a mode - the switch is one click away."; }
            if (section == UiPhase.History) LayoutHistory();
            if (section == UiPhase.Theme) LayoutTheme();
            if (section == UiPhase.Optimize) { LayoutOptimize(); StartMeasureOnce(); }
        }

        private void ShowMonitor()
        {
            if (_phase == UiPhase.Applying) return;
            _phase = UiPhase.Monitor;
            _busy.Visible = false;
            _homeSection.Visible = false;
            _optSection.Visible = false;
            _monSection.Visible = true;
            _histSection.Visible = false;
            _themeSection.Visible = false;
            _navHome.Active = false;
            _navOpt.Active = false;
            _navMon.Active = true;
            _navHist.Active = false;
            _navTheme.Active = false;
            EnsureMonitorEngine();
            _monitorPanel.AttachToEngine();
            _status.Text = "Live system monitor - the overlay stays over the game.";
        }

        private void ShowHistorySection()
        {
            ShowSection(UiPhase.History);
            _status.Text = "Every run is logged - this run included.";
        }

        private void ShowThemeSection()
        {
            ShowSection(UiPhase.Theme);
            _status.Text = "Theme - changes apply live and persist for the next session.";
        }

        // ---- probe / state ----------------------------------------------------

        private void EnterProbe()
        {
            _phase = UiPhase.Probe;
            SetNavEnabled(false);
            _busy.Visible = true;
            _spin.Visible = true;
            _busyGlyph.Visible = false;
            _busyTitle.Text = "Contacting ASUS hardware...";
            _busyText.Visible = false;
            ShowResultButtons(false, false);
            _bar.Visible = true;
            _bar.Active = true;
            _status.Text = "Probing...";
            Log.Info("UI: probing");

            RunBg(delegate
            {
                bool ok;
                string msg = AsusControl.Precheck(false, out ok);
                bool? es = EnergySaver.GetSavedState();
                SafeInvoke(delegate
                {
                    if (!ok)
                    {
                        EnterResult(false, "ASUS hardware interface not found", msg, null);
                        return;
                    }
                    _currentEco = AsusControl.GetDgpuState() == 1;
                    _esOn = es == null ? _currentEco : es.Value;
                    try
                    {
                        string[] lines = AsusControl.DescribeState().Replace("\r\n", "\n").Split('\n');
                        if (lines.Length > 0) _transport = lines[0].Trim();
                    }
                    catch { }
                    UpdateStateUi();
                    SetNavEnabled(true);

                    // (v1.2.2) --eco wins over --auto: "GPU Mode Switch.exe
                    // --eco --auto" used to fall into the go branch (the
                    // _autoMode check came first) and applied STANDARD while
                    // the caller asked for eco. Mode flags are checked before
                    // the auto/confirm flow now.
                    if (_startEco)
                    {
                        if (_autoMode)
                        {
                            Log.Info("UI: --eco --auto given, applying right away");
                            BeginEcoApply();
                        }
                        else if (_confirmMode) EnterEcoConfirm();
                        else BeginEcoApply();
                    }
                    else if (_autoMode || _startGo)
                    {
                        EnterOptimize();
                        if (_autoMode)
                        {
                            // --auto (unchanged semantics): apply ONLY the system
                            // optimizations + GPU switch, no selection stage.
                            Log.Info("UI: --auto given, applying right away");
                            BeginGoApply();
                        }
                    }
                    else
                    {
                        ShowSection(UiPhase.Home);
                    }
                });
            });
        }

        private void SetNavEnabled(bool on)
        {
            _navHome.Enabled = on;
            _navOpt.Enabled = on;
            _navMon.Enabled = on;
            _navHist.Enabled = on;
            _navTheme.Enabled = on;
        }

        // Paints the live state everywhere it shows: header chips, the Home
        // banner and the ACTIVE badges on the mode cards.
        private void UpdateStateUi()
        {
            string gpu = _currentEco ? "dGPU OFF (Eco)" : "dGPU ON (Standard)";
            SetChip(_chipGpu, gpu, _currentEco ? Ui.Eco : Ui.Go);
            SetChip(_chipEs, "Energy Saver " + (_esOn ? "ON" : "OFF"), _esOn ? Ui.Eco : Ui.TextDim);
            SetChip(_chipBus, _transport.Length > 0 ? _transport : "transport ?", Ui.Cyan);

            _homeStateBig.Text = _currentEco ? "ECO" : "STANDARD";
            _homeStateBig.ForeColor = _currentEco ? Ui.Eco : Ui.Go;
            _homeTransport.Text = _transport;
            _goCard.CardActive = !_currentEco;
            _ecoCard.CardActive = _currentEco;
        }

        // ---- optimize section --------------------------------------------------

        private void EnterOptimize()
        {
            if (_phase == UiPhase.Applying) return;
            _phase = UiPhase.Optimize;
            _busy.Visible = false;
            _homeSection.Visible = false;
            _optSection.Visible = true;
            _monSection.Visible = false;
            _histSection.Visible = false;
            _themeSection.Visible = false;
            _navHome.Active = false;
            _navOpt.Active = true;
            _navMon.Active = false;
            _navHist.Active = false;
            _navTheme.Active = false;
            _status.Text = "Ready - choose what GO applies, then press GO.";
            EnsureMonitorEngine();
            _monitorPanel.AttachToEngine();
            LayoutOptimize();
            StartMeasureOnce();
            BeginTrayDetect();
            Log.Info("UI: selection stage");
        }

        // One checkbox per installed app-cache target whose cache dirs resolved
        // non-empty (created once, laid out every pass).
        private void EnsureAppCacheSwitches()
        {
            if (_optLaidOut) return;
            _optLaidOut = true;
            List<AppCacheTarget> targets = AppCacheCleaner.Targets();
            for (int i = 0; i < targets.Count; i++)
            {
                AppCacheTarget t = targets[i];
                if (t.CacheDirs == null || t.CacheDirs.Length == 0) continue;
                ToggleSwitch sw = MakeSwitch("Clean " + t.Name + " cache", false, Ui.Cyan);
                sw.Tag = "clean.appcache." + t.Name;
                sw.Name = t.Name;
                _appCacheBoxes.Add(sw);
                _cleanCard.Controls.Add(sw);
            }
        }

        private void ShowFreezeListEditor()
        {
            Log.Info("UI: freeze list editor opened");
            using (FreezeListEditorForm f = new FreezeListEditorForm())
            {
                f.ShowDialog(this);
            }
        }

        // Starts a tray detection round: rows show "Scanning...", the
        // background thread detects, rows populate (shared by the optimize
        // deck and the result overlay).
        private void BeginTrayDetect()
        {
            foreach (ToggleSwitch sw in _trayBoxes)
            {
                sw.Text = "Scanning...";
                sw.Checked = false;
                sw.Enabled = false;
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
                _trayBoxes[i].Text = a.Label + (a.Running ? "  (running)" : "  (not running)");
                _trayBoxes[i].Checked = a.Running;
                _trayBoxes[i].Enabled = a.Running;
            }
        }

        private void BeginCloseTrayApps()
        {
            if (_trayBusy) return;
            List<TrayAppInfo> selected = GatherSelectedTrayApps();
            if (selected.Count == 0) return;

            _trayBusy = true;
            _resTrayClose.Enabled = false;
            _closeTray.Enabled = false;
            Log.Chan("TRAY", "TrayApps: closing " + selected.Count + " selected app(s)");
            RunBg(delegate
            {
                foreach (TrayAppInfo a in selected) TrayApps.Close(a);
                TrayApps.Detect();
                SafeInvoke(delegate
                {
                    PopulateTrayRows();
                    _trayBusy = false;
                    _resTrayClose.Enabled = true;
                    _closeTray.Enabled = true;
                });
            });
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

        // Background measurement (strictly read-only, the RunBg pattern): when
        // it lands, switches get their measured sizes and gate reasons lock
        // the whole cleanup card with the reasons shown in an amber banner.
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
                List<string> gatesSvc = StorageAnalyzer.CheckGatesServicing();
                SafeInvoke(delegate { ApplyMeasurements(tier1, deep, gpu, app, runningTargets, gates, gatesSvc); });
            });
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

        private void ApplyMeasurements(List<CleanCategory> tier1, List<CleanCategory> deep,
            List<CleanCategory> gpu, List<CleanCategory> app, List<string> runningTargets,
            List<string> gates, List<string> gatesSvc)
        {
            _tier1Measured = tier1;
            _gateReasons = gates;
            _gateReasonsSvc = gatesSvc;
            EnsureAppCacheSwitches();

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
                if (c.Name.IndexOf("shader cache", StringComparison.OrdinalIgnoreCase) >= 0) shaderBytes += c.Bytes;
            }
            _cleanGpuBox.Text = "GPU shader caches" + SizeSuffix(shaderBytes);

            foreach (ToggleSwitch sw in _appCacheBoxes)
            {
                string name = sw.Name;
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
                sw.Text = "Clean " + name + " cache" + SizeSuffix(bytes) + (running ? " (app running)" : "");
                if (running) sw.Checked = false;    // a running app starts UNchecked
            }

            foreach (CleanCategory c in deep)
            {
                if (c.Name == DeepClean.CatPreviousInstallations && c.Bytes > 0)
                {
                    _oldLabel.Text = "also found: previous Windows installations " +
                        StorageCleaner.FormatBytes(c.Bytes) + " (report only - never deleted)";
                    _oldLabel.Visible = true;
                }
            }

            // D7 gates, scoped (v1.2.1): global gates (elevation missing)
            // lock the whole cleanup card; SERVICING gates (pending reboot,
            // WU busy) lock only the Windows Update / component store rows -
            // temp, shader and browser caches stay available (a pending
            // rename cannot interact with them, and v1.2.0's global lock
            // froze the whole section on a normal machine). The cleaners
            // re-check the gates at GO.
            if (gates != null && gates.Count > 0)
            {
                _gateLabel.Text = "LOCKED - cleanup unavailable right now: " + string.Join("; ", gates.ToArray());
                _gateLabel.Visible = true;
                _cleanWuBox.Enabled = false;
                _cleanDismBox.Enabled = false;
                _cleanDeepBox.Enabled = false;
                _cleanGpuBox.Enabled = false;
                foreach (ToggleSwitch sw in _appCacheBoxes) sw.Enabled = false;
                foreach (string reason in gates) Log.Warn("cleanup gate: " + reason);
            }
            else if (gatesSvc != null && gatesSvc.Count > 0)
            {
                _gateLabel.Text = "Windows Update / component store rows LOCKED (restart Windows to clear): " +
                    string.Join("; ", gatesSvc.ToArray());
                _gateLabel.Visible = true;
                _cleanWuBox.Enabled = false;
                _cleanDismBox.Enabled = false;
                foreach (string reason in gatesSvc) Log.Warn("cleanup gate (servicing): " + reason);
            }

            LayoutOptimize();
        }

        // ---- apply: Go Time ----------------------------------------------------

        private void BeginGoApply()
        {
            if (_phase != UiPhase.Optimize && _phase != UiPhase.Probe) return;
            _phase = UiPhase.Applying;
            SetNavEnabled(false);
            ShowBusy("Applying GO TIME...", true);

            // Capture the launch-time selections (UI thread).
            // --auto semantics (unchanged): ONLY the system optimizations + GPU
            // switch; the performance and cleanup groups never run unattended.
            bool sessionFeatures = !_autoMode;
            List<TrayAppInfo> toClose = GatherSelectedTrayApps();
            bool fGameMode = _optBoxes[0].Checked;
            bool fDnd = _optBoxes[1].Checked;
            bool fDvr = _optBoxes[2].Checked;
            bool fThrottle = _optBoxes[3].Checked;
            bool fServices = _optBoxes[4].Checked;
            bool gatesClear = _gateReasons == null || _gateReasons.Count == 0;
            bool gatesSvcClear = _gateReasonsSvc == null || _gateReasonsSvc.Count == 0;
            bool pFreeze = sessionFeatures && _freezeBox.Checked;
            bool pPlan = sessionFeatures && _planBox.Checked;
            bool pWuPause = sessionFeatures && _wuPauseBox.Checked;
            bool cWu = sessionFeatures && gatesClear && gatesSvcClear && _cleanWuBox.Checked;
            bool cDism = sessionFeatures && gatesClear && gatesSvcClear && _cleanDismBox.Checked;
            bool cDeep = sessionFeatures && gatesClear && _cleanDeepBox.Checked;
            bool cGpu = sessionFeatures && gatesClear && _cleanGpuBox.Checked;
            List<string> cleanApps = new List<string>();
            if (sessionFeatures && gatesClear)
            {
                foreach (ToggleSwitch sw in _appCacheBoxes)
                {
                    if (sw.Checked) cleanApps.Add(sw.Name);
                }
            }
            Log.Info("UI: selections - GameMode=" + fGameMode + " DND=" + fDnd + " DVR=" + fDvr +
                        " Throttle=" + fThrottle + " Services=" + fServices + " TrayToClose=" + toClose.Count +
                        " Freeze=" + pFreeze + " Plan=" + pPlan + " WUPause=" + pWuPause +
                        " Cleanup(WU=" + cWu + " DISM=" + cDism + " Deep=" + cDeep + " GPU=" + cGpu +
                        " AppCaches=" + cleanApps.Count + ")");

            System.Diagnostics.Stopwatch goWatch = System.Diagnostics.Stopwatch.StartNew();
            RunBg(delegate
            {
                foreach (TrayAppInfo a in toClose) TrayApps.Close(a);
                SwitchOutcome r;
                try
                {
                    r = AsusControl.SwitchTo(false);
                }
                catch (Exception ex)
                {
                    Log.Error("UNEXPECTED ERROR", ex);
                    r = new SwitchOutcome();
                    r.Headline = "Unexpected error";
                    r.Detail = ex.Message + "\n\nFull details: View log.";
                }
                string prep = "";
                string cleanupSummary = "";
                if (r.Ok)
                {
                    prep = GamePrep.ApplyForGaming(fGameMode, fDnd, fDvr, fThrottle, fServices);
                    if (prep.Length > 0) r.Detail += "\n" + prep;
                }
                if (r.Ok && sessionFeatures)
                {
                    GoExtraResult extra = RunGoSession(pFreeze, pPlan, pWuPause, cWu, cDism, cDeep, cGpu, cleanApps);
                    if (extra.DetailLines.Length > 0) r.Detail += extra.DetailLines;
                    cleanupSummary = extra.CleanupBlock;

                    SessionRecord rec = new SessionRecord();
                    rec.App = "GPU Mode Switch";
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
                string sumCopy = cleanupSummary;
                SafeInvoke(delegate
                {
                    _cleanupSummary = sumCopy;
                    _currentEco = false;
                    _esOn = false;
                    if (r.Ok && sessionFeatures) AfterGoSession();
                    EnterResult(r.Ok, r.Headline, r.Detail, r);
                });
            });
        }

        // ---- apply: Eco --------------------------------------------------------

        private void EnterEcoConfirm()
        {
            _phase = UiPhase.Confirm;
            SetNavEnabled(false);
            _busy.Visible = true;
            _spin.Visible = false;
            _busyGlyph.Visible = false;
            _busyTitle.Text = "Switch to ECO MODE?";
            _busyText.Visible = true;
            _busyText.Text = "The dGPU powers off completely (battery friendly, silent).\n" +
                "Energy Saver engages silently; any GO session state (frozen apps,\n" +
                "power plan, paused Windows Update) is restored.\n\n" +
                "Reversible at any time - press GO TIME on Home to switch back.";
            ShowResultButtons(false, false);
            _confirmApplyBtn.Visible = true;
            _confirmCancelBtn.Visible = true;
            _status.Text = "Ready to apply Eco Mode";
            Log.Info("UI: waiting for Apply (eco)");
        }

        private void BeginEcoApply()
        {
            if (_phase == UiPhase.Applying) return;
            _phase = UiPhase.Applying;
            SetNavEnabled(false);
            ShowBusy("Applying ECO MODE...", true);
            Log.Info("UI: applying (eco)");

            RunBg(delegate
            {
                SwitchOutcome r;
                try
                {
                    r = AsusControl.SwitchTo(true);
                }
                catch (Exception ex)
                {
                    Log.Error("UNEXPECTED ERROR", ex);
                    r = new SwitchOutcome();
                    r.Headline = "Unexpected error";
                    r.Detail = ex.Message + "\n\nFull details: View log.";
                }
                // Eco-safe restore (belt and braces): make sure no earlier GO
                // session leaves frozen processes, a foreign power plan or a
                // paused Windows Update behind.
                SessionSafety.RestoreAll();
                SwitchOutcome ro = r;
                SafeInvoke(delegate
                {
                    _currentEco = true;
                    _esOn = true;
                    DisposeOverlay();
                    try { if (MonitorEngine.Running) MonitorEngine.Stop(); } catch { }
                    EnterResult(ro.Ok, ro.Headline, ro.Detail, ro);
                });
            });
        }

        // ---- GO session features (v1.1 chain, unchanged semantics) -------------

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

        private GoExtraResult RunGoSession(bool pFreeze, bool pPlan, bool pWuPause,
            bool cWu, bool cDism, bool cDeep, bool cGpu, List<string> cleanApps)
        {
            GoExtraResult outc = new GoExtraResult();

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

            // Servicing gates (pending reboot / WU busy) scope to the WU purge
            // and component store rows only - deep clean, shader caches and
            // app caches never touch servicing state (v1.2.1).
            if (cWu || cDism)
            {
                List<string> gateReasonsSvc = StorageAnalyzer.CheckGatesServicing();
                if (gateReasonsSvc.Count > 0)
                {
                    if (cWu) outc.DetailLines += "\nWindows Update cache purge skipped - restart Windows first (see log)";
                    if (cDism) outc.DetailLines += "\nComponent store cleanup skipped - restart Windows first (see log)";
                    cWu = false;
                    cDism = false;
                }
            }

            bool anyCleanup2 = cWu || cDism || cDeep || cGpu ||
                (cleanApps != null && cleanApps.Count > 0);
            if (!anyCleanup2) return outc;

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

        // ---- session tray + overlay --------------------------------------------

        // The GO session is active: this form owns the one MonitorEngine (D6)
        // and the session tray (D4) with its five callbacks. In the unified
        // app the tray's eco item is the full eco switch (in-process).
        private void AfterGoSession()
        {
            EnsureMonitorEngine();
            if (_tray != null) return;
            _tray = new SessionTray(
                delegate { Show(); Activate(); },           // open window
                BeginEcoApply,                              // "Go Eco" = full switch + restore
                ToggleOverlay,                              // overlay toggle
                SessionStatusText,                          // status balloon text
                BeginExitApp);                              // clean shutdown
            SessionSafety.ActiveTray = _tray;
            _tray.Show("GO session active");
            Log.Chan("TRAY", "session tray created (GO session active)");
        }

        private void EnsureMonitorEngine()
        {
            if (!MonitorEngine.Running) MonitorEngine.Start(MonitorEngine.SavedIntervalMs);
        }

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

        // ---- profiles -------------------------------------------------------------

        private Dictionary<string, bool> CollectAllSelections()
        {
            Dictionary<string, bool> map = new Dictionary<string, bool>();
            AppendSelections(_optScroll, map);
            AppendSelections(_busy, map);
            return map;
        }

        private static void AppendSelections(Control root, Dictionary<string, bool> map)
        {
            foreach (Control c in root.Controls)
            {
                ToggleSwitch sw = c as ToggleSwitch;
                if (sw == null || sw.Tag == null) continue;
                map[(string)sw.Tag] = sw.Checked;
            }
        }

        private void ApplyProfileSelections(string name, Dictionary<string, bool> selections)
        {
            if (selections == null) return;
            foreach (KeyValuePair<string, bool> kv in selections)
            {
                ToggleSwitch target = FindSwitchByTag(kv.Key);
                if (target == null)
                {
                    Log.Chan("PROFILE", "profile apply: unknown key '" + kv.Key + "' ignored");
                    continue;
                }
                if (!target.Enabled)
                {
                    Log.Chan("PROFILE", "profile apply: '" + kv.Key + "' is locked (safety gates) - left unchanged");
                    continue;
                }
                target.Checked = kv.Value;
            }
            Log.Chan("PROFILE", "profile applied: " + name);
        }

        private ToggleSwitch FindSwitchByTag(string tag)
        {
            ToggleSwitch t = FindSwitchIn(_optScroll, tag);
            if (t != null) return t;
            return FindSwitchIn(_busy, tag);
        }

        private static ToggleSwitch FindSwitchIn(Control root, string tag)
        {
            foreach (Control c in root.Controls)
            {
                ToggleSwitch sw = c as ToggleSwitch;
                if (sw != null && sw.Tag != null &&
                    string.Equals((string)sw.Tag, tag, StringComparison.Ordinal))
                {
                    return sw;
                }
            }
            return null;
        }

        // ---- busy / result ----------------------------------------------------------

        private void ShowBusy(string title, bool spinner)
        {
            _busy.Visible = true;
            // (z fixed at construction: busy is above the sections, below the header strip)
            _spin.Visible = spinner;
            _busyGlyph.Visible = false;
            _busyTitle.Text = title;
            _busyTitle.ForeColor = Ui.TextHi;
            _busyText.Visible = false;
            _resultTrayAvailable = false;
            _resTrayTitle.Visible = false;
            _resTrayClose.Visible = false;
            ShowResultButtons(false, false);
            _confirmApplyBtn.Visible = false;
            _confirmCancelBtn.Visible = false;
            _bar.Visible = true;
            _bar.Active = true;
            _status.Text = title;
            LayoutBusy();
        }

        private void ShowResultButtons(bool resultButtons, bool restart)
        {
            _resLogBtn.Visible = resultButtons;
            _resHistBtn.Visible = resultButtons;
            _resSessBtn.Visible = resultButtons;
            _resHomeBtn.Visible = resultButtons;
            _resRestartBtn.Visible = resultButtons && restart;
        }

        private void EnterResult(bool ok, string headline, string detail, SwitchOutcome r)
        {
            _phase = UiPhase.Result;
            _last = r;
            SetNavEnabled(true);
            UpdateStateUi();

            _busy.Visible = true;
            // (z fixed at construction)
            _spin.Visible = false;
            _busyGlyph.Visible = true;
            if (Ui.HasGlyphs)
            {
                _busyGlyph.Text = ok ? "\uE73E" : "\uE7BA";       // check / warning
            }
            else
            {
                _busyGlyph.Text = ok ? "OK" : "!";
            }
            _busyGlyph.ForeColor = ok ? Ui.Eco : Ui.Red;
            _busyTitle.Text = (ok ? "" : "Failed - ") + headline;
            _busyTitle.ForeColor = ok ? Ui.TextHi : Ui.Red;

            string text = detail == null ? "" : detail;
            if (_cleanupSummary.Length > 0)
            {
                text = text + "\n\n" + _cleanupSummary;
                _cleanupSummary = "";
            }
            _busyText.Visible = true;
            _busyText.Text = text;
            _bar.Visible = false;
            _bar.Active = false;
            _confirmApplyBtn.Visible = false;
            _confirmCancelBtn.Visible = false;
            ShowResultButtons(true, r != null && r.Ok && r.NeedsRestart);
            _status.Text = (ok ? "OK - " : "Failed - ") + headline;

            // v1.0.22 behavior kept: the tray picker stays available after a
            // switch - in the unified app it lives in the result overlay.
            _resultTrayAvailable = true;
            _resTrayTitle.Visible = true;
            _resTrayClose.Visible = true;
            LayoutBusy();
            BeginTrayDetect();
        }

        // ---- plumbing ------------------------------------------------------------

        private static Bitmap LoadResourcePng(string name)
        {
            try
            {
                Stream s = typeof(Program).Assembly.GetManifestResourceStream(name);
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
            if (_spin.Visible) _spin.Advance();
        }

        // Borderless window: the bare 6px frame resizes, any bare interior
        // panel background drags (MakeDraggable).
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            const int HTCLIENT = 1;
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
                    int e = 8;
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
                }
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Region = new Region(UiShapes.RoundRect(0, 0, ClientSize.Width, ClientSize.Height, 22));
        }

        private void RunBg(ThreadStart work)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { work(); }
                catch (Exception ex)
                {
                    Log.Error("BACKGROUND ERROR", ex);
                    // Abnormal end of a background step: eco-safe restore so a
                    // half-applied session is never left behind.
                    try { SessionSafety.RestoreAll(); } catch { }
                    SafeInvoke(delegate
                    {
                        EnterResult(false, "Unexpected error",
                            ex.Message + "\n\nFull details: View log.", null);
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
    }
}
