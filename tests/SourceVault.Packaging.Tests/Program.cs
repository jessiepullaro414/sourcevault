using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SourceVault.Packaging;

// Package formats v1 and v2 and key files (docs/package-import-design.md, docs/vault-any-file-design.md section 5.3). Synthetic content and ephemeral software RSA keys
// confined to this test assembly; production uses the vault PC's TPM import key and the source computer's TPM sender key.
int passed = 0;
void Assert(bool value, string what) { if (!value) throw new Exception("Assertion failed: " + what); }
void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
void Throws<T>(Action action, string what) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name + ": " + what);
}

using var recipientKey = RSA.Create(2048);
using var senderKey = RSA.Create(2048);
using var otherKey = RSA.Create(2048);
var recipient = PublicKeyFile.Create(PublicKeyFile.RecipientFormat, "VAULT-PC", recipientKey.ExportSubjectPublicKeyInfo());
var sender = PublicKeyFile.Create(PublicKeyFile.SenderFormat, "DEV-PC", senderKey.ExportSubjectPublicKeyInfo());
byte[] Sign(byte[] hash) => senderKey.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
byte[] Unwrap(byte[] wrapped) => recipientKey.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
RSA? Registered(byte[] fingerprint) => fingerprint.AsSpan().SequenceEqual(sender.Fingerprint) ? sender.ToRsa() : null;
byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
PackageContents Read(byte[] package) => Package.Read(package, recipient.Fingerprint, Registered, Unwrap);

List<(string, byte[])> Sample() =>
[
    ("README.md", Utf8("# Fictional sample\n")),
    ("src/app/Program.cs", Utf8("System.Console.WriteLine(\"synthetic\");\n")),
    ("empty.txt", []),
];
byte[] Pack(IReadOnlyList<(string, byte[])> files, string name = "Fictional sample")
{
    using var output = new MemoryStream();
    Package.Write(output, name, files, recipient, sender, Sign);
    return output.ToArray();
}
byte[] PackRaw(byte[] plaintext, Func<byte[], byte[]>? sign = null, PublicKeyFile? asSender = null, Func<long, long>? order = null)
{
    using var output = new MemoryStream();
    Package.WritePlaintext(output, plaintext, recipient, asSender ?? sender, sign ?? Sign, order);
    return output.ToArray();
}

// ---- key files ----------------------------------------------------------------------------------------------------
Test("key files: round trip, fingerprint display, and the expected format only", () =>
{
    var parsed = PublicKeyFile.Parse(recipient.ToJson(), PublicKeyFile.RecipientFormat);
    Assert(parsed.Computer == "VAULT-PC" && parsed.Fingerprint.AsSpan().SequenceEqual(recipient.Fingerprint), "round trip");
    Assert(parsed.FingerprintText.Split('-').Length == 6 && parsed.FingerprintText.Split('-').All(g => g.Length == 4 && g.All(Uri.IsHexDigit) && g == g.ToUpperInvariant()), "a check code of six groups of four");
    Assert(parsed.FingerprintText.Replace("-", "") == Convert.ToHexString(parsed.Fingerprint)[..24], "the first 96 bits of the fingerprint");
    Throws<KeyFileException>(() => PublicKeyFile.Parse(recipient.ToJson(), PublicKeyFile.SenderFormat), "recipient file used as a sender file");
});

Test("key files: tampered, extended, wrong-size or malformed files are refused", () =>
{
    // Each variant changes one field of a valid sender file (built from parsed JSON: the serializer escapes '+' in base64).
    string With(Action<System.Text.Json.Nodes.JsonObject> change)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(sender.ToJson())!.AsObject();
        change(node);
        return node.ToJsonString();
    }
    string[] bad =
    [
        With(o => o["fingerprint"] = Convert.ToHexStringLower(recipient.Fingerprint)), // fingerprint of another key
        With(o => o["extra"] = 1),                                                       // unknown field
        With(o => o["version"] = 2),                                                     // version
        With(o => o["computer"] = "DEV PC!"),                                            // computer name
        With(o => o["publicKey"] = "not base64!"),                                       // key encoding
        With(o => o.Remove("computer")),                                                 // missing field
        "{\"format\":\"sourcevault-sender\",\"format\":\"sourcevault-sender\"}",         // duplicate
        "[]", "not json", "",
    ];
    Assert(PublicKeyFile.Parse(Encoding.UTF8.GetBytes(With(_ => { })), PublicKeyFile.SenderFormat).Computer == "DEV-PC", "control: the unchanged file parses");
    foreach (var text in bad) Throws<KeyFileException>(() => PublicKeyFile.Parse(Encoding.UTF8.GetBytes(text), PublicKeyFile.SenderFormat), text.Length > 40 ? text[..40] : text);
    using var big = RSA.Create(3072);
    Throws<KeyFileException>(() => PublicKeyFile.Create(PublicKeyFile.SenderFormat, "X", big.ExportSubjectPublicKeyInfo()), "RSA-3072");
    Throws<KeyFileException>(() => PublicKeyFile.Parse(new byte[PublicKeyFile.MaxFileBytes + 1], PublicKeyFile.SenderFormat), "oversize");
});

