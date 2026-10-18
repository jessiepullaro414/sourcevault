using System.Security.Cryptography;
using System.Text.Json;
using SourceVault.Client;
using SourceVault.Storage;

namespace SourceVault.Import;

/// <summary>
/// Imports a scanned folder into a NEW vault project over the service pipe, then decrypts every imported file and
/// compares it byte-for-byte with the original. Prints paths and counts, never file content. The originals are not
/// touched.
/// </summary>
internal static class Importer
{
    internal const int MaxBatchFiles = 256;
    internal const int MaxBatchBytes = 600 * 1024; // raw content per request; base64 keeps the frame well under 1 MiB

    internal static void PrintPlan(ImportPlan plan, TextWriter output)
    {
        output.WriteLine($"{plan.Files.Count} file(s) to import, {plan.TotalBytes / 1024.0:N1} KiB in total.");
        foreach (var group in plan.Skipped.GroupBy(s => s.Reason).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"Skipped {group.Count()}: {group.Key}");
            foreach (var item in group.Take(8)) output.WriteLine($"    {item.Path}");
            if (group.Count() > 8) output.WriteLine($"    … and {group.Count() - 8} more");
        }
        if (plan.Files.Count > RevisionStore.MaximumFiles)
            output.WriteLine($"Too many files: a project holds at most {RevisionStore.MaximumFiles}. Exclude more folders with --exclude.");
    }

    /// <summary>Returns 0 when every file was imported and verified.</summary>
    internal static int Run(ImportPlan plan, string projectName, Func<VaultClient> connect, TextWriter output)
    {
        if (plan.Files.Count == 0) { output.WriteLine("Nothing to import."); return 1; }
        if (plan.Files.Count > RevisionStore.MaximumFiles) { output.WriteLine("Not imported: too many files."); return 1; }
        using var client = connect();
        Expect(client.Begin(), "connect");
        var created = client.Call("CreateProject", w => w.WriteString("name", projectName));
        if (created.Code == "AlreadyExists") { output.WriteLine($"A project named \"{projectName}\" already exists. Imports always go into a new project; choose another name."); return 1; }
        var projectId = Expect(created, "create project").GetProperty("projectId").GetString()!;
        var snapshot = Expect(created, "create project").GetProperty("snapshotId").GetString()!;
        output.WriteLine($"Created project \"{projectName}\".");

        int imported = 0;
        foreach (var batch in Batches(plan.Files))
        {
            var contents = new List<byte[]>();
            try
            {
                foreach (var file in batch)
                {
                    var bytes = File.ReadAllBytes(file.FullPath);
                    contents.Add(bytes);
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), file.Sha256))
                        throw new ImportException($"{file.Path} changed after it was scanned. {imported} of {plan.Files.Count} files are in \"{projectName}\"; run the import again into a new project.");
                }
                var response = client.Call("ImportFiles", w =>
                {
                    w.WriteString("projectId", projectId);
                    w.WriteString("expectedSnapshotId", snapshot);
                    w.WriteStartArray("files");
                    for (int i = 0; i < batch.Count; i++) { w.WriteStartObject(); w.WriteString("name", batch[i].Path); w.WriteBase64String("contentBase64", contents[i]); w.WriteEndObject(); }
                    w.WriteEndArray();
                });
                if (!response.Ok) throw new ImportException($"The vault refused a batch ({response.Code}). {imported} of {plan.Files.Count} files are in \"{projectName}\".");
                snapshot = response.Body.GetProperty("snapshotId").GetString()!;
                imported += batch.Count;
                output.WriteLine($"Imported {imported} of {plan.Files.Count}…");
            }
            finally { foreach (var c in contents) CryptographicOperations.ZeroMemory(c); }
        }

        // Verify: decrypt every file through the service and compare with the hash taken at scan time.
        var listing = Expect(client.Call("ListFiles", w => w.WriteString("projectId", projectId)), "list files");
        var byName = listing.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("fileId").GetString()!, StringComparer.Ordinal);
        int verified = 0, mismatched = 0;
        foreach (var file in plan.Files)
        {
            if (!byName.TryGetValue(file.Path, out var fileId)) { mismatched++; output.WriteLine($"MISSING  {file.Path}"); continue; }
            var opened = client.Call("OpenDocument", w => { w.WriteString("projectId", projectId); w.WriteString("fileId", fileId); w.WriteString("expectedSnapshotId", snapshot); });
            if (!opened.Ok) { mismatched++; output.WriteLine($"UNREADABLE {file.Path} ({opened.Code})"); continue; }
            var back = opened.Body.GetProperty("contentBase64").GetBytesFromBase64();
            try { if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(back), file.Sha256)) verified++; else { mismatched++; output.WriteLine($"DIFFERENT {file.Path}"); } }
            finally { CryptographicOperations.ZeroMemory(back); }
            if (verified % 100 == 0) output.WriteLine($"Verified {verified} of {plan.Files.Count}…");
        }
        client.Call("EndSession");
        if (mismatched > 0) { output.WriteLine($"FAILED: {mismatched} file(s) did not verify. Keep your originals."); return 1; }
        output.WriteLine($"Done: {imported} file(s) imported into \"{projectName}\"; all {verified} decrypted and matched the originals byte for byte.");
        output.WriteLine("Your original folder was not changed. Deleting it does not securely erase it from an SSD.");
        return 0;
    }

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
