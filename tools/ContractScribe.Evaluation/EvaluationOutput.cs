using System.Security.Cryptography;

namespace ContractScribe.Evaluation;

internal static class EvaluationOutput
{
    internal static bool TryResolveDirectory(
        string value,
        IReadOnlyCollection<string> forbiddenRoots,
        out string? directory)
    {
        directory = null;
        try
        {
            if (!TryValidateDirectory(value, forbiddenRoots, out var candidate)
                || candidate is null)
            {
                return false;
            }

            Directory.CreateDirectory(candidate);
            if (!IsSafeDirectory(candidate, forbiddenRoots)
                || File.Exists(Path.Join(candidate, "evaluation-report.json"))
                || File.Exists(Path.Join(candidate, "evaluation-partial.json")))
            {
                return false;
            }

            directory = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool TryValidateDirectory(
        string value,
        IReadOnlyCollection<string> forbiddenRoots,
        out string? directory)
    {
        directory = null;
        try
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            var candidate = Path.GetFullPath(value);
            if (!IsStrictDescendant(temporaryRoot, candidate)
                || forbiddenRoots.Select(Path.GetFullPath).Any(root => Overlaps(root, candidate))
                || ContainsReparsePoint(temporaryRoot, candidate)
                || File.Exists(candidate)
                || File.Exists(Path.Join(candidate, "evaluation-report.json"))
                || File.Exists(Path.Join(candidate, "evaluation-partial.json")))
            {
                return false;
            }

            directory = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool TryValidateNewPrivateDirectory(
        string value,
        IReadOnlyCollection<string> forbiddenRoots,
        out string? directory)
    {
        directory = null;
        if (!TryValidateDirectory(value, forbiddenRoots, out var candidate)
            || candidate is null
            || Directory.Exists(candidate))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(candidate);
        if (parent is null || !Directory.Exists(parent))
        {
            return false;
        }

        directory = candidate;
        return true;
    }

    internal static bool TryResolveExistingTemporaryDirectory(string value, out string? directory)
    {
        directory = null;
        try
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            var candidate = Path.GetFullPath(value);
            if (!Directory.Exists(candidate)
                || !IsStrictDescendant(temporaryRoot, candidate)
                || ContainsReparsePoint(temporaryRoot, candidate))
            {
                return false;
            }

            directory = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static async Task WriteAtomicAsync(
        string directory,
        string fileName,
        ReadOnlyMemory<byte> bytes,
        IReadOnlyCollection<string> forbiddenRoots,
        CancellationToken cancellationToken)
    {
        if (!IsSafeDirectory(directory, forbiddenRoots))
        {
            throw new InvalidDataException("evaluation.output.invalid");
        }

        var destination = Path.Join(directory, fileName);
        var temporary = Path.Join(directory, ".write-" + RandomNumberGenerator.GetHexString(16));
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            if (!IsSafeDirectory(directory, forbiddenRoots))
            {
                throw new InvalidDataException("evaluation.output.invalid");
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static void DeleteOwnedPartial(
        string directory,
        IReadOnlyCollection<string> forbiddenRoots)
    {
        if (!IsSafeDirectory(directory, forbiddenRoots))
        {
            throw new InvalidDataException("evaluation.output.invalid");
        }

        var partial = Path.Join(directory, "evaluation-partial.json");
        if (File.Exists(partial))
        {
            File.Delete(partial);
        }
    }

    private static bool IsSafeDirectory(
        string candidate,
        IReadOnlyCollection<string> forbiddenRoots)
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        return Directory.Exists(candidate)
            && IsStrictDescendant(temporaryRoot, candidate)
            && !ContainsReparsePoint(temporaryRoot, candidate)
            && forbiddenRoots.Select(Path.GetFullPath).All(root => !Overlaps(root, candidate));
    }

    private static bool Overlaps(string first, string second) =>
        IsSameOrDescendant(first, second) || IsSameOrDescendant(second, first);

    private static bool IsStrictDescendant(string root, string candidate) =>
        !SamePath(root, candidate) && IsSameOrDescendant(root, candidate);

    private static bool IsSameOrDescendant(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "."
            || !Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool SamePath(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool ContainsReparsePoint(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var component in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, component);
            if ((Directory.Exists(current) || File.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }

        return false;
    }
}
