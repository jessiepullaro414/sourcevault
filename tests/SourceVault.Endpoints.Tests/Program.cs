extern alias pack;
using System.ComponentModel;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SourceVault.Client;
using SourceVault.Contracts;
using SourceVault.Packaging;
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

string lastVault = ""; // the vault of the most recent StartServer, for tests that look at its folders
(VaultPipeServer Server, string Pipe) StartServer(PipePeerPolicy? policy = null, TimeProvider? clock = null)
{
    var vault = Path.Combine(workspace, "vault-" + Guid.NewGuid().ToString("N"));
    lastVault = vault;
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

Test("DeleteProject: exact name required, other connections then get NotFound, the name is free again", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var c = new Client(pipe, me))
    using (var other = new Client(pipe, me))
    {
        c.Begin(); other.Begin();
        var created = c.Ok("CreateProject", ("name", "Doomed-Name"));
        var project = created.GetProperty("projectId").GetString()!;
        c.Ok("CreateFile", ("projectId", project), ("name", "a.ts"), ("expectedSnapshotId", created.GetProperty("snapshotId").GetString()!), ("contentBase64", B64("doomed-content")));
        Assert(other.Ok("ListFiles", ("projectId", project)).GetProperty("files").GetArrayLength() == 1, "other sees it");
        var raw = c.Raw("DeleteProject", ("projectId", project), ("confirmName", "doomed-name"));
        Assert(raw.Contains("\"InvalidRequest\"") && !raw.Contains("Doomed", StringComparison.OrdinalIgnoreCase), "case-different name refused, no name echoed");
        Assert(c.Code("DeleteProject", ("projectId", project)) == "InvalidRequest", "confirmName required");
        Assert(c.Code("DeleteProject", ("projectId", Guid.NewGuid().ToString("D")), ("confirmName", "Doomed-Name")) == "NotFound", "unknown project");
        Assert(c.Ok("DeleteProject", ("projectId", project), ("confirmName", "Doomed-Name")).GetProperty("filesRemoved").GetBoolean(), "deleted");
        Assert(other.Code("ListFiles", ("projectId", project)) == "NotFound", "other connection: gone");
        Assert(c.Code("DeleteProject", ("projectId", project), ("confirmName", "Doomed-Name")) == "NotFound", "second delete");
        Assert(c.Ok("ListProjects").GetProperty("projects").GetArrayLength() == 0, "listing empty");
        c.Ok("CreateProject", ("name", "Doomed-Name"));
    }
});

Test("RenameFile, RenameFolder, DeleteFile and RenameProject over the pipe", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var c = new Client(pipe, me))
    {
        c.Begin();
        var created = c.Ok("CreateProject", ("name", "Ops"));
        var project = created.GetProperty("projectId").GetString()!;
        string snap = created.GetProperty("snapshotId").GetString()!;
        string Create(string name) { var r = c.Ok("CreateFile", ("projectId", project), ("name", name), ("expectedSnapshotId", snap), ("contentBase64", B64("synthetic " + name))); snap = r.GetProperty("snapshotId").GetString()!; return r.GetProperty("fileId").GetString()!; }
        var a = Create("src/app/a.ts"); var b = Create("src/app/b.ts"); var readme = Create("README.md");
        Assert(c.Code("RenameFile", ("projectId", project), ("fileId", a), ("newName", "src/app/b.ts"), ("expectedSnapshotId", snap)) == "AlreadyExists", "rename onto a file");
        Assert(c.Code("RenameFile", ("projectId", project), ("fileId", a), ("newName", "../a.ts"), ("expectedSnapshotId", snap)) == "InvalidRequest", "bad name");
        Assert(c.Code("RenameFile", ("projectId", project), ("fileId", Guid.NewGuid().ToString("D")), ("newName", "z.ts"), ("expectedSnapshotId", snap)) == "NotFound", "unknown file");
        Assert(c.Code("RenameFile", ("projectId", project), ("fileId", a), ("newName", "z.ts"), ("expectedSnapshotId", Guid.NewGuid().ToString("D"))) == "RevisionConflict", "stale");
        snap = c.Ok("RenameFile", ("projectId", project), ("fileId", readme), ("newName", "docs/README.md"), ("expectedSnapshotId", snap)).GetProperty("snapshotId").GetString()!;
        var folder = c.Ok("RenameFolder", ("projectId", project), ("oldPrefix", "SRC/app"), ("newPrefix", "lib"), ("expectedSnapshotId", snap));
        Assert(folder.GetProperty("renamed").GetInt32() == 2, "two files moved");
        snap = folder.GetProperty("snapshotId").GetString()!;
        Assert(c.Code("RenameFolder", ("projectId", project), ("oldPrefix", "nothing"), ("newPrefix", "x"), ("expectedSnapshotId", snap)) == "NotFound", "empty folder");
        Assert(c.Code("RenameFolder", ("projectId", project), ("oldPrefix", "lib"), ("newPrefix", "docs/README.md"), ("expectedSnapshotId", snap)) == "AlreadyExists", "folder onto a file");
        var files = c.Ok("ListFiles", ("projectId", project)).GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("fileId").GetString()!, f => f.GetProperty("name").GetString()!);
        Assert(files[a] == "lib/a.ts" && files[b] == "lib/b.ts" && files[readme] == "docs/README.md", "names after moves");
        var opened = c.Ok("OpenDocument", ("projectId", project), ("fileId", a), ("expectedSnapshotId", snap));
        Assert(Encoding.UTF8.GetString(opened.GetProperty("contentBase64").GetBytesFromBase64()) == "synthetic src/app/a.ts", "content unchanged by moves");
        snap = c.Ok("DeleteFile", ("projectId", project), ("fileId", b), ("expectedSnapshotId", snap)).GetProperty("snapshotId").GetString()!;
        Assert(c.Code("OpenDocument", ("projectId", project), ("fileId", b), ("expectedSnapshotId", snap)) == "NotFound", "deleted");
        Assert(c.Code("DeleteFile", ("projectId", project), ("fileId", b), ("expectedSnapshotId", snap)) == "NotFound", "deleted twice");
        var raw = c.Raw("RenameProject", ("projectId", project), ("newName", "Ops"));
        Assert(raw.Contains("\"Ok\""), "same name is fine");
        c.Ok("CreateProject", ("name", "Other"));
        raw = c.Raw("RenameProject", ("projectId", project), ("newName", "OTHER"));
        Assert(raw.Contains("\"AlreadyExists\"") && !raw.Contains("Other", StringComparison.OrdinalIgnoreCase), "project name clash, no name echoed");
        c.Ok("RenameProject", ("projectId", project), ("newName", "Ops renamed"));
        Assert(c.Ok("ListProjects").GetProperty("projects").EnumerateArray().Any(p => p.GetProperty("name").GetString() == "Ops renamed"), "renamed project listed");
    }
});

Test("history over the pipe: who and when, old versions, deleted files and restore", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var c = new Client(pipe, me))
    {
        c.Begin();
        var created = c.Ok("CreateProject", ("name", "History"));
        var project = created.GetProperty("projectId").GetString()!;
        var file = c.Ok("CreateFile", ("projectId", project), ("name", "a.ts"), ("expectedSnapshotId", created.GetProperty("snapshotId").GetString()!), ("contentBase64", B64("version one")));
        var fileId = file.GetProperty("fileId").GetString()!; var firstRevision = file.GetProperty("revisionId").GetString()!;
        var snap = c.Ok("ApplyEdits", ("projectId", project), ("fileId", fileId), ("expectedSnapshotId", file.GetProperty("snapshotId").GetString()!), ("contentBase64", B64("version two"))).GetProperty("snapshotId").GetString()!;
        var history = c.Ok("ListHistory", ("projectId", project), ("fileId", fileId), ("page", 0));
        var revisions = history.GetProperty("revisions").EnumerateArray().ToList();
        Assert(revisions.Count == 2 && !history.GetProperty("more").GetBoolean(), "two versions");
        Assert(revisions[0].GetProperty("action").GetString() == "edited" && revisions[1].GetProperty("action").GetString() == "created", "actions");
        var account = me.Translate(typeof(NTAccount)).Value;
        Assert(revisions.All(r => r.GetProperty("savedBy").GetString() == account), "saved by the caller's account");
        Assert(revisions[1].GetProperty("revisionId").GetString() == firstRevision, "oldest is the first revision");
        var old = c.Ok("OpenRevision", ("projectId", project), ("fileId", fileId), ("revisionId", firstRevision));
        Assert(Encoding.UTF8.GetString(old.GetProperty("contentBase64").GetBytesFromBase64()) == "version one", "old content");
        Assert(c.Code("OpenRevision", ("projectId", project), ("fileId", fileId), ("revisionId", Guid.NewGuid().ToString("D"))) == "NotFound", "unknown revision");
        Assert(c.Code("ListHistory", ("projectId", project), ("fileId", fileId), ("page", -1)) == "InvalidRequest", "negative page");
        Assert(c.Code("ListHistory", ("projectId", project), ("fileId", fileId), ("page", "0")) == "InvalidRequest", "page as text");
        snap = c.Ok("DeleteFile", ("projectId", project), ("fileId", fileId), ("expectedSnapshotId", snap)).GetProperty("snapshotId").GetString()!;
        var deleted = c.Ok("ListDeletedFiles", ("projectId", project)).GetProperty("files").EnumerateArray().Single();
        Assert(deleted.GetProperty("fileId").GetString() == fileId && deleted.GetProperty("name").GetString() == "a.ts" && deleted.GetProperty("deletedBy").GetString() == account, "deleted listed");
        Assert(c.Ok("ListHistory", ("projectId", project), ("fileId", fileId), ("page", 0)).GetProperty("revisions").GetArrayLength() == 2, "deleted file keeps history");
        snap = c.Ok("CreateFile", ("projectId", project), ("name", "a.ts"), ("expectedSnapshotId", snap), ("contentBase64", B64("someone else"))).GetProperty("snapshotId").GetString()!;
        var raw = c.Raw("RestoreFile", ("projectId", project), ("fileId", fileId), ("newName", "a.ts"), ("expectedSnapshotId", snap));
        Assert(raw.Contains("\"AlreadyExists\"") && !raw.Contains("a.ts"), "restore onto a taken name");
        snap = c.Ok("RestoreFile", ("projectId", project), ("fileId", fileId), ("newName", "restored/a.ts"), ("expectedSnapshotId", snap)).GetProperty("snapshotId").GetString()!;
        var back = c.Ok("OpenDocument", ("projectId", project), ("fileId", fileId), ("expectedSnapshotId", snap));
        Assert(Encoding.UTF8.GetString(back.GetProperty("contentBase64").GetBytesFromBase64()) == "version two", "restored at its last version");
        Assert(c.Ok("ListDeletedFiles", ("projectId", project)).GetProperty("files").GetArrayLength() == 0, "no longer deleted");
    }
});

