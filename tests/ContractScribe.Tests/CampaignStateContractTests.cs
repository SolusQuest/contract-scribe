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
        Assert.Equal("b772dabcbcce3435dcd4ff138dcc63e84fb6e19dc87d45e2f987cf0d89ac99ce", artifact.Sha256);
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
            PatchContext(exchange.Request));
        var patchReserved = WithState(
            proposalComplete,
            proposalComplete.WorkItems,
            new CampaignPatchReservation(
                activeRequest.ArtifactSha256,
                proposalComplete.CheckpointRevision,
                PatchAttemptCount: 1,
                ElapsedMilliseconds: 0));
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
        foreach (var mutation in mutations)
        {
            var changed = CreateScribeExchange(work, requestMutation: mutation.Request, resultMutation: mutation.Result);
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
        var firstRequest = CampaignStateFactory.ReconstructPatchRequest(firstOnly, PatchContext(firstExchange.Request));
        var candidate = Candidate(first.WorkItemKey, firstProposal, firstRequest.ArtifactSha256, Hash('8'));
        var mixed = WithState(
            scenario.InitialState,
            ReplaceWork(
                ReplaceWork(scenario.InitialState, first.WorkItemKey, CampaignWorkStatus.Accepted, firstProposal),
                second.WorkItemKey,
                CampaignWorkStatus.ProposalComplete,
                secondProposal),
            activeReservation: null,
            candidateObservation: candidate);

        var active = CampaignStateFactory.ReconstructPatchRequest(mixed, PatchContext(firstExchange.Request));
        var accepted = CampaignStateFactory.ReconstructAcceptedPatchRequest(mixed, PatchContext(firstExchange.Request));

        Assert.Equal(2, active.Blocks.Length);
        Assert.Equal([first.WorkItemKey, second.WorkItemKey], active.Blocks.Select(block => block.BlockId));
        Assert.Single(accepted.Blocks);
        Assert.Equal(first.WorkItemKey, accepted.Blocks[0].BlockId);
        Assert.Equal(firstRequest.ArtifactSha256, accepted.ArtifactSha256);
    }

    [Fact]
    public void Cumulative_outcome_uses_a_closed_typed_result_presence_matrix()
    {
        var accepted = CreateAcceptedCandidateScenario();
        var candidate = Assert.IsType<CampaignCandidateObservation>(accepted.State.CandidateObservation);
        _ = WithState(
            accepted.State,
            accepted.State.WorkItems,
            activeReservation: null,
            candidateObservation: candidate,
            cumulativeOutcome: new CampaignCumulativeOutcome(
                CampaignCumulativeOutcomeKind.Accepted,
                candidate.PatchRequestSha256,
                candidate.PatchResultCommitmentSha256,
                accepted.State.CheckpointRevision));
        AssertInvalidCorrelation(() => WithState(
            accepted.State,
            accepted.State.WorkItems,
            activeReservation: null,
            candidateObservation: candidate,
            cumulativeOutcome: new CampaignCumulativeOutcome(
                CampaignCumulativeOutcomeKind.Accepted,
                candidate.PatchRequestSha256,
                PatchResultCommitmentSha256: null,
                CompletedFromCheckpointRevision: accepted.State.CheckpointRevision)));

        foreach (var kind in new[] { CampaignCumulativeOutcomeKind.Rejected, CampaignCumulativeOutcomeKind.Stale })
        {
            _ = StateWithOutcome(kind, Hash('8'));
            AssertInvalidCorrelation(() => StateWithOutcome(kind, resultCommitment: null));
        }

        foreach (var kind in new[]
        {
            CampaignCumulativeOutcomeKind.HostFailure,
            CampaignCumulativeOutcomeKind.Cancelled,
            CampaignCumulativeOutcomeKind.Timeout,
        })
        {
            _ = StateWithOutcome(kind, resultCommitment: null);
            AssertInvalidCorrelation(() => StateWithOutcome(kind, Hash('8')));
        }
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
    public void Candidate_observation_rejects_duplicate_order_membership_count_and_bound_mutations()
    {
        var scenario = CreateAcceptedCandidateScenario();
        var candidate = Assert.IsType<CampaignCandidateObservation>(scenario.State.CandidateObservation);

        AssertInvalidCorrelation(() => WithState(
            scenario.State,
            scenario.State.WorkItems,
            activeReservation: null,
            candidateObservation: candidate with
            {
                AcceptedWorkItemKeys = [candidate.AcceptedWorkItemKeys[0], candidate.AcceptedWorkItemKeys[0]],
            }));
        AssertInvalidCorrelation(() => WithState(
            scenario.State,
            scenario.State.WorkItems,
            activeReservation: null,
            candidateObservation: candidate with
            {
                PatchRequestSha256 = "not-a-sha",
            }));
        AssertInvalidBound(() => WithState(
            scenario.State,
            scenario.State.WorkItems,
            activeReservation: null,
            candidateObservation: candidate with
            {
                ChangedFiles = [candidate.ChangedFiles[0] with { ChangedDocumentationBlockCount = 2 }],
            }));
        AssertInvalidCorrelation(() => WithState(
            scenario.State,
            scenario.State.WorkItems,
            activeReservation: null,
            candidateObservation: candidate with
            {
                ChangedFiles =
                [
                    candidate.ChangedFiles[0],
                    candidate.ChangedFiles[0] with { CandidateFileSha256 = Hash('9') },
                ],
            }));
        AssertInvalidBound(() => WithState(
            scenario.State,
            scenario.State.WorkItems,
            activeReservation: null,
            candidateObservation: candidate with
            {
                ChangedFiles = [candidate.ChangedFiles[0] with
                {
                    CandidateDocumentationByteCount = checked((int)scenario.State.ConfiguredCeilings.CampaignBudget.MaximumPatchBytes + 1),
                }],
            }));
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

    private static ProposalScenario CreateProposalScenario()
    {
        const string Context = "synthetic.v1";
        var specifications = new[]
        {
            new TargetSpecification(
                "M:Synthetic.Widget.Run(System.String)",
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
                100,
                20,
                1_000_000,
                100,
                3,
                1_000_000,
                500_000,
                100_000,
                5_000_000,
                120_000,
                8,
                costEnforced: true,
                "USD",
                Content(CampaignPlanningContentFamily.CostRatePolicy, "cost", "rates-v1")),
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
        var initial = CampaignStateFactory.CreateInitial("style.synthetic", styleProjection, input, plan);
        return new ProposalScenario(styleProjection, input, plan, initial);
    }

    private static ScribeExchange CreateScribeExchange(
        CampaignPlanningWorkItem work,
        string inputIdentity = "samples/Synthetic.csproj",
        string attemptId = "scribe-attempt.0123456789abcdef0123456789abcdef",
        Action<JsonObject>? requestMutation = null,
        Action<JsonObject>? resultMutation = null)
    {
        var target = Assert.Single(work.Targets);
        var source = Assert.IsType<CampaignPlanningRepositorySourceAuthority>(target.Source);
        var requestNode = ReadJsonFixture("documentation-scribe", "v1", "valid", "request.json");
        requestNode["context"]!["inputIdentity"] = inputIdentity;
        SetSymbol(requestNode["target"]!["symbolRef"]!, target.SymbolRef);
        SetSource(requestNode["target"]!["sourceCommitment"]!, source);
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

        var resultNode = ReadJsonFixture("documentation-scribe", "v1", "valid", "proposal-result.json");
        resultNode["scribeRequestSha256"] = request.ArtifactSha256;
        resultNode["attemptId"] = attemptId;
        resultNode["runEnvelope"]!["scribeRequestSha256"] = request.ArtifactSha256;
        resultNode["runEnvelope"]!["attemptId"] = attemptId;
        SetSymbol(resultNode["terminal"]!["target"]!["symbolRef"]!, target.SymbolRef);
        SetSource(resultNode["terminal"]!["target"]!["sourceCommitment"]!, source);
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

    private static ScribeExchange CreateNonProposalExchange(CampaignPlanningWorkItem work)
    {
        var proposalExchange = CreateScribeExchange(work);
        var resultNode = ReadJsonFixture("documentation-scribe", "v1", "valid", "skip-result.json");
        resultNode["scribeRequestSha256"] = proposalExchange.Request.ArtifactSha256;
        resultNode["attemptId"] = proposalExchange.Result.AttemptId.Value;
        resultNode["runEnvelope"]!["scribeRequestSha256"] = proposalExchange.Request.ArtifactSha256;
        resultNode["runEnvelope"]!["attemptId"] = proposalExchange.Result.AttemptId.Value;
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
            exchange.Request.ToolPolicyId,
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

    private static DocumentationPatchContext PatchContext(DocumentationScribeRequest request) =>
        ParsePatchRequest("repository-request.json").Context;

    private static CampaignCandidateObservation Candidate(
        string workItemKey,
        CampaignTrustedProposal proposal,
        string requestSha256,
        string resultCommitmentSha256)
    {
        var locator = Assert.IsType<DocumentationPatchRepositoryLocator>(proposal.PatchBlock.Locator);
        return new CampaignCandidateObservation(
            [workItemKey],
            [new CampaignChangedFileObservation(
                locator.Path,
                locator.OriginalFileSha256,
                Hash('9'),
                ChangedDocumentationBlockCount: 1,
                OriginalDocumentationByteCount: 32,
                CandidateDocumentationByteCount: 48,
                OriginalDocumentationLineCount: 1,
                CandidateDocumentationLineCount: 3)],
            requestSha256,
            resultCommitmentSha256);
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
        var request = CampaignStateFactory.ReconstructPatchRequest(proposalComplete, PatchContext(exchange.Request));
        var accepted = WithState(
            scenario.InitialState,
            ReplaceWork(scenario.InitialState, work.WorkItemKey, CampaignWorkStatus.Accepted, proposal),
            activeReservation: null,
            candidateObservation: Candidate(work.WorkItemKey, proposal, request.ArtifactSha256, Hash('8')));
        return new AcceptedCandidateScenario(accepted, request);
    }

    private static CampaignCheckpointState StateWithOutcome(
        CampaignCumulativeOutcomeKind kind,
        string? resultCommitment) =>
        CampaignStateFactory.CreateValidated(
            ProductRevision(),
            "campaign.test",
            Snapshot(),
            0,
            Ceilings(),
            EmptyCharges(),
            [],
            cumulativeOutcome: new CampaignCumulativeOutcome(kind, Hash('7'), resultCommitment, 0));

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
