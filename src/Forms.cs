//  Forms.cs  (v1.1.0 - Wave 4)
//  ---------------------------
//  The windows of both apps: the themed borderless main window with its
//  phase machine and selection stage (MainForm + UiPhase), the log viewer
//  (LogForm) and the Go Time tray-app picker data (TrayAppInfo / TrayApps,
//  MODE_STANDARD only). Per-class comments below.
//
//  Split out of GpuModeSwitch.cs (Wave 2, zero behavior change). Wave 4
//  upgraded LogForm into a full log viewer (history over past per-run
//  logs, severity filter, find-next, open-folder); the rest of the file
//  is unchanged.

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
            _optTitle.Text = "System optimizations";
            _optTitle.ForeColor = Color.FromArgb(165, 165, 172);
            _optTitle.AutoSize = true;
            _optTitle.Location = new Point(24, 148);
            _optTitle.BackColor = Color.Transparent;
            _optTitle.Visible = false;

            string[] optLabels = { "Game Mode", "Do not disturb", "Game DVR recording off", "Network throttling off", "Pause background services" };
            Point[] optSpots =
            {
                new Point(24, 176), new Point(300, 176),
                new Point(24, 202), new Point(300, 202),
                new Point(24, 228), new Point(300, 228)
            };
            for (int i = 0; i < optLabels.Length; i++)
            {
                CheckBox cb = new CheckBox();
                cb.Text = optLabels[i];
                cb.AutoSize = true;
                cb.Location = optSpots[i];
                cb.ForeColor = Color.FromArgb(200, 200, 210);
                cb.BackColor = Color.Transparent;
                cb.Checked = true;
                cb.Visible = false;
                _optBoxes.Add(cb);
                Controls.Add(cb);
            }
            Controls.Add(_optTitle);

            _trayTitle.Text = "Tray apps detected:";
            _trayTitle.ForeColor = Color.FromArgb(165, 165, 172);
            _trayTitle.AutoSize = true;
            _trayTitle.Location = new Point(24, 258);
            _trayTitle.BackColor = Color.Transparent;
            _trayTitle.Visible = false;

            string[] trayLabels = { "Parsec", "Google Drive", "Jellyfin", "Riot Client", "Riot Vanguard" };
            Point[] traySpots =
            {
                new Point(24, 282), new Point(300, 282),
                new Point(24, 308), new Point(300, 308),
                new Point(24, 334)
            };
            for (int i = 0; i < trayLabels.Length; i++)
            {
                CheckBox cb = new CheckBox();
                cb.Text = trayLabels[i];
                cb.AutoSize = true;
                cb.Location = traySpots[i];
                cb.ForeColor = Color.FromArgb(200, 200, 210);
                cb.BackColor = Color.Transparent;
                cb.Visible = false;
                cb.Enabled = false;
                _trayBoxes.Add(cb);
                Controls.Add(cb);
            }

            _closeTray.Text = "Close selected";
            _closeTray.FlatStyle = FlatStyle.Flat;
            _closeTray.FlatAppearance.BorderColor = _accent;
            _closeTray.ForeColor = Color.White;
            _closeTray.BackColor = Color.FromArgb(42, 42, 49);
            _closeTray.Size = new Size(150, 30);
            _closeTray.Location = new Point(300, 330);
            _closeTray.TabStop = false;
            _closeTray.Visible = false;
            _closeTray.Click += delegate { BeginCloseTrayApps(); };

            Controls.Add(_trayTitle);
            Controls.Add(_closeTray);
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
                AsusControl.Shutdown();
            };
        }

#if MODE_STANDARD
        private void ShowTraySection()
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
                _trayBoxes[i].Text = a.Label + (a.Running ? "  (running)" : "  (not running)");
                _trayBoxes[i].Checked = a.Running;
                _trayBoxes[i].Enabled = a.Running;
            }
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
        // Selection stage: both toggle groups are shown at launch and the
        // user decides which optimizations (and tray apps) to apply.
        private void EnterSelect(string precheckText)
        {
            _phase = UiPhase.Confirm;
            _bar.Active = false;
            HideAllButtons();
            _optTitle.Visible = true;
            foreach (CheckBox cb in _optBoxes) { cb.Visible = true; cb.Checked = true; cb.Enabled = true; }
            ShowTraySection();
            _apply.Text = "GO";
            _apply.Visible = true;
            _cancel.Visible = true;
            _status.ForeColor = Color.FromArgb(235, 235, 240);
            _status.Text = "Ready - choose optimizations, then press GO";
            _detail.Text = precheckText;
            Log.Info("UI: selection stage");
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

#if MODE_STANDARD
            // Capture the launch-time selections (UI thread).
            List<TrayAppInfo> toClose = GatherSelectedTrayApps();
            bool fGameMode = _optBoxes[0].Checked;
            bool fDnd = _optBoxes[1].Checked;
            bool fDvr = _optBoxes[2].Checked;
            bool fThrottle = _optBoxes[3].Checked;
            bool fServices = _optBoxes[4].Checked;
            HideOptGroup();
            _trayTitle.Visible = false;
            foreach (CheckBox cb in _trayBoxes) cb.Visible = false;
            Log.Info("UI: selections - GameMode=" + fGameMode + " DND=" + fDnd + " DVR=" + fDvr +
                        " Throttle=" + fThrottle + " Services=" + fServices + " TrayToClose=" + toClose.Count);
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
#if MODE_STANDARD
                if (r.Ok)
                {
                    string prep = GamePrep.ApplyForGaming(fGameMode, fDnd, fDvr, fThrottle, fServices);
                    if (prep.Length > 0) r.Detail += "\n" + prep;
                }
#endif
                SafeInvoke(delegate { EnterResult(r.Ok, r.Headline, r.Detail, r); });
            });
        }

#if MODE_STANDARD
        private void HideOptGroup()
        {
            _optTitle.Visible = false;
            foreach (CheckBox cb in _optBoxes) cb.Visible = false;
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
            _status.Text = (ok ? "OK - " : "Failed - ") + headline;
            _status.ForeColor = ok ? _accent : Color.FromArgb(255, 120, 120);
            _detail.Text = detail;
            _logBtn.Visible = true;
            _logBtn.FlatAppearance.BorderColor = ok ? Color.FromArgb(90, 90, 98) : _accent;
            _close.Visible = true;
            if (r != null && r.Ok && r.NeedsRestart) _restart.Visible = true;
#if MODE_STANDARD
            ShowTraySection();
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
