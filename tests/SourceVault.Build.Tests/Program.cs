using System.Formats.Tar;
using System.IO.Pipes;
using System.Text;
using SourceVault.Build;

// Build pipeline tests with synthetic data: config validation, artifact publishing, the build steps against an
// in-memory container, the Docker Engine client against a fake engine on a named pipe, and the private channel.
// No Docker engine, container, service or network is used.
if (args is not [var workspaceArg] || !Path.IsPathFullyQualified(workspaceArg)) { Console.Error.WriteLine("usage: SourceVault.Build.Tests ABSOLUTE_WORKSPACE"); return 2; }
string workspace = Path.Combine(workspaceArg, "build-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);
int passed = 0;
void Assert(bool value, string what) { if (!value) throw new Exception("Assertion failed: " + what); }
void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();
byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

string Config(string target, string root = @"D:\\SourceVault\\artifacts") => $"{{\"version\":1,\"artifactRoot\":\"{root}\",\"targets\":[{target}]}}";
const string LinuxTarget = "{\"name\":\"linux\",\"engine\":\"npipe://./pipe/docker_engine\",\"container\":\"box\",\"platform\":\"linux\",\"sourcePath\":\"/sv/src\",\"command\":[\"/bin/sh\",\"-c\",\"./build.sh\"],\"artifactPath\":\"/sv/out\"}";
void RejectConfig(string json, string why)
{
    try { BuildConfig.Parse(Utf8(json)); } catch (BuildConfigException) { return; }
    throw new Exception("Accepted bad config: " + why);
}

Test("config: the shipped example parses; defaults apply", () =>
{
    var example = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "installer", "build.example.json");
    var text = File.ReadAllText(Path.GetFullPath(example));
    try { BuildConfig.Parse(Utf8(text)); throw new Exception("placeholder thumbprint accepted"); }
    catch (BuildConfigException e) { Assert(e.Message.Contains("clientCertificateThumbprint"), e.Message); }
    var config = BuildConfig.Parse(Utf8(text.Replace("REPLACE-WITH-THUMBPRINT-IN-LocalMachine-My", new string('a', 40))));
    Assert(config.Targets.Count == 2 && config.Targets[0].Engine.Scheme == "tcp" && config.Targets[1].IsWindows, "two targets");
    Assert(config.Targets[0].ClientCertificateThumbprint == new string('A', 40), "thumbprint normalized");
    var minimal = BuildConfig.Parse(Utf8(Config(LinuxTarget)));
    var t = minimal.Targets.Single();
    Assert(t.TimeoutSeconds == 900 && t.MaxArtifactBytes == 512L * 1024 * 1024 && !t.AllowNetwork && minimal.ArtifactRoot == @"D:\SourceVault\artifacts", "defaults");
});

Test("config: unsafe or ambiguous settings are refused", () =>
{
    string With(string from, string to) => Config(LinuxTarget.Replace(from, to));
    RejectConfig(Config(LinuxTarget).Replace("\"version\":1", "\"version\":2"), "version");
    RejectConfig(With("npipe://./pipe/docker_engine", "tcp://127.0.0.1:2375"), "tcp without tls");
    RejectConfig(With("npipe://./pipe/docker_engine\"", "tcp://10.0.0.5:2376\",\"tls\":{\"caFile\":\"C:\\\\ca.pem\",\"clientCertificateThumbprint\":\"" + new string('A', 40) + "\"}"), "non-loopback engine");
    RejectConfig(With("npipe://./pipe/docker_engine", "http://localhost:2375"), "http engine");
    RejectConfig(With("\"/sv/src\"", "\"/\""), "root as source path");
    RejectConfig(With("\"/sv/src\"", "\"/src\""), "one-level source path");
    RejectConfig(With("\"/sv/src\"", "\"/sv/../etc\""), "dot-dot");
    RejectConfig(With("\"/sv/src\"", "\"/sv/out\""), "source equals artifacts");
    RejectConfig(With("\"/sv/src\"", "\"/sv/out/src\""), "source inside artifacts");
    RejectConfig(With("\"box\"", "\"bad name!\""), "container name");
    RejectConfig(With("\"command\":[\"/bin/sh\",\"-c\",\"./build.sh\"]", "\"command\":[]"), "empty command");
    RejectConfig(With("\"linux\",\"engine\"", "\"linux\",\"extra\":1,\"engine\""), "unknown property");
    RejectConfig(With("\"platform\":\"linux\"", "\"platform\":\"mac\""), "platform");
    RejectConfig(Config(LinuxTarget + "," + LinuxTarget.Replace("\"linux\",\"engine\"", "\"LINUX\",\"engine\"")), "duplicate names");
    RejectConfig(Config(LinuxTarget, @"C:\\"), "drive root");
    RejectConfig(Config(LinuxTarget, @"\\\\server\\share\\out"), "network path");
    RejectConfig(Config(LinuxTarget, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).Replace("\\", "\\\\") + @"\\SourceVault\\out"), "inside SourceVault data");
    var windows = LinuxTarget.Replace("\"platform\":\"linux\"", "\"platform\":\"windows\"").Replace("\"/sv/src\"", "\"C:\\\\sv\\\\src\"").Replace("\"/sv/out\"", "\"C:\\\\sv\\\\out\"");
    Assert(BuildConfig.Parse(Utf8(Config(windows))).Targets[0].IsWindows, "windows paths");
    RejectConfig(Config(windows.Replace("C:\\\\sv\\\\src", "C:\\\\sv\\\\src&calc")), "cmd metacharacter in a windows path");
    RejectConfig("{\"version\":1,\"version\":1}", "duplicate property");
    RejectConfig("not json", "not json");
});

