//  EnergySaver.cs  (v1.0.22)
//  -------------------------
//  Windows Energy Saver + Power Mode overlay control: the legacy power API
//  threshold, the EnergySaverState registry intent and the Settings
//  UI-Automation toggle. Per-class comment below.
//
//  Split out of GpuModeSwitch.cs (Wave 2, zero behavior change).

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using Microsoft.Win32;

namespace GpuModeSwitch
{
    // ---------------------------------------------------------------------
    // Windows 11 Energy Saver control. There is no documented API for the
    // instant "Turn on now" toggle, so the apps use the documented hidden
    // power setting instead: the Energy Saver battery threshold
    // (SUB_ENERGYSAVER / ESBATTTHRESHOLD). Eco sets it to 100% (Energy Saver
    // always engages when on battery), Go Time sets it to 0 (never
    // auto-engages). The value is written to the active scheme with
    // PowerWriteDCValueIndex and applied immediately with
    // PowerSetActiveScheme. The EnergySaverState registry value is also
    // written as the persisted intent. Everything is logged with raw return
    // codes; a failure never blocks the GPU switch.
    // ---------------------------------------------------------------------
    internal static class EnergySaver
    {
        private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\Power";
        private const string ValueName = "EnergySaverState";

        private static readonly Guid SubEnergySaver = new Guid("DE830923-A562-41AF-A086-E3A2C6BAD2DA"); // SUB_ENERGYSAVER
        private static readonly Guid EsBattThreshold = new Guid("E69653CA-CF6F-4166-B25A-4D6A2C1B4E7F"); // ESBATTTHRESHOLD

        // Windows 11 Power Mode overlay (the Settings "Power mode" slider).
        // Index 0 = Battery saver / best efficiency ... 3 = Best performance.
        private static readonly Guid SubPowerModeOverlay = new Guid("C763B4EC-0E50-4B6B-9BED-2B92A6EE884E");
        private static readonly Guid SettingOverlay = new Guid("7EC1751B-60ED-4588-AFB5-9819D3D77D90");

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, ref uint AcValueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint AcValueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, ref uint DcValueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint DcValueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);

