# Project contents — asus-gpu-mode-switch v1.1.1

This folder is a self-contained bundle of the project's documents and
history so the entire project can be reviewed from one place — on any
machine, by any person or agent — without digging through branches or
git tooling. Refreshed at every release (currently: **v1.1.1**,
2026-09-07).

| File | What it is |
|---|---|
| `README.md` | Project front page: what the apps do, the full version history, usage, the Logging section and the storage-cleanup safety model. |
| `CHANGELOG.md` | The running human-readable history of both apps — appended as a fixed step of every release, plus the release-process checklist. |
| `PORTABILITY.md` | How to carry the project between PCs: clone/build, per-user state files, remotes and publish steps. |
| `HANDBOOK.md` | The living handoff document (copy of `docs/HANDBOOK.md`) — hard constraints (C# 5 / csc.exe / no SDK), the architecture map, design decisions D1–D10, the feature checklist, per-wave progress with verification evidence, build/verify commands and next steps. |
| `BUILD_NOTES.md` | Baseline + Wave 6 + **v1.1.1** build evidence, the Energy Saver GUID probe evidence, and the manual (UAC-gated) verification checklist (items 1–13). |
| `COMMIT_HISTORY.md` | The complete commit history — the v1.1.0 18-agent build (7 commits) plus the v1.1.1 handoff and fix commits, with full messages and file stats. |
| `HANDOFF_v1.1.1.md` | The post-release handoff (copy of `docs/HANDOFF_v1.1.1.md`): current-state inventory, verified root causes with file:line pointers, the v1.1.1 scope, and the v1.2.0 single-app design sketch. |
| `LICENSE` | MIT license. |

## Where things live in the repo

- Source code: `src/` (build with `src\build.cmd` — uses only the C# compiler
  that ships with Windows; no Visual Studio, no .NET SDK, no NuGet).
- Living copies: `docs/HANDBOOK.md`, `docs/HANDOFF_v1.1.1.md`,
  `BUILD_NOTES.md`, `README.md`, `CHANGELOG.md`, `PORTABILITY.md` (the files
  in this folder are snapshots of those, taken at the v1.1.1 release).
- Build outputs land in `dist/` and are intentionally gitignored; the built
  executables (`Go Time.exe`, `Eco Mode.exe`) are attached to the GitHub
  **v1.1.1 release**.

## Publish state

- `main` on the ryanthabot mirror carries the v1.1.1 release commit
  (`0ac721b`), tag `v1.1.1`, and the GitHub Release with both exes; this
  bundle-refresh commit follows it.
- bigthabot (the primary repo) still sits at the v1.1.0 tag pending a
  one-command sync from a machine with bigthabot credentials:
  `git pull https://github.com/ryanthabot/asus-gpu-mode-switch main && git push`.

## Known issues

- The v1.1.0 release blocker (invisible Go Time selection stage) and the
  obtrusive Energy Saver Settings popup are **fixed in v1.1.1** — the full
  root-cause story (including the corrected ESBATTTHRESHOLD GUID that makes
  silent switching work on build 26200) is in `HANDOFF_v1.1.1.md` and the
  v1.1.1 section of `BUILD_NOTES.md`; the release history is in
  `CHANGELOG.md`.
- OPEN: **v1.2.0** will merge the two executables into ONE app with both
  modes inside (owner decision D10; design sketch in `HANDOFF_v1.1.1.md`
  §7), pending the owner's confirmation of the final app name and icon.
