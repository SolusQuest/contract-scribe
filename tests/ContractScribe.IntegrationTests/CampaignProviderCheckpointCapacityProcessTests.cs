using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed partial class CampaignCliProcessTests
{
    [Fact]
    public async Task Production_first_provider_checkpoint_capacity_is_admitted_before_publication_and_preserves_retained_state()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await using var server = new ProposalLoopbackServer();
        var outside = CreatePrivateDirectory("contract-scribe-provider-capacity");
        try
        {
            var configurationPath = Path.Join(outside, "consumer.json");
            await WriteConsumerLayerAsync(configurationPath, server.Endpoint);
            var layer = JsonNode.Parse(await File.ReadAllTextAsync(configurationPath))!;
            layer["budgets"] = new JsonObject { ["campaign"] = new JsonObject { ["maximumProviderRequests"] = 128 } };
            await File.WriteAllTextAsync(configurationPath, layer.ToJsonString());
            await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), RequiredPolicy);
            var stateDirectory = Path.Join(outside, "state");
            Directory.CreateDirectory(stateDirectory);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(stateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var statePath = Path.Join(stateDirectory, "checkpoint.json");
            var sourcePath = Path.Join(fixture.Root, "App", "App.cs");
            CampaignPreflightResult Preflight(CampaignOperation operation, string snapshot, string? path = null) => CampaignPreflight.Run(
                new(operation, fixture.Root, "App/App.csproj", "policy.json", snapshot, path ?? statePath,
                    configurationPath, null, "campaign.integration"), RepositoryRoot);
            async Task<(CampaignCheckpointArtifact Artifact, CampaignScribeExecutionCapability Execution,
                CampaignPlanningInput Input, CampaignWorkPlan Plan, DocumentationScribeRequest Request)> Template(
                string snapshot, CampaignCheckpointState? reference = null)
            {
                var preflight = Preflight(CampaignOperation.Start, snapshot);
                var configuration = preflight.Configuration.Document;
                var policy = configuration.CreateExecutionPolicy();
                var execution = configuration.CreateExecutionCapability(policy);
                CampaignPlanningInput? input = null;
                CampaignWorkPlan? plan = null;
                CampaignCheckpointArtifact? artifact = null;
                DocumentationScribeRequest? request = null;
                Exception? failure = null;
                var host = new ProductionRepositorySessionHost(new ContractScribe.Core.Hosting.HostBuildProvenance(CliBuildIdentity.Current.SourceRevision));
                var result = await host.RunAsync(new(fixture.Root, "App/App.csproj", preflight.PolicyBytes, PublicationTarget: null, PublishResult: false),
                    new ProductionAuditHostControls(SessionConsumer: (bundle, token) =>
                    {
                        try
                        {
                            input = CampaignCommandRunner.CreatePlanningInput(preflight, configuration, policy, bundle, token, new(1));
                            plan = CampaignPlanner.Plan(input);
                            // The public codec accepts intrinsic current-shape checkpoints. Reconstruct
                            // one from a real production plan to exercise retained-state admission too.
                            var state = reference is null
                                ? CampaignStateFactory.CreateInitial(configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                                    configuration.ScribeRequest.StyleProfileTemplate.ExactProjection, execution, bundle.Session.InputIdentity, input, plan)
                                : CampaignStateFactory.CreateValidated(reference.ProductRevision, plan.CampaignLineage,
                                    reference.Snapshot with
                                    {
                                        OpaqueSnapshotBinding = input.Snapshot.OpaqueSnapshotBinding,
                                        RepositoryCommitmentSha256 = input.Snapshot.RepositoryCommitmentSha256,
                                        InputCommitmentSha256 = input.Snapshot.InputCommitmentSha256,
                                        ExecutionCommitmentSha256 = plan.ExecutionCommitment,
                                    }, 0, reference.ConfiguredCeilings, reference.LineageCharges,
                                    plan.WorkItems.Select(work => new CampaignWorkItemState(work.WorkItemKey, 0, 0,
                                        work.Disposition.Kind == CampaignPlanningDispositionKind.Executable ? CampaignWorkStatus.Planned : CampaignWorkStatus.Closed,
                                        null, work.Disposition.Kind == CampaignPlanningDispositionKind.Terminal
                                            ? new CampaignWorkClosedOutcome(CampaignWorkOutcomeStage.Planning, CampaignWorkOutcomeCode.PlanningTerminal,
                                                null, null, null, null, null, null, work.WorkItemKey) : null)), plan.Batch);
                            artifact = CampaignStateJson.CreateArtifact(state);
                            var work = plan.WorkItems.Single(item => item.Disposition.Kind == CampaignPlanningDispositionKind.Executable);
                            request = DocumentationScribeValidation.ParseRequest(CampaignScribeRequestBuilder.Build(bundle, work, policy, configuration, token)).Request;
                        }
                        catch (Exception exception) { failure = exception; throw; }
                        return Task.CompletedTask;
                    }), CancellationToken.None);
                Assert.True(failure is null, failure?.ToString());
                Assert.Equal(ContractScribe.Core.Hosting.HostExecutionOutcome.Succeeded, result.Terminal.ExecutionOutcome);
                Assert.NotNull(artifact);
                Assert.NotNull(request);
                return (artifact, execution, input!, plan!, request);
            }

            var baselineSource = ProviderCapacitySource(900, 0);
            await File.WriteAllTextAsync(sourcePath, baselineSource, new UTF8Encoding(false, true));
            var baseline = await Template("snapshot.provider-capacity");
            const int RemainingBytes = 128;
            const int BytesPerNamespaceScalar = 164 * 4 * 6;
            var growth = CampaignStateContract.MaximumArtifactUtf8Bytes - RemainingBytes - baseline.Artifact.ExactUtf8Json.Length;
            Assert.True(growth > 0);
            var nearSource = ProviderCapacitySource(900 + growth / BytesPerNamespaceScalar, growth % BytesPerNamespaceScalar);
            await File.WriteAllTextAsync(sourcePath, nearSource, new UTF8Encoding(false, true));
            var near = await Template("snapshot.provider-capacity", baseline.Artifact.State);
            Assert.Equal(663, near.Plan.Batch.CompleteTargets.Length);
            Assert.Equal(CampaignStateContract.MaximumArtifactUtf8Bytes - RemainingBytes, near.Artifact.ExactUtf8Json.Length);
            Assert.True(CampaignStateJson.Parse(near.Artifact.ExactUtf8Json.AsMemory()).IsValid);
            var configuration = Preflight(CampaignOperation.Start, "snapshot.provider-capacity").Configuration.Document;
            CampaignTransitionResult Admit(CampaignCheckpointArtifact artifact, CampaignPlanningInput input, CampaignWorkPlan plan,
                DocumentationScribeRequest request) => CampaignStateReducer.AdmitProviderInvocation(artifact, near.Execution,
                    configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                    configuration.ScribeRequest.StyleProfileTemplate.ExactProjection, input, plan,
                    plan.WorkItems.Single(item => item.Disposition.Kind == CampaignPlanningDispositionKind.Executable).WorkItemKey,
                    request, CampaignStateFactory.CreateInvocationTargetAllowance(artifact.State, new(1)), CampaignInvocationTestPolicy.Create(request.Limits));
            var rejected = Admit(near.Artifact, near.Input, near.Plan, near.Request);
            Assert.Equal(CampaignTransitionKind.Rejected, rejected.Kind);
            Assert.Equal(CampaignTransitionFailure.CheckpointCapacity, rejected.Failure);
            Assert.Equal(near.Artifact.ExactUtf8Json, rejected.Artifact.ExactUtf8Json);
            Assert.Equal(0, rejected.Artifact.State.LineageCharges.OuterInvocations);
            var valid = Admit(baseline.Artifact, baseline.Input, baseline.Plan, baseline.Request);
            Assert.Equal(CampaignTransitionKind.Applied, valid.Kind);
            Assert.IsType<CampaignProviderReservation>(valid.Artifact.State.ActiveReservation);
            Assert.True(CampaignStateJson.Parse(valid.Artifact.ExactUtf8Json.AsMemory()).IsValid);
            var capacity = Assert.Throws<CampaignStateValidationException>(() => CampaignStateFactory.CreateInitial(
                configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                configuration.ScribeRequest.StyleProfileTemplate.ExactProjection, near.Execution,
                "App/App.csproj", near.Input, near.Plan));
            Assert.Equal(CampaignStateValidationCode.DocumentTooLarge, capacity.Code);
            var zeroInput = near.Input with { TargetLimit = new(0) };
            var zeroPlan = CampaignPlanner.Plan(zeroInput);
            var zero = CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateInitial(
                configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                configuration.ScribeRequest.StyleProfileTemplate.ExactProjection, near.Execution,
                "App/App.csproj", zeroInput, zeroPlan));
            Assert.Empty(zero.State.Batch.SelectedTargetKeys);
            Assert.Equal(663, zero.State.Batch.CompleteTargets.Length);

            var baselineRetry = ProviderCapacityRetryCheckpoint(baseline.Artifact, baseline.Request);
            var retryKey = baseline.Plan.WorkItems.Single(item => item.Disposition.Kind == CampaignPlanningDispositionKind.Executable).WorkItemKey;
            var closedRetry = CampaignStateReducer.RetryProviderInvocation(baselineRetry, null, baseline.Execution,
                configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                configuration.ScribeRequest.StyleProfileTemplate.ExactProjection, baseline.Input, baseline.Plan,
                retryKey, baseline.Request, CampaignStateFactory.CreateInvocationTargetAllowance(baselineRetry.State, new(1)), CampaignInvocationTestPolicy.Create(baseline.Request.Limits));
            Assert.Equal(CampaignTransitionKind.Applied, closedRetry.Kind);
            Assert.Equal(2, closedRetry.Artifact.State.LineageCharges.OuterInvocations);
            var reservationGrowth = valid.Artifact.ExactUtf8Json.Length - baseline.Artifact.ExactUtf8Json.Length;
            var boundedCompletionGrowth = CampaignStateReducer.ValidateProviderSettlementCapacity(valid.Artifact.State)
                - baseline.Artifact.ExactUtf8Json.Length;
            growth = CampaignStateContract.MaximumArtifactUtf8Bytes - reservationGrowth - baseline.Artifact.ExactUtf8Json.Length;
            var retrySource = ProviderCapacitySource(900 + growth / BytesPerNamespaceScalar, growth % BytesPerNamespaceScalar);
            await File.WriteAllTextAsync(sourcePath, retrySource, new UTF8Encoding(false, true));
            var retryTemplate = await Template("snapshot.provider-capacity", baseline.Artifact.State);
            var retryCapacity = Assert.Throws<CampaignStateValidationException>(() => CampaignStateFactory.CreateInitial(configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                configuration.ScribeRequest.StyleProfileTemplate.ExactProjection, retryTemplate.Execution,
                "App/App.csproj", retryTemplate.Input, retryTemplate.Plan));
            Assert.Equal(CampaignStateValidationCode.DocumentTooLarge, retryCapacity.Code);
            Assert.Equal(CampaignTransitionFailure.CheckpointCapacity,
                Admit(retryTemplate.Artifact, retryTemplate.Input, retryTemplate.Plan, retryTemplate.Request).Failure);
            // Intrinsically valid current-shape checkpoints may lack the producer's
            // completion headroom. Recovery must retire their outstanding host claim
            // before a fresh invocation attempts the same semantic work.
            var retryArtifact = ProviderCapacityLoadedReservation(retryTemplate.Artifact, retryTemplate.Request);
            Assert.Equal(CampaignStateContract.MaximumArtifactUtf8Bytes, retryArtifact.ExactUtf8Json.Length);
            Assert.True(CampaignStateJson.Parse(retryArtifact.ExactUtf8Json.AsMemory()).IsValid);
            var retryWorkKey = ((CampaignProviderReservation)retryArtifact.State.ActiveReservation!).WorkItemKey;
            var acceptedRetry = CampaignCheckpointAcceptance.AcceptCurrent(CampaignCheckpointReadResult.Found(
                retryArtifact.ExactUtf8Json.AsSpan(), retryArtifact.CheckpointRevision, retryArtifact.Sha256)).AcceptedCheckpoint!;
            var retired = CampaignStateReducer.RetireInterruptedProviderAttempt(acceptedRetry);
            Assert.Equal(CampaignTransitionKind.Applied, retired.Kind);
            Assert.Null(retired.Artifact.State.ActiveReservation);
            Assert.NotNull(retired.Artifact.State.WorkItems.Single(item => item.WorkItemKey == retryWorkKey).PausedProviderAttempt);
            Assert.Equal(1, retired.Artifact.State.LineageCharges.OuterInvocations);
            Assert.Equal(0, retired.Artifact.State.LineageCharges.ProviderRequests.TotalCharged);
            Assert.False(acceptedRetry.TryRetireReservation());
            var acceptedRetired = CampaignCheckpointAcceptance.AcceptCurrent(CampaignCheckpointReadResult.Found(
                retired.Artifact.ExactUtf8Json.AsSpan(), retired.Artifact.CheckpointRevision, retired.Artifact.Sha256)).AcceptedCheckpoint!;
            var retry = CampaignStateReducer.RetryProviderInvocation(retired.Artifact, acceptedRetired, retryTemplate.Execution,
                configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                configuration.ScribeRequest.StyleProfileTemplate.ExactProjection, retryTemplate.Input, retryTemplate.Plan,
                retryTemplate.Plan.WorkItems.Single(item => item.Disposition.Kind == CampaignPlanningDispositionKind.Executable).WorkItemKey,
                retryTemplate.Request, CampaignStateFactory.CreateInvocationTargetAllowance(retired.Artifact.State, new(1)), CampaignInvocationTestPolicy.Create(retryTemplate.Request.Limits));
            Assert.Equal(CampaignTransitionFailure.CheckpointCapacity, retry.Failure);
            Assert.Equal(retired.Artifact.ExactUtf8Json, retry.Artifact.ExactUtf8Json);
            Assert.Equal(1, retry.Artifact.State.LineageCharges.OuterInvocations);
            Assert.NotNull(retry.Artifact.State.WorkItems.Single(item => item.WorkItemKey == retryWorkKey).PausedProviderAttempt);

            var fittingInput = baseline.Input with
            {
                Snapshot = baseline.Input.Snapshot with { OpaqueSnapshotBinding = "snapshot.provider-successor" },
            };
            var fittingPlan = CampaignPlanner.Plan(fittingInput);
            var fittingTemplate = CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateSupersessionTemplate(
                configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                configuration.ScribeRequest.StyleProfileTemplate.ExactProjection, baseline.Execution,
                "App/App.csproj", fittingInput, fittingPlan));
            var fittingSupersession = CampaignStateReducer.Supersede(baseline.Artifact, null,
                CampaignCheckpointAcceptance.CreateInitialAuthority(fittingTemplate), baseline.Execution,
                configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                configuration.ScribeRequest.StyleProfileTemplate.ExactProjection, "App/App.csproj", fittingInput, fittingPlan);
            Assert.Equal(CampaignTransitionKind.Applied, fittingSupersession.Kind);
            var summaryGrowth = fittingSupersession.Artifact.ExactUtf8Json.Length - fittingTemplate.ExactUtf8Json.Length;
            growth = checked((int)(CampaignStateContract.MaximumArtifactUtf8Bytes - Math.Max(boundedCompletionGrowth, summaryGrowth)
                - RemainingBytes - baseline.Artifact.ExactUtf8Json.Length));
            var successorSource = ProviderCapacitySource(900 + growth / BytesPerNamespaceScalar, growth % BytesPerNamespaceScalar);
            await File.WriteAllTextAsync(sourcePath, successorSource, new UTF8Encoding(false, true));
            var successor = await Template("snapshot.provider-successor", baseline.Artifact.State);
            // The template admits both reservation and bounded completion. The
            // final carried predecessor summary exhausts that future capacity.
            _ = CampaignStateFactory.CreateInitial(configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                configuration.ScribeRequest.StyleProfileTemplate.ExactProjection, successor.Execution,
                "App/App.csproj", successor.Input, successor.Plan);
            CampaignTransitionResult Supersede(CampaignCheckpointArtifact predecessor) => CampaignStateReducer.Supersede(
                predecessor, null, CampaignCheckpointAcceptance.CreateInitialAuthority(successor.Artifact), successor.Execution,
                configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                configuration.ScribeRequest.StyleProfileTemplate.ExactProjection, "App/App.csproj", successor.Input, successor.Plan);
            var finalRejected = Supersede(baseline.Artifact);
            Assert.Equal(CampaignTransitionFailure.CheckpointCapacity, finalRejected.Failure);
            Assert.Equal(baseline.Artifact.ExactUtf8Json, finalRejected.Artifact.ExactUtf8Json);
            var exhaustedCharges = baseline.Artifact.State.LineageCharges with
            {
                ProviderRequests = new(0, baseline.Artifact.State.ConfiguredCeilings.CampaignBudget.MaximumProviderRequests!.Value,
                    baseline.Artifact.State.ConfiguredCeilings.CampaignBudget.MaximumProviderRequests!.Value),
            };
            var exhaustedPredecessor = CampaignStateJson.CreateArtifact(ProviderCapacityCopy(
                baseline.Artifact.State, baseline.Artifact.State.WorkItems, exhaustedCharges));
            var exhaustedSuccessor = Supersede(exhaustedPredecessor);
            Assert.True(exhaustedSuccessor.Kind == CampaignTransitionKind.Applied, exhaustedSuccessor.Failure.ToString());
            Assert.Equal(exhaustedCharges, exhaustedSuccessor.Artifact.State.LineageCharges);
            Assert.Null(exhaustedSuccessor.Artifact.State.ActiveReservation);
            Assert.True(exhaustedSuccessor.Artifact.ExactUtf8Json.Length < CampaignStateContract.MaximumArtifactUtf8Bytes);

            if (!OperatingSystem.IsLinux()) return;
            await File.WriteAllTextAsync(sourcePath, nearSource, new UTF8Encoding(false, true));
            var credentialReads = 0;
            string? Credential(string _) { credentialReads++; throw new InvalidOperationException("Capacity admission cannot read credentials."); }
            async Task<CliExecutionResult> Run(CampaignOperation operation, string snapshot, int quota, string? path = null) =>
                await CampaignCommandRunner.RunAsync(CliBuildIdentity.Current, Preflight(operation, snapshot, path),
                    CancellationToken.None, Credential, targetLimit: new(quota));
            var started = await Run(CampaignOperation.Start, "snapshot.provider-capacity", 1);
            AssertCapacity(started, revision: null);
            Assert.False(File.Exists(statePath));
            await File.WriteAllBytesAsync(statePath, near.Artifact.ExactUtf8Json.ToArray());
            File.SetUnixFileMode(statePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var before = await File.ReadAllBytesAsync(statePath);
            foreach (var snapshot in new[] { "snapshot.provider-capacity", "snapshot.provider-successor" })
            {
                var resumed = await Run(CampaignOperation.Resume, snapshot, 1);
                AssertCapacity(resumed, near.Artifact.CheckpointRevision);
                Assert.Equal(before, await File.ReadAllBytesAsync(statePath));
                Assert.Single(Directory.GetFiles(stateDirectory));
            }
            var zeroPath = Path.Join(stateDirectory, "zero.json");
            var noDispatch = await Run(CampaignOperation.Start, "snapshot.provider-capacity", 0, zeroPath);
            Assert.Equal(5, noDispatch.ExitCode);
            Assert.Equal(663, CampaignStateJson.Parse(await File.ReadAllBytesAsync(zeroPath)).Artifact!.State.Batch.CompleteTargets.Length);

            await File.WriteAllTextAsync(sourcePath, retrySource, new UTF8Encoding(false, true));
            await File.WriteAllBytesAsync(statePath, retryArtifact.ExactUtf8Json.ToArray());
            AssertCapacity(await Run(CampaignOperation.Resume, "snapshot.provider-capacity", 1), retired.Artifact.CheckpointRevision);
            Assert.Equal(retired.Artifact.ExactUtf8Json.ToArray(), await File.ReadAllBytesAsync(statePath));
            await File.WriteAllTextAsync(sourcePath, successorSource, new UTF8Encoding(false, true));
            await File.WriteAllBytesAsync(statePath, baseline.Artifact.ExactUtf8Json.ToArray());
            AssertCapacity(await Run(CampaignOperation.Resume, "snapshot.provider-successor", 1), baseline.Artifact.CheckpointRevision);
            Assert.Equal(baseline.Artifact.ExactUtf8Json.ToArray(), await File.ReadAllBytesAsync(statePath));
            Assert.Equal(0, credentialReads);
            Assert.Equal(0, server.RequestCount);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    private static void AssertCapacity(CliExecutionResult result, long? revision)
    {
        Assert.Equal(4, result.ExitCode);
        using var envelope = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("state", envelope.RootElement.GetProperty("terminalLayer").GetString());
        Assert.Equal("campaign.checkpoint-too-large", envelope.RootElement.GetProperty("outcome").GetString());
        var actualRevision = envelope.RootElement.GetProperty("checkpointRevision");
        if (revision is null) Assert.Equal(JsonValueKind.Null, actualRevision.ValueKind);
        else Assert.Equal(revision.Value, actualRevision.GetInt64());
    }

    private static string ProviderCapacitySource(int namespaceScalars, int paddingScalars) =>
        SupersessionCapacitySource(namespaceScalars, paddingScalars).Replace("; } }\n", "; public void Run() { } } }\n", StringComparison.Ordinal);

    private static CampaignCheckpointArtifact ProviderCapacityRetryCheckpoint(CampaignCheckpointArtifact initial, DocumentationScribeRequest request)
    {
        var key = initial.State.WorkItems.Single(item => item.Status == CampaignWorkStatus.Planned).WorkItemKey;
        var attempt = CampaignStateFactory.CreateScribeAttemptId(initial.State.Snapshot.ExecutionCommitmentSha256,
            initial.State.ConfiguredCeilings.ScribeExecutionAuthority, key, 1);
        var items = initial.State.WorkItems.Select(item => item.WorkItemKey != key ? item : item with
        {
            OuterAttemptCount = 1,
            Status = CampaignWorkStatus.Closed,
            ClosedOutcome = new(CampaignWorkOutcomeStage.Scribe, CampaignWorkOutcomeCode.ProviderFailure,
                CampaignProviderFinalDisposition.Retryable, request.ArtifactSha256, attempt, null, null, null, key),
        }).ToImmutableArray();
        return CampaignStateJson.CreateArtifact(ProviderCapacityCopy(initial.State, items,
            initial.State.LineageCharges with { OuterInvocations = 1 }));
    }

    private static CampaignCheckpointState ProviderCapacityCopy(CampaignCheckpointState state,
        ImmutableArray<CampaignWorkItemState> workItems, CampaignLineageCharges charges) =>
        CampaignStateFactory.CreateValidated(state.ProductRevision, state.CampaignLineage, state.Snapshot,
            state.CheckpointRevision, state.ConfiguredCeilings, charges, workItems, state.Batch,
            state.ActiveReservation, state.CandidateObservation, state.CumulativeOutcome, state.KnownCompletedOperations,
            state.TerminalOutcome, state.Predecessor);

    private static CampaignCheckpointArtifact ProviderCapacityLoadedReservation(CampaignCheckpointArtifact initial,
        DocumentationScribeRequest request)
    {
        var state = initial.State;
        var work = state.WorkItems.Single(item => item.Status == CampaignWorkStatus.Planned);
        var budget = CampaignBudgetAccounting.ReserveProviderInvocation(state, CampaignInvocationTestPolicy.Create(state.ConfiguredCeilings.ScribeRunLimits));
        Assert.Equal(CampaignBudgetDecisionKind.Admitted, budget.Kind);
        var attempt = CampaignStateFactory.CreateScribeAttemptId(state.Snapshot.ExecutionCommitmentSha256,
            state.ConfiguredCeilings.ScribeExecutionAuthority, work.WorkItemKey, 1);
        return CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateValidated(state.ProductRevision,
            state.CampaignLineage, state.Snapshot, state.CheckpointRevision + 1, state.ConfiguredCeilings,
            budget.Charges!, state.WorkItems.Select(item => item.WorkItemKey == work.WorkItemKey
                ? item with { OuterAttemptCount = 1 } : item), state.Batch,
            new CampaignProviderReservation(work.WorkItemKey, request.ArtifactSha256, attempt, budget.Exposure!)
            { ExecutionStartRevision = state.CheckpointRevision + 1, CurrentOperationOrdinal = 1 },
            state.CandidateObservation, state.CumulativeOutcome, state.KnownCompletedOperations,
            state.TerminalOutcome, state.Predecessor));
    }
}
