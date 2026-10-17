using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SourceVault.Storage;

if (args.Length < 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Explicit absolute test workspace required.");
if (args.Length == 2 && args[1] == "hold-lock")
{
    using var locked = new FileStream(Path.Combine(args[0], "LOCK"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    Console.WriteLine("READY"); Console.Out.Flush();
    Console.ReadLine(); return;
}
Directory.CreateDirectory(args[0]);
string testRoot = Path.Combine(args[0], "revisions-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
int passed = 0;
void Assert(bool value) { if (!value) throw new Exception("Assertion failed."); }
void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected rejection."); }
void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
using var key = new EphemeralProtector();
(RevisionStore Store, string Path, Guid Project) Fixture()
{
    var path = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
    var project = Guid.NewGuid(); return (new(path, project, key), path, project);
}
byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

Test("initialize, commit, replace, reopen and stable file identity", () =>
{
    var f = Fixture(); var first = f.Store.Initialize(); Assert(first.Files.Count == 0);
    var next = f.Store.Commit(first.SnapshotId, "synthetic.cs", Text("synthetic-value-alpha"));
    var changed = f.Store.Commit(next.SnapshotId, "SYNTHETIC.cs", Text("synthetic-value-beta"));
    Assert(changed.Files.Count == 1 && next.Files[0].FileId == changed.Files[0].FileId);
    Assert(new RevisionStore(f.Path, f.Project, key).List().SnapshotId == changed.SnapshotId);
    Assert(Encoding.UTF8.GetString(f.Store.Open(changed.SnapshotId, changed.Files[0].FileId)) == "synthetic-value-beta");
    Reject<IOException>(() => f.Store.Initialize());
    Reject<RevisionConflictException>(() => f.Store.Open(first.SnapshotId, changed.Files[0].FileId));
    Reject<KeyNotFoundException>(() => f.Store.Open(changed.SnapshotId, Guid.NewGuid()));
    foreach (var path in Directory.GetFiles(f.Path))
    {
        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        Assert(!raw.Contains("synthetic-value") && !raw.Contains("synthetic.cs", StringComparison.OrdinalIgnoreCase));
    }
});

foreach (var name in new[] { "", ".", "..", "../x", "a/b", "a\\b", "a:b", "CON", "NUL.txt", "COM1.cs", "LPT¹.txt", "CONIN$", "x.", "x ", "a\n", "a?b", new string('a',129), "\ud800" })
Test("reject logical name " + passed, () => { var f = Fixture(); var s = f.Store.Initialize(); Reject<ArgumentException>(() => f.Store.Commit(s.SnapshotId, name, Text("synthetic"))); });

Test("reject document limits and non UTF8", () =>
{
    var f = Fixture(); var s = f.Store.Initialize();
    Reject<ArgumentException>(() => f.Store.Commit(s.SnapshotId, "x", new byte[262145]));
    Reject<InvalidDataException>(() => f.Store.Commit(s.SnapshotId, "x", new byte[] { 0xff }));
    var next = f.Store.Commit(s.SnapshotId, "empty", []); Assert(f.Store.Open(next.SnapshotId, next.Files[0].FileId).Length == 0);
});

foreach (var stage in Enum.GetValues<CommitStage>())
Test("fault interruption and retry " + stage, () =>
{
    var f = Fixture(); var before = f.Store.Initialize();
    f.Store.FaultInjection = point => { if (point == stage) throw new SimulatedCrashException(); };
    Reject<SimulatedCrashException>(() => f.Store.Commit(before.SnapshotId, "fault.cs", Text("synthetic-fault")));
    var reopened = new RevisionStore(f.Path, f.Project, key); var after = reopened.List();
    Assert(stage == CommitStage.HeadPublished ? after.Files.Count == 1 : after.SnapshotId == before.SnapshotId);
    if (stage == CommitStage.HeadPublished) Reject<RevisionConflictException>(() => reopened.Commit(before.SnapshotId, "fault.cs", Text("synthetic-retry")));
    else Assert(reopened.Commit(before.SnapshotId, "fault.cs", Text("synthetic-retry")).Files.Count == 1);
});

Test("competing store instances allow exactly one expected-snapshot writer", () =>
{
    var f = Fixture(); var initial = f.Store.Initialize(); int success = 0, conflicts = 0;
    Parallel.For(0, 4, i =>
    {
        try { new RevisionStore(f.Path, f.Project, key).Commit(initial.SnapshotId, "test" + i, Text("synthetic-race")); Interlocked.Increment(ref success); }
        catch (RevisionConflictException) { Interlocked.Increment(ref conflicts); }
    });
    Assert(success == 1 && conflicts == 3);
});

Test("bounded crossprocess lock", () =>
{
    var f = Fixture(); f.Store.Initialize();
    var info = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
    info.ArgumentList.Add(typeof(EphemeralProtector).Assembly.Location); info.ArgumentList.Add(f.Path); info.ArgumentList.Add("hold-lock");
    using var process = Process.Start(info)!;
    try
    {
        Assert(process.StandardOutput.ReadLine() == "READY");
        var timer = Stopwatch.StartNew();
        Reject<IOException>(() => new RevisionStore(f.Path, f.Project, key, TimeSpan.FromMilliseconds(100)).List());
        Assert(timer.Elapsed < TimeSpan.FromSeconds(3));
    }
    finally { process.StandardInput.WriteLine(); process.WaitForExit(5000); }
});

foreach (var kind in new[] { "head", "manifest", "document", "missing", "project", "length" })
Test("fail closed damaged state " + kind, () =>
{
    var f = Fixture(); var a = f.Store.Initialize(); var b = f.Store.Commit(a.SnapshotId, "tamper.cs", Text("synthetic-authentication"));
    string blob = Path.Combine(f.Path, (kind == "manifest" ? b.SnapshotId : b.Files[0].RevisionId).ToString("D") + ".svr");
    if (kind == "head") File.WriteAllText(Path.Combine(f.Path,"HEAD"), "invalid");
    else if (kind == "missing") File.Delete(blob);
    else if (kind == "project") { Reject<CryptographicException>(() => new RevisionStore(f.Path, Guid.NewGuid(), key).List()); return; }
    else if (kind == "length") File.WriteAllBytes(blob, new byte[262491]);
    else { var bytes = File.ReadAllBytes(blob); bytes[^1] ^= 1; File.WriteAllBytes(blob, bytes); }
    Reject<Exception>(() => f.Store.List());
});

const string goodEntry = "{\"fileId\":\"0f3a5c1e-7d2b-4c9a-8e6f-1a2b3c4d5e6f\",\"name\":\"a.cs\",\"revisionId\":\"9b8c7d6e-5f4a-4b3c-9d2e-1f0a9b8c7d6e\",\"byteLength\":0,\"recordSha256\":\"HASH\"}";
string Entry(string hash) => goodEntry.Replace("HASH", hash);
foreach (var malformed in new[]
{
    "{\"version\":2,\"version\":2,\"snapshotId\":\"ID\",\"files\":[]}",
    "{\"version\":1,\"snapshotId\":\"ID\",\"files\":[]}", // legacy schema without record hashes
    "{\"version\":3,\"snapshotId\":\"ID\",\"files\":[]}",
    "{\"version\":2,\"snapshotId\":\"ID\",\"files\":[],\"unknown\":1}",
    "{\"version\":2,\"snapshotId\":\"ID\",\"files\":[{}]}",
    "{\"version\":2,\"snapshotId\":\"ID\",\"files\":[" + Entry(new string('a', 64)).Replace(",\"recordSha256\":\"" + new string('a', 64) + "\"", "") + "]}",
    "{\"version\":2,\"snapshotId\":\"ID\",\"files\":[" + Entry(new string('A', 64)) + "]}",
    "{\"version\":2,\"snapshotId\":\"ID\",\"files\":[" + Entry(new string('a', 63)) + "]}",
    "{\"version\":2,\"snapshotId\":\"ID\",\"files\":[" + Entry("zz" + new string('a', 62)) + "]}",
})
Test("authenticated malformed manifest " + passed, () =>
{
    var f = Fixture(); var s = f.Store.Initialize();
    var encrypted = RecordCodec.Encrypt(new(f.Project, Guid.Empty, s.SnapshotId, RecordKind.Manifest), Text(malformed.Replace("ID", s.SnapshotId.ToString("D"))), key);
    File.WriteAllBytes(Path.Combine(f.Path, s.SnapshotId.ToString("D") + ".svr"), encrypted);
    Reject<InvalidDataException>(() => f.Store.List());
});

Test("manifest duplicate names and identifiers rejected", () =>
{
    var f = Fixture(); var s = f.Store.Initialize();
    var entry = new { fileId = Guid.NewGuid(), name = "synthetic.cs", revisionId = Guid.NewGuid(), byteLength = 0, recordSha256 = new string('a', 64) };
    var manifest = JsonSerializer.SerializeToUtf8Bytes(new { version = 2, snapshotId = s.SnapshotId, files = new[] { entry, entry } });
    File.WriteAllBytes(Path.Combine(f.Path, s.SnapshotId + ".svr"), RecordCodec.Encrypt(new(f.Project, Guid.Empty, s.SnapshotId, RecordKind.Manifest), manifest, key));
    Reject<InvalidDataException>(() => f.Store.List());
});

Test("manifest file count bound", () =>
{
    var f = Fixture(); var s = f.Store.Initialize();
    var manifest = JsonSerializer.SerializeToUtf8Bytes(new { version = 2, snapshotId = s.SnapshotId, files = Enumerable.Range(0, 1025).Select(i => new { fileId = Guid.NewGuid(), name = "synthetic" + i, revisionId = Guid.NewGuid(), byteLength = 0, recordSha256 = new string('a', 64) }) });
    File.WriteAllBytes(Path.Combine(f.Path, s.SnapshotId + ".svr"), RecordCodec.Encrypt(new(f.Project, Guid.Empty, s.SnapshotId, RecordKind.Manifest), manifest, key));
    Reject<InvalidDataException>(() => f.Store.List());
});

// Unwrap budget: each unwrap is a ~45 ms TPM operation on real hardware, so the counts are the performance contract.
using var counting = new CountingProtector();
(RevisionStore Store, string Path, Guid Project, StoreSnapshot Snap) Populated(int files)
{
    var path = Path.Combine(testRoot, Guid.NewGuid().ToString("N")); var project = Guid.NewGuid();
    var store = new RevisionStore(path, project, counting); var snap = store.Initialize();
    for (int i = 0; i < files; i++) snap = store.Commit(snap.SnapshotId, $"file{i}.cs", Text($"synthetic-content-{i}"));
    return (store, path, project, snap);
}
int Unwraps(Action action) { counting.Unwraps = 0; action(); return counting.Unwraps; }

Test("unwrap budget: cold list 1, warm list 0, open 1, commit 0, bulk read N", () =>
{
    var p = Populated(8);
    var cold = new RevisionStore(p.Path, p.Project, counting);
    Assert(Unwraps(() => cold.List()) == 1);
    Assert(Unwraps(() => cold.List()) == 0);
    Assert(Unwraps(() => CryptographicOperations.ZeroMemory(cold.Open(p.Snap.SnapshotId, p.Snap.Files[3].FileId))) == 1);
    StoreSnapshot next = p.Snap;
    Assert(Unwraps(() => next = cold.Commit(p.Snap.SnapshotId, "file3.cs", Text("synthetic-edit"))) == 0);
    var seen = new List<string>();
    Assert(Unwraps(() => cold.ReadSnapshot(next.SnapshotId, (f, content) => seen.Add(f.LogicalName + "=" + Encoding.UTF8.GetString(content)))) == 8);
    Assert(seen.Count == 8 && seen.Contains("file3.cs=synthetic-edit") && seen.Contains("file7.cs=synthetic-content-7"));
    Reject<RevisionConflictException>(() => cold.ReadSnapshot(p.Snap.SnapshotId, (_, _) => { }));
});

Test("store root given with a trailing separator still reads its records", () =>
{
    var p = Populated(2);
    var trailing = new RevisionStore(p.Path + Path.DirectorySeparatorChar, p.Project, counting);
    Assert(trailing.List().SnapshotId == p.Snap.SnapshotId);
    Assert(Encoding.UTF8.GetString(trailing.Open(p.Snap.SnapshotId, p.Snap.Files[1].FileId)) == "synthetic-content-1");
});

Test("cache invalidated by another instance's commit", () =>
{
    var p = Populated(3);
    var a = new RevisionStore(p.Path, p.Project, counting); a.List();
    var b = new RevisionStore(p.Path, p.Project, counting).Commit(p.Snap.SnapshotId, "new.cs", Text("synthetic-other-writer"));
    StoreSnapshot seenByA = p.Snap;
    Assert(Unwraps(() => seenByA = a.List()) == 1 && seenByA.SnapshotId == b.SnapshotId && seenByA.Files.Count == 4);
    Reject<RevisionConflictException>(() => a.Commit(p.Snap.SnapshotId, "late.cs", Text("synthetic-stale")));
});

Test("cache not reused when the manifest record is replaced under the same id", () =>
{
    var p = Populated(2);
    p.Store.List(); // warm
    var replacement = JsonSerializer.SerializeToUtf8Bytes(new { version = 2, snapshotId = p.Snap.SnapshotId, files = Array.Empty<object>() });
    File.WriteAllBytes(Path.Combine(p.Path, p.Snap.SnapshotId + ".svr"), RecordCodec.Encrypt(new(p.Project, Guid.Empty, p.Snap.SnapshotId, RecordKind.Manifest), replacement, counting));
    StoreSnapshot listed = p.Snap;
    Assert(Unwraps(() => listed = p.Store.List()) == 1 && listed.Files.Count == 0);
});

Test("warm instance still detects a swapped document record without decrypting", () =>
{
    var p = Populated(2);
    p.Store.List(); // warm
    var first = Path.Combine(p.Path, p.Snap.Files[0].RevisionId + ".svr");
    var second = Path.Combine(p.Path, p.Snap.Files[1].RevisionId + ".svr");
    File.Copy(second, first, overwrite: true); // same length: both synthetic contents are equally long
    Assert(Unwraps(() => Reject<InvalidDataException>(() => p.Store.List())) == 0);
});

foreach (var target in new[] { "LOCK", "HEAD", "blob", "root" })
{
    var f = Fixture(); var s = f.Store.Initialize();
    var b = f.Store.Commit(s.SnapshotId, "link.cs", Text("synthetic-link"));
    var link = target switch { "root" => Path.Combine(testRoot, Guid.NewGuid().ToString("N")), "blob" => Path.Combine(f.Path, b.Files[0].RevisionId + ".svr"), _ => Path.Combine(f.Path, target) };
    var missingTarget = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
    try
    {
        if (target == "root") Directory.CreateSymbolicLink(link, missingTarget);
        else { File.Delete(link); File.CreateSymbolicLink(link, missingTarget); }
    }
    catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
    { Console.WriteLine("SKIP dangling reparse " + target + ": link creation unavailable (" + e.GetType().Name + ")"); continue; }
    Test("reject dangling reparse " + target, () =>
    {
        if (target == "root") Reject<IOException>(() => new RevisionStore(link, f.Project, key).Initialize());
        else Reject<IOException>(() => f.Store.List());
    });
}

Console.WriteLine($"Revision store: {passed} checks passed; synthetic workspace retained at {testRoot}");

sealed class SimulatedCrashException : Exception;
sealed class CountingProtector : IKeyProtector, IDisposable
{
    private readonly RSA rsa = RSA.Create(2048);
    public int Unwraps;
    public byte[] WrapKey(ReadOnlySpan<byte> key) { lock (rsa) return rsa.Encrypt(key.ToArray(), RSAEncryptionPadding.OaepSHA256); }
    public byte[] UnwrapKey(ReadOnlySpan<byte> key) { lock (rsa) { Unwraps++; return rsa.Decrypt(key.ToArray(), RSAEncryptionPadding.OaepSHA256); } }
    public void Dispose() => rsa.Dispose();
}
sealed class EphemeralProtector : IKeyProtector, IDisposable
{
    private readonly RSA rsa = RSA.Create(2048);
    public byte[] WrapKey(ReadOnlySpan<byte> key) { lock (rsa) return rsa.Encrypt(key.ToArray(), RSAEncryptionPadding.OaepSHA256); }
    public byte[] UnwrapKey(ReadOnlySpan<byte> key) { lock (rsa) return rsa.Decrypt(key.ToArray(), RSAEncryptionPadding.OaepSHA256); }
    public void Dispose() => rsa.Dispose();
}
