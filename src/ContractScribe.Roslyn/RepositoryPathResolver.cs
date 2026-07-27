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

        var physicalRoot = ResolveExistingPath(lexicalRoot);
        var physicalInput = ResolveExistingPath(lexicalInput);
        RequireContained(physicalRoot, physicalInput, "input.path-outside-root");
        return new ResolvedRepositoryPaths(lexicalRoot, physicalRoot, lexicalInput, physicalInput);
    }

    public string ResolveProject(string lexicalRoot, string physicalRoot, string projectPath)
    {
        var lexicalProject = Path.GetFullPath(projectPath);
        RequireContained(lexicalRoot, lexicalProject, "graph.project-outside-root");
        if (!File.Exists(lexicalProject))
        {
            throw LoaderException.Graph("graph.project-not-found");
        }

        var physicalProject = ResolveExistingPath(lexicalProject);
        RequireContained(physicalRoot, physicalProject, "graph.project-outside-root");
        return physicalProject;
    }

    public string RelativeIdentity(string physicalRoot, string physicalPath) =>
        Path.GetRelativePath(physicalRoot, physicalPath).Replace('\\', '/').Normalize();

    private string ResolveExistingPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw LoaderException.Input("input.path-invalid");
        var current = root;
        var segments = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var visited = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var hops = 0;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (!info.Exists)
            {
                throw LoaderException.Input("input.path-not-found");
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            if (++hops > 32)
            {
                throw LoaderException.Input("input.path-link-limit");
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw LoaderException.Input("input.path-link-invalid");
            current = Path.GetFullPath(target.FullName);
            if (!visited.Add(current))
            {
                throw LoaderException.Input("input.path-link-loop");
            }
        }

        return TrimDirectory(Path.GetFullPath(current));
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
}

internal sealed record ResolvedRepositoryPaths(
    string LexicalRoot,
    string PhysicalRoot,
    string LexicalInput,
    string PhysicalInput);

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
