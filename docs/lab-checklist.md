# Lab test checklist

Owner-run tests that development cannot do: they need the installed service, administrator rights, real hardware, a sign-out or a reboot. Tick each one and add the date and anything odd. Use synthetic projects only. **Needs** says what the test disturbs, so tests can wait until the machine is free.

Before any of these: rebuild the package (`scripts\Build-Installer.ps1`) and run `Setup-SourceVault.ps1 -Update` from it, elevated. (Needs: admin; closes editors; restarts the services.) Run scripts as `powershell -ExecutionPolicy Bypass -File .\<script>.ps1`, because Windows blocks running `.ps1` files directly by default. Run setup from the package folder, not from the repository's `installer\` folder.

## Results

- **2026-10-18, Windows 11 Pro 25H2 (build 26200), firmware TPM 2.0:** `Setup-SourceVault.ps1 -Update` installed the hardening build. The installed service, `SourceVault.Windows.dll` and lab client hashes match the package, and the service is RUNNING. Then the installed lab client `check` passed all 8 steps:
  - listed 3 projects, all authenticated at startup;
  - saved and decrypted `check.txt`, with the stale edit refused;
  - decrypted `README.md` from *Synthetic demo 1*;
  - refused a forged capability.

  So on the real service, the new **client hash pins** and the **PID-reuse check** accept an untouched installed client. The tamper half of the pin test (row below) is still to do.
- **2026-10-18, Docker:** Docker Desktop 4.95 was installed **per user** (no `com.docker.service`). `docker desktop engine use windows` refused with *engine is disabled*, and `\\.\pipe\docker_engine` answered as Docker Desktop (paused). The first `New-WindowsBuildContainer.ps1` run then failed on a script bug: its helper called itself instead of `docker.exe`. That is fixed, and the script now targets `npipe:////./pipe/docker_engine` explicitly. Decision: Microsoft's standalone engine for Windows, Docker Engine in WSL for Linux, no Docker Desktop.

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
| [~] | **Client hash pin (new):** after setup, the editor and importer connect (lab client connected 2026-10-18; editor and importer not yet run). Then, as admin, append one byte to a copy of `client\SourceVault.Import.dll`, swap it in, and check the importer is refused ("only accepts the installed…"). Restore it with `Setup-SourceVault.ps1 -Update` | admin |
| [ ] | `Setup-SourceVault.ps1 -Status` reports everything OK | admin |

## Builds (no sign-out or reboot)

