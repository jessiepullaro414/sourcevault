# SourceVault security whitepaper

**Version 1.0, 2026-10-20.** Supersedes draft 0.1 (same day).

This version is complete for review: it describes the whole design, every control, and every limit the owner has accepted. It is **not a certification**. Read it with three things in mind:
- **No independent audit yet.** SourceVault has had no independent security review. Section 14 says what that review must cover, and real source code or a public release must wait for it.
- **Limited evidence.** Everything was tested with synthetic inputs, much of it in process. The real-machine runs were on one development PC.
- **Claims follow the record.** Every claim is limited to what [status.md](status.md) and [lab-checklist.md](lab-checklist.md) record. Section 2 lists each piece of evidence, and marks what is verified and what is scheduled but not yet run. A scheduled check is not counted as evidence until its result is recorded.

---

## 1. Summary

SourceVault keeps a team's source code encrypted on one Windows PC. The code is readable only through SourceVault on that PC, by the people the administrator names, and by that PC's administrators, whom the design trusts.

- **Keys stay in the TPM.** The vault key is a non-exportable, decrypt-only RSA-2048 key in the PC's TPM 2.0, usable only by the service account `NT SERVICE\SourceVault`. There is no software fallback and, by owner decision, no recovery key.
- **Everything in the vault is encrypted at rest.** File contents, file names, project names, history and build logs are all AES-256-GCM records, whose keys are wrapped by the TPM key, either directly or through a per-project key. Tampering is detected and refused.
- **One way in, through a checked door.** Operators reach the vault only through a local named pipe. On every request the service checks:
  - the caller's group and logon type;
  - the exact installed program;
  - the caller's own SourceVault password, at every session start.

  Sessions expire.
- **An editor designed against accidental export.** The editor receives decrypted text only for files that are opened. What's copied in it stays in it: nothing reaches the Windows clipboard. It blocks drags out, printing and developer tools, has no route to the network, and asks Windows to exclude its window from screen capture.
- **Controlled ways in and out.** Code comes in through a verifying importer, or as an encrypted, signed package from a registered computer. It goes out only as build output. Builds run in containers with no network, under a second service identity, and their output goes to a folder the administrator chooses and locks down.

**What it does not do.** SourceVault does not protect against:
- the PC's own administrators;
- malware running as the operator;
- an operator who sets out to copy code out.

It does not guarantee that screen capture is blocked, and it does not process source only in RAM. Section 11 lists every non-claim.

---

## 2. Status of the evidence

| Evidence | Covers | Status |
|---|---|---|
| **Owner-run lab, real TPM** (2026-10-17 and 2026-10-18; Windows 11 Pro 25H2, Intel firmware TPM 2.0) | Service installed under its virtual account; TPM key provisioning (decrypt-only, export refused); vault-root ACL gate; startup authentication of every project; tamper drill (stray file and flipped manifest bit refused, vault restored byte-for-byte); pipe DACL refusing a non-operator; the installed lab client's end-to-end check, including a refused forged capability and a refused byte-identical copy outside Program Files; hash pins and the PID-reuse check accepting an untouched client | **Verified** |
| **Owner-run lab, real Docker engines** (2026-10-20, same PC) | The installed editor against the installed service: a folder import, then builds of the fictional `dotnet-hello` sample on the **Windows target** (Microsoft's standalone engine, process isolation, the copy check passed) and the **Linux target** (Docker in a WSL distro of its own, reached over loopback TLS with a client certificate). Both builds published the expected files and left out `Hello.pdb`. `-Status` reported Secure Boot on, BitLocker off on this development PC (owner's choice; the vault PC is a different machine), and every project's integrity check passed. Five setup-script bugs were found and fixed in this run (section 10) | **Verified** |
| **In-process tests** (`scripts/Verify.ps1`, 2026-10-20) | Record codec 76, revision store 79, endpoints over real local pipes 56, builds against a fake Docker engine 19, packaging 12, editor host policy 70 and editor web 88 checks, service 123 assertions, and Windows storage-root validation 38 (1 skipped: no symlink privilege), plus contract and IPC tests. Software stand-ins replace the TPM keys in these tests | **Verified** (passing) |
| **Native probe** (2026-10-19) | The editor's browser layer: from a deliberately unrestricted test page, 0 TCP connections and 0 UDP packets reached loopback listeners | **Verified** |
| **Scheduled owner lab run** (2026-10-21) | The rows still open in [lab-checklist.md](lab-checklist.md), among them: guided "Set up Docker builds" on a clean PC; own build images (Java/Ant); `docker load` of the drive's archives on Docker 29; SourceVault passwords (lockout, reset); offline mode; the clipboard and drop rules natively; capture exclusion against real tools; `-Status` on the vault PC (BitLocker and Secure Boot) | **Scheduled.** Each becomes evidence only when its result is recorded |
| **Not yet scheduled** | Reboot persistence and power loss; package import between two real TPMs; the full offline deployment drive; the any-file vault at 25,000 files and several GiB | **Open** |

When a scheduled check is run, its row in `lab-checklist.md` and the matching sentence here are updated together. A failed check becomes an open finding in section 13.

---

## 3. System overview

