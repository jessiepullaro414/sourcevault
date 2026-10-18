# Initial architecture decisions (ADR 0001)

Status: development baseline, protocol and storage contracts v1. Production release is blocked until lab validation and security review.

- Windows 11 Pro/Enterprise x64 with TPM 2.0 and Hyper-V is the proposed deployment. Exact supported builds and hardware remain lab gates. Development currently uses a restricted Windows environment.
- Target .NET 10 LTS and BCL-only feasibility projects. The future editor is WPF/WebView2 with offline Monaco; it is not included in the feasibility milestone.
- Operators are standard users on a managed workstation; administrators/OS/firmware and provisioning are trusted. Same-user renderer inspection and intentional source export through modified outputs are outside the confidentiality claim.
- Initial build recipe will use TypeScript/JavaScript and one pinned compiler, with synthetic input. Its JavaScript output is plainly readable and does not promise concealment of implementation. Later native/managed build profiles require their own artifact policy.
- Select the practical encrypted-storage builder profile. RAM-only processing is experimental and must never be inferred from tar/stdin or tmpfs alone.
- A device-only nonexportable CNG Platform Crypto Provider wrapping key is the initial policy. No recovery recipient or software fallback is implemented. Owner decision (2026-10-18): there is no recovery, by design. Clearing the TPM, deleting the service profile or replacing the hardware makes the vault permanently unreadable. Never clear a real TPM to validate failure behavior.
- The key service and build broker are separate identities. Compiler code never runs in the key service. Prototype projects do not themselves constitute deployed services.
- The coordinator owns contracts, repository configuration, integration, and status. Record codec, revision store, and Windows service components have distinct worker ownership. Test software protectors are confined to test assemblies; the production host accepts only an existing TPM-protected key.

This directory is now a local Git project with a `main` branch. The source tree is the working project; earlier ZIPs are historical snapshots. Sidebar project registration is separate from the filesystem/Git project and is not performed by the available project tools.

Data storage, renderer, IPC, guest isolation, packaging, signing, installation, and updates have separate gates. No source-vault product claim follows from a passing unit test or an interactive spike.
