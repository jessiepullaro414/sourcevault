# Design: the vault holds any file (proposal, 2026-10-19; needs coordinator review)

**Owner decision (2026-10-19):** the vault must hold **any file a user adds**: binary files (icons, bitmaps, `.lib`/`.dll`), other encodings (UTF-16 `.rc` files, Windows code pages), larger files and more files per project. In the owner's words: "I can't control what someone might upload". Real C++/MFC projects also can't build without these files (`docs/cpp-build-design.md`, section 5, on branch `claude/hopeful-pasteur-d204a1`).

Status: **design only. Nothing here is built.** It changes the record format, the manifest schema, the package format and the protocol. AGENTS.md requires the coordinator's review for all of these. Synthetic inputs only. The decisions it needs are at the end.

**Unchanged:**
- the TPM vault key, the import key and sender keys;
- no software fallback and no recovery;
- the pipe's security (DACL, peer checks, capabilities, session expiry);
- the catalog index (`projects.json`) and the build-log store's contents;
- the 1 MiB frame limit.

## 1. Summary

| Area | Today | Proposed |
|---|---|---|
| Content | strict UTF-8 text | any bytes, stored exactly. The store labels each revision `text` or `binary` (section 2.1) |
| Per file | 256 KiB | **1 GiB** |
| Per project (live files) | 1,024 files | **25,000 files** and **8 GiB** in total |
| Record | `SVR1`: one AES-GCM block and one TPM-wrapped key per record | `SVR2`: 512 KiB segments, streamed. The data key is wrapped by a **project key**, and only the project key is TPM-wrapped (section 2.3) |
| Manifest | schema 3, at most 1 MiB | schema 4: `kind`, `sha256`, 64-bit lengths, a project-key reference; at most 32 MiB |
| Protocol | content inline as `contentBase64` (≤ 256 KiB) | inline content unchanged; new **uploads in parts** and **text reads in parts**; `RevertFile`, `VerifyProject`; new code `NotText` |
| Editor | every file opens in Monaco | text up to 1 MiB opens in Monaco. Other files show an info panel: rename, delete, replace from disk, history, revert |
| Importer and Pack | text only, skips the rest | every file, apart from the folder exclusions and structural limits; package format v2, spooled and streamed |
| Builds | whole project in memory, at most 1,024 files of 256 KiB | the tar is byte-exact already. The build pipe and the tar are **streamed**, with the new limits |

## 2. Storage

### 2.1 Content is bytes; how the store tells text from binary

- **The store holds bytes exactly as given.** It never converts encodings or line endings. A UTF-16 `.rc` file or a Windows-1252 source comes out of a build byte-identical to what went in.
- **The store decides the kind, never the client.** At write time it streams the content through a stateful strict UTF-8 decoder and a NUL check:
  - `text`: valid strict UTF-8 (a UTF-8 BOM is allowed) with no NUL byte;
  - `binary`: everything else.

  This is the importer's existing rule (`ImportPlan.IsText`). The NUL check is what catches UTF-16: ASCII text in UTF-16 is full of zero bytes, which are valid UTF-8.
- **Where the kind is recorded.** It goes in the authenticated manifest entry and in the history entry for that revision, so it is decided once per revision and trusted later. It is not in the record header: the record format carries bytes only.
- **Code pages can't be detected reliably,** and UTF-16 without a BOM can't either. They are `binary` for the vault and the editor, and they build like any other file. Editing UTF-16 files is a possible later kind (`utf16le`, decision O2); the enum leaves room for it.
- **Empty files are `text`.**

### 2.2 Record format `SVR2` (new cryptographic format)

Why a new format is needed:
- `SVR1` encrypts a record as **one** AES-GCM block. Reading or writing it needs the whole plaintext in memory, and its length field and limits assume small documents.
- Every `SVR1` record has its own RSA-wrapped key: **one TPM unwrap (~45 ms) per record read**.

`SVR2` is a segmented ("STREAM"-style) construction. It is the same construction as the reviewed package format v1 (`docs/package-import-design.md`), applied per record. It uses no new primitives: AES-256-GCM, RSA-OAEP-SHA256 and SHA-256.

**Fixed prefix, 68 bytes.** Integers are little-endian; UUIDs are in network order, as in `SVR1`.

| Field | Bytes |
|---|---|
| magic `SVR2` | 4 |
| kind: Document = 1, Manifest = 2, ProjectKey = 3 | 1 |
| wrap mode: 1 = TPM (RSA-OAEP-SHA256), 2 = project key | 1 |
| reserved, zero | 2 |
| project UUID | 16 |
| file UUID (empty for Manifest and ProjectKey) | 16 |
| revision UUID (record id) | 16 |
| plaintext length, uint64 | 8 |
| segment size, uint32, must equal 524,288 | 4 |