```
 Operator (member of "SourceVault Operators")
   │
   │  SourceVault.Editor.exe (WPF host + WebView2 renderer)   SourceVault.Import.exe   Lab client
   │        │ bridge v2 (ids, names, text only)                       │
   │        ▼                                                         │
   │  SourceVault.Client ──── \\.\pipe\SourceVault.v1 (protocol v2) ──┘
   │                                   │
   │                                   ▼
   │                   SourceVault service  (NT SERVICE\SourceVault)
   │                    │        │                 │
   │          TPM 2.0 vault key  vault root         \\.\pipe\SourceVault.Build.v1
   │          + import key       (encrypted stores, │
   │                              operator passwords)▼
   │                               SourceVault build service (NT SERVICE\SourceVaultBuild)
   │                                  │ Windows engine: \\.\pipe\docker_engine (owner checked)
   │                                  │ Linux engine in WSL: 127.0.0.1:2376, mutual TLS
   │                                  ▼
   │                  build containers, no network ──► artifact folder (plaintext, locked down)
   │
 Another computer: SourceVault Pack ──► encrypted, signed package (.svpkg) ──► importer ──► service
```

The components:
- **Vault service.** One Windows service under a virtual account.
  - It holds the only handle to the TPM vault key, owns the vault folder, and serves protocol v2 on a local pipe.
  - It is the only component that decrypts.
  - It checks operators' SourceVault passwords, whose hashes it keeps inside the encrypted vault.
- **Vault root.** A folder with an exact, validated ACL: `C:\ProgramData\SourceVault\vault` by default, or one on a fixed internal drive chosen at install. It holds an encrypted project index, an encrypted accounts store, and one encrypted store per project.
- **Editor.** A WPF host with a WebView2 renderer that runs the vendored Monaco editor.
  - Only the host talks to the service.
  - The renderer sees project and file ids, names, and the text of files it opens.
- **Importer and Pack.**
  - The importer brings a local folder into a new project and verifies it.
  - Pack, on another computer, encrypts a folder to this PC and signs it with that computer's TPM.
- **Build service.** A second service identity, and the only one with Docker access.
  - It runs builds in administrator-configured containers, and never touches the vault or the TPM.
  - Section 9.3 covers the engines and images it uses.
- **Setup.** `SourceVault Setup.cmd` is a guided administrator tool, also on the Start menu as *SourceVault Administration*.
  - It installs, updates, adds and removes operators, resets passwords, registers Pack computers, and reports status.
  - Its option 6 sets up the build engines and targets (section 10).
- **Demo mode.** `SourceVault.Editor.exe --demo` runs the editor on an in-page simulated vault, for presentations.
  - **Nothing in demo mode is encrypted.** It never connects to the service, and it says so in red throughout.

---

## 4. Threat model

### 4.1 Assets

1. Source code and its history.
2. File and project names, which can reveal what a project is.
3. The TPM vault key and the import key, and their use.
4. The integrity of the vault: a change must be detected, not silently used.
5. Operators' SourceVault passwords.

### 4.2 Trusted

- **Administrators, the operating system, firmware and the TPM.** Anyone with local administrator rights can run code as the service and use its key. SourceVault does not defend against them, and says so (AGENTS.md, user guide).
- **Operators, to read every project.** Every member of *SourceVault Operators* can open every project, by owner decision (2026-10-18). There are no per-project permissions.
- **The installation, as provisioned by an administrator.** Ordinary users can't write to Program Files, the configuration, the artifact folder, or the vault root's ancestors.
- **The build images.** Microsoft's .NET SDK images are pinned by digest. Owner-supplied images are pinned by image ID (section 9.3). A build image is trusted to run builds; it is not trusted with the vault.

### 4.3 In scope (what SourceVault is designed to resist)

| Adversary or event | Intended outcome |
|---|---|
| Another, non-operator user of the same PC | Can't open the pipe (DACL), the vault folder (ACL) or the artifact folder (ACL). Sees no names or content |
| Someone at an operator's unlocked, signed-in PC | Can't open the vault without that operator's SourceVault password. Five wrong tries lock the operator out for 15 minutes |
| The vault disk or vault folder copied or stolen on its own | Ciphertext only. The keys are in this PC's TPM, which doesn't export them |
| Bit-flips, truncation, swapped or stray files in the vault | Detected. The operation, or the service start, is refused; nothing is silently repaired |
| A program other than the installed SourceVault clients, run by an operator | Refused by the executable path and hash checks (defence in depth; see 7.4) |
| A stolen or forged session capability | Refused. A capability is bound to its connection, user and logon session |
| A package intercepted or swapped in transit | Only this PC can decrypt it, and only registered senders' signatures are accepted |
| A fake Docker engine (a pipe squatted while Docker is down) or a tampered output folder | The build is refused before any source is sent (section 9.3) |
| Accidental export by an operator: copy and paste, dragging out, printing, screenshots, or the editor reaching a website | Made harder by the editor's lockdown (section 8) |
| A process crash during a save | The project always reopens at a complete snapshot |

### 4.4 Out of scope

