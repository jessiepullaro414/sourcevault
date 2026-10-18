using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace SourceVault.Build;

public sealed record ContainerInfo(bool Running, string NetworkMode, string Os);
public sealed record ExecResult(int? ExitCode, byte[] Output, bool Truncated, bool TimedOut);

/// <summary>A Docker engine call failed. Carries only the HTTP status (0: not reachable), never response text.</summary>
public sealed class DockerException(int status) : Exception($"Docker engine error {status}.")
{
    public int Status { get; } = status;
}

/// <summary>The few Docker Engine API calls a build needs. The interface is the test seam.</summary>
public interface IDockerEngine : IDisposable
{
    Task<ContainerInfo> InspectAsync(string container, CancellationToken ct);
    Task PutArchiveAsync(string container, string path, Stream tar, CancellationToken ct);
    /// <summary>Runs a command in the container, collecting combined output up to <paramref name="maxOutput"/> bytes.</summary>
    Task<ExecResult> ExecAsync(string container, IReadOnlyList<string> command, string? workingDirectory, TimeSpan timeout, int maxOutput, CancellationToken ct);
    /// <summary>A tar of <paramref name="path"/>, or null if it does not exist. The caller disposes and bounds it.</summary>
    Task<Stream?> GetArchiveAsync(string container, string path, CancellationToken ct);
    Task RestartAsync(string container, CancellationToken ct);
}

/// <summary>
/// Docker Engine API v1.41 over a named pipe (<c>npipe://./pipe/docker_engine</c>) or loopback TCP with TLS client
/// certificates (the WSL engine). The server certificate must chain to the configured CA; the system trust store is
/// not used. Responses are bounded; error bodies are never read or reported.
/// </summary>
public sealed class DockerEngineClient : IDockerEngine
{
    private const string Api = "/v1.41";
    private const int MaxJsonBytes = 1024 * 1024;
    private readonly HttpClient http;
    private readonly X509Certificate2? clientCertificate, authority;

