using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using Json.Schema;

namespace ContractScribe.Tests;

public sealed class CampaignStateContractTests
{
    private static readonly Lazy<JsonSchema> CampaignSchema = new(LoadCampaignSchema);

    [Fact]
    public void Canonical_artifact_has_fixed_bytes_lf_and_digest()
    {
        var artifact = CampaignStateJson.CreateArtifact(CreateState());
        var expected = File.ReadAllBytes(FixturePath("empty-terminal.json"));

        Assert.Equal(expected, artifact.ExactUtf8Json.ToArray());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(),
            artifact.Sha256);
        Assert.Equal("775e06bd7812aa5713eeefcd837da3ea6324a82f85018556832448c3962ec990", artifact.Sha256);
        Assert.Equal((byte)'\n', expected[^1]);
        Assert.NotEqual((byte)'\n', expected[^2]);
    }

    [Fact]
    public void Known_answer_conforms_to_the_published_registry()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(FixturePath("empty-terminal.json")));

        var evaluation = EvaluateCampaignSchema(document.RootElement);
        Assert.True(evaluation.IsValid, DescribeSchemaFailures(evaluation));
    }

    [Fact]
    public void Trusted_proposal_canonical_bytes_conform_to_the_published_registry()
    {
        var state = CreateProposalCompleteState();
        var artifact = CampaignStateJson.CreateArtifact(state);
        using var document = JsonDocument.Parse(artifact.ExactUtf8Json.AsMemory());
        var proposal = Assert.Single(state.WorkItems, work => work.Status == CampaignWorkStatus.ProposalComplete)
            .TrustedProposal;

        Assert.NotNull(proposal);
        Assert.Contains(proposal.Evidence, evidence =>
            evidence.Subject is ComponentEvidenceSubject
            {
                ComponentKind: ComponentKind.Parameter,
                Identity: "parameter/0",
            });
        Assert.Contains(proposal.Evidence, evidence =>
            evidence.Subject is ComponentEvidenceSubject
            {
                ComponentKind: ComponentKind.Return,
                Identity: "return",
            });
        var evaluation = EvaluateCampaignSchema(document.RootElement);
        Assert.True(evaluation.IsValid, DescribeSchemaFailures(evaluation));
        Assert.True(CampaignStateJson.Parse(artifact.ExactUtf8Json.AsMemory()).IsValid);
    }

    [Fact]
    public void Published_registry_closes_the_subject_kind_and_identity_matrix()
    {
        var cases = new (string Kind, string? ComponentKind, string? Identity, bool Expected)[]
        {
            ("target", null, null, true),
            ("component", "component.type-parameter", "type-parameter/0", true),
            ("component", "component.parameter", "parameter/0", true),
            ("component", "component.return", "return", true),
            ("component", "component.value", "value", true),
            ("target", "component.parameter", "parameter/0", false),
            ("component", null, null, false),
            ("component", "component.unknown", "unknown/0", false),
            ("component", "component.parameter", "return", false),
            ("component", "component.return", "parameter/0", false),
            ("component", "component.type-parameter", "type-parameter/00", false),
            ("component", "component.parameter", "parameter/00", false),
            ("unexpected", null, null, false),
            ("component", "component.parameter", "parameter/" + new string('1', 128), false),
        };

        foreach (var testCase in cases)
        {
            var root = CreateProposalCanonicalNode();
            var subject = root["workItems"]![0]!["trustedProposal"]!["evidence"]!
                .AsArray()
                .Select(evidence => evidence!["subject"]!)
                .Single(candidate => candidate["componentKind"]?.GetValue<string>() == "component.parameter");
            subject["kind"] = testCase.Kind;
            subject["componentKind"] = testCase.ComponentKind;
            subject["identity"] = testCase.Identity;
            using var document = JsonDocument.Parse(root.ToJsonString());
            var evaluation = EvaluateCampaignSchema(document.RootElement);
            var actual = evaluation.IsValid;

            Assert.True(
                actual == testCase.Expected,
                $"subject matrix mismatch: {testCase.Kind}|{testCase.ComponentKind}|{testCase.Identity}: "
                + DescribeSchemaFailures(evaluation));
        }
    }

    [Theory]
    [InlineData("duplicate-root.json", CampaignStateValidationCode.DuplicateProperty)]
    [InlineData("unknown-extension.json", CampaignStateValidationCode.UnknownProperty)]
    public void Raw_invalid_vectors_fail_with_stable_codes(
        string name,
        CampaignStateValidationCode expected)
    {
        var result = CampaignStateJson.Parse(File.ReadAllBytes(FixturePath("invalid", name)));

        Assert.False(result.IsValid);
        Assert.Equal(expected, result.FailureCode);
    }

    [Fact]
    public void Canonical_round_trip_is_exact_and_culture_independent()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var bytes = new[] { "en-US", "tr-TR", "zh-CN" }
                .Select(WriteUnderCulture)
                .ToArray();

            Assert.All(bytes.Skip(1), value => Assert.Equal(bytes[0], value));
            var parsed = CampaignStateJson.Parse(bytes[0]);
            Assert.True(parsed.IsValid);
            Assert.Equal(bytes[0], parsed.Artifact!.ExactUtf8Json.ToArray());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Canonical_artifact_is_identical_across_fresh_processes_and_cultures()
    {
        var temporaryRoot = Path.Join(Path.GetTempPath(), "contract-scribe-campaign-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var outputs = new[] { "en-US", "tr-TR", "zh-CN" }
                .Select(culture => RunFreshProcessProbe(temporaryRoot, culture))
                .ToArray();
            Assert.All(outputs.Skip(1), output => Assert.Equal(outputs[0], output));
            Assert.Equal(File.ReadAllBytes(FixturePath("empty-terminal.json")), outputs[0]);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void Fresh_process_probe_emits_exact_artifact_when_explicitly_invoked()
    {
        var outputPath = Environment.GetEnvironmentVariable("CONTRACT_SCRIBE_CAMPAIGN_STATE_PROBE_OUTPUT");
        if (string.IsNullOrEmpty(outputPath))
        {
            return;
        }

        var cultureName = Environment.GetEnvironmentVariable("CONTRACT_SCRIBE_CAMPAIGN_STATE_PROBE_CULTURE")
            ?? throw new InvalidOperationException("The child-process culture was not supplied.");
        var culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        File.WriteAllBytes(outputPath, CampaignStateJson.CreateArtifact(CreateState()).ExactUtf8Json.ToArray());
    }

    [Fact]
    public void Trusted_proposal_requires_the_exact_durable_provider_reservation()
    {
        var scenario = CreateProposalScenario();
        var first = scenario.Plan.WorkItems[0];
        var second = scenario.Plan.WorkItems[1];
        var exchange = CreateScribeExchange(first);
        var secondExchange = CreateScribeExchange(second);

        AssertInvalidCorrelation(() => AdmitProposal(scenario, scenario.InitialState, first, exchange));

        var wrongWorkReservation = WithState(
            scenario.InitialState,
            scenario.InitialState.WorkItems,
            ProviderReservation(second.WorkItemKey, exchange));
        AssertInvalidCorrelation(() => AdmitProposal(scenario, wrongWorkReservation, first, exchange));

        var alternateRequest = CreateScribeExchange(first, inputIdentity: "samples/Alternate.csproj");
        var wrongRequestReservation = WithState(
            scenario.InitialState,
            scenario.InitialState.WorkItems,
            ProviderReservation(first.WorkItemKey, alternateRequest));
        AssertInvalidCorrelation(() => AdmitProposal(scenario, wrongRequestReservation, first, exchange));
        AssertInvalidCorrelation(() => AdmitProposal(
            scenario,
            wrongRequestReservation,
            first,
            alternateRequest));

        var alternateAttempt = CreateScribeExchange(
            first,
            attemptId: "scribe-attempt.ffffffffffffffffffffffffffffffff");
        var wrongAttemptReservation = WithState(
            scenario.InitialState,
            scenario.InitialState.WorkItems,
            ProviderReservation(first.WorkItemKey, alternateAttempt));
        AssertInvalidCorrelation(() => AdmitProposal(scenario, wrongAttemptReservation, first, exchange));

        AssertInvalidCorrelation(() => AdmitProposal(
            scenario,
            WithState(
                scenario.InitialState,
                scenario.InitialState.WorkItems,
                ProviderReservation(first.WorkItemKey, exchange)),
            second,
            secondExchange));

        var exactState = WithState(
            scenario.InitialState,
            scenario.InitialState.WorkItems,
            ProviderReservation(first.WorkItemKey, exchange));
        var exactProposal = AdmitProposal(scenario, exactState, first, exchange);

        var proposalComplete = WithState(
            scenario.InitialState,
            ReplaceWork(scenario.InitialState, first.WorkItemKey, CampaignWorkStatus.ProposalComplete, exactProposal),
            activeReservation: null);
        var activeRequest = CampaignStateFactory.ReconstructPatchRequest(
            proposalComplete,
            PatchContext(exchange.Request),
            CurrentEvidence(exchange));
        var patchReserved = WithState(
            proposalComplete,
            proposalComplete.WorkItems,
            CampaignStateFactory.CreatePatchReservation(
                proposalComplete,
                activeRequest,
                patchAttemptCount: 1,
                elapsedMilliseconds: 0));
        AssertInvalidCorrelation(() => AdmitProposal(scenario, patchReserved, second, secondExchange));

        Assert.Equal(exchange.Request.ArtifactSha256, exactProposal.HistoricalScribeRequestSha256);
        Assert.Equal(exchange.Result.AttemptId, exactProposal.HistoricalAttemptId);
    }

    [Fact]
    public void Trusted_proposal_rejects_every_authority_and_projection_substitution_class()
    {
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var original = CreateScribeExchange(work);
        var reserved = WithState(
            scenario.InitialState,
            scenario.InitialState.WorkItems,
            ProviderReservation(work.WorkItemKey, original));
        var mutations = new (Action<JsonObject> Request, Action<JsonObject>? Result)[]
        {
            (request =>
            {
                const string alternate = "M:Synthetic.Widget.Alternate(System.String)";
                SetSymbol(request["target"]!["symbolRef"]!, "synthetic.v1", alternate);
                foreach (var subject in request["evidenceReferences"]!
                    .AsArray()
                    .Select(evidence => evidence!["subject"]!))
                {
                    SetSymbol(subject["parentSymbolRef"] ?? subject["symbolRef"]!, "synthetic.v1", alternate);
                }
            }, result => SetSymbol(
                result["terminal"]!["target"]!["symbolRef"]!,
                "synthetic.v1",
                "M:Synthetic.Widget.Alternate(System.String)")),
            (request => request["target"]!["sourceCommitment"]!["contentSha256"] = Hash('a'),
                result => result["terminal"]!["target"]!["sourceCommitment"]!["contentSha256"] = Hash('a')),
            (request =>
            {
                request["target"]!["applicableComponents"]!.AsArray().RemoveAt(1);
                request["styleProfile"]!["componentPolicies"]!.AsArray().RemoveAt(1);
                request["evidenceReferences"]!.AsArray().RemoveAt(1);
            }, result => result["terminal"]!["contentUnits"]!.AsArray().RemoveAt(2)),
            (request => request["evidenceReferences"]![0]!["contentSha256"] = Hash('a'), null),
            (request => request["styleProfile"]!["allowedLiterals"]!.AsArray().Add("Zeta"), null),
            (request => request["toolPolicyId"] = "tool-policy.alternate.v1",
                result => result["runEnvelope"]!["toolPolicyId"] = "tool-policy.alternate.v1"),
        };
        foreach (var changed in mutations.Select(mutation =>
            CreateScribeExchange(work, requestMutation: mutation.Request, resultMutation: mutation.Result)))
        {
            AssertInvalidCorrelation(() => AdmitProposal(scenario, reserved, work, changed));
        }

        var productChanged = CampaignStateFactory.CreateValidated(
            scenario.InitialState.ProductRevision with { ContentSha256 = Hash('f') },
            scenario.InitialState.CampaignLineage,
            scenario.InitialState.Snapshot,
            scenario.InitialState.CheckpointRevision,
            scenario.InitialState.ConfiguredCeilings,
            scenario.InitialState.LineageCharges,
            scenario.InitialState.WorkItems,
            ProviderReservation(work.WorkItemKey, original));
        AssertInvalidCorrelation(() => AdmitProposal(scenario, productChanged, work, original));
        var snapshotChanged = CampaignStateFactory.CreateValidated(
            scenario.InitialState.ProductRevision,
            scenario.InitialState.CampaignLineage,
            scenario.InitialState.Snapshot with { OpaqueSnapshotBinding = "snapshot.alternate" },
            scenario.InitialState.CheckpointRevision,
            scenario.InitialState.ConfiguredCeilings,
            scenario.InitialState.LineageCharges,
            scenario.InitialState.WorkItems,
            ProviderReservation(work.WorkItemKey, original));
        AssertInvalidCorrelation(() => AdmitProposal(scenario, snapshotChanged, work, original));

        var nonProposal = CreateNonProposalExchange(work);
        AssertInvalidCorrelation(() => AdmitProposal(scenario, reserved, work, nonProposal));

        var proposal = AdmitProposal(scenario, reserved, work, original);
        AssertInvalidCorrelation(() => WithState(
            scenario.InitialState,
            ReplaceWork(
                scenario.InitialState,
                work.WorkItemKey,
                CampaignWorkStatus.ProposalComplete,
                proposal with { ToolPolicyId = "tool-policy.alternate.v1" }),
            activeReservation: null));
    }

    [Fact]
    public void Provider_admission_and_invocation_reject_every_request_authority_mutation()
    {
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var original = CreateScribeExchange(work);
        var authority = ExecutionAuthority(original);
        var mutations = new (
            string Name,
            Action<JsonObject> Request,
            Action<JsonObject>? Result)[]
        {
            ("input-identity", root => root["context"]!["inputIdentity"] = "samples/Alternate.csproj", null),
            ("target-profile", root => root["context"]!["targetProfile"] = "profile.assembly-visible", null),
            ("audit-outcome", root => root["context"]!["auditOutcome"] = "audit.outcome.compliant", null),
            ("symbol", root =>
            {
                const string alternate = "M:Synthetic.Widget.Alternate(System.String)";
                SetSymbol(root["target"]!["symbolRef"]!, "synthetic.v1", alternate);
                foreach (var subject in root["evidenceReferences"]!
                    .AsArray()
                    .Select(evidence => evidence!["subject"]!))
                {
                    SetSymbol(
                        subject["parentSymbolRef"] ?? subject["symbolRef"]!,
                        "synthetic.v1",
                        alternate);
                }
            }, root => SetSymbol(
                root["terminal"]!["target"]!["symbolRef"]!,
                "synthetic.v1",
                "M:Synthetic.Widget.Alternate(System.String)")),
            ("source-locator", root =>
                root["target"]!["sourceCommitment"]!["locator"]!["repository"]!["path"] =
                    "src/Synthetic/Alternate.cs",
                root => root["terminal"]!["target"]!["sourceCommitment"]!["locator"]!["repository"]!["path"] =
                    "src/Synthetic/Alternate.cs"),
            ("source-sha", root => root["target"]!["sourceCommitment"]!["contentSha256"] = Hash('f'),
                root => root["terminal"]!["target"]!["sourceCommitment"]!["contentSha256"] = Hash('f')),
            ("component-name", root =>
                root["target"]!["applicableComponents"]![0]!["name"] = "alternate",
                root => root["terminal"]!["contentUnits"]![1]!["name"] = "alternate"),
            ("style-profile", root => root["styleProfile"]!["allowedLiterals"]!.AsArray().Add("Zeta"), null),
            ("limits", root => root["limits"]!["maximumOutputTokens"] = 4_096, null),
            ("tool-policy", root => root["toolPolicyId"] = "tool-policy.alternate.v1",
                root => root["runEnvelope"]!["toolPolicyId"] = "tool-policy.alternate.v1"),
        };

        foreach (var (name, requestMutation, resultMutation) in mutations)
        {
            var changed = CreateScribeExchange(
                work,
                requestMutation: requestMutation,
                resultMutation: resultMutation);
            var rejected = CampaignStateReducer.AdmitProviderInvocation(
                CampaignStateJson.CreateArtifact(scenario.InitialState),
                authority,
                "style.synthetic",
                scenario.StyleProjection,
                scenario.Input,
                scenario.Plan,
                work.WorkItemKey,
                changed.Request);
            Assert.True(
                rejected.Kind == CampaignTransitionKind.Rejected
                    && rejected.Failure == CampaignTransitionFailure.InvalidAuthority,
                name);

            var fabricated = CampaignStateJson.CreateArtifact(WithState(
                scenario.InitialState,
                scenario.InitialState.WorkItems.Select(item =>
                    string.Equals(item.WorkItemKey, work.WorkItemKey, StringComparison.Ordinal)
                        ? item with { OuterAttemptCount = 1 }
                        : item).ToImmutableArray(),
                ProviderReservation(work.WorkItemKey, changed)));
            Assert.Throws<CampaignStateValidationException>(() =>
                CampaignStateReducer.CreateProviderInvocationAuthority(
                    AcceptCurrentForTest(fabricated),
                    authority,
                    "style.synthetic",
                    scenario.StyleProjection,
                    scenario.Input,
                    scenario.Plan,
                    changed.Request));
        }
    }

    [Fact]
    public void Trusted_proposal_binds_exact_execution_authority_and_request_limits()
    {
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var exchange = CreateScribeExchange(work);
        var reserved = WithState(
            scenario.InitialState,
            scenario.InitialState.WorkItems,
            ProviderReservation(work.WorkItemKey, exchange));
        var exact = new CampaignScribeExecutionAuthority(
            exchange.Result.RunEnvelope.ProviderConfigurationId,
            exchange.Result.RunEnvelope.ModelConfigurationId,
            exchange.Result.RunEnvelope.ScribeProtocolId,
            exchange.Request.ToolPolicyId);

        foreach (var changed in new[]
        {
            exact with { ProviderConfigurationId = "provider.alternate.v1" },
            exact with { ModelConfigurationId = "model.alternate.v1" },
            exact with { ScribeProtocolId = "scribe-protocol.alternate.v1" },
            exact with { ToolPolicyId = "tool-policy.alternate.v1" },
        })
        {
            AssertInvalidCorrelation(() => CampaignStateFactory.CreateTrustedProposal(
                reserved,
                changed,
                "style.synthetic",
                scenario.StyleProjection,
                scenario.Input,
                scenario.Plan,
                work.WorkItemKey,
                exchange.Request,
                exchange.Result));
        }

        var changedLimits = CreateScribeExchange(
            work,
            requestMutation: root => root["limits"]!["maximumOutputTokens"] = 4_096);
        var changedLimitState = WithState(
            scenario.InitialState,
            scenario.InitialState.WorkItems,
            ProviderReservation(work.WorkItemKey, changedLimits));
        AssertInvalidCorrelation(() => AdmitProposal(scenario, changedLimitState, work, changedLimits));

        var proposal = AdmitProposal(scenario, reserved, work, exchange);
        var complete = WithState(
            scenario.InitialState,
            ReplaceWork(scenario.InitialState, work.WorkItemKey, CampaignWorkStatus.ProposalComplete, proposal),
            activeReservation: null);
        AssertMutationFailure(
            complete,
            root => root["workItems"]![0]!["trustedProposal"]!["providerConfigurationId"] =
                "provider.alternate.v1",
            CampaignStateValidationCode.InvalidCorrelation);
    }

    [Fact]
    public void Fresh_session_reconstruction_rebinds_every_persisted_evidence_fact()
    {
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var historical = CreateScribeExchange(work);
        var proposal = AdmitProposal(
            scenario,
            WithState(
                scenario.InitialState,
                scenario.InitialState.WorkItems,
                ProviderReservation(work.WorkItemKey, historical)),
            work,
            historical);
        var complete = WithState(
            scenario.InitialState,
            ReplaceWork(scenario.InitialState, work.WorkItemKey, CampaignWorkStatus.ProposalComplete, proposal),
            activeReservation: null);
        var fresh = CreateFreshContextExchange(work);

        var reconstructed = CampaignStateFactory.ReconstructPatchRequest(
            complete,
            PatchContext(fresh.Request),
            CurrentEvidence(fresh));
        Assert.Equal(fresh.Request.Context.RepositoryContextRef, reconstructed.Context.RepositoryContextRef);

        var foreignInput = CreateFreshContextExchange(
            work,
            inputIdentity: "samples/Alternate.csproj");
        AssertInvalidCorrelation(() => CampaignStateFactory.ReconstructPatchRequest(
            complete,
            PatchContext(foreignInput.Request),
            CurrentEvidence(foreignInput)));

        AssertInvalidCorrelation(() => CampaignStateFactory.ReconstructPatchRequest(
            complete,
            PatchContext(fresh.Request),
            CurrentEvidence(fresh)[1..]));
        AssertInvalidCorrelation(() => CampaignStateFactory.ReconstructPatchRequest(
            complete,
            PatchContext(fresh.Request),
            CurrentEvidence(fresh).Add(CurrentEvidence(fresh)[0])));

        var changedEvidence = CreateScribeExchange(work, requestMutation: root =>
            root["evidenceReferences"]![0]!["contentSha256"] = Hash('f'));
        AssertInvalidCorrelation(() => CampaignStateFactory.ReconstructPatchRequest(
            complete,
            PatchContext(changedEvidence.Request),
            CurrentEvidence(changedEvidence)));
    }

    [Fact]
    public void Accepted_reconstruction_uses_a_fresh_request_identity_and_new_reservation_after_restart()
    {
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var historical = CreateScribeExchange(work);
        var proposal = AdmitProposal(
            scenario,
            WithState(
                scenario.InitialState,
                scenario.InitialState.WorkItems,
                ProviderReservation(work.WorkItemKey, historical)),
            work,
            historical);
        var proposalComplete = WithState(
            scenario.InitialState,
            ReplaceWork(scenario.InitialState, work.WorkItemKey, CampaignWorkStatus.ProposalComplete, proposal),
            activeReservation: null);
        proposalComplete = MutateValidState(proposalComplete, root =>
        {
            var second = root["workItems"]![1]!;
            second["status"] = "closed";
            second["closedOutcome"] = new JsonObject
            {
                ["stage"] = "scribe",
                ["code"] = "insufficient-evidence",
                ["providerDisposition"] = null,
                ["scribeRequestSha256"] = Hash('c'),
                ["attemptId"] = "scribe-attempt.22222222222222222222222222222222",
                ["patchRequestSha256"] = null,
                ["patchResultCommitmentSha256"] = null,
            };
        });
        var historicalRequest = CampaignStateFactory.ReconstructPatchRequest(
            proposalComplete,
            PatchContext(historical.Request),
            CurrentEvidence(historical));
        var proposalArtifact = CampaignStateJson.CreateArtifact(proposalComplete);
        var historicalReservation = CampaignStateReducer.ReservePatchInvocation(
            proposalArtifact,
            historicalRequest,
            elapsedMilliseconds: 1_000);
        var historicalInvocation = CampaignStateReducer.CreatePatchInvocationAuthority(
            AcceptForTest(proposalArtifact, historicalReservation),
            historicalRequest);
        Assert.True(historicalInvocation.TryBeginDispatch());
        var completed = CampaignStateReducer.CompletePatchInvocation(
            historicalReservation.Artifact,
            historicalInvocation,
            historicalRequest,
            CreateAcceptedPatchResult(historicalRequest, proposal),
            activeElapsedMilliseconds: 500);
        Assert.True(completed.Kind == CampaignTransitionKind.Applied, completed.Failure.ToString());
        Assert.Equal(CampaignTerminalKind.Complete, completed.Artifact.State.TerminalOutcome!.Kind);
        Assert.Equal(CampaignWorkStatus.Accepted, completed.Artifact.State.WorkItems[0].Status);
        var fresh = CreateFreshContextExchange(work);

        var freshRequest = CampaignStateFactory.ReconstructAcceptedPatchRequest(
            completed.Artifact.State,
            PatchContext(fresh.Request),
            CurrentEvidence(fresh));
        Assert.NotEqual(historicalRequest.ArtifactSha256, freshRequest.ArtifactSha256);
        Assert.Equal(fresh.Request.Context.RepositoryContextRef, freshRequest.Context.RepositoryContextRef);
        Assert.Equal(
            historicalRequest.ArtifactSha256,
            completed.Artifact.State.CandidateObservation!.PatchRequestSha256);

        var foreignInput = CreateFreshContextExchange(
            work,
            inputIdentity: "samples/Alternate.csproj");
        AssertInvalidCorrelation(() => CampaignStateFactory.ReconstructAcceptedPatchRequest(
            completed.Artifact.State,
            PatchContext(foreignInput.Request),
            CurrentEvidence(foreignInput)));

        var freshReservation = CampaignStateReducer.ReservePatchInvocation(
            completed.Artifact,
            freshRequest,
            elapsedMilliseconds: 1_000);
        Assert.Equal(CampaignTransitionKind.Applied, freshReservation.Kind);
        Assert.Null(freshReservation.Artifact.State.TerminalOutcome);
        Assert.Equal(CampaignWorkStatus.Accepted, freshReservation.Artifact.State.WorkItems[0].Status);
        var freshInvocation = CampaignStateReducer.CreatePatchInvocationAuthority(
            AcceptForTest(completed.Artifact, freshReservation),
            freshRequest);
        Assert.True(freshInvocation.TryBeginDispatch());
        var reconstructedFromReservation = CampaignStateFactory.ReconstructAcceptedPatchRequest(
            freshReservation.Artifact.State,
            PatchContext(fresh.Request),
            CurrentEvidence(fresh));
        Assert.Equal(freshRequest.ArtifactSha256, reconstructedFromReservation.ArtifactSha256);
        Assert.Equal(
            freshRequest.ArtifactSha256,
            Assert.IsType<CampaignPatchReservation>(
                freshReservation.Artifact.State.ActiveReservation).PatchRequestSha256);

        var reconstructed = CampaignStateReducer.CompletePatchInvocation(
            freshReservation.Artifact,
            freshInvocation,
            freshRequest,
            CreateAcceptedPatchResult(freshRequest, proposal),
            activeElapsedMilliseconds: 500);
        Assert.Equal(CampaignTransitionKind.Applied, reconstructed.Kind);
        Assert.Equal(CampaignTerminalKind.Complete, reconstructed.Artifact.State.TerminalOutcome!.Kind);
        Assert.Equal(CampaignWorkStatus.Accepted, reconstructed.Artifact.State.WorkItems[0].Status);
    }

    [Fact]
    public void Active_and_accepted_reconstruction_use_distinct_complete_projection_sets()
    {
        var scenario = CreateProposalScenario();
        var first = scenario.Plan.WorkItems[0];
        var second = scenario.Plan.WorkItems[1];
        var firstExchange = CreateScribeExchange(first);
        var secondExchange = CreateScribeExchange(second);
        var firstProposal = AdmitProposal(
            scenario,
            WithState(
                scenario.InitialState,
                scenario.InitialState.WorkItems,
                ProviderReservation(first.WorkItemKey, firstExchange)),
            first,
            firstExchange);
        var firstComplete = WithState(
            scenario.InitialState,
            ReplaceWork(scenario.InitialState, first.WorkItemKey, CampaignWorkStatus.ProposalComplete, firstProposal),
            ProviderReservation(second.WorkItemKey, secondExchange));
        var secondProposal = AdmitProposal(scenario, firstComplete, second, secondExchange);

        var firstOnly = WithState(
            scenario.InitialState,
            ReplaceWork(scenario.InitialState, first.WorkItemKey, CampaignWorkStatus.ProposalComplete, firstProposal),
            activeReservation: null);
        var firstRequest = CampaignStateFactory.ReconstructPatchRequest(
            firstOnly,
            PatchContext(firstExchange.Request),
            CurrentEvidence(firstExchange));
        var firstReserved = WithPatchReservation(firstOnly, firstRequest);
        var candidate = CreateAcceptedCompletion(firstReserved, firstRequest, firstProposal).CandidateObservation!;
        var mixed = WithState(
            scenario.InitialState,
            ReplaceWork(
                ReplaceWork(scenario.InitialState, first.WorkItemKey, CampaignWorkStatus.Accepted, firstProposal),
                second.WorkItemKey,
                CampaignWorkStatus.ProposalComplete,
                secondProposal),
            activeReservation: null,
            candidateObservation: candidate);

        var active = CampaignStateFactory.ReconstructPatchRequest(
            mixed,
            PatchContext(firstExchange.Request),
            CurrentEvidence(firstExchange, secondExchange));
        var accepted = CampaignStateFactory.ReconstructAcceptedPatchRequest(
            mixed,
            PatchContext(firstExchange.Request),
            CurrentEvidence(firstExchange));

        Assert.Equal(2, active.Blocks.Length);
        Assert.Equal([first.WorkItemKey, second.WorkItemKey], active.Blocks.Select(block => block.BlockId));
        Assert.Single(accepted.Blocks);
        Assert.Equal(first.WorkItemKey, accepted.Blocks[0].BlockId);
        Assert.Equal(firstRequest.ArtifactSha256, accepted.ArtifactSha256);

        var reconstructedRequests = new[] { "rejected", "stale", "host-failure", "cancelled", "timeout" }
            .Select(laterOutcome => MutateValidState(mixed, root =>
                root["cumulativeOutcome"] = new JsonObject
                {
                    ["kind"] = laterOutcome,
                    ["patchRequestSha256"] = active.ArtifactSha256,
                    ["patchResultCommitmentSha256"] = laterOutcome is "rejected" or "stale" ? Hash('a') : null,
                    ["completedFromCheckpointRevision"] = mixed.CheckpointRevision,
                }))
            .Select(withLaterOutcome => CampaignStateFactory.ReconstructAcceptedPatchRequest(
                withLaterOutcome,
                PatchContext(firstExchange.Request),
                CurrentEvidence(firstExchange)));
        Assert.All(reconstructedRequests, reconstructed =>
            Assert.Equal(firstRequest.ArtifactSha256, reconstructed.ArtifactSha256));
    }

    [Fact]
    public void Cumulative_outcome_uses_a_closed_typed_result_presence_matrix()
    {
        var accepted = CreateAcceptedCandidateScenario();
        Assert.NotNull(accepted.State.CumulativeOutcome);
        Assert.Equal(CampaignCumulativeOutcomeKind.Accepted, accepted.State.CumulativeOutcome.Kind);
        AssertMutationFailure(
            accepted.State,
            root => root["cumulativeOutcome"]!["patchResultCommitmentSha256"] = null,
            CampaignStateValidationCode.InvalidCorrelation);

        foreach (var kind in new[] { "rejected", "stale" })
        {
            AssertValidMutation(accepted.State, root => root["cumulativeOutcome"]!["kind"] = kind);
            AssertMutationFailure(
                accepted.State,
                root =>
                {
                    root["cumulativeOutcome"]!["kind"] = kind;
                    root["cumulativeOutcome"]!["patchResultCommitmentSha256"] = null;
                },
                CampaignStateValidationCode.InvalidCorrelation);
        }

        foreach (var kind in new[] { "host-failure", "cancelled", "timeout" })
        {
            AssertValidMutation(accepted.State, root =>
            {
                root["cumulativeOutcome"]!["kind"] = kind;
                root["cumulativeOutcome"]!["patchResultCommitmentSha256"] = null;
            });
            AssertMutationFailure(
                accepted.State,
                root => root["cumulativeOutcome"]!["kind"] = kind,
                CampaignStateValidationCode.InvalidCorrelation);
        }
    }

    [Fact]
    public void Work_outcome_preserves_provider_final_disposition_and_c1_disposition()
    {
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var exchange = CreateScribeExchange(work);
        var retryable = MutateValidState(scenario.InitialState, root =>
        {
            var row = root["workItems"]![0]!;
            row["status"] = "closed";
            row["trustedProposal"] = null;
            row["closedOutcome"] = new JsonObject
            {
                ["stage"] = "scribe",
                ["code"] = "provider-failure",
                ["providerDisposition"] = "retryable",
                ["scribeRequestSha256"] = exchange.Request.ArtifactSha256,
                ["attemptId"] = exchange.Result.AttemptId.Value,
                ["patchRequestSha256"] = null,
                ["patchResultCommitmentSha256"] = null,
            };
        });

        CampaignStateFactory.ValidateCurrentContext(
            retryable,
            "style.synthetic",
            scenario.StyleProjection,
            "samples/Synthetic.csproj",
            scenario.Input,
            scenario.Plan);
        var roundTrip = CampaignStateJson.Parse(CampaignStateJson.Write(retryable));
        Assert.True(roundTrip.IsValid);
        Assert.Equal(
            CampaignProviderFinalDisposition.Retryable,
            roundTrip.Artifact!.State.WorkItems[0].ClosedOutcome!.ProviderDisposition);
        Assert.Empty(typeof(CampaignWorkClosedOutcome).GetConstructors());

        AssertMutationFailure(
            retryable,
            root => root["workItems"]![0]!["closedOutcome"]!["providerDisposition"] = null,
            CampaignStateValidationCode.InvalidShape);
        AssertMutationFailure(
            retryable,
            root =>
            {
                root["workItems"]![0]!["closedOutcome"]!["code"] = "validation-failure";
                root["workItems"]![0]!["closedOutcome"]!["providerDisposition"] = "terminal";
            },
            CampaignStateValidationCode.InvalidShape);

        var planningRollback = MutateValidState(retryable, root =>
        {
            root["workItems"]![0]!["closedOutcome"]!["stage"] = "planning";
            root["workItems"]![0]!["closedOutcome"]!["code"] = "planning-terminal";
            root["workItems"]![0]!["closedOutcome"]!["providerDisposition"] = null;
            root["workItems"]![0]!["closedOutcome"]!["scribeRequestSha256"] = null;
            root["workItems"]![0]!["closedOutcome"]!["attemptId"] = null;
            root["workItems"]![0]!["closedOutcome"]!["patchRequestSha256"] = null;
            root["workItems"]![0]!["closedOutcome"]!["patchResultCommitmentSha256"] = null;
        });
        AssertInvalidCorrelation(() => CampaignStateFactory.ValidateCurrentContext(
            planningRollback,
            "style.synthetic",
            scenario.StyleProjection,
            "samples/Synthetic.csproj",
            scenario.Input,
            scenario.Plan));

        var patch = MutateValidState(retryable, root =>
        {
            var outcome = root["workItems"]![0]!["closedOutcome"]!;
            outcome["stage"] = "patch";
            outcome["code"] = "patch-rejected";
            outcome["providerDisposition"] = null;
            outcome["scribeRequestSha256"] = null;
            outcome["attemptId"] = null;
            outcome["patchRequestSha256"] = Hash('a');
            outcome["patchResultCommitmentSha256"] = Hash('b');
        });
        Assert.Equal(CampaignWorkOutcomeStage.Patch, patch.WorkItems[0].ClosedOutcome!.Stage);
        Assert.Equal(CampaignWorkOutcomeCode.PatchRejected, patch.WorkItems[0].ClosedOutcome!.Code);
        Assert.Equal(Hash('a'), patch.WorkItems[0].ClosedOutcome!.PatchRequestSha256);
        Assert.Equal(Hash('b'), patch.WorkItems[0].ClosedOutcome!.PatchResultCommitmentSha256);
        CampaignStateFactory.ValidateCurrentContext(
            patch,
            "style.synthetic",
            scenario.StyleProjection,
            "samples/Synthetic.csproj",
            scenario.Input,
            scenario.Plan);

        AssertMutationFailure(
            patch,
            root => root["workItems"]![0]!["closedOutcome"]!["scribeRequestSha256"] = Hash('c'),
            CampaignStateValidationCode.InvalidShape);
        AssertMutationFailure(
            patch,
            root => root["workItems"]![0]!["closedOutcome"]!["patchResultCommitmentSha256"] = null,
            CampaignStateValidationCode.InvalidShape);
        AssertMutationFailure(
            retryable,
            root => root["workItems"]![0]!["closedOutcome"]!["patchRequestSha256"] = Hash('c'),
            CampaignStateValidationCode.InvalidShape);
    }

    [Fact]
    public void Typed_rejected_results_have_distinct_exact_commitments()
    {
        var request = ParsePatchRequest("repository-request.json");
        var rejected = ParsePatchResult("rejected-no-op-result.json");
        var alternate = DocumentationPatchValidator.CreateResult(
            request,
            rejected.Outcome,
            rejected.Targets.Select(_ => DocumentationPatchTargetStatus.Invalid),
            rejected.ChangedFiles.Select(file => new DocumentationPatchChangedFileInput(
                file.Path,
                file.OriginalFileSha256,
                file.CandidateFileSha256,
                file.ChangedDocumentationBlockCount,
                file.OriginalDocumentationByteCount,
                file.CandidateDocumentationByteCount,
                file.OriginalDocumentationLineCount,
                file.CandidateDocumentationLineCount)),
            rejected.Invariants,
            [new DocumentationPatchDiagnostic(
                DocumentationPatchDiagnosticSeverity.Error,
                "patch.rejected.unsafe-change",
                rejected.Targets[0].BlockId,
                Path: null,
                Pointer: null)]);

        var first = CampaignStateFactory.CreatePatchResultCommitment(request, rejected);
        var second = CampaignStateFactory.CreatePatchResultCommitment(request, alternate);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Patch_rejection_factory_closes_only_the_exact_independent_invalid_work_and_replays_exactly()
    {
        var scenario = CreatePatchRejectionScenario();

        var decision = CampaignStateFactory.CreatePatchRejectionReduction(
            scenario.Predecessor,
            "style.synthetic",
            scenario.ProposalScenario.StyleProjection,
            "samples/Synthetic.csproj",
            scenario.ProposalScenario.Input,
            scenario.ProposalScenario.Plan,
            scenario.Request,
            scenario.Result,
            scenario.SelectedWorkItemKey);

        Assert.Equal(CampaignPatchRejectionDecisionKind.Removable, decision.Kind);
        var reduction = Assert.IsType<CampaignPatchRejectionReduction>(decision.Reduction);
        Assert.Empty(typeof(CampaignPatchRejectionReduction).GetConstructors());
        var invocationAuthority = CampaignStateReducer.CreatePatchInvocationAuthority(
            AcceptForTest(scenario.ReservationPredecessor, scenario.ReservationTransition),
            scenario.Request);
        Assert.True(invocationAuthority.TryBeginDispatch());
        Assert.False(invocationAuthority.TryBeginDispatch());

        var applied = CampaignStateReducer.ApplyPatchRejection(
            scenario.Predecessor,
            invocationAuthority,
            reduction,
            activeElapsedMilliseconds: 500);
        Assert.Equal(CampaignTransitionKind.Applied, applied.Kind);
        Assert.Equal(scenario.Predecessor.CheckpointRevision + 1, applied.Artifact.CheckpointRevision);
        var selected = Assert.Single(applied.Artifact.State.WorkItems, item =>
            string.Equals(item.WorkItemKey, scenario.SelectedWorkItemKey, StringComparison.Ordinal));
        Assert.Equal(CampaignWorkStatus.Closed, selected.Status);
        Assert.Equal(CampaignWorkOutcomeStage.Patch, selected.ClosedOutcome!.Stage);
        Assert.Equal(CampaignWorkOutcomeCode.PatchRejected, selected.ClosedOutcome.Code);
        Assert.Equal(scenario.Request.ArtifactSha256, selected.ClosedOutcome.PatchRequestSha256);
        Assert.Equal(
            CampaignStateFactory.CreatePatchResultCommitment(scenario.Request, scenario.Result),
            selected.ClosedOutcome.PatchResultCommitmentSha256);
        Assert.Null(applied.Artifact.State.ActiveReservation);
        Assert.Equal(CampaignCumulativeOutcomeKind.Rejected, applied.Artifact.State.CumulativeOutcome!.Kind);
        Assert.Equal(500, applied.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.Observed);

        var repeatedFromPredecessor = CampaignStateReducer.ApplyPatchRejection(
            scenario.Predecessor,
            invocationAuthority,
            reduction,
            activeElapsedMilliseconds: 500);
        Assert.Equal(CampaignTransitionKind.Applied, repeatedFromPredecessor.Kind);
        Assert.True(applied.Artifact.ExactUtf8Json.AsSpan().SequenceEqual(
            repeatedFromPredecessor.Artifact.ExactUtf8Json.AsSpan()));
        var repeatedFromSuccessor = CampaignStateReducer.ApplyPatchRejection(
            applied.Artifact,
            invocationAuthority,
            reduction,
            activeElapsedMilliseconds: 500);
        Assert.Equal(CampaignTransitionKind.Unchanged, repeatedFromSuccessor.Kind);
        Assert.True(applied.Artifact.ExactUtf8Json.AsSpan().SequenceEqual(
            repeatedFromSuccessor.Artifact.ExactUtf8Json.AsSpan()));

        var conflicting = CampaignStateReducer.Stop(
            applied.Artifact,
            CampaignTerminalKind.Cancelled);
        Assert.Equal(CampaignTransitionKind.Applied, conflicting.Kind);
        var rebound = CampaignStateReducer.ApplyPatchRejection(
            conflicting.Artifact,
            invocationAuthority,
            reduction,
            activeElapsedMilliseconds: 500);
        Assert.Equal(CampaignTransitionKind.Rejected, rebound.Kind);
        Assert.Equal(CampaignTransitionFailure.ConflictingReplay, rebound.Failure);
    }

    [Fact]
    public void Patch_rejection_factory_fails_closed_for_each_attribution_boundary()
    {
        var scenario = CreatePatchRejectionScenario();
        var cases = new[]
        {
            ("wrong-work", scenario.Predecessor.State, scenario.Request, scenario.Result, "work.missing"),
        };

        foreach (var (name, state, request, result, key) in cases)
        {
            var decision = CampaignStateFactory.CreatePatchRejectionReduction(
                CampaignStateJson.CreateArtifact(state),
                "style.synthetic",
                scenario.ProposalScenario.StyleProjection,
                "samples/Synthetic.csproj",
                scenario.ProposalScenario.Input,
                scenario.ProposalScenario.Plan,
                request,
                result,
                key);
            Assert.True(
                decision.Kind == CampaignPatchRejectionDecisionKind.NonRemovable,
                name);
            Assert.Null(decision.Reduction);
        }

        foreach (var rejected in new[]
        {
            CreateRejectedPatchResult(
                scenario.Request,
                scenario.SelectedWorkItemKey,
                "patch.rejected.no-effective-change",
                diagnosticBlockId: null),
            CreateRejectedPatchResult(
                scenario.Request,
                scenario.Request.Blocks[1].BlockId,
                "patch.rejected.unsafe-change",
                diagnosticBlockId: scenario.Request.Blocks[1].BlockId),
        })
        {
            var decision = CampaignStateFactory.CreatePatchRejectionReduction(
                scenario.Predecessor,
                "style.synthetic",
                scenario.ProposalScenario.StyleProjection,
                "samples/Synthetic.csproj",
                scenario.ProposalScenario.Input,
                scenario.ProposalScenario.Plan,
                scenario.Request,
                rejected,
                scenario.SelectedWorkItemKey);
            Assert.Equal(CampaignPatchRejectionDecisionKind.NonRemovable, decision.Kind);
        }
    }

    [Fact]
    public void Non_removable_patch_rejection_is_a_durable_cumulative_stop()
    {
        var scenario = CreatePatchRejectionScenario();
        var result = CreateRejectedPatchResult(
            scenario.Request,
            scenario.SelectedWorkItemKey,
            "patch.rejected.no-effective-change",
            diagnosticBlockId: null);
        var invocation = CampaignStateReducer.CreatePatchInvocationAuthority(
            AcceptForTest(scenario.ReservationPredecessor, scenario.ReservationTransition),
            scenario.Request);
        Assert.True(invocation.TryBeginDispatch());

        var completed = CampaignStateReducer.CompletePatchInvocation(
            scenario.Predecessor,
            invocation,
            scenario.Request,
            result,
            activeElapsedMilliseconds: 500);

        Assert.Equal(CampaignTransitionKind.Applied, completed.Kind);
        Assert.Null(completed.Artifact.State.ActiveReservation);
        Assert.Equal(CampaignCumulativeOutcomeKind.Rejected, completed.Artifact.State.CumulativeOutcome!.Kind);
        Assert.Equal(CampaignTerminalKind.Failed, completed.Artifact.State.TerminalOutcome!.Kind);
        Assert.All(completed.Artifact.State.WorkItems,
            item => Assert.Equal(CampaignWorkStatus.ProposalComplete, item.Status));
        var rerun = CampaignStateReducer.ReservePatchInvocation(
            completed.Artifact,
            scenario.Request,
            elapsedMilliseconds: 1_000);
        Assert.Equal(CampaignTransitionKind.Rejected, rerun.Kind);
    }

    [Fact]
    public void Reducer_consumes_one_bound_M3_outcome_and_settles_the_exact_reservation()
    {
        var scenario = CreateProposalScenario(costCurrency: "currency.usd");
        var work = scenario.Plan.WorkItems[0];
        var requestExchange = CreateScribeExchange(work);
        var authority = ExecutionAuthority(requestExchange);

        var admitted = CampaignStateReducer.AdmitProviderInvocation(
            CampaignStateJson.CreateArtifact(scenario.InitialState),
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            work.WorkItemKey,
            requestExchange.Request);

        Assert.Equal(CampaignTransitionKind.Applied, admitted.Kind);
        var reserved = Assert.IsType<CampaignProviderReservation>(admitted.Artifact.State.ActiveReservation);
        var attemptId = reserved.AttemptId;
        Assert.Equal(1, admitted.Artifact.State.WorkItems[0].OuterAttemptCount);
        Assert.Equal(1, admitted.Artifact.State.LineageCharges.OuterInvocations);

        var completionExchange = CreateScribeExchange(work, attemptId: attemptId.Value);
        Assert.Equal(requestExchange.Request.ArtifactSha256, completionExchange.Request.ArtifactSha256);
        var outcome = DocumentationScribeValidation.BindValidatedRunOutcome(
            completionExchange.Request,
            attemptId,
            completionExchange.Result);
        var acceptedAdmission = AcceptForTest(
            CampaignStateJson.CreateArtifact(scenario.InitialState),
            admitted);
        var substitutedAuthority = authority with { ProviderConfigurationId = "provider.substituted" };
        Assert.Throws<ArgumentException>(() => CampaignStateReducer.CreateProviderInvocationAuthority(
            acceptedAdmission,
            substitutedAuthority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            completionExchange.Request));
        var invocationAuthority = CampaignStateReducer.CreateProviderInvocationAuthority(
            acceptedAdmission,
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            completionExchange.Request);
        Assert.True(invocationAuthority.TryBeginDispatch(out var dispatchedAttempt));
        Assert.Equal(attemptId, dispatchedAttempt);
        Assert.False(invocationAuthority.TryBeginDispatch(out _));
        var substitutedCompletion = CampaignStateReducer.CompleteProviderInvocation(
            admitted.Artifact,
            invocationAuthority,
            substitutedAuthority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            outcome,
            completionExchange.Result.RunEnvelope.ElapsedMilliseconds);
        Assert.Equal(CampaignTransitionKind.Rejected, substitutedCompletion.Kind);
        Assert.Equal(CampaignTransitionFailure.InvalidAuthority, substitutedCompletion.Failure);
        var substitutedRetry = CampaignStateReducer.RetryProviderInvocation(
            admitted.Artifact,
            substitutedAuthority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            work.WorkItemKey,
            completionExchange.Request);
        Assert.Equal(CampaignTransitionKind.Rejected, substitutedRetry.Kind);
        Assert.Equal(CampaignTransitionFailure.InvalidAuthority, substitutedRetry.Failure);

        var completed = CampaignStateReducer.CompleteProviderInvocation(
            admitted.Artifact,
            invocationAuthority,
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            outcome,
            completionExchange.Result.RunEnvelope.ElapsedMilliseconds);

        Assert.True(completed.Kind == CampaignTransitionKind.Applied, completed.Failure.ToString());
        Assert.Null(completed.Artifact.State.ActiveReservation);
        Assert.Equal(CampaignWorkStatus.ProposalComplete, completed.Artifact.State.WorkItems[0].Status);
        Assert.Equal(
            completionExchange.Result.RunEnvelope.ProviderRequestCount,
            completed.Artifact.State.LineageCharges.ProviderRequests.Observed);
        Assert.Equal(
            completionExchange.Result.RunEnvelope.ElapsedMilliseconds,
            completed.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.Observed);

        var replay = CampaignStateReducer.ApplyTransition(
            completed.Artifact,
            completed);
        Assert.Equal(CampaignTransitionKind.Unchanged, replay.Kind);
        Assert.Equal(CampaignTransitionFailure.None, replay.Failure);
    }

    [Fact]
    public async Task Provider_dispatch_grant_is_owned_by_the_exact_writer_and_consumed_once()
    {
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var exchange = CreateScribeExchange(work);
        var authority = ExecutionAuthority(exchange);
        var predecessor = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var admitted = CampaignStateReducer.AdmitProviderInvocation(
            predecessor,
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            work.WorkItemKey,
            exchange.Request);

        var successful = await CampaignCheckpointAcceptance.AcceptAsync(
            new TransitionCheckpointStore(predecessor),
            admitted);
        var accepted = Assert.IsType<CampaignAcceptedCheckpoint>(successful.AcceptedCheckpoint);
        _ = CampaignStateReducer.CreateProviderInvocationAuthority(
            accepted,
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            exchange.Request);
        var reused = Assert.Throws<ArgumentException>(() =>
            CampaignStateReducer.CreateProviderInvocationAuthority(
                accepted,
                authority,
                "style.synthetic",
                scenario.StyleProjection,
                scenario.Input,
                scenario.Plan,
                exchange.Request));
        Assert.Contains("does not grant this dispatch", reused.Message, StringComparison.Ordinal);

        var concurrentStore = new TransitionCheckpointStore(predecessor)
        {
            ReplaceResult = CampaignCheckpointWriteKind.CurrentMismatch,
            WinnerOnRejectedWrite = admitted.Artifact,
        };
        var concurrent = await CampaignCheckpointAcceptance.AcceptAsync(concurrentStore, admitted);
        var concurrentAccepted = Assert.IsType<CampaignAcceptedCheckpoint>(concurrent.AcceptedCheckpoint);
        var concurrentLoser = Assert.Throws<ArgumentException>(() =>
            CampaignStateReducer.CreateProviderInvocationAuthority(
                concurrentAccepted,
                authority,
                "style.synthetic",
                scenario.StyleProjection,
                scenario.Input,
                scenario.Plan,
                exchange.Request));
        Assert.Contains("does not grant this dispatch", concurrentLoser.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Closed_retryable_provider_outcome_admits_a_fresh_changed_request_and_retires_the_old_attempt()
    {
        var scenario = CreateProposalScenario(
            costEnforced: false,
            maximumElapsedMilliseconds: 300_000);
        var work = scenario.Plan.WorkItems[0];
        var firstExchange = CreateScribeExchange(work);
        var authority = ExecutionAuthority(firstExchange);
        var admitted = CampaignStateReducer.AdmitProviderInvocation(
            CampaignStateJson.CreateArtifact(scenario.InitialState),
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            work.WorkItemKey,
            firstExchange.Request);
        var firstAttempt = Assert.IsType<CampaignProviderReservation>(
            admitted.Artifact.State.ActiveReservation).AttemptId;
        var failureExchange = CreateScribeExchange(
            work,
            attemptId: firstAttempt.Value,
            resultFixture: "retryable-failure-result.json");
        var outcome = DocumentationScribeValidation.BindValidatedRunOutcome(
            failureExchange.Request,
            firstAttempt,
            failureExchange.Result);
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(
            AcceptForTest(CampaignStateJson.CreateArtifact(scenario.InitialState), admitted),
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            failureExchange.Request);
        Assert.True(invocation.TryBeginDispatch(out var dispatchedAttempt));
        Assert.Equal(firstAttempt, dispatchedAttempt);

        var completed = CampaignStateReducer.CompleteProviderInvocation(
            admitted.Artifact,
            invocation,
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            outcome,
            failureExchange.Result.RunEnvelope.ElapsedMilliseconds);

        Assert.Equal(CampaignTransitionKind.Applied, completed.Kind);
        Assert.Null(completed.Artifact.State.TerminalOutcome);
        Assert.Equal(CampaignProviderFinalDisposition.Retryable,
            completed.Artifact.State.WorkItems[0].ClosedOutcome!.ProviderDisposition);

        var fresh = CreateFreshContextExchange(work);
        Assert.NotEqual(firstExchange.Request.ArtifactSha256, fresh.Request.ArtifactSha256);
        var foreignInput = CreateFreshContextExchange(
            work,
            inputIdentity: "samples/Alternate.csproj");
        var rejectedForeignInput = CampaignStateReducer.RetryProviderInvocation(
            completed.Artifact,
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            work.WorkItemKey,
            foreignInput.Request);
        Assert.Equal(CampaignTransitionKind.Rejected, rejectedForeignInput.Kind);
        Assert.Equal(CampaignTransitionFailure.InvalidAuthority, rejectedForeignInput.Failure);
        var retry = CampaignStateReducer.RetryProviderInvocation(
            completed.Artifact,
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            work.WorkItemKey,
            fresh.Request);

        Assert.Equal(CampaignTransitionKind.Applied, retry.Kind);
        Assert.Equal(CampaignWorkStatus.Planned, retry.Artifact.State.WorkItems[0].Status);
        Assert.Null(retry.Artifact.State.WorkItems[0].ClosedOutcome);
        Assert.NotEqual(
            firstAttempt,
            Assert.IsType<CampaignProviderReservation>(retry.Artifact.State.ActiveReservation).AttemptId);
        Assert.Equal(fresh.Request.ArtifactSha256,
            Assert.IsType<CampaignProviderReservation>(retry.Artifact.State.ActiveReservation).ScribeRequestSha256);

        var late = CampaignStateReducer.CompleteProviderInvocation(
            retry.Artifact,
            invocation,
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            outcome,
            failureExchange.Result.RunEnvelope.ElapsedMilliseconds);
        Assert.Equal(CampaignTransitionKind.Rejected, late.Kind);
        Assert.Equal(CampaignTransitionFailure.InvalidCorrelation, late.Failure);
    }

    [Fact]
    public void Disabled_campaign_cost_ignores_valid_provider_cost_telemetry()
    {
        var scenario = CreateProposalScenario(costEnforced: false);
        var work = scenario.Plan.WorkItems[0];
        var exchange = CreateScribeExchange(work);
        var authority = ExecutionAuthority(exchange);
        var admitted = CampaignStateReducer.AdmitProviderInvocation(
            CampaignStateJson.CreateArtifact(scenario.InitialState),
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            work.WorkItemKey,
            exchange.Request);
        var attempt = Assert.IsType<CampaignProviderReservation>(
            admitted.Artifact.State.ActiveReservation).AttemptId;
        var completedExchange = CreateScribeExchange(work, attemptId: attempt.Value);
        var outcome = DocumentationScribeValidation.BindValidatedRunOutcome(
            completedExchange.Request,
            attempt,
            completedExchange.Result);
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(
            AcceptForTest(CampaignStateJson.CreateArtifact(scenario.InitialState), admitted),
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            completedExchange.Request);
        Assert.True(invocation.TryBeginDispatch(out var dispatchedAttempt));
        Assert.Equal(attempt, dispatchedAttempt);

        var completed = CampaignStateReducer.CompleteProviderInvocation(
            admitted.Artifact,
            invocation,
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            outcome,
            completedExchange.Result.RunEnvelope.ElapsedMilliseconds,
            simultaneousStop: CampaignTerminalKind.Cancelled);

        Assert.Equal(CampaignTransitionKind.Applied, completed.Kind);
        Assert.Equal(0, completed.Artifact.State.LineageCharges.CostMicrounits.TotalCharged);
        Assert.Equal(CampaignWorkStatus.ProposalComplete, completed.Artifact.State.WorkItems[0].Status);
        Assert.Null(completed.Artifact.State.TerminalOutcome);
    }

    [Fact]
    public void Provider_admission_at_candidate_ceiling_persists_exhaustion_before_dispatch()
    {
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var exchange = CreateScribeExchange(work);
        var atCeiling = WithState(
            scenario.InitialState,
            scenario.InitialState.WorkItems.Select(item =>
                string.Equals(item.WorkItemKey, work.WorkItemKey, StringComparison.Ordinal)
                    ? item with
                    {
                        CandidateAttemptCount =
                            scenario.InitialState.ConfiguredCeilings.CampaignBudget.MaximumCandidatesPerBlock,
                    }
                    : item).ToImmutableArray(),
            activeReservation: null);

        var result = CampaignStateReducer.AdmitProviderInvocation(
            CampaignStateJson.CreateArtifact(atCeiling),
            ExecutionAuthority(exchange),
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            work.WorkItemKey,
            exchange.Request);

        Assert.Equal(CampaignTransitionKind.Applied, result.Kind);
        Assert.Null(result.Artifact.State.ActiveReservation);
        Assert.Equal(CampaignTerminalKind.Exhausted, result.Artifact.State.TerminalOutcome!.Kind);
    }

    [Fact]
    public void Full_active_projection_blocks_dispatch_and_authoritative_proposal_still_settles()
    {
        var scenario = CreateProposalScenario(
            costCurrency: "currency.usd",
            maximumBlocks: 1);
        var firstWork = scenario.Plan.WorkItems[0];
        var secondWork = scenario.Plan.WorkItems[1];
        var firstExchange = CreateScribeExchange(firstWork);
        var firstProposal = AdmitProposal(
            scenario,
            WithState(
                scenario.InitialState,
                scenario.InitialState.WorkItems,
                ProviderReservation(firstWork.WorkItemKey, firstExchange)),
            firstWork,
            firstExchange);
        var atCapacity = WithState(
            scenario.InitialState,
            ReplaceWork(
                scenario.InitialState,
                firstWork.WorkItemKey,
                CampaignWorkStatus.ProposalComplete,
                firstProposal),
            activeReservation: null);
        var secondExchange = CreateScribeExchange(secondWork);
        var secondAuthority = ExecutionAuthority(secondExchange);

        var blocked = CampaignStateReducer.AdmitProviderInvocation(
            CampaignStateJson.CreateArtifact(atCapacity),
            secondAuthority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            secondWork.WorkItemKey,
            secondExchange.Request);

        Assert.Equal(CampaignTransitionKind.Rejected, blocked.Kind);
        Assert.Equal(CampaignTransitionFailure.ProjectionCapacityUnavailable, blocked.Failure);
        Assert.True(CampaignStateJson.CreateArtifact(atCapacity).ExactUtf8Json.AsSpan()
            .SequenceEqual(blocked.Artifact.ExactUtf8Json.AsSpan()));
        Assert.Equal(CampaignWorkStatus.ProposalComplete, blocked.Artifact.State.WorkItems[0].Status);

        var admittedBeforeCapacity = CampaignStateReducer.AdmitProviderInvocation(
            CampaignStateJson.CreateArtifact(scenario.InitialState),
            secondAuthority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            secondWork.WorkItemKey,
            secondExchange.Request);
        var reservation = Assert.IsType<CampaignProviderReservation>(
            admittedBeforeCapacity.Artifact.State.ActiveReservation);
        var correlatedAtCapacity = WithState(
            admittedBeforeCapacity.Artifact.State,
            ReplaceWork(
                admittedBeforeCapacity.Artifact.State,
                firstWork.WorkItemKey,
                CampaignWorkStatus.ProposalComplete,
                firstProposal),
            admittedBeforeCapacity.Artifact.State.ActiveReservation);
        var correlatedArtifact = CampaignStateJson.CreateArtifact(correlatedAtCapacity);
        var completionExchange = CreateScribeExchange(secondWork, attemptId: reservation.AttemptId.Value);
        var exception = Assert.Throws<ArgumentException>(() =>
            CampaignStateReducer.CreateProviderInvocationAuthority(
            AcceptCurrentForTest(correlatedArtifact),
            secondAuthority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            completionExchange.Request));
        Assert.Contains("does not grant this dispatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregate_invalid_proposal_fails_closed_without_becoming_budget_exhaustion()
    {
        var scenario = CreateProposalScenario(costCurrency: "currency.usd");
        var firstWork = scenario.Plan.WorkItems[0];
        var secondWork = scenario.Plan.WorkItems[1];
        var firstExchange = CreateScribeExchange(firstWork);
        var firstProposal = AdmitProposal(
            scenario,
            WithState(
                scenario.InitialState,
                scenario.InitialState.WorkItems,
                ProviderReservation(firstWork.WorkItemKey, firstExchange)),
            firstWork,
            firstExchange);
        var firstComplete = WithState(
            scenario.InitialState,
            ReplaceWork(
                scenario.InitialState,
                firstWork.WorkItemKey,
                CampaignWorkStatus.ProposalComplete,
                firstProposal),
            activeReservation: null);
        var collisionId = firstProposal.Evidence[0].EvidenceReferenceId;
        string? replacedId = null;
        void MutateRequest(JsonObject root)
        {
            var evidence = root["evidenceReferences"]![0]!;
            replacedId = evidence["evidenceReferenceId"]!.GetValue<string>();
            evidence["evidenceReferenceId"] = collisionId;
            evidence["contentSha256"] = Hash('f');
        }

        void MutateResult(JsonObject root)
        {
            foreach (var ids in root["terminal"]!["contentUnits"]!
                .AsArray()
                .Select(unit => unit!["evidenceReferenceIds"]!.AsArray()))
            {
                for (var index = 0; index < ids.Count; index++)
                {
                    if (string.Equals(ids[index]!.GetValue<string>(), replacedId, StringComparison.Ordinal))
                    {
                        ids[index] = collisionId;
                    }
                }
            }
        }

        var requested = CreateScribeExchange(secondWork, requestMutation: MutateRequest, resultMutation: MutateResult);
        var authority = ExecutionAuthority(requested);
        var predecessor = CampaignStateJson.CreateArtifact(firstComplete);
        var admitted = CampaignStateReducer.AdmitProviderInvocation(
            predecessor,
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            secondWork.WorkItemKey,
            requested.Request);
        Assert.Equal(CampaignTransitionKind.Applied, admitted.Kind);
        var reservation = Assert.IsType<CampaignProviderReservation>(admitted.Artifact.State.ActiveReservation);
        var completionExchange = CreateScribeExchange(
            secondWork,
            attemptId: reservation.AttemptId.Value,
            requestMutation: MutateRequest,
            resultMutation: MutateResult);
        var outcome = DocumentationScribeValidation.BindValidatedRunOutcome(
            completionExchange.Request,
            reservation.AttemptId,
            completionExchange.Result);
        Assert.Equal(requested.Request.ArtifactSha256, completionExchange.Request.ArtifactSha256);
        Assert.Equal(authority, ExecutionAuthority(completionExchange));
        Assert.Equal(reservation.ScribeRequestSha256, outcome.Request.ArtifactSha256);
        Assert.Equal(reservation.AttemptId, outcome.RunResult.AttemptId);
        Assert.Equal(reservation.AttemptId, outcome.RunResult.RunEnvelope.AttemptId);
        Assert.NotEqual(
            CampaignBudgetDecisionKind.Invalid,
            CampaignBudgetAccounting.SettleProviderInvocation(
                admitted.Artifact.State,
                outcome,
                completionExchange.Result.RunEnvelope.ElapsedMilliseconds).Kind);
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(
            AcceptForTest(predecessor, admitted),
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            completionExchange.Request);
        Assert.True(invocation.TryBeginDispatch(out _));

        var completed = CampaignStateReducer.CompleteProviderInvocation(
            admitted.Artifact,
            invocation,
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            outcome,
            completionExchange.Result.RunEnvelope.ElapsedMilliseconds);

        Assert.Equal(CampaignTransitionKind.Rejected, completed.Kind);
        Assert.Equal(CampaignTransitionFailure.InvalidAuthority, completed.Failure);
        Assert.True(admitted.Artifact.ExactUtf8Json.AsSpan()
            .SequenceEqual(completed.Artifact.ExactUtf8Json.AsSpan()));
    }

    [Fact]
    public void Valid_budget_terminal_overrun_is_settled_and_cleared_as_durable_exhaustion()
    {
        var scenario = CreateProposalScenario(costCurrency: "currency.usd");
        var work = scenario.Plan.WorkItems[0];
        var requestExchange = CreateScribeExchange(work);
        var authority = ExecutionAuthority(requestExchange);
        var admitted = CampaignStateReducer.AdmitProviderInvocation(
            CampaignStateJson.CreateArtifact(scenario.InitialState),
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            work.WorkItemKey,
            requestExchange.Request);
        var attempt = Assert.IsType<CampaignProviderReservation>(
            admitted.Artifact.State.ActiveReservation).AttemptId;
        var completedExchange = CreateScribeExchange(
            work,
            attemptId: attempt.Value,
            resultMutation: root =>
            {
                root["terminal"]!["code"] = "scribe.failure.budget";
                root["terminal"]!.AsObject().Remove("providerFinalDisposition");
                root["runEnvelope"]!["usage"] = new JsonObject
                {
                    ["uncachedInputTokens"] =
                        requestExchange.Request.Limits.MaximumUncachedInputTokens + 1,
                };
                root["runEnvelope"]!["cost"] = new JsonObject
                {
                    ["currencyId"] = "currency.usd",
                    ["amountMicrounits"] =
                        requestExchange.Request.Limits.MaximumCostMicrounits + 1,
                };
            },
            resultFixture: "failure-result.json");
        var outcome = DocumentationScribeValidation.BindValidatedRunOutcome(
            completedExchange.Request,
            attempt,
            completedExchange.Result);
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(
            AcceptForTest(CampaignStateJson.CreateArtifact(scenario.InitialState), admitted),
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            completedExchange.Request);
        Assert.True(invocation.TryBeginDispatch(out var dispatchedAttempt));
        Assert.Equal(attempt, dispatchedAttempt);

        var completed = CampaignStateReducer.CompleteProviderInvocation(
            admitted.Artifact,
            invocation,
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            outcome,
            completedExchange.Result.RunEnvelope.ElapsedMilliseconds);

        Assert.Equal(CampaignTransitionKind.Applied, completed.Kind);
        Assert.Null(completed.Artifact.State.ActiveReservation);
        Assert.Equal(CampaignTerminalKind.Exhausted, completed.Artifact.State.TerminalOutcome!.Kind);
        Assert.Equal(
            requestExchange.Request.Limits.MaximumUncachedInputTokens + 1,
            completed.Artifact.State.LineageCharges.UncachedInputTokens.Observed);
    }

    [Theory]
    [InlineData("cancelled", CampaignTerminalKind.Cancelled)]
    [InlineData("timeout", CampaignTerminalKind.Timeout)]
    public void Authoritative_provider_stop_wins_over_simultaneous_budget_exhaustion(
        string outcomeKind,
        CampaignTerminalKind expectedTerminal)
    {
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var requestExchange = CreateScribeExchange(work);
        var authority = ExecutionAuthority(requestExchange);
        var predecessor = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var admitted = CampaignStateReducer.AdmitProviderInvocation(
            predecessor,
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            work.WorkItemKey,
            requestExchange.Request);
        var attempt = Assert.IsType<CampaignProviderReservation>(
            admitted.Artifact.State.ActiveReservation).AttemptId;
        var completionExchange = CreateScribeExchange(
            work,
            attemptId: attempt.Value,
            resultFixture: outcomeKind == "cancelled" ? "cancelled-result.json" : "failure-result.json",
            resultMutation: root =>
            {
                if (outcomeKind == "timeout")
                {
                    root["terminal"]!["code"] = "scribe.failure.timeout";
                    root["terminal"]!.AsObject().Remove("providerFinalDisposition");
                }

                root["runEnvelope"]!["usage"] = new JsonObject
                {
                    ["uncachedInputTokens"] = requestExchange.Request.Limits.MaximumUncachedInputTokens + 1,
                };
            });
        var outcome = DocumentationScribeValidation.BindValidatedRunOutcome(
            completionExchange.Request,
            attempt,
            completionExchange.Result);
        var invocation = CampaignStateReducer.CreateProviderInvocationAuthority(
            AcceptForTest(predecessor, admitted),
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            completionExchange.Request);
        Assert.True(invocation.TryBeginDispatch(out _));

        var completed = CampaignStateReducer.CompleteProviderInvocation(
            admitted.Artifact,
            invocation,
            authority,
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            outcome,
            completionExchange.Result.RunEnvelope.ElapsedMilliseconds);

        Assert.Equal(CampaignTransitionKind.Applied, completed.Kind);
        Assert.Equal(expectedTerminal, completed.Artifact.State.TerminalOutcome!.Kind);
        Assert.Equal(CampaignWorkStatus.Closed, completed.Artifact.State.WorkItems[0].Status);
        Assert.Equal(
            requestExchange.Request.Limits.MaximumUncachedInputTokens + 1,
            completed.Artifact.State.LineageCharges.UncachedInputTokens.Observed);
    }

    [Theory]
    [InlineData(CampaignCumulativeOutcomeKind.Cancelled, CampaignTerminalKind.Cancelled)]
    [InlineData(CampaignCumulativeOutcomeKind.Timeout, CampaignTerminalKind.Timeout)]
    public void Authoritative_patch_stop_wins_over_simultaneous_budget_exhaustion(
        CampaignCumulativeOutcomeKind outcomeKind,
        CampaignTerminalKind expectedTerminal)
    {
        var scenario = CreatePatchRejectionScenario();
        var invocation = CampaignStateReducer.CreatePatchInvocationAuthority(
            AcceptForTest(scenario.ReservationPredecessor, scenario.ReservationTransition),
            scenario.Request);
        Assert.True(invocation.TryBeginDispatch());

        var completed = CampaignStateReducer.CompletePatchHostInvocation(
            scenario.Predecessor,
            invocation,
            scenario.Request,
            outcomeKind,
            activeElapsedMilliseconds:
                scenario.Predecessor.State.ConfiguredCeilings.CampaignBudget.MaximumElapsedMilliseconds + 1);

        Assert.Equal(CampaignTransitionKind.Applied, completed.Kind);
        Assert.Equal(expectedTerminal, completed.Artifact.State.TerminalOutcome!.Kind);
        Assert.Equal(outcomeKind, completed.Artifact.State.CumulativeOutcome!.Kind);
    }

    [Fact]
    public void Public_state_surface_cannot_rebind_a_valid_patch_outcome()
    {
        var scenario = CreatePatchRejectionScenario();
        var decision = CampaignStateFactory.CreatePatchRejectionReduction(
            scenario.Predecessor,
            "style.synthetic",
            scenario.ProposalScenario.StyleProjection,
            "samples/Synthetic.csproj",
            scenario.ProposalScenario.Input,
            scenario.ProposalScenario.Plan,
            scenario.Request,
            scenario.Result,
            scenario.SelectedWorkItemKey);
        var reduction = Assert.IsType<CampaignPatchRejectionReduction>(decision.Reduction);
        var invocation = CampaignStateReducer.CreatePatchInvocationAuthority(
            AcceptForTest(scenario.ReservationPredecessor, scenario.ReservationTransition),
            scenario.Request);
        Assert.True(invocation.TryBeginDispatch());
        var applied = CampaignStateReducer.ApplyPatchRejection(
            scenario.Predecessor,
            invocation,
            reduction);
        var patchOutcome = applied.Artifact.State.WorkItems.Single(item =>
            string.Equals(item.WorkItemKey, scenario.SelectedWorkItemKey, StringComparison.Ordinal)).ClosedOutcome;
        var otherKey = scenario.Request.Blocks.Single(block =>
            !string.Equals(block.BlockId, scenario.SelectedWorkItemKey, StringComparison.Ordinal)).BlockId;
        var rebound = applied.Artifact.State.WorkItems.Select(item =>
            string.Equals(item.WorkItemKey, otherKey, StringComparison.Ordinal)
                ? item with
                {
                    Status = CampaignWorkStatus.Closed,
                    TrustedProposal = null,
                    ClosedOutcome = patchOutcome,
                }
                : item);

        var exception = Assert.Throws<CampaignStateValidationException>(() =>
            CampaignStateFactory.CreateValidated(
                applied.Artifact.State.ProductRevision,
                applied.Artifact.State.CampaignLineage,
                applied.Artifact.State.Snapshot,
                applied.Artifact.State.CheckpointRevision,
                applied.Artifact.State.ConfiguredCeilings,
                applied.Artifact.State.LineageCharges,
                rebound,
                cumulativeOutcome: applied.Artifact.State.CumulativeOutcome));
        Assert.Equal(CampaignStateValidationCode.InvalidCorrelation, exception.Code);
        Assert.Empty(typeof(CampaignAcceptedCheckpoint).GetConstructors());
        Assert.Empty(typeof(CampaignCheckpointAcceptanceResult).GetConstructors());
    }

    [Fact]
    public void Same_and_changed_request_patch_retries_use_fresh_revision_authority_and_reject_old_completion()
    {
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var exchange = CreateScribeExchange(work);
        var proposal = AdmitProposal(
            scenario,
            WithState(
                scenario.InitialState,
                scenario.InitialState.WorkItems,
                ProviderReservation(work.WorkItemKey, exchange)),
            work,
            exchange);
        var proposalComplete = WithState(
            scenario.InitialState,
            ReplaceWork(
                scenario.InitialState,
                work.WorkItemKey,
                CampaignWorkStatus.ProposalComplete,
                proposal),
            activeReservation: null);
        proposalComplete = MutateValidState(proposalComplete, root =>
        {
            var second = root["workItems"]![1]!;
            second["status"] = "closed";
            second["trustedProposal"] = null;
            second["closedOutcome"] = new JsonObject
            {
                ["stage"] = "scribe",
                ["code"] = "insufficient-evidence",
                ["providerDisposition"] = null,
                ["scribeRequestSha256"] = Hash('c'),
                ["attemptId"] = "scribe-attempt.22222222222222222222222222222222",
                ["patchRequestSha256"] = null,
                ["patchResultCommitmentSha256"] = null,
            };
        });
        var request = CampaignStateFactory.ReconstructPatchRequest(
            proposalComplete,
            PatchContext(exchange.Request),
            CurrentEvidence(exchange));

        var first = CampaignStateReducer.ReservePatchInvocation(
            CampaignStateJson.CreateArtifact(proposalComplete),
            request,
            elapsedMilliseconds: 1_000);
        Assert.Equal(CampaignTransitionKind.Applied, first.Kind);
        var firstReservation = Assert.IsType<CampaignPatchReservation>(first.Artifact.State.ActiveReservation);
        Assert.Equal(first.Artifact.CheckpointRevision, firstReservation.ExpectedCheckpointRevision);
        Assert.Equal(1, firstReservation.PatchAttemptCount);
        var oldAuthority = CampaignStateReducer.CreatePatchInvocationAuthority(
            AcceptForTest(CampaignStateJson.CreateArtifact(proposalComplete), first),
            request);
        Assert.True(oldAuthority.TryBeginDispatch());
        var invalidRetry = CampaignStateReducer.RetryPatchInvocation(
            first.Artifact,
            request,
            elapsedMilliseconds: -1);
        Assert.Equal(CampaignTransitionKind.Rejected, invalidRetry.Kind);
        Assert.True(first.Artifact.ExactUtf8Json.AsSpan().SequenceEqual(
            invalidRetry.Artifact.ExactUtf8Json.AsSpan()));

        var sameRequestRetry = CampaignStateReducer.RetryPatchInvocation(
            first.Artifact,
            request,
            elapsedMilliseconds: 1_000);
        Assert.Equal(CampaignTransitionKind.Applied, sameRequestRetry.Kind);
        Assert.Equal(request.ArtifactSha256,
            Assert.IsType<CampaignPatchReservation>(sameRequestRetry.Artifact.State.ActiveReservation).PatchRequestSha256);

        var freshExchange = CreateFreshContextExchange(work);
        var freshRequest = CampaignStateFactory.ReconstructPatchRequest(
            proposalComplete,
            PatchContext(freshExchange.Request),
            CurrentEvidence(freshExchange));
        Assert.NotEqual(request.ArtifactSha256, freshRequest.ArtifactSha256);
        var retry = CampaignStateReducer.RetryPatchInvocation(
            first.Artifact,
            freshRequest,
            elapsedMilliseconds: 1_000);
        Assert.Equal(CampaignTransitionKind.Applied, retry.Kind);
        var retryReservation = Assert.IsType<CampaignPatchReservation>(retry.Artifact.State.ActiveReservation);
        Assert.Equal(first.Artifact.CheckpointRevision + 1, retryReservation.ExpectedCheckpointRevision);
        Assert.Equal(freshRequest.ArtifactSha256, retryReservation.PatchRequestSha256);
        Assert.Equal(2, retry.Artifact.State.LineageCharges.PatchValidationInvocations);
        Assert.Equal(
            1_000,
            retry.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.ConservativeUnobserved);

        var acceptedResult = CreateAcceptedPatchResult(freshRequest, proposal);
        var freshAuthority = CampaignStateReducer.CreatePatchInvocationAuthority(
            AcceptForTest(first.Artifact, retry),
            freshRequest);
        Assert.True(freshAuthority.TryBeginDispatch());
        Assert.False(freshAuthority.TryBeginDispatch());
        var hostTimeout = CampaignStateReducer.CompletePatchHostInvocation(
            retry.Artifact,
            freshAuthority,
            freshRequest,
            CampaignCumulativeOutcomeKind.Timeout,
            activeElapsedMilliseconds: 500);
        Assert.Equal(CampaignTransitionKind.Applied, hostTimeout.Kind);
        Assert.Equal(CampaignTerminalKind.Timeout, hostTimeout.Artifact.State.TerminalOutcome!.Kind);
        Assert.Equal(CampaignCumulativeOutcomeKind.Timeout, hostTimeout.Artifact.State.CumulativeOutcome!.Kind);
        Assert.Equal(500, hostTimeout.Artifact.State.LineageCharges.ActiveElapsedMilliseconds.Observed);

        var late = CampaignStateReducer.CompletePatchInvocation(
            retry.Artifact,
            oldAuthority,
            freshRequest,
            acceptedResult,
            activeElapsedMilliseconds: 500);
        Assert.Equal(CampaignTransitionKind.Rejected, late.Kind);
        Assert.Equal(CampaignTransitionFailure.InvalidCorrelation, late.Failure);

        Assert.Empty(typeof(CampaignPatchInvocationAuthority).GetConstructors());
        var completed = CampaignStateReducer.CompletePatchInvocation(
            retry.Artifact,
            freshAuthority,
            freshRequest,
            acceptedResult,
            activeElapsedMilliseconds: 500);
        Assert.Equal(CampaignTransitionKind.Applied, completed.Kind);
        Assert.Null(completed.Artifact.State.ActiveReservation);
        Assert.Equal(CampaignWorkStatus.Accepted, completed.Artifact.State.WorkItems[0].Status);
        Assert.Equal(CampaignTerminalKind.Complete, completed.Artifact.State.TerminalOutcome!.Kind);
        Assert.Equal(CampaignTerminalReason.AllWorkClosed, completed.Artifact.State.TerminalOutcome.Reason);
        Assert.Equal(retryReservation.ExpectedCheckpointRevision,
            completed.Artifact.State.CumulativeOutcome!.CompletedFromCheckpointRevision);

        var openAccepted = MutateValidState(completed.Artifact.State, root =>
        {
            root["terminalOutcome"] = null;
            var second = root["workItems"]![1]!;
            second["status"] = "planned";
            second["closedOutcome"] = null;
        });
        var acceptedRequest = CampaignStateFactory.ReconstructPatchRequest(
            openAccepted,
            PatchContext(freshExchange.Request),
            CurrentEvidence(freshExchange));
        var acceptedReservation = CampaignStateReducer.ReservePatchInvocation(
            CampaignStateJson.CreateArtifact(openAccepted),
            acceptedRequest,
            elapsedMilliseconds: 1_000);
        Assert.Equal(CampaignTransitionKind.Applied, acceptedReservation.Kind);
        var acceptedRow = Assert.Single(
            acceptedReservation.Artifact.State.WorkItems,
            item => item.Status == CampaignWorkStatus.Accepted);
        Assert.Equal(
            completed.Artifact.State.WorkItems[0].CandidateAttemptCount + 1,
            acceptedRow.CandidateAttemptCount);
    }

    [Fact]
    public void Over_ceiling_accepted_patch_settles_and_clears_without_installing_incompatible_candidate()
    {
        var scenario = CreateProposalScenario(maximumPatchBytes: 1);
        var work = scenario.Plan.WorkItems[0];
        var exchange = CreateScribeExchange(work);
        var proposal = AdmitProposal(
            scenario,
            WithState(
                scenario.InitialState,
                scenario.InitialState.WorkItems,
                ProviderReservation(work.WorkItemKey, exchange)),
            work,
            exchange);
        var proposalComplete = WithState(
            scenario.InitialState,
            ReplaceWork(
                scenario.InitialState,
                work.WorkItemKey,
                CampaignWorkStatus.ProposalComplete,
                proposal),
            activeReservation: null);
        proposalComplete = MutateValidState(proposalComplete, root =>
        {
            var second = root["workItems"]![1]!;
            second["status"] = "closed";
            second["trustedProposal"] = null;
            second["closedOutcome"] = new JsonObject
            {
                ["stage"] = "scribe",
                ["code"] = "insufficient-evidence",
                ["providerDisposition"] = null,
                ["scribeRequestSha256"] = Hash('c'),
                ["attemptId"] = "scribe-attempt.22222222222222222222222222222222",
                ["patchRequestSha256"] = null,
                ["patchResultCommitmentSha256"] = null,
            };
        });
        var request = CampaignStateFactory.ReconstructPatchRequest(
            proposalComplete,
            PatchContext(exchange.Request),
            CurrentEvidence(exchange));
        var reserved = CampaignStateReducer.ReservePatchInvocation(
            CampaignStateJson.CreateArtifact(proposalComplete),
            request,
            elapsedMilliseconds: 1_000);
        var invocation = CampaignStateReducer.CreatePatchInvocationAuthority(
            AcceptForTest(CampaignStateJson.CreateArtifact(proposalComplete), reserved),
            request);
        Assert.True(invocation.TryBeginDispatch());

        var completed = CampaignStateReducer.CompletePatchInvocation(
            reserved.Artifact,
            invocation,
            request,
            CreateAcceptedPatchResult(request, proposal),
            activeElapsedMilliseconds: 500);

        Assert.Equal(CampaignTransitionKind.Applied, completed.Kind);
        Assert.Null(completed.Artifact.State.ActiveReservation);
        Assert.Null(completed.Artifact.State.CandidateObservation);
        Assert.Null(completed.Artifact.State.CumulativeOutcome);
        Assert.Equal(CampaignWorkStatus.ProposalComplete, completed.Artifact.State.WorkItems[0].Status);
        Assert.Equal(CampaignTerminalKind.Exhausted, completed.Artifact.State.TerminalOutcome!.Kind);
    }

    [Fact]
    public void Supersession_revalidates_the_fresh_template_and_continues_lineage_revision()
    {
        var scenario = CreateProposalScenario();
        var successorInput = scenario.Input with
        {
            Snapshot = scenario.Input.Snapshot with
            {
                OpaqueSnapshotBinding = "snapshot.second",
                RepositoryCommitmentSha256 = Hash('9'),
                InputCommitmentSha256 = Hash('8'),
            },
        };
        var successorPlan = CampaignPlanner.Plan(successorInput);
        var successorTemplate = CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateInitial(
            "style.synthetic",
            scenario.StyleProjection,
            "samples/Synthetic.csproj",
            successorInput,
            successorPlan));
        var predecessor = CampaignStateJson.CreateArtifact(scenario.InitialState);
        var simultaneousStop = CampaignStateReducer.Stop(
            predecessor,
            CampaignTerminalKind.Cancelled);

        var applied = CampaignStateReducer.Supersede(
            predecessor,
            CampaignCheckpointAcceptance.CreateInitialAuthority(successorTemplate),
            "style.synthetic",
            scenario.StyleProjection,
            "samples/Synthetic.csproj",
            successorInput,
            successorPlan,
            simultaneousOldSnapshotTransition: simultaneousStop);

        Assert.Equal(CampaignTransitionKind.Applied, applied.Kind);
        Assert.Equal(predecessor.CheckpointRevision + 1, applied.Artifact.CheckpointRevision);
        Assert.Equal(predecessor.Sha256, applied.Artifact.State.Predecessor!.FinalCheckpointSha256);
        Assert.Equal(predecessor.State.LineageCharges, applied.Artifact.State.LineageCharges);
        Assert.Equal(successorTemplate.State.Snapshot, applied.Artifact.State.Snapshot);
        Assert.Null(applied.Artifact.State.CandidateObservation);
        Assert.Null(applied.Artifact.State.ActiveReservation);
        Assert.Null(applied.Artifact.State.TerminalOutcome);

        var replay = CampaignStateReducer.ApplyTransition(
            applied.Artifact,
            applied);
        Assert.Equal(CampaignTransitionKind.Unchanged, replay.Kind);
        Assert.True(applied.Artifact.ExactUtf8Json.AsSpan().SequenceEqual(
            replay.Artifact.ExactUtf8Json.AsSpan()));

        var sameSnapshotTemplate = CampaignStateJson.CreateArtifact(CampaignStateFactory.CreateInitial(
            "style.synthetic",
            scenario.StyleProjection,
            "samples/Synthetic.csproj",
            scenario.Input,
            scenario.Plan));
        var rejected = CampaignStateReducer.Supersede(
            predecessor,
            CampaignCheckpointAcceptance.CreateInitialAuthority(sameSnapshotTemplate),
            "style.synthetic",
            scenario.StyleProjection,
            "samples/Synthetic.csproj",
            scenario.Input,
            scenario.Plan);
        Assert.Equal(CampaignTransitionKind.Rejected, rejected.Kind);
        Assert.Equal(CampaignTransitionFailure.InvalidAuthority, rejected.Failure);
    }

    [Fact]
    public void Initial_authority_rejects_revision_zero_execution_and_patch_history()
    {
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var exchange = CreateScribeExchange(work);
        var proposal = AdmitProposal(
            scenario,
            WithState(
                scenario.InitialState,
                scenario.InitialState.WorkItems,
                ProviderReservation(work.WorkItemKey, exchange)),
            work,
            exchange);
        var proposalComplete = WithState(
            scenario.InitialState,
            ReplaceWork(
                scenario.InitialState,
                work.WorkItemKey,
                CampaignWorkStatus.ProposalComplete,
                proposal),
            activeReservation: null);
        var scribeHistory = MutateValidState(scenario.InitialState, root =>
        {
            var item = root["workItems"]![0]!;
            item["status"] = "closed";
            item["closedOutcome"] = new JsonObject
            {
                ["stage"] = "scribe",
                ["code"] = "insufficient-evidence",
                ["providerDisposition"] = null,
                ["scribeRequestSha256"] = Hash('c'),
                ["attemptId"] = "scribe-attempt.22222222222222222222222222222222",
                ["patchRequestSha256"] = null,
                ["patchResultCommitmentSha256"] = null,
            };
        });
        var patchHistory = MutateValidState(scenario.InitialState, root =>
        {
            var item = root["workItems"]![0]!;
            item["status"] = "closed";
            item["closedOutcome"] = new JsonObject
            {
                ["stage"] = "patch",
                ["code"] = "patch-rejected",
                ["providerDisposition"] = null,
                ["scribeRequestSha256"] = null,
                ["attemptId"] = null,
                ["patchRequestSha256"] = Hash('d'),
                ["patchResultCommitmentSha256"] = Hash('e'),
            };
        });

        foreach (var state in new[] { proposalComplete, scribeHistory, patchHistory })
        {
            Assert.Throws<ArgumentException>(() =>
                CampaignCheckpointAcceptance.CreateInitialAuthority(
                    CampaignStateJson.CreateArtifact(state)));
        }
    }

    [Fact]
    public void Candidate_observation_rejects_duplicate_order_membership_count_and_bound_mutations()
    {
        var scenario = CreateAcceptedCandidateScenario();
        AssertMutationFailure(
            scenario.State,
            root => root["candidateObservation"]!["acceptedWorkItemKeys"]!.AsArray()
                .Add(root["candidateObservation"]!["acceptedWorkItemKeys"]![0]!.GetValue<string>()),
            CampaignStateValidationCode.InvalidCorrelation);
        AssertMutationFailure(
            scenario.State,
            root => root["candidateObservation"]!["patchRequestSha256"] = "not-a-sha",
            CampaignStateValidationCode.InvalidCorrelation);
        AssertMutationFailure(
            scenario.State,
            root => root["candidateObservation"]!["acceptedProjectionCommitmentSha256"] = Hash('f'),
            CampaignStateValidationCode.InvalidCorrelation);
        AssertMutationFailure(
            scenario.State,
            root => root["candidateObservation"]!["changedFiles"]![0]!["changedDocumentationBlockCount"] = 2,
            CampaignStateValidationCode.InvalidBound);
        AssertMutationFailure(
            scenario.State,
            root => root["candidateObservation"]!["changedFiles"]!.AsArray()
                .Add(root["candidateObservation"]!["changedFiles"]![0]!.DeepClone()),
            CampaignStateValidationCode.InvalidCorrelation);
        AssertMutationFailure(
            scenario.State,
            root => root["candidateObservation"]!["changedFiles"]![0]!["candidateDocumentationByteCount"] =
                scenario.State.ConfiguredCeilings.CampaignBudget.MaximumPatchBytes + 1,
            CampaignStateValidationCode.InvalidBound);
    }

    [Fact]
    public void Candidate_and_completion_are_bound_to_the_exact_state_proposal_projection_and_reservation()
    {
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var firstExchange = CreateScribeExchange(work);
        var alternateExchange = CreateScribeExchange(work, resultMutation: root =>
            root["terminal"]!["contentUnits"]![0]!["lines"]![0] =
                "Runs the alternate synthetic widget operation.");
        var firstProposal = AdmitProposal(
            scenario,
            WithState(
                scenario.InitialState,
                scenario.InitialState.WorkItems,
                ProviderReservation(work.WorkItemKey, firstExchange)),
            work,
            firstExchange);
        var alternateProposal = AdmitProposal(
            scenario,
            WithState(
                scenario.InitialState,
                scenario.InitialState.WorkItems,
                ProviderReservation(work.WorkItemKey, alternateExchange)),
            work,
            alternateExchange);
        Assert.NotEqual(firstProposal.ProposalCommitmentSha256, alternateProposal.ProposalCommitmentSha256);

        var proposalComplete = WithState(
            scenario.InitialState,
            ReplaceWork(
                scenario.InitialState,
                work.WorkItemKey,
                CampaignWorkStatus.ProposalComplete,
                firstProposal),
            activeReservation: null);
        var request = CampaignStateFactory.ReconstructPatchRequest(
            proposalComplete,
            PatchContext(firstExchange.Request),
            CurrentEvidence(firstExchange));
        var result = CreateAcceptedPatchResult(request, firstProposal);
        AssertInvalidCorrelation(() => CampaignStateFactory.CreatePatchCompletion(
            proposalComplete,
            request,
            result));
        var reserved = CampaignStateReducer.ReservePatchInvocation(
            CampaignStateJson.CreateArtifact(proposalComplete),
            request,
            elapsedMilliseconds: 1_000);
        Assert.Equal(CampaignTransitionKind.Applied, reserved.Kind);
        var completion = CampaignStateFactory.CreatePatchCompletion(reserved.Artifact.State, request, result);
        Assert.Equal(
            reserved.Artifact.CheckpointRevision,
            completion.CumulativeOutcome.CompletedFromCheckpointRevision);

        AssertInvalidCorrelation(() => WithState(
            scenario.InitialState,
            ReplaceWork(
                scenario.InitialState,
                work.WorkItemKey,
                CampaignWorkStatus.Accepted,
                alternateProposal),
            activeReservation: null,
            candidateObservation: completion.CandidateObservation,
            cumulativeOutcome: completion.CumulativeOutcome));

        var fresh = CreateFreshContextExchange(work);
        var freshRequest = CampaignStateFactory.ReconstructPatchRequest(
            proposalComplete,
            PatchContext(fresh.Request),
            CurrentEvidence(fresh));
        AssertInvalidCorrelation(() => CampaignStateFactory.CreatePatchCompletion(
            reserved.Artifact.State,
            freshRequest,
            CreateAcceptedPatchResult(freshRequest, firstProposal)));
    }

    [Theory]
    [InlineData("whitespace", CampaignStateValidationCode.InvalidCanonicalBytes)]
    [InlineData("duplicate", CampaignStateValidationCode.DuplicateProperty)]
    [InlineData("unknown", CampaignStateValidationCode.UnknownProperty)]
    [InlineData("version", CampaignStateValidationCode.UnsupportedVersion)]
    [InlineData("overflow", CampaignStateValidationCode.InvalidShape)]
    [InlineData("closed-value", CampaignStateValidationCode.InvalidVocabulary)]
    [InlineData("unpaired-surrogate", CampaignStateValidationCode.InvalidShape)]
    public void Noncanonical_or_unrecognized_documents_fail_closed(
        string mutation,
        CampaignStateValidationCode expected)
    {
        var canonical = Encoding.UTF8.GetString(CampaignStateJson.Write(CreateState()));
        var mutated = mutation switch
        {
            "whitespace" => canonical.Replace("{\"campaignStateVersion\"", "{ \"campaignStateVersion\"", StringComparison.Ordinal),
            "duplicate" => canonical.Replace("{\"campaignStateVersion\":1", "{\"campaignStateVersion\":1,\"campaignStateVersion\":1", StringComparison.Ordinal),
            "unknown" => canonical.Replace("{\"campaignStateVersion\":1", "{\"campaignStateVersion\":1,\"unexpected\":null", StringComparison.Ordinal),
            "version" => canonical.Replace("\"campaignStateVersion\":1", "\"campaignStateVersion\":2", StringComparison.Ordinal),
            "overflow" => canonical.Replace("\"checkpointRevision\":0", "\"checkpointRevision\":9223372036854775808", StringComparison.Ordinal),
            "closed-value" => canonical.Replace("\"kind\":\"complete\"", "\"kind\":\"future\"", StringComparison.Ordinal),
            "unpaired-surrogate" => canonical.Replace("\"campaign.test\"", "\"\\uD800\"", StringComparison.Ordinal),
            _ => throw new InvalidOperationException(),
        };

        var parsed = CampaignStateJson.Parse(Encoding.UTF8.GetBytes(mutated));
        Assert.False(parsed.IsValid);
        Assert.Equal(expected, parsed.FailureCode);
    }

    [Fact]
    public void Over_bounded_work_collection_fails_before_item_projection()
    {
        var canonical = Encoding.UTF8.GetString(CampaignStateJson.Write(CreateState()));
        var oversized = canonical.Replace(
            "\"workItems\":[]",
            "\"workItems\":[" + string.Join(',', Enumerable.Repeat("null", CampaignStateContract.MaximumWorkItems + 1)) + "]",
            StringComparison.Ordinal);

        Assert.Equal(
            CampaignStateValidationCode.InvalidBound,
            CampaignStateJson.Parse(Encoding.UTF8.GetBytes(oversized)).FailureCode);
    }

    [Fact]
    public void Byte_transport_rejects_bom_invalid_utf8_and_oversize_before_projection()
    {
        var canonical = CampaignStateJson.Write(CreateState());
        Assert.Equal(
            CampaignStateValidationCode.BomNotAllowed,
            CampaignStateJson.Parse(new byte[] { 0xef, 0xbb, 0xbf }.Concat(canonical).ToArray()).FailureCode);
        Assert.Equal(
            CampaignStateValidationCode.InvalidUtf8,
            CampaignStateJson.Parse(new byte[] { 0x7b, 0x22, 0xc3, 0x28, 0x7d }).FailureCode);
        Assert.Equal(
            CampaignStateValidationCode.DocumentTooLarge,
            CampaignStateJson.Parse(new byte[CampaignStateContract.MaximumArtifactUtf8Bytes + 1]).FailureCode);
    }

    [Fact]
    public void Validation_diagnostics_do_not_echo_private_input()
    {
        const string marker = "PRIVATE/source/C:/secret/provider-response";
        var failure = Assert.Throws<CampaignStateValidationException>(() =>
            CampaignStateFactory.CreateValidated(
                ProductRevision(),
                marker,
                Snapshot(),
                0,
                Ceilings(),
                EmptyCharges(),
                []));

        Assert.DoesNotContain(marker, failure.Message, StringComparison.Ordinal);
        Assert.Equal(CampaignStateValidationCode.InvalidVocabulary, failure.Code);
    }

    [Fact]
    public void Charge_decomposition_is_checked_as_one_invariant()
    {
        var invalid = EmptyCharges() with
        {
            ProviderRequests = new CampaignChargeObservation(2, 3, 4),
        };

        var failure = Assert.Throws<CampaignStateValidationException>(() =>
            CampaignStateFactory.CreateValidated(
                ProductRevision(), "campaign.test", Snapshot(), 0, Ceilings(), invalid, []));
        Assert.Equal(CampaignStateValidationCode.InvalidCorrelation, failure.Code);
    }

    [Fact]
    public void Terminal_kind_reason_and_work_membership_are_one_closed_invariant()
    {
        var failure = Assert.Throws<CampaignStateValidationException>(() =>
            CampaignStateFactory.CreateValidated(
                ProductRevision(), "campaign.test", Snapshot(), 0, Ceilings(), EmptyCharges(), [],
                terminalOutcome: new CampaignTerminalOutcome(
                    CampaignTerminalKind.Complete,
                    CampaignTerminalReason.Budget)));

        Assert.Equal(CampaignStateValidationCode.InvalidShape, failure.Code);
    }

    [Fact]
    public void Predecessor_summary_is_bounded_and_cannot_invent_more_files_than_work()
    {
        var predecessor = new CampaignPredecessorSummary(
            ProductRevision(),
            Snapshot() with { OpaqueSnapshotBinding = "snapshot.previous" },
            Hash('7'),
            2,
            Hash('8'),
            CampaignTerminalKind.Complete,
            null,
            new CampaignPredecessorCandidateSummary(1, 2, 0, 0, 0, 0, null, null));

        var failure = Assert.Throws<CampaignStateValidationException>(() =>
            CampaignStateFactory.CreateValidated(
                ProductRevision(), "campaign.test", Snapshot(), 0, Ceilings(), EmptyCharges(), [],
                terminalOutcome: new CampaignTerminalOutcome(
                    CampaignTerminalKind.Complete,
                    CampaignTerminalReason.NoWork),
                predecessor: predecessor));
        Assert.Equal(CampaignStateValidationCode.InvalidCorrelation, failure.Code);
    }

    [Fact]
    public void Predecessor_requires_distinct_execution_and_a_closed_candidate_presence_matrix()
    {
        var priorSnapshot = Snapshot() with
        {
            OpaqueSnapshotBinding = "snapshot.previous",
            ExecutionCommitmentSha256 = Hash('6'),
        };
        var zero = new CampaignPredecessorSummary(
            ProductRevision(),
            priorSnapshot,
            Hash('7'),
            2,
            Hash('8'),
            CampaignTerminalKind.Complete,
            null,
            new CampaignPredecessorCandidateSummary(0, 0, 0, 0, 0, 0, null, null));
        var state = CampaignStateFactory.CreateValidated(
            ProductRevision(), "campaign.test", Snapshot(), 0, Ceilings(), EmptyCharges(), [],
            terminalOutcome: new CampaignTerminalOutcome(CampaignTerminalKind.Complete, CampaignTerminalReason.NoWork),
            predecessor: zero);
        Assert.True(CampaignStateJson.Parse(CampaignStateJson.Write(state)).IsValid);

        var sameExecution = zero with
        {
            Snapshot = priorSnapshot with { ExecutionCommitmentSha256 = Snapshot().ExecutionCommitmentSha256 },
        };
        AssertInvalidCorrelation(() => CampaignStateFactory.CreateValidated(
            ProductRevision(), "campaign.test", Snapshot(), 0, Ceilings(), EmptyCharges(), [],
            terminalOutcome: new CampaignTerminalOutcome(CampaignTerminalKind.Complete, CampaignTerminalReason.NoWork),
            predecessor: sameExecution));

        AssertMutationFailure(
            state,
            root =>
            {
                root["predecessor"]!["candidate"]!["patchRequestSha256"] = Hash('9');
                root["predecessor"]!["candidate"]!["patchResultCommitmentSha256"] = Hash('a');
            },
            CampaignStateValidationCode.InvalidShape);
        AssertMutationFailure(
            state,
            root =>
            {
                root["predecessor"]!["candidate"]!["acceptedCount"] = 1;
                root["predecessor"]!["candidate"]!["patchRequestSha256"] = Hash('9');
                root["predecessor"]!["candidate"]!["patchResultCommitmentSha256"] = Hash('a');
            },
            CampaignStateValidationCode.InvalidShape);
    }

    [Fact]
    public void Patch_state_producer_facts_are_factory_derived_and_not_publicly_constructible()
    {
        Assert.DoesNotContain(typeof(CampaignPatchReservation).GetConstructors(), constructor => constructor.IsPublic);
        Assert.DoesNotContain(typeof(CampaignCandidateObservation).GetConstructors(), constructor => constructor.IsPublic);
        Assert.DoesNotContain(typeof(CampaignCumulativeOutcome).GetConstructors(), constructor => constructor.IsPublic);

        var proposalState = CreateProposalCompleteState();
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var exchange = CreateScribeExchange(work);
        var request = CampaignStateFactory.ReconstructPatchRequest(
            proposalState,
            PatchContext(exchange.Request),
            CurrentEvidence(exchange));
        var reservation = CampaignStateFactory.CreatePatchReservation(
            proposalState,
            request,
            patchAttemptCount: 2,
            elapsedMilliseconds: 17);
        Assert.Equal(request.ArtifactSha256, reservation.PatchRequestSha256);
        Assert.Equal(proposalState.CheckpointRevision, reservation.ExpectedCheckpointRevision);

        var proposal = Assert.IsType<CampaignTrustedProposal>(proposalState.WorkItems[0].TrustedProposal);
        var reservedState = WithState(
            proposalState,
            proposalState.WorkItems,
            reservation);
        var completion = CreateAcceptedCompletion(reservedState, request, proposal);
        Assert.Equal(request.Blocks.Select(block => block.BlockId), completion.CandidateObservation!.AcceptedWorkItemKeys);
        Assert.Equal(request.ArtifactSha256, completion.CumulativeOutcome.PatchRequestSha256);
        Assert.Equal(
            completion.CandidateObservation.PatchResultCommitmentSha256,
            completion.CumulativeOutcome.PatchResultCommitmentSha256);
        AssertInvalidCorrelation(() => CampaignStateFactory.CreateHostPatchOutcome(
            proposalState,
            request,
            CampaignCumulativeOutcomeKind.Timeout));
        var timeout = CampaignStateFactory.CreateHostPatchOutcome(
            reservedState,
            request,
            CampaignCumulativeOutcomeKind.Timeout);
        Assert.Equal(request.ArtifactSha256, timeout.PatchRequestSha256);
        Assert.Equal(reservedState.CheckpointRevision, timeout.CompletedFromCheckpointRevision);
        Assert.Null(timeout.PatchResultCommitmentSha256);
    }

    [Fact]
    public void Patch_factory_family_enforces_input_identity_at_every_state_bound_request_seam()
    {
        var proposalState = CreateProposalCompleteState();
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var exchange = CreateScribeExchange(work);
        var request = CampaignStateFactory.ReconstructPatchRequest(
            proposalState,
            PatchContext(exchange.Request),
            CurrentEvidence(exchange));
        var proposal = Assert.IsType<CampaignTrustedProposal>(proposalState.WorkItems[0].TrustedProposal);
        var foreignExchange = CreateFreshContextExchange(
            work,
            "repoctx-33333333333333333333333333333333",
            "samples/Alternate.csproj");
        var foreign = RebindPatchRequest(request, PatchContext(foreignExchange.Request));

        AssertInvalidCorrelation(() => CampaignStateFactory.CreatePatchReservation(
            proposalState,
            foreign,
            patchAttemptCount: 1,
            elapsedMilliseconds: 0));

        var foreignReserved = MutateValidState(proposalState, root =>
            root["activeReservation"] = new JsonObject
            {
                ["kind"] = "patch",
                ["patchRequestSha256"] = foreign.ArtifactSha256,
                ["expectedCheckpointRevision"] = proposalState.CheckpointRevision,
                ["patchAttemptCount"] = 1,
                ["elapsedMilliseconds"] = 0,
            });
        AssertInvalidCorrelation(() => CampaignStateFactory.CreatePatchCompletion(
            foreignReserved,
            foreign,
            CreateAcceptedPatchResult(foreign, proposal)));
        foreach (var kind in new[]
        {
            CampaignCumulativeOutcomeKind.HostFailure,
            CampaignCumulativeOutcomeKind.Cancelled,
            CampaignCumulativeOutcomeKind.Timeout,
        })
        {
            AssertInvalidCorrelation(() => CampaignStateFactory.CreateHostPatchOutcome(
                foreignReserved,
                foreign,
                kind));
        }

        var accepted = CreateAcceptedCandidateScenario();
        var foreignAcceptedExchange = CreateFreshContextExchange(
            work,
            "repoctx-44444444444444444444444444444444",
            "samples/Alternate.sln");
        var foreignAccepted = RebindPatchRequest(
            accepted.Request,
            PatchContext(foreignAcceptedExchange.Request));
        AssertInvalidCorrelation(() => CampaignStateFactory.CreatePatchReservation(
            accepted.State,
            foreignAccepted,
            patchAttemptCount: 1,
            elapsedMilliseconds: 0));

        var freshExchange = CreateFreshContextExchange(
            work,
            "repoctx-55555555555555555555555555555555");
        var fresh = RebindPatchRequest(request, PatchContext(freshExchange.Request));
        var freshReserved = WithState(
            proposalState,
            proposalState.WorkItems,
            CampaignStateFactory.CreatePatchReservation(
                proposalState,
                fresh,
                patchAttemptCount: 1,
                elapsedMilliseconds: 0));
        var completion = CampaignStateFactory.CreatePatchCompletion(
            freshReserved,
            fresh,
            CreateAcceptedPatchResult(fresh, proposal));
        Assert.Equal(fresh.ArtifactSha256, completion.CumulativeOutcome.PatchRequestSha256);
        foreach (var kind in new[]
        {
            CampaignCumulativeOutcomeKind.HostFailure,
            CampaignCumulativeOutcomeKind.Cancelled,
            CampaignCumulativeOutcomeKind.Timeout,
        })
        {
            Assert.Equal(
                fresh.ArtifactSha256,
                CampaignStateFactory.CreateHostPatchOutcome(freshReserved, fresh, kind).PatchRequestSha256);
        }
    }

    [Fact]
    public void Work_item_collection_stops_at_the_contract_cap_before_validation_or_unbounded_materialization()
    {
        var scenario = CreateProposalScenario();
        var enumerated = 0;

        IEnumerable<CampaignWorkItemState> OverBound()
        {
            for (var index = 0; index <= CampaignStateContract.MaximumWorkItems; index++)
            {
                enumerated++;
                yield return scenario.InitialState.WorkItems[0];
            }

            throw new InvalidOperationException("The capped collector enumerated past the first over-bound item.");
        }

        AssertInvalidBound(() => CampaignStateFactory.CreateValidated(
            scenario.InitialState.ProductRevision,
            scenario.InitialState.CampaignLineage,
            scenario.InitialState.Snapshot,
            scenario.InitialState.CheckpointRevision,
            scenario.InitialState.ConfiguredCeilings,
            scenario.InitialState.LineageCharges,
            OverBound()));
        Assert.Equal(CampaignStateContract.MaximumWorkItems + 1, enumerated);
    }

    [Fact]
    public void Published_schema_and_runtime_share_primitive_boundary_domains()
    {
        AssertSchemaRuntimeBoundary(
            CreateState(),
            root => root["checkpointRevision"] = CampaignStateContract.MaximumObservation,
            root => root["checkpointRevision"] = CampaignStateContract.MaximumObservation + 1);
        AssertSchemaRuntimeBoundary(
            CreateState(),
            root => root["configuredCeilings"]!["campaignBudget"]!["maximumInputTokens"] =
                CampaignStateContract.MaximumCampaignInputTokens,
            root => root["configuredCeilings"]!["campaignBudget"]!["maximumInputTokens"] =
                CampaignStateContract.MaximumCampaignInputTokens + 1);
        AssertSchemaRuntimeBoundary(
            CreateState(),
            root => root["configuredCeilings"]!["scribeRunLimits"]!["maximumOutputTokens"] =
                DocumentationScribeContract.MaximumConfiguredOutputTokens,
            root => root["configuredCeilings"]!["scribeRunLimits"]!["maximumOutputTokens"] =
                DocumentationScribeContract.MaximumConfiguredOutputTokens + 1);
        AssertSchemaRuntimeBoundary(
            CreateState(),
            root => root["configuredCeilings"]!["scribeRunLimits"]!["maximumElapsedMilliseconds"] =
                DocumentationScribeContract.MaximumConfiguredElapsedMilliseconds,
            root => root["configuredCeilings"]!["scribeRunLimits"]!["maximumElapsedMilliseconds"] =
                DocumentationScribeContract.MaximumConfiguredElapsedMilliseconds + 1);

        var accepted = CreateAcceptedCandidateScenario().State;
        AssertSchemaAndRuntimeReject(accepted, root =>
            root["candidateObservation"]!["changedFiles"]![0]!["path"] = "C:/alternate.cs");
        var proposal = CreateProposalCompleteState();
        AssertSchemaAndRuntimeReject(proposal, root =>
            root["workItems"]![0]!["trustedProposal"]!["historicalAttemptId"] = "attempt.arbitrary");
        AssertSchemaAndRuntimeReject(proposal, root =>
            root["workItems"]![0]!["trustedProposal"]!["evidence"]![0]!["claimCategoryIds"] =
                new JsonArray());
        AssertSchemaAndRuntimeReject(proposal, root =>
            root["workItems"]![0]!["trustedProposal"]!["evidence"]![0]!["evidenceReferenceId"] =
                "Evidence.Invalid");
        AssertSchemaAndRuntimeReject(proposal, root =>
            root["workItems"]![0]!["trustedProposal"]!["evidence"]![0]!["authority"] =
                "authority.test");
        AssertSchemaAndRuntimeReject(proposal, root =>
            root["workItems"]![0]!["trustedProposal"]!["evidence"]![0]!["subject"]!["identity"] =
                "parameter/" + new string('1', 129));
        AssertSchemaAndRuntimeReject(proposal, root =>
        {
            var claims = new JsonArray();
            foreach (var index in Enumerable.Range(0, 65))
            {
                claims.Add($"claim.{index:D2}");
            }

            root["workItems"]![0]!["trustedProposal"]!["evidence"]![0]!["claimCategoryIds"] = claims;
        });
        AssertSchemaAndRuntimeReject(proposal, root =>
            root["workItems"]![0]!["trustedProposal"]!["evidence"]![0]!["locator"] = new JsonObject
            {
                ["kind"] = "generated",
                ["producerKind"] = "sourceGenerator",
                ["producerId"] = "tgp." + Hash('a'),
                ["outputId"] = "tgo." + Hash('b'),
                ["sourceSha256"] = Hash('c'),
                ["span"] = new JsonObject { ["start"] = 1, ["end"] = 1 },
            });

        var invalidRepositoryPaths = new[]
        {
            "/root.cs",
            "C:/drive.cs",
            ".",
            "..",
            "./relative.cs",
            "evidence/.",
            "evidence/..",
            "evidence/../relative.cs",
            "evidence/line\nbreak/../relative.cs",
            "evidence\\file.cs",
            "evidence/\0file.cs",
            "evidence//file.cs",
            "evidence//\n",
            "evidence/line\nbreak//file.cs",
            "evidence/line\nbreak/",
            "evidence/",
        };
        Assert.All(invalidRepositoryPaths, path => AssertSchemaAndRuntimeReject(proposal, root =>
            root["workItems"]![0]!["trustedProposal"]!["evidence"]![0]!["locator"]!["path"] = path));

        var reversedSpan = Assert.IsType<JsonObject>(JsonNode.Parse(CampaignStateJson.Write(proposal)));
        var span = reversedSpan["workItems"]![0]!["trustedProposal"]!["evidence"]![0]!["locator"]!["span"]!;
        span["start"] = 2;
        span["end"] = 1;
        using (var document = JsonDocument.Parse(reversedSpan.ToJsonString()))
        {
            Assert.True(EvaluateCampaignSchema(document.RootElement).IsValid);
        }

        Assert.False(CampaignStateJson.Parse(
            Encoding.UTF8.GetBytes(reversedSpan.ToJsonString() + "\n")).IsValid);
    }

    [Fact]
    public void Published_schema_and_runtime_share_documentation_id_domain_across_proposal_channels()
    {
        var proposal = CreateProposalCompleteState();
        var invalidDocumentationIds = new[]
        {
            "M:A\uFFFE",
            "M:A\uFFFF",
            "M:A\n",
        };
        Action<JsonObject, string>[] channelMutations =
        [
            (root, value) =>
            {
                var evidence = root["workItems"]![0]!["trustedProposal"]!["evidence"]!.AsArray();
                var subject = evidence
                    .Select(item => item!["subject"]!)
                    .Single(item => item["kind"]!.GetValue<string>() == "target");
                subject["parentSymbolRef"]!["documentationCommentId"] = value;
            },
            (root, value) =>
            {
                var evidence = root["workItems"]![0]!["trustedProposal"]!["evidence"]!.AsArray();
                var subject = evidence
                    .Select(item => item!["subject"]!)
                    .First(item => item["kind"]!.GetValue<string>() == "component");
                subject["parentSymbolRef"]!["documentationCommentId"] = value;
            },
            (root, value) =>
                root["workItems"]![0]!["trustedProposal"]!["evidence"]![0]!["locator"] = new JsonObject
                {
                    ["kind"] = "metadata",
                    ["assemblyIdentity"] = "synthetic.v1",
                    ["documentationCommentId"] = value,
                },
            (root, value) =>
                root["workItems"]![0]!["trustedProposal"]!["patchBlock"]!["symbolRef"]!["documentationCommentId"] = value,
        ];

        Assert.All(invalidDocumentationIds, value =>
            Assert.All(channelMutations, mutate =>
                AssertSchemaAndRuntimeReject(proposal, root => mutate(root, value))));
    }

    [Theory]
    [InlineData("\uFFFD")]
    [InlineData("\U0001F600")]
    public void Documentation_id_xml_scalar_boundaries_round_trip_through_every_proposal_channel(string suffix)
    {
        var documentationId = "M:Synthetic.Widget.Run(System.String)" + suffix;
        var scenario = CreateProposalScenario(documentationId);
        var work = scenario.Plan.WorkItems.Single(item =>
            Assert.Single(item.Targets).SymbolRef.DocumentationCommentId == documentationId);
        var exchange = CreateScribeExchange(
            work,
            requestMutation: root =>
                root["evidenceReferences"]![0]!["locator"] = new JsonObject
                {
                    ["metadata"] = new JsonObject
                    {
                        ["assemblyIdentity"] = "synthetic.v1",
                        ["documentationCommentId"] = documentationId,
                    },
                });
        var proposal = AdmitProposal(
            scenario,
            WithState(
                scenario.InitialState,
                scenario.InitialState.WorkItems,
                ProviderReservation(work.WorkItemKey, exchange)),
            work,
            exchange);
        var complete = WithState(
            scenario.InitialState,
            ReplaceWork(
                scenario.InitialState,
                work.WorkItemKey,
                CampaignWorkStatus.ProposalComplete,
                proposal),
            activeReservation: null);
        var artifact = CampaignStateJson.CreateArtifact(complete);

        using var document = JsonDocument.Parse(artifact.ExactUtf8Json.AsMemory());
        var evaluation = EvaluateCampaignSchema(document.RootElement);
        Assert.True(evaluation.IsValid, DescribeSchemaFailures(evaluation));
        Assert.True(CampaignStateJson.Parse(artifact.ExactUtf8Json.AsMemory()).IsValid);

        var root = Assert.IsType<JsonObject>(JsonNode.Parse(artifact.ExactUtf8Json.AsSpan()));
        var trustedProposal = root["workItems"]!
            .AsArray()
            .Single(item => item!["trustedProposal"] is not null)!["trustedProposal"]!;
        Assert.Equal(
            documentationId,
            trustedProposal["patchBlock"]!["symbolRef"]!["documentationCommentId"]!.GetValue<string>());
        Assert.All(
            trustedProposal["evidence"]!
                .AsArray()
                .Select(item => item!["subject"]!["symbolRef"] ?? item!["subject"]!["parentSymbolRef"]),
            symbol => Assert.Equal(
                documentationId,
                symbol!["documentationCommentId"]!.GetValue<string>()));
        Assert.Equal(
            documentationId,
            trustedProposal["evidence"]![0]!["locator"]!["documentationCommentId"]!.GetValue<string>());
    }

    [Fact]
    public void Published_schema_and_runtime_share_exception_type_documentation_id_domain()
    {
        var proposal = CreateProposalCompleteStateWithException("T:System.Exception");
        var invalidDocumentationIds = new[]
        {
            "M:WrongPrefix",
            "T:A\u0001",
            "T:A\t",
            "T:A\n",
            "T:A ",
            "T:A\u0085",
            "T:A\u00A0",
            "T:A\u1680",
            "T:A\u2000",
            "T:A\u200A",
            "T:A\u2028",
            "T:A\u2029",
            "T:A\u202F",
            "T:A\u205F",
            "T:A\u3000",
            "T:A\uFFFE",
            "T:A\uFFFF",
            "T:A<",
            "T:A>",
            "T:A&",
            "T:A\"",
            "T:A'",
        };

        Assert.All(invalidDocumentationIds, value =>
            AssertSchemaAndRuntimeReject(proposal, root =>
                root["workItems"]![0]!["trustedProposal"]!["patchBlock"]!["content"]!["exceptions"]![0]!["typeDocumentationId"] = value));
    }

    [Theory]
    [InlineData("T:System.Exception")]
    [InlineData("T:Example\uFFFD")]
    [InlineData("T:Example\u200B")]
    [InlineData("T:Example\U0001F600")]
    public void Exception_type_documentation_id_boundaries_round_trip_through_production_proposal(string value)
    {
        var state = CreateProposalCompleteStateWithException(value);
        var artifact = CampaignStateJson.CreateArtifact(state);

        using var document = JsonDocument.Parse(artifact.ExactUtf8Json.AsMemory());
        var evaluation = EvaluateCampaignSchema(document.RootElement);
        Assert.True(evaluation.IsValid, DescribeSchemaFailures(evaluation));
        Assert.True(CampaignStateJson.Parse(artifact.ExactUtf8Json.AsMemory()).IsValid);

        var root = Assert.IsType<JsonObject>(JsonNode.Parse(artifact.ExactUtf8Json.AsSpan()));
        var trustedProposal = root["workItems"]!
            .AsArray()
            .Single(item => item!["trustedProposal"] is not null)!["trustedProposal"]!;
        Assert.Equal(
            value,
            trustedProposal["patchBlock"]!["content"]!["exceptions"]![0]!["typeDocumentationId"]!.GetValue<string>());
    }

    [Fact]
    public void Stable_m3_evidence_projection_is_shared_by_producer_runtime_and_schema()
    {
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var astralPath = "evidence/" + string.Concat(Enumerable.Repeat("\U0001F600", 300)) + ".txt";
        Assert.True(astralPath.EnumerateRunes().Count() <= 512);
        Assert.True(astralPath.Length > 512);
        var variants = new Action<JsonObject>[]
        {
            root =>
            {
                var span = root["evidenceReferences"]![0]!["locator"]!["repository"]!["span"]!;
                span["end"] = span["start"]!.GetValue<int>();
            },
            root => root["evidenceReferences"]![0]!["locator"]!["repository"]!["path"] = astralPath,
            root => root["evidenceReferences"]![0]!["locator"] = new JsonObject
            {
                ["metadata"] = new JsonObject
                {
                    ["assemblyIdentity"] = "synthetic.v1",
                    ["documentationCommentId"] = "M:Synthetic.Widget.Run(System.String)",
                },
            },
            root => root["evidenceReferences"]![0]!["locator"] = new JsonObject
            {
                ["generatedOutput"] = new JsonObject
                {
                    ["producerKind"] = "source-generator",
                    ["producerId"] = "sgp." + Hash('a'),
                    ["outputId"] = "sgo." + Hash('b'),
                    ["sourceSha256"] = Hash('c'),
                    ["span"] = new JsonObject { ["start"] = 1, ["end"] = 1 },
                },
            },
            root => root["evidenceReferences"]![0]!["locator"] = new JsonObject
            {
                ["synthetic"] = new JsonObject { ["fixtureId"] = "synthetic.v1" },
            },
            root => root["evidenceReferences"]![0]!["locator"]!["repository"]!["path"] =
                "evidence/line\nbreak.cs",
            root => root["evidenceReferences"]![0]!["locator"]!["repository"]!["path"] =
                "evidence/\n",
            root => root["evidenceReferences"]![0]!["locator"]!["repository"]!["path"] =
                "evidence/.\n",
            root => root["evidenceReferences"]![0]!["locator"]!["repository"]!["path"] =
                "evidence/..\n",
            root => root["evidenceReferences"]![0]!["locator"]!["repository"]!["path"] =
                "evidence/.\n/next.cs",
            root => root["evidenceReferences"]![0]!["locator"]!["repository"]!["path"] =
                "evidence/..\n/next.cs",
            root => root["evidenceReferences"]![0]!["locator"]!["repository"]!["path"] =
                "evidence/carriage\rreturn.cs",
            root => root["evidenceReferences"]![0]!["locator"]!["repository"]!["path"] =
                "evidence/tab\tpath.cs",
            root => root["evidenceReferences"]![0]!["locator"]!["repository"]!["path"] =
                "evidence/line\u2028separator.cs",
            root => root["evidenceReferences"]![0]!["locator"]!["repository"]!["path"] =
                "evidence/paragraph\u2029separator.cs",
            root => root["evidenceReferences"]![0]!["locator"]!["repository"]!["path"] =
                "evidence/name:part.cs",
        };

        Assert.All(variants, mutation =>
        {
            var exchange = CreateScribeExchange(work, requestMutation: mutation);
            var proposal = AdmitProposal(
                scenario,
                WithState(
                    scenario.InitialState,
                    scenario.InitialState.WorkItems,
                    ProviderReservation(work.WorkItemKey, exchange)),
                work,
                exchange);
            var complete = WithState(
                scenario.InitialState,
                ReplaceWork(
                    scenario.InitialState,
                    work.WorkItemKey,
                    CampaignWorkStatus.ProposalComplete,
                    proposal),
                activeReservation: null);
            var artifact = CampaignStateJson.CreateArtifact(complete);
            using var document = JsonDocument.Parse(artifact.ExactUtf8Json.AsMemory());
            Assert.True(EvaluateCampaignSchema(document.RootElement).IsValid);
            Assert.True(CampaignStateJson.Parse(artifact.ExactUtf8Json.AsMemory()).IsValid);
        });

        var reversedRequest = ReadJsonFixture("documentation-scribe", "v1", "valid", "request.json");
        var reversedRequestSpan = reversedRequest["evidenceReferences"]![0]!["locator"]!["repository"]!["span"]!;
        reversedRequestSpan["start"] = 2;
        reversedRequestSpan["end"] = 1;
        Assert.NotNull(DocumentationScribeValidation.ParseRequest(
            Encoding.UTF8.GetBytes(reversedRequest.ToJsonString())).Failure);
    }

    [Fact]
    public void Core_checkpoint_contract_has_no_host_or_execution_layer_reference()
    {
        var references = typeof(CampaignStateContract).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("ContractScribe.Agent", references);
        Assert.DoesNotContain("ContractScribe.Patching", references);
        Assert.DoesNotContain("ContractScribe.Roslyn", references);
        Assert.DoesNotContain("Octokit", references);
    }

    private static ProposalScenario CreateProposalScenario(
        string firstDocumentationId = "M:Synthetic.Widget.Run(System.String)",
        string costCurrency = "USD",
        bool costEnforced = true,
        long maximumElapsedMilliseconds = 120_000,
        long maximumPatchBytes = 1_000_000,
        int maximumBlocks = 100,
        int maximumProviderRequests = 100)
    {
        const string Context = "synthetic.v1";
        var specifications = new[]
        {
            new TargetSpecification(
                firstDocumentationId,
                "src/Synthetic/Widget.cs",
                "public void Run(string value) { }",
                "decl.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            new TargetSpecification(
                "M:Synthetic.Widget.Stop(System.String)",
                "src/Synthetic/Widget.Stop.cs",
                "public void Stop(string value) { }",
                "decl.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
        };
        var classificationsBuffer = new ClassificationCandidateBuffer();
        foreach (var specification in specifications)
        {
            classificationsBuffer.AddTarget(
                Context,
                specification.DocumentationId,
                PrimarySymbolKind.Method,
                ImmutableArray<SymbolTrait>.Empty,
                ClassificationOrigin.Source,
                [ClassificationInput.RepositoryLocator(
                    specification.Path,
                    0,
                    specification.DeclarationText.Length)]);
            classificationsBuffer.AddComponent(
                Context,
                specification.DocumentationId,
                ComponentKind.Parameter,
                "parameter/0",
                ClassificationOrigin.Source);
            classificationsBuffer.AddComponent(
                Context,
                specification.DocumentationId,
                ComponentKind.Return,
                "return",
                ClassificationOrigin.Source);
        }

        var classifications = Assert.IsType<ClassificationSet>(
            classificationsBuffer.Normalize(TargetProfile.ExternalApi).ClassificationSet);
        var declarationSets = specifications.ToDictionary(
            specification => specification.DocumentationId,
            specification => new DeclarationSet(
                DocumentationObservationInput.RepositoryDeclaration(
                    specification.DeclarationId,
                    DocumentationAuthorityRole.Ordinary,
                    "project.synthetic",
                    specification.Path,
                    Sha256(specification.DeclarationText),
                    DocumentationObservationInput.Span(0, specification.DeclarationText.Length),
                    specification.DeclarationText,
                    DocumentationObservationInput.Span(0, 0),
                    string.Empty,
                    documentationSpan: null,
                    documentationText: null,
                    DocumentationBlockState.NoBlock,
                    parentSubstantive: false),
                DocumentationObservationInput.RepositoryDeclaration(
                    specification.DeclarationId,
                    DocumentationAuthorityRole.Ordinary,
                    "project.synthetic",
                    specification.Path,
                    Sha256(specification.DeclarationText),
                    DocumentationObservationInput.Span(0, specification.DeclarationText.Length),
                    specification.DeclarationText,
                    DocumentationObservationInput.Span(0, 0),
                    string.Empty,
                    documentationSpan: null,
                    documentationText: null,
                    DocumentationBlockState.NoBlock,
                    parentSubstantive: false,
                    componentLocalName: "value",
                    componentMatch: DocumentationComponentMatch.Absent),
                DocumentationObservationInput.RepositoryDeclaration(
                    specification.DeclarationId,
                    DocumentationAuthorityRole.Ordinary,
                    "project.synthetic",
                    specification.Path,
                    Sha256(specification.DeclarationText),
                    DocumentationObservationInput.Span(0, specification.DeclarationText.Length),
                    specification.DeclarationText,
                    DocumentationObservationInput.Span(0, 0),
                    string.Empty,
                    documentationSpan: null,
                    documentationText: null,
                    DocumentationBlockState.NoBlock,
                    parentSubstantive: false,
                    componentLocalName: null,
                    componentMatch: DocumentationComponentMatch.Absent)),
            StringComparer.Ordinal);
        var observationBuffer = new DocumentationObservationCandidateBuffer(classifications);
        foreach (var specification in specifications)
        {
            var target = classifications.Targets.Single(value =>
                string.Equals(value.SymbolRef.DocumentationCommentId, specification.DocumentationId, StringComparison.Ordinal));
            var components = classifications.Components.Where(value =>
                value.ParentSymbolRef == target.SymbolRef).ToImmutableArray();
            var declarations = declarationSets[specification.DocumentationId];
            observationBuffer.AddTarget(target, true, [declarations.Target]);
            observationBuffer.AddComponent(
                components.Single(value => value.ComponentKind == ComponentKind.Parameter),
                true,
                [declarations.Parameter]);
            observationBuffer.AddComponent(
                components.Single(value => value.ComponentKind == ComponentKind.Return),
                true,
                [declarations.Return]);
        }

        var observations = Assert.IsType<DocumentationObservationSet>(
            observationBuffer.Normalize().ObservationSet);
        var policy = ParseRequiredPolicy();
        var contribution = Assert.IsType<PolicyContributionSet>(
            PolicyConfigurationEvaluator.Evaluate(
                policy,
                specifications.Select(specification =>
                    PolicyConfigurationInput.Repository("src/Synthetic.csproj", specification.Path))).ContributionSet);
        var auditInputs = ImmutableArray.CreateBuilder<AuditRecordInput>();
        var evidenceAuthority = ImmutableArray.CreateBuilder<CampaignPlanningEvidenceAuthority>();
        foreach (var specification in specifications)
        {
            var target = classifications.Targets.Single(value =>
                string.Equals(value.SymbolRef.DocumentationCommentId, specification.DocumentationId, StringComparison.Ordinal));
            var components = classifications.Components.Where(value =>
                value.ParentSymbolRef == target.SymbolRef).ToImmutableArray();
            var declarations = declarationSets[specification.DocumentationId];
            var targetObservation = observations.Observations.Single(value =>
                value.Subject.ParentSymbolRef == target.SymbolRef && value.Subject.ComponentKind is null);
            var targetBinding = BindEvidence(
                targetObservation,
                declarations.Target,
                EvidenceInput.TargetSubject(Context, specification.DocumentationId),
                specification.Path);
            auditInputs.Add(AuditInput.Target(target, contribution, targetBinding));
            evidenceAuthority.Add(new CampaignPlanningEvidenceAuthority(targetObservation, targetBinding));
            foreach (var component in components)
            {
                var declaration = component.ComponentKind == ComponentKind.Parameter
                    ? declarations.Parameter
                    : declarations.Return;
                var componentObservation = observations.Observations.Single(value =>
                    value.Subject.ParentSymbolRef == target.SymbolRef
                    && value.Subject.ComponentKind == component.ComponentKind);
                var componentBinding = BindEvidence(
                    componentObservation,
                    declaration,
                    EvidenceInput.ComponentSubject(
                        Context,
                        specification.DocumentationId,
                        component.ComponentKind,
                        component.Identity),
                    specification.Path);
                auditInputs.Add(AuditInput.Component(component, contribution, componentBinding));
                evidenceAuthority.Add(new CampaignPlanningEvidenceAuthority(componentObservation, componentBinding));
            }
        }

        var audit = AuditAggregator.Aggregate(
            TargetProfile.ExternalApi,
            classifications,
            policy,
            auditInputs);
        var styleProfile = ReadScribeRequest().StyleProfile;
        var targetAuthorities = specifications.Select(specification =>
        {
            var target = classifications.Targets.Single(value =>
                string.Equals(value.SymbolRef.DocumentationCommentId, specification.DocumentationId, StringComparison.Ordinal));
            var source = new CampaignPlanningRepositorySourceAuthority(
                specification.Path,
                Sha256("physical:" + specification.Path),
                Sha256(specification.DeclarationText),
                specification.DeclarationId,
                Sha256(specification.DeclarationText),
                DocumentationPatchRepositoryEncoding.Utf8,
                DocumentationObservationInput.Span(0, specification.DeclarationText.Length),
                DocumentationObservationInput.Span(0, specification.DeclarationText.Length),
                DocumentationObservationInput.Span(0, specification.DeclarationText.Length),
                DocumentationObservationInput.Span(0, specification.DeclarationText.Length),
                documentationSpan: null,
                DocumentationBlockState.NoBlock);
            return new CampaignPlanningTargetAuthority(
                target,
                source,
                [
                    new CampaignPlanningApplicableComponent(ComponentKind.Parameter, "parameter/0", "value"),
                    new CampaignPlanningApplicableComponent(ComponentKind.Return, "return", null),
                ],
                [target.SymbolRef],
                multiDeclarator: false,
                primaryConstructor: false,
                primaryConstructorAlias: false,
                styleProfile);
        }).ToImmutableArray();
        var snapshot = new CampaignPlanningSnapshot(
            "campaign.synthetic",
            "snapshot.first",
            Hash('1'),
            Hash('2'),
            Hash('3'),
            TargetProfile.ExternalApi);
        var requestTemplate = ReadScribeRequest();
        var executionPolicy = new CampaignPlanningExecutionPolicy(
            requestTemplate.Limits,
            new CampaignPlanningBudgetPolicy(
                maximumBlocks,
                20,
                maximumPatchBytes,
                maximumProviderRequests,
                3,
                1_000_000,
                500_000,
                100_000,
                5_000_000,
                maximumElapsedMilliseconds,
                8,
                costEnforced,
                costEnforced ? costCurrency : null,
                costEnforced
                    ? Content(CampaignPlanningContentFamily.CostRatePolicy, "cost", "rates-v1")
                    : null),
            Content(CampaignPlanningContentFamily.ProposalContract, "proposal", "proposal-v1"),
            Content(CampaignPlanningContentFamily.AgentProtocol, "agent", "agent-v1"),
            Content(CampaignPlanningContentFamily.ContextSelectionPolicy, "context", "context-v1"),
            Content(CampaignPlanningContentFamily.ToolPolicyAndRegistry, "tools", "tools-v1"),
            Content(CampaignPlanningContentFamily.ProviderModelRequestProfile, "provider", "provider-v1"),
            Content(CampaignPlanningContentFamily.RetryPolicy, "retry", "retry-v1"),
            Content(CampaignPlanningContentFamily.M2ProjectionPolicy, "m2", "m2-v1"),
            Content(CampaignPlanningContentFamily.ProductContractRevision, "product", "product-v1"));
        var input = new CampaignPlanningInput(
            snapshot,
            executionPolicy,
            classifications,
            observations,
            evidenceAuthority.ToImmutable(),
            audit,
            new CampaignPlanningOwnerAuthoritySet(targetAuthorities
                .Select(target => new CampaignPlanningOwnerAuthority([target]))
                .ToImmutableArray()));
        var plan = CampaignPlanner.Plan(input);
        Assert.Equal(2, plan.WorkItems.Length);
        var styleProjection = JsonSerializer.SerializeToElement(new { style = "synthetic-v1" });
        var initial = CampaignStateFactory.CreateInitial(
            "style.synthetic",
            styleProjection,
            "samples/Synthetic.csproj",
            input,
            plan);
        return new ProposalScenario(styleProjection, input, plan, initial);
    }

    private static ScribeExchange CreateScribeExchange(
        CampaignPlanningWorkItem work,
        string inputIdentity = "samples/Synthetic.csproj",
        string attemptId = "scribe-attempt.0123456789abcdef0123456789abcdef",
        Action<JsonObject>? requestMutation = null,
        Action<JsonObject>? resultMutation = null,
        string resultFixture = "proposal-result.json")
    {
        var target = Assert.Single(work.Targets);
        var source = Assert.IsType<CampaignPlanningRepositorySourceAuthority>(target.Source);
        var requestNode = ReadJsonFixture("documentation-scribe", "v1", "valid", "request.json");
        requestNode["context"]!["inputIdentity"] = inputIdentity;
        SetSymbol(requestNode["target"]!["symbolRef"]!, target.SymbolRef);
        SetSource(requestNode["target"]!["sourceCommitment"]!, source);
        var evidenceIdSuffix = work.WorkItemKey[^8..];
        var evidenceIdMap = requestNode["evidenceReferences"]!
            .AsArray()
            .ToDictionary(
                evidence => evidence!["evidenceReferenceId"]!.GetValue<string>(),
                evidence => evidence!["evidenceReferenceId"]!.GetValue<string>() + "." + evidenceIdSuffix,
                StringComparer.Ordinal);
        foreach (var evidence in requestNode["evidenceReferences"]!.AsArray())
        {
            var originalId = evidence!["evidenceReferenceId"]!.GetValue<string>();
            evidence["evidenceReferenceId"] = evidenceIdMap[originalId];
        }
        foreach (var subject in requestNode["evidenceReferences"]!
            .AsArray()
            .Select(evidence => evidence!["subject"]!))
        {
            if (subject["parentSymbolRef"] is { } parent)
            {
                SetSymbol(parent, target.SymbolRef);
            }
            else if (subject["symbolRef"] is { } symbol)
            {
                SetSymbol(symbol, target.SymbolRef);
            }
        }

        requestMutation?.Invoke(requestNode);

        var requestParse = DocumentationScribeValidation.ParseRequest(
            Encoding.UTF8.GetBytes(requestNode.ToJsonString()));
        Assert.Null(requestParse.Failure);
        var request = Assert.IsType<DocumentationScribeRequest>(requestParse.Request);
        Assert.True(DocumentationScribeAttemptId.TryParse(attemptId, out var parsedAttempt));

        var resultNode = ReadJsonFixture("documentation-scribe", "v1", "valid", resultFixture);
        resultNode["scribeRequestSha256"] = request.ArtifactSha256;
        resultNode["attemptId"] = attemptId;
        resultNode["runEnvelope"]!["scribeRequestSha256"] = request.ArtifactSha256;
        resultNode["runEnvelope"]!["attemptId"] = attemptId;
        if (resultNode["terminal"]!["target"] is { } resultTarget)
        {
            SetSymbol(resultTarget["symbolRef"]!, target.SymbolRef);
            SetSource(resultTarget["sourceCommitment"]!, source);
        }

        if (resultNode["terminal"]!["contentUnits"] is JsonArray contentUnits)
        {
            foreach (var ids in contentUnits
                .Select(unit => unit!["evidenceReferenceIds"]!.AsArray()))
            {
                for (var index = 0; index < ids.Count; index++)
                {
                    var originalId = ids[index]!.GetValue<string>();
                    ids[index] = evidenceIdMap[originalId];
                }
            }
        }
        resultMutation?.Invoke(resultNode);
        var resultParse = DocumentationScribeValidation.ParseRunResult(
            request,
            parsedAttempt,
            [],
            Encoding.UTF8.GetBytes(resultNode.ToJsonString()));
        Assert.Null(resultParse.Failure);
        return new ScribeExchange(
            request,
            Assert.IsType<DocumentationScribeRunResult>(resultParse.Result));
    }

    private static ScribeExchange CreateFreshContextExchange(
        CampaignPlanningWorkItem work,
        string contextRef = "repoctx-22222222222222222222222222222222",
        string inputIdentity = "samples/Synthetic.csproj") =>
        CreateScribeExchange(
            work,
            inputIdentity,
            requestMutation: root =>
            {
                root["context"]!["repositoryContextRef"] = contextRef;
                foreach (var evidence in root["evidenceReferences"]!.AsArray())
                {
                    evidence!["repositoryContextRef"] = contextRef;
                }

                foreach (var contextReference in root["contextReferences"]!.AsArray())
                {
                    contextReference!["repositoryContextRef"] = contextRef;
                }
            },
            resultMutation: root => root["terminal"]!["target"]!["repositoryContextRef"] = contextRef);

    private static ScribeExchange CreateScribeExchangeWithException(
        CampaignPlanningWorkItem work,
        string typeDocumentationId)
    {
        string? exceptionEvidenceId = null;
        return CreateScribeExchange(
            work,
            requestMutation: root =>
            {
                var evidence = root["evidenceReferences"]!.AsArray();
                var summary = evidence.Single(item =>
                    item!["evidenceReferenceId"]!.GetValue<string>()
                        .StartsWith("evidence.summary.", StringComparison.Ordinal));
                var summaryId = summary!["evidenceReferenceId"]!.GetValue<string>();
                exceptionEvidenceId = "evidence.type" + summaryId["evidence.summary".Length..];
                var exception = Assert.IsType<JsonObject>(summary.DeepClone());
                exception["evidenceReferenceId"] = exceptionEvidenceId;
                exception["kind"] = "evidence.public-contract";
                exception["relation"] = "evidence.constrains";
                exception["authority"] = "authority.public-contract";
                exception["contentSha256"] = Hash('e');
                exception["claimCategoryIds"] = new JsonArray { "claim.behavior" };
                evidence.Add(exception);
            },
            resultMutation: root =>
            {
                var evidenceId = exceptionEvidenceId
                    ?? throw new InvalidOperationException("The exception evidence ID was not projected.");
                root["terminal"]!["contentUnits"]!.AsArray().Add(new JsonObject
                {
                    ["kind"] = "content.exception",
                    ["typeDocumentationId"] = typeDocumentationId,
                    ["lines"] = new JsonArray { "The operation is invalid." },
                    ["claimCategoryId"] = "claim.behavior",
                    ["evidenceReferenceIds"] = new JsonArray { evidenceId },
                });
            });
    }

    private static ScribeExchange CreateNonProposalExchange(CampaignPlanningWorkItem work)
    {
        var proposalExchange = CreateScribeExchange(work);
        var resultNode = ReadJsonFixture("documentation-scribe", "v1", "valid", "skip-result.json");
        resultNode["scribeRequestSha256"] = proposalExchange.Request.ArtifactSha256;
        resultNode["attemptId"] = proposalExchange.Result.AttemptId.Value;
        resultNode["runEnvelope"]!["scribeRequestSha256"] = proposalExchange.Request.ArtifactSha256;
        resultNode["runEnvelope"]!["attemptId"] = proposalExchange.Result.AttemptId.Value;
        resultNode["terminal"]!["evidenceReferenceIds"]![0] = proposalExchange.Request.EvidenceReferences
            .Single(evidence => evidence.EvidenceReferenceId.StartsWith("evidence.summary.", StringComparison.Ordinal))
            .EvidenceReferenceId;
        var parsed = DocumentationScribeValidation.ParseRunResult(
            proposalExchange.Request,
            proposalExchange.Result.AttemptId,
            [],
            Encoding.UTF8.GetBytes(resultNode.ToJsonString()));
        Assert.Null(parsed.Failure);
        return new ScribeExchange(
            proposalExchange.Request,
            Assert.IsType<DocumentationScribeRunResult>(parsed.Result));
    }

    private static CampaignTrustedProposal AdmitProposal(
        ProposalScenario scenario,
        CampaignCheckpointState state,
        CampaignPlanningWorkItem work,
        ScribeExchange exchange)
    {
        foreach (var evidence in exchange.Request.EvidenceReferences)
        {
            Assert.True(evidence.Subject is TargetEvidenceSubject or ComponentEvidenceSubject, evidence.EvidenceReferenceId + " subject");
            Assert.True(Enum.IsDefined(evidence.Kind), evidence.EvidenceReferenceId + " kind");
            Assert.True(Enum.IsDefined(evidence.Relation), evidence.EvidenceReferenceId + " relation");
            Assert.True(Enum.IsDefined(evidence.Authority), evidence.EvidenceReferenceId + " authority");
            Assert.True(evidence.Locator is RepositoryEvidenceLocator or MetadataEvidenceLocator or GeneratedOutputEvidenceLocator or SyntheticEvidenceLocator, evidence.EvidenceReferenceId + " locator");
        }

        return CampaignStateFactory.CreateTrustedProposal(
            state,
            new CampaignScribeExecutionAuthority(
                exchange.Result.RunEnvelope.ProviderConfigurationId,
                exchange.Result.RunEnvelope.ModelConfigurationId,
                exchange.Result.RunEnvelope.ScribeProtocolId,
                exchange.Request.ToolPolicyId),
            "style.synthetic",
            scenario.StyleProjection,
            scenario.Input,
            scenario.Plan,
            work.WorkItemKey,
            exchange.Request,
            exchange.Result);
    }

    private static CampaignProviderReservation ProviderReservation(string workItemKey, ScribeExchange exchange) =>
        new(
            workItemKey,
            exchange.Request.ArtifactSha256,
            exchange.Result.AttemptId,
            new CampaignProviderReservationExposure(0, 0, 0, 0, 0, 0));

    private static CampaignScribeExecutionAuthority ExecutionAuthority(ScribeExchange exchange) =>
        new(
            exchange.Result.RunEnvelope.ProviderConfigurationId,
            exchange.Result.RunEnvelope.ModelConfigurationId,
            exchange.Result.RunEnvelope.ScribeProtocolId,
            exchange.Request.ToolPolicyId);

    private static ImmutableArray<DocumentationScribeEvidenceReference> CurrentEvidence(
        params ScribeExchange[] exchanges)
    {
        var referenced = exchanges
            .SelectMany(exchange => Assert.IsType<DocumentationScribeProposalTerminal>(exchange.Result.Terminal)
                .ContentUnits.SelectMany(unit => unit.EvidenceReferenceIds))
            .ToHashSet(StringComparer.Ordinal);
        return exchanges
            .SelectMany(exchange => exchange.Request.EvidenceReferences.Concat(exchange.Result.DynamicEvidenceReferences))
            .Where(evidence => referenced.Contains(evidence.EvidenceReferenceId))
            .GroupBy(evidence => evidence.EvidenceReferenceId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(evidence => evidence.EvidenceReferenceId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static CampaignPatchCompletion CreateAcceptedCompletion(
        CampaignCheckpointState reservedState,
        DocumentationPatchRequest request,
        CampaignTrustedProposal proposal)
        => CampaignStateFactory.CreatePatchCompletion(
            reservedState,
            request,
            CreateAcceptedPatchResult(request, proposal));

    private static DocumentationPatchValidationResult CreateAcceptedPatchResult(
        DocumentationPatchRequest request,
        CampaignTrustedProposal proposal)
    {
        var locator = Assert.IsType<DocumentationPatchRepositoryLocator>(proposal.PatchBlock.Locator);
        var invariants = new[]
        {
            "patch.invariant.non-documentation-tokens-unchanged",
            "patch.invariant.selected-documentation-only",
            "patch.invariant.no-new-parse-diagnostics",
            "patch.invariant.symbol-semantics-unchanged",
            "patch.invariant.repository-scope",
            "patch.invariant.file-representation-preserved",
            "patch.invariant.idempotent",
            "patch.invariant.traceable",
            "patch.invariant.fail-closed",
        }.Select(id => new DocumentationPatchInvariantResult(id, DocumentationPatchInvariantStatus.Passed));
        var result = DocumentationPatchValidator.CreateResult(
            request,
            DocumentationPatchOutcome.Accepted,
            request.Blocks.Select(_ => DocumentationPatchTargetStatus.Valid),
            [new DocumentationPatchChangedFileInput(
                locator.Path,
                locator.OriginalFileSha256,
                Hash('9'),
                request.Blocks.Length,
                OriginalDocumentationByteCount:
                    proposal.PatchBlock.EditKind == DocumentationPatchEditKind.Insert ? 0 : 32,
                CandidateDocumentationByteCount: 48,
                OriginalDocumentationLineCount:
                    proposal.PatchBlock.EditKind == DocumentationPatchEditKind.Insert ? 0 : 1,
                CandidateDocumentationLineCount: 3)],
            invariants,
            []);
        return result;
    }

    private static PatchRejectionScenario CreatePatchRejectionScenario()
    {
        var scenario = CreateProposalScenario();
        var first = scenario.Plan.WorkItems[0];
        var second = scenario.Plan.WorkItems[1];
        var firstExchange = CreateScribeExchange(first);
        var secondExchange = CreateScribeExchange(
            second,
            attemptId: "scribe-attempt.11111111111111111111111111111111");
        var firstProposal = AdmitProposal(
            scenario,
            WithState(
                scenario.InitialState,
                scenario.InitialState.WorkItems,
                ProviderReservation(first.WorkItemKey, firstExchange)),
            first,
            firstExchange);
        var firstComplete = WithState(
            scenario.InitialState,
            ReplaceWork(
                scenario.InitialState,
                first.WorkItemKey,
                CampaignWorkStatus.ProposalComplete,
                firstProposal),
            ProviderReservation(second.WorkItemKey, secondExchange));
        var secondProposal = AdmitProposal(scenario, firstComplete, second, secondExchange);
        var proposalComplete = WithState(
            scenario.InitialState,
            ReplaceWork(
                ReplaceWork(
                    scenario.InitialState,
                    first.WorkItemKey,
                    CampaignWorkStatus.ProposalComplete,
                    firstProposal),
                second.WorkItemKey,
                CampaignWorkStatus.ProposalComplete,
                secondProposal),
            activeReservation: null);
        var request = CampaignStateFactory.ReconstructPatchRequest(
            proposalComplete,
            PatchContext(firstExchange.Request),
            CurrentEvidence(firstExchange, secondExchange));
        var reservationPredecessor = CampaignStateJson.CreateArtifact(proposalComplete);
        var reserved = CampaignStateReducer.ReservePatchInvocation(
            reservationPredecessor,
            request,
            elapsedMilliseconds: 1_000);
        Assert.Equal(CampaignTransitionKind.Applied, reserved.Kind);
        var result = CreateRejectedPatchResult(
            request,
            first.WorkItemKey,
            "patch.rejected.unsafe-change");
        return new PatchRejectionScenario(
            scenario,
            reserved,
            reservationPredecessor,
            reserved.Artifact,
            request,
            result,
            first.WorkItemKey);
    }

    private static DocumentationPatchValidationResult CreateRejectedPatchResult(
        DocumentationPatchRequest request,
        string invalidWorkItemKey,
        string diagnosticCode,
        string? diagnosticBlockId = "__selected__")
    {
        var invariants = new[]
        {
            "patch.invariant.non-documentation-tokens-unchanged",
            "patch.invariant.selected-documentation-only",
            "patch.invariant.no-new-parse-diagnostics",
            "patch.invariant.symbol-semantics-unchanged",
            "patch.invariant.repository-scope",
            "patch.invariant.file-representation-preserved",
            "patch.invariant.idempotent",
            "patch.invariant.traceable",
            "patch.invariant.fail-closed",
        }.Select(id => new DocumentationPatchInvariantResult(id, DocumentationPatchInvariantStatus.Passed));
        var noEffectiveChange = diagnosticCode == "patch.rejected.no-effective-change";
        return DocumentationPatchValidator.CreateResult(
            request,
            DocumentationPatchOutcome.Rejected,
            request.Blocks.Select(block => string.Equals(block.BlockId, invalidWorkItemKey, StringComparison.Ordinal)
                    && !noEffectiveChange
                ? DocumentationPatchTargetStatus.Invalid
                : DocumentationPatchTargetStatus.Valid),
            [],
            invariants,
            [new DocumentationPatchDiagnostic(
                DocumentationPatchDiagnosticSeverity.Error,
                diagnosticCode,
                diagnosticBlockId == "__selected__" ? invalidWorkItemKey : diagnosticBlockId,
                Path: null,
                Pointer: null)]);
    }

    private static CampaignCheckpointState WithPatchReservation(
        CampaignCheckpointState state,
        DocumentationPatchRequest request) =>
        WithState(
            state,
            state.WorkItems,
            CampaignStateFactory.CreatePatchReservation(
                state,
                request,
                patchAttemptCount: 1,
                elapsedMilliseconds: 0),
            state.CandidateObservation,
            state.CumulativeOutcome);

    private static void AssertMutationFailure(
        CampaignCheckpointState state,
        Action<JsonObject> mutation,
        CampaignStateValidationCode expected)
    {
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(CampaignStateJson.Write(state)));
        mutation(root);
        var parsed = CampaignStateJson.Parse(Encoding.UTF8.GetBytes(root.ToJsonString() + "\n"));
        Assert.False(parsed.IsValid);
        Assert.Equal(expected, parsed.FailureCode);
    }

    private static void AssertValidMutation(CampaignCheckpointState state, Action<JsonObject> mutation)
    {
        _ = MutateValidState(state, mutation);
    }

    private static CampaignCheckpointState MutateValidState(
        CampaignCheckpointState state,
        Action<JsonObject> mutation)
    {
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(CampaignStateJson.Write(state)));
        mutation(root);
        var parsed = CampaignStateJson.Parse(Encoding.UTF8.GetBytes(root.ToJsonString() + "\n"));
        Assert.True(parsed.IsValid, parsed.FailureCode?.ToString());
        return parsed.Artifact!.State;
    }

    private static void AssertSchemaRuntimeBoundary(
        CampaignCheckpointState state,
        Action<JsonObject> maximumMutation,
        Action<JsonObject> overMutation)
    {
        var maximum = Assert.IsType<JsonObject>(JsonNode.Parse(CampaignStateJson.Write(state)));
        maximumMutation(maximum);
        using (var document = JsonDocument.Parse(maximum.ToJsonString()))
        {
            var evaluation = EvaluateCampaignSchema(document.RootElement);
            Assert.True(evaluation.IsValid, DescribeSchemaFailures(evaluation));
        }

        var maximumParse = CampaignStateJson.Parse(Encoding.UTF8.GetBytes(maximum.ToJsonString() + "\n"));
        Assert.True(maximumParse.IsValid, maximumParse.FailureCode?.ToString());

        AssertSchemaAndRuntimeReject(state, overMutation);
    }

    private static void AssertSchemaAndRuntimeReject(
        CampaignCheckpointState state,
        Action<JsonObject> mutation)
    {
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(CampaignStateJson.Write(state)));
        mutation(root);
        using (var document = JsonDocument.Parse(root.ToJsonString()))
        {
            Assert.False(EvaluateCampaignSchema(document.RootElement).IsValid);
        }

        Assert.False(CampaignStateJson.Parse(
            Encoding.UTF8.GetBytes(root.ToJsonString() + "\n")).IsValid);
    }

    private static CampaignCheckpointState WithState(
        CampaignCheckpointState basis,
        IEnumerable<CampaignWorkItemState> workItems,
        CampaignActiveReservation? activeReservation,
        CampaignCandidateObservation? candidateObservation = null,
        CampaignCumulativeOutcome? cumulativeOutcome = null) =>
        CampaignStateFactory.CreateValidated(
            basis.ProductRevision,
            basis.CampaignLineage,
            basis.Snapshot,
            basis.CheckpointRevision,
            basis.ConfiguredCeilings,
            basis.LineageCharges,
            workItems,
            activeReservation,
            candidateObservation,
            cumulativeOutcome,
            basis.TerminalOutcome,
            basis.Predecessor);

    private static ImmutableArray<CampaignWorkItemState> ReplaceWork(
        CampaignCheckpointState state,
        string workItemKey,
        CampaignWorkStatus status,
        CampaignTrustedProposal proposal) =>
        state.WorkItems.Select(work => string.Equals(work.WorkItemKey, workItemKey, StringComparison.Ordinal)
            ? work with { Status = status, TrustedProposal = proposal, ClosedOutcome = null }
            : work).ToImmutableArray();

    private static ImmutableArray<CampaignWorkItemState> ReplaceWork(
        ImmutableArray<CampaignWorkItemState> workItems,
        string workItemKey,
        CampaignWorkStatus status,
        CampaignTrustedProposal proposal) =>
        workItems.Select(work => string.Equals(work.WorkItemKey, workItemKey, StringComparison.Ordinal)
            ? work with { Status = status, TrustedProposal = proposal, ClosedOutcome = null }
            : work).ToImmutableArray();

    private static DocumentationPatchContext PatchContext(DocumentationScribeRequest request)
    {
        var node = ReadJsonFixture("documentation-patch", "v1", "valid", "repository-request.json");
        node["context"]!["repositoryContextRef"] = request.Context.RepositoryContextRef.Value;
        node["context"]!["inputIdentity"] = request.Context.InputIdentity;
        var parsed = DocumentationPatchValidator.ParseRequest(Encoding.UTF8.GetBytes(node.ToJsonString()));
        Assert.True(parsed.IsValid, parsed.Failure?.Code);
        return parsed.Request!.Context;
    }

    private static DocumentationPatchRequest RebindPatchRequest(
        DocumentationPatchRequest request,
        DocumentationPatchContext context)
    {
        var writer = typeof(CampaignStateJson).GetMethod(
            "WritePatchRequest",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var bytes = Assert.IsType<byte[]>(writer!.Invoke(
            null,
            [context, request.ProvenanceCatalog, request.Blocks]));
        var parsed = DocumentationPatchValidator.ParseRequest(bytes);
        Assert.True(parsed.IsValid, parsed.Failure?.Code);
        return Assert.IsType<DocumentationPatchRequest>(parsed.Request);
    }

    private static CampaignCheckpointState CreateProposalCompleteState()
    {
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var exchange = CreateScribeExchange(work);
        var proposal = AdmitProposal(
            scenario,
            WithState(
                scenario.InitialState,
                scenario.InitialState.WorkItems,
                ProviderReservation(work.WorkItemKey, exchange)),
            work,
            exchange);
        return WithState(
            scenario.InitialState,
            ReplaceWork(scenario.InitialState, work.WorkItemKey, CampaignWorkStatus.ProposalComplete, proposal),
            activeReservation: null);
    }

    private static CampaignCheckpointState CreateProposalCompleteStateWithException(string typeDocumentationId)
    {
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var exchange = CreateScribeExchangeWithException(work, typeDocumentationId);
        var proposal = AdmitProposal(
            scenario,
            WithState(
                scenario.InitialState,
                scenario.InitialState.WorkItems,
                ProviderReservation(work.WorkItemKey, exchange)),
            work,
            exchange);
        return WithState(
            scenario.InitialState,
            ReplaceWork(scenario.InitialState, work.WorkItemKey, CampaignWorkStatus.ProposalComplete, proposal),
            activeReservation: null);
    }

    private static JsonObject CreateProposalCanonicalNode() =>
        Assert.IsType<JsonObject>(JsonNode.Parse(
            CampaignStateJson.CreateArtifact(CreateProposalCompleteState()).ExactUtf8Json.AsSpan()));

    private static AcceptedCandidateScenario CreateAcceptedCandidateScenario()
    {
        var scenario = CreateProposalScenario();
        var work = scenario.Plan.WorkItems[0];
        var exchange = CreateScribeExchange(work);
        var proposal = AdmitProposal(
            scenario,
            WithState(
                scenario.InitialState,
                scenario.InitialState.WorkItems,
                ProviderReservation(work.WorkItemKey, exchange)),
            work,
            exchange);
        var proposalComplete = WithState(
            scenario.InitialState,
            ReplaceWork(scenario.InitialState, work.WorkItemKey, CampaignWorkStatus.ProposalComplete, proposal),
            activeReservation: null);
        var request = CampaignStateFactory.ReconstructPatchRequest(
            proposalComplete,
            PatchContext(exchange.Request),
            CurrentEvidence(exchange));
        var reserved = WithPatchReservation(proposalComplete, request);
        var completion = CreateAcceptedCompletion(reserved, request, proposal);
        var accepted = WithState(
            scenario.InitialState,
            ReplaceWork(scenario.InitialState, work.WorkItemKey, CampaignWorkStatus.Accepted, proposal),
            activeReservation: null,
            candidateObservation: completion.CandidateObservation,
            cumulativeOutcome: completion.CumulativeOutcome);
        return new AcceptedCandidateScenario(accepted, request);
    }

    private static byte[] RunFreshProcessProbe(string temporaryRoot, string culture)
    {
        var outputPath = Path.Join(temporaryRoot, culture + ".json");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("test");
        start.ArgumentList.Add(Path.Join(RepositoryRoot(), "tests", "ContractScribe.Tests", "ContractScribe.Tests.csproj"));
        start.ArgumentList.Add("--configuration");
        start.ArgumentList.Add("Release");
        start.ArgumentList.Add("--no-build");
        start.ArgumentList.Add("--no-restore");
        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add("FullyQualifiedName=ContractScribe.Tests.CampaignStateContractTests.Fresh_process_probe_emits_exact_artifact_when_explicitly_invoked");
        start.Environment["CONTRACT_SCRIBE_CAMPAIGN_STATE_PROBE_OUTPUT"] = outputPath;
        start.Environment["CONTRACT_SCRIBE_CAMPAIGN_STATE_PROBE_CULTURE"] = culture;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The fresh-process probe did not start.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "The fresh-process probe timed out.");
        Assert.True(process.ExitCode == 0, standardOutput + Environment.NewLine + standardError);
        return File.ReadAllBytes(outputPath);
    }

    private static void AssertInvalidCorrelation(Action action)
    {
        var failure = Assert.Throws<CampaignStateValidationException>(action);
        Assert.Equal(CampaignStateValidationCode.InvalidCorrelation, failure.Code);
    }

    private static void AssertInvalidBound(Action action)
    {
        var failure = Assert.Throws<CampaignStateValidationException>(action);
        Assert.Equal(CampaignStateValidationCode.InvalidBound, failure.Code);
    }

    private static DocumentationPatchRequest ParsePatchRequest(string name)
    {
        var parsed = DocumentationPatchValidator.ParseRequest(
            File.ReadAllBytes(Path.Join(RepositoryRoot(), "tests", "fixtures", "documentation-patch", "v1", "valid", name)));
        Assert.True(parsed.IsValid, parsed.Failure?.Code);
        return Assert.IsType<DocumentationPatchRequest>(parsed.Request);
    }

    private static DocumentationPatchValidationResult ParsePatchResult(string name)
    {
        var parsed = DocumentationPatchValidator.ParseValidationResult(
            File.ReadAllBytes(Path.Join(RepositoryRoot(), "tests", "fixtures", "documentation-patch", "v1", "valid", name)));
        Assert.True(parsed.IsValid, parsed.Failure?.Code);
        return Assert.IsType<DocumentationPatchValidationResult>(parsed.Result);
    }

    private static DocumentationScribeRequest ReadScribeRequest()
    {
        var parsed = DocumentationScribeValidation.ParseRequest(
            File.ReadAllBytes(Path.Join(RepositoryRoot(), "tests", "fixtures", "documentation-scribe", "v1", "valid", "request.json")));
        Assert.Null(parsed.Failure);
        return Assert.IsType<DocumentationScribeRequest>(parsed.Request);
    }

    private static JsonObject ReadJsonFixture(params string[] segments) =>
        Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(Path.Join(
            new[] { RepositoryRoot(), "tests", "fixtures" }.Concat(segments).ToArray()))));

    private static void SetSymbol(JsonNode node, SymbolRef symbol)
    {
        node["compilationContextRef"] = symbol.CompilationContextRef;
        node["documentationCommentId"] = symbol.DocumentationCommentId;
    }

    private static void SetSymbol(JsonNode node, string compilationContextRef, string documentationCommentId)
    {
        node["compilationContextRef"] = compilationContextRef;
        node["documentationCommentId"] = documentationCommentId;
    }

    private static void SetSource(JsonNode node, CampaignPlanningRepositorySourceAuthority source)
    {
        node["contentSha256"] = source.ContentSha256;
        var repository = node["locator"]!["repository"]!;
        repository["path"] = source.Path;
        repository["span"]!["start"] = source.RequestedDeclarationSpan.Start;
        repository["span"]!["end"] = source.RequestedDeclarationSpan.End;
    }

    private static BoundObservationEvidence BindEvidence(
        DocumentationObservation observation,
        DocumentationDeclarationInput declaration,
        EvidenceSubject subject,
        string repositoryPath)
    {
        const string EvidenceId = "evidence.declaration";
        var candidate = EvidenceInput.Candidate(
            EvidenceId,
            subject,
            EvidenceKind.SourceDeclaration,
            EvidenceRelation.Declares,
            declaration.DeclarationText,
            EvidenceInput.RepositoryLocator(
                repositoryPath,
                declaration.DeclarationSpan.Start,
                declaration.DeclarationSpan.End));
        var bundle = Assert.IsType<EvidenceBundle>(EvidenceNormalizer.Normalize([candidate]).Bundle);
        return Assert.IsType<BoundObservationEvidence>(EvidenceObservationBinder.Bind(
            observation,
            bundle,
            [EvidenceBindingInput.Declaration(declaration.DeclarationId, EvidenceId, documentationEvidenceId: null)]).Binding);
    }

    private static PolicyDocumentV1 ParseRequiredPolicy()
    {
        const string json = """
            {"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"required"}
            """;
        return Assert.IsType<PolicyDocumentV1>(
            PolicyConfigurationEvaluator.Parse(Encoding.UTF8.GetBytes(json)).Document);
    }

    private static CampaignPlanningContentAuthority Content(
        CampaignPlanningContentFamily family,
        string id,
        string content) =>
        CampaignPlanningContentAuthority.CreateValidatedJsonProjection(
            family,
            id,
            JsonSerializer.SerializeToElement(new { value = content }));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return Assert.IsType<DirectoryInfo>(directory).FullName;
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record TargetSpecification(
        string DocumentationId,
        string Path,
        string DeclarationText,
        string DeclarationId);

    private sealed record DeclarationSet(
        DocumentationDeclarationInput Target,
        DocumentationDeclarationInput Parameter,
        DocumentationDeclarationInput Return);

    private sealed record ProposalScenario(
        JsonElement StyleProjection,
        CampaignPlanningInput Input,
        CampaignWorkPlan Plan,
        CampaignCheckpointState InitialState);

    private sealed record ScribeExchange(
        DocumentationScribeRequest Request,
        DocumentationScribeRunResult Result);

    private sealed record AcceptedCandidateScenario(
        CampaignCheckpointState State,
        DocumentationPatchRequest Request);

    private sealed record PatchRejectionScenario(
        ProposalScenario ProposalScenario,
        CampaignTransitionResult ReservationTransition,
        CampaignCheckpointArtifact ReservationPredecessor,
        CampaignCheckpointArtifact Predecessor,
        DocumentationPatchRequest Request,
        DocumentationPatchValidationResult Result,
        string SelectedWorkItemKey);

    private static CampaignCheckpointState CreateState() =>
        CampaignStateFactory.CreateValidated(
            ProductRevision(),
            "campaign.test",
            Snapshot(),
            0,
            Ceilings(),
            EmptyCharges(),
            [],
            terminalOutcome: new CampaignTerminalOutcome(
                CampaignTerminalKind.Complete,
                CampaignTerminalReason.NoWork));

    private static CampaignStateProductRevision ProductRevision() =>
        new("contract-scribe.test", Hash('1'));

    private static CampaignStateSnapshotAuthority Snapshot() =>
        new(
            "snapshot.test",
            Hash('2'),
            Hash('3'),
            Hash('8'),
            Hash('4'),
            TargetProfile.ExternalApi,
            Hash('5'));

    private static CampaignStateConfiguredCeilings Ceilings() =>
        new(
            new CampaignStateCampaignBudget(
                512, 512, 1_048_576, 8, 3, 100_000, 100_000, 100_000,
                1_000_000, 60_000, 3, false, null, null, null),
            new CampaignStateScribeLimits(
                32, 262_144, 64, 262_144, 8, 8, 16, 3,
                100_000, 100_000, 100_000, 1_000_000, 60_000),
            new CampaignStyleConfigurationAuthority("style.test", Hash('6')),
            Hash('7'));

    private static CampaignLineageCharges EmptyCharges()
    {
        var zero = new CampaignChargeObservation(0, 0, 0);
        return new CampaignLineageCharges(0, zero, zero, zero, zero, zero, zero, zero, zero, 0);
    }

    private static CampaignAcceptedCheckpoint AcceptForTest(
        CampaignCheckpointArtifact predecessor,
        CampaignTransitionResult transition)
    {
        Assert.Equal(CampaignTransitionKind.Applied, transition.Kind);
        var result = CampaignCheckpointAcceptance.AcceptAsync(
                new TransitionCheckpointStore(predecessor),
                transition)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, result.Kind);
        return Assert.IsType<CampaignAcceptedCheckpoint>(result.AcceptedCheckpoint);
    }

    private static CampaignAcceptedCheckpoint AcceptCurrentForTest(CampaignCheckpointArtifact artifact)
    {
        var result = CampaignCheckpointAcceptance.AcceptCurrentAsync(
                new TransitionCheckpointStore(artifact))
            .AsTask()
            .GetAwaiter()
            .GetResult();
        Assert.Equal(CampaignCheckpointAcceptanceKind.Accepted, result.Kind);
        return Assert.IsType<CampaignAcceptedCheckpoint>(result.AcceptedCheckpoint);
    }

    private sealed class TransitionCheckpointStore : ICampaignCheckpointStore
    {
        private CampaignCheckpointArtifact _artifact;

        public TransitionCheckpointStore(CampaignCheckpointArtifact artifact) => _artifact = artifact;

        public CampaignCheckpointWriteKind ReplaceResult { get; init; } = CampaignCheckpointWriteKind.Written;
        public CampaignCheckpointArtifact? WinnerOnRejectedWrite { get; init; }

        public ValueTask<CampaignCheckpointReadResult> ReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(CampaignCheckpointReadResult.Found(
                _artifact.ExactUtf8Json.AsSpan(),
                _artifact.CheckpointRevision,
                _artifact.Sha256));

        public ValueTask<CampaignCheckpointWriteResult> CreateIfAbsentAsync(
            ReadOnlyMemory<byte> exactUtf8Json,
            long checkpointRevision,
            string sha256,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                new CampaignCheckpointWriteResult(CampaignCheckpointWriteKind.AlreadyPresent));

        public ValueTask<CampaignCheckpointWriteResult> ReplaceIfCurrentAsync(
            long expectedCheckpointRevision,
            string expectedSha256,
            ReadOnlyMemory<byte> exactUtf8Json,
            long checkpointRevision,
            string sha256,
            CancellationToken cancellationToken)
        {
            if (ReplaceResult != CampaignCheckpointWriteKind.Written)
            {
                _artifact = WinnerOnRejectedWrite ?? _artifact;
                return ValueTask.FromResult(new CampaignCheckpointWriteResult(ReplaceResult));
            }

            if (_artifact.CheckpointRevision != expectedCheckpointRevision
                || !string.Equals(_artifact.Sha256, expectedSha256, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(new CampaignCheckpointWriteResult(
                    CampaignCheckpointWriteKind.CurrentMismatch));
            }

            _artifact = Assert.IsType<CampaignCheckpointArtifact>(
                CampaignStateJson.Parse(exactUtf8Json).Artifact);
            return ValueTask.FromResult(new CampaignCheckpointWriteResult(
                CampaignCheckpointWriteKind.Written));
        }
    }

    private static string Hash(char value) => new(value, 64);

    private static byte[] WriteUnderCulture(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        return CampaignStateJson.Write(CreateState());
    }

    private static string FixtureRoot() => Path.GetFullPath(Path.Join(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "fixtures", "campaign", "state"));

    private static string FixturePath(string name) =>
        Path.GetFullPath(Path.Join(FixtureRoot(), name));

    private static string FixturePath(string directory, string name) =>
        Path.GetFullPath(Path.Join(FixtureRoot(), directory, name));

    private static JsonSchema LoadCampaignSchema()
    {
        var registry = new SchemaRegistry();
        var options = new BuildOptions { SchemaRegistry = registry };
        _ = JsonSchema.FromText(File.ReadAllText(PatchRequestSchemaPath()), options);
        return JsonSchema.FromText(File.ReadAllText(SchemaPath()), options);
    }

    private static EvaluationResults EvaluateCampaignSchema(JsonElement value) =>
        CampaignSchema.Value.Evaluate(value, new EvaluationOptions { OutputFormat = OutputFormat.List });

    private static string DescribeSchemaFailures(EvaluationResults evaluation)
    {
        var failures = new List<string>();
        CollectSchemaFailures(evaluation, failures);
        return string.Join(", ", failures
            .OrderBy(failure => failure.Length)
            .ThenBy(failure => failure, StringComparer.Ordinal)
            .Take(40));
    }

    private static void CollectSchemaFailures(EvaluationResults evaluation, List<string> failures)
    {
        if (!evaluation.IsValid)
        {
            var keywords = evaluation.Errors is null
                ? string.Empty
                : string.Join("+", evaluation.Errors.Keys.Order(StringComparer.Ordinal));
            failures.Add(evaluation.EvaluationPath + "|" + evaluation.InstanceLocation + ":" + keywords);
        }

        if (evaluation.Details is not null)
        {
            foreach (var detail in evaluation.Details)
            {
                CollectSchemaFailures(detail, failures);
            }
        }
    }

    private static string PatchRequestSchemaPath() => Path.GetFullPath(Path.Join(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "schemas", "documentation-patch", "v1.request.schema.json"));

    private static string SchemaPath() => Path.GetFullPath(Path.Join(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "schemas", "campaign-state", "v1.schema.json"));
}
