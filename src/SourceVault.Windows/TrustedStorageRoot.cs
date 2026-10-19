using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SourceVault.Windows;

/// <summary>
/// Read-only validation of the "externally provisioned, trusted, service-owned root" that
/// <c>SourceVault.Storage.RevisionStore</c> assumes (contracts/storage-v1.md). It never changes ACLs, never creates
/// directories and never throws on bad input: callers get a pass/fail result with machine-readable findings.
/// It is a point-in-time check; it does not stop an administrator, and it is not proof against a writer racing the
/// check, which is exactly why untrusted write access to the root and its ancestors is a failure.
/// </summary>
public static class TrustedStorageRoot
{
    public static StorageRootValidationResult Validate(string? path, TrustedPrincipals principals, IStorageRootInspector? inspector = null)
    {
        ArgumentNullException.ThrowIfNull(principals);
        try
        {
            var syntax = StorageRootPolicy.ValidatePathSyntax(path);
            if (syntax.Count > 0) return new(syntax);
            inspector ??= new FileSystemStorageRootInspector();
            var findings = new List<StorageRootFinding>();
            var volume = inspector.ObserveVolume(path!);
            // A missing, offline or locked drive is reported on its own: there is nothing on it to judge.
            if (!volume.Available) return new([new StorageRootFinding(FindingCodes.VolumeUnavailable, "volume")]);
            if (volume.DriveType != DriveType.Fixed) findings.Add(new(FindingCodes.VolumeNotFixed, "volume"));
            if (!string.Equals(volume.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(volume.FileSystem, "ReFS", StringComparison.OrdinalIgnoreCase))
                findings.Add(new(FindingCodes.VolumeFilesystemUnsupported, "volume"));
            if (!volume.LocalDisk) findings.Add(new(FindingCodes.VolumeNotLocalDisk, "volume"));
            var chain = new List<DirectoryObservation>();
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
                chain.Add(inspector.ObserveDirectory(current));
            findings.AddRange(StorageRootPolicy.Evaluate(chain, principals));
            return new(findings);
        }
        catch (Exception)
        {
            // Deliberately opaque: no exception text, path or SID is surfaced.
            return new([new StorageRootFinding(FindingCodes.ValidationError, "root")]);
        }
    }

    /// <summary>Human-readable description of the ACL a provisioning script must create. Applies nothing.</summary>
    public static string DescribeExpectedAcl(TrustedPrincipals principals)
    {
        ArgumentNullException.ThrowIfNull(principals);
        var sb = new StringBuilder();
        sb.AppendLine("Trusted storage root: required provisioning (performed by a reviewed lab/installer step, never by the service).");
        sb.AppendLine("Layout: <volume>\\<dedicated trusted directory>\\<store root>. The store root's parent must NOT be the volume root and the store root must not be a volume root.");
        sb.AppendLine("Volume: local fixed NTFS or ReFS on a drive letter that maps to a disk volume (not subst), on a non-removable SATA/ATA/SAS/SCSI/NVMe/RAID/Storage Spaces disk (not USB, 1394, SD/MMC, iSCSI or a mounted VHD/VHDX).");
        sb.AppendLine("No UNC, \\\\?\\ or device paths, no relative segments, no junction/symlink/mount point at the root or any ancestor.");
        sb.AppendLine("Store root (and, recommended, its parent):");
        sb.AppendLine("  Owner: one of the trusted principals (recommended " + Name(PreferredOwner(principals)) + ").");
        sb.AppendLine("  DACL: protected (inheritance from the parent disabled; no inherited ACEs).");
        sb.AppendLine("  Allow ACEs, container+object inherit, FullControl, ONLY for: " + string.Join(", ", principals.AclGrantees.Select(Name)) + ".");
        sb.AppendLine("  No allow ACE granting write-type rights to anyone else (Everyone, Authenticated Users, Users, CREATOR OWNER, OWNER RIGHTS, interactive users). Deny ACEs are permitted.");
        sb.AppendLine("  Write-type rights = WriteData/CreateFiles, AppendData/CreateDirectories, WriteExtendedAttributes, DeleteSubdirectoriesAndFiles, WriteAttributes, Delete, ChangePermissions, TakeOwnership, GenericAll, GenericWrite, AccessSystemSecurity.");
        sb.AppendLine("Parent directory of the root: untrusted principals may have none of CreateFiles, AppendData, DeleteSubdirectoriesAndFiles, Delete, ChangePermissions, TakeOwnership.");
        sb.AppendLine("Every higher ancestor up to the volume root: untrusted principals may have none of DeleteSubdirectoriesAndFiles, Delete, ChangePermissions, TakeOwnership (AppendData/CreateFiles on higher ancestors are tolerated; this is what lets stock C:\\ and C:\\ProgramData pass).");
        sb.AppendLine("Owners of every ancestor must be trusted principals (stock Windows volume roots are owned by SYSTEM or TrustedInstaller).");
        sb.AppendLine("Equivalent root DACL as SDDL (DACL section only; owner is set separately): " + ExpectedRootSddl(principals));
        return sb.ToString();
    }

    /// <summary>DACL-only SDDL for the root: protected, FullControl (OI+CI) for each ACL grantee. Pure; applies nothing.</summary>
    public static string ExpectedRootSddl(TrustedPrincipals principals)
    {
        ArgumentNullException.ThrowIfNull(principals);
        var sb = new StringBuilder("D:P");
        foreach (var sid in principals.AclGrantees) sb.Append("(A;OICI;FA;;;").Append(sid.Value).Append(')');
        return sb.ToString();
    }

    private static SecurityIdentifier PreferredOwner(TrustedPrincipals p) =>
        p.AclGrantees.Contains(TrustedPrincipals.Administrators) ? TrustedPrincipals.Administrators : p.AclGrantees[0];

    private static string Name(SecurityIdentifier sid) => sid.Equals(TrustedPrincipals.LocalSystem) ? "NT AUTHORITY\\SYSTEM (S-1-5-18)"
        : sid.Equals(TrustedPrincipals.Administrators) ? "BUILTIN\\Administrators (S-1-5-32-544)"
        : sid.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal) ? "service/virtual account " + sid.Value : sid.Value;
}

