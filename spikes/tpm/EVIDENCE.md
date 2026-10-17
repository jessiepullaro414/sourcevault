# T01 development evidence

Date: 2026-10-17. Restricted development environment; not the intended deployed service identity.

| Check | Observed result | Meaning |
|---|---|---|
| Read-only PowerShell `Add-Type` P/Invoke: `NCryptOpenStorageProvider("Microsoft Platform Crypto Provider")` | status `0x00000000` | Provider can be opened in this development context. |
| `NCryptGetProperty(provider, "Impl Type")` | status `0x00000000`, length `4`, value `1` | Provider declares hardware implementation. This is not an attestation or decrypt test. |
| TPM key creation/roundtrip | Not run | Intentionally avoids persisting machine/account cryptographic state in development checks. |
| .NET 10.0.402 build, warnings as errors | Passed: 0 warnings, 0 errors | BCL-only project compiles; offline restore with repository NuGet.Config. |
| Console `self-test` | Passed: 23 assertions, `tpmTouched=false` | Argument and metadata-policy safety only. |
| Console `probe` | Passed: exact provider, hardware implementation flag; current-user SID available | Read-only metadata query only. |
| Service account, profile loading, ACLs, reboot, cross-machine and private-export tests | Not run | Lab gates remain open. |

No services were installed or started. No ACLs/policies were changed. No TPM was cleared. No key was created by the recorded checks. The coordinator's earlier `Get-Tpm` "absent" result came from an unelevated run and was wrong. `tpmtool getdeviceinformation` (2026-10-17) reports an Intel firmware TPM 2.0 that is present, initialized and ready for storage. The provider flag and device report still do not prove that a key operation works. The physical/device operation gate is blocked pending a suitable lab.

Build commands used a verified portable SDK at `work/toolchain/dotnet/dotnet.exe` relative to the chat workspace. Process-local `DOTNET_CLI_HOME` was set to repository `artifacts/cli-home`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0`, and `APPDATA` redirected into `spikes/tpm/.local-appdata` because the sandbox cannot read the actual user NuGet configuration. No permanent environment variables were changed. Restore used `--configfile <absolute-repository-path>/NuGet.Config`; build used `--no-restore`; runs used `--no-build`.
