# Feasibility milestone status

Date: October 17, 2026. Encrypted storage and service foundations are implemented in a local Git project. This is not yet a functioning encrypted editor or deployed vault service.

| Component | Delivered | Observed verification |
|---|---|---|
| Shared contracts | Bounded frames, strict UTF-8/JSON, duplicate/unknown envelope rejection, canonical IDs, deadlines | 26 tests passed |
| TPM spike (T01) | Safe provider probe, explicit lab-only key commands, strict hardware-provider/nonexportable policy checks | 23 argument/policy assertions passed; no key created |
| IPC spike (T02) | Exact-rights DACL, first-instance/remote restrictions, peer checks, capabilities, monotonic terminal expiry | 11 synthetic tests passed, including real same-process local pipes |
| Builder spike (T03) | Bounded USTAR input/artifact validator, synthetic ADD compiler, sanitized inventory, experimental guest wrapper | 25 tests passed; guest execution not tested |
| Record encryption (T04) | AES-256-GCM, fresh per-record wrapped data keys, authenticated context, bounded binary format, cleanup | 65 tests passed |
| Revision storage (T04) | Encrypted manifests/documents, immutable blobs, optimistic conflicts, exclusive locks, atomic HEAD publication | 38 tests passed; four symlink tests skipped |
| Windows service foundation | Open-only TPM adapter, exact service identity guard, SCM lifecycle, fixed strict configuration | 44 assertions passed; SCM/TPM untouched |
| Storage integration | Create/edit/reopen, stale updates, competing writers, synthetic disk scan, unavailable protector | Six checks passed |

Coordinator ran `scripts/Verify.ps1` against all twelve projects in Release using Microsoft .NET SDK 10.0.402: **238 checks passed, zero build warnings/errors**. Four dangling-symlink tests explicitly skipped because the host could not create the test links. Projects use BCL only, with NuGet feeds cleared. The SDK was downloaded into the chat workspace's `work/toolchain/`, verified with SHA-512 against Microsoft's release metadata, and was not installed system-wide. See `toolchain.json` for the recorded package metadata.

Direct DLL execution outside the restricted sandbox was required for IPC verification. An independent native diagnostic established that this environment requires `FILE_READ_ATTRIBUTES` in the pipe ACE; final mask `0x00100083` still excludes `FILE_CREATE_PIPE_INSTANCE`. Restricted-token failures were not treated as successful isolation tests. These results do not establish deployed cross-account access control.

Independent worker reviews checked protocol parsing/framing and native IPC. The latter found a wall-clock expiry issue; it was corrected using monotonic time, terminal invalidation, and simulated-time tests before final verification. These reviews are development checks, not an external security audit.

Read-only provider metadata query succeeded, but `Get-Tpm` reported no present/ready/enabled TPM. Hyper-V and Docker tooling were unavailable. No WSL instance was started. Windows services, persistent TPM keys, machine ACLs, VM configuration, or security policies were not provisioned. Initial SDK bootstrap emitted a development-certificate installation message before certificate generation was disabled; no certificate-trust operation was requested. The verifier disables certificate generation and redirects tooling state into the workspace.

The Linux wrapper passed `bash -n` syntax validation using Git Bash outside the sandbox; Linux runtime behavior remains untested. Test fixture archives deliberately contain synthetic plaintext and are development evidence, not proof of protected source storage.

New storage integration fixtures use ephemeral software RSA confined to test assemblies. The production library has no software protector and the production service cannot select one. Synthetic document content and names were absent from the tested store files; this limited canary scan is not proof of universal non-persistence. The revision suite also exercised a real child-process exclusive lock and four commit interruption stages. Exception fault injection does not establish power-loss durability.

Workers reviewed the binary record layout, codec and revision integrity paths. Review prompted hardening of dangling-link attribute checks. The library still requires an externally provisioned trusted service-owned root/ancestors; it does not establish ownership or prevent a hostile filesystem writer racing operations. Before source endpoints can be enabled, deployment must enforce those ACL prerequisites. List/Open currently authenticate every committed document, so actual TPM throughput is another integration gate.

The service host implements SCM entry points and cancellation, but its ready state means only an existing key opened under the required identity. It exposes no document, build or pipe API. Its synchronous key open has no forced cancellation and requires startup/shutdown timing validation on hardware.

## Gates still open

- Actual service host/virtual identity, key/vault/service ACLs, hardware key operations, restart/reboot, export denial, and cross-machine binding.
- Real-hardware validation of record/store integration, power-loss durability, throughput, rotation and chosen recovery design.
- Distinct editor identity and mutual pipe behavior, genuine separate-account/remote-client denial, lock/revocation wiring, signer/path policy and process-lifetime races.
- WPF/WebView2/Monaco editor, offline asset bundle and persistence/capture/debugging tests.
- Approved real compiler, authenticated guest channel, VM-management delegation, guest lifecycle, artifact publication, and persistence investigation.
- Offline installer/update signing, recovery drills, license selection and independent security review.

See [next assignments](next-tasks.md) before dispatching further implementation. Only synthetic inputs should be used until the appropriate gates pass.
