using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

internal sealed record CliPreflightResult(
    string RepositoryRoot,
    string InputPath,
    byte[] PolicyBytes,
    ResolvedPublicationTarget PublicationTarget);

internal sealed class CliPreflightException(string code) : Exception
{
    public string Code { get; } = code;
}

internal static class CliPreflight
{
    private static readonly string[] InputExtensions = [".sln", ".slnx", ".csproj"];

    public static CliPreflightResult Run(
        AuditCommandArguments arguments,
        string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrEmpty(currentDirectory);

        string lexicalRoot;
        string resolvedRoot;
        try
        {
            lexicalRoot = Path.GetFullPath(arguments.RepositoryRoot, currentDirectory);
            if (!Directory.Exists(lexicalRoot))
            {
                throw Failure("cli.preflight.repository-root");
            }
            resolvedRoot = ResolveExistingPath(lexicalRoot);
            if (FileSystemEntryClassifier.Classify(resolvedRoot)
                != FileSystemEntryKind.Directory)
            {
                throw Failure("cli.preflight.repository-root");
            }
        }
        catch (CliPreflightException)
        {
            throw;
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            throw Failure("cli.preflight.repository-root");
        }

        var input = ResolveConfined(
            arguments.Input,
            lexicalRoot,
            resolvedRoot,
            "cli.preflight.input-escape",
            "cli.preflight.input");
        if (!IsRegularFileNoFollow(input)
            || !InputExtensions.Contains(
                Path.GetExtension(input),
                StringComparer.OrdinalIgnoreCase))
        {
            throw Failure("cli.preflight.input");
        }

        var policy = ResolveConfined(
            arguments.Policy,
            lexicalRoot,
            resolvedRoot,
            "cli.preflight.policy-escape",
            "cli.preflight.policy");
        if (!IsRegularFileNoFollow(policy))
        {
            throw Failure("cli.preflight.policy");
        }

        byte[] policyBytes;
        try
        {
            policyBytes = File.ReadAllBytes(policy);
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            throw Failure("cli.preflight.policy");
        }

        var output = ResolveOutput(arguments.Output, currentDirectory, resolvedRoot);
        return new CliPreflightResult(
            resolvedRoot,
            input,
            policyBytes,
            ResolvedPublicationTarget.ForExternalCli(resolvedRoot, output));
    }

    private static string ResolveConfined(
        string value,
        string lexicalRoot,
        string resolvedRoot,
        string escapeCode,
        string invalidCode)
    {
        try
        {
            var lexical = Path.IsPathFullyQualified(value)
                ? Path.GetFullPath(value)
                : Path.GetFullPath(value, lexicalRoot);
            var resolved = ResolveExistingPath(lexical, resolvedRoot, escapeCode);
            if (!IsContainedOrEqual(resolvedRoot, resolved))
            {
                throw Failure(escapeCode);
            }
            return resolved;
        }
        catch (CliPreflightException)
        {
            throw;
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            throw Failure(invalidCode);
        }
    }

    private static string ResolveOutput(
        string value,
        string currentDirectory,
        string resolvedRoot)
    {
        string lexical;
        string resolvedParent;
        string final;
        try
        {
            lexical = Path.GetFullPath(value, currentDirectory);
            var lexicalParent = Path.GetDirectoryName(lexical)
                ?? throw Failure("cli.preflight.output-parent");
            resolvedParent = ResolveExistingPath(lexicalParent);
            final = Path.Join(resolvedParent, Path.GetFileName(lexical));
        }
        catch (CliPreflightException)
        {
            throw;
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            throw Failure("cli.preflight.output-parent");
        }

        if (IsContainedOrEqual(resolvedRoot, final))
        {
            throw Failure("cli.preflight.output-inside-root");
        }
        if (!Directory.Exists(resolvedParent))
        {
            throw Failure("cli.preflight.output-parent");
        }
        if (!IsSafeOutputFinal(final))
        {
            throw Failure("cli.preflight.output-reparse");
        }
        return final;
    }

    private static string ResolveExistingPath(
        string path,
        string? confinementRoot = null,
        string? escapeCode = null)
    {
        var pending = Path.GetFullPath(path);
        var followedLinks = new HashSet<string>(PathComparer);
        for (var linkCount = 0; ; linkCount++)
        {
            if (linkCount > 63)
            {
                throw new IOException("Too many symbolic links were encountered.");
            }
            var root = Path.GetPathRoot(pending)
                ?? throw new ArgumentException("The path must have a root.", nameof(path));
            var segments = pending[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            var followedLink = false;

            for (var index = 0; index < segments.Length; index++)
            {
                var candidate = Path.Join(current, segments[index]);
                FileSystemEntryKind kind;
                try
                {
                    kind = FileSystemEntryClassifier.Classify(candidate);
                }
                catch (Exception exception) when (IsPathFailure(exception))
                {
                    kind = FileSystemEntryKind.Absent;
                }

                if (kind == FileSystemEntryKind.Absent
                    || (kind == FileSystemEntryKind.Other && index < segments.Length - 1))
                {
                    for (; index < segments.Length; index++)
                    {
                        current = Path.Join(current, segments[index]);
                    }
                    return EnsureConfined(current, confinementRoot, escapeCode);
                }
                if (kind != FileSystemEntryKind.Link)
                {
                    current = candidate;
                    continue;
                }

                var normalizedCandidate = Path.GetFullPath(candidate);
                if (!followedLinks.Add(normalizedCandidate))
                {
                    throw new IOException("A symbolic-link cycle was encountered.");
                }
                var target = ResolveOneLink(candidate);
                EnsureConfined(target, confinementRoot, escapeCode);
                pending = Path.GetFullPath(Path.Join(
                    target,
                    Path.Join(segments[(index + 1)..])));
                followedLink = true;
                break;
            }

            if (!followedLink)
            {
                return EnsureConfined(current, confinementRoot, escapeCode);
            }
        }
    }

    private static string ResolveOneLink(string path)
    {
        var attributes = File.GetAttributes(path);
        FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        var target = info.ResolveLinkTarget(returnFinalTarget: false);
        if (target is null)
        {
            throw new IOException("A reparse point did not expose a link target.");
        }
        return Path.GetFullPath(target.FullName);
    }

    private static string EnsureConfined(
        string path,
        string? confinementRoot,
        string? escapeCode)
    {
        var full = Path.GetFullPath(path);
        if (confinementRoot is not null && !IsContainedOrEqual(confinementRoot, full))
        {
            throw Failure(escapeCode
                ?? throw new InvalidOperationException("An escape code is required."));
        }
        return full;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static bool IsRegularFileNoFollow(string path)
    {
        try
        {
            return FileSystemEntryClassifier.Classify(path)
                == FileSystemEntryKind.RegularFile;
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            return false;
        }
    }

    private static bool IsSafeOutputFinal(string path)
    {
        try
        {
            return FileSystemEntryClassifier.Classify(path)
                is FileSystemEntryKind.Absent or FileSystemEntryKind.RegularFile;
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            return false;
        }
    }

    private static bool IsContainedOrEqual(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(normalizedRoot, normalizedPath, comparison)
            || normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                comparison);
    }

    private static bool IsPathFailure(Exception exception) =>
        exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException;

    private static CliPreflightException Failure(string code) => new(code);
}
