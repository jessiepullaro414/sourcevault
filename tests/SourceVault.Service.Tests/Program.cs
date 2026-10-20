using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SourceVault.Service;
using SourceVault.Windows;

var count = 0;
void Check(bool value) { if (!value) throw new InvalidOperationException("Assertion failed"); count++; }
void Reject(Action action)
{
    try { action(); }
    catch (Exception e) when (e is ArgumentException or InvalidDataException or JsonException or CryptographicException) { count++; return; }
    throw new InvalidOperationException("Rejection expected");
}
ServiceConfiguration Parse(string text) => ServiceConfiguration.Parse(Encoding.UTF8.GetBytes(text));
const string valid = "{\"schemaVersion\":1,\"mode\":\"MetadataOnly\",\"keyName\":\"SourceVault.Lab.Unit\"}";
Check(Parse(valid).KeyName == "SourceVault.Lab.Unit");
foreach (var text in new[] { "{}", "[]", "null", "{", valid.Replace("1,", "2,"), valid.Replace("1,", "1.5,"),
    valid.Replace("MetadataOnly", "SourceEnabled"), valid.Replace("SourceVault.Lab.Unit", "SourceVault.Production"),
    valid.Replace("SourceVault.Lab.Unit", "SourceVault.Lab.../escape"), valid.Replace("SourceVault.Lab.Unit", "SourceVault.Lab."),
    valid.Replace("\"mode\":", "\"unknown\":"), valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
    valid.Replace("\"keyName\":\"SourceVault.Lab.Unit\"", "\"keyName\":null"), valid.Replace("\"MetadataOnly\"", "true"),
    valid.Replace("\"schemaVersion\":1,", ""), valid.Replace("Unit", new string('a', 81)), new string(' ', 4097) }) Reject(() => Parse(text));
