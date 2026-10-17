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

Bodies are exact: every listed field must be present, nothing else is allowed, and ids are canonical UUIDs. Content travels as `contentBase64` (standard base64 of strict UTF-8, ≤ 256 KiB decoded).

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

Not in v2: deleting or renaming files and projects, per-file history, per-project permissions, and builds.
