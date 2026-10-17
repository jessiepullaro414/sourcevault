# Next implementation tasks

The record codec, revision store, Windows adapter, and SCM host foundation are implemented and covered by synthetic tests. These are distinct from passing hardware/deployment gates. Do not introduce a software key provider to bypass those gates.

## Next worker wave

1. **Service-host lab integration (T01/T02 continuation).** Use the implemented SCM-compatible metadata host and existing-key adapter. Validate actual `NT SERVICE\SourceVault` identity, service profile/key provisioning, service/key/vault DACLs, restart/shutdown timing, and mutual pipe checks on a TPM-equipped test machine. Implement reviewed provisioning; the current console key spike is not itself an SCM service. Do not enable source endpoints until identity and trusted storage-root policy pass.
2. **Storage hardening and service integration (T04/T05).** Review `contracts/storage-v1.md` and the tested implementation. Add trusted-root ownership/ACL validation in the Windows deployment boundary; implement key rotation/recovery decisions; measure hardware unwrap throughput; run power-loss tests; execute skipped reparse cases in a suitable lab. Then integrate bounded authorized document operations with the service. Current test protectors remain confined to test assemblies. No production import until the TPM gate passes.
3. **Editor shell against mock protocol (T06 preparation).** Build WPF/WebView2 with pinned offline packages and Monaco. Start with synthetic documents and explicit mock mode; expose no decryption shortcut. Keep document text out of profile persistence. Require strict web-message schema and origin checks, navigation lockdown, debugging override tests, and truthful capture status. Editor packages are not fetched or bundled in this milestone.

## Builder continuation

T03 requires a supported Hyper-V lab host, verified Linux image/toolchain and dedicated identities. Implement and test the narrow VM-management helper and authenticated guest channel before transferring confidential content. The current synthetic CLI is not that broker. The supplied guest wrapper is untested. Establish the practical encrypted-storage profile first; defer RAM-only claims.

## Decisions before real source or public release

- Confirm operator/administrator threat model and intentional-export policy.
- Confirm device-only loss policy versus an explicitly designed owner recovery envelope.
- Approve supported hardware/OS/runtime and offline update strategy.
- Choose a real initial compiler and artifact policy. Readable JavaScript output does not conceal implementation.
- Choose an open-source license and signing/release ownership. No license is assumed automatically.
