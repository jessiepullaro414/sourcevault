# Windows editor preview host

`src/SourceVault.Editor.Host` is a WPF/WebView2 shell for the offline Monaco assets. Run it with exactly `--synthetic-preview`. It has no service, storage, key, or real source access. A normal resizable Windows title bar supplies minimize, maximize, and close controls.

Build or publish the project with .NET 10 SDK and the repository local NuGet feed. The host targets Windows x64; the pinned WebView2 package supplies its loader. Microsoft Edge WebView2 Runtime must already be installed. The preview does not install or download it. Missing-runtime and missing-assets failures display a local notice.

```powershell
dotnet publish src/SourceVault.Editor.Host/SourceVault.Editor.Host.csproj -c Release -o artifacts/editor-preview
./artifacts/editor-preview/SourceVault.Editor.exe --synthetic-preview
```

The published application maps `https://sourcevault.local` to its `assets` directory. Downloads, new windows, permissions, screen capture (`getDisplayMedia`), browser accelerator keys (including browser print/devtools), default menus, external drops, password storage, and autofill are disabled. Startup rejects all nonempty `WEBVIEW2_` environment variables (browser arguments, runtime/profile folders, debugger controls) and the WebView2 registry policy overrides described below. Runtime process launch auditing remains a deployment review gate. These controls reduce accidental export; they do not establish a confidentiality boundary against a process owner, administrator, debugger, modified renderer, or operating-system capture.

## Only the bundled app, no network

Owner requirement (2026-10-19): the editor runs only its own bundled Monaco app and cannot reach any website or network address, in `--synthetic-preview` and `--vault` alike. The host enforces this in layers that do not depend on each other. Decisions are pure functions in `PreviewPolicy` and `NetworkPolicy`, tested in `tests/SourceVault.Editor.Host.Tests`.

1. **What may load (`PreviewPolicy`).**
   - Top-level navigation is allowed only to exactly `https://sourcevault.local/index.html`: no query, no fragment, no other asset on the app origin, no other origin or scheme. Every frame navigation is cancelled.
   - Every resource request goes through the host filter: all URIs, all resource contexts, and all request sources (pages and dedicated workers, shared workers, service workers). Only the app origin and app-origin blob workers are served; anything else gets a host-made 403 and is never sent.
   - External URI schemes (`mailto:`, `ms-settings:`, `file:` and so on) are refused by navigation and cancelled again in `LaunchingExternalUriScheme`, so no other application is launched. Basic-auth and client-certificate prompts are cancelled; certificate errors cancel the request.
2. **Browser arguments (`NetworkPolicy`).** These cover traffic that never reaches the request filter (DNS prefetch, preconnect, WebRTC ICE, browser background services) and a renderer that gets past it. WebView2 starts with:
   - `--proxy-server=http://127.0.0.1:1 --proxy-bypass-list=<-loopback>`: every HTTP(S) and WebSocket connection, loopback included, goes to a loopback port nothing serves. A fixed proxy has no direct fallback.
   - `--host-resolver-rules="MAP * ~NOTFOUND"`: no host name resolves.
   - `--webrtc-ip-handling-policy=disable_non_proxied_udp`: WebRTC may use only the dead proxy. Upstream Chromium spells this `--force-webrtc-ip-handling-policy`; the runtime measured here ignored that spelling, which is passed as well in case a later runtime honours only it.
   - `--disable-background-networking --disable-component-update --disable-domain-reliability --no-pings`.

   The virtual host is served inside the browser and does not use the proxy.
