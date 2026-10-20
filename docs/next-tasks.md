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

- **Encrypted import from another computer (2026-10-19):** SourceVault Pack, a separate TPM import key, registered senders and `SourceVault.Import.exe --package` ([package-import-design.md](package-import-design.md)). Its lab run is in the checklist.

- **Vault on another drive (2026-10-19):** `Setup-SourceVault.ps1 -VaultRoot` ([vault-location-design.md](vault-location-design.md)).
- **Editor network lockdown (2026-10-19):** Monaco only, with no route to the network ([editor-host.md](editor-host.md)).

- **Offline deployment drive (2026-10-19):** `Build-DeploymentKit.ps1` and `Deploy-SourceVault.ps1`, with pinned sources and images fetched without Docker ([deployment-kit-design.md](deployment-kit-design.md)). Its lab run is in the checklist.

- **Editor features (2026-10-19):** Go to file, recently opened files, Search all files (protocol v2 `SearchProject`), outlines for C#, C and C++, header/source switching, editor settings, Save all, Compare with saved, format, split editor, status-bar details, the shortcut list and focus mode, plus a warning before closing with unsaved edits ([editor-host.md](editor-host.md)). Reviewed in the browser against the fake host; the real-keyboard and installed-editor checks are in the lab checklist.

Still open:

1. **Editor native validation (T06).** The interactive items are in the lab checklist. Still for development: anything those runs turn up. Pagefile and hibernation persistence remain out of scope and documented.
2. **Client identity.** An Authenticode signer check waits for the signing decision (below). Genuine separate-account and remote-client denial, and lock/revocation across several editors, are lab items.
3. **Storage.** The skipped reparse-point cases need an elevated run. Power loss is a lab item. TPM throughput beyond 200 files is unmeasured. A crash between creating a project's folder and recording it in the index leaves an orphan folder, which makes startup refuse (code 11) until an administrator removes it. That is by design ("for review"); decide whether to keep it.
4. **Builds.** Artifacts go through a `.partial` folder and are published with no approval step; an owner-approval release step is not built. The compiler is chosen (below). `installer/New-BuildContainer.ps1` (Linux) and `installer/New-WindowsBuildContainer.ps1` (Windows) create the containers; both still need their lab runs. Whether the build service can copy into a running Windows container depends on its isolation mode: the Windows script tests exactly that. Offline builds cannot restore NuGet packages yet: a reviewed local package source is the next step if projects need packages. **C++ (owner, 2026-10-19: the code is also C++):** the editor now handles C and C++ files, but both build containers carry only the .NET SDK. Building C++ needs a pinned toolchain in each container (for example the MSVC Build Tools on Windows Server Core, and GCC or Clang with CMake on Linux), chosen by the owner. Design: [cpp-build-design.md](cpp-build-design.md). Owner answers (2026-10-19): GCC on Linux; Visual Studio 2026 Build Tools with MSBuild, x86 and x64, ATL and MFC; licence covered; C++ added to the existing two containers; static and dynamic runtimes. Still open: the older toolsets, the Linux build system, and **the vault's text-only limits** (UTF-16 `.rc` files, binary icons and vendored libraries would be skipped on import; section 5).
5. **Release.** Offline installer and update signing, a supported hardware/OS matrix, and an independent security review.

## Retired or out of scope (owner, 2026-10-18)

- **Hyper-V builder (T03): retired.** Builds use administrator-configured Docker containers through `NT SERVICE\SourceVaultBuild` (decisions D3/D4). `spikes/builder` stays as historical reference; its guest wrapper is untested. Make no RAM-only or isolation claims for builds.
- **Key rotation: out of scope.** The key never leaves the TPM, and anyone who could misuse it is a local administrator, whom the design already trusts.

## Decisions before real source or public release

- Confirm operator/administrator threat model and intentional-export policy.
- ~~Confirm device-only loss policy versus an owner recovery envelope.~~ Decided 2026-10-18: device-only, no recovery. Do not add a recovery envelope, escrow or key export.
- Approve supported hardware/OS/runtime and offline update strategy.
- ~~Choose a real initial compiler.~~ Decided 2026-10-18: C#/.NET with Microsoft's .NET SDK image (`mcr.microsoft.com/dotnet/sdk:10.0.402`, pinned by digest) on Docker in WSL for the Linux part, and the Windows Server Core variant (`10.0.402-windowsservercore-ltsc2025`, pinned by digest) for the Windows part; the owner's project has both. The PC was upgraded to Windows 11 Pro for Windows containers. Engines (owner, 2026-10-18): Microsoft's standalone Docker engine for Windows (a `docker` service on `\\.\pipe\docker_engine`), and Docker Engine inside the WSL distro for Linux. Docker Desktop is not used: a per-user install had its Windows engine disabled, and it held the standard pipe. Artifact policy: dropping source maps and debug symbols decided 2026-10-18; readable output (for example .NET IL) still does not conceal implementation.
- Signing/release ownership. This also unblocks an Authenticode client check.
- ~~License.~~ Decided 2026-10-18: MIT, chosen by the owner in the GitHub repository (`LICENSE`).
- ~~Per-project permissions.~~ Decided 2026-10-18: none. Every member of *SourceVault Operators* can open every project; access is limited by that group alone.
- ~~Key rotation.~~ Decided 2026-10-18: out of scope.
- ~~Hyper-V builder (T03).~~ Decided 2026-10-18: retired.
