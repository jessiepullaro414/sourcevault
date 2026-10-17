using System.ComponentModel;
using System.IO.Pipes;
using System.Text.Json;
using SourceVault.Contracts;

namespace SourceVault.IpcSpike;

internal static class Program
{
    private static readonly Peer Self = NativePipe.Current;
    private static readonly PeerPolicy SameProcess = new(Self.Sid, Self.Session, Self.ProcessId);

    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Windows is required."); return 2; }
        if (args is not ["self-test"])
        {
            Console.Error.WriteLine("Usage: SourceVault.IpcSpike self-test (synthetic local pipes only)");
            return 2;
        }
        try
        {
            await Run("monotonic idle and absolute expiry are terminal", ExpiryTest);
            await Run("metadata session and capability binding", SessionTest);
            await Run("wrong client SID rejected", () => DenialTest(SameProcess with { Sid = "S-1-5-19" }));
            await Run("wrong client session rejected", () => DenialTest(SameProcess with { Session = uint.MaxValue }));
            await Run("wrong client process policy rejected", () => DenialTest(SameProcess with { ProcessId = uint.MaxValue }));
            await Run("wrong server identity rejected", ServerIdentityTest);
            await Run("first instance squatting rejected", SquattingTest);
            await Run("truncated frame rejected", TruncationTest);
            await Run("empty frame rejected", () => MalformedFrameTest(new byte[4], "LimitExceeded"));
            await Run("malformed JSON rejected", () => MalformedFrameTest(new byte[] { 1, 0, 0, 0, 123 }, "InvalidRequest"));
            await Run("caller cancellation of partial frame", DeadlineTest);
            Console.WriteLine("PASS 11 IPC checks; service-account, remote-host, signing, and lock/revocation gates remain unverified.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"FAIL IPC check ({ex.GetType().Name})."); return 1; }
    }

    private static async Task Run(string name, Func<Task> action)
    {
        try { await action(); }
        catch (Win32Exception ex) { Console.Error.WriteLine($"FAIL {name}: Windows error {ex.NativeErrorCode}."); throw; }
        catch (Exception ex) { Console.Error.WriteLine($"FAIL {name}: {ex.GetType().Name}."); throw; }
        Console.WriteLine($"PASS {name}");
    }

    private static async Task SessionTest()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        string name = Name();
        using var server = NativePipe.Create(name, Self.Sid);
        var serving = new SpikeProtocol(SameProcess).ServeAsync(server, cts.Token);
        using (var client = NativePipe.Connect(name, SameProcess))
        {
            using var before = await Exchange(client, "GetBuildStatus", new { capability = "invalid", buildId = Guid.NewGuid().ToString("D") }, cts.Token);
            AssertCode(before, "Unauthorized");
            using var begin = await Exchange(client, "BeginSession", new { projectId = SpikeProtocol.ProjectId.ToString("D") }, cts.Token);
            AssertCode(begin, "Ok");
            string capability = begin.RootElement.GetProperty("body").GetProperty("capability").GetString()!;
            using var status = await Exchange(client, "GetBuildStatus", new { capability, buildId = Guid.NewGuid().ToString("D") }, cts.Token);
            AssertCode(status, "NotFound");
            using var denied = await Exchange(client, "GetBuildStatus", new { capability = Convert.ToBase64String(new byte[32]), buildId = Guid.NewGuid().ToString("D") }, cts.Token);
            AssertCode(denied, "Unauthorized");
            using var unsupported = await Exchange(client, "ListProjects", new { }, cts.Token);
            AssertCode(unsupported, "UnsupportedOperation");
            using var unknownField = await Exchange(client, "BeginSession", new { projectId = SpikeProtocol.ProjectId.ToString("D"), path = "forbidden" }, cts.Token);
            AssertCode(unknownField, "InvalidRequest");
        }
        await ExpectDisconnect(serving);
    }

