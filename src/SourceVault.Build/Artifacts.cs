using System.Formats.Tar;

namespace SourceVault.Build;

/// <summary>Build output was refused. <see cref="Reason"/> is a fixed code; nothing from the archive is echoed.</summary>
public sealed class ArtifactRejectedException(string reason) : Exception("Artifacts rejected: " + reason)
{
    public string Reason { get; } = reason;
}

/// <summary>An output file left out because it can reveal source. <see cref="Reason"/> is a fixed code.</summary>
public sealed record DroppedArtifact(string Path, string Reason);

/// <summary>What was published. <see cref="Dropped"/> lists at most <see cref="ArtifactFilter.MaxListed"/> of the <see cref="DroppedCount"/> left out.</summary>
public sealed record PublishedArtifacts(string Folder, int Files, long Bytes, IReadOnlyList<DroppedArtifact> Dropped, int DroppedCount)
{
    public PublishedArtifacts(string folder, int files, long bytes) : this(folder, files, bytes, [], 0) { }
}

/// <summary>
/// Output files that can reveal source, left out of the published artifacts by default (owner decision 2026-10-18,
/// docs/hardening-design.md section 5). An administrator can keep a kind per target with build.json "keepFileTypes".
/// This reduces accidental disclosure only: a build can still copy source into any file it keeps.
/// </summary>
public static class ArtifactFilter
{
    public const int MaxListed = 100;
    public const string DsymFolder = ".dsym";
    public const string InlineSourceMap = "inline-source-map";
    internal const int InlineScanBytes = 64 * 1024;
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".map"] = "SourceMap",
        [".pdb"] = "DebugSymbols", [".mdb"] = "DebugSymbols", [".debug"] = "DebugSymbols", [".dwo"] = "DebugSymbols", [".dwp"] = "DebugSymbols",
        [".ilk"] = "Intermediate", [".obj"] = "Intermediate", [".o"] = "Intermediate", [".iobj"] = "Intermediate", [".ipdb"] = "Intermediate",
    };
    private static readonly string[] ScriptExtensions = [".js", ".mjs", ".cjs", ".css"];
    private static readonly byte[] InlineMarker = "sourceMappingURL=data:"u8.ToArray();

    /// <summary>Every value "keepFileTypes" may name.</summary>
    public static IReadOnlyList<string> Kinds { get; } = [.. Extensions.Keys, DsymFolder, InlineSourceMap];

    /// <summary>The drop reason for a relative artifact path ('/'-separated), or null to publish it.</summary>
    internal static string? ReasonFor(string relative, IReadOnlySet<string> keep)
    {
        var parts = relative.TrimEnd('/').Split('/');
        if (!keep.Contains(DsymFolder) && parts.SkipLast(1).Any(p => p.EndsWith(".dSYM", StringComparison.OrdinalIgnoreCase))) return "DebugSymbols";
        var extension = Path.GetExtension(parts[^1]).ToLowerInvariant(); // keep holds lowercase kinds
        return Extensions.TryGetValue(extension, out var reason) && !keep.Contains(extension) ? reason : null;
    }

    /// <summary>True for a script or stylesheet whose last 64 KiB hold an inline (data:) source map, which embeds the source.</summary>
    internal static bool HasInlineSourceMap(string file, string relative, IReadOnlySet<string> keep)
    {
        if (keep.Contains(InlineSourceMap) || !ScriptExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase)) return false;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = (int)Math.Min(stream.Length, InlineScanBytes);
        stream.Seek(-length, SeekOrigin.End);
        var tail = new byte[length];
        stream.ReadExactly(tail);
        return tail.AsSpan().IndexOf(InlineMarker) >= 0;
    }
}

/// <summary>
/// Publishes a container's artifact folder (a tar from the Docker archive API) to the host. Untrusted input: only
/// regular files and folders, relative paths with valid Windows names, within the size and count limits; links,
/// devices, absolute or "..", duplicate (case-insensitive) and over-long paths reject the whole set. Files are written
/// to a fresh ".partial" folder with CreateNew, and the folder is renamed into place only when everything was written.
/// Files that can reveal source (<see cref="ArtifactFilter"/>) are left out unless <c>keep</c> names their kind; limits
/// still count them.
/// </summary>
public static class ArtifactPublisher
{
    public const int MaxFiles = 10000;
    public const int MaxRelativePath = 200;

