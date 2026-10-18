# SourceVault

Local Git project for an offline encrypted-source vault. This is a development build, not a production security product.

Read [architecture decisions](docs/architecture.md), [protocol baseline](contracts/protocol-v1.md), and [implementation status](docs/status.md). The detailed project plan and subagent task packets are in the parent output directory.

The project now includes a runnable **Monaco text editor preview**, authenticated encryption, an encrypted revision store, a Windows TPM adapter, an SCM-compatible service foundation, integration tests, and protocol/pipe/builder prototypes. Real TPM operations, deployed service accounts and Hyper-V isolation remain unverified.

The intended architecture is a TPM-backed vault service, a controlled WPF/WebView2/Monaco editor, and a separate isolated build broker. The record codec and revision store encrypt document content and manifests, preserve immutable revisions, reject conflicting updates, and fail on damaged active state. The service foundation opens only an existing TPM key under its required identity; source endpoints remain disabled pending deployment validation. The editor currently uses synthetic documents and in-memory revisions. No production installer is delivered yet.

Use a .NET 10 SDK on Windows and Node.js for web tests. NuGet sources point only to the vendored local feed; the editor references pinned Microsoft WebView2, while service/storage projects remain BCL-only. From this directory run `pwsh -File scripts/Verify.ps1 -DotnetPath <path-to-dotnet.exe>`. Hardware probes and deployment scripts have separate instructions and are not automatically run by verification.

## Open the editor

From this project folder:

```powershell
pwsh -File scripts/Start-Editor.ps1
```

The launcher uses the workspace SDK when available, otherwise a supplied `-DotnetPath`. It builds on first run; use `-Rebuild` after closing an older preview to publish changes. The native preview requires an already installed WebView2 Runtime and .NET 10 Windows Desktop Runtime. It installs neither. The local development machine has both.

Alternatively, run `node scripts/Serve-Editor-Preview.mjs 4174` and visit `http://127.0.0.1:4174/index.html`. The server binds loopback only and serves the same bundled assets. Monaco, workers and fonts load locally without a CDN.

You can type, switch files, use undo/find, save session-only revisions, and lock/unlock the view. No real files, vault, key service or build broker are connected; refreshing or closing discards changes. Capture exclusion is deliberately off in this synthetic preview. See [editor host details](docs/editor-host.md), [review checks](docs/editor-preview-checks.md), and [dependency manifest](docs/editor-dependencies.json).

SDK selection is pinned in `global.json`. Verification uses direct DLL invocation, writes synthetic fixtures under `artifacts/`, and may fail under restricted pipe-access tokens; do not weaken the DACL to make it pass. It creates no TPM keys and installs no services. To create a source-only archive, run `pwsh -File scripts/Package-Source.ps1 -DestinationPath <new-absolute-zip-path>`.

For the storage-only synthetic integration demo after building, run:

```powershell
dotnet tests/SourceVault.Integration.Tests/bin/Release/net10.0/SourceVault.Integration.Tests.dll (Join-Path (Get-Location) 'artifacts/demo')
```

The demo uses an ephemeral RSA fixture inside the test executable and never accepts real source or persists a private key. Production components have no software-key fallback. The local working tree and Git history are the source of truth; earlier ZIP files are historical snapshots.

Security limits: source becomes plaintext in an authorized editor and builder; local administrators are trusted; capture exclusion is best effort; arbitrary editable code and readable output can intentionally carry source. No RAM-only or absolute anti-extraction guarantee is made.

No license has been selected yet. Choose a license before public distribution; no license grant is implied by this development repository.

## Install on a computer

To install SourceVault on another Windows computer, run `powershell -ExecutionPolicy Bypass -File scripts\Build-Installer.ps1`. It builds a standalone package (`artifacts\installer\SourceVault-Setup-<date>.zip`): the service, the editor, the lab client, a private .NET runtime, the setup and uninstall scripts, and a SHA-256 manifest of every file.

On the target computer, run `Setup-SourceVault.ps1` from an elevated PowerShell. It installs, provisions the TPM key, creates the vault, starts the service, and adds a Start menu shortcut. [docs/user-guide.md](docs/user-guide.md), shipped in the package as `README.md`, covers setup, daily use and the current limits.
