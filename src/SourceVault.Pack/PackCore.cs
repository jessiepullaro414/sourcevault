using System.Security.Cryptography;
using SourceVault.Import;
using SourceVault.Packaging;

namespace SourceVault.Pack;

internal sealed class PackException(string message) : Exception(message);

/// <summary>
/// What SourceVault Pack does, shared by the command line and the window so both pick, check and encrypt exactly the
/// same way (docs/package-import-design.md). Nothing here prints or shows file content.
/// </summary>
internal static class PackCore
{
    /// <summary>A strict read of the vault PC's recipient file.</summary>
    internal static PublicKeyFile ReadRecipient(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new PackException("Recipient file not found.");
        if (info.Length > PublicKeyFile.MaxFileBytes) throw new KeyFileException("The file is larger than 8 KiB.");
        return PublicKeyFile.Parse(File.ReadAllBytes(info.FullName), PublicKeyFile.RecipientFormat);
    }

    /// <summary>The importer's scan rules: which files of the folder go in, and why the others are skipped.</summary>
    internal static ImportPlan Scan(string folder, IEnumerable<string>? excludes = null) => ImportPlan.Scan(folder, excludes);

    /// <summary>Why a plan can't be packed, or null when it can.</summary>
    internal static string? Problem(ImportPlan plan) =>
        plan.Files.Count == 0 ? "Nothing to pack: no file in that folder is UTF-8 text the vault accepts."
        : plan.Files.Count > Package.MaxFiles ? $"Too many files: a package holds at most {Package.MaxFiles}. Exclude some folders."
        : null;

    /// <summary>The project name suggested to the vault PC: the folder's name.</summary>
    internal static string SuggestedName(ImportPlan plan) => Path.GetFileName(plan.Root.TrimEnd('\\'));

    /// <summary>This computer's public sender file, from its signing key.</summary>
    internal static PublicKeyFile SenderFile(IPackageSigner signer) =>
        PublicKeyFile.Create(PublicKeyFile.SenderFormat, Computer(), signer.SubjectPublicKeyInfo());

    /// <summary>Writes a sender file to a new path (never overwrites).</summary>
    internal static void WriteNew(string path, byte[] contents)
    {
        if (File.Exists(path)) throw new PackException($"{path} already exists; choose another file name.");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        file.Write(contents);
    }

    /// <summary>
    /// Re-reads every planned file (it must be unchanged since the scan), encrypts the set for <paramref name="recipient"/>,
    /// signs it and writes a new package file. Nothing is left behind on failure. Returns the package id and size.
    /// </summary>
    internal static (Guid Id, long Bytes) Write(ImportPlan plan, PublicKeyFile recipient, IPackageSigner signer, string target, string? name = null)
    {
        if (Problem(plan) is { } problem) throw new PackException(problem);
        target = Path.GetFullPath(target);
        if (File.Exists(target)) throw new PackException($"{target} already exists; choose a new file name.");
        var sender = SenderFile(signer);
        var files = new List<(string Path, byte[] Content)>();
        try
        {
            foreach (var candidate in plan.Files)
            {
                var content = File.ReadAllBytes(candidate.FullPath);
                files.Add((candidate.Path, content));
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(content), candidate.Sha256))
                    throw new PackException($"{candidate.Path} changed since the folder was scanned. Nothing was written; scan again.");
            }
            Guid id;
            try
            {
                using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                id = Package.Write(stream, name ?? SuggestedName(plan), files, recipient, sender, signer.SignHash);
                stream.Flush(true);
            }
            catch { try { File.Delete(target); } catch (IOException) { } throw; }
            return (id, new FileInfo(target).Length);
        }
        finally { foreach (var (_, content) in files) CryptographicOperations.ZeroMemory(content); }
    }

    // Key files allow letters, digits, '.', '-' and '_' in computer names.
    internal static string Computer()
    {
        var clean = new string(Environment.MachineName.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').Take(63).ToArray());
        return clean.Length > 0 && char.IsAsciiLetterOrDigit(clean[0]) ? clean : "SOURCE-PC";
    }
}
