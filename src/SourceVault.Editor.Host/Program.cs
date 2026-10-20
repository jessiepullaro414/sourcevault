using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SourceVault.Client;
using SourceVault.Import;

namespace SourceVault.Editor.Host;

// Demo (owner request, 2026-10-19): the vault editor on an in-page simulated vault, for showing it without the
// service. Nothing is encrypted; the page says so in red.
internal enum EditorMode { SyntheticPreview, Vault, Demo }

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        EditorMode? mode = args switch
        {
            ["--synthetic-preview"] => EditorMode.SyntheticPreview,
            ["--vault"] => EditorMode.Vault,
            ["--demo"] => EditorMode.Demo,
            _ => null,
        };
        if (mode is null)
        {
            MessageBox.Show("Launch with --demo for the demonstration (a simulated vault; nothing is encrypted), --synthetic-preview for the synthetic preview, or --vault to open the SourceVault service (installed editor only).",
                "SourceVault editor", MessageBoxButton.OK, MessageBoxImage.Information);
            return 2;
        }
        var window = new EditorWindow(mode.Value);
        int exit = new Application().Run(window);
        // Vault mode (owner decision): the per-run browser profile is deleted once the browser process has exited.
        // Demo mode does the same, so typed demo text leaves less behind.
        if (mode is EditorMode.Vault or EditorMode.Demo && window.ProfilePath is { } profile)
        {
            window.BrowserExited.Wait(TimeSpan.FromSeconds(10));
            ProfileStore.TryDelete(profile);
        }
        return exit;
    }
}

internal sealed class EditorWindow : Window
{
    private static readonly TimeSpan KeepAliveAfter = TimeSpan.FromMinutes(2);
    private readonly EditorMode mode;
    private readonly Grid surface = new();
    private readonly WebView2 webView = new();
    private readonly TaskCompletionSource browserExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EditorVaultController? vault;
    private bool closing;
    private bool captureExcluded;
    private DateTime lastVaultActivity = DateTime.UtcNow;
    private int unsavedFiles; // from the renderer's status messages (a count only)
    private ImportPlan? pendingImport; // "Import a folder": scanned, waiting for the name and a confirmation
    private bool closeConfirmed;
    private string? lockReasonOverride;