3. **Services that call home.** SmartScreen is off (`IsReputationCheckingRequired = false`). Crash dumps are never sent to Microsoft (`IsCustomCrashReportingEnabled = true`); they stay in the profile, which vault mode deletes after each run. There is no OS-account single sign-on and no browser extensions. The profile is exclusive to this editor's browser process.
4. **WebRTC removed by the host.** WebRTC traffic (STUN, TURN, peer UDP) bypasses the proxy, the request filter and CSP. Before any page script runs, the host's document-created script (`NetworkPolicy.DocumentCreatedScript`) removes `RTCPeerConnection`, `webkitRTCPeerConnection`, `RTCDataChannel` and `RTCIceTransport` from every document. This does not depend on the shipped page.
5. **Renderer.** The `index.html` CSP is `default-src 'none'` with `connect-src 'self'`, `form-action 'none'` and `base-uri 'none'`. A test pins each directive to an allowlist and checks that the CSP comes before every link and script. Before anything else runs, `installNetworkBlocks()` in `shell.mjs` removes these and makes them unredefinable:
   - `WebSocket`, `WebSocketStream`, `WebTransport` and `EventSource`;
   - the WebRTC constructors and `SharedWorker`;
   - `navigator.sendBeacon` and `navigator.serviceWorker`.

   `window.open` returns `null`. Tests scan the shipped first-party files for network calls, URL sinks, HTML injection and external URLs, and confirm `index.html` is the only shipped document.
6. **Overrides refused.** The WebView2 loader appends `[HKLM|HKCU]\Software\Policies\Microsoft\Edge\WebView2\AdditionalBrowserArguments` after the host's arguments, and the last copy of a switch wins. It also lets the registry replace the runtime and profile folders. The host therefore reads, but never writes, every setting subkey there. It refuses to start if a value could apply to it: named `SourceVault.Editor.exe`, `*`, or anything else that is not another program's `.exe`.

**Limits.** These are not boundaries against same-user processes or administrators, which AGENTS.md puts out of scope:
- a same-user process listening on 127.0.0.1:1 would receive proxied requests;
- the registry can be changed after the startup check;
- a debugger or replaced runtime defeats all of it.

The WebView2 runtime may ignore switches it considers essential, and here it did ignore one WebRTC spelling, so only native observation proves their effect (see the lab checklist in [editor-preview-checks.md](editor-preview-checks.md)). Some traffic belongs to the operating system rather than the editor's browser: the Edge Update service, Windows diagnostic data and the OS spellchecker. These are machine policy. The renderer guard is page script only: Monaco's workers keep their own globals, and there the CSP, the host filter and the proxy still apply.

**Checked on this machine (2026-10-19, WebView2 Runtime 155.0.4283.62, synthetic assets only).** A scratch probe (not in the repository) opened a WebView2 with the host's environment options, browser arguments and document-created script. It mapped the shipped `assets` folder and a second, CSP-free probe page, with no request filter, so only the browser layer was under test. Loopback TCP and UDP listeners counted anything that got through. Results:
- Monaco loaded through the virtual host with the dead proxy, and the TypeScript worker reported diagnostics.
- These probes from the CSP-free page all failed, and the listeners received **0 TCP connections and 0 UDP packets**: `fetch` to `127.0.0.1` and `localhost`, WebSocket, image, `sendBeacon`, `<link rel=preconnect>`, and top-level navigation to a loopback port and to `example.invalid`. WebRTC was unavailable (`TypeError`).
- After 60 s idle the browser process tree (5 processes) held no TCP connections and no UDP endpoints.
- A control run with WebView2 defaults reached the listeners (8 TCP connections, 6 STUN packets). Its browser held 4 established TLS connections to Microsoft addresses with no page asking for them.
- Before the fix, `--force-webrtc-ip-handling-policy` alone still let 6 STUN packets out. `--webrtc-ip-handling-policy` alone and the document-created script alone each stopped them.

The published host was also launched with `--synthetic-preview` for 45 s. Its browser command line carried the switches, Monaco rendered the synthetic workspace, and its process tree held no TCP connections or UDP endpoints.

Not exercised natively:
- the registry refusal (writing the policy key needs administrator rights);
- `LaunchingExternalUriScheme`, the host's navigation and request filters (the probe page bypassed them on purpose), and vault mode;
- traffic seen on the wire.

The probe observed sockets, not packets. See the lab checklist.

Profiles live under `%LOCALAPPDATA%\SourceVault\profiles\{preview|vault}\<random>`. An installed editor's own folder is not user-writable, so the profile can't live there. Only synthetic data should be used.

