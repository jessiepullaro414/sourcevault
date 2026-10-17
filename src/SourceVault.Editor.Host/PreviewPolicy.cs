using System.Text;
using System.Text.Json;

namespace SourceVault.Editor.Host;

/// <summary>Kinds of renderer-to-host bridge message the preview accepts.</summary>
internal enum PreviewMessageKind { Ready, Status }

/// <summary>A fully validated bridge message. It carries no renderer-supplied strings.</summary>
internal readonly record struct PreviewMessage(PreviewMessageKind Kind, bool Locked, int DirtyCount, int RevisionCount);

/// <summary>
/// Pure, UI-free security decisions for the synthetic editor preview host: which URIs belong to
/// the app origin, which web-message payloads are valid, and which environment the host will run in.
/// No WPF or WebView2 types appear here so the policy can be exercised by plain console tests.
/// Every function fails closed: anything unexpected returns false.
/// </summary>
internal static class PreviewPolicy
{
    internal const string AppHost = "sourcevault.local";
    internal const string AppOrigin = "https://" + AppHost;
    internal const int MaxUriLength = 8192;
    internal const int MaxMessageBytes = 4096;
    internal const int MaxMessageDepth = 3;
    internal const int MaxDirtyCount = 32;
    internal const int MaxRevisionCount = 1_000_000;
    internal const string EnvironmentPrefix = "WEBVIEW2_";
    internal const string ReadyType = "sourcevault.preview.ready";
    internal const string StatusType = "sourcevault.preview.status";

    /// <summary>True when <paramref name="value"/> is an absolute URI on exactly https://sourcevault.local (port 443, no userinfo).</summary>
    internal static bool IsAppUri(string? value) => TryParseAppUri(value, out _, out _);

    /// <summary>Navigation (top level and frames) may only target the app origin.</summary>
    internal static bool IsNavigationAllowed(string? uri) => IsAppUri(uri);

    /// <summary>
    /// True for blob URIs minted by the app origin (Monaco workers): blob:https://sourcevault.local/&lt;guid&gt;
    /// with no query. The path must be a canonical GUID as produced by Chromium.
    /// </summary>
    internal static bool IsAppBlobUri(string? value)
    {
        if (value is null || !value.StartsWith("blob:", StringComparison.Ordinal)) return false;
        if (!TryParseAppUri(value[5..], out string path, out bool hasQuery) || hasQuery) return false;
        return path.Length == 37 && path[0] == '/' && Guid.TryParseExact(path[1..], "D", out _);
    }

    /// <summary>Resource requests are allowed only to the app origin or app-origin blob URIs.</summary>
    internal static bool IsResourceAllowed(string? uri) => IsAppUri(uri) || IsAppBlobUri(uri);

    // Strict, string-level origin parse. The framework Uri class is consulted afterwards as a second opinion,
    // never as the only check: it normalizes case, dot segments, IDN and backslashes, which hides hostile forms.
    private static bool TryParseAppUri(string? value, out string path, out bool hasQuery)
    {
        path = string.Empty;
        hasQuery = false;
        if (value is null || value.Length == 0 || value.Length > MaxUriLength) return false;
        foreach (char c in value)
            if (c < 0x21 || c > 0x7E || c == '\\') return false; // no controls, spaces, non-ASCII (IDN/homoglyphs), backslashes
        if (!value.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase)) return false;
        string rest = value[AppOrigin.Length..];
        if (rest.StartsWith(":443", StringComparison.Ordinal)) rest = rest[4..];
        if (rest.Length > 0 && rest[0] is not ('/' or '?' or '#')) return false; // rejects sourcevault.local.evil, @evil, :8443, ...
        int end = rest.IndexOfAny(['?', '#']);
        string rawPath = end < 0 ? rest : rest[..end];
        hasQuery = end >= 0 && rest[end] == '?';
        if (!IsSafePath(rawPath)) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != AppHost || uri.Port != 443 || uri.UserInfo.Length != 0)
            return false;
        path = rawPath.Length == 0 ? "/" : rawPath;
        return true;
    }

    // The path is mapped to a folder by the host. Reject encodings that could smuggle separators or dot segments
    // past a check performed on the undecoded string, including double encoding (%252e).
    private static bool IsSafePath(string rawPath)
    {
        for (int i = 0; i < rawPath.Length; i++)
        {
            if (rawPath[i] != '%') continue;
            if (i + 2 >= rawPath.Length || !Uri.IsHexDigit(rawPath[i + 1]) || !Uri.IsHexDigit(rawPath[i + 2])) return false;
            string hex = rawPath.Substring(i + 1, 2).ToUpperInvariant();
            if (hex is "2E" or "2F" or "5C" or "25" or "00") return false; // encoded '.', '/', '\', '%', NUL
            i += 2;
        }
        foreach (string segment in rawPath.Split('/'))
            if (segment is "." or "..") return false;
        return true;
    }

    /// <summary>
    /// Validates a renderer web message. Accepts only a JSON object with exactly the four fields
    /// type/locked/dirtyCount/revisionCount, a known type, bounded counts, at most 4096 UTF-8 bytes and depth 3.
    /// Returns false (never throws) for anything else.
    /// </summary>
    internal static bool TryParseMessage(string? json, out PreviewMessage message)
    {
        message = default;
        if (json is null || Encoding.UTF8.GetByteCount(json) > MaxMessageBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxMessageDepth });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            foreach (var property in root.EnumerateObject())
            {
                count++;
                if (!names.Add(property.Name)) return false; // duplicate (including escaped-name duplicates)
            }
            if (count != 4) return false;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return false;
            if (!root.TryGetProperty("locked", out var locked) || locked.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            if (!root.TryGetProperty("dirtyCount", out var dirty) || dirty.ValueKind != JsonValueKind.Number ||
                !dirty.TryGetInt32(out int dirtyCount) || dirtyCount is < 0 or > MaxDirtyCount) return false;
            if (!root.TryGetProperty("revisionCount", out var revision) || revision.ValueKind != JsonValueKind.Number ||
                !revision.TryGetInt32(out int revisionCount) || revisionCount is < 0 or > MaxRevisionCount) return false;
            PreviewMessageKind kind;
            switch (type.GetString())
            {
                case ReadyType: kind = PreviewMessageKind.Ready; break;
                case StatusType: kind = PreviewMessageKind.Status; break;
                default: return false;
            }
            message = new PreviewMessage(kind, locked.GetBoolean(), dirtyCount, revisionCount);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            return false; // invalid messages have no effect and are never logged
        }
    }

    /// <summary>
    /// Returns the name of the first nonempty WEBVIEW2_* variable (case-insensitive), or null when the
    /// environment is clean. Such variables can change browser arguments, runtime location, profile or debugging.
    /// Empty values are treated as unset, matching how Windows stores environment variables.
    /// </summary>
    internal static string? FindWebView2Override(IEnumerable<KeyValuePair<string, string?>> environment)
    {
        foreach (var entry in environment)
            if (entry.Key is not null &&
                entry.Key.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(entry.Value))
                return entry.Key;
        return null;
    }
}