// ---- packages -----------------------------------------------------------------------------------------------------
Test("package: round trip keeps names, contents, order, id and sender; names and contents are not in the clear", () =>
{
    var package = Pack(Sample());
    using var contents = Read(package);
    Assert(contents.Files.Count == 3 && contents.Files[1].Path == "src/app/Program.cs" && contents.Files[2].Content.Length == 0, "files");
    Assert(Encoding.UTF8.GetString(contents.Files[0].Content) == "# Fictional sample\n" && contents.SuggestedName == "Fictional sample", "content and name");
    Assert(contents.SenderFingerprint.AsSpan().SequenceEqual(sender.Fingerprint) && contents.PackageId != Guid.Empty, "metadata");
    var raw = Encoding.Latin1.GetString(package);
    Assert(!raw.Contains("Program.cs") && !raw.Contains("synthetic") && !raw.Contains("Fictional"), "nothing readable in the package");
});

Test("package: several 1 MiB chunks round trip; every package uses a fresh key", () =>
{
    var files = Enumerable.Range(0, 9).Select(i => ($"part{i}.txt", Utf8(new string((char)('a' + i), 250 * 1024)))).ToList();
    var package = Pack(files);
    Assert(BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(356)) == 3, "three chunks");
    using (var contents = Read(package)) Assert(contents.Files.Select(f => f.Content.Length).All(n => n == 250 * 1024) && contents.Files[8].Content[0] == (byte)'i', "large round trip");
    var again = Pack(files);
    Assert(!package.AsSpan(100, 256).SequenceEqual(again.AsSpan(100, 256)) && !package.AsSpan(Package.HeaderBytes, 64).SequenceEqual(again.AsSpan(Package.HeaderBytes, 64)), "fresh key per package");
});

Test("package: a flipped bit anywhere is refused before anything is decrypted", () =>
{
    var package = Pack(Sample());
    int unwraps = 0;
    var positions = new[] { 0, 8, 10, 12, 28, 36, 68, 100, 355, 356, 360, Package.HeaderBytes, Package.HeaderBytes + 20, package.Length - Package.SignatureBytes - 1, package.Length - 1 };
    foreach (var at in positions)
    {
        var copy = package.ToArray(); copy[at] ^= 0x01;
        Throws<Exception>(() => Package.Read(copy, recipient.Fingerprint, Registered, w => { unwraps++; return Unwrap(w); }).Dispose(), "bit at " + at);
    }
    Assert(unwraps == 0, "the TPM unwrap is never reached for an altered package");
});

Test("package: truncated, extended, oversized or empty input is refused", () =>
{
    var package = Pack(Sample());
    Throws<PackageRejectedException>(() => Read(package[..^1]), "one byte short");
    Throws<PackageRejectedException>(() => Read([.. package, 0]), "one byte extra");
    Throws<PackageRejectedException>(() => Read([]), "empty");
    var huge = package.ToArray();
    BinaryPrimitives.WriteInt64LittleEndian(huge.AsSpan(360), Package.MaxPlaintextBytes + 1);
    Throws<PackageRejectedException>(() => Read(huge), "declared length over the limit");
});

