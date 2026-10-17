# Windows service host — development baseline

The service is an actual own-process SCM executable using BCL P/Invoke. It is deliberately a **metadata-only, non-operational vault host**. It validates its service identity, fixed configuration and an already-provisioned TPM wrapping key, then waits for STOP or SHUTDOWN. It has no named-pipe listener, source import/read/edit/build API, software-key flag, key deletion, or automatic provisioning path. Existing IPC experiments remain separate.

**Explicit provisioning (lab).** An administrator can start the service once with the fixed start parameter `provision-key` (`sc start SourceVault provision-key`). Only under `NT SERVICE\SourceVault` (checked before any provider access), `TpmKeyProvisioner.CreateDecryptOnly` then:

1. creates the configured key with `PCP_KEY_USAGE_POLICY = NCRYPT_PCP_ENCRYPTION_KEY`, refusing if the key already exists and never overwriting;
2. requires both private-key exports to be refused;
3. reopens the key through `TpmKeyProtector.OpenExisting`, with the same checks used at normal startup;
4. runs one synthetic wrap/unwrap;
5. reports STOPPED with a bounded service-specific exit code.

Any other start parameter stops with Win32 87. A key that fails a later check is left in place for administrator review. The runtime adapter remains open-only. Install, provision, test and uninstall scripts are in [`scripts/lab`](../scripts/lab/README.md).

## Components and commands

- `SourceVault.Windows.TpmKeyProtector.OpenExisting(keyName)` implements storage `IKeyProtector` and `IDisposable`. It enforces current primary identity `NT SERVICE\SourceVault` by SID before opening provider/key. It opens only Microsoft Platform Crypto Provider current-identity persistent RSA-2048 keys, with exactly nonexportable and decryption-only policies, using silent open. Hardware/software provider flags are checked. Wrap/unwrap use OAEP-SHA256 with exact 32-byte/256-byte inputs and zero invalid decrypted key buffers. No persisted key is opened by the metadata probe.
- `SourceVault.Service.exe metadata-probe` checks only hardware provider availability and emits fixed safe metadata. A passing probe does not establish TPM key usability or a deployment guarantee.
- `SourceVault.Service.exe service` immediately connects to SCM. Direct console launch fails with a fixed unavailable result; there is no console-service fallback.
- `SourceVault.Service.Tests.dll` executes synthetic configuration, lifecycle and key-policy checks. The lifecycle initializer seam is internal, friend-visible to the test assembly; runtime configuration cannot select an injected implementation.

Configuration is read only from `AppContext.BaseDirectory/sourcevault.service.json`, never the working directory, arguments, environment or caller-supplied paths. Maximum 4096 bytes; exact required properties, no duplicates/unknown fields/comments/trailing commas; no key names outside bounded `SourceVault.Lab.*`. All directory ancestors and the config file are checked for reparse points. Application directory ACL trust is a deployment requirement: these checks cannot defeat an administrator or a malicious writer racing in a writable installation directory. Do not run the service from this development checkout.

Example configuration for an administrator-reviewed lab installation (do not place production data in it):

```json
{"schemaVersion":1,"mode":"MetadataOnly","keyName":"SourceVault.Lab.ServiceGate"}
```

Lifecycle publishes START_PENDING, RUNNING only after existing-key validation, STOP_PENDING on STOP/SHUTDOWN, and STOPPED once after disposal. STOP/SHUTDOWN signals a cancellation event and returns without waiting for teardown. Accepted controls are zero while pending/stopped and STOP|SHUTDOWN while running. Initialization/disposal failures report fixed Win32 1064, never exception text. No periodic fake checkpoints are emitted. The 15-second pending hint is a lab starting point, not a measured guarantee. Synchronous CNG key-open cannot be forcibly canceled: a stop during initialization signals cancellation but completion must wait for CNG to return. Real timeout/hang behavior remains a gate.

## Lab installation and provisioning runbook (not executed)

1. Use a disposable Windows 11 TPM2 lab PC/VM configuration under administrator control; synthetic fixtures only. Decide recovery policy before importing anything valuable. Never clear a real TPM as a test step.
2. Publish reviewed release binaries for win-x64 to a dedicated administrator-owned installation directory. Check executable/config/dependency directories and every ancestor disallow ordinary-user writes/reparse-point substitution. Use an administrator-owned configuration file. Record verified file hashes. Framework-dependent publishing requires an independently trusted installed .NET 10 runtime; apphost files alone are insufficient. Avoid runtime downloads on the offline workstation.
3. An administrator may create an own-process demand-start service named exactly `SourceVault`, with an explicitly quoted absolute executable path and fixed `service` argument, running as virtual account `NT SERVICE\SourceVault` with no password. Keep startup manual during validation; do not enable recovery restart loops. Confirm primary token SID, required service logon policy, and minimal service object access separately. `scripts/lab/Install-ServiceLab.ps1` implements this step; it records evidence only when the owner runs it.
4. **Provisioning (now implemented as `provision-key`, unverified until the owner runs `scripts/lab/Provision-ServiceKey.ps1`):** originally a blocked gate: existing TPM spike creates keys under its current interactive identity; that key is not the service account's current-user key. Do not use it as the service key or switch silently to machine scope. Design/review a dedicated tightly controlled service-identity provisioning operation before this step. Establish whether the virtual account has a usable loaded CNG user profile on the target Windows build. Verify profile availability, exact provider and persistence, exact RSA/OAEP support, nonexportability, key object ACLs, failure on missing key and wrong account. This version does not create the service key, so a fresh installation is expected to fail startup safely until provisioning is completed.
5. Write the fixed configuration only after provisioning is reviewed. Start through SCM; verify START_PENDING -> RUNNING. Stop and repeat start, then shutdown in the lab. Check resource disposal and no orphan host process. Test wrong/missing config/key, duplicate/unknown config properties, wrong service identity, exportable/software-provider/machine-scope keys, unavailable TPM and locked/unavailable service profile. Record outcomes without logging key/source/exception text. Validate SCM failure/status reporting, timeouts, control handler responsiveness and pending-state timing on actual hardware.
6. Do not connect encrypted storage or IPC/source endpoints until service identity, ACL, key provisioning/profile, recovery and deployment reviews pass. Integrating the store remains a separate security-reviewed milestone.

