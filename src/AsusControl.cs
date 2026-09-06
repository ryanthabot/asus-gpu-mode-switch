//  AsusControl.cs  (v1.0.22)
//  -------------------------
//  The hardware path that switches the GPU mode: the transport abstraction
//  (AsusTransport), the direct \\.\ATKACPI DeviceIoControl transport
//  (AtkAcpiTransport), the WMI root\WMI fallback transports (WmiTransport),
//  the switch result DTO (SwitchOutcome), the high-level switch orchestrator
//  with read-back verification and MUX fallback (AsusControl), and the
//  NVIDIA Display Container service control (GpuServices). Per-class
//  comments below carry the details.
//
//  Split out of GpuModeSwitch.cs (Wave 2, zero behavior change).

using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // Transport abstraction: a channel that can read/write ASUS ACPI device
    // values. Two kinds exist: direct kernel I/O on \\.\ATKACPI, and WMI.
    // ---------------------------------------------------------------------
    internal abstract class AsusTransport
    {
        public abstract string Name { get; }
        public abstract bool Open();
        public abstract void Close();
        public abstract int ReadRaw(uint deviceId);              // -1 = failed
        public abstract bool Write(uint deviceId, uint value);   // true = firmware OK
    }

    // Primary: direct DeviceIoControl on \\.\ATKACPI (works on G513QR and all
    // recent ROG/TUF/Strix/Zephyrus firmware with the ASUS Optimization driver).
    internal class AtkAcpiTransport : AsusTransport
    {
        private const string DEVICE_NAME = "\\\\.\\ATKACPI";
        private const uint IOCTL_CONTROL = 0x0022240C;
        private const uint METHOD_DSTS = 0x53545344;   // read
        private const uint METHOD_DEVS = 0x53564544;   // write

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint FILE_SHARE_READ = 1;
        private const uint FILE_SHARE_WRITE = 2;

        private IntPtr _handle = IntPtr.Zero;

        public override string Name { get { return "direct ACPI device (\\\\.\\ATKACPI)"; } }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
            byte[] lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize,
            ref uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        public override bool Open()
        {
            if (_handle != IntPtr.Zero) return true;
            IntPtr h = CreateFile(DEVICE_NAME,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            int err = Marshal.GetLastWin32Error();
            if (h == new IntPtr(-1) || h == IntPtr.Zero)
            {
                Logger.Line("ATKACPI: open failed (Win32 error " + err + ")");
                return false;
            }
            _handle = h;
            Logger.Line("ATKACPI: device opened");
            return true;
        }

        public override void Close()
        {
            if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
        }

        // Buffer layout: [methodId u32][args byte-length u32][args]
        // args for DSTS: [deviceId u32][0 u32];  for DEVS: [deviceId u32][value u32]
        // Output: 16 bytes; DSTS -> result int at offset 0 (includes 0x10000 status
        // bit), DEVS -> 1 on success.
        // Returns the raw output int, or -1 if the ioctl itself failed.
        private int CallMethod(string label, uint methodId, uint deviceId, uint value)
        {
            byte[] args = new byte[8];
            BitConverter.GetBytes(deviceId).CopyTo(args, 0);
            BitConverter.GetBytes(value).CopyTo(args, 4);

            byte[] inBuf = new byte[8 + args.Length];
            byte[] outBuf = new byte[16];
            BitConverter.GetBytes(methodId).CopyTo(inBuf, 0);
            BitConverter.GetBytes((uint)args.Length).CopyTo(inBuf, 4);
            Array.Copy(args, 0, inBuf, 8, args.Length);

            uint returned = 0;
            bool ok = DeviceIoControl(_handle, IOCTL_CONTROL, inBuf, (uint)inBuf.Length,
                outBuf, (uint)outBuf.Length, ref returned, IntPtr.Zero);
            int err = Marshal.GetLastWin32Error();

            int outVal = -1;
            if (ok) outVal = BitConverter.ToInt32(outBuf, 0);

            // GetLastWin32Error is only meaningful when the call failed.
            Logger.Line(string.Format("  ATKACPI {0} dev=0x{1:X8} val={2} -> ok={3} raw=0x{4:X8}{5}",
                label, deviceId, value, ok, (long)outVal, ok ? "" : " (win32err=" + err + ")"));
            return outVal;
        }

        public override int ReadRaw(uint deviceId)
        {
            if (_handle == IntPtr.Zero) return -1;
            return CallMethod("DSTS", METHOD_DSTS, deviceId, 0);
        }

        public override bool Write(uint deviceId, uint value)
        {
            if (_handle == IntPtr.Zero) return false;
            return CallMethod("DEVS", METHOD_DEVS, deviceId, value) == 1;
        }
    }

    // Fallback: WMI classes in root\WMI. Different firmware generations expose
    // different classes (AsusAtkWmi_WMNB on ATK-era firmware, ASUS_WMI on others).
    internal class WmiTransport : AsusTransport
    {
        private readonly string _className;
        private ManagementObject _object;

        public WmiTransport(string className) { _className = className; }

        public override string Name { get { return "WMI root\\WMI class " + _className; } }

        public override bool Open()
        {
            if (_object != null) return true;
            try
            {
                ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "root\\WMI", "SELECT * FROM " + _className);
                ManagementObjectCollection results = searcher.Get();
                foreach (ManagementObject o in results) { _object = o; break; }
                results.Dispose();
                searcher.Dispose();
                Logger.Line("WMI " + _className + ": " + (_object != null ? "class found" : "class not present"));
                return _object != null;
            }
            catch (Exception ex)
            {
                Logger.Line("WMI " + _className + ": query failed - " + ex.Message);
                return false;
            }
        }

        public override void Close() { _object = null; }

        private static PropertyData FindProperty(PropertyDataCollection properties, string name)
        {
            foreach (PropertyData p in properties)
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        private static void AssignInParams(ManagementBaseObject inParams, uint id, uint value)
        {
            PropertyData pId = FindProperty(inParams.Properties, "Device_ID");
            PropertyData pVal = FindProperty(inParams.Properties, "Control_Status");
            if (pVal == null) pVal = FindProperty(inParams.Properties, "Value");

            if (pId != null && pVal != null)
            {
                pId.Value = id;
                pVal.Value = value;
                return;
            }

            int i = 0;
            foreach (PropertyData p in inParams.Properties)
            {
                p.Value = i == 0 ? (object)id : (object)value;
                i++;
                if (i >= 2) break;
            }
        }

        private static uint ToUint(object v)
        {
            return v == null ? 0 : Convert.ToUInt32(v);
        }

        public override int ReadRaw(uint deviceId)
        {
            try
            {
                ManagementBaseObject inParams = _object.GetMethodParameters("DSTS");
                AssignInParams(inParams, deviceId, 0);
                ManagementBaseObject outParams = _object.InvokeMethod("DSTS", inParams, null);
                if (outParams == null) return -1;

                PropertyData val = FindProperty(outParams.Properties, "Return_Value");
                if (val == null) val = FindProperty(outParams.Properties, "result");
                if (val == null)
                {
                    foreach (PropertyData p in outParams.Properties)
                    {
                        if (!string.Equals(p.Name, "ReturnValue", StringComparison.OrdinalIgnoreCase))
                        {
                            val = p;
                            break;
                        }
                    }
                }
                if (val == null || val.Value == null) return -1;
                int raw = unchecked((int)ToUint(val.Value));
                Logger.Line(string.Format("  WMI {0} DSTS dev=0x{1:X8} -> raw=0x{2:X8}", _className, deviceId, (long)raw));
                return raw;
            }
            catch (Exception ex)
            {
                Logger.Line("  WMI " + _className + " DSTS dev=0x" + deviceId.ToString("X8") + " failed - " + ex.Message);
                return -1;
            }
        }

        public override bool Write(uint deviceId, uint value)
        {
            try
            {
                ManagementBaseObject inParams = _object.GetMethodParameters("DEVS");
                AssignInParams(inParams, deviceId, value);
                ManagementBaseObject outParams = _object.InvokeMethod("DEVS", inParams, null);
                if (outParams == null)
                {
                    Logger.Line(string.Format("  WMI {0} DEVS dev=0x{1:X8} val={2} -> no output", _className, deviceId, value));
                    return true;
                }

                PropertyData rc = FindProperty(outParams.Properties, "result");
                if (rc == null) rc = FindProperty(outParams.Properties, "ReturnValue");
                if (rc == null || rc.Value == null)
                {
                    Logger.Line(string.Format("  WMI {0} DEVS dev=0x{1:X8} val={2} -> no result code (treated as OK)", _className, deviceId, value));
                    return true;
                }
                uint code = ToUint(rc.Value);
                Logger.Line(string.Format("  WMI {0} DEVS dev=0x{1:X8} val={2} -> result={3}", _className, deviceId, value, code));
                return code == 0 || code == 1;   // firmware reports 1 on success
            }
            catch (Exception ex)
            {
                Logger.Line("  WMI " + _className + " DEVS dev=0x" + deviceId.ToString("X8") + " failed - " + ex.Message);
                return false;
            }
        }
    }

    internal class SwitchOutcome
    {
        public bool Ok;
        public bool Changed;
        public bool NeedsRestart;
        public bool RunAgainAfterRestart;
        public string Headline = "";
        public string Detail = "";
    }

    // ---------------------------------------------------------------------
    // High-level control: picks a working transport + device IDs, then
    // switches the GPU mode with logging and read-back verification.
    // ---------------------------------------------------------------------
    internal static class AsusControl
    {
        private const uint DGPU_ID = 0x00090020;       // dGPU power: 0 = on, 1 = off (ROG / TUF / Zephyrus / Strix)
        private const uint DGPU_ID_VIVO = 0x00090120;  // Vivobook / Zenbook Pro variant
        private const uint MUX_ID = 0x00090016;        // MUX: 0 = dGPU direct, 1 = Optimus/hybrid
        private const uint MUX_ID_VIVO = 0x00090026;   // Vivobook / Zenbook Pro variant

        private static AsusTransport _transport;
        private static bool _probed;
        private static uint _dgpuId;
        private static uint _muxId;
        private static bool _muxSupported;
        private static string _lastError = "Unknown error.";

        public static bool Available { get { return Probe(); } }
        public static bool MuxSupported { get { Probe(); return _muxSupported; } }
        public static string LastError { get { return _lastError; } }

        // DSTS responses always carry upper status/presence bits on supported
        // devices (e.g. 0x00010000). A response of bare 0 with NO upper bits
        // means the firmware does not implement the device ID at all - it must
        // not be read as "value = 0" (a supported device at 0 answers
        // 0x00010000). This mirrors G-Helper, where such a result comes back
        // negative and the endpoint is treated as unsupported.
        private static int NormalizeState(int raw, string what)
        {
            if (raw < 0)
            {
                Logger.Line("  normalize " + what + ": raw<0 -> unsupported/failed");
                return -1;
            }
            if ((raw & 0xFFFF0000) == 0)
            {
                Logger.Line("  normalize " + what + ": raw=0x" + raw.ToString("X8") +
                            " -> no status bits, device not implemented");
                return -1;
            }
            int v = raw - 0x10000;
            if (v == 0 || v == 1)
            {
                Logger.Line("  normalize " + what + ": raw=0x" + raw.ToString("X8") + " -> " + v);
                return v;
            }
            v = raw & 0xFFFF;
            if (v == 0 || v == 1)
            {
                Logger.Line("  normalize " + what + ": raw=0x" + raw.ToString("X8") + " -> " + v);
                return v;
            }
            Logger.Line("  normalize " + what + ": raw=0x" + raw.ToString("X8") + " -> UNRECOGNIZED");
            return -1;
        }

        private static bool Probe()
        {
            if (_probed) return _transport != null;
            _probed = true;

            AsusTransport[] candidates = new AsusTransport[]
            {
                new AtkAcpiTransport(),
                new WmiTransport("AsusAtkWmi_WMNB"),
                new WmiTransport("ASUS_WMI"),
            };
            uint[] dgpuIds = new uint[] { DGPU_ID, DGPU_ID_VIVO };
            uint[] muxIds = new uint[] { MUX_ID, MUX_ID_VIVO };

            foreach (AsusTransport t in candidates)
            {
                Logger.Line("Probing transport: " + t.Name);
                if (!t.Open()) continue;

                for (int i = 0; i < dgpuIds.Length; i++)
                {
                    int dgpu = NormalizeState(t.ReadRaw(dgpuIds[i]), "dGPU probe 0x" + dgpuIds[i].ToString("X8"));
                    if (dgpu < 0) continue;

                    // dGPU endpoint answers - this transport and ID pair are it.
                    _transport = t;
                    _dgpuId = dgpuIds[i];
                    _muxId = muxIds[i];
                    _muxSupported = NormalizeState(t.ReadRaw(_muxId), "MUX probe 0x" + _muxId.ToString("X8")) >= 0;
                    Logger.Line("Selected: " + t.Name + "  dGPU=0x" + _dgpuId.ToString("X8") +
                                "  MUX=0x" + _muxId.ToString("X8") + ( _muxSupported ? " (supported)" : " (not present)"));
                    return true;
                }
                t.Close();
            }

            _lastError =
                "No ASUS control interface answered on this PC.\n\n" +
                "Tried: the ACPI device \\\\.\\ATKACPI and the WMI classes\n" +
                "AsusAtkWmi_WMNB / ASUS_WMI (root\\WMI).\n\n" +
                "These are provided by the \"ASUS System Control Interface\"\n" +
                "driver that comes with Armoury Crate / MyASUS. Install or\n" +
                "repair Armoury Crate, reboot, and run this app again.\n\n" +
                "Full details: View log.";
            Logger.Line("PROBE FAILED: no transport answered.");
            return false;
        }

        public static int GetDgpuState()
        {
            if (!Available) return -1;
            return NormalizeState(_transport.ReadRaw(_dgpuId), "dGPU read");
        }

        public static int GetMuxState()
        {
            if (!MuxSupported) return -1;
            return NormalizeState(_transport.ReadRaw(_muxId), "MUX read");
        }

        // Read-only probe used by the confirm phase: reports the current state
        // without changing anything.
        public static string Precheck(bool eco, out bool ok)
        {
            if (!Available)
            {
                ok = false;
                return _lastError;
            }
            ok = true;

            int gpu = GetDgpuState();
            string s = "Current state: dGPU " +
                (gpu == 0 ? "on" : gpu == 1 ? "off (eco)" : "unknown");
            if (MuxSupported)
            {
                int mux = GetMuxState();
                s += ", MUX " + (mux == 1 ? "hybrid" : mux == 0 ? "dGPU-direct" : "unknown");
            }
            s += ".\nTarget: " + (eco ? "Eco Mode (dGPU off)" : "Standard mode (dGPU on)") + ".";
            bool? es = EnergySaver.GetSavedState();
            s += "\nWindows Energy Saver (saved state): " + (es == null ? "unknown" : (es == true ? "on" : "off")) + ".";
            if (gpu == (eco ? 1 : 0))
            {
                s += "\nAlready in the target mode - Apply will simply confirm it.";
            }
            Logger.Line("Precheck done: " + s.Replace("\n", " | "));
            return s;
        }

        public static SwitchOutcome SwitchTo(bool eco)
        {
            SwitchOutcome o = new SwitchOutcome();
            Logger.Line("=== Switch requested: " + (eco ? "ECO (dGPU off)" : "STANDARD (dGPU on)") + " ===");

            if (!Available)
            {
                o.Headline = "ASUS hardware interface not found";
                o.Detail = _lastError;
                return o;
            }

            int gpuBefore = GetDgpuState();
            int muxBefore = MuxSupported ? GetMuxState() : -1;
            Logger.Line(string.Format("State before switch: dGPU={0} (0=on 1=off) MUX={1} (0=dGPU-direct 1=hybrid)", gpuBefore, muxBefore));

            // Live toggle first - this is what Armoury Crate does: just flip
            // the dGPU power flag and let the driver follow. The MUX/display
            // path is only touched as a fallback, if the firmware refuses the
            // write while the display actually runs on the dGPU.
            int want = eco ? 1 : 0;
            if (gpuBefore == want)
            {
                o.Ok = true;
                o.Changed = false;
                o.Headline = eco ? "Already in Eco Mode" : "Already in Standard mode";
                // Still sync Power Mode + Energy Saver with the mode.
                bool pmOk = EnergySaver.ApplyPowerModeOverlay(eco ? 0u : 3u);
                bool esApplied = EnergySaver.Sync(eco);
                o.Detail = "Nothing needed changing.\nCurrent state: dGPU " + (eco ? "off" : "on") +
                       ", hybrid display path.\n" +
                       (pmOk
                           ? "Power Mode: " + (eco ? "Battery saver (best efficiency)." : "Best performance.") + "\n"
                           : "") +
                       (esApplied
                           ? "Windows Energy Saver: " + (eco ? "always on (on battery)." : "off.")
                           : "Note: Windows Energy Saver could not be changed - see View log.");
                if (eco) o.Detail += "\n" + GamePrep.ApplyForEco();
                Logger.Line("No change needed - already in target mode.");
                return o;
            }

            if (eco)
            {
                // Release the NVIDIA driver first so the firmware can
                // actually cut power when the flag is written (G-Helper
                // order: stop service, then write the eco flag).
                Logger.Line("Releasing NVIDIA driver service before the eco write...");
                GpuServices.StopAll();
            }

            Logger.Line("Writing dGPU power flag: " + want);
            bool written = _transport.Write(_dgpuId, (uint)want);

            if (!written && eco)
            {
                // The firmware can refuse while the GPU is busy; the
                // service is released now, so retry once.
                Logger.Line("dGPU write refused - retrying once after NV service release");
                GpuServices.StopAll();
                written = _transport.Write(_dgpuId, (uint)want);
                if (written) Logger.Line("dGPU write accepted on retry.");
            }

            if (!written)
            {
                Logger.Line("dGPU write refused by firmware.");

                // One case genuinely needs a restart: the display path itself
                // runs through the dGPU (Ultimate / hard MUX mode). Move the
                // MUX to hybrid (lands at restart); the flag applies next run.
                if (MuxSupported && muxBefore == 0)
                {
                    bool muxWrite = _transport.Write(_muxId, 1);
                    Logger.Line("MUX -> hybrid write result: " + muxWrite + " (applies at restart)");

                    o.Ok = true;
                    o.Changed = true;
                    o.NeedsRestart = true;
                    o.RunAgainAfterRestart = true;
                    o.Headline = "One-time restart needed (display path is on the dGPU)";
                    o.Detail = "The dGPU power flag can't be applied while the display\n" +
                               "path runs through the dGPU. The MUX has been switched\n" +
                               "back to hybrid - that takes effect at the next restart.\n\n" +
                               "Restart now, then run " + (eco ? "Eco Mode" : "Go Time") + " once more.\n" +
                               "After that one-time restart, switching is instant.";
                    return o;
                }

                o.Headline = "The dGPU power change was refused";
                o.Detail = "Something is still using the dGPU. Close games and 3D apps,\nthen run this again.\n\nFull details: View log.";
                return o;
            }

            int gpuAfter = GetDgpuState();
            Logger.Line("State after switch: dGPU=" + gpuAfter + " (want " + want + ")");
            if (gpuAfter != want)
            {
                o.Headline = "The change did not stick";
                o.Detail = eco
                    ? "The dGPU is still powered. Close games/apps that use it and try again.\n(A connected XG Mobile can also block Eco mode.)\n\nFull details: View log."
                    : "The dGPU could not be enabled. Restart the laptop and try again.\n\nFull details: View log.";
                return o;
            }

            if (!eco)
            {
                // Give the bus a moment to re-enumerate the powered-on GPU,
                // then restart the NVIDIA driver service so it comes back
                // usable immediately - no reboot needed.
                Logger.Line("Waiting 3s for the dGPU to re-enumerate...");
                Thread.Sleep(3000);
                GpuServices.RestartAll();
            }

            // The flag is written and verified - applied live, no restart needed
            // (a restart only finalizes if something is holding the GPU).
            o.Ok = true;
            o.Changed = true;
            o.NeedsRestart = false;
            o.Headline = eco ? "Eco Mode applied" : "Standard mode applied";
            o.Detail = eco
                ? "dGPU power is now off (battery friendly, quieter). The NVIDIA\n" +
                  "driver service was released so this took effect immediately.\n\n" +
                  "If the dGPU still shows as active, a game was holding it -\n" +
                  "close it and run this again. A restart also finalizes."
                : "dGPU power is now on, hybrid (MSHybrid) display path. The NVIDIA\n" +
                  "driver service was restarted so the GPU comes back right away.\n\n" +
                  "Give it a few seconds if it is still re-enumerating; a restart\n" +
                  "finalizes if anything looks off.";

            // Sync the Power Mode overlay (works on all builds) and the Energy
            // Saver threshold (older builds) with the mode.
            Logger.Line("Power Mode overlay: setting " + (eco ? "0 (Battery saver / best efficiency)" : "3 (Best performance)"));
            bool pmSynced = EnergySaver.ApplyPowerModeOverlay(eco ? 0u : 3u);
            bool esSynced = EnergySaver.Sync(eco);

            o.Detail += pmSynced
                ? "\nPower Mode: " + (eco ? "Battery saver (best efficiency)." : "Best performance.")
                : "\nNote: Power Mode could not be changed - see View log.";
            o.Detail += esSynced
                ? "\nWindows Energy Saver: " + (eco ? "always on (on battery)." : "off.")
                : "";
            if (eco) o.Detail += "\n" + GamePrep.ApplyForEco();
            Logger.Line("Switch complete (applied live, no restart required).");
            return o;
        }

        public static void Shutdown()
        {
            if (_transport != null) _transport.Close();
        }

        public static string DescribeState()
        {
            if (!Available) return _lastError + "\n\n(Log: " + Logger.FilePath + ")";

            int gpu = GetDgpuState();
            string s = "Connected via: " + _transport.Name + "\n";
            s += "dGPU control endpoint: 0x" + _dgpuId.ToString("X8") + "\n";
            s += "dGPU power: " + StateText(gpu, "enabled", "disabled (eco)") + "\n";
            if (MuxSupported)
            {
                int mux = GetMuxState();
                s += "GPU MUX endpoint: 0x" + _muxId.ToString("X8") + "\n";
                s += "GPU MUX: " + StateText(mux, "Optimus / hybrid (standard)", "dGPU direct (ultimate)") + "\n";
            }
            else
            {
                s += "GPU MUX: not available on this model\n";
            }
            bool? es = EnergySaver.GetSavedState();
            s += "Windows Energy Saver (saved state): " + (es == null ? "unknown" : (es == true ? "on" : "off")) + "\n";

#if MODE_ECO
            s += "\nThis app switches to: ECO (dGPU off)";
#else
            s += "\nThis app switches to: STANDARD (dGPU on, hybrid)";
#endif
            return s;
        }

        private static string StateText(int state, string zeroText, string oneText)
        {
            if (state == 0) return zeroText;
            if (state == 1) return oneText;
            return "unknown (" + state + ")";
        }
    }

    // ---------------------------------------------------------------------
    // NVIDIA Display Container service control. Releasing this service lets
    // the firmware actually cut dGPU power when switching to Eco (the driver
    // otherwise holds the device), and restarting it after switching to
    // Standard makes the dGPU come back without a reboot. Same as G-Helper.
    // All of this is best-effort and logged; failures never abort the switch.
    // ---------------------------------------------------------------------
    internal static class GpuServices
    {
        private static string[] FindNvServices()
        {
            List<string> found = new List<string>();
            try
            {
                ServiceController[] all = ServiceController.GetServices();
                foreach (ServiceController s in all)
                {
                    if (s.ServiceName != null &&
                        s.ServiceName.StartsWith("NVDisplay.Container", StringComparison.OrdinalIgnoreCase))
                        found.Add(s.ServiceName);
                    s.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.Line("NV service lookup failed: " + ex.Message);
            }
            if (found.Count == 0)
                Logger.Line("No NVIDIA Display Container services found (AMD-only system or driver absent).");
            return found.ToArray();
        }

        // Used before switching to Eco: release the driver so power can be cut.
        public static void StopAll()
        {
            string[] names = FindNvServices();
            foreach (string n in names)
            {
                try
                {
                    using (ServiceController sc = new ServiceController(n))
                    {
                    if (sc.Status == ServiceControllerStatus.Running ||
                        sc.Status == ServiceControllerStatus.StartPending)
                    {
                        sc.Stop();
                        try
                        {
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                            Logger.Line("NV service stopped: " + n);
                        }
                        catch (System.ServiceProcess.TimeoutException)
                        {
                            Logger.Line("NV service stop timed out after 15s (" + n + ") - continuing, non-fatal");
                        }
                    }
                        else
                        {
                            Logger.Line("NV service already " + sc.Status + ": " + n);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Line("NV service stop failed (" + n + "): " + ex.Message);
                }
            }
        }

        // Used after switching to Standard: (re)start the driver service so the
        // dGPU is usable immediately.
        public static void RestartAll()
        {
            string[] names = FindNvServices();
            foreach (string n in names)
            {
                try
                {
                    using (ServiceController sc = new ServiceController(n))
                    {
                        if (sc.Status == ServiceControllerStatus.Running ||
                            sc.Status == ServiceControllerStatus.StartPending)
                        {
                            sc.Stop();
                            try
                            {
                                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                                Logger.Line("NV service stopped for restart: " + n);
                            }
                            catch (System.ServiceProcess.TimeoutException)
                            {
                                Logger.Line("NV service stop timed out (" + n + ") - attempting start anyway");
                            }
                        }
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                        Logger.Line("NV service running: " + n);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Line("NV service restart failed (" + n + "): " + ex.Message);
                }
            }
        }
    }
}
