namespace SourceVault.Service;

internal enum ServiceState : uint { Stopped = 1, StartPending = 2, StopPending = 3, Running = 4 }
internal readonly record struct Status(ServiceState State, uint AcceptedControls, uint ExitCode, uint Checkpoint, uint WaitHint, uint ServiceSpecificExitCode = 0);

// Internal test seam; production has a single fixed initialization path.
internal sealed class ServiceLifecycle : IDisposable
{
    private readonly object sync = new();
    private readonly ManualResetEventSlim stop = new(false);
    private readonly Action<Status> report;
    private bool started;
    private ServiceState state = ServiceState.Stopped;
    internal ServiceLifecycle(Action<Status> report) => this.report = report;
    internal void RequestStop()
    {
        lock (sync)
        {
            if (state is ServiceState.Stopped or ServiceState.StopPending) return;
            try { Publish(ServiceState.StopPending, 0); }
            finally { stop.Set(); }
        }
    }
    internal void Run(Func<IDisposable> initialize)
    {
        lock (sync)
        {
            if (started) throw new InvalidOperationException();
            started = true;
        }
        uint exit = 0;
        IDisposable? resource = null;
        try
        {
            lock (sync)
            {
                Publish(ServiceState.StartPending, 0);
            }
            resource = initialize();
            lock (sync)
            {
                if (state == ServiceState.StartPending) Publish(ServiceState.Running, 0);
            }
            stop.Wait();
        }
        catch { exit = 1064; } // ERROR_EXCEPTION_IN_SERVICE, no exception text or secret metadata.
        finally
        {
            try { resource?.Dispose(); } catch { exit = 1064; }
            lock (sync)
            {
                if (state != ServiceState.Stopped) Publish(ServiceState.Stopped, exit);
            }
        }
    }
    private void Publish(ServiceState next, uint exit)
    {
        state = next;
        report(new(next, next == ServiceState.Running ? 5u : 0u, exit,
            next is ServiceState.StartPending or ServiceState.StopPending ? 1u : 0u,
            next is ServiceState.StartPending or ServiceState.StopPending ? 15000u : 0u));
    }
    public void Dispose() => stop.Dispose();
}

