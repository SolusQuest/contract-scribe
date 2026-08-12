using Xunit.Abstractions;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed class IntegrationTestCollectionOrderer : ITestCollectionOrderer
{
    // Start the named process lanes and low-CPU timeout wait before short
    // collections to reduce the process-test tail. This priority order is not a
    // global bound on subprocess work performed by other collections.
    public IEnumerable<ITestCollection> OrderTestCollections(
        IEnumerable<ITestCollection> testCollections) =>
        testCollections
            .OrderBy(CollectionPriority)
            .ThenBy(collection => collection.DisplayName, StringComparer.Ordinal);

    private static int CollectionPriority(ITestCollection collection)
    {
        if (collection.DisplayName == "Integration process lane 1")
        {
            return 0;
        }
        if (collection.DisplayName == "Integration process lane 2")
        {
            return 1;
        }
        if (collection.DisplayName.Contains(
                nameof(AuditCliWorkspaceTimeoutProcessTests),
                StringComparison.Ordinal))
        {
            return 2;
        }
        return 3;
    }
}
