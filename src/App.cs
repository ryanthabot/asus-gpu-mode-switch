//  App.cs  (v1.0.22)
//  -----------------
//  Two executables, one shared codebase (selected with a /define at build time):
//    MODE_STANDARD  ->  "Go Time.exe"   : Standard GPU mode (MSHybrid, dGPU on)
//    MODE_ECO       ->  "Eco Mode.exe"  : Eco GPU mode      (dGPU powered off)
//
//  v1.0.22 changes (Go Time):
//    - Launch-time selection stage: after probing, Go Time shows both toggle
//      groups - "System optimizations" (Game Mode, do-not-disturb, Game DVR
//      recording off, network throttling off, pause background services) and
//      the "Tray apps detected" picker - and waits for the user to press GO.
//      Only the ticked items are applied; unticked ones are actively
//      restored (captures/throttling back to defaults, paused services
//      restarted). --auto skips the selection stage and applies everything.
//      Eco Mode is unchanged (one-click; --confirm gives it a confirm stage).
//    - The Go Time window grew to 560x640 to fit both groups; still fully
//      resizable with the same themed borderless look.
//
//  v1.0.21 changes:
//    - Game prep expanded (still 100% reversible via Eco Mode):
//        Go Time: Game DVR background recording off (AppCaptureEnabled /
//          GameDVR_Enabled), multimedia network throttling disabled
//          (NetworkThrottlingIndex = 0xFFFFFFFF), plus six more services
//          paused when present (WerSvc, MapsBroker, TrkWks, WMPNetworkSvc,
//          SEMgrSvc, Fax).
//        Eco Mode: all of it restored (captures on, throttling back to the
//          Windows default of 10, services restarted).
//    - Deliberately NOT touched: Xbox/Game Pass services (breaks store game
//      logins), biometrics (login), text input / audio / display / themes
//      services (breaks input, sound, visuals). The power plan itself also
//      always stays Balanced.
//
//  v1.0.20 changes:
//    - Energy Saver flow reordered to search-first: the "Always use energy
//      saver" toggle is looked for before any expansion click, so an already
//      expanded card (persisted across runs by the Settings process) is
//      never accidentally collapsed by a blind show-more click. Expansion
//      only happens when the toggle is genuinely not visible, and the
//      search is retried after each expand attempt.
//
//  v1.0.19 changes:
//    - Log window rebuilt on a TableLayoutPanel shell: the Copy log / Close
//      buttons can no longer disappear regardless of DPI or resize state.
//    - The log text is auto-selected when the window opens (HideSelection
//      off), so Ctrl+C copies straight away; Copy log remains as the
//      one-click alternative.
//
//  v1.0.18 changes (Go Time tray picker):
//    - Watchdog handling: apps like Parsec are relaunched by their own
//      Windows service the moment they are killed. The picker now discovers
//      matching services by scanning the Services registry for binaries
//      whose path contains the app's process names, stops them first, then
//      kills processes - retrying up to 3 rounds with a re-check after each.
//      A final result line reports whether the app stayed closed.
//
//  v1.0.17 changes (Go Time only):
//    - Tray app picker: after the switch completes, Go Time scans for known
//      tray applications (Parsec, Google Drive, Jellyfin, Riot Client and
//      Riot Vanguard) and shows a checkbox row for each detected one, with a
//      "Close selected" button. Closing tries a graceful window close first,
//      then kills; Vanguard's vgc service stop is also attempted. Detection
//      matches running process names (contains for long names, exact for
//      short ones like "vgc") and every action is logged.
//
//  v1.0.16 changes:
//    - Layout fixes: window widened (560x400 default) with proper inner
//      padding so no text clips at the edges; subtitle fits; detail area
//      taller. Both windows are now resizable (drag edges/corners; the
//      themed borderless main window handles edge hit-testing itself and
//      keeps its rounded corners while resizing; the log window is a
//      standard resizable window with docked layout). Controls reflow with
//      anchors while resizing.
//
//  v1.0.15 changes:
//    - Energy Saver control that actually works on build 26200+: the app
//      opens Settings > System > Power & battery > Energy saver via
//      ms-settings:powersleep, expands the Energy saver card and toggles
//      "Always use energy saver" through UI Automation (Eco = on, Go Time =
//      off), then closes Settings. This switch persists across reboots and
//      engages Energy Saver whenever the laptop runs on battery. The
//      Settings window opens briefly while the toggle runs.
//    - Game preparation: Go Time enables Windows Game Mode, turns on
//      do-not-disturb (no toast popups mid-game) and pauses background
//      services games don't need (SysMain, Windows Search, Print Spooler,
//      DiagTrack). Eco Mode restores all of it. The power plan itself is
//      never changed - it stays Balanced.
//    - Power Mode overlay (v1.0.14) and the legacy threshold attempt remain
//      as quiet secondary levers.
//
//  v1.0.7: main + log windows appear on the taskbar with the app icon.
//  v1.0.6: application icons. v1.0.5: bare-zero DSTS = device not implemented.
//  v1.0.4: live toggle always first. v1.0.3: live switching via NV driver
//  service release/restart. v1.0.2: diagnostic logging. v1.0.1: direct
//  \\.\ATKACPI transport.
//
//  Transport: direct DeviceIoControl on the ASUS ACPI device \\.\ATKACPI
//  (control code 0x0022240C, methods DSTS = read / DEVS = write), falling
//  back to WMI classes AsusAtkWmi_WMNB / ASUS_WMI (root\WMI). This is the
//  same BIOS-level switch Armoury Crate drives; Armoury Crate does not need
//  to be running.
//
//  Device IDs (Linux kernel asus-wmi driver / G-Helper):
//    0x00090020  dGPU power  (0 = enabled, 1 = disabled)      [Vivobook: 0x00090120]
//    0x00090016  GPU MUX     (0 = dGPU direct, 1 = Optimus/hybrid) [Vivobook: 0x00090026]
//
//  Targets .NET Framework 4.x (preinstalled on Windows 10/11); built with the
//  compiler that ships with Windows - see src\build.cmd.
//
//  Wave 2 split: everything above described GpuModeSwitch.cs, the single
//  v1.0.22 source file. The v1.1 decomposition spread the same code across
//  App.cs, Logger.cs, AsusControl.cs, GamePrep.cs, EnergySaver.cs, Theme.cs
//  and Forms.cs (all compiled together by build.cmd). Zero behavior change.

using System;
using System.Windows.Forms;

namespace GpuModeSwitch
{
    internal static class Program
    {
        public const string Version = "1.0.22";

        [STAThread]
        private static void Main(string[] args)
        {
#if MODE_ECO
            Logger.Init("Eco Mode", "EcoMode");
#else
            Logger.Init("Go Time", "GoTime");
#endif

            bool confirm = false;
            bool auto = false;
            bool statusOnly = false;
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "--confirm", StringComparison.OrdinalIgnoreCase)) confirm = true;
                    if (string.Equals(a, "--auto", StringComparison.OrdinalIgnoreCase)) auto = true;
                    if (string.Equals(a, "--status", StringComparison.OrdinalIgnoreCase)) statusOnly = true;
                }
            }

            if (statusOnly)
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
            Application.Run(new MainForm(confirm, auto));
        }
    }
}