// ---- artifacts ----------------------------------------------------------------------------------------------------
MemoryStream Tar(params (string Name, TarEntryType Type, string? Text)[] entries)
{
    var stream = new MemoryStream();
    using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        foreach (var (name, type, text) in entries)
        {
            var entry = new PaxTarEntry(type, name);
            if (type == TarEntryType.SymbolicLink) entry.LinkName = "/etc/passwd";
            if (text is not null) entry.DataStream = new MemoryStream(Utf8(text));
            writer.WriteEntry(entry);
        }
    stream.Position = 0;
    return stream;
}
string Dest() => Path.Combine(workspace, "artifacts-" + Guid.NewGuid().ToString("N"));

Test("artifacts: files and folders are published under the build folder", () =>
{
    var dest = Dest();
    var result = Run(ArtifactPublisher.PublishAsync(Tar(("out/", TarEntryType.Directory, null), ("out/app.js", TarEntryType.RegularFile, "console.log(1)"), ("out/lib/x.dll", TarEntryType.RegularFile, "MZ")), "out", dest, 1 << 20, default));
    Assert(result.Files == 2 && result.Bytes == 16 && File.ReadAllText(Path.Combine(dest, "app.js")) == "console.log(1)" && File.Exists(Path.Combine(dest, "lib", "x.dll")), "published");
    Assert(!Directory.Exists(dest + ".partial"), "no partial folder left");
});

Test("artifacts: links, escapes, bad names, duplicates and oversize sets are refused whole", () =>
{
    void Refuse(MemoryStream tar, string reason, long max = 1 << 20)
    {
        var dest = Dest();
        try { Run(ArtifactPublisher.PublishAsync(tar, "out", dest, max, default)); throw new Exception("accepted: " + reason); }
        catch (ArtifactRejectedException e) { Assert(e.Reason == reason, $"{reason} (got {e.Reason})"); }
        Assert(!Directory.Exists(dest) && !Directory.Exists(dest + ".partial"), "nothing left behind: " + reason);
    }
    Refuse(Tar(("out/a.txt", TarEntryType.RegularFile, "a"), ("out/link", TarEntryType.SymbolicLink, null)), "NotAFileOrFolder");
    Refuse(Tar(("out/../../evil.txt", TarEntryType.RegularFile, "x")), "BadName");
    Refuse(Tar(("elsewhere/x.txt", TarEntryType.RegularFile, "x")), "OutsideArtifactFolder");
    Refuse(Tar(("out/CON.txt", TarEntryType.RegularFile, "x")), "BadName");
    Refuse(Tar(("out/a:b", TarEntryType.RegularFile, "x")), "BadName");
    Refuse(Tar(("out/A.txt", TarEntryType.RegularFile, "x"), ("out/a.TXT", TarEntryType.RegularFile, "y")), "DuplicateName");
    Refuse(Tar(("out/big.bin", TarEntryType.RegularFile, new string('x', 100))), "TooLarge", max: 50);
});

