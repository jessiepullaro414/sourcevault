# Named-pipe endpoints — design (proposed, not built)

Status: design approved by the owner 2026-10-17 (decisions at the end); not built yet. This builds on the IPC spike (`spikes/ipc`), `SourceVault.Contracts` framing, the project catalog, and storage schema 2. Synthetic data only until the owner decides otherwise.

## What exists and is reused

- **Framing and envelope** (`SourceVault.Contracts`): 4-byte length, 1 B to 1 MiB; strict UTF-8 JSON; duplicate or unknown fields rejected; depth ≤ 16; 10-second frame deadline; canonical UUIDs.
- **Spike pipe security**:
  - protected DACL with exact rights `0x00100083`, never generic write;
  - `FILE_FLAG_FIRST_PIPE_INSTANCE` and `PIPE_REJECT_REMOTE_CLIENTS`;
  - per-frame `RunAsClient` token checks (SID, session, PID);
  - the client checks the server's PID and token SID before sending anything;
  - a random 256-bit capability per connection, 5-minute idle / 30-minute absolute expiry on a monotonic clock, terminal on expiry.
- **Service**: verified catalog and key at startup; `ProjectCatalog` and `RevisionStore` with exact unwrap budgets.

## Transport

- **Pipe:** `\\.\pipe\SourceVault.v1`, created by the service (`NT SERVICE\SourceVault`) only after startup verification passes. RUNNING continues to mean "verified", now also "listening".
- **DACL:** protected; SYSTEM plus the **allowed operators** (decision 1) get exactly `0x00100083`. Nobody else gets any right, including Administrators: admins can still act as SYSTEM, which is outside the threat model.
- **Every frame, the server checks** the client's token user SID against the allowed operators, rejects network and service logons, records the token session id, and reads the client PID from the OS.
- **Client executable** (decision 2): optionally, the client's process image path must be one of the admin-owned installed clients under `C:\Program Files\SourceVault\`. Checked by opening the process handle first, so a reused PID cannot pass. This is defense in depth only: it does not stop same-user code injection into the editor, and the docs will say so.
- **The client, before sending anything,** verifies that the server process token is `NT SERVICE\SourceVault`, as in the spike.
- **Concurrency** (decision 3): up to N connections, each with at most one request in flight and its own capability. Store operations serialize on the store's existing file lock.

## Sessions

- **`BeginSession`** returns a capability bound to the connection, the OS user SID and the logon session.
- **Not bound to a project.** Projects are named per request: one editor works across projects. This is a contract change from v1, which bound sessions to a project.
- **No per-project permissions in this version.** Every allowed operator can see every project.
- **Invalidation:** the service subscribes to session-change notifications (`SERVICE_ACCEPT_SESSIONCHANGE`). On lock, logoff or disconnect of a Windows session it ends every SourceVault session from that Windows session, so locking the workstation forces a new `BeginSession` after unlock. Idle and absolute expiry are unchanged.

## Operations (protocol v1 → v2 catalog)

Every request body is strict: exact fields, canonical UUIDs, and the capability on every operation except `BeginSession`. Document content travels as **base64 of the UTF-8 bytes**. That gives a fixed bound: 256 KiB of content becomes about 342 KiB in the frame. JSON string escaping could expand control characters 6× and overflow the 1 MiB frame.

| Operation | Request body | Response body | Notes |
|---|---|---|---|
| BeginSession | `{}` | `capability`, `idleSeconds`, `absoluteSeconds` | One per connection |
| ListProjects | `capability` | `projects[]`: `projectId`, `name`, `snapshotId`, `fileCount` | 0 TPM unwraps warm |
| CreateProject | `capability`, `name` | `projectId`, `snapshotId` | **New**. `AlreadyExists` on a name clash |
| ListFiles | `capability`, `projectId` | `snapshotId`, `files[]`: `fileId`, `name`, `revisionId`, `byteLength` | 0 warm |
| OpenDocument | `capability`, `projectId`, `fileId`, `expectedSnapshotId` | `snapshotId`, `revisionId`, `contentBase64` | 1 unwrap |
| CreateFile | `capability`, `projectId`, `name`, `expectedSnapshotId`, `contentBase64` | `snapshotId`, `fileId`, `revisionId` | **New**. `AlreadyExists` if the name is taken (the store would otherwise replace it) |
| ApplyEdits | `capability`, `projectId`, `fileId`, `expectedSnapshotId`, `contentBase64` | `snapshotId`, `revisionId` | Whole-file replacement. `RevisionConflict` if anything in the project changed since `expectedSnapshotId` (project-wide optimistic lock: strict, simple) |
| EndSession | `capability` | `{}` | Ends the session and closes the connection |
| RequestBuild, GetBuildStatus | — | — | `Unavailable` |
| CloseDocument | — | — | **Removed**: the server keeps no per-document state |

Error codes: those in the contract, plus **`AlreadyExists`** (new). No exception text and no names or content ever appear in errors.

Not in this version: deleting or renaming files and projects (the store has no delete), and per-file revision history.

## Plaintext handling

- **Service side:** decrypted bytes are encoded straight to base64 into a pooled byte buffer with `Utf8JsonWriter`, never through a managed `string`. Response buffers are zeroed after writing. Incoming content is decoded into a byte buffer that is zeroed after commit.
- **Remaining copies:** some still exist in pipe buffers, the client process and the renderer, as the current limits already state.
- **Logs:** none for content. At most bounded in-memory counters (operation, result code). An Event Log audit sink needs source registration and is deferred.

## Build steps

1. **Service endpoints.** Pipe server, policy, sessions and the operations above, with in-process pipe tests on a temporary vault and test protector. The peer policy is injectable for tests and pinned to the service account in production. Contract v2 document.
2. **Lab client.** A small console `SourceVault.LabClient`, installed under Program Files by the lab scripts. It verifies the server, lists projects, opens a file, and applies a synthetic edit. This proves the path on the real TPM with real accounts, including a denial test from a non-operator account if one is available.
3. **Editor host.** The WPF host, not the renderer, connects and holds the capability. The renderer bridge grows a reviewed list/open/save schema carrying content, then a native validation pass.

## Decisions (owner, 2026-10-17)

1. **Access is by local group.** The lab install script creates the group *SourceVault Operators*, and an administrator manages its membership. The pipe DACL and the per-frame token check both use the group SID.
2. **The client executable check is required.** The connecting process must be an installed SourceVault client under `C:\Program Files\SourceVault\`. It is documented as defense in depth, not protection against code injected into the client.
3. **Up to 4 concurrent connections**, each with its own session and at most one request in flight.
