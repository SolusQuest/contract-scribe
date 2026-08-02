namespace ContractScribe.Roslyn;

internal sealed class ResolvedPublicationTarget
{
    private const string ValidationResultRelativePath = "TestResults/audit-result.json";

    private ResolvedPublicationTarget(
        string repositoryRoot,
        string finalPath,
        PublicationTargetKind kind)
    {
        RepositoryRoot = repositoryRoot;
        FinalPath = finalPath;
        ParentPath = Path.GetDirectoryName(finalPath)
            ?? throw new ArgumentException("The publication target must have a parent directory.", nameof(finalPath));
        Kind = kind;
    }

    public string RepositoryRoot { get; }

    public string FinalPath { get; }

    public string ParentPath { get; }

    public PublicationTargetKind Kind { get; }

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
        return new ResolvedPublicationTarget(root, final, PublicationTargetKind.ExternalCli);
    }

    public static ResolvedPublicationTarget ForValidationFixture(string repositoryRoot)
    {
        var root = NormalizeRoot(repositoryRoot);
        var final = Path.GetFullPath(Path.Join(root, ValidationResultRelativePath));
        RequireExistingParent(final);
        return new ResolvedPublicationTarget(root, final, PublicationTargetKind.ValidationFixture);
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

internal enum PublicationTargetKind
{
    ExternalCli,
    ValidationFixture,
}
