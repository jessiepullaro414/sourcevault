# SourceVault protocol v2 (owner-approved 2026-10-17)

Supersedes v1 for the service pipe. v1 stays valid for the IPC spike only. Design and decisions: `docs/pipe-endpoints-design.md`. Synthetic data only until the owner decides otherwise.

## Framing and envelope

Unchanged from v1 except for the version and operation catalog. A frame is a 4-byte little-endian length (1..1,048,576) followed by strict UTF-8 JSON, nesting ≤ 16. Duplicate or unknown properties, comments, trailing commas, invalid UTF-8 and non-canonical UUIDs are rejected.

- **Request:** `{version: 2, requestId: <canonical UUID>, operation, body: {}}`.
- **Response:** `{version: 2, requestId, ok, code, body | null}`.
- **Server timing:** waits up to 5 minutes for a frame to begin; once it begins, the rest must arrive within 10 seconds.
- **Malformed frames** — including wrong version, an unknown operation, or a v1-only operation — close the connection without a response.

## Transport and peers

- **Pipe:** `\\.\pipe\SourceVault.v1`.
- **Owner and DACL:** the pipe is owned by `NT SERVICE\SourceVault`. Its protected DACL gives the service account full access (to create instances), and SYSTEM and *SourceVault Operators* exactly `0x00120083` (read, write, read attributes, read permissions, synchronize). Remote clients are rejected, the first instance uses first-instance protection, and there are at most 4 instances.
- **Server identity:** the client verifies the pipe's owner SID before sending anything. Users cannot open service processes, so the owner is the server's identity. Client SQOS is identification-level only.
- **Per-frame checks:** on every frame the server checks the client token through identification-level impersonation: it must be a member of *SourceVault Operators*, have INTERACTIVE, and lack NETWORK and SERVICE. The client process image must be one of the installed clients under `C:\Program Files\SourceVault\`, at exact paths. User, logon session and PID must not change during a connection.
- **Client duty:** installed clients grant `NT SERVICE\SourceVault` only `PROCESS_QUERY_LIMITED_INFORMATION` on their own process (`VaultPipe.AllowServiceToIdentifyThisProcess`). Otherwise they are rejected.
- **Limits of these checks:** the executable check is defense in depth. It does not stop code injected into an allowed client. A same-user process that passes its pipe handle on and exits could have its PID reused.

## Sessions

- **`BeginSession`** issues a 256-bit capability, once per connection. The session is bound to the connection, the OS user and the logon session, and not to a project.
- **Every other operation** requires `capability`. A missing or wrong capability returns `Unauthorized` and closes the connection.
- **Expiry:** 5 minutes idle and 30 minutes absolute, on a monotonic clock. Expiry returns `SessionExpired` and closes the connection.
- **Windows sessions:** lock, logoff, console disconnect or remote disconnect closes every vault connection from that Windows session.

## Operations

Bodies are exact: every listed field must be present, nothing else is allowed, and ids are canonical UUIDs. Content travels as `contentBase64` (standard base64 of strict UTF-8, ≤ 256 KiB decoded). Binary files and anything larger use the any-file operations at the end of this file.

| Operation | Request body | Success body |
|---|---|---|
| BeginSession | `{}` | `capability`, `idleSeconds`, `absoluteSeconds` |
| ListProjects | `capability` | `projects[]`: `projectId`, `name`, `snapshotId`, `fileCount` |
| CreateProject | `capability`, `name` (1–64 chars, no control characters or edge whitespace) | `projectId`, `snapshotId` |
| ListFiles | `capability`, `projectId` | `snapshotId`, `files[]`: `fileId`, `name`, `revisionId`, `byteLength` |
| OpenDocument | `capability`, `projectId`, `fileId`, `expectedSnapshotId` | `snapshotId`, `revisionId`, `contentBase64` |
| CreateFile | `capability`, `projectId`, `name`, `expectedSnapshotId`, `contentBase64` | `snapshotId`, `fileId`, `revisionId` |
| ApplyEdits | `capability`, `projectId`, `fileId`, `expectedSnapshotId`, `contentBase64` | `snapshotId`, `revisionId` |
| EndSession | `capability` | `{}`, then the connection closes |
| RequestBuild, GetBuildStatus | any | always `Unavailable` |

Concurrency is project-wide and optimistic: any commit in the project since `expectedSnapshotId` makes OpenDocument, CreateFile and ApplyEdits return `RevisionConflict`. Names are case-insensitive, so CreateFile and CreateProject return `AlreadyExists` on a clash.

## Error codes

`Ok`, `InvalidRequest`, `UnsupportedVersion`, `UnsupportedOperation`, `Unauthorized`, `NotFound`, `RevisionConflict`, `AlreadyExists` (new in v2), `LimitExceeded`, `SessionExpired`, `Canceled`, `Unavailable`, `InternalError`.

- `Unavailable` covers vault integrity, key and store failures; the input itself was valid.
- Errors never contain exception text, names or content.

Not in v2: deleting or renaming files and projects (project deletion was added later; see the end of this file), per-file history, per-project permissions, and builds.

## Addition: folder paths and ImportFiles (owner request, 2026-10-18)

**File names are relative paths.** Segments are separated by `/`. A name has at most 32 segments and 260 characters in total, and each segment follows the earlier file-name rules. A path can't be both a file and a folder (`a` alongside `a/b`); that clash returns `AlreadyExists`. The storage format is unchanged: names live inside the encrypted manifest.

**`ImportFiles`.** Request body: `capability`, `projectId`, `expectedSnapshotId`, and `files[]`, where each entry is `{name, contentBase64}`. Success body: `snapshotId`, `imported`.

- 1–256 entries per request, committed as one snapshot, all or nothing.
- **Create-only:** a name that already exists, or clashes with a path, returns `AlreadyExists`, and nothing is written.
- Duplicate names within one request return `InvalidRequest`; more than 256 entries returns `LimitExceeded`.
- Used by `SourceVault.Import.exe`, an installed client allowed at `<install root>\client\SourceVault.Import.exe`.

## Addition: DeleteProject (owner request, 2026-10-18)

**`DeleteProject`.** Request body: `capability`, `projectId`, `confirmName`. Success body: `filesRemoved` (boolean).

- `confirmName` must equal the project's name exactly, including letter case. A mismatch returns `InvalidRequest`, and the error never echoes the name.
- The project and all its revisions are deleted for every connection. Later requests for it return `NotFound`, and its name can be reused.
- `filesRemoved` is false when some encrypted files were in use. The project is already gone from the catalog, and the service removes the files later, at the latest on its next start. See `contracts/storage-v1.md`.
- Deleting or renaming files and renaming projects were added later; see below.

## Addition: file and project rename, file delete (owner request, 2026-10-18)

See `docs/files-history-builds-design.md`, section 1. Every operation also takes `capability`.

| Operation | Request body | Success body |
|---|---|---|
| RenameFile | `projectId`, `fileId`, `newName`, `expectedSnapshotId` | `snapshotId` |
| RenameFolder | `projectId`, `oldPrefix`, `newPrefix`, `expectedSnapshotId` | `snapshotId`, `renamed` |
| DeleteFile | `projectId`, `fileId`, `expectedSnapshotId` | `snapshotId` |
| RenameProject | `projectId`, `newName` | `{}` |

- **RenameFile** moves a file to any valid path. Its file id and current revision are kept: only the encrypted manifest changes, and no content is re-encrypted.
- **RenameFolder** moves every file whose path starts with `oldPrefix/` (case-insensitive) in one commit. No such file returns `NotFound`; a resulting path that is too long or too deep returns `InvalidRequest`.
- **DeleteFile** removes the file from the snapshot. With manifest schema 3 it moves to the deleted list and can be restored (see "file history" below).
- **Errors:**
  - a name taken (case-insensitive) or a file/folder clash: `AlreadyExists`;
  - an unknown file: `NotFound`;
  - a stale `expectedSnapshotId`: `RevisionConflict`.

  Errors never echo names.

## Addition: file history, deleted files, restore (owner-approved, 2026-10-18)

See `docs/files-history-builds-design.md`, section 2. Every operation also takes `capability`.

| Operation | Request body | Success body |
|---|---|---|
| ListHistory | `projectId`, `fileId`, `page` (integer, 0 = newest, ≤ 10,000) | `fileId`, `revisions[]`, `more` |
| OpenRevision | `projectId`, `fileId`, `revisionId` | `revisionId`, `contentBase64` |
| ListDeletedFiles | `projectId` | `files[]`: `fileId`, `name`, `byteLength`, `deletedAt`, `deletedBy` |
| RestoreFile | `projectId`, `fileId`, `newName`, `expectedSnapshotId` | `snapshotId` |

- **ListHistory** returns revisions newest first: `revisionId`, `savedAt` (UTC or null), `savedBy` (account or null), `byteLength`, `action`, and `name`, the path the file had at that save. A page holds up to 256 entries, and `more` says whether older pages exist. It works for live and deleted files.
- **OpenRevision** decrypts any revision in a live or deleted file's history.
- **RestoreFile** brings back a deleted file under `newName`, which can be its old path or a new one, at its last revision. A taken name returns `AlreadyExists`.
- **savedBy / deletedBy** is the calling operator's Windows account name. The service records it only inside encrypted records.
- **DeleteFile** now keeps the file's history, and the file can be restored.

Merging needs no new protocol operation. The editor host fetches the text its copy was based on, and the current text, with `ListFiles` and `OpenRevision`. It saves the merge with `ApplyEdits` only if the file is still at the revision that was merged with; otherwise the save reports `Conflict` and the merge is redone.

## Addition: builds (owner-approved, 2026-10-18)

See `docs/files-history-builds-design.md`, section 4. Every operation also takes `capability`. If no build service is configured, every build operation returns `Unavailable`, whatever its body.

| Operation | Request body | Success body |
|---|---|---|
| ListBuildTargets | — | `targets[]`: names from the administrator's `build.json` |
| RequestBuild | `projectId`, `target` | `buildNumber` (1, 2, … per project) |
| GetBuildStatus | `projectId`, `buildNumber` | a build |
| ListBuilds | `projectId` | `builds[]`, newest first (the 100 newest stored, plus queued and running) |
| OpenBuildLog | `projectId`, `buildNumber` | `contentBase64` (UTF-8, ≤ 250 KiB; the end of the output is kept) |

- **A build** has these fields: `buildNumber`, `target`, `state` (`queued` / `running` / `succeeded` / `failed` / `timedOut` / `rejected` / `unavailable`), `reason` (a fixed code or null), `exitCode`, `requestedBy`, `requestedAt`, `finishedAt`, `artifactFolder` (a host path or null), `artifactFiles`, `artifactBytes`, `artifactsDropped` (output files left out because they can reveal source, listed at the end of the log; added 2026-10-18, so older clients ignore it and records stored before then read as 0), `logAvailable` and `logTruncated`.
- **Errors:** an unknown target or build returns `NotFound`. A full queue (1 running and 4 waiting for that target) returns `LimitExceeded`. A build service that isn't reachable returns `Unavailable`.
- **What is built:** the project's current saved snapshot, decrypted by the vault service and sent to the build service over the private build pipe below. The buffers are zeroed afterwards.

### The private build pipe `\.\pipe\SourceVault.Build.v1`

- **Access:** owned by `NT SERVICE\SourceVaultBuild`. Its protected DACL gives that account full access and `NT SERVICE\SourceVault` exactly `0x00120083`; nobody else has access, and remote clients are rejected.
- **Identity checks:** the vault service verifies the pipe owner before sending anything, and the build service checks the client token's user on every connection.
- **Framing:** frames are the same as above (≤ 1 MiB), with one request per connection:
  - `{op: "targets"}`
  - a build, version 2 (any file, streamed; both services are installed together):
    1. `{op: "build", version: 2, target, projectId, buildNumber, fileCount, totalBytes}`, answered `{ok, accepted}` or a refusal;
    2. file-table frames `{files: [{name, length}]}` (at most 1,000 entries each), read in full and checked (names, duplicates, 25,000 files, 1 GiB each, 8 GiB in all) before `{ok, ready}`;
    3. data frames `{dataBase64}` (at most 512 KiB, never spanning two files), in table order, or `{abort}` when a record fails its integrity check (reason `SourceUnavailable`).

    The build service streams the tar into the container as the data arrives. The copy has its own 30-minute bound (reason `CopyTimedOut`), not counted in the build's timeout.

  The reply is `{ok, …}`; the build reply carries `state`, `reason`, `exitCode`, `logBase64` (≤ 512 KiB), `logTruncated`, `artifactFolder`, `artifactFiles`, `artifactBytes` and `artifactsDropped`.
- **The build service** never receives project names and never opens the vault or the TPM key.

## Addition: encrypted package import (owner-approved, 2026-10-19)

See `docs/package-import-design.md`. Every operation also takes `capability`. Without a provisioned import key every one of these returns `Unavailable`.

| Operation | Request body | Success body |
|---|---|---|
| GetImportRecipient | — | `computer`, `publicKey` (base64 SubjectPublicKeyInfo), `fingerprint` (hex SHA-256) |
| BeginPackageImport | `length` | `{}` |
| PackageImportPart | `offset`, `dataBase64` | `received` |
| FinishPackageImport | `projectName` | `projectId`, `snapshotId`, `imported`, `packageId`, `sender` |

- **Upload rules:**
  - one upload per connection, and one across the whole service; a second gets `Unavailable`;
  - `length` must be at least 624 bytes and at most `Package.MaxPackageBytes` (a little over 8 GiB), otherwise `LimitExceeded`;
  - the service spools the encrypted upload into `staging\` with a running hash, checks the signature against that hash, then re-hashes the spool while streaming its files into the store (package format v2; v1 packages are still accepted);
  - parts must start exactly where the last one ended; a part is at most 512 KiB, and must not go past `length`.
- **Finishing:**
  - `FinishPackageImport` ends the upload whatever its result. So does closing the connection.
  - It creates a **new** project only. A taken name is `AlreadyExists`, and nothing is written.
- **New error codes:**
  - `PackageRejected`: the package is malformed, altered, made for another recipient, or contains files the vault does not accept. Nothing is created.
  - `UnknownSender`: the signature is valid, but its computer is not registered in `C:\ProgramData\SourceVault\senders`.

## Addition: search all files (owner request, 2026-10-19)

The editor's **Search all files**. Plain text only: no regular expressions, so a query can never be expensive.

| Operation | Request body | Success body |
|---|---|---|
| SearchProject | `capability`, `projectId`, `query`, `matchCase`, `wholeWord` | `matches[]`, `truncated`, `filesSearched`, `filesSkipped` |

- **Query:** 1 to 200 characters on one line; otherwise `InvalidRequest`. `matchCase` and `wholeWord` must be JSON booleans. Whole word means the characters on both sides are not letters, digits or `_`.
- **What is searched:** the text of every `text` file of at most 16 MiB in the project's current snapshot, line by line (a trailing CR is ignored). Binary and larger files are counted in `filesSkipped`. Unsaved edits are not in the vault; the editor searches those itself, in its drafts.
- **Each match:** `fileId`, `name`, `line` and `column` (1-based, UTF-16 units), `length`, `preview` (the line around the match, at most 240 characters plus a leading `…` when cut, leading spaces removed) and `previewOffset` (where the match starts in `preview`).
- **Limits:** at most 500 matches; `truncated` is `true` when there were more. Matches are sorted by name (ignoring case), line and column.
- **Cost:** a search decrypts every file of the project inside the service, like a build. Only matching lines leave it.

## Addition: any file (approved 2026-10-19)

See `docs/vault-any-file-design.md`. The vault stores files of any kind (binary, UTF-16, large text) byte-exact. Every operation also takes `capability`.

**Listings.** `ListFiles`, `ListHistory` and `ListDeletedFiles` entries gain:
- `kind`: `text` (strict UTF-8, no NUL) or `binary`;
- `sha256`: hex SHA-256 of the plaintext, or `null` for a legacy record (text written before this change).

`byteLength` can now be up to 1 GiB (a JSON integer).

**Inline operations keep their shape.** `contentBase64` is still at most 256 KiB:
- `OpenDocument` and `OpenRevision`: a binary revision is `NotText`; text over 256 KiB is `LimitExceeded` (read it with `ReadTextPart`);
- `ApplyEdits` (the editor saving text) still takes strict UTF-8 only;
- `CreateFile` and `ImportFiles` accept any content up to 256 KiB per file; the store decides `text` or `binary`.

**Uploads in parts** (content over 256 KiB, or any file from disk):

| Operation | Request body | Success body |
|---|---|---|
| BeginUpload | `projectId`, `fileId` (the file to replace, or `null` for a new file), `length` (0 … 1 GiB), `sha256` (hex, of the plaintext) | `uploadId`, `fileId`, `partBytes` (524,288) |
| UploadPart | `uploadId`, `offset`, `dataBase64` (1 … 512 KiB decoded) | `received` |
| CommitUploads | `projectId`, `expectedSnapshotId`, `files[]`: `{uploadId, name}` (1 … 256; `name` is `null` for a replacement) | `snapshotId`, `files[]`: `{fileId, revisionId, kind}` |
| CancelUpload | `uploadId` | `{}` |

- Parts arrive in order, without gaps. At the last byte the plaintext SHA-256 must equal `sha256`, otherwise `InvalidRequest` and the upload is discarded.
- The service writes only ciphertext, into `staging\` under the vault root; it never sees an OS path.
- `CommitUploads` is all or nothing. A named entry creates a file (a taken name is `AlreadyExists`); an unnamed one replaces the file its upload began for (`NotFound` if it is gone). A stale snapshot is `RevisionConflict`, and the staged uploads survive it.
- Bounds per connection: one upload receiving at a time; at most 256 staged, holding at most 8 GiB. An upload with no part for 2 minutes is discarded. Closing the connection, `EndSession` or expiry discards them all.

**Reading, reverting, verifying:**

| Operation | Request body | Success body |
|---|---|---|
| ReadTextPart | `projectId`, `fileId`, `revisionId`, `offset` (a multiple of 524,288) | `length` (the whole file), `dataBase64` (that segment) |
| RevertFile | `projectId`, `fileId`, `revisionId`, `expectedSnapshotId` | `snapshotId` |
| VerifyProject | `projectId` | `filesVerified`, `filesFailed` |

- `ReadTextPart` is only for `text` revisions of at most 1 MiB (the editor's limit): a binary revision is `NotText`, larger text `LimitExceeded`. **No operation returns binary content or text over 1 MiB to a client**; such content leaves the service only through a build.
- `RevertFile` makes a revision from the file's own history current again (history action `reverted`). Nothing is re-encrypted or sent.
- `VerifyProject` decrypts and checks every live file. A failure reports counts only, never names, and the project is then `Unavailable`, as after a failed startup check.

**Limits** (`LimitExceeded`): 1 GiB per file, 25,000 files and 8 GiB per project, and 4 GiB of free disk kept on the vault volume.

**New error code:** `NotText`: the operation needs text and the revision is binary.

## Addition: offline projects (owner decision, 2026-10-20)

A project that fails authentication at service start, or later fails `VerifyProject`, is **offline** instead of stopping the service.
- `ListProjects` gains `unavailable[]`: `{projectId, name}` for each offline project. They are not in `projects[]`.
- Every operation on an offline project returns `Unavailable`, except `DeleteProject`.
- An unexpected entry in the vault root or a damaged project index still refuses the whole service (1066/11).
- The service writes `%ProgramData%\SourceVault\status\vault.json` (version, time, count of available projects, ids of offline ones; never names) for `Setup-SourceVault.ps1 -Status`.