Test("builds: queue, run through the build service, status, encrypted logs that survive a restart", () =>
{
    var vault = Path.Combine(workspace, "vault-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(vault);
    var catalog = new ProjectCatalog(vault, key);
    var fake = new FakeBuildService();
    var builds = new BuildCoordinator(catalog, fake);
    var finished = new System.Collections.Concurrent.BlockingCollection<BuildRecord>();
    builds.Finished += (_, r) => finished.Add(r);
    var name = "SourceVault.test." + Guid.NewGuid().ToString("N");
    using var server = new VaultPipeServer(name, me, new PipePeerPolicy(authenticatedUsers, [self]), new VaultProtocol(catalog, catalog.OpenAndVerify(), builds: builds));
    server.Start();
    using var c = new Client(name, me);
    c.Begin();
    var created = c.Ok("CreateProject", ("name", "Buildable"));
    var project = created.GetProperty("projectId").GetString()!;
    var snap = c.Ok("CreateFile", ("projectId", project), ("name", "src/app.ts"), ("expectedSnapshotId", created.GetProperty("snapshotId").GetString()!), ("contentBase64", B64("export const synthetic = 1;\n"))).GetProperty("snapshotId").GetString()!;
    c.Ok("CreateFile", ("projectId", project), ("name", "build.sh"), ("expectedSnapshotId", snap), ("contentBase64", B64("echo build\n")));
    Assert(c.Ok("ListBuildTargets").GetProperty("targets").EnumerateArray().Select(t => t.GetString()).SequenceEqual(["linux"]), "targets");
    Assert(c.Code("RequestBuild", ("projectId", project), ("target", "mac")) == "NotFound", "unknown target");
    Assert(c.Code("RequestBuild", ("projectId", Guid.NewGuid().ToString("D")), ("target", "linux")) == "NotFound", "unknown project");
    var number = c.Ok("RequestBuild", ("projectId", project), ("target", "linux")).GetProperty("buildNumber").GetInt32();
    Assert(number == 1, "first build is 1");
    Assert(finished.TryTake(out var done, TimeSpan.FromSeconds(20)) && done.State == "succeeded", "finished");
    var status = c.Ok("GetBuildStatus", ("projectId", project), ("buildNumber", 1));
    Assert(status.GetProperty("state").GetString() == "succeeded" && status.GetProperty("artifactFolder").GetString() == @"D:\artifacts\1-linux" && status.GetProperty("logAvailable").GetBoolean(), "status");
    Assert(status.GetProperty("artifactFiles").GetInt32() == 3 && status.GetProperty("artifactsDropped").GetInt32() == 2, "dropped count stored and reported");
    Assert(status.GetProperty("requestedBy").GetString() == me.Translate(typeof(NTAccount)).Value, "requested by the caller");
    var log = Encoding.UTF8.GetString(c.Ok("OpenBuildLog", ("projectId", project), ("buildNumber", 1)).GetProperty("contentBase64").GetBytesFromBase64());
    Assert(log == "synthetic-build-log: 2 files\n", "log text: " + log);
    Assert(fake.Received.Single().Select(f => f.Name).OrderBy(n => n).SequenceEqual(["build.sh", "src/app.ts"]) && fake.ReceivedText["src/app.ts"] == "export const synthetic = 1;\n", "the build service got the decrypted project");
    fake.Fail = true;
    Assert(c.Ok("RequestBuild", ("projectId", project), ("target", "linux")).GetProperty("buildNumber").GetInt32() == 2, "second build");
    Assert(finished.TryTake(out var second, TimeSpan.FromSeconds(20)) && second.State == "failed" && second.ExitCode == 1, "failed build recorded");
    Assert(c.Ok("ListBuilds", ("projectId", project)).GetProperty("builds").EnumerateArray().Select(b => b.GetProperty("buildNumber").GetInt32()).SequenceEqual([2, 1]), "newest first");
    Assert(c.Code("GetBuildStatus", ("projectId", project), ("buildNumber", 99)) == "NotFound", "unknown build");
    Assert(c.Code("GetBuildStatus", ("projectId", project), ("buildNumber", 0)) == "InvalidRequest", "build numbers start at 1");
    // Logs and records are encrypted on disk and come back after a restart.
    foreach (var file in Directory.GetFiles(Path.Combine(vault, "builds"), "*", SearchOption.AllDirectories))
        Assert(File.ReadAllBytes(file).AsSpan().IndexOf("synthetic-build-log"u8) < 0, "no plaintext log on disk");
    var restarted = new BuildCoordinator(new ProjectCatalog(vault, key), fake);
    Assert(restarted.List(Guid.Parse(project)).Select(r => r.State).SequenceEqual(["failed", "succeeded"]), "records after restart");
    // A build service that is not there makes builds unavailable, nothing else.
    var offline = new BuildCoordinator(catalog, new FakeBuildService { Offline = true });
    try { offline.Targets(); throw new Exception("offline targets"); } catch (SourceVault.Build.BuildServiceUnavailableException) { }
    // Deleting the project deletes its build logs.
    c.Ok("DeleteProject", ("projectId", project), ("confirmName", "Buildable"));
    Assert(!Directory.Exists(Path.Combine(vault, "builds", project)), "build logs removed with the project");
    Assert(new ProjectCatalog(vault, key).OpenAndVerify().Count == 0, "vault still verifies");
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
        Assert(c.Code("CreateFile", ("projectId", project), ("name", "a//b.ts"), ("expectedSnapshotId", s0), ("contentBase64", B64("x"))) == "InvalidRequest", "bad file name");
        Assert(c.Code("CreateFile", ("projectId", project), ("name", "big.ts"), ("expectedSnapshotId", s0), ("contentBase64", Convert.ToBase64String(new byte[262145]))) == "LimitExceeded", "content limit");
        Assert(c.Code("CreateFile", ("projectId", project), ("name", "b64.ts"), ("expectedSnapshotId", s0), ("contentBase64", "@@@")) == "InvalidRequest", "invalid base64");
        Assert(c.Code("CreateFile", ("projectId", project), ("name", "max.ts"), ("expectedSnapshotId", s0), ("contentBase64", Convert.ToBase64String(new byte[262144]))) == "Ok", "exactly 256 KiB fits a frame");
        var s1 = c.Ok("ListFiles", ("projectId", project)).GetProperty("snapshotId").GetString()!;
        Assert(c.Code("CreateFile", ("projectId", project), ("name", "bin.ts"), ("expectedSnapshotId", s1), ("contentBase64", Convert.ToBase64String(new byte[] { 0xff }))) == "Ok", "any bytes inline (stored as binary)");
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

Test("unwrap budget over the pipe: warm list 1 (index), warm open 1, warm edit 1 (the project key, never cached between operations)", () =>
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
        key.Unwraps = 0; c.Ok("ApplyEdits", ("projectId", project), ("fileId", fileId), ("expectedSnapshotId", s1), ("contentBase64", B64("y"))); Assert(key.Unwraps == 1, "warm ApplyEdits = " + key.Unwraps);
    }
});

Test("lab client: check and list run end to end and never print document content", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    {
        SourceVault.Client.VaultClient Connect() => new(VaultPipe.Connect(pipe, me, TimeSpan.FromSeconds(5)));
        // A service-created demo project, so the check also decrypts a file it did not write itself.
        using (var setup = new Client(pipe, me))
        {
            setup.Begin();
            var demo = setup.Ok("CreateProject", ("name", "Synthetic demo 1"));
            setup.Ok("CreateFile", ("projectId", demo.GetProperty("projectId").GetString()!), ("name", "README.md"),
                ("expectedSnapshotId", demo.GetProperty("snapshotId").GetString()!), ("contentBase64", B64("# Synthetic demo\n")));
        }
        var first = new StringWriter();
        Assert(SourceVault.LabClient.LabCommands.Check(Connect, first) == 0, "first check: " + first);
        Assert(first.ToString().Contains("created project") && first.ToString().Contains("created check.txt"), "first run creates");
        var second = new StringWriter();
        Assert(SourceVault.LabClient.LabCommands.Check(Connect, second) == 0, "second check: " + second);
        Assert(second.ToString().Contains("saved a new revision") && second.ToString().Contains("decrypted README.md"), "second run edits");
        var listing = new StringWriter();
        Assert(SourceVault.LabClient.LabCommands.List(Connect, listing) == 0 && listing.ToString().Contains("check.txt"), "list");
        foreach (var output in new[] { first.ToString(), second.ToString(), listing.ToString() })
            Assert(!output.Contains("nonce:") && !output.Contains("# Synthetic demo") && !output.Contains("SourceVault lab client check"), "no content printed");
        var missing = new StringWriter();
        Assert(SourceVault.LabClient.LabCommands.SaveCheck(Connect, missing) == 1 && missing.ToString().Contains("run save-loop first"), "save-check before any loop");
        var loop = new StringWriter();
        Assert(SourceVault.LabClient.LabCommands.SaveLoop(Connect, loop, 5) == 0 && loop.ToString().Contains("starting at count 0") && loop.ToString().TrimEnd().EndsWith("saved 5"), "save-loop: " + loop);
        Assert(SourceVault.LabClient.LabCommands.SaveLoop(Connect, new StringWriter(), 3) == 0, "a second loop continues");
        var check = new StringWriter();
        Assert(SourceVault.LabClient.LabCommands.SaveCheck(Connect, check) == 0 && check.ToString().Contains("holds count 8."), "save-check: " + check);
    }
});

// ---- Editor vault controller (SourceVault.Client) against the same in-process service ----
EditorVaultController Controller(string pipe) =>
    new(_ => Task.FromResult(new SourceVault.Client.VaultClient(VaultPipe.Connect(pipe, me, TimeSpan.FromSeconds(5)))));
T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();
void ExternalEdit(string pipe, Guid projectId, Guid fileId, string text)
{
    using var other = new SourceVault.Client.VaultClient(VaultPipe.Connect(pipe, me, TimeSpan.FromSeconds(5)));
    other.Begin();
    var listing = other.Call("ListFiles", w => w.WriteString("projectId", projectId.ToString("D")));
    var edited = other.Call("ApplyEdits", w =>
    {
        w.WriteString("projectId", projectId.ToString("D")); w.WriteString("fileId", fileId.ToString("D"));
        w.WriteString("expectedSnapshotId", listing.Body.GetProperty("snapshotId").GetString()); w.WriteBase64String("contentBase64", Encoding.UTF8.GetBytes(text));
    });
    if (!edited.Ok) throw new Exception("external edit failed: " + edited.Code);
}
LockReason AwaitLock(TaskCompletionSource<LockReason> tcs) =>
    tcs.Task.Wait(TimeSpan.FromSeconds(5)) ? tcs.Task.Result : throw new Exception("Locked event not raised");

Test("controller: per-file concurrency — saving one file never conflicts another; text round-trips", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var ctl = Controller(pipe))
    {
        Assert(Run(ctl.UnlockAsync()) is { Ok: true, Value.Count: 0 }, "unlock");
        var project = Run(ctl.CreateProjectAsync("Controller")).Value!.ProjectId;
        var a = Run(ctl.CreateFileAsync(project, "a.ts", "A1")).Value!.FileId;
        var b = Run(ctl.CreateFileAsync(project, "b.ts", "B1")).Value!.FileId;
        Assert(Run(ctl.SaveDocumentAsync(project, a, "A2")).Ok, "save a");
        Assert(Run(ctl.SaveDocumentAsync(project, b, "B2")).Ok, "save b after a moved the project snapshot");
        Assert(Run(ctl.SaveDocumentAsync(project, a, "A3 — é ✓ 𝄞")).Ok, "save a again");
        using var fresh = Controller(pipe);
        Run(fresh.UnlockAsync());
        Assert(Run(fresh.OpenDocumentAsync(project, a)).Value!.Text == "A3 — é ✓ 𝄞", "non-ASCII round trip");
        Assert(Run(fresh.OpenDocumentAsync(project, b)).Value!.Text == "B2", "b content");
        Assert(Run(fresh.ListProjectsAsync()).Value!.Single().FileCount == 2, "project listing");
    }
});

Test("controller: deleting a project forgets its opened files", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var ctl = Controller(pipe))
    {
        Run(ctl.UnlockAsync());
        var project = Run(ctl.CreateProjectAsync("Gone")).Value!.ProjectId;
        var a = Run(ctl.CreateFileAsync(project, "a.ts", "A1")).Value!.FileId;
        Assert(Run(ctl.DeleteProjectAsync(project, "gone")).Code == "InvalidRequest", "wrong name");
        Assert(Run(ctl.DeleteProjectAsync(project, "Gone")) is { Ok: true, Value: true }, "deleted");
        Assert(Run(ctl.SaveDocumentAsync(project, a, "A2")).Code == "NotOpen", "opened entry dropped");
        Assert(Run(ctl.ListProjectsAsync()).Value!.Count == 0, "listing empty");
    }
});

Test("controller: rename keeps an open file saveable; deleting a file someone else changed is Conflict", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var ctl = Controller(pipe))
    {
        Run(ctl.UnlockAsync());
        var project = Run(ctl.CreateProjectAsync("FileOps")).Value!.ProjectId;
        var a = Run(ctl.CreateFileAsync(project, "src/a.ts", "A1")).Value!.FileId;
        var b = Run(ctl.CreateFileAsync(project, "src/b.ts", "B1")).Value!.FileId;
        Assert(Run(ctl.RenameFileAsync(project, a, "lib/a.ts")).Ok, "rename");
        Assert(Run(ctl.SaveDocumentAsync(project, a, "A2")).Ok, "still saves after a rename");
        Assert(Run(ctl.RenameFolderAsync(project, "src", "old")) is { Ok: true, Value: 1 }, "folder rename");
        Assert(Run(ctl.RenameProjectAsync(project, "FileOps 2")).Ok, "project rename");
        ExternalEdit(pipe, project, b, "B-by-someone-else");
        Assert(Run(ctl.DeleteFileAsync(project, b)).Code == "Conflict", "changed elsewhere since opened");
        Assert(Run(ctl.DeleteFileAsync(project, a)).Ok, "delete");
        Assert(Run(ctl.SaveDocumentAsync(project, a, "A3")).Code == "NotOpen", "deleted file forgotten");
        Assert(Run(ctl.ListFilesAsync(project)).Value!.Select(f => f.Name).SequenceEqual(new[] { "old/b.ts" }), "remaining");
    }
});

