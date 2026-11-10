using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SourceVault.Service;

/// <summary>
/// Approvers typed by name (docs/disc-export-design.md, section 3). The account is resolved to a SID, and its groups
/// are worked out with Windows' AuthZ API from the SID alone (local resource manager, so local groups and the domain
/// groups nested in them count). Anything that can't be resolved is "no", never assumed. BCL P/Invoke only.
/// </summary>
internal sealed class AuthzAccounts : IAccountDirectory
{
    public SecurityIdentifier? Resolve(string account)
    {
        if (string.IsNullOrWhiteSpace(account) || account.Length > 256 || account.Any(char.IsControl)) return null;
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(account.Trim()).Translate(typeof(SecurityIdentifier));
            return sid.IsAccountSid() ? sid : null; // a user account, not a group or a well-known SID
        }
        catch (Exception e) when (e is IdentityNotMappedException or SystemException) { return null; }
    }

    public string AccountName(SecurityIdentifier user)
    {
        try { return user.Translate(typeof(NTAccount)).Value; }
        catch (Exception e) when (e is IdentityNotMappedException or SystemException) { return user.Value; }
    }

    public bool? IsMember(SecurityIdentifier user, SecurityIdentifier group)
    {
        var sid = new byte[user.BinaryLength];
        user.GetBinaryForm(sid, 0);
        var sidPointer = Marshal.AllocHGlobal(sid.Length);
        IntPtr manager = IntPtr.Zero, context = IntPtr.Zero, groups = IntPtr.Zero;
        try
        {
            Marshal.Copy(sid, 0, sidPointer, sid.Length);
            if (!AuthzInitializeResourceManager(AuthzRmFlagNoAudit, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, null, out manager)) return null;
            if (!AuthzInitializeContextFromSid(0, sidPointer, manager, IntPtr.Zero, default, IntPtr.Zero, out context)) return null;
            AuthzGetInformationFromContext(context, AuthzContextInfoGroupsSids, 0, out uint size, IntPtr.Zero); // asks for the size
            if (size is 0 or > 1 << 20) return null;
            groups = Marshal.AllocHGlobal((int)size);
            if (!AuthzGetInformationFromContext(context, AuthzContextInfoGroupsSids, size, out _, groups)) return null;
            // TOKEN_GROUPS: DWORD GroupCount, then SID_AND_ATTRIBUTES { PSID; DWORD } entries aligned to the pointer size.
            int count = Marshal.ReadInt32(groups), entry = IntPtr.Size * 2;
            for (int i = 0; i < count; i++)
            {
                var member = new SecurityIdentifier(Marshal.ReadIntPtr(groups, IntPtr.Size + i * entry));
                int attributes = Marshal.ReadInt32(groups, IntPtr.Size + i * entry + IntPtr.Size);
                if ((attributes & SeGroupEnabled) != 0 && (attributes & SeGroupUseForDenyOnly) == 0 && member.Equals(group)) return true;
            }
            return false;
        }
        finally
        {
            if (groups != IntPtr.Zero) Marshal.FreeHGlobal(groups);
            if (context != IntPtr.Zero) AuthzFreeContext(context);
            if (manager != IntPtr.Zero) AuthzFreeResourceManager(manager);
            Marshal.FreeHGlobal(sidPointer);
        }
    }

    private const uint AuthzRmFlagNoAudit = 0x1, AuthzContextInfoGroupsSids = 2;
    private const int SeGroupEnabled = 0x4, SeGroupUseForDenyOnly = 0x10;

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }

    [DllImport("authz.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool AuthzInitializeResourceManager(uint flags, IntPtr accessCheck, IntPtr computeDynamicGroups, IntPtr freeDynamicGroups, string? name, out IntPtr manager);
    [DllImport("authz.dll", SetLastError = true)]
    private static extern bool AuthzInitializeContextFromSid(uint flags, IntPtr sid, IntPtr manager, IntPtr expiration, Luid identifier, IntPtr dynamicGroupArgs, out IntPtr context);
    [DllImport("authz.dll", SetLastError = true)]
    private static extern bool AuthzGetInformationFromContext(IntPtr context, uint infoClass, uint bufferSize, out uint sizeRequired, IntPtr buffer);
    [DllImport("authz.dll")] private static extern bool AuthzFreeContext(IntPtr context);
    [DllImport("authz.dll")] private static extern bool AuthzFreeResourceManager(IntPtr manager);
}
