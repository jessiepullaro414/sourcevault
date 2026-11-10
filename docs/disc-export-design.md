# Design: writing a project to a finalized disc, with two approvers (2026-11-10)

**Owner request (2026-11-10):** a way to write source to a disc and finalize it.
- Choosing the option shows a message that explains you must be approved.
- The person burning must be in the "disc burners OU".
- Two people approve, and the disc is signed with their two names.

**Owner decisions (2026-11-10):**

| # | Question | Decision |
|---|---|---|
| E1 | How approvers approve | **Each signs in at the PC** with their Windows account and their own SourceVault password, which the service checks. They are two different people, and neither is the burner |
| E2 | Who may approve | **A separate group,** *SourceVault Disc Approvers*. Approvers are also operators, because the SourceVault password belongs to operators |
| E3 | What goes on the disc | **Plain source files:** the project's saved files, readable on any computer |
| E4 | How the disc is signed | **A signed record on the disc.** It lists the project, every file's SHA-256, the burner, both approvers and the time, and is signed by a new signing key in this PC's TPM |

**About the OU.** Windows can't check membership of an Active Directory OU: an OU is a folder of objects, not a group. As with *SourceVault Operators*, the service checks two local groups, *SourceVault Disc Burners* and *SourceVault Disc Approvers*. In a domain, the administrator adds the AD group that holds the OU's people to the local group. Setup can add accounts or groups (`-AddDiscBurner`, `-AddDiscApprover`).

**This is a new export path.** Until now, code left the vault only as build output (whitepaper 9.3). A disc made this way holds the project's source in plain, readable form. SourceVault controls who may make one, and records that it was made. It can't control the disc afterwards.

---

## 1. The flow

1. **The burner chooses *Write to disc…*** in the editor, from the project's explorer.
   - The editor first shows the **approval message** (section 5).
   - If the burner isn't in *SourceVault Disc Burners*, the message says so, and nothing else happens.
2. **Prepare.** The burner picks a disc drive with a blank, write-once disc in it. The service then:
   - checks the burner's group from the pipe's client token, never from the message;
   - checks the disc: write-once media only (CD-R, DVD±R, DVD±R DL, BD-R), blank, and big enough;
   - fixes the project's **current saved snapshot**. The disc gets exactly that, and nothing else.

   The service returns a burn id, the number of files and the size. The volume label is made at write time, from the approvers' names.
3. **Two approvals.** On the same screen, each approver types their Windows account (for example `PC\alice`) and their own SourceVault password. For each one, the service checks:
   - the password, against that account's stored hash. Wrong tries count toward a **separate approval lockout** for that account: 5 tries, then 15 minutes. A burner guessing can lock approvals by that person, but never their own SourceVault unlock;
   - membership of *SourceVault Disc Approvers* and *SourceVault Operators*, resolved for that account (section 3);
   - that it is not the burner, and not the first approver.

   Approvals are tied to the burn id and its snapshot, used once, and expire after 15 minutes.
4. **Write and finalize.** With two approvals, the burner clicks **Write and finalize disc**. The service then:
   1. re-checks the burner's session and group, that the project hasn't changed since Prepare, and that both approvals are still valid;
   2. builds the signed record (section 4);
   3. writes the files and the record to the disc, and **closes the disc** so nothing can be added;
   4. reads the disc back and compares every file's SHA-256 with the record.
5. **The result.** The editor shows the disc id, the record's check code, and *Verified* or why not. The service stores an encrypted burn record in the vault, whether the burn succeeded or failed (section 6).

Each step that fails says why, with a fixed code (section 7). Nothing is written to the disc until step 4.

## 2. Who writes the disc: the service, with Windows' own burning engine

- **Only the vault service decrypts,** so it also writes the disc. The editor never receives the files' bytes: no protocol operation returns binary content to a client, and this doesn't add one.
- **The burning engine:** Windows' built-in IMAPI2 (Image Mastering API v2), through COM from the .NET base class library, with no third-party package.
  - The disc image is ISO 9660 + Joliet + UDF.
  - Each file is added as a stream that decrypts from the snapshot as IMAPI reads it, so the plaintext isn't staged as files on this PC's disk.
  - Writing uses `IDiscFormat2Data` with `ForceMediaToBeClosed = true` (finalize).
- **Unverified until the lab run:**
  - that IMAPI2 works from the service's virtual account and its non-interactive session;
  - that it doesn't spool the image to a temporary file;
  - how long a 4.7 GB DVD takes;
  - reading the disc back right after closing it.

  These are lab rows (section 9). If IMAPI can't run in the service, the fallback is a design change, not a quiet workaround.
- **Rewritable discs are refused** (CD-RW, DVD±RW, DVD-RAM, BD-RE). They can be erased and rewritten, which defeats finalizing.

## 3. Checking an approver who isn't the signed-in user

