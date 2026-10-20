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

/// <summary>A source file changed between the scan and packing (its length or SHA-256 differs).</summary>
public sealed class PackageSourceChangedException(string path) : Exception($"{path} changed since the folder was scanned.")
{
    public string Path { get; } = path;
}

/// <summary>A decrypted, fully checked package held in memory. <see cref="Dispose"/> zeroes every file's content.</summary>
public sealed class PackageContents(Guid packageId, byte[] senderFingerprint, string suggestedName, IReadOnlyList<(string Path, byte[] Content)> files) : IDisposable
{
    public Guid PackageId { get; } = packageId;
    public byte[] SenderFingerprint { get; } = senderFingerprint;
    public string SuggestedName { get; } = suggestedName;
    public IReadOnlyList<(string Path, byte[] Content)> Files { get; } = files;
    public void Dispose() { foreach (var (_, content) in Files) CryptographicOperations.ZeroMemory(content); }
}

/// <summary>One file to pack: its vault path, exact length and SHA-256 from the scan, and how to open it for reading.</summary>
public sealed record PackageFile(string Path, long Length, byte[] Sha256, Func<Stream> Open);

/// <summary>One file listed in a package's table.</summary>
public sealed record PackageEntry(string Path, long Length, byte[] Sha256);

/// <summary>
/// Package file format (docs/package-import-design.md, "Implementation details"): a 368-byte header, AES-256-GCM chunks
/// of 1 MiB plaintext under a fresh per-package key wrapped with RSA-OAEP-SHA256 to the recipient, and an RSA-PSS-SHA256
/// sender signature over SHA-256(header ‖ chunks). Integers are little-endian. The plaintext is the file table, then
/// the files' contents in table order.
/// Version 2 (approved 2026-10-19, docs/vault-any-file-design.md section 5.3) keeps that construction and changes the
/// limits only: any bytes, up to 25,000 files, 1 GiB per file and 8 GiB in all. Writers write version 2; readers
/// accept 1 and 2. Both sides stream: neither ever holds the package in memory.
/// </summary>
public static class Package
{
    public const int HeaderBytes = 368;
    public const int SignatureBytes = 256;
    public const int ChunkBytes = 1024 * 1024;
    public const int TagBytes = 16;
    public const int MaxPathBytes = 1024;
    public const int MaxSuggestedNameBytes = 128;
    public const int MaxFiles = RevisionStore.MaximumFiles;          // version 2
    public const long MaxFileBytes = RevisionStore.MaximumFileBytes;  // version 2
    public const long MaxContentBytes = RevisionStore.MaximumProjectBytes;
    private const int MaxFilesV1 = 1024, MaxFileBytesV1 = 256 * 1024;
    public static readonly long MaxTableBytes = 2 + 2 + MaxSuggestedNameBytes + 4 + (long)MaxFiles * (2 + MaxPathBytes + 4 + 32);
    public static readonly long MaxPlaintextBytes = MaxTableBytes + MaxContentBytes;
    public static readonly long MaxPackageBytes = HeaderBytes + MaxPlaintextBytes + ChunkCount(MaxPlaintextBytes) * TagBytes + SignatureBytes;
    private static ReadOnlySpan<byte> Magic => "SVPKG1\0\0"u8;
    private const ushort Version = 2, FlagSigned = 1;

    internal static long ChunkCount(long plaintextLength) => (plaintextLength + ChunkBytes - 1) / ChunkBytes;

    // ---- writing (SourceVault Pack, on the source computer) ------------------------------------------------------

    /// <summary>
    /// Writes a version 2 package of <paramref name="files"/> for <paramref name="recipient"/>, signed by
    /// <paramref name="signHash"/> (RSA-PSS-SHA256 over a 32-byte SHA-256 digest, by the sender key whose public file is
    /// <paramref name="sender"/>). Files are streamed, one 1 MiB chunk in memory; each must still have the length and
    /// SHA-256 it was scanned with (<see cref="PackageSourceChangedException"/> otherwise). Returns the package id.
    /// </summary>
    public static Guid Write(Stream output, string suggestedName, IReadOnlyList<PackageFile> files, PublicKeyFile recipient, PublicKeyFile sender, Func<byte[], byte[]> signHash)
    {
        var table = Table(suggestedName, files.Select(f => new PackageEntry(f.Path, f.Length, f.Sha256)).ToList());
        try { return WriteStream(output, new PlaintextSource(table, files), table.Length + files.Sum(f => f.Length), Version, recipient, sender, signHash, null); }
        finally { CryptographicOperations.ZeroMemory(table); }
    }