- **Local administrators**, SYSTEM, and anyone who can install drivers or services.
- **Malware or another process running as the operator.** This includes debuggers, code injected into an allowed client, a replaced WebView2 runtime, a process listening on the editor's dead proxy port, and a keylogger capturing the SourceVault password.
- **Intentional exfiltration by an operator.** An operator can read code and retype it elsewhere, photograph the screen, or write a build script that copies source into the build output.
- **Physical attacks on the TPM or the running machine:** bus sniffing, cold boot, DMA.
- **Rollback of a whole valid old vault.** Restoring an entire earlier copy of a project folder isn't detected (storage contract v1).
- **Side channels, cameras, and capture tools that ignore window display affinity.**
- **Availability.** Losing the TPM loses the vault, by design. The Linux build target is unavailable while the account that owns its WSL distro is signed out.

### 4.5 A stolen whole PC (not just its disk)

A thief with the **whole PC** can't read the vault from the disk alone, because the key never leaves the TPM. But if the Windows volume is unencrypted, they can change it offline, for example by planting a service. On the next boot that code runs as an administrator on the same TPM, which is outside this model and gives access to the key.

**The control is BitLocker and Secure Boot on every vault PC.** SourceVault reports them but doesn't enforce them:
- **`Setup-SourceVault.ps1 -Status` checks both,** for the Windows drive and the vault drive, and warns if either is off. On the development PC it correctly reported BitLocker off (2026-10-20).
- **The vault PC has both on,** as reported by the owner. Confirming that with `-Status` on the vault PC is a scheduled lab row.
- **A BitLocker startup PIN is still recommended.** With TPM-only unlock, Windows starts without one, and the remaining attacks need the running machine, which is outside this model.

---

## 5. Cryptographic design

### 5.1 Keys and secrets

| Key or secret | Where it lives | Used for | Lifetime |
|---|---|---|---|
| **Vault key** | TPM 2.0 (Microsoft Platform Crypto Provider). RSA-2048, non-exportable, decrypt-only (`PCP_KEY_USAGE_POLICY` encryption-only), under `NT SERVICE\SourceVault` | Wrapping the data keys of manifests and project-key records (RSA-OAEP-SHA256) | The life of the TPM. Created once by an explicit administrator step; never overwritten, never created during normal operation |
| **Import key** | TPM, as above, but a separate key | Unwrapping package keys only. Kept apart so that outsiders' data never reaches the vault key (decision D1) | As above. If it can't be opened, package import is unavailable and the vault still runs |
| **Sender key** | The source computer's TPM, non-exportable | Signing packages (RSA-PSS-SHA256) | Registered on the vault PC by an administrator, after the check codes are compared |
| **Project key** | An encrypted `ProjectKey` record, wrapped by the vault key | Wrapping the data keys of a project's file and history records (AES-256-GCM) | Unwrapped at most once per store operation, under the project lock, and zeroed when the operation ends. Never cached, logged or sent |
| **Data key** | Wrapped inside each record | Encrypting exactly one record | Fresh and random for every record; never reused |
| **Package key** | Wrapped to the import key, in the package header | Encrypting one package | One package |
| **Session capability** | Service memory and the client | Authorising requests on one connection | 256 random bits; expires after 5 minutes idle or 30 minutes in total |
| **SourceVault password hash** | The encrypted accounts store in the vault | Checking an operator's password at session start (section 7.5) | Until the operator changes it or an administrator resets it |
| **Build TLS keys** | Server key: root-only, inside the WSL distro. Client key: the machine certificate store, non-exportable, readable only by `NT SERVICE\SourceVaultBuild` and administrators | Mutual TLS between the build service and the Linux engine | 825 days; `-ReconnectTls` replaces them |

**No recovery, by design.** The owner decided (2026-10-18) that there is no recovery envelope, escrow or key export. Clearing the TPM, replacing the motherboard or deleting the service profile makes the vault permanently unreadable.

**No key rotation.** It's out of scope: the key never leaves the TPM, and anyone who could misuse it is an administrator, whom the model already trusts.

### 5.2 Records

Every stored object is an encrypted record: file content, history, manifests, project keys, the project index, build logs, and the accounts store.

- **`SVR1`** was written until 2026-10-19 and is still read.
  - It is one AES-256-GCM block with a random 96-bit nonce. Its data key is wrapped by the TPM key with RSA-OAEP-SHA256.
  - The AAD is the 74-byte fixed prefix (magic, kind, project, file and revision ids, lengths, nonce), followed by the wrapped key.
  - Text only, at most 256 KiB.
- **`SVR2`** is written now, and was coordinator-reviewed. It is a segmented construction for any content up to 1 GiB:
  - **Prefix:** 68 bytes of magic, kind, wrap mode, project, file and revision ids, plaintext length, and segment size (512 KiB).
  - **Wrapped data key:**
    - **Wrap mode 1** (manifests, project keys): RSA-OAEP-SHA256, by the TPM key.
    - **Wrap mode 2** (files and history): AES-256-GCM, by the project key, with a random nonce and AAD = prefix ‖ project key id.
  - **Segments:** 512 KiB AES-256-GCM segments. Segment *i* uses nonce 0⁴ ‖ *i* and AAD = SHA-256(prefix ‖ wrapped key) ‖ *i* ‖ last-segment flag. So reordering, splicing between records, truncation and dropping the last segment are all refused. An empty plaintext is one empty segment.
  - **Size check:** the exact record size is checked before any key is unwrapped.
