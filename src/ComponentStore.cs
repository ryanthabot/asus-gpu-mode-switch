//  ComponentStore.cs  (v1.1.0 - Wave 5, agent A8)
//  ------------------------------------------------
//  Tier 2 storage cleanup: the WinSxS component store, handled STRICTLY
//  through DISM. Hard boundary (design decision D7): C:\Windows\WinSxS is
//  NEVER touched by this code except through Dism.exe - no enumeration, no
//  deletion, no file IO of any kind against that folder. Everything this
//  class does is run two documented DISM commands and report what they say.
//
//  Analyze() runs the read-only
//      Dism.exe /Online /Cleanup-Image /AnalyzeComponentStore
//  and parses the "Component Store Cleanup Recommended : Yes/No",
//  "Actual Size of Component Store :" and "Size of Component Store in
//  WinSxS folder :" lines out of its output. The full raw output is kept
//  in DismAnalysis.OutputText (so the UI/log also shows the
//  "... Reclaimable ..." detail lines); a parse miss on a localized
//  Windows is logged as a WARN, raw output preserved.
//
//  RunCleanup() is the Tier 2 executor and is analyze-first ALWAYS (D3):
//    1. re-check the D7 safety gates - StorageAnalyzer.CheckGates() must
//       come back empty; any block reason is logged as an ERROR and the
//       cleanup is refused (fail closed),
//    2. run the analyze step again and continue only when DISM itself
//       recommends a cleanup ("nothing to do" is a success),
//    3. run the ONE mutating command this suite may ever use:
//          Dism.exe /Online /Cleanup-Image /StartComponentCleanup
//
//  /ResetBase is FORBIDDEN by design decision D3 - never append it. D3
//  Tier 3 (REJECTED) says: "DISM /ResetBase and Windows.old removal are
//  explicitly rejected for this suite (irreversible, breaks update
//  uninstall/repair). Do not implement them." The cleanup argument string
//  exists only as the CleanupArguments const below and a defensive runtime
//  guard refuses to execute anything that ever contains /ResetBase.
//
//  DISM "Error: 1726" (the remote procedure call failed) on
//  /StartComponentCleanup is a known Windows 11 24H2+ issue where the
//  cleanup actually ran; it is logged as a retryable warning and treated
//  as success (non-fatal), per the Wave 5 contract.
//
//  Timing: the analyze step can take several minutes and the cleanup tens
//  of minutes. Analyze() kills DISM after 20 minutes and RunCleanup after
//  45 (both log an ERROR on timeout and keep whatever output was captured).
//  Both streams are pumped asynchronously so the hard timeout stays in
//  charge even if DISM ever hangs mid-line. A single busy flag serializes
//  the two operations - they can never overlap each other or themselves;
//  a refused call is logged, and IsBusy lets the UI disable its controls
//  while DISM runs. Callers are expected to invoke them on a background
//  thread (the RunBg/SafeInvoke pattern).
//
//  Logging goes through the CLEAN channel: "dism analyze: ...",
//  "dism cleanup: ..." and streamed cleanup progress as "dism: <line>".
//  DISM's backspace/spinner progress padding is collapsed: only lines that
//  differ after stripping '\b' and trimming are logged. Nothing here throws
//  to callers - every failure is logged and returned as a value.
//
//  New in v1.1.0 Wave 5 per docs\HANDBOOK.md section 4 (D3/D7) and
//  section 8 (A8).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // Result of one DISM /AnalyzeComponentStore run. Plain data holder for
    // the Wave 6 UI and the cleanup flow; the full raw DISM output is kept
    // in OutputText so nothing DISM said is lost (including the "Size of
    // Component Store in WinSxS folder" and "... Reclaimable ..." lines).
    // ---------------------------------------------------------------------
    public class DismAnalysis
    {
        public bool CleanupRecommended;     // "Component Store Cleanup Recommended : Yes"
        public string ActualSizeText;       // value after "Actual Size of Component Store :"
        public string SizeText;             // value after "Size of Component Store in WinSxS folder :"
        public bool ReclaimableShown;       // any output line mentioned "Reclaimable"
        public int ExitCode;                // DISM's exit code (-1 when it never exited cleanly)
        public string OutputText;           // raw stdout, preserved in full
        public string ErrorText;            // raw stderr (+ failure description)

        public DismAnalysis()
        {
            CleanupRecommended = false;
            ActualSizeText = "";
            SizeText = "";
            ReclaimableShown = false;
            ExitCode = -1;
            OutputText = "";
            ErrorText = "";
        }
    }

    // ---------------------------------------------------------------------
    // WinSxS component store analyze + cleanup, strictly via DISM. See the
    // file header for the D3 (/ResetBase forbidden, analyze-first) and D7
    // (WinSxS only ever touched by Dism.exe; gates checked before any
    // cleanup) boundaries and for the timing/serialization rules.
    // ---------------------------------------------------------------------
    public static class ComponentStore
    {
        // ---- DISM command lines (the ONLY ones this class can execute) ----

        // Read-only analysis of the component store.
        private const string AnalyzeArguments = "/Online /Cleanup-Image /AnalyzeComponentStore";

        // The Tier 2 cleanup.
        // /ResetBase is FORBIDDEN by design decision D3 - never append it.
        // (D3 Tier 3 is REJECTED: /ResetBase is irreversible and breaks
        // update uninstall/repair.) This const is the single place the
        // cleanup arguments exist; nothing is appended to it at runtime,
        // and RunCleanup() additionally refuses to execute the string if
        // it ever contains /ResetBase.
        private const string CleanupArguments = "/Online /Cleanup-Image /StartComponentCleanup";

        // Analyze can take several minutes; the cleanup tens of minutes.
        private const int AnalyzeTimeoutMs = 20 * 60 * 1000;    // 20 minutes
        private const int CleanupTimeoutMs = 45 * 60 * 1000;    // 45 minutes

        // Meaningful lines kept for the UI tail (RunCleanup's outputTail).
        private const int OutputTailLines = 40;

        // Analyze-output markers (matched case-insensitively; English DISM
        // output - on a localized Windows the parse stays empty, which is
        // logged as a WARN while the raw output is still preserved).
        private const string RecommendedMarker = "Component Store Cleanup Recommended";
        private const string ActualSizeMarker = "Actual Size of Component Store";
        private const string FolderSizeMarker = "Size of Component Store in WinSxS folder";
        private const string ReclaimableMarker = "Reclaimable";

        private static readonly object _gate = new object();
        private static bool _busy;

        // True while an Analyze()/RunCleanup() DISM operation is in flight
        // (UI hint: disable the component-store controls while it runs).
        public static bool IsBusy
        {
            get
            {
                lock (_gate) { return _busy; }
            }
        }

        // ---- public API ----------------------------------------------------

        // Runs the read-only DISM component-store analysis. Never throws; on
        // any failure the returned DismAnalysis carries ExitCode = -1, a
        // description in ErrorText and whatever output was captured. A call
        // while another DISM operation runs is refused (logged) and returns
        // an empty analysis with that note in ErrorText.
        public static DismAnalysis Analyze()
        {
            lock (_gate)
            {
                if (_busy)
                {
                    Log.Warn("dism analyze: another DISM operation is already running - request refused");
                    DismAnalysis refused = new DismAnalysis();
                    refused.ErrorText = "another DISM operation is already in progress";
                    return refused;
                }
                _busy = true;
            }
            try
            {
                return AnalyzeLocked();
            }
            finally
            {
                lock (_gate) { _busy = false; }
            }
        }

        // Convenience wrapper: runs Analyze() and answers just the question
        // "does DISM recommend a component store cleanup?".
        public static bool CleanupRecommended()
        {
            DismAnalysis result = Analyze();
            return result != null && result.CleanupRecommended;
        }

        // Tier 2 executor: D7 gate re-check -> analyze-first ALWAYS (D3) ->
        // /StartComponentCleanup (never /ResetBase). Returns true on success
        // and on the "nothing to do" / error-1726 paths; false when the gates
        // refuse, DISM could not be run/finished in time, or it exited with
        // a real failure. outputTail carries the last ~40 meaningful output
        // lines (\r\n-joined) for the UI; "" on the early paths.
        public static bool RunCleanup(out string outputTail)
        {
            outputTail = "";
            lock (_gate)
            {
                if (_busy)
                {
                    Log.Warn("dism cleanup: another DISM operation is already running - request refused");
                    return false;
                }
                _busy = true;
            }
            try
            {
                return RunCleanupLocked(out outputTail);
            }
            finally
            {
                lock (_gate) { _busy = false; }
            }
        }

        // ---- steps (run while holding the busy flag) ------------------------

        private static bool RunCleanupLocked(out string outputTail)
        {
            outputTail = "";

            // a) GATE RE-CHECK (D7): any block reason fails closed.
            List<string> reasons = StorageAnalyzer.CheckGates();
            if (reasons.Count > 0)
            {
                foreach (string reason in reasons)
                {
                    Log.Error("dism cleanup: gate blocked - " + reason);
                }
                Log.Chan("CLEAN", "dism cleanup: refused (" + reasons.Count + " safety gate reason(s))");
                return false;
            }

            // b) Analyze-first ALWAYS (D3): only run the cleanup when DISM's
            //    own analysis recommends it. "Nothing to do" is a success.
            DismAnalysis pre = AnalyzeLocked();
            if (!pre.CleanupRecommended)
            {
                if (pre.ExitCode < 0 && pre.ErrorText.Length > 0)
                {
                    Log.Chan("CLEAN", "dism cleanup: skipped (analyze step did not complete - " +
                        Summarize(pre.ErrorText) + ")");
                }
                else
                {
                    Log.Chan("CLEAN", "dism cleanup: skipped (analyzer says not recommended)");
                }
                return true;
            }

            // c) /StartComponentCleanup - the only mutating command this
            //    class can ever run. Defensive D3 guard: the argument string
            //    comes from the const above and nothing is appended, but if
            //    a future edit ever sneaks /ResetBase in, refuse to run.
            if (CleanupArguments.IndexOf("/ResetBase", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Log.Error("dism cleanup: /ResetBase is FORBIDDEN by design decision D3 - aborted");
                return false;
            }

            Log.Chan("CLEAN", "dism cleanup: running Dism.exe " + CleanupArguments +
                " (progress follows; this can take up to 45 minutes)");

            List<string> meaningful = new List<string>();
            object lineGate = new object();
            string lastLogged = "";
            string stdout;
            string stderr;
            int exit;
            bool timedOut;
            bool exited = RunDism(CleanupArguments, CleanupTimeoutMs,
                delegate(string rawLine)
                {
                    string cleaned = CleanLine(rawLine);
                    if (cleaned.Length == 0) return;
                    bool changed;
                    lock (lineGate)
                    {
                        meaningful.Add(cleaned);
                        // Collapse DISM's backspace/spinner progress: log a
                        // line only when it differs from the previous one
                        // after stripping '\b' and trimming the padding.
                        changed = !string.Equals(cleaned, lastLogged, StringComparison.Ordinal);
                        if (changed) lastLogged = cleaned;
                    }
                    if (changed) Log.Chan("CLEAN", "dism: " + cleaned);
                },
                out timedOut, out stdout, out stderr, out exit);

            outputTail = BuildTail(meaningful);

            if (!exited)
            {
                if (timedOut)
                {
                    Log.Error("dism cleanup: timed out after 45 minutes - DISM was killed (partial output kept in the tail)");
                }
                else
                {
                    Log.Error("dism cleanup: Dism.exe could not be run - " + Summarize(stderr));
                }
                return false;
            }

            // d) Final line (logged for every completed run, good or bad).
            Log.Chan("CLEAN", "dism cleanup: exit " + Inv(exit));

            // Error 1726 ("the remote procedure call failed") is a known
            // 24H2+ StartComponentCleanup issue where the cleanup actually
            // ran - retryable warning, non-fatal (success).
            string combined = stdout + Environment.NewLine + stderr;
            if (exit == 1726 ||
                combined.IndexOf("error 1726", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("error: 1726", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Log.Warn("dism cleanup: error 1726 (known 24H2+ issue, treated as retryable warning)");
                return true;
            }
            if (exit != 0)
            {
                Log.Error("dism cleanup: DISM reported a failure - " + Summarize(combined));
                return false;
            }
            return true;
        }

        // Analyze body; the caller holds the busy flag.
        private static DismAnalysis AnalyzeLocked()
        {
            DismAnalysis result = new DismAnalysis();
            Log.Chan("CLEAN", "dism analyze: running Dism.exe " + AnalyzeArguments +
                " (read-only; this can take several minutes)");

            string stdout;
            string stderr;
            int exit;
            bool timedOut;
            bool exited = RunDism(AnalyzeArguments, AnalyzeTimeoutMs, null,
                out timedOut, out stdout, out stderr, out exit);

            result.OutputText = stdout;
            result.ErrorText = (stderr == null ? "" : stderr).Trim();

            if (!exited)
            {
                result.ExitCode = -1;
                if (timedOut)
                {
                    Log.Error("dism analyze: timed out after 20 minutes - DISM was killed (partial output kept)");
                }
                else
                {
                    Log.Error("dism analyze: Dism.exe could not be run - " + Summarize(result.ErrorText));
                }
                return result;
            }

            result.ExitCode = exit;
            bool recommendedSeen = ParseAnalyzeOutput(result, stdout);
            if (exit != 0)
            {
                Log.Error("dism analyze: exit " + Inv(exit) +
                    (result.ErrorText.Length > 0 ? " - " + Summarize(result.ErrorText) : ""));
            }
            else if (!recommendedSeen)
            {
                Log.Warn("dism analyze: expected lines were not found in the DISM output (localized Windows?) - " +
                    "raw output preserved in DismAnalysis.OutputText");
            }
            Log.Chan("CLEAN", "dism analyze: exit " + Inv(exit) + ", cleanup recommended=" +
                (recommendedSeen ? (result.CleanupRecommended ? "Yes" : "No") : "unknown") +
                ", actual size " + (result.ActualSizeText.Length > 0 ? result.ActualSizeText : "(not parsed)"));
            return result;
        }

        // ---- DISM execution --------------------------------------------------

        // Runs Dism.exe hidden (UseShellExecute=false, CreateNoWindow) with
        // both streams redirected. When `onLine` is non-null it is invoked
        // with every stdout line as it arrives (the cleanup's streamed
        // progress); stdout/stderr always come back with whatever was
        // captured. Returns true when DISM ran and exited within `timeoutMs`
        // (then `exitCode` is DISM's real exit code); returns false when the
        // process was killed after the timeout (`timedOut` = true) or when
        // it could not be started at all (`timedOut` = false, the failure
        // described in `stderr`). Never logs - the callers own the reporting.
        private static bool RunDism(string arguments, int timeoutMs, Action<string> onLine,
                                    out bool timedOut, out string stdout, out string stderr,
                                    out int exitCode)
        {
            timedOut = false;
            stdout = "";
            stderr = "";
            exitCode = -1;
            StringBuilder outBuf = new StringBuilder();
            StringBuilder errBuf = new StringBuilder();
            object bufGate = new object();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = DismPath();
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (Process p = Process.Start(psi))
                {
                    // Both streams are pumped asynchronously: DISM rewrites
                    // its progress line with \b/\r padding and can go minutes
                    // between newlines, so a synchronous ReadToEnd/ReadLine
                    // on this thread could outwait the hard timeout below.
                    // The event pumps keep this thread free to enforce it.
                    p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lock (bufGate) { outBuf.AppendLine(e.Data); }
                        if (onLine != null) onLine(e.Data);
                    };
                    p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lock (bufGate) { errBuf.AppendLine(e.Data); }
                    };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    bool exited = p.WaitForExit(timeoutMs);
                    if (!exited)
                    {
                        timedOut = true;
                        try { p.Kill(); } catch { }
                    }
                    // Parameterless WaitForExit also lets the async output
                    // pumps drain (required after a timed WaitForExit).
                    p.WaitForExit();
                    lock (bufGate)
                    {
                        stdout = outBuf.ToString();
                        stderr = errBuf.ToString();
                    }
                    if (!exited) return false;
                    exitCode = p.ExitCode;
                    return true;
                }
            }
            catch (Exception ex)
            {
                stderr = (stderr.Length > 0 ? stderr + Environment.NewLine : "") +
                    ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // Full path of Dism.exe (next to the OS in System32); falls back to
        // the bare name and lets CreateProcess resolve it from its search
        // path. Best-effort, never throws.
        private static string DismPath()
        {
            try
            {
                string systemDir = Environment.SystemDirectory;     // C:\Windows\System32
                if (!string.IsNullOrEmpty(systemDir))
                {
                    string candidate = Path.Combine(systemDir, "Dism.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch
            {
                // fall through to the bare name
            }
            return "Dism.exe";
        }

        // ---- analyze parsing -------------------------------------------------

        // Parses the interesting lines out of the raw analyze output
        // (case-insensitive contains; value = text after the first ':').
        // Returns true when the "Component Store Cleanup Recommended" marker
        // was seen at all (so a localized-Windows parse miss is detectable).
        private static bool ParseAnalyzeOutput(DismAnalysis result, string stdout)
        {
            bool recommendedSeen = false;
            string[] lines = stdout.Replace("\r\n", "\n").Split('\n');
            foreach (string raw in lines)
            {
                string line = CleanLine(raw);
                if (line.Length == 0) continue;

                if (line.IndexOf(RecommendedMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    recommendedSeen = true;
                    result.CleanupRecommended = AfterColon(line).StartsWith("yes", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (line.IndexOf(ActualSizeMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.ActualSizeText = AfterColon(line);
                    continue;
                }
                if (line.IndexOf(FolderSizeMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.SizeText = AfterColon(line);
                }
                if (line.IndexOf(ReclaimableMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.ReclaimableShown = true;
                }
            }
            return recommendedSeen;
        }

        // Text after the first ':' in the line, trimmed ("" when none).
        private static string AfterColon(string line)
        {
            int colon = line.IndexOf(':');
            return colon < 0 ? "" : line.Substring(colon + 1).Trim();
        }

        // Strips DISM's backspace progress artifacts from one output line
        // and trims the padding.
        private static string CleanLine(string raw)
        {
            if (raw == null) return "";
            return raw.Replace("\b", "").Trim();
        }

        // Last OutputTailLines meaningful lines, joined with \r\n (UI tail).
        private static string BuildTail(List<string> meaningful)
        {
            if (meaningful.Count == 0) return "";
            int start = meaningful.Count - OutputTailLines;
            if (start < 0) start = 0;
            StringBuilder tail = new StringBuilder();
            for (int i = start; i < meaningful.Count; i++)
            {
                if (tail.Length > 0) tail.Append("\r\n");
                tail.Append(meaningful[i]);
            }
            return tail.ToString();
        }

        // ---- small helpers ----------------------------------------------------

        // Culture-invariant text for an int in a log line.
        private static string Inv(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        // One-line, length-capped version of raw command output for log lines.
        private static string Summarize(string text)
        {
            string t = (text == null ? "" : text).Replace("\r", " ").Replace("\n", " ").Trim();
            while (t.Contains("  ")) t = t.Replace("  ", " ");
            if (t.Length > 300) t = t.Substring(0, 300) + "...";
            return t.Length == 0 ? "(no output)" : t;
        }
    }
}
