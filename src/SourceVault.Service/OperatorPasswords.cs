using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SourceVault.Storage;

namespace SourceVault.Service;

/// <summary>
/// A SourceVault password per operator (owner decision 2026-10-20), checked by the service at the start of every
/// session, on top of Windows sign-in and the operators group. Hashes are PBKDF2-HMAC-SHA256 (600,000 iterations,
/// 16-byte salt, 32-byte hash) inside the vault's encrypted accounts store, never on disk in the clear.
/// <list type="bullet">
/// <item>An operator with no password, or whose password an administrator reset, sets one when starting a session.</item>
/// <item>Five wrong passwords in a row lock that operator out for 15 minutes, across every connection.</item>
/// <item>A reset is a file <c>&lt;resets folder&gt;\&lt;SID&gt;.json</c> = <c>{"resetId": "&lt;uuid&gt;"}</c>, which only
/// administrators can write (<c>Setup-SourceVault.ps1 -ResetPassword</c>). Each reset id is honoured once.</item>
/// </list>
/// The lockout counter lives in service memory: restarting the service, which needs an administrator, clears it.
/// </summary>
internal sealed class OperatorPasswords(ProjectCatalog catalog, string resetsFolder, TimeProvider? clock = null)
{
    internal const int MinLength = 12, MaxLength = 256, Iterations = 600_000, MaxFailures = 5;
    internal static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);
    internal static string DefaultResetsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SourceVault", "password-resets");
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, (int Failures, long LockedUntil, string? ResetSeen)> attempts = new();

    private sealed record Account(string Sid, byte[] Salt, byte[] Hash, int Iterations, string? ResetId);

    /// <summary>
    /// The protocol code for a session start: Ok, PasswordRequired, PasswordNotSet, WrongPassword, PasswordLocked or
    /// PasswordTooWeak. <paramref name="newPassword"/> sets the first (or a reset) password; otherwise
    /// <paramref name="password"/> must match.
    /// </summary>
    internal string BeginSession(SecurityIdentifier user, string? password, string? newPassword)
    {
        lock (gate)
        {
            var accounts = Load();
            var reset = PendingReset(user);
            var account = accounts.FirstOrDefault(a => a.Sid == user.Value);
            bool enrolled = account is not null && (reset is null || reset == account.ResetId);
            if (reset is not null) ClearLockoutOnNewReset(user, reset);
            if (!enrolled)
            {
                if (newPassword is null) return "PasswordNotSet";
                if (!Strong(newPassword)) return "PasswordTooWeak";
                accounts.RemoveAll(a => a.Sid == user.Value);
                accounts.Add(Hash(user.Value, newPassword, reset));
                Save(accounts);
                attempts.TryRemove(user.Value, out _);
                return "Ok";
            }
            if (newPassword is not null || password is null) return "PasswordRequired";
            return Verify(user, account!, password);
        }
    }

    /// <summary>Changes the caller's password: the current one must match (wrong ones count toward the lockout).</summary>
    internal string ChangePassword(SecurityIdentifier user, string password, string newPassword)
    {
        lock (gate)
        {
            var accounts = Load();
            var account = accounts.FirstOrDefault(a => a.Sid == user.Value);
            if (account is null) return "PasswordNotSet";
            var check = Verify(user, account, password);
            if (check != "Ok") return check;
            if (!Strong(newPassword)) return "PasswordTooWeak";
            accounts.Remove(account);
            accounts.Add(Hash(user.Value, newPassword, account.ResetId));
            Save(accounts);
            return "Ok";
        }
    }

    // 12-256 characters, no control characters. Length is what matters; no composition rules (NIST SP 800-63B).
    internal static bool Strong(string password) => password.Length is >= MinLength and <= MaxLength && !password.Any(char.IsControl);

    private string Verify(SecurityIdentifier user, Account account, string password)
    {
        long now = time.GetTimestamp();
        var state = attempts.GetValueOrDefault(user.Value);
        if (state.Failures >= MaxFailures && now < state.LockedUntil) return "PasswordLocked";
        if (password.Length > MaxLength) return Failed(user, state, now);
        var candidate = Derive(password, account.Salt, account.Iterations);
        try
        {
            if (CryptographicOperations.FixedTimeEquals(candidate, account.Hash)) { attempts.TryRemove(user.Value, out _); return "Ok"; }
        }
        finally { CryptographicOperations.ZeroMemory(candidate); }
        return Failed(user, state, now);
    }

    private string Failed(SecurityIdentifier user, (int Failures, long LockedUntil, string? ResetSeen) state, long now)
    {
        int failures = state.Failures + 1;
        long lockedUntil = failures >= MaxFailures ? now + (long)(Lockout.TotalSeconds * time.TimestampFrequency) : 0;
        attempts[user.Value] = (failures, lockedUntil, state.ResetSeen);
        return failures >= MaxFailures ? "PasswordLocked" : "WrongPassword";
    }

    // A new reset id also lifts a lockout (once per reset).
    private void ClearLockoutOnNewReset(SecurityIdentifier user, string reset)
    {
        var state = attempts.GetValueOrDefault(user.Value);
        if (state.ResetSeen != reset) attempts[user.Value] = (0, 0, reset);
    }

    private static Account Hash(string sid, string password, string? resetId)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new Account(sid, salt, Derive(password, salt, Iterations), Iterations, resetId);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, 32); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    // The administrator's reset for this operator, if any: a small file only administrators can write.
    private string? PendingReset(SecurityIdentifier user)
    {
        try
        {
            var path = Path.Combine(resetsFolder, user.Value + ".json");
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 1024) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return doc.RootElement.TryGetProperty("resetId", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParseExact(id.GetString(), "D", out var g) ? g.ToString("D") : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; } // unreadable: no reset
    }

    private List<Account> Load()
    {
        var content = catalog.ReadAccounts();
        if (content is null) return [];
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.GetProperty("version").GetInt32() != 1) throw new InvalidDataException();
            return doc.RootElement.GetProperty("accounts").EnumerateArray().Select(a => new Account(
                a.GetProperty("sid").GetString()!, a.GetProperty("salt").GetBytesFromBase64(), a.GetProperty("hash").GetBytesFromBase64(),
                a.GetProperty("iterations").GetInt32(), a.GetProperty("resetId").ValueKind == JsonValueKind.String ? a.GetProperty("resetId").GetString() : null)).ToList();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { throw new InvalidDataException("Accounts document invalid."); }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    private void Save(List<Account> accounts)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            accounts = accounts.OrderBy(a => a.Sid, StringComparer.Ordinal).Select(a => new
                { sid = a.Sid, salt = Convert.ToBase64String(a.Salt), hash = Convert.ToBase64String(a.Hash), iterations = a.Iterations, resetId = a.ResetId }),
        });
        try { catalog.WriteAccounts(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
