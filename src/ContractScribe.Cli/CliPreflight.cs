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
            if (!Directory.Exists(resolvedRoot))
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
        if (!IsRegularFile(input)
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
        if (!IsRegularFile(policy))
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
            var resolved = ResolveExistingPath(lexical);
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
        if (IsReparsePoint(final) || Directory.Exists(final))
        {
            throw Failure("cli.preflight.output-reparse");
        }
        return final;
    }

    private static string ResolveExistingPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)
            ?? throw new ArgumentException("The path must have a root.", nameof(path));
        var relative = full[root.Length..];
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        for (var index = 0; index < segments.Length; index++)
        {
            var candidate = Path.Join(current, segments[index]);
            FileSystemInfo? info = GetExistingInfo(candidate);
            if (info is null)
            {
                for (; index < segments.Length; index++)
                {
                    current = Path.Join(current, segments[index]);
                }
                return Path.GetFullPath(current);
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            current = target?.FullName ?? info.FullName;
        }
        return Path.GetFullPath(current);
    }

    private static FileSystemInfo? GetExistingInfo(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory)
                ? new DirectoryInfo(path)
                : new FileInfo(path);
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            return null;
        }
    }

    private static bool IsRegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.Directory)
                && !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            return exception is not (FileNotFoundException or DirectoryNotFoundException);
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
