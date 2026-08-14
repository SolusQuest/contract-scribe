using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;

namespace ContractScribe.Roslyn;

public enum DocumentationPatchRepositoryBaselineStatus
{
    Captured,
    Stale,
    Rejected,
}

public enum DocumentationPatchRepositoryRebindStatus
{
    Unchanged,
    Stale,
    Rejected,
}

public enum DocumentationPatchSemanticInputRole
{
    Source,
    AdditionalFile,
    AnalyzerConfig,
}

public sealed record DocumentationPatchPhysicalIdentity
{
    internal DocumentationPatchPhysicalIdentity(
        ulong volume,
        ulong fileId,
        long length,
        ulong linkCount,
        bool isDirectory)
    {
        Volume = volume;
        FileId = fileId;
        Length = length;
        LinkCount = linkCount;
        IsDirectory = isDirectory;
    }

    public ulong Volume { get; }

    public ulong FileId { get; }

    public long Length { get; }

    public ulong LinkCount { get; }

    public bool IsDirectory { get; }
}

public sealed record DocumentationPatchRepositoryBaselineEntry
{
    internal DocumentationPatchRepositoryBaselineEntry(
        string repositoryPath,
        string sourceIdentity,
        ImmutableArray<byte> bytes,
        string sha256,
        DocumentationPatchPhysicalIdentity physicalIdentity)
    {
        RepositoryPath = repositoryPath;
        SourceIdentity = sourceIdentity;
        Bytes = bytes;
        Sha256 = sha256;
        PhysicalIdentity = physicalIdentity;
    }

    public string RepositoryPath { get; }

    internal string SourceIdentity { get; }

    public string Kind => "file";

    public long Length => Bytes.Length;

    public ImmutableArray<byte> Bytes { get; }

    public string Sha256 { get; }

    public DocumentationPatchPhysicalIdentity PhysicalIdentity { get; }
}

public sealed record DocumentationPatchSemanticInputFact
{
    internal DocumentationPatchSemanticInputFact(
        string repositoryPath,
        string projectIdentity,
        string compilationContextRef,
        DocumentationPatchSemanticInputRole role,
        string logicalPath)
    {
        RepositoryPath = repositoryPath;
        ProjectIdentity = projectIdentity;
        CompilationContextRef = compilationContextRef;
        Role = role;
        LogicalPath = logicalPath;
    }

    public string RepositoryPath { get; }

    public string ProjectIdentity { get; }

    public string CompilationContextRef { get; }

    public DocumentationPatchSemanticInputRole Role { get; }

    public string LogicalPath { get; }
}

public sealed record DocumentationPatchRepositoryBaselineCaptureResult
{
    internal DocumentationPatchRepositoryBaselineCaptureResult(
        DocumentationPatchRepositoryBaselineStatus status,
        string? failureCode,
        DocumentationPatchRepositoryBaseline? baseline)
    {
        Status = status;
        FailureCode = failureCode;
        Baseline = baseline;
    }

    public DocumentationPatchRepositoryBaselineStatus Status { get; }

    public string? FailureCode { get; }

    public DocumentationPatchRepositoryBaseline? Baseline { get; }
}

public sealed record DocumentationPatchRepositoryRebindResult
{
    internal DocumentationPatchRepositoryRebindResult(
        DocumentationPatchRepositoryRebindStatus status,
        string? failureCode)
    {
        Status = status;
        FailureCode = failureCode;
    }

    public DocumentationPatchRepositoryRebindStatus Status { get; }

    public string? FailureCode { get; }
}

public sealed record DocumentationPatchCandidateRootValidation
{
    internal DocumentationPatchCandidateRootValidation(
        bool isValid,
        DocumentationPatchPhysicalIdentity? physicalIdentity)
    {
        IsValid = isValid;
        PhysicalIdentity = physicalIdentity;
    }

    public bool IsValid { get; }

    public DocumentationPatchPhysicalIdentity? PhysicalIdentity { get; }
}

public sealed record DocumentationPatchCandidateLocationValidation
{
    internal DocumentationPatchCandidateLocationValidation(
        bool isValid,
        DocumentationPatchPhysicalIdentity? parentIdentity)
    {
        IsValid = isValid;
        ParentIdentity = parentIdentity;
    }

    public bool IsValid { get; }

    public DocumentationPatchPhysicalIdentity? ParentIdentity { get; }
}

