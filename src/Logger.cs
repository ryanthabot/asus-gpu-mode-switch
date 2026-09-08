//  Logger.cs  (v1.1.0 - Wave 3)
//  --------------------
//  Logging core (static class Log) - the foundation every module logs through.
//
//  Per-run files: each run gets %LOCALAPPDATA%\GpuModeSwitch\logs\<GoTime|
//  EcoMode>\<App>_yyyy-MM-dd_HHmmss.log (one file per run; the old single
//  GoTime.log / EcoMode.log from v1.0.x is retired).
//
//  Line format: yyyy-MM-dd HH:mm:ss.fff [LEVEL] (channel) message
//    LEVEL   INFO / WARN / ERROR
//    channel APP (default) or a module tag: CLEAN, GPU, FREEZE, POWER, TRAY,
//            MONITOR, PROFILE, SESSION (unknown tags are still accepted).
//  Exceptions logged through Error(msg, ex) append the exception type +
//  message + stack trace on indented continuation lines.
//
//  Dual sink, thread-safe through a single lock: an in-memory StringBuilder
//  buffer (shown by the log window and copied by "Copy log") plus
//  File.AppendAllText to the run's file. All file IO is best-effort - if the
//  file sink fails, the buffer keeps working and a single WARN is recorded;
//  logging must never crash or slow the app.
//
//  Retention (on BeginSession, after the session header is written): in the
//  app's log folder delete files older than 30 days, then - if the folder is
//  still over 200 MB - delete oldest-first (by the timestamp parsed from the
//  file name, LastWriteTime as fallback) until it is at or under 200 MB.
//  Files stamped today and the current run's log are never deleted.
//
//  Replaces the v1.0.x Logger class (single mirror file, rewritten fresh each
//  run). Split out of GpuModeSwitch.cs in Wave 2; rewritten in Wave 3 per
//  docs\HANDBOOK.md section 4 (D1/D8) and section 8.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // Logging core: in-memory buffer + per-run log file, session headers,
    // retention pruning and log listing. See file header for the contract.
    // ---------------------------------------------------------------------
    internal static class Log
    {
        private const string RootFolderName = "GpuModeSwitch";
        private const string LogsFolderName = "logs";
        private const long SizeCapBytes = 200L * 1024 * 1024;   // 200 MB
        private const int AgeLimitDays = 30;

        private static readonly object _gate = new object();
        private static readonly StringBuilder _buffer = new StringBuilder();

        private static string _logsRoot = "";
        private static string _appFolder = "";
        private static string _currentLogPath = "";
        private static bool _fileOk;
        private static Stopwatch _sessionClock;

        // ---- API -----------------------------------------------------------

        // General INFO line, APP channel.
        public static void Info(string msg)
        {
            Write("INFO", "APP", msg, null);
        }

        // Warning line, APP channel.
        public static void Warn(string msg)
        {
            Write("WARN", "APP", msg, null);
        }

        // Error line, APP channel; the exception (if any) is appended as
        // indented continuation lines (type + message + stack).
        public static void Error(string msg, Exception ex = null)
        {
            Write("ERROR", "APP", msg, ex);
        }

        // Channel-tagged INFO line. Channels: CLEAN, GPU, FREEZE, POWER, TRAY,
        // MONITOR, PROFILE, SESSION - unknown channel names are still accepted.
        public static void Chan(string channel, string msg)
        {
            string ch = string.IsNullOrEmpty(channel) ? "APP" : channel.Trim().ToUpperInvariant();
            Write("INFO", ch, msg, null);
        }

        // Full text of the current run's log buffer (source for the log
        // window's text box and its "Copy log" button).
        public static string Snapshot()
        {
            lock (_gate)
            {
                return _buffer.ToString();
            }
        }

        // Full path of the current run's log file ("" before BeginSession).
        public static string CurrentLogPath
        {
            get
            {
                lock (_gate)
                {
                    return _currentLogPath;
                }
            }
        }

        // Root logs directory: %LOCALAPPDATA%\GpuModeSwitch\logs
        public static string LogsRoot
        {
            get
            {
                if (_logsRoot.Length == 0)
                {
                    _logsRoot = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        Path.Combine(RootFolderName, LogsFolderName));
                }
                return _logsRoot;
            }
        }

        // Starts the per-run log file and writes the session header block,
        // then prunes old logs (30 days / 200 MB). appFolder/file prefix is
        // derived from appName: names containing "Eco" -> EcoMode, else GoTime.
        public static void BeginSession(string appName, string version)
        {
            string name = appName == null ? "" : appName;
            string folder = FolderFor(name);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
            string dir = Path.Combine(LogsRoot, folder);
            string path = Path.Combine(dir, folder + "_" + stamp + ".log");

            lock (_gate)
            {
                _appFolder = folder;
                _currentLogPath = path;
                _fileOk = false;
                _buffer.Length = 0;
                _sessionClock = Stopwatch.StartNew();
            }

            try
            {
                Directory.CreateDirectory(dir);
                lock (_gate) { _fileOk = true; }
            }
            catch (Exception ex)
            {
                lock (_gate) { _fileOk = false; }
                Warn("log file could not be created - " + ex.Message);
            }

            Info("=== " + name + " v" + version + " session (" + ActiveDefine() + ") ===");
            Info("Session start: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            Info("Log file : " + path);
            Info("Windows build: " + WindowsBuild());
            Info("Machine  : " + MachineModel());
            Info("Admin    : " + AdminState());
            Info(".NET runtime: " + Environment.Version.ToString());

            PruneLogs(dir);
        }

        // Writes the closing footer: the run's result and total duration.
        public static void EndSession(string result)
        {
            string duration = "unknown";
            lock (_gate)
            {
                if (_sessionClock != null)
                {
                    TimeSpan t = _sessionClock.Elapsed;
                    duration = string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
                        (int)t.TotalHours, t.Minutes, t.Seconds);
                }
            }
            Info("=== Session end: " + (result == null ? "" : result) + " (total " + duration + ") ===");
        }

        // Full paths of the given app's log files, NEWEST FIRST.
        // "app" is mapped like appName in BeginSession ("Eco" -> EcoMode).
        public static string[] ListLogs(string app)
        {
            List<KeyValuePair<DateTime, string>> found = new List<KeyValuePair<DateTime, string>>();
            try
            {
                DirectoryInfo dir = new DirectoryInfo(Path.Combine(LogsRoot, FolderFor(app == null ? "" : app)));
                if (!dir.Exists) return new string[0];
                foreach (FileInfo f in dir.GetFiles("*.log"))
                {
                    found.Add(new KeyValuePair<DateTime, string>(StampOf(f), f.FullName));
                }
            }
            catch
            {
                return new string[0];
            }
            found.Sort(delegate (KeyValuePair<DateTime, string> a, KeyValuePair<DateTime, string> b)
            {
                return b.Key.CompareTo(a.Key);      // newest first
            });
            string[] result = new string[found.Count];
            for (int i = 0; i < found.Count; i++) result[i] = found[i].Value;
            return result;
        }

        // ---- core ----------------------------------------------------------

        // Formats one entry (main line + optional exception continuations) and
        // appends it to both sinks under the single lock. File failures turn
        // the file sink off for the rest of the run with one WARN.
        private static void Write(string level, string channel, string msg, Exception ex)
        {
            StringBuilder text = new StringBuilder();
            text.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            text.Append(" [").Append(level).Append("] (").Append(channel).Append(") ");
            text.Append(msg == null ? "" : msg);

            if (ex != null)
            {
                text.Append(Environment.NewLine);
                text.Append("  Exception: ").Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
                string stack = ex.StackTrace;
                if (!string.IsNullOrEmpty(stack))
                {
                    string[] frames = stack.Replace("\r\n", "\n").Split('\n');
                    foreach (string frame in frames)
                    {
                        text.Append(Environment.NewLine).Append("  ").Append(frame.TrimEnd());
                    }
                }
            }
            text.AppendLine();

            lock (_gate)
            {
                _buffer.Append(text.ToString());
                if (_fileOk)
                {
                    try
                    {
                        File.AppendAllText(_currentLogPath, text.ToString());
                    }
                    catch (Exception fex)
                    {
                        _fileOk = false;    // keep buffering; single WARN below
                        _buffer.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                            " [WARN] (APP) log file sink disabled after a write failure - " + fex.Message +
                            Environment.NewLine);
                    }
                }
            }
        }

        // Retention: delete files older than 30 days, then enforce the 200 MB
        // folder cap oldest-first. Never touches files stamped today or the
        // current run's log. Removals are logged as INFO lines (this runs
        // after the session header, so the lines land in the new log file).
        private static void PruneLogs(string appDir)
        {
            try
            {
                DirectoryInfo dir = new DirectoryInfo(appDir);
                if (!dir.Exists) return;

                DateTime today = DateTime.Today;
                DateTime cutoff = today.AddDays(-AgeLimitDays);
                List<KeyValuePair<DateTime, FileInfo>> kept = new List<KeyValuePair<DateTime, FileInfo>>();
                long total = 0;

                foreach (FileInfo f in dir.GetFiles("*.log"))
                {
                    if (string.Equals(f.FullName, CurrentLogPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;                       // never delete the current log
                    }
                    DateTime stamp = StampOf(f);
                    if (stamp.Date == today)
                    {
                        total += f.Length;              // counted, but never deleted
                        continue;                       // never delete today's logs
                    }
                    if (stamp < cutoff)
                    {
                        DeleteLogged(f, "age " + (int)(today - stamp.Date).TotalDays + " days");
                        continue;
                    }
                    kept.Add(new KeyValuePair<DateTime, FileInfo>(stamp, f));
                    total += f.Length;
                }

                if (total <= SizeCapBytes) return;

                kept.Sort(delegate (KeyValuePair<DateTime, FileInfo> a, KeyValuePair<DateTime, FileInfo> b)
                {
                    return a.Key.CompareTo(b.Key);      // oldest first
                });
                foreach (KeyValuePair<DateTime, FileInfo> kv in kept)
                {
                    if (total <= SizeCapBytes) break;
                    long len = kv.Value.Length;
                    if (DeleteLogged(kv.Value, "size cap 200 MB"))
                    {
                        total -= len;
                    }
                }
            }
            catch (Exception ex)
            {
                Info("retention: pruning skipped - " + ex.Message);
            }
        }

        // Deletes one log file; returns false if it could not be deleted.
        private static bool DeleteLogged(FileInfo f, string reason)
        {
            try
            {
                f.Delete();
                Info("retention: removed " + f.Name + " (" + reason + ")");
                return true;
            }
            catch (Exception ex)
            {
                Info("retention: could not remove " + f.Name + " - " + ex.Message);
                return false;
            }
        }

        // Run stamp parsed from the file name (<App>_yyyy-MM-dd_HHmmss.log),
        // falling back to LastWriteTime for files that do not match.
        private static DateTime StampOf(FileInfo f)
        {
            string name = Path.GetFileNameWithoutExtension(f.Name);
            int sep = name.IndexOf('_');
            if (sep >= 0 && sep + 1 < name.Length)
            {
                DateTime parsed;
                if (DateTime.TryParseExact(name.Substring(sep + 1), "yyyy-MM-dd_HHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                {
                    return parsed;
                }
            }
            return f.LastWriteTime;
        }

        // Log subfolder / file prefix for an app name: "Eco" -> EcoMode,
        // "Go Time"/"GoTime" -> GoTime (both = the retired v1.x exes; their
        // old logs stay browsable), anything else (the unified
        // "GPU Mode Switch") -> GpuModeSwitch.
        private static string FolderFor(string appName)
        {
            if (appName != null)
            {
                if (appName.IndexOf("Eco", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "EcoMode";
                }
                if (appName.IndexOf("Go Time", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    appName.IndexOf("GoTime", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "GoTime";
                }
            }
            return "GpuModeSwitch";
        }

        private static string ActiveDefine()
        {
            return "UNIFIED";   // v1.2.0: one target, no MODE_* defines
        }

        // Windows build number + update revision from the registry, e.g.
        // "26100.2894"; falls back to Environment.OSVersion on any failure.
        private static string WindowsBuild()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion"))
                {
                    if (k != null)
                    {
                        string build = k.GetValue("CurrentBuild") as string;
                        if (!string.IsNullOrEmpty(build))
                        {
                            object ubr = k.GetValue("UBR");
                            if (ubr is int)
                            {
                                return build + "." + ((int)ubr).ToString(CultureInfo.InvariantCulture);
                            }
                            return build;
                        }
                    }
                }
            }
            catch
            {
                // best-effort - fall through to Environment.OSVersion
            }
            return Environment.OSVersion.VersionString;
        }

        // Machine model via WMI Win32_ComputerSystem; failure-tolerant.
        private static string MachineModel()
        {
            try
            {
                using (ManagementObjectSearcher s =
                    new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject m in s.Get())
                    {
                        string maker = m["Manufacturer"] as string;
                        string model = m["Model"] as string;
                        string combined = ((maker == null ? "" : maker) + " " + (model == null ? "" : model)).Trim();
                        if (combined.Length > 0) return combined;
                    }
                }
            }
            catch (Exception ex)
            {
                return "(model query failed: " + ex.Message + ")";
            }
            return "(model unknown)";
        }

        // Administrator check via WindowsPrincipal; failure-tolerant.
        private static string AdminState()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                if (id != null)
                {
                    WindowsPrincipal principal = new WindowsPrincipal(id);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator) ? "yes (elevated)" : "no";
                }
            }
            catch
            {
                // fall through
            }
            return "unknown";
        }
    }
}
