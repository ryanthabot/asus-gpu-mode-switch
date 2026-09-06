//  GpuTools.cs  (v1.1.0 - Wave 5, agent A11)
//  ------------------------------------------
//  GPU maintenance tools: shader-cache measurement + cleanup, NVIDIA driver
//  installer leftover cleanup and the Hardware-Accelerated GPU Scheduling
//  (HAGS) toggle. Everything logs through the GPU channel (the channel
//  AsusControl uses); the "gputools" / "measure" / "hags" message prefixes
//  keep these lines apart from the transport lines inside the log.
//
//  CANONICAL GPU CACHE PATH TABLE (this file owns the cleaning paths):
//      NVIDIA:  %LOCALAPPDATA%\NVIDIA\DXCache
//               %LOCALAPPDATA%\NVIDIA\GLCache
//               C:\ProgramData\NVIDIA Corporation\NV_Cache
//      AMD:     %LOCALAPPDATA%\AMD\DxCache
//               %LOCALAPPDATA%\AMD\Dx9Cache
//               %LOCALAPPDATA%\AMD\GLCache
//      DirectX: %LOCALAPPDATA%\D3DSCache
//      Driver installer leftovers (SEPARATE, confirm-flagged category):
//               C:\NVIDIA                                       (installer extraction dirs)
//               C:\ProgramData\NVIDIA Corporation\Downloader    (driver download cache)
//  The seven shader-cache categories deliberately reuse the exact names
//  StorageAnalyzer.MeasureAll() produced for them, so the Wave 6 UI sees
//  one set of names across analyzer and cleaner. StorageAnalyzer keeps its
//  own parallel shader path list purely for measuring - accepted
//  duplication; THIS table is canonical for cleaning, so a path change
//  happens here first and is mirrored into the analyzer's measure pass.
//
//  Deletion model: the cache directories' CONTENTS are deleted recursively
//  (per-item try/catch; locked/in-use files are skipped + WARNed and
//  tallied; access-denied gets one retry with the read-only attribute
//  cleared); the cache directories themselves are kept - the graphics
//  drivers rebuild their caches automatically. NO running-process check is
//  needed for shader caches: when Windows deletes a file that is still
//  open, the file remains on disk until the last handle closes, so an
//  in-use cache entry never disappears under a running driver (and locked
//  entries are skipped anyway).
//
//  Driver installer leftovers hold downloaded driver installers; they are
//  deleted ONLY when Clean(includeDriverLeftovers: true) - they are NOT
//  part of the normal checkbox flow, because deleting them means
//  re-downloading a driver costs bandwidth the next time it is needed.
//  Measure() always reports their size so the UI can show what the confirm
//  flag would free.
//
//  D7 HARD GUARD before EVERY deletion (belt-and-braces, same approach as
//  StorageCleaner; the fixed path table above lies entirely outside this
//  set, so a hit can only ever be a programming error):
//      C:\Windows\WinSxS                                  (anything inside)
//      C:\Windows\System32\catroot
//      C:\Windows\System32\catroot2
//      C:\Windows\Installer
//      C:\Windows\Servicing
//      C:\Windows\SoftwareDistribution\DataStore
//      any path containing "pending.xml"
//      blank or relative paths (fail closed)
//  A guard hit logs "GUARD: refusing <path>" and aborts that item.
//
//  Gates: Clean() re-checks StorageAnalyzer.CheckGates() itself (never
//  trusts the caller); any block reason -> every reason is logged as an
//  ERROR, nothing is deleted and Clean returns false.
//
//  HAGS: Hardware-Accelerated GPU Scheduling is the DWORD value HwSchMode
//  under HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers
//  (2 = On, 1 = Off). SetHags writes the value (the app runs elevated per
//  the requireAdministrator manifest) and the change takes effect only
//  after the next reboot. The value is NEVER deleted: turning HAGS off
//  writes 1, because deleting the value would revert to the Windows
//  default, which is interpreted differently across Windows builds and GPU
//  drivers.
//
//  Failure model: Measure() never throws (per-location try/catch, per-item
//  failures swallowed into Notes); Clean() returns false only when the
//  gates block the run, all other failures degrade into WARNs and result
//  notes. Thread safety: no mutable static state except the
//  _hagsSetThisSession flag, so both methods are safe to call from a
//  background thread (Wave 6 runs cleanup via RunBg).
//
//  Long paths are enumerated AND deleted through kernel32 FindFirstFileW /
//  DeleteFileW / RemoveDirectoryW with the \\?\ extended-length prefix -
//  .NET 4.x rejects that prefix in its IO object model, so the interop is
//  duplicated here file-privately (same structs and conventions as
//  StorageAnalyzer.cs / StorageCleaner.cs: reparse points are never
//  followed or descended, per-item failures are swallowed).
//
//  Compile verification only this wave - Clean() and SetHags() are never
//  executed here (they delete files / write the registry); the only
//  runtime exercise is a read-only Measure() smoke run in an out-of-tree
//  harness (no BeginSession -> no disk writes). New file for v1.1.0
//  (Wave 5, A11) per docs\HANDBOOK.md sections 2, 3, 4 (D3/D7) and 8.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // GPU tools (static): shader-cache measure/clean over the canonical
    // path table, confirm-flagged driver installer leftover cleanup and the
    // HAGS registry toggle. See the file header for the safety model.
    // ---------------------------------------------------------------------
    public static class GpuTools
    {
        // ---- log channel ----------------------------------------------------
        private const string Channel = "GPU";

        // ---- category names (shader caches match StorageAnalyzer.MeasureAll) --
        private const string CatNvidiaDxCache = "NVIDIA shader cache (DXCache)";
        private const string CatNvidiaGlCache = "NVIDIA shader cache (GLCache)";
        private const string CatNvidiaNvCache = "NVIDIA shader cache (NV_Cache)";
        private const string CatAmdDxCache = "AMD shader cache (DxCache)";
        private const string CatAmdDx9Cache = "AMD shader cache (Dx9Cache)";
        private const string CatAmdGlCache = "AMD shader cache (GLCache)";
        private const string CatDirect3DSCache = "DirectX shader cache (D3DSCache)";
        private const string CatNvidiaInstallerLeftovers = "NVIDIA driver installer leftovers (C:\\NVIDIA)";
        private const string CatNvidiaDownloader = "NVIDIA driver download cache (Downloader)";

        // ---- HAGS registry location -------------------------------------------
        private const string GraphicsDriversKeyPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
        private const string HwSchModeValueName = "HwSchMode";
        private const int HwSchModeOn = 2;
        private const int HwSchModeOff = 1;

        private const int MaxErrorSamples = 5;      // verbatim failure samples per location

        // Set by SetHags after a successful write; RebootRequiredForHags()
        // reports it (per-session only - a fresh app run reads the registry).
        private static bool _hagsSetThisSession;

        // ---- canonical path table ---------------------------------------------

        // One row of the canonical path table. DriverLeftover rows are cleaned
        // only on the confirm flag (see the file header).
        private sealed class GpuLocation
        {
            public readonly string Name;
            public readonly string Path;
            public readonly bool DriverLeftover;

            public GpuLocation(string name, string path, bool driverLeftover)
            {
                Name = name;
                Path = path;
                DriverLeftover = driverLeftover;
            }
        }

        // The canonical table, resolved per session (the user profile paths
        // differ per user). Order is stable and matches Measure()'s output.
        private static List<GpuLocation> BuildLocations()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            List<GpuLocation> locations = new List<GpuLocation>();
            locations.Add(new GpuLocation(CatNvidiaDxCache,
                Path.Combine(localAppData, Path.Combine("NVIDIA", "DXCache")), false));
            locations.Add(new GpuLocation(CatNvidiaGlCache,
                Path.Combine(localAppData, Path.Combine("NVIDIA", "GLCache")), false));
            locations.Add(new GpuLocation(CatNvidiaNvCache,
                Path.Combine(commonAppData, Path.Combine("NVIDIA Corporation", "NV_Cache")), false));
            locations.Add(new GpuLocation(CatAmdDxCache,
                Path.Combine(localAppData, Path.Combine("AMD", "DxCache")), false));
            locations.Add(new GpuLocation(CatAmdDx9Cache,
                Path.Combine(localAppData, Path.Combine("AMD", "Dx9Cache")), false));
            locations.Add(new GpuLocation(CatAmdGlCache,
                Path.Combine(localAppData, Path.Combine("AMD", "GLCache")), false));
            locations.Add(new GpuLocation(CatDirect3DSCache,
                Path.Combine(localAppData, "D3DSCache"), false));
            locations.Add(new GpuLocation(CatNvidiaInstallerLeftovers, @"C:\NVIDIA", true));
            locations.Add(new GpuLocation(CatNvidiaDownloader,
                Path.Combine(commonAppData, Path.Combine("NVIDIA Corporation", "Downloader")), true));
            return locations;
        }

        private static GpuLocation FindLocation(List<GpuLocation> table, string name)
        {
            if (name == null) return null;
            foreach (GpuLocation loc in table)
            {
                if (string.Equals(loc.Name, name, StringComparison.Ordinal)) return loc;
            }
            return null;
        }

        // ---- API: measurement (read-only) ---------------------------------------

        // Measures every location in the canonical path table (read-only) and
        // returns one CleanCategory (Kind "gpu") per PRESENT location; a
        // location that does not exist logs "not present (skipped)" without a
        // category - the same convention StorageAnalyzer uses for its shader
        // caches. Never throws; per-item failures are swallowed into Notes.
        // One [GPU] "measure '<name>': ..." line per location.
        public static List<CleanCategory> Measure()
        {
            List<CleanCategory> results = new List<CleanCategory>();
            try
            {
                foreach (GpuLocation loc in BuildLocations())
                {
                    MeasureLocation(results, loc);
                }
            }
            catch (Exception ex)
            {
                Log.Error("gputools: measure failed", ex);
            }
            return results;
        }

        // Measures one location; a missing path logs "not present (skipped)"
        // and adds no category, everything found is tallied into a category.
        private static void MeasureLocation(List<CleanCategory> results, GpuLocation loc)
        {
            CleanCategory cat = new CleanCategory(loc.Name, CleanCategory.KindGpu,
                loc.DriverLeftover
                    ? "medium (holds driver installers; confirm-flagged)"
                    : "low (rebuilt automatically)");
            try
            {
                if (string.IsNullOrEmpty(loc.Path) || !Directory.Exists(loc.Path))
                {
                    Log.Chan(Channel, "measure '" + loc.Name + "': not present (skipped)");
                    return;     // no category - StorageAnalyzer's convention
                }
                Tally tally = new Tally();
                WalkAndMeasure(ToLongPath(loc.Path), tally);
                cat.Bytes = tally.Bytes;
                cat.Files = tally.Files;
                cat.Notes = AppendNote(cat.Notes, tally.DescribeMeasure());
                if (loc.DriverLeftover)
                {
                    cat.Notes = AppendNote(cat.Notes,
                        "driver installer leftover - cleaned only with the driver-leftovers " +
                        "confirm flag (re-downloading a driver costs bandwidth)");
                }
                FinishMeasure(results, cat);
            }
            catch (Exception ex)
            {
                cat.Notes = AppendNote(cat.Notes, "measurement failed: " + ex.Message);
                FinishMeasure(results, cat);
            }
        }

        // Adds the category to the results and writes its single [GPU] line
        // (same shape as StorageAnalyzer's CLEAN measure lines).
        private static void FinishMeasure(List<CleanCategory> results, CleanCategory cat)
        {
            results.Add(cat);
            string line = "measure '" + cat.Name + "': " + cat.SizeText + " in " +
                cat.Files.ToString("N0", CultureInfo.InvariantCulture) + " files";
            if (!string.IsNullOrEmpty(cat.Notes))
            {
                line += " - " + cat.Notes;
            }
            Log.Chan(Channel, line);
        }

        // ---- API: cleaning --------------------------------------------------------

        // Executes the selected GPU locations. Returns false ONLY when the
        // safety gates block the run (nothing is deleted in that case);
        // individual location failures are logged into their results and
        // never change the return value. Categories with Selected == false
        // are skipped. The driver installer leftovers are deleted only when
        // includeDriverLeftovers is true (they are NOT part of the normal
        // checkbox flow - deleting them means re-downloading a driver costs
        // bandwidth). One [GPU] "gputools '<name>': ..." line per location
        // plus the final total line with before/after free space.
        public static bool Clean(List<CleanCategory> selected, bool includeDriverLeftovers,
            out List<CleanResult> results, out long totalBytesFreed)
        {
            results = new List<CleanResult>();
            totalBytesFreed = 0L;

            // Gate re-check - never trust the caller. Any block reason:
            // nothing is deleted at all.
            List<string> reasons = StorageAnalyzer.CheckGates();
            if (reasons != null && reasons.Count > 0)
            {
                foreach (string reason in reasons)
                {
                    Log.Error("gputools: gate blocks cleanup - " + reason);
                }
                Log.Chan(Channel, "gputools: nothing was deleted (safety gates blocked the run)");
                return false;
            }

            if (selected == null || selected.Count == 0)
            {
                Log.Chan(Channel, "gputools: no categories selected - nothing to do");
                return true;
            }

            Log.Chan(Channel, "gputools: cleaning " + CountSelected(selected) + " selected location(s) - " +
                "driver leftovers " + (includeDriverLeftovers
                    ? "INCLUDED (re-downloading a driver costs bandwidth)"
                    : "excluded (confirm flag not set)"));

            List<GpuLocation> table = BuildLocations();
            long before = FreeSpaceOfSystemDrive();

            foreach (CleanCategory cat in selected)
            {
                if (cat == null) continue;
                if (!cat.Selected)
                {
                    Log.Chan(Channel, "gputools '" + cat.Name + "': not selected - skipped");
                    continue;
                }
                CleanResult r = CleanOne(cat, table, includeDriverLeftovers);
                results.Add(r);
                totalBytesFreed += r.BytesFreed;
                string line = "gputools '" + cat.Name + "': " + r.Summary;
                if (!string.IsNullOrEmpty(r.Notes))
                {
                    line += " - " + r.Notes;
                }
                Log.Chan(Channel, line);
            }

            long after = FreeSpaceOfSystemDrive();
            if (before >= 0 && after >= 0)
            {
                long delta = after - before;
                Log.Chan(Channel, "gputools: total " + StorageCleaner.FormatBytes(totalBytesFreed) +
                    " freed; free space before " + StorageCleaner.FormatBytes(before) +
                    " -> after " + StorageCleaner.FormatBytes(after) +
                    " (delta " + (delta >= 0 ? "+" : "-") + StorageCleaner.FormatBytes(Math.Abs(delta)) + ")");
            }
            else
            {
                Log.Chan(Channel, "gputools: total " + StorageCleaner.FormatBytes(totalBytesFreed) +
                    " freed; free space before/after unknown (drive information unavailable)");
            }
            return true;
        }

        private static int CountSelected(List<CleanCategory> selected)
        {
            int n = 0;
            foreach (CleanCategory cat in selected)
            {
                if (cat != null && cat.Selected) n++;
            }
            return n;
        }

        // Cleans one location against the canonical table: the CONTENTS of
        // the cache directory are deleted recursively, the directory itself
        // is kept (drivers rebuild their caches).
        private static CleanResult CleanOne(CleanCategory cat, List<GpuLocation> table,
            bool includeDriverLeftovers)
        {
            GpuLocation loc = FindLocation(table, cat.Name);
            if (loc == null)
            {
                return new CleanResult(cat.Name, 0, 0, 0,
                    "unknown location - GpuTools has no path-table entry for this name, nothing was done");
            }
            if (loc.DriverLeftover && !includeDriverLeftovers)
            {
                return new CleanResult(cat.Name, 0, 0, 0,
                    "driver leftover kept - cleaned only with the driver-leftovers confirm flag " +
                    "(re-downloading a driver costs bandwidth)");
            }
            if (IsForbiddenPath(loc.Path))
            {
                Log.Error("GUARD: refusing " + DisplayPath(loc.Path));
                return new CleanResult(cat.Name, 0, 0, 0, "refused by the D7 guard");
            }
            if (string.IsNullOrEmpty(loc.Path) || !Directory.Exists(loc.Path))
            {
                return new CleanResult(cat.Name, 0, 0, 0, "not present");
            }
            Tally tally = new Tally();
            DeleteContents(loc.Path, tally);
            string notes = loc.DriverLeftover
                ? "driver installer leftovers, contents only (the folder itself is kept) - " +
                  "re-downloading a driver costs bandwidth"
                : "cache rebuilt automatically by the graphics driver (contents only, the folder itself is kept)";
            if (tally.Files == 0 && tally.Skipped == 0 && tally.FoldersDeleted == 0 && tally.Refused == 0)
            {
                notes = AppendNote(notes, "already empty");
            }
            return FinishCategory(cat.Name, notes, tally);
        }

        // Moves a tally into the CleanResult for one location.
        private static CleanResult FinishCategory(string categoryName, string notes, Tally tally)
        {
            CleanResult result = new CleanResult(categoryName, tally.Bytes, tally.Files, tally.Skipped, notes);
            result.Notes = AppendNote(notes, tally.DescribeClean());
            return result;
        }

        // ---- deletion walkers (guard-checked, long-path safe) ------------------------

        // Deletes every child of rootDir (files directly, directories after
        // their contents) - rootDir itself is NEVER deleted. Top-level
        // junctions/symlinks are skipped (never followed), deeper ones are
        // unlinked without descending into their target.
        private static void DeleteContents(string rootDir, Tally tally)
        {
            if (IsForbiddenPath(rootDir))
            {
                Log.Error("GUARD: refusing " + DisplayPath(rootDir));
                tally.Refused++;
                return;
            }
            ForEachEntry(rootDir, tally, delegate (string dir, string name, ref Win32FindData data)
            {
                bool isDir = (data.FileAttributes & FileAttributeDirectory) != 0;
                if (isDir && (data.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    tally.ReparseSkipped++;     // junctions/symlinks are never followed
                    return;
                }
                DeleteItem(dir, name, ref data, isDir, tally);
            });
        }

        // Guard + delete of one entry (file or directory) with per-item
        // exception containment: a failure is logged and tallied, never
        // thrown back into the walk.
        private static void DeleteItem(string dir, string name, ref Win32FindData data,
            bool isDir, Tally tally)
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
                Log.Warn("gputools: skipped " + DisplayPath(path) + " (" + ex.Message + ")");
            }
        }

        // Removes a directory AFTER its contents. A directory that stays
        // behind because contents were locked is counted in FoldersLeft (the
        // file-level warns already explain why). Access-denied gets one
        // retry with the read-only attribute cleared.
        private static void DeleteDirectoryEntry(string dirPath, Tally tally)
        {
            DeleteDirectoryContents(dirPath, tally);
            if (RemoveDirectoryW(dirPath))
            {
                tally.FoldersDeleted++;
                return;
            }
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorFileNotFound || error == ErrorPathNotFound) return;
            if (error == ErrorAccessDenied && TryClearReadOnly(dirPath))
            {
                if (RemoveDirectoryW(dirPath))
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
                Log.Warn("gputools: could not remove folder " + DisplayPath(dirPath) +
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
            ForEachEntry(dirPath, tally, delegate (string dir, string name, ref Win32FindData data)
            {
                bool isDir = (data.FileAttributes & FileAttributeDirectory) != 0;
                if (isDir && (data.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    // junction/symlink inside a purged tree: remove the link
                    // itself, never descend into its target
                    if (RemoveDirectoryW(dir + "\\" + name)) tally.FoldersDeleted++;
                    else tally.FoldersLeft++;
                    return;
                }
                DeleteItem(dir, name, ref data, isDir, tally);
            });
        }

        // Deletes one file and tallies the outcome. Files gone in the
        // meantime are silent; in-use files are WARNed and tallied as
        // skipped; access-denied gets one retry with the read-only attribute
        // cleared.
        private static void DeleteFileEntry(string filePath, long size, Tally tally)
        {
            if (DeleteFileW(filePath))
            {
                tally.Bytes += size;
                tally.Files++;
                return;
            }
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorFileNotFound || error == ErrorPathNotFound) return;   // already gone
            if (error == ErrorAccessDenied && TryClearReadOnly(filePath))
            {
                if (DeleteFileW(filePath))
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
            Log.Warn("gputools: skipped " + DisplayPath(filePath) + " (" + reason + ")");
        }

        private static bool TryClearReadOnly(string filePath)
        {
            try
            {
                return SetFileAttributesW(filePath, FileAttributeNormal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- measurement walk (read-only, long-path safe) -----------------------------

        // Recursively sums file sizes and counts into the tally; reparse
        // points are skipped (loop safety); enumeration failures land in
        // the tally (rendered into the category notes).
        private static void WalkAndMeasure(string dirPath, Tally tally)
        {
            ForEachEntry(dirPath, tally, delegate (string dir, string name, ref Win32FindData data)
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
                WalkAndMeasure(dir + "\\" + name, tally);
            });
        }

        // ---- directory enumeration (kernel32, long-path safe) ---------------------------

        private delegate void EntryVisitor(string dirPath, string name, ref Win32FindData data);

        // Enumerates one directory level through FindFirstFileW (which takes
        // \\?\ paths natively) and calls visit for every entry except
        // "."/"..". A directory that cannot be opened (or an enumeration
        // that ends early) is recorded in the tally AND warned once - the
        // measure pass surfaces it in the category notes, the clean pass in
        // its failure tally. The find handle is always closed.
        private static void ForEachEntry(string dirPath, Tally tally, EntryVisitor visit)
        {
            string longDir = ToLongPath(dirPath);
            Win32FindData data;
            IntPtr handle = FindFirstFileW(longDir + "\\*", out data);
            if (handle == InvalidFindHandle)
            {
                int error = Marshal.GetLastWin32Error();
                tally.EnumerationFailures++;
                tally.AddSample(dirPath, Win32Message(error));
                Log.Warn("gputools: could not enumerate " + DisplayPath(dirPath) +
                    " (" + Win32Message(error) + ")");
                return;
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
                        visit(longDir, name, ref data);
                    }
                    more = FindNextFileW(handle, out data);
                    if (!more) lastError = Marshal.GetLastWin32Error();
                }
                if (lastError != 0 && lastError != ErrorNoMoreFiles)
                {
                    tally.EnumerationFailures++;
                    tally.AddSample(dirPath, "enumeration ended early - " + Win32Message(lastError));
                }
            }
            finally
            {
                FindClose(handle);
            }
        }

        // ---- D7 hard guard -----------------------------------------------------------------

        // The D7 hard guard, checked before EVERY deletion (see the file
        // header for the list). Blank/relative paths fail closed. This can
        // only ever fire on a programming error - the fixed path table is
        // entirely outside the guarded set.
        private static bool IsForbiddenPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;    // blank fails closed
            string trimmed = path.Trim();
            if (!Path.IsPathRooted(trimmed)) return true;   // relative paths fail closed
            string p = trimmed;
            if (p.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase)) p = p.Substring(4);
            p = p.Replace('/', '\\').ToLowerInvariant();

            // Anything containing pending.xml (D7 never-touch list).
            if (p.Contains("pending.xml")) return true;

            if (IsUnderOrEqual(p, @"c:\windows\winsxs")) return true;
            if (IsUnderOrEqual(p, @"c:\windows\system32\catroot")) return true;
            if (IsUnderOrEqual(p, @"c:\windows\system32\catroot2")) return true;
            if (IsUnderOrEqual(p, @"c:\windows\installer")) return true;
            if (IsUnderOrEqual(p, @"c:\windows\servicing")) return true;
            if (IsUnderOrEqual(p, @"c:\windows\softwaredistribution\datastore")) return true;
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

        // ---- HAGS (Hardware-Accelerated GPU Scheduling) --------------------------------------

        // Current HAGS state for display, read from
        // HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode:
        //     2               -> "On"
        //     1               -> "Off"
        //     value absent    -> "Windows default (Off)"  (the Windows default
        //                        is Off on most builds; the hint makes that explicit)
        //     unexpected value or unreadable -> "Windows default"
        // Read-only; never throws.
        public static string HagsStateText()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(GraphicsDriversKeyPath))
                {
                    if (key == null) return "Windows default (Off)";
                    object raw = key.GetValue(HwSchModeValueName);
                    if (raw is int)
                    {
                        int mode = (int)raw;
                        if (mode == HwSchModeOn) return "On";
                        if (mode == HwSchModeOff) return "Off";
                        return "Windows default";
                    }
                    return "Windows default (Off)";     // value absent -> Windows default
                }
            }
            catch (Exception)
            {
                return "Windows default";               // state unreadable - do not guess
            }
        }

        // Writes HwSchMode = 2 (on) or 1 (off) under GraphicsDrivers. The
        // change takes effect after the next reboot. The value is NEVER
        // deleted - turning HAGS off writes 1, because deleting the value
        // would revert to the Windows default, which is interpreted
        // differently across builds and GPU drivers. Returns true when the
        // write succeeded.
        public static bool SetHags(bool on)
        {
            try
            {
                int value = on ? HwSchModeOn : HwSchModeOff;
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    GraphicsDriversKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree))
                {
                    if (key != null)
                    {
                        key.SetValue(HwSchModeValueName, value, RegistryValueKind.DWord);
                    }
                    else
                    {
                        // The GraphicsDrivers key exists on every normal
                        // install; create it so the value can still be
                        // written explicitly (belt-and-braces).
                        using (RegistryKey created = Registry.LocalMachine.CreateSubKey(
                            GraphicsDriversKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree))
                        {
                            if (created == null)
                            {
                                Log.Error("hags: could not open or create " + GraphicsDriversKeyPath +
                                    " - state not changed");
                                return false;
                            }
                            created.SetValue(HwSchModeValueName, value, RegistryValueKind.DWord);
                        }
                    }
                }
                _hagsSetThisSession = true;
                Log.Chan(Channel, "hags: set to " + (on ? "On" : "Off") + " (effective after reboot)");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("hags: writing " + HwSchModeValueName + " failed - state not changed", ex);
                return false;
            }
        }

        // True when SetHags succeeded this session - a HAGS change only
        // takes effect after the next reboot. A failed write does not set
        // the flag (nothing changed, so there is nothing to reboot for).
        // Intentionally per-session: a fresh app run reads the registry
        // state instead. Simple helper for the Wave 6 UI.
        public static bool RebootRequiredForHags()
        {
            return _hagsSetThisSession;
        }

        // ---- formatting / small helpers --------------------------------------------------

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

        private static string AppendNote(string existing, string addition)
        {
            if (string.IsNullOrEmpty(addition)) return existing == null ? "" : existing;
            if (string.IsNullOrEmpty(existing)) return addition;
            return existing + "; " + addition;
        }

        private static long FileSizeOf(Win32FindData data)
        {
            return ((long)data.FileSizeHigh << 32) | (long)data.FileSizeLow;
        }

        // Returns the path with the \\?\ extended-length prefix so that
        // enumeration and deletion tolerate paths beyond MAX_PATH. Only
        // canonical local drive paths (C:\...) are prefixed; anything else
        // is returned unchanged.
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

        // ---- win32 interop (kernel32; same pattern as StorageAnalyzer.cs / StorageCleaner.cs) ----

        private const int ErrorAccessDenied = 5;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorSharingViolation = 32;
        private const int ErrorLockViolation = 33;
        private const int ErrorDirNotEmpty = 145;
        private const int ErrorNoMoreFiles = 18;
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

        // ---- tally ---------------------------------------------------------------------------

        // Running totals for one measure or clean pass over one location.
        // Per-item failures land in capped verbatim samples
        // (MaxErrorSamples); DescribeMeasure / DescribeClean render the
        // measure-side (analyzer wording) and clean-side (cleaner wording)
        // summaries - a tally instance is never used for both passes at
        // once, so the two sample sets never mix.
        private sealed class Tally
        {
            public long Bytes;
            public int Files;
            public int Skipped;                 // files that could not be deleted
            public int FoldersDeleted;
            public int FoldersLeft;             // directories that could not be removed
            public int EnumerationFailures;     // directories that could not be opened / ended early
            public int Refused;                 // D7 guard refusals
            public int ReparseSkipped;
            public List<string> Samples = new List<string>();

            public void AddSample(string path, string reason)
            {
                if (Samples.Count >= MaxErrorSamples) return;
                string msg = reason == null ? "unknown error" : reason;
                if (msg.Length > 120) msg = msg.Substring(0, 117) + "...";
                Samples.Add(DisplayPath(path) + " (" + msg + ")");
            }

            // "" when the walk was clean, otherwise e.g.
            // "1 item(s) not counted - <path> (error); 2 junction/symlink folder(s) skipped".
            public string DescribeMeasure()
            {
                List<string> parts = new List<string>();
                if (EnumerationFailures > 0)
                {
                    string detail = "";
                    if (Samples.Count > 0)
                    {
                        detail = " - " + string.Join("; ", Samples.ToArray());
                        if (EnumerationFailures > Samples.Count)
                        {
                            detail += "; and " + (EnumerationFailures - Samples.Count) + " more";
                        }
                    }
                    parts.Add(EnumerationFailures.ToString("N0", CultureInfo.InvariantCulture) +
                        " item(s) not counted" + detail);
                }
                if (ReparseSkipped > 0)
                {
                    parts.Add(ReparseSkipped.ToString("N0", CultureInfo.InvariantCulture) +
                        " junction/symlink folder(s) skipped");
                }
                return string.Join("; ", parts.ToArray());
            }

            // "" when nothing notable happened, otherwise e.g.
            // "3 file(s) skipped - <samples>[; and N more]; 1 folder(s) could not be removed".
            public string DescribeClean()
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
                if (EnumerationFailures > 0)
                {
                    parts.Add(EnumerationFailures.ToString("N0", CultureInfo.InvariantCulture) +
                        " folder(s) could not be enumerated");
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