    public EditorWindow(EditorMode mode)
    {
        this.mode = mode;
        Width = 1450;
        Height = 940;
        MinWidth = 1000;
        MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(16, 20, 29));
        Content = surface;
        UpdateTitle(locked: mode == EditorMode.Vault);
        ShowNotice("Opening SourceVault", mode switch
        {
            EditorMode.Vault => "Preparing the vault editor…",
            EditorMode.Demo => "Preparing the demo. Nothing in it is encrypted.",
            _ => "Preparing the offline synthetic editor…",
        });
        // Vault mode (owner decision): ask Windows to exclude this window from capture. Best effort, reported truthfully.
        SourceInitialized += (_, _) =>
        {
            if (mode == EditorMode.Vault)
                captureExcluded = CaptureProtection.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, CaptureProtection.ExcludeFromCapture);
        };
        Loaded += async (_, _) => await InitializeAsync();
        // Unsaved edits live only in this window: ask before closing it with some.
        Closing += (_, e) =>
        {
            if (mode == EditorMode.SyntheticPreview || unsavedFiles == 0 || closeConfirmed) return;
            var answer = MessageBox.Show(this, mode == EditorMode.Demo
                ? $"{unsavedFiles} demo file(s) have unsaved edits. Demo projects are never stored, so closing discards everything in them.\n\nClose anyway?"
                : $"{unsavedFiles} file(s) have unsaved edits. They exist only in this window and are lost if you close it.\n\nClose anyway?",
                "Unsaved edits", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes) closeConfirmed = true; else e.Cancel = true;
        };
        Closed += (_, _) =>
        {
            closing = true;
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            vault?.Dispose(); // closes the pipe; the service ends the session
            webView.Dispose();
            if (ProfilePath is null) browserExited.TrySetResult();
        };
    }

    internal string? ProfilePath { get; private set; }
    internal Task BrowserExited => browserExited.Task;

    private async Task InitializeAsync()
    {
        try
        {
            if (PreviewPolicy.FindWebView2Override(Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Select(entry => new KeyValuePair<string, string?>((string)entry.Key, entry.Value as string))) is not null)
            {
                ShowNotice("Browser override detected", "The editor will not start with WEBVIEW2_ environment overrides, including browser debugging or alternate profiles. Launch from a clean environment.");
                return;
            }
            // Registry overrides are applied by the WebView2 loader after ours and could replace the browser arguments below.
            if (NetworkPolicy.FindRegistryOverride(ReadWebView2PolicyValues(), Path.GetFileName(Environment.ProcessPath) ?? "") is not null)
            {
                ShowNotice("Browser policy override detected", "The editor will not start while a WebView2 policy override (Software\\Policies\\Microsoft\\Edge\\WebView2) could apply to it, because such an override can change its browser settings or network access. Ask your workstation administrator to remove it.");
                return;
            }
            string assets = Path.Combine(AppContext.BaseDirectory, "assets");
            if (!File.Exists(Path.Combine(assets, "index.html")))
            {
                ShowNotice("Editor assets missing", "Build or publish the editor host with its offline web assets, then launch again.");
                return;
            }

            ProfilePath = ProfileStore.Create(mode);
            // WPF WebView2 must join the loaded visual tree before initialization.
            // Keep the startup notice above it until the offline document is ready.
            surface.Children.Insert(0, webView);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: ProfilePath, options: new CoreWebView2EnvironmentOptions
            {
                // Keeps the browser off the network: dead proxy, no DNS, no non-proxied WebRTC, no background services (NetworkPolicy).
                AdditionalBrowserArguments = NetworkPolicy.AdditionalBrowserArguments,
                // Crash dumps can hold document text: never send them to Microsoft. They stay in the profile folder,
                // which vault mode deletes after each run.
                IsCustomCrashReportingEnabled = true,
                AllowSingleSignOnUsingOSPrimaryAccount = false,
                AreBrowserExtensionsEnabled = false,
                // No other process may attach a WebView to this browser process and profile.
                ExclusiveUserDataFolderAccess = true,
            });
            environment.BrowserProcessExited += (_, _) => browserExited.TrySetResult();
            if (closing) return;
            await webView.EnsureCoreWebView2Async(environment);
            if (closing) return;
            var core = webView.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(PreviewPolicy.AppHost, assets, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.IsReputationCheckingRequired = false; // SmartScreen would send visited URLs to Microsoft
            // Drops reach only the page's explorer drop zone (shell.mjs); everything else is refused there, and a dropped
            // file can never navigate (NavigationStarting allows only the app). The page passes dropped files to this host
            // as WebView2 objects, without reading them; the host reads them.
            webView.AllowExternalDrop = mode != EditorMode.SyntheticPreview;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.ScreenCaptureStarting += (_, e) => e.Cancel = true;
            // Only the bundled app may load: one entry document, no frames, no external URI schemes, no auth prompts.
            core.NavigationStarting += (_, e) => e.Cancel = !PreviewPolicy.IsNavigationAllowed(e.Uri);
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            core.BasicAuthenticationRequested += (_, e) => e.Cancel = true;
            core.ClientCertificateRequested += (_, e) => { e.Cancel = true; e.Handled = true; };
            core.ServerCertificateErrorDetected += (_, e) => e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            // Every URI, every resource context, and every request source: pages and dedicated workers, shared workers and service workers.
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (!PreviewPolicy.IsResourceAllowed(e.Request.Uri))
                    e.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "Content-Type: text/plain\r\n");
            };
            // WebRTC is the one network path the proxy and request filter do not cover: remove it before any page script.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(NetworkPolicy.DocumentCreatedScript);
            if (closing) return;
            core.WebMessageReceived += ReceiveMessage;
            core.ProcessFailed += (_, _) => ShowNotice("Editor stopped", "Close the editor and launch it again.");
            webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 16, 20, 29);
            if (mode == EditorMode.Vault)
            {
                // Only the host talks to the service; the capability and snapshot/revision ids never reach the renderer.
                vault = new EditorVaultController(ct => Task.Run(VaultClient.ConnectInstalled, ct));
                vault.Locked += reason => Dispatcher.InvokeAsync(() =>
                {
                    var text = lockReasonOverride ?? reason switch { LockReason.User => "user", LockReason.Expired => "expired", _ => "closed" };
                    lockReasonOverride = null;
                    PostLocked(text);
                });
                Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            }
            foreach (UIElement child in surface.Children.Cast<UIElement>().Where(child => child != webView).ToArray())
                surface.Children.Remove(child);
            core.Navigate(PreviewPolicy.EntryUri);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowNotice("WebView2 Runtime unavailable", "The editor requires the Microsoft Edge WebView2 Runtime. Nothing has been installed or changed. Ask your workstation administrator to provision the approved offline runtime.");
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (!closing) ShowNotice("Unable to open editor", "The local editor could not initialize. Check the offline assets, the WebView2 Runtime, and that your local application data folder is writable.");
        }
    }

    // Every (setting, value name) pair under HKLM and HKCU Software\Policies\Microsoft\Edge\WebView2\<setting>. Read only.
    private static List<(string Setting, string ValueName)> ReadWebView2PolicyValues()
    {
        var found = new List<(string, string)>();
        foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            using var policy = root.OpenSubKey(NetworkPolicy.PolicyKey);
            if (policy is null) continue;
            foreach (string setting in policy.GetSubKeyNames())
            {
                using var key = policy.OpenSubKey(setting);
                if (key is null) { found.Add((setting, "*")); continue; } // present but unreadable: fail closed
                foreach (string valueName in key.GetValueNames()) found.Add((setting, valueName));
            }
        }
        return found;
    }

    private void ReceiveMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Decision logic lives in PreviewPolicy and BridgePolicy; invalid messages have no effect and are never logged.
        if (!PreviewPolicy.IsAppUri(e.Source)) return;
        string json = e.WebMessageAsJson;
        if (vault is not null && BridgePolicy.TryParseVaultRequest(json, out var request))
        {
            var dropped = DroppedPaths(e);
            // Dropped items only with dropFiles, and dropFiles only with dropped items; anything else is ignored.
            if (dropped is { Count: 0 } == (request.Operation == VaultOperation.DropFiles) || dropped is null) return;
            _ = HandleVaultRequestAsync(request, dropped);
            return;
        }
        if (!PreviewPolicy.TryParseMessage(json, out var message)) return;
        switch (message.Kind)
        {
            case PreviewMessageKind.Ready:
                webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    type = "sourcevault.host.metadata",
                    mode = mode switch { EditorMode.Vault => "vault", EditorMode.Demo => "demo", _ => "synthetic-preview" },
                    captureExclusion = captureExcluded,
                    serviceConnected = vault is { IsLocked: false },
                }));
                break;
            case PreviewMessageKind.Status:
                unsavedFiles = message.DirtyCount;
                if (mode == EditorMode.SyntheticPreview) UpdateTitle(message.Locked);
                // Editing activity keeps an otherwise quiet vault session from idling out (5-minute service limit).
                else if (vault is { IsLocked: false } && DateTime.UtcNow - lastVaultActivity > KeepAliveAfter)
                {
                    lastVaultActivity = DateTime.UtcNow;
                    _ = vault.ListProjectsAsync();
                }
                break;
        }
    }

    // The paths of files and folders dropped with this message (empty when none); null when anything else came with it.
    private static List<string>? DroppedPaths(CoreWebView2WebMessageReceivedEventArgs e)
    {
        var objects = e.AdditionalObjects;
        if (objects is null || objects.Count == 0) return [];
        if (objects.Count > BridgePolicy.MaxDroppedItems) return null;
        var paths = new List<string>();
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not CoreWebView2File file || !Path.IsPathFullyQualified(file.Path)) return null;
            paths.Add(file.Path);
        }
        return paths;
    }

    private async Task HandleVaultRequestAsync(VaultBridgeRequest request, List<string> dropped)
    {
        var controller = vault!;
        lastVaultActivity = DateTime.UtcNow;
        string response;
        try
        {
            response = request.Operation switch
            {
                VaultOperation.ListProjects => Respond(request, await controller.ListProjectsAsync(), (w, projects) =>
                {
                    w.WriteStartArray("projects");
                    foreach (var p in projects) { w.WriteStartObject(); w.WriteString("projectId", p.ProjectId.ToString("D")); w.WriteString("name", p.Name); w.WriteNumber("fileCount", p.FileCount); w.WriteBoolean("available", p.Available); w.WriteEndObject(); }
                    w.WriteEndArray();
                }),
                VaultOperation.CreateProject => Respond(request, await controller.CreateProjectAsync(request.Name!), (w, p) =>
                    { w.WriteString("projectId", p.ProjectId.ToString("D")); w.WriteString("name", p.Name); }),
                VaultOperation.DeleteProject => Respond(request, await controller.DeleteProjectAsync(request.ProjectId, request.Name!), (w, removed) =>
                    w.WriteBoolean("filesRemoved", removed)),
                VaultOperation.RenameProject => Respond(request, await controller.RenameProjectAsync(request.ProjectId, request.NewName!), (w, _) => { }),
                VaultOperation.RenameFile => Respond(request, await controller.RenameFileAsync(request.ProjectId, request.FileId, request.NewName!), (w, _) => { }),
                VaultOperation.RenameFolder => Respond(request, await controller.RenameFolderAsync(request.ProjectId, request.Name!, request.NewName!), (w, count) =>
                    w.WriteNumber("renamed", count)),
                VaultOperation.DeleteFile => Respond(request, await controller.DeleteFileAsync(request.ProjectId, request.FileId), (w, _) => { }),
                VaultOperation.ListHistory => Respond(request, await controller.ListHistoryAsync(request.ProjectId, request.FileId, request.Page), (w, h) =>
                {
                    w.WriteStartArray("revisions");
                    foreach (var r in h.Revisions)
                    {
                        w.WriteStartObject();
                        w.WriteString("revisionId", r.RevisionId.ToString("D"));
                        if (r.SavedAt is { } at) w.WriteString("savedAt", at.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)); else w.WriteNull("savedAt");
                        if (r.SavedBy is { } by) w.WriteString("savedBy", by); else w.WriteNull("savedBy");
                        w.WriteNumber("byteLength", r.ByteLength); w.WriteString("action", r.Action); w.WriteString("name", r.Name);
                        w.WriteString("kind", r.Kind); if (r.Sha256 is null) w.WriteNull("sha256"); else w.WriteString("sha256", r.Sha256);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteBoolean("more", h.More);
                }),
                VaultOperation.OpenRevision => Respond(request, await controller.OpenRevisionAsync(request.ProjectId, request.FileId, request.RevisionId), (w, text) =>
                    w.WriteString("text", text)),
                VaultOperation.ListDeleted => Respond(request, await controller.ListDeletedAsync(request.ProjectId), (w, files) =>
                {
                    w.WriteStartArray("files");
                    foreach (var f in files)
                    {
                        w.WriteStartObject();
                        w.WriteString("fileId", f.FileId.ToString("D")); w.WriteString("name", f.Name); w.WriteNumber("byteLength", f.ByteLength);
                        w.WriteString("deletedAt", f.DeletedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                        if (f.DeletedBy is { } by) w.WriteString("deletedBy", by); else w.WriteNull("deletedBy");
                        w.WriteString("kind", f.Kind);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }),
                VaultOperation.RestoreFile => Respond(request, await controller.RestoreFileAsync(request.ProjectId, request.FileId, request.NewName!), (w, _) => { }),
                VaultOperation.MergeSources => Respond(request, await controller.MergeSourcesAsync(request.ProjectId, request.FileId), (w, m) =>
                {
                    // Not mergeable (binary or over 1 MiB on a side): no texts, only the revision "keep mine" saves over.
                    w.WriteBoolean("mergeable", m.Mergeable);
                    if (m.Mergeable) { w.WriteString("base", m.BaseText); w.WriteString("theirs", m.TheirsText); }
                    w.WriteString("theirsRevisionId", m.TheirsRevision.ToString("D"));
                }),
                VaultOperation.SaveMerged => Respond(request, await controller.SaveMergedAsync(request.ProjectId, request.FileId, request.Text!, request.RevisionId), (w, f) =>
                    w.WriteNumber("byteLength", f.ByteLength)),
                VaultOperation.ListBuildTargets => Respond(request, await controller.ListBuildTargetsAsync(), (w, targets) =>
                    { w.WriteStartArray("targets"); foreach (var t in targets) w.WriteStringValue(t); w.WriteEndArray(); }),
                VaultOperation.RequestBuild => Respond(request, await controller.RequestBuildAsync(request.ProjectId, request.Name!), (w, number) =>
                    w.WriteNumber("buildNumber", number)),
                VaultOperation.ListBuilds => Respond(request, await controller.ListBuildsAsync(request.ProjectId), (w, builds) =>
                    { w.WriteStartArray("builds"); foreach (var b in builds) { w.WriteStartObject(); WriteBuild(w, b); w.WriteEndObject(); } w.WriteEndArray(); }),
                VaultOperation.BuildStatus => Respond(request, await controller.GetBuildStatusAsync(request.ProjectId, request.BuildNumber), WriteBuild),
                VaultOperation.OpenBuildLog => Respond(request, await controller.OpenBuildLogAsync(request.ProjectId, request.BuildNumber), (w, text) =>
                    w.WriteString("text", text)),
                VaultOperation.ListFiles => Respond(request, await controller.ListFilesAsync(request.ProjectId), (w, files) =>
                {
                    // Revision ids stay in the host.
                    w.WriteStartArray("files");
                    foreach (var f in files) { w.WriteStartObject(); WriteFile(w, f); w.WriteEndObject(); }
                    w.WriteEndArray();
                }),
                VaultOperation.OpenDocument => Respond(request, await controller.OpenDocumentAsync(request.ProjectId, request.FileId), (w, doc) =>
                    { w.WriteString("fileId", doc.File.FileId.ToString("D")); w.WriteString("name", doc.File.Name); w.WriteString("text", doc.Text); }),
                VaultOperation.SaveDocument => Respond(request, await controller.SaveDocumentAsync(request.ProjectId, request.FileId, request.Text!), (w, f) =>
                    w.WriteNumber("byteLength", f.ByteLength)),
                VaultOperation.CreateFile => Respond(request, await controller.CreateFileAsync(request.ProjectId, request.Name!, request.Text!), (w, f) =>
                    { w.WriteString("fileId", f.FileId.ToString("D")); w.WriteString("name", f.Name); }),
                VaultOperation.SearchProject => Respond(request, await controller.SearchProjectAsync(request.ProjectId, request.Query!, request.MatchCase, request.WholeWord), (w, s) =>
                {
                    w.WriteStartArray("matches");
                    foreach (var m in s.Matches)
                    {
                        w.WriteStartObject();
                        w.WriteString("fileId", m.FileId.ToString("D")); w.WriteString("name", m.Name);
                        w.WriteNumber("line", m.Line); w.WriteNumber("column", m.Column); w.WriteNumber("length", m.Length); w.WriteString("preview", m.Preview); w.WriteNumber("previewOffset", m.PreviewOffset);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteBoolean("truncated", s.Truncated); w.WriteNumber("filesSearched", s.FilesSearched); w.WriteNumber("filesSkipped", s.FilesSkipped);
                }),
                VaultOperation.FileInfo => Respond(request, await controller.FileInfoAsync(request.ProjectId, request.FileId), (w, d) =>
                {
                    WriteFile(w, d.File);
                    if (d.SavedAt is { } at) w.WriteString("savedAt", at.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)); else w.WriteNull("savedAt");
                    if (d.SavedBy is { } by) w.WriteString("savedBy", by); else w.WriteNull("savedBy");
                }),
                VaultOperation.ReplaceFromDisk => ChooseFiles(multiple: false) is { } chosen
                    ? Respond(request, await controller.ReplaceFromDiskAsync(request.ProjectId, request.FileId, chosen[0]), WriteFile)
                    : BridgePolicy.BuildResponse(request.RequestId, "Cancelled", null),
                VaultOperation.KeepMine => Respond(request, await controller.KeepMineAsync(request.ProjectId, request.FileId), WriteFile),
                VaultOperation.KeepTheirs => Respond(request, await controller.KeepTheirsAsync(request.ProjectId, request.FileId), WriteFile),
                VaultOperation.AddFromDisk => ChooseFiles(multiple: true) is { } many
                    ? Respond(request, await controller.AddFromDiskAsync(request.ProjectId, request.Folder!, many), (w, added) =>
                        { w.WriteStartArray("files"); foreach (var f in added) { w.WriteStartObject(); WriteFile(w, f); w.WriteEndObject(); } w.WriteEndArray(); })
                    : BridgePolicy.BuildResponse(request.RequestId, "Cancelled", null),
                VaultOperation.RevertFile => Respond(request, await controller.RevertFileAsync(request.ProjectId, request.FileId, request.RevisionId), (w, _) => { }),
                VaultOperation.OpenBuildFolder => OpenBuildFolder(request, await controller.GetBuildStatusAsync(request.ProjectId, request.BuildNumber)),
                VaultOperation.ChooseImportFolder => await ChooseImportFolderAsync(request),
                VaultOperation.ImportFolder => await ImportFolderAsync(request, controller),
                VaultOperation.CancelImport => CancelImport(request),
                VaultOperation.DropFiles => await DropFilesAsync(request, dropped, controller),
                // Display preferences only (EditorSettings); they never contain names or text and need no vault session.
                VaultOperation.GetSettings => BridgePolicy.BuildResponse(request.RequestId, "Ok", w => { w.WriteStartObject("settings"); EditorSettings.Load(EditorSettings.FilePath).Write(w); w.WriteEndObject(); }),
                VaultOperation.SaveSettings => SaveSettings(request),
                VaultOperation.CloseDocument => Close(request, controller),
                VaultOperation.Lock => await LockAsync(request, controller),
                VaultOperation.Unlock => await UnlockAsync(request, controller),
                _ => BridgePolicy.BuildResponse(request.RequestId, "UnsupportedOperation", null),
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            response = BridgePolicy.BuildResponse(request.RequestId, "InternalError", null); // never reflect details
        }
        if (!closing) webView.CoreWebView2?.PostWebMessageAsJson(response);
    }

    // ---- import a folder, and dropped files (owner request 2026-10-20) -----------------------------------------------

    /// <summary>
    /// Shows the host's folder dialog and scans the folder with the importer's rules (each file is read once to hash it).
    /// The scan stays in the host; the page gets the folder's name, counts and the skipped names with reasons.
    /// </summary>
    private async Task<string> ChooseImportFolderAsync(VaultBridgeRequest request)
    {
        pendingImport = null;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Import a folder as a new project", Multiselect = false, AddToRecent = false };
        if (dialog.ShowDialog(this) != true) return BridgePolicy.BuildResponse(request.RequestId, "Cancelled", null);
        ImportPlan plan;
        try { plan = await Task.Run(() => ImportPlan.Scan(dialog.FolderName)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return BridgePolicy.BuildResponse(request.RequestId, "FileUnreadable", null); }
        pendingImport = plan;
        return BridgePolicy.BuildResponse(request.RequestId, "Ok", w =>
        {
            w.WriteString("folder", Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.Root)));
            w.WriteNumber("files", plan.Files.Count); w.WriteNumber("bytes", plan.TotalBytes);
            if (plan.LimitProblem is { } problem) w.WriteString("problem", problem); else w.WriteNull("problem");
            w.WriteNumber("skippedCount", plan.Skipped.Count);
            w.WriteStartArray("skipped");
            foreach (var s in plan.Skipped.Take(200)) { w.WriteStartObject(); w.WriteString("path", s.Path); w.WriteString("reason", s.Reason); w.WriteEndObject(); }
            w.WriteEndArray();
        });
    }

    private async Task<string> ImportFolderAsync(VaultBridgeRequest request, EditorVaultController controller)
    {
        var plan = pendingImport;
        pendingImport = null;
        if (plan is null) return BridgePolicy.BuildResponse(request.RequestId, "NotFound", null);
        if (plan.LimitProblem is not null || plan.Files.Count == 0) return BridgePolicy.BuildResponse(request.RequestId, "LimitExceeded", null);
        var files = plan.Files.Select(f => (f.Path, new DiskFile(Path.GetFileName(f.FullPath), f.Length, Opener(f.FullPath), f.Sha256))).ToList();
        var result = await controller.ImportProjectAsync(request.Name!, files, Progress(request.RequestId, plan.TotalBytes));
        return Respond(request, result, (w, r) => { w.WriteString("projectId", r.ProjectId.ToString("D")); w.WriteNumber("imported", r.Imported); w.WriteNumber("verified", r.Verified); });
    }

    private string CancelImport(VaultBridgeRequest request)
    {
        pendingImport = null;
        return BridgePolicy.BuildResponse(request.RequestId, "Ok", _ => { });
    }

    /// <summary>
    /// Adds dropped files and folders under <c>folder</c>. A dropped folder keeps its name and is scanned with the
    /// importer's rules (links never followed; .git, bin, obj and the like skipped). Nothing is replaced: a taken name
    /// stops the add, keeping what was already committed.
    /// </summary>
    private async Task<string> DropFilesAsync(VaultBridgeRequest request, List<string> paths, EditorVaultController controller)
    {
        var prefix = request.Folder!.Length == 0 ? "" : request.Folder + "/";
        List<(string Name, DiskFile File)> files;
        int skipped;
        try
        {
            (files, skipped) = await Task.Run(() =>
            {
                var found = new List<(string, DiskFile)>();
                int left = 0;
                foreach (var path in paths)
                {
                    var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { left++; continue; } // links are never followed
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var plan = ImportPlan.Scan(path);
                        left += plan.Skipped.Count;
                        found.AddRange(plan.Files.Select(f => (prefix + leaf + "/" + f.Path, new DiskFile(Path.GetFileName(f.FullPath), f.Length, Opener(f.FullPath), f.Sha256))));
                    }
                    else if (SourceVault.Storage.RevisionStore.IsValidLogicalName(prefix + leaf))
                        found.Add((prefix + leaf, new DiskFile(leaf, new FileInfo(path).Length, Opener(path))));
                    else left++;
                }
                return (found, left);
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return BridgePolicy.BuildResponse(request.RequestId, "FileUnreadable", null); }
        if (files.Count == 0) return BridgePolicy.BuildResponse(request.RequestId, "NothingToAdd", null);
        var result = await controller.AddManyAsync(request.ProjectId, files, Progress(request.RequestId, files.Sum(f => f.File.Length)));
        return Respond(request, result, (w, r) =>
        {
            w.WriteNumber("added", r.Added.Count); w.WriteNumber("skipped", skipped);
            w.WriteStartArray("names"); foreach (var f in r.Added.Take(20)) w.WriteStringValue(f.Name); w.WriteEndArray();
            if (r.Stopped is { } stopped) w.WriteString("stopped", stopped); else w.WriteNull("stopped");
        });
    }

    private static Func<Stream> Opener(string path) => () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);

    // Upload progress for the page, at most four times a second (bytes only, never names or content).
    private Action<long> Progress(Guid requestId, long total)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long last = -1000;
        return done =>
        {
            long now = clock.ElapsedMilliseconds;
            if (now - Interlocked.Read(ref last) < 250 && done < total) return;
            Interlocked.Exchange(ref last, now);
            Dispatcher.InvokeAsync(() => { if (!closing) webView.CoreWebView2?.PostWebMessageAsJson(BridgePolicy.BuildProgress(requestId, done, total)); });
        };
    }

    /// <summary>
    /// Opens a finished build's output folder in Explorer. The folder comes from the service's build record, never from
    /// the renderer; output files are ordinary plaintext files by design, so this shows nothing new.
    /// </summary>
    private static string OpenBuildFolder(VaultBridgeRequest request, VaultResult<BuildSummary> status)
    {
        if (!status.Ok) return BridgePolicy.BuildResponse(request.RequestId, status.Code, null);
        var folder = status.Value!.ArtifactFolder;
        if (folder is null || !Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.Contains('"') || !Directory.Exists(folder))
            return BridgePolicy.BuildResponse(request.RequestId, "NotFound", null);
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(explorer, $"\"{folder}\"") { UseShellExecute = false })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { return BridgePolicy.BuildResponse(request.RequestId, "Unavailable", null); }
        return BridgePolicy.BuildResponse(request.RequestId, "Ok", _ => { });
    }

    // A file as the renderer sees it: no revision id (it stays in the host).
    private static void WriteFile(System.Text.Json.Utf8JsonWriter w, FileSummary f)
    {
        w.WriteString("fileId", f.FileId.ToString("D")); w.WriteString("name", f.Name); w.WriteNumber("byteLength", f.ByteLength);
        w.WriteString("kind", f.Kind);
        if (f.Sha256 is null) w.WriteNull("sha256"); else w.WriteString("sha256", f.Sha256);
    }

    /// <summary>
    /// The host's own Open dialog for "Replace from disk" and "Add files from disk". The chosen paths and the bytes stay
    /// in the host; the renderer learns only the vault names. Null: cancelled.
    /// Windows may still remember the last folder used in its Open dialog.
    /// </summary>
    private List<DiskFile>? ChooseFiles(bool multiple)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = multiple ? "Add files to the vault" : "Replace with a file from disk",
            Multiselect = multiple, CheckFileExists = true, ValidateNames = true, AddToRecent = false,
        };
        if (dialog.ShowDialog(this) != true) return null;
        var files = new List<DiskFile>();
        foreach (var path in dialog.FileNames.Take(EditorVaultController.MaxAddFiles + 1))
        {
            long length;
            try { length = new FileInfo(path).Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { length = -1; } // read failures surface as FileUnreadable
            var chosen = path;
            files.Add(new DiskFile(Path.GetFileName(path), length, () => new FileStream(chosen, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan)));
        }
        return files;
    }

    private static void WriteBuild(System.Text.Json.Utf8JsonWriter w, BuildSummary b)
    {
        w.WriteNumber("buildNumber", b.Number); w.WriteString("target", b.Target); w.WriteString("state", b.State);
        if (b.Reason is null) w.WriteNull("reason"); else w.WriteString("reason", b.Reason);
        if (b.ExitCode is { } exit) w.WriteNumber("exitCode", exit); else w.WriteNull("exitCode");
        if (b.RequestedBy is null) w.WriteNull("requestedBy"); else w.WriteString("requestedBy", b.RequestedBy);
        w.WriteString("requestedAt", b.RequestedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        if (b.FinishedAt is { } done) w.WriteString("finishedAt", done.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)); else w.WriteNull("finishedAt");
        if (b.ArtifactFolder is null) w.WriteNull("artifactFolder"); else w.WriteString("artifactFolder", b.ArtifactFolder);
        w.WriteNumber("artifactFiles", b.ArtifactFiles); w.WriteNumber("artifactBytes", b.ArtifactBytes); w.WriteNumber("artifactsDropped", b.ArtifactsDropped);
        w.WriteBoolean("logAvailable", b.LogAvailable); w.WriteBoolean("logTruncated", b.LogTruncated);
    }

    private static string Respond<T>(VaultBridgeRequest request, VaultResult<T> result, Action<System.Text.Json.Utf8JsonWriter, T> write) =>
        BridgePolicy.BuildResponse(request.RequestId, result.Code, result.Ok ? w => write(w, result.Value!) : null);

    private static string SaveSettings(VaultBridgeRequest request)
    {
        try { request.Settings!.Save(EditorSettings.FilePath); return BridgePolicy.BuildResponse(request.RequestId, "Ok", _ => { }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return BridgePolicy.BuildResponse(request.RequestId, "Unavailable", null); }
    }

    private static string Close(VaultBridgeRequest request, EditorVaultController controller)
    {
        controller.Close(request.ProjectId, request.FileId);
        return BridgePolicy.BuildResponse(request.RequestId, "Ok", _ => { });
    }

    private async Task<string> LockAsync(VaultBridgeRequest request, EditorVaultController controller)
    {
        await controller.LockAsync();
        UpdateTitle(locked: true);
        return BridgePolicy.BuildResponse(request.RequestId, "Ok", _ => { });
    }

    private async Task<string> UnlockAsync(VaultBridgeRequest request, EditorVaultController controller)
    {
        var result = await controller.UnlockAsync();
        UpdateTitle(locked: !result.Ok, notConnected: result.Code is "ServiceNotRunning" or "ServiceNotInstalled" or "ServerUntrusted");
        return Respond(request, result, (w, changed) =>
        {
            // Opened files someone else changed while locked: the renderer reports these drafts as conflicts.
            w.WriteStartArray("changed");
            foreach (var (projectId, fileId) in changed) { w.WriteStartObject(); w.WriteString("projectId", projectId.ToString("D")); w.WriteString("fileId", fileId.ToString("D")); w.WriteEndObject(); }
            w.WriteEndArray();
        });
    }

    // Windows lock or logoff locks the editor at once, without waiting for the service to drop the connection.
    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is not (Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.SessionLogoff
            or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect)) return;
        Dispatcher.InvokeAsync(async () =>
        {
            if (vault is null) return;
            // No vault session (the page may be working offline, with files in memory): the page must still hide them.
            if (vault.IsLocked) { PostLocked("windows"); return; }
            lockReasonOverride = "windows"; // the controller reports this as a user lock; the renderer should say why
            await vault.LockAsync();
        });
    }

    private void PostLocked(string reason)
    {
        UpdateTitle(locked: true);
        if (!closing) webView.CoreWebView2?.PostWebMessageAsJson(BridgePolicy.BuildLocked(reason));
    }

    private void UpdateTitle(bool locked, bool notConnected = false) => Title = mode switch
    {
        EditorMode.Vault when notConnected => "SourceVault — NOT CONNECTED (the service isn't running)",
        EditorMode.Vault => locked ? "SourceVault — Vault editor (locked)" : "SourceVault — Vault editor",
        EditorMode.Demo => "SourceVault — DEMO (not encrypted)",
        _ => locked ? "SourceVault — Locked synthetic preview" : "SourceVault — Synthetic editor preview",
    };

    private void ShowNotice(string heading, string detail)
    {
        surface.Children.Clear();
        var panel = new StackPanel { MaxWidth = 620, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(36) };
        panel.Children.Add(new TextBlock { Text = heading, FontSize = 27, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 16) });
        panel.Children.Add(new TextBlock { Text = detail, FontSize = 16, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap });
        surface.Children.Add(panel);
    }
}

