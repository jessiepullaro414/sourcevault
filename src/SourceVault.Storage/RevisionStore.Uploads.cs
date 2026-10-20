using System.Security.Cryptography;
using System.Text;

namespace SourceVault.Storage;

/// <summary>An upload whose SVR2 record is complete in the staging folder, ready for <see cref="StoreChange.Stage"/>.</summary>
public sealed record StagedFile(Guid FileId, Guid RevisionId, long Length, FileKind Kind, byte[] Sha256, byte[] RecordSha256, string Path, bool NewFile, Guid ProjectKeyId);

/// <summary>The operation needs text and this revision is binary (protocol code NotText).</summary>
public sealed class NotTextException() : Exception("The revision is not text.");

/// <summary>
/// One upload in progress (docs/vault-any-file-design.md section 3.2). Its record context and data key are fixed when it
/// begins; the data key was wrapped by the project key, which was then zeroed. Plaintext arrives in order and is
/// encrypted one segment at a time straight to the staging folder, so only ciphertext is ever written. The service
/// hashes the plaintext as it goes and decides text or binary. Dispose deletes an unfinished staging file.
/// </summary>
public sealed class StoreUpload : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly FileStream file;
    private readonly HashingWriteStream output;
    private readonly SegmentedRecordWriter writer;
    private readonly IncrementalHash plaintext = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly Decoder utf8 = StrictUtf8.GetDecoder();
    private readonly byte[] expectedSha256;
    private readonly Guid projectKeyId;
    private bool binary, completed, disposed;

    internal StoreUpload(string path, RecordContext context, long length, byte[] expectedSha256, bool newFile, ProjectKey key)
    {
        Path = path; FileId = context.FileId; RevisionId = context.RevisionId; Length = length; NewFile = newFile;
        this.expectedSha256 = expectedSha256;
        projectKeyId = key.Id;
        file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan);
        try
        {
            output = new HashingWriteStream(file);
            writer = new SegmentedRecordWriter(context, length, output, null, key);
        }
        catch { file.Dispose(); File.Delete(path); throw; }
    }

    public string Path { get; }
    public Guid FileId { get; }
    public Guid RevisionId { get; }
    public long Length { get; }
    public bool NewFile { get; }
    public long Received { get; private set; }

    /// <summary>The next bytes, in order. Throws past the declared length.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed || Received + data.Length > Length) throw new InvalidOperationException("More data than the upload's length.");
        writer.Write(data);
        plaintext.AppendData(data);
        if (!binary)
        {
            if (data.IndexOf((byte)0) >= 0) binary = true;
            else try { _ = utf8.GetCharCount(data, flush: false); } catch (DecoderFallbackException) { binary = true; }
        }
        Received += data.Length;
    }

    /// <summary>
    /// After the last byte: finishes the record and checks the plaintext SHA-256 against the one given at the start.
    /// A mismatch throws <see cref="InvalidDataException"/> (the caller discards the upload).
    /// </summary>
    public StagedFile Complete()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed || Received != Length) throw new InvalidOperationException("The upload is not complete.");
        writer.Complete();
        file.Flush(true);
        var actual = plaintext.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actual, expectedSha256)) throw new InvalidDataException("The upload does not match its SHA-256.");
        if (!binary) try { _ = utf8.GetCharCount([], flush: true); } catch (DecoderFallbackException) { binary = true; }
        completed = true;
        var staged = new StagedFile(FileId, RevisionId, Length, binary ? FileKind.Binary : FileKind.Text, actual, output.Hash(), Path, NewFile, projectKeyId);
        writer.Dispose(); file.Dispose();
        return staged;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        writer.Dispose(); plaintext.Dispose(); file.Dispose();
        if (!completed) try { File.Delete(Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private sealed class HashingWriteStream(Stream inner) : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { hash.AppendData(buffer); inner.Write(buffer); }
        public byte[] Hash() => hash.GetHashAndReset();
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }
    }
}