/// <summary>Pure policy: no filesystem or OS calls except <see cref="Path.GetFullPath(string)"/> canonicalisation.</summary>
public static class StorageRootPolicy
{
    // --- Write-type rights decision -------------------------------------------------------------------------------
    // A right is "write-type" if holding it lets a principal change what the service later reads, replace/remove the
    // root or an ancestor, or take over its ACL. Raw FileSystemRights bits (ntifs.h FILE_* values):
    //   0x00002 WriteData/CreateFiles      0x00004 AppendData/CreateDirectories   0x00010 WriteExtendedAttributes
    //   0x00040 DeleteSubdirectoriesAndFiles (delete child)   0x00100 WriteAttributes
    //   0x10000 Delete   0x40000 ChangePermissions (WRITE_DAC)   0x80000 TakeOwnership (WRITE_OWNER)
    // plus unmapped generic bits GENERIC_ALL 0x10000000, GENERIC_WRITE 0x40000000 and ACCESS_SYSTEM_SECURITY
    // 0x01000000 (never legitimate for an untrusted principal; treated as write-type, fail closed).
    // Read-type bits (ReadData, ReadEA, ExecuteFile/Traverse, ReadAttributes, ReadPermissions, Synchronize) are never
    // write-type. Deny ACEs are ignored and are not subtracted from allows (conservative: any untrusted allow counts).
    private const uint WriteData = 0x2, AppendData = 0x4, WriteEa = 0x10, DeleteChild = 0x40, WriteAttributes = 0x100;
    private const uint Delete = 0x10000, WriteDac = 0x40000, WriteOwner = 0x80000;
    private const uint Generic = 0x10000000 | 0x40000000 | 0x01000000;

    // Root: anything that can modify the root's contents, metadata or ACL (inherit-only ACEs count: they govern children).
    internal const uint RootWriteMask = WriteData | AppendData | WriteEa | DeleteChild | WriteAttributes | Delete | WriteDac | WriteOwner | Generic;
    // Parent: rights that let a principal remove/rename/replace the root or pre-create a sibling in its place, or change the parent's ACL.
    internal const uint ParentWriteMask = WriteData | AppendData | DeleteChild | Delete | WriteDac | WriteOwner | Generic;
    // Higher ancestors: only rights that let a principal remove/rename the next ancestor down or rewrite this one's ACL.
    // Creating files/directories in a higher ancestor cannot replace anything below it, so AppendData/CreateFiles are
    // tolerated there. This is the volume-root policy: the default Authenticated Users AppendData on C:\ (and Users
    // create rights on C:\ProgramData) are acceptable ONLY because the root must live in a dedicated directory
    // (root's parent != volume root); if the parent *is* the volume root, AppendData is a parent finding like any other.
    internal const uint AncestorWriteMask = DeleteChild | Delete | WriteDac | WriteOwner | Generic;