- **Preview:** profiles are retained for inspection.
- **Vault:** by owner decision, the profile is deleted once the browser process exits. Profiles left by a crashed run are deleted at the next start, skipping any profile whose lock file is still held by a running editor.

Browser profiles, caches, dumps, paging and other OS behavior make this unsuitable as a claim of RAM-only handling.

Capture exclusion is **off** in the synthetic preview, so it can be demonstrated and photographed. In `--vault` mode it is **on** by owner decision (`SetWindowDisplayAffinity`, `WDA_EXCLUDEFROMCAPTURE`). It is best effort, not guaranteed capture protection, and the host reports the actual result to the UI.

## Vault mode (`--vault`)

Design: [editor-vault-design.md](editor-vault-design.md).

**Talking to the service.** Only the host talks to the service, through `SourceVault.Client.EditorVaultController`. The capability and snapshot/revision ids never reach the renderer.

**Renderer requests.** Requests are validated by `BridgePolicy`: exact `{type, requestId, operation, args}`, canonical ids, text at most 1 MiB of strict UTF-8, messages at most 8 MiB (checked before parsing). Operations: `listProjects`, `createProject`, `listFiles`, `openDocument`, `closeDocument`, `saveDocument`, `createFile`, `lock`, `unlock`. Responses carry protocol codes plus:
- `Conflict` and `NotOpen`;
- unlock failures `ServiceNotInstalled`, `ServiceNotRunning`, `AccessDenied`, `Busy`, `ServerUntrusted` and `ClientRejected`.

**Locking.** A Windows lock or logoff locks the editor at once. Lock events tell the renderer why (`user`, `windows`, `closed` or `expired`). Editing activity sends a keep-alive at most every 2 minutes.

**Installed build.** `SourceVault.Editor.exe` is pinned to `..\runtime` (`SourceVaultInstalled=true`). `Install-Editor.ps1` installs it at the exact path the service allows and adds the matching Windows desktop runtime. The development preview build keeps normal runtime lookup, so `Start-Editor.ps1` still works.

**Checked on this machine with a copy of the installed layout:**
- the window's display affinity was `0x11` (excluded from capture);
- a 9.4 MB profile was created with its lock held, and deleted after a normal close;
- a killed editor's profile was deleted at the next launch;
- unrelated folders were left alone.

**Renderer (step 3).** `app.mjs` asks the host for its mode before rendering anything, so a vault editor never shows the synthetic sample. Preview mode (`preview-app.mjs` over `state.mjs`) is unchanged. Vault mode (`vault-app.mjs` over `vault-session.mjs`) has:

- a project picker and project creation;
- files loaded only when opened, and their text dropped when closed;
- **+ New file**;
- a conflict banner with **Reload their version** / **Keep editing**;
- lock screens for the user, Windows, expiry and disconnects, plus explained unlock failures;
- drafts kept while locked and re-checked on unlock.

For browser review, `index.html?demo` (or the older `?fake-vault`) runs the demo below without a host. The demo vault (`demo-host.mjs`) mirrors the host bridge rules and controller semantics.

Reviewed in the browser with the fake host: picker, open, edit and save, external edit then conflict then reload, user lock and unlock, Windows lock, a failed unlock message, new file, and switch project, with no console errors. Preview mode was rechecked unchanged.

**Not yet done:** running vault mode as the installed editor against the service (design step 4).

## Demo mode (`--demo`, owner request, 2026-10-19)

The whole vault editor, without the service, for showing it to people. `SourceVault.Editor.exe --demo` runs the vault UI on `demo-host.mjs`, a simulated vault in the page's memory. It has fictional TypeScript and C++ projects, history, conflicts and merge, deleted files, simulated builds, search, settings and every editor feature.