MemoryStream RevealingOutput() => Tar(("out/", TarEntryType.Directory, null),
    ("out/app.js", TarEntryType.RegularFile, "console.log(1)"),
    ("out/app.js.map", TarEntryType.RegularFile, "{\"sourcesContent\":[\"synthetic\"]}"),
    ("out/App.PDB", TarEntryType.RegularFile, "synthetic symbols"),
    ("out/obj/", TarEntryType.Directory, null), ("out/obj/x.o", TarEntryType.RegularFile, "synthetic object"),
    ("out/x.dSYM/", TarEntryType.Directory, null), ("out/x.dSYM/Contents/dwarf", TarEntryType.RegularFile, "synthetic dwarf"),
    ("out/inline.js", TarEntryType.RegularFile, "f()\n//# sourceMappingURL=data:application/json;base64,c3ludGhldGlj\n"),
    ("out/lib/static.lib", TarEntryType.RegularFile, "synthetic library"));

Test("artifacts: source maps, debug symbols, intermediates and inline source maps are left out and listed", () =>
{
    var dest = Dest();
    var result = Run(ArtifactPublisher.PublishAsync(RevealingOutput(), "out", dest, 1 << 20, default));
    Assert(result.Files == 2 && result.Bytes == "console.log(1)".Length + "synthetic library".Length, $"two files published (got {result.Files})");
    Assert(result.DroppedCount == 5 && result.Dropped.Count == 5, "five left out");
    var reasons = result.Dropped.ToDictionary(d => d.Path, d => d.Reason);
    Assert(reasons["app.js.map"] == "SourceMap" && reasons["App.PDB"] == "DebugSymbols" && reasons["obj/x.o"] == "Intermediate"
        && reasons["x.dSYM/Contents/dwarf"] == "DebugSymbols" && reasons["inline.js"] == "InlineSourceMap", string.Join(", ", reasons));
    var onDisk = Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dest, f)).Order().ToArray();
    Assert(onDisk.SequenceEqual(["app.js", @"lib\static.lib"]), "only the kept files are on disk: " + string.Join(", ", onDisk));
    Assert(!Directory.Exists(Path.Combine(dest, "x.dSYM")), "no dSYM folder");
});

Test("artifacts: keepFileTypes publishes the kinds an administrator chose; a map comment beyond the last 64 KiB is not seen", () =>
{
    var dest = Dest();
    var keep = new HashSet<string>([".map", ".dsym", "inline-source-map"]);
    var result = Run(ArtifactPublisher.PublishAsync(RevealingOutput(), "out", dest, 1 << 20, default, keep));
    Assert(result.Files == 5 && result.DroppedCount == 2 && result.Dropped.Select(d => d.Path).Order().SequenceEqual(["App.PDB", "obj/x.o"]), "only .pdb and .o still left out");
    var early = Dest();
    var padded = "//# sourceMappingURL=data:application/json;base64,c3ludGhldGlj\n" + new string(' ', ArtifactFilter.InlineScanBytes + 10);
    var kept = Run(ArtifactPublisher.PublishAsync(Tar(("out/early.js", TarEntryType.RegularFile, padded)), "out", early, 1 << 20, default));
    Assert(kept.Files == 1 && kept.DroppedCount == 0, "documented limit: only the end of the file is checked");
});

Test("config: keepFileTypes accepts known kinds once each, case-insensitively", () =>
{
    var t = BuildConfig.Parse(Utf8(Config(LinuxTarget.Replace("\"artifactPath\"", "\"keepFileTypes\":[\".MAP\",\"inline-source-map\"],\"artifactPath\"")))).Targets[0];
    Assert(t.Keep.SetEquals([".map", "inline-source-map"]), "parsed and lowercased");
    Assert(BuildConfig.Parse(Utf8(Config(LinuxTarget))).Targets[0].Keep.Count == 0, "default keeps nothing");
    string Keep(string list) => Config(LinuxTarget.Replace("\"artifactPath\"", $"\"keepFileTypes\":{list},\"artifactPath\""));
    RejectConfig(Keep("[\".exe\"]"), "not a dropped kind");
    RejectConfig(Keep("[\".map\",\".MAP\"]"), "listed twice");
    RejectConfig(Keep("\".map\""), "not an array");
    RejectConfig(Keep("[1]"), "not text");
});

