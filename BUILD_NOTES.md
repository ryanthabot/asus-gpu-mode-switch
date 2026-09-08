# BUILD_NOTES.md — baseline verification (Wave 1 / Agent A1)

- **Date:** 2026-09-06
- **Baseline version:** v1.0.22 (git tag-equivalent commit `4260aef` on `main`: "v1.0.22: launch-time selection stage in Go Time (system optimizations + tray apps, GO to apply)")
- **Branch for v1.1 work:** `v1.1-logging-cleanup` (created from `main` @ `4260aef`)
- **Working tree after clone:** clean, `main` up to date with `origin/main`

## Compiler used (exact path)

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

Compiler banner (verified by running `csc.exe /help`):

```
Microsoft (R) Visual C# Compiler version 4.8.9221.0
for C# 5
Copyright (C) Microsoft Corporation. All rights reserved.
```

This is the constraint in one line: **the compiler is "for C# 5"** — no C# 6+ syntax
(string interpolation `$""`, `?.`/`??=`, expression-bodied members, `nameof`,
out-var, etc.) will compile. `src\build.cmd` falls back to
`%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe` only if the Framework64
copy is missing (it is not, on this machine).

## Baseline build output summary

Command (from repo root, Git Bash):

```
cmd //c "src\\build.cmd"
```

Full captured output:

```
Build OK:
Eco Mode.exe
Go Time.exe
```

- Exit code: **0**
- Compiler lines: **none** — `/nologo` + a completely clean compile (0 errors, 0
  warnings) for both `/define:MODE_STANDARD` (Go Time) and `/define:MODE_ECO`
  (Eco Mode). The only output is the script's own `Build OK:` listing.
- UI Automation assemblies were resolved from the GAC
  (`%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient` and
  `...\UIAutomationTypes`) and WindowsBase from
  `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll`, exactly
  as scripted.

### Artifacts produced (gitignored `dist/`)

| File | Size (bytes) | Notes |
|---|---|---|
| `dist\Go Time.exe` | 92,672 | `/define:MODE_STANDARD`, icon `src\gotime.ico`, resource `gotime-256.png` |
| `dist\Eco Mode.exe` | 86,016 | `/define:MODE_ECO`, icon `src\ecomode.ico`, resource `ecomode-256.png` |

Both are `anycpu` `winexe` builds with `src\app.manifest` as the Win32 manifest
(`requireAdministrator`, PerMonitorV2 DPI aware).

The optional read-only `--status` run was **not** executed during bootstrap:
the exes carry a `requireAdministrator` manifest, so launching one from this
session pops an elevation prompt plus a modal MessageBox. Left as a manual
verify step (see HANDBOOK §7).

## Repo file inventory (at baseline v1.0.22)

```
asus-gpu-mode-switch/
├── .gitignore                 (dist/, .vs/, *.user, icons-preview/)
├── LICENSE                    (MIT)
├── README.md                  (13,592 bytes; full feature/changelog history v1.0.1 -> v1.0.22)
├── src/
│   ├── GpuModeSwitch.cs       (115,422 bytes, 2,678 lines — the ONLY source file)
│   ├── app.manifest           (1,132 bytes; requireAdministrator + DPI awareness)
│   ├── build.cmd              (2,567 bytes; two csc.exe invocations)
│   ├── gotime.ico             (20,827 bytes, multi-size 16/32/48/256)
│   ├── ecomode.ico            (21,447 bytes, multi-size 16/32/48/256)
│   ├── gotime-256.png         (5,725 bytes; embedded as GpuModeSwitch.appicon.png)
│   ├── ecomode-256.png        (6,345 bytes; embedded as GpuModeSwitch.appicon.png)
│   ├── nvidia-eye.svg         (896 bytes; glyph source for icon generation)
│   ├── make-icons.ps1         (7,387 bytes; icon artwork generator, not part of build)
│   └── pack-ico.ps1           (5,537 bytes; packs PNGs into multi-size .ico, not part of build)
└── dist/                      (gitignored build output)
    ├── Go Time.exe
    └── Eco Mode.exe
```

