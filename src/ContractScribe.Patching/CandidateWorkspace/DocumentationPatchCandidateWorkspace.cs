using System.Collections.Immutable;
using ContractScribe.Patching.CandidateWorkspace;
using ContractScribe.Roslyn;
using CandidateIdentity = ContractScribe.Patching.CandidateWorkspace.DocumentationPatchCandidateFileSystem.CandidatePhysicalIdentity;

namespace ContractScribe.Patching;

internal sealed class DocumentationPatchCandidateWorkspaceBuilder
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly Action<DocumentationPatchApplicationStage, string?>? observer;

    public DocumentationPatchCandidateWorkspaceBuilder(
        Action<DocumentationPatchApplicationStage, string?>? observer = null)
    {
        this.observer = observer;
    }

    public DocumentationPatchCandidateHandle Build(
        DocumentationPatchRepositoryBaseline baseline,
        IReadOnlyDictionary<string, byte[]> selectedBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(selectedBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var remainingSelectedPaths = selectedBytes.Keys
            .Select(ValidateRepositoryPath)
            .ToHashSet(PathComparer);
        if (remainingSelectedPaths.Count != selectedBytes.Count)
        {
            throw new DocumentationPatchApplicationException(
                DocumentationPatchApplicationStatus.Rejected,
                "patch.rejected.unsafe-change");
        }

        var parentPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetTempPath()));
        var parentIdentity = DocumentationPatchCandidateFileSystem.ReadDirectoryIdentity(
            parentPath);
        var rootName = "contract-scribe-candidate-" + Guid.NewGuid().ToString("N");
        var rootPath = Path.Join(parentPath, rootName);
        var rootIdentity = DocumentationPatchCandidateFileSystem.CreateNewDirectory(
            parentPath,
            parentIdentity,
            rootName);
        var validation = baseline.ValidateCandidateRoot(rootPath);
        if (!validation.IsValid
            || validation.PhysicalIdentity is not { } validatedRoot
            || !Matches(rootIdentity, validatedRoot))
        {
            _ = DocumentationPatchCandidateFileSystem.DeleteOwnedEntry(
                parentPath,
                parentIdentity,
                rootName,
                rootIdentity);
            throw new DocumentationPatchApplicationException(
                DocumentationPatchApplicationStatus.Rejected,
                "patch.rejected.unsafe-change");
        }

        observer?.Invoke(DocumentationPatchApplicationStage.CandidateRootCreated, rootPath);
        var directories = new Dictionary<string, CandidateIdentity>(PathComparer)
        {
            [string.Empty] = rootIdentity,
        };
        var files = new Dictionary<string, CandidateWorkspaceFile>(PathComparer);
        var lease = new CandidateWorkspaceLease(
            parentPath,
            parentIdentity,
            rootName,
            rootPath,
            directories,
            files);
        try
        {
            var originalIdentities = baseline.Entries
                .Select(entry => (
                    entry.PhysicalIdentity.Volume,
                    entry.PhysicalIdentity.FileId))
                .ToHashSet();
            foreach (var entry in baseline.Entries.OrderBy(
                         entry => entry.RepositoryPath,
                         StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ValidateRepositoryPath(entry.RepositoryPath);
                var parentRelative = Parent(path);
                EnsureDirectories(rootPath, parentRelative, directories);
                var parentFullPath = PhysicalPath(rootPath, parentRelative);
                var bytes = selectedBytes.TryGetValue(path, out var selected)
                    ? selected
                    : entry.Bytes.ToArray();
                if (selected is not null)
                {
                    _ = remainingSelectedPaths.Remove(path);
                }

                using var stream = DocumentationPatchCandidateFileSystem.CreateNewRegularFile(
                    parentFullPath,
                    directories[parentRelative],
                    Leaf(path));
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                var identity = DocumentationPatchCandidateFileSystem.ReadFileIdentity(stream);
                if (identity.LinkCount != 1
                    || originalIdentities.Contains((identity.Volume, identity.FileId)))
                {
                    throw new DocumentationPatchApplicationException(
                        DocumentationPatchApplicationStatus.Rejected,
                        "patch.rejected.unsafe-change");
                }

                files.Add(path, new CandidateWorkspaceFile(
                    path,
                    ImmutableArray.CreateRange(bytes),
                    identity));
                observer?.Invoke(DocumentationPatchApplicationStage.CandidateEntryWritten, rootPath);
            }

            if (remainingSelectedPaths.Count != 0)
            {
                throw new DocumentationPatchApplicationException(
                    DocumentationPatchApplicationStatus.Rejected,
                    "patch.rejected.unsafe-change");
            }

            cancellationToken.ThrowIfCancellationRequested();
            VerifyCandidate(rootPath, directories, files, cancellationToken);
            observer?.Invoke(DocumentationPatchApplicationStage.CandidateReadbackComplete, rootPath);
            observer?.Invoke(DocumentationPatchApplicationStage.BeforeOriginalRebind, rootPath);
            var rebind = baseline.Rebind(cancellationToken);
            if (rebind.Status != DocumentationPatchRepositoryRebindStatus.Unchanged)
            {
                throw new DocumentationPatchApplicationException(
                    rebind.Status == DocumentationPatchRepositoryRebindStatus.Stale
                        ? DocumentationPatchApplicationStatus.Stale
                        : DocumentationPatchApplicationStatus.Rejected,
                    rebind.FailureCode ?? "patch.stale.repository-context");
            }

            return new DocumentationPatchCandidateHandle(
                baseline,
                lease,
                files.Values.OrderBy(file => file.RepositoryPath, StringComparer.Ordinal)
                    .ToImmutableArray());
        }
        catch
        {
            lease.Cleanup();
            throw;
        }
    }

    private static void EnsureDirectories(
        string rootPath,
        string relativeDirectory,
        IDictionary<string, CandidateIdentity> directories)
    {
        if (string.IsNullOrEmpty(relativeDirectory))
        {
            return;
        }

        var current = string.Empty;
        foreach (var segment in relativeDirectory.Split('/'))
        {
            var parent = current;
            current = string.IsNullOrEmpty(current) ? segment : current + "/" + segment;
            if (directories.ContainsKey(current))
            {
                continue;
            }

            var parentPath = PhysicalPath(rootPath, parent);
            var parentIdentity = DocumentationPatchCandidateFileSystem.ReadDirectoryIdentity(
                parentPath);
            if (parentIdentity != directories[parent])
            {
                throw new IOException("The candidate directory changed during creation.");
            }

            var identity = DocumentationPatchCandidateFileSystem.CreateNewDirectory(
                parentPath,
                parentIdentity,
                segment);
            directories.Add(current, identity);
        }
    }

    private static void VerifyCandidate(
        string rootPath,
        IReadOnlyDictionary<string, CandidateIdentity> directories,
        IReadOnlyDictionary<string, CandidateWorkspaceFile> files,
        CancellationToken cancellationToken)
    {
        var observed = new HashSet<string>(PathComparer);
        var pending = new Stack<(string FullPath, string RelativePath)>();
        pending.Push((rootPath, string.Empty));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, relativeDirectory) = pending.Pop();
            if (!directories.TryGetValue(relativeDirectory, out var expectedDirectory)
                || DocumentationPatchCandidateFileSystem.ReadDirectoryIdentity(directory)
                    != expectedDirectory)
            {
                throw new IOException("The candidate directory identity changed.");
            }

            foreach (var path in Directory.EnumerateFileSystemEntries(directory)
                         .Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("The candidate contains a reparse entry.");
                }

                var relative = string.IsNullOrEmpty(relativeDirectory)
                    ? Path.GetFileName(path)
                    : relativeDirectory + "/" + Path.GetFileName(path);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push((path, relative));
                    continue;
                }

                if (!files.TryGetValue(relative, out var expectedFile)
                    || !observed.Add(relative))
                {
                    throw new IOException("The candidate file set changed.");
                }

                var parent = Parent(relative);
                var read = DocumentationPatchCandidateFileSystem.ReadOwnedRegularFile(
                    PhysicalPath(rootPath, parent),
                    directories[parent],
                    Leaf(relative),
                    expectedFile.Identity,
                    cancellationToken);
                if (!read.Bytes.AsSpan().SequenceEqual(expectedFile.Bytes.AsSpan()))
                {
                    throw new IOException("The candidate file bytes changed.");
                }
            }
        }

        if (observed.Count != files.Count)
        {
            throw new IOException("The candidate file set is incomplete.");
        }
    }

    private static string ValidateRepositoryPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)
            || Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new DocumentationPatchApplicationException(
                DocumentationPatchApplicationStatus.Rejected,
                "patch.rejected.unsafe-change");
        }

        return normalized;
    }

    private static string PhysicalPath(string root, string relative) =>
        string.IsNullOrEmpty(relative)
            ? root
            : Path.Join(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string Parent(string path) =>
        path.LastIndexOf('/') is var index && index >= 0 ? path[..index] : string.Empty;

    private static string Leaf(string path) =>
        path.LastIndexOf('/') is var index && index >= 0 ? path[(index + 1)..] : path;

    private static bool Matches(
        CandidateIdentity candidate,
        DocumentationPatchPhysicalIdentity baseline) =>
        candidate.Volume == baseline.Volume
        && candidate.FileId == baseline.FileId
        && candidate.IsDirectory == baseline.IsDirectory;
}

