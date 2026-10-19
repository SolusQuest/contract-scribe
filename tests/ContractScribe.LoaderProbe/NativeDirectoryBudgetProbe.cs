using System.Runtime.InteropServices;
using System.Text.Json;
using ContractScribe.Roslyn;

internal static class NativeDirectoryBudgetProbe
{
    internal static int Run(string root)
    {
        if (!DarwinFileSystem.IsSupported) return 64;
        var policy = DocumentationPatchRepositoryPolicy.Create(root,
            ["Sample.cs"], ["unrelated", "child"],
            RepositoryInventory.Capture(root, CancellationToken.None), []);
        using (var warm = new DarwinFileSystem.RootLease(root))
        using (warm.OpenRoot()) { }
        if (GetLimit(8, out var limits) != 0) return 64;
        limits.Current = Math.Min(limits.Current, 96ul);
        if (SetLimit(8, ref limits) != 0) return 64;
        // This is an isolated probe process. Never change the test runner's or
        // host's descriptor limit, and do not inspect descriptor names/content.
        var before = CountDescriptors((int)limits.Current);
        var peak = before;
        var visited = 0;
        var stage = "capture";
        try
        {
            var captured = DocumentationPatchRepositoryBaselineCapture.CaptureObservation(
                policy, DocumentationPatchRepositoryCaptureMode.Candidate, CancellationToken.None);
            stage = "walk";
            using (var authority = new DarwinFileSystem.RootLease(root))
            using (var selected = authority.OpenRoot())
            using (var unrelated = authority.OpenDirectory(selected, "unrelated", CancellationToken.None))
            {
                foreach (var entry in authority.Enumerate(unrelated, CancellationToken.None))
                {
                    using var child = authority.OpenDirectory(unrelated, entry.Name,
                        CancellationToken.None, observation: entry);
                    if (authority.Enumerate(child, CancellationToken.None).Any()) return 65;
                    peak = Math.Max(peak, CountDescriptors((int)limits.Current));
                    visited++;
                }
            }
            stage = "reobserve";
            var rebound = DocumentationPatchRepositoryBaselineCapture.CaptureObservation(
                policy, DocumentationPatchRepositoryCaptureMode.Candidate, CancellationToken.None);
            var expected = captured.Entries.Single();
            var actual = rebound.Entries.Single();
            var unchanged = captured.RootIdentity == rebound.RootIdentity
                && expected.RepositoryPath == "Sample.cs" && actual.RepositoryPath == expected.RepositoryPath
                && actual.PhysicalIdentity == expected.PhysicalIdentity && actual.Sha256 == expected.Sha256
                && actual.Bytes.AsSpan().SequenceEqual(expected.Bytes.AsSpan())
                && captured.DirectoryIdentities.OrderBy(pair => pair.Key)
                    .SequenceEqual(rebound.DirectoryIdentities.OrderBy(pair => pair.Key));
            var bounded = peak <= before + 16 && visited == 512 && unchanged;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                bounded,
                visited,
                before,
                peak,
                descriptorBudget = limits.Current,
                unchanged,
            }));
            return bounded ? 0 : 65;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                failure = exception.GetType().Name,
                failureCode = (exception as DocumentationPatchBaselineException)?.Code,
                stage,
                visited,
                before,
                peak,
                descriptorBudget = limits.Current,
            }));
            return 65;
        }
    }

    private static int CountDescriptors(int limit) =>
        Enumerable.Range(0, limit).Count(descriptor => GetDescriptorFlags(descriptor, 1) >= 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct Limits
    {
        internal ulong Current;
        internal ulong Maximum;
    }

    [DllImport("libc", EntryPoint = "getrlimit", SetLastError = true)]
    private static extern int GetLimit(int resource, out Limits limits);
    [DllImport("libc", EntryPoint = "setrlimit", SetLastError = true)]
    private static extern int SetLimit(int resource, ref Limits limits);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int GetDescriptorFlags(int descriptor, int command);
}
