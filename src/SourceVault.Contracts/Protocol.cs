using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SourceVault.Contracts;

public static class ProtocolLimits
{
    public const int Version = 1;
    public const int MaxFrameBytes = 1_048_576;
    public const int MaxJsonDepth = 16;
    public const int MaxDocumentBytes = 256 * 1024;
    public static readonly TimeSpan FrameDeadline = TimeSpan.FromSeconds(10);
}

public sealed class ProtocolException : Exception
{
    // Deliberately never include peer-controlled text or parser exception details.
    public string Code { get; }
    public ProtocolException(string code) : base(code) => Code = code;
}

public sealed record RequestEnvelope(int Version, Guid RequestId, string Operation, JsonElement Body);

public static class FrameCodec
{
    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProtocolLimits.FrameDeadline);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > ProtocolLimits.MaxFrameBytes)
            throw new ProtocolException("LimitExceeded");
        var payload = new byte[(int)length];
        try
        {
            await stream.ReadExactlyAsync(payload, deadline.Token).ConfigureAwait(false);
            return payload;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(payload);
            throw;
        }
    }

    /// <summary>
    /// For long-lived connections: waits up to <paramref name="idleTimeout"/> for a frame to begin, then requires the
    /// rest of the frame within <see cref="ProtocolLimits.FrameDeadline"/>. Returns null on a clean close between frames.
    /// </summary>
    public static async Task<byte[]?> ReadAfterIdleAsync(Stream stream, TimeSpan idleTimeout, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            idle.CancelAfter(idleTimeout);
            if (await stream.ReadAsync(header.AsMemory(0, 1), idle.Token).ConfigureAwait(false) == 0) return null;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProtocolLimits.FrameDeadline);
        await stream.ReadExactlyAsync(header.AsMemory(1), deadline.Token).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > ProtocolLimits.MaxFrameBytes) throw new ProtocolException("LimitExceeded");
        var payload = new byte[(int)length];
        try
        {
            await stream.ReadExactlyAsync(payload, deadline.Token).ConfigureAwait(false);
            return payload;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(payload);
            throw;
        }
    }

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length is 0 or > ProtocolLimits.MaxFrameBytes)
            throw new ProtocolException("LimitExceeded");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProtocolLimits.FrameDeadline);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
        await stream.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
        await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
    }
}

/// <summary>A protocol version and its exact operation catalog.</summary>
public sealed class ProtocolCatalog
{
    public int Version { get; }
    public IReadOnlySet<string> Operations { get; }
    private ProtocolCatalog(int version, params string[] operations) =>
        (Version, Operations) = (version, new HashSet<string>(operations, StringComparer.Ordinal));

    public static readonly ProtocolCatalog V1 = new(1,
        "BeginSession", "ListProjects", "ListFiles", "OpenDocument", "ApplyEdits",
        "CloseDocument", "RequestBuild", "GetBuildStatus", "EndSession");

    /// <summary>contracts/protocol-v2.md: sessions not bound to a project, project/file creation, no CloseDocument.</summary>
    public static readonly ProtocolCatalog V2 = new(2,
        "BeginSession", "ListProjects", "CreateProject", "ListFiles", "OpenDocument", "CreateFile",
        "ImportFiles", "DeleteProject", "RenameProject", "RenameFile", "RenameFolder", "DeleteFile", "ListHistory", "OpenRevision",
        "ListDeletedFiles", "RestoreFile", "ApplyEdits", "EndSession", "ListBuildTargets", "ListBuilds", "OpenBuildLog",
        "RequestBuild", "GetBuildStatus", "GetImportRecipient", "BeginPackageImport", "PackageImportPart", "FinishPackageImport");
}

/// <summary>
/// A parsed request that keeps the parsed document over the caller's frame buffer instead of copying it, so
/// request content exists only in that buffer, which the caller zeroes after disposing this object.
/// </summary>
public sealed class ParsedRequest : IDisposable
{
    private readonly JsonDocument document;
    internal ParsedRequest(JsonDocument document, Guid requestId, string operation, JsonElement body) =>
        (this.document, RequestId, Operation, Body) = (document, requestId, operation, body);
    public Guid RequestId { get; }
    public string Operation { get; }
    public JsonElement Body { get; }
    public void Dispose() => document.Dispose();
}

public static class ProtocolParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static RequestEnvelope ParseRequest(ReadOnlySpan<byte> json)
    {
        using var parsed = Parse(json.ToArray(), ProtocolCatalog.V1);
        return new RequestEnvelope(ProtocolCatalog.V1.Version, parsed.RequestId, parsed.Operation, parsed.Body.Clone());
    }

    /// <summary>Strict parse against <paramref name="catalog"/> without copying the frame. Dispose before zeroing it.</summary>
    public static ParsedRequest ParseRequest(ReadOnlyMemory<byte> frame, ProtocolCatalog catalog) => Parse(frame, catalog);

    private static ParsedRequest Parse(ReadOnlyMemory<byte> frame, ProtocolCatalog catalog)
    {
        var json = frame.Span;
        if (json.Length is 0 or > ProtocolLimits.MaxFrameBytes)
            throw new ProtocolException("LimitExceeded");
        JsonDocument? document = null;
        try
        {
            _ = StrictUtf8.GetCharCount(json);
            RejectDuplicateProperties(json);
            document = JsonDocument.Parse(frame, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = ProtocolLimits.MaxJsonDepth
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ProtocolException("InvalidRequest");
            int fields = 0;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is not ("version" or "requestId" or "operation" or "body"))
                    throw new ProtocolException("InvalidRequest");
                fields++;
            }
            if (fields != 4 || !root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int versionNumber))
                throw new ProtocolException("InvalidRequest");
            if (versionNumber != catalog.Version)
                throw new ProtocolException("UnsupportedVersion");
            if (!root.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String ||
                !TryParseId(id.GetString(), out Guid requestId))
                throw new ProtocolException("InvalidRequest");
            if (!root.TryGetProperty("operation", out var operation) ||
                operation.ValueKind != JsonValueKind.String || operation.GetString() is not string operationName)
                throw new ProtocolException("InvalidRequest");
            if (!catalog.Operations.Contains(operationName))
                throw new ProtocolException("UnsupportedOperation");
            if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
                throw new ProtocolException("InvalidRequest");
            var parsed = new ParsedRequest(document, requestId, operationName, body);
            document = null; // owned by the result
            return parsed;
        }
        catch (JsonException) { throw new ProtocolException("InvalidRequest"); }
        catch (DecoderFallbackException) { throw new ProtocolException("InvalidRequest"); }
        catch (InvalidOperationException) { throw new ProtocolException("InvalidRequest"); }
        finally { document?.Dispose(); }
    }

    public static bool TryParseId(string? text, out Guid id) =>
        Guid.TryParseExact(text, "D", out id) && id != Guid.Empty &&
        string.Equals(text, id.ToString("D"), StringComparison.Ordinal);

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = ProtocolLimits.MaxJsonDepth
        });
        var properties = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    properties.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    properties.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (!properties.Peek().Add(reader.GetString()!))
                        throw new ProtocolException("InvalidRequest");
                    break;
            }
        }
    }
}