public sealed class DocumentationPatchRepositoryBaseline
{
    private readonly LoadedRepositorySession session;
    private readonly DocumentationPatchRepositoryPolicy policy;
    private readonly ImmutableDictionary<string, DocumentationPatchRepositoryBaselineEntry> byPath;
    private readonly ImmutableDictionary<string, DocumentationPatchPhysicalIdentity> directoryIdentities;

    internal DocumentationPatchRepositoryBaseline(
        LoadedRepositorySession session,
        DocumentationPatchRepositoryPolicy policy,
        DocumentationPatchPhysicalIdentity rootIdentity,
        ImmutableArray<DocumentationPatchRepositoryBaselineEntry> entries,
        ImmutableDictionary<string, DocumentationPatchPhysicalIdentity> directoryIdentities)
    {
        this.session = session;
        this.policy = policy;
        RootIdentity = rootIdentity;
        Entries = entries;
        SemanticInputs = policy.SemanticInputs;
        this.directoryIdentities = directoryIdentities;
        byPath = entries.ToImmutableDictionary(
            entry => entry.RepositoryPath,
            DocumentationPatchRepositoryPolicy.PathComparer);
    }

    public ImmutableArray<DocumentationPatchRepositoryBaselineEntry> Entries { get; }

    public ImmutableArray<DocumentationPatchSemanticInputFact> SemanticInputs { get; }

    internal DocumentationPatchPhysicalIdentity RootIdentity { get; }

    internal bool IsBoundTo(LoadedRepositorySession candidate) =>
        ReferenceEquals(session, candidate);

    internal bool TryGetEntry(
        string repositoryPath,
        out DocumentationPatchRepositoryBaselineEntry entry) =>
        byPath.TryGetValue(repositoryPath, out entry!);

    public DocumentationPatchRepositoryRebindResult Rebind(
        CancellationToken cancellationToken = default)
    {
        if (!session.IsDocumentationPatchAuthorityAvailable(policy))
        {
            return new DocumentationPatchRepositoryRebindResult(
                DocumentationPatchRepositoryRebindStatus.Stale,
                "patch.stale.repository-context");
        }

        try
        {
            var observation = DocumentationPatchRepositoryBaselineCapture.CaptureObservation(
                policy,
                DocumentationPatchRepositoryCaptureMode.Candidate,
                cancellationToken);
            if (observation.RootIdentity != RootIdentity
                || !DirectoryIdentitiesEqual(
                    directoryIdentities,
                    observation.DirectoryIdentities)
                || observation.Entries.Length != Entries.Length)
            {
                return StaleRebind();
            }

            for (var index = 0; index < Entries.Length; index++)
            {
                var expected = Entries[index];
                var actual = observation.Entries[index];
                if (!string.Equals(
                        expected.RepositoryPath,
                        actual.RepositoryPath,
                        DocumentationPatchRepositoryPolicy.PathComparison)
                    || expected.PhysicalIdentity != actual.PhysicalIdentity
                    || !string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal)
                    || !expected.Bytes.AsSpan().SequenceEqual(actual.Bytes.AsSpan()))
                {
                    return StaleRebind();
                }
            }

            return new DocumentationPatchRepositoryRebindResult(
                DocumentationPatchRepositoryRebindStatus.Unchanged,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DocumentationPatchBaselineException exception)
        {
            return new DocumentationPatchRepositoryRebindResult(
                exception.Status == DocumentationPatchRepositoryBaselineStatus.Stale
                    ? DocumentationPatchRepositoryRebindStatus.Stale
                    : DocumentationPatchRepositoryRebindStatus.Rejected,
                exception.Code);
        }
    }

    public DocumentationPatchCandidateRootValidation ValidateCandidateRoot(
        string candidateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateRoot);
        if (session.IsDisposed)
        {
            return new DocumentationPatchCandidateRootValidation(false, null);
        }

        try
        {
            var candidate = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(candidateRoot));
            if (Contains(policy.PhysicalRoot, candidate)
                || Contains(candidate, policy.PhysicalRoot))
            {
                return new DocumentationPatchCandidateRootValidation(false, null);
            }