| Done | Test | Needs |
|---|---|---|
| [ ] | **Remove Docker Desktop** (or quit it and stop it starting at sign-in, and turn off its WSL integration). On 2026-10-18 it held `\\.\pipe\docker_engine` even while paused, which the standalone engine needs | your uninstall |
| [ ] | **Windows engine:** follow Microsoft Learn's "Prep Windows for containers" (`install-docker-ce.ps1` from `microsoft/Windows-Containers`; read it first). It turns on **Containers** and restarts. Afterwards, `Get-Service docker` is Running and `docker version` reports server OS `windows` | admin; **restart**; download |
| [ ] | Windows engine access: `New-LocalGroup -Name docker`, add `"group": "docker"` to `C:\ProgramData\docker\config\daemon.json`, `Restart-Service docker` | admin |
| [ ] | **Windows build container (new):** elevated `New-WindowsBuildContainer.ps1`. It pulls the pinned Windows Server Core .NET SDK image (about 2.7 GB) and creates `sourcevault-build-windows`. **Record which isolation it used and whether the copy check passed**: that decides whether the Windows target can work at all | admin; download |
| [ ] | **Linux engine:** Docker Engine installed in the WSL distro (Docker's "Install Docker Engine on Ubuntu" apt instructions), with systemd on in `/etc/wsl.conf` so `dockerd` starts with the distro | your install; `wsl --shutdown` |
| [ ] | **Linux build container (new):** `New-BuildContainer.ps1 -Distro <name>`, run as your normal user. It pulls the pinned .NET SDK image (about 335 MB), creates `sourcevault-build-linux`, and must end with "a fictional console app built and ran with no network". Record the output | download |
| [ ] | Run `Setup-SourceVault.ps1 -Update -WithBuilds`, then `Grant-BuildDockerAccess.ps1 -Group docker`, then `New-WslDockerTls.ps1 -Distro <name>` and apply the dockerd settings it prints (restart that distro's Docker) | admin; the WSL distro's Docker restarts |
| [ ] | Write `C:\ProgramData\SourceVault\config\build.json` with the `windows` and `linux` targets the two container scripts printed (the `tls` thumbprint comes from `New-WslDockerTls.ps1`), then run `Setup-SourceVault.ps1 -Update -WithBuilds` again and `-Status` | admin |
| [ ] | Import `samples\dotnet-hello` from the package (`SourceVault.Import.exe <package>\samples\dotnet-hello --project "Hello sample"`). **Build** it on the Linux target: the output folder has `Hello.dll`, `Hello`, `Hello.deps.json` and `Hello.runtimeconfig.json` | — |
| [ ] | **Build** `Hello sample` on the Windows target: the output folder has `Hello.exe`, `Hello.dll`, `Hello.deps.json` and `Hello.runtimeconfig.json` | — |
| [ ] | **Dropped files (new):** the `dotnet-hello` build lists `Hello.pdb` as left out (DebugSymbols) at the end of its log, and *Left out: 1 file* in the result | — |
| [ ] | A build that runs past `timeoutSeconds` is *timed out*; a container with a network gets *refused* when `allowNetwork` is false | — |

## Needs a sign-out or reboot (wait until the machine is free)

| Done | Test | Needs |
|---|---|---|
| [ ] | Reboot, then `Test-ServiceLab.ps1`: the service starts, opens its key, and the editor works | **reboot** |
| [ ] | Set the service to automatic start, reboot, and check it is RUNNING before anyone signs in | **reboot** |
| [ ] | Sign-out with the editor open: the vault session ends, and nothing is left running | **sign-out** |
| [ ] | A second Windows account that is *not* in *SourceVault Operators* is refused by the pipe | **sign-in as another user** |
| [ ] | Power loss: `SourceVault.LabClient.exe save-loop`, cut power mid-run, boot, then `save-check` shows the last confirmed count (or one more) | **hard power-off** |

### Runbook for the reboot round

The package is `artifacts\installer\SourceVault-Setup\` in the repository (worktree) folder. "Elevated" means *Run as administrator*; "normal" means an ordinary PowerShell window. A reboot ends any Claude session, so reopen it afterwards and paste the outputs.

**0. Install this build** (elevated). Close all editor and lab client windows first.
```powershell
cd "C:\Users\root\Project\SourceVault\.claude\worktrees\project-review-e08e79\artifacts\installer\SourceVault-Setup"
powershell -ExecutionPolicy Bypass -File .\Setup-SourceVault.ps1 -Update
powershell -ExecutionPolicy Bypass -File .\Setup-SourceVault.ps1 -Status
```
Then, in a normal window: `& "C:\Program Files\SourceVault\client\SourceVault.LabClient.exe" check`. All steps should pass, which also proves the new hash pins accept the installed files. Open the editor from the Start menu once.

**1. Automatic start** (elevated). `-Update` keeps the current start mode, which was manual for this lab install.
```powershell
sc.exe config SourceVault start= delayed-auto
```

**2. Reboot.** At the sign-in screen, wait **3 minutes** (delayed start runs about 2 minutes after boot), then sign in. Elevated:
```powershell
$svc = Get-CimInstance Win32_Service -Filter "Name='SourceVault'"; $svc.State
(Get-Process -Id $svc.ProcessId).StartTime; (Get-Process explorer | Sort-Object StartTime | Select-Object -First 1).StartTime
```
The service should be `Running`, and its start time should be earlier than explorer's, which is when you signed in. Then run the start/stop check, and leave the service running:
```powershell
sc.exe stop SourceVault
powershell -ExecutionPolicy Bypass -File "C:\Users\root\Project\SourceVault\.claude\worktrees\project-review-e08e79\scripts\lab\Test-ServiceLab.ps1" -LeaveRunning
```
Normal window: the lab client `check` again, then open the editor, open a project and save a file.

**3. Sign-out.** With the editor open and unlocked, sign out (Start, your account, *Sign out*), then sign back in. In a normal window, `Get-Process SourceVault.Editor -ErrorAction SilentlyContinue` should show nothing. The editor then opens from the Start menu and unlocks normally.

**4. Second account (optional).** You must create the account yourself: a standard local user, **not** in *SourceVault Operators*. Use *Switch user* to sign in as it and run `& "C:\Program Files\SourceVault\client\SourceVault.LabClient.exe" list`. Expected: "Access denied by the pipe". Sign that account out afterwards.

**5. Power loss — last, and only when nothing else is running** (the other agent, unsaved work: everything open is lost). In a normal window:
```powershell
& "C:\Program Files\SourceVault\client\SourceVault.LabClient.exe" save-loop
```
Once some "saved N" lines have appeared, **photograph the screen** and immediately hold the power button until the PC turns off (or pull the plug). Boot, sign in, wait 3 minutes, then run elevated `Setup-SourceVault.ps1 -Status`. The service should be running, which means every project authenticated at startup. Normal window:
```powershell
& "C:\Program Files\SourceVault\client\SourceVault.LabClient.exe" save-check
```
The count must be the last N in the photo, or N+1.

**Afterwards:** to go back to manual start, run `sc.exe config SourceVault start= demand` (elevated).

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