## Notes for the next agents

- Everything ships from **one** C# file, `src\GpuModeSwitch.cs` (namespace
  `GpuModeSwitch`), split at compile time by the `MODE_STANDARD` / `MODE_ECO`
  `/define` symbols. Any new feature must compile under **both** defines or be
  wrapped in `#if MODE_STANDARD` / `#if MODE_ECO`.
- Current logging (`internal static class Logger`) is a StringBuilder mirror +
  `File.AppendAllText` to a single per-app file
  (`%LOCALAPPDATA%\GpuModeSwitch\GoTime.log` / `EcoMode.log`, rewritten fresh
  each run). Wave 3 (A3) replaces this with the per-run timestamped `Log`
  contract in `docs\HANDBOOK.md` §8.
- The v1.1 plan of record (features, safety gates, design decisions) lives in
  `docs\HANDBOOK.md`. **Read it before touching any source file**, and append
  to its §4/§6 rather than rewriting.

---

# Wave 6 verification (A18 — GO-flow integration, final agent)

- **Date:** 2026-09-06
- **Commit:** `7841c88` `feat(go-flow): wire Wave 4-6 modules into the apps (selection stage v1.1, GO session chain, session tray, eco-safe restore)` on `v1.1-logging-cleanup` (not pushed — never push)
- **Files changed:** `src\Forms.cs` (+1,314/−63 lines incl. new `SessionSafety`, `FreezeListEditorForm`, selection-stage v1.1, GO session chain, session tray/overlay, result-stage buttons), `src\App.cs` (Version 1.1.0, unhandled-exception hooks)

## Integrated build (both targets, zero diagnostics)

```
cmd //c "src\build.cmd"        (from repo root)
Build OK:
Eco Mode.exe
Go Time.exe
EXIT=0
```

csc (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, banner 4.8.9221.0
"for C# 5") printed **nothing** — zero errors, zero warnings — for BOTH
`/define:MODE_STANDARD` and `/define:MODE_ECO`.

| Artifact | Size (bytes) | previous wave |
|---|---|---|
| `dist\Go Time.exe` | 292,864 | 270,336 |
| `dist\Eco Mode.exe` | 265,216 | 263,680 |

## Static checks

- C# 5 banned-syntax scan over all of `src\`: `$"` interpolation — **0 hits**;
  `?.` null-conditional — **0 hits**; `nameof` — **0 hits**; `=>` (lambda OR
  expression-bodied) — **0 hits**. (`grep -c` per file, no non-zero counts.)
- Define split proven in the binaries by UTF-16 string scan (PowerShell
  `[Text.Encoding]::Unicode.GetString`, both byte alignments):
  "Freeze background apps", "Ultimate Performance plan", "Pause Windows
  Update", "Windows Update cache purge", "Component store cleanup (DISM)",
  "Deep clean", "GPU shader caches", "Edit list...", "Cleanup unavailable:",
  "report only - never deleted", "session tray created", "profile applied" —
  all present **only** in `Go Time.exe`. ("Ultimate Performance plan",
  "Log History", "Session History", "Profile:" also decode in Eco Mode.exe —
  expected: those live in PowerPlans/LogBrowser/SessionHistory/Profiles
  strings that compile into both targets.)

## Reachability wiring points (code inspection, `src\Forms.cs` line refs)