Test("controller: history, restore of a deleted file, and merge sources saved on top of theirs", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var ctl = Controller(pipe))
    {
        Run(ctl.UnlockAsync());
        var project = Run(ctl.CreateProjectAsync("Merge")).Value!.ProjectId;
        var a = Run(ctl.CreateFileAsync(project, "a.ts", "line 1\nline 2\nline 3\n")).Value!.FileId;
        ExternalEdit(pipe, project, a, "line 1\nline 2 (theirs)\nline 3\n");
        Assert(Run(ctl.SaveDocumentAsync(project, a, "line 1 (mine)\nline 2\nline 3\n")).Code == "Conflict", "conflict first");
        var sources = Run(ctl.MergeSourcesAsync(project, a));
        Assert(sources.Ok && sources.Value!.BaseText == "line 1\nline 2\nline 3\n" && sources.Value.TheirsText == "line 1\nline 2 (theirs)\nline 3\n", "base and theirs");
        ExternalEdit(pipe, project, a, "line 1\nline 2 (theirs again)\nline 3\n");
        Assert(Run(ctl.SaveMergedAsync(project, a, "merged", sources.Value!.TheirsRevision)).Code == "Conflict", "changed again since the merge");
        sources = Run(ctl.MergeSourcesAsync(project, a));
        Assert(Run(ctl.SaveMergedAsync(project, a, "line 1 (mine)\nline 2 (theirs again)\nline 3\n", sources.Value!.TheirsRevision)).Ok, "merged save");
        Assert(Run(ctl.SaveDocumentAsync(project, a, "after merge")).Ok, "normal saves continue from the merged revision");
        var history = Run(ctl.ListHistoryAsync(project, a, 0)).Value!;
        Assert(history.Revisions.Count == 5 && history.Revisions[^1].Action == "created", "five versions");
        Assert(Run(ctl.OpenRevisionAsync(project, a, history.Revisions[^1].RevisionId)).Value == "line 1\nline 2\nline 3\n", "oldest text");
        Assert(Run(ctl.DeleteFileAsync(project, a)).Ok, "delete");
        var deleted = Run(ctl.ListDeletedAsync(project)).Value!.Single();
        Assert(deleted.FileId == a && deleted.Name == "a.ts", "listed as deleted");
        Assert(Run(ctl.RestoreFileAsync(project, a, "a.ts")).Ok, "restore");
        Assert(Run(ctl.ListFilesAsync(project)).Value!.Single().FileId == a, "back");
        Assert(Run(ctl.MergeSourcesAsync(project, Guid.NewGuid())).Code == "NotOpen", "unknown file");
    }
});

Test("controller: someone else's edit is Conflict; other files still save", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var ctl = Controller(pipe))
    {
        Run(ctl.UnlockAsync());
        var project = Run(ctl.CreateProjectAsync("Shared")).Value!.ProjectId;
        var a = Run(ctl.CreateFileAsync(project, "a.ts", "A1")).Value!.FileId;
        var b = Run(ctl.CreateFileAsync(project, "b.ts", "B1")).Value!.FileId;
        ExternalEdit(pipe, project, a, "A-by-someone-else");
        Assert(Run(ctl.SaveDocumentAsync(project, a, "A-mine")).Code == "Conflict", "conflict on a");
        Assert(Run(ctl.SaveDocumentAsync(project, b, "B-mine")).Ok, "b still saves");
        Assert(Run(ctl.OpenDocumentAsync(project, a)).Value!.Text == "A-by-someone-else", "reload sees their version");
        Assert(Run(ctl.SaveDocumentAsync(project, a, "A-after-reload")).Ok, "save after reload");
    }
});

Test("controller: a commit racing in between check and commit is retried once", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var ctl = Controller(pipe))
    {
        Run(ctl.UnlockAsync());
        var project = Run(ctl.CreateProjectAsync("Race")).Value!.ProjectId;
        var a = Run(ctl.CreateFileAsync(project, "a.ts", "A1")).Value!.FileId;
        var b = Run(ctl.CreateFileAsync(project, "b.ts", "B1")).Value!.FileId;
        int hooks = 0;
        ctl.BeforeCommit = () => { if (hooks++ == 0) ExternalEdit(pipe, project, b, "B-racing"); return Task.CompletedTask; };
        Assert(Run(ctl.SaveDocumentAsync(project, a, "A2")).Ok && hooks == 2, "retried after the race");
        ctl.BeforeCommit = null;
        Assert(Run(ctl.OpenDocumentAsync(project, a)).Value!.Text == "A2", "a saved");
    }
});

Test("controller: expiry and Windows session end lock it with a reason; unlock re-checks drafts", () =>
{
    var clock = new ManualClock();
    var (server, pipe) = StartServer(clock: clock);
    using (server)
    using (var ctl = Controller(pipe))
    {
        var reasons = new TaskCompletionSource<LockReason>();
        ctl.Locked += r => reasons.TrySetResult(r);
        Run(ctl.UnlockAsync());
        var project = Run(ctl.CreateProjectAsync("Locks")).Value!.ProjectId;
        var a = Run(ctl.CreateFileAsync(project, "a.ts", "A1")).Value!.FileId;
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert(Run(ctl.SaveDocumentAsync(project, a, "A2")).Code == "Locked" && ctl.IsLocked, "expired session locks");
        Assert(AwaitLock(reasons) == LockReason.Expired, "reason expired");
        Assert(Run(ctl.UnlockAsync()) is { Ok: true, Value.Count: 0 }, "unlock, nothing changed");

        reasons = new TaskCompletionSource<LockReason>();
        ctl.LockAsync().GetAwaiter().GetResult();
        Assert(AwaitLock(reasons) == LockReason.User, "reason user");
        ExternalEdit(pipe, project, a, "A-while-locked");
        var unlocked = Run(ctl.UnlockAsync());
        Assert(unlocked.Ok && unlocked.Value!.Single() == (project, a), "unlock reports the changed draft");
        Assert(Run(ctl.SaveDocumentAsync(project, a, "A-draft")).Code == "Conflict", "draft save conflicts");

        reasons = new TaskCompletionSource<LockReason>();
        server.EndWindowsSession(unchecked((uint)System.Diagnostics.Process.GetCurrentProcess().SessionId));
        Assert(Run(ctl.ListProjectsAsync()).Code == "Locked", "Windows lock closes the connection");
        Assert(AwaitLock(reasons) == LockReason.Closed, "reason closed");
    }
});

Test("controller: locked, not-open and content limits", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var ctl = Controller(pipe))
    {
        Assert(Run(ctl.ListProjectsAsync()).Code == "Locked", "starts locked");
        Run(ctl.UnlockAsync());
        var project = Run(ctl.CreateProjectAsync("Limits")).Value!.ProjectId;
        Assert(Run(ctl.SaveDocumentAsync(project, Guid.NewGuid(), "x")).Code == "NotOpen", "save of a file never opened");
        Assert(Run(ctl.CreateFileAsync(project, "big.ts", new string('x', EditorVaultController.MaxEditorTextBytes + 1))).Code == "LimitExceeded", "over 1 MiB");
        Assert(Run(ctl.CreateFileAsync(project, "bad.ts", "\ud800")).Code == "InvalidRequest", "lone surrogate is not UTF-8");
        Assert(Run(ctl.CreateFileAsync(project, "a/../b.ts", "x")).Code == "InvalidRequest", "bad name");
        Assert(Run(ctl.OpenDocumentAsync(project, Guid.NewGuid())).Code == "NotFound", "unknown file");
    }
});

// ---- any-file vault in the editor host (docs/vault-any-file-design.md section 4) ----
DiskFile Disk(string name, byte[] bytes) => new(name, bytes.Length, () => new MemoryStream(bytes, writable: false));

Test("controller: text of 256 KiB to 1 MiB opens and saves in parts; history and merge read it in parts", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var ctl = Controller(pipe))
    {
        Run(ctl.UnlockAsync());
        var project = Run(ctl.CreateProjectAsync("Large text")).Value!.ProjectId;
        string Lines(char c, int kib) => string.Concat(Enumerable.Range(0, kib * 16).Select(i => $"{c}{i:D10} line of synthetic text 0123456789abcdefghijklmnopqr\n")); // 64 bytes a line
        var first = Lines('a', 700);
        var created = Run(ctl.CreateFileAsync(project, "big.txt", first));
        Assert(created is { Ok: true, Value.ByteLength: 700 * 1024, Value.Kind: "text" }, "created through an upload: " + created.Code);
        var id = created.Value!.FileId;
        Assert(Run(ctl.SaveDocumentAsync(project, id, Lines('b', 1000))).Ok, "saved in parts");
        using var fresh = Controller(pipe);
        Run(fresh.UnlockAsync());
        Assert(Run(fresh.OpenDocumentAsync(project, id)).Value!.Text == Lines('b', 1000), "opened in parts");
        var history = Run(ctl.ListHistoryAsync(project, id, 0)).Value!.Revisions;
        Assert(Run(ctl.OpenRevisionAsync(project, id, history[1].RevisionId)).Value == first, "old revision read in parts");
        ExternalEdit(pipe, project, id, "small now\n"); // someone else
        Assert(Run(fresh.SaveDocumentAsync(project, id, Lines('c', 400))).Code == "Conflict", "conflict");
        var merge = Run(fresh.MergeSourcesAsync(project, id)).Value!;
        Assert(merge.Mergeable && merge.BaseText == Lines('b', 1000) && merge.TheirsText == "small now\n", "merge sources across sizes");
        Assert(Run(fresh.SaveMergedAsync(project, id, Lines('c', 400), merge.TheirsRevision)).Ok, "merged save in parts");
        Assert(Run(fresh.ListFilesAsync(project)).Value!.Single().Openable, "still openable at 400 KiB");
    }
});

Test("controller: binary files: info, replace from disk, revert, history; never opened as text; search skips them", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var ctl = Controller(pipe))
    {
        Run(ctl.UnlockAsync());
        var project = Run(ctl.CreateProjectAsync("Binary")).Value!.ProjectId;
        var icon = new byte[700 * 1024]; new Random(3).NextBytes(icon); icon[0] = 0;
        var added = Run(ctl.AddFromDiskAsync(project, "res", [Disk("app.ico", icon), Disk("notes.txt", Encoding.UTF8.GetBytes("Widget notes\n"))]));
        Assert(added is { Ok: true, Value.Count: 2 } && added.Value[0].Name == "res/app.ico" && added.Value[0].Kind == "binary" && added.Value[1].Kind == "text", "added: " + added.Code);
        var id = added.Value![0].FileId;
        Assert(added.Value[0].Sha256 == Convert.ToHexStringLower(SHA256.HashData(icon)), "hash of the plaintext");
        Assert(Run(ctl.OpenDocumentAsync(project, id)).Code == "NotText", "binary never opens as text");
        var info = Run(ctl.FileInfoAsync(project, id)).Value!;
        Assert(info.File.ByteLength == icon.Length && !info.File.Openable && info.SavedBy is not null && info.SavedAt is not null, "info panel");
        var second = new byte[3000]; new Random(4).NextBytes(second); second[0] = 0;
        var replaced = Run(ctl.ReplaceFromDiskAsync(project, id, Disk("other.ico", second)));
        Assert(replaced is { Ok: true, Value.ByteLength: 3000, Value.Kind: "binary" } && replaced.Value.Name == "res/app.ico", "replaced, same name: " + replaced.Code);
        var history = Run(ctl.ListHistoryAsync(project, id, 0)).Value!.Revisions;
        Assert(history.Count == 2 && history.All(r => r.Kind == "binary" && r.Sha256 is { Length: 64 }), "history with kind and hash");
        Assert(Run(ctl.OpenRevisionAsync(project, id, history[1].RevisionId)).Code == "NotText", "no binary content to the editor");
        Assert(Run(ctl.RevertFileAsync(project, id, history[1].RevisionId)).Ok, "revert");
        Assert(Run(ctl.ListFilesAsync(project)).Value!.Single(f => f.FileId == id).Sha256 == Convert.ToHexStringLower(SHA256.HashData(icon)), "first version current again");
        Assert(Run(ctl.ListHistoryAsync(project, id, 0)).Value!.Revisions[0].Action == "reverted", "reverted in history");
        var search = Run(ctl.SearchProjectAsync(project, "Widget", false, false)).Value!;
        Assert(search.Matches.Count == 1 && search.FilesSkipped == 1, "the binary is skipped and counted");
        Assert(Run(ctl.AddFromDiskAsync(project, "res", [Disk("app.ico", [1, 2])])).Code == "AlreadyExists", "a taken name adds nothing");
        Assert(Run(ctl.ListFilesAsync(project)).Value!.Count == 2, "still two files");
    }
});

