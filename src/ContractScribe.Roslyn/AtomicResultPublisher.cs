using System.Security.Cryptography;

namespace ContractScribe.Roslyn;

internal sealed class AtomicResultPublisher
{
    private const string StagingFileName = ".audit-result.json.contractscribe-stage";
    private readonly string repositoryRoot;
    private readonly string finalPath;
    private readonly string parentPath;
    private readonly string stagingPath;
    private readonly DateTime parentCreationTimeUtc;
    private readonly ProductionAuditHostControls controls;
    private string? stagedSha256;

    private AtomicResultPublisher(
        string repositoryRoot,
        string finalPath,
        string parentPath,
        ProductionAuditHostControls controls)
    {
        this.repositoryRoot = repositoryRoot;
        this.finalPath = finalPath;
        this.parentPath = parentPath;
        stagingPath = Path.Join(parentPath, StagingFileName);
        this.controls = controls;
        parentCreationTimeUtc = new DirectoryInfo(parentPath).CreationTimeUtc;
    }

    public string StagingPath => stagingPath;

    public static AtomicResultPublisher Prepare(
        string repositoryRoot,
        string resultPath,
        ProductionAuditHostControls controls)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var final = Path.GetFullPath(resultPath);
        if (!IsContained(root, final)
            || string.Equals(root, final, PathComparison()))
        {
            throw new PublicationException("host.publication.invalidation-failed");
        }
        var parent = Path.GetDirectoryName(final)
            ?? throw new PublicationException("host.publication.invalidation-failed");
        EnsureNoReparsePath(root, parent);
        Directory.CreateDirectory(parent);
        EnsureNoReparsePath(root, parent);
        var publisher = new AtomicResultPublisher(root, final, parent, controls);
        publisher.DeleteSafeEntry(final, "invalidate-existing");
        publisher.DeleteSafeEntry(publisher.stagingPath, "cleanup-staging");
        publisher.RevalidateParent();
        return publisher;
    }

    public void Stage(ReadOnlySpan<byte> bytes)
    {
        RevalidateParent();
        if (File.Exists(stagingPath) || Directory.Exists(stagingPath))
        {
            throw new PublicationException("host.publication.finalization-failed");
        }
        using (var stream = new FileStream(
            stagingPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        ValidateRegularFile(stagingPath);
        var readback = File.ReadAllBytes(stagingPath);
        if (!readback.AsSpan().SequenceEqual(bytes))
        {
            throw new PublicationException("host.publication.finalization-failed");
        }
        stagedSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public string CommitRename()
    {
        RevalidateParent();
        ValidateRegularFile(stagingPath);
        if (File.Exists(finalPath) || Directory.Exists(finalPath))
        {
            throw new PublicationException("host.publication.finalization-failed");
        }
        if (controls.Fault is
            ProductionHostFault.PublicationFinalization or
            ProductionHostFault.PublicationCleanup)
        {
            throw new PublicationException("host.publication.finalization-failed");
        }
        var committedSha256 = stagedSha256
            ?? throw new PublicationException("host.publication.finalization-failed");
        using (var stream = new FileStream(
                   stagingPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            var currentSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(
                    currentSha256,
                    committedSha256,
                    StringComparison.Ordinal))
            {
                throw new PublicationException("host.publication.finalization-failed");
            }
        }
        File.Move(stagingPath, finalPath, overwrite: false);
        return committedSha256;
    }

    public bool TryCleanupStaging()
    {
        if (controls.Fault == ProductionHostFault.PublicationCleanup)
        {
            return false;
        }
        try
        {
            DeleteSafeEntry(stagingPath, "cleanup-staging");
            return !File.Exists(stagingPath) && !Directory.Exists(stagingPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PublicationException)
        {
            return false;
        }
    }

    private void DeleteSafeEntry(string path, string operation)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }
        if (operation == "invalidate-existing"
            && controls.Fault == ProductionHostFault.PublicationInvalidation)
        {
            throw new PublicationException("host.publication.invalidation-failed");
        }
        ValidateRegularFile(path);
        File.Delete(path);
    }

    private void RevalidateParent()
    {
        EnsureNoReparsePath(repositoryRoot, parentPath);
        var parent = new DirectoryInfo(parentPath);
        if (!parent.Exists
            || parent.CreationTimeUtc != parentCreationTimeUtc
            || (parent.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new PublicationException("host.publication.finalization-failed");
        }
    }

    private static void ValidateRegularFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new PublicationException("host.publication.finalization-failed");
        }
    }

    private static void EnsureNoReparsePath(string root, string target)
    {
        var relative = Path.GetRelativePath(root, target);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new PublicationException("host.publication.invalidation-failed");
        }
        var current = root;
        if ((new DirectoryInfo(current).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new PublicationException("host.publication.invalidation-failed");
        }
        foreach (var segment in relative.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            if (Directory.Exists(current)
                && (new DirectoryInfo(current).Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PublicationException("host.publication.invalidation-failed");
            }
        }
    }

    private static bool IsContained(string root, string path) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison());

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

internal sealed class PublicationException(string failureCode) : IOException
{
    public string FailureCode { get; } = failureCode;
}
