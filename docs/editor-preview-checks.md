# Editor preview review and acceptance checks

The editor milestone is a synthetic UI preview. Passing these checks does not authorize real-source import, decrypting service endpoints, or deployment. Browser preview and native WebView2 checks are separate: a working browser tab does not establish native host policy.

## Checks that can run during development

- Compile the native host against the pinned local WebView2 package; restore must use the repository's local-only package feed. Inspect launch scripts for debugging ports, security-disabling switches, executable/runtime overrides, and unbounded path input.
- Review first-party web files for `fetch`, network URLs, clipboard access, file pickers, downloads, storage APIs, source logging, and HTML injection. Inspect the vendored Monaco license/version/hash record separately; a search through upstream minified code is not evidence that a feature is reachable.
- Trace each native web-message handler: exact trusted origin, bounded message length, strict known fields, fixed operation allowlist, and no file, process, arbitrary path, or decryption operation. Invalid messages must not execute actions or expose exception details.
- Trace navigation, new-window, download, permission, external URI, frame, and resource handling. Resource access must resolve to the vendored app assets, not arbitrary adjacent files. Check percent-encoded traversal, query/fragment variations, alternate ports, and hostile lookalike origins against the actual policy implementation.
- Exercise synthetic document selection, edits, dirty state, Save, Find, keyboard shortcuts, lock/unlock, and preview reset. Save must state that its result is memory-only. Lock must stop editing, hide the editor and Find widget, and accurately state that the preview retains session text in memory.
- Load Monaco and language workers offline. Verify there are no requests to CDNs or external servers and that TypeScript/JavaScript language features use local workers. Surface asset/runtime failures instead of silently substituting a production-looking editor.

Do not add tests that merely restate implementation constants. If native policy is extracted into a pure helper, meaningful executable cases should cover origin parsing, resource traversal, and hostile message shapes. UI behavior requires an actual runtime.

## Native checks requiring an interactive Windows lab

1. Launch the documented host command with its normal, capture-enabled synthetic preview. Confirm preview status remains visible, and the native menu, context menus, keyboard shortcuts, drag/drop and print/save paths do not expose exports.
2. Confirm WebView2 developer tools, browser accelerator keys, downloads, external links, permissions, and new windows are blocked under the deployed runtime. Test host startup when environment variables or registry overrides request debugging/runtime options; source-bearing operation must not be enabled by such a launch.
3. Verify edit, Find, selection, indentation, undo/redo, Save, document switching, and lock/unlock work. Confirm unsaved changes and memory-only saving have accurate UI feedback.
4. Confirm native navigation/resource/message rejection with malformed requests and inspect the resulting logs for absence of text and secrets.
5. Test capture protection only in its explicit native mode, recording whether `SetWindowDisplayAffinity` succeeds. Check screenshots/recording on supported Windows builds; the API is best effort and is not a confidentiality guarantee. A browser preview cannot exercise this native API.
6. Inspect the WebView2 user-data directory, temporary files, logs, crash artifacts, paging/hibernation behavior, and worker caches with unique synthetic markers. Disabling web storage in first-party code does not prove that the renderer or operating system cannot persist memory. This inspection is a prerequisite to any later claim about plaintext persistence.
7. Restart the preview and verify synthetic edits disappear. Closing or locking a renderer is not proof that all process memory has been securely erased.

## Scope limits

The preview does not authenticate an installed editor to the key service, load encrypted projects, enforce real revision concurrency, run a compiler, or validate TPM/service/VM isolation. Any displayed build or service capability must be labeled unavailable or preview-only. Monaco provides editing and selected language services; it is not the VS Code extension host and does not provide IntelliSense for every language automatically.

## Review record

The initial host and session state review found no real-source import, crypto fallback, builder endpoint, clipboard API, text logging, or persistent web storage in the first-party files available at review time. The host bridge accepts only bounded preview metadata/status messages, validates the HTTPS virtual origin, and rejects extra or duplicate JSON fields. This is a code inspection, not a native runtime validation.

An initial host issue was reported and addressed in code: startup now refuses nonempty `WEBVIEW2_*` environment overrides, and external drop is disabled. `CoreWebView2Environment.CreateAsync` can inherit browser arguments, executable paths, and profile overrides from the environment or registry. Setting `AreDevToolsEnabled = false` alone does not reject requested remote-debugging arguments. Microsoft documents this behavior in [the CreateAsync API reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2environment.createasync). Environment refusal still requires a native launch check; registry behavior remains a native deployment gate unless explicitly checked.

The completed frontend review found no first-party source logging, runtime network fetching, HTML injection, clipboard API, or persistent browser storage. Synthetic text is represented by Monaco models and memory-only preview session state. Lock detaches the active model and hides the editor; reset disposes the models and recreates the sample state. The UI explicitly says that locking retains session memory and saving does not persist across application close. The host/frontend bridge was reread after integration: both use the same four-field `sourcevault.preview.ready/status` envelope. A transient schema mismatch observed during concurrent implementation is resolved.

The loopback browser preview server binds only `127.0.0.1`, accepts the exact loopback Host and GET/HEAD methods, resolves assets through `realpath` under its fixed web root, and supplies no-store/CSP headers. It has no write or service endpoints. This review does not replace parent-owned browser/worker/layout QA or an interactive native launch.

A native smoke test on 2026-10-17 found copy, cut and paste working despite the code review above. The capture-phase handler only called `preventDefault`, which does not stop Monaco's own textarea handlers from filling or reading `clipboardData`, and Monaco also calls `navigator.clipboard` directly. The fix stops propagation at the window before Monaco's handlers, cancels paste/drop/cut `beforeinput`, and removes `navigator.clipboard` before Monaco loads. Synthetic event checks and real keystrokes confirmed the block in the browser preview, and a manual check confirmed it in the native window. Other native checklist items remain unrecorded.

Persistence canary (checklist item 6, partial), 2026-10-17. The operator typed a unique canary into the native editor, saved a session revision, locked, unlocked and closed. A byte search afterwards (UTF-8 and UTF-16LE, full string and each half) found no canary in the new WebView2 profile (282 files), anywhere under `artifacts/` (2,921 files, including all earlier profiles), in `%LOCALAPPDATA%\CrashDumps`, or in `%LOCALAPPDATA%\Temp` (Claude tooling folders excluded, because the transcript contains the canary; each pass was capped at four minutes). Bundled sample-text markers were also absent from every profile, and a positive-control scan of the assets folder found them. Limits: Chromium stores some data compressed (LevelDB, disk cache), so a raw byte search can miss compressed copies; the pagefile, hibernation file and process memory were not inspected; one run on one machine is not proof of non-persistence.
