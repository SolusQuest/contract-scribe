namespace ContractScribe.Roslyn.IntegrationTests;

[Collection("Integration process lane 2")]
public sealed class AuditCliPublicationProcessTests
{
    [Fact]
    public Task TerminalCommit_RemainsAuthoritativeWhenPresentationWriteFails() =>
        AuditCliProcessTests.AssertTerminalCommitRemainsAuthoritativeWhenPresentationWriteFailsAsync();
}
