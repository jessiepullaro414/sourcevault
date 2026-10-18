using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SourceVault.Build;

/// <summary>One build target: a named, already-running container on a Docker engine.</summary>
public sealed record BuildTarget(
    string Name, Uri Engine, string? CaFile, string? ClientCertificateThumbprint, string Container, string Platform,
    string SourcePath, IReadOnlyList<string> Command, string ArtifactPath, int TimeoutSeconds, long MaxArtifactBytes, bool AllowNetwork,
    IReadOnlySet<string>? KeepFileTypes = null)
{
    public bool IsWindows => Platform == "windows";
    /// <summary>Lowercase <see cref="ArtifactFilter.Kinds"/> the administrator chose to publish anyway.</summary>
    public IReadOnlySet<string> Keep => KeepFileTypes ?? new HashSet<string>();
}

/// <summary>The configuration file is invalid. The message names the field, never secrets.</summary>
public sealed class BuildConfigException(string message) : Exception(message);

/// <summary>
/// The administrator-only build configuration, <c>%ProgramData%\SourceVault\config\build.json</c>
/// (example: installer/build.example.json). Strict: unknown or duplicate properties, unsafe paths, plain-TCP Docker and
/// non-loopback engines are refused. Re-read for every build, so edits apply without a restart.
/// </summary>
public sealed record BuildConfig(string ArtifactRoot, IReadOnlyList<BuildTarget> Targets)
{
    public const int MaxFileBytes = 64 * 1024;
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SourceVault", "config", "build.json");

