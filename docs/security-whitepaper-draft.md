# SourceVault security whitepaper

**DRAFT 0.1, 2026-10-20. Not reviewed. Not for distribution as a security claim.**

SourceVault is a development build. It has been tested with synthetic inputs only, most of it in process, and it has had **no independent security review**. This paper describes the design and the evidence as they stand. Every claim is limited to what [status.md](status.md) and [lab-checklist.md](lab-checklist.md) record. Where something is designed but not yet verified on a real machine, the paper says so. An independent third-party audit is required before real source code or a public release (see section 14).

---

## 1. Summary

SourceVault keeps a team's source code encrypted on one Windows PC, so that it is readable only through SourceVault on that PC, by the people the administrator names (and by that PC's administrators, whom the design trusts).

- **Keys stay in the TPM.** The vault key is a non-exportable, decrypt-only RSA-2048 key in the PC's TPM 2.0, usable only by the service account `NT SERVICE\SourceVault`. There is no software fallback and, by owner decision, no recovery key.
- **Everything in the vault is encrypted at rest.** File contents, file names, project names, history and build logs are stored as AES-256-GCM records whose keys are wrapped by the TPM key, either directly or through a per-project key. Tampering is detected and refused.
- **One way in, through a checked door.** Operators reach the vault only through a local named pipe. The service checks the caller's group, logon type and the exact installed program on every request, and sessions expire.
- **An editor designed against accidental export.** The editor receives decrypted text only for files that are opened. It blocks copy, paste, drag-and-drop, printing and developer tools. It has no route to the network, and asks Windows to exclude its window from screen capture.
- **Controlled ways in and out.** Code comes in through a verifying importer or as an encrypted, signed package from a registered computer. It goes out only as build output, which the administrator directs to a known folder.

**What it does not do.** SourceVault does not protect against the PC's own administrators, against malware running as the operator, or against an operator who sets out to copy code out. It does not guarantee that screen capture is blocked, and it does not process source only in RAM. Section 11 lists every non-claim.

---

## 2. Status of the evidence

| Kind of evidence | What it covers |
|---|---|
| **Owner-run lab, real TPM** (2026-10-17 and 2026-10-18, Windows 11 Pro 25H2, Intel firmware TPM 2.0) | Service install under its virtual account; TPM key provisioning (decrypt-only, export refused); vault-root ACL gate; startup authentication of every project; tamper drill (stray file and flipped manifest bit refused, vault restored byte-for-byte); pipe DACL refusing a non-operator; the installed lab client's end-to-end check, including a refused forged capability and a refused byte-identical client copy outside Program Files; hash pins and the PID-reuse check accepting an untouched client |
| **In-process tests** (`scripts/Verify.ps1`, 2026-10-19) | Record codec 76, revision store 79, endpoints over real local pipes 50, builds against a fake Docker engine 16, packaging 12, editor host policy 67 and editor web 74 checks, plus contract, IPC, storage-root and service tests. Software stand-ins replace the TPM keys in these tests |
| **Native probe** (2026-10-19) | The editor's browser layer: 0 TCP connections and 0 UDP packets reached loopback listeners from a deliberately unrestricted test page |
| **Not yet verified** | Reboot persistence, power loss, the installed editor and importer against the installed service, package import with real TPM keys, real Docker builds, the offline deployment drive, the any-file vault at scale, capture exclusion against real capture tools, and the remaining native editor checks |

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
   │          + import key       (encrypted stores) │
   │                                                ▼
   │                               SourceVault build service (NT SERVICE\SourceVaultBuild)
   │                                                │ Docker Engine API (pipe, or loopback TLS)
   │                                                ▼
   │                               administrator's build containers ──► artifact folder (plaintext)
   │
 Another computer: SourceVault Pack ──► encrypted, signed package (.svpkg) ──► importer ──► service
