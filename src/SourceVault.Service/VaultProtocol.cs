using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using SourceVault.Contracts;
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
    private readonly TimeProvider clock;
    // Projects created or verified in this process. Only the service creates projects, so this is authoritative.
    private readonly ConcurrentDictionary<Guid, bool> projects = new();

    internal VaultProtocol(ProjectCatalog catalog, IEnumerable<ProjectInfo> verified, TimeProvider? clock = null, BuildCoordinator? builds = null)
    {
        this.catalog = catalog;
        this.builds = builds;
        this.clock = clock ?? TimeProvider.System;
        foreach (var p in verified) projects[p.ProjectId] = true;
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
            if (!Fields(body) || state.Capability is not null) return Error(request, "InvalidRequest");
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
        catch (RequestException e) { return Error(request, e.Code); }
        catch (SourceVault.Build.BuildServiceUnavailableException) { return Error(request, "Unavailable"); }
        // Input is validated before the store is called, so anything the store or catalog rejects here is an
        // integrity, availability or key failure of the vault itself.
        catch (Exception e) when (e is VaultCatalogException or CryptographicException or IOException or InvalidDataException
            or ArgumentException or UnauthorizedAccessException) { return Error(request, "Unavailable"); }
        catch (Exception e) when (e is not OutOfMemoryException) { return Error(request, "InternalError"); }
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
        var verified = catalog.OpenAndVerify();
        foreach (var p in verified) projects[p.ProjectId] = true;        return Ok(request, w =>
        {
            w.WriteStartArray("projects");
            foreach (var p in verified)
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
        var names = builds!.Targets();
        return Ok(request, w => { w.WriteStartArray("targets"); foreach (var n in names) w.WriteStringValue(n); w.WriteEndArray(); });
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
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }

    private ProtocolResponse OpenDocument(ParsedRequest request)
    {
        Require(request.Body, "capability", "projectId", "fileId", "expectedSnapshotId");
        var store = Project(request.Body);
        var fileId = Id(request.Body, "fileId");
        var expected = Id(request.Body, "expectedSnapshotId");
        var snapshot = store.List();
        if (snapshot.SnapshotId != expected) throw new RevisionConflictException();
        var revision = (snapshot.Files.SingleOrDefault(f => f.FileId == fileId) ?? throw new KeyNotFoundException()).RevisionId;
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
        var content = Content(request.Body);
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
                documents.Add((name, Content(file)));
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

    private ProtocolResponse ApplyEdits(ParsedRequest request, ConnectionState state)
    {
        Require(request.Body, "capability", "projectId", "fileId", "expectedSnapshotId", "contentBase64");
        var store = Project(request.Body);
        var fileId = Id(request.Body, "fileId");
        var expected = Id(request.Body, "expectedSnapshotId");
        var content = Content(request.Body);
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
        return catalog.OpenProject(id);
    }

    private static void End(ConnectionState state)
    {
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

    private static byte[] Content(JsonElement body)
    {
        var value = body.GetProperty("contentBase64");
        if (value.ValueKind != JsonValueKind.String || !value.TryGetBytesFromBase64(out var bytes)) throw new RequestException("InvalidRequest");
        if (bytes.Length > ProtocolLimits.MaxDocumentBytes) { CryptographicOperations.ZeroMemory(bytes); throw new RequestException("LimitExceeded"); }
        if (!RevisionStore.IsValidContent(bytes)) { CryptographicOperations.ZeroMemory(bytes); throw new RequestException("InvalidRequest"); }
        return bytes;
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