Test("build: dropped files are listed at the end of the log, and the old log buffer is zeroed", () =>
{
    var box = new FakeContainer { ArchiveOverride = _ => RevealingOutput() };
    var target = BuildConfig.Parse(Utf8(Config(LinuxTarget))).Targets[0];
    IReadOnlyList<(string, byte[])> source = [("build.sh", Utf8("#!/bin/sh\necho synthetic\n"))];
    var outcome = Run(BuildRunner.RunAsync(target, Dest(), Guid.NewGuid(), 1, new InMemoryBuildSource(source), new FakeEngine(box), default));
    var log = Encoding.UTF8.GetString(outcome.Log);
    Assert(outcome.State == BuildState.Succeeded && outcome.Artifacts!.DroppedCount == 5, "succeeded with five left out");
    Assert(log.Contains("5 output file(s) were not published") && log.Contains("  app.js.map  (SourceMap)") && log.Contains("keepFileTypes"), log);
    var many = Enumerable.Range(0, ArtifactFilter.MaxListed).Select(i => new DroppedArtifact($"m{i}.map", "SourceMap")).ToList();
    var original = Utf8("synthetic build output\n");
    var noticed = Encoding.UTF8.GetString(BuildRunner.WithDroppedNotice(original, new PublishedArtifacts("D:\\x", 0, 0, many, 150)));
    Assert(noticed.StartsWith("synthetic build output\n") && noticed.Contains("...and 50 more"), "capped list");
    Assert(original.All(b => b == 0), "old buffer zeroed");
    var plain = Utf8("unchanged");
    Assert(ReferenceEquals(BuildRunner.WithDroppedNotice(plain, new PublishedArtifacts("D:\\x", 1, 1)), plain), "nothing dropped: log untouched");
});

// ---- build steps against an in-memory container ------------------------------------------------------------------
var linux = BuildConfig.Parse(Utf8(Config(LinuxTarget))).Targets[0];
IReadOnlyList<(string, byte[])> Source() => [("build.sh", Utf8("#!/bin/sh\necho synthetic\n")), ("src/app.ts", Utf8("export const x = 1;\n"))];

Test("build: source goes in, the command runs there, artifacts come out, folders are emptied before and after", () =>
{
    var box = new FakeContainer();
    box.Build = c =>
    {
        Assert(c.Files.ContainsKey("/sv/src/build.sh") && c.Files.ContainsKey("/sv/src/src/app.ts"), "source copied in");
        c.Files["/sv/out/app.js"] = Utf8("compiled");
        return (0, "compiling…\ndone\n");
    };
    var root = Dest(); var project = Guid.NewGuid();
    var outcome = Run(BuildRunner.RunAsync(linux, root, project, 7, new InMemoryBuildSource(Source()), new FakeEngine(box), default));
    Assert(outcome.State == BuildState.Succeeded && outcome.ExitCode == 0 && Encoding.UTF8.GetString(outcome.Log) == "compiling…\ndone\n", "succeeded");
    var folder = Path.Combine(root, project.ToString("D"), "7-linux");
    Assert(outcome.Artifacts!.Folder == folder && File.ReadAllText(Path.Combine(folder, "app.js")) == "compiled", "artifact published");
    Assert(box.Calls.SequenceEqual(["inspect box", "clean", "put /sv/src", "exec /bin/sh -c ./build.sh", "get /sv/out", "clean"]), string.Join(" | ", box.Calls));
    Assert(box.Files.Count == 0, "container folders emptied afterwards");
});