**Then the wrapped data key:**
- **Wrap mode 1:** the 32-byte data key, RSA-OAEP-SHA256-wrapped by the TPM key (256 bytes), as in `SVR1`.
- **Wrap mode 2:** project key id (16) ‖ random nonce (12) ‖ AES-256-GCM(project key, data key; AAD = the 68-byte prefix) (32 + 16 tag) = 76 bytes.

**Then the segments:**
- The plaintext is split into segments of 512 KiB; the last one may be shorter. An empty plaintext is one empty segment, so a truncated record can always be detected.
- Segment *i* is AES-256-GCM under the record's data key:
  - nonce = 4 zero bytes ‖ *i* as uint64;
  - AAD = SHA-256(prefix ‖ wrapped key) ‖ *i* as uint64 ‖ a last-segment flag (1 byte);
  - followed by its 16-byte tag.
- **Exact size** = 68 + wrapped-key length + plaintext length + 16 × segment count. It is checked before any unwrap, as in `SVR1`.

**Rules kept from `SVR1`:**
- a **fresh random data key for every record**, never reused;
- the caller's expected context (project, file, revision, kind) must match the header;
- data keys and failed plaintext are zeroed in `finally`;
- exceptions are generic.

The index nonce is safe because each key encrypts exactly one record. The random nonce under the project key is safe at this scale: a project wraps a few hundred thousand keys over its life, far below the 2³² limit for random 96-bit nonces under one key.

**Streaming.** A reader decrypts one segment at a time, so memory is one segment however large the file is. Every segment is authenticated before it is used. Truncation, reordering, splicing from another record, and dropping the last segment are all refused. The manifest's SHA-256 of the whole record file is checked at the end of the stream (section 2.5). A streaming consumer treats what it received as provisional until then, and aborts on a mismatch (the build fails, the read part is refused).

**Readers accept `SVR1` and `SVR2`; writers write only `SVR2`.** `SVR1` records keep their existing checks, including strict UTF-8.

### 2.3 Project key: the cost of TPM-wrapped keys at this scale

This is Phase B of `docs/store-performance-design.md`. It was deferred "until the build broker exists and real project sizes are known". Both are now known: the build service exists, and the owner expects thousands of files.