The approver isn't the Windows user running the editor, so there is no token for them on the pipe. The service:
1. **Resolves the typed account** to a SID (`NTAccount.Translate`). An unknown account is refused.
2. **Checks the SourceVault password** for that SID with the same PBKDF2 check and lockout as session start (`OperatorPasswords.Verify`). An account with no SourceVault password is refused (`PasswordNotSet`): approvers must have unlocked SourceVault at least once.
3. **Works out the groups** the account belongs to with Windows' AuthZ API (`AuthzInitializeContextFromSid`, local resource manager). That includes local groups, and domain groups nested in them. It must include *SourceVault Disc Approvers* and *SourceVault Operators*.
4. **Refuses** the burner's own SID, and a SID that has already approved this burn.

**Be aware:**
- The approver types their SourceVault password on the burner's PC. Malware running as the burner could capture it. That is the same limit as for every SourceVault password (whitepaper 4.4).
- AuthZ group resolution for domain accounts needs the domain to be reachable. If it can't resolve them, approval is refused, never assumed.

## 4. The signed record on the disc

Files written at the disc's root, next to the project's files, which go in the folder `source\` at their vault paths (a fixed name, so a project name never has to fit a disc file system's rules):

| File | Content |
|---|---|
| `SOURCEVAULT-DISC.json` | The record (below), UTF-8, written once and signed byte-for-byte |
| `SOURCEVAULT-DISC.sig` | RSA-PSS-SHA256 signature over SHA-256 of the exact bytes of `SOURCEVAULT-DISC.json`, by the disc signing key |
| `SOURCEVAULT-DISC-KEY.der` | The signing key's public key (SubjectPublicKeyInfo), for convenience. **It proves nothing by itself:** a verifier must compare its check code with the one this PC's administrator publishes |
| `README-SOURCEVAULT.txt` | Plain words: what the disc is, who approved it, and how to verify it |

**The record**, version 1:

```json
{
  "format": "sourcevault-disc", "version": 1,
  "discId": "<uuid>", "createdUtc": "2026-11-10T14:03:22Z",
  "computer": "<this PC's name>", "signingKey": "<SHA-256 fingerprint of the public key>",
  "project": { "name": "<project name>", "snapshotId": "<uuid>" },
  "burner":    { "account": "PC\\carol", "sid": "S-1-5-..." },
  "approvers": [ { "account": "PC\\alice", "sid": "S-1-5-...", "approvedUtc": "..." },
                 { "account": "PC\\bob",   "sid": "S-1-5-...", "approvedUtc": "..." } ],
  "files": [ { "path": "src/main.cs", "length": 1234, "sha256": "<hex>" } ]
}
```

**The disc signing key** is a new TPM key, separate from the vault and import keys:
- RSA-2048, **signing only** (`PCP_KEY_USAGE_POLICY` = signature), non-exportable, under `NT SERVICE\SourceVault`;
- created once by setup (`provision-disc-key`), like the import key;
- if it's missing or unusable, disc writing is unavailable and nothing else changes.

It is separate because the vault key is decrypt-only by design, and a signing key must never be able to unwrap vault keys.

**The check code:** the first 96 bits of the key's SHA-256 fingerprint, as six groups of four characters, as for Pack (whitepaper 5.4). `Setup-SourceVault.ps1 -Status` prints it, and the administrator publishes it to whoever will check discs.

**The volume label:** `SV <yyyy-mm-dd> <approver 1>+<approver 2>`, using the approvers' account names without the domain, cut to 32 characters. The label is a convenience for people. The signed record is what counts.

**Verifying a disc anywhere:** `SourceVault.Pack.exe verify-disc <drive or folder> --check-code <code>`. Pack already runs on other computers. It checks:
1. the key's check code matches the one given;
2. the signature;
3. that every file listed exists with its SHA-256, and that no other files are in the project folder.

It then prints the project, the burner, the two approvers and the time.

**Cryptographic format change:** this adds a signed record format and a new TPM key, so it needs coordinator review before it is built (AGENTS.md). Reviewed with this design: the signature covers the exact bytes, so there is no canonicalization; RSA-PSS-SHA256 is as Pack already uses; the key is sign-only.

## 5. The approval message

Shown when *Write to disc…* is chosen, before anything else:

> **Writing to a disc exports this project's source.**
> You must be approved to do this:
> - you must be in **SourceVault Disc Burners**;
> - **two people from SourceVault Disc Approvers** must approve it here, each signing in with their own Windows account and SourceVault password. Neither can be you.
>
> The disc will hold this project's saved files as ordinary, readable files. It will be **finalized**: nothing can be added or erased afterwards. It will carry a record signed by this PC, with your name and both approvers' names.
> Keep the disc as safe as the code itself. SourceVault can't protect it once it leaves this PC.

If the burner isn't a disc burner, the screen shows the message with *You are not in SourceVault Disc Burners. Ask an administrator.*, and stops there.

## 6. The burn log

- **Every attempt is recorded,** in an encrypted `exports` store in the vault, kept like the accounts store and checked at startup.
- **What's recorded:** the disc id, the project, the snapshot, the burner, both approvers, the drive's name, the media type, the time, the record's SHA-256, and the result.
- **Who sees it:** administrators, through `-Status`, which shows counts and the last burn and never file names. Operators see their own burns in the editor.
- **Nothing is logged in the clear.**

## 7. Protocol (protocol v2, additive)

| Operation | Request body | Success body |
|---|---|---|
| ListDiscDrives | `capability` | `allowed` (caller is a disc burner), `checkCode`, `drives[]`: `driveId`, `name`, `media` (`type`, `writeOnce`, `blank`, `freeBytes`) |
| BeginDiscBurn | `capability`, `projectId`, `driveId` | `burnId`, `fileCount`, `totalBytes` |
| ApproveDiscBurn | `capability`, `burnId`, `account`, `password` | `approver` (account name), `approvals` (0–2) |
| StartDiscBurn | `capability`, `burnId` | `{}`, then poll |
| DiscBurnStatus | `capability`, `burnId` | `state` (`waiting`, `writing`, `closing`, `verifying`, `done`, `failed`), `progress` (0–100), `reason`, `discId`, `verified` |
| CancelDiscBurn | `capability`, `burnId` | `{}`; a burn that has started writing can't be cancelled |

**New codes:**
- `NotDiscBurner`, `NotDiscApprover`, `SameAsBurner`, `AlreadyApproved`;
- `ApprovalsMissing`, `ApprovalExpired`, `ProjectChanged`;
- `DiscUnavailable`: no signing key, or no IMAPI;
- `NoMedia`, `MediaNotWriteOnce`, `MediaNotBlank`, `MediaTooSmall`;
- `BurnFailed`, `VerifyFailed`.

The password codes are reused for approvers: `WrongPassword`, `PasswordLocked`, `PasswordNotSet`.

**Limits:**
- one burn at a time per PC;
- a burn id lives 30 minutes, approvals 15;
- at most 25,000 files and the media's free space.

## 8. What this does not do (non-claims to add to the whitepaper)

- **The disc can't be protected once it exists.** Finalizing stops changes to *that* disc, not copying it.
- **The signature proves this PC's service made the record and that two approvers signed in.** It doesn't prove who physically holds the disc. If the check code isn't compared, it doesn't stop someone from making a look-alike disc with their own key.
- **Approvers can be pressured or deceived;** the service checks identities, not intentions.
- **Plaintext passes through the burning engine and the drive.** Like build output, it is plaintext once written.

## 9. Testing

- **In process:**
  - the approval rules (group, burner, duplicate, expiry, snapshot change, lockout), with a fake group resolver and a software signing key in the tests only;
  - record building and signature verification, including any changed byte failing;
  - `verify-disc` against a folder;
  - the editor flow against the demo vault, with a simulated drive;
  - the bridge's exact operation shapes.
- **Lab, owner-run** (needs a disc drive and blank discs):
  1. provision the disc key; `-Status` shows its check code;
  2. a burner who isn't in the group is refused with the message;
  3. two approvals, then write and finalize a CD-R with `dotnet-hello`; the read-back verifies;
  4. `verify-disc` on another PC with the check code;
  5. a DVD+RW is refused;
  6. a third attempt with the same approver twice is refused;
  7. search Temp and the vault folder for the sample's text: no plaintext copy.

## 10. Build order

1. Contracts: this protocol, plus the record format in `contracts/disc-record-v1.md`.
2. Service:
   - the disc key (`provision-disc-key`) and the signer;
   - group checks for the burner (token) and approvers (AuthZ);
   - `OperatorPasswords.Verify`;
   - the burn state machine;
   - the record;
   - the encrypted burn log.
3. The disc writer behind an `IDiscWriter` interface: IMAPI2 in production, a fake in tests.
4. Client controller, editor host bridge operations, and the editor's *Write to disc* view, with the message and the approval slots.
5. Pack `verify-disc`.
6. Setup: groups, `-AddDiscBurner` and `-AddDiscApprover`, provisioning the disc key, and `-Status` lines.
7. Docs: user guide, protocol, whitepaper (a new export path, non-claims), lab rows.

## 11. Status (2026-11-10)

**Built, in process:**
- the service flow, with a fake drive, a software signing key and fake accounts in tests;
- the record format and `verify-disc`;
- the editor screen and the demo simulation;
- setup: groups, people, the disc key, and `-Status`.

**Tests:** endpoints 60 (4 for discs), web 90 (2 for discs), editor host 71 (1 for discs). The editor screen was also checked in the demo editor.

**Not yet verified,** and needing the lab run with a disc drive:
- IMAPI2 from the service account;
- AuthZ group resolution;
- the TPM disc key;
- a real burn and read-back.
