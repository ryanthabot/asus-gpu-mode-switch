# Changelog — Big's GPU Switch & Game Optimizer (Go Time & Eco Mode)

One human-readable running history of BOTH applications. This file is a
fixed step of every release: a new version entry is appended every time an
error is repaired and every time a feature is added — entries are never
renumbered or rewritten. Fuller prose for older versions lives in
`README.md`; the authoritative code history is git; GitHub Releases carry
the built executables.

## v1.3.0 — 2026-09-09

**The "Big" release: new name, theme engine, tray-app suite, monitor
expansion and popup restyle.** The app is renamed **Big's GPU Switch &
Game Optimizer** (`Big's GPU Switch & Game Optimizer.exe`, was
`GPU Mode Switch.exe`) with the new tagline "dGPU Control & System
Optimization". Logs keep living in `%LOCALAPPDATA%\GpuModeSwitch` so
existing history is preserved.

1. **Tray-app suite (10 apps, close on GO, restore on Eco).** The Optimize
   page's tray group now detects **Wise Care 365, Overwolf, OpenBet
   LocatorT, NVIDIA Broadcast and Wallpaper Engine** alongside the existing
   Parsec, Google Drive, Jellyfin, Riot Client and Riot Vanguard (paths
   field-verified on black-ice, with runtime fallback resolution via the
   uninstall registry). Everything this app closes is remembered for the
   session and **restarted automatically on the next Eco switch**; a
   "Restore tray apps" action on Home and in the session tray menu restores
   them on demand while in GO TIME. Watchdog services (Parsec, vgc,
   WiseBootAssistant, OverwolfUpdater) are stopped first and restores use
   each app's real autostart arguments (e.g. Broadcast's `--launch-hidden`,
   Wallpaper Engine's `-silent`).
2. **Monitor expansion.** New labeled rows: **CPU temp (avg)** (mean of the
   ACPI thermal zones), **CPU hotspot** and **iGPU temp** (shown as honest
   dimmed "n/a" — no driverless API on Windows reads Ryzen hotspot or
   Radeon temps; a footnote says so), **iGPU usage** (best-effort, mapped
   from GPU engine counters once the dGPU is identified by correlating
   with nvidia-smi), and **dGPU usage / dGPU temp** as their own rows.
   **Disk rows are now per-drive** — `Disk (C:)`, `Disk (D:)` … with a
   Disk view selector (C: only / D: only / Both (separate) / Combined)
   persisted beside the refresh rate. The overlay gained dGPU rows.
3. **Theme engine + Theme page.** The whole palette is now runtime-mutable
   with a new **Theme** page: five presets (Midnight — the classic look,
   Carbon, Ocean, Ember, Frost), a custom accent color picker, a **header
   gradient** (two colors, horizontal/diagonal), a **left-navigation color
   picker**, and reset-to-default. Theme persists to
   `%LOCALAPPDATA%\GpuModeSwitch\theme.txt` and applies live.
4. **Popup restyle.** Session History and Log Browser now wear a **dark
   title bar** (DWM immersive dark), owner-drawn dark **column headers**
   matching the Optimize page's Profile bar, and the shared tool-button
   recipe (Segoe UI Semibold) also adopted by the Profile bar's
   Apply/Save/Delete/Refresh everywhere. Fixed en route: a latent
   LogBrowser constructor crash (SplitContainer Panel2MinSize set before
   the control was sized).
5. **Header alignment fix.** The "Connected via ..." chip wrapped to two
   lines and rode a few pixels high next to its neighbors; chips are now
   single-line (AutoEllipsis, shortened "via ..." text) and the chip row
   was slimmed to make room for the longer app title. Minimize/exit
   buttons untouched.

## v1.2.2 — 2026-09-08

**Field fixes from the v1.2.1 black-ice logs plus one startup hang found
during verification** (five repairs, one feature):

1. **Energy Saver: the Settings window must be RESTORED, not minimized.**
   The v1.2.1 flow opened `ms-settings:powersleep` minimized and
   re-minimized it the moment it appeared — but a minimized WinUI window
   virtualizes its content out of the UIA tree, so the Energy saver card
   was never found and the automation aborted every run. The window is
   now opened normal + `SW_RESTORE` + foregrounded before the search.
   Field-verified both directions on black-ice build 26200:
   `toggle now On (verified)` / `toggle now Off (verified)`.
