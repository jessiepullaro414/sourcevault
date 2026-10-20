# Design: unsaved edits that survive a crash (proposal, 2026-10-20; needs decisions)

**Owner request (2026-10-20):** unsaved work should survive a crash or a forced restart. Today unsaved edits exist only in the editor's memory: an editor crash, a power cut or a Windows update restart loses them.

Status: **design only, nothing built.** It adds a new kind of stored data to the vault, so it needs the owner's decisions and the coordinator's review (AGENTS.md) before any code. Synthetic inputs only.

## 1. What changes for the user

- While someone edits, the editor keeps an **encrypted copy of each unsaved file in the vault**, a few seconds after they stop typing.
- After a crash or a restart, opening the project shows: *"2 files have unsaved edits from 14:32 (before the editor closed). Restore them / Discard them."* Restoring puts the text back as unsaved edits, exactly like today's drafts: nothing becomes a saved version until they press Save.
- Saving, reverting to the saved version, closing the tab or discarding removes that copy.
- Conflicts work as today: if someone saved the file meanwhile, the restored edits are marked *changed by someone else* and can be merged.

## 2. How it is stored

- **One drafts store per project**, an ordinary `RevisionStore` like the build-log stores. It lives at `drafts\<projectId>\` in the vault root, a new top-level folder that the layout check accepts.
  - It has its own project key and `SVR2` records, so the record format is unchanged.
  - It is created on first use and deleted with the project.
- **One record per (person, file)**, overwritten in place. The store keeps no history of drafts. Each manifest entry holds:
  - the file id;
  - the revision the edits are based on;
  - the time;
  - the person, as their Windows SID.

  The manifest is encrypted.
- **Old draft records** are removed with ordinary deletion of ciphertext, as for superseded manifests (decision C5). This is not secure deletion.
- **Size:** text the editor can hold, up to 1 MiB per file. Inline up to 256 KiB, uploads above that, as for saves.

## 3. Protocol (v2 additions)

| Operation | Request body | Success body |
|---|---|---|
| SaveDraft | `projectId`, `fileId`, `basedOnRevisionId`, `contentBase64` (or an upload) | `savedAt` |
| ListDrafts | `projectId` | `drafts[]`: `fileId`, `basedOnRevisionId`, `savedAt`, `byteLength` |
| OpenDraft | `projectId`, `fileId` | `contentBase64`, `basedOnRevisionId` |
| DiscardDraft | `projectId`, `fileId` | `{}` |

- **Ownership:** every operation acts on the caller's own drafts only. The service takes the person from the pipe's client token, never from the request.
- **Saving a file** (`ApplyEdits`, uploads) also discards the caller's draft of that file, in the same commit or right after it.

## 4. Editor

- **Autosave:** the host sends `SaveDraft` 5 seconds after the last keystroke in a file with unsaved edits, and at most every 30 seconds while someone types non-stop. Each send counts as session activity.
- **No send while locked.** The editor has no session then. So a lock or session expiry keeps the last copy sent, which is at most a few seconds old.
- **Unlock and open project:** `ListDrafts`; any draft newer than the file's saved version is offered for restore.
- **Demo mode** keeps drafts in memory only, as now.

## 5. What this does and doesn't protect (be aware)

- **More data at rest, all encrypted.** Today unsaved edits never touch the disk. With this, they are on disk, encrypted under the project's key, like saved files. They are readable by the service and by anyone who can act as it: administrators, as for everything.
- **Per person, not secret from administrators.** Other operators can't read your drafts through SourceVault, even though they can read every saved file.
- **Deleting a draft is not secure deletion,** as for every vault record.
- **Not a backup.** Drafts live in the same vault, on the same TPM.

## 6. Decisions needed

### Owner

- **D1. Whose drafts:** each person's own only (recommended), or visible to every operator, like saved files?
- **D2. When to autosave:** 5 seconds after typing stops, at most every 30 seconds (recommended), or another interval.
- **D3. How long drafts are kept:** until saved or discarded, removing ones older than 30 days unused (recommended); or never removed automatically.
- **D4. Restore prompt:** ask on opening the project (recommended), or restore automatically and say so.

### Coordinator (storage, per AGENTS.md)

- **C1.** A new top-level `drafts\` folder in the vault root, with one ordinary `RevisionStore` per project and no record-format change.
- **C2.** Per-person ownership enforced from the pipe token.
- **C3.** Overwriting a person's draft record and deleting the old ciphertext.

## 7. Tests (synthetic only)

- Store: save, overwrite, list, open and discard; another person's drafts are invisible and refused; the store is deleted with its project; the layout check accepts `drafts\`; ciphertext only on disk (canary search).
- Protocol: ownership comes from the token; a save discards the saver's draft; limits.
- Editor: autosave timing with a fake clock; restore after a simulated crash (a new session); conflicts on restore; demo mode stays in memory.