```

- **Vault service.** One Windows service under a virtual account. It holds the only handle to the TPM vault key, owns the vault folder, and serves protocol v2 on a local pipe. It is the only component that decrypts.
- **Vault root.** A folder (default `C:\ProgramData\SourceVault\vault`, or a fixed internal drive chosen at install) with an exact, validated ACL. It holds an encrypted project index and one encrypted store per project.
- **Editor.** A WPF host with a WebView2 renderer running the vendored Monaco editor. Only the host talks to the service; the renderer sees project and file ids, names and the text of files it opens.
- **Importer and Pack.** The importer brings a local folder into a new project and verifies it. Pack, on another computer, encrypts a folder to this PC and signs it with that computer's TPM.
- **Build service.** A second service identity that alone holds Docker access. It runs builds in administrator-configured containers and never touches the vault or the TPM.
- **Demo mode.** `SourceVault.Editor.exe --demo` runs the editor on an in-page simulated vault for presentations. **Nothing in demo mode is encrypted**, it never connects to the service, and it says so in red throughout.

---

## 4. Threat model

### 4.1 Assets

1. Source code and its history.
2. File and project names (they can reveal what a project is).
3. The TPM vault key and the import key, and their use.
4. The integrity of the vault: a change must be detected, not silently used.

### 4.2 Trusted

- **Administrators, the operating system, firmware and the TPM.** Anyone with local administrator rights can run code as the service and use its key. SourceVault does not defend against them, and says so (AGENTS.md, user guide).
- **Operators**, to read every project. Every member of *SourceVault Operators* can open every project, by owner decision (2026-10-18); there are no per-project permissions.
- **The installation**, as provisioned by an administrator: Program Files, the configuration and the vault root's ancestors are not writable by ordinary users.

### 4.3 In scope (what SourceVault is designed to resist)

| Adversary or event | Intended outcome |
|---|---|
| Another, non-operator user of the same PC | Cannot open the pipe (DACL) or the vault folder (ACL). Sees no names or content |
| The vault disk or vault folder copied or stolen on its own | Ciphertext only; the keys are in this PC's TPM, which does not export them |
| Bit-flips, truncation, swapped or stray files in the vault | Detected; the operation or the service start is refused, never silently repaired |
| A program other than the installed SourceVault clients, run by an operator | Refused by the executable path and hash checks (defence in depth; see 7.4) |
| A stolen or forged session capability | Refused; a capability is bound to its connection, user and logon session |
| A package intercepted or swapped in transit | Only this PC can decrypt it; only registered senders' signatures are accepted |
| Accidental export by an operator (copy and paste, drag, print, screenshots, the editor reaching a website) | Made harder by the editor's lockdown (section 8) |
| A process crash during a save | The project always reopens at a complete snapshot |

### 4.4 Out of scope

- **Local administrators**, SYSTEM, and anyone who can install drivers or services.
- **Malware or another process running as the operator**, including debuggers, code injected into an allowed client, a replaced WebView2 runtime, and a process listening on the editor's dead proxy port.
- **Intentional exfiltration by an operator.** An operator can read code and retype it elsewhere, photograph the screen, or write a build script that copies source into the build output.
- **Physical attacks on the TPM or the running machine** (bus sniffing, cold boot, DMA).
- **Rollback of a whole valid old vault.** Restoring an entire earlier copy of a project folder is not detected (storage contract v1).
- **Side channels, cameras, and capture tools that ignore window display affinity.**
- **Availability.** Losing the TPM loses the vault, by design.

### 4.5 Open finding: a stolen whole PC (not just its disk)

SourceVault does not require BitLocker or Secure Boot on the Windows volume. A thief with the **whole PC** cannot read the vault from the disk alone, because the key never leaves the TPM. But if the Windows volume is unencrypted, they can modify it offline, for example by planting a service, and on the next boot run code as an administrator on that same TPM, which is outside this model and gives access to the key.

**Status (2026-10-20):**
- **The deployment PC has BitLocker and Secure Boot on.** This is as reported by the owner; it is not yet confirmed by a recorded check.
- **`Setup-SourceVault.ps1 -Status` now reports both,** for the Windows drive and the vault drive, and warns if either is off. Running it on the vault PC is a lab row.
- **Every vault PC needs them.** SourceVault reports them but does not enforce them.
- **Still recommended:** a BitLocker startup PIN. With TPM-only unlock, Windows starts without one, and the remaining attacks need the running machine (outside this model).

---

## 5. Cryptographic design

### 5.1 Keys

| Key | Where it lives | Used for | Lifetime |
|---|---|---|---|
| **Vault key** | TPM 2.0, Microsoft Platform Crypto Provider, RSA-2048, non-exportable, decrypt-only (`PCP_KEY_USAGE_POLICY` encryption-only), under `NT SERVICE\SourceVault` | Wrapping the data keys of manifests and project-key records (RSA-OAEP-SHA256) | The life of the TPM. Created once by an explicit administrator step; never overwritten, never created during normal operation |
| **Import key** | TPM, as above, a separate key | Unwrapping package keys only. Kept apart so that outsiders' data never reaches the vault key (decision D1) | As above. If it can't be opened, package import is unavailable and the vault still runs |
| **Sender key** | The source computer's TPM, non-exportable | Signing packages (RSA-PSS-SHA256) | Registered on the vault PC by an administrator after a fingerprint comparison |
| **Project key** | An encrypted `ProjectKey` record, wrapped by the vault key | Wrapping the data keys of a project's file and history records (AES-256-GCM) | Unwrapped at most once per store operation, under the project lock, zeroed when the operation ends; never cached, logged or sent |
| **Data key** | Wrapped inside each record | Encrypting exactly one record | Fresh and random for every record; never reused |
| **Package key** | Wrapped to the import key in the package header | Encrypting one package | One package |
| **Session capability** | Service memory and the client | Authorising requests on one connection | 256 random bits; 5 minutes idle, 30 minutes absolute |

**No recovery, by design.** The owner decided (2026-10-18) that there is no recovery envelope, escrow or key export. Clearing the TPM, replacing the motherboard or deleting the service profile makes the vault permanently unreadable. **Key rotation** is out of scope: the key never leaves the TPM, and anyone who could misuse it is an administrator, whom the model already trusts.

### 5.2 Records

Every stored object (file content, history, manifest, project key, project index, build log) is an encrypted record.

- **`SVR1`** (written until 2026-10-19, still read): one AES-256-GCM block with a random 96-bit nonce; the data key is RSA-OAEP-SHA256-wrapped by the TPM key. The AAD is the 74-byte fixed prefix (magic, kind, project, file and revision ids, lengths, nonce) followed by the wrapped key. Text only, at most 256 KiB.
- **`SVR2`** (written now; coordinator-reviewed): a segmented construction for any content up to 1 GiB.
  - A 68-byte prefix: magic, kind, wrap mode, project, file and revision ids, plaintext length, segment size (512 KiB).
  - The wrapped data key. **Wrap mode 1** (manifests, project keys): RSA-OAEP-SHA256 by the TPM key. **Wrap mode 2** (files and history): AES-256-GCM by the project key, with a random nonce and AAD = prefix ‖ project key id.
  - 512 KiB AES-256-GCM segments. Segment *i* uses nonce 0⁴ ‖ *i* and AAD = SHA-256(prefix ‖ wrapped key) ‖ *i* ‖ last-segment flag. Reordering, splicing between records, truncation and dropping the last segment are therefore refused. An empty plaintext is one empty segment.
  - The exact record size is checked before any key is unwrapped.
- **Both formats:** the caller supplies the expected context (project, file, revision, kind) and it must match the header; a record can't choose its own identity. Data keys and failed plaintext are zeroed in `finally`. Errors are generic.

**Why a per-project key.** One TPM unwrap takes about 45 ms on the test machine's firmware TPM, and the TPM serves one command at a time. Wrapping every record directly would make a build or search of a 25,000-file project take about 19 minutes of unwraps. The project key reduces that to one or two unwraps per operation. **The trade-off, accepted by the owner:** during an operation that reads content, one key in service memory can decrypt the whole project.

### 5.3 Manifests and integrity binding

Each project's current state is an encrypted manifest (schema 4): file ids, encrypted names, revision ids, sizes, kind, the SHA-256 of each file's plaintext, and the SHA-256 of each referenced encrypted record. `HEAD` holds only the manifest's id. Because the manifest is itself an authenticated record, it binds every record it references.

- **Every operation** checks that each referenced record exists, has its exact expected size and is not a reparse point.
- **Every content read** also checks the record's SHA-256 at the end of the stream, and every segment is authenticated before use.
- **A full check** (`VerifyProject`) decrypts every file; the importer runs it after every import.
- **Startup** authenticates the project index and every project's manifest, and checks every record's existence and size. A damaged index or an unexpected entry refuses the whole service (code 11). Since 2026-10-20 a project that fails is taken **offline** (listed as unavailable, refusing everything but deletion, reported by id to administrators) and the rest of the vault starts. Since decision C4 (2026-10-19), a same-size change to a file record is caught when that file is read rather than at startup; a changed manifest is still refused at startup.

### 5.4 Packages

A package (`.svpkg`) carries a project from another computer.

- A random package key, RSA-OAEP-SHA256-wrapped to the vault PC's **import key**, recorded with that key's fingerprint.
- Contents in 1 MiB AES-256-GCM chunks bound to the header and the chunk index.
- An **RSA-PSS-SHA256 signature** by the sender's TPM key over SHA-256 of the header and all chunks. The signature is checked against the hash of the bytes as they arrived **before anything is decrypted**, and the sender must be registered in an administrator-only folder.
- **Version 2** (coordinator-reviewed) keeps this construction, raises the limits to the vault's, and is streamed: Pack reads files as it writes and stops if one changes; the service spools the ciphertext in its staging folder, verifies the signature, then re-hashes the spool while importing.
- **People compare a check code,** the first 96 bits of the fingerprint as six groups of four characters. A key with a matching code takes about 2⁹⁶ attempts to make. Software always compares all 256 bits.
- **Be aware:** the recipient file is public, so anyone can encrypt *to* this PC, but only registered computers' packages are accepted. A package can be imported more than once; each import creates a new project and never overwrites one. A signature proves which registered computer packed the files, not who wrote them.

### 5.5 Algorithms

| Purpose | Algorithm | Implementation |
|---|---|---|
| Content encryption | AES-256-GCM, 96-bit nonces, 128-bit tags | .NET BCL (`AesGcm`) |
| TPM key wrap | RSA-2048 OAEP with SHA-256 | Windows CNG, Microsoft Platform Crypto Provider |
| Project-key wrap | AES-256-GCM, random nonce | .NET BCL |
| Package signature | RSA-2048 PSS with SHA-256 | Windows CNG (TPM) to sign; .NET BCL to verify |
| Hashes | SHA-256 | .NET BCL |
| Randomness | OS CSPRNG | .NET `RandomNumberGenerator` |

No custom primitives. The service and storage projects use only the .NET base class library; there are no third-party cryptographic packages.

---

## 6. Storage integrity and durability

- **Immutable revisions.** Each save writes new encrypted records with `CreateNew` and a new manifest; nothing is overwritten. History is kept for the life of the project.
- **Atomic publication.** Records and the manifest are flushed, then `PREVIOUS` and `HEAD` are replaced with single renames and `HEAD` is flushed. An interruption leaves the complete old or the complete new state. This replaced `File.Replace` after a kill test found it could leave no `HEAD` (2026-10-19).
- **Evidence:** a child process is killed at random points 50 times per run, and the store always reopens at a complete snapshot. That is process-crash evidence. **Power loss is not yet tested.**
- **Fail closed.** The store never falls back to `PREVIOUS` because `HEAD` failed. Malformed or tampered state is refused, and the administrator decides.
- **Not prevented:** rollback of a whole valid old store, and secure deletion. Deleting a project or superseded manifests removes ciphertext with ordinary file deletes.
- **Lab evidence (2026-10-17):** the tamper drill refused a stray file in the vault root (1066/11) and one flipped bit in a project manifest (1066/12), and the restored vault started.

---

## 7. Access control and the service boundary

### 7.1 Service identity

The service runs as the virtual account `NT SERVICE\SourceVault`. It checks its own identity by SID before touching the TPM provider, and refuses keys that are exportable, not hardware-backed, the wrong size or not decrypt-only. Its configuration is read only from beside the executable, at most 4 KiB, with an exact schema; never from arguments, the environment or the working folder.

### 7.2 Vault root

Before opening the key, the service validates the vault root (`TrustedStorageRoot`): a fixed local NTFS or ReFS volume (USB, network, virtual and removable disks refused), no reparse points in the ancestor chain, trusted owners, the exact protected DACL, and per-level checks on who can write. Refusal stops the service with code 10 (or 17 if the drive is missing or BitLocker-locked). **Lab-verified 2026-10-17**, including an unelevated session being denied listing and writing.

### 7.3 The pipe

- `\\.\pipe\SourceVault.v1`, owned by the service account. Its protected DACL gives SYSTEM and *SourceVault Operators* exactly `0x00120083`. Remote clients are rejected, the first instance is protected, and there are at most 4 instances.
- **The client checks the server:** it refuses a pipe not owned by `NT SERVICE\SourceVault` before sending anything.
- **The server checks the client on every frame:** a member of *SourceVault Operators*, with INTERACTIVE and without NETWORK or SERVICE in its token; the same user, logon session and process throughout the connection.

### 7.4 Which programs may connect

- **Executable path:** the client process must be one of the installed clients at its exact path under Program Files (lab-verified: PowerShell and a byte-identical copy elsewhere were refused).
- **Hash pins:** on each connection the service re-reads `install-manifest.sha256` and requires the client's folder, recursively and including the editor's web assets, to hold exactly the listed files with matching hashes. An extra or changed file is refused.
- **PID reuse:** the client process must still be running and must be older than the connection.
- **Startup hooks are off** in every program (`StartupHookSupport=false`), so `DOTNET_STARTUP_HOOKS` can't load code into an installed client.
- **Be aware:** these are defence in depth. An administrator can rewrite the manifest; only files on disk are checked, not the running image; code injected into an allowed client (a debugger, or a .NET profiler set through environment variables) is not stopped. Authenticode signer checks wait for a signing decision.

### 7.5 Sessions

`BeginSession` issues a 256-bit capability bound to the connection, the Windows user and the logon session. A wrong capability closes the connection. Sessions expire after 5 idle minutes or 30 minutes in total (monotonic clock). A Windows lock, sign-out or disconnect closes every vault connection from that Windows session, and the editor locks at once.

### 7.6 Protocol hygiene

Frames are length-prefixed (at most 1 MiB) strict UTF-8 JSON with bounded nesting. Unknown or duplicate fields, comments, non-canonical UUIDs and invalid UTF-8 are rejected. Concurrency is optimistic: a stale snapshot is refused, never merged silently. Errors are fixed codes; they never contain exception text, names or content.

---

## 8. The editor

### 8.1 Host and renderer

The WPF host owns the service connection through `SourceVault.Client`. The renderer (the Monaco page in WebView2) never holds the capability or snapshot ids. Its requests pass `BridgePolicy`: exact message shape, exact argument set per operation, canonical ids, bounded names and text (1 MiB of strict UTF-8, 8 MiB per message, checked before parsing). Anything malformed is ignored without a reply.

### 8.2 What the operator sees, and what never reaches the renderer

- The renderer receives decrypted text only for files the operator opens, and closing a tab drops it. Search and builds decrypt inside the service; only matching lines (search) or build output leave it.
- The editor shows text up to 1 MiB. **Binary files and larger text never reach the renderer:** they show a details panel (kind, size, last change, SHA-256). No protocol operation returns binary content to any client; it leaves the service only through a build.
- **Files from disk** (*Add from disk*, *Replace from disk*): the host shows the native Open dialog, reads the file and streams it to the service. The renderer never sees the path or the bytes. Windows may remember the last folder used in that dialog.

### 8.3 Reducing accidental export

- **Clipboard:** nothing copied in the editor reaches the Windows clipboard. Since 2026-10-20 copy and cut work inside the editor: the text stays in the page's memory, and the Windows clipboard gets only a fixed notice. Pasting text in from other programs is allowed, because it brings code in rather than out.
- **Drops:** files and folders dropped on the explorer are passed to the host unread, and the host reads and imports them. Every other drop, and every drag out, is refused.
- **Offline work:** when the service can't be reached, the user may choose *Work offline*. The editor then turns red and keeps editing only text it already holds in memory, plus new files. Nothing is decrypted or written to disk, and nothing reaches the vault until the service is back and the normal save checks run. A Windows lock ends it.
- **Blocked:** dragging out, the context menu, printing, developer tools, browser shortcuts, downloads, new windows, permission prompts, autofill and password storage. Startup refuses `WEBVIEW2_*` environment overrides and WebView2 registry policy overrides that could apply to the editor.
- **No network, in layers that don't depend on each other:** navigation only to the bundled entry page; a host filter on every resource request from every context; a dead loopback proxy and `MAP * ~NOTFOUND` host resolution; WebRTC removed before page script runs and restricted to the proxy; SmartScreen, crash upload, single sign-on and extensions off; a `default-src 'none'` CSP; and network APIs removed in the page.
  - **Measured (2026-10-19):** from a deliberately unrestricted probe page, loopback listeners received 0 TCP connections and 0 UDP packets, and after 60 s idle the browser process tree held no sockets. A control run with WebView2 defaults reached them, and held TLS connections to Microsoft addresses unprompted.
- **Capture exclusion:** in vault mode the host asks Windows to exclude the window from capture (`WDA_EXCLUDEFROMCAPTURE`) and reports whether it succeeded. **This is best effort.** Cameras, some capture tools and anyone with access to the session can bypass it.
- **Profiles:** the browser profile of a vault-mode run is deleted when the browser exits, and leftovers from crashed runs at the next start. A typed canary left no trace in the profile, Temp or crash dumps in a recorded native check.

### 8.4 Limits

Decrypted text of open files is in the editor's memory and the service's, and Windows may write memory to the page file, hibernation file or crash dumps. None of these controls is a boundary against the operator's own processes, administrators, a debugger or a modified runtime.

---

## 9. Getting code in and out

### 9.1 Folder import

`SourceVault.Import.exe` brings a local folder into a **new** project: any file up to 1 GiB, at most 25,000 files and 8 GiB, links never followed, and well-known build and VCS folders skipped. After importing, it compares every file's SHA-256 with the vault's and asks the service to decrypt every record (`VerifyProject`).

`--delete-source` deletes only files that verified and are still identical, after a typed `DELETE`; each is overwritten with zeros, then deleted. **This is not a secure erase:** SSDs, backups, shadow copies, sync clients and `.git` (which is kept) can retain copies.

### 9.2 Encrypted packages

See section 5.4. **Be aware:** the source computer still holds the code as ordinary files.

### 9.3 Builds: the designed export path

Builds are the one way vault content becomes plaintext files, and they are designed as such.

- **Separation:** Docker access is administrator-equivalent, so it belongs to a second identity, `NT SERVICE\SourceVaultBuild`. That identity has no access to the vault or the TPM, never receives project names, and serves only the vault service over an access-checked pipe. The vault service checks that pipe's owner before sending anything.
- **Administrator-only configuration** (`build.json`): containers, commands, paths, time and size limits. Operators see target names only. A container with network access is refused unless the administrator allows it per target; plain-TCP Docker is refused.
- **The engine is checked:** a Windows engine's named pipe must be owned by SYSTEM or Administrators before anything is sent, and the connection allows identification only. A pipe created by an ordinary user while Docker is down is refused (`EngineUntrusted`). The WSL engine's TLS certificate must chain to the administrator's own CA.
- **The output folder is checked** before every build, like the vault root: no links, and only the build service, SYSTEM and Administrators can change it or any folder above it. Otherwise the build is refused before any source is sent.
- **Output checks:** regular files and folders only, no links, devices, absolute or `..` paths, size and count limits. Source maps, debug symbols, compiler intermediates and inline source maps are dropped and listed in the encrypted build log. Output goes to `<artifactRoot>\<projectId>\<build>-<target>`, so names never appear on disk.
- **Be aware:**
  - a build runs the project's own scripts and any vendored binaries, so anyone who can edit a project can make its build copy source into the output. The administrator controls *where* output comes from, not *what* it contains;
  - the long-lived containers hold plaintext source during a build, and caches or copies can persist in them;
  - builds have not yet run against a real Docker engine.

---

## 10. Deployment and supply chain

- **Offline installation.** `Build-DeploymentKit.ps1` assembles a drive on a connected PC: SourceVault, Pack, Docker, WSL and Ubuntu, and the build images, each pinned and checked by SHA-256 or image digest, plus a manifest of every file. `Deploy-SourceVault.ps1` installs from it on an offline PC.
  - **Be aware:** the kit manifest is not signed, so the drive must stay in trusted custody. Docker's archives are trusted on first use, because Docker publishes no checksums. The Ubuntu image gets no updates on an offline PC.
- **Third-party code.** The service and storage use only the .NET base class library. The editor vendors Microsoft Monaco 0.57.0 and the WebView2 SDK 1.0.4258.31 from their official registries, with SHA-512 hashes and licences recorded (`editor-dependencies.json`); the WebView2 package's Microsoft signature was verified. Nothing is fetched at run time, and there is no CDN.
- **Signing.** SourceVault's own binaries, installer and updates are not yet signed; that waits for a signing decision.
- **Upgrades.** The any-file storage upgrade is one-way: once a project is converted, an older service refuses the vault.

---

## 11. Explicit non-claims

SourceVault does **not** claim:
- resistance to local administrators, SYSTEM, or anyone who can run code as the service;
- runtime executable identity: the client checks are path and file-hash checks, not proof of the running code;
- guaranteed blocking of screen capture;
- prevention of intentional exfiltration by an operator;
- RAM-only processing, or that plaintext never reaches the page file, hibernation file or crash dumps;
- secure deletion;
- protection against rollback of a whole valid old vault;
- recovery of a vault after a TPM clear or hardware loss;
- that demo mode protects anything: it is unencrypted by design.

---

## 12. Logging and privacy

- The service and build service log no source, file or project names, keys, capabilities or build output. Errors are fixed codes.
- Project names exist only in the encrypted index; folders and artifact paths use ids.
- Editor settings store eight display fields only, never names or text.
- Build logs, which can contain source, are stored encrypted in the vault and shown read-only in the editor.

---

## 13. Residual risks

| Risk | Current mitigation | Status |
|---|---|---|
| Stolen whole PC with an unencrypted Windows volume (section 4.5) | BitLocker and Secure Boot on the deployment PC (owner-reported); `-Status` warns if either is off | Mitigated on the deployment PC once the `-Status` lab row confirms it; a startup PIN is still recommended |
| Administrator or same-user malware | Out of scope; documented | Accepted |
| Operator exports code through a build | Output folder and artifact filtering; documented as an export path | Accepted; owner-approval release step not built |
| Project key in memory during a read | Unwrapped once per operation, zeroed after, never cached | Accepted trade-off (owner, 2026-10-19) |
| Plaintext in memory, page file or dumps | Profile deletion; crash dumps not uploaded | Accepted; documented |
| Whole-vault rollback | None | Accepted in storage v1; revisit |
| Unsigned binaries and kit | Hash pins, kit manifest, custody | Open: signing decision |
| Capture tools ignoring display affinity | Best-effort exclusion | Accepted; documented |
| Power loss during a commit | Flushes and atomic renames; process-kill tests | Lab test pending |
| TPM loss | None, by design (no recovery) | Accepted (owner, 2026-10-18) |
| Every operator can read every project | Group membership only | Accepted (owner, 2026-10-18) |

---

## 14. Before this paper can be final

1. **Run the lab checklist** ([lab-checklist.md](lab-checklist.md)): reboot and power loss, the installed editor and importer, package import on real TPMs, real Docker builds, the deployment drive, the any-file vault at scale, capture tools, and the remaining native editor checks. Record each result in `status.md` and update this paper.
2. **Owner decisions** ([next-tasks.md](next-tasks.md)): the operator and administrator threat model, intentional-export policy, supported hardware and OS, signing and release ownership, and whether to add a BitLocker startup PIN (section 4.5; BitLocker and Secure Boot are on the deployment PC).
3. **Independent security audit** by a third party, covering the cryptographic formats (`SVR1`, `SVR2`, manifests, packages v1 and v2), the service's access checks, the editor's lockdown and the build path. Development-time reviews by the coordinator and workers are not an audit.
4. **Measurements:** TPM and AES-GCM throughput at 25,000 files, replacing this paper's estimates.

---

## Appendix A. Where each topic is specified

| Topic | Document |
|---|---|
| Protocol, pipe, sessions, error codes | `contracts/protocol-v2.md` |
| Record format SVR1, store contract | `contracts/storage-v1.md`, `docs/record-codec.md`, `docs/revision-store.md` |
| SVR2, project key, any-file limits | `docs/vault-any-file-design.md` |
| Service identity, key provisioning, vault root, startup checks | `docs/service-host.md`, `docs/vault-location-design.md` |
| Client checks (PID reuse, hash pins) | `docs/hardening-design.md` |
| Editor lockdown and capture exclusion | `docs/editor-host.md`, `docs/editor-vault-design.md` |
| Packages and Pack | `docs/package-import-design.md` |
| Builds | `docs/files-history-builds-design.md` |
| Offline deployment | `docs/deployment-kit-design.md` |
| Evidence and open items | `docs/status.md`, `docs/lab-checklist.md`, `docs/next-tasks.md` |

## Appendix B. Glossary

- **Operator:** a Windows user in *SourceVault Operators*; can read and edit every project.
- **Capability:** a random session token issued by the service for one connection.
- **Record:** one encrypted file in the vault (content, history, manifest, key or index).
- **Manifest:** the encrypted list of a project's current files and their record hashes.
- **Package:** an encrypted, signed project made by Pack on another computer.
- **TPM:** Trusted Platform Module 2.0, the chip that holds the vault and import keys.
