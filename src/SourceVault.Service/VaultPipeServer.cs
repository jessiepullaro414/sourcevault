using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using SourceVault.Contracts;
using SourceVault.Windows;

namespace SourceVault.Service;

/// <summary>
/// Serves protocol v2 on the vault pipe: up to <see cref="VaultPipe.MaxInstances"/> connections, one request in
/// flight each. Every frame is re-checked against the peer policy and must come from the same user, logon session
/// and process as the first. Malformed frames, policy failures and terminal session states close the connection
/// without reflecting any detail. Nothing is logged.
/// </summary>
internal sealed class VaultPipeServer : IDisposable
{
    private readonly string name;
    private readonly SecurityIdentifier serverAccount;
    private readonly PipePeerPolicy policy;
    private readonly VaultProtocol protocol;
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<ConnectionState, NamedPipeServerStream> active = new();
    private readonly List<Task> listeners = [];

    internal VaultPipeServer(string name, SecurityIdentifier serverAccount, PipePeerPolicy policy, VaultProtocol protocol) =>
        (this.name, this.serverAccount, this.policy, this.protocol) = (name, serverAccount, policy, protocol);

    /// <summary>Creates the first instance synchronously, so a squatted name fails startup, then starts listening.</summary>
    internal void Start()
    {
        var first = VaultPipe.CreateServerInstance(name, serverAccount, policy.OperatorsGroup, first: true);
        listeners.Add(Task.Run(() => ListenAsync(first)));
        for (int i = 1; i < VaultPipe.MaxInstances; i++) listeners.Add(Task.Run(() => ListenAsync(null)));
    }

    /// <summary>Lock, logoff or disconnect of a Windows session ends every vault session from it.</summary>
    internal void EndWindowsSession(uint sessionId)
    {
        foreach (var (state, pipe) in active)
            if (state.WindowsSession == sessionId) Close(state, pipe);
    }

    private async Task ListenAsync(NamedPipeServerStream? initial)
    {
        var next = initial;
        while (!stopping.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = next ?? VaultPipe.CreateServerInstance(name, serverAccount, policy.OperatorsGroup, first: false); }
            catch when (!stopping.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(1), stopping.Token).ConfigureAwait(false); continue; }
            catch { return; }
            next = null;
            var state = new ConnectionState();
            try
            {
                await pipe.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                var connectedUtc = DateTime.UtcNow; // the client process must be older than this (PID-reuse check)
                active[state] = pipe;
                await ServeAsync(pipe, state, connectedUtc).ConfigureAwait(false);
            }
            catch { /* any failure closes this connection only; details are never reported */ }
            finally { Close(state, pipe); active.TryRemove(state, out _); }
        }
    }

    // The operator's account name ("PC\alice") for file history, which stores it only inside encrypted records.
    // Falls back to the SID text when the name can't be resolved.
    private static string AccountName(SecurityIdentifier user)
    {
        try { return user.Translate(typeof(NTAccount)).Value; }
        catch (Exception e) when (e is IdentityNotMappedException or SystemException) { return user.Value; }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, ConnectionState state, DateTime connectedUtc)
    {
        PipePeer? first = null;
        while (!state.Closed && !stopping.IsCancellationRequested)
        {
            var frame = await FrameCodec.ReadAfterIdleAsync(pipe, VaultProtocol.IdleExpiry, stopping.Token).ConfigureAwait(false);
            if (frame is null) return;
            try
            {
                var peer = VaultPipe.ValidateClient(pipe, policy, connectedUtc);
                if (first is null)
                {
                    policy.ValidateInstalledFiles(peer.ImagePath); // hash pins, once per connection
                    first = peer; state.WindowsSession = peer.Session; state.Actor = AccountName(peer.User); state.User = peer.User;
                }
                else if (!peer.User.Equals(first.User) || peer.Session != first.Session || peer.ProcessId != first.ProcessId || peer.ProcessCreated != first.ProcessCreated) return;
                ProtocolResponse response;
                using (var request = ProtocolParser.ParseRequest(frame, ProtocolCatalog.V2))
                    response = protocol.Handle(request, state);
                try { await FrameCodec.WriteAsync(pipe, response.Buffer.WrittenMemory, stopping.Token).ConfigureAwait(false); }
                finally { response.Buffer.Clear(); } // zeroes the written bytes
                if (response.Close) return;
            }
            finally { CryptographicOperations.ZeroMemory(frame); }
        }
    }

    private static void Close(ConnectionState state, NamedPipeServerStream pipe)
    {
        if (state.Capability is not null) CryptographicOperations.ZeroMemory(state.Capability);
        state.Capability = null;
        state.Closed = true;
        state.OnClose?.Invoke(); // ends an unfinished package upload and frees its slot
        state.DiscardUploads(); // file uploads never outlive their connection
        state.OnClose = null;
        try { pipe.Dispose(); } catch { /* already closed */ }
    }

    public void Dispose()
    {
        stopping.Cancel();
        foreach (var (state, pipe) in active) Close(state, pipe);
        try { Task.WaitAll([.. listeners], TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        stopping.Dispose();
    }
}
