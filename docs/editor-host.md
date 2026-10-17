# Windows editor preview host

`src/SourceVault.Editor.Host` is a WPF/WebView2 shell for the offline Monaco assets. Run it with exactly `--synthetic-preview`. It has no service, storage, key, or real source access. A normal resizable Windows title bar supplies minimize, maximize, and close controls.

Build or publish the project with .NET 10 SDK and the repository local NuGet feed. The host targets Windows x64; the pinned WebView2 package supplies its loader. Microsoft Edge WebView2 Runtime must already be installed. The preview does not install or download it. Missing-runtime and missing-assets failures display a local notice.

```powershell
dotnet publish src/SourceVault.Editor.Host/SourceVault.Editor.Host.csproj -c Release -o artifacts/editor-preview
./artifacts/editor-preview/SourceVault.Editor.exe --synthetic-preview
```

The published application maps `https://sourcevault.local` to its `assets` directory. Resource requests and navigations outside that exact HTTPS origin are blocked; origin-bound blob workers are allowed for Monaco. Downloads, new windows, permissions, browser accelerator keys (including browser print/devtools), default menus, external drops, password storage, and autofill are disabled. Startup rejects all nonempty `WEBVIEW2_` environment variables, including browser arguments, runtime/profile folders, and debugger controls. Registry/policy runtime overrides and runtime process launch auditing remain deployment review gates. These controls reduce accidental export; they do not establish a confidentiality boundary against a process owner, administrator, debugger, modified renderer, or operating-system capture.

The profile directory is `preview-profile/<random UUID>` under the application output directory. Only synthetic data should be used. Profiles are retained for inspection and are never automatically deleted. Browser profiles, caches, dumps, paging, and other OS behavior make this unsuitable as a claim of RAM-only handling.

Capture exclusion is **off** in this synthetic preview, so the editor can be demonstrated and photographed through normal software screenshots. A `SetWindowDisplayAffinity` declaration is reserved for a future reviewed deployment and is never called. Even when enabled later, the API is best effort, not guaranteed capture protection.

The bridge accepts messages from the app origin only, bounded to 4 KiB and depth 3. The exact shape is `{ "type": "sourcevault.preview.ready" | "sourcevault.preview.status", "locked": boolean, "dirtyCount": integer, "revisionCount": integer }`. Counts must be 0..32 and 0..1000000 respectively. Duplicate/unknown properties and operations have no effect. A ready message receives `{ "type": "sourcevault.host.metadata", "mode": "synthetic-preview", "captureExclusion": false, "serviceConnected": false }`. The bridge never receives source text or evaluates script; status messages only update the window title. The frontend owns its synthetic lock presentation.

Runtime navigation, actual rendering, and user interactions require a Windows desktop smoke check. No TPM/service/VM security gate follows from this preview.
