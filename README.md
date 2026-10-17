# SourceVault

Local Git project for an offline encrypted-source vault. This is a development build, not a production security product.

Read [architecture decisions](docs/architecture.md), [protocol baseline](contracts/protocol-v1.md), and [implementation status](docs/status.md). The detailed project plan and subagent task packets are in the parent output directory.

The current milestone contains twelve buildable projects and **238 passing synthetic checks**, with four skipped symlink checks. It includes authenticated encryption, an encrypted revision store, a Windows TPM adapter, an SCM-compatible service foundation, integration tests, and the earlier protocol/pipe/builder prototypes. See [next assignments](docs/next-tasks.md). Real TPM operations, deployed service accounts and Hyper-V isolation remain unverified.

The intended architecture is a TPM-backed vault service, a controlled WPF/WebView2/Monaco editor, and a separate isolated build broker. The record codec and revision store now encrypt document content and manifests, preserve immutable revisions, reject conflicting updates, and fail on damaged active state. The service foundation opens only an existing TPM key under its required identity; source endpoints remain disabled pending deployment validation. No editor or production installer is delivered yet.

Use a .NET 10 SDK on Windows. Projects intentionally clear NuGet feeds and use only the base class library. From this directory run `pwsh -File scripts/Verify.ps1 -DotnetPath <path-to-dotnet.exe>`. Hardware probes and deployment scripts have separate instructions and are not automatically run by verification.

SDK selection is pinned in `global.json`. Verification uses direct DLL invocation, writes synthetic fixtures under `artifacts/`, and may fail under restricted pipe-access tokens; do not weaken the DACL to make it pass. It creates no TPM keys and installs no services. To create a source-only archive, run `pwsh -File scripts/Package-Source.ps1 -DestinationPath <new-absolute-zip-path>`.

For the storage-only synthetic integration demo after building, run:

```powershell
dotnet tests/SourceVault.Integration.Tests/bin/Release/net10.0/SourceVault.Integration.Tests.dll (Join-Path (Get-Location) 'artifacts/demo')
```

The demo uses an ephemeral RSA fixture inside the test executable and never accepts real source or persists a private key. Production components have no software-key fallback. The local working tree and Git history are the source of truth; earlier ZIP files are historical snapshots.

Security limits: source becomes plaintext in an authorized editor and builder; local administrators are trusted; capture exclusion is best effort; arbitrary editable code and readable output can intentionally carry source. No RAM-only or absolute anti-extraction guarantee is made.

No license has been selected yet. Choose a license before public distribution; no license grant is implied by this development repository.