    private static readonly Regex TargetName = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant);
    private static readonly Regex ContainerName = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex PipeEngine = new(@"^npipe://\./pipe/([A-Za-z0-9._-]{1,128})$", RegexOptions.CultureInvariant);
    private static readonly Regex TcpEngine = new(@"^tcp://(127\.0\.0\.1|\[::1\]):([0-9]{1,5})$", RegexOptions.CultureInvariant);
    private static readonly Regex LinuxPath = new("^(/[A-Za-z0-9._-]+){2,16}$", RegexOptions.CultureInvariant);
    // Windows container paths are spliced into a cmd.exe cleanup line, so only plain characters are allowed.
    private static readonly Regex WindowsPath = new(@"^[A-Za-z]:(\\[A-Za-z0-9._ -]+){2,16}$", RegexOptions.CultureInvariant);
    private static readonly Regex Thumbprint = new("^[0-9A-Fa-f]{40}$", RegexOptions.CultureInvariant);

    public static BuildConfig Load(string path)
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxFileBytes) throw new BuildConfigException("build.json is larger than 64 KiB.");
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { throw new BuildConfigException("build.json does not exist."); }
        return Parse(bytes);
    }

    public static BuildConfig Parse(ReadOnlySpan<byte> json)
    {
        JsonDocument document;
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(json);
            document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException) { throw new BuildConfigException("build.json is not valid JSON."); }
        using (document)
        {
            var root = document.RootElement;
            Properties(root, "build.json", ["version", "artifactRoot", "targets"], []);
            if (root.GetProperty("version").ValueKind != JsonValueKind.Number || root.GetProperty("version").GetInt32() != 1) throw new BuildConfigException("version must be 1.");
            var artifactRoot = Text(root, "artifactRoot", 1, 200);
            if (!Path.IsPathFullyQualified(artifactRoot) || artifactRoot.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetFullPath(artifactRoot) != artifactRoot.TrimEnd('\\')
                || Path.GetPathRoot(artifactRoot)!.TrimEnd('\\') == artifactRoot.TrimEnd('\\'))
                throw new BuildConfigException("artifactRoot must be a full local folder path such as D:\\SourceVault\\artifacts (not a drive root or network path).");
            var protectedRoots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SourceVault"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SourceVault"),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            };
            foreach (var p in protectedRoots)
                if (p.Length > 0 && (artifactRoot.Equals(p, StringComparison.OrdinalIgnoreCase) || artifactRoot.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)))
                    throw new BuildConfigException("artifactRoot must not be inside the SourceVault install, data or Windows folders.");
            var list = root.GetProperty("targets");
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is 0 or > 16) throw new BuildConfigException("targets must list 1 to 16 targets.");
            var targets = new List<BuildTarget>();
            foreach (var item in list.EnumerateArray()) targets.Add(ParseTarget(item));
            if (targets.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count) throw new BuildConfigException("Target names must be unique.");
            return new BuildConfig(artifactRoot.TrimEnd('\\'), targets);
        }
    }

    private static BuildTarget ParseTarget(JsonElement item)
    {
        Properties(item, "target", ["name", "engine", "container", "platform", "sourcePath", "command", "artifactPath"],
            ["tls", "timeoutSeconds", "maxArtifactMegabytes", "allowNetwork", "keepFileTypes"]);
        var name = Text(item, "name", 1, 32);
        if (!TargetName.IsMatch(name)) throw new BuildConfigException("A target name must be 1-32 letters, digits, '-' or '_'.");
        string where = $"target '{name}'";
        var engineText = Text(item, "engine", 1, 200);
        string? caFile = null, thumbprint = null;
        bool tcp;
        if (PipeEngine.IsMatch(engineText)) tcp = false;
        else if (TcpEngine.Match(engineText) is { Success: true } m && int.Parse(m.Groups[2].Value) is >= 1 and <= 65535) tcp = true;
        else throw new BuildConfigException($"{where}: engine must be npipe://./pipe/<name> or tcp://127.0.0.1:<port> (TLS required).");
        if (tcp)
        {
            if (!item.TryGetProperty("tls", out var tls)) throw new BuildConfigException($"{where}: a tcp engine needs tls (plain-TCP Docker lets any program on this PC control it).");
            Properties(tls, $"{where} tls", ["caFile", "clientCertificateThumbprint"], []);
            caFile = Text(tls, "caFile", 1, 260);
            if (!Path.IsPathFullyQualified(caFile)) throw new BuildConfigException($"{where}: tls.caFile must be a full path.");
            thumbprint = Text(tls, "clientCertificateThumbprint", 1, 200);
            if (!Thumbprint.IsMatch(thumbprint)) throw new BuildConfigException($"{where}: tls.clientCertificateThumbprint must be the 40 hex characters of the client certificate in LocalMachine\\My (see scripts/lab/New-WslDockerTls.ps1).");
            thumbprint = thumbprint.ToUpperInvariant();
        }
        else if (item.TryGetProperty("tls", out _)) throw new BuildConfigException($"{where}: tls applies only to tcp engines.");
        var container = Text(item, "container", 1, 128);
        if (!ContainerName.IsMatch(container)) throw new BuildConfigException($"{where}: container is not a valid Docker container name.");
        var platform = Text(item, "platform", 5, 7);
        if (platform is not ("linux" or "windows")) throw new BuildConfigException($"{where}: platform must be linux or windows.");
        var pathRule = platform == "linux" ? LinuxPath : WindowsPath;
        string ContainerPath(string field)
        {
            var value = Text(item, field, 1, 200);
            var segments = value.Split(platform == "linux" ? '/' : '\\');
            if (!pathRule.IsMatch(value) || segments.Any(s => s is "." or "..") || value.EndsWith(' ') || value.EndsWith('.'))
                throw new BuildConfigException($"{where}: {field} must be an absolute {platform} path at least two folders deep, like {(platform == "linux" ? "/sourcevault/src" : @"C:\sourcevault\src")}.");
            return value;
        }
        var sourcePath = ContainerPath("sourcePath");
        var artifactPath = ContainerPath("artifactPath");
        var separator = platform == "linux" ? "/" : "\\";
        var comparison = platform == "linux" ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (sourcePath.Equals(artifactPath, comparison) || sourcePath.StartsWith(artifactPath + separator, comparison))
            throw new BuildConfigException($"{where}: sourcePath must differ from artifactPath and not be inside it.");
        var commandElement = item.GetProperty("command");
        if (commandElement.ValueKind != JsonValueKind.Array || commandElement.GetArrayLength() is 0 or > 32) throw new BuildConfigException($"{where}: command must list 1 to 32 arguments.");
        var command = new List<string>();
        foreach (var arg in commandElement.EnumerateArray())
        {
            var text = arg.ValueKind == JsonValueKind.String ? arg.GetString()! : throw new BuildConfigException($"{where}: command arguments must be strings.");
            if (text.Length is 0 or > 1024 || text.Any(char.IsControl)) throw new BuildConfigException($"{where}: command arguments must be 1-1024 characters without control characters.");
            command.Add(text);
        }
        int timeout = Number(item, "timeoutSeconds", 900, 10, 3600, where);
        int maxMegabytes = Number(item, "maxArtifactMegabytes", 512, 1, 4096, where);
        bool allowNetwork = false;
        if (item.TryGetProperty("allowNetwork", out var network))
            allowNetwork = network.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new BuildConfigException($"{where}: allowNetwork must be true or false.") };
        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (item.TryGetProperty("keepFileTypes", out var keepElement))
        {
            string allowed = string.Join(", ", ArtifactFilter.Kinds);
            if (keepElement.ValueKind != JsonValueKind.Array || keepElement.GetArrayLength() > ArtifactFilter.Kinds.Count) throw new BuildConfigException($"{where}: keepFileTypes must list some of: {allowed}.");
            foreach (var kind in keepElement.EnumerateArray())
            {
                var text = kind.ValueKind == JsonValueKind.String ? kind.GetString()!.ToLowerInvariant() : "";
                if (!ArtifactFilter.Kinds.Contains(text) || !keep.Add(text)) throw new BuildConfigException($"{where}: keepFileTypes must list some of: {allowed} (each once).");
            }
        }
        return new BuildTarget(name, new Uri(engineText), caFile, thumbprint, container, platform, sourcePath, command, artifactPath, timeout, maxMegabytes * 1024L * 1024L, allowNetwork, keep);
    }

    private static int Number(JsonElement item, string field, int fallback, int min, int max, string where)
    {
        if (!item.TryGetProperty(field, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int n) || n < min || n > max) throw new BuildConfigException($"{where}: {field} must be a whole number from {min} to {max}.");
        return n;
    }

    private static string Text(JsonElement item, string field, int min, int max)
    {
        var value = item.GetProperty(field);
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new BuildConfigException($"{field} must be text.");
        if (text.Length < min || text.Length > max || text.Any(char.IsControl)) throw new BuildConfigException($"{field} must be {min}-{max} characters.");
        return text;
    }

    private static void Properties(JsonElement value, string where, string[] required, string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new BuildConfigException($"{where} must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new BuildConfigException($"{where}: '{property.Name}' appears twice.");
            if (!required.Contains(property.Name) && !optional.Contains(property.Name)) throw new BuildConfigException($"{where}: unknown setting '{property.Name}'.");
        }
        foreach (var name in required) if (!seen.Contains(name)) throw new BuildConfigException($"{where}: '{name}' is required.");
    }
}
