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
```

`StoreSnapshot` contains `SnapshotId` and an immutable `Files` collection. Each `StoreFile` exposes `FileId`, `LogicalName`, `RevisionId`, and `ByteLength`. Replacing an existing case-insensitive logical name retains its file ID. A stale expected snapshot raises `RevisionConflictException`. Missing documents raise `KeyNotFoundException`. Callers must zero returned document buffers when finished; managed JSON/name objects and caller-owned content have normal managed-memory lifetimes.

Documents are strict UTF-8, at most 256 KiB. A project holds at most 1024 files. Names are validated deterministic relative basenames, at most 128 UTF-16 characters; Windows reserved devices, separators, control characters, malformed Unicode, trailing periods/spaces, and invalid Windows basename characters are rejected. No API accepts an operator-supplied filesystem path.

Each fresh revision uses an opaque GUID `.svr` filename. Document content and the manifest including logical names are encrypted through `RecordCodec`. `HEAD` contains only the 36-byte canonical snapshot GUID. `PREVIOUS` is an explicit recovery aid, never an automatic fallback. `LOCK` is a cross-process exclusive file lease; acquisition waits at most the configured duration (default five seconds, maximum one minute). The lock is released on process exit. All APIs hold the lease through their operation.

Commit authenticates the current manifest and all referenced documents, checks the expected snapshot, writes and flushes a fresh encrypted document, writes and flushes a fresh encrypted manifest, then writes/flushes an opaque same-directory temporary pointer and atomically replaces `HEAD` with `File.Replace`, retaining `PREVIOUS`. Initialization uses a no-overwrite rename and refuses any existing state except the lock file. Failures can leave encrypted orphan blobs or an opaque pointer temporary file. Initialization failure before publication requires explicit administrative review. No orphan cleanup or secure deletion is implemented.

`List` and `Open` authenticate every document referenced by the active manifest, including its length and strict UTF-8 encoding, to reject missing or damaged committed state immediately. `Open` then decrypts the selected document for the caller. This deliberately costs multiple private-key unwrap operations per request; TPM throughput and project-size limits require performance measurements on actual hardware before production integration. The store has no plaintext cache.

Authenticated manifests reject unknown/duplicate properties, unsupported schema versions, wrong snapshot IDs, invalid/noncanonical/empty IDs, duplicate file or revision IDs, duplicate case-insensitive names, invalid lengths, and excessive file counts. Any malformed or tampered active state fails explicitly. The implementation never selects `PREVIOUS` because `HEAD` failed. Replaying a complete valid old store is outside the current format's protection.

The caller must provision service-owned root and ancestors with restrictive ACLs. The library walks ancestors and each referenced file using `File.GetAttributes` and rejects reparse points, including dangling links where the OS exposes their attributes. These checks do not establish ownership or prevent a malicious writer racing filesystem operations. Administrator/OS integrity and trusted non-writable ancestors remain prerequisites. No ACLs or services are installed by the tests.

Synthetic tests exercise normal lifecycle, encrypted disk content, stale snapshots, separate-instance concurrency, an actual child-process lock, logical-name/document/count limits, authenticated malformed manifests, cross-project substitution, missing and tampered records, and exceptions injected after document flush, manifest flush, before HEAD publication, and after HEAD publication. Reparse tests report explicit skips when creating symlinks is unavailable. Fault injection is internal and friend-visible only to the test assembly. Exception interruption is not equivalent to a power failure: filesystem atomic replacement, directory-entry durability, device write-cache behavior, and machine-crash recovery require separate Windows lab validation.

Run the console test DLL with an explicit absolute synthetic workspace argument; all fixture directories remain beneath that path for inspection. The `hold-lock` argument is reserved for the test runner's child process. No real source should be imported until deployment gates pass.
