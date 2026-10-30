using System.Reflection;
using System.Security.Cryptography;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.GitData;
using ContractScribe.GitHub.NativeGit;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    [ContractScribe.TestSupport.NativeFileSystemFact]
    public async Task Native_checkout_source_fetch_denial_is_transport_failure_without_fallback_or_replay()
    {
        await using var fixture = await Fixture.CreateAsync(targetCount: 2);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var bridge = new GitHubNativeLoopbackBridge(fixture.GitHub, fixture.Outside, deadline.Token);
        bridge.DeniedObjectOid = fixture.GitHub.Commits[fixture.GitHub.BaseOid]["tree"]!["sha"]!.GetValue<string>();
        var environment = fixture.Environment();
        environment["CONTRACTSCRIBE_TEST_GITHUB_GIT_ENDPOINT"] = bridge.Endpoint.AbsoluteUri;
        var result = await fixture.RunArgs(fixture.Args(), environment);
        AssertResult(result, 5, "host-failure");
        Assert.Equal(1, bridge.DeniedObjectRequests);
        // Checkout follows claim acquisition; disposal still releases that
        // claim exactly once. The failed source read creates no publication.
        Assert.Equal(1, bridge.GitMutationsAtObjectDenial);
        Assert.Equal(2, bridge.GitMutations);
        bridge.ImportNativeEffects();
        var document = fixture.GitHub.CoordinationDocument();
        Assert.True(document["claim"]!["expiresUnixSeconds"]!.GetValue<long>() <= fixture.GitHub.ServerNow.ToUnixTimeSeconds());
        Assert.Null(document["publication"]);
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.GitHub.Mutations);
        Assert.Equal(0, fixture.GitHub.PrAttempts);
        Assert.DoesNotContain(fixture.GitHub.Requests, request => request.Method == "GET"
            && (request.Path.Contains("/git/trees/", StringComparison.Ordinal)
                || request.Path.Contains("/git/blobs/", StringComparison.Ordinal)));
        await fixture.AssertSourceUnchanged();
    }

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData("commit")]
    [InlineData("tree")]
    [InlineData("blob")]
    public async Task Native_proposal_basis_fetch_denial_is_transport_failure_without_fallback_or_replay(string kind)
    {
        var directory = Path.Join(Path.GetTempPath(), "native-source-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            byte[] original = "original\n"u8.ToArray(), candidate = "candidate\n"u8.ToArray();
            await using var api = new GitHubProposalLoopbackServer(new Dictionary<string, byte[]> { ["App.cs"] = original });
            await using var bridge = new GitHubNativeLoopbackBridge(api, directory, deadline.Token);
            var root = api.Commits[api.BaseOid]["tree"]!["sha"]!.GetValue<string>();
            bridge.DeniedObjectOid = kind switch { "commit" => api.BaseOid, "tree" => root, _ => api.Trees[root].Single().Oid };
            using var registration = (IDisposable)typeof(GitHubTransportTestHook)
                .GetMethod("Register", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [api.Endpoint, null, 30_000])!;
            typeof(GitHubTransportTestHook).GetMethod("RegisterGitOrigin", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [bridge.Endpoint]);
            static string Hash(char value) => new(value, 64);
            static string Sha(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
            var authority = GitHubPublicationFactory.CreateAuthority(new("Owner", "repo", "refs/heads/main", api.BaseOid,
                "campaign", Hash('1'), Hash('2'), Hash('3'), 1, Hash('4'), Sha(candidate), Hash('6'), Hash('7'), Hash('8'),
                "initial", "generation", GitHubPublicationTransitionKind.Initial,
                new(Hash('a'), 100, [new("target-0000", "work-0000")], ["work-0000"]),
                [new("App.cs", Sha(original), Sha(candidate), 1, 1, 2, 1, 1)], null));
            var payload = GitHubPublicationFactory.CreatePayload(authority, [new("App.cs", candidate)]);
            using var client = GitHubApiClient.Create(authority, GitHubTransportTestHook.Placeholder,
                storage: new GitHubPrivateStorage(directory, () => { }, deadline.Token));
            await using var coordination = GitHubCoordinationStore.Create(client);
            var read = await coordination.ReadCurrentAsync(deadline.Token);
            Assert.NotNull(read.Read);
            // The ordinary checkpoint observation initializes the native reader;
            // an absent ref grants no lease or mutation authority to this proof.
            Assert.Equal(CampaignCheckpointReadKind.NotFound, (await coordination.ReadAsync(deadline.Token)).Kind);
            Assert.True(coordination.UsesNativeData);
            var result = await GitHubProposalStore.Create(client, coordination).InspectAsync(read.Read, payload, deadline.Token);
            Assert.Equal(GitHubProposalOutcome.Failed, result.Outcome);
            Assert.Equal(GitHubProposalFailureKind.Transport, result.Failure?.Kind);
            Assert.Equal(GitHubFailureCode.InvalidResponse, result.Failure?.Transport?.Code);
            Assert.Equal(GitHubDelivery.Read, result.Failure?.Delivery);
            Assert.Null(result.Failure?.Context);
            Assert.Equal(1, bridge.DeniedObjectRequests);
            Assert.Equal(0, bridge.GitMutations);
            Assert.Equal(0, api.Mutations);
            Assert.Equal(0, api.PrAttempts);
            Assert.DoesNotContain(api.Requests, request => request.Method == "GET"
                && (request.Path.Contains("/git/commits/", StringComparison.Ordinal)
                    || request.Path.Contains("/git/trees/", StringComparison.Ordinal)
                    || request.Path.Contains("/git/blobs/", StringComparison.Ordinal)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
