using System.Security.Cryptography;
using System.Text.Json;
using SourceVault.Client;
using SourceVault.Storage;

namespace SourceVault.Import;

/// <summary>
/// Imports a scanned folder into a NEW vault project over the service pipe: any file up to 1 GiB, small ones in
/// batches and large ones uploaded in parts. Then it checks every file's SHA-256 in the vault against the scan and has
/// the service decrypt and check every record. Prints paths and counts, never file content. The originals are not
/// touched.
/// </summary>
internal static class Importer
{
    internal const int MaxBatchFiles = 256;
    internal const int MaxBatchBytes = 600 * 1024; // raw content per request; base64 keeps the frame well under 1 MiB

    internal static void PrintPlan(ImportPlan plan, TextWriter output)
    {
        output.WriteLine($"{plan.Files.Count} file(s) to import, {plan.TotalBytes / 1048576.0:N1} MB in total.");
        foreach (var group in plan.Skipped.GroupBy(s => s.Reason).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"Skipped {group.Count()}: {group.Key}");
            foreach (var item in group.Take(8)) output.WriteLine($"    {item.Path}");
            if (group.Count() > 8) output.WriteLine($"    … and {group.Count() - 8} more");
        }
        if (plan.LimitProblem is { } problem) output.WriteLine(problem);
    }

    /// <summary>Returns 0 when every file was imported and verified.</summary>
    internal static int Run(ImportPlan plan, string projectName, Func<VaultClient> connect, TextWriter output)
    {
        if (plan.Files.Count == 0) { output.WriteLine("Nothing to import."); return 1; }
        if (plan.LimitProblem is { } problem) { output.WriteLine("Not imported. " + problem); return 1; }
        using var client = connect();
        Expect(client.Begin(ConsolePassword.Ask), "connect");
        var created = client.Call("CreateProject", w => w.WriteString("name", projectName));
        if (created.Code == "AlreadyExists") { output.WriteLine($"A project named \"{projectName}\" already exists. Imports always go into a new project; choose another name."); return 1; }
        var projectId = Expect(created, "create project").GetProperty("projectId").GetString()!;
        var snapshot = Expect(created, "create project").GetProperty("snapshotId").GetString()!;
        output.WriteLine($"Created project \"{projectName}\".");

        int imported = 0;
        string Partial() => $"{imported} of {plan.Files.Count} files are in \"{projectName}\"; run the import again into a new project.";
        // Files up to 256 KiB go in batches; larger ones (any size up to 1 GiB) are uploaded in parts and committed one by one.
        foreach (var batch in Batches(plan.Files.Where(f => f.Length <= InlineBytes).ToList()))
        {
            var contents = new List<byte[]>();
            try
            {
                foreach (var file in batch)
                {
                    var bytes = File.ReadAllBytes(file.FullPath);
                    contents.Add(bytes);
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), file.Sha256))
                        throw new ImportException($"{file.Path} changed after it was scanned. {Partial()}");
                }
                var response = client.Call("ImportFiles", w =>
                {
                    w.WriteString("projectId", projectId);
                    w.WriteString("expectedSnapshotId", snapshot);
                    w.WriteStartArray("files");
                    for (int i = 0; i < batch.Count; i++) { w.WriteStartObject(); w.WriteString("name", batch[i].Path); w.WriteBase64String("contentBase64", contents[i]); w.WriteEndObject(); }
                    w.WriteEndArray();
                });
                if (!response.Ok) throw new ImportException($"The vault refused a batch ({response.Code}). {Partial()}");
                snapshot = response.Body.GetProperty("snapshotId").GetString()!;
                imported += batch.Count;
                output.WriteLine($"Imported {imported} of {plan.Files.Count}…");
            }
            finally { foreach (var c in contents) CryptographicOperations.ZeroMemory(c); }
        }
        var part = new byte[PartBytes];
        try
        {
            foreach (var file in plan.Files.Where(f => f.Length > InlineBytes))
            {
                var begun = client.Call("BeginUpload", w =>
                {
                    w.WriteString("projectId", projectId); w.WriteNull("fileId"); w.WriteNumber("length", file.Length);
                    w.WriteString("sha256", Convert.ToHexStringLower(file.Sha256));
                });
                if (!begun.Ok) throw new ImportException($"The vault refused {file.Path} ({begun.Code}). {Partial()}");
                var uploadId = begun.Body.GetProperty("uploadId").GetString()!;
                using (var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                {
                    if (stream.Length != file.Length) throw new ImportException($"{file.Path} changed after it was scanned. {Partial()}");
                    for (long offset = 0; offset < file.Length;)
                    {
                        int n = stream.ReadAtLeast(part, (int)Math.Min(PartBytes, file.Length - offset), throwOnEndOfStream: false);
                        if (n == 0) throw new ImportException($"{file.Path} changed after it was scanned. {Partial()}");
                        var sent = client.Call("UploadPart", w =>
                        {
                            w.WriteString("uploadId", uploadId); w.WriteNumber("offset", offset); w.WriteBase64String("dataBase64", part.AsSpan(0, n));
                        });
                        // The service checks the SHA-256 at the last byte: a file changed since the scan is refused there.
                        if (!sent.Ok) throw new ImportException(sent.Code == "InvalidRequest" && offset + n == file.Length
                            ? $"{file.Path} changed after it was scanned. {Partial()}" : $"The vault refused {file.Path} ({sent.Code}). {Partial()}");
                        offset += n;
                    }
                }
                var committed = client.Call("CommitUploads", w =>
                {
                    w.WriteString("projectId", projectId); w.WriteString("expectedSnapshotId", snapshot);
                    w.WriteStartArray("files"); w.WriteStartObject(); w.WriteString("uploadId", uploadId); w.WriteString("name", file.Path); w.WriteEndObject(); w.WriteEndArray();
                });
                if (!committed.Ok) throw new ImportException($"The vault refused {file.Path} ({committed.Code}). {Partial()}");
                snapshot = committed.Body.GetProperty("snapshotId").GetString()!;
                imported++;
                output.WriteLine($"Imported {imported} of {plan.Files.Count} ({file.Path}, {file.Length / 1048576.0:N1} MB)…");
            }
        }
        finally { CryptographicOperations.ZeroMemory(part); }

        // Verify: every file's SHA-256 in the vault listing matches the one taken at scan time, then the service decrypts
        // every record once and checks it against its manifest (VerifyProject). Binary content never comes back out.
        var listing = Expect(client.Call("ListFiles", w => w.WriteString("projectId", projectId)), "list files");
        var byName = listing.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!, f => f, StringComparer.Ordinal);
        int matched = 0, mismatched = 0;
        foreach (var file in plan.Files)
        {
            if (!byName.TryGetValue(file.Path, out var entry)) { mismatched++; output.WriteLine($"MISSING   {file.Path}"); continue; }
            var sha = entry.GetProperty("sha256");
            if (sha.ValueKind == System.Text.Json.JsonValueKind.String && sha.GetString() == Convert.ToHexStringLower(file.Sha256) && entry.GetProperty("byteLength").GetInt64() == file.Length) matched++;
            else { mismatched++; output.WriteLine($"DIFFERENT {file.Path}"); }
        }
        var verify = Expect(client.Call("VerifyProject", w => w.WriteString("projectId", projectId)), "verify the project");
        int decrypted = verify.GetProperty("filesVerified").GetInt32(), failed = verify.GetProperty("filesFailed").GetInt32();
        client.Call("EndSession");
        if (mismatched > 0 || failed > 0 || decrypted != plan.Files.Count)
        { output.WriteLine($"FAILED: {mismatched + failed} file(s) did not verify. Keep your originals."); return 1; }
        output.WriteLine($"Done: {imported} file(s) imported into \"{projectName}\"; all {matched} match the originals (SHA-256) and decrypted cleanly in the service.");
        output.WriteLine("Your original folder was not changed. Deleting it does not securely erase it from an SSD.");
        return 0;
    }

    internal const int InlineBytes = 256 * 1024, PartBytes = 512 * 1024;

    internal static IEnumerable<List<ImportCandidate>> Batches(IReadOnlyList<ImportCandidate> files)
    {
        var batch = new List<ImportCandidate>();
        long bytes = 0;
        foreach (var file in files)
        {
            if (batch.Count > 0 && (batch.Count == MaxBatchFiles || bytes + file.Length > MaxBatchBytes)) { yield return batch; batch = []; bytes = 0; }
            batch.Add(file); bytes += file.Length;
        }
        if (batch.Count > 0) yield return batch;
    }

    private static JsonElement Expect(VaultResponse response, string step) =>
        response.Ok ? response.Body : throw new ImportException($"Could not {step} ({response.Code}).");
}

internal sealed class ImportException(string message) : Exception(message);
