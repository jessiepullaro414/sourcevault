using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SourceVault.Revisions.Tests")]
namespace SourceVault.Storage;

/// <summary>How the store classifies a revision's bytes (docs/vault-any-file-design.md section 2.1).</summary>
public enum FileKind : byte { Text = 1, Binary = 2 }

/// <summary>
/// One file of a snapshot. <see cref="Sha256"/> is the plaintext SHA-256; it is null for content saved before the
/// any-file upgrade, which is in the old record format (SVR1) and always text of at most 256 KiB.
/// </summary>
public sealed record StoreFile(Guid FileId, string LogicalName, Guid RevisionId, long ByteLength, FileKind Kind = FileKind.Text, byte[]? Sha256 = null)
{
    /// <summary>Saved in record format SVR2 (any content since the any-file upgrade).</summary>
    internal bool Segmented => Sha256 is not null;
}
public sealed record StoreSnapshot(Guid SnapshotId, IReadOnlyList<StoreFile> Files);

/// <summary>One change in <see cref="RevisionStore.CommitChanges"/>.</summary>
public abstract record StoreChange
{
    private StoreChange() { }
    /// <summary>Creates a file, or replaces the file with this name (case-insensitive), keeping its file ID. Any bytes:
    /// the store decides whether they are text or binary. <paramref name="Action"/> labels the history entry: created,
    /// edited or imported (default: created or edited).</summary>
    public sealed record Write(string Name, byte[] Content, string? Action = null) : StoreChange;
    /// <summary>Renames or moves a file; its ID and current revision are kept.</summary>
    public sealed record Rename(Guid FileId, string NewName) : StoreChange;
    /// <summary>Moves a file to the deleted list; its history is kept and it can be restored.</summary>
    public sealed record Delete(Guid FileId) : StoreChange;
    /// <summary>Brings a deleted file back under <paramref name="Name"/>, at the revision it had when deleted.</summary>
    public sealed record Restore(Guid FileId, string Name) : StoreChange;
    /// <summary>Commits an upload staged by <see cref="RevisionStore.BeginUpload"/>: a new file under <paramref name="Name"/>
    /// (create-only), or, with a null name, the new revision of the file the upload was begun for (its name is kept).</summary>
    public sealed record Stage(StagedFile File, string? Name, string? Action = null) : StoreChange;
    /// <summary>Makes a revision from the file's own history current again ("reverted"); nothing is re-encrypted.</summary>
    public sealed record Revert(Guid FileId, Guid RevisionId) : StoreChange;
}
public sealed class RevisionConflictException : Exception
{
    public RevisionConflictException() : base("Snapshot has changed.") { }
}
/// <summary>A size, count or free-space limit (protocol code LimitExceeded). Nothing was written.</summary>
public sealed class StoreLimitException(string message) : Exception(message);

internal enum CommitStage { DocumentFlushed, ManifestFlushed, BeforeHeadPublish, HeadPublished }

/// <summary>
/// Encrypted immutable revisions in a trusted service-owned directory. Since the any-file upgrade (approved 2026-10-19,
/// docs/vault-any-file-design.md) content is any bytes up to 1 GiB, saved in record format SVR2 under a per-project key;
/// manifests are schema 4. Older stores (schema 2/3, SVR1 records) are read as before and move to the new format on
/// their next commit, one-way.
/// </summary>
public sealed partial class RevisionStore
{
    public const int MaximumFiles = 25000;
    public const long MaximumFileBytes = SegmentedRecord.MaxDocumentLength;
    public const long MaximumProjectBytes = 8L << 30;
    public const long FreeSpaceReserveBytes = 4L << 30;
    private const int MaximumLegacyDocumentBytes = 262144;   // SVR1 content
    private const int MaximumLegacyManifestBytes = 1048576;  // SVR1 manifests (schema 2/3)
    private static readonly long MaximumManifestRecordBytes = SegmentedRecord.RecordLength(RecordKind.Manifest, SegmentedRecord.MaxManifestLength);
    private static readonly long ProjectKeyRecordBytes = SegmentedRecord.RecordLength(RecordKind.ProjectKey, ProjectKey.KeyLength);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string root;
    private readonly Guid projectId;
    private readonly IKeyProtector protector;
    private readonly TimeSpan lockTimeout;
    // Last authenticated manifest. Ids, names, lengths and hashes only: no document text, no keys.
    private volatile Manifest? cache;
    internal Action<CommitStage>? FaultInjection { get; set; }
    // Limits lowered by tests only, so their boundaries can be tested without gigabytes of fixtures.
    internal int FileLimit { get; set; } = MaximumFiles;
    internal long ProjectByteLimit { get; set; } = MaximumProjectBytes;
    internal long FreeSpaceReserve { get; set; } = FreeSpaceReserveBytes;

    private sealed record KeyRef(Guid Id, byte[] Sha256);
    // An authenticated manifest plus the SHA-256 of its own record file (cache key) and of each document record
    // (live and deleted files), each live file's history reference (null: saved before history existed), the deleted
    // files, and the project key record (null for schema 2/3, before the any-file upgrade).
    private sealed record Manifest(StoreSnapshot Snapshot, byte[] RecordSha256, IReadOnlyDictionary<Guid, byte[]> DocumentSha256,
        IReadOnlyDictionary<Guid, HistoryRef?> History, IReadOnlyList<DeletedEntry> Deleted, KeyRef? Key);

