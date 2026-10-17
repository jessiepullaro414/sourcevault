# Next implementation tasks

The initial feasibility source milestone is distinct from passing the hardware and deployment gates. Follow this order; do not introduce a software key provider in order to bypass the hardware gate.

## Next worker wave

1. **Service-host lab integration (T01/T02 continuation).** Add a BCL-only or explicitly pinned supported Windows service host for the TPM and pipe logic. The host must implement SCM lifecycle correctly, provision only under reviewed administrative configuration, and never run interactive user-provided commands. Validate actual `NT SERVICE\SourceVault` identity, profile/key access, service/key/vault DACLs, restart, and mutual pipe checks on an appropriate TPM-equipped test machine. Source ownership should be assigned before dispatch. This blocks production storage integration.
2. **Encrypted record format review (T04 preparation).** Specify authenticated file/manifest records, key epochs, nonce uniqueness, crash-safe commit, revision conflicts, and replay limits. Review the format before building persistence. A test-only protector may exercise the store, but must never be reachable by production configuration. No production import until the TPM gate passes.
3. **Editor shell against mock protocol (T06 preparation).** Build WPF/WebView2 with pinned offline packages and Monaco. Start with synthetic documents and explicit mock mode; expose no decryption shortcut. Keep document text out of profile persistence. Require strict web-message schema and origin checks, navigation lockdown, debugging override tests, and truthful capture status. Editor packages are not fetched or bundled in this milestone.

## Builder continuation

T03 requires a supported Hyper-V lab host, verified Linux image/toolchain and dedicated identities. Implement and test the narrow VM-management helper and authenticated guest channel before transferring confidential content. The current synthetic CLI is not that broker. The supplied guest wrapper is untested. Establish the practical encrypted-storage profile first; defer RAM-only claims.

## Decisions before real source or public release

- Confirm operator/administrator threat model and intentional-export policy.
- Confirm device-only loss policy versus an explicitly designed owner recovery envelope.
- Approve supported hardware/OS/runtime and offline update strategy.
- Choose a real initial compiler and artifact policy. Readable JavaScript output does not conceal implementation.
- Choose an open-source license and signing/release ownership. No license is assumed automatically.
