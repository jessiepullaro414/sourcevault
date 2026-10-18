# Lab test checklist

Owner-run tests that development cannot do: they need the installed service, administrator rights, real hardware, a sign-out or a reboot. Tick each one and add the date and anything odd. Use synthetic projects only. **Needs** says what the test disturbs, so tests can wait until the machine is free.

Before any of these: rebuild the package (`scripts\Build-Installer.ps1`) and run `Setup-SourceVault.ps1 -Update` from it, elevated. (Needs: admin; closes editors; restarts the services.)

## Editor and vault (no sign-out or reboot)

| Done | Test | Needs |
|---|---|---|
| [ ] | Open the editor from the Start menu, open a synthetic project, edit and save; close and reopen and the edit is there | — |
| [ ] | Conflict: change the same file with the lab client, then **Merge** (clean and overlapping) and **Reload their version** | — |
| [ ] | Rename and move a file and a folder; delete a file and restore it from *Deleted files*; rename the project | — |
| [ ] | History: compare an old version, restore it as a draft, save | — |
| [ ] | Win+L locks the editor at once; unlock shows the drafts, and a file changed meanwhile is marked `!` | lock screen only |
| [ ] | Capture check: a screenshot (Snipping Tool) and a screen recording show the editor blank, and the status bar reports capture exclusion on | — |
| [ ] | Profile canary: type a unique marker, save, close; search `%LOCALAPPDATA%\SourceVault\profiles`, `%LOCALAPPDATA%\Temp` and `%LOCALAPPDATA%\CrashDumps` for it (UTF-8 and UTF-16) | — |
| [ ] | Delete a project (typed name); another open editor gets "no longer exists" | — |
| [ ] | `SourceVault.Import.exe` on a synthetic folder with `--dry-run`, then a real import, then `--delete-source` (type `DELETE`) | — |
| [ ] | **Client hash pin (new):** after setup, the editor and importer connect. Then, as admin, append one byte to a copy of `client\SourceVault.Import.dll`, swap it in, and check the importer is refused ("only accepts the installed…"). Restore it with `Setup-SourceVault.ps1 -Update` | admin |
| [ ] | `Setup-SourceVault.ps1 -Status` reports everything OK | admin |

## Builds (no sign-out or reboot)

| Done | Test | Needs |
|---|---|---|
| [ ] | Run `Setup-SourceVault.ps1 -Update -WithBuilds`, write `build.json` from `build.example.json`, then run `Grant-BuildDockerAccess.ps1` | admin; restarts Docker access for the build service |
| [ ] | WSL target: `New-WslDockerTls.ps1 -Distro <name>`, apply the printed dockerd settings, restart that distro's Docker | the WSL distro restarts |
| [ ] | Build a synthetic project on the Linux target: succeeds, log shown, output folder has the files | — |
| [ ] | Build on the Windows target. **Record whether copying into the running Windows container works** (Hyper-V isolation may refuse it) | — |
| [ ] | **Dropped files (new):** a build that writes `app.js.map` and `app.pdb` publishes the other files and lists both as dropped | — |
| [ ] | A build that runs past `timeoutSeconds` is *timed out*; a container with a network gets *refused* when `allowNetwork` is false | — |

## Needs a sign-out or reboot (wait until the machine is free)

| Done | Test | Needs |
|---|---|---|
| [ ] | Reboot, then `Test-ServiceLab.ps1`: the service starts, opens its key, and the editor works | **reboot** |
| [ ] | Set the service to automatic start, reboot, and check it is RUNNING before anyone signs in | **reboot** |
| [ ] | Sign-out with the editor open: the vault session ends, and nothing is left running | **sign-out** |
| [ ] | A second Windows account that is *not* in *SourceVault Operators* is refused by the pipe | **sign-in as another user** |
| [ ] | Power loss: start a long series of saves (the lab client), cut power (or hard-reset a VM), boot, check the service starts and the project opens at a complete revision | **hard power-off** |

## Needs administrator and leaves the service stopped for a while

| Done | Test | Needs |
|---|---|---|
| [ ] | Missing key: rename the key in the config, start the service: it refuses (exit code shown by `-Status`); restore | admin; service down |
| [ ] | Bad config: an invalid `sourcevault.service.json` is refused at start; restore | admin; service down |
| [ ] | Elevated `scripts/Verify.ps1`: the 5 symlink tests run instead of skipping | admin |

## Editor native checklist (from `editor-preview-checks.md`)

| Done | Test | Needs |
|---|---|---|
| [ ] | F12, Ctrl+Shift+I, Ctrl+P, Ctrl+S (browser), Ctrl+F5 and the context menu do nothing outside the app's own handling | — |
| [ ] | Registry/policy overrides: set a WebView2 `AdditionalBrowserArguments` policy with `--remote-debugging-port`, start the editor, check that no debugging port opens; remove it | admin; registry |
| [ ] | Devtools via `WEBVIEW2_*` environment variables are refused at startup | — |
| [ ] | Every Monaco feature still loads in the native window (TypeScript errors, find widget, icons) | — |

Results go in `docs/status.md` (one line each, with the date), and failures become development tasks in `docs/next-tasks.md`.
