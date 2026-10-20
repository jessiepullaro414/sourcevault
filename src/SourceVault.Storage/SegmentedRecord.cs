using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SourceVault.Storage;

/// <summary>
/// A project key (docs/vault-any-file-design.md, section 2.3, decision C2): a random AES-256 key that wraps the data keys
/// of a project's content and history records. It is stored TPM-wrapped in a <see cref="RecordKind.ProjectKey"/> record,
/// unwrapped at most once per store operation, and zeroed by <see cref="Dispose"/>. Never cache, log or send it.
/// </summary>
public sealed class ProjectKey : IDisposable
{
    public const int KeyLength = 32;
    private readonly byte[] key;
    private bool disposed;

    private ProjectKey(Guid id, byte[] key) { Id = id; this.key = key; }

    public Guid Id { get; }

    /// <summary>A new random project key and its TPM-wrapped record (kind ProjectKey, wrap mode 1, record id = key id).</summary>
    public static (ProjectKey Key, byte[] Record) Create(Guid projectId, IKeyProtector protector)
    {
        var id = Guid.NewGuid();
        var key = RandomNumberGenerator.GetBytes(KeyLength);
        try
        {
            var record = SegmentedRecord.Encrypt(new(projectId, Guid.Empty, id, RecordKind.ProjectKey), key, protector);
            return (new ProjectKey(id, key), record);
        }
        catch { CryptographicOperations.ZeroMemory(key); throw; }
    }

    /// <summary>Unwraps a project key record. The caller has already checked the record against the manifest.</summary>
    public static ProjectKey Open(Guid projectId, Guid keyId, ReadOnlySpan<byte> record, IKeyProtector protector)
    {
        var key = SegmentedRecord.Decrypt(new(projectId, Guid.Empty, keyId, RecordKind.ProjectKey), record, protector);
        if (key.Length != KeyLength) { CryptographicOperations.ZeroMemory(key); throw new CryptographicException("Invalid encrypted record."); }
        return new ProjectKey(keyId, key);
    }

    internal ReadOnlySpan<byte> Bytes
    {
        get { ObjectDisposedException.ThrowIf(disposed, this); return key; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CryptographicOperations.ZeroMemory(key);
    }
}

/// <summary>
/// Record format SVR2 (approved 2026-10-19, docs/vault-any-file-design.md section 2.2): a 68-byte prefix, a wrapped
/// data key, then the plaintext in 512 KiB AES-256-GCM segments. Segment i uses nonce 0⁴ ‖ i (uint64 LE) and AAD
/// SHA-256(prefix ‖ wrapped key) ‖ i (uint64 LE) ‖ last-segment flag, so truncation, reordering, splicing from another
/// record and a dropped last segment are all refused. Every record has a fresh random data key, wrapped by the TPM
/// (mode 1: manifests and project keys) or by the project key (mode 2: content and history). Readers check the exact
/// size before any unwrap, decrypt one segment at a time, and authenticate each segment before it is used.
/// </summary>
public static class SegmentedRecord
{
    public const int PrefixLength = 68;
    public const int SegmentLength = 524288;
    public const int TagLength = 16;
    public const int TpmWrappedKeyLength = 256;
    public const int ProjectWrappedKeyLength = 16 + 12 + 32 + TagLength;
    public const long MaxDocumentLength = 1L << 30;
    public const long MaxManifestLength = 32L << 20;
    private const byte WrapTpm = 1, WrapProject = 2;
    private static ReadOnlySpan<byte> Magic => "SVR2"u8;

    /// <summary>Segments in a record: an empty plaintext is one empty segment, so a truncated record is always detected.</summary>
    public static long SegmentCount(long plaintextLength) => plaintextLength == 0 ? 1 : (plaintextLength + SegmentLength - 1) / SegmentLength;

    /// <summary>The exact size of a record with this plaintext length and key wrap.</summary>
    public static long RecordLength(RecordKind kind, long plaintextLength) =>
        PrefixLength + WrappedKeyLength(kind) + plaintextLength + TagLength * SegmentCount(plaintextLength);

