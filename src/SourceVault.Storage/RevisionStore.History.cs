using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace SourceVault.Storage;

/// <summary>One saved version of a file. <see cref="SavedAt"/> and <see cref="SavedBy"/> are null for the revision a file
/// had when history began ("before-history").</summary>
public sealed record FileRevision(Guid RevisionId, long ByteLength, string Name, DateTimeOffset? SavedAt, string? SavedBy, string Action, FileKind Kind = FileKind.Text);

/// <summary>A page of a file's history, newest first. <see cref="More"/>: older pages exist.</summary>
public sealed record HistoryPage(IReadOnlyList<FileRevision> Revisions, bool More);

/// <summary>A deleted file that can be restored.</summary>
public sealed record DeletedFile(Guid FileId, string LogicalName, long ByteLength, DateTimeOffset DeletedAt, string? DeletedBy, FileKind Kind = FileKind.Text);

// Per-file history (manifest schema 3, docs/files-history-builds-design.md section 2).
//
// Each file's history is a chain of encrypted history records ("chunks"), newest first. A chunk lists up to 256
// revisions (oldest first) and may reference the previous, full chunk. The manifest references the newest chunk by id
// and SHA-256, and each chunk references its revisions' records and the older chunk the same way, so every old revision
// is authenticated from the current manifest. Chunks use the ordinary document record format with context
// (project, file, chunk id). Since the any-file upgrade chunks are version 2 (each entry adds the revision's kind and
// plaintext SHA-256) and are written as SVR2 under the project key; version 1 SVR1 chunks are still read. Saving appends to the newest chunk (one decryption, cached
// afterwards); renames don't touch history. Chunks are read lazily: a missing or altered chunk fails only operations
// that read that file's history.
public sealed partial class RevisionStore
{
    private const int MaxChunkEntries = 256;     // keeps a chunk well under the 256 KiB document record limit
    private const int MaxChunkBytes = 1048576;   // version 2 chunk plaintext
    private static readonly long MaxChunkRecordBytes = SegmentedRecord.RecordLength(RecordKind.Document, MaxChunkBytes);
    internal const int MaximumDeleted = 512;     // unchanged by the any-file upgrade (manifest size)
    private const int MaxActorLength = 256;

    private sealed record HistoryRef(Guid RecordId, byte[] Sha256);
    private sealed record DeletedEntry(StoreFile File, HistoryRef? History, DateTimeOffset DeletedAt, string? DeletedBy);
    private sealed record HistoryEntry(Guid RevisionId, byte[] RecordSha256, long ByteLength, string Name, DateTimeOffset? SavedAt, string? SavedBy, string Action,
        FileKind Kind, byte[]? Sha256);
    private sealed record HistoryChunk(Guid FileId, IReadOnlyList<HistoryEntry> Entries, HistoryRef? Older);

    // Authenticated chunks by record id, with the record hash they were verified against. Metadata only (ids, names,
    // times, account names), like the manifest cache. Cleared when large.
    private readonly ConcurrentDictionary<Guid, (byte[] Sha256, HistoryChunk Chunk)> chunks = new();

    /// <summary>
    /// A page of a file's history, newest first; page 0 is the newest. Works for live and deleted files. A file not saved
    /// since history began has one "before-history" entry: the revision it had then.
    /// </summary>
    public HistoryPage ReadHistory(Guid fileId, int page)
    {
        if (page < 0) throw new ArgumentException("Invalid page.");
        using var lease = AcquireLock();
        var manifest = ReadCurrent();
        var (file, historyRef) = Locate(manifest, fileId);
        if (historyRef is null)
            return page == 0 ? new([new FileRevision(file.RevisionId, file.ByteLength, file.LogicalName, null, null, "before-history", file.Kind)], false) : new([], false);
        using var keys = new KeyScope(this, manifest);
        var chunk = LoadChunk(fileId, historyRef, keys);
        for (int i = 0; i < page; i++)
        {
            if (chunk.Older is null) return new([], false);
            chunk = LoadChunk(fileId, chunk.Older, keys);
        }
        return new(chunk.Entries.Reverse().Select(e => new FileRevision(e.RevisionId, e.ByteLength, e.Name, e.SavedAt, e.SavedBy, e.Action, e.Kind)).ToList(), chunk.Older is not null);
    }

