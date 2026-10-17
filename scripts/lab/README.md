# Service-account lab scripts

These scripts install the **metadata-only** SourceVault service under its own virtual account, `NT SERVICE\SourceVault`, then have that account create and use its own TPM key. They do not create a vault folder, open a store, or expose any document endpoint. Use synthetic data only.

They change machine state: a Windows service, a folder under Program Files, and a persisted TPM-backed key in the service account's profile. Read them before running. The owner runs the elevated steps; development checks never do.

## Order

| Step | Script | Run as | What it does |
|---|---|---|---|
| 1 | `Publish-ServiceLab.ps1` | your normal user | Builds the service into `artifacts/service-publish`. |
| 2 | `Install-ServiceLab.ps1` | **administrator** | Copies a private .NET 10 runtime (from the SHA-512-verified workspace SDK) and the service to `C:\Program Files\SourceVault`. Writes the config (key `SourceVault.Lab.Service01` by default) and checks that ordinary users can't modify the files. Records SHA-256 hashes in `install-manifest.sha256`. Registers service `SourceVault` as `NT SERVICE\SourceVault` with manual start. Refuses to run if either the service or the folder already exists. |
| 3 | `Provision-ServiceKey.ps1` | **administrator** | Runs `sc start SourceVault provision-key` once. Running as the service account, the service creates a decrypt-only key (`PCP_KEY_USAGE_POLICY` encryption-only) and never overwrites an existing one. It then confirms both private exports are refused, opens the key through the same checks used at normal startup, runs a synthetic wrap/unwrap, and stops. The result is reported only as an exit code. |
| 4 | `Test-ServiceLab.ps1` | **administrator** | Starts the service normally. It must reach RUNNING only after opening its key under its own identity. The script checks the process account, stops the service, and confirms a clean exit with no leftover process. Rerun it after a reboot. |
| 5 | `New-VaultRoot.ps1` | **administrator** | Creates the fixed vault root `C:\ProgramData\SourceVault\vault`, using the exact protected ACL the installed service reports (`vault-root-sddl`). The `SourceVault` parent folder is protected too: SYSTEM and Administrators get full control, and the service gets read/traverse on that folder only. The script then runs the service's read-only `vault-root-check`. It refuses if `C:\ProgramData\SourceVault` already exists, and creates no store or files. |
| 6 | `New-SyntheticProject.ps1` | **administrator** | Runs `sc start SourceVault create-synthetic-project` once. As the service account, the service verifies the vault, creates project "Synthetic demo N" with three fixed fictional files encrypted under its TPM key, re-verifies, and stops. |
| 7 | `Test-VaultTamper.ps1` | **administrator** | Fail-closed drill, synthetic vaults only. A stray file in the vault root must refuse startup with 1066/11. One flipped bit in a project's live manifest must refuse with 1066/12. The original bytes are restored and checked by SHA-256, and the restored vault must reach RUNNING. |
| 8 | `New-OperatorsGroup.ps1` | **administrator** | Creates the local group *SourceVault Operators* if it is missing and adds a member (default: you). The service only lets this group use its pipe, and refuses to start without the group (1066/13). The member must **sign out and back in** before the membership takes effect. |
| 9 | `Test-PipeAccess.ps1` | **normal user** | With the service left running (`Test-ServiceLab.ps1 -LeaveRunning`), probes the pipe from PowerShell, which is not an installed client. If you are not an operator in this logon, the DACL refuses. If you are, the service closes without answering. Either is a pass. |
| 10 | `Install-LabClient.ps1` | **administrator** | Installs or replaces `C:\Program Files\SourceVault\client\SourceVault.LabClient.exe`, the exact path the service accepts. Checks that ordinary users can't modify it, and re-hashes the install. The client runs only on the private runtime next to it and ignores `DOTNET_ROOT` and global .NET installs. |
| 11 | `SourceVault.LabClient.exe check` / `list` | **normal user, operator** | `check` exercises the real pipe end to end: session, project and file creation or editing, decrypt and byte-compare, refusal of a stale edit, a service-created synthetic file, a forged capability refused, and EndSession. `list` shows project and file names and sizes. Neither ever prints document content. |
| — | `Update-ServiceLab.ps1` | **administrator** | Replaces the installed service binaries with a new `Publish-ServiceLab.ps1` build. Keeps the config, runtime, registration and key, and re-hashes the files. |
| — | `Uninstall-ServiceLab.ps1` | **administrator** | Stops and deletes the service and removes the install folder. `-RemoveVaultRoot` also removes `C:\ProgramData\SourceVault`, but only if the vault is empty. The TPM key stays in the service profile; nothing here deletes keys. |

**Vault-root gate.** At every normal start, before opening its key, the service validates the vault root with `TrustedStorageRoot`. It refuses to start if the root is missing or untrusted, stopping with Win32 1066 and service-specific code 10. To see the findings, run this elevated:

```powershell
& "C:\Program Files\SourceVault\runtime\dotnet.exe" "C:\Program Files\SourceVault\service\SourceVault.Service.dll" vault-root-check
```

From an **elevated** Windows PowerShell in this repository folder:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\lab\Install-ServiceLab.ps1
```

Run the other elevated scripts the same way.

## Provisioning outcomes

| Exit | Meaning |
|---|---|
| 0 | Created: key exists, decrypt-only, export refused, roundtrip passed |
| 1066 / 1 | Already exists; nothing changed |
| 1066 / 2 | Unavailable: wrong identity, config/provider missing, or creation failed |
| 1066 / 3 | Created but failed the startup policy checks (left in place) |
| 1066 / 4 | Created but exportable (left in place; do not use) |
| 1066 / 5 | Created and opened but the roundtrip failed (left in place) |
| 87 | Invalid start parameter |
| 1066 / 10 | Normal start refused: vault root missing or untrusted |
| 1066 / 11 | Normal start refused: unexpected entry in the vault root, or the project index failed authentication |
| 1066 / 12 | Normal start refused: a project failed authentication (whole service refuses, by owner decision) |
| 1066 / 13 | Normal start refused: local group *SourceVault Operators* is missing |
| 1066 / 14 | Normal start refused: the pipe could not be created (for example, the name was already taken) |

Win32 exit 1066 means "service-specific error"; the number after the slash is the service-specific code.

## Limits

The service binary is `dotnet.exe` running `SourceVault.Service.dll`, so Windows sees `dotnet.exe` as the executable; no executable-identity claim follows. Local administrators can still act as the service, read its profile, or replace the install. Anything later wrapped by this key is lost if the TPM is cleared or the service profile is deleted; no recovery envelope exists yet.