    public RevisionStore(string root, Guid projectId, IKeyProtector protector, TimeSpan? lockTimeout = null)
    {
        if (!Path.IsPathFullyQualified(root) || projectId == Guid.Empty) throw new ArgumentException("Invalid store configuration.");
        this.root = Path.GetFullPath(root);
        this.projectId = projectId;
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        this.lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5);
        if (this.lockTimeout < TimeSpan.Zero || this.lockTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentException("Invalid lock timeout.");
    }

    public StoreSnapshot Initialize()
    {
        CheckAncestors(root);
        Directory.CreateDirectory(root);
        using var lease = AcquireLock();
        // Orphans from failed initialization require explicit review, never implicit reuse.
        if (Directory.EnumerateFileSystemEntries(root).Any(p => Path.GetFileName(p) != "LOCK"))
            throw new IOException("Store is not empty.");
        using var keys = new KeyScope(this, null);
        var snapshot = Snapshot(Guid.NewGuid(), []);
        var manifest = WriteManifest(snapshot, new Dictionary<Guid, byte[]>(), new Dictionary<Guid, HistoryRef?>(), [], keys.Reference());
        Publish(snapshot.SnapshotId, previousId: null);
        cache = manifest;
        return snapshot;
    }

    public StoreSnapshot List()
    {
        using var lease = AcquireLock();
        return ReadCurrent().Snapshot;
    }

    public byte[] Open(Guid expectedSnapshotId, Guid fileId)
    {
        using var lease = AcquireLock();
        var manifest = ReadCurrent();
        if (manifest.Snapshot.SnapshotId != expectedSnapshotId) throw new RevisionConflictException();
        var entry = manifest.Snapshot.Files.SingleOrDefault(f => f.FileId == fileId) ?? throw new KeyNotFoundException("Document not found.");
        using var keys = new KeyScope(this, manifest);
        return ReadDocument(entry, manifest.DocumentSha256[entry.RevisionId], keys);
    }

    /// <summary>
    /// Decrypts every document of one snapshot once, under a single lock lease, for bulk consumers such as a build.
    /// Each plaintext buffer is zeroed as soon as <paramref name="consume"/> returns; copy only what must be kept.
    /// The project key is unwrapped at most once for the whole call.
    /// </summary>
    public void ReadSnapshot(Guid expectedSnapshotId, Action<StoreFile, ReadOnlySpan<byte>> consume)
    {
        ArgumentNullException.ThrowIfNull(consume);
        using var lease = AcquireLock();
        var manifest = ReadCurrent();
        if (manifest.Snapshot.SnapshotId != expectedSnapshotId) throw new RevisionConflictException();
        using var keys = new KeyScope(this, manifest);
        foreach (var entry in manifest.Snapshot.Files)
        {
            var content = ReadDocument(entry, manifest.DocumentSha256[entry.RevisionId], keys);
            try { consume(entry, content); }
            finally { CryptographicOperations.ZeroMemory(content); }
        }
    }

