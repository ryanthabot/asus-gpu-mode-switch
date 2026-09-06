//  SystemMonitor.cs  (v1.1.0 - Wave 4, agent A16)
//  -----------------------------------------------
//  Live system monitor: MonitorSample (one reading), MonitorEngine (periodic
//  sampler) and MonitorPanel (embeddable dark-theme UserControl). Per D6 the
//  monitor is a panel tab inside the app plus a small overlay window (A17);
//  it never runs as a hidden background service and only samples while the
//  host wants it (Start/Stop lifecycle is the host's job - A18).
//
//  MonitorEngine: a System.Windows.Forms.Timer drives the sampling so every
//  MonitorSample arrives on the UI thread - both MonitorPanel and the Wave 5
//  overlay can consume SampleReady without any marshaling of their own (the
//  panel still guards with InvokeRequired so a manual RunOnce from a worker
//  thread cannot cross threads). Marshaling decision documented here instead
//  of a System.Threading.Timer: no InvokeRequired dance for consumers, and a
//  2 s cadence never blocks the UI for the microseconds the sampling takes.
//
//  Per tick (each metric individually try/caught; a failure keeps the last
//  known value or reports N/A - never aborts the tick):
//    CPU  - PerformanceCounter "\Processor(_Total)\% Processor Time",
//           created once and primed (a % counter needs one discarded read
//           to anchor the two-sample delta before NextValue() is valid).
//    RAM  - kernel32 GlobalMemoryStatusEx (P/Invoke below, MEMORYSTATUSEX).
//    Disk - PerformanceCounter "\PhysicalDisk(_Total)\% Disk Time",
//           created once and primed like CPU.
//    GPU  - nvidia-smi.exe located once per session (C:\Windows\System32,
//           C:\Program Files\NVIDIA Corporation\NVSMI, then PATH) and run
//           with --query-gpu=utilization.gpu,temperature.gpu (hidden window,
//           short timeout). Absent or failing -> GPU N/A. CPU LoadPercentage
//           is deliberately NOT faked into a GPU reading.
//    CPU temp - WMI root\WMI MSAcpi_ThermalZoneTemperature (CurrentTemperature
//           is tenths of Kelvin -> Celsius); only on machines exposing it.
//
//  Log contract (channel MONITOR): "monitor: started (N ms)" / "monitor:
//  stopped" on the lifecycle, one line when nvidia-smi is found, and the two
//  one-time-per-session unavailability lines
//      monitor: gpu metrics unavailable (no nvidia-smi)
//      monitor: cpu temp unavailable (MSAcpi_ThermalZoneTemperature)
//  (the second exact message also covers a first-fail on a run that later
//  recovers - it is never repeated, so the log cannot be spammed).
//
//  C# 5 only (see docs\HANDBOOK.md section 2). New in v1.1.0 Wave 4 per
//  docs\HANDBOOK.md section 4 (D6) and section 8.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // One monitor reading. Plain public-field DTO consumed by MonitorPanel
    // (Wave 6 tab) and the overlay (Wave 5). The Has* flags say which
    // optional metrics are real for this machine; the matching value fields
    // hold the last-known reading otherwise and must be ignored.
    // ---------------------------------------------------------------------
    public class MonitorSample
    {
        public float CpuPercent;          // 0-100
        public ulong RamUsedBytes;
        public ulong RamTotalBytes;
        public float DiskActivePercent;   // 0-100 (can read >100 on some disks; clamped)
        public float GpuPercent;          // valid when HasGpu
        public float GpuTempC;            // valid when HasGpuTemp
        public float CpuTempC;            // valid when HasCpuTemp
        public bool HasGpu;
        public bool HasGpuTemp;
        public bool HasCpuTemp;
        public DateTime Timestamp;

        // "4.2 / 16.0 GB" - RAM used / total. Invariant format so the panel
        // never depends on the user's decimal separator.
        public string RamText
        {
            get
            {
                double used = RamUsedBytes / 1073741824.0;
                double total = RamTotalBytes / 1073741824.0;
                return string.Format(CultureInfo.InvariantCulture, "{0:0.0} / {1:0.0} GB", used, total);
            }
        }
    }

    // ---------------------------------------------------------------------
    // Sampling engine (static). A Windows.Forms.Timer fires every tick and
    // builds one MonitorSample on the UI thread, then raises SampleReady.
    // Start/Stop may be called repeatedly; Stop disposes the counters and a
    // later Start recreates and re-primes them. Call Start from a UI thread
    // (the host form) - the timer's ticks inherit that thread.
    // ---------------------------------------------------------------------
    public static class MonitorEngine
    {
        public const int DefaultIntervalMs = 2000;
        private const int MinIntervalMs = 250;        // sane floor: % counters need breathing room
        private const int SmiTimeoutMs = 2500;        // short timeout for one nvidia-smi run

        private static readonly object _gate = new object();

        private static Timer _timer;                  // created once on the host UI thread, kept for restarts
        private static PerformanceCounter _cpuCounter;
        private static PerformanceCounter _diskCounter;
        private static ManagementObjectSearcher _cpuTempSearcher;

        // Last-known values: a failed metric keeps these instead of breaking the tick.
        private static float _lastCpu;
        private static float _lastDisk;
        private static float _lastGpu;
        private static float _lastGpuTemp;
        private static float _lastCpuTemp;
        private static ulong _lastRamUsed;
        private static ulong _lastRamTotal;
        private static bool _hasGpu;
        private static bool _hasGpuTemp;
        private static bool _hasCpuTemp;

        // Session-level state for the optional metrics.
        private static bool _smiResolved;             // nvidia-smi lookup done once per session
        private static string _smiPath = "";
        private static bool _gpuDead;                 // no nvidia-smi at all - skip every later tick
        private static bool _gpuUnavailableLogged;    // the one-time unavailability line was written
        private static bool _cpuTempDead;             // sensor never answered - stop querying
        private static bool _cpuTempEverWorked;
        private static bool _cpuTempUnavailableLogged;

        private static MonitorSample _lastSample;

        // Fired every tick with a fresh sample (UI thread when the engine
        // runs on its Forms.Timer; also fired by RunOnce from the caller's
        // thread). Subscribers must tolerate any thread or marshal themselves.
        public static event Action<MonitorSample> SampleReady;

        // True while the engine is sampling.
        public static bool Running
        {
            get { return _timer != null && _timer.Enabled; }
        }

        // Last sample produced (null before the first tick / RunOnce).
        public static MonitorSample LastSample
        {
            get { return _lastSample; }
        }

        // Starts sampling every intervalMs (clamped to >= 250). If already
        // running, just updates the interval. Recreates and re-primes the
        // performance counters after a previous Stop.
        public static void Start(int intervalMs)
        {
            if (intervalMs < MinIntervalMs)
            {
                intervalMs = MinIntervalMs;
            }

            EnsureCounters();

            if (_timer == null)
            {
                _timer = new Timer();                 // System.Windows.Forms.Timer
                _timer.Tick += OnTick;
            }

            _timer.Interval = intervalMs;
            if (!_timer.Enabled)
            {
                _timer.Start();
                Log.Chan("MONITOR", "monitor: started (" + intervalMs.ToString(CultureInfo.InvariantCulture) + " ms)");
            }
        }

        // Starts with the default interval (2000 ms).
        public static void Start()
        {
            Start(DefaultIntervalMs);
        }

        // Stops sampling and disposes the counters (a later Start recreates
        // and re-primes them). Safe to call when not running.
        public static void Stop()
        {
            bool wasRunning = _timer != null && _timer.Enabled;
            if (wasRunning)
            {
                _timer.Stop();
                Log.Chan("MONITOR", "monitor: stopped");
            }
            DisposeCounters();
        }

        // One manual sample, for tests: creates + primes the counters if
        // needed, samples once and fires SampleReady on the caller's thread.
        // Note: a cold call (no prior Start) reads the % counters right after
        // priming, so CPU/disk report ~0 - run Start() for real numbers.
        public static void RunOnce()
        {
            EnsureCounters();
            SampleNow();
        }

        // ---- internals -----------------------------------------------------

        private static void OnTick(object sender, EventArgs e)
        {
            SampleNow();
        }

        // Builds one sample under the lock, then fires the event outside it.
        private static void SampleNow()
        {
            MonitorSample s;
            lock (_gate)
            {
                s = new MonitorSample();
                s.Timestamp = DateTime.Now;

                SampleCpu(s);
                SampleRam(s);
                SampleDisk(s);
                SampleGpu(s);
                SampleCpuTemp(s);

                s.CpuPercent = ClampPct(_lastCpu);
                s.RamUsedBytes = _lastRamUsed;
                s.RamTotalBytes = _lastRamTotal;
                s.DiskActivePercent = ClampPct(_lastDisk);
                s.GpuPercent = ClampPct(_lastGpu);
                s.HasGpu = _hasGpu;
                s.GpuTempC = _lastGpuTemp;
                s.HasGpuTemp = _hasGpu && _hasGpuTemp;
                s.CpuTempC = _lastCpuTemp;
                s.HasCpuTemp = _hasCpuTemp;

                _lastSample = s;
            }

            Action<MonitorSample> handler = SampleReady;
            if (handler != null)
            {
                handler(s);
            }
        }

        // Creates the performance counters once and primes each % counter
        // (one discarded NextValue anchors the delta - without it the first
        // real read reports 0). Counter-creation failures leave the counter
        // null; the per-tick catch then simply keeps the last-known value.
        private static void EnsureCounters()
        {
            lock (_gate)
            {
                if (_cpuCounter == null)
                {
                    try
                    {
                        PerformanceCounter c = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                        c.NextValue();                    // prime: first read is discarded
                        _cpuCounter = c;
                    }
                    catch
                    {
                        _cpuCounter = null;
                    }
                }

                if (_diskCounter == null)
                {
                    try
                    {
                        PerformanceCounter c = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total");
                        c.NextValue();                    // prime
                        _diskCounter = c;
                    }
                    catch
                    {
                        _diskCounter = null;
                    }
                }
            }
        }

        private static void DisposeCounters()
        {
            lock (_gate)
            {
                if (_cpuCounter != null)
                {
                    try { _cpuCounter.Dispose(); } catch { }
                    _cpuCounter = null;
                }
                if (_diskCounter != null)
                {
                    try { _diskCounter.Dispose(); } catch { }
                    _diskCounter = null;
                }
                if (_cpuTempSearcher != null)
                {
                    try { _cpuTempSearcher.Dispose(); } catch { }
                    _cpuTempSearcher = null;
                }
            }
        }

        // ---- per-tick samplers (each fails soft) ---------------------------

        private static void SampleCpu(MonitorSample s)
        {
            try
            {
                if (_cpuCounter != null)
                {
                    float v = _cpuCounter.NextValue();
                    if (v < 0f) v = 0f;                   // rounding can dip slightly below 0
                    _lastCpu = v;
                }
            }
            catch
            {
                // keep last-known
            }
        }

        private static void SampleDisk(MonitorSample s)
        {
            try
            {
                if (_diskCounter != null)
                {
                    float v = _diskCounter.NextValue();
                    if (v < 0f) v = 0f;
                    _lastDisk = v;                        // can exceed 100 on some disks; clamped on publish
                }
            }
            catch
            {
                // keep last-known
            }
        }

        // kernel32 GlobalMemoryStatusEx (MEMORYSTATUSEX) - no PerformanceCounter.
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private static void SampleRam(MonitorSample s)
        {
            try
            {
                MEMORYSTATUSEX st = new MEMORYSTATUSEX();
                st.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref st))
                {
                    _lastRamUsed = st.ullTotalPhys - st.ullAvailPhys;
                    _lastRamTotal = st.ullTotalPhys;
                }
            }
            catch
            {
                // keep last-known
            }
        }

        // GPU utilization + temperature via nvidia-smi.exe. Resolution and
        // failure reporting happen once per session; a run that fails later
        // stays silent and just reports N/A until it recovers.
        private static void SampleGpu(MonitorSample s)
        {
            if (_gpuDead)
            {
                return;                                   // HasGpu stays false - CPU LoadPercentage is never faked in
            }

            if (!_smiResolved)
            {
                _smiResolved = true;
                _smiPath = FindNvidiaSmi();
                if (_smiPath.Length == 0)
                {
                    _gpuDead = true;
                    LogGpuUnavailableOnce("monitor: gpu metrics unavailable (no nvidia-smi)");
                    return;
                }
                Log.Chan("MONITOR", "monitor: gpu metrics via nvidia-smi (" + _smiPath + ")");
            }

            try
            {
                float util;
                float temp;
                bool hasTemp;
                if (RunNvidiaSmi(out util, out temp, out hasTemp))
                {
                    _lastGpu = util;
                    _hasGpu = true;
                    if (hasTemp)
                    {
                        _lastGpuTemp = temp;
                        _hasGpuTemp = true;
                    }
                    else
                    {
                        _hasGpuTemp = false;              // driver answered, but no temp field
                    }
                }
                else
                {
                    _hasGpu = false;                      // report N/A rather than a stale number
                    _hasGpuTemp = false;
                    LogGpuUnavailableOnce("monitor: gpu metrics unavailable (nvidia-smi run failed)");
                }
            }
            catch
            {
                _hasGpu = false;
                _hasGpuTemp = false;
                LogGpuUnavailableOnce("monitor: gpu metrics unavailable (nvidia-smi run failed)");
            }
        }

        private static void LogGpuUnavailableOnce(string message)
        {
            if (_gpuUnavailableLogged)
            {
                return;
            }
            _gpuUnavailableLogged = true;
            Log.Chan("MONITOR", message);
        }

        // Locates nvidia-smi.exe once: C:\Windows\System32, then the classic
        // NVSMI folder, then every PATH entry. "" when nowhere.
        private static string FindNvidiaSmi()
        {
            try
            {
                List<string> candidates = new List<string>();
                candidates.Add(@"C:\Windows\System32\nvidia-smi.exe");
                try
                {
                    candidates.Add(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"));
                }
                catch
                {
                }
                candidates.Add(@"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe");

                string pathVar = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(pathVar))
                {
                    string[] dirs = pathVar.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < dirs.Length; i++)
                    {
                        string dir = dirs[i].Trim();
                        if (dir.Length == 0)
                        {
                            continue;
                        }
                        try
                        {
                            candidates.Add(Path.Combine(dir, "nvidia-smi.exe"));
                        }
                        catch
                        {
                        }
                    }
                }

                for (int i = 0; i < candidates.Count; i++)
                {
                    string path = candidates[i];
                    try
                    {
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        {
                            return path;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            return "";
        }

        // Runs `nvidia-smi --query-gpu=utilization.gpu,temperature.gpu
        // --format=csv,noheader,nounits` (hidden, short timeout) and parses
        // the first line "util, temp". Output is tiny, so reading stdout to
        // the end before the timeout wait cannot deadlock.
        private static bool RunNvidiaSmi(out float util, out float temp, out bool hasTemp)
        {
            util = 0f;
            temp = 0f;
            hasTemp = false;

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = _smiPath;
            psi.Arguments = "--query-gpu=utilization.gpu,temperature.gpu --format=csv,noheader,nounits";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using (Process p = Process.Start(psi))
            {
                if (p == null)
                {
                    return false;
                }
                string output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(SmiTimeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return false;
                }
                if (p.ExitCode != 0)
                {
                    return false;
                }
                return ParseGpuOutput(output, out util, out temp, out hasTemp);
            }
        }

        // First non-empty line: "34, 45" (nounits). A "N/A" temp is allowed -
        // utilization only. Multiple GPUs: the first line wins.
        private static bool ParseGpuOutput(string output, out float util, out float temp, out bool hasTemp)
        {
            util = 0f;
            temp = 0f;
            hasTemp = false;

            if (string.IsNullOrEmpty(output))
            {
                return false;
            }

            string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                string[] parts = line.Split(',');
                if (parts.Length < 2)
                {
                    return false;
                }

                float u;
                if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out u))
                {
                    return false;
                }

                string tempText = parts[1].Trim();
                float t;
                if (tempText.Equals("n/a", StringComparison.OrdinalIgnoreCase) ||
                    !float.TryParse(tempText, NumberStyles.Float, CultureInfo.InvariantCulture, out t))
                {
                    util = u;
                    hasTemp = false;
                    return true;
                }

                util = u;
                temp = t;
                hasTemp = true;
                return true;
            }
            return false;
        }

        // CPU temperature via WMI root\WMI MSAcpi_ThermalZoneTemperature
        // (CurrentTemperature is tenths of Kelvin). Only queried while the
        // sensor answers; the first failure logs the one-time line and the
        // metric reports N/A for the rest of the session.
        private static void SampleCpuTemp(MonitorSample s)
        {
            if (_cpuTempDead)
            {
                return;
            }

            try
            {
                float c = ReadCpuTempWmi();
                _lastCpuTemp = c;
                _hasCpuTemp = true;
                _cpuTempEverWorked = true;
            }
            catch
            {
                if (!_cpuTempEverWorked)
                {
                    if (!_cpuTempUnavailableLogged)
                    {
                        _cpuTempUnavailableLogged = true;
                        Log.Chan("MONITOR", "monitor: cpu temp unavailable (MSAcpi_ThermalZoneTemperature)");
                    }
                    _cpuTempDead = true;
                    _hasCpuTemp = false;
                }
                // else: a working sensor hiccupped - keep the last-known value
            }
        }

        // Returns the hottest plausible zone temperature in Celsius. Zones
        // outside -50..150 C are treated as garbage (some boards report raw
        // ambient placeholders). Throws when the class is unavailable.
        private static float ReadCpuTempWmi()
        {
            if (_cpuTempSearcher == null)
            {
                _cpuTempSearcher = new ManagementObjectSearcher(
                    "root\\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            }

            float best = float.MinValue;
            using (ManagementObjectCollection results = _cpuTempSearcher.Get())
            {
                foreach (ManagementObject zone in results)
                {
                    try
                    {
                        object raw = zone["CurrentTemperature"];
                        if (raw == null)
                        {
                            continue;
                        }
                        float tenthsKelvin = Convert.ToUInt32(raw);
                        float celsius = tenthsKelvin / 10f - 273.15f;
                        if (celsius > best && celsius > -50f && celsius < 150f)
                        {
                            best = celsius;
                        }
                    }
                    catch
                    {
                        // unreadable zone - try the next one
                    }
                    finally
                    {
                        try { zone.Dispose(); } catch { }
                    }
                }
            }

            if (best == float.MinValue)
            {
                throw new InvalidOperationException("no usable MSAcpi_ThermalZoneTemperature reading");
            }
            return best;
        }

        private static float ClampPct(float v)
        {
            if (v < 0f) return 0f;
            if (v > 100f) return 100f;
            return v;
        }
    }

    // ---------------------------------------------------------------------
    // Embeddable dark-theme panel: a grid of labeled bars (CPU %, RAM,
    // Disk %, GPU %, GPU temp, CPU temp) plus an "updated HH:mm:ss" footer.
    // Deterministic TableLayoutPanel layout (LogForm pattern) that survives
    // any DPI and resize; minimum 360x180. AttachToEngine/DetachFromEngine
    // manage the SampleReady subscription; Dispose detaches on its own.
    // ---------------------------------------------------------------------
    public class MonitorPanel : UserControl
    {
        private const int RowCpu = 0;
        private const int RowRam = 1;
        private const int RowDisk = 2;
        private const int RowGpu = 3;
        private const int RowGpuTemp = 4;
        private const int RowCpuTemp = 5;
        private const int RowFooter = 6;

        // Dark palette - same inline values as the rest of the UI (Theme.cs
        // keeps colors at the call sites; ShimmerBar's track + MainForm's
        // accent green are reused here).
        private static readonly Color PanelBack = Color.FromArgb(24, 24, 28);
        private static readonly Color BarTrack = Color.FromArgb(40, 40, 47);
        private static readonly Color BarFill = Color.FromArgb(76, 195, 138);
        private static readonly Color TextMain = Color.FromArgb(220, 220, 226);
        private static readonly Color TextMuted = Color.FromArgb(150, 150, 158);

        private readonly TableLayoutPanel _grid = new TableLayoutPanel();
        private readonly ProgressBar[] _bars = new ProgressBar[6];
        private readonly Label[] _values = new Label[6];
        private readonly Label _footer = new Label();
        private bool _attached;

        public MonitorPanel()
        {
            DoubleBuffered = true;
            BackColor = PanelBack;
            Size = new Size(430, 250);
            MinimumSize = new Size(360, 180);

            _grid.Dock = DockStyle.Fill;
            _grid.ColumnCount = 3;
            _grid.RowCount = 7;
            _grid.Padding = new Padding(10, 8, 10, 8);
            _grid.BackColor = PanelBack;
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));          // metric name
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));      // bar
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));          // value
            for (int i = 0; i < 7; i++)
            {
                _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            string[] titles = new string[] { "CPU", "RAM", "Disk", "GPU", "GPU temp", "CPU temp" };
            for (int i = 0; i < 6; i++)
            {
                Label name = new Label();
                name.Text = titles[i];
                name.AutoSize = true;
                name.ForeColor = TextMuted;
                name.BackColor = PanelBack;
                name.TextAlign = ContentAlignment.MiddleLeft;
                name.Margin = new Padding(0, 5, 8, 0);

                ProgressBar bar = new ProgressBar();
                bar.Minimum = 0;
                bar.Maximum = 100;
                bar.Value = 0;
                bar.Height = 14;
                bar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                bar.Margin = new Padding(0, 6, 8, 2);
                // Classic-mode rendering honors ForeColor/BackColor, which the
                // themed renderer ignores; re-applied whenever the handle is
                // recreated (theme switch, DPI change, ...).
                bar.ForeColor = BarFill;
                bar.BackColor = BarTrack;
                bar.HandleCreated += delegate { StyleBarHandle(bar); };
                if (bar.IsHandleCreated)
                {
                    StyleBarHandle(bar);
                }

                Label val = new Label();
                val.Text = "--";
                val.AutoSize = true;
                val.ForeColor = TextMain;
                val.BackColor = PanelBack;
                val.TextAlign = ContentAlignment.MiddleLeft;
                val.Margin = new Padding(0, 5, 0, 0);

                _bars[i] = bar;
                _values[i] = val;
                _grid.Controls.Add(name, 0, i);
                _grid.Controls.Add(bar, 1, i);
                _grid.Controls.Add(val, 2, i);
            }

            _footer.Text = "updated --:--:--";
            _footer.AutoSize = true;
            _footer.ForeColor = TextMuted;
            _footer.BackColor = PanelBack;
            _footer.TextAlign = ContentAlignment.MiddleLeft;
            _footer.Margin = new Padding(0, 10, 0, 0);
            _grid.Controls.Add(_footer, 0, RowFooter);
            _grid.SetColumnSpan(_footer, 3);

            Controls.Add(_grid);
        }

        // Subscribes to the engine (idempotent) and immediately paints the
        // engine's last sample, if any, so the panel never shows blanks after
        // a re-attach.
        public void AttachToEngine()
        {
            if (_attached)
            {
                return;
            }
            MonitorEngine.SampleReady += OnSampleReady;
            _attached = true;
            MonitorSample last = MonitorEngine.LastSample;
            if (last != null)
            {
                OnSampleReady(last);
            }
        }

        public void DetachFromEngine()
        {
            if (!_attached)
            {
                return;
            }
            MonitorEngine.SampleReady -= OnSampleReady;
            _attached = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DetachFromEngine();
            }
            base.Dispose(disposing);
        }

        // ---- sample application --------------------------------------------

        private void OnSampleReady(MonitorSample s)
        {
            if (s == null)
            {
                return;
            }
            // The engine's Forms.Timer fires on the UI thread, so this is
            // usually a no-op check; a manual RunOnce from a worker thread
            // (tests) is marshaled instead of crossing threads.
            if (InvokeRequired)
            {
                if (!IsHandleCreated || IsDisposed)
                {
                    return;                               // next tick repaints
                }
                BeginInvoke(new Action<MonitorSample>(OnSampleReady), s);
                return;
            }
            ApplySample(s);
        }

        private void ApplySample(MonitorSample s)
        {
            try
            {
                SetBar(RowCpu, s.CpuPercent);
                _values[RowCpu].Text = FmtPercent(s.CpuPercent);

                double ramPct = s.RamTotalBytes > 0
                    ? s.RamUsedBytes * 100.0 / s.RamTotalBytes
                    : 0.0;
                SetBar(RowRam, (float)ramPct);
                _values[RowRam].Text = s.RamText;

                SetBar(RowDisk, s.DiskActivePercent);
                _values[RowDisk].Text = FmtPercent(s.DiskActivePercent);

                if (s.HasGpu)
                {
                    SetBar(RowGpu, s.GpuPercent);
                    _values[RowGpu].Text = FmtPercent(s.GpuPercent);
                }
                else
                {
                    SetBar(RowGpu, 0f);
                    _values[RowGpu].Text = "N/A";
                }

                if (s.HasGpuTemp)
                {
                    SetBar(RowGpuTemp, s.GpuTempC);       // 0-100 C on the same bar scale
                    _values[RowGpuTemp].Text = FmtTemp(s.GpuTempC);
                }
                else
                {
                    SetBar(RowGpuTemp, 0f);
                    _values[RowGpuTemp].Text = "N/A";
                }

                if (s.HasCpuTemp)
                {
                    SetBar(RowCpuTemp, s.CpuTempC);
                    _values[RowCpuTemp].Text = FmtTemp(s.CpuTempC);
                }
                else
                {
                    SetBar(RowCpuTemp, 0f);
                    _values[RowCpuTemp].Text = "N/A";
                }

                _footer.Text = "updated " + s.Timestamp.ToString("HH:mm:ss");
            }
            catch
            {
                // Cosmetic UI path only - a bad sample must never break the app.
            }
        }

        private void SetBar(int row, float value)
        {
            int v = (int)Math.Round(value);
            if (v < 0) v = 0;
            if (v > 100) v = 100;                         // ProgressBar.Value throws outside 0-100
            _bars[row].Value = v;
        }

        private static string FmtPercent(float value)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} %", value);
        }

        private static string FmtTemp(float celsius)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0} \u00B0C", celsius);
        }

        // Switches the ProgressBar to classic (non-visual-styles) rendering so
        // the dark track/fill colors are actually used instead of the system
        // green-on-white theme. Best-effort: if uxtheme refuses, the default
        // themed bar still works.
        private static void StyleBarHandle(ProgressBar bar)
        {
            try
            {
                SetWindowTheme(bar.Handle, "", "");
            }
            catch
            {
            }
        }

        [DllImport("uxtheme.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string subAppName, string subAppList);
    }
}
