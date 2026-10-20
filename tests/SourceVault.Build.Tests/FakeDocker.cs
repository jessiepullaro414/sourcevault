using System.Formats.Tar;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SourceVault.Build;

/// <summary>
/// An in-memory container for build tests: a file system (path -> bytes), a scripted build command, and a log of calls.
/// The build "runs" by applying <see cref="Build"/> to the files, so tests can check what was copied in and out.
/// </summary>
sealed class FakeContainer
{
    public bool Running = true;
    public string NetworkMode = "none";
    public string Os = "linux";
    public readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal); // absolute container paths
    public readonly List<string> Calls = [];
    public Func<FakeContainer, (int ExitCode, string Output)> Build = _ => (0, "ok\n");
    public bool Hang;                     // the build never finishes (timeout)
    public bool PutFails;                 // the engine fails while receiving the source
    public Func<string, Stream?>? ArchiveOverride;
}

sealed class FakeEngine(FakeContainer container) : IDockerEngine
{
    public Task<ContainerInfo> InspectAsync(string name, CancellationToken ct) { container.Calls.Add("inspect " + name); return Task.FromResult(new ContainerInfo(container.Running, container.NetworkMode, container.Os)); }

    public async Task PutArchiveAsync(string name, string path, Stream tar, CancellationToken ct)
    {
        container.Calls.Add("put " + path);
        using var reader = new TarReader(tar);
        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(true, ct)) is not null)
            if (entry.EntryType == TarEntryType.RegularFile)
            {
                if (container.PutFails) throw new IOException("synthetic engine failure mid-upload");
                var data = new MemoryStream(); entry.DataStream?.CopyTo(data); // an empty file has no data stream
                container.Files[path + "/" + entry.Name] = data.ToArray();
            }
    }

    public async Task<ExecResult> ExecAsync(string name, IReadOnlyList<string> command, string? workingDirectory, TimeSpan timeout, int maxOutput, CancellationToken ct)
    {
        if (command[0] is "/bin/sh" && command.Contains("sourcevault-clean"))
        {
            container.Calls.Add("clean");
            foreach (var key in container.Files.Keys.Where(k => k.StartsWith(command[^2] + "/") || k.StartsWith(command[^1] + "/")).ToList()) container.Files.Remove(key);
            return new ExecResult(0, [], false, false);
        }
        container.Calls.Add("exec " + string.Join(' ', command));
        if (container.Hang) { try { await Task.Delay(timeout, ct); } catch (TaskCanceledException) { } return new ExecResult(null, Encoding.UTF8.GetBytes("partial"), false, true); }
        var (exit, output) = container.Build(container);
        var bytes = Encoding.UTF8.GetBytes(output);
        return new ExecResult(exit, bytes.Length > maxOutput ? bytes[..maxOutput] : bytes, bytes.Length > maxOutput, false);
    }

    public Task<Stream?> GetArchiveAsync(string name, string path, CancellationToken ct)
    {
        container.Calls.Add("get " + path);
        if (container.ArchiveOverride is not null) return Task.FromResult(container.ArchiveOverride(path));
        var prefix = path + "/";
        var entries = container.Files.Where(f => f.Key.StartsWith(prefix)).ToList();
        if (entries.Count == 0 && !container.Files.Keys.Any(k => k == path + "/.keep")) return Task.FromResult<Stream?>(null);
        var folder = path[(path.LastIndexOf('/') + 1)..];
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, folder + "/"));
            foreach (var (key, data) in entries)
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, folder + "/" + key[prefix.Length..]) { DataStream = new MemoryStream(data) });
        }
        stream.Position = 0;
        return Task.FromResult<Stream?>(stream);
    }

    public Task RestartAsync(string name, CancellationToken ct) { container.Calls.Add("restart"); return Task.CompletedTask; }
    public void Dispose() { }
}

/// <summary>
/// A minimal Docker Engine HTTP/1.1 server on a named pipe, for testing <see cref="DockerEngineClient"/>'s wire format:
/// one request per connection (Connection: close), Content-Length bodies, close-delimited exec output.
/// </summary>
sealed class FakeDockerServer : IDisposable
{
    public readonly string PipeName = "sv-fake-docker-" + Guid.NewGuid().ToString("N");
    public readonly List<string> Requests = [];
    public readonly Dictionary<string, byte[]> Uploads = [];
    public byte[] ExecOutput = Encoding.UTF8.GetBytes("built\n");
    public int ExitCode = 0;
    public byte[]? Archive;
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;

