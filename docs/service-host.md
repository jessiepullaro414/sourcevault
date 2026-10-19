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

1. Use a disposable Windows 11 TPM2 lab PC/VM configuration under administrator control; synthetic fixtures only. Recovery policy: none, by owner decision (2026-10-18), so anything in the vault is lost with the TPM. Never clear a real TPM as a test step.
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
| `Test-ServiceLab.ps1` with the vault root | RUNNING after 1.6 s as `NT SERVICE\SourceVault`; stopped in 0.9 s with exit 0; process exited. |
| Unelevated session (same user, admin token filtered) | Listing and file creation denied on both `C:ProgramDataSourceVault` and `vault`. |

The validator therefore passed under the service account itself, including reading the ancestor chain. No store has been created in the root yet.

## Project catalog at startup

The vault root holds only `index\`, a `RevisionStore` with a fixed id containing one encrypted `projects.json` (project ids and names), and `projects\<projectId>\`, one store per project. Project names never appear outside the encrypted index. `SourceVault.Storage.ProjectCatalog` implements this with no record-format change. Deleted projects are tombstoned in the index until their directories are gone; after verification, startup retries any unfinished deletion (best effort) — see `contracts/storage-v1.md`.

Normal start, before RUNNING, checks in order: identity, config, the trusted vault root (code 10), opening the key, the layout and index (code 11), then every project's authentication (code 12). By owner decision, any failure refuses the whole service. An empty vault root is a fresh vault with zero projects, and verification creates nothing. Cost: 2 TPM unwraps for the index plus 1 per project.

Lab start parameter `create-synthetic-project` verifies the vault, creates "Synthetic demo N" with three fixed fictional files, re-verifies, and stops. `scripts/lab/Test-VaultTamper.ps1` drills codes 11 and 12 and restores the vault byte-for-byte.

Owner-run lab, 2026-10-17:

| Step | Result |
|---|---|
| `Update-ServiceLab.ps1`, then `Test-ServiceLab.ps1` on the empty vault | RUNNING in 2.0 s: a fresh vault is accepted with zero projects. |
| `New-SyntheticProject.ps1` | Created a three-file synthetic project under `NT SERVICE\SourceVault` with its TPM key, and re-verified the vault (1 project folder). |
| `Test-ServiceLab.ps1` with the project | RUNNING in 1.3 s with the index and project authenticated at startup; clean stop, exit 0. |
| `Test-VaultTamper.ps1` | A stray file in the vault root was refused with 1066/11; one flipped bit in the project manifest was refused with 1066/12; the restored vault reached RUNNING. The vault was restored byte-for-byte (SHA-256 verified). |

## Pipe endpoints (protocol v2)

Once startup verification passes, the service listens on `\.\pipe\SourceVault.v1` and serves [protocol v2](../contracts/protocol-v2.md). RUNNING now means both "verified" and "listening". Startup additionally refuses with 1066/13 if the *SourceVault Operators* group is missing, and with 1066/14 if the pipe cannot be created.

The service accepts `SERVICE_CONTROL_SESSIONCHANGE` (accepted controls `0x85` while running). Lock, logoff and disconnect close that Windows session's vault connections.

Two production details differ from the IPC spike, both found while building on this machine:

- **Users cannot open service processes** (verified: an unelevated user is denied even limited query on LocalSystem, LocalService and NetworkService processes). Clients therefore identify the server by the **pipe's owner SID**, not by its process token.
- **Service accounts get no access to user processes** (verified: user process DACLs grant only the user, SYSTEM, and limited query to the logon SID). Installed clients therefore grant the service `PROCESS_QUERY_LIMITED_INFORMATION` on themselves so that their executable path can be read.

Tested in process (`tests/SourceVault.Endpoints.Tests`, 12 cases) with a real local pipe, synthetic vault and test key.

Owner-run lab, 2026-10-17:

| Step | Result |
|---|---|
| `New-OperatorsGroup.ps1` | Created *SourceVault Operators* and added the owner. The first run failed parameter validation (description over 48 characters) before changing anything; fixed in `3895916`. |
| `Update-ServiceLab.ps1`, then `Test-ServiceLab.ps1 -LeaveRunning` | RUNNING in 2.6 s as `NT SERVICE\SourceVault`, and `\.pipeSourceVault.v1` was listening. |
| `Test-PipeAccess.ps1` from a normal window, in a sign-in that predates the group | The pipe DACL refused the connection. |

Still to run: `Test-PipeAccess.ps1` after signing out and in, which should show the executable check closing the connection without an answer; and a real installed client, the lab client (next step).

## Lab client

`src/SourceVault.LabClient` builds `SourceVault.LabClient.exe`, an apphost that runs only on the private runtime at `..\runtime` (`AppHostRelativeDotNet` with `AppHostDotNetSearch=AppRelative`). `scripts/lab/Install-LabClient.ps1` installs it at `<install root>\client\`, the exact path the service accepts.

At startup the client grants the service limited query on its own process. It then connects, refusing any pipe not owned by `NT SERVICE\SourceVault`. It has two commands, and neither ever prints document content:

- `list`: project and file names and sizes.
- `check`: an end-to-end exercise. It creates or edits a fictional file, reopens it and compares bytes, confirms a stale edit is refused, decrypts a file from the service-created synthetic project, confirms a forged capability is refused, and ends the session.

Checked without admin on this machine, using a copy of the installed layout:

- it runs on the app-relative runtime, and `DOTNET_ROOT` is ignored;
- it refuses to start with no fallback when `..\runtime` is missing;
- against the live installed service it was **denied by the pipe DACL**, because this sign-in predates the operators group.

`check` and `list` are also tested in process (`Endpoints.Tests`, 13 cases).

Owner-run lab, 2026-10-17, after signing out and in as an operator:

| Step | Result |
|---|---|
| `Test-ServiceLab.ps1 -LeaveRunning` (admin) | RUNNING in 1.5 s as `NT SERVICE\SourceVault`; the pipe was listening. |
| `SourceVault.LabClient.exe check` (installed, normal window) | All 9 steps passed. It listed 1 project, created "Lab client check" (210 ms) and `check.txt` (107 bytes, 41 ms), and reopened and decrypted it with matching bytes (83 ms). A stale edit was refused with `RevisionConflict`. It decrypted `README.md` (101 bytes) from the service-created "Synthetic demo 1". EndSession closed the connection, and a forged capability was refused and closed. |
| `Test-PipeAccess.ps1` (operator, normal window) | PowerShell connected but the service closed without answering: the executable check. |
| A byte-identical copy of the client and runtime under `artifacts` (operator, unelevated) | Refused: the service closed the connection. The installed client then listed both projects. The check keys on the admin-owned install path, not on file contents. |

The owner sign-in now holds the operators group. Still open: a reboot test, and the editor (design step 3).

## Client checks: PID reuse and hash pins (2026-10-18)

Design: [hardening-design.md](hardening-design.md), sections 1 and 2.

- **PID reuse.** The server records when it accepted a connection. The client process must still be running and must have been created no later than that, checked while the server holds a handle to it, so a reused PID is refused. Its creation time is pinned with the PID for every later frame.
- **Hash pins.** On each connection's first frame, the service re-reads `install-manifest.sha256` from the install root. It refuses the client unless its folder (`client\` or `editor\`, recursively, assets included) holds exactly the listed files with matching SHA-256 and no links. A missing or malformed manifest refuses every client; the service itself still starts.
- **Limits.** An administrator can rewrite the manifest, and only files on disk are checked, not the running image. The shared `runtime\` is checked by `-Status`, not per connection.
- **Verified:** in-process tests only (the endpoint tests). Lab check: [lab-checklist.md](lab-checklist.md).

## Package import key and registered senders (2026-10-19)

Design: [package-import-design.md](package-import-design.md).

- **Import key:** `sc start SourceVault provision-import-key` creates a second decrypt-only, non-exportable TPM key, `<vault key name>.Import`, the same way `provision-key` creates the vault key. Setup runs it on every install and update; exit code 1066/1 means it already exists.
- **At startup:** the service opens the import key if it exists. If it can't, package import is `Unavailable`, and nothing else changes: the vault still starts.
- **Senders:** `C:\ProgramData\SourceVault\senders\<fingerprint>.json`. Administrators and SYSTEM have full control, and the service can only read it. The service reads it for each import; deleting a file unregisters that computer.
- **Tool command:** `SourceVault.Service.dll sender-check <file>` (read-only, used by `Setup-SourceVault.ps1 -RegisterSender`) validates a sender file and prints its computer name and fingerprint.
- **Verified:** in process only, with software stand-ins for both TPM keys. Lab steps are in [lab-checklist.md](lab-checklist.md).

## Build service role (2026-10-18)

The same service program also runs as a second service, **SourceVaultBuild**, under its own virtual account `NT SERVICE\SourceVaultBuild`, started with the argument `build-service`. Setup registers it only with `-WithBuilds`.

- **What it never touches:** the vault, the TPM key and the operator pipe. Its account has no access to the vault folder or the key.
- **What it serves:** only `\.\pipe\SourceVault.Build.v1`, for `NT SERVICE\SourceVault`. It reads `%ProgramData%\SourceVault\config\build.json` on every request, so edits apply without a restart.
- **Docker:** it talks to the configured Docker engines directly (Docker Engine API v1.41 over the engine's named pipe, or loopback TLS with a client certificate) and writes artifacts under `artifactRoot`.
- **Startup refusal codes:** 15 means `NT SERVICE\SourceVault` doesn't exist; 16 means the service isn't running as `NT SERVICE\SourceVaultBuild`; 14 means its pipe couldn't be created.
- **Not verified on a real machine yet:** service registration, Docker group membership of the virtual account, and the Docker Engine API against real engines (Windows containers in particular, where copying into a running Hyper-V-isolated container may not be supported; process isolation is recommended).
