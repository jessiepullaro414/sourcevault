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

namespace SourceVault.Editor.Host;

internal enum EditorMode { SyntheticPreview, Vault }

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        EditorMode? mode = args switch
        {
            ["--synthetic-preview"] => EditorMode.SyntheticPreview,
            ["--vault"] => EditorMode.Vault,
            _ => null,
        };
        if (mode is null)
        {
            MessageBox.Show("Launch with --synthetic-preview for the synthetic demonstration, or --vault to open the SourceVault service (installed editor only).",
                "SourceVault editor", MessageBoxButton.OK, MessageBoxImage.Information);
            return 2;
        }
        var window = new EditorWindow(mode.Value);
        int exit = new Application().Run(window);
        // Vault mode (owner decision): the per-run browser profile is deleted once the browser process has exited.
        if (mode == EditorMode.Vault && window.ProfilePath is { } profile)
        {
            window.BrowserExited.Wait(TimeSpan.FromSeconds(10));
            ProfileStore.TryDelete(profile);
        }
        return exit;
    }
}

internal sealed class EditorWindow : Window
{
    private const string AppOrigin = PreviewPolicy.AppOrigin;
    private static readonly TimeSpan KeepAliveAfter = TimeSpan.FromMinutes(2);
    private readonly EditorMode mode;
    private readonly Grid surface = new();
    private readonly WebView2 webView = new();
    private readonly TaskCompletionSource browserExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EditorVaultController? vault;
    private bool closing;
    private bool captureExcluded;
    private DateTime lastVaultActivity = DateTime.UtcNow;
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
        ShowNotice("Opening SourceVault", mode == EditorMode.Vault ? "Preparing the vault editor…" : "Preparing the offline synthetic editor…");
        // Vault mode (owner decision): ask Windows to exclude this window from capture. Best effort, reported truthfully.
        SourceInitialized += (_, _) =>
        {
            if (mode == EditorMode.Vault)
                captureExcluded = CaptureProtection.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, CaptureProtection.ExcludeFromCapture);
        };
        Loaded += async (_, _) => await InitializeAsync();
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
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: ProfilePath);
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
            webView.AllowExternalDrop = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NavigationStarting += (_, e) => e.Cancel = !PreviewPolicy.IsNavigationAllowed(e.Uri);
            core.FrameNavigationStarting += (_, e) => e.Cancel = !PreviewPolicy.IsNavigationAllowed(e.Uri);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (!PreviewPolicy.IsResourceAllowed(e.Request.Uri))
                    e.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "Content-Type: text/plain\r\n");
            };
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
            core.Navigate(AppOrigin + "/index.html");
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

    private void ReceiveMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Decision logic lives in PreviewPolicy and BridgePolicy; invalid messages have no effect and are never logged.
        if (!PreviewPolicy.IsAppUri(e.Source)) return;
        string json = e.WebMessageAsJson;
        if (vault is not null && BridgePolicy.TryParseVaultRequest(json, out var request))
        {
            _ = HandleVaultRequestAsync(request);
            return;
        }
        if (!PreviewPolicy.TryParseMessage(json, out var message)) return;
        switch (message.Kind)
        {
            case PreviewMessageKind.Ready:
                webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    type = "sourcevault.host.metadata",
                    mode = mode == EditorMode.Vault ? "vault" : "synthetic-preview",
                    captureExclusion = captureExcluded,
                    serviceConnected = vault is { IsLocked: false },
                }));
                break;
            case PreviewMessageKind.Status:
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

    private async Task HandleVaultRequestAsync(VaultBridgeRequest request)
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
                    foreach (var p in projects) { w.WriteStartObject(); w.WriteString("projectId", p.ProjectId.ToString("D")); w.WriteString("name", p.Name); w.WriteNumber("fileCount", p.FileCount); w.WriteEndObject(); }
                    w.WriteEndArray();
                }),
                VaultOperation.CreateProject => Respond(request, await controller.CreateProjectAsync(request.Name!), (w, p) =>
                    { w.WriteString("projectId", p.ProjectId.ToString("D")); w.WriteString("name", p.Name); }),
                VaultOperation.DeleteProject => Respond(request, await controller.DeleteProjectAsync(request.ProjectId, request.Name!), (w, removed) =>
                    w.WriteBoolean("filesRemoved", removed)),
                VaultOperation.ListFiles => Respond(request, await controller.ListFilesAsync(request.ProjectId), (w, files) =>
                {
                    // Revision ids stay in the host.
                    w.WriteStartArray("files");
                    foreach (var f in files) { w.WriteStartObject(); w.WriteString("fileId", f.FileId.ToString("D")); w.WriteString("name", f.Name); w.WriteNumber("byteLength", f.ByteLength); w.WriteEndObject(); }
                    w.WriteEndArray();
                }),
                VaultOperation.OpenDocument => Respond(request, await controller.OpenDocumentAsync(request.ProjectId, request.FileId), (w, doc) =>
                    { w.WriteString("fileId", doc.File.FileId.ToString("D")); w.WriteString("name", doc.File.Name); w.WriteString("text", doc.Text); }),
                VaultOperation.SaveDocument => Respond(request, await controller.SaveDocumentAsync(request.ProjectId, request.FileId, request.Text!), (w, f) =>
                    w.WriteNumber("byteLength", f.ByteLength)),
                VaultOperation.CreateFile => Respond(request, await controller.CreateFileAsync(request.ProjectId, request.Name!, request.Text!), (w, f) =>
                    { w.WriteString("fileId", f.FileId.ToString("D")); w.WriteString("name", f.Name); }),
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

    private static string Respond<T>(VaultBridgeRequest request, VaultResult<T> result, Action<System.Text.Json.Utf8JsonWriter, T> write) =>
        BridgePolicy.BuildResponse(request.RequestId, result.Code, result.Ok ? w => write(w, result.Value!) : null);

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
        UpdateTitle(locked: !result.Ok);
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
            if (vault is null || vault.IsLocked) return;
            lockReasonOverride = "windows"; // the controller reports this as a user lock; the renderer should say why
            await vault.LockAsync();
        });
    }

    private void PostLocked(string reason)
    {
        UpdateTitle(locked: true);
        if (!closing) webView.CoreWebView2?.PostWebMessageAsJson(BridgePolicy.BuildLocked(reason));
    }

    private void UpdateTitle(bool locked) => Title = mode == EditorMode.Vault
        ? (locked ? "SourceVault — Vault editor (locked)" : "SourceVault — Vault editor")
        : (locked ? "SourceVault — Locked synthetic preview" : "SourceVault — Synthetic editor preview");

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
/// WebView2 profiles live under %LOCALAPPDATA%\SourceVault\profiles\{vault|preview}\&lt;random&gt;, because an installed
/// editor's own folder is not user-writable. Vault profiles are deleted after each run and leftovers from crashed runs
/// at the next start (owner decision). Preview profiles are kept for inspection, as before.
/// </summary>
internal static class ProfileStore
{
    internal static string Root(EditorMode mode) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SourceVault", "profiles", mode == EditorMode.Vault ? "vault" : "preview");

    private const string LockFile = "sourcevault.lock";
    // Held for the life of this process, so a second running editor never deletes this profile as "stale".
    private static FileStream? held;

    internal static string Create(EditorMode mode)
    {
        var root = Root(mode);
        Directory.CreateDirectory(root);
        if (IsReparsePoint(root)) throw new IOException("Profile root is a link.");
        if (mode == EditorMode.Vault)
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
