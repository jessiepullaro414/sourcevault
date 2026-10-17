using System.Security.AccessControl;
using System.Security.Principal;

namespace SourceVault.Windows;

public enum FindingSeverity { Error, Warning }

/// <summary>Machine-readable finding. Never contains exception text or paths. Location is "root", "volume" or "ancestor:N" (N=1 is the parent).</summary>
public sealed record StorageRootFinding(string Code, string Location, FindingSeverity Severity = FindingSeverity.Error, string? Subject = null);

public sealed class StorageRootValidationResult
{
    public StorageRootValidationResult(IEnumerable<StorageRootFinding> findings) => Findings = findings.ToList();
    public IReadOnlyList<StorageRootFinding> Findings { get; }
    /// <summary>True only when there are no error-severity findings. Warnings do not fail validation.</summary>
    public bool Passed => Findings.All(f => f.Severity != FindingSeverity.Error);
    public bool HasCode(string code) => Findings.Any(f => f.Code == code);
    public bool HasCode(string code, string location) => Findings.Any(f => f.Code == code && f.Location == location);
}

public static class FindingCodes
{
    public const string PathInvalid = "path_invalid";
    public const string PathNotAbsolute = "path_not_absolute";
    public const string PathUncOrDevice = "path_unc_or_device";
    public const string PathRelativeSegment = "path_relative_segment";
    public const string PathNotCanonical = "path_not_canonical";
    public const string VolumeNotFixed = "volume_not_fixed";
    public const string VolumeFilesystemUnsupported = "volume_filesystem_unsupported";
    public const string RootIsVolumeRoot = "root_is_volume_root";
    public const string Missing = "missing";
    public const string NotADirectory = "not_a_directory";
    public const string ReparsePoint = "reparse_point";
    public const string SecurityUnreadable = "security_unreadable";
    public const string OwnerUntrusted = "owner_untrusted";
    public const string DaclMissing = "dacl_missing";
    public const string DaclNotProtected = "dacl_not_protected";
    public const string UntrustedWriteAccess = "untrusted_write_access";
    public const string UntrustedReadAccess = "untrusted_read_access";
    public const string ValidationError = "validation_error";
}

/// <summary>One allow/deny ACE reduced to what the policy needs. Plain data so policy tests need no filesystem.</summary>
public sealed record AceInfo(SecurityIdentifier Sid, bool IsAllow, uint Rights, bool InheritOnly)
{
    public AceInfo(SecurityIdentifier sid, bool isAllow, FileSystemRights rights, bool inheritOnly = false)
        : this(sid, isAllow, unchecked((uint)(int)rights), inheritOnly) { }
}

public sealed record DirectorySecurityInfo(SecurityIdentifier? Owner, bool DaclPresent, bool DaclProtected, IReadOnlyList<AceInfo> Aces)
{
    /// <summary>Converts a (possibly in-memory, synthetic) FileSystemSecurity without any name lookups.</summary>
    public static DirectorySecurityInfo FromSecurity(FileSystemSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var aces = new List<AceInfo>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference is not SecurityIdentifier sid) throw new InvalidDataException();
            aces.Add(new AceInfo(sid, rule.AccessControlType == AccessControlType.Allow, rule.FileSystemRights,
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0));
        }
        return new(owner, raw.DiscretionaryAcl is not null, security.AreAccessRulesProtected, aces);
    }
}

public enum DirectoryState { Directory, Missing, NotADirectory, ReparsePoint, Unreadable }

public sealed record DirectoryObservation(DirectoryState State, DirectorySecurityInfo? Security);

public sealed record VolumeObservation(DriveType DriveType, string? FileSystem);

/// <summary>Read-only view of the filesystem. Real implementation: <see cref="FileSystemStorageRootInspector"/>.</summary>
public interface IStorageRootInspector
{
    VolumeObservation ObserveVolume(string path);
    /// <summary>Must not follow a reparse point on the final component and must not throw.</summary>
    DirectoryObservation ObserveDirectory(string path);
}
