using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SourceVault.Packaging;

/// <summary>A key file is malformed. The message names the problem, never file contents.</summary>
public sealed class KeyFileException(string message) : Exception(message);

/// <summary>
/// A public recipient file (the vault PC's import key) or sender file (a source computer's signing key):
/// <c>{"format", "version": 1, "computer", "publicKey": base64 SubjectPublicKeyInfo, "fingerprint": hex SHA-256 of it}</c>.
/// Public by design; holds no secrets. The key must be RSA-2048 and the fingerprint must match it.
/// </summary>
public sealed record PublicKeyFile(string Format, string Computer, byte[] SubjectPublicKeyInfo)
{
    public const string RecipientFormat = "sourcevault-recipient";
    public const string SenderFormat = "sourcevault-sender";
    public const int MaxFileBytes = 8 * 1024;
    private static readonly Regex ComputerName = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$", RegexOptions.CultureInvariant);

    public byte[] Fingerprint => SHA256.HashData(SubjectPublicKeyInfo);

    /// <summary>Eight groups of eight hex digits, for people to compare on two screens.</summary>
    public string FingerprintText => Display(Fingerprint);

    /// <summary>
    /// The check code people compare on two screens (owner request 2026-10-20: shorter than the 64 hex digits): the
    /// first 96 bits of the fingerprint as six groups of four, "4F2A-91C0-77DE-0B13-AA42-9C5E". Making a key whose code
    /// matches a given one takes about 2^96 tries. Software always compares the full 256-bit fingerprint.
    /// </summary>
    public static string Display(ReadOnlySpan<byte> fingerprint)
    {
        var hex = Convert.ToHexString(fingerprint[..12]);
        return string.Join('-', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 4, 4)));
    }

    public static PublicKeyFile Create(string format, string computer, byte[] subjectPublicKeyInfo)
    {
        if (format is not (RecipientFormat or SenderFormat)) throw new KeyFileException("Unknown key file format.");
        if (!ComputerName.IsMatch(computer)) throw new KeyFileException("Computer name must be 1-63 letters, digits, '.', '-' or '_'.");
        using var rsa = ImportRsa(subjectPublicKeyInfo);
        return new(format, computer, subjectPublicKeyInfo.ToArray());
    }

    /// <summary>An RSA-2048 public key from exactly these SubjectPublicKeyInfo bytes, or a <see cref="KeyFileException"/>.</summary>
    public static RSA ImportRsa(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out int read);
            if (read != subjectPublicKeyInfo.Length || rsa.KeySize != 2048) throw new KeyFileException("The key must be an RSA-2048 public key.");
            return rsa;
        }
        catch (CryptographicException) { rsa.Dispose(); throw new KeyFileException("The key is not a valid RSA public key."); }
        catch { rsa.Dispose(); throw; }
    }

    public RSA ToRsa() => ImportRsa(SubjectPublicKeyInfo);

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
    {
        ["format"] = Format, ["version"] = 1, ["computer"] = Computer,
        ["publicKey"] = Convert.ToBase64String(SubjectPublicKeyInfo), ["fingerprint"] = Convert.ToHexStringLower(Fingerprint),
    }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Strict parse of a key file of the <paramref name="expectedFormat"/>.</summary>
    public static PublicKeyFile Parse(ReadOnlySpan<byte> json, string expectedFormat)
    {
        if (json.Length is 0 or > MaxFileBytes) throw new KeyFileException("The key file is empty or larger than 8 KiB.");
        JsonDocument document;
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(json);
            document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 2 });
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException) { throw new KeyFileException("The key file is not valid JSON."); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new KeyFileException("The key file must be a JSON object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!seen.Add(property.Name) || property.Name is not ("format" or "version" or "computer" or "publicKey" or "fingerprint"))
                    throw new KeyFileException($"Unexpected or repeated field '{property.Name}'.");
            if (seen.Count != 5) throw new KeyFileException("The key file must have format, version, computer, publicKey and fingerprint.");
            string Text(string name) => root.GetProperty(name).ValueKind == JsonValueKind.String ? root.GetProperty(name).GetString()! : throw new KeyFileException($"'{name}' must be text.");
            var format = Text("format");
            if (format != expectedFormat) throw new KeyFileException(expectedFormat == RecipientFormat ? "This is not a SourceVault recipient file." : "This is not a SourceVault sender file.");
            if (root.GetProperty("version").ValueKind != JsonValueKind.Number || !root.GetProperty("version").TryGetInt32(out int version) || version != 1)
                throw new KeyFileException("Unsupported key file version.");
            byte[] spki;
            try { spki = Convert.FromBase64String(Text("publicKey")); } catch (FormatException) { throw new KeyFileException("'publicKey' is not base64."); }
            var file = Create(format, Text("computer"), spki);
            if (!string.Equals(Text("fingerprint"), Convert.ToHexStringLower(file.Fingerprint), StringComparison.Ordinal))
                throw new KeyFileException("The fingerprint does not match the key.");
            return file;
        }
    }
}
