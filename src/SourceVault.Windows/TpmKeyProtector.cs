using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using SourceVault.Storage;

namespace SourceVault.Windows;

// Existing-key adapter only. Provisioning is deliberately a separate, reviewed operation.
public sealed class TpmKeyProtector : IKeyProtector, IDisposable
{
    internal const string ProviderName = "Microsoft Platform Crypto Provider";
    private readonly CngKey key;
    private readonly RSACng rsa;
    private readonly object sync = new();
    private bool disposed;
    private TpmKeyProtector(CngKey opened)
    {
        key = opened;
        try
        {
            ValidatePolicy(key.Provider?.Provider ?? "", key.Algorithm.Algorithm ?? "", key.KeySize,
                key.ExportPolicy, key.KeyUsage, key.IsEphemeral, key.IsMachineKey);
            rsa = new RSACng(key);
        }
        catch { key.Dispose(); throw; }
    }
    public static TpmKeyProtector OpenExisting(string keyName)
    {
        ValidateKeyName(keyName);
        RequireServiceIdentity(); // Must precede hardware/provider/key operations.
        ProbeHardwareProvider();
        return new(CngKey.Open(keyName, new CngProvider(ProviderName), CngKeyOpenOptions.Silent));
    }
    public static void RequireServiceIdentity()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        var expected = (SecurityIdentifier)new NTAccount(@"NT SERVICE\SourceVault").Translate(typeof(SecurityIdentifier));
        if (identity.User is null || !identity.User.Equals(expected) || identity.ImpersonationLevel != TokenImpersonationLevel.None)
            throw new UnauthorizedAccessException();
    }
    internal static void ValidateKeyName(string name)
    {
        const string prefix = "SourceVault.Lab.";
        if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal) || name.Length <= prefix.Length || name.Length > 80 ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new ArgumentException("Invalid key identifier.");
    }
    internal static void ValidatePolicy(string provider, string algorithm, int bits, CngExportPolicies export,
        CngKeyUsages usage, bool ephemeral, bool machine)
    {
        if (provider != ProviderName || algorithm != "RSA" || bits != 2048 || export != CngExportPolicies.None ||
            usage != CngKeyUsages.Decryption || ephemeral || machine) throw new CryptographicException();
    }
    public byte[] WrapKey(ReadOnlySpan<byte> dataKey)
    {
        if (dataKey.Length != 32) throw new ArgumentException("Invalid key length.");
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = rsa.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256);
            if (result.Length != 256) throw new CryptographicException();
            return result;
        }
    }
    public byte[] UnwrapKey(ReadOnlySpan<byte> wrappedKey)
    {
        if (wrappedKey.Length != 256) throw new ArgumentException("Invalid wrapped key length.");
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = rsa.Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256);
            if (result.Length == 32) return result;
            CryptographicOperations.ZeroMemory(result);
            throw new CryptographicException();
        }
    }
    // Provider metadata only: neither opens a persisted key nor creates one.
    public static void ProbeHardwareProvider()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var status = Native.NCryptOpenStorageProvider(out var handle, ProviderName, 0);
        if (status != 0) throw new CryptographicException(status);
        try
        {
            var bytes = new byte[4];
            status = Native.NCryptGetProperty(handle, "Impl Type", bytes, 4, out var length, 0);
            if (status != 0) throw new CryptographicException(status);
            var flags = BitConverter.ToUInt32(bytes);
            if (length != 4 || (flags & 1) == 0 || (flags & 2) != 0) throw new CryptographicException();
        }
        finally { Native.NCryptFreeObject(handle); }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            rsa.Dispose();
            key.Dispose();
        }
    }
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