Test("controller: a replacement that conflicts is staged for keep mine; keep theirs discards it; bad disk reads change nothing", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var ctl = Controller(pipe))
    {
        Run(ctl.UnlockAsync());
        var project = Run(ctl.CreateProjectAsync("Keep")).Value!.ProjectId;
        var id = Run(ctl.AddFromDiskAsync(project, "", [Disk("logo.png", [0x89, 0x50, 0x4E, 0x47, 0, 1])])).Value![0].FileId;
        Run(ctl.FileInfoAsync(project, id));
        ExternalEdit(pipe, project, id, "someone else's text\n");
        Assert(Run(ctl.ReplaceFromDiskAsync(project, id, Disk("mine.png", [0x89, 0, 7]))).Code == "Conflict", "conflict");
        var mine = Run(ctl.KeepMineAsync(project, id));
        Assert(mine is { Ok: true, Value.ByteLength: 3, Value.Kind: "binary" }, "keep mine commits the staged upload: " + mine.Code);
        Assert(Run(ctl.ListHistoryAsync(project, id, 0)).Value!.Revisions.Count == 3, "theirs stays in history");
        Assert(Run(ctl.KeepMineAsync(project, id)).Code == "UploadExpired", "nothing staged any more");
        ExternalEdit(pipe, project, id, "theirs again\n");
        Assert(Run(ctl.ReplaceFromDiskAsync(project, id, Disk("mine.png", [0x89, 0, 8]))).Code == "Conflict", "conflict again");
        var theirs = Run(ctl.KeepTheirsAsync(project, id));
        Assert(theirs is { Ok: true, Value.Kind: "text" } && Run(ctl.ListHistoryAsync(project, id, 0)).Value!.Revisions.Count == 4, "keep theirs saves nothing");
        Assert(Run(ctl.ReplaceFromDiskAsync(project, id, Disk("next.png", [0x89, 0, 9]))).Ok, "theirs is now the version shown");
        // The file on disk changes between the hash and the upload, or can't be read.
        int opens = 0;
        var changing = new DiskFile("x.bin", 4, () => new MemoryStream(++opens == 1 ? new byte[] { 0, 1, 2, 3 } : new byte[] { 0, 1, 2, 4 }));
        Assert(Run(ctl.ReplaceFromDiskAsync(project, id, changing)).Code == "SourceChanged", "changed while read");
        Assert(Run(ctl.ReplaceFromDiskAsync(project, id, new DiskFile("y.bin", 4, () => throw new IOException("locked")))).Code == "FileUnreadable", "unreadable");
        Assert(Run(ctl.ReplaceFromDiskAsync(project, id, new DiskFile("z.bin", 9, () => new MemoryStream(new byte[4])))).Code == "FileUnreadable", "shorter than listed");
        Assert(!ctl.IsLocked, "disk failures don't lock the session");
        Assert(Run(ctl.ListHistoryAsync(project, id, 0)).Value!.Revisions.Count == 5, "nothing committed by the failures");
        Assert(Run(ctl.ReplaceFromDiskAsync(project, id, new DiskFile("huge.bin", EditorVaultController.MaxFileBytes + 1, () => Stream.Null))).Code == "LimitExceeded", "over 1 GiB");
    }
});

Test("controller: a text draft conflicting with a binary version is not mergeable; keep mine saves over theirs", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var ctl = Controller(pipe))
    {
        Run(ctl.UnlockAsync());
        var project = Run(ctl.CreateProjectAsync("Mixed")).Value!.ProjectId;
        var id = Run(ctl.CreateFileAsync(project, "app.rc", "IDS 1\n")).Value!.FileId;
        using (var other = Controller(pipe))
        {
            Run(other.UnlockAsync());
            Assert(Run(other.ReplaceFromDiskAsync(project, id, Disk("app.rc", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("IDS 2\r\n")).ToArray()))).Value!.Kind == "binary", "UTF-16 is stored as binary");
        }
        Assert(Run(ctl.SaveDocumentAsync(project, id, "IDS 3\n")).Code == "Conflict", "conflict");
        var merge = Run(ctl.MergeSourcesAsync(project, id)).Value!;
        Assert(!merge.Mergeable && merge.BaseText is null && merge.TheirsText is null, "no text merge with a binary side");
        Assert(Run(ctl.SaveMergedAsync(project, id, "IDS 3\n", merge.TheirsRevision)).Ok, "keep mine");
        Assert(Run(ctl.OpenDocumentAsync(project, id)).Value!.Text == "IDS 3\n", "mine is current");
    }
});

// ---- "Import a folder" and dropped files from the editor (owner request 2026-10-20) ----
DiskFile Scanned(string name, byte[] bytes) => new(name, bytes.Length, () => new MemoryStream(bytes, writable: false), SHA256.HashData(bytes));

Test("controller: importing a folder makes a verified new project; any failure removes it again", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var ctl = Controller(pipe))
    {
        Run(ctl.UnlockAsync());
        var icon = new byte[600 * 1024]; new Random(7).NextBytes(icon); icon[0] = 0;
        var files = new List<(string, DiskFile)> { ("README.md", Scanned("README.md", Encoding.UTF8.GetBytes("# imported\n"))), ("res/app.ico", Scanned("app.ico", icon)) };
        long last = 0;
        var imported = Run(ctl.ImportProjectAsync("From a folder", files, done => last = done));
        Assert(imported is { Ok: true, Value.Imported: 2, Value.Verified: 2 }, "imported and verified: " + imported.Code);
        Assert(last == icon.Length + 11, "progress reached every byte: " + last);
        var listed = Run(ctl.ListFilesAsync(imported.Value!.ProjectId)).Value!;
        Assert(listed.Single(f => f.Name == "res/app.ico").Sha256 == Convert.ToHexStringLower(SHA256.HashData(icon)), "byte-exact");
        Assert(Run(ctl.ImportProjectAsync("From a folder", files)).Code == "AlreadyExists", "a taken name");
        // A file that changed after the scan: refused at its last byte, and the new project is gone again.
        var changed = new List<(string, DiskFile)> { ("a.txt", Scanned("a.txt", Encoding.UTF8.GetBytes("one\n"))),
            ("b.txt", new DiskFile("b.txt", 4, () => new MemoryStream(Encoding.UTF8.GetBytes("two\n")), SHA256.HashData(Encoding.UTF8.GetBytes("TWO\n")))) };
        Assert(Run(ctl.ImportProjectAsync("Half imported", changed)).Code == "SourceChanged", "changed after the scan");
        Assert(Run(ctl.ListProjectsAsync()).Value!.All(p => p.Name != "Half imported"), "nothing half-imported is left");
    }
});

Test("controller: adding many dropped files commits in batches; a clash part-way keeps what was added", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var ctl = Controller(pipe))
    {
        Run(ctl.UnlockAsync());
        var project = Run(ctl.CreateProjectAsync("Dropped")).Value!.ProjectId;
        var many = Enumerable.Range(0, EditorVaultController.MaxAddFiles + 44).Select(i => ($"drop/f{i:D3}.txt", Scanned($"f{i:D3}.txt", Encoding.UTF8.GetBytes($"file {i}\n")))).ToList();
        var added = Run(ctl.AddManyAsync(project, many));
        Assert(added is { Ok: true, Value.Stopped: null } && added.Value.Added.Count == many.Count, "two commits, all added");
        Assert(Run(ctl.ListFilesAsync(project)).Value!.Count == many.Count, "listed");
        var clash = Enumerable.Range(0, EditorVaultController.MaxAddFiles).Select(i => ($"more/g{i:D3}.txt", Scanned($"g{i:D3}.txt", [1, 2, 3]))).Append(("drop/f000.txt", Scanned("f000.txt", [4]))).ToList();
        var partial = Run(ctl.AddManyAsync(project, clash));
        Assert(partial is { Ok: true, Value.Stopped: "AlreadyExists" } && partial.Value.Added.Count == EditorVaultController.MaxAddFiles, "the first batch stays; the clash stops the rest: " + partial.Value?.Stopped);
        Assert(Run(ctl.AddManyAsync(project, [("drop/f001.txt", Scanned("f001.txt", [5]))])).Code == "AlreadyExists", "nothing added: a plain failure");
    }
});

// ---- a project that fails its check is offline, not fatal (owner decision 2026-10-20) ----
Test("offline project: listed as unavailable, refuses everything but deletion; the status file has ids only", () =>
{
    var vault = Path.Combine(workspace, "vault-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(vault);
    var setup = new ProjectCatalog(vault, key);
    var good = setup.CreateProject("Healthy");
    var bad = setup.CreateProject("Broken project");
    var store = Path.Combine(vault, "projects", bad.ProjectId.ToString("D"));
    var live = Path.Combine(store, File.ReadAllText(Path.Combine(store, "HEAD")) + ".svr");
    var raw = File.ReadAllBytes(live); raw[^1] ^= 1; File.WriteAllBytes(live, raw);
    var catalog = new ProjectCatalog(vault, key);
    var check = catalog.Verify();
    Assert(check.Offline.Single().ProjectId == bad.ProjectId, "found at startup");
    var statusFolder = Path.Combine(workspace, "status-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(statusFolder);
    SourceVault.Service.VaultStatus.WriteTo(statusFolder, check.Projects.Count, check.Offline.Select(o => o.ProjectId), DateTimeOffset.UnixEpoch);
    var status = File.ReadAllText(Path.Combine(statusFolder, "vault.json"));
    Assert(status.Contains(bad.ProjectId.ToString("D")) && status.Contains("\"projectsAvailable\": 1") && !status.Contains("Broken") && !status.Contains("Healthy"), "ids and counts only: " + status);
    var reported = new List<Guid>();
    var name = "SourceVault.test." + Guid.NewGuid().ToString("N");
    using var server = new VaultPipeServer(name, me, new PipePeerPolicy(authenticatedUsers, [self]),
        new VaultProtocol(catalog, check.Projects, offline: check.Offline, status: (_, offline) => { reported.Clear(); reported.AddRange(offline); }));
    server.Start();
    using var c = new Client(name, me);
    c.Begin();
    var listing = c.Ok("ListProjects");
    Assert(listing.GetProperty("projects").EnumerateArray().Single().GetProperty("name").GetString() == "Healthy", "available projects");
    Assert(listing.GetProperty("unavailable").EnumerateArray().Single().GetProperty("projectId").GetString() == bad.ProjectId.ToString("D"), "offline projects are named, so they can be found and deleted");
    Assert(c.Code("ListFiles", ("projectId", bad.ProjectId.ToString("D"))) == "Unavailable", "nothing in it opens");
    Assert(c.Code("ListFiles", ("projectId", good.ProjectId.ToString("D"))) == "Ok", "the rest of the vault works");
    Assert(c.Code("DeleteProject", ("projectId", bad.ProjectId.ToString("D")), ("confirmName", "Broken project")) == "Ok", "an offline project can be deleted");
    Assert(reported.Count == 0, "the status file no longer lists it");
    Assert(!c.Ok("ListProjects").GetProperty("unavailable").EnumerateArray().Any(), "gone");
    using var ctl = Controller(name);
    Run(ctl.UnlockAsync());
    Assert(Run(ctl.ListProjectsAsync()).Value!.All(p => p.Available), "the editor's view");
});

Test("controller: unlock failures are reported as fixed, explainable codes", () =>
{
    foreach (var (failure, expected) in new (Exception, string)[]
    {
        (new IdentityNotMappedException(), "ServiceNotInstalled"), (new Win32Exception(2), "ServiceNotRunning"),
        (new Win32Exception(5), "AccessDenied"), (new Win32Exception(231), "Busy"),
        (new UnauthorizedAccessException(), "ServerUntrusted"), (new EndOfStreamException(), "ClientRejected"),
    })
    {
        using var ctl = new EditorVaultController(_ => Task.FromException<SourceVault.Client.VaultClient>(failure));
        var result = Run(ctl.UnlockAsync());
        Assert(result.Code == expected && ctl.IsLocked, $"{failure.GetType().Name} -> {result.Code}");
    }
    // A real rejection: the service closes without answering a program that is not an allowed client.
    var (server, pipe) = StartServer(new PipePeerPolicy(authenticatedUsers, [@"C:\Program Files\SourceVault\editor\SourceVault.Editor.exe"]));
    using (server)
    using (var ctl = Controller(pipe))
        Assert(Run(ctl.UnlockAsync()).Code == "ClientRejected", "executable check");
});

// ---- Import: ImportFiles over the pipe, and the SourceVault.Import tool end to end ----
string ImportBody(params (string Name, string Text)[] files) => "[" + string.Join(",", files.Select(f =>
    JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = f.Name, ["contentBase64"] = B64(f.Text) }))) + "]";
string ImportCode(Client c, string project, string snapshot, string filesJson) =>
    c.RawBody("ImportFiles", $"{{\"capability\":{JsonSerializer.Serialize(c.Capability)},\"projectId\":\"{project}\",\"expectedSnapshotId\":\"{snapshot}\",\"files\":{filesJson}}}");

