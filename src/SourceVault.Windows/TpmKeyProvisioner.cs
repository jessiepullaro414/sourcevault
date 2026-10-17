using System.Security.Cryptography;

namespace SourceVault.Windows;

/// <summary>Bounded result of a provisioning attempt. Values are reported as the service-specific exit code.</summary>
public enum ProvisionOutcome : uint
{
    Created = 0,
    AlreadyExists = 1,
    Unavailable = 2,      // wrong identity, missing provider/config, or key creation failed
    PolicyRejected = 3,   // created, but the production open path refused its metadata
    ExportNotRefused = 4, // created, but the private key could be exported
    RoundtripFailed = 5,  // created and opened, but a synthetic wrap/unwrap did not match
}

/// <summary>
/// One-time creation of the service's decrypt-only TPM wrapping key, run only under <c>NT SERVICE\SourceVault</c>.
/// Kept separate from <see cref="TpmKeyProtector"/> so the runtime adapter stays open-only. Never overwrites or
/// deletes a key; a key that fails a later check is left in place and reported, for administrator cleanup.
/// </summary>
public static class TpmKeyProvisioner
{
    private const string PcpKeyUsagePolicyProperty = "PCP_KEY_USAGE_POLICY"; // NCRYPT_PCP_KEY_USAGE_POLICY_PROPERTY
    private const uint PcpEncryptionKey = 0x2; // NCRYPT_PCP_ENCRYPTION_KEY; without it PCP creates a generic sign+encrypt key

    public static ProvisionOutcome CreateDecryptOnly(string keyName)
    {
        TpmKeyProtector.ValidateKeyName(keyName);
        try
        {
            TpmKeyProtector.RequireServiceIdentity(); // Must precede hardware/provider/key operations.
            TpmKeyProtector.ProbeHardwareProvider();
        }
        catch (Exception e) when (e is CryptographicException or UnauthorizedAccessException or PlatformNotSupportedException
            or System.Security.Principal.IdentityNotMappedException) // service account absent: not installed
        {
            return ProvisionOutcome.Unavailable;
        }
        var provider = new CngProvider(TpmKeyProtector.ProviderName);
        if (CngKey.Exists(keyName, provider)) return ProvisionOutcome.AlreadyExists;

        var parameters = new CngKeyCreationParameters
        {
            Provider = provider,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Decryption,
            KeyCreationOptions = CngKeyCreationOptions.None, // never OverwriteExistingKey, never MachineKey
        };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
        parameters.Parameters.Add(new CngProperty(PcpKeyUsagePolicyProperty, BitConverter.GetBytes(PcpEncryptionKey), CngPropertyOptions.None));
        CngKey created;
        try { created = CngKey.Create(CngAlgorithm.Rsa, keyName, parameters); }
        catch (CryptographicException) { return ProvisionOutcome.Unavailable; }

        using (created)
            foreach (var format in new[] { CngKeyBlobFormat.GenericPrivateBlob, CngKeyBlobFormat.Pkcs8PrivateBlob })
            {
                try
                {
                    var exported = created.Export(format);
                    CryptographicOperations.ZeroMemory(exported);
                    return ProvisionOutcome.ExportNotRefused;
                }
                catch (CryptographicException) { /* expected: nonexportable hardware key */ }
            }

        TpmKeyProtector opened;
        try { opened = TpmKeyProtector.OpenExisting(keyName); } // the same metadata checks the service uses at startup
        catch (Exception e) when (e is CryptographicException or UnauthorizedAccessException) { return ProvisionOutcome.PolicyRejected; }
        using (opened)
        {
            var dataKey = RandomNumberGenerator.GetBytes(32);
            byte[]? recovered = null;
            try
            {
                recovered = opened.UnwrapKey(opened.WrapKey(dataKey));
                return CryptographicOperations.FixedTimeEquals(dataKey, recovered) ? ProvisionOutcome.Created : ProvisionOutcome.RoundtripFailed;
            }
            catch (CryptographicException) { return ProvisionOutcome.RoundtripFailed; }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKey);
                if (recovered is not null) CryptographicOperations.ZeroMemory(recovered);
            }
        }
    }
}