- **Both formats:**
  - The caller supplies the expected context (project, file, revision, kind), and it must match the header. A record can't choose its own identity.
  - Data keys and failed plaintext are zeroed in `finally`.
  - Errors are generic.

**Why a per-project key.** One TPM unwrap takes about 45 ms on the test machine's firmware TPM, and the TPM serves one command at a time. Wrapping every record directly would make a build or search of a 25,000-file project take about 19 minutes of unwraps. The project key cuts that to one or two unwraps per operation.

**The trade-off, accepted by the owner:** during an operation that reads content, one key in service memory can decrypt the whole project.

### 5.3 Manifests and integrity binding

Each project's current state is an encrypted manifest (schema 4). It lists:
- file ids, encrypted names, revision ids, sizes and kinds;
- the SHA-256 of each file's plaintext;
- the SHA-256 of each referenced encrypted record.

`HEAD` holds only the manifest's id. Because the manifest is itself an authenticated record, it binds every record it references.

What is checked, and when:
- **Every operation** checks that each referenced record exists, has exactly its expected size, and is not a reparse point.
- **Every content read** also checks the record's SHA-256 at the end of the stream, and authenticates every segment before use.
- **A full check** (`VerifyProject`) decrypts every file. The importer runs it after every import.
- **Startup** authenticates the project index, the accounts store and every project's manifest, and checks every record's existence and size.
  - A damaged index or an unexpected entry refuses the whole service (code 11).
  - A project that fails is taken **offline**: it is listed as unavailable, refuses everything but deletion, and is reported by id to administrators. The rest of the vault starts.
  - Since decision C4 (2026-10-19), a same-size change to a file record is caught when that file is read, not at startup. A changed manifest is still refused at startup.

### 5.4 Packages

A package (`.svpkg`) carries a project from another computer. It has:
- **A random package key,** wrapped to the vault PC's **import key** with RSA-OAEP-SHA256, and recorded with that key's fingerprint.
- **The contents** in 1 MiB AES-256-GCM chunks, bound to the header and the chunk index.
- **An RSA-PSS-SHA256 signature** by the sender's TPM key, over SHA-256 of the header and all chunks.
  - The signature is checked against the hash of the bytes as they arrived, **before anything is decrypted**.
  - The sender must be registered in a folder only administrators can change.

**Version 2** (coordinator-reviewed) keeps this construction, raises the limits to the vault's, and streams:
- Pack reads files as it writes them, and stops if one changes.
- The service spools the ciphertext in its staging folder, keeping a free-space reserve, verifies the signature, then re-hashes the spool while importing.

**People compare a check code:** the first 96 bits of the fingerprint, as six groups of four characters. Making a key with a matching code takes about 2⁹⁶ attempts. Software always compares all 256 bits.

**Be aware:**
- The recipient file is public, so anyone can encrypt *to* this PC. Only registered computers' packages are accepted, though.
- A package can be imported more than once. Each import creates a new project and never overwrites one.
- A signature proves which registered computer packed the files, not who wrote them.

### 5.5 Algorithms

| Purpose | Algorithm | Implementation |
|---|---|---|
| Content encryption | AES-256-GCM, 96-bit nonces, 128-bit tags | .NET BCL (`AesGcm`) |
| TPM key wrap | RSA-2048 OAEP with SHA-256 | Windows CNG, Microsoft Platform Crypto Provider |
| Project-key wrap | AES-256-GCM, random nonce | .NET BCL |
| Package signature | RSA-2048 PSS with SHA-256 | Windows CNG (TPM) to sign; .NET BCL to verify |
| Password hashing | PBKDF2-HMAC-SHA256, 600,000 iterations, 16-byte salt, 32-byte hash, constant-time comparison | .NET BCL |
| Build engine link | TLS with client certificates, from the administrator's own CA (RSA-3072, SHA-256) | OpenSSL in the distro to issue; .NET BCL and Docker to use |
| Hashes | SHA-256 | .NET BCL |
| Randomness | OS CSPRNG | .NET `RandomNumberGenerator` |

There are no custom primitives. The service and storage projects use only the .NET base class library, with no third-party cryptographic packages.

---

## 6. Storage integrity and durability

- **Immutable revisions.** Each save writes new encrypted records with `CreateNew`, and a new manifest; nothing is overwritten. History is kept for the life of the project.
- **Atomic publication.** Records and the manifest are flushed, then `PREVIOUS` and `HEAD` are replaced with single renames, and `HEAD` is flushed. An interruption leaves either the complete old state or the complete new state. This replaced `File.Replace` after a kill test found it could leave no `HEAD` (2026-10-19).
- **Evidence:** a child process is killed at random points 50 times per run, and the store always reopens at a complete snapshot. That is evidence for process crashes. **Power loss is not yet tested.**
- **Fail closed.** The store never falls back to `PREVIOUS` because `HEAD` failed. Malformed or tampered state is refused, and the administrator decides what to do.
- **Not prevented:** rollback of a whole valid old store, and secure deletion. Deleting a project or superseded manifests removes ciphertext with ordinary file deletes.
- **Lab evidence (2026-10-17):** the tamper drill refused a stray file in the vault root (1066/11) and one flipped bit in a project manifest (1066/12), and the restored vault then started.

---