Test("build: failures, timeouts and unsafe containers", () =>
{
    var failing = new FakeContainer { Build = _ => (2, "error: synthetic\n") };
    var failed = Run(BuildRunner.RunAsync(linux, Dest(), Guid.NewGuid(), 1, new InMemoryBuildSource(Source()), new FakeEngine(failing), default));
    Assert(failed.State == BuildState.Failed && failed.ExitCode == 2 && failed.Artifacts is null && !failing.Calls.Contains("get /sv/out"), "failed build publishes nothing");
    var hanging = new FakeContainer { Hang = true };
    var quick = linux with { TimeoutSeconds = 1 };
    var timedOut = Run(BuildRunner.RunAsync(quick, Dest(), Guid.NewGuid(), 1, new InMemoryBuildSource(Source()), new FakeEngine(hanging), default));
    Assert(timedOut.State == BuildState.TimedOut && hanging.Calls.Contains("restart") && hanging.Calls[^1] == "clean", "timeout restarts then cleans");
    Assert(Run(BuildRunner.RunAsync(linux, Dest(), Guid.NewGuid(), 1, new InMemoryBuildSource(Source()), new FakeEngine(new FakeContainer { NetworkMode = "bridge" }), default)) is { State: BuildState.Rejected, Reason: "NetworkNotDisabled" }, "network refused");
    Assert(Run(BuildRunner.RunAsync(linux with { AllowNetwork = true }, Dest(), Guid.NewGuid(), 1, new InMemoryBuildSource(Source()), new FakeEngine(new FakeContainer { NetworkMode = "bridge" }), default)).State == BuildState.Succeeded, "network allowed by config");
    Assert(Run(BuildRunner.RunAsync(linux, Dest(), Guid.NewGuid(), 1, new InMemoryBuildSource(Source()), new FakeEngine(new FakeContainer { Running = false }), default)) is { State: BuildState.Unavailable, Reason: "ContainerNotRunning" }, "not running");
    Assert(Run(BuildRunner.RunAsync(linux, Dest(), Guid.NewGuid(), 1, new InMemoryBuildSource(Source()), new FakeEngine(new FakeContainer { Os = "windows" }), default)) is { State: BuildState.Rejected, Reason: "PlatformMismatch" }, "platform");
    var linky = new FakeContainer { ArchiveOverride = _ => Tar(("out/", TarEntryType.Directory, null), ("out/l", TarEntryType.SymbolicLink, null)) };
    var rejected = Run(BuildRunner.RunAsync(linux, Dest(), Guid.NewGuid(), 1, new InMemoryBuildSource(Source()), new FakeEngine(linky), default));
    Assert(rejected is { State: BuildState.Rejected, Reason: "ArtifactsNotAFileOrFolder" } && rejected.Log.Length > 0, "bad artifacts refused, log kept");
    var none = Run(BuildRunner.RunAsync(linux, Dest(), Guid.NewGuid(), 1, new InMemoryBuildSource(Source()), new FakeEngine(new FakeContainer()), default));
    Assert(none.State == BuildState.Succeeded && none.Artifacts!.Files == 0, "no output folder: success with no artifacts");
});

Test("build: the windows cleanup line quotes the configured paths", () =>
{
    var windows = linux with { Platform = "windows", SourcePath = @"C:\sv\src", ArtifactPath = @"C:\sv\out" };
    var line = BuildRunner.CleanupCommand(windows);
    Assert(line[0] == "cmd.exe" && line[^1].Contains("rmdir /s /q \"C:\\sv\\src\"") && line[^1].Contains("mkdir \"C:\\sv\\out\""), line[^1]);
});

// ---- Docker Engine API client over a named pipe ---------------------------------------------------------------------
Test("docker client: inspect, upload, exec with multiplexed output, archive download, restart, errors", () =>
{
    using var server = new FakeDockerServer();
    using var client = new DockerEngineClient(new Uri($"npipe://./pipe/{server.PipeName}"));
    var info = Run(client.InspectAsync("box", default));
    Assert(info is { Running: true, NetworkMode: "none", Os: "linux" }, "inspect");
    try { Run(client.InspectAsync("missing", default)); throw new Exception("404 accepted"); }
    catch (DockerException e) { Assert(e.Status == 404 && !e.Message.Contains("secret"), "status only, no body"); }
    Run(client.PutArchiveAsync("box", "/sv/src", new MemoryStream(Utf8("tar-bytes")), default).ContinueWith(t => { t.GetAwaiter().GetResult(); return 0; }));
    Assert(Encoding.UTF8.GetString(server.Uploads["/sv/src"]) == "tar-bytes", "upload body and path");
    var exec = Run(client.ExecAsync("box", ["make", "all"], "/sv/src", TimeSpan.FromSeconds(10), 1024, default));
    Assert(exec is { ExitCode: 0, TimedOut: false, Truncated: false } && Encoding.UTF8.GetString(exec.Output) == "built\nwarning\n", "exec output demultiplexed");
    Assert(server.Requests.Contains("cmd make all in /sv/src"), "exec command and folder");
    server.ExitCode = 3; server.ExecOutput = Utf8(new string('x', 5000));
    var truncated = Run(client.ExecAsync("box", ["make"], null, TimeSpan.FromSeconds(10), 100, default));
    Assert(truncated is { ExitCode: 3, Truncated: true } && truncated.Output.Length == 100, "bounded output, exit code");
    Assert(Run(client.GetArchiveAsync("box", "/sv/out", default)) is null, "missing folder is null");
    server.Archive = Tar(("out/a.txt", TarEntryType.RegularFile, "artifact")).ToArray();
    using (var archive = Run(client.GetArchiveAsync("box", "/sv/out", default))!)
    {
        var copy = new MemoryStream(); archive.CopyTo(copy);
        Assert(copy.ToArray().SequenceEqual(server.Archive), "archive bytes");
    }
    Run(client.RestartAsync("box", default).ContinueWith(t => { t.GetAwaiter().GetResult(); return 0; }));
    Assert(server.Requests.Contains("POST /v1.41/containers/box/restart?t=0"), "restart");
});