| Feature | Wiring point |
|---|---|
| View log (LogForm, Copy log inside) | `MainForm` ctor `_logBtn.Click` → `new LogForm("Go Time").ShowDialog(this)` (Forms.cs ~1114) |
| Log History browser | `_histBtn.Click` → `LogBrowserForm.ShowBrowser(this)` (Forms.cs ~1127) |
| Session History viewer | `_sessBtn.Click` → `SessionHistoryForm.ShowHistory(this)` (Forms.cs ~1130) |
| ProfileBar | ctor `_profileBar.CollectSelections += CollectAllSelections` / `ApplyRequested += ApplyProfileSelections` (Forms.cs ~1253); shown in the select panel top row |
| MonitorPanel | select panel; `AttachToEngine` at select show + monitor toggle (Forms.cs ~1339/1640); `MonitorEngine.Start(2000)` in `EnsureMonitorEngine` (~2348) |
| SessionTray | `AfterGoSuccess()` → `new SessionTray(...)` + `Show("Go Time session active")` (Forms.cs ~2335) |
| MonitorOverlayForm | `ToggleOverlay()` → `new MonitorOverlayForm()` + `Attach()` + `Show()` (Forms.cs ~2403) |
| GO session chain | `RunGoSession()` — `ProcessFreezer.FreezeSelected` (~2077), `PowerPlans.SetUltimate` (~2099), `WuPause.PauseUpdates` (~2127), `StorageCleaner.Clean` (~2176), `ComponentStore.RunCleanup` (~2195), `DeepClean.Clean` (~2233), `GpuTools.Clean(meas, false, ...)` (~2255), `AppCacheCleaner.Clean` (~2274); `SessionHistory.Append` (~2031) |
| Eco-safe restore | `SessionSafety.RestoreAll()` — Eco apply (Forms.cs ~1999), tray restore (~2359), tray exit (~2388), `RunBg` catch (~2561), `FormClosed` (~1301), `App.cs` ThreadException/UnhandledException (~165/170) |
| Freeze list editor | `_editFreezeList.Click` → `FreezeListEditorForm.ShowDialog(this)` (seed + GetUserList/SaveUserList + IsGuarded feedback) |

## Analyze-only dry run (out-of-tree harness — NO deletion calls)

Harness: temp dir outside the repo, `DryRun.cs` calling only
`StorageAnalyzer.MeasureAll/CheckGates`, `DeepClean.Measure`,
`GpuTools.Measure`, `AppCacheCleaner.Measure/Targets` — **no `Clean()` call
exists in the harness** (grep-verifiable), never calls `Log.BeginSession` so
it writes nothing to disk (verified: no log files created). Compiled with the
same framework csc; ran unelevated (no UAC). Results (2026-09-06, this
machine, unelevated):

```
StorageAnalyzer.MeasureAll(): 13 categories
  [wu]  Windows Update download cache      16.2 MB / 4 files
  [wu]  Delivery Optimization cache        0 B (not present)
  [deepclean] Windows temp (>7 days)       0 B (C:\Windows\Temp not enumerated unelevated)
  [deepclean] Windows error reports        0 B
  [wu]  Old update log archives            19.8 MB / 67 files
  [wu]  Update reporting log               682.7 KB / 1 file
  [deepclean] User temp files              522.3 MB / 702 files
  [deepclean] Crash dumps                  0 B (Minidump not enumerated unelevated)
  [deepclean] Thumbnail caches             214.8 MB / 30 files
  [gpu] NVIDIA shader cache (DXCache)      23.15 GB / 279 files
  [gpu] NVIDIA shader cache (GLCache)      64 B / 2 files
  [gpu] AMD shader cache (DxCache)         48.9 MB / 669 files
  [gpu] DirectX shader cache (D3DSCache)   192.4 MB / 232 files

DeepClean.Measure(): 3 categories
  Per-user error reports                   0 B (not present)
  Setup & upgrade logs                     1.1 MB / 1 file (Panther setup.etl)
  Previous Windows installations (REPORT ONLY) 549.88 GB / 81,752 files
    (Windows.old 549.88 GB + $WINDOWS.~WS 361.6 KB; $WINDOWS.~BT not present;
     never deleted anywhere in the suite - D3 Tier 3)

GpuTools.Measure(): 4 categories (identical sizes to the analyzer - paths cross-validated)
AppCacheCleaner.Measure(): 6 present targets
  Chrome 10.9 MB, Edge 352.5 MB, Brave 1.27 GB (app running), Steam 658.9 MB
  (app running), Discord 346.4 MB (app running), Battle.net 67.3 MB
AppCacheCleaner.Targets(): 10 targets, 6 with resolved cache dirs
  (Vivaldi, Opera, Firefox, Epic Games Launcher resolve empty -> no checkbox)

StorageAnalyzer.CheckGates(): 3 block reasons (unelevated harness, correct):
  - not running elevated
  - PendingFileRenameOperations pending (reboot needed)
  - WU service UsoSvc Running
```

