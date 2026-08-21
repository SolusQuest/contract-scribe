using System.Security.Cryptography;

namespace ContractScribe.Evaluation;

internal static class EvaluationOutput
{
    internal static bool TryResolveDirectory(string value, out string? directory)
    {
        directory = null;
        try
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            var candidate = Path.GetFullPath(value);
            var relative = Path.GetRelativePath(temporaryRoot, candidate);
            if (relative is "." or ".."
                || Path.IsPathRooted(relative)
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || ContainsReparsePoint(temporaryRoot, candidate))
            {
                return false;
            }

            Directory.CreateDirectory(candidate);
            if (new DirectoryInfo(candidate).Attributes.HasFlag(FileAttributes.ReparsePoint))
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
        CancellationToken cancellationToken)
    {
        var destination = Path.Join(directory, fileName);
        var temporary = Path.Join(directory, ".write-" + RandomNumberGenerator.GetHexString(16));
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
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
