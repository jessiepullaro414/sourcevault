using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SourceVault.Revisions.Tests")]
namespace SourceVault.Storage;

public sealed record StoreFile(Guid FileId, string LogicalName, Guid RevisionId, int ByteLength);
public sealed record StoreSnapshot(Guid SnapshotId, IReadOnlyList<StoreFile> Files);

/// <summary>One change in <see cref="RevisionStore.CommitChanges"/>.</summary>
public abstract record StoreChange
{
    private StoreChange() { }
    /// <summary>Creates a file, or replaces the file with this name (case-insensitive), keeping its file ID.
    /// <paramref name="Action"/> labels the history entry: created, edited or imported (default: created or edited).</summary>
    public sealed record Write(string Name, byte[] Content, string? Action = null) : StoreChange;
    /// <summary>Renames or moves a file; its ID and current revision are kept.</summary>
    public sealed record Rename(Guid FileId, string NewName) : StoreChange;
    /// <summary>Moves a file to the deleted list; its history is kept and it can be restored.</summary>
    public sealed record Delete(Guid FileId) : StoreChange;
    /// <summary>Brings a deleted file back under <paramref name="Name"/>, at the revision it had when deleted.</summary>
    public sealed record Restore(Guid FileId, string Name) : StoreChange;
}
public sealed class RevisionConflictException : Exception
{
    public RevisionConflictException() : base("Snapshot has changed.") { }
}

internal enum CommitStage { DocumentFlushed, ManifestFlushed, BeforeHeadPublish, HeadPublished }

/// <summary>Encrypted immutable revisions in a trusted service-owned directory.</summary>
public sealed partial class RevisionStore
{
    public const int MaximumFiles = 1024;
    private const int MaximumDocumentBytes = 262144;
    private const int MaximumManifestBytes = 1048576;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string root;
    private readonly Guid projectId;
    private readonly IKeyProtector protector;
    private readonly TimeSpan lockTimeout;
    // Last authenticated manifest. Ids, names, lengths and record hashes only: no document text, no keys.
    private volatile Manifest? cache;
    internal Action<CommitStage>? FaultInjection { get; set; }

