namespace ContractScribe.HostValidation;

public static class RepositoryObserver
{
    private static readonly HashSet<string> ProtectedExtensions = new(
        [".cs", ".csproj", ".props", ".targets", ".sln", ".slnx", ".slnf"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ProtectedNames = new(
        ["global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "packages.lock.json"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> IgnoredSegments = new(
        [".git", ".tmp", "TestResults"],
        StringComparer.OrdinalIgnoreCase);

    public static RepositorySnapshot Capture(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new ProtocolException("HV148_OBSERVER_ROOT_MISSING");
        }

        var protectedFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var otherFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var allowedDesignTimeFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        try
        {
            foreach (var path in Directory.EnumerateFiles(fullRoot, "*", options))
            {
                var relative = Path.GetRelativePath(fullRoot, path).Replace(Path.DirectorySeparatorChar, '/');
                var segments = relative.Split('/');
                if (segments.Any(segment => IgnoredSegments.Contains(segment)))
                {
                    continue;
                }

                var identity = CanonicalJson.Sha256File(path);
                if (segments.Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                {
                    allowedDesignTimeFiles.Add(relative, identity);
                }
                else if (IsProtected(relative))
                {
                    protectedFiles.Add(relative, identity);
                }
                else
                {
                    otherFiles.Add(relative, identity);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProtocolException("HV149_OBSERVER_SNAPSHOT_FAILED", exception);
        }

        return new RepositorySnapshot(protectedFiles, otherFiles, allowedDesignTimeFiles);
    }

    public static RepositoryDelta Compare(RepositorySnapshot before, RepositorySnapshot after) =>
        new(
            Created(before.ProtectedFiles, after.ProtectedFiles),
            Deleted(before.ProtectedFiles, after.ProtectedFiles),
            Changed(before.ProtectedFiles, after.ProtectedFiles),
            Created(before.OtherFiles, after.OtherFiles),
            Deleted(before.OtherFiles, after.OtherFiles),
            Changed(before.OtherFiles, after.OtherFiles),
            Created(before.AllowedDesignTimeFiles, after.AllowedDesignTimeFiles),
            Changed(before.AllowedDesignTimeFiles, after.AllowedDesignTimeFiles));

    public static bool HasProtectedMutation(RepositoryDelta delta) =>
        delta.ProtectedCreated.Count != 0
        || delta.ProtectedDeleted.Count != 0
        || delta.ProtectedChanged.Count != 0;

    public static bool HasUnexpectedMutation(RepositoryDelta delta) =>
        HasProtectedMutation(delta)
        || delta.OtherCreated.Count != 0
        || delta.OtherDeleted.Count != 0
        || delta.OtherChanged.Count != 0;

    private static bool IsProtected(string relative)
    {
        var fileName = Path.GetFileName(relative);
        return ProtectedNames.Contains(fileName)
            || ProtectedExtensions.Contains(Path.GetExtension(fileName));
    }

    private static IReadOnlyList<string> Created(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after) =>
        after.Keys.Except(before.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<string> Deleted(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after) =>
        before.Keys.Except(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<string> Changed(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after) =>
        before.Keys.Intersect(after.Keys, StringComparer.Ordinal)
            .Where(path => before[path] != after[path])
            .Order(StringComparer.Ordinal)
            .ToArray();
}
