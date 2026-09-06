//  DeepClean.cs  (v1.1.0 - Wave 5, agent A10)
//  ------------------------------------------
//  Deep clean suite: the DELETING counterpart for the categories D9 assigned
//  to this module, plus a report-only visibility category for the Wave 6 UI.
//  Clean() receives the selected CleanCategory list and executes ONLY the
//  categories owned here:
//
//    - Per-user error reports : ReportQueue + ReportArchive under
//          %LOCALAPPDATA%\Microsoft\Windows\WER, items older than 7 days
//          (newer reports are kept). StorageCleaner (A7) already covers the
//          ProgramData WER trees - this is the per-user half.
//    - Setup & upgrade logs   : files older than 30 days in
//          C:\Windows\Logs\MoSetup, C:\Windows\Logs\DISM and
//          C:\Windows\Logs\SIH (folder structure kept, aged files only);
//          plus %SystemRoot% (= C:\Windows) TOP-LEVEL files matching
//          setupapi*.dev.log / setupapi*.log ONLY when named *.log.old or
//          *.old and older than 30 days - the active setupapi.dev.log is
//          never touched (it does not end in ".old"); plus C:\Windows\Panther
//          TOP-LEVEL files only, matching setup*.log/.etl/.xml older than
//          30 days EXCEPT setupact.log / setuperr.log. Panther is sensitive:
//          nothing under a subdirectory is ever touched, and anything not
//          matching the exact selector above is skipped.
//    - Previous Windows installations (report only): C:\Windows.old,
//          C:\$WINDOWS.~BT, C:\$WINDOWS.~WS - MEASURE ONLY. Deletion is NOT
//          implemented anywhere in this file: removing previous installations
//          is Tier 3 and explicitly rejected by design (D3). The category is
//          measured (each present root with its size) and Selected = false so
//          the Wave 6 UI gets visibility without enabling the dangerous
//          action; if Clean() ever receives it selected, it returns a zeroed
//          result with a refusal note and deletes nothing.
//
//  CATEGORY OWNERSHIP SPLIT (D9) - one owner per cleanup target, enforced by
//  every module returning a zeroed "owned by <module>" result for foreign
//  categories it receives:
//      StorageCleaner  (A7)  : Windows Update download cache, Delivery
//                              Optimization cache, Windows temp (>7 days),
//                              user temp, Windows error reports (ProgramData
//                              WER), old update log archives, update
//                              reporting log, crash dumps, thumbnail caches
//      DeepClean       (A10) : per-user error reports, setup & upgrade logs,
//                              previous Windows installations (report only)
//                              - this file
//      AppCacheCleaner (A9)  : browser/app caches (CACHE-ONLY, D5)
//      GpuTools        (A11) : GPU shader caches + driver leftovers
//      ComponentStore  (A8)  : DISM analyze / StartComponentCleanup
//
//  D7 HARD GUARD (belt-and-braces, checked before EVERY deletion - same
//  approach as StorageCleaner's IsForbiddenPath, extended here with the
//  Panther rule this module needs): refuses (case-insensitive,
//  full-path prefix-or-exact):
//      C:\Windows\WinSxS                              (anything inside)
//      C:\Windows\System32\catroot
//      C:\Windows\System32\catroot2
//      C:\Windows\Installer
//      C:\Windows\Servicing
//      any path containing "pending.xml"
//      C:\Windows\SoftwareDistribution                (except Download
//          children and ReportingEvents.log - never touched by this module
//          anyway, the rule is duplicated so the guard fails closed)
//      C:\Windows\Panther                             EXCEPT top-level files
//          matching the aged setup-log selector above (name + top-level
//          enforced by the guard; the 30-day age rule is enforced by the
//          caller's selector before a path is ever proposed).
//  A guard hit logs "GUARD: refusing <path>" and aborts that item - with the
//  fixed category paths above it can only ever fire on a programming error.
//
//  Failure model: Clean() re-checks StorageAnalyzer.CheckGates() itself
//  (never trusts the caller; any reason -> Log.Error per reason, nothing is
//  deleted, return false). Locked/in-use files are skipped with a WARN
//  ("deepclean: skipped <path> (in use)") and tallied; access-denied gets
//  one retry with the read-only attribute cleared. Every category and every
//  item is individually try/caught; Clean() never throws. One [CLEAN] result
//  line per category plus the final
//      deepclean: total X freed; free space before A -> after B (delta D)
//
//  Long paths are enumerated AND deleted through kernel32 FindFirstFileW /
//  DeleteFileW / RemoveDirectoryW with the \\?\ extended-length prefix - the
//  .NET 4.x IO object model rejects that prefix, so the interop from
//  StorageAnalyzer/StorageCleaner is duplicated here file-privately (same
//  structs, same conventions: reparse points are never followed, per-item
//  failures are swallowed). The fixed C:\Windows paths mirror the path
//  tables of StorageAnalyzer.cs / StorageCleaner.cs (target OS: Windows
//  10/11 with %SystemRoot% = C:\Windows).
//
//  Thread safety: the only static mutable state is the run gate - a second
//  concurrent Clean() is refused. Measure() is fully read-only and reentrant.
//
//  Compile verification only this wave - Clean() is never executed (it
//  deletes files). New file for v1.1.0 (Wave 5, A10) per docs\HANDBOOK.md
//  sections 2, 3, 4 (D3/D7/D9) and 8.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // Deep clean suite (static). See the file header for the category
    // ownership split (D9), the guarded targets and the failure model.
    // All state is per-call; the only static field is the run gate.
    // ---------------------------------------------------------------------
    public static class DeepClean
    {
        // ---- exact category names owned by this module (D9) -----------------
        public const string CatPerUserErrorReports = "Per-user error reports";
        public const string CatSetupUpgradeLogs = "Setup & upgrade logs";
        public const string CatPreviousInstallations =
            "Previous Windows installations (report only)";

        // ---- fixed targets (C:\Windows roots mirror the other path tables) --
        private const string MoSetupLogsPath = @"C:\Windows\Logs\MoSetup";
        private const string DismLogsPath = @"C:\Windows\Logs\DISM";
        private const string SihLogsPath = @"C:\Windows\Logs\SIH";
        private const string WindowsRootPath = @"C:\Windows";
        private const string PantherPath = @"C:\Windows\Panther";
        private const string WindowsOldPath = @"C:\Windows.old";
        private const string WindowsBtPath = @"C:\$WINDOWS.~BT";
        private const string WindowsWsPath = @"C:\$WINDOWS.~WS";

        // ---- rules -----------------------------------------------------------
        private const int WerMaxAgeDays = 7;        // per-user WER age rule
        private const int SetupLogMaxAgeDays = 30;  // setup/upgrade log age rule
        private const int MaxErrorSamples = 5;      // verbatim skip/error samples per tally

        // Exact category names owned by StorageCleaner (A7) - used only to
        // render the "owned by" note for foreign categories.
        private static readonly string[] StorageCleanerOwnedNames =
        {
            "Windows Update download cache", "Delivery Optimization cache",
            "Windows temp (>7 days)", "Windows error reports",
            "Old update log archives", "Update reporting log",
            "User temp files", "Crash dumps", "Thumbnail caches"
        };

        // Only one cleanup run at a time per module.
        private static readonly object _runGate = new object();

        // ---- API -------------------------------------------------------------

        // Read-only measurement of this module's three categories (D9). Never
        // throws, writes nothing; one "measure '<name>': ..." CLEAN line per
        // category, same format as StorageAnalyzer.MeasureAll().
        public static List<CleanCategory> Measure()
        {
            List<CleanCategory> results = new List<CleanCategory>();
            MeasurePerUserErrorReports(results);
            MeasureSetupUpgradeLogs(results);
            MeasurePreviousInstallations(results);
            return results;
        }

        // The single entry point: executes the selected DeepClean categories.
        // Returns false ONLY when the safety gates block the run or another
        // DeepClean run is already active; individual category failures are
        // logged into their results and never change the return value.
        // Categories with Selected == false are skipped (the caller owns the
        // final tick decision). Foreign categories come back as zeroed
        // results with an "owned by <module>" note (D9). Never throws.
        public static bool Clean(List<CleanCategory> selected, out List<CleanResult> results,
            out long totalBytesFreed)
        {
            results = new List<CleanResult>();
            totalBytesFreed = 0L;

            if (!Monitor.TryEnter(_runGate))
            {
                Log.Error("deepclean: another deep clean run is already in progress - refusing to start a second one");
                return false;
            }
            try
            {
                try
                {
                    return CleanCore(selected, out results, out totalBytesFreed);
                }
                catch (Exception ex)
                {
                    // Absolutely never throw out of Clean(): a completely
                    // unexpected failure is logged and reported as a blocked
                    // run with nothing freed.
                    Log.Error("deepclean: run aborted by an unexpected error", ex);
                    results = new List<CleanResult>();
                    totalBytesFreed = 0L;
                    return false;
                }
            }
            finally
            {
                Monitor.Exit(_runGate);
            }
        }

        private static bool CleanCore(List<CleanCategory> selected, out List<CleanResult> results,
            out long totalBytesFreed)
        {
            results = new List<CleanResult>();
            totalBytesFreed = 0L;

            // a) gate re-check - never trust the caller
            List<string> reasons = StorageAnalyzer.CheckGates();
            if (reasons != null && reasons.Count > 0)
            {
                foreach (string reason in reasons)
                {
                    Log.Error("deepclean: gate blocks cleanup - " + reason);
                }
                Log.Chan("CLEAN", "deepclean: nothing was deleted (safety gates blocked the run)");
                return false;
            }

            if (selected == null || selected.Count == 0)
            {
                Log.Chan("CLEAN", "deepclean: no categories selected - nothing to do");
                return true;
            }

            // b) free space before (system drive)
            long before = FreeSpaceOfSystemDrive();

            foreach (CleanCategory cat in selected)
            {
                if (cat == null) continue;
                if (!cat.Selected)
                {
                    Log.Chan("CLEAN", "deepclean '" + cat.Name + "': not selected - skipped");
                    continue;
                }
                CleanResult r;
                try
                {
                    r = Dispatch(cat);
                }
                catch (Exception ex)
                {
                    Log.Error("deepclean: category '" + cat.Name + "' failed", ex);
                    r = new CleanResult(cat.Name, 0, 0, 0, "failed: " + ex.Message);
                }
                results.Add(r);
                totalBytesFreed += r.BytesFreed;
                string line = "deepclean '" + cat.Name + "': " + StorageCleaner.FormatBytes(r.BytesFreed) +
                    " freed, " + r.FilesDeleted.ToString("N0", CultureInfo.InvariantCulture) +
                    " files deleted, " + r.FilesSkipped.ToString("N0", CultureInfo.InvariantCulture) +
                    " skipped";
                if (!string.IsNullOrEmpty(r.Notes)) line += " - " + r.Notes;
                Log.Chan("CLEAN", line);
            }

            // c) free space after + final line
            long after = FreeSpaceOfSystemDrive();
            if (before >= 0 && after >= 0)
            {
                long delta = after - before;
                Log.Chan("CLEAN", "deepclean: total " + StorageCleaner.FormatBytes(totalBytesFreed) +
                    " freed; free space before " + StorageCleaner.FormatBytes(before) + " -> after " +
                    StorageCleaner.FormatBytes(after) + " (delta " + (delta >= 0 ? "+" : "-") +
                    StorageCleaner.FormatBytes(Math.Abs(delta)) + ")");
            }
            else
            {
                Log.Chan("CLEAN", "deepclean: total " + StorageCleaner.FormatBytes(totalBytesFreed) +
                    " freed; free space before/after unknown (drive information unavailable)");
            }
            return true;
        }

        // ---- dispatch ----------------------------------------------------------

        // Dispatches one category by its exact name; foreign categories come
        // back with an owner note instead of being forgotten (D9).
        private static CleanResult Dispatch(CleanCategory cat)
        {
            switch (cat.Name)
            {
                case CatPerUserErrorReports: return CleanPerUserErrorReports();
                case CatSetupUpgradeLogs: return CleanSetupUpgradeLogs();
                case CatPreviousInstallations:
                    // Report only by design (D3 Tier 3): no deletion exists
                    // in this file, selected or not.
                    Log.Chan("CLEAN", "deepclean: '" + CatPreviousInstallations +
                        "' is report only - removal rejected by design (D3 Tier 3), nothing was deleted");
                    return new CleanResult(CatPreviousInstallations, 0, 0, 0,
                        "REPORT ONLY - removal of previous Windows installations is rejected by design " +
                        "(D3 Tier 3); DeepClean implements no deletion for this category, nothing was deleted");
                default: return SkipForeignCategory(cat);
            }
        }

        // Zeroed result for categories owned by other Wave 5 modules (D9).
        private static CleanResult SkipForeignCategory(CleanCategory cat)
        {
            if (string.Equals(cat.Kind, CleanCategory.KindGpu, StringComparison.Ordinal))
            {
                return new CleanResult(cat.Name, 0, 0, 0, "owned by GpuTools - not run by DeepClean");
            }
            if (string.Equals(cat.Kind, CleanCategory.KindAppCache, StringComparison.Ordinal))
            {
                return new CleanResult(cat.Name, 0, 0, 0, "owned by AppCacheCleaner - not run by DeepClean");
            }
            if (string.Equals(cat.Kind, CleanCategory.KindDism, StringComparison.Ordinal))
            {
                return new CleanResult(cat.Name, 0, 0, 0, "owned by ComponentStore - not run by DeepClean");
            }
            foreach (string name in StorageCleanerOwnedNames)
            {
                if (string.Equals(cat.Name, name, StringComparison.Ordinal))
                {
                    return new CleanResult(cat.Name, 0, 0, 0, "owned by StorageCleaner - not run by DeepClean");
                }
            }
            return new CleanResult(cat.Name, 0, 0, 0,
                "unknown category - DeepClean has no cleaner for this target, nothing was done");
        }

        // ---- category measurements (each self-contained + try/caught) --------

        // Per-user error reports: ReportQueue + ReportArchive under
        // %LOCALAPPDATA%\Microsoft\Windows\WER, items older than 7 days
        // (old folders with all of their contents). StorageCleaner covers the
        // ProgramData WER trees (D9).
        private static void MeasurePerUserErrorReports(List<CleanCategory> results)
        {
            CleanCategory cat = new CleanCategory(CatPerUserErrorReports, CleanCategory.KindDeepClean,
                "low (per-user queued/sent reports only)");
            try
            {
                string baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Path.Combine("Microsoft", Path.Combine("Windows", "WER")));
                DateTime cutoff = DateTime.Now - TimeSpan.FromDays(WerMaxAgeDays);
                MeasureAgedTopLevel(cat, Path.Combine(baseDir, "ReportQueue"), "ReportQueue", cutoff);
                MeasureAgedTopLevel(cat, Path.Combine(baseDir, "ReportArchive"), "ReportArchive", cutoff);
                cat.Notes = AppendNote(cat.Notes,
                    "items older than " + WerMaxAgeDays + " days under " + baseDir +
                    " (the ProgramData WER trees belong to StorageCleaner)");
            }
            catch (Exception ex)
            {
                cat.Notes = AppendNote(cat.Notes, "measurement failed: " + ex.Message);
            }
            FinishMeasure(results, cat);
        }

        // Setup & upgrade logs: aged files in MoSetup/DISM/SIH, aged
        // setupapi*.old files at the Windows root (never the active
        // setupapi.dev.log) and aged top-level Panther setup logs.
        private static void MeasureSetupUpgradeLogs(List<CleanCategory> results)
        {
            CleanCategory cat = new CleanCategory(CatSetupUpgradeLogs, CleanCategory.KindDeepClean,
                "low (aged setup/DISM/SIH logs only)");
            try
            {
                DateTime cutoff = DateTime.Now - TimeSpan.FromDays(SetupLogMaxAgeDays);
                MeasureAgedFiles(cat, MoSetupLogsPath, "MoSetup", cutoff);
                MeasureAgedFiles(cat, DismLogsPath, "DISM", cutoff);
                MeasureAgedFiles(cat, SihLogsPath, "SIH", cutoff);
                MeasureTopLevelFilesMatching(cat, WindowsRootPath, "Windows root",
                    delegate(string name, ref Win32FindData data)
                    {
                        return IsAgedSetupApiOldLog(name, ref data, cutoff);
                    }, "setupapi*.log.old/*.old logs");
                MeasureTopLevelFilesMatching(cat, PantherPath, "Panther",
                    delegate(string name, ref Win32FindData data)
                    {
                        return IsAgedPantherSetupLog(name, ref data, cutoff);
                    }, "Panther aged setup logs");
                cat.Notes = AppendNote(cat.Notes,
                    "files older than " + SetupLogMaxAgeDays + " days in MoSetup/DISM/SIH; " +
                    "C:\\Windows setupapi*.log.old/*.old only (the active setupapi.dev.log is never touched); " +
                    "Panther top-level setup*.log/.etl/.xml only (setupact.log/setuperr.log and " +
                    "subdirectories are never touched)");
            }
            catch (Exception ex)
            {
                cat.Notes = AppendNote(cat.Notes, "measurement failed: " + ex.Message);
            }
            FinishMeasure(results, cat);
        }

        // Previous Windows installations: MEASURE ONLY (D3 Tier 3 - removal
        // rejected by design). Each present root is noted with its size; the
        // category is always added with Selected = false so the Wave 6 UI
        // shows visibility without enabling the action.
        private static void MeasurePreviousInstallations(List<CleanCategory> results)
        {
            CleanCategory cat = new CleanCategory(CatPreviousInstallations, CleanCategory.KindDeepClean,
                "excluded - removal rejected by design (D3)");
            cat.Selected = false;
            try
            {
                MeasurePreviousInstallationRoot(cat, "Windows.old", WindowsOldPath);
                MeasurePreviousInstallationRoot(cat, "$WINDOWS.~BT", WindowsBtPath);
                MeasurePreviousInstallationRoot(cat, "$WINDOWS.~WS", WindowsWsPath);
                cat.Notes = AppendNote(cat.Notes,
                    "report only - deletion is rejected by design (D3 Tier 3); " +
                    "DeepClean implements no deletion for this category");
            }
            catch (Exception ex)
            {
                cat.Notes = AppendNote(cat.Notes, "measurement failed: " + ex.Message);
            }
            FinishMeasure(results, cat);
        }

        // One previous-installation root (Windows.old / $WINDOWS.~BT /
        // $WINDOWS.~WS): full recursive size + count, noted per root.
        private static void MeasurePreviousInstallationRoot(CleanCategory cat, string label, string root)
        {
            if (!Directory.Exists(root))
            {
                cat.Notes = AppendNote(cat.Notes, label + ": not present");
                return;
            }
            MeasureTally tally = new MeasureTally();
            WalkMeasuredTree(ToLongPath(root), tally, DateTime.MinValue);
            cat.Bytes += tally.Bytes;
            cat.Files += tally.Files;
            string describe = tally.Describe();
            cat.Notes = AppendNote(cat.Notes,
                label + ": " + StorageCleaner.FormatBytes(tally.Bytes) + " in " +
                tally.Files.ToString("N0", CultureInfo.InvariantCulture) + " files" +
                (describe.Length > 0 ? " - " + describe : ""));
        }

        // ---- category cleaners ---------------------------------------------------

        // Per-user WER ReportQueue + ReportArchive: top-level items older
        // than 7 days (old folders with all of their contents), newer
        // reports kept.
        private static CleanResult CleanPerUserErrorReports()
        {
            string baseDir;
            try
            {
                baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Path.Combine("Microsoft", Path.Combine("Windows", "WER")));
            }
            catch (Exception ex)
            {
                return new CleanResult(CatPerUserErrorReports, 0, 0, 0,
                    "could not resolve %LOCALAPPDATA% - " + ex.Message);
            }
            DateTime cutoff = DateTime.Now - TimeSpan.FromDays(WerMaxAgeDays);
            CleanTally tally = new CleanTally();
            string notes = "per-user WER reports older than " + WerMaxAgeDays +
                " days (newer reports are kept; the ProgramData WER trees belong to StorageCleaner)";
            string queueDir = Path.Combine(baseDir, "ReportQueue");
            string archiveDir = Path.Combine(baseDir, "ReportArchive");
            if (!Directory.Exists(queueDir))
            {
                notes = AppendNote(notes, "ReportQueue: not present");
            }
            else
            {
                DeleteAgedChildren(queueDir, cutoff, tally);
            }
            if (!Directory.Exists(archiveDir))
            {
                notes = AppendNote(notes, "ReportArchive: not present");
            }
            else
            {
                DeleteAgedChildren(archiveDir, cutoff, tally);
            }
            return FinishCategory(CatPerUserErrorReports, notes, tally);
        }

        // Setup & upgrade logs: aged files in MoSetup/DISM/SIH (folder
        // structure kept), aged setupapi*.old files at the Windows root
        // (never the active setupapi.dev.log) and aged top-level Panther
        // setup logs (setupact.log/setuperr.log and subdirectories excluded).
        private static CleanResult CleanSetupUpgradeLogs()
        {
            DateTime cutoff = DateTime.Now - TimeSpan.FromDays(SetupLogMaxAgeDays);
            CleanTally tally = new CleanTally();
            string notes = "setup/upgrade logs older than " + SetupLogMaxAgeDays + " days";

            if (!Directory.Exists(MoSetupLogsPath))
            {
                notes = AppendNote(notes, "MoSetup: not present");
            }
            else
            {
                WalkAgedFiles(MoSetupLogsPath, cutoff, tally);
            }
            if (!Directory.Exists(DismLogsPath))
            {
                notes = AppendNote(notes, "DISM: not present");
            }
            else
            {
                WalkAgedFiles(DismLogsPath, cutoff, tally);
            }
            if (!Directory.Exists(SihLogsPath))
            {
                notes = AppendNote(notes, "SIH: not present");
            }
            else
            {
                WalkAgedFiles(SihLogsPath, cutoff, tally);
            }

            if (!Directory.Exists(WindowsRootPath))
            {
                notes = AppendNote(notes, "Windows root: not present");
            }
            else
            {
                DeleteTopLevelFilesMatching(WindowsRootPath,
                    delegate(string name, ref Win32FindData data)
                    {
                        return IsAgedSetupApiOldLog(name, ref data, cutoff);
                    }, tally);
            }

            if (!Directory.Exists(PantherPath))
            {
                notes = AppendNote(notes, "Panther: not present");
            }
            else
            {
                DeleteTopLevelFilesMatching(PantherPath,
                    delegate(string name, ref Win32FindData data)
                    {
                        return IsAgedPantherSetupLog(name, ref data, cutoff);
                    }, tally);
            }

            notes = AppendNote(notes,
                "setupapi*.log.old/*.old only - the active setupapi.dev.log is never touched; " +
                "Panther: top-level setup*.log/.etl/.xml only (setupact.log/setuperr.log and " +
                "subdirectories excluded); folder structure kept");
            return FinishCategory(CatSetupUpgradeLogs, notes, tally);
        }

        // ---- selectors ------------------------------------------------------------

        // C:\Windows top-level setupapi*.log.old / setupapi*.old files older
        // than the cutoff. The active setupapi.dev.log never ends in ".old"
        // and is therefore never selected.
        private static bool IsAgedSetupApiOldLog(string name, ref Win32FindData data, DateTime cutoff)
        {
            if ((data.FileAttributes & FileAttributeDirectory) != 0) return false;
            if (!name.StartsWith("setupapi", StringComparison.OrdinalIgnoreCase)) return false;
            if (!name.EndsWith(".old", StringComparison.OrdinalIgnoreCase)) return false;
            return FileTimeToDateTime(data.LastWriteTime) < cutoff;
        }

        // C:\Windows\Panther TOP-LEVEL setup*.log/.etl/.xml files older than
        // the cutoff, except setupact.log and setuperr.log. Panther is
        // sensitive: when in doubt, skip (the D7 guard double-checks name +
        // top-level at deletion time too).
        private static bool IsAgedPantherSetupLog(string name, ref Win32FindData data, DateTime cutoff)
        {
            if ((data.FileAttributes & FileAttributeDirectory) != 0) return false;
            if (!name.StartsWith("setup", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(name, "setupact.log", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(name, "setuperr.log", StringComparison.OrdinalIgnoreCase)) return false;
            if (!name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".etl", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return FileTimeToDateTime(data.LastWriteTime) < cutoff;
        }

        // ---- deletion walkers (guard-checked, long-path safe) --------------------------

        // Deletes whole selected subtrees under rootDir: every top-level
        // entry older than topLevelCutoff is deleted recursively; newer
        // entries are left untouched. rootDir itself is NEVER deleted
        // (children only).
        private static void DeleteAgedChildren(string rootDir, DateTime topLevelCutoff, CleanTally tally)
        {
            if (IsForbiddenPath(rootDir))
            {
                Log.Error("GUARD: refusing " + DisplayPath(rootDir));
                tally.Refused++;
                return;
            }
            ForEachEntry(rootDir, delegate(string dir, string name, ref Win32FindData data)
            {
                bool isDir = (data.FileAttributes & FileAttributeDirectory) != 0;
                if (isDir && (data.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    tally.ReparseSkipped++;     // junctions/symlinks are never followed
                    return;
                }
                if (topLevelCutoff != DateTime.MinValue &&
                    FileTimeToDateTime(data.LastWriteTime) >= topLevelCutoff)
                {
                    return;                     // too recent - kept (not a skip)
                }
                DeleteItem(dir, name, ref data, isDir, tally);
            });
        }

        // Recursively deletes FILES older than the cutoff (directories are
        // never deleted - the log folders keep their structure, only aged
        // files go). Reparse points are never followed.
        private static void WalkAgedFiles(string dirPath, DateTime cutoff, CleanTally tally)
        {
            ForEachEntry(dirPath, delegate(string dir, string name, ref Win32FindData data)
            {
                if ((data.FileAttributes & FileAttributeDirectory) != 0)
                {
                    if ((data.FileAttributes & FileAttributeReparsePoint) != 0)
                    {
                        tally.ReparseSkipped++;     // never follow junctions/symlinks
                        return;
                    }
                    string sub = dir + "\\" + name;
                    if (IsForbiddenPath(sub))
                    {
                        Log.Error("GUARD: refusing " + DisplayPath(sub));
                        tally.Refused++;
                        return;
                    }
                    WalkAgedFiles(sub, cutoff, tally);
                    return;
                }
                if (FileTimeToDateTime(data.LastWriteTime) >= cutoff) return;
                string path = dir + "\\" + name;
                if (IsForbiddenPath(path))
                {
                    Log.Error("GUARD: refusing " + DisplayPath(path));
                    tally.Refused++;
                    return;
                }
                DeleteFileEntry(path, FileSizeOf(data), tally);
            });
        }

        // Deletes TOP-LEVEL FILES of rootDir matching the selector (never
        // descends into subdirectories - used for the Windows root and for
        // Panther). rootDir itself is never deleted.
        private static void DeleteTopLevelFilesMatching(string rootDir, FileSelector selector,
            CleanTally tally)
        {
            if (IsForbiddenPath(rootDir))
            {
                Log.Error("GUARD: refusing " + DisplayPath(rootDir));
                tally.Refused++;
                return;
            }
            ForEachEntry(rootDir, delegate(string dir, string name, ref Win32FindData data)
            {
                if ((data.FileAttributes & FileAttributeDirectory) != 0) return;
                if (!selector(name, ref data)) return;
                string path = dir + "\\" + name;
                if (IsForbiddenPath(path))
                {
                    Log.Error("GUARD: refusing " + DisplayPath(path));
                    tally.Refused++;
                    return;
                }
                DeleteFileEntry(path, FileSizeOf(data), tally);
            });
        }

        // Guard + delete of one entry (file or directory) with per-item
        // exception containment: a failure is logged and tallied, never
        // thrown to the walk.
        private static void DeleteItem(string dir, string name, ref Win32FindData data, bool isDir,
            CleanTally tally)
        {
            string path = dir + "\\" + name;
            if (IsForbiddenPath(path))
            {
                Log.Error("GUARD: refusing " + DisplayPath(path));
                tally.Refused++;
                return;
            }
            try
            {
                if (isDir) DeleteDirectoryEntry(path, tally);
                else DeleteFileEntry(path, FileSizeOf(data), tally);
            }
            catch (Exception ex)
            {
                if (isDir) tally.FoldersLeft++; else tally.Skipped++;
                tally.AddSample(path, ex.Message);
                Log.Warn("deepclean: skipped " + DisplayPath(path) + " (" + ex.Message + ")");
            }
        }

        // Removes a directory AFTER its contents: contents first (per-item
        // guard + tally), then the directory itself. A directory that stays
        // behind because contents were locked is counted in FoldersLeft
        // (no extra warn - the file-level warns already explain why); other
        // removal failures get a WARN. Access-denied gets one retry with
        // the read-only attribute cleared.
        private static void DeleteDirectoryEntry(string dirPath, CleanTally tally)
        {
            DeleteDirectoryContents(dirPath, tally);
            if (RemoveDirectoryW(ToLongPath(dirPath)))
            {
                tally.FoldersDeleted++;
                return;
            }
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorFileNotFound || error == ErrorPathNotFound) return;
            if (error == ErrorAccessDenied && TryClearReadOnly(dirPath))
            {
                if (RemoveDirectoryW(ToLongPath(dirPath)))
                {
                    tally.FoldersDeleted++;
                    return;
                }
                error = Marshal.GetLastWin32Error();
                if (error == ErrorFileNotFound || error == ErrorPathNotFound) return;
            }
            tally.FoldersLeft++;
            if (error != ErrorDirNotEmpty)
            {
                Log.Warn("deepclean: could not remove folder " + DisplayPath(dirPath) +
                    " (" + Win32Message(error) + ")");
            }
        }

        private static void DeleteDirectoryContents(string dirPath, CleanTally tally)
        {
            if (IsForbiddenPath(dirPath))
            {
                Log.Error("GUARD: refusing " + DisplayPath(dirPath));
                tally.Refused++;
                return;
            }
            ForEachEntry(dirPath, delegate(string dir, string name, ref Win32FindData data)
            {
                bool isDir = (data.FileAttributes & FileAttributeDirectory) != 0;
                if (isDir && (data.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    // junction/symlink inside a purged tree: remove the link
                    // itself, never descend into its target
                    if (RemoveDirectoryW(ToLongPath(dir + "\\" + name))) tally.FoldersDeleted++;
                    else tally.FoldersLeft++;
                    return;
                }
                DeleteItem(dir, name, ref data, isDir, tally);
            });
        }

        // Deletes one file and tallies the outcome. Files gone in the
        // meantime are silent; in-use files are WARNed and tallied as
        // skipped; access-denied gets one retry with the read-only
        // attribute cleared.
        private static void DeleteFileEntry(string filePath, long size, CleanTally tally)
        {
            if (DeleteFileW(ToLongPath(filePath)))
            {
                tally.Bytes += size;
                tally.Files++;
                return;
            }
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorFileNotFound || error == ErrorPathNotFound) return;   // already gone
            if (error == ErrorAccessDenied && TryClearReadOnly(filePath))
            {
                if (DeleteFileW(ToLongPath(filePath)))
                {
                    tally.Bytes += size;
                    tally.Files++;
                    return;
                }
                error = Marshal.GetLastWin32Error();
                if (error == ErrorFileNotFound || error == ErrorPathNotFound) return;
            }
            tally.Skipped++;
            string reason = IsInUseError(error) ? "in use" : Win32Message(error);
            tally.AddSample(filePath, reason);
            Log.Warn("deepclean: skipped " + DisplayPath(filePath) + " (" + reason + ")");
        }

        private static bool TryClearReadOnly(string filePath)
        {
            try
            {
                return SetFileAttributesW(ToLongPath(filePath), FileAttributeNormal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- D7 hard guard ---------------------------------------------------------

        // The D7 hard guard, checked before EVERY deletion (see the file
        // header for the list - StorageCleaner's set plus the Panther rule
        // this module needs). Blank paths fail closed. With the fixed
        // category paths above this can only ever fire on a programming
        // error.
        private static bool IsForbiddenPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;    // blank fails closed
            string p = path.Trim();
            if (p.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) p = p.Substring(4);
            p = p.Replace('/', '\\').ToLowerInvariant();

            // Anything containing pending.xml (D7 never-touch list).
            if (p.Contains("pending.xml")) return true;

            if (IsUnderOrEqual(p, @"c:\windows\winsxs")) return true;
            if (IsUnderOrEqual(p, @"c:\windows\system32\catroot")) return true;
            if (IsUnderOrEqual(p, @"c:\windows\system32\catroot2")) return true;
            if (IsUnderOrEqual(p, @"c:\windows\installer")) return true;
            if (IsUnderOrEqual(p, @"c:\windows\servicing")) return true;

            if (IsUnderOrEqual(p, @"c:\windows\softwaredistribution"))
            {
                // Only Download children and ReportingEvents.log would ever
                // be allowed under SoftwareDistribution (DataStore and the
                // rest are off-limits). This module never goes there - the
                // rule is duplicated so the guard fails closed.
                bool downloadChild = IsUnderOrEqual(p, @"c:\windows\softwaredistribution\download");
                bool reportingLog = string.Equals(p, @"c:\windows\softwaredistribution\reportingevents.log",
                    StringComparison.Ordinal);
                if (!downloadChild && !reportingLog) return true;
            }

            // Panther is sensitive: everything under it is refused EXCEPT
            // top-level files matching the aged setup-log selector (the name
            // + top-level rules live in the guard; the 30-day age rule is
            // enforced by the caller's selector before a path is proposed).
            if (IsUnderOrEqual(p, @"c:\windows\panther"))
            {
                return !IsPantherSetupLogPath(p);
            }
            return false;
        }

        // True for exactly the Panther paths the guard allows through:
        // top-level files named setup*.log/.etl/.xml (with setupact.log and
        // setuperr.log always excluded). Subdirectories are never allowed.
        private static bool IsPantherSetupLogPath(string p)
        {
            const string pantherRoot = @"c:\windows\panther\";
            if (!p.StartsWith(pantherRoot, StringComparison.Ordinal)) return false;
            string name = p.Substring(pantherRoot.Length);
            if (name.Length == 0 || name.IndexOf('\\') >= 0) return false;  // subdirectories: never
            if (string.Equals(name, "setupact.log", StringComparison.Ordinal)) return false;
            if (string.Equals(name, "setuperr.log", StringComparison.Ordinal)) return false;
            if (!name.StartsWith("setup", StringComparison.Ordinal)) return false;
            return name.EndsWith(".log", StringComparison.Ordinal) ||
                   name.EndsWith(".etl", StringComparison.Ordinal) ||
                   name.EndsWith(".xml", StringComparison.Ordinal);
        }

        // True when p equals prefix or lies inside it (boundary-aware: the
        // next character after the prefix must be end-of-string or '\').
        private static bool IsUnderOrEqual(string p, string prefix)
        {
            if (!p.StartsWith(prefix, StringComparison.Ordinal)) return false;
            return p.Length == prefix.Length || p[prefix.Length] == '\\';
        }

        // ---- measurement walkers (read-only, long-path safe) ----------------------

        // Measures a whole tree into the category; when cutoff is set, only
        // TOP-LEVEL entries older than the cutoff are counted (old folders
        // with all of their contents), deeper levels are never age-filtered.
        private static void MeasureAgedTopLevel(CleanCategory cat, string root, string missingLabel,
            DateTime cutoff)
        {
            if (!Directory.Exists(root))
            {
                cat.Notes = AppendNote(cat.Notes, missingLabel + ": not present");
                return;
            }
            MeasureTally tally = new MeasureTally();
            WalkMeasuredTree(ToLongPath(root), tally, cutoff);
            MergeMeasureTally(cat, tally);
        }

        // Measures all files older than the cutoff anywhere under root
        // (folder structure kept, same rule the cleaner applies).
        private static void MeasureAgedFiles(CleanCategory cat, string root, string missingLabel,
            DateTime cutoff)
        {
            if (!Directory.Exists(root))
            {
                cat.Notes = AppendNote(cat.Notes, missingLabel + ": not present");
                return;
            }
            MeasureTally tally = new MeasureTally();
            WalkAgedFilesMeasure(ToLongPath(root), tally, cutoff);
            MergeMeasureTally(cat, tally);
        }

        // Measures the top-level files of root matching the selector (never
        // descends); a "none found" note distinguishes present-but-empty
        // from not present.
        private static void MeasureTopLevelFilesMatching(CleanCategory cat, string root,
            string missingLabel, FileSelector selector, string zeroLabel)
        {
            if (!Directory.Exists(root))
            {
                cat.Notes = AppendNote(cat.Notes, missingLabel + ": not present");
                return;
            }
            MeasureTally tally = new MeasureTally();
            ForEachEntryMeasure(ToLongPath(root), tally,
                delegate(string dir, string name, ref Win32FindData data)
                {
                    if ((data.FileAttributes & FileAttributeDirectory) != 0) return;
                    if (!selector(name, ref data)) return;
                    tally.Bytes += FileSizeOf(data);
                    tally.Files++;
                });
            MergeMeasureTally(cat, tally);
            if (tally.Files == 0 && tally.Errors == 0)
            {
                cat.Notes = AppendNote(cat.Notes, zeroLabel + ": none found");
            }
        }

        // Recursive read-only size/count walk; topLevelCutoff filters only
        // the top level (DateTime.MinValue = no age filter). Same
        // conventions as the deletion walks: reparse points skipped,
        // per-item failures swallowed into the tally.
        private static void WalkMeasuredTree(string dirPath, MeasureTally tally, DateTime topLevelCutoff)
        {
            ForEachEntryMeasure(dirPath, tally, delegate(string dir, string name, ref Win32FindData data)
            {
                if (topLevelCutoff != DateTime.MinValue &&
                    FileTimeToDateTime(data.LastWriteTime) >= topLevelCutoff)
                {
                    return;                     // top-level entry is too recent
                }
                if ((data.FileAttributes & FileAttributeDirectory) == 0)
                {
                    tally.Bytes += FileSizeOf(data);
                    tally.Files++;
                    return;
                }
                if ((data.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    tally.ReparseSkipped++;
                    return;
                }
                WalkMeasuredTree(dir + "\\" + name, tally, DateTime.MinValue);
            });
        }

        private static void WalkAgedFilesMeasure(string dirPath, MeasureTally tally, DateTime cutoff)
        {
            ForEachEntryMeasure(dirPath, tally, delegate(string dir, string name, ref Win32FindData data)
            {
                if ((data.FileAttributes & FileAttributeDirectory) != 0)
                {
                    if ((data.FileAttributes & FileAttributeReparsePoint) != 0)
                    {
                        tally.ReparseSkipped++;
                        return;
                    }
                    WalkAgedFilesMeasure(dir + "\\" + name, tally, cutoff);
                    return;
                }
                if (FileTimeToDateTime(data.LastWriteTime) < cutoff)
                {
                    tally.Bytes += FileSizeOf(data);
                    tally.Files++;
                }
            });
        }

        // ---- result / note helpers -------------------------------------------------

        // Adds the category to the results and writes its single CLEAN line:
        // measure '<Name>': <SizeText> in <N> files [- <Notes>].
        private static void FinishMeasure(List<CleanCategory> results, CleanCategory cat)
        {
            results.Add(cat);
            string line = "measure '" + cat.Name + "': " + cat.SizeText + " in " +
                cat.Files.ToString("N0", CultureInfo.InvariantCulture) + " files";
            if (!string.IsNullOrEmpty(cat.Notes))
            {
                line += " - " + cat.Notes;
            }
            Log.Chan("CLEAN", line);
        }

        // Moves a clean tally into the CleanResult for one category.
        private static CleanResult FinishCategory(string categoryName, string notes, CleanTally tally)
        {
            CleanResult result = new CleanResult(categoryName, tally.Bytes, tally.Files, tally.Skipped, notes);
            result.Notes = AppendNote(notes, tally.Describe());
            return result;
        }

        private static void MergeMeasureTally(CleanCategory cat, MeasureTally tally)
        {
            cat.Bytes += tally.Bytes;
            cat.Files += tally.Files;
            cat.Notes = AppendNote(cat.Notes, tally.Describe());
        }

        private static string AppendNote(string existing, string addition)
        {
            if (string.IsNullOrEmpty(addition)) return existing == null ? "" : existing;
            if (string.IsNullOrEmpty(existing)) return addition;
            return existing + "; " + addition;
        }

        // ---- drive helper ------------------------------------------------------------

        // Available free space of the system drive (-1 when unavailable).
        private static long FreeSpaceOfSystemDrive()
        {
            try
            {
                string root = Path.GetPathRoot(Environment.SystemDirectory);
                if (string.IsNullOrEmpty(root)) return -1;
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        // ---- directory enumeration (kernel32, long-path safe) ---------------------------

        private delegate void EntryVisitor(string dirPath, string name, ref Win32FindData data);

        private delegate bool FileSelector(string name, ref Win32FindData data);

        // Clean-side enumerator: one directory level through FindFirstFileW
        // (which takes \\?\ paths natively), visiting every entry except
        // "."/"..". A directory that cannot be opened logs one WARN and
        // yields nothing; the find handle is always closed.
        private static void ForEachEntry(string dirPath, EntryVisitor visit)
        {
            string longDir = ToLongPath(dirPath);
            Win32FindData data;
            IntPtr handle = FindFirstFileW(longDir + "\\*", out data);
            if (handle == InvalidFindHandle)
            {
                Log.Warn("deepclean: could not enumerate " + DisplayPath(dirPath) + " (" +
                    Win32Message(Marshal.GetLastWin32Error()) + ")");
                return;
            }
            try
            {
                bool more = true;
                while (more)
                {
                    string name = data.FileName;
                    if (!string.Equals(name, ".") && !string.Equals(name, ".."))
                    {
                        visit(longDir, name, ref data);
                    }
                    more = FindNextFileW(handle, out data);
                }
            }
            finally
            {
                FindClose(handle);
            }
        }

        // Measure-side enumerator: same walk, but an unopenable directory is
        // swallowed into the tally (measure must stay quiet except for its
        // one line per category).
        private static void ForEachEntryMeasure(string dirPath, MeasureTally tally, EntryVisitor visit)
        {
            Win32FindData data;
            IntPtr handle = FindFirstFileW(dirPath + "\\*", out data);
            if (handle == InvalidFindHandle)
            {
                tally.AddError(dirPath + ": not enumerated",
                    new Win32Exception(Marshal.GetLastWin32Error()));
                return;
            }
            try
            {
                bool more = true;
                while (more)
                {
                    string name = data.FileName;
                    if (!string.Equals(name, ".") && !string.Equals(name, ".."))
                    {
                        visit(dirPath, name, ref data);
                    }
                    more = FindNextFileW(handle, out data);
                }
            }
            finally
            {
                FindClose(handle);
            }
        }

        // ---- small shared helpers ---------------------------------------------------------

        private static long FileSizeOf(Win32FindData data)
        {
            return ((long)data.FileSizeHigh << 32) | (long)data.FileSizeLow;
        }

        // FILETIME -> local DateTime (same convention as FileInfo.LastWriteTime).
        private static DateTime FileTimeToDateTime(Win32FileTime value)
        {
            long fileTime = ((long)value.High << 32) | (long)value.Low;
            if (fileTime <= 0) return DateTime.MinValue;
            try
            {
                return DateTime.FromFileTime(fileTime);
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTime.MinValue;
            }
        }

        // Returns the path with the \\?\ extended-length prefix so that
        // enumeration and deletion tolerate paths beyond MAX_PATH. Only
        // canonical local drive paths (C:\...) are prefixed; anything else
        // is returned unchanged.
        private static string ToLongPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return path;
            }
            if (path.Length > 3 && path[1] == ':' && path[2] == '\\' && path.IndexOf('/') < 0)
            {
                return @"\\?\" + path;
            }
            return path;
        }

        // Strips the \\?\ extended-length prefix for display in notes/warns.
        private static string DisplayPath(string path)
        {
            if (path != null && path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return path.Substring(4);
            }
            return path;
        }

        private static bool IsInUseError(int error)
        {
            return error == ErrorSharingViolation || error == ErrorLockViolation;
        }

        private static string Win32Message(int error)
        {
            try
            {
                return new Win32Exception(error).Message;
            }
            catch (Exception)
            {
                return "win32 error " + error.ToString(CultureInfo.InvariantCulture);
            }
        }

        // ---- win32 interop (kernel32; same pattern as StorageAnalyzer.cs) ------------------

        private const int ErrorAccessDenied = 5;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorSharingViolation = 32;
        private const int ErrorLockViolation = 33;
        private const int ErrorDirNotEmpty = 145;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeReparsePoint = 0x00000400;
        private const uint FileAttributeNormal = 0x00000080;
        private static readonly IntPtr InvalidFindHandle = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32FileTime
        {
            public uint Low;
            public uint High;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Win32FindData
        {
            public uint FileAttributes;
            public Win32FileTime CreationTime;
            public Win32FileTime LastAccessTime;
            public Win32FileTime LastWriteTime;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint Reserved0;
            public uint Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string AlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string fileName, out Win32FindData findData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(IntPtr findHandle, out Win32FindData findData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr findHandle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileW(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool RemoveDirectoryW(string pathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetFileAttributesW(string fileName, uint fileAttributes);

        // ---- measure tally ------------------------------------------------------------------

        // Running totals for one measurement walk. Per-item failures are
        // swallowed here and summarized (at most MaxErrorSamples verbatim
        // samples, then "and N more") - same convention as StorageAnalyzer.
        private sealed class MeasureTally
        {
            public long Bytes;
            public int Files;
            public int Errors;
            public int ReparseSkipped;
            public List<string> ErrorSamples = new List<string>();

            public void AddError(string what, Exception ex)
            {
                Errors++;
                if (ErrorSamples.Count >= MaxErrorSamples) return;
                string msg = ex == null ? "unknown error" : ex.Message;
                if (msg.Length > 120) msg = msg.Substring(0, 117) + "...";
                ErrorSamples.Add(DisplayPath(what) + " (" + msg + ")");
            }

            // "" when the walk was clean, otherwise e.g.
            // "3 item(s) not counted - <samples>[; and N more]; 1 junction/symlink folder(s) skipped".
            public string Describe()
            {
                List<string> parts = new List<string>();
                if (Errors > 0)
                {
                    string detail = string.Join("; ", ErrorSamples.ToArray());
                    if (Errors > ErrorSamples.Count)
                    {
                        detail += "; and " + (Errors - ErrorSamples.Count) + " more";
                    }
                    parts.Add(Errors + " item(s) not counted - " + detail);
                }
                if (ReparseSkipped > 0)
                {
                    parts.Add(ReparseSkipped + " junction/symlink folder(s) skipped");
                }
                return string.Join("; ", parts.ToArray());
            }
        }

        // ---- clean tally ----------------------------------------------------------------------

        // Running totals for one clean pass. Per-item failures land in
        // capped verbatim samples (MaxErrorSamples) and are summarized by
        // Describe() into the CleanResult.Notes - same convention as
        // StorageCleaner.
        private sealed class CleanTally
        {
            public long Bytes;
            public int Files;
            public int Skipped;
            public int FoldersDeleted;
            public int FoldersLeft;
            public int Refused;
            public int ReparseSkipped;
            public List<string> Samples = new List<string>();

            public void AddSample(string path, string reason)
            {
                if (Samples.Count >= MaxErrorSamples) return;
                string msg = reason == null ? "unknown error" : reason;
                if (msg.Length > 120) msg = msg.Substring(0, 117) + "...";
                Samples.Add(DisplayPath(path) + " (" + msg + ")");
            }

            // "" when nothing notable happened, otherwise e.g.
            // "3 file(s) skipped - <samples>[; and N more]; 1 folder(s) could not be removed".
            public string Describe()
            {
                List<string> parts = new List<string>();
                if (Skipped > 0)
                {
                    string detail = "";
                    if (Samples.Count > 0)
                    {
                        detail = " - " + string.Join("; ", Samples.ToArray());
                        if (Skipped > Samples.Count)
                        {
                            detail += "; and " + (Skipped - Samples.Count) + " more";
                        }
                    }
                    parts.Add(Skipped.ToString("N0", CultureInfo.InvariantCulture) + " file(s) skipped" + detail);
                }
                if (FoldersLeft > 0)
                {
                    parts.Add(FoldersLeft.ToString("N0", CultureInfo.InvariantCulture) +
                        " folder(s) could not be removed (contents locked)");
                }
                if (Refused > 0)
                {
                    parts.Add(Refused.ToString("N0", CultureInfo.InvariantCulture) +
                        " item(s) REFUSED by the D7 guard");
                }
                if (ReparseSkipped > 0)
                {
                    parts.Add(ReparseSkipped.ToString("N0", CultureInfo.InvariantCulture) +
                        " junction/symlink folder(s) skipped");
                }
                return string.Join("; ", parts.ToArray());
            }
        }
    }
}
