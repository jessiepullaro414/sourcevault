using System.ComponentModel;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SourceVault.Contracts;
using SourceVault.Service;
using SourceVault.Storage;
using SourceVault.Windows;

// In-process protocol v2 tests over real local named pipes, synthetic data and a test-only software key.
// Server and client run as the current user here; production pins the server to NT SERVICE\SourceVault.
if (args is not [var workspaceArg] || !Path.IsPathFullyQualified(workspaceArg)) { Console.Error.WriteLine("usage: SourceVault.Endpoints.Tests ABSOLUTE_WORKSPACE"); return 2; }
string workspace = Path.Combine(workspaceArg, "endpoints-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);
int passed = 0;
void Assert(bool value, string what) { if (!value) throw new Exception("Assertion failed: " + what); }
void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }

var me = WindowsIdentity.GetCurrent().User!;
var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
var nobodyGroup = new SecurityIdentifier("S-1-5-21-1000000001-1000000002-1000000003-4242"); // no token contains this
string self = Environment.ProcessPath!;
// Exercise the client-side helper production clients call so the service can read their executable path.
VaultPipe.AllowServiceToIdentifyThisProcess(me);
using var key = new CountingProtector();

(VaultPipeServer Server, string Pipe) StartServer(PipePeerPolicy? policy = null, TimeProvider? clock = null)
{
    var vault = Path.Combine(workspace, "vault-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(vault);
    var catalog = new ProjectCatalog(vault, key);
    var name = "SourceVault.test." + Guid.NewGuid().ToString("N");
    var server = new VaultPipeServer(name, me, policy ?? new PipePeerPolicy(authenticatedUsers, [self]), new VaultProtocol(catalog, catalog.OpenAndVerify(), clock));
    server.Start();
    return (server, name);
}
string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
bool ClosedBy(Action action)
{
    try { action(); return false; }
    catch (Exception e) when (e is EndOfStreamException or IOException or ObjectDisposedException or OperationCanceledException) { return true; }
}

Test("full flow: session, create project, create file, list, open, edit, conflict", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var c = new Client(pipe, me))
    {
        c.Begin();
        Assert(c.Ok("ListProjects").GetProperty("projects").GetArrayLength() == 0, "empty vault");
        var created = c.Ok("CreateProject", ("name", "Demo"));
        var project = created.GetProperty("projectId").GetString()!;
        var s0 = created.GetProperty("snapshotId").GetString()!;
        var file = c.Ok("CreateFile", ("projectId", project), ("name", "main.ts"), ("expectedSnapshotId", s0), ("contentBase64", B64("export const a = 1;\n")));
        var fileId = file.GetProperty("fileId").GetString()!;
        var s1 = file.GetProperty("snapshotId").GetString()!;
        var files = c.Ok("ListFiles", ("projectId", project));
        Assert(files.GetProperty("snapshotId").GetString() == s1 && files.GetProperty("files")[0].GetProperty("name").GetString() == "main.ts", "list files");
        var opened = c.Ok("OpenDocument", ("projectId", project), ("fileId", fileId), ("expectedSnapshotId", s1));
        Assert(Encoding.UTF8.GetString(opened.GetProperty("contentBase64").GetBytesFromBase64()) == "export const a = 1;\n", "open content");
        Assert(opened.GetProperty("revisionId").GetString() == file.GetProperty("revisionId").GetString(), "open revision");
        var edited = c.Ok("ApplyEdits", ("projectId", project), ("fileId", fileId), ("expectedSnapshotId", s1), ("contentBase64", B64("export const a = 2;\n")));
        var s2 = edited.GetProperty("snapshotId").GetString()!;
        var reopened = c.Ok("OpenDocument", ("projectId", project), ("fileId", fileId), ("expectedSnapshotId", s2));
        Assert(Encoding.UTF8.GetString(reopened.GetProperty("contentBase64").GetBytesFromBase64()) == "export const a = 2;\n", "edited content");
        Assert(c.Code("ApplyEdits", ("projectId", project), ("fileId", fileId), ("expectedSnapshotId", s1), ("contentBase64", B64("stale"))) == "RevisionConflict", "stale edit");
        Assert(c.Code("OpenDocument", ("projectId", project), ("fileId", fileId), ("expectedSnapshotId", s1)) == "RevisionConflict", "stale open");
        var listed = c.Ok("ListProjects").GetProperty("projects")[0];
        Assert(listed.GetProperty("name").GetString() == "Demo" && listed.GetProperty("fileCount").GetInt32() == 1, "project listing");
        Assert(c.Code("EndSession") == "Ok" && ClosedBy(() => c.Code("ListProjects")), "end session closes");
    }
});

