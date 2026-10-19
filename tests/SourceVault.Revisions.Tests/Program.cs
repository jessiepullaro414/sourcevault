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
if (args.Length == 4 && args[1] == "commit-loop")
{
    // Test child: commits until killed. The ephemeral test key arrives on stdin (never on disk or the command line).
    using var rsa = RSA.Create();
    rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(Console.ReadLine()!), out _);
    using var imported = new ImportedProtector(rsa);
    var store = new RevisionStore(args[2], Guid.Parse(args[3]), imported);
    for (int i = 0; ; i++)
    {
        var current = store.List();
        var next = store.Commit(current.SnapshotId, $"src/f{i % 5}.ts", Encoding.UTF8.GetBytes($"synthetic-kill-{i}\n"));
        Console.WriteLine("COMMITTED " + next.SnapshotId.ToString("D")); Console.Out.Flush();
    }
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

foreach (var name in new[] { "", ".", "..", "../x", "a\\b", "a:b", "CON", "NUL.txt", "COM1.cs", "LPT¹.txt", "CONIN$", "x.", "x ", "a\n", "a?b", new string('a',129), "\ud800",
    "/a", "a/", "a//b", "a/./b", "a/../b", "src/CON/x.ts", "src/x./y", string.Join('/', Enumerable.Repeat("d", 33)), string.Join('/', Enumerable.Repeat(new string('d', 20), 13)) })
Test("reject logical name " + passed, () => { var f = Fixture(); var s = f.Store.Initialize(); Reject<ArgumentException>(() => f.Store.Commit(s.SnapshotId, name, Text("synthetic"))); });

Test("folder paths: nested names commit and reopen; a file and a folder cannot share a path", () =>
{
    var f = Fixture(); var s = f.Store.Initialize();
    s = f.Store.Commit(s.SnapshotId, "src/app/main.ts", Text("synthetic-nested"));
    s = f.Store.Commit(s.SnapshotId, string.Join('/', Enumerable.Repeat("d", 31)) + "/deep.ts", Text("synthetic-deep"));
    var reopened = new RevisionStore(f.Path, f.Project, key).List();
    Assert(reopened.Files.Any(x => x.LogicalName == "src/app/main.ts"));
    Reject<NameExistsException>(() => f.Store.Commit(reopened.SnapshotId, "src/app", Text("file where a folder is")));
    Reject<NameExistsException>(() => f.Store.Commit(reopened.SnapshotId, "SRC/app/main.ts/x", Text("folder where a file is")));
    Assert(f.Store.List().SnapshotId == reopened.SnapshotId);
});


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

