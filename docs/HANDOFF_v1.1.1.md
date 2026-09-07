# HANDOFF v1.1.1 — post-release known issues, current state, and the updated owner prompt

Written 2026-09-07 after the first real-world v1.1.0 run (Windows build 26200.8457,
ASUS ROG Strix G513QR, full session log in §5 and in the owner's prompt). Every
code claim below was re-verified against commit `5df43b7` (tag `v1.1.0`).
**Read `docs/HANDBOOK.md` first** (hard constraints, architecture, decisions
D1–D9); this file adds what the handbook cannot say from inside the 18-agent
build: what actually happened when a human used v1.1.0, what is broken, why,
and exactly where the fixes go.

> **v1.1.1 OUTCOME (2026-09-07):** §2.1 (selection stage) and §2.2 (Energy
> Saver) are FIXED. §2.2's analysis was superseded during the fix — the
> "silent threshold dead on 24H2+/26200 (whesvc)" premise was wrong: whesvc
> is an unrelated service, and the real cause was a hallucinated GUID tail;
> with the correct GUID the documented power API is silently writable on
> build 26200 (probe-verified). See the v1.1.1 entries in HANDBOOK §6 and
> CHANGELOG.md. §2.3's monitor note and §2.4's docs gaps are addressed.
> §4.7 / §7 (single app, v1.2.0) pending the owner's name/icon decision.
> This document is retained as the historical handoff record.

- Repo: https://github.com/bigthabot/asus-gpu-mode-switch (primary/publish —
  at `5df43b7`, tag `v1.1.0`, at the time of writing).
- Mirror: https://github.com/ryanthabot/asus-gpu-mode-switch — carries this
  very handoff commit (`05862fb`, one commit ahead of bigthabot). This PC's
  credentials (ryanthabot) cannot push to bigthabot (403). To sync bigthabot,
  run once on a machine with bigthabot credentials:
  `git pull https://github.com/ryanthabot/asus-gpu-mode-switch main && git push`
- Test machine that produced the log: G513QR, no MUX (`0x00090016` reads bare
  zero = not implemented), ATKACPI transport works, NVIDIA dGPU.

---

## §1 Current behavior inventory (what works, what doesn't)

Status legend: **RUNTIME-VERIFIED** = proven working on a real machine;
**BUILD-VERIFIED ONLY** = compiles clean (both `/define` targets) but its
runtime path was never exercised by a human before/at v1.1.0; **BROKEN** =
fails at runtime with a known root cause.

| Capability | App | Status | Evidence |
|---|---|---|---|
| dGPU off + NVIDIA service release (eco write) | Eco | **RUNTIME-VERIFIED** | owner: "eco mode correctly switches off the dgpu"; v1.0.x history |
| dGPU on + NV service restart (standard write) | Go Time | **RUNTIME-VERIFIED** (v1.0.x path, unchanged) | the switch code itself is untouched by v1.1; only *reaching* it is broken (§2.1) |
| ATKACPI probe, DSTS normalize, MUX "not present" handling | both | **RUNTIME-VERIFIED** | log lines 18:12:42.553–.566 |
| Energy Saver engage (Settings UIA) | Eco | **RUNTIME-VERIFIED, obtrusive** | owner: "correctly ... engages windows' energy saver toggle" — but it visibly opens Settings and moves the mouse (§2.2) |
| Energy Saver sync | Go Time | **UNREACHABLE at v1.1.0** (cascade of §2.1) + same obtrusive UIA when reached | `AsusControl.cs:456`, `:563` |
| Power Mode overlay (eco=Battery saver, go=Best performance) | both | **BUILD-VERIFIED ONLY** (silent, expected fine — plain power API) | `EnergySaver.cs:408` |
| Selection stage: options visible, groups, GO reachable | Go Time | **BROKEN** — the panel never becomes visible | §2.1 |
| GPU switch / optimizations / cleanup behind GO | Go Time | **UNREACHABLE** at v1.1.0 (cascade of §2.1); build-verified only | log: "UI: selection stage" then no GO, window closed 1m45s later |
| Tray-app picker + close (v1.0.22 path) | Go Time | **RUNTIME-VERIFIED** in v1.0.x; v1.1 wiring build-verified only (unreachable at v1.1.0) | |
| Session tray + overlay | Go Time | **BUILD-VERIFIED ONLY**, unreachable at v1.1.0 (created in `AfterGoSuccess` only after a successful GO) | `Forms.cs:2335`, `:2403` |
| Process freezer / Ultimate plan / WU pause | Go Time | **BUILD-VERIFIED ONLY** (design-verified, never human-run) | `BUILD_NOTES.md` checklist items 5–8 |
| All cleaners (StorageCleaner, DISM, DeepClean, GpuTools, AppCacheCleaner) | Go Time | **BUILD-VERIFIED ONLY** + read-only Measure smoke runs (never executed a deletion) | gates correctly blocked the v1.1.0 run (§3) |
| Analyzer (measure + gates) | Go Time | **RUNTIME-VERIFIED** (the v1.1.0 log shows a full correct measure pass) | log lines 18:12:49–.53 |
| Logging rewrite (per-run files, header, retention) | both | **RUNTIME-VERIFIED** | the log itself is the evidence |
| Log viewer/browser, profiles, session history, monitor | Go Time | **BUILD-VERIFIED ONLY** (monitor engine started correctly in the log: "monitor: started (2000 ms)") | |
| `--auto` / `--confirm` / `--status` flags | both | **RUNTIME-VERIFIED** in v1.0.x; unchanged in v1.1 | |

