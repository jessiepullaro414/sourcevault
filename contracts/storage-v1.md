# Storage v1 — reviewed development contract

Development baseline, synthetic inputs only. This is envelope encryption and crash-consistent revision storage, not a verified production vault. TPM/service ACL and lab gates still apply. Rollback of an entire valid old store is explicitly not prevented.

## Public component API

Namespace `SourceVault.Storage`, BCL-only library targeting net10.0 (platform independent). Public `IKeyProtector` has `byte[] WrapKey(ReadOnlySpan<byte> key)` and `byte[] UnwrapKey(ReadOnlySpan<byte> wrappedKey)`. Wrapped keys must have exactly 256 bytes (RSA-2048/OAEP-SHA256 for v1). No software implementation lives in production storage code; test RSA protectors exist only in test assemblies. `RecordContext` = project ID, file ID, revision ID, record kind (Document=1, Manifest=2). Nonempty canonical IDs; manifest uses file ID Guid.Empty only. `RecordCodec.Encrypt(context, ReadOnlySpan<byte>, protector)` and `Decrypt(expectedContext, ReadOnlySpan<byte>, protector)` return byte arrays; validate before allocating or invoking unwrap. Public constants and helpers can be added without changing this contract.

`RevisionStore(root, projectId, protector)` operates one project at a trusted absolute service-owned root. Provide Initialize/Create initial empty manifest, List/Open and Commit replacement document APIs with optimistic expected snapshot revision. File IDs are opaque, logical filenames are inside encrypted manifest, no paths supplied by operator. Snapshots and file revisions are immutable. API naming may be finalized by storage worker, but publish signatures before integration. All document source is strict UTF-8 <=256 KiB. Logical names: one relative basename <=128 characters; no separators, colon, dot/dotdot, control chars, Windows reserved device names, or trailing space/dot; validate deterministically cross-platform.

## Encrypted record layout

Exact binary sequence: magic ASCII `SVR1` (4), kind (1), reserved zero (3), project UUID (16), file UUID (16), revision UUID (16), wrapped-key length uint16 little-endian (2), plaintext/ciphertext length uint32 little-endian (4), nonce (12), wrapped key (256), GCM tag (16), ciphertext (declared length). UUID bytes are RFC/network order (`Guid.TryWriteBytes(..., bigEndian:true, ...)`). Fixed prefix through nonce is 74 bytes, followed by wrapped key, tag, ciphertext. Exact record size = 346 + ciphertext length. Check all lengths/reserved bytes/IDs/kind/maxima/exact overall size before key unwrap. Context must match caller expectation, not be accepted solely from untrusted record header.

New random 32-byte data key **for every record**, random 12-byte nonce, AES-256-GCM 16-byte tag. Wrap with TPM protector. AAD = fixed prefix (74 bytes) concatenated with wrapped key (256 bytes). This binds kind, identities, revision, lengths, nonce, and wrapped key. Maximum document plaintext 262144 bytes; maximum manifest 1048576 bytes; ciphertext lengths are identical. Zero fresh/unwrapped data keys and failed decrypt buffers in finally paths. Exceptions/logs must not contain plaintext, filenames or keys. Fresh per-record data keys avoid key/nonce reuse across retries; do not add key reuse optimization without format review.

## Project store and atomic commits

Use opaque GUID blob filenames. `HEAD` holds only the opaque committed manifest ID; it is not a plaintext source/name index. Manifest revision ID equals that ID, manifest context binds project and empty file ID. Encrypted manifest stores schema version, snapshot ID and a bounded list of entries (file ID, logical basename, document revision/blob ID, byte length). Each document blob uses its file/revision context. No plaintext temporary files, source logs or filenames on disk. Reject duplicate entries/unknown properties/unsupported versions and invalid counts/IDs before trusting manifest contents.

Every operation obtains an exclusive project lock with bounded timeout (cross-process FileShare.None lock). Fail closed if active state is malformed, missing referenced committed blobs, wrong context or tampered. Do not silently recover from tampering by selecting stale state. Check root/project/files for symlinks/reparse points and require trusted service-owned directory ancestors; this is defense in depth and cannot defeat an administrator or malicious writer racing in a writable root. Do not claim secure deletion.

Commit: lock; read/authenticate current snapshot; compare expected snapshot (throw dedicated conflict error); write fresh encrypted document blob with CreateNew; flush; write fresh encrypted manifest blob with CreateNew; flush; publish new opaque HEAD through a same-directory temporary pointer and atomic replace/rename, preserving previous pointer if supported. A failed commit may leave encrypted orphan blobs but must preserve complete old or complete new HEAD state. No automatic orphan deletion needed initially. Subsequent reads verify actual referenced record context/length. Initialize must not overwrite any existing state. HEAD corruption and missing committed files fail explicitly. A previous pointer is for explicit administrator-reviewed recovery, never an automatic fallback after integrity failure.

Fault injection hooks may be internal/friend-test only at pre/post document, manifest, and HEAD publish points. Test crash interruption, retries and concurrency with synthetic fixtures. Power-loss durability and OS-specific rename behavior require separate lab evidence; ordinary unit fault injection is not proof of power-loss safety.

## Integration boundaries

Production TPM adapter must explicitly select hardware provider, reject exportable/nonpersisted/wrong-policy keys and never create keys during an ordinary read. The service must not expose this store until actual deployment gates pass. An explicit synthetic developer demo may exercise a test-only protector from the test project; no runtime `--software-key`/environment fallback is permitted in any production executable.

## Manifest schema 2 (owner-approved change, 2026-10-17)

The record layout above is unchanged. The manifest JSON `version` is now `2`, and each file entry gains a required `recordSha256`: 64 lowercase hex characters, the SHA-256 of that document's complete encrypted record file. Readers reject version 1 and any other version. Because the manifest is itself an authenticated record, these hashes bind every committed document record. Readers must verify each referenced record's exact size and hash on every operation. They may skip decrypting a record unless its content is requested.

A store instance may cache its last authenticated manifest in memory (no document plaintext or keys). It may reuse that cache only when `HEAD` names the same snapshot id and the manifest record file's SHA-256 is unchanged. Record-format changes, including any key hierarchy, still require separate review. See `docs/store-performance-design.md`.

## Addition: folder paths and batch commits (2026-10-18)

Logical names may now be relative paths of `/`-separated segments: at most 32 segments and 260 characters, each segment a valid basename under the earlier rules. No path may be a folder prefix of another file (case-insensitive). Readers reject manifests that break this.

`RevisionStore.CommitMany` writes many documents and one manifest in a single all-or-nothing commit. The record format and manifest schema 2 are unchanged.

## Addition: project deletion (owner request, 2026-10-18)

The record format, manifest schema and store layout are unchanged. Only the plaintext schema of the encrypted catalog index document `projects.json` changes:

- **Version 2:** `{version: 2, projects: [{projectId, name}], deleted: [projectId]}`. `deleted` lists tombstones: projects removed from the catalog whose directories may still exist.
- **Readers** accept versions 1 and 2. An id may appear only once across `projects` and `deleted`. Writers always write version 2, so a version 1 vault upgrades on its next change.
- **Verification:** every listed project's directory must exist. A directory under `projects\` must belong to a listed project or a tombstone; anything else fails closed as a layout failure, as before.
- **Delete order:** (1) the index moves the project to `deleted`; (2) its directory is removed without following links; (3) the tombstone is cleared. A crash or a file in use between the steps leaves a tombstone, and the service retries step 2 at startup.
