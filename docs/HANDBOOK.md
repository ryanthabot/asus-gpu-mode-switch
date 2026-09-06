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

## §3 Architecture map (as of Wave 3, A3 — v1.1.0 in progress)

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
| `Forms.cs` | `LogForm : Form` | Log viewer: read-only monospace box (pre-selected text), path strip, **Copy log** + Close buttons on a TableLayoutPanel shell (DPI-proof); reads `Log.Snapshot()` / `Log.CurrentLogPath` (A4 will upgrade it). | ctor `LogForm(string appName)` |
| `Forms.cs` | `TrayAppInfo` / `TrayApps` (static, `#if MODE_STANDARD`) | Known tray apps (Parsec, Google Drive, Jellyfin, Riot Client, Riot Vanguard); detection against running processes; close = stop matching watchdog services (registry scan) then graceful close → kill, 3 rounds. | `Known`, `Detect()`, `Close(TrayAppInfo)` |
| `Forms.cs` | `UiPhase` (enum) | Main-window phase machine states. | `Probe`, `Confirm`, `Applying`, `Result` |
| `Forms.cs` | `MainForm : Form` | Themed borderless resizable window (rounded corners, fade-in, edge drag/resize via WndProc), phase machine (`UiPhase`: Probe → Confirm/Select → Applying → Result), selection-stage checkboxes + tray picker (Standard), restart prompt, View log button. | ctor `MainForm(bool confirmMode, bool autoMode)` |

### Log API (`src\Logger.cs` — Wave 3; the logging core every module logs through)

| Member | Signature | Notes |
|---|---|---|
| `Info` | `void Info(string msg)` | INFO line, channel APP. |
| `Warn` | `void Warn(string msg)` | WARN line, channel APP. |
| `Error` | `void Error(string msg, Exception ex = null)` | ERROR line; exception type + message + stack appended on indented continuation lines. |
| `Chan` | `void Chan(string channel, string msg)` | Channel-tagged INFO line. Channels: `CLEAN`, `GPU`, `FREEZE`, `POWER`, `TRAY`, `MONITOR`, `PROFILE`, `SESSION` — unknown tags still accepted. Existing modules log as GPU = AsusControl, POWER = EnergySaver/Power Mode, TRAY = TrayApps; everything else APP. |
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

`LogBrowser.cs` (A5), `StorageAnalyzer.cs` (A6), `StorageCleaner.cs` (A7),
`ComponentStore.cs` (A8), `AppCacheCleaner.cs` (A9), `DeepClean.cs` (A10),
`GpuTools.cs` (A11), `ProcessFreezer.cs` (A12), `PowerPlans.cs` (A13),
`Profiles.cs` (A14), `SessionHistory.cs` (A15), `SystemMonitor.cs` (A16),
`Overlay.cs` + `TrayIcon.cs` (A17).

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
| Log window upgrade (per-run files, open-log-folder, richer view) | **A4** | not started |
| Log browser (`LogBrowser.cs`, browse/list/past logs) | **A5** | not started |
| Storage analyzer (`StorageAnalyzer.cs`, analyze-first reports) | **A6** | not started |
| Tier 1 safe cache purger (`StorageCleaner.cs`) | **A7** | not started |
| Component store analyze + StartComponentCleanup (`ComponentStore.cs`) | **A8** | not started |
| Per-app browser/app cache cleaner, CACHE-ONLY (`AppCacheCleaner.cs`) | **A9** | not started |
| Deep clean suite (`DeepClean.cs`) | **A10** | not started |
| GPU shader-cache tools (`GpuTools.cs`) | **A11** | not started |
| Background process freezer (`ProcessFreezer.cs`) | **A12** | not started |
| Ultimate Performance power plan switcher + Windows Update pauser (`PowerPlans.cs`) | **A13** | not started |
| Named profiles (`Profiles.cs`) | **A14** | not started |
| Session history (`SessionHistory.cs`) | **A15** | not started |
| Live system monitor (`SystemMonitor.cs`, panel tab) | **A16** | not started |
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

1. **Wave 3 — A3 (logging rewrite): DONE** (see §5/§6). `src\Logger.cs`
   now implements `static class Log` exactly per D1/D8; the full Log API
   table lives in §3. All later modules log through `Log.Info` / `Log.Warn` /
   `Log.Error` / `Log.Chan(channel, msg)` with the channels CLEAN, GPU,
   FREEZE, POWER, TRAY, MONITOR, PROFILE, SESSION; new log-adjacent UI reads
   `Log.Snapshot()` / `Log.CurrentLogPath` / `Log.LogsRoot` /
   `Log.ListLogs(app)` (newest first).

2. **Wave 4 — one feature per agent, in this order:**

   - **A4 — Log window upgrade (Forms.cs `LogForm` ONLY):** richer viewer
     over the existing `Log` API (`Snapshot()`, `CurrentLogPath`,
     `LogsRoot`) — per-run awareness, an **open log folder** action, richer
     view. No other Forms.cs changes; no new file.
   - **A5 — Log browser:** new `src\LogBrowser.cs`, `static
     ShowBrowser(Form owner)`; browse/list past runs via `Log.ListLogs(app)`
     (newest first) and open/view a selected log file.
   - **A6 — Storage analyzer:** new `src\StorageAnalyzer.cs`; class
     `CleanCategory { Name, Kind, Bytes, Files, Notes, RiskLabel, Selected }`
     plus `CheckGates()` (D7 gates: pending reboot / WU installing / not
     elevated). **Analyze-first, no deletion in this module.**
   - **A12 — Process freezer:** new `src\ProcessFreezer.cs`; ntdll
     `NtSuspendProcess` / `NtResumeProcess` P/Invoke; freeze set from
     `freezelist.txt`; a **never-freeze guard** for critical/system
     processes (and never this app's own process). Logs through the FREEZE
     channel.
   - **A13 — Power plans + WU pauser:** new `src\PowerPlans.cs`; Ultimate
     Performance plan switching (powrprof) and Windows Update pause
     (registry/Policy). Logs through the POWER channel.
   - **A14 — Named profiles:** new `src\Profiles.cs` + `ProfileBar`
     UserControl; persisted as `profiles.json` (parse with the already
     referenced `System.Web.Extensions` JavaScriptSerializer or a minimal
     reader).
   - **A15 — Session history:** new `src\SessionHistory.cs`; append-only
     `sessions.jsonl` next to the logs; `SessionHistoryForm` viewer. Logs
     through the SESSION channel.
   - **A16 — Live system monitor:** new `src\SystemMonitor.cs` +
     `MonitorPanel` UserControl; samples only while visible (D6). Logs
     through the MONITOR channel.

   After Wave 4 the remaining waves per §5: A7/A8/A9/A10/A11 cleaners/tools,
   then A17 tray/overlay, then A18 GO-flow integration. Each agent: build
   after your change (`cmd /c "src\build.cmd"` from repo root, both targets
   zero diagnostics), update §5/§6, append decisions to §4, commit with a
   conventional message, never push.
