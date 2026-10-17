using System.Security.Cryptography;
using System.Text;
using SourceVault.Storage;

// An explicit synthetic test assembly, never referenced by the service host.
// Its ephemeral software RSA fixture is not a runtime fallback and is never persisted.
if (args is not [var suppliedRoot] || !Path.IsPathFullyQualified(suppliedRoot))
{
    Console.Error.WriteLine("Usage: SourceVault.Integration.Tests ABSOLUTE_TEST_WORKSPACE");
    return 2;
}
string root = Path.Combine(suppliedRoot, "integration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool condition) { if (!condition) throw new InvalidOperationException("Synthetic assertion failed"); }
void Pass(string name) { passed++; Console.WriteLine("PASS " + name); }
byte[] original = Encoding.UTF8.GetBytes("SOURCEVAULT_SYNTHETIC_ONLY_8a0a497167dd4feb9c7822b8a51c901d\nlet answer = 41;");
byte[] replacement = Encoding.UTF8.GetBytes("SOURCEVAULT_SYNTHETIC_REPLACEMENT_f851b526830b44fd9920d404137a29c0\nlet answer = 42;");
try
{
    using var protector = new SyntheticRsaFixture();
    Guid projectId = Guid.NewGuid();
    var store = new RevisionStore(root, projectId, protector);
    var empty = store.Initialize();
    Check(empty.SnapshotId != Guid.Empty && empty.Files.Count == 0);
    var first = store.Commit(empty.SnapshotId, "private-synthetic.ts", original);
    Check(first.SnapshotId != empty.SnapshotId && first.Files.Count == 1);
    Check(store.Open(first.SnapshotId, first.Files[0].FileId).AsSpan().SequenceEqual(original));
    Pass("create and read through encrypted revision API");

    var reopened = new RevisionStore(root, projectId, protector);
    var second = reopened.Commit(first.SnapshotId, "private-synthetic.ts", replacement);
    Check(second.Files[0].FileId == first.Files[0].FileId);
    Check(second.Files[0].RevisionId != first.Files[0].RevisionId);
    Check(new RevisionStore(root, projectId, protector).Open(second.SnapshotId, second.Files[0].FileId).AsSpan().SequenceEqual(replacement));
    Pass("edit and reopen preserve stable file and immutable revisions");

    bool conflict = false;
    try { store.Commit(first.SnapshotId, "private-synthetic.ts", original); }
    catch (RevisionConflictException) { conflict = true; }
    Check(conflict && store.List().SnapshotId == second.SnapshotId);
    Pass("stale editor cannot overwrite a committed revision");

    using var release = new ManualResetEventSlim(false);
    int successful = 0, conflicts = 0;
    Task Race(string name) => Task.Run(() =>
    {
        var contender = new RevisionStore(root, projectId, protector, TimeSpan.FromSeconds(5));
        release.Wait();
        try { contender.Commit(second.SnapshotId, name, original); Interlocked.Increment(ref successful); }
        catch (RevisionConflictException) { Interlocked.Increment(ref conflicts); }
    });
    var left = Race("synthetic-left.ts");
    var right = Race("synthetic-right.ts");
    release.Set();
    await Task.WhenAll(left, right);
    Check(successful == 1 && conflicts == 1 && store.List().Files.Count == 2);
    Pass("separate store instances serialize competing commits");

    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        byte[] data = File.ReadAllBytes(file);
        Check(data.AsSpan().IndexOf(original) < 0 && data.AsSpan().IndexOf(replacement) < 0);
        Check(data.AsSpan().IndexOf(Encoding.UTF8.GetBytes("private-synthetic.ts")) < 0);
        Check(!Path.GetFileName(file).Contains("private-synthetic", StringComparison.Ordinal));
    }
    Pass("tested store files contain no synthetic source or logical filename");

    bool denied = false;
    try { new RevisionStore(root, projectId, new UnavailableFixture()).List(); }
    catch (CryptographicException) { denied = true; }
    Check(denied);
    Pass("unavailable key protector fails without a fallback");

    Console.WriteLine($"Integration checks passed: {passed}; fixtures=synthetic-only; hardware-and-service-gates=unverified");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAIL integration (" + ex.GetType().Name + ")");
    return 1;
}
finally
{
    CryptographicOperations.ZeroMemory(original);
    CryptographicOperations.ZeroMemory(replacement);
}

sealed class SyntheticRsaFixture : IKeyProtector, IDisposable
{
    private readonly RSA rsa = RSA.Create(2048);
    private readonly object gate = new();
    public byte[] WrapKey(ReadOnlySpan<byte> key) { lock (gate) return rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256); }
    public byte[] UnwrapKey(ReadOnlySpan<byte> key) { lock (gate) return rsa.Decrypt(key, RSAEncryptionPadding.OaepSHA256); }
    public void Dispose() => rsa.Dispose();
}
sealed class UnavailableFixture : IKeyProtector
{
    public byte[] WrapKey(ReadOnlySpan<byte> key) => throw new CryptographicException("Unavailable");
    public byte[] UnwrapKey(ReadOnlySpan<byte> key) => throw new CryptographicException("Unavailable");
}