Temp harness dir deleted afterwards. Expect the numbers above to change as
the machine is used; the elevated app will also enumerate `C:\Windows\Temp`
and `Minidump` (harness could not, unelevated).

## MANUAL VERIFICATION CHECKLIST (UAC-gated — cannot be automated)

The exes carry `requireAdministrator`, so each step below pops a UAC prompt
and must be done interactively by a human:

1. [ ] `Go Time.exe --status` — MessageBox shows the detected transport,
       dGPU/MUX state; no switch happens; a new log file appears under
       `%LOCALAPPDATA%\GpuModeSwitch\logs\GoTime\`.
2. [ ] Launch `Go Time.exe` — selection stage shows: Profile bar on top,
       precheck text, System optimizations, Tray apps detected (Scanning... →
       running states), Performance group (Freeze/Plan/WU-pause + Edit
       list...), Storage cleanup group with measured sizes on the captions,
       "(app running)" suffixes, Windows.old "report only" note, gate
       warning + disabled cleanup boxes if any gate blocks, collapsible
       system monitor at the bottom (expand → live bars).
3. [ ] Profiles: Save... a profile, change checkboxes, Apply it — every
       checkbox round-trips; Delete/Refresh work; unknown keys (hand-edit
       profiles.json) are ignored with a log line.
4. [ ] Freeze list editor: open "Edit list...", add "notepad.exe" (becomes
       "notepad"), add "explorer" → refused with the protected-names
       message; Save → freezelist.txt updated; reopen shows the saved list.
5. [ ] Real GO run with "Freeze background apps" + one cleanup box ticked:
       GPU switches, detail shows freeze/plan/WU one-liners + cleanup
       summary block, per-category freed lines and the total; session tray
       appears ("Go Time session active"); Session History shows the record
       with freed bytes; Log History opens the browser; Copy log works.
6. [ ] Tray menu: double-click opens the window; Status shows the balloon
       with dGPU state + CPU/RAM; Toggle overlay shows the draggable
       overlay (drag it, verify live updates); Restore runs the eco-safe
       restore (frozen apps resume, power plan back, WU resumed, tray
       disappears); Exit closes the app cleanly.
7. [ ] Eco round-trip: run `Eco Mode.exe` after a Go Time session — the
       switch applies AND the eco-safe restore runs (log line
       "restore: eco-safe session restore done"; no frozen process, no
       foreign power plan, wuauserv/bits/DoSvc back Running).
8. [ ] Abnormal-exit safety: kill `Go Time.exe` from Task Manager
       mid-session (after freezing) → the next Eco Mode apply (or any Go
       Time run/exit) resumes the frozen apps and restores plan/WU.
9. [ ] Log retention: drop a 150 MB + a 100 MB fake log older than today
       into the app's log folder, start the app → oldest pruned with
       "retention: removed ... (size cap 200 MB)" lines; today's files and
       the current log survive.
10. [ ] Log browser: search a term across all logs; click a hit → opens
       that file at the line; Open folder selects the file in Explorer.
       Filter + find-next + Copy log / Copy all inside View log.

---

# v1.1.1 verification (fix release — built and verified 2026-09-07)

Machine: dev PC, Windows build 26200 (same build family as the target
G513QR but NOT ASUS hardware) — build, static and probe verification only;
the interactive paths remain manual checklist items (11–13 added below).

## Scope (commits e46618e / 9c0f0cf / 0ac721b)

- `fix(select)` — the selection-stage panel is made visible in `EnterSelect`
  (Forms.cs); created hidden in the ctor, it was never shown, so GO was
  unreachable and every GO-gated feature was dead.
- `fix(energy-saver)` — silent-first `Sync`: corrected ESBATTTHRESHOLD GUID,
  AC+DC threshold write with read-back verification, and the Settings UIA
  fallback reworked (no `mouse_event`, own minimized window only).
- `docs(release)` — CHANGELOG.md, PORTABILITY.md, README/HANDBOOK/HANDOFF.

## Integrated build (both targets, zero diagnostics)

`cmd /c "src\build.cmd"` from the repo root, run twice during the fix
(after each code change) — csc is the Windows-shipped
`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
(4.8.9221.0 for C# 5), silent on success, exit 0, `Build OK:` + both exes.

| Artifact | v1.1.0 | v1.1.1 |
|---|---|---|
| `dist\Go Time.exe` | 292,864 | **293,376** |
| `dist\Eco Mode.exe` | 265,216 | **265,216** (net IL change absorbed by section alignment — the mouse_event removal offsets the additions; content verified by the string scan below) |

## Static checks

- Banned C# 6 syntax scan over the three changed files (`$"`, `?.`,
  `nameof(`, `=>`, `??=`, `using static`): **0 hits**.