    public static async Task<PublishedArtifacts> PublishAsync(Stream tar, string containerFolderName, string destination, long maxBytes, CancellationToken ct,
        IReadOnlySet<string>? keep = null)
    {
        keep ??= new HashSet<string>();
        if (Directory.Exists(destination) || Directory.Exists(destination + ".partial")) throw new ArtifactRejectedException("DestinationExists");
        var partial = destination + ".partial";
        Directory.CreateDirectory(partial);
        int files = 0, published = 0, droppedCount = 0; long bytes = 0, publishedBytes = 0;
        var dropped = new List<DroppedArtifact>();
        void Drop(string relative, string reason) { droppedCount++; if (dropped.Count < ArtifactFilter.MaxListed) dropped.Add(new(relative, reason)); }
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = new TarReader(tar, leaveOpen: true);
            TarEntry? entry;
            while ((entry = await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false)) is not null)
            {
                if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes) continue; // PAX metadata (reader applies it)
                bool isFile = entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile;
                bool isFolder = entry.EntryType == TarEntryType.Directory;
                if (!isFile && !isFolder) throw new ArtifactRejectedException("NotAFileOrFolder");
                var relative = RelativeName(entry.Name, containerFolderName);
                if (relative is null) continue; // the artifact folder itself
                if (!seen.Add(relative.TrimEnd('/'))) throw new ArtifactRejectedException("DuplicateName");
                var target = Path.Combine(partial, relative.TrimEnd('/').Replace('/', '\\'));
                if (isFolder)
                {
                    if (!keep.Contains(ArtifactFilter.DsymFolder) && relative.TrimEnd('/').Split('/').Any(p => p.EndsWith(".dSYM", StringComparison.OrdinalIgnoreCase))) continue;
                    Directory.CreateDirectory(target); continue;
                }
                if (++files > MaxFiles) throw new ArtifactRejectedException("TooManyFiles");
                bytes += entry.Length;
                if (entry.Length < 0 || bytes > maxBytes) throw new ArtifactRejectedException("TooLarge");
                if (ArtifactFilter.ReasonFor(relative, keep) is { } reason) { Drop(relative, reason); continue; } // the reader skips its data
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (entry.DataStream is not null) await CopyBoundedAsync(entry.DataStream, output, entry.Length, ct).ConfigureAwait(false);
                    await output.FlushAsync(ct).ConfigureAwait(false);
                }
                if (ArtifactFilter.HasInlineSourceMap(target, relative, keep)) { File.Delete(target); Drop(relative, "InlineSourceMap"); continue; }
                published++; publishedBytes += entry.Length;
            }
            Directory.Move(partial, destination);
            return new PublishedArtifacts(destination, published, publishedBytes, dropped, droppedCount);
        }
        catch (Exception e) when (e is not ArtifactRejectedException and not OperationCanceledException)
        {
            TryDelete(partial);
            throw new ArtifactRejectedException(e is InvalidDataException or FormatException ? "InvalidArchive" : "WriteFailed");
        }
        catch { TryDelete(partial); throw; }
    }

    // The archive of /x/out holds "out/..." entries: strip that first segment. Returns null for the folder itself.
    internal static string? RelativeName(string name, string containerFolderName)
    {
        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.None).ToList();
        if (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1); // folder entries end with '/'
        if (parts.Count > 0 && parts[0] == ".") parts.RemoveAt(0);
        if (parts.Count == 0 || !parts[0].Equals(containerFolderName, StringComparison.OrdinalIgnoreCase)) throw new ArtifactRejectedException("OutsideArtifactFolder");
        parts.RemoveAt(0);
        if (parts.Count == 0) return null;
        if (parts.Count > 32) throw new ArtifactRejectedException("TooDeep");
        foreach (var part in parts) if (!ValidSegment(part)) throw new ArtifactRejectedException("BadName");
        var relative = string.Join('/', parts);
        if (relative.Length > MaxRelativePath) throw new ArtifactRejectedException("NameTooLong");
        return relative;
    }

    internal static bool ValidSegment(string name)
    {
        if (name.Length is 0 or > 128 || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.')) return false;
        if (name.Any(c => char.IsControl(c) || c is '\\' or '/' or ':' or '<' or '>' or '"' or '|' or '?' or '*')) return false;
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) return false;
        if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3])) return false;
        return true;
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long length, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long remaining = length;
        while (remaining > 0)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException();
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
