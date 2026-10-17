using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;

namespace SourceVault.TpmSpike;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var command = Arguments.Parse(args);
            if (command.Verb == "self-test") return SelfTests.Run();
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            // Opening the provider does not create a key or change TPM state.
            TpmKeyProtector.RequireHardwareProvider();
            Console.WriteLine("provider=Microsoft Platform Crypto Provider; providerReportsHardware=true; deviceKeyOperation=not-tested");
            using var identity = WindowsIdentity.GetCurrent();
            Console.WriteLine($"identitySid={identity.User?.Value ?? "unavailable"}; scope=current-identity");
            if (command.Verb == "probe" && command.KeyName is null) return 0;
            var name = command.KeyName!;
            using var protector = command.Verb == "lab-create"
                ? TpmKeyProtector.Create(name)
                : TpmKeyProtector.Open(name);
            Console.WriteLine("key=opened; algorithm=RSA; bits=2048; exportPolicy=None; usage=Decryption");
            if (command.Verb is "lab-create" or "lab-roundtrip")
            {
                var dataKey = RandomNumberGenerator.GetBytes(TpmKeyProtector.DataKeyBytes);
                byte[]? recovered = null;
                try
                {
                    var wrapped = protector.Wrap(dataKey);
                    recovered = protector.Unwrap(wrapped);
                    if (!CryptographicOperations.FixedTimeEquals(dataKey, recovered))
                        throw new CryptographicException();
                    Console.WriteLine("syntheticRoundtrip=passed; plaintextOrKeyMaterialLogged=false");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(dataKey);
                    if (recovered is not null) CryptographicOperations.ZeroMemory(recovered);
                }
            }
            return 0;
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("Invalid arguments. Usage: [probe [--key SourceVault.Lab.NAME]] | self-test | lab-open --key NAME | lab-roundtrip --key NAME | lab-create --key NAME --ack-device-only-loss");
            return 2;
        }
        catch (Exception error) when (error is CryptographicException or PlatformNotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Do not log exception text: key/provider errors may reveal unreviewed metadata.
            Console.Error.WriteLine($"unavailable; category={error.GetType().Name}; no-fallback=true");
            return 3;
        }
    }
}

internal sealed record Arguments(string Verb, string? KeyName)
{
    public static Arguments Parse(string[] args)
    {
        var verb = args.Length == 0 ? "probe" : args[0];
        if (verb is not ("probe" or "self-test" or "lab-open" or "lab-roundtrip" or "lab-create"))
            throw new ArgumentException();
        string? name = null;
        var acknowledged = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--key" && name is null && i + 1 < args.Length) name = args[++i];
            else if (args[i] == "--ack-device-only-loss" && !acknowledged) acknowledged = true;
            else throw new ArgumentException();
        }
        if (name is not null) ValidateName(name);
        if (verb == "self-test" && (name is not null || acknowledged)) throw new ArgumentException();
        if (verb.StartsWith("lab-", StringComparison.Ordinal) && name is null) throw new ArgumentException();
        if ((verb == "lab-create") != acknowledged) throw new ArgumentException();
        return new Arguments(verb, name);
    }

    public static void ValidateName(string name)
    {
        const string prefix = "SourceVault.Lab.";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length <= prefix.Length || name.Length > 80
            || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new ArgumentException();
    }
}

// This spike only wraps random 256-bit data keys. It is not a ciphertext record format.
internal sealed class TpmKeyProtector : IDisposable
{
    internal const int DataKeyBytes = 32;
    private const int RsaBits = 2048;
    private const int CiphertextBytes = RsaBits / 8;
    private const string ProviderName = "Microsoft Platform Crypto Provider";
    private const string PcpKeyUsagePolicyProperty = "PCP_KEY_USAGE_POLICY"; // NCRYPT_PCP_KEY_USAGE_POLICY_PROPERTY
    private const uint PcpEncryptionKey = 0x2; // NCRYPT_PCP_ENCRYPTION_KEY
    private static readonly CngProvider Provider = new(ProviderName);
    private readonly CngKey key;
    private readonly RSACng rsa;

    private TpmKeyProtector(CngKey key)
    {
        this.key = key;
        try
        {
            RequireHardwareProvider();
            // Check the opened object, not merely the requested creation settings.
            ValidatePolicy(key.Provider?.Provider ?? "", key.Algorithm.Algorithm ?? "", key.KeySize,
                key.ExportPolicy, key.KeyUsage, key.IsEphemeral, key.IsMachineKey);
            rsa = new RSACng(key);
        }
        catch { key.Dispose(); throw; }
    }

