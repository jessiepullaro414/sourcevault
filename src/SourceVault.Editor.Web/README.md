# Offline editor frontend

`index.html` is the browser entry point. Serve this directory through the project preview server on localhost, or map it to `https://sourcevault.local/` in the WebView2 host. Do not open it as a `file:` URL: ES modules and Monaco workers require an appropriate origin. All runtime assets are local, including the pinned Monaco distribution in `vendor/monaco/vs`.

This is a synthetic editor demonstration. It makes no service, vault, TPM, encryption, compiler, Docker, or build requests. Source edits, revision snapshots, Monaco models, view state, and undo buffers exist only in the renderer's current session. Refreshing or closing discards them. The bundled synthetic seed itself is an ordinary asset on disk.

The editor deliberately sets Monaco's `editContext: false` option to use its textarea input path. This preserves Windows accessibility and desktop automation compatibility where Chromium's newer EditContext textbox does not expose usable focus or bounds. This input setting does not change session storage behavior.

## Interaction

- Select a sample from the explorer or its open tab. Each file retains its buffer and undo history.
- Edit, undo with Ctrl+Z, and find with Ctrl+F using Monaco.
- Save revision or Ctrl+S checkpoints only the active file in session memory. It does not save a vault revision or write to disk.
- A dirty file cannot be closed until its session checkpoint is saved. Closing a clean tab retains its model for reopening.
- Lock detaches the active Monaco model, hides its view, and focuses Unlock. Unlock resumes existing session state. This does not guarantee memory erasure.
- Reset immediately restores bundled synthetic samples and discards session edits and checkpoints.

Copy, cut, paste, drag/drop, context-menu, and print affordances are blocked on a best-effort basis. These browser controls do not establish a confidentiality boundary. No screenshot protection is claimed by this frontend.

## Test and host boundary

From the repository root, run `node --test tests/editor-web/state.test.mjs`. Tests cover buffer independence, dirty detection, immutable snapshots, stale-save conflict rejection, locked operations, file lifecycle, and reset. Browser rendering and worker loading must also be verified separately.

The optional native bridge receives exactly `{ type, locked, dirtyCount, revisionCount }`, where type is `sourcevault.preview.ready` or `sourcevault.preview.status`. Messages contain metadata only. A host may request a lock with `{ type: 'sourcevault.preview.lock' }`. Native host metadata is not used to assert working security features. The standalone browser mode needs no bridge.

The CSP permits same-origin scripts, fonts, workers, and connection requests (used for local language-worker asset loading), blob worker bootstrap URLs, and Monaco's inline style generation. It excludes third-party origins. Do not weaken it merely to suppress worker warnings: verify the bundled distribution and asset MIME mappings first.
