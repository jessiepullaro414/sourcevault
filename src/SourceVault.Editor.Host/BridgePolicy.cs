using System.Buffers;
using System.Text;
using System.Text.Json;

namespace SourceVault.Editor.Host;

internal enum VaultOperation { ListProjects, CreateProject, ListFiles, OpenDocument, CloseDocument, SaveDocument, CreateFile, Lock, Unlock }

/// <summary>A fully validated renderer request. Ids are canonical; text is valid UTF-8 within the document limit.</summary>
internal sealed record VaultBridgeRequest(Guid RequestId, VaultOperation Operation, Guid ProjectId, Guid FileId, string? Name, string? Text);

/// <summary>
/// Bridge v2 (docs/editor-vault-design.md): pure, UI-free validation of renderer-to-host vault requests and
/// construction of host-to-renderer messages. Requests carry ids, names and text only, never capabilities or
/// snapshot/revision ids, which stay in the host. Anything unexpected is rejected without throwing.
/// </summary>
internal static class BridgePolicy
{
    internal const string RequestType = "sourcevault.vault.request";
    internal const string ResponseType = "sourcevault.vault.response";
    internal const string LockedType = "sourcevault.vault.locked";
    internal const int MaxMessageChars = 2 * 1024 * 1024; // checked before parsing; escaped 256 KiB text fits
    internal const int MaxTextBytes = 256 * 1024;
    internal const int MaxNameChars = 260; // vault paths: up to 260 characters ("src/app/main.ts")
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly Dictionary<string, (VaultOperation Op, string[] Args)> Operations = new(StringComparer.Ordinal)
    {
        ["listProjects"] = (VaultOperation.ListProjects, []),
        ["createProject"] = (VaultOperation.CreateProject, ["name"]),
        ["listFiles"] = (VaultOperation.ListFiles, ["projectId"]),
        ["openDocument"] = (VaultOperation.OpenDocument, ["projectId", "fileId"]),
        ["closeDocument"] = (VaultOperation.CloseDocument, ["projectId", "fileId"]),
        ["saveDocument"] = (VaultOperation.SaveDocument, ["projectId", "fileId", "text"]),
        ["createFile"] = (VaultOperation.CreateFile, ["projectId", "name", "text"]),
        ["lock"] = (VaultOperation.Lock, []),
        ["unlock"] = (VaultOperation.Unlock, []),
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
            Guid projectId = Guid.Empty, fileId = Guid.Empty;
            string? name = null, text = null;
            if (op.Args.Contains("projectId") && !TryId(args.GetProperty("projectId"), out projectId)) return false;
            if (op.Args.Contains("fileId") && !TryId(args.GetProperty("fileId"), out fileId)) return false;
            if (op.Args.Contains("name") && !TryName(args.GetProperty("name"), out name)) return false;
            if (op.Args.Contains("text") && !TryText(args.GetProperty("text"), out text)) return false;
            request = new VaultBridgeRequest(requestId, op.Op, projectId, fileId, name, text);
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
