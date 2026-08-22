using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace ContractScribe.Evaluation;

internal static class EvaluationOutput
{
    private const uint PrivateDirectoryCreateMode = 0x1C0;

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
            || candidate is null)
        {
            return false;
        }

        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var parent = Path.GetDirectoryName(candidate);
        if (!Directory.Exists(temporaryRoot)
            || new DirectoryInfo(temporaryRoot).LinkTarget is not null
            || parent is null
            || !SamePath(temporaryRoot, parent)
            || !EntryIsAbsentWithoutFollowing(candidate))
        {
            return false;
        }

        directory = candidate;
        return true;
    }

    internal static bool TryResolveNewPrivateDirectory(
        string value,
        IReadOnlyCollection<string> forbiddenRoots,
        out string? directory)
    {
        directory = null;
        if (!TryValidateNewPrivateDirectory(value, forbiddenRoots, out var candidate)
            || candidate is null)
        {
            return false;
        }

        var created = false;
        try
        {
            if (!OperatingSystem.IsLinux()
                || MakeDirectory(candidate, PrivateDirectoryCreateMode) != 0)
            {
                return false;
            }

            created = true;
            var expectedMode = UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute;
            if (!IsSafeDirectory(candidate, forbiddenRoots)
                || File.GetUnixFileMode(candidate) != expectedMode)
            {
                return false;
            }

            directory = candidate;
            created = false;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (created)
            {
                DeleteEmptyPrivateDirectory(candidate);
            }
        }
    }

    internal static void DeleteEmptyPrivateDirectory(string value)
    {
        try
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            var candidate = Path.GetFullPath(value);
            if (SamePath(temporaryRoot, Path.GetDirectoryName(candidate) ?? string.Empty)
                && Directory.Exists(candidate)
                && !ContainsReparsePoint(temporaryRoot, candidate)
                && !Directory.EnumerateFileSystemEntries(candidate).Any())
            {
                Directory.Delete(candidate);
            }
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return;
        }
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

    private static bool EntryIsAbsentWithoutFollowing(string candidate)
    {
        if (OperatingSystem.IsLinux())
        {
            if (LStat(candidate, out _) == 0)
            {
                return false;
            }

            return Marshal.GetLastPInvokeError() == 2;
        }

        try
        {
            _ = File.GetAttributes(candidate);
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat(string path, out LinuxStat value);

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int MakeDirectory(string path, uint mode);

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        internal ulong Device;
        internal ulong Inode;
        internal ulong HardLinkCount;
        internal uint Mode;
        internal uint UserId;
        internal uint GroupId;
        internal int Padding;
        internal ulong RawDevice;
        internal long Size;
        internal long BlockSize;
        internal long Blocks;
        internal LinuxTimespec AccessTime;
        internal LinuxTimespec ModificationTime;
        internal LinuxTimespec ChangeTime;
        internal long Reserved0;
        internal long Reserved1;
        internal long Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxTimespec
    {
        internal long Seconds;
        internal long Nanoseconds;
    }
}
