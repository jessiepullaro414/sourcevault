# T01: TPM wrapping-key feasibility

This is a console spike, not a Windows service or production vault. It wraps exactly 32-byte random data keys with persisted 2048-bit RSA and OAEP-SHA256 through **Microsoft Platform Crypto Provider**. No source document format is defined here. No software fallback exists. Only synthetic in-memory data is used.

The default command opens the named provider, reads `Impl Type`, reports whether it declares hardware implementation, and prints the current identity SID. It never creates a key. A registered hardware provider is not evidence of physical TPM provenance, key persistence, successful decrypt, or account isolation. A virtual TPM may expose the same interface.

## Commands

From the repository root, with an installed .NET 10 SDK:

```powershell
dotnet build spikes/tpm/SourceVault.TpmSpike.csproj
dotnet run --no-build --project spikes/tpm -- self-test
dotnet run --no-build --project spikes/tpm -- probe
```

`self-test` does not access the TPM or create keys. It exercises argument safety and rejection of software-provider, exportable, ephemeral, wrong-algorithm, wrong-size, wrong-usage and machine-scope policy metadata. These tests do not establish actual OS enforcement.

Only on a disposable, authorized lab machine, under the identity being evaluated:

```powershell
dotnet run --no-build --project spikes/tpm -- lab-create --key SourceVault.Lab.T01 --ack-device-only-loss
dotnet run --no-build --project spikes/tpm -- lab-open --key SourceVault.Lab.T01
dotnet run --no-build --project spikes/tpm -- lab-roundtrip --key SourceVault.Lab.T01
dotnet run --no-build --project spikes/tpm -- probe --key SourceVault.Lab.T01
```

`lab-create` persists a key and runs a synthetic roundtrip. It refuses an existing key name and does not delete an existing key on failure. Creation can succeed before a later policy/roundtrip check fails; record the key name for lab cleanup. The acknowledgment is required because there is no recovery recipient. `lab-open` only opens and validates metadata. `lab-roundtrip` opens the existing key and performs a fresh synthetic encrypt/decrypt. No keys, ciphertexts, or plaintexts are printed. Exit codes: 0 success; 2 invalid arguments; 3 unavailable or policy failure.

Names are confined to the `SourceVault.Lab.` namespace. There is intentionally no export, delete, TPM clear, policy change, ACL mutation or service installation command. Cleanup of persisted lab keys belongs to the trusted lab administrator's separately reviewed provisioning procedure. This tool does not produce a recovery backup.

## Fail-closed checks

Before creation/open, require the exact provider and its hardware flag, and reject the software implementation flag. After opening, require exact provider, RSA-2048, `CngExportPolicies.None`, decryption-only usage, persisted non-ephemeral state, and current-identity rather than machine scope. Unknown or inaccessible properties fail the operation. Unsupported OAEP-SHA256 fails without trying a weaker scheme. Invalid wrapped lengths are rejected before decrypt; wrong plaintext lengths are zeroed and rejected. Temporary plaintext arrays used in the demo are zeroed after use; managed memory and library internals are not a comprehensive memory-erasure guarantee.

The provider/OS is trusted. These local metadata checks do not perform remote attestation, restrict which executable can invoke the key, or protect against administrators, debuggers, compromised OS, or code running as the same service identity. The current-identity container scope does not establish the final vault directory ACL policy.

## Actual virtual-service-account lab gate

Do **not** register this console executable directly with `sc.exe`: it does not implement the Service Control Manager entrypoint. The next integration must add a trusted, reviewable service host that calls the same operations and records only bounded status codes. No installation/start script is supplied because invoking an ordinary console as a service would provide misleading evidence.

1. Prepare a disposable supported Windows machine with a TPM and the .NET runtime. Record OS build, physical/vTPM provenance, Secure Boot state and TPM readiness through trusted administrative tools. Never clear a TPM for this test.
2. Add an SCM-compatible lab host under `NT SERVICE\SourceVault`. Keep executable/configuration paths administrator-owned. Configure its filesystem access and service DACL through the reviewed installer. Do not grant interactive operators start-time arguments or arbitrary commands.
3. Under that exact service identity, run the metadata probe, then create a unique synthetic lab key and roundtrip it. Current-user CNG storage can depend on the service profile being loaded; validate this explicitly. Failure blocks the gate. Do not silently switch to machine scope, LocalSystem or software storage to make the test pass. A change to machine scope requires an explicit key-security-descriptor design and review.
4. Stop/restart the service and roundtrip the original key. Reboot the machine and repeat. Record actual identity SID, key name, provider/policy metadata, and success/failure codes only.
5. As a standard operator and a separate unprivileged account, attempt to open the same key name. Require failure and verify no implicit key creation. Negative results must be interpreted in the correct namespace: an absent current-user key alone is not proof that copied blobs cannot be decrypted.
6. Inspect the provider-managed persisted key blob location and effective ACLs using trusted tools; confirm the operator cannot read, replace or delete the blob. Separately verify vault directory ACLs. Do not assume the provider's default ACL meets the deployment requirement. Record administrator access as trusted scope.
7. With synthetic key blobs only, and an administrator-reviewed lab transfer procedure preserving required metadata, attempt to use a copied persisted blob on a second machine. Require decrypt failure. A missing container/identity is an inconclusive test of TPM binding; validate it reaches an actual TPM-bound private operation. Do not copy a private key out through an export path or clear either TPM.
8. In the dedicated service-host test harness, attempt private-key export and require provider rejection, inject damaged ciphertext of valid length and require decrypt failure, and verify error/log paths contain no secret material. Repeat absence/disabled-provider tests in a disposable VM snapshot. This spike exposes no production export command.

Each row needs observed evidence before T01 passes. Service deployment, key ACLs, reboot persistence, export denial, tampering and cross-machine decrypt have not been validated by the non-mutating probe.

## References

- [Microsoft CNG key storage providers](https://learn.microsoft.com/en-us/windows/win32/seccertenroll/cng-key-storage-providers)
- [CNG property identifiers and implementation flags](https://learn.microsoft.com/en-us/windows/win32/seccng/key-storage-property-identifiers)
- [CngKey.Open](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cngkey.open?view=net-10.0)
- [Key storage and retrieval](https://learn.microsoft.com/en-us/windows/win32/seccng/key-storage-and-retrieval)

See `EVIDENCE.md` for the actual development checks.
