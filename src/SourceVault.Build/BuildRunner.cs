using System.Formats.Tar;
using System.IO.Pipelines;
using System.Security.Cryptography;

namespace SourceVault.Build;

/// <summary>How a build ended. <see cref="Reason"/> is a fixed code (never container output) when not Succeeded/Failed.</summary>
public enum BuildState { Succeeded, Failed, TimedOut, Rejected, Unavailable }

public sealed record BuildOutcome(BuildState State, string? Reason, int? ExitCode, byte[] Log, bool LogTruncated, PublishedArtifacts? Artifacts);

/// <summary>
/// One build in a named container (docs/files-history-builds-design.md, section 4):
/// 1. the container must be running, on the configured platform, and (unless allowed) have no network;
/// 2. sourcePath and artifactPath are emptied; 3. the project is copied in as a tar, streamed from the source straight
/// into the engine (any-file design section 6: a few chunks in memory, 30 minutes at most, not counted in the build's
/// own time limit; a source that fails mid-copy fails the build with SourceUnavailable); 4. the command runs with a time
/// limit, output captured (bounded); 5. on success the artifact folder is published to the host;
/// 6. both folders are emptied again. A timed-out build restarts the container to stop it.
/// Emptying folders is best effort, not erasure: whatever the build copied elsewhere stays in the container.
/// </summary>
public static class BuildRunner
{
    public const int MaxLogBytes = 512 * 1024;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(2);
    internal static TimeSpan CopyTimeout = TimeSpan.FromMinutes(30);

    public static async Task<BuildOutcome> RunAsync(BuildTarget target, string artifactRoot, Guid projectId, int buildNumber,
        IBuildSource source, IDockerEngine engine, CancellationToken ct)
    {
        ContainerInfo info;
        try { info = await engine.InspectAsync(target.Container, ct).ConfigureAwait(false); }
        catch (Exception e) when (IsUntrusted(e)) { return Unavailable("EngineUntrusted"); } // nothing was sent
        catch (DockerException e) { return Unavailable(e.Status == 404 ? "ContainerNotFound" : "EngineUnavailable"); }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException) { return Unavailable("EngineUnavailable"); }
        if (!info.Running) return Unavailable("ContainerNotRunning");
        if (!string.Equals(info.Os, target.Platform, StringComparison.OrdinalIgnoreCase)) return new(BuildState.Rejected, "PlatformMismatch", null, [], false, null);
        if (!target.AllowNetwork && info.NetworkMode != "none") return new(BuildState.Rejected, "NetworkNotDisabled", null, [], false, null);

        bool needsCleanup = false;
        try
        {
            if (!await CleanAsync(target, engine, ct).ConfigureAwait(false)) return Unavailable("CleanupFailed");
            needsCleanup = true;
            if (await CopySourceAsync(target, source, engine, ct).ConfigureAwait(false) is { } failed) return failed;
            var run = await engine.ExecAsync(target.Container, target.Command, target.SourcePath, TimeSpan.FromSeconds(target.TimeoutSeconds), MaxLogBytes, ct).ConfigureAwait(false);
            if (run.TimedOut)
            {
                try { await engine.RestartAsync(target.Container, ct).ConfigureAwait(false); } catch (Exception e) when (e is DockerException or HttpRequestException or IOException) { }
                return new(BuildState.TimedOut, "TimedOut", null, run.Output, run.Truncated, null);
            }
            if (run.ExitCode != 0) return new(BuildState.Failed, null, run.ExitCode, run.Output, run.Truncated, null);
            var folderName = target.ArtifactPath[(target.ArtifactPath.LastIndexOfAny(['/', '\\']) + 1)..];
            var destination = Path.Combine(artifactRoot, projectId.ToString("D"), $"{buildNumber}-{target.Name}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Right before writing: neither the output folder nor this project's folder may be a link (a junction would send
            // the output elsewhere). Their parents were checked when the build was accepted.
            if (IsLink(artifactRoot) || IsLink(Path.GetDirectoryName(destination)!)) return new(BuildState.Rejected, "ArtifactFolderUntrusted", 0, run.Output, run.Truncated, null);
            await using var archive = await engine.GetArchiveAsync(target.Container, target.ArtifactPath, ct).ConfigureAwait(false);
            PublishedArtifacts published;
            if (archive is null) { Directory.CreateDirectory(destination); published = new(destination, 0, 0); } // no output folder: nothing to publish
            else
            {
                try { published = await ArtifactPublisher.PublishAsync(archive, folderName, destination, target.MaxArtifactBytes, ct, target.Keep).ConfigureAwait(false); }
                catch (ArtifactRejectedException e) { return new(BuildState.Rejected, "Artifacts" + e.Reason, 0, run.Output, run.Truncated, null); }
            }
            return new(BuildState.Succeeded, null, 0, WithDroppedNotice(run.Output, published), run.Truncated, published);
        }
        catch (Exception e) when (IsUntrusted(e)) { return Unavailable("EngineUntrusted"); } // a later connection: refused before sending
        catch (DockerException) { return Unavailable("EngineFailed"); }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException) { return Unavailable("EngineFailed"); }
        finally
        {
            if (needsCleanup)
                try { await CleanAsync(target, engine, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception e) when (e is DockerException or HttpRequestException or IOException) { }
        }
    }

    private static BuildOutcome Unavailable(string reason) => new(BuildState.Unavailable, reason, null, [], false, null);

