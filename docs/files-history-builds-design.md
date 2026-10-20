# Design: file operations, history, merge and container builds (owner-approved 2026-10-18)

> **Since 2026-10-19** the vault holds any file: the text-only and 256 KiB / 1,024-file limits below are superseded by [vault-any-file-design.md](vault-any-file-design.md).

Owner requests from 2026-10-18:
- add file delete/rename and project rename;
- add per-file history and merging;
- connect builds to the existing Docker containers through an administrator-only config file.

Owner decisions already made:
- **No recovery.** Key loss is device-only.
- **No per-project permissions.** Every member of *SourceVault Operators* can open every project.
- **Builds** run inside **named, already-running containers**. There are two: a Linux container on a Docker engine inside WSL, and a Windows container on the Windows Docker engine.
- **Artifacts** come out to the host as ordinary files.
- **Build logs** are stored encrypted in the vault.
- **Only an administrator** sets the artifact paths.

Status: all four steps are built and tested in process (step 4 against a simulated Docker engine). Not yet run on a real machine: the installed services, Docker engines, the WSL TLS setup. The owner's decisions are at the end.

---

## 1. File delete and rename, project rename

**Storage.** A rename changes only the name in the encrypted manifest. The file id and every revision record stay the same, so no content is re-encrypted. A delete removes the entry from the manifest. What happens to its history depends on decision D2. `RevisionStore` gains `CommitChanges(expectedSnapshotId, changes)`, where a change is `Write(name, content)`, `Rename(fileId, newName)` or `Delete(fileId)`. It is one all-or-nothing commit with the same path rules as today: case-insensitive names, and no path that is both a file and a folder.

**Protocol v2 additions.** Every operation also takes `capability`.

| Operation | Request body | Success body |
|---|---|---|
| RenameFile | `projectId`, `fileId`, `newName`, `expectedSnapshotId` | `snapshotId` |
| RenameFolder | `projectId`, `oldPrefix`, `newPrefix`, `expectedSnapshotId` | `snapshotId`, `renamed` |
| DeleteFile | `projectId`, `fileId`, `expectedSnapshotId` | `snapshotId` |
| RenameProject | `projectId`, `newName` | `{}` |

- **RenameFolder** renames every file under the prefix in one commit, up to 1,024 files.
- **RenameProject** rewrites only the encrypted catalog index.
- **Errors** are the same as today: `RevisionConflict`, `AlreadyExists` and `NotFound`.

**Editor.** Right-click menus stay blocked, so each explorer row gets a **⋯** button with *Rename*, *Delete* and *History*.
- **Rename** edits the name inline. Open tabs follow the file, because they are tracked by file id.
- **Delete** asks *Delete `src/a.ts`?* with a Delete button. It is refused while the file has unsaved edits.
- **Rename project** is in the project list, next to *Delete…*.
- **Someone else renames or deletes your open file.** A rename shows the new name on the next refresh. A delete marks the tab *Deleted by someone else*. Your draft is kept, and *Save as new file* is offered.

## 2. Per-file history

**Today.** Every old encrypted revision record stays on disk, but nothing links it to its file. `PREVIOUS` holds only the one prior snapshot.

**Proposed: manifest schema 3, a storage-format change that needs owner review.**
- Each file entry gains `historyRecordId` and `historyRecordSha256`. These point to an encrypted **history record** for that file, which lists every revision: `{revisionId, recordSha256, byteLength, savedAt (UTC), savedBy (Windows account name), action: created|edited|renamed|imported}`.
- Each save writes a new history record and the manifest points at the newest one. Because the hash chain runs manifest → history record → old revision records, every old revision stays authenticated, exactly like current ones.
- The history record uses the existing record format and codec. Its context is the file id plus a fresh record id, so **no cryptographic format change**.
- The manifest stays small. A file with 1,000 saves has a history record of about 150 KB, read only when someone opens the history.
- Schema 2 manifests are read and upgraded on the next commit. Files committed before the upgrade start their history at that commit, because older revisions were never linked.
- **Retention:** all revisions, for the life of the project. Nothing is pruned, since the store never deletes records. Deleting the project removes everything.

**Protocol additions.**

| Operation | Request body | Success body |
|---|---|---|
| ListHistory | `projectId`, `fileId` | newest first, ≤ 1,000 per page: `revisionId`, `savedAt`, `savedBy`, `byteLength`, `action`, `name` |
| OpenRevision | `projectId`, `fileId`, `revisionId` | `contentBase64` |

