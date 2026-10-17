# T01 development evidence

Date: 2026-10-17. Restricted development environment; not the intended deployed service identity.

| Check | Observed result | Meaning |
|---|---|---|
| Read-only PowerShell `Add-Type` P/Invoke: `NCryptOpenStorageProvider("Microsoft Platform Crypto Provider")` | status `0x00000000` | Provider can be opened in this development context. |
| `NCryptGetProperty(provider, "Impl Type")` | status `0x00000000`, length `4`, value `1` | Provider declares hardware implementation. This is not an attestation or decrypt test. |
| TPM key creation (`lab-create --key SourceVault.Lab.T01`, owner-approved, interactive user account) | Key **created**, then refused by the spike's policy check (exit 3). PCP reports usage `Decryption, Signing`, with `PCP_KEY_USAGE_POLICY = 0x10003` (low bits `0x3` = generic sign+encrypt) | The spike requests decrypt-only `KeyUsage`, but PCP creates a generic key unless creation sets `PCP_KEY_USAGE_POLICY` to encryption-only. The fail-closed check worked. The same check in `src/SourceVault.Windows/TpmKeyProtector.cs` would also reject this key. |
| Read-only diagnostic on that key (scratch tool, not committed) | RSA-2048, `ExportPolicy=None`, persisted, user scope. `GenericPrivateBlob` and `Pkcs8PrivateBlob` exports refused. 20/20 synthetic OAEP-SHA256 wrap/unwrap roundtrips passed; average wrap 0.1 ms, average TPM unwrap 47 ms | Hardware key operations and export denial work under the interactive user. Unwrap cost is about 47 ms per record, so authenticating every document on List/Open will not scale to large projects without design changes. |
| .NET 10.0.402 build, warnings as errors | Passed: 0 warnings, 0 errors | BCL-only project compiles; offline restore with repository NuGet.Config. |
| Console `self-test` | Passed: 23 assertions, `tpmTouched=false` | Argument and metadata-policy safety only. |
| Console `probe` | Passed: exact provider, hardware implementation flag; current-user SID available | Read-only metadata query only. |
| Service account, profile loading, ACLs, reboot, cross-machine and private-export tests | Not run | Lab gates remain open. |

No services were installed or started. No ACLs/policies were changed. No TPM was cleared. One persisted lab key, `SourceVault.Lab.T01`, now exists in the interactive user's PCP key store. The owner created it with explicit approval and acknowledged that it cannot be recovered. It has not been deleted; the tool has no delete command. The coordinator's earlier `Get-Tpm` "absent" result came from an unelevated run and was wrong. `tpmtool getdeviceinformation` (2026-10-17) reports an Intel firmware TPM 2.0 that is present, initialized and ready for storage. Key operations now work under the interactive user (above). The service-identity, reboot, cross-machine and decrypt-only-creation gates remain open.

Build commands used a verified portable SDK at `work/toolchain/dotnet/dotnet.exe` relative to the chat workspace. Process-local `DOTNET_CLI_HOME` was set to repository `artifacts/cli-home`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0`, and `APPDATA` redirected into `spikes/tpm/.local-appdata` because the sandbox cannot read the actual user NuGet configuration. No permanent environment variables were changed. Restore used `--configfile <absolute-repository-path>/NuGet.Config`; build used `--no-restore`; runs used `--no-build`.
