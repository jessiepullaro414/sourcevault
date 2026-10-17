using System.Buffers;
using System.Security.Principal;
using System.Text.Json;
using SourceVault.Contracts;
using SourceVault.Windows;

namespace SourceVault.LabClient;

internal sealed record VaultResponse(string Code, JsonElement Body)
{
    internal bool Ok => Code == "Ok";
}

/// <summary>Minimal protocol v2 client. One connection, one session, one request at a time.</summary>
internal sealed class VaultClient : IDisposable
{
    internal const string ServiceAccount = @"NT SERVICE\SourceVault";
    private readonly Stream pipe;
    private string? capability;

    internal VaultClient(Stream pipe) => this.pipe = pipe;

    /// <summary>
    /// Connects to the installed service. First lets the service read this process's executable path (it is
    /// otherwise denied all access to user processes), then refuses any pipe not owned by the service account.
    /// </summary>
    internal static VaultClient ConnectInstalled()
    {
        var service = (SecurityIdentifier)new NTAccount(ServiceAccount).Translate(typeof(SecurityIdentifier));
        VaultPipe.AllowServiceToIdentifyThisProcess(service);
        return new VaultClient(VaultPipe.Connect(VaultPipe.ProductionName, service, TimeSpan.FromSeconds(10)));
    }

    internal VaultResponse Begin()
    {
        var response = Call("BeginSession");
        if (response.Ok) capability = response.Body.GetProperty("capability").GetString();
        return response;
    }

    /// <summary>Sends one request. The capability is added automatically unless <paramref name="capabilityOverride"/> is given.</summary>
    internal VaultResponse Call(string operation, Action<Utf8JsonWriter>? fields = null, string? capabilityOverride = null)
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
        try
        {
            FrameCodec.WriteAsync(pipe, buffer.WrittenMemory).GetAwaiter().GetResult();
            var frame = FrameCodec.ReadAsync(pipe).GetAwaiter().GetResult();
            try
            {
                using var doc = JsonDocument.Parse(frame);
                var root = doc.RootElement;
                return new(root.GetProperty("code").GetString()!, root.GetProperty("body").Clone());
            }
            finally { Array.Clear(frame); }
        }
        finally { buffer.Clear(); }
    }

    public void Dispose()
    {
        capability = null;
        pipe.Dispose();
    }
}
