using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SourceVault.Storage;

namespace SourceVault.Packaging;

/// <summary>The package is malformed, altered, for another recipient, or its contents break the vault's rules. No detail is given.</summary>
public sealed class PackageRejectedException() : Exception("The package was rejected.");

/// <summary>The package's signature is from a computer that is not registered on this vault PC.</summary>
public sealed class UnknownSenderException(byte[] senderFingerprint) : Exception("The package's sender is not registered.")
{
    public byte[] SenderFingerprint { get; } = senderFingerprint;
}

/// <summary>A decrypted, fully checked package. <see cref="Dispose"/> zeroes every file's content.</summary>
public sealed class PackageContents(Guid packageId, byte[] senderFingerprint, string suggestedName, IReadOnlyList<(string Path, byte[] Content)> files) : IDisposable
{
    public Guid PackageId { get; } = packageId;
    public byte[] SenderFingerprint { get; } = senderFingerprint;
    public string SuggestedName { get; } = suggestedName;
    public IReadOnlyList<(string Path, byte[] Content)> Files { get; } = files;
    public void Dispose() { foreach (var (_, content) in Files) CryptographicOperations.ZeroMemory(content); }
}

/// <summary>
/// Package file format v1 (docs/package-import-design.md, "Implementation details"): a 368-byte header, AES-256-GCM
/// chunks of 1 MiB plaintext under a fresh per-package key wrapped with RSA-OAEP-SHA256 to the recipient, and an
/// RSA-PSS-SHA256 sender signature over SHA-256(header ‖ chunks). Integers are little-endian.
/// </summary>
public static class Package
{
    public const int HeaderBytes = 368;
    public const int SignatureBytes = 256;
    public const int ChunkBytes = 1024 * 1024;
    public const int TagBytes = 16;
    public const int MaxFiles = RevisionStore.MaximumFiles;
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxPathBytes = 1024;
    public const int MaxSuggestedNameBytes = 128;
    public static readonly long MaxPlaintextBytes = 2 + 2 + MaxSuggestedNameBytes + 4 + (long)MaxFiles * (2 + MaxPathBytes + 4 + 32 + MaxFileBytes);
    public static readonly long MaxPackageBytes = HeaderBytes + MaxPlaintextBytes + ChunkCount(MaxPlaintextBytes) * TagBytes + SignatureBytes;
    private static ReadOnlySpan<byte> Magic => "SVPKG1\0\0"u8;
    private const ushort Version = 1, FlagSigned = 1;

    internal static long ChunkCount(long plaintextLength) => (plaintextLength + ChunkBytes - 1) / ChunkBytes;

    // ---- writing (SourceVault Pack, on the source computer) ------------------------------------------------------