Test("killed mid-commit, 50 times: the store always reopens at a complete snapshot and accepts the next commit", () =>
{
    // Process.Kill (TerminateProcess) at random points, 50 times: no finally blocks, no flushes, no cleanup run. It caught
    // File.Replace leaving no HEAD when killed between its two renames (fixed 2026-10-19: Publish uses single renames).
    // Process-crash evidence only; power loss is a lab test (docs/lab-checklist.md).
    using var rsa = RSA.Create(2048);
    using var shared = new ImportedProtector(rsa);
    var path = Path.Combine(testRoot, Guid.NewGuid().ToString("N")); var project = Guid.NewGuid();
    new RevisionStore(path, project, shared).Initialize();
    var random = new Random(20261018);
    for (int round = 0; round < 50; round++)
    {
        var reported = new List<Guid>();
        var info = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { typeof(EphemeralProtector).Assembly.Location, testRoot, "commit-loop", path, project.ToString("D") }) info.ArgumentList.Add(a);
        using (var child = Process.Start(info)!)
        {
            child.StandardInput.WriteLine(Convert.ToBase64String(rsa.ExportPkcs8PrivateKey())); child.StandardInput.Flush();
            int wanted = random.Next(1, 4);
            while (reported.Count < wanted) reported.Add(Guid.Parse(child.StandardOutput.ReadLine()!["COMMITTED ".Length..]));
            Thread.Sleep(random.Next(0, 25)); // land somewhere inside a later commit
            child.Kill(); child.WaitForExit();
        }
        var reopened = new RevisionStore(path, project, shared);
        var snapshot = reopened.List(); // fails if HEAD, the manifest or any referenced record is incomplete
        Assert(snapshot.SnapshotId == reported[^1] || !reported.SkipLast(1).Contains(snapshot.SnapshotId)); // the last reported one, or one newer
        int read = 0;
        reopened.ReadSnapshot(snapshot.SnapshotId, (file, content) =>
        {
            var text = Encoding.UTF8.GetString(content);
            Assert(file.LogicalName == "after-kill.ts" ? text == "synthetic-after" : text.StartsWith("synthetic-kill-", StringComparison.Ordinal));
            read++;
        });
        Assert(read == snapshot.Files.Count && read is >= 1 and <= 6); // src/f0..f4 plus after-kill.ts
        reopened.Commit(snapshot.SnapshotId, "after-kill.ts", Text("synthetic-after")); // the lock was released by process death
    }
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

Test("unwrap budget: cold list 1, warm list 0, open 1, cold edit 1 (history), warm edit 0, bulk read N", () =>
{
    var p = Populated(8);
    var cold = new RevisionStore(p.Path, p.Project, counting);
    Assert(Unwraps(() => cold.List()) == 1);
    Assert(Unwraps(() => cold.List()) == 0);
    Assert(Unwraps(() => CryptographicOperations.ZeroMemory(cold.Open(p.Snap.SnapshotId, p.Snap.Files[3].FileId))) == 1);
    StoreSnapshot next = p.Snap;
    Assert(Unwraps(() => next = cold.Commit(p.Snap.SnapshotId, "file3.cs", Text("synthetic-edit"))) == 1); // reads file3's history once
    Assert(Unwraps(() => next = cold.Commit(next.SnapshotId, "file3.cs", Text("synthetic-edit"))) == 0);
    var seen = new List<string>();
    Assert(Unwraps(() => cold.ReadSnapshot(next.SnapshotId, (f, content) => seen.Add(f.LogicalName + "=" + Encoding.UTF8.GetString(content)))) == 8);
    Assert(seen.Count == 8 && seen.Contains("file3.cs=synthetic-edit") && seen.Contains("file7.cs=synthetic-content-7"));
    Reject<RevisionConflictException>(() => cold.ReadSnapshot(p.Snap.SnapshotId, (_, _) => { }));
});

Test("CommitMany: many files in one snapshot, all or nothing, no TPM unwraps when warm", () =>
{
    var path = Path.Combine(testRoot, Guid.NewGuid().ToString("N")); var project = Guid.NewGuid();
    var store = new RevisionStore(path, project, counting); var s = store.Initialize();
    var batch = Enumerable.Range(0, 50).Select(i => ($"src/m{i / 10}/f{i}.ts", Text($"synthetic-{i}"))).ToList();
    StoreSnapshot next = s;
    Assert(Unwraps(() => next = store.CommitMany(s.SnapshotId, batch)) == 0);
    Assert(next.Files.Count == 50 && Directory.GetFiles(path, "*.svr").Length == 1 + 1 + 50 + 50); // manifests, documents, history records
    Reject<ArgumentException>(() => store.CommitMany(next.SnapshotId, [("dup.ts", Text("a")), ("DUP.ts", Text("b"))]));
    Reject<RevisionConflictException>(() => store.CommitMany(s.SnapshotId, [("late.ts", Text("x"))]));
    var replaced = store.CommitMany(next.SnapshotId, [("src/m0/f0.ts", Text("synthetic-replaced")), ("new.ts", Text("synthetic-new"))]);
    Assert(replaced.Files.Count == 51 && replaced.Files.Single(x => x.LogicalName == "src/m0/f0.ts").FileId == next.Files.Single(x => x.LogicalName == "src/m0/f0.ts").FileId);
    Assert(Encoding.UTF8.GetString(store.Open(replaced.SnapshotId, replaced.Files.Single(x => x.LogicalName == "src/m0/f0.ts").FileId)) == "synthetic-replaced");
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

// Project catalog: vault root = index\ + projects\<id>\, names only inside the encrypted index.
string NewVault() { var v = Path.Combine(testRoot, "vault-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(v); return v; }
void RejectCatalog(CatalogFailure expected, Action action)
{
    try { action(); } catch (VaultCatalogException e) when (e.Failure == expected) { return; }
    throw new Exception("Expected catalog failure " + expected);
}
(string Vault, ProjectInfo A, ProjectInfo B) TwoProjects()
{
    var v = NewVault(); var c = new ProjectCatalog(v, counting);
    var a = c.CreateProject("Alpha"); var b = c.CreateProject("Beta");
    var store = c.OpenProject(a.ProjectId); store.Commit(a.Snapshot.SnapshotId, "main.cs", Text("synthetic-alpha-main"));
    return (v, a, b);
}

Test("catalog: empty vault is a fresh vault and verification creates nothing", () =>
{
    var v = NewVault();
    Assert(new ProjectCatalog(v, counting).OpenAndVerify().Count == 0 && Directory.GetFileSystemEntries(v).Length == 0);
});

Test("catalog: create, reopen, names only inside encrypted index, unwrap budget 2 + projects", () =>
{
    var t = TwoProjects();
    var fresh = new ProjectCatalog(t.Vault, counting);
    IReadOnlyList<ProjectInfo> projects = [];
    Assert(Unwraps(() => projects = fresh.OpenAndVerify()) == 2 + 2);
    Assert(Unwraps(() => fresh.OpenAndVerify()) == 1); // warm stores; only the index document is decrypted again
    Assert(projects.Select(p => p.Name).SequenceEqual(new[] { "Alpha", "Beta" }));
    Assert(projects[0].Snapshot.Files.Count == 1 && projects[1].Snapshot.Files.Count == 0);
    Assert(Encoding.UTF8.GetString(fresh.OpenProject(t.A.ProjectId).Open(projects[0].Snapshot.SnapshotId, projects[0].Snapshot.Files[0].FileId)) == "synthetic-alpha-main");
    Assert(Directory.GetFileSystemEntries(t.Vault).Select(Path.GetFileName).OrderBy(n => n).SequenceEqual(new[] { "index", "projects" }));
    foreach (var file in Directory.GetFiles(t.Vault, "*", SearchOption.AllDirectories))
    {
        var raw = File.ReadAllBytes(file);
        Assert(raw.AsSpan().IndexOf(Text("Alpha")) < 0 && raw.AsSpan().IndexOf(Text("Beta")) < 0 && raw.AsSpan().IndexOf(Text("synthetic-alpha")) < 0);
    }
});

Test("catalog: duplicate and invalid project names rejected", () =>
{
    var t = TwoProjects(); var c = new ProjectCatalog(t.Vault, counting);
    Reject<NameExistsException>(() => c.CreateProject("alpha"));
    foreach (var bad in new[] { "", " lead", "trail ", "tab\tname", new string('n', 65), "\ud800" })
        Reject<ArgumentException>(() => c.CreateProject(bad));
    Assert(c.OpenAndVerify().Count == 2);
});

Test("catalog: unexpected entries fail closed as layout", () =>
{
    var t = TwoProjects();
    File.WriteAllText(Path.Combine(t.Vault, "stray.txt"), "x");
    RejectCatalog(CatalogFailure.Layout, () => new ProjectCatalog(t.Vault, counting).OpenAndVerify());
    var u = TwoProjects();
    Directory.CreateDirectory(Path.Combine(u.Vault, "projects", Guid.NewGuid().ToString("D")));
    RejectCatalog(CatalogFailure.Layout, () => new ProjectCatalog(u.Vault, counting).OpenAndVerify());
    var w = TwoProjects();
    Directory.Delete(Path.Combine(w.Vault, "projects", w.B.ProjectId.ToString("D")), true);
    RejectCatalog(CatalogFailure.Layout, () => new ProjectCatalog(w.Vault, counting).OpenAndVerify());
    var x = NewVault(); Directory.CreateDirectory(Path.Combine(x, "projects"));
    RejectCatalog(CatalogFailure.Layout, () => new ProjectCatalog(x, counting).OpenAndVerify());
});

Test("catalog: tampered index and tampered project fail closed with their own category", () =>
{
    var t = TwoProjects();
    void FlipLiveManifest(string storeDir)
    {
        var live = Path.Combine(storeDir, File.ReadAllText(Path.Combine(storeDir, "HEAD")) + ".svr");
        var raw = File.ReadAllBytes(live); raw[^1] ^= 1; File.WriteAllBytes(live, raw);
    }
    FlipLiveManifest(Path.Combine(t.Vault, "index"));
    RejectCatalog(CatalogFailure.Index, () => new ProjectCatalog(t.Vault, counting).OpenAndVerify());
    var u = TwoProjects();
    FlipLiveManifest(Path.Combine(u.Vault, "projects", u.A.ProjectId.ToString("D")));
    RejectCatalog(CatalogFailure.Project, () => new ProjectCatalog(u.Vault, counting).OpenAndVerify());
});

Test("catalog: delete needs the exact name, removes the files, and frees the name", () =>
{
    var t = TwoProjects(); var c = new ProjectCatalog(t.Vault, counting);
    c.OpenAndVerify();
    Reject<ArgumentException>(() => c.DeleteProject(t.A.ProjectId, "alpha"));
    Reject<KeyNotFoundException>(() => c.DeleteProject(Guid.NewGuid(), "Alpha"));
    Assert(c.DeleteProject(t.A.ProjectId, "Alpha"));
    Assert(!Directory.Exists(Path.Combine(t.Vault, "projects", t.A.ProjectId.ToString("D"))));
    Reject<KeyNotFoundException>(() => c.DeleteProject(t.A.ProjectId, "Alpha"));
    var after = new ProjectCatalog(t.Vault, counting).OpenAndVerify();
    Assert(after.Select(p => p.Name).SequenceEqual(new[] { "Beta" }));
    Assert(c.CreateProject("Alpha").ProjectId != t.A.ProjectId && new ProjectCatalog(t.Vault, counting).OpenAndVerify().Count == 2);
});

Test("catalog: an interrupted delete is tombstoned, verifies, and is purged later", () =>
{
    var t = TwoProjects(); var c = new ProjectCatalog(t.Vault, counting);
    c.OpenAndVerify();
    var dir = Path.Combine(t.Vault, "projects", t.A.ProjectId.ToString("D"));
    using (new FileStream(Path.Combine(dir, "LOCK"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    {
        Assert(!c.DeleteProject(t.A.ProjectId, "Alpha")); // a file in use: the directory survives
        Assert(Directory.Exists(dir));
        var during = new ProjectCatalog(t.Vault, counting);
        Assert(during.OpenAndVerify().Select(p => p.Name).SequenceEqual(new[] { "Beta" })); // the leftover is allowed, not listed
        Assert(during.PurgeDeleted() == 1);
    }
    // Restart: the leftover is still tombstoned; a purge removes it and clears the tombstone.
    var restarted = new ProjectCatalog(t.Vault, counting);
    Assert(restarted.OpenAndVerify().Count == 1 && restarted.PurgeDeleted() == 0 && !Directory.Exists(dir));
    Assert(new ProjectCatalog(t.Vault, counting).OpenAndVerify().Count == 1);
    // The tombstone is gone, so recreating that directory is a stray entry again.
    Directory.CreateDirectory(dir);
    RejectCatalog(CatalogFailure.Layout, () => new ProjectCatalog(t.Vault, counting).OpenAndVerify());
});

Test("catalog: a tombstone allows only its own directory", () =>
{
    var t = TwoProjects(); var c = new ProjectCatalog(t.Vault, counting);
    c.OpenAndVerify();
    var dir = Path.Combine(t.Vault, "projects", t.A.ProjectId.ToString("D"));
    using (new FileStream(Path.Combine(dir, "LOCK"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        Assert(!c.DeleteProject(t.A.ProjectId, "Alpha"));
    Directory.CreateDirectory(Path.Combine(t.Vault, "projects", Guid.NewGuid().ToString("D")));
    RejectCatalog(CatalogFailure.Layout, () => new ProjectCatalog(t.Vault, counting).OpenAndVerify());
});

Test("catalog: a version 1 index (before deletion) still opens and upgrades on the next change", () =>
{
    var v = NewVault(); var id = Guid.NewGuid();
    var index = new RevisionStore(Path.Combine(v, "index"), ProjectCatalog.IndexStoreId, counting);
    index.Commit(index.Initialize().SnapshotId, "projects.json", Text($"{{\"version\":1,\"projects\":[{{\"projectId\":\"{id:D}\",\"name\":\"Legacy\"}}]}}"));
    Directory.CreateDirectory(Path.Combine(v, "projects"));
    new RevisionStore(Path.Combine(v, "projects", id.ToString("D")), id, counting).Initialize();
    var c = new ProjectCatalog(v, counting);
    Assert(c.OpenAndVerify().Single().Name == "Legacy");
    c.CreateProject("Second");
    Assert(c.DeleteProject(id, "Legacy"));
    Assert(new ProjectCatalog(v, counting).OpenAndVerify().Select(p => p.Name).SequenceEqual(new[] { "Second" }));
});

Test("changes: rename keeps the file id, revision and content; names can be swapped; folders move", () =>
{
    var f = Fixture(); var s = f.Store.Initialize();
    s = f.Store.CommitMany(s.SnapshotId, [("a.ts", Text("synthetic-a")), ("b.ts", Text("synthetic-b")), ("src/x.ts", Text("synthetic-x"))]);
    var a = s.Files.Single(x => x.LogicalName == "a.ts"); var b = s.Files.Single(x => x.LogicalName == "b.ts");
    int records = Directory.GetFiles(f.Path, "*.svr").Length;
    var swapped = f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Rename(a.FileId, "b.ts"), new StoreChange.Rename(b.FileId, "a.ts")]);
    Assert(Directory.GetFiles(f.Path, "*.svr").Length == records + 1); // only a new manifest: nothing re-encrypted
    var a2 = swapped.Files.Single(x => x.FileId == a.FileId);
    Assert(a2.LogicalName == "b.ts" && a2.RevisionId == a.RevisionId && Encoding.UTF8.GetString(f.Store.Open(swapped.SnapshotId, a.FileId)) == "synthetic-a");
    var x = swapped.Files.Single(e => e.LogicalName == "src/x.ts");
    var moved = f.Store.CommitChanges(swapped.SnapshotId, [new StoreChange.Rename(x.FileId, "lib/deep/x.ts")]);
    Assert(moved.Files.Single(e => e.FileId == x.FileId).LogicalName == "lib/deep/x.ts");
    var cased = f.Store.CommitChanges(moved.SnapshotId, [new StoreChange.Rename(a.FileId, "B.ts")]); // letter case only
    Assert(cased.Files.Single(e => e.FileId == a.FileId).LogicalName == "B.ts");
});

Test("changes: delete, and rename/write/delete together in one commit", () =>
{
    var f = Fixture(); var s = f.Store.Initialize();
    s = f.Store.CommitMany(s.SnapshotId, [("a.ts", Text("synthetic-a")), ("b.ts", Text("synthetic-b"))]);
    var a = s.Files.Single(x => x.LogicalName == "a.ts"); var b = s.Files.Single(x => x.LogicalName == "b.ts");
    var gone = f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Delete(a.FileId)]);
    Assert(gone.Files.Count == 1 && gone.Files[0].FileId == b.FileId);
    Reject<KeyNotFoundException>(() => f.Store.Open(gone.SnapshotId, a.FileId));
    // Delete b, then a write takes over its old name as a new file.
    var mixed = f.Store.CommitChanges(gone.SnapshotId, [new StoreChange.Delete(b.FileId), new StoreChange.Write("b.ts", Text("synthetic-new"))]);
    Assert(mixed.Files.Single().FileId != b.FileId && Encoding.UTF8.GetString(f.Store.Open(mixed.SnapshotId, mixed.Files[0].FileId)) == "synthetic-new");
    var empty = f.Store.CommitChanges(mixed.SnapshotId, [new StoreChange.Delete(mixed.Files[0].FileId)]);
    Assert(empty.Files.Count == 0 && f.Store.List().SnapshotId == empty.SnapshotId);
});

Test("changes: clashes, unknown ids and stale snapshots are refused before anything is written", () =>
{
    var f = Fixture(); var s = f.Store.Initialize();
    s = f.Store.CommitMany(s.SnapshotId, [("a.ts", Text("synthetic-a")), ("b.ts", Text("synthetic-b")), ("dir/c.ts", Text("synthetic-c"))]);
    var a = s.Files.Single(x => x.LogicalName == "a.ts");
    int records = Directory.GetFiles(f.Path, "*.svr").Length;
    Reject<NameExistsException>(() => f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Rename(a.FileId, "B.TS")]));
    Reject<NameExistsException>(() => f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Rename(a.FileId, "dir")])); // a file where a folder is
    Reject<NameExistsException>(() => f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Rename(a.FileId, "b.ts/inner.ts")])); // a folder where a file is
    Reject<NameExistsException>(() => f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Rename(a.FileId, "z.ts"), new StoreChange.Write("dir", Text("synthetic"))]));
    Reject<KeyNotFoundException>(() => f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Delete(Guid.NewGuid())]));
    Reject<ArgumentException>(() => f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Rename(a.FileId, "x.ts"), new StoreChange.Delete(a.FileId)]));
    Reject<ArgumentException>(() => f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Rename(a.FileId, "../x.ts")]));
    Reject<ArgumentException>(() => f.Store.CommitChanges(s.SnapshotId, []));
    Reject<RevisionConflictException>(() => f.Store.CommitChanges(Guid.NewGuid(), [new StoreChange.Delete(a.FileId)]));
    Assert(Directory.GetFiles(f.Path, "*.svr").Length == records && f.Store.List().SnapshotId == s.SnapshotId);
});

