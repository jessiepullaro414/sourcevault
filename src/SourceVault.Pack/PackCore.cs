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
        plan.Files.Count == 0 ? "Nothing to pack: that folder has no file the vault can take."
        : plan.LimitProblem; // over 25,000 files or 8 GiB

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
    /// Streams every planned file (each must be unchanged since the scan) into a package for <paramref name="recipient"/>
    /// (format version 2: any file, one 1 MiB chunk in memory), signs it and writes a new package file. Nothing is left
    /// behind on failure. Returns the package id and size.
    /// </summary>
    internal static (Guid Id, long Bytes) Write(ImportPlan plan, PublicKeyFile recipient, IPackageSigner signer, string target, string? name = null)
    {
        if (Problem(plan) is { } problem) throw new PackException(problem);
        target = Path.GetFullPath(target);
        if (File.Exists(target)) throw new PackException($"{target} already exists; choose a new file name.");
        var sender = SenderFile(signer);
        var files = plan.Files.Select(c => new PackageFile(c.Path, c.Length, c.Sha256,
            () => new FileStream(c.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))).ToList();
        Guid id;
        try
        {
            using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            id = Package.Write(stream, name ?? SuggestedName(plan), files, recipient, sender, signer.SignHash);
            stream.Flush(true);
        }
        catch (Exception e)
        {
            try { File.Delete(target); } catch (IOException) { }
            if (e is PackageSourceChangedException changed) throw new PackException($"{changed.Path} changed since the folder was scanned. Nothing was written; scan again.");
            if (e is IOException or UnauthorizedAccessException && e is not PackException) throw new PackException("A file could not be read while packing. Nothing was written; scan again.");
            throw;
        }
        return (id, new FileInfo(target).Length);
    }

    // Key files allow letters, digits, '.', '-' and '_' in computer names.
    internal static string Computer()
    {
        var clean = new string(Environment.MachineName.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').Take(63).ToArray());
        return clean.Length > 0 && char.IsAsciiLetterOrDigit(clean[0]) ? clean : "SOURCE-PC";
    }
}