Test("ImportFiles: batches of new files with folder paths; create-only; limits; no TPM unwraps when warm", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var c = new Client(pipe, me))
    {
        c.Begin();
        var created = c.Ok("CreateProject", ("name", "Imported"));
        var project = created.GetProperty("projectId").GetString()!;
        string s0 = created.GetProperty("snapshotId").GetString()!;
        key.Unwraps = 0;
        using var first = JsonDocument.Parse(ImportCode(c, project, s0, ImportBody(("src/app/main.ts", "export const a = 1;\n"), ("src/util.ts", "u"), ("README.md", "# r"))));
        Assert(first.RootElement.GetProperty("code").GetString() == "Ok" && first.RootElement.GetProperty("body").GetProperty("imported").GetInt32() == 3, "first batch");
        Assert(key.Unwraps == 1, "an import batch needs one unwrap (the project key): " + key.Unwraps);
        var s1 = first.RootElement.GetProperty("body").GetProperty("snapshotId").GetString()!;
        var names = c.Ok("ListFiles", ("projectId", project)).GetProperty("files").EnumerateArray().Select(f => f.GetProperty("name").GetString()).OrderBy(n => n).ToArray();
        Assert(names.SequenceEqual(new[] { "README.md", "src/app/main.ts", "src/util.ts" }), "paths kept");
        Assert(ImportCode(c, project, s1, ImportBody(("SRC/util.ts", "again"))).Contains("\"AlreadyExists\""), "existing name");
        Assert(ImportCode(c, project, s1, ImportBody(("src/app", "file where a folder is"))).Contains("\"AlreadyExists\""), "path clash");
        Assert(ImportCode(c, project, s1, ImportBody(("a.ts", "1"), ("A.ts", "2"))).Contains("\"InvalidRequest\""), "duplicate in batch");
        Assert(ImportCode(c, project, s1, ImportBody(("a/../b.ts", "1"))).Contains("\"InvalidRequest\""), "bad path");
        Assert(ImportCode(c, project, s0, ImportBody(("late.ts", "1"))).Contains("\"RevisionConflict\""), "stale snapshot");
        Assert(ImportCode(c, project, s1, ImportBody(Enumerable.Range(0, 257).Select(i => ($"f{i}.ts", "x")).ToArray())).Contains("\"LimitExceeded\""), "batch limit");
        Assert(c.Ok("ListFiles", ("projectId", project)).GetProperty("files").GetArrayLength() == 3, "refused batches wrote nothing");
    }
});

string NewSourceFolder()
{
    var root = Path.Combine(workspace, "source-" + Guid.NewGuid().ToString("N"));
    void Write(string relative, byte[] bytes) { var p = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllBytes(p, bytes); }
    Write(@"src\app\main.ts", Encoding.UTF8.GetBytes("export function main() { return 'synthetic'; }\n"));
    Write(@"src\util.ts", Encoding.UTF8.GetBytes("export const é = '✓ 𝄞';\n"));
    Write("README.md", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# Synthetic import\n")]); // UTF-8 BOM kept byte for byte
    Write(".gitignore", Encoding.UTF8.GetBytes("bin/\n"));
    Write(@"node_modules\left-pad\index.js", Encoding.UTF8.GetBytes("module.exports = 1;\n"));
    Write(@"bin\Debug\app.dll", [0x4D, 0x5A, 0x00, 0x01]);
    Write(@"assets\logo.png", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00]);
    Write("big.txt", Encoding.UTF8.GetBytes(new string('x', 262145)));
    Write("latin1.txt", [0x63, 0x61, 0x66, 0xE9]); // "café" in Latin-1: not UTF-8
    return root;
}

Test("importer: scan takes any file (binary, Latin-1, over 256 KiB) and explains every skip; links are not followed", () =>
{
    var root = NewSourceFolder();
    var junction = Path.Combine(root, "linked");
    var linked = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{Path.Combine(root, "src")}\"") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
    linked.WaitForExit();
    var plan = SourceVault.Import.ImportPlan.Scan(root);
    Assert(plan.Files.Select(f => f.Path).SequenceEqual(new[] { ".gitignore", "assets/logo.png", "big.txt", "latin1.txt", "README.md", "src/app/main.ts", "src/util.ts" }), "included: " + string.Join(",", plan.Files.Select(f => f.Path)));
    string Reason(string path) => plan.Skipped.Single(s => s.Path == path).Reason;
    Assert(Reason("node_modules/") == SourceVault.Import.ImportPlan.ExcludedFolder && Reason("bin/") == SourceVault.Import.ImportPlan.ExcludedFolder, "excluded folders");
    Assert(plan.Files.Single(f => f.Path == "big.txt").Length == 262145 && plan.LimitProblem is null, "large file and limits");
    if (Directory.Exists(junction)) Assert(Reason("linked") == SourceVault.Import.ImportPlan.Link, "junction not followed");
    var withExtra = SourceVault.Import.ImportPlan.Scan(root, ["src"]);
    Assert(withExtra.Files.Count == 5 && withExtra.Skipped.Any(s => s.Path == "src/"), "--exclude adds folders");
});

Test("importer: imports into a new project, verifies every file byte for byte, refuses an existing name", () =>
{
    var root = NewSourceFolder();
    var (server, pipe) = StartServer();
    using (server)
    {
        SourceVault.Client.VaultClient Connect() => new(VaultPipe.Connect(pipe, me, TimeSpan.FromSeconds(5)));
        var plan = SourceVault.Import.ImportPlan.Scan(root);
        var output = new StringWriter();
        Assert(SourceVault.Import.Importer.Run(plan, "Imported app", Connect, output) == 0, "import: " + output);
        Assert(output.ToString().Contains("all 7 match the originals"), "verified: " + output);
        Assert(!output.ToString().Contains("synthetic'") && !output.ToString().Contains("✓"), "no content printed");
        using (var c = new Client(pipe, me))
        {
            c.Begin();
            var project = c.Ok("ListProjects").GetProperty("projects").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Imported app");
            var files = c.Ok("ListFiles", ("projectId", project.GetProperty("projectId").GetString()!));
            var readme = files.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "README.md");
            var bytes = c.Ok("OpenDocument", ("projectId", project.GetProperty("projectId").GetString()!), ("fileId", readme.GetProperty("fileId").GetString()!),
                ("expectedSnapshotId", files.GetProperty("snapshotId").GetString()!)).GetProperty("contentBase64").GetBytesFromBase64();
            Assert(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "README.md"))), "BOM and bytes preserved");
        }
        var again = new StringWriter();
        Assert(SourceVault.Import.Importer.Run(plan, "imported APP", Connect, again) == 1 && again.ToString().Contains("already exists"), "existing project name");
        File.AppendAllText(Path.Combine(root, "src", "util.ts"), "// edited after the scan\n");
        try { SourceVault.Import.Importer.Run(plan, "Second import", Connect, new StringWriter()); throw new Exception("changed file accepted"); }
        catch (SourceVault.Import.ImportException e) { Assert(e.Message.Contains("changed after it was scanned"), "changed file"); }
        // A large file changed after the scan (same length) is refused by the service at its last byte.
        var plan2 = SourceVault.Import.ImportPlan.Scan(root);
        var big = Path.Combine(root, "big.txt"); var bigBytes = File.ReadAllBytes(big); bigBytes[^1] = (byte)'y'; File.WriteAllBytes(big, bigBytes);
        try { SourceVault.Import.Importer.Run(plan2, "Third import", Connect, new StringWriter()); throw new Exception("changed large file accepted"); }
        catch (SourceVault.Import.ImportException e) { Assert(e.Message.Contains("big.txt changed after it was scanned"), e.Message); }
    }
});

bool TryJunction(string link, string target)
{
    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
    p.WaitForExit();
    return p.ExitCode == 0 && Directory.Exists(link);
}
[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

Test("importer --delete-source: typed DELETE only; deletes unchanged imported files; keeps skipped, changed and hard-linked ones", () =>
{
    var root = NewSourceFolder();
    Directory.CreateDirectory(Path.Combine(root, ".git")); File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/main\n");
    File.WriteAllText(Path.Combine(root, "linked.ts"), "export const linked = 1;\n");
    var plan = SourceVault.Import.ImportPlan.Scan(root);
    var output = new StringWriter();
    Assert(SourceVault.Import.SourceCleanup.Run(plan, "P", () => "delete", output) == 0 && output.ToString().Contains("nothing was deleted"), "lowercase is not consent");
    Assert(SourceVault.Import.SourceCleanup.Run(plan, "P", () => null, new StringWriter()) == 0, "no input is not consent");
    Assert(plan.Files.All(f => File.Exists(f.FullPath)), "nothing deleted without consent");
    Assert(output.ToString().Contains(".git stays"), ".git warning");

    File.AppendAllText(Path.Combine(root, "src", "util.ts"), "// edited after the import\n");
    var hardLink = Path.Combine(workspace, "hardlink-" + Guid.NewGuid().ToString("N") + ".ts");
    bool linked = CreateHardLink(hardLink, Path.Combine(root, "linked.ts"), IntPtr.Zero);
    output = new StringWriter();
    int deleted = SourceVault.Import.SourceCleanup.Run(plan, "P", () => " DELETE ", output);
    var text = output.ToString();
    Assert(deleted == plan.Files.Count - 1 - (linked ? 1 : 0), "deleted count: " + text);
    Assert(File.Exists(Path.Combine(root, "src", "util.ts")) && text.Contains(SourceVault.Import.SourceCleanup.Changed), "changed file kept");
    Assert(!File.Exists(Path.Combine(root, "src", "app", "main.ts")) && !Directory.Exists(Path.Combine(root, "src", "app")), "emptied folder removed");
    Assert(Directory.Exists(Path.Combine(root, "src")), "folder with a kept file stays");
    if (linked) Assert(File.Exists(Path.Combine(root, "linked.ts")) && File.ReadAllText(hardLink) == "export const linked = 1;\n" && text.Contains(SourceVault.Import.SourceCleanup.HardLinked), "hard link untouched");
    foreach (var keep in new[] { @".git\HEAD", @"node_modules\left-pad\index.js", @"bin\Debug\app.dll" })
        Assert(File.Exists(Path.Combine(root, keep)), "skipped item kept: " + keep);
    foreach (var gone in new[] { @"assets\logo.png", "big.txt", "latin1.txt" }) // imported now (any file), so deleted like the rest
        Assert(!File.Exists(Path.Combine(root, gone)), "imported binary or large file deleted: " + gone);
    Assert(Directory.Exists(root), "root kept");
    Assert(!text.Contains("synthetic'") && !text.Contains("✓"), "no content printed");
});

Test("importer --delete-source: a folder swapped for a junction after the scan is not followed", () =>
{
    var root = NewSourceFolder();
    var plan = SourceVault.Import.ImportPlan.Scan(root);
    var elsewhere = Path.Combine(workspace, "elsewhere-" + Guid.NewGuid().ToString("N"));
    Directory.Move(Path.Combine(root, "src"), elsewhere);
    if (!TryJunction(Path.Combine(root, "src"), elsewhere)) { Console.WriteLine("SKIP junction swap: junction creation unavailable"); return; }
    var output = new StringWriter();
    SourceVault.Import.SourceCleanup.Run(plan, "P", () => "DELETE", output);
    Assert(File.Exists(Path.Combine(elsewhere, "util.ts")) && File.Exists(Path.Combine(elsewhere, "app", "main.ts")), "files behind the junction untouched");
    Assert(output.ToString().Contains(SourceVault.Import.SourceCleanup.LinkInPath), "reported");
    Assert(!File.Exists(Path.Combine(root, "README.md")), "other files still cleaned");
});

Test("importer: batches stay under the frame limit and 256 files", () =>
{
    var many = Enumerable.Range(0, 600).Select(i => new SourceVault.Import.ImportCandidate($"f{i}.ts", "", 2048, new byte[32])).ToList();
    var batches = SourceVault.Import.Importer.Batches(many).ToList();
    Assert(batches.Sum(b => b.Count) == 600 && batches.All(b => b.Count <= 256 && b.Sum(f => f.Length) <= SourceVault.Import.Importer.MaxBatchBytes), "batching");
    var big = Enumerable.Range(0, 5).Select(i => new SourceVault.Import.ImportCandidate($"b{i}.ts", "", 256 * 1024, new byte[32])).ToList();
    Assert(SourceVault.Import.Importer.Batches(big).All(b => b.Sum(f => f.Length) <= SourceVault.Import.Importer.MaxBatchBytes), "large files split");
});

bool Refused(Action action)
{
    try { action(); return false; }
    catch (UnauthorizedAccessException) { return true; }
}

Test("PID reuse: the client must still be running and older than the connection", () =>
{
    using var current = System.Diagnostics.Process.GetCurrentProcess();
    long created = VaultPipe.CheckProcessLifetime(current.SafeHandle, DateTime.UtcNow);
    Assert(created > 0 && created <= DateTime.UtcNow.ToFileTimeUtc(), "a running, older process passes");
    var createdUtc = DateTime.FromFileTimeUtc(created);
    Assert(Refused(() => VaultPipe.CheckProcessLifetime(current.SafeHandle, createdUtc.AddSeconds(-1))), "a process newer than the connection is a reused PID");
    using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
    child.WaitForExit();
    Assert(Refused(() => VaultPipe.CheckProcessLifetime(child.SafeHandle, DateTime.UtcNow)), "an exited process is refused");
});

// A synthetic install root: <root>\client\{Tool.exe, Tool.dll, sub\data.json} and a manifest in setup's format.
(string Root, string Exe) FakeInstall(Func<string, string, string>? editManifest = null)
{
    var root = Path.Combine(workspace, "install-" + Guid.NewGuid().ToString("N"));
    var client = Path.Combine(root, "client");
    Directory.CreateDirectory(Path.Combine(client, "sub"));
    File.WriteAllText(Path.Combine(client, "Tool.exe"), "synthetic apphost");
    File.WriteAllText(Path.Combine(client, "Tool.dll"), "synthetic app code");
    File.WriteAllText(Path.Combine(client, "sub", "data.json"), "{}");
    Directory.CreateDirectory(Path.Combine(root, "service"));
    File.WriteAllText(Path.Combine(root, "service", "other.dll"), "not pinned for clients");
    var lines = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)
        .Select(f => $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}  {f[(root.Length + 1)..]}");
    var text = string.Join("\r\n", lines) + "\r\n";
    File.WriteAllText(Path.Combine(root, ClientPins.ManifestName), editManifest is null ? text : editManifest(text, root), new UTF8Encoding(false));
    return (root, Path.Combine(client, "Tool.exe"));
}

