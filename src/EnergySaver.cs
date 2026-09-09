//  EnergySaver.cs  (v1.1.1)
//  -------------------------
//  Windows Energy Saver + Power Mode overlay control. v1.1.1 order (silent
//  first): the documented SUB_ENERGYSAVER battery-charge threshold via the
//  power API (works on every build tested incl. 26200 - see the note on the
//  EsBattThreshold GUID), the EnergySaverState registry intent, and only as
//  a fallback the Settings UI-Automation toggle - minimized, mouse-free and
//  restricted to a window this process opened.
//
//  Split out of GpuModeSwitch.cs (Wave 2, zero behavior change).

using System;
using System.Collections.Generic;
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
        // ESBATTTHRESHOLD, "Charge level" (0-100%). v1.1.1: the GUID used
        // through v1.1.0 had a wrong tail and returned ERROR_FILE_NOT_FOUND
        // on EVERY build - misread as "the API is gone on 24H2+". The tail
        // below matches the setting defined under
        // HKLM\...\PowerSettings\de830923-... on real installs (verified on
        // build 26200: read + write + read-back all rc=0).
        private static readonly Guid EsBattThreshold = new Guid("E69653CA-CF7F-4F05-AA73-CB833FA90AD4");

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

        // (v1.2.2) Real-click fallback for Settings controls that expose no
        // Invoke/Toggle pattern (the "Show more settings" expander on some
        // builds). The synthetic click needs the target visible, so the
        // Settings window is restored + foregrounded first (RestoreWindow).
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // true = on, false = off, null = unknown / not present (persisted value)
        public static bool? GetSavedState()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(KeyPath))
                {
                    if (k == null)
                    {
                        Log.Chan("POWER", "EnergySaver: read failed - Control\\Power key missing");
                        return null;
                    }
                    object v = k.GetValue(ValueName);
                    if (v == null)
                    {
                        Log.Chan("POWER", "EnergySaver: read failed - value not present yet");
                        return null;
                    }
                    int i = Convert.ToInt32(v);
                    if (i == 1) { Log.Chan("POWER", "EnergySaver: saved state=1 (on)"); return true; }
                    if (i == 2) { Log.Chan("POWER", "EnergySaver: saved state=2 (off)"); return false; }
                    Log.Chan("POWER", "EnergySaver: saved state=" + i + " (unrecognized)");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log.Chan("POWER", "EnergySaver: read failed - " + ex.Message);
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
                        Log.Chan("POWER", "EnergySaver: registry write failed - key missing");
                        return;
                    }
                    k.SetValue(ValueName, v, RegistryValueKind.DWord);
                    Log.Chan("POWER", "EnergySaver: persisted state = " + v + " (" + (on ? "ON" : "OFF") + ")");
                }
            }
            catch (Exception ex)
            {
                Log.Chan("POWER", "EnergySaver: registry write failed - " + ex.Message);
            }
        }

        // Full sync (v1.2.1): the Settings "Always use energy saver" switch
        // is the PRIMARY control - on 24H2+/26200 the power service (whesvc)
        // ignores the legacy charge-level threshold, so a threshold write
        // that reports rc=0/verified can silently change nothing the user
        // can see (v1.1.1 field regression: silent-first meant the working
        // Settings toggle never ran). The threshold write is still made as
        // a silent supplement for builds that honor it; success is judged
        // on the real switch when its automation runs.
        public static bool Sync(bool on)
        {
            WriteSavedState(on);
            bool thresholdOk = SetAutoThreshold(on ? 100u : 0u);
            if (ToggleAlwaysUseEnergySaver(on)) return true;
            Log.Chan("POWER", "EnergySaver: Settings automation failed - " +
                        (thresholdOk ? "charge-level threshold still applied" : "no fallback succeeded"));
            return thresholdOk;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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

        // v1.1.1 window discipline for the fallback: never adopt a Settings
        // window that existed before ours, and keep ours minimized.
        private static List<IntPtr> SnapshotSettingsWindows()
        {
            List<IntPtr> hwnds = new List<IntPtr>();
            AutomationElementCollection wins = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "ApplicationFrameWindow"));
            foreach (AutomationElement w in wins)
            {
                string nm = "";
                try { nm = w.Current.Name; } catch {}
                if (nm == null || !nm.Contains("Settings")) continue;
                try { hwnds.Add((IntPtr)w.Current.NativeWindowHandle); } catch {}
            }
            return hwnds;
        }

        private static AutomationElement FindNewSettingsWindow(List<IntPtr> before)
        {
            AutomationElementCollection wins = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "ApplicationFrameWindow"));
            foreach (AutomationElement w in wins)
            {
                string nm = "";
                try { nm = w.Current.Name; } catch {}
                if (nm == null || !nm.Contains("Settings")) continue;
                IntPtr h = IntPtr.Zero;
                try { h = (IntPtr)w.Current.NativeWindowHandle; } catch {}
                if (before.Contains(h)) continue;   // the user's own window - never touch it
                return w;
            }
            return null;
        }

        private static void MinimizeWindow(AutomationElement el)
        {
            try
            {
                IntPtr hwnd = (IntPtr)el.Current.NativeWindowHandle;
                ShowWindow(hwnd, 6);   // SW_MINIMIZE - may flash in the taskbar, never covers the screen
                Log.Chan("POWER", "EnergySaver: our Settings window minimized");
            }
            catch { }
        }

        // SW_RESTORE: a minimized WinUI window virtualizes its content away
        // from the UIA tree, so the window is restored before any search.
        // Also foregrounded: the real-click fallback (ClickPoint) needs the
        // window unobscured at its UIA-reported screen position.
        private static void RestoreWindow(AutomationElement el)
        {
            try
            {
                IntPtr hwnd = (IntPtr)el.Current.NativeWindowHandle;
                ShowWindow(hwnd, 9);   // SW_RESTORE
                SetForegroundWindow(hwnd);
                Log.Chan("POWER", "EnergySaver: our Settings window restored for automation");
            }
            catch { }
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
                Log.Chan("POWER", "EnergySaver: Energy saver card not found on the page");
                return false;
            }
            Log.Chan("POWER", "EnergySaver: card found at " + gRect.X + "," + gRect.Y);

            // (v1.2.2) The page may open scrolled (Settings remembers it), and
            // an off-screen toggle is virtualized out of the UIA tree while a
            // stale rect makes the click land on nothing. Scroll the card to
            // the top of the viewport, then re-snapshot so every rect below
            // is fresh.
            ScrollIntoView(esGroup);
            try { gRect = esGroup.Current.BoundingRectangle; } catch {}

            AutomationElementCollection fresh = settings.FindAll(TreeScope.Descendants, Condition.TrueCondition);

            // After scrolling, an already-expanded card may expose the toggle
            // without any click - pressing "Show more settings" now would
            // COLLAPSE it. Check first.
            foreach (AutomationElement e in fresh)
            {
                string tn = "";
                try { tn = e.Current.Name; } catch {}
                if (tn != "Always use energy saver") continue;
                object tp;
                if (!e.TryGetCurrentPattern(TogglePattern.Pattern, out tp)) continue;
                System.Windows.Rect tr = System.Windows.Rect.Empty;
                try { tr = e.Current.BoundingRectangle; } catch {}
                if (tr.IsEmpty) continue;
                float tcy = (float)(tr.Y + tr.Height / 2);
                if (tcy >= gRect.Y - 5 && tcy <= gRect.Y + gRect.Height + 5)
                {
                    Log.Chan("POWER", "EnergySaver: card already expanded (toggle visible after scroll) - no click made");
                    return true;
                }
            }

            foreach (AutomationElement e in fresh)
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
                    object pat;
                    if (e.TryGetCurrentPattern(InvokePattern.Pattern, out pat))
                    {
                        ((InvokePattern)pat).Invoke();
                        return true;
                    }
                    // (v1.2.2) No Invoke pattern on this build - click the
                    // button's center for real instead of giving up. Safe
                    // here: the Settings window is our own, restored and
                    // foregrounded, and the rect is in screen coordinates.
                    Log.Chan("POWER", "EnergySaver: expand button has no Invoke pattern - clicking its center instead");
                    ClickPoint((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
                    return true;
                }
            }
            Log.Chan("POWER", "EnergySaver: no show-more button inside the card - it appears already expanded");
            return false;
        }

        // One left click at a screen point (SetCursorPos + mouse_event).
        private static void ClickPoint(int x, int y)
        {
            try
            {
                SetCursorPos(x, y);
                Thread.Sleep(150);                      // let hover/animation settle
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);   // LEFTDOWN
                Thread.Sleep(40);
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);   // LEFTUP
            }
            catch { }
        }

        // Scrolls an element to the top of the Settings page viewport so the
        // elements under it (the expander's hidden rows) enter the UIA tree
        // and report real screen rects. No-op when unsupported.
        private static void ScrollIntoView(AutomationElement el)
        {
            try
            {
                object p;
                if (el.TryGetCurrentPattern(ScrollItemPattern.Pattern, out p))
                {
                    ((ScrollItemPattern)p).ScrollIntoView();
                    Thread.Sleep(400);              // let the scroll + revirtualization settle
                }
            }
            catch { }
        }

        private static void CloseSettings(AutomationElement settings)
        {
            try
            {
                IntPtr hwnd = (IntPtr)settings.Current.NativeWindowHandle;
                PostMessage(hwnd, 0x10, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE
                Log.Chan("POWER", "EnergySaver: Settings window closed");
            }
            catch { }
        }

        // Fallback only (v1.1.1): opens Settings > Power & battery, expands
        // the Energy saver card and toggles "Always use energy saver" via UI
        // Automation. v1.2.2: the window must be RESTORED, not minimized -
        // a minimized WinUI window renders nothing, its content is
        // virtualized out of the UIA tree and the Energy saver card is
        // "not found on the page" (v1.1.1/v1.2.1 field regression; the
        // verified-working v1.0.15-1.0.20 flow showed the window briefly).
        // Still mouse-free (Invoke/Toggle patterns only) and only a window
        // this process launched is used - a pre-existing Settings window is
        // never adopted or closed.
        public static bool ToggleAlwaysUseEnergySaver(bool on)
        {
            List<IntPtr> preExisting = SnapshotSettingsWindows();
            Log.Chan("POWER", "EnergySaver: opening Settings > Power & battery > Energy saver");
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:powersleep")
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                });
            }
            catch (Exception ex)
            {
                Log.Chan("POWER", "EnergySaver: could not open Settings - " + ex.Message);
                return false;
            }

            AutomationElement settings = null;
            for (int i = 0; i < 15 && settings == null; i++)
            {
                Thread.Sleep(400);
                settings = FindNewSettingsWindow(preExisting);
                if (settings != null) RestoreWindow(settings);   // content must render for UIA
            }
            if (settings == null)
            {
                Log.Chan("POWER", "EnergySaver: no new Settings window appeared (existing ones left untouched)");
                return false;
            }
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
                    if (attempt > 0) Log.Chan("POWER", "EnergySaver: toggle found after expanding the card");
                    break;
                }

                // Not visible: the card is collapsed - press its own
                // "Show more settings" button to expand it.
                Log.Chan("POWER", "EnergySaver: toggle not visible - expanding the Energy saver card (attempt " + (attempt + 1) + "/3)");
                if (!ClickEnergySaverExpand(settings)) break;
            }

            if (toggle == null)
            {
                Log.Chan("POWER", "EnergySaver: 'Always use energy saver' toggle not found");
                CloseSettings(settings);
                return false;
            }

            ToggleState before = GetToggleState(toggle);
            Log.Chan("POWER", "EnergySaver: 'Always use energy saver' is currently " + before);
            if ((before == ToggleState.On) == on)
            {
                Log.Chan("POWER", "EnergySaver: already " + (on ? "ON" : "OFF") + " (verified)");
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
                    // (v1.2.2) Pattern vanished between find and flip (rare,
                    // but seen on WinUI virtualization) - click the switch
                    // for real instead of failing the whole apply.
                    System.Windows.Rect tr = toggle.Current.BoundingRectangle;
                    if (tr.IsEmpty)
                    {
                        Log.Chan("POWER", "EnergySaver: toggle pattern unavailable and rect empty");
                        CloseSettings(settings);
                        return false;
                    }
                    Log.Chan("POWER", "EnergySaver: toggle pattern unavailable - clicking its center instead");
                    ClickPoint((int)(tr.X + tr.Width / 2), (int)(tr.Y + tr.Height / 2));
                }
            }
            catch (Exception ex)
            {
                Log.Chan("POWER", "EnergySaver: toggle failed - " + ex.Message);
                CloseSettings(settings);
                return false;
            }

            Thread.Sleep(700);
            ToggleState after = GetToggleState(toggle);
            bool ok = (after == ToggleState.On) == on;
            Log.Chan("POWER", "EnergySaver: toggle now " + after + (ok ? " (verified)" : " (MISMATCH)"));
            CloseSettings(settings);
            return ok;
        }

        // ESBATTTHRESHOLD ("Charge level", 0-100%): the documented
        // SUB_ENERGYSAVER setting that governs when Energy Saver engages.
        // Eco writes 100 (always engage - the same "always on" the Settings
        // toggle expresses); Go Time writes 0 (never auto-engage while
        // gaming - stronger than the Settings toggle-off, which only returns
        // to the charge-level default; Eco puts it back). Written to both AC
        // and DC, applied immediately, verified by read-back.
        private static bool SetAutoThreshold(uint percent)
        {
            try
            {
                IntPtr p;
                uint rc = PowerGetActiveScheme(IntPtr.Zero, out p);
                if (rc != 0)
                {
                    Log.Chan("POWER", "EnergySaver: PowerGetActiveScheme rc=" + rc);
                    return false;
                }
                Guid scheme = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
                Marshal.FreeCoTaskMem(p);
                Log.Chan("POWER", "EnergySaver: active scheme " + scheme.ToString("B") +
                            " - setting energy saver charge level to " + percent + "%");

                Guid sub = SubEnergySaver;
                Guid set = EsBattThreshold;

                rc = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, percent);
                Log.Chan("POWER", "EnergySaver: PowerWriteACValueIndex rc=" + rc + " (0 = OK)");
                if (rc != 0) Log.Chan("POWER", "EnergySaver: AC index refused - continuing with the battery (DC) index");

                rc = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, percent);
                Log.Chan("POWER", "EnergySaver: PowerWriteDCValueIndex rc=" + rc + " (0 = OK)");
                if (rc != 0) return false;

                uint acRead = 0xFFFFFFFF, dcRead = 0xFFFFFFFF;
                PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, ref acRead);
                PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, ref dcRead);
                Log.Chan("POWER", "EnergySaver: read-back AC=" + acRead + " DC=" + dcRead +
                            (dcRead == percent ? " (verified)" : " (MISMATCH)"));
                if (dcRead != percent) return false;

                rc = PowerSetActiveScheme(IntPtr.Zero, ref scheme);   // apply now
                Log.Chan("POWER", "EnergySaver: PowerSetActiveScheme rc=" + rc + " (0 = OK)");
                return rc == 0;
            }
            catch (Exception ex)
            {
                Log.Chan("POWER", "EnergySaver: threshold write failed - " + ex.Message);
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
                    Log.Chan("POWER", "PowerMode: PowerGetActiveScheme rc=" + rc);
                    return false;
                }
                Guid scheme = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
                Marshal.FreeCoTaskMem(p);

                Guid sub = SubPowerModeOverlay;
                Guid set = SettingOverlay;

                rc = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, index);
                Log.Chan("POWER", "PowerMode: PowerWriteACValueIndex " + index + " rc=" + rc + " (0 = OK)");
                if (rc != 0) return false;

                rc = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, index);
                Log.Chan("POWER", "PowerMode: PowerWriteDCValueIndex " + index + " rc=" + rc + " (0 = OK)");
                if (rc != 0) return false;

                rc = PowerSetActiveScheme(IntPtr.Zero, ref scheme);   // apply now
                Log.Chan("POWER", "PowerMode: PowerSetActiveScheme rc=" + rc + " (0 = OK)");

                uint acCheck = 0xFFFFFFFF, dcCheck = 0xFFFFFFFF;
                PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, ref acCheck);
                PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, ref dcCheck);
                bool ok = acCheck == index && dcCheck == index;
                Log.Chan("POWER", "PowerMode: read-back AC=" + acCheck + " DC=" + dcCheck + (ok ? " (verified)" : " (MISMATCH)"));
                return ok;
            }
            catch (Exception ex)
            {
                Log.Chan("POWER", "PowerMode: overlay write failed - " + ex.Message);
                return false;
            }
        }
    }
}
