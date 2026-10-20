using System.Buffers;
using System.Text;
using System.Text.Json;

namespace SourceVault.Editor.Host;

internal enum VaultOperation { ListProjects, CreateProject, DeleteProject, RenameProject, ListFiles, RenameFile, RenameFolder, DeleteFile, ListHistory, OpenRevision, ListDeleted, RestoreFile, MergeSources, SaveMerged, ListBuildTargets, RequestBuild, ListBuilds, BuildStatus, OpenBuildLog, OpenDocument, CloseDocument, SaveDocument, CreateFile, Lock, Unlock, SearchProject, GetSettings, SaveSettings,
    FileInfo, ReplaceFromDisk, KeepMine, KeepTheirs, AddFromDisk, RevertFile, OpenBuildFolder, ChooseImportFolder, ImportFolder, CancelImport, DropFiles, UnlockWithPassword, SetPassword, ChangePassword }

/// <summary>A fully validated renderer request. Ids are canonical; text is valid UTF-8 within the document limit.</summary>
internal sealed record VaultBridgeRequest(Guid RequestId, VaultOperation Operation, Guid ProjectId, Guid FileId, string? Name, string? Text, string? NewName = null, Guid RevisionId = default, int Page = 0, int BuildNumber = 0,
    string? Query = null, bool MatchCase = false, bool WholeWord = false, EditorSettings? Settings = null, string? Folder = null,
    string? Password = null, string? NewPassword = null);

/// <summary>
/// Bridge v2 (docs/editor-vault-design.md): pure, UI-free validation of renderer-to-host vault requests and
/// construction of host-to-renderer messages. Requests carry ids, names and text only, never capabilities or
/// snapshot ids, which stay in the host. Revision ids appear only for history (openRevision) and to save a merge on top
/// of the revision it was merged with (saveMerged); the host still checks every save against the vault. Anything
/// unexpected is rejected without throwing.
/// </summary>
internal static class BridgePolicy
{
    internal const string RequestType = "sourcevault.vault.request";
    internal const string ResponseType = "sourcevault.vault.response";
    internal const string LockedType = "sourcevault.vault.locked";
    internal const string ProgressType = "sourcevault.vault.progress";
    /// <summary>Files dropped in one message (WebView2 additional objects); more is ignored like any malformed request.</summary>
    internal const int MaxDroppedItems = 1000;
    internal const int MaxMessageChars = 8 * 1024 * 1024; // checked before parsing; escaped 1 MiB text fits (at most 6 characters a byte)
    internal const int MaxTextBytes = 1024 * 1024; // the editor's text limit (docs/vault-any-file-design.md section 4)
    internal const int MaxNameChars = 260; // vault paths: up to 260 characters ("src/app/main.ts")
    internal const int MaxQueryChars = 200; // "Search all files": one line of plain text
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly Dictionary<string, (VaultOperation Op, string[] Args)> Operations = new(StringComparer.Ordinal)
    {
        ["listProjects"] = (VaultOperation.ListProjects, []),
        ["createProject"] = (VaultOperation.CreateProject, ["name"]),
        ["deleteProject"] = (VaultOperation.DeleteProject, ["projectId", "name"]), // name: the typed confirmation
        ["renameProject"] = (VaultOperation.RenameProject, ["projectId", "newName"]),
        ["renameFile"] = (VaultOperation.RenameFile, ["projectId", "fileId", "newName"]),
        ["renameFolder"] = (VaultOperation.RenameFolder, ["projectId", "name", "newName"]), // name: the folder path
        ["deleteFile"] = (VaultOperation.DeleteFile, ["projectId", "fileId"]),
        ["listHistory"] = (VaultOperation.ListHistory, ["projectId", "fileId", "page"]),
        ["openRevision"] = (VaultOperation.OpenRevision, ["projectId", "fileId", "revisionId"]),
        ["listDeleted"] = (VaultOperation.ListDeleted, ["projectId"]),
        ["restoreFile"] = (VaultOperation.RestoreFile, ["projectId", "fileId", "newName"]),
        ["mergeSources"] = (VaultOperation.MergeSources, ["projectId", "fileId"]),
        ["saveMerged"] = (VaultOperation.SaveMerged, ["projectId", "fileId", "text", "revisionId"]), // revisionId: theirs, from mergeSources
        ["listBuildTargets"] = (VaultOperation.ListBuildTargets, []),
        ["requestBuild"] = (VaultOperation.RequestBuild, ["projectId", "name"]), // name: the target
        ["listBuilds"] = (VaultOperation.ListBuilds, ["projectId"]),
        ["buildStatus"] = (VaultOperation.BuildStatus, ["projectId", "buildNumber"]),
        ["openBuildLog"] = (VaultOperation.OpenBuildLog, ["projectId", "buildNumber"]),
        ["openBuildFolder"] = (VaultOperation.OpenBuildFolder, ["projectId", "buildNumber"]), // the host asks the service for the folder
        ["listFiles"] = (VaultOperation.ListFiles, ["projectId"]),
        ["openDocument"] = (VaultOperation.OpenDocument, ["projectId", "fileId"]),
        ["closeDocument"] = (VaultOperation.CloseDocument, ["projectId", "fileId"]),
        ["saveDocument"] = (VaultOperation.SaveDocument, ["projectId", "fileId", "text"]),
        ["createFile"] = (VaultOperation.CreateFile, ["projectId", "name", "text"]),
        ["lock"] = (VaultOperation.Lock, []),
        ["unlock"] = (VaultOperation.Unlock, []),
        // The operator's SourceVault password (2026-10-20), checked by the service: never stored or logged by the host.
        ["unlockWithPassword"] = (VaultOperation.UnlockWithPassword, ["password"]),
        ["setPassword"] = (VaultOperation.SetPassword, ["newPassword"]), // the first one, or after an administrator's reset
        ["changePassword"] = (VaultOperation.ChangePassword, ["password", "newPassword"]),
        ["searchProject"] = (VaultOperation.SearchProject, ["projectId", "query", "matchCase", "wholeWord"]),
        ["getSettings"] = (VaultOperation.GetSettings, []),
        ["saveSettings"] = (VaultOperation.SaveSettings, ["settings"]), // display preferences only (EditorSettings)
        // Files the editor doesn't show (binary, or text over 1 MiB) and files from disk. The host shows its own Open
        // dialog and reads the file; the renderer never sends or receives a path or the bytes.
        ["fileInfo"] = (VaultOperation.FileInfo, ["projectId", "fileId"]),
        ["replaceFromDisk"] = (VaultOperation.ReplaceFromDisk, ["projectId", "fileId"]),
        ["keepMine"] = (VaultOperation.KeepMine, ["projectId", "fileId"]),     // after a replacement conflicted
        ["keepTheirs"] = (VaultOperation.KeepTheirs, ["projectId", "fileId"]),
        ["addFromDisk"] = (VaultOperation.AddFromDisk, ["projectId", "folder"]), // folder: "" for the project root
        ["revertFile"] = (VaultOperation.RevertFile, ["projectId", "fileId", "revisionId"]),
        // Import a folder as a new project: the host's folder dialog and scan, then the import (owner request 2026-10-20).
        ["chooseImportFolder"] = (VaultOperation.ChooseImportFolder, []),
        ["importFolder"] = (VaultOperation.ImportFolder, ["name"]), // name: the new project's name
        ["cancelImport"] = (VaultOperation.CancelImport, []),
        // Files and folders dropped on the explorer. The dropped items travel as WebView2 additional objects, which the
        // host turns into paths; the JSON carries only where they go. Every other operation must carry none.
        ["dropFiles"] = (VaultOperation.DropFiles, ["projectId", "folder"]),
    };