Test("client pins: an untouched client folder passes", () =>
{
    var (root, exe) = FakeInstall();
    new ClientPins(root, ["client"]).Verify(exe);
    new PipePeerPolicy(authenticatedUsers, [exe], new ClientPins(root + "\\", ["client"])).ValidateInstalledFiles(exe); // trailing separator is fine
});

Test("client pins: a changed, extra or missing file is refused", () =>
{
    var (root, exe) = FakeInstall();
    var pins = new ClientPins(root, ["client"]);
    File.AppendAllText(Path.Combine(root, "client", "Tool.dll"), "!");
    Assert(Refused(() => pins.Verify(exe)), "changed file");
    (root, exe) = FakeInstall(); pins = new ClientPins(root, ["client"]);
    File.WriteAllText(Path.Combine(root, "client", "sub", "planted.dll"), "x");
    Assert(Refused(() => pins.Verify(exe)), "extra file");
    (root, exe) = FakeInstall(); pins = new ClientPins(root, ["client"]);
    File.Delete(Path.Combine(root, "client", "sub", "data.json"));
    Assert(Refused(() => pins.Verify(exe)), "missing file");
});

Test("client pins: an update that rewrites the manifest is honoured without a restart", () =>
{
    var (root, exe) = FakeInstall();
    var pins = new ClientPins(root, ["client"]);
    var dll = Path.Combine(root, "client", "Tool.dll");
    File.WriteAllText(dll, "updated synthetic app code");
    Assert(Refused(() => pins.Verify(exe)), "stale manifest refuses the new file");
    var manifest = Path.Combine(root, ClientPins.ManifestName);
    var lines = File.ReadAllLines(manifest).Select(l => l.EndsWith(@"  client\Tool.dll", StringComparison.Ordinal) ? $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll)))}  client\\Tool.dll" : l);
    File.WriteAllLines(manifest, lines);
    pins.Verify(exe);
});

Test("client pins fail closed: no manifest, a malformed manifest, an unpinned folder, a path outside the root", () =>
{
    var (root, exe) = FakeInstall();
    File.Delete(Path.Combine(root, ClientPins.ManifestName));
    Assert(Refused(() => new ClientPins(root, ["client"]).Verify(exe)), "no manifest");
    foreach (Func<string, string, string> bad in new Func<string, string, string>[]
    {
        (t, _) => t + "zz" + t[2..66] + "client\\x\r\n",                 // non-hex hash
        (t, _) => t + t.Split("\r\n")[0] + "\r\n",                         // duplicate line
        (t, _) => t + new string('0', 64) + "  client\\..\\service\\x\r\n", // dot segment
        (t, r) => t + new string('0', 64) + "  " + r + "\\client\\abs\r\n", // rooted path
        (t, _) => t.Replace("  ", " ", StringComparison.Ordinal),           // wrong separator
    })
    {
        var (r2, e2) = FakeInstall(bad);
        Assert(Refused(() => new ClientPins(r2, ["client"]).Verify(e2)), "malformed manifest rejects everything");
    }
    var (r3, e3) = FakeInstall();
    Assert(Refused(() => new ClientPins(r3, ["editor"]).Verify(e3)), "folder not pinned");
    Assert(Refused(() => new ClientPins(r3, ["client"]).Verify(Path.Combine(r3, "client", "sub", "data.json"))), "image must sit directly in a pinned folder");
    Assert(Refused(() => new ClientPins(r3, ["client"]).Verify(Path.Combine(workspace, "client", "Tool.exe"))), "outside the install root");
});

Test("client pins: a link inside the client folder is refused", () =>
{
    var (root, exe) = FakeInstall();
    var pins = new ClientPins(root, ["client"]);
    var target = Path.Combine(workspace, "link-target-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(target);
    if (!TryJunction(Path.Combine(root, "client", "linked"), target)) { Console.WriteLine("SKIP client pins link case: junction creation unavailable"); return; }
    Assert(Refused(() => pins.Verify(exe)), "junction refused");
});

// ---- encrypted packages from another computer (docs/package-import-design.md) -------------------------------------
// Test-only software keys stand in for the vault PC's TPM import key and the source computer's TPM sender key.
using var importRsa = RSA.Create(2048);
using var senderRsa = RSA.Create(2048);
using var otherImportRsa = RSA.Create(2048); // a second vault PC's import key
var senderFile = SourceVault.Packaging.PublicKeyFile.Create(SourceVault.Packaging.PublicKeyFile.SenderFormat, "DEV-PC", senderRsa.ExportSubjectPublicKeyInfo());
byte[] SenderSign(byte[] hash) => senderRsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

(VaultPipeServer Server, string Pipe, PackageImports Packages) StartPackageServer(bool registerSender = true, RSA? import = null)
{
    var vault = Path.Combine(workspace, "vault-" + Guid.NewGuid().ToString("N"));
    var senders = Path.Combine(workspace, "senders-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(vault); Directory.CreateDirectory(senders);
    if (registerSender) File.WriteAllBytes(Path.Combine(senders, Convert.ToHexStringLower(senderFile.Fingerprint) + ".json"), senderFile.ToJson());
    var catalog = new ProjectCatalog(vault, key);
    import ??= importRsa;
    var packages = new PackageImports(new RsaTestKey(import), import.ExportSubjectPublicKeyInfo(), "TEST-VAULT", senders);
    var name = "SourceVault.test." + Guid.NewGuid().ToString("N");
    var server = new VaultPipeServer(name, me, new PipePeerPolicy(authenticatedUsers, [self]), new VaultProtocol(catalog, catalog.OpenAndVerify(), packages: packages));
    server.Start();
    return (server, name, packages);
}
byte[] MakePackage(PackageImports packages, IReadOnlyList<(string, byte[])> files)
{
    using var output = new MemoryStream();
    SourceVault.Packaging.Package.Write(output, "Fictional", files, packages.Recipient, senderFile, SenderSign);
    return output.ToArray();
}
List<(string, byte[])> PackageFiles() => [("README.md", Encoding.UTF8.GetBytes("# Fictional package\n")), ("src/main.cs", Encoding.UTF8.GetBytes("class Synthetic { }\n"))];
string Upload(Client c, byte[] package, string project)
{
    var code = c.Code("BeginPackageImport", ("length", package.Length));
    if (code != "Ok") return code;
    for (int offset = 0; offset < package.Length; offset += 512 * 1024)
    {
        code = c.Code("PackageImportPart", ("offset", offset), ("dataBase64", Convert.ToBase64String(package, offset, Math.Min(512 * 1024, package.Length - offset))));
        if (code != "Ok") return code;
    }
    return c.Code("FinishPackageImport", ("projectName", project));
}

Test("package import: a registered computer's package becomes a new project with the same files", () =>
{
    var (server, pipe, packages) = StartPackageServer();
    using (server)
    using (var c = new Client(pipe, me))
    {
        c.Begin();
        var recipient = c.Ok("GetImportRecipient");
        Assert(recipient.GetProperty("fingerprint").GetString() == Convert.ToHexStringLower(packages.Recipient.Fingerprint) && recipient.GetProperty("computer").GetString() == "TEST-VAULT", "recipient");
        var files = Enumerable.Range(0, 6).Select(i => ($"big/part{i}.txt", Encoding.UTF8.GetBytes(new string((char)('a' + i), 200 * 1024)))).Concat(PackageFiles()).ToList();
        var package = MakePackage(packages, files); // about 1.2 MB: several upload parts and two chunks
        Assert(Upload(c, package, "From DEV-PC") == "Ok", "imported");
        var project = c.Ok("ListProjects").GetProperty("projects").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "From DEV-PC");
        var id = project.GetProperty("projectId").GetString()!;
        var listing = c.Ok("ListFiles", ("projectId", id));
        Assert(listing.GetProperty("files").GetArrayLength() == 8, "all files");
        var main = listing.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "src/main.cs");
        var opened = c.Ok("OpenDocument", ("projectId", id), ("fileId", main.GetProperty("fileId").GetString()!), ("expectedSnapshotId", listing.GetProperty("snapshotId").GetString()!));
        Assert(Encoding.UTF8.GetString(opened.GetProperty("contentBase64").GetBytesFromBase64()) == "class Synthetic { }\n", "content");
        var history = c.Ok("ListHistory", ("projectId", id), ("fileId", main.GetProperty("fileId").GetString()!), ("page", 0));
        Assert(history.GetProperty("revisions")[0].GetProperty("action").GetString() == "imported", "history says imported");
    }
});

Test("package import refusals: unregistered sender, altered, another recipient, taken name, path clash; nothing is left behind", () =>
{
    var (server, pipe, packages) = StartPackageServer();
    var (otherServer, otherPipe, otherPackages) = StartPackageServer(registerSender: false, import: otherImportRsa);
    using (server) using (otherServer)
    using (var c = new Client(pipe, me))
    using (var o = new Client(otherPipe, me))
    {
        c.Begin(); o.Begin();
        Assert(Upload(o, MakePackage(otherPackages, PackageFiles()), "Stranger") == "UnknownSender", "unregistered sender");
        var altered = MakePackage(packages, PackageFiles()); altered[Package.HeaderBytes + 3] ^= 1;
        Assert(Upload(c, altered, "Altered") == "PackageRejected", "altered");
        Assert(Upload(c, MakePackage(otherPackages, PackageFiles()), "Elsewhere") == "PackageRejected", "made for another recipient");
        Assert(Upload(c, MakePackage(packages, [("a", Encoding.UTF8.GetBytes("x")), ("a/b", Encoding.UTF8.GetBytes("y"))]), "Clash") == "PackageRejected", "file and folder at one path");
        Assert(Upload(c, MakePackage(packages, PackageFiles()), " bad name") == "InvalidRequest", "project name");
        Assert(c.Ok("ListProjects").GetProperty("projects").GetArrayLength() == 0 && o.Ok("ListProjects").GetProperty("projects").GetArrayLength() == 0, "no project left behind by any refusal");
        Assert(Upload(c, MakePackage(packages, PackageFiles()), "Taken") == "Ok", "first import");
        Assert(Upload(c, MakePackage(packages, PackageFiles()), "Taken") == "AlreadyExists", "taken name");
    }
});

