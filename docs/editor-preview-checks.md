# Editor preview review and acceptance checks

The editor milestone is a synthetic UI preview. Passing these checks does not authorize real-source import, decrypting service endpoints, or deployment. Browser preview and native WebView2 checks are separate: a working browser tab does not establish native host policy.

## Checks that can run during development

- Compile the native host against the pinned local WebView2 package; restore must use the repository's local-only package feed. Inspect launch scripts for debugging ports, security-disabling switches, executable/runtime overrides, and unbounded path input.
- Review first-party web files for `fetch`, network URLs, clipboard access, file pickers, downloads, storage APIs, source logging, and HTML injection. Inspect the vendored Monaco license/version/hash record separately; a search through upstream minified code is not evidence that a feature is reachable.
- Trace each native web-message handler: exact trusted origin, bounded message length, strict known fields, fixed operation allowlist, and no file, process, arbitrary path, or decryption operation. Invalid messages must not execute actions or expose exception details.
- Trace navigation, new-window, download, permission, external URI, frame, and resource handling. Resource access must resolve to the vendored app assets, not arbitrary adjacent files. Check percent-encoded traversal, query/fragment variations, alternate ports, and hostile lookalike origins against the actual policy implementation.
- No network (see "Only the bundled app, no network" in [editor-host.md](editor-host.md)). `SourceVault.Editor.Host.Tests` covers:
  - top-level navigation limited to the exact entry document;
  - external schemes and browser-internal pages;
  - network addresses in many spellings (IP forms, localhost, metadata address, CDNs, `ws:` and `stun:`);
  - browser-argument invariants: dead loopback proxy with no bypass or direct fallback, `MAP * ~NOTFOUND`, WebRTC limited to proxied UDP, no debugging, sandbox or profile switches, no duplicate switch;
  - registry override detection, failing closed;
  - the shipped `index.html` CSP allowlist and order;
  - scans of shipped first-party code for network calls and URL sinks.

  `tests/editor-web/network-guard.test.mjs` checks that `installNetworkBlocks()` removes the network APIs and that they cannot be redefined.
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

No-network review, 2026-10-19. Owner request: "our Monaco can ONLY be used and no website can be reached."

The audit found these gaps, now closed in code:
- No browser-level block existed. DNS prefetch, preconnect, WebRTC and browser background services never reach the request filter, and a default WebView2 held TLS connections to Microsoft addresses with no page asking for them.
- Crash dumps, which can contain document text, were sent to Microsoft under OS consent.
- SmartScreen was on.
- The request filter did not cover shared or service workers.
- Any app-origin asset could be navigated to top-level, and app-origin frames were allowed.
- External URI schemes relied on navigation cancellation alone.
- A registry `AdditionalBrowserArguments` policy value, appended after the host's arguments, could undo the browser arguments.

[editor-host.md](editor-host.md) describes the enforcement. A native probe measured these results:
- `--force-webrtc-ip-handling-policy` did not stop STUN UDP on runtime 155.0.4283.62;
- `--webrtc-ip-handling-policy` and the host's document-created script each did.

Socket-level results are recorded in editor-host.md; they are not a packet capture.

## Owner lab checklist: no network

Use the published editor on a lab machine, in both `--synthetic-preview` and `--vault`, with synthetic data only. DevTools are disabled, so these checks rely on Windows tools.

1. **Identify the editor's browser.** Other apps (Teams, Widgets, Outlook) also run `msedgewebview2.exe`. Find the editor's browser process and its children with `Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Select-Object ProcessId, ParentProcessId, CommandLine`. The editor's browser process is the one started by `SourceVault.Editor.exe`, and its command line contains `--proxy-server=http://127.0.0.1:1`, `MAP * ~NOTFOUND` and `--webrtc-ip-handling-policy=disable_non_proxied_udp`. Record the runtime version.
2. **Watch the network while using the editor.** Start Resource Monitor (Network tab, filter on those process IDs) or a packet capture (`pktmon` or Wireshark) before launching. Use the editor for at least 10 minutes: open, edit and save files, hover TypeScript symbols, use Find, lock and unlock; in vault mode also open history and builds. Expected: no TCP connection, UDP datagram or DNS query from the editor's processes to any non-loopback address. At most, failed connection attempts to `127.0.0.1:1`. Also leave the editor idle for 10 minutes and check again, for background services.
3. **Links in content.** In a synthetic TypeScript file, add a comment `/** See {@link https://example.com} and ![x](https://example.com/x.png) */` above a function, hover a call to it, and click the link if Monaco shows one. Expected: no browser or other application opens, no image loads, and step 2 shows no traffic.
4. **External applications.** Expected: no Windows "open this app?" prompt ever appears and no other application starts. In synthetic content, typing `mailto:` or `ms-settings:` text must not be clickable (Monaco links are off).
5. **Environment override.** In a new PowerShell window run `$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS='--proxy-server=direct://'`, then launch the editor. Expected: "Browser override detected" and no editor content.
6. **Registry override (lab VM only; needs administrator rights and changes machine policy; undo afterwards).**
   - Create `HKCU\Software\Policies\Microsoft\Edge\WebView2\AdditionalBrowserArguments` with string value `SourceVault.Editor.exe` = `--proxy-server=direct://` and launch. Expected: "Browser policy override detected".
   - Repeat with the value named `*`, and under `HKLM`.
   - Confirm that a value named `other.exe` does not block the editor.
   - Delete the key.
7. **Crash data.** After a vault session, confirm the profile, including any `Crashpad` folder, is deleted after a normal close. Confirm Windows Error Reporting (`%LOCALAPPDATA%\CrashDumps`, Reliability Monitor) shows no WebView2 report upload for the editor.
8. **Record** the runtime version, the Windows build, and any connection seen in step 2 together with the owning process. A connection by a WebView2 process that is not the editor's, or by the Edge Update service, is not an editor finding; note it separately.