- Repo-wide search for removed members (`ClickCenter`, `mouse_event`,
  `FindSettingsWindow`): no dangling references.
- UTF-16 string scan of BOTH built exes: the new Energy Saver log strings
  ("no new Settings window appeared", "read-back AC=") and the version
  string "1.1.1" are present in Go Time.exe AND Eco Mode.exe — the fix is
  compiled into both targets.

## Energy Saver silent path — probe evidence (load-bearing verification)

Out-of-tree harness (temp dir + framework csc, deleted afterwards), run on
AC power and STRICTLY no-op (the harness wrote back the identical value it
read; this machine's charge level remains 30%):

```
PowerGetActiveScheme rc=0  scheme={1a993c44-...}
OLD code GUID E69653CA-CF6F-4166-B25A-4D6A2C1B4E7F  DC read rc=2  (ERROR_FILE_NOT_FOUND)
REGISTRY GUID  E69653CA-CF7F-4F05-AA73-CB833FA90AD4  DC read rc=0  value=30
REGISTRY GUID                                     AC read rc=0  value=0
REGISTRY GUID write-back(same value)              DC write rc=0
verify DC=30 (unchanged - no-op verified)
```

Corroboration: `sc qc whesvc` → "Windows Health and Optimized Experiences"
(unrelated to Energy Saver — the v1.1.0 "ES moved to whesvc" note was
wrong); `powercfg /aliases` exposes no energy-related alias on this build
and the subgroup is queryable only through the PowerSettings registry hive.
Conclusion: the silent-first `Sync` order is backed by direct evidence on
build 26200; the UIA fallback keeps its own read-back verification.

## MANUAL VERIFICATION CHECKLIST — v1.1.1 additions (UAC-gated, owner, on the G513QR)

11. [ ] Go Time selection stage SHOWS every group at launch (profile bar,
       optimizations, tray apps, performance, cleanup with measured sizes,
       monitor) — the v1.1.0 empty window is gone.
12. [ ] Eco Mode run: NO Settings window appears at any point; the log's
       POWER channel shows the silent path (charge level 100%, write
       rc=0, read-back verified) and Windows reports Energy Saver on.
13. [ ] Go Time GO run: the same silent POWER lines at 0%; Energy Saver
       reports off afterwards.

(Items 1–10 above still apply; 2, 5, 6 and 7 are exactly the paths the
v1.1.1 fixes unblock.)

---

# v1.2.0 verification (the single app + redesign — built 2026-09-07)

Machine: dev PC, Windows build 26200 (an ASUS desktop board — ATKACPI
opens but rejects the GPU device IDs, i.e. the probe-failure path, which
is what the visual verification exercised).

## Scope

One commit series: `feat(unified)` (single app, D10) + `fix(gameprep)` +
`docs(release)`. 7 files changed (Forms.cs rebuilt, Theme.cs Ui-kit,
App.cs, build.cmd, Logger.cs, LogBrowser.cs, GamePrep.cs, TrayIcon.cs,
AsusControl.cs, plus docs).

## Integrated build (single target, zero diagnostics)

`cmd /c "src\build.cmd"` — one csc invocation, no defines:

| Artifact | size |
|---|---|
| `dist\GPU Mode Switch.exe` | **324,608 bytes** |

Static checks: banned C# 6 syntax scan (`$"`, `?.`, `nameof(`, `=>`,
`??=`, `using static`) — 0 hits; `#if MODE` — 0 left anywhere; UTF-16
scan of the exe: "GPU MODE SWITCH", "1.2.0", "LOCKED - cleanup
unavailable", "Go Eco (switch + restore)", "UNIFIED" all present.