Test("catalog: rename project; names stay unique; letter-case change of its own name allowed", () =>
{
    var t = TwoProjects(); var c = new ProjectCatalog(t.Vault, counting);
    c.OpenAndVerify();
    Reject<NameExistsException>(() => c.RenameProject(t.A.ProjectId, "BETA"));
    Reject<KeyNotFoundException>(() => c.RenameProject(Guid.NewGuid(), "Gamma"));
    Reject<ArgumentException>(() => c.RenameProject(t.A.ProjectId, " padded"));
    c.RenameProject(t.A.ProjectId, "ALPHA");
    c.RenameProject(t.B.ProjectId, "Gamma");
    var after = new ProjectCatalog(t.Vault, counting).OpenAndVerify();
    Assert(after.Select(p => p.Name).SequenceEqual(new[] { "ALPHA", "Gamma" }) && after[0].ProjectId == t.A.ProjectId && after[0].Snapshot.Files.Count == 1);
});

// Per-file history (manifest schema 3).
Test("history: every save is listed newest first with who and when; old revisions open; renames keep history", () =>
{
    var f = Fixture(); var s = f.Store.Initialize();
    s = f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Write("a.ts", Text("synthetic-v1"))], @"PC\alice");
    var id = s.Files.Single().FileId;
    s = f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Write("a.ts", Text("synthetic-v2"))], @"PC\bob");
    s = f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Rename(id, "src/a.ts")], @"PC\bob");
    s = f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Write("src/a.ts", Text("synthetic-v3"))], @"PC\alice");
    var page = f.Store.ReadHistory(id, 0);
    Assert(!page.More && page.Revisions.Select(r => r.Action).SequenceEqual(new[] { "edited", "edited", "created" }));
    Assert(page.Revisions.Select(r => r.SavedBy).SequenceEqual(new[] { @"PC\alice", @"PC\bob", @"PC\alice" }));
    Assert(page.Revisions.Select(r => r.Name).SequenceEqual(new[] { "src/a.ts", "a.ts", "a.ts" }));
    Assert(page.Revisions.All(r => r.SavedAt is { } at && Math.Abs((DateTimeOffset.UtcNow - at).TotalMinutes) < 5));
    Assert(page.Revisions[0].RevisionId == s.Files.Single().RevisionId);
    Assert(Encoding.UTF8.GetString(f.Store.OpenRevision(id, page.Revisions[2].RevisionId)) == "synthetic-v1");
    Assert(Encoding.UTF8.GetString(f.Store.OpenRevision(id, page.Revisions[1].RevisionId)) == "synthetic-v2");
    Reject<KeyNotFoundException>(() => f.Store.OpenRevision(id, Guid.NewGuid()));
    Reject<KeyNotFoundException>(() => f.Store.ReadHistory(Guid.NewGuid(), 0));
    Assert(f.Store.ReadHistory(id, 1).Revisions.Count == 0);
    // Reopened by a fresh instance (cold caches), the history and old content still authenticate.
    var fresh = new RevisionStore(f.Path, f.Project, key);
    Assert(fresh.ReadHistory(id, 0).Revisions.Count == 3 && Encoding.UTF8.GetString(fresh.OpenRevision(id, page.Revisions[2].RevisionId)) == "synthetic-v1");
    // History is encrypted: account names appear nowhere on disk.
    foreach (var file in Directory.GetFiles(f.Path)) Assert(File.ReadAllBytes(file).AsSpan().IndexOf(Text("alice")) < 0);
});