        // true = on, false = off, null = unknown / not present (persisted value)
        public static bool? GetSavedState()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(KeyPath))
                {
                    if (k == null)
                    {
                        Logger.Line("EnergySaver: read failed - Control\\Power key missing");
                        return null;
                    }
                    object v = k.GetValue(ValueName);
                    if (v == null)
                    {
                        Logger.Line("EnergySaver: read failed - value not present yet");
                        return null;
                    }
                    int i = Convert.ToInt32(v);
                    if (i == 1) { Logger.Line("EnergySaver: saved state=1 (on)"); return true; }
                    if (i == 2) { Logger.Line("EnergySaver: saved state=2 (off)"); return false; }
                    Logger.Line("EnergySaver: saved state=" + i + " (unrecognized)");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Logger.Line("EnergySaver: read failed - " + ex.Message);
                return null;
            }
        }

        // Persisted intent: honored at boot even if the live toggle fails.
        private static void WriteSavedState(bool on)
        {
            int v = on ? 1 : 2;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(KeyPath, true))
                {
                    if (k == null)
                    {
                        Logger.Line("EnergySaver: registry write failed - key missing");
                        return;
                    }
                    k.SetValue(ValueName, v, RegistryValueKind.DWord);
                    Logger.Line("EnergySaver: persisted state = " + v + " (" + (on ? "ON" : "OFF") + ")");
                }
            }
            catch (Exception ex)
            {
                Logger.Line("EnergySaver: registry write failed - " + ex.Message);
            }
        }

        // Full sync: the "Always use energy saver" switch in Settings is the
        // primary control (persists, works on 24H2+/26200+); the legacy
        // threshold is a fallback for older builds.
        public static bool Sync(bool on)
        {
            WriteSavedState(on);
            if (ToggleAlwaysUseEnergySaver(on)) return true;
            Logger.Line("EnergySaver: Settings automation failed - trying legacy threshold fallback");
            return SetAutoThreshold(on ? 100u : 0u);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        private static AutomationElement FindSettingsWindow()
        {
            AutomationElement root = AutomationElement.RootElement;
            Condition cond = new PropertyCondition(AutomationElement.ClassNameProperty, "ApplicationFrameWindow");
            AutomationElementCollection wins = root.FindAll(TreeScope.Children, cond);
            foreach (AutomationElement w in wins)
            {
                string nm = "";
                try { nm = w.Current.Name; } catch {}
                if (nm != null && nm.Contains("Settings")) return w;
            }
            return null;
        }

        private static ToggleState GetToggleState(AutomationElement el)
        {
            try
            {
                object pat;
                if (el.TryGetCurrentPattern(TogglePattern.Pattern, out pat))
                {
                    return ((TogglePattern)pat).Current.ToggleState;
                }
            }
            catch { }
            return ToggleState.Indeterminate;
        }

        private static AutomationElement FindToggleByName(AutomationElement parent, string name)
        {
            AutomationElementCollection els = parent.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, name));
            foreach (AutomationElement e in els)
            {
                try
                {
                    object pat;
                    if (e.TryGetCurrentPattern(TogglePattern.Pattern, out pat)) return e;
                }
                catch { }
            }
            return null;
        }

        private static void ClickCenter(System.Windows.Rect r)
        {
            int cx = (int)(r.X + r.Width / 2);
            int cy = (int)(r.Y + r.Height / 2);
            int sw = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
            int sh = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;
            uint ax = (uint)Math.Round(cx * 65535.0 / sw);
            uint ay = (uint)Math.Round(cy * 65535.0 / sh);
            Logger.Line("EnergySaver: clicking " + cx + "," + cy);
            mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE, ax, ay, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }

        // Presses the Energy saver card's own "Show more settings" button
        // (matched by position within the card). Returns false when the card
        // is already expanded and exposes no such button.
        private static bool ClickEnergySaverExpand(AutomationElement settings)
        {
            AutomationElementCollection all = settings.FindAll(TreeScope.Descendants, Condition.TrueCondition);

            AutomationElement esGroup = null;
            System.Windows.Rect gRect = System.Windows.Rect.Empty;
            foreach (AutomationElement e in all)
            {
                string n = "";
                try { n = e.Current.Name; } catch {}
                if (n != null && n.StartsWith("Energy saver"))
                {
                    string ct = "";
                    try { ct = e.Current.ControlType.ProgrammaticName; } catch {}
                    if (ct.Contains("Group"))
                    {
                        esGroup = e;
                        try { gRect = e.Current.BoundingRectangle; } catch {}
                        break;
                    }
                }
            }
            if (esGroup == null)
            {
                Logger.Line("EnergySaver: Energy saver card not found on the page");
                return false;
            }
            Logger.Line("EnergySaver: card found at " + gRect.X + "," + gRect.Y);

            foreach (AutomationElement e in all)
            {
                string n = "";
                try { n = e.Current.Name; } catch {}
                if (n != "Show more settings") continue;
                System.Windows.Rect r = System.Windows.Rect.Empty;
                try { r = e.Current.BoundingRectangle; } catch {}
                if (r.IsEmpty) continue;
                float cy = (float)(r.Y + r.Height / 2);
                if (cy >= gRect.Y - 5 && cy <= gRect.Y + gRect.Height + 5)
                {
                    ClickCenter(r);
                    return true;
                }
            }
            Logger.Line("EnergySaver: no show-more button inside the card - it appears already expanded");
            return false;
        }

        private static void CloseSettings(AutomationElement settings)
        {
            try
            {
                IntPtr hwnd = (IntPtr)settings.Current.NativeWindowHandle;
                PostMessage(hwnd, 0x10, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE
                Logger.Line("EnergySaver: Settings window closed");
            }
            catch { }
        }

        // Opens Settings > Power & battery, expands the Energy saver card and
        // toggles "Always use energy saver" to the requested state via UI
        // Automation. Works on 24H2+/26200+ where no power API exists.
        public static bool ToggleAlwaysUseEnergySaver(bool on)
        {
            Logger.Line("EnergySaver: opening Settings > Power & battery > Energy saver");
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:powersleep") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Line("EnergySaver: could not open Settings - " + ex.Message);
                return false;
            }

            AutomationElement settings = null;
            for (int i = 0; i < 15 && settings == null; i++)
            {
                Thread.Sleep(400);
                settings = FindSettingsWindow();
            }
            if (settings == null)
            {
                Logger.Line("EnergySaver: Settings window never appeared");
                return false;
            }
            Thread.Sleep(800);

            Thread.Sleep(800);

            // Search FIRST. If the toggle is already visible (the card stays
            // expanded across runs while the Settings process is alive), no
            // expansion click is made at all - clicking it blindly would
            // collapse the card and hide the toggle.
            AutomationElement toggle = null;
            for (int attempt = 0; attempt < 3 && toggle == null; attempt++)
            {
                for (int i = 0; i < 4 && toggle == null; i++)
                {
                    Thread.Sleep(350);
                    toggle = FindToggleByName(settings, "Always use energy saver");
                }
                if (toggle != null)
                {
                    if (attempt > 0) Logger.Line("EnergySaver: toggle found after expanding the card");
                    break;
                }

                // Not visible: the card is collapsed - press its own
                // "Show more settings" button to expand it.
                Logger.Line("EnergySaver: toggle not visible - expanding the Energy saver card (attempt " + (attempt + 1) + "/3)");
                if (!ClickEnergySaverExpand(settings)) break;
            }

            if (toggle == null)
            {
                Logger.Line("EnergySaver: 'Always use energy saver' toggle not found");
                CloseSettings(settings);
                return false;
            }

            ToggleState before = GetToggleState(toggle);
            Logger.Line("EnergySaver: 'Always use energy saver' is currently " + before);
            if ((before == ToggleState.On) == on)
            {
                Logger.Line("EnergySaver: already " + (on ? "ON" : "OFF") + " (verified)");
                CloseSettings(settings);
                return true;
            }

            try
            {
                object pat;
                if (toggle.TryGetCurrentPattern(TogglePattern.Pattern, out pat))
                {
                    ((TogglePattern)pat).Toggle();
                }
                else
                {
                    Logger.Line("EnergySaver: toggle pattern unavailable");
                    CloseSettings(settings);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Line("EnergySaver: toggle failed - " + ex.Message);
                CloseSettings(settings);
                return false;
            }

            Thread.Sleep(700);
            ToggleState after = GetToggleState(toggle);
            bool ok = (after == ToggleState.On) == on;
            Logger.Line("EnergySaver: toggle now " + after + (ok ? " (verified)" : " (MISMATCH)"));
            CloseSettings(settings);
            return ok;
        }

        private static bool SetAutoThreshold(uint percent)
        {
            try
            {
                IntPtr p;
                uint rc = PowerGetActiveScheme(IntPtr.Zero, out p);
                if (rc != 0)
                {
                    Logger.Line("EnergySaver: PowerGetActiveScheme rc=" + rc);
                    return false;
                }
                Guid scheme = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
                Marshal.FreeCoTaskMem(p);
                Logger.Line("EnergySaver: active scheme " + scheme.ToString("B") +
                            " - setting battery threshold to " + percent + "%");

                Guid sub = SubEnergySaver;
                Guid set = EsBattThreshold;

                rc = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, percent);
                Logger.Line("EnergySaver: PowerWriteDCValueIndex rc=" + rc + " (0 = OK)");
                if (rc == 2)
                {
                    Logger.Line("EnergySaver: rc=2 (ERROR_FILE_NOT_FOUND) - this Windows build does not " +
                                "expose the Energy Saver threshold via the legacy power API " +
                                "(ES moved to the whesvc service on 24H2+/26200+).");
                }
                if (rc != 0) return false;

                uint readBack = 0xFFFFFFFF;
                rc = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, ref readBack);
                Logger.Line("EnergySaver: PowerReadDCValueIndex rc=" + rc + " value=" + readBack +
                            (rc == 0 && readBack == percent ? " (verified)" : " (MISMATCH)"));
                if (rc != 0 || readBack != percent) return false;

                rc = PowerSetActiveScheme(IntPtr.Zero, ref scheme);   // apply now
                Logger.Line("EnergySaver: PowerSetActiveScheme rc=" + rc + " (0 = OK)");
                return rc == 0;
            }
            catch (Exception ex)
            {
                Logger.Line("EnergySaver: threshold write failed - " + ex.Message);
                return false;
            }
        }

        // Windows 11 Power Mode overlay: 0 = Battery saver (best efficiency),
        // 1 = Better battery, 2 = Better performance, 3 = Best performance.
        // Written to both AC and battery, applied immediately. This is the
        // power-saving lever that build 26200+ still exposes.
        public static bool ApplyPowerModeOverlay(uint index)
        {
            try
            {
                IntPtr p;
                uint rc = PowerGetActiveScheme(IntPtr.Zero, out p);
                if (rc != 0)
                {
                    Logger.Line("PowerMode: PowerGetActiveScheme rc=" + rc);
                    return false;
                }
                Guid scheme = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
                Marshal.FreeCoTaskMem(p);

                Guid sub = SubPowerModeOverlay;
                Guid set = SettingOverlay;

                rc = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, index);
                Logger.Line("PowerMode: PowerWriteACValueIndex " + index + " rc=" + rc + " (0 = OK)");
                if (rc != 0) return false;

                rc = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, index);
                Logger.Line("PowerMode: PowerWriteDCValueIndex " + index + " rc=" + rc + " (0 = OK)");
                if (rc != 0) return false;

                rc = PowerSetActiveScheme(IntPtr.Zero, ref scheme);   // apply now
                Logger.Line("PowerMode: PowerSetActiveScheme rc=" + rc + " (0 = OK)");

                uint acCheck = 0xFFFFFFFF, dcCheck = 0xFFFFFFFF;
                PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, ref acCheck);
                PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, ref dcCheck);
                bool ok = acCheck == index && dcCheck == index;
                Logger.Line("PowerMode: read-back AC=" + acCheck + " DC=" + dcCheck + (ok ? " (verified)" : " (MISMATCH)"));
                return ok;
            }
            catch (Exception ex)
            {
                Logger.Line("PowerMode: overlay write failed - " + ex.Message);
                return false;
            }
        }
    }
}
