using SourceVault.Windows;

namespace SourceVault.Service;

/// <summary>
/// The build output folder (build.json artifactRoot), checked like the vault root (internal security review,
/// 2026-10-20): a fixed local NTFS/ReFS volume, no links, trusted owners, a protected ACL, and no untrusted principal able
/// to change the folder or its parents. Trusted here: the build service, SYSTEM, Administrators (and TrustedInstaller as
/// an owner). Operators' read access is expected and only a warning. Without this, a non-operator who could rename a
/// parent folder could put a link in its place and receive the build output.
/// </summary>
internal static class ArtifactRoot
{
    internal static TrustedPrincipals Principals
    {
        get
        {
            var build = ServiceSid.FromServiceName(ScmHost.BuildServiceName);
            return new TrustedPrincipals([build, TrustedPrincipals.LocalSystem, TrustedPrincipals.Administrators, TrustedPrincipals.TrustedInstaller],
                [build, TrustedPrincipals.LocalSystem, TrustedPrincipals.Administrators]);
        }
    }

    internal static StorageRootValidationResult Validate(string root, IStorageRootInspector? inspector = null) =>
        TrustedStorageRoot.Validate(root, Principals, inspector);

    internal static bool IsTrusted(string root) => Validate(root).Passed;
}
