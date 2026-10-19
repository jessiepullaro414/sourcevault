using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SourceVault.Pack;

/// <summary>What Pack signs with. Production: <see cref="SenderKey"/> in this computer's TPM. Tests substitute a software key.</summary>
internal interface IPackageSigner : IDisposable
{
    byte[] SubjectPublicKeyInfo();
    byte[] SignHash(byte[] hash);
}

/// <summary>The sender key could not be created, opened or used. The message says what to do; it never holds key material.</summary>
internal sealed class SenderKeyException(string message) : Exception(message);

/// <summary>
/// This computer's package-signing key (docs/package-import-design.md, D2): RSA-2048, signing only, created in the
/// TPM by Microsoft Platform Crypto Provider for the current Windows user, never exportable. There is no software
/// fallback: without a usable TPM, Pack refuses.
/// </summary>
internal sealed class SenderKey : IPackageSigner
{
    internal const string KeyName = "SourceVault.Sender";
    private const string ProviderName = "Microsoft Platform Crypto Provider";
    private const string PcpKeyUsagePolicyProperty = "PCP_KEY_USAGE_POLICY";
    private const uint PcpSignatureKey = 0x1; // NCRYPT_PCP_SIGNATURE_KEY
    private readonly CngKey key;
    private readonly RSACng rsa;

    private SenderKey(CngKey opened)
    {
        key = opened;
        try
        {
            if (key.Provider?.Provider != ProviderName || key.Algorithm.Algorithm != "RSA" || key.KeySize != 2048
                || key.ExportPolicy != CngExportPolicies.None || key.IsEphemeral || key.IsMachineKey)
                throw new SenderKeyException($"The existing key '{KeyName}' is not a non-exportable TPM RSA-2048 key; delete it, then run 'SourceVault.Pack.exe init' again.");
            rsa = new RSACng(key);
        }
        catch { key.Dispose(); throw; }
    }

    internal static bool Exists() => CngKey.Exists(KeyName, Provider());

    /// <summary>Creates the key if it doesn't exist (true when created), then opens it and checks it can't be exported.</summary>
    internal static (SenderKey Key, bool Created) OpenOrCreate()
    {
        var provider = Provider();
        bool created = false;
        if (!CngKey.Exists(KeyName, provider))
        {
            var parameters = new CngKeyCreationParameters
            {
                Provider = provider,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing,
                KeyCreationOptions = CngKeyCreationOptions.None, // never OverwriteExistingKey, never MachineKey
            };
            parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
            parameters.Parameters.Add(new CngProperty(PcpKeyUsagePolicyProperty, BitConverter.GetBytes(PcpSignatureKey), CngPropertyOptions.None));
            try { CngKey.Create(CngAlgorithm.Rsa, KeyName, parameters).Dispose(); created = true; }
            catch (CryptographicException) { throw new SenderKeyException("This computer's TPM could not create the signing key. Pack needs a working TPM 2.0 (Windows 11 requires one); check that it is on in the firmware and ready in tpm.msc."); }
        }
        return (Open(), created);
    }

    internal static SenderKey Open()
    {
        CngKey opened;
        try { opened = CngKey.Open(KeyName, Provider(), CngKeyOpenOptions.Silent); }
        catch (CryptographicException) { throw new SenderKeyException("This computer has no SourceVault sender key yet. Run 'SourceVault.Pack.exe init' first."); }
        var sender = new SenderKey(opened);
        foreach (var format in new[] { CngKeyBlobFormat.GenericPrivateBlob, CngKeyBlobFormat.Pkcs8PrivateBlob })
        {
            try
            {
                var exported = opened.Export(format);
                CryptographicOperations.ZeroMemory(exported);
                sender.Dispose();
                throw new SenderKeyException($"The key '{KeyName}' can be exported, so it is not protected by the TPM; Pack will not use it.");
            }
            catch (CryptographicException) { /* expected: a non-exportable hardware key */ }
        }
        return sender;
    }

    public byte[] SubjectPublicKeyInfo() => rsa.ExportSubjectPublicKeyInfo();

    /// <summary>RSA-PSS SHA-256 over a 32-byte digest, inside the TPM.</summary>
    public byte[] SignHash(byte[] hash)
    {
        try { return rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pss); }
        catch (CryptographicException) { throw new SenderKeyException("This computer's TPM refused to sign (RSA-PSS SHA-256). Record this on the lab checklist."); }
    }

    private static CngProvider Provider()
    {
        if (!OperatingSystem.IsWindows()) throw new SenderKeyException("SourceVault Pack runs on Windows only.");
        // The provider must be the hardware Platform Crypto Provider, not a software emulation.
        int status = NCryptOpenStorageProvider(out var handle, ProviderName, 0);
        if (status != 0) throw new SenderKeyException("This computer has no TPM key provider (Microsoft Platform Crypto Provider). Pack needs a TPM 2.0.");
        try
        {
            var bytes = new byte[4];
            status = NCryptGetProperty(handle, "Impl Type", bytes, 4, out int length, 0);
            uint flags = BitConverter.ToUInt32(bytes);
            if (status != 0 || length != 4 || (flags & 1) == 0 || (flags & 2) != 0) throw new SenderKeyException("The TPM key provider is not hardware-backed; Pack will not use a software key.");
        }
        finally { NCryptFreeObject(handle); }
        return new CngProvider(ProviderName);
    }

    public void Dispose() { rsa.Dispose(); key.Dispose(); }

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptOpenStorageProvider(out IntPtr provider, string providerName, int flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptGetProperty(IntPtr provider, string property, [Out] byte[] output, int outputBytes, out int resultBytes, int flags);
    [DllImport("ncrypt.dll")] private static extern int NCryptFreeObject(IntPtr handle);
}