    /// <summary>The same, for files already in memory (small packages and tests).</summary>
    public static Guid Write(Stream output, string suggestedName, IReadOnlyList<(string Path, byte[] Content)> files, PublicKeyFile recipient, PublicKeyFile sender, Func<byte[], byte[]> signHash) =>
        Write(output, suggestedName, files.Select(f => new PackageFile(f.Path, f.Content.LongLength, SHA256.HashData(f.Content), () => new MemoryStream(f.Content, writable: false))).ToList(),
            recipient, sender, signHash);

    // Encrypts and signs an already-built plaintext body. Tests use it to build packages with bad contents inside; the
    // header's version follows the body's first two bytes when they are 1 or 2.
    internal static Guid WritePlaintext(Stream output, byte[] plaintext, PublicKeyFile recipient, PublicKeyFile sender, Func<byte[], byte[]> signHash,
        Func<long, long>? chunkOrder = null)
    {
        ushort version = plaintext.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(plaintext) is 1 or 2 ? BinaryPrimitives.ReadUInt16LittleEndian(plaintext) : Version;
        return WriteStream(output, new PlaintextSource(plaintext, []), plaintext.Length, version, recipient, sender, signHash, chunkOrder, plaintext);
    }

    private static Guid WriteStream(Stream output, PlaintextSource source, long plaintextLength, ushort version, PublicKeyFile recipient, PublicKeyFile sender,
        Func<byte[], byte[]> signHash, Func<long, long>? chunkOrder, byte[]? whole = null)
    {
        if (recipient.Format != PublicKeyFile.RecipientFormat || sender.Format != PublicKeyFile.SenderFormat) throw new ArgumentException("Wrong key file kinds.");
        if (plaintextLength <= 0 || plaintextLength > MaxPlaintextBytes) throw new ArgumentException("Package too large.");
        var key = RandomNumberGenerator.GetBytes(32);
        var plain = new byte[ChunkBytes];
        try
        {
            var id = Guid.NewGuid();
            var header = new byte[HeaderBytes];
            Magic.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), version);
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
            long chunks = ChunkCount(plaintextLength);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(356), (uint)chunks);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(360), plaintextLength);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            output.Write(header); hash.AppendData(header);
            var headerHash = SHA256.HashData(header);
            using var gcm = new AesGcm(key, TagBytes);
            var buffer = new byte[ChunkBytes + TagBytes];
            for (long i = 0; i < chunks; i++)
            {
                int length = (int)Math.Min(ChunkBytes, plaintextLength - i * ChunkBytes);
                long index = chunkOrder?.Invoke(i) ?? i; // tests: encrypt chunk 'index' as if it were chunk i
                if (chunkOrder is null) source.Fill(plain.AsSpan(0, length));
                else
                {
                    if ((int)Math.Min(ChunkBytes, plaintextLength - index * ChunkBytes) != length) throw new ArgumentException("Chunk lengths differ.");
                    whole.AsSpan((int)(index * ChunkBytes), length).CopyTo(plain);
                }
                gcm.Encrypt(Nonce(index), plain.AsSpan(0, length), buffer.AsSpan(0, length), buffer.AsSpan(length, TagBytes), Aad(headerHash, index, index == chunks - 1));
                output.Write(buffer, 0, length + TagBytes); hash.AppendData(buffer, 0, length + TagBytes);
            }
            source.Finish();
            var signature = signHash(hash.GetHashAndReset());
            if (signature.Length != SignatureBytes) throw new CryptographicException("Unexpected signature length.");
            output.Write(signature);
            return id;
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); source.Dispose(); }
    }

    // The plaintext as a stream of chunks: the table, then each file read in order and checked against its scan.
    private sealed class PlaintextSource(byte[] table, IReadOnlyList<PackageFile> files) : IDisposable
    {
        private int tableAt, file = -1;
        private Stream? current;
        private long currentLeft;
        private IncrementalHash? currentHash;

        public void Fill(Span<byte> destination)
        {
            while (!destination.IsEmpty)
            {
                if (tableAt < table.Length)
                {
                    int n = Math.Min(destination.Length, table.Length - tableAt);
                    table.AsSpan(tableAt, n).CopyTo(destination); tableAt += n; destination = destination[n..];
                    continue;
                }
                if (current is null || currentLeft == 0) { NextFile(); continue; }
                int read = current.Read(destination[..(int)Math.Min(destination.Length, currentLeft)]);
                if (read == 0) throw new PackageSourceChangedException(files[file].Path);
                currentHash!.AppendData(destination[..read]);
                currentLeft -= read; destination = destination[read..];
            }
        }

        private void NextFile()
        {
            CloseFile();
            if (++file >= files.Count) throw new ArgumentException("More plaintext than the files hold.");
            current = files[file].Open();
            if (current.CanSeek && current.Length != files[file].Length) throw new PackageSourceChangedException(files[file].Path);
            currentLeft = files[file].Length;
            currentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        // Checks the file just finished: no bytes beyond its length, and the SHA-256 it was scanned with.
        private void CloseFile()
        {
            if (current is null) return;
            bool longer = current.ReadByte() != -1;
            current.Dispose(); current = null;
            var actual = currentHash!.GetHashAndReset(); currentHash.Dispose(); currentHash = null;
            if (longer || !CryptographicOperations.FixedTimeEquals(actual, files[file].Sha256)) throw new PackageSourceChangedException(files[file].Path);
        }

        public void Finish()
        {
            CloseFile();
            while (++file < files.Count) // trailing empty files have no bytes but are checked like the rest
            {
                current = files[file].Open(); currentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); currentLeft = 0;
                if (files[file].Length != 0) throw new PackageSourceChangedException(files[file].Path);
                CloseFile();
            }
        }

        public void Dispose() { current?.Dispose(); currentHash?.Dispose(); }
    }

    // The version 2 file table: version, suggested name, count, then per file path, length (uint32) and SHA-256.
    private static byte[] Table(string suggestedName, IReadOnlyList<PackageEntry> files)
    {
        var name = Encoding.UTF8.GetBytes(suggestedName);
        if (name.Length > MaxSuggestedNameBytes) throw new ArgumentException("Suggested name too long.");
        if (files.Count is 0 or > MaxFiles) throw new ArgumentException($"A package holds 1 to {MaxFiles} files.");
        var paths = files.Select(f => Encoding.UTF8.GetBytes(f.Path)).ToList();
        CheckEntries(Version, files, paths);
        var table = new byte[2 + 2 + name.Length + 4 + paths.Sum(p => 2 + p.Length + 4 + 32)];
        var span = table.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, Version); span = span[2..];
        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)name.Length); span = span[2..];
        name.CopyTo(span); span = span[name.Length..];
        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)files.Count); span = span[4..];
        for (int i = 0; i < files.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)paths[i].Length); span = span[2..];
            paths[i].CopyTo(span); span = span[paths[i].Length..];
            BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)files[i].Length); span = span[4..];
            files[i].Sha256.CopyTo(span); span = span[32..];
        }
        return table;
    }

    /// <summary>A whole version 2 plaintext in memory (tests build packages from it).</summary>
    internal static byte[] Plaintext(string suggestedName, IReadOnlyList<(string Path, byte[] Content)> files)
    {
        var table = Table(suggestedName, files.Select(f => new PackageEntry(f.Path, f.Content.LongLength, SHA256.HashData(f.Content))).ToList());
        var plaintext = new byte[table.Length + files.Sum(f => (long)f.Content.Length)];
        table.CopyTo(plaintext, 0);
        long at = table.Length;
        foreach (var (_, content) in files) { content.CopyTo(plaintext, at); at += content.Length; }
        return plaintext;
    }

    // Vault rules for every file, on both sides: names, limits, no case-insensitive duplicates. (Version 1 content must
    // also be text; that is checked while it is read.)
    private static void CheckEntries(int version, IReadOnlyList<PackageEntry> files, IReadOnlyList<byte[]> pathBytes)
    {
        long maxFile = version == 1 ? MaxFileBytesV1 : MaxFileBytes;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        for (int i = 0; i < files.Count; i++)
            if (pathBytes[i].Length is 0 or > MaxPathBytes || !RevisionStore.IsValidLogicalName(files[i].Path) || !seen.Add(files[i].Path)
                || files[i].Length < 0 || files[i].Length > maxFile || (total += files[i].Length) > MaxContentBytes)
                throw new ArgumentException("A file breaks the vault's rules.");
    }

    // ---- reading (the vault service) -----------------------------------------------------------------------------

    /// <summary>
    /// Checks a package spooled to <paramref name="spool"/> (seekable, the whole package) and opens it for streaming.
    /// <paramref name="signedHash"/> is SHA-256 of everything but the signature, taken while the package arrived.
    /// Order: exact size, header, recipient, registered sender (<paramref name="senderKey"/> returns null for an unknown
    /// fingerprint), signature over <paramref name="signedHash"/>; only then the TPM unwrap (<paramref name="unwrapKey"/>)
    /// and the decryption of the file table, which is checked whole. Throws <see cref="PackageRejectedException"/> or
    /// <see cref="UnknownSenderException"/>. The reader then streams the contents (<see cref="PackageReader.ReadContents"/>).
    /// </summary>
    public static PackageReader Open(Stream spool, byte[] signedHash, ReadOnlySpan<byte> recipientFingerprint, Func<byte[], RSA?> senderKey, Func<byte[], byte[]> unwrapKey)
    {
        if (!spool.CanSeek || !spool.CanRead) throw new ArgumentException("A seekable package is required.");
        long size = spool.Length;
        if (size < HeaderBytes + SignatureBytes || size > MaxPackageBytes || signedHash.Length != 32) throw new PackageRejectedException();
        var header = new byte[HeaderBytes];
        spool.Position = 0; spool.ReadExactly(header);
        int version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
        if (!header.AsSpan(0, 8).SequenceEqual(Magic) || version is not (1 or 2) || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10)) != FlagSigned) throw new PackageRejectedException();
        var id = new Guid(header.AsSpan(12, 16));
        long chunks = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(356));
        long plaintextLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(360));
        if (plaintextLength <= 0 || plaintextLength > MaxPlaintextBytes || chunks != ChunkCount(plaintextLength)
            || size != HeaderBytes + plaintextLength + chunks * TagBytes + SignatureBytes) throw new PackageRejectedException();
        if (!CryptographicOperations.FixedTimeEquals(header.AsSpan(36, 32), recipientFingerprint)) throw new PackageRejectedException();

        var senderFingerprint = header.AsSpan(68, 32).ToArray();
        using (var sender = senderKey(senderFingerprint) ?? throw new UnknownSenderException(senderFingerprint))
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(sender.ExportSubjectPublicKeyInfo()), senderFingerprint)) throw new PackageRejectedException();
            var signature = new byte[SignatureBytes];
            spool.Position = size - SignatureBytes; spool.ReadExactly(signature);
            if (!sender.VerifyHash(signedHash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw new PackageRejectedException();
        }

        byte[] key;
        try { key = unwrapKey(header.AsSpan(100, 256).ToArray()); }
        catch (Exception e) when (e is CryptographicException or ArgumentException) { throw new PackageRejectedException(); }
        if (key.Length != 32) { CryptographicOperations.ZeroMemory(key); throw new PackageRejectedException(); }
        var reader = new PackageReader(spool, header, key, version, chunks, plaintextLength, id, senderFingerprint, signedHash);
        try { reader.ReadTable(); return reader; }
        catch { reader.Dispose(); throw; }
    }

    /// <summary>Checks and decrypts a whole package held in memory (small packages and tests).</summary>
    public static PackageContents Read(ReadOnlySpan<byte> package, ReadOnlySpan<byte> recipientFingerprint, Func<byte[], RSA?> senderKey, Func<byte[], byte[]> unwrapKey)
    {
        if (package.Length < HeaderBytes + SignatureBytes || package.Length > MaxPackageBytes) throw new PackageRejectedException();
        var signed = SHA256.HashData(package[..^SignatureBytes]);
        using var spool = new MemoryStream(package.ToArray(), writable: false);
        using var reader = Open(spool, signed, recipientFingerprint, senderKey, unwrapKey);
        var files = reader.Files.Select(e => (e.Path, Content: new byte[e.Length])).ToList();
        try
        {
            int index = 0, at = 0;
            reader.ReadContents((_, piece) => { piece.CopyTo(files[index].Content.AsSpan(at)); at += piece.Length; }, _ => { index++; at = 0; });
            return new PackageContents(reader.PackageId, reader.SenderFingerprint, reader.SuggestedName, files);
        }
        catch { foreach (var (_, content) in files) CryptographicOperations.ZeroMemory(content); throw; }
    }

    internal static byte[] Nonce(long index)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(nonce.AsSpan(4), index);
        return nonce;
    }

    internal static byte[] Aad(byte[] headerHash, long index, bool last)
    {
        var aad = new byte[37];
        headerHash.CopyTo(aad, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(aad.AsSpan(32), (uint)index);
        aad[36] = last ? (byte)1 : (byte)0;
        return aad;
    }

    internal static void Check(int version, IReadOnlyList<PackageEntry> files, IReadOnlyList<byte[]> pathBytes)
    {
        try { CheckEntries(version, files, pathBytes); }
        catch (ArgumentException) { throw new PackageRejectedException(); }
    }
    internal static int MaxFilesFor(int version) => version == 1 ? MaxFilesV1 : MaxFiles;
}