internal sealed class CandidateWorkspaceLease
{
    private readonly IReadOnlyDictionary<string, CandidateIdentity> directories;
    private readonly IReadOnlyDictionary<string, CandidateWorkspaceFile> files;
    private int cleaned;

    public CandidateWorkspaceLease(
        string parentPath,
        CandidateIdentity parentIdentity,
        string rootName,
        string rootPath,
        IReadOnlyDictionary<string, CandidateIdentity> directories,
        IReadOnlyDictionary<string, CandidateWorkspaceFile> files)
    {
        ParentPath = parentPath;
        ParentIdentity = parentIdentity;
        RootName = rootName;
        RootPath = rootPath;
        this.directories = directories;
        this.files = files;
    }

    public string ParentPath { get; }

    public CandidateIdentity ParentIdentity { get; }

    public string RootName { get; }

    public string RootPath { get; }

    public void Cleanup()
    {
        if (Interlocked.Exchange(ref cleaned, 1) != 0)
        {
            return;
        }

        foreach (var file in files.Values.OrderByDescending(
                     file => file.RepositoryPath.Length))
        {
            var parent = Parent(file.RepositoryPath);
            if (!directories.TryGetValue(parent, out var parentIdentity))
            {
                continue;
            }

            _ = DocumentationPatchCandidateFileSystem.DeleteOwnedEntry(
                PhysicalPath(RootPath, parent),
                parentIdentity,
                Leaf(file.RepositoryPath),
                file.Identity);
        }

        foreach (var directory in directories
                     .Where(pair => !string.IsNullOrEmpty(pair.Key))
                     .OrderByDescending(pair => pair.Key.Length))
        {
            var parent = Parent(directory.Key);
            if (!directories.TryGetValue(parent, out var parentIdentity))
            {
                continue;
            }

            _ = DocumentationPatchCandidateFileSystem.DeleteOwnedEntry(
                PhysicalPath(RootPath, parent),
                parentIdentity,
                Leaf(directory.Key),
                directory.Value);
        }

        if (directories.TryGetValue(string.Empty, out var rootIdentity))
        {
            _ = DocumentationPatchCandidateFileSystem.DeleteOwnedEntry(
                ParentPath,
                ParentIdentity,
                RootName,
                rootIdentity);
        }
    }

