# Offline editor frontend

`index.html` is the browser entry point. Serve this directory through the project preview server on localhost, or map it to `https://sourcevault.local/` in the WebView2 host. Do not open it as a `file:` URL: ES modules and Monaco workers require an appropriate origin. All runtime assets are local, including the pinned Monaco distribution in `vendor/monaco/vs`.

This is a synthetic editor demonstration. It makes no real service, vault, TPM, encryption, compiler, Docker, or build requests. Its only "service" is the in-memory protocol mock described below. Source edits, revision snapshots, Monaco models, view state, and undo buffers exist only in the renderer's current session. Refreshing or closing discards them. The bundled synthetic seed itself is an ordinary asset on disk.

The editor deliberately sets Monaco's `editContext: false` option to use its textarea input path. This preserves Windows accessibility and desktop automation compatibility where Chromium's newer EditContext textbox does not expose usable focus or bounds. This input setting does not change session storage behavior.

## Interaction

- Select a sample from the explorer or its open tab. Each file retains its buffer and undo history.
- Edit, undo with Ctrl+Z, and find with Ctrl+F using Monaco.
- Save revision or Ctrl+S checkpoints only the active file in session memory. It does not save a vault revision or write to disk.
- A dirty file cannot be closed until its session checkpoint is saved. Closing a clean tab retains its model for reopening.
- Lock detaches the active Monaco model, hides its view, and focuses Unlock. Unlock resumes existing session state. This does not guarantee memory erasure.
- Reset immediately restores bundled synthetic samples and discards session edits and checkpoints.

Copy, cut, paste, drag/drop, context-menu, and print affordances are blocked on a best-effort basis. These browser controls do not establish a confidentiality boundary. No screenshot protection is claimed by this frontend.

## Mock protocol service

`mock-service.mjs` is a pure ES module (no DOM, network, storage, or logging) that imitates `contracts/protocol-v1.md` in memory with synthetic seed files. It is not the real service: nothing is encrypted, durable, or authenticated beyond the mock's own capability check. `state.mjs` routes open, save (`ApplyEdits` with the expected immutable revision ID), close, lock and unlock through it; Monaco models, drafts and dirty state stay renderer-local.

- Envelope `{version, requestId, operation, body}` and response `{version, requestId, ok, code, body}` follow the contract: unknown/missing/extra fields, non-canonical UUIDs, and wrong versions are rejected with the contract error codes; frames over 1 MiB, documents over 256 KiB UTF-8, or nesting over 16 return `LimitExceeded`. A request ID that cannot be echoed is answered with the all-zero UUID (mock-only; the real transport drops the connection). Errors never include exception text.
- `BeginSession` returns a random 256-bit hex capability (`crypto.getRandomValues`). Every later operation carries it in `body.capability` and rechecks it: unknown, ended, or revoked capabilities give `Unauthorized`; 5 minutes idle or 30 minutes absolute (injectable clock) gives `SessionExpired`. Only one live session exists per mock instance.
- Operations: `BeginSession`, `ListProjects`, `ListFiles`, `OpenDocument`, `ApplyEdits` (complete text replacement; stale `expectedRevision` gives `RevisionConflict` and changes nothing; the document must be open), `CloseDocument`, `EndSession`. `RequestBuild` and `GetBuildStatus` return `Unavailable` after authorization.
- `handle(object)` takes parsed envelopes. `handleJson(string)` uses `parseStrictJson`, which also rejects duplicate property names and invalid JSON. On the real wire, duplicate-key rejection is the native transport's job.
- UI behavior: Lock sends `EndSession` (the capability dies) and Unlock begins a new session and reopens the open tabs. Drafts and unsaved edits remain in renderer memory while locked. If the mock session expires or is revoked, the UI locks itself and says so; unlock to continue. Edits send a throttled metadata-only `ListProjects` ping so an actively edited session is not idle-expired. Document text is never sent to the native host bridge.

## Test and host boundary

From the repository root, run `node --test tests/editor-web/state.test.mjs tests/editor-web/mock-service.test.mjs`. State tests cover buffer independence, dirty detection, immutable snapshots, stale-save conflict rejection, locked operations, file lifecycle, reset, and mock-session expiry/lock handling. Mock tests cover envelope rejection, capability checks, fake-clock idle/absolute expiry, revision conflicts, size limits, `Unavailable` build operations, and `EndSession` invalidation. Browser rendering and worker loading must also be verified separately.

The optional native bridge receives exactly `{ type, locked, dirtyCount, revisionCount }`, where type is `sourcevault.preview.ready` or `sourcevault.preview.status`. Messages contain metadata only. A host may request a lock with `{ type: 'sourcevault.preview.lock' }`. Native host metadata is not used to assert working security features. The standalone browser mode needs no bridge.

The CSP permits same-origin scripts, fonts, workers, and connection requests (used for local language-worker asset loading), blob worker bootstrap URLs, and Monaco's inline style generation. It excludes third-party origins. Do not weaken it merely to suppress worker warnings: verify the bundled distribution and asset MIME mappings first.

## Vault mode

The editor host decides the mode. `app.mjs` posts a ready message and waits for the host metadata before rendering:

- `mode: "vault"` starts `vault-app.mjs` on `vault-session.mjs`, which speaks bridge v2 to the host;
- anything else starts the synthetic preview (`preview-app.mjs` on `state.mjs` and the in-renderer mock).

In vault mode this page never sees capabilities or snapshot/revision ids, and never puts document text in status messages.

Demo mode (`SourceVault.Editor.exe --demo`, or `index.html?demo` in a browser) runs this page on `demo-host.mjs`, an in-page simulated vault with fictional data and a panel to simulate events. It is shown in red and nothing in it is encrypted. Vault mode never loads it. Node tests: `tests/editor-web/vault-session.test.mjs`, `workbench.test.mjs` and `any-file.test.mjs` (binary and large files, from disk, revert, keep mine/theirs; the demo vault makes the files up).
