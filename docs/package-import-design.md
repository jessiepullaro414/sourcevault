# Design: importing encrypted projects from another computer (owner-approved 2026-10-19; built, in-process tested)

Owner request (2026-10-19): code that lives as ordinary files on another computer must arrive at the vault PC **encrypted**. A small separate program on the other computer encrypts it, and the vault PC imports it.

Owner answers so far:
- **Source:** plain files on another computer, not another vault.
- **Contents:** the current files only. History starts fresh on the vault PC, with the action `imported`.
- **Copy:** the other computer keeps its files.

## The idea in one paragraph

The vault PC has an **import key** in its TPM, readable only by the vault service. Its public half is a small, public **recipient file**, which you carry to the other computer. A new program there, **SourceVault Pack**, encrypts a project folder to that recipient file and writes one **package file** (`.svpkg`). You bring the package back in any way you like, and `SourceVault.Import.exe --package` hands it to the vault service. The service decrypts it in memory and creates a new project. Plaintext never touches the vault PC's disk, and the importer program never sees it. A stolen package is useless without this PC's TPM.

```
other computer                                   vault PC
  project folder ─► SourceVault.Pack ─► .svpkg ───────► SourceVault.Import.exe ─► service (TPM import key) ─► new project
        ▲                                 (encrypted; USB, network share, email: all fine)
  recipient file (public) ◄──────────── exported once by the administrator
```

## Components

1. **Import key** (vault PC). A second decrypt-only, non-exportable RSA-2048 key in the TPM, under `NT SERVICE\SourceVault`, created by setup the same way as the vault key: the existing provisioning code with a different key name. **Kept separate from the vault key** (decision D1), so the key that protects the vault never decrypts data an outsider supplied.
2. **Recipient file** (`SourceVault-recipient-<computer>.json`). The import key's public key, its SHA-256 **fingerprint**, the computer name and a date. It holds no secrets.
   - An administrator exports it: `Setup-SourceVault.ps1 -ExportRecipient <path>`.
   - Both programs show the fingerprint. Compare it on both screens once, so a swapped recipient file is caught.
3. **SourceVault Pack** (`SourceVault.Pack.exe`, other computer).
   - A single self-contained program: no install and no admin rights needed. It uses .NET's built-in libraries only.
   - `SourceVault.Pack.exe <folder> --to <recipient file> --out <file.svpkg> [--exclude <name>]... [--dry-run]`
   - It uses **the same scan rules as the importer**: UTF-8 text files up to 256 KiB, at most 1,024 files, links never followed, and `.git`, `bin`, `obj` and the rest skipped. It lists what it skips, shows the recipient's fingerprint and computer name, and asks you to confirm before writing.
   - It writes no plaintext temporary files.
4. **Package file** (`.svpkg`, format below). It is ciphertext, so it can travel any way you like.
5. **Import** (vault PC). `SourceVault.Import.exe --package <file.svpkg> --project "<new name>"` streams the encrypted bytes to the service over the existing pipe. The service then:
   - unwraps the package key with the TPM import key and decrypts the package in memory;
   - checks every file against the same rules again;
   - creates a **new** project (never adds to or overwrites one), and commits every file with the history action `imported`;
   - reports the file count and confirms each file's SHA-256 against the package's own list.

   Only operators can import, as today.

## Package format v1 (a new cryptographic format: needs owner review)

It uses only primitives already used in SourceVault: RSA-OAEP-SHA256 and AES-256-GCM.

