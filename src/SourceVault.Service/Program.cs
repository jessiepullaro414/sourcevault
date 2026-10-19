using SourceVault.Windows;
namespace SourceVault.Service;
internal static class Program
{
    internal static string ParseCommand(string[] args) => args.Length == 1 && args[0] is "metadata-probe" or "service" or "build-service" or "build-config-check" or "vault-root-check" or "vault-root-sddl"
        ? args[0] : throw new ArgumentException();
    public static int Main(string[] args)
    {
        try
        {
            switch (ParseCommand(args))
            {
                case "service": return new ScmHost().Run();
                case "build-service": return new ScmHost(buildRole: true).Run();
                case "build-config-check":
                    // Read-only, for setup and -Status: validates build.json exactly as the build service does.
                    try
                    {
                        var config = SourceVault.Build.BuildConfig.Load(SourceVault.Build.BuildConfig.DefaultPath, VaultRoot.ConfiguredVaultFolder());
                        Console.WriteLine($"artifactRoot={config.ArtifactRoot}");
                        Console.WriteLine($"targets={string.Join(",", config.Targets.Select(t => t.Name))}");
                        return 0;
                    }
                    catch (SourceVault.Build.BuildConfigException e) { Console.WriteLine("invalid: " + e.Message); return 4; }
                case "vault-root-sddl":
                    // The exact root DACL the validator requires, for the administrator provisioning script.
                    Console.WriteLine(TrustedStorageRoot.ExpectedRootSddl(VaultRoot.Principals));
                    return 0;
                case "vault-root-check":
                    // Read-only. Findings carry codes, levels and SIDs only. Run elevated: the root grants nobody else read.
                    // The root comes from sourcevault.service.json (schema 1: the default), exactly as the service reads it.
                    string root;
                    try { root = ServiceConfiguration.LoadFromApplicationDirectory().VaultRootPath; }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
                    { Console.WriteLine("invalid: sourcevault.service.json is missing or not valid"); return 4; }
                    var result = VaultRoot.Validate(root);
                    Console.WriteLine($"vaultRoot={root}; trusted={(result.Passed ? "yes" : "no")}");
                    foreach (var f in result.Findings) Console.WriteLine($"{f.Severity.ToString().ToLowerInvariant()} {f.Code} at {f.Location}{(f.Subject is null ? "" : " (" + f.Subject + ")")}");
                    return result.Passed ? 0 : 4;
            }
            TpmKeyProtector.ProbeHardwareProvider();
            Console.WriteLine("hardwareProvider=available; persistedKeysOpened=false; sourceEndpoints=disabled; serviceDeployment=unverified");
            return 0;
        }
        catch (ArgumentException) { Console.Error.WriteLine("usage: SourceVault.Service metadata-probe | service | build-service | build-config-check | vault-root-check | vault-root-sddl"); return 2; }
        catch { Console.Error.WriteLine("unavailable; no-fallback=true; sourceEndpoints=disabled"); return 3; }
    }
}
