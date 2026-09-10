//  SessionHistory.cs  (v1.1.0 - Wave 4)
//  --------------------
//  Structured per-run session records (A15): every completed run can append
//  one JSON line to %LOCALAPPDATA%\GpuModeSwitch\sessions.jsonl (JSONL - one
//  self-contained object per line, serialized with the already referenced
//  System.Web.Extensions JavaScriptSerializer), so past sessions stay
//  reviewable long after their per-run log files have rotated away.
//
//    SessionRecord       plain data holder: UTC timestamp, app, mode,
//                        actions applied, space freed per category, duration,
//                        error count, result. The parameterless constructor
//                        is required by the serializer round-trip.
//    SessionHistory      static store: Append (one line, never throws),
//                        ReadAll (tolerant parse, newest first), ExportText
//                        (human-readable rendering for the export file and
//                        the clipboard) and a shared FormatBytes helper.
//    SessionHistoryForm  dark-themed non-modal viewer ("Session History"):
//                        record list (When local / App / Mode / Result /
//                        Freed / Errors, newest first) with Refresh, a
//                        read-only detail pane showing the selected record's
//                        ExportText (recomputed on selection, pre-selected so
//                        Ctrl+C works immediately, like LogForm), plus
//                        "Export .txt" and "Copy" buttons.
//
//  Everything here is best-effort: storage failures are logged and swallowed
//  - session history must never crash or slow the app. Recordings log through
//  Log.Chan("SESSION", ...); the viewer logs its actions through Log.Info.
//
//  Open with SessionHistoryForm.ShowHistory(owner): non-modal, owned by the
//  caller form so it never orphans (plain Show if the owner is gone).
//  Reachable from Wave 6 (A18 wires the entry); compiles into both targets.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // One recorded session run. Plain data holder for the JSONL store and
    // the viewer; public fields serialize directly with the
    // JavaScriptSerializer and the parameterless constructor keeps the
    // deserialization round-trip working.
    // ---------------------------------------------------------------------
    public class SessionRecord
    {
        public DateTime UtcTimestamp;                    // when the run finished (UTC)
        public string App;                               // "Go Time" / "Eco Mode"
        public string Mode;                              // e.g. "Standard" / "Eco"
        public List<string> ActionsApplied;              // what was actually applied
        public Dictionary<string, long> SpaceFreedByCategory;   // category -> bytes
        public double DurationSec;                       // total run duration
        public int ErrorCount;                           // non-fatal errors this run
        public string Result;                            // short end-of-run result

        public SessionRecord()
        {
            UtcTimestamp = DateTime.MinValue;
            App = "";
            Mode = "";
            ActionsApplied = new List<string>();
            SpaceFreedByCategory = new Dictionary<string, long>();
            DurationSec = 0.0;
            ErrorCount = 0;
            Result = "";
        }

        // Convenience constructor for code that records a session in memory.
        public SessionRecord(string app, string mode, string result)
            : this()
        {
            UtcTimestamp = DateTime.UtcNow;
            App = app == null ? "" : app;
            Mode = mode == null ? "" : mode;
            Result = result == null ? "" : result;
        }
    }

    // ---------------------------------------------------------------------
    // Session history persistence: an append-only JSONL file (one JSON
    // object per line) at %LOCALAPPDATA%\GpuModeSwitch\sessions.jsonl.
    // Append never rewrites the file (a run only ever adds its line at the
    // end); ReadAll is deliberately tolerant - a corrupt line is skipped
    // with one WARN each (warnings capped at 3) so one bad line can never
    // hide the rest of the history. All operations are best-effort: any
    // failure is logged and nothing here ever throws.
    // ---------------------------------------------------------------------
    public static class SessionHistory
    {
        private const string RootFolderName = "GpuModeSwitch";
        private const string FileName = "sessions.jsonl";
        private const int MaxCorruptWarnings = 3;   // corrupt-line WARN cap

        // Full path of the session history store:
        // %LOCALAPPDATA%\GpuModeSwitch\sessions.jsonl
        public static string FilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Path.Combine(RootFolderName, FileName));
            }
        }

        // Appends one record as a single JSON line. If the record carries no
        // timestamp, "now" (UTC) is used. Never throws: failures are logged
        // through Log.Error and the caller keeps working either way. On
        // success the recording is logged through the SESSION channel.
        public static void Append(SessionRecord r)
        {
            if (r == null) return;
            if (r.UtcTimestamp == DateTime.MinValue)
            {
                r.UtcTimestamp = DateTime.UtcNow;
            }

            int actions = r.ActionsApplied == null ? 0 : r.ActionsApplied.Count;
            long total = TotalFreed(r);
            string spaceSummary = total > 0 ? FormatBytes(total) + " freed" : "no space freed";

            try
            {
                string path = FilePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(r);
                File.AppendAllText(path, json + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Log.Error("session history: could not write " + FileName, ex);
                return;
            }

            Log.Chan("SESSION", "session recorded: " + SafeStr(r.App) + " " + SafeStr(r.Mode) +
                " - " + SafeStr(r.Result) + " (" + actions + " action(s), " + spaceSummary + ")");
        }

        // Reads and parses every line of the store, returning the records
        // sorted by timestamp DESCENDING (newest first). Tolerant: corrupt
        // lines are skipped with one WARN each (warnings capped at 3), a
        // missing file simply yields an empty list.
        public static List<SessionRecord> ReadAll()
        {
            List<SessionRecord> result = new List<SessionRecord>();
            string path = FilePath;

            string[] lines;
            try
            {
                if (!File.Exists(path))
                {
                    return result;              // no history yet - not an error
                }
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                Log.Error("session history: could not read " + path, ex);
                return result;
            }

            JavaScriptSerializer ser = new JavaScriptSerializer();
            int warned = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i] == null ? "" : lines[i].Trim();
                if (trimmed.Length == 0) continue;      // blank line - ignore

                SessionRecord rec = null;
                try
                {
                    rec = ser.Deserialize<SessionRecord>(trimmed);
                }
                catch (Exception)
                {
                    rec = null;                          // fall through to the corrupt branch
                }

                if (rec == null)
                {
                    if (warned < MaxCorruptWarnings)
                    {
                        warned++;
                        Log.Warn("session history: corrupt line " + (i + 1) + " in " +
                            FileName + " skipped (invalid JSON)");
                    }
                    continue;
                }

                // Normalize what the serializer may have left null or in a
                // non-UTC kind, so sorting and display stay consistent.
                if (rec.App == null) rec.App = "";
                if (rec.Mode == null) rec.Mode = "";
                if (rec.Result == null) rec.Result = "";
                if (rec.ActionsApplied == null) rec.ActionsApplied = new List<string>();
                if (rec.SpaceFreedByCategory == null) rec.SpaceFreedByCategory = new Dictionary<string, long>();
                if (rec.UtcTimestamp != DateTime.MinValue && rec.UtcTimestamp.Kind != DateTimeKind.Utc)
                {
                    rec.UtcTimestamp = rec.UtcTimestamp.ToUniversalTime();
                }
                result.Add(rec);
            }

            result.Sort(delegate (SessionRecord a, SessionRecord b)
            {
                return b.UtcTimestamp.CompareTo(a.UtcTimestamp);    // newest first
            });
            return result;
        }

        // Renders the given records as a human-readable multi-line text (one
        // block per record: local timestamp, app, mode, result, duration,
        // bulleted actions, space freed per category + total) - the source
        // for the viewer's detail pane, the "Export .txt" file and "Copy".
        public static string ExportText(List<SessionRecord> records)
        {
            StringBuilder sb = new StringBuilder();
            if (records == null || records.Count == 0)
            {
                sb.AppendLine("(no session history)");
                return sb.ToString();
            }

            string rule = "--------------------------------------------------------------------";
            foreach (SessionRecord r in records)
            {
                if (r == null) continue;

                sb.AppendLine(rule);
                sb.AppendLine(r.UtcTimestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture) + " (local)  |  " + SafeStr(r.App) +
                    "  |  Mode: " + SafeStr(r.Mode));
                sb.AppendLine("Result   : " + SafeStr(r.Result));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "Duration : {0:0.0} s", r.DurationSec));
                sb.AppendLine("Errors   : " + r.ErrorCount.ToString(CultureInfo.InvariantCulture));

                sb.AppendLine("Actions applied:");
                if (r.ActionsApplied == null || r.ActionsApplied.Count == 0)
                {
                    sb.AppendLine("  (none)");
                }
                else
                {
                    foreach (string action in r.ActionsApplied)
                    {
                        sb.AppendLine("  - " + SafeStr(action));
                    }
                }

                sb.AppendLine("Space freed:");
                if (r.SpaceFreedByCategory == null || r.SpaceFreedByCategory.Count == 0)
                {
                    sb.AppendLine("  (none)");
                }
                else
                {
                    foreach (KeyValuePair<string, long> kv in r.SpaceFreedByCategory)
                    {
                        sb.AppendLine("  - " + SafeStr(kv.Key) + ": " + FormatBytes(kv.Value));
                    }
                    sb.AppendLine("  Total: " + FormatBytes(TotalFreed(r)));
                }
                sb.AppendLine(rule);
            }
            return sb.ToString();
        }

        // Shared byte-count formatter (B / KB / MB / GB), e.g. "124.5 MB".
        public static string FormatBytes(long bytes)
        {
            double value = bytes;
            string unit = "B";
            if (value >= 1024d) { value /= 1024d; unit = "KB"; }
            if (value >= 1024d) { value /= 1024d; unit = "MB"; }
            if (value >= 1024d) { value /= 1024d; unit = "GB"; }
            if (unit == "B")
            {
                return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            }
            return value.ToString("0.#", CultureInfo.InvariantCulture) + " " + unit;
        }

        // Sum of the per-category bytes freed in one record.
        private static long TotalFreed(SessionRecord r)
        {
            if (r == null || r.SpaceFreedByCategory == null) return 0;
            long total = 0;
            foreach (KeyValuePair<string, long> kv in r.SpaceFreedByCategory)
            {
                total += kv.Value;
            }
            return total;
        }

        private static string SafeStr(string s)
        {
            return s == null ? "" : s;
        }
    }

    // ---------------------------------------------------------------------
    // Session history viewer (v1.1.0, Wave 4): dark-themed non-modal window
    // listing every recorded session (newest first) with a read-only detail
    // pane for the selected record and "Export .txt" / "Copy" actions.
    // Same recipe as LogForm / LogBrowserForm: flat dark controls, a
    // TableLayoutPanel shell (deterministic at any DPI) over a horizontal
    // SplitContainer, pre-selected detail text so Ctrl+C works immediately.
    // ---------------------------------------------------------------------
    public class SessionHistoryForm : Form
    {
        private readonly ListView _list = new ListView();
        private readonly TextBox _detail = new TextBox();
        private readonly Button _refreshBtn = new Button();
        private readonly Button _exportBtn = new Button();
        private readonly Button _copyBtn = new Button();
        private readonly Label _status = new Label();
        private readonly FlowLayoutPanel _toolbar = new FlowLayoutPanel();
        private readonly SplitContainer _split = new SplitContainer();
        private readonly List<SessionRecord> _records = new List<SessionRecord>();
        private bool _populating;       // suppress the selection handler while filling

        public SessionHistoryForm()
        {
            Text = "Session History";
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(880, 580);
            MinimumSize = new Size(720, 480);
            BackColor = Ui.PopupBack;
            Font = new Font("Segoe UI", 9f);
            WindowIcons.Apply(this);
            DarkChrome.Apply(this);     // native title bar follows the dark body

            // v1.3.0: the form is constructed fresh on every open, so colors
            // read here follow the live theme; the public ApplyTheme covers a
            // form kept open across a theme change.
            BuildToolbar();
            BuildList();
            BuildDetail();
            BuildStatus();
            BuildLayout();

            // Splitter distance is only sane once the form has real bounds;
            // the detail text is already pre-selected, so hand it focus.
            Shown += delegate
            {
                try { _split.SplitterDistance = (int)(_split.Height * 0.52); } catch { }
                _detail.Focus();
            };

            Repopulate();
            Log.Info("session history: opened (" + _records.Count + " session(s))");
        }

        // Opens the history as a non-modal window owned by "owner" (falls
        // back to an unowned window if the owner is already gone).
        public static void ShowHistory(Form owner)
        {
            SessionHistoryForm form = new SessionHistoryForm();
            if (owner != null && !owner.IsDisposed)
            {
                form.Show(owner);
            }
            else
            {
                form.Show();
            }
        }

        // ---- UI construction ------------------------------------------------

        private void BuildToolbar()
        {
            InitToolButton(_refreshBtn, "Refresh");
            InitToolButton(_exportBtn, "Export .txt");
            InitToolButton(_copyBtn, "Copy");

            _refreshBtn.Click += delegate { OnRefresh(); };
            _exportBtn.Click += delegate { OnExport(); };
            _copyBtn.Click += delegate { OnCopy(); };

            _toolbar.Controls.Add(_refreshBtn);
            _toolbar.Controls.Add(_exportBtn);
            _toolbar.Controls.Add(_copyBtn);
        }

        // Flat dark buttons, the one shared recipe (Ui.StyleToolButton).
        private static void InitToolButton(Button b, string text)
        {
            b.Text = text;
            b.AutoSize = true;
            Ui.StyleToolButton(b, false);
            b.Margin = new Padding(2, 6, 2, 6);
            b.TabStop = false;
        }

        private void BuildList()
        {
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.BackColor = Ui.PopupListBack;
            _list.ForeColor = Ui.PopupListText;
            _list.BorderStyle = BorderStyle.FixedSingle;
            _list.Dock = DockStyle.Fill;
            _list.Sorting = SortOrder.None;     // order comes from _records (newest first)
            _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _list.OwnerDraw = true;             // dark headers + dark rows (Ui helpers)
            _list.DrawColumnHeader += Ui.DrawListHeader;
            _list.DrawItem += Ui.DrawListItem;
            _list.DrawSubItem += Ui.DrawListCell;
            _list.Columns.Add("When (local)", 140);
            _list.Columns.Add("App", 90);
            _list.Columns.Add("Mode", 90);
            _list.Columns.Add("Result", 230);
            _list.Columns.Add("Freed", 95, HorizontalAlignment.Right);
            _list.Columns.Add("Errors", 62, HorizontalAlignment.Right);
            _list.SelectedIndexChanged += OnSelectedChanged;
        }

        private void BuildDetail()
        {
            _detail.Multiline = true;
            _detail.ReadOnly = true;
            _detail.ScrollBars = ScrollBars.Vertical;
            _detail.WordWrap = false;
            _detail.BackColor = Ui.PopupListBack;
            _detail.ForeColor = Ui.PopupListText;
            _detail.BorderStyle = BorderStyle.FixedSingle;
            _detail.Font = new Font("Consolas", 9f);
            _detail.Dock = DockStyle.Fill;
            _detail.HideSelection = false;      // keep the selection visible without focus
        }

        private void BuildStatus()
        {
            _status.Text = "No history loaded yet.";
            _status.ForeColor = Ui.PopupTextDim;
            _status.BackColor = Ui.PopupBack;
            _status.AutoSize = false;
            _status.Height = 26;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Padding = new Padding(12, 4, 12, 0);
            _status.AutoEllipsis = true;
        }

        // TableLayoutPanel shell (like LogForm/LogBrowser): toolbar / split /
        // status - deterministic at any DPI and window size.
        private void BuildLayout()
        {
            _split.Dock = DockStyle.Fill;
            _split.Orientation = Orientation.Horizontal;    // list over detail pane
            _split.BackColor = Ui.PopupBack;
            _split.Panel1.BackColor = Ui.PopupBack;
            _split.Panel2.BackColor = Ui.PopupBack;
            _split.Panel1MinSize = 140;
            _split.Panel2MinSize = 120;
            _split.Panel1.Controls.Add(_list);
            _split.Panel2.Controls.Add(_detail);

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

        // Re-derives every popup color from the live Ui palette (v1.3.0).
        // The constructor already reads Ui at build time - this exists for a
        // form kept open while the theme changes (and for verification).
        public void ApplyTheme()
        {
            BackColor = Ui.PopupBack;
            _toolbar.BackColor = Ui.PopupBack;
            _status.BackColor = Ui.PopupBack;
            _status.ForeColor = Ui.PopupTextDim;
            _split.BackColor = Ui.PopupBack;
            _split.Panel1.BackColor = Ui.PopupBack;
            _split.Panel2.BackColor = Ui.PopupBack;
            _list.BackColor = Ui.PopupListBack;
            _list.ForeColor = Ui.PopupListText;
            _detail.BackColor = Ui.PopupListBack;
            _detail.ForeColor = Ui.PopupListText;
            foreach (Control c in _toolbar.Controls)
            {
                Button b = c as Button;
                if (b != null) Ui.StyleToolButton(b, false);
            }
            _list.Invalidate();
            Invalidate(true);
        }

        // ---- data -----------------------------------------------------------

        // Re-reads the store and fills the list, newest first. Selects the
        // newest record (its ExportText lands pre-selected in the detail
        // pane, so Ctrl+C works immediately, like LogForm).
        private void Repopulate()
        {
            _populating = true;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                _records.Clear();
                _records.AddRange(SessionHistory.ReadAll());

                foreach (SessionRecord r in _records)
                {
                    ListViewItem item = new ListViewItem(WhenLabel(r));
                    item.SubItems.Add(SafeStr(r.App));
                    item.SubItems.Add(SafeStr(r.Mode));
                    item.SubItems.Add(SafeStr(r.Result));
                    item.SubItems.Add(SessionHistory.FormatBytes(SpaceFreedOf(r)));
                    item.SubItems.Add(r.ErrorCount.ToString(CultureInfo.InvariantCulture));
                    item.Tag = r;
                    _list.Items.Add(item);
                }
            }
            finally
            {
                _list.EndUpdate();
                _populating = false;
            }

            if (_list.Items.Count > 0)
            {
                _list.Items[0].Selected = true;     // newest first
                _list.Items[0].Focused = true;
            }
            else
            {
                ShowDetail(null);
            }

            _status.Text = _records.Count == 0
                ? "No sessions recorded yet - the history fills as sessions complete."
                : _records.Count + " session(s) on file - newest first";
        }

        private void OnRefresh()
        {
            Repopulate();
            Log.Info("session history: refreshed (" + _records.Count + " session(s))");
        }

        // Local "When" cell: local time of the run, or a placeholder.
        private static string WhenLabel(SessionRecord r)
        {
            if (r == null || r.UtcTimestamp == DateTime.MinValue) return "(unknown)";
            return r.UtcTimestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture);
        }

        private static string SafeStr(string s)
        {
            return s == null ? "" : s;
        }

        private static long SpaceFreedOf(SessionRecord r)
        {
            if (r == null || r.SpaceFreedByCategory == null) return 0;
            long total = 0;
            foreach (KeyValuePair<string, long> kv in r.SpaceFreedByCategory)
            {
                total += kv.Value;
            }
            return total;
        }

        // ---- detail pane ----------------------------------------------------

        // Recomputes the detail pane for the selected record (ExportText).
        private void OnSelectedChanged(object sender, EventArgs e)
        {
            if (_populating) return;
            SessionRecord r = _list.SelectedItems.Count > 0
                ? _list.SelectedItems[0].Tag as SessionRecord
                : null;
            ShowDetail(r);
        }

        private void ShowDetail(SessionRecord r)
        {
            if (r == null)
            {
                _detail.Text = _records.Count == 0
                    ? "(no session history yet)"
                    : "(no session selected)";
            }
            else
            {
                List<SessionRecord> one = new List<SessionRecord>();
                one.Add(r);
                _detail.Text = SessionHistory.ExportText(one);
            }
            _detail.SelectAll();        // pre-selected: Ctrl+C copies immediately
        }

        // ---- export / copy --------------------------------------------------

        // The text behind "Export .txt" and "Copy": the selected record, or
        // the whole history when nothing is selected.
        private string TextForExport()
        {
            if (_list.SelectedItems.Count > 0)
            {
                SessionRecord r = _list.SelectedItems[0].Tag as SessionRecord;
                if (r != null)
                {
                    List<SessionRecord> one = new List<SessionRecord>();
                    one.Add(r);
                    return SessionHistory.ExportText(one);
                }
            }
            return SessionHistory.ExportText(_records);
        }

        private void OnExport()
        {
            string text = TextForExport();
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "Export session history";
                dlg.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
                dlg.DefaultExt = "txt";
                dlg.AddExtension = true;
                dlg.FileName = "session-history_" +
                    DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture) + ".txt";

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    File.WriteAllText(dlg.FileName, text);
                }
                catch (Exception ex)
                {
                    Log.Error("session history: export failed", ex);
                    MessageBox.Show(this, "Could not write the file:" + Environment.NewLine + ex.Message,
                        "Session History", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                Log.Info("session history: exported to " + dlg.FileName);
                _status.Text = "Exported to " + dlg.FileName;
            }
        }

        private void OnCopy()
        {
            string text = TextForExport();
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                Log.Error("session history: copy failed", ex);
                MessageBox.Show(this, "Clipboard copy failed:" + Environment.NewLine + ex.Message,
                    "Session History", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Log.Info("session history: copied");
            _status.Text = "Copied to the clipboard.";
        }
    }
}
