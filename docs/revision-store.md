# Encrypted revision store development baseline

`SourceVault.Storage.RevisionStore` operates at the exact supplied absolute project directory. The caller supplies a nonempty project UUID and a hardware-backed `IKeyProtector` in production. The storage library contains no software protector. Tests use ephemeral RSA only with synthetic data. This component is not yet exposed through a service.

Public entry points:

```csharp
new RevisionStore(string root, Guid projectId, IKeyProtector protector,
                  TimeSpan? lockTimeout = null);
StoreSnapshot Initialize();
StoreSnapshot List();
byte[] Open(Guid expectedSnapshotId, Guid fileId);
StoreSnapshot Commit(Guid expectedSnapshotId, string logicalName,
                     ReadOnlySpan<byte> utf8Content);
void ReadSnapshot(Guid expectedSnapshotId,
                  Action<StoreFile, ReadOnlySpan<byte>> consume); // bulk read; buffers zeroed after each callback
```

`StoreSnapshot` contains `SnapshotId` and an immutable `Files` collection. Each `StoreFile` exposes `FileId`, `LogicalName`, `RevisionId`, and `ByteLength`. Replacing an existing case-insensitive logical name retains its file ID. A stale expected snapshot raises `RevisionConflictException`. Missing documents raise `KeyNotFoundException`. Callers must zero returned document buffers when finished; managed JSON/name objects and caller-owned content have normal managed-memory lifetimes.

Documents were strict UTF-8, at most 256 KiB, and a project held at most 1024 files, until 2026-10-19; see "Any file" at the end. Names are validated deterministic relative basenames, at most 128 UTF-16 characters; Windows reserved devices, separators, control characters, malformed Unicode, trailing periods/spaces, and invalid Windows basename characters are rejected. No API accepts an operator-supplied filesystem path.

Each fresh revision uses an opaque GUID `.svr` filename. Document content and the manifest including logical names are encrypted through `RecordCodec`. `HEAD` contains only the 36-byte canonical snapshot GUID. `PREVIOUS` is an explicit recovery aid, never an automatic fallback. `LOCK` is a cross-process exclusive file lease; acquisition waits at most the configured duration (default five seconds, maximum one minute). The lock is released on process exit. All APIs hold the lease through their operation.

Commit authenticates the current manifest, verifies every referenced document record against its manifest hash, checks the expected snapshot, writes and flushes a fresh encrypted document, writes and flushes a fresh encrypted manifest, then writes and flushes opaque same-directory pointer files and publishes them with two single renames (`File.Move` with overwrite, which is MoveFileEx replacing the target): first a pointer holding the current snapshot id onto `PREVIOUS`, then the new pointer onto `HEAD`. So an interruption at any point leaves the old `HEAD` or the new one, never neither. Until 2026-10-19 this used `File.Replace`, which is two renames internally; a process killed between them left no `HEAD` (the kill test caught it in about a third of runs). After an interrupted commit `PREVIOUS` may equal `HEAD`. Since 2026-10-18 the store then flushes the new `HEAD` (`FlushFileBuffers`), so NTFS commits the logged rename before the commit returns. Initialization uses a no-overwrite rename and refuses any existing state except the lock file. Failures can leave encrypted orphan blobs or an opaque pointer temporary file. Initialization failure before publication requires explicit administrative review. No orphan cleanup or secure deletion is implemented.

**Manifest schema 2** (2026-10-17, see [design](store-performance-design.md)): each manifest entry carries `recordSha256`, the SHA-256 of that document's complete encrypted record. Every operation checks every referenced record's exact size (`346 + byteLength`) and hash against the authenticated manifest, without decrypting it. Missing, truncated, altered, swapped or older-revision records therefore fail immediately, as before. `Open` and `ReadSnapshot` additionally decrypt, which verifies the AES-GCM tag, the context and strict UTF-8. Schema 1 manifests are rejected.

Each store instance caches its last authenticated manifest: ids, logical names, lengths and record hashes, but no document text or keys. The cache is reused only while `HEAD` names the same snapshot and the manifest record file is byte-identical; a commit by any other instance or process forces a fresh decrypt. TPM unwraps per operation: List 0 warm and 1 cold; Commit 0 warm (wraps are public-key operations); Open 1; ReadSnapshot N. Measured on this machine's firmware TPM with 200 files:

| Operation | Time |
|---|---|
| List (warm) | 47 ms |
| Open | 141 ms |
| Commit | 89 ms |
| ReadSnapshot | 8.7 s |

Under the old schema, 30 files already took 1.4 s per operation and 42.6 s for a full read. Record reads check only the record file itself for reparse points, because the lock lease has just checked the root and all of its ancestors and every record lives directly in the root. The store has no plaintext cache.

Authenticated manifests reject unknown/duplicate properties, unsupported schema versions, wrong snapshot IDs, invalid/noncanonical/empty IDs, duplicate file or revision IDs, duplicate case-insensitive names, invalid lengths, and excessive file counts. Any malformed or tampered active state fails explicitly. The implementation never selects `PREVIOUS` because `HEAD` failed. Replaying a complete valid old store is outside the current format's protection.

The caller must provision service-owned root and ancestors with restrictive ACLs. The library walks ancestors and each referenced file using `File.GetAttributes` and rejects reparse points, including dangling links where the OS exposes their attributes. These checks do not establish ownership or prevent a malicious writer racing filesystem operations. Administrator/OS integrity and trusted non-writable ancestors remain prerequisites. No ACLs or services are installed by the tests.

Synthetic tests exercise normal lifecycle, encrypted disk content, stale snapshots, separate-instance concurrency, an actual child-process lock, logical-name/document/count limits, authenticated malformed manifests, cross-project substitution, missing and tampered records, and exceptions injected after document flush, manifest flush, before HEAD publication, and after HEAD publication. Reparse tests report explicit skips when creating symlinks is unavailable. Fault injection is internal and friend-visible only to the test assembly. A child process is also killed (`Process.Kill`, no cleanup) at random points while committing, 50 times per run; each time the store reopens at a complete snapshot, every file decrypts, and the next commit succeeds. Neither exception nor process-kill interruption is equivalent to a power failure: filesystem atomic replacement, directory-entry durability, device write-cache behavior, and machine-crash recovery require separate Windows lab validation.

Run the console test DLL with an explicit absolute synthetic workspace argument; all fixture directories remain beneath that path for inspection. The `hold-lock` argument is reserved for the test runner's child process. No real source should be imported until deployment gates pass.

## Any file (2026-10-19)

Built from [vault-any-file-design.md](vault-any-file-design.md) (sections 2 and 7):
- **Content is bytes.** Each file is `text` (strict UTF-8, no NUL) or `binary`, decided by the store, up to 1 GiB. A project holds at most 25,000 live files and 8 GiB, and commits are refused while the volume would drop below 4 GiB free (`StoreLimitException`).
- **Records:** new records are `SVR2` (see `record-codec.md`). Each project has a project key (`ProjectKey` record, TPM-wrapped); document and history records wrap their data keys with it. It is unwrapped at most once per operation, under the project lock, and zeroed when the operation ends; never cached.
- **Manifest schema 4:** `projectKeyId`, `projectKeySha256`, and per entry `kind` and `sha256` (of the plaintext; `null` for an `SVR1` text record). History chunks are version 2.
- **Checks (decision C4):** every operation checks each record's existence, exact size and that it is not a reparse point; the record's SHA-256 is checked when its content is read, at the end of the stream. `VerifyProject` decrypts everything. The service's startup check uses existence and size, so a same-size change to a file's record is caught when it is read, not at startup; a changed manifest is still refused at startup.
- **Uploads:** `BeginUpload` stages an encrypted record in the vault root's `staging\` folder (ciphertext only, emptied at service start); `CommitChanges` with `Stage` changes moves staged records in under the lock. `RevertFile` points a file at an older record again.
- **Superseded manifests (decision C5):** after `HEAD` is published, manifest records other than `HEAD` and `PREVIOUS` are deleted; a failed delete is retried after the next commit.
- **Migration:** one-way and lazy. Schema 3 stores read as before (`kind: text`, `sha256: null`); the first commit adds a project key and writes schema 4. An older service then refuses the vault.

Tests: `tests/SourceVault.Revisions.Tests` (79 checks), using lowered internal limits for the file, project and free-space boundaries. The TPM benchmark at 25,000 files is a lab item.
