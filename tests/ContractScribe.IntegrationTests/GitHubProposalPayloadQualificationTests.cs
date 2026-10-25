using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ContractScribe.Core;
using Xunit.Abstractions;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class GitHubProposalCliProcessTests
{
    private readonly ITestOutputHelper payloadOutput;
    public GitHubProposalCliProcessTests(ITestOutputHelper output) => payloadOutput = output;

    [ContractScribe.TestSupport.NativeFileSystemTheory]
    [InlineData(0)]
    [InlineData(250)]
    public async Task Actual_CLI_payload_reuse_completes_M2_publication_and_cleanup_with_the_original_Host_limit(int delayMilliseconds)
    {
        await using var fixture = await Fixture.CreateAsync();
        long? hostStart = null, publicationStart = null, publishedTail = null;
        var calls = new List<(string Method, string Path, string Kind, bool Claim, bool Checkpoint, bool Plan)>();
        var stages = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var claimExpirations = new HashSet<long>();
        string? proposalHead = null;
        long? lastExpiry = null;
        var proposalAdvances = 0;
        var renewals = 0;
        fixture.GitHub.BeforeResponse = request =>
        {
            var observations = File.Exists(fixture.Observations) ? File.ReadAllLines(fixture.Observations) : [];
            var start = observations.SingleOrDefault(line => line.StartsWith("finalization-start:", StringComparison.Ordinal));
            if (start is not null) hostStart ??= long.Parse(start.AsSpan("finalization-start:".Length), CultureInfo.InvariantCulture);
            if (hostStart is null) return null;
            if (observations.Contains("before-publication", StringComparer.Ordinal)) publicationStart ??= Stopwatch.GetTimestamp();
            var document = fixture.GitHub.CoordinationDocument();
            var head = fixture.GitHub.Refs.Single(pair => pair.Key.Contains("/coordination/", StringComparison.Ordinal)).Value;
            var root = fixture.GitHub.Trees[fixture.GitHub.Commits[head]["tree"]!["sha"]!.GetValue<string>()];
            var leaf = fixture.GitHub.Trees[root.Single(entry => entry.Path == ".contract-scribe").Oid];
            var stateOid = leaf.Single(entry => entry.Path == "coordination-state-v1.json").Oid;
            var kind = request.Method == "POST" ? "mutation"
                : request.Path.Contains("/git/commits/", StringComparison.Ordinal) ? "commit"
                : request.Path.Contains("/git/trees/", StringComparison.Ordinal) ? "tree"
                : request.Path.Contains("/git/blobs/", StringComparison.Ordinal) ? "blob" : "mutable";
            bool Blob(string? oid) => oid is not null && request.Method == "GET"
                && request.Path.EndsWith("/git/blobs/" + oid, StringComparison.Ordinal);
            calls.Add((request.Method, request.Path, kind, Blob(stateOid),
                Blob(document["checkpoint"]?["oid"]?.GetValue<string>()), Blob(document["plan"]?["oid"]?.GetValue<string>())));
            if (document["claim"]?["expiresUnixSeconds"] is { } expires)
            {
                var expiry = expires.GetValue<long>();
                if (lastExpiry is { } prior && expiry > prior) renewals++;
                lastExpiry = expiry;
                claimExpirations.Add(expiry);
            }
            var proposal = fixture.GitHub.Refs.SingleOrDefault(pair => pair.Key.Contains("/proposals/", StringComparison.Ordinal)).Value;
            if (proposal is not null && proposal != proposalHead) { proposalHead = proposal; proposalAdvances++; }
            if (document["publication"] is not null)
            {
                var stage = fixture.GitHub.Coordination()["stage"]!.GetValue<string>();
                if (stage == "published") publishedTail ??= Stopwatch.GetTimestamp();
                if (request.Method == "GET")
                {
                    if (!stages.TryGetValue(stage, out var paths)) stages[stage] = paths = new(StringComparer.Ordinal);
                    paths.Add(request.Path);
                }
            }
            return delayMilliseconds == 0 ? null : Task.Delay(delayMilliseconds);
        };
        var wall = Stopwatch.StartNew();
        using var process = CampaignCliProcessTests.Start(fixture.Args(), fixture.Environment());
        CampaignCliProcessTests.ProcessResult result;
        try
        {
            // A test observation budget, not a change to the product's 300s Host.
            await process.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(delayMilliseconds == 0 ? 3 : 6));
            result = await process.CompleteAsync();
        }
        finally { await CampaignCliProcessTests.StopAsync(process); }
        wall.Stop();
        var end = Stopwatch.GetTimestamp();
        var hostMilliseconds = hostStart is { } started ? Stopwatch.GetElapsedTime(started, end).TotalMilliseconds : (double?)null;
        var summary = new
        {
            responseDelayMilliseconds = delayMilliseconds,
            hostLimitMilliseconds = 300000,
            actualHostStartObserved = hostStart is not null,
            hostThroughProcessExitMilliseconds = hostMilliseconds,
            fullCliWallMilliseconds = wall.Elapsed.TotalMilliseconds,
            prePublicationUpperBoundMilliseconds = hostStart is { } h && publicationStart is { } p ? Stopwatch.GetElapsedTime(h, p).TotalMilliseconds : (double?)null,
            publishedTailIncludingCleanupMilliseconds = publishedTail is { } tail ? Stopwatch.GetElapsedTime(tail, end).TotalMilliseconds : (double?)null,
            physicalCallsInsideHost = calls.Count,
            callsByKind = calls.GroupBy(call => call.Kind).ToDictionary(group => group.Key, group => group.Count()),
            actualCurrentClaimBodyGets = calls.Count(call => call.Claim),
            actualCheckpointBodyGets = calls.Count(call => call.Checkpoint),
            actualPlanBodyGets = calls.Count(call => call.Plan),
            distinctConfirmedClaimExpirations = claimExpirations.Count,
            confirmedRenewalsInsideHost = renewals,
            proposalRefAdvances = proposalAdvances,
            observedPublicationStages = stages.Keys.Order(StringComparer.Ordinal).ToArray(),
            fixture.GitHub.PrAttempts,
            fixture.GitHub.PublicationWrites,
            result.ExitCode,
        };
        payloadOutput.WriteLine(JsonSerializer.Serialize(summary));
        payloadOutput.WriteLine(result.Stdout);
        AssertResult(result, 0, "published");
        Assert.NotNull(hostMilliseconds);
        Assert.True(hostMilliseconds is >= 0 and <= 300000, "Host through cleanup/process exit: " + hostMilliseconds);
        Assert.NotNull(publicationStart);
        Assert.NotNull(publishedTail);
        Assert.True(calls.Count(call => call.Claim) > 0);
        Assert.True(calls.Count(call => call.Checkpoint) > 0);
        Assert.True(calls.Count(call => call.Plan) > 0);
        Assert.Single(File.ReadAllLines(fixture.Observations), line => line.StartsWith("finalization-start:", StringComparison.Ordinal));
        Assert.Equal(1, fixture.GitHub.PrAttempts);
        Assert.Equal(1, proposalAdvances);
        Assert.Single(fixture.GitHub.PullRequests);
        var final = fixture.Checkpoint();
        Assert.Null(final.State.ActiveReservation);
        Assert.True(final.State.LineageCharges.PatchValidationInvocations > 0);
        // The child has exited after DisposeAsync, so the original owner released
        // its claim. A timeout or an incomplete cleanup cannot satisfy this.
        var claim = fixture.GitHub.CoordinationDocument()["claim"]!.AsObject();
        Assert.True(claim["expiresUnixSeconds"]!.GetValue<long>() <= fixture.GitHub.ServerNow.ToUnixTimeSeconds());
        await AssertActualActionConsumer(fixture.Outside, result.Stdout, result.ExitCode, successful: true);
        await fixture.AssertSourceUnchanged();
    }
}
