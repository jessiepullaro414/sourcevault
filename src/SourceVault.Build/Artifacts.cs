using System.Formats.Tar;

namespace SourceVault.Build;

/// <summary>Build output was refused. <see cref="Reason"/> is a fixed code; nothing from the archive is echoed.</summary>
public sealed class ArtifactRejectedException(string reason) : Exception("Artifacts rejected: " + reason)
{
    public string Reason { get; } = reason;
}

public sealed record PublishedArtifacts(string Folder, int Files, long Bytes);

/// <summary>
/// Publishes a container's artifact folder (a tar from the Docker archive API) to the host. Untrusted input: only
/// regular files and folders, relative paths with valid Windows names, within the size and count limits; links,
/// devices, absolute or "..", duplicate (case-insensitive) and over-long paths reject the whole set. Files are written
/// to a fresh ".partial" folder with CreateNew, and the folder is renamed into place only when everything was written.
/// </summary>
public static class ArtifactPublisher
{
    public const int MaxFiles = 10000;
    public const int MaxRelativePath = 200;

    public static async Task<PublishedArtifacts> PublishAsync(Stream tar, string containerFolderName, string destination, long maxBytes, CancellationToken ct)
    {
        if (Directory.Exists(destination) || Directory.Exists(destination + ".partial")) throw new ArtifactRejectedException("DestinationExists");
        var partial = destination + ".partial";
        Directory.CreateDirectory(partial);
        int files = 0; long bytes = 0;
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
                if (isFolder) { Directory.CreateDirectory(target); continue; }
                if (++files > MaxFiles) throw new ArtifactRejectedException("TooManyFiles");
                bytes += entry.Length;
                if (entry.Length < 0 || bytes > maxBytes) throw new ArtifactRejectedException("TooLarge");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                if (entry.DataStream is not null) await CopyBoundedAsync(entry.DataStream, output, entry.Length, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            Directory.Move(partial, destination);
            return new PublishedArtifacts(destination, files, bytes);
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
