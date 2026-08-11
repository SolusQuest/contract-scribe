using Xunit;

// Two serial process lanes cap active build/Loader/CLI work. The third worker is
// reserved for isolated fixture checks and the intentionally waiting timeout case.
[assembly: CollectionBehavior(MaxParallelThreads = 3)]
[assembly: TestCollectionOrderer(
    "ContractScribe.Roslyn.IntegrationTests.IntegrationTestCollectionOrderer",
    "ContractScribe.IntegrationTests")]
