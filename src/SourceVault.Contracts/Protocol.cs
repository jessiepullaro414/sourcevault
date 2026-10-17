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

public static class ProtocolParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
    {
        "BeginSession", "ListProjects", "ListFiles", "OpenDocument", "ApplyEdits",
        "CloseDocument", "RequestBuild", "GetBuildStatus", "EndSession"
    };

    public static RequestEnvelope ParseRequest(ReadOnlySpan<byte> json)
    {
        if (json.Length is 0 or > ProtocolLimits.MaxFrameBytes)
            throw new ProtocolException("LimitExceeded");
        try
        {
            _ = StrictUtf8.GetCharCount(json);
            RejectDuplicateProperties(json);
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
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
            if (versionNumber != ProtocolLimits.Version)
                throw new ProtocolException("UnsupportedVersion");
            if (!root.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String ||
                !TryParseId(id.GetString(), out Guid requestId))
                throw new ProtocolException("InvalidRequest");
            if (!root.TryGetProperty("operation", out var operation) ||
                operation.ValueKind != JsonValueKind.String || operation.GetString() is not string operationName)
                throw new ProtocolException("InvalidRequest");
            if (!Operations.Contains(operationName))
                throw new ProtocolException("UnsupportedOperation");
            if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
                throw new ProtocolException("InvalidRequest");
            return new RequestEnvelope(versionNumber, requestId, operationName, body.Clone());
        }
        catch (JsonException) { throw new ProtocolException("InvalidRequest"); }
        catch (DecoderFallbackException) { throw new ProtocolException("InvalidRequest"); }
        catch (InvalidOperationException) { throw new ProtocolException("InvalidRequest"); }
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