            var identity = DocumentationPatchBaselineFileSystem.ReadDirectoryIdentity(
                candidate);
            var isDistinct = !SameDirectory(identity, RootIdentity)
                && !directoryIdentities.Values.Any(directory =>
                    SameDirectory(identity, directory));
            return new DocumentationPatchCandidateRootValidation(
                isDistinct,
                isDistinct ? identity : null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or DocumentationPatchBaselineException)
        {
            return new DocumentationPatchCandidateRootValidation(false, null);
        }
    }

    public DocumentationPatchCandidateLocationValidation ValidateCandidateLocation(
        string parentPath,
        string candidateRootName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateRootName);
        if (session.IsDisposed
            || candidateRootName is "." or ".."
            || candidateRootName.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return new DocumentationPatchCandidateLocationValidation(false, null);
        }

        try
        {
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
            var candidate = Path.Join(parent, candidateRootName);
            if (Contains(policy.PhysicalRoot, candidate)
                || Contains(candidate, policy.PhysicalRoot))
            {
                return new DocumentationPatchCandidateLocationValidation(false, null);
            }

            var parentIdentity = DocumentationPatchBaselineFileSystem.ReadDirectoryIdentity(parent);
            if (SameDirectory(parentIdentity, RootIdentity)
                || directoryIdentities.Values.Any(identity =>
                    SameDirectory(parentIdentity, identity)))
            {
                return new DocumentationPatchCandidateLocationValidation(false, null);
            }

            return new DocumentationPatchCandidateLocationValidation(true, parentIdentity);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or DocumentationPatchBaselineException)
        {
            return new DocumentationPatchCandidateLocationValidation(false, null);
        }
    }

    private static bool DirectoryIdentitiesEqual(
        ImmutableDictionary<string, DocumentationPatchPhysicalIdentity> left,
        ImmutableDictionary<string, DocumentationPatchPhysicalIdentity> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var identity)
                || identity != pair.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.Equals(
                normalizedRoot,
                DocumentationPatchRepositoryPolicy.PathComparison)
            || normalizedCandidate.StartsWith(
                prefix,
                DocumentationPatchRepositoryPolicy.PathComparison);
    }

    private static bool SameDirectory(
        DocumentationPatchPhysicalIdentity left,
        DocumentationPatchPhysicalIdentity right) =>
        left.IsDirectory
        && right.IsDirectory
        && left.Volume == right.Volume
        && left.FileId == right.FileId;

    private static DocumentationPatchRepositoryRebindResult StaleRebind() =>
        new(
            DocumentationPatchRepositoryRebindStatus.Stale,
            "patch.stale.repository-context");
}

