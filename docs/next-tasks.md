# Next implementation tasks

Updated 2026-10-18. The vault service, encrypted store, editor vault mode, importer, file operations, history, merge and container builds are implemented, and `scripts/Verify.ps1` passes (see [status](status.md)). Owner-run lab checks have passed for the installed service, key provisioning, vault root, catalog tamper checks, pipe DACL and lab client. Everything added since then is tested in process only. Do not introduce a software key provider to bypass the hardware gates, and use synthetic inputs only.

## Owner lab steps (next)

These need an installed service, administrator rights or real hardware, so they are owner-run.

1. **Install and smoke-test the editor** (step 4 of `docs/editor-vault-design.md`). Rebuild the package (`scripts\Build-Installer.ps1`) and run `Setup-SourceVault.ps1 -Update` from it. Then, in `SourceVault.Editor.exe --vault` from the Start menu:
   - open a synthetic project, edit and save;
   - make a conflict with the lab client, then Merge and Reload;
   - rename and move files and folders, delete and restore files, rename a project;
   - History: compare and restore;
   - Windows lock (Win+L), the capture check, and a profile canary scan after exit;
   - delete a project, and run `SourceVault.Import.exe --delete-source` on a synthetic folder.
2. **Builds.**
   1. Run `-Update -WithBuilds`, write `build.json` from `installer/build.example.json`, then run `Grant-BuildDockerAccess.ps1`.
   2. For WSL, run `New-WslDockerTls.ps1 -Distro <name>` and apply the printed dockerd settings.
   3. Run `-Update -WithBuilds` again, then build a synthetic project on each target.
   4. Record what works, especially the Windows-container archive copy (copying into a running Hyper-V-isolated container may be unsupported).
3. **Reboot and failure cases.** Reboot, then rerun `Test-ServiceLab.ps1` (does the service start at boot and still open its key?). Test a missing key and a missing or invalid configuration.
4. Run `scripts/Verify.ps1` once from an elevated PowerShell so the 5 symlink checks run instead of being skipped.

## Development work

1. **Editor native validation (T06).** Run and record the rest of the native checklist in `docs/editor-preview-checks.md`. Clipboard blocking and one persistence canary are already recorded. Still to do:
   - developer tools and browser accelerators;
   - registry/policy WebView2 overrides;
   - rejecting bad navigations, resources and messages;
   - recording the capture-exclusion result in vault mode;
   - confirming the stricter `PreviewPolicy` loads every Monaco asset, worker and the codicon font;
   - a fuller persistence inspection (compressed Chromium stores, pagefile and hibernation are not yet covered).
2. **Pipe and client identity.** The editor refuses a pipe not owned by `NT SERVICE\SourceVault`, and the service accepts only its installed client executables by path. Still open:
   - a signer/version check on the client executable;
   - process-lifetime and PID-reuse races;
   - genuine separate-account and remote-client denial on a lab machine;
   - lock/revocation behavior across several editors.
3. **Storage hardening (T04/T05).**
   - power-loss tests (exception fault injection exists; real interruption does not);
   - the skipped reparse-point cases, run in a suitable lab;
   - TPM throughput beyond 200 files;
   - key rotation, within the no-recovery decision.
4. **Build hardening.** Builds publish validated artifacts straight to `artifactRoot`. Still open:
   - a quarantine/release step for artifacts;
   - a policy for source maps, debug/PDB files, archives and build metadata;
   - a real initial compiler.
   Builds remain an intentional export path, whatever the policy (see `docs/files-history-builds-design.md`).
5. **Release.** Offline installer and update signing, a supported hardware/OS matrix, and an independent security review.

## Builder continuation

Builds now use administrator-configured Docker containers through `NT SERVICE\SourceVaultBuild` (owner decisions D3/D4, 2026-10-18). The original T03 plan was a dedicated Hyper-V Linux VM with an authenticated guest channel and VM-management helper. It is not built, and `spikes/builder` stays a synthetic spike with an untested guest wrapper. Decide whether T03 is retired or kept as a stronger later profile. Until then, make no RAM-only or isolation claims for builds.

## Decisions before real source or public release

- Confirm operator/administrator threat model and intentional-export policy.
- ~~Confirm device-only loss policy versus an owner recovery envelope.~~ Decided 2026-10-18: device-only, no recovery. Do not add a recovery envelope, escrow or key export.
- Approve supported hardware/OS/runtime and offline update strategy.
- Choose a real initial compiler and artifact policy. Readable JavaScript output does not conceal implementation.
- Signing/release ownership.
- ~~License.~~ Decided 2026-10-18: MIT, chosen by the owner in the GitHub repository (`LICENSE`).
- ~~Per-project permissions.~~ Decided 2026-10-18: none. Every member of *SourceVault Operators* can open every project; access is limited by that group alone.