Check(SourceVault.Service.Program.ParseCommand(["service"]) == "service");
Check(SourceVault.Service.Program.ParseCommand(["metadata-probe"]) == "metadata-probe");
Check(SourceVault.Service.Program.ParseCommand(["vault-root-check"]) == "vault-root-check");
Check(SourceVault.Service.Program.ParseCommand(["vault-root-sddl"]) == "vault-root-sddl");
Reject(() => SourceVault.Service.Program.ParseCommand(["vault-root-check", @"D:\elsewhere"]));
// The vault root comes only from sourcevault.service.json: schema 1 means the default under ProgramData, schema 2
// names an administrator-chosen root. Never from arguments or the environment.
Check(VaultRoot.DefaultPath == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SourceVault", "vault"));
Check(Path.IsPathFullyQualified(VaultRoot.DefaultPath));
Check(Parse(valid).VaultRootPath == VaultRoot.DefaultPath);
string V2(object? root) => "{\"schemaVersion\":2,\"mode\":\"MetadataOnly\",\"keyName\":\"SourceVault.Lab.Unit\",\"vaultRoot\":" + JsonSerializer.Serialize(root) + "}";
Check(Parse(V2(@"K:\SourceVault\vault")).VaultRootPath == @"K:\SourceVault\vault");
Check(Parse(V2(@"K:\SourceVault\vault")).KeyName == "SourceVault.Lab.Unit");
Check(Parse(V2(@"D:\Data\Work\SV\vault")).VaultRootPath == @"D:\Data\Work\SV\vault");
Check(Parse(V2(VaultRoot.DefaultPath)).VaultRootPath == VaultRoot.DefaultPath);
var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
foreach (var root in new object?[] { null, 7, "", "K:", @"K:\", @"K:\vault", @"K:\SourceVault\vault\", "K:/SourceVault/vault", @"K:\SourceVault\..\vault",
    @"K:\SourceVault\.\vault", @"K:\SourceVault\vault.", @"K:\SourceVault\vault ", @"K:\SourceVault\CON", @"K:\SourceVault\nul.txt", @"SourceVault\vault",
    @"\SourceVault\vault", @"K:SourceVault\vault", @"\server\share\SourceVault\vault", @"\?\K:\SourceVault\vault", @"\.\K:\SourceVault\vault",
    @"K:\SourceVault\va*lt", "K:\\SourceVault\\vault\n", @"K:\" + new string('a', 60) + @"\" + new string('b', 60),
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SourceVault", "vault"),
    Path.Combine(ScmHost.InstallRoot, "vault"), Path.Combine(programData, "SourceVault"), Path.Combine(programData, "SourceVault", "config"),
    Path.Combine(programData, "SourceVault", "config", "vault"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "vault") })
    Reject(() => Parse(V2(root)));
// Schema and field set stay strict: v1 never carries vaultRoot, v2 always does, nothing else is accepted.
var rootField = ",\"vaultRoot\":" + JsonSerializer.Serialize(@"K:\SourceVault\vault");
Check(V2(@"K:\SourceVault\vault").Contains(rootField)); // the edits below really change the document
Reject(() => Parse(valid.Replace("}", rootField + "}")));
Reject(() => Parse(V2(@"K:\SourceVault\vault").Replace(rootField, "")));
Reject(() => Parse(V2(@"K:\SourceVault\vault").Replace("\"schemaVersion\":2", "\"schemaVersion\":3")));
Reject(() => Parse(V2(@"K:\SourceVault\vault").Replace("}", rootField.Replace("SourceVault", "Other") + "}")));
Reject(() => Parse(V2(@"K:\SourceVault\vault").Replace("}", ",\"extra\":1}")));
Reject(() => Parse(V2(@"K:\SourceVault\vault").Replace("SourceVault.Lab.Unit", "SourceVault.Production")));
Check(VaultRoot.Overlaps(@"K:\A\vault", @"k:\a") && VaultRoot.Overlaps(@"K:\A", @"K:\A\vault\") && !VaultRoot.Overlaps(@"K:\AB\vault", @"K:\A"));

// The startup gate: a missing, offline or locked drive is code 17; anything else untrusted is code 10.
uint Refusal(Action action) { try { action(); } catch (StartupRefusedException e) { count++; return e.Code; } throw new InvalidOperationException("Refusal expected"); }
Check(Refusal(() => VaultRoot.RequireTrusted(@"K:\SourceVault\vault", new SyntheticVolume(new(DriveType.NoRootDirectory, null, Available: false)))) == StartupRefusedException.VaultDriveUnavailable);
Check(Refusal(() => VaultRoot.RequireTrusted(@"K:\SourceVault\vault", new SyntheticVolume(new(DriveType.Fixed, "NTFS", LocalDisk: true)))) == StartupRefusedException.VaultRootUntrusted);
Check(Refusal(() => VaultRoot.RequireTrusted(@"K:\SourceVault\vault", new SyntheticVolume(new(DriveType.Fixed, "NTFS", LocalDisk: false)))) == StartupRefusedException.VaultRootUntrusted);
Check(Refusal(() => VaultRoot.RequireTrusted(@"K:\SourceVault\vault", new SyntheticVolume(new(DriveType.Removable, "exFAT", LocalDisk: false)))) == StartupRefusedException.VaultRootUntrusted);
// build.json may not put output in, or around, the configured vault's folder.
var buildJson = Encoding.UTF8.GetBytes("{\"version\":1,\"artifactRoot\":" + JsonSerializer.Serialize(@"K:\SourceVault\out") + ",\"targets\":[{\"name\":\"web\",\"engine\":\"npipe://./pipe/docker_engine\",\"container\":\"sv-build\",\"platform\":\"linux\",\"sourcePath\":\"/work/src\",\"command\":[\"npm\",\"run\",\"build\"],\"artifactPath\":\"/work/out\"}]}");
Check(SourceVault.Build.BuildConfig.Parse(buildJson, @"D:\Elsewhere").ArtifactRoot == @"K:\SourceVault\out");
foreach (var folder in new[] { @"K:\SourceVault", @"k:\sourcevault\", @"K:\SourceVault\out", @"K:\SourceVault\out\deeper" })
{
    try { SourceVault.Build.BuildConfig.Parse(buildJson, folder); throw new InvalidOperationException("artifactRoot overlapping the vault folder accepted"); }
    catch (SourceVault.Build.BuildConfigException) { count++; }
}
foreach (var commandArgs in new[] { Array.Empty<string>(), new[] { "console" }, new[] { "service", "--key", "untrusted" }, new[] { "metadata-probe", "--source", "x" } })
    Reject(() => SourceVault.Service.Program.ParseCommand(commandArgs));
const string provider = "Microsoft Platform Crypto Provider";
TpmKeyProtector.ValidatePolicy(provider, "RSA", 2048, CngExportPolicies.None, CngKeyUsages.Decryption, false, false); count++;
Reject(() => TpmKeyProtector.ValidatePolicy("Microsoft Software Key Storage Provider", "RSA", 2048, 0, CngKeyUsages.Decryption, false, false));
Reject(() => TpmKeyProtector.ValidatePolicy(provider, "ECDSA", 2048, 0, CngKeyUsages.Decryption, false, false));
Reject(() => TpmKeyProtector.ValidatePolicy(provider, "RSA", 1024, 0, CngKeyUsages.Decryption, false, false));
Reject(() => TpmKeyProtector.ValidatePolicy(provider, "RSA", 2048, CngExportPolicies.AllowExport, CngKeyUsages.Decryption, false, false));
Reject(() => TpmKeyProtector.ValidatePolicy(provider, "RSA", 2048, 0, CngKeyUsages.AllUsages, false, false));
Reject(() => TpmKeyProtector.ValidatePolicy(provider, "RSA", 2048, 0, CngKeyUsages.Decryption, true, false));
Reject(() => TpmKeyProtector.ValidatePolicy(provider, "RSA", 2048, 0, CngKeyUsages.Decryption, false, true));

// SCM start parameters: argv[0] is the service name; only the fixed provision-key parameter is accepted.
Check(ScmHost.ParseStartMode(["SourceVault"]) == ScmHost.StartMode.Run);
Check(ScmHost.ParseStartMode(["SourceVault", "provision-key"]) == ScmHost.StartMode.ProvisionKey);
Check(ScmHost.ParseStartMode(["SourceVault", "create-synthetic-project"]) == ScmHost.StartMode.CreateSyntheticProject);
Check(ScmHost.ParseStartMode(["SourceVault", "create-synthetic-project", "Real name"]) == ScmHost.StartMode.Invalid);
foreach (var startArgs in new string?[][] { [], ["SourceVault", "Provision-Key"], ["SourceVault", "provision-key", "SourceVault.Lab.Other"],
    ["SourceVault", "provision-key "], ["SourceVault", null], ["SourceVault", "--software-key"] })
    Check(ScmHost.ParseStartMode(startArgs) == ScmHost.StartMode.Invalid);
// Provisioning refuses any identity other than NT SERVICE\SourceVault before touching the provider or TPM.
Check(TpmKeyProvisioner.CreateDecryptOnly("SourceVault.Lab.Unit") == ProvisionOutcome.Unavailable);
Reject(() => TpmKeyProvisioner.CreateDecryptOnly("SourceVault.Production"));

// Synchronization is deterministic; waits have bounded deadlines so hung tests fail.
var statuses = new List<Status>();
using var running = new ManualResetEventSlim();
var resource = new FakeResource();
using (var lifecycle = new ServiceLifecycle(s => { lock (statuses) statuses.Add(s); if (s.State == ServiceState.Running) running.Set(); }))
{
    lifecycle.RequestStop(); // Before startup is ignored.
    var task = Task.Run(() => lifecycle.Run(() => resource));
    Check(running.Wait(TimeSpan.FromSeconds(5)));
    lifecycle.RequestStop(); lifecycle.RequestStop();
    Check(task.Wait(TimeSpan.FromSeconds(5)));
    Check(resource.Disposed);
    Check(statuses.Select(s => s.State).SequenceEqual(new[] { ServiceState.StartPending, ServiceState.Running, ServiceState.StopPending, ServiceState.Stopped }));
    Check(statuses[0].AcceptedControls == 0 && statuses[1].AcceptedControls == 0x85 && statuses[2].AcceptedControls == 0);
    Check(statuses[0].Checkpoint == 1 && statuses[1].Checkpoint == 0 && statuses[3].WaitHint == 0);
    Check(statuses[3].ExitCode == 0);
    lifecycle.RequestStop(); Check(statuses.Count == 4);
}
statuses.Clear();
using (var lifecycle = new ServiceLifecycle(s => statuses.Add(s)))
{
    lifecycle.Run(() => throw new InvalidOperationException("sensitive failure text must never be reported"));
    Check(statuses.Select(s => s.State).SequenceEqual(new[] { ServiceState.StartPending, ServiceState.Stopped }));
    Check(statuses[^1].ExitCode == 1064);
}
statuses.Clear();
using (var lifecycle = new ServiceLifecycle(s => statuses.Add(s)))
{
    lifecycle.Run(() => { lifecycle.RequestStop(); return new FakeResource(); });
    Check(statuses.Select(s => s.State).SequenceEqual(new[] { ServiceState.StartPending, ServiceState.StopPending, ServiceState.Stopped }));
}
statuses.Clear();
using (var lifecycle = new ServiceLifecycle(s => statuses.Add(s)))
{
    lifecycle.Run(() => { lifecycle.RequestStop(); return new FakeResource { ThrowOnDispose = true }; });
    Check(statuses[^1].State == ServiceState.Stopped && statuses[^1].ExitCode == 1064);
}
// A refused startup check (untrusted vault root) stops with ERROR_SERVICE_SPECIFIC_ERROR and its bounded code,
// never reaching RUNNING; other failures keep the generic 1064.
statuses.Clear();
using (var lifecycle = new ServiceLifecycle(s => statuses.Add(s)))
{
    lifecycle.Run(() => throw new StartupRefusedException(StartupRefusedException.VaultRootUntrusted));
    Check(statuses.Select(s => s.State).SequenceEqual(new[] { ServiceState.StartPending, ServiceState.Stopped }));
    Check(statuses[^1].ExitCode == 1066 && statuses[^1].ServiceSpecificExitCode == 10);
}
statuses.Clear();
using (var lifecycle = new ServiceLifecycle(s => statuses.Add(s)))
{
    lifecycle.Run(() => throw new StartupRefusedException(StartupRefusedException.VaultDriveUnavailable));
    Check(statuses[^1].State == ServiceState.Stopped && statuses[^1].ExitCode == 1066 && statuses[^1].ServiceSpecificExitCode == 17);
}
// ---- internal security review 2026-10-20 -----------------------------------------------------------------------------
// The build output folder is judged like the vault root, with the build service as the trusted writer.
{
    var build = ServiceSid.FromServiceName("SourceVaultBuild");
    var users = new System.Security.Principal.SecurityIdentifier("S-1-5-11"); // Authenticated Users
    var operators = new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001");
    DirectoryObservation Dir(System.Security.Principal.SecurityIdentifier owner, bool isProtected, params AceInfo[] aces) =>
        new(DirectoryState.Directory, new DirectorySecurityInfo(owner, true, isProtected, aces));
    var full = System.Security.AccessControl.FileSystemRights.FullControl;
    var read = System.Security.AccessControl.FileSystemRights.ReadAndExecute;
    var root = Dir(TrustedPrincipals.Administrators, true, new AceInfo(TrustedPrincipals.LocalSystem, true, full), new AceInfo(TrustedPrincipals.Administrators, true, full),
        new AceInfo(build, true, System.Security.AccessControl.FileSystemRights.Modify), new AceInfo(operators, true, read));
    var lockedParent = Dir(TrustedPrincipals.Administrators, true, new AceInfo(TrustedPrincipals.Administrators, true, full), new AceInfo(operators, true, read));
    var userParent = Dir(TrustedPrincipals.Administrators, false, new AceInfo(TrustedPrincipals.Administrators, true, full), new AceInfo(users, true, System.Security.AccessControl.FileSystemRights.Modify));
    var driveRoot = Dir(TrustedPrincipals.TrustedInstaller, false, new AceInfo(TrustedPrincipals.Administrators, true, full), new AceInfo(users, true, System.Security.AccessControl.FileSystemRights.Modify, inheritOnly: true));
    var good = ArtifactRoot.Validate(@"D:\SourceVault\artifacts", new FakeInspector(new() { [@"D:\SourceVault\artifacts"] = root, [@"D:\SourceVault"] = lockedParent, [@"D:\"] = driveRoot }));
    Check(good.Passed); // operators' read access is only a warning
    var bad = ArtifactRoot.Validate(@"D:\SourceVault\artifacts", new FakeInspector(new() { [@"D:\SourceVault\artifacts"] = root, [@"D:\SourceVault"] = userParent, [@"D:\"] = driveRoot }));
    Check(!bad.Passed && bad.HasCode(FindingCodes.UntrustedWriteAccess, "ancestor:1")); // users could rename the parent and plant a link
    var linked = ArtifactRoot.Validate(@"D:\SourceVault\artifacts", new FakeInspector(new() { [@"D:\SourceVault\artifacts"] = new(DirectoryState.ReparsePoint, null), [@"D:\SourceVault"] = lockedParent, [@"D:\"] = driveRoot }));
    Check(!linked.Passed && linked.HasCode(FindingCodes.ReparsePoint, "root"));
}
// No program runs .NET startup hooks (DOTNET_STARTUP_HOOKS): Directory.Build.props sets StartupHookSupport=false.
Check(AppContext.TryGetSwitch("System.StartupHookProvider.IsSupported", out var hooks) && !hooks);

Console.WriteLine($"serviceTests=passed; assertions={count}; tpmTouched=false; scmTouched=false; endpoints=pipe-v2 (see Endpoints.Tests)");

sealed class FakeInspector(Dictionary<string, DirectoryObservation> directories) : IStorageRootInspector
{
    public VolumeObservation ObserveVolume(string path) => new(DriveType.Fixed, "NTFS", LocalDisk: true);
    public DirectoryObservation ObserveDirectory(string path) => directories.TryGetValue(path, out var d) ? d : new(DirectoryState.Missing, null);
}
/// <summary>Synthetic volume; every directory reads as missing. No filesystem access.</summary>
sealed class SyntheticVolume(VolumeObservation volume) : IStorageRootInspector
{
    public VolumeObservation ObserveVolume(string path) => volume;
    public DirectoryObservation ObserveDirectory(string path) => new(DirectoryState.Missing, null);
}
sealed class FakeResource : IDisposable
{
    internal bool Disposed { get; private set; }
    internal bool ThrowOnDispose { get; init; }
    public void Dispose() { Disposed = true; if (ThrowOnDispose) throw new InvalidOperationException(); }
}