    /// <summary>Decrypts any revision in a live or deleted file's history. The caller zeroes the result.</summary>
    public byte[] OpenRevision(Guid fileId, Guid revisionId)
    {
        using var lease = AcquireLock();
        var manifest = ReadCurrent();
        var (file, historyRef) = Locate(manifest, fileId);
        using var keys = new KeyScope(this, manifest);
        if (file.RevisionId == revisionId) return ReadDocument(file, manifest.DocumentSha256[revisionId], keys);
        for (var next = historyRef; next is not null;)
        {
            var chunk = LoadChunk(fileId, next, keys);
            var entry = chunk.Entries.FirstOrDefault(e => e.RevisionId == revisionId);
            if (entry is not null) return ReadDocument(new StoreFile(fileId, entry.Name, entry.RevisionId, entry.ByteLength, entry.Kind, entry.Sha256), entry.RecordSha256, keys);
            next = chunk.Older;
        }
        throw new KeyNotFoundException("Revision not found.");
    }

    /// <summary>Deleted files that can be restored, most recently deleted first.</summary>
    public IReadOnlyList<DeletedFile> ListDeleted()
    {
        using var lease = AcquireLock();
        return ReadCurrent().Deleted.Reverse().Select(d => new DeletedFile(d.File.FileId, d.File.LogicalName, d.File.ByteLength, d.DeletedAt, d.DeletedBy, d.File.Kind)).ToList();
    }

    private static (StoreFile File, HistoryRef? History) Locate(Manifest manifest, Guid fileId)
    {
        var live = manifest.Snapshot.Files.FirstOrDefault(f => f.FileId == fileId);
        if (live is not null) return (live, manifest.History.GetValueOrDefault(fileId));
        var deleted = manifest.Deleted.FirstOrDefault(d => d.File.FileId == fileId) ?? throw new KeyNotFoundException("Document not found.");
        return (deleted.File, deleted.History);
    }

    // Appends one entry: to the newest chunk, or to a new chunk that references it when it is full.
    private HistoryRef AppendHistory(Guid fileId, HistoryRef? prior, HistoryEntry? legacy, HistoryEntry entry, KeyScope keys)
    {
        HistoryChunk next;
        if (prior is null) next = new HistoryChunk(fileId, legacy is null ? [entry] : [legacy, entry], null);
        else
        {
            var chunk = LoadChunk(fileId, prior, keys);
            next = chunk.Entries.Count >= MaxChunkEntries ? new HistoryChunk(fileId, [entry], prior) : chunk with { Entries = [.. chunk.Entries, entry] };
        }
        var recordId = Guid.NewGuid();
        var plaintext = SerializeChunk(next);
        try
        {
            if (plaintext.Length > MaxChunkBytes) throw new StoreLimitException("History record too large.");
            var record = SegmentedRecord.Encrypt(new(projectId, fileId, recordId, RecordKind.Document), plaintext, null, keys.Get());
            WriteBlob(recordId, record);
            var written = new HistoryRef(recordId, SHA256.HashData(record));
            Remember(recordId, written.Sha256, next);
            return written;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private HistoryChunk LoadChunk(Guid fileId, HistoryRef reference, KeyScope keys)
    {
        var encrypted = ReadBounded(BlobPath(reference.RecordId), MaxChunkRecordBytes);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(encrypted), reference.Sha256)) throw new InvalidDataException("History record does not match.");
        if (chunks.TryGetValue(reference.RecordId, out var cached) && CryptographicOperations.FixedTimeEquals(cached.Sha256, reference.Sha256) && cached.Chunk.FileId == fileId)
            return cached.Chunk;
        var context = new RecordContext(projectId, fileId, reference.RecordId, RecordKind.Document);
        bool segmented = SegmentedRecord.IsSegmented(encrypted);
        var plaintext = segmented ? SegmentedRecord.Decrypt(context, encrypted, null, keys.Get()) : RecordCodec.Decrypt(context, encrypted, protector);
        try
        {
            var chunk = ParseChunk(plaintext, fileId, segmented);
            Remember(reference.RecordId, reference.Sha256, chunk);
            return chunk;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private void Remember(Guid recordId, byte[] sha256, HistoryChunk chunk)
    {
        if (chunks.Count > 512) chunks.Clear();
        chunks[recordId] = (sha256, chunk);
    }

    private static byte[] SerializeChunk(HistoryChunk chunk) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 2, fileId = chunk.FileId.ToString("D"),
        entries = chunk.Entries.Select(e => new
        {
            revisionId = e.RevisionId.ToString("D"), recordSha256 = Convert.ToHexStringLower(e.RecordSha256), byteLength = e.ByteLength,
            name = e.Name, savedAt = e.SavedAt is { } at ? FormatTime(at) : null, savedBy = e.SavedBy, action = e.Action,
            kind = e.Kind == FileKind.Text ? "text" : "binary", sha256 = e.Sha256 is { } s ? Convert.ToHexStringLower(s) : null,
        }),
        older = chunk.Older is { } older ? new { recordId = older.RecordId.ToString("D"), recordSha256 = Convert.ToHexStringLower(older.Sha256) } : null,
    });

