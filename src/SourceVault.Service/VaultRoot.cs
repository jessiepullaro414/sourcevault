using SourceVault.Windows;

namespace SourceVault.Service;

/// <summary>
/// The service's fixed vault root. Not configurable: no operator- or config-supplied storage paths.
/// Provisioned by an administrator (scripts/lab/New-VaultRoot.ps1); the service only validates it.
/// </summary>
internal static class VaultRoot
{
    internal static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "SourceVault", "vault");

    internal static TrustedPrincipals Principals => TrustedPrincipals.CreateDefault(ScmHost.ServiceName);

    internal static StorageRootValidationResult Validate() => TrustedStorageRoot.Validate(Path, Principals);
}

/// <summary>A startup check failed for a known reason, reported as a bounded service-specific exit code.</summary>
internal sealed class StartupRefusedException(uint code) : Exception
{
    internal const uint VaultRootUntrusted = 10;
    internal uint Code { get; } = code;
}