## 7. Access control and the service boundary

### 7.1 Service identity

The service runs as the virtual account `NT SERVICE\SourceVault`. It checks its own identity by SID before touching the TPM provider. It refuses keys that are exportable, not hardware-backed, the wrong size, or not decrypt-only.

Its configuration is read only from beside the executable: at most 4 KiB, with an exact schema. It is never read from arguments, the environment or the working folder.

### 7.2 Vault root

Before opening the key, the service validates the vault root (`TrustedStorageRoot`). It requires:
- a fixed local NTFS or ReFS volume (USB, network, virtual and removable disks are refused);
- no reparse points anywhere in the ancestor chain;
- trusted owners;
- the exact protected DACL;
- per-level checks on who can write.

A refusal stops the service with code 10, or 17 if the drive is missing or BitLocker-locked. **Lab-verified 2026-10-17**, including an unelevated session being denied both listing and writing.

### 7.3 The pipe

- **The pipe itself:** `\\.\pipe\SourceVault.v1`, owned by the service account. Its protected DACL gives SYSTEM and *SourceVault Operators* exactly `0x00120083`. Remote clients are rejected, the first instance is protected, and there are at most 4 instances.
- **The client checks the server:** it refuses a pipe not owned by `NT SERVICE\SourceVault`, before sending anything.
- **The server checks the client on every frame:**
  - a member of *SourceVault Operators*;
  - with INTERACTIVE in its token, and without NETWORK or SERVICE;
  - the same user, logon session and process throughout the connection.

### 7.4 Which programs may connect

- **Executable path:** the client process must be one of the installed clients, at its exact path under Program Files. Lab-verified: PowerShell, and a byte-identical copy elsewhere, were both refused.
- **Hash pins:** on each connection the service re-reads `install-manifest.sha256`. The client's folder, recursively and including the editor's web assets, must hold exactly the listed files, with matching hashes. An extra or changed file is refused.
- **PID reuse:** the client process must still be running, and must be older than the connection.
- **Startup hooks are off** in every program (`StartupHookSupport=false`), so `DOTNET_STARTUP_HOOKS` can't load code into an installed client.
- **Be aware: these are defence in depth.**
  - An administrator can rewrite the manifest.
  - Only the files on disk are checked, not the running image.
  - Code injected into an allowed client is not stopped, for example by a debugger or by a .NET profiler set through environment variables.
  - Authenticode signer checks wait for a signing decision.

### 7.5 Sessions and the SourceVault password

**The SourceVault password (owner decision, 2026-10-20).** Every operator has their own password, separate from Windows, and the service checks it at every session start: when the editor opens, and after every lock.
- **Storage:** PBKDF2-HMAC-SHA256 hashes (section 5.5), in an encrypted accounts store in the vault. Hashes are never on disk in the clear.
- **Rules:** 12 to 256 characters, no control characters.
- **Lockout:** five wrong tries in a row lock that operator out for 15 minutes. The counter is kept in service memory, so a service restart, which needs administrator rights, clears it.
- **Reset:** administrators can reset a password (`-ResetPassword`) but can't read or set one. Each reset is honoured once, and also lifts a lockout. The operator then chooses a new password.
- **What it stops:** someone at an unattended, signed-in PC opening the vault.
- **What it doesn't stop:** malware running as the operator, which could capture the password as it's typed.

**Capabilities.** `BeginSession` issues a 256-bit capability, bound to the connection, the Windows user and the logon session. A wrong capability closes the connection.
- Sessions expire after 5 idle minutes, or 30 minutes in total (monotonic clock).
- A Windows lock, sign-out or disconnect closes every vault connection from that Windows session, and the editor locks at once.

### 7.6 Protocol hygiene

- **Frames:** length-prefixed (at most 1 MiB), strict UTF-8 JSON with bounded nesting.
- **Rejected:** unknown or duplicate fields, comments, non-canonical UUIDs, and invalid UTF-8.
- **Concurrency is optimistic:** a stale snapshot is refused, never merged silently.
- **Errors are fixed codes.** They never contain exception text, names or content.
- **Additive changes:** a client must accept a field's absence, and an unknown field in a request is still refused. The most recent such change is `platforms[]` in `ListBuildTargets`.

---

## 8. The editor

### 8.1 Host and renderer

The WPF host owns the service connection, through `SourceVault.Client`. The renderer (the Monaco page in WebView2) never holds the capability or snapshot ids.

Every renderer request must pass `BridgePolicy`:
- the exact message shape;
- the exact set of arguments for its operation;
- canonical ids;
- bounded names and text: 1 MiB of strict UTF-8, 8 MiB per message, checked before parsing;
- for passwords, 1 to 256 characters, with no control characters.

Anything malformed is ignored, without a reply.

### 8.2 What the operator sees, and what never reaches the renderer

- **Decrypted text only for opened files.** The renderer receives text only for files the operator opens, and closing a tab drops it. Search and builds decrypt inside the service; only matching lines (search) or build output leave it.
- **Binary files and large text never reach the renderer.** The editor shows text up to 1 MiB. Anything else gets a details panel: kind, size, last change and SHA-256. No protocol operation returns binary content to any client; it leaves the service only through a build.
- **Files from disk** (*Add from disk*, *Replace from disk*, *Import a folder…*, and dropping on the explorer): the host shows the native dialog, or receives the dropped files unread, then reads and streams them to the service. The renderer never sees the path or the bytes. Windows may remember the last folder used in the dialog.
- **Passwords:** the renderer passes them to the host, which passes them to the service and never stores or logs them. The page clears the fields after each try.

