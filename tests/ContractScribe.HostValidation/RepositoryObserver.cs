using System.Text;

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

    public static RepositorySnapshot Capture(
        string root,
        IReadOnlyList<string>? allowedDesignTimeRoots = null)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new ProtocolException("HV148_OBSERVER_ROOT_MISSING");
        }

        var protectedFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var otherFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var allowedDesignTimeFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var allowedRoots = (allowedDesignTimeRoots ?? [])
            .Select(path => NormalizeRelative(path).TrimEnd('/') + "/")
            .ToArray();

        try
        {
            var pending = new Stack<string>();
            pending.Push(fullRoot);
            while (pending.Count != 0)
            {
                var directory = pending.Pop();
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    var relative = NormalizeRelative(Path.GetRelativePath(fullRoot, path));
                    var segments = relative.Split('/');
                    if (segments.Any(segment => IgnoredSegments.Contains(segment)))
                    {
                        continue;
                    }

                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        var info = (attributes & FileAttributes.Directory) != 0
                            ? (FileSystemInfo)new DirectoryInfo(path)
                            : new FileInfo(path);
                        var marker = CanonicalJson.Sha256(Encoding.UTF8.GetBytes(
                            $"reparse\0{info.LinkTarget ?? "unresolved"}"));
                        AddIdentity(relative, marker, allowedRoots, protectedFiles, otherFiles, allowedDesignTimeFiles);
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(path);
                        continue;
                    }

                    AddIdentity(
                        relative,
                        CanonicalJson.Sha256File(path),
                        allowedRoots,
                        protectedFiles,
                        otherFiles,
                        allowedDesignTimeFiles);
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
            Deleted(before.AllowedDesignTimeFiles, after.AllowedDesignTimeFiles),
            Changed(before.AllowedDesignTimeFiles, after.AllowedDesignTimeFiles));

    public static bool HasProtectedMutation(RepositoryDelta delta) =>
        delta.ProtectedCreated.Count != 0
        || delta.ProtectedDeleted.Count != 0
        || delta.ProtectedChanged.Count != 0;

    public static bool HasUnexpectedMutation(RepositoryDelta delta) =>
        HasProtectedMutation(delta)
        || delta.OtherCreated.Count != 0
        || delta.OtherDeleted.Count != 0
        || delta.OtherChanged.Count != 0
        || delta.AllowedDesignTimeDeleted.Count != 0;

    private static void AddIdentity(
        string relative,
        string identity,
        IReadOnlyList<string> allowedRoots,
        IDictionary<string, string> protectedFiles,
        IDictionary<string, string> otherFiles,
        IDictionary<string, string> allowedDesignTimeFiles)
    {
        if (allowedRoots.Any(root => (relative + "/").StartsWith(root, StringComparison.Ordinal)))
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

    private static string NormalizeRelative(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

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
