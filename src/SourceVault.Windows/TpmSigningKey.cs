using System.Security.Cryptography;

namespace SourceVault.Windows;

/// <summary>
/// The service's disc signing key (docs/disc-export-design.md, section 4): RSA-2048 in the TPM (Microsoft Platform
/// Crypto Provider), sign-only, non-exportable, under <c>NT SERVICE\SourceVault</c>. Separate from the decrypt-only vault
/// and import keys, so a signature can never be turned into an unwrap. Existing-key adapter only: creation is
/// <see cref="TpmKeyProvisioner.CreateSignOnly"/>, run once by setup.
/// </summary>
public sealed class TpmSigningKey : IDisposable
{
    private readonly CngKey key;
    private readonly RSACng rsa;
    private readonly object sync = new();
    private bool disposed;

    private TpmSigningKey(CngKey opened)
    {
        key = opened;
        try
        {
            ValidatePolicy(key.Provider?.Provider ?? "", key.Algorithm.Algorithm ?? "", key.KeySize, key.ExportPolicy, key.KeyUsage, key.IsEphemeral, key.IsMachineKey);
            foreach (var format in new[] { CngKeyBlobFormat.GenericPrivateBlob, CngKeyBlobFormat.Pkcs8PrivateBlob })
            {
                byte[]? exported = null;
                try { exported = key.Export(format); }
                catch (CryptographicException) { continue; } // expected: a non-exportable hardware key
                CryptographicOperations.ZeroMemory(exported);
                throw new CryptographicException();
            }
            rsa = new RSACng(key);
        }
        catch { key.Dispose(); throw; }
    }

    public static TpmSigningKey OpenExisting(string keyName)
    {
        TpmKeyProtector.ValidateKeyName(keyName);
        TpmKeyProtector.RequireServiceIdentity(); // Must precede hardware/provider/key operations.
        TpmKeyProtector.ProbeHardwareProvider();
        return new(CngKey.Open(keyName, new CngProvider(TpmKeyProtector.ProviderName), CngKeyOpenOptions.Silent));
    }

    internal static void ValidatePolicy(string provider, string algorithm, int bits, CngExportPolicies export, CngKeyUsages usage, bool ephemeral, bool machine)
    {
        if (provider != TpmKeyProtector.ProviderName || algorithm != "RSA" || bits != 2048 || export != CngExportPolicies.None ||
            usage != CngKeyUsages.Signing || ephemeral || machine) throw new CryptographicException();
    }

    /// <summary>RSA-PSS SHA-256 over a 32-byte digest, inside the TPM.</summary>
    public byte[] SignHash(byte[] hash)
    {
        if (hash.Length != 32) throw new ArgumentException("Invalid hash length.");
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
    }

    /// <summary>The key's public half (SubjectPublicKeyInfo); the private key never leaves the TPM.</summary>
    public byte[] ExportSubjectPublicKeyInfo()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return rsa.ExportSubjectPublicKeyInfo();
        }
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
}