internal sealed record DocumentationPatchRepositoryPolicy(
    string PhysicalRoot,
    ImmutableHashSet<string> ProtectedPaths,
    ImmutableArray<string> AllowedOutputRoots,
    ImmutableDictionary<string, InventoryEntry> ProtectedCommitments,
    ImmutableArray<DocumentationPatchSemanticInputFact> SemanticInputs)
{
    public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static StringComparison PathComparison { get; } = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static DocumentationPatchRepositoryPolicy Create(
        string physicalRoot,
        IEnumerable<string> protectedPaths,
        IEnumerable<string> allowedOutputRoots,
        IReadOnlyDictionary<string, InventoryEntry> inventory,
        IReadOnlyList<LoadedProject> projects)
    {
        var protectedSet = protectedPaths.ToImmutableHashSet(PathComparer);
        var outputRoots = allowedOutputRoots
            .Select(NormalizeRepositoryPath)
            .Distinct(PathComparer)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var commitments = inventory
            .Where(pair => pair.Value.Kind == "file"
                && (protectedSet.Contains(pair.Key)
                    || IsRepositoryInputExtension(pair.Key)))
            .ToImmutableDictionary(
                pair => NormalizeRepositoryPath(pair.Key),
                pair => pair.Value,
                PathComparer);
        return new DocumentationPatchRepositoryPolicy(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(physicalRoot)),
            protectedSet,
            outputRoots,
            commitments,
            CollectSemanticInputs(physicalRoot, projects));
    }

    internal static DocumentationPatchRepositoryPolicy CreateForTests(
        string physicalRoot,
        IReadOnlyList<LoadedProject> projects,
        IEnumerable<string>? allowedOutputRoots = null)
    {
        var protectedPaths = projects
            .SelectMany(project => project.SourceTrees.Values)
            .Select(source => source.RepositoryPath)
            .Where(path => path is not null)
            .Select(path => path!)
            .ToArray();
        return Create(
            physicalRoot,
            protectedPaths,
            allowedOutputRoots ?? [],
            RepositoryInventory.Capture(physicalRoot, CancellationToken.None),
            projects);
    }

    public bool IsProtected(string repositoryPath) =>
        ProtectedPaths.Contains(repositoryPath)
        || IsRepositoryInputExtension(repositoryPath);

    public bool IsGoverned(string repositoryPath)
    {
        var path = NormalizeRepositoryPath(repositoryPath);
        if (path.Equals(".git", StringComparison.Ordinal)
            || path.StartsWith(".git/", StringComparison.Ordinal))
        {
            return false;
        }

        if (IsProtected(path))
        {
            return true;
        }

        return !AllowedOutputRoots.Any(root =>
            path.Equals(root, PathComparison)
            || path.StartsWith(root + "/", PathComparison));
    }

    public bool HasProtectedDescendant(string repositoryPath)
    {
        var prefix = NormalizeRepositoryPath(repositoryPath).TrimEnd('/') + "/";
        return ProtectedPaths.Any(path => path.StartsWith(prefix, PathComparison));
    }

    public static string NormalizeRepositoryPath(string path) =>
        path.Replace('\\', '/').Trim('/');

    private static bool IsRepositoryInputExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".cs" or ".csproj" or ".props" or ".targets" or ".sln" or ".slnx" or ".editorconfig";

    private static ImmutableArray<DocumentationPatchSemanticInputFact> CollectSemanticInputs(
        string root,
        IReadOnlyList<LoadedProject> projects)
    {
        var facts = ImmutableArray.CreateBuilder<DocumentationPatchSemanticInputFact>();
        foreach (var loaded in projects)
        {
            AddSourceDocuments(facts, root, loaded);

            AddDocuments(
                facts,
                root,
                loaded,
                loaded.Project.AdditionalDocuments,
                DocumentationPatchSemanticInputRole.AdditionalFile);
            AddDocuments(
                facts,
                root,
                loaded,
                loaded.Project.AnalyzerConfigDocuments,
                DocumentationPatchSemanticInputRole.AnalyzerConfig);
        }

        return facts
            .Distinct()
            .OrderBy(fact => fact.RepositoryPath, StringComparer.Ordinal)
            .ThenBy(fact => fact.ProjectIdentity, StringComparer.Ordinal)
            .ThenBy(fact => fact.CompilationContextRef, StringComparer.Ordinal)
            .ThenBy(fact => fact.Role)
            .ThenBy(fact => fact.LogicalPath, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static void AddSourceDocuments(
        ImmutableArray<DocumentationPatchSemanticInputFact>.Builder facts,
        string root,
        LoadedProject loaded)
    {
        var repositorySources = loaded.SourceTrees.Values
            .Where(source => source.Kind == LoadedSourceKind.Repository
                && source.RepositoryPath is not null)
            .Select(source => source.RepositoryPath!)
            .ToHashSet(PathComparer);
        var represented = new HashSet<string>(PathComparer);
        foreach (var document in loaded.Project.Documents)
        {
            if (document.FilePath is not { } path
                || !TryRepositoryPath(root, path, out var repositoryPath)
                || !repositorySources.Contains(repositoryPath))
            {
                continue;
            }

            represented.Add(repositoryPath);
            AddDocument(facts, loaded, document, repositoryPath,
                DocumentationPatchSemanticInputRole.Source);
        }

        // Synthetic sessions can carry a compilation without a workspace document.
        // Production MSBuild sessions take the document path above so linked logical
        // paths and same-physical-file multiplicity remain intact.
        foreach (var repositoryPath in repositorySources.Except(represented, PathComparer))
        {
            facts.Add(new DocumentationPatchSemanticInputFact(
                repositoryPath,
                loaded.ProjectIdentity,
                loaded.CompilationContextRef,
                DocumentationPatchSemanticInputRole.Source,
                repositoryPath));
        }
    }

    private static void AddDocuments(
        ImmutableArray<DocumentationPatchSemanticInputFact>.Builder facts,
        string root,
        LoadedProject loaded,
        IEnumerable<TextDocument> documents,
        DocumentationPatchSemanticInputRole role)
    {
        foreach (var document in documents)
        {
            if (document.FilePath is not { } path
                || !TryRepositoryPath(root, path, out var repositoryPath))
            {
                continue;
            }

            AddDocument(facts, loaded, document, repositoryPath, role);
        }
    }

    private static void AddDocument(
        ImmutableArray<DocumentationPatchSemanticInputFact>.Builder facts,
        LoadedProject loaded,
        TextDocument document,
        string repositoryPath,
        DocumentationPatchSemanticInputRole role)
    {
        var logicalPath = document.Folders.Count == 0
            ? document.Name
            : string.Join('/', document.Folders.Append(document.Name));
        facts.Add(new DocumentationPatchSemanticInputFact(
            repositoryPath,
            loaded.ProjectIdentity,
            loaded.CompilationContextRef,
            role,
            logicalPath));
    }

    private static bool TryRepositoryPath(
        string root,
        string path,
        out string repositoryPath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedPath.StartsWith(prefix, PathComparison))
        {
            repositoryPath = string.Empty;
            return false;
        }

        repositoryPath = NormalizeRepositoryPath(Path.GetRelativePath(
            normalizedRoot,
            normalizedPath));
        return true;
    }
}

