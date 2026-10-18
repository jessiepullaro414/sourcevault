using System.Security.Cryptography;
using System.Text;
using SourceVault.Storage;

namespace SourceVault.Import;

internal sealed record ImportCandidate(string Path, string FullPath, long Length, byte[] Sha256);
internal sealed record SkippedItem(string Path, string Reason);

/// <summary>
/// What an import would bring in from a folder. Only UTF-8 text files the vault accepts are included; everything else
/// is listed with a reason. Links (symbolic links, junctions) are never followed. Reads files as the person running
/// the import; nothing is changed on disk.
/// </summary>
internal sealed class ImportPlan
{
    internal static readonly string[] DefaultExcludedFolders =
        [".git", ".svn", ".hg", "node_modules", "bin", "obj", "dist", "build", "out", "target", ".vs", ".idea",
         "__pycache__", ".venv", "venv", "packages", ".gradle", ".next", "coverage"];
    internal const string TooLarge = "larger than 256 KiB";
    internal const string NotText = "binary or not UTF-8 text";
    internal const string BadName = "name not allowed in the vault";
    internal const string Link = "link or junction (not followed)";
    internal const string ExcludedFolder = "excluded folder";
    internal const string CaseDuplicate = "name differs from another only by letter case";
    internal const string Unreadable = "could not be read";
    private const int MaxBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public required IReadOnlyList<ImportCandidate> Files { get; init; }
    public required IReadOnlyList<SkippedItem> Skipped { get; init; }
    public long TotalBytes => Files.Sum(f => f.Length);

    internal static ImportPlan Scan(string root, IEnumerable<string>? extraExcludedFolders = null)
    {
        var rootInfo = new DirectoryInfo(Path.GetFullPath(root));
        if (!rootInfo.Exists) throw new DirectoryNotFoundException("Folder not found.");
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The folder itself is a link; point the importer at the real folder.");
        var excluded = new HashSet<string>(DefaultExcludedFolders.Concat(extraExcludedFolders ?? []), StringComparer.OrdinalIgnoreCase);
        var files = new List<ImportCandidate>();
        var skipped = new List<SkippedItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(DirectoryInfo Dir, string Prefix)>();
        pending.Push((rootInfo, ""));
        while (pending.Count > 0)
        {
            var (dir, prefix) = pending.Pop();
            FileSystemInfo[] entries;
            try { entries = dir.GetFileSystemInfos(); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { skipped.Add(new(prefix.TrimEnd('/'), Unreadable)); continue; }
            foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                var path = prefix + entry.Name;
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { skipped.Add(new(path, Link)); continue; }
                if (entry is DirectoryInfo sub)
                {
                    if (excluded.Contains(sub.Name)) skipped.Add(new(path + "/", ExcludedFolder));
                    else pending.Push((sub, path + "/"));
                    continue;
                }
                var file = (FileInfo)entry;
                if (!RevisionStore.IsValidLogicalName(path)) { skipped.Add(new(path, BadName)); continue; }
                if (file.Length > MaxBytes) { skipped.Add(new(path, TooLarge)); continue; }
                byte[] bytes;
                try { bytes = File.ReadAllBytes(file.FullName); }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException) { skipped.Add(new(path, Unreadable)); continue; }
                try
                {
                    if (!IsText(bytes)) { skipped.Add(new(path, NotText)); continue; }
                    if (!seen.Add(path)) { skipped.Add(new(path, CaseDuplicate)); continue; }
                    files.Add(new(path, file.FullName, bytes.Length, SHA256.HashData(bytes)));
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        }
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return new ImportPlan { Files = files, Skipped = skipped };
    }

    /// <summary>Strict UTF-8 without NUL bytes. UTF-16 files (full of NULs) and binaries are excluded.</summary>
    internal static bool IsText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IndexOf((byte)0) >= 0) return false;
        try { _ = StrictUtf8.GetCharCount(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }
}
