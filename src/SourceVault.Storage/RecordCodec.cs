using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SourceVault.Storage;

/// <summary>The bounded, context-bound v1 envelope format. No key protector fallback is provided.</summary>
public static class RecordCodec
{
    public const int FixedPrefixLength = 74;
    public const int WrappedKeyLength = 256;
    public const int TagLength = 16;
    public const int RecordOverhead = 346;
    public const int MaxDocumentLength = 262144;
    public const int MaxManifestLength = 1048576;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encrypt(RecordContext context, ReadOnlySpan<byte> plaintext, IKeyProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ValidateContext(context);
        if (plaintext.Length > MaximumLength(context.Kind))
            throw new ArgumentException("Record payload exceeds its limit.", nameof(plaintext));
        if (context.Kind == RecordKind.Document && !IsUtf8(plaintext))
            throw new ArgumentException("Document payload must be strict UTF-8.", nameof(plaintext));

        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[]? record = null;
        bool succeeded = false;
        try
        {
            byte[] wrapped = protector.WrapKey(key);
            if (wrapped is null || wrapped.Length != WrappedKeyLength)
                throw InvalidRecord();
            record = new byte[RecordOverhead + plaintext.Length];
            "SVR1"u8.CopyTo(record);
            record[4] = (byte)context.Kind;
            context.ProjectId.TryWriteBytes(record.AsSpan(8, 16), bigEndian: true, out _);
            context.FileId.TryWriteBytes(record.AsSpan(24, 16), bigEndian: true, out _);
            context.RevisionId.TryWriteBytes(record.AsSpan(40, 16), bigEndian: true, out _);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56, 2), WrappedKeyLength);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(58, 4), (uint)plaintext.Length);
            RandomNumberGenerator.Fill(record.AsSpan(62, 12));
            wrapped.CopyTo(record.AsSpan(FixedPrefixLength, WrappedKeyLength));
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(record.AsSpan(62, 12), plaintext, record.AsSpan(RecordOverhead),
                record.AsSpan(FixedPrefixLength + WrappedKeyLength, TagLength),
                record.AsSpan(0, FixedPrefixLength + WrappedKeyLength));
            succeeded = true;
            return record;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (!succeeded && record is not null) CryptographicOperations.ZeroMemory(record);
        }
    }

    public static byte[] Decrypt(RecordContext expectedContext, ReadOnlySpan<byte> record, IKeyProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ValidateContext(expectedContext);
        // Perform all structural and caller-context checks before allocation or key service access.
        if (record.Length < RecordOverhead || !record[..4].SequenceEqual("SVR1"u8) ||
            record[4] != (byte)expectedContext.Kind || record[5] != 0 || record[6] != 0 || record[7] != 0 ||
            new Guid(record.Slice(8, 16), bigEndian: true) != expectedContext.ProjectId ||
            new Guid(record.Slice(24, 16), bigEndian: true) != expectedContext.FileId ||
            new Guid(record.Slice(40, 16), bigEndian: true) != expectedContext.RevisionId ||
            BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(56, 2)) != WrappedKeyLength)
            throw InvalidRecord();
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(58, 4));
        if (length > MaximumLength(expectedContext.Kind) || (ulong)RecordOverhead + length != (ulong)record.Length)
            throw InvalidRecord();

        byte[]? key = null;
        byte[]? plaintext = null;
        bool succeeded = false;
        try
        {
            key = protector.UnwrapKey(record.Slice(FixedPrefixLength, WrappedKeyLength));
            if (key is null || key.Length != 32) throw InvalidRecord();
            plaintext = new byte[(int)length];
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(record.Slice(62, 12), record[RecordOverhead..],
                record.Slice(FixedPrefixLength + WrappedKeyLength, TagLength), plaintext,
                record[..(FixedPrefixLength + WrappedKeyLength)]);
            if (expectedContext.Kind == RecordKind.Document && !IsUtf8(plaintext)) throw InvalidRecord();
            succeeded = true;
            return plaintext;
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (!succeeded && plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void ValidateContext(RecordContext context)
    {
        if (context.ProjectId == Guid.Empty || context.RevisionId == Guid.Empty ||
            (context.Kind != RecordKind.Document && context.Kind != RecordKind.Manifest) ||
            (context.Kind == RecordKind.Document ? context.FileId == Guid.Empty : context.FileId != Guid.Empty))
            throw new ArgumentException("Invalid record context.", nameof(context));
    }

    private static int MaximumLength(RecordKind kind) => kind == RecordKind.Document ? MaxDocumentLength : MaxManifestLength;
    private static CryptographicException InvalidRecord() => new("Invalid encrypted record.");
    private static bool IsUtf8(ReadOnlySpan<byte> bytes)
    {
        try { _ = StrictUtf8.GetCharCount(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }
}
