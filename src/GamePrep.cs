//  GamePrep.cs  (v1.0.22)
//  ----------------------
//  Game preparation (Go Time) and restoration (Eco Mode): Windows Game
//  Mode, do-not-disturb, Game DVR recording, multimedia network throttling
//  and the paused background gamer services. Per-class comment below.
//
//  Split out of GpuModeSwitch.cs (Wave 2, zero behavior change).

using System;
using System.Collections.Generic;
using System.ServiceProcess;
using Microsoft.Win32;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // Game preparation (Go Time) and restoration (Eco Mode). Go Time enables
    // Windows Game Mode, turns on do-not-disturb and pauses background
    // services games don't need; Eco Mode restores them. All best-effort and
    // logged; failures never block the GPU switch. Stopped services also
    // come back on their own at the next reboot.
    // ---------------------------------------------------------------------
    internal static class GamePrep
    {
        private static readonly string[] GamerServices =
        {
            "SysMain", "WSearch", "Spooler", "DiagTrack",
            "WerSvc", "MapsBroker", "TrkWks", "WMPNetworkSvc", "SEMgrSvc", "Fax"
        };

        private static void SetHkcuDword(string subKey, string valueName, int value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(subKey))
                {
                    if (k == null) { Log.Info("GamePrep: HKCU write failed - " + subKey); return; }
                    k.SetValue(valueName, value, RegistryValueKind.DWord);
                    Log.Info("GamePrep: " + valueName + " = " + value);
                }
            }
            catch (Exception ex)
            {
                Log.Info("GamePrep: HKCU write failed - " + ex.Message);
            }
        }

        // v1.2.0: takes int - RegistryKey.SetValue REQUIRES System.Int32 for
        // RegistryValueKind.DWord; the uint overload threw "type of the value
        // object did not match" on every 0xFFFFFFFF write (see the 2026-09-07
        // field log), so NetworkThrottlingIndex never actually turned off.
        private static void SetHklmDword(string subKey, string valueName, int value)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(subKey, true))
                {
                    if (k == null) { Log.Info("GamePrep: HKLM write failed - " + subKey); return; }
                    k.SetValue(valueName, value, RegistryValueKind.DWord);
                    Log.Info("GamePrep: HKLM " + valueName + " = " + value);
                }
            }
            catch (Exception ex)
            {
                Log.Info("GamePrep: HKLM write failed - " + ex.Message);
            }
        }

        // Go Time: applies the ticked system optimizations. Unticked ones are
        // actively restored, so a previously paused state doesn't linger.
        public static string ApplyForGaming(bool gameMode, bool dnd, bool dvr, bool throttle, bool pauseServices)
        {
            List<string> parts = new List<string>();
            if (gameMode)
            {
                SetHkcuDword(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1);
                parts.Add("Game Mode on");
            }
            if (dnd)
            {
                SetHkcuDword(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings", "NOC_GLOBAL_SETTING_TOASTS_ENABLED", 0);
                parts.Add("do-not-disturb on");
            }
            else
            {
                SetHkcuDword(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings", "NOC_GLOBAL_SETTING_TOASTS_ENABLED", 1);
                parts.Add("toasts restored");
            }
            if (dvr)
            {
                SetHkcuDword(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0);
                SetHkcuDword(@"System\GameConfigStore", "GameDVR_Enabled", 0);
                parts.Add("game captures off");
            }
            else
            {
                SetHkcuDword(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 1);
                SetHkcuDword(@"System\GameConfigStore", "GameDVR_Enabled", 1);
                parts.Add("game captures on");
            }
            if (throttle)
            {
                SetHklmDword(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", -1);   // 0xFFFFFFFF = no throttling
                parts.Add("network throttling off");
            }
            else
            {
                SetHklmDword(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", 10);
                parts.Add("network throttling default");
            }
            if (pauseServices)
            {
                int paused = PauseGamerServices();
                parts.Add(paused + "/" + GamerServices.Length + " background services paused");
            }
            else
            {
                int running = RestartGamerServices();
                parts.Add(running + "/" + GamerServices.Length + " background services restored");
            }
            return string.Join(", ", parts.ToArray());
        }

        private static int PauseGamerServices()
        {
            int paused = 0;
            foreach (string svc in GamerServices)
            {
                try
                {
                    using (ServiceController sc = new ServiceController(svc))
                    {
                        if (sc.Status == ServiceControllerStatus.Running ||
                            sc.Status == ServiceControllerStatus.StartPending)
                        {
                            sc.Stop();
                            try
                            {
                                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                                paused++;
                                Log.Info("GamePrep: paused " + svc);
                            }
                            catch (System.ServiceProcess.TimeoutException)
                            {
                                Log.Info("GamePrep: " + svc + " stop timed out - continuing");
                            }
                        }
                        else
                        {
                            paused++;
                            Log.Info("GamePrep: " + svc + " already " + sc.Status);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ex.Message != null && ex.Message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0) Log.Info("GamePrep: " + svc + " not installed - skipped");
                    else Log.Info("GamePrep: " + svc + " stop failed - " + ex.Message);
                }
            }
            return paused;
        }

        private static int RestartGamerServices()
        {
            int running = 0;
            foreach (string svc in GamerServices)
            {
                try
                {
                    using (ServiceController sc = new ServiceController(svc))
                    {
                        if (sc.Status == ServiceControllerStatus.Stopped ||
                            sc.Status == ServiceControllerStatus.StopPending)
                        {
                            sc.Start();
                            try
                            {
                                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                                running++;
                                Log.Info("GamePrep: restarted " + svc);
                            }
                            catch (System.ServiceProcess.TimeoutException)
                            {
                                Log.Info("GamePrep: " + svc + " start timed out - continuing");
                            }
                        }
                        else
                        {
                            running++;
                            Log.Info("GamePrep: " + svc + " already " + sc.Status);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ex.Message != null && ex.Message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0) Log.Info("GamePrep: " + svc + " not installed - skipped");
                    else Log.Info("GamePrep: " + svc + " start failed - " + ex.Message);
                }
            }
            return running;
        }

        // Eco Mode: bring everything back.
        public static string ApplyForEco()
        {
            SetHkcuDword(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings", "NOC_GLOBAL_SETTING_TOASTS_ENABLED", 1);
            SetHkcuDword(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 1);
            SetHkcuDword(@"System\GameConfigStore", "GameDVR_Enabled", 1);
            SetHklmDword(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", 10);   // Windows default

            int running = 0;
            foreach (string svc in GamerServices)
            {
                try
                {
                    using (ServiceController sc = new ServiceController(svc))
                    {
                        if (sc.Status == ServiceControllerStatus.Stopped ||
                            sc.Status == ServiceControllerStatus.StopPending)
                        {
                            sc.Start();
                            try
                            {
                                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                                running++;
                                Log.Info("GamePrep: restarted " + svc);
                            }
                            catch (System.ServiceProcess.TimeoutException)
                            {
                                Log.Info("GamePrep: " + svc + " start timed out - continuing");
                            }
                        }
                        else
                        {
                            running++;
                            Log.Info("GamePrep: " + svc + " already " + sc.Status);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ex.Message != null && ex.Message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0) Log.Info("GamePrep: " + svc + " not installed - skipped");
                    else Log.Info("GamePrep: " + svc + " start failed - " + ex.Message);
                }
            }
            return "Toasts restored, game captures on, network throttling default, " +
                   running + "/" + GamerServices.Length + " background services running.";
        }
    }
}
