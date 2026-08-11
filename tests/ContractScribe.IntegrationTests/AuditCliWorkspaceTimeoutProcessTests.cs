namespace ContractScribe.Roslyn.IntegrationTests;

public sealed class AuditCliWorkspaceTimeoutProcessTests
{
    [Fact]
    public Task WorkspaceLoadTimeout_UsesTheRealBlockingGeneratorSeam() =>
        AuditCliProcessTests.AssertWorkspaceLoadTimeoutUsesTheRealBlockingGeneratorSeamAsync();
}
