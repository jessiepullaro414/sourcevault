using System.Text.Json;
using SourceVault.Windows;

namespace SourceVault.Service;

internal sealed record ServiceConfiguration(string KeyName)
{
    internal const string FileName = "sourcevault.service.json";
    /// <summary>The package import key (docs/package-import-design.md, D1): a separate TPM key named after the vault key.</summary>
    internal string ImportKeyName => KeyName + ".Import";
    internal static ServiceConfiguration LoadFromApplicationDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        var path = Path.Combine(directory.FullName, FileName);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > 4096) throw new InvalidDataException();
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return Parse(bytes);
    }
    internal static ServiceConfiguration Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 1 or > 4096) throw new InvalidDataException();
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? keyName = null;
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new InvalidDataException();
            switch (property.Name)
            {
                case "schemaVersion":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var version) || version != 1) throw new InvalidDataException();
                    break;
                case "mode":
                    if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != "MetadataOnly") throw new InvalidDataException();
                    break;
                case "keyName":
                    if (property.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException();
                    keyName = property.Value.GetString();
                    break;
                default: throw new InvalidDataException();
            }
        }
        if (seen.Count != 3 || keyName is null) throw new InvalidDataException();
        TpmKeyProtector.ValidateKeyName(keyName);
        return new(keyName);
    }
}