/// <summary>
/// WebView2 profiles live under %LOCALAPPDATA%\SourceVault\profiles\{vault|demo|preview}\&lt;random&gt;, because an installed
/// editor's own folder is not user-writable. Vault and demo profiles are deleted after each run and leftovers from crashed
/// runs at the next start (owner decision). Preview profiles are kept for inspection, as before.
/// </summary>
internal static class ProfileStore
{
    internal static string Root(EditorMode mode) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SourceVault", "profiles", mode switch { EditorMode.Vault => "vault", EditorMode.Demo => "demo", _ => "preview" });

    private const string LockFile = "sourcevault.lock";
    // Held for the life of this process, so a second running editor never deletes this profile as "stale".
    private static FileStream? held;

    internal static string Create(EditorMode mode)
    {
        var root = Root(mode);
        Directory.CreateDirectory(root);
        if (IsReparsePoint(root)) throw new IOException("Profile root is a link.");
        if (mode is EditorMode.Vault or EditorMode.Demo)
            foreach (var stale in Directory.EnumerateDirectories(root))
                if (!InUse(stale)) TryDelete(stale);
        var profile = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        held = new FileStream(Path.Combine(profile, LockFile), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        return profile;
    }

    private static bool InUse(string profile)
    {
        try { using var probe = new FileStream(Path.Combine(profile, LockFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (FileNotFoundException) { return false; }
        catch (IOException) { return true; } // another editor holds it
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>Deletes one profile folder (only a 32-hex-named, non-link folder under a profile root). Best effort.</summary>
    internal static void TryDelete(string profile)
    {
        var name = Path.GetFileName(profile);
        if (name.Length != 32 || !name.All(Uri.IsHexDigit) || !Directory.Exists(profile)) return;
        if (held is not null && string.Equals(Path.GetDirectoryName(held.Name), profile, StringComparison.OrdinalIgnoreCase)) { held.Dispose(); held = null; }
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (IsReparsePoint(profile)) { Directory.Delete(profile); return; } // remove the link itself, never its target
                Directory.Delete(profile, recursive: true); // does not follow links inside
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(300); } // browser still releasing files
        }
    }

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}

internal static class CaptureProtection
{
    // WDA_EXCLUDEFROMCAPTURE. Used in vault mode only (owner decision); the synthetic preview stays capturable.
    // Best effort: it does not stop cameras, some capture tools, or anyone with access to the session.
    internal const uint ExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}