internal static class DocumentationPatchRepositoryBaselineCapture
{
    public static DocumentationPatchRepositoryBaselineCaptureResult Capture(
        LoadedRepositorySession session,
        DocumentationPatchRepositoryPolicy policy,
        CancellationToken cancellationToken) =>
        Capture(
            session,
            policy,
            DocumentationPatchRepositoryCaptureMode.Candidate,
            cancellationToken);

    public static DocumentationPatchRepositoryBaselineCaptureResult CaptureForResolution(
        LoadedRepositorySession session,
        DocumentationPatchRepositoryPolicy policy,
        CancellationToken cancellationToken) =>
        Capture(
            session,
            policy,
            DocumentationPatchRepositoryCaptureMode.Resolution,
            cancellationToken);

    private static DocumentationPatchRepositoryBaselineCaptureResult Capture(
        LoadedRepositorySession session,
        DocumentationPatchRepositoryPolicy policy,
        DocumentationPatchRepositoryCaptureMode mode,
        CancellationToken cancellationToken)
    {
        try
        {
            var observation = CaptureObservation(policy, mode, cancellationToken);
            return new DocumentationPatchRepositoryBaselineCaptureResult(
                DocumentationPatchRepositoryBaselineStatus.Captured,
                null,
                new DocumentationPatchRepositoryBaseline(
                    session,
                    policy,
                    observation.RootIdentity,
                    observation.Entries,
                    observation.DirectoryIdentities));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DocumentationPatchBaselineException exception)
        {
            return new DocumentationPatchRepositoryBaselineCaptureResult(
                exception.Status,
                exception.Code,
                null);
        }
    }

    internal static DocumentationPatchRepositoryObservation CaptureObservation(
        DocumentationPatchRepositoryPolicy policy,
        DocumentationPatchRepositoryCaptureMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = ObserveShape(policy, mode, cancellationToken);
        var entries = ImmutableArray.CreateBuilder<DocumentationPatchRepositoryBaselineEntry>();
        var identities = new HashSet<(ulong Volume, ulong FileId)>();
        foreach (var file in before.Files.OrderBy(file => file.RepositoryPath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.Join(
                policy.PhysicalRoot,
                file.ReadPath.Replace('/', Path.DirectorySeparatorChar));
            var read = DocumentationPatchBaselineFileSystem.ReadRegularFile(
                fullPath,
                cancellationToken);
            if (mode == DocumentationPatchRepositoryCaptureMode.Candidate
                && (read.Identity.LinkCount != 1
                    || !identities.Add((read.Identity.Volume, read.Identity.FileId))))
            {
                throw DocumentationPatchBaselineException.Rejected();
            }

            entries.Add(new DocumentationPatchRepositoryBaselineEntry(
                file.RepositoryPath,
                file.SourceIdentity,
                ImmutableArray.Create(read.Bytes),
                Convert.ToHexString(SHA256.HashData(read.Bytes)).ToLowerInvariant(),
                read.Identity));
        }

        var after = ObserveShape(policy, mode, cancellationToken);
        if (before.RootIdentity != after.RootIdentity
            || !before.Files.SequenceEqual(after.Files)
            || !DirectoryIdentitiesEqual(before.DirectoryIdentities, after.DirectoryIdentities))
        {
            throw DocumentationPatchBaselineException.Stale();
        }

        var immutableEntries = entries.ToImmutable();
        if (mode == DocumentationPatchRepositoryCaptureMode.Candidate)
        {
            ValidateProtectedCommitments(policy, immutableEntries);
        }
        return new DocumentationPatchRepositoryObservation(
            before.RootIdentity,
            immutableEntries,
            before.DirectoryIdentities);
    }

    private static DocumentationPatchRepositoryShape ObserveShape(
        DocumentationPatchRepositoryPolicy policy,
        DocumentationPatchRepositoryCaptureMode mode,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, DocumentationPatchObservedFile>(
            DocumentationPatchRepositoryPolicy.PathComparer);
        var directories = new Dictionary<string, DocumentationPatchPhysicalIdentity>(
            DocumentationPatchRepositoryPolicy.PathComparer);
        var pending = new Stack<(string FullPath, string RepositoryPath)>();
        pending.Push((policy.PhysicalRoot, string.Empty));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, repositoryPath) = pending.Pop();
            var directoryIdentity = DocumentationPatchBaselineFileSystem.ReadDirectoryIdentity(
                directory);
            directories[repositoryPath] = directoryIdentity;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory)
                         .Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = DocumentationPatchRepositoryPolicy.NormalizeRepositoryPath(
                    string.IsNullOrEmpty(repositoryPath)
                        ? Path.GetFileName(path)
                        : repositoryPath + "/" + Path.GetFileName(path));
                if (relative.Equals(".git", StringComparison.Ordinal)
                    || relative.StartsWith(".git/", StringComparison.Ordinal))
                {
                    continue;
                }

                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (mode == DocumentationPatchRepositoryCaptureMode.Candidate
                        && (Directory.Exists(path)
                            || policy.IsGoverned(relative)
                            || policy.HasProtectedDescendant(relative)))
                    {
                        throw DocumentationPatchBaselineException.Rejected();
                    }