    /// <summary>
    /// Writes a package of <paramref name="files"/> for <paramref name="recipient"/>, signed by <paramref name="signHash"/>
    /// (RSA-PSS-SHA256 over a 32-byte SHA-256 digest, by the sender key whose public file is <paramref name="sender"/>).
    /// The files must already pass the vault's rules; they are checked again here. Returns the package id.
    /// </summary>
    public static Guid Write(Stream output, string suggestedName, IReadOnlyList<(string Path, byte[] Content)> files,
        PublicKeyFile recipient, PublicKeyFile sender, Func<byte[], byte[]> signHash)
    {
        var plaintext = Plaintext(suggestedName, files);
        try { return WritePlaintext(output, plaintext, recipient, sender, signHash); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    // Encrypts and signs an already-built plaintext body. Tests use it to build packages with bad contents inside.
    internal static Guid WritePlaintext(Stream output, byte[] plaintext, PublicKeyFile recipient, PublicKeyFile sender, Func<byte[], byte[]> signHash,
        Func<long, long>? chunkOrder = null)
    {
        if (recipient.Format != PublicKeyFile.RecipientFormat || sender.Format != PublicKeyFile.SenderFormat) throw new ArgumentException("Wrong key file kinds.");
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var id = Guid.NewGuid();
            var header = new byte[HeaderBytes];
            Magic.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), Version);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), FlagSigned);
            id.TryWriteBytes(header.AsSpan(12, 16));
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(28), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            recipient.Fingerprint.CopyTo(header.AsSpan(36));
            sender.Fingerprint.CopyTo(header.AsSpan(68));
            using (var rsa = recipient.ToRsa())
            {
                var wrapped = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
                if (wrapped.Length != 256) throw new CryptographicException();
                wrapped.CopyTo(header.AsSpan(100));
            }
            long chunks = ChunkCount(plaintext.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(356), (uint)chunks);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(360), plaintext.Length);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            output.Write(header); hash.AppendData(header);
            var headerHash = SHA256.HashData(header);
            using var gcm = new AesGcm(key, TagBytes);
            var buffer = new byte[ChunkBytes + TagBytes];
            for (long i = 0; i < chunks; i++)
            {
                int length = (int)Math.Min(ChunkBytes, plaintext.Length - i * ChunkBytes);
                var cipher = buffer.AsSpan(0, length); var tag = buffer.AsSpan(length, TagBytes);
                long source = chunkOrder?.Invoke(i) ?? i; // tests: encrypt chunk 'source' as if it were chunk i
                int sourceLength = (int)Math.Min(ChunkBytes, plaintext.Length - source * ChunkBytes);
                if (sourceLength != length) throw new ArgumentException("Chunk lengths differ.");
                gcm.Encrypt(Nonce(source), plaintext.AsSpan((int)(source * ChunkBytes), length), cipher, tag, Aad(headerHash, source, source == chunks - 1));
                output.Write(buffer, 0, length + TagBytes); hash.AppendData(buffer, 0, length + TagBytes);
            }
            var signature = signHash(hash.GetHashAndReset());
            if (signature.Length != SignatureBytes) throw new CryptographicException("Unexpected signature length.");
            output.Write(signature);
            return id;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal static byte[] Plaintext(string suggestedName, IReadOnlyList<(string Path, byte[] Content)> files)
    {
        var name = Encoding.UTF8.GetBytes(suggestedName);
        if (name.Length > MaxSuggestedNameBytes) throw new ArgumentException("Suggested name too long.");
        if (files.Count is 0 or > MaxFiles) throw new ArgumentException("A package holds 1 to 1024 files.");
        var paths = files.Select(f => Encoding.UTF8.GetBytes(f.Path)).ToList();
        CheckFiles(files.Select(f => f.Path).ToList(), files.Select(f => f.Content).ToList(), paths);
        long length = 2 + 2 + name.Length + 4 + paths.Sum(p => 2L + p.Length + 4 + 32) + files.Sum(f => (long)f.Content.Length);
        var plaintext = new byte[length];
        var span = plaintext.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, Version); span = span[2..];
        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)name.Length); span = span[2..];
        name.CopyTo(span); span = span[name.Length..];
        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)files.Count); span = span[4..];
        for (int i = 0; i < files.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)paths[i].Length); span = span[2..];
            paths[i].CopyTo(span); span = span[paths[i].Length..];
            BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)files[i].Content.Length); span = span[4..];
            SHA256.HashData(files[i].Content).CopyTo(span); span = span[32..];
        }
        foreach (var (_, content) in files) { content.CopyTo(span); span = span[content.Length..]; }
        return plaintext;
    }

    // Vault rules for every file, on both sides: names, text content, limits, no case-insensitive duplicates.
    private static void CheckFiles(IReadOnlyList<string> paths, IReadOnlyList<byte[]> contents, IReadOnlyList<byte[]> pathBytes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < paths.Count; i++)
            if (pathBytes[i].Length is 0 or > MaxPathBytes || !RevisionStore.IsValidLogicalName(paths[i]) || !seen.Add(paths[i])
                || contents[i].Length > MaxFileBytes || !RevisionStore.IsValidContent(contents[i]))
                throw new ArgumentException("A file breaks the vault's rules.");
    }

    // ---- reading (the vault service) -----------------------------------------------------------------------------

    /// <summary>
    /// Checks and decrypts a package addressed to <paramref name="recipientFingerprint"/>. Order: exact size, header,
    /// recipient, registered sender (<paramref name="senderKey"/> returns null for an unknown fingerprint), signature,
    /// and only then the TPM unwrap (<paramref name="unwrapKey"/>) and decryption; then every file is checked against the
    /// vault's rules and its hash. Throws <see cref="PackageRejectedException"/> or <see cref="UnknownSenderException"/>.
    /// </summary>
    public static PackageContents Read(ReadOnlySpan<byte> package, ReadOnlySpan<byte> recipientFingerprint,
        Func<byte[], RSA?> senderKey, Func<byte[], byte[]> unwrapKey)
    {
        if (package.Length < HeaderBytes + SignatureBytes || package.Length > MaxPackageBytes) throw new PackageRejectedException();
        var header = package[..HeaderBytes];
        if (!header[..8].SequenceEqual(Magic) || BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != Version
            || BinaryPrimitives.ReadUInt16LittleEndian(header[10..]) != FlagSigned) throw new PackageRejectedException();
        var id = new Guid(header.Slice(12, 16));
        long chunks = BinaryPrimitives.ReadUInt32LittleEndian(header[356..]);
        long plaintextLength = BinaryPrimitives.ReadInt64LittleEndian(header[360..]);
        if (plaintextLength <= 0 || plaintextLength > MaxPlaintextBytes || chunks != ChunkCount(plaintextLength)
            || package.Length != HeaderBytes + plaintextLength + chunks * TagBytes + SignatureBytes) throw new PackageRejectedException();
        if (!CryptographicOperations.FixedTimeEquals(header.Slice(36, 32), recipientFingerprint)) throw new PackageRejectedException();

        var senderFingerprint = header.Slice(68, 32).ToArray();
        using (var sender = senderKey(senderFingerprint) ?? throw new UnknownSenderException(senderFingerprint))
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(sender.ExportSubjectPublicKeyInfo()), senderFingerprint)) throw new PackageRejectedException();
            var signed = SHA256.HashData(package[..^SignatureBytes]);
            if (!sender.VerifyHash(signed, package[^SignatureBytes..], HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw new PackageRejectedException();
        }

        byte[] key;
        try { key = unwrapKey(header.Slice(100, 256).ToArray()); }
        catch (Exception e) when (e is CryptographicException or ArgumentException) { throw new PackageRejectedException(); }
        var plaintext = new byte[plaintextLength];
        try
        {
            if (key.Length != 32) throw new PackageRejectedException();
            var headerHash = SHA256.HashData(header);
            using (var gcm = new AesGcm(key, TagBytes))
            {
                var body = package[HeaderBytes..^SignatureBytes];
                for (long i = 0; i < chunks; i++)
                {
                    int length = (int)Math.Min(ChunkBytes, plaintextLength - i * ChunkBytes);
                    var chunk = body.Slice((int)(i * (ChunkBytes + TagBytes)), length + TagBytes);
                    try { gcm.Decrypt(Nonce(i), chunk[..length], chunk[length..], plaintext.AsSpan((int)(i * ChunkBytes), length), Aad(headerHash, i, i == chunks - 1)); }
                    catch (AuthenticationTagMismatchException) { throw new PackageRejectedException(); }
                }
            }
            return ParsePlaintext(plaintext, id, senderFingerprint);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static PackageContents ParsePlaintext(ReadOnlySpan<byte> plaintext, Guid id, byte[] senderFingerprint)
    {
        var strict = new UTF8Encoding(false, true);
        var span = plaintext;
        ReadOnlySpan<byte> Take(ref ReadOnlySpan<byte> s, int n) { if (n < 0 || s.Length < n) throw new PackageRejectedException(); var r = s[..n]; s = s[n..]; return r; }
        string Utf8(ReadOnlySpan<byte> b) { try { return strict.GetString(b); } catch (DecoderFallbackException) { throw new PackageRejectedException(); } }
        if (BinaryPrimitives.ReadUInt16LittleEndian(Take(ref span, 2)) != Version) throw new PackageRejectedException();
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(Take(ref span, 2));
        if (nameLength > MaxSuggestedNameBytes) throw new PackageRejectedException();
        var suggested = Utf8(Take(ref span, nameLength));
        long count = BinaryPrimitives.ReadUInt32LittleEndian(Take(ref span, 4));
        if (count is 0 or > MaxFiles) throw new PackageRejectedException();
        var entries = new List<(string Path, int Length, byte[] Hash, byte[] PathBytes)>();
        for (int i = 0; i < count; i++)
        {
            int pathLength = BinaryPrimitives.ReadUInt16LittleEndian(Take(ref span, 2));
            var pathBytes = Take(ref span, pathLength).ToArray();
            long length = BinaryPrimitives.ReadUInt32LittleEndian(Take(ref span, 4));
            if (length > MaxFileBytes) throw new PackageRejectedException();
            entries.Add((Utf8(pathBytes), (int)length, Take(ref span, 32).ToArray(), pathBytes));
        }
        var files = new List<(string Path, byte[] Content)>();
        try
        {
            foreach (var (path, length, hash, _) in entries)
            {
                var content = Take(ref span, length).ToArray();
                files.Add((path, content));
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(content), hash)) throw new PackageRejectedException();
            }
            if (!span.IsEmpty) throw new PackageRejectedException();
            try { CheckFiles(entries.Select(e => e.Path).ToList(), files.Select(f => f.Content).ToList(), entries.Select(e => e.PathBytes).ToList()); }
            catch (ArgumentException) { throw new PackageRejectedException(); }
            return new PackageContents(id, senderFingerprint, suggested, files);
        }
        catch { foreach (var (_, content) in files) CryptographicOperations.ZeroMemory(content); throw; }
    }

    private static byte[] Nonce(long index)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(nonce.AsSpan(4), index);
        return nonce;
    }

    private static byte[] Aad(byte[] headerHash, long index, bool last)
    {
        var aad = new byte[37];
        headerHash.CopyTo(aad, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(aad.AsSpan(32), (uint)index);
        aad[36] = last ? (byte)1 : (byte)0;
        return aad;
    }
}