2. **Energy Saver: scroll into view + fresh-rect expand click.** On some
   runs the page opened scrolled (Settings remembers scroll position):
   the toggle sat below the fold where WinUI virtualizes it out of the
   tree, and the expand click used rects snapshotted BEFORE the scroll,
   so it could miss — or worse, land on the off-screen toggle and flip
   it blindly. The card is now scrolled into view (`ScrollItemPattern`)
   first, the element snapshot is re-taken after the scroll, a
   real-click fallback (`SetCursorPos` + `mouse_event`) presses "Show
   more settings" when it exposes no Invoke pattern (observed on 26200),
   and if the toggle is already visible after scrolling no expander
   click is made at all (clicking would collapse an expanded card). The
   toggle flip itself has the same real-click fallback for the rare
   pattern-vanishes case.
3. **Startup hang: counter priming left the UI thread.** Every launch
   froze ~12 s while `MonitorEngine.Start` created and primed its
   performance counters synchronously on the UI thread — and on a
   machine with a degraded WMI/PDH stack that same call blocked for
   minutes, leaving a blank window that ignored all clicks (exactly what
   the v1.2.2 visual test caught on black-ice). Counter priming now runs
   once on a pool thread, every sample runs on a pool thread (the
   Forms.Timer tick only schedules it), and `DisposeCounters` uses
   `Monitor.TryEnter` so a stop can never block on an in-flight prime.
   The `SampleReady` event was already documented as any-thread (both
   subscribers marshal via `BeginInvoke`).
