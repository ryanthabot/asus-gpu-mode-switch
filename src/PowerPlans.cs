//  PowerPlans.cs  (v1.1.0 - Wave 4, agent A13)
//  -------------------------------------------
//  Ultimate Performance power plan switcher plus a session-scoped Windows
//  Update pauser. Both halves are best-effort, fully reversible and never
//  throw to callers: PowerPlans returns bool, WuPause swallows and logs
//  everything, and every step lands in the log through the POWER channel.
//
//  PowerPlans
//  ----------
//  SetUltimate() duplicates Windows' hidden Ultimate Performance scheme
//  (template GUID e9a42b02-d5df-448d-aa00-03f14749eb61) once, remembers the
//  plan that was active before the first activation, then activates it.
//  RestorePrevious() switches back to that remembered plan. The bookkeeping
//  lives in %LOCALAPPDATA%\GpuModeSwitch\powerplan.txt (folder created when
//  missing), two lines:
//      line 1: GUID of the scheme WE created (kept forever, so later runs
//              reuse the same scheme instead of piling up duplicates)
//      line 2: GUID of the plan that was active before the first
//              successful SetUltimate() - cleared again by RestorePrevious()
//  Idempotence rules:
//    - SetUltimate() reuses the stored created-GUID when `powercfg /query`
//      still accepts it (exit code 0) and duplicates a fresh one otherwise;
//    - the previous plan is captured only on the first successful run and
//      never overwritten afterwards (and never set to the Ultimate plan
//      itself);
//    - RestorePrevious() without a remembered previous plan is a logged
//      no-op, so it is safe on any exit path and safe to call twice.
//
//  WuPause
//  -------
//  Stops wuauserv (Windows Update), bits (Background Intelligent Transfer
//  Service) and DoSvc (Delivery Optimization) for the current session and
//  restarts exactly the services THIS process stopped. A service is recorded
//  in a static list only when our Stop() call was accepted, and
//  ResumeUpdates() touches nothing else. ResumeUpdates() is exception-safe
//  by design so it can run on any exit path: every failure is caught and
//  logged, never thrown.
//
//  NOTE: WuPause never changes a service's StartType (Automatic / Manual /
//  Disabled stay untouched). Only the running instance is stopped/started;
//  a service left stopped (for example after a timed-out stop) comes back
//  by itself at the next reboot or manual start.
//
//  Everything here shells out to powercfg.exe (documented CLI, no P/Invoke
//  needed) and uses ServiceController, matching the process/service
//  handling style of GamePrep.cs and AsusControl.cs. New file for v1.1.0
//  (Wave 4, A13) per docs\HANDBOOK.md section 8.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // Ultimate Performance plan switcher. See the file header for the state
    // file format and the idempotence rules. All failures are logged with
    // the raw powercfg output and reported as false - never thrown.
    // ---------------------------------------------------------------------
    public static class PowerPlans
    {
        // The well-known hidden "Ultimate Performance" scheme template.
        private const string UltimateTemplateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

        // In-memory mirror of the state file, used as a fallback for the
        // current session when the file itself cannot be read.
        private static string _createdPlan = "";
        private static string _previousPlan = "";

        // First GUID in a text; Guid.Empty when none parses.
        private static readonly Regex GuidPattern = new Regex(
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

        // Duplicates (once) and activates the Ultimate Performance plan,
        // remembering the previously active plan for RestorePrevious().
        public static bool SetUltimate()
        {
            try
            {
                string created;
                string previous;
                ReadState(out created, out previous);

                // 1. Resolve the scheme to activate: reuse the one we created
                //    earlier if it still exists, otherwise duplicate it.
                if (created.Length > 0)
                {
                    string queryOutput;
                    if (!RunPowercfg("/query " + created, out queryOutput))
                    {
                        Log.Chan("POWER", "power: stored Ultimate plan " + created +
                            " no longer exists - duplicating a fresh one");
                        created = "";
                    }
                    else
                    {
                        Log.Chan("POWER", "power: reusing previously created Ultimate plan " + created);
                    }
                }
                if (created.Length == 0)
                {
                    string duplicateOutput;
                    if (!RunPowercfg("-duplicatescheme " + UltimateTemplateGuid, out duplicateOutput))
                    {
                        Log.Error("power: could not duplicate the Ultimate Performance plan - " + Summarize(duplicateOutput));
                        return false;
                    }
                    Guid duplicated = ParseGuid(duplicateOutput);
                    if (duplicated == Guid.Empty)
                    {
                        Log.Error("power: could not parse the new scheme GUID from powercfg output - " + Summarize(duplicateOutput));
                        return false;
                    }
                    created = duplicated.ToString("D");
                    Log.Chan("POWER", "power: duplicated Ultimate Performance plan " + created);
                }

                // 2. Remember the previously active plan (first run only).
                if (string.Equals(previous, created, StringComparison.OrdinalIgnoreCase))
                {
                    previous = "";      // sanitize a corrupt state file
                }
                string active = CurrentActiveGuid();
                if (previous.Length == 0)
                {
                    if (active.Length == 0)
                    {
                        Log.Chan("POWER", "power: could not read the active plan - nothing to remember yet");
                    }
                    else if (string.Equals(active, created, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Chan("POWER", "power: the Ultimate plan is already active - no previous plan to remember");
                    }
                    else
                    {
                        previous = active;
                        Log.Chan("POWER", "power: previous plan " + previous + " remembered");
                    }
                }

                // 3. Persist the state BEFORE activating, so a crash between
                //    the two still leaves a restorable record.
                WriteState(created, previous);

                // 4. Activate (skipped when already active - idempotent re-runs).
                if (string.Equals(active, created, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Chan("POWER", "power: Ultimate Performance already active");
                    return true;
                }
                string setActiveOutput;
                if (!RunPowercfg("/setactive " + created, out setActiveOutput))
                {
                    Log.Error("power: could not activate the Ultimate Performance plan - " + Summarize(setActiveOutput));
                    return false;
                }
                Log.Chan("POWER", "power: activated Ultimate Performance");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("power: SetUltimate failed", ex);
                return false;
            }
        }

        // Restores the plan that was active before the first SetUltimate().
        // Idempotent and safe on any exit path: without a remembered previous
        // plan it is a logged no-op. On success the created-GUID is kept (so
        // SetUltimate keeps reusing the same scheme) and only the previous
        // plan is cleared.
        public static bool RestorePrevious()
        {
            try
            {
                string created;
                string previous;
                ReadState(out created, out previous);
                if (previous.Length == 0)
                {
                    Log.Chan("POWER", "power: nothing to restore");
                    return true;
                }
                string setActiveOutput;
                if (!RunPowercfg("/setactive " + previous, out setActiveOutput))
                {
                    Log.Error("power: could not restore the previous plan " + previous + " - " + Summarize(setActiveOutput));
                    return false;
                }
                Log.Chan("POWER", "power: restored plan " + previous);
                WriteState(created, "");    // keep the created scheme for reuse
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("power: RestorePrevious failed", ex);
                return false;
            }
        }

        // ---- helpers -------------------------------------------------------

        // Runs powercfg.exe with the given arguments (console hidden, both
        // output streams captured). Returns true when the exit code is 0;
        // `output` holds stdout (+ stderr when present), or the failure reason.
        // Never logs and never throws - the caller decides how to report a
        // failure, with the raw output.
        private static bool RunPowercfg(string arguments, out string output)
        {
            output = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powercfg";
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    // stdout first: a /query dump can exceed the pipe buffer
                    // while powercfg's stderr stays empty until exit, so this
                    // order cannot deadlock.
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(30000))
                    {
                        try { p.Kill(); } catch { }
                        output = Summarize(stdout + " " + stderr) + " (powercfg did not exit within 30s)";
                        return false;
                    }
                    output = (stdout + Environment.NewLine + stderr).Trim();
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                output = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // GUID of the currently active power scheme ("" when it cannot be read).
        private static string CurrentActiveGuid()
        {
            string output;
            if (!RunPowercfg("/getactivescheme", out output)) return "";
            Guid active = ParseGuid(output);
            return active == Guid.Empty ? "" : active.ToString("D");
        }

        // First GUID found in the text; Guid.Empty when none parses.
        private static Guid ParseGuid(string text)
        {
            if (string.IsNullOrEmpty(text)) return Guid.Empty;
            Match m = GuidPattern.Match(text);
            while (m.Success)
            {
                Guid parsed;
                if (Guid.TryParse(m.Value, out parsed)) return parsed;
                m = m.NextMatch();
            }
            return Guid.Empty;
        }

        // Same as ParseGuid but as a lowercase string ("" when absent) for the
        // state file and log lines.
        private static string NormalizeGuid(string text)
        {
            Guid parsed = ParseGuid(text);
            return parsed == Guid.Empty ? "" : parsed.ToString("D");
        }

        // Path of the state file: %LOCALAPPDATA%\GpuModeSwitch\powerplan.txt
        private static string StateFilePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine("GpuModeSwitch", "powerplan.txt"));
        }

        // Reads the state file (two lines: created-GUID, previous-GUID; either
        // may be missing or empty). Falls back to the in-session mirror when
        // the file cannot be read. Never throws.
        private static void ReadState(out string created, out string previous)
        {
            created = _createdPlan;
            previous = _previousPlan;
            try
            {
                string path = StateFilePath();
                if (!File.Exists(path)) return;
                string[] lines = File.ReadAllLines(path);
                string storedCreated = NormalizeGuid(lines.Length > 0 ? lines[0] : "");
                string storedPrevious = NormalizeGuid(lines.Length > 1 ? lines[1] : "");
                if (storedCreated.Length > 0)
                {
                    created = storedCreated;
                    previous = storedPrevious;      // may be "" - the file wins
                }
            }
            catch (Exception ex)
            {
                Log.Warn("power: could not read the power plan state file - " + ex.Message);
            }
        }

        // Writes the state file (creating the folder when needed) and updates
        // the in-session mirror. Best-effort: a file failure is logged as an
        // error, not thrown.
        private static void WriteState(string created, string previous)
        {
            _createdPlan = created;
            _previousPlan = previous;
            try
            {
                string path = StateFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, new string[] { created, previous });
            }
            catch (Exception ex)
            {
                Log.Error("power: could not write the power plan state file - " + ex.Message);
            }
        }

        // One-line, length-capped version of raw command output for log lines.
        private static string Summarize(string text)
        {
            string t = text == null ? "" : text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (t.Contains("  ")) t = t.Replace("  ", " ");
            if (t.Length > 500) t = t.Substring(0, 500) + "...";
            return t.Length == 0 ? "(no output)" : t;
        }
    }

    // ---------------------------------------------------------------------
    // Session-scoped Windows Update pauser: stops wuauserv (Windows Update),
    // bits (Background Intelligent Transfer Service) and DoSvc (Delivery
    // Optimization) on PauseUpdates() and restarts exactly the services THIS
    // process stopped on ResumeUpdates(). StartType is never changed (see
    // the file header). Every step is individually try/caught, so
    // ResumeUpdates is safe on any exit path - a failure never escapes.
    // ---------------------------------------------------------------------
    public static class WuPause
    {
        private static readonly string[] WuServices = { "wuauserv", "bits", "DoSvc" };

        private static readonly object _gate = new object();
        private static readonly List<string> _stoppedByUs = new List<string>();

        // Stops the Windows Update services for this session. Already-stopped
        // services are logged and skipped; only services whose Stop() we
        // actually issued are recorded for ResumeUpdates(). Safe to call
        // more than once.
        public static void PauseUpdates()
        {
            try
            {
                foreach (string name in WuServices)
                {
                    PauseOne(name);
                }
            }
            catch (Exception ex)
            {
                Log.Error("wu-pause: PauseUpdates failed", ex);
            }
        }

        // Restarts the services this session stopped. With nothing recorded
        // it is a logged no-op. Exception-safe by design (exit paths).
        public static void ResumeUpdates()
        {
            try
            {
                string[] names;
                lock (_gate)
                {
                    if (_stoppedByUs.Count == 0)
                    {
                        Log.Chan("POWER", "wu-pause: nothing to resume");
                        return;
                    }
                    names = _stoppedByUs.ToArray();
                    _stoppedByUs.Clear();       // one restore attempt per session
                }
                foreach (string name in names)
                {
                    ResumeOne(name);
                }
            }
            catch (Exception ex)
            {
                Log.Error("wu-pause: ResumeUpdates failed", ex);
            }
        }

        private static void PauseOne(string name)
        {
            try
            {
                using (ServiceController sc = new ServiceController(name))
                {
                    if (sc.Status == ServiceControllerStatus.Running ||
                        sc.Status == ServiceControllerStatus.StartPending)
                    {
                        sc.Stop();
                        lock (_gate)            // Stop() accepted - we own the restore
                        {
                            if (!_stoppedByUs.Contains(name)) _stoppedByUs.Add(name);
                        }
                        try
                        {
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                            Log.Chan("POWER", "wu-pause: stopped " + name);
                        }
                        catch (System.ServiceProcess.TimeoutException)
                        {
                            try { sc.Refresh(); } catch { }
                            Log.Warn("wu-pause: " + name + " stop timed out after 15s (last status " + sc.Status +
                                ") - it is still recorded for the restore");
                        }
                    }
                    else if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        Log.Chan("POWER", "wu-pause: " + name + " was already stopped");
                    }
                    else
                    {
                        Log.Chan("POWER", "wu-pause: " + name + " was already " + sc.Status + " - left alone");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("wu-pause: " + name + " could not be stopped - " + ex.Message);
            }
        }

        private static void ResumeOne(string name)
        {
            try
            {
                using (ServiceController sc = new ServiceController(name))
                {
                    if (sc.Status == ServiceControllerStatus.Stopped ||
                        sc.Status == ServiceControllerStatus.StopPending)
                    {
                        sc.Start();
                        try
                        {
                            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                            Log.Chan("POWER", "wu-pause: restarted " + name);
                        }
                        catch (System.ServiceProcess.TimeoutException)
                        {
                            try { sc.Refresh(); } catch { }
                            Log.Warn("wu-pause: " + name + " start timed out after 15s (last status " + sc.Status + ")");
                        }
                    }
                    else
                    {
                        Log.Chan("POWER", "wu-pause: " + name + " already running again (status " + sc.Status + ")");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("wu-pause: " + name + " could not be restarted - " + ex.Message);
            }
        }
    }
}