public sealed partial class RevisionStore
{
    /// <summary>The editor's limit for text it opens, read in parts (decision O1).</summary>
    public const int MaximumEditorTextBytes = 1048576;

    /// <summary>
    /// Begins an upload of <paramref name="length"/> bytes into <paramref name="stagingFolder"/> (a service-owned folder
    /// on the vault volume): a new file, or the next revision of <paramref name="replaceFileId"/>. Checks the project, the
    /// size limits and free space. A store without a project key first gets one in a key-only commit, so the upload's
    /// data key is wrapped by the key its commit will reference (another client's next save then conflicts once).
    /// </summary>
    public StoreUpload BeginUpload(string stagingFolder, Guid? replaceFileId, long length, byte[] sha256)
    {
        if (!Path.IsPathFullyQualified(stagingFolder) || !Directory.Exists(stagingFolder)) throw new ArgumentException("Invalid staging folder.");
        if (sha256 is not { Length: 32 }) throw new ArgumentException("Invalid SHA-256.");
        if (length < 0 || length > MaximumFileBytes) throw new StoreLimitException("A file is larger than 1 GiB.");
        CheckAncestors(stagingFolder);
        using var lease = AcquireLock();
        var manifest = ReadCurrent();
        if (replaceFileId is { } replace && !manifest.Snapshot.Files.Any(f => f.FileId == replace)) throw new KeyNotFoundException("Document not found.");
        long kept = manifest.Snapshot.Files.Where(f => f.FileId != replaceFileId).Sum(f => f.ByteLength);
        if (kept + length > ProjectByteLimit) throw new StoreLimitException("Project size exceeded.");
        EnsureFreeSpace(length + (1L << 20));
        using var keys = new KeyScope(this, manifest);
        if (manifest.Key is null) PublishNext(manifest, Snapshot(Guid.NewGuid(), manifest.Snapshot.Files), manifest.DocumentSha256, manifest.History, manifest.Deleted, keys);
        var fileId = replaceFileId ?? Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var path = Path.Combine(stagingFolder, revisionId.ToString("D") + ".svr");
        return new StoreUpload(path, new(projectId, fileId, revisionId, RecordKind.Document), length, (byte[])sha256.Clone(), replaceFileId is null, keys.Get());
    }

