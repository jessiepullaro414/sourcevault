using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SourceVault.Storage;

int passed = 0;
var failures = new List<string>();
void Check(string name, Action test)
{
    try { test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failures.Add(name); Console.WriteLine($"FAIL {name}: {e.GetType().Name}"); }
}
void Assert(bool value) { if (!value) throw new Exception("Assertion failed."); }
void Throws<T>(Action test) where T : Exception
{
    try { test(); } catch (T) { return; }
    throw new Exception("Expected rejection.");
}
bool Cleared(byte[]? value) => value is not null && value.All(b => b == 0);
using var protector = new TestRsaProtector();
var context = new RecordContext(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), Guid.NewGuid(), Guid.NewGuid(), RecordKind.Document);
var manifest = new RecordContext(context.ProjectId, Guid.Empty, Guid.NewGuid(), RecordKind.Manifest);
byte[] source = Encoding.UTF8.GetBytes("// synthetic only\nconst text = 'λ🙂';\n");
byte[] record = RecordCodec.Encrypt(context, source, protector);
void RejectBeforeUnwrap(byte[] candidate, RecordContext? expected = null)
{
    int calls = protector.UnwrapCalls;
    Throws<CryptographicException>(() => RecordCodec.Decrypt(expected ?? context, candidate, protector));
    Assert(protector.UnwrapCalls == calls);
}

Check("UTF-8 document roundtrip and unwrap key cleared", () =>
{
    Assert(RecordCodec.Decrypt(context, record, protector).SequenceEqual(source));
    Assert(Cleared(protector.LastUnwrapped));
});
Check("binary layout and RFC UUID order", () =>
{
    Assert(record.Length == RecordCodec.RecordOverhead + source.Length);
    Assert(record.AsSpan(0, 4).SequenceEqual("SVR1"u8));
    Assert(Convert.ToHexString(record.AsSpan(8, 16)) == "00112233445566778899AABBCCDDEEFF");
    Assert(BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(56, 2)) == 256);
    Assert(BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(58, 4)) == source.Length);
});
Check("empty document", () => Assert(RecordCodec.Decrypt(context, RecordCodec.Encrypt(context, [], protector), protector).Length == 0));
Check("maximum document", () =>
{
    byte[] input = new byte[RecordCodec.MaxDocumentLength];
    Assert(RecordCodec.Decrypt(context, RecordCodec.Encrypt(context, input, protector), protector).SequenceEqual(input));
});
Check("maximum raw binary manifest", () =>
{
    byte[] input = Enumerable.Repeat((byte)0xff, RecordCodec.MaxManifestLength).ToArray();
    Assert(RecordCodec.Decrypt(manifest, RecordCodec.Encrypt(manifest, input, protector), protector).SequenceEqual(input));
});
Check("fresh record keys and nonces", () =>
{
    byte[] first = RecordCodec.Encrypt(context, source, protector);
    byte[] firstKey = protector.LastWrappedKey!.ToArray();
    byte[] second = RecordCodec.Encrypt(context, source, protector);
    Assert(!firstKey.SequenceEqual(protector.LastWrappedKey!));
    Assert(!first.AsSpan(62, 12).SequenceEqual(second.AsSpan(62, 12)));
    CryptographicOperations.ZeroMemory(firstKey);
});
Check("oversized document rejected before wrap", () =>
{
    int calls = protector.WrapCalls;
    Throws<ArgumentException>(() => RecordCodec.Encrypt(context, new byte[RecordCodec.MaxDocumentLength + 1], protector));
    Assert(protector.WrapCalls == calls);
});
Check("oversized manifest rejected before wrap", () =>
{
    int calls = protector.WrapCalls;
    Throws<ArgumentException>(() => RecordCodec.Encrypt(manifest, new byte[RecordCodec.MaxManifestLength + 1], protector));
    Assert(protector.WrapCalls == calls);
});
foreach (byte[] invalidUtf8 in new byte[][] { [0xff], [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80], [0xe2, 0x82] })
    Check("invalid UTF-8 rejected before wrap " + Convert.ToHexString(invalidUtf8), () =>
    {
        int calls = protector.WrapCalls;
        Throws<ArgumentException>(() => RecordCodec.Encrypt(context, invalidUtf8, protector));
        Assert(protector.WrapCalls == calls);
    });

foreach (var invalid in new[] {
    context with { ProjectId = Guid.Empty }, context with { RevisionId = Guid.Empty },
    context with { FileId = Guid.Empty }, context with { Kind = (RecordKind)0 },
    context with { Kind = (RecordKind)255 }, manifest with { FileId = Guid.NewGuid() } })
    Check("invalid caller context " + (int)invalid.Kind + "/" + invalid.FileId, () =>
    {
        int calls = protector.UnwrapCalls + protector.WrapCalls;
        Throws<ArgumentException>(() => RecordCodec.Encrypt(invalid, source, protector));
        Throws<ArgumentException>(() => RecordCodec.Decrypt(invalid, record, protector));
        Assert(protector.UnwrapCalls + protector.WrapCalls == calls);
    });

foreach (int offset in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 23, 24, 39, 40, 55, 56, 57 })
    Check("header mutation rejected before unwrap at " + offset, () =>
    {
        byte[] changed = record.ToArray(); changed[offset] ^= 0x80;
        RejectBeforeUnwrap(changed);
    });
