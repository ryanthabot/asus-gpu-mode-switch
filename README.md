# Big's GPU Switch & Game Optimizer — "Go Time" & "Eco Mode"

**ONE app, both modes**: **`Big's GPU Switch & Game Optimizer.exe`** (v1.2.3;
formerly `GPU Mode Switch.exe`) switches the
**GPU Mode** on ASUS laptops — the same Standard / Eco switch that lives in
*Armoury Crate → Devices → System Settings → GPU Performance* — without
opening Armoury Crate at all, then optimizes the session, monitors the
hardware and themes itself to taste. Launch it and Home offers both modes;
the old two-executable pair (Go Time.exe / Eco Mode.exe) is retired.

> **v1.2.3** — **the "Big" release**: renamed to **Big's GPU Switch &
> Game Optimizer** (tagline: *dGPU Control & System Optimization*); a
> **10-app tray suite** (adds Wise Care 365, Overwolf, OpenBet LocatorT,
> NVIDIA Broadcast, Wallpaper Engine — close on GO TIME, auto-restore on
> Eco plus manual restore from Home/the session tray); **Monitor** gains
> dGPU/iGPU/CPU-temp-average rows, per-disk (C:/D:) graphs with a disk-view
> selector (honest "n/a" placeholders where Windows exposes no driverless
> sensor); a full **theme engine** (presets, accent color picker, header
> gradient, navigation-bar color, live apply); **Session History / Log
> Browser popups restyled dark** to match the app (dark title bar,
> Profile-bar-colored column headers, Segoe UI Semibold buttons); and the
> header's "Connected via" chip alignment fixed. Details in
> `CHANGELOG.md`.

| Mode | What it does |
|---|---|
| **GO TIME** | Standard GPU mode: dGPU enabled, hybrid (MSHybrid) display path — configure the session on the Optimize page, then press GO |
| **ECO MODE** | Eco GPU mode: the dGPU is completely powered off (battery / silence) — one click from Home |

