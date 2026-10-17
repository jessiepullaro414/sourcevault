using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SourceVault.Contracts;

int passed = 0;
var failures = new List<string>();
const string Id = "b4b85c2d-a531-450f-b639-80fa2fdf0f39";
string Valid(string body = "{}") => $$"""{"version":1,"requestId":"{{Id}}","operation":"BeginSession","body":{{body}}}""";

async Task Check(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failures.Add(name); Console.WriteLine($"FAIL {name}: {e.GetType().Name}"); }
}
void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
void Reject(string input, string expected = "InvalidRequest")
{
    try { ProtocolParser.ParseRequest(Encoding.UTF8.GetBytes(input)); }
    catch (ProtocolException e) when (e.Code == expected) { return; }
    throw new Exception("Expected protocol rejection");
}
async Task Throws<T>(Func<Task> operation) where T : Exception
{
    try { await operation(); }
    catch (T) { return; }
    throw new Exception("Expected failure");
}

await Check("valid body survives parser document disposal", () =>
{
    var value = ProtocolParser.ParseRequest(Encoding.UTF8.GetBytes(Valid("{\"projectId\":\"" + Id + "\"}")));
    Assert(value.RequestId.ToString() == Id && value.Body.GetProperty("projectId").GetString() == Id);
    return Task.CompletedTask;
});
await Check("duplicate envelope property", () => { Reject(Valid().Replace("\"version\":1", "\"version\":1,\"version\":1")); return Task.CompletedTask; });
await Check("escaped duplicate property", () => { Reject(Valid("{\"x\":1,\"\\u0078\":2}")); return Task.CompletedTask; });
await Check("nested duplicate in array", () => { Reject(Valid("{\"a\":[{\"x\":1,\"x\":2}]}")); return Task.CompletedTask; });
await Check("unknown field", () => { Reject(Valid().Replace("\"version\":1", "\"untrusted\":true,\"version\":1")); return Task.CompletedTask; });
await Check("missing field", () => { Reject(Valid().Replace("\"version\":1,", "")); return Task.CompletedTask; });
await Check("unsupported version", () => { Reject(Valid().Replace("\"version\":1", "\"version\":2"), "UnsupportedVersion"); return Task.CompletedTask; });
await Check("wrong version type", () => { Reject(Valid().Replace("\"version\":1", "\"version\":\"1\"")); return Task.CompletedTask; });
await Check("unknown operation", () => { Reject(Valid().Replace("BeginSession", "ExecuteShell"), "UnsupportedOperation"); return Task.CompletedTask; });
await Check("noncanonical ID", () => { Reject(Valid().Replace(Id, Id.ToUpperInvariant())); return Task.CompletedTask; });
await Check("empty ID", () => { Reject(Valid().Replace(Id, Guid.Empty.ToString())); return Task.CompletedTask; });
await Check("body must be object", () => { Reject(Valid("[]")); return Task.CompletedTask; });
await Check("trailing comma", () => { Reject(Valid("{\"x\":1,}")); return Task.CompletedTask; });
await Check("JSON comments", () => { Reject(Valid("{/*comment*/}")); return Task.CompletedTask; });
await Check("trailing document", () => { Reject(Valid() + "{}"); return Task.CompletedTask; });
await Check("depth bound", () => { Reject(Valid("{\"x\":" + new string('[', 17) + "0" + new string(']', 17) + "}")); return Task.CompletedTask; });
await Check("invalid UTF8", () =>
{
    var bytes = Encoding.UTF8.GetBytes(Valid("{\"x\":\"a\"}"));
    bytes[Array.IndexOf(bytes, (byte)'a', bytes.Length - 6)] = 0xFF;
    try { ProtocolParser.ParseRequest(bytes); }
    catch (ProtocolException e) when (e.Code == "InvalidRequest") { return Task.CompletedTask; }
    throw new Exception("Expected failure");
});
await Check("frame roundtrip", async () =>
{
    using var stream = new MemoryStream();
    var payload = Encoding.UTF8.GetBytes(Valid());
    await FrameCodec.WriteAsync(stream, payload);
    stream.Position = 0;
    Assert((await FrameCodec.ReadAsync(stream)).AsSpan().SequenceEqual(payload));
});
await Check("partial stream reads", async () =>
{
    using var memory = new MemoryStream();
    await FrameCodec.WriteAsync(memory, Encoding.UTF8.GetBytes(Valid()));
    using var stream = new OneByteStream(memory.ToArray());
    Assert(ProtocolParser.ParseRequest(await FrameCodec.ReadAsync(stream)).Operation == "BeginSession");
});
foreach (uint length in new uint[] { 0, ProtocolLimits.MaxFrameBytes + 1, uint.MaxValue })
{
    await Check($"reject frame length {length}", async () =>
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Throws<ProtocolException>(async () => { await FrameCodec.ReadAsync(stream); });
    });
}
await Check("truncated header", async () =>
{
    using var stream = new MemoryStream(new byte[] { 2, 0 });
    await Throws<EndOfStreamException>(async () => { await FrameCodec.ReadAsync(stream); });
});
await Check("truncated payload", async () =>
{
    using var stream = new MemoryStream(new byte[] { 3, 0, 0, 0, 1 });
    await Throws<EndOfStreamException>(async () => { await FrameCodec.ReadAsync(stream); });
});
await Check("canceled read", async () =>
{
    using var stream = new MemoryStream(new byte[] { 1, 0, 0, 0, 0 });
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await Throws<OperationCanceledException>(async () => { await FrameCodec.ReadAsync(stream, cancellation.Token); });
});
await Check("zero-byte write rejected", async () =>
{
    using var stream = new MemoryStream();
    await Throws<ProtocolException>(() => FrameCodec.WriteAsync(stream, Array.Empty<byte>()));
    Assert(stream.Length == 0);
});
Console.WriteLine($"Protocol tests: {passed} passed; {failures.Count} failed.");
return failures.Count == 0 ? 0 : 1;

sealed class OneByteStream(byte[] data) : MemoryStream(data)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
}
