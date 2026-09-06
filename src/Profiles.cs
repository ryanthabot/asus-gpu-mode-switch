//  Profiles.cs  (v1.1.0 - Wave 4, agent A14)
//  -----------------------------------------
//  Named checkbox presets ("profiles") shared by the Wave 6 UI.
//
//  Profile        one preset: a display name plus the checkbox state it
//                 captures (Dictionary<string,bool>; key = checkbox id as
//                 the host defines them, value = checked).
//  ProfileStore   JSON persistence at %LOCALAPPDATA%\GpuModeSwitch\
//                 profiles.json - a JSON array of
//                 {"Name":...,"Selections":{...}} objects handled with the
//                 already-referenced System.Web.Extensions
//                 JavaScriptSerializer. LoadAll keeps file order (oldest
//                 first); Save upserts by exact name (replace in place,
//                 else append); every write is a full rewrite; every
//                 failure is logged and never fatal - a missing or corrupt
//                 file yields an empty list plus a single WARN.
//  ProfileBar     dark-themed horizontal bar UserControl (Label + ComboBox
//                 + Apply / Save... / Delete / Refresh) that the Wave 6
//                 host wires to its checkbox groups through three events:
//                 ApplyRequested and Saved
//                 (Action<string, Dictionary<string, bool>>) and
//                 CollectSelections (Func<Dictionary<string, bool>>,
//                 raised by Save to grab the CURRENT checkbox state).
//                 Apply always reads the selected profile fresh from the
//                 store (never a cached copy); Delete asks Yes/No first;
//                 Save names the preset through the ProfileNameDialog mini
//                 modal defined at the bottom of this file.
//
//  Every action is logged through Log (channel PROFILE for profile
//  actions). Colors stay inline at the call sites, exactly as in Forms.cs.
//  Standalone: no Forms.cs types are referenced.
//
//  New file for v1.1.0 (Wave 4, A14) per docs\HANDBOOK.md section 8.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // One named preset. Plain data holder for the JSON store and the Wave 6
    // UI; the parameterless constructor is required by the JavaScriptSerializer
    // round-trip, the second constructor is a convenience for code that
    // builds profiles in memory.
    // ---------------------------------------------------------------------
    public class Profile
    {
        public string Name;
        public Dictionary<string, bool> Selections;

        public Profile()
        {
            Name = "";
            Selections = new Dictionary<string, bool>();
        }

        public Profile(string name, Dictionary<string, bool> selections)
        {
            Name = name == null ? "" : name;
            Selections = selections == null ? new Dictionary<string, bool>() : selections;
        }
    }

    // ---------------------------------------------------------------------
    // Profile persistence: a single JSON file holding every saved profile.
    // All operations are best-effort and log their outcome; nothing here
    // ever throws. The file keeps insertion order, so LoadAll returns
    // profiles oldest first (the UI shows them in that order).
    // ---------------------------------------------------------------------
    public static class ProfileStore
    {
        private const string RootFolderName = "GpuModeSwitch";
        private const string FileName = "profiles.json";

        // Full path of the profile store:
        // %LOCALAPPDATA%\GpuModeSwitch\profiles.json
        public static string FilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Path.Combine(RootFolderName, FileName));
            }
        }

        // Loads every saved profile, in file order. A missing or corrupt
        // file yields an empty list plus a single WARN - never an exception.
        public static List<Profile> LoadAll()
        {
            List<Profile> result = new List<Profile>();
            string path = FilePath;

            string json;
            try
            {
                if (!File.Exists(path))
                {
                    Log.Warn("profiles: no profiles saved yet (" + path + ")");
                    return result;
                }
                json = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                Log.Error("profiles: could not read " + path, ex);
                return result;
            }

            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                List<Profile> loaded = ser.Deserialize<List<Profile>>(json);
                if (loaded != null)
                {
                    foreach (Profile p in loaded)
                    {
                        if (p == null) continue;
                        if (p.Name == null) p.Name = "";
                        if (p.Selections == null) p.Selections = new Dictionary<string, bool>();
                        result.Add(p);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("profiles: " + FileName + " is unreadable (corrupt?) - starting empty - " + ex.Message);
                result.Clear();
            }
            return result;
        }

        // Full rewrite of the store. Best-effort: any failure is logged and
        // reported as false; the caller keeps working either way.
        public static bool SaveAll(List<Profile> profiles)
        {
            string path = FilePath;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(profiles == null ? new List<Profile>() : profiles);
                File.WriteAllText(path, json);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("profiles: could not write " + path, ex);
                return false;
            }
        }

        // Upsert by exact name: replaces the existing entry in place (file
        // order preserved), else appends at the end.
        public static bool Save(string name, Dictionary<string, bool> selections)
        {
            string key = name == null ? "" : name;
            List<Profile> all = LoadAll();

            Profile entry = new Profile(key, selections);
            bool replaced = false;
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].Name, key, StringComparison.Ordinal))
                {
                    all[i] = entry;
                    replaced = true;
                    break;
                }
            }
            if (!replaced) all.Add(entry);

            bool ok = SaveAll(all);
            if (ok)
            {
                int keys = selections == null ? 0 : selections.Count;
                Log.Chan("PROFILE", "profile saved: " + key + " (" + keys + " keys)");
            }
            return ok;
        }

        // Removes the named profile if present and rewrites the store.
        public static bool Delete(string name)
        {
            string key = name == null ? "" : name;
            List<Profile> all = LoadAll();

            int idx = -1;
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].Name, key, StringComparison.Ordinal))
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0)
            {
                Log.Chan("PROFILE", "profile delete skipped: '" + key + "' not found");
                return true;
            }

            all.RemoveAt(idx);
            bool ok = SaveAll(all);
            if (ok) Log.Chan("PROFILE", "profile deleted: " + key);
            return ok;
        }

        // Exact-name lookup, always read fresh from disk (never cached).
        public static Profile Find(string name)
        {
            string key = name == null ? "" : name;
            foreach (Profile p in LoadAll())
            {
                if (string.Equals(p.Name, key, StringComparison.Ordinal)) return p;
            }
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Profile bar: a horizontal dark-themed strip the Wave 6 host drops
    // into its window. The host wires the checkbox groups to this bar:
    //   - CollectSelections  the bar raises it when Save is clicked and
    //                        expects the CURRENT checkbox state back;
    //   - ApplyRequested     the bar fires it with the selected profile's
    //                        selections (read fresh from the store);
    //   - Saved              fired after a successful save.
    // Recommended placement: dock or position at least 560 x 44 px (the
    // default size); the strip is one horizontal row, as in LogForm.
    // ---------------------------------------------------------------------
    public class ProfileBar : UserControl
    {
        private readonly ComboBox _combo;
        private readonly Button _applyBtn;
        private readonly Button _saveBtn;
        private readonly Button _deleteBtn;
        private readonly Button _refreshBtn;

        // Fired when Apply is clicked: profile name + its selections, read
        // fresh from the store. The host applies them to its checkbox groups.
        public event Action<string, Dictionary<string, bool>> ApplyRequested;

        // Fired after a successful save: the stored name and the selections
        // that were saved (the same dictionary CollectSelections returned).
        public event Action<string, Dictionary<string, bool>> Saved;

        // The host supplies the CURRENT checkbox state here when Save is
        // clicked. Left unattached (or returning null) aborts the save.
        public event Func<Dictionary<string, bool>> CollectSelections;

        public ProfileBar()
        {
            _combo = MakeCombo();

            _applyBtn = MakeToolButton("Apply");
            _applyBtn.Click += delegate { OnApplyClicked(); };

            _saveBtn = MakeToolButton("Save...");
            _saveBtn.Click += delegate { OnSaveClicked(); };

            _deleteBtn = MakeToolButton("Delete");
            _deleteBtn.Click += delegate { OnDeleteClicked(); };

            _refreshBtn = MakeToolButton("Refresh");
            _refreshBtn.Click += delegate { Reload(); };

            BackColor = Color.FromArgb(24, 24, 28);
            Font = new Font("Segoe UI", 9f);
            Size = new Size(560, 44);
            TabStop = false;

            // One FlowLayoutPanel strip - the same proven pattern as the
            // LogForm bars (deterministic at any DPI, buttons never vanish).
            FlowLayoutPanel bar = MakeBar();
            bar.Dock = DockStyle.Fill;
            bar.Controls.Add(MakeFieldLabel("Profile:"));
            bar.Controls.Add(_combo);
            bar.Controls.Add(_applyBtn);
            bar.Controls.Add(_saveBtn);
            bar.Controls.Add(_deleteBtn);
            bar.Controls.Add(_refreshBtn);
            Controls.Add(bar);

            Reload();
        }

        // Re-lists the saved profiles (fresh from the store). The previous
        // selection is kept when still present, else the first profile is
        // selected (none if the store is empty).
        public void Reload()
        {
            string keep = SelectedName();
            List<Profile> all = ProfileStore.LoadAll();

            _combo.Items.Clear();
            foreach (Profile p in all)
            {
                _combo.Items.Add(p.Name);
            }

            int select = -1;
            for (int i = 0; i < _combo.Items.Count; i++)
            {
                if (string.Equals((string)_combo.Items[i], keep, StringComparison.Ordinal))
                {
                    select = i;
                    break;
                }
            }
            if (select < 0 && _combo.Items.Count > 0) select = 0;
            if (select >= 0) _combo.SelectedIndex = select;

            Log.Chan("PROFILE", "profile bar: reloaded (" + _combo.Items.Count + " profile(s))");
        }

        // ---- actions -------------------------------------------------------

        // Apply: read the selected profile FRESH from the store (never a
        // cached copy) and hand it to the host.
        private void OnApplyClicked()
        {
            string name = SelectedName();
            if (name.Length == 0)
            {
                Log.Warn("profiles: apply clicked with no profile selected");
                return;
            }

            Profile p = ProfileStore.Find(name);
            if (p == null)
            {
                Log.Warn("profiles: apply failed - '" + name + "' is no longer in the store");
                return;
            }

            Log.Chan("PROFILE", "profile apply requested: " + p.Name + " (" + p.Selections.Count + " keys)");
            Action<string, Dictionary<string, bool>> handler = ApplyRequested;
            if (handler != null) handler(p.Name, p.Selections);
        }

        // Save: grab the CURRENT selections from the host, ask for a name,
        // store the profile, refresh and notify.
        private void OnSaveClicked()
        {
            Func<Dictionary<string, bool>> collector = CollectSelections;
            if (collector == null)
            {
                Log.Warn("profiles: save clicked but no selection collector is attached");
                return;
            }

            Dictionary<string, bool> selections;
            try
            {
                selections = collector();
            }
            catch (Exception ex)
            {
                Log.Error("profiles: selection collector failed", ex);
                return;
            }
            if (selections == null)
            {
                Log.Warn("profiles: save aborted - collector returned no selections");
                return;
            }

            string initial = SelectedName();
            if (initial.Length == 0) initial = "New profile";

            string name = PromptForName(initial);
            if (name == null)
            {
                Log.Chan("PROFILE", "profile save canceled");
                return;
            }

            if (ProfileStore.Save(name, selections))
            {
                Reload();
                SelectName(name);
                Action<string, Dictionary<string, bool>> handler = Saved;
                if (handler != null) handler(name, selections);
            }
        }

        // Delete: confirm, remove, refresh.
        private void OnDeleteClicked()
        {
            string name = SelectedName();
            if (name.Length == 0)
            {
                Log.Warn("profiles: delete clicked with no profile selected");
                return;
            }

            DialogResult dr = MessageBox.Show(
                "Delete the profile \"" + name + "\"?",
                "Delete profile", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (dr != DialogResult.Yes)
            {
                Log.Chan("PROFILE", "profile delete canceled: " + name);
                return;
            }

            if (ProfileStore.Delete(name))
            {
                Reload();
            }
        }

        // ---- helpers -------------------------------------------------------

        // Currently selected profile name ("" when nothing is selected).
        private string SelectedName()
        {
            object sel = _combo.SelectedItem;
            return sel == null ? "" : sel.ToString();
        }

        // Selects the given profile name when present (no-op otherwise).
        private void SelectName(string name)
        {
            for (int i = 0; i < _combo.Items.Count; i++)
            {
                if (string.Equals((string)_combo.Items[i], name, StringComparison.Ordinal))
                {
                    _combo.SelectedIndex = i;
                    return;
                }
            }
        }

        // Asks for a profile name (dark-themed modal). Returns the trimmed
        // name, or null when canceled. "initial" pre-fills the box.
        private string PromptForName(string initial)
        {
            using (ProfileNameDialog dlg = new ProfileNameDialog())
            {
                dlg.ProfileName = initial;
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return null;
                return dlg.ProfileName;
            }
        }

        // ---- dark-theme control factories (Forms.cs style) ------------------

        private static ComboBox MakeCombo()
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Width = 190;
            c.FlatStyle = FlatStyle.Flat;
            c.BackColor = Color.FromArgb(45, 45, 52);
            c.ForeColor = Color.FromArgb(220, 220, 226);
            c.Margin = new Padding(4, 8, 4, 8);
            return c;
        }

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
            b.Margin = new Padding(4, 8, 4, 8);
            b.TabStop = false;
            return b;
        }

        private static Label MakeFieldLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = Color.FromArgb(140, 140, 148);
            l.BackColor = Color.FromArgb(24, 24, 28);
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = new Padding(12, 10, 2, 4);
            return l;
        }

        private static FlowLayoutPanel MakeBar()
        {
            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.FlowDirection = FlowDirection.LeftToRight;
            bar.WrapContents = false;       // one horizontal strip
            bar.BackColor = Color.FromArgb(24, 24, 28);
            return bar;
        }

        // -----------------------------------------------------------------
        // Tiny dark-themed modal for entering a profile name (TextBox +
        // OK/Cancel). Enter = OK, Esc = Cancel; empty names keep it open.
        // -----------------------------------------------------------------
        private class ProfileNameDialog : Form
        {
            private readonly TextBox _box = new TextBox();
            private readonly Button _ok = new Button();
            private readonly Button _cancel = new Button();

            public ProfileNameDialog()
            {
                Text = "Save profile";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(360, 118);
                BackColor = Color.FromArgb(24, 24, 28);
                Font = new Font("Segoe UI", 9f);

                Label prompt = new Label();
                prompt.Text = "Profile name:";
                prompt.AutoSize = true;
                prompt.ForeColor = Color.FromArgb(165, 165, 172);
                prompt.BackColor = Color.FromArgb(24, 24, 28);
                prompt.Location = new Point(14, 14);

                _box.BackColor = Color.FromArgb(14, 14, 16);
                _box.ForeColor = Color.FromArgb(205, 205, 210);
                _box.BorderStyle = BorderStyle.FixedSingle;
                _box.Location = new Point(14, 36);
                _box.Size = new Size(332, 23);
                _box.TextChanged += delegate { UpdateOkState(); };

                _ok.Text = "OK";
                _ok.Size = new Size(80, 28);
                _ok.Location = new Point(182, 74);
                StyleDialogButton(_ok);
                _ok.Click += delegate { AcceptName(); };

                _cancel.Text = "Cancel";
                _cancel.Size = new Size(80, 28);
                _cancel.Location = new Point(266, 74);
                StyleDialogButton(_cancel);
                _cancel.Click += delegate { DialogResult = DialogResult.Cancel; };

                Controls.Add(prompt);
                Controls.Add(_box);
                Controls.Add(_ok);
                Controls.Add(_cancel);
                AcceptButton = _ok;
                CancelButton = _cancel;
                ActiveControl = _box;

                UpdateOkState();
            }

            // The entered name (trimmed). Never null.
            public string ProfileName
            {
                get { return _box.Text == null ? "" : _box.Text.Trim(); }
                set
                {
                    _box.Text = value == null ? "" : value;
                    _box.SelectAll();
                }
            }

            private void AcceptName()
            {
                if (ProfileName.Length == 0) return;    // empty names stay open
                DialogResult = DialogResult.OK;
            }

            private void UpdateOkState()
            {
                _ok.Enabled = ProfileName.Length > 0;
            }

            private static void StyleDialogButton(Button b)
            {
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
                b.ForeColor = Color.FromArgb(220, 220, 226);
                b.BackColor = Color.FromArgb(45, 45, 52);
                b.TabStop = false;
            }
        }
    }
}
