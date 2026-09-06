# asus-gpu-mode-switch — Agent Handbook (living document)

This is the handoff document for the 18-agent v1.1.0 build. **Any agent on any
computer**: clone the repo, read this file top to bottom, then work your wave.
Keep it current — append to §4 and §6, update §5 status as features land, never
rewrite history sections.

- Repo: https://github.com/ryanthabot/asus-gpu-mode-switch
- Current stable baseline: **v1.0.22** (commit `4260aef` on `main`)
- Upgrade branch: **`v1.1-logging-cleanup`** (all v1.1 work happens here; never push)
- Baseline build evidence: `BUILD_NOTES.md` (repo root)

---

## §1 Project summary

Two one-click Windows executables — **Go Time.exe** and **Eco Mode.exe** — that
switch the GPU Mode on ASUS laptops (the same Standard/Eco switch in
*Armoury Crate → Devices → System Settings → GPU Performance*) without opening
Armoury Crate. Both exes are built from **one shared C# codebase**
(`src\*.cs`, namespace `GpuModeSwitch` — split across seven module files in
Wave 2, see §3) with the C# compiler that ships with Windows.

- **Go Time.exe** (`/define:MODE_STANDARD`): switches to Standard GPU mode —
  dGPU powered on, hybrid (MSHybrid) display path. Since v1.0.22 it also shows
  a **launch-time selection stage**: two checkbox groups ("System
  optimizations": Game Mode, do-not-disturb, Game DVR recording off, network
  throttling off, pause background services — all ticked by default; and
  "Tray apps detected": Parsec, Google Drive, Jellyfin, Riot Client, Riot
  Vanguard). Only ticked items are applied when the user presses **GO**;
  unticked items are actively restored. `--auto` skips the selection stage and
  applies everything.
- **Eco Mode.exe** (`/define:MODE_ECO`): switches to Eco GPU mode — the dGPU is
  completely powered off (battery/silence). One-click; `--confirm` gives it a
  review stage before applying.

**How the build works:** `src\build.cmd` runs
`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` twice against all of
`src\*.cs` — once with `/define:MODE_STANDARD` producing
`dist\Go Time.exe`, once with `/define:MODE_ECO` producing
`dist\Eco Mode.exe`. Each build embeds its own `.ico` as the Win32 icon and its
256px PNG as resource `GpuModeSwitch.appicon.png`, and uses
`src\app.manifest` (requireAdministrator) as the Win32 manifest. No Visual
Studio, no .NET SDK, no NuGet, no internet needed.

**How the switch works (hardware path):** both apps drive the same BIOS-level
switch Armoury Crate drives, trying transports in order: (1) direct
`DeviceIoControl` on the ACPI device `\\.\ATKACPI` (control code
`0x0022240C`, methods DSTS = read / DEVS = write) — the primary path; (2) WMI
class `AsusAtkWmi_WMNB` (`root\WMI`); (3) WMI class `ASUS_WMI` (`root\WMI`).
Device IDs (per the Linux kernel `asus-wmi` driver / G-Helper): `0x00090020`
dGPU power (0 = on, 1 = off; Vivobook variant `0x00090120`), `0x00090016` GPU
MUX (0 = dGPU-direct, 1 = hybrid; Vivobook variant `0x00090026`). The live
dGPU power toggle is always attempted first; the MUX write is only a fallback
for the one case the firmware refuses (display path physically on the dGPU =
"Ultimate"), which asks for exactly one restart.

CLI flags understood by both exes: `--confirm` (opt-in review stage),
`--auto` (Go Time only: skip selection, apply everything), `--status`
(read-only probe shown in a MessageBox).

v1.1.0 adds: a logging rewrite (per-run timestamped logs, browser, retention),
Windows Update storage cleanup integrated into the GO flow, plus 10 new
features (see §5). The suite stays **two exes**.

---

## §2 Hard constraints (do not violate)

1. **Compiler:** `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
   (banner: `Microsoft (R) Visual C# Compiler version 4.8.9221.0 for C# 5`).
   Nothing else. `build.cmd` may fall back to the `Framework` (32-bit) copy.
2. **Language level: C# 5 only.** Banned (will not compile): string
   interpolation (`$"..."`), null-conditional `?.` and `??=`, expression-bodied
   members, `nameof`, out-var declarations, auto-property initializers,
   `using static`, exception filters. Write C# 5: explicit properties
   (`get { return x; }`), `string.Format` / concatenation, anonymous-method
   delegates (`delegate { ... }`) instead of lambdas is fine either way, but
   no C# 6+ syntax anywhere.
3. **Runtime: .NET Framework 4.x only** (preinstalled on Windows 10/11).
4. **No external libraries / NuGet.** Allowed references (as used by
   `build.cmd`): `System.dll`, `System.Core`, `System.Windows.Forms.dll`,
   `System.Drawing.dll`, `System.Management.dll`, `System.ServiceProcess.dll`,
   `WindowsBase.dll` (from `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF`),
   and the GAC `UIAutomationClient.dll` + `UIAutomationTypes.dll`
   (resolved by `build.cmd` from
   `%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\...`). `System.Web.Extensions`
   is also an allowed ref if a future module needs it. External P/Invoke into
   OS DLLs (kernel32, powrprof, dwmapi) is the established pattern and fine.
5. **Elevation:** `src\app.manifest` sets `requestedExecutionLevel
   level="requireAdministrator"` — the UAC prompt is expected and required
   (the ASUS WMI/DEVS write needs it). Keep the manifest on both targets.
   Consequence for agents: you cannot silently run the exes from a script.
6. **Two `/define` targets:** `MODE_STANDARD` → `dist\Go Time.exe`,
   `MODE_ECO` → `dist\Eco Mode.exe`. Any code meant for only one app must be
   wrapped in `#if MODE_STANDARD` / `#if MODE_ECO` (see `TrayApps`, the
   selection stage, and the per-app constants in `MainForm`).
7. **Never push to remote.** Git identity is already configured globally
   (bigthabot) — do not set or change git config.
8. **Don't modify source files outside your wave's scope.** Wave 1 touched no
   source; Wave 2 (A2) does the file split; other waves take their named
   modules (§3, §5).
9. Target OS: Windows 10/11 x64. The apps already handle build-specific
   behavior (Energy Saver differences on 24H2/26200+) — preserve that spirit:
   degrade gracefully, log honestly, never block the main function on a
   best-effort side feature.

---

## §3 Architecture map (as of Wave 5, A17 — v1.1.0 in progress)

### Current files