### 8.3 Reducing accidental export

- **Clipboard:** nothing copied in the editor reaches the Windows clipboard. Copy and cut work inside the editor: the text stays in the page's memory, and the Windows clipboard gets only a fixed notice. Pasting text in from other programs is allowed, because it brings code in rather than out.
- **Drops:** files and folders dropped on the explorer are passed to the host unread. Every other drop, and every drag out, is refused.
- **Offline work:** when the service can't be reached, the user may choose *Work offline*.
  - The editor turns red and keeps editing only the text it already holds in memory, plus new files.
  - Nothing is decrypted or written to disk.
  - Nothing reaches the vault until the service is back, the password is entered again, and the normal save checks run.
  - A Windows lock ends offline work.
- **Blocked:** dragging out, the context menu, printing, developer tools, browser shortcuts, downloads, new windows, permission prompts, autofill and password storage. Startup refuses `WEBVIEW2_*` environment overrides, and WebView2 registry policy overrides that could apply to the editor.
- **No network,** in layers that don't depend on each other:
  - navigation only to the bundled entry page;
  - a host filter on every resource request, from every context;
  - a dead loopback proxy, and `MAP * ~NOTFOUND` host resolution;
  - WebRTC removed before page script runs, and restricted to the proxy;
  - SmartScreen, crash upload, single sign-on and extensions off;
  - a `default-src 'none'` CSP;
  - network APIs removed in the page.

  **Measured (2026-10-19):** from a deliberately unrestricted probe page, loopback listeners received 0 TCP connections and 0 UDP packets. After 60 s idle, the browser process tree held no sockets. A control run with WebView2's defaults did reach the listeners, and held TLS connections to Microsoft addresses unprompted.
- **Capture exclusion:** in vault mode, the host asks Windows to exclude the window from capture (`WDA_EXCLUDEFROMCAPTURE`) and reports whether that succeeded. **This is best effort.** Cameras, some capture tools, and anyone with access to the session can bypass it.
- **Profiles:** the browser profile of a vault-mode run is deleted when the browser exits, and leftovers from crashed runs are deleted at the next start. A typed canary left no trace in the profile, Temp or crash dumps, in a recorded native check.

### 8.4 Limits

The decrypted text of open files is in the editor's memory and the service's. Windows may write that memory to the page file, the hibernation file or crash dumps.

None of these controls is a boundary against the operator's own processes, administrators, a debugger or a modified runtime.

---

## 9. Getting code in and out

### 9.1 Folder import

Folders come in through `SourceVault.Import.exe`, or through *Import a folder…* in the editor, and always become a **new** project.
- **Limits:** any file up to 1 GiB, at most 25,000 files and 8 GiB.
- **Skipped:** links are never followed, and well-known build and VCS folders are left out.
- **After importing:** every file's SHA-256 is compared with the vault's, and the service is asked to decrypt every record (`VerifyProject`).
- **If an import fails:** in the editor, the half-imported project is removed again. The command-line importer leaves it in place and says how many files reached it, and the import is then rerun into a new project.

**`--delete-source`** deletes only files that verified and are still identical, after a typed `DELETE`. It uses a single handle that doesn't follow links: each file is overwritten with zeros, then deleted.

**This is not a secure erase.** SSDs, backups, shadow copies, sync clients and `.git` (which is kept) can all retain copies.

### 9.2 Encrypted packages

See section 5.4. **Be aware:** the source computer still holds the code as ordinary files.

### 9.3 Builds: the designed export path

Builds are the one way vault content becomes plaintext files, and they are designed as such.

**Who does what.**
- **Separation:** Docker access is administrator-equivalent, so it belongs to a second identity, `NT SERVICE\SourceVaultBuild`. That identity:
  - has no access to the vault or the TPM;
  - never receives project names;
  - serves only the vault service, over an access-checked pipe. The vault service checks that pipe's owner before sending anything.
- **What runs is the administrator's choice.** The administrator-only `build.json` names each target's container, entry command, paths and time and size limits.
  - The entry command runs the project's own script: `build.cmd` for Windows targets, `build.sh` for Linux targets.
  - Those scripts are ordinary project files, encrypted and versioned. The editor's **Build steps** opens or creates them from templates.
  - Operators see target names and platforms, never the configuration.
  - A container with network access is refused unless the administrator allows it for that target. Plain-TCP Docker is refused.

**The engines.**
- **Windows:** Microsoft's standalone Docker engine. Only administrators and an empty local group `docker` may use its pipe, and only the build service is added to that group.
  - Its pipe must be owned by SYSTEM or Administrators before anything is sent, and the connection allows identification only.
  - A pipe created by an ordinary user while Docker is down is refused (`EngineUntrusted`). That was tested with a squatted pipe in process; the lab row with the real engine is scheduled.
