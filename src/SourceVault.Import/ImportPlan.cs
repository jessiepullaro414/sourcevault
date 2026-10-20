using System.Security.Cryptography;
using SourceVault.Storage;

namespace SourceVault.Import;

internal sealed record ImportCandidate(string Path, string FullPath, long Length, byte[] Sha256);
internal sealed record SkippedItem(string Path, string Reason);

/// <summary>
/// What an import would bring in from a folder: every file, whatever its content (docs/vault-any-file-design.md section
/// 5.1), except excluded folders, links and junctions (never followed), names the vault can't hold, case duplicates,
/// unreadable files and files over 1 GiB, each listed with a reason. A plan over 25,000 files or 8 GiB is refused as a
/// whole (<see cref="LimitProblem"/>). Each file is read once to hash it; nothing is changed on disk.
/// </summary>
internal sealed class ImportPlan
{
    internal static readonly string[] DefaultExcludedFolders =
        [".git", ".svn", ".hg", "node_modules", "bin", "obj", "dist", "build", "out", "target", ".vs", ".idea",
         "__pycache__", ".venv", "venv", "packages", ".gradle", ".next", "coverage"];
    internal const string TooLarge = "larger than 1 GiB";
    internal const string BadName = "name not allowed in the vault";
    internal const string Link = "link or junction (not followed)";
    internal const string ExcludedFolder = "excluded folder";
    internal const string CaseDuplicate = "name differs from another only by letter case";
    internal const string Unreadable = "could not be read";
    private const long MaxBytes = RevisionStore.MaximumFileBytes;

    /// <summary>Full path of the scanned folder.</summary>
    public required string Root { get; init; }
    public required IReadOnlyList<ImportCandidate> Files { get; init; }
    public required IReadOnlyList<SkippedItem> Skipped { get; init; }
    public long TotalBytes => Files.Sum(f => f.Length);

    /// <summary>Why the whole plan can't go into one project (too many files or bytes), or null.</summary>
    public string? LimitProblem =>
        Files.Count > RevisionStore.MaximumFiles ? $"Too many files: a project holds at most {RevisionStore.MaximumFiles:N0}. Exclude more folders with --exclude."
        : TotalBytes > RevisionStore.MaximumProjectBytes ? $"Too large: a project holds at most {RevisionStore.MaximumProjectBytes >> 30} GiB. Exclude more folders with --exclude."
        : null;

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
                if (!seen.Add(path)) { skipped.Add(new(path, CaseDuplicate)); continue; }
                byte[] hash; long length;
                try
                {
                    using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                    length = stream.Length;
                    hash = SHA256.HashData(stream); // streamed: the file is never all in memory
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException) { seen.Remove(path); skipped.Add(new(path, Unreadable)); continue; }
                files.Add(new(path, file.FullName, length, hash));
            }
        }
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return new ImportPlan { Root = rootInfo.FullName, Files = files, Skipped = skipped };
    }
}
