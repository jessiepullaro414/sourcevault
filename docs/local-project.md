# Local project

This folder is the working SourceVault Git project: branch `main`, pushed to https://github.com/jessiepullaro414/sourcevault (MIT license, chosen by the owner).

The project folder is `C:\Users\root\Project\SourceVault` (moved there on 2026-10-18 from a long scratch path under `Documents\Codex`). It sits inside the owner's `C:\Users\root\Project` repository, whose `.gitignore` excludes `SourceVault/`; the two repositories are separate.

## Local-only files: `local\` (git-ignored)

| Folder | What it is |
|---|---|
| `local\toolchain\dotnet\` | The verified portable .NET 10 SDK the scripts use by default (`Verify.ps1`, `Build-Installer.ps1`, `Publish-ServiceLab.ps1`, `Start-Editor.ps1`). |
| `local\planning\` | The original planning documents: `SourceVault-implementation-plan.md`, `SourceVault-subagent-tasks.md`, and the first feasibility snapshot ZIP. |
| `local\work\` | Scratch folders from early feasibility runs (synthetic test workspaces). Safe to delete. |

`artifacts\` (also git-ignored) holds build output, the NuGet cache restored from `vendor\packages`, test workspaces and the setup package.

Open `SourceVault.sln` in a .NET-capable IDE, or run `scripts\Verify.ps1`. Durable instructions are in `AGENTS.md`; current evidence and work remaining are in `docs\status.md` and `docs\next-tasks.md`.
