using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using SourceVault.Contracts;

namespace SourceVault.Build;

/// <summary>A build's result as the vault service receives it.</summary>
public sealed record BuildReply(string State, string? Reason, int? ExitCode, byte[] Log, bool LogTruncated, string? ArtifactFolder, int ArtifactFiles, long ArtifactBytes,
    int ArtifactsDropped = 0);

/// <summary>The vault service's view of the build service (the test seam).</summary>
public interface IBuildService
{
    Task<IReadOnlyList<string>> TargetsAsync(CancellationToken ct);
    Task<BuildReply> RunAsync(string target, Guid projectId, int buildNumber, IBuildSource source, CancellationToken ct);
}

/// <summary>The build service is not installed, not running, misconfigured, or answered unexpectedly.</summary>
public sealed class BuildServiceUnavailableException(string reason) : Exception("Build service unavailable: " + reason)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// The private channel between the vault service (client) and the build service (server), over a pipe only those two
/// accounts can use. Frames are <see cref="FrameCodec"/> JSON. One connection per request:
///   {op:"targets"}                                   -> {ok, targets[] | code}
///   build, version 2 (any-file design section 6; both services are installed together):
///     {op:"build", version:2, target, projectId, buildNumber, fileCount, totalBytes}
///                                                     -> {ok:true, accepted:true} | {ok:false, code}
///     then table frames {files:[{name, length}]} (at most 1,000 each) until fileCount entries
///                                                     -> {ok:true, ready:true} | {ok:false, code}
/// Every refusal comes while the vault side is waiting to read, never while it is still writing (the service reads
/// the whole table before refusing a bad entry), so neither side can block the other.
///     then data frames {dataBase64} (at most 512 KiB, never spanning files) with the files' bytes in table order,
///     or {abort} if the vault cannot read a record
///                                                     -> {ok, state, reason, exitCode, logBase64, logTruncated, artifactFolder, artifactFiles, artifactBytes, artifactsDropped}
/// Every name and limit is checked before any byte is copied. Source crosses only this pipe; the build service never
/// sees the vault, its key, or project names.
/// </summary>
public static class BuildChannel
{
    public const string PipeName = "SourceVault.Build.v1";
    public const int Version = 2;
    public const int MaxFiles = 25000;
    public const long MaxFileBytes = 1L << 30;
    public const long MaxTotalBytes = 8L << 30;
    public const int MaxChunkBytes = 512 * 1024;
    private const int TableEntriesPerFrame = 1000;
    private static readonly TimeSpan BuildWait = TimeSpan.FromMinutes(70); // longest allowed build after the copy

