using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class AuditAggregationTests
{
    [Theory]
    [InlineData(PolicyExpectation.Required, DocumentationObservationValue.Present, "audit.outcome.compliant", "audit.reason.required-present")]
    [InlineData(PolicyExpectation.Required, DocumentationObservationValue.Absent, "audit.outcome.violation", "audit.reason.required-absent")]
    [InlineData(PolicyExpectation.Optional, DocumentationObservationValue.Present, "audit.outcome.compliant", "audit.reason.optional-present")]
    [InlineData(PolicyExpectation.Optional, DocumentationObservationValue.Absent, "audit.outcome.compliant", "audit.reason.optional-absent")]
    [InlineData(PolicyExpectation.Forbidden, DocumentationObservationValue.Present, "audit.outcome.violation", "audit.reason.forbidden-present")]
    [InlineData(PolicyExpectation.Forbidden, DocumentationObservationValue.Absent, "audit.outcome.compliant", "audit.reason.forbidden-absent")]
    public void OrdinaryMatrix_ProducesExactOutcomeAndReason(
        PolicyExpectation expectation,
        DocumentationObservationValue observation,
        string expectedOutcome,
        string expectedReason)
    {
        var scenario = CreateSupportedTarget();
        var policy = CreatePolicy(expectation, TargetProfile.ExternalApi);
        var contributions = Evaluate(policy, [
            PolicyConfigurationInput.Repository("src/Audit.csproj", "src/Widget.cs"),
        ]);
        var evidence = CreateBoundEvidence(scenario, observation);

        var document = AuditAggregator.Aggregate(
            TargetProfile.ExternalApi,
            scenario.Classifications,
            policy,
            [AuditInput.Target(scenario.Target, contributions, evidence)]);
        using var parsed = JsonDocument.Parse(AuditJson.Write(document));
        var result = parsed.RootElement.GetProperty("results")[0];

        Assert.Equal(expectedOutcome, result.GetProperty("auditOutcome").GetString());
        Assert.Equal(expectedReason, result.GetProperty("reasonCode").GetString());
        Assert.Equal(
            PolicyConfigurationVocabulary.GetId(expectation),
            result.GetProperty("policyExpectation").GetString());
        Assert.Equal(
            DocumentationObservationVocabulary.GetId(observation),
            result.GetProperty("documentationObservation").GetString());
        Assert.NotEmpty(result.GetProperty("evidenceIds").EnumerateArray());
        Assert.Equal(
            "evidence.bundle.complete",
            result.GetProperty("evidenceBundle").GetProperty("availabilityStatus").GetString());
    }

    [Fact]
    public void TargetProfileGate_AppliesInBothDirectionsAndToEmptyResults()
    {
        var external = CreateSupportedTarget(TargetProfile.ExternalApi);
        var assemblyPolicy = CreatePolicy(
            PolicyExpectation.Required,
            TargetProfile.AssemblyVisible);
        var assemblyContributions = Evaluate(
            assemblyPolicy,
            [PolicyConfigurationInput.Repository("src/Audit.csproj", "src/Widget.cs")]);
        var failure = Assert.Throws<AuditValidationException>(() =>
            AuditAggregator.Aggregate(
                TargetProfile.ExternalApi,
                external.Classifications,
                assemblyPolicy,
                [AuditInput.Target(external.Target, assemblyContributions)]));
        Assert.Equal(AuditValidationCode.TargetProfileMismatch, failure.Code);

        var emptyBuffer = new ClassificationCandidateBuffer();
        var empty = Assert.IsType<ClassificationSet>(
            emptyBuffer.Normalize(TargetProfile.ExternalApi).ClassificationSet);
        var externalPolicy = CreatePolicy(
            PolicyExpectation.Required,
            TargetProfile.ExternalApi);
        var reverse = Assert.Throws<AuditValidationException>(() =>
            AuditAggregator.Aggregate(
                TargetProfile.AssemblyVisible,
                empty,
                externalPolicy,
                []));
        Assert.Equal(AuditValidationCode.TargetProfileMismatch, reverse.Code);

        var document = AuditAggregator.Aggregate(
            TargetProfile.ExternalApi,
            empty,
            externalPolicy,
            []);
        Assert.Equal(0, document.ResultCount);
    }

    [Fact]
    public void ContributionSetFromDifferentPolicy_FailsClosed()
    {
        var scenario = CreateSupportedTarget();
        var accepted = CreatePolicy(
            PolicyExpectation.Required,
            TargetProfile.ExternalApi);
        var different = CreatePolicy(
            PolicyExpectation.Optional,
            TargetProfile.ExternalApi);
        var contributions = Evaluate(different, [
            PolicyConfigurationInput.Repository("src/Audit.csproj", "src/Widget.cs"),
        ]);

        var failure = Assert.Throws<AuditValidationException>(() =>
            AuditAggregator.Aggregate(
                TargetProfile.ExternalApi,
                scenario.Classifications,
                accepted,
                [AuditInput.Target(
                    scenario.Target,
                    contributions,
                    CreateBoundEvidence(
                        scenario,
                        DocumentationObservationValue.Present))]));

        Assert.Equal(AuditValidationCode.InvalidPolicy, failure.Code);
    }

    [Fact]
    public void ComponentMalformedXml_ProducesExactProjectionAndCanonicalInputOrder()
    {
        var scenario = CreateSupportedComponent();
        var policy = CreatePolicy(
            PolicyExpectation.Required,
            TargetProfile.ExternalApi);
        var targetContributions = Evaluate(policy, []);
        var componentContributions = Evaluate(policy, [
            PolicyConfigurationInput.Repository("src/Audit.csproj", "src/Widget.cs"),
        ]);
        var evidence = CreateMalformedComponentEvidence(scenario);
        var target = AuditInput.Target(
            scenario.Target,
            targetContributions);
        var component = AuditInput.Component(
            scenario.Component,
            componentContributions,
            evidence);

        var forward = AuditJson.Write(AuditAggregator.Aggregate(
            TargetProfile.ExternalApi,
            scenario.Classifications,
            policy,
            [target, component]));
        var reverse = AuditJson.Write(AuditAggregator.Aggregate(
            TargetProfile.ExternalApi,
            scenario.Classifications,
            policy,
            [component, target]));

        Assert.Equal(forward, reverse);
        using var parsed = JsonDocument.Parse(forward);
        var results = parsed.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal(
            ["TargetClassification", "ComponentClassification"],
            results.Select(result => result.GetProperty("classification")
                .GetProperty("recordType").GetString()));
        var componentResult = results[1];
        AssertProjection(
            componentResult,
            "audit.reason.documentation-unavailable.malformed-xml",
            "single",
            "required",
            "documentation.unavailable",
            "evidence.bundle.complete",
            null);
        Assert.Single(componentResult.GetProperty("evidenceIds").EnumerateArray());
        Assert.Equal(
            "malformed",
            componentResult.GetProperty("evidenceAuthority")
                .GetProperty("declarations")[0]
                .GetProperty("blockState").GetString());
    }

    [Fact]
    public void PrecedenceCatalog_IsClosedAndProjectsExactSkippedShapes()
    {
        var classificationStates = Enum.GetValues<PrecedenceClassificationState>();
        var policyStates = Enum.GetValues<PrecedencePolicyState>();
        var evidenceStates = Enum.GetValues<PrecedenceEvidenceState>();
        var expectedCaseIds = (
            from classification in classificationStates
            from policy in policyStates
            from evidence in evidenceStates
            select CaseId(classification, policy, evidence))
            .ToHashSet(StringComparer.Ordinal);
        var executedCaseIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var classificationState in classificationStates)
            foreach (var policyState in policyStates)
                foreach (var evidenceState in evidenceStates)
                {
                    var caseId = CaseId(classificationState, policyState, evidenceState);
                    Assert.True(executedCaseIds.Add(caseId));
                    if (evidenceState is PrecedenceEvidenceState.Malformed
                        or PrecedenceEvidenceState.PartialMalformed)
                    {
                        AssertComponentPrecedenceCase(
                            classificationState,
                            policyState,
                            evidenceState,
                            caseId);
                        continue;
                    }

                    var evidenceTarget = CreateSupportedTarget();
                    var target = classificationState == PrecedenceClassificationState.Supported
                        ? evidenceTarget
                        : CreateUnsupportedTarget();
                    var (policy, contributions, contributionExpectations) = CreatePolicyState(policyState);
                    var evidence = evidenceState switch
                    {
                        PrecedenceEvidenceState.OrdinaryPresent => CreateBoundEvidence(
                            evidenceTarget,
                            DocumentationObservationValue.Present),
                        PrecedenceEvidenceState.SourceUnavailable =>
                            CreateSourceUnavailableEvidence(evidenceTarget),
                        PrecedenceEvidenceState.Partial => CreatePartialEvidence(evidenceTarget),
                        PrecedenceEvidenceState.Missing => null,
                        _ => throw new ArgumentOutOfRangeException(nameof(evidenceState)),
                    };
                    var mustFail = classificationState == PrecedenceClassificationState.Supported
                        && policyState == PrecedencePolicyState.Usable
                        && evidenceState == PrecedenceEvidenceState.Missing;

                    if (mustFail)
                    {
                        var failure = Assert.Throws<AuditValidationException>(() =>
                            AuditAggregator.Aggregate(
                                TargetProfile.ExternalApi,
                                target.Classifications,
                                policy,
                                [AuditInput.Target(target.Target, contributions, evidence)]));
                        Assert.Equal(AuditValidationCode.InvalidEvidence, failure.Code);
                        continue;
                    }

                    var document = AuditAggregator.Aggregate(
                        TargetProfile.ExternalApi,
                        target.Classifications,
                        policy,
                        [AuditInput.Target(target.Target, contributions, evidence)]);
                    using var parsed = JsonDocument.Parse(AuditJson.Write(document));
                    AssertClosedProjection(
                        parsed.RootElement.GetProperty("results")[0],
                        ExpectedProjection(classificationState, policyState, evidenceState),
                        contributionExpectations,
                        caseId);
                }

        Assert.True(expectedCaseIds.SetEquals(executedCaseIds));
    }

    private static void AssertComponentPrecedenceCase(
        PrecedenceClassificationState classificationState,
        PrecedencePolicyState policyState,
        PrecedenceEvidenceState evidenceState,
        string caseId)
    {
        var evidenceScenario = CreateSupportedComponent();
        var scenario = classificationState == PrecedenceClassificationState.Supported
            ? evidenceScenario
            : CreateSkippedComponent();
        var (policy, contributions, contributionExpectations) = CreatePolicyState(policyState);
        var targetContributions = Evaluate(policy, []);
        var evidence = evidenceState == PrecedenceEvidenceState.Malformed
            ? CreateMalformedComponentEvidence(evidenceScenario)
            : CreatePartialMalformedComponentEvidence(evidenceScenario);
        var document = AuditAggregator.Aggregate(
            TargetProfile.ExternalApi,
            scenario.Classifications,
            policy,
            [
                AuditInput.Target(scenario.Target, targetContributions),
                AuditInput.Component(scenario.Component, contributions, evidence),
            ]);
        using var parsed = JsonDocument.Parse(AuditJson.Write(document));
        var componentResult = Assert.Single(
            parsed.RootElement.GetProperty("results").EnumerateArray(),
            result => result.GetProperty("classification").GetProperty("recordType")
                .GetString() == "ComponentClassification");
        AssertClosedProjection(
            componentResult,
            ExpectedProjection(classificationState, policyState, evidenceState),
            contributionExpectations,
            caseId);
    }

    private static string CaseId(
        PrecedenceClassificationState classification,
        PrecedencePolicyState policy,
        PrecedenceEvidenceState evidence) =>
        $"classification-{classification.ToString().ToLowerInvariant()}__policy-{policy.ToString().ToLowerInvariant()}__evidence-{evidence.ToString().ToLowerInvariant()}";

    private static (
        PolicyDocumentV1 Policy,
        PolicyContributionSet Contributions,
        string[] ContributionExpectations) CreatePolicyState(PrecedencePolicyState state)
    {
        if (state == PrecedencePolicyState.Conflict)
        {
            var conflict = CreateConflictPolicy();
            return (
                conflict,
                Evaluate(conflict, [
                    PolicyConfigurationInput.Repository("src/Audit.csproj", "src/A.cs"),
                    PolicyConfigurationInput.Repository("src/Audit.csproj", "src/B.cs"),
                ]),
                ["required", "forbidden"]);
        }

        var required = CreatePolicy(PolicyExpectation.Required, TargetProfile.ExternalApi);
        return state == PrecedencePolicyState.Usable
            ? (
                required,
                Evaluate(required, [
                    PolicyConfigurationInput.Repository("src/Audit.csproj", "src/Widget.cs"),
                ]),
                ["required"])
            : (required, Evaluate(required, []), []);
    }

    private static ClosedProjection ExpectedProjection(
        PrecedenceClassificationState classification,
        PrecedencePolicyState policy,
        PrecedenceEvidenceState evidence)
    {
        if (classification == PrecedenceClassificationState.Skipped)
        {
            return ClosedProjection.Skipped(
                "audit.reason.classification-skipped",
                "unavailable");
        }

        if (policy == PrecedencePolicyState.Conflict)
        {
            return ClosedProjection.Skipped("audit.reason.policy-conflict", "conflict");
        }

        if (policy == PrecedencePolicyState.Unavailable)
        {
            return ClosedProjection.Skipped("audit.reason.policy-unavailable", "unavailable");
        }

        return evidence switch
        {
            PrecedenceEvidenceState.OrdinaryPresent => new ClosedProjection(
                "audit.outcome.compliant",
                "audit.reason.required-present",
                "single",
                "required",
                "documentation.present",
                "evidence.bundle.complete",
                null,
                EvidenceIdCount: 1,
                EvidenceItemCount: 1,
                HasAuthority: true),
            PrecedenceEvidenceState.SourceUnavailable => new ClosedProjection(
                "audit.outcome.skipped",
                "audit.reason.documentation-unavailable",
                "single",
                "required",
                "documentation.unavailable",
                "evidence.bundle.unavailable",
                "evidence.omission.source-unavailable",
                EvidenceIdCount: 0,
                EvidenceItemCount: 0,
                HasAuthority: false),
            PrecedenceEvidenceState.Partial or PrecedenceEvidenceState.PartialMalformed =>
                new ClosedProjection(
                    "audit.outcome.skipped",
                    "audit.reason.evidence-incomplete",
                    "single",
                    "required",
                    "documentation.unavailable",
                    "evidence.bundle.partial",
                    "evidence.omission.budget-exhausted",
                    EvidenceIdCount: 0,
                    EvidenceItemCount: 1,
                    HasAuthority: false),
            PrecedenceEvidenceState.Malformed => new ClosedProjection(
                "audit.outcome.skipped",
                "audit.reason.documentation-unavailable.malformed-xml",
                "single",
                "required",
                "documentation.unavailable",
                "evidence.bundle.complete",
                null,
                EvidenceIdCount: 1,
                EvidenceItemCount: 1,
                HasAuthority: true),
            _ => throw new InvalidOperationException("Missing evidence is the closed failure row."),
        };
    }

    private static void AssertClosedProjection(
        JsonElement result,
        ClosedProjection expected,
        IReadOnlyList<string> contributionExpectations,
        string caseId)
    {
        var actualOutcome = result.GetProperty("auditOutcome").GetString();
        Assert.True(
            string.Equals(expected.Outcome, actualOutcome, StringComparison.Ordinal),
            $"{caseId}: expected outcome {expected.Outcome}, actual {actualOutcome}.");
        Assert.Equal(expected.Reason, result.GetProperty("reasonCode").GetString());
        Assert.Equal(expected.Resolution, result.GetProperty("policyResolution").GetString());
        AssertNullableString(expected.Expectation, result.GetProperty("policyExpectation"));
        AssertNullableString(expected.Observation, result.GetProperty("documentationObservation"));
        Assert.Equal(
            contributionExpectations,
            result.GetProperty("policyContributions").EnumerateArray()
                .Select(item => item.GetProperty("policyExpectation").GetString()!)
                .ToArray());
        var evidenceIds = result.GetProperty("evidenceIds").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        Assert.Equal(expected.EvidenceIdCount, evidenceIds.Length);
        Assert.Equal(evidenceIds.Order(StringComparer.Ordinal), evidenceIds);
        Assert.Equal(expected.HasAuthority, result.TryGetProperty("evidenceAuthority", out _));
        var bundle = result.GetProperty("evidenceBundle");
        Assert.Equal(expected.BundleStatus, bundle.GetProperty("availabilityStatus").GetString());
        Assert.Equal(
            expected.EvidenceItemCount,
            bundle.GetProperty("items").GetArrayLength());
        Assert.Equal(expected.HasAuthority, bundle.TryGetProperty("observationSubject", out _));
        Assert.Equal(expected.Omission is not null, bundle.TryGetProperty("omissionReason", out var omission));
        if (expected.Omission is not null)
        {
            Assert.Equal(expected.Omission, omission.GetString());
        }
    }

    private static void AssertProjection(
        JsonElement result,
        string reason,
        string resolution,
        string? expectation,
        string? observation,
        string bundleStatus,
        string? omission)
    {
        Assert.Equal(reason, result.GetProperty("reasonCode").GetString());
        Assert.Equal(resolution, result.GetProperty("policyResolution").GetString());
        AssertNullableString(expectation, result.GetProperty("policyExpectation"));
        AssertNullableString(observation, result.GetProperty("documentationObservation"));
        var bundle = result.GetProperty("evidenceBundle");
        Assert.Equal(bundleStatus, bundle.GetProperty("availabilityStatus").GetString());
        Assert.Equal(omission is not null, bundle.TryGetProperty("omissionReason", out var actual));
        if (omission is not null)
        {
            Assert.Equal(omission, actual.GetString());
        }
    }

    private static void AssertNullableString(string? expected, JsonElement actual)
    {
        if (expected is null)
        {
            Assert.Equal(JsonValueKind.Null, actual.ValueKind);
        }
        else
        {
            Assert.Equal(expected, actual.GetString());
        }
    }

    private static TargetScenario CreateSupportedTarget(
        TargetProfile profile = TargetProfile.ExternalApi) =>
        CreateTarget(PrimarySymbolKind.Class, profile);

    private static TargetScenario CreateUnsupportedTarget() =>
        CreateTarget(PrimarySymbolKind.Unknown, TargetProfile.ExternalApi);

    private static ComponentScenario CreateSupportedComponent()
    {
        const string context = "synthetic.v1";
        const string documentationCommentId =
            "M:AuditFixtures.Widget.Run(System.String)";
        var buffer = new ClassificationCandidateBuffer();
        buffer.AddTarget(
            context,
            documentationCommentId,
            PrimarySymbolKind.Method,
            ImmutableArray<SymbolTrait>.Empty,
            ClassificationOrigin.Source,
            [ClassificationInput.RepositoryLocator("src/Widget.cs")]);
        buffer.AddComponent(
            context,
            documentationCommentId,
            ComponentKind.Parameter,
            "parameter/0",
            ClassificationOrigin.Source);
        var set = Assert.IsType<ClassificationSet>(
            buffer.Normalize(TargetProfile.ExternalApi).ClassificationSet);
        return new ComponentScenario(
            set,
            Assert.Single(set.Targets),
            Assert.Single(set.Components));
    }

    private static TargetScenario CreateTarget(
        PrimarySymbolKind kind,
        TargetProfile profile)
    {
        var buffer = new ClassificationCandidateBuffer();
        buffer.AddTarget(
            "synthetic.v1",
            "T:AuditFixtures.Widget",
            kind,
            ImmutableArray<SymbolTrait>.Empty,
            ClassificationOrigin.Source,
            [ClassificationInput.RepositoryLocator("src/Widget.cs")]);
        var set = Assert.IsType<ClassificationSet>(buffer.Normalize(profile).ClassificationSet);
        return new TargetScenario(set, Assert.Single(set.Targets));
    }

    private static PolicyDocumentV1 CreatePolicy(
        PolicyExpectation expectation,
        TargetProfile profile)
    {
        var json = $$"""
            {"schemaVersion":1,"targetProfile":"{{ClassificationVocabulary.GetId(profile)}}","defaultDecision":"{{PolicyConfigurationVocabulary.GetId(expectation)}}"}
            """;
        return Assert.IsType<PolicyDocumentV1>(
            PolicyConfigurationEvaluator.Parse(Encoding.UTF8.GetBytes(json)).Document);
    }

    private static PolicyDocumentV1 CreateConflictPolicy()
    {
        const string json = """
            {"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"optional","rules":[{"id":"required-a","priority":2,"decision":"required","sourcePaths":{"include":["src/A.cs"]}},{"id":"forbidden-b","priority":1,"decision":"forbidden","sourcePaths":{"include":["src/B.cs"]}}]}
            """;
        return Assert.IsType<PolicyDocumentV1>(
            PolicyConfigurationEvaluator.Parse(Encoding.UTF8.GetBytes(json)).Document);
    }

    private static PolicyContributionSet Evaluate(
        PolicyDocumentV1 policy,
        IEnumerable<PolicyContributionInput> inputs) =>
        Assert.IsType<PolicyContributionSet>(
            PolicyConfigurationEvaluator.Evaluate(policy, inputs).ContributionSet);

    private static BoundObservationEvidence CreateBoundEvidence(
        TargetScenario scenario,
        DocumentationObservationValue value)
    {
        const string documentation = "/// <summary>Documented.</summary>\n";
        const string body = "public class Widget { }";
        var original = value == DocumentationObservationValue.Present
            ? documentation
            : body;
        var declaration = CreateDeclaration(value);
        var observationBuffer = new DocumentationObservationCandidateBuffer(
            scenario.Classifications);
        observationBuffer.AddTarget(scenario.Target, true, [declaration]);
        var observation = Assert.Single(Assert.IsType<DocumentationObservationSet>(
            observationBuffer.Normalize().ObservationSet).Observations);
        var evidenceId = value == DocumentationObservationValue.Present
            ? "evidence.xml-doc"
            : "evidence.declaration";
        var bundle = Assert.IsType<EvidenceBundle>(EvidenceNormalizer.Normalize([
            EvidenceInput.Candidate(
                evidenceId,
                EvidenceInput.TargetSubject("synthetic.v1", "T:AuditFixtures.Widget"),
                value == DocumentationObservationValue.Present
                    ? EvidenceKind.SourceXmlDocumentation
                    : EvidenceKind.SourceDeclaration,
                value == DocumentationObservationValue.Present
                    ? EvidenceRelation.Documents
                    : EvidenceRelation.Declares,
                original,
                EvidenceInput.RepositoryLocator("src/Widget.cs", 0, original.Length)),
        ]).Bundle);
        var binding = EvidenceBindingInput.Declaration(
            declaration.DeclarationId,
            value == DocumentationObservationValue.Absent ? evidenceId : null,
            value == DocumentationObservationValue.Present ? evidenceId : null);
        return Assert.IsType<BoundObservationEvidence>(
            EvidenceObservationBinder.Bind(observation, bundle, [binding]).Binding);
    }

    private static BoundObservationEvidence CreateSourceUnavailableEvidence(
        TargetScenario scenario)
    {
        var observationBuffer = new DocumentationObservationCandidateBuffer(
            scenario.Classifications);
        observationBuffer.AddTarget(scenario.Target, false, []);
        var observation = Assert.Single(Assert.IsType<DocumentationObservationSet>(
            observationBuffer.Normalize().ObservationSet).Observations);
        var bundle = Assert.IsType<EvidenceBundle>(EvidenceNormalizer.Normalize(
            [],
            [EvidenceOmissionReason.SourceUnavailable]).Bundle);
        return Assert.IsType<BoundObservationEvidence>(
            EvidenceObservationBinder.Bind(observation, bundle, []).Binding);
    }

    private static BoundObservationEvidence CreatePartialEvidence(TargetScenario scenario)
    {
        const string documentation = "/// <summary>Documented.</summary>\n";
        var declaration = CreateDeclaration(DocumentationObservationValue.Present);
        var observationBuffer = new DocumentationObservationCandidateBuffer(
            scenario.Classifications);
        observationBuffer.AddTarget(scenario.Target, true, [declaration]);
        var observation = Assert.Single(Assert.IsType<DocumentationObservationSet>(
            observationBuffer.Normalize().ObservationSet).Observations);
        var bundle = Assert.IsType<EvidenceBundle>(EvidenceNormalizer.Normalize(
            [
                EvidenceInput.Candidate(
                    "evidence.partial",
                    EvidenceInput.TargetSubject("synthetic.v1", "T:AuditFixtures.Widget"),
                    EvidenceKind.SourceXmlDocumentation,
                    EvidenceRelation.Documents,
                    documentation,
                    EvidenceInput.RepositoryLocator("src/Widget.cs", 0, documentation.Length)),
            ],
            budgets: EvidenceInput.Budgets(32, 4, 32768)).Bundle);
        return Assert.IsType<BoundObservationEvidence>(
            EvidenceObservationBinder.Bind(observation, bundle, []).Binding);
    }

    private static ComponentScenario CreateSkippedComponent()
    {
        const string context = "synthetic.v1";
        const string documentationCommentId = "P:AuditFixtures.Widget.Value";
        var buffer = new ClassificationCandidateBuffer();
        buffer.AddTarget(
            context,
            documentationCommentId,
            PrimarySymbolKind.Property,
            ImmutableArray<SymbolTrait>.Empty,
            ClassificationOrigin.Source,
            [ClassificationInput.RepositoryLocator("src/Widget.cs")]);
        buffer.AddComponent(
            context,
            documentationCommentId,
            ComponentKind.AccessorGet,
            "accessor/get",
            ClassificationOrigin.Source);
        var set = Assert.IsType<ClassificationSet>(
            buffer.Normalize(TargetProfile.ExternalApi).ClassificationSet);
        return new ComponentScenario(
            set,
            Assert.Single(set.Targets),
            Assert.Single(set.Components));
    }

    private static BoundObservationEvidence CreateMalformedComponentEvidence(
        ComponentScenario scenario)
    {
        const string documentation = "/// <summary>Malformed\n";
        const string body = "public void Run(string value) { }";
        var declarationText = documentation + body;
        var declaration = DocumentationObservationInput.RepositoryDeclaration(
            "decl." + new string('e', 64),
            DocumentationAuthorityRole.Ordinary,
            "project." + new string('b', 64),
            "src/Widget.cs",
            Sha256(declarationText),
            DocumentationObservationInput.Span(0, declarationText.Length),
            declarationText,
            DocumentationObservationInput.Span(0, documentation.Length),
            documentation,
            DocumentationObservationInput.Span(0, documentation.Length),
            documentation,
            DocumentationBlockState.Malformed,
            parentSubstantive: true,
            componentLocalName: "value",
            componentMatch: null);
        var observationBuffer = new DocumentationObservationCandidateBuffer(
            scenario.Classifications);
        observationBuffer.AddTarget(
            scenario.Target,
            true,
            [CreateDeclaration(DocumentationObservationValue.Absent)]);
        observationBuffer.AddComponent(scenario.Component, true, [declaration]);
        var observation = Assert.Single(
            Assert.IsType<DocumentationObservationSet>(
                observationBuffer.Normalize().ObservationSet).Observations,
            value => value.Subject.ComponentKind is not null);
        const string evidenceId = "evidence.malformed-xml";
        var bundle = Assert.IsType<EvidenceBundle>(EvidenceNormalizer.Normalize([
            EvidenceInput.Candidate(
                evidenceId,
                EvidenceInput.ComponentSubject(
                    "synthetic.v1",
                    "M:AuditFixtures.Widget.Run(System.String)",
                    ComponentKind.Parameter,
                    "parameter/0"),
                EvidenceKind.SourceXmlDocumentation,
                EvidenceRelation.Documents,
                documentation,
                EvidenceInput.RepositoryLocator(
                    "src/Widget.cs",
                    0,
                    documentation.Length)),
        ]).Bundle);
        return Assert.IsType<BoundObservationEvidence>(
            EvidenceObservationBinder.Bind(
                observation,
                bundle,
                [EvidenceBindingInput.Declaration(
                    declaration.DeclarationId,
                    declarationEvidenceId: null,
                    documentationEvidenceId: evidenceId)]).Binding);
    }

    private static BoundObservationEvidence CreatePartialMalformedComponentEvidence(
        ComponentScenario scenario)
    {
        const string documentation = "/// <summary>Malformed\n";
        const string body = "public void Run(string value) { }";
        var declarationText = documentation + body;
        var declaration = DocumentationObservationInput.RepositoryDeclaration(
            "decl." + new string('e', 64),
            DocumentationAuthorityRole.Ordinary,
            "project." + new string('b', 64),
            "src/Widget.cs",
            Sha256(declarationText),
            DocumentationObservationInput.Span(100, 100 + declarationText.Length),
            declarationText,
            DocumentationObservationInput.Span(100, 100 + documentation.Length),
            documentation,
            DocumentationObservationInput.Span(100, 100 + documentation.Length),
            documentation,
            DocumentationBlockState.Malformed,
            parentSubstantive: true,
            componentLocalName: "value",
            componentMatch: null);
        var observationBuffer = new DocumentationObservationCandidateBuffer(
            scenario.Classifications);
        observationBuffer.AddTarget(
            scenario.Target,
            true,
            [CreateDeclaration(DocumentationObservationValue.Absent)]);
        observationBuffer.AddComponent(scenario.Component, true, [declaration]);
        var observation = Assert.Single(
            Assert.IsType<DocumentationObservationSet>(
                observationBuffer.Normalize().ObservationSet).Observations,
            value => value.Subject.ComponentKind is not null);
        var bundle = Assert.IsType<EvidenceBundle>(EvidenceNormalizer.Normalize(
            [
                EvidenceInput.Candidate(
                    "evidence.partial-malformed",
                    EvidenceInput.ComponentSubject(
                        "synthetic.v1",
                        "M:AuditFixtures.Widget.Run(System.String)",
                        ComponentKind.Parameter,
                        "parameter/0"),
                    EvidenceKind.SourceXmlDocumentation,
                    EvidenceRelation.Documents,
                    documentation,
                    EvidenceInput.RepositoryLocator(
                        "src/Widget.cs",
                        100,
                        100 + documentation.Length)),
            ],
            budgets: EvidenceInput.Budgets(32, 4, 32768)).Bundle);
        return Assert.IsType<BoundObservationEvidence>(
            EvidenceObservationBinder.Bind(observation, bundle, []).Binding);
    }

    private static DocumentationDeclarationInput CreateDeclaration(
        DocumentationObservationValue value)
    {
        const string documentation = "/// <summary>Documented.</summary>\n";
        const string body = "public class Widget { }";
        var present = value == DocumentationObservationValue.Present;
        var leading = present ? documentation : string.Empty;
        var declaration = leading + body;
        return DocumentationObservationInput.RepositoryDeclaration(
            "decl." + new string('d', 64),
            DocumentationAuthorityRole.Ordinary,
            "project." + new string('b', 64),
            "src/Widget.cs",
            Sha256(declaration),
            DocumentationObservationInput.Span(0, declaration.Length),
            declaration,
            DocumentationObservationInput.Span(0, leading.Length),
            leading,
            present ? DocumentationObservationInput.Span(0, documentation.Length) : null,
            present ? documentation : null,
            present ? DocumentationBlockState.WellFormed : DocumentationBlockState.NoBlock,
            parentSubstantive: present);
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private enum PrecedenceClassificationState
    {
        Supported,
        Skipped,
    }

    private enum PrecedencePolicyState
    {
        Usable,
        Conflict,
        Unavailable,
    }

    private enum PrecedenceEvidenceState
    {
        OrdinaryPresent,
        SourceUnavailable,
        Partial,
        Malformed,
        PartialMalformed,
        Missing,
    }

    private sealed record ClosedProjection(
        string Outcome,
        string Reason,
        string Resolution,
        string? Expectation,
        string? Observation,
        string BundleStatus,
        string? Omission,
        int EvidenceIdCount,
        int EvidenceItemCount,
        bool HasAuthority)
    {
        public static ClosedProjection Skipped(string reason, string resolution) => new(
            "audit.outcome.skipped",
            reason,
            resolution,
            null,
            null,
            "evidence.bundle.unavailable",
            "evidence.omission.not-provided",
            EvidenceIdCount: 0,
            EvidenceItemCount: 0,
            HasAuthority: false);
    }

    private sealed record TargetScenario(
        ClassificationSet Classifications,
        TargetClassification Target);

    private sealed record ComponentScenario(
        ClassificationSet Classifications,
        TargetClassification Target,
        ComponentClassification Component);
}
