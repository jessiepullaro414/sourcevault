using System.Runtime.InteropServices;
using SourceVault.Storage;
using SourceVault.Windows;

namespace SourceVault.Service;

internal sealed class ScmHost
{
    internal const string ServiceName = "SourceVault";
    private readonly Native.ServiceMain main;
    private readonly Native.Handler handler;
    private IntPtr statusHandle;
    private volatile ServiceLifecycle? lifecycle;
    private int failure;
    internal ScmHost() { main = ServiceMain; handler = HandleControl; }
    internal int Run()
    {
        if (!OperatingSystem.IsWindows()) return 3;
        var table = new[] { new Native.TableEntry { Name = ServiceName, Main = main }, new Native.TableEntry() };
        if (!Native.StartServiceCtrlDispatcherW(table)) return 3;
        GC.KeepAlive(main);
        GC.KeepAlive(handler);
        return failure;
    }
    private void ServiceMain(uint count, IntPtr arguments)
    {
        // No exception may cross an unmanaged callback boundary.
        try
        {
            statusHandle = Native.RegisterServiceCtrlHandlerExW(ServiceName, handler, IntPtr.Zero);
            if (statusHandle == IntPtr.Zero) { failure = 3; return; }
            switch (ParseStartMode(ReadArguments(count, arguments)))
            {
                case StartMode.ProvisionKey: Provision(); return;
                case StartMode.CreateSyntheticProject: CreateSyntheticProject(); return;
                case StartMode.Invalid: Report(new(ServiceState.Stopped, 0, 87, 0, 0)); return; // ERROR_INVALID_PARAMETER
            }
            using var instance = new ServiceLifecycle(Report);
            lifecycle = instance;
            // RUNNING only after identity, config, vault root, key and every project have been verified.
            // No pipe, document or build endpoint exists here yet.
            instance.Run(() => OpenVerifiedVault().Key);
        }
        catch { failure = 3; }
        finally { lifecycle = null; }
    }
    internal enum StartMode { Run, ProvisionKey, CreateSyntheticProject, Invalid }

    // SCM passes the service name as argv[0], then any `sc start SourceVault <args>` start parameters.
    // Only the fixed lab parameters are accepted; everything else is refused.
    internal static StartMode ParseStartMode(IReadOnlyList<string?> args) => args.Count switch
    {
        1 => StartMode.Run,
        2 when args[1] == "provision-key" => StartMode.ProvisionKey,
        2 when args[1] == "create-synthetic-project" => StartMode.CreateSyntheticProject,
        _ => StartMode.Invalid,
    };

    // Identity, config, trusted vault root, existing key, then every project authenticated. The caller owns the key.
    private static (TpmKeyProtector Key, ProjectCatalog Catalog, IReadOnlyList<ProjectInfo> Projects) OpenVerifiedVault()
    {
        TpmKeyProtector.RequireServiceIdentity();
        var config = ServiceConfiguration.LoadFromApplicationDirectory();
        if (!VaultRoot.Validate().Passed) throw new StartupRefusedException(StartupRefusedException.VaultRootUntrusted);
        var key = TpmKeyProtector.OpenExisting(config.KeyName);
        try
        {
            var catalog = new ProjectCatalog(VaultRoot.Path, key);
            try { return (key, catalog, catalog.OpenAndVerify()); }
            catch (VaultCatalogException e)
            {
                throw new StartupRefusedException(e.Failure == CatalogFailure.Project
                    ? StartupRefusedException.ProjectUntrusted : StartupRefusedException.CatalogUntrusted);
            }
        }
        catch { key.Dispose(); throw; }
    }

