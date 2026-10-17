using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using SourceVault.Windows;

// Usage: SourceVault.Windows.Tests.dll <absolute test workspace>
// ACLs are only ever changed on directories this program creates under the workspace. Everything else is read-only
// or a pure evaluation over synthetic ACE data. Synthetic data only.
if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Explicit absolute test workspace required.");
Directory.CreateDirectory(args[0]);
var workspace = Path.Combine(args[0], "windows-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);

int passed = 0, skipped = 0, failed = 0;
void Assert(bool value, string? why = null) { if (!value) throw new Exception("Assertion failed" + (why is null ? "." : ": " + why)); }
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (SkipException e) { skipped++; Console.WriteLine("SKIP " + name + ": " + e.Message); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
string Codes(StorageRootValidationResult r) => string.Join(",", r.Findings.Select(f => f.Code + "@" + f.Location));

// ---- synthetic principals and ACE helpers -------------------------------------------------------------------------
SecurityIdentifier Sid(string s) => new(s);
var SY = TrustedPrincipals.LocalSystem; var BA = TrustedPrincipals.Administrators;
var Everyone = Sid("S-1-1-0"); var AuthUsers = Sid("S-1-5-11"); var Users = Sid("S-1-5-32-545");
var CreatorOwner = Sid("S-1-3-0"); var Interactive = Sid("S-1-5-4"); var SomeUser = Sid("S-1-5-21-1000-2000-3000-1001");
var OwnerRights = Sid("S-1-3-4");
var service = ServiceSid.FromServiceName("SourceVault");
var trusted = TrustedPrincipals.CreateDefault();
var untrustedPrincipals = new[] { Everyone, AuthUsers, Users, CreatorOwner, Interactive, SomeUser, OwnerRights };
const FileSystemRights Read = (FileSystemRights)0x1200a9;       // stock "read & execute"
const FileSystemRights Modify = (FileSystemRights)0x1301bf;
var GenericAll = (FileSystemRights)0x10000000; var GenericWrite = (FileSystemRights)0x40000000;
AceInfo Allow(SecurityIdentifier s, FileSystemRights r, bool inheritOnly = false) => new(s, true, r, inheritOnly);
AceInfo Deny(SecurityIdentifier s, FileSystemRights r) => new(s, false, r, false);
DirectoryObservation Dir(SecurityIdentifier? owner, bool prot, params AceInfo[] aces) => new(DirectoryState.Directory, new(owner, true, prot, aces));
DirectoryObservation GoodRoot(params AceInfo[] extra) => Dir(SY, true, [Allow(service, FileSystemRights.FullControl), Allow(SY, FileSystemRights.FullControl), Allow(BA, FileSystemRights.FullControl), .. extra]);
DirectoryObservation GoodAncestor(params AceInfo[] extra) => Dir(BA, false, [Allow(SY, FileSystemRights.FullControl), Allow(BA, FileSystemRights.FullControl), Allow(Users, Read), .. extra]);
// root, parent, ancestor, volume root for C:\SourceVault\Data\store -> chain[0..3]
const string SyntheticRoot = @"C:\SourceVault\Data\store";
DirectoryObservation[] GoodChain() => [GoodRoot(), GoodAncestor(), GoodAncestor(), GoodAncestor()];
StorageRootValidationResult Run(DirectoryObservation[] chain, string path = SyntheticRoot, TrustedPrincipals? p = null, VolumeObservation? volume = null)
    => TrustedStorageRoot.Validate(path, p ?? trusted, new FakeInspector(path, chain, volume ?? new(DriveType.Fixed, "NTFS")));
void Expect(StorageRootValidationResult r, string code, string location) => Assert(!r.Passed && r.HasCode(code, location), $"expected {code}@{location}, got [{Codes(r)}]");

// ---- path syntax --------------------------------------------------------------------------------------------------
Test("valid synthetic path syntax accepted", () =>
{
    Assert(StorageRootPolicy.ValidatePathSyntax(SyntheticRoot).Count == 0);
    Assert(StorageRootPolicy.ValidatePathSyntax(@"D:\Vault.Data\store 1").Count == 0);
});
Test("relative, UNC, device, non-canonical and malformed paths rejected", () =>
{
    var cases = new (string? Path, string Code)[]
    {
        (null, FindingCodes.PathInvalid), ("", FindingCodes.PathInvalid), ("store", FindingCodes.PathNotAbsolute),
        (@"store\x", FindingCodes.PathNotAbsolute), (@"C:store", FindingCodes.PathNotAbsolute), (@"\store", FindingCodes.PathNotAbsolute),
        ("C:/SourceVault/store", FindingCodes.PathNotAbsolute), (@"\\server\share\store", FindingCodes.PathUncOrDevice),
        (@"\\?\C:\SourceVault\store", FindingCodes.PathUncOrDevice), (@"\\.\C:\SourceVault\store", FindingCodes.PathUncOrDevice),
        (@"\\?\UNC\server\share", FindingCodes.PathUncOrDevice), ("//server/share/x", FindingCodes.PathUncOrDevice),
        (@"C:\SourceVault\..\store", FindingCodes.PathRelativeSegment), (@"C:\SourceVault\.\store", FindingCodes.PathRelativeSegment),
        (@"C:\SourceVault\store\..", FindingCodes.PathRelativeSegment), (@"C:\SourceVault\\store", FindingCodes.PathInvalid),
        (@"C:\SourceVault\store\", FindingCodes.PathInvalid), (@"C:\SourceVault\store.", FindingCodes.PathInvalid),
        (@"C:\SourceVault\store ", FindingCodes.PathInvalid), (@"C:\SourceVault\store:stream", FindingCodes.PathInvalid),
        (@"C:\SourceVault\st*re", FindingCodes.PathInvalid), (@"C:\SourceVault\NUL", FindingCodes.PathUncOrDevice),
        (@"C:\SourceVault\com1.txt", FindingCodes.PathUncOrDevice), (@"C:\SourceVault\st" + "\0" + "ore", FindingCodes.PathInvalid),
        ("C:\\SourceVault\\" + new string('a', 1100), FindingCodes.PathInvalid),
    };
    foreach (var (p, code) in cases)
    {
        var findings = StorageRootPolicy.ValidatePathSyntax(p);
        Assert(findings.Count == 1 && findings[0].Code == code, $"case '{p?.Replace("\0", "\\0")}' expected {code}, got [{string.Join(",", findings.Select(f => f.Code))}]");
        // Public entry point must fail closed without ever touching the inspector.
        var result = TrustedStorageRoot.Validate(p, trusted, new ThrowingInspector());
        Assert(!result.Passed && result.Findings[0].Code == code);
    }
});

// ---- pure policy: root ---------------------------------------------------------------------------------------------
Test("correctly protected synthetic chain passes", () =>
{
    var r = Run(GoodChain()); Assert(r.Passed && r.Findings.Count == 0, Codes(r));
});
Test("missing, non-directory, reparse and unreadable states fail at the right level", () =>
{
    foreach (var (state, code) in new[] { (DirectoryState.Missing, FindingCodes.Missing), (DirectoryState.NotADirectory, FindingCodes.NotADirectory),
        (DirectoryState.ReparsePoint, FindingCodes.ReparsePoint), (DirectoryState.Unreadable, FindingCodes.SecurityUnreadable) })
        for (var level = 0; level < 4; level++)
        {
            var chain = GoodChain(); chain[level] = new(state, null);
            Expect(Run(chain), code, level == 0 ? "root" : "ancestor:" + level);
        }
});
Test("owner untrusted on root and ancestors, null owner", () =>
{
    foreach (var bad in untrustedPrincipals)
    {
        var chain = GoodChain(); chain[0] = Dir(bad, true, Allow(service, FileSystemRights.FullControl)); Expect(Run(chain), FindingCodes.OwnerUntrusted, "root");
        chain = GoodChain(); chain[2] = Dir(bad, false, Allow(SY, FileSystemRights.FullControl)); Expect(Run(chain), FindingCodes.OwnerUntrusted, "ancestor:2");
    }
    var nullOwner = GoodChain(); nullOwner[0] = Dir(null, true, Allow(SY, FileSystemRights.FullControl)); Expect(Run(nullOwner), FindingCodes.OwnerUntrusted, "root");
    foreach (var good in new[] { service, SY, BA, TrustedPrincipals.TrustedInstaller })
    { var chain = GoodChain(); chain[0] = Dir(good, true, Allow(SY, FileSystemRights.FullControl)); Assert(Run(chain).Passed); }
});
Test("root DACL absent (null DACL) and inherited (unprotected) fail", () =>
{
    var chain = GoodChain(); chain[0] = new(DirectoryState.Directory, new(SY, false, true, [])); Expect(Run(chain), FindingCodes.DaclMissing, "root");
    chain = GoodChain(); chain[0] = Dir(SY, false, Allow(SY, FileSystemRights.FullControl)); Expect(Run(chain), FindingCodes.DaclNotProtected, "root");
    chain = GoodChain(); chain[2] = new(DirectoryState.Directory, new(BA, false, false, [])); Expect(Run(chain), FindingCodes.DaclMissing, "ancestor:2");
});
Test("write-type ACE for any untrusted principal on root fails (every write bit)", () =>
{
    var writeBits = new[] { FileSystemRights.WriteData, FileSystemRights.AppendData, FileSystemRights.WriteExtendedAttributes,
        FileSystemRights.DeleteSubdirectoriesAndFiles, FileSystemRights.WriteAttributes, FileSystemRights.Delete,
        FileSystemRights.ChangePermissions, FileSystemRights.TakeOwnership, FileSystemRights.Write, FileSystemRights.Modify,
        FileSystemRights.FullControl, GenericAll, GenericWrite, Modify };
    foreach (var who in untrustedPrincipals)
        foreach (var bit in writeBits)
        {
            Expect(Run([GoodRoot(Allow(who, bit)), GoodAncestor(), GoodAncestor(), GoodAncestor()]), FindingCodes.UntrustedWriteAccess, "root");
            // Inherit-only ACEs on the root govern children created later, so they count too.
            Expect(Run([GoodRoot(Allow(who, bit, inheritOnly: true)), GoodAncestor(), GoodAncestor(), GoodAncestor()]), FindingCodes.UntrustedWriteAccess, "root");
        }
});
Test("read-only untrusted ACE on root passes with warning; deny ACEs and trusted writes are fine", () =>
{
    var r = Run([GoodRoot(Allow(Users, Read)), GoodAncestor(), GoodAncestor(), GoodAncestor()]);
    Assert(r.Passed && r.HasCode(FindingCodes.UntrustedReadAccess, "root") && r.Findings.Single().Severity == FindingSeverity.Warning, Codes(r));
    r = Run([GoodRoot(Deny(Everyone, FileSystemRights.FullControl), Deny(Users, Modify)), GoodAncestor(), GoodAncestor(), GoodAncestor()]);
    Assert(r.Passed && r.Findings.Count == 0, Codes(r));
});
Test("combined weak rights of one untrusted principal reported once", () =>
{
    var r = Run([GoodRoot(Allow(Users, FileSystemRights.ReadData), Allow(Users, FileSystemRights.WriteData)), GoodAncestor(), GoodAncestor(), GoodAncestor()]);
    Assert(r.Findings.Count == 1 && r.Findings[0].Subject == Users.Value);
});
Test("root that is a volume root is rejected", () =>
{
    var r = Run([GoodRoot()], @"D:\"); Expect(r, FindingCodes.RootIsVolumeRoot, "root");
});

// ---- pure policy: ancestors ----------------------------------------------------------------------------------------
Test("parent: create/append/delete-child/delete/change-permissions/take-ownership by untrusted fail", () =>
{
    foreach (var who in untrustedPrincipals)
        foreach (var bit in new[] { FileSystemRights.CreateFiles, FileSystemRights.CreateDirectories, FileSystemRights.DeleteSubdirectoriesAndFiles,
            FileSystemRights.Delete, FileSystemRights.ChangePermissions, FileSystemRights.TakeOwnership, FileSystemRights.Write, FileSystemRights.Modify,
            FileSystemRights.FullControl, GenericAll })
            Expect(Run([GoodRoot(), GoodAncestor(Allow(who, bit)), GoodAncestor(), GoodAncestor()]), FindingCodes.UntrustedWriteAccess, "ancestor:1");
});
Test("parent: read/list/traverse by untrusted and deny ACEs are fine", () =>
{
    var r = Run([GoodRoot(), GoodAncestor(Allow(Everyone, Read), Allow(AuthUsers, FileSystemRights.ListDirectory | FileSystemRights.Traverse),
        Deny(Users, FileSystemRights.FullControl)), GoodAncestor(), GoodAncestor()]);
    Assert(r.Passed && r.Findings.Count == 0, Codes(r));
});
Test("higher ancestors: delete-child/delete/change-permissions/take-ownership by untrusted fail", () =>
{
    foreach (var level in new[] { 2, 3 })
        foreach (var bit in new[] { FileSystemRights.DeleteSubdirectoriesAndFiles, FileSystemRights.Delete, FileSystemRights.ChangePermissions,
            FileSystemRights.TakeOwnership, FileSystemRights.Modify, FileSystemRights.FullControl, GenericAll })
        {
            var chain = GoodChain(); chain[level] = GoodAncestor(Allow(AuthUsers, bit));
            Expect(Run(chain), FindingCodes.UntrustedWriteAccess, "ancestor:" + level);
        }
});
Test("volume-root policy: AppendData/CreateFiles on a HIGHER ancestor (stock C:\\) tolerated, but not on the parent", () =>
{
    // Authenticated Users AppendData on the volume root, root in a dedicated directory two levels down: passes.
    var chain = GoodChain(); chain[3] = GoodAncestor(Allow(AuthUsers, FileSystemRights.CreateDirectories), Allow(Users, FileSystemRights.CreateFiles));
    var r = Run(chain); Assert(r.Passed && r.Findings.Count == 0, Codes(r));
    // Same ACL when the volume root IS the parent (root = C:\store): fails as an ordinary parent finding.
    var direct = Run([GoodRoot(), GoodAncestor(Allow(AuthUsers, FileSystemRights.CreateDirectories))], @"C:\store");
    Expect(direct, FindingCodes.UntrustedWriteAccess, "ancestor:1");
});
Test("inherit-only ACEs on ancestors do not apply to the ancestor itself", () =>
{
    var chain = GoodChain(); chain[1] = GoodAncestor(Allow(AuthUsers, FileSystemRights.Modify, inheritOnly: true), Allow(CreatorOwner, FileSystemRights.FullControl, inheritOnly: true));
    var r = Run(chain); Assert(r.Passed && r.Findings.Count == 0, Codes(r));
});
Test("stock Windows 11 ACLs for C:\\ and C:\\ProgramData accept a protected store below ProgramData", () =>
{
    // Copied (as plain strings) from a stock machine: C:\ owned by TrustedInstaller; ProgramData grants Users create/append.
    const string trustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    const string rootSddl = "O:" + trustedInstaller + "G:" + trustedInstaller + "D:PAI(A;;LC;;;AU)(A;OICIIO;SDGXGWGR;;;AU)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";
    const string programData = "O:SYG:SYD:PAI(A;OICIIO;GA;;;CO)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;CI;DCLCRPCR;;;BU)";
    DirectoryObservation FromSddl(string sddl) { var s = new DirectorySecurity(); s.SetSecurityDescriptorSddlForm(sddl); return new(DirectoryState.Directory, DirectorySecurityInfo.FromSecurity(s)); }
    var chain = new[] { GoodRoot(), GoodAncestor(), FromSddl(programData), FromSddl(rootSddl) };
    var r = Run(chain, @"C:\ProgramData\SourceVault\store"); Assert(r.Passed && r.Findings.Count == 0, Codes(r));
    // The same stock ACLs reject a store directly in C:\ProgramData (Users can create/append there) ...
    var direct = Run([GoodRoot(), FromSddl(programData), FromSddl(rootSddl)], @"C:\ProgramData\store");
    Expect(direct, FindingCodes.UntrustedWriteAccess, "ancestor:1");
    // ... and a store whose chain does not trust TrustedInstaller flags the volume-root owner.
    var strict = Run(chain, @"C:\ProgramData\SourceVault\store", new TrustedPrincipals([service, SY, BA]));
    Expect(strict, FindingCodes.OwnerUntrusted, "ancestor:3");
});
Test("volume checks: non-fixed and non-NTFS/ReFS volumes rejected", () =>
{
    Expect(Run(GoodChain(), volume: new(DriveType.Network, "NTFS")), FindingCodes.VolumeNotFixed, "volume");
    Expect(Run(GoodChain(), volume: new(DriveType.Removable, "NTFS")), FindingCodes.VolumeNotFixed, "volume");
    Expect(Run(GoodChain(), volume: new(DriveType.Fixed, "FAT32")), FindingCodes.VolumeFilesystemUnsupported, "volume");
    Assert(Run(GoodChain(), volume: new(DriveType.Fixed, "ReFS")).Passed);
});
Test("unexpected failures become an opaque validation_error", () =>
{
    var r = TrustedStorageRoot.Validate(SyntheticRoot, trusted, new ThrowingInspector());
    Assert(!r.Passed && r.Findings.Count == 1 && r.Findings[0].Code == FindingCodes.ValidationError && r.Findings[0].Subject is null);
});

// ---- conversion from real FileSystemSecurity objects (in memory) ----------------------------------------------------
Test("FileSystemSecurity conversion: owner, protection, inherit-only, deny, null DACL", () =>
{
    var s = new DirectorySecurity();
    s.SetOwner(SY);
    s.SetAccessRuleProtection(true, false);
    s.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.WriteData, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
    s.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify, AccessControlType.Deny));
    var info = DirectorySecurityInfo.FromSecurity(s);
    Assert(info.Owner == SY && info.DaclPresent && info.DaclProtected, "owner/present/protected " + info.Owner + info.DaclPresent + info.DaclProtected);
    Assert(info.Aces.Any(a => a.Sid == Everyone && a.IsAllow && a.InheritOnly && (a.Rights & 0x2) != 0));
    Assert(info.Aces.Any(a => a.Sid == Users && !a.IsAllow && !a.InheritOnly));
    var unprotected = new DirectorySecurity(); unprotected.SetOwner(SY); unprotected.SetAccessRuleProtection(false, false);
    Assert(!DirectorySecurityInfo.FromSecurity(unprotected).DaclProtected, "unprotected");
    // A null DACL (everyone has full control). .NET may surface it either as DaclPresent=false or as an explicit
    // Everyone allow-all ACE; either way the result must never pass.
    var rawNull = new RawSecurityDescriptor(ControlFlags.SelfRelative, BA, null, null, null);
    var nullBytes = new byte[rawNull.BinaryLength]; rawNull.GetBinaryForm(nullBytes, 0);
    var nullDacl = new DirectorySecurity(); nullDacl.SetSecurityDescriptorBinaryForm(nullBytes, AccessControlSections.Owner | AccessControlSections.Access);
    var nullInfo = DirectorySecurityInfo.FromSecurity(nullDacl);
    var nullChain = GoodChain(); nullChain[0] = new(DirectoryState.Directory, nullInfo);
    Assert(!Run(nullChain).Passed, "null DACL must fail; DaclPresent=" + nullInfo.DaclPresent + " aces=" + nullInfo.Aces.Count);
    var chain = GoodChain(); chain[0] = new(DirectoryState.Directory, info); Expect(Run(chain), FindingCodes.UntrustedWriteAccess, "root");
});

// ---- principals and expected ACL ---------------------------------------------------------------------------------
Test("service SID derivation matches the well-known TrustedInstaller SID", () =>
{
    Assert(ServiceSid.FromServiceName("TrustedInstaller") == TrustedPrincipals.TrustedInstaller);
    Assert(ServiceSid.FromServiceName("trustedinstaller") == TrustedPrincipals.TrustedInstaller);
    Assert(ServiceSid.FromServiceName("SourceVault") != ServiceSid.FromServiceName("SourceVault2"));
});
Test("service SID derivation matches the OS translation of NT SERVICE\\EventLog", () =>
{
    SecurityIdentifier? os;
    try { os = (SecurityIdentifier)new NTAccount(@"NT SERVICE\EventLog").Translate(typeof(SecurityIdentifier)); }
    catch (Exception e) when (e is IdentityNotMappedException or SystemException) { throw new SkipException("account lookup unavailable (" + e.GetType().Name + ")"); }
    Assert(ServiceSid.FromServiceName("EventLog") == os);
});
Test("principal sets validate their inputs", () =>
{
    Assert(trusted.IsTrusted(service) && trusted.IsTrusted(SY) && trusted.IsTrusted(BA) && trusted.IsTrusted(TrustedPrincipals.TrustedInstaller));
    Assert(!trusted.IsTrusted(Everyone) && !trusted.IsTrusted(AuthUsers) && !trusted.IsTrusted(Users));
    Assert(trusted.AclGrantees.Count == 3 && !trusted.AclGrantees.Contains(TrustedPrincipals.TrustedInstaller));
    try { _ = new TrustedPrincipals([]); Assert(false); } catch (ArgumentException) { }
    try { _ = new TrustedPrincipals([SY], [BA]); Assert(false); } catch (ArgumentException) { }
});
Test("expected ACL description and SDDL are self-consistent", () =>
{
    var text = TrustedStorageRoot.DescribeExpectedAcl(trusted);
    Assert(text.Contains(service.Value) && text.Contains("S-1-5-18") && text.Contains("S-1-5-32-544") && text.Contains("protected"));
    var sddl = TrustedStorageRoot.ExpectedRootSddl(trusted);
    Assert(sddl == $"D:P(A;OICI;FA;;;{service.Value})(A;OICI;FA;;;S-1-5-18)(A;OICI;FA;;;S-1-5-32-544)", sddl);
    var s = new DirectorySecurity(); s.SetOwner(BA); s.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
    var info = DirectorySecurityInfo.FromSecurity(s);
    Assert(info.DaclProtected && info.Aces.Count == 3);
    var chain = GoodChain(); chain[0] = new(DirectoryState.Directory, info);
    var r = Run(chain); Assert(r.Passed && r.Findings.Count == 0, Codes(r));
});

// ---- real filesystem under the workspace ---------------------------------------------------------------------------
var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException();
var userTrusted = new TrustedPrincipals([user, SY, BA]);
string NewDir(string name) { var p = Path.Combine(workspace, name); Directory.CreateDirectory(p); return p; }
void Lock(string dir, params FileSystemAccessRule[] extra)
{
    // Only ever called on directories created by this program under the workspace; the current user keeps FullControl so cleanup works.
    var di = new DirectoryInfo(dir);
    var sec = di.GetAccessControl();
    sec.SetAccessRuleProtection(true, false);
    foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, false, typeof(SecurityIdentifier))) sec.RemoveAccessRuleSpecific(rule);
    sec.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
    foreach (var rule in extra) sec.AddAccessRule(rule);
    di.SetAccessControl(sec);
}
FileSystemAccessRule WriteRule(SecurityIdentifier who) => new(who, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);
// Real inspector for the test-created root and parent only; ancestors outside the workspace are never judged here.
StorageRootValidationResult RunReal(string root, string parent, TrustedPrincipals p) => TrustedStorageRoot.Validate(root, p, new HybridInspector(root, parent, GoodAncestor()));

