# Project contents — asus-gpu-mode-switch v1.1.0

This folder is a self-contained bundle of the project's documents and
history so the entire project can be reviewed from one place — on any
machine, by any person or agent — without digging through branches or
git tooling.

| File | What it is |
|---|---|
| `README.md` | Project front page: what the apps do, the full v1.1.0 changelog, usage, the Logging section (paths + retention), and the storage-cleanup safety model. |
| `HANDBOOK.md` | The living handoff document (copy of `docs/HANDBOOK.md`) — hard constraints (C# 5 / csc.exe / no SDK), the complete architecture map with every module's public API, the design-decisions log (D1–D9), the 18-agent feature checklist, the per-wave progress log with verification evidence, exact build/verify commands, and next steps. An agent that clones this repo and reads only this file can continue the project unaided. |
| `BUILD_NOTES.md` | Baseline build evidence (v1.0.22), compiler details, artifact inventory, and the manual (UAC-gated) verification checklist for v1.1.0. |
| `COMMIT_HISTORY.md` | The complete commit history of the v1.1.0 build — all 7 commits from the v1.0.22 baseline to the v1.1.0 release, with full commit messages and per-commit file stats (the "commit details"). |
| `LICENSE` | MIT license. |

## Where things live in the repo

- Source code: `src/` (build with `src\build.cmd` — uses only the C# compiler
  that ships with Windows; no Visual Studio, no .NET SDK, no NuGet).
- Living copies: `docs/HANDBOOK.md`, `BUILD_NOTES.md`, `README.md` (the files
  in this folder are snapshots of those, taken at the v1.1.0 release).
- Build outputs land in `dist/` and are intentionally gitignored; the built
  executables (`Go Time.exe`, `Eco Mode.exe`) are attached to the GitHub
  **v1.1.0 release**.

## Publish state

- Branch `v1.1-logging-cleanup` (the 18-agent build branch) — published.
- `main` — fast-forwarded to the same commit, so the repo landing page shows
  v1.1.0. Release commit: `cdb52f2`; the commit that adds this bundle
  follows it.
- Tag `v1.1.0` — on the release commit; GitHub Release `v1.1.0` carries the
  two executables and release notes.

Known issue at publish time (fix planned as v1.1.1): Go Time's selection
stage can render with its option groups invisible (the stage container stays
hidden), which also prevents a first GO press from ever reaching the
switch/cleanup/tray paths. Eco Mode is unaffected. Details and the pending
fix plan are in `HANDBOOK.md` §6 / `BUILD_NOTES.md`.
