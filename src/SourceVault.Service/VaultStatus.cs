using System.Text.Json;

namespace SourceVault.Service;

/// <summary>
/// The administrator's view of the vault (owner decision 2026-10-20): <c>%ProgramData%\SourceVault\status\vault.json</c>,
/// written at startup and whenever a project goes offline. Ids and counts only, never names or content: project
/// names exist only in the encrypted index. Setup creates the folder (administrators and SYSTEM full control, the
/// service Modify, nobody else) and <c>Setup-SourceVault.ps1 -Status</c> reads it. Best effort: without the folder
/// nothing is written.
/// </summary>
internal static class VaultStatus
{
    internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SourceVault", "status");

    internal static void Write(int available, IEnumerable<Guid> offline) => WriteTo(Folder, available, offline, DateTimeOffset.UtcNow);

    internal static void WriteTo(string folder, int available, IEnumerable<Guid> offline, DateTimeOffset now)
    {
        try
        {
            if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return;
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("version", 1);
                w.WriteString("updatedAt", now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
                w.WriteNumber("projectsAvailable", available);
                w.WriteStartArray("projectsOffline");
                foreach (var id in offline.Order()) w.WriteStringValue(id.ToString("D"));
                w.WriteEndArray();
                w.WriteEndObject();
            }
            var path = Path.Combine(folder, "vault.json");
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, buffer.WrittenSpan.ToArray());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort; the editor still shows offline projects */ }
    }
}
