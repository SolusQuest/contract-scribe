namespace ContractScribe.HostValidation;

public static class RepositoryPaths
{
    public static string NormalizeRoot(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot) || !File.Exists(Path.Join(fullRoot, "ContractScribe.slnx")))
        {
            throw new ProtocolException("HV112_REPOSITORY_ROOT_INVALID");
        }

        return fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string ResolveConfined(string root, string relativePath, bool mustExist = true)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || relativePath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ProtocolException("HV113_PATH_NOT_RELATIVE");
        }

        var normalizedRoot = NormalizeRoot(root);
        var candidate = Path.GetFullPath(Path.Join(normalizedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureWithinRoot(normalizedRoot, candidate);
        CheckReparseChain(normalizedRoot, candidate);
        if (mustExist && !File.Exists(candidate) && !Directory.Exists(candidate))
        {
            throw new ProtocolException("HV114_ARTIFACT_MISSING");
        }

        return candidate;
    }

    public static string ToRepositoryRelative(string root, string path)
    {
        var normalizedRoot = NormalizeRoot(root);
        var fullPath = Path.GetFullPath(path);
        EnsureWithinRoot(normalizedRoot, fullPath);
        return Path.GetRelativePath(normalizedRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static void EnsureWithinRoot(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.Equals(root, comparison)
            && !candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new ProtocolException("HV115_PATH_ESCAPE");
        }
    }

    private static void CheckReparseChain(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Join(current, segment);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current)
                    ? new FileInfo(current)
                    : null;
            if (info is null || !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            FileSystemInfo? target;
            try
            {
                target = info.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (IOException exception)
            {
                throw new ProtocolException("HV116_REPARSE_UNRESOLVED", exception);
            }

            if (target is null)
            {
                throw new ProtocolException("HV116_REPARSE_UNRESOLVED");
            }

            EnsureWithinRoot(root, Path.GetFullPath(target.FullName));
        }
    }
}
