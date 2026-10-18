using System.Formats.Tar;
using System.Security.Cryptography;

namespace SourceVault.Build;

/// <summary>How a build ended. <see cref="Reason"/> is a fixed code (never container output) when not Succeeded/Failed.</summary>
public enum BuildState { Succeeded, Failed, TimedOut, Rejected, Unavailable }

public sealed record BuildOutcome(BuildState State, string? Reason, int? ExitCode, byte[] Log, bool LogTruncated, PublishedArtifacts? Artifacts);

/// <summary>
/// One build in a named container (docs/files-history-builds-design.md, section 4):
/// 1. the container must be running, on the configured platform, and (unless allowed) have no network;
/// 2. sourcePath and artifactPath are emptied; 3. the project is copied in as a tar; 4. the command runs with a time
/// limit, output captured (bounded); 5. on success the artifact folder is published to the host;
/// 6. both folders are emptied again. A timed-out build restarts the container to stop it.
/// Emptying folders is best effort, not erasure: whatever the build copied elsewhere stays in the container.
/// </summary>
public static class BuildRunner
{
    public const int MaxLogBytes = 512 * 1024;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(2);

    public static async Task<BuildOutcome> RunAsync(BuildTarget target, string artifactRoot, Guid projectId, int buildNumber,
        IReadOnlyList<(string Name, byte[] Content)> files, IDockerEngine engine, CancellationToken ct)
    {
        ContainerInfo info;
        try { info = await engine.InspectAsync(target.Container, ct).ConfigureAwait(false); }
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
            var tar = SourceArchive(files);
            try { await engine.PutArchiveAsync(target.Container, target.SourcePath, tar, ct).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(tar.GetBuffer()); tar.Dispose(); } // the tar holds plaintext source
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
            await using var archive = await engine.GetArchiveAsync(target.Container, target.ArtifactPath, ct).ConfigureAwait(false);
            PublishedArtifacts published;
            if (archive is null) { Directory.CreateDirectory(destination); published = new(destination, 0, 0); } // no output folder: nothing to publish
            else
            {
                try { published = await ArtifactPublisher.PublishAsync(archive, folderName, destination, target.MaxArtifactBytes, ct).ConfigureAwait(false); }
                catch (ArtifactRejectedException e) { return new(BuildState.Rejected, "Artifacts" + e.Reason, 0, run.Output, run.Truncated, null); }
            }
            return new(BuildState.Succeeded, null, 0, run.Output, run.Truncated, published);
        }
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

    // Removes and recreates sourcePath and artifactPath inside the container. Paths come from the validated admin config.
    internal static IReadOnlyList<string> CleanupCommand(BuildTarget target) => target.IsWindows
        ? ["cmd.exe", "/d", "/c",
           $"(if exist \"{target.SourcePath}\" rmdir /s /q \"{target.SourcePath}\") & (if exist \"{target.ArtifactPath}\" rmdir /s /q \"{target.ArtifactPath}\") & mkdir \"{target.SourcePath}\" & (if not exist \"{target.ArtifactPath}\" mkdir \"{target.ArtifactPath}\")"]
        : ["/bin/sh", "-c", "rm -rf -- \"$1\" \"$2\" && mkdir -p -- \"$1\" \"$2\"", "sourcevault-clean", target.SourcePath, target.ArtifactPath];

    private static async Task<bool> CleanAsync(BuildTarget target, IDockerEngine engine, CancellationToken ct)
    {
        var result = await engine.ExecAsync(target.Container, CleanupCommand(target), null, StepTimeout, 4096, ct).ConfigureAwait(false);
        return !result.TimedOut && result.ExitCode == 0;
    }

    // The project as an in-memory tar of relative paths ("src/a.ts"); extracted by Docker into sourcePath.
    internal static MemoryStream SourceArchive(IReadOnlyList<(string Name, byte[] Content)> files)
    {
        // Pre-sized so the buffer never grows: a reallocation would leave an unzeroed copy of the source behind.
        var stream = new MemoryStream(files.Sum(f => f.Content.Length + 3072) + 4096);
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            var folders = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, content) in files)
            {
                var parts = name.Split('/');
                for (int i = 1; i < parts.Length; i++)
                {
                    var folder = string.Join('/', parts[..i]) + "/";
                    if (folders.Add(folder)) writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, folder) { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute });
                }
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, DataStream = new MemoryStream(content, writable: false) };
                writer.WriteEntry(entry);
            }
        }
        stream.Position = 0;
        return stream;
    }

    /// <summary>Zeroes the plaintext buffers of a build's files.</summary>
    public static void Zero(IReadOnlyList<(string Name, byte[] Content)> files)
    {
        foreach (var (_, content) in files) CryptographicOperations.ZeroMemory(content);
    }
}
