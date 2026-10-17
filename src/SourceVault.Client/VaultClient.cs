using System.Buffers;
using System.Security.Principal;
using System.Text.Json;
using SourceVault.Contracts;
using SourceVault.Windows;

namespace SourceVault.Client;

public sealed record VaultResponse(string Code, JsonElement Body)
{
    public bool Ok => Code == "Ok";
}

/// <summary>
/// Minimal protocol v2 client: one connection, one session, one request in flight (callers are serialized).
/// The capability never leaves this object.
/// </summary>
public sealed class VaultClient : IDisposable
{
    public const string ServiceAccount = @"NT SERVICE\SourceVault";
    private readonly Stream pipe;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? capability;

    public VaultClient(Stream pipe) => this.pipe = pipe;

    /// <summary>
    /// Connects to the installed service. First lets the service read this process's executable path (it is
    /// otherwise denied all access to user processes), then refuses any pipe not owned by the service account.
    /// Only installed SourceVault clients pass the service's executable check.
    /// </summary>
    public static VaultClient ConnectInstalled()
    {
        var service = (SecurityIdentifier)new NTAccount(ServiceAccount).Translate(typeof(SecurityIdentifier));
        VaultPipe.AllowServiceToIdentifyThisProcess(service);
        return new VaultClient(VaultPipe.Connect(VaultPipe.ProductionName, service, TimeSpan.FromSeconds(10)));
    }

    public VaultResponse Begin() => BeginAsync().GetAwaiter().GetResult();

    public async Task<VaultResponse> BeginAsync(CancellationToken cancellationToken = default)
    {
        var response = await CallAsync("BeginSession", null, null, cancellationToken).ConfigureAwait(false);
        if (response.Ok) capability = response.Body.GetProperty("capability").GetString();
        return response;
    }

    /// <summary>Synchronous convenience for console tools.</summary>
    public VaultResponse Call(string operation, Action<Utf8JsonWriter>? fields = null, string? capabilityOverride = null) =>
        CallAsync(operation, fields, capabilityOverride).GetAwaiter().GetResult();

    /// <summary>Sends one request. The capability is added automatically unless <paramref name="capabilityOverride"/> is given.</summary>
    public async Task<VaultResponse> CallAsync(string operation, Action<Utf8JsonWriter>? fields = null, string? capabilityOverride = null,
        CancellationToken cancellationToken = default)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("version", ProtocolCatalog.V2.Version);
            w.WriteString("requestId", Guid.NewGuid().ToString("D"));
            w.WriteString("operation", operation);
            w.WriteStartObject("body");
            var cap = capabilityOverride ?? (operation == "BeginSession" ? null : capability);
            if (cap is not null) w.WriteString("capability", cap);
            fields?.Invoke(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(pipe, buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            var frame = await FrameCodec.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(frame);
                var root = doc.RootElement;
                return new(root.GetProperty("code").GetString()!, root.GetProperty("body").Clone());
            }
            finally { Array.Clear(frame); }
        }
        finally
        {
            gate.Release();
            buffer.Clear(); // zeroes request bytes, which may include content
        }
    }

    public void Dispose()
    {
        capability = null;
        pipe.Dispose();
        gate.Dispose();
    }
}
