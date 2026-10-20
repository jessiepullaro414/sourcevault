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
- **2026-10-20, first Windows build from the editor (dev PC, Windows 11 Pro 26200, synthetic sample only).**
  - **Setup:** standalone Docker 29.9.1 via `install-docker-ce.ps1` (hash matched the pin), then the `docker` group (`New-LocalGroup` needs a description of 48 characters or fewer; fixed in `Deploy-SourceVault.ps1`). Then setup `-Update -WithBuilds` and `New-WindowsBuildContainer.ps1`: **process isolation**, network `none`, 4 GB, copy check and offline test build passed. Then `Grant-BuildDockerAccess.ps1 -Group docker`; `-Status` all OK except BitLocker (off on this PC by the owner's choice).
  - **First build attempt:** failed with *CleanupFailed*. The cleanup line quoted its paths, and the engine re-escaped those quotes with backslashes, which `cmd.exe` reads literally. The fix passes the cleanup as bare words, and Windows container paths no longer allow spaces.
  - **After the fix:** `dotnet-hello` built on the Windows target (build 3). The output folder had `Hello.exe`, `Hello.dll`, `Hello.deps.json` and `Hello.runtimeconfig.json`; `Hello.pdb` was not published.
- **2026-10-20, first Linux build from the editor (same dev PC; WSL 2.6.1 was already installed).**
  - **Setup:** the steps of Deploy-SourceVault.ps1 phases 4 and 6, without the drive: a dedicated distro `SourceVault-Ubuntu` imported from Ubuntu's 24.04 WSL image (hash matched), Docker 29.9.1 static binaries under systemd (no bridge, no iptables), the keep-alive sign-in task, then `New-BuildContainer.ps1` and `New-WslDockerTls.ps1`. The TLS listener was on 127.0.0.1:2376, reachable from Windows.
  - **Two script bugs found and fixed:**
    - `wsl.exe -- wslpath C:\...` went through the distro's shell, which stripped the backslashes. It now uses `--exec`.
    - PowerShell ends piped text with CRLF, so the last line of every script piped into the distro carried a carriage return. Docker's systemd link became `docker.service` plus that carriage return, and Docker never started. The distro's `sed` now strips it.
  - **Result:** `dotnet-hello` built on the Linux target (build 4). The output folder had `Hello`, `Hello.dll`, `Hello.deps.json` and `Hello.runtimeconfig.json`; `Hello.pdb` was not published.

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
| [ ] | **Editor features (new):** in the installed editor with a synthetic C++ project, on a real keyboard: Ctrl+P, Ctrl+E, Ctrl+Shift+F (a result in an unsaved file too), Ctrl+Shift+O and Alt+O on a `.cpp`, **Ctrl+\\** (split) and **Ctrl+,** (settings; the browser check could not send these two), Ctrl+K Ctrl+S opens the list without saving, Ctrl+K D then **Revert to saved** and Ctrl+Z, Ctrl+Alt+S, Ctrl+K Z | — |
| [ ] | **Settings file (new):** after saving settings, `%LOCALAPPDATA%\SourceVault\editor-settings.json` holds only the eight display fields; the canary marker is not in it. Replace it with `{}`, then with 5 KiB of text: the editor starts with defaults | — |
| [ ] | **Demo (new):** `Build-EditorDemo.ps1`, copy the zip to a PC **without** SourceVault, unzip, run *Start SourceVault Demo*: the window is red and titled *DEMO (not encrypted)*, the demo controls work, and a screenshot captures it. Close it: `%LOCALAPPDATA%\SourceVault\profiles\demo` is empty | a second PC (or this one) |
| [ ] | **Close warning (new):** with an unsaved edit, closing the window asks *Close anyway?*; **No** keeps it open with the edit; with nothing unsaved it closes without asking | — |

## Easier everyday use (2026-10-20; synthetic files only)

| Done | Test | Needs |
|---|---|---|
| [ ] | **Clipboard:** in the installed editor, Ctrl+C a line, paste it into another vault file (it pastes the line); paste into Notepad (it pastes only the *Copied inside SourceVault…* notice). Copy text in Notepad and Ctrl+V into the editor (it pastes). Lock and unlock: Ctrl+V pastes the notice text no more | — |
| [ ] | **Drops:** drag a file and a folder (containing a `.git` folder) from Explorer onto the Explorer panel: they are added, `.git` skipped. Drop on the editor, a tab or the inspector: nothing happens. Drag text out of the editor into Notepad: nothing arrives | — |
| [ ] | **Import a folder:** the folder dialog, the skipped list, Import, the new project opens; a second import with the same name says it is taken | — |
| [ ] | **Open folder** in the build view opens the output folder in Explorer | builds |
| [ ] | **Guided setup:** double-click `SourceVault Setup.cmd` on a test PC: the elevation prompt, the questions, the summary, the install. Then Start menu → *SourceVault Administration*: status ends with *All good*; add a second account (the sign-out prompt), remove it (the "until they sign out" note) | admin; a test PC |
| [ ] | **Offline project:** `scripts/lab/Test-VaultTamper.ps1` now expects the service to start with the tampered project offline and `status\vault.json` to list it; the editor shows it struck through; Delete works; `-Status` warns | admin; service down |
| [ ] | **Not connected:** stop the service (`sc stop SourceVault`, admin), open the editor: it is red, says *The SourceVault service isn't running*, and the window title says *NOT CONNECTED*; start the service, click **Try again**: it unlocks and the red goes away | admin |
| [ ] | **Offline work:** with a file open, stop the service; lock and unlock the editor (it can't connect): **Work offline**, edit, make a new file, Save both; Win+L hides them and unlocking Windows shows the lock screen, not the files; start the service, **Try again**: both are saved. Search the profile and `%TEMP%` for a marker typed offline: not found | admin |
| [ ] | **Check codes:** Pack (window and `init`), `--export-recipient` and `-RegisterSender` all show the same six-group code for the same key | second PC |

## Security fixes (2026-10-20)

| Done | Test | Needs |
|---|---|---|
| [ ] | **SourceVault password:** in the installed editor: choose one at the first unlock; lock and unlock with it; 5 wrong tries lock it (the 6th, even right, is refused); `-ResetPassword` for that account lets them choose a new one; change it in settings; the importer asks for it in the console. Search the vault folder and `%LOCALAPPDATA%` for the password text: not found | admin for the reset |
| [ ] | **Secure Boot and BitLocker on the vault PC:** `-Status` shows *Secure Boot is on* and *BitLocker protects the Windows drive* (and the vault drive, if another one). Record the result in the whitepaper (section 4.5) | admin; the vault PC |
| [ ] | **Output folder check:** with the setup-created artifact folder, a build works and `-Status` shows no artifact warning. Then point `artifactRoot` at a new folder under one ordinary users can modify (e.g. `D:\Shared\out`, with Users Modify on `D:\Shared`): `-Status` warns, and a build ends *could not run* with the output-folder message. Restore it | admin; builds |
| [ ] | **Startup hooks:** set `DOTNET_STARTUP_HOOKS` to any path in a normal window and start the installed editor and importer from it: both run normally (the hook is ignored), and the importer still connects | — |
| [ ] | **Docker pipe owner:** with the real Windows engine running, a build still works. Its pipe's owner, `(Get-Acl '\\.\pipe\docker_engine').Owner`, is SYSTEM or Administrators. Stop the engine, create a pipe with that name as a normal user (a small PowerShell `NamedPipeServerStream`), request a build: it ends *could not run* with *something other than the Docker engine answered*, and the pipe receives no data | admin; Docker |

## Any-file vault (no sign-out or reboot; synthetic files only)

Generate the files; never use real source or downloaded binaries. Start from a vault backup you can restore, because **the upgrade is one-way**: after the first change to a project, an older version refuses the vault.

| Done | Test | Needs |
|---|---|---|
| [ ] | **Upgrade:** with the new version installed over a vault holding older projects, open one, save a file: it still opens, and `-Status` is OK after a service restart | admin (restart) |
| [ ] | **Import any file:** `SourceVault.Import.exe` on a generated folder with a random 300 MB `.lib`, a UTF-16 `.rc`, an `.ico` and 2,000 small text files: dry run lists them, the import ends "all N match the originals (SHA-256) and decrypted cleanly in the service" | — |
| [ ] | **Editor, info panel:** the `.ico` and `.lib` show *binary* with size and SHA-256 (compare with `Get-FileHash`); a 700 KiB text file opens and saves; a 2 MiB log shows *large* | — |
| [ ] | **From disk:** **+ Add from disk** (several files) and **Replace from disk…** with the native dialog; History lists both versions; **Revert to this version** brings the first back (same SHA-256). A file locked by another program is refused with "could not read", and the session stays unlocked | — |
| [ ] | **Keep mine / theirs:** replace a file with the lab client (or a second editor) after opening its details here, then **Replace from disk…**: the choice appears; try both | — |
| [ ] | **Search:** "N files skipped (binary or too large)" with binaries in the project | — |
| [ ] | **Read-time check (C4):** stop the service, flip one bit in a *file* record (not the manifest) of a synthetic project with `scripts/lab/Test-VaultTamper.ps1`-style care, start it: it starts (startup checks existence and size); opening that file is refused (*Unavailable*), and an import's `VerifyProject` would report it. Restore the record | admin; service restart |
| [ ] | **Benchmark:** 25,000 generated files and several GiB: List, Open, Commit, a build, a search and `VerifyProject`, times recorded in `revision-store.md` | — |
| [ ] | **Builds:** a real Docker copy of a large synthetic project to both targets; the `cpp-hello` sample with a synthetic UTF-16 `.rc` and binary `.ico` arrives byte-identical | Docker |
| [ ] | **Pack v2:** Pack on the source computer with a 300 MB binary, import here; a package from the previous Pack is still accepted | second PC |

## Encrypted import from another computer (no sign-out or reboot)

Needs a second Windows computer with a TPM 2.0 (the "source computer"), and synthetic code only.

| Done | Test | Needs |
|---|---|---|
| [ ] | After `Setup-SourceVault.ps1 -Update`: the *TPM key for package import* step says "created" (or "already exists"), and `-Status` lists `package senders registered: (none)` | admin |
| [ ] | `SourceVault.Import.exe --export-recipient recipient.json` writes the file and prints a fingerprint | — |
| [ ] | On the source computer: copy `tools\SourceVault-Pack`, open `pack\SourceVaultPack.exe` and click **Set up this computer** (or run `pack\SourceVault.Pack.exe init`). **Record whether the TPM created the key**, then copy its sender file back | source computer |
| [ ] | The Pack window: choosing a recipient shows its computer and fingerprint; choosing a folder fills **Included** and **Skipped** (binaries, `.git`, `bin`, `obj`); **Create encrypted package** writes a package after the confirmation | source computer |
| [ ] | `Setup-SourceVault.ps1 -RegisterSender <file>`: the fingerprint matches what `init` printed; type YES | admin |
| [ ] | On the source computer: `SourceVault.Pack.exe <synthetic folder> --to recipient.json --out test.svpkg`. The recipient fingerprint matches the vault PC's. **Record whether the TPM signed** (RSA-PSS) | source computer |
| [ ] | `SourceVault.Import.exe --package test.svpkg --project "Packed sample"`: imports N files, names the sender computer, and the project opens in the editor with the same files | — |
| [ ] | Refusals: the same package with one byte changed is *rejected*; a package from an unregistered computer gives *not registered*; importing to a taken name gives *already exists*. Nothing is created in any of them | — |
| [ ] | Search the vault PC's `%TEMP%` and the importer's folder for a marker inside the packed files: nothing (plaintext only ever existed in the service's memory) | — |
## Vault on another drive (e.g. K:)

The full owner-run steps are in [vault-location-design.md](vault-location-design.md), "Lab checklist". Most need an elevated prompt, and step 6 a reboot.

| Done | Test | Needs |
|---|---|---|
| [ ] | Record the drive's type, file system, bus and root ACL (step 1) | admin |
| [ ] | Fresh install with `-VaultRoot K:\SourceVault\vault -WithSyntheticDemo`: RUNNING, and `-Status` shows the K: path; an existing C: install still passes after `-Update` (steps 2-3) | admin; a test machine or fresh install |
| [ ] | K: offline or locked: start refuses with 1066/17 and creates nothing; back online, RUNNING (step 4). A USB drive is refused (step 5) | admin; service down |
| [ ] | Reboot with the vault on K: (step 6); uninstall keeps it and a reinstall authenticates it (step 7); the tamper drill on K: (step 8) | **reboot**; admin |

## Editor: Monaco only, no network

The full steps are in [editor-preview-checks.md](editor-preview-checks.md), "Owner lab checklist: no network".

| Done | Test | Needs |
|---|---|---|
| [ ] | Find the editor's `msedgewebview2.exe` processes, then watch them with Resource Monitor or a packet capture through 10 minutes of use and 10 idle: no connection, UDP or DNS to any non-loopback address (steps 1-2) | — |
| [ ] | Links in content open nothing; no external application or "open this app?" prompt (steps 3-4) | — |
| [ ] | A `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` environment variable is refused (step 5) | — |
| [ ] | A registry `AdditionalBrowserArguments` policy for the editor is refused, and one for another program isn't (step 6; undo afterwards) | admin; lab VM; machine policy |
| [ ] | Crash data: the profile, including `Crashpad`, is gone after a vault session, and nothing is reported to Microsoft (step 7) | — |
## Offline deployment drive

Builds on a connected PC, then a **fresh Windows 11 Pro PC with a TPM 2.0 and no internet** (needs restarts and a sign-out). Steps are in the user guide, "Deploying from a drive".

| Done | Test | Needs |
|---|---|---|
| [ ] | `Build-DeploymentKit.ps1 -Destination <drive>\SourceVaultKit -RecordNewHashes`: every download verified; both images saved; review and commit the recorded Docker hashes in `installer\kit-sources.json` | about 4.5 GB download |
| [ ] | `Test-DeploymentKit.ps1` on the drive: OK. Change one byte of any file: FAIL | — |
| [ ] | **Archive check on Docker 29's containerd image store (new, 2026-10-20):** on a connected PC, `scripts\Save-RegistryImage.ps1` with the `sdk-linux` values from `kit-sources.json` (`-OutFile` in a scratch folder). In a WSL distro running Docker 29 (for example `SourceVault-Ubuntu`), first `docker image inspect --format '{{.Id}}' sha256:3a48280a9a7c6276c3eeaba1efa6a08820f18c84fb9e6274c187719ecfaf9fbc` must fail (that manifest is not an image yet). Then `New-BuildContainer.ps1 -Distro <name> -ImageArchive <that .tar> -Name sourcevault-build-loadcheck` must print *matches the pinned image ID (sha256:3a48280a…)* and end with the offline test build. If it names `0d99ebf1…`, the image came from an earlier pull, not the archive. Then `docker rm -f sourcevault-build-loadcheck`. Optional: on the Windows engine (classic store), `New-WindowsBuildContainer.ps1 -ImageArchive` with a saved `sdk-windows` must name `22585ab0…` | about 335 MB download (2.7 GB more for Windows) |
| [ ] | On the target: `Deploy-SourceVault.ps1` phases 1-2, then restart | admin; **restart** |
| [ ] | Run it again: the Windows engine, WSL and Ubuntu, and the Linux engine come up from the drive. **Record the Windows container's isolation and copy check**, and that both images load with their pinned image IDs | admin |
| [ ] | SourceVault is installed in the chosen vault location, with builds for the targets that work; `-Status` OK. Sign out and in, run it again: `transfer\recipient-<PC>.json` is written | admin; **sign-out** |
| [ ] | Two legs: Pack encrypts the sample to that recipient on the source computer; `-ImportPackages` imports it; build it on each target | source computer |
| [ ] | After a restart and sign-in, the WSL keep-alive task is running and a Linux build still works | **restart** |
## Builds (no sign-out or reboot)

| Done | Test | Needs |
|---|---|---|
| [ ] | **Remove Docker Desktop** (or quit it and stop it starting at sign-in, and turn off its WSL integration). On 2026-10-18 it held `\\.\pipe\docker_engine` even while paused, which the standalone engine needs | your uninstall |
| [x] 2026-10-20 dev PC | **Windows engine:** follow Microsoft Learn's "Prep Windows for containers" (`install-docker-ce.ps1` from `microsoft/Windows-Containers`; read it first). It turns on **Containers** and restarts. Afterwards, `Get-Service docker` is Running and `docker version` reports server OS `windows` | admin; **restart**; download |
| [x] 2026-10-20 dev PC | Windows engine access: `New-LocalGroup -Name docker`, add `"group": "docker"` to `C:\ProgramData\docker\config\daemon.json`, `Restart-Service docker` | admin |
| [x] 2026-10-20 dev PC: process isolation, copy check passed | **Windows build container (new):** elevated `New-WindowsBuildContainer.ps1`. It pulls the pinned Windows Server Core .NET SDK image (about 2.7 GB) and creates `sourcevault-build-windows`. **Record which isolation it used and whether the copy check passed**: that decides whether the Windows target can work at all | admin; download |
| [x] 2026-10-20 dev PC: static binaries, as Deploy-SourceVault.ps1 does | **Linux engine:** Docker Engine installed in the WSL distro (Docker's "Install Docker Engine on Ubuntu" apt instructions), with systemd on in `/etc/wsl.conf` so `dockerd` starts with the distro | your install; `wsl --shutdown` |
| [x] 2026-10-20 dev PC: ended with the offline test build | **Linux build container (new):** `New-BuildContainer.ps1 -Distro <name>`, run as your normal user. It pulls the pinned .NET SDK image (about 335 MB), creates `sourcevault-build-linux`, and must end with "a fictional console app built and ran with no network". Record the output | download |
| [x] 2026-10-20 dev PC | Run `Setup-SourceVault.ps1 -Update -WithBuilds`, then `Grant-BuildDockerAccess.ps1 -Group docker`, then `New-WslDockerTls.ps1 -Distro <name>` and apply the dockerd settings it prints (restart that distro's Docker) | admin; the WSL distro's Docker restarts |
| [x] 2026-10-20 dev PC: -Status targets=windows,linux | Write `C:\ProgramData\SourceVault\config\build.json` with the `windows` and `linux` targets the two container scripts printed (the `tls` thumbprint comes from `New-WslDockerTls.ps1`), then run `Setup-SourceVault.ps1 -Update -WithBuilds` again and `-Status` | admin |
| [x] 2026-10-20 dev PC, from the editor | Import `samples\dotnet-hello` from the package (`SourceVault.Import.exe <package>\samples\dotnet-hello --project "Hello sample"`). **Build** it on the Linux target: the output folder has `Hello.dll`, `Hello`, `Hello.deps.json` and `Hello.runtimeconfig.json` | — |
| [x] 2026-10-20 dev PC, from the editor, after the cleanup fix | **Build** `Hello sample` on the Windows target: the output folder has `Hello.exe`, `Hello.dll`, `Hello.deps.json` and `Hello.runtimeconfig.json` | — |
| [ ] | **Dropped files (new):** the `dotnet-hello` build lists `Hello.pdb` as left out (DebugSymbols) at the end of its log, and *Left out: 1 file* in the result | — |
| [ ] | A build that runs past `timeoutSeconds` is *timed out*; a container with a network gets *refused* when `allowNetwork` is false | — |
| [ ] | **Set up builds (guided setup option 6, new):** on a PC without Docker or WSL builds, with SourceVault installed. Choose both targets: it turns on Windows Containers (restart, then 6 again), downloads and checks Docker's engines and Ubuntu's image, creates both containers, sets up TLS, writes `build.json` and grants access. `-Status` ends with both targets and the build service running. Run it a second time: every step says *already* / *kept*. Then build `dotnet-hello` on both targets | admin; **restart**; downloads |
| [ ] | **Own build image (Java/Ant, new):** in the SourceVault distro, `docker pull eclipse-temurin:21-jdk` and read its ID (`docker image inspect --format '{{.Id}}'`). `New-BuildContainer.ps1 -Distro SourceVault-Ubuntu -OwnerImageId <ID> -Name sourcevault-build-java -TargetName java` lists `java`/`javac`. Add the printed `java` target (with the Linux target's `tls` block) to `build.json`; setup `-Update -WithBuilds`. In the editor, **Build steps** on `java` creates `build.sh` with the project's Ant (`sh ant/bin/ant -noinput dist && cp -r dist/. /sourcevault/out/`). **Build:** the output folder has the jar(s). A wrong `-OwnerImageId` is refused before any container is created | download (the image, once) |

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