                    if (mode == DocumentationPatchRepositoryCaptureMode.Resolution
                        && Directory.Exists(path))
                    {
                        var target = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)
                            ?? throw DocumentationPatchBaselineException.Rejected();
                        var targetPath = Path.GetFullPath(target.FullName);
                        if (!TryRepositoryPath(policy.PhysicalRoot, targetPath, out _))
                        {
                            throw DocumentationPatchBaselineException.Rejected();
                        }

                        pending.Push((targetPath, relative));
                    }
                    else if (mode == DocumentationPatchRepositoryCaptureMode.Resolution
                        && policy.IsGoverned(relative))
                    {
                        var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)
                            ?? throw DocumentationPatchBaselineException.Rejected();
                        var targetPath = Path.GetFullPath(target.FullName);
                        if (!TryRepositoryPath(policy.PhysicalRoot, targetPath, out var sourceIdentity)
                            || !files.TryAdd(
                                relative,
                                new DocumentationPatchObservedFile(
                                    relative,
                                    DocumentationPatchRepositoryPolicy.NormalizeRepositoryPath(
                                        Path.GetRelativePath(policy.PhysicalRoot, targetPath)),
                                    sourceIdentity)))
                        {
                            throw DocumentationPatchBaselineException.Rejected();
                        }
                    }

                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push((path, relative));
                    continue;
                }

                if (policy.IsGoverned(relative)
                    && !TryAddObservedFile(files, policy, relative, path))
                {
                    throw DocumentationPatchBaselineException.Rejected();
                }
            }
        }

        var relevantDirectories = RelevantDirectories(files.Keys);
        var filteredDirectories = directories
            .Where(pair => relevantDirectories.Contains(pair.Key))
            .ToImmutableDictionary(
                pair => pair.Key,
                pair => pair.Value,
                DocumentationPatchRepositoryPolicy.PathComparer);
        return new DocumentationPatchRepositoryShape(
            directories[string.Empty],
            files.Values.OrderBy(file => file.RepositoryPath, StringComparer.Ordinal).ToImmutableArray(),
            filteredDirectories);
    }

    private static string PhysicalIdentity(string repositoryPath) =>
        OperatingSystem.IsWindows()
            ? repositoryPath.ToUpperInvariant()
            : repositoryPath;

    private static bool TryAddObservedFile(
        Dictionary<string, DocumentationPatchObservedFile> files,
        DocumentationPatchRepositoryPolicy policy,
        string repositoryPath,
        string fullPath)
    {
        var resolved = new RepositoryPathResolver().ResolveSource(
            policy.PhysicalRoot,
            Path.GetFullPath(fullPath));
        var readPath = DocumentationPatchRepositoryPolicy.NormalizeRepositoryPath(
            Path.GetRelativePath(policy.PhysicalRoot, resolved.PhysicalPath));
        var sourceIdentity = new RepositoryPathResolver().PhysicalIdentity(
            policy.PhysicalRoot,
            resolved.PhysicalPath);
        return files.TryAdd(
            repositoryPath,
            new DocumentationPatchObservedFile(
                repositoryPath,
                readPath,
                sourceIdentity));
    }

    private static bool TryRepositoryPath(
        string root,
        string path,
        out string repositoryPath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedPath.StartsWith(
            prefix,
            DocumentationPatchRepositoryPolicy.PathComparison))
        {
            repositoryPath = string.Empty;
            return false;
        }

        repositoryPath = PhysicalIdentity(
            DocumentationPatchRepositoryPolicy.NormalizeRepositoryPath(
                Path.GetRelativePath(normalizedRoot, normalizedPath)));
        return true;
    }

    private static HashSet<string> RelevantDirectories(IEnumerable<string> files)
    {
        var result = new HashSet<string>(DocumentationPatchRepositoryPolicy.PathComparer)
        {
            string.Empty,
        };
        foreach (var file in files)
        {
            var current = file;
            while (current.LastIndexOf('/') is var separator && separator >= 0)
            {
                current = current[..separator];
                result.Add(current);
            }
        }

        return result;
    }

    private static void ValidateProtectedCommitments(
        DocumentationPatchRepositoryPolicy policy,
        ImmutableArray<DocumentationPatchRepositoryBaselineEntry> entries)
    {
        var protectedEntries = entries
            .Where(entry => policy.IsProtected(entry.RepositoryPath))
            .ToImmutableDictionary(
                entry => entry.RepositoryPath,
                DocumentationPatchRepositoryPolicy.PathComparer);
        if (protectedEntries.Count != policy.ProtectedCommitments.Count)
        {
            throw DocumentationPatchBaselineException.Stale();
        }

        foreach (var commitment in policy.ProtectedCommitments)
        {
            if (!protectedEntries.TryGetValue(commitment.Key, out var entry)
                || commitment.Value.Kind != "file"
                || commitment.Value.Length != entry.Length
                || !string.Equals(
                    commitment.Value.Sha256,
                    entry.Sha256,
                    StringComparison.Ordinal))
            {
                throw DocumentationPatchBaselineException.Stale();
            }
        }
    }

    private static bool DirectoryIdentitiesEqual(
        IReadOnlyDictionary<string, DocumentationPatchPhysicalIdentity> left,
        IReadOnlyDictionary<string, DocumentationPatchPhysicalIdentity> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        return left.All(pair =>
            right.TryGetValue(pair.Key, out var identity)
            && identity == pair.Value);
    }
}

