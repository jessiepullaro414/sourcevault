# Store performance and service hookup — design (proposed, not built)

Status: Phase A approved 2026-10-17 (decisions at the end); implementation in progress. Phase B deferred.

## Problem, measured

Every `RevisionStore` operation calls `ReadCurrent()`. That method decrypts the manifest and then **decrypts every committed document**, discarding the plaintext, only to prove that nothing was tampered with. Each decrypt is a TPM RSA unwrap of that record's data key.

Measured on this machine's firmware TPM 2.0 with lab key `SourceVault.Lab.T02` and 30 synthetic files of about 2 KB:

| Operation | Time | TPM unwraps | Projected at 1,000 files |
|---|---|---|---|
| List | 1.36 s | 31 (N+1) | ~45 s |
| Open one file | 1.45 s | 32 (N+2) | ~45 s |
| Commit one edit | 1.35 s | 31 (N+1) | ~45 s |
| Read every file (build) | 42.6 s | 960 (N²+N) | ~12 hours |

One unwrap costs about 45 ms. Public-key wraps cost about 0.1 ms, and AES-GCM is negligible. The TPM processes commands one at a time, so parallelism does not help.

## Phase A — remove unnecessary unwraps (recommended now)

Neither the record format nor the "fresh TPM-wrapped key per record" rule changes. Only the manifest schema and the read path change.

**A1. Manifest v2 carries a hash of each document record.** Each manifest entry gains `recordSha256`: the SHA-256 of the complete encrypted document record file, computed at commit. The manifest itself is AES-GCM-authenticated under a TPM-wrapped key, so these hashes are trustworthy. `ReadCurrent()` then checks each referenced record by exact size (`346 + byteLength`) and hash, **without decrypting it**. List still fails closed on missing, truncated, altered, swapped or older-revision files. The only coverage lost at List time is a record that is byte-identical to what was committed but no longer decrypts (for example, a different TPM key). Open still catches that, fail-closed.

**A2. Cache of the authenticated manifest, per store instance.** After decrypting manifest X, the store keeps the snapshot id, the SHA-256 of the manifest record file, and the parsed snapshot. On the next operation it reads `HEAD`. If the id is unchanged and the manifest file still hashes the same, it reuses the parsed snapshot; otherwise it decrypts again. Commit seeds the cache with the manifest it just wrote. A commit by another instance or process changes `HEAD`, which forces a miss. The cache holds ids, logical names and lengths only: no document text and no keys. It lives in memory and is cleared when the store is disposed.

**A3. Open decrypts only the requested file:** hash check, then one unwrap.

**A4. Bulk read for builds.** `ReadSnapshot(snapshotId)` authenticates the manifest once and decrypts each document once, yielding them one at a time. That is N unwraps instead of N²+N.

| Operation | Today | Phase A, first call after start | Phase A, warm |
|---|---|---|---|
| List | N+1 | 1 + N hashes | 0 + manifest hash |
| Open one file | N+2 | 2 | 1 (~45 ms) |
| Commit | N+1 | 1 | 0 (wraps are ~0.1 ms) |
| Read every file | N²+N | N+1 | N (1,000 files ≈ 45 s) |

Hashing cost: at most 1,024 files × 256 KiB = 256 MiB per cold List. It is typically far less, and the warm path hashes only the manifest.

**Format and compatibility.** Manifest `version` becomes 2, and version 1 is rejected. All existing stores are synthetic test fixtures, so no migration is needed. The contract gets a reviewed v2 section in `contracts/storage-v1.md`. `docs/record-codec.md` is unchanged.

**Tests.**
- Existing tamper and fail-closed cases now fail at List, through hash checks.
- New cases: a replaced manifest file under the same name invalidates the cache; a commit by a second instance invalidates the first instance's cache; `ReadSnapshot` returns exactly the committed set.
- Exact unwrap counts per operation, using a counting test protector, so regressions show up deterministically.
- The TPM benchmark above is rerun and recorded.

## Phase B — key hierarchy for builds (decide later)

Even after Phase A, reading a whole 1,000-file project costs about 45 s of TPM time. A two-level hierarchy removes that cost: one project key is TPM-wrapped, and each record's data key is wrapped by the project key in software. That requires a **record-format change**. It also means a symmetric key that can decrypt the whole project lives in service memory while the service runs. Today, every decrypt requires asking the TPM.

A hierarchy also simplifies key rotation and any future recovery envelope, because rewrapping one project key replaces rewrapping every record. **Recommendation:** defer until the build broker exists and real project sizes are known, then review it as its own format change.

## Service hookup (after Phase A)

1. **Layout.** One store per project at `vault\projects\<projectId>\`; project ids are opaque. A project needs a human name, so an encrypted project index record is required. That is a small new record kind, designed with this step.
2. **Startup.** After the vault-root check and key open, the service opens and Lists each project, at one unwrap per project. Decision needed: if one project fails authentication, does the whole service refuse to reach RUNNING (strict, simple), or does only that project become unavailable?
3. **Still no document endpoints.** Exposing List/Open/ApplyEdits over the named pipe is the next milestone. It reuses the IPC spike's DACL, peer checks and capabilities, and it needs the protocol additions for creating projects and files.

## Decisions (owner, 2026-10-17)

1. **Phase A approved** as described: manifest v2 with record hashes, the manifest cache, and the bulk read.
2. **A failed project at startup refuses the whole service.** It never reaches RUNNING until an administrator reviews the project.
3. **Phase B deferred** until the build broker exists and real project sizes are known.