> **v1.2.2** — **Energy Saver verified both directions + visible window
> controls + a Monitor refresh-rate picker** (includes everything from
> v1.2.1): the Settings automation now RESTORES (never minimizes) its own
> window — a minimized WinUI window virtualizes its content out of the UIA
> tree, which is why every v1.2.x Energy Saver run died at "card not
> found" — scrolls the Energy saver card into view, re-snapshots element
> rects after the scroll (stale rects could click the wrong thing), and
> falls back to a real click when the "Show more settings" expander
> exposes no Invoke pattern (build 26200). Field-verified on the target
> G513QR: `toggle now On (verified)` on eco, `toggle now Off (verified)`
> on go. The window's **`—` and `✕` buttons now actually paint** (a
> WinForms z-order gotcha had the home section covering the whole header
> strip). The **Monitor page gained a refresh-rate dropdown** (1 s / 2 s /
> 5 s / 10 s / 15 s, default 2 s) that re-targets sampling live and
> persists across sessions. Also fixed: a **startup hang** —
> performance-counter priming ran on the UI thread (12 s frozen on a
> healthy machine, minutes-to-forever on a degraded WMI stack; all
> counter/sampling work moved to pool threads), and **`--eco --auto`
> applied Standard first** (the mode flag now wins). v1.2.1 (same release
> train): scoped storage-cleanup gates so a pending reboot locks only the
> WU/component-store rows instead of everything.
>
> **v1.2.0** — **the single app + full redesign**: ONE executable
> (`GPU Mode Switch.exe`) with both modes inside — Home shows the current
> mode and two glowing mode cards (GO TIME / ECO MODE, the active one
> pulses); a sidebar rail (Home / Optimize / Monitor / History) makes every
> section viewable; the Go Time selection stage is rebuilt with animated
> toggle switches in rounded cards, and **locked cleanup now explains
> itself** (amber banner with the gate reasons + lock glyphs — fixing the
> v1.1.1 "can't toggle the bottom boxes" report, which was the safety
> gates working); a live Monitor page and a History page (log viewer /
> searchable log history / session history); the session tray's eco item
> now performs the full eco switch in-process. Also fixed:
> NetworkThrottlingIndex never actually turned off (a uint/int registry
> type mismatch), and missing services (Fax) log "not installed" instead
> of failing. Logs now write to `logs\GpuModeSwitch\`.
>
> **v1.1.1** — **the v1.1.0 blocker fixed + silent Energy Saver**: Go
> Time's selection stage rendered empty (the stage panel was created hidden
> and never shown — GO was unreachable, so the switch, optimizations,
> cleanup, session tray and overlay never ran; one missing line, five
> symptoms). Energy Saver switching is now **silent-first** in both apps:
> the old "no silent API on 24H2+" belief traced to a wrong threshold GUID
> tail — with the correct GUID the documented power API works on build
> 26200. Eco writes the Energy Saver charge level to 100% (always engage),
> Go Time to 0% (never auto-engages while gaming) — AC + DC, verified by
> read-back, **nothing appears on screen**. The Settings automation is now
> fallback-only and mouse-free (UIA Invoke/Toggle patterns on our own
> minimized window only; your existing Settings windows are never touched).
> Added **`CHANGELOG.md`** (one human-readable history of both apps,
> updated every release) and **`PORTABILITY.md`** (carry the project
> between PCs).

> **v1.1.0** — **the session suite**: logging rewrite, storage cleanup,
> performance features, session tray + overlay, named profiles, session
> history and a live system monitor — all inside the same two executables.
>
> - **Logging rewrite**: every run writes its own timestamped log file to
>   `%LOCALAPPDATA%\GpuModeSwitch\logs\GoTime\` / `...\EcoMode\`
>   (`<App>_yyyy-MM-dd_HHmmss.log`). Retention: files older than **30 days**
>   are pruned, then oldest-first until the folder is ≤ **200 MB** (today's
>   logs and the current run's log are never deleted). The **View log**
>   window gained a history dropdown (past per-run logs), a severity filter
>   (All/Info/Warn/Error), find-next and an *Open folder* button; the
>   **Log History** browser lists and searches every log from both apps;
>   *Copy log* still copies the displayed log with one click. See the
>   [Logging](#logging) section below.
> - **Storage cleanup (Go Time selection stage)**: *Windows Update cache
>   purge* (stop wuauserv/bits/UsoSvc → purge `SoftwareDistribution\Download`
>   children → restart → re-detect; Delivery Optimization cache via the
>   official PowerShell cmdlet; old update log archives), *Component store
>   cleanup* through `DISM /StartComponentCleanup` (analyze-first, always),
>   *Deep clean* (aged per-user error reports, setup & upgrade logs) plus the
>   tier-1 targets (Windows temp >7 days, WER archives, user temp, crash
>   dumps, Explorer thumbnail caches), *GPU shader caches* (NVIDIA/AMD/
>   D3DSCache) and per-app browser/launcher caches — **CACHE-ONLY**
>   (cookies, history, passwords, sessions, bookmarks and every other
>   personal-data store are never touched). Everything is **analyze-first**:
>   sizes are measured in the background before GO and shown on the
>   checkboxes; safety gates (pending reboot, Windows Update busy, not
>   elevated) disable the whole group with the reasons shown. Never touched:
>   `WinSxS` (read/analyzed via DISM only), `catroot`/`catroot2`,
>   `C:\Windows\Installer`, the Servicing folder, `pending.xml`.
>   `DISM /ResetBase` and Windows.old removal are **deliberately not
>   offered** (Windows.old is measured and reported, never deleted).
> - **Performance features**: a background **process freezer** (suspend/resume
>   the apps in an editable freeze list for the session — nothing is ever
>   killed, and a never-freeze guard protects critical processes), the
>   **Ultimate Performance** power plan (the previous plan is remembered and
>   restored) and a **session-scoped Windows Update pause** (StartType never
>   changed, exactly what was stopped is restarted).
> - **Session tray + overlay**: after a successful GO a tray icon appears
>   (Open Go Time / Restore / Toggle overlay / Status / Exit) and a compact
>   click-draggable **overlay** shows live CPU/RAM/disk/GPU numbers over the
>   game. The tray lives only for the session — nothing autostarts.
> - **Named profiles**: save and re-apply every checkbox on the selection
>   stage (optimizations, tray apps, performance, cleanup) by name.
> - **Session history**: every GO run is recorded (actions applied, space
>   freed per category, duration, errors) and browsable via the
>   **Session History** viewer.
> - **Live system monitor**: an expandable monitor panel (CPU/RAM/disk/GPU/
>   temps) inside Go Time, also feeding the overlay.
>
> **`--auto` semantics (unchanged):** `--auto` skips the selection stage and
> applies ONLY the v1.0.22 set — system optimizations + GPU switch. The
> performance and storage-cleanup features **never run unattended**: freezing
> other apps' processes, switching power plans, pausing Windows Update and
> deleting files always require the explicit GO click. Cleanup checkboxes are
> unticked by default.
>
> **v1.0.22** — **launch-time selection stage in Go Time**: after probing,
> Go Time shows **two toggle groups** and waits for **GO**:
> - **System optimizations** — Game Mode, do-not-disturb, Game DVR recording
>   off, network throttling off, pause background services (all ticked by
>   default; untick anything you don't want — unticked items are actively
>   restored, so a previously paused state doesn't linger).
> - **Tray apps detected** — Parsec, Google Drive, Jellyfin, Riot Client,
>   Riot Vanguard; tick the running ones to close.
> Press GO and only the selected items are applied. Run with `--auto` to skip
> the selection stage and apply everything immediately. Eco Mode is unchanged
> (one-click; `--confirm` gives it a confirm stage). The Go Time window grew
> to 560x640 to fit both groups (still fully resizable).
>
> **v1.0.21** — **deeper game prep, still fully reversible**: Go Time now
> also turns off **Game DVR background recording**, disables **multimedia
> network throttling** (restored to the Windows default by Eco Mode), and
> pauses six more background services (**WerSvc, MapsBroker, TrkWks,
> WMPNetworkSvc, SEMgrSvc, Fax**). Deliberately excluded: Xbox/Game Pass
> services, biometrics, text input, audio and display services — anything
> that would break logins, sound or the shell.
>
> **v1.0.20** — **Energy Saver flow reordered to search-first**: the
> "Always use energy saver" toggle is looked for *before* any expansion
> click, so an already expanded Energy saver card (persisted across runs by
> the Settings process) is never collapsed by a blind click. The card's
> show-more button is only pressed when the toggle is genuinely not visible,
> and the search is retried after each expand attempt.
>
> **v1.0.19** — **log window rebuilt on a deterministic layout**: the Copy log
> and Close buttons can no longer vanish regardless of DPI or resize state,
> the log text is **pre-selected** when the window opens (Ctrl+C copies
> straight away), and the log file path is shown in its own top strip.
>
> **v1.0.18** — **tray app close now handles watchdog services**: Parsec's
> own Windows service was silently relaunching `parsecmd` after every kill.
> The picker now discovers matching services from the Services registry,
> stops them before killing, and re-checks up to 3 rounds — reporting in the
> log whether the app stayed closed.
>
> **v1.0.17** — **Go Time tray app picker**: after the switch, Go Time scans
> for known tray applications — **Parsec, Google Drive, Jellyfin, Riot Client
> and Riot Vanguard** — and lists each with a checkbox. Tick the running ones
> you want gone and press **Close selected** (graceful close first, then
> kill; Vanguard's vgc service stop is also attempted). Detection and every
> close action are logged.
>
> **v1.0.16** — **layout fixes**: window widened with proper padding so no
> text clips at the edges, and **both windows are now resizable** — drag any
> edge or corner of the themed main window (it keeps its rounded corners and
> dark look; the interior still drags the window), and the log window is a
> standard resizable window with a docked layout.
>
> **v1.0.15** — **game prep + Energy Saver control**: Go Time enables Game
> Mode, do-not-disturb and pauses background services (SysMain, Windows
> Search, Print Spooler, DiagTrack); Eco Mode restores them and keeps Energy
> Saver always on (via the real switch in Settings > Power & battery >
> Energy saver). The power plan itself always stays Balanced.
>
> **v1.0.14** — **Power Mode switching added** (the power-saving lever that
> build 26200+ still exposes): **Eco Mode** sets the Windows 11 Power Mode to
> **Battery saver** (best efficiency) on both AC and battery; **Go Time** sets
> it to **Best performance**. Written via `PowerWriteAC/DCValueIndex` +
> `PowerSetActiveScheme`, effective immediately, no popup. The Energy Saver
> threshold attempt and registry intent are still applied quietly (they work
> on older Windows builds).
>
> **v1.0.13** — **honest Energy Saver status handling**: on the newest
> Windows 11 builds (24H2+/26200+, where Energy Saver moved to the `whesvc`
> service), the legacy Energy Saver threshold setting is no longer exposed
> via the power API — the write returns "not found" and the app now says so
> explicitly in the result window and log ("not controllable via the power
> API on this Windows build"), instead of a generic failure. On builds/models
> that still expose the setting, the v1.0.12 threshold mechanism (100%/0%)
> works unchanged. The NV driver service stop/restart is also more patient
> (15s) and a stop-timeout is clearly logged as non-fatal.
>
> **v1.0.12** — **Energy Saver now uses the documented power setting** (no
> more Quick Settings popup): Eco Mode sets the Energy Saver battery threshold
> to **100%** (Energy Saver always engages when on battery) and Go Time sets
> it to **0%** (never auto-engages).
>
> **v1.0.11** — Energy Saver toggle via Quick Settings UI Automation
> (removed in v1.0.12; the tile could not be located reliably and the
> popup was unwanted).
>
> **v1.0.10** — **Windows 11 Energy Saver is now synced with the mode**: Eco
> Mode turns Energy Saver **on**, Go Time turns it **off**.
>
> **v1.0.9** — **one-click is back**: launching an app probes and applies
> immediately, with the themed window and shimmer animation running the whole
> time. The confirm step still exists as an opt-in: run with `--confirm` to
> review the detected state and press Apply first.
>
> **v1.0.8** — themed UI: borderless window with the app logo, animated
> shimmer bar, fade-in, background-thread switching; confirm flow moved to
> opt-in (`--confirm`); Go Time icon rebuilt with true transparent corners
> and a gradient 3D tile.
>
> **v1.0.7** — the main window and the diagnostic log window now appear on
> the taskbar (with the app's own NVIDIA-eye / leaf icon), instead of being
> hidden while the switch runs.
>
> **v1.0.6** — application icons added: **Go Time** carries the NVIDIA eye on a
> dark tile (rendered from the official glyph), **Eco Mode** a white leaf on
> green. Both embedded as multi-size `.ico` (16/32/48/256).
>
> **v1.0.5** — fixed MUX misdetection diagnosed from a G513QR log: a bare-zero
> DSTS response (no status bits) now correctly means *"device not implemented
> by this firmware"* instead of *"MUX in dGPU-direct mode"*. On MUX-less
> machines like the G513QR the apps now skip the MUX entirely and do a pure
> live dGPU power toggle — no MUX writes, no restart flow.
>
> **v1.0.4** — the live switch is now **always attempted first**, exactly like
> Armoury Crate: just flip the dGPU power flag, no restart. The MUX/one-time
> restart flow was demoted to a fallback that only kicks in if the firmware
> itself refuses the write while the display path is physically running on
> the dGPU.
>
> **v1.0.3** — Standard ↔ Eco applies **live, without a restart**: the NVIDIA
> Display Container driver service is released before switching to Eco (so the
> firmware can cut dGPU power immediately) and restarted after switching back
> to Standard (so the GPU returns right away).
>
> **v1.0.2** — safe two-step fallback + full diagnostic logging
> (*View log → Copy log*, plus `%LOCALAPPDATA%\GpuModeSwitch\`).
>
> **v1.0.1** — switched from the `ASUS_WMI` WMI class to the direct ACPI device
> (`\\.\ATKACPI`) that modern firmware actually uses. This fixes the
> *"ASUS hardware interface not found"* error on models like the
> **ROG Strix G15 (G513QR)**. The WMI classes remain as fallback.

## Download

Grab **`GPU Mode Switch.exe`** (the single unified app) from the
[**Releases**](../../releases/latest) page.

## Requirements

- An **ASUS laptop with a dedicated GPU** (ROG, Zephyrus, Strix, TUF, Vivobook Pro, Zenbook Pro…)
- Windows 10 or 11 (nothing else needs to be installed — the apps use the .NET
  Framework that ships with Windows)
- The **ASUS System Control Interface** drivers must be present. They ship with
  Armoury Crate or MyASUS, so if either of those has ever been installed, you're set.
- Administrator rights — a UAC prompt when you double-click is expected and normal.

## How to use

1. Close games and other apps that are using the dGPU.
2. Double-click **GPU Mode Switch.exe**, confirm the UAC prompt.
3. Home shows the current mode — click **ECO MODE** to power the dGPU off,
   or **GO TIME** to configure the session on the Optimize page and press
   GO. The switch applies live (no restart) and the shimmer bar tracks the
   whole cycle.
4. Click the other card whenever you want to switch back.

Optional: run with `--eco` / `--gotime` to preselect a mode, `--auto` to
apply immediately without the selection stage, or `--confirm` to review
the detected state and press **Apply** before anything is switched
(`--eco --auto` = one-shot eco switch, e.g. from a script or scheduled
task).

A restart is only ever needed in one situation: if the firmware refuses the
switch because the display path is physically running through the dGPU
(Ultimate mode). The app detects that, moves the MUX back to hybrid, and asks
for **one** restart — after that, switching is instant every time.

**When something goes wrong:** click **View log** in the app — it shows every
probe, read and write with raw hex values. *Copy log* puts it on the clipboard
so you can paste it into a bug report. Each run also writes its own log file —
see the [Logging](#logging) section.

Run either app with `--status` (e.g. from a terminal) to see the detected
hardware interface and the current GPU state without switching anything.

## How it works

Armoury Crate is just a UI on top of a BIOS-level switch. Both apps call that
switch directly, trying these channels in order until one answers:

1. **Direct ACPI device I/O** — `\\.\ATKACPI` via `DeviceIoControl`
   (control code `0x0022240C`, methods `DSTS` = read / `DEVS` = write).
   This is what G513QR-class firmware uses and is the primary path.
2. **WMI class `AsusAtkWmi_WMNB`** (`root\WMI`) — older ATK-era firmware.
3. **WMI class `ASUS_WMI`** (`root\WMI`) — other firmware generations.

The device IDs being switched (documented by the Linux kernel `asus-wmi`
driver and used by G-Helper):

| Device ID | Function | Values |
|---|---|---|
| `0x00090020` | dGPU power (Vivobook: `0x00090120`) | 0 = on, 1 = off (eco) |
| `0x00090016` | GPU MUX (Vivobook: `0x00090026`) | 0 = dGPU direct, 1 = Optimus/hybrid |

Both apps keep the MUX on the hybrid path and toggle dGPU power — exactly what
Armoury Crate's *Standard* and *Eco* cards do. The apps auto-detect which
endpoint pair your firmware implements and verify the change by reading the
state back. If Armoury Crate is installed, its UI will show the new mode the
next time you open it.

References: the [Linux kernel `asus-wmi` driver](https://github.com/torvalds/linux/blob/master/include/linux/platform_data/x86/asus-wmi.h)
(which documents these device IDs) and [G-Helper](https://github.com/seerge/g-helper)
(the open-source ASUS control app that uses the same interface on Windows).

## Logging

Since v1.1.0 every run writes its own timestamped log file:

```
%LOCALAPPDATA%\GpuModeSwitch\logs\GoTime\Go Time_yyyy-MM-dd_HHmmss.log
%LOCALAPPDATA%\GpuModeSwitch\logs\EcoMode\Eco Mode_yyyy-MM-dd_HHmmss.log
```

- Each log line reads `yyyy-MM-dd HH:mm:ss.fff [LEVEL] (channel) message`
  with LEVEL = INFO/WARN/ERROR and an optional channel tag (CLEAN, GPU,
  FREEZE, POWER, TRAY, MONITOR, PROFILE, SESSION).
- **Retention:** on every session start, files older than **30 days** are
  deleted, then oldest-first until the folder is ≤ **200 MB** total. Files
  stamped today and the current run's log are never deleted.
- **Finding logs:** the in-app **View log** window shows the live log of the
  current run plus every past per-run log (history dropdown, severity filter,
  find-next, *Open folder*). The **Log History** button (result stage) opens
  a browser over all logs of both apps with *Search all logs*. *Copy log*
  copies the displayed text, *Copy all* the whole unfiltered source.
- If a log file cannot be written (permissions, disk), the file sink disables
  itself for that run with a single warning — the in-memory buffer and the
  Copy buttons keep working.

The full agent-facing build/handoff documentation (module map, design
decisions, verification evidence) lives in [`docs/HANDBOOK.md`](docs/HANDBOOK.md).

## Storage cleanup & session features (safety model)

- **Analyze-first, always:** sizes are measured read-only before anything can
  be selected; the cleaners re-measure and re-check the safety gates
  (pending reboot / Windows Update busy / not elevated) right before
  deleting — any reason blocks everything and is logged.
- **Cache-only app cleaning:** only whitelisted cache folder names
  (`Cache`, `Code Cache`, `GPUCache`, `shadercache`, `cache2`, …) under the
  explicit per-app parent directories are candidates, and a second
  forbidden-name wall (cookies, history, logins, bookmarks, local storage,
  …) is re-checked before any enumeration or delete.
- **Session-scoped features are reversible:** the freezer only suspends
  (never kills) and resumes exactly what it suspended; the power plan
  restore returns the plan that was active before; the WU pause restarts
  only the services this process stopped and never changes StartType.
  Everything is also restored on Eco Mode, on errors and on exit.
- **`--auto` never touches any of this** — see the v1.1.0 notes above.

## Troubleshooting

- **"ASUS hardware interface not found"** — the ASUS System Control Interface
  driver isn't answering. Install or *repair* Armoury Crate (or MyASUS →
  customer service → driver updates), reboot, and try again. Run the app with
  `--status` to see exactly which channels were tried.
- **Switch doesn't stick / "refused"** — something is still using the dGPU
  (game, browser with hardware acceleration, XG Mobile). Close it and retry.
- **"ASUS WMI interface not found" on a desktop or another brand** — expected;
  this is laptop firmware, it doesn't exist there.
- **Windows SmartScreen warning on first run** — the exes are unsigned.
  Click *More info* → *Run anyway*.
- **Stays in the old mode until you restart** — by design; the MUX/power change
  finalizes on reboot.
- **Reporting a bug** — click *View log → Copy log* in the app and paste the
  output into your issue; it contains everything needed to diagnose remotely.

## Building from source

`src\build.cmd` — that's it. It compiles both executables with the C# compiler
that ships with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`),
so no Visual Studio or .NET SDK is needed.

