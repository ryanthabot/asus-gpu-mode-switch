# Changelog — ASUS GPU Mode Switch (Go Time & Eco Mode)

One human-readable running history of BOTH applications. This file is a
fixed step of every release: a new version entry is appended every time an
error is repaired and every time a feature is added — entries are never
renumbered or rewritten. Fuller prose for older versions lives in
`README.md`; the authoritative code history is git; GitHub Releases carry
the built executables.

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