    private static HistoryChunk ParseChunk(byte[] plaintext, Guid fileId, bool segmented)
    {
        try
        {
            using var document = JsonDocument.Parse(plaintext, new JsonDocumentOptions { MaxDepth = 6 });
            var value = document.RootElement;
            Properties(value, "version", "fileId", "entries", "older");
            int version = value.GetProperty("version").GetInt32();
            // Version 2 chunks are SVR2 records; version 1 chunks are SVR1.
            if (version != (segmented ? 2 : 1) || ReadId(value.GetProperty("fileId")) != fileId) throw new InvalidDataException();
            var list = value.GetProperty("entries");
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is 0 or > MaxChunkEntries) throw new InvalidDataException();
            var entries = new List<HistoryEntry>();
            var revisions = new HashSet<Guid>();
            foreach (var item in list.EnumerateArray())
            {
                if (version == 1) Properties(item, "revisionId", "recordSha256", "byteLength", "name", "savedAt", "savedBy", "action");
                else Properties(item, "revisionId", "recordSha256", "byteLength", "name", "savedAt", "savedBy", "action", "kind", "sha256");
                var revision = ReadId(item.GetProperty("revisionId"));
                long length = item.GetProperty("byteLength").GetInt64();
                var kind = FileKind.Text; byte[]? sha256 = null;
                if (version == 2)
                {
                    kind = item.GetProperty("kind").GetString() switch { "text" => FileKind.Text, "binary" => FileKind.Binary, _ => throw new InvalidDataException() };
                    var s = item.GetProperty("sha256");
                    if (s.ValueKind != JsonValueKind.Null) sha256 = ReadSha256(s);
                }
                var name = item.GetProperty("name").GetString()!;
                ValidateName(name);
                var at = item.GetProperty("savedAt");
                DateTimeOffset? savedAt = at.ValueKind == JsonValueKind.Null ? null : ParseTime(at.GetString());
                var by = item.GetProperty("savedBy");
                string? savedBy = by.ValueKind == JsonValueKind.Null ? null : by.GetString();
                ValidateActor(savedBy);
                var action = item.GetProperty("action").GetString();
                // A revision saved before the upgrade (no plaintext hash) is SVR1 text of at most 256 KiB.
                long limit = sha256 is null ? MaximumLegacyDocumentBytes : MaximumFileBytes;
                if (action is not ("created" or "edited" or "imported" or "restored" or "reverted" or "before-history") || length < 0 || length > limit ||
                    (sha256 is null && kind != FileKind.Text)) throw new InvalidDataException();
                if ((action == "before-history") != (savedAt is null)) throw new InvalidDataException();
                // A restore or revert re-adds a revision that is already listed; any other repeat is invalid.
                if (!revisions.Add(revision) && action is not ("restored" or "reverted")) throw new InvalidDataException();
                entries.Add(new HistoryEntry(revision, ReadSha256(item.GetProperty("recordSha256")), length, name, savedAt, savedBy, action, kind, sha256));
            }
            var older = value.GetProperty("older");
            HistoryRef? olderRef = null;
            if (older.ValueKind != JsonValueKind.Null)
            {
                Properties(older, "recordId", "recordSha256");
                olderRef = new HistoryRef(ReadId(older.GetProperty("recordId")), ReadSha256(older.GetProperty("recordSha256")));
            }
            return new HistoryChunk(fileId, entries, olderRef);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException or InvalidDataException)
        { throw new InvalidDataException("Invalid encrypted history."); }
    }

    // UTC, millisecond precision, one canonical text form.
    private static DateTimeOffset Now()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
    }
    private static string FormatTime(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string? text)
    {
        if (text is null || !DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)) throw new InvalidDataException();
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    /// <summary>An account name for history: null, or 1-256 characters without control characters.</summary>
    private static void ValidateActor(string? actor)
    {
        if (actor is not null && (actor.Length is 0 or > MaxActorLength || actor.Any(char.IsControl))) throw new ArgumentException("Invalid actor.");
    }
}