    /// <summary>Whether <paramref name="record"/> starts with the SVR2 magic (readers accept SVR1 and SVR2).</summary>
    public static bool IsSegmented(ReadOnlySpan<byte> record) => record.Length >= 4 && record[..4].SequenceEqual(Magic);

    /// <summary>Manifests and project keys are TPM-wrapped; documents (content and history) are wrapped by the project key.</summary>
    private static bool UsesProjectKey(RecordKind kind) => kind == RecordKind.Document;
    private static int WrappedKeyLength(RecordKind kind) => UsesProjectKey(kind) ? ProjectWrappedKeyLength : TpmWrappedKeyLength;
    private static long MaximumLength(RecordKind kind) => kind switch
    {
        RecordKind.Document => MaxDocumentLength,
        RecordKind.Manifest => MaxManifestLength,
        RecordKind.ProjectKey => ProjectKey.KeyLength,
        _ => throw new ArgumentException("Invalid record context."),
    };

    /// <summary>Encrypts a whole small record in memory (manifests, project keys, history chunks, small files).</summary>
    public static byte[] Encrypt(RecordContext context, ReadOnlySpan<byte> plaintext, IKeyProtector? protector, ProjectKey? projectKey = null)
    {
        ValidateContext(context);
        if (plaintext.Length > MaximumLength(context.Kind) || (context.Kind == RecordKind.ProjectKey && plaintext.Length != ProjectKey.KeyLength))
            throw new ArgumentException("Record payload exceeds its limit.", nameof(plaintext));
        var buffer = new MemoryStream((int)RecordLength(context.Kind, plaintext.Length));
        using (var writer = new SegmentedRecordWriter(context, plaintext.Length, buffer, protector, projectKey))
        {
            writer.Write(plaintext);
            writer.Complete();
        }
        return buffer.ToArray();
    }