4. **The header strip paints.** The `—`/`✕` buttons (and title/chips
   alongside them) were invisible because of a WinForms z-order gotcha:
   `Controls.Add` appends to the END of the collection where index 0 is
   TOPMOST, so the full-size home section sat ABOVE the strip that was
   "added last". `_headerStrip.BringToFront()` (plus the buttons' own)
   puts the chrome permanently on top. Visually verified via remote
   screenshots on black-ice.
5. **`--eco --auto` applies eco.** The mode flags were checked after the
   `--auto` branch, so `--eco --auto` ran a STANDARD apply first;
   `_startEco` now wins and logs `UI: --eco --auto given, applying right
   away`.

**New:** the Monitor section has a **refresh-rate dropdown** (1 s / 2 s /
5 s / 10 s / 15 s, default 2 s). The choice re-targets the sampling timer
live and persists in `%LOCALAPPDATA%\GpuModeSwitch\monitor_interval.txt`
across sessions. Field-verified: dropdown opens and selects, persistence
(`10000` on disk after the test), and the `monitor: started (2000 ms)` →
`refresh rate set to N ms (saved)` log trail.

## v1.2.1 — 2026-09-07

**Field fixes from the v1.2.0 black-ice logs** (three repairs, no behavior
changes elsewhere):

1. **Energy Saver actually switches again.** v1.1.1 made the charge-level
   threshold write "silent-first" — but on build 26200 the power service
   (whesvc) ignores that legacy setting, so the write reported
   `rc=0 (verified)` while changing nothing the user can see, and the
   working Settings automation never ran (the field log showed only
   `setting energy saver charge level to 100%` and no Settings lines).
   v1.2.1 inverts the order: the Settings "Always use energy saver"
   automation is primary again (search-first, minimized, mouse-free), the
   threshold write stays as a silent supplement for builds that honor it.
2. **Storage cleanup is no longer all-locked on a normal machine.** The
   safety gates were global: `bits`/`UsoSvc` running (near-permanent on
   Windows 11) plus a lingering PendingFileRenameOperations entry locked
   the entire cleanup card — even though the cleaner itself stops
   usosvc → wuauserv → bits before the WU caches, and temp/shader/browser
   caches cannot interact with servicing state. Gates are now scoped:
   global (elevation only) locks everything; SERVICING gates (pending
   reboot signals) lock only the Windows Update cache and component store
   rows, with their own amber banner. The WU-busy check is gone as a gate
   entirely (the cleaner's own stop/restart logic covers it). GO-time
   re-checks follow the same scope.
3. **Minimize button.** The borderless window's top-right corner now has
   `—` (minimize) beside `✕` (exit). Minimizing is safe in any phase —
   background work continues and the taskbar icon restores the window.

## v1.2.0 — 2026-09-07

**THE SINGLE APP (owner decision D10): one executable, both modes.**
`Go Time.exe` and `Eco Mode.exe` are retired — replaced by
**`GPU Mode Switch.exe`**. Launch it and Home shows the current mode with
two big cards: **GO TIME** (configure & launch the session) and
**ECO MODE** (one click, silent). The compile-time MODE_STANDARD/MODE_ECO
split is gone; the mode is chosen at runtime (cards, `--gotime` / `--eco`
flags, or the session tray's "Go Eco"). Old flags still work (`--auto`,
`--confirm`, `--status`). The session tray's eco item now performs the
**full eco switch in-process** (switch + eco-safe restore).

**The redesign — a proper command deck.**
- **Sidebar rail** (Home / Optimize / Monitor / History) with Segoe glyph
  icons — every section is viewable at any time.
- **Home**: current-mode banner + two glowing mode cards with the app
  emblems, taglines and action pills; the ACTIVE mode pulses.
- **Optimize deck**: the Go Time selection stage rebuilt as rounded cards
  with **animated toggle switches** (no more tiny checkboxes) — system
  optimizations, tray apps, performance, storage cleanup. **Locked cleanup
  now explains itself**: when the safety gates block, an amber banner
  lists the reasons and the rows show a lock glyph (the v1.1.1 field
  report: grayed-out untickable boxes read as "broken").
- **Monitor page**: big live sensor grid + overlay toggle.
- **History page**: current log viewer, searchable log history across every
  run (old GoTime/EcoMode folders included), session history.
- Busy/result overlay with spinner, result glyph, wrapped text and the
  after-switch tray picker; borderless rounded window with fade-in kept.

**Fixed**
- **NetworkThrottlingIndex never actually turned off**: the HKLM write
  passed a `uint` to `RegistryKey.SetValue` with `RegistryValueKind.Dword`
  (which requires `int`) — every "network throttling off" threw a type
  mismatch (visible in the 2026-09-07 field logs). Now writes `-1`
  (0xFFFFFFFF) correctly.
- Missing services (e.g. Fax not installed) now log "not installed -
  skipped" instead of a failure.
- **Known-issue UX from the v1.1.1 field report** ("bottom buttons can't
  be toggled"): that was the D7 cleanup gates disabling rows with only a
  small label — see the lock banner above.

**Notes**
- Logs now go to `logs\GpuModeSwitch\` (the v1.x GoTime/EcoMode folders
  stay browsable in Log History). Version constant: `src/App.cs`.
- WinForms note for the future: custom owner-drawn controls added early to
  a shared strip never received WM_PAINT in this shell — the header uses
  plain labels added last (documented in HANDBOOK §6 v1.2.0).

## v1.1.1 — 2026-09-07

**Fixed**
- **Go Time's selection stage rendered empty** — the panel hosting every
  option group was created hidden and never made visible, so nothing could
  be selected and GO was effectively unreachable. Because GO never ran, the
  dGPU switch, optimizations, cleanup, session tray and overlay never
  executed either — one missing line, five symptoms. The panel is now shown
  when the stage opens (the log of the v1.1.0 run confirmed GO was never
  pressed: "UI: selection stage" then nothing until the window closed).
- **Energy Saver switching is now silent-first in both apps.** Through
  v1.1.0 the real "always use energy saver" toggle was driven through a
  Settings window that appeared on screen and moved the real mouse cursor.
  The "no silent API on 24H2+" belief in the old code was wrong: the
  ESBATTTHRESHOLD GUID it used had a hallucinated tail and returned
  ERROR_FILE_NOT_FOUND on every build. With the correct GUID
  (`e69653ca-cf7f-4f05-aa73-cb833fa90ad4`, "Charge level") the documented
  power API works on Windows build 26200 — verified by probe: read rc=0,
  write rc=0, read-back rc=0. Eco Mode now writes the charge level to 100%
  (energy saver always engages) and Go Time to 0% (never auto-engages while
  gaming), AC and DC, applied immediately, verified by read-back — nothing
  appears on screen.
- The Settings UI Automation remains only as a fallback (used when the
  silent write fails) and is now as invisible as the platform allows: our
  Settings window is opened minimized and minimized again the moment it
  appears, the "Show more settings" expand button is pressed via the UIA
  InvokePattern (all `mouse_event` code is gone — the real cursor is never
  moved), and a Settings window that already existed before ours is never
  adopted or closed.

**Added**
- `CHANGELOG.md` (this file) — one human-readable history of both apps,
  appended as a fixed step of every release.
- `PORTABILITY.md` — how to carry this project to and from, launch and edit
  it from different PCs: clone, build, per-user state files, publish steps.

**Notes**
- Go Time's "energy saver off" (charge level 0%) is intentionally stronger
  than the Settings toggle-off (which only returns to the charge-level
  default): while gaming, energy saver never auto-engages on battery.
  Running Eco Mode sets it back to always-on. Both values are logged with
  raw return codes.
- Cleanup gates, the "nvidia-smi run failed" warning while the dGPU is
  powered off, and the report-only Windows.old measurement are correct
  behavior, unchanged.

## v1.1.0 — 2026-09-06

**The session suite** (the 18-agent build; the single-source file split
into 21 modules): per-run timestamped logging with retention and a log
browser/history UI; Go Time's launch-time selection stage extended with a
Performance group (background process freezer, Ultimate Performance plan,
session-scoped Windows Update pause) and a Storage cleanup group (Windows
Update cache purge, DISM component store, deep clean, GPU shader caches,
per-app caches — CACHE-only, analyze-first, safety-gated); named profiles;
session history; a live system monitor; a session tray with a draggable
overlay after GO. `--auto` still applies only the safe v1.0.22 set.

## v1.0.1 – v1.0.22 — 2026-09-02 → 2026-09-06

- **v1.0.22** — launch-time selection stage (system optimizations + tray
  apps, GO to apply).
- **v1.0.21** — deeper reversible game prep (Game DVR off, network
  throttling off, six more paused services).
- **v1.0.20** — search-first Energy Saver Settings flow (no blind expand
  click).
- **v1.0.19** — log window rebuilt on a deterministic layout (buttons can
  no longer vanish).
- **v1.0.18** — tray-app closing handles watchdog services (Parsec
  relaunch fix).
- **v1.0.17** — Go Time tray app picker (Parsec, Google Drive, Jellyfin,
  Riot Client, Vanguard).
- **v1.0.16** — layout fixes; both windows resizable.
- **v1.0.15** — game prep (Game Mode, DND, pause background services) +
  Settings-based Energy Saver toggle.
- **v1.0.14** — Power Mode overlay switching (Eco = Battery saver,
  Go Time = Best performance).
- **v1.0.13** — honest Energy Saver build-support messaging; patient NV
  service stop/restart.
- **v1.0.12** — Energy Saver via the documented SUB_ENERGYSAVER threshold.
- **v1.0.11** — Energy Saver toggle via Quick Settings UI Automation.
- **v1.0.10** — Windows 11 Energy Saver synced with the GPU mode.
- **v1.0.9** — one-click default again (`--confirm` opts into a confirm
  stage).
- **v1.0.8** — themed splash UI; `--auto` skips the confirm stage.
- **v1.0.7** — main + log windows on the taskbar with app icons.
- **v1.0.6** — app icons (NVIDIA eye / eco leaf).
- **v1.0.5** — bare-zero DSTS = device not implemented (fixes MUX
  misdetection on the G513QR, which has no MUX).
- **v1.0.4** — live toggle always attempted first; MUX restart flow demoted
  to fallback.
- **v1.0.3** — live Standard ↔ Eco switching without restart (NV driver
  service release/restart).
- **v1.0.2** — safe two-step fallback + full diagnostic logging.
- **v1.0.1** — switched from the `ASUS_WMI` class to the direct ATKACPI
  device.

## Release process (every version, no exceptions)

1. Fix the error / build the feature; bump `Program.Version`
   (`src/App.cs`).
2. `cmd /c "src\build.cmd"` — csc must print zero diagnostics.
3. Append this file's new version entry (what was repaired/added, in plain
   language).
4. Commit (`fix:` / `feat:` / `docs:` prefixes), push to the remotes you
   have rights to (see PORTABILITY.md).
5. Tag the version and publish a GitHub Release with both exes from
   `dist\` attached.
