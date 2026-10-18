# Editor on the vault service — design (proposed, not built)

Status: design approved by the owner 2026-10-17 (decisions at the end). Build steps 1 (client library, controller) and 2 (host `--vault` mode, bridge policy, capture exclusion, profile handling, `Install-Editor.ps1`) are done. Steps 3 (renderer) and 4 (owner smoke) are not started. Step 3 of `pipe-endpoints-design.md`. Builds on protocol v2 (`contracts/protocol-v2.md`), the installed lab client pattern, and the existing synthetic editor (`docs/editor-host.md`). Synthetic data only until the owner decides otherwise.

## Shape

```
renderer (WebView2, msedgewebview2.exe)  ── bridge v2 (web messages) ──  editor host (WPF, SourceVault.Editor.exe)  ── protocol v2 ──  service
  Monaco, UI; sees text only while open                     holds capability, snapshot/revision ids; validates everything        TPM key, store
```

- **Only the host talks to the service.** The renderer processes never open the pipe, and never see the capability or snapshot ids.
- **Two launch modes.** `--synthetic-preview` is unchanged: the in-renderer mock and the bundled sample. `--vault` is new: it connects to the installed service. There is no automatic fallback between them, and the window title and status bar always say which mode is active.
- **Installed like the lab client.** `C:\Program Files\SourceVault\editor\SourceVault.Editor.exe` is the exact path the service already allows, pinned to `..\runtime` (which gains `Microsoft.WindowsDesktop.App` 10.0.13, 96 MB, from the verified workspace SDK). The WebView2 runtime stays the system-installed one, already required today.
- **At startup** the host grants the service limited query on its own process and refuses any pipe not owned by `NT SERVICE\SourceVault`.

## Shared client library

The lab client's `VaultClient` moves to a new `SourceVault.Client` library, used by both the lab client and the editor host, with async calls so the UI thread never blocks on the pipe. It gains an `EditorVaultController`: UI-free and testable against the in-process service. The controller:

- **Holds per-document state** in the host: for each open file, the revision it was opened at.
- **Turns project-wide locking into per-file concurrency.** The service's optimistic lock is project-wide, so saving file B would make a later save of file A conflict, even though A never changed. To save A, the controller lists files; if A's revision is still the one it opened, it saves against the current snapshot. A commit that races in between makes the server conflict, and the controller retries once. A real conflict, where someone else changed A, is reported to the renderer.
- **Handles session loss.** On `SessionExpired`, a closed connection, or a lock (by the user or by the service on Windows lock), it marks the session locked and notifies the renderer.

## Bridge v2 (renderer ↔ host)

Validated on the host by a pure policy (an extension of `PreviewPolicy`), with hostile-input tests like the existing 38.

- **Renderer → host:** `{type: "sourcevault.vault.request", requestId, operation, args}`. Operations: `listProjects`, `createProject{name}`, `listFiles{projectId}`, `openDocument{projectId, fileId}`, `saveDocument{projectId, fileId, text}`, `createFile{projectId, name, text}`, `lock`, `unlock`.
  - Fields are exact; ids are canonical UUIDs; `text` is at most 256 KiB of UTF-8.
  - The whole message is at most 2 MiB, at depth ≤ 4, and must come from the app origin only.
- **Host → renderer:**
  - `{type: "sourcevault.vault.response", requestId, code, result}`;
  - `{type: "sourcevault.vault.locked", reason}`, where reason is `user`, `windows` or `expired`;
  - the existing metadata message, now carrying `mode: "vault"` and `serviceConnected`.
- **What crosses:** document text crosses the bridge only in `openDocument` results and in `saveDocument`/`createFile` arguments. Nothing else carries text. Codes are the protocol's, plus `Conflict` (someone else changed this file since you opened it). The existing four-field preview status messages stay as they are.

## Renderer

- **Session layer.** A new async `vault-session.mjs` implements the same session interface as `state.mjs` over the bridge. `state.mjs` and the mock stay for preview mode. `app.mjs` moves to async calls and picks the session type from the host metadata.
- **New UI:** a project picker; the explorer shows vault files; "New file" and "New project"; and on a conflict, the choice of **Reload** (discard local edits) or **Keep editing** (no merge in v1). Save sends whole-file text, as v2 requires.
- **Unchanged:** the clipboard, drag/drop, context menu and print stay blocked, as already verified natively. The browser clipboard is the system clipboard, so it cannot be allowed "inside the editor only".

## Plaintext handling and limits

- **Where text lives:** document text exists in the host process (briefly, while relayed), in the renderer processes, and in Monaco models. Same-user process inspection is outside the claim, as now.
- **Profile** (decision 2): the WebView2 profile goes in `%LOCALAPPDATA%\SourceVault\profiles\<random>`, because Program Files is not user-writable.
- **Capture exclusion** (decision 1): `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`, reported truthfully in the UI. It is best effort, never a guarantee.
- **Not addressed:** pagefile, hibernation and crash-dump persistence remain out of scope, and the docs say so.

## Build steps

1. **Library:** `SourceVault.Client`, plus the controller and its tests against the in-process service (per-file concurrency, retry, session loss). The lab client moves onto it.
2. **Host:** `--vault` mode, the bridge policy and its tests, capture exclusion, the profile location and cleanup, and the install script (`Install-Editor.ps1`, which adds the desktop runtime).
3. **Renderer:** `vault-session.mjs` with node tests against a fake bridge, the async `app.mjs`, and the picker and conflict UI. The browser preview gains a fake host so the vault UI can be reviewed without the service.
4. **Owner smoke checklist:** native, on this machine — open a synthetic project, edit and save, conflict via the lab client, Windows lock, capture check, and a profile canary scan after exit.

## Decisions (owner, 2026-10-17)

1. **Capture exclusion is on in vault mode.** It is best effort, and the UI says so. The synthetic preview keeps it off.
2. **The vault-mode WebView2 profile is deleted at exit**, and leftovers from crashed runs are deleted at the next start.
3. **Unsaved edits are kept hidden while the session is locked.** They stay in renderer memory behind the lock screen. On unlock, each draft is re-checked against the vault, and a file someone else changed is reported as a conflict.
