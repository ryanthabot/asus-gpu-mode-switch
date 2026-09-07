# Portability — carrying this project between PCs

This file exists so the software package can be brought to and from,
launched and edited from different PCs. The git repository IS the portable
state: everything needed to build and edit the apps is committed; build
outputs and per-user state are deliberately not.

## 1. What lives where

| Where | What | Portable? |
|---|---|---|
| The repo (`src\`, `docs\`, `*.md`) | All source, docs, this guide | yes — clone anywhere |
| `dist\` (gitignored) | `Go Time.exe`, `Eco Mode.exe` | rebuilt in seconds; also on GitHub Releases |
| `%LOCALAPPDATA%\GpuModeSwitch\` | Per-user runtime state (below) | per-machine by design |

Per-user state files (recreated with sensible defaults when missing —
copying them to a new PC restores your lists/history):

- `logs\GoTime\` and `logs\EcoMode\` — one timestamped log per run
  (30-day / 200 MB retention per folder, pruned at session start).
- `freezelist.txt` — process names the freezer may suspend (never killed).
- `powerplan.txt` — the Ultimate plan duplicate + the plan to restore.
- `profiles.json` — named selection-stage presets.
- `sessions.jsonl` — one record per GO run (actions, space freed, errors).

## 2. Set up on any PC (Windows 10/11 x64)

No Visual Studio, no .NET SDK, no NuGet — only git and the compiler that
ships with Windows:

```
git clone <repo-url>
cd asus-gpu-mode-switch
cmd /c "src\build.cmd"
```

Expected: csc prints nothing, then `Build OK:` with both exes in `dist\`.
The exes require an admin manifest (UAC prompt) — that is by design.
Runtime behavior details: `docs\HANDBOOK.md` (constraints, architecture);
current known state: `docs\HANDOFF_v1.1.1.md`.

## 3. Remotes and who can push

| Remote | URL | Notes |
|---|---|---|
| primary | https://github.com/bigthabot/asus-gpu-mode-switch | writable only with bigthabot credentials |
| mirror | https://github.com/ryanthabot/asus-gpu-mode-switch | the original account's copy, kept commit-identical |

Work on whichever remote your current machine can push to, then sync the
other from a machine with its credentials:

```
git pull <other-remote-url> main && git push
```

Releases are published wherever you have rights; mirror the tag and
release to the other account when syncing.

## 4. Making a release (summary — full list in CHANGELOG.md)

1. Code done; `Program.Version` bumped (`src/App.cs`).
2. `cmd /c "src\build.cmd"` — zero csc diagnostics.
3. Append the CHANGELOG.md entry.
4. Commit, push the remote(s) you can.
5. `git tag vX.Y.Z && git push <remote> vX.Y.Z`, then publish the GitHub
   Release with `dist\Go Time.exe` and `dist\Eco Mode.exe` attached
   (e.g. `gh release create vX.Y.Z "dist/Go Time.exe" "dist/Eco Mode.exe"
   --title ... --notes ...`).
