using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SourceVault.Storage;

namespace SourceVault.Packaging;

/// <summary>Someone named in a disc record: the burner, or an approver (with the time they approved).</summary>
public sealed record DiscPerson(string Account, string Sid, DateTimeOffset? ApprovedUtc = null);

/// <summary>One project file on the disc: its path below <see cref="DiscRecords.SourceFolder"/>, length and SHA-256.</summary>
public sealed record DiscRecordFile(string Path, long Length, byte[] Sha256);

/// <summary>The signed record a finalized disc carries (docs/disc-export-design.md, section 4; contracts/disc-record-v1.md).</summary>
public sealed record DiscRecord(Guid DiscId, DateTimeOffset CreatedUtc, string Computer, byte[] SigningKeyFingerprint,
    string ProjectName, Guid SnapshotId, DiscPerson Burner, IReadOnlyList<DiscPerson> Approvers, IReadOnlyList<DiscRecordFile> Files);

/// <summary>A disc that is not what its record says. The message says what is wrong; it never holds file contents.</summary>
public sealed class DiscRecordException(string message) : Exception(message);

/// <summary>
/// Disc record format version 1: the exact UTF-8 bytes of <see cref="RecordFile"/> are signed with RSA-PSS SHA-256
/// (over their SHA-256) by the vault PC's disc signing key, a sign-only TPM key. There is no canonicalization: the
/// verifier checks the bytes as they are on the disc, then parses them strictly.
/// </summary>
public static class DiscRecords
{
    public const string Format = "sourcevault-disc";
    public const int Version = 1;
    public const string RecordFile = "SOURCEVAULT-DISC.json";
    public const string SignatureFile = "SOURCEVAULT-DISC.sig";
    public const string KeyFile = "SOURCEVAULT-DISC-KEY.der";
    public const string ReadmeFile = "README-SOURCEVAULT.txt";
    /// <summary>The project's files go in this folder at the disc's root; the record and its companions sit beside it.</summary>
    public const string SourceFolder = "source";
    public const int MaxRecordBytes = 32 * 1024 * 1024; // 25,000 files with long paths fit easily
    public const int MaxFiles = 25_000;
    public const int MaxAccountChars = 256;

    public static byte[] ToJson(DiscRecord record)
    {
        Validate(record);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            w.WriteNumber("version", Version);
            w.WriteString("discId", record.DiscId.ToString("D"));
            w.WriteString("createdUtc", Utc(record.CreatedUtc));
            w.WriteString("computer", record.Computer);
            w.WriteString("signingKey", Convert.ToHexStringLower(record.SigningKeyFingerprint));
            w.WriteStartObject("project");
            w.WriteString("name", record.ProjectName);
            w.WriteString("snapshotId", record.SnapshotId.ToString("D"));
            w.WriteEndObject();
            w.WriteStartObject("burner");
            w.WriteString("account", record.Burner.Account);
            w.WriteString("sid", record.Burner.Sid);
            w.WriteEndObject();
            w.WriteStartArray("approvers");
            foreach (var a in record.Approvers)
            {
                w.WriteStartObject();
                w.WriteString("account", a.Account);
                w.WriteString("sid", a.Sid);
                w.WriteString("approvedUtc", Utc(a.ApprovedUtc!.Value));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("files");
            foreach (var f in record.Files)
            {
                w.WriteStartObject();
                w.WriteString("path", f.Path);
                w.WriteNumber("length", f.Length);
                w.WriteString("sha256", Convert.ToHexStringLower(f.Sha256));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>Strict parse: exactly the version-1 properties, exactly two approvers, valid paths, no duplicates.</summary>
    public static DiscRecord Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length is 0 or > MaxRecordBytes) throw new DiscRecordException("The disc record is empty or too large.");
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            Exact(root, "format", "version", "discId", "createdUtc", "computer", "signingKey", "project", "burner", "approvers", "files");
            if (root.GetProperty("format").GetString() != Format || root.GetProperty("version").GetInt32() != Version) throw new DiscRecordException("This is not a version 1 SourceVault disc record.");
            var project = root.GetProperty("project"); Exact(project, "name", "snapshotId");
            var burner = root.GetProperty("burner"); Exact(burner, "account", "sid");
            var approvers = root.GetProperty("approvers").EnumerateArray().Select(a =>
            {
                Exact(a, "account", "sid", "approvedUtc");
                return new DiscPerson(a.GetProperty("account").GetString()!, a.GetProperty("sid").GetString()!, ParseUtc(a.GetProperty("approvedUtc").GetString()));
            }).ToList();
            var files = root.GetProperty("files").EnumerateArray().Select(f =>
            {
                Exact(f, "path", "length", "sha256");
                return new DiscRecordFile(f.GetProperty("path").GetString()!, f.GetProperty("length").GetInt64(), Convert.FromHexString(f.GetProperty("sha256").GetString()!));
            }).ToList();
            var record = new DiscRecord(Guid.ParseExact(root.GetProperty("discId").GetString()!, "D"), ParseUtc(root.GetProperty("createdUtc").GetString()),
                root.GetProperty("computer").GetString()!, Convert.FromHexString(root.GetProperty("signingKey").GetString()!),
                project.GetProperty("name").GetString()!, Guid.ParseExact(project.GetProperty("snapshotId").GetString()!, "D"),
                new DiscPerson(burner.GetProperty("account").GetString()!, burner.GetProperty("sid").GetString()!), approvers, files);
            Validate(record);
            return record;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new DiscRecordException("The disc record is not valid.");
        }
    }

