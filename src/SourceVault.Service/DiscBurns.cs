using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using SourceVault.Packaging;
using SourceVault.Storage;

namespace SourceVault.Service;

/// <summary>A disc in a drive. <see cref="Type"/> is IMAPI's media type name, e.g. "CD-R", "DVD+R", "BD-R", "DVD+RW".</summary>
internal sealed record DiscMedia(string Type, bool WriteOnce, bool Blank, long FreeBytes);

/// <summary>A disc drive that can write. <see cref="Media"/> is null when it holds no disc.</summary>
internal sealed record DiscDrive(string DriveId, string Name, DiscMedia? Media);

/// <summary>One file in the disc image: its path on the disc ('/'-separated), length, and a reader of its plaintext.</summary>
internal sealed record DiscImageFile(string Path, long Length, Func<IEnumerable<ArraySegment<byte>>> Read);

/// <summary>The disc writer failed with a fixed protocol code (NoMedia, MediaNotWriteOnce, MediaNotBlank, MediaTooSmall, BurnFailed, DiscUnavailable).</summary>
internal sealed class DiscWriteException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

/// <summary>Writes one finalized disc. Production: <see cref="ImapiDiscWriter"/> (Windows' IMAPI2). Tests: a fake.</summary>
internal interface IDiscWriter
{
    IReadOnlyList<DiscDrive> Drives();
    /// <summary>Writes the files as the disc's only session and closes the disc (nothing can be added afterwards).</summary>
    void WriteAndClose(string driveId, string volumeLabel, IReadOnlyList<DiscImageFile> files, Action<string, int> progress, CancellationToken ct);
    /// <summary>The written disc's root folder once Windows has mounted it (for the read-back check), or null.</summary>
    string? MountedRoot(string driveId);
}

/// <summary>The disc signing key: a sign-only TPM key (production) or a software key (tests only).</summary>
internal interface IDiscSigner
{
    byte[] SubjectPublicKeyInfo();
    byte[] SignHash(byte[] hash);
}

/// <summary>Windows accounts that aren't on the pipe: an approver typed by name (production: <see cref="AuthzAccounts"/>).</summary>
internal interface IAccountDirectory
{
    SecurityIdentifier? Resolve(string account);
    string AccountName(SecurityIdentifier user);
    /// <summary>Whether <paramref name="user"/> is in <paramref name="group"/>, nested groups included; null when that can't be worked out.</summary>
    bool? IsMember(SecurityIdentifier user, SecurityIdentifier group);
}

/// <summary>A refusal with a fixed protocol code (docs/disc-export-design.md, section 7).</summary>
internal sealed class DiscBurnException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