| Part | Contents |
|---|---|
| Header (readable, but authenticated) | magic `SVPKG1`; format version; package id (16 random bytes); recipient fingerprint; package key wrapped with RSA-OAEP-SHA256 to the import key (256 bytes); creation time (UTC); if D2: sender fingerprint |
| Body | chunks of at most 1 MiB, each AES-256-GCM. The package key is fresh and random for every package, so a chunk's nonce can be its index. Each chunk's associated data is `SHA-256(header) ‖ chunk index ‖ last-chunk flag`, so reordered, missing, truncated or extra chunks are refused |
| Plaintext inside the body | a manifest (format version, a suggested project name, then each file's relative path, length and SHA-256), followed by the file contents in manifest order |
| If D2: signature | the sender key's signature over `SHA-256(header ‖ all ciphertext)` |

File names, sizes and contents are all inside the encrypted body. The readable header shows only which recipient a package is for and when it was made. The service bounds every length before allocating memory, and a package fails as a whole: there is no partial project.

## What this protects and what it doesn't (be aware)

- **Protects:** the code in transit and wherever the package is stored. Only this PC's vault service can decrypt a package; a copy on a USB stick, share or mail server is ciphertext.
- **The other computer holds plaintext.** The code lives there as ordinary files, and SourceVault doesn't change that. Pack doesn't delete or protect the originals.
- **Anyone with the recipient file can make a package for this PC.** The recipient file is public by design. Without D2, someone could swap a package in transit for one of their own with plausible names, and an operator might import it. It would only ever become a *new* project.
- **A TPM clear** makes outstanding packages unreadable, like the vault itself. Pack them again; the originals are still on the other computer.
- **History records** the action `imported` and the operator who imported it. The importer prints the package id and the registered sender computer; neither is stored in the vault. A signature proves which registered computer packed the files, not who wrote the code.

## Build order (after approval)

1. **Shared scan rules:** moved into a small library that both the importer and Pack use.
2. **Package codec** (pack and unpack) with tests: round trip; tampered header, chunk, tag or wrapped key; reordered, truncated and extra chunks; wrong recipient; over-long lengths; hostile names.
3. **Import key:** provisioning (service start mode and setup), the `-ExportRecipient` export, and fingerprint display. Lab-run on the vault PC.
4. **Service import operation** (protocol v2 additions) and `SourceVault.Import.exe --package`. Tested in process over real pipes.
5. **`SourceVault.Pack.exe`:** published self-contained, added to the package (`tools\`), and documented in the user guide and the lab checklist.

## Decisions (owner, 2026-10-19)

- **D1: separate import key.** A second decrypt-only TPM key, `<vault key name>.Import`, under `NT SERVICE\SourceVault`.
- **D2: signed packages.**
  - Pack creates a signing key in the **source computer's TPM**: Microsoft Platform Crypto Provider, RSA-2048, signing only, not exportable, per user, named `SourceVault.Sender`. There is no software fallback: Pack refuses on a computer without a usable TPM (Windows 11 requires one).
  - An administrator registers each source computer's public sender file on the vault PC once. The service refuses packages from unregistered senders.
- **D3: Pack runs on Windows only.**
- **D4: the same limits as the importer:** UTF-8 text only, 256 KiB per file, 1,024 files.

## Implementation details

**Recipient and sender files** are UTF-8 JSON, at most 8 KiB, with no unknown or duplicate fields:
- shape: `{"format": "sourcevault-recipient" | "sourcevault-sender", "version": 1, "computer": "<name>", "publicKey": "<base64 SubjectPublicKeyInfo>", "fingerprint": "<hex SHA-256 of that SubjectPublicKeyInfo>"}`;
- the key must be RSA-2048, and the fingerprint must match the key;
- fingerprints are shown as eight groups of eight hex digits.

**Package file v1**, binary, integers little-endian:

| Field | Bytes |
|---|---|
| magic `SVPKG1\0\0` | 8 |
| version = 1 | 2 |
| flags = 1 (signed) | 2 |
| package id (random) | 16 |
| creation time, Unix milliseconds | 8 |
| recipient fingerprint | 32 |
| sender fingerprint | 32 |
| package key wrapped with RSA-OAEP-SHA256 to the recipient | 256 |
| chunk count | 4 |
| plaintext length | 8 |
| **header total** | **368** |
| chunks: AES-256-GCM ciphertext plus a 16-byte tag | chunk *i* holds 1 MiB of plaintext, except the last |
| signature: RSA-PSS SHA-256 by the sender over `SHA-256(header ‖ all chunks)` | 256 |

- **Chunks:** nonce = 4 zero bytes ‖ chunk index (64-bit). Associated data = `SHA-256(header)` ‖ chunk index (32-bit) ‖ last-chunk flag (1 byte).
- **Exact size:** the file length must equal exactly what the header implies, so trailing or missing bytes are refused.
- **Plaintext:**
  - version (2), suggested project-name length (2) and name;
  - file count (4), at most 1,024;
  - per file: path length (2) and UTF-8 path, content length (4), at most 256 KiB, and SHA-256 (32);
  - then every file's content, in that order.
- **Checks on import:**
  - every path passes the vault's name rules, with no case-insensitive duplicates;
  - every content is valid vault text and matches its SHA-256;
  - the total package is at most 260 MiB.
- **Order:** the signature is verified before any decryption, so nothing from an unregistered or altered package is decrypted.

**Protocol v2 additions.** Every operation also takes `capability`.

| Operation | Request body | Success body |
|---|---|---|
| GetImportRecipient | — | `computer`, `publicKey`, `fingerprint` |
| BeginPackageImport | `length` (bytes) | `{}` |
| PackageImportPart | `offset`, `dataBase64` (at most 512 KiB) | `received` |
| FinishPackageImport | `projectName` | `projectId`, `snapshotId`, `imported`, `packageId`, `sender` (the registered computer name) |

- **One upload per connection, and one package import at a time across the service**, so the service holds at most 260 MiB.
- **New error codes:**
  - `PackageRejected`: malformed, altered, or for another recipient;
  - `UnknownSender`: the signature is valid, but the sender isn't registered.
- **Other errors:** `GetImportRecipient` and the import operations return `Unavailable` while the import key isn't provisioned. Parts must arrive in order, without gaps.

**Registered senders:** `C:\ProgramData\SourceVault\senders\<fingerprint>.json`.
- Setup creates the folder: administrators and SYSTEM have full control, `NT SERVICE\SourceVault` can read, nobody else has access.
- `Setup-SourceVault.ps1 -RegisterSender <file>` validates a sender file and copies it there.
- The service reads the folder for each import.

**Import key provisioning:** a one-shot service start mode, `provision-import-key`, like `provision-key`. Setup runs it at install and update; it does nothing if the key exists. The service opens the import key at startup when it exists; without it, package import is `Unavailable`, and nothing else is affected.

**Command lines:**
- `SourceVault.Import.exe --export-recipient <path>` (operator): writes the recipient file and prints its fingerprint.
- `SourceVault.Import.exe --package <file.svpkg> --project "<name>"` (operator).
- `SourceVault.Pack.exe init` (source computer, once): creates the sender key and writes `SourceVault-sender-<computer>.json`.
- `SourceVault.Pack.exe <folder> --to <recipient file> --out <file.svpkg> [--exclude <name>]... [--dry-run]`.
- `SourceVaultPack.exe` (owner request 2026-10-19): the same in a small WPF window, sharing `PackCore` with the command line. It sets up the computer, chooses the recipient and the folder, shows the **Included** and **Skipped** lists, and creates the package after a confirmation that repeats both fingerprints.

## Decisions as asked

- **D1. A separate import key (recommended), or reuse the vault key?** Reusing it saves one setup step, but lets outsiders' data reach the key that protects the vault.
- **D2. Should packages be signed by the computer that made them?**
  - **(a) No:** simplest, and anyone with the recipient file can make a package.
  - **(b) Yes (recommended):** each source computer gets a signing key; an administrator registers its public file on the vault PC once, and the service refuses packages from unregistered computers. That is one more one-time step per source computer.
- **D3. Where does Pack run?** Windows only, or also Linux? The Linux-only part of your project may live on a Linux computer. .NET can build Pack for both.
- **D4. Limits:** the same as the importer (text only, 256 KiB per file, 1,024 files)? Binary files would be skipped and listed, as today.