    private static bool IsLink(string folder) => (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0;

    // The engine's pipe wasn't the engine's (HttpClient wraps connection failures).
    private static bool IsUntrusted(Exception? e)
    {
        for (; e is not null; e = e.InnerException) if (e is DockerEngineUntrustedException) return true;
        return false;
    }

    // Streams the source into sourcePath as a tar: a producer writes tar entries into a pipe while the engine reads the
    // upload body from it, so memory is the pipe's window. Null on success; otherwise the build's outcome.
    private static async Task<BuildOutcome?> CopySourceAsync(BuildTarget target, IBuildSource source, IDockerEngine engine, CancellationToken ct)
    {
        using var copy = CancellationTokenSource.CreateLinkedTokenSource(ct);
        copy.CancelAfter(CopyTimeout);
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 2 << 20, resumeWriterThreshold: 1 << 20, useSynchronizationContext: false));
        var producer = Task.Run(() => WriteTarAsync(source, pipe.Writer, copy.Token), CancellationToken.None);
        Exception? upload = null;
        try { await engine.PutArchiveAsync(target.Container, target.SourcePath, pipe.Reader.AsStream(), copy.Token).ConfigureAwait(false); }
        catch (Exception e) when (e is not OutOfMemoryException) { upload = e; }
        finally { await pipe.Reader.CompleteAsync().ConfigureAwait(false); } // a stopped upload releases a waiting producer
        try { await producer.ConfigureAwait(false); }
        catch (BuildSourceException) { return new(BuildState.Failed, "SourceUnavailable", null, [], false, null); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(BuildState.Failed, "CopyTimedOut", null, [], false, null); }
        catch (Exception) when (upload is not null) { } // the upload's failure is reported below
        if (upload is OperationCanceledException && !ct.IsCancellationRequested) return new(BuildState.Failed, "CopyTimedOut", null, [], false, null);
        if (upload is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(upload).Throw();
        return null;
    }

    // The project as a tar of relative paths ("src/a.ts"), written entry by entry from the source into the pipe.
    internal static async Task WriteTarAsync(IBuildSource source, PipeWriter writer, CancellationToken ct)
    {
        try
        {
            await using (var stream = writer.AsStream(leaveOpen: true))
            await using (var tar = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
            {
                var folders = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in source.Files)
                {
                    var parts = file.Name.Split('/');
                    for (int i = 1; i < parts.Length; i++)
                    {
                        var folder = string.Join('/', parts[..i]) + "/";
                        if (folders.Add(folder)) await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, folder) { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute }, ct).ConfigureAwait(false);
                    }
                    var data = new SourceFileStream(source, file.Length, ct);
                    var entry = new PaxTarEntry(TarEntryType.RegularFile, file.Name) { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, DataStream = data };
                    await tar.WriteEntryAsync(entry, ct).ConfigureAwait(false);
                    if (data.Position != file.Length) throw new BuildSourceException("SourceUnavailable");
                }
            }
            if (!(await source.NextAsync(ct).ConfigureAwait(false)).IsEmpty) throw new BuildSourceException("SourceUnavailable"); // more bytes than the table
            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception e) { await writer.CompleteAsync(e).ConfigureAwait(false); throw; }
    }

    // The dropped files are listed at the end of the build log, which the vault stores encrypted (and, when trimming
    // a long log, keeps the end of). The old buffer, which may hold build output, is zeroed.
    internal static byte[] WithDroppedNotice(byte[] output, PublishedArtifacts published)
    {
        if (published.DroppedCount == 0) return output;
        var notice = new System.Text.StringBuilder();
        notice.Append($"\n[SourceVault] {published.DroppedCount} output file(s) were not published because they can reveal source:\n");
        foreach (var d in published.Dropped) notice.Append($"  {d.Path}  ({d.Reason})\n");
        if (published.DroppedCount > published.Dropped.Count) notice.Append($"  ...and {published.DroppedCount - published.Dropped.Count} more\n");
        notice.Append("[SourceVault] An administrator can publish a kind anyway with \"keepFileTypes\" in build.json.\n");
        var log = output.Concat(System.Text.Encoding.UTF8.GetBytes(notice.ToString())).ToArray();
        CryptographicOperations.ZeroMemory(output);
        return log;
    }

    // Removes and recreates sourcePath and artifactPath inside the container. Paths come from the validated admin config.
    // Windows: one bare word per argument, with no quotes or spaces in any of them. The engine joins the arguments into
    // a command line and escapes embedded quotes with backslashes, which cmd.exe doesn't understand: a quoted path
    // reached mkdir as \"C:\...\\\" and every build failed with CleanupFailed. The config allows no spaces in these paths.
    // mkdir fails if a folder is still there, so a folder rmdir couldn't remove fails the cleanup.
    internal static IReadOnlyList<string> CleanupCommand(BuildTarget target) => target.IsWindows
        ? ["cmd.exe", "/d", "/c",
           "rmdir", "/s", "/q", target.SourcePath, "2>nul", "&", "rmdir", "/s", "/q", target.ArtifactPath, "2>nul", "&",
           "mkdir", target.SourcePath, "&&", "mkdir", target.ArtifactPath]
        : ["/bin/sh", "-c", "rm -rf -- \"$1\" \"$2\" && mkdir -p -- \"$1\" \"$2\"", "sourcevault-clean", target.SourcePath, target.ArtifactPath];

    private static async Task<bool> CleanAsync(BuildTarget target, IDockerEngine engine, CancellationToken ct)
    {
        var result = await engine.ExecAsync(target.Container, CleanupCommand(target), null, StepTimeout, 4096, ct).ConfigureAwait(false);
        return !result.TimedOut && result.ExitCode == 0;
    }

}
