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

## §3 Architecture map (as of Wave 4, A16 — v1.1.0 in progress)

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

### Future modules (added by later waves, one feature each)

`StorageCleaner.cs` (A7), `ComponentStore.cs` (A8), `AppCacheCleaner.cs` (A9),
`DeepClean.cs` (A10), `GpuTools.cs` (A11), `Overlay.cs` + `TrayIcon.cs` (A17).

(Landed in Wave 4: `LogBrowser.cs` A5, `StorageAnalyzer.cs` A6,
`ProcessFreezer.cs` A12, `PowerPlans.cs` A13, `Profiles.cs` A14,
`SessionHistory.cs` A15, `SystemMonitor.cs` A16.)

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
| Tier 1 safe cache purger (`StorageCleaner.cs`) | **A7** | not started |
| Component store analyze + StartComponentCleanup (`ComponentStore.cs`) | **A8** | not started |
| Per-app browser/app cache cleaner, CACHE-ONLY (`AppCacheCleaner.cs`) | **A9** | not started |
| Deep clean suite (`DeepClean.cs`) | **A10** | not started |
| GPU shader-cache tools (`GpuTools.cs`) | **A11** | not started |
| Background process freezer (`ProcessFreezer.cs`) | **A12** | **done** (Wave 4) |
| Ultimate Performance power plan switcher + Windows Update pauser (`PowerPlans.cs`) | **A13** | **done** (Wave 4) |
| Named profiles (`Profiles.cs`) | **A14** | **done** (Wave 4) |
| Session history (`SessionHistory.cs`) | **A15** | **done** (Wave 4) |
| Live system monitor (`SystemMonitor.cs`, panel tab) | **A16** | **done** (Wave 4) |
| Session-only tray menu + overlay (`TrayIcon.cs`, `Overlay.cs`) | **A17** | not started |
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

2. **Wave 5 — cleaners/tools, one feature per agent (parallelizable; each
   owns exactly one new file):**

   - **A7 — Tier 1 cleaner:** new `src\StorageCleaner.cs`. Input: selected
     `CleanCategory` list (from `StorageAnalyzer.MeasureAll()`). WU purge:
     record service states → stop `usosvc→wuauserv→bits` → delete
     **children** of `SoftwareDistribution\Download` (never the folder) →
     restart `bits→wuauserv→usosvc` → `DetectNow()` via ProgID
     `Microsoft.Update.AutoUpdate`. DO cache via
     `powershell -NoProfile -Command "Delete-DeliveryOptimizationCache
     -Force"` (never `-IncludePinnedFiles`). Temp/WER/CBS/log deletions;
     skip+log locked files; tally bytes freed per category + before/after
     `DriveInfo` free space. HARD GUARD (D7): never touch WinSxS contents,
     catroot, catroot2, `C:\Windows\Installer`, Servicing, pending.xml.
     Refuse to run when `StorageAnalyzer.CheckGates()` returns reasons.
     Logs through CLEAN.
   - **A8 — Component store (Tier 2):** new `src\ComponentStore.cs`.
     `Analyze()`: run `Dism.exe /Online /Cleanup-Image /AnalyzeComponentStore`
     hidden, parse "Component Store Cleanup Recommended" + sizes.
     `RunCleanup()`: `/StartComponentCleanup` ONLY — `/ResetBase` is
     explicitly FORBIDDEN (D3). Stream progress to CLEAN; error 1726 =
     retryable warning; never run when gates fail.
   - **A9 — App cache cleaner (CACHE-ONLY, D5):** new
     `src\AppCacheCleaner.cs`. Targets: Chrome/Edge/Firefox/Brave/Opera/
     Vivaldi (%LOCALAPPDATA% cache dirs), Steam (appcache/shadercache),
     Discord (Cache, Code Cache), Epic (webcache), Battle.net (Cache) —
     name, paths, process names each. `Measure()` / `Clean(selected)`;
     running-process warn list. HARD EXCLUSION (enforced): only cache-named
     dirs are ever enumerated/deleted — Cookies, History, Login Data,
     Sessions, Bookmarks, Local Storage, places.sqlite, cookies.sqlite are
     untouchable. Logs through CLEAN.
   - **A10 — Deep clean:** new `src\DeepClean.cs`. `%TEMP%` (unlocked),
     `C:\Windows\Temp` (>7d), MEMORY.DMP + Minidump, WER trees,
     thumbnail/icon caches (Explorer locks — skip+log), old setup/upgrade
     logs. Reuse `CleanCategory`; shader caches NOT here (A11 owns). Logs
     through CLEAN.
   - **A11 — GPU tools:** new `src\GpuTools.cs`. Shader caches: NVIDIA
     DXCache/GLCache, ProgramData NV_Cache, AMD DxCache/Dx9Cache/GLCache,
     D3DSCache — measure + clean (skip locked). Driver leftovers:
     `C:\NVIDIA`, `ProgramData\NVIDIA Corporation\Downloader` — measure +
     clean with confirm flag. HAGS: read/write
     `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode`
     (2=on/1=off), current state + reboot note. Logs through GPU.
   - **A17 — Overlay + session tray:** new `src\Overlay.cs` +
     `src\TrayIcon.cs`. Overlay: frameless TopMost semi-transparent
     ~260×120 form bound to `MonitorEngine.SampleReady`/`LastSample`,
     click-drag movable, Show/Toggle/Close. Tray: session-only `NotifyIcon`
     shown after GO applies — menu: Open window / Eco Mode (restore) /
     Overlay toggle / Status balloon / Exit; Hide on restore/exit; icon
     drawn in code. Logs through TRAY/MONITOR.

3. **Wave 6 — A18 (GO-flow integration, docs, final verification):** wire
   all module APIs into `Forms.cs` + `App.cs` (read each module's §3 rows
   first). Selection stage adds: **Performance** checkboxes (freeze apps w/
   `ProcessFreezer.GetUserList()` picker, Ultimate plan via `PowerPlans`,
   pause WU via `WuPause`); **Storage cleanup** group (Tier 1 purge,
   component store, deep clean, per-app cache checkboxes) populated with
   `StorageAnalyzer.MeasureAll()` sizes on stage open — `CheckGates()`
   checked, blocked items disabled with reason; `MonitorPanel` section;
   `ProfileBar`. GO executes freezer → plan → WU pause → cleanup, logs +
   `SessionHistory.Append`; result stage shows space-freed summary + buttons
   Log History (`LogBrowserForm.ShowBrowser`) / Session History
   (`SessionHistoryForm.ShowHistory`) / View log / Copy log. Eco Mode:
   `ProcessFreezer.ResumeAll()` + `PowerPlans.RestorePrevious()` +
   `WuPause.ResumeUpdates()` + tray hide. Version 1.1.0 in App.cs; README:
   version-history entry + feature docs + logging/retention section +
   cleanup safety model + pointer to this handbook. Final verify: build both
   exes, analyze-only dry run, retention prune check, log browser + filter +
   search, Copy log in both apps, Eco round-trip.

Parallel-wave rule (as used in Wave 4): agents in the same wave own
disjoint files, verify out-of-tree in a temp-dir harness (never run the
in-repo `src\build.cmd` while other agents are mid-write), report
HANDBOOK-UPDATE blocks instead of editing the handbook, and never commit —
the orchestrator runs the integrated build, applies handbook updates, and
commits once per wave with a detailed message.
