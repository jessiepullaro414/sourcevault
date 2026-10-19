# Design: hardening wave (owner decisions 2026-10-18)

Development items that need no lab, chosen after the 2026-10-18 review. Lab tests for them are in [lab-checklist.md](lab-checklist.md).

## Owner decisions

- **Build output:** source maps, debug symbols and similar files are **dropped** from the published artifacts, and the build result lists what was dropped. The build still succeeds.
- **Client check:** **pin file hashes.** The service accepts a client only if its executable and app files match the hashes setup recorded. Authenticode signer checks wait for a signing decision.
- **Key rotation:** **not now.** The key never leaves the TPM, and anyone who could misuse it is a local administrator, whom the design already trusts. Recorded as out of scope; no recovery envelope either.
- **Hyper-V builder (T03):** **retired.** Builds use the administrator's Docker containers. `spikes/builder` stays as historical reference.

## 1. Pipe client: PID reuse

`VaultPipe.ValidateClient` reads the client PID from the pipe, then opens the process to read its executable. If the connecting process exits in between, Windows can reuse its PID for another process, possibly an allowed client.

Fix: the server records when the connection was accepted. After opening the process (the open handle stops the PID being reused while it is held), it requires:
- the process to still be running;
- its creation time to be no later than the connection time.

A reused PID belongs to a newer process and is refused. The peer's creation time is pinned with the PID for every later frame. A same-user process can still hand an open pipe handle to an allowed client and exit; same-user code is outside the claim, as before.

## 2. Client hash pins

Setup (and the lab install scripts) already write `install-manifest.sha256`, which holds the SHA-256 of every installed file, in the admin-only install root. On each new connection the service reads it again (it is about 70 KB, so an update needs no service restart) and pins, for each allowed client, **every file in that client's install folder**, recursively: `client\` for the lab client and importer (0.6 MB), and `editor\` including its web assets (27 MB), because the renderer's scripts are code too.
- **Missing manifest, a malformed manifest, or a folder with no entries:** that client is refused, so the check fails closed. The service still starts, and the other clients still work.
- **When a client connects:** the folder must hold exactly the pinned files (an extra file, such as a planted DLL, is refused), and each must match its hash. The files are hashed on every new connection (once per connection, not per request), with no cache that timestamps could fool. The cost is tens of milliseconds for the editor's 27 MB, and the editor connects once per unlock.
- **What this adds:** it catches a replaced or modified client file, for example after a permission mistake on Program Files or a partial update. It does not protect against an administrator, who can rewrite the manifest. The running process image is not re-checked, only the files on disk.
- **The shared runtime:** `runtime\` is not pinned per connection, because hashing 70 MB on each connection is too slow. Setup's `-Status` checks every file.

## 3. Durable HEAD publication

Commit already flushes the document, manifest and pointer-file data before swapping `HEAD`. The rename itself was not flushed. (Update 2026-10-19: the kill test then showed `File.Replace` itself is two renames and could leave no `HEAD`; publication now uses two single renames, see `revision-store.md`.) After the replace, the store now opens `HEAD` and flushes it (`FlushFileBuffers`), which commits the NTFS metadata for that file. A new test runs commits in a child process, kills it at random points (`Process.Kill`, no cleanup), and then checks that the store always opens at a complete snapshot. This is process-crash evidence, not power-loss evidence; the power-loss test is a lab step.

## 4. Monaco assets under `PreviewPolicy`

A host test walks every file under the vendored `vendor/monaco` folder and checks that `PreviewPolicy` allows it at its app-origin URL. This catches a policy tightening that would silently break a worker, language module or font. Native loading is still a lab check.

## 5. Dropped build files

`build.json` gains an optional per-target `"keepFileTypes"`: extensions the administrator explicitly allows. Default dropped types:

| Kind | Extensions / names |
|---|---|
| Source maps | `.map` |
| Debug symbols | `.pdb`, `.mdb`, `.dSYM` (folder), `.debug`, `.dwo`, `.dwp` |
| Compiler intermediates | `.ilk`, `.obj`, `.o`, `.iobj`, `.ipdb` |

Static libraries (`.a`, `.lib`, `.exp`) were considered and are **not** dropped: they are ordinary library outputs.

- **Text artifacts** (`.js`, `.mjs`, `.cjs`, `.css`) that end with a `sourceMappingURL` comment pointing at inline data (`data:`) are dropped too, because the inline map embeds the source. The comment is searched only in the last 64 KiB of the file.
- **The build result** gains `artifactsDropped`, a count, shown under the output folder. Up to 100 paths with their reasons (`SourceMap`, `DebugSymbols`, `Intermediate`, `InlineSourceMap`) are appended to the build log, which the vault stores encrypted. A list in the build record would not fit: the index holds 100 builds in one 256 KiB document.
- **keepFileTypes** values: any of the extensions above, `.dsym` for `*.dSYM` folders, and `inline-source-map`.
- **Limits:** this reduces accidental disclosure. A build can still copy source into any kept file; builds remain an export path (see `files-history-builds-design.md`).