| File | Responsibility |
|---|---|
| `src\App.cs` | `Program` entry point + the original v1.0.x header/history; picks app identity by define, parses `--confirm` / `--auto` / `--status`. |
| `src\Logger.cs` | `Log` (static) — logging core: per-run timestamped log files, dual sink (buffer + file), session header/footer, retention pruning (30 days / 200 MB). Full API in the **Log API** table below. |
| `src\AsusControl.cs` | `AsusTransport`, `AtkAcpiTransport`, `WmiTransport`, `SwitchOutcome`, `AsusControl`, `GpuServices`. |
| `src\GamePrep.cs` | `GamePrep` (Go Time system optimizations / Eco restoration). |
| `src\EnergySaver.cs` | `EnergySaver` (Energy Saver + Power Mode overlay, power API + registry + Settings automation). |
| `src\Theme.cs` | UI primitives: `WindowIcons`, `UiShapes`, `ShimmerBar`. Colors stay inline at the call sites (as in v1.0.22). |
| `src\Forms.cs` | `MainForm`, `LogForm`, `UiPhase` enum, `TrayAppInfo` + `TrayApps` (`#if MODE_STANDARD`). |
| `src\LogBrowser.cs` | `LogBrowserForm` — log history debugging window: browse/list/view/search past logs from both apps. |
| `src\StorageAnalyzer.cs` | `CleanCategory` + `StorageAnalyzer` (A6): read-only storage cleanup analyzer — measures the WU / deep-clean / GPU shader-cache targets and checks the D7 safety gates. Strictly no deletion here; logs on the `CLEAN` channel. |
| `src\ProcessFreezer.cs` | `ProcessFreezer` + `FreezeResult` — background process freezer: ntdll suspend/resume of the user's `freezelist.txt` apps during a gaming session (see class map below). |
| `src\PowerPlans.cs` | `PowerPlans` + `WuPause` (A13): Ultimate Performance plan switcher and session-scoped Windows Update pauser (both fully reversible). |
| `src\Profiles.cs` | `Profile`, `ProfileStore`, `ProfileBar` (+ private `ProfileNameDialog` modal) — named checkbox presets, JSON persistence and the dark profile bar for the Wave 6 UI (A14). |
| `src\SessionHistory.cs` | Per-run session history: append-only `sessions.jsonl` store + "Session History" viewer (Wave 4, A15). |
| `src\SystemMonitor.cs` | Live system monitor (A16): `MonitorSample` (one reading), `MonitorEngine` (UI-thread sampling engine) and `MonitorPanel` (embeddable dark-theme grid of labeled bars). |
| `src\StorageCleaner.cs` | `CleanResult` + `StorageCleaner` (A7): the Tier 1 DELETING executor — WU download-cache purge (service stop/start + DetectNow via reflection COM), Delivery Optimization cmdlet run, temp/WER/log-archive/crash-dump/thumbnail deletions with the D7 hard guard re-checked per item. Logs on the `CLEAN` channel. |
| `src\ComponentStore.cs` | `DismAnalysis` + `ComponentStore` (A8): Tier 2 WinSxS component-store cleanup, strictly through DISM — read-only `/AnalyzeComponentStore` with output parsing, plus the gated `/StartComponentCleanup` executor. `/ResetBase` FORBIDDEN (D3); WinSxS never touched except through DISM (D7). Logs on the `CLEAN` channel. |
| `src\AppCacheCleaner.cs` | `AppCacheTarget` + `AppCacheCleanResult` + `AppCacheCleaner` (A9): per-app browser/launcher cache cleaner — CACHE-ONLY per D5 (see class map below). |
| `src\DeepClean.cs` | `DeepClean` (A10): deep clean suite — per-user WER error reports (>7 days), setup & upgrade logs (MoSetup/DISM/SIH + aged `setupapi*.old` + Panther top-level setup logs, D7-guarded), and report-only previous Windows installations (`Windows.old`/`$WINDOWS.~BT`/`$WINDOWS.~WS` measured, never deleted). Foreign categories come back zeroed with an "owned by <module>" note (D9). |
| `src\GpuTools.cs` | `GpuTools` (A11): GPU shader-cache measure/clean (NVIDIA DXCache/GLCache/NV_Cache, AMD DxCache/Dx9Cache/GLCache, D3DSCache), confirm-flagged NVIDIA driver installer leftovers (C:\NVIDIA, ProgramData\NVIDIA Corporation\Downloader) and the HAGS (HwSchMode) toggle. Logs on the `GPU` channel. |
| `src\Overlay.cs` | `MonitorOverlayForm` (A17): compact gaming overlay over the monitor engine — frameless TopMost semi-transparent (~260×120, Opacity 0.85, near-black, rounded corners via `UiShapes`), Consolas 9f metric grid (CPU/RAM/Disk/GPU/GPU temp), click-drag anywhere, "monitor off" hint while the engine is not sampling (never starts/stops the engine itself, per D6). |
| `src\TrayIcon.cs` | `SessionTray` (A17, D4): session-only tray quick menu — NotifyIcon with the icon drawn in code (16×16 accent-green rounded square + "G", no asset files), dark-themed ContextMenuStrip (Open Go Time / Restore (Eco Mode) / Toggle overlay / Status balloon / Exit), double-click opens the window; five injected callbacks, null = disabled item; no Forms.cs references. |
| `src\app.manifest` | Win32 manifest: `requireAdministrator`, Windows 10/11 supportedOS, `dpiAware` + PerMonitorV2. |
| `src\build.cmd` | Two csc.exe invocations (MODE_STANDARD / MODE_ECO), each compiling **all `src\*.cs`**; GAC lookup for UIAutomation, WPF dir for WindowsBase; refs incl. `System.Management`, `System.ServiceProcess`, `System.Web.Extensions`; outputs to `dist\`. |
| `src\gotime.ico` / `src\ecomode.ico` | Multi-size (16/32/48/256) taskbar/title icons, one per exe. |
| `src\gotime-256.png` / `src\ecomode-256.png` | Embedded per-build as resource `GpuModeSwitch.appicon.png` (window logo). |
| `src\nvidia-eye.svg`, `src\make-icons.ps1`, `src\pack-ico.ps1` | Icon artwork sources/generators — not part of the build. |
| `README.md`, `LICENSE`, `.gitignore` | Docs (full v1.0.1→v1.0.22 changelog), MIT, ignores `dist/ .vs/ *.user icons-preview/`. |

### Classes by file (namespace `GpuModeSwitch` — key public surface)

| File | Class | Responsibility | Key API |
|---|---|---|---|
| `App.cs` | `Program` (static) | Entry point; picks app identity by define; parses `--confirm` / `--auto` / `--status`; `--status` shows a MessageBox then exits. | `const string Version = "1.0.22"`, `Main(string[])` |
| `Logger.cs` | `Log` (static) | Logging core (Wave 3): per-run log files `%LOCALAPPDATA%\GpuModeSwitch\logs\<GoTime\|EcoMode>\<App>_yyyy-MM-dd_HHmmss.log`, thread-safe buffer + file dual sink, session header/footer, retention (30 days / 200 MB), log listing. | see the **Log API** table below |
| `AsusControl.cs` | `AsusTransport` (abstract) | Transport abstraction: read/write ASUS ACPI values. | `Name`, `Open()`, `Close()`, `ReadRaw(uint)`, `Write(uint, uint)` |
| `AsusControl.cs` | `AtkAcpiTransport : AsusTransport` | Primary: direct `DeviceIoControl` on `\\.\ATKACPI`, IOCTL `0x0022240C`, DSTS/DEVS. | (inherits; logs every call with raw hex) |
| `AsusControl.cs` | `WmiTransport : AsusTransport` | Fallback: WMI `root\WMI` classes `AsusAtkWmi_WMNB` / `ASUS_WMI` (DSTS/DEVS methods). | (inherits) |
| `AsusControl.cs` | `SwitchOutcome` | Result DTO of a switch. | `Ok`, `Changed`, `NeedsRestart`, `RunAgainAfterRestart`, `Headline`, `Detail` |
| `AsusControl.cs` | `AsusControl` (static) | Probes transports × device-ID pairs, normalizes DSTS responses (bare-zero = not implemented), performs the switch with read-back verification, orchestrates NV service release/restart and the MUX fallback. | `Available`, `MuxSupported`, `LastError`, `GetDgpuState()`, `GetMuxState()`, `Precheck(bool eco, out bool ok)`, `SwitchTo(bool eco)`, `Shutdown()`, `DescribeState()` |
| `AsusControl.cs` | `GpuServices` (static) | Finds/stops/starts `NVDisplay.Container*` services (release driver before Eco write, restart after Standard). Best-effort, never fatal. | `StopAll()`, `RestartAll()` |
| `GamePrep.cs` | `GamePrep` (static) | Game prep (Go Time) / restoration (Eco): Game Mode, do-not-disturb, Game DVR, NetworkThrottlingIndex, pause/restart 10 gamer services (SysMain, WSearch, Spooler, DiagTrack, WerSvc, MapsBroker, TrkWks, WMPNetworkSvc, SEMgrSvc, Fax). Unticked items are actively restored. | `ApplyForGaming(bool gameMode, bool dnd, bool dvr, bool throttle, bool pauseServices)`, `ApplyForEco()` |
| `EnergySaver.cs` | `EnergySaver` (static) | Windows Energy Saver + Power Mode overlay: P/Invoke `PowerWriteAC/DCValueIndex` + `PowerSetActiveScheme` on SUB_ENERGYSAVER/ESBATTTHRESHOLD (100%/0%) and the Power Mode overlay GUIDs (0 = best efficiency, 3 = best performance); `EnergySaverState` registry intent; UI-Automation toggle of the Settings "Always use energy saver" switch (search-first flow). | `GetSavedState()`, `Sync(bool on)`, `ToggleAlwaysUseEnergySaver(bool on)`, `ApplyPowerModeOverlay(uint index)` |
| `Theme.cs` | `WindowIcons` (static) | Applies the exe's own icon to a form. | `Apply(Form)` |
| `Theme.cs` | `UiShapes` (static) | Rounded-rect `GraphicsPath` helper for the themed UI. | `RoundRect(...)` |
| `Theme.cs` | `ShimmerBar : Control` | Indeterminate animated shimmer progress bar. | `Active`, `Advance()`, `Accent` |
| `Forms.cs` | `LogForm : Form` | Log viewer (Wave 4): read-only monospace box over the **live buffer or any older per-run log**; history dropdown (`Log.ListLogs`, newest first, "Current session (live)" on top + Refresh), severity filter All/Info/Warn/Error (parses the `[LEVEL]` prefix, exception continuations ride along, live view re-filters on change), case-insensitive **Find next** (wraps, Enter repeats), **Open folder** (Explorer `/select` on the viewed log), **Copy log** (displayed text) + **Copy all** (whole source), path strip, pre-selected text (Ctrl+C immediately), 5-row deterministic TableLayoutPanel shell (DPI-proof). Reads `Log.Snapshot()` / `Log.CurrentLogPath` / `Log.ListLogs(appName)`; logs UI actions as `log viewer: ...` via `Log.Info`. | ctor `LogForm(string appName)` |
| `Forms.cs` | `TrayAppInfo` / `TrayApps` (static, `#if MODE_STANDARD`) | Known tray apps (Parsec, Google Drive, Jellyfin, Riot Client, Riot Vanguard); detection against running processes; close = stop matching watchdog services (registry scan) then graceful close → kill, 3 rounds. | `Known`, `Detect()`, `Close(TrayAppInfo)` |
| `Forms.cs` | `UiPhase` (enum) | Main-window phase machine states. | `Probe`, `Confirm`, `Applying`, `Result` |
| `Forms.cs` | `MainForm : Form` | Themed borderless resizable window (rounded corners, fade-in, edge drag/resize via WndProc), phase machine (`UiPhase`: Probe → Confirm/Select → Applying → Result), selection-stage checkboxes + tray picker (Standard), restart prompt, View log button. | ctor `MainForm(bool confirmMode, bool autoMode)` |
| `LogBrowser.cs` | `LogBrowserForm : Form` | Log-history debugging window (Wave 4): LEFT list of every log from BOTH apps (`Log.LogsRoot` + `GoTime`/`EcoMode`, merged newest first; columns App, File, Size (KB), Last write), RIGHT read-only monospace viewer in LogForm's style (files > ~2 MB load the tail with a notice line; text pre-selected so Ctrl+C works immediately), toolbar (Find box + Search find-next / Search all logs / Open folder via `explorer.exe /select` / Copy view / Refresh) and a status strip. Search is case-insensitive; search-all lists matching lines with file + line number in a results pane (cap 500 + truncation notice; clicking a hit opens that file at that line). Files opened with `FileShare.ReadWrite` so the live current log is viewable; search-all runs on ThreadPool with the RunBg/SafeInvoke pattern. Logs via `Log.Info`: "log browser: opened ..." / "log browser: search '...' ...". | `ShowBrowser(Form owner)` (non-modal, owned by caller); compiled into both targets, reachable from Go Time only (menu entry = Wave 6/A18) |
| `StorageAnalyzer.cs` | `CleanCategory` | Result DTO of one cleanable target. | `Name`, `Kind` (`wu`/`dism`/`deepclean`/`appcache`/`gpu`), `Bytes`, `Files`, `Notes`, `RiskLabel`, `Selected`; `SizeText` (B/KB/MB/GB) |
| `StorageAnalyzer.cs` | `StorageAnalyzer` (static) | Read-only measurement of all cleanup targets (WU download cache, Delivery Optimization, Windows temp >7 days, WER reports, old CBS/WindowsUpdate log archives, ReportingEvents.log, user temp, crash dumps, Explorer thumbnail caches, per-path GPU shader caches) + D7 gates (elevation, pending-reboot signals, busy WU services) that fail closed. Long paths via kernel32 `FindFirstFileW`/`GetFileAttributesExW` with the `\\?\` prefix (DirectoryInfo rejects it on .NET 4.x); reparse points skipped; per-item failures swallowed into `Notes`. | `MeasureAll()`, `CheckGates()`, `GatesSummaryText(List<string>)` |
| `ProcessFreezer.cs` | `FreezeResult` (public struct) | Result DTO of one freeze/resume round, consumed by the Wave 6 UI. | `Suspended`, `Failed`, `SkippedGuarded` (ints), `Summary` (string tally) |
| `ProcessFreezer.cs` | `ProcessFreezer` (public static) | Background process freezer (A12): suspends/resumes freeze-list apps via ntdll `NtSuspendProcess`/`NtResumeProcess` on `Process` handles. Freeze list `%LOCALAPPDATA%\GpuModeSwitch\freezelist.txt` (one name per line, no .exe; defaults = the TrayApps process names from Forms.cs: parsec, googledrivefs, googledrivesync, jellyfin, riotclient, vgtray, vgc). Never-freeze guard (system, smss, csrss, wininit, winlogon, services, lsass, dwm, explorer, audiodg, MsMpEng, SecurityHealthService + Go Time/Eco Mode + own PID; blank names fail closed); resume only this session's tracked PID+name pairs with a PID-reuse re-check; nothing is ever killed; logs through the FREEZE channel. | `GetUserList()`, `SaveUserList(List<string>)`, `SeedDefaultListIfMissing()`, `IsGuarded(string)`, `FreezeSelected()`, `ResumeAll()`, `ResumeAllSafe()` |
| `PowerPlans.cs` | `PowerPlans` (static) | Ultimate Performance plan switcher via powercfg.exe: duplicates the hidden Ultimate template (e9a42b02-d5df-448d-aa00-03f14749eb61) once, remembers the previously active plan (first successful run only, never overwritten), activates idempotently. State file `%LOCALAPPDATA%\GpuModeSwitch\powerplan.txt`: line 1 = created-GUID (kept for reuse), line 2 = previous-GUID (cleared by restore). All failures logged with raw output; never throws. | `SetUltimate()`, `RestorePrevious()` (both `bool`) |
| `PowerPlans.cs` | `WuPause` (static) | Session-scoped WU pauser: stops wuauserv/bits/DoSvc and restarts exactly the services this process stopped (static per-service flags); StartType never changed; exception-safe for exit paths. | `PauseUpdates()`, `ResumeUpdates()` |
| `Profiles.cs` | `Profile` | One named preset (plain data holder for the store and Wave 6 UI). | fields `Name`, `Selections` (`Dictionary<string,bool>`); ctors `Profile()` (serializer-required) and `Profile(name, selections)` |
| `Profiles.cs` | `ProfileStore` (static) | JSON persistence at `%LOCALAPPDATA%\GpuModeSwitch\profiles.json` (array of `{Name, Selections}`) via the already-referenced `System.Web.Extensions` JavaScriptSerializer; file order kept; every write a full rewrite (serialize → `File.WriteAllText`); failures logged, never thrown; missing/corrupt file → empty list + one WARN. | `FilePath`, `LoadAll()`, `SaveAll(List<Profile>)`, `Save(name, selections)` (upsert by exact name; logs `[PROFILE] profile saved: <name> (N keys)`), `Delete(name)` (logs `[PROFILE] profile deleted: <name>`), `Find(name)` (exact, always fresh) |
| `Profiles.cs` | `ProfileBar : UserControl` | Dark horizontal bar (Label "Profile:" + DropDownList combo + Apply/Save.../Delete/Refresh, Forms.cs palette inline, default 560×44) wiring Wave 6 checkbox groups to saved profiles; Apply reads the selected profile fresh from the store; Save grabs the host state via `CollectSelections`, asks the name in the dark `ProfileNameDialog` modal, then saves/refreshes/fires `Saved`; Delete confirms Yes/No; every action logs `[PROFILE]`. | events `ApplyRequested`, `Saved` (`Action<string, Dictionary<string,bool>>`), `CollectSelections` (`Func<Dictionary<string,bool>>`); `Reload()` |
| `SessionHistory.cs` | `SessionRecord` | One recorded run — plain data holder for the JSONL store; parameterless ctor keeps the JavaScriptSerializer round-trip working. | public fields: `UtcTimestamp`, `App`, `Mode`, `ActionsApplied` (List<string>), `SpaceFreedByCategory` (Dictionary<string,long>), `DurationSec`, `ErrorCount`, `Result` |
| `SessionHistory.cs` | `SessionHistory` (static) | Append-only JSONL store at `%LOCALAPPDATA%\GpuModeSwitch\sessions.jsonl` (one serialized object per line); all ops best-effort, never throws; corrupt lines skipped with WARNs capped at 3; timestamps normalized to UTC. | `FilePath`, `Append(SessionRecord)`, `ReadAll()` (newest first), `ExportText(List<SessionRecord>)`, `FormatBytes(long)` |
| `SessionHistory.cs` | `SessionHistoryForm : Form` | "Session History" viewer: dark list (When local / App / Mode / Result / Freed / Errors, newest first) + Refresh, read-only detail pane showing the selected record's ExportText (pre-selected so Ctrl+C works immediately), "Export .txt" + "Copy" buttons; logs `Log.Info` "session history: ...". | ctor `SessionHistoryForm()`, `static void ShowHistory(Form owner)` (non-modal, owned) |
| `SystemMonitor.cs` | `MonitorSample` | One monitor reading; public-field DTO for the panel (Wave 6) and overlay (Wave 5). `RamText` formats "4.2 / 16.0 GB". | fields `CpuPercent`, `RamUsedBytes`, `RamTotalBytes`, `DiskActivePercent`, `GpuPercent`, `GpuTempC`, `CpuTempC`, `HasGpu`, `HasGpuTemp`, `HasCpuTemp`, `Timestamp`; prop `RamText` |
| `SystemMonitor.cs` | `MonitorEngine` (static) | Periodic sampler on a System.Windows.Forms.Timer (samples arrive on the UI thread; decision documented in the file header). CPU + disk PerformanceCounters (created once, primed, recreated after Stop), RAM via kernel32 `GlobalMemoryStatusEx` P/Invoke, GPU via nvidia-smi (System32 → NVSMI → PATH, resolved once per session, hidden window, 2.5 s timeout; absent/failed → N/A, never faked from CPU), CPU temp via WMI `root\WMI MSAcpi_ThermalZoneTemperature`. Per-metric try/catch keeps last-known; unavailability logged once per session. Logs through MONITOR. | `Start(int)`, `Start()`, `Stop()`, `RunOnce()`, `Running`, `LastSample`, `event Action<MonitorSample> SampleReady` |
| `SystemMonitor.cs` | `MonitorPanel : UserControl` | Dark-theme embeddable panel: deterministic TableLayoutPanel grid (Label + ProgressBar + value per metric: CPU %, RAM, Disk %, GPU %, GPU temp, CPU temp; "N/A" when unavailable) + "updated HH:mm:ss" footer; min 360x180; bars dark via uxtheme SetWindowTheme classic mode. | ctor `MonitorPanel()`, `AttachToEngine()`, `DetachFromEngine()` |
| `StorageCleaner.cs` | `CleanResult` | Outcome DTO of one cleaned category (deleting counterpart of `CleanCategory`) for the Wave 6 UI and session history. | fields `CategoryName`, `BytesFreed`, `FilesDeleted`, `FilesSkipped`, `Notes`; ctors `CleanResult()` and `CleanResult(name, bytes, deleted, skipped, notes)`; `Summary` ("1.20 GB freed, 567 files deleted, 3 skipped") |
| `StorageCleaner.cs` | `StorageCleaner` (static) | Tier 1 cleaner (Wave 5, A7): re-checks the D7 gates itself (any reason → nothing deleted, returns false), then per exact category name: WU purge (stop UsoSvc→wuauserv→bits bounded 20 s, delete CHILDREN of SoftwareDistribution\Download only, restart bits→wuauserv→UsoSvc only services it stopped, DetectNow via `Microsoft.Update.AutoUpdate` reflection COM — wuauserv refusal aborts only that category); DO cache via `Delete-DeliveryOptimizationCache -Force` (never `-IncludePinnedFiles`, 120 s kill timeout, freed bytes = pre-measured estimate); Windows temp >7 d, user temp, WER >7 d, CbsPersist_*.cab + WindowsUpdate logs >30 d, ReportingEvents.log (during the purge pass when selected, else standalone), crash dumps, thumbnail caches (locked files skip+WARN); gpu/appcache/dism kinds returned zeroed with an "owned by ..." note (D9). HARD GUARD `IsForbiddenPath` before every deletion: WinSxS, catroot, catroot2, `C:\Windows\Installer`, Servicing, anything containing pending.xml, and everything under SoftwareDistribution except Download children + ReportingEvents.log (DataStore included); hit → `Log.Error "GUARD: refusing <path>"`. Long paths deleted via kernel32 `FindFirstFileW`/`DeleteFileW`/`RemoveDirectoryW` with `\\?\`; reparse points never followed; read-only access-denied gets one attribute-clear retry; static run gate refuses concurrent `Clean()` calls. | `Clean(List<CleanCategory>, out List<CleanResult>, out long)`, `FormatBytes(long)` |
| `ComponentStore.cs` | `DismAnalysis` | Result DTO of one DISM `/AnalyzeComponentStore` run; raw output preserved in full. | fields `CleanupRecommended`, `ActualSizeText`, `SizeText`, `ReclaimableShown`, `ExitCode` (-1 when it never exited cleanly), `OutputText`, `ErrorText` |
| `ComponentStore.cs` | `ComponentStore` (static) | Tier 2 executor (A8). `Analyze()` runs `Dism.exe /Online /Cleanup-Image /AnalyzeComponentStore` hidden (both streams pumped async so the 20-min hard timeout stays in charge; kill + ERROR on timeout) and parses case-insensitively: "Component Store Cleanup Recommended : Yes/No" → CleanupRecommended, "Actual Size of Component Store :" → ActualSizeText, "Size of Component Store in WinSxS folder :" → SizeText, any "Reclaimable" line → ReclaimableShown; localized-output parse miss → WARN, raw output kept. `RunCleanup(out outputTail)`: D7 gate re-check first (any reason → Log.Error each + refuse), analyze-first ALWAYS (not recommended → "skipped (analyzer says not recommended)" + success), then `/StartComponentCleanup` — /ResetBase FORBIDDEN (D3): arguments exist only as a const + a defensive runtime guard that aborts on /ResetBase; streamed progress `dism: <line>` collapsed by stripping `\b` + trimming and dropping repeats; 45-min kill; DISM error 1726 → WARN "known 24H2+ issue, treated as retryable warning" + success; final `dism cleanup: exit N` + `outputTail` (last ~40 meaningful lines) for the UI. One shared busy flag serializes both ops. | `Analyze()`, `CleanupRecommended()`, `RunCleanup(out string)`, `IsBusy` |
| `AppCacheCleaner.cs` | `AppCacheTarget` | One cleanable app target: `Name`, `ProcessNames` (no .exe; any one alive blocks cleaning), `CacheDirs` (RESOLVED absolute dirs from `AppCacheCleaner.Targets()` — empty when the app is not installed). | ctor `(string name, string[] processNames, string[] cacheDirs)` |
| `AppCacheCleaner.cs` | `AppCacheCleanResult` | Result of cleaning one target (delete result DTO; CleanCategory is a measurement). | fields `Name`, `BytesFreed`, `FilesDeleted`, `FilesSkipped`, `Notes` |
| `AppCacheCleaner.cs` | `AppCacheCleaner` (static) | Per-app browser/launcher cache cleaner (A9, D5 CACHE-ONLY — the hard safety rule; stated in the class header). Two independent walls: (1) the literal whitelist IS the D5 enforcement — candidates are ONLY ever `Cache`/`Code Cache`/`GPUCache`/`DawnCache`/`GrShaderCache`/`ShaderCache`/`Media Cache`/`cache2`/`startupCache`/`htmlcache`/`webcache*`/`shadercache`/`depotcache` resolved under the explicit per-app parents (Chromium `User Data\<profile>` for Chrome/Edge/Brave/Vivaldi, Opera Stable/GX, Firefox `Profiles\*\cache2`+startupCache, Steam htmlcache + steamapps shadercache/depotcache via HKCU SteamPath + libraryfolders.vdf — no disk scan, Discord `Cache`,`Code Cache`,`GPUCache`, Epic `webcache*`, Battle.net `Cache`,`GPUCache` only); (2) a forbidden-name wall (cookies, history, login data, sessions, bookmarks, local storage, indexeddb, web data, places.sqlite, cookies.sqlite, key3/key4.db, logins.json, formhistory.sqlite, sync data, ...) checked against every candidate path segment and child name right before any enumeration or delete (WARN + skip) — even a future path-table bug cannot delete personal data. `Clean` deletes cache-dir CONTENTS only (dirs kept), per-item try/catch, locked files skipped+WARN, reparse points never followed, an app with a running process is never touched (skip + WARN; process-scan failure fails closed), never throws. `Measure` is read-only; absent installs skipped with a CLEAN "not present" note. | `Targets()`, `RunningApps()`, `Measure()`, `Clean(List<string>, out long totalBytesFreed)`, `IsForbiddenName(string)` |
| `DeepClean.cs` | `DeepClean` (static) | Deep clean suite (A10, ownership split D9): `Measure()` read-only-measures its three categories (per-user WER ReportQueue/ReportArchive items >7 days; aged MoSetup/DISM/SIH files + `C:\Windows` `setupapi*.log.old`/`*.old` (>30d, never the active setupapi.dev.log) + Panther top-level `setup*.log/.etl/.xml` >30d except setupact.log/setuperr.log; previous installations measured per root). `Clean(selected, out results, out totalBytesFreed)` mirrors StorageCleaner.Clean: re-checks `CheckGates()` fail-closed, static run gate, before/after free space, final `deepclean: total ...` line, never throws, locked files skip+WARN. D7 hard guard = StorageCleaner's forbidden set + a Panther rule. "Previous Windows installations (report only)" is `Selected=false` and implements no deletion (D3 Tier 3). Long paths via kernel32 `\\?\` walkers. | `Measure()`, `Clean(List<CleanCategory>, out List<CleanResult>, out long)`, name consts `CatPerUserErrorReports`/`CatSetupUpgradeLogs`/`CatPreviousInstallations` |
| `GpuTools.cs` | `GpuTools` (static) | GPU tools (A11): owns the CANONICAL cache path table (7 shader-cache categories reusing StorageAnalyzer's exact names + 2 confirm-flagged driver-leftover locations). `Measure()` = read-only, one gpu-kind CleanCategory per present location, missing = "not present (skipped)" log only. `Clean()` re-checks `CheckGates()` itself (fail closed), deletes cache-dir CONTENTS recursively (dirs kept; drivers rebuild), kernel32 `\\?\` long-path walkers, D7 hard guard before every deletion, locked files skip+WARN, per-location "gputools '<name>': X freed, N files deleted, M skipped" + before/after free-space total. Driver leftovers cleaned only on the confirm flag. HAGS = HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode (2=on/1=off; value never deleted, off writes 1; effective after reboot). | `Measure()`, `Clean(selected, includeDriverLeftovers, out results, out totalBytesFreed)`, `HagsStateText()`, `SetHags(bool)`, `RebootRequiredForHags()` |
| `Overlay.cs` | `MonitorOverlayForm : Form` | Compact frameless overlay bound to the monitor engine: CPU xx.x% / RAM a/b GB / Disk xx% / GPU xx% or N/A / GPU t°C or N/A + "updated HH:mm:ss" footer (Consolas 9f, dark inline palette, N/A dimmed). Shows without stealing focus (`ShowWithoutActivation`); first show lands bottom-right of the primary working area; click-drag anywhere; "monitor off" hint while `MonitorEngine.Running` is false (attach + per-sample + 2 s poll); sample handler InvokeRequired-marshaled and try/caught; Dispose detaches. Logs MONITOR: "overlay: attached/detached/shown/hidden/toggled". | ctor `MonitorOverlayForm()`, `Attach()`, `Detach()`, `Toggle()` |
| `TrayIcon.cs` | `SessionTray : IDisposable` | Session-only tray icon (D4; compiled into both targets with no `#if` — inert until instantiated, only Go Time's Wave 6 code instantiates it). Icon drawn in code (16×16 accent-green rounded square + white "G"; HICON freed via `DestroyIcon`; system-icon fallback). Dark `ContextMenuStrip` (private `ProfessionalColorTable`): Open Go Time / — / Restore (Eco Mode) / Toggle overlay / Status (balloon) / — / Exit; double-click = openWindow; null callback → item disabled; every invocation logged + try/caught. | ctor `SessionTray(Action openWindow, Action applyEco, Action toggleOverlay, Func<string> statusText, Action exitApp)`, `Show(string initialTip)`, `Hide()`, `SetStatus(string)`, `Dispose()` |

