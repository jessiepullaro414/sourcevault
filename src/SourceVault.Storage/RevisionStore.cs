using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SourceVault.Revisions.Tests")]
namespace SourceVault.Storage;

public sealed record StoreFile(Guid FileId, string LogicalName, Guid RevisionId, int ByteLength);
public sealed record StoreSnapshot(Guid SnapshotId, IReadOnlyList<StoreFile> Files);
public sealed class RevisionConflictException : Exception
{
    public RevisionConflictException() : base("Snapshot has changed.") { }
}

internal enum CommitStage { DocumentFlushed, ManifestFlushed, BeforeHeadPublish, HeadPublished }

/// <summary>Encrypted immutable revisions in a trusted service-owned directory.</summary>
public sealed class RevisionStore
{
    public const int MaximumFiles = 1024;
    private const int MaximumDocumentBytes = 262144;
    private const int MaximumManifestBytes = 1048576;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string root;
    private readonly Guid projectId;
    private readonly IKeyProtector protector;
    private readonly TimeSpan lockTimeout;
    internal Action<CommitStage>? FaultInjection { get; set; }

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
        WriteManifest(snapshot);
        Publish(snapshot.SnapshotId, false);
        return snapshot;
    }

    public StoreSnapshot List()
    {
        using var lease = AcquireLock();
        return ReadCurrent();
    }

    public byte[] Open(Guid expectedSnapshotId, Guid fileId)
    {
        using var lease = AcquireLock();
        var snapshot = ReadCurrent();
        if (snapshot.SnapshotId != expectedSnapshotId) throw new RevisionConflictException();
        var entry = snapshot.Files.SingleOrDefault(f => f.FileId == fileId) ?? throw new KeyNotFoundException("Document not found.");
        return ReadDocument(entry);
    }

    public StoreSnapshot Commit(Guid expectedSnapshotId, string logicalName, ReadOnlySpan<byte> utf8Content)
    {
        ValidateName(logicalName);
        ValidateContent(utf8Content);
        using var lease = AcquireLock();
        var current = ReadCurrent();
        if (current.SnapshotId != expectedSnapshotId) throw new RevisionConflictException();
        var existing = current.Files.SingleOrDefault(f => StringComparer.OrdinalIgnoreCase.Equals(f.LogicalName, logicalName));
        if (existing is null && current.Files.Count == MaximumFiles) throw new InvalidDataException("Document count exceeded.");
        var entry = new StoreFile(existing?.FileId ?? Guid.NewGuid(), logicalName, Guid.NewGuid(), utf8Content.Length);
        WriteBlob(entry.RevisionId, RecordCodec.Encrypt(new(projectId, entry.FileId, entry.RevisionId, RecordKind.Document), utf8Content, protector));
        FaultInjection?.Invoke(CommitStage.DocumentFlushed);
        var entries = current.Files.Where(f => f.FileId != entry.FileId).Append(entry).ToArray();
        var next = Snapshot(Guid.NewGuid(), entries);
        WriteManifest(next);
        FaultInjection?.Invoke(CommitStage.ManifestFlushed);
        Publish(next.SnapshotId, true);
        return next;
    }

    private static StoreSnapshot Snapshot(Guid id, IEnumerable<StoreFile> files) => new(id, Array.AsReadOnly(files.ToArray()));

    private StoreSnapshot ReadCurrent()
    {
        var pointer = ReadBounded(Path.Combine(root, "HEAD"), 36);
        if (pointer.Length != 36 || !TryId(Utf8.GetString(pointer), out var id)) throw new InvalidDataException("Invalid active pointer.");
        var encrypted = ReadBounded(BlobPath(id), MaximumManifestBytes + 346);
        var plaintext = RecordCodec.Decrypt(new(projectId, Guid.Empty, id, RecordKind.Manifest), encrypted, protector);
        try
        {
            var snapshot = ParseManifest(plaintext, id);
            // Every committed reference must authenticate, even for a metadata-only listing.
            foreach (var entry in snapshot.Files)
            {
                var content = ReadDocument(entry);
                CryptographicOperations.ZeroMemory(content);
            }
            return snapshot;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private byte[] ReadDocument(StoreFile entry)
    {
        var encrypted = ReadBounded(BlobPath(entry.RevisionId), MaximumDocumentBytes + 346);
        var content = RecordCodec.Decrypt(new(projectId, entry.FileId, entry.RevisionId, RecordKind.Document), encrypted, protector);
        try
        {
            if (content.Length != entry.ByteLength) throw new InvalidDataException("Document length mismatch.");
            ValidateContent(content);
            return content;
        }
        catch { CryptographicOperations.ZeroMemory(content); throw; }
    }

    private void WriteManifest(StoreSnapshot snapshot)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, snapshotId = snapshot.SnapshotId.ToString("D"),
            files = snapshot.Files.Select(f => new { fileId = f.FileId.ToString("D"), name = f.LogicalName, revisionId = f.RevisionId.ToString("D"), byteLength = f.ByteLength })
        });
        try { WriteBlob(snapshot.SnapshotId, RecordCodec.Encrypt(new(projectId, Guid.Empty, snapshot.SnapshotId, RecordKind.Manifest), plaintext, protector)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static StoreSnapshot ParseManifest(byte[] plaintext, Guid expectedId)
    {
        try
        {
            using var document = JsonDocument.Parse(plaintext, new JsonDocumentOptions { MaxDepth = 8 });
            var value = document.RootElement;
            Properties(value, "version", "snapshotId", "files");
            if (value.GetProperty("version").GetInt32() != 1 || ReadId(value.GetProperty("snapshotId")) != expectedId) throw new InvalidDataException();
            var files = value.GetProperty("files");
            if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > MaximumFiles) throw new InvalidDataException();
            var ids = new HashSet<Guid>(); var revisions = new HashSet<Guid>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<StoreFile>();
            foreach (var file in files.EnumerateArray())
            {
                Properties(file, "fileId", "name", "revisionId", "byteLength");
                var fileId = ReadId(file.GetProperty("fileId"));
                var revision = ReadId(file.GetProperty("revisionId"));
                var name = file.GetProperty("name").GetString()!;
                ValidateName(name);
                int length = file.GetProperty("byteLength").GetInt32();
                if (!ids.Add(fileId) || !revisions.Add(revision) || revision == expectedId || !names.Add(name) || length < 0 || length > MaximumDocumentBytes) throw new InvalidDataException();
                entries.Add(new(fileId, name, revision, length));
            }
            return Snapshot(expectedId, entries);
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

    private static void ValidateContent(ReadOnlySpan<byte> value)
    {
        if (value.Length > MaximumDocumentBytes) throw new ArgumentException("Document limit exceeded.");
        try { _ = Utf8.GetCharCount(value); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Invalid document encoding."); }
    }

    private static void ValidateName(string name)
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
    private byte[] ReadBounded(string path, int limit)
    {
        CheckAncestors(path);
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