## Verification

BCL only, target .NET 10 Windows. Restore explicitly with repository `NuGet.Config` (empty package sources), then build with `-m:1 -p:UseSharedCompilation=false`; execute the test DLL directly with the local dotnet host. Set task-local APPDATA and DOTNET_CLI_HOME and DOTNET_GENERATE_ASPNET_CERTIFICATE=false in restricted environments. No install, ACL, key provisioning, real TPM operation or SCM connection is part of unit checks.

Official references used for ABI and behavior:

- [StartServiceCtrlDispatcherW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-startservicectrldispatcherw): null-terminated service table, dispatcher timing and console launch failure.
- [RegisterServiceCtrlHandlerExW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-registerservicectrlhandlerexw): register before reporting service status.
- [HandlerEx](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nc-winsvc-lphandler_function_ex): callback ABI and control return values.
- [SetServiceStatus](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-setservicestatus): pending controls, progress checkpoints, cleanup before the single STOPPED report.
- [Service state transitions](https://learn.microsoft.com/en-us/windows/win32/services/service-status-transitions): accepted controls and transition semantics.

Remaining lab gates: service provisioning/profile, actual hardware-provider behavior and synthetic key roundtrip under that identity; ACL and installation trust; SCM start/stop/shutdown behavior; crash/timeout handling; authenticated IPC integration; storage integration; and recovery/security review. Compilation and synthetic checks do not close any of these gates.

## Lab evidence

2026-10-17, this development machine (Intel firmware TPM 2.0), owner-run elevated scripts:

| Step | Result |
|---|---|
| `Install-ServiceLab.ps1` | Service `SourceVault` registered as `NT SERVICE\SourceVault`. A later read-only query confirmed manual start, `SERVICE_SID_TYPE: UNRESTRICTED`, and the exact quoted path. 202 files were hashed, and a later re-hash found 0 mismatches. No user-writable files. |
| `Provision-ServiceKey.ps1` | Outcome `Created`: decrypt-only key `SourceVault.Lab.Service01` created under the service identity. Both private exports were refused, the startup policy checks passed, and a synthetic roundtrip passed. |
| `Test-ServiceLab.ps1` | RUNNING after 1.3 s, with process owner `NT SERVICE\SourceVault`. Stopped in 0.7 s with Win32 exit 0, and the service process exited. |

Still open: start after reboot; missing/wrong key and config cases under SCM; shutdown during start; vault root ACLs; mutual pipe checks.

## Vault-root gate

The vault root is fixed at `%ProgramData%\SourceVault\vault` (`VaultRoot.Path`). It is never taken from config, arguments or the environment. On every normal start the service checks identity, then config, then validates the root with `TrustedStorageRoot` against SYSTEM, Administrators, TrustedInstaller and its own service SID, and only then opens its key. An untrusted or missing root stops the service before RUNNING with Win32 1066, service-specific code 10. The store is still not opened.

Console commands, both read-only:

- `vault-root-check` prints findings as codes, locations and SIDs; exit 0 means trusted, 4 means not. Run it elevated, because the root grants nobody else read access.
- `vault-root-sddl` prints the exact root DACL the validator requires. `scripts/lab/New-VaultRoot.ps1` uses it to create the root, then verifies the result.

Owner-run lab, 2026-10-17:

| Step | Result |
|---|---|
| `Update-ServiceLab.ps1` | New binaries installed; config, runtime, registration and key kept; 202 files re-hashed. |
| `Test-ServiceLab.ps1` with no vault root | Refused as designed: never reached RUNNING, stopped with 1066/10. |
| `New-VaultRoot.ps1` | First run applied the ACLs, but a verification bug (raw SID vs alias comparison) stopped it before the check. Fixed in `aa1722d`. The rerun re-applied the ACLs over the empty leftovers, and `vault-root-check` reported `trusted=yes`. |
| `Test-ServiceLab.ps1` with the vault root | RUNNING after 1.6 s as `NT SERVICESourceVault`; stopped in 0.9 s with exit 0; process exited. |
| Unelevated session (same user, admin token filtered) | Listing and file creation denied on both `C:ProgramDataSourceVault` and `vault`. |

The validator therefore passed under the service account itself, including reading the ancestor chain. No store has been created in the root yet.

## Project catalog at startup

The vault root holds only `index\`, a `RevisionStore` with a fixed id containing one encrypted `projects.json` (project ids and names), and `projects\<projectId>\`, one store per project. Project names never appear outside the encrypted index. `SourceVault.Storage.ProjectCatalog` implements this with no record-format change.

Normal start, before RUNNING, checks in order: identity, config, the trusted vault root (code 10), opening the key, the layout and index (code 11), then every project's authentication (code 12). By owner decision, any failure refuses the whole service. An empty vault root is a fresh vault with zero projects, and verification creates nothing. Cost: 2 TPM unwraps for the index plus 1 per project.

Lab start parameter `create-synthetic-project` verifies the vault, creates "Synthetic demo N" with three fixed fictional files, re-verifies, and stops. `scripts/lab/Test-VaultTamper.ps1` drills codes 11 and 12 and restores the vault byte-for-byte. Neither has been run by the owner yet.
