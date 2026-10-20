using System.Runtime.InteropServices;
using System.Security.Principal;
using SourceVault.Build;
using SourceVault.Storage;
using SourceVault.Windows;

namespace SourceVault.Service;

internal sealed class ScmHost
{
    internal const string ServiceName = "SourceVault";
    // The build service runs this same program as a separate service, under its own account. It never opens the vault
    // or the TPM key, and its account has no access to either; it alone holds Docker access.
    internal const string BuildServiceName = "SourceVaultBuild";
    private readonly bool buildRole;
    private string Name => buildRole ? BuildServiceName : ServiceName;
    private readonly Native.ServiceMain main;
    private readonly Native.Handler handler;
    private IntPtr statusHandle;
    private volatile ServiceLifecycle? lifecycle;
    private int failure;
    internal ScmHost(bool buildRole = false) { this.buildRole = buildRole; main = ServiceMain; handler = HandleControl; }
    internal int Run()
    {
        if (!OperatingSystem.IsWindows()) return 3;
        var table = new[] { new Native.TableEntry { Name = Name, Main = main }, new Native.TableEntry() };
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
            statusHandle = Native.RegisterServiceCtrlHandlerExW(Name, handler, IntPtr.Zero);
            if (statusHandle == IntPtr.Zero) { failure = 3; return; }
            if (buildRole)
            {
                if (ParseStartMode(ReadArguments(count, arguments)) != StartMode.Run) { Report(new(ServiceState.Stopped, 0, 87, 0, 0)); return; }
                using var build = new ServiceLifecycle(Report);
                lifecycle = build;
                build.Run(StartBuildEndpoints);
                return;
            }
            switch (ParseStartMode(ReadArguments(count, arguments)))
            {
                case StartMode.ProvisionKey: Provision(importKey: false); return;
                case StartMode.ProvisionImportKey: Provision(importKey: true); return;
                case StartMode.CreateSyntheticProject: CreateSyntheticProject(); return;
                case StartMode.Invalid: Report(new(ServiceState.Stopped, 0, 87, 0, 0)); return; // ERROR_INVALID_PARAMETER
            }
            using var instance = new ServiceLifecycle(Report);
            lifecycle = instance;
            // RUNNING only after identity, config, vault root, key and every project have been verified and the
            // pipe is listening. No build endpoint exists.
            instance.Run(StartEndpoints);
        }
        catch { failure = 3; }
        finally { lifecycle = null; }
    }
    internal enum StartMode { Run, ProvisionKey, ProvisionImportKey, CreateSyntheticProject, Invalid }

    // SCM passes the service name as argv[0], then any `sc start SourceVault <args>` start parameters.
    // Only the fixed lab parameters are accepted; everything else is refused.
    internal static StartMode ParseStartMode(IReadOnlyList<string?> args) => args.Count switch
    {
        1 => StartMode.Run,
        2 when args[1] == "provision-key" => StartMode.ProvisionKey,
        2 when args[1] == "provision-import-key" => StartMode.ProvisionImportKey,
        2 when args[1] == "create-synthetic-project" => StartMode.CreateSyntheticProject,
        _ => StartMode.Invalid,
    };

    internal const string OperatorsGroupName = "SourceVault Operators";
    private volatile VaultPipeServer? server;

    private IDisposable StartEndpoints()
    {
        var (key, catalog, check) = OpenVerifiedVault();
        var importKey = OpenImportKey();
        try
        {
            SecurityIdentifier operators;
            try { operators = (SecurityIdentifier)new NTAccount(OperatorsGroupName).Translate(typeof(SecurityIdentifier)); }
            catch (IdentityNotMappedException) { throw new StartupRefusedException(StartupRefusedException.OperatorsGroupMissing); }
            using var self = WindowsIdentity.GetCurrent();
            var pipeServer = new VaultPipeServer(VaultPipe.ProductionName, self.User!,
                new PipePeerPolicy(operators, InstalledClients(), new ClientPins(InstallRoot, ["client", "editor"])), new VaultProtocol(catalog, check.Projects, builds: new BuildCoordinator(catalog, BuildServiceClient()), offline: check.Offline, status: VaultStatus.Write, passwords: new OperatorPasswords(catalog, OperatorPasswords.DefaultResetsFolder),
                    packages: importKey is null ? null : new PackageImports(importKey, importKey.ExportSubjectPublicKeyInfo(), PackageImports.ComputerName, PackageImports.DefaultSendersFolder)));
            try { pipeServer.Start(); }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                pipeServer.Dispose();
                throw new StartupRefusedException(StartupRefusedException.PipeUnavailable); // e.g. name already taken
            }
            server = pipeServer;
            return new Owned(() => { server = null; pipeServer.Dispose(); importKey?.Dispose(); key.Dispose(); });
        }
        catch { importKey?.Dispose(); key.Dispose(); throw; }
    }

    // Build role: running as NT SERVICE\SourceVaultBuild, serving the private build pipe to NT SERVICE\SourceVault only.
    private IDisposable StartBuildEndpoints()
    {
        using var self = WindowsIdentity.GetCurrent();
        SecurityIdentifier expectedSelf, vault;
        try
        {
            expectedSelf = (SecurityIdentifier)new NTAccount("NT SERVICE", BuildServiceName).Translate(typeof(SecurityIdentifier));
            vault = (SecurityIdentifier)new NTAccount("NT SERVICE", ServiceName).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException) { throw new StartupRefusedException(StartupRefusedException.VaultServiceMissing); }
        if (self.User is null || !expectedSelf.Equals(self.User)) throw new StartupRefusedException(StartupRefusedException.WrongIdentity);
        var pipeServer = new BuildPipeServer(BuildChannel.PipeName, self.User!, vault, () => BuildConfig.Load(BuildConfig.DefaultPath, VaultRoot.ConfiguredVaultFolder()),
            target => new DockerEngineClient(target.Engine, target.CaFile, target.ClientCertificateThumbprint));
        try { pipeServer.Start(); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            pipeServer.Dispose();
            throw new StartupRefusedException(StartupRefusedException.PipeUnavailable);
        }
        return pipeServer;
    }

    // Vault role: builds go to the build service, which must own its pipe as NT SERVICE\SourceVaultBuild. If that
    // service is not installed or not running, build requests report Unavailable; nothing else is affected.
    internal static IBuildService BuildServiceClient() => new BuildChannel.Client(_ =>
    {
        var build = (SecurityIdentifier)new NTAccount("NT SERVICE", BuildServiceName).Translate(typeof(SecurityIdentifier));
        return Task.FromResult<Stream>(VaultPipe.Connect(BuildChannel.PipeName, build, TimeSpan.FromSeconds(5)));
    });

    internal static string InstallRoot => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")));

    // Exact executables allowed to connect: apphost .exe files under the admin-owned install root. Never the
    // shared runtime's dotnet.exe, which would let any DLL connect. Their folders are also hash-pinned (ClientPins).
    internal static IEnumerable<string> InstalledClients()
    {
        yield return Path.Combine(InstallRoot, "client", "SourceVault.LabClient.exe");
        yield return Path.Combine(InstallRoot, "client", "SourceVault.Import.exe");
        yield return Path.Combine(InstallRoot, "editor", "SourceVault.Editor.exe");
    }

    private sealed class Owned(Action dispose) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) dispose(); }
    }

    // Identity, config, trusted vault root, existing key, then every project authenticated; a project that fails is
    // offline, not fatal (owner decision 2026-10-20). The caller owns the key.
    private static (TpmKeyProtector Key, ProjectCatalog Catalog, VaultCheck Check) OpenVerifiedVault()
    {
        TpmKeyProtector.RequireServiceIdentity();
        var config = ServiceConfiguration.LoadFromApplicationDirectory();
        VaultRoot.RequireTrusted(config.VaultRootPath);
        var key = TpmKeyProtector.OpenExisting(config.KeyName);
        try
        {
            var catalog = new ProjectCatalog(config.VaultRootPath, key);
            VaultCheck check;
            try { check = catalog.Verify(); }
            catch (VaultCatalogException e)
            {
                throw new StartupRefusedException(e.Failure == CatalogFailure.Project
                    ? StartupRefusedException.ProjectUntrusted : StartupRefusedException.CatalogUntrusted);
            }
            VaultStatus.Write(check.Projects.Count, check.Offline.Select(o => o.ProjectId));
            // Finish any deletion a crash or a file in use interrupted. Best effort: a pending tombstone stays allowed.
            try { catalog.PurgeDeleted(); } catch (Exception e) when (e is not OutOfMemoryException) { }
            // Uploads never survive a restart (decision C6): their staging folders hold ciphertext nobody will commit.
            try { catalog.ClearStaging(); } catch (Exception e) when (e is not OutOfMemoryException) { }
            return (key, catalog, check);
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
            var (key, catalog, check) = OpenVerifiedVault();
            var projects = check.Projects; // a lab run on a vault with an offline project fails its final strict check
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

    // The package import key, if provisioned (provision-import-key). Without it package import is Unavailable and
    // nothing else changes, so a missing or unusable import key never stops the vault.
    private static TpmKeyProtector? OpenImportKey()
    {
        try { return TpmKeyProtector.OpenExisting(ServiceConfiguration.LoadFromApplicationDirectory().ImportKeyName); }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or UnauthorizedAccessException or ArgumentException or InvalidDataException or IOException) { return null; }
    }

    // One-shot key creation under the service identity: the vault key (provision-key) or the separate package import
    // key (provision-import-key). Reports START_PENDING, creates and checks the key, then STOPPED. The outcome is only
    // the service-specific exit code (AlreadyExists = 1 when the key is already there); nothing is logged.
    private void Provision(bool importKey)
    {
        Report(new(ServiceState.StartPending, 0, 0, 1, 60000));
        var outcome = ProvisionOutcome.Unavailable;
        try
        {
            var config = ServiceConfiguration.LoadFromApplicationDirectory();
            outcome = TpmKeyProvisioner.CreateDecryptOnly(importKey ? config.ImportKeyName : config.KeyName);
        }
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
            if (control == 14) // SERVICE_CONTROL_SESSIONCHANGE
            {
                // WTS_CONSOLE_DISCONNECT, WTS_REMOTE_DISCONNECT, WTS_SESSION_LOGOFF, WTS_SESSION_LOCK end vault sessions.
                // eventData is WTSSESSION_NOTIFICATION { DWORD cbSize; DWORD dwSessionId; }.
                if (eventType is 2 or 4 or 6 or 7 && eventData != IntPtr.Zero)
                    server?.EndWindowsSession(unchecked((uint)Marshal.ReadInt32(eventData, 4)));
                return 0;
            }
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
