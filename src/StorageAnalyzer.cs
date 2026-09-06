//  StorageAnalyzer.cs  (v1.1.0 - Wave 4, agent A6)
//  ------------------------------------------------
//  Storage cleanup analyzer: measures the Windows Update / deep-clean / GPU
//  shader-cache cleanup targets and checks the cleanup safety gates (D7).
//
//  DETECTION AND MEASUREMENT ONLY - STRICTLY READ-ONLY. Nothing in this file
//  deletes, moves, creates or writes anything: measurement walks directories
//  and reads file metadata; CheckGates reads registry values, checks one file
//  for existence and queries service status. All deletion belongs to the
//  later cleaner modules (StorageCleaner.cs A7, DeepClean.cs A10, ...).
//  Analyze-first always (D3): this file produces the numbers, the cleaners
//  decide what to delete - never the reverse.
//
//  Log contract (channel CLEAN): one line per measured category
//      measure '<Name>': <SizeText> in <Files> files [- <Notes>]
//  (absent GPU shader-cache paths log "not present (skipped)" without adding
//  a category), plus a single gate line from CheckGates():
//      gates: N block reason(s)   /   gates: clear
//
//  Failure model: every category is individually try/caught so one failure
//  never breaks the rest; per-item IO/access failures are swallowed and
//  summarized in Notes (up to 5 verbatim samples, then "and N more"). Missing
//  paths measure 0 bytes with a "not present" note. Long paths (files deep
//  under SoftwareDistribution can exceed MAX_PATH) are enumerated with the
//  kernel32 FindFirstFileW / GetFileAttributesExW calls, which natively
//  accept the \\?\ extended-length prefix - the .NET DirectoryInfo/FileInfo
//  object model rejects that prefix on .NET Framework 4.x. Reparse points
//  (junctions/symlinks) are never followed (loop safety).
//
//  New in v1.1.0 Wave 4 per docs\HANDBOOK.md section 4 (D3/D7) and section 8.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // One cleanable category found by StorageAnalyzer.MeasureAll(). Plain
    // data holder for the Wave 6 UI and the cleaner modules. Kind is one of
    // the Kind* constants below. Selected defaults to true for every
    // category; the Windows Update download cache and Delivery Optimization
    // categories additionally note that their services must be stopped
    // before cleaning (the cleaner module stops them; the Wave 6 UI owns
    // the final decision).
    // ---------------------------------------------------------------------
    public class CleanCategory
    {
        public const string KindWu = "wu";
        public const string KindDism = "dism";
        public const string KindDeepClean = "deepclean";
        public const string KindAppCache = "appcache";
        public const string KindGpu = "gpu";

        public string Name;
        public string Kind;
        public long Bytes;
        public int Files;
        public string Notes;
        public string RiskLabel;
        public bool Selected;

        public CleanCategory()
        {
            Name = "";
            Kind = "";
            Bytes = 0;
            Files = 0;
            Notes = "";
            RiskLabel = "low";
            Selected = true;
        }

        public CleanCategory(string name, string kind, string riskLabel) : this()
        {
            Name = name;
            Kind = kind;
            RiskLabel = riskLabel;
        }

        // Human-readable size for the UI/log, e.g. "567 B", "12.3 KB",
        // "1,234.5 MB" or "2.75 GB".
        public string SizeText
        {
            get
            {
                long b = Bytes < 0 ? 0 : Bytes;
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
        }
    }

    // ---------------------------------------------------------------------
    // Storage cleanup analyzer (static). MeasureAll() walks every cleanup
    // target and returns one CleanCategory per target; CheckGates() returns
    // the D7 safety-gate block reasons (an empty list = safe to clean);
    // GatesSummaryText() renders the reasons for the UI. Read-only, logged
    // on the CLEAN channel, best-effort everywhere.
    // ---------------------------------------------------------------------
    public static class StorageAnalyzer
    {
        // ---- measured targets ---------------------------------------------
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

        // ---- D7 gate targets ------------------------------------------------
        private const string CbsRegistryKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing";
        private const string WuRebootRegistryKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";
        private const string SessionManagerRegistryKey =
            @"SYSTEM\CurrentControlSet\Control\Session Manager";
        private const string PendingXmlPath = @"C:\Windows\WinSxS\pending.xml";

        private const int TempMaxAgeDays = 7;       // c) Windows temp rule
        private const int CabMaxAgeDays = 30;       // e) CbsPersist_*.cab rule
        private const int MaxErrorSamples = 5;      // verbatim error notes per category

        private static readonly string[] WuServiceNames =
        {
            "wuauserv", "bits", "UsoSvc", "DoSvc", "TrustedInstaller"
        };

        // ---- API ------------------------------------------------------------

        // Measures every cleanup target (read-only) and returns one
        // CleanCategory per target in a stable order. Never throws: each
        // category is wrapped individually and logs its own CLEAN line.
        public static List<CleanCategory> MeasureAll()
        {
            List<CleanCategory> results = new List<CleanCategory>();
            MeasureWuDownloadCache(results);
            MeasureDeliveryOptimizationCache(results);
            MeasureWindowsTemp(results);
            MeasureErrorReports(results);
            MeasureUpdateLogArchives(results);
            MeasureReportingEventsLog(results);
            MeasureUserTemp(results);
            MeasureCrashDumps(results);
            MeasureThumbnailCaches(results);
            MeasureShaderCaches(results);
            return results;
        }

        // Returns the cleanup safety-gate block reasons (D7); an empty list
        // means all gates are clear. Checked: process elevation, pending
        // reboot (servicing registry flags, PendingFileRenameOperations,
        // WinSxS pending.xml) and busy Windows Update services. Every
        // individual check is try/caught and fails closed - a gate that
        // cannot be verified becomes a block reason. Read-only; also writes
        // the single "gates: ..." CLEAN line.
        public static List<string> CheckGates()
        {
            List<string> reasons = new List<string>();

            // - elevation -----------------------------------------------------
            try
            {
                bool elevated = false;
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    if (identity != null)
                    {
                        WindowsPrincipal principal = new WindowsPrincipal(identity);
                        elevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
                    }
                }
                if (!elevated)
                {
                    reasons.Add("The process is not running elevated (as administrator) - " +
                        "cleanup needs the UAC-elevated app; relaunch and accept the prompt.");
                }
            }
            catch (Exception ex)
            {
                reasons.Add("Elevation could not be verified (" + ex.Message + ") - " +
                    "run the app as administrator to be safe.");
            }

            // - pending reboot signals ----------------------------------------
            try
            {
                if (RegistryKeyExists(CbsRegistryKey + @"\RebootPending"))
                {
                    reasons.Add("Windows Component Based Servicing has a pending reboot " +
                        "(RebootPending) - restart Windows before cleaning.");
                }
            }
            catch (Exception ex)
            {
                reasons.Add("The Component Based Servicing reboot flag could not be checked (" +
                    ex.Message + ") - restart Windows before cleaning to be safe.");
            }

            try
            {
                if (RegistryKeyExists(CbsRegistryKey + @"\PackagesPending"))
                {
                    reasons.Add("Windows servicing has pending package operations (PackagesPending) - " +
                        "restart Windows before cleaning.");
                }
            }
            catch (Exception ex)
            {
                reasons.Add("The Component Based Servicing package flag could not be checked (" +
                    ex.Message + ") - restart Windows before cleaning to be safe.");
            }

            try
            {
                if (RegistryKeyExists(WuRebootRegistryKey))
                {
                    reasons.Add("Windows Update reports a reboot is required - " +
                        "restart Windows before cleaning.");
                }
            }
            catch (Exception ex)
            {
                reasons.Add("The Windows Update reboot flag could not be checked (" +
                    ex.Message + ") - restart Windows before cleaning to be safe.");
            }

            try
            {
                if (PendingFileRenamesPending())
                {
                    reasons.Add("File rename operations are pending for the next boot " +
                        "(Session Manager PendingFileRenameOperations) - restart Windows before cleaning.");
                }
            }
            catch (Exception ex)
            {
                reasons.Add("Pending file rename operations could not be checked (" +
                    ex.Message + ") - restart Windows before cleaning to be safe.");
            }

            try
            {
                if (File.Exists(PendingXmlPath))
                {
                    reasons.Add("The component store has a pending servicing stage (WinSxS\\pending.xml) - " +
                        "restart Windows before cleaning (that file is never touched).");
                }
            }
            catch (Exception ex)
            {
                reasons.Add("WinSxS\\pending.xml could not be checked (" + ex.Message + ") - " +
                    "restart Windows before cleaning to be safe.");
            }

            // - Windows Update services busy ----------------------------------
            foreach (string name in WuServiceNames)
            {
                try
                {
                    using (ServiceController sc = new ServiceController(name))
                    {
                        ServiceControllerStatus status = sc.Status;
                        if (status == ServiceControllerStatus.Running ||
                            status == ServiceControllerStatus.StartPending ||
                            status == ServiceControllerStatus.StopPending)
                        {
                            reasons.Add("Windows Update service '" + name + "' is " + status +
                                " - do not clean the Windows Update caches while it is busy.");
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    // Service not installed on this machine - not a gate.
                }
                catch (Exception ex)
                {
                    reasons.Add("Windows Update service '" + name + "' could not be checked (" +
                        ex.Message + ") - treat its caches as busy.");
                }
            }

            Log.Chan("CLEAN", reasons.Count == 0
                ? "gates: clear"
                : "gates: " + reasons.Count.ToString("N0", CultureInfo.InvariantCulture) + " block reason(s)");
            return reasons;
        }

        // Renders the gate reasons for the UI (message boxes, gate panels):
        // a single friendly sentence when clear, otherwise the count plus
        // one "- reason" line per block.
        public static string GatesSummaryText(List<string> reasons)
        {
            if (reasons == null || reasons.Count == 0)
            {
                return "All cleanup safety gates are clear.";
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(reasons.Count.ToString("N0", CultureInfo.InvariantCulture));
            sb.Append(reasons.Count == 1 ? " safety gate blocks cleanup:" : " safety gates block cleanup:");
            foreach (string reason in reasons)
            {
                sb.Append(Environment.NewLine).Append(" - ").Append(reason);
            }
            return sb.ToString();
        }

        // ---- category measurements (each self-contained + try/caught) -------

        // a) Windows Update download cache: all children of
        //    C:\Windows\SoftwareDistribution\Download.
        private static void MeasureWuDownloadCache(List<CleanCategory> results)
        {
            CleanCategory cat = new CleanCategory("Windows Update download cache", CleanCategory.KindWu,
                "needs WU services stopped first");
            try
            {
                MeasureTreeInto(cat, WuDownloadPath, null);
                cat.Notes = AppendNote(cat.Notes,
                    "cleaner must stop wuauserv/bits/UsoSvc before deleting anything here");
            }
            catch (Exception ex)
            {
                cat.Notes = AppendNote(cat.Notes, "measurement failed: " + ex.Message);
            }
            Finish(results, cat);
        }

        // b) Delivery Optimization cache.
        private static void MeasureDeliveryOptimizationCache(List<CleanCategory> results)
        {
            CleanCategory cat = new CleanCategory("Delivery Optimization cache", CleanCategory.KindWu,
                "needs DO/WU services stopped first");
            try
            {
                MeasureTreeInto(cat, DeliveryOptimizationPath, null);
                cat.Notes = AppendNote(cat.Notes,
                    "cleaner must stop/flush Delivery Optimization (DoSvc) before deleting anything here");
            }
            catch (Exception ex)
            {
                cat.Notes = AppendNote(cat.Notes, "measurement failed: " + ex.Message);
            }
            Finish(results, cat);
        }

        // c) C:\Windows\Temp: top-level files and folders whose LastWriteTime
        //    is older than 7 days (old folders counted with all contents).
        private static void MeasureWindowsTemp(List<CleanCategory> results)
        {
            CleanCategory cat = new CleanCategory("Windows temp (>7 days)", CleanCategory.KindDeepClean,
                "low (7-day age rule)");
            try
            {
                cat.Notes = AppendNote(cat.Notes,
                    "top-level items last written more than " + TempMaxAgeDays +
                    " days ago (old folders counted with all contents)");
                MeasureAgedTreeInto(cat, WindowsTempPath, null, TimeSpan.FromDays(TempMaxAgeDays));
            }
            catch (Exception ex)
            {
                cat.Notes = AppendNote(cat.Notes, "measurement failed: " + ex.Message);
            }
            Finish(results, cat);
        }

        // d) Windows error reports: WER ReportQueue + ReportArchive.
        private static void MeasureErrorReports(List<CleanCategory> results)
        {
            CleanCategory cat = new CleanCategory("Windows error reports", CleanCategory.KindDeepClean,
                "low (queued/sent reports only)");
            try
            {
                MeasureTreeInto(cat, WerQueuePath, "ReportQueue");
                MeasureTreeInto(cat, WerArchivePath, "ReportArchive");
            }
            catch (Exception ex)
            {
                cat.Notes = AppendNote(cat.Notes, "measurement failed: " + ex.Message);
            }
            Finish(results, cat);
        }

        // e) Old update log archives: CbsPersist_*.cab older than 30 days in
        //    C:\Windows\Logs\CBS plus all files under C:\Windows\Logs\WindowsUpdate.
        private static void MeasureUpdateLogArchives(List<CleanCategory> results)
        {
            CleanCategory cat = new CleanCategory("Old update log archives", CleanCategory.KindWu,
                "low (log files only)");
            try
            {
                MeasureOldCbsCabs(cat);
                MeasureTreeInto(cat, WuLogsPath, "WindowsUpdate logs");
                cat.Notes = AppendNote(cat.Notes,
                    "CbsPersist_*.cab older than " + CabMaxAgeDays + " days, plus all WindowsUpdate logs");
            }
            catch (Exception ex)
            {
                cat.Notes = AppendNote(cat.Notes, "measurement failed: " + ex.Message);
            }
            Finish(results, cat);
        }

        // f) Update reporting log: ReportingEvents.log.
        private static void MeasureReportingEventsLog(List<CleanCategory> results)
        {
            CleanCategory cat = new CleanCategory("Update reporting log", CleanCategory.KindWu,
                "low (single log file)");
            try
            {
                MeasureFileInto(cat, "ReportingEvents.log", ReportingEventsPath);
            }
            catch (Exception ex)
            {
                cat.Notes = AppendNote(cat.Notes, "measurement failed: " + ex.Message);
            }
            Finish(results, cat);
        }

        // g) User temp files: %TEMP% (all children).
        private static void MeasureUserTemp(List<CleanCategory> results)
        {
            CleanCategory cat = new CleanCategory("User temp files", CleanCategory.KindDeepClean,
                "low (current user temp)");
            try
            {
                string temp = Path.GetTempPath();
                if (temp.Length > 3 && temp.EndsWith("\\", StringComparison.Ordinal))
                {
                    temp = temp.Substring(0, temp.Length - 1);
                }
                MeasureTreeInto(cat, temp, null);
                cat.Notes = AppendNote(cat.Notes, "%TEMP% = " + temp);
            }
            catch (Exception ex)
            {
                cat.Notes = AppendNote(cat.Notes, "measurement failed: " + ex.Message);
            }
            Finish(results, cat);
        }

        // h) Crash dumps: C:\Windows\MEMORY.DMP + C:\Windows\Minidump\*.
        private static void MeasureCrashDumps(List<CleanCategory> results)
        {
            CleanCategory cat = new CleanCategory("Crash dumps", CleanCategory.KindDeepClean,
                "low (debug data only)");
            try
            {
                MeasureFileInto(cat, "MEMORY.DMP", MemoryDumpPath);
                MeasureTreeInto(cat, MinidumpPath, "Minidump");
            }
            catch (Exception ex)
            {
                cat.Notes = AppendNote(cat.Notes, "measurement failed: " + ex.Message);
            }
            Finish(results, cat);
        }

        // i) Thumbnail caches: thumbcache_/iconcache_ files under
        //    %LOCALAPPDATA%\Microsoft\Windows\Explorer (Explorer usually
        //    locks these - the cleaner must handle the locks).
        private static void MeasureThumbnailCaches(List<CleanCategory> results)
        {
            CleanCategory cat = new CleanCategory("Thumbnail caches", CleanCategory.KindDeepClean,
                "low (rebuilt automatically; often locked by Explorer)");
            try
            {
                string explorerDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Path.Combine("Microsoft", Path.Combine("Windows", "Explorer")));
                MeasureThumbCacheFiles(cat, explorerDir);
                cat.Notes = AppendNote(cat.Notes,
                    "thumbcache_*.db / iconcache_*.db - Explorer usually locks these");
            }
            catch (Exception ex)
            {
                cat.Notes = AppendNote(cat.Notes, "measurement failed: " + ex.Message);
            }
            Finish(results, cat);
        }

        // j) GPU shader caches, one category per existing path (own path
        //    table for now; GpuTools.cs owns this area from Wave 5).
        private static void MeasureShaderCaches(List<CleanCategory> results)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            MeasureShaderCache(results, "NVIDIA shader cache (DXCache)",
                Path.Combine(localAppData, "NVIDIA", "DXCache"));
            MeasureShaderCache(results, "NVIDIA shader cache (GLCache)",
                Path.Combine(localAppData, "NVIDIA", "GLCache"));
            MeasureShaderCache(results, "NVIDIA shader cache (NV_Cache)",
                Path.Combine(commonAppData, "NVIDIA Corporation", "NV_Cache"));
            MeasureShaderCache(results, "AMD shader cache (DxCache)",
                Path.Combine(localAppData, "AMD", "DxCache"));
            MeasureShaderCache(results, "AMD shader cache (Dx9Cache)",
                Path.Combine(localAppData, "AMD", "Dx9Cache"));
            MeasureShaderCache(results, "AMD shader cache (GLCache)",
                Path.Combine(localAppData, "AMD", "GLCache"));
            MeasureShaderCache(results, "DirectX shader cache (D3DSCache)",
                Path.Combine(localAppData, "D3DSCache"));
        }

        // One shader-cache category, added only when the path exists; an
        // absent path logs "not present (skipped)" without a category.
        private static void MeasureShaderCache(List<CleanCategory> results, string name, string root)
        {
            CleanCategory cat = new CleanCategory(name, CleanCategory.KindGpu,
                "low (rebuilt automatically)");
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    Log.Chan("CLEAN", "measure '" + name + "': not present (skipped)");
                    return;
                }
                MeasureTreeInto(cat, root, null);
                Finish(results, cat);
            }
            catch (Exception ex)
            {
                cat.Notes = AppendNote(cat.Notes, "measurement failed: " + ex.Message);
                Finish(results, cat);
            }
        }

        // ---- per-target helpers ---------------------------------------------

        // CbsPersist_*.cab files in C:\Windows\Logs\CBS older than 30 days.
        private static void MeasureOldCbsCabs(CleanCategory cat)
        {
            if (!Directory.Exists(CbsLogsPath))
            {
                cat.Notes = AppendNote(cat.Notes, "CBS logs: not present");
                return;
            }
            DateTime cutoff = DateTime.Now - TimeSpan.FromDays(CabMaxAgeDays);
            Tally tally = new Tally();
            ForEachEntry(ToLongPath(CbsLogsPath), tally,
                delegate (string dir, string name, ref Win32FindData data)
                {
                    if ((data.FileAttributes & FileAttributeDirectory) != 0) return;
                    if (!name.StartsWith("CbsPersist_", StringComparison.OrdinalIgnoreCase)) return;
                    if (!name.EndsWith(".cab", StringComparison.OrdinalIgnoreCase)) return;
                    if (FileTimeToDateTime(data.LastWriteTime) >= cutoff) return;
                    tally.Bytes += FileSizeOf(data);
                    tally.Files++;
                });
            MergeTally(cat, tally);
        }

        // thumbcache_/iconcache_ files in the Explorer cache folder.
        private static void MeasureThumbCacheFiles(CleanCategory cat, string dirPath)
        {
            if (!Directory.Exists(dirPath))
            {
                cat.Notes = AppendNote(cat.Notes, "not present");
                return;
            }
            Tally tally = new Tally();
            ForEachEntry(ToLongPath(dirPath), tally,
                delegate (string dir, string name, ref Win32FindData data)
                {
                    if ((data.FileAttributes & FileAttributeDirectory) != 0) return;
                    if (!name.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("iconcache_", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    tally.Bytes += FileSizeOf(data);
                    tally.Files++;
                });
            MergeTally(cat, tally);
        }

        // ---- generic measurement helpers --------------------------------------

        // Recursively measures a whole directory tree into the category.
        // Missing roots get a "not present" note (missingLabel prefixes it
        // for multi-root categories); passes null for plain "not present".
        private static void MeasureTreeInto(CleanCategory cat, string root, string missingLabel)
        {
            if (!Directory.Exists(root))
            {
                cat.Notes = AppendNote(cat.Notes,
                    string.IsNullOrEmpty(missingLabel) ? "not present" : missingLabel + ": not present");
                return;
            }
            Tally tally = new Tally();
            WalkDirectory(ToLongPath(root), tally, DateTime.MinValue);
            MergeTally(cat, tally);
        }

        // Recursively measures a directory tree with an age filter at the
        // top level: files and folders whose last write time is older than
        // maxAge are counted (folders with all of their contents).
        private static void MeasureAgedTreeInto(CleanCategory cat, string root, string missingLabel, TimeSpan maxAge)
        {
            if (!Directory.Exists(root))
            {
                cat.Notes = AppendNote(cat.Notes,
                    string.IsNullOrEmpty(missingLabel) ? "not present" : missingLabel + ": not present");
                return;
            }
            Tally tally = new Tally();
            WalkDirectory(ToLongPath(root), tally, DateTime.Now - maxAge);
            MergeTally(cat, tally);
        }

        // Measures one single file into the category (missing -> note).
        private static void MeasureFileInto(CleanCategory cat, string label, string filePath)
        {
            Win32FileAttributeData data;
            if (!GetFileAttributesExW(ToLongPath(filePath), GetFileExInfoStandard, out data))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorFileNotFound || error == ErrorPathNotFound)
                {
                    cat.Notes = AppendNote(cat.Notes,
                        string.IsNullOrEmpty(label) ? "not present" : label + ": not present");
                }
                else
                {
                    cat.Notes = AppendNote(cat.Notes,
                        label + " not counted (" + new Win32Exception(error).Message + ")");
                }
                return;
            }
            cat.Bytes += ((long)data.FileSizeHigh << 32) | (long)data.FileSizeLow;
            cat.Files++;
        }

        // Moves a tally's numbers into the category and appends its notes.
        private static void MergeTally(CleanCategory cat, Tally tally)
        {
            cat.Bytes += tally.Bytes;
            cat.Files += tally.Files;
            cat.Notes = AppendNote(cat.Notes, tally.Describe());
        }

        // Adds the category to the results and writes its single CLEAN line:
        // measure '<Name>': <SizeText> in <N> files [- <Notes>].
        private static void Finish(List<CleanCategory> results, CleanCategory cat)
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

        private static string AppendNote(string existing, string addition)
        {
            if (string.IsNullOrEmpty(addition)) return existing == null ? "" : existing;
            if (string.IsNullOrEmpty(existing)) return addition;
            return existing + "; " + addition;
        }

        // ---- directory walking (kernel32, long-path safe) -----------------------

        // Recursively sums file sizes and counts into the tally. When
        // topLevelCutoff is set, only top-level entries whose last write is
        // older are counted (folders with all of their contents); deeper
        // levels are never age-filtered. Reparse points (junctions/symlinks)
        // are skipped (loop safety); enumeration failures land in the tally.
        private static void WalkDirectory(string dirPath, Tally tally, DateTime topLevelCutoff)
        {
            ForEachEntry(dirPath, tally,
                delegate (string dir, string name, ref Win32FindData data)
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
                    WalkDirectory(dir + "\\" + name, tally, DateTime.MinValue);
                });
        }

        // Enumerates one directory level through FindFirstFileW (which takes
        // \\?\ paths natively) and calls visit for every entry except
        // "."/"..". Returns false when the directory itself could not be
        // opened (the win32 reason is recorded in the tally); the find
        // handle is always closed before returning.
        private static bool ForEachEntry(string dirPath, Tally tally, FindDataVisitor visit)
        {
            Win32FindData data;
            IntPtr handle = FindFirstFileW(dirPath + "\\*", out data);
            if (handle == InvalidFindHandle)
            {
                tally.AddError(dirPath + ": not enumerated",
                    new Win32Exception(Marshal.GetLastWin32Error()));
                return false;
            }
            try
            {
                int lastError = 0;
                bool more = true;
                while (more)
                {
                    string name = data.FileName;
                    if (!string.Equals(name, ".") && !string.Equals(name, ".."))
                    {
                        visit(dirPath, name, ref data);
                    }
                    more = FindNextFileW(handle, out data);
                    if (!more) lastError = Marshal.GetLastWin32Error();
                }
                if (lastError != 0 && lastError != ErrorNoMoreFiles)
                {
                    tally.AddError(dirPath + ": enumeration ended early", new Win32Exception(lastError));
                }
            }
            finally
            {
                FindClose(handle);
            }
            return true;
        }

        private delegate void FindDataVisitor(string dirPath, string name, ref Win32FindData data);

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
        // enumeration tolerates paths beyond MAX_PATH (files deep under
        // SoftwareDistribution). Only canonical local drive paths (C:\...)
        // are prefixed; anything else is returned unchanged.
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

        // Strips the \\?\ extended-length prefix for display in notes.
        private static string DisplayPath(string path)
        {
            if (path != null && path.StartsWith("\\\\?\\", StringComparison.Ordinal))
            {
                return path.Substring(4);
            }
            return path;
        }

        // ---- gate helpers -------------------------------------------------------

        // True when the HKLM registry key exists (does not swallow errors -
        // callers fail closed on exceptions).
        private static bool RegistryKeyExists(string keyPath)
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(keyPath))
            {
                return k != null;
            }
        }

        // True when Session Manager's PendingFileRenameOperations value
        // holds at least one non-empty entry.
        private static bool PendingFileRenamesPending()
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(SessionManagerRegistryKey))
            {
                if (k == null) return false;
                object raw = k.GetValue("PendingFileRenameOperations");
                string[] ops = raw as string[];
                if (ops != null)
                {
                    foreach (string op in ops)
                    {
                        if (!string.IsNullOrEmpty(op)) return true;
                    }
                    return false;
                }
                string single = raw as string;
                return !string.IsNullOrEmpty(single);
            }
        }

        // ---- win32 interop (kernel32 is an established pattern here) -------------

        private const int GetFileExInfoStandard = 0;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorNoMoreFiles = 18;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeReparsePoint = 0x00000400;
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

        // ---- tally ---------------------------------------------------------------

        // Running totals for one measurement walk. Per-item failures are
        // swallowed here and summarized (at most MaxErrorSamples verbatim
        // samples, then "and N more").
        private sealed class Tally
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
    }
}
