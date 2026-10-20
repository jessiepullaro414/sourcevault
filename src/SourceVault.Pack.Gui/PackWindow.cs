using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SourceVault.Import;
using SourceVault.Packaging;

namespace SourceVault.Pack.Gui;

internal static class Program
{
    [STAThread]
    public static int Main() => new Application().Run(new PackWindow());
}

/// <summary>
/// SourceVault Pack in a window: set up this computer's TPM signing key, choose the vault PC's recipient file and a
/// project folder, see what goes in and what is skipped, then write the encrypted package. Shows names, sizes and
/// fingerprints only, never file content. Uses <see cref="PackCore"/>, exactly like the command line.
/// </summary>
internal sealed class PackWindow : Window
{
    private readonly TextBlock senderText = Wrapped(), recipientText = Wrapped(), folderText = Wrapped(), statusText = Wrapped();
    private readonly Button setupButton = new() { Content = "Set up this computer…" };
    private readonly Button createButton = new() { Content = "Create encrypted package…", IsEnabled = false, FontWeight = FontWeights.SemiBold };
    private readonly TextBox excludeBox = new() { MinWidth = 260, ToolTip = "Folder names to skip as well, separated by commas" };
    private readonly ListView included = new(), skipped = new();
    private readonly TabItem includedTab = new() { Header = "Included" }, skippedTab = new() { Header = "Skipped" };
    private PublicKeyFile? recipient;
    private ImportPlan? plan;
    private string? folder;
    private bool senderReady;

