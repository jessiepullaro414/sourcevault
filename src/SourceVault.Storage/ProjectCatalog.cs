using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SourceVault.Storage;

public sealed record ProjectInfo(Guid ProjectId, string Name, StoreSnapshot Snapshot);

public enum CatalogFailure { Layout, Index, Project }

/// <summary>The vault's projects could not be verified. Carries only a failure category, never paths or names.</summary>
public sealed class VaultCatalogException(CatalogFailure failure) : Exception("Vault catalog verification failed.")
{
    public CatalogFailure Failure { get; } = failure;
}

/// <summary>
/// The set of projects in a vault root. Layout, with nothing else permitted in the root:
///   index\                  a RevisionStore (fixed id) holding one encrypted document, projects.json
///   projects\&lt;projectId&gt;\   one RevisionStore per project
/// Project names exist only inside the encrypted index; directory names are opaque ids. No record-format change:
/// the index is an ordinary encrypted store.
/// </summary>
public sealed class ProjectCatalog
{
    /// <summary>Fixed store id of the index, so its records are bound to the index context.</summary>
    public static readonly Guid IndexStoreId = new("2b8e4f1c-6a3d-4e7b-9c05-d1f2a3b4c5e6");
    public const int MaximumProjects = 256;
    private const string IndexDocument = "projects.json";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string vaultRoot;
    private readonly IKeyProtector protector;

    public ProjectCatalog(string vaultRoot, IKeyProtector protector)
    {
        if (!Path.IsPathFullyQualified(vaultRoot)) throw new ArgumentException("Invalid vault root.");
        this.vaultRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(vaultRoot));
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    private string IndexPath => Path.Combine(vaultRoot, "index");
    private string ProjectsPath => Path.Combine(vaultRoot, "projects");
    private string ProjectPath(Guid id) => Path.Combine(ProjectsPath, id.ToString("D"));

    /// <summary>
    /// Verifies the whole vault and returns its projects. An empty root is a fresh vault with no projects.
    /// Fails closed on any unexpected entry, a damaged index, or any project that does not authenticate.
    /// Costs 2 TPM unwraps for the index plus 1 per project.
    /// </summary>
    public IReadOnlyList<ProjectInfo> OpenAndVerify()
    {
        var entries = ReadIndexEntries(); // null: fresh vault
        if (entries is null) return [];
        var expected = entries.Select(e => e.ProjectId.ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] present;
        try
        {
            present = Directory.Exists(ProjectsPath)
                ? Directory.EnumerateFileSystemEntries(ProjectsPath).Select(p => Path.GetFileName(p)!).ToArray()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new VaultCatalogException(CatalogFailure.Layout); }
        if (present.Length != expected.Count || !present.All(expected.Contains)) throw new VaultCatalogException(CatalogFailure.Layout);
        var result = new List<ProjectInfo>();
        foreach (var (id, name) in entries)
        {
            try { result.Add(new(id, name, new RevisionStore(ProjectPath(id), id, protector).List())); }
            catch (Exception e) when (e is not OutOfMemoryException) { throw new VaultCatalogException(CatalogFailure.Project); }
        }
        return result;
    }

    /// <summary>
    /// Creates an empty project and records it in the index. The project store is created first; if the index
    /// commit then fails, the orphan project directory makes the next verification fail closed for review.
    /// </summary>
    public ProjectInfo CreateProject(string name)
    {
        ValidateProjectName(name);
        var entries = ReadIndexEntries();
        if (entries is null)
        {
            var index = new RevisionStore(IndexPath, IndexStoreId, protector);
            var empty = index.Initialize();
            index.Commit(empty.SnapshotId, IndexDocument, SerializeIndex([]));
            entries = [];
        }
        if (entries.Count >= MaximumProjects) throw new InvalidDataException("Project count exceeded.");
        if (entries.Any(e => StringComparer.OrdinalIgnoreCase.Equals(e.Name, name))) throw new ArgumentException("Project name already exists.");
        var id = Guid.NewGuid();
        Directory.CreateDirectory(ProjectsPath);
        var snapshot = new RevisionStore(ProjectPath(id), id, protector).Initialize();
        var indexStore = new RevisionStore(IndexPath, IndexStoreId, protector);
        var current = indexStore.List();
        indexStore.Commit(current.SnapshotId, IndexDocument, SerializeIndex(entries.Append((id, name)).ToList()));
        return new(id, name, snapshot);
    }

    /// <summary>The store of a project that <see cref="OpenAndVerify"/> returned.</summary>
    public RevisionStore OpenProject(Guid projectId) => new(ProjectPath(projectId), projectId, protector);

    private List<(Guid ProjectId, string Name)>? ReadIndexEntries()
    {
        string[] rootEntries;
        try { rootEntries = Directory.EnumerateFileSystemEntries(vaultRoot).Select(p => Path.GetFileName(p)!).ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new VaultCatalogException(CatalogFailure.Layout); }
        if (rootEntries.Length == 0) return null;
        if (!rootEntries.Contains("index", StringComparer.Ordinal) || rootEntries.Any(n => n is not ("index" or "projects")))
            throw new VaultCatalogException(CatalogFailure.Layout);
        byte[]? content = null;
        try
        {
            var index = new RevisionStore(IndexPath, IndexStoreId, protector);
            var snapshot = index.List();
            if (snapshot.Files.Count != 1 || snapshot.Files[0].LogicalName != IndexDocument) throw new InvalidDataException();
            content = index.Open(snapshot.SnapshotId, snapshot.Files[0].FileId);
            return ParseIndex(content);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new VaultCatalogException(CatalogFailure.Index); }
        finally { if (content is not null) CryptographicOperations.ZeroMemory(content); }
    }

    private static byte[] SerializeIndex(List<(Guid ProjectId, string Name)> entries) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1,
        projects = entries.Select(e => new { projectId = e.ProjectId.ToString("D"), name = e.Name }),
    });

    private static List<(Guid, string)> ParseIndex(byte[] content)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        Properties(root, "version", "projects");
        if (root.GetProperty("version").GetInt32() != 1) throw new InvalidDataException();
        var projects = root.GetProperty("projects");
        if (projects.ValueKind != JsonValueKind.Array || projects.GetArrayLength() > MaximumProjects) throw new InvalidDataException();
        var result = new List<(Guid, string)>();
        var ids = new HashSet<Guid>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects.EnumerateArray())
        {
            Properties(project, "projectId", "name");
            var text = project.GetProperty("projectId").GetString();
            if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || id == IndexStoreId || id.ToString("D") != text) throw new InvalidDataException();
            var name = project.GetProperty("name").GetString()!;
            ValidateProjectName(name);
            if (!ids.Add(id) || !names.Add(name)) throw new InvalidDataException();
            result.Add((id, name));
        }
        return result;
    }

    private static void Properties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !expected.Contains(property.Name, StringComparer.Ordinal)) throw new InvalidDataException();
        if (seen.Count != expected.Length) throw new InvalidDataException();
    }

    /// <summary>Display name: 1-64 characters, no control characters, no leading/trailing whitespace.</summary>
    public static void ValidateProjectName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || name.Trim() != name || name.Any(char.IsControl))
            throw new ArgumentException("Invalid project name.");
        try { _ = Utf8.GetByteCount(name); } catch (EncoderFallbackException) { throw new ArgumentException("Invalid project name."); }
    }
}
