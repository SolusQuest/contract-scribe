using Xunit;

// Three workers plus the collection orderer are the scheduling configuration
// observed to pass on the required Ubuntu runner. Named process lanes reduce
// overlap for their members but do not classify or cap every subprocess launch.
[assembly: CollectionBehavior(MaxParallelThreads = 3)]
[assembly: TestCollectionOrderer(
    "ContractScribe.Roslyn.IntegrationTests.IntegrationTestCollectionOrderer",
    "ContractScribe.IntegrationTests")]
