using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace SourceVault.Windows;

/// <summary>
/// Configured set of principals (by SID, never by name lookup) that may own or hold write-type rights on a
/// storage root and its ancestors. <see cref="AclGrantees"/> are the principals a provisioning script is
/// expected to grant access to; <see cref="Trusted"/> may additionally contain principals that are accepted
/// as owners/ancestor holders but receive no ACE on the root (for example TrustedInstaller, which owns
/// the volume root on stock Windows 11).
/// </summary>
public sealed class TrustedPrincipals
{
    public static readonly SecurityIdentifier LocalSystem = new("S-1-5-18");
    public static readonly SecurityIdentifier Administrators = new("S-1-5-32-544");
    public static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private readonly HashSet<SecurityIdentifier> trusted;
    private readonly List<SecurityIdentifier> grantees;

    public TrustedPrincipals(IEnumerable<SecurityIdentifier> trusted, IEnumerable<SecurityIdentifier>? aclGrantees = null)
    {
        ArgumentNullException.ThrowIfNull(trusted);
        this.trusted = new HashSet<SecurityIdentifier>(trusted);
        if (this.trusted.Count == 0) throw new ArgumentException("At least one trusted principal is required.", nameof(trusted));
        grantees = (aclGrantees ?? this.trusted).Distinct().ToList();
        if (grantees.Count == 0 || grantees.Any(g => !this.trusted.Contains(g)))
            throw new ArgumentException("ACL grantees must be a non-empty subset of trusted principals.", nameof(aclGrantees));
    }

    /// <summary>Service virtual account, SYSTEM and Administrators get ACEs; TrustedInstaller is accepted as an owner/ancestor holder only.</summary>
    public static TrustedPrincipals CreateDefault(string serviceName = "SourceVault")
    {
        var service = ServiceSid.FromServiceName(serviceName);
        return new TrustedPrincipals([service, LocalSystem, Administrators, TrustedInstaller], [service, LocalSystem, Administrators]);
    }

    public IReadOnlyCollection<SecurityIdentifier> Trusted => trusted;
    public IReadOnlyList<SecurityIdentifier> AclGrantees => grantees;
    public bool IsTrusted(SecurityIdentifier sid) => trusted.Contains(sid);
}

/// <summary>Derives the per-service SID (NT SERVICE\name, S-1-5-80-...) offline; the service need not be installed.</summary>
public static class ServiceSid
{
    public static SecurityIdentifier FromServiceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256) throw new ArgumentException("Invalid service name.", nameof(name));
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(name.ToUpperInvariant()));
        var parts = new string[5];
        for (var i = 0; i < 5; i++) parts[i] = BitConverter.ToUInt32(hash, i * 4).ToString(CultureInfo.InvariantCulture);
        return new SecurityIdentifier("S-1-5-80-" + string.Join('-', parts));
    }
}
