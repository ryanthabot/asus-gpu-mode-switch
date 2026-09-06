//  AppCacheCleaner.cs  (v1.1.0 - Wave 5, agent A9)
//  ------------------------------------------------
//  Per-app browser/launcher cache cleaner (Chrome, Edge, Brave, Opera,
//  Vivaldi, Firefox, Steam, Discord, Epic Games Launcher, Battle.net).
//
//  D5 CACHE-ONLY GUARANTEE - THE hard safety rule of this module. Only
//  cache content is ever touched; cookies, browsing history, saved
//  passwords/autofill, active login sessions, bookmarks/favorites and
//  localStorage/IndexedDB user data are untouchable. Two independent walls
//  enforce this:
//
//  1) THE WHITELIST IS THE D5 ENFORCEMENT. A cache-dir candidate is ONLY
//     ever one of the literal names below, resolved under the explicit
//     per-app parent paths in this file:
//         Cache, Code Cache, GPUCache, DawnCache, GrShaderCache,
//         ShaderCache, Media Cache, cache2, startupCache, htmlcache,
//         webcache* (webcache / webcache_414x / webcache_443x),
//         shadercache, depotcache
//     There is NO globbing beyond enumerating profile dirs under the app's
//     own "User Data" / "Profiles" folder. Nothing else is ever considered;
//     a candidate whose leaf name is not on this whitelist is refused with
//     a WARN even before the second wall runs. (Example of the rule at
//     work: Battle.net also has cache-like dirs that are NOT on this
//     whitelist - they are skipped, per D5 "when in doubt: skip and log".)
//
//  2) FORBIDDEN-NAME WALL (defense in depth). IsForbiddenName() holds the
//     personal-data names (cookies, history, "login data", sessions,
//     bookmarks, "local storage", indexeddb, "web data", places.sqlite,
//     cookies.sqlite, key3.db/key4.db, logins.json, formhistory.sqlite,
//     "sync data", ...). Every candidate dir AND every path segment AND
//     every child file/dir name is checked right before any enumeration or
//     delete; any hit -> Log.Warn "appcache: forbidden name '<name>'
//     blocked" and skip. Even if a future path table has a bug, this wall
//     prevents personal-data deletion.
//
//  Deletion model: Clean() removes the CONTENTS of the resolved cache dirs
//  (the cache dirs themselves are kept) with per-item try/catch - locked
//  files are skipped and WARNed, never fatal. A target whose app is
//  currently running (any of its process names alive) is NEVER cleaned -
//  skipped with a WARN; close the app first. Reparse points (junctions/
//  symlinks) are never followed or deleted. Nothing in this file ever
//  throws out of its public methods; failures degrade into notes and WARNs.
//
//  Log contract (channel CLEAN): one line per target when measuring
//      measure '<Name>': <SizeText> in <N> files [- <Notes>]
//      measure '<Name>': not present (skipped)
//  and per target when cleaning
//      appcache '<Name>': <bytes> freed, N files deleted, M skipped
//  plus a final total line. Missing installs are skipped with a note
//  (never scanned for on disk).
//
//  New in v1.1.0 Wave 5 per docs\HANDBOOK.md section 4 (D5) and section 8.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // One cleanable app target. Name is the display name (also the key the
    // Wave 6 UI passes back into Clean), ProcessNames are the process names
    // (without .exe) that block cleaning while running, CacheDirs are the
    // RESOLVED absolute cache directories produced by AppCacheCleaner.
    // Targets() (env vars expanded, profile subdirs enumerated, only dirs
    // that exist kept; empty when the app is not installed).
    // ---------------------------------------------------------------------
    public class AppCacheTarget
    {
        public string Name;
        public string[] ProcessNames;
        public string[] CacheDirs;

        public AppCacheTarget(string name, string[] processNames, string[] cacheDirs)
        {
            Name = name == null ? "" : name;
            ProcessNames = processNames == null ? new string[0] : processNames;
            CacheDirs = cacheDirs == null ? new string[0] : cacheDirs;
        }
    }

    // ---------------------------------------------------------------------
    // Result of cleaning one target - plain data holder for the Wave 6 UI
    // and SessionHistory. (Own DTO instead of reusing CleanCategory: this
    // is a delete result, not a measurement; BytesFreed/FilesDeleted/
    // FilesSkipped say exactly what happened.)
    // ---------------------------------------------------------------------
    public class AppCacheCleanResult
    {
        public string Name;
        public long BytesFreed;
        public int FilesDeleted;
        public int FilesSkipped;
        public string Notes;

        public AppCacheCleanResult()
        {
            Name = "";
            BytesFreed = 0;
            FilesDeleted = 0;
            FilesSkipped = 0;
            Notes = "";
        }

        public AppCacheCleanResult(string name) : this()
        {
            Name = name == null ? "" : name;
        }
    }

    // ---------------------------------------------------------------------
    // App cache cleaner (static). Measure() is read-only (sizes + running
    // warnings); Clean(list, out totalBytesFreed) deletes cache CONTENTS
    // only. Both go through the whitelist + forbidden-name walls below.
    // ---------------------------------------------------------------------
    public static class AppCacheCleaner
    {
        // ---- the literal cache-name whitelist (D5 enforcement, see header)
        // Stored lowercase; comparisons are case-insensitive.
        private static readonly string[] WhitelistedCacheNames =
        {
            "cache", "code cache", "gpucache", "dawncache", "grshadercache",
            "shadercache", "media cache", "cache2", "startupcache",
            "htmlcache", "shadercache", "depotcache"
            // plus the "webcache" prefix rule in IsWhitelistedCacheName
            // (Epic's webcache / webcache_414x / webcache_443x variants).
        };
        private const string WebcachePrefix = "webcache";

        // ---- the forbidden-name wall (defense in depth, see header) ------
        // Stored lowercase; comparisons are case-insensitive.
        private static readonly string[] ForbiddenNames =
        {
            "cookies", "cookie", "history", "login data", "sessions",
            "session storage", "bookmarks", "favicons", "local storage",
            "indexeddb", "web data", "places.sqlite", "cookies.sqlite",
            "key3.db", "key4.db", "logins.json", "formhistory.sqlite",
            "sync data"
        };

        // ---- literal per-profile cache subdirs ----------------------------
        // Chromium-family apps (Chrome/Edge/Brave/Vivaldi): inside every
        // profile dir under "User Data".
        private static readonly string[] ChromiumCacheDirNames =
        {
            "Cache", "Code Cache", "GPUCache", "DawnCache", "GrShaderCache",
            "ShaderCache", "Media Cache"
        };
        // Opera keeps its caches directly under the app folder (no
        // User Data\<profile> layout).
        private static readonly string[] OperaCacheDirNames =
        {
            "Cache", "Code Cache", "GPUCache", "ShaderCache", "Media Cache"
        };
        // Steam library steamapps dirs.
        private static readonly string[] SteamCacheDirNames =
        {
            "shadercache", "depotcache"
        };

        private const string RiskLabel = "safe - cache files only";

        // =================================================================
        // Public API
        // =================================================================

        // The resolved target table. Targets whose install dir does not
        // exist are still returned with an empty CacheDirs array (so the
        // caller always gets the full name list). Freshly resolved on every
        // call - cheap (a few dozen Directory.Exists checks) and always
        // current (profiles come and go).
        public static List<AppCacheTarget> Targets()
        {
            List<AppCacheTarget> list = new List<AppCacheTarget>();
            SafeAdd(list, "Google Chrome", new string[] { "chrome" },
                delegate { return BuildChromiumTarget("Google Chrome", new string[] { "chrome" },
                    @"%LOCALAPPDATA%\Google\Chrome\User Data"); });
            SafeAdd(list, "Microsoft Edge", new string[] { "msedge" },
                delegate { return BuildChromiumTarget("Microsoft Edge", new string[] { "msedge" },
                    @"%LOCALAPPDATA%\Microsoft\Edge\User Data"); });
            SafeAdd(list, "Brave", new string[] { "brave" },
                delegate { return BuildChromiumTarget("Brave", new string[] { "brave" },
                    @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data"); });
            SafeAdd(list, "Vivaldi", new string[] { "vivaldi" },
                delegate { return BuildChromiumTarget("Vivaldi", new string[] { "vivaldi" },
                    @"%LOCALAPPDATA%\Vivaldi\User Data"); });
            SafeAdd(list, "Opera", new string[] { "opera" }, BuildOperaTarget);
            SafeAdd(list, "Firefox", new string[] { "firefox" }, BuildFirefoxTarget);
            SafeAdd(list, "Steam", new string[] { "steam", "steamwebhelper" }, BuildSteamTarget);
            SafeAdd(list, "Discord", new string[] { "Discord", "Update" },
                delegate { return BuildFlatTarget("Discord", new string[] { "Discord", "Update" },
                    @"%APPDATA%\discord", new string[] { "Cache", "Code Cache", "GPUCache" }); });
            SafeAdd(list, "Epic Games Launcher", new string[] { "EpicGamesLauncher" },
                delegate { return BuildFlatTarget("Epic Games Launcher", new string[] { "EpicGamesLauncher" },
                    @"%LOCALAPPDATA%\EpicGamesLauncher\Saved",
                    new string[] { "webcache", "webcache_414x", "webcache_443x" }); });
            SafeAdd(list, "Battle.net", new string[] { "Battle.net", "Agent" },
                delegate { return BuildFlatTarget("Battle.net", new string[] { "Battle.net", "Agent" },
                    @"%LOCALAPPDATA%\Battle.net", new string[] { "Cache", "GPUCache" }); });
            return list;
        }

        // Process names from the target table currently running (for the
        // Wave 6 "app running" warning). Best-effort; never throws.
        public static List<string> RunningApps()
        {
            List<string> found = new List<string>();
            try
            {
                foreach (AppCacheTarget target in Targets())
                {
                    foreach (string processName in target.ProcessNames)
                    {
                        if (ContainsName(found, processName)) continue;
                        if (IsProcessRunning(processName)) found.Add(processName);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("appcache: running-app scan failed", ex);
            }
            return found;
        }

        // Read-only measurement: one CleanCategory (Kind "appcache") per
        // target that currently has any cache bytes on disk. Categories for
        // absent installs are skipped (a CLEAN "not present" line is logged
        // instead); a running app is noted in the category so the Wave 6 UI
        // can warn before cleaning.
        public static List<CleanCategory> Measure()
        {
            List<CleanCategory> results = new List<CleanCategory>();
            try
            {
                List<string> running = RunningApps();
                foreach (AppCacheTarget target in Targets())
                {
                    CleanCategory cat = new CleanCategory(target.Name, CleanCategory.KindAppCache, RiskLabel);

                    List<string> runningHere = RunningProcessesOf(target, running);
                    if (runningHere.Count > 0)
                    {
                        cat.Notes = AppendNote(cat.Notes,
                            "app running (" + JoinNames(runningHere) + ") - close it to clean its cache");
                    }

                    foreach (string dir in target.CacheDirs)
                    {
                        if (!RecheckCandidate(dir)) continue;   // wall re-check; logs on block
                        if (!Directory.Exists(dir)) continue;   // vanished between resolve and walk
                        Counters c = new Counters();
                        MeasureTree(dir, c);
                        cat.Bytes += c.Bytes;
                        cat.Files += c.Files;
                        if (c.Skipped > 0)
                        {
                            cat.Notes = AppendNote(cat.Notes, c.Skipped + " unreadable");
                        }
                    }

                    if (cat.Bytes > 0)
                    {
                        results.Add(cat);
                        string line = "measure '" + cat.Name + "': " + cat.SizeText + " in " +
                            cat.Files.ToString("N0", CultureInfo.InvariantCulture) + " files";
                        if (cat.Notes.Length > 0) line += " - " + cat.Notes;
                        Log.Chan("CLEAN", line);
                    }
                    else
                    {
                        Log.Chan("CLEAN", "measure '" + cat.Name + "': not present (skipped)");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("appcache: measure failed", ex);
            }
            return results;
        }

        // Deletes the CONTENTS of the resolved cache dirs of every selected
        // target (the cache dirs themselves are kept). A target whose app is
        // running is never touched. Per-item try/catch: locked files are
        // skipped + WARNed; this method never throws. Returns one result per
        // cleaned target; totalBytesFreed sums all BytesFreed.
        public static List<AppCacheCleanResult> Clean(List<string> targetNames, out long totalBytesFreed)
        {
            List<AppCacheCleanResult> results = new List<AppCacheCleanResult>();
            totalBytesFreed = 0;
            try
            {
                if (targetNames == null || targetNames.Count == 0)
                {
                    Log.Chan("CLEAN", "appcache: no targets selected - nothing to do");
                    return results;
                }

                List<string> running = RunningApps();
                foreach (AppCacheTarget target in Targets())
                {
                    if (!ContainsName(targetNames, target.Name)) continue;
                    AppCacheCleanResult r = CleanTarget(target, running);
                    results.Add(r);
                    totalBytesFreed += r.BytesFreed;
                }

                Log.Chan("CLEAN", "appcache: total " + SessionHistory.FormatBytes(totalBytesFreed) +
                    " freed across " + results.Count + " target(s)");
            }
            catch (Exception ex)
            {
                Log.Error("appcache: clean failed", ex);
            }
            return results;
        }

        // The forbidden-name wall (see header): true when the name (a file
        // or directory name, compared case-insensitively against the list)
        // is personal data that must never be enumerated or deleted.
        public static bool IsForbiddenName(string name)
        {
            string normalized = NormalizeName(name);
            if (normalized.Length == 0) return false;
            for (int i = 0; i < ForbiddenNames.Length; i++)
            {
                if (normalized == ForbiddenNames[i]) return true;
            }
            return false;
        }

        // =================================================================
        // Target resolution (the whitelist wall, part 1)
        // =================================================================

        private delegate AppCacheTarget TargetBuilder();

        // One builder failure must never lose the whole table: the target
        // is still returned (empty CacheDirs) and the failure is WARNed.
        private static void SafeAdd(List<AppCacheTarget> list, string name, string[] processNames, TargetBuilder build)
        {
            try
            {
                list.Add(build());
            }
            catch (Exception ex)
            {
                Log.Warn("appcache: could not resolve target '" + name + "' - " + ex.Message);
                list.Add(new AppCacheTarget(name, processNames, new string[0]));
            }
        }

        // Expands %VARS% in a raw table path. Unexpanded tokens (missing
        // env var) yield "" so the target simply resolves empty.
        private static string EnvPath(string raw)
        {
            if (raw == null || raw.Length == 0) return "";
            string expanded = Environment.ExpandEnvironmentVariables(raw);
            if (expanded.IndexOf('%') >= 0) return "";   // a var did not resolve
            return expanded;
        }

        // Chromium family: enumerate profile dirs under the app's own
        // User Data folder (Default, Profile *, Guest Profile, System
        // Profile - nothing else), then add the literal cache subdirs that
        // actually exist. No other globbing, ever.
        private static AppCacheTarget BuildChromiumTarget(string name, string[] processNames, string userDataRaw)
        {
            List<string> dirs = new List<string>();
            string userDataRoot = EnvPath(userDataRaw);
            if (userDataRoot.Length > 0 && Directory.Exists(userDataRoot))
            {
                foreach (string profile in ChromiumProfiles(userDataRoot))
                {
                    foreach (string cacheName in ChromiumCacheDirNames)
                    {
                        AddIfExistingCacheDir(dirs, Path.Combine(profile, cacheName));
                    }
                }
            }
            return new AppCacheTarget(name, processNames, dirs.ToArray());
        }

        // Known Chromium profile dir names only (case-insensitive); every
        // candidate path also passes the forbidden-name wall.
        private static List<string> ChromiumProfiles(string userDataRoot)
        {
            List<string> profiles = new List<string>();
            try
            {
                DirectoryInfo root = new DirectoryInfo(userDataRoot);
                foreach (DirectoryInfo sub in root.GetDirectories())
                {
                    string n = sub.Name;
                    bool known = EqualIgnoreCase(n, "Default") ||
                                 EqualIgnoreCase(n, "Guest Profile") ||
                                 EqualIgnoreCase(n, "System Profile") ||
                                 StartsWithIgnoreCase(n, "Profile ");
                    if (!known) continue;
                    string forbidden = FirstForbiddenSegment(sub.FullName);
                    if (forbidden != null)
                    {
                        Log.Warn("appcache: forbidden name '" + forbidden + "' blocked: " + sub.FullName);
                        continue;
                    }
                    profiles.Add(sub.FullName);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("appcache: could not enumerate profiles under " + userDataRoot + " - " + ex.Message);
            }
            profiles.Sort(StringComparer.OrdinalIgnoreCase);
            return profiles;
        }

        // Opera keeps caches directly under "Opera Stable"/"Opera GX Stable".
        private static AppCacheTarget BuildOperaTarget()
        {
            List<string> dirs = new List<string>();
            string operaRoot = EnvPath(@"%LOCALAPPDATA%\Opera Software");
            if (operaRoot.Length > 0 && Directory.Exists(operaRoot))
            {
                string[] appDirs = new string[] { "Opera Stable", "Opera GX Stable" };
                foreach (string appDir in appDirs)
                {
                    string parent = Path.Combine(operaRoot, appDir);
                    if (!Directory.Exists(parent)) continue;
                    foreach (string cacheName in OperaCacheDirNames)
                    {
                        AddIfExistingCacheDir(dirs, Path.Combine(parent, cacheName));
                    }
                }
            }
            return new AppCacheTarget("Opera", new string[] { "opera" }, dirs.ToArray());
        }

        // Firefox: cache2 (+ startupCache sibling) per profile dir under
        // the app's own Profiles folder - the sanctioned one-level glob.
        private static AppCacheTarget BuildFirefoxTarget()
        {
            List<string> dirs = new List<string>();
            string profilesRoot = EnvPath(@"%LOCALAPPDATA%\Mozilla\Firefox\Profiles");
            if (profilesRoot.Length > 0 && Directory.Exists(profilesRoot))
            {
                try
                {
                    DirectoryInfo root = new DirectoryInfo(profilesRoot);
                    foreach (DirectoryInfo profile in root.GetDirectories())
                    {
                        string forbidden = FirstForbiddenSegment(profile.FullName);
                        if (forbidden != null)
                        {
                            Log.Warn("appcache: forbidden name '" + forbidden + "' blocked: " + profile.FullName);
                            continue;
                        }
                        AddIfExistingCacheDir(dirs, Path.Combine(profile.FullName, "cache2"));
                        AddIfExistingCacheDir(dirs, Path.Combine(profile.FullName, "startupCache"));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("appcache: could not enumerate Firefox profiles - " + ex.Message);
                }
            }
            return new AppCacheTarget("Firefox", new string[] { "firefox" }, dirs.ToArray());
        }

        // Steam: the built-in browser cache (%LOCALAPPDATA%\Steam\htmlcache)
        // plus shadercache/depotcache under every discovered steamapps dir.
        // Discovery = HKCU SteamPath + best-effort %ProgramFiles(x86)%\Steam
        // + the library folders listed in libraryfolders.vdf. Missing pieces
        // are skipped with a note - never a disk-wide scan.
        private static AppCacheTarget BuildSteamTarget()
        {
            List<string> dirs = new List<string>();
            AddIfExistingCacheDir(dirs, EnvPath(@"%LOCALAPPDATA%\Steam\htmlcache"));

            List<string> roots = SteamRoots();
            List<string> steamappsDirs = new List<string>();
            foreach (string root in roots)
            {
                AddDistinctDir(steamappsDirs, Path.Combine(root, "steamapps"));
            }
            foreach (string root in roots)
            {
                string vdf = Path.Combine(Path.Combine(root, "steamapps"), "libraryfolders.vdf");
                foreach (string library in ParseSteamLibraries(vdf))
                {
                    AddDistinctDir(steamappsDirs, Path.Combine(library, "steamapps"));
                }
            }
            foreach (string steamapps in steamappsDirs)
            {
                foreach (string cacheName in SteamCacheDirNames)
                {
                    AddIfExistingCacheDir(dirs, Path.Combine(steamapps, cacheName));
                }
            }
            return new AppCacheTarget("Steam", new string[] { "steam", "steamwebhelper" }, dirs.ToArray());
        }

        // Steam install roots: registry SteamPath first, then the
        // ProgramFiles(x86) fallback. Both optional; deduped.
        private static List<string> SteamRoots()
        {
            List<string> roots = new List<string>();
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (key != null)
                    {
                        string steamPath = (key.GetValue("SteamPath") as string) ?? "";
                        steamPath = steamPath.Trim().Replace('/', '\\');
                        if (steamPath.Length > 0 && Directory.Exists(steamPath))
                        {
                            AddDistinctDir(roots, steamPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("appcache: could not read HKCU\\Software\\Valve\\Steam - " + ex.Message);
            }
            string programFilesX86 = EnvPath(@"%ProgramFiles(x86)%");
            if (programFilesX86.Length > 0)
            {
                string fallback = Path.Combine(programFilesX86, "Steam");
                if (Directory.Exists(fallback)) AddDistinctDir(roots, fallback);
            }
            return roots;
        }

        // Best-effort parse of libraryfolders.vdf: every "path" "..." pair
        // whose value is an existing directory. Unreadable/absent file is
        // not an error (older clients keep everything in the default
        // steamapps dir, already covered).
        private static List<string> ParseSteamLibraries(string vdfPath)
        {
            List<string> libraries = new List<string>();
            if (vdfPath == null || vdfPath.Length == 0 || !File.Exists(vdfPath)) return libraries;
            string text;
            try
            {
                text = File.ReadAllText(vdfPath);
            }
            catch (Exception ex)
            {
                Log.Warn("appcache: steam libraryfolders.vdf unreadable (using default steamapps only) - " + ex.Message);
                return libraries;
            }
            int searchFrom = 0;
            while (true)
            {
                int key = text.IndexOf("\"path\"", searchFrom, StringComparison.OrdinalIgnoreCase);
                if (key < 0) break;
                int open = text.IndexOf('"', key + 6);
                if (open < 0) break;
                int close = text.IndexOf('"', open + 1);
                if (close < 0) break;
                // VDF escapes backslashes as \\ - unescape before use.
                string library = text.Substring(open + 1, close - open - 1).Trim()
                    .Replace("\\\\", "\\").Replace('/', '\\');
                searchFrom = close + 1;
                if (library.Length > 0 && Directory.Exists(library))
                {
                    AddDistinctDir(libraries, library);
                }
            }
            return libraries;
        }

        // Flat layout apps (Discord, Epic, Battle.net): literal cache dir
        // names directly under one parent. Note (D5): Battle.net dirs like
        // "BrowserCache" are NOT on the whitelist and are never added.
        private static AppCacheTarget BuildFlatTarget(string name, string[] processNames, string parentRaw, string[] cacheDirNames)
        {
            List<string> dirs = new List<string>();
            string parent = EnvPath(parentRaw);
            if (parent.Length > 0 && Directory.Exists(parent))
            {
                foreach (string cacheName in cacheDirNames)
                {
                    AddIfExistingCacheDir(dirs, Path.Combine(parent, cacheName));
                }
            }
            return new AppCacheTarget(name, processNames, dirs.ToArray());
        }

        // Adds a candidate only when it exists AND passes both walls
        // (whitelist leaf name + forbidden-name segments). This is where
        // the D5 whitelist does its work at resolution time.
        private static void AddIfExistingCacheDir(List<string> dirs, string candidate)
        {
            if (candidate == null || candidate.Length == 0) return;
            if (!Directory.Exists(candidate)) return;
            if (!CandidateAllowed(candidate)) return;
            AddDistinctDir(dirs, candidate);
        }

        // Adds a path to a list once (trailing separators trimmed,
        // case-insensitive dedupe - Steam roots can repeat across sources).
        private static void AddDistinctDir(List<string> list, string path)
        {
            if (path == null || path.Length == 0) return;
            string trimmed = path.TrimEnd('\\', '/');
            if (trimmed.Length == 0) return;
            if (ContainsName(list, trimmed)) return;
            list.Add(trimmed);
        }

        // =================================================================
        // The two safety walls
        // =================================================================

        // Whitelist + forbidden-name check for one candidate cache dir.
        // Returns false (with a WARN) when the candidate must not be used.
        private static bool CandidateAllowed(string candidate)
        {
            string leaf = LeafOf(candidate);
            if (!IsWhitelistedCacheName(leaf))
            {
                Log.Warn("appcache: non-whitelisted cache name '" + leaf + "' blocked: " + candidate);
                return false;
            }
            string forbidden = FirstForbiddenSegment(candidate);
            if (forbidden != null)
            {
                Log.Warn("appcache: forbidden name '" + forbidden + "' blocked: " + candidate);
                return false;
            }
            return true;
        }

        // The whitelist: exact literal names (case-insensitive) plus the
        // sanctioned "webcache" prefix rule for the Epic variants.
        private static bool IsWhitelistedCacheName(string name)
        {
            string normalized = NormalizeName(name);
            if (normalized.Length == 0) return false;
            for (int i = 0; i < WhitelistedCacheNames.Length; i++)
            {
                if (normalized == WhitelistedCacheNames[i]) return true;
            }
            return normalized.StartsWith(WebcachePrefix, StringComparison.OrdinalIgnoreCase);
        }

        // Forbidden-name wall over every path segment. Returns the first
        // offending segment (original case, for the log) or null.
        private static string FirstForbiddenSegment(string path)
        {
            if (path == null || path.Length == 0) return null;
            string[] segments = path.Split(new char[] { '\\', '/' });
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i].Length == 0) continue;
                if (IsForbiddenName(segments[i])) return segments[i];
            }
            return null;
        }

        private static string LeafOf(string path)
        {
            if (path == null) return "";
            string trimmed = path.TrimEnd('\\', '/');
            int cut = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
            return cut < 0 ? trimmed : trimmed.Substring(cut + 1);
        }

        private static string NormalizeName(string name)
        {
            return name == null ? "" : name.Trim().ToLowerInvariant();
        }

        private static bool EqualIgnoreCase(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static bool StartsWithIgnoreCase(string a, string b)
        {
            if (a == null || b == null) return false;
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
        }

        // =================================================================
        // Running-app detection
        // =================================================================

        private static bool IsProcessRunning(string processName)
        {
            Process[] procs = null;
            try
            {
                procs = Process.GetProcessesByName(processName);
            }
            catch (Exception ex)
            {
                // Cannot tell -> fail closed (D5): report the app as running
                // so its caches are never deleted while in doubt.
                Log.Warn("appcache: could not check process '" + processName + "' - " + ex.Message +
                    " (assuming it is running)");
                return true;
            }
            bool running = procs != null && procs.Length > 0;
            DisposeAll(procs);
            return running;
        }

        // The target's process names that are currently running (subset of
        // the pre-computed running list).
        private static List<string> RunningProcessesOf(AppCacheTarget target, List<string> running)
        {
            List<string> here = new List<string>();
            if (running == null || running.Count == 0) return here;
            foreach (string processName in target.ProcessNames)
            {
                if (ContainsName(running, processName)) here.Add(processName);
            }
            return here;
        }

        private static void DisposeAll(Process[] procs)
        {
            if (procs == null) return;
            for (int i = 0; i < procs.Length; i++)
            {
                try { procs[i].Dispose(); }
                catch (Exception) { }
            }
        }

        private static bool ContainsName(List<string> list, string name)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (EqualIgnoreCase(list[i], name)) return true;
            }
            return false;
        }

        private static string JoinNames(List<string> names)
        {
            return string.Join(", ", names.ToArray());
        }

        // =================================================================
        // Measurement (read-only)
        // =================================================================

        private sealed class Counters
        {
            public long Bytes;
            public int Files;
            public int Skipped;
        }

        // Re-checks one resolved candidate through the walls before walking
        // or deleting it (defense in depth - resolution already filtered).
        private static bool RecheckCandidate(string dir)
        {
            string forbidden = FirstForbiddenSegment(dir);
            if (forbidden != null)
            {
                Log.Warn("appcache: forbidden name '" + forbidden + "' blocked: " + dir);
                return false;
            }
            string leaf = LeafOf(dir);
            if (!IsWhitelistedCacheName(leaf))
            {
                Log.Warn("appcache: non-whitelisted cache name '" + leaf + "' blocked: " + dir);
                return false;
            }
            return true;
        }

        // Recursive read-only size/count walk. Every directory level is
        // individually try/caught; reparse points are never followed.
        private static void MeasureTree(string dirPath, Counters c)
        {
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(dirPath).GetFileSystemInfos();
            }
            catch (Exception)
            {
                c.Skipped++;
                return;
            }
            foreach (FileSystemInfo entry in entries)
            {
                if (IsForbiddenName(entry.Name))
                {
                    Log.Warn("appcache: forbidden name '" + entry.Name + "' blocked: " + entry.FullName);
                    c.Skipped++;
                    continue;
                }
                try
                {
                    FileInfo file = entry as FileInfo;
                    if (file != null)
                    {
                        c.Bytes += file.Length;
                        c.Files++;
                        continue;
                    }
                    DirectoryInfo sub = entry as DirectoryInfo;
                    if (sub == null) continue;
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        c.Skipped++;
                        continue;
                    }
                    MeasureTree(sub.FullName, c);
                }
                catch (Exception)
                {
                    c.Skipped++;
                }
            }
        }

        // =================================================================
        // Cleaning (contents only; the cache dirs themselves are kept)
        // =================================================================

        private static AppCacheCleanResult CleanTarget(AppCacheTarget target, List<string> running)
        {
            AppCacheCleanResult r = new AppCacheCleanResult(target.Name);

            List<string> runningHere = RunningProcessesOf(target, running);
            if (runningHere.Count > 0)
            {
                // NEVER delete from a running browser/launcher.
                r.Notes = "app running; close it to clean its cache";
                Log.Warn("appcache: skipped '" + target.Name + "' - app running (" +
                    JoinNames(runningHere) + "); close it to clean its cache");
                Log.Chan("CLEAN", "appcache '" + target.Name + "': skipped - app running; close it to clean its cache");
                return r;
            }

            foreach (string dir in target.CacheDirs)
            {
                // Forbidden-name wall + whitelist, re-checked per candidate
                // right before any enumeration or delete.
                string forbidden = FirstForbiddenSegment(dir);
                if (forbidden != null)
                {
                    Log.Warn("appcache: forbidden name '" + forbidden + "' blocked: " + dir);
                    r.Notes = AppendNote(r.Notes, "forbidden name '" + forbidden + "' blocked");
                    continue;
                }
                string leaf = LeafOf(dir);
                if (!IsWhitelistedCacheName(leaf))
                {
                    Log.Warn("appcache: non-whitelisted cache name '" + leaf + "' blocked: " + dir);
                    continue;
                }
                if (!Directory.Exists(dir))
                {
                    r.Notes = AppendNote(r.Notes, "part not present");
                    continue;
                }
                try
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    {
                        Log.Warn("appcache: skipped reparse point " + dir);
                        r.FilesSkipped++;
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("appcache: could not read attributes of " + dir + " - " + ex.Message);
                    r.FilesSkipped++;
                    continue;
                }
                DeleteContents(dir, r);
            }

            string line = "appcache '" + target.Name + "': " + SessionHistory.FormatBytes(r.BytesFreed) +
                " freed, " + r.FilesDeleted.ToString("N0", CultureInfo.InvariantCulture) +
                " files deleted, " + r.FilesSkipped.ToString("N0", CultureInfo.InvariantCulture) + " skipped";
            if (r.Notes.Length > 0) line += " - " + r.Notes;
            Log.Chan("CLEAN", line);
            return r;
        }

        // Deletes the CONTENTS of one cache dir, recursively, with per-item
        // try/catch: locked files are skipped + WARNed (never fatal), bytes
        // are tallied from the file length measured before deletion.
        private static void DeleteContents(string dirPath, AppCacheCleanResult r)
        {
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(dirPath).GetFileSystemInfos();
            }
            catch (Exception ex)
            {
                Log.Warn("appcache: could not enumerate " + dirPath + " - " + ex.Message);
                r.FilesSkipped++;
                return;
            }
            foreach (FileSystemInfo entry in entries)
            {
                if (IsForbiddenName(entry.Name))
                {
                    Log.Warn("appcache: forbidden name '" + entry.Name + "' blocked: " + entry.FullName);
                    r.FilesSkipped++;
                    continue;
                }
                try
                {
                    FileInfo file = entry as FileInfo;
                    if (file != null)
                    {
                        long size = 0;
                        try { size = file.Length; }
                        catch (Exception) { }
                        file.Delete();
                        r.BytesFreed += size;
                        r.FilesDeleted++;
                        continue;
                    }
                    DirectoryInfo sub = entry as DirectoryInfo;
                    if (sub == null) continue;
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        Log.Warn("appcache: skipped reparse point " + sub.FullName);
                        r.FilesSkipped++;
                        continue;
                    }
                    DeleteContents(sub.FullName, r);   // contents first, per-item try/catch inside
                    try
                    {
                        sub.Delete(false);             // remove the now-(possibly)-empty folder
                    }
                    catch (Exception)
                    {
                        r.FilesSkipped++;              // still non-empty (locked items) or locked itself
                    }
                }
                catch (Exception ex)
                {
                    r.FilesSkipped++;
                    Log.Warn("appcache: skipped locked item " + entry.FullName + " (" + ex.Message + ")");
                }
            }
        }

        // =================================================================
        // Small shared helpers
        // =================================================================

        private static string AppendNote(string existing, string addition)
        {
            if (string.IsNullOrEmpty(addition)) return existing == null ? "" : existing;
            if (string.IsNullOrEmpty(existing)) return addition;
            return existing + "; " + addition;
        }
    }
}