foreach (uint length in new[] { 0u, (uint)source.Length - 1, (uint)source.Length + 1, 262145u, uint.MaxValue })
    Check("declared length rejected before unwrap " + length, () =>
    {
        byte[] changed = record.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(58, 4), length);
        RejectBeforeUnwrap(changed);
    });
Check("all truncations rejected before unwrap", () =>
{
    for (int length = 0; length < record.Length; length++) RejectBeforeUnwrap(record[..length]);
});
Check("trailing data rejected before unwrap", () => RejectBeforeUnwrap([.. record, 0]));
foreach (var expected in new[] {
    context with { ProjectId = Guid.NewGuid() }, context with { FileId = Guid.NewGuid() },
    context with { RevisionId = Guid.NewGuid() }, manifest })
    Check("expected context mismatch rejected before unwrap " + expected.Kind + "/" + expected.RevisionId,
        () => RejectBeforeUnwrap(record, expected));

foreach (int offset in new[] { 62, 73, 74, 329, 330, 345, 346, record.Length - 1 })
    Check("authenticated region tamper rejected at " + offset, () =>
    {
        byte[] changed = record.ToArray(); changed[offset] ^= 1;
        protector.LastUnwrapped = null;
        Throws<CryptographicException>(() => RecordCodec.Decrypt(context, changed, protector));
        if (protector.LastUnwrapped is not null) Assert(Cleared(protector.LastUnwrapped));
    });
Check("wrong RSA key rejects wrapped key", () =>
{
    using var other = new TestRsaProtector();
    Throws<CryptographicException>(() => RecordCodec.Decrypt(context, record, other));
});
Check("wrapped-key bytes remain AAD even with permissive unwrap", () =>
{
    byte[] dataKey = protector.UnwrapKey(record.AsSpan(74, 256));
    var fixedKey = new FixedTestProtector(dataKey);
    byte[] changed = record.ToArray(); changed[74] ^= 1;
    Throws<CryptographicException>(() => RecordCodec.Decrypt(context, changed, fixedKey));
    Assert(Cleared(dataKey));
});
foreach (int size in new[] { 0, 16, 31, 33, 64 })
    Check("invalid unwrapped key length cleared " + size, () =>
    {
        byte[] key = Enumerable.Repeat((byte)0xa5, size).ToArray();
        Throws<CryptographicException>(() => RecordCodec.Decrypt(context, record, new FixedTestProtector(key)));
        Assert(Cleared(key));
    });
Check("null unwrapped key rejects", () => Throws<CryptographicException>(() => RecordCodec.Decrypt(context, record, new FixedTestProtector(null))));
Check("incorrect wrapping length rejects", () => Throws<CryptographicException>(() => RecordCodec.Encrypt(context, source, new FixedTestProtector(new byte[32]))));
Check("authenticated invalid UTF-8 document rejects", () =>
{
    // Construct an otherwise valid authenticated record externally to test post-authentication policy.
    byte[] input = [0xff];
    byte[] fixture = RecordCodec.Encrypt(context, new byte[1], protector);
    byte[] dataKey = protector.UnwrapKey(fixture.AsSpan(74, 256));
    using (var aes = new AesGcm(dataKey, 16))
        aes.Encrypt(fixture.AsSpan(62, 12), input, fixture.AsSpan(346), fixture.AsSpan(330, 16), fixture.AsSpan(0, 330));
    CryptographicOperations.ZeroMemory(dataKey);
    Throws<CryptographicException>(() => RecordCodec.Decrypt(context, fixture, protector));
    Assert(Cleared(protector.LastUnwrapped));
});
Check("null protectors reject", () =>
{
    Throws<ArgumentNullException>(() => RecordCodec.Encrypt(context, source, null!));
    Throws<ArgumentNullException>(() => RecordCodec.Decrypt(context, record, null!));
});

Console.WriteLine($"Record codec: {passed} passed, {failures.Count} failed.");
return failures.Count == 0 ? 0 : 1;

// Ephemeral software keys belong only to this synthetic test executable.
sealed class TestRsaProtector : IKeyProtector, IDisposable
{
    private readonly RSA rsa = RSA.Create(2048);
    public int WrapCalls { get; private set; }
    public int UnwrapCalls { get; private set; }
    public byte[]? LastWrappedKey { get; private set; }
    public byte[]? LastUnwrapped { get; set; }
    public byte[] WrapKey(ReadOnlySpan<byte> key)
    {
        WrapCalls++;
        if (LastWrappedKey is not null) CryptographicOperations.ZeroMemory(LastWrappedKey);
        LastWrappedKey = key.ToArray();
        return rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
    }
    public byte[] UnwrapKey(ReadOnlySpan<byte> wrappedKey)
    {
        UnwrapCalls++;
        LastUnwrapped = rsa.Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256);
        return LastUnwrapped;
    }
    public void Dispose()
    {
        if (LastWrappedKey is not null) CryptographicOperations.ZeroMemory(LastWrappedKey);
        if (LastUnwrapped is not null) CryptographicOperations.ZeroMemory(LastUnwrapped);
        rsa.Dispose();
    }
}

sealed class FixedTestProtector(byte[]? key) : IKeyProtector
{
    public byte[] WrapKey(ReadOnlySpan<byte> plaintextKey) => new byte[1];
    public byte[] UnwrapKey(ReadOnlySpan<byte> wrappedKey) => key!;
}