Test("names: duplicates are AlreadyExists, and errors carry no names or content", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var c = new Client(pipe, me))
    {
        c.Begin();
        var created = c.Ok("CreateProject", ("name", "Secret-Project-Name"));
        var project = created.GetProperty("projectId").GetString()!;
        var s1 = c.Ok("CreateFile", ("projectId", project), ("name", "Secret-File.ts"), ("expectedSnapshotId", created.GetProperty("snapshotId").GetString()!), ("contentBase64", B64("secret-content"))).GetProperty("snapshotId").GetString()!;
        var raw = c.Raw("CreateFile", ("projectId", project), ("name", "SECRET-FILE.TS"), ("expectedSnapshotId", s1), ("contentBase64", B64("secret-content")));
        Assert(raw.Contains("\"AlreadyExists\"") && !raw.Contains("Secret", StringComparison.OrdinalIgnoreCase) && !raw.Contains(B64("secret-content")), "file name clash");
        raw = c.Raw("CreateProject", ("name", "secret-project-name"));
        Assert(raw.Contains("\"AlreadyExists\"") && !raw.Contains("Secret", StringComparison.OrdinalIgnoreCase), "project name clash");
    }
});

Test("authorization: no session, wrong capability and a second BeginSession", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    {
        using (var c = new Client(pipe, me))
            Assert(c.Code("ListProjects", ("capability", Convert.ToBase64String(new byte[32]))) == "Unauthorized" && ClosedBy(() => c.Code("ListProjects")), "no session");
        using (var c = new Client(pipe, me))
        {
            c.Begin();
            Assert(c.RawBody("BeginSession", "{}").Contains("\"InvalidRequest\""), "second begin");
            Assert(c.Code("ListProjects", ("capability", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))) == "Unauthorized", "wrong capability");
            Assert(ClosedBy(() => c.Code("ListProjects")), "closed after wrong capability");
        }
    }
});

Test("expiry: 5-minute idle and 30-minute absolute on a monotonic clock are terminal", () =>
{
    var clock = new ManualClock();
    var (server, pipe) = StartServer(clock: clock);
    using (server)
    {
        using (var c = new Client(pipe, me))
        {
            c.Begin();
            clock.Advance(TimeSpan.FromMinutes(4)); Assert(c.Code("ListProjects") == "Ok", "within idle");
            clock.Advance(TimeSpan.FromMinutes(5)); Assert(c.Code("ListProjects") == "SessionExpired", "idle expiry");
            Assert(ClosedBy(() => c.Code("ListProjects")), "expired connection closed");
        }
        using (var c = new Client(pipe, me))
        {
            c.Begin();
            for (int i = 0; i < 7; i++) { clock.Advance(TimeSpan.FromMinutes(4)); Assert(c.Code("ListProjects") == "Ok", "active session"); }
            clock.Advance(TimeSpan.FromMinutes(3)); Assert(c.Code("ListProjects") == "SessionExpired", "absolute expiry");
        }
    }
});

Test("malformed frames, v1 envelopes and removed operations close the connection without a response", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    {
        foreach (var frame in new[] { "not json", "{\"version\":1,\"requestId\":\"" + Guid.NewGuid() + "\",\"operation\":\"ListProjects\",\"body\":{}}",
            "{\"version\":2,\"requestId\":\"" + Guid.NewGuid() + "\",\"operation\":\"CloseDocument\",\"body\":{}}",
            "{\"version\":2,\"version\":2,\"requestId\":\"" + Guid.NewGuid() + "\",\"operation\":\"BeginSession\",\"body\":{}}" })
        {
            using var c = new Client(pipe, me);
            Assert(ClosedBy(() => c.SendFrame(Encoding.UTF8.GetBytes(frame))), "closed: " + frame[..Math.Min(20, frame.Length)]);
        }
    }
});