### Log API (`src\Logger.cs` — Wave 3; the logging core every module logs through)

| Member | Signature | Notes |
|---|---|---|
| `Info` | `void Info(string msg)` | INFO line, channel APP. |
| `Warn` | `void Warn(string msg)` | WARN line, channel APP. |
| `Error` | `void Error(string msg, Exception ex = null)` | ERROR line; exception type + message + stack appended on indented continuation lines. |
| `Chan` | `void Chan(string channel, string msg)` | Channel-tagged INFO line. Channels: `CLEAN`, `GPU`, `FREEZE`, `POWER`, `TRAY`, `MONITOR`, `PROFILE`, `SESSION` — unknown tags still accepted. Existing modules log as GPU = AsusControl, POWER = EnergySaver/Power Mode + PowerPlans/WuPause, TRAY = TrayApps; everything else APP. |
| `BeginSession` | `void BeginSession(string appName, string version)` | Creates the per-run file `%LOCALAPPDATA%\GpuModeSwitch\logs\<GoTime\|EcoMode>\<App>_yyyy-MM-dd_HHmmss.log` (folder/prefix: appName containing "Eco" → EcoMode, else GoTime), writes the session header block (app + version + active `/define` mode, Windows build from registry CurrentBuild+UBR, machine model via WMI `Win32_ComputerSystem`, admin check via `WindowsPrincipal`, .NET runtime version), then prunes retention. |
| `EndSession` | `void EndSession(string result)` | Footer line: result + total session duration. Called by `Program.Main` on both exit paths. |
| `CurrentLogPath` | `string` (property) | Full path of the current run's log file (`""` before BeginSession). |
| `LogsRoot` | `string` (property) | Root logs directory `%LOCALAPPDATA%\GpuModeSwitch\logs`. |
| `ListLogs` | `string[] ListLogs(string app)` | Full paths of that app's log files, **newest first**. |
| `Snapshot` | `string Snapshot()` | Full text of the run's buffer — LogForm's viewer text and Copy log source. (Addition to the §8 contract sketch, made for LogForm; A4 keeps using it.) |