    /// <summary>Decrypts a whole small record in memory. Prefer <see cref="SegmentedRecordReader"/> for large ones.</summary>
    public static byte[] Decrypt(RecordContext expected, ReadOnlySpan<byte> record, IKeyProtector? protector, ProjectKey? projectKey = null)
    {
        using var stream = new MemoryStream(record.ToArray(), writable: false);
        using var reader = new SegmentedRecordReader(expected, stream, protector, projectKey);
        if (reader.PlaintextLength > int.MaxValue) throw InvalidRecord();
        var plaintext = new byte[reader.PlaintextLength];
        bool succeeded = false;
        try
        {
            var segment = new byte[(int)Math.Min(SegmentLength, reader.PlaintextLength)];
            try
            {
                long offset = 0;
                for (int n; (n = reader.ReadNextSegment(segment)) > 0 || !reader.Finished;)
                {
                    segment.AsSpan(0, n).CopyTo(plaintext.AsSpan((int)offset));
                    offset += n;
                }
            }
            finally { CryptographicOperations.ZeroMemory(segment); }
            succeeded = true;
            return plaintext;
        }
        finally { if (!succeeded) CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal static void ValidateContext(RecordContext context)
    {
        if (context.ProjectId == Guid.Empty || context.RevisionId == Guid.Empty ||
            context.Kind is not (RecordKind.Document or RecordKind.Manifest or RecordKind.ProjectKey) ||
            (context.Kind == RecordKind.Document ? context.FileId == Guid.Empty : context.FileId != Guid.Empty))
            throw new ArgumentException("Invalid record context.", nameof(context));
    }

    internal static void WritePrefix(Span<byte> prefix, RecordContext context, long plaintextLength)
    {
        Magic.CopyTo(prefix);
        prefix[4] = (byte)context.Kind;
        prefix[5] = UsesProjectKey(context.Kind) ? WrapProject : WrapTpm;
        prefix[6] = 0; prefix[7] = 0;
        context.ProjectId.TryWriteBytes(prefix.Slice(8, 16), bigEndian: true, out _);
        context.FileId.TryWriteBytes(prefix.Slice(24, 16), bigEndian: true, out _);
        context.RevisionId.TryWriteBytes(prefix.Slice(40, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64LittleEndian(prefix.Slice(56, 8), (ulong)plaintextLength);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.Slice(64, 4), SegmentLength);
    }

    /// <summary>Checks a prefix against the caller's context; returns the plaintext length. No key is touched.</summary>
    internal static long CheckPrefix(ReadOnlySpan<byte> prefix, RecordContext expected)
    {
        if (prefix.Length != PrefixLength || !prefix[..4].SequenceEqual(Magic) || prefix[4] != (byte)expected.Kind ||
            prefix[5] != (UsesProjectKey(expected.Kind) ? WrapProject : WrapTpm) || prefix[6] != 0 || prefix[7] != 0 ||
            new Guid(prefix.Slice(8, 16), bigEndian: true) != expected.ProjectId ||
            new Guid(prefix.Slice(24, 16), bigEndian: true) != expected.FileId ||
            new Guid(prefix.Slice(40, 16), bigEndian: true) != expected.RevisionId ||
            BinaryPrimitives.ReadUInt32LittleEndian(prefix.Slice(64, 4)) != SegmentLength)
            throw InvalidRecord();
        ulong length = BinaryPrimitives.ReadUInt64LittleEndian(prefix.Slice(56, 8));
        if (length > (ulong)MaximumLength(expected.Kind) || (expected.Kind == RecordKind.ProjectKey && length != ProjectKey.KeyLength)) throw InvalidRecord();
        return (long)length;
    }

    // Mode 2: key id ‖ random nonce ‖ AES-256-GCM(project key, data key; AAD = prefix ‖ key id) ‖ tag.
    internal static byte[] WrapWithProjectKey(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> dataKey, ProjectKey projectKey)
    {
        var wrapped = new byte[ProjectWrappedKeyLength];
        projectKey.Id.TryWriteBytes(wrapped.AsSpan(0, 16), bigEndian: true, out _);
        RandomNumberGenerator.Fill(wrapped.AsSpan(16, 12));
        using var gcm = new AesGcm(projectKey.Bytes, TagLength);
        gcm.Encrypt(wrapped.AsSpan(16, 12), dataKey, wrapped.AsSpan(28, 32), wrapped.AsSpan(60, TagLength), WrapAad(prefix, wrapped.AsSpan(0, 16)));
        return wrapped;
    }

    internal static byte[] UnwrapWithProjectKey(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> wrapped, ProjectKey projectKey)
    {
        if (wrapped.Length != ProjectWrappedKeyLength || new Guid(wrapped[..16], bigEndian: true) != projectKey.Id) throw InvalidRecord();
        var dataKey = new byte[32];
        try
        {
            using var gcm = new AesGcm(projectKey.Bytes, TagLength);
            gcm.Decrypt(wrapped.Slice(16, 12), wrapped.Slice(28, 32), wrapped.Slice(60, TagLength), dataKey, WrapAad(prefix, wrapped[..16]));
            return dataKey;
        }
        catch (CryptographicException) { CryptographicOperations.ZeroMemory(dataKey); throw InvalidRecord(); }
    }

    private static byte[] WrapAad(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> keyId)
    {
        var aad = new byte[PrefixLength + 16];
        prefix.CopyTo(aad);
        keyId.CopyTo(aad.AsSpan(PrefixLength));
        return aad;
    }

    internal static byte[] HeaderHash(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> wrapped)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(prefix); hash.AppendData(wrapped);
        return hash.GetHashAndReset();
    }

    internal static void SegmentNonce(Span<byte> nonce, long index)
    {
        nonce[..4].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], (ulong)index);
    }

    internal static void SegmentAad(Span<byte> aad, ReadOnlySpan<byte> headerHash, long index, bool last)
    {
        headerHash.CopyTo(aad);
        BinaryPrimitives.WriteUInt64LittleEndian(aad.Slice(32, 8), (ulong)index);
        aad[40] = last ? (byte)1 : (byte)0;
    }

    internal static CryptographicException InvalidRecord() => new("Invalid encrypted record.");
}

/// <summary>
/// Writes one SVR2 record to a stream, one segment at a time: memory is one segment however large the plaintext.
/// The plaintext length is fixed up front (it is in the prefix); <see cref="Complete"/> refuses a short or long write.
/// An unfinished writer leaves an invalid record, which the caller deletes. Dispose zeroes the data key and buffer.
/// </summary>
public sealed class SegmentedRecordWriter : IDisposable
{
    private readonly Stream output;
    private readonly long plaintextLength;
    private readonly long segments;
    private readonly byte[] dataKey = RandomNumberGenerator.GetBytes(32);
    private readonly AesGcm gcm;
    private readonly byte[] headerHash;
    private readonly byte[] buffer;
    private readonly byte[] cipher;
    private int buffered;
    private long index;
    private long written;
    private bool completed, disposed;

