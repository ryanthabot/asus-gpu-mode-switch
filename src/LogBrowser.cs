//  LogBrowser.cs  (v1.1.0 - Wave 4)
//  ------------------
//  Log-history debugging window (LogBrowserForm). Lists every log file from
//  BOTH apps' folders (%LOCALAPPDATA%\GpuModeSwitch\logs\GoTime and
//  ...\EcoMode, resolved through Log.LogsRoot) merged newest first, shows
//  the selected file in a read-only monospace viewer (files over ~2 MB load
//  only their tail, with a notice line), and searches:
//    - "Search"        find the next match in the viewed file (wraps);
//    - "Search all"    every matching line across all listed logs, listed
//                      with file name + line number (capped at 500 hits,
//                      truncation notice); clicking a hit opens that file
//                      at that line.
//  The toolbar also has Open folder (Explorer /select on the selected file)
//  and Copy view (the viewer text is pre-selected on load so Ctrl+C works
//  immediately, exactly like LogForm). Every file open and every search is
//  logged through Log.Info ("log browser: ...").
//
//  Open with LogBrowserForm.ShowBrowser(owner): non-modal, owned by the
//  caller form so it never orphans. Reachable from Go Time only for now
//  (the menu entry is wired by a later wave); the file compiles into both
//  exe targets.
//
//  New file for v1.1.0 (Wave 4, A5) per docs\HANDBOOK.md section 8.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // Log-history browser: file list (both apps, newest first), read-only
    // viewer with find-next, search across all logs, open-in-Explorer and
    // copy. Colors are inline at the call sites, exactly as in Forms.cs.
    // ---------------------------------------------------------------------
    internal class LogBrowserForm : Form
    {
        // Viewer load cap: files bigger than this show only their last ~2 MB.
        private const long MaxLoadBytes = 2L * 1024 * 1024;
        // Search-all result cap (further hits are dropped, with a notice).
        private const int MaxHits = 500;
        // Characters kept from each matching line in the results list.
        private const int MaxSnippetChars = 300;

        private readonly ListView _files = new ListView();
        private readonly ListView _results = new ListView();
        private readonly TextBox _viewer = new TextBox();
        private readonly TextBox _searchBox = new TextBox();
        private readonly Button _searchBtn = new Button();
        private readonly Button _searchAllBtn = new Button();
        private readonly Button _openFolderBtn = new Button();
        private readonly Button _copyBtn = new Button();
        private readonly Button _refreshBtn = new Button();
        private readonly Label _status = new Label();
        private readonly Label _resultsCaption = new Label();
        private readonly FlowLayoutPanel _toolbar = new FlowLayoutPanel();
        private readonly SplitContainer _split = new SplitContainer();
        private readonly SplitContainer _rightSplit = new SplitContainer();
        private readonly System.Windows.Forms.Timer _copyFlash = new System.Windows.Forms.Timer();

        private readonly List<LogFileEntry> _entries = new List<LogFileEntry>();
        private bool _populating;       // suppress selection handlers while filling lists
        private bool _searching;        // one search-all at a time
        private bool _viewTruncated;    // the viewer currently shows only a file tail
        private string _currentPath = "";

        public LogBrowserForm()
        {
            Text = "Log History";
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(980, 640);
            MinimumSize = new Size(760, 520);
            BackColor = Ui.PopupBack;
            Font = new Font("Segoe UI", 9f);
            WindowIcons.Apply(this);
            DarkChrome.Apply(this);     // native title bar follows the dark body

            // v1.2.3: the form is constructed fresh on every open, so colors
            // read here follow the live theme; the public ApplyTheme covers a
            // form kept open across a theme change.
            BuildToolbar();
            BuildFileList();
            BuildViewer();
            BuildResults();
            BuildStatus();
            BuildLayout();

            _copyFlash.Interval = 1500;
            _copyFlash.Tick += delegate
            {
                _copyFlash.Stop();
                _copyBtn.Text = "Copy view";
            };

            // Splitter distances are only sane once the form has real bounds.
            // The panel minimums must wait too: both SplitContainers sit at
            // their default 150x100 size in the constructor and setting them
            // there throws (SplitterDistance range check).
            Shown += delegate
            {
                _split.Panel1MinSize = 240;
                _split.Panel2MinSize = 240;
                _rightSplit.Panel1MinSize = 120;
                _rightSplit.Panel2MinSize = 100;
                try { _split.SplitterDistance = 520; } catch { }
                if (!_rightSplit.Panel2Collapsed) TryGiveResultsSpace();
            };

            FormClosed += delegate { _copyFlash.Dispose(); };

            Repopulate();
            Log.Info("log browser: opened (" + _entries.Count + " log files)");
        }

        // Opens the browser as a non-modal window owned by "owner" (falls
        // back to an unowned window if the owner is already gone).
        public static void ShowBrowser(Form owner)
        {
            LogBrowserForm browser = new LogBrowserForm();
            if (owner != null && !owner.IsDisposed)
            {
                browser.Show(owner);
            }
            else
            {
                browser.Show();
            }
        }

        // ---- UI construction ------------------------------------------------

        private void BuildToolbar()
        {
            Label find = new Label();
            find.Text = "Find:";
            find.ForeColor = Ui.PopupTextDim;
            find.BackColor = Ui.PopupBack;
            find.AutoSize = true;
            find.Margin = new Padding(6, 9, 2, 3);

            _searchBox.Width = 250;
            _searchBox.BackColor = Ui.PopupListBack;
            _searchBox.ForeColor = Ui.PopupListText;
            _searchBox.BorderStyle = BorderStyle.FixedSingle;
            _searchBox.Margin = new Padding(2, 6, 6, 6);
            _searchBox.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    FindNextInView();
                }
            };

            InitToolButton(_searchBtn, "Search", true);
            InitToolButton(_searchAllBtn, "Search all logs", false);
            InitToolButton(_openFolderBtn, "Open folder", false);
            InitToolButton(_copyBtn, "Copy view", false);
            InitToolButton(_refreshBtn, "Refresh", false);

            _searchBtn.Click += delegate { FindNextInView(); };
            _searchAllBtn.Click += delegate { RunSearchAll(); };
            _openFolderBtn.Click += delegate { OpenFolder(); };
            _copyBtn.Click += delegate { CopyView(); };
            _refreshBtn.Click += delegate { Repopulate(); };

            _toolbar.Controls.Add(find);
            _toolbar.Controls.Add(_searchBox);
            _toolbar.Controls.Add(_searchBtn);
            _toolbar.Controls.Add(_searchAllBtn);
            _toolbar.Controls.Add(_openFolderBtn);
            _toolbar.Controls.Add(_copyBtn);
            _toolbar.Controls.Add(_refreshBtn);
        }

        // Flat dark buttons, the one shared recipe (Ui.StyleToolButton).
        private static void InitToolButton(Button b, string text, bool primary)
        {
            b.Text = text;
            b.AutoSize = true;
            Ui.StyleToolButton(b, primary);
            b.Margin = new Padding(2, 6, 2, 6);
            b.TabStop = false;
        }

        private void BuildFileList()
        {
            _files.View = View.Details;
            _files.FullRowSelect = true;
            _files.MultiSelect = false;
            _files.BackColor = Ui.PopupListBack;
            _files.ForeColor = Ui.PopupListText;
            _files.BorderStyle = BorderStyle.FixedSingle;
            _files.Dock = DockStyle.Fill;
            _files.Sorting = SortOrder.None;        // order comes from _entries (newest first)
            _files.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _files.OwnerDraw = true;                // dark headers + dark rows (Ui helpers)
            _files.DrawColumnHeader += Ui.DrawListHeader;
            _files.DrawItem += Ui.DrawListItem;
            _files.DrawSubItem += Ui.DrawListCell;
            _files.Columns.Add("App", 80);
            _files.Columns.Add("File", 250);
            _files.Columns.Add("Size (KB)", 85, HorizontalAlignment.Right);
            _files.Columns.Add("Last write", 135);
            _files.SelectedIndexChanged += OnFileSelected;
        }

        private void BuildViewer()
        {
            _viewer.Multiline = true;
            _viewer.ReadOnly = true;
            _viewer.ScrollBars = ScrollBars.Both;
            _viewer.WordWrap = false;
            _viewer.BackColor = Ui.PopupListBack;
            _viewer.ForeColor = Ui.PopupListText;
            _viewer.BorderStyle = BorderStyle.FixedSingle;
            _viewer.Font = new Font("Consolas", 9f);
            _viewer.Dock = DockStyle.Fill;
            _viewer.HideSelection = false;      // keep the selection visible without focus
        }

        private void BuildResults()
        {
            _results.View = View.Details;
            _results.FullRowSelect = true;
            _results.MultiSelect = false;
            _results.BackColor = Ui.PopupListBack;
            _results.ForeColor = Ui.PopupListText;
            _results.BorderStyle = BorderStyle.FixedSingle;
            _results.Dock = DockStyle.Fill;
            _results.Sorting = SortOrder.None;
            _results.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _results.OwnerDraw = true;          // dark headers + dark rows (Ui helpers)
            _results.DrawColumnHeader += Ui.DrawListHeader;
            _results.DrawItem += Ui.DrawListItem;
            _results.DrawSubItem += Ui.DrawListCell;
            _results.Columns.Add("App", 80);
            _results.Columns.Add("File", 200);
            _results.Columns.Add("Line", 55, HorizontalAlignment.Right);
            _results.Columns.Add("Match", 460);
            _results.SelectedIndexChanged += OnResultSelected;

            _resultsCaption.Text = "Search results";
            _resultsCaption.ForeColor = Ui.PopupTextDim;
            _resultsCaption.BackColor = Ui.PopupBack;
            _resultsCaption.AutoSize = false;
            _resultsCaption.Height = 22;
            _resultsCaption.TextAlign = ContentAlignment.MiddleLeft;
            _resultsCaption.Padding = new Padding(6, 2, 6, 0);
            _resultsCaption.Dock = DockStyle.Top;
        }

        private void BuildStatus()
        {
            _status.Text = "Select a log on the left.";
            _status.ForeColor = Ui.PopupTextDim;
            _status.BackColor = Ui.PopupBack;
            _status.AutoSize = false;
            _status.Height = 26;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Padding = new Padding(12, 4, 12, 0);
            _status.AutoEllipsis = true;
        }

        // TableLayoutPanel shell (like LogForm): toolbar / split / status -
        // deterministic at any DPI and window size.
        private void BuildLayout()
        {
            _split.Dock = DockStyle.Fill;
            _split.Orientation = Orientation.Vertical;          // file list | viewer
            _split.BackColor = Ui.PopupBack;
            _split.Panel1.BackColor = Ui.PopupBack;
            _split.Panel2.BackColor = Ui.PopupBack;
            // (min sizes set in Shown - see the note there)
            _split.Panel1.Controls.Add(_files);
            _split.Panel2.Controls.Add(_rightSplit);

            _rightSplit.Dock = DockStyle.Fill;
            _rightSplit.Orientation = Orientation.Horizontal;   // viewer over results
            _rightSplit.BackColor = Ui.PopupBack;
            _rightSplit.Panel1.BackColor = Ui.PopupBack;
            _rightSplit.Panel2.BackColor = Ui.PopupBack;
            // (min sizes set in Shown - see the note there)
            _rightSplit.Panel2Collapsed = true;
            _rightSplit.Panel1.Controls.Add(_viewer);
            _rightSplit.Panel2.Controls.Add(_results);          // added first: fills the rest
            _rightSplit.Panel2.Controls.Add(_resultsCaption);   // caption strip on top

            _toolbar.Dock = DockStyle.Top;
            _toolbar.FlowDirection = FlowDirection.LeftToRight;
            _toolbar.WrapContents = true;
            _toolbar.AutoSize = true;
            _toolbar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _toolbar.Padding = new Padding(10, 6, 10, 2);
            _toolbar.BackColor = Ui.PopupBack;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.BackColor = Ui.PopupBack;
            layout.Controls.Add(_toolbar, 0, 0);
            layout.Controls.Add(_split, 0, 1);
            layout.Controls.Add(_status, 0, 2);
            Controls.Add(layout);
        }

        // Re-derives every popup color from the live Ui palette (v1.2.3).
        // The constructor already reads Ui at build time - this exists for a
        // form kept open while the theme changes (and for verification).
        public void ApplyTheme()
        {
            BackColor = Ui.PopupBack;
            _toolbar.BackColor = Ui.PopupBack;
            _status.BackColor = Ui.PopupBack;
            _status.ForeColor = Ui.PopupTextDim;
            _resultsCaption.BackColor = Ui.PopupBack;
            _resultsCaption.ForeColor = Ui.PopupTextDim;
            _split.BackColor = Ui.PopupBack;
            _split.Panel1.BackColor = Ui.PopupBack;
            _split.Panel2.BackColor = Ui.PopupBack;
            _rightSplit.BackColor = Ui.PopupBack;
            _rightSplit.Panel1.BackColor = Ui.PopupBack;
            _rightSplit.Panel2.BackColor = Ui.PopupBack;
            foreach (Control c in _toolbar.Controls)
            {
                Button b = c as Button;
                if (b != null) Ui.StyleToolButton(b, b == _searchBtn);
            }
            foreach (ListView lv in new ListView[] { _files, _results })
            {
                lv.BackColor = Ui.PopupListBack;
                lv.ForeColor = Ui.PopupListText;
                lv.Invalidate();
            }
            _viewer.BackColor = Ui.PopupListBack;
            _viewer.ForeColor = Ui.PopupListText;
            _searchBox.BackColor = Ui.PopupListBack;
            _searchBox.ForeColor = Ui.PopupListText;
            Invalidate(true);
        }

        // ---- data -----------------------------------------------------------

        // Re-enumerates both apps' log folders and fills the file list,
        // newest first across the two apps merged. Selects the newest file.
        private void Repopulate()
        {
            _populating = true;
            _files.BeginUpdate();
            try
            {
                _files.Items.Clear();
                _entries.Clear();
                CollectFolder("GpuModeSwitch", "GPU Mode Switch");
                CollectFolder("GoTime", "Go Time");
                CollectFolder("EcoMode", "Eco Mode");
                _entries.Sort(delegate (LogFileEntry a, LogFileEntry b)
                {
                    return b.LastWrite.CompareTo(a.LastWrite);      // newest first
                });
                foreach (LogFileEntry e in _entries)
                {
                    ListViewItem item = new ListViewItem(e.App);
                    item.SubItems.Add(e.Name);
                    item.SubItems.Add(KbLabel(e.Length));
                    item.SubItems.Add(e.LastWrite.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    item.Tag = e;
                    _files.Items.Add(item);
                }
            }
            finally
            {
                _files.EndUpdate();
                _populating = false;
            }

            if (_files.Items.Count > 0)
            {
                _files.Items[0].Selected = true;    // opens the newest log in the viewer
                _files.Items[0].Focused = true;
            }
            else
            {
                _currentPath = "";
                _viewTruncated = false;
                _viewer.Text = "No log files found.\r\n\r\nFolder: " + Log.LogsRoot;
                _viewer.SelectAll();
                _status.Text = "No logs under " + Log.LogsRoot;
            }
        }

        // Adds one app's logs (LogsRoot + folder) to _entries. Folder names
        // are the two known ones next to Log.LogsRoot; the LOCALAPPDATA root
        // itself comes from Log.LogsRoot, never hardcoded here.
        private void CollectFolder(string folder, string appLabel)
        {
            try
            {
                DirectoryInfo dir = new DirectoryInfo(Path.Combine(Log.LogsRoot, folder));
                if (!dir.Exists) return;
                foreach (FileInfo f in dir.GetFiles("*.log"))
                {
                    LogFileEntry e = new LogFileEntry();
                    e.App = appLabel;
                    e.FullPath = f.FullName;
                    e.Name = f.Name;
                    e.Length = f.Length;
                    e.LastWrite = f.LastWriteTime;
                    _entries.Add(e);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("log browser: could not list " + folder + " logs - " + ex.Message);
            }
        }

        // Loads the selected file into the viewer. Files over ~2 MB load only
        // their tail, with a notice line on top. Opened with FileShare.ReadWrite
        // so the live current log can be read while the app appends to it.
        private void LoadFile(LogFileEntry entry)
        {
            _currentPath = entry.FullPath;
            string text;
            long size = 0;
            bool truncated = false;
            try
            {
                byte[] raw;
                using (FileStream fs = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    size = fs.Length;
                    long start = 0;
                    if (size > MaxLoadBytes)
                    {
                        start = size - MaxLoadBytes;
                        truncated = true;
                    }
                    fs.Seek(start, SeekOrigin.Begin);
                    raw = new byte[(int)(fs.Length - start)];
                    int read = 0;
                    while (read < raw.Length)
                    {
                        int n = fs.Read(raw, read, raw.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < raw.Length)
                    {
                        byte[] exact = new byte[read];
                        Array.Copy(raw, exact, read);
                        raw = exact;
                    }
                }
                if (truncated)
                {
                    // drop the partial first line left over from the seek
                    int nl = Array.IndexOf(raw, (byte)'\n');
                    if (nl >= 0 && nl + 1 < raw.Length)
                    {
                        byte[] tail = new byte[raw.Length - (nl + 1)];
                        Array.Copy(raw, nl + 1, tail, 0, tail.Length);
                        raw = tail;
                    }
                }
                text = Encoding.UTF8.GetString(raw);
                text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
                if (truncated)
                {
                    text = "[Log browser: this file is " + KbLabel(size) +
                        " - showing the last ~2 MB]\r\n" + text;
                }
                _viewer.Text = text;
                _viewer.SelectAll();        // pre-selected: Ctrl+C copies immediately
                _viewTruncated = truncated;
                _status.Text = entry.FullPath + "  (" + KbLabel(size) + (truncated ? ", tail shown" : "") + ")";
                Log.Info("log browser: opened " + entry.Name + " (" + KbLabel(size) +
                    (truncated ? ", last ~2 MB shown" : "") + ")");
            }
            catch (Exception ex)
            {
                _viewer.Text = "Could not read " + entry.FullPath + "\r\n\r\n" + ex.Message;
                _viewer.SelectAll();
                _viewTruncated = false;
                _status.Text = "Could not read " + entry.Name + " - " + ex.Message;
                Log.Error("log browser: could not open " + entry.FullPath, ex);
            }
        }

        // ---- actions --------------------------------------------------------

        // Case-insensitive find-next in the currently viewed file, wrapping.
        private void FindNextInView()
        {
            string q = _searchBox.Text.Trim();
            if (q.Length == 0)
            {
                _status.Text = "Type something to search for.";
                _searchBox.Focus();
                return;
            }
            string hay = _viewer.Text;
            if (hay.Length == 0)
            {
                _status.Text = "Nothing is loaded to search in.";
                return;
            }
            int start = _viewer.SelectionStart + _viewer.SelectionLength;
            if (start >= hay.Length) start = 0;
            int idx = hay.IndexOf(q, start, StringComparison.OrdinalIgnoreCase);
            bool wrapped = false;
            if (idx < 0 && start > 0)
            {
                wrapped = true;
                idx = hay.IndexOf(q, 0, StringComparison.OrdinalIgnoreCase);
            }
            string name = _currentPath.Length > 0 ? Path.GetFileName(_currentPath) : "(no file)";
            if (idx < 0)
            {
                _status.Text = "No matches for \"" + q + "\" in " + name + ".";
                Log.Info("log browser: search '" + q + "' in " + name + " - no matches");
                return;
            }
            _viewer.Select(idx, q.Length);
            _viewer.ScrollToCaret();
            int line = _viewer.GetLineFromCharIndex(idx) + 1;
            _status.Text = "Match for \"" + q + "\" in " + name + " at line " +
                line.ToString(CultureInfo.InvariantCulture) + (wrapped ? " (wrapped)" : "");
            Log.Info("log browser: search '" + q + "' in " + name + " - match at line " +
                line.ToString(CultureInfo.InvariantCulture) + (wrapped ? " (wrapped)" : ""));
        }

        // Searches every listed log file on a background thread and fills the
        // results pane (capped at MaxHits, with a truncation notice).
        private void RunSearchAll()
        {
            if (_searching) return;
            string q = _searchBox.Text.Trim();
            if (q.Length == 0)
            {
                _status.Text = "Type something to search for.";
                _searchBox.Focus();
                return;
            }
            List<LogFileEntry> snapshot = new List<LogFileEntry>(_entries);
            if (snapshot.Count == 0)
            {
                _status.Text = "No log files to search.";
                return;
            }
            _searching = true;
            _searchAllBtn.Enabled = false;
            UseWaitCursor = true;
            _status.Text = "Searching all logs for \"" + q + "\"...";
            RunBg(delegate
            {
                List<SearchHit> hits = new List<SearchHit>();
                bool truncated = false;
                foreach (LogFileEntry e in snapshot)
                {
                    if (hits.Count >= MaxHits)
                    {
                        truncated = true;
                        break;
                    }
                    try
                    {
                        using (FileStream fs = new FileStream(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (StreamReader reader = new StreamReader(fs, Encoding.UTF8, true, 65536))
                        {
                            int lineNo = 0;
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                lineNo++;
                                if (line.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    SearchHit hit = new SearchHit();
                                    hit.App = e.App;
                                    hit.File = e.Name;
                                    hit.Path = e.FullPath;
                                    hit.Line = lineNo;
                                    hit.Snippet = line.Trim();
                                    if (hit.Snippet.Length > MaxSnippetChars)
                                    {
                                        hit.Snippet = hit.Snippet.Substring(0, MaxSnippetChars) + " ...";
                                    }
                                    hits.Add(hit);
                                    if (hits.Count >= MaxHits)
                                    {
                                        truncated = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("log browser: search could not read " + e.Name + " - " + ex.Message);
                    }
                }
                SafeInvoke(delegate { ShowSearchResults(q, snapshot.Count, hits, truncated); });
            });
        }

        private void ShowSearchResults(string q, int fileCount, List<SearchHit> hits, bool truncated)
        {
            _searching = false;
            _searchAllBtn.Enabled = true;
            UseWaitCursor = false;

            _populating = true;
            _results.BeginUpdate();
            try
            {
                _results.Items.Clear();
                foreach (SearchHit h in hits)
                {
                    ListViewItem item = new ListViewItem(h.App);
                    item.SubItems.Add(h.File);
                    item.SubItems.Add(h.Line.ToString(CultureInfo.InvariantCulture));
                    item.SubItems.Add(h.Snippet);
                    item.Tag = h;
                    _results.Items.Add(item);
                }
            }
            finally
            {
                _results.EndUpdate();
                _populating = false;
            }

            if (_rightSplit.Panel2Collapsed)
            {
                _rightSplit.Panel2Collapsed = false;
            }
            TryGiveResultsSpace();
            _resultsCaption.Text = truncated
                ? "Search results - first " + hits.Count + " hits (truncated, refine the search)"
                : "Search results - " + hits.Count + " hits";
            string suffix = truncated ? " (truncated at " + MaxHits.ToString(CultureInfo.InvariantCulture) + ")" : "";
            _status.Text = "Search \"" + q + "\": " + hits.Count.ToString(CultureInfo.InvariantCulture) +
                " hits across " + fileCount.ToString(CultureInfo.InvariantCulture) + " log files" + suffix;
            Log.Info("log browser: search '" + q + "' across " +
                fileCount.ToString(CultureInfo.InvariantCulture) + " files, " +
                hits.Count.ToString(CultureInfo.InvariantCulture) + " hits" + suffix);
        }

        // Opens the hit's file in the viewer and selects its line.
        private void OpenHit(SearchHit hit)
        {
            LogFileEntry entry = FindEntry(hit.Path);
            if (entry == null)
            {
                entry = new LogFileEntry();
                entry.App = hit.App;
                entry.Name = hit.File;
                entry.FullPath = hit.Path;
                try
                {
                    FileInfo f = new FileInfo(hit.Path);
                    if (f.Exists)
                    {
                        entry.Length = f.Length;
                        entry.LastWrite = f.LastWriteTime;
                    }
                }
                catch { }
            }

            LoadFile(entry);    // logs the open

            int line = hit.Line - 1;
            if (_viewTruncated) line++;     // the tail notice shifts every file line down by one
            int idx = _viewer.GetFirstCharIndexFromLine(line);
            if (idx >= 0)
            {
                string[] lines = _viewer.Lines;
                int len = line < lines.Length ? lines[line].Length : 0;
                _viewer.Select(idx, len);
                _viewer.ScrollToCaret();
                _viewer.Focus();
                _status.Text = entry.Name + ", line " + hit.Line.ToString(CultureInfo.InvariantCulture) + " selected";
            }
        }

        private LogFileEntry FindEntry(string fullPath)
        {
            foreach (LogFileEntry e in _entries)
            {
                if (string.Equals(e.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }

        // Explorer with the selected log pre-selected (or the logs root).
        private void OpenFolder()
        {
            string target = _currentPath.Length > 0 ? _currentPath : Log.LogsRoot;
            try
            {
                Process.Start("explorer.exe", "/select,\"" + target + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open Explorer: " + ex.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Copies the currently displayed viewer text (it is pre-selected on
        // load, so Ctrl+C works without the button too).
        private void CopyView()
        {
            if (_viewer.TextLength == 0)
            {
                _status.Text = "Nothing to copy.";
                return;
            }
            try
            {
                Clipboard.SetText(_viewer.Text);
                _copyBtn.Text = "Copied!";
                _copyFlash.Stop();
                _copyFlash.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Clipboard copy failed: " + ex.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Gives the results pane a sensible share of the right side.
        private void TryGiveResultsSpace()
        {
            try
            {
                if (_rightSplit.Height > 340)
                {
                    _rightSplit.SplitterDistance = _rightSplit.Height - 180;
                }
            }
            catch
            {
                // splitter range constraints - keep whatever distance is valid
            }
        }

        // ---- handlers / plumbing -------------------------------------------

        private void OnFileSelected(object sender, EventArgs e)
        {
            if (_populating) return;
            if (_files.SelectedItems.Count != 1) return;
            LogFileEntry entry = _files.SelectedItems[0].Tag as LogFileEntry;
            if (entry != null) LoadFile(entry);
        }

        private void OnResultSelected(object sender, EventArgs e)
        {
            if (_populating) return;
            if (_results.SelectedItems.Count != 1) return;
            SearchHit hit = _results.SelectedItems[0].Tag as SearchHit;
            if (hit != null) OpenHit(hit);
        }

        private static string KbLabel(long bytes)
        {
            return (bytes / 1024.0).ToString("N1", CultureInfo.InvariantCulture);
        }

        private void RunBg(ThreadStart work)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { work(); }
                catch (Exception ex)
                {
                    Log.Error("log browser: background error", ex);
                    SafeInvoke(delegate { SearchFailed(ex); });
                }
            });
        }

        private void SafeInvoke(MethodInvoker mi)
        {
            try { Invoke(mi); }
            catch { }   // form gone while a background step finished - nothing to do
        }

        private void SearchFailed(Exception ex)
        {
            _searching = false;
            _searchAllBtn.Enabled = true;
            UseWaitCursor = false;
            _status.Text = "Search failed - " + ex.Message;
        }

        // One row of the file list.
        private class LogFileEntry
        {
            public string App;          // "Go Time" / "Eco Mode"
            public string FullPath;
            public string Name;
            public long Length;
            public DateTime LastWrite;
        }

        // One row of the search-all results list.
        private class SearchHit
        {
            public string App;
            public string File;
            public string Path;
            public int Line;
            public string Snippet;
        }
    }
}