    private sealed class TestClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }

    private static Task ExpiryTest()
    {
        foreach (bool absolute in new[] { false, true })
        {
            var clock = new TestClock();
            var protocol = new SpikeProtocol(SameProcess, clock);
            using var begin = JsonSerializer.SerializeToDocument(protocol.Handle(ProtocolParser.ParseRequest(Request("BeginSession", new { projectId = SpikeProtocol.ProjectId.ToString("D") }))));
            AssertCode(begin, "Ok");
            string cap = begin.RootElement.GetProperty("body").GetProperty("capability").GetString()!;
            var status = ProtocolParser.ParseRequest(Request("GetBuildStatus", new { capability = cap, buildId = Guid.NewGuid().ToString("D") }));
            if (absolute)
            {
                for (int i = 0; i < 7; i++)
                {
                    clock.Advance(TimeSpan.FromMinutes(4));
                    using var active = JsonSerializer.SerializeToDocument(protocol.Handle(status));
                    AssertCode(active, "NotFound");
                }
                clock.Advance(TimeSpan.FromMinutes(2));
            }
            else clock.Advance(TimeSpan.FromMinutes(5));
            using var expired = JsonSerializer.SerializeToDocument(protocol.Handle(status));
            AssertCode(expired, "SessionExpired");
            using var restart = JsonSerializer.SerializeToDocument(protocol.Handle(ProtocolParser.ParseRequest(Request("BeginSession", new { projectId = SpikeProtocol.ProjectId.ToString("D") }))));
            AssertCode(restart, "SessionExpired");
            using var terminal = JsonSerializer.SerializeToDocument(protocol.Handle(status));
            AssertCode(terminal, "SessionExpired");
        }
        return Task.CompletedTask;
    }

    private static async Task DenialTest(PeerPolicy policy)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        string name = Name();
        using var server = NativePipe.Create(name, Self.Sid);
        var serving = new SpikeProtocol(policy).ServeAsync(server, cts.Token);
        using var client = NativePipe.Connect(name, SameProcess);
        await FrameCodec.WriteAsync(client, Request("BeginSession", new { projectId = SpikeProtocol.ProjectId.ToString("D") }), cts.Token);
        try { await serving; throw new InvalidOperationException("Denied peer was accepted."); }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task ServerIdentityTest()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        string name = Name();
        using var server = NativePipe.Create(name, Self.Sid);
        var connected = server.WaitForConnectionAsync(cts.Token);
        try
        {
            using var rejected = NativePipe.Connect(name, SameProcess with { Sid = "S-1-5-19" });
            throw new InvalidOperationException("Wrong server was accepted.");
        }
        catch (UnauthorizedAccessException) { }
        await connected;
    }

    private static Task SquattingTest()
    {
        string name = Name();
        using var first = NativePipe.Create(name, Self.Sid);
        try
        {
            using var second = NativePipe.Create(name, Self.Sid);
            throw new InvalidOperationException("Second first-instance server was accepted.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 5 or 231) { }
        return Task.CompletedTask;
    }

    private static async Task TruncationTest()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        string name = Name();
        using var server = NativePipe.Create(name, Self.Sid);
        var connected = server.WaitForConnectionAsync(cts.Token);
        using (var client = NativePipe.Connect(name, SameProcess))
        {
            await connected;
            await client.WriteAsync(new byte[] { 20, 0, 0, 0, 123 }, cts.Token);
        }
        try { _ = await FrameCodec.ReadAsync(server, cts.Token); throw new InvalidOperationException("Truncation accepted."); }
        catch (EndOfStreamException) { }
    }

    private static async Task DeadlineTest()
    {
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        string name = Name();
        using var server = NativePipe.Create(name, Self.Sid);
        var connected = server.WaitForConnectionAsync(connectCts.Token);
        using var client = NativePipe.Connect(name, SameProcess);
        await connected;
        await client.WriteAsync(new byte[] { 20, 0, 0, 0, 123 }, connectCts.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try { _ = await FrameCodec.ReadAsync(server, deadline.Token); throw new InvalidOperationException("Deadline ignored."); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
    }

    private static async Task MalformedFrameTest(byte[] frame, string expectedCode)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        string name = Name();
        using var server = NativePipe.Create(name, Self.Sid);
        var serving = new SpikeProtocol(SameProcess).ServeAsync(server, cts.Token);
        using var client = NativePipe.Connect(name, SameProcess);
        await client.WriteAsync(frame, cts.Token);
        try { await serving; throw new InvalidOperationException("Malformed frame was accepted."); }
        catch (ProtocolException ex) when (ex.Code == expectedCode) { }
    }

    private static async Task<JsonDocument> Exchange(NamedPipeClientStream pipe, string operation, object body, CancellationToken token)
    {
        await FrameCodec.WriteAsync(pipe, Request(operation, body), token);
        return JsonDocument.Parse(await FrameCodec.ReadAsync(pipe, token));
    }
    private static byte[] Request(string operation, object body) => JsonSerializer.SerializeToUtf8Bytes(new { version = 1, requestId = Guid.NewGuid().ToString("D"), operation, body });
    private static string Name() => $"SourceVault-IpcTest-{Guid.NewGuid():N}";
    private static void AssertCode(JsonDocument response, string expected)
    {
        if (response.RootElement.GetProperty("code").GetString() != expected) throw new InvalidOperationException("Unexpected response code.");
    }
    private static async Task ExpectDisconnect(Task task)
    {
        try { await task; }
        catch (EndOfStreamException) { }
        catch (IOException) { }
    }
}