    public SegmentedRecordWriter(RecordContext context, long plaintextLength, Stream output, IKeyProtector? protector, ProjectKey? projectKey = null)
    {
        SegmentedRecord.ValidateContext(context);
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        if (plaintextLength < 0) throw new ArgumentOutOfRangeException(nameof(plaintextLength));
        Span<byte> prefix = stackalloc byte[SegmentedRecord.PrefixLength];
        SegmentedRecord.WritePrefix(prefix, context, plaintextLength);
        _ = SegmentedRecord.CheckPrefix(prefix, context); // the same limits a reader enforces
        try
        {
            byte[] wrapped;
            if (context.Kind == RecordKind.Document)
                wrapped = SegmentedRecord.WrapWithProjectKey(prefix, dataKey, projectKey ?? throw new ArgumentException("A project key is required.", nameof(projectKey)));
            else
            {
                wrapped = (protector ?? throw new ArgumentNullException(nameof(protector))).WrapKey(dataKey);
                if (wrapped is null || wrapped.Length != SegmentedRecord.TpmWrappedKeyLength) throw SegmentedRecord.InvalidRecord();
            }
            headerHash = SegmentedRecord.HeaderHash(prefix, wrapped);
            output.Write(prefix);
            output.Write(wrapped);
            gcm = new AesGcm(dataKey, SegmentedRecord.TagLength);
        }
        catch { CryptographicOperations.ZeroMemory(dataKey); throw; }
        this.plaintextLength = plaintextLength;
        segments = SegmentedRecord.SegmentCount(plaintextLength);
        // One segment of memory, or less for a small record.
        buffer = new byte[(int)Math.Min(SegmentedRecord.SegmentLength, plaintextLength)];
        cipher = new byte[buffer.Length];
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed || written + data.Length > plaintextLength) throw new InvalidOperationException("More data than the record's length.");
        while (!data.IsEmpty)
        {
            int n = Math.Min(data.Length, buffer.Length - buffered);
            data[..n].CopyTo(buffer.AsSpan(buffered));
            buffered += n; written += n; data = data[n..];
            if (buffered == buffer.Length && written < plaintextLength) Flush(last: false);
        }
    }

    /// <summary>Writes the last segment. Throws if fewer bytes were written than the declared length.</summary>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed || written != plaintextLength) throw new InvalidOperationException("The record's length was not reached.");
        Flush(last: true);
        if (index != segments) throw new InvalidOperationException("Segment count mismatch.");
        completed = true;
    }

    private void Flush(bool last)
    {
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> aad = stackalloc byte[41];
        Span<byte> tag = stackalloc byte[SegmentedRecord.TagLength];
        SegmentedRecord.SegmentNonce(nonce, index);
        SegmentedRecord.SegmentAad(aad, headerHash, index, last);
        gcm.Encrypt(nonce, buffer.AsSpan(0, buffered), cipher.AsSpan(0, buffered), tag, aad);
        output.Write(cipher.AsSpan(0, buffered));
        output.Write(tag);
        CryptographicOperations.ZeroMemory(buffer.AsSpan(0, buffered));
        buffered = 0; index++;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        gcm?.Dispose();
        CryptographicOperations.ZeroMemory(dataKey);
        CryptographicOperations.ZeroMemory(buffer);
    }
}

/// <summary>
/// Reads one SVR2 record from a seekable stream. The constructor checks the prefix against the caller's context and the
/// stream's exact length before any key is unwrapped. Segments are authenticated before they are returned, in order
/// (<see cref="ReadNextSegment"/>) or by index (<see cref="ReadSegment"/>). Dispose zeroes the data key.
/// </summary>
public sealed class SegmentedRecordReader : IDisposable
{
    private readonly Stream input;
    private readonly long start;
    private readonly int wrappedLength;
    private readonly byte[] dataKey;
    private readonly AesGcm gcm;
    private readonly byte[] headerHash;
    private readonly byte[] cipher;
    private long next;
    private bool disposed;

