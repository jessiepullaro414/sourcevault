using System.Security.Cryptography;
using System.Text;

namespace SourceVault.Windows;

/// <summary>
/// SHA-256 pins for the installed clients' folders, read from <c>install-manifest.sha256</c>, which setup writes into
/// the admin-only install root (one "HEX  relative\path" line per file). A client is accepted only if its folder
/// holds exactly the pinned files and each matches its hash. Fails closed: a missing or malformed manifest, or a
/// folder with no entries, pins nothing, so that folder's clients are refused. This catches replaced or modified
/// client files (a permission mistake, a partial update); it does not constrain an administrator, who can rewrite
/// the manifest, and it checks files on disk, not the running process image. See docs/hardening-design.md.
/// </summary>
public sealed class ClientPins
{
    public const string ManifestName = "install-manifest.sha256";
    internal const int MaxManifestBytes = 4 * 1024 * 1024;
    internal const int MaxFolderFiles = 4096;
    private readonly string installRoot;
    private readonly HashSet<string> clientFolders;

    /// <summary>
    /// Pins the given top-level folders of <paramref name="installRoot"/>. The manifest is read again on every
    /// <see cref="Verify"/> (it is small), so an update that rewrites it needs no service restart.
    /// </summary>
    public ClientPins(string installRoot, IEnumerable<string> clientFolders) =>
        (this.installRoot, this.clientFolders) = (Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)), clientFolders.ToHashSet(StringComparer.OrdinalIgnoreCase));

    // "editor" -> { "editor\x.dll" -> hash }. Never throws: a bad or missing manifest pins nothing.
    private Dictionary<string, Dictionary<string, byte[]>> ReadManifest()
    {
        var folders = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(installRoot, ManifestName);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return folders;
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length is < 1 or > MaxManifestBytes) return folders;
                bytes = new byte[(int)stream.Length];
                stream.ReadExactly(bytes);
            }
            var parsed = Parse(bytes);
            if (parsed is null) return folders;
            foreach (var (relative, hash) in parsed)
            {
                var top = relative.Split('\\')[0];
                if (!clientFolders.Contains(top)) continue;
                if (!folders.TryGetValue(top, out var files)) folders[top] = files = new(StringComparer.OrdinalIgnoreCase);
                files[relative] = hash;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { folders.Clear(); }
        return folders;
    }

    /// <summary>Strict parse: every line "64 hex chars, two spaces, relative path". Any bad or duplicate line rejects the whole manifest (null).</summary>
    internal static List<(string Relative, byte[] Hash)>? Parse(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); } catch (DecoderFallbackException) { return null; }
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var result = new List<(string, byte[])>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.Length < 67 || line[64] != ' ' || line[65] != ' ') return null;
            byte[] hash;
            try { hash = Convert.FromHexString(line.AsSpan(0, 64)); } catch (FormatException) { return null; }
            var relative = line[66..];
            if (!ValidRelative(relative) || !seen.Add(relative)) return null;
            result.Add((relative, hash));
        }
        return result;
    }

    private static bool ValidRelative(string relative)
    {
        if (relative.Length > 400 || relative.Contains('/') || Path.IsPathRooted(relative) || relative.Contains(':')) return false;
        foreach (var part in relative.Split('\\'))
            if (part.Length == 0 || part is "." or ".." || part.Any(char.IsControl)) return false;
        return true;
    }

    /// <summary>
    /// Throws <see cref="UnauthorizedAccessException"/> unless the folder holding <paramref name="imagePath"/> (a
    /// top-level folder of the install root) contains exactly its pinned files, no links, and every hash matches.
    /// </summary>
    public void Verify(string imagePath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(imagePath));
        if (folder is null || !string.Equals(Path.GetDirectoryName(folder), installRoot, StringComparison.OrdinalIgnoreCase)) throw Rejected();
        var top = Path.GetFileName(folder);
        if (!ReadManifest().TryGetValue(top, out var expected) || expected.Count == 0) throw Rejected();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false };
        try
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw Rejected();
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw Rejected();
                if (entry is DirectoryInfo) continue;
                var relative = entry.FullName[(installRoot.Length + 1)..];
                if (found.Count >= MaxFolderFiles || !expected.TryGetValue(relative, out var hash) || !found.Add(relative)) throw Rejected();
                byte[] actual;
                using (var stream = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.Read)) actual = SHA256.HashData(stream);
                if (!actual.AsSpan().SequenceEqual(hash)) throw Rejected();
            }
        }
        catch (Exception e) when (e is IOException or System.Security.SecurityException) { throw Rejected(); }
        if (found.Count != expected.Count) throw Rejected(); // a pinned file is missing
    }

    private static UnauthorizedAccessException Rejected() => new("Client files do not match the install manifest.");
}
