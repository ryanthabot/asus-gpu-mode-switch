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
