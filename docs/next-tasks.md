# Next implementation tasks

The record codec, revision store, Windows adapter, and SCM host foundation are implemented and covered by synthetic tests. A synthetic-only editor preview (WPF/WebView2 host, offline Monaco, memory-only revisions) is implemented with pinned, vendored packages; see [editor preview checks](editor-preview-checks.md). These are distinct from passing hardware/deployment gates. Do not introduce a software key provider to bypass those gates.

## Next worker wave

1. **Service-host lab integration (T01/T02 continuation).** Service-identity key provisioning (`provision-key`) and owner-run lab scripts (`scripts/lab`) now exist; run them on this TPM-equipped machine and record the results. Use the implemented SCM-compatible metadata host and existing-key adapter. Validate actual `NT SERVICE\SourceVault` identity, service profile/key provisioning, service/key/vault DACLs, restart/shutdown timing, and mutual pipe checks on a TPM-equipped test machine. Implement reviewed provisioning; the current console key spike is not itself an SCM service. Do not enable source endpoints until identity and trusted storage-root policy pass.
2. **Storage hardening and service integration (T04/T05).** Review `contracts/storage-v1.md` and the tested implementation. A read-only trusted-root validator (`SourceVault.Windows/TrustedStorageRoot.cs`) now exists; gate service readiness on it when the service first opens a store, and write the reviewed provisioning that applies `ExpectedRootSddl`; implement key rotation/recovery decisions; measure hardware unwrap throughput; run power-loss tests; execute skipped reparse cases in a suitable lab. Then integrate bounded authorized document operations with the service. Current test protectors remain confined to test assemblies. No production import until the TPM gate passes.
3. **Editor native validation (T06 continuation).** The synthetic preview shell exists. Run and record the interactive native checklist in `docs/editor-preview-checks.md`: export paths, devtools/accelerators, navigation/resource/message rejection, registry/policy WebView2 overrides, and WebView2 profile/cache/crash-dump inspection with unique synthetic markers. Confirm the stricter `PreviewPolicy` still loads every Monaco asset, worker and the codicon font. Exercise capture exclusion only in an explicit, separately reviewed mode and report it as best effort. (Policy extraction with hostile-input tests and the package signature check are done.)
4. **Editor against the service protocol (T06).** The renderer already talks to an in-memory protocol-v1 mock. Next, move the mock behind the native host over the real framing, still synthetic-only, so the host owns the capability and the renderer never sees it. Expose no decryption shortcut or generic IPC passthrough; keep document text out of the bridge until the protocol message set is reviewed. Real service connection waits on item 1.

## Builder continuation

T03 requires a supported Hyper-V lab host, verified Linux image/toolchain and dedicated identities. Implement and test the narrow VM-management helper and authenticated guest channel before transferring confidential content. The current synthetic CLI is not that broker. The supplied guest wrapper is untested. Establish the practical encrypted-storage profile first; defer RAM-only claims.

## Decisions before real source or public release

- Confirm operator/administrator threat model and intentional-export policy.
- Confirm device-only loss policy versus an explicitly designed owner recovery envelope.
- Approve supported hardware/OS/runtime and offline update strategy.
- Choose a real initial compiler and artifact policy. Readable JavaScript output does not conceal implementation.
- Choose an open-source license and signing/release ownership. No license is assumed automatically.
