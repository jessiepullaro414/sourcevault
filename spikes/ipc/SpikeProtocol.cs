using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using SourceVault.Contracts;

namespace SourceVault.IpcSpike;

internal sealed class SpikeProtocol(PeerPolicy policy, TimeProvider? timeProvider = null)
{
    internal static readonly Guid ProjectId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private byte[]? capability;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long started;
    private long lastActivity;
    private bool expired;

    internal async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken);
            for (int count = 0; count < 16; count++) // fixed connection/request budget for the spike
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                byte[] bytes = await FrameCodec.ReadAsync(pipe, deadline.Token);
                policy.Validate(NativePipe.ClientPeer(pipe));
                var request = ProtocolParser.ParseRequest(bytes);
                object response = Handle(request);
                await FrameCodec.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(response), deadline.Token);
            }
        }
        finally
        {
            if (capability is not null) CryptographicOperations.ZeroMemory(capability);
            capability = null;
        }
    }

    internal object Handle(RequestEnvelope request)
    {
        long now = clock.GetTimestamp();
        string code = "Ok";
        object? body = null;
        if (capability is not null && (clock.GetElapsedTime(started, now) >= TimeSpan.FromMinutes(30) || clock.GetElapsedTime(lastActivity, now) >= TimeSpan.FromMinutes(5)))
        {
            expired = true;
            CryptographicOperations.ZeroMemory(capability);
            capability = null;
        }
        if (expired) code = "SessionExpired";
        else if (request.Operation is not ("BeginSession" or "GetBuildStatus")) code = "UnsupportedOperation";
        else if (request.Operation == "BeginSession")
        {
            if (!Fields(request.Body, "projectId") || !Uuid(request.Body, "projectId", out var project)) code = "InvalidRequest";
            else if (project != ProjectId) code = "NotFound";
            else if (capability is not null) code = "InvalidRequest";
            else
            {
                capability = RandomNumberGenerator.GetBytes(32);
                started = lastActivity = now;
                body = new { capability = Convert.ToBase64String(capability), idleExpirySeconds = 300, absoluteExpirySeconds = 1800 };
            }
        }
        else if (!Fields(request.Body, "capability", "buildId") || !Uuid(request.Body, "buildId", out _)) code = "InvalidRequest";
        else if (capability is null) code = "Unauthorized";
        else if (!ValidCapability(request.Body)) code = "Unauthorized";
        else
        {
            lastActivity = now;
            // No build execution or source access exists in this spike.
            code = "NotFound";
        }
        return new { version = 1, requestId = request.RequestId.ToString("D"), ok = code == "Ok", code, body };
    }

    private bool ValidCapability(JsonElement body)
    {
        var value = body.GetProperty("capability");
        if (value.ValueKind != JsonValueKind.String) return false;
        Span<byte> candidate = stackalloc byte[32];
        bool valid = Convert.TryFromBase64String(value.GetString()!, candidate, out int written) && written == 32 &&
            CryptographicOperations.FixedTimeEquals(candidate, capability!);
        CryptographicOperations.ZeroMemory(candidate);
        return valid;
    }
    private static bool Fields(JsonElement body, params string[] names) =>
        body.ValueKind == JsonValueKind.Object && body.EnumerateObject().Count() == names.Length &&
        body.EnumerateObject().All(p => names.Contains(p.Name, StringComparer.Ordinal));
    private static bool Uuid(JsonElement body, string name, out Guid value)
    {
        value = Guid.Empty;
        var field = body.GetProperty(name);
        return field.ValueKind == JsonValueKind.String && Guid.TryParseExact(field.GetString(), "D", out value) &&
            value != Guid.Empty && field.GetString() == value.ToString("D");
    }
}
