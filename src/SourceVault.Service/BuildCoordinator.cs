using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SourceVault.Build;
using SourceVault.Storage;

namespace SourceVault.Service;

/// <summary>One build as operators see it. States: queued, running, succeeded, failed, timedOut, rejected, unavailable.</summary>
internal sealed record BuildRecord(int Number, string Target, string State, string? Reason, int? ExitCode, string? RequestedBy,
    DateTimeOffset RequestedAt, DateTimeOffset? FinishedAt, string? ArtifactFolder, int ArtifactFiles, long ArtifactBytes, bool LogAvailable, bool LogTruncated, int ArtifactsDropped = 0);

/// <summary>
/// Runs builds through the build service (docs/files-history-builds-design.md, section 4). Per target one build runs
/// and up to 4 wait. The project is decrypted only for the copy sent to the build service, and those buffers are
/// zeroed afterwards. Each finished build's record and log are stored encrypted in the project's build-log store
/// (builds.json plus logs/&lt;n&gt;.log, the newest 100 listed); queued and running builds exist only in memory.
/// </summary>
internal sealed class BuildCoordinator(ProjectCatalog catalog, IBuildService service, TimeProvider? clock = null)
{
    internal const int MaxWaiting = 4;
    internal const int MaxListed = 100;
    private const string IndexName = "builds.json";
    private static readonly UTF8Encoding Lenient = new(false, false);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(Guid Project, int Number), BuildRecord> active = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, object> numbering = new();
    private (DateTimeOffset At, IReadOnlyList<string> Names)? targets;

    /// <summary>Raised when a build finishes (tests).</summary>
    internal event Action<Guid, BuildRecord>? Finished;

    internal IReadOnlyList<string> Targets()
    {
        var cached = targets;
        if (cached is { } c && time.GetUtcNow() - c.At < TimeSpan.FromSeconds(30)) return c.Names;
        var names = service.TargetsAsync(CancellationToken.None).GetAwaiter().GetResult();
        targets = (time.GetUtcNow(), names);
        return names;
    }

