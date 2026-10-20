using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SourceVault.Client;

public enum LockReason { User, Closed, Expired }

/// <summary>A project. Not <c>Available</c>: it failed its integrity check and is offline (it can only be deleted).</summary>
public sealed record ProjectSummary(Guid ProjectId, string Name, int FileCount, bool Available = true);
/// <summary>A live file. <c>Kind</c> is "text" or "binary"; <c>Sha256</c> (hex) is null for a legacy text record.</summary>
public sealed record FileSummary(Guid FileId, string Name, Guid RevisionId, long ByteLength, string Kind = "text", string? Sha256 = null)
{
    /// <summary>Whether the editor can show it: text of at most 1 MiB. Other files get the info panel.</summary>
    public bool Openable => Kind == "text" && ByteLength <= EditorVaultController.MaxEditorTextBytes;
}
public sealed record OpenedDocument(FileSummary File, string Text);
/// <summary>An info-panel file: its listing and its last save (when, who).</summary>
public sealed record FileDetails(FileSummary File, DateTimeOffset? SavedAt, string? SavedBy);
public sealed record RevisionSummary(Guid RevisionId, DateTimeOffset? SavedAt, string? SavedBy, long ByteLength, string Action, string Name, string Kind = "text", string? Sha256 = null);
public sealed record HistoryListing(IReadOnlyList<RevisionSummary> Revisions, bool More);
public sealed record DeletedSummary(Guid FileId, string Name, long ByteLength, DateTimeOffset DeletedAt, string? DeletedBy, string Kind = "text");
/// <summary>
/// Inputs for a three-way merge: the text the editor's copy is based on, and the current text and revision. Not
/// <c>Mergeable</c> (texts null) when either side is binary or over 1 MiB: the editor then offers keep mine / keep theirs.
/// </summary>
public sealed record MergeSources(string? BaseText, string? TheirsText, Guid TheirsRevision, bool Mergeable = true);
/// <summary>
/// A file the host read from disk (chosen in its own dialog, or dropped). The renderer never sees the path or the bytes.
/// <c>Sha256</c>, when known from a scan, saves the first read; the service still checks it against the uploaded bytes.
/// </summary>
public sealed record DiskFile(string Name, long Length, Func<Stream> Open, byte[]? Sha256 = null);
/// <summary>Files added in several commits: what was added, and the code that stopped it part-way (null: all of it).</summary>
public sealed record BatchAdd(IReadOnlyList<FileSummary> Added, string? Stopped);
/// <summary>A folder imported as a new project: files imported, and the service's full check (decrypted and verified).</summary>
public sealed record ImportOutcome(Guid ProjectId, int Imported, int Verified);
public sealed record BuildSummary(int Number, string Target, string State, string? Reason, int? ExitCode, string? RequestedBy, DateTimeOffset RequestedAt,
    DateTimeOffset? FinishedAt, string? ArtifactFolder, int ArtifactFiles, long ArtifactBytes, bool LogAvailable, bool LogTruncated, int ArtifactsDropped = 0);

/// <summary>One "Search all files" hit: 1-based line and column (UTF-16), the match length, a short preview of the line and where the match starts in it.</summary>
public sealed record SearchMatch(Guid FileId, string Name, int Line, int Column, int Length, string Preview, int PreviewOffset);
public sealed record SearchResult(IReadOnlyList<SearchMatch> Matches, bool Truncated, int FilesSearched, int FilesSkipped = 0);

/// <summary>Outcome of a controller call: a protocol code, or <c>Conflict</c> / <c>Locked</c> / <c>NotOpen</c>.</summary>
public sealed record VaultResult<T>(string Code, T? Value)
{
    public bool Ok => Code == "Ok";
}

/// <summary>
/// The editor host's side of the vault: UI-free, so it is tested against the real service code in process.
/// It owns the connection, capability and every snapshot/revision id; callers (the bridge) only ever see ids,
/// names and text.
///
/// The service's optimistic lock is project-wide. The controller turns that into per-file concurrency: a save
/// succeeds if this file is still at the revision it was opened at, whatever happened to other files. A commit that
/// races in between is retried once. Someone else changing this file is reported as <c>Conflict</c>.
/// </summary>
public sealed class EditorVaultController : IDisposable
{
    /// <summary>Text the editor opens and saves (docs/vault-any-file-design.md section 4).</summary>
    public const int MaxEditorTextBytes = 1024 * 1024;
    /// <summary>Up to this size text goes inline (OpenDocument, ApplyEdits); larger goes in parts.</summary>
    internal const int InlineBytes = SourceVault.Contracts.ProtocolLimits.MaxDocumentBytes;
    internal const int PartBytes = 512 * 1024;
    /// <summary>The service's per-file limit; checked here before reading a file from disk.</summary>
    public const long MaxFileBytes = 1L << 30;
    /// <summary>Files in one "Add files from disk" (one CommitUploads).</summary>
    public const int MaxAddFiles = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Func<CancellationToken, Task<VaultClient>> connect;
    private readonly SemaphoreSlim gate = new(1, 1);
    // (project, file) -> revision the editor's copy is based on. Ids only; kept across locks for the unlock re-check.
    private readonly Dictionary<(Guid, Guid), Guid> opened = [];
    // (project, file) -> a staged replacement from disk that conflicted, kept for "keep mine". The service discards it
    // when the connection or session ends, or after 2 idle minutes.
    private readonly Dictionary<(Guid, Guid), Guid> pending = [];
    private VaultClient? client;

    public EditorVaultController(Func<CancellationToken, Task<VaultClient>> connect) => this.connect = connect;

    public bool IsLocked { get; private set; } = true;
    public event Action<LockReason>? Locked;
    /// <summary>Test seam: runs after a save has checked the file's revision and before it commits.</summary>
    internal Func<Task>? BeforeCommit { get; set; }

