# SourceVault

Early implementation of an offline encrypted-source vault. This repository is a feasibility milestone, not a production security product.

Read [architecture decisions](docs/architecture.md), [protocol baseline](contracts/protocol-v1.md), and [implementation status](docs/status.md). The detailed project plan and subagent task packets are in the parent output directory.

The first milestone contains five buildable projects and **85 passing synthetic checks**: shared protocol validation, a TPM-provider spike, a local pipe authorization spike, and a bounded builder-input/artifact spike. See [next assignments](docs/next-tasks.md) for the remaining work. Real TPM operations, deployed service accounts and Hyper-V isolation remain unverified.

The intended architecture is a TPM-backed vault service, a controlled WPF/WebView2/Monaco editor, and a separate isolated build broker. The initial work targets the three hardest integration boundaries first: TPM/service keys, local pipe authorization, and build/storage isolation. No editor or production installer is delivered yet.

Use a .NET 10 SDK on Windows. Projects intentionally clear NuGet feeds and use only the base class library. From this directory run `pwsh -File scripts/Verify.ps1 -DotnetPath <path-to-dotnet.exe>`. Hardware probes and deployment scripts have separate instructions and are not automatically run by verification.

SDK selection is pinned in `global.json`. Verification uses direct DLL invocation, writes synthetic fixtures under `artifacts/`, and may fail under restricted pipe-access tokens; do not weaken the DACL to make it pass. It creates no TPM keys and installs no services. To create a source-only archive, run `pwsh -File scripts/Package-Source.ps1 -DestinationPath <new-absolute-zip-path>`.

Security limits: source becomes plaintext in an authorized editor and builder; local administrators are trusted; capture exclusion is best effort; arbitrary editable code and readable output can intentionally carry source. No RAM-only or absolute anti-extraction guarantee is made.

No license has been selected yet. Choose a license before public distribution; no license grant is implied by this development repository.
