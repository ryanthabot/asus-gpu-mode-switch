//  Logger.cs  (v1.0.22)
//  --------------------
//  Diagnostic log: kept in memory for the in-app viewer and mirrored to
//  %LOCALAPPDATA%\GpuModeSwitch\<app>.log so it can be relayed remotely.
//  (Wave 3 replaces this with the per-run Log contract - see
//  docs\HANDBOOK.md sections 4 and 8.)
//
//  Split out of GpuModeSwitch.cs (Wave 2, zero behavior change).

using System;
using System.IO;
using System.Management;
using System.Text;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // Diagnostic log: kept in memory for the in-app viewer and mirrored to
    // %LOCALAPPDATA%\GpuModeSwitch\<app>.log so it can be relayed remotely.
    // ---------------------------------------------------------------------
    internal static class Logger
    {
        private static readonly StringBuilder _buffer = new StringBuilder();
        private static string _filePath = "(log file not available)";
        private static bool _fileOk;

        public static string Text { get { return _buffer.ToString(); } }
        public static string FilePath { get { return _filePath; } }

        public static void Init(string appTitle, string fileName)
        {
            Line("=== " + appTitle + " v" + Program.Version + " ===");
            Line("Time : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Line("OS   : " + Environment.OSVersion.VersionString);
            try
            {
                using (ManagementObjectSearcher s =
                    new ManagementObjectSearcher("SELECT Model,Manufacturer FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject m in s.Get())
                    {
                        Line("PC   : " + m["Manufacturer"] + " " + m["Model"]);
                        break;
                    }
                }
                using (ManagementObjectSearcher s =
                    new ManagementObjectSearcher("SELECT Product FROM Win32_BaseBoard"))
                {
                    foreach (ManagementObject m in s.Get())
                    {
                        Line("Board: " + m["Product"]);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Line("PC   : (model query failed: " + ex.Message + ")");
            }

            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GpuModeSwitch");
                Directory.CreateDirectory(dir);
                _filePath = Path.Combine(dir, fileName + ".log");
                File.WriteAllText(_filePath, Text);   // fresh log per run
                _fileOk = true;
                Line("Log  : " + _filePath);
            }
            catch (Exception ex)
            {
                _filePath = "(log file could not be created: " + ex.Message + ")";
            }
        }

        public static void Line(string text)
        {
            lock (_buffer)
            {
                _buffer.AppendLine(text);
            }
            if (_fileOk)
            {
                try
                {
                    File.AppendAllText(_filePath, text + Environment.NewLine);
                }
                catch
                {
                    // file mirroring is best-effort; the in-memory copy always works
                }
            }
        }
    }
}