- **Linux:** Docker's static engine, in a WSL distro of its own (`SourceVault-Ubuntu`), with no bridge network and no iptables.
  - It listens on `127.0.0.1:2376` with mutual TLS, from the administrator's own CA. Only the holder of the client key, which only the build service can read, is accepted.
  - **Be aware:** the distro belongs to the Windows account that set it up. That account can control the engine from inside WSL, and so can see source in the Linux containers during a build. In practice it is an administrator's account.

**The images.**
- **Microsoft's .NET SDK images** are pinned by digest when pulled, and by image ID when loaded from the drive.
- **The owner's own build images** (for Java/Ant, C++ and other toolchains) are created only from an exact image ID (`-OwnerImageId`), and are never pulled by SourceVault.
- **Docker 29's containerd image store** reports a manifest digest as the image ID, so the drive's offline check accepts either the pinned configuration digest or the pinned manifest digest.

**The containers.** They are long-lived and created by setup, with:
- no network;
- memory limits;
- SourceVault's keep-alive as the entry point;
- on Linux, all capabilities dropped, `no-new-privileges`, an init process, a process limit, and `/tmp` in memory. Builds run as root inside the container, as in Microsoft's .NET image.

**Before and after each build.**
- **The output folder is checked** before every build, the same way as the vault root: no links, and only the build service, SYSTEM and Administrators can change it or any folder above it. Otherwise the build is refused before any source is sent.
- **The container's folders are emptied** before and after each build. The cleanup command is passed as bare words, because the engine re-escapes quotes in a way `cmd.exe` doesn't read. That was found and fixed in the 2026-10-20 lab run.
- **Output checks:**
  - regular files and folders only: no links, devices, absolute paths or `..` paths;
  - size and count limits;
  - source maps, debug symbols, compiler intermediates and inline source maps are dropped, and listed in the encrypted build log (the lab run showed `Hello.pdb` left out);
  - output goes to `<artifactRoot>\<projectId>\<build>-<target>`, so names never appear on disk.

**Be aware:**
- **A build can copy source into its output.** A build runs the project's own scripts and any vendored tools, so anyone who can edit a project can make its build do that. The administrator controls *where* output goes and what runs it, not *what* it contains.
- **Containers can keep plaintext.** The long-lived containers hold plaintext source during a build. Emptying their folders is best effort, not erasure, and caches or copies elsewhere in a container can persist.
- **No package downloads.** Builds can't download NuGet, Maven or Ivy packages; anything a build needs must be in the project or the image.

---

## 10. Deployment and supply chain

**Installation.** There are two routes:
- **The setup package:** a folder and a `.zip`, from `scripts\Build-Installer.ps1`, with a manifest of every file that setup checks first.
- **The offline deployment drive,** for the vault PC. `Build-DeploymentKit.ps1` assembles it on a connected PC. It holds SourceVault, Pack, Docker, WSL and Ubuntu, and the build images, each pinned and checked by SHA-256 or image digest. `Deploy-SourceVault.ps1` installs from it.

**Build setup without the drive** (guided setup, option 6: `Setup-Builds.ps1`). It does the drive's Docker steps, using files from a downloads folder:
- it downloads a missing file only after the administrator says yes;
- it checks every file against `kit-sources.json` (size, and SHA-256) before use;
- the build images come by their pinned digests.

**Be aware:**
- **The drive manifest is not signed,** so the drive must stay in trusted custody.
- **Docker's archives are trusted on first use,** because Docker publishes no checksums. Their hashes were recorded from the development PC's downloads (2026-10-20).
- **The Ubuntu image gets no updates** on an offline PC.

**Script fixes from the 2026-10-20 lab run.** Five setup-script bugs were found and fixed, all in owner-run setup paths. Each is now covered by the fixed scripts and recorded in the lab checklist:
1. a Windows group description over `New-LocalGroup`'s limit;
2. Windows paths passed to WSL through its shell, which stripped the backslashes;
3. a stray carriage return that PowerShell adds to every script piped into WSL, which broke the systemd unit that starts Docker;
4. the cleanup command's quoting;
5. image IDs on the containerd store.

None of them affected encryption or access control; they stopped setup or builds from working. They are why the remaining lab rows matter before relying on the drive.

**Third-party code.**
- The service and storage use only the .NET base class library.
- The editor vendors Microsoft Monaco 0.57.0 and the WebView2 SDK 1.0.4258.31 from their official registries, with SHA-512 hashes and licences recorded (`editor-dependencies.json`). The WebView2 package's Microsoft signature was verified.
- Nothing is fetched at run time, and there is no CDN.

**Signing.** SourceVault's own binaries, installer and updates are not yet signed; that waits for a signing decision.

**Upgrades.** The any-file storage upgrade is one-way: once a project is converted, an older service refuses the vault.

---

## 11. Explicit non-claims

SourceVault does **not** claim:
- resistance to local administrators, SYSTEM, or anyone who can run code as the service;
- runtime executable identity: the client checks are path and file-hash checks, not proof of the running code;
- guaranteed blocking of screen capture;
- prevention of intentional exfiltration by an operator, including through build scripts;
- RAM-only processing, or that plaintext never reaches the page file, the hibernation file or crash dumps;
- secure deletion, of the vault's ciphertext, of imported originals, or of build containers' contents;
- protection against rollback of a whole valid old vault;
- recovery of a vault after a TPM clear or hardware loss;
- that the SourceVault password resists malware running as the operator;
- that demo mode protects anything: it is unencrypted by design.

