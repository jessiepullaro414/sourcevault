using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SourceVault.Editor.Host;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--synthetic-preview")
        {
            MessageBox.Show("Launch with --synthetic-preview to open the synthetic editor demonstration. Real vault access is not implemented.",
                "SourceVault preview", MessageBoxButton.OK, MessageBoxImage.Information);
            return 2;
        }

        var app = new Application();
        return app.Run(new EditorWindow());
    }
}

internal sealed class EditorWindow : Window
{
    private const string AppOrigin = PreviewPolicy.AppOrigin;
    private readonly Grid surface = new();
    private readonly WebView2 webView = new();
    private bool closing;

    public EditorWindow()
    {
        Title = "SourceVault — Synthetic editor preview";
        Width = 1450;
        Height = 940;
        MinWidth = 1000;
        MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(16, 20, 29));
        Content = surface;
        ShowNotice("Opening SourceVault", "Preparing the offline synthetic editor…");
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => { closing = true; webView.Dispose(); };
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (PreviewPolicy.FindWebView2Override(Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Select(entry => new KeyValuePair<string, string?>((string)entry.Key, entry.Value as string))) is not null)
            {
                ShowNotice("Browser override detected", "This preview will not start with WEBVIEW2_ environment overrides, including browser debugging or alternate profiles. Launch from a clean environment.");
                return;
            }
            string assets = Path.Combine(AppContext.BaseDirectory, "assets");
            if (!File.Exists(Path.Combine(assets, "index.html")))
            {
                ShowNotice("Editor assets missing", "Build or publish the editor host with its offline web assets, then launch again.");
                return;
            }

            // Each preview has a fresh profile. It contains synthetic browser data only.
            // Profiles are retained for inspection; this app performs no recursive deletion.
            string profile = Path.Combine(AppContext.BaseDirectory, "preview-profile", Guid.NewGuid().ToString("N"));
            // WPF WebView2 must join the loaded visual tree before initialization.
            // Keep the startup notice above it until the offline document is ready.
            surface.Children.Insert(0, webView);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
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
            core.ProcessFailed += (_, _) => ShowNotice("Editor stopped", "Close this preview and launch it again. No vault was connected.");
            webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 16, 20, 29);
            foreach (UIElement child in surface.Children.Cast<UIElement>().Where(child => child != webView).ToArray())
                surface.Children.Remove(child);
            core.Navigate(AppOrigin + "/index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowNotice("WebView2 Runtime unavailable", "This preview requires the Microsoft Edge WebView2 Runtime. Nothing has been installed or changed. Ask your workstation administrator to provision the approved offline runtime.");
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (!closing) ShowNotice("Unable to open editor", "The local editor could not initialize. Check the offline assets, WebView2 Runtime, and write access to the application preview-profile directory. No vault was connected.");
        }
    }

    private void ReceiveMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Decision logic lives in PreviewPolicy; invalid messages have no effect and are never logged.
        if (!PreviewPolicy.IsAppUri(e.Source)) return;
        if (!PreviewPolicy.TryParseMessage(e.WebMessageAsJson, out var message)) return;
        switch (message.Kind)
        {
            case PreviewMessageKind.Ready:
                webView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"sourcevault.host.metadata\",\"mode\":\"synthetic-preview\",\"captureExclusion\":false,\"serviceConnected\":false}");
                break;
            case PreviewMessageKind.Status:
                Title = message.Locked ? "SourceVault — Locked synthetic preview" : "SourceVault — Synthetic editor preview";
                break;
        }
    }

    private void ShowNotice(string heading, string detail)
    {
        surface.Children.Clear();
        var panel = new StackPanel { MaxWidth = 620, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(36) };
        panel.Children.Add(new TextBlock { Text = heading, FontSize = 27, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 16) });
        panel.Children.Add(new TextBlock { Text = detail, FontSize = 16, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap });
        surface.Children.Add(panel);
    }
}

internal static class CaptureProtection
{
    // Reserved for a reviewed real deployment. Deliberately never called in this preview:
    // screenshots are allowed so the synthetic UI can be inspected and demonstrated.
    internal const uint ExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}