    // ---- client (vault service) ----------------------------------------------------------------------------------
    public sealed class Client(Func<CancellationToken, Task<Stream>> connect) : IBuildService
    {
        public async Task<IReadOnlyList<string>> TargetsAsync(CancellationToken ct)
        {
            await using var pipe = await Connect(ct).ConfigureAwait(false);
            await Send(pipe, w => w.WriteString("op", "targets"), ct).ConfigureAwait(false);
            using var reply = await Receive(pipe, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            var root = Ok(reply);
            return root.GetProperty("targets").EnumerateArray().Select(t => t.GetString()!).ToList();
        }

        public async Task<BuildReply> RunAsync(string target, Guid projectId, int buildNumber, IBuildSource source, CancellationToken ct)
        {
            await using var pipe = await Connect(ct).ConfigureAwait(false);
            var files = source.Files;
            long total = files.Sum(f => f.Length);
            await Send(pipe, w =>
            {
                w.WriteString("op", "build"); w.WriteNumber("version", Version); w.WriteString("target", target); w.WriteString("projectId", projectId.ToString("D"));
                w.WriteNumber("buildNumber", buildNumber); w.WriteNumber("fileCount", files.Count); w.WriteNumber("totalBytes", total);
            }, ct).ConfigureAwait(false);
            using (var accepted = await Receive(pipe, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) Ok(accepted); // refused: version, counts, target
            for (int at = 0; at < files.Count; at += TableEntriesPerFrame)
            {
                var page = files.Skip(at).Take(TableEntriesPerFrame).ToList();
                await Send(pipe, w =>
                {
                    w.WriteStartArray("files");
                    foreach (var f in page) { w.WriteStartObject(); w.WriteString("name", f.Name); w.WriteNumber("length", f.Length); w.WriteEndObject(); }
                    w.WriteEndArray();
                }, ct, 65536).ConfigureAwait(false);
            }
            using (var ready = await Receive(pipe, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) Ok(ready); // refused: unknown target, limits
            try
            {
                for (long sent = 0; sent < total;)
                {
                    var chunk = await source.NextAsync(ct).ConfigureAwait(false);
                    if (chunk.IsEmpty || chunk.Length > MaxChunkBytes) throw new BuildSourceException("SourceUnavailable");
                    await Send(pipe, w => w.WriteBase64String("dataBase64", chunk.Span), ct, 16384 + chunk.Length * 4 / 3).ConfigureAwait(false);
                    sent += chunk.Length;
                }
            }
            catch (BuildSourceException e) { await Send(pipe, w => w.WriteString("abort", e.Reason), ct).ConfigureAwait(false); }
            using var reply = await Receive(pipe, BuildWait + BuildRunner.CopyTimeout, ct).ConfigureAwait(false);
            var root = Ok(reply);
            var log = root.GetProperty("logBase64").GetBytesFromBase64();
            int? exit = root.GetProperty("exitCode").ValueKind == JsonValueKind.Number ? root.GetProperty("exitCode").GetInt32() : null;
            string? reason = root.GetProperty("reason").ValueKind == JsonValueKind.String ? root.GetProperty("reason").GetString() : null;
            string? folder = root.GetProperty("artifactFolder").ValueKind == JsonValueKind.String ? root.GetProperty("artifactFolder").GetString() : null;
            return new BuildReply(root.GetProperty("state").GetString()!, reason, exit, log, root.GetProperty("logTruncated").GetBoolean(), folder,
                root.GetProperty("artifactFiles").GetInt32(), root.GetProperty("artifactBytes").GetInt64(),
                root.TryGetProperty("artifactsDropped", out var dropped) ? dropped.GetInt32() : 0); // absent from an older build service
        }

        private async Task<Stream> Connect(CancellationToken ct)
        {
            try { return await connect(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { throw new BuildServiceUnavailableException("NotRunning"); }
        }

        private static JsonElement Ok(JsonDocument reply)
        {
            var root = reply.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return root;
            throw new BuildServiceUnavailableException(root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString()! : "Refused");
        }
    }

    // ---- server (build service) ----------------------------------------------------------------------------------
    /// <summary>Serves one connection. <paramref name="loadConfig"/> is read per request so admin edits apply at once.</summary>
    /// <param name="artifactRootTrusted">
    /// The build service's check of the output folder and its parents (no links, nobody else can change them; internal
    /// security review 2026-10-20). Refused with ArtifactFolderUntrusted before the vault sends anything. Null: tests only.
    /// </param>
    public static async Task ServeAsync(Stream pipe, Func<BuildConfig> loadConfig, Func<BuildTarget, IDockerEngine> engineFor, CancellationToken ct,
        Func<string, bool>? artifactRootTrusted = null)
    {
        using var request = await Receive(pipe, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        var root = request.RootElement;
        var op = root.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
        BuildConfig config;
        try { config = loadConfig(); }
        catch (BuildConfigException) { await Refuse(pipe, "ConfigInvalid", ct).ConfigureAwait(false); return; }
        if (op == "targets")
        {
            await Send(pipe, w => { w.WriteBoolean("ok", true); w.WriteStartArray("targets"); foreach (var t in config.Targets) w.WriteStringValue(t.Name); w.WriteEndArray(); }, ct).ConfigureAwait(false);
            return;
        }
        if (op != "build") { await Refuse(pipe, "InvalidRequest", ct).ConfigureAwait(false); return; }
        var targetName = root.GetProperty("target").GetString();
        var target = config.Targets.FirstOrDefault(t => t.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
        if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out int v) || v != Version
            || !Guid.TryParseExact(root.GetProperty("projectId").GetString(), "D", out var projectId) || !root.GetProperty("buildNumber").TryGetInt32(out int number) || number < 1
            || !root.GetProperty("fileCount").TryGetInt32(out int count) || count is < 0 or > MaxFiles
            || !root.GetProperty("totalBytes").TryGetInt64(out long total) || total is < 0 or > MaxTotalBytes)
        { await Refuse(pipe, "InvalidRequest", ct).ConfigureAwait(false); return; }
        if (target is null) { await Refuse(pipe, "UnknownTarget", ct).ConfigureAwait(false); return; }
        if (artifactRootTrusted is not null && !artifactRootTrusted(config.ArtifactRoot)) { await Refuse(pipe, "ArtifactFolderUntrusted", ct).ConfigureAwait(false); return; }
        await Send(pipe, w => { w.WriteBoolean("ok", true); w.WriteBoolean("accepted", true); }, ct).ConfigureAwait(false);
        // The whole table, every name and limit, before a single byte is copied. A bad entry is remembered and the rest of
        // the table still read, so the refusal comes when the vault side is listening.
        var files = new List<BuildFile>(count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long sum = 0;
        int seen = 0;
        bool valid = true;
        while (seen < count)
        {
            using var frame = await Receive(pipe, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            if (!frame.RootElement.TryGetProperty("files", out var page) || page.ValueKind != JsonValueKind.Array || page.GetArrayLength() is 0 or > TableEntriesPerFrame
                || seen + page.GetArrayLength() > count)
            { await Refuse(pipe, "InvalidRequest", ct).ConfigureAwait(false); return; } // not this protocol: no point reading on
            foreach (var entry in page.EnumerateArray())
            {
                seen++;
                var name = entry.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
                if (!valid || !ValidSourceName(name) || !names.Add(name) || !entry.TryGetProperty("length", out var l) || !l.TryGetInt64(out long length)
                    || length is < 0 or > MaxFileBytes || (sum += length) > total)
                { valid = false; continue; }
                files.Add(new BuildFile(name, length));
            }
        }
        if (!valid || sum != total) { await Refuse(pipe, "InvalidRequest", ct).ConfigureAwait(false); return; }
        await Send(pipe, w => { w.WriteBoolean("ok", true); w.WriteBoolean("ready", true); }, ct).ConfigureAwait(false);
        var source = new PipeBuildSource(pipe, files);
        try
        {
            BuildOutcome outcome;
            using (var engine = engineFor(target))
                outcome = await BuildRunner.RunAsync(target, config.ArtifactRoot, projectId, number, source, engine, ct).ConfigureAwait(false);
            // A copy that stopped early (engine error, timeout) still reads the rest, so the vault side never blocks writing.
            await source.DrainAsync(ct).ConfigureAwait(false);
            try
            {
                await Send(pipe, w =>
                {
                    w.WriteBoolean("ok", true);
                    w.WriteString("state", outcome.State.ToString());
                    if (outcome.Reason is null) w.WriteNull("reason"); else w.WriteString("reason", outcome.Reason);
                    if (outcome.ExitCode is { } exit) w.WriteNumber("exitCode", exit); else w.WriteNull("exitCode");
                    w.WriteBase64String("logBase64", outcome.Log);
                    w.WriteBoolean("logTruncated", outcome.LogTruncated);
                    if (outcome.Artifacts is { } a) w.WriteString("artifactFolder", a.Folder); else w.WriteNull("artifactFolder");
                    w.WriteNumber("artifactFiles", outcome.Artifacts?.Files ?? 0);
                    w.WriteNumber("artifactBytes", outcome.Artifacts?.Bytes ?? 0);
                    w.WriteNumber("artifactsDropped", outcome.Artifacts?.DroppedCount ?? 0);
                }, ct, 16384 + outcome.Log.Length * 4 / 3).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(outcome.Log); }
        }
        finally { source.Dispose(); }
    }

    // The build service's view of the copy: data frames from the vault side, checked against the table.
    private sealed class PipeBuildSource(Stream pipe, IReadOnlyList<BuildFile> files) : IBuildSource, IDisposable
    {
        private int file;
        private long fileLeft = files.Count > 0 ? files[0].Length : 0;
        private byte[]? last;
        private bool aborted;
        public IReadOnlyList<BuildFile> Files => files;

        public async ValueTask<ReadOnlyMemory<byte>> NextAsync(CancellationToken ct)
        {
            Zero();
            while (file < files.Count && fileLeft == 0) { file++; fileLeft = file < files.Count ? files[file].Length : 0; }
            if (file >= files.Count) return ReadOnlyMemory<byte>.Empty;
            if (aborted) throw new BuildSourceException("SourceUnavailable");
            using var frame = await Receive(pipe, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            var root = frame.RootElement;
            if (root.TryGetProperty("abort", out _)) { aborted = true; throw new BuildSourceException("SourceUnavailable"); }
            if (!root.TryGetProperty("dataBase64", out var data) || data.ValueKind != JsonValueKind.String) { aborted = true; throw new BuildSourceException("InvalidRequest"); }
            last = data.GetBytesFromBase64();
            if (last.Length == 0 || last.Length > MaxChunkBytes || last.Length > fileLeft) { aborted = true; throw new BuildSourceException("InvalidRequest"); }
            fileLeft -= last.Length;
            return last;
        }

        /// <summary>Reads and discards whatever the vault side still sends, up to the table's end or an abort.</summary>
        public async Task DrainAsync(CancellationToken ct)
        {
            try { while (!aborted && !(await NextAsync(ct).ConfigureAwait(false)).IsEmpty) { } }
            catch (BuildSourceException) { }
            Zero();
        }

        private void Zero() { if (last is not null) CryptographicOperations.ZeroMemory(last); last = null; }
        public void Dispose() => Zero();
    }

    // Source names as the vault stores them: '/'-separated relative paths of valid Windows names.
    internal static bool ValidSourceName(string name) =>
        name.Length is > 0 and <= 260 && name.Split('/') is { Length: <= 32 } parts && parts.All(ArtifactPublisher.ValidSegment);

    private static Task Refuse(Stream pipe, string code, CancellationToken ct) =>
        Send(pipe, w => { w.WriteBoolean("ok", false); w.WriteString("code", code); }, ct);

    private static async Task Send(Stream pipe, Action<Utf8JsonWriter> body, CancellationToken ct, int capacity = 4096)
    {
        var buffer = new ArrayBufferWriter<byte>(capacity);
        try
        {
            using (var w = new Utf8JsonWriter(buffer)) { w.WriteStartObject(); body(w); w.WriteEndObject(); }
            await FrameCodec.WriteAsync(pipe, buffer.WrittenMemory, ct).ConfigureAwait(false);
        }
        finally { buffer.Clear(); } // zeroes the written bytes, which may hold source or build output
    }

    private static async Task<JsonDocument> Receive(Stream pipe, TimeSpan wait, CancellationToken ct)
    {
        var frame = await FrameCodec.ReadAfterIdleAsync(pipe, wait, ct).ConfigureAwait(false) ?? throw new BuildServiceUnavailableException("Closed");
        try { return JsonDocument.Parse(frame.ToArray(), new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException) { throw new BuildServiceUnavailableException("InvalidReply"); }
        finally { CryptographicOperations.ZeroMemory(frame); }
    }
}