    public StoreSnapshot Commit(Guid expectedSnapshotId, string logicalName, ReadOnlySpan<byte> content)
    {
        var copy = content.ToArray();
        try { return CommitMany(expectedSnapshotId, [(logicalName, copy)]); }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    /// <summary>
    /// Creates or replaces several documents in one snapshot: one new manifest, all or nothing. A name that matches an
    /// existing file (case-insensitively) replaces it and keeps its file ID. Callers zero their buffers afterwards.
    /// </summary>
    public StoreSnapshot CommitMany(Guid expectedSnapshotId, IReadOnlyList<(string Name, byte[] Content)> documents) =>
        CommitChanges(expectedSnapshotId, documents.Select(d => (StoreChange)new StoreChange.Write(d.Name, d.Content)).ToList());

    /// <summary>
    /// Applies writes, renames, deletes and restores in one snapshot: one new manifest, all or nothing. Deletes apply
    /// first, then restores, then renames (all at once, so names can be swapped), then writes. A write whose name matches
    /// a file after the renames replaces it and keeps its file ID. A rename keeps the file ID and its current revision
    /// record, so no content is re-encrypted. A delete moves the file to the deleted list with its history; a restore
    /// brings it back at its last revision. Every write and restore appends to the file's encrypted history, recording
    /// <paramref name="actor"/> (an account name) and the time. Unknown file IDs throw
    /// <see cref="KeyNotFoundException"/>; a resulting duplicate name or file/folder clash throws
    /// <see cref="NameExistsException"/>, and a count, size or free-space limit <see cref="StoreLimitException"/>, before
    /// anything is written. Callers zero their buffers.
    /// </summary>
    public StoreSnapshot CommitChanges(Guid expectedSnapshotId, IReadOnlyList<StoreChange> changes, string? actor = null)
    {
        if (changes.Count == 0 || changes.Count > FileLimit) throw new ArgumentException("Invalid change count.");
        var writes = changes.OfType<StoreChange.Write>().ToList();
        var renames = changes.OfType<StoreChange.Rename>().ToList();
        var deletes = changes.OfType<StoreChange.Delete>().ToList();
        var restores = changes.OfType<StoreChange.Restore>().ToList();
        var stages = changes.OfType<StoreChange.Stage>().ToList();
        var reverts = changes.OfType<StoreChange.Revert>().ToList();
        if (writes.Count + renames.Count + deletes.Count + restores.Count + stages.Count + reverts.Count != changes.Count) throw new ArgumentException("Unknown change.");
        ValidateActor(actor);
        foreach (var w in writes)
        {
            ValidateName(w.Name);
            ArgumentNullException.ThrowIfNull(w.Content);
            if (w.Content.LongLength > MaximumFileBytes) throw new StoreLimitException("A file is larger than 1 GiB.");
            if (w.Action is not (null or "created" or "edited" or "imported")) throw new ArgumentException("Invalid action.");
        }
        foreach (var s in stages)
        {
            ArgumentNullException.ThrowIfNull(s.File);
            if (s.Name is not null) ValidateName(s.Name);
            if ((s.Name is null) == s.File.NewFile) throw new ArgumentException("A new file needs a name; a replacement keeps its own.");
            if (s.Action is not (null or "created" or "edited" or "imported")) throw new ArgumentException("Invalid action.");
        }
        foreach (var r in renames) ValidateName(r.NewName);
        foreach (var r in restores) ValidateName(r.Name);
        var newNames = writes.Select(w => w.Name).Concat(stages.Where(s => s.Name is not null).Select(s => s.Name!)).ToList();
        if (newNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != newNames.Count) throw new ArgumentException("Duplicate names in one commit.");
        var targeted = renames.Select(r => r.FileId).Concat(deletes.Select(d => d.FileId)).Concat(restores.Select(r => r.FileId))
            .Concat(reverts.Select(r => r.FileId)).Concat(stages.Where(s => s.Name is null).Select(s => s.File.FileId)).ToList();
        if (targeted.Contains(Guid.Empty) || targeted.Distinct().Count() != targeted.Count) throw new ArgumentException("A file can change only once per commit.");
        using var lease = AcquireLock();
        var manifest = ReadCurrent();
        var current = manifest.Snapshot;
        if (current.SnapshotId != expectedSnapshotId) throw new RevisionConflictException();
        var byId = current.Files.ToDictionary(f => f.FileId);
        var deletedById = manifest.Deleted.ToDictionary(d => d.File.FileId);
        foreach (var id in renames.Select(r => r.FileId).Concat(deletes.Select(d => d.FileId)).Concat(reverts.Select(r => r.FileId))
            .Concat(stages.Where(s => s.Name is null).Select(s => s.File.FileId))) if (!byId.ContainsKey(id)) throw new KeyNotFoundException("Document not found.");
        foreach (var r in restores) if (!deletedById.ContainsKey(r.FileId)) throw new KeyNotFoundException("Deleted document not found.");
        using var keys = new KeyScope(this, manifest);
        // Reverts: the revision must be in the file's own history (or be its current one), and its record still present.
        var revertTo = new Dictionary<Guid, (StoreFile File, byte[] RecordSha256)>();
        foreach (var r in reverts)
        {
            var found = FindRevision(manifest, r.FileId, r.RevisionId, keys);
            if (!File.Exists(BlobPath(found.File.RevisionId)) || new FileInfo(BlobPath(found.File.RevisionId)).Length != RecordSize(found.File))
                throw new InvalidDataException("A record does not match the history.");
            revertTo[r.FileId] = found;
        }
        var now = Now();
        var history = new Dictionary<Guid, HistoryRef?>(manifest.History);
        var deleted = manifest.Deleted.Where(d => !restores.Any(r => r.FileId == d.File.FileId)).ToList();
        foreach (var d in deletes)
        {
            deleted.Add(new DeletedEntry(byId[d.FileId], history.GetValueOrDefault(d.FileId), now, actor));
            byId.Remove(d.FileId); history.Remove(d.FileId);
        }
        foreach (var r in restores) { var d = deletedById[r.FileId]; byId[r.FileId] = d.File with { LogicalName = r.Name }; history[r.FileId] = d.History; }
        foreach (var r in renames) byId[r.FileId] = byId[r.FileId] with { LogicalName = r.NewName };
        var byName = new Dictionary<string, StoreFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in byId.Values) if (!byName.TryAdd(f.LogicalName, f)) throw new NameExistsException(); // renamed or restored onto another file
        // Uploads committed as new files are create-only, like ImportFiles.
        foreach (var s in stages) if (s.Name is not null && byName.ContainsKey(s.Name)) throw new NameExistsException();
        // Every name, count, size and space check happens before any record is written or moved.
        var planned = writes.Select(w => (Write: w, Existing: byName.GetValueOrDefault(w.Name))).ToList();
        var changedIds = planned.Where(p => p.Existing is not null).Select(p => p.Existing!.FileId)
            .Concat(stages.Where(s => s.Name is null).Select(s => s.File.FileId)).Concat(reverts.Select(r => r.FileId)).ToHashSet();
        var kept = byId.Values.Where(f => !changedIds.Contains(f.FileId)).ToList();
        var finalNames = kept.Select(f => f.LogicalName).Concat(writes.Select(w => w.Name))
            .Concat(stages.Select(s => s.Name ?? byId[s.File.FileId].LogicalName)).Concat(reverts.Select(r => byId[r.FileId].LogicalName)).ToList();
        if (finalNames.Count > FileLimit) throw new StoreLimitException("Document count exceeded.");
        if (!IsValidTree(finalNames)) throw new NameExistsException(); // a file where a folder is, or vice versa
        long written = writes.Sum(w => w.Content.LongLength);
        long finalBytes = kept.Sum(f => f.ByteLength) + written + stages.Sum(s => s.File.Length) + revertTo.Values.Sum(r => r.File.ByteLength);
        if (finalBytes > ProjectByteLimit) throw new StoreLimitException("Project size exceeded.");
        EnsureFreeSpace(written + changes.Count * 4096L + (1L << 20)); // staged records are already on this volume
        foreach (var s in stages) CheckStaged(s.File, manifest);

        var added = new List<StoreFile>();
        var hashes = new Dictionary<Guid, byte[]>();
        var appends = new List<(Guid FileId, HistoryRef? Prior, HistoryEntry? Legacy, HistoryEntry Entry)>();
        void Produced(StoreFile entry, byte[] recordSha256, StoreFile? existing, string action)
        {
            hashes[entry.RevisionId] = recordSha256;
            added.Add(entry);
            var prior = existing is null ? null : history.GetValueOrDefault(existing.FileId);
            // A file saved before history existed starts its history with the revision it had then.
            var legacy = existing is not null && prior is null ? HistoryEntryFor(existing, manifest.DocumentSha256[existing.RevisionId], null, null, "before-history") : null;
            appends.Add((entry.FileId, prior, legacy, HistoryEntryFor(entry, recordSha256, now, actor, action)));
        }
        foreach (var (write, existing) in planned)
        {
            var entry = new StoreFile(existing?.FileId ?? Guid.NewGuid(), write.Name, Guid.NewGuid(), write.Content.LongLength, KindOf(write.Content), SHA256.HashData(write.Content));
            var record = SegmentedRecord.Encrypt(new(projectId, entry.FileId, entry.RevisionId, RecordKind.Document), write.Content, null, keys.Get());
            WriteBlob(entry.RevisionId, record);
            Produced(entry, SHA256.HashData(record), existing, write.Action ?? (existing is null ? "created" : "edited"));
        }
        foreach (var s in stages)
        {
            var existing = s.Name is null ? byId[s.File.FileId] : null;
            var target = BlobPath(s.File.RevisionId);
            CheckAncestors(target);
            File.Move(s.File.Path, target, overwrite: false); // same volume: the vault root's staging folder
            Produced(new StoreFile(s.File.FileId, s.Name ?? existing!.LogicalName, s.File.RevisionId, s.File.Length, s.File.Kind, s.File.Sha256),
                s.File.RecordSha256, existing, s.Action ?? (existing is null ? "created" : "edited"));
        }
        foreach (var r in reverts)
        {
            var existing = byId[r.FileId];
            var (file, recordSha256) = revertTo[r.FileId];
            Produced(file with { FileId = r.FileId, LogicalName = existing.LogicalName }, recordSha256, existing, "reverted");
        }
        foreach (var r in restores)
        {
            var file = byId[r.FileId];
            appends.Add((r.FileId, history.GetValueOrDefault(r.FileId), null, HistoryEntryFor(file, manifest.DocumentSha256[file.RevisionId], now, actor, "restored")));
        }
        if (writes.Count + stages.Count > 0) FaultInjection?.Invoke(CommitStage.DocumentFlushed);
        foreach (var (fileId, prior, legacy, entry) in appends) history[fileId] = AppendHistory(fileId, prior, legacy, entry, keys);
        // Sets, not nested scans: a project can have 25,000 files.
        var addedIds = added.Select(a => a.FileId).ToHashSet();
        var entries = byId.Values.Where(f => !changedIds.Contains(f.FileId) && !addedIds.Contains(f.FileId)).Concat(added).ToArray();
        foreach (var f in entries.Concat(deleted.Select(d => d.File))) if (!hashes.ContainsKey(f.RevisionId)) hashes[f.RevisionId] = manifest.DocumentSha256[f.RevisionId];
        if (deleted.Count > MaximumDeleted) deleted = deleted.Skip(deleted.Count - MaximumDeleted).ToList(); // oldest drop off the list; their records stay
        var liveIds = entries.Select(f => f.FileId).ToHashSet();
        foreach (var id in history.Keys.Where(id => !liveIds.Contains(id)).ToList()) history.Remove(id);
        var next = Snapshot(Guid.NewGuid(), entries);
        return PublishNext(manifest, next, hashes, history, deleted, keys);
    }