Line format: `yyyy-MM-dd HH:mm:ss.fff [LEVEL] (channel) message` (LEVEL =
INFO/WARN/ERROR; default channel APP). Dual sink under a single lock:
in-memory `StringBuilder` buffer + `File.AppendAllText` to the run's file; all
file IO is try/caught — a file-sink failure disables the sink for the rest of
the run with a single WARN while the buffer keeps working, so logging can
never crash or stall the app. Retention runs on BeginSession after the header
block (so removals are captured in the new file): delete files older than
30 days, then — if the folder total still exceeds 200 MB — delete
oldest-first by the `<App>_yyyy-MM-dd_HHmmss` timestamp parsed from the file
name (LastWriteTime fallback) until ≤ 200 MB; **never** files stamped today
and never the current log. Removal lines:
`retention: removed <name> (age N days)` / `(size cap 200 MB)`. See D8.

Conventions worth preserving: every side effect is logged; every best-effort
step is individually try/caught and never aborts the main flow; the UI updates
via `RunBg` (ThreadPool) + `SafeInvoke` (marshals to UI thread).

### File split (Wave 2, A2 — done, zero behavior change)

The single 2,678-line `GpuModeSwitch.cs` was decomposed into the seven module
files above. Every class moved verbatim; each `#if MODE_STANDARD` /
`#if MODE_ECO` region stayed inside one file, so the two-target conditional
compilation behaves exactly as before; `build.cmd` compiles all `src\*.cs`
into both targets — the two `/define` targets and output names never change.
Two placement notes vs. the original sketch: `WindowIcons` lives in
`Theme.cs` (it is a UI primitive), and the `UiPhase` enum (used only by
`MainForm`) lives in `Forms.cs`.

### Future modules

**None — every planned module has landed** (Wave 4: LogBrowser, StorageAnalyzer,
ProcessFreezer, PowerPlans, Profiles, SessionHistory, SystemMonitor; Wave 5:
StorageCleaner, ComponentStore, AppCacheCleaner, DeepClean, GpuTools, Overlay,
TrayIcon). What remains is Wave 6 integration only (A18, §8).

---

## §4 Design decisions log (APPEND-ONLY — add new decisions at the bottom, never edit old ones)

**D1 (2026-09-06, A1/a3-agreed) — Logging rewrite contract.**
Per-run timestamped log files under
`%LOCALAPPDATA%\GpuModeSwitch\logs\<GoTime|EcoMode>\<App>_yyyy-MM-dd_HHmmss.log`
(one file per run; the old single `GoTime.log`/`EcoMode.log` is retired).
Line format: `yyyy-MM-dd HH:mm:ss.fff [LEVEL] (channel) message`.
Channels: `CLEAN`, `GPU`, `FREEZE`, `POWER`, `TRAY`, `MONITOR`, `PROFILE`,
`SESSION` (plus untagged/general lines). Retention: on session start, delete
log files older than 30 days, then delete oldest-first until the folder is
≤ 200 MB total; **never delete today's logs**. The Copy log button survives
and copies the current run's log. Implemented by A3 in Wave 3 (contract in §8).

**D2 (2026-09-06, A1) — Windows Update storage cleanup lives in the GO flow as checkbox groups, not a separate app.**
The cleanup suite is presented inside Go Time's launch-time selection stage as
checkbox groups (like "System optimizations" today), applied after GO
alongside the GPU switch. Eco Mode does not grow cleanup UI.

**D3 (2026-09-06, A1) — Storage cleanup tiers.**
- **Tier 1 — safe cache purge (default candidate):** stop `wuauserv`, `bits`,
  `usosvc` → purge `C:\Windows\SoftwareDistribution\Download\*` → restart the
  services → trigger `wuauclt /DetectNow` (or UsoClient equivalent) so WU
  re-detects; Delivery Optimization cache via PowerShell
  `Delete-DeliveryOptimizationCache -Force`; `C:\Windows\Temp` files older
  than 7 days; WER report archives; old `CbsPersist_*.cab` files in
  `C:\Windows\Logs\CBS`; old Windows Update `.etl` logs. Everything is
  logged per item (found size, deleted size, failures are non-fatal).
- **Tier 2 — component store, analyze-first always:** run
  `DISM /Online /AnalyzeComponentStore` and offer
  `DISM /Online /StartComponentCleanup` **only if DISM's output recommends
  it**. Never runs automatically without the analysis result and user tick.
- **Tier 3 — REJECTED:** `DISM /ResetBase` and Windows.old removal are
  explicitly rejected for this suite (irreversible, breaks update
  uninstall/repair). Do not implement them.
- Analyze-first always: any tier shows what it found before deleting.

**D4 (2026-09-06, A1) — Suite shape.** The suite stays **two exes** (Go Time +
Eco Mode). Go Time gains a **session-only tray icon** (lives only while the
process runs; no autostart, no persistence) exposing the v1.1 features in a
context menu.

**D5 (2026-09-06, A1) — Browser/app cache cleaner is CACHE-ONLY.**
The per-app browser/app cache cleaner (A9) may delete: HTTP cache, code
cache, GPUCache, shader caches, service-worker *cache storage* temp data,
thumbnails. It must **never** touch: cookies, browsing history, saved
passwords/autofill, active login sessions, bookmarks/favorites, localStorage/
IndexedDB user data. When in doubt for a cache dir: skip it and log why.

**D6 (2026-09-06, A1) — Monitor shape.** The live system monitor (A16) is a
**panel tab inside the app** plus a small **overlay** window (A17). It never
runs as a hidden background service; it only samples while visible/session.

**D7 (2026-09-06, A1) — Cleanup safety gates.** All cleanup actions are
blocked (with a clear logged reason and UI message) when: a reboot is pending
(pending file-rename operations / servicing reboot flags), Windows Update is
actively installing, or the process is not elevated. **Never touch, ever:**
`WinSxS` contents (read/analyze via DISM only), `catroot`/`catroot2`,
`C:\Windows\Installer`, the Servicing folder, `pending.xml`. Cleanup only
targets the Tier 1/Tier 2 locations in D3 and the cache paths in D5.

**D8 (2026-09-06, A3) — Logging line format + retention policy finalized.**
`Log` (`src\Logger.cs`) is the single logging core; all modules log through it.
Line format `yyyy-MM-dd HH:mm:ss.fff [LEVEL] (channel) message`; LEVEL is
INFO/WARN/ERROR; channel is APP (default) or one of CLEAN, GPU, FREEZE, POWER,
TRAY, MONITOR, PROFILE, SESSION (unknown tags accepted). Migration mapping for
v1.0.x lines: AsusControl → GPU, EnergySaver/Power Mode overlay → POWER,
TrayApps → TRAY, everything else APP; the two UI exception reports became
`Log.Error` (exception type/message/stack on continuation lines). Retention on
BeginSession (run after the header block so removals are captured in the new
file): in the app's log folder delete files older than 30 days, then — if the
folder total still exceeds 200 MB — delete oldest-first by the
`<App>_yyyy-MM-dd_HHmmss` timestamp parsed from the file name (LastWriteTime
fallback) until ≤ 200 MB; **never delete files stamped today or the current
run's log**. Removal lines: `retention: removed <name> (age N days)` /
`(size cap 200 MB)`. File-sink failures disable the file sink for the rest of
the run with a single WARN; the in-memory buffer (and therefore the log window
+ Copy log) always keeps working. `Log` also exposes `Snapshot()` (full buffer
text) beyond the original contract list — LogForm consumes it; A4 builds on it.

**D9 (2026-09-06, orchestrator) — Cleanup category ownership split (A7/A9/A10/A11/A8).**
Every cleanup target has exactly one owning Wave 5 module, and every cleaner
returns a zeroed `CleanResult` with an `owned by <module>` note for foreign
categories it receives. **StorageCleaner (A7)** owns the Tier 1 categories
from `StorageAnalyzer.MeasureAll()` (Windows Update download cache, Delivery
Optimization cache, Windows temp >7 days, user temp files, Windows error
reports in the ProgramData WER trees, old update log archives, update
reporting log, crash dumps, thumbnail caches) — A10 therefore does NOT
re-implement them. **DeepClean (A10)** owns the new categories: per-user
error reports (%LOCALAPPDATA% WER ReportQueue/ReportArchive, items older
than 7 days), setup & upgrade logs (MoSetup/DISM/SIH files older than 30
days; aged `setupapi*.log.old`/`*.old` at the Windows root, never the active
setupapi.dev.log; `C:\Windows\Panther` top-level `setup*.log/.etl/.xml`
older than 30 days except setupact.log/setuperr.log — subdirectories never
touched, when in doubt skip + note), and previous Windows installations as a
REPORT-ONLY category (measure `C:\Windows.old`, `C:\$WINDOWS.~BT`,
`C:\$WINDOWS.~WS` with Selected=false; deletion rejected by design per the
D3 Tier 3 decision and not implemented anywhere in DeepClean — this gives
the Wave 6 UI visibility without enabling the dangerous action).
**AppCacheCleaner (A9)** owns browser/app caches (cache-only per D5),
**GpuTools (A11)** owns GPU shader caches and driver leftovers, and
**ComponentStore (A8)** owns the DISM component store (Tier 2).

---

## §5 Feature checklist (v1.1.0)

| Feature | Owning agent | Status |
|---|---|---|
| Bootstrap: clone, branch, baseline build, handbook | **A1** | **done** (Wave 1) |
| Decomposition of `GpuModeSwitch.cs` into module files (zero behavior change) | **A2** (Wave 2) | **done** |
| Logging rewrite (`Logger.cs` → `Log` contract, per-run files, retention) | **A3** (Wave 3) | **done** |
| Log window upgrade (per-run files, open-log-folder, richer view) | **A4** | **done** (Wave 4, out-of-tree verified both defines) |
| Log browser (`LogBrowser.cs`, browse/list/past logs) | **A5** | **done** (Wave 4; menu wiring lands with A18) |
| Storage analyzer (`StorageAnalyzer.cs`, analyze-first reports) | **A6** | **done** (Wave 4; live read-only smoke run included) |
| Tier 1 safe cache purger (`StorageCleaner.cs`) | **A7** | **done** (Wave 5, out-of-tree verified both defines) |
| Component store analyze + StartComponentCleanup (`ComponentStore.cs`) | **A8** | **done** (Wave 5; out-of-tree verified both defines) |
| Per-app browser/app cache cleaner, CACHE-ONLY (`AppCacheCleaner.cs`) | **A9** | **done** (Wave 5; out-of-tree verified both defines + read-only Measure smoke run) |
| Deep clean suite (`DeepClean.cs`) | **A10** | **done** (Wave 5; out-of-tree compile verified on both defines + read-only Measure smoke run; Clean never executed by design) |
| GPU shader-cache tools (`GpuTools.cs`) | **A11** | **done** (Wave 5; out-of-tree harness both defines zero diagnostics, read-only Measure smoke run incl. DXCache 23.15 GB cross-validated against the analyzer) |
| Background process freezer (`ProcessFreezer.cs`) | **A12** | **done** (Wave 4) |
| Ultimate Performance power plan switcher + Windows Update pauser (`PowerPlans.cs`) | **A13** | **done** (Wave 4) |
| Named profiles (`Profiles.cs`) | **A14** | **done** (Wave 4) |
| Session history (`SessionHistory.cs`) | **A15** | **done** (Wave 4) |
| Live system monitor (`SystemMonitor.cs`, panel tab) | **A16** | **done** (Wave 4) |
| Session-only tray menu + overlay (`TrayIcon.cs`, `Overlay.cs`) | **A17** | **done** (Wave 5; out-of-tree verified both defines; tray/overlay wiring lands with A18) |
| GO-flow integration of cleanup into Go Time selection stage | **A18** | not started |

