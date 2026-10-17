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
| — | `Uninstall-ServiceLab.ps1` | **administrator** | Stops and deletes the service and removes the install folder. The TPM key stays in the service profile; nothing here deletes keys. |

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

Win32 exit 1066 means "service-specific error"; the number after the slash is the service-specific code.

## Limits

The service binary is `dotnet.exe` running `SourceVault.Service.dll`, so Windows sees `dotnet.exe` as the executable; no executable-identity claim follows. Local administrators can still act as the service, read its profile, or replace the install. Anything later wrapped by this key is lost if the TPM is cleared or the service profile is deleted; no recovery envelope exists yet.