    public PackWindow()
    {
        Title = "SourceVault Pack";
        Width = 760; Height = 720; MinWidth = 560; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var chooseRecipient = new Button { Content = "Choose recipient file…" };
        var chooseFolder = new Button { Content = "Choose project folder…" };
        var rescan = new Button { Content = "Scan again" };
        setupButton.Click += (_, _) => Run(SetUp);
        chooseRecipient.Click += (_, _) => Run(ChooseRecipient);
        chooseFolder.Click += (_, _) => Run(ChooseFolder);
        rescan.Click += (_, _) => Run(() => { if (folder is not null) ScanFolder(folder); });
        createButton.Click += (_, _) => Run(CreatePackage);

        included.View = Columns(("File", "Path", 470), ("Bytes", "Length", 110));
        skipped.View = Columns(("Path", "Path", 380), ("Reason", "Reason", 220));
        includedTab.Content = included; skippedTab.Content = skipped;

        var layout = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        var top = new StackPanel();
        top.Children.Add(Heading("1. This computer"));
        top.Children.Add(senderText);
        top.Children.Add(Row(setupButton));
        top.Children.Add(Heading("2. The vault PC"));
        top.Children.Add(new TextBlock { Text = "Its recipient file comes from an operator there: SourceVault.Import.exe --export-recipient <file>.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
        top.Children.Add(recipientText);
        top.Children.Add(Row(chooseRecipient));
        top.Children.Add(Heading("3. Project folder"));
        top.Children.Add(folderText);
        top.Children.Add(Row(chooseFolder, new TextBlock { Text = "Also skip folders named:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) }, excludeBox, rescan));
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);

        var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        bottom.Children.Add(Row(createButton));
        bottom.Children.Add(statusText);
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(bottom);
        layout.Children.Add(new TabControl { Items = { includedTab, skippedTab }, Margin = new Thickness(0, 8, 0, 0) });
        Content = layout;

        senderText.Text = "Checking this computer's TPM signing key…";
        recipientText.Text = "No recipient file chosen.";
        folderText.Text = "No folder chosen.";
        statusText.Text = "Any file up to 1 GiB is packed (at most 25,000 files and 8 GiB); your folder is never changed.";
        Loaded += (_, _) => Run(RefreshSender);
    }

    // ---- steps ------------------------------------------------------------------------------------------------------

    private void RefreshSender()
    {
        senderReady = false;
        if (!SenderKey.Exists())
        {
            senderText.Text = "This computer has no SourceVault signing key yet. Set it up once: the key is created in this computer's TPM and can never be exported.";
            setupButton.Content = "Set up this computer…";
        }
        else
        {
            using var key = SenderKey.Open();
            var sender = PackCore.SenderFile(key);
            senderText.Text = $"Signing as \"{sender.Computer}\". Sender check code:\n{sender.FingerprintText}\nThe vault PC's administrator registers this computer once with its sender file (Setup-SourceVault.ps1 -RegisterSender).";
            setupButton.Content = "Save sender file again…";
            senderReady = true;
        }
        UpdateCreate();
    }

    private void SetUp()
    {
        var (key, created) = SenderKey.OpenOrCreate();
        using (key)
        {
            var sender = PackCore.SenderFile(key);
            var dialog = new SaveFileDialog { Title = "Save this computer's sender file", FileName = $"SourceVault-sender-{sender.Computer}.json", Filter = "Sender file (*.json)|*.json", OverwritePrompt = false };
            if (dialog.ShowDialog(this) == true)
            {
                PackCore.WriteNew(dialog.FileName, sender.ToJson());
                Status($"{(created ? "Created this computer's signing key in its TPM. " : "")}Saved the sender file: {dialog.FileName}. It holds no secrets; give it to the vault PC's administrator.");
            }
            else if (created) Status("Created this computer's signing key in its TPM. Save its sender file when you are ready.");
        }
        RefreshSender();
    }

    private void ChooseRecipient()
    {
        var dialog = new OpenFileDialog { Title = "Choose the vault PC's recipient file", Filter = "Recipient file (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        recipient = null;
        UpdateCreate();
        recipient = PackCore.ReadRecipient(dialog.FileName);
        recipientText.Text = $"Encrypting for the vault PC \"{recipient.Computer}\". Recipient check code:\n{recipient.FingerprintText}\nCheck that this matches the check code shown on the vault PC when the file was exported.";
        UpdateCreate();
    }

    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose the project folder to pack" };
        if (dialog.ShowDialog(this) != true) return;
        folder = dialog.FolderName;
        ScanFolder(folder);
    }

    private void ScanFolder(string path)
    {
        plan = null;
        UpdateCreate();
        var excludes = excludeBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        plan = PackCore.Scan(path, excludes);
        included.ItemsSource = plan.Files.Select(f => new { f.Path, Length = f.Length.ToString("N0") }).ToList();
        skipped.ItemsSource = plan.Skipped.Select(s => new { s.Path, s.Reason }).ToList();
        includedTab.Header = $"Included ({plan.Files.Count})";
        skippedTab.Header = $"Skipped ({plan.Skipped.Count})";
        folderText.Text = $"{plan.Root}\n{plan.Files.Count} file(s), {plan.TotalBytes:N0} bytes; {plan.Skipped.Count} skipped (binaries, large files, links and folders such as .git, bin, obj, node_modules).";
        if (PackCore.Problem(plan) is { } problem) Status(problem);
        UpdateCreate();
    }

    private void CreatePackage()
    {
        if (plan is null || recipient is null || !senderReady) return;
        var dialog = new SaveFileDialog { Title = "Save the encrypted package", FileName = PackCore.SuggestedName(plan) + ".svpkg", Filter = "SourceVault package (*.svpkg)|*.svpkg", OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true) return;
        using var key = SenderKey.Open();
        var sender = PackCore.SenderFile(key);
        var question = $"Encrypt {plan.Files.Count} file(s) from\n{plan.Root}\n\nfor the vault PC \"{recipient.Computer}\"\n{recipient.FingerprintText}\n\nsigned by \"{sender.Computer}\"?\n\nOnly that vault PC can open the package. Your folder is not changed.";
        if (MessageBox.Show(this, question, "Create encrypted package", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { Status("Nothing was written."); return; }
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var (id, bytes) = PackCore.Write(plan, recipient, key, dialog.FileName);
            Status($"Wrote {dialog.FileName} ({bytes:N0} bytes), package id {id:D}.\nCarry it to the vault PC any way you like, then an operator there runs:\nSourceVault.Import.exe --package \"{Path.GetFileName(dialog.FileName)}\" --project \"<new project name>\"");
        }
        finally { Cursor = null; }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private void UpdateCreate() => createButton.IsEnabled = senderReady && recipient is not null && plan is not null && PackCore.Problem(plan) is null;

    private void Status(string text) => statusText.Text = text;

    // Every step reports a refusal in the window instead of crashing; messages never contain file content.
    private void Run(Action step)
    {
        try { step(); }
        catch (Exception e) when (e is PackException or SenderKeyException or KeyFileException or IOException or UnauthorizedAccessException)
        {
            var message = e is KeyFileException ? "The recipient file is not usable: " + e.Message : e is DirectoryNotFoundException ? "Folder not found." : e.Message;
            Status(message);
            MessageBox.Show(this, message, "SourceVault Pack", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateCreate();
        }
    }

    private static TextBlock Wrapped() => new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4), FontFamily = new FontFamily("Segoe UI") };
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) };
    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        foreach (var child in children) { if (child is Button b) b.Padding = new Thickness(10, 4, 10, 4); row.Children.Add(child); if (child is Button) row.Children.Add(new Border { Width = 8 }); }
        return row;
    }
    private static GridView Columns(params (string Header, string Binding, double Width)[] columns)
    {
        var view = new GridView();
        foreach (var (header, binding, width) in columns) view.Columns.Add(new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = new System.Windows.Data.Binding(binding) });
        return view;
    }
}