```
asus-gpu-mode-switch/
├── src/                    ← all *.cs files compile into BOTH apps
│   │                          (MODE_STANDARD -> "Go Time.exe",
│   │                           MODE_ECO -> "Eco Mode.exe")
│   ├── App.cs … TrayIcon.cs (20 module files; see docs/HANDBOOK.md §3
│   │                          for the full module map)
│   ├── app.manifest       ← requires administrator
│   └── build.cmd          ← builds dist\Go Time.exe + dist\Eco Mode.exe
├── docs/HANDBOOK.md       ← architecture, design decisions, build evidence
├── BUILD_NOTES.md         ← verification evidence per wave
├── LICENSE (MIT)
└── README.md
```

> The v1.1.0 development was driven by an 18-agent build documented in
> `docs/HANDBOOK.md` — start there if you want to understand or extend the
> codebase (per-module APIs, D1–D9 design decisions, cleanup safety model).

## Limitations

- Laptops only — desktop motherboards don't expose this interface.
- The "Optimized" (Advanced Optimus auto-switch) mode is not supported;
  these apps switch between Standard and Eco only.
- Model-specific quirks exist. If your machine reports the interface as missing
  or refuses writes even with everything closed, open an issue with the output
  of `Eco Mode.exe --status`.

## License

[MIT](LICENSE)