/// <summary>
/// An opened package (signature checked, file table decrypted and checked). <see cref="ReadContents"/> streams each
/// file's bytes in table order, one 1 MiB chunk in memory: every chunk is GCM-checked, every file's SHA-256 compared with
/// the table, and the package re-hashed on the way so a spool changed since its signature was checked is refused.
/// Dispose zeroes the package key and buffers.
/// </summary>
public sealed class PackageReader : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Stream spool;
    private readonly byte[] key, headerHash, signedHash;
    private readonly int version;
    private readonly long chunks, plaintextLength;
    private readonly AesGcm gcm;
    private readonly IncrementalHash rehash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] cipher = new byte[Package.ChunkBytes + Package.TagBytes];
    private readonly byte[] plain = new byte[Package.ChunkBytes];
    private long chunk;          // next chunk to decrypt
    private int plainAt, plainLength; // unread bytes in 'plain'
    private bool read;

    internal PackageReader(Stream spool, byte[] header, byte[] key, int version, long chunks, long plaintextLength, Guid id, byte[] senderFingerprint, byte[] signedHash)
    {
        this.spool = spool; this.key = key; this.version = version; this.chunks = chunks; this.plaintextLength = plaintextLength; this.signedHash = signedHash;
        PackageId = id; SenderFingerprint = senderFingerprint;
        headerHash = SHA256.HashData(header);
        rehash.AppendData(header);
        gcm = new AesGcm(key, Package.TagBytes);
        spool.Position = Package.HeaderBytes;
    }

    public Guid PackageId { get; }
    public byte[] SenderFingerprint { get; }
    public string SuggestedName { get; private set; } = "";
    public int Version => version;
    public IReadOnlyList<PackageEntry> Files { get; private set; } = [];

    // Plaintext bytes in order, decrypting chunk by chunk.
    private void ReadPlain(Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            if (plainAt == plainLength)
            {
                if (chunk == chunks) throw new PackageRejectedException();
                int length = (int)Math.Min(Package.ChunkBytes, plaintextLength - chunk * Package.ChunkBytes);
                spool.ReadExactly(cipher.AsSpan(0, length + Package.TagBytes));
                rehash.AppendData(cipher, 0, length + Package.TagBytes);
                try { gcm.Decrypt(Package.Nonce(chunk), cipher.AsSpan(0, length), cipher.AsSpan(length, Package.TagBytes), plain.AsSpan(0, length), Package.Aad(headerHash, chunk, chunk == chunks - 1)); }
                catch (AuthenticationTagMismatchException) { throw new PackageRejectedException(); }
                chunk++; plainAt = 0; plainLength = length;
            }
            int n = Math.Min(destination.Length, plainLength - plainAt);
            plain.AsSpan(plainAt, n).CopyTo(destination); plainAt += n; destination = destination[n..];
        }
    }

    internal void ReadTable()
    {
        Span<byte> small = stackalloc byte[4];
        string Utf8(ReadOnlySpan<byte> b) { try { return StrictUtf8.GetString(b); } catch (DecoderFallbackException) { throw new PackageRejectedException(); } }
        ReadPlain(small[..2]);
        if (BinaryPrimitives.ReadUInt16LittleEndian(small) != version) throw new PackageRejectedException();
        ReadPlain(small[..2]);
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(small);
        if (nameLength > Package.MaxSuggestedNameBytes) throw new PackageRejectedException();
        var name = new byte[nameLength]; ReadPlain(name);
        SuggestedName = Utf8(name);
        ReadPlain(small);
        long count = BinaryPrimitives.ReadUInt32LittleEndian(small);
        if (count is 0 || count > Package.MaxFilesFor(version)) throw new PackageRejectedException();
        var entries = new List<PackageEntry>((int)count);
        var pathBytes = new List<byte[]>((int)count);
        long tableBytes = 2 + 2 + nameLength + 4;
        for (int i = 0; i < count; i++)
        {
            ReadPlain(small[..2]);
            int pathLength = BinaryPrimitives.ReadUInt16LittleEndian(small);
            if (pathLength is 0 or > Package.MaxPathBytes) throw new PackageRejectedException();
            var path = new byte[pathLength]; ReadPlain(path);
            ReadPlain(small);
            long length = BinaryPrimitives.ReadUInt32LittleEndian(small);
            var sha = new byte[32]; ReadPlain(sha);
            entries.Add(new PackageEntry(Utf8(path), length, sha)); pathBytes.Add(path);
            tableBytes += 2 + pathLength + 4 + 32;
        }
        Package.Check(version, entries, pathBytes);
        if (tableBytes + entries.Sum(e => e.Length) != plaintextLength) throw new PackageRejectedException();
        Files = entries;
    }

    /// <summary>
    /// Streams every file's content in table order: <paramref name="consume"/> gets each piece (valid only during the
    /// call), <paramref name="fileDone"/> each file once all its bytes arrived and its SHA-256 matched. Throws
    /// <see cref="PackageRejectedException"/> on any failure; whatever was consumed must then be discarded.
    /// </summary>
    public void ReadContents(Action<PackageEntry, ReadOnlySpan<byte>> consume, Action<PackageEntry>? fileDone = null)
    {
        if (read) throw new InvalidOperationException("The contents can be read once.");
        read = true;
        var piece = new byte[Package.ChunkBytes];
        try
        {
            foreach (var entry in Files)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[]? text = version == 1 ? new byte[entry.Length] : null; // version 1 files must be strict UTF-8
                try
                {
                    for (long left = entry.Length; left > 0;)
                    {
                        int n = (int)Math.Min(piece.Length, left);
                        ReadPlain(piece.AsSpan(0, n));
                        hash.AppendData(piece, 0, n);
                        if (text is not null) piece.AsSpan(0, n).CopyTo(text.AsSpan((int)(entry.Length - left)));
                        consume(entry, piece.AsSpan(0, n));
                        left -= n;
                    }
                    if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), entry.Sha256)) throw new PackageRejectedException();
                    if (text is not null && !RevisionStore.IsValidContent(text)) throw new PackageRejectedException();
                }
                finally { if (text is not null) CryptographicOperations.ZeroMemory(text); }
                fileDone?.Invoke(entry);
            }
            if (chunk != chunks || plainAt != plainLength) throw new PackageRejectedException();
            // Everything the signature covers was read again: it must be exactly what was signed.
            if (!CryptographicOperations.FixedTimeEquals(rehash.GetHashAndReset(), signedHash)) throw new PackageRejectedException();
        }
        finally { CryptographicOperations.ZeroMemory(piece); }
    }

    public void Dispose()
    {
        gcm.Dispose(); rehash.Dispose();
        CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(cipher);
    }
}
