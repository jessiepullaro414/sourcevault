# SourceVault

Local Git project for an offline encrypted-source vault. This is a development build, not a production security product.

Read [architecture decisions](docs/architecture.md), the protocol contracts ([v1](contracts/protocol-v1.md), [v2](contracts/protocol-v2.md)), [implementation status](docs/status.md) and [next tasks](docs/next-tasks.md). The original project plan and subagent task packets are in `local/planning/` (git-ignored; see [local project](docs/local-project.md)).

The intended architecture is a TPM-backed vault service, a controlled WPF/WebView2/Monaco editor, and a separate build service. What exists today:

- **Vault service** (`NT SERVICE\SourceVault`): opens an existing decrypt-only TPM key under its own identity, refuses to start on an untrusted vault root or a tampered project catalog, and serves protocol v2 on a local named pipe restricted to *SourceVault Operators* and the installed client executables.
- **Encrypted store:** AES-256-GCM records with TPM-wrapped data keys; encrypted manifests, file names and per-file history (manifest schema 3); immutable revisions, optimistic conflicts and atomic commits.
- **Editor** (`SourceVault.Editor.exe --vault`): Monaco in WebView2. It has projects and files, rename/move/delete/restore, history with diff and restore, three-way merge after a save conflict, and builds. The host alone holds the session capability; capture exclusion is on (best effort). `--synthetic-preview` keeps the memory-only demo.
- **Importer** (`SourceVault.Import.exe`): imports a folder into a new project, verifies every file through the service, and optionally deletes the verified originals (`--delete-source`).
- **Builds:** a separate `NT SERVICE\SourceVaultBuild` service runs builds in administrator-configured, already-running Docker containers and returns logs that the vault stores encrypted.
- **Setup package:** `Setup-SourceVault.ps1` installs, provisions the key, creates the vault and starts the service.

The service, key provisioning, vault-root gate, catalog tamper checks, pipe DACL and lab client have passed owner-run lab checks on one machine's firmware TPM. The installed editor, the importer, file operations, history, merge and builds are tested in process only. Reboot persistence, real Docker engines, the native editor checklist and an independent security review are still open. See [status](docs/status.md).

Use a .NET 10 SDK on Windows and Node.js for web tests. NuGet sources point only to the vendored local feed; the editor references pinned Microsoft WebView2, while service/storage projects remain BCL-only. From this directory run `pwsh -File scripts/Verify.ps1`. It uses the verified SDK in `local/toolchain` when present, otherwise `dotnet` on PATH, or pass `-DotnetPath <path-to-dotnet.exe>`. Hardware probes and deployment scripts have separate instructions and are not automatically run by verification.

## Open the editor preview

This is the synthetic preview, for trying the editor without the service. On an installed computer, the vault editor is the Start menu shortcut (see [user guide](docs/user-guide.md)). From this project folder:

```powershell
pwsh -File scripts/Start-Editor.ps1
```

The launcher uses the workspace SDK when available, otherwise a supplied `-DotnetPath`. It builds on first run; use `-Rebuild` after closing an older preview to publish changes. The native preview requires an already installed WebView2 Runtime and .NET 10 Windows Desktop Runtime. It installs neither. The local development machine has both.

Alternatively, run `node scripts/Serve-Editor-Preview.mjs 4174` and visit `http://127.0.0.1:4174/index.html`. The server binds loopback only and serves the same bundled assets. Monaco, workers and fonts load locally without a CDN.

You can type, switch files, use undo/find, save session-only revisions, and lock/unlock the view. In preview mode no real files, vault, key service or build service are connected; refreshing or closing discards changes. To review the vault UI in a browser without the service, open `index.html?fake-vault`, which uses a simulated host (`dev/fake-host.mjs`). Capture exclusion is deliberately off in this synthetic preview. See [editor host details](docs/editor-host.md), [review checks](docs/editor-preview-checks.md), and [dependency manifest](docs/editor-dependencies.json).

SDK selection is pinned in `global.json`. Verification uses direct DLL invocation, writes synthetic fixtures under `artifacts/`, and may fail under restricted pipe-access tokens; do not weaken the DACL to make it pass. It creates no TPM keys and installs no services. To create a source-only archive, run `pwsh -File scripts/Package-Source.ps1 -DestinationPath <new-absolute-zip-path>`.

For the storage-only synthetic integration demo after building, run:

```powershell
dotnet tests/SourceVault.Integration.Tests/bin/Release/net10.0/SourceVault.Integration.Tests.dll (Join-Path (Get-Location) 'artifacts/demo')
```

The demo uses an ephemeral RSA fixture inside the test executable and never accepts real source or persists a private key. Production components have no software-key fallback. The local working tree and Git history are the source of truth; earlier ZIP files are historical snapshots.

Security limits: source becomes plaintext in an authorized editor and builder; local administrators are trusted; capture exclusion is best effort; arbitrary editable code and readable output can intentionally carry source. No RAM-only or absolute anti-extraction guarantee is made.

Licensed under the [MIT License](LICENSE), chosen by the owner on 2026-10-18. Vendored Monaco and WebView2 keep their own licenses (`src/SourceVault.Editor.Web/vendor/monaco/LICENSE`, `vendor/packages/WebView2-LICENSE.txt`).

## Install on a computer

To install SourceVault on another Windows computer, run `powershell -ExecutionPolicy Bypass -File scripts\Build-Installer.ps1`. It builds a standalone package (`artifacts\installer\SourceVault-Setup-<date>.zip`): the service, the editor, the lab client, a private .NET runtime, the setup and uninstall scripts, and a SHA-256 manifest of every file.

On the target computer, run `Setup-SourceVault.ps1` from an elevated PowerShell. It installs, provisions the TPM key, creates the vault, starts the service, and adds a Start menu shortcut. [docs/user-guide.md](docs/user-guide.md), shipped in the package as `README.md`, covers setup, daily use and the current limits.