## Visual verification — out-of-tree no-manifest harness

The real exe is `requireAdministrator`; a non-elevated agent cannot drive
an elevated window (Windows UIPI blocks input/UIA). Harness: the SAME
`src\*.cs` compiled to a temp exe WITHOUT the manifest (asInvoker) —
identical UI, fully drivable. Driven through screenshots + the
accessibility tree:

- **Probe-failure path** (this machine's ATKACPI rejects the GPU IDs):
  busy overlay → result overlay with detail text, tray rows correctly
  disabled, all buttons reachable. Session log lands in
  `logs\GpuModeSwitch\` with the "UNIFIED" header. ✓
- **Home**: both mode cards render (emblems, taglines, action pills);
  ECO card shows the ACTIVE badge with pulsing dot; header + status
  labels visible. ✓
- **Optimize**: profile bar, system/tray/performance/cleanup cards,
  animated switches, amber LOCKED banner with the gate reasons, GO
  button. ✓
- **Monitor**: live sensor bars sampling (CPU/Disk/RAM; GPU N/A — no
  queryable NVIDIA here), overlay button. ✓
- **History**: all three action cards render. ✓
- Harness + temp artifacts deleted afterwards.

## Known-issue fixes verified from the owner's field logs

- `GamePrep: HKLM write failed - The type of the value object did not
  match...` — NetworkThrottlingIndex wrote a `uint` with
  RegistryValueKind.Dword (needs `int`); now writes -1 (0xFFFFFFFF).
- `Fax stop failed - Service Fax was not found` — now logged as
  "not installed - skipped".
- "Bottom buttons can't be toggled" — the D7 cleanup gates disabling
  rows with only a small label; the deck now shows the amber LOCKED
  banner + per-row lock glyphs with the reasons.

## WinForms paint lesson (for future agents)

In this borderless/DPI shell, custom owner-drawn controls added EARLY to
a shared header strip never received WM_PAINT (verified by an OnPaint
log probe: 0 invocations with valid Visible/handle/bounds), while
late-added siblings painted fine. The header therefore uses plain
Labels added LAST inside a dedicated, always-topmost `_headerStrip`,
and `ShowBusy` no longer calls `BringToFront` (it used to cover the
header). Do not reintroduce GradientLabel/StatusChip there without
re-testing.

## MANUAL CHECKLIST — v1.2.0 additions (owner, on the G513QR)

14. [ ] Launch `GPU Mode Switch.exe`: Home shows the current mode; the
       inactive card matches reality (dGPU on → GO TIME active).
15. [ ] GO TIME card → Optimize deck: every switch toggles with the
       animated slide; the cleanup card shows either live sizes or the
       LOCKED banner with reasons (never silently grayed).
16. [ ] GO: switch + selected features apply; result overlay; session
       tray appears; tray "Go Eco (switch + restore)" flips to Eco fully.
17. [ ] ECO MODE card from Home: one click, silent, result overlay.
18. [ ] Monitor page shows live bars; "Show overlay over the game"
       opens the draggable overlay.
