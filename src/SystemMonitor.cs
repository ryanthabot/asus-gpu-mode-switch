//  SystemMonitor.cs  (v1.1.0 - Wave 4, agent A16)
//  -----------------------------------------------
//  Live system monitor: MonitorSample (one reading), MonitorEngine (periodic
//  sampler) and MonitorPanel (embeddable dark-theme UserControl). Per D6 the
//  monitor is a panel tab inside the app plus a small overlay window (A17);
//  it never runs as a hidden background service and only samples while the
//  host wants it (Start/Stop lifecycle is the host's job - A18).
//
//  MonitorEngine: a System.Windows.Forms.Timer schedules the sampling, but
//  (v1.2.2) the tick itself only queues the real work onto the thread pool -
//  counter priming, WMI queries and nvidia-smi can all block for a long time
//  on a degraded WMI/PDH stack (observed 12 s healthy, minutes on a sick
//  one), and any of that on the UI thread froze the whole window at startup.
//  SampleReady may therefore fire on any thread; both MonitorPanel and the
//  overlay already marshal via InvokeRequired/BeginInvoke.
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
//  v1.2.3 sensor expansion: the dGPU is its own metric (nvidia-smi line 0;
//  the legacy GpuPercent/GpuTempC/HasGpu/HasGpuTemp stay as dGPU aliases so
//  the overlay/session code is untouched), a best-effort iGPU utilization
//  via the "GPU Engine" counter category (which phys id is the dGPU is
//  learned by correlating the per-phys 3D utilization with the nvidia-smi
//  number over a few samples, then cached; honest N/A until proven), the
//  mean of all plausible ACPI zones next to the hottest-zone legacy field,
//  and per-fixed-drive ("LogicalDisk","% Disk Time","C:") counters next to
//  the combined PhysicalDisk _Total row. HasCpuHotspot / HasIgpuTemp are
//  always false - Windows exposes no driverless API for either and neither
//  is ever faked. The monitor's disk-view choice persists like the interval
//  (monitor_diskview.txt).
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
        public float GpuPercent;          // LEGACY alias of DgpuPercent (valid when HasGpu)
        public float GpuTempC;            // LEGACY alias of DgpuTempC (valid when HasGpuTemp)
        public float CpuTempC;            // hottest plausible ACPI zone (valid when HasCpuTemp)
        public bool HasGpu;               // LEGACY alias of HasDgpu
        public bool HasGpuTemp;           // LEGACY alias of HasDgpuTemp
        public bool HasCpuTemp;
        public DateTime Timestamp;

        // v1.2.3 sensor expansion. The dGPU is the discrete NVIDIA adapter
        // (nvidia-smi GPU 0 on this app's target machines); the iGPU is every
        // other "GPU Engine" phys once the phys mapping is proven.
        public float DgpuPercent;         // valid when HasDgpu
        public float DgpuTempC;           // valid when HasDgpuTemp
        public bool HasDgpu;
        public bool HasDgpuTemp;
        public float IgpuPercent;         // valid when HasIgpu
        public bool HasIgpu;              // false until the phys mapping is proven
        public float CpuTempAvgC;         // mean of all plausible zones; valid when HasCpuTempAvg
        public bool HasCpuTempAvg;        // tracks HasCpuTemp (same sensor, averaged)
        public bool HasCpuHotspot;        // ALWAYS false - no driverless Windows API; never faked
        public bool HasIgpuTemp;          // ALWAYS false - no driverless Windows API; never faked

        // Per-fixed-drive utilization: DiskNames[i] is a letter ("C:", "D:",
        // ... - every DriveType=3 drive found), DiskPct[i] its % Disk Time
        // (clamped). DiskActivePercent stays the PhysicalDisk _Total value.
        public string[] DiskNames;
        public float[] DiskPct;

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

        // Refresh-rate choices offered by the Monitor page dropdown (v1.2.2).
        public static readonly int[] IntervalChoicesMs = { 1000, 2000, 5000, 10000, 15000 };
        private static readonly string IntervalFile = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GpuModeSwitch", "monitor_interval.txt");

        // Saved interval (clamped to the choice set; default 2000). Read at
        // startup so the dropdown and engine agree across sessions.
        public static int SavedIntervalMs
        {
            get
            {
                try
                {
                    if (System.IO.File.Exists(IntervalFile))
                    {
                        int v;
                        if (int.TryParse(System.IO.File.ReadAllText(IntervalFile).Trim(), out v))
                        {
                            foreach (int c in IntervalChoicesMs) if (c == v) return v;
                        }
                    }
                }
                catch { }
                return DefaultIntervalMs;
            }
            set
            {
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(IntervalFile));
                    System.IO.File.WriteAllText(IntervalFile, value.ToString(CultureInfo.InvariantCulture));
                }
                catch { }   // persistence is best-effort; the live interval still applies
            }
        }

        // Disk-view choices for the Monitor page dropdown (v1.2.3): which
        // per-disk rows MonitorPanel shows. "combined" = the single
        // "Disks (combined)" row; "both" = one row per fixed drive plus the
        // combined row. Persisted like the interval.
        public static readonly string[] DiskViewChoices = { "C", "D", "both", "combined" };
        public const string DefaultDiskView = "both";
        private static readonly string DiskViewFile = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GpuModeSwitch", "monitor_diskview.txt");

        // Saved disk view (validated against the choice set; default "both").
        public static string SavedDiskView
        {
            get
            {
                try
                {
                    if (System.IO.File.Exists(DiskViewFile))
                    {
                        string v = System.IO.File.ReadAllText(DiskViewFile).Trim();
                        foreach (string c in DiskViewChoices)
                        {
                            if (c == v) return v;
                        }
                    }
                }
                catch { }
                return DefaultDiskView;
            }
            set
            {
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DiskViewFile));
                    System.IO.File.WriteAllText(DiskViewFile, value);
                }
                catch { }   // best-effort, like the interval
            }
        }

        // (v1.2.2) Counter creation and every sample can block for a long
        // time on a degraded WMI/PDH stack (observed: ~12 s just to prime on
        // a healthy machine, minutes-to-forever on one with a sick WMI repo,
        // which froze the whole UI at startup). Neither ever runs on the UI
        // thread anymore: priming happens once on a pool thread and the
        // Forms.Timer tick only schedules the sample onto the pool. The
        // SampleReady event was already documented as any-thread (both
        // subscribers marshal via BeginInvoke).
        private static volatile bool _priming;
        private static volatile bool _sampling;

        private static readonly object _gate = new object();

        private static Timer _timer;                  // created once on the host UI thread, kept for restarts
        private static PerformanceCounter _cpuCounter;
        private static PerformanceCounter _diskCounter;
        private static ManagementObjectSearcher _cpuTempSearcher;

        // (v1.2.3) per-fixed-drive counters: one ("LogicalDisk","% Disk
        // Time","C:") per DriveType=3 drive found, created once per Ensure
        // cycle on the pool thread. _diskNamesSnapshot lets UI threads read
        // the discovered letters without taking _gate (a sample can hold it
        // for the whole nvidia-smi timeout).
        private static readonly List<string> _diskNames = new List<string>();
        private static Dictionary<string, PerformanceCounter> _diskCounters;
        private static volatile string[] _diskNamesSnapshot = new string[0];

        // (v1.2.3) "GPU Engine" utilization counters for the iGPU metric.
        // Instance names change as processes start and stop, so the list is
        // rebuilt every ~5 samples (category enumeration is expensive - never
        // per sample).
        private static Dictionary<string, PerformanceCounter> _gpuEngCounters;
        private static int _gpuEngRefreshIn = 1;      // rebuild right after the first sample
        private const int GpuEngRefreshEvery = 5;

        // Last-known values: a failed metric keeps these instead of breaking the tick.
        private static float _lastCpu;
        private static float _lastDisk;
        private static float _lastGpu;
        private static float _lastGpuTemp;
        private static float _lastCpuTemp;
        private static float _lastCpuTempAvg;
        private static float _lastIgpu;
        private static ulong _lastRamUsed;
        private static ulong _lastRamTotal;
        private static readonly Dictionary<string, float> _lastDiskByLetter = new Dictionary<string, float>();
        private static bool _hasGpu;
        private static bool _hasGpuTemp;
        private static bool _hasCpuTemp;
        private static bool _hasIgpu;

        // Session-level state for the optional metrics.
        private static bool _smiResolved;             // nvidia-smi lookup done once per session
        private static string _smiPath = "";
        private static bool _gpuDead;                 // no nvidia-smi at all - skip every later tick
        private static bool _gpuUnavailableLogged;    // the one-time unavailability line was written
        private static bool _cpuTempDead;             // sensor never answered - stop querying
        private static bool _cpuTempEverWorked;
        private static bool _cpuTempUnavailableLogged;

        // iGPU correlation state: which "phys_N" segment of the GPU Engine
        // instances is the NVIDIA dGPU. Learned once (nvidia-smi % vs each
        // phys 3D-utilization sum over a rolling window), cached for the
        // session; every other phys is the iGPU.
        private static bool _gpuEngDead;              // category never answered - stop querying
        private static bool _gpuEngEverWorked;
        private static bool _gpuEngUnavailableLogged;
        private static int _dgpuPhys = -1;            // -1 = not proven yet
        private static int _physSeenMask;             // bit N set = phys_N seen at least once
        private static readonly List<float> _smiHist = new List<float>();
        private static readonly List<Dictionary<int, float>> _physHist = new List<Dictionary<int, float>>();
        private const int PhysHistMax = 6;
        private const float CorrelateTolerancePct = 15f;

        private static MonitorSample _lastSample;

        // Fired every tick with a fresh sample. Since v1.2.2 samples run on
        // pool threads (the UI-thread tick only schedules them), so this can
        // fire on any thread. Subscribers must tolerate any thread or
        // marshal themselves.
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

        // Fixed logical disks discovered for this machine (letters like
        // "C:"), for UIs that lay rows out before the first sample. Lock-free:
        // a UI thread must never wait on _gate (a sample holds it for the
        // nvidia-smi timeout).
        public static string[] FixedDisks
        {
            get { return _diskNamesSnapshot; }
        }

        // Starts sampling every intervalMs (clamped to >= 250). If already
        // running, just updates the interval. Counter priming is queued to a
        // pool thread (see _priming) - Start itself never blocks.
        public static void Start(int intervalMs)
        {
            if (intervalMs < MinIntervalMs)
            {
                intervalMs = MinIntervalMs;
            }

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
            PrimeCountersAsync();
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
        // needed (BLOCKING - unlike Start), samples once and fires
        // SampleReady on the caller's thread. Note: a cold call (no prior
        // Start) reads the % counters right after priming, so CPU/disk
        // report ~0 - run Start() for real numbers.
        public static void RunOnce()
        {
            EnsureCounters();
            SampleNow();
        }

        // ---- internals -----------------------------------------------------

        private static void OnTick(object sender, EventArgs e)
        {
            // Ticks fire on the UI thread: never sample there (a sick WMI
            // stack would freeze the whole window). Skip while priming or
            // while a previous sample is still running - the next tick
            // catches up.
            if (_priming || _sampling) return;
            _sampling = true;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try { SampleNow(); }
                finally { _sampling = false; }
            });
        }

        // Queues counter creation to the pool once per need. Harmless if the
        // counters already exist or a priming run is in flight.
        private static void PrimeCountersAsync()
        {
            if (_cpuCounter != null && _diskCounter != null && _diskCounters != null
                && (_gpuEngCounters != null || _gpuEngDead)) return;
            if (_priming) return;
            _priming = true;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try { EnsureCounters(); }
                finally { _priming = false; }
            });
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
                SampleIgpu(s);                        // after SampleGpu: correlation uses this tick's smi number
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

                // v1.2.3: the dGPU is its own metric; the legacy Gpu* fields
                // above stay as aliases (overlay / session code unchanged).
                s.DgpuPercent = ClampPct(_lastGpu);
                s.HasDgpu = _hasGpu;
                s.DgpuTempC = _lastGpuTemp;
                s.HasDgpuTemp = _hasGpu && _hasGpuTemp;
                s.IgpuPercent = ClampPct(_lastIgpu);
                s.HasIgpu = _hasIgpu;
                s.CpuTempAvgC = _lastCpuTempAvg;
                s.HasCpuTempAvg = _hasCpuTemp;
                s.HasCpuHotspot = false;              // honest: no driverless hotspot API
                s.HasIgpuTemp = false;                // honest: no driverless iGPU temp API

                string[] names = _diskNames.ToArray();
                float[] pct = new float[names.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    float v;
                    pct[i] = _lastDiskByLetter.TryGetValue(names[i], out v) ? ClampPct(v) : 0f;
                }
                s.DiskNames = names;
                s.DiskPct = pct;
                _diskNamesSnapshot = names;

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

                if (_diskCounters == null)
                {
                    BuildLogicalDiskCounters();
                }

                if (_gpuEngCounters == null && !_gpuEngDead)
                {
                    RefreshGpuEngineCounters();
                    _gpuEngRefreshIn = GpuEngRefreshEvery;
                }
            }
        }

        private static void DisposeCounters()
        {
            // Never wait: if a pool thread is priming/sampling under the
            // gate, skip disposal (a couple of leaked PDH handles until
            // process exit beat a frozen UI). The next Start re-primes.
            if (!System.Threading.Monitor.TryEnter(_gate)) return;
            try
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
                if (_diskCounters != null)
                {
                    foreach (KeyValuePair<string, PerformanceCounter> kv in _diskCounters)
                    {
                        try { kv.Value.Dispose(); } catch { }
                    }
                    _diskCounters = null;
                }
                if (_gpuEngCounters != null)
                {
                    foreach (KeyValuePair<string, PerformanceCounter> kv in _gpuEngCounters)
                    {
                        try { kv.Value.Dispose(); } catch { }
                    }
                    _gpuEngCounters = null;
                }
                // The phys mapping is machine-stable and stays cached; the
                // time-adjacent correlation history does not survive a stop.
                _smiHist.Clear();
                _physHist.Clear();
                _gpuEngRefreshIn = 1;
                if (_cpuTempSearcher != null)
                {
                    try { _cpuTempSearcher.Dispose(); } catch { }
                    _cpuTempSearcher = null;
                }
            }
            finally
            {
                System.Threading.Monitor.Exit(_gate);
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

            if (_diskCounters != null)
            {
                foreach (KeyValuePair<string, PerformanceCounter> kv in _diskCounters)
                {
                    try
                    {
                        float v = kv.Value.NextValue();
                        if (v < 0f) v = 0f;
                        _lastDiskByLetter[kv.Key] = v;    // clamped on publish
                    }
                    catch
                    {
                        // keep last-known for this letter
                    }
                }
            }
        }

        // One ("LogicalDisk","% Disk Time","X:") counter per fixed drive
        // (DriveType=3) - every fixed drive on the machine, not just C:/D:.
        // A drive whose counter fails to create is simply not reported.
        // Caller must hold _gate (pool thread only - counter creation).
        private static void BuildLogicalDiskCounters()
        {
            Dictionary<string, PerformanceCounter> built = new Dictionary<string, PerformanceCounter>();
            List<string> names = new List<string>();
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    DriveInfo d = drives[i];
                    if (d.DriveType != DriveType.Fixed)
                    {
                        continue;
                    }
                    string letter;
                    try
                    {
                        letter = d.Name.TrimEnd('\\');    // "C:\" -> "C:"
                    }
                    catch
                    {
                        continue;
                    }
                    if (letter.Length == 0)
                    {
                        continue;
                    }
                    try
                    {
                        PerformanceCounter c = new PerformanceCounter("LogicalDisk", "% Disk Time", letter);
                        c.NextValue();                    // prime
                        built[letter] = c;
                        names.Add(letter);
                    }
                    catch
                    {
                        // no counter for this letter - skip it
                    }
                }
            }
            catch
            {
                // DriveInfo enumeration failed - report no per-letter disks
            }
            _diskCounters = built;
            _diskNames.Clear();
            _diskNames.AddRange(names);
            _diskNamesSnapshot = names.ToArray();
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
                float[] utils;
                float[] temps;
                bool[] hasTemps;
                if (RunNvidiaSmi(out utils, out temps, out hasTemps) && utils.Length > 0)
                {
                    // dGPU = nvidia-smi GPU 0 (line 0) - the discrete adapter
                    // on this app's single-NVIDIA-GPU target machines. The
                    // later lines exist for multi-NVIDIA-GPU boxes; only GPU 0
                    // is reported today, same as v1.2.
                    _lastGpu = utils[0];
                    _hasGpu = true;
                    if (hasTemps[0])
                    {
                        _lastGpuTemp = temps[0];
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
        // EVERY output line (one per NVIDIA GPU; the dGPU is line 0). Output
        // is tiny, so reading stdout to the end before the timeout wait
        // cannot deadlock.
        private static bool RunNvidiaSmi(out float[] utils, out float[] temps, out bool[] hasTemps)
        {
            utils = new float[0];
            temps = new float[0];
            hasTemps = new bool[0];

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
                return ParseGpuOutput(output, out utils, out temps, out hasTemps);
            }
        }

        // Every non-empty line: "util, temp" (nounits), one line per NVIDIA
        // GPU in ascending index order; index 0 is the dGPU. A "N/A" temp on
        // a line is allowed - utilization only for that GPU. Any malformed
        // line fails the whole parse (the strictness of the v1.2 first-line
        // parser, applied to every line). Public for the offline parser tests
        // (probe harness).
        public static bool ParseGpuOutput(string output, out float[] utils, out float[] temps, out bool[] hasTemps)
        {
            utils = new float[0];
            temps = new float[0];
            hasTemps = new bool[0];

            if (string.IsNullOrEmpty(output))
            {
                return false;
            }

            List<float> u = new List<float>();
            List<float> t = new List<float>();
            List<bool> h = new List<bool>();

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

                float util;
                if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out util))
                {
                    return false;
                }

                string tempText = parts[1].Trim();
                float temp;
                if (tempText.Equals("n/a", StringComparison.OrdinalIgnoreCase) ||
                    !float.TryParse(tempText, NumberStyles.Float, CultureInfo.InvariantCulture, out temp))
                {
                    u.Add(util);
                    t.Add(0f);
                    h.Add(false);
                }
                else
                {
                    u.Add(util);
                    t.Add(temp);
                    h.Add(true);
                }
            }

            if (u.Count == 0)
            {
                return false;
            }
            utils = u.ToArray();
            temps = t.ToArray();
            hasTemps = h.ToArray();
            return true;
        }

        // ---- iGPU (best effort, v1.2.3) -------------------------------------
        //
        // Windows exposes per-engine utilization in the "GPU Engine"
        // performance category; instance names look like
        //   pid_1234_luid_0x00000000_0x0000C3B7_phys_0_eng_3_engtype_3D
        // The numeric "phys_N" segment identifies the physical adapter, but
        // WHICH phys id is the NVIDIA dGPU is documented nowhere. So it is
        // learned once: while nvidia-smi is alive, the per-phys 3D
        // utilization sum is compared against the nvidia-smi dGPU number over
        // a rolling window of samples; the phys that tracks it (within
        // tolerance, and clearly better than the runner-up, and only once the
        // dGPU actually did something - at 0% every adapter "matches") is the
        // dGPU. The mapping is cached for the session and every OTHER phys is
        // the iGPU. Until the mapping is proven - or forever on
        // single-adapter machines - the iGPU honestly reports N/A. Any
        // category failure marks the metric dead for the session (the
        // _gpuDead pattern).

        private static void SampleIgpu(MonitorSample s)
        {
            if (_gpuEngDead || _gpuEngCounters == null)
            {
                return;
            }

            _gpuEngRefreshIn--;
            if (_gpuEngRefreshIn <= 0)
            {
                RefreshGpuEngineCounters();               // every ~5 samples; enumeration is expensive
                _gpuEngRefreshIn = GpuEngRefreshEvery;
            }
            if (_gpuEngDead)
            {
                return;
            }

            try
            {
                Dictionary<int, float> physNow = new Dictionary<int, float>();
                foreach (KeyValuePair<string, PerformanceCounter> kv in _gpuEngCounters)
                {
                    try
                    {
                        float v = kv.Value.NextValue();
                        if (v < 0f) v = 0f;
                        int phys = ParsePhysId(kv.Key);
                        if (phys < 0)
                        {
                            continue;
                        }
                        float sum;
                        physNow[phys] = physNow.TryGetValue(phys, out sum) ? sum + v : v;
                        if (phys < 31)
                        {
                            _physSeenMask |= 1 << phys;
                        }
                    }
                    catch
                    {
                        // dead instance - dropped at the next refresh
                    }
                }

                if (_hasGpu)
                {
                    // correlation input: this tick's nvidia-smi dGPU number
                    _smiHist.Add(_lastGpu);
                    _physHist.Add(physNow);
                    while (_smiHist.Count > PhysHistMax)
                    {
                        _smiHist.RemoveAt(0);
                        _physHist.RemoveAt(0);
                    }
                    if (_dgpuPhys < 0 && _smiHist.Count >= 3)
                    {
                        TryCorrelateDgpuPhys();
                    }
                }

                if (_dgpuPhys >= 0)
                {
                    float igpu = 0f;
                    bool seen = false;
                    foreach (KeyValuePair<int, float> kv in physNow)
                    {
                        if (kv.Key == _dgpuPhys)
                        {
                            continue;
                        }
                        igpu += kv.Value;
                        seen = true;
                    }
                    if (seen)
                    {
                        _lastIgpu = igpu;                // multi-engine sums can pass 100; clamped on publish
                        _hasIgpu = true;
                    }
                    // else: no non-dGPU 3D engine right now - keep last-known
                }
                _gpuEngEverWorked = true;
            }
            catch
            {
                if (!_gpuEngEverWorked)
                {
                    _gpuEngDead = true;
                    _hasIgpu = false;
                    LogGpuEngUnavailableOnce("monitor: igpu metrics unavailable (GPU Engine category)");
                }
                // else: a working stack hiccupped - keep the last-known value
            }
        }

        private static void LogGpuEngUnavailableOnce(string message)
        {
            if (_gpuEngUnavailableLogged)
            {
                return;
            }
            _gpuEngUnavailableLogged = true;
            Log.Chan("MONITOR", message);
        }

        // Rebuilds the GPU Engine counter set from the live instance list
        // (instances appear/disappear as processes start and stop). Caller
        // must hold _gate (pool thread only - counter creation).
        private static void RefreshGpuEngineCounters()
        {
            Dictionary<string, PerformanceCounter> fresh = new Dictionary<string, PerformanceCounter>();
            try
            {
                PerformanceCounterCategory cat = new PerformanceCounterCategory("GPU Engine");
                string[] instances = cat.GetInstanceNames();
                for (int i = 0; i < instances.Length; i++)
                {
                    string inst = instances[i];
                    if (inst.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    if (ParsePhysId(inst) < 0)
                    {
                        continue;
                    }
                    try
                    {
                        PerformanceCounter c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                        c.NextValue();                    // prime
                        fresh[inst] = c;
                    }
                    catch
                    {
                        // instance vanished mid-enumeration - skip it
                    }
                }
                _gpuEngEverWorked = true;
            }
            catch
            {
                if (!_gpuEngEverWorked)
                {
                    _gpuEngDead = true;                  // no GPU Engine category at all
                    _hasIgpu = false;
                    LogGpuEngUnavailableOnce("monitor: igpu metrics unavailable (GPU Engine category)");
                }
                // else: an enumeration hiccup - keep the previous counters
                return;
            }

            if (_gpuEngCounters != null)
            {
                foreach (KeyValuePair<string, PerformanceCounter> kv in _gpuEngCounters)
                {
                    if (!fresh.ContainsKey(kv.Key))
                    {
                        try { kv.Value.Dispose(); } catch { }
                    }
                }
            }
            _gpuEngCounters = fresh;
        }

        // "pid_..._phys_3_eng_..." -> 3; -1 when there is no phys segment.
        private static int ParsePhysId(string instance)
        {
            int i = instance.IndexOf("phys_", StringComparison.Ordinal);
            if (i < 0)
            {
                return -1;
            }
            i += 5;
            int n = 0;
            bool any = false;
            while (i < instance.Length && instance[i] >= '0' && instance[i] <= '9')
            {
                n = n * 10 + (instance[i] - '0');
                i++;
                any = true;
                if (n > 999)
                {
                    return -1;                           // runaway digits - not a phys id
                }
            }
            return any ? n : -1;
        }

        // Picks the phys whose 3D utilization sum tracks the nvidia-smi dGPU
        // number best across the rolling history. Guarded three ways: the dGPU
        // must have been actually busy at least once (>= 10%), the best mean
        // abs diff must be within tolerance, and it must beat the runner-up
        // by a margin - otherwise the data is not distinguishing anything
        // yet and the decision waits. Cached for the session once proven.
        private static void TryCorrelateDgpuPhys()
        {
            float maxSmi = 0f;
            for (int i = 0; i < _smiHist.Count; i++)
            {
                if (_smiHist[i] > maxSmi) maxSmi = _smiHist[i];
            }
            if (maxSmi < 10f)
            {
                return;                                   // idle dGPU cannot be told from an idle iGPU
            }

            int bestPhys = -1, nextPhys = -1;
            float bestDiff = float.MaxValue, nextDiff = float.MaxValue;
            for (int bit = 0; bit < 31; bit++)
            {
                if ((_physSeenMask & (1 << bit)) == 0)
                {
                    continue;
                }
                int samples = 0;
                float diff = 0f;
                for (int i = 0; i < _smiHist.Count; i++)
                {
                    float v;
                    if (!_physHist[i].TryGetValue(bit, out v))
                    {
                        continue;
                    }
                    diff += Math.Abs(Math.Min(v, 100f) - Math.Min(_smiHist[i], 100f));
                    samples++;
                }
                if (samples < 2)
                {
                    continue;
                }
                diff /= samples;
                if (diff < bestDiff)
                {
                    nextDiff = bestDiff;
                    nextPhys = bestPhys;
                    bestDiff = diff;
                    bestPhys = bit;
                }
                else if (diff < nextDiff)
                {
                    nextDiff = diff;
                    nextPhys = bit;
                }
            }

            if (bestPhys < 0 || bestDiff > CorrelateTolerancePct)
            {
                return;                                   // not provable (yet)
            }
            if (nextPhys >= 0 && bestDiff + 3f > nextDiff)
            {
                return;                                   // ambiguous - wait for distinguishable data
            }

            _dgpuPhys = bestPhys;                         // cached for the session
            _smiHist.Clear();
            _physHist.Clear();
            Log.Chan("MONITOR", "monitor: gpu engine phys mapped (dgpu = phys_" + bestPhys.ToString(CultureInfo.InvariantCulture) + ")");
        }

        // CPU temperature via WMI root\WMI MSAcpi_ThermalZoneTemperature
        // (CurrentTemperature is tenths of Kelvin). v1.2.3 reads every zone:
        // the hottest feeds the legacy CpuTempC field and the mean of all
        // plausible zones feeds CpuTempAvgC (the panel shows the average -
        // one honest "CPU temp" concept per field). Only queried while the
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
                List<float> zones = ReadThermalZonesWmi();
                _lastCpuTemp = HottestZone(zones);
                _lastCpuTempAvg = AverageZones(zones);
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

        // All plausible ACPI zone temperatures in Celsius, in enumeration
        // order. Zones outside -50..150 C are treated as garbage (some boards
        // report raw ambient placeholders). Throws when the class is
        // unavailable or no zone is plausible.
        private static List<float> ReadThermalZonesWmi()
        {
            if (_cpuTempSearcher == null)
            {
                _cpuTempSearcher = new ManagementObjectSearcher(
                    "root\\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            }

            List<float> zones = new List<float>();
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
                        if (celsius > -50f && celsius < 150f)
                        {
                            zones.Add(celsius);
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

            if (zones.Count == 0)
            {
                throw new InvalidOperationException("no usable MSAcpi_ThermalZoneTemperature reading");
            }
            return zones;
        }

        // Mean of the plausible readings (-50..150 C); float.NaN when none.
        // Public for the offline tests (probe harness).
        public static float AverageZones(List<float> zoneCelsius)
        {
            float sum = 0f;
            int n = 0;
            if (zoneCelsius != null)
            {
                for (int i = 0; i < zoneCelsius.Count; i++)
                {
                    float c = zoneCelsius[i];
                    if (c > -50f && c < 150f)
                    {
                        sum += c;
                        n++;
                    }
                }
            }
            return n > 0 ? sum / n : float.NaN;
        }

        // Hottest plausible reading; float.NaN when none. Public for the
        // offline tests (probe harness).
        public static float HottestZone(List<float> zoneCelsius)
        {
            float best = float.MinValue;
            if (zoneCelsius != null)
            {
                for (int i = 0; i < zoneCelsius.Count; i++)
                {
                    float c = zoneCelsius[i];
                    if (c > best && c > -50f && c < 150f)
                    {
                        best = c;
                    }
                }
            }
            return best == float.MinValue ? float.NaN : best;
        }

        private static float ClampPct(float v)
        {
            if (v < 0f) return 0f;
            if (v > 100f) return 100f;
            return v;
        }
    }

    // ---------------------------------------------------------------------
    // Embeddable monitor panel (v1.2.3 rework): a TableLayoutPanel grid of
    // labeled bars built dynamically from the engine's disk list and the
    // disk-view selector -
    //   CPU | CPU temp (avg) | CPU hotspot | RAM | Disk (C:) | Disk (D:) |
    //   Disks (combined) | iGPU usage | iGPU temp | dGPU usage | dGPU temp |
    //   footer
    // CPU hotspot and iGPU temp have no driverless Windows API: those rows
    // render dimmed with the value "n/a" and never a bar - honest
    // placeholders, never faked readings. Colors come from the Ui.Mon*
    // palette; ApplyTheme() re-reads it (MainForm.ApplyTheme calls it).
    // Deterministic TableLayoutPanel layout (LogForm pattern) that survives
    // any DPI and resize. AttachToEngine/DetachFromEngine manage the
    // SampleReady subscription; Dispose detaches on its own.
    // ---------------------------------------------------------------------
    public class MonitorPanel : UserControl
    {
        // One metric row. Bar is null on the dim "n/a" placeholder rows.
        private class MonRow
        {
            public string Key;             // "cpu","cputemp","cpuhot","ram","diskall","igpu","igputemp","dgpu","dgputemp" or "disk:C:"
            public Label Name;
            public ProgressBar Bar;
            public Label Value;
            public bool Dim;
        }

        private readonly TableLayoutPanel _grid = new TableLayoutPanel();
        private readonly List<MonRow> _rows = new List<MonRow>();
        private readonly Label _footer = new Label();
        private readonly Label _footnote = new Label();
        private bool _attached;
        private string _diskView = MonitorEngine.DefaultDiskView;
        private string[] _builtForDisks = new string[0];   // letters the current rows were built for

        public MonitorPanel()
        {
            DoubleBuffered = true;
            BackColor = Ui.MonPanelBack;
            Size = new Size(430, 340);
            MinimumSize = new Size(360, 200);

            _grid.Dock = DockStyle.Fill;
            _grid.ColumnCount = 3;
            _grid.Padding = new Padding(10, 8, 10, 8);
            _grid.BackColor = Ui.MonPanelBack;
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));          // metric name
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));      // bar
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));          // value

            _footer.Text = "updated --:--:--";
            _footer.AutoSize = true;
            _footer.TextAlign = ContentAlignment.MiddleLeft;
            _footer.Margin = new Padding(0, 10, 0, 0);

            // honest-capability footnote for the dim placeholder rows
            _footnote.Text = "hotspot + iGPU temp need a kernel sensor driver \u2014 not readable on this Windows build";
            _footnote.AutoSize = true;
            _footnote.MaximumSize = new Size(650, 0);     // wraps instead of distorting the grid
            _footnote.Font = new Font("Segoe UI", 8f);
            _footnote.TextAlign = ContentAlignment.MiddleLeft;
            _footnote.Margin = new Padding(0, 6, 0, 0);

            Controls.Add(_grid);
            RebuildRows(DiscoverDisks(), true);
            ApplyThemeColors();
        }

        // ---- row construction ----------------------------------------------

        // Which fixed disks to lay rows out for: the last real sample when
        // the engine already ran, else the engine's discovered set (empty
        // before the counters are built - the first sample rebuilds).
        private static string[] DiscoverDisks()
        {
            MonitorSample last = MonitorEngine.LastSample;
            if (last != null && last.DiskNames != null && last.DiskNames.Length > 0)
            {
                return last.DiskNames;
            }
            return MonitorEngine.FixedDisks;
        }

        private static bool SameDisks(string[] a, string[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }

        // (Re)builds the whole row set: at construction, whenever the engine
        // reports a different disk set, and when the disk view changes.
        private void RebuildRows(string[] disks, bool force)
        {
            if (disks == null)
            {
                disks = new string[0];
            }
            if (!force && SameDisks(disks, _builtForDisks))
            {
                return;
            }
            _builtForDisks = disks;

            _grid.SuspendLayout();
            _grid.Controls.Clear();
            _rows.Clear();
            _grid.RowStyles.Clear();

            AddRow("cpu", "CPU", false);
            AddRow("cputemp", "CPU temp (avg)", false);
            AddRow("cpuhot", "CPU hotspot", true);
            AddRow("ram", "RAM", false);

            if (_diskView == "combined")
            {
                AddRow("diskall", "Disks (combined)", false);
            }
            else
            {
                for (int i = 0; i < disks.Length; i++)
                {
                    if (_diskView == "C" && disks[i] != "C:") continue;
                    if (_diskView == "D" && disks[i] != "D:") continue;
                    AddRow("disk:" + disks[i], "Disk (" + disks[i] + ")", false);
                }
                AddRow("diskall", "Disks (combined)", false);
            }

            AddRow("igpu", "iGPU usage", false);
            AddRow("igputemp", "iGPU temp", true);
            AddRow("dgpu", "dGPU usage", false);
            AddRow("dgputemp", "dGPU temp", false);

            int footerRow = _rows.Count;
            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _grid.Controls.Add(_footer, 0, footerRow);
            _grid.SetColumnSpan(_footer, 3);

            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _grid.Controls.Add(_footnote, 0, footerRow + 1);
            _grid.SetColumnSpan(_footnote, 3);

            _grid.RowCount = footerRow + 2;
            _grid.ResumeLayout(true);
            Height = PreferredHeight;
            ApplyThemeColors();
        }

        private void AddRow(string key, string title, bool dim)
        {
            MonRow r = new MonRow();
            r.Key = key;
            r.Dim = dim;

            Label name = new Label();
            name.Text = title;
            name.AutoSize = true;
            name.TextAlign = ContentAlignment.MiddleLeft;
            name.Margin = new Padding(0, 5, 8, 0);
            r.Name = name;

            if (!dim)
            {
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
                bar.HandleCreated += delegate { StyleBarHandle(bar); };
                if (bar.IsHandleCreated)
                {
                    StyleBarHandle(bar);
                }
                r.Bar = bar;
            }

            Label val = new Label();
            val.Text = dim ? "n/a" : "--";
            val.AutoSize = true;
            val.TextAlign = ContentAlignment.MiddleLeft;
            val.Margin = new Padding(0, 5, 0, 0);
            r.Value = val;

            int row = _rows.Count;
            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _grid.Controls.Add(name, 0, row);
            if (r.Bar != null)
            {
                _grid.Controls.Add(r.Bar, 1, row);
            }
            _grid.Controls.Add(val, 2, row);
            _rows.Add(r);
        }

        // ---- public API ------------------------------------------------------

        // Disk-view selector value ("C" / "D" / "both" / "combined"; anything
        // else falls back to "both"). Rebuilds the rows.
        public void SetDiskView(string view)
        {
            if (view != "C" && view != "D" && view != "combined")
            {
                view = "both";
            }
            if (view == _diskView)
            {
                return;
            }
            _diskView = view;
            RebuildRows(DiscoverDisks(), true);
        }

        // Height that fits the current rows exactly - the monitor page uses
        // it to place the controls under the panel.
        public int PreferredHeight
        {
            get { return 30 + _rows.Count * 25 + 34; }
        }

        // Re-reads the Ui.Mon* palette (called at construction and by
        // MainForm.ApplyTheme after a live theme change).
        public void ApplyTheme()
        {
            ApplyThemeColors();
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
                string[] disks = s.DiskNames;
                if (disks == null)
                {
                    disks = new string[0];
                }
                RebuildRows(disks, false);                // the row set follows the machine's fixed disks

                double ramPct = s.RamTotalBytes > 0
                    ? s.RamUsedBytes * 100.0 / s.RamTotalBytes
                    : 0.0;

                for (int i = 0; i < _rows.Count; i++)
                {
                    MonRow r = _rows[i];
                    if (r.Dim)
                    {
                        continue;                         // honest "n/a" placeholders - nothing to update
                    }
                    if (r.Key == "cpu")
                    {
                        SetRow(r, s.CpuPercent, FmtPercent(s.CpuPercent));
                    }
                    else if (r.Key == "cputemp")
                    {
                        if (s.HasCpuTempAvg)
                        {
                            SetRow(r, s.CpuTempAvgC, FmtTemp(s.CpuTempAvgC));
                        }
                        else if (s.HasCpuTemp)
                        {
                            SetRow(r, s.CpuTempC, FmtTemp(s.CpuTempC));   // single-zone machine: avg == hottest
                        }
                        else
                        {
                            SetNa(r);
                        }
                    }
                    else if (r.Key == "ram")
                    {
                        SetRow(r, (float)ramPct, s.RamText);
                    }
                    else if (r.Key == "diskall")
                    {
                        SetRow(r, s.DiskActivePercent, FmtPercent(s.DiskActivePercent));
                    }
                    else if (r.Key.Length > 5 && r.Key.StartsWith("disk:", StringComparison.Ordinal))
                    {
                        string letter = r.Key.Substring(5);
                        int idx = -1;
                        if (s.DiskNames != null)
                        {
                            for (int d = 0; d < s.DiskNames.Length; d++)
                            {
                                if (s.DiskNames[d] == letter) { idx = d; break; }
                            }
                        }
                        if (idx >= 0 && s.DiskPct != null && idx < s.DiskPct.Length)
                        {
                            SetRow(r, s.DiskPct[idx], FmtPercent(s.DiskPct[idx]));
                        }
                        else
                        {
                            SetNa(r);                     // drive vanished - no fake number
                        }
                    }
                    else if (r.Key == "igpu")
                    {
                        if (s.HasIgpu) SetRow(r, s.IgpuPercent, FmtPercent(s.IgpuPercent));
                        else SetNa(r);
                    }
                    else if (r.Key == "dgpu")
                    {
                        if (s.HasDgpu) SetRow(r, s.DgpuPercent, FmtPercent(s.DgpuPercent));
                        else SetNa(r);
                    }
                    else if (r.Key == "dgputemp")
                    {
                        if (s.HasDgpuTemp) SetRow(r, s.DgpuTempC, FmtTemp(s.DgpuTempC));   // 0-100 C on the bar scale
                        else SetNa(r);
                    }
                }

                _footer.Text = "updated " + s.Timestamp.ToString("HH:mm:ss");
            }
            catch
            {
                // Cosmetic UI path only - a bad sample must never break the app.
            }
        }

        private void SetRow(MonRow r, float barValue, string text)
        {
            if (r.Bar != null)
            {
                SetBar(r.Bar, barValue);
            }
            r.Value.Text = text;
        }

        private void SetNa(MonRow r)
        {
            if (r.Bar != null)
            {
                SetBar(r.Bar, 0f);
            }
            r.Value.Text = "N/A";
        }

        private static void SetBar(ProgressBar bar, float value)
        {
            int v = (int)Math.Round(value);
            if (v < 0) v = 0;
            if (v > 100) v = 100;                         // ProgressBar.Value throws outside 0-100
            bar.Value = v;
        }

        private static string FmtPercent(float value)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} %", value);
        }

        private static string FmtTemp(float celsius)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0} \u00B0C", celsius);
        }

        // ---- theming ---------------------------------------------------------

        private void ApplyThemeColors()
        {
            Color back = Ui.MonPanelBack;
            Color dim = Ui.Shift(Ui.MonTextMuted, -0.35f);
            BackColor = back;
            _grid.BackColor = back;
            for (int i = 0; i < _rows.Count; i++)
            {
                MonRow r = _rows[i];
                r.Name.BackColor = back;
                r.Name.ForeColor = r.Dim ? dim : Ui.MonTextMuted;
                r.Value.BackColor = back;
                r.Value.ForeColor = r.Dim ? dim : Ui.MonTextMain;
                if (r.Bar != null)
                {
                    r.Bar.ForeColor = Ui.MonBarFill;
                    r.Bar.BackColor = Ui.MonBarTrack;
                }
            }
            _footer.BackColor = back;
            _footer.ForeColor = dim;
            _footnote.BackColor = back;
            _footnote.ForeColor = dim;
            Invalidate(true);
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