Test("package import uploads: parts in order only, one upload at a time, freed when a connection closes; Unavailable without an import key", () =>
{
    var (server, pipe, packages) = StartPackageServer();
    using (server)
    {
        var package = MakePackage(packages, PackageFiles());
        using (var c = new Client(pipe, me))
        {
            c.Begin();
            Assert(c.Code("PackageImportPart", ("offset", 0), ("dataBase64", "AAAA")) == "InvalidRequest", "part before begin");
            Assert(c.Code("FinishPackageImport", ("projectName", "X")) == "InvalidRequest", "finish before begin");
            Assert(c.Code("BeginPackageImport", ("length", Package.MaxPackageBytes + 1)) == "LimitExceeded", "too large");
            Assert(c.Code("BeginPackageImport", ("length", package.Length)) == "Ok", "begin");
            Assert(c.Code("BeginPackageImport", ("length", package.Length)) == "InvalidRequest", "one upload per connection");
            using (var d = new Client(pipe, me))
            {
                d.Begin();
                Assert(d.Code("BeginPackageImport", ("length", package.Length)) == "Unavailable", "one upload at a time across connections");
            }
            Assert(c.Code("PackageImportPart", ("offset", 10), ("dataBase64", "AAAA")) == "InvalidRequest", "gap");
            Assert(c.Code("PackageImportPart", ("offset", 0), ("dataBase64", Convert.ToBase64String(new byte[package.Length + 1]))) == "LimitExceeded", "past the end");
            Assert(c.Code("PackageImportPart", ("offset", 0), ("dataBase64", Convert.ToBase64String(package, 0, 100))) == "Ok", "a first part");
            Assert(c.Code("FinishPackageImport", ("projectName", "X")) == "InvalidRequest", "finish before all bytes");
            Assert(c.Code("PackageImportPart", ("offset", 100), ("dataBase64", "AAAA")) == "InvalidRequest", "a refused finish ends the upload");
            Assert(c.Code("BeginPackageImport", ("length", package.Length)) == "Ok", "a new upload can start after it ended");
            c.Code("EndSession"); // closes with an unfinished upload: the slot must be freed
        }
        Thread.Sleep(200); // the server closes the connection and frees the slot
        using (var e = new Client(pipe, me)) { e.Begin(); Assert(Upload(e, package, "After close") == "Ok", "slot freed by the closed connection"); }
    }
    var (plain, plainPipe) = StartServer();
    using (plain)
    using (var c = new Client(plainPipe, me))
    {
        c.Begin();
        Assert(c.Code("GetImportRecipient") == "Unavailable" && c.Code("BeginPackageImport", ("length", 1000)) == "Unavailable", "no import key");
    }
});

Test("importer --export-recipient and --package end to end; it prints names and fingerprints only", () =>
{
    var (server, pipe, packages) = StartPackageServer();
    using (server)
    {
        SourceVault.Client.VaultClient Connect() => new(VaultPipe.Connect(pipe, me, TimeSpan.FromSeconds(5)));
        var recipientPath = Path.Combine(workspace, "recipient-" + Guid.NewGuid().ToString("N") + ".json");
        var exported = new StringWriter();
        Assert(SourceVault.Import.PackageImport.ExportRecipient(recipientPath, Connect, exported) == 0, "export: " + exported);
        var recipient = SourceVault.Packaging.PublicKeyFile.Parse(File.ReadAllBytes(recipientPath), SourceVault.Packaging.PublicKeyFile.RecipientFormat);
        Assert(recipient.Fingerprint.AsSpan().SequenceEqual(packages.Recipient.Fingerprint) && exported.ToString().Contains(recipient.FingerprintText), "the file and the printed fingerprint match the service");
        try { SourceVault.Import.PackageImport.ExportRecipient(recipientPath, Connect, new StringWriter()); throw new Exception("overwrote"); } catch (SourceVault.Import.ImportException) { }
        var packagePath = Path.Combine(workspace, "package-" + Guid.NewGuid().ToString("N") + ".svpkg");
        using (var output = File.Create(packagePath)) Package.Write(output, "Fictional", PackageFiles(), recipient, senderFile, SenderSign);
        var imported = new StringWriter();
        Assert(SourceVault.Import.PackageImport.Run(packagePath, "Via importer", Connect, imported) == 0, "import: " + imported);
        Assert(imported.ToString().Contains("Imported 2 file(s)") && imported.ToString().Contains("\"DEV-PC\""), "report");
        var again = new StringWriter();
        Assert(SourceVault.Import.PackageImport.Run(packagePath, "Via importer", Connect, again) == 1 && again.ToString().Contains("already exists"), "taken name explained");
        foreach (var text in new[] { exported.ToString(), imported.ToString(), again.ToString() })
            Assert(!text.Contains("Fictional package") && !text.Contains("class Synthetic"), "no content printed");
    }
});

Test("SourceVault Pack: scan, recipient check and confirmation, then a package the service imports; dry run writes nothing", () =>
{
    var (server, pipe, packages) = StartPackageServer();
    using (server)
    {
        var source = Path.Combine(workspace, "pack-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(source, "src"));
        Directory.CreateDirectory(Path.Combine(source, ".git"));
        File.WriteAllText(Path.Combine(source, "README.md"), "# Fictional pack source\n");
        File.WriteAllText(Path.Combine(source, "src", "app.cs"), "class FictionalPack { }\n");
        File.WriteAllBytes(Path.Combine(source, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0x00]);
        File.WriteAllText(Path.Combine(source, ".git", "config"), "[core]\n");
        var recipientPath = Path.Combine(workspace, "pack-recipient-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(recipientPath, packages.Recipient.ToJson());
        var outPath = Path.Combine(workspace, "pack-" + Guid.NewGuid().ToString("N") + ".svpkg");
        pack::SourceVault.Pack.IPackageSigner Signer() => new SoftwareSigner(senderRsa);

        var dry = new StringWriter();
        Assert(pack::SourceVault.Pack.Commands.Pack([source, "--to", recipientPath, "--dry-run"], dry, () => throw new Exception("no prompt in a dry run"), Signer) == 0, "dry run: " + dry);
        Assert(dry.ToString().Contains("3 file(s) to pack") && dry.ToString().Contains(".git/") && dry.ToString().Contains(packages.Recipient.FingerprintText), "plan and fingerprint shown: " + dry);
        Assert(!File.Exists(outPath), "dry run wrote nothing");

        var declined = new StringWriter();
        Assert(pack::SourceVault.Pack.Commands.Pack([source, "--to", recipientPath, "--out", outPath], declined, () => "no", Signer) == 1 && !File.Exists(outPath), "anything but YES writes nothing");

        var packed = new StringWriter();
        Assert(pack::SourceVault.Pack.Commands.Pack([source, "--to", recipientPath, "--out", outPath, "--name", "Fictional"], packed, () => "YES", Signer) == 0, "pack: " + packed);
        try { pack::SourceVault.Pack.Commands.Pack([source, "--to", recipientPath, "--out", outPath, "--yes"], new StringWriter(), () => null, Signer); throw new Exception("overwrote"); }
        catch (pack::SourceVault.Pack.PackException) { }
        foreach (var text in new[] { dry.ToString(), packed.ToString() })
            Assert(!text.Contains("Fictional pack source") && !text.Contains("class FictionalPack"), "Pack prints no content");

        SourceVault.Client.VaultClient Connect() => new(VaultPipe.Connect(pipe, me, TimeSpan.FromSeconds(5)));
        var imported = new StringWriter();
        Assert(SourceVault.Import.PackageImport.Run(outPath, "Packed elsewhere", Connect, imported) == 0 && imported.ToString().Contains("Imported 3 file(s)"), "service imports what Pack wrote: " + imported);
        using var c = new Client(pipe, me);
        c.Begin();
        var id = c.Ok("ListProjects").GetProperty("projects").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Packed elsewhere").GetProperty("projectId").GetString()!;
        var listed = c.Ok("ListFiles", ("projectId", id)).GetProperty("files").EnumerateArray().ToList();
        Assert(listed.Select(f => f.GetProperty("name").GetString()).Order(StringComparer.Ordinal).SequenceEqual(["README.md", "logo.png", "src/app.cs"]), "every file, without .git");
        var logo = listed.Single(f => f.GetProperty("name").GetString() == "logo.png");
        Assert(logo.GetProperty("kind").GetString() == "binary" && logo.GetProperty("sha256").GetString() == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, "logo.png")))), "the binary arrives byte-exact");
    }
});

Test("SearchProject: plain-text matches with line, column and preview; case, whole word, the 500 limit and bad queries", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var c = new Client(pipe, me))
    {
        c.Begin();
        var created = c.Ok("CreateProject", ("name", "Search"));
        var project = created.GetProperty("projectId").GetString()!;
        var snap = created.GetProperty("snapshotId").GetString()!;
        string Create(string name, string text) { var r = c.Ok("CreateFile", ("projectId", project), ("name", name), ("expectedSnapshotId", snap), ("contentBase64", B64(text))); snap = r.GetProperty("snapshotId").GetString()!; return r.GetProperty("fileId").GetString()!; }
        Create("src/b.cs", "class Widget {}\r\n    var widgetCount = 1; // Widget\r\n");
        Create("a.ts", "export const widget = 2;\n");
        Create("many.txt", string.Concat(Enumerable.Repeat("x x x x x x x x x x\n", 60))); // 600 matches of "x"
        Create("long.txt", new string('a', 300) + "needle\n");
        JsonElement Search(string query, bool matchCase = false, bool wholeWord = false) =>
            c.Ok("SearchProject", ("projectId", project), ("query", query), ("matchCase", matchCase), ("wholeWord", wholeWord));

        var all = Search("widget");
        var hits = all.GetProperty("matches").EnumerateArray().Select(m => $"{m.GetProperty("name").GetString()}:{m.GetProperty("line").GetInt32()}:{m.GetProperty("column").GetInt32()}").ToArray();
        Assert(hits.SequenceEqual(["a.ts:1:14", "src/b.cs:1:7", "src/b.cs:2:9", "src/b.cs:2:29"]), "case-insensitive, sorted by file then line: " + string.Join(", ", hits));
        Assert(all.GetProperty("matches")[2].GetProperty("preview").GetString() == "var widgetCount = 1; // Widget", "preview trims leading space and the CR");
        Assert(all.GetProperty("matches")[2].GetProperty("previewOffset").GetInt32() == 4 && all.GetProperty("matches")[3].GetProperty("previewOffset").GetInt32() == 24, "preview offsets point at each match");
        Assert(!all.GetProperty("truncated").GetBoolean() && all.GetProperty("filesSearched").GetInt32() == 4, "searched every file");
        var far = Search("needle").GetProperty("matches")[0];
        var farPreview = far.GetProperty("preview").GetString()!;
        Assert(farPreview.Length == 241 && farPreview[0] == '…' && farPreview.Substring(far.GetProperty("previewOffset").GetInt32(), 6) == "needle", "a long line is cut around the match: " + farPreview.Length);
        Assert(Search("Widget", matchCase: true).GetProperty("matches").GetArrayLength() == 2, "match case");
        Assert(Search("widget", wholeWord: true).GetProperty("matches").GetArrayLength() == 3, "whole word excludes widgetCount");
        Assert(Search("nothing-here").GetProperty("matches").GetArrayLength() == 0, "no match");
        var many = Search("x");
        Assert(many.GetProperty("matches").GetArrayLength() == 500 && many.GetProperty("truncated").GetBoolean(), "at most 500 matches, reported as truncated");
        foreach (var bad in new object[] { "", new string('q', 201), "two\nlines" })
            Assert(c.Code("SearchProject", ("projectId", project), ("query", bad), ("matchCase", false), ("wholeWord", false)) == "InvalidRequest", "bad query refused");
        Assert(c.Code("SearchProject", ("projectId", project), ("query", "x"), ("matchCase", "yes"), ("wholeWord", false)) == "InvalidRequest", "flags must be booleans");
        Assert(c.Code("SearchProject", ("projectId", Guid.NewGuid().ToString("D")), ("query", "x"), ("matchCase", false), ("wholeWord", false)) == "NotFound", "unknown project");
    }
});

// ---- Any file (docs/vault-any-file-design.md section 3): uploads in parts, NotText, text in parts, revert, verify ----
string Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
(string UploadId, string FileId) UploadFile(Client c, string project, string? fileId, byte[] data, string? sha = null)
{
    var begun = c.Ok("BeginUpload", ("projectId", project), ("fileId", (object)fileId!), ("length", data.LongLength), ("sha256", sha ?? Hex(data)));
    Assert(begun.GetProperty("partBytes").GetInt32() == 524288, "part size");
    var id = begun.GetProperty("uploadId").GetString()!;
    for (int at = 0; at < data.Length; at += 524288)
    {
        var part = data.AsSpan(at, Math.Min(524288, data.Length - at)).ToArray();
        var code = c.Code("UploadPart", ("uploadId", id), ("offset", (long)at), ("dataBase64", Convert.ToBase64String(part)));
        if (code != "Ok") return (code, "");
    }
    return (id, begun.GetProperty("fileId").GetString()!);
}
JsonElement CommitUploads(Client c, string project, string snapshot, params (string UploadId, string? Name)[] files) =>
    c.Ok("CommitUploads", ("projectId", project), ("expectedSnapshotId", snapshot),
        ("files", files.Select(f => new Dictionary<string, object?> { ["uploadId"] = f.UploadId, ["name"] = f.Name }).ToArray()));
JsonElement FileEntry(Client c, string project, string fileId) =>
    c.Ok("ListFiles", ("projectId", project)).GetProperty("files").EnumerateArray().Single(f => f.GetProperty("fileId").GetString() == fileId);

