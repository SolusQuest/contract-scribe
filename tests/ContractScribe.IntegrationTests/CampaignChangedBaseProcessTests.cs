using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

[Collection("Integration process lane 1")]
public sealed class CampaignChangedBaseProcessTests
{
    public static TheoryData<string, bool, bool> SupersessionCrashCases
    {
        get
        {
            var path = Path.Join(
                CampaignCliProcessTests.RepositoryRoot,
                "tests", "fixtures", "campaign", "changed-base", "process-boundary-matrix.json");
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var data = new TheoryData<string, bool, bool>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var authority = item.GetProperty("expectedAuthority").GetString();
                data.Add(
                    item.GetProperty("hook").GetString()!,
                    authority is "successor" or "successor-without-reservation",
                    authority is "successor-without-reservation");
            }
            return data;
        }
    }

    [Fact]
    public async Task ExactFixtureRevisions_WithIdenticalAuditAndContent_RotateAllCurrentAuthority()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await ProcessFixture.CreateAsync(
            executable: true,
            maximumCampaignElapsedMilliseconds: 600_000);
        CopyRevision("revision-a", fixture.Repository.Root);

        var first = await CampaignCliProcessTests.RunAsync(
            fixture.Args("start", "snapshot.changed-base.a"), TimeSpan.FromMinutes(5));
        var predecessor = ReadArtifact(fixture.StatePath);
        CampaignCliProcessTests.AssertCampaign(first, 0, "campaign.complete", predecessor.CheckpointRevision);
        var predecessorKeys = predecessor.State.WorkItems.Select(item => item.WorkItemKey).ToArray();
        Assert.NotEmpty(predecessorKeys);
        Assert.NotNull(predecessor.State.CandidateObservation);
        Assert.NotNull(predecessor.State.CumulativeOutcome);

        await InterruptAtAsync(fixture, "resume", "snapshot.changed-base.a",
            CampaignProcessBoundaryHooks.PatchBeforeDispatch);
        var ambiguous = ReadArtifact(fixture.StatePath);
        var oldReservation = Assert.IsType<CampaignPatchReservation>(ambiguous.State.ActiveReservation);
        var providerCallsBeforeSupersession = fixture.Server.RequestCount;
        var oldEvidence = ReadTargetEvidence(Assert.Single(fixture.Server.RequestBodies));

        CopyRevision("revision-b", fixture.Repository.Root);
        await InterruptAtAsync(fixture, "resume", "snapshot.changed-base.b",
            CampaignProcessBoundaryHooks.CheckpointAfterReplacementBeforeReadback);
        var successor = ReadArtifact(fixture.StatePath);
        var successorKeys = successor.State.WorkItems.Select(item => item.WorkItemKey).ToArray();

        Assert.Equal(predecessor.State.Snapshot.RepositoryCommitmentSha256,
            successor.State.Snapshot.RepositoryCommitmentSha256);
        Assert.Equal(predecessor.State.Snapshot.InputCommitmentSha256,
            successor.State.Snapshot.InputCommitmentSha256);
        Assert.NotEqual(predecessor.State.Snapshot.ExecutionCommitmentSha256,
            successor.State.Snapshot.ExecutionCommitmentSha256);
        Assert.False(predecessorKeys.SequenceEqual(successorKeys));
        Assert.Equal(ambiguous.Sha256, successor.State.Predecessor!.FinalCheckpointSha256);
        Assert.Equal("patch", successor.State.Predecessor.Reservation!.Kind);
        Assert.Equal(oldReservation.PatchRequestSha256,
            successor.State.Predecessor.Reservation.CorrelationSha256);
        Assert.Equal(oldReservation.ElapsedMilliseconds,
            successor.State.Predecessor.Reservation.ConservativeCharge);
        Assert.True(successor.State.Predecessor.Candidate.AcceptedCount > 0);
        Assert.Empty(successor.State.Predecessor.CompletedOperations);
        Assert.All(successor.State.WorkItems, item =>
        {
            Assert.Equal(CampaignWorkStatus.Planned, item.Status);
            Assert.Null(item.TrustedProposal);
            Assert.Equal(0, item.OuterAttemptCount);
            Assert.Equal(0, item.CandidateAttemptCount);
        });
        Assert.Null(successor.State.ActiveReservation);
        Assert.Null(successor.State.CandidateObservation);
        Assert.Null(successor.State.CumulativeOutcome);
        Assert.True(successor.State.LineageCharges.PatchValidationInvocations
            >= ambiguous.State.LineageCharges.PatchValidationInvocations);
        Assert.Equal(providerCallsBeforeSupersession, fixture.Server.RequestCount);

        var completed = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.changed-base.b"), TimeSpan.FromMinutes(5));
        var completedArtifact = ReadArtifact(fixture.StatePath);
        CampaignCliProcessTests.AssertCampaign(
            completed, 0, "campaign.complete", completedArtifact.CheckpointRevision);
        Assert.Equal(providerCallsBeforeSupersession + 1, fixture.Server.RequestCount);
        var newEvidence = ReadTargetEvidence(fixture.Server.RequestBodies[^1]);
        AssertCollisionCommitments(oldEvidence, newEvidence);
        Assert.DoesNotContain(completedArtifact.State.WorkItems,
            item => predecessorKeys.Contains(item.WorkItemKey, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(SupersessionCrashCases))]
    public async Task SupersessionCrash_LeavesExactlyOneCompleteAuthority(
        string hook,
        bool successorExpected,
        bool noReservationExpected)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await ProcessFixture.CreateAsync(executable: true);
        await InterruptAtAsync(fixture, "start", "snapshot.crash.a",
            CampaignProcessBoundaryHooks.ProposalBeforeReservationCommit);
        var predecessorBytes = await File.ReadAllBytesAsync(fixture.StatePath);
        var predecessor = ReadArtifact(fixture.StatePath);

        await InterruptAtAsync(fixture, "resume", "snapshot.crash.b", hook);
        var observedBytes = await File.ReadAllBytesAsync(fixture.StatePath);
        var observed = ReadArtifact(fixture.StatePath);

        if (successorExpected)
        {
            Assert.Equal("snapshot.crash.b", observed.State.Snapshot.OpaqueSnapshotBinding);
            Assert.Equal(predecessor.Sha256, observed.State.Predecessor!.FinalCheckpointSha256);
            if (noReservationExpected) Assert.Null(observed.State.ActiveReservation);
        }
        else
        {
            Assert.Equal(predecessorBytes, observedBytes);
            Assert.Equal("snapshot.crash.a", observed.State.Snapshot.OpaqueSnapshotBinding);
        }

        var recovered = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.crash.b"), TimeSpan.FromMinutes(5));
        var current = ReadArtifact(fixture.StatePath);
        CampaignCliProcessTests.AssertCampaign(recovered, 0, "campaign.complete", current.CheckpointRevision);
    }

    [Fact]
    public async Task DriftAndProductMismatch_LeaveThePredecessorByteIdenticalWithoutDispatch()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await ProcessFixture.CreateAsync(executable: true);
        await InterruptAtAsync(fixture, "start", "snapshot.drift.a",
            CampaignProcessBoundaryHooks.ProposalBeforeReservationCommit);
        var originalState = await File.ReadAllBytesAsync(fixture.StatePath);
        var originalConfiguration = await File.ReadAllBytesAsync(fixture.ConfigurationPath);
        var sourcePath = Path.Join(fixture.Repository.Root, "App", "App.cs");
        var originalSource = await File.ReadAllBytesAsync(sourcePath);

        await File.WriteAllTextAsync(sourcePath,
            "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App\n{\n    public static void Changed() { }\n}\n");
        var staleSameSnapshot = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.drift.a"), TimeSpan.FromMinutes(3));
        CampaignCliProcessTests.AssertCampaign(
            staleSameSnapshot, 4, "campaign.incompatible-snapshot", 0);
        Assert.Equal(originalState, await File.ReadAllBytesAsync(fixture.StatePath));
        Assert.Equal(0, fixture.Server.RequestCount);
        await File.WriteAllBytesAsync(sourcePath, originalSource);

        await File.WriteAllTextAsync(
            Path.Join(fixture.Repository.Root, "policy.json"),
            CampaignCliProcessTests.OptionalPolicy);
        var policyDrift = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.drift.b"), TimeSpan.FromMinutes(3));
        CampaignCliProcessTests.AssertCampaign(policyDrift, 4, "campaign.incompatible-snapshot", 0);
        Assert.Equal(originalState, await File.ReadAllBytesAsync(fixture.StatePath));
        Assert.Equal(0, fixture.Server.RequestCount);

        await File.WriteAllTextAsync(
            Path.Join(fixture.Repository.Root, "policy.json"),
            CampaignCliProcessTests.RequiredPolicy);
        await CampaignCliProcessTests.WriteConfigurationAsync(
            fixture.ConfigurationPath,
            fixture.Server.Endpoint,
            maximumPatchElapsedMilliseconds: 1);
        var configurationDrift = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.drift.b"), TimeSpan.FromMinutes(3));
        CampaignCliProcessTests.AssertCampaign(configurationDrift, 4, "campaign.incompatible-snapshot", 0);
        Assert.Equal(originalState, await File.ReadAllBytesAsync(fixture.StatePath));
        Assert.Equal(0, fixture.Server.RequestCount);

        var invalidProduct = JsonNode.Parse(originalConfiguration)!.AsObject();
        invalidProduct["planning"]!["productContractRevisionSha256"] = new string('0', 64);
        await File.WriteAllTextAsync(
            fixture.ConfigurationPath,
            invalidProduct.ToJsonString(),
            new UTF8Encoding(false, true));
        var productMismatch = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.drift.b"), TimeSpan.FromMinutes(3));
        CampaignCliProcessTests.AssertCampaign(productMismatch, 4, "campaign.invalid-configuration", null);
        Assert.Equal(originalState, await File.ReadAllBytesAsync(fixture.StatePath));
        Assert.Equal(0, fixture.Server.RequestCount);
    }

    [Fact]
    public async Task TargetEvolution_SelectsOnlyTheFreshAuditAndPlan()
    {
        if (!OperatingSystem.IsLinux()) return;
        var matrixPath = Path.Join(
            CampaignCliProcessTests.RepositoryRoot,
            "tests", "fixtures", "campaign", "changed-base", "target-evolution-matrix.json");
        using var matrix = JsonDocument.Parse(await File.ReadAllBytesAsync(matrixPath));
        await using var fixture = await ProcessFixture.CreateAsync(executable: true);
        var targetEvolutionInputPath = Path.Join(fixture.Repository.Root, "TargetEvolution.slnx");
        const string BaselineTargetEvolutionInput =
            "<Solution><Project Path=\"App/App.csproj\" /></Solution>";
        await File.WriteAllTextAsync(targetEvolutionInputPath, BaselineTargetEvolutionInput);
        fixture.Input = "TargetEvolution.slnx";
        await InterruptAtAsync(fixture, "start", "snapshot.target.a",
            CampaignProcessBoundaryHooks.ProposalAfterProposalReadback);
        var baselineEvidence = ReadTargetEvidence(Assert.Single(fixture.Server.RequestBodies));
        var baselineSourcePath = Path.Join(fixture.Repository.Root, "App", "App.cs");
        var baselineProjectPath = Path.Join(fixture.Repository.Root, "App", "App.csproj");
        var baselineSource = await File.ReadAllBytesAsync(baselineSourcePath);
        var baselineProject = await File.ReadAllBytesAsync(baselineProjectPath);

        var failedPredecessor = ReadArtifact(fixture.StatePath);
        var failedPredecessorBytes = await File.ReadAllBytesAsync(fixture.StatePath);
        await File.WriteAllTextAsync(baselineProjectPath, "<Project>");
        var requestCountBeforeFailure = fixture.Server.RequestCount;
        var failedLoad = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.target.invalid"), TimeSpan.FromMinutes(3));
        CampaignCliProcessTests.AssertCampaign(
            failedLoad, 5, "campaign.load-failure", failedPredecessor.CheckpointRevision);
        Assert.Equal(failedPredecessorBytes, await File.ReadAllBytesAsync(fixture.StatePath));
        Assert.Equal(requestCountBeforeFailure, fixture.Server.RequestCount);

        var index = 0;
        foreach (var evolution in matrix.RootElement.EnumerateArray().Select(item => item.GetString()!))
        {
            await File.WriteAllTextAsync(targetEvolutionInputPath, BaselineTargetEvolutionInput);
            await File.WriteAllBytesAsync(baselineSourcePath, baselineSource);
            await File.WriteAllBytesAsync(baselineProjectPath, baselineProject);
            var changedContextProjectPath = Path.Join(
                fixture.Repository.Root, "App", "ChangedContext.csproj");
            if (File.Exists(changedContextProjectPath)) File.Delete(changedContextProjectPath);
            foreach (var extra in new[] { "Moved.cs", "Alpha.cs", "Zulu.cs" })
            {
                var extraPath = Path.Join(fixture.Repository.Root, "App", extra);
                if (File.Exists(extraPath)) File.Delete(extraPath);
            }
            var predecessor = ReadArtifact(fixture.StatePath);
            var predecessorKeys = predecessor.State.WorkItems.Select(item => item.WorkItemKey).ToHashSet(
                StringComparer.Ordinal);
            var expectsNoWork = await ApplyTargetEvolutionAsync(fixture.Repository.Root, evolution);
            if (string.Equals(evolution, "changed-compilation-context", StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(
                    targetEvolutionInputPath,
                    "<Solution><Project Path=\"App/ChangedContext.csproj\" /></Solution>");
            }
            var requestCount = fixture.Server.RequestCount;
            var snapshot = $"snapshot.target.{++index}";

            if (expectsNoWork)
            {
                var noWork = await CampaignCliProcessTests.RunAsync(
                    fixture.Args("resume", snapshot), TimeSpan.FromMinutes(5));
                var successor = ReadArtifact(fixture.StatePath);
                CampaignCliProcessTests.AssertCampaign(
                    noWork, 0, "campaign.no-work", successor.CheckpointRevision);
                Assert.Empty(successor.State.WorkItems);
                Assert.Equal(requestCount, fixture.Server.RequestCount);
            }
            else if (string.Equals(evolution, "reordered-plan", StringComparison.Ordinal))
            {
                var completed = await CampaignCliProcessTests.RunAsync(
                    fixture.Args("resume", snapshot), TimeSpan.FromMinutes(5));
                var successor = ReadArtifact(fixture.StatePath);
                CampaignCliProcessTests.AssertCampaign(
                    completed, 0, "campaign.complete", successor.CheckpointRevision);
            }
            else
            {
                var hook = string.Equals(
                    evolution,
                    "changed-applicable-components",
                    StringComparison.Ordinal)
                    ? CampaignProcessBoundaryHooks.ProposalAfterClosedReadback
                    : CampaignProcessBoundaryHooks.ProposalAfterProposalReadback;
                await InterruptAtAsync(fixture, "resume", snapshot, hook);
            }

            var current = ReadArtifact(fixture.StatePath);
            Assert.Equal(snapshot, current.State.Snapshot.OpaqueSnapshotBinding);
            Assert.Equal(predecessor.Sha256, current.State.Predecessor!.FinalCheckpointSha256);
            Assert.DoesNotContain(current.State.WorkItems, item => predecessorKeys.Contains(item.WorkItemKey));
            Assert.Null(current.State.ActiveReservation);
            var freshRequests = fixture.Server.RequestBodies.Skip(requestCount).ToArray();
            AssertFreshTargetEvidence(evolution, baselineEvidence, freshRequests);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentSupersession_ProducesOneSuccessorAndNoDuplicateDispatch(bool competing)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await ProcessFixture.CreateAsync(executable: true);
        await InterruptAtAsync(fixture, "start", "snapshot.concurrent.a",
            CampaignProcessBoundaryHooks.ProposalBeforeReservationCommit);
        var predecessorBytes = await File.ReadAllBytesAsync(fixture.StatePath);
        Assert.Equal(0, fixture.Server.RequestCount);

        if (competing)
        {
            await AssertPhysicalLeaseConflictAsync(fixture, predecessorBytes);
        }
        else
        {
            await AssertExactSuccessorAdoptionAsync(fixture);
        }
    }

    private static async Task AssertPhysicalLeaseConflictAsync(
        ProcessFixture fixture,
        byte[] predecessorBytes)
    {
        var acknowledgement = Path.Join(fixture.Outside, "concurrent.holder.ack");
        var release = Path.Join(fixture.Outside, "concurrent.holder.release");
        using var holder = CampaignCliProcessTests.Start(
            fixture.Args("resume", "snapshot.concurrent.b"),
            CampaignCliProcessTests.HookEnvironment(
                CampaignProcessBoundaryHooks.CheckpointBeforeReplacement,
                acknowledgement,
                release));
        try
        {
            await CampaignCliProcessTests.WaitForFileAsync(
                acknowledgement,
                holder,
                CampaignProcessBoundaryHooks.CheckpointBeforeReplacement,
                TimeSpan.FromMinutes(3));
            var blocked = await CampaignCliProcessTests.RunAsync(
                fixture.Args("resume", "snapshot.concurrent.c"), TimeSpan.FromMinutes(5));
            CampaignCliProcessTests.AssertCampaign(blocked, 4, "campaign.lease-conflict", null);
            Assert.Equal(predecessorBytes, await File.ReadAllBytesAsync(fixture.StatePath));
            Assert.Equal(0, fixture.Server.RequestCount);
        }
        finally
        {
            await CampaignCliProcessTests.StopAsync(holder);
        }

        var completed = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.concurrent.b"), TimeSpan.FromMinutes(5));
        var current = ReadArtifact(fixture.StatePath);
        CampaignCliProcessTests.AssertCampaign(completed, 0, "campaign.complete", current.CheckpointRevision);
        Assert.Equal("snapshot.concurrent.b", current.State.Snapshot.OpaqueSnapshotBinding);
        Assert.Null(current.State.ActiveReservation);
        Assert.Equal(1, fixture.Server.RequestCount);
    }

    private static async Task AssertExactSuccessorAdoptionAsync(ProcessFixture fixture)
    {
        var acknowledgement = Path.Join(fixture.Outside, "concurrent.writer.ack");
        var release = Path.Join(fixture.Outside, "concurrent.writer.release");
        using var staleWriter = CampaignCliProcessTests.Start(
            fixture.Args("resume", "snapshot.concurrent.b"),
            CampaignCliProcessTests.HookEnvironment(
                CampaignProcessBoundaryHooks.ProposalBeforeReservationCommit,
                acknowledgement,
                release));
        try
        {
            await CampaignCliProcessTests.WaitForFileAsync(
                acknowledgement,
                staleWriter,
                CampaignProcessBoundaryHooks.ProposalBeforeReservationCommit,
                TimeSpan.FromMinutes(3));
            var successor = ReadArtifact(fixture.StatePath);
            Assert.Equal("snapshot.concurrent.b", successor.State.Snapshot.OpaqueSnapshotBinding);
            Assert.Null(successor.State.ActiveReservation);
            Assert.Equal(0, fixture.Server.RequestCount);

            var adopted = await CampaignCliProcessTests.RunAsync(
                fixture.Args("resume", "snapshot.concurrent.b"), TimeSpan.FromMinutes(5));
            var current = ReadArtifact(fixture.StatePath);
            CampaignCliProcessTests.AssertCampaign(adopted, 0, "campaign.complete", current.CheckpointRevision);
            Assert.Equal("snapshot.concurrent.b", current.State.Snapshot.OpaqueSnapshotBinding);
            Assert.Null(current.State.ActiveReservation);
            Assert.Equal(1, fixture.Server.RequestCount);
        }
        finally
        {
            await CampaignCliProcessTests.StopAsync(staleWriter);
        }

        Assert.Equal(1, fixture.Server.RequestCount);
    }

    [Fact]
    public async Task CompleteM4ProductionPath_RecoversSupersedesAndCompletes()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await ProcessFixture.CreateAsync(
            executable: true,
            maximumCampaignElapsedMilliseconds: 600_000,
            maximumCandidatesPerBlock: 8);
        var first = await CampaignCliProcessTests.RunAsync(
            fixture.Args("start", "snapshot.production.a"), TimeSpan.FromMinutes(5));
        var accepted = ReadArtifact(fixture.StatePath);
        CampaignCliProcessTests.AssertCampaign(first, 0, "campaign.complete", accepted.CheckpointRevision);
        Assert.NotNull(accepted.State.CumulativeOutcome);
        Assert.NotNull(accepted.State.CandidateObservation);
        Assert.Contains(accepted.State.WorkItems, item => item.Status == CampaignWorkStatus.Accepted);
        var providerCalls = fixture.Server.RequestCount;

        await InterruptAtAsync(fixture, "resume", "snapshot.production.a",
            CampaignProcessBoundaryHooks.PatchBeforeDispatch);
        var ambiguous = ReadArtifact(fixture.StatePath);
        Assert.IsType<CampaignPatchReservation>(ambiguous.State.ActiveReservation);
        Assert.NotNull(ambiguous.State.CumulativeOutcome);
        Assert.Equal(providerCalls, fixture.Server.RequestCount);

        var sameSnapshot = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.production.a"), TimeSpan.FromMinutes(5));
        var recovered = ReadArtifact(fixture.StatePath);
        CampaignCliProcessTests.AssertCampaign(
            sameSnapshot, 0, "campaign.complete", recovered.CheckpointRevision);
        Assert.Null(recovered.State.ActiveReservation);
        Assert.True(recovered.State.LineageCharges.PatchValidationInvocations
            >= accepted.State.LineageCharges.PatchValidationInvocations);

        await File.WriteAllTextAsync(
            Path.Join(fixture.Repository.Root, "App", "App.cs"),
            "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App\n{\n    public static void RunAgain() { }\n}\n");
        var changed = await CampaignCliProcessTests.RunAsync(
            fixture.Args("resume", "snapshot.production.b"), TimeSpan.FromMinutes(5));
        var completed = ReadArtifact(fixture.StatePath);
        CampaignCliProcessTests.AssertCampaign(changed, 0, "campaign.complete", completed.CheckpointRevision);
        Assert.Equal("snapshot.production.b", completed.State.Snapshot.OpaqueSnapshotBinding);
        Assert.Equal(recovered.Sha256, completed.State.Predecessor!.FinalCheckpointSha256);
        Assert.True(completed.State.LineageCharges.ProviderRequests.TotalCharged
            >= recovered.State.LineageCharges.ProviderRequests.TotalCharged);
        Assert.All(completed.State.WorkItems, item => Assert.NotEqual(
            ambiguous.State.WorkItems[0].WorkItemKey, item.WorkItemKey));
        Assert.True(fixture.Server.RequestCount > providerCalls);
    }

    private static async Task InterruptAtAsync(
        ProcessFixture fixture,
        string operation,
        string snapshot,
        string hook)
    {
        var id = Guid.NewGuid().ToString("N");
        var acknowledgement = Path.Join(fixture.Outside, id + ".ack");
        using var running = CampaignCliProcessTests.Start(
            fixture.Args(operation, snapshot),
            new Dictionary<string, string?>
            {
                ["DOTNET_STARTUP_HOOKS"] = CampaignCliProcessTests.StartupHookPath,
                ["CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_NAME"] = hook,
                ["CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_ACK"] = acknowledgement,
            });
        try
        {
            await CampaignCliProcessTests.WaitForFileAsync(
                acknowledgement, running, hook, TimeSpan.FromMinutes(3));
            running.Process.Kill(entireProcessTree: true);
            await running.Process.WaitForExitAsync();
            var interrupted = await running.CompleteAsync();
            Assert.Empty(interrupted.Stdout);
            Assert.Empty(interrupted.Stderr);
            Assert.True(CampaignStateJson.Parse(await File.ReadAllBytesAsync(fixture.StatePath)).IsValid);
        }
        finally
        {
            await CampaignCliProcessTests.StopAsync(running);
        }
    }

    private static CampaignCheckpointArtifact ReadArtifact(string statePath)
    {
        var parsed = CampaignStateJson.Parse(File.ReadAllBytes(statePath));
        Assert.True(parsed.IsValid);
        return Assert.IsType<CampaignCheckpointArtifact>(parsed.Artifact);
    }

    private static JsonElement ReadTargetEvidence(byte[] body)
    {
        using var wire = JsonDocument.Parse(body);
        foreach (var message in wire.RootElement.GetProperty("messages").EnumerateArray())
        {
            var content = message.GetProperty("content").GetString();
            if (content is null) continue;
            using var parsed = JsonDocument.Parse(content);
            if (parsed.RootElement.TryGetProperty("authority", out var authority)
                && string.Equals(authority.GetString(), "target-evidence", StringComparison.Ordinal))
            {
                return parsed.RootElement.Clone();
            }
        }
        throw new InvalidDataException("Target evidence message was not found.");
    }

    private static void AssertCollisionCommitments(JsonElement oldEvidence, JsonElement newEvidence)
    {
        var oldTarget = oldEvidence.GetProperty("terminalTarget");
        var newTarget = newEvidence.GetProperty("terminalTarget");
        Assert.Equal(oldTarget.GetProperty("symbolRef").GetRawText(),
            newTarget.GetProperty("symbolRef").GetRawText());
        Assert.Equal(oldTarget.GetProperty("sourceCommitment").GetRawText(),
            newTarget.GetProperty("sourceCommitment").GetRawText());
        Assert.Equal(oldEvidence.GetProperty("applicableComponents").GetRawText(),
            newEvidence.GetProperty("applicableComponents").GetRawText());
        Assert.Equal(
            oldEvidence.GetProperty("evidenceReferences")[0].GetProperty("contentSha256").GetString(),
            newEvidence.GetProperty("evidenceReferences")[0].GetProperty("contentSha256").GetString());
    }

    private static void AssertFreshTargetEvidence(
        string evolution,
        JsonElement baselineEvidence,
        IReadOnlyList<byte[]> requestBodies)
    {
        if (evolution is "disappeared" or "became-compliant")
        {
            Assert.Empty(requestBodies);
            return;
        }

        var evidence = requestBodies.Select(ReadTargetEvidence).ToArray();
        var expectedDocumentationIds = evolution switch
        {
            "changed-applicable-components" => new[] { "M:Fixture.App.Run(System.Int32)" },
            "similar-display-name" => new[] { "M:Fixture.App.RunAgain" },
            "reordered-plan" => new[] { "M:Fixture.App.Alpha", "M:Fixture.App.Zulu" },
            _ => new[] { "M:Fixture.App.Run" },
        };
        Assert.Equal(expectedDocumentationIds, evidence.Select(item => item
            .GetProperty("terminalTarget")
            .GetProperty("symbolRef")
            .GetProperty("documentationCommentId")
            .GetString()));

        var baselineTarget = baselineEvidence.GetProperty("terminalTarget");
        foreach (var item in evidence)
        {
            var target = item.GetProperty("terminalTarget");
            var source = target.GetProperty("sourceCommitment");
            var sourceLocator = source.GetProperty("locator").GetProperty("repository");
            var reference = item.GetProperty("evidenceReferences")[0];
            var referenceLocator = reference.GetProperty("locator").GetProperty("repository");
            Assert.Equal(target.GetProperty("repositoryContextRef").GetString(),
                reference.GetProperty("repositoryContextRef").GetString());
            Assert.Equal(sourceLocator.GetProperty("path").GetString(),
                referenceLocator.GetProperty("path").GetString());
            Assert.Equal(source.GetProperty("contentSha256").GetString(),
                reference.GetProperty("contentSha256").GetString());
            Assert.Equal(64, source.GetProperty("contentSha256").GetString()!.Length);
            Assert.True(sourceLocator.GetProperty("span").GetProperty("end").GetInt32()
                > sourceLocator.GetProperty("span").GetProperty("start").GetInt32());
        }

        var firstTarget = evidence[0].GetProperty("terminalTarget");
        var firstSymbol = firstTarget.GetProperty("symbolRef");
        var baselineSymbol = baselineTarget.GetProperty("symbolRef");
        var firstPath = firstTarget.GetProperty("sourceCommitment")
            .GetProperty("locator").GetProperty("repository").GetProperty("path").GetString();
        var baselinePath = baselineTarget.GetProperty("sourceCommitment")
            .GetProperty("locator").GetProperty("repository").GetProperty("path").GetString();
        var firstContent = firstTarget.GetProperty("sourceCommitment").GetProperty("contentSha256").GetString();
        var baselineContent = baselineTarget.GetProperty("sourceCommitment").GetProperty("contentSha256").GetString();

        if (string.Equals(evolution, "moved-source-authority", StringComparison.Ordinal))
        {
            Assert.Equal(baselineSymbol.GetRawText(), firstSymbol.GetRawText());
            Assert.Equal("App/Moved.cs", firstPath);
            Assert.NotEqual(baselinePath, firstPath);
            Assert.NotEqual(baselineContent, firstContent);
        }
        else if (string.Equals(evolution, "changed-compilation-context", StringComparison.Ordinal))
        {
            Assert.Equal(baselineSymbol.GetProperty("documentationCommentId").GetString(),
                firstSymbol.GetProperty("documentationCommentId").GetString());
            Assert.NotEqual(baselineSymbol.GetProperty("compilationContextRef").GetString(),
                firstSymbol.GetProperty("compilationContextRef").GetString());
            Assert.Equal(baselinePath, firstPath);
            Assert.Equal(baselineContent, firstContent);
        }
        else if (string.Equals(evolution, "changed-applicable-components", StringComparison.Ordinal))
        {
            Assert.Equal(
                new[] { "parameter:parameter/0", "return:return" },
                evidence[0].GetProperty("applicableComponents").EnumerateArray().Select(component =>
                    component.GetProperty("kind").GetString() + ":"
                    + component.GetProperty("identity").GetString()));
            Assert.NotEqual(baselineContent, firstContent);
        }
    }

    private static async Task<bool> ApplyTargetEvolutionAsync(string repositoryRoot, string evolution)
    {
        var sourcePath = Path.Join(repositoryRoot, "App", "App.cs");
        switch (evolution)
        {
            case "disappeared":
                await File.WriteAllTextAsync(sourcePath,
                    "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App { }\n");
                return true;
            case "became-compliant":
                await File.WriteAllTextAsync(sourcePath,
                    "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App\n{\n    /// <summary>Runs the operation.</summary>\n    public static void Run() { }\n}\n");
                return true;
            case "moved-source-authority":
                await File.WriteAllTextAsync(sourcePath,
                    "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static partial class App { }\n");
                await File.WriteAllTextAsync(Path.Join(repositoryRoot, "App", "Moved.cs"),
                    "namespace Fixture;\npublic static partial class App\n{\n    public static void Run() { }\n}\n");
                return false;
            case "changed-compilation-context":
                var projectPath = Path.Join(repositoryRoot, "App", "App.csproj");
                File.Move(
                    projectPath,
                    Path.Join(repositoryRoot, "App", "ChangedContext.csproj"));
                return false;
            case "changed-applicable-components":
                await File.WriteAllTextAsync(sourcePath,
                    "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App\n{\n    public static int Run(int value) => value;\n}\n");
                return false;
            case "similar-display-name":
                await File.WriteAllTextAsync(sourcePath,
                    "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App\n{\n    public static void RunAgain() { }\n}\n");
                return false;
            case "reordered-plan":
                await File.WriteAllTextAsync(sourcePath,
                    "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static partial class App { }\n");
                await File.WriteAllTextAsync(Path.Join(repositoryRoot, "App", "Alpha.cs"),
                    "namespace Fixture;\npublic static partial class App\n{\n    public static void Alpha() { }\n}\n");
                await File.WriteAllTextAsync(Path.Join(repositoryRoot, "App", "Zulu.cs"),
                    "namespace Fixture;\npublic static partial class App\n{\n    public static void Zulu() { }\n}\n");
                return false;
            default:
                throw new InvalidDataException("Unknown target-evolution fixture.");
        }
    }

    private static void CopyRevision(string revision, string repositoryRoot) => File.Copy(
        Path.Join(
            CampaignCliProcessTests.RepositoryRoot,
            "tests", "fixtures", "campaign", "changed-base", revision, "revision.json"),
        Path.Join(repositoryRoot, "changed-base-revision.json"),
        overwrite: true);

    private sealed class ProcessFixture : IAsyncDisposable
    {
        private ProcessFixture(
            LoaderFixture repository,
            CampaignCliProcessTests.ProposalLoopbackServer server,
            string outside,
            string configurationPath,
            string statePath)
        {
            Repository = repository;
            Server = server;
            Outside = outside;
            ConfigurationPath = configurationPath;
            StatePath = statePath;
        }

        internal LoaderFixture Repository { get; }
        internal CampaignCliProcessTests.ProposalLoopbackServer Server { get; }
        internal string Outside { get; }
        internal string ConfigurationPath { get; }
        internal string StatePath { get; }

        internal string Input { get; set; } = "App/App.csproj";

        internal string[] Args(string operation, string snapshot) => CampaignCliProcessTests.Args(
                operation, Repository.Root, StatePath, ConfigurationPath, snapshot)
            .Select(argument => string.Equals(argument, "App/App.csproj", StringComparison.Ordinal)
                ? Input
                : argument)
            .ToArray();

        [SupportedOSPlatform("linux")]
        internal static async Task<ProcessFixture> CreateAsync(
            bool executable,
            int? maximumCampaignElapsedMilliseconds = null,
            int? maximumCandidatesPerBlock = null)
        {
            var repository = await LoaderFixture.CreateAsync();
            if (executable)
            {
                await CampaignCliProcessTests.SetSingleWorkItemSourceAsync(repository.Root);
            }
            await File.WriteAllTextAsync(
                Path.Join(repository.Root, "policy.json"),
                executable ? CampaignCliProcessTests.RequiredPolicy : CampaignCliProcessTests.OptionalPolicy);
            var server = new CampaignCliProcessTests.ProposalLoopbackServer();
            var outside = CampaignCliProcessTests.CreatePrivateDirectory("contract-scribe-changed-base");
            var stateDirectory = Path.Join(outside, "state");
            Directory.CreateDirectory(stateDirectory);
            File.SetUnixFileMode(stateDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var configurationPath = Path.Join(outside, "campaign.json");
            await CampaignCliProcessTests.WriteConfigurationAsync(configurationPath, server.Endpoint);
            if (maximumCampaignElapsedMilliseconds is not null || maximumCandidatesPerBlock is not null)
            {
                var configuration = JsonNode.Parse(await File.ReadAllBytesAsync(configurationPath))!.AsObject();
                if (maximumCampaignElapsedMilliseconds is { } maximumElapsed)
                {
                    configuration["budgets"]!["campaign"]!["maximumElapsedMilliseconds"] = maximumElapsed;
                }
                if (maximumCandidatesPerBlock is { } maximumCandidates)
                {
                    configuration["budgets"]!["campaign"]!["maximumCandidatesPerBlock"] = maximumCandidates;
                }
                await File.WriteAllTextAsync(
                    configurationPath,
                    configuration.ToJsonString(),
                    new UTF8Encoding(false, true));
            }
            return new(
                repository,
                server,
                outside,
                configurationPath,
                Path.Join(stateDirectory, "checkpoint.json"));
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Repository.DisposeAsync();
            if (Directory.Exists(Outside)) Directory.Delete(Outside, recursive: true);
        }
    }
}