// ---- the private channel, end to end over a named pipe -------------------------------------------------------------
Test("channel: targets and a whole build cross the pipe; bad names and unknown targets are refused", () =>
{
    var name = "sv-build-test-" + Guid.NewGuid().ToString("N");
    var root = Dest();
    var config = BuildConfig.Parse(Utf8(Config(LinuxTarget, root.Replace("\\", "\\\\"))));
    var box = new FakeContainer { Build = c => { c.Files["/sv/out/result.txt"] = c.Files["/sv/src/src/app.ts"]; return (0, "ok\n"); } };
    using var stop = new CancellationTokenSource();
    var serving = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try { await pipe.WaitForConnectionAsync(stop.Token); } catch (OperationCanceledException) { return; }
            try { await BuildChannel.ServeAsync(pipe, () => config, _ => new FakeEngine(box), stop.Token); } catch { }
        }
    });
    var client = new BuildChannel.Client(async ct => { var p = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous); await p.ConnectAsync(5000, ct); return p; });
    Assert(Run(client.TargetsAsync(default)).SequenceEqual(["linux"]), "targets");
    var project = Guid.NewGuid();
    var reply = Run(client.RunAsync("LINUX", project, 3, new InMemoryBuildSource(Source()), default));
    Assert(reply.State == "Succeeded" && reply.ArtifactFiles == 1 && Encoding.UTF8.GetString(reply.Log) == "ok\n", "build reply");
    Assert(File.ReadAllText(Path.Combine(root, project.ToString("D"), "3-linux", "result.txt")) == "export const x = 1;\n", "artifact from the source");
    try { Run(client.RunAsync("nope", project, 4, new InMemoryBuildSource(Source()), default)); throw new Exception("unknown target accepted"); }
    catch (BuildServiceUnavailableException e) { Assert(e.Reason == "UnknownTarget", e.Reason); }
    try { Run(client.RunAsync("linux", project, 5, new InMemoryBuildSource([("../escape.sh", Utf8("x"))]), default)); throw new Exception("bad name accepted"); }
    catch (BuildServiceUnavailableException e) { Assert(e.Reason == "InvalidRequest", e.Reason); }
    stop.Cancel();
    try { using var poke = new NamedPipeClientStream(".", name); poke.Connect(200); } catch { }
    serving.Wait(3000);
});

// ---- streaming (docs/vault-any-file-design.md section 6) ----------------------------------------------------------
List<(string, byte[])> LargeProject()
{
    var big = new byte[3 * 1024 * 1024 + 7]; new Random(3).NextBytes(big); big[0] = 0;
    var files = Enumerable.Range(0, 2000).Select(i => ($"src/m{i % 20}/f{i}.c", Utf8($"int f{i};\n"))).ToList();
    files.Add(("lib/big.bin", big)); files.Add(("empty.txt", []));
    return files;
}