    internal static TpmKeyProtector Create(string name)
    {
        Arguments.ValidateName(name);
        RequireHardwareProvider();
        // No OverwriteExistingKey flag. Existing keys must never be silently replaced.
        var parameters = new CngKeyCreationParameters
        {
            Provider = Provider,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Decryption,
            KeyCreationOptions = CngKeyCreationOptions.None,
        };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(RsaBits), CngPropertyOptions.None));
        // PCP ignores KeyUsage when choosing TPM object attributes and creates a generic sign+encrypt key
        // (observed PCP_KEY_USAGE_POLICY 0x10003) unless this provider-specific policy is set.
        parameters.Parameters.Add(new CngProperty(PcpKeyUsagePolicyProperty, BitConverter.GetBytes(PcpEncryptionKey), CngPropertyOptions.None));
        return new TpmKeyProtector(CngKey.Create(CngAlgorithm.Rsa, name, parameters));
    }

    internal static TpmKeyProtector Open(string name)
    {
        Arguments.ValidateName(name);
        RequireHardwareProvider();
        return new TpmKeyProtector(CngKey.Open(name, Provider, CngKeyOpenOptions.Silent));
    }

    internal byte[] Wrap(ReadOnlySpan<byte> dataKey)
    {
        if (dataKey.Length != DataKeyBytes) throw new ArgumentException();
        return rsa.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256);
    }

    internal static void ValidatePolicy(string provider, string algorithm, int bits, CngExportPolicies export,
        CngKeyUsages usage, bool ephemeral, bool machine)
    {
        if (provider != ProviderName || algorithm != "RSA" || bits != RsaBits
            || export != CngExportPolicies.None || usage != CngKeyUsages.Decryption || ephemeral || machine)
            throw new CryptographicException();
    }

    internal byte[] Unwrap(ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length != CiphertextBytes) throw new ArgumentException();
        var plaintext = rsa.Decrypt(ciphertext, RSAEncryptionPadding.OaepSHA256);
        if (plaintext.Length == DataKeyBytes) return plaintext;
        CryptographicOperations.ZeroMemory(plaintext);
        throw new CryptographicException();
    }

    internal static void RequireHardwareProvider()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var status = Native.NCryptOpenStorageProvider(out var provider, ProviderName, 0);
        if (status != 0) throw new CryptographicException(status);
        try
        {
            var value = new byte[4];
            status = Native.NCryptGetProperty(provider, "Impl Type", value, value.Length, out var size, 0);
            if (status != 0) throw new CryptographicException(status);
            const uint HardwareFlag = 0x1;
            const uint SoftwareFlag = 0x2;
            var implementation = BitConverter.ToUInt32(value);
            if (size != 4 || (implementation & HardwareFlag) == 0 || (implementation & SoftwareFlag) != 0)
                throw new CryptographicException();
        }
        finally { Native.NCryptFreeObject(provider); }
    }

    public void Dispose() { rsa.Dispose(); key.Dispose(); }

    private static class Native
    {
        [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
        internal static extern int NCryptOpenStorageProvider(out IntPtr provider, string providerName, int flags);
        [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
        internal static extern int NCryptGetProperty(IntPtr provider, string property, [Out] byte[] output, int outputBytes, out int resultBytes, int flags);
        [DllImport("ncrypt.dll")]
        internal static extern int NCryptFreeObject(IntPtr handle);
    }
}

internal static class SelfTests
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("Self-test failed"); count++; }
        void Reject(string[] input)
        {
            try { Arguments.Parse(input); }
            catch (ArgumentException) { count++; return; }
            throw new InvalidOperationException("Self-test failed");
        }
        Check(Arguments.Parse([]).Verb == "probe");
        Check(Arguments.Parse(["probe", "--key", "SourceVault.Lab.Unit"]).KeyName is not null);
        Check(Arguments.Parse(["lab-create", "--key", "SourceVault.Lab.Unit", "--ack-device-only-loss"]).Verb == "lab-create");
        Reject(["lab-create", "--key", "SourceVault.Lab.Unit"]);
        Reject(["probe", "--ack-device-only-loss"]);
        Reject(["lab-open"]);
        Reject(["self-test", "--key", "SourceVault.Lab.Unit"]);
        Reject(["probe", "--key", "SourceVault.Production"]);
        Reject(["probe", "--key", "SourceVault.Lab."]);
        Reject(["probe", "--key", "SourceVault.Lab.../escape"]);
        Reject(["probe", "--key", "SourceVault.Lab.Unit", "--key", "SourceVault.Lab.Unit"]);
        Reject(["probe", "--unknown"]);
        Reject(["delete", "--key", "SourceVault.Lab.Unit"]);
        Reject(["probe", "--key", "SourceVault.Lab." + new string('a', 81)]);
        const string provider = "Microsoft Platform Crypto Provider";
        void PolicyReject(string p = provider, string a = "RSA", int bits = 2048,
            CngExportPolicies export = CngExportPolicies.None, CngKeyUsages usage = CngKeyUsages.Decryption,
            bool ephemeral = false, bool machine = false)
        {
            try { TpmKeyProtector.ValidatePolicy(p, a, bits, export, usage, ephemeral, machine); }
            catch (CryptographicException) { count++; return; }
            throw new InvalidOperationException("Self-test failed");
        }
        TpmKeyProtector.ValidatePolicy(provider, "RSA", 2048, CngExportPolicies.None, CngKeyUsages.Decryption, false, false);
        count++;
        PolicyReject(p: "Microsoft Software Key Storage Provider");
        PolicyReject(a: "ECDSA");
        PolicyReject(bits: 1024);
        PolicyReject(export: CngExportPolicies.AllowExport);
        PolicyReject(export: CngExportPolicies.AllowPlaintextArchiving);
        PolicyReject(usage: CngKeyUsages.AllUsages);
        PolicyReject(ephemeral: true);
        PolicyReject(machine: true);
        Console.WriteLine($"selfTests=passed; assertions={count}; tpmTouched=false; scope=argument-and-policy-safety");
        return 0;
    }
}
