using SourceVault.Windows;
namespace SourceVault.Service;
internal static class Program
{
    internal static string ParseCommand(string[] args) => args.Length == 1 && args[0] is "metadata-probe" or "service"
        ? args[0] : throw new ArgumentException();
    public static int Main(string[] args)
    {
        try
        {
            if (ParseCommand(args) == "service") return new ScmHost().Run();
            TpmKeyProtector.ProbeHardwareProvider();
            Console.WriteLine("hardwareProvider=available; persistedKeysOpened=false; sourceEndpoints=disabled; serviceDeployment=unverified");
            return 0;
        }
        catch (ArgumentException) { Console.Error.WriteLine("usage: SourceVault.Service metadata-probe | service"); return 2; }
        catch { Console.Error.WriteLine("unavailable; no-fallback=true; sourceEndpoints=disabled"); return 3; }
    }
}
