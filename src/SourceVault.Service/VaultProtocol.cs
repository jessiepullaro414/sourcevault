using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using SourceVault.Contracts;
using SourceVault.Packaging;
using SourceVault.Storage;

namespace SourceVault.Service;

/// <summary>Per-connection session state. Only this connection ever sees its capability.</summary>
internal sealed class ConnectionState
{
    internal byte[]? Capability;
    internal long Started, LastActivity;
    internal bool Closed; // terminal: expired, ended, revoked or rejected
    internal uint WindowsSession;
    internal string? Actor; // the operator's account name, recorded in encrypted file history
    internal System.Security.Principal.SecurityIdentifier? User; // the operator, from the pipe's client token (never the message)
    // A package being uploaded, at most one per connection: spooled to staging\ as it arrives (ciphertext), hashed on the way.
    internal FileStream? PackageSpool;
    internal string? PackageFolder;
    internal IncrementalHash? PackageHash;
    internal long PackageLength, UploadReceived;
    internal Action? OnClose; // releases an upload's service-wide slot when the connection closes
    // File uploads in parts (docs/vault-any-file-design.md section 3.2): ciphertext in this connection's staging folder.
    internal readonly Dictionary<Guid, FileUpload> FileUploads = new();
    internal string? StagingFolder;

    /// <summary>Discards every file upload of this connection and its staging folder (close, end or expiry).</summary>
    internal void DiscardUploads()
    {
        lock (FileUploads)
        {
            foreach (var upload in FileUploads.Values) upload.Discard();
            FileUploads.Clear();
            if (StagingFolder is { } folder) try { Directory.Delete(folder, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            StagingFolder = null;
        }
    }
}

/// <summary>One file upload: receiving (parts still arriving) or staged (complete, waiting for CommitUploads).</summary>
internal sealed class FileUpload(Guid projectId, StoreUpload receiving, long lastActivity)
{
    internal Guid ProjectId { get; } = projectId;
    internal StoreUpload? Receiving { get; private set; } = receiving;
    internal StagedFile? Staged { get; private set; }
    internal long LastActivity { get; set; } = lastActivity;
    internal long Length => Receiving?.Length ?? Staged!.Length;
    internal void Complete() { Staged = Receiving!.Complete(); Receiving.Dispose(); Receiving = null; }
    internal void Discard()
    {
        Receiving?.Dispose(); Receiving = null; // deletes its unfinished staging file
        if (Staged is { } staged) try { File.Delete(staged.Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Staged = null;
    }
}

internal readonly record struct ProtocolResponse(ArrayBufferWriter<byte> Buffer, bool Close);

/// <summary>
/// contracts/protocol-v2.md over a verified <see cref="ProjectCatalog"/>. Responses never contain exception text,
/// and errors never contain names or content. Document content is written as base64 straight from the decrypted
/// buffer, which is zeroed; the caller zeroes the response buffer after sending it.
/// </summary>
internal sealed class VaultProtocol
{
    internal static readonly TimeSpan IdleExpiry = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan AbsoluteExpiry = TimeSpan.FromMinutes(30);
    private readonly ProjectCatalog catalog;
    private readonly BuildCoordinator? builds; // null: no build service configured, so build operations are Unavailable
    private readonly PackageImports? packages; // null: no import key provisioned, so package import is Unavailable
    private readonly TimeProvider clock;
    // Projects created or verified in this process. Only the service creates projects, so this is authoritative.
    private readonly ConcurrentDictionary<Guid, bool> projects = new();
    // Projects that failed their check (at startup, or VerifyProject): offline until an administrator has looked. They
    // are listed as unavailable, and the only operation they allow is deletion.
    private readonly ConcurrentDictionary<Guid, bool> failed = new();
    private readonly Action<int, IEnumerable<Guid>>? status; // writes the administrator's status file (ids and counts only)
    private readonly OperatorPasswords? passwords; // null: in-process tests that don't exercise passwords

    internal VaultProtocol(ProjectCatalog catalog, IEnumerable<ProjectInfo> verified, TimeProvider? clock = null, BuildCoordinator? builds = null, PackageImports? packages = null,
        IEnumerable<(Guid ProjectId, string Name)>? offline = null, Action<int, IEnumerable<Guid>>? status = null, OperatorPasswords? passwords = null)
    {
        this.passwords = passwords;
        this.catalog = catalog;
        this.builds = builds;
        this.packages = packages;
        this.status = status;
        this.clock = clock ?? TimeProvider.System;
        foreach (var p in verified) projects[p.ProjectId] = true;
        foreach (var (id, _) in offline ?? []) { projects[id] = true; failed[id] = true; }
    }

    private void ReportStatus()
    {
        try { status?.Invoke(projects.Count - failed.Count, failed.Keys); } catch (Exception e) when (e is not OutOfMemoryException) { /* best effort */ }
    }

    internal ProtocolResponse Handle(ParsedRequest request, ConnectionState state)
    {
        long now = clock.GetTimestamp();
        if (state.Capability is not null &&
            (clock.GetElapsedTime(state.Started, now) >= AbsoluteExpiry || clock.GetElapsedTime(state.LastActivity, now) >= IdleExpiry))
            End(state);
        if (state.Closed) return Error(request, "SessionExpired", close: true);
        var body = request.Body;

        if (request.Operation == "BeginSession")
        {
            // {} | {password} | {newPassword}: the operator's SourceVault password, checked before a capability exists.
            bool withPassword = Fields(body, "password"), withNew = Fields(body, "newPassword");
            if (!(Fields(body) || withPassword || withNew) || state.Capability is not null) return Error(request, "InvalidRequest");
            if (passwords is not null)
            {
                string? password = withPassword ? PasswordText(body, "password") : null, newPassword = withNew ? PasswordText(body, "newPassword") : null;
                if ((withPassword && password is null) || (withNew && newPassword is null) || state.User is null) return Error(request, "InvalidRequest");
                string code;
                try { code = passwords.BeginSession(state.User, password, newPassword); }
                catch (Exception e) when (e is VaultCatalogException or InvalidDataException or CryptographicException or IOException) { return Error(request, "Unavailable"); }
                if (code != "Ok") return Error(request, code);
            }
            state.Capability = RandomNumberGenerator.GetBytes(32);
            state.Started = state.LastActivity = now;
            return Ok(request, w =>
            {
                w.WriteBase64String("capability", state.Capability);
                w.WriteNumber("idleSeconds", (int)IdleExpiry.TotalSeconds);
                w.WriteNumber("absoluteSeconds", (int)AbsoluteExpiry.TotalSeconds);
            });
        }
        // Every other operation needs a valid capability; a missing or wrong one ends the connection.
        if (!body.TryGetProperty("capability", out var presented) || !ValidCapability(presented, state.Capability))
        {
            End(state);
            return Error(request, "Unauthorized", close: true);
        }
        state.LastActivity = now;
        try
        {
            return request.Operation switch
            {
                "EndSession" => EndSession(request, state),
                "ChangePassword" => ChangePassword(request, state),
                "ListProjects" => ListProjects(request),
                "CreateProject" => CreateProject(request),
                "ListFiles" => ListFiles(request),
                "OpenDocument" => OpenDocument(request),
                "CreateFile" => CreateFile(request, state),
                "ImportFiles" => ImportFiles(request, state),
                "DeleteProject" => DeleteProject(request),
                "RenameProject" => RenameProject(request),
                "RenameFile" => RenameFile(request, state),
                "RenameFolder" => RenameFolder(request, state),
                "DeleteFile" => DeleteFile(request, state),
                "ListHistory" => ListHistory(request),
                "OpenRevision" => OpenRevision(request),
                "ListDeletedFiles" => ListDeletedFiles(request),
                "RestoreFile" => RestoreFile(request, state),
                "ApplyEdits" => ApplyEdits(request, state),
                "SearchProject" => SearchProject(request),
                "BeginUpload" => BeginUpload(request, state, now),
                "UploadPart" => UploadPart(request, state, now),
                "CommitUploads" => CommitUploads(request, state, now),
                "CancelUpload" => CancelUpload(request, state),
                "ReadTextPart" => ReadTextPart(request),
                "RevertFile" => RevertFile(request, state),
                "VerifyProject" => VerifyProject(request),
                "GetImportRecipient" or "BeginPackageImport" or "PackageImportPart" or "FinishPackageImport" when packages is null => Error(request, "Unavailable"),
                "GetImportRecipient" => GetImportRecipient(request, packages!),
                "BeginPackageImport" => BeginPackageImport(request, state, packages!),
                "PackageImportPart" => PackageImportPart(request, state),
                "FinishPackageImport" => FinishPackageImport(request, state, packages!),
                "ListBuildTargets" or "RequestBuild" or "GetBuildStatus" or "ListBuilds" or "OpenBuildLog" when builds is null => Error(request, "Unavailable"),
                "ListBuildTargets" => ListBuildTargets(request),
                "RequestBuild" => RequestBuild(request, state),
                "GetBuildStatus" => GetBuildStatus(request),
                "ListBuilds" => ListBuilds(request),
                "OpenBuildLog" => OpenBuildLog(request),
                _ => Error(request, "UnsupportedOperation"),
            };
        }
        catch (RevisionConflictException) { return Error(request, "RevisionConflict"); }
        catch (KeyNotFoundException) { return Error(request, "NotFound"); }
        catch (NameExistsException) { return Error(request, "AlreadyExists"); }
        catch (StoreLimitException) { return Error(request, "LimitExceeded"); } // count, size or free space (any-file design)
        catch (NotTextException) { return Error(request, "NotText"); }
        catch (RequestException e) { return Error(request, e.Code); }
        catch (SourceVault.Build.BuildServiceUnavailableException) { return Error(request, "Unavailable"); }
        // Input is validated before the store is called, so anything the store or catalog rejects here is an
        // integrity, availability or key failure of the vault itself.
        catch (Exception e) when (e is VaultCatalogException or CryptographicException or IOException or InvalidDataException
            or ArgumentException or UnauthorizedAccessException) { return Error(request, "Unavailable"); }
        catch (Exception e) when (e is not OutOfMemoryException) { return Error(request, "InternalError"); }
    }

    private static string? PasswordText(JsonElement body, string name)
    {
        var value = body.GetProperty(name);
        return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= OperatorPasswords.MaxLength } text ? text : null;
    }

    private ProtocolResponse ChangePassword(ParsedRequest request, ConnectionState state)
    {
        Require(request.Body, "capability", "password", "newPassword");
        if (passwords is null || state.User is null) throw new RequestException("Unavailable");
        var password = PasswordText(request.Body, "password") ?? throw new RequestException("InvalidRequest");
        var newPassword = PasswordText(request.Body, "newPassword") ?? throw new RequestException("InvalidRequest");
        var code = passwords.ChangePassword(state.User, password, newPassword);
        return code == "Ok" ? Ok(request, _ => { }) : Error(request, code);
    }

    private ProtocolResponse EndSession(ParsedRequest request, ConnectionState state)
    {
        Require(request.Body, "capability");
        End(state);
        return Ok(request, _ => { }, close: true);
    }

    private ProtocolResponse ListProjects(ParsedRequest request)
    {
        Require(request.Body, "capability");
        var check = catalog.Verify();
        foreach (var p in check.Projects) projects[p.ProjectId] = true;
        bool changed = false;
        foreach (var (id, _) in check.Offline) { projects[id] = true; changed |= failed.TryAdd(id, true); }
        if (changed) ReportStatus();
        var available = check.Projects.Where(p => !failed.ContainsKey(p.ProjectId)).ToList();
        var unavailable = check.Offline.Concat(check.Projects.Where(p => failed.ContainsKey(p.ProjectId)).Select(p => (p.ProjectId, p.Name))).ToList();
        return Ok(request, w =>
        {
            // Projects that failed their check: names only, so operators can see what is offline and delete it.
            w.WriteStartArray("unavailable");
            foreach (var (id, name) in unavailable) { w.WriteStartObject(); w.WriteString("projectId", id.ToString("D")); w.WriteString("name", name); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteStartArray("projects");
            foreach (var p in available)
            {
                w.WriteStartObject();
                w.WriteString("projectId", p.ProjectId.ToString("D"));
                w.WriteString("name", p.Name);
                w.WriteString("snapshotId", p.Snapshot.SnapshotId.ToString("D"));
                w.WriteNumber("fileCount", p.Snapshot.Files.Count);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }

    private ProtocolResponse CreateProject(ParsedRequest request)
    {
        Require(request.Body, "capability", "name");
        var name = Text(request.Body, "name");
        try { ProjectCatalog.ValidateProjectName(name); } catch (ArgumentException) { throw new RequestException("InvalidRequest"); }
        var created = catalog.CreateProject(name);
        projects[created.ProjectId] = true;
        return Ok(request, w =>
        {
            w.WriteString("projectId", created.ProjectId.ToString("D"));
            w.WriteString("snapshotId", created.Snapshot.SnapshotId.ToString("D"));
        });
    }

    /// <summary>
    /// Deletes a project and all its revisions. confirmName must equal the project's name exactly, so a stale or
    /// mistaken id cannot delete a different project. Other connections then get NotFound for that project.
    /// filesRemoved is false when some files were in use; the tombstone keeps them until a later cleanup.
    /// </summary>
    private ProtocolResponse DeleteProject(ParsedRequest request)
    {
        Require(request.Body, "capability", "projectId", "confirmName");
        var id = Id(request.Body, "projectId");
        var confirmName = Text(request.Body, "confirmName");
        if (!projects.ContainsKey(id)) throw new KeyNotFoundException();
        bool removed;
        try { removed = catalog.DeleteProject(id, confirmName); }
        catch (ArgumentException) { throw new RequestException("InvalidRequest"); }
        catch (KeyNotFoundException) { projects.TryRemove(id, out _); throw; }
        projects.TryRemove(id, out _);
        if (failed.TryRemove(id, out _)) ReportStatus();
        return Ok(request, w => w.WriteBoolean("filesRemoved", removed));
    }

    private ProtocolResponse RenameProject(ParsedRequest request)
    {
        Require(request.Body, "capability", "projectId", "newName");
        var id = Id(request.Body, "projectId");
        var name = Text(request.Body, "newName");
        try { ProjectCatalog.ValidateProjectName(name); } catch (ArgumentException) { throw new RequestException("InvalidRequest"); }
        if (!projects.ContainsKey(id)) throw new KeyNotFoundException();
        catalog.RenameProject(id, name);
        return Ok(request, _ => { });
    }

    /// <summary>Renames or moves one file. Its id and revision are kept; only the encrypted manifest changes.</summary>
    private ProtocolResponse RenameFile(ParsedRequest request, ConnectionState state)
    {
        Require(request.Body, "capability", "projectId", "fileId", "newName", "expectedSnapshotId");
        var store = Project(request.Body);
        var fileId = Id(request.Body, "fileId");
        var name = Text(request.Body, "newName");
        if (!RevisionStore.IsValidLogicalName(name)) throw new RequestException("InvalidRequest");
        return Changed(request, store, Id(request.Body, "expectedSnapshotId"), [new StoreChange.Rename(fileId, name)], null, state);
    }

    /// <summary>Renames every file under a folder path in one commit ("src/app" to "lib/app").</summary>
    private ProtocolResponse RenameFolder(ParsedRequest request, ConnectionState state)
    {
        Require(request.Body, "capability", "projectId", "oldPrefix", "newPrefix", "expectedSnapshotId");
        var store = Project(request.Body);
        var oldPrefix = Text(request.Body, "oldPrefix");
        var newPrefix = Text(request.Body, "newPrefix");
        if (!RevisionStore.IsValidLogicalName(oldPrefix) || !RevisionStore.IsValidLogicalName(newPrefix)) throw new RequestException("InvalidRequest");
        var expected = Id(request.Body, "expectedSnapshotId");
        var current = store.List();
        if (current.SnapshotId != expected) throw new RevisionConflictException();
        var moved = current.Files.Where(f => f.LogicalName.StartsWith(oldPrefix + "/", StringComparison.OrdinalIgnoreCase)).ToList();
        if (moved.Count == 0) throw new KeyNotFoundException();
        var changes = new List<StoreChange>();
        foreach (var f in moved)
        {
            var name = newPrefix + f.LogicalName[oldPrefix.Length..];
            if (!RevisionStore.IsValidLogicalName(name)) throw new RequestException("InvalidRequest"); // too long or too deep
            changes.Add(new StoreChange.Rename(f.FileId, name));
        }
        return Changed(request, store, expected, changes, w => w.WriteNumber("renamed", moved.Count), state);
    }

    private ProtocolResponse DeleteFile(ParsedRequest request, ConnectionState state)
    {
        Require(request.Body, "capability", "projectId", "fileId", "expectedSnapshotId");
        var store = Project(request.Body);
        return Changed(request, store, Id(request.Body, "expectedSnapshotId"), [new StoreChange.Delete(Id(request.Body, "fileId"))], null, state);
    }

    // The store re-checks the expected snapshot under its lock; unknown ids are NotFound and clashes AlreadyExists.
    private ProtocolResponse Changed(ParsedRequest request, RevisionStore store, Guid expected, IReadOnlyList<StoreChange> changes, Action<Utf8JsonWriter>? extra, ConnectionState state)
    {
        var next = store.CommitChanges(expected, changes, state.Actor);
        return Ok(request, w => { w.WriteString("snapshotId", next.SnapshotId.ToString("D")); extra?.Invoke(w); });
    }

    /// <summary>One page of a live or deleted file's history, newest first. Page 0 is the newest; more: older pages exist.</summary>
    private ProtocolResponse ListHistory(ParsedRequest request)
    {
        Require(request.Body, "capability", "projectId", "fileId", "page");
        var store = Project(request.Body);
        var fileId = Id(request.Body, "fileId");
        var pageValue = request.Body.GetProperty("page");
        if (pageValue.ValueKind != JsonValueKind.Number || !pageValue.TryGetInt32(out int page) || page is < 0 or > 10000) throw new RequestException("InvalidRequest");
        var history = store.ReadHistory(fileId, page);
        return Ok(request, w =>
        {
            w.WriteString("fileId", fileId.ToString("D"));
            w.WriteStartArray("revisions");
            foreach (var r in history.Revisions)
            {
                w.WriteStartObject();
                w.WriteString("revisionId", r.RevisionId.ToString("D"));
                if (r.SavedAt is { } at) w.WriteString("savedAt", at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture)); else w.WriteNull("savedAt");
                if (r.SavedBy is { } by) w.WriteString("savedBy", by); else w.WriteNull("savedBy");
                w.WriteNumber("byteLength", r.ByteLength);
                w.WriteString("action", r.Action);
                w.WriteString("name", r.Name);
                WriteKind(w, r.Kind, r.Sha256);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteBoolean("more", history.More);
        }, capacity: 16384 + history.Revisions.Count * 700);
    }

    /// <summary>Decrypts one revision from a live or deleted file's history.</summary>
    private ProtocolResponse OpenRevision(ParsedRequest request)
    {
        Require(request.Body, "capability", "projectId", "fileId", "revisionId");
        var store = Project(request.Body);
        var revisionId = Id(request.Body, "revisionId");
        InlineText(store.Revision(Id(request.Body, "fileId"), revisionId));
        var content = store.OpenRevision(Id(request.Body, "fileId"), revisionId);
        try
        {
            return Ok(request, w =>
            {
                w.WriteString("revisionId", revisionId.ToString("D"));
                w.WriteBase64String("contentBase64", content);
            }, capacity: 16384 + (content.Length + 2) / 3 * 4); // pre-sized so the buffer never reallocates (see OpenDocument)
        }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    private ProtocolResponse ListDeletedFiles(ParsedRequest request)
    {
        Require(request.Body, "capability", "projectId");
        var deleted = Project(request.Body).ListDeleted();
        return Ok(request, w =>
        {
            w.WriteStartArray("files");
            foreach (var d in deleted)
            {
                w.WriteStartObject();
                w.WriteString("fileId", d.FileId.ToString("D"));
                w.WriteString("name", d.LogicalName);
                w.WriteNumber("byteLength", d.ByteLength);
                WriteKind(w, d.Kind, d.Sha256);
                w.WriteString("deletedAt", d.DeletedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
                if (d.DeletedBy is { } by) w.WriteString("deletedBy", by); else w.WriteNull("deletedBy");
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }, capacity: 16384 + deleted.Count * 700);
    }

    /// <summary>Brings a deleted file back, under its old name or a new one, at the revision it had when deleted.</summary>
    private ProtocolResponse RestoreFile(ParsedRequest request, ConnectionState state)
    {
        Require(request.Body, "capability", "projectId", "fileId", "newName", "expectedSnapshotId");
        var store = Project(request.Body);
        var name = Text(request.Body, "newName");
        if (!RevisionStore.IsValidLogicalName(name)) throw new RequestException("InvalidRequest");
        return Changed(request, store, Id(request.Body, "expectedSnapshotId"), [new StoreChange.Restore(Id(request.Body, "fileId"), name)], null, state);
    }

    // ---- builds (docs/files-history-builds-design.md, section 4) -----------------------------------------------------
    private ProtocolResponse ListBuildTargets(ParsedRequest request)
    {
        Require(request.Body, "capability");
        var list = builds!.Targets();
        return Ok(request, w =>
        {
            w.WriteStartArray("targets"); foreach (var t in list) w.WriteStringValue(t.Name); w.WriteEndArray();
            w.WriteStartArray("platforms"); foreach (var t in list) w.WriteStringValue(t.Platform); w.WriteEndArray(); // same order; "" if unknown
        });
    }

    private ProtocolResponse RequestBuild(ParsedRequest request, ConnectionState state)
    {
        Require(request.Body, "capability", "projectId", "target");
        var id = Id(request.Body, "projectId");
        var target = Text(request.Body, "target");
        if (target.Length is 0 or > 32) throw new RequestException("InvalidRequest");
        if (!projects.ContainsKey(id)) throw new KeyNotFoundException();
        int number;
        try { number = builds!.Request(id, target, state.Actor); }
        catch (InvalidOperationException) { throw new RequestException("LimitExceeded"); } // that target's queue is full
        return Ok(request, w => w.WriteNumber("buildNumber", number));
    }

    private ProtocolResponse GetBuildStatus(ParsedRequest request)
    {
        Require(request.Body, "capability", "projectId", "buildNumber");
        var id = Id(request.Body, "projectId");
        if (!projects.ContainsKey(id)) throw new KeyNotFoundException();
        var record = builds!.Status(id, Number(request.Body, "buildNumber"));
        return Ok(request, w => WriteBuild(w, record));
    }

    private ProtocolResponse ListBuilds(ParsedRequest request)
    {
        Require(request.Body, "capability", "projectId");
        var id = Id(request.Body, "projectId");
        if (!projects.ContainsKey(id)) throw new KeyNotFoundException();
        var list = builds!.List(id);
        return Ok(request, w => { w.WriteStartArray("builds"); foreach (var r in list) { w.WriteStartObject(); WriteBuild(w, r); w.WriteEndObject(); } w.WriteEndArray(); },
            capacity: 16384 + list.Count * 1024);
    }

    private ProtocolResponse OpenBuildLog(ParsedRequest request)
    {
        Require(request.Body, "capability", "projectId", "buildNumber");
        var id = Id(request.Body, "projectId");
        if (!projects.ContainsKey(id)) throw new KeyNotFoundException();
        var content = builds!.OpenLog(id, Number(request.Body, "buildNumber"));
        try { return Ok(request, w => w.WriteBase64String("contentBase64", content), capacity: 16384 + (content.Length + 2) / 3 * 4); }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    private static void WriteBuild(Utf8JsonWriter w, BuildRecord r)
    {
        w.WriteNumber("buildNumber", r.Number); w.WriteString("target", r.Target); w.WriteString("state", r.State);
        if (r.Reason is null) w.WriteNull("reason"); else w.WriteString("reason", r.Reason);
        if (r.ExitCode is { } exit) w.WriteNumber("exitCode", exit); else w.WriteNull("exitCode");
        if (r.RequestedBy is null) w.WriteNull("requestedBy"); else w.WriteString("requestedBy", r.RequestedBy);
        w.WriteString("requestedAt", Time(r.RequestedAt));
        if (r.FinishedAt is { } done) w.WriteString("finishedAt", Time(done)); else w.WriteNull("finishedAt");
        if (r.ArtifactFolder is null) w.WriteNull("artifactFolder"); else w.WriteString("artifactFolder", r.ArtifactFolder);
        w.WriteNumber("artifactFiles", r.ArtifactFiles); w.WriteNumber("artifactBytes", r.ArtifactBytes); w.WriteNumber("artifactsDropped", r.ArtifactsDropped);
        w.WriteBoolean("logAvailable", r.LogAvailable); w.WriteBoolean("logTruncated", r.LogTruncated);
    }

    private static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static int Number(JsonElement body, string name)
    {
        var value = body.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) && n >= 1 ? n : throw new RequestException("InvalidRequest");
    }

    private ProtocolResponse ListFiles(ParsedRequest request)
    {
        Require(request.Body, "capability", "projectId");
        var snapshot = Project(request.Body).List();
        return Ok(request, w =>
        {
            w.WriteString("snapshotId", snapshot.SnapshotId.ToString("D"));
            w.WriteStartArray("files");
            foreach (var f in snapshot.Files)
            {
                w.WriteStartObject();
                w.WriteString("fileId", f.FileId.ToString("D"));
                w.WriteString("name", f.LogicalName);
                w.WriteString("revisionId", f.RevisionId.ToString("D"));
                w.WriteNumber("byteLength", f.ByteLength);
                WriteKind(w, f.Kind, f.Sha256);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }, capacity: 16384 + snapshot.Files.Count * 600);
    }

    private static void WriteKind(Utf8JsonWriter w, FileKind kind, byte[]? sha256)
    {
        w.WriteString("kind", kind == FileKind.Text ? "text" : "binary");
        if (sha256 is null) w.WriteNull("sha256"); else w.WriteString("sha256", Convert.ToHexStringLower(sha256));
    }

    private ProtocolResponse OpenDocument(ParsedRequest request)
    {
        Require(request.Body, "capability", "projectId", "fileId", "expectedSnapshotId");
        var store = Project(request.Body);
        var fileId = Id(request.Body, "fileId");
        var expected = Id(request.Body, "expectedSnapshotId");
        var snapshot = store.List();
        if (snapshot.SnapshotId != expected) throw new RevisionConflictException();
        var entry = snapshot.Files.SingleOrDefault(f => f.FileId == fileId) ?? throw new KeyNotFoundException();
        InlineText(entry);
        var revision = entry.RevisionId;
        var content = store.Open(expected, fileId); // re-checks the snapshot under the lock: a racing commit conflicts
        try
        {
            // Pre-size beyond Utf8JsonWriter's 4 KiB growth request so the buffer never reallocates, which would
            // leave an unzeroed copy of the content behind.
            return Ok(request, w =>
            {
                w.WriteString("snapshotId", expected.ToString("D"));
                w.WriteString("revisionId", revision.ToString("D"));
                w.WriteBase64String("contentBase64", content);
            }, capacity: 16384 + (content.Length + 2) / 3 * 4);
        }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    private ProtocolResponse CreateFile(ParsedRequest request, ConnectionState state)
    {
        Require(request.Body, "capability", "projectId", "name", "expectedSnapshotId", "contentBase64");
        var store = Project(request.Body);
        var name = Text(request.Body, "name");
        if (!RevisionStore.IsValidLogicalName(name)) throw new RequestException("InvalidRequest");
        var expected = Id(request.Body, "expectedSnapshotId");
        var content = Content(request.Body, textOnly: false); // any bytes; the store decides text or binary
        try
        {
            var current = store.List();
            if (current.SnapshotId != expected) throw new RevisionConflictException();
            if (current.Files.Any(f => StringComparer.OrdinalIgnoreCase.Equals(f.LogicalName, name))) throw new NameExistsException();
            // The store re-checks the expected snapshot under its lock, so a racing create of the same name conflicts.
            var next = store.CommitChanges(expected, [new StoreChange.Write(name, content, "created")], state.Actor);
            var file = next.Files.Single(f => StringComparer.OrdinalIgnoreCase.Equals(f.LogicalName, name));
            return Ok(request, w =>
            {
                w.WriteString("snapshotId", next.SnapshotId.ToString("D"));
                w.WriteString("fileId", file.FileId.ToString("D"));
                w.WriteString("revisionId", file.RevisionId.ToString("D"));
            });
        }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    internal const int MaxImportBatch = 256;

    /// <summary>
    /// Adds up to 256 new files in one snapshot, all or nothing (used by the importer). Create-only: any name that
    /// already exists, or that clashes with an existing folder/file path, is AlreadyExists and nothing is written.
    /// </summary>
    private ProtocolResponse ImportFiles(ParsedRequest request, ConnectionState state)
    {
        Require(request.Body, "capability", "projectId", "expectedSnapshotId", "files");
        var store = Project(request.Body);
        var expected = Id(request.Body, "expectedSnapshotId");
        var files = request.Body.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is 0) throw new RequestException("InvalidRequest");
        if (files.GetArrayLength() > MaxImportBatch) throw new RequestException("LimitExceeded");
        var documents = new List<(string Name, byte[] Content)>();
        try
        {
            foreach (var file in files.EnumerateArray())
            {
                Require(file, "name", "contentBase64");
                var name = Text(file, "name");
                if (!RevisionStore.IsValidLogicalName(name)) throw new RequestException("InvalidRequest");
                documents.Add((name, Content(file, textOnly: false)));
            }
            if (documents.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != documents.Count) throw new RequestException("InvalidRequest");
            var current = store.List();
            if (current.SnapshotId != expected) throw new RevisionConflictException();
            var existing = current.Files.Select(f => f.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (documents.Any(d => existing.Contains(d.Name))) throw new NameExistsException();
            if (current.Files.Count + documents.Count > RevisionStore.MaximumFiles) throw new RequestException("LimitExceeded");
            var next = store.CommitChanges(expected, documents.Select(d => (StoreChange)new StoreChange.Write(d.Name, d.Content, "imported")).ToList(), state.Actor); // re-checks the snapshot under the lock; path clashes are NameExists
            return Ok(request, w =>
            {
                w.WriteString("snapshotId", next.SnapshotId.ToString("D"));
                w.WriteNumber("imported", documents.Count);
            });
        }
        finally { foreach (var (_, content) in documents) CryptographicOperations.ZeroMemory(content); }
    }

    // ---- encrypted packages from another computer (docs/package-import-design.md) ---------------------------------

    internal const int MaxPackagePartBytes = 512 * 1024;
    internal const int PackageCommitFiles = 1024; // staged records per commit of a package import

    /// <summary>This PC's recipient file (the import key's public half): public, for SourceVault Pack on the other computer.</summary>
    private ProtocolResponse GetImportRecipient(ParsedRequest request, PackageImports packages)
    {
        Require(request.Body, "capability");
        var recipient = packages.Recipient;
        return Ok(request, w =>
        {
            w.WriteString("computer", recipient.Computer);
            w.WriteString("publicKey", Convert.ToBase64String(recipient.SubjectPublicKeyInfo));
            w.WriteString("fingerprint", Convert.ToHexStringLower(recipient.Fingerprint));
        });
    }

    /// <summary>
    /// Starts an upload of <c>length</c> bytes, spooled to this connection's folder in staging\ (the package is ciphertext)
    /// and hashed as it arrives. One per connection, and one at a time across the service.
    /// </summary>
    private ProtocolResponse BeginPackageImport(ParsedRequest request, ConnectionState state, PackageImports packages)
    {
        Require(request.Body, "capability", "length");
        var value = request.Body.GetProperty("length");
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long length) || length < Package.HeaderBytes + Package.SignatureBytes || state.PackageSpool is not null)
            throw new RequestException("InvalidRequest");
        if (length > Package.MaxPackageBytes) throw new RequestException("LimitExceeded");
        // The spool and then the project's records each take about the package's size; keep the reserve free after both.
        catalog.EnsureFreeSpace(2 * length);
        if (!packages.TryEnter()) throw new RequestException("Unavailable"); // another package import is running
        int released = 0; // the connection closing (another thread) and FinishPackageImport may both release it
        state.OnClose = () =>
        {
            if (Interlocked.Exchange(ref released, 1) == 1) return;
            state.PackageSpool?.Dispose(); state.PackageSpool = null; // DeleteOnClose
            state.PackageHash?.Dispose(); state.PackageHash = null;
            if (state.PackageFolder is { } folder) try { Directory.Delete(folder, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            state.PackageFolder = null;
            packages.Exit();
        };
        try
        {
            state.PackageFolder = catalog.CreateStagingFolder();
            state.PackageSpool = new FileStream(Path.Combine(state.PackageFolder, "package.svpkg"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            state.PackageHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            state.PackageLength = length;
            state.UploadReceived = 0;
        }
        catch { state.OnClose(); state.OnClose = null; throw; }
        return Ok(request, _ => { });
    }

    /// <summary>The next part of the upload, in order and without gaps: <c>offset</c> must equal the bytes received so far.</summary>
    private ProtocolResponse PackageImportPart(ParsedRequest request, ConnectionState state)
    {
        Require(request.Body, "capability", "offset", "dataBase64");
        var spool = state.PackageSpool ?? throw new RequestException("InvalidRequest");
        var offset = request.Body.GetProperty("offset");
        if (offset.ValueKind != JsonValueKind.Number || !offset.TryGetInt64(out long at) || at != state.UploadReceived) throw new RequestException("InvalidRequest");
        var data = request.Body.GetProperty("dataBase64");
        if (data.ValueKind != JsonValueKind.String || !data.TryGetBytesFromBase64(out var bytes) || bytes.Length == 0) throw new RequestException("InvalidRequest");
        if (bytes.Length > MaxPackagePartBytes || state.UploadReceived + bytes.Length > state.PackageLength) throw new RequestException("LimitExceeded");
        spool.Write(bytes);
        // The signature covers everything before its own 256 bytes: hash exactly that, as it arrives.
        long signedEnd = state.PackageLength - Package.SignatureBytes;
        if (state.UploadReceived < signedEnd) state.PackageHash!.AppendData(bytes, 0, (int)Math.Min(bytes.Length, signedEnd - state.UploadReceived));
        state.UploadReceived += bytes.Length;
        return Ok(request, w => w.WriteNumber("received", state.UploadReceived));
    }

    /// <summary>
    /// Verifies and decrypts the uploaded package and creates a new project from it, all or nothing. A package that
    /// is malformed, altered or for another PC is PackageRejected; a valid one from an unregistered computer is
    /// UnknownSender. The upload ends either way.
    /// </summary>
    private ProtocolResponse FinishPackageImport(ParsedRequest request, ConnectionState state, PackageImports packages)
    {
        try
        {
            Require(request.Body, "capability", "projectName");
            var spool = state.PackageSpool;
            if (spool is null || state.UploadReceived != state.PackageLength) throw new RequestException("InvalidRequest");
            var name = Text(request.Body, "projectName");
            try { ProjectCatalog.ValidateProjectName(name); } catch (ArgumentException) { throw new RequestException("InvalidRequest"); }
            spool.Flush(true);
            PackageReader reader;
            try { reader = packages.Open(spool, state.PackageHash!.GetHashAndReset()); }
            catch (PackageRejectedException) { throw new RequestException("PackageRejected"); }
            catch (UnknownSenderException) { throw new RequestException("UnknownSender"); }
            using (reader)
            {
                var sender = packages.RegisteredSender(reader.SenderFingerprint)?.Computer ?? throw new RequestException("UnknownSender");
                var created = catalog.CreateProject(name); // a taken name is AlreadyExists, before anything is written
                projects[created.ProjectId] = true;
                StoreSnapshot next = created.Snapshot;
                var staged = new List<(string Path, StagedFile File)>();
                var folder = catalog.CreateStagingFolder();
                try
                {
                    var store = catalog.OpenProject(created.ProjectId);
                    // Each file streams from the package straight into a staged SVR2 record (one chunk in memory), then the
                    // records are committed in batches, with the history action "imported".
                    using (var session = store.BeginImport(folder))
                    {
                        StoreUpload? current = null;
                        try
                        {
                            reader.ReadContents(
                                (entry, piece) => { current ??= session.Begin(entry.Length, entry.Sha256); current.Write(piece); },
                                entry => { current ??= session.Begin(entry.Length, entry.Sha256); staged.Add((entry.Path, current.Complete())); current.Dispose(); current = null; });
                        }
                        finally { current?.Dispose(); }
                    }
                    foreach (var batch in staged.Chunk(PackageCommitFiles))
                        next = store.CommitChanges(next.SnapshotId, batch.Select(s => (StoreChange)new StoreChange.Stage(s.File, s.Path, "imported")).ToList(), state.Actor);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // All or nothing: remove the new project again. A path clash or bad content inside the package is the package's fault.
                    try { catalog.DeleteProject(created.ProjectId, name); projects.TryRemove(created.ProjectId, out _); } catch (Exception cleanup) when (cleanup is not OutOfMemoryException) { }
                    if (e is NameExistsException or PackageRejectedException) throw new RequestException("PackageRejected");
                    throw;
                }
                finally { try { Directory.Delete(folder, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
                return Ok(request, w =>
                {
                    w.WriteString("projectId", created.ProjectId.ToString("D"));
                    w.WriteString("snapshotId", next.SnapshotId.ToString("D"));
                    w.WriteNumber("imported", staged.Count);
                    w.WriteString("packageId", reader.PackageId.ToString("D"));
                    w.WriteString("sender", sender);
                });
            }
        }
        finally { state.OnClose?.Invoke(); state.OnClose = null; }
    }

    // ---- searching a project (the editor's "Search all files") ---------------------------------------------------

    internal const int MaxSearchMatches = 500, MaxSearchQueryChars = 200, MaxSearchPreviewChars = 240, MaxSearchFileBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Plain-text search over every file of the project's current snapshot (no regular expressions). Returns up to 500
    /// matches with the matching line as a short preview, in file-name order. Decrypts each file once, in this process.
    /// </summary>
    private ProtocolResponse SearchProject(ParsedRequest request)
    {
        Require(request.Body, "capability", "projectId", "query", "matchCase", "wholeWord");
        var store = Project(request.Body);
        var query = Text(request.Body, "query");
        if (query.Length is 0 or > MaxSearchQueryChars || query.Contains('\n') || query.Contains('\r')) throw new RequestException("InvalidRequest");
        bool Flag(string name) => request.Body.GetProperty(name).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new RequestException("InvalidRequest") };
        bool matchCase = Flag("matchCase"), wholeWord = Flag("wholeWord");
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var found = new List<(Guid FileId, string Name, int Line, int Column, string Preview, int PreviewOffset)>();
        bool truncated = false; int searched = 0;
        var snapshot = store.List();
        // Text only, and at most 16 MiB per file in memory; the rest are counted as skipped.
        static bool Searchable(StoreFile f) => f.Kind == FileKind.Text && f.ByteLength <= MaxSearchFileBytes;
        int skipped = snapshot.Files.Count(f => !Searchable(f));
        store.ReadSnapshot(snapshot.SnapshotId, Searchable, (file, content) =>
        {
            searched++;
            if (truncated) return;
            var text = System.Text.Encoding.UTF8.GetString(content);
            int lineNumber = 0;
            foreach (var line in text.Split('\n'))
            {
                lineNumber++;
                var trimmed = line.TrimEnd('\r');
                for (int at = trimmed.IndexOf(query, comparison); at >= 0; at = trimmed.IndexOf(query, at + 1, comparison))
                {
                    if (wholeWord && (IsWordChar(trimmed, at - 1) || IsWordChar(trimmed, at + query.Length))) continue;
                    if (found.Count == MaxSearchMatches) { truncated = true; return; }
                    var (preview, offset) = Preview(trimmed, at);
                    found.Add((file.FileId, file.LogicalName, lineNumber, at + 1, preview, offset));
                }
            }
        });
        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) is var c && c != 0 ? c : a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));
        return Ok(request, w =>
        {
            w.WriteStartArray("matches");
            foreach (var m in found)
            {
                w.WriteStartObject();
                w.WriteString("fileId", m.FileId.ToString("D")); w.WriteString("name", m.Name);
                w.WriteNumber("line", m.Line); w.WriteNumber("column", m.Column); w.WriteNumber("length", query.Length);
                w.WriteString("preview", m.Preview); w.WriteNumber("previewOffset", m.PreviewOffset);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteBoolean("truncated", truncated);
            w.WriteNumber("filesSearched", searched);
            w.WriteNumber("filesSkipped", skipped);
        });

        static bool IsWordChar(string s, int i) => i >= 0 && i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_');
        // The line around the match, at most 240 characters, leading whitespace removed, and where the match starts in it.
        static (string Text, int Offset) Preview(string line, int at)
        {
            int start = Math.Max(0, Math.Min(at - 60, line.Length - MaxSearchPreviewChars));
            var window = line.Substring(start, Math.Min(MaxSearchPreviewChars, line.Length - start));
            if (start > 0) return ("…" + window, at - start + 1);
            var trimmed = window.TrimStart();
            return (trimmed, Math.Max(0, at - (window.Length - trimmed.Length)));
        }
    }

    // ---- any file: uploads in parts, text in parts, revert, verify (docs/vault-any-file-design.md section 3) --------

    internal const int UploadPartBytes = SegmentedRecord.SegmentLength;
    internal const int MaxStagedUploads = 256, MaxCommitUploads = 256;
    internal const long MaxStagedBytes = 8L << 30;
    internal static readonly TimeSpan UploadIdle = TimeSpan.FromMinutes(2);

    /// <summary>Begins an upload: a new file (fileId null) or the next revision of fileId. One receiving per connection.</summary>
    private ProtocolResponse BeginUpload(ParsedRequest request, ConnectionState state, long now)
    {
        Require(request.Body, "capability", "projectId", "fileId", "length", "sha256");
        var projectId = Id(request.Body, "projectId");
        var store = Project(request.Body);
        var fileValue = request.Body.GetProperty("fileId");
        Guid? fileId = fileValue.ValueKind == JsonValueKind.Null ? null : Id(request.Body, "fileId");
        var lengthValue = request.Body.GetProperty("length");
        if (lengthValue.ValueKind != JsonValueKind.Number || !lengthValue.TryGetInt64(out long length) || length < 0) throw new RequestException("InvalidRequest");
        if (length > RevisionStore.MaximumFileBytes) throw new RequestException("LimitExceeded");
        var sha256 = Sha256(request.Body, "sha256");
        lock (state.FileUploads)
        {
            DiscardIdle(state, now);
            if (state.FileUploads.Values.Any(u => u.Receiving is not null)) throw new RequestException("InvalidRequest");
            if (state.FileUploads.Count >= MaxStagedUploads || state.FileUploads.Values.Sum(u => u.Length) + length > MaxStagedBytes) throw new RequestException("LimitExceeded");
            state.StagingFolder ??= catalog.CreateStagingFolder();
            var upload = store.BeginUpload(state.StagingFolder, fileId, length, sha256);
            var slot = new FileUpload(projectId, upload, now);
            try { if (length == 0) slot.Complete(); }
            catch (InvalidDataException) { slot.Discard(); throw new RequestException("InvalidRequest"); }
            var uploadId = Guid.NewGuid();
            state.FileUploads[uploadId] = slot;
            return Ok(request, w =>
            {
                w.WriteString("uploadId", uploadId.ToString("D"));
                w.WriteString("fileId", upload.FileId.ToString("D"));
                w.WriteNumber("partBytes", UploadPartBytes);
            });
        }
    }

    /// <summary>The next part, in order and without gaps. After the last byte the plaintext SHA-256 must match.</summary>
    private ProtocolResponse UploadPart(ParsedRequest request, ConnectionState state, long now)
    {
        Require(request.Body, "capability", "uploadId", "offset", "dataBase64");
        var uploadId = Id(request.Body, "uploadId");
        lock (state.FileUploads)
        {
            DiscardIdle(state, now);
            if (!state.FileUploads.TryGetValue(uploadId, out var slot) || slot.Receiving is not { } upload) throw new RequestException("InvalidRequest");
            var offset = request.Body.GetProperty("offset");
            if (offset.ValueKind != JsonValueKind.Number || !offset.TryGetInt64(out long at) || at != upload.Received) throw new RequestException("InvalidRequest");
            var data = request.Body.GetProperty("dataBase64");
            if (data.ValueKind != JsonValueKind.String || !data.TryGetBytesFromBase64(out var bytes) || bytes.Length == 0) throw new RequestException("InvalidRequest");
            try
            {
                if (bytes.Length > UploadPartBytes || upload.Received + bytes.Length > upload.Length) throw new RequestException("LimitExceeded");
                upload.Write(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            slot.LastActivity = now;
            long received = upload.Received;
            if (received == upload.Length)
            {
                try { slot.Complete(); }
                catch (InvalidDataException) { slot.Discard(); state.FileUploads.Remove(uploadId); throw new RequestException("InvalidRequest"); } // wrong SHA-256
            }
            return Ok(request, w => w.WriteNumber("received", received));
        }
    }

    /// <summary>
    /// Commits staged uploads in one snapshot, all or nothing: an entry with a name creates a file (create-only), one
    /// without replaces the file its upload began for. A conflict keeps the staged uploads, so "keep mine" needs no
    /// second upload.
    /// </summary>
    private ProtocolResponse CommitUploads(ParsedRequest request, ConnectionState state, long now)
    {
        Require(request.Body, "capability", "projectId", "expectedSnapshotId", "files");
        var projectId = Id(request.Body, "projectId");
        var store = Project(request.Body);
        var expected = Id(request.Body, "expectedSnapshotId");
        var files = request.Body.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0) throw new RequestException("InvalidRequest");
        if (files.GetArrayLength() > MaxCommitUploads) throw new RequestException("LimitExceeded");
        lock (state.FileUploads)
        {
            DiscardIdle(state, now);
            var ids = new List<Guid>();
            var changes = new List<StoreChange>();
            foreach (var item in files.EnumerateArray())
            {
                Require(item, "uploadId", "name");
                var uploadId = Id(item, "uploadId");
                if (ids.Contains(uploadId) || !state.FileUploads.TryGetValue(uploadId, out var slot) || slot.Staged is not { } staged || slot.ProjectId != projectId)
                    throw new RequestException("InvalidRequest");
                var nameValue = item.GetProperty("name");
                string? name = nameValue.ValueKind == JsonValueKind.Null ? null : Text(item, "name");
                if ((name is null) == staged.NewFile || (name is not null && !RevisionStore.IsValidLogicalName(name))) throw new RequestException("InvalidRequest");
                ids.Add(uploadId);
                changes.Add(new StoreChange.Stage(staged, name));
            }
            var next = store.CommitChanges(expected, changes, state.Actor); // a conflict throws before anything moves
            var results = ids.Select(id => state.FileUploads[id].Staged!).ToList();
            foreach (var id in ids) state.FileUploads.Remove(id); // their records are in the project now
            return Ok(request, w =>
            {
                w.WriteString("snapshotId", next.SnapshotId.ToString("D"));
                w.WriteStartArray("files");
                foreach (var staged in results)
                {
                    w.WriteStartObject();
                    w.WriteString("fileId", staged.FileId.ToString("D")); w.WriteString("revisionId", staged.RevisionId.ToString("D"));
                    w.WriteString("kind", staged.Kind == FileKind.Text ? "text" : "binary");
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        }
    }

    private ProtocolResponse CancelUpload(ParsedRequest request, ConnectionState state)
    {
        Require(request.Body, "capability", "uploadId");
        var uploadId = Id(request.Body, "uploadId");
        lock (state.FileUploads)
        {
            if (!state.FileUploads.Remove(uploadId, out var slot)) throw new RequestException("InvalidRequest");
            slot.Discard();
        }
        return Ok(request, _ => { });
    }

    // An upload with no part for 2 minutes is discarded (staged ones wait for CommitUploads, but not forever).
    private void DiscardIdle(ConnectionState state, long now)
    {
        foreach (var (id, slot) in state.FileUploads.ToList())
            if (clock.GetElapsedTime(slot.LastActivity, now) >= UploadIdle) { slot.Discard(); state.FileUploads.Remove(id); }
    }

    /// <summary>One 512 KiB part of a text revision of at most 1 MiB (the editor's limit). Binary is NotText.</summary>
    private ProtocolResponse ReadTextPart(ParsedRequest request)
    {
        Require(request.Body, "capability", "projectId", "fileId", "revisionId", "offset");
        var store = Project(request.Body);
        var offsetValue = request.Body.GetProperty("offset");
        if (offsetValue.ValueKind != JsonValueKind.Number || !offsetValue.TryGetInt64(out long offset) || offset < 0 || offset % UploadPartBytes != 0) throw new RequestException("InvalidRequest");
        (long Length, byte[] Data) part;
        try { part = store.ReadTextPart(Id(request.Body, "fileId"), Id(request.Body, "revisionId"), offset); }
        catch (ArgumentOutOfRangeException) { throw new RequestException("InvalidRequest"); }
        try
        {
            return Ok(request, w =>
            {
                w.WriteNumber("length", part.Length);
                w.WriteBase64String("dataBase64", part.Data);
            }, capacity: 16384 + (part.Data.Length + 2) / 3 * 4); // pre-sized so the buffer never reallocates (see OpenDocument)
        }
        finally { CryptographicOperations.ZeroMemory(part.Data); }
    }

    /// <summary>Makes a revision from the file's own history current again; nothing is re-encrypted or sent.</summary>
    private ProtocolResponse RevertFile(ParsedRequest request, ConnectionState state)
    {
        Require(request.Body, "capability", "projectId", "fileId", "revisionId", "expectedSnapshotId");
        var store = Project(request.Body);
        return Changed(request, store, Id(request.Body, "expectedSnapshotId"), [new StoreChange.Revert(Id(request.Body, "fileId"), Id(request.Body, "revisionId"))], null, state);
    }

    /// <summary>Decrypts and checks every live file. A failure reports counts only, and the project is then Unavailable.</summary>
    private ProtocolResponse VerifyProject(ParsedRequest request)
    {
        Require(request.Body, "capability", "projectId");
        var id = Id(request.Body, "projectId");
        var (verified, failedFiles) = Project(request.Body).VerifyProject();
        if (failedFiles > 0 && failed.TryAdd(id, true)) ReportStatus();
        return Ok(request, w => { w.WriteNumber("filesVerified", verified); w.WriteNumber("filesFailed", failedFiles); });
    }

    private static byte[] Sha256(JsonElement body, string name)
    {
        var value = body.GetProperty(name);
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : null;
        if (text is null || text.Length != 64 || text.Any(c => c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))) throw new RequestException("InvalidRequest");
        return Convert.FromHexString(text);
    }

    private ProtocolResponse ApplyEdits(ParsedRequest request, ConnectionState state)
    {
        Require(request.Body, "capability", "projectId", "fileId", "expectedSnapshotId", "contentBase64");
        var store = Project(request.Body);
        var fileId = Id(request.Body, "fileId");
        var expected = Id(request.Body, "expectedSnapshotId");
        var content = Content(request.Body, textOnly: true); // the editor saving text
        try
        {
            var current = store.List();
            if (current.SnapshotId != expected) throw new RevisionConflictException();
            var entry = current.Files.SingleOrDefault(f => f.FileId == fileId) ?? throw new KeyNotFoundException();
            var next = store.CommitChanges(expected, [new StoreChange.Write(entry.LogicalName, content, "edited")], state.Actor); // same name keeps the same file id
            var file = next.Files.Single(f => f.FileId == fileId);
            return Ok(request, w =>
            {
                w.WriteString("snapshotId", next.SnapshotId.ToString("D"));
                w.WriteString("revisionId", file.RevisionId.ToString("D"));
            });
        }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    private RevisionStore Project(JsonElement body)
    {
        var id = Id(body, "projectId");
        if (!projects.ContainsKey(id)) throw new KeyNotFoundException();
        if (failed.ContainsKey(id)) throw new RequestException("Unavailable");
        return catalog.OpenProject(id);
    }

    private static void End(ConnectionState state)
    {
        state.DiscardUploads();
        if (state.Capability is not null) CryptographicOperations.ZeroMemory(state.Capability);
        state.Capability = null;
        state.Closed = true;
    }

    private static bool ValidCapability(JsonElement presented, byte[]? expected)
    {
        if (expected is null || presented.ValueKind != JsonValueKind.String) return false;
        Span<byte> candidate = stackalloc byte[33];
        bool valid = Convert.TryFromBase64String(presented.GetString()!, candidate, out int written) && written == 32 &&
            CryptographicOperations.FixedTimeEquals(candidate[..32], expected);
        CryptographicOperations.ZeroMemory(candidate);
        return valid;
    }

    private static byte[] Content(JsonElement body, bool textOnly)
    {
        var value = body.GetProperty("contentBase64");
        if (value.ValueKind != JsonValueKind.String || !value.TryGetBytesFromBase64(out var bytes)) throw new RequestException("InvalidRequest");
        if (bytes.Length > ProtocolLimits.MaxDocumentBytes) { CryptographicOperations.ZeroMemory(bytes); throw new RequestException("LimitExceeded"); }
        if (textOnly && !RevisionStore.IsValidContent(bytes)) { CryptographicOperations.ZeroMemory(bytes); throw new RequestException("InvalidRequest"); }
        return bytes;
    }

    // Inline opens return text of at most 256 KiB; binary is NotText, larger text is read in parts (ReadTextPart).
    private static void InlineText(StoreFile file)
    {
        if (file.Kind != FileKind.Text) throw new NotTextException();
        if (file.ByteLength > ProtocolLimits.MaxDocumentBytes) throw new RequestException("LimitExceeded");
    }

    private static string Text(JsonElement body, string name)
    {
        var value = body.GetProperty(name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new RequestException("InvalidRequest");
    }

    private static Guid Id(JsonElement body, string name)
    {
        var value = body.GetProperty(name);
        return value.ValueKind == JsonValueKind.String && ProtocolParser.TryParseId(value.GetString(), out var id) ? id : throw new RequestException("InvalidRequest");
    }

    private static void Require(JsonElement body, params string[] names)
    {
        if (!Fields(body, names)) throw new RequestException("InvalidRequest");
    }

    private static bool Fields(JsonElement body, params string[] names) =>
        body.ValueKind == JsonValueKind.Object && body.EnumerateObject().Count() == names.Length &&
        body.EnumerateObject().All(p => names.Contains(p.Name, StringComparer.Ordinal));

    private static ProtocolResponse Ok(ParsedRequest request, Action<Utf8JsonWriter> body, bool close = false, int capacity = 4096) =>
        Write(request.RequestId, "Ok", body, close, capacity);

    private static ProtocolResponse Error(ParsedRequest request, string code, bool close = false) =>
        Write(request.RequestId, code, null, close, 512);

    private static ProtocolResponse Write(Guid requestId, string code, Action<Utf8JsonWriter>? body, bool close, int capacity)
    {
        var buffer = new ArrayBufferWriter<byte>(capacity);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("version", ProtocolCatalog.V2.Version);
            w.WriteString("requestId", requestId.ToString("D"));
            w.WriteBoolean("ok", code == "Ok");
            w.WriteString("code", code);
            if (body is null) w.WriteNull("body");
            else { w.WriteStartObject("body"); body(w); w.WriteEndObject(); }
            w.WriteEndObject();
        }
        return new(buffer, close);
    }

    private sealed class RequestException(string code) : Exception(code)
    {
        internal string Code { get; } = code;
    }
}
