# SourceVault — setup and user guide

SourceVault keeps source code encrypted on disk with a key that never leaves this computer's TPM chip. You read and edit the code only through the SourceVault editor, which gets each file from a background Windows service. Nothing is decrypted until you open a file.

> **This is a development build.** Use **synthetic (made-up) code only**: nothing you would mind losing or leaking. Real source should wait for an independent security review and for the open items in "Limits" below.

---

## Part 1: Administrator setup

### Requirements

- Windows 11, or Windows 10 version 2004 or newer, 64-bit.
- **TPM 2.0, present and ready.** Check in *Windows Security → Device security → Security processor*. If it's missing, enable it in the BIOS/UEFI.
  - **Never clear a TPM that protects a vault.** Clearing it makes the vault permanently unreadable.
- **Microsoft Edge WebView2 Runtime.** Windows 11 includes it. Setup never downloads anything; if it's missing, install Microsoft's "Evergreen Standalone" runtime first.
- An administrator account, used for setup only.

### Install

1. Copy `SourceVault-Setup-<date>.zip` to the computer.
2. Right-click the zip → **Properties** → tick **Unblock** (if shown) → **OK**. Then extract it.
3. Open **Windows PowerShell as administrator** (Start → type *PowerShell* → *Run as administrator*) and go to the **extracted package folder**, the one containing `Setup-SourceVault.ps1` and `payload`:
   ```powershell
   cd "C:\path\to\SourceVault-Setup"
   ```
   Every Setup and Uninstall command below runs from this folder. If you built the package yourself, it is `artifacts\installer\SourceVault-Setup` in the source checkout, not the checkout's root or `installer` folder.
4. Optional: run the pre-flight check, which changes nothing:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Setup-SourceVault.ps1 -CheckOnly
   ```
   It confirms that every package file matches its recorded hash, and checks Windows, the TPM and WebView2.
5. Install, naming the Windows accounts that will use SourceVault. They become **operators**:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Setup-SourceVault.ps1 -Operators "COMPUTER\alice","COMPUTER\bob"
   ```
   - With no `-Operators`, the account running setup becomes the only operator.
   - Add `-WithSyntheticDemo` to create a small fictional project to try things on.

Setup prints `OK` for each step and stops at the first problem with an explanation. It does the following:

| Step | What it does |
|---|---|
| Operators group | Creates the local group **SourceVault Operators** and adds the operators. Only its members can use SourceVault. |
| Program files | Installs to `C:\Program Files\SourceVault` with its own private .NET runtime, checks that ordinary users can't modify anything there, and records the hash of every file. |
| Service | Registers **SourceVault** running as its own account, `NT SERVICE\SourceVault`, starting automatically at boot (delayed start). |
| TPM key | The service creates its own **decrypt-only, non-exportable** key in the TPM, then tests it. |
| Vault folder | Creates `C:\ProgramData\SourceVault\vault`, readable only by the service, SYSTEM and Administrators. Operators can't open it directly. |
| Start | Starts the service and confirms it verified the vault and is listening. |
| Shortcut | Adds **SourceVault Editor** to the Start menu for everyone. |

6. **Every operator must sign out of Windows and back in once.** Until they do, the editor reports "not in SourceVault Operators", because Windows applies group membership only at sign-in.

### Check, update, add people, remove

- **Health check** (as administrator):
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\Setup-SourceVault.ps1 -Status
  ```
  Reports the service account and state, whether installed files still match their hashes, the operators, and whether the vault folder's permissions are trusted.
- **Installed files are checked on every connection.** The service accepts the editor and importer only while every file in their folders matches `install-manifest.sha256`. If a file was changed, added or removed, they are refused ("only accepts the installed…"); run `-Update` from the package to restore them.
- **Update to a newer package.** Close all editors, then from the new package:
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\Setup-SourceVault.ps1 -Update
  ```
  Program files are replaced. The key, the vault and the settings are kept.
