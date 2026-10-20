using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SourceVault.Storage;

public sealed record ProjectInfo(Guid ProjectId, string Name, StoreSnapshot Snapshot);
/// <summary>A vault check: the projects that authenticated, and those that did not (offline until an administrator looks).</summary>
public sealed record VaultCheck(IReadOnlyList<ProjectInfo> Projects, IReadOnlyList<(Guid ProjectId, string Name)> Offline);

/// <summary>A project or file name is already taken (case-insensitive). Carries no name.</summary>
public sealed class NameExistsException() : Exception("Name already exists.");

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
///
/// Deletion is crash-safe: the index first moves the project to a "deleted" list (a tombstone), then its directory
/// is removed, then the tombstone is cleared. A directory may exist without an index entry only while its tombstone
/// does; any other stray directory still fails verification.
/// </summary>
public sealed class ProjectCatalog
{
    /// <summary>Fixed store id of the index, so its records are bound to the index context.</summary>
    public static readonly Guid IndexStoreId = new("2b8e4f1c-6a3d-4e7b-9c05-d1f2a3b4c5e6");
    /// <summary>accounts\ (2026-10-20): one encrypted document with the operators' SourceVault password hashes.</summary>
    public static readonly Guid AccountsStoreId = new("7c1d9e3a-4b6f-4a2e-8d5c-3e9f1a2b6c40");
    private const string AccountsDocument = "operators.json";
    private readonly object accountsGate = new();
    public const int MaximumProjects = 256;
    private const string IndexDocument = "projects.json";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string vaultRoot;
    private readonly IKeyProtector protector;
    // Store instances are kept so their authenticated-manifest caches stay warm across requests.
    private readonly ConcurrentDictionary<Guid, RevisionStore> stores = new();
    private readonly object createGate = new();

    // Index document: version 1 = { projects }, version 2 = { projects, deleted } (tombstones awaiting cleanup).
    private sealed record IndexState(List<(Guid ProjectId, string Name)> Projects, List<Guid> Deleted);

    public ProjectCatalog(string vaultRoot, IKeyProtector protector)
    {
        if (!Path.IsPathFullyQualified(vaultRoot)) throw new ArgumentException("Invalid vault root.");
        this.vaultRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(vaultRoot));
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    /// <summary>Free space kept on the vault drive (<see cref="RevisionStore.FreeSpaceReserveBytes"/>); lower or higher in tests only.</summary>
    internal long FreeSpaceReserve { get; set; } = RevisionStore.FreeSpaceReserveBytes;

    /// <summary>
    /// Throws <see cref="StoreLimitException"/> when writing <paramref name="bytes"/> to the vault drive would leave less than
    /// the reserve free: for writes outside a project store, such as a package spool (internal security review 2026-10-20).
    /// </summary>
    public void EnsureFreeSpace(long bytes)
    {
        long available;
        try { available = new DriveInfo(Path.GetPathRoot(vaultRoot)!).AvailableFreeSpace; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return; } // the write itself reports
        if (available - bytes < FreeSpaceReserve) throw new StoreLimitException("Not enough free space on the vault drive.");
    }

    private string IndexPath => Path.Combine(vaultRoot, "index");
    private string ProjectsPath => Path.Combine(vaultRoot, "projects");
    private string ProjectPath(Guid id) => Path.Combine(ProjectsPath, id.ToString("D"));
    private string BuildsPath => Path.Combine(vaultRoot, "builds");
    private string AccountsPath => Path.Combine(vaultRoot, "accounts");

