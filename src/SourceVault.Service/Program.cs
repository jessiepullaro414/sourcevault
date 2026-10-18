using SourceVault.Windows;
namespace SourceVault.Service;
internal static class Program
{
    internal static string ParseCommand(string[] args) => args.Length == 1 && args[0] is "metadata-probe" or "service" or "build-service" or "vault-root-check" or "vault-root-sddl"
        ? args[0] : throw new ArgumentException();
    public static int Main(string[] args)
    {
        try
        {
            switch (ParseCommand(args))
            {
                case "service": return new ScmHost().Run();
                case "build-service": return new ScmHost(buildRole: true).Run();
                case "vault-root-sddl":
                    // The exact root DACL the validator requires, for the administrator provisioning script.
                    Console.WriteLine(TrustedStorageRoot.ExpectedRootSddl(VaultRoot.Principals));
                    return 0;
                case "vault-root-check":
                    // Read-only. Findings carry codes, levels and SIDs only. Run elevated: the root grants nobody else read.
                    var result = VaultRoot.Validate();
                    Console.WriteLine($"vaultRoot={VaultRoot.Path}; trusted={(result.Passed ? "yes" : "no")}");
                    foreach (var f in result.Findings) Console.WriteLine($"{f.Severity.ToString().ToLowerInvariant()} {f.Code} at {f.Location}{(f.Subject is null ? "" : " (" + f.Subject + ")")}");
                    return result.Passed ? 0 : 4;
            }
            TpmKeyProtector.ProbeHardwareProvider();
            Console.WriteLine("hardwareProvider=available; persistedKeysOpened=false; sourceEndpoints=disabled; serviceDeployment=unverified");
            return 0;
        }
        catch (ArgumentException) { Console.Error.WriteLine("usage: SourceVault.Service metadata-probe | service | build-service | vault-root-check | vault-root-sddl"); return 2; }
        catch { Console.Error.WriteLine("unavailable; no-fallback=true; sourceEndpoints=disabled"); return 3; }
    }
}
