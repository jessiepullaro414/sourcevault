using System.Security.Cryptography;
using SourceVault.Packaging;
using SourceVault.Storage;

namespace SourceVault.Service;

/// <summary>
/// Encrypted packages from another computer (docs/package-import-design.md). Holds the vault PC's import key (a TPM
/// key separate from the vault key) and reads the administrator's registered senders, one file per sender in
/// <see cref="SendersFolder"/>, fresh for every import. One import runs at a time across the service.
/// </summary>
internal sealed class PackageImports(IKeyProtector importKey, byte[] importPublicKey, string computer, string sendersFolder)
{
    internal static string DefaultSendersFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SourceVault", "senders");
    internal static string ComputerName => SanitizeComputer(Environment.MachineName);
    internal string SendersFolder { get; } = sendersFolder;
    private readonly SemaphoreSlim gate = new(1, 1);

    internal PublicKeyFile Recipient { get; } = PublicKeyFile.Create(PublicKeyFile.RecipientFormat, computer, importPublicKey);

    /// <summary>Only one package upload/import at a time, so the service holds at most one package in memory.</summary>
    internal bool TryEnter() => gate.Wait(0);
    internal void Exit() => gate.Release();

    /// <summary>Checks, verifies and decrypts a package addressed to this PC. The caller disposes the result.</summary>
    internal PackageContents Open(ReadOnlySpan<byte> package) =>
        Package.Read(package, Recipient.Fingerprint, Sender, wrapped => importKey.UnwrapKey(wrapped));

    /// <summary>The registered sender with this fingerprint, or null. A file that is malformed or doesn't match its name counts as unregistered.</summary>
    internal RSA? Sender(byte[] fingerprint) => RegisteredSender(fingerprint)?.ToRsa();

    internal PublicKeyFile? RegisteredSender(byte[] fingerprint)
    {
        var path = Path.Combine(SendersFolder, Convert.ToHexStringLower(fingerprint) + ".json");
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > PublicKeyFile.MaxFileBytes) return null;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var file = PublicKeyFile.Parse(bytes, PublicKeyFile.SenderFormat);
            return file.Fingerprint.AsSpan().SequenceEqual(fingerprint) ? file : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyFileException) { return null; }
    }

    // Computer names go into public key files, which allow letters, digits, '.', '-' and '_'.
    internal static string SanitizeComputer(string name)
    {
        var clean = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').Take(63).ToArray());
        return clean.Length > 0 && char.IsAsciiLetterOrDigit(clean[0]) ? clean : "VAULT-PC";
    }
}