- **Nothing is encrypted, and the demo says so in red.** The chrome and the editor theme are red. A banner, the title-bar badge, the status bar, the window title, the lock screens, the explorer note, the session card, the Save tooltip and messages, the builds caption and the settings dialog all say *not encrypted*. The inspector's note says spillage can happen: typed text stays in memory, can reach the page file or crash data, and the window can be captured.
- **Host:** demo mode never creates the service controller and never opens the pipe. It accepts only the status messages that preview mode accepts. Every network and export block is unchanged. Capture exclusion is **off**, so the demo can be presented and screen-shared. The browser profile (`profiles\demo`) is deleted after each run, as in vault mode. Closing with unsaved demo edits asks first.
- **Page:** `app.mjs` loads `demo-host.mjs` only when the host reports `mode: "demo"` (or for `?demo` in a development browser). Vault mode never imports it (a test checks this). Settings in the demo stay in memory; nothing goes to `editor-settings.json`.
- **Demo controls:** a foldable panel makes the events other people and the service cause: someone else edits or deletes the open file, a Windows lock, session expiry, a failed unlock, a failed build.
- **Launching:**
  - `scripts\Start-Editor.ps1 -Demo` from the repository;
  - `scripts\Build-EditorDemo.ps1` builds `artifacts\demo\SourceVault-Demo` and a zip: the editor on its own private runtime, `Start SourceVault Demo.cmd`, a README and a SHA-256 manifest. It runs on any Windows 10/11 x64 PC with the WebView2 Runtime, with no install and no administrator rights.

## Editor features (owner request, 2026-10-19)

Everyday comforts in vault mode. Shortcuts are listed in the editor (Ctrl+K Ctrl+S) and in [user-guide.md](user-guide.md).