    public FakeDockerServer() => loop = Task.Run(ServeAsync);

    private async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try { await pipe.WaitForConnectionAsync(stop.Token); }
            catch { pipe.Dispose(); return; }
            _ = Task.Run(async () => { try { await HandleAsync(pipe); } catch { } finally { pipe.Dispose(); } });
        }
    }

    private async Task HandleAsync(Stream pipe)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n")) { if (await pipe.ReadAsync(one) == 0) return; head.Append((char)one[0]); }
        var lines = head.ToString().Split("\r\n");
        var requestLine = lines[0].Split(' ');
        string method = requestLine[0], target = requestLine[1];
        int length = lines.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Select(p => int.Parse(p[1].Trim())).FirstOrDefault();
        var body = new byte[length];
        await pipe.ReadExactlyAsync(body);
        lock (Requests) Requests.Add(method + " " + target);
        async Task Reply(int status, string? json = null, byte[]? raw = null, string type = "application/json", bool closeDelimited = false)
        {
            var payload = raw ?? (json is null ? [] : Encoding.UTF8.GetBytes(json));
            var header = $"HTTP/1.1 {status} X\r\nContent-Type: {type}\r\n" + (closeDelimited ? "" : $"Content-Length: {payload.Length}\r\n") + "Connection: close\r\n\r\n";
            await pipe.WriteAsync(Encoding.ASCII.GetBytes(header));
            await pipe.WriteAsync(payload);
            await pipe.FlushAsync();
        }
        if (method == "GET" && target == "/v1.41/containers/box/json") await Reply(200, "{\"State\":{\"Running\":true},\"HostConfig\":{\"NetworkMode\":\"none\"},\"Platform\":\"linux\",\"Extra\":[1,2]}");
        else if (method == "GET" && target.StartsWith("/v1.41/containers/missing/")) await Reply(404, "{\"message\":\"secret detail\"}");
        else if (method == "PUT" && target.StartsWith("/v1.41/containers/box/archive?path=")) { lock (Uploads) Uploads[Uri.UnescapeDataString(target.Split("path=")[1])] = body; await Reply(200); }
        else if (method == "POST" && target == "/v1.41/containers/box/exec")
        {
            using var doc = JsonDocument.Parse(body);
            lock (Requests) Requests.Add("cmd " + string.Join(' ', doc.RootElement.GetProperty("Cmd").EnumerateArray().Select(e => e.GetString())) + " in " + doc.RootElement.GetProperty("WorkingDir").GetString());
            await Reply(201, "{\"Id\":\"abc123\"}");
        }
        else if (method == "POST" && target == "/v1.41/exec/abc123/start")
        {
            // Multiplexed: one stdout frame and one stderr frame.
            var frames = new MemoryStream();
            void Frame(byte kind, byte[] data) { frames.Write([kind, 0, 0, 0, (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]); frames.Write(data); }
            Frame(1, ExecOutput); Frame(2, Encoding.UTF8.GetBytes("warning\n"));
            await Reply(200, raw: frames.ToArray(), type: "application/vnd.docker.multiplexed-stream", closeDelimited: true);
        }
        else if (method == "GET" && target == "/v1.41/exec/abc123/json") await Reply(200, $"{{\"ExitCode\":{ExitCode},\"Running\":false}}");
        else if (method == "GET" && target.StartsWith("/v1.41/containers/box/archive?path=")) { if (Archive is null) await Reply(404); else await Reply(200, raw: Archive, type: "application/x-tar"); }
        else if (method == "POST" && target == "/v1.41/containers/box/restart?t=0") await Reply(204);
        else await Reply(500, "{\"message\":\"unexpected\"}");
    }

    public void Dispose() { stop.Cancel(); try { loop.Wait(2000); } catch { } }
}

/// <summary>A source that fails after some chunks, like a vault record failing its check mid-copy.</summary>
sealed class FailingSource(IReadOnlyList<(string Name, byte[] Content)> files, int failAfterChunks) : IBuildSource
{
    private readonly InMemoryBuildSource inner = new(files);
    private int chunks;
    public IReadOnlyList<BuildFile> Files => inner.Files;
    public ValueTask<ReadOnlyMemory<byte>> NextAsync(CancellationToken ct) =>
        ++chunks > failAfterChunks ? throw new BuildSourceException("SourceUnavailable") : inner.NextAsync(ct);
}