    // Writes the next manifest (the first commit on an old store creates the project key), publishes it, and prunes.
    private StoreSnapshot PublishNext(Manifest manifest, StoreSnapshot next, IReadOnlyDictionary<Guid, byte[]> hashes,
        IReadOnlyDictionary<Guid, HistoryRef?> history, IReadOnlyList<DeletedEntry> deleted, KeyScope keys)
    {
        var current = manifest.Snapshot;
        var previousPointer = TryReadPointer("PREVIOUS");
        var written = WriteManifest(next, hashes, history, deleted, keys.Reference());
        FaultInjection?.Invoke(CommitStage.ManifestFlushed);
        Publish(next.SnapshotId, previousId: current.SnapshotId); // current was authenticated by the caller, under the lock
        cache = written;
        // Decision C5: nothing reads a manifest older than PREVIOUS, so the one PREVIOUS named before this commit goes.
        // Plain deletion of ciphertext; a failure only leaves a stray record.
        if (previousPointer is { } stale && stale != next.SnapshotId && stale != current.SnapshotId) TryDeleteRecord(stale);
        return next;
    }

    private static HistoryEntry HistoryEntryFor(StoreFile file, byte[] recordSha256, DateTimeOffset? at, string? by, string action) =>
        new(file.RevisionId, recordSha256, file.ByteLength, file.LogicalName, at, by, action, file.Kind, file.Sha256);

    /// <summary>Text is strict UTF-8 (a BOM allowed) with no NUL byte; everything else is binary. Empty files are text.</summary>
    public static FileKind KindOf(ReadOnlySpan<byte> content)
    {
        if (content.IndexOf((byte)0) >= 0) return FileKind.Binary;
        try { _ = Utf8.GetCharCount(content); return FileKind.Text; }
        catch (DecoderFallbackException) { return FileKind.Binary; }
    }