Test("strict bodies, unknown projects, limits and unavailable builds", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var c = new Client(pipe, me))
    {
        c.Begin();
        var created = c.Ok("CreateProject", ("name", "Limits"));
        var project = created.GetProperty("projectId").GetString()!;
        var s0 = created.GetProperty("snapshotId").GetString()!;
        Assert(c.Code("ListFiles", ("projectId", project), ("extra", "x")) == "InvalidRequest", "extra field");
        Assert(c.Code("ListFiles", ("projectId", project.ToUpperInvariant())) == "InvalidRequest", "non-canonical id");
        Assert(c.Code("ListFiles", ("projectId", Guid.NewGuid().ToString("D"))) == "NotFound", "unknown project");
        Assert(c.Code("CreateProject", ("name", " padded")) == "InvalidRequest", "bad project name");
        Assert(c.Code("CreateFile", ("projectId", project), ("name", "a/b.ts"), ("expectedSnapshotId", s0), ("contentBase64", B64("x"))) == "InvalidRequest", "bad file name");
        Assert(c.Code("CreateFile", ("projectId", project), ("name", "big.ts"), ("expectedSnapshotId", s0), ("contentBase64", Convert.ToBase64String(new byte[262145]))) == "LimitExceeded", "content limit");
        Assert(c.Code("CreateFile", ("projectId", project), ("name", "bin.ts"), ("expectedSnapshotId", s0), ("contentBase64", Convert.ToBase64String(new byte[] { 0xff }))) == "InvalidRequest", "invalid UTF-8");
        Assert(c.Code("CreateFile", ("projectId", project), ("name", "b64.ts"), ("expectedSnapshotId", s0), ("contentBase64", "@@@")) == "InvalidRequest", "invalid base64");
        Assert(c.Code("CreateFile", ("projectId", project), ("name", "max.ts"), ("expectedSnapshotId", s0), ("contentBase64", Convert.ToBase64String(new byte[262144]))) == "Ok", "exactly 256 KiB fits a frame");
        Assert(c.Code("RequestBuild", ("projectId", project)) == "Unavailable", "builds unavailable");
    }
});

Test("peer policy: non-operators and non-installed executables get no response", () =>
{
    var (outsider, pipe1) = StartServer(new PipePeerPolicy(nobodyGroup, [self]));
    using (outsider)
    using (var c = new Client(pipe1, me))
        Assert(ClosedBy(() => c.Begin()), "not in operators group");
    var (wrongImage, pipe2) = StartServer(new PipePeerPolicy(authenticatedUsers, [@"C:\Program Files\SourceVault\client\SourceVault.LabClient.exe"]));
    using (wrongImage)
    using (var c = new Client(pipe2, me))
        Assert(ClosedBy(() => c.Begin()), "executable not installed client");
});

Test("server identity: the client refuses a pipe owned by anyone but the expected account", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    {
        try { using var c = new Client(pipe, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)); throw new Exception("accepted wrong owner"); }
        catch (UnauthorizedAccessException) { }
    }
});

Test("first-instance protection: a second server cannot take an existing name", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    {
        try
        {
            using var squatter = VaultPipe.CreateServerInstance(pipe, me, authenticatedUsers, first: true);
            throw new Exception("second first instance created");
        }
        catch (Win32Exception) { }
        catch (UnauthorizedAccessException) { }
    }
});

Test("at most 4 connections; a fifth waits until one closes", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    {
        var clients = Enumerable.Range(0, 4).Select(_ => { var c = new Client(pipe, me); c.Begin(); return c; }).ToList();
        try
        {
            try { using var fifth = new Client(pipe, me, TimeSpan.FromMilliseconds(500)); throw new Exception("fifth connection accepted"); }
            catch (Win32Exception) { }
            clients[0].Dispose();
            using var later = new Client(pipe, me, TimeSpan.FromSeconds(5));
            later.Begin();
            Assert(clients[1].Code("ListProjects") == "Ok", "others unaffected");
        }
        finally { foreach (var c in clients) c.Dispose(); }
    }
});

Test("Windows session lock/logoff ends sessions from that session only", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var c = new Client(pipe, me))
    {
        c.Begin();
        server.EndWindowsSession(unchecked((uint)System.Diagnostics.Process.GetCurrentProcess().SessionId) + 1000);
        Assert(c.Code("ListProjects") == "Ok", "other session untouched");
        server.EndWindowsSession(unchecked((uint)System.Diagnostics.Process.GetCurrentProcess().SessionId));
        Assert(ClosedBy(() => c.Code("ListProjects")), "own session ended");
    }
});