Test("history: deleted files keep their history and can be restored, under their old name or a new one", () =>
{
    var f = Fixture(); var s = f.Store.Initialize();
    s = f.Store.CommitMany(s.SnapshotId, [("a.ts", Text("synthetic-a1")), ("b.ts", Text("synthetic-b"))]);
    var a = s.Files.Single(x => x.LogicalName == "a.ts").FileId;
    s = f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Write("a.ts", Text("synthetic-a2"))]);
    s = f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Delete(a)], @"PC\carol");
    var deleted = f.Store.ListDeleted().Single();
    Assert(deleted.FileId == a && deleted.LogicalName == "a.ts" && deleted.DeletedBy == @"PC\carol" && s.Files.Count == 1);
    Assert(f.Store.ReadHistory(a, 0).Revisions.Count == 2);
    Assert(Encoding.UTF8.GetString(f.Store.OpenRevision(a, f.Store.ReadHistory(a, 0).Revisions[1].RevisionId)) == "synthetic-a1");
    Reject<KeyNotFoundException>(() => f.Store.Open(s.SnapshotId, a));
    // Someone takes the name; restoring under it clashes, under another name works and keeps the file id.
    s = f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Write("a.ts", Text("synthetic-new"))]);
    Reject<NameExistsException>(() => f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Restore(a, "A.TS")]));
    Reject<KeyNotFoundException>(() => f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Restore(s.Files[0].FileId, "x.ts")])); // not deleted
    s = f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Restore(a, "old/a.ts")], @"PC\carol");
    var restored = s.Files.Single(x => x.FileId == a);
    Assert(restored.LogicalName == "old/a.ts" && Encoding.UTF8.GetString(f.Store.Open(s.SnapshotId, a)) == "synthetic-a2");
    Assert(f.Store.ListDeleted().Count == 0 && f.Store.ReadHistory(a, 0).Revisions[0].Action == "restored");
});