    public SegmentedRecordReader(RecordContext expected, Stream input, IKeyProtector? protector, ProjectKey? projectKey = null)
    {
        SegmentedRecord.ValidateContext(expected);
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        if (!input.CanSeek || !input.CanRead) throw new ArgumentException("A seekable stream is required.", nameof(input));
        start = input.Position;
        Span<byte> prefix = stackalloc byte[SegmentedRecord.PrefixLength];
        if (input.Length - start < SegmentedRecord.PrefixLength) throw SegmentedRecord.InvalidRecord();
        input.ReadExactly(prefix);
        PlaintextLength = SegmentedRecord.CheckPrefix(prefix, expected);
        wrappedLength = expected.Kind == RecordKind.Document ? SegmentedRecord.ProjectWrappedKeyLength : SegmentedRecord.TpmWrappedKeyLength;
        // Exact size before any unwrap: no partial, padded or appended record reaches the key service.
        if (input.Length - start != SegmentedRecord.RecordLength(expected.Kind, PlaintextLength)) throw SegmentedRecord.InvalidRecord();
        Segments = SegmentedRecord.SegmentCount(PlaintextLength);
        var wrapped = new byte[wrappedLength];
        input.ReadExactly(wrapped);
        if (expected.Kind == RecordKind.Document)
            dataKey = SegmentedRecord.UnwrapWithProjectKey(prefix, wrapped, projectKey ?? throw new ArgumentException("A project key is required.", nameof(projectKey)));
        else
        {
            dataKey = (protector ?? throw new ArgumentNullException(nameof(protector))).UnwrapKey(wrapped);
            if (dataKey is null || dataKey.Length != 32) { if (dataKey is not null) CryptographicOperations.ZeroMemory(dataKey); throw SegmentedRecord.InvalidRecord(); }
        }
        headerHash = SegmentedRecord.HeaderHash(prefix, wrapped);
        gcm = new AesGcm(dataKey, SegmentedRecord.TagLength);
        cipher = new byte[(int)Math.Min(SegmentedRecord.SegmentLength, PlaintextLength) + SegmentedRecord.TagLength];
    }

    public long PlaintextLength { get; }
    public long Segments { get; }
    /// <summary>True once the last segment has been read by <see cref="ReadNextSegment"/>.</summary>
    public bool Finished => next == Segments;

    /// <summary>Decrypts the next segment into <paramref name="destination"/> (at least one segment long); returns its length.</summary>
    public int ReadNextSegment(Span<byte> destination)
    {
        if (Finished) return 0;
        int n = ReadSegment(next, destination);
        next++;
        return n;
    }

    /// <summary>Decrypts segment <paramref name="index"/> into <paramref name="destination"/>; returns its length.</summary>
    public int ReadSegment(long index, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (index < 0 || index >= Segments) throw new ArgumentOutOfRangeException(nameof(index));
        bool last = index == Segments - 1;
        int length = last ? (int)(PlaintextLength - index * SegmentedRecord.SegmentLength) : SegmentedRecord.SegmentLength;
        if (destination.Length < length) throw new ArgumentException("Destination too small.", nameof(destination));
        input.Position = start + SegmentedRecord.PrefixLength + wrappedLength + index * (SegmentedRecord.SegmentLength + SegmentedRecord.TagLength);
        var segment = cipher.AsSpan(0, length + SegmentedRecord.TagLength);
        input.ReadExactly(segment);
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> aad = stackalloc byte[41];
        SegmentedRecord.SegmentNonce(nonce, index);
        SegmentedRecord.SegmentAad(aad, headerHash, index, last);
        try { gcm.Decrypt(nonce, segment[..length], segment[length..], destination[..length], aad); }
        catch (CryptographicException) { CryptographicOperations.ZeroMemory(destination[..length]); throw SegmentedRecord.InvalidRecord(); }
        return length;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        gcm?.Dispose();
        if (dataKey is not null) CryptographicOperations.ZeroMemory(dataKey);
    }
}