    /// <summary>The signature: RSA-PSS SHA-256 over SHA-256 of the record's exact bytes, by <paramref name="signHash"/> (the TPM key).</summary>
    public static byte[] Sign(byte[] recordJson, Func<byte[], byte[]> signHash) => signHash(SHA256.HashData(recordJson));

    /// <summary>The volume label: "SV yyyy-mm-dd first+second" with the approvers' names without their domain, at most 32 characters.</summary>
    public static string VolumeLabel(DateTimeOffset createdUtc, IReadOnlyList<string> approverAccounts)
    {
        static string Short(string account) => new(account[(account.LastIndexOf('\\') + 1)..].Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        var label = $"SV {createdUtc.UtcDateTime:yyyy-MM-dd} {string.Join('+', approverAccounts.Select(Short))}";
        return label.Length <= 32 ? label : label[..32];
    }

    /// <summary>The plain-words file next to the record: what the disc is, who approved it, and how to verify it.</summary>
    public static string Readme(DiscRecord record) => string.Join("\r\n",
        "SourceVault disc",
        "================",
        "",
        $"This disc holds the saved files of the SourceVault project \"{record.ProjectName}\", written on {record.Computer} at {Utc(record.CreatedUtc)} (UTC).",
        $"Written by:   {record.Burner.Account}",
        $"Approved by:  {record.Approvers[0].Account} and {record.Approvers[1].Account}",
        $"Disc id:      {record.DiscId:D}",
        $"Signing key:  {PublicKeyFile.Display(record.SigningKeyFingerprint)}",
        "",
        "The files are in the \"source\" folder. SOURCEVAULT-DISC.json lists every one with its SHA-256, and",
        "SOURCEVAULT-DISC.sig is that list's signature by the disc signing key of the computer that wrote the disc.",
        "",
        "To check this disc, run SourceVault Pack on any Windows computer:",
        "    SourceVault.Pack.exe verify-disc <this drive> --check-code <the check code>",
        "Get the check code from that computer's SourceVault administrator, not from this disc: the key file here",
        "is only a convenience, and a disc made by someone else would carry their own key.",
        "",
        "This disc contains source code as ordinary, readable files. Keep it as safe as the code itself.",
        "");

    /// <summary>A verified disc (or folder copy of one): the record, once every check passed.</summary>
    public sealed record Verified(DiscRecord Record, string CheckCode);

    /// <summary>
    /// Checks a disc's root folder: the key's check code is <paramref name="checkCode"/> and is the key the record names;
    /// the signature is valid; and the source folder holds exactly the listed files, each with its length and SHA-256.
    /// Links are never followed. Throws <see cref="DiscRecordException"/> on the first problem.
    /// </summary>
    public static Verified Verify(string root, string checkCode)
    {
        byte[] Read(string name, int max)
        {
            var path = System.IO.Path.Combine(root, name);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new DiscRecordException($"{name} is missing.");
            if (new FileInfo(path).Length > max) throw new DiscRecordException($"{name} is too large.");
            return File.ReadAllBytes(path);
        }
        var json = Read(RecordFile, MaxRecordBytes);
        var signature = Read(SignatureFile, 1024);
        var key = Read(KeyFile, 1024);
        var fingerprint = SHA256.HashData(key);
        var expected = Normalize(checkCode);
        if (expected.Length != 24 || expected != Normalize(PublicKeyFile.Display(fingerprint)))
            throw new DiscRecordException("The disc's signing key does not have the check code you gave. Don't trust this disc's record.");
        using (var rsa = PublicKeyFile.ImportRsa(key))
            if (!rsa.VerifyHash(SHA256.HashData(json), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new DiscRecordException("The disc record's signature is not valid: the record was changed, or signed by another key.");
        var record = Parse(json);
        if (!CryptographicOperations.FixedTimeEquals(record.SigningKeyFingerprint, fingerprint)) throw new DiscRecordException("The record names a different signing key than the one on the disc.");

        var source = System.IO.Path.Combine(root, SourceFolder);
        var listed = record.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(source))
        {
            var pending = new Stack<string>(); pending.Push(source);
            while (pending.Count > 0)
            {
                var folder = pending.Pop();
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new DiscRecordException("The source folder contains a link.");
                foreach (var sub in Directory.EnumerateDirectories(folder)) pending.Push(sub);
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var relative = System.IO.Path.GetRelativePath(source, file).Replace('\\', '/');
                    if (!listed.TryGetValue(relative, out var entry)) throw new DiscRecordException($"{relative} is on the disc but not in its record.");
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new DiscRecordException($"{relative} is a link.");
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                    if (stream.Length != entry.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), entry.Sha256))
                        throw new DiscRecordException($"{relative} does not match its record.");
                    seen.Add(relative);
                }
            }
        }
        var missing = record.Files.FirstOrDefault(f => !seen.Contains(f.Path));
        if (missing is not null) throw new DiscRecordException($"{missing.Path} is in the record but not on the disc.");
        return new Verified(record, PublicKeyFile.Display(fingerprint));
    }

    private static string Normalize(string code) => new(code.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static void Validate(DiscRecord r)
    {
        bool Text(string? s, int max) => !string.IsNullOrWhiteSpace(s) && s.Length <= max && !s.Any(char.IsControl);
        bool Sid(string? s) => s is not null && s.StartsWith("S-1-", StringComparison.Ordinal) && s.Length <= 184 && s.All(c => char.IsAsciiDigit(c) || c is 'S' or '-');
        if (r.SigningKeyFingerprint.Length != 32 || !Text(r.Computer, 64) || !Text(r.ProjectName, 128)) throw new DiscRecordException("The disc record is not valid.");
        if (!Text(r.Burner.Account, MaxAccountChars) || !Sid(r.Burner.Sid)) throw new DiscRecordException("The disc record's burner is not valid.");
        if (r.Approvers.Count != 2 || r.Approvers.Any(a => !Text(a.Account, MaxAccountChars) || !Sid(a.Sid) || a.ApprovedUtc is null)
            || r.Approvers[0].Sid == r.Approvers[1].Sid || r.Approvers.Any(a => a.Sid == r.Burner.Sid))
            throw new DiscRecordException("A disc record needs two different approvers, neither of them the burner.");
        if (r.Files.Count > MaxFiles) throw new DiscRecordException("The disc record lists too many files.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in r.Files)
            if (!RevisionStore.IsValidLogicalName(f.Path) || f.Length < 0 || f.Sha256.Length != 32 || !paths.Add(f.Path))
                throw new DiscRecordException("The disc record lists an invalid or duplicate file.");
    }

    private static void Exact(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new JsonException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject()) if (!seen.Add(p.Name) || !names.Contains(p.Name)) throw new JsonException();
        if (seen.Count != names.Length) throw new JsonException();
    }

    private static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseUtc(string? s) => DateTimeOffset.ParseExact(s ?? "", "yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
}
