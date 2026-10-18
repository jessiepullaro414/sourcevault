using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SourceVault.Client;

public enum LockReason { User, Closed, Expired }

public sealed record ProjectSummary(Guid ProjectId, string Name, int FileCount);
public sealed record FileSummary(Guid FileId, string Name, Guid RevisionId, int ByteLength);
public sealed record OpenedDocument(FileSummary File, string Text);

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
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Func<CancellationToken, Task<VaultClient>> connect;
    private readonly SemaphoreSlim gate = new(1, 1);
    // (project, file) -> revision the editor's copy is based on. Ids only; kept across locks for the unlock re-check.
    private readonly Dictionary<(Guid, Guid), Guid> opened = [];
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
        if (result.Ok) opened.Remove((projectId, fileId));
        return result;
    });

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
        var bytes = Encode(text, out var code);
        if (bytes is null) return Fail<FileSummary>(code);
        try
        {
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
                return new("Ok", file with { RevisionId = revision, ByteLength = bytes.Length });
            }
            return Fail<FileSummary>("RevisionConflict");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    });

    /// <summary>Creates a file and treats it as opened at its first revision.</summary>
    public Task<VaultResult<FileSummary>> CreateFileAsync(Guid projectId, string name, string text, CancellationToken ct = default) => Guarded(async () =>
    {
        var bytes = Encode(text, out var code);
        if (bytes is null) return Fail<FileSummary>(code);
        try
        {
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

    /// <summary>Forgets an opened document (tab closed): it is no longer saved or re-checked.</summary>
    public void Close(Guid projectId, Guid fileId)
    {
        gate.Wait();
        try { opened.Remove((projectId, fileId)); } finally { gate.Release(); }
    }

    private sealed record FileListing(Guid SnapshotId, IReadOnlyList<FileSummary> Files);

    private async Task<VaultResult<FileListing>> ListFilesCoreAsync(Guid projectId, CancellationToken ct)
    {
        var r = await client!.CallAsync("ListFiles", w => w.WriteString("projectId", projectId.ToString("D")), null, ct).ConfigureAwait(false);
        if (!r.Ok) { await NoteSessionCode(r.Code).ConfigureAwait(false); return Fail<FileListing>(r.Code); }
        return new("Ok", new(Guid.Parse(r.Body.GetProperty("snapshotId").GetString()!), r.Body.GetProperty("files").EnumerateArray()
            .Select(f => new FileSummary(Guid.Parse(f.GetProperty("fileId").GetString()!), f.GetProperty("name").GetString()!,
                Guid.Parse(f.GetProperty("revisionId").GetString()!), f.GetProperty("byteLength").GetInt32())).ToList()));
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
        if (bytes.Length > SourceVault.Contracts.ProtocolLimits.MaxDocumentBytes) { CryptographicOperations.ZeroMemory(bytes); code = "LimitExceeded"; return null; }
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
