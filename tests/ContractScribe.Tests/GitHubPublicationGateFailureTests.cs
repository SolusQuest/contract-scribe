using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Publication;
using ContractScribe.GitHub.PullRequests;
using ContractScribe.GitHub.Transport;
using Branch = ContractScribe.Tests.GitHubProposalBranchTests;

namespace ContractScribe.Tests;

public sealed partial class GitHubPublicationReconcilerTests
{
    [Theory]
    [InlineData("clean", GitHubPublicationResultKind.Published)]
    [InlineData("slow-renew", GitHubPublicationResultKind.Published)]
    [InlineData("own-renew", GitHubPublicationResultKind.Published)]
    [InlineData("foreign-ref", GitHubPublicationResultKind.Conflict)]
    [InlineData("owner-loss", GitHubPublicationResultKind.Conflict)]
    [InlineData("lease-expired", GitHubPublicationResultKind.Conflict)]
    [InlineData("deadline-direct-preflight", GitHubPublicationResultKind.Timeout)]
    [InlineData("foreign-ref-later-deadline", GitHubPublicationResultKind.Conflict)]
    [InlineData("deadline-preflight", GitHubPublicationResultKind.Timeout)]
    [InlineData("deadline-content-preflight", GitHubPublicationResultKind.Timeout)]
    [InlineData("caller-content-preflight", GitHubPublicationResultKind.Cancelled)]
    [InlineData("recovery-cancel-content-preflight", GitHubPublicationResultKind.Timeout)]
    [InlineData("preflight-permission", GitHubPublicationResultKind.Permission)]
    [InlineData("preflight-missing-read", GitHubPublicationResultKind.Conflict)]
    [InlineData("r5-deadline-ref-fence", GitHubPublicationResultKind.Timeout)]
    [InlineData("r5-caller-ref-fence", GitHubPublicationResultKind.Cancelled)]
    [InlineData("r5-deadline-post-fence", GitHubPublicationResultKind.Timeout)]
    [InlineData("r5-caller-post-fence", GitHubPublicationResultKind.Cancelled)]
    [InlineData("r5-missing-ref-post-fence", GitHubPublicationResultKind.Conflict)]
    [InlineData("permission-content-verification", GitHubPublicationResultKind.Permission)]
    [InlineData("lost-ack-foreign-content-verification", GitHubPublicationResultKind.Conflict)]
    public async Task Actual_checkpoint_publication_preserves_gate_failure_and_first_cause(
        string fault, GitHubPublicationResultKind expected)
    {
        var remote = new Remote { ServerNow = DateTimeOffset.UtcNow, ExpectedPublisher = GitHubPublicationPrincipal.ActionsBot };
        var artifact = GitHubCoordinationRefTests.PublicationCheckpointFixture("campaign");
        var file = Assert.Single(artifact.State.CandidateObservation!.ChangedFiles);
        var original = Assert.Single(Enumerable.Range(0, 2).Select(index =>
            Encoding.UTF8.GetBytes($"public void Operation{index:D3}(string value) {{ }}")),
            bytes => Convert.ToHexStringLower(SHA256.HashData(bytes)) == file.OriginalFileSha256);
        byte[] candidate = Encoding.UTF8.GetBytes(file.Path + ":candidate");
        Assert.Equal(file.CandidateFileSha256, Convert.ToHexStringLower(SHA256.HashData(candidate)));
        remote.Git.SetCheckpointFixtureFile(file.Path, original);
        var authority = GitHubPublicationTestBatch.BindCheckpoint(Branch.Authority(remote.Git), artifact);
        var payload = GitHubPublicationFactory.CreatePayload(authority, [new(file.Path, candidate)]);
        using var hook = (IDisposable)typeof(GitHubTransportTestHook).GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new Uri("http://127.0.0.1:43219/"), new Handler(remote), 30_000])!;
        using var client = GitHubApiClient.CreateCheckpoint(ValidatedGitHubCheckpointAuthority.Create(
            authority.RepositoryOwner, authority.RepositoryName, authority.TargetRef,
            authority.CampaignLineage, authority.ExpectedBaseCommitOid), GitHubTransportTestHook.Placeholder);
        var store = GitHubCoordinationStore.Create(client);
        var clock = new GitHubPublicationFinalizationTests.DeadlineClock();
        using var caller = new CancellationTokenSource();
        using var host = new GitHubPublicationFinalization(caller.Token, clock);
        var injected = false;
        var renewalCas = false;
        try
        {
            Assert.Equal(CampaignCheckpointReadKind.Found, await store.AcquireCheckpointAsync(default));
            SeedTerminalCheckpoint(remote, authority, artifact);
            if (fault == "slow-renew") remote.SecondsPerRequest = 1;
            var token = host.Begin(store.CheckpointToken);
            client.BindFinalizationDeadline(host.DeadlineToken, () => host.TimedOut);
            var lostContentAcknowledgement = false;
            if (fault == "lost-ack-foreign-content-verification") remote.AfterMutation = (ordinal, body) =>
            {
                if (JsonNode.Parse(body ?? "null")?["message"]?.GetValue<string>()
                    .StartsWith("ContractScribe proposal v1\n", StringComparison.Ordinal) != true) return;
                remote.LoseAt = ordinal;
                remote.LoseAfter = true;
                lostContentAcknowledgement = true;
                remote.AfterMutation = null;
            };
            remote.BeforeRequest = (request, _) =>
            {
                // Inject at actual IO inside each private producer, not a replacement guard.
                var frames = new StackTrace().GetFrames();
                var callback = frames.Any(frame => frame.GetMethod()?.DeclaringType?.FullName is { } name
                    && name.Contains("GitHubPublicationReconciler", StringComparison.Ordinal)
                    && name.Contains("ctor", StringComparison.Ordinal));
                var finalVerification = frames.Any(frame => frame.GetMethod()?.DeclaringType?.FullName?
                    .Contains("FinalVerification", StringComparison.Ordinal) == true);
                var absenceFence = frames.Any(frame => frame.GetMethod()?.DeclaringType?.FullName?
                    .Contains("RevalidateAbsenceAsync", StringComparison.Ordinal) == true);
                GitHubCoordinationStage? stage = CurrentCheckpointDocument(remote, authority).Publication is not null
                    ? remote.State(authority).Stage : null;
                var r5Stage = fault.Contains("post-fence", StringComparison.Ordinal)
                    ? GitHubCoordinationStage.ProposalRefAdvanced : GitHubCoordinationStage.ContentCreated;
                var atFault = fault.EndsWith("content-verification", StringComparison.Ordinal)
                    ? finalVerification && stage == GitHubCoordinationStage.Claimed
                        && (fault == "permission-content-verification" ? callback : !callback)
                    : fault == "deadline-preflight" ? callback
                    : fault == "deadline-direct-preflight" ? !callback && !absenceFence
                        && frames.Any(frame => frame.GetMethod()?.DeclaringType?.FullName?
                            .Contains("PreflightAsync", StringComparison.Ordinal) == true)
                    : fault.StartsWith("r5-", StringComparison.Ordinal) ? absenceFence && stage == r5Stage
                    : callback && stage == GitHubCoordinationStage.ContentCreated;
                if (atFault && !injected && fault is not ("clean" or "slow-renew"))
                {
                    injected = true;
                    if (fault.Contains("deadline", StringComparison.Ordinal) && fault != "foreign-ref-later-deadline") clock.Expire();
                    else if (fault.Contains("caller", StringComparison.Ordinal)) caller.Cancel();
                    else if (fault.StartsWith("foreign-ref", StringComparison.Ordinal) || fault == "lost-ack-foreign-content-verification")
                        remote.Git.Refs[GitHubPublicationFactory.CreateProposalRef(authority)] = authority.ExpectedBaseCommitOid;
                    else if (fault == "r5-missing-ref-post-fence")
                        remote.Git.Refs.Remove(GitHubPublicationFactory.CreateProposalRef(authority));
                    else if (fault == "lease-expired") remote.ServerNow = remote.ServerNow!.Value.AddSeconds(301);
                    else if (fault is "own-renew" or "recovery-cancel-content-preflight") remote.ServerNow = remote.ServerNow!.Value.AddSeconds(61);
                    else if (fault == "owner-loss") ReplaceCheckpointOwner(remote, authority);
                    else if (fault is "preflight-permission" or "preflight-missing-read" or "permission-content-verification")
                    {
                        remote.RejectRead = _ =>
                        {
                            remote.RejectRead = null;
                            return fault == "preflight-missing-read" ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden;
                        };
                    }
                }
                if (fault == "recovery-cancel-content-preflight" && injected && stage == GitHubCoordinationStage.ContentCreated)
                {
                    if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/graphql") renewalCas = true;
                    if (renewalCas && request.Method == HttpMethod.Get
                        && request.RequestUri!.AbsolutePath.Contains("/git/commits/", StringComparison.Ordinal)) clock.Expire();
                }
                return Task.CompletedTask;
            };
            var observed = await GitHubPublicationFacade.PublishCheckpointAsync(client, store, authority, payload, token);
            if (fault is not ("clean" or "slow-renew")) Assert.True(injected);
            if (fault == "foreign-ref-later-deadline")
            {
                Assert.False(host.TimedOut);
                clock.Expire();
            }
            Assert.Equal(expected, observed.Result.Kind);
            if (fault.Contains("post-fence", StringComparison.Ordinal))
            {
                var residual = Assert.IsType<GitHubPublicationRefResidual>(observed.Result.RefResidual);
                if (fault == "r5-missing-ref-post-fence")
                {
                    Assert.False(remote.Git.Refs.ContainsKey(GitHubPublicationFactory.CreateProposalRef(authority)));
                    Assert.Equal(remote.State(authority).ProposalCommitOid, residual.ObservedOid);
                }
                else Assert.Equal(remote.Git.Refs[GitHubPublicationFactory.CreateProposalRef(authority)], residual.ObservedOid);
                Assert.NotNull(observed.Result.RemoteFailure);
            }
            else if (fault.Contains("ref-fence", StringComparison.Ordinal)
                || fault is "deadline-content-preflight" or "caller-content-preflight"
                    or "permission-content-verification" or "lost-ack-foreign-content-verification")
            {
                var residual = Assert.IsType<GitHubPublicationContentResidual>(observed.Result.ContentResidual);
                Assert.True(remote.Git.Commits.ContainsKey(residual.ExpectedOid));
                Assert.NotNull(observed.Result.RemoteFailure);
            }
            if (expected is GitHubPublicationResultKind.Cancelled or GitHubPublicationResultKind.Timeout
                or GitHubPublicationResultKind.Permission || fault is "preflight-missing-read" or "r5-missing-ref-post-fence")
            {
                var diagnostic = Assert.IsType<GitHubPublicationDiagnostic>(observed.Diagnostic);
                Assert.Equal(fault is "preflight-missing-read" or "r5-missing-ref-post-fence" ? GitHubFailureCode.NotFound
                    : expected == GitHubPublicationResultKind.Permission ? GitHubFailureCode.Permission
                    : expected == GitHubPublicationResultKind.Timeout ? GitHubFailureCode.Timeout : GitHubFailureCode.Cancelled,
                    diagnostic.RecoveryCode ?? diagnostic.TransportCode);
                if (fault.StartsWith("preflight-", StringComparison.Ordinal) || fault == "r5-missing-ref-post-fence")
                    Assert.Equal(fault == "preflight-permission" ? 403 : 404, diagnostic.TransportHttpStatus);
            }
            if (fault == "permission-content-verification")
                Assert.Equal(403, observed.Diagnostic!.RecoveryHttpStatus);
            if (fault == "lost-ack-foreign-content-verification")
            {
                Assert.True(lostContentAcknowledgement);
                Assert.Equal(GitHubFailureCode.ResponseLost, observed.Diagnostic!.TransportCode);
                Assert.Null(observed.Diagnostic.RecoveryCode);
            }
            var published = expected == GitHubPublicationResultKind.Published;
            Assert.Equal(published ? 1 : 0, remote.PrAttempts);
            Assert.Equal(published || fault.Contains("post-fence", StringComparison.Ordinal) ? 1 : 0, remote.Git.ProposalWrites);
            if (fault.StartsWith("foreign-ref", StringComparison.Ordinal))
                Assert.Equal(authority.ExpectedBaseCommitOid, remote.Git.Refs[GitHubPublicationFactory.CreateProposalRef(authority)]);
            Assert.Equal(artifact.Sha256, CurrentCheckpointDocument(remote, authority).Checkpoint!.Sha256);
        }
        finally
        {
            remote.BeforeRequest = null;
            remote.SecondsPerRequest = 0;
            await store.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static GitHubCoordinationDocument CurrentCheckpointDocument(Remote remote, ValidatedGitHubPublicationAuthority authority)
    {
        var head = remote.Git.Refs[GitHubPublicationFactory.CreateCoordinationRef(authority)];
        var leaf = Assert.Single(remote.Git.Trees[remote.Git.Commits[head].TreeOid]).Oid;
        return GitHubCoordinationDocument.Decode(remote.Git.Blobs[
            remote.Git.Trees[leaf].Single(entry => entry.Path == GitHubCoordinationObjects.StatePath).Oid]);
    }

    private static void SeedTerminalCheckpoint(Remote remote, ValidatedGitHubPublicationAuthority authority, CampaignCheckpointArtifact artifact)
    {
        var head = remote.Git.Refs[GitHubPublicationFactory.CreateCoordinationRef(authority)];
        var document = CurrentCheckpointDocument(remote, authority);
        var bytes = artifact.ExactUtf8Json.ToArray();
        var blobOid = GitHubCoordinationObjects.ObjectOid("blob", bytes);
        remote.Git.Blobs[blobOid] = bytes;
        byte[] plan = "{}"u8.ToArray();
        var planOid = GitHubCoordinationObjects.ObjectOid("blob", plan);
        remote.Git.Blobs[planOid] = plan;
        StoreCheckpointDocument(remote, authority, GitHubCoordinationObjects.Prepare(document.RetainAt(head) with
        {
            PredecessorOid = head,
            Checkpoint = new(blobOid, artifact.Sha256, artifact.CheckpointRevision),
            Plan = new(planOid, Convert.ToHexStringLower(SHA256.HashData(plan)), artifact.State.Snapshot.ExecutionCommitmentSha256, artifact.State.Batch.Identity),
        }));
    }

    private static void ReplaceCheckpointOwner(Remote remote, ValidatedGitHubPublicationAuthority authority)
    {
        var head = remote.Git.Refs[GitHubPublicationFactory.CreateCoordinationRef(authority)];
        var document = CurrentCheckpointDocument(remote, authority).RetainAt(head);
        StoreCheckpointDocument(remote, authority, GitHubCoordinationObjects.Prepare(document with
        {
            PredecessorOid = head,
            Claim = document.Claim! with { Owner = new string('f', 64), Epoch = document.Claim.Epoch + 1 },
        }));
    }

    private static void StoreCheckpointDocument(Remote remote, ValidatedGitHubPublicationAuthority authority, GitHubPreparedCoordination prepared)
    {
        remote.Git.Blobs[prepared.BlobOid] = prepared.StateBytes.ToArray();
        remote.Git.Trees[prepared.LeafTreeOid] = GitHubCoordinationObjects.LeafEntries(prepared);
        remote.Git.Trees[prepared.RootTreeOid] = GitHubCoordinationObjects.RootEntries(prepared);
        var commit = GitHubCoordinationObjects.CommitRequest(prepared);
        remote.Git.Commits[prepared.CommitOid] = new(prepared.CommitOid, prepared.RootTreeOid, [commit.ParentOid], prepared.Message, commit.Author, commit.Committer);
        remote.Git.Refs[GitHubPublicationFactory.CreateCoordinationRef(authority)] = prepared.CommitOid;
    }
}
