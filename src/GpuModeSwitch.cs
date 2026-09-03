//  GpuModeSwitch.cs  (v1.0.1)
//  --------------------------
//  One source file, two executables (selected with a /define at build time):
//    MODE_STANDARD  ->  "Go Time.exe"   : Standard GPU mode (MSHybrid, dGPU on)
//    MODE_ECO       ->  "Eco Mode.exe"  : Eco GPU mode      (dGPU powered off)
//
//  v1.0.1: talks to the ASUS ACPI device \\.\ATKACPI directly via
//  DeviceIoControl (control code 0x0022240C, methods DSTS = read /
//  DEVS = write) - this is what recent ASUS firmware generations such as the
//  ROG Strix G15 (G513QR) actually respond to. Falls back to the WMI classes
//  AsusAtkWmi_WMNB and ASUS_WMI (root\WMI) on older/other firmware.
//  This is the same BIOS-level switch Armoury Crate drives from its
//  "GPU Performance" page; Armoury Crate does not need to be running.
//
//  Device IDs (Linux kernel asus-wmi driver / G-Helper):
//    0x00090020  dGPU power  (0 = enabled, 1 = disabled)      [Vivobook: 0x00090120]
//    0x00090016  GPU MUX     (0 = dGPU direct, 1 = Optimus/hybrid) [Vivobook: 0x00090026]
//
//  Targets .NET Framework 4.x (preinstalled on Windows 10/11); built with the
//  compiler that ships with Windows - see src\build.cmd. Run with --status to
//  inspect the current state without switching anything.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace GpuModeSwitch
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--status", StringComparison.OrdinalIgnoreCase))
            {
#if MODE_ECO
                MessageBox.Show(AsusControl.DescribeState(), "Eco Mode - status",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
#else
                MessageBox.Show(AsusControl.DescribeState(), "Go Time - status",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
#endif
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

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
            if (h == new IntPtr(-1) || h == IntPtr.Zero) return false;
            _handle = h;
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
        private byte[] CallMethod(uint methodId, byte[] methodArgs)
        {
            byte[] inBuf = new byte[8 + methodArgs.Length];
            byte[] outBuf = new byte[16];
            BitConverter.GetBytes(methodId).CopyTo(inBuf, 0);
            BitConverter.GetBytes((uint)methodArgs.Length).CopyTo(inBuf, 4);
            Array.Copy(methodArgs, 0, inBuf, 8, methodArgs.Length);

            uint returned = 0;
            if (!DeviceIoControl(_handle, IOCTL_CONTROL, inBuf, (uint)inBuf.Length,
                outBuf, (uint)outBuf.Length, ref returned, IntPtr.Zero))
                return null;
            return outBuf;
        }

        public override int ReadRaw(uint deviceId)
        {
            if (_handle == IntPtr.Zero) return -1;
            byte[] args = new byte[8];
            BitConverter.GetBytes(deviceId).CopyTo(args, 0);
            byte[] outBuf = CallMethod(METHOD_DSTS, args);
            if (outBuf == null || outBuf.Length < 4) return -1;
            return BitConverter.ToInt32(outBuf, 0);
        }

        public override bool Write(uint deviceId, uint value)
        {
            if (_handle == IntPtr.Zero) return false;
            byte[] args = new byte[8];
            BitConverter.GetBytes(deviceId).CopyTo(args, 0);
            BitConverter.GetBytes(value).CopyTo(args, 4);
            byte[] outBuf = CallMethod(METHOD_DEVS, args);
            if (outBuf == null || outBuf.Length < 4) return false;
            return BitConverter.ToInt32(outBuf, 0) == 1;
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
                return _object != null;
            }
            catch
            {
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
                return unchecked((int)ToUint(val.Value));
            }
            catch
            {
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
                if (outParams == null) return true;

                PropertyData rc = FindProperty(outParams.Properties, "result");
                if (rc == null) rc = FindProperty(outParams.Properties, "ReturnValue");
                if (rc == null || rc.Value == null) return true;
                uint code = ToUint(rc.Value);
                return code == 0 || code == 1;   // firmware reports 1 on success
            }
            catch
            {
                return false;
            }
        }
    }

    internal class SwitchOutcome
    {
        public bool Ok;
        public bool Changed;
        public bool NeedsRestart;
        public string Headline = "";
        public string Detail = "";
    }

    // ---------------------------------------------------------------------
    // High-level control: picks a working transport + device IDs, then
    // switches the GPU mode with read-back verification.
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

        // DSTS results carry a 0x10000 status bit; some models return the plain
        // value instead. Accept either representation.
        private static int NormalizeState(int raw)
        {
            if (raw < 0) return -1;
            int v = raw - 0x10000;
            if (v == 0 || v == 1) return v;
            v = raw & 0xFFFF;
            if (v == 0 || v == 1) return v;
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
                if (!t.Open()) continue;

                for (int i = 0; i < dgpuIds.Length; i++)
                {
                    int dgpu = NormalizeState(t.ReadRaw(dgpuIds[i]));
                    if (dgpu < 0) continue;

                    // dGPU endpoint answers - this transport and ID pair are it.
                    _transport = t;
                    _dgpuId = dgpuIds[i];
                    _muxId = muxIds[i];
                    _muxSupported = NormalizeState(t.ReadRaw(_muxId)) >= 0;
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
                "repair Armoury Crate, reboot, and run this app again.";
            return false;
        }

        public static int GetDgpuState()
        {
            if (!Available) return -1;
            return NormalizeState(_transport.ReadRaw(_dgpuId));
        }

        public static int GetMuxState()
        {
            if (!MuxSupported) return -1;
            return NormalizeState(_transport.ReadRaw(_muxId));
        }

        public static SwitchOutcome SwitchTo(bool eco)
        {
            SwitchOutcome o = new SwitchOutcome();

            if (!Available)
            {
                o.Headline = "ASUS hardware interface not found";
                o.Detail = _lastError;
                return o;
            }

            int gpuBefore = GetDgpuState();
            int muxBefore = MuxSupported ? GetMuxState() : -1;
            bool changed = false;

            // Both Eco and Standard keep the display on the hybrid (Optimus) path.
            // If the MUX is currently on dGPU-direct, move it back first.
            if (MuxSupported && muxBefore == 0)
            {
                if (!_transport.Write(_muxId, 1))
                {
                    o.Headline = "Could not switch the MUX back to hybrid";
                    o.Detail = "The firmware refused the MUX change.\nRestart the laptop and run this again.";
                    return o;
                }
                changed = true;
            }

            int want = eco ? 1 : 0;
            if (gpuBefore != want)
            {
                if (!_transport.Write(_dgpuId, (uint)want))
                {
                    o.Headline = "The dGPU power change was refused";
                    o.Detail = eco
                        ? "Something is still using the dGPU. Close games and 3D apps,\nthen run this again."
                        : "The firmware refused the change. Restart the laptop and try again.";
                    return o;
                }
                changed = true;
            }

            int gpuAfter = GetDgpuState();
            if (gpuAfter != want)
            {
                o.Headline = "The change did not stick";
                o.Detail = eco
                    ? "The dGPU is still powered. Close games/apps that use it and try again.\n(A connected XG Mobile can also block Eco mode.)"
                    : "The dGPU could not be enabled. Restart the laptop and try again.";
                return o;
            }

            o.Ok = true;
            o.Changed = changed;

            if (!changed)
            {
                o.Headline = eco ? "Already in Eco Mode" : "Already in Standard mode";
                o.Detail = "Nothing needed changing.\nCurrent state: dGPU " + (eco ? "off" : "on") +
                           ", hybrid display path.";
                return o;
            }

            o.NeedsRestart = true;
            o.Headline = eco ? "Eco Mode set" : "Standard mode set";
            string detail = eco
                ? "dGPU powered off (battery friendly, quieter)."
                : "dGPU enabled, hybrid (MSHybrid) display path.";
            if (MuxSupported && muxBefore == 0) detail += "\nMUX moved from dGPU-direct back to hybrid.";
            detail += "\nA restart is required for it to fully apply.";
            o.Detail = detail;
            return o;
        }

        public static void Shutdown()
        {
            if (_transport != null) _transport.Close();
        }

        public static string DescribeState()
        {
            if (!Available) return _lastError;

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

    internal class MainForm : Form
    {
        private readonly Label _title = new Label();
        private readonly Label _subtitle = new Label();
        private readonly Label _status = new Label();
        private readonly Label _detail = new Label();
        private readonly Button _restart = new Button();
        private readonly Button _close = new Button();

#if MODE_ECO
        private const bool TargetEco = true;
        private const string Title = "ECO MODE";
        private const string Subtitle = "Eco GPU mode  |  dGPU powered off  |  battery friendly";
        private readonly Color _accent = Color.FromArgb(76, 195, 138);
#else
        private const bool TargetEco = false;
        private const string Title = "GO TIME";
        private const string Subtitle = "Standard GPU mode  |  dGPU on  |  hybrid display path";
        private readonly Color _accent = Color.FromArgb(255, 70, 85);
#endif

        public MainForm()
        {
            Text = TargetEco ? "Eco Mode" : "Go Time";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(480, 262);
            BackColor = Color.FromArgb(24, 24, 28);
            Font = new Font("Segoe UI", 9.5f);

            _title.Text = Title;
            _title.Font = new Font("Segoe UI", 19f, FontStyle.Bold);
            _title.ForeColor = _accent;
            _title.AutoSize = true;
            _title.Location = new Point(24, 18);
            _title.BackColor = Color.Transparent;

            _subtitle.Text = Subtitle;
            _subtitle.ForeColor = Color.FromArgb(150, 150, 158);
            _subtitle.AutoSize = true;
            _subtitle.Location = new Point(26, 64);
            _subtitle.BackColor = Color.Transparent;

            _status.Text = "Switching GPU mode...";
            _status.ForeColor = Color.FromArgb(235, 235, 240);
            _status.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
            _status.AutoSize = false;
            _status.Size = new Size(432, 28);
            _status.Location = new Point(24, 110);
            _status.BackColor = Color.Transparent;

            _detail.ForeColor = Color.FromArgb(165, 165, 172);
            _detail.AutoSize = false;
            _detail.Size = new Size(432, 86);
            _detail.Location = new Point(24, 140);
            _detail.BackColor = Color.Transparent;

            _restart.Text = "Restart now";
            _restart.FlatStyle = FlatStyle.Flat;
            _restart.FlatAppearance.BorderColor = _accent;
            _restart.FlatAppearance.BorderSize = 1;
            _restart.ForeColor = Color.White;
            _restart.BackColor = Color.FromArgb(45, 45, 52);
            _restart.Size = new Size(120, 32);
            _restart.Location = new Point(336, 216);
            _restart.Visible = false;
            _restart.Click += OnRestart;

            _close.Text = "Close";
            _close.FlatStyle = FlatStyle.Flat;
            _close.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
            _close.ForeColor = Color.FromArgb(210, 210, 216);
            _close.BackColor = Color.FromArgb(45, 45, 52);
            _close.Size = new Size(80, 32);
            _close.Location = new Point(248, 216);
            _close.Click += delegate { Close(); };

            Controls.Add(_title);
            Controls.Add(_subtitle);
            Controls.Add(_status);
            Controls.Add(_detail);
            Controls.Add(_restart);
            Controls.Add(_close);

            TryDarkTitleBar();
            Shown += delegate { OnRun(); };
            FormClosed += delegate { AsusControl.Shutdown(); };
        }

        private void OnRun()
        {
            Refresh();
            Thread.Sleep(250);   // let the "switching" state be readable; the switch itself is fast

            SwitchOutcome r;
            try
            {
                r = AsusControl.SwitchTo(TargetEco);
            }
            catch (Exception ex)
            {
                r = new SwitchOutcome();
                r.Headline = "Unexpected error";
                r.Detail = ex.Message;
            }

            _status.Text = (r.Ok ? "OK - " : "Failed - ") + r.Headline;
            _status.ForeColor = r.Ok ? _accent : Color.FromArgb(255, 120, 120);
            _detail.Text = r.Detail;
            _restart.Visible = r.Ok && r.NeedsRestart;
        }

        private void OnRestart(object sender, EventArgs e)
        {
            DialogResult dr = MessageBox.Show(
                "Restart the laptop now?\n\nMake sure your work is saved - Windows will restart in a few seconds.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (dr != DialogResult.Yes) return;

            try
            {
                Process.Start("shutdown.exe", "/r /t 5 /c \"Applying GPU mode change\"");
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not start a restart: " + ex.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private void TryDarkTitleBar()
        {
            try
            {
                int on = 1;
                DwmSetWindowAttribute(Handle, 20, ref on, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
            }
            catch
            {
                // older Windows without dark title bars - not a problem
            }
        }
    }
}