- **Add or remove an operator:** *Computer Management → Local Users and Groups → Groups → SourceVault Operators*, or `Add-LocalGroupMember -Group "SourceVault Operators" -Member "COMPUTER\name"`. The person signs out and in afterwards.
- **Uninstall** (as administrator, from the package):
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\Uninstall-SourceVault.ps1
  ```
  - By default this keeps the encrypted vault, so a later install **on this same computer** can open it again.
  - `-DeleteVaultData` permanently deletes all projects. It asks you to type a confirmation.
  - `-RemoveOperatorsGroup` also removes the group.
  - The TPM key itself is never deleted.

### Builds (optional): run builds in your Docker containers

SourceVault can send a project's saved files to an **already-running, named Docker container**, run the build command there, and copy the build's output to a folder on this PC. A separate service, **SourceVault Build** (`NT SERVICE\SourceVaultBuild`), does this; the vault service never gets Docker access.

**The C#/.NET build container.** The supported compiler is Microsoft's .NET SDK image (`mcr.microsoft.com/dotnet/sdk`), pinned by digest, on Docker inside a WSL distro. To create it, run from the package folder, as your normal user, with Docker running in the distro:
```powershell
powershell -ExecutionPolicy Bypass -File .\New-BuildContainer.ps1 -Distro Ubuntu-24.04
```
- **What it creates:** `sourcevault-build-linux`, with no network, dropped privileges, and restart with the engine.
- **What it checks:** that an offline test build runs.
- **What it prints:** the `build.json` target to use.

Each project needs a `build.sh` at its top level that writes its output to `/sourcevault/out`. The package's `samples\dotnet-hello` is a fictional example: import it, then build it.
- **No network in builds:** NuGet packages can't be downloaded, so projects with package references don't build yet.

**The Windows build container** builds Windows-only code, such as WPF or WinForms projects. It needs Windows 11 Pro, Enterprise or Education.
1. Turn on the **Containers** and **Hyper-V** features (*Turn Windows features on or off*), then restart.
2. Install a Docker engine that runs Windows containers, for example Docker Desktop switched to Windows containers.
3. From the package folder, as administrator:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\New-WindowsBuildContainer.ps1
   ```
   - **What it creates:** `sourcevault-build-windows`, from Microsoft's Windows Server Core .NET SDK image (about 2.7 GB), pinned by digest, with no network.
   - **What it checks:** that a file can be copied into the running container and back out. That is the step the build service needs, and Docker may refuse it for Hyper-V-isolated containers. If this check fails, the Windows target can't build yet; keep the output.
   - **What it prints:** the `windows` target for `build.json`.

Each project builds on the Windows target with a `build.cmd` at its top level that writes to `C:\sourcevault\out`.

**A project with a Windows part and a Linux part** has both scripts: `build.cmd` builds the Windows part and `build.sh` the Linux part. Every build sends the whole project; pick the target in the editor.

1. **Install the build service** (as administrator, from the package folder):
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Setup-SourceVault.ps1 -Update -WithBuilds
   ```
   This registers the service and creates `C:\ProgramData\SourceVault\config\`. Only administrators can change it, and operators can't read it. It also puts `build.example.json` there.
2. **Write the configuration.** Copy `build.example.json` to `build.json` in that folder and edit it. For each target, set:
   - `name`: what operators pick;
   - `engine`: the Windows engine's pipe, or the WSL engine's TLS port;
   - `container`: its name;
   - `platform`: `linux` or `windows`;
   - `sourcePath`: where the project goes inside the container;
   - `command`: what runs there;
   - `artifactPath`: the folder whose contents are the build output.

   Also set `artifactRoot`: where output lands on this PC. Only an administrator can set these paths. Every field is described in `docs/files-history-builds-design.md`.
3. **Give the build service Docker access:**
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Grant-BuildDockerAccess.ps1            # Docker Desktop: group docker-users
   powershell -ExecutionPolicy Bypass -File .\Grant-BuildDockerAccess.ps1 -Group docker   # standalone dockerd with "group": "docker"
   ```
   **Docker access is administrator-equivalent** on this PC. That's why only the build service gets it.
