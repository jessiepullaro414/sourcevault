using System.Security.Cryptography;

namespace SourceVault.Import;

/// <summary>
/// --delete-source: after an import has verified every file, removes from the original folder exactly the files that
/// were imported, and only those still identical to what the vault now holds. Each file is opened exclusively,
/// re-hashed through that handle, overwritten with zeros, flushed and deleted. Skipped files, excluded folders
/// (.git, node_modules, ...) and the folder itself are kept; folders left empty are removed. Links are never
/// followed. Prints paths and counts only.
///
/// The overwrite is best effort: SSDs, copy-on-write file systems, shadow copies, backups and cloud sync can keep
/// earlier copies, so this is a cleanup, not a secure erase.
/// </summary>
internal static class SourceCleanup
{
    internal const string ConfirmWord = "DELETE";
    internal const string Changed = "changed since the import";
    internal const string Missing = "no longer there";
    internal const string LinkInPath = "a link or junction is in its path";
    internal const string ReadOnly = "read-only";
    internal const string Locked = "in use or access denied";
    internal const string HardLinked = "has other hard links";

    /// <summary>Asks for the typed confirmation, then cleans up. Returns the number of files deleted (0 if declined).</summary>
    internal static int Run(ImportPlan plan, string projectName, Func<string?> readLine, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine($"--delete-source: {plan.Files.Count} imported file(s) can now be deleted from {plan.Root}");
        output.WriteLine("  Only files still identical to the verified copy in the vault are deleted; each is overwritten with zeros first.");
        if (plan.Skipped.Count > 0) output.WriteLine($"  {plan.Skipped.Count} skipped item(s) stay, including excluded folders.");
        if (plan.Skipped.Any(s => s.Reason == ImportPlan.ExcludedFolder && s.Path.TrimEnd('/').Split('/')[^1].Equals(".git", StringComparison.OrdinalIgnoreCase)))
            output.WriteLine("  WARNING: .git stays, and its history still contains this source. Delete or move it yourself if that matters.");
        output.WriteLine("  Overwriting does not reliably erase data on SSDs, and backups, shadow copies or cloud sync may keep copies.");
        output.WriteLine($"  The only copy will be the vault project \"{projectName}\", readable only on this computer, with no recovery if its TPM is cleared.");
        output.Write($"Type {ConfirmWord} to delete these files, or anything else to keep them: ");
        if (readLine()?.Trim() != ConfirmWord) { output.WriteLine("Kept: nothing was deleted."); return 0; }

        var root = plan.Root;
        var kept = new List<(string Path, string Reason)>();
        var touchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int deleted = 0;
        foreach (var file in plan.Files)
        {
            var reason = DeleteOne(root, file);
            if (reason is null)
            {
                deleted++;
                for (var dir = Path.GetDirectoryName(file.FullPath); dir is not null && IsStrictlyUnder(dir, root); dir = Path.GetDirectoryName(dir)) touchedDirs.Add(dir);
            }
            else kept.Add((file.Path, reason));
        }
        // Deepest first; only empty, real folders; never the root.
        int removedDirs = 0;
        foreach (var dir in touchedDirs.OrderByDescending(d => d.Length))
        {
            try
            {
                var info = new DirectoryInfo(dir);
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0 && !info.EnumerateFileSystemInfos().Any()) { info.Delete(); removedDirs++; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        output.WriteLine($"Deleted {deleted} of {plan.Files.Count} imported file(s) and {removedDirs} emptied folder(s). The folder itself was kept.");
        foreach (var group in kept.GroupBy(k => k.Reason))
        {
            output.WriteLine($"Kept {group.Count()}: {group.Key}");
            foreach (var item in group.Take(8)) output.WriteLine($"    {item.Path}");
            if (group.Count() > 8) output.WriteLine($"    … and {group.Count() - 8} more");
        }
        return deleted;
    }

    /// <summary>Null when deleted; otherwise why the file was kept.</summary>
    internal static string? DeleteOne(string root, ImportCandidate file)
    {
        if (!IsStrictlyUnder(file.FullPath, root)) return LinkInPath;
        // Re-check every folder between the root and the file: one may have been swapped for a junction since the scan.
        for (var dir = Path.GetDirectoryName(file.FullPath); dir is not null && IsStrictlyUnder(dir, root); dir = Path.GetDirectoryName(dir))
        {
            try { if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return LinkInPath; }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return Missing; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Locked; }
        }
        FileAttributes attributes;
        try { attributes = File.GetAttributes(file.FullPath); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return Missing; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Locked; }
        if ((attributes & FileAttributes.ReparsePoint) != 0) return LinkInPath;
        if ((attributes & FileAttributes.ReadOnly) != 0) return ReadOnly;
        byte[]? content = null;
        try
        {
            // Exclusive handle: nothing can change the file between the hash check and the overwrite.
            using (var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                if (stream.Length != file.Length) return Changed;
                if (LinkCount(stream.SafeFileHandle) != 1) return HardLinked;
                content = new byte[stream.Length];
                stream.ReadExactly(content);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(content), file.Sha256)) return Changed;
                stream.Position = 0;
                stream.Write(new byte[content.Length]);
                stream.Flush(flushToDisk: true);
            }
            File.Delete(file.FullPath);
            return null;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return Missing; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Locked; }
        finally { if (content is not null) CryptographicOperations.ZeroMemory(content); }
    }

    private static bool IsStrictlyUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "." && relative != ".." && !relative.StartsWith(@"..\", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    // A file with several hard links shares its data with another name; overwriting it would zero that file too.
    private static uint LinkCount(Microsoft.Win32.SafeHandles.SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var info) ? info.NumberOfLinks : uint.MaxValue;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleFileInformation information);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, LastAccessLow, LastAccessHigh, LastWriteLow, LastWriteHigh; // FILETIMEs: 4-byte aligned
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
}