    private static readonly string[] ReservedNames =
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    // Allow-list of STORAGE_BUS_TYPE values (winioctl.h) for a vault disk: SCSI 1, ATA 3, RAID 8, SAS 10, SATA 11,
    // Storage Spaces 16, NVMe 17. Everything else fails closed, notably USB 7, 1394 4, SD 12, MMC 13, iSCSI 9,
    // Fibre 6, Virtual 14 and FileBackedVirtual 15 (a mounted VHD/VHDX, whose file lives on an unchecked volume).
    // Limits: a Thunderbolt NVMe or eSATA enclosure reports NVMe/SATA, and Storage Spaces hides its physical disks.
    public static bool IsLocalDiskBus(uint busType, bool removableMedia) => !removableMedia && busType is 1 or 3 or 8 or 10 or 11 or 16 or 17;

    /// <summary>True only for a drive letter's DOS-device target that is a disk volume, e.g. <c>\Device\HarddiskVolume3</c>; a <c>subst</c> drive maps to <c>\??\C:\...</c>.</summary>
    public static bool IsVolumeDeviceTarget(string? target)
    {
        const string prefix = @"\Device\HarddiskVolume";
        return target is not null && target.Length > prefix.Length && target.Length <= prefix.Length + 5 &&
            target.StartsWith(prefix, StringComparison.Ordinal) && !target.AsSpan(prefix.Length).ContainsAnyExceptInRange('0', '9');
    }

    public static IReadOnlyList<StorageRootFinding> ValidatePathSyntax(string? path)
    {
        StorageRootFinding F(string code) => new(code, "root");
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.Any(c => c < ' ')) return [F(FindingCodes.PathInvalid)];
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return [F(FindingCodes.PathUncOrDevice)];
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return [F(FindingCodes.PathNotAbsolute)];
        var rest = path[3..];
        if (rest.Length > 0)
        {
            foreach (var segment in rest.Split('\\'))
            {
                if (segment is "." or "..") return [F(FindingCodes.PathRelativeSegment)];
                if (segment.Length == 0 || segment.IndexOfAny(['/', ':', '*', '?', '"', '<', '>', '|']) >= 0 || segment[^1] is ' ' or '.')
                    return [F(FindingCodes.PathInvalid)];
                var stem = segment.Split('.')[0].TrimEnd(' ');
                if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) return [F(FindingCodes.PathUncOrDevice)];
            }
        }
        // Rejects anything the OS would normalise (short-name-independent: forward slashes, trailing dots, '.' segments).
        if (!string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal)) return [F(FindingCodes.PathNotCanonical)];
        return [];
    }

    /// <param name="chain">Index 0 is the root, 1 its parent, ... last is the volume root.</param>
    public static IReadOnlyList<StorageRootFinding> Evaluate(IReadOnlyList<DirectoryObservation> chain, TrustedPrincipals trusted)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(trusted);
        var findings = new List<StorageRootFinding>();
        if (chain.Count == 0) return [new(FindingCodes.ValidationError, "root")];
        if (chain.Count == 1) findings.Add(new(FindingCodes.RootIsVolumeRoot, "root"));
        for (var i = 0; i < chain.Count; i++)
        {
            var location = i == 0 ? "root" : "ancestor:" + i;
            var observation = chain[i];
            switch (observation.State)
            {
                case DirectoryState.Missing: findings.Add(new(FindingCodes.Missing, location)); continue;
                case DirectoryState.NotADirectory: findings.Add(new(FindingCodes.NotADirectory, location)); continue;
                case DirectoryState.ReparsePoint: findings.Add(new(FindingCodes.ReparsePoint, location)); continue;
                case DirectoryState.Unreadable: findings.Add(new(FindingCodes.SecurityUnreadable, location)); continue;
            }
            var security = observation.Security;
            if (security is null) { findings.Add(new(FindingCodes.SecurityUnreadable, location)); continue; }
            if (security.Owner is null || !trusted.IsTrusted(security.Owner))
                findings.Add(new(FindingCodes.OwnerUntrusted, location, FindingSeverity.Error, security.Owner?.Value));
            if (!security.DaclPresent) { findings.Add(new(FindingCodes.DaclMissing, location)); continue; } // null DACL = everyone full control
            if (i == 0 && !security.DaclProtected) findings.Add(new(FindingCodes.DaclNotProtected, location));
            var mask = i == 0 ? RootWriteMask : i == 1 ? ParentWriteMask : AncestorWriteMask;
            var untrusted = new Dictionary<SecurityIdentifier, uint>();
            foreach (var ace in security.Aces)
            {
                if (!ace.IsAllow || trusted.IsTrusted(ace.Sid)) continue;
                if (i > 0 && ace.InheritOnly) continue; // inherit-only ACEs do not apply to the ancestor itself
                untrusted[ace.Sid] = untrusted.GetValueOrDefault(ace.Sid) | ace.Rights;
            }
            foreach (var (sid, rights) in untrusted.OrderBy(p => p.Key.Value, StringComparer.Ordinal))
            {
                if ((rights & mask) != 0) findings.Add(new(FindingCodes.UntrustedWriteAccess, location, FindingSeverity.Error, sid.Value));
                else if (i == 0 && rights != 0) findings.Add(new(FindingCodes.UntrustedReadAccess, location, FindingSeverity.Warning, sid.Value));
            }
        }
        return findings;
    }
}

