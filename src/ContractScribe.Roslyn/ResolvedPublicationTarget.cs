namespace ContractScribe.Roslyn;

internal sealed class ResolvedPublicationTarget
{
    private const string TestResultRelativePath = "TestResults/audit-result.json";

    private ResolvedPublicationTarget(
        string repositoryRoot,
        string finalPath)
    {
        RepositoryRoot = repositoryRoot;
        FinalPath = finalPath;
        ParentPath = Path.GetDirectoryName(finalPath)
            ?? throw new ArgumentException("The publication target must have a parent directory.", nameof(finalPath));
    }

    public string RepositoryRoot { get; }

    public string FinalPath { get; }

    public string ParentPath { get; }

    public static ResolvedPublicationTarget ForExternalCli(
        string repositoryRoot,
        string outputPath)
    {
        var root = NormalizeRoot(repositoryRoot);
        var final = Path.GetFullPath(outputPath);
        if (IsContained(root, final) || string.Equals(root, final, PathComparison()))
        {
            throw new ArgumentException(
                "The Audit CLI output must be outside the repository root.",
                nameof(outputPath));
        }
        RequireExistingParent(final);
        return new ResolvedPublicationTarget(root, final);
    }

    public static ResolvedPublicationTarget ForTestResult(string repositoryRoot)
    {
        var root = NormalizeRoot(repositoryRoot);
        var final = Path.GetFullPath(Path.Join(root, TestResultRelativePath));
        RequireExistingParent(final);
        return new ResolvedPublicationTarget(root, final);
    }

    private static string NormalizeRoot(string repositoryRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        if (!Directory.Exists(root))
        {
            throw new ArgumentException("The repository root must already exist.", nameof(repositoryRoot));
        }
        return root;
    }

    private static void RequireExistingParent(string finalPath)
    {
        var parent = Path.GetDirectoryName(finalPath);
        if (parent is null || !Directory.Exists(parent))
        {
            throw new ArgumentException(
                "The publication target parent must be created by preflight before Host execution.",
                nameof(finalPath));
        }
    }

    private static bool IsContained(string root, string path) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison());

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
