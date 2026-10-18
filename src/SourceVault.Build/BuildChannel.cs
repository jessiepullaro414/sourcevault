using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using SourceVault.Contracts;

namespace SourceVault.Build;

/// <summary>A build's result as the vault service receives it.</summary>
public sealed record BuildReply(string State, string? Reason, int? ExitCode, byte[] Log, bool LogTruncated, string? ArtifactFolder, int ArtifactFiles, long ArtifactBytes);

/// <summary>The vault service's view of the build service (the test seam).</summary>
public interface IBuildService
{
    Task<IReadOnlyList<string>> TargetsAsync(CancellationToken ct);
    Task<BuildReply> RunAsync(string target, Guid projectId, int buildNumber, IReadOnlyList<(string Name, byte[] Content)> files, CancellationToken ct);
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
///   {op:"build", target, projectId, buildNumber, fileCount}, then fileCount × {name, contentBase64}
///                                                     -> {ok, state, reason, exitCode, logBase64, logTruncated, artifactFolder, artifactFiles, artifactBytes}
/// Source crosses only this pipe; the build service never sees the vault, its key, or project names.
/// </summary>
public static class BuildChannel
{
    public const string PipeName = "SourceVault.Build.v1";
    public const int MaxFiles = 1024;
    public const int MaxFileBytes = 256 * 1024;
    private static readonly TimeSpan BuildWait = TimeSpan.FromMinutes(70); // longest allowed build plus copying

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

        public async Task<BuildReply> RunAsync(string target, Guid projectId, int buildNumber, IReadOnlyList<(string Name, byte[] Content)> files, CancellationToken ct)
        {
            await using var pipe = await Connect(ct).ConfigureAwait(false);
            await Send(pipe, w =>
            {
                w.WriteString("op", "build"); w.WriteString("target", target); w.WriteString("projectId", projectId.ToString("D"));
                w.WriteNumber("buildNumber", buildNumber); w.WriteNumber("fileCount", files.Count);
            }, ct).ConfigureAwait(false);
            foreach (var (name, content) in files)
                await Send(pipe, w => { w.WriteString("name", name); w.WriteBase64String("contentBase64", content); }, ct, 16384 + content.Length * 4 / 3).ConfigureAwait(false);
            using var reply = await Receive(pipe, BuildWait, ct).ConfigureAwait(false);
            var root = Ok(reply);
            var log = root.GetProperty("logBase64").GetBytesFromBase64();
            int? exit = root.GetProperty("exitCode").ValueKind == JsonValueKind.Number ? root.GetProperty("exitCode").GetInt32() : null;
            string? reason = root.GetProperty("reason").ValueKind == JsonValueKind.String ? root.GetProperty("reason").GetString() : null;
            string? folder = root.GetProperty("artifactFolder").ValueKind == JsonValueKind.String ? root.GetProperty("artifactFolder").GetString() : null;
            return new BuildReply(root.GetProperty("state").GetString()!, reason, exit, log, root.GetProperty("logTruncated").GetBoolean(), folder,
                root.GetProperty("artifactFiles").GetInt32(), root.GetProperty("artifactBytes").GetInt64());
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
    public static async Task ServeAsync(Stream pipe, Func<BuildConfig> loadConfig, Func<BuildTarget, IDockerEngine> engineFor, CancellationToken ct)
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
        if (!Guid.TryParseExact(root.GetProperty("projectId").GetString(), "D", out var projectId) || !root.GetProperty("buildNumber").TryGetInt32(out int number) || number < 1
            || !root.GetProperty("fileCount").TryGetInt32(out int count) || count is < 0 or > MaxFiles)
        { await Refuse(pipe, "InvalidRequest", ct).ConfigureAwait(false); return; }
        var files = new List<(string, byte[])>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                using var frame = await Receive(pipe, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                var name = frame.RootElement.GetProperty("name").GetString() ?? "";
                if (!ValidSourceName(name) || files.Any(f => string.Equals(f.Item1, name, StringComparison.OrdinalIgnoreCase))) { await Refuse(pipe, "InvalidRequest", ct).ConfigureAwait(false); return; }
                var content = frame.RootElement.GetProperty("contentBase64").GetBytesFromBase64();
                if (content.Length > MaxFileBytes) { CryptographicOperations.ZeroMemory(content); await Refuse(pipe, "InvalidRequest", ct).ConfigureAwait(false); return; }
                files.Add((name, content));
            }
            if (target is null) { await Refuse(pipe, "UnknownTarget", ct).ConfigureAwait(false); return; }
            BuildOutcome outcome;
            using (var engine = engineFor(target))
                outcome = await BuildRunner.RunAsync(target, config.ArtifactRoot, projectId, number, files, engine, ct).ConfigureAwait(false);
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
                }, ct, 16384 + outcome.Log.Length * 4 / 3).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(outcome.Log); }
        }
        finally { BuildRunner.Zero(files); }
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