    public DockerEngineClient(Uri engine, string? caFile = null, string? clientCertificateThumbprint = null)
    {
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        if (engine.Scheme == "npipe")
        {
            var pipeName = engine.AbsolutePath.StartsWith("/pipe/", StringComparison.Ordinal) ? engine.AbsolutePath["/pipe/".Length..] : throw new ArgumentException("Invalid engine.");
            handler.ConnectCallback = async (_, ct) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await pipe.ConnectAsync(5000, ct).ConfigureAwait(false); return pipe; }
                catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
            };
            http = new HttpClient(handler) { BaseAddress = new Uri("http://docker"), Timeout = Timeout.InfiniteTimeSpan };
        }
        else if (engine.Scheme == "tcp")
        {
            if (caFile is null || clientCertificateThumbprint is null) throw new ArgumentException("TLS settings required.");
            authority = X509CertificateLoader.LoadCertificateFromFile(caFile);
            clientCertificate = FindClientCertificate(clientCertificateThumbprint);
            var trusted = authority;
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = [clientCertificate],
                RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null && ChainsTo(certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()), trusted),
            };
            http = new HttpClient(handler) { BaseAddress = new Uri($"https://{engine.Host}:{engine.Port}"), Timeout = Timeout.InfiniteTimeSpan };
        }
        else throw new ArgumentException("Invalid engine.");
    }

    private static X509Certificate2 FindClientCertificate(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
        return found.Count == 1 && found[0].HasPrivateKey ? found[0] : throw new DockerException(0);
    }

    // The engine's certificate must chain to the configured CA only (custom root trust, no system roots).
    private static bool ChainsTo(X509Certificate2 certificate, X509Certificate2 authority)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }

    public async Task<ContainerInfo> InspectAsync(string container, CancellationToken ct)
    {
        using var json = await GetJsonAsync($"{Api}/containers/{Uri.EscapeDataString(container)}/json", ct).ConfigureAwait(false);
        var root = json.RootElement;
        bool running = root.TryGetProperty("State", out var state) && state.TryGetProperty("Running", out var r) && r.ValueKind == JsonValueKind.True;
        string network = root.TryGetProperty("HostConfig", out var host) && host.TryGetProperty("NetworkMode", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
        string os = root.TryGetProperty("Platform", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
        return new ContainerInfo(running, network, os);
    }

    public async Task PutArchiveAsync(string container, string path, Stream tar, CancellationToken ct)
    {
        using var content = new StreamContent(tar);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");
        using var response = await http.PutAsync($"{Api}/containers/{Uri.EscapeDataString(container)}/archive?path={Uri.EscapeDataString(path)}", content, ct).ConfigureAwait(false);
        Ensure(response);
    }

    public async Task<ExecResult> ExecAsync(string container, IReadOnlyList<string> command, string? workingDirectory, TimeSpan timeout, int maxOutput, CancellationToken ct)
    {
        var create = JsonSerializer.SerializeToUtf8Bytes(new { AttachStdout = true, AttachStderr = true, Tty = false, Cmd = command, WorkingDir = workingDirectory ?? "" });
        string id;
        using (var created = await PostJsonAsync($"{Api}/containers/{Uri.EscapeDataString(container)}/exec", create, ct).ConfigureAwait(false))
            id = created.RootElement.GetProperty("Id").GetString() ?? throw new DockerException(0);
        if (id.Length is 0 or > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new DockerException(0);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var output = new MemoryStream();
        bool truncated = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/exec/{id}/start") { Content = Json(JsonSerializer.SerializeToUtf8Bytes(new { Detach = false, Tty = false })) };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            Ensure(response);
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            // Multiplexed stream: 8-byte header (stream type, 3 zero bytes, big-endian length), then the payload.
            var header = new byte[8];
            var buffer = new byte[81920];
            while (await ReadFullyAsync(stream, header, limit.Token).ConfigureAwait(false))
            {
                long remaining = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
                while (remaining > 0)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), limit.Token).ConfigureAwait(false);
                    if (read == 0) throw new DockerException(0);
                    remaining -= read;
                    int keep = (int)Math.Min(read, maxOutput - output.Length);
                    if (keep > 0) output.Write(buffer, 0, keep);
                    if (keep < read) truncated = true; // keep reading so the command is not blocked on a full pipe
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ExecResult(null, output.ToArray(), truncated, true);
        }
        using var inspect = await GetJsonAsync($"{Api}/exec/{id}/json", ct).ConfigureAwait(false);
        var exit = inspect.RootElement.TryGetProperty("ExitCode", out var code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : (int?)null;
        return new ExecResult(exit, output.ToArray(), truncated, false);
    }

    public async Task<Stream?> GetArchiveAsync(string container, string path, CancellationToken ct)
    {
        var response = await http.GetAsync($"{Api}/containers/{Uri.EscapeDataString(container)}/archive?path={Uri.EscapeDataString(path)}", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) { response.Dispose(); return null; }
        try { Ensure(response); return new OwnedStream(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), response); }
        catch { response.Dispose(); throw; }
    }

    public async Task RestartAsync(string container, CancellationToken ct)
    {
        using var response = await http.PostAsync($"{Api}/containers/{Uri.EscapeDataString(container)}/restart?t=0", null, ct).ConfigureAwait(false);
        Ensure(response);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        Ensure(response);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> PostJsonAsync(string path, byte[] body, CancellationToken ct)
    {
        using var response = await http.PostAsync(path, Json(body), ct).ConfigureAwait(false);
        Ensure(response);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    private static ByteArrayContent Json(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxJsonBytes) throw new DockerException(0);
            buffer.Write(chunk, 0, read);
        }
        try { return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { throw new DockerException(0); }
    }

    private static async Task<bool> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) return total == 0 ? false : throw new DockerException(0);
            total += read;
        }
        return true;
    }

    private static void Ensure(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new DockerException((int)response.StatusCode);
    }

    public void Dispose()
    {
        http.Dispose();
        clientCertificate?.Dispose();
        authority?.Dispose();
    }

    // A response stream that also disposes its response.
    private sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); owner.Dispose(); } base.Dispose(disposing); }
    }
}