/// <summary>
/// Writing a project to a finalized disc, with two approvers (owner request and decisions E1-E4, 2026-11-10;
/// docs/disc-export-design.md). The burner must be in SourceVault Disc Burners (from the pipe's token). Two different
/// approvers, neither the burner, each sign in with their account and SourceVault password, and must be in SourceVault
/// Disc Approvers and SourceVault Operators. The disc gets the project's saved snapshot as plain files plus a record
/// signed by the disc signing key, and is closed. Every attempt is recorded in the encrypted exports store.
/// One burn at a time per PC.
/// </summary>
internal sealed class DiscBurns(ProjectCatalog catalog, IDiscWriter writer, IDiscSigner signer, IAccountDirectory directory, OperatorPasswords passwords,
    SecurityIdentifier operators, SecurityIdentifier? approvers, string computer, TimeProvider? clock = null)
{
    internal static readonly TimeSpan BurnLifetime = TimeSpan.FromMinutes(30), ApprovalLifetime = TimeSpan.FromMinutes(15);
    internal const string ApproversGroupName = "SourceVault Disc Approvers", BurnersGroupName = "SourceVault Disc Burners";
    private const long ImageOverheadBytes = 4 << 20; // file system structures, the record and its companions
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly object logGate = new();
    private Burn? current;

    private sealed class Burn
    {
        internal required Guid BurnId, ProjectId, SnapshotId;
        internal required string ProjectName, DriveId, DriveName, MediaType;
        internal required SecurityIdentifier Burner;
        internal required string BurnerAccount;
        internal required IReadOnlyList<StoreFile> Files;
        internal required long TotalBytes;
        internal required long Created;
        internal readonly List<(SecurityIdentifier Sid, string Account, DateTimeOffset At, long Stamp)> Approvals = [];
        internal string State = "waiting"; // waiting, writing, closing, verifying, done, failed, cancelled
        internal int Progress;
        internal string? Reason;
        internal Guid? DiscId;
        internal bool Verified;
        internal bool Finished => State is "done" or "failed" or "cancelled";
    }

    /// <summary>The disc signing key's check code (six groups of four), which administrators publish to whoever checks discs.</summary>
    internal string CheckCode => PublicKeyFile.Display(SHA256.HashData(signer.SubjectPublicKeyInfo()));

    internal IReadOnlyList<DiscDrive> Drives()
    {
        try { return writer.Drives(); }
        catch (DiscWriteException e) { throw new DiscBurnException(e.Code); }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new DiscBurnException("DiscUnavailable"); } // no IMAPI, or it failed
    }

    /// <summary>Prepares a burn of the project's current saved snapshot on a blank write-once disc. Nothing is written yet.</summary>
    internal (Guid BurnId, int FileCount, long TotalBytes) Begin(SecurityIdentifier burner, bool isBurner, Guid projectId, string projectName, string driveId)
    {
        if (!isBurner) throw new DiscBurnException("NotDiscBurner");
        lock (gate)
        {
            if (current is { Finished: false } busy && !Expired(busy)) throw new DiscBurnException("DiscBusy");
            var drive = Drives().FirstOrDefault(d => d.DriveId == driveId) ?? throw new DiscBurnException("NoMedia");
            var media = drive.Media ?? throw new DiscBurnException("NoMedia");
            if (!media.WriteOnce) throw new DiscBurnException("MediaNotWriteOnce");
            if (!media.Blank) throw new DiscBurnException("MediaNotBlank");
            var snapshot = catalog.OpenProject(projectId).List();
            if (snapshot.Files.Count > DiscRecords.MaxFiles) throw new DiscBurnException("LimitExceeded");
            long total = snapshot.Files.Sum(f => f.ByteLength);
            long onDisc = snapshot.Files.Sum(f => (f.ByteLength + 2047) / 2048 * 2048) + ImageOverheadBytes;
            if (onDisc > media.FreeBytes) throw new DiscBurnException("MediaTooSmall");
            current = new Burn
            {
                BurnId = Guid.NewGuid(), ProjectId = projectId, SnapshotId = snapshot.SnapshotId, ProjectName = projectName,
                DriveId = drive.DriveId, DriveName = drive.Name, MediaType = media.Type, Burner = burner, BurnerAccount = directory.AccountName(burner),
                Files = snapshot.Files, TotalBytes = total, Created = time.GetTimestamp(),
            };
            return (current.BurnId, snapshot.Files.Count, total);
        }
    }

    /// <summary>
    /// One approver signs in: their SourceVault password first (wrong tries count toward their lockout), then their
    /// groups. Returns the approver's account name and the number of approvals so far.
    /// </summary>
    internal (string Approver, int Approvals) Approve(SecurityIdentifier caller, Guid burnId, string account, string password)
    {
        lock (gate)
        {
            var burn = Owned(caller, burnId);
            if (burn.State != "waiting") throw new DiscBurnException("InvalidRequest");
            if (burn.Approvals.Count >= 2) throw new DiscBurnException("AlreadyApproved");
            var sid = directory.Resolve(account) ?? throw new DiscBurnException("NotDiscApprover");
            if (sid.Equals(burn.Burner)) throw new DiscBurnException("SameAsBurner");
            if (burn.Approvals.Any(a => a.Sid.Equals(sid))) throw new DiscBurnException("AlreadyApproved");
            var code = passwords.VerifyOther(sid, password);
            if (code != "Ok") throw new DiscBurnException(code); // WrongPassword, PasswordLocked, PasswordNotSet
            if (approvers is null || directory.IsMember(sid, approvers) != true || directory.IsMember(sid, operators) != true) throw new DiscBurnException("NotDiscApprover");
            var name = directory.AccountName(sid);
            burn.Approvals.Add((sid, name, time.GetUtcNow(), time.GetTimestamp()));
            return (name, burn.Approvals.Count);
        }
    }

    /// <summary>Starts writing: two fresh approvals, the burner still a burner, and the project unchanged since Begin.</summary>
    internal void Start(SecurityIdentifier caller, bool isBurner, Guid burnId)
    {
        Burn burn;
        lock (gate)
        {
            burn = Owned(caller, burnId);
            if (!isBurner) throw new DiscBurnException("NotDiscBurner");
            if (burn.State != "waiting") throw new DiscBurnException("InvalidRequest");
            if (burn.Approvals.Count < 2) throw new DiscBurnException("ApprovalsMissing");
            long now = time.GetTimestamp();
            if (burn.Approvals.Any(a => time.GetElapsedTime(a.Stamp, now) >= ApprovalLifetime)) { burn.Approvals.Clear(); throw new DiscBurnException("ApprovalExpired"); }
            if (catalog.OpenProject(burn.ProjectId).List().SnapshotId != burn.SnapshotId) { burn.State = "failed"; burn.Reason = "ProjectChanged"; Log(burn, null); throw new DiscBurnException("ProjectChanged"); }
            burn.State = "writing";
        }
        _ = Task.Run(() => Run(burn));
    }

    internal void Cancel(SecurityIdentifier caller, Guid burnId)
    {
        lock (gate)
        {
            var burn = Owned(caller, burnId);
            if (burn.State != "waiting") throw new DiscBurnException("InvalidRequest"); // a write that has started can't be stopped safely
            burn.State = "cancelled";
        }
    }

    internal (string State, int Progress, string? Reason, Guid? DiscId, bool Verified, int Approvals) Status(SecurityIdentifier caller, Guid burnId)
    {
        lock (gate)
        {
            var burn = Owned(caller, burnId);
            return (burn.State, burn.Progress, burn.Reason, burn.DiscId, burn.Verified, burn.Approvals.Count);
        }
    }

    private Burn Owned(SecurityIdentifier caller, Guid burnId)
    {
        var burn = current;
        if (burn is null || burn.BurnId != burnId || !burn.Burner.Equals(caller)) throw new KeyNotFoundException();
        if (!burn.Finished && burn.State == "waiting" && Expired(burn)) { burn.State = "failed"; burn.Reason = "ApprovalExpired"; }
        return burn;
    }

    private bool Expired(Burn burn) => burn.State == "waiting" && time.GetElapsedTime(burn.Created, time.GetTimestamp()) >= BurnLifetime;

    // The write itself, off the request thread. Plaintext streams from the snapshot into the disc writer; the record is
    // built from the manifest's hashes (and any legacy file without one is hashed first), signed, and written with it.
    private void Run(Burn burn)
    {
        DiscRecord? record = null;
        try
        {
            var store = catalog.OpenProject(burn.ProjectId);
            using var reader = store.OpenSnapshot(burn.SnapshotId);
            var created = time.GetUtcNow();
            var files = reader.Files.Select(f => new DiscRecordFile(f.LogicalName, f.ByteLength, f.Sha256 ?? HashOf(reader, f))).ToList();
            record = new DiscRecord(Guid.NewGuid(), created, computer, SHA256.HashData(signer.SubjectPublicKeyInfo()), burn.ProjectName, burn.SnapshotId,
                new DiscPerson(burn.BurnerAccount, burn.Burner.Value), burn.Approvals.Select(a => new DiscPerson(a.Account, a.Sid.Value, a.At)).ToList(), files);
            var json = DiscRecords.ToJson(record);
            var signature = DiscRecords.Sign(json, signer.SignHash);
            var image = reader.Files.Select(f => new DiscImageFile($"{DiscRecords.SourceFolder}/{f.LogicalName}", f.ByteLength, () => reader.Read(f))).ToList();
            image.Add(Fixed(DiscRecords.RecordFile, json));
            image.Add(Fixed(DiscRecords.SignatureFile, signature));
            image.Add(Fixed(DiscRecords.KeyFile, signer.SubjectPublicKeyInfo()));
            image.Add(Fixed(DiscRecords.ReadmeFile, System.Text.Encoding.UTF8.GetBytes(DiscRecords.Readme(record))));
            lock (gate) burn.DiscId = record.DiscId;
            writer.WriteAndClose(burn.DriveId, DiscRecords.VolumeLabel(created, record.Approvers.Select(a => a.Account).ToList()), image,
                (stage, percent) => { lock (gate) { burn.State = stage; burn.Progress = Math.Clamp(percent, 0, 100); } }, CancellationToken.None);
            lock (gate) { burn.State = "verifying"; burn.Progress = 0; }
            bool verified = ReadBack(burn.DriveId, record);
            lock (gate) { burn.Verified = verified; burn.State = "done"; burn.Progress = 100; if (!verified) burn.Reason = "VerifyFailed"; }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            lock (gate) { burn.State = "failed"; burn.Reason = e is DiscWriteException w ? w.Code : e is RevisionConflictException ? "ProjectChanged" : "BurnFailed"; }
        }
        finally { Log(burn, record); }
    }

    private static DiscImageFile Fixed(string path, byte[] content) => new(path, content.Length, () => [new ArraySegment<byte>(content)]);

    private static byte[] HashOf(RevisionStore.SnapshotReader reader, StoreFile file)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var segment in reader.Read(file)) hash.AppendData(segment);
        return hash.GetHashAndReset();
    }

    // Reads the closed disc back through Windows (it may take a while to mount) and checks it like verify-disc does.
    private bool ReadBack(string driveId, DiscRecord record)
    {
        for (int i = 0; i < 60; i++)
        {
            var root = writer.MountedRoot(driveId);
            if (root is not null)
            {
                try { return DiscRecords.Verify(root, CheckCode).Record.DiscId == record.DiscId; }
                catch (Exception e) when (e is DiscRecordException or IOException or UnauthorizedAccessException) { return false; }
            }
            Thread.Sleep(1000);
        }
        return false;
    }

    // Every attempt that reached the writer or was refused at Start is recorded, encrypted, with who and what.
    private void Log(Burn burn, DiscRecord? record)
    {
        lock (logGate)
        {
            try
            {
                var entries = new List<JsonElement>();
                var existing = catalog.ReadExports();
                if (existing is not null)
                    try
                    {
                        using var doc = JsonDocument.Parse(existing);
                        entries.AddRange(doc.RootElement.GetProperty("burns").EnumerateArray().Select(e => e.Clone()));
                    }
                    finally { CryptographicOperations.ZeroMemory(existing); }
                using var buffer = new MemoryStream();
                using (var w = new Utf8JsonWriter(buffer))
                {
                    w.WriteStartObject();
                    w.WriteNumber("version", 1);
                    w.WriteStartArray("burns");
                    foreach (var e in entries) e.WriteTo(w);
                    w.WriteStartObject();
                    w.WriteString("burnId", burn.BurnId.ToString("D"));
                    if (burn.DiscId is { } disc) w.WriteString("discId", disc.ToString("D"));
                    w.WriteString("projectId", burn.ProjectId.ToString("D"));
                    w.WriteString("projectName", burn.ProjectName);
                    w.WriteString("snapshotId", burn.SnapshotId.ToString("D"));
                    w.WriteString("burner", burn.BurnerAccount);
                    w.WriteStartArray("approvers"); foreach (var a in burn.Approvals) w.WriteStringValue(a.Account); w.WriteEndArray();
                    w.WriteString("drive", burn.DriveName);
                    w.WriteString("media", burn.MediaType);
                    w.WriteNumber("files", burn.Files.Count);
                    w.WriteNumber("bytes", burn.TotalBytes);
                    w.WriteString("finishedUtc", time.GetUtcNow().UtcDateTime.ToString("O"));
                    if (record is not null) w.WriteString("recordSha256", Convert.ToHexStringLower(SHA256.HashData(DiscRecords.ToJson(record))));
                    w.WriteString("result", burn.State);
                    if (burn.Reason is not null) w.WriteString("reason", burn.Reason);
                    w.WriteBoolean("verified", burn.Verified);
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                var bytes = buffer.ToArray();
                try { catalog.WriteExports(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            catch (Exception e) when (e is not OutOfMemoryException) { /* the burn's own result stands; -Status reports a damaged log */ }
        }
    }
}

/// <summary>The production disc signer: the service's sign-only TPM key.</summary>
internal sealed class TpmDiscSigner(SourceVault.Windows.TpmSigningKey key) : IDiscSigner
{
    public byte[] SubjectPublicKeyInfo() => key.ExportSubjectPublicKeyInfo();
    public byte[] SignHash(byte[] hash) => key.SignHash(hash);
}
