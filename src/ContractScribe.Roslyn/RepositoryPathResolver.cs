namespace ContractScribe.Roslyn;

internal sealed class RepositoryPathResolver
{
    private readonly StringComparison comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public ResolvedRepositoryPaths Resolve(string repositoryRoot, string inputPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Path.IsPathFullyQualified(repositoryRoot))
        {
            throw LoaderException.Input("input.repository-root-invalid");
        }

        var lexicalRoot = TrimDirectory(Path.GetFullPath(repositoryRoot));
        if (!Directory.Exists(lexicalRoot))
        {
            throw LoaderException.Input("input.repository-root-not-found");
        }

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw LoaderException.Input("input.path-invalid");
        }

        var lexicalInput = Path.GetFullPath(
            Path.IsPathFullyQualified(inputPath)
                ? inputPath
                : Path.Combine(lexicalRoot, inputPath));
        RequireContained(lexicalRoot, lexicalInput, "input.path-outside-root");

        var extension = Path.GetExtension(lexicalInput);
        if (!extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw LoaderException.Input("input.path-not-supported");
        }

        if (!File.Exists(lexicalInput))
        {
            throw LoaderException.Input("input.path-not-found");
        }

        var rootResolution = ResolveExistingPath(lexicalRoot);
        var physicalRoot = rootResolution.PhysicalPath;
        var inputResolution = ResolveExistingPath(lexicalInput, physicalRoot);
        RequireContained(physicalRoot, inputResolution.PhysicalPath, "input.path-outside-root");
        return new ResolvedRepositoryPaths(
            lexicalRoot,
            physicalRoot,
            lexicalInput,
            inputResolution.PhysicalPath,
            rootResolution.TraversedReparseEntries
                .Concat(inputResolution.TraversedReparseEntries)
                .Distinct(PathComparer())
                .ToArray());
    }

    public ResolvedPhysicalPath ResolveProject(string lexicalRoot, string physicalRoot, string projectPath)
    {
        var lexicalProject = Path.GetFullPath(projectPath);
        if (!IsContained(lexicalRoot, lexicalProject)
            && !IsContained(physicalRoot, lexicalProject))
        {
            throw LoaderException.Graph("graph.project-outside-root");
        }

        if (!lexicalProject.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw LoaderException.Graph("graph.project-not-csharp");
        }

        if (!File.Exists(lexicalProject))
        {
            throw LoaderException.Graph("graph.project-not-found");
        }

        var resolution = ResolveExistingPath(lexicalProject, physicalRoot);
        RequireContained(physicalRoot, resolution.PhysicalPath, "graph.project-outside-root");
        return resolution;
    }

    private bool IsContained(string root, string candidate)
    {
        var normalizedRoot = TrimDirectory(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.Equals(normalizedRoot, comparison)
            || normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                comparison);
    }

    public string RelativeIdentity(string physicalRoot, string physicalPath) =>
        Path.GetRelativePath(physicalRoot, physicalPath).Replace('\\', '/');

    public ResolvedPhysicalPath ResolveSource(string physicalRoot, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)
            || !Path.IsPathFullyQualified(sourcePath))
        {
            throw LoaderException.Graph("graph.source-outside-root");
        }

        var fullPath = Path.GetFullPath(sourcePath);
        if (!IsContained(physicalRoot, fullPath))
        {
            throw LoaderException.Graph("graph.source-outside-root");
        }

        try
        {
            var resolution = ResolveExistingPath(
                fullPath,
                physicalRoot,
                allowMissingLeaf: true);
            if (!IsContained(physicalRoot, resolution.PhysicalPath))
            {
                throw LoaderException.Graph("graph.source-outside-root");
            }

            return resolution;
        }
        catch (LoaderException)
        {
            throw LoaderException.Graph("graph.source-outside-root");
        }
    }

    public ResolvedPhysicalPath ResolveSemantic(string physicalRoot, string path) =>
        ResolveExistingPath(Path.GetFullPath(path), physicalRoot);

    private ResolvedPhysicalPath ResolveExistingPath(
        string path,
        string? containmentRoot = null,
        bool allowMissingLeaf = false)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw LoaderException.Input("input.path-invalid");
        var current = root;
        var segments = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var visited = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var traversedReparseEntries = new List<string>();
        var hops = 0;
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (!info.Exists)
            {
                if (allowMissingLeaf && index == segments.Length - 1)
                {
                    return new ResolvedPhysicalPath(
                        TrimDirectory(Path.GetFullPath(current)),
                        traversedReparseEntries.Distinct(PathComparer()).ToArray());
                }

                throw LoaderException.Input("input.path-not-found");
            }

            while ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                traversedReparseEntries.Add(Path.GetFullPath(current));
                if (++hops > 32)
                {
                    throw LoaderException.Input("input.path-link-limit");
                }

                var target = info.ResolveLinkTarget(returnFinalTarget: false)
                    ?? throw LoaderException.Input("input.path-link-invalid");
                current = Path.GetFullPath(target.FullName);
                if (containmentRoot is not null)
                {
                    RequireContained(containmentRoot, current, "input.path-outside-root");
                }

                if (!visited.Add(current))
                {
                    throw LoaderException.Input("input.path-link-loop");
                }

                info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);
                if (!info.Exists)
                {
                    throw LoaderException.Input("input.path-not-found");
                }
            }
        }

        return new ResolvedPhysicalPath(
            TrimDirectory(Path.GetFullPath(current)),
            traversedReparseEntries.Distinct(PathComparer()).ToArray());
    }

    private void RequireContained(string root, string candidate, string code)
    {
        var normalizedRoot = TrimDirectory(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        if (normalizedCandidate.Equals(normalizedRoot, comparison))
        {
            return;
        }

        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.StartsWith(prefix, comparison))
        {
            throw LoaderException.Input(code);
        }
    }

    private static string TrimDirectory(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.TrimEndingDirectorySeparator(path);
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

internal sealed record ResolvedPhysicalPath(
    string PhysicalPath,
    IReadOnlyList<string> TraversedReparseEntries);

internal sealed record ResolvedRepositoryPaths(
    string LexicalRoot,
    string PhysicalRoot,
    string LexicalInput,
    string PhysicalInput,
    IReadOnlyList<string> TraversedReparseEntries);

internal sealed class LoaderException : Exception
{
    private LoaderException(string stage, string code)
    {
        Stage = stage;
        Code = code;
    }

    public string Stage { get; }

    public string Code { get; }

    public static LoaderException Input(string code) => new("input", code);

    public static LoaderException Toolchain(string code) => new("toolchain", code);

    public static LoaderException Graph(string code) => new("graph", code);

    public static LoaderException Workspace(string code) => new("workspace", code);

    public static LoaderException Compilation(string code) => new("compilation", code);

    public static LoaderException Generated(string code) => new("generated", code);
}