4. **For a Docker engine inside WSL**, set up TLS client certificates:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\New-WslDockerTls.ps1 -Distro Ubuntu-24.04
   ```
   It prints the Docker settings to apply in the distro and the `tls` block for `build.json`. Plain TCP without TLS is refused. The Linux target works only while that WSL distro and its Docker engine are running.
5. **Apply:** run `Setup-SourceVault.ps1 -Update -WithBuilds` again. It validates `build.json` and creates the artifact folder (the build service can write, *SourceVault Operators* can read, nobody else has access), then starts the build service. `-Status` reports the build service and the configuration.

Containers should have **no network** (`--network none`). Builds are refused otherwise, unless a target sets `"allowNetwork": true`.

**Files that can reveal source are left out of the output** by default, and the build log lists them:
- source maps (`.map`);
- debug symbols (`.pdb`, `.mdb`, `.debug`, `.dwo`, `.dwp`, `*.dSYM` folders);
- compiler intermediates (`.obj`, `.o`, `.ilk`, `.iobj`, `.ipdb`);
- scripts and stylesheets (`.js`, `.mjs`, `.cjs`, `.css`) that end with an inline `sourceMappingURL=data:` map.

To publish a kind anyway, add it to the target, for example `"keepFileTypes": [".pdb", ".dsym", "inline-source-map"]`. This only catches the usual accidents: a build can still copy source into any file it keeps.

---

## Part 2: Using SourceVault

### Open the editor

Start menu → **SourceVault Editor**. It connects to the vault by itself.

- The title bar says **Vault**. If it says *Synthetic preview*, you started the demonstration mode, not the vault.
- The right-hand panel shows **Capture: Excluded (best effort)**. The window asks Windows to keep it out of screenshots and screen recordings.

If you see a lock screen instead, it explains why:

| Message | What to do |
|---|---|
| *not in 'SourceVault Operators'* | Ask your administrator to add you, then sign out and back in. |
| *service is not running* | Ask your administrator to run `Setup-SourceVault.ps1 -Status`. |
| *only accepts the installed SourceVault editor* | Start the editor from the Start menu, not from a copy elsewhere. |
| *all service connections are in use* | Up to 4 editors can connect at once. Close one and try again. |

### Projects and files

- **Choose a project** from the list, or type a name and **Create project**. Names are 1–64 characters.
- **Open a file** from the Explorer. Only files you open are decrypted; closing a tab removes its text from the editor.
- **+ New file** creates a file. Use `/` for folders (`src/app/main.ts`), up to 260 characters and 32 levels. Names can't contain `\ : * ? " < > |`. Names aren't case-sensitive, so `Main.ts` and `main.ts` count as the same name.
- **Rename, move or delete a file:** click **⋯** on its row in the Explorer.
  - **Rename / move** takes a full path, so `src/a.ts` → `lib/a.ts` moves it.
  - **Rename folder** moves every file in that file's folder at once.
  - **Delete** asks first, and is refused while the file has unsaved edits. Deleted files can be restored: **Deleted files** in the Explorer lists them with who deleted them and when; **Restore** brings one back with its history (under a new path if its old one is taken).
  - **History** lists every saved version of the file: when, who, and the path it had. Choosing one compares it side by side with the current version (read-only). **Restore this version** puts that text into the editor as unsaved edits; saving makes it the newest version, so history is never rewritten.
  - Open tabs and unsaved edits follow a renamed file.
- **If someone else deletes a file you have unsaved edits in,** it shows *Deleted by someone else*. **Save** creates it again with your text, or **⋯ → Delete** discards your version.
- **Rename a project:** **Switch project** → **Rename…** next to it.
- **Switch project** returns to the project list. Save or reload edited files first.
- **Save** with **Ctrl+S** or the Save button. Each save becomes a new encrypted, unchangeable revision in the vault. Until you save, your edits exist only in the editor; a dot on the tab means unsaved changes.
- Limits: 256 KiB per file (UTF-8 text), 1,024 files per project, 256 projects.

### Delete a project