    /// <summary>
    /// The operators' password document, decrypted, or null when nobody has set a password yet. The caller zeroes it.
    /// A damaged accounts store fails closed (<see cref="CatalogFailure.Index"/>).
    /// </summary>
    public byte[]? ReadAccounts()
    {
        lock (accountsGate)
        {
            if (!Directory.Exists(AccountsPath)) return null;
            try
            {
                var store = Store(AccountsStoreId);
                var snapshot = store.List();
                if (snapshot.Files.Count == 0) return null;
                if (snapshot.Files.Count != 1 || snapshot.Files[0].LogicalName != AccountsDocument) throw new InvalidDataException();
                return store.Open(snapshot.SnapshotId, snapshot.Files[0].FileId);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { throw new VaultCatalogException(CatalogFailure.Index); }
        }
    }

    /// <summary>Replaces the operators' password document (encrypted like every record).</summary>
    public void WriteAccounts(byte[] content)
    {
        lock (accountsGate)
        {
            var store = Store(AccountsStoreId);
            var snapshot = Directory.Exists(AccountsPath) ? store.List() : store.Initialize();
            store.Commit(snapshot.SnapshotId, AccountsDocument, content);
        }
    }
    private string BuildLogPath(Guid id) => Path.Combine(BuildsPath, id.ToString("D"));
    private readonly ConcurrentDictionary<Guid, RevisionStore> buildLogs = new();
    private string StagingPath => Path.Combine(vaultRoot, "staging");

    /// <summary>
    /// staging\ (decision C6): uploads in progress, as SVR2 ciphertext only, one random folder per connection, on the
    /// vault's own volume so a commit moves records with a rename. Emptied when the service starts.
    /// </summary>
    public string CreateStagingFolder()
    {
        if (Directory.Exists(StagingPath) && (File.GetAttributes(StagingPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Untrusted staging path.");
        var folder = Path.Combine(StagingPath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        if ((File.GetAttributes(StagingPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Untrusted staging path.");
        return folder;
    }

    /// <summary>Removes everything in staging\ (leftovers of uploads from a previous run). Never follows links.</summary>
    public void ClearStaging()
    {
        if (!Directory.Exists(StagingPath)) return;
        if ((File.GetAttributes(StagingPath) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(StagingPath); return; } // the link itself
        foreach (var entry in new DirectoryInfo(StagingPath).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry is FileInfo) entry.Delete();
            else ((DirectoryInfo)entry).Delete(recursive: true); // does not follow links inside
        }
    }

    /// <summary>
    /// Verifies the whole vault and returns its projects. An empty root is a fresh vault with no projects.
    /// Fails closed on any unexpected entry, a damaged index, or any project that does not authenticate.
    /// Costs 2 TPM unwraps for the index plus 1 per project.
    /// </summary>
    public IReadOnlyList<ProjectInfo> OpenAndVerify()
    {
        var check = Verify();
        if (check.Offline.Count > 0) throw new VaultCatalogException(CatalogFailure.Project);
        return check.Projects;
    }

    /// <summary>
    /// Like <see cref="OpenAndVerify"/>, but a project that does not authenticate is returned in
    /// <see cref="VaultCheck.Offline"/> instead of failing the whole vault (owner decision 2026-10-20): the service
    /// starts without it, and it stays unusable until an administrator has looked. An unexpected entry in the vault
    /// root or a damaged index still fails closed, because then no project can be trusted.
    /// </summary>
    public VaultCheck Verify()
    {
        var state = ReadIndex(); // null: fresh vault
        if (state is null) return new([], []);
        var expected = state.Projects.Select(e => e.ProjectId.ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tombstoned = state.Deleted.Select(id => id.ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] present;
        try
        {
            present = Directory.Exists(ProjectsPath)
                ? Directory.EnumerateFileSystemEntries(ProjectsPath).Select(p => Path.GetFileName(p)!).ToArray()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new VaultCatalogException(CatalogFailure.Layout); }
        // Every listed project must exist; a directory may otherwise exist only for a project awaiting cleanup.
        if (!expected.All(present.Contains) || !present.All(p => expected.Contains(p) || tombstoned.Contains(p))) throw new VaultCatalogException(CatalogFailure.Layout);
        // Build logs (created on first build) may exist only for listed or tombstoned projects; they are verified when read.
        string[] logs;
        try { logs = Directory.Exists(BuildsPath) ? Directory.EnumerateFileSystemEntries(BuildsPath).Select(p => Path.GetFileName(p)!).ToArray() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new VaultCatalogException(CatalogFailure.Layout); }
        if (!logs.All(p => expected.Contains(p) || tombstoned.Contains(p))) throw new VaultCatalogException(CatalogFailure.Layout);
        var result = new List<ProjectInfo>();
        var offline = new List<(Guid, string)>();
        foreach (var (id, name) in state.Projects)
        {
            try { result.Add(new(id, name, Store(id).List())); }
            catch (Exception e) when (e is not OutOfMemoryException) { offline.Add((id, name)); }
        }
        return new(result, offline);
    }

    /// <summary>
    /// Creates an empty project and records it in the index. The project store is created first; if the index
    /// commit then fails, the orphan project directory makes the next verification fail closed for review.
    /// </summary>
    public ProjectInfo CreateProject(string name)
    {
        ValidateProjectName(name);
        lock (createGate) return CreateProjectLocked(name);
    }

    private ProjectInfo CreateProjectLocked(string name)
    {
        var state = ReadIndex();
        if (state is null)
        {
            var index = Store(IndexStoreId);
            var empty = index.Initialize();
            state = new IndexState([], []);
            index.Commit(empty.SnapshotId, IndexDocument, SerializeIndex(state));
        }
        if (state.Projects.Count + state.Deleted.Count >= MaximumProjects) throw new InvalidDataException("Project count exceeded.");
        if (state.Projects.Any(e => StringComparer.OrdinalIgnoreCase.Equals(e.Name, name))) throw new NameExistsException();
        var id = Guid.NewGuid();
        Directory.CreateDirectory(ProjectsPath);
        var snapshot = Store(id).Initialize();
        WriteIndex(new IndexState([.. state.Projects, (id, name)], state.Deleted));
        return new(id, name, snapshot);
    }

    /// <summary>
    /// Deletes a project and all its encrypted files. <paramref name="confirmName"/> must equal the project's name
    /// exactly (a guard against deleting the wrong id). The project disappears from the index at once; its directory is
    /// then removed, and if that fails (e.g. a file is in use) the tombstone keeps it allowed until
    /// <see cref="PurgeDeleted"/> succeeds.
    /// </summary>
    public bool DeleteProject(Guid projectId, string confirmName)
    {
        lock (createGate)
        {
            var state = ReadIndex() ?? throw new KeyNotFoundException();
            var entry = state.Projects.FirstOrDefault(p => p.ProjectId == projectId);
            if (entry.ProjectId == Guid.Empty) throw new KeyNotFoundException();
            if (!string.Equals(entry.Name, confirmName, StringComparison.Ordinal)) throw new ArgumentException("Confirmation does not match the project name.");
            WriteIndex(new IndexState(state.Projects.Where(p => p.ProjectId != projectId).ToList(), [.. state.Deleted, projectId]));
            stores.TryRemove(projectId, out _); buildLogs.TryRemove(projectId, out _);
            // The deletion is recorded; anything that fails from here on only leaves the tombstone for a later purge.
            try { return PurgeLocked() == 0; }
            catch (Exception e) when (e is not OutOfMemoryException) { return false; }
        }
    }

    /// <summary>Renames a project. Only the encrypted index changes. A letter-case-only change of its own name is allowed.</summary>
    public void RenameProject(Guid projectId, string newName)
    {
        ValidateProjectName(newName);
        lock (createGate)
        {
            var state = ReadIndex() ?? throw new KeyNotFoundException();
            int index = state.Projects.FindIndex(p => p.ProjectId == projectId);
            if (index < 0) throw new KeyNotFoundException();
            if (state.Projects.Any(p => p.ProjectId != projectId && StringComparer.OrdinalIgnoreCase.Equals(p.Name, newName))) throw new NameExistsException();
            if (state.Projects[index].Name == newName) return;
            var projects = state.Projects.ToList();
            projects[index] = (projectId, newName);
            WriteIndex(state with { Projects = projects });
        }
    }

    /// <summary>Removes directories of deleted projects and clears their tombstones. Returns how many remain pending.</summary>
    public int PurgeDeleted()
    {
        lock (createGate) return PurgeLocked();
    }

    private int PurgeLocked()
    {
        var state = ReadIndex();
        if (state is null || state.Deleted.Count == 0) return 0;
        var remaining = new List<Guid>();
        foreach (var id in state.Deleted)
        {
            try
            {
                foreach (var path in new[] { ProjectPath(id), BuildLogPath(id) })
                    if (Directory.Exists(path))
                    {
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) Directory.Delete(path); // remove a link itself, never its target
                        else Directory.Delete(path, recursive: true); // does not follow links inside
                    }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { remaining.Add(id); }
        }
        if (remaining.Count != state.Deleted.Count) WriteIndex(state with { Deleted = remaining });
        return remaining.Count;
    }

    /// <summary>The store of a project that <see cref="OpenAndVerify"/> returned.</summary>
    public RevisionStore OpenProject(Guid projectId) => Store(projectId);

    /// <summary>
    /// The encrypted build-log store of a live project (<c>builds&lt;projectId&gt;</c>), created on first use. Its store id is
    /// derived from the project id, so its records can never be mistaken for the project's own.
    /// </summary>
    public RevisionStore OpenBuildLog(Guid projectId)
    {
        if (buildLogs.TryGetValue(projectId, out var cached)) return cached;
        lock (createGate)
        {
            var state = ReadIndex() ?? throw new KeyNotFoundException();
            if (!state.Projects.Any(p => p.ProjectId == projectId)) throw new KeyNotFoundException();
            var store = new RevisionStore(BuildLogPath(projectId), BuildLogStoreId(projectId), protector);
            if (!Directory.Exists(BuildLogPath(projectId))) { Directory.CreateDirectory(BuildsPath); store.Initialize(); }
            return buildLogs.GetOrAdd(projectId, store);
        }
    }

    internal static Guid BuildLogStoreId(Guid projectId)
    {
        Span<byte> input = stackalloc byte[48]; // 23-byte label, zero padding, then the 16-byte project id
        "SourceVault.BuildLog.v1"u8.CopyTo(input);
        projectId.TryWriteBytes(input[32..]);
        return new Guid(SHA256.HashData(input)[..16]);
    }

    private RevisionStore Store(Guid id) => stores.GetOrAdd(id, key => key == IndexStoreId
        ? new RevisionStore(IndexPath, IndexStoreId, protector)
        : key == AccountsStoreId ? new RevisionStore(AccountsPath, AccountsStoreId, protector)
        : new RevisionStore(ProjectPath(key), key, protector));

    private void WriteIndex(IndexState state)
    {
        var indexStore = Store(IndexStoreId);
        indexStore.Commit(indexStore.List().SnapshotId, IndexDocument, SerializeIndex(state));
    }

    // The accounts store authenticates like any store; a damaged one refuses the vault (the passwords can't be trusted).
    private void VerifyAccounts()
    {
        var content = ReadAccounts();
        if (content is not null) CryptographicOperations.ZeroMemory(content);
    }

    private IndexState? ReadIndex()
    {
        string[] rootEntries;
        try { rootEntries = Directory.EnumerateFileSystemEntries(vaultRoot).Select(p => Path.GetFileName(p)!).ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new VaultCatalogException(CatalogFailure.Layout); }
        // staging\ (uploads in progress, ciphertext only) and accounts\ (passwords, set before any project) alone do not
        // make a vault: such a root has no projects yet.
        if (rootEntries.All(n => n is "staging" or "accounts")) { VerifyAccounts(); return null; }
        if (!rootEntries.Contains("index", StringComparer.Ordinal) || rootEntries.Any(n => n is not ("index" or "projects" or "builds" or "staging" or "accounts")))
            throw new VaultCatalogException(CatalogFailure.Layout);
        VerifyAccounts();
        byte[]? content = null;
        try
        {
            var index = Store(IndexStoreId);
            var snapshot = index.List();
            if (snapshot.Files.Count != 1 || snapshot.Files[0].LogicalName != IndexDocument) throw new InvalidDataException();
            content = index.Open(snapshot.SnapshotId, snapshot.Files[0].FileId);
            return ParseIndex(content);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new VaultCatalogException(CatalogFailure.Index); }
        finally { if (content is not null) CryptographicOperations.ZeroMemory(content); }
    }

    private static byte[] SerializeIndex(IndexState state) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 2,
        projects = state.Projects.Select(e => new { projectId = e.ProjectId.ToString("D"), name = e.Name }),
        deleted = state.Deleted.Select(id => id.ToString("D")),
    });

    private static IndexState ParseIndex(byte[] content)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        int version = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        if (version == 1) Properties(root, "version", "projects");
        else if (version == 2) Properties(root, "version", "projects", "deleted");
        else throw new InvalidDataException();
        var projects = root.GetProperty("projects");
        if (projects.ValueKind != JsonValueKind.Array || projects.GetArrayLength() > MaximumProjects) throw new InvalidDataException();
        var result = new List<(Guid, string)>();
        var ids = new HashSet<Guid>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects.EnumerateArray())
        {
            Properties(project, "projectId", "name");
            var id = ReadId(project.GetProperty("projectId"));
            var name = project.GetProperty("name").GetString()!;
            ValidateProjectName(name);
            if (!ids.Add(id) || !names.Add(name)) throw new InvalidDataException();
            result.Add((id, name));
        }
        var deleted = new List<Guid>();
        if (version == 2)
        {
            var list = root.GetProperty("deleted");
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaximumProjects) throw new InvalidDataException();
            foreach (var item in list.EnumerateArray())
            {
                var id = ReadId(item);
                if (!ids.Add(id)) throw new InvalidDataException(); // never both live and deleted, never twice
                deleted.Add(id);
            }
        }
        return new IndexState(result, deleted);
    }

    private static Guid ReadId(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || id == IndexStoreId || id.ToString("D") != text) throw new InvalidDataException();
        return id;
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