## §2 Known issues — root causes and exact fix pointers

### 2.1 Go Time selection stage invisible (THE blocker; everything else cascades from it)

- `Forms.cs:1142` — the panel is created hidden: `_selectPanel.Visible = false;`
  (right after `AutoScroll`/`BackColor` setup in the MainForm constructor region).
- `Forms.cs:1624` `EnterSelect(string precheckText)` — runs the whole stage
  setup (`GrowForSelection`, `LayoutSelectContent`, `SetSelectGroupsVisible(true)`,
  monitor attach, measurement, tray detect) and logs "UI: selection stage" —
  but **never sets `_selectPanel.Visible = true`**.
- The ONLY `_selectPanel.Visible = true` in the file is `Forms.cs:1422`, inside
  `EnterResultTraySection()` — which runs **after** the switch, in the result
  stage.
- Consequence chain (matches the user's complaint word for word): no option
  groups visible → GO is a mystery button with nothing to select → GO never
  pressed → no dGPU cycle, no optimizations, no cleanup, no session tray, no
  overlay (all of those live behind GO). The v1.1.0 log confirms: "UI:
  selection stage" at 18:12:48.999, then nothing but measurements, then
  "Session end: main window closed (total 00:01:45)". The GPU code is NOT at
  fault.
- **Fix**: in `EnterSelect` (Forms.cs:1624), set `_selectPanel.Visible = true;`
  (e.g. right before/after `LayoutSelectContent(precheckText)` at line 1637).
  Then verify the interplay with `GrowForSelection()` (form grows 560x640 →
  600x880) and that `EnterResultTraySection` (1422) still reconfigures the
  panel correctly for the result stage (it moves/resizes it — the new
  visibility must not fight it). One-line root fix; regression-check the full
  stage layout at least at 100% and 150% DPI.

### 2.2 Energy Saver switching pops a visible Settings window and hijacks the mouse

- `EnergySaver.cs:119` `Sync(bool on)`: `WriteSavedState` → tries
  `ToggleAlwaysUseEnergySaver(on)` (the real "Always use energy saver"
  Settings toggle) FIRST → only on failure falls back to `SetAutoThreshold`
  (EnergySaver.cs:358, the legacy `ESBATTTHRESHOLD` power value).
- The UIA path is genuinely visible and obtrusive: launches
  `ms-settings:powersleep` visibly (`EnergySaver.cs:266`), moves the REAL
  mouse cursor (`mouse_event` at `EnergySaver.cs:182–194`, constants at
  127–136), can adopt/close the user's own Settings window, and can orphan a
  window on timeout. It DOES work (Eco verified) — the owner wants it
  invisible in BOTH apps.
- **Why you can't just reorder to silent-first**: the silent fallback
  `SetAutoThreshold` is known-broken on modern builds — the code itself
  documents (EnergySaver.cs:379–384) that on 24H2+/26200+ the legacy power API
  returns rc=2 `ERROR_FILE_NOT_FOUND` because Energy Saver moved to the
  **whesvc** service. That is exactly why UIA was made primary. Do not ship
  "silent-only" without a path that actually flips the real toggle on 26200.
- Call sites (both exes, both the "already in target mode" path and the real
  switch path): `AsusControl.cs:455–456` and `AsusControl.cs:562–563`.
- **Fix directions (research first, in this order):**
  1. Find a genuinely silent API for the real ES toggle on build 26200
     (whesvc era). Candidates to verify: whether whesvc honors the
     `HKLM\SYSTEM\CurrentControlSet\Control\Power\EnergySaverState` value
     (already written by `WriteSavedState`, EnergySaver.cs:94) at *runtime*,
     not just boot; any new power GUID exposed via `PowerWriteValueIndex`;
     any COM/service control surface on whesvc. Keep
     `ApplyPowerModeOverlay` (silent, works) as the complementary lever.
  2. If no fully silent API exists, minimize intrusion honestly: drive the
     toggle with UIA `TogglePattern.Toggle()` instead of `mouse_event`
     (no cursor hijack), never adopt the user's existing Settings window,
     always close only the window we opened, log the residual visibility.
     Present this trade-off to the owner in the release notes rather than
     shipping a silent path that silently fails.
- Whatever lands: keep the "never blocks the GPU switch on failure" rule and
  the POWER-channel logging with raw return codes.

### 2.3 Minor: monitor GPU metrics warning noise

- `SystemMonitor.cs:100` (`SmiTimeoutMs = 2500`) and `:426/:433/:437`
  (`LogGpuUnavailableOnce`): one WARN per session when the nvidia-smi run
  fails. In the v1.1.0 log the binary was FOUND (`C:\Windows\System32\nvidia-smi.exe`)
  but the run failed — almost certainly because the dGPU was **off** (Eco
  state; GO never pressed), which is correct N/A behavior, not a bug.
  Optional v1.1.1 polish: when the dGPU state is known-off, log "gpu metrics
  unavailable (dGPU off)" once instead of "run failed".

### 2.4 Documentation gaps (fix as part of the v1.1.1 release discipline)

- No `CHANGELOG.md` exists anywhere; the version history lives only in
  README blockquotes (unheaded chain) — see owner requirement §4.1.
- `project contents/CONTENTS.md:35–39` announced this known issue and pointed
  to "HANDBOOK.md §6 / BUILD_NOTES.md" for details that were never written.
  This handoff doc + the appended HANDBOOK §6 entry / §8 block resolve that
  dangling pointer; keep `project contents/` refreshed per release.
- HANDBOOK §8 said "PROJECT COMPLETE" with no post-release section — amended
  (see HANDBOOK §8 "Post-release known issues (v1.1.1)").

### 2.5 Portability / repo state (for whichever machine continues the work)

- The repo is the portable state. Remotes: `bigthabot` (primary/publish) and
  `ryanthabot` (mirror; the original account). Both hold identical `main` at
  `5df43b7` + tag `v1.1.0`. `dist/` is gitignored — exes come from GitHub
  Releases or a local `src\build.cmd` run.
- Per-user runtime state (NOT in the repo; document in the portability file):
  `%LOCALAPPDATA%\GpuModeSwitch\` — `logs\GoTime|EcoMode\` per-run logs,
  `freezelist.txt`, `powerplan.txt`, `profiles.json`, `sessions.jsonl`.

## §3 Known NON-issues in the v1.1.0 log (do not chase these)

1. **4 cleanup gate blocks** (PendingFileRenameOperations; wuauserv/bits/
   UsoSvc Running) — the D7 safety gates working exactly as designed. A
   reboot + Windows Update going idle clears them. Not a bug.
2. **"gpu metrics unavailable (nvidia-smi run failed)"** — expected while the
   dGPU is powered off (see §2.3).
3. **Windows.old 549.93 GB "report only"** — by design (D3 Tier 3: measured,
   never deleted, anywhere in the suite).
4. **"appcache: forbidden name '...' blocked" WARNs for Steam htmlcache** —
   the D5 forbidden-name wall correctly refusing Steam's embedded profile
   data (Favicons/History/Login Data/...) that happens to live under a
   whitelisted cache parent. Working as designed.

## §4 New requirements from the owner (v1.1.1 scope)

1. **Fix the selection-stage bug** (§2.1) — restores Go Time's entire feature
   surface (options, GO chain, optimizations, cleanup, tray, overlay).
   Then work through the 10-item manual checklist in `BUILD_NOTES.md`.
2. **Invisible Energy Saver switching in BOTH apps** (§2.2) — no Settings
   window on screen, no mouse movement; silent-first with honest fallback
   per the research order in §2.2.
3. **Running changelog, one document, human-readable** — create
   `CHANGELOG.md` at the repo root; every release (including v1.1.1)
   appends to it as part of the release process itself, so the file can
   never drift from reality.
4. **Separate portability file** — a doc (e.g. `PORTABILITY.md`, or a
   refreshed `project contents/` bundle) describing how to carry the package
   between PCs: clone/build (`src\build.cmd`), the per-user state files
   (§2.5), release/publish steps. Goal: the software package can be brought
   to and from, launched and edited from different PCs.
5. **GitHub release discipline** — the full release history continues; a new
   version is cut EVERY time an error is repaired and EVERY time a feature
   is added. This release is **v1.1.1** (append; never renumber history).
   Bump `Program.Version` at `src/App.cs:148`.
6. **HANDBOOK stays living** — append to §4/§6, update §5; never rewrite
   history sections (per its own header rules).
7. **SINGLE APP (owner decision, 2026-09-07)** — the owner wants ONE
   executable with both modes inside it, not two separate exes. This
   supersedes handbook decision D4 ("the suite stays two exes") — recorded
   as D10 in HANDBOOK §4. Scope it as **v1.2.0** (it is a feature; per the
   owner's own release discipline a feature gets its own version AFTER the
   v1.1.1 error fixes). Full design sketch in §7 below.

## §5 The complete v1.1.0 Go Time session log (the owner's report)

```
2026-09-06 18:12:41.988 [INFO] (APP) === Go Time v1.1.0 session (MODE_STANDARD) ===
2026-09-06 18:12:41.990 [INFO] (APP) Session start: 2026-09-06 18:12:41.990
2026-09-06 18:12:41.991 [INFO] (APP) Log file : C:\Users\bd799\AppData\Local\GpuModeSwitch\logs\GoTime\GoTime_2026-09-06_181241.log
2026-09-06 18:12:41.993 [INFO] (APP) Windows build: 26200.8457
2026-09-06 18:12:42.099 [INFO] (APP) Machine  : ASUSTeK COMPUTER INC. ROG Strix G513QR_G513QR
2026-09-06 18:12:42.102 [INFO] (APP) Admin    : yes (elevated)
2026-09-06 18:12:42.103 [INFO] (APP) .NET runtime: 4.0.30319.42000
2026-09-06 18:12:42.270 [WARN] (APP) profiles: no profiles saved yet (C:\Users\bd799\AppData\Local\GpuModeSwitch\profiles.json)
2026-09-06 18:12:42.271 [INFO] (PROFILE) profile bar: reloaded (0 profile(s))
2026-09-06 18:12:42.545 [INFO] (APP) UI: probing
2026-09-06 18:12:42.553 [INFO] (GPU) Probing transport: direct ACPI device (\\.\ATKACPI)
2026-09-06 18:12:42.556 [INFO] (GPU) ATKACPI: device opened
2026-09-06 18:12:42.560 [INFO] (GPU)   ATKACPI DSTS dev=0x00090020 val=0 -> ok=True raw=0x00010001
2026-09-06 18:12:42.564 [INFO] (GPU)   normalize dGPU probe 0x00090020: raw=0x00010001 -> 1
2026-09-06 18:12:42.565 [INFO] (GPU)   ATKACPI DSTS dev=0x00090016 val=0 -> ok=True raw=0x00000000
2026-09-06 18:12:42.565 [INFO] (GPU)   normalize MUX probe 0x00090016: raw=0x00000000 -> no status bits, device not implemented
2026-09-06 18:12:42.566 [INFO] (GPU) Selected: direct ACPI device (\\.\ATKACPI)  dGPU=0x00090020  MUX=0x00090016 (not present)
2026-09-06 18:12:42.568 [INFO] (GPU)   ATKACPI DSTS dev=0x00090020 val=0 -> ok=True raw=0x00010001
2026-09-06 18:12:42.568 [INFO] (GPU)   normalize dGPU read: raw=0x00010001 -> 1
2026-09-06 18:12:42.572 [INFO] (POWER) EnergySaver: saved state=1 (on)
2026-09-06 18:12:42.572 [INFO] (GPU) Precheck done: Current state: dGPU off (eco). | Target: Standard mode (dGPU on). | Windows Energy Saver (saved state): on.
2026-09-06 18:12:48.988 [INFO] (MONITOR) monitor: started (2000 ms)
2026-09-06 18:12:48.999 [INFO] (APP) UI: selection stage
2026-09-06 18:12:49.010 [INFO] (CLEAN) measure 'Windows Update download cache': 16.2 MB in 4 files - cleaner must stop wuauserv/bits/UsoSvc before deleting anything here
2026-09-06 18:12:49.012 [INFO] (CLEAN) measure 'Delivery Optimization cache': 0 B in 0 files - cleaner must stop/flush Delivery Optimization (DoSvc) before deleting anything here
2026-09-06 18:12:49.019 [INFO] (CLEAN) measure 'Windows temp (>7 days)': 977 B in 1 files - top-level items last written more than 7 days ago (old folders counted with all contents)
2026-09-06 18:12:49.020 [INFO] (CLEAN) measure 'Windows error reports': 0 B in 0 files
2026-09-06 18:12:49.024 [INFO] (TRAY) TrayApps: Parsec not running
2026-09-06 18:12:49.024 [INFO] (TRAY) TrayApps: Google Drive not running
2026-09-06 18:12:49.025 [INFO] (TRAY) TrayApps: Jellyfin not running
2026-09-06 18:12:49.026 [INFO] (TRAY) TrayApps: Riot Client not running
2026-09-06 18:12:49.027 [INFO] (TRAY) TrayApps: Riot Vanguard not running
2026-09-06 18:12:49.026 [INFO] (CLEAN) measure 'Old update log archives': 19.5 MB in 67 files - CbsPersist_*.cab older than 30 days, plus all WindowsUpdate logs
2026-09-06 18:12:49.030 [INFO] (CLEAN) measure 'Update reporting log': 684.8 KB in 1 files
2026-09-06 18:12:49.233 [INFO] (CLEAN) measure 'User temp files': 522.1 MB in 694 files - %TEMP% = C:\Users\bd799\AppData\Local\Temp
2026-09-06 18:12:49.235 [INFO] (CLEAN) measure 'Crash dumps': 15.2 MB in 4 files - MEMORY.DMP: not present
2026-09-06 18:12:49.239 [INFO] (CLEAN) measure 'Thumbnail caches': 214.8 MB in 30 files - thumbcache_*.db / iconcache_*.db - Explorer usually locks these
2026-09-06 18:12:49.246 [INFO] (CLEAN) measure 'NVIDIA shader cache (DXCache)': 23.15 GB in 279 files
2026-09-06 18:12:49.247 [INFO] (CLEAN) measure 'NVIDIA shader cache (GLCache)': 64 B in 2 files
2026-09-06 18:12:49.248 [INFO] (CLEAN) measure 'NVIDIA shader cache (NV_Cache)': not present (skipped)
2026-09-06 18:12:49.252 [INFO] (CLEAN) measure 'AMD shader cache (DxCache)': 48.9 MB in 669 files
2026-09-06 18:12:49.253 [INFO] (CLEAN) measure 'AMD shader cache (Dx9Cache)': not present (skipped)
2026-09-06 18:12:49.254 [INFO] (CLEAN) measure 'AMD shader cache (GLCache)': not present (skipped)
2026-09-06 18:12:49.279 [INFO] (CLEAN) measure 'DirectX shader cache (D3DSCache)': 192.5 MB in 234 files
2026-09-06 18:12:49.283 [INFO] (CLEAN) measure 'Per-user error reports': 0 B in 0 files - ReportQueue: not present; ReportArchive: not present; items older than 7 days under C:\Users\bd799\AppData\Local\Microsoft\Windows\WER (the ProgramData WER trees belong to StorageCleaner)
2026-09-06 18:12:49.297 [INFO] (CLEAN) measure 'Setup & upgrade logs': 1.1 MB in 1 files - setupapi*.log.old/*.old logs: none found; files older than 30 days in MoSetup/DISM/SIH; C:\Windows setupapi*.log.old/*.old only (the active setupapi.dev.log is never touched); Panther top-level setup*.log/.etl/.xml only (setupact.log/setuperr.log and subdirectories are never touched)
2026-09-06 18:12:50.990 [INFO] (MONITOR) monitor: gpu metrics via nvidia-smi (C:\Windows\System32\nvidia-smi.exe)
2026-09-06 18:12:51.113 [INFO] (MONITOR) monitor: gpu metrics unavailable (nvidia-smi run failed)
2026-09-06 18:12:52.670 [INFO] (CLEAN) measure 'Previous Windows installations (report only)': 549.93 GB in 82,021 files - Windows.old: 549.93 GB in 82,014 files - 58 junction/symlink folder(s) skipped; $WINDOWS.~BT: not present; $WINDOWS.~WS: 361.6 KB in 7 files; report only - deletion is rejected by design (D3 Tier 3); DeepClean implements no deletion for this category
2026-09-06 18:12:52.684 [INFO] (GPU) measure 'NVIDIA shader cache (DXCache)': 23.15 GB in 279 files
2026-09-06 18:12:52.685 [INFO] (GPU) measure 'NVIDIA shader cache (GLCache)': 64 B in 2 files
2026-09-06 18:12:52.686 [INFO] (GPU) measure 'NVIDIA shader cache (NV_Cache)': not present (skipped)
2026-09-06 18:12:52.689 [INFO] (GPU) measure 'AMD shader cache (DxCache)': 48.9 MB in 669 files
2026-09-06 18:12:52.689 [INFO] (GPU) measure 'AMD shader cache (Dx9Cache)': not present (skipped)
2026-09-06 18:12:52.689 [INFO] (GPU) measure 'AMD shader cache (GLCache)': not present (skipped)
2026-09-06 18:12:52.711 [INFO] (GPU) measure 'DirectX shader cache (D3DSCache)': 192.5 MB in 234 files
2026-09-06 18:12:52.711 [INFO] (GPU) measure 'NVIDIA driver installer leftovers (C:\NVIDIA)': not present (skipped)
2026-09-06 18:12:52.712 [INFO] (GPU) measure 'NVIDIA driver download cache (Downloader)': not present (skipped)
2026-09-06 18:12:52.898 [INFO] (CLEAN) measure 'Google Chrome': 10.9 MB in 70 files
2026-09-06 18:12:52.910 [INFO] (CLEAN) measure 'Microsoft Edge': 352.5 MB in 2,703 files
2026-09-06 18:12:52.935 [INFO] (CLEAN) measure 'Brave': 1.27 GB in 8,371 files - app running (brave) - close it to clean its cache
2026-09-06 18:12:52.937 [INFO] (CLEAN) measure 'Vivaldi': not present (skipped)
2026-09-06 18:12:52.938 [INFO] (CLEAN) measure 'Opera': not present (skipped)
2026-09-06 18:12:52.938 [INFO] (CLEAN) measure 'Firefox': not present (skipped)
2026-09-06 18:12:52.981 [WARN] (APP) appcache: forbidden name 'Favicons' blocked: C:\Users\bd799\AppData\Local\Steam\htmlcache\Default\Favicons
2026-09-06 18:12:52.985 [WARN] (APP) appcache: forbidden name 'History' blocked: C:\Users\bd799\AppData\Local\Steam\htmlcache\Default\History
2026-09-06 18:12:52.986 [WARN] (APP) appcache: forbidden name 'IndexedDB' blocked: C:\Users\bd799\AppData\Local\Steam\htmlcache\Default\IndexedDB
2026-09-06 18:12:52.986 [WARN] (APP) appcache: forbidden name 'Local Storage' blocked: C:\Users\bd799\AppData\Local\Steam\htmlcache\Default\Local Storage
2026-09-06 18:12:52.987 [WARN] (APP) appcache: forbidden name 'Login Data' blocked: C:\Users\bd799\AppData\Local\Steam\htmlcache\Default\Login Data
2026-09-06 18:12:52.987 [WARN] (APP) appcache: forbidden name 'Cookies' blocked: C:\Users\bd799\AppData\Local\Steam\htmlcache\Default\Network\Cookies
2026-09-06 18:12:52.990 [WARN] (APP) appcache: forbidden name 'Session Storage' blocked: C:\Users\bd799\AppData\Local\Steam\htmlcache\Default\Session Storage
2026-09-06 18:12:52.993 [WARN] (APP) appcache: forbidden name 'Sync Data' blocked: C:\Users\bd799\AppData\Local\Steam\htmlcache\Default\Sync Data
2026-09-06 18:12:52.993 [WARN] (APP) appcache: forbidden name 'Web Data' blocked: C:\Users\bd799\AppData\Local\Steam\htmlcache\Default\Web Data
2026-09-06 18:12:52.996 [WARN] (APP) appcache: forbidden name 'IndexedDB' blocked: C:\Users\bd799\AppData\Local\Steam\htmlcache\Default\WebStorage\1\IndexedDB
2026-09-06 18:12:52.998 [WARN] (APP) appcache: forbidden name 'IndexedDB' blocked: C:\Users\bd799\AppData\Local\Steam\htmlcache\Default\WebStorage\2\IndexedDB
2026-09-06 18:12:53.024 [INFO] (CLEAN) measure 'Steam': 658.9 MB in 5,306 files - app running (steam, steamwebhelper) - close it to clean its cache; 11 unreadable
2026-09-06 18:12:53.032 [INFO] (CLEAN) measure 'Discord': 348.2 MB in 2,106 files - app running (Discord) - close it to clean its cache
2026-09-06 18:12:53.032 [INFO] (CLEAN) measure 'Epic Games Launcher': not present (skipped)
2026-09-06 18:12:53.324 [INFO] (CLEAN) measure 'Battle.net': 67.3 MB in 881 files
2026-09-06 18:12:53.515 [INFO] (CLEAN) gates: 4 block reason(s)
2026-09-06 18:12:53.528 [WARN] (APP) cleanup gate: File rename operations are pending for the next boot (Session Manager PendingFileRenameOperations) - restart Windows before cleaning.
2026-09-06 18:12:53.528 [WARN] (APP) cleanup gate: Windows Update service 'wuauserv' is Running - do not clean the Windows Update caches while it is busy.
2026-09-06 18:12:53.528 [WARN] (APP) cleanup gate: Windows Update service 'bits' is Running - do not clean the Windows Update caches while it is busy.
2026-09-06 18:12:53.529 [WARN] (APP) cleanup gate: Windows Update service 'UsoSvc' is Running - do not clean the Windows Update caches while it is busy.
2026-09-06 18:14:27.299 [INFO] (FREEZE) ProcessFreezer: nothing was frozen this session - nothing to resume
2026-09-06 18:14:27.303 [INFO] (POWER) power: nothing to restore
2026-09-06 18:14:27.306 [INFO] (POWER) wu-pause: nothing to resume
2026-09-06 18:14:27.307 [INFO] (APP) restore: eco-safe session restore done
2026-09-06 18:14:27.310 [INFO] (MONITOR) monitor: stopped
2026-09-06 18:14:27.363 [INFO] (APP) === Session end: main window closed (total 00:01:45) ===
```

Key reading of the log (all confirmed by code inspection):
- Probe/precheck/measure/gates/tray-detect/monitor-start all worked.
- "UI: selection stage" — then **no GO**: nothing between 18:12:53 and
  18:14:27 except window close. The stage's contents were invisible (§2.1).
- The eco-safe restore and clean shutdown worked.

## §6 Environment, build and conventions (short form — full detail in HANDBOOK §2/§7)

- Compiler: `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` only,
  **C# 5 syntax only** (no `$"..."`, `?.`, `nameof`, `=>` members, out-var,
  expression bodies). Build: `cmd /c "src\build.cmd"` from repo root →
  `dist\Go Time.exe` + `dist\Eco Mode.exe` (gitignored).
- Two `/define` targets (`MODE_STANDARD`/`MODE_ECO`); single-target code
  wrapped in `#if`. Allowed references fixed (HANDBOOK §2.4). requireAdministrator
  manifest → UAC prompt is expected; runtime verification is the manual
  checklist in `BUILD_NOTES.md`.
- Version constant: `src/App.cs:148` (`Program.Version`).
- Logging: `Log` static (`Logger.cs`), channels APP/CLEAN/GPU/FREEZE/POWER/
  TRAY/MONITOR/PROFILE/SESSION; every side effect logged; best-effort steps
  individually try/caught; UI updates via RunBg + SafeInvoke.
- Commit style: conventional prefixes (`fix:`, `feat:`, `docs:`) as in the
  existing history. Publish to `bigthabot` (and the `ryanthabot` mirror when
  credentials allow). GitHub Release per version with both exes attached.

## §7 Single-app design sketch (owner requirement, 2026-09-07 — target v1.2.0)

The owner wants one executable, not two. What changes and what doesn't:

**The new shape**
- One exe — proposed name `GPU Mode Switch.exe` (matches the repo; confirm
  the name/icon preference with the owner before building the release).
  `build.cmd` drops to a single csc invocation; the `MODE_STANDARD` /
  `MODE_ECO` defines and every `#if` region go away — both paths compile in
  and the mode is chosen at RUNTIME.
- Launch → probe → a new **home screen** (new `UiPhase.Home` before
  Confirm/Select): current GPU state (dGPU on/off, Energy Saver state) plus
  two big actions — **Go Time (Standard)** and **Eco Mode**. The action for
  the mode you are already in shows as active/disabled with an "already in
  this mode" note (it can still be pressed to re-sync Energy Saver/Power
  Mode and, for eco, run the restore).
- Go Time action → the existing selection stage (once the §2.1 fix lands)
  → GO → switch + session features. Behavior unchanged.
- Eco action → the existing one-click flow (review stage with `--confirm`)
  → switch + `SessionSafety.RestoreAll`. Behavior unchanged.
- Session tray "Restore (Eco Mode)" becomes first-class in v1.2.0: today it
  only undoes session state and leaves the GPU switch to the other exe (per
  old D4); in the single app it can run the full eco switch in-process.
- CLI: `--gotime` / `--eco` preselect the mode (skipping the home screen);
  `--auto` keeps its Go-Time-only meaning, `--confirm` and `--status`
  unchanged. A desktop shortcut with `--eco` reproduces today's one-click
  Eco Mode experience.
- Logging: `Log.BeginSession` already names the folder from the app name —
  pass the RUN's mode ("Go Time"/"Eco Mode") so the existing
  `logs\GoTime|EcoMode` split and the Log History browser keep working
  unchanged.

**What barely changes** — `AsusControl`, `EnergySaver`, `GamePrep`, all
cleaners/freezer/plans/monitor/overlay/profiles/history modules are
mode-agnostic already. The real work is `App.cs` (identity by define →
runtime mode), `Forms.cs` (home phase + un-`#if` the two flows), `build.cmd`
(one invocation, one icon set, both 256px PNGs embedded if per-mode artwork
is kept in the UI), and the release process (one exe attached).

**Versioning** — v1.1.1 first (selection-stage fix + invisible Energy
Saver + CHANGELOG/portability discipline), verified via the BUILD_NOTES
checklist; THEN v1.2.0 (this merge) as its own release. Doing the fix
first matters: the fix is one line in the current structure and
independently verifiable; the merge then restructures around known-good
code. If the owner prefers one combined release, fold this into v1.1.1 —
the decision belongs to the owner, not the agent.

## §8 The complete updated owner prompt (copy-paste ready)

> i am currently working on a project found at this github address
> https://github.com/bigthabot/asus-gpu-mode-switch/releases/tag/v1.1.0
>
> before writing any code: read `docs/HANDBOOK.md` (hard constraints: C# 5
> only, the Windows-shipped csc.exe compiler, no NuGet/SDK — see its §2) and
> `docs/HANDOFF_v1.1.1.md` (the full current-state inventory, verified root
> causes with file:line pointers, and the v1.1.1 scope). the handoff doc
> lives on the ryanthabot mirror at commit `05862fb` (one commit ahead of
> the bigthabot repo above — pull it, or clone
> https://github.com/ryanthabot/asus-gpu-mode-switch); both carry v1.1.0
> code (commit `5df43b7`).
>
> "eco mode" correctly switches off the dgpu as well as engages windows'
> energy saver toggle. i would like both apps to be able to switch that
> ability without popping up a settings window i.e. i want it to be invisible
> on screen. "go-time" shows no options to select, and is not correctly
> cycling the dgpu, no optimizations are available to select, no cleanup.
> no tray icon showed up and i didn't see a way to engage an overlay.
>
> root causes are already verified — fix, don't re-diagnose:
> 1. the go time selection stage is invisible because the panel that holds
>    every option group is created hidden (`src/Forms.cs:1142`) and
>    `EnterSelect` (`src/Forms.cs:1624`) never shows it — the only place it
>    becomes visible is `EnterResultTraySection` (`src/Forms.cs:1422`),
>    which runs AFTER the switch. because GO showed nothing to select, GO
>    was never pressed in the v1.1.0 run, so the dgpu cycle, optimizations,
>    cleanup, tray and overlay never ran at all — they all live behind GO.
>    fix the visibility in `EnterSelect` and regression-check the layout at
>    100% and 150% DPI.
> 2. the energy saver popup: `EnergySaver.Sync` (`src/EnergySaver.cs:119`)
>    drives the real toggle through visible Settings UI automation
>    (`ms-settings:powersleep` launched at line 266, real mouse movement at
>    lines 182–194). the existing silent fallback (`SetAutoThreshold`,
>    line 358) writes a legacy power value that FAILS on windows 24H2+/26200
>    (rc=2 — energy saver moved to the whesvc service; the code documents
>    this at lines 379–384), which is why the visible path is primary.
>    research a genuinely silent path on build 26200 first (verify whether
>    whesvc honors the EnergySaverState registry value live, or another
>    API); if none exists, use UIA TogglePattern instead of mouse movement,
>    never touch the user's own settings window, always close what you
>    opened, and tell me honestly what is still visible. call sites:
>    `src/AsusControl.cs:456` and `:563` (both apps, both the already-in-mode
>    and switched paths).
> 3. in the v1.1.0 log, the four cleanup-gate blocks, the "nvidia-smi run
>    failed" warning (dGPU was off — expected), and the Windows.old
>    "report only" line are all correct behavior, not bugs. do not chase them.
>
> i want to keep a running changelog of all changes made to both applications
> kept in one document that now automatically updates after each release that
> is human readable text, as well as a separate file doing the same so that
> this particular software package can be brought to and from, launched and
> edited from different pc's. continue keeping a full release history on
> github adding a new version everytime a new error is repaired and a new
> feature is added.
>
> also: i wanted this to be a single app, not 2 individual apps. today the
> project ships two executables (Go Time.exe and Eco Mode.exe) built from
> one shared codebase with two compiler defines. starting with the release
> AFTER the v1.1.1 fixes (call it v1.2.0), merge them into ONE executable
> with both modes inside: launch shows the current gpu state and two
> actions (Go Time / Eco Mode); each action keeps its existing behavior
> (selection stage + GO for go time, one-click or --confirm for eco); the
> session tray's "Restore (Eco Mode)" runs the full eco switch in-process;
> the cli gains --gotime / --eco; build.cmd becomes a single compile with
> no MODE_STANDARD/MODE_ECO defines. the full design sketch is
> docs/HANDOFF_v1.1.1.md §7 — follow it, and confirm the final app name
> and icon with me before building that release.
>
> there is a known issue that has already been found. check documents and any
> associated places that that information may be documented. research known
> issue and resolve. major changes that were applied were done to "go time".
> the log from the most recent instance of the software (v1.1.0) is pasted
> below in its entirety if needed. append to ver 1.1.1
>
> (the complete v1.1.0 session log is in `docs/HANDOFF_v1.1.1.md` §5 —
> read it there; it is the same log pasted to the previous agent)
