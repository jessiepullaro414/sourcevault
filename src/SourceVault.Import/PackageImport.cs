using SourceVault.Client;
using SourceVault.Packaging;

namespace SourceVault.Import;

/// <summary>
/// Encrypted packages from another computer (docs/package-import-design.md). The importer only carries ciphertext:
/// the vault service verifies, decrypts and imports the package, so plaintext never exists in this process or on
/// this PC's disk. Prints names, counts and fingerprints only.
/// </summary>
internal static class PackageImport
{
    internal const int PartBytes = 512 * 1024;

    /// <summary>Uploads a package and creates the new project <paramref name="project"/> from it. 0 on success.</summary>
    internal static int Run(string packagePath, string project, Func<VaultClient> connect, TextWriter output)
    {
        var info = new FileInfo(packagePath);
        if (!info.Exists) throw new ImportException("Package file not found.");
        if (info.Length > Package.MaxPackageBytes) throw new ImportException("That file is larger than any SourceVault package can be.");
        var package = File.ReadAllBytes(info.FullName); // ciphertext
        output.WriteLine($"Package: {info.Name} ({package.Length:N0} bytes). Sending it to the vault service, which checks and decrypts it.");
        using var client = connect();
        Expect(client.Begin(ConsolePassword.Ask), "BeginSession");
        var begin = client.Call("BeginPackageImport", w => w.WriteNumber("length", package.Length));
        if (!begin.Ok) return Report(begin.Code, project, output);
        for (int offset = 0; offset < package.Length; offset += PartBytes)
        {
            int at = offset, count = Math.Min(PartBytes, package.Length - offset);
            var part = client.Call("PackageImportPart", w => { w.WriteNumber("offset", at); w.WriteBase64String("dataBase64", package.AsSpan(at, count)); });
            if (!part.Ok) return Report(part.Code, project, output);
        }
        var done = client.Call("FinishPackageImport", w => w.WriteString("projectName", project));
        if (!done.Ok) return Report(done.Code, project, output);
        output.WriteLine($"Imported {done.Body.GetProperty("imported").GetInt32()} file(s) into the new project \"{project}\".");
        output.WriteLine($"The package (id {done.Body.GetProperty("packageId").GetString()}) was signed by the registered computer \"{done.Body.GetProperty("sender").GetString()}\".");
        output.WriteLine("Every file was checked against the package's own hashes before it was saved. You can delete the package file: it is ciphertext either way.");
        client.Call("EndSession");
        return 0;
    }

    /// <summary>Writes this PC's recipient file (public) for SourceVault Pack on the other computer. 0 on success.</summary>
    internal static int ExportRecipient(string path, Func<VaultClient> connect, TextWriter output)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full)) throw new ImportException("That file already exists; choose a new file name.");
        using var client = connect();
        Expect(client.Begin(ConsolePassword.Ask), "BeginSession");
        var reply = client.Call("GetImportRecipient");
        if (!reply.Ok) return Report(reply.Code, null, output);
        var recipient = PublicKeyFile.Create(PublicKeyFile.RecipientFormat, reply.Body.GetProperty("computer").GetString()!,
            Convert.FromBase64String(reply.Body.GetProperty("publicKey").GetString()!));
        if (Convert.ToHexStringLower(recipient.Fingerprint) != reply.Body.GetProperty("fingerprint").GetString()) throw new ImportException("The service returned an inconsistent recipient key.");
        client.Call("EndSession");
        using (var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write)) file.Write(recipient.ToJson());
        output.WriteLine($"Wrote the recipient file for \"{recipient.Computer}\": {full}");
        output.WriteLine($"Check code: {recipient.FingerprintText}");
        output.WriteLine("It holds no secrets. Copy it to the other computer, and check that SourceVault Pack shows this same check code.");
        return 0;
    }

    private static int Report(string code, string? project, TextWriter output)
    {
        output.WriteLine(code switch
        {
            "PackageRejected" => "Refused: the package is damaged, was changed after it was made, was made for another computer's recipient file, or contains files the vault does not accept. Nothing was imported.",
            "UnknownSender" => "Refused: the package is signed by a computer that is not registered on this PC. An administrator registers that computer's sender file with Setup-SourceVault.ps1 -RegisterSender. Nothing was imported.",
            "AlreadyExists" => $"Refused: a project named \"{project}\" already exists. Choose another name. Nothing was imported.",
            "LimitExceeded" => "Refused: the package is larger than the vault accepts.",
            "InvalidRequest" => "Refused: the project name is not allowed (1-64 characters, no leading or trailing spaces).",
            "Unavailable" => "Package import is not available: the import key is not set up on this PC (run Setup-SourceVault.ps1 -Update), another package import is running, or the vault could not save.",
            _ => $"The service refused the request ({code}). Nothing was imported.",
        });
        return 1;
    }

    private static void Expect(VaultResponse response, string operation)
    {
        if (!response.Ok) throw new ImportException($"{operation} failed ({response.Code}).");
    }
}