**Cost of one TPM unwrap per record (today's rule), at about 45 ms each:**

| Operation | 1,024 files (today's limit) | 25,000 files |
|---|---|---|
| Open one file | 45 ms | 45 ms; a 1 GiB file would be one record, which `SVR1` can't hold |
| Build (`ReadSnapshot`) | ~46 s | **~19 min** |
| Search all files | ~46 s | **~19 min** |
| Import (wraps only) | 0.1 s | 2.5 s |

The TPM processes one command at a time, so parallelism doesn't help. Without a key hierarchy, a large project can't be built or searched in reasonable time.

**Proposal:**
- **One project key per project.** It is a random 256-bit AES key in a `ProjectKey` record (kind 3, wrap mode 1, so TPM-wrapped). Its id and record SHA-256 are in the manifest.
- **Content and history records** use wrap mode 2: their fresh data key is wrapped by the project key in software.
- **The manifest and the project-key record** stay TPM-wrapped (mode 1). So reading a manifest never depends on the project key.
- **Lifetime of the project key in memory:** it is unwrapped **at most once per store operation**, under the project lock, and zeroed in `finally` when the operation ends. It is **never cached between operations**, logged, or sent anywhere. One exception: an upload holds only the **data key of the record it is writing** until the upload ends (section 3.2); the project key is zeroed right after wrapping.
- **Build-log stores** are ordinary `RevisionStore`s, so they get their own project key the same way.

**Unwraps per operation, with the project key:**

| Operation | Cold store instance | Warm |
|---|---|---|
| List | 1 (manifest) | 0 |
| Open / read parts | 2 | 1 per request |
| Commit with writes, restores or reverts (any size, any number of files; these write history records) | 2 | 1 |
| Rename, delete | 1 | 0 |
| Build, search, `VerifyProject` | 2, plus AES-GCM over the project's bytes | 1, plus the same |

So a 25,000-file build costs one unwrap plus AES-GCM decryption. AES-GCM runs at more than a gigabyte per second on current x64 CPUs; this is an estimate, to be measured. Disk reads and the Docker copy then dominate.

**What this gives up (be aware):** during an operation that reads content, the service's memory holds a key that decrypts **every** content record of that project. Today every record needs its own TPM request. Anyone who can read the service's memory is an administrator, whom the design already trusts. But a memory dump taken during a build or search exposes the whole project, not one file. This is the trade-off Phase B named, and it is decision C2.

### 2.4 Manifest schema 4 and history records version 2

**Manifest schema 4** is schema 3 plus the following:
- **`projectKeyId` and `projectKeySha256`** at the top level, both required. Every schema 4 manifest is written by a commit that has a project key (section 7).
- **Each live and deleted entry gains:**
  - `kind` (`text` | `binary`);
  - `sha256`: the plaintext SHA-256, computed by the service while it encrypted; null for entries carried over from schema 3;
  - `byteLength`, now up to 2³⁰ (and stored as a 64-bit value in the API: `StoreFile.ByteLength` becomes `long`).
- **Limits:** at most 25,000 live files and 512 deleted entries (unchanged). The manifest record is at most **32 MiB**. A typical entry is about 500 bytes, so 25,000 files take about 12.5 MB. The worst case, 260-character names of 3-byte characters, is about 1,250 bytes per entry, about 31.3 MB in all; that is why the limit is 32 MiB.
- **Readers** accept schemas 3 and 4; **writers** write 4. Validation stays as strict as now: no unknown or duplicate fields, ids unique, kinds from the list. The file/folder clash check must run in O(n log n) (sorted names), not pairwise.

**History record version 2:** each entry gains `kind` and `sha256`, and `byteLength` goes up to 2³⁰. A new action, `reverted`, is added (section 3). Readers accept versions 1 and 2. A version 1 entry reads as `kind: text, sha256: null`.

### 2.5 What every operation verifies

**Today:** every operation reads and hashes **every** referenced document record, even a List (`ReadCurrent`). At 1,024 × 256 KiB that is at most 256 MiB. At 25,000 files and 8 GiB it would mean seconds to minutes per List, Open or Save. The same is true of the per-record reparse-point check, at about 20–50 µs per file.

**Proposed:**
- **Every operation:**
  - one directory enumeration of the project folder, which returns each entry's name, size and attributes in bulk;
  - every referenced record (live, deleted, history, project key) must be present, have its **exact expected size**, and not be a reparse point;
  - the manifest record is hashed as now, because the manifest cache depends on it.
- **Every content read** (Open, read parts, `OpenRevision`, build, search, history): the record's SHA-256 is checked against the manifest, and the record is decrypted.
- **`VerifyProject`** (new operation, and run by the importer after an import): decrypts every live record of a project inside the service and compares the plaintext SHA-256 with the manifest. It costs the same as a build.

**What changes:** a record altered without changing its size is now caught when it is read, not by the next List. It is still caught before any of its content is used. Decision C4. The alternative is to keep full hashing, at a cost that grows with the project's total size.

### 2.6 Disk: staging, superseded manifests, free space

- **Staging folder.** A new `staging\` folder in the vault root holds uploads and package spools while they are in progress, as ciphertext only. The root layout check accepts it, alongside `index`, `projects` and `builds`.
  - Each connection writes under a random folder name.
  - The service empties `staging\` at startup, removing leftovers from crashed runs.
  - A finished upload moves into the project folder with a same-volume rename, under the project lock.
- **Superseded manifests.** Every commit leaves its old manifest record behind, and nothing ever reads it again: history references revisions, and `PREVIOUS` names only one snapshot. At 12 MB per manifest, 1,000 saves would leave 12 GB.
  - **Proposed:** after `HEAD` is published and flushed, delete manifest records other than `HEAD` and `PREVIOUS`.
  - A failed delete is ignored and retried after the next commit.
  - This is ordinary deletion of ciphertext, not secure deletion. Decision C5.
- **Free space.** Uploads, commits and package imports are refused with `LimitExceeded` when the vault volume would drop below **4 GiB free**. The import or save fails cleanly instead of the disk filling up mid-commit.
- **History keeps everything**, as today (D2 of `files-history-builds-design.md`). Replacing a 500 MB library ten times keeps 5 GB on disk until the project is deleted. Decision O5.

### 2.7 Limits and their reasons

| Limit | Value | Reason |
|---|---|---|
| File size | 1 GiB | Big prebuilt static libraries and resource blobs in C++ trees are commonly hundreds of MB. 1 GiB leaves headroom, keeps an upload around a minute, and every segment index and offset stays small. Anything bigger is skipped and listed by the importer and Pack |
| Live files per project | 25,000 | Vendored libraries run to thousands of files. 25,000 keeps the single manifest under 32 MiB in the worst case (section 2.4), so a commit rewrites it in tens of milliseconds (estimate, to be measured) |
| Live bytes per project | 8 GiB | Every build copies the whole snapshot into a container, and every search and verify reads it. 8 GiB keeps those around a minute (estimate, to be measured), and bounds a package |
| Path | 260 characters, 32 segments (unchanged) | Windows tools in the build containers still meet `MAX_PATH`. Longer paths are skipped and listed (decision O7) |
| Deleted entries | 512 (unchanged) | Manifest size |
| Manifest record | 32 MiB | Worst case at 25,000 files; bounds the service's memory per cold operation |
| Segment | 512 KiB | Equal to an upload or read part, so one part is one segment, and memory per stream is one segment |
| Free space kept | 4 GiB | A failed write never fills the vault volume |

Lower internal limits are settable in tests only, so the boundaries can be tested without gigabytes of fixtures.

## 3. Protocol (v2 additions)

**Frames stay at most 1 MiB.** A frame carries at most 512 KiB of decoded content (about 683 KiB of base64), so every request and response stays well under the frame limit. Every operation also takes `capability`.

### 3.1 Inline operations: unchanged in shape

- **`CreateFile` and `ImportFiles`** accept **any bytes** up to 256 KiB per file, and the store sets the kind.
- **`ApplyEdits`** still requires strict UTF-8 text, because it is the editor saving text. Anything else is `InvalidRequest`.
- **`OpenDocument` and `OpenRevision`:**
  - a `binary` revision returns **`NotText`**;
  - a `text` revision over 256 KiB returns `LimitExceeded`, and the client reads it in parts (3.3).
- **`ListFiles`, `ListDeletedFiles` and `ListHistory`** entries gain `kind` and `sha256` (null for files carried over and not saved since).
- **`SearchProject`** gains `filesSkipped` in its reply.

The editor host, importer and lab client are installed together with the service, so they are updated in the same release.

### 3.2 Uploads in parts (content larger than 256 KiB, or from disk)

| Operation | Request body | Success body |
|---|---|---|
| BeginUpload | `projectId`, `fileId` (UUID to replace that file, or null for a new file), `length` (0 … 1 GiB), `sha256` (hex, of the plaintext) | `uploadId`, `fileId`, `partBytes` (524,288) |
| UploadPart | `uploadId`, `offset`, `dataBase64` (1 … 512 KiB decoded) | `received` |
| CommitUploads | `projectId`, `expectedSnapshotId`, `files[]`: `{uploadId, name}` (1 … 256; `name` is null for a replacement) | `snapshotId`, `files[]`: `{fileId, revisionId, kind}` |
| CancelUpload | `uploadId` | `{}` |

**BeginUpload** checks the project, the size limits and the free space, then:
- chooses the revision id, so the record's context is fixed;
- unwraps the project key once, wraps a fresh data key with it, and zeroes the project key;
- creates the record file in `staging\` with `CreateNew`.

**UploadPart:**
- parts arrive in order with no gaps, as for package parts;
- each completed segment is encrypted and written straight away;
- the service hashes the plaintext as it goes.

**When the last byte arrives:** the plaintext SHA-256 must equal `sha256`, otherwise `InvalidRequest` and the upload is discarded. The data key is then zeroed, and the upload is **staged**.

**CommitUploads** is one all-or-nothing commit, like `CommitChanges`:
- it re-checks each staged record's size and hash;
- moves the records into the project folder under the lock;
- writes the history entries and the manifest.

Its rules:
- An entry with a null upload `fileId` creates a file; a taken name is `AlreadyExists` (create-only, like `ImportFiles`).
- A replacement keeps the file id and name; the file must still exist (`NotFound`).
- A stale `expectedSnapshotId` is `RevisionConflict`. The staged uploads survive a conflict, so "keep mine" needs no second upload.

**Bounds:**
- one upload **receiving** at a time per connection;
- at most 256 **staged** uploads per connection, holding at most 8 GiB;
- an upload with no part for 2 minutes is discarded;
- closing the connection, `EndSession` or session expiry discards all of that connection's uploads and deletes their staging files.

**What the upload never sees:** no OS path, and no plaintext on disk. The service writes only ciphertext. The editor host and the importer read the source file themselves.

### 3.3 Reading text in parts

| Operation | Request body | Success body |
|---|---|---|
| ReadTextPart | `projectId`, `fileId`, `revisionId`, `offset` (a multiple of 524,288) | `length` (the whole file), `dataBase64` (that segment) |

- Only for `text` revisions of at most **1 MiB**, the editor's limit. A `binary` revision is `NotText`, and a larger one is `LimitExceeded`.
- The request decrypts exactly one segment: one project-key unwrap, no state between requests.
- **There is deliberately no operation that returns binary content or large files to a client.** Content the editor can't show doesn't leave the service. Its only way out is a build (decision O4).

### 3.4 Revert and verify

| Operation | Request body | Success body |
|---|---|---|
| RevertFile | `projectId`, `fileId`, `revisionId`, `expectedSnapshotId` | `snapshotId` |
| VerifyProject | `projectId` | `filesVerified`, `filesFailed` |

- **RevertFile** makes a revision from the file's own history current again. It adds a `reverted` history entry pointing at that revision record, and nothing is re-encrypted or sent to the client. It works for text and binary files. The editor uses it for binary files; text keeps its existing "Restore puts the old text in the editor" flow.
- **VerifyProject** is described in section 2.5. A failure reports a count only, never names, and the service then treats the project as failed, as at startup.

### 3.5 Error codes

- **New: `NotText`.** The operation needs text, and this revision is binary.
- **Reused:**
  - `LimitExceeded` covers size, count, staged and free-space limits;
  - `Unavailable` covers store and key failures;
  - `InvalidRequest` covers a wrong length or hash, a gap, or an unknown upload.

## 4. Editor

**Files the editor opens:** `text` revisions up to **1 MiB**.
- Up to 256 KiB, through `OpenDocument` and `ApplyEdits`, as now.
- From 256 KiB to 1 MiB, through `ReadTextPart` and an upload. The host does this transparently.
- **Bridge limits:** text up to 1 MiB, and a message up to 8 MiB of characters. JSON escaping can multiply text by six; the length is checked before parsing, as now.

**Other files** (binary, or text over 1 MiB) are listed in the explorer with a badge (*binary* or *large*) and their size. Choosing one shows an **info panel** instead of Monaco. It shows the name, kind, size, the last save (when, who) and the SHA-256. Its actions:
- **Rename, Delete, History**, as for text files;
- **Replace from disk…** The host shows a native *Open file* dialog. The host reads the chosen file and streams it with `BeginUpload` and `UploadPart`, then `CommitUploads`.
  - The renderer never receives the bytes or the path. It sends only `replaceFromDisk{projectId, fileId}`.
  - The same dialog gives **Add files from disk…** in a folder's **⋯** menu (decision O3).
- **No download, open-with or preview.** Clipboard, drag/drop and print stay blocked, as now. Image preview for icons and bitmaps would add an image decoder to the renderer's attack surface; it is out of this change (decision O8).

**History:**
- For a binary or large file, the list shows each version with its size, kind, SHA-256 and who saved it, and **no diff**.
- **Revert to this version** calls `RevertFile` after a confirmation.
- Text files keep the diff view and restore.
- A file whose kind changed between versions shows the diff only between text versions.

**Merge:**
- Text files keep the three-way merge.
- **When any of base, theirs or yours is binary or over 1 MiB,** the conflict banner instead offers:
  - **Keep mine:** recommit the staged upload against the current snapshot;
  - **Keep theirs:** cancel my upload.
- Either way, the other version stays in history.

**Search all files:** searches `text` files up to 16 MiB each. It skips binary files and larger text files, and reports *N files skipped (binary or too large)* from `filesSkipped`. Drafts are text, so the editor's own draft search is unchanged.

**Go to file and recent files:** include every file; a non-openable file opens its info panel.

## 5. Importer and SourceVault Pack

### 5.1 Scan rules (shared library)

Every file is taken, whatever its content. Skipped, and listed with a reason:
- **folder exclusions:** the default list (`.git`, `bin`, `obj`, …) and `--exclude`, unchanged;
- **links and junctions:** never followed, unchanged;
- **names the vault can't hold:** invalid segments, or more than 260 characters or 32 segments;
- **case duplicates;**
- **unreadable files;**
- **files over 1 GiB.**

The rules "not UTF-8", "contains NUL" and "over 256 KiB" are removed. Over 25,000 files or 8 GiB fails the whole plan with a message to exclude folders, as the 1,024-file limit does today.

The scan reads each file once to hash it. The import or pack reads it again, streamed, and fails if it changed in between, as now.

### 5.2 Folder import (`SourceVault.Import.exe`)

- Files up to 256 KiB still go in `ImportFiles` batches. Larger files go through uploads, committed by `CommitUploads` in batches of up to 256.
- **Verification** no longer decrypts files back through `OpenDocument`, which can't return binary content. Instead:
  - every file's `sha256` from `ListFiles` must equal the hash taken at scan time;
  - then the importer calls `VerifyProject`, so every record is decrypted once inside the service.
- An import is still a series of commits into a new project, as today. A failure part-way leaves a partial project, and the message says so.

### 5.3 Package format v2 (needs coordinator review)

**The construction is unchanged:** the 368-byte header, the RSA-OAEP-wrapped package key, 1 MiB AES-GCM chunks with index nonces and header-bound AAD, and the RSA-PSS signature over `SHA-256(header ‖ all chunks)`.

**What changes:**

| | v1 | v2 |
|---|---|---|
| header and plaintext `version` | 1 | 2 |
| files | ≤ 1,024 | ≤ 25,000 |
| per-file length (uint32) | ≤ 256 KiB | ≤ 1 GiB |
| content rule | strict UTF-8 | any bytes |
| total plaintext | ≤ ~260 MiB | ≤ 8 GiB plus the file table |

- **Pack** writes v2 only. **The service** accepts v1 and v2.
- **The plaintext layout already suits streaming:** the file table comes first, then the contents in table order.
- **Pack streams too.** It reads each file in parts while it encrypts, rather than holding the package in memory.

**The service no longer holds the package in memory** (today `BeginPackageImport` allocates the whole length):
1. **Upload:** each part is appended to a spool file in `staging\` (ciphertext), and the service hashes it as it arrives.
2. **Finish:** checks the header, recipient and registered sender, then verifies the signature against the running hash. Nothing is decrypted before this, as today.
3. **Decrypt:** unwraps the package key, then reads the spool again chunk by chunk.
   - Every chunk is GCM-checked, and the spool is re-hashed during this pass. A spool altered between the passes is refused.
   - The file table (at most about 26 MB) is parsed and checked first: names, counts, sizes.
   - Each file's content is streamed straight into a staged `SVR2` record, and its SHA-256 is checked.
4. **Commit:** a new project, then the staged records in commits of up to 1,024 files each, with the history action `imported`. A failure deletes the new project, as now, so nothing partial remains.

**Bounds:** one package import at a time across the service (unchanged), at most 8 GiB plus overhead on disk, and one 1 MiB chunk in memory.

## 6. Builds

**The tar copy itself is unaffected.** `BuildRunner.SourceArchive` writes PAX tar entries from bytes, so UTF-16, code-page and binary files reach `sourcePath` byte-identical. Files still arrive without the executable bit. Artifacts come only from `artifactPath`, so a vendored `.lib`/`.dll` in the source doesn't change the artifact rules.

**What around the tar must change:**
- **The vault service** collects every decrypted file into a list before sending (`BuildCoordinator`): the whole project in memory.
- **The build pipe** sends each file as one `{name, contentBase64}` frame, so files over about 750 KiB can't fit in a 1 MiB frame. The build service also enforces 1,024 files and 256 KiB per file (`BuildChannel`).
- **The build service** builds the whole tar in a `MemoryStream` before calling `PutArchive`.

**Proposed build pipe, version 2** (private; both services are installed together):
1. `{op: "build", target, projectId, buildNumber, fileCount, totalBytes}`.
2. File-table frames: up to 1,000 `{name, length}` per frame, `fileCount` entries in all. The build service validates every name, duplicate and limit **before** the copy starts.
3. Data frames, `{dataBase64}` of at most 512 KiB, carrying the files' bytes in table order.

**Streaming:**
- The vault service reads each record segment by segment (`ReadSnapshot` streams) and writes the frames.
- The build service writes the tar **as a stream** into the `PutArchive` request body (chunked HTTP, over the named pipe or TLS socket as now).
- Memory on both sides is a few segments, not the project.

**A record that fails its end-of-stream check** aborts the copy. The build ends `failed` with reason `SourceUnavailable`, and `sourcePath` is emptied as after any build.

**Limits** are the vault's: 25,000 files, 1 GiB per file, 8 GiB in all. A large project's copy takes longer, so the copy time is not counted in `timeoutSeconds`. It has its own fixed bound of 30 minutes.

## 7. Migration of existing vaults

**No re-encryption pass, and no downtime beyond the upgrade.** Upgrading is lazy and one-way.
- **Reading:** a schema 3 manifest and its `SVR1` records read as before. Every schema 3 entry reads as `kind: text` (it passed strict UTF-8 at write time) and `sha256: null`.
- **New stores** (`Initialize`) get a project key in their first manifest.
- **The first commit on a schema 3 store** gets one in the same commit:
  1. it creates the project key record (one TPM wrap) and flushes it;
  2. writes the new `SVR2` records;
  3. writes a schema 4 manifest, then publishes `HEAD`.

  The crash order is the same as for documents: a crash leaves an unreferenced key record or the complete old state.
- **`BeginUpload` on a schema 3 store** first makes a key-only commit: the key record and a schema 4 manifest, with no file changes. An upload's data key must be wrapped by the key its commit will reference. That commit gives the project a new snapshot id, so another client's next save conflicts once and retries, as after any commit.
- **Mixed projects:** a migrated project mixes `SVR1` and `SVR2` records. Old records still cost one TPM unwrap each when read, until a file is saved again. Old projects had at most 1,024 files, so a build of an untouched migrated project costs at most about 46 s of unwraps, as today. `sha256` for those entries fills in as files are saved. `VerifyProject` decrypts them, but doesn't fill it in, because the manifest isn't rewritten by a read.
- **Also migrated:** build-log stores, the same way. The catalog index and package v1 files need nothing.
- **Downgrade:** an older service refuses a schema 4 manifest, and because one failed project refuses the whole service, it won't start. So after the upgrade an older version can't be reinstalled over the vault. The deployment kit must carry the new version (decision O6).
- **Existing data:** all existing stores are synthetic, but the migration is designed and tested as if they weren't.

## 8. Memory and frame bounds, in one place

| Path | Bound |
|---|---|
| Pipe frame | 1 MiB (unchanged) |
| Content per frame | 256 KiB inline; 512 KiB per upload or read part |
| Record stream | one 512 KiB segment in memory |
| Manifest | ≤ 32 MiB plaintext, plus its parsed form, per cold operation |
| Uploads | 1 receiving per connection; ≤ 256 staged and ≤ 8 GiB per connection; 4 connections; 2-minute idle discard |
| Package import | 1 at a time; spooled to disk (ciphertext); one 1 MiB chunk in memory; file table ≤ ~26 MB |
| Build copy | file table ≤ 25,000 entries in ≤ 1 MiB frames; data in 512 KiB parts; tar streamed |
| Search | one text file ≤ 16 MiB in memory at a time; ≤ 500 matches |
| Editor bridge | text ≤ 1 MiB; message ≤ 8 MiB characters |
| Disk | per file 1 GiB; per project 8 GiB live; 4 GiB of free space kept |

## 9. What this does and doesn't protect (be aware)

- **The project key** (section 2.3): during a content-reading operation, one key in service memory decrypts the whole project.
- **Binary files are opaque to the editor's safeguards.** A user can upload anything, including an archive of other source. The vault stores it encrypted like any file. Only a build can get it out, and builds are already an export path (`files-history-builds-design.md`, section 4).
- **Prebuilt `.lib`/`.dll` files in a project run code at build time** if the build loads them. That already applied to build scripts. Treat vendored binaries like build scripts.
- **The editor host reads files the operator picks from disk.** Those files are plaintext on that disk already. SourceVault doesn't delete or protect the originals, as with the importer.
- **No new claims.** There is no RAM-only processing (the editor and host hold text, as now), no secure deletion (superseded manifests and discarded uploads are plain deletes of ciphertext), and no administrator resistance.

## 10. Tests (synthetic only)

All fixtures are generated in the test workspace: random bytes, synthetic UTF-16 `.rc`-style files, and Windows-1252 byte sequences. No real source, and no download. Gigabyte-scale cases use lowered internal limits, plus one optional long run on generated data.

- **`SVR2` codec:**
  - round trips at 0, 1, segment − 1, segment, segment + 1, many segments, and the maximum (generated on the fly);
  - the exact byte layout;
  - refusal of every truncation at each segment boundary, reordered, duplicated or spliced segments (from another record), a flipped last flag, and mutated header, wrapped key, tag or ciphertext;
  - wrong context, wrap modes 1 and 2, a wrong project key id;
  - zeroing on success and failure;
  - memory per stream bounded (allocation counter);
  - `SVR1` still read.
- **Store:**
  - kind classification for UTF-8, UTF-8 with BOM, UTF-16 LE/BE with and without BOM, NUL, invalid UTF-8, code-page bytes and empty files;
  - schema 3 → 4 migration from a fixture made by the current code, with mixed `SVR1` and `SVR2` snapshots;
  - exact unwrap counts with the counting protector: build = 1 warm and 2 cold for `SVR2`, and N for `SVR1` leftovers;
  - limits at their boundaries;
  - size-only verification at List, and same-size tampering caught at Open, build, search and `VerifyProject`;
  - pruning of superseded manifests;
  - startup cleanup of `staging\`;
  - the random-kill test extended to project-key creation, staged-record moves and manifest pruning.
- **Protocol** (in process, over real pipes):
  - the upload lifecycle: order, gaps, overrun, wrong length or hash, cancel, connection close, session expiry, the idle discard, staged limits, uploads on several connections;
  - `CommitUploads`: create-only, replace, conflict, then "keep mine" without re-upload;
  - `NotText`; `ReadTextPart` bounds;
  - `RevertFile` for text and binary files;
  - `VerifyProject`;
  - maximum-size parts staying under 1 MiB frames;
  - hostile bodies.
- **Editor:**
  - bridge-policy hostile tests for the new messages and the 8 MiB bound;
  - renderer node tests: info panel, badges, binary history, keep mine/theirs, search skipped count;
  - review in the browser against the fake host.

  Native file-dialog checks are lab items.
- **Importer and Pack:**
  - scans now include binary, UTF-16 and code-page files, and still skip links, excluded folders, bad names, case duplicates and files over 1 GiB;
  - the package v2 codec: every v1 test, multi-chunk files, a file changing between the two reads, and v1 packages still importing;
  - spool tampering between the hash and decrypt passes is refused;
  - an import with several commits is verified by hash and `VerifyProject`.
- **Builds:**
  - against the fake engine: binary and UTF-16 bytes arrive byte-identical in the tar;
  - the streamed `PutArchive` and file-table frame limits;
  - an abort mid-stream ends as `SourceUnavailable`, with cleanup.
- **Lab (owner, unverified until run):**
  - the TPM benchmark at 25,000 synthetic files and several GiB: List, Open, Commit, build, search and `VerifyProject`, recorded in `revision-store.md`;
  - a real Docker copy of a large synthetic project to both targets;
  - the `cpp-hello` sample with a synthetic UTF-16 `.rc` file and a binary `.ico`.

## 11. Build order (after approval)

Each step is tested and committed before the next:
1. The `SVR2` codec and the project key: storage library only.
2. Manifest schema 4, history v2, the new verification, staging, manifest pruning, migration.
3. Protocol: uploads, `ReadTextPart`, `RevertFile`, `VerifyProject`, `NotText`.
4. The build pipe v2, streaming.
5. The scan library, the importer, package v2, Pack and the Pack window.
6. Editor: info panel, replace and add from disk, binary history and merge, search counts.
7. Documentation (`storage-v1.md`, `protocol-v2.md`, `user-guide.md`, the lab checklist) and the lab runs.

## 12. Decisions needed

### Coordinator (format and security review, per AGENTS.md)

- **C1. Record format `SVR2`:** segmented AES-256-GCM with index nonces, header-bound AAD and a last-segment flag (section 2.2). Approve, or ask for changes.
- **C2. Project key hierarchy:**
  - a TPM-wrapped project key wraps every content record's data key;
  - it is unwrapped once per operation and never cached between operations;
  - the manifest stays TPM-wrapped.

  This trades "every record needs the TPM" for builds and searches that scale (section 2.3). Without it, the 25,000-file limit isn't practical; the alternative is to keep per-record TPM keys and a much lower file limit.
- **C3. Manifest schema 4 and history v2,** including the plaintext `sha256` in the manifest and its return by `ListFiles`.
- **C4. Verification:** check existence and exact size on every operation, and the hash on every read, plus `VerifyProject` (recommended). The alternative is to keep full hashing every time, at a cost that grows with project size.
- **C5. Pruning superseded manifests** after a commit, keeping `HEAD` and `PREVIOUS` (recommended). The alternative is to keep them all, which costs disk on every save.
- **C6. The `staging\` folder** in the vault root, and its startup cleanup.
- **C7. Package format v2** and the spooled two-pass import.
- **C8. Build pipe v2** with streaming.
- **C9. The protocol additions** and the new `NotText` code.

### Owner

- **O1. Limits:** 1 GiB per file, 25,000 files and 8 GiB per project, 1 MiB for the editor, 16 MiB per searched file, 4 GiB of free space kept. Accept, or name other values. Bigger values raise the build and search times and the manifest size.
- **O2. UTF-16 and code-page files:** stored and built exactly, but not opened in the editor (recommended for this change). Or, later, editing for UTF-16 files with a BOM, which round-trip safely.
- **O3. Adding and replacing files from disk in the editor**, through a native file dialog in the host. This is a new way into the vault besides the importer. Yes (recommended: it is the only way to update a binary file short of a new import) or no.
- **O4. No way to download binary or large files from the vault** (recommended). Their only way out is a build. Or allow an export, which would be a new export path.
- **O5. History for large binary files:** keep every version, as for text (recommended, consistent with D2), with the free-space guard. Or keep only the last *N* versions of binary files, which would be the first deletion of history.
- **O6. The upgrade is one-way:** after it, older SourceVault versions can't open the vault.
- **O7. Path limits stay** at 260 characters and 32 segments. Longer paths are skipped and listed.
- **O8. No image preview** for icons and bitmaps in this change.