In the project list (**Switch project**), click **Delete…** next to the project, **type its exact name** (letter case matters), and click **Delete project**.

- This permanently deletes the project and **every saved revision** of every file in it, for everyone. There is no undo and no recycle bin.
- Save or reload any edited files of that project first.
- Anyone else who has the project open gets "no longer exists" on their next action.
- If some encrypted files were in use, the project disappears at once and the service removes the leftover files later (at the latest when it next starts).
- Like any deletion, it doesn't securely erase data from an SSD. The leftover data is encrypted, though, and the service no longer lists it.

### Import an existing folder

Use the importer to bring a folder of code into a **new** project. Copy/paste is blocked, so this is how existing code gets in. Run it from a normal (not administrator) PowerShell, as an operator, while the service is running:

```powershell
& "C:\Program Files\SourceVault\client\SourceVault.Import.exe" "C:\code\my-app" --project "My app" --dry-run
& "C:\Program Files\SourceVault\client\SourceVault.Import.exe" "C:\code\my-app" --project "My app"
```

- **`--dry-run` first:** it lists what would be imported and what would be skipped, with reasons. It doesn't connect and changes nothing.
- **What's imported:** UTF-8 text files up to 256 KiB, with their folder structure. Up to 1,024 files per project.
- **What's skipped:**
  - binaries and non-UTF-8 files (images, DLLs, UTF-16 text)
  - files over 256 KiB
  - links and junctions, which are never followed
  - the folders `.git`, `node_modules`, `bin`, `obj`, `dist`, `build`, `out`, `target`, `.vs`, `.idea`, `__pycache__`, `.venv`, `venv`, `packages`, `.gradle`, `.next` and `coverage`
  - more folders by name with `--exclude <name>` (repeatable)
- **Always a new project:** it never adds to or overwrites an existing one. If the name is taken, choose another.
- **Verified:** after importing, it decrypts **every** file through the service and compares it byte for byte with your original. It ends with "all N decrypted and matched the originals" or lists any file that didn't.
- **Changed files:** if a file changes while the import runs, the import stops. Run it again into a new project name.
- **Your original folder is not changed** unless you add `--delete-source` (below).
- **A half-finished import** (stopped or failed) stays in the project list. Delete it in the editor (see "Delete a project") and import again.

#### Removing the originals: `--delete-source`

```powershell
& "C:\Program Files\SourceVault\client\SourceVault.Import.exe" "C:\code\my-app" --project "My app" --delete-source
```

- It runs **only after every file has verified**. If anything fails to verify, nothing is deleted.
- It then explains what will happen and asks you to **type `DELETE`**. Anything else keeps everything.
- It deletes **only the files it imported**, and only those still byte-for-byte identical to the vault's copy. Each is opened exclusively, overwritten with zeros, then deleted. Folders left empty are removed; the top folder itself is kept.
- **Kept, and listed with a reason:** skipped files (binaries, large files and so on), excluded folders, files changed since the import, read-only files, files in use, files with other hard links, and anything reached through a link or junction.
- **`.git` is kept.** Its history still contains your source. Delete or move it yourself if that matters.
- **Not a secure erase.** SSDs, backups, shadow copies, OneDrive/Dropbox sync and the recycle bin of other tools may keep earlier copies.
- After this, **the vault project is the only copy**: readable only on this computer, with no recovery if the TPM is cleared. Think twice before using it on code you can't recreate.

### Build a project

**Builds** in the Explorer opens the build view (only if your administrator set up builds).

- Pick a **target** and click **Build saved files**. The build uses the files **as saved in the vault**; unsaved edits are not included. One build per target runs at a time; up to 4 more wait.
- The list shows each build with when, who and how long. It refreshes while a build is queued or running.
- Click a build to see its result, its **output folder** on this PC (*Output (N files)*), and its **log**. The log is stored encrypted in the vault and shown read-only here, like files; copying is blocked.
- **Output files are ordinary, unencrypted files** in that folder, readable by every operator. That is their purpose.
- **Left out:** source maps, debug symbols and similar files are not published. The result says how many, and the end of the log lists them. Your administrator can allow a kind.
- Common results:
  - *failed*: the build command returned an error (see the log);
  - *timed out*: it ran longer than allowed and was stopped;
  - *refused*: unsafe output such as links or bad names, or a container that has network access when it shouldn't;
  - *could not run*: the container, the Docker engine or the build service isn't available; ask your administrator.