/// <summary>Real, read-only inspector. Reads attributes without following the final component; never throws.</summary>
public sealed class FileSystemStorageRootInspector : IStorageRootInspector
{
    public VolumeObservation ObserveVolume(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            var type = drive.DriveType;
            if (type == DriveType.NoRootDirectory || (type == DriveType.Fixed && !drive.IsReady)) return new(type, null, Available: false);
            if (!drive.IsReady) return new(type, null);
            return new(type, drive.DriveFormat, LocalDisk.Probe(path[0]));
        }
        catch (Exception) { return new(DriveType.Unknown, null); }
    }

    public DirectoryObservation ObserveDirectory(string path)
    {
        try
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new(DirectoryState.Missing, null); }
            if ((attributes & FileAttributes.ReparsePoint) != 0) return new(DirectoryState.ReparsePoint, null);
            if ((attributes & FileAttributes.Directory) == 0) return new(DirectoryState.NotADirectory, null);
            var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            return new(DirectoryState.Directory, DirectorySecurityInfo.FromSecurity(security));
        }
        catch (Exception) { return new(DirectoryState.Unreadable, null); }
    }
}

/// <summary>Read-only device query for one drive letter. Opens the volume with no access rights (metadata only); never throws.</summary>
internal static class LocalDisk
{
    internal static bool Probe(char driveLetter)
    {
        try
        {
            if (!char.IsAsciiLetter(driveLetter)) return false;
            var buffer = new char[1024];
            var length = QueryDosDeviceW(driveLetter + ":", buffer, buffer.Length);
            if (length == 0) return false;
            var end = Array.IndexOf(buffer, '\0', 0, (int)length);
            var target = new string(buffer, 0, end < 0 ? (int)length : end);
            if (!StorageRootPolicy.IsVolumeDeviceTarget(target)) return false;
            using var volume = CreateFileW(@"\\.\" + driveLetter + ":", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (volume.IsInvalid) return false;
            var query = new byte[12]; // STORAGE_PROPERTY_QUERY { StorageDeviceProperty = 0, PropertyStandardQuery = 0 }
            var descriptor = new byte[1024]; // STORAGE_DEVICE_DESCRIPTOR
            if (!DeviceIoControl(volume, IoctlStorageQueryProperty, query, query.Length, descriptor, descriptor.Length, out var returned, IntPtr.Zero) || returned < 32) return false;
            return StorageRootPolicy.IsLocalDiskBus(BitConverter.ToUInt32(descriptor, 28), descriptor[10] != 0); // BusType, RemovableMedia
        }
        catch (Exception) { return false; }
    }

    private const uint FileShareReadWrite = 0x1 | 0x2, OpenExisting = 3, IoctlStorageQueryProperty = 0x2D1400;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint QueryDosDeviceW(string device, [Out] char[] target, int max);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