Test("build streaming: 2,000 files and a 3 MiB binary arrive byte-exact through the tar stream", () =>
{
    var files = LargeProject();
    var box = new FakeContainer
    {
        Build = c =>
        {
            Assert(c.Files["/sv/src/lib/big.bin"].SequenceEqual(files.Single(f => f.Item1 == "lib/big.bin").Item2), "binary byte-exact");
            Assert(c.Files.Count(k => k.Key.StartsWith("/sv/src/src/")) == 2000 && c.Files["/sv/src/src/m7/f1007.c"].SequenceEqual(Utf8("int f1007;\n")), "every small file");
            Assert(c.Files["/sv/src/empty.txt"].Length == 0, "empty file");
            return (0, "ok\n");
        },
    };
    Assert(Run(BuildRunner.RunAsync(linux, Dest(), Guid.NewGuid(), 1, new InMemoryBuildSource(files), new FakeEngine(box), default)).State == BuildState.Succeeded, "built");
});

Test("build streaming: a source failing mid-copy fails the build (SourceUnavailable), runs nothing, and empties the folders", () =>
{
    var box = new FakeContainer();
    var outcome = Run(BuildRunner.RunAsync(linux, Dest(), Guid.NewGuid(), 1, new FailingSource(LargeProject(), 50), new FakeEngine(box), default));
    Assert(outcome is { State: BuildState.Failed, Reason: "SourceUnavailable" }, outcome.State + " " + outcome.Reason);
    Assert(!box.Calls.Any(c => c.StartsWith("exec /bin/sh -c ./build")) && box.Calls[^1] == "clean" && box.Files.Count == 0, string.Join(" | ", box.Calls));
});

Test("channel streaming: a file bigger than any frame, an abort, an engine failure mid-copy, and the table limits", () =>
{
    var name = "sv-build-stream-" + Guid.NewGuid().ToString("N");
    var root = Dest();
    var config = BuildConfig.Parse(Utf8(Config(LinuxTarget, root.Replace("\\", "\\\\"))));
    var box = new FakeContainer();
    using var stop = new CancellationTokenSource();
    var serving = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try { await pipe.WaitForConnectionAsync(stop.Token); } catch (OperationCanceledException) { return; }
            try { await BuildChannel.ServeAsync(pipe, () => config, _ => new FakeEngine(box), stop.Token); } catch { }
        }
    });
    var client = new BuildChannel.Client(async ct => { var p = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous); await p.ConnectAsync(5000, ct); return p; });
    // A deadlock (both sides writing) would hang; a bounded wait turns it into a failure instead.
    BuildReply Within(Task<BuildReply> task) { Assert(task.Wait(TimeSpan.FromSeconds(60)), "no deadlock"); return task.Result; }
    var files = LargeProject();
    box.Build = c => { Assert(c.Files["/sv/src/lib/big.bin"].SequenceEqual(files.Single(f => f.Item1 == "lib/big.bin").Item2), "3 MiB file over the pipe"); return (0, "ok\n"); };
    Assert(Within(client.RunAsync("linux", Guid.NewGuid(), 1, new InMemoryBuildSource(files), default)).State == "Succeeded", "large build over the pipe");
    var aborted = Within(client.RunAsync("linux", Guid.NewGuid(), 2, new FailingSource(files, 40), default));
    Assert(aborted is { State: "Failed", Reason: "SourceUnavailable" }, aborted.State + " " + aborted.Reason);
    box.PutFails = true;
    var engineFailed = Within(client.RunAsync("linux", Guid.NewGuid(), 3, new InMemoryBuildSource(files), default)); // the service drains the rest
    Assert(engineFailed is { State: "Unavailable", Reason: "EngineFailed" }, engineFailed.State + " " + engineFailed.Reason);
    box.PutFails = false;
    foreach (var bad in new[] { new InMemoryBuildSource([("a.c", Utf8("1")), ("A.C", Utf8("2"))]), new InMemoryBuildSource(Enumerable.Range(0, BuildChannel.MaxFiles + 1).Select(i => ($"f{i}", Array.Empty<byte>())).ToList()) })
    {
        try { Within(client.RunAsync("linux", Guid.NewGuid(), 4, bad, default)); throw new Exception("bad table accepted"); }
        catch (AggregateException e) when (e.InnerException is BuildServiceUnavailableException { Reason: "InvalidRequest" }) { }
    }
    stop.Cancel();
    try { using var poke = new NamedPipeClientStream(".", name); poke.Connect(200); } catch { }
    serving.Wait(TimeSpan.FromSeconds(5));
});

Console.WriteLine($"Build tests: {passed} passed; synthetic workspace retained at {workspace}");
return 0;