Test("any file: binary and large uploads in parts, NotText, text in parts, replacement, wrong hash, conflict keeps the upload", () =>
{
    var (server, pipe) = StartServer();
    using (server)
    using (var c = new Client(pipe, me))
    {
        c.Begin();
        var created = c.Ok("CreateProject", ("name", "AnyFile"));
        var project = created.GetProperty("projectId").GetString()!;
        string s = created.GetProperty("snapshotId").GetString()!;
        var blob = new byte[700 * 1024]; new Random(7).NextBytes(blob); blob[0] = 0; // binary, two parts
        var (blobUpload, blobId) = UploadFile(c, project, null, blob);
        var committed = CommitUploads(c, project, s, (blobUpload, "lib/blob.bin"));
        s = committed.GetProperty("snapshotId").GetString()!;
        Assert(committed.GetProperty("files")[0].GetProperty("kind").GetString() == "binary", "store decides binary");
        var entry = FileEntry(c, project, blobId);
        Assert(entry.GetProperty("kind").GetString() == "binary" && entry.GetProperty("sha256").GetString() == Hex(blob) && entry.GetProperty("byteLength").GetInt64() == blob.Length, "listing kind and hash");
        var firstRevision = entry.GetProperty("revisionId").GetString()!;
        Assert(c.Code("OpenDocument", ("projectId", project), ("fileId", blobId), ("expectedSnapshotId", s)) == "NotText", "binary never opens inline");
        Assert(c.Code("ReadTextPart", ("projectId", project), ("fileId", blobId), ("revisionId", firstRevision), ("offset", 0L)) == "NotText", "no binary in parts either");

        // Large text: LimitExceeded inline, readable in 512 KiB parts up to the editor's 1 MiB.
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("synthetic line λ\n", 600 * 1024 / 18)));
        var (textUpload, textId) = UploadFile(c, project, null, text);
        s = CommitUploads(c, project, s, (textUpload, "src/big.txt")).GetProperty("snapshotId").GetString()!;
        var textRevision = FileEntry(c, project, textId).GetProperty("revisionId").GetString()!;
        Assert(c.Code("OpenDocument", ("projectId", project), ("fileId", textId), ("expectedSnapshotId", s)) == "LimitExceeded", "large text not inline");
        var part0 = c.Ok("ReadTextPart", ("projectId", project), ("fileId", textId), ("revisionId", textRevision), ("offset", 0L));
        var part1 = c.Ok("ReadTextPart", ("projectId", project), ("fileId", textId), ("revisionId", textRevision), ("offset", 524288L));
        Assert(part0.GetProperty("length").GetInt64() == text.Length, "whole length");
        Assert(part0.GetProperty("dataBase64").GetBytesFromBase64().Concat(part1.GetProperty("dataBase64").GetBytesFromBase64()).SequenceEqual(text), "parts reassemble");
        Assert(c.Code("ReadTextPart", ("projectId", project), ("fileId", textId), ("revisionId", textRevision), ("offset", 1L)) == "InvalidRequest", "part-aligned offsets only");
        Assert(c.Code("ReadTextPart", ("projectId", project), ("fileId", textId), ("revisionId", textRevision), ("offset", 2L * 524288)) == "InvalidRequest", "past the end");

        // Replacement keeps the file id and name and adds to its history; a name on a replacement is refused.
        var blob2 = new byte[] { 0, 1, 2, 3 };
        var (replace, sameId) = UploadFile(c, project, blobId, blob2);
        Assert(sameId == blobId, "replacement keeps the id");
        Assert(c.Code("CommitUploads", ("projectId", project), ("expectedSnapshotId", s), ("files", new[] { new Dictionary<string, object?> { ["uploadId"] = replace, ["name"] = "x.bin" } })) == "InvalidRequest", "replacement keeps its name");
        s = CommitUploads(c, project, s, (replace, null)).GetProperty("snapshotId").GetString()!;
        var history = c.Ok("ListHistory", ("projectId", project), ("fileId", blobId), ("page", 0)).GetProperty("revisions");
        Assert(history.GetArrayLength() == 2 && history[0].GetProperty("action").GetString() == "edited" && history[1].GetProperty("kind").GetString() == "binary", "history of a binary file");
        Assert(FileEntry(c, project, blobId).GetProperty("name").GetString() == "lib/blob.bin", "name kept");

        // A wrong SHA-256 is refused at the last byte and the upload is gone.
        var (bad, _) = UploadFile(c, project, null, [1, 2, 3], sha: new string('0', 64));
        Assert(bad == "InvalidRequest", "wrong hash");
        // A stale snapshot conflicts and keeps the staged upload; the retry needs no second upload.
        var (kept, _) = UploadFile(c, project, null, [9, 0, 7]); // a NUL byte: binary
        Assert(c.Code("CommitUploads", ("projectId", project), ("expectedSnapshotId", created.GetProperty("snapshotId").GetString()!),
            ("files", new[] { new Dictionary<string, object?> { ["uploadId"] = kept, ["name"] = "k.bin" } })) == "RevisionConflict", "conflict");
        s = CommitUploads(c, project, s, (kept, "k.bin")).GetProperty("snapshotId").GetString()!;
        // A taken name is AlreadyExists (create-only).
        var (dup, _) = UploadFile(c, project, null, [5]);
        Assert(c.Code("CommitUploads", ("projectId", project), ("expectedSnapshotId", s), ("files", new[] { new Dictionary<string, object?> { ["uploadId"] = dup, ["name"] = "K.BIN" } })) == "AlreadyExists", "create-only");
        Assert(c.Code("CancelUpload", ("uploadId", dup)) == "Ok" && c.Code("CancelUpload", ("uploadId", dup)) == "InvalidRequest", "cancel once");

        // Revert makes the first binary revision current again; verify decrypts every file.
        s = c.Ok("RevertFile", ("projectId", project), ("fileId", blobId), ("revisionId", firstRevision), ("expectedSnapshotId", s)).GetProperty("snapshotId").GetString()!;
        Assert(FileEntry(c, project, blobId).GetProperty("revisionId").GetString() == firstRevision, "reverted");
        Assert(c.Ok("ListHistory", ("projectId", project), ("fileId", blobId), ("page", 0)).GetProperty("revisions")[0].GetProperty("action").GetString() == "reverted", "history says reverted");
        Assert(c.Code("RevertFile", ("projectId", project), ("fileId", blobId), ("revisionId", Guid.NewGuid().ToString("D")), ("expectedSnapshotId", s)) == "NotFound", "unknown revision");
        var verify = c.Ok("VerifyProject", ("projectId", project));
        Assert(verify.GetProperty("filesVerified").GetInt32() == 3 && verify.GetProperty("filesFailed").GetInt32() == 0, "verify");

        // Inline: CreateFile takes any bytes; ApplyEdits only text; search skips binary files and says so.
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var inline = c.Ok("CreateFile", ("projectId", project), ("name", "inline.bin"), ("expectedSnapshotId", s), ("contentBase64", Convert.ToBase64String(bytes)));
        s = inline.GetProperty("snapshotId").GetString()!;
        Assert(FileEntry(c, project, inline.GetProperty("fileId").GetString()!).GetProperty("kind").GetString() == "binary", "inline binary");
        Assert(c.Code("ApplyEdits", ("projectId", project), ("fileId", textId), ("expectedSnapshotId", s), ("contentBase64", Convert.ToBase64String(bytes))) == "InvalidRequest", "edits are text");
        var search = c.Ok("SearchProject", ("projectId", project), ("query", "synthetic line"), ("matchCase", false), ("wholeWord", false));
        Assert(search.GetProperty("filesSkipped").GetInt32() == 3 && search.GetProperty("filesSearched").GetInt32() == 1 && search.GetProperty("matches").GetArrayLength() > 0, "search skips binaries");

        // One receiving upload per connection.
        var begun = c.Ok("BeginUpload", ("projectId", project), ("fileId", (object)null!), ("length", 10L), ("sha256", Hex(new byte[10])));
        Assert(c.Code("BeginUpload", ("projectId", project), ("fileId", (object)null!), ("length", 10L), ("sha256", Hex(new byte[10]))) == "InvalidRequest", "one receiving at a time");
        Assert(c.Code("BeginUpload", ("projectId", project), ("fileId", (object)null!), ("length", (1L << 30) + 1), ("sha256", Hex(new byte[10]))) is "InvalidRequest" or "LimitExceeded", "1 GiB limit");
        Assert(c.Code("UploadPart", ("uploadId", begun.GetProperty("uploadId").GetString()!), ("offset", 0L), ("dataBase64", Convert.ToBase64String(new byte[11]))) == "LimitExceeded", "past the length");
    }
    // Closing the connection discards its uploads: nothing is left in staging.
    Thread.Sleep(200);
    Assert(!Directory.EnumerateFiles(Path.Combine(lastVault, "staging"), "*", SearchOption.AllDirectories).Any(), "staging emptied on close");
});

Test("any file: an upload idle for 2 minutes is discarded; the vault root accepts staging\\", () =>
{
    var clock = new ManualClock();
    var (server, pipe) = StartServer(clock: clock);
    using (server)
    using (var c = new Client(pipe, me))
    {
        c.Begin();
        var created = c.Ok("CreateProject", ("name", "Idle"));
        var project = created.GetProperty("projectId").GetString()!;
        var begun = c.Ok("BeginUpload", ("projectId", project), ("fileId", (object)null!), ("length", 4L), ("sha256", Hex([1, 2, 3, 4])));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert(c.Code("UploadPart", ("uploadId", begun.GetProperty("uploadId").GetString()!), ("offset", 0L), ("dataBase64", Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }))) == "InvalidRequest", "idle upload discarded");
        Assert(new ProjectCatalog(lastVault, key).OpenAndVerify().Count == 1, "staging is part of the vault layout");
    }
});

Console.WriteLine($"Endpoint tests: {passed} passed; synthetic workspace retained at {workspace}");
return 0;

sealed class Client : IDisposable
{
    private readonly NamedPipeClientStream pipe;
    private string? capability;
    public string? Capability => capability;
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

// Test-only: a software RSA key standing in for the source computer's TPM sender key in SourceVault Pack.
sealed class SoftwareSigner(RSA rsa) : pack::SourceVault.Pack.IPackageSigner
{
    public byte[] SubjectPublicKeyInfo() => rsa.ExportSubjectPublicKeyInfo();
    public byte[] SignHash(byte[] hash) => rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    public void Dispose() { }
}

// Test-only: an RSA key standing in for the TPM import key.
sealed class RsaTestKey(RSA rsa) : IKeyProtector
{
    public byte[] WrapKey(ReadOnlySpan<byte> k) => rsa.Encrypt(k.ToArray(), RSAEncryptionPadding.OaepSHA256);
    public byte[] UnwrapKey(ReadOnlySpan<byte> k) => rsa.Decrypt(k.ToArray(), RSAEncryptionPadding.OaepSHA256);
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

sealed class FakeBuildService : SourceVault.Build.IBuildService
{
    public bool Offline, Fail;
    public readonly List<IReadOnlyList<(string Name, byte[] Content)>> Received = [];
    public readonly Dictionary<string, string> ReceivedText = [];
    public Task<IReadOnlyList<string>> TargetsAsync(CancellationToken ct) =>
        Offline ? throw new SourceVault.Build.BuildServiceUnavailableException("NotRunning") : Task.FromResult<IReadOnlyList<string>>(["linux"]);
    public async Task<SourceVault.Build.BuildReply> RunAsync(string target, Guid projectId, int buildNumber, SourceVault.Build.IBuildSource source, CancellationToken ct)
    {
        if (Offline) throw new SourceVault.Build.BuildServiceUnavailableException("NotRunning");
        var files = new List<(string Name, byte[] Content)>();
        foreach (var f in source.Files) // read the stream as the build service would
        {
            var content = new byte[f.Length];
            for (int at = 0; at < content.Length;)
            {
                var chunk = await source.NextAsync(ct);
                if (chunk.IsEmpty) throw new Exception("short source");
                chunk.CopyTo(content.AsMemory(at)); at += chunk.Length;
            }
            files.Add((f.Name, content));
        }
        if (!(await source.NextAsync(ct)).IsEmpty) throw new Exception("long source");
        lock (Received)
        {
            Received.Add(files.Select(f => (f.Name, f.Content.ToArray())).ToList());
            foreach (var (name, content) in files) ReceivedText[name] = Encoding.UTF8.GetString(content);
        }
        var log = Encoding.UTF8.GetBytes($"synthetic-build-log: {files.Count} files\n");
        return Fail
            ? new SourceVault.Build.BuildReply("Failed", null, 1, log, false, null, 0, 0)
            : new SourceVault.Build.BuildReply("Succeeded", null, 0, log, false, $@"D:\artifacts\{buildNumber}-{target}", 3, 1234, ArtifactsDropped: 2);
    }
}