### When someone else changed the file

If the file changed in the vault since you opened it, Save does **not** overwrite it. A yellow banner appears:

- **Merge** combines your edits with theirs. Changes to different lines are combined automatically. Where you both changed the same lines, a merge view shows *Yours* and *Theirs* for each place: choose **Take yours**, **Take theirs** or **Take both**, then **Use merged text**. The result is in the editor as unsaved edits (*Merged · not saved yet*): check it, then **Save**. If they changed the file again in the meantime, Save reports the conflict again and you merge once more.
- **Reload their version** throws away your unsaved edits and shows the current file.
- **Keep editing** hides the banner, but saving keeps failing until you merge or reload.

Different files never conflict: two people can edit different files of the same project at the same time.

### Locking and sessions

- **Lock session** (top right) hides the editor and ends your vault session.
- **Windows lock** (Win+L), **sign-out** and **disconnects** lock the editor immediately.
- Sessions also end after **5 minutes without activity** or **30 minutes in total**. Just click **Unlock vault**.
- **Unsaved edits are kept** while locked. On unlock, the editor checks each one against the vault, and any file someone else changed meanwhile is marked `!` and treated as a conflict.

### What the editor deliberately blocks

**Copy, cut, paste, drag-and-drop, right-click menus, printing, developer tools and browser shortcuts are all disabled.** This is intentional, to make accidental copying out harder. Type code in; don't paste it.

---

## Part 3: Correct use: do and don't

**Do**
- Keep using synthetic code until the security review is done.
- Lock (Win+L) whenever you step away.
- Save often. Saved revisions are encrypted and kept; unsaved edits live only in the editor's memory.
- Ask your administrator to run `-Status` if anything looks wrong.

**Don't**
- **Don't clear or reset the TPM**, reinstall Windows, or replace the motherboard on a vault computer. The key exists only in this TPM, there is **no recovery key**, and the vault would become permanently unreadable. Check with your administrator before BIOS/UEFI or TPM firmware changes.
- **Don't try to copy `C:\ProgramData\SourceVault` to another computer as a backup.** The files can only be decrypted by this computer's TPM. There is no supported backup or migration yet.
- Don't add people to *SourceVault Operators* who shouldn't read every project: every operator can open every project, by design (owner decision 2026-10-18).
- Don't run the editor from anywhere except the Start menu shortcut. Other copies are rejected.
- Don't screen-share the editor and expect the viewer to see it. Capture exclusion blanks it, though not reliably for every tool, so don't rely on it either way.

## Limits (be aware)

- **Administrators of this computer** can act as the service and read the vault. SourceVault protects against other users and stolen disks, not against the machine's own admins.
- **Text of open files** is in the editor's memory, and Windows may write memory to the pagefile or hibernation file. Capture exclusion is best effort; cameras and some tools bypass it.
- **No recovery, by design.** No backup tool and no migration to another computer either: if this computer's TPM is cleared or the computer is lost, the vault is gone.
- **Not yet verified:** that everything works after a reboot (automatic start at boot), and an independent security review.
- **Builds are an export path.** A build runs the project's own scripts, so anyone who can edit a project can make its build copy the source into the output folder, which holds ordinary files. With network access a build could also send it elsewhere. The build containers hold plain source while a build runs, and caches or copies a build makes can stay in the container. The administrator-only paths control *where* output comes from, not *what* it contains.
- **Builds are not yet verified against a real Docker engine.** They were tested against a simulated engine; the first real run is a lab step.
- **History is kept forever** for the life of a project: every saved version stays encrypted on disk until the project is deleted. Files saved before this version start their history at the version they had then.