Rules: update your row's Status as you go (`in progress` → `done` with the
commit hash); mark blocked with the reason. Add new rows at the bottom.

---

## §6 Progress log (append-only)

- **2026-09-06 — Wave 1 / A1 (Bootstrap) completed.**
  - Cloned https://github.com/ryanthabot/asus-gpu-mode-switch into
    `C:\Users\bd799\Documents\Projects\system optimization\asus-gpu-mode-switch`
    (working tree clean at `main` @ `4260aef`, v1.0.22).
  - Created and switched to branch `v1.1-logging-cleanup`.
  - Read `README.md`, `src\GpuModeSwitch.cs` (2,678 lines, all classes),
    `src\build.cmd`, `src\app.manifest`. No source files modified.
  - **Baseline build verified:** `cmd //c "src\build.cmd"` from repo root →
    exit code 0, zero csc diagnostics, output `Build OK:` + `Eco Mode.exe`,
    `Go Time.exe`. Artifacts: `dist\Go Time.exe` (92,672 bytes),
    `dist\Eco Mode.exe` (86,016 bytes). csc confirmed as
    `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, banner
    "version 4.8.9221.0 for C# 5". Full evidence in `BUILD_NOTES.md`.
    (`--status` run deferred — the requireAdministrator manifest makes
    unattended runs pop UAC + a modal MessageBox; it is a manual verify step.)
  - Wrote `BUILD_NOTES.md` (repo root) and this handbook (`docs\HANDBOOK.md`).
  - Committed both as `chore(bootstrap): clone baseline v1.0.22 and create
    living handbook` on `v1.1-logging-cleanup`. Not pushed (never push).

- **2026-09-06 — Wave 2 / A2 (Decomposition) completed.**
  - Read this handbook, `src\GpuModeSwitch.cs` (2,678 lines, 16 classes +
    `UiPhase` enum) and `src\build.cmd`. Split the single source into seven
    module files under `src\`, moving whole classes verbatim (no logic
    edits, no renames; every `#if MODE_STANDARD` / `#if MODE_ECO` region
    stayed inside one file):
    - `App.cs` — `Program` (plus the original v1.0.x file header/history).
    - `Logger.cs` — `Logger` (behavior unchanged this wave).
    - `AsusControl.cs` — `AsusTransport`, `AtkAcpiTransport`,
      `WmiTransport`, `SwitchOutcome`, `AsusControl`, `GpuServices`.
    - `GamePrep.cs` — `GamePrep`.
    - `EnergySaver.cs` — `EnergySaver`.
    - `Theme.cs` — `WindowIcons`, `UiShapes`, `ShimmerBar`.
    - `Forms.cs` — `LogForm`, `TrayAppInfo`, `TrayApps`
      (`#if MODE_STANDARD`), `UiPhase`, `MainForm`.
  - `build.cmd`: both csc invocations now compile all `src\*.cs` and gain
    `/r:System.Web.Extensions.dll` (`System.Management.dll` and
    `System.ServiceProcess.dll` were already referenced — future waves need
    all three; adding now avoids build.cmd churn). Defines, manifest,
    icons, resources and outputs unchanged.
  - `src\GpuModeSwitch.cs` was deleted only after the split compiled
    cleanly (content preserved in git history).
  - **Build verified:** `cmd //c "src\\build.cmd"` from repo root → exit
    code 0, zero csc diagnostics, output `Build OK:` + `Eco Mode.exe`,
    `Go Time.exe`. Artifacts: `dist\Go Time.exe` **92,672 bytes** (baseline
    92,672 — identical) and `dist\Eco Mode.exe` **86,016 bytes** (baseline
    86,016 — identical). Define split confirmed in the binaries via UTF-16
    string scan: "System optimizations" / "GO TIME" only in Go Time.exe;
    "Eco Mode - status" / "ECO MODE" only in Eco Mode.exe. Exes not run
    (requireAdministrator manifest pops UAC unattended — manual verify).
  - Handbook updated: §1 (single-file wording), §3 (new file/class map +
    split notes), §5 (A2 done), §6 (this entry), §8 (rewritten for Wave 3).
  - Committed as `refactor(split): decompose GpuModeSwitch.cs into seven
    module files, no behavior change` on `v1.1-logging-cleanup`. Not pushed
    (never push).