Test("unwrap budget over the pipe: warm list 1 (index), warm open 1, warm edit 0", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var c = new Client(pipe, me))
    {
        c.Begin();
        var created = c.Ok("CreateProject", ("name", "Budget"));
        var project = created.GetProperty("projectId").GetString()!;
        var file = c.Ok("CreateFile", ("projectId", project), ("name", "f.ts"), ("expectedSnapshotId", created.GetProperty("snapshotId").GetString()!), ("contentBase64", B64("x")));
        var s1 = file.GetProperty("snapshotId").GetString()!; var fileId = file.GetProperty("fileId").GetString()!;
        c.Ok("ListProjects");
        key.Unwraps = 0; c.Ok("ListProjects"); Assert(key.Unwraps == 1, "warm ListProjects = " + key.Unwraps);
        key.Unwraps = 0; c.Ok("OpenDocument", ("projectId", project), ("fileId", fileId), ("expectedSnapshotId", s1)); Assert(key.Unwraps == 1, "warm OpenDocument = " + key.Unwraps);
        key.Unwraps = 0; c.Ok("ApplyEdits", ("projectId", project), ("fileId", fileId), ("expectedSnapshotId", s1), ("contentBase64", B64("y"))); Assert(key.Unwraps == 0, "warm ApplyEdits = " + key.Unwraps);
    }
});

Console.WriteLine($"Endpoint tests: {passed} passed; synthetic workspace retained at {workspace}");
return 0;

sealed class Client : IDisposable
{
    private readonly NamedPipeClientStream pipe;
    private string? capability;
    public Client(string name, SecurityIdentifier server, TimeSpan? busy = null) => pipe = VaultPipe.Connect(name, server, busy ?? TimeSpan.FromSeconds(5));

    public void Begin()
    {
        var body = Ok("BeginSession");
        capability = body.GetProperty("capability").GetString();
    }
    public JsonElement Ok(string op, params (string, object)[] fields)
    {
        using var doc = JsonDocument.Parse(Raw(op, fields));
        if (doc.RootElement.GetProperty("code").GetString() != "Ok") throw new Exception(op + " failed: " + doc.RootElement.GetProperty("code").GetString());
        return doc.RootElement.GetProperty("body").Clone();
    }
    public string Code(string op, params (string, object)[] fields)
    {
        using var doc = JsonDocument.Parse(Raw(op, fields));
        return doc.RootElement.GetProperty("code").GetString()!;
    }
    public string Raw(string op, params (string Name, object Value)[] fields)
    {
        var body = new Dictionary<string, object>();
        if (op != "BeginSession" && capability is not null && !fields.Any(f => f.Name == "capability")) body["capability"] = capability;
        foreach (var (n, v) in fields) body[n] = v;
        return RawBody(op, JsonSerializer.Serialize(body));
    }
    public string RawBody(string op, string bodyJson) =>
        SendFrame(Encoding.UTF8.GetBytes($"{{\"version\":2,\"requestId\":\"{Guid.NewGuid():D}\",\"operation\":\"{op}\",\"body\":{bodyJson}}}"));
    public string SendFrame(byte[] frame)
    {
        FrameCodec.WriteAsync(pipe, frame).GetAwaiter().GetResult();
        return Encoding.UTF8.GetString(FrameCodec.ReadAsync(pipe).GetAwaiter().GetResult());
    }
    public void Dispose() => pipe.Dispose();
}

sealed class ManualClock : TimeProvider
{
    private long ticks = 1;
    public override long GetTimestamp() => ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan by) => ticks += by.Ticks;
}

sealed class CountingProtector : IKeyProtector, IDisposable
{
    private readonly RSA rsa = RSA.Create(2048);
    public int Unwraps;
    public byte[] WrapKey(ReadOnlySpan<byte> k) { lock (rsa) return rsa.Encrypt(k.ToArray(), RSAEncryptionPadding.OaepSHA256); }
    public byte[] UnwrapKey(ReadOnlySpan<byte> k) { lock (rsa) { Interlocked.Increment(ref Unwraps); return rsa.Decrypt(k.ToArray(), RSAEncryptionPadding.OaepSHA256); } }
    public void Dispose() => rsa.Dispose();
}