- **Navigation.** Go to file (Ctrl+P, fuzzy; `:` line, `@` symbol, `>` command), recently opened files (Ctrl+E), Search all files (Ctrl+Shift+F), and an outline (Ctrl+Shift+O).
- **Comfort.** Editor settings (Ctrl+,), Save all (Ctrl+Alt+S) with an unsaved count in the status bar, Compare with saved (Ctrl+K D, with Revert that Ctrl+Z undoes), and Format document (Shift+Alt+F) where Monaco has a formatter. Otherwise the editor says there is none.
- **Layout.** Split editor (Ctrl+\\, two Monaco editors sharing the models), status-bar details (selection, indentation, line endings, file size against the editor's 1 MiB limit), the shortcut list, and focus mode (Ctrl+K Z).
- **C and C++.** `.c` is C. `.cpp`, `.cc`, `.cxx`, `.c++`, `.cppm`, `.ixx` and the headers `.h`, `.hpp`, `.hh`, `.hxx`, `.h++`, `.inl`, `.ipp`, `.tpp` are C++ (Monaco's C++ highlighting covers both). Alt+O switches between a header and its source: same folder first, then anywhere, for `include/` and `src/` layouts. Build files are mapped as well: `.vcxproj`, `.csproj`, `.props`, `.targets` as XML, `.ps1`, `.sh`, `.cmd`/`.bat`, `.ini` and `Dockerfile`.
- **Outline.** Monaco's TypeScript worker outlines TypeScript and JavaScript. For C#, C and C++, `workbench.mjs` has a line-based outline (namespaces, types, functions and methods, C# properties and fields, C/C++ macros). It skips comments, strings and preprocessor lines. It is not a compiler: several declarations on one line, or multi-line raw strings, can be missed or misplaced. Go to definition and completion across files remain TypeScript/JavaScript only (no C# or C++ language server).

**New bridge operations** (`BridgePolicy`):
- `searchProject {projectId, query, matchCase, wholeWord}`: the query is 1 to 200 characters with no control characters, and the flags are booleans. The host passes it to the service's `SearchProject` (contract: [protocol-v2.md](../contracts/protocol-v2.md)) and returns `matches`, `truncated` and `filesSearched`. The renderer then searches files with unsaved edits in their drafts, with the same rules (`withDrafts` in `workbench.mjs`), because the service only has the saved text.
- `getSettings {}` and `saveSettings {settings}`: these need no vault session and work while locked.

**Settings file.** `%LOCALAPPDATA%\SourceVault\editor-settings.json`, per Windows user, read and written only by the host (`EditorSettings`). It holds exactly eight display fields: theme (`dark`, `light` or `highContrast`), font size 10 to 28, tab size 2, 4 or 8, and the booleans insert spaces, word wrap, minimap, show whitespace and line numbers. It never holds file names, paths, project names or text. The bridge refuses any other field (a `recentFiles` list, for example). On read, a file over 4 KiB, a link, or any invalid content means the defaults. Writes go to a temporary file, then a rename.

**What stays in memory only.** Recently opened files, search results and the split layout are never written anywhere. Search results hold lines of text from files that may not be open, so locking drops them, as it closes the comparison view.

**Closing with unsaved edits.** In vault mode, closing the window while the renderer reports unsaved edits (the status message's `dirtyCount`) asks *Close anyway?* first. Closing discards them, as before. This prompt only adds a warning.

Reviewed in the browser with the fake host (2026-10-19), which has a fictional C++ project: C++ highlighting and outline, Alt+O, Go to file, Ctrl+E, search including an unsaved draft and the jump to a match, split editor, compare and revert then undo, settings (theme and font live, then saved), the shortcut list, Ctrl+K Ctrl+S not saving, focus mode, Save all, the C++ "no formatter" message, and lock dropping search results then unlock. TypeScript kept its formatter and outline, and preview mode was unchanged. The browser automation could not send Ctrl+\\ or Ctrl+, (it sends no key code for punctuation), so those were run through their commands and buttons. A real keyboard check is a lab row.

The bridge accepts messages from the app origin only, bounded to 4 KiB and depth 3. The exact shape is `{ "type": "sourcevault.preview.ready" | "sourcevault.preview.status", "locked": boolean, "dirtyCount": integer, "revisionCount": integer }`. Counts must be 0..32 and 0..1000000 respectively. Duplicate/unknown properties and operations have no effect. A ready message receives `{ "type": "sourcevault.host.metadata", "mode": "synthetic-preview", "captureExclusion": false, "serviceConnected": false }`. The bridge never receives source text or evaluates script; status messages only update the window title. The frontend owns its synthetic lock presentation.

Runtime navigation, actual rendering, and user interactions require a Windows desktop smoke check. No TPM/service/VM security gate follows from this preview.

## Any file (2026-10-19)

From [vault-any-file-design.md](vault-any-file-design.md) section 4.
- **What the editor opens:** text up to 1 MiB. Up to 256 KiB it goes inline (`OpenDocument`, `ApplyEdits`); from 256 KiB to 1 MiB the host reads it with `ReadTextPart` and saves it with an upload, transparently.
- **Other files** (binary, or text over 1 MiB) are listed with a *binary* or *large* badge and open an **info panel**: name, kind, size, last change and SHA-256, with rename, delete, history and **Replace from disk…**. The explorer adds **+ Add from disk** and, per folder, **Add files from disk to *folder*/**.
- **Files from disk:** the host shows the native *Open* dialog (`AddToRecent` off; Windows may still remember the last folder), reads the chosen files (hashing first, then streaming with `BeginUpload`/`UploadPart`/`CommitUploads`), and returns only vault names, sizes, kinds and hashes. The renderer never sends or receives a path or the bytes. A disk read failure is `FileUnreadable`, a file changed between the two reads `SourceChanged`; neither locks the session.
- **No way out:** there is no download, open-with or preview; binary content never reaches the renderer (decisions O4, O8).
- **History** lists kind and SHA-256; binary and large versions have no diff, and **Revert to this version** (two-step) calls `RevertFile`.
- **Conflicts:** a replacement that conflicts stays staged in the service for **Keep mine** / **Keep theirs** (2 minutes, and until the session ends). A text draft against a binary or large version gets the same two choices instead of a text merge.
- **New bridge operations:** `fileInfo`, `replaceFromDisk`, `keepMine`, `keepTheirs` (`projectId`, `fileId`), `addFromDisk` (`projectId`, `folder`: `""` or a path without leading or trailing `/`) and `revertFile` (`projectId`, `fileId`, `revisionId`). Listings add `kind` and `sha256`; `mergeSources` adds `mergeable`; `searchProject` adds `filesSkipped`. New codes: `NotText`, `Cancelled`, `UploadExpired`, `SourceChanged`, `FileUnreadable`.
- **Demo mode** simulates all of it with made-up files; nothing is read from disk.