    // A staged record must be exactly as completed: same size, same SHA-256, not a link, under this store's project key.
    private void CheckStaged(StagedFile staged, Manifest manifest)
    {
        CheckAncestors(staged.Path);
        var info = new FileInfo(staged.Path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length != SegmentedRecord.RecordLength(RecordKind.Document, staged.Length) ||
            manifest.Key is not { } key || key.Id != staged.ProjectKeyId)
            throw new InvalidDataException("A staged upload changed.");
        using var stream = new FileStream(staged.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), staged.RecordSha256)) throw new InvalidDataException("A staged upload changed.");
    }

    // A live or deleted file's revision: its current one or one from its history.
    private (StoreFile File, byte[] RecordSha256) FindRevision(Manifest manifest, Guid fileId, Guid revisionId, KeyScope keys)
    {
        var (file, historyRef) = Locate(manifest, fileId);
        if (file.RevisionId == revisionId) return (file, manifest.DocumentSha256[revisionId]);
        for (var next = historyRef; next is not null;)
        {
            var chunk = LoadChunk(fileId, next, keys);
            var entry = chunk.Entries.FirstOrDefault(e => e.RevisionId == revisionId);
            if (entry is not null) return (new StoreFile(fileId, entry.Name, entry.RevisionId, entry.ByteLength, entry.Kind, entry.Sha256), entry.RecordSha256);
            next = chunk.Older;
        }
        throw new KeyNotFoundException("Revision not found.");
    }

    /// <summary>One revision's metadata (name, length, kind, hash), from the current snapshot or the file's history.</summary>
    public StoreFile Revision(Guid fileId, Guid revisionId)
    {
        using var lease = AcquireLock();
        var manifest = ReadCurrent();
        using var keys = new KeyScope(this, manifest);
        return FindRevision(manifest, fileId, revisionId, keys).File;
    }

    /// <summary>
    /// One 512 KiB part of a text revision of at most 1 MiB (the editor's limit): <paramref name="offset"/> is a multiple
    /// of the segment size. A binary revision throws <see cref="NotTextException"/>, a larger one
    /// <see cref="StoreLimitException"/>. The whole record is checked against its hash before the part is decrypted.
    /// </summary>
    public (long Length, byte[] Data) ReadTextPart(Guid fileId, Guid revisionId, long offset)
    {
        using var lease = AcquireLock();
        var manifest = ReadCurrent();
        using var keys = new KeyScope(this, manifest);
        var (file, recordSha256) = FindRevision(manifest, fileId, revisionId, keys);
        if (file.Kind != FileKind.Text) throw new NotTextException();
        if (file.ByteLength > MaximumEditorTextBytes) throw new StoreLimitException("Text larger than the editor's limit.");
        if (offset < 0 || offset % SegmentedRecord.SegmentLength != 0 || (offset > 0 && offset >= file.ByteLength)) throw new ArgumentOutOfRangeException(nameof(offset));
        if (!file.Segmented) return (file.ByteLength, ReadDocument(file, recordSha256, keys)); // SVR1: at most 256 KiB, one part
        var encrypted = ReadBounded(BlobPath(file.RevisionId), SegmentedRecord.RecordLength(RecordKind.Document, MaximumEditorTextBytes));
        if (encrypted.Length != RecordSize(file) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(encrypted), recordSha256))
            throw new InvalidDataException("Document record does not match the manifest.");
        using var reader = new SegmentedRecordReader(new(projectId, fileId, file.RevisionId, RecordKind.Document), new MemoryStream(encrypted, writable: false), null, keys.Get());
        var part = new byte[(int)Math.Min(SegmentedRecord.SegmentLength, file.ByteLength - offset)];
        try { reader.ReadSegment(offset / SegmentedRecord.SegmentLength, part); return (file.ByteLength, part); }
        catch { CryptographicOperations.ZeroMemory(part); throw; }
    }

    /// <summary>
    /// Decrypts every live revision and checks it against the manifest (record and plaintext hashes). Costs about as much
    /// as a build. Returns counts only.
    /// </summary>
    public (int Verified, int Failed) VerifyProject()
    {
        using var lease = AcquireLock();
        var manifest = ReadCurrent();
        using var keys = new KeyScope(this, manifest);
        int verified = 0, failed = 0;
        foreach (var file in manifest.Snapshot.Files)
        {
            try
            {
                if (file.Segmented) ReadContent(file, manifest.DocumentSha256[file.RevisionId], keys, _ => { });
                else CryptographicOperations.ZeroMemory(ReadDocument(file, manifest.DocumentSha256[file.RevisionId], keys));
                verified++;
            }
            catch (Exception e) when (e is InvalidDataException or CryptographicException or IOException) { failed++; }
        }
        return (verified, failed);
    }

    /// <summary>Like <see cref="ReadSnapshot(Guid, Action{StoreFile, ReadOnlySpan{byte}})"/>, for the files <paramref name="include"/> accepts only.</summary>
    public void ReadSnapshot(Guid expectedSnapshotId, Func<StoreFile, bool> include, Action<StoreFile, ReadOnlySpan<byte>> consume)
    {
        ArgumentNullException.ThrowIfNull(include);
        ArgumentNullException.ThrowIfNull(consume);
        using var lease = AcquireLock();
        var manifest = ReadCurrent();
        if (manifest.Snapshot.SnapshotId != expectedSnapshotId) throw new RevisionConflictException();
        using var keys = new KeyScope(this, manifest);
        foreach (var entry in manifest.Snapshot.Files.Where(include))
        {
            var content = ReadDocument(entry, manifest.DocumentSha256[entry.RevisionId], keys);
            try { consume(entry, content); }
            finally { CryptographicOperations.ZeroMemory(content); }
        }
    }
}
