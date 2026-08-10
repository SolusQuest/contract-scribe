using Xunit.Abstractions;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed class IntegrationTestCollectionOrderer : ITestCollectionOrderer
{
    // Start the bounded process lanes and the low-CPU timeout wait before short
    // collections so worker ordering does not create a long process-test tail.
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