    /// <summary>Opens a connection and session. Returns the opened files that changed in the vault while locked.</summary>
    public Task<VaultResult<IReadOnlyList<(Guid ProjectId, Guid FileId)>>> UnlockAsync(CancellationToken ct = default) => Guarded(async () =>
    {
        client?.Dispose();
        client = null;
        VaultResponse begun;
        try
        {
            client = await connect(ct).ConfigureAwait(false);
            begun = await client.BeginAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (UnlockFailure(e) is { } code)
        {
            client?.Dispose(); client = null;
            return new VaultResult<IReadOnlyList<(Guid, Guid)>>(code, null);
        }
        if (!begun.Ok) { client.Dispose(); client = null; return new VaultResult<IReadOnlyList<(Guid, Guid)>>(begun.Code, null); }
        IsLocked = false;
        var changed = new List<(Guid, Guid)>();
        foreach (var project in opened.Keys.Select(k => k.Item1).Distinct().ToList())
        {
            var files = await ListFilesCoreAsync(project, ct).ConfigureAwait(false);
            if (!files.Ok) { if (files.Code == "NotFound") changed.AddRange(opened.Keys.Where(k => k.Item1 == project)); continue; }
            foreach (var key in opened.Keys.Where(k => k.Item1 == project))
            {
                var now = files.Value!.Files.FirstOrDefault(f => f.FileId == key.Item2);
                if (now is null || now.RevisionId != opened[key]) changed.Add(key);
            }
        }
        return new VaultResult<IReadOnlyList<(Guid, Guid)>>("Ok", changed);
    }, unlocking: true);

    /// <summary>Ends the session (user lock). Opened-document ids are kept for the unlock re-check.</summary>
    public async Task LockAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { await EndAsync(LockReason.User, sendEnd: true).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public Task<VaultResult<IReadOnlyList<ProjectSummary>>> ListProjectsAsync(CancellationToken ct = default) => Guarded(async () =>
    {
        var r = await client!.CallAsync("ListProjects", null, null, ct).ConfigureAwait(false);
        if (!r.Ok) return Fail<IReadOnlyList<ProjectSummary>>(r.Code);
        return new("Ok", r.Body.GetProperty("projects").EnumerateArray()
            .Select(p => new ProjectSummary(Guid.Parse(p.GetProperty("projectId").GetString()!), p.GetProperty("name").GetString()!, p.GetProperty("fileCount").GetInt32()))
            .Concat(r.Body.TryGetProperty("unavailable", out var offline) // absent from an older service
                ? offline.EnumerateArray().Select(p => new ProjectSummary(Guid.Parse(p.GetProperty("projectId").GetString()!), p.GetProperty("name").GetString()!, 0, Available: false))
                : [])
            .ToList());
    });

    public Task<VaultResult<ProjectSummary>> CreateProjectAsync(string name, CancellationToken ct = default) => Guarded(async () =>
    {
        var r = await client!.CallAsync("CreateProject", w => w.WriteString("name", name), null, ct).ConfigureAwait(false);
        return r.Ok ? new VaultResult<ProjectSummary>("Ok", new(Guid.Parse(r.Body.GetProperty("projectId").GetString()!), name, 0)) : Fail<ProjectSummary>(r.Code);
    });

    /// <summary>
    /// Deletes a project and every revision in it. <paramref name="confirmName"/> must be the project's exact name.
    /// The value is false when some encrypted files were in use; the service removes them later.
    /// </summary>
    public Task<VaultResult<bool>> DeleteProjectAsync(Guid projectId, string confirmName, CancellationToken ct = default) => Guarded(async () =>
    {
        var r = await client!.CallAsync("DeleteProject", w =>
        {
            w.WriteString("projectId", projectId.ToString("D")); w.WriteString("confirmName", confirmName);
        }, null, ct).ConfigureAwait(false);
        if (!r.Ok) return Fail<bool>(r.Code);
        foreach (var key in opened.Keys.Where(k => k.Item1 == projectId).ToList()) opened.Remove(key);
        return new("Ok", r.Body.GetProperty("filesRemoved").GetBoolean());
    });

    public Task<VaultResult<bool>> RenameProjectAsync(Guid projectId, string newName, CancellationToken ct = default) => Guarded(async () =>
    {
        var r = await client!.CallAsync("RenameProject", w => { w.WriteString("projectId", projectId.ToString("D")); w.WriteString("newName", newName); }, null, ct).ConfigureAwait(false);
        return r.Ok ? new VaultResult<bool>("Ok", true) : Fail<bool>(r.Code);
    });

    /// <summary>Renames or moves a file. Its content and revision are unchanged, so an open copy stays valid.</summary>
    public Task<VaultResult<bool>> RenameFileAsync(Guid projectId, Guid fileId, string newName, CancellationToken ct = default) => Guarded(() =>
        ProjectChangeAsync(projectId, "RenameFile", null, w => { w.WriteString("fileId", fileId.ToString("D")); w.WriteString("newName", newName); }, _ => true, ct));

    /// <summary>Renames every file under a folder. Returns how many files moved.</summary>
    public Task<VaultResult<int>> RenameFolderAsync(Guid projectId, string oldPrefix, string newPrefix, CancellationToken ct = default) => Guarded(() =>
        ProjectChangeAsync(projectId, "RenameFolder", null, w => { w.WriteString("oldPrefix", oldPrefix); w.WriteString("newPrefix", newPrefix); },
            body => body.GetProperty("renamed").GetInt32(), ct));

    /// <summary>Deletes a file. <c>Conflict</c>: it is open here and someone else changed it since it was opened.</summary>
    public Task<VaultResult<bool>> DeleteFileAsync(Guid projectId, Guid fileId, CancellationToken ct = default) => Guarded(async () =>
    {
        var result = await ProjectChangeAsync(projectId, "DeleteFile", files =>
        {
            var file = files.FirstOrDefault(f => f.FileId == fileId);
            if (file is null) return "NotFound";
            return opened.TryGetValue((projectId, fileId), out var basedOn) && file.RevisionId != basedOn ? "Conflict" : null;
        }, w => w.WriteString("fileId", fileId.ToString("D")), _ => true, ct).ConfigureAwait(false);
        if (result.Ok) { opened.Remove((projectId, fileId)); await DiscardPendingAsync(projectId, fileId, ct).ConfigureAwait(false); }
        return result;
    });

    /// <summary>One page of a file's history (live or deleted), newest first.</summary>
    public Task<VaultResult<HistoryListing>> ListHistoryAsync(Guid projectId, Guid fileId, int page, CancellationToken ct = default) => Guarded(async () =>
    {
        var r = await client!.CallAsync("ListHistory", w => { w.WriteString("projectId", projectId.ToString("D")); w.WriteString("fileId", fileId.ToString("D")); w.WriteNumber("page", page); }, null, ct).ConfigureAwait(false);
        if (!r.Ok) return Fail<HistoryListing>(r.Code);
        return new("Ok", new HistoryListing(r.Body.GetProperty("revisions").EnumerateArray().Select(e => new RevisionSummary(
            Guid.Parse(e.GetProperty("revisionId").GetString()!),
            e.GetProperty("savedAt").ValueKind == JsonValueKind.Null ? null : DateTimeOffset.Parse(e.GetProperty("savedAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            e.GetProperty("savedBy").ValueKind == JsonValueKind.Null ? null : e.GetProperty("savedBy").GetString(),
            e.GetProperty("byteLength").GetInt64(), e.GetProperty("action").GetString()!, e.GetProperty("name").GetString()!, KindOf(e), Sha256Of(e))).ToList(),
            r.Body.GetProperty("more").GetBoolean()));
    });

    // "kind" and "sha256" are absent from an older service, whose files are all text.
    private static string KindOf(JsonElement e) => e.TryGetProperty("kind", out var k) && k.GetString() == "binary" ? "binary" : "text";
    private static string? Sha256Of(JsonElement e) => e.TryGetProperty("sha256", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;

    /// <summary>The text of one revision from a file's history.</summary>
    public Task<VaultResult<string>> OpenRevisionAsync(Guid projectId, Guid fileId, Guid revisionId, CancellationToken ct = default) => Guarded(() =>
        OpenRevisionCoreAsync(projectId, fileId, revisionId, ct));

    private async Task<VaultResult<string>> OpenRevisionCoreAsync(Guid projectId, Guid fileId, Guid revisionId, CancellationToken ct)
    {
        var r = await client!.CallAsync("OpenRevision", w =>
        {
            w.WriteString("projectId", projectId.ToString("D")); w.WriteString("fileId", fileId.ToString("D")); w.WriteString("revisionId", revisionId.ToString("D"));
        }, null, ct).ConfigureAwait(false);
        if (r.Code == "LimitExceeded") return await ReadTextPartsAsync(projectId, fileId, revisionId, ct).ConfigureAwait(false); // over 256 KiB
        if (!r.Ok) return Fail<string>(r.Code);
        var bytes = r.Body.GetProperty("contentBase64").GetBytesFromBase64();
        try { return new("Ok", StrictUtf8.GetString(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    // Text of 256 KiB to 1 MiB, one 512 KiB segment per request (ReadTextPart). Binary is NotText; larger, LimitExceeded.
    private async Task<VaultResult<string>> ReadTextPartsAsync(Guid projectId, Guid fileId, Guid revisionId, CancellationToken ct)
    {
        byte[]? all = null;
        try
        {
            long offset = 0;
            do
            {
                var r = await client!.CallAsync("ReadTextPart", w =>
                {
                    w.WriteString("projectId", projectId.ToString("D")); w.WriteString("fileId", fileId.ToString("D"));
                    w.WriteString("revisionId", revisionId.ToString("D")); w.WriteNumber("offset", offset);
                }, null, ct).ConfigureAwait(false);
                if (!r.Ok) return Fail<string>(r.Code);
                long length = r.Body.GetProperty("length").GetInt64();
                if (length > MaxEditorTextBytes || (all is not null && length != all.Length)) return Fail<string>("LimitExceeded");
                all ??= new byte[length];
                var part = r.Body.GetProperty("dataBase64").GetBytesFromBase64();
                try
                {
                    if (part.Length == 0 && length > 0 || offset + part.Length > length) return Fail<string>("Unavailable");
                    part.CopyTo(all, offset);
                    offset += part.Length;
                }
                finally { CryptographicOperations.ZeroMemory(part); }
            }
            while (offset < all.Length);
            return new("Ok", StrictUtf8.GetString(all));
        }
        catch (DecoderFallbackException) { return Fail<string>("NotText"); }
        finally { if (all is not null) CryptographicOperations.ZeroMemory(all); }
    }

    /// <summary>Plain-text search across the project's saved files, done by the service (at most 500 matches).</summary>
    public Task<VaultResult<SearchResult>> SearchProjectAsync(Guid projectId, string query, bool matchCase, bool wholeWord, CancellationToken ct = default) => Guarded(async () =>
    {
        var r = await client!.CallAsync("SearchProject", w =>
        {
            w.WriteString("projectId", projectId.ToString("D")); w.WriteString("query", query);
            w.WriteBoolean("matchCase", matchCase); w.WriteBoolean("wholeWord", wholeWord);
        }, null, ct).ConfigureAwait(false);
        if (!r.Ok) return Fail<SearchResult>(r.Code);
        return new("Ok", new SearchResult(r.Body.GetProperty("matches").EnumerateArray().Select(e => new SearchMatch(
            Guid.Parse(e.GetProperty("fileId").GetString()!), e.GetProperty("name").GetString()!, e.GetProperty("line").GetInt32(),
            e.GetProperty("column").GetInt32(), e.GetProperty("length").GetInt32(), e.GetProperty("preview").GetString()!, e.GetProperty("previewOffset").GetInt32())).ToList(),
            r.Body.GetProperty("truncated").GetBoolean(), r.Body.GetProperty("filesSearched").GetInt32(),
            r.Body.TryGetProperty("filesSkipped", out var skipped) ? skipped.GetInt32() : 0)); // binary or over 16 MiB
    });

    public Task<VaultResult<IReadOnlyList<DeletedSummary>>> ListDeletedAsync(Guid projectId, CancellationToken ct = default) => Guarded(async () =>
    {
        var r = await client!.CallAsync("ListDeletedFiles", w => w.WriteString("projectId", projectId.ToString("D")), null, ct).ConfigureAwait(false);
        if (!r.Ok) return Fail<IReadOnlyList<DeletedSummary>>(r.Code);
        return new("Ok", r.Body.GetProperty("files").EnumerateArray().Select(e => new DeletedSummary(
            Guid.Parse(e.GetProperty("fileId").GetString()!), e.GetProperty("name").GetString()!, e.GetProperty("byteLength").GetInt64(),
            DateTimeOffset.Parse(e.GetProperty("deletedAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            e.GetProperty("deletedBy").ValueKind == JsonValueKind.Null ? null : e.GetProperty("deletedBy").GetString(), KindOf(e))).ToList());
    });

    /// <summary>Restores a deleted file under <paramref name="name"/> (its old name or a new one).</summary>
    public Task<VaultResult<bool>> RestoreFileAsync(Guid projectId, Guid fileId, string name, CancellationToken ct = default) => Guarded(() =>
        ProjectChangeAsync(projectId, "RestoreFile", null, w => { w.WriteString("fileId", fileId.ToString("D")); w.WriteString("newName", name); }, _ => true, ct));

    /// <summary>
    /// For a three-way merge after a conflict: the text this editor's copy is based on, and the file's current text and
    /// revision ("theirs"). Nothing changes until <see cref="SaveMergedAsync"/> is called with that revision.
    /// </summary>
    public Task<VaultResult<MergeSources>> MergeSourcesAsync(Guid projectId, Guid fileId, CancellationToken ct = default) => Guarded(async () =>
    {
        if (!opened.TryGetValue((projectId, fileId), out var basedOn)) return Fail<MergeSources>("NotOpen");
        var list = await ListFilesCoreAsync(projectId, ct).ConfigureAwait(false);
        if (!list.Ok) return Fail<MergeSources>(list.Code);
        var file = list.Value!.Files.FirstOrDefault(f => f.FileId == fileId);
        if (file is null) return Fail<MergeSources>("NotFound");
        // Binary or over 1 MiB on either side: no text merge, only keep mine (save over theirs) or keep theirs.
        var notMergeable = new VaultResult<MergeSources>("Ok", new MergeSources(null, null, file.RevisionId, Mergeable: false));
        if (!file.Openable) return notMergeable;
        var baseText = await OpenRevisionCoreAsync(projectId, fileId, basedOn, ct).ConfigureAwait(false);
        if (baseText.Code is "NotText" or "LimitExceeded") return notMergeable;
        if (!baseText.Ok) return Fail<MergeSources>(baseText.Code);
        var theirs = await OpenRevisionCoreAsync(projectId, fileId, file.RevisionId, ct).ConfigureAwait(false);
        if (!theirs.Ok) return Fail<MergeSources>(theirs.Code);
        return new("Ok", new MergeSources(baseText.Value!, theirs.Value!, file.RevisionId));
    });

    /// <summary>
    /// Saves a merge result on top of <paramref name="theirsRevision"/>. <c>Conflict</c>: the file changed again since
    /// the merge sources were fetched. On success the editor's copy is based on the new revision.
    /// </summary>
    public Task<VaultResult<FileSummary>> SaveMergedAsync(Guid projectId, Guid fileId, string text, Guid theirsRevision, CancellationToken ct = default) => Guarded(async () =>
    {
        if (!opened.ContainsKey((projectId, fileId))) return Fail<FileSummary>("NotOpen");
        return await SaveCoreAsync(projectId, fileId, text, theirsRevision, ct).ConfigureAwait(false);
    });

    // ---- builds ---------------------------------------------------------------------------------------------------
    public Task<VaultResult<IReadOnlyList<string>>> ListBuildTargetsAsync(CancellationToken ct = default) => Guarded(async () =>
    {
        var r = await client!.CallAsync("ListBuildTargets", null, null, ct).ConfigureAwait(false);
        return r.Ok ? new VaultResult<IReadOnlyList<string>>("Ok", r.Body.GetProperty("targets").EnumerateArray().Select(t => t.GetString()!).ToList()) : Fail<IReadOnlyList<string>>(r.Code);
    });

    /// <summary>Queues a build of the project's current saved files. Returns the build number.</summary>
    public Task<VaultResult<int>> RequestBuildAsync(Guid projectId, string target, CancellationToken ct = default) => Guarded(async () =>
    {
        var r = await client!.CallAsync("RequestBuild", w => { w.WriteString("projectId", projectId.ToString("D")); w.WriteString("target", target); }, null, ct).ConfigureAwait(false);
        return r.Ok ? new VaultResult<int>("Ok", r.Body.GetProperty("buildNumber").GetInt32()) : Fail<int>(r.Code);
    });

    public Task<VaultResult<IReadOnlyList<BuildSummary>>> ListBuildsAsync(Guid projectId, CancellationToken ct = default) => Guarded(async () =>
    {
        var r = await client!.CallAsync("ListBuilds", w => w.WriteString("projectId", projectId.ToString("D")), null, ct).ConfigureAwait(false);
        return r.Ok ? new VaultResult<IReadOnlyList<BuildSummary>>("Ok", r.Body.GetProperty("builds").EnumerateArray().Select(ReadBuild).ToList()) : Fail<IReadOnlyList<BuildSummary>>(r.Code);
    });

    public Task<VaultResult<BuildSummary>> GetBuildStatusAsync(Guid projectId, int buildNumber, CancellationToken ct = default) => Guarded(async () =>
    {
        var r = await client!.CallAsync("GetBuildStatus", w => { w.WriteString("projectId", projectId.ToString("D")); w.WriteNumber("buildNumber", buildNumber); }, null, ct).ConfigureAwait(false);
        return r.Ok ? new VaultResult<BuildSummary>("Ok", ReadBuild(r.Body)) : Fail<BuildSummary>(r.Code);
    });

    /// <summary>A finished build's output. It may contain source text, like any open file.</summary>
    public Task<VaultResult<string>> OpenBuildLogAsync(Guid projectId, int buildNumber, CancellationToken ct = default) => Guarded(async () =>
    {
        var r = await client!.CallAsync("OpenBuildLog", w => { w.WriteString("projectId", projectId.ToString("D")); w.WriteNumber("buildNumber", buildNumber); }, null, ct).ConfigureAwait(false);
        if (!r.Ok) return Fail<string>(r.Code);
        var bytes = r.Body.GetProperty("contentBase64").GetBytesFromBase64();
        try { return new VaultResult<string>("Ok", Encoding.UTF8.GetString(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    });

    private static BuildSummary ReadBuild(JsonElement e)
    {
        static string? S(JsonElement e, string n) => e.GetProperty(n).ValueKind == JsonValueKind.String ? e.GetProperty(n).GetString() : null;
        static DateTimeOffset? T(JsonElement e, string n) => S(e, n) is { } t ? DateTimeOffset.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : null;
        return new BuildSummary(e.GetProperty("buildNumber").GetInt32(), S(e, "target")!, S(e, "state")!, S(e, "reason"),
            e.GetProperty("exitCode").ValueKind == JsonValueKind.Number ? e.GetProperty("exitCode").GetInt32() : null, S(e, "requestedBy"),
            T(e, "requestedAt")!.Value, T(e, "finishedAt"), S(e, "artifactFolder"), e.GetProperty("artifactFiles").GetInt32(), e.GetProperty("artifactBytes").GetInt64(),
            e.GetProperty("logAvailable").GetBoolean(), e.GetProperty("logTruncated").GetBoolean(),
            e.TryGetProperty("artifactsDropped", out var dropped) ? dropped.GetInt32() : 0); // absent from an older service
    }

    // List, optionally check the listing, then commit against that snapshot; a commit racing in between is retried once.
    private async Task<VaultResult<T>> ProjectChangeAsync<T>(Guid projectId, string operation, Func<IReadOnlyList<FileSummary>, string?>? check,
        Action<Utf8JsonWriter> fields, Func<JsonElement, T> result, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var list = await ListFilesCoreAsync(projectId, ct).ConfigureAwait(false);
            if (!list.Ok) return Fail<T>(list.Code);
            if (check?.Invoke(list.Value!.Files) is { } refused) return Fail<T>(refused);
            var r = await client!.CallAsync(operation, w =>
            {
                w.WriteString("projectId", projectId.ToString("D")); fields(w);
                w.WriteString("expectedSnapshotId", list.Value!.SnapshotId.ToString("D"));
            }, null, ct).ConfigureAwait(false);
            if (r.Code == "RevisionConflict") continue;
            return r.Ok ? new VaultResult<T>("Ok", result(r.Body)) : Fail<T>(r.Code);
        }
        return Fail<T>("RevisionConflict");
    }

    public Task<VaultResult<IReadOnlyList<FileSummary>>> ListFilesAsync(Guid projectId, CancellationToken ct = default) => Guarded(async () =>
    {
        var r = await ListFilesCoreAsync(projectId, ct).ConfigureAwait(false);
        return r.Ok ? new VaultResult<IReadOnlyList<FileSummary>>("Ok", r.Value!.Files) : Fail<IReadOnlyList<FileSummary>>(r.Code);
    });

    public Task<VaultResult<OpenedDocument>> OpenDocumentAsync(Guid projectId, Guid fileId, CancellationToken ct = default) => Guarded(async () =>
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var list = await ListFilesCoreAsync(projectId, ct).ConfigureAwait(false);
            if (!list.Ok) return Fail<OpenedDocument>(list.Code);
            var file = list.Value!.Files.FirstOrDefault(f => f.FileId == fileId);
            if (file is null) return Fail<OpenedDocument>("NotFound");
            if (file.Kind != "text") return Fail<OpenedDocument>("NotText");
            if (file.ByteLength > MaxEditorTextBytes) return Fail<OpenedDocument>("LimitExceeded");
            if (file.ByteLength > InlineBytes)
            {
                // The revision id pins the content, so no snapshot check is needed between the parts.
                var text = await ReadTextPartsAsync(projectId, fileId, file.RevisionId, ct).ConfigureAwait(false);
                if (!text.Ok) return Fail<OpenedDocument>(text.Code);
                opened[(projectId, fileId)] = file.RevisionId;
                return new("Ok", new OpenedDocument(file, text.Value!));
            }
            var r = await client!.CallAsync("OpenDocument", w =>
            {
                w.WriteString("projectId", projectId.ToString("D")); w.WriteString("fileId", fileId.ToString("D"));
                w.WriteString("expectedSnapshotId", list.Value.SnapshotId.ToString("D"));
            }, null, ct).ConfigureAwait(false);
            if (r.Code == "RevisionConflict") continue; // another commit raced in; list again
            if (!r.Ok) return Fail<OpenedDocument>(r.Code);
            var bytes = r.Body.GetProperty("contentBase64").GetBytesFromBase64();
            try
            {
                opened[(projectId, fileId)] = file.RevisionId;
                return new("Ok", new OpenedDocument(file, StrictUtf8.GetString(bytes)));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        return Fail<OpenedDocument>("RevisionConflict");
    });

    /// <summary>Saves a whole-file replacement of an opened document. <c>Conflict</c>: someone else changed it.</summary>
    public Task<VaultResult<FileSummary>> SaveDocumentAsync(Guid projectId, Guid fileId, string text, CancellationToken ct = default) => Guarded(async () =>
    {
        if (!opened.TryGetValue((projectId, fileId), out var basedOn)) return Fail<FileSummary>("NotOpen");
        return await SaveCoreAsync(projectId, fileId, text, basedOn, ct).ConfigureAwait(false);
    });

    // Saves only if the file is still at basedOn: per-file concurrency over the service's project-wide snapshot.
    private async Task<VaultResult<FileSummary>> SaveCoreAsync(Guid projectId, Guid fileId, string text, Guid basedOn, CancellationToken ct)
    {
        var bytes = Encode(text, out var code);
        if (bytes is null) return Fail<FileSummary>(code);
        try
        {
            if (bytes.Length > InlineBytes)
            {
                // Over 256 KiB: upload in parts, then commit it as a replacement if this file is still at basedOn.
                var check = await ListFilesCoreAsync(projectId, ct).ConfigureAwait(false);
                if (!check.Ok) return Fail<FileSummary>(check.Code);
                var current = check.Value!.Files.FirstOrDefault(f => f.FileId == fileId);
                if (current is null) return Fail<FileSummary>("NotFound");
                if (current.RevisionId != basedOn) return Fail<FileSummary>("Conflict");
                var upload = await UploadAsync(projectId, fileId, bytes.Length, SHA256.HashData(bytes), () => new MemoryStream(bytes, writable: false), ct).ConfigureAwait(false);
                if (!upload.Ok) return Fail<FileSummary>(upload.Code);
                var saved = await CommitReplacementAsync(projectId, fileId, upload.Value!, basedOn, ct).ConfigureAwait(false);
                if (!saved.Ok) await CancelUploadAsync(upload.Value!.Id, ct).ConfigureAwait(false);
                return saved;
            }
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var list = await ListFilesCoreAsync(projectId, ct).ConfigureAwait(false);
                if (!list.Ok) return Fail<FileSummary>(list.Code);
                var file = list.Value!.Files.FirstOrDefault(f => f.FileId == fileId);
                if (file is null) return Fail<FileSummary>("NotFound");
                if (file.RevisionId != basedOn) return Fail<FileSummary>("Conflict");
                if (BeforeCommit is not null) await BeforeCommit().ConfigureAwait(false);
                var r = await client!.CallAsync("ApplyEdits", w =>
                {
                    w.WriteString("projectId", projectId.ToString("D")); w.WriteString("fileId", fileId.ToString("D"));
                    w.WriteString("expectedSnapshotId", list.Value.SnapshotId.ToString("D")); w.WriteBase64String("contentBase64", bytes);
                }, null, ct).ConfigureAwait(false);
                if (r.Code == "RevisionConflict") continue; // something else in the project committed; re-check this file
                if (!r.Ok) return Fail<FileSummary>(r.Code);
                var revision = Guid.Parse(r.Body.GetProperty("revisionId").GetString()!);
                opened[(projectId, fileId)] = revision;
                return new("Ok", file with { RevisionId = revision, ByteLength = bytes.Length, Kind = "text", Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) });
            }
            return Fail<FileSummary>("RevisionConflict");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>Creates a file and treats it as opened at its first revision.</summary>
    public Task<VaultResult<FileSummary>> CreateFileAsync(Guid projectId, string name, string text, CancellationToken ct = default) => Guarded(async () =>
    {
        var bytes = Encode(text, out var code);
        if (bytes is null) return Fail<FileSummary>(code);
        try
        {
            if (bytes.Length > InlineBytes)
            {
                var added = await AddCoreAsync(projectId, [(name, new DiskFile(name, bytes.Length, () => new MemoryStream(bytes, writable: false)))], ct).ConfigureAwait(false);
                if (!added.Ok) return Fail<FileSummary>(added.Code);
                opened[(projectId, added.Value![0].FileId)] = added.Value[0].RevisionId;
                return new("Ok", added.Value[0]);
            }
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var list = await ListFilesCoreAsync(projectId, ct).ConfigureAwait(false);
                if (!list.Ok) return Fail<FileSummary>(list.Code);
                var r = await client!.CallAsync("CreateFile", w =>
                {
                    w.WriteString("projectId", projectId.ToString("D")); w.WriteString("name", name);
                    w.WriteString("expectedSnapshotId", list.Value!.SnapshotId.ToString("D")); w.WriteBase64String("contentBase64", bytes);
                }, null, ct).ConfigureAwait(false);
                if (r.Code == "RevisionConflict") continue;
                if (!r.Ok) return Fail<FileSummary>(r.Code);
                var summary = new FileSummary(Guid.Parse(r.Body.GetProperty("fileId").GetString()!), name,
                    Guid.Parse(r.Body.GetProperty("revisionId").GetString()!), bytes.Length);
                opened[(projectId, summary.FileId)] = summary.RevisionId;
                return new("Ok", summary);
            }
            return Fail<FileSummary>("RevisionConflict");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    });

    // ---- files the editor doesn't show (binary, or text over 1 MiB), and files from disk -----------------------------

    /// <summary>
    /// The info panel's file: its listing and its last save. Like opening a document, this records the revision the
    /// panel shows, so a replacement or revert is a <c>Conflict</c> if someone else changed the file meanwhile.
    /// </summary>
    public Task<VaultResult<FileDetails>> FileInfoAsync(Guid projectId, Guid fileId, CancellationToken ct = default) => Guarded(async () =>
    {
        var list = await ListFilesCoreAsync(projectId, ct).ConfigureAwait(false);
        if (!list.Ok) return Fail<FileDetails>(list.Code);
        var file = list.Value!.Files.FirstOrDefault(f => f.FileId == fileId);
        if (file is null) return Fail<FileDetails>("NotFound");
        var history = await client!.CallAsync("ListHistory", w => { w.WriteString("projectId", projectId.ToString("D")); w.WriteString("fileId", fileId.ToString("D")); w.WriteNumber("page", 0); }, null, ct).ConfigureAwait(false);
        if (!history.Ok) return Fail<FileDetails>(history.Code);
        DateTimeOffset? savedAt = null; string? savedBy = null;
        foreach (var last in history.Body.GetProperty("revisions").EnumerateArray().Take(1))
        {
            if (last.GetProperty("savedAt").ValueKind == JsonValueKind.String) savedAt = DateTimeOffset.Parse(last.GetProperty("savedAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            if (last.GetProperty("savedBy").ValueKind == JsonValueKind.String) savedBy = last.GetProperty("savedBy").GetString();
        }
        opened[(projectId, fileId)] = file.RevisionId;
        return new("Ok", new FileDetails(file, savedAt, savedBy));
    });

    /// <summary>
    /// Replaces a file's content with a file the host read from disk. <c>Conflict</c>: someone else changed it since
    /// the editor showed it; the upload stays staged for <see cref="KeepMineAsync"/> (or <see cref="KeepTheirsAsync"/>).
    /// </summary>
    public Task<VaultResult<FileSummary>> ReplaceFromDiskAsync(Guid projectId, Guid fileId, DiskFile source, CancellationToken ct = default) => Guarded(async () =>
    {
        if (source.Length > MaxFileBytes) return Fail<FileSummary>("LimitExceeded");
        await DiscardPendingAsync(projectId, fileId, ct).ConfigureAwait(false);
        Guid? basedOn = opened.TryGetValue((projectId, fileId), out var b) ? b : null;
        var list = await ListFilesCoreAsync(projectId, ct).ConfigureAwait(false);
        if (!list.Ok) return Fail<FileSummary>(list.Code);
        if (list.Value!.Files.All(f => f.FileId != fileId)) return Fail<FileSummary>("NotFound");
        var upload = await UploadFromDiskAsync(projectId, fileId, source, ct).ConfigureAwait(false);
        if (!upload.Ok) return Fail<FileSummary>(upload.Code);
        var saved = await CommitReplacementAsync(projectId, fileId, upload.Value!, basedOn, ct).ConfigureAwait(false);
        if (saved.Code == "Conflict") pending[(projectId, fileId)] = upload.Value!.Id;
        else if (!saved.Ok) await CancelUploadAsync(upload.Value!.Id, ct).ConfigureAwait(false);
        return saved;
    });

    /// <summary>After a replacement conflicted: commits the staged upload over their version (theirs stays in history).</summary>
    public Task<VaultResult<FileSummary>> KeepMineAsync(Guid projectId, Guid fileId, CancellationToken ct = default) => Guarded(async () =>
    {
        if (!pending.Remove((projectId, fileId), out var uploadId)) return Fail<FileSummary>("UploadExpired");
        var saved = await CommitReplacementAsync(projectId, fileId, new Upload(uploadId, null, null), null, ct).ConfigureAwait(false);
        if (!saved.Ok) await CancelUploadAsync(uploadId, ct).ConfigureAwait(false);
        return saved;
    });

    /// <summary>After a replacement conflicted: discards the staged upload and takes their version as the one shown.</summary>
    public Task<VaultResult<FileSummary>> KeepTheirsAsync(Guid projectId, Guid fileId, CancellationToken ct = default) => Guarded(async () =>
    {
        await DiscardPendingAsync(projectId, fileId, ct).ConfigureAwait(false);
        var list = await ListFilesCoreAsync(projectId, ct).ConfigureAwait(false);
        if (!list.Ok) return Fail<FileSummary>(list.Code);
        var file = list.Value!.Files.FirstOrDefault(f => f.FileId == fileId);
        if (file is null) return Fail<FileSummary>("NotFound");
        opened[(projectId, fileId)] = file.RevisionId;
        return new("Ok", file);
    });

    /// <summary>
    /// Adds files the host read from disk under <paramref name="folder"/> ("" for the project root), all in one commit.
    /// A taken name is <c>AlreadyExists</c> and nothing is added.
    /// </summary>
    public Task<VaultResult<IReadOnlyList<FileSummary>>> AddFromDiskAsync(Guid projectId, string folder, IReadOnlyList<DiskFile> files, CancellationToken ct = default) => Guarded(async () =>
    {
        if (files.Count is 0 or > MaxAddFiles || files.Any(f => f.Length > MaxFileBytes)) return Fail<IReadOnlyList<FileSummary>>("LimitExceeded");
        var named = files.Select(f => (folder.Length == 0 ? f.Name : folder + "/" + f.Name, f)).ToList();
        var added = await AddCoreAsync(projectId, named, ct).ConfigureAwait(false);
        return added.Ok ? new VaultResult<IReadOnlyList<FileSummary>>("Ok", added.Value) : Fail<IReadOnlyList<FileSummary>>(added.Code);
    });

    /// <summary>
    /// Adds any number of files (dropped files and folders) in commits of up to <see cref="MaxAddFiles"/>. A failure
    /// part-way keeps what was already committed and reports it with the code that stopped it.
    /// <paramref name="progress"/> receives the bytes uploaded so far.
    /// </summary>
    public Task<VaultResult<BatchAdd>> AddManyAsync(Guid projectId, IReadOnlyList<(string Name, DiskFile File)> files, Action<long>? progress = null, CancellationToken ct = default) => Guarded(() =>
        AddManyCoreAsync(projectId, files, progress, ct));

    private async Task<VaultResult<BatchAdd>> AddManyCoreAsync(Guid projectId, IReadOnlyList<(string Name, DiskFile File)> files, Action<long>? progress, CancellationToken ct)
    {
        if (files.Count == 0 || files.Any(f => f.File.Length > MaxFileBytes)) return Fail<BatchAdd>("LimitExceeded");
        var added = new List<FileSummary>();
        long done = 0;
        foreach (var batch in files.Chunk(MaxAddFiles))
        {
            var result = await AddCoreAsync(projectId, batch, n => progress?.Invoke(done + n), ct).ConfigureAwait(false);
            if (!result.Ok) return added.Count == 0 ? Fail<BatchAdd>(result.Code) : new("Ok", new BatchAdd(added, result.Code));
            added.AddRange(result.Value!);
            done += batch.Sum(f => f.File.Length);
        }
        return new("Ok", new BatchAdd(added, null));
    }

    /// <summary>
    /// Imports scanned files as a new project named <paramref name="projectName"/>, like the importer: every file
    /// uploaded, every stored SHA-256 compared with the scan, then the service decrypts and checks every record
    /// (VerifyProject). Any failure deletes the new project again, so nothing half-imported is left behind.
    /// </summary>
    public Task<VaultResult<ImportOutcome>> ImportProjectAsync(string projectName, IReadOnlyList<(string Name, DiskFile File)> files, Action<long>? progress = null, CancellationToken ct = default) => Guarded(async () =>
    {
        if (files.Count == 0) return Fail<ImportOutcome>("InvalidRequest");
        var created = await client!.CallAsync("CreateProject", w => w.WriteString("name", projectName), null, ct).ConfigureAwait(false);
        if (!created.Ok) return Fail<ImportOutcome>(created.Code);
        var projectId = Guid.Parse(created.Body.GetProperty("projectId").GetString()!);
        string? failure;
        int verified = 0;
        var added = await AddManyCoreAsync(projectId, files, progress, ct).ConfigureAwait(false);
        if (!added.Ok) failure = added.Code;
        else if (added.Value!.Stopped is { } stopped) failure = stopped;
        else
        {
            failure = null;
            var listing = await ListFilesCoreAsync(projectId, ct).ConfigureAwait(false);
            if (!listing.Ok) failure = listing.Code;
            else
            {
                var stored = listing.Value!.Files.ToDictionary(f => f.Name, f => f.Sha256, StringComparer.OrdinalIgnoreCase);
                foreach (var (name, file) in files)
                    if (file.Sha256 is { } sha && (!stored.TryGetValue(name, out var hex) || hex != Convert.ToHexStringLower(sha))) { failure = "Unavailable"; break; }
            }
            if (failure is null)
            {
                var check = await client!.CallAsync("VerifyProject", w => w.WriteString("projectId", projectId.ToString("D")), null, ct).ConfigureAwait(false);
                if (!check.Ok) failure = check.Code;
                else if (check.Body.GetProperty("filesFailed").GetInt32() > 0) failure = "Unavailable";
                else verified = check.Body.GetProperty("filesVerified").GetInt32();
            }
        }
        if (failure is null) return new("Ok", new ImportOutcome(projectId, files.Count, verified));
        // All or nothing for the user: remove the half-imported project (its name is the confirmation).
        await client!.CallAsync("DeleteProject", w => { w.WriteString("projectId", projectId.ToString("D")); w.WriteString("confirmName", projectName); }, null, ct).ConfigureAwait(false);
        return Fail<ImportOutcome>(failure);
    });

    /// <summary>
    /// Makes a revision from the file's history current again (nothing is re-encrypted or sent here). <c>Conflict</c>:
    /// the editor shows this file and someone else changed it since.
    /// </summary>
    public Task<VaultResult<bool>> RevertFileAsync(Guid projectId, Guid fileId, Guid revisionId, CancellationToken ct = default) => Guarded(async () =>
    {
        var result = await ProjectChangeAsync(projectId, "RevertFile", files =>
        {
            var file = files.FirstOrDefault(f => f.FileId == fileId);
            if (file is null) return "NotFound";
            return opened.TryGetValue((projectId, fileId), out var basedOn) && file.RevisionId != basedOn ? "Conflict" : null;
        }, w => { w.WriteString("fileId", fileId.ToString("D")); w.WriteString("revisionId", revisionId.ToString("D")); }, _ => true, ct).ConfigureAwait(false);
        if (result.Ok && opened.ContainsKey((projectId, fileId))) opened[(projectId, fileId)] = revisionId; // the file now points at that record
        return result;
    });

    /// <summary>Forgets an opened document (tab closed) or info panel: it is no longer saved or re-checked.</summary>
    public void Close(Guid projectId, Guid fileId)
    {
        gate.Wait();
        try { opened.Remove((projectId, fileId)); pending.Remove((projectId, fileId)); } finally { gate.Release(); } // the service discards a staged upload after 2 idle minutes
    }

    private sealed record Upload(Guid Id, long? Length, string? Sha256);

    // A file read failure on disk, kept apart from pipe failures (IOException), which lock the session.
    private sealed class DiskReadException : Exception;

    private async Task<VaultResult<Upload>> UploadFromDiskAsync(Guid projectId, Guid? fileId, DiskFile source, CancellationToken ct, Action<long>? progress = null)
    {
        byte[] sha256;
        if (source.Sha256 is { Length: 32 } known) sha256 = known; // from the scan; the service checks the uploaded bytes against it
        else
        {
            try { sha256 = await Task.Run(() => HashDisk(source), ct).ConfigureAwait(false); }
            catch (DiskReadException) { return Fail<Upload>("FileUnreadable"); }
        }
        return await UploadAsync(projectId, fileId, source.Length, sha256, source.Open, ct, progress).ConfigureAwait(false);
    }

    // First pass over the file: its SHA-256, which the service checks against the bytes of the second pass.
    private static byte[] HashDisk(DiskFile source)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[PartBytes];
        try
        {
            using var stream = OpenDisk(source.Open);
            long total = 0;
            int n;
            while ((n = ReadDisk(stream, buffer, buffer.Length)) > 0) { hash.AppendData(buffer, 0, n); total += n; }
            if (total != source.Length) throw new DiskReadException(); // changed since it was chosen
            return hash.GetHashAndReset();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static Stream OpenDisk(Func<Stream> open)
    {
        try { return open(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { throw new DiskReadException(); }
    }

    // Reads up to count bytes (fewer only at the end of the file).
    private static int ReadDisk(Stream stream, byte[] buffer, int count)
    {
        try
        {
            int total = 0, n;
            while (total < count && (n = stream.Read(buffer, total, count - total)) > 0) total += n;
            return total;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new DiskReadException(); }
    }

    // BeginUpload and its parts. The service checks the SHA-256 at the last byte; a file changed meanwhile is SourceChanged.
    private async Task<VaultResult<Upload>> UploadAsync(Guid projectId, Guid? fileId, long length, byte[] sha256, Func<Stream> open, CancellationToken ct, Action<long>? progress = null)
    {
        var hex = Convert.ToHexStringLower(sha256);
        var begun = await client!.CallAsync("BeginUpload", w =>
        {
            w.WriteString("projectId", projectId.ToString("D"));
            if (fileId is { } id) w.WriteString("fileId", id.ToString("D")); else w.WriteNull("fileId");
            w.WriteNumber("length", length); w.WriteString("sha256", hex);
        }, null, ct).ConfigureAwait(false);
        if (!begun.Ok) return Fail<Upload>(begun.Code);
        var uploadId = Guid.Parse(begun.Body.GetProperty("uploadId").GetString()!);
        int partBytes = Math.Clamp(begun.Body.GetProperty("partBytes").GetInt32(), 1, PartBytes);
        if (length == 0) return new("Ok", new Upload(uploadId, length, hex));
        var buffer = new byte[partBytes];
        try
        {
            using var stream = OpenDisk(open);
            long offset = 0;
            while (offset < length)
            {
                int n = ReadDisk(stream, buffer, (int)Math.Min(partBytes, length - offset));
                if (n == 0) { await CancelUploadAsync(uploadId, ct).ConfigureAwait(false); return Fail<Upload>("SourceChanged"); } // shorter than before
                long at = offset;
                var sent = await client!.CallAsync("UploadPart", w =>
                {
                    w.WriteString("uploadId", uploadId.ToString("D")); w.WriteNumber("offset", at); w.WriteBase64String("dataBase64", buffer.AsSpan(0, n));
                }, null, ct).ConfigureAwait(false);
                if (!sent.Ok)
                {
                    await CancelUploadAsync(uploadId, ct).ConfigureAwait(false);
                    return Fail<Upload>(sent.Code == "InvalidRequest" && at + n == length ? "SourceChanged" : sent.Code);
                }
                offset += n;
                progress?.Invoke(n);
            }
            return new("Ok", new Upload(uploadId, length, hex));
        }
        catch (DiskReadException)
        {
            await CancelUploadAsync(uploadId, ct).ConfigureAwait(false);
            return Fail<Upload>("FileUnreadable");
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    // Commits a staged replacement if the file is still at basedOn (null: whatever is current); retries a racing commit.
    private async Task<VaultResult<FileSummary>> CommitReplacementAsync(Guid projectId, Guid fileId, Upload upload, Guid? basedOn, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var list = await ListFilesCoreAsync(projectId, ct).ConfigureAwait(false);
            if (!list.Ok) return Fail<FileSummary>(list.Code);
            var file = list.Value!.Files.FirstOrDefault(f => f.FileId == fileId);
            if (file is null) return Fail<FileSummary>("NotFound");
            if (basedOn is { } b && file.RevisionId != b) return Fail<FileSummary>("Conflict");
            if (BeforeCommit is not null) await BeforeCommit().ConfigureAwait(false);
            var r = await CommitUploadsAsync(projectId, list.Value.SnapshotId, [(upload.Id, null)], ct).ConfigureAwait(false);
            if (r.Code == "RevisionConflict") continue; // the staged upload survives a conflict
            if (!r.Ok) return Fail<FileSummary>(r.Code == "InvalidRequest" ? "UploadExpired" : r.Code);
            var (_, revision, kind) = r.Value![0];
            if (opened.ContainsKey((projectId, fileId))) opened[(projectId, fileId)] = revision;
            var after = await ListFilesCoreAsync(projectId, ct).ConfigureAwait(false); // size and hash as stored
            return new("Ok", after.Ok && after.Value!.Files.FirstOrDefault(f => f.FileId == fileId) is { } now && now.RevisionId == revision
                ? now : file with { RevisionId = revision, Kind = kind, ByteLength = upload.Length ?? file.ByteLength, Sha256 = upload.Sha256 });
        }
        return Fail<FileSummary>("RevisionConflict");
    }

    // Uploads every file, then commits them as new files in one snapshot. Any failure discards all the uploads.
    private async Task<VaultResult<List<FileSummary>>> AddCoreAsync(Guid projectId, IReadOnlyList<(string Name, DiskFile File)> files, CancellationToken ct) =>
        await AddCoreAsync(projectId, files, null, ct).ConfigureAwait(false);

    // progress: bytes uploaded so far in this call.
    private async Task<VaultResult<List<FileSummary>>> AddCoreAsync(Guid projectId, IReadOnlyList<(string Name, DiskFile File)> files, Action<long>? progress, CancellationToken ct)
    {
        var uploads = new List<Upload>();
        long sent = 0;
        try
        {
            foreach (var (_, file) in files)
            {
                var upload = await UploadFromDiskAsync(projectId, null, file, ct, n => progress?.Invoke(sent += n)).ConfigureAwait(false);
                if (!upload.Ok) return Fail<List<FileSummary>>(upload.Code);
                uploads.Add(upload.Value!);
            }
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var list = await ListFilesCoreAsync(projectId, ct).ConfigureAwait(false);
                if (!list.Ok) return Fail<List<FileSummary>>(list.Code);
                var r = await CommitUploadsAsync(projectId, list.Value!.SnapshotId, uploads.Select((u, i) => (u.Id, (string?)files[i].Name)).ToList(), ct).ConfigureAwait(false);
                if (r.Code == "RevisionConflict") continue;
                if (!r.Ok) return Fail<List<FileSummary>>(r.Code);
                var added = r.Value!.Select((c, i) => new FileSummary(c.FileId, files[i].Name, c.RevisionId, uploads[i].Length!.Value, c.Kind, uploads[i].Sha256)).ToList();
                uploads.Clear(); // committed: nothing to discard
                return new("Ok", added);
            }
            return Fail<List<FileSummary>>("RevisionConflict");
        }
        finally
        {
            foreach (var upload in uploads) await CancelUploadAsync(upload.Id, ct).ConfigureAwait(false);
        }
    }

    private async Task<VaultResult<List<(Guid FileId, Guid RevisionId, string Kind)>>> CommitUploadsAsync(Guid projectId, Guid snapshotId, IReadOnlyList<(Guid UploadId, string? Name)> entries, CancellationToken ct)
    {
        var r = await client!.CallAsync("CommitUploads", w =>
        {
            w.WriteString("projectId", projectId.ToString("D")); w.WriteString("expectedSnapshotId", snapshotId.ToString("D"));
            w.WriteStartArray("files");
            foreach (var (uploadId, name) in entries)
            {
                w.WriteStartObject(); w.WriteString("uploadId", uploadId.ToString("D"));
                if (name is null) w.WriteNull("name"); else w.WriteString("name", name);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }, null, ct).ConfigureAwait(false);
        if (!r.Ok) return Fail<List<(Guid, Guid, string)>>(r.Code);
        return new("Ok", r.Body.GetProperty("files").EnumerateArray().Select(f => (Guid.Parse(f.GetProperty("fileId").GetString()!),
            Guid.Parse(f.GetProperty("revisionId").GetString()!), KindOf(f))).ToList());
    }

    private async Task CancelUploadAsync(Guid uploadId, CancellationToken ct) =>
        await client!.CallAsync("CancelUpload", w => w.WriteString("uploadId", uploadId.ToString("D")), null, ct).ConfigureAwait(false); // already gone is fine

    private async Task DiscardPendingAsync(Guid projectId, Guid fileId, CancellationToken ct)
    {
        if (pending.Remove((projectId, fileId), out var uploadId)) await CancelUploadAsync(uploadId, ct).ConfigureAwait(false);
    }

    private sealed record FileListing(Guid SnapshotId, IReadOnlyList<FileSummary> Files);

    private async Task<VaultResult<FileListing>> ListFilesCoreAsync(Guid projectId, CancellationToken ct)
    {
        var r = await client!.CallAsync("ListFiles", w => w.WriteString("projectId", projectId.ToString("D")), null, ct).ConfigureAwait(false);
        if (!r.Ok) { await NoteSessionCode(r.Code).ConfigureAwait(false); return Fail<FileListing>(r.Code); }
        return new("Ok", new(Guid.Parse(r.Body.GetProperty("snapshotId").GetString()!), r.Body.GetProperty("files").EnumerateArray()
            .Select(f => new FileSummary(Guid.Parse(f.GetProperty("fileId").GetString()!), f.GetProperty("name").GetString()!,
                Guid.Parse(f.GetProperty("revisionId").GetString()!), f.GetProperty("byteLength").GetInt64(), KindOf(f), Sha256Of(f))).ToList()));
    }

    // Serializes calls, refuses work while locked, and turns session loss into a lock with a reason.
    private async Task<VaultResult<T>> Guarded<T>(Func<Task<VaultResult<T>>> body, bool unlocking = false)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsLocked && !unlocking) return Fail<T>("Locked");
            var result = await body().ConfigureAwait(false);
            await NoteSessionCode(result.Code).ConfigureAwait(false);
            // A lost session surfaces as Locked; the Locked event carries the reason.
            return result.Code is "SessionExpired" or "Unauthorized" ? Fail<T>("Locked") : result;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException or OperationCanceledException)
        {
            await EndAsync(LockReason.Closed, sendEnd: false).ConfigureAwait(false);
            return Fail<T>("Locked");
        }
        finally { gate.Release(); }
    }

    private async Task NoteSessionCode(string code)
    {
        if (code is "SessionExpired" or "Unauthorized") await EndAsync(code == "SessionExpired" ? LockReason.Expired : LockReason.Closed, sendEnd: false).ConfigureAwait(false);
    }

    private async Task EndAsync(LockReason reason, bool sendEnd)
    {
        var current = client;
        client = null;
        pending.Clear(); // the service discards a connection's uploads when it ends
        bool wasUnlocked = !IsLocked;
        IsLocked = true;
        if (current is not null)
        {
            if (sendEnd) { try { await current.CallAsync("EndSession").ConfigureAwait(false); } catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException) { } }
            current.Dispose();
        }
        // Raised off this call chain: handlers may call back into the controller without deadlocking on its gate.
        if (wasUnlocked && Locked is { } handler) _ = Task.Run(() => handler(reason));
    }

    /// <summary>Why a connection could not be opened, as a fixed code the UI can explain. Null: not a connection failure.</summary>
    internal static string? UnlockFailure(Exception e) => e switch
    {
        System.Security.Principal.IdentityNotMappedException => "ServiceNotInstalled", // NT SERVICE\SourceVault does not exist
        System.ComponentModel.Win32Exception { NativeErrorCode: 2 } => "ServiceNotRunning", // no pipe
        System.ComponentModel.Win32Exception { NativeErrorCode: 5 } => "AccessDenied",      // not an operator in this sign-in
        System.ComponentModel.Win32Exception { NativeErrorCode: 231 } => "Busy",           // all connections in use
        System.ComponentModel.Win32Exception => "ServiceNotRunning",
        UnauthorizedAccessException => "ServerUntrusted",      // pipe not owned by the service account
        IOException or EndOfStreamException => "ClientRejected", // service closed without answering: not an installed client
        _ => null,
    };

    private static byte[]? Encode(string text, out string code)
    {
        code = "Ok";
        byte[] bytes;
        try { bytes = StrictUtf8.GetBytes(text); }
        catch (EncoderFallbackException) { code = "InvalidRequest"; return null; }
        if (bytes.Length > MaxEditorTextBytes) { CryptographicOperations.ZeroMemory(bytes); code = "LimitExceeded"; return null; }
        return bytes;
    }

    private static VaultResult<T> Fail<T>(string code) => new(code, default);

    public void Dispose()
    {
        client?.Dispose();
        client = null;
        IsLocked = true;
        gate.Dispose();
    }
}
