using System.IO;
using System.Text.Json;

namespace SourceVault.Editor.Host;

/// <summary>
/// The editor's display preferences, saved per Windows user in %LOCALAPPDATA%\SourceVault\editor-settings.json.
/// Only these fixed fields with bounded values: never file names, paths, project names or document text. Pure and
/// strict, so a malformed or tampered file simply falls back to the defaults.
/// </summary>
internal sealed record EditorSettings(string Theme, int FontSize, bool WordWrap, bool Minimap, int TabSize, bool InsertSpaces, bool RenderWhitespace, bool LineNumbers)
{
    internal const int MaxFileBytes = 4096;
    internal static readonly EditorSettings Default = new("dark", 13, false, true, 2, true, false, true);
    private static readonly string[] Themes = ["dark", "light", "highContrast"];
    private static readonly string[] Fields = ["theme", "fontSize", "wordWrap", "minimap", "tabSize", "insertSpaces", "renderWhitespace", "lineNumbers"];

    internal static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SourceVault", "editor-settings.json");

    /// <summary>Exactly the eight fields with allowed values. Never throws.</summary>
    internal static bool TryParse(JsonElement element, out EditorSettings settings)
    {
        settings = Default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name) || !Fields.Contains(property.Name)) return false;
        if (seen.Count != Fields.Length) return false;
        var theme = element.GetProperty("theme");
        if (theme.ValueKind != JsonValueKind.String || !Themes.Contains(theme.GetString())) return false;
        if (!Int(element, "fontSize", out int fontSize) || fontSize is < 10 or > 28) return false;
        if (!Int(element, "tabSize", out int tabSize) || tabSize is not (2 or 4 or 8)) return false;
        if (!Bool(element, "wordWrap", out bool wordWrap) || !Bool(element, "minimap", out bool minimap) || !Bool(element, "insertSpaces", out bool insertSpaces)
            || !Bool(element, "renderWhitespace", out bool whitespace) || !Bool(element, "lineNumbers", out bool lineNumbers)) return false;
        settings = new(theme.GetString()!, fontSize, wordWrap, minimap, tabSize, insertSpaces, whitespace, lineNumbers);
        return true;
    }

    internal static EditorSettings Parse(string json)
    {
        if (json.Length > MaxFileBytes) return Default;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 2 });
            return TryParse(doc.RootElement, out var settings) ? settings : Default;
        }
        catch (JsonException) { return Default; }
    }

    internal void Write(Utf8JsonWriter w)
    {
        w.WriteString("theme", Theme); w.WriteNumber("fontSize", FontSize); w.WriteBoolean("wordWrap", WordWrap); w.WriteBoolean("minimap", Minimap);
        w.WriteNumber("tabSize", TabSize); w.WriteBoolean("insertSpaces", InsertSpaces); w.WriteBoolean("renderWhitespace", RenderWhitespace); w.WriteBoolean("lineNumbers", LineNumbers);
    }

    internal string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })) { w.WriteStartObject(); Write(w); w.WriteEndObject(); }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The saved settings, or the defaults if there are none or they are invalid.</summary>
    internal static EditorSettings Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0) return Default;
            return Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Default; }
    }

    /// <summary>Writes atomically (a temporary file, then a rename) so a crash never leaves half a file.</summary>
    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, ToJson());
        File.Move(temporary, path, overwrite: true);
    }

    private static bool Int(JsonElement e, string name, out int value) { value = 0; var v = e.GetProperty(name); return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value); }
    private static bool Bool(JsonElement e, string name, out bool value)
    {
        var v = e.GetProperty(name);
        value = v.ValueKind == JsonValueKind.True;
        return v.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }
}