- **2026-09-06 — Wave 3 / A3 (Logging core rewrite) completed.**
  - Rewrote `src\Logger.cs`: the old `Logger` (single mirrored
    `GoTime.log`/`EcoMode.log`, rewritten fresh each run) is replaced by
    `static class Log` implementing the §8 contract exactly, plus
    `Snapshot()` for LogForm: `Info`, `Warn`, `Error(msg, ex = null)`,
    `Chan(channel, msg)`, `BeginSession(appName, version)`,
    `EndSession(result)`, `CurrentLogPath`, `LogsRoot`,
    `ListLogs(app)` (newest first), `Snapshot()`. Line format
    `yyyy-MM-dd HH:mm:ss.fff [LEVEL] (channel) message`; per-run files under
    `%LOCALAPPDATA%\GpuModeSwitch\logs\<GoTime|EcoMode>\<App>_yyyy-MM-dd_HHmmss.log`;
    session header (app+version+/define mode, registry CurrentBuild+UBR,
    WMI Win32_ComputerSystem model, WindowsPrincipal admin check, .NET
    runtime version) and EndSession footer (result + duration); dual sink
    (buffer + `File.AppendAllText`) under a single lock, file failures
    disable the file sink with a single WARN; retention after the header
    block (30 days, then 200 MB oldest-first by parsed file-name timestamp;
    never today's files nor the current log). Decisions recorded as D8.
  - Migrated **all 130 existing `Logger` call sites** (zero left; grep
    verified) with identical message text: `App.cs` 2 (Init → BeginSession
    + new EndSession on both exit paths), `AsusControl.cs` 44 (42 →
    `Chan("GPU", ...)` incl. one `Chan("POWER", ...)` for the Power Mode
    overlay line, 1 `CurrentLogPath` in DescribeState), `EnergySaver.cs` 39
    (`Chan("POWER", ...)`), `GamePrep.cs` 18 (`Info`), `Forms.cs` 27 (14
    TrayApps → `Chan("TRAY", ...)`, 6 UI lines → `Info`, 2 explicit error
    reports → `Error("...", ex)`, LogForm: 2 `Snapshot()` + 2
    `CurrentLogPath` — the only LogForm change; viewer, pre-selection, path
    strip and Copy log behave exactly as before).
  - **Build verified:** `cmd //c "src\build.cmd"` from repo root → exit
    code 0, zero csc diagnostics (csc is silent on success), output
    `Build OK:` + `Eco Mode.exe`, `Go Time.exe`. Artifacts:
    `dist\Go Time.exe` **99,328 bytes** (baseline 92,672 — +6.7 KB from the
    new Log core) and `dist\Eco Mode.exe` **92,160 bytes** (baseline
    86,016 — +6.1 KB). Exes not run (requireAdministrator manifest pops UAC
    unattended — manual verify).
  - **Runtime smoke test of `Log` itself** (out-of-tree harness compiled
    with csc against `src\Logger.cs` only — NOT the real exes, no UAC):
    confirmed per-run file creation under `%LOCALAPPDATA%\GpuModeSwitch\
    logs\GoTime\`, header content (Windows build 26200.8457 = CurrentBuild
    + UBR, machine model "ASUSTeK COMPUTER INC. ROG Strix G513QR_G513QR"
    via WMI, admin check, .NET 4.0.30319.42000), channel tags incl. an
    unknown channel, WARN level, exception continuation lines, EndSession
    footer, `ListLogs` newest-first, and both retention paths with
    synthetic files (248-day-old file removed `(age 248 days)`; 150 MB +
    100 MB files pushed the folder over 200 MB → oldest removed
    `(size cap 200 MB)`, newer 100 MB file and today's logs kept; legacy
    v1.0.x `GpuModeSwitch\GoTime.log`/`EcoMode.log` untouched). Test
    artifacts (harness + test logs folder) removed afterwards.
  - Handbook updated: §3 (as-of header, file/class rows, new **Log API**
    table, LogForm row), §4 (D8), §5 (A3 done), §6 (this entry), §8
    (rewritten for Wave 4).
  - Committed as `feat(logging): per-run timestamped log files with dual
    sink, session headers and retention` on `v1.1-logging-cleanup`. Not
    pushed (never push).

- **2026-09-06 — Wave 4 / A4 (Log window upgrade) completed.**
  - Upgraded `LogForm` in `src\Forms.cs` ONLY (MainForm's
    `new LogForm(appName)` contract unchanged). The viewer now shows the
    **live buffer (`Log.Snapshot()`) or any older per-run log**: history
    ComboBox (DropDownList) populated from `Log.ListLogs(appName)`
    newest-first with "Current session (live)" pinned on top and the
    current run's file tagged "(current)"; **Refresh** re-lists and
    reloads; selecting an older file loads it (`File.ReadAllText`,
    failure-tolerant with a `Log.Warn`); selecting the live/current entry
    returns to the live buffer. Severity filter combo (All/Info/Warn/Error)
    parses the `[LEVEL]` line prefix (space-leading lines are treated as
    exception continuations of the entry above them); a non-All filter
    shows matching entries only and the live view re-filters from a fresh
    snapshot on change. **Find next** searches the displayed text
    case-insensitively, wraps at the end, highlights via selection
    (HideSelection=false), Enter in the search box repeats. **Open folder**
    runs `Process.Start("explorer.exe", "/select,\"" + path + "\"")` for
    the viewed log. **Copy log** now copies what is displayed (filtered
    view or selected old file); a second **Copy all** button copies the
    unfiltered source (three autosize buttons use ~half of the 680 px row
    — no crowding). Preserved exactly: path strip (now tracks the viewed
    log), pre-selected text with focus on Shown (Ctrl+C works
    immediately), monospace read-only viewer, and the deterministic
    TableLayoutPanel shell (v1.0.19 lesson) — extended to 5 rows (path /
    history / filter+find / viewer / buttons), all new controls on
    AutoSize FlowLayoutPanel bars. UI actions logged via `Log.Info`:
    `log viewer: opened <filename>`, `filter=<label>`,
    `history refreshed (N saved log(s))`, `open folder <path>`,
    `find "..." - no match`, copy counts; unreadable file → `Log.Warn`.
  - **Build verified (out-of-tree harness per parallel-wave rules — repo
    `src\build.cmd` NOT run by the agent):** entire current `src\` copied
    to a temp dir; the same two csc invocations produced **zero csc
    diagnostics, exit code 0** for BOTH `/define:MODE_STANDARD` and
    `/define:MODE_ECO`. Temp dir deleted afterwards.

- **2026-09-06 — Wave 4 / A5 (Log browser) completed.**
  - New `src\LogBrowser.cs` — `LogBrowserForm` (dark theme inline colors
    like Forms.cs, sizable, min 760x520, title "Log History"): LEFT file
    list of every log from BOTH apps (`Log.LogsRoot` + GoTime / EcoMode,
    merged and sorted newest first; columns App, File, Size (KB), Last
    write), RIGHT read-only Consolas viewer in LogForm's style (files over
    ~2 MB load only their tail with a notice line; text pre-selected on
    load so Ctrl+C works immediately), toolbar with Find box + Search /
    Search all logs / Open folder / Copy view / Refresh, and a status/path
    strip.
  - Search: "Search" = case-insensitive find-next in the viewed file
    (wraps around); "Search all logs" = every matching line across all
    listed logs with file name + line number in a results pane (capped at
    500 with truncation notice); clicking a hit opens that file in the
    viewer and selects the hit line. Open folder uses
    `explorer.exe /select,"<file>"`; Copy view copies the viewer text.
    Files are opened with FileShare.ReadWrite so the live current log can
    be viewed while it is written; search-all runs on a ThreadPool thread
    using the RunBg/SafeInvoke pattern. Logs via `Log.Info`
    ("log browser: ...").
  - `LogBrowserForm.ShowBrowser(owner)` opens it non-modal and owned so it
    never orphans. Class declared `internal` to match every other form in
    the codebase; the static entry point is public. No other file touched
    — the Go Time menu entry is Wave 6/A18 as planned, so the form is not
    reachable from the UI yet (expected).
  - **Build verified out-of-tree per the parallel-wave rules:** the entire
    current `src\` copied to a temp dir outside the repo, compiled with
    the exact `build.cmd` csc commands — `/define:MODE_STANDARD` and
    `/define:MODE_ECO` each finished with **zero diagnostics** (exit 0).
    Temp dir deleted afterwards.

- **2026-09-06 — Wave 4 / A6 (Storage analyzer) completed.**
  - New `src\StorageAnalyzer.cs` (detection/measurement ONLY, strictly
    read-only): `public class CleanCategory` (`Name`, `Kind`
    `wu|dism|deepclean|appcache|gpu`, `Bytes`, `Files`, `Notes`,
    `RiskLabel`, `Selected` — Selected=true for all; the WU download cache
    and Delivery Optimization categories note that services must be
    stopped first, final call is the Wave 6 UI's) and
    `public static class StorageAnalyzer` with `MeasureAll()` (11 fixed
    categories + up to 7 per-path GPU shader-cache categories, added only
    when present), `CheckGates()` (elevation via WindowsPrincipal; CBS
    RebootPending/PackagesPending, WindowsUpdate Auto Update
    RebootRequired, Session Manager PendingFileRenameOperations, WinSxS
    pending.xml; WU services wuauserv/bits/UsoSvc/DoSvc/TrustedInstaller
    busy on Running/StartPending/StopPending — every check fails closed
    into a block reason) and `GatesSummaryText(reasons)`.
  - Measurement: recursive size/file-count walks; per-item failures
    swallowed into `Notes` (5 verbatim samples + "and N more"); missing
    paths → 0 bytes with a "not present" note; Windows temp applies the
    7-day rule at the top level; `CbsPersist_*.cab` older than 30 days;
    reparse points skipped. Long paths enumerated through kernel32
    `FindFirstFileW`/`GetFileAttributesExW` with the `\\?\` prefix — .NET
    4.x DirectoryInfo/FileInfo reject that prefix ("Illegal characters in
    path", caught in the live smoke run, fixed by the P/Invoke walk).
  - Logging: one `Log.Chan("CLEAN", "measure '<name>': <size> in <N> files
    [- notes]")` line per category plus the single `gates: N block
    reason(s)` / `gates: clear` line from `CheckGates()`.
  - **Verified out-of-tree:** entire `src\` copied to a temp dir, copied
    `build.cmd` run there — both `/define:MODE_STANDARD` and
    `/define:MODE_ECO` → zero csc diagnostics ("Build OK:" + both exes,
    exit 0). Read-only runtime smoke harness (Logger.cs +
    StorageAnalyzer.cs, no BeginSession, nothing written to disk)
    returned real measurements (e.g. NVIDIA DXCache 23.15 GB in 279
    files) and 3 correct gate reasons unelevated. Temp dirs deleted.

- **2026-09-06 — Wave 4 / A12 (Background process freezer) completed.**
  - New `src\ProcessFreezer.cs` (the only file A12 touched):
    `public static class ProcessFreezer` + `public struct FreezeResult`
    (`Suspended`, `Failed`, `SkippedGuarded`, `Summary`). P/Invoke ntdll
    `NtSuspendProcess` / `NtResumeProcess` (uint NTSTATUS) on handles from
    `Process.GetProcessesByName` / `GetProcessById`; every per-process
    step try/caught (a process can exit between listing and opening).
  - Freeze list: `%LOCALAPPDATA%\GpuModeSwitch\freezelist.txt`, one
    process name per line (no .exe); tolerant read (empty list on
    failure); `SeedDefaultListIfMissing()` writes the TrayApps candidate
    process names from Forms.cs (parsec, googledrivefs, googledrivesync,
    jellyfin, riotclient, vgtray, vgc), never overwrites;
    `FreezeSelected()` seeds first so a fresh machine still gets the
    defaults.
  - Safety boundary (stated in the class-header comment): static
    never-freeze guard list (system, smss, csrss, wininit, winlogon,
    services, lsass, dwm, explorer, audiodg, MsMpEng, SecurityHealthService,
    plus this suite's "Go Time"/"Eco Mode") + the app's own PID;
    blank/unreadable names fail closed; resume works ONLY on the PID+name
    pairs tracked this session, re-checking each PID's current name before
    resuming (PID-reuse refused and dropped); nothing is ever killed —
    suspend/resume only; a repeated `FreezeSelected()` skips
    already-tracked PIDs (never suspended twice); documented Wave 6
    limitation: processes started after the freeze pass are not frozen
    this session. Resume-failed entries stay tracked for retry;
    `ResumeAll()` on an empty session list is a clean zero no-op;
    `ResumeAllSafe()` never throws (Wave 6 crash/exit paths).
  - Logging: successes via `Log.Chan("FREEZE", ...)` ("suspended <name>
    (pid N)" / "resumed <name> (pid N)"), failures as `Log.Warn`.
  - **Build verified out-of-tree:** entire current `src\` folder copied to
    a temp dir outside the repo + this file added, compiled with
    build.cmd's exact csc commands — both define targets with zero
    diagnostics, `Build OK:` + both exes; temp dir deleted. No live
    processes touched; suspend/resume is exercised in Wave 6's manual
    verification.

- **2026-09-06 — Wave 4 / A13 (Power plans + WU pauser) completed.**
  - New `src\PowerPlans.cs` (namespace `GpuModeSwitch`, two public static
    classes; compiled into both exe targets, no #if):
    - `PowerPlans` — `SetUltimate()` / `RestorePrevious()`. Duplicates the
      hidden Ultimate Performance template (e9a42b02-d5df-448d-aa00-
      03f14749eb61) via `powercfg -duplicatescheme`, parses the new GUID
      from stdout (GUID regex), captures the active scheme as "previous"
      on the first successful run only (never overwritten on re-runs,
      never the Ultimate GUID itself), persists state BEFORE activating,
      activates with `powercfg /setactive`. Re-runs reuse the stored
      created-GUID when `powercfg /query <guid>` exits 0 (else
      re-duplicate) and skip the setactive when already active.
      `RestorePrevious()` restores the remembered plan, keeps the
      created-GUID, clears only the previous field, and is a logged no-op
      without state. State file
      `%LOCALAPPDATA%\GpuModeSwitch\powerplan.txt` (dir auto-created;
      line 1 created-GUID, line 2 previous-GUID) with an in-session memory
      mirror as read fallback. Helpers: `RunPowercfg` (UseShellExecute=
      false, CreateNoWindow=true, stdout-then-stderr capture, 30 s wait +
      kill), `ParseGuid`, `CurrentActiveGuid()`. Every failure →
      `Log.Error` with the raw powercfg output; nothing throws (bool
      returns).
    - `WuPause` — `PauseUpdates()` stops wuauserv, bits, DoSvc
      (ServiceController, Running/StartPending only, bounded
      WaitForStatus(Stopped, 15 s); "wu-pause: stopped <name>" / "was
      already stopped"; WARN on timeout/failure) and records per-service
      flags only when our Stop() was accepted; `ResumeUpdates()` restarts
      exactly those (bounded 15 s, "wu-pause: restarted <name>"), clears
      the flags first, and is a no-op ("wu-pause: nothing to resume") when
      nothing was recorded. Both methods fully try/caught (exit-path
      safe). **StartType is never changed** (documented in the file
      header).
  - All logging through the POWER channel: "power: ..." / "wu-pause: ...".
  - **Build verified out-of-tree per the parallel-wave rules** (repo
    `src\build.cmd` not run by the agent): full current `src\` copied to a
    temp dir outside the repo plus this new file, build.cmd's exact csc
    commands executed there — `/define:MODE_STANDARD` exit 0,
    `/define:MODE_ECO` exit 0, csc silent on both (zero diagnostics).
    Banned-syntax self-scan (`$"`, `?.`, `=>`, `nameof`, `??=`,
    `using static`): 0 hits. Temp dir deleted afterwards. Runtime
    round-trip (actual plan switch / service stop-start) deferred to
    Wave 6 manual verification.

- **2026-09-06 — Wave 4 / A14 (Named profiles) completed.**
  - New standalone `src\Profiles.cs` (references only `Log` +
    `System.Web.Extensions`; no Forms.cs types), three public types:
    - `Profile` — `Name` + `Selections` (`Dictionary<string,bool>`),
      parameterless ctor for the serializer.
    - `ProfileStore` (static) — JSON store at
      `%LOCALAPPDATA%\GpuModeSwitch\profiles.json` (array of
      `{Name, Selections}`) through the already-referenced
      JavaScriptSerializer; `LoadAll()` keeps file order; `SaveAll` full
      rewrite (serialize → `File.WriteAllText`, failures → `Log.Error` +
      false); `Save` upserts by exact name (replace in place, else append)
      and logs `[PROFILE] profile saved: <name> (N keys)`; `Delete` logs
      `[PROFILE] profile deleted: <name>`; `Find` exact match, always
      fresh; missing/corrupt file → empty list + one WARN; plus a
      `FilePath` property.
    - `ProfileBar : UserControl` — dark horizontal bar (Profile label,
      DropDownList combo, Apply / Save... / Delete / Refresh) with
      `ApplyRequested` + `Saved`
      (`Action<string, Dictionary<string,bool>>`) and `CollectSelections`
      (`Func<Dictionary<string,bool>>`, raised by Save for the host's
      current checkbox state). Apply reads the selected profile fresh from
      the store (no selection → warn, never fires); Save names it via the
      dark `ProfileNameDialog` modal (default = combo text or "New
      profile", empty name keeps it open); Delete confirms Yes/No;
      `Reload()` re-lists from the store. Every action logs through
      `Log.Chan("PROFILE", ...)`.
  - **Build verified out-of-tree** (parallel-wave rules; repo `build.cmd`
    NOT run): entire `src\` copied to a temp dir + `Profiles.cs`,
    build.cmd's two csc commands run per target — MODE_STANDARD exit 0 /
    zero diagnostics, MODE_ECO exit 0 / zero diagnostics. Temp harness
    deleted. File is pure ASCII — the Save button reads "Save..." with
    three ASCII dots (no BOM issues with csc).

- **2026-09-06 — Wave 4 / A15 (Session history) completed.**
  - New `src\SessionHistory.cs` (only file touched): `SessionRecord`
    (public fields, serializer round-trip ctor); `static SessionHistory` —
    append-only `%LOCALAPPDATA%\GpuModeSwitch\sessions.jsonl` (one
    JavaScriptSerializer object per line; Append never throws — failures
    go to `Log.Error`, success logs
    `Log.Chan("SESSION", "session recorded: <App> <Mode> - <Result>
    (N action(s), <space summary>)")` with a B/KB/MB/GB FormatBytes
    summary); `ReadAll` (corrupt lines skipped, WARNs capped at 3,
    timestamps normalized to UTC, newest first, missing file = empty
    list); `ExportText` (human-readable blocks: local timestamp, app,
    mode, result, duration, bulleted actions, space freed per category +
    total); public `FormatBytes(long)` helper. `SessionHistoryForm`
    ("Session History", dark LogForm/LogBrowser recipe — TableLayoutPanel
    shell over a horizontal SplitContainer, min 720x480): ListView (When
    local/App/Mode/Result/Freed/Errors, newest first) + Refresh, read-only
    detail pane re-rendering the selected record's ExportText (pre-selected
    on load so Ctrl+C works immediately), "Export .txt" (SaveFileDialog;
    selected record or all when none selected) and "Copy". Entry point
    `public static void ShowHistory(Form owner)` — non-modal, owned (plain
    Show if the owner is gone). Compiles into both exe targets; A18 wires
    the entry (Wave 6).
  - **Build verified (out-of-tree harness):** copied the entire current
    `src\` (incl. all parallel Wave 4 files) to a temp dir outside the
    repo, added `src\SessionHistory.cs`, compiled BOTH `/define` targets
    with build.cmd's csc commands — zero diagnostics per target (csc
    silent, exit 0 for MODE_STANDARD and MODE_ECO; the copied `build.cmd`
    printed `Build OK:` + both exes). Temp dir deleted; no runtime writes
    (no `sessions.jsonl` created this wave).

- **2026-09-06 — Wave 4 / A16 (Live system monitor) completed.**
  - Created `src\SystemMonitor.cs` (947 lines, new file only):
    - `MonitorSample` — public-field DTO (`CpuPercent`, `RamUsedBytes`/
      `RamTotalBytes`, `DiskActivePercent`, `GpuPercent`, `GpuTempC`,
      `CpuTempC`, `HasGpu`/`HasGpuTemp`/`HasCpuTemp`, `Timestamp`) plus
      `RamText` ("4.2 / 16.0 GB", invariant culture).
    - `MonitorEngine` (static) — System.Windows.Forms.Timer (default
      2000 ms, 250 ms floor) so every sample arrives on the UI thread for
      both the panel and the Wave 5 overlay (marshaling decision
      documented in the file header); `event Action<MonitorSample>
      SampleReady` per tick, `LastSample`/`Running`, `RunOnce()` for
      tests. CPU = "\Processor(_Total)\% Processor Time", Disk =
      "\PhysicalDisk(_Total)\% Disk Time" — created once, primed with one
      discarded NextValue, disposed on Stop, recreated + re-primed on
      re-Start. RAM via kernel32 GlobalMemoryStatusEx P/Invoke
      (MEMORYSTATUSEX in-file). GPU via nvidia-smi resolved once per
      session (C:\Windows\System32 → C:\Program Files\NVIDIA
      Corporation\NVSMI → PATH), run hidden with a 2.5 s timeout, first
      "util, temp" CSV line parsed (temp "N/A" handled); absent/failed →
      HasGpu=false, CPU LoadPercentage never faked into GPU. CPU temp via
      WMI root\WMI MSAcpi_ThermalZoneTemperature (tenths of Kelvin →
      Celsius, hottest plausible zone). Every metric individually
      try/caught, keeps last-known on failure; the two unavailability
      lines ("monitor: gpu metrics unavailable (no nvidia-smi)" /
      "monitor: cpu temp unavailable (MSAcpi_ThermalZoneTemperature)") log
      exactly once per session — never spam. Lifecycle logs
      "monitor: started (N ms)" / "monitor: stopped" through
      Log.Chan("MONITOR").
    - `MonitorPanel : UserControl` — dark theme (inline palette per the
      Theme.cs convention), deterministic TableLayoutPanel (LogForm
      pattern): 3 columns x 7 rows, Label + ProgressBar + value per metric
      ("N/A" rows bar at 0), "updated HH:mm:ss" footer, min 360x180,
      resizable. Bars render dark via uxtheme SetWindowTheme("","")
      classic mode, re-applied on HandleCreated. AttachToEngine/
      DetachFromEngine manage the SampleReady subscription (attach paints
      MonitorEngine.LastSample immediately); Dispose detaches; updates
      guard with InvokeRequired so a worker-thread RunOnce cannot cross
      threads.
  - **Verified out-of-tree** (parallel-wave rules; repo build.cmd
    untouched, nothing run in-repo by the agent): entire current `src\`
    copied to a temp dir and compiled with build.cmd's exact csc commands
    (csc 4.8.9221.0 for C# 5): `Build OK:` + both exes, exit 0, and both
    per-target csc runs exited 0 with zero output. First harness run
    (13 .cs files) and a re-run after SessionHistory.cs landed (14 .cs
    files) both passed. Both temp harness dirs deleted afterwards.
    Compile verification only — no counters exercised at runtime this
    wave.

- **2026-09-06 — Wave 4 integrated (orchestrator).**
  - Ran the integrated in-repo build for the first time with all 14 source
    files: `cmd //c "src\build.cmd"` → exit 0, zero csc diagnostics,
    `Build OK:` + `Eco Mode.exe` (177,152 bytes) and `Go Time.exe`
    (183,808 bytes). All eight Wave-4 files present in `git status`
    (`M src/Forms.cs`, 7 new files). Handbook §3/§5/§6 updated from the
    agents' paste-ready blocks; §8 rewritten for Wave 5.

- **2026-09-06 — Wave 5 / A7 (Tier 1 storage cleaner) completed.**
  - New `src\StorageCleaner.cs` (1,456 lines incl. header, only file
    touched): `public class CleanResult` (CategoryName/BytesFreed/
    FilesDeleted/FilesSkipped/Notes, `Summary` one-liner) +
    `public static class StorageCleaner.Clean(List<CleanCategory> selected,
    out results, out totalBytesFreed)`.
  - Gate re-check inside `Clean` (never trusts the caller): any
    `StorageAnalyzer.CheckGates()` reason → each logged as `Log.Error`,
    empty results, 0 bytes, false, nothing deleted. Final line
    `clean: total <X> freed; free space before <A> -> after <B> (delta <D>)`.
  - WU purge per D3: stop UsoSvc → wuauserv → bits (already-stopped
    tolerated, bounded WaitForStatus 20 s; WARN-and-continue for
    UsoSvc/bits; a wuauserv refusal aborts ONLY the WU category and
    restarts what we stopped); children of
    SoftwareDistribution\Download deleted (folder itself never touched);
    finally-block restart bits → wuauserv → UsoSvc limited to services
    this process stopped; re-detection via reflection COM ProgID
    `Microsoft.Update.AutoUpdate` → `DetectNow` (no `dynamic`, so
    build.cmd stays untouched; failure = WARN, non-fatal).
  - Delivery Optimization: fresh pre-measure walk, then
    `powershell -NoProfile -ExecutionPolicy Bypass -Command
    "Delete-DeliveryOptimizationCache -Force"` (never
    `-IncludePinnedFiles`), hidden, 120 s timeout + kill; success reports
    the pre-measured bytes as a noted estimate.
  - Windows temp >7 days (top-level age rule), user temp (all unlocked),
    WER ReportQueue/ReportArchive >7 days, CbsPersist_*.cab + WindowsUpdate
    logs >30 days, ReportingEvents.log (during the purge pass when
    selected, else standalone with skip+warn), MEMORY.DMP + Minidump,
    thumbcache_/iconcache_ files (Explorer locks → skip+log expected).
    gpu/appcache/dism categories come back zeroed noting their owner (D9).
  - D7 hard guard `IsForbiddenPath` re-checked before every deletion
    (WinSxS, catroot, catroot2, Installer, Servicing, pending.xml, and
    everything under SoftwareDistribution except Download children +
    ReportingEvents.log); hits log `GUARD: refusing <path>` and abort the
    item. Locked files → `clean: skipped <path> (in use)` WARNs tallied
    (5 verbatim samples + "and N more"); access-denied retried once with
    the read-only attribute cleared. Long-path deletes via kernel32
    FindFirstFileW/DeleteFileW/RemoveDirectoryW with `\\?\`; reparse
    points never followed; static run gate refuses concurrent `Clean()`;
    safe on a background thread.
  - **Build verified out-of-tree** (repo build.cmd not run by the agent):
    entire current `src\` (17 .cs incl. the parallel A8/A9 files) copied
    to a temp dir, build.cmd's exact csc commands per target — both exit 0,
    zero diagnostics, `Build OK:` + both exes. Banned-syntax scan clean.
    Compile verification only — `Clean` never executed (it deletes files);
    runtime exercise is Wave 6's manual verification.

- **2026-09-06 — Wave 5 / A8 (Component store, Tier 2) completed.**
  - New `src\ComponentStore.cs` (only file touched): `public class
    DismAnalysis` (CleanupRecommended, ActualSizeText, SizeText,
    ReclaimableShown, ExitCode, OutputText, ErrorText) +
    `public static class ComponentStore` with `Analyze()`,
    `CleanupRecommended()` and `RunCleanup(out string outputTail)`.
  - `Analyze()`: hidden `Dism.exe /Online /Cleanup-Image
    /AnalyzeComponentStore` (both streams pumped asynchronously so the
    20-minute hard timeout stays in charge; kill + Log.Error on timeout),
    full raw output preserved; case-insensitive parse of "Component Store
    Cleanup Recommended : Yes/No", "Actual Size of Component Store :",
    "Size of Component Store in WinSxS folder :", any "Reclaimable" line;
    localized-output parse miss → WARN with raw output kept.
  - `RunCleanup(out outputTail)`: D7 gate re-check first (fail closed),
    analyze-first ALWAYS (not recommended → "skipped (analyzer says not
    recommended)" + true), then `/StartComponentCleanup` — /ResetBase is
    FORBIDDEN (D3): the argument string exists only as the
    `CleanupArguments` const plus a defensive runtime guard that aborts if
    it ever contains /ResetBase; stdout streamed line-by-line through
    CLEAN (`dism: <line>`), collapsing DISM's backspace/spinner progress;
    45-minute cap (kill + Error); DISM error 1726 → WARN "known 24H2+
    issue, treated as retryable warning" + true; final `dism cleanup:
    exit N` + outputTail (last ~40 meaningful lines) for the UI.
  - One shared busy flag serializes Analyze and RunCleanup (never overlap
    each other or themselves; refusals logged); `public static bool
    IsBusy` for the UI. File + class headers state the D7 boundary: WinSxS
    is never touched except through DISM.
  - **Build verified out-of-tree** (repo build.cmd NOT run): entire
    current `src\` copied to a temp dir, build.cmd's exact csc commands
    for BOTH defines — csc silent (zero diagnostics), exit 0 per target,
    both exes produced. (First harness run failed only in the then-
    mid-write AppCacheCleaner.cs and passed after a wait + fresh re-copy.)
    Banned-syntax scan 0 hits. Temp dir deleted. Compile gate only — no
    DISM run this wave.

- **2026-09-06 — Wave 5 / A9 (App cache cleaner) completed.**
  - New `src\AppCacheCleaner.cs` (971 lines, only file touched): D5
    CACHE-ONLY per-app cache cleaner for Chrome, Edge, Brave, Opera,
    Vivaldi, Firefox, Steam, Discord, Epic Games Launcher and Battle.net.
    `AppCacheTarget` (Name / ProcessNames / resolved CacheDirs) +
    `AppCacheCleanResult` (Name / BytesFreed / FilesDeleted / FilesSkipped
    / Notes) + static `AppCacheCleaner` (`Targets()`, `RunningApps()`,
    read-only `Measure()`, `Clean(list, out totalBytesFreed)`,
    `IsForbiddenName()`).
  - D5 enforcement — two independent walls, stated in the class header:
    (1) the literal cache-name whitelist IS the enforcement — candidates
    only ever the listed names (Cache, Code Cache, GPUCache, DawnCache,
    GrShaderCache, ShaderCache, Media Cache, cache2, startupCache,
    htmlcache, webcache*, shadercache, depotcache) under the explicit
    per-app parents (profile-dir enumeration the only glob; Steam
    libraries from HKCU SteamPath + %ProgramFiles(x86)%\Steam +
    libraryfolders.vdf — VDF backslash escaping unescaped, caught by the
    smoke run; never a disk-wide scan; non-whitelisted names e.g. Battle.net
    "BrowserCache" refused with a WARN); (2) the forbidden-name wall
    (cookies, history, login data, sessions, bookmarks, local storage,
    indexeddb, web data, places.sqlite, cookies.sqlite, key3/key4.db,
    logins.json, formhistory.sqlite, sync data, ...) re-checked per
    candidate segment and per child name right before any enumeration or
    delete — even a future path-table bug cannot delete personal data.
  - Clean removes cache-dir CONTENTS only (dirs kept), per-item try/catch,
    locked files skipped + WARN, reparse points never followed/deleted, an
    app with any of its process names running is never cleaned (skip +
    WARN; a failing process scan fails closed), never throws; Measure logs
    `measure '<Name>': ...` / `not present (skipped)` on CLEAN, Clean logs
    `appcache '<Name>': ... freed, N files deleted, M skipped` + a total
    line; reuses CleanCategory (Kind "appcache"), Log and
    SessionHistory.FormatBytes.
  - **Build verified out-of-tree** (repo build.cmd NOT run): entire
    current `src\` copied to a temp dir + this file, both csc commands —
    MODE_STANDARD exit 0 / zero diagnostics, MODE_ECO exit 0 / zero
    diagnostics, `Build OK:` + both exes; file pure ASCII. **Read-only
    Measure smoke run** (no BeginSession, zero disk writes) returned real
    sizes (Edge 352.5 MB, Brave 1.26 GB, Steam 674.4 MB incl.
    D:\SteamLibrary shadercache, Discord 344.7 MB, Chrome 10.9 MB,
    Battle.net 67.3 MB) with correct running-app warnings
    (brave/steam/steamwebhelper/Discord) and forbidden-wall probes.
    Clean() compile-verified only — live exercise lands with Wave 6.

- **2026-09-06 — Wave 5 / A10 (Deep clean suite) completed.**
  - New `src\DeepClean.cs` (1,328 lines, only file touched): `public
    static class DeepClean` with `Measure()` + `Clean(selected, out
    results, out totalBytesFreed)` mirroring StorageCleaner's discipline
    (gate re-check fail-closed, static run gate, before/after free space,
    per-category + final `deepclean:` CLEAN lines, never throws). Owns
    three D9 categories: "Per-user error reports" (%LOCALAPPDATA% WER
    ReportQueue/ReportArchive, >7 days), "Setup & upgrade logs"
    (MoSetup/DISM/SIH files >30 days, folder structure kept; `C:\Windows`
    top-level `setupapi*.old`/`*.log.old` >30 days — the active
    setupapi.dev.log can never match; Panther TOP-LEVEL
    `setup*.log/.etl/.xml` >30 days except setupact.log/setuperr.log,
    subdirectories never touched) and "Previous Windows installations
    (report only)" (measure-only, Selected=false, no deletion implemented
    anywhere — D3 Tier 3). Foreign categories return a zeroed CleanResult
    with an "owned by <module>" note (D9). D7 hard guard = StorageCleaner's
    IsForbiddenPath set plus a Panther rule. Long paths via kernel32
    `\\?\` walkers; pure ASCII source.
  - **Build verified out-of-tree** (repo build.cmd NOT run): both defines
    zero diagnostics, `Build OK:` + both exes, exit 0. One first-pass
    error in DeepClean.cs only (missing MaxErrorSamples const) fixed and
    re-verified.
  - **Read-only Measure smoke run** (no BeginSession — verified nothing
    written to disk; Clean never invoked): per-user WER not present;
    1.1 MB aged setup log found = exactly `C:\Windows\Panther\setup.etl`
    (Panther's `setup.exe` directory and `setupinfo` correctly skipped);
    **Windows.old measured 549.88 GB in 81,745 files** (unelevated
    access-denied items swallowed into capped note samples, 41 junctions
    skipped), $WINDOWS.~WS 361.6 KB, $WINDOWS.~BT not present, category
    Selected=false as designed.

- **2026-09-06 — Wave 5 / A11 (GPU shader-cache tools + HAGS) completed.**
  - New `src\GpuTools.cs` (978 lines, only file touched, pure ASCII):
    `public static class GpuTools` with `Measure()`, `Clean(selected,
    includeDriverLeftovers, out results, out totalBytesFreed)`,
    `HagsStateText()`, `SetHags(bool)`, `RebootRequiredForHags()`. Owns
    the canonical GPU cache path table: NVIDIA DXCache/GLCache
    (%LOCALAPPDATA%) + ProgramData NV_Cache, AMD DxCache/Dx9Cache/GLCache,
    D3DSCache, plus the confirm-flagged driver leftovers C:\NVIDIA and
    ProgramData\NVIDIA Corporation\Downloader. The seven shader-cache
    category names deliberately match StorageAnalyzer.MeasureAll()'s so
    the Wave 6 UI sees one set of names (accepted duplication, documented
    in both headers).
  - Safety model: `Clean()` re-checks `CheckGates()` itself (fail closed);
    D7 hard guard before every deletion; cache-dir CONTENTS deleted
    recursively, directories kept; locked files skip + WARN (no
    running-process check needed for shader caches — Windows keeps
    deleted-in-use files until handles close, documented); access-denied
    gets one attribute-clear retry; junctions never followed. Driver
    leftovers cleaned only with the confirm flag (re-downloading a driver
    costs bandwidth — always measured so the UI can show the size).
  - HAGS: reads/writes HwSchMode (DWORD 2=On/1=Off) under
    HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers; the value is
    NEVER deleted (off writes 1); state text "On"/"Off"/"Windows default
    (Off)"/"Windows default"; takes effect after reboot;
    `RebootRequiredForHags()` returns the per-session flag set only by a
    successful `SetHags`.
  - **Build verified out-of-tree** (repo build.cmd NOT run): both defines
    zero diagnostics (MODE_STANDARD exit 0, MODE_ECO exit 0), `Build OK:`
    + both exes. **Read-only runtime smoke run**: measured NVIDIA DXCache
    23.15 GB in 279 files (byte-identical to A6's analyzer numbers — path
    tables cross-validated), GLCache 64 B, AMD DxCache 48.9 MB, D3DSCache
    192.4 MB; NV_Cache/Dx9Cache/GLCache/C:\NVIDIA/Downloader correctly
    "not present (skipped)"; HagsStateText "Windows default (Off)".

- **2026-09-06 — Wave 5 / A17 (Overlay + session tray) completed.**
  - New `src\Overlay.cs` + `src\TrayIcon.cs` (only files touched),
    standalone (only Theme.cs/`UiShapes`, SystemMonitor.cs
    `MonitorEngine`+`MonitorSample` and `Log`; no Forms.cs types):
    - `MonitorOverlayForm : Form` — frameless TopMost semi-transparent
      (Opacity 0.85, near-black, rounded corners via `UiShapes`,
      ShowInTaskbar=false, 260x120, `ShowWithoutActivation` so toggling
      never steals the game's focus); first show lands bottom-right of the
      primary working area. Deterministic TableLayoutPanel grid: CPU /
      RAM / Disk / GPU / GPU temp + "updated HH:mm:ss" footer, Consolas
      9f, N/A dimmed. `Attach()`/`Detach()` manage the
      `MonitorEngine.SampleReady` subscription (attach paints LastSample
      immediately; Dispose detaches); `Toggle()` flips Visible. "monitor
      off" hint while the engine is not sampling (attach + per-sample +
      2 s poll) — the overlay never starts/stops the engine (D6).
      Click-drag anywhere. Logs MONITOR: "overlay: attached/detached/
      shown/hidden/toggled".
    - `SessionTray : IDisposable` — session-only NotifyIcon (D4: lives
      only while the process runs; nothing on disk). Icon drawn in code
      (16x16 accent-green rounded square + white "G"; HICON freed via
      DestroyIcon; system-icon fallback). Dark ContextMenuStrip via a
      private ProfessionalColorTable: Open Go Time / Restore (Eco Mode) /
      Toggle overlay / Status (balloon) / Exit; double-click =
      openWindow. Five ctor-injected callbacks; null = disabled item;
      every invocation logged through TRAY and try/caught (Log.Error,
      never a crash). Show(tip) / Hide() / SetStatus() / idempotent
      Dispose. Compiled into both targets with no #if — inert until
      instantiated, only Go Time's Wave 6 code (A18) instantiates it.
  - **Build verified out-of-tree** (repo build.cmd NOT run): both defines
    csc exit 0 with zero diagnostics; copied build.cmd printed `Build OK:`
    + both exes, exit 0. Banned-syntax scan 0 hits. Temp dir deleted.
    Runtime behavior (balloons, drag) deferred to Wave 6 manual
    verification.

- **2026-09-06 — Wave 5 integrated (orchestrator).**
  - Ran the integrated in-repo build with all 20 source files:
    `cmd //c "src\build.cmd"` → exit 0, zero csc diagnostics, `Build OK:`
    + `Eco Mode.exe` (263,680 bytes) and `Go Time.exe` (270,336 bytes).
    All seven Wave-5 files present as untracked in `git status`. Handbook
    §3/§5/§6 updated from the agents' paste-ready blocks + D9 appended to
    §4; §8 rewritten for Wave 6.

---

## §7 Build & verify (exact commands)

From **any** clone of this repo on Windows 10/11 x64 (Git Bash shown; CMD
equivalent in parentheses):

```bash
# 1. Build both exes (clean compile expected; csc is silent on success)
cd "C:\Users\bd799\Documents\Projects\system optimization\asus-gpu-mode-switch"
cmd //c "src\\build.cmd"          # (CMD: cmd /c "src\build.cmd")

# 2. Confirm the artifacts
ls -la dist                       # expect: "Eco Mode.exe" and "Go Time.exe"

# 3. OPTIONAL read-only check — shows detected interface + GPU state, changes nothing.
#    CAUTION: the exes are requireAdministrator -> expect a UAC prompt and a
#    MessageBox you must click OK on. Do not automate this unattended.
"./dist/Go Time.exe" --status
```

Expected result: `csc` prints **nothing** (`/nologo`, 0 errors, 0 warnings),
the script prints `Build OK:` followed by `Eco Mode.exe` and `Go Time.exe`,
exit code 0. If csc prints errors: they are C# 5 violations or a bad
reference — fix the code, never change the compiler or add references outside
§2. On any machine without the Framework64 compiler, `build.cmd` falls back to
`Framework` (32-bit) automatically; UIAutomation DLLs come from the GAC.

`dist\` is gitignored — never commit build artifacts.

---

## §8 Next steps

1. **Waves 1–4 — DONE** (see §5/§6): bootstrap, decomposition, logging core
   (`Log` contract in §3 — all modules log through `Log.Info` / `Log.Warn` /
   `Log.Error` / `Log.Chan(channel, msg)`), and eight feature modules
   (log window, log browser, storage analyzer, process freezer, power plans +
   WU pauser, profiles, session history, monitor engine + panel). The
   integrated in-repo build of all 14 source files passed with zero
   diagnostics on both `/define` targets.

2. **Wave 5 — cleaners/tools: DONE** (see §5/§6). All six modules landed
   (StorageCleaner A7, ComponentStore A8, AppCacheCleaner A9, DeepClean A10,
   GpuTools A11, Overlay + TrayIcon A17). Cleanup category ownership is
   defined by D9; the integrated in-repo build of all 20 source files
   passed with zero diagnostics on both `/define` targets.

3. **Wave 6 — A18 (GO-flow integration, docs, final verification) — the
   last agent.** Wire the finished module APIs into `Forms.cs` + `App.cs`
   (read each module's §3 rows first; the modules are static classes with
   no UI of their own except `ProfileBar`, `MonitorPanel`,
   `LogBrowserForm.ShowBrowser`, `SessionHistoryForm.ShowHistory`):

   - **Selection stage additions (Go Time, `#if MODE_STANDARD`):**
     - **Performance** checkbox group: "Freeze background apps" (with a
       picker/editor over `ProcessFreezer.GetUserList()`/`SaveUserList()`,
       seeded via `SeedDefaultListIfMissing()`), "Ultimate Performance
       plan" (`PowerPlans.SetUltimate()`), "Pause Windows Update"
       (`WuPause.PauseUpdates()`).
     - **Storage cleanup** checkbox group: "Windows Update cache purge"
       (A7), "Component store cleanup (DISM)" (A8), "Deep clean" (A10),
       "GPU shader caches" (A11), per-app cache checkboxes (A9 — one per
       `AppCacheCleaner.Targets()` entry, with `RunningApps()` warnings).
       On stage open run `StorageAnalyzer.MeasureAll()` + `DeepClean.
       Measure()` + `GpuTools.Measure()` + `AppCacheCleaner.Measure()` in
       the background (RunBg) and show sizes next to each checkbox; run
       `StorageAnalyzer.CheckGates()` — any block reason disables the
       cleanup group with the reason shown.
     - `MonitorPanel` (A16) embedded section; `ProfileBar` (A14) wired to
       the checkbox groups (`CollectSelections` gathers every checkbox by
       key, `ApplyRequested` restores them).
   - **GO execution sequence:** GPU switch (existing) → freezer
     (`ProcessFreezer.FreezeSelected()`) → power plan → WU pause → cleanup
     in this order: `StorageCleaner.Clean(selected Tier-1 categories)` →
     `ComponentStore.RunCleanup` (only if the DISM checkbox is ticked) →
     `DeepClean.Clean(selected)` → `GpuTools.Clean(selected,
     includeDriverLeftovers=false)` → `AppCacheCleaner.Clean(selected app
     names)`. Every step logged; on completion
     `SessionHistory.Append(new SessionRecord {...})` with the actions
     applied and per-category `SpaceFreedByCategory` from the CleanResults.
   - **Result stage:** show the space-freed summary (`CleanResult.Summary`
     lines + total via `StorageCleaner.FormatBytes`), HAGS reboot note if
     used, and buttons: **Log History** (`LogBrowserForm.ShowBrowser(this)`),
     **Session History** (`SessionHistoryForm.ShowHistory(this)`), existing
     View log + Copy log.
   - **Session tray (D4):** after GO applies successfully instantiate
     `SessionTray(openWindow, applyEco, toggleOverlay, statusText,
     exitApp)` and `Show("Go Time session active")`; `applyEco` runs the
     Eco restoration path then hides the tray; overlay toggles a
     `MonitorOverlayForm` (Attach/Detach with the engine).
   - **Eco Mode path:** `ProcessFreezer.ResumeAllSafe()` +
     `PowerPlans.RestorePrevious()` + `WuPause.ResumeUpdates()` + tray
     hide/Dispose must ALL run on the Eco restore path (and on abnormal
     exit) — never leave a frozen process, a paused WU, or a foreign power
     plan behind.
   - **Version + README:** bump `Program.Version` to **1.1.0** in App.cs;
     README: version-history entry for v1.1.0, feature docs, the logging
     locations/retention section, the cleanup safety model (D3/D5/D7/D9 in
     brief), and a pointer to `docs\HANDBOOK.md`. Update §5 (A18 done) and
     §6 (final entry) here.
   - **Final verification:** integrated build both targets zero
     diagnostics; analyze-only dry run (measure + gates, NO deletion);
     retention prune check; log browser + filter + search; Copy log in
     both apps; tray/overlay manual checklist documented in
     BUILD_NOTES.md (UAC-gated manual steps).

Parallel-wave rule (as used in Waves 4–5): agents in the same wave own
disjoint files, verify out-of-tree in a temp-dir harness (never run the
in-repo `src\build.cmd` while other agents are mid-write), report
HANDBOOK-UPDATE blocks instead of editing the handbook, and never commit —
the orchestrator runs the integrated build, applies handbook updates, and
commits once per wave with a detailed message. A18 (Wave 6) is a single
agent and edits shared files (`Forms.cs`, `App.cs`) that no other agent
touches, so it may build in-repo and update the handbook directly.