    private static string PhysicalPath(string root, string relative) =>
        string.IsNullOrEmpty(relative)
            ? root
            : Path.Join(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string Parent(string path) =>
        path.LastIndexOf('/') is var index && index >= 0 ? path[..index] : string.Empty;

    private static string Leaf(string path) =>
        path.LastIndexOf('/') is var index && index >= 0 ? path[(index + 1)..] : path;
}

internal sealed record CandidateWorkspaceFile(
    string RepositoryPath,
    ImmutableArray<byte> Bytes,
    CandidateIdentity Identity);

public sealed class DocumentationPatchCandidateHandle : IDisposable
{
    private readonly object gate = new();
    private readonly CandidateWorkspaceLease lease;
    private bool invalidated;

    internal DocumentationPatchCandidateHandle(
        DocumentationPatchRepositoryBaseline baseline,
        CandidateWorkspaceLease lease,
        ImmutableArray<CandidateWorkspaceFile> files)
    {
        Baseline = baseline;
        this.lease = lease;
        Files = files;
    }

    internal DocumentationPatchRepositoryBaseline Baseline { get; }

    internal ImmutableArray<CandidateWorkspaceFile> Files { get; }

    internal string RootPath => lease.RootPath;

    public bool IsInvalidated
    {
        get
        {
            lock (gate)
            {
                return invalidated;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (invalidated)
            {
                return;
            }

            invalidated = true;
        }

        lease.Cleanup();
    }

    internal DocumentationPatchCandidateConsumption? TryConsume()
    {
        lock (gate)
        {
            if (invalidated)
            {
                return null;
            }

            invalidated = true;
            return new DocumentationPatchCandidateConsumption(Baseline, lease, Files);
        }
    }
}

internal sealed class DocumentationPatchCandidateConsumption : IDisposable
{
    private readonly CandidateWorkspaceLease lease;
    private int disposed;

    internal DocumentationPatchCandidateConsumption(
        DocumentationPatchRepositoryBaseline baseline,
        CandidateWorkspaceLease lease,
        ImmutableArray<CandidateWorkspaceFile> files)
    {
        Baseline = baseline;
        this.lease = lease;
        Files = files;
    }

    internal DocumentationPatchRepositoryBaseline Baseline { get; }

    internal ImmutableArray<CandidateWorkspaceFile> Files { get; }

    internal string RootPath => lease.RootPath;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            lease.Cleanup();
        }
    }
}