    /// <summary>Exactly {type, requestId, operation, args} with the operation's exact argument set. Never throws.</summary>
    internal static bool TryParseVaultRequest(string? json, out VaultBridgeRequest request)
    {
        request = null!;
        if (json is null || json.Length == 0 || json.Length > MaxMessageChars) return false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (!ExactFields(root, "type", "requestId", "operation", "args")) return false;
            if (root.GetProperty("type").ValueKind != JsonValueKind.String || root.GetProperty("type").GetString() != RequestType) return false;
            if (!TryId(root.GetProperty("requestId"), out var requestId)) return false;
            var opElement = root.GetProperty("operation");
            if (opElement.ValueKind != JsonValueKind.String || !Operations.TryGetValue(opElement.GetString()!, out var op)) return false;
            var args = root.GetProperty("args");
            if (!ExactFields(args, op.Args)) return false;
            Guid projectId = Guid.Empty, fileId = Guid.Empty, revisionId = Guid.Empty;
            int page = 0, buildNumber = 0;
            string? name = null, text = null, newName = null, query = null, folder = null, password = null, newPassword = null;
            bool matchCase = false, wholeWord = false;
            EditorSettings? settings = null;
            if (op.Args.Contains("projectId") && !TryId(args.GetProperty("projectId"), out projectId)) return false;
            if (op.Args.Contains("fileId") && !TryId(args.GetProperty("fileId"), out fileId)) return false;
            if (op.Args.Contains("name") && !TryName(args.GetProperty("name"), out name)) return false;
            if (op.Args.Contains("text") && !TryText(args.GetProperty("text"), out text)) return false;
            if (op.Args.Contains("newName") && !TryName(args.GetProperty("newName"), out newName)) return false;
            if (op.Args.Contains("revisionId") && !TryId(args.GetProperty("revisionId"), out revisionId)) return false;
            if (op.Args.Contains("page") && !TryPage(args.GetProperty("page"), out page)) return false;
            if (op.Args.Contains("buildNumber") && !TryBuildNumber(args.GetProperty("buildNumber"), out buildNumber)) return false;
            if (op.Args.Contains("query") && !TryQuery(args.GetProperty("query"), out query)) return false;
            if (op.Args.Contains("matchCase") && !TryBool(args.GetProperty("matchCase"), out matchCase)) return false;
            if (op.Args.Contains("wholeWord") && !TryBool(args.GetProperty("wholeWord"), out wholeWord)) return false;
            if (op.Args.Contains("settings")) { if (!EditorSettings.TryParse(args.GetProperty("settings"), out var parsed)) return false; settings = parsed; }
            if (op.Args.Contains("folder") && !TryFolder(args.GetProperty("folder"), out folder)) return false;
            if (op.Args.Contains("password") && !TryPassword(args.GetProperty("password"), out password)) return false;
            if (op.Args.Contains("newPassword") && !TryPassword(args.GetProperty("newPassword"), out newPassword)) return false;
            request = new VaultBridgeRequest(requestId, op.Op, projectId, fileId, name, text, newName, revisionId, page, buildNumber, query, matchCase, wholeWord, settings, folder, password, newPassword);
            return true;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>{type: response, requestId, code, result | null}. <paramref name="result"/> writes the result object's members.</summary>
    internal static string BuildResponse(Guid requestId, string code, Action<Utf8JsonWriter>? result) => Build(w =>
    {
        w.WriteString("type", ResponseType);
        w.WriteString("requestId", requestId.ToString("D"));
        w.WriteString("code", code);
        if (result is null) w.WriteNull("result");
        else { w.WriteStartObject("result"); result(w); w.WriteEndObject(); }
    });

    /// <summary>{type: progress, requestId, done, total}: bytes uploaded so far by a long request (import, drop).</summary>
    internal static string BuildProgress(Guid requestId, long done, long total) => Build(w =>
    {
        w.WriteString("type", ProgressType);
        w.WriteString("requestId", requestId.ToString("D"));
        w.WriteNumber("done", done); w.WriteNumber("total", total);
    });

    /// <summary>{type: locked, reason} with reason one of user | windows | closed | expired.</summary>
    internal static string BuildLocked(string reason) => reason is "user" or "windows" or "closed" or "expired"
        ? Build(w => { w.WriteString("type", LockedType); w.WriteString("reason", reason); })
        : throw new ArgumentException("Unknown lock reason.");

    private static string Build(Action<Utf8JsonWriter> members)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            members(w);
            w.WriteEndObject();
        }
        try { return Encoding.UTF8.GetString(buffer.WrittenSpan); }
        finally { buffer.Clear(); }
    }

    // Exact property set, no duplicates (JsonDocument keeps duplicates, so they are counted here).
    private static bool ExactFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !names.Contains(property.Name, StringComparer.Ordinal)) return false;
        return seen.Count == names.Length;
    }

    private static bool TryId(JsonElement value, out Guid id)
    {
        id = Guid.Empty;
        if (value.ValueKind != JsonValueKind.String) return false;
        var text = value.GetString();
        return Guid.TryParseExact(text, "D", out id) && id != Guid.Empty && text == id.ToString("D");
    }

    private static bool TryName(JsonElement value, out string? name)
    {
        name = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return name is { Length: > 0 and <= MaxNameChars } && !name.Any(char.IsControl) && IsStrictUtf8(name, out _);
    }

    // A folder path for new files: "" (the project root) or a name, without a leading or trailing '/'.
    private static bool TryFolder(JsonElement value, out string? folder)
    {
        folder = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (folder is null) return false;
        if (folder.Length == 0) return true;
        return TryName(value, out folder) && !folder!.StartsWith('/') && !folder.EndsWith('/');
    }

    // One line of plain text, 1-200 characters, no control characters.
    internal const int MaxPasswordChars = 256;
    // 1-256 characters, no control characters (the service decides what is strong enough).
    private static bool TryPassword(JsonElement value, out string? password)
    {
        password = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return password is { Length: > 0 and <= MaxPasswordChars } && !password.Any(char.IsControl) && IsStrictUtf8(password, out _);
    }

    private static bool TryQuery(JsonElement value, out string? query)
    {
        query = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return query is { Length: > 0 and <= MaxQueryChars } && !query.Any(char.IsControl);
    }

    private static bool TryBool(JsonElement value, out bool flag)
    {
        flag = value.ValueKind == JsonValueKind.True;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool TryPage(JsonElement value, out int page)
    {
        page = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out page) && page is >= 0 and <= 10000;
    }

    private static bool TryBuildNumber(JsonElement value, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number) && number >= 1;
    }

    private static bool TryText(JsonElement value, out string? text)
    {
        text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return text is not null && IsStrictUtf8(text, out int bytes) && bytes <= MaxTextBytes;
    }

    private static bool IsStrictUtf8(string value, out int bytes)
    {
        try { bytes = StrictUtf8.GetByteCount(value); return true; }
        catch (EncoderFallbackException) { bytes = 0; return false; } // lone surrogates
    }
}