Test("package: another recipient, an unregistered sender and a forged signature are refused", () =>
{
    var package = Pack(Sample());
    Throws<PackageRejectedException>(() => Package.Read(package, SHA256.HashData(otherKey.ExportSubjectPublicKeyInfo()), Registered, Unwrap), "another recipient");
    var stranger = PublicKeyFile.Create(PublicKeyFile.SenderFormat, "STRANGER", otherKey.ExportSubjectPublicKeyInfo());
    var fromStranger = PackRaw(Package.Plaintext("x", Sample()), h => otherKey.SignHash(h, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), stranger);
    try { Read(fromStranger); throw new Exception("accepted an unregistered sender"); }
    catch (UnknownSenderException e) { Assert(e.SenderFingerprint.AsSpan().SequenceEqual(stranger.Fingerprint), "reports which sender"); }
    var forged = PackRaw(Package.Plaintext("x", Sample()), h => otherKey.SignHash(h, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)); // claims the registered sender
    Throws<PackageRejectedException>(() => Read(forged), "signature by another key");
    var pkcs1 = PackRaw(Package.Plaintext("x", Sample()), h => senderKey.SignHash(h, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    Throws<PackageRejectedException>(() => Read(pkcs1), "PKCS#1 v1.5 instead of PSS");
    Throws<PackageRejectedException>(() => Package.Read(package, recipient.Fingerprint, Registered, _ => throw new CryptographicException()), "unwrap failure");
});

Test("package: chunks reordered by a registered sender are refused by their associated data", () =>
{
    var files = Enumerable.Range(0, 9).Select(i => ($"part{i}.txt", Utf8(new string('z', 233017)))).ToList(); // 3 chunks: two full
    var plaintext = Package.Plaintext("x", files);
    Assert(Package.ChunkCount(plaintext.Length) == 3, "three chunks");
    var swapped = PackRaw(plaintext, order: i => i switch { 0 => 1, 1 => 0, _ => i });
    Throws<PackageRejectedException>(() => Read(swapped), "chunks 0 and 1 swapped and re-signed");
});

Test("package: contents that break the vault's rules are refused even when correctly signed", () =>
{
    byte[] Body(Action<BinaryWriter> write) { using var m = new MemoryStream(); using (var w = new BinaryWriter(m)) write(w); return m.ToArray(); }
    void File(BinaryWriter w, string path, byte[] content, byte[]? hash = null) { var p = Utf8(path); w.Write((ushort)p.Length); w.Write(p); w.Write((uint)content.Length); w.Write(hash ?? SHA256.HashData(content)); }
    byte[] One(string path, byte[] content, byte[]? hash = null, Action<BinaryWriter>? tail = null) => Body(w =>
    {
        w.Write((ushort)1); w.Write((ushort)0); w.Write((uint)1); File(w, path, content, hash); w.Write(content); tail?.Invoke(w);
    });
    Assert(Read(PackRaw(One("ok.txt", Utf8("fine\n")))).Files.Count == 1, "control: a valid raw body is accepted");
    var rejected = new Dictionary<string, byte[]>
    {
        ["traversal"] = One("../evil.txt", Utf8("x")),
        ["absolute"] = One("C:/evil.txt", Utf8("x")),
        ["reserved name"] = One("CON.txt", Utf8("x")),
        ["binary content"] = One("bin.dat", [0x00, 0xFF]),
        ["over 256 KiB"] = One("big.txt", Utf8(new string('a', 256 * 1024 + 1))),
        ["wrong hash"] = One("ok.txt", Utf8("fine\n"), new byte[32]),
        ["trailing bytes"] = One("ok.txt", Utf8("fine\n"), tail: w => w.Write((byte)0)),
        ["unknown version"] = Body(w => { w.Write((ushort)3); w.Write((ushort)0); w.Write((uint)0); }),
        ["no files"] = Body(w => { w.Write((ushort)1); w.Write((ushort)0); w.Write((uint)0); }),
        ["duplicate by case"] = Body(w => { w.Write((ushort)1); w.Write((ushort)0); w.Write((uint)2); File(w, "a.txt", Utf8("1")); File(w, "A.TXT", Utf8("2")); w.Write(Utf8("1")); w.Write(Utf8("2")); }),
        ["content cut short"] = Body(w => { w.Write((ushort)1); w.Write((ushort)0); w.Write((uint)1); File(w, "a.txt", Utf8("12345")); w.Write(Utf8("12")); }),
        ["too many files"] = Body(w => { w.Write((ushort)1); w.Write((ushort)0); w.Write((uint)(Package.MaxFiles + 1)); }),
    };
    foreach (var (what, body) in rejected) Throws<PackageRejectedException>(() => Read(PackRaw(body)), what);
    Throws<ArgumentException>(() => Pack([("../x", Utf8("x"))]), "the writer refuses bad names too");
    Throws<ArgumentException>(() => Pack([("a.txt", Utf8("1")), ("A.txt", Utf8("2"))]), "the writer refuses case duplicates");
});


// ---- version 2 (any file, streamed) ---------------------------------------------------------------------------------
Test("package v2: binary, UTF-16 and over-256 KiB files round trip; v2 still enforces names and duplicates", () =>
{
    var big = new byte[3 * 1024 * 1024 + 11]; new Random(5).NextBytes(big); // several chunks, crossing chunk boundaries
    var files = new List<(string, byte[])> { ("res/icon.ico", [0, 0, 1, 0, 0xFF]), ("res/app.rc", Encoding.Unicode.GetBytes("﻿IDS 1\r\n")), ("lib/big.bin", big), ("empty", []) };
    using var contents = Read(Pack(files));
    Assert(contents.Files.Count == 4 && contents.Files.Zip(files).All(z => z.First.Path == z.Second.Item1 && z.First.Content.SequenceEqual(z.Second.Item2)), "byte-exact");
    Assert(BinaryPrimitives.ReadUInt16LittleEndian(Pack(files).AsSpan(8)) == 2, "writers write version 2");
    byte[] V2(Action<BinaryWriter> write) { using var m = new MemoryStream(); using (var w = new BinaryWriter(m)) write(w); return m.ToArray(); }
    void Entry(BinaryWriter w, string path, byte[] content) { var b = Utf8(path); w.Write((ushort)b.Length); w.Write(b); w.Write((uint)content.Length); w.Write(SHA256.HashData(content)); }
    Throws<PackageRejectedException>(() => Read(PackRaw(V2(w => { w.Write((ushort)2); w.Write((ushort)0); w.Write((uint)1); Entry(w, "../x", [1]); w.Write((byte)1); }))), "v2 traversal");
    Throws<PackageRejectedException>(() => Read(PackRaw(V2(w => { w.Write((ushort)2); w.Write((ushort)0); w.Write((uint)2); Entry(w, "a", [1]); Entry(w, "A", [2]); w.Write((byte)1); w.Write((byte)2); }))), "v2 duplicate");
    Throws<PackageRejectedException>(() => Read(PackRaw(V2(w => { w.Write((ushort)2); w.Write((ushort)0); w.Write((uint)1); Entry(w, "a", [1]); w.Write((byte)9); }))), "v2 wrong hash");
});

Test("package v2: streamed from files on disk; a file changed after the scan stops the writer", () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "sv-pack-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
    try
    {
        var a = Path.Combine(folder, "a.bin"); var b = Path.Combine(folder, "b.bin");
        var aBytes = new byte[1536 * 1024]; new Random(1).NextBytes(aBytes); File.WriteAllBytes(a, aBytes);
        File.WriteAllBytes(b, Utf8("second\n"));
        PackageFile Of(string path, string name) => new(name, new FileInfo(path).Length, SHA256.HashData(File.ReadAllBytes(path)), () => File.OpenRead(path));
        var planned = new[] { Of(a, "a.bin"), Of(b, "dir/b.bin") };
        using (var output = new MemoryStream())
        {
            Package.Write(output, "Streamed", planned, recipient, sender, Sign);
            using var contents = Read(output.ToArray());
            Assert(contents.Files[0].Content.SequenceEqual(aBytes) && contents.Files[1].Path == "dir/b.bin", "streamed round trip");
        }
        File.WriteAllBytes(b, Utf8("changed\n")); // same length, different bytes
        Throws<PackageSourceChangedException>(() => Package.Write(new MemoryStream(), "Streamed", planned, recipient, sender, Sign), "changed file");
    }
    finally { Directory.Delete(folder, recursive: true); }
});

Test("package spool: the signature is checked against the hash taken on arrival; a spool changed afterwards is refused while streaming", () =>
{
    var files = Enumerable.Range(0, 3).Select(i => ($"f{i}.bin", Enumerable.Repeat((byte)i, 700 * 1024).ToArray())).ToList(); // 3 chunks
    var package = Pack(files);
    var signed = SHA256.HashData(package.AsSpan(0, package.Length - Package.SignatureBytes));
    // A wrong arrival hash: refused before anything is decrypted.
    int unwraps = 0;
    Throws<PackageRejectedException>(() => Package.Open(new MemoryStream(package), new byte[32], recipient.Fingerprint, Registered, w => { unwraps++; return Unwrap(w); }), "wrong arrival hash");
    Assert(unwraps == 0, "no unwrap before the signature");
    // Changed after the signature check (the last chunk's ciphertext): refused while streaming.
    var bytes = package.ToArray(); var spool = new MemoryStream(bytes); // shares the array, so a change below is a change to the spool
    using var reader = Package.Open(spool, signed, recipient.Fingerprint, Registered, Unwrap);
    Assert(reader.Files.Count == 3 && reader.Version == 2, "table read");
    bytes[package.Length - Package.SignatureBytes - 20] ^= 1;
    Throws<PackageRejectedException>(() => reader.ReadContents((_, _) => { }), "changed spool");
});

Console.WriteLine($"Packaging tests: {passed} passed.");
return 0;
