using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SourceVault.Client;

namespace SourceVault.LabClient;

/// <summary>
/// LAB ONLY commands. They print names, sizes and hash prefixes, never document content, so this client cannot
/// export source even if real data is ever stored.
/// </summary>
internal static class LabCommands
{
    internal const string CheckProject = "Lab client check";
    internal const string CheckFile = "check.txt";

    internal static int List(Func<VaultClient> connect, TextWriter output)
    {
        using var client = connect();
        Expect(client.Begin(), "BeginSession");
        var projects = Expect(client.Call("ListProjects"), "ListProjects").GetProperty("projects");
        output.WriteLine($"{projects.GetArrayLength()} project(s)");
        foreach (var project in projects.EnumerateArray())
        {
            var id = project.GetProperty("projectId").GetString()!;
            output.WriteLine($"- {project.GetProperty("name").GetString()} ({project.GetProperty("fileCount").GetInt32()} files)");
            var files = Expect(client.Call("ListFiles", w => w.WriteString("projectId", id)), "ListFiles").GetProperty("files");
            foreach (var f in files.EnumerateArray())
                output.WriteLine($"    {f.GetProperty("name").GetString()}  {f.GetProperty("byteLength").GetInt32()} bytes");
        }
        client.Call("EndSession");
        return 0;
    }

    /// <summary>End-to-end exercise of the service through the real pipe. Returns the number of failed steps.</summary>
    internal static int Check(Func<VaultClient> connect, TextWriter output)
    {
        int failures = 0;
        void Report(bool ok, string what, Stopwatch? timer = null)
        {
            if (!ok) failures++;
            output.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}{(timer is null ? "" : $" ({timer.Elapsed.TotalMilliseconds:F0} ms)")}");
        }