    private void EnsureFreeSpace(long needed)
    {
        long available;
        try { available = new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return; } // not a drive letter: the write itself reports
        if (available - needed < FreeSpaceReserve) throw new StoreLimitException("Not enough free space on the vault drive.");
    }

    private static StoreSnapshot Snapshot(Guid id, IEnumerable<StoreFile> files) => new(id, Array.AsReadOnly(files.ToArray()));

    // ---- reading and checking -------------------------------------------------------------------------------------

    private Manifest ReadCurrent()
    {
        var id = TryReadPointer("HEAD") ?? throw new InvalidDataException("Invalid active pointer.");
        var encrypted = ReadBounded(BlobPath(id), MaximumManifestRecordBytes);
        var recordSha256 = SHA256.HashData(encrypted);
        // Reuse the last authenticated manifest only if HEAD names it and its record file is byte-identical.
        var manifest = cache;
        if (manifest is null || manifest.Snapshot.SnapshotId != id || !CryptographicOperations.FixedTimeEquals(manifest.RecordSha256, recordSha256))
        {
            cache = null;
            bool segmented = SegmentedRecord.IsSegmented(encrypted);
            var context = new RecordContext(projectId, Guid.Empty, id, RecordKind.Manifest);
            var plaintext = segmented ? SegmentedRecord.Decrypt(context, encrypted, protector) : RecordCodec.Decrypt(context, encrypted, protector);
            try { manifest = ParseManifest(plaintext, id, recordSha256, segmented); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        CheckRecords(manifest);
        cache = manifest;
        return manifest;
    }

    // Decision C4: every operation checks that every referenced record (live, deleted, history, project key) is present,
    // is not a link, and (where its size is known) has exactly the expected size, from one directory listing. Content
    // records are hashed against the manifest whenever they are read, before any of their content is used.
    private void CheckRecords(Manifest manifest)
    {
        var present = new Dictionary<string, (long Length, FileAttributes Attributes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in new DirectoryInfo(root).EnumerateFiles("*.svr")) present[info.Name] = (info.Length, info.Attributes);
        void Expect(Guid id, long? length)
        {
            if (!present.TryGetValue(id.ToString("D") + ".svr", out var found) || (length is { } l && found.Length != l && (found.Attributes & FileAttributes.ReparsePoint) == 0))
                throw new InvalidDataException("A record does not match the manifest.");
            if ((found.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Untrusted store path.");
        }
        foreach (var f in manifest.Snapshot.Files) Expect(f.RevisionId, RecordSize(f));
        foreach (var d in manifest.Deleted) { Expect(d.File.RevisionId, RecordSize(d.File)); if (d.History is { } h) Expect(h.RecordId, null); }
        foreach (var h in manifest.History.Values) if (h is not null) Expect(h.RecordId, null);
        if (manifest.Key is { } key) Expect(key.Id, ProjectKeyRecordBytes);
    }

    private static long RecordSize(StoreFile file) => file.Segmented
        ? SegmentedRecord.RecordLength(RecordKind.Document, file.ByteLength)
        : file.ByteLength + RecordCodec.RecordOverhead;

    /// <summary>The whole plaintext of one revision, checked against the manifest. The caller zeroes it.</summary>
    private byte[] ReadDocument(StoreFile entry, byte[] recordSha256, KeyScope keys)
    {
        if (!entry.Segmented)
        {
            var encrypted = ReadBounded(BlobPath(entry.RevisionId), MaximumLegacyDocumentBytes + RecordCodec.RecordOverhead);
            if (encrypted.Length != RecordSize(entry) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(encrypted), recordSha256))
                throw new InvalidDataException("Document record does not match the manifest.");
            var legacy = RecordCodec.Decrypt(new(projectId, entry.FileId, entry.RevisionId, RecordKind.Document), encrypted, protector);
            try
            {
                if (legacy.Length != entry.ByteLength || !IsValidContent(legacy)) throw new InvalidDataException("Document length mismatch.");
                return legacy;
            }
            catch { CryptographicOperations.ZeroMemory(legacy); throw; }
        }
        var content = new byte[entry.ByteLength];
        long offset = 0;
        try
        {
            ReadContent(entry, recordSha256, keys, segment => { segment.CopyTo(content.AsSpan((int)offset)); offset += segment.Length; });
            if (entry.Kind == FileKind.Text && KindOf(content) != FileKind.Text) throw new InvalidDataException("Document kind mismatch.");
            return content;
        }
        catch { CryptographicOperations.ZeroMemory(content); throw; }
    }

    /// <summary>
    /// Streams one SVR2 revision segment by segment (one segment in memory). The record file's SHA-256 and the plaintext
    /// SHA-256 are checked against the manifest at the end: until this returns, what <paramref name="consume"/> received
    /// is provisional, and a mismatch throws.
    /// </summary>
    private void ReadContent(StoreFile entry, byte[] recordSha256, KeyScope keys, Action<ReadOnlySpan<byte>> consume)
    {
        var path = BlobPath(entry.RevisionId);
        CheckStoreFile(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        if (file.Length != RecordSize(entry)) throw new InvalidDataException("Document record does not match the manifest.");
        using var hashing = new HashingReadStream(file);
        using var plain = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var reader = new SegmentedRecordReader(new(projectId, entry.FileId, entry.RevisionId, RecordKind.Document), hashing, null, keys.Get()))
        {
            if (reader.PlaintextLength != entry.ByteLength) throw new InvalidDataException("Document length mismatch.");
            var buffer = new byte[(int)Math.Min(SegmentedRecord.SegmentLength, entry.ByteLength)];
            try
            {
                for (int n; !reader.Finished;)
                {
                    n = reader.ReadNextSegment(buffer);
                    plain.AppendData(buffer, 0, n);
                    consume(buffer.AsSpan(0, n));
                }
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
        if (hashing.Position != file.Length || !CryptographicOperations.FixedTimeEquals(hashing.Hash(), recordSha256) ||
            !CryptographicOperations.FixedTimeEquals(plain.GetHashAndReset(), entry.Sha256))
            throw new InvalidDataException("Document record does not match the manifest.");
    }

    // ---- the project key: unwrapped at most once per operation, never kept between operations (decision C2) --------

    private sealed class KeyScope(RevisionStore store, Manifest? manifest) : IDisposable
    {
        private KeyRef? reference = manifest?.Key;
        private ProjectKey? key;

        /// <summary>The project key, unwrapped (or, for a store without one, created and written) on first use.</summary>
        public ProjectKey Get()
        {
            if (key is not null) return key;
            if (reference is null)
            {
                var (created, record) = ProjectKey.Create(store.projectId, store.protector);
                try { store.WriteBlob(created.Id, record); }
                catch { created.Dispose(); throw; }
                reference = new KeyRef(created.Id, SHA256.HashData(record));
                return key = created;
            }
            var encrypted = store.ReadBounded(store.BlobPath(reference.Id), ProjectKeyRecordBytes);
            if (encrypted.Length != ProjectKeyRecordBytes || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(encrypted), reference.Sha256))
                throw new InvalidDataException("Project key record does not match the manifest.");
            return key = ProjectKey.Open(store.projectId, reference.Id, encrypted, store.protector);
        }

        /// <summary>The key reference for a schema 4 manifest; creates the key if the store has none yet.</summary>
        public KeyRef Reference()
        {
            if (reference is null) Get();
            return reference!;
        }

        public void Dispose() => key?.Dispose();
    }

    // ---- manifest schema 4 ----------------------------------------------------------------------------------------
    // Schema 4 (approved 2026-10-19): schema 3 plus the project key reference and, per file, its kind and plaintext
    // SHA-256 (null for content saved before the upgrade, which is SVR1). Encrypted as SVR2, TPM-wrapped. Schemas 2 and 3
    // (SVR1) are still read.
    private const int ManifestVersion = 4;

    private Manifest WriteManifest(StoreSnapshot snapshot, IReadOnlyDictionary<Guid, byte[]> documentSha256,
        IReadOnlyDictionary<Guid, HistoryRef?> history, IReadOnlyList<DeletedEntry> deleted, KeyRef key)
    {
        Dictionary<string, object?> Entry(StoreFile f, HistoryRef? h) => new()
        {
            ["fileId"] = f.FileId.ToString("D"), ["name"] = f.LogicalName, ["revisionId"] = f.RevisionId.ToString("D"), ["byteLength"] = f.ByteLength,
            ["recordSha256"] = Convert.ToHexStringLower(documentSha256[f.RevisionId]),
            ["historyRecordId"] = h?.RecordId.ToString("D"), ["historyRecordSha256"] = h is { } r ? Convert.ToHexStringLower(r.Sha256) : null,
            ["kind"] = f.Kind == FileKind.Text ? "text" : "binary", ["sha256"] = f.Sha256 is { } s ? Convert.ToHexStringLower(s) : null,
        };
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = ManifestVersion, snapshotId = snapshot.SnapshotId.ToString("D"),
            projectKeyId = key.Id.ToString("D"), projectKeySha256 = Convert.ToHexStringLower(key.Sha256),
            files = snapshot.Files.Select(f => Entry(f, history.GetValueOrDefault(f.FileId))),
            deleted = deleted.Select(d =>
            {
                var entry = Entry(d.File, d.History);
                entry["deletedAt"] = FormatTime(d.DeletedAt); entry["deletedBy"] = d.DeletedBy;
                return entry;
            }),
        });
        try
        {
            if (plaintext.Length > SegmentedRecord.MaxManifestLength) throw new StoreLimitException("Manifest too large.");
            var record = SegmentedRecord.Encrypt(new(projectId, Guid.Empty, snapshot.SnapshotId, RecordKind.Manifest), plaintext, protector);
            WriteBlob(snapshot.SnapshotId, record);
            return new(snapshot, SHA256.HashData(record), documentSha256, history, deleted, key);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static Manifest ParseManifest(byte[] plaintext, Guid expectedId, byte[] recordSha256, bool segmented)
    {
        try
        {
            using var document = JsonDocument.Parse(plaintext, new JsonDocumentOptions { MaxDepth = 8 });
            var value = document.RootElement;
            int version = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            // Schema 4 is SVR2-encrypted; schemas 2 and 3 are SVR1. A mismatch is a downgrade or a forgery.
            if (segmented != (version == 4)) throw new InvalidDataException();
            if (version == 2) Properties(value, "version", "snapshotId", "files");
            else if (version == 3) Properties(value, "version", "snapshotId", "files", "deleted");
            else if (version == 4) Properties(value, "version", "snapshotId", "projectKeyId", "projectKeySha256", "files", "deleted");
            else throw new InvalidDataException();
            if (ReadId(value.GetProperty("snapshotId")) != expectedId) throw new InvalidDataException();
            var files = value.GetProperty("files");
            if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > MaximumFiles) throw new InvalidDataException();
            // Every id is unique across live files, deleted files, revisions, history records and the project key.
            var ids = new HashSet<Guid> { expectedId }; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            KeyRef? key = null;
            if (version == 4)
            {
                key = new KeyRef(ReadId(value.GetProperty("projectKeyId")), ReadSha256(value.GetProperty("projectKeySha256")));
                if (!ids.Add(key.Id)) throw new InvalidDataException();
            }
            var entries = new List<StoreFile>();
            var hashes = new Dictionary<Guid, byte[]>();
            var history = new Dictionary<Guid, HistoryRef?>();
            string[] fileProperties = version switch
            {
                2 => ["fileId", "name", "revisionId", "byteLength", "recordSha256"],
                3 => ["fileId", "name", "revisionId", "byteLength", "recordSha256", "historyRecordId", "historyRecordSha256"],
                _ => ["fileId", "name", "revisionId", "byteLength", "recordSha256", "historyRecordId", "historyRecordSha256", "kind", "sha256"],
            };
            (StoreFile File, HistoryRef? History) ReadEntry(JsonElement file)
            {
                var fileId = ReadId(file.GetProperty("fileId"));
                var revision = ReadId(file.GetProperty("revisionId"));
                var name = file.GetProperty("name").GetString()!;
                ValidateName(name);
                long length = file.GetProperty("byteLength").GetInt64();
                var hash = ReadSha256(file.GetProperty("recordSha256"));
                var kind = FileKind.Text; byte[]? sha256 = null;
                if (version == 4)
                {
                    kind = file.GetProperty("kind").GetString() switch { "text" => FileKind.Text, "binary" => FileKind.Binary, _ => throw new InvalidDataException() };
                    var s = file.GetProperty("sha256");
                    if (s.ValueKind != JsonValueKind.Null) sha256 = ReadSha256(s);
                }
                // Content saved before the upgrade (no plaintext hash) is SVR1: text of at most 256 KiB.
                long limit = sha256 is null ? MaximumLegacyDocumentBytes : MaximumFileBytes;
                if (!ids.Add(fileId) || !ids.Add(revision) || length < 0 || length > limit || (sha256 is null && kind != FileKind.Text)) throw new InvalidDataException();
                HistoryRef? historyRef = null;
                if (version >= 3)
                {
                    var recordId = file.GetProperty("historyRecordId"); var recordHash = file.GetProperty("historyRecordSha256");
                    if (recordId.ValueKind == JsonValueKind.Null != (recordHash.ValueKind == JsonValueKind.Null)) throw new InvalidDataException();
                    if (recordId.ValueKind != JsonValueKind.Null)
                    {
                        historyRef = new HistoryRef(ReadId(recordId), ReadSha256(recordHash));
                        if (!ids.Add(historyRef.RecordId)) throw new InvalidDataException();
                    }
                }
                hashes.Add(revision, hash);
                return (new StoreFile(fileId, name, revision, length, kind, sha256), historyRef);
            }
            foreach (var file in files.EnumerateArray())
            {
                Properties(file, fileProperties);
                var (entry, historyRef) = ReadEntry(file);
                if (!names.Add(entry.LogicalName)) throw new InvalidDataException();
                entries.Add(entry);
                history.Add(entry.FileId, historyRef);
            }
            if (!IsValidTree(names)) throw new InvalidDataException();
            var deleted = new List<DeletedEntry>();
            if (version >= 3)
            {
                var list = value.GetProperty("deleted");
                if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaximumDeleted) throw new InvalidDataException();
                foreach (var item in list.EnumerateArray())
                {
                    Properties(item, [.. fileProperties, "deletedAt", "deletedBy"]);
                    var (entry, historyRef) = ReadEntry(item);
                    var by = item.GetProperty("deletedBy");
                    string? deletedBy = by.ValueKind == JsonValueKind.Null ? null : by.GetString();
                    ValidateActor(deletedBy);
                    deleted.Add(new DeletedEntry(entry, historyRef, ParseTime(item.GetProperty("deletedAt").GetString()), deletedBy));
                }
            }
            return new(Snapshot(expectedId, entries), recordSha256, hashes, history, deleted, key);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException or InvalidDataException)
        { throw new InvalidDataException("Invalid encrypted manifest."); }
    }

    private static void Properties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!actual.Add(property.Name) || !expected.Contains(property.Name, StringComparer.Ordinal)) throw new InvalidDataException();
        if (actual.Count != expected.Length) throw new InvalidDataException();
    }
    private static bool TryId(string? value, out Guid id) => Guid.TryParseExact(value, "D", out id) && id != Guid.Empty && id.ToString("D") == value;
    private static Guid ReadId(JsonElement value) => TryId(value.GetString(), out var id) ? id : throw new InvalidDataException();
    private static byte[] ReadSha256(JsonElement value)
    {
        var text = value.GetString();
        if (text is null || text.Length != 64 || text.Any(c => c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))) throw new InvalidDataException();
        return Convert.FromHexString(text);
    }

    /// <summary>Whether <paramref name="name"/> is accepted as a logical file name. For callers validating input up front.</summary>
    public static bool IsValidLogicalName(string name)
    {
        try { ValidateName(name); return true; } catch (ArgumentException) { return false; }
    }

    /// <summary>Whether <paramref name="content"/> is editable text for the protocol's inline operations: strict UTF-8,
    /// at most 256 KiB. (The store itself accepts any bytes; see <see cref="KindOf"/>.)</summary>
    public static bool IsValidContent(ReadOnlySpan<byte> content)
    {
        if (content.Length > MaximumLegacyDocumentBytes) return false;
        try { _ = Utf8.GetCharCount(content); return true; } catch (DecoderFallbackException) { return false; }
    }

    public const int MaximumPathLength = 260;
    public const int MaximumPathDepth = 32;

    // A logical name is a relative path: 1-32 '/'-separated segments, at most 260 characters, each segment a valid
    // Windows file name (no empty, '.', '..', reserved device names, separators or trailing dot/space).
    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaximumPathLength) throw new ArgumentException("Invalid logical name.");
        var segments = name.Split('/');
        if (segments.Length > MaximumPathDepth) throw new ArgumentException("Invalid logical name.");
        foreach (var segment in segments) ValidateSegment(segment);
    }

    /// <summary>No path is both a file and a folder of another path (case-insensitive), e.g. "a" with "a/b".</summary>
    private static bool IsValidTree(IEnumerable<string> names)
    {
        var files = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (var name in files)
            for (int slash = name.IndexOf('/'); slash > 0; slash = name.IndexOf('/', slash + 1))
                if (files.Contains(name[..slash])) return false;
        return true;
    }

    private static void ValidateSegment(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128 || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.') ||
            name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*')) throw new ArgumentException("Invalid logical name.");
        try { _ = Utf8.GetByteCount(name); } catch (EncoderFallbackException) { throw new ArgumentException("Invalid logical name."); }
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3]))) throw new ArgumentException("Invalid logical name.");
    }

    // ---- files ------------------------------------------------------------------------------------------------------

    private string BlobPath(Guid id) => Path.Combine(root, id.ToString("D") + ".svr");
    private static void CheckAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Untrusted store path.");
        }
    }
    private FileStream AcquireLock()
    {
        CheckAncestors(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Store is unavailable.");
        var path = Path.Combine(root, "LOCK");
        var timer = Stopwatch.StartNew();
        while (true)
        {
            CheckAncestors(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (timer.Elapsed < lockTimeout) { Thread.Sleep(10); }
        }
    }
    private void CheckStoreFile(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) throw new IOException("Untrusted store path.");
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return; } // the open reports it
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Untrusted store path.");
    }
    // Reads happen only under the lock lease, which has just checked the root and all its ancestors, and every
    // record lives directly in the root; so only the file itself is checked here. Re-walking the ancestor chain
    // per record cost ~12 filesystem calls per file on every operation.
    private byte[] ReadBounded(string path, long limit)
    {
        CheckStoreFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 0 || stream.Length > limit || stream.Length > Array.MaxLength) throw new InvalidDataException("Invalid stored record length.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Stored record changed.");
        return bytes;
    }
    // HEAD must be valid. PREVIOUS is a recovery aid only: anything unreadable there just means nothing to prune.
    private Guid? TryReadPointer(string name)
    {
        var path = Path.Combine(root, name);
        if (name != "HEAD")
        {
            try { return TryReadPointer(path, out var previous) ? previous : null; }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
        }
        return TryReadPointer(path, out var id) ? id : throw new InvalidDataException("Invalid active pointer.");
    }
    private bool TryReadPointer(string path, out Guid id)
    {
        var pointer = ReadBounded(path, 36);
        return TryId(pointer.Length == 36 ? Encoding.ASCII.GetString(pointer) : null, out id);
    }
    private void WriteBlob(Guid id, byte[] encrypted)
    {
        var path = BlobPath(id);
        CheckAncestors(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(encrypted);
        stream.Flush(true);
    }
    private void TryDeleteRecord(Guid id)
    {
        try { var path = BlobPath(id); CheckStoreFile(path); File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    // A flushed, opaque same-directory pointer file holding one snapshot id. Leftovers from interruptions are allowed.
    private string WritePointer(Guid id)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("D") + ".head");
        CheckAncestors(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(Utf8.GetBytes(id.ToString("D")));
        stream.Flush(true);
        return path;
    }

    // Each step is a single rename (MoveFileEx, replacing the target), so an interruption at any point leaves either
    // the old HEAD or the new one, never neither. File.Replace is two renames (HEAD to PREVIOUS, then the temporary to
    // HEAD) and a process killed between them left no HEAD at all (found by the kill test, 2026-10-19). After an
    // interrupted commit PREVIOUS may equal HEAD; it is a recovery aid only and is never read automatically.
    private void Publish(Guid id, Guid? previousId)
    {
        var head = Path.Combine(root, "HEAD");
        var previous = Path.Combine(root, "PREVIOUS");
        CheckAncestors(head); CheckAncestors(previous);
        var temporary = WritePointer(id);
        FaultInjection?.Invoke(CommitStage.BeforeHeadPublish);
        if (previousId is { } prior)
        {
            File.Move(WritePointer(prior), previous, overwrite: true);
            File.Move(temporary, head, overwrite: true);
        }
        else File.Move(temporary, head, false);
        // The pointer's data was flushed in WritePointer; the swap itself is file-system metadata. FlushFileBuffers on the new
        // HEAD forces NTFS to commit its log, which holds the rename, so a returned commit survives a crash. Power
        // loss and device write caches still need the lab test (docs/lab-checklist.md).
        using (var published = new FileStream(head, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) published.Flush(true);
        FaultInjection?.Invoke(CommitStage.HeadPublished);
    }

    /// <summary>A read-only view of a file stream that hashes every byte in the order read; seeking is allowed only to
    /// the current position, so the hash always covers the whole record exactly once.</summary>
    private sealed class HashingReadStream(Stream inner) : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => position;
            set { if (value != position) throw new NotSupportedException("Records are read in order."); }
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            hash.AppendData(buffer[..n]);
            position += n;
            return n;
        }
        public byte[] Hash() => hash.GetHashAndReset();
        public override long Seek(long offset, SeekOrigin origin) => origin == SeekOrigin.Begin ? Position = offset : throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }
    }
}