Test("real filesystem: protected test directory passes with current user as trusted service principal", () =>
{
    var parent = NewDir("good-parent"); var root = Path.Combine(parent, "store"); Directory.CreateDirectory(root);
    Lock(parent); Lock(root);
    var r = RunReal(root, parent, userTrusted); Assert(r.Passed, Codes(r));
    // Informational only: the real, unmodified ancestors above the workspace are environment-specific.
    var full = TrustedStorageRoot.Validate(root, new TrustedPrincipals([user, SY, BA, TrustedPrincipals.TrustedInstaller]));
    Console.WriteLine("INFO full real-chain result for workspace root (environment-specific, not asserted): " + (full.Passed ? "pass" : "fail") + " [" + Codes(full) + "]");
});
Test("real filesystem: SDDL from ExpectedRootSddl applied to a test directory passes", () =>
{
    var parent = NewDir("sddl-parent"); var root = Path.Combine(parent, "store"); Directory.CreateDirectory(root);
    Lock(parent);
    var p = new TrustedPrincipals([user, SY, BA], [user, SY, BA]);
    var di = new DirectoryInfo(root); var sec = di.GetAccessControl();
    sec.SetSecurityDescriptorSddlForm(TrustedStorageRoot.ExpectedRootSddl(p), AccessControlSections.Access); di.SetAccessControl(sec);
    var r = RunReal(root, parent, p); Assert(r.Passed, Codes(r));
});
Test("real filesystem: inherited (unprotected) DACL on root fails", () =>
{
    var parent = NewDir("inh-parent"); var root = Path.Combine(parent, "store"); Directory.CreateDirectory(root);
    Lock(parent);
    var r = RunReal(root, parent, userTrusted); Expect(r, FindingCodes.DaclNotProtected, "root");
});
Test("real filesystem: Everyone / Users write ACE on root fails", () =>
{
    foreach (var who in new[] { Everyone, Users })
    {
        var parent = NewDir("ew-parent-" + who.Value); var root = Path.Combine(parent, "store"); Directory.CreateDirectory(root);
        Lock(parent); Lock(root, WriteRule(who));
        Expect(RunReal(root, parent, userTrusted), FindingCodes.UntrustedWriteAccess, "root");
    }
});
Test("real filesystem: untrusted write on parent fails", () =>
{
    var parent = NewDir("pw-parent"); var root = Path.Combine(parent, "store"); Directory.CreateDirectory(root);
    Lock(parent, WriteRule(Users)); Lock(root);
    Expect(RunReal(root, parent, userTrusted), FindingCodes.UntrustedWriteAccess, "ancestor:1");
});
Test("real filesystem: untrusted owner fails", () =>
{
    var parent = NewDir("ow-parent"); var root = Path.Combine(parent, "store"); Directory.CreateDirectory(root);
    Lock(parent); Lock(root);
    // The creating user (or Administrators, when elevated) owns the directory; trust only SYSTEM plus a synthetic SID.
    var r = RunReal(root, parent, new TrustedPrincipals([SY, SomeUser]));
    Expect(r, FindingCodes.OwnerUntrusted, "root");
});
Test("real filesystem: missing root and file-as-root fail", () =>
{
    var parent = NewDir("miss-parent"); Lock(parent);
    var missing = Path.Combine(parent, "absent");
    Expect(RunReal(missing, parent, userTrusted), FindingCodes.Missing, "root");
    var file = Path.Combine(parent, "file"); File.WriteAllText(file, "synthetic");
    Expect(RunReal(file, parent, userTrusted), FindingCodes.NotADirectory, "root");
});
Test("real filesystem: junction as root fails; junction as ancestor fails", () =>
{
    var target = NewDir("junc-target"); Directory.CreateDirectory(Path.Combine(target, "inner"));
    var link = Path.Combine(workspace, "junc-link");
    if (!TryMakeJunction(link, target)) throw new SkipException("junction creation unavailable");
    try
    {
        var asRoot = TrustedStorageRoot.Validate(link, userTrusted); Expect(asRoot, FindingCodes.ReparsePoint, "root");
        var asAncestor = TrustedStorageRoot.Validate(Path.Combine(link, "inner"), userTrusted); Expect(asAncestor, FindingCodes.ReparsePoint, "ancestor:1");
        var deeper = Path.Combine(target, "inner", "deep"); Directory.CreateDirectory(deeper);
        Expect(TrustedStorageRoot.Validate(Path.Combine(link, "inner", "deep"), userTrusted), FindingCodes.ReparsePoint, "ancestor:2");
    }
    finally { try { Directory.Delete(link); } catch (IOException) { } }
});
Test("real filesystem: directory symlink as root fails; as ancestor fails", () =>
{
    var target = NewDir("sym-target"); Directory.CreateDirectory(Path.Combine(target, "inner"));
    var link = Path.Combine(workspace, "sym-link");
    try { Directory.CreateSymbolicLink(link, target); }
    catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
    { throw new SkipException("symlink creation unavailable (" + e.GetType().Name + ")"); }
    try
    {
        Expect(TrustedStorageRoot.Validate(link, userTrusted), FindingCodes.ReparsePoint, "root");
        Expect(TrustedStorageRoot.Validate(Path.Combine(link, "inner"), userTrusted), FindingCodes.ReparsePoint, "ancestor:1");
    }
    finally { try { Directory.Delete(link); } catch (IOException) { } }
});
Test("real filesystem: findings do not leak workspace paths", () =>
{
    var r = TrustedStorageRoot.Validate(Path.Combine(workspace, "no-such-parent", "store"), userTrusted);
    Assert(!r.Passed);
    foreach (var f in r.Findings) Assert(!(f.Location + f.Code + f.Subject).Contains(workspace, StringComparison.OrdinalIgnoreCase));
});

