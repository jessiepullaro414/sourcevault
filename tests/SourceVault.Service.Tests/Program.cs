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
    Check(statuses[0].AcceptedControls == 0 && statuses[1].AcceptedControls == 5 && statuses[2].AcceptedControls == 0);
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
Console.WriteLine($"serviceTests=passed; assertions={count}; tpmTouched=false; scmTouched=false; sourceEndpoints=disabled");
sealed class FakeResource : IDisposable
{
    internal bool Disposed { get; private set; }
    internal bool ThrowOnDispose { get; init; }
    public void Dispose() { Disposed = true; if (ThrowOnDispose) throw new InvalidOperationException(); }
}