Test("history: a schema 2 store upgrades; its files start history at the revision they had", () =>
{
    var path = Path.Combine(testRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
    var project = Guid.NewGuid(); var fileId = Guid.NewGuid(); var revision = Guid.NewGuid(); var snapshot = Guid.NewGuid();
    var content = Text("synthetic-legacy");
    var record = RecordCodec.Encrypt(new(project, fileId, revision, RecordKind.Document), content, key);
    File.WriteAllBytes(Path.Combine(path, revision + ".svr"), record);
    var manifest = JsonSerializer.SerializeToUtf8Bytes(new { version = 2, snapshotId = snapshot.ToString("D"),
        files = new[] { new { fileId = fileId.ToString("D"), name = "legacy.ts", revisionId = revision.ToString("D"), byteLength = content.Length, recordSha256 = Convert.ToHexStringLower(SHA256.HashData(record)) } } });
    File.WriteAllBytes(Path.Combine(path, snapshot + ".svr"), RecordCodec.Encrypt(new(project, Guid.Empty, snapshot, RecordKind.Manifest), manifest, key));
    File.WriteAllText(Path.Combine(path, "HEAD"), snapshot.ToString("D"));
    var store = new RevisionStore(path, project, key);
    var s = store.List();
    Assert(s.Files.Single().LogicalName == "legacy.ts");
    var before = store.ReadHistory(fileId, 0).Revisions.Single();
    Assert(before.Action == "before-history" && before.SavedAt is null && before.RevisionId == revision);
    s = store.CommitChanges(s.SnapshotId, [new StoreChange.Write("legacy.ts", Text("synthetic-after"))], @"PC\dave");
    var history = store.ReadHistory(fileId, 0).Revisions;
    Assert(history.Select(r => r.Action).SequenceEqual(new[] { "edited", "before-history" }));
    Assert(Encoding.UTF8.GetString(store.OpenRevision(fileId, revision)) == "synthetic-legacy");
    Assert(new RevisionStore(path, project, key).List().SnapshotId == s.SnapshotId); // the upgraded manifest reads back
});

Test("history: chunks chain after 256 saves; a tampered history record fails only that file's history", () =>
{
    var f = Fixture(); var s = f.Store.Initialize();
    s = f.Store.CommitMany(s.SnapshotId, [("a.ts", Text("synthetic-0")), ("b.ts", Text("synthetic-b"))]);
    var a = s.Files.Single(x => x.LogicalName == "a.ts").FileId;
    for (int i = 1; i <= 300; i++) s = f.Store.CommitChanges(s.SnapshotId, [new StoreChange.Write("a.ts", Text("synthetic-" + i))]);
    var first = f.Store.ReadHistory(a, 0); var second = f.Store.ReadHistory(a, 1);
    Assert(first.More && first.Revisions.Count == 45 && !second.More && second.Revisions.Count == 256);
    Assert(Encoding.UTF8.GetString(f.Store.OpenRevision(a, second.Revisions[^1].RevisionId)) == "synthetic-0");
    // Corrupt one byte of a's newest history record: its history fails closed, everything else still works.
    var known = new HashSet<string>(s.Files.Select(x => x.RevisionId + ".svr")) { File.ReadAllText(Path.Combine(f.Path, "HEAD")) + ".svr" };
    var fresh = new RevisionStore(f.Path, f.Project, key);
    var newest = Directory.GetFiles(f.Path, "*.svr").Select(p => new FileInfo(p)).Where(i => !known.Contains(i.Name)).OrderByDescending(i => i.LastWriteTimeUtc).ThenByDescending(i => i.CreationTimeUtc).First();
    var raw = File.ReadAllBytes(newest.FullName); raw[^1] ^= 1; File.WriteAllBytes(newest.FullName, raw);
    Reject<InvalidDataException>(() => fresh.ReadHistory(a, 0));
    Assert(fresh.List().Files.Count == 2 && Encoding.UTF8.GetString(fresh.Open(s.SnapshotId, a)) == "synthetic-300");
});

Test("history: warm saves need no TPM unwrap; a cold save of an existing file needs one", () =>
{
    var path = Path.Combine(testRoot, Guid.NewGuid().ToString("N")); var project = Guid.NewGuid();
    var store = new RevisionStore(path, project, counting);
    var s = store.Initialize();
    s = store.CommitMany(s.SnapshotId, [("a.ts", Text("synthetic-1"))]);
    Assert(Unwraps(() => s = store.CommitChanges(s.SnapshotId, [new StoreChange.Write("a.ts", Text("synthetic-2"))])) == 0);
    var cold = new RevisionStore(path, project, counting);
    Assert(Unwraps(() => cold.CommitChanges(cold.List().SnapshotId, [new StoreChange.Write("a.ts", Text("synthetic-3"))])) == 2); // manifest + history
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
// Test-only: wraps an RSA key the test created (the kill test shares one with its child process over stdin).
sealed class ImportedProtector(RSA rsa) : IKeyProtector, IDisposable
{
    public byte[] WrapKey(ReadOnlySpan<byte> key) { lock (rsa) return rsa.Encrypt(key.ToArray(), RSAEncryptionPadding.OaepSHA256); }
    public byte[] UnwrapKey(ReadOnlySpan<byte> key) { lock (rsa) return rsa.Decrypt(key.ToArray(), RSAEncryptionPadding.OaepSHA256); }
    public void Dispose() { }
}
sealed class EphemeralProtector : IKeyProtector, IDisposable
{
    private readonly RSA rsa = RSA.Create(2048);
    public byte[] WrapKey(ReadOnlySpan<byte> key) { lock (rsa) return rsa.Encrypt(key.ToArray(), RSAEncryptionPadding.OaepSHA256); }
    public byte[] UnwrapKey(ReadOnlySpan<byte> key) { lock (rsa) return rsa.Decrypt(key.ToArray(), RSAEncryptionPadding.OaepSHA256); }
    public void Dispose() => rsa.Dispose();
}
