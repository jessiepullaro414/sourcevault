using SourceVault.Windows;

namespace SourceVault.Service;

/// <summary>
/// The service's vault root: <see cref="DefaultPath"/>, or the administrator-chosen <c>vaultRoot</c> in
/// sourcevault.service.json (schema 2). Never taken from arguments or the environment. Provisioned by an administrator
/// (setup, or scripts/lab/New-VaultRoot.ps1); the service only validates it.
/// </summary>
internal static class VaultRoot
{
    internal const int MaxPathLength = 120;

    internal static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "SourceVault", "vault");

    internal static TrustedPrincipals Principals => TrustedPrincipals.CreateDefault(ScmHost.ServiceName);

    internal static StorageRootValidationResult Validate(string path, IStorageRootInspector? inspector = null) =>
        TrustedStorageRoot.Validate(path, Principals, inspector);

    /// <summary>Startup gate: code 17 when the drive is missing, offline or locked; code 10 for any other finding.</summary>
    internal static void RequireTrusted(string path, IStorageRootInspector? inspector = null)
    {
        var result = Validate(path, inspector);
        if (result.HasCode(FindingCodes.VolumeUnavailable)) throw new StartupRefusedException(StartupRefusedException.VaultDriveUnavailable);
        if (!result.Passed) throw new StartupRefusedException(StartupRefusedException.VaultRootUntrusted);
    }

    /// <summary>
    /// Config-time shape of an administrator-chosen root (pure; the filesystem is judged at startup): canonical
    /// <c>X:\folder\leaf</c> or deeper, at most <see cref="MaxPathLength"/> characters, and not overlapping the install
    /// folder, the admin configuration folder or Windows.
    /// </summary>
    internal static bool IsAcceptablePath(string path)
    {
        if (path.Length > MaxPathLength || StorageRootPolicy.ValidatePathSyntax(path).Count > 0) return false;
        if (path[3..].Split('\\').Length < 2) return false; // the root's parent must be a dedicated folder, not the drive root
        string[] reserved =
        [
            ScmHost.InstallRoot,
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SourceVault"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SourceVault", "config"),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ];
        return !reserved.Any(r => r.Length > 0 && Overlaps(path, r));
    }

    /// <summary>The folder holding the configured vault, which build output must stay out of (read per build request).</summary>
    internal static string ConfiguredVaultFolder()
    {
        try { return System.IO.Path.GetDirectoryName(ServiceConfiguration.LoadFromApplicationDirectory().VaultRootPath)!; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { throw new SourceVault.Build.BuildConfigException("sourcevault.service.json could not be read, so the vault folder is unknown."); }
    }

    internal static bool Overlaps(string a, string b)
    {
        a = System.IO.Path.TrimEndingDirectorySeparator(a); b = System.IO.Path.TrimEndingDirectorySeparator(b);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A startup check failed for a known reason, reported as a bounded service-specific exit code.</summary>
internal sealed class StartupRefusedException(uint code) : Exception
{
    internal const uint VaultRootUntrusted = 10;
    internal const uint CatalogUntrusted = 11; // unexpected entries in the vault root, or the project index failed
    internal const uint ProjectUntrusted = 12; // a project store failed authentication
    internal const uint OperatorsGroupMissing = 13; // local group "SourceVault Operators" does not exist
    internal const uint PipeUnavailable = 14; // the pipe could not be created, e.g. its name was already taken
    internal const uint VaultServiceMissing = 15; // build service: NT SERVICE\SourceVault (its only client) does not exist
    internal const uint WrongIdentity = 16; // build service: not running as NT SERVICE\SourceVaultBuild
    internal const uint VaultDriveUnavailable = 17; // the vault's drive letter is missing, offline or locked (e.g. BitLocker)
    internal uint Code { get; } = code;
}
