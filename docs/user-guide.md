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
- Files can't be deleted or renamed individually yet.

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

### When someone else changed the file

If the file changed in the vault since you opened it, Save does **not** overwrite it. A yellow banner appears:

- **Reload their version** throws away your unsaved edits and shows the current file.
- **Keep editing** hides the banner, but saving will keep failing until you reload. There is no merge yet, so copy your intent by hand.

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
- Don't add people to *SourceVault Operators* who shouldn't read every project: there are no per-project permissions yet.
- Don't run the editor from anywhere except the Start menu shortcut. Other copies are rejected.
- Don't screen-share the editor and expect the viewer to see it. Capture exclusion blanks it, though not reliably for every tool, so don't rely on it either way.

## Limits (be aware)

- **Administrators of this computer** can act as the service and read the vault. SourceVault protects against other users and stolen disks, not against the machine's own admins.
- **Text of open files** is in the editor's memory, and Windows may write memory to the pagefile or hibernation file. Capture exclusion is best effort; cameras and some tools bypass it.
- **No recovery, by design.** No backup tool and no migration to another computer either: if this computer's TPM is cleared or the computer is lost, the vault is gone.
- **Not yet verified:** that everything works after a reboot (automatic start at boot), and an independent security review.
- **Builds are not available yet.** The vault stores and edits files only.
- **Projects can be deleted; files can't** be deleted or renamed yet, and projects can't be renamed.
