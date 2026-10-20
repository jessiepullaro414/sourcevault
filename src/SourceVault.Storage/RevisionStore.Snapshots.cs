using System.Security.Cryptography;

namespace SourceVault.Storage;

public sealed partial class RevisionStore
{
    /// <summary>
    /// Opens one snapshot for streaming, for a build copy (docs/vault-any-file-design.md section 6). The manifest is read
    /// and checked under the project lock, which is then released: revision records are immutable once written, so a long
    /// copy never blocks saves. Deleting the project meanwhile makes the reads fail. The project key is unwrapped at most
    /// once and held until <see cref="SnapshotReader.Dispose"/>; during the copy it can decrypt the whole project
    /// (decision C2).
    /// </summary>
    public SnapshotReader OpenSnapshot(Guid expectedSnapshotId)
    {
        Manifest manifest;
        using (AcquireLock())
        {
            manifest = ReadCurrent();
            if (manifest.Snapshot.SnapshotId != expectedSnapshotId) throw new RevisionConflictException();
        }
        var keys = new KeyScope(this, manifest);
        return new SnapshotReader(manifest.Snapshot.Files, file => ReadSegments(file, manifest.DocumentSha256[file.RevisionId], keys), keys);
    }

    /// <summary>A snapshot's files, each readable once as a stream of plaintext segments.</summary>
    public sealed class SnapshotReader : IDisposable
    {
        private readonly Func<StoreFile, IEnumerable<ArraySegment<byte>>> read;
        private readonly IDisposable keys;

        internal SnapshotReader(IReadOnlyList<StoreFile> files, Func<StoreFile, IEnumerable<ArraySegment<byte>>> read, IDisposable keys)
        {
            Files = files; this.read = read; this.keys = keys;
        }

        public IReadOnlyList<StoreFile> Files { get; }

        /// <summary>
        /// The file's plaintext, one segment (at most 512 KiB) at a time; each segment buffer is reused and zeroed, so use
        /// it before asking for the next. The record and plaintext hashes are checked after the last segment: a mismatch
        /// throws then, and everything received is to be discarded.
        /// </summary>
        public IEnumerable<ArraySegment<byte>> Read(StoreFile file) => read(file);

        public void Dispose() => keys.Dispose();
    }

    // SVR2: segment by segment through a hashing view of the record file. SVR1 (at most 256 KiB): one piece.
    private IEnumerable<ArraySegment<byte>> ReadSegments(StoreFile entry, byte[] recordSha256, KeyScope keys)
    {
        if (!entry.Segmented)
        {
            var legacy = ReadDocument(entry, recordSha256, keys);
            try { if (legacy.Length > 0) yield return legacy; }
            finally { CryptographicOperations.ZeroMemory(legacy); }
            yield break;
        }
        var path = BlobPath(entry.RevisionId);
        CheckStoreFile(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        if (file.Length != RecordSize(entry)) throw new InvalidDataException("Document record does not match the manifest.");
        using var hashing = new HashingReadStream(file);
        using var plain = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[(int)Math.Min(SegmentedRecord.SegmentLength, entry.ByteLength)];
        try
        {
            using (var reader = new SegmentedRecordReader(new(projectId, entry.FileId, entry.RevisionId, RecordKind.Document), hashing, null, keys.Get()))
            {
                if (reader.PlaintextLength != entry.ByteLength) throw new InvalidDataException("Document length mismatch.");
                while (!reader.Finished)
                {
                    int n = reader.ReadNextSegment(buffer);
                    plain.AppendData(buffer, 0, n);
                    if (n > 0) yield return new ArraySegment<byte>(buffer, 0, n);
                }
            }
            if (hashing.Position != file.Length || !CryptographicOperations.FixedTimeEquals(hashing.Hash(), recordSha256) ||
                !CryptographicOperations.FixedTimeEquals(plain.GetHashAndReset(), entry.Sha256))
                throw new InvalidDataException("Document record does not match the manifest.");
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