        using (var client = connect())
        {
            var t = Stopwatch.StartNew();
            Report(client.Begin().Ok, "session started; capability issued to this connection only", t);

            t.Restart();
            var projects = Expect(client.Call("ListProjects"), "ListProjects").GetProperty("projects").EnumerateArray().ToList();
            Report(true, $"listed {projects.Count} project(s); every project was authenticated at service startup", t);

            var check = projects.FirstOrDefault(p => p.GetProperty("name").GetString() == CheckProject);
            string projectId;
            if (check.ValueKind == JsonValueKind.Undefined)
            {
                t.Restart();
                projectId = Expect(client.Call("CreateProject", w => w.WriteString("name", CheckProject)), "CreateProject").GetProperty("projectId").GetString()!;
                Report(true, $"created project \"{CheckProject}\"", t);
            }
            else projectId = check.GetProperty("projectId").GetString()!;

            var listing = Expect(client.Call("ListFiles", w => w.WriteString("projectId", projectId)), "ListFiles");
            var before = listing.GetProperty("snapshotId").GetString()!;
            var existing = listing.GetProperty("files").EnumerateArray().FirstOrDefault(f => f.GetProperty("name").GetString() == CheckFile);
            // Fictional content with a fresh nonce, so each run proves a new write round-trips.
            var content = Encoding.UTF8.GetBytes($"SourceVault lab client check\nnonce: {Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}\nwritten: {DateTime.UtcNow:O}\n");
            t.Restart();
            VaultResponse saved;
            string fileId;
            if (existing.ValueKind == JsonValueKind.Undefined)
            {
                saved = client.Call("CreateFile", w => { w.WriteString("projectId", projectId); w.WriteString("name", CheckFile); w.WriteString("expectedSnapshotId", before); w.WriteBase64String("contentBase64", content); });
                fileId = saved.Ok ? saved.Body.GetProperty("fileId").GetString()! : "";
                Report(saved.Ok, $"created {CheckFile} ({content.Length} bytes, encrypted by the service)", t);
            }
            else
            {
                fileId = existing.GetProperty("fileId").GetString()!;
                saved = client.Call("ApplyEdits", w => { w.WriteString("projectId", projectId); w.WriteString("fileId", fileId); w.WriteString("expectedSnapshotId", before); w.WriteBase64String("contentBase64", content); });
                Report(saved.Ok, $"saved a new revision of {CheckFile} ({content.Length} bytes)", t);
            }
            if (!saved.Ok) return failures + 1;
            var after = saved.Body.GetProperty("snapshotId").GetString()!;

            t.Restart();
            var opened = Expect(client.Call("OpenDocument", w => { w.WriteString("projectId", projectId); w.WriteString("fileId", fileId); w.WriteString("expectedSnapshotId", after); }), "OpenDocument");
            var back = opened.GetProperty("contentBase64").GetBytesFromBase64();
            Report(CryptographicOperations.FixedTimeEquals(back, content), $"reopened and decrypted: {back.Length} bytes, SHA-256 {Convert.ToHexStringLower(SHA256.HashData(back))[..12]}… matches what was written", t);
            CryptographicOperations.ZeroMemory(back);

            var stale = client.Call("ApplyEdits", w => { w.WriteString("projectId", projectId); w.WriteString("fileId", fileId); w.WriteString("expectedSnapshotId", before); w.WriteBase64String("contentBase64", content); });
            Report(stale.Code == "RevisionConflict", $"an edit based on the old snapshot was refused ({stale.Code})");
            CryptographicOperations.ZeroMemory(content);

            var demo = projects.FirstOrDefault(p => p.GetProperty("name").GetString()!.StartsWith("Synthetic demo", StringComparison.Ordinal));
            if (demo.ValueKind != JsonValueKind.Undefined)
            {
                var demoId = demo.GetProperty("projectId").GetString()!;
                var demoFiles = Expect(client.Call("ListFiles", w => w.WriteString("projectId", demoId)), "ListFiles");
                var first = demoFiles.GetProperty("files")[0];
                t.Restart();
                var demoDoc = client.Call("OpenDocument", w => { w.WriteString("projectId", demoId); w.WriteString("fileId", first.GetProperty("fileId").GetString()!); w.WriteString("expectedSnapshotId", demoFiles.GetProperty("snapshotId").GetString()!); });
                var demoBytes = demoDoc.Ok ? demoDoc.Body.GetProperty("contentBase64").GetBytesFromBase64() : null;
                var demoLength = demoBytes?.Length ?? -1;
                if (demoBytes is not null) CryptographicOperations.ZeroMemory(demoBytes);
                Report(demoDoc.Ok && demoLength == first.GetProperty("byteLength").GetInt32(), $"decrypted {first.GetProperty("name").GetString()} from \"{demo.GetProperty("name").GetString()}\" ({demoLength} bytes)", t);
            }
            else output.WriteLine("SKIP: no \"Synthetic demo\" project (run New-SyntheticProject.ps1 to include one)");

            var end = client.Call("EndSession");
            bool closed;
            try { client.Call("ListProjects"); closed = false; } catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException) { closed = true; }
            Report(end.Ok && closed, "EndSession closed the connection");
        }

        using (var intruder = connect())
        {
            intruder.Begin();
            var forged = intruder.Call("ListProjects", capabilityOverride: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            bool closed;
            try { intruder.Call("ListProjects"); closed = false; } catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException) { closed = true; }
            Report(forged.Code == "Unauthorized" && closed, "a forged capability was refused and the connection closed");
        }
        output.WriteLine(failures == 0 ? "All lab client checks passed." : $"{failures} check(s) failed.");
        return failures;
    }

    internal const string PowerLossProject = "Lab power-loss check";
    internal const string CounterFile = "counter.txt";
    private static byte[] CounterContent(int count) => Encoding.UTF8.GetBytes($"SourceVault power-loss check (fictional)\ncount: {count}\n");

    /// <summary>
    /// For the power-loss lab test: saves the fictional counter file <paramref name="saves"/> times, printing
    /// "saved N" only after the service confirmed each save. Cut the power while it runs, then run save-check.
    /// </summary>
    internal static int SaveLoop(Func<VaultClient> connect, TextWriter output, int saves)
    {
        using var client = connect();
        Expect(client.Begin(), "BeginSession");
        var (projectId, fileId, snapshot, count) = Counter(client, create: true);
        output.WriteLine($"starting at count {count}; cut the power any time");
        for (int i = 0; i < saves; i++)
        {
            var content = CounterContent(++count);
            var saved = client.Call("ApplyEdits", w => { w.WriteString("projectId", projectId); w.WriteString("fileId", fileId!); w.WriteString("expectedSnapshotId", snapshot); w.WriteBase64String("contentBase64", content); });
            snapshot = Expect(saved, "ApplyEdits").GetProperty("snapshotId").GetString()!;
            output.WriteLine($"saved {count}"); output.Flush();
        }
        client.Call("EndSession");
        return 0;
    }

    /// <summary>After the power came back: prints the count the vault holds (only this fictional file's number).</summary>
    internal static int SaveCheck(Func<VaultClient> connect, TextWriter output)
    {
        using var client = connect();
        Expect(client.Begin(), "BeginSession");
        var (_, fileId, _, count) = Counter(client, create: false);
        client.Call("EndSession");
        if (fileId is null) { output.WriteLine($"FAIL: no \"{PowerLossProject}\" project or {CounterFile}; run save-loop first."); return 1; }
        output.WriteLine($"PASS: the vault opened and {CounterFile} decrypted; it holds count {count}.");
        output.WriteLine("Compare with the last \"saved N\" line before the power cut: the count must be N or N+1 (a save in flight may or may not have landed).");
        return 0;
    }

    // The counter project's ids, current snapshot and count (0 when the file doesn't exist yet).
    private static (string ProjectId, string? FileId, string Snapshot, int Count) Counter(VaultClient client, bool create)
    {
        var projects = Expect(client.Call("ListProjects"), "ListProjects").GetProperty("projects").EnumerateArray().ToList();
        var project = projects.FirstOrDefault(p => p.GetProperty("name").GetString() == PowerLossProject);
        string projectId;
        if (project.ValueKind != JsonValueKind.Undefined) projectId = project.GetProperty("projectId").GetString()!;
        else if (create) projectId = Expect(client.Call("CreateProject", w => w.WriteString("name", PowerLossProject)), "CreateProject").GetProperty("projectId").GetString()!;
        else return ("", null, "", 0);
        var listing = Expect(client.Call("ListFiles", w => w.WriteString("projectId", projectId)), "ListFiles");
        var snapshot = listing.GetProperty("snapshotId").GetString()!;
        var file = listing.GetProperty("files").EnumerateArray().FirstOrDefault(f => f.GetProperty("name").GetString() == CounterFile);
        if (file.ValueKind == JsonValueKind.Undefined)
        {
            if (!create) return (projectId, null, snapshot, 0);
            var made = Expect(client.Call("CreateFile", w => { w.WriteString("projectId", projectId); w.WriteString("name", CounterFile); w.WriteString("expectedSnapshotId", snapshot); w.WriteBase64String("contentBase64", CounterContent(0)); }), "CreateFile");
            return (projectId, made.GetProperty("fileId").GetString()!, made.GetProperty("snapshotId").GetString()!, 0);
        }
        var fileId = file.GetProperty("fileId").GetString()!;
        var text = Encoding.UTF8.GetString(Expect(client.Call("OpenDocument", w => { w.WriteString("projectId", projectId); w.WriteString("fileId", fileId); w.WriteString("expectedSnapshotId", snapshot); }), "OpenDocument").GetProperty("contentBase64").GetBytesFromBase64());
        var line = text.Split('\n').FirstOrDefault(l => l.StartsWith("count: ", StringComparison.Ordinal));
        if (line is null || !int.TryParse(line["count: ".Length..], out int count)) throw new LabClientException($"{CounterFile} is not a counter file");
        return (projectId, fileId, snapshot, count);
    }

    private static JsonElement Expect(VaultResponse response, string operation) =>
        response.Ok ? response.Body : throw new LabClientException($"{operation} returned {response.Code}");
}

internal sealed class LabClientException(string message) : Exception(message);