**Editor.** *History* opens a list (when, who, size).
- Choosing a revision opens it **read-only**, beside the current version, in Monaco's diff view.
- **Restore** puts that old text into the editor as unsaved edits. Saving makes a new revision, so history is never rewritten.

`savedBy` records which operator saved. It is stored only inside the encrypted history.

## 3. Merging

Today, saving a file someone else changed is a hard conflict. Proposed: a **three-way line merge**.
- **Inputs:**
  - *base*, the revision you opened (fetched with `OpenRevision`, which needs history);
  - *theirs*, the current revision;
  - *yours*, your draft.
- **Clean merge.** If the changes don't touch the same lines, the conflict banner offers **Merge**. The merged text replaces your draft as unsaved edits based on *theirs*, and you review and save.
- **Overlapping changes.** Each overlap becomes a conflict block with *Take mine*, *Take theirs* and *Take both* buttons, and an *N conflicts left* counter. Save stays disabled until none are left.
  - Blocks are tracked by editor decorations, not by text markers, so code that legitimately contains `<<<<<<<` is not confused.
- The merge runs in the renderer: a dependency-free Myers diff and diff3 in `merge.mjs`, with node tests. The service still refuses any save whose base is stale, so a merge can never overwrite unseen changes.
- Binary files don't exist in the vault (text only), so there's no binary merge.

## 4. Builds in the existing containers

### Config file (administrator only)

`C:\ProgramData\SourceVault\config\build.json`. Setup creates the folder:
- **Administrators and SYSTEM:** full control.
- **The build identity:** read (see D3).
- **Operators:** no access.

Editing it requires administrator rights. The service reads it at start and on each build. The editor sees **target names only**. Example: [`installer/build.example.json`](../installer/build.example.json).

