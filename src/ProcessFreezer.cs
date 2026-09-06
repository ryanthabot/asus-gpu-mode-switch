//  ProcessFreezer.cs  (v1.1.0 - Wave 4)
//  ---------------------
//  Suspend/resume of user-chosen background applications during a gaming
//  session (wired into the UI by Wave 6). Suspension uses ntdll's
//  NtSuspendProcess / NtResumeProcess on handles opened through
//  System.Diagnostics.Process - nothing is ever killed.
//
//  The freeze set is %LOCALAPPDATA%\GpuModeSwitch\freezelist.txt, one
//  process name per line (no .exe). The seeded defaults mirror the
//  candidate process names of the TrayApps list in Forms.cs (Parsec,
//  Google Drive, Jellyfin, Riot Client, Riot Vanguard -> parsec,
//  googledrivefs, googledrivesync, jellyfin, riotclient, vgtray, vgc).
//
//  Safety-critical module - the hard rules live in the ProcessFreezer
//  class comment below. Successes log through Log.Chan("FREEZE", ...);
//  failures additionally go out as Log.Warn lines ("ProcessFreezer: ...").
//
//  New file for v1.1.0 (Wave 4, A12) per docs\HANDBOOK.md section 8.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // Outcome of one FreezeSelected() or ResumeAll() round, for the Wave 6
    // UI. Field meaning per method:
    //   FreezeSelected: Suspended = processes frozen, Failed = suspend
    //     attempts that failed, SkippedGuarded = names/PIDs refused by the
    //     guard, Summary = "N suspended, M failed, K guarded-skipped".
    //   ResumeAll: Suspended = processes resumed, Failed = resume attempts
    //     that failed (guarded processes can never be tracked, so this is
    //     always 0 here), Summary = "N resumed, M failed" plus tallies for
    //     processes that exited while frozen or were refused on PID reuse.
    // ---------------------------------------------------------------------
    public struct FreezeResult
    {
        public int Suspended;
        public int Failed;
        public int SkippedGuarded;
        public string Summary;
    }

    // ---------------------------------------------------------------------
    // Process freezer (v1.1.0, Wave 4 / A12). Freezes and resumes the
    // user-configured background apps around a gaming session.
    //
    //  SAFETY BOUNDARY - hard rules:
    //    1. The static guard list below (critical Windows processes, audio,
    //       Defender, this suite's own exe names) plus this app's own
    //       process ID is the safety boundary: guarded names are refused by
    //       IsGuarded() and never suspended. Blank or unreadable process
    //       names count as guarded too (fail closed).
    //    2. Resume works ONLY on the PID + name pairs this session actually
    //       suspended - never on foreign PIDs. Before resuming, the PID's
    //       current process name is re-checked against the recorded name;
    //       a mismatch (the PID was reused by another program) is refused,
    //       logged, and dropped from tracking forever.
    //    3. NOTHING is ever killed - suspend/resume only.
    //    4. Documented limitation for the Wave 6 UI: only processes already
    //       running at FreezeSelected() time get frozen. An app started or
    //       watchdog-restarted after that moment is NOT frozen this
    //       session. A repeated FreezeSelected() skips PIDs it already
    //       tracks, so a process is never suspended twice.
    //    5. List entries whose resume attempt failed stay tracked so a
    //       later ResumeAll() can retry them (no process is left frozen
    //       with no recovery path). ResumeAllSafe() is the never-throwing
    //       variant for crash/exit paths.
    //
    //  Freeze-list names are matched case-insensitively with ".exe"
    //  stripped, exactly via Process.GetProcessesByName (freezing must be
    //  exact, unlike the contains-match TrayApps uses in Forms.cs).
    // ---------------------------------------------------------------------
    public static class ProcessFreezer
    {
        [DllImport("ntdll.dll")]
        private static extern uint NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        private static extern uint NtResumeProcess(IntPtr processHandle);

        private const string RootFolderName = "GpuModeSwitch";
        private const string ListFileName = "freezelist.txt";

        // Default freeze list: the TrayApps candidate process names from
        // Forms.cs (Parsec, Google Drive, Jellyfin, Riot Client, Riot
        // Vanguard), lowercased as the file stores them.
        private static readonly string[] DefaultList = new string[]
        {
            "parsec",
            "googledrivefs",
            "googledrivesync",
            "jellyfin",
            "riotclient",
            "vgtray",
            "vgc",
        };

        // The never-freeze guard list (see rule 1 in the class comment).
        // Compared case-insensitively after ".exe" is stripped.
        private static readonly HashSet<string> GuardedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system", "smss", "csrss", "wininit", "winlogon", "services",
            "lsass", "dwm", "explorer", "audiodg", "MsMpEng",
            "SecurityHealthService",
            "go time", "eco mode"      // this suite's own two exes
        };

        // One successfully suspended process of this session. The recorded
        // Name is what ResumeAll() re-checks the PID against (rule 2).
        private sealed class FrozenPid
        {
            public int Pid;
            public string Name;
        }

        private static readonly object _sessionGate = new object();
        private static readonly List<FrozenPid> _session = new List<FrozenPid>();
        private static int _ownPid = -1;

        // Full path of the freeze list file.
        private static string ListFilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Path.Combine(RootFolderName, ListFileName));
            }
        }

        // This app's own process ID (rule 1). PID 0 as the fallback can
        // never wrongly match a real process.
        private static int OwnPid
        {
            get
            {
                if (_ownPid < 0)
                {
                    try { _ownPid = Process.GetCurrentProcess().Id; }
                    catch { _ownPid = 0; }
                }
                return _ownPid;
            }
        }

        // ---- Public API ----------------------------------------------------

        // Reads the freeze list (one process name per line, no .exe).
        // Tolerates a missing file or read failures with an empty list;
        // blank lines and duplicates are dropped, ".exe" stripped.
        public static List<string> GetUserList()
        {
            List<string> names = new List<string>();
            try
            {
                string path = ListFilePath;
                if (!File.Exists(path)) return names;
                string[] lines = File.ReadAllLines(path);
                foreach (string raw in lines)
                {
                    string name = NormalizeName(raw);
                    if (name.Length == 0) continue;
                    if (!ContainsName(names, name)) names.Add(name);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("ProcessFreezer: could not read " + ListFileName + " - " + ex.Message + " (using an empty list)");
            }
            return names;
        }

        // Persists the freeze list, one process name per line (no .exe).
        // Creates the GpuModeSwitch folder if needed. Best-effort: failures
        // are logged, never thrown.
        public static void SaveUserList(List<string> names)
        {
            try
            {
                List<string> clean = new List<string>();
                if (names != null)
                {
                    foreach (string raw in names)
                    {
                        string name = NormalizeName(raw);
                        if (name.Length == 0) continue;
                        if (!ContainsName(clean, name)) clean.Add(name);
                    }
                }
                string path = ListFilePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(path, clean.ToArray());
                Log.Chan("FREEZE", "ProcessFreezer: saved " + clean.Count + " name(s) to " + ListFileName);
            }
            catch (Exception ex)
            {
                Log.Warn("ProcessFreezer: could not save " + ListFileName + " - " + ex.Message);
            }
        }

        // Writes the default freeze list (the TrayApps process names from
        // Forms.cs) if freezelist.txt does not exist yet. Never overwrites
        // an existing list; best-effort.
        public static void SeedDefaultListIfMissing()
        {
            try
            {
                string path = ListFilePath;
                if (File.Exists(path)) return;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(path, DefaultList);
                Log.Chan("FREEZE", "ProcessFreezer: seeded default freeze list (" + DefaultList.Length + " names) - " + path);
            }
            catch (Exception ex)
            {
                Log.Warn("ProcessFreezer: could not seed " + ListFileName + " - " + ex.Message);
            }
        }

        // True if a process name may never be frozen: it is on the static
        // guard list (critical Windows processes, Defender, this suite's
        // own exe names) or blank/unreadable (fail closed). Case-insensitive,
        // ".exe" stripped. This app's own process ID is checked separately
        // in FreezeSelected() - together they form the safety boundary
        // documented in the class comment.
        public static bool IsGuarded(string processName)
        {
            string name = NormalizeName(processName);
            if (name.Length == 0) return true;
            return GuardedNames.Contains(name);
        }

        // Suspends every running process from the user's freeze list.
        // Guarded names/PIDs are refused and counted; every successful
        // suspension is recorded so ResumeAll() can undo exactly this
        // session's set. A process already frozen this session is skipped
        // (never suspended twice). FreezeSelected() seeds the default list
        // first, so a first run on a fresh machine still freezes the
        // TrayApps defaults.
        public static FreezeResult FreezeSelected()
        {
            FreezeResult r = new FreezeResult();
            SeedDefaultListIfMissing();

            List<string> names = GetUserList();
            if (names.Count == 0)
            {
                r.Summary = "0 suspended, 0 failed, 0 guarded-skipped (freeze list is empty)";
                Log.Chan("FREEZE", "ProcessFreezer: freeze list is empty - nothing to suspend");
                return r;
            }

            Log.Chan("FREEZE", "ProcessFreezer: freezing " + names.Count + " configured name(s)");
            int ownPid = OwnPid;

            foreach (string name in names)
            {
                string baseName = NormalizeName(name);
                if (baseName.Length == 0) continue;

                if (IsGuarded(baseName))
                {
                    r.SkippedGuarded++;
                    Log.Warn("ProcessFreezer: guarded - '" + baseName + "' is on the never-freeze list, not suspended");
                    continue;
                }

                Process[] procs = null;
                try { procs = Process.GetProcessesByName(baseName); }
                catch (Exception ex)
                {
                    r.Failed++;
                    Log.Warn("ProcessFreezer: could not look up '" + baseName + "' - " + ex.Message);
                    continue;
                }

                if (procs.Length == 0)
                {
                    Log.Chan("FREEZE", "ProcessFreezer: '" + baseName + "' is not running - nothing to suspend");
                }

                foreach (Process p in procs)
                {
                    int pid = -1;
                    try
                    {
                        pid = p.Id;   // throws if it exited between listing and here
                        if (pid == ownPid)
                        {
                            r.SkippedGuarded++;
                            Log.Warn("ProcessFreezer: guarded - pid " + pid + " is this app itself, not suspended");
                        }
                        else if (IsGuarded(p.ProcessName))
                        {
                            r.SkippedGuarded++;
                            Log.Warn("ProcessFreezer: guarded - '" + p.ProcessName + "' (pid " + pid + ") is on the never-freeze list, not suspended");
                        }
                        else if (IsTracked(pid))
                        {
                            Log.Chan("FREEZE", "ProcessFreezer: '" + baseName + "' (pid " + pid + ") is already frozen this session - skipped");
                        }
                        else
                        {
                            IntPtr h = p.Handle;   // throws for exited/protected processes
                            uint status = NtSuspendProcess(h);
                            if (status == 0)
                            {
                                Track(pid, baseName);
                                r.Suspended++;
                                Log.Chan("FREEZE", "suspended " + baseName + " (pid " + pid + ")");
                            }
                            else
                            {
                                r.Failed++;
                                Log.Warn("ProcessFreezer: could not suspend '" + baseName + "' (pid " + pid + ") - NTSTATUS 0x" + status.ToString("X8"));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        r.Failed++;
                        Log.Warn("ProcessFreezer: could not suspend '" + baseName + "' (pid " + (pid >= 0 ? pid.ToString() : "?") + ") - " + ex.Message);
                    }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }

            r.Summary = string.Format("{0} suspended, {1} failed, {2} guarded-skipped", r.Suspended, r.Failed, r.SkippedGuarded);
            Log.Chan("FREEZE", "ProcessFreezer: freeze round done - " + r.Summary);
            return r;
        }

        // Resumes exactly the processes this session suspended (tracked
        // list only - never foreign PIDs; each PID is re-checked against
        // the recorded name first). Clearing the session list makes a
        // second call a clean no-op; entries whose resume attempt failed
        // stay tracked for a retry (rule 5). Empty list = clean no-op
        // returning zeros.
        public static FreezeResult ResumeAll()
        {
            FreezeResult r = new FreezeResult();

            List<FrozenPid> toResume;
            lock (_sessionGate)
            {
                toResume = new List<FrozenPid>(_session);
                _session.Clear();
            }

            if (toResume.Count == 0)
            {
                r.Summary = "0 resumed, 0 failed (nothing was frozen this session)";
                Log.Chan("FREEZE", "ProcessFreezer: nothing was frozen this session - nothing to resume");
                return r;
            }

            Log.Chan("FREEZE", "ProcessFreezer: resuming " + toResume.Count + " process(es) frozen this session");
            int gone = 0;
            int refused = 0;
            List<FrozenPid> retry = new List<FrozenPid>();

            foreach (FrozenPid fp in toResume)
            {
                bool keepTracked = false;
                Process p = null;
                try { p = Process.GetProcessById(fp.Pid); }   // ArgumentException if not running
                catch (ArgumentException)
                {
                    gone++;
                    Log.Chan("FREEZE", "ProcessFreezer: '" + fp.Name + "' (pid " + fp.Pid + ") already exited - nothing to resume");
                }
                catch (Exception ex)
                {
                    r.Failed++;
                    Log.Warn("ProcessFreezer: could not verify '" + fp.Name + "' (pid " + fp.Pid + ") - " + ex.Message + " (not resumed)");
                }

                if (p != null)
                {
                    try
                    {
                        string nowName = null;
                        try { nowName = p.ProcessName; } catch { nowName = null; }

                        if (nowName == null)
                        {
                            gone++;
                            Log.Chan("FREEZE", "ProcessFreezer: '" + fp.Name + "' (pid " + fp.Pid + ") already exited - nothing to resume");
                        }
                        else if (!string.Equals(NormalizeName(nowName), fp.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            refused++;
                            Log.Warn("ProcessFreezer: pid " + fp.Pid + " now runs '" + nowName + "', not the '" + fp.Name + "' suspended this session - not resumed (PID-reuse guard)");
                        }
                        else
                        {
                            IntPtr h = p.Handle;
                            uint status = NtResumeProcess(h);
                            if (status == 0)
                            {
                                r.Suspended++;
                                Log.Chan("FREEZE", "resumed " + fp.Name + " (pid " + fp.Pid + ")");
                            }
                            else
                            {
                                r.Failed++;
                                keepTracked = true;
                                Log.Warn("ProcessFreezer: could not resume '" + fp.Name + "' (pid " + fp.Pid + ") - NTSTATUS 0x" + status.ToString("X8") + " (kept for retry)");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        r.Failed++;
                        keepTracked = true;
                        Log.Warn("ProcessFreezer: could not resume '" + fp.Name + "' (pid " + fp.Pid + ") - " + ex.Message + " (kept for retry)");
                    }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }

                if (keepTracked) retry.Add(fp);
            }

            foreach (FrozenPid fp in retry) Track(fp.Pid, fp.Name);

            r.Summary = string.Format("{0} resumed, {1} failed", r.Suspended, r.Failed);
            if (gone > 0) r.Summary += ", " + gone + " gone (exited while frozen)";
            if (refused > 0) r.Summary += ", " + refused + " refused (PID reuse)";
            if (retry.Count > 0) r.Summary += ", " + retry.Count + " still frozen (kept for retry)";
            Log.Chan("FREEZE", "ProcessFreezer: resume done - " + r.Summary);
            return r;
        }

        // ResumeAll() for crash/exit paths (Wave 6): identical, but swallows
        // every exception so a cleanup path can never throw. Still only ever
        // touches this session's own tracked PIDs; never kills anything.
        public static void ResumeAllSafe()
        {
            try
            {
                ResumeAll();
            }
            catch (Exception ex)
            {
                try { Log.Warn("ProcessFreezer: ResumeAllSafe swallowed an exception - " + ex.Message); }
                catch { }
            }
        }

        // ---- Helpers -------------------------------------------------------

        // Trims, strips one trailing ".exe" (any case). Returns "" when
        // nothing usable remains (an empty name is treated as guarded).
        private static string NormalizeName(string raw)
        {
            if (raw == null) return "";
            string name = raw.Trim();
            if (name.Length >= 4 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 4).Trim();
            }
            return name;
        }

        private static bool ContainsName(List<string> names, string name)
        {
            foreach (string existing in names)
            {
                if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool IsTracked(int pid)
        {
            lock (_sessionGate)
            {
                foreach (FrozenPid fp in _session)
                {
                    if (fp.Pid == pid) return true;
                }
            }
            return false;
        }

        private static void Track(int pid, string name)
        {
            lock (_sessionGate)
            {
                foreach (FrozenPid fp in _session)
                {
                    if (fp.Pid == pid) return;   // paranoia: never double-track
                }
                FrozenPid entry = new FrozenPid();
                entry.Pid = pid;
                entry.Name = name;
                _session.Add(entry);
            }
        }
    }
}
