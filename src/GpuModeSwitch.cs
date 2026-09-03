//  GpuModeSwitch.cs
//  ----------------
//  One source file, two executables (selected with a /define at build time):
//    MODE_STANDARD  ->  "Go Time.exe"   : Standard GPU mode (MSHybrid, dGPU on)
//    MODE_ECO       ->  "Eco Mode.exe"  : Eco GPU mode      (dGPU powered off)
//
//  Both talk straight to the ASUS WMI/ACPI interface (namespace root\WMI,
//  class ASUS_WMI, methods DSTS = read, DEVS = write). This is the same
//  BIOS-level switch Armoury Crate drives from its "GPU Performance" page,
//  so Armoury Crate does not need to be running (or installed) for this to work.
//
//  Device IDs follow the Linux kernel asus-wmi driver and G-Helper:
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
                MessageBox.Show(AsusWmi.DescribeState(), "Eco Mode - status",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
#else
                MessageBox.Show(AsusWmi.DescribeState(), "Go Time - status",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
#endif
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
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

    internal static class AsusWmi
    {
        private const uint DGPU_ID = 0x00090020;       // dGPU power: 0 = on, 1 = off (ROG / TUF / Zephyrus / Strix)
        private const uint DGPU_ID_VIVO = 0x00090120;  // Vivobook / Zenbook Pro variant
        private const uint MUX_ID = 0x00090016;        // MUX: 0 = dGPU direct, 1 = Optimus/hybrid
        private const uint MUX_ID_VIVO = 0x00090026;   // Vivobook / Zenbook Pro variant

        private static ManagementClass _class;
        private static bool _probed;
        private static uint _dgpuId;
        private static uint _muxId;
        private static bool _muxSupported;

        public static bool Available { get { return Probe(); } }
        public static bool MuxSupported { get { Probe(); return _muxSupported; } }

        private static bool Probe()
        {
            if (_probed) return _dgpuId != 0;
            _probed = true;

            try
            {
                _class = new ManagementClass("root\\WMI", "ASUS_WMI", null);
                _class.Get();
            }
            catch
            {
                _class = null;
                return false;
            }

            // Pick the endpoint pair this firmware actually implements.
            if (ReadRaw(DGPU_ID) >= 0) _dgpuId = DGPU_ID;
            else if (ReadRaw(DGPU_ID_VIVO) >= 0) _dgpuId = DGPU_ID_VIVO;
            if (_dgpuId == 0) return false;

            if (ReadRaw(MUX_ID) >= 0) { _muxId = MUX_ID; _muxSupported = true; }
            else if (ReadRaw(MUX_ID_VIVO) >= 0) { _muxId = MUX_ID_VIVO; _muxSupported = true; }

            return true;
        }

        private static int ReadRaw(uint id)
        {
            try
            {
                ManagementBaseObject inParams = _class.GetMethodParameters("DSTS");
                AssignInParams(inParams, id, 0);
                ManagementBaseObject outParams = _class.InvokeMethod("DSTS", inParams, null);
                if (outParams == null) return -1;

                PropertyData val = FindProperty(outParams.Properties, "Return_Value");
                if (val == null) val = FindProperty(outParams.Properties, "result");
                if (val == null) val = FindProperty(outParams.Properties, "Value");
                if (val == null || val.Value == null) return -1;
                return unchecked((int)ToUint(val.Value));
            }
            catch
            {
                return -1;
            }
        }

        private static bool WriteValue(uint id, uint value)
        {
            try
            {
                ManagementBaseObject inParams = _class.GetMethodParameters("DEVS");
                AssignInParams(inParams, id, value);
                ManagementBaseObject outParams = _class.InvokeMethod("DEVS", inParams, null);
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

        public static int GetDgpuState()
        {
            if (!Available) return -1;
            int raw = ReadRaw(_dgpuId);
            return raw < 0 ? -1 : (int)((uint)raw & 0xFFFF);
        }

        public static int GetMuxState()
        {
            if (!MuxSupported) return -1;
            int raw = ReadRaw(_muxId);
            return raw < 0 ? -1 : (int)((uint)raw & 0xFFFF);
        }

        public static SwitchOutcome SwitchTo(bool eco)
        {
            SwitchOutcome o = new SwitchOutcome();

            if (!Available)
            {
                o.Headline = "ASUS hardware interface not found";
                o.Detail = "The ASUS WMI interface (root\\WMI, class ASUS_WMI) is missing.\n" +
                           "Install Armoury Crate or MyASUS first so the ASUS System Control\n" +
                           "Interface drivers are present, then run this app again.\n" +
                           "(It cannot run on desktops or non-ASUS machines.)";
                return o;
            }

            int gpuBefore = GetDgpuState();
            int muxBefore = MuxSupported ? GetMuxState() : -1;
            bool changed = false;

            // Both Eco and Standard keep the display on the hybrid (Optimus) path.
            // If the MUX is currently on dGPU-direct, move it back first.
            if (MuxSupported && muxBefore == 0)
            {
                if (!WriteValue(_muxId, 1))
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
                if (!WriteValue(_dgpuId, (uint)want))
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

        public static string DescribeState()
        {
            if (!Available)
            {
                return "ASUS WMI interface (root\\WMI / ASUS_WMI) was not found.\n\n" +
                       "This tool only works on ASUS laptops that have the ASUS System\n" +
                       "Control Interface drivers (bundled with Armoury Crate / MyASUS).\n" +
                       "It cannot run on desktops or other brands.";
            }

            int gpu = GetDgpuState();
            string s = "ASUS WMI: OK (root\\WMI / ASUS_WMI)\n";
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

        // ---- WMI plumbing helpers (parameter names vary slightly between
        // ---- firmware generations, so match by name, fall back to position)

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

        private static PropertyData FindProperty(PropertyDataCollection properties, string name)
        {
            foreach (PropertyData p in properties)
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        private static uint ToUint(object v)
        {
            return v == null ? 0 : Convert.ToUInt32(v);
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
            _detail.Size = new Size(432, 72);
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
        }

        private void OnRun()
        {
            Refresh();
            Thread.Sleep(250);   // let the "switching" state be readable; the switch itself is fast

            SwitchOutcome r;
            try
            {
                r = AsusWmi.SwitchTo(TargetEco);
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