```json
{
  "version": 1,
  "artifactRoot": "D:\\SourceVault\\artifacts",
  "targets": [
    {
      "name": "linux",
      "engine": "tcp://127.0.0.1:2376",
      "tls": { "caFile": "C:\\ProgramData\\SourceVault\\config\\wsl-docker-ca.pem", "clientCertificateThumbprint": "<thumbprint in LocalMachine\\My>" },
      "container": "sourcevault-build-linux",
      "platform": "linux",
      "sourcePath": "/sourcevault/src",
      "command": ["/bin/sh", "-c", "sed -i 's/\\r$//' build.sh && exec /bin/sh build.sh"],
      "artifactPath": "/sourcevault/out",
      "timeoutSeconds": 900,
      "maxArtifactMegabytes": 512,
      "allowNetwork": false
    },
    {
      "name": "windows",
      "engine": "npipe://./pipe/docker_engine",
      "container": "sourcevault-build-windows",
      "platform": "windows",
      "sourcePath": "C:\\sourcevault\\src",
      "command": ["cmd.exe", "/d", "/c", "build.cmd"],
      "artifactPath": "C:\\sourcevault\\out",
      "timeoutSeconds": 900,
      "maxArtifactMegabytes": 512,
      "allowNetwork": false
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `name` | What operators pick in the editor. 1–32 letters, digits, `-` or `_`. |
| `engine` | `npipe://./pipe/<name>` for a Windows engine, or `tcp://127.0.0.1:<port>` with **required** `tls` for the WSL engine. Plain-TCP Docker (port 2375, no TLS) is refused: any program on the PC could control it. |
| `container` | Name of the already-running container. The build fails with *container not running* otherwise. |
| `platform` | `linux` or `windows`. Selects path rules and the cleanup command. |
| `sourcePath` | Folder inside the container that receives the project. Emptied before and after every build. |
| `command` | What runs in `sourcePath`. Set by the administrator; operators can't change it. Files arrive without the executable bit, so run scripts through the shell (`/bin/sh build.sh`, not `./build.sh`); the example also strips Windows line endings first. |
| `artifactPath` | Folder inside the container whose contents become the artifacts. Emptied before every build. |
| `artifactRoot` | Host folder for artifacts. Each build goes to `<artifactRoot>\<projectId>\<buildNumber>-<target>\`. Folder names use ids, so project names never appear on disk. |
| `timeoutSeconds` | The build is stopped after this many seconds (up to 3,600). |
| `maxArtifactMegabytes` | Output larger than this is refused, and nothing is published. |
| `allowNetwork` | `false` means the service refuses to build unless the container's network mode is `none`. |

### What a build does
1. An operator picks a project and a target and clicks **Build**. The `RequestBuild` operation returns a build number; the editor then polls `GetBuildStatus`.
2. The service decrypts the project's files and copies them, as an in-memory tar, into `sourcePath` through the Docker Engine API. It talks to the engine directly over the named pipe or TLS socket, using only .NET's built-in libraries; `docker.exe` is not needed.
3. It runs `command` in the container with a timeout and captures the output, up to 1 MiB.
4. It reads `artifactPath` back as a tar and validates it:
   - regular files and folders only;
   - no links or devices;
   - no absolute or `..` paths;
   - within the size and file-count limits.
5. It writes the artifacts to a fresh folder under `artifactRoot`. They are plaintext, by design. Operators can read them; only the service writes there.
6. It empties `sourcePath` and `artifactPath` in the container.
7. It stores the build log **encrypted** in the vault, in a per-project build store. The editor shows it read-only, with copy blocked as everywhere. Status, the build number and the artifact folder path are also shown.

Each target runs one build at a time, with up to 4 queued. Builds of a deleted project are cancelled.

### What this does and doesn't protect (be aware)
- **Builds are an export path.** The build runs the project's own scripts, so anyone who can edit a project can make its build copy the source into the artifacts, which land in plaintext on the host. The admin-only paths control *where* artifacts come from, not *what* they contain. With `allowNetwork: true`, a build could also send source over the network.
- **The containers hold plaintext source during a build.** They are long-lived, so compiler caches, package caches or anything the build script copies elsewhere can **persist** inside the container's disk. Emptying `sourcePath` is best effort, not erasure. Whoever can access those containers or their Docker engines can read that.
- **The output folder is checked (2026-10-20).** Before every build, `artifactRoot` and every folder above it must pass the vault root's checks, with the build service, SYSTEM and Administrators as the trusted principals: no links, a protected ACL, and nobody else able to change, rename or replace them. Otherwise the build is refused before any source is sent (`ArtifactFolderUntrusted`). The project's own output folder is checked for links again right before writing.
- **The Windows engine's pipe is checked (2026-10-20).** The build service connects to `npipe://` engines for identification only, and refuses a pipe not owned by SYSTEM or Administrators before sending anything (reason `EngineUntrusted`). Otherwise a local user could create the pipe while Docker is down and receive the source. The TCP engine was already protected by its certificate check.
- **Docker access is administrator-equivalent.** Whatever account talks to a Docker engine can start containers that mount `C:\`. Giving that right to the TPM-key service would make the key service admin-equivalent. That is why D3 proposes a separate build identity.
- **The WSL engine is per-user.** WSL distros run under your Windows account and stop when idle or when you sign out, so the Linux target works only while that distro and its Docker engine are running. Reaching it from a Windows service needs Docker's TCP listener with TLS client certificates, which you configure in the distro (a lab script will be provided).
- The service's own logs never contain source, file names or build output.

## Build order

Each step is tested and committed before the next:
1. File rename/delete, folder rename, project rename. No format change.
2. History: manifest schema 3, history records, and the history and diff view.
3. Merge. Needs `OpenRevision` from step 2.
4. Builds: config, Docker Engine client, artifact validation, encrypted logs, the editor Build panel, setup/lab scripts. Tested against a fake engine in-process, then lab-run by you against the real containers.

## Decisions (owner, 2026-10-18)

- **D1: approved.** Manifest schema 3 with per-file history records.
- **D2: keep.** A deleted file's history is kept and can be restored from *Deleted files* in its project.
- **D3: separate build service.** `NT SERVICE\SourceVaultBuild` holds Docker access; the vault service never does.
- **D4: yes.** The WSL Linux target uses Docker's TCP listener with TLS client certificates, set up by a lab script.

The questions as they were asked:

- **D1:** Approve manifest schema 3 with per-file history records. It is a storage-format change but uses the same record codec.
- **D2:** When a file is deleted, is its history kept (restorable from *Deleted files* in that project) or dropped from the history list? Either way the encrypted records stay on disk until the project is deleted, as today.
- **D3:** Who talks to Docker? (a) A separate service, `NT SERVICE\SourceVaultBuild`, holds Docker access, receives the source from the vault service over a private pipe and never touches the TPM key (recommended). (b) The vault service itself.
- **D4:** The WSL Linux target requires the Docker TCP listener with TLS client certificates. Is that acceptable, or should Linux builds wait until the Windows engine can run them?
