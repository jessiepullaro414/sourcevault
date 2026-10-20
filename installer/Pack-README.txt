SourceVault Pack
================

Encrypts a whole project folder on THIS computer so it can be imported into one SourceVault vault PC. The package
can only be opened by that vault PC's TPM, and it is signed by this computer's TPM. Use synthetic code until the
security review is done.

Needs: Windows 10/11 with a TPM 2.0 that is on and ready (tpm.msc). Nothing to install: keep this folder together
(pack\ runs on runtime\). No administrator rights needed.

THE WINDOW: double-click pack\SourceVaultPack.exe

  1. This computer   - "Set up this computer" once: creates the signing key in the TPM and saves this computer's
                       sender file. Give that file to the vault PC's administrator, who registers it with
                       Setup-SourceVault.ps1 -RegisterSender <file> and compares the check code shown here.
  2. The vault PC    - "Choose recipient file": the file an operator there made with
                       SourceVault.Import.exe --export-recipient <file>. Check its check code matches the vault PC's.
  3. Project folder  - "Choose project folder": every subfolder is included. The Included and Skipped tabs show
                       exactly what goes in and why the rest is left out.
  Then "Create encrypted package", choose where to save it, and confirm.

THE COMMAND LINE (same rules, for scripts): pack\SourceVault.Pack.exe

     pack\SourceVault.Pack.exe init
     pack\SourceVault.Pack.exe C:\code\my-app --to recipient.json --out my-app.svpkg --dry-run
     pack\SourceVault.Pack.exe C:\code\my-app --to recipient.json --out my-app.svpkg

What goes in: every file from the folder and all its subfolders, text or binary (icons, .lib and .dll files,
UTF-16 .rc files), up to 1 GiB each, at most 25,000 files and 8 GiB in all. Links, files over 1 GiB, names the vault
can't hold, and folders such as .git, bin, obj and node_modules are skipped and listed. Your folder is not changed.

Then carry the .svpkg file to the vault PC any way you like (it is encrypted) and import it there:
     SourceVault.Import.exe --package my-app.svpkg --project "My app"

Be aware: the code on this computer stays ordinary, unencrypted files. Pack does not protect or delete them.