    // LAB ONLY. One-shot creation of a synthetic project (fixed fictional content) under the service identity,
    // then a full re-verification. Lets the owner exercise the encrypted store on the real TPM without any
    // document endpoint. Outcome is only the exit code.
    private void CreateSyntheticProject()
    {
        Report(new(ServiceState.StartPending, 0, 0, 1, 60000));
        uint win32 = 0, specific = 0;
        try
        {
            var (key, catalog, projects) = OpenVerifiedVault();
            using (key)
            {
                var project = catalog.CreateProject($"Synthetic demo {projects.Count + 1}");
                var store = catalog.OpenProject(project.ProjectId);
                var snapshot = project.Snapshot;
                foreach (var (name, text) in SyntheticFiles)
                    snapshot = store.Commit(snapshot.SnapshotId, name, System.Text.Encoding.UTF8.GetBytes(text));
                if (catalog.OpenAndVerify().Count != projects.Count + 1) throw new InvalidOperationException();
            }
        }
        catch (StartupRefusedException refused) { win32 = 1066; specific = refused.Code; }
        catch { win32 = 1064; }
        Report(new(ServiceState.Stopped, 0, win32, 0, 0, specific));
    }

    private static readonly (string Name, string Text)[] SyntheticFiles =
    [
        ("README.md", "# Synthetic demo project\n\nFictional content created by the SourceVault lab service. Not real source.\n"),
        ("main.ts", "import { greet } from './util';\n\nexport function main(): string {\n  return greet('synthetic');\n}\n"),
        ("util.ts", "export function greet(name: string): string {\n  return `hello, ${name}`;\n}\n"),
    ];

    private static string?[] ReadArguments(uint count, IntPtr arguments)
    {
        if (count > 8 || (count > 0 && arguments == IntPtr.Zero)) return [];
        var result = new string?[count];
        for (var i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, i * IntPtr.Size));
        return result;
    }

    // One-shot key creation under the service identity. Reports START_PENDING, creates and checks the key,
    // then STOPPED. The outcome is only the service-specific exit code; nothing is logged.
    private void Provision()
    {
        Report(new(ServiceState.StartPending, 0, 0, 1, 60000));
        var outcome = ProvisionOutcome.Unavailable;
        try { outcome = TpmKeyProvisioner.CreateDecryptOnly(ServiceConfiguration.LoadFromApplicationDirectory().KeyName); }
        catch { outcome = ProvisionOutcome.Unavailable; }
        Report(outcome == ProvisionOutcome.Created
            ? new(ServiceState.Stopped, 0, 0, 0, 0)
            : new(ServiceState.Stopped, 0, 1066, 0, 0, (uint)outcome)); // ERROR_SERVICE_SPECIFIC_ERROR
    }

    private uint HandleControl(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        try
        {
            if (control is 1 or 5) { lifecycle?.RequestStop(); return 0; }
            if (control == 4) return 0; // Interrogate: status unchanged; no redundant SetServiceStatus call.
            return 120; // ERROR_CALL_NOT_IMPLEMENTED.
        }
        catch { failure = 3; return 1064; }
    }
    private void Report(Status value)
    {
        var status = new Native.ServiceStatus
        {
            Type = 0x10, State = (uint)value.State, Accepted = value.AcceptedControls,
            Win32ExitCode = value.ExitCode, ServiceSpecificExitCode = value.ServiceSpecificExitCode,
            Checkpoint = value.Checkpoint, WaitHint = value.WaitHint
        };
        if (!Native.SetServiceStatus(statusHandle, ref status)) throw new InvalidOperationException();
        if (value.ExitCode != 0) failure = 3;
    }
    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void ServiceMain(uint count, IntPtr arguments);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate uint Handler(uint control, uint eventType, IntPtr eventData, IntPtr context);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct TableEntry
        {
            [MarshalAs(UnmanagedType.LPWStr)] internal string? Name;
            internal ServiceMain? Main;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ServiceStatus
        {
            internal uint Type, State, Accepted, Win32ExitCode, ServiceSpecificExitCode, Checkpoint, WaitHint;
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool StartServiceCtrlDispatcherW([In] TableEntry[] table);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern IntPtr RegisterServiceCtrlHandlerExW(string name, Handler handler, IntPtr context);
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
    }
}
