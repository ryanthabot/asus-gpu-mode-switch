# Commit history — asus-gpu-mode-switch v1.1.0

Complete commit history of the v1.1.0 build (branch `v1.1-logging-cleanup`),
generated from `git log 4260aef..cdb52f2` (v1.0.22 baseline → v1.1.0 release).
The build was produced by eighteen sub-agents across six orchestrated waves;
every commit carries a detailed body per the project's commit discipline
(what changed file-by-file, decisions made, verification evidence, next steps).

commit 6b435a2b9c0466badab8873faac27e4d7185ef73
Author:     bigthabot <325692204+bigthabot@users.noreply.github.com>
AuthorDate: Sun Sep 6 14:27:30 2026 -0400
Commit:     bigthabot <325692204+bigthabot@users.noreply.github.com>
CommitDate: Sun Sep 6 14:27:30 2026 -0400

    chore(bootstrap): clone baseline v1.0.22 and create living handbook
    
    Wave 1 / Agent A1 (Bootstrap) of the v1.1.0 build. No source files were
    modified in this wave - documentation and verification only.
    
    Files changed:
    
    * BUILD_NOTES.md (NEW, repo root)
      - Baseline verification record: date 2026-09-06, baseline v1.0.22
        (main @ 4260aef), branch v1.1-logging-cleanup created from it.
      - Exact compiler path used: C:\Windows\Microsoft.NET\Framework64\
        v4.0.30319\csc.exe, banner "Microsoft (R) Visual C# Compiler
        version 4.8.9221.0 / for C# 5".
      - Baseline build output summary: cmd //c "src\build.cmd" from repo
        root -> exit code 0, zero csc diagnostics (/nologo, clean compile
        for both MODE_STANDARD and MODE_ECO), output captured verbatim:
            Build OK:
            Eco Mode.exe
            Go Time.exe
        Artifacts: dist\Go Time.exe (92,672 bytes), dist\Eco Mode.exe
        (86,016 bytes), both anycpu winexe with requireAdministrator
        manifest; UIAutomation refs resolved from GAC, WindowsBase from
        the Framework64 WPF dir.
      - Full repo file inventory at baseline (src\GpuModeSwitch.cs 115,422
        bytes / 2,678 lines single-source, app.manifest, build.cmd, two
        .ico + two 256px PNG resources, icon tooling scripts, README,
        LICENSE, .gitignore; dist\ gitignored).
      - Notes: --status smoke run deferred (requireAdministrator manifest
        pops UAC + modal MessageBox; manual verify step, see handbook S7).
    
    * docs/HANDBOOK.md (NEW, 8 sections, living handoff document)
      - S1 Project summary: what the two exes do, single-source dual-target
        build (MODE_STANDARD -> Go Time.exe, MODE_ECO -> Eco Mode.exe via
        csc.exe), hardware transport chain (\.\ATKACPI DeviceIoControl
        0x0022240C DSTS/DEVS -> WMI AsusAtkWmi_WMNB -> WMI ASUS_WMI), dGPU
        power/MUX device IDs, CLI flags (--confirm/--auto/--status).
      - S2 Hard constraints: v4.0.30319 csc.exe only, C# 5 only (no string
        interpolation, no ?., no expression-bodied members, no nameof),
        .NET Framework 4.x, no NuGet/external libraries (allowed refs:
        System.*, System.Core, System.Drawing, System.Windows.Forms,
        System.Management, System.ServiceProcess, System.Web.Extensions,
        WindowsBase, UIAutomationClient/Types), requireAdministrator
        manifest, two /define targets, never push, never touch source
        outside your wave.
      - S3 Architecture map: responsibility + key public API for every
        class in GpuModeSwitch.cs (Program, Logger, AsusTransport,
        AtkAcpiTransport, WmiTransport, SwitchOutcome, AsusControl,
        GpuServices, GamePrep, EnergySaver, WindowIcons, UiShapes,
        ShimmerBar, LogForm, TrayAppInfo/TrayApps, MainForm) plus the
        planned Wave 2 file split (App.cs, Theme.cs, AsusControl.cs,
        EnergySaver.cs, GamePrep.cs, Logger.cs, Forms.cs) and future
        modules (LogBrowser, StorageAnalyzer, StorageCleaner,
        ComponentStore, AppCacheCleaner, DeepClean, GpuTools,
        ProcessFreezer, PowerPlans, Profiles, SessionHistory,
        SystemMonitor, Overlay, TrayIcon).
      - S4 Design decisions log (append-only), seeded D1-D7: logging
        contract (per-run timestamped files, line format
        "yyyy-MM-dd HH:mm:ss.fff [LEVEL] (channel) message", channels
        CLEAN/GPU/FREEZE/POWER/TRAY/MONITOR/PROFILE/SESSION, retention
        30 days then oldest-first to 200 MB, never delete today's logs);
        storage cleanup tiers (Tier 1 safe cache purge incl.
        SoftwareDistribution\Download with wuauserv/bits/usosvc stop+
        restart+DetectNow, Delete-DeliveryOptimizationCache -Force,
        Windows\Temp >7d, WER, CbsPersist_*.cab, WU ETL; Tier 2 DISM
        /AnalyzeComponentStore -> StartComponentCleanup only if
        recommended; Tier 3 /ResetBase + Windows.old REMOVED/rejected);
        cleanup lives in the GO flow as checkbox groups; suite stays two
        exes with a session-only tray icon in Go Time; browser/app cache
        cleaner is CACHE-ONLY (never cookies/history/passwords/sessions/
        bookmarks/localStorage); monitor = panel tab + overlay; safety
        gates (reboot pending / WU busy / not elevated block cleanup;
        never touch WinSxS contents, catroot, catroot2, C:\Windows\
        Installer, Servicing, pending.xml).
      - S5 Feature checklist: 18-row ownership table A1-A18 (A1 done,
        all others not started).
      - S6 Progress log: Wave 1/A1 completion entry with build evidence.
      - S7 Build & verify: exact commands (cd repo root; cmd //c
        "src\build.cmd"; check dist; optional read-only
        "dist\Go Time.exe" --status with UAC caveat). Expected: clean
        csc compile, two exes in dist\.
      - S8 Next steps: Wave 2 (A2) multi-file decomposition with zero
        behavior change, then Wave 3 (A3) Logger.cs -> Log contract
        (contract included verbatim: Info/Warn/Error/Chan/BeginSession/
        EndSession/CurrentLogPath/LogsRoot/ListLogs).
    
    Verification performed this wave:
    
    * git clone of ryanthabot/asus-gpu-mode-switch: clean, main @ 4260aef
      (v1.0.22), then branch v1.1-logging-cleanup created and checked out.
    * Baseline build re-run on this machine: exit 0, no compiler output,
      Build OK: Eco Mode.exe / Go Time.exe (see BUILD_NOTES.md).
    * csc.exe banner check confirms C# 5 language level constraint.
    
    Next: Wave 2 - A2 multi-file decomposition of src\GpuModeSwitch.cs
    (App.cs, Theme.cs, AsusControl.cs, EnergySaver.cs, GamePrep.cs,
    Logger.cs, Forms.cs) with zero behavior change, then Wave 3 - A3
    logging rewrite to the Log contract.

 BUILD_NOTES.md   | 105 +++++++++++++++++
 docs/HANDBOOK.md | 349 +++++++++++++++++++++++++++++++++++++++++++++++++++++++
 2 files changed, 454 insertions(+)

commit 5739bbbdc205c93c2373bc3e308edff4296ea503
Author:     bigthabot <325692204+bigthabot@users.noreply.github.com>
AuthorDate: Sun Sep 6 14:43:54 2026 -0400
Commit:     bigthabot <325692204+bigthabot@users.noreply.github.com>
CommitDate: Sun Sep 6 14:43:54 2026 -0400

    refactor(split): decompose GpuModeSwitch.cs into seven module files, no behavior change
    
    Wave 2 (agent A2): the single 2,678-line src/GpuModeSwitch.cs was split into
    seven module files under src/, moving whole classes verbatim - no logic
    edits, no renames, no formatting churn beyond the file headers and per-file
    using lists. Every '#if MODE_STANDARD / #if MODE_ECO' region stayed inside
    one file, so the two-target conditional compilation behaves exactly as
    before. Each class now exists exactly once across the codebase.
    
    Class -> file map (namespace GpuModeSwitch):
    - src/App.cs: Program (plus the original v1.0.x file header/history)
    - src/Logger.cs: Logger (behavior unchanged this wave; Log contract lands in Wave 3)
    - src/AsusControl.cs: AsusTransport, AtkAcpiTransport, WmiTransport,
      SwitchOutcome, AsusControl, GpuServices
    - src/GamePrep.cs: GamePrep
    - src/EnergySaver.cs: EnergySaver
    - src/Theme.cs: WindowIcons, UiShapes, ShimmerBar
    - src/Forms.cs: MainForm, LogForm, UiPhase, TrayAppInfo, TrayApps
      (the TrayApps block keeps its #if MODE_STANDARD wrapper)
    
    build.cmd changes:
    - both csc invocations now compile all src/*.cs instead of the single
      src/GpuModeSwitch.cs
    - added /r:System.Web.Extensions.dll to both invocations;
      System.Management.dll and System.ServiceProcess.dll were already
      referenced - all three are pre-approved for later waves (avoids build
      churn when A7/A9/A12 land)
    - the two /define targets, /win32manifest:app.manifest, win32 icons,
      embedded appicon.png resources and output names are unchanged
    
    Verification evidence:
    - cmd /c "src\build.cmd" from repo root: exit code 0, zero csc diagnostics
      (0 errors, 0 warnings; output is just "Build OK:" + both exes), compiler
      C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe (C# 5)
    - dist/Go Time.exe  = 92,672 bytes == baseline 92,672 (BUILD_NOTES.md)
    - dist/Eco Mode.exe = 86,016 bytes == baseline 86,016 (BUILD_NOTES.md)
    - define split confirmed inside the binaries via UTF-16 string scan:
      "System optimizations" / "GO TIME" only in Go Time.exe;
      "Eco Mode - status" / "ECO MODE" only in Eco Mode.exe
    - exes not executed (requireAdministrator manifest pops UAC unattended;
      --status stays a manual verify step per HANDBOOK section 7)
    
    Handbook updates (docs/HANDBOOK.md):
    - section 1: "one shared C# source file" wording updated to the split
      src/*.cs codebase (minimal touch-up to stay accurate)
    - section 3: architecture map rewritten - new file table, class->file
      table, split marked done with placement notes (WindowIcons lives in
      Theme.cs as a UI primitive; UiPhase enum lives in Forms.cs with MainForm)
    - section 5: A2 row marked done
    - section 6: Wave 2/A2 progress entry appended with build evidence
    - section 8: rewritten to point at Wave 3 (A3 logging core) with the
      LOGGER CONTRACT verbatim
    
    Next: Wave 3 - A3 logging core (static class Log per HANDBOOK section 8).
    Never pushed (per hard constraints).

 docs/HANDBOOK.md     |  155 +--
 src/App.cs           |  162 +++
 src/AsusControl.cs   |  724 ++++++++++++++
 src/EnergySaver.cs   |  450 +++++++++
 src/Forms.cs         |  981 ++++++++++++++++++
 src/GamePrep.cs      |  244 +++++
 src/GpuModeSwitch.cs | 2678 --------------------------------------------------
 src/Logger.cs        |   97 ++
 src/Theme.cs         |  120 +++
 src/build.cmd        |   11 +-
 10 files changed, 2884 insertions(+), 2738 deletions(-)

commit e42503b21b97b13e1f04fe7380ae0843f5e9590b
Author:     bigthabot <325692204+bigthabot@users.noreply.github.com>
AuthorDate: Sun Sep 6 15:05:11 2026 -0400
Commit:     bigthabot <325692204+bigthabot@users.noreply.github.com>
CommitDate: Sun Sep 6 15:05:11 2026 -0400

    feat(logging): per-run timestamped log files with dual sink, session headers and retention
    
    Wave 3 / A3 - logging core rewrite. The old internal static class Logger
    (single %LOCALAPPDATA%\GpuModeSwitch\GoTime.log|EcoMode.log mirror, rewritten
    fresh each run) is replaced by static class Log, the foundation all later
    modules log through. Docs\HANDBOOK.md D1 contract, finalized as D8.
    
    src\Logger.cs (rewritten):
    - static class Log with the exact contract surface: Info(msg), Warn(msg),
      Error(msg, ex = null), Chan(channel, msg), BeginSession(appName, version),
      EndSession(result), CurrentLogPath, LogsRoot, ListLogs(app) (NEWEST first),
      plus Snapshot() returning the full run buffer (consumed by LogForm;
      A4 builds on it).
    - Per-run files: %LOCALAPPDATA%\GpuModeSwitch\logs\<GoTime|EcoMode>\
      <App>_yyyy-MM-dd_HHmmss.log; folder/file prefix mapped from appName
      ("Eco" -> EcoMode, else GoTime). Legacy flat v1.0.x logs stay untouched.
    - Line format: yyyy-MM-dd HH:mm:ss.fff [LEVEL] (channel) message;
      LEVEL INFO/WARN/ERROR, default channel APP; unknown channel tags accepted.
      Error(msg, ex) appends exception type + message + stack on indented
      continuation lines.
    - BeginSession writes the session header block: app + version + active
      /define mode (MODE_STANDARD/MODE_ECO), Windows build from registry
      CurrentBuild + UBR (Environment.OSVersion fallback), machine model via WMI
      Win32_ComputerSystem (failure-tolerant), admin check via WindowsPrincipal
      role Administrator, .NET runtime version. EndSession writes a footer with
      result + total session duration.
    - Dual sink, thread-safe under a single lock: in-memory StringBuilder buffer
      + File.AppendAllText to the run's file; every file IO try/caught - a file
      sink failure disables the sink for the rest of the run with a single WARN
      while the buffer keeps working (logging can never crash or stall the app).
    - Retention on BeginSession, after the header block so removals are captured
      in the new file: delete files older than 30 days, then if the folder total
      still exceeds 200 MB delete oldest-first by the <App>_yyyy-MM-dd_HHmmss
      timestamp parsed from the file name (LastWriteTime fallback) until
      <= 200 MB; NEVER files stamped today and NEVER the current log; removals
      logged as "retention: removed <name> (age N days)" / "(size cap 200 MB)".
    
    Call-site migration - all 130 old Logger call sites converted, identical
    message text, zero Logger references left (grep-verified):
    - src\App.cs (2): Logger.Init -> Log.BeginSession("Go Time"/"Eco Mode",
      Program.Version); Log.EndSession added on both exit paths ("status shown",
      "main window closed") so the footer is actually written.
    - src\AsusControl.cs (44): 42 lines -> Log.Chan("GPU", ...), the Power Mode
      overlay line -> Log.Chan("POWER", ...), DescribeState -> Log.CurrentLogPath.
    - src\EnergySaver.cs (39): all -> Log.Chan("POWER", ...).
    - src\GamePrep.cs (18): all -> Log.Info(...).
    - src\Forms.cs (27): 14 TrayApps lines -> Log.Chan("TRAY", ...), 6 UI lines
      -> Log.Info(...), the UNEXPECTED ERROR / BACKGROUND ERROR reports ->
      Log.Error("...", ex), and LogForm now reads Log.Snapshot() (text box +
      Copy log) and Log.CurrentLogPath (path strip + clipboard-failure dialog).
      LogForm change kept minimal by design: same viewer, pre-selected text,
      path strip and Copy log behavior; A4 upgrades it properly.
    No dead wrappers: the Logger class is gone.
    
    Verification:
    - cmd /c "src\build.cmd" from repo root -> exit 0, zero csc diagnostics
      (csc 4.8.9221.0 for C# 5 is silent on success), output "Build OK:" with
      "Eco Mode.exe" and "Go Time.exe". Artifacts: dist\Go Time.exe 99,328 bytes
      (baseline 92,672), dist\Eco Mode.exe 92,160 bytes (baseline 86,016) -
      ~6 KB growth from the Log core. Exes not run (requireAdministrator
      manifest pops UAC unattended).
    - Out-of-tree smoke test of src\Logger.cs (csc-compiled harness, not the
      real exes): per-run file creation, header content (26200.8457,
      "ASUSTeK COMPUTER INC. ROG Strix G513QR_G513QR", admin check, .NET
      4.0.30319.42000), channel tags incl. unknown, WARN, exception
      continuations, footer, ListLogs newest-first, and both retention paths
      with synthetic files (248-day-old file removed "(age 248 days)";
      150 MB + 100 MB over the 200 MB cap -> oldest removed
      "(size cap 200 MB)", newer files and today's logs kept; legacy
      GpuModeSwitch\GoTime.log / EcoMode.log untouched). Test artifacts removed.
    
    docs\HANDBOOK.md:
    - SS3: as-of header -> Wave 3; file table row and class table row for
      Logger.cs -> Log; new "Log API" full-contract table; LogForm row notes
      Snapshot()/CurrentLogPath.
    - SS4: appended D8 (logging line format + retention policy finalized,
      including the GPU/POWER/TRAY migration mapping and Snapshot() addition).
    - SS5: A3 row -> done.
    - SS6: Wave 3/A3 progress entry with the above build + smoke-test evidence.
    - SS8: rewritten for Wave 4 - A4 log window upgrade (Forms.cs LogForm only),
      A5 log browser (LogBrowser.cs, static ShowBrowser(Form owner)), A6 storage
      analyzer (StorageAnalyzer.cs, CleanCategory + CheckGates(), no deletion),
      A12 process freezer (ProcessFreezer.cs, ntdll NtSuspendProcess/
      NtResumeProcess, freezelist.txt, never-freeze guard), A13 power plans +
      WU pauser (PowerPlans.cs), A14 profiles (Profiles.cs, ProfileBar,
      profiles.json), A15 session history (SessionHistory.cs, sessions.jsonl,
      SessionHistoryForm), A16 monitor (SystemMonitor.cs, MonitorPanel).
    
    Next: Wave 4 - A4/A5/A6/A12, then A13/A14/A15/A16.

 docs/HANDBOOK.md   | 191 ++++++++++++++++++-----
 src/App.cs         |   6 +-
 src/AsusControl.cs |  88 +++++------
 src/EnergySaver.cs |  78 +++++-----
 src/Forms.cs       |  54 +++----
 src/GamePrep.cs    |  36 ++---
 src/Logger.cs      | 441 +++++++++++++++++++++++++++++++++++++++++++++++------
 7 files changed, 680 insertions(+), 214 deletions(-)

commit 30554e280e7796c683af1e3e1ed13a64b1c08369
Author:     bigthabot <325692204+bigthabot@users.noreply.github.com>
AuthorDate: Sun Sep 6 16:07:38 2026 -0400
Commit:     bigthabot <325692204+bigthabot@users.noreply.github.com>
CommitDate: Sun Sep 6 16:07:38 2026 -0400

    feat(modules): land eight Wave-4 feature modules behind the Log core
    
    What changed (file by file):
    - src/Forms.cs (A4): LogForm upgraded in place - history dropdown over
      Log.ListLogs (newest first, 'Current session (live)' pinned), severity
      filter All/Info/Warn/Error (parses [LEVEL] prefix, exception
      continuations ride along), case-insensitive Find-next (wraps, Enter
      repeats), Open-folder (explorer /select), Copy log = displayed text +
      new Copy all, path strip tracks the viewed log; 5-row deterministic
      TableLayoutPanel shell preserved (v1.0.19 lesson). MainForm contract
      unchanged.
    - src/LogBrowser.cs (A5, new): LogBrowserForm - both apps' logs merged
      newest-first (App/File/Size/Last write), tail-loading viewer for >2 MB
      files, find-next + search-all across files (cap 500, click-to-open at
      line), Open folder + Copy view + Refresh, FileShare.ReadWrite so the
      live log is viewable, ShowBrowser(Form owner) entry point.
    - src/StorageAnalyzer.cs (A6, new): CleanCategory DTO (Name/Kind/Bytes/
      Files/Notes/RiskLabel/Selected, SizeText) + StorageAnalyzer.MeasureAll()
      over 11 fixed categories + per-path GPU shader caches, CheckGates()
      failing closed (elevation, CBS/WU pending-reboot signals,
      PendingFileRenameOperations, pending.xml, busy wuauserv/bits/UsoSvc/
      DoSvc/TrustedInstaller). Read-only by design; long-path walks via
      kernel32 FindFirstFileW (\?\) because .NET 4.x DirectoryInfo rejects
      the prefix.
    - src/ProcessFreezer.cs (A12, new): ntdll NtSuspendProcess/NtResumeProcess
      freezer with session-tracked PID+name pairs, PID-reuse re-check,
      never-freeze guard (critical system processes + our exes + own PID),
      freezelist.txt persistence seeded from the TrayApps names,
      FreezeSelected/ResumeAll/ResumeAllSafe. Nothing is ever killed.
    - src/PowerPlans.cs (A13, new): PowerPlans.SetUltimate/RestorePrevious
      (powercfg -duplicatescheme e9a42b02-..., GUID cached in powerplan.txt,
      previous plan remembered once, idempotent, never throws) + WuPause.
      PauseUpdates/ResumeUpdates (session-scoped stop/start of
      wuauserv/bits/DoSvc, StartType never changed).
    - src/Profiles.cs (A14, new): Profile + ProfileStore (JavaScriptSerializer
      JSON store at profiles.json, upsert/delete/find, never throws) +
      ProfileBar UserControl (Apply/Save.../Delete/Refresh,
      ApplyRequested/Saved/CollectSelections events, dark ProfileNameDialog).
    - src/SessionHistory.cs (A15, new): SessionRecord + append-only
      sessions.jsonl store (tolerant parse, newest first) + ExportText +
      SessionHistoryForm (list + detail + Export .txt + Copy,
      ShowHistory(Form owner)).
    - src/SystemMonitor.cs (A16, new): MonitorSample DTO + MonitorEngine
      (2s Forms.Timer; CPU/disk PerformanceCounters primed once, RAM via
      GlobalMemoryStatusEx, GPU via nvidia-smi (System32/NVSMI/PATH), CPU temp
      via MSAcpi_ThermalZoneTemperature; unavailability logged once per
      session, never faked) + MonitorPanel (dark TableLayoutPanel grid of
      bars, AttachToEngine/DetachFromEngine).
    - docs/HANDBOOK.md: SS3 as-of Wave 4 + 7 new file rows + 16 new class rows
      + LogForm row replaced + Chan notes updated + future-modules list
      pruned; SS5 eight features marked done; SS6 eight Wave-4 entries +
      orchestrator integrated-build entry appended; SS8 rewritten for Wave 5.
    
    Decisions: follows SS4 D1-D8 (no new decisions; tier/safety/cache-only
    rules unchanged). Parallel-wave rule applied: each agent verified in an
    out-of-tree temp harness (repo build.cmd untouched by agents), returned
    paste-ready handbook text, committed nothing.
    
    Verification:
    - Integrated in-repo build (all 14 source files):
      cmd //c srcbuild.cmd -> exit 0, zero csc diagnostics,
      'Build OK:' + dist\Eco Mode.exe (177,152 B) + dist\Go Time.exe
      (183,808 B). Exes not run (requireAdministrator pops UAC unattended).
    - Per-agent out-of-tree harness builds: both define targets zero
      diagnostics each (A4/A5/A6/A12/A13/A14/A15/A16).
    - A6 live read-only smoke run measured real targets (e.g. NVIDIA DXCache
      23.15 GB in 279 files) and returned 3 correct unelevated gate reasons.
    - A3-style runtime smoke not applicable this wave (no runtime writes;
      sessions.jsonl/freezelist.txt/profiles.json NOT created).
    
    Next: Wave 5 - A7 Tier 1 cleaner, A8 component store, A9 app cache
    cleaner, A10 deep clean, A11 GPU tools, A17 overlay + session tray.

 docs/HANDBOOK.md       | 483 +++++++++++++++++++++----
 src/Forms.cs           | 546 ++++++++++++++++++++++++----
 src/LogBrowser.cs      | 788 ++++++++++++++++++++++++++++++++++++++++
 src/PowerPlans.cs      | 478 +++++++++++++++++++++++++
 src/ProcessFreezer.cs  | 513 +++++++++++++++++++++++++++
 src/Profiles.cs        | 595 +++++++++++++++++++++++++++++++
 src/SessionHistory.cs  | 668 ++++++++++++++++++++++++++++++++++
 src/StorageAnalyzer.cs | 946 ++++++++++++++++++++++++++++++++++++++++++++++++
 src/SystemMonitor.cs   | 947 +++++++++++++++++++++++++++++++++++++++++++++++++
 9 files changed, 5841 insertions(+), 123 deletions(-)

commit 3d6f83888039ce10bf8a9756fe22c4a9aac26ce0
Author:     bigthabot <325692204+bigthabot@users.noreply.github.com>
AuthorDate: Sun Sep 6 17:08:07 2026 -0400
Commit:     bigthabot <325692204+bigthabot@users.noreply.github.com>
CommitDate: Sun Sep 6 17:08:07 2026 -0400

    feat(cleanup+ui): land Wave-5 cleaner/tool modules and overlay/tray
    
    What changed (file by file):
    - src/StorageCleaner.cs (A7, new): Tier 1 executor - WU purge (stop
      UsoSvc->wuauserv->bits bounded 20s, delete CHILDREN of
      SoftwareDistribution\Download only, restart only services it stopped,
      DetectNow via Microsoft.Update.AutoUpdate reflection COM - no dynamic),
      Delivery Optimization via Delete-DeliveryOptimizationCache -Force
      (never -IncludePinnedFiles), temp/WER/CBS-log/crash-dump/thumbnail
      deletions, D7 hard guard IsForbiddenPath before every deletion
      (WinSxS/catroot/catroot2/Installer/Servicing/pending.xml/DataStore),
      long-path kernel32 \?\ walkers, static run gate, before/after
      free-space reporting. CleanResult DTO public for Wave 6.
    - src/ComponentStore.cs (A8, new): DISM AnalyzeComponentStore parser
      (20-min cap, localized-parse-miss tolerant) + gated StartComponentCleanup
      executor (/ResetBase FORBIDDEN by D3 with defensive runtime guard;
      error 1726 = retryable warning; 45-min cap; busy-flag serialization).
    - src/AppCacheCleaner.cs (A9, new): CACHE-ONLY (D5) cleaner for Chrome/
      Edge/Brave/Opera/Vivaldi/Firefox/Steam/Discord/Epic/Battle.net - two
      independent walls: literal cache-name whitelist under explicit per-app
      parents (Steam libs via HKCU SteamPath + libraryfolders.vdf) +
      forbidden-name wall (cookies/history/logins/sessions/bookmarks/etc.)
      checked per candidate segment before any enumerate/delete. Running app
      = never touched (fails closed).
    - src/DeepClean.cs (A10, new): per-user WER reports (>7d), setup/upgrade
      logs (MoSetup/DISM/SIH + aged setupapi*.old + Panther top-level setup
      logs, guarded), and REPORT-ONLY previous-installations category
      (Windows.old measured, never deleted - Tier 3 rejected per D3).
      Ownership split with A7 recorded as decision D9.
    - src/GpuTools.cs (A11, new): canonical shader-cache table (NVIDIA
      DXCache/GLCache/NV_Cache, AMD DxCache/Dx9Cache/GLCache, D3DSCache) +
      confirm-flagged driver leftovers (C:\NVIDIA, NVIDIA Downloader) + HAGS
      toggle (HwSchMode 2/1, value never deleted, reboot note).
    - src/Overlay.cs + src/TrayIcon.cs (A17, new): MonitorOverlayForm
      (frameless TopMost semi-transparent overlay over MonitorEngine,
      ShowWithoutActivation, click-drag, 'monitor off' hint) and SessionTray
      (session-only NotifyIcon, code-drawn icon, five injected callbacks,
      dark context menu) - both compiled into both targets, wired by A18.
    
    Decisions: D9 appended to handbook SS4 (cleanup category ownership split
    A7/A9/A10/A11/A8 - every cleaner returns zeroed 'owned by <module>'
    results for foreign categories). No other decision changes.
    
    Verification:
    - Integrated in-repo build (all 20 source files):
      cmd //c 'src\build.cmd' -> exit 0, zero csc diagnostics, 'Build OK:' +
      dist\Eco Mode.exe (263,680 B) + dist\Go Time.exe (270,336 B).
    - Per-agent out-of-tree harness builds: both define targets zero
      diagnostics each (A7/A8/A9/A10/A11/A17).
    - Read-only Measure smoke runs (no BeginSession, no disk writes):
      - A9: Edge 352.5 MB, Brave 1.26 GB, Steam 674.4 MB (D:\SteamLibrary
        shadercache found via libraryfolders.vdf), Discord 344.7 MB with
        correct running-app warnings.
      - A10: Windows.old measured 549.88 GB / 81,745 files (report-only
        category), Panther setup.etl 1.1 MB correctly identified.
      - A11: NVIDIA DXCache 23.15 GB / 279 files - byte-identical to A6's
        analyzer numbers (path tables cross-validated); HAGS 'Windows
        default (Off)'.
    - Clean()/SetHags()/DISM never executed (deletion/registry/runtime
      exercise is Wave 6 manual verification).
    
    Next: Wave 6 - A18 GO-flow integration (selection-stage Performance +
    Storage cleanup groups, GO sequence, session tray/overlay wiring, Eco
    restore path, version 1.1.0 + README, final verification).

 docs/HANDBOOK.md       |  440 ++++++++++++---
 src/AppCacheCleaner.cs |  971 ++++++++++++++++++++++++++++++++
 src/ComponentStore.cs  |  557 ++++++++++++++++++
 src/DeepClean.cs       | 1328 +++++++++++++++++++++++++++++++++++++++++++
 src/GpuTools.cs        |  978 ++++++++++++++++++++++++++++++++
 src/Overlay.cs         |  404 ++++++++++++++
 src/StorageCleaner.cs  | 1456 ++++++++++++++++++++++++++++++++++++++++++++++++
 src/TrayIcon.cs        |  342 ++++++++++++
 8 files changed, 6390 insertions(+), 86 deletions(-)

commit 7841c88d7e7dd4b5a5568cf4a870a87994842fc2
Author:     bigthabot <325692204+bigthabot@users.noreply.github.com>
AuthorDate: Sun Sep 6 17:47:47 2026 -0400
Commit:     bigthabot <325692204+bigthabot@users.noreply.github.com>
CommitDate: Sun Sep 6 17:58:45 2026 -0400

    feat(go-flow): wire Wave 4-6 modules into the apps (selection stage v1.1, GO session chain, session tray, eco-safe restore)
    
    Forms.cs (Wave 6, A18 - GO-flow integration):
    - MainForm selection stage rebuilt into one scrollable panel
      (_selectPanel); the window grows to 880px (clamped to the working
      area) when the stage shows; result stage keeps the v1.0.22 tray
      picker via a tray-only mode of the same panel
    - New "Performance" group: Freeze background apps (with an "Edit
      list..." FreezeListEditorForm modal over ProcessFreezer.GetUserList/
      SaveUserList; .exe stripped, blank/guarded names refused via
      IsGuarded), Ultimate Performance plan, Pause Windows Update
    - New "Storage cleanup" group: Windows Update cache purge, Component
      store cleanup (DISM), Deep clean, GPU shader caches + one checkbox
      per installed AppCacheCleaner target (running apps start unchecked);
      background RunBg measurement (StorageAnalyzer.MeasureAll, DeepClean.
      Measure, GpuTools.Measure, AppCacheCleaner.Measure, CheckGates)
      appends measured sizes to captions, adds the report-only
      "previous Windows installations" note and disables the whole group
      with reasons when the D7 gates block (gates re-checked again at GO)
    - ProfileBar wired: stable Tag keys (opt.*, tray.*, perf.*, clean.*,
      clean.appcache.<Name>); Apply sets every checkbox by key, ignores
      unknown keys with a log line, never resurrects gate-disabled boxes
    - MonitorPanel section (collapsible, attached at select phase);
      MonitorEngine.Start(2000) at select show / tray creation, Stop on
      session end or close (exactly one engine owner: MainForm)
    - GO chain after the unchanged GPU switch + optimizations (never via
      --auto): FreezeSelected -> SetUltimate -> PauseUpdates -> cleanup
      (StorageCleaner tier-1 by ticked kinds -> ComponentStore.RunCleanup
      -> DeepClean.Clean fresh-measured -> GpuTools.Clean(includeDriver-
      Leftovers:false) -> AppCacheCleaner.Clean); every step try/caught +
      logged; unticked reversible features actively restored
    - SessionRecord appended per GO run (App/Mode/actions/freed bytes/
      duration/error count/result with "free:" total)
    - Result stage: cleanup summary block (per-category Summary + total,
      capped at 12 lines), Log History (LogBrowserForm.ShowBrowser) and
      Session History (SessionHistoryForm.ShowHistory) buttons alongside
      the existing View log
    - SessionTray created after a successful non-auto GO (open/eco-safe
      restore/overlay toggle/status/exit); MonitorOverlayForm created on
      first toggle (Attach, EnsureMonitorEngine)
    - SessionSafety.RestoreAll(): ResumeAllSafe -> RestorePrevious ->
      ResumeUpdates -> tray hide/dispose; called on Eco Mode apply, tray
      restore, background errors (RunBg catch), FormClosed
    - FreezeListEditorForm (new, MODE_STANDARD); tray sections refactored
      (BeginTrayDetect/PopulateTrayRows/LayoutTrayGroup)
    
    App.cs:
    - Program.Version 1.0.22 -> 1.1.0 (+ header history for v1.1.0)
    - Application.ThreadException + AppDomain.UnhandledException hooks:
      Log.Error + SessionSafety.RestoreAll() (never leave a frozen process,
      paused WU or foreign power plan behind)
    
    Verification: integrated build cmd //c "src\build.cmd" -> exit 0, zero
    csc diagnostics on BOTH /define:MODE_STANDARD and /define:MODE_ECO;
    artifacts dist\Go Time.exe 292,864 B, dist\Eco Mode.exe 265,216 B.
    C# 5 banned-syntax scan ($", ?., nameof, =>): 0 hits across src\.
    Define split proven by UTF-16 string scan of both exes (selection-stage
    strings only in Go Time.exe). Out-of-tree READ-ONLY dry run (measure +
    gates only, no Clean calls, no BeginSession, zero disk writes):
    13 analyzer categories (DXCache 23.15 GB, user temp 522.3 MB,
    thumbnails 214.8 MB, WU cache 16.2 MB), DeepClean Windows.old
    549.88 GB report-only, 6 present app-cache targets, 3 unelevated gate
    reasons. Exes not launched (requireAdministrator pops UAC unattended -
    manual checklist in BUILD_NOTES.md).
    
    Next: docs (README v1.1.0, handbook final state, BUILD_NOTES evidence).

 src/App.cs   |   45 +-
 src/Forms.cs | 1336 +++++++++++++++++++++++++++++++++++++++++++++++++++++++---
 2 files changed, 1318 insertions(+), 63 deletions(-)

commit cdb52f2bfc9f37da8c5be428359ab4853b41b5e5
Author:     bigthabot <325692204+bigthabot@users.noreply.github.com>
AuthorDate: Sun Sep 6 17:59:58 2026 -0400
Commit:     bigthabot <325692204+bigthabot@users.noreply.github.com>
CommitDate: Sun Sep 6 17:59:58 2026 -0400

    docs: v1.1.0 release docs, final handbook state, Wave 6 verification evidence
    
    README.md:
    - v1.1.0 version-history entry at the top (changelog style): logging
      rewrite (per-run timestamped files, 30-day/200MB retention, log
      window history/filter/find, Log History browser, Copy log kept),
      storage cleanup (tier 1 WU cache purge + Delivery Optimization +
      temp/WER/logs, tier 2 DISM component store analyze-first, deep
      clean, GPU shader caches, per-app caches CACHE-ONLY, analyze-first
      with measured sizes, D7 safety gates; WinSxS/catroot/Installer/
      Servicing/pending.xml never touched; /ResetBase and Windows.old
      removal deliberately NOT offered), performance features (process
      freezer with never-freeze guard, Ultimate Performance plan,
      session-scoped WU pause), session tray + overlay, named profiles,
      session history, live monitor
    - --auto semantics documented: the new groups never run unattended
    - new "Logging" section (paths, line format, retention, how to find
      logs via View log / Log History / Copy log) + "Storage cleanup &
      session features (safety model)" section
    - stale per-run log path reference fixed (GoTime.log/EcoMode.log are
      retired); Building-from-source tree updated to the module layout +
      docs/HANDBOOK.md pointer
    
    BUILD_NOTES.md (Wave 6 section appended):
    - integrated build evidence: both defines zero diagnostics,
      Go Time.exe 292,864 B / Eco Mode.exe 265,216 B
    - static checks: C# 5 banned-syntax scan 0 hits; UTF-16 define-split
      string scan of both exes
    - reachability wiring table (exact Forms.cs wiring points for LogForm
      Copy log, LogBrowserForm, SessionHistoryForm, ProfileBar,
      MonitorPanel, SessionTray, overlay, GO chain, SessionSafety)
    - analyze-only dry run numbers (13 analyzer categories, DXCache
      23.15 GB, user temp 522.3 MB, Windows.old 549.88 GB report-only,
      6 present app-cache targets, 3 unelevated gate reasons; NO Clean
      calls in the harness, zero disk writes)
    - 10-item MANUAL VERIFICATION CHECKLIST (UAC-gated: --status,
      selection stage, profiles, freeze-list editor, real GO with
      cleanup, tray menu + overlay drag, Eco round-trip, abnormal-exit
      restore, retention prune, log browser)
    
    docs/HANDBOOK.md:
    - header + §1 summary now reflect the completed v1.1.0 (selection
      stage groups, --auto note)
    - §3 marked FINAL: App.cs row (1.1.0 + crash hooks), Forms.cs row
      (Wave 6 wiring), new SessionSafety/FreezeListEditorForm rows,
      MainForm row rewritten (selection stage v1.1, GO chain, tray,
      result stage), LogBrowser/SessionHistory wiring notes updated
    - §5: A18 done (commit 7841c88)
    - §6: final Wave 6 entry with build/dry-run/manual-checklist evidence
    - §8 rewritten: project complete + how to continue (manual UAC
      verification, release steps, future ideas incl. the HAGS toggle
      that is deliberately not in the default flow)
    
    Next: human UAC-gated verification per BUILD_NOTES.md, then release.

 BUILD_NOTES.md   | 154 +++++++++++++++++++++++++++++
 README.md        | 119 ++++++++++++++++++++++-
 docs/HANDBOOK.md | 291 ++++++++++++++++++++++++++++++++++++-------------------
 3 files changed, 462 insertions(+), 102 deletions(-)

------------------------------------------------------------------------

# v1.1.1 commits (post-release handoff + fix release, 2026-09-07)

Generated from `git log cdb52f2..0ac721b` (v1.1.0 release -> v1.1.1 release),
authored on the ryanthabot mirror with the same commit discipline.

commit 5df43b757efdd3acb24f30aec0dd17382e109f73
Author: bigthabot <325692204+bigthabot@users.noreply.github.com>
Date:   2026-09-07 16:27:49 -0400

    docs(project-contents): add self-contained review bundle for off-repo viewing
    
    What changed (file by file):
    - project contents/CONTENTS.md (new): index of the bundle — what each
      file is, where the living copies sit in the repo, publish state
      (branch/main/tag/release), and the known v1.1.0 issue (Go Time
      selection stage can render empty; fix planned as v1.1.1).
    - project contents/HANDBOOK.md (new): snapshot of docs/HANDBOOK.md at
      the v1.1.0 release — the full handoff document (constraints, module
      API map, decisions D1-D9, checklist, wave progress log with
      verification evidence, build/verify commands).
    - project contents/README.md, BUILD_NOTES.md, LICENSE (new): snapshots
      of the root docs so the bundle is self-contained.
    - project contents/COMMIT_HISTORY.md (new, generated): full commit
      details of the v1.1.0 build — git log 4260aef..cdb52f2 with full
      messages and per-commit file stats, chronological order.
    
    Why: repo owner asked for the entire project (docs, commit details,
    handoff document, handbook, markdown files) to be published in a
    'project contents' folder so the project can be viewed in its entirety
    elsewhere. Snapshots are taken at v1.1.0 (cdb52f2); the living copies
    under docs/ remain the canonical versions for future waves.
    
    Verification: folder contents listed (6 files); COMMIT_HISTORY.md is
    608 lines covering all 7 build commits; no source files touched;
    working tree clean after commit.
    
    Next: push v1.1-logging-cleanup, fast-forward main, tag v1.1.0, create
    the GitHub release with the two built executables attached.

 project contents/BUILD_NOTES.md    |  259 ++++++++
 project contents/COMMIT_HISTORY.md |  608 ++++++++++++++++++
 project contents/CONTENTS.md       |   39 ++
 project contents/HANDBOOK.md       | 1227 ++++++++++++++++++++++++++++++++++++
 project contents/LICENSE           |   21 +
 project contents/README.md         |  365 +++++++++++
 6 files changed, 2519 insertions(+)

commit 05862fb6edd19b312a3fc0644f0bf393d352b35d
Author: ryanthabot <258673122+ryanthabot@users.noreply.github.com>
Date:   2026-09-07 19:00:12 -0400

    docs(handoff): v1.1.1 handoff — verified post-release root causes, current-state inventory, owner prompt
    
    - docs/HANDOFF_v1.1.1.md: behavior inventory (runtime-verified vs
      build-verified vs broken), root causes with file:line pointers for the
      invisible Go Time selection stage (Forms.cs:1142/1624/1422) and the
      obtrusive Energy Saver Settings automation (EnergySaver.cs:119/266/
      182-194, silent fallback dead on 24H2+/26200 per lines 379-384),
      known non-issues explained from the v1.1.0 log, v1.1.1 scope
      (fix + invisible ES + CHANGELOG.md + portability file + release
      discipline), the complete v1.1.0 session log, and the updated
      copy-paste owner prompt.
    - HANDBOOK: append-only §6 post-release entry + §8 post-release
      known-issues block (resolves the dangling CONTENTS.md pointer).

 docs/HANDBOOK.md       |  40 ++++++
 docs/HANDOFF_v1.1.1.md | 359 +++++++++++++++++++++++++++++++++++++++++++++++++
 2 files changed, 399 insertions(+)

commit e0042b94b8c727c3cd4cb92d8422655385c7e2cc
Author: ryanthabot <258673122+ryanthabot@users.noreply.github.com>
Date:   2026-09-07 19:01:04 -0400

    docs(handoff): note exact repo sync state (ryanthabot ahead by the handoff commits; how to sync bigthabot)

 docs/HANDOFF_v1.1.1.md | 17 ++++++++++++-----
 1 file changed, 12 insertions(+), 5 deletions(-)

commit fd3e655056d8ab48edded4ed58096396b8ba4b93
Author: ryanthabot <258673122+ryanthabot@users.noreply.github.com>
Date:   2026-09-07 19:04:54 -0400

    docs(requirement): owner wants a single app (both modes in one exe) — D10 supersedes D4
    
    - HANDOFF_v1.1.1.md: new §7 merge design sketch (home screen, runtime mode
      selection, --gotime/--eco, single compile, tray restore goes in-process,
      what barely changes), scope note (v1.2.0 after the v1.1.1 fixes), §4 item
      7, and the requirement added to the embedded owner prompt.
    - HANDBOOK: D10 appended (append-only), §2.6 annotated as superseded from
      v1.2.0, §8 known-issues block gains the requirement.

 docs/HANDBOOK.md       | 17 +++++++++++++
 docs/HANDOFF_v1.1.1.md | 68 +++++++++++++++++++++++++++++++++++++++++++++++++-
 2 files changed, 84 insertions(+), 1 deletion(-)

commit e46618eccbc922721fc7af931e65578a653d31e8
Author: ryanthabot <258673122+ryanthabot@users.noreply.github.com>
Date:   2026-09-07 19:26:57 -0400

    fix(select): show the selection stage panel so GO is reachable
    
    The panel hosting every option group was created hidden (Forms.cs ctor)
    and EnterSelect never set it visible - only EnterResultTraySection
    (post-switch) did. With nothing to select, GO was never pressed in the
    v1.1.0 field run, so the dGPU switch, optimizations, cleanup, session
    tray and overlay never executed: one missing line, five symptoms
    (docs/HANDOFF_v1.1.1.md 2.1).

 src/Forms.cs | 1 +
 1 file changed, 1 insertion(+)

commit 9c0f0cf94d7a7af5b0d1f2c68509f98bfa3fdbeb
Author: ryanthabot <258673122+ryanthabot@users.noreply.github.com>
Date:   2026-09-07 19:26:57 -0400

    fix(energy-saver): silent-first switching; corrected ESBATTTHRESHOLD GUID works on 26200
    
    The 'threshold API removed on 24H2+/26200 (moved to whesvc)' belief was
    wrong: whesvc is Windows Health and Optimized Experiences (unrelated),
    and PowerRead/WriteValueIndex had been failing with rc=2 on every build
    because the ESBATTTHRESHOLD GUID had a hallucinated tail. Correct GUID
    e69653ca-cf7f-4f05-aa73-cb833fa90ad4 (Charge level, 0-100%) verified on
    build 26200: read rc=0 (DC=30), no-op write rc=0, read-back verified.
    
    - Sync is now silent-first: threshold write (AC+DC, read-back verified,
      applied immediately); Settings automation only on failure.
    - Eco writes 100% (energy saver always), Go Time 0% (never auto-engages
      while gaming - intentionally stronger than the Settings toggle-off).
    - Settings fallback reworked: mouse_event removed entirely (expand via
      UIA InvokePattern, toggle via TogglePattern), pre-existing Settings
      windows are snapshotted and never adopted or closed, our window is
      opened and kept minimized.
    - Program.Version 1.1.1.

 src/App.cs         |   2 +-
 src/EnergySaver.cs | 173 +++++++++++++++++++++++++++++++++--------------------
 2 files changed, 110 insertions(+), 65 deletions(-)

commit 0ac721b221e82aa559b0205fd9224a969e8c7205
Author: ryanthabot <258673122+ryanthabot@users.noreply.github.com>
Date:   2026-09-07 19:26:57 -0400

    docs(release): v1.1.1 - CHANGELOG.md, PORTABILITY.md, README/HANDBOOK/HANDOFF updates
    
    - CHANGELOG.md: the running human-readable history of both apps (full
      v1.0.1 -> v1.1.1) with the release-process rule that appends every
      version.
    - PORTABILITY.md: clone/build/per-user state/publish, and the
      bigthabot-primary + ryanthabot-mirror remote layout with the sync
      command.
    - README: v1.1.1 entry. HANDBOOK: v1.1.1 section-6 entry, section-8
      items 1-3 marked fixed. HANDOFF: v1.1.1 outcome banner (including the
      superseded whesvc premise).

 CHANGELOG.md           | 117 +++++++++++++++++++++++++++++++++++++++++++++++++
 PORTABILITY.md         |  68 ++++++++++++++++++++++++++++
 README.md              |  16 +++++++
 docs/HANDBOOK.md       |  44 ++++++++++++++++++-
 docs/HANDOFF_v1.1.1.md |  10 +++++
 5 files changed, 254 insertions(+), 1 deletion(-)