internal sealed record DocumentationPatchRepositoryObservation(
    DocumentationPatchPhysicalIdentity RootIdentity,
    ImmutableArray<DocumentationPatchRepositoryBaselineEntry> Entries,
    ImmutableDictionary<string, DocumentationPatchPhysicalIdentity> DirectoryIdentities);

internal sealed record DocumentationPatchRepositoryShape(
    DocumentationPatchPhysicalIdentity RootIdentity,
    ImmutableArray<DocumentationPatchObservedFile> Files,
    ImmutableDictionary<string, DocumentationPatchPhysicalIdentity> DirectoryIdentities);

internal sealed record DocumentationPatchObservedFile(
    string RepositoryPath,
    string ReadPath,
    string SourceIdentity);

internal enum DocumentationPatchRepositoryCaptureMode
{
    Candidate,
    Resolution,
}

internal sealed class DocumentationPatchBaselineException : Exception
{
    private DocumentationPatchBaselineException(
        DocumentationPatchRepositoryBaselineStatus status,
        string code)
    {
        Status = status;
        Code = code;
    }

    public DocumentationPatchRepositoryBaselineStatus Status { get; }

    public string Code { get; }

    public static DocumentationPatchBaselineException Stale() =>
        new(
            DocumentationPatchRepositoryBaselineStatus.Stale,
            "patch.stale.repository-context");

    public static DocumentationPatchBaselineException Rejected() =>
        new(
            DocumentationPatchRepositoryBaselineStatus.Rejected,
            "patch.rejected.unsafe-change");
}
