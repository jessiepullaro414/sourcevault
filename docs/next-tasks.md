# Next implementation tasks

Updated 2026-10-18. The vault service, encrypted store, editor vault mode, importer, file operations, history, merge and container builds are implemented, and `scripts/Verify.ps1` passes (see [status](status.md)). Owner-run lab checks have passed for the installed service, key provisioning, vault root, catalog tamper checks, pipe DACL and lab client. Everything added since then is tested in process only. Do not introduce a software key provider to bypass the hardware gates, and use synthetic inputs only.

## Owner lab steps (next)

**All owner-run tests are tracked in [lab-checklist.md](lab-checklist.md)**, grouped by what they disturb: no sign-out or reboot, needs a sign-out or reboot, or stops the service for a while. Start with the editor and builds sections; they need neither a sign-out nor a reboot. Record results in `status.md`, and turn failures into tasks here.

## Development work

Done on 2026-10-18 (in-process tests only; design and decisions in [hardening-design.md](hardening-design.md)):
- **Pipe client PID reuse:** the client must still be running and be older than the connection.
- **Client hash pins:** every file in the client's install folder must match `install-manifest.sha256`, re-read per connection.
- **Durable HEAD:** publication is flushed, and a kill-at-random-points test covers process crashes.
- **Monaco assets:** a test checks that every shipped asset passes `PreviewPolicy`.
- **Build output:** source maps, debug symbols, intermediates and inline source maps are dropped and listed in the log; `keepFileTypes` overrides.

Still open:

1. **Editor native validation (T06).** The interactive items are in the lab checklist. Still for development: anything those runs turn up. Pagefile and hibernation persistence remain out of scope and documented.
2. **Client identity.** An Authenticode signer check waits for the signing decision (below). Genuine separate-account and remote-client denial, and lock/revocation across several editors, are lab items.
3. **Storage.** The skipped reparse-point cases need an elevated run. Power loss is a lab item. TPM throughput beyond 200 files is unmeasured. A crash between creating a project's folder and recording it in the index leaves an orphan folder, which makes startup refuse (code 11) until an administrator removes it. That is by design ("for review"); decide whether to keep it.
4. **Builds.** Artifacts go through a `.partial` folder and are published with no approval step; an owner-approval release step is not built. A real initial compiler is still to be chosen.
5. **Release.** Offline installer and update signing, a supported hardware/OS matrix, and an independent security review.

## Retired or out of scope (owner, 2026-10-18)

- **Hyper-V builder (T03): retired.** Builds use administrator-configured Docker containers through `NT SERVICE\SourceVaultBuild` (decisions D3/D4). `spikes/builder` stays as historical reference; its guest wrapper is untested. Make no RAM-only or isolation claims for builds.
- **Key rotation: out of scope.** The key never leaves the TPM, and anyone who could misuse it is a local administrator, whom the design already trusts.

## Decisions before real source or public release

- Confirm operator/administrator threat model and intentional-export policy.
- ~~Confirm device-only loss policy versus an owner recovery envelope.~~ Decided 2026-10-18: device-only, no recovery. Do not add a recovery envelope, escrow or key export.
- Approve supported hardware/OS/runtime and offline update strategy.
- Choose a real initial compiler and artifact policy. Readable JavaScript output does not conceal implementation. (Dropping source maps and debug symbols: decided 2026-10-18.)
- Signing/release ownership. This also unblocks an Authenticode client check.
- ~~License.~~ Decided 2026-10-18: MIT, chosen by the owner in the GitHub repository (`LICENSE`).
- ~~Per-project permissions.~~ Decided 2026-10-18: none. Every member of *SourceVault Operators* can open every project; access is limited by that group alone.
- ~~Key rotation.~~ Decided 2026-10-18: out of scope.
- ~~Hyper-V builder (T03).~~ Decided 2026-10-18: retired.