Console.WriteLine($"Windows storage-root validation: {passed} PASS, {skipped} SKIP, {failed} FAIL; synthetic workspace retained at {workspace}");
return failed == 0 ? 0 : 1;

static bool TryMakeJunction(string link, string target)
{
    try
    {
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15000)) { process.Kill(); return false; }
        return process.ExitCode == 0 && Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
    }
    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return false; }
}

sealed class SkipException(string reason) : Exception(reason);

sealed class ThrowingInspector : IStorageRootInspector
{
    public VolumeObservation ObserveVolume(string path) => throw new InvalidOperationException("secret-detail " + path);
    public DirectoryObservation ObserveDirectory(string path) => throw new InvalidOperationException("secret-detail " + path);
}

/// <summary>Maps each path in the chain of <c>root</c> to a supplied synthetic observation (index 0 = root).</summary>
sealed class FakeInspector : IStorageRootInspector
{
    private readonly Dictionary<string, DirectoryObservation> map = new(StringComparer.OrdinalIgnoreCase);
    private readonly VolumeObservation volume;
    public FakeInspector(string root, DirectoryObservation[] chain, VolumeObservation volume)
    {
        this.volume = volume;
        var i = 0;
        for (var p = root; p is not null; p = Path.GetDirectoryName(p), i++)
            map[p] = i < chain.Length ? chain[i] : throw new InvalidOperationException("Synthetic chain too short.");
        if (i != chain.Length) throw new InvalidOperationException("Synthetic chain length mismatch.");
    }
    public VolumeObservation ObserveVolume(string path) => volume;
    public DirectoryObservation ObserveDirectory(string path) => map[path];
}

/// <summary>Real inspection for the test-created root and parent; a fixed synthetic observation for everything above them.</summary>
sealed class HybridInspector(string root, string parent, DirectoryObservation above) : IStorageRootInspector
{
    private readonly FileSystemStorageRootInspector real = new();
    public VolumeObservation ObserveVolume(string path) => real.ObserveVolume(path);
    public DirectoryObservation ObserveDirectory(string path) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) || string.Equals(path, parent, StringComparison.OrdinalIgnoreCase) ? real.ObserveDirectory(path) : above;
}
