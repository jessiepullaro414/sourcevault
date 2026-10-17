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
    private readonly TimeProvider clock;
    // Projects created or verified in this process. Only the service creates projects, so this is authoritative.
    private readonly ConcurrentDictionary<Guid, bool> projects = new();

    internal VaultProtocol(ProjectCatalog catalog, IEnumerable<ProjectInfo> verified, TimeProvider? clock = null)
    {
        this.catalog = catalog;
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
                "CreateFile" => CreateFile(request),
                "ApplyEdits" => ApplyEdits(request),
                "RequestBuild" or "GetBuildStatus" => Error(request, "Unavailable"),
                _ => Error(request, "UnsupportedOperation"),
            };
        }
        catch (RevisionConflictException) { return Error(request, "RevisionConflict"); }
        catch (KeyNotFoundException) { return Error(request, "NotFound"); }
        catch (NameExistsException) { return Error(request, "AlreadyExists"); }
        catch (RequestException e) { return Error(request, e.Code); }
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
        foreach (var p in verified) projects[p.ProjectId] = true;
        return Ok(request, w =>
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

    private ProtocolResponse CreateFile(ParsedRequest request)
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
            var next = store.Commit(expected, name, content);
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

    private ProtocolResponse ApplyEdits(ParsedRequest request)
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
            var next = store.Commit(expected, entry.LogicalName, content); // same name keeps the same file id
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