    /// <summary>Queues a build. Throws KeyNotFoundException for an unknown target, InvalidOperationException when the queue is full.</summary>
    internal int Request(Guid projectId, string target, string? actor)
    {
        var name = Targets().FirstOrDefault(t => t.Equals(target, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException();
        if (active.Values.Count(r => r.Target.Equals(name, StringComparison.OrdinalIgnoreCase)) > MaxWaiting) throw new InvalidOperationException("Queue full.");
        BuildRecord record;
        lock (numbering.GetOrAdd(projectId, _ => new object()))
        {
            var stored = ReadIndex(projectId);
            int next = Math.Max(stored.Count == 0 ? 0 : stored.Max(r => r.Number),
                active.Keys.Where(k => k.Project == projectId).Select(k => k.Number).DefaultIfEmpty(0).Max()) + 1;
            record = new BuildRecord(next, name, "queued", null, null, actor, time.GetUtcNow(), null, null, 0, 0, false, false);
            active[(projectId, next)] = record;
        }
        _ = Task.Run(() => RunAsync(projectId, record));
        return record.Number;
    }

    /// <summary>The project's builds, newest first: queued and running ones, then the stored ones.</summary>
    internal IReadOnlyList<BuildRecord> List(Guid projectId) =>
        active.Where(a => a.Key.Project == projectId).Select(a => a.Value)
            .Concat(ReadIndex(projectId)).GroupBy(r => r.Number).Select(g => g.First()).OrderByDescending(r => r.Number).ToList();

    internal BuildRecord Status(Guid projectId, int number) =>
        active.TryGetValue((projectId, number), out var running) ? running
            : ReadIndex(projectId).FirstOrDefault(r => r.Number == number) ?? throw new KeyNotFoundException();

    /// <summary>A finished build's log (UTF-8). The caller zeroes it.</summary>
    internal byte[] OpenLog(Guid projectId, int number)
    {
        var record = Status(projectId, number);
        if (!record.LogAvailable) throw new KeyNotFoundException();
        var store = catalog.OpenBuildLog(projectId);
        var snapshot = store.List();
        var file = snapshot.Files.FirstOrDefault(f => f.LogicalName == $"logs/{number}.log") ?? throw new KeyNotFoundException();
        return store.Open(snapshot.SnapshotId, file.FileId);
    }

    private async Task RunAsync(Guid projectId, BuildRecord queued)
    {
        var slot = slots.GetOrAdd(queued.Target, _ => new SemaphoreSlim(1, 1));
        await slot.WaitAsync().ConfigureAwait(false);
        var files = new List<(string Name, byte[] Content)>();
        BuildRecord finished;
        byte[] log = [];
        try
        {
            active[(projectId, queued.Number)] = queued with { State = "running" };
            var store = catalog.OpenProject(projectId);
            var snapshot = store.List();
            store.ReadSnapshot(snapshot.SnapshotId, (file, content) => files.Add((file.LogicalName, content.ToArray())));
            var reply = await service.RunAsync(queued.Target, projectId, queued.Number, files, CancellationToken.None).ConfigureAwait(false);
            log = reply.Log;
            var state = reply.State switch
            {
                "Succeeded" => "succeeded", "Failed" => "failed", "TimedOut" => "timedOut", "Rejected" => "rejected", _ => "unavailable",
            };
            finished = queued with { State = state, Reason = reply.Reason, ExitCode = reply.ExitCode, FinishedAt = time.GetUtcNow(), ArtifactFolder = reply.ArtifactFolder,
                ArtifactFiles = reply.ArtifactFiles, ArtifactBytes = reply.ArtifactBytes, ArtifactsDropped = reply.ArtifactsDropped, LogTruncated = reply.LogTruncated };
        }
        catch (BuildServiceUnavailableException e) { finished = queued with { State = "unavailable", Reason = e.Reason, FinishedAt = time.GetUtcNow() }; }
        catch (Exception e) when (e is not OutOfMemoryException) { finished = queued with { State = "unavailable", Reason = "VaultError", FinishedAt = time.GetUtcNow() }; }
        finally { BuildRunner.Zero(files); slot.Release(); }
        try { finished = Store(projectId, finished, log); }
        catch (Exception e) when (e is not OutOfMemoryException) { } // e.g. the project was deleted meanwhile: the record stays in memory
        finally { CryptographicOperations.ZeroMemory(log); }
        lock (numbering.GetOrAdd(projectId, _ => new object()))
        {
            if (finished.LogAvailable) active.TryRemove((projectId, finished.Number), out _);
            else active[(projectId, finished.Number)] = finished;
        }
        Finished?.Invoke(projectId, finished);
    }

    // Persists the record and its log (bounded to a document: the end of the output is kept) in one commit.
    private BuildRecord Store(Guid projectId, BuildRecord record, byte[] log)
    {
        var text = Lenient.GetString(log); // build output may be any bytes; invalid UTF-8 becomes U+FFFD
        var bytes = Encoding.UTF8.GetBytes(text);
        const int limit = 250 * 1024;
        bool truncated = record.LogTruncated;
        if (bytes.Length > limit)
        {
            int start = bytes.Length - limit;
            while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80) start++; // not inside a character
            var tail = bytes[start..];
            CryptographicOperations.ZeroMemory(bytes);
            bytes = [.. "[earlier output truncated]\n"u8, .. tail];
            CryptographicOperations.ZeroMemory(tail);
            truncated = true;
        }
        try
        {
            var stored = record with { LogAvailable = true, LogTruncated = truncated };
            var store = catalog.OpenBuildLog(projectId);
            lock (numbering.GetOrAdd(projectId, _ => new object()))
            {
                var index = ReadIndex(projectId).Where(r => r.Number != stored.Number).Append(stored).OrderByDescending(r => r.Number).Take(MaxListed).ToList();
                var snapshot = store.List();
                store.CommitChanges(snapshot.SnapshotId, [new StoreChange.Write($"logs/{stored.Number}.log", bytes), new StoreChange.Write(IndexName, SerializeIndex(index))], record.RequestedBy);
                indexCache[projectId] = index;
            }
            return stored;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private readonly ConcurrentDictionary<Guid, IReadOnlyList<BuildRecord>> indexCache = new();

    private IReadOnlyList<BuildRecord> ReadIndex(Guid projectId)
    {
        if (indexCache.TryGetValue(projectId, out var cached)) return cached;
        var store = catalog.OpenBuildLog(projectId);
        var snapshot = store.List();
        var file = snapshot.Files.FirstOrDefault(f => f.LogicalName == IndexName);
        IReadOnlyList<BuildRecord> records = [];
        if (file is not null)
        {
            var content = store.Open(snapshot.SnapshotId, file.FileId);
            try { records = ParseIndex(content); }
            finally { CryptographicOperations.ZeroMemory(content); }
        }
        indexCache[projectId] = records;
        return records;
    }

    private static byte[] SerializeIndex(IReadOnlyList<BuildRecord> records) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1,
        builds = records.Select(r => new
        {
            number = r.Number, target = r.Target, state = r.State, reason = r.Reason, exitCode = r.ExitCode, requestedBy = r.RequestedBy,
            requestedAt = r.RequestedAt.ToUnixTimeMilliseconds(), finishedAt = r.FinishedAt?.ToUnixTimeMilliseconds(),
            artifactFolder = r.ArtifactFolder, artifactFiles = r.ArtifactFiles, artifactBytes = r.ArtifactBytes, artifactsDropped = r.ArtifactsDropped, logTruncated = r.LogTruncated,
        }),
    });

    private static IReadOnlyList<BuildRecord> ParseIndex(byte[] content)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 6 });
        var root = document.RootElement;
        if (root.GetProperty("version").GetInt32() != 1) throw new InvalidDataException();
        static string? Str(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.String ? e.GetProperty(name).GetString() : null;
        return root.GetProperty("builds").EnumerateArray().Take(MaxListed).Select(e => new BuildRecord(
            e.GetProperty("number").GetInt32(), Str(e, "target") ?? throw new InvalidDataException(), Str(e, "state") ?? throw new InvalidDataException(),
            Str(e, "reason"), e.GetProperty("exitCode").ValueKind == JsonValueKind.Number ? e.GetProperty("exitCode").GetInt32() : null, Str(e, "requestedBy"),
            DateTimeOffset.FromUnixTimeMilliseconds(e.GetProperty("requestedAt").GetInt64()),
            e.GetProperty("finishedAt").ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeMilliseconds(e.GetProperty("finishedAt").GetInt64()) : null,
            Str(e, "artifactFolder"), e.GetProperty("artifactFiles").GetInt32(), e.GetProperty("artifactBytes").GetInt64(), true,
            e.GetProperty("logTruncated").GetBoolean(),
            e.TryGetProperty("artifactsDropped", out var dropped) ? dropped.GetInt32() : 0)).ToList(); // absent in records stored before 2026-10-18
    }
}