---

## 12. Logging and privacy

- **The services log no secrets.** The service and the build service log no source, file or project names, keys, passwords, capabilities or build output. Errors are fixed codes.
- **Names stay encrypted.** Project names exist only in the encrypted index. Folders and artifact paths use ids.
- **Offline projects are reported by id only,** in `%ProgramData%\SourceVault\status`, which only administrators can read.
- **Editor settings** store eight display fields only, never names or text.
- **Build logs**, which can contain source, are stored encrypted in the vault and shown read-only in the editor.

---

## 13. Residual risks

| Risk | Current mitigation | Status |
|---|---|---|
| Stolen whole PC with an unencrypted Windows volume (section 4.5) | BitLocker and Secure Boot on the vault PC (owner-reported); `-Status` warns if either is off | Confirmed on the vault PC when its `-Status` lab row is recorded; a startup PIN is still recommended |
| Administrator or same-user malware | Out of scope; documented | Accepted |
| An operator exports code through a build | Output folder lockdown and artifact filtering; documented as an export path | Accepted; an owner-approval release step is not built |
| The WSL distro's owner can control the Linux engine | The distro belongs to the account that set it up (in practice an administrator); TLS keeps other accounts out | Accepted; documented |
| The project key is in memory during a read | Unwrapped once per operation, zeroed afterwards, never cached | Accepted trade-off (owner, 2026-10-19) |
| Plaintext in memory, the page file or dumps | Profile deletion; crash dumps not uploaded | Accepted; documented |
| Plaintext left in long-lived build containers | Folders emptied before and after each build | Accepted; best effort, documented |
| Whole-vault rollback | None | Accepted in storage v1; to revisit |
| Unsigned binaries and drive | Hash pins, package and drive manifests, custody | Open: signing decision |
| Capture tools that ignore display affinity | Best-effort exclusion | Accepted; documented |
| Power loss during a commit | Flushes and atomic renames; process-kill tests | Open: lab test not yet scheduled |
| TPM loss | None, by design (no recovery) | Accepted (owner, 2026-10-18) |
| Every operator can read every project | Group membership, plus each operator's own password | Accepted (owner, 2026-10-18) |
| The lockout counter resets when the service restarts | Only administrators can restart the service | Accepted; documented |

---

## 14. The path to an audited release

1. **Record the scheduled lab run** (2026-10-21) in `lab-checklist.md` and `status.md`. Update section 2, and add any failure to section 13.
2. **Run the lab rows not yet scheduled:** reboot and power loss, package import between two real TPMs, the full offline deployment drive, and the any-file vault at 25,000 files.
3. **Owner decisions** ([next-tasks.md](next-tasks.md)):
   - signing and release ownership;
   - the intentional-export policy (and whether to build an approval step for build output);
   - supported hardware and OS;
   - whether to add a BitLocker startup PIN;
   - the drafts design (D1–D4).
4. **Independent security audit** by a third party. It should cover:
   - the cryptographic formats: `SVR1`, `SVR2`, manifests, packages v1 and v2, the accounts store;
   - the service's access checks and password handling;
   - the editor's lockdown;
   - the build path, including the engines and the TLS setup.

   Development-time reviews by the coordinator and workers are not an audit.
5. **Measurements:** TPM and AES-GCM throughput at 25,000 files, to replace this paper's estimates.

This paper is then revised to version 1.1, with the audit's findings and their resolutions.

---

## Appendix A. Where each topic is specified

| Topic | Document |
|---|---|
| Protocol, pipe, sessions, passwords, error codes | `contracts/protocol-v2.md` |
| Record format SVR1, store contract | `contracts/storage-v1.md`, `docs/record-codec.md`, `docs/revision-store.md` |
| SVR2, project key, any-file limits | `docs/vault-any-file-design.md` |
| Service identity, key provisioning, vault root, startup checks | `docs/service-host.md`, `docs/vault-location-design.md` |
| Client checks (PID reuse, hash pins) | `docs/hardening-design.md` |
| Editor lockdown, capture exclusion, offline work, passwords | `docs/editor-host.md`, `docs/editor-vault-design.md` |
| Packages and Pack | `docs/package-import-design.md` |
| Builds, engines, own images | `docs/files-history-builds-design.md`, `docs/cpp-build-design.md`, `docs/user-guide.md` |
| Offline deployment, build setup | `docs/deployment-kit-design.md`, `installer/Setup-Builds.ps1` |
| Evidence and open items | `docs/status.md`, `docs/lab-checklist.md`, `docs/next-tasks.md` |

## Appendix B. Glossary

- **Operator:** a Windows user in *SourceVault Operators*, with their own SourceVault password. Can read and edit every project.
- **Capability:** a random session token the service issues for one connection.
- **Record:** one encrypted file in the vault: content, history, a manifest, a key, the index, or the accounts store.
- **Manifest:** the encrypted list of a project's current files and their record hashes.
- **Package:** an encrypted, signed project made by Pack on another computer.
- **Build target:** a named, administrator-configured container that builds a project's saved files.
- **TPM:** Trusted Platform Module 2.0, the chip that holds the vault and import keys.
