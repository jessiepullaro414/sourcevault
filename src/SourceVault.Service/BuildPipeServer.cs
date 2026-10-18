using System.IO.Pipes;
using System.Security.Principal;
using SourceVault.Build;
using SourceVault.Windows;

namespace SourceVault.Service;

/// <summary>
/// The build service's private pipe (<see cref="BuildChannel.PipeName"/>): owned by the build service account, usable
/// only by the vault service account, which every connection must prove through its OS token. Up to 4 connections
/// (one build per target runs at a time). Nothing is logged; any failure closes that connection only.
/// </summary>
internal sealed class BuildPipeServer : IDisposable
{
    private const int Instances = 4;
    private readonly string name;
    private readonly SecurityIdentifier serverAccount, vaultAccount;
    private readonly Func<BuildConfig> loadConfig;
    private readonly Func<BuildTarget, IDockerEngine> engineFor;
    private readonly CancellationTokenSource stopping = new();
    private readonly List<Task> listeners = [];

    internal BuildPipeServer(string name, SecurityIdentifier serverAccount, SecurityIdentifier vaultAccount, Func<BuildConfig> loadConfig, Func<BuildTarget, IDockerEngine> engineFor) =>
        (this.name, this.serverAccount, this.vaultAccount, this.loadConfig, this.engineFor) = (name, serverAccount, vaultAccount, loadConfig, engineFor);

    /// <summary>Creates the first instance synchronously, so a squatted name fails startup, then starts listening.</summary>
    internal void Start()
    {
        var first = VaultPipe.CreatePrivateServerInstance(name, serverAccount, vaultAccount, first: true);
        listeners.Add(Task.Run(() => ListenAsync(first)));
        for (int i = 1; i < Instances; i++) listeners.Add(Task.Run(() => ListenAsync(null)));
    }

    private async Task ListenAsync(NamedPipeServerStream? initial)
    {
        var next = initial;
        while (!stopping.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = next ?? VaultPipe.CreatePrivateServerInstance(name, serverAccount, vaultAccount, first: false); }
            catch when (!stopping.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(1), stopping.Token).ConfigureAwait(false); continue; }
            catch { return; }
            next = null;
            try
            {
                await pipe.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                VaultPipe.ValidatePrivateClient(pipe, vaultAccount);
                await BuildChannel.ServeAsync(pipe, loadConfig, engineFor, stopping.Token).ConfigureAwait(false);
            }
            catch { /* this connection only; never reported */ }
            finally { try { pipe.Dispose(); } catch { } }
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        try { Task.WaitAll([.. listeners], TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
        stopping.Dispose();
    }
}