    // An authenticated manifest plus the SHA-256 of its own record file (cache key) and of each document record
    // (live and deleted files), each live file's history reference (null: saved before history existed) and the
    // deleted files.
    private sealed record Manifest(StoreSnapshot Snapshot, byte[] RecordSha256, IReadOnlyDictionary<Guid, byte[]> DocumentSha256,
        IReadOnlyDictionary<Guid, HistoryRef?> History, IReadOnlyList<DeletedEntry> Deleted);

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
        var snapshot = Snapshot(Guid.NewGuid(), []);
        var manifest = WriteManifest(snapshot, new Dictionary<Guid, byte[]>(), new Dictionary<Guid, HistoryRef?>(), []);
        Publish(snapshot.SnapshotId, false);
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
        return ReadDocument(entry, manifest.DocumentSha256[entry.RevisionId]);
    }

    /// <summary>
    /// Decrypts every document of one snapshot once, under a single lock lease, for bulk consumers such as a build.
    /// Each plaintext buffer is zeroed as soon as <paramref name="consume"/> returns; copy only what must be kept.
    /// </summary>
    public void ReadSnapshot(Guid expectedSnapshotId, Action<StoreFile, ReadOnlySpan<byte>> consume)
    {
        ArgumentNullException.ThrowIfNull(consume);
        using var lease = AcquireLock();
        var manifest = ReadCurrent();
        if (manifest.Snapshot.SnapshotId != expectedSnapshotId) throw new RevisionConflictException();
        foreach (var entry in manifest.Snapshot.Files)
        {
            var content = ReadDocument(entry, manifest.DocumentSha256[entry.RevisionId]);
            try { consume(entry, content); }
            finally { CryptographicOperations.ZeroMemory(content); }
        }
    }

    public StoreSnapshot Commit(Guid expectedSnapshotId, string logicalName, ReadOnlySpan<byte> utf8Content)
    {
        var copy = utf8Content.ToArray();
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
    /// <see cref="NameExistsException"/>, before anything is written. Callers zero their buffers.
    /// </summary>
    public StoreSnapshot CommitChanges(Guid expectedSnapshotId, IReadOnlyList<StoreChange> changes, string? actor = null)
    {
        if (changes.Count == 0 || changes.Count > MaximumFiles) throw new ArgumentException("Invalid change count.");
        var writes = changes.OfType<StoreChange.Write>().ToList();
        var renames = changes.OfType<StoreChange.Rename>().ToList();
        var deletes = changes.OfType<StoreChange.Delete>().ToList();
        var restores = changes.OfType<StoreChange.Restore>().ToList();
        if (writes.Count + renames.Count + deletes.Count + restores.Count != changes.Count) throw new ArgumentException("Unknown change.");
        ValidateActor(actor);
        foreach (var w in writes) { ValidateName(w.Name); ValidateContent(w.Content); if (w.Action is not (null or "created" or "edited" or "imported")) throw new ArgumentException("Invalid action."); }
        foreach (var r in renames) ValidateName(r.NewName);
        foreach (var r in restores) ValidateName(r.Name);
        if (writes.Select(w => w.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != writes.Count) throw new ArgumentException("Duplicate names in one commit.");
        var targeted = renames.Select(r => r.FileId).Concat(deletes.Select(d => d.FileId)).Concat(restores.Select(r => r.FileId)).ToList();
        if (targeted.Contains(Guid.Empty) || targeted.Distinct().Count() != targeted.Count) throw new ArgumentException("A file can change only once per commit.");
        using var lease = AcquireLock();
        var manifest = ReadCurrent();
        var current = manifest.Snapshot;
        if (current.SnapshotId != expectedSnapshotId) throw new RevisionConflictException();
        var byId = current.Files.ToDictionary(f => f.FileId);
        var deletedById = manifest.Deleted.ToDictionary(d => d.File.FileId);
        foreach (var id in renames.Select(r => r.FileId).Concat(deletes.Select(d => d.FileId))) if (!byId.ContainsKey(id)) throw new KeyNotFoundException("Document not found.");
        foreach (var r in restores) if (!deletedById.ContainsKey(r.FileId)) throw new KeyNotFoundException("Deleted document not found.");
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
        // Every name and count check happens before any record is written.
        var planned = writes.Select(w => (Write: w, Existing: byName.GetValueOrDefault(w.Name))).ToList();
        var finalNames = byId.Values.Where(f => !planned.Any(p => p.Existing?.FileId == f.FileId)).Select(f => f.LogicalName).Concat(writes.Select(w => w.Name)).ToList();
        if (finalNames.Count > MaximumFiles) throw new InvalidDataException("Document count exceeded.");
        if (!IsValidTree(finalNames)) throw new NameExistsException(); // a file where a folder is, or vice versa
        var replaced = new HashSet<Guid>();
        var added = new List<StoreFile>();
        var hashes = new Dictionary<Guid, byte[]>();
        var appends = new List<(Guid FileId, HistoryRef? Prior, HistoryEntry? Legacy, HistoryEntry Entry)>();
        foreach (var (write, existing) in planned)
        {
            if (existing is not null) replaced.Add(existing.FileId);
            var entry = new StoreFile(existing?.FileId ?? Guid.NewGuid(), write.Name, Guid.NewGuid(), write.Content.Length);
            var record = RecordCodec.Encrypt(new(projectId, entry.FileId, entry.RevisionId, RecordKind.Document), write.Content, protector);
            WriteBlob(entry.RevisionId, record);
            hashes[entry.RevisionId] = SHA256.HashData(record);
            added.Add(entry);
            var prior = existing is null ? null : history.GetValueOrDefault(existing.FileId);
            // A file saved before history existed starts its history with the revision it had then.
            var legacy = existing is not null && prior is null
                ? new HistoryEntry(existing.RevisionId, manifest.DocumentSha256[existing.RevisionId], existing.ByteLength, existing.LogicalName, null, null, "before-history") : null;
            appends.Add((entry.FileId, prior, legacy,
                new HistoryEntry(entry.RevisionId, hashes[entry.RevisionId], entry.ByteLength, entry.LogicalName, now, actor, write.Action ?? (existing is null ? "created" : "edited"))));
        }
        foreach (var r in restores)
        {
            var file = byId[r.FileId];
            appends.Add((r.FileId, history.GetValueOrDefault(r.FileId), null,
                new HistoryEntry(file.RevisionId, manifest.DocumentSha256[file.RevisionId], file.ByteLength, file.LogicalName, now, actor, "restored")));
        }
        if (writes.Count > 0) FaultInjection?.Invoke(CommitStage.DocumentFlushed);
        foreach (var (fileId, prior, legacy, entry) in appends) history[fileId] = AppendHistory(fileId, prior, legacy, entry);
        var entries = byId.Values.Where(f => !replaced.Contains(f.FileId)).Concat(added).ToArray();
        foreach (var f in entries.Concat(deleted.Select(d => d.File))) if (!hashes.ContainsKey(f.RevisionId)) hashes[f.RevisionId] = manifest.DocumentSha256[f.RevisionId];
        if (deleted.Count > MaximumDeleted) deleted = deleted.Skip(deleted.Count - MaximumDeleted).ToList(); // oldest drop off the list; their records stay
        foreach (var id in history.Keys.Where(id => !entries.Any(f => f.FileId == id)).ToList()) history.Remove(id);
        var next = Snapshot(Guid.NewGuid(), entries);
        var written = WriteManifest(next, hashes, history, deleted);
        FaultInjection?.Invoke(CommitStage.ManifestFlushed);
        Publish(next.SnapshotId, true);
        cache = written;
        return next;
    }

    private static StoreSnapshot Snapshot(Guid id, IEnumerable<StoreFile> files) => new(id, Array.AsReadOnly(files.ToArray()));

    private Manifest ReadCurrent()
    {
        var pointer = ReadBounded(Path.Combine(root, "HEAD"), 36);
        if (pointer.Length != 36 || !TryId(Utf8.GetString(pointer), out var id)) throw new InvalidDataException("Invalid active pointer.");
        var encrypted = ReadBounded(BlobPath(id), MaximumManifestBytes + 346);
        var recordSha256 = SHA256.HashData(encrypted);
        // Reuse the last authenticated manifest only if HEAD names it and its record file is byte-identical.
        var manifest = cache;
        if (manifest is null || manifest.Snapshot.SnapshotId != id || !CryptographicOperations.FixedTimeEquals(manifest.RecordSha256, recordSha256))
        {
            cache = null;
            var plaintext = RecordCodec.Decrypt(new(projectId, Guid.Empty, id, RecordKind.Manifest), encrypted, protector);
            try { manifest = ParseManifest(plaintext, id, recordSha256); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        // Every committed reference must be present and byte-identical to what was committed, even for a
        // metadata-only listing. The authenticated manifest holds each record's SHA-256, so no decryption is needed.
        foreach (var entry in manifest.Snapshot.Files)
            ReadDocumentRecord(entry, manifest.DocumentSha256[entry.RevisionId]);
        cache = manifest;
        return manifest;
    }

    private byte[] ReadDocumentRecord(StoreFile entry, byte[] expectedSha256)
    {
        var encrypted = ReadBounded(BlobPath(entry.RevisionId), MaximumDocumentBytes + 346);
        if (encrypted.Length != entry.ByteLength + 346 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(encrypted), expectedSha256))
            throw new InvalidDataException("Document record does not match the manifest.");
        return encrypted;
    }

    private byte[] ReadDocument(StoreFile entry, byte[] expectedSha256)
    {
        var encrypted = ReadDocumentRecord(entry, expectedSha256);
        var content = RecordCodec.Decrypt(new(projectId, entry.FileId, entry.RevisionId, RecordKind.Document), encrypted, protector);
        try
        {
            if (content.Length != entry.ByteLength) throw new InvalidDataException("Document length mismatch.");
            ValidateContent(content);
            return content;
        }
        catch { CryptographicOperations.ZeroMemory(content); throw; }
    }

    // Manifest schema 3 (owner-approved 2026-10-18): schema 2 plus, per file, an optional reference to its encrypted
    // history record, and a list of deleted files with their history. Schema 2 manifests are still read.
    private const int ManifestVersion = 3;

    private Manifest WriteManifest(StoreSnapshot snapshot, IReadOnlyDictionary<Guid, byte[]> documentSha256,
        IReadOnlyDictionary<Guid, HistoryRef?> history, IReadOnlyList<DeletedEntry> deleted)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = ManifestVersion, snapshotId = snapshot.SnapshotId.ToString("D"),
            files = snapshot.Files.Select(f => new
            {
                fileId = f.FileId.ToString("D"), name = f.LogicalName, revisionId = f.RevisionId.ToString("D"), byteLength = f.ByteLength,
                recordSha256 = Convert.ToHexStringLower(documentSha256[f.RevisionId]),
                historyRecordId = history.GetValueOrDefault(f.FileId)?.RecordId.ToString("D"),
                historyRecordSha256 = history.GetValueOrDefault(f.FileId) is { } h ? Convert.ToHexStringLower(h.Sha256) : null,
            }),
            deleted = deleted.Select(d => new
            {
                fileId = d.File.FileId.ToString("D"), name = d.File.LogicalName, revisionId = d.File.RevisionId.ToString("D"), byteLength = d.File.ByteLength,
                recordSha256 = Convert.ToHexStringLower(documentSha256[d.File.RevisionId]),
                historyRecordId = d.History?.RecordId.ToString("D"),
                historyRecordSha256 = d.History is { } h ? Convert.ToHexStringLower(h.Sha256) : null,
                deletedAt = FormatTime(d.DeletedAt), deletedBy = d.DeletedBy,
            }),
        });
        try
        {
            var record = RecordCodec.Encrypt(new(projectId, Guid.Empty, snapshot.SnapshotId, RecordKind.Manifest), plaintext, protector);
            WriteBlob(snapshot.SnapshotId, record);
            return new(snapshot, SHA256.HashData(record), documentSha256, history, deleted);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static Manifest ParseManifest(byte[] plaintext, Guid expectedId, byte[] recordSha256)
    {
        try
        {
            using var document = JsonDocument.Parse(plaintext, new JsonDocumentOptions { MaxDepth = 8 });
            var value = document.RootElement;
            int version = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            if (version == 2) Properties(value, "version", "snapshotId", "files");
            else if (version == 3) Properties(value, "version", "snapshotId", "files", "deleted");
            else throw new InvalidDataException();
            if (ReadId(value.GetProperty("snapshotId")) != expectedId) throw new InvalidDataException();
            var files = value.GetProperty("files");
            if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > MaximumFiles) throw new InvalidDataException();
            // Every id is unique across live files, deleted files, revisions and history records.
            var ids = new HashSet<Guid> { expectedId }; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<StoreFile>();
            var hashes = new Dictionary<Guid, byte[]>();
            var history = new Dictionary<Guid, HistoryRef?>();
            string[] fileProperties = version == 2
                ? ["fileId", "name", "revisionId", "byteLength", "recordSha256"]
                : ["fileId", "name", "revisionId", "byteLength", "recordSha256", "historyRecordId", "historyRecordSha256"];
            (StoreFile File, HistoryRef? History) ReadEntry(JsonElement file)
            {
                var fileId = ReadId(file.GetProperty("fileId"));
                var revision = ReadId(file.GetProperty("revisionId"));
                var name = file.GetProperty("name").GetString()!;
                ValidateName(name);
                int length = file.GetProperty("byteLength").GetInt32();
                var hash = ReadSha256(file.GetProperty("recordSha256"));
                if (!ids.Add(fileId) || !ids.Add(revision) || length < 0 || length > MaximumDocumentBytes) throw new InvalidDataException();
                HistoryRef? historyRef = null;
                if (version == 3)
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
                return (new StoreFile(fileId, name, revision, length), historyRef);
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
            if (version == 3)
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
            return new(Snapshot(expectedId, entries), recordSha256, hashes, history, deleted);
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

    /// <summary>Whether <paramref name="content"/> is accepted as a document: strict UTF-8, at most 256 KiB.</summary>
    public static bool IsValidContent(ReadOnlySpan<byte> content)
    {
        try { ValidateContent(content); return true; } catch (Exception e) when (e is ArgumentException or InvalidDataException) { return false; }
    }

    private static void ValidateContent(ReadOnlySpan<byte> value)
    {
        if (value.Length > MaximumDocumentBytes) throw new ArgumentException("Document limit exceeded.");
        try { _ = Utf8.GetCharCount(value); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Invalid document encoding."); }
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
    private static void CheckStoreFile(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return; } // the open reports it
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Untrusted store path.");
    }
    // Reads happen only under the lock lease, which has just checked the root and all its ancestors, and every
    // record lives directly in the root; so only the file itself is checked here. Re-walking the ancestor chain
    // per record cost ~12 filesystem calls per file on every operation.
    private byte[] ReadBounded(string path, int limit)
    {
        if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) throw new IOException("Untrusted store path.");
        CheckStoreFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 0 || stream.Length > limit) throw new InvalidDataException("Invalid stored record length.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Stored record changed.");
        return bytes;
    }
    private void WriteBlob(Guid id, byte[] encrypted)
    {
        var path = BlobPath(id);
        CheckAncestors(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(encrypted);
        stream.Flush(true);
    }
    private void Publish(Guid id, bool replace)
    {
        var head = Path.Combine(root, "HEAD");
        var previous = Path.Combine(root, "PREVIOUS");
        var temporary = Path.Combine(root, Guid.NewGuid().ToString("D") + ".head");
        CheckAncestors(head); CheckAncestors(previous); CheckAncestors(temporary);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(Utf8.GetBytes(id.ToString("D")));
            stream.Flush(true);
        }
        FaultInjection?.Invoke(CommitStage.BeforeHeadPublish);
        if (replace) File.Replace(temporary, head, previous);
        else File.Move(temporary, head, false);
        FaultInjection?.Invoke(CommitStage.HeadPublished);
    }
}
