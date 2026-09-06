//  StorageCleaner.cs  (v1.1.0 - Wave 5, agent A7)
//  ----------------------------------------------
//  Tier 1 storage cleaner (D3): the DELETING counterpart to the read-only
//  StorageAnalyzer (A6). Clean() receives the selected CleanCategory list
//  (from StorageAnalyzer.MeasureAll()), re-checks the D7 safety gates itself
//  (never trusts the caller) and executes only the Tier 1 safe cache purge:
//
//    - Windows Update download cache : stop usosvc -> wuauserv -> bits
//          (bounded 20 s waits, already-stopped tolerated), delete the
//          CHILDREN of C:\Windows\SoftwareDistribution\Download (never the
//          folder itself), restart bits -> wuauserv -> usosvc (only services
//          this process stopped), then trigger WU re-detection through the
//          Microsoft.Update.AutoUpdate COM ProgID via reflection
//          (Type.GetTypeFromProgID + InvokeMember - no `dynamic`, so
//          build.cmd never needs Microsoft.CSharp.dll). A wuauserv that
//          refuses to stop aborts ONLY this category with an error result;
//          the services we stopped are restarted and the other categories
//          still run.
//    - Delivery Optimization cache   : powershell.exe
//          "Delete-DeliveryOptimizationCache -Force" (NEVER with
//          -IncludePinnedFiles), 120 s timeout with kill. The cmdlet does
//          not report byte counts, so the cache is measured immediately
//          before the run and that size is reported as a freed ESTIMATE.
//    - Windows temp (>7 days)        : top-level items older than 7 days
//          (old folders with all of their contents).
//    - User temp files               : everything unlocked under %TEMP%.
//    - Windows error reports         : ReportQueue/ReportArchive items
//          older than 7 days (newer reports are kept).
//    - Old update log archives       : CbsPersist_*.cab older than 30 days
//          plus WindowsUpdate log files older than 30 days.
//    - Update reporting log          : ReportingEvents.log (auto-recreated;
//          deleted during the WU purge pass while wuauserv is stopped when
//          that category is selected too, otherwise standalone with
//          skip+warn when locked).
//    - Crash dumps                   : MEMORY.DMP + Minidump\*.
//    - Thumbnail caches              : thumbcache_/iconcache_ files - most
//          are Explorer-locked, skip+log is expected and fine.
//    - Kind "gpu"/"appcache"/"dism" categories are NOT this module's: they
//          come back as zero-byte results with a note pointing at GpuTools
//          / AppCacheCleaner / ComponentStore (Wave 5) so the caller sees
//          they were not forgotten.
//
//  D7 HARD GUARD (belt-and-braces; stated here per the class contract):
//  every computed deletion target passes IsForbiddenPath() first - it
//  refuses (case-insensitive, full-path prefix-or-exact):
//      C:\Windows\WinSxS                              (anything inside)
//      C:\Windows\System32\catroot
//      C:\Windows\System32\catroot2
//      C:\Windows\Installer
//      C:\Windows\Servicing
//      any path containing "pending.xml"
//      C:\Windows\SoftwareDistribution\DataStore
//          (under SoftwareDistribution ONLY Download children and
//           ReportingEvents.log are ever allowed through)
//  A guard hit logs "GUARD: refusing <path>" and aborts that item - it can
//  only ever fire on a programming error, never on the fixed paths above.
//
//  Failure model: the gates fail closed (any reason -> nothing is deleted
//  and Clean returns false); every category and every item is individually
//  try/caught; locked/in-use files are skipped with a WARN
//  ("clean: skipped <path> (in use)") and tallied in CleanResult.FilesSkipped;
//  access-denied gets one retry with the read-only attribute cleared (temp
//  folders often hold read-only leftovers). Everything is logged through the
//  CLEAN channel, one summary line per category plus the final total line.
//
//  Long paths (files deep under SoftwareDistribution exceed MAX_PATH) are
//  enumerated AND deleted through kernel32 FindFirstFileW/DeleteFileW/
//  RemoveDirectoryW with the \\?\ extended-length prefix - .NET 4.x rejects
//  that prefix in its IO object model, so the interop from StorageAnalyzer
//  is duplicated here file-privately (same structs, same conventions:
//  reparse points are never followed, per-item failures are swallowed).
//
//  Thread safety: no mutable static state and safe to call from a background
//  thread (Wave 6 runs cleanup via RunBg); a static run gate refuses a
//  second concurrent Clean() because the WU service stop/start dance must
//  never run twice at once.
//
//  Compile verification only this wave - Clean() is never executed here (it
//  deletes files). New file for v1.1.0 (Wave 5, A7) per docs\HANDBOOK.md
//  sections 2, 3, 4 (D3/D7) and 8.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // Result of cleaning one category (the deleting counterpart of
    // CleanCategory). Plain data holder for the Wave 6 UI and the session
    // history; Summary renders the human-readable one-liner.
    // ---------------------------------------------------------------------
    public class CleanResult
    {
        public string CategoryName;
        public long BytesFreed;
        public int FilesDeleted;
        public int FilesSkipped;
        public string Notes;

        public CleanResult()
        {
            CategoryName = "";
            BytesFreed = 0;
            FilesDeleted = 0;
            FilesSkipped = 0;
            Notes = "";
        }

        public CleanResult(string categoryName, long bytesFreed, int filesDeleted, int filesSkipped, string notes)
            : this()
        {
            CategoryName = categoryName;
            BytesFreed = bytesFreed;
            FilesDeleted = filesDeleted;
            FilesSkipped = filesSkipped;
            Notes = notes == null ? "" : notes;
        }

        // Human-readable one-liner, e.g. "1.20 GB freed, 567 files deleted, 3 skipped".
        public string Summary
        {
            get
            {
                return StorageCleaner.FormatBytes(BytesFreed) + " freed, " +
                    FilesDeleted.ToString("N0", CultureInfo.InvariantCulture) + " files deleted, " +
                    FilesSkipped.ToString("N0", CultureInfo.InvariantCulture) + " skipped";
            }
        }
    }

    // ---------------------------------------------------------------------
    // Tier 1 storage cleaner (static). See the file header for the flows,
    // the D7 hard guard list and the failure model. All state is per-call;
    // the only static field is the run gate.
    // ---------------------------------------------------------------------
    public static class StorageCleaner
    {
        // ---- exact category names produced by StorageAnalyzer.MeasureAll() ----
        private const string CatWuDownload = "Windows Update download cache";
        private const string CatDeliveryOptimization = "Delivery Optimization cache";
        private const string CatWindowsTemp = "Windows temp (>7 days)";
        private const string CatErrorReports = "Windows error reports";
        private const string CatUpdateLogArchives = "Old update log archives";
        private const string CatReportingEvents = "Update reporting log";
        private const string CatUserTemp = "User temp files";
        private const string CatCrashDumps = "Crash dumps";
        private const string CatThumbnailCaches = "Thumbnail caches";

        // ---- fixed targets (mirror of the analyzer's path table) ---------------
        private const string WuDownloadPath =
            @"C:\Windows\SoftwareDistribution\Download";
        private const string DeliveryOptimizationPath =
            @"C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache";
        private const string WindowsTempPath = @"C:\Windows\Temp";
        private const string WerQueuePath = @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue";
        private const string WerArchivePath = @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive";
        private const string CbsLogsPath = @"C:\Windows\Logs\CBS";
        private const string WuLogsPath = @"C:\Windows\Logs\WindowsUpdate";
        private const string ReportingEventsPath = @"C:\Windows\SoftwareDistribution\ReportingEvents.log";
        private const string MemoryDumpPath = @"C:\Windows\MEMORY.DMP";
        private const string MinidumpPath = @"C:\Windows\Minidump";

        // ---- services ----------------------------------------------------------
        private const string ServiceUsoSvc = "UsoSvc";
        private const string ServiceWuauserv = "wuauserv";
        private const string ServiceBits = "bits";

        // ---- rules -------------------------------------------------------------
        private const int TempMaxAgeDays = 7;         // Windows temp + WER age rule
        private const int LogArchiveMaxAgeDays = 30;  // CBS cabs + WindowsUpdate logs
        private const int StopServiceTimeoutSeconds = 20;
        private const int PowerShellTimeoutMs = 120000;
        private const int MaxErrorSamples = 5;        // verbatim skip samples per category

        // Only one cleanup run at a time (the service stop/start dance must
        // never be executed by two threads concurrently).
        private static readonly object _runGate = new object();

        // ---- API -------------------------------------------------------------

        // The single entry point: executes the selected Tier 1 categories.
        // Returns false ONLY when the safety gates block the run (nothing is
        // deleted in that case) or another cleanup run is already active;
        // individual category failures are logged into their results and
        // never change the return value. Categories with Selected == false
        // are skipped (the caller owns the final tick decision).
        public static bool Clean(List<CleanCategory> selected, out List<CleanResult> results, out long totalBytesFreed)
        {
            results = new List<CleanResult>();
            totalBytesFreed = 0L;

            if (!Monitor.TryEnter(_runGate))
            {
                Log.Error("clean: another cleanup run is already in progress - refusing to start a second one");
                return false;
            }
            try
            {
                // a) gate re-check - never trust the caller
                List<string> reasons = StorageAnalyzer.CheckGates();
                if (reasons != null && reasons.Count > 0)
                {
                    foreach (string reason in reasons)
                    {
                        Log.Error("clean: gate blocks cleanup - " + reason);
                    }
                    Log.Chan("CLEAN", "clean: nothing was deleted (safety gates blocked the run)");
                    return false;
                }

                if (selected == null || selected.Count == 0)
                {
                    Log.Chan("CLEAN", "clean: no categories selected - nothing to do");
                    return true;
                }

                // Shared per-run state (the reporting-log category may be
                // cleaned during the WU purge pass while wuauserv is down).
                CleanRun run = new CleanRun();
                foreach (CleanCategory scan in selected)
                {
                    if (scan == null) continue;
                    if (string.Equals(scan.Name, CatReportingEvents, StringComparison.Ordinal))
                    {
                        run.ReportingEventsSelected = true;
                    }
                }

                // b) free space before (system drive)
                long before = FreeSpaceOfSystemDrive();

                foreach (CleanCategory cat in selected)
                {
                    if (cat == null) continue;
                    if (!cat.Selected)
                    {
                        Log.Chan("CLEAN", "clean '" + cat.Name + "': not selected - skipped");
                        continue;
                    }
                    CleanResult r = Dispatch(cat, run);
                    results.Add(r);
                    totalBytesFreed += r.BytesFreed;
                    string line = "clean '" + cat.Name + "': " + FormatBytes(r.BytesFreed) + " freed, " +
                        r.FilesDeleted.ToString("N0", CultureInfo.InvariantCulture) + " files deleted, " +
                        r.FilesSkipped.ToString("N0", CultureInfo.InvariantCulture) + " skipped";
                    if (!string.IsNullOrEmpty(r.Notes)) line += " - " + r.Notes;
                    Log.Chan("CLEAN", line);
                }

                // b) free space after + f) final line
                long after = FreeSpaceOfSystemDrive();
                if (before >= 0 && after >= 0)
                {
                    long delta = after - before;
                    Log.Chan("CLEAN", "clean: total " + FormatBytes(totalBytesFreed) + " freed; free space before " +
                        FormatBytes(before) + " -> after " + FormatBytes(after) +
                        " (delta " + (delta >= 0 ? "+" : "-") + FormatBytes(Math.Abs(delta)) + ")");
                }
                else
                {
                    Log.Chan("CLEAN", "clean: total " + FormatBytes(totalBytesFreed) +
                        " freed; free space before/after unknown (drive information unavailable)");
                }
                return true;
            }
            finally
            {
                Monitor.Exit(_runGate);
            }
        }

        // ---- dispatch ----------------------------------------------------------

        // Dispatches one category by its exact analyzer name; categories that
        // belong to other Wave 5 modules come back with a note instead of
        // being forgotten.
        private static CleanResult Dispatch(CleanCategory cat, CleanRun run)
        {
            switch (cat.Name)
            {
                case CatWuDownload: return CleanWuDownloadCache(run);
                case CatDeliveryOptimization: return CleanDeliveryOptimizationCache();
                case CatWindowsTemp: return CleanWindowsTemp();
                case CatErrorReports: return CleanErrorReports();
                case CatUpdateLogArchives: return CleanUpdateLogArchives();
                case CatReportingEvents: return CleanReportingEventsLog(run);
                case CatUserTemp: return CleanUserTemp();
                case CatCrashDumps: return CleanCrashDumps();
                case CatThumbnailCaches: return CleanThumbnailCaches();
                default: return SkipForeignCategory(cat);
            }
        }

        // Zero-byte result for categories owned by other Wave 5 modules.
        private static CleanResult SkipForeignCategory(CleanCategory cat)
        {
            if (string.Equals(cat.Kind, CleanCategory.KindGpu, StringComparison.Ordinal))
            {
                return new CleanResult(cat.Name, 0, 0, 0, "handled by GpuTools (Wave 5) - not run by StorageCleaner");
            }
            if (string.Equals(cat.Kind, CleanCategory.KindAppCache, StringComparison.Ordinal))
            {
                return new CleanResult(cat.Name, 0, 0, 0, "handled by AppCacheCleaner (Wave 5) - not run by StorageCleaner");
            }
            if (string.Equals(cat.Kind, CleanCategory.KindDism, StringComparison.Ordinal))
            {
                return new CleanResult(cat.Name, 0, 0, 0, "handled by ComponentStore (Wave 5) - not run by StorageCleaner");
            }
            return new CleanResult(cat.Name, 0, 0, 0,
                "unknown category - StorageCleaner has no cleaner for this target, nothing was done");
        }

        // ---- category cleaners ---------------------------------------------------

        // Windows Update download cache: the D3 purge flow. Services are
        // stopped in order usosvc -> wuauserv -> bits; the CHILDREN of
        // SoftwareDistribution\Download are deleted (never the folder
        // itself); the restart happens in the finally block so it also runs
        // on the abort path; WU re-detection is triggered after the restart.
        private static CleanResult CleanWuDownloadCache(CleanRun run)
        {
            Log.Chan("CLEAN", "clean: WU purge starting (stopping services)");
            Tally tally = new Tally();
            List<string> stoppedByUs = new List<string>();
            bool servicesReady = false;
            string purgeError = null;

            try
            {
                // ii) stop in order usosvc -> wuauserv -> bits; wuauserv is
                //     mandatory - if it refuses, this category aborts (the
                //     finally block restarts what we stopped) while the
                //     remaining categories still run.
                EnsureServiceStopped(ServiceUsoSvc, stoppedByUs);
                if (!EnsureServiceStopped(ServiceWuauserv, stoppedByUs))
                {
                    Log.Error("clean: WU purge aborted - wuauserv refused to stop, nothing was deleted");
                    return new CleanResult(CatWuDownload, 0, 0, 0,
                        "ABORTED - wuauserv could not be stopped, nothing was deleted; " +
                        "services stopped by this run were restarted");
                }
                if (!EnsureServiceStopped(ServiceBits, stoppedByUs))
                {
                    Log.Warn("clean: bits could not be stopped - purging anyway (some files may be locked)");
                }
                servicesReady = true;

                try
                {
                    // iii) children of Download only - the folder itself is
                    //      NEVER deleted.
                    DeleteSelectedChildren(WuDownloadPath, DateTime.MinValue, tally);

                    // The update reporting log rides along while the services
                    // are down (its own category reports the outcome later).
                    if (run.ReportingEventsSelected && run.ReportingEventsResult == null)
                    {
                        CleanResult reporting = TryDeleteReportingEvents(
                            "deleted while wuauserv was stopped (WU purge pass); " +
                            "the file is auto-recreated by Windows Update");
                        if (reporting != null)
                        {
                            run.ReportingEventsResult = reporting;
                        }
                    }
                }
                catch (Exception ex)
                {
                    purgeError = "purge failed partway: " + ex.Message;
                    Log.Error("clean: WU purge failed partway (restarting services)", ex);
                }
            }
            finally
            {
                // iv) restart in order bits -> wuauserv -> usosvc - only
                //     services THIS process stopped.
                RestartStoppedServices(stoppedByUs);
                if (servicesReady)
                {
                    TriggerUpdateDetection();   // v) non-fatal
                }
            }

            return FinishCategory(CatWuDownload,
                purgeError != null
                    ? purgeError
                    : "children of SoftwareDistribution\\Download only (the folder itself is kept)",
                tally);
        }

        // Delivery Optimization cache: documented PowerShell cmdlet, never
        // with -IncludePinnedFiles; freed bytes are a pre-measured estimate.
        private static CleanResult CleanDeliveryOptimizationCache()
        {
            if (IsForbiddenPath(DeliveryOptimizationPath))
            {
                // Cannot happen with today's path table - belt-and-braces.
                Log.Error("GUARD: refusing " + DisplayPath(DeliveryOptimizationPath));
                return new CleanResult(CatDeliveryOptimization, 0, 0, 0, "refused by the D7 guard");
            }

            Tally measure = new Tally();
            if (!Directory.Exists(DeliveryOptimizationPath))
            {
                return new CleanResult(CatDeliveryOptimization, 0, 0, 0, "cache empty or not present - nothing to delete");
            }
            MeasureTreeOnly(DeliveryOptimizationPath, measure);
            if (measure.Files == 0)
            {
                return new CleanResult(CatDeliveryOptimization, 0, 0, 0, "cache empty or not present - nothing to delete");
            }
            string preNote = FormatBytes(measure.Bytes) + " in " +
                measure.Files.ToString("N0", CultureInfo.InvariantCulture) + " file(s) measured before the run";

            string output;
            bool ok = RunHiddenProcess(ResolvePowershell(),
                "-NoProfile -ExecutionPolicy Bypass -Command \"Delete-DeliveryOptimizationCache -Force\"",
                PowerShellTimeoutMs, out output);
            if (ok)
            {
                Log.Chan("CLEAN", "clean: Delete-DeliveryOptimizationCache completed" +
                    (output.Length > 0 ? " - " + Summarize(output) : ""));
                return new CleanResult(CatDeliveryOptimization, measure.Bytes, 0, 0,
                    preNote + "; estimate - the cmdlet does not report bytes or file counts");
            }
            Log.Error("clean: Delete-DeliveryOptimizationCache failed - " + Summarize(output));
            return new CleanResult(CatDeliveryOptimization, 0, 0, 0,
                preNote + "; cmdlet failed, nothing deleted: " + Summarize(output));
        }

        // C:\Windows\Temp: top-level items older than 7 days (old folders
        // with all of their contents), same age rule the analyzer measured.
        private static CleanResult CleanWindowsTemp()
        {
            if (!Directory.Exists(WindowsTempPath))
            {
                return new CleanResult(CatWindowsTemp, 0, 0, 0, "not present");
            }
            Tally tally = new Tally();
            DeleteSelectedChildren(WindowsTempPath, DateTime.Now - TimeSpan.FromDays(TempMaxAgeDays), tally);
            return FinishCategory(CatWindowsTemp,
                "top-level items older than " + TempMaxAgeDays + " days only", tally);
        }

        // WER ReportQueue + ReportArchive: items older than 7 days, newer
        // reports kept.
        private static CleanResult CleanErrorReports()
        {
            string notes = "reports older than " + WerNoteAge() + " (newer reports are kept)";
            Tally tally = new Tally();
            DateTime cutoff = DateTime.Now - TimeSpan.FromDays(TempMaxAgeDays);
            if (!Directory.Exists(WerQueuePath))
            {
                notes = AppendNote(notes, "ReportQueue: not present");
            }
            else
            {
                DeleteSelectedChildren(WerQueuePath, cutoff, tally);
            }
            if (!Directory.Exists(WerArchivePath))
            {
                notes = AppendNote(notes, "ReportArchive: not present");
            }
            else
            {
                DeleteSelectedChildren(WerArchivePath, cutoff, tally);
            }
            return FinishCategory(CatErrorReports, notes, tally);
        }

        // CbsPersist_*.cab older than 30 days in C:\Windows\Logs\CBS plus
        // WindowsUpdate log files older than 30 days (folder structure kept,
        // only aged files go).
        private static CleanResult CleanUpdateLogArchives()
        {
            string notes = "CbsPersist_*.cab and WindowsUpdate log files older than " +
                LogArchiveMaxAgeDays + " days";
            Tally tally = new Tally();
            DateTime cutoff = DateTime.Now - TimeSpan.FromDays(LogArchiveMaxAgeDays);

            if (!Directory.Exists(CbsLogsPath))
            {
                notes = AppendNote(notes, "CBS logs: not present");
            }
            else
            {
                DeleteMatchingFiles(CbsLogsPath, delegate(string name, ref Win32FindData data)
                {
                    if ((data.FileAttributes & FileAttributeDirectory) != 0) return false;
                    if (!name.StartsWith("CbsPersist_", StringComparison.OrdinalIgnoreCase)) return false;
                    if (!name.EndsWith(".cab", StringComparison.OrdinalIgnoreCase)) return false;
                    return FileTimeToDateTime(data.LastWriteTime) < cutoff;
                }, tally);
            }

            if (!Directory.Exists(WuLogsPath))
            {
                notes = AppendNote(notes, "WindowsUpdate logs: not present");
            }
            else
            {
                DeleteMatchingFiles(WuLogsPath, delegate(string name, ref Win32FindData data)
                {
                    if ((data.FileAttributes & FileAttributeDirectory) != 0) return false;
                    return FileTimeToDateTime(data.LastWriteTime) < cutoff;
                }, tally);
            }
            return FinishCategory(CatUpdateLogArchives, notes, tally);
        }

        // ReportingEvents.log: auto-recreated by Windows Update; when the WU
        // purge ran, this was already deleted while wuauserv was stopped and
        // that result is reported here; otherwise a standalone attempt with
        // skip+warn when locked.
        private static CleanResult CleanReportingEventsLog(CleanRun run)
        {
            if (run.ReportingEventsResult != null)
            {
                return run.ReportingEventsResult;
            }
            Log.Chan("CLEAN", "clean: deleting ReportingEvents.log (auto-recreated by Windows Update)");
            CleanResult deleted = TryDeleteReportingEvents("file is auto-recreated by Windows Update");
            if (deleted != null)
            {
                return deleted;
            }
            return new CleanResult(CatReportingEvents, 0, 0, 1, "file was locked (in use) - left in place");
        }

        // %TEMP%: all unlocked items (no age rule).
        private static CleanResult CleanUserTemp()
        {
            string temp;
            try
            {
                temp = Path.GetTempPath();
                if (temp.Length > 3 && temp.EndsWith("\\", StringComparison.Ordinal))
                {
                    temp = temp.Substring(0, temp.Length - 1);
                }
            }
            catch (Exception ex)
            {
                return new CleanResult(CatUserTemp, 0, 0, 0, "could not resolve %TEMP% - " + ex.Message);
            }
            if (!Directory.Exists(temp))
            {
                return new CleanResult(CatUserTemp, 0, 0, 0, "%TEMP% = " + temp + " - not present");
            }
            Tally tally = new Tally();
            DeleteSelectedChildren(temp, DateTime.MinValue, tally);
            return FinishCategory(CatUserTemp, "%TEMP% = " + temp + " (all unlocked items)", tally);
        }

        // Crash dumps: MEMORY.DMP + everything in C:\Windows\Minidump.
        private static CleanResult CleanCrashDumps()
        {
            string notes = "";
            Tally tally = new Tally();
            if (!FileExistsLong(MemoryDumpPath))
            {
                notes = AppendNote(notes, "MEMORY.DMP: not present");
            }
            else
            {
                DeleteSingleFile(MemoryDumpPath, tally);
            }
            if (!Directory.Exists(MinidumpPath))
            {
                notes = AppendNote(notes, "Minidump: not present");
            }
            else
            {
                DeleteSelectedChildren(MinidumpPath, DateTime.MinValue, tally);
            }
            return FinishCategory(CatCrashDumps, notes, tally);
        }

        // Thumbnail caches: thumbcache_/iconcache_ files in the Explorer
        // cache folder - Explorer usually locks them, skipped files are the
        // expected outcome while it runs.
        private static CleanResult CleanThumbnailCaches()
        {
            string explorerDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine("Microsoft", Path.Combine("Windows", "Explorer")));
            if (!Directory.Exists(explorerDir))
            {
                return new CleanResult(CatThumbnailCaches, 0, 0, 0, "not present");
            }
            Tally tally = new Tally();
            DeleteMatchingFiles(explorerDir, delegate(string name, ref Win32FindData data)
            {
                if ((data.FileAttributes & FileAttributeDirectory) != 0) return false;
                return name.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("iconcache_", StringComparison.OrdinalIgnoreCase);
            }, tally);
            return FinishCategory(CatThumbnailCaches,
                "thumbcache_/iconcache_ files - Explorer usually locks these while running, " +
                "skipped files are expected", tally);
        }

        // ---- reporting log helper --------------------------------------------

        // Deletes ReportingEvents.log (auto-recreated by Windows Update; the
        // only file besides the Download children ever allowed under
        // SoftwareDistribution). Returns the result - or null when the file
        // was locked, so the caller can retry later or report the skip.
        private static CleanResult TryDeleteReportingEvents(string contextNote)
        {
            if (IsForbiddenPath(ReportingEventsPath))
            {
                Log.Error("GUARD: refusing " + DisplayPath(ReportingEventsPath));
                return new CleanResult(CatReportingEvents, 0, 0, 0, "refused by the D7 guard");
            }
            long size = 0;
            bool present = false;
            Win32FileAttributeData data;
            if (GetFileAttributesExW(ToLongPath(ReportingEventsPath), GetFileExInfoStandard, out data))
            {
                present = true;
                size = ((long)data.FileSizeHigh << 32) | (long)data.FileSizeLow;
            }
            if (!present)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorFileNotFound && error != ErrorPathNotFound)
                {
                    return new CleanResult(CatReportingEvents, 0, 0, 0, "not accessible (" + Win32Message(error) + ")");
                }
                return new CleanResult(CatReportingEvents, 0, 0, 0, "not present");
            }
            if (DeleteFileW(ToLongPath(ReportingEventsPath)))
            {
                return new CleanResult(CatReportingEvents, size, 1, 0, contextNote);
            }
            int deleteError = Marshal.GetLastWin32Error();
            if (deleteError == ErrorFileNotFound || deleteError == ErrorPathNotFound)
            {
                return new CleanResult(CatReportingEvents, 0, 0, 0, "not present");
            }
            Log.Warn("clean: skipped " + DisplayPath(ReportingEventsPath) +
                (IsInUseError(deleteError) ? " (in use)" : " (" + Win32Message(deleteError) + ")"));
            return null;
        }

        // ---- services -----------------------------------------------------------

        // Stops one service with a bounded 20 s wait. Returns true when the
        // service is Stopped after the call - whether it was already
        // stopped, finished stopping on its own, or was stopped by us
        // (recorded in stoppedByUs so only services WE stopped are
        // restarted later). Failures are WARNs; never throws.
        private static bool EnsureServiceStopped(string name, List<string> stoppedByUs)
        {
            try
            {
                using (ServiceController sc = new ServiceController(name))
                {
                    ServiceControllerStatus original = sc.Status;
                    Log.Chan("CLEAN", "clean: " + name + " status was " + original);
                    if (original == ServiceControllerStatus.Stopped)
                    {
                        Log.Chan("CLEAN", "clean: " + name + " was already stopped");
                        return true;
                    }
                    if (original != ServiceControllerStatus.StopPending)
                    {
                        sc.Stop();
                        stoppedByUs.Add(name);      // Stop() accepted - we own the restart
                    }
                    try
                    {
                        sc.WaitForStatus(ServiceControllerStatus.Stopped,
                            TimeSpan.FromSeconds(StopServiceTimeoutSeconds));
                        Log.Chan("CLEAN", original == ServiceControllerStatus.StopPending
                            ? "clean: " + name + " finished stopping on its own"
                            : "clean: stopped " + name);
                        return true;
                    }
                    catch (System.ServiceProcess.TimeoutException)
                    {
                        try { sc.Refresh(); } catch { }
                        Log.Warn("clean: " + name + " did not stop within " + StopServiceTimeoutSeconds +
                            "s (status " + sc.Status + ")");
                        return sc.Status == ServiceControllerStatus.Stopped;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("clean: " + name + " could not be stopped - " + ex.Message);
                return false;
            }
        }

        // Restarts exactly the services this process stopped, in order
        // bits -> wuauserv -> usosvc (reverse of the stop order).
        private static void RestartStoppedServices(List<string> stoppedByUs)
        {
            if (stoppedByUs.Count == 0)
            {
                Log.Chan("CLEAN", "clean: no Windows Update services to restart");
                return;
            }
            string[] restartOrder = { ServiceBits, ServiceWuauserv, ServiceUsoSvc };
            foreach (string name in restartOrder)
            {
                if (stoppedByUs.Contains(name)) RestartService(name);
            }
        }

        // Starts one service again (bounded 20 s wait); failures are WARNs,
        // never thrown.
        private static void RestartService(string name)
        {
            try
            {
                using (ServiceController sc = new ServiceController(name))
                {
                    ServiceControllerStatus status = sc.Status;
                    if (status == ServiceControllerStatus.Stopped || status == ServiceControllerStatus.StopPending)
                    {
                        sc.Start();
                        try
                        {
                            sc.WaitForStatus(ServiceControllerStatus.Running,
                                TimeSpan.FromSeconds(StopServiceTimeoutSeconds));
                            Log.Chan("CLEAN", "clean: restarted " + name);
                        }
                        catch (System.ServiceProcess.TimeoutException)
                        {
                            try { sc.Refresh(); } catch { }
                            Log.Warn("clean: " + name + " did not restart within " + StopServiceTimeoutSeconds +
                                "s (status " + sc.Status + ")");
                        }
                    }
                    else
                    {
                        Log.Chan("CLEAN", "clean: " + name + " is already " + status + " - not started again");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("clean: " + name + " could not be restarted - " + ex.Message);
            }
        }

        // ---- WU re-detection -------------------------------------------------------

        // Triggers Windows Update re-detection so the purged cache is
        // repopulated with fresh metadata: reflection COM
        // (Type.GetTypeFromProgID + Activator.CreateInstance +
        // Type.InvokeMember) instead of `dynamic`, so build.cmd never needs
        // the Microsoft.CSharp reference. Failure = WARN, non-fatal.
        private static void TriggerUpdateDetection()
        {
            try
            {
                Type autoUpdateType = Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate");
                if (autoUpdateType == null)
                {
                    Log.Warn("clean: WU re-detection skipped - ProgID Microsoft.Update.AutoUpdate is not registered");
                    return;
                }
                object autoUpdate = Activator.CreateInstance(autoUpdateType);
                try
                {
                    autoUpdateType.InvokeMember("DetectNow",
                        BindingFlags.InvokeMethod | BindingFlags.Instance | BindingFlags.Public,
                        null, autoUpdate, null);
                    Log.Chan("CLEAN", "clean: WU re-detection triggered (DetectNow)");
                }
                finally
                {
                    try { Marshal.ReleaseComObject(autoUpdate); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("clean: WU re-detection failed (non-fatal) - " + ex.Message);
            }
        }

        // ---- process / drive helpers -------------------------------------------------

        // Runs a console process fully hidden with both output streams
        // captured; returns true when the exit code is 0. A timeout kills
        // the process and reports a failure. Never throws.
        private static bool RunHiddenProcess(string fileName, string arguments, int timeoutMs, out string output)
        {
            output = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = fileName;
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    // stdout first: the same deadlock-safe order as PowerPlans.
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        output = Summarize(stdout + " " + stderr) +
                            " (did not exit within " + (timeoutMs / 1000) + "s - killed)";
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

        // Full path of the Windows PowerShell executable (canonical location
        // first, PATH resolution as fallback).
        private static string ResolvePowershell()
        {
            try
            {
                string full = Path.Combine(Environment.SystemDirectory,
                    Path.Combine("WindowsPowerShell", Path.Combine("v1.0", "powershell.exe")));
                if (File.Exists(full)) return full;
            }
            catch (Exception) { }
            return "powershell.exe";
        }

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

        // ---- deletion walkers (guard-checked, long-path safe) --------------------------

        // Deletes whole selected subtrees under rootDir: every top-level
        // entry passing topLevelCutoff (DateTime.MinValue = no age rule) is
        // deleted recursively; non-selected top-level entries are left
        // untouched. rootDir itself is NEVER deleted (children only).
        private static void DeleteSelectedChildren(string rootDir, DateTime topLevelCutoff, Tally tally)
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

        // Recursively deletes FILES matching the selector at any depth;
        // directories themselves are never deleted here (the log-archive
        // categories keep their folder structure, only aged files go).
        private static void DeleteMatchingFiles(string rootDir, FileSelector selector, Tally tally)
        {
            if (IsForbiddenPath(rootDir))
            {
                Log.Error("GUARD: refusing " + DisplayPath(rootDir));
                tally.Refused++;
                return;
            }
            WalkAndDeleteFiles(rootDir, selector, tally);
        }

        private static void WalkAndDeleteFiles(string dirPath, FileSelector selector, Tally tally)
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
                    WalkAndDeleteFiles(dir + "\\" + name, selector, tally);
                    return;
                }
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
        private static void DeleteItem(string dir, string name, ref Win32FindData data, bool isDir, Tally tally)
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
                Log.Warn("clean: skipped " + DisplayPath(path) + " (" + ex.Message + ")");
            }
        }

        // Removes a directory AFTER its contents: contents first (per-item
        // guard + tally), then the directory itself. A directory that stays
        // behind because contents were locked is counted in FoldersLeft
        // (no extra warn - the file-level warns already explain why); other
        // removal failures get a WARN. Access-denied gets one retry with
        // the read-only attribute cleared.
        private static void DeleteDirectoryEntry(string dirPath, Tally tally)
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
                Log.Warn("clean: could not remove folder " + DisplayPath(dirPath) +
                    " (" + Win32Message(error) + ")");
            }
        }

        private static void DeleteDirectoryContents(string dirPath, Tally tally)
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
        private static void DeleteFileEntry(string filePath, long size, Tally tally)
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
            Log.Warn("clean: skipped " + DisplayPath(filePath) + " (" + reason + ")");
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

        // Deletes one exact file (guard-checked, silent when absent).
        private static void DeleteSingleFile(string filePath, Tally tally)
        {
            if (IsForbiddenPath(filePath))
            {
                Log.Error("GUARD: refusing " + DisplayPath(filePath));
                tally.Refused++;
                return;
            }
            long size = 0;
            Win32FileAttributeData data;
            if (GetFileAttributesExW(ToLongPath(filePath), GetFileExInfoStandard, out data))
            {
                size = ((long)data.FileSizeHigh << 32) | (long)data.FileSizeLow;
                DeleteFileEntry(filePath, size, tally);
            }
        }

        // True when the path exists (file or directory; unreadable counts as
        // present - the deletion attempt reports the truth). Long-path safe.
        private static bool FileExistsLong(string path)
        {
            Win32FileAttributeData data;
            if (!GetFileAttributesExW(ToLongPath(path), GetFileExInfoStandard, out data))
            {
                int error = Marshal.GetLastWin32Error();
                return error != ErrorFileNotFound && error != ErrorPathNotFound;
            }
            return true;
        }

        // Read-only size/count walk (used for the Delivery Optimization
        // estimate): same conventions as the delete walks - reparse points
        // skipped, per-item failures swallowed.
        private static void MeasureTreeOnly(string dirPath, Tally tally)
        {
            ForEachEntry(dirPath, delegate(string dir, string name, ref Win32FindData data)
            {
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
                MeasureTreeOnly(dir + "\\" + name, tally);
            });
        }

        // ---- directory enumeration (kernel32, long-path safe) ---------------------------

        private delegate void EntryVisitor(string dirPath, string name, ref Win32FindData data);

        private delegate bool FileSelector(string name, ref Win32FindData data);

        // Enumerates one directory level through FindFirstFileW (which takes
        // \\?\ paths natively) and calls visit for every entry except
        // "."/"..". A directory that cannot be opened logs one WARN and
        // yields nothing; the find handle is always closed.
        private static void ForEachEntry(string dirPath, EntryVisitor visit)
        {
            string longDir = ToLongPath(dirPath);
            Win32FindData data;
            IntPtr handle = FindFirstFileW(longDir + "\\*", out data);
            if (handle == InvalidFindHandle)
            {
                Log.Warn("clean: could not enumerate " + DisplayPath(dirPath) + " (" +
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

        // ---- D7 hard guard ---------------------------------------------------------

        // The D7 hard guard, checked before EVERY deletion (see the file
        // header for the list). Blank paths fail closed. This can only ever
        // fire on a programming error - the fixed category paths above are
        // all outside the guarded set.
        private static bool IsForbiddenPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;    // blank fails closed
            string p = path.Trim();
            if (p.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase)) p = p.Substring(4);
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
                // Only Download children and ReportingEvents.log are ever
                // allowed under SoftwareDistribution (DataStore and the rest
                // are off-limits).
                bool downloadChild = IsUnderOrEqual(p, @"c:\windows\softwaredistribution\download");
                bool reportingLog = string.Equals(p, @"c:\windows\softwaredistribution\reportingevents.log",
                    StringComparison.Ordinal);
                if (!downloadChild && !reportingLog) return true;
            }
            return false;
        }

        // True when p equals prefix or lies inside it (boundary-aware: the
        // next character after the prefix must be end-of-string or '\', so
        // "catroot" does not accidentally swallow unrelated names while
        // "catroot2" is still matched by its own entry).
        private static bool IsUnderOrEqual(string p, string prefix)
        {
            if (!p.StartsWith(prefix, StringComparison.Ordinal)) return false;
            return p.Length == prefix.Length || p[prefix.Length] == '\\';
        }

        // ---- formatting / small helpers -------------------------------------------------

        // Same B/KB/MB/GB formatting as CleanCategory.SizeText (invariant
        // culture). Public: the Wave 6 UI renders CleanResult values too.
        public static string FormatBytes(long bytes)
        {
            long b = bytes < 0 ? 0 : bytes;
            if (b < 1024L)
            {
                return b.ToString("N0", CultureInfo.InvariantCulture) + " B";
            }
            if (b < 1024L * 1024L)
            {
                return (b / 1024.0).ToString("N1", CultureInfo.InvariantCulture) + " KB";
            }
            if (b < 1024L * 1024L * 1024L)
            {
                return (b / (1024.0 * 1024.0)).ToString("N1", CultureInfo.InvariantCulture) + " MB";
            }
            return (b / (1024.0 * 1024.0 * 1024.0)).ToString("N2", CultureInfo.InvariantCulture) + " GB";
        }

        // Moves a tally into the CleanResult for one category.
        private static CleanResult FinishCategory(string categoryName, string notes, Tally tally)
        {
            CleanResult result = new CleanResult(categoryName, tally.Bytes, tally.Files, tally.Skipped, notes);
            result.Notes = AppendNote(notes, tally.Describe());
            return result;
        }

        private static string AppendNote(string existing, string addition)
        {
            if (string.IsNullOrEmpty(addition)) return existing == null ? "" : existing;
            if (string.IsNullOrEmpty(existing)) return addition;
            return existing + "; " + addition;
        }

        private static string WerNoteAge()
        {
            return TempMaxAgeDays + " days";
        }

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
        // enumeration and deletion tolerate paths beyond MAX_PATH (files
        // deep under SoftwareDistribution). Only canonical local drive paths
        // (C:\...) are prefixed; anything else is returned unchanged.
        private static string ToLongPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("\\\\?\\", StringComparison.Ordinal))
            {
                return path;
            }
            if (path.Length > 3 && path[1] == ':' && path[2] == '\\' && path.IndexOf('/') < 0)
            {
                return "\\\\?\\" + path;
            }
            return path;
        }

        // Strips the \\?\ extended-length prefix for display in notes/warns.
        private static string DisplayPath(string path)
        {
            if (path != null && path.StartsWith("\\\\?\\", StringComparison.Ordinal))
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

        // One-line, length-capped version of raw command output for log lines.
        private static string Summarize(string text)
        {
            string t = text == null ? "" : text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (t.Contains("  ")) t = t.Replace("  ", " ");
            if (t.Length > 300) t = t.Substring(0, 300) + "...";
            return t.Length == 0 ? "(no output)" : t;
        }

        // ---- win32 interop (kernel32; same pattern as StorageAnalyzer.cs) -----------------

        private const int GetFileExInfoStandard = 0;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32FileAttributeData
        {
            public uint FileAttributes;
            public Win32FileTime CreationTime;
            public Win32FileTime LastAccessTime;
            public Win32FileTime LastWriteTime;
            public uint FileSizeHigh;
            public uint FileSizeLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string fileName, out Win32FindData findData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(IntPtr findHandle, out Win32FindData findData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr findHandle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetFileAttributesExW(string fileName, int infoLevelId,
            out Win32FileAttributeData data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileW(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool RemoveDirectoryW(string pathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetFileAttributesW(string fileName, uint fileAttributes);

        // ---- tally -------------------------------------------------------------------------

        // Running totals for one clean/measure pass. Per-item failures land
        // in capped verbatim samples (MaxErrorSamples) and are summarized by
        // Describe() into the CleanResult.Notes.
        private sealed class Tally
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

        // ---- per-run state ------------------------------------------------------------------

        // State shared between the dispatch loop and the WU purge: the
        // reporting-log category is cleaned during the purge pass (while
        // wuauserv is stopped) but its result is reported at the category's
        // own position in the results list.
        private sealed class CleanRun
        {
            public bool ReportingEventsSelected;
            public CleanResult ReportingEventsResult;
        }
    }
}
