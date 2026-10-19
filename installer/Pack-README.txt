SourceVault Pack
================

Encrypts a project folder on THIS computer so it can be imported into one SourceVault vault PC. The package
can only be opened by that vault PC's TPM, and it is signed by this computer's TPM. Use synthetic code until the
security review is done.

Needs: Windows 10/11 with a TPM 2.0 that is on and ready (tpm.msc). Nothing to install: keep this folder together
(pack\ runs on runtime\). No administrator rights needed.

1. Once, on this computer, create its signing key and sender file:
     pack\SourceVault.Pack.exe init
   Give the printed SourceVault-sender-<computer>.json to the vault PC's administrator. They register it with
     Setup-SourceVault.ps1 -RegisterSender <file>
   and compare the fingerprint it shows with the one printed here.

2. Get the vault PC's recipient file (an operator there runs: SourceVault.Import.exe --export-recipient <file>).
   Check that its fingerprint matches what the vault PC printed.

3. Pack a folder (first with --dry-run to see what is included and skipped):
     pack\SourceVault.Pack.exe C:\code\my-app --to recipient.json --out my-app.svpkg --dry-run
     pack\SourceVault.Pack.exe C:\code\my-app --to recipient.json --out my-app.svpkg
   Only UTF-8 text files up to 256 KiB are packed (at most 1,024); binaries, .git, bin, obj, node_modules and
   similar folders are skipped and listed. Your folder is not changed.

4. Carry my-app.svpkg to the vault PC any way you like (it is encrypted) and import it there:
     SourceVault.Import.exe --package my-app.svpkg --project "My app"

Be aware: the code on this computer stays ordinary, unencrypted files. Pack does not protect or delete them.
