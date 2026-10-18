# Windows editor preview host

`src/SourceVault.Editor.Host` is a WPF/WebView2 shell for the offline Monaco assets. Run it with exactly `--synthetic-preview`. It has no service, storage, key, or real source access. A normal resizable Windows title bar supplies minimize, maximize, and close controls.

Build or publish the project with .NET 10 SDK and the repository local NuGet feed. The host targets Windows x64; the pinned WebView2 package supplies its loader. Microsoft Edge WebView2 Runtime must already be installed. The preview does not install or download it. Missing-runtime and missing-assets failures display a local notice.

```powershell
dotnet publish src/SourceVault.Editor.Host/SourceVault.Editor.Host.csproj -c Release -o artifacts/editor-preview
./artifacts/editor-preview/SourceVault.Editor.exe --synthetic-preview
```

The published application maps `https://sourcevault.local` to its `assets` directory. Resource requests and navigations outside that exact HTTPS origin are blocked; origin-bound blob workers are allowed for Monaco. Downloads, new windows, permissions, browser accelerator keys (including browser print/devtools), default menus, external drops, password storage, and autofill are disabled. Startup rejects all nonempty `WEBVIEW2_` environment variables, including browser arguments, runtime/profile folders, and debugger controls. Registry/policy runtime overrides and runtime process launch auditing remain deployment review gates. These controls reduce accidental export; they do not establish a confidentiality boundary against a process owner, administrator, debugger, modified renderer, or operating-system capture.

Profiles live under `%LOCALAPPDATA%\SourceVault\profiles\{preview|vault}\<random>`. An installed editor's own folder is not user-writable, so the profile can't live there. Only synthetic data should be used.

- **Preview:** profiles are retained for inspection.
- **Vault:** by owner decision, the profile is deleted once the browser process exits. Profiles left by a crashed run are deleted at the next start, skipping any profile whose lock file is still held by a running editor.

Browser profiles, caches, dumps, paging and other OS behavior make this unsuitable as a claim of RAM-only handling.

Capture exclusion is **off** in the synthetic preview, so it can be demonstrated and photographed. In `--vault` mode it is **on** by owner decision (`SetWindowDisplayAffinity`, `WDA_EXCLUDEFROMCAPTURE`). It is best effort, not guaranteed capture protection, and the host reports the actual result to the UI.

## Vault mode (`--vault`)

Design: [editor-vault-design.md](editor-vault-design.md).

**Talking to the service.** Only the host talks to the service, through `SourceVault.Client.EditorVaultController`. The capability and snapshot/revision ids never reach the renderer.

**Renderer requests.** Requests are validated by `BridgePolicy`: exact `{type, requestId, operation, args}`, canonical ids, text at most 256 KiB of strict UTF-8, messages at most 2 MiB. Operations: `listProjects`, `createProject`, `listFiles`, `openDocument`, `closeDocument`, `saveDocument`, `createFile`, `lock`, `unlock`. Responses carry protocol codes plus:
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

For browser review, `index.html?fake-vault` loads `dev/fake-host.mjs`, which mirrors the host bridge rules and controller semantics and has buttons to simulate an external edit, a Windows lock, expiry and an unlock failure. `dev/` is excluded from the native editor's assets.

Reviewed in the browser with the fake host: picker, open, edit and save, external edit then conflict then reload, user lock and unlock, Windows lock, a failed unlock message, new file, and switch project, with no console errors. Preview mode was rechecked unchanged.

**Not yet done:** running vault mode as the installed editor against the service (design step 4).

The bridge accepts messages from the app origin only, bounded to 4 KiB and depth 3. The exact shape is `{ "type": "sourcevault.preview.ready" | "sourcevault.preview.status", "locked": boolean, "dirtyCount": integer, "revisionCount": integer }`. Counts must be 0..32 and 0..1000000 respectively. Duplicate/unknown properties and operations have no effect. A ready message receives `{ "type": "sourcevault.host.metadata", "mode": "synthetic-preview", "captureExclusion": false, "serviceConnected": false }`. The bridge never receives source text or evaluates script; status messages only update the window title. The frontend owns its synthetic lock presentation.

Runtime navigation, actual rendering, and user interactions require a Windows desktop smoke check. No TPM/service/VM security gate follows from this preview.
