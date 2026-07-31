using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContractScribe.Core;

public abstract class AuditResultRecordInput
{
    private protected AuditResultRecordInput(
        object classification,
        PolicyContributionSet policyContributions,
        BoundObservationEvidence? evidence)
    {
        ClassificationRecord = classification;
        PolicyContributions = policyContributions;
        Evidence = evidence;
    }

    public PolicyContributionSet PolicyContributions { get; }

    public BoundObservationEvidence? Evidence { get; }

    internal object ClassificationRecord { get; }
}

public sealed class TargetAuditResultInput : AuditResultRecordInput
{
    internal TargetAuditResultInput(
        TargetClassification classification,
        PolicyContributionSet policyContributions,
        BoundObservationEvidence? evidence)
        : base(classification, policyContributions, evidence)
    {
        Classification = classification;
    }

    public TargetClassification Classification { get; }
}

public sealed class ComponentAuditResultInput : AuditResultRecordInput
{
    internal ComponentAuditResultInput(
        ComponentClassification classification,
        PolicyContributionSet policyContributions,
        BoundObservationEvidence? evidence)
        : base(classification, policyContributions, evidence)
    {
        Classification = classification;
    }

    public ComponentClassification Classification { get; }
}

public sealed class UnresolvedAuditResultInput : AuditResultRecordInput
{
    internal UnresolvedAuditResultInput(
        UnresolvedClassification classification,
        PolicyContributionSet policyContributions,
        BoundObservationEvidence? evidence)
        : base(classification, policyContributions, evidence)
    {
        Classification = classification;
    }

    public UnresolvedClassification Classification { get; }
}

public static class AuditResultInput
{
    public static TargetAuditResultInput Target(
        TargetClassification classification,
        PolicyContributionSet policyContributions,
        BoundObservationEvidence? evidence = null) =>
        new(
            classification ?? throw new ArgumentNullException(nameof(classification)),
            policyContributions ?? throw new ArgumentNullException(nameof(policyContributions)),
            evidence);

    public static ComponentAuditResultInput Component(
        ComponentClassification classification,
        PolicyContributionSet policyContributions,
        BoundObservationEvidence? evidence = null) =>
        new(
            classification ?? throw new ArgumentNullException(nameof(classification)),
            policyContributions ?? throw new ArgumentNullException(nameof(policyContributions)),
            evidence);

    public static UnresolvedAuditResultInput Unresolved(
        UnresolvedClassification classification,
        PolicyContributionSet policyContributions,
        BoundObservationEvidence? evidence = null) =>
        new(
            classification ?? throw new ArgumentNullException(nameof(classification)),
            policyContributions ?? throw new ArgumentNullException(nameof(policyContributions)),
            evidence);
}

public static class AuditResultAggregator
{
    public static AuditResultDocument Aggregate(
        TargetProfile requestedTargetProfile,
        ClassificationSet classifications,
        PolicyDocumentV1 acceptedPolicy,
        IEnumerable<AuditResultRecordInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(classifications);
        ArgumentNullException.ThrowIfNull(acceptedPolicy);
        ArgumentNullException.ThrowIfNull(inputs);
        Require(
            Enum.IsDefined(requestedTargetProfile)
            && classifications.TargetProfile == requestedTargetProfile
            && acceptedPolicy.TargetProfile == requestedTargetProfile,
            AuditResultValidationCode.TargetProfileMismatch,
            "Policy, classification, and requested Audit Result target profiles must match.");

        var expected = ExpectedClassifications(classifications);
        var materialized = new Dictionary<string, AuditResultRecordInput>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            if (input is null)
            {
                throw Failure(
                    AuditResultValidationCode.InvalidShape,
                    "Audit Result inputs cannot contain null records.");
            }

            Require(
                input.PolicyContributions.TargetProfile == requestedTargetProfile,
                AuditResultValidationCode.TargetProfileMismatch,
                "A Policy contribution set has a different target profile.");
            RequireAcceptedPolicyContributions(
                acceptedPolicy,
                input.PolicyContributions);
            var key = SubjectKey(input.ClassificationRecord);
            Require(
                expected.TryGetValue(key, out var classification)
                && Equals(classification, input.ClassificationRecord),
                AuditResultValidationCode.InvalidClassification,
                "An aggregation input is not a member of the accepted ClassificationSet.");
            Require(
                materialized.TryAdd(key, input),
                AuditResultValidationCode.InvalidClassification,
                "A classification has more than one aggregation input.");
        }

        Require(
            materialized.Count == expected.Count
            && expected.Keys.All(materialized.ContainsKey),
            AuditResultValidationCode.InvalidClassification,
            "Every accepted classification must produce exactly one Audit Result input.");

        var results = new JsonArray();
        foreach (var input in materialized.Values)
        {
            results.Add(CreateResult(input));
        }

        var root = new JsonObject
        {
            ["auditResultVersion"] = AuditResultVocabulary.AuditResultVersion,
            ["policyConfigurationVersion"] = PolicyConfigurationVocabulary.SchemaVersion,
            ["taxonomyRegistryVersion"] = 1,
            ["targetProfile"] = ClassificationVocabulary.GetId(requestedTargetProfile),
            ["results"] = results,
        };
        using var parsed = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(root));
        var document = parsed.RootElement.Clone();
        AuditResultJsonModel.Validate(
            document,
            originalEvidence: null,
            requireOriginalEvidence: false,
            trustSourceValidatedTruncation: true);
        return new AuditResultDocument(document);
    }

    private static void RequireAcceptedPolicyContributions(
        PolicyDocumentV1 acceptedPolicy,
        PolicyContributionSet contributionSet)
    {
        Require(
            !contributionSet.Contributions.IsDefault,
            AuditResultValidationCode.InvalidPolicy,
            "The Policy contribution set is not initialized.");
        var inputs = contributionSet.Contributions.Select<
            PolicyContribution,
            PolicyContributionInput>(contribution => contribution switch
            {
                RepositoryPolicyContribution repository =>
                    PolicyConfigurationInput.Repository(
                        repository.ProjectPath,
                        repository.SourcePath),
                GeneratedPolicyContribution generated =>
                    PolicyConfigurationInput.Generated(
                        generated.ProjectPath,
                        PolicyConfigurationVocabulary.GetId(
                            generated.GeneratedOutput.ProducerKind),
                        generated.GeneratedOutput.ProducerId,
                        generated.GeneratedOutput.OutputId),
                _ => throw Failure(
                    AuditResultValidationCode.InvalidPolicy,
                    "Unknown Policy contribution type."),
            });
        var reevaluated = PolicyConfigurationEvaluator.Evaluate(
            acceptedPolicy,
            inputs);
        Require(
            reevaluated.Status == PolicyRunStatus.Success
            && reevaluated.ContributionSet is { } accepted
            && accepted.TargetProfile == contributionSet.TargetProfile
            && accepted.Contributions.SequenceEqual(
                contributionSet.Contributions),
            AuditResultValidationCode.InvalidPolicy,
            "Policy contributions were not produced by the accepted Policy document.");
    }

    private static JsonObject CreateResult(AuditResultRecordInput input)
    {
        var contributions = input.PolicyContributions.Contributions;
        Require(
            !contributions.IsDefault,
            AuditResultValidationCode.InvalidPolicy,
            "The Policy contribution set is not initialized.");
        var expectations = contributions
            .Select(contribution => contribution.Expectation)
            .Distinct()
            .ToArray();
        Require(
            contributions.All(contribution => contribution is not null)
            && contributions.All(contribution => Enum.IsDefined(contribution.Expectation)),
            AuditResultValidationCode.InvalidPolicy,
            "The Policy contribution set contains an invalid contribution.");

        var supported = input.ClassificationRecord switch
        {
            TargetClassification target => target.SupportStatus == SupportStatus.Supported,
            ComponentClassification component => component.SupportStatus == SupportStatus.Supported,
            UnresolvedClassification => false,
            _ => throw Failure(
                AuditResultValidationCode.InvalidClassification,
                "Unknown classification record type."),
        };
        var resolution = !supported || contributions.IsEmpty
            ? AuditPolicyResolution.Unavailable
            : expectations.Length > 1
                ? AuditPolicyResolution.Conflict
                : contributions.Length == 1
                    ? AuditPolicyResolution.Single
                    : AuditPolicyResolution.AllDeclarationsAgree;
        var expectation = resolution is AuditPolicyResolution.Conflict or
            AuditPolicyResolution.Unavailable
            ? (PolicyExpectation?)null
            : expectations.Single();

        DocumentationObservationValue? observation;
        AuditOutcome outcome;
        AuditReason reason;
        BoundObservationEvidence? selectedEvidence;
        if (!supported)
        {
            observation = null;
            outcome = AuditOutcome.Skipped;
            reason = AuditReason.ClassificationSkipped;
            selectedEvidence = null;
        }
        else if (resolution == AuditPolicyResolution.Conflict)
        {
            observation = null;
            outcome = AuditOutcome.Skipped;
            reason = AuditReason.PolicyConflict;
            selectedEvidence = null;
        }
        else if (resolution == AuditPolicyResolution.Unavailable)
        {
            observation = null;
            outcome = AuditOutcome.Skipped;
            reason = AuditReason.PolicyUnavailable;
            selectedEvidence = null;
        }
        else
        {
            Require(
                input.Evidence is not null,
                AuditResultValidationCode.InvalidEvidence,
                "A supported classification with usable Policy requires bounded observation evidence.");
            var evidence = input.Evidence!;
            Require(
                Enum.IsDefined(evidence.ObservationValue)
                && Enum.IsDefined(evidence.Bundle.AvailabilityStatus),
                AuditResultValidationCode.InvalidEvidence,
                "Bound observation evidence contains an unknown value.");
            selectedEvidence = evidence;
            if (evidence.Bundle.AvailabilityStatus == EvidenceAvailabilityStatus.Unavailable)
            {
                observation = DocumentationObservationValue.Unavailable;
                outcome = AuditOutcome.Skipped;
                reason = AuditReason.DocumentationUnavailable;
            }
            else if (evidence.Bundle.AvailabilityStatus == EvidenceAvailabilityStatus.Partial)
            {
                observation = DocumentationObservationValue.Unavailable;
                outcome = AuditOutcome.Skipped;
                reason = AuditReason.EvidenceIncomplete;
            }
            else if (evidence.ObservationValue == DocumentationObservationValue.Unavailable)
            {
                observation = DocumentationObservationValue.Unavailable;
                outcome = AuditOutcome.Skipped;
                reason = AuditReason.DocumentationUnavailableMalformedXml;
            }
            else
            {
                Require(
                    evidence.SupportsOrdinaryResult,
                    AuditResultValidationCode.InvalidEvidence,
                    "Bound evidence does not authorize an ordinary audit outcome.");
                observation = evidence.ObservationValue;
                (outcome, reason) = DeriveMatrix(expectation!.Value, observation.Value);
            }
        }

        var result = new JsonObject
        {
            ["classification"] = SerializeClassification(input.ClassificationRecord),
            ["policyContributions"] = SerializePolicyContributions(contributions),
            ["policyExpectation"] = expectation is { } policyExpectation
                ? PolicyConfigurationVocabulary.GetId(policyExpectation)
                : null,
            ["policyResolution"] = AuditResultVocabulary.GetId(resolution),
            ["documentationObservation"] = observation is { } observationValue
                ? DocumentationObservationVocabulary.GetId(observationValue)
                : null,
            ["auditOutcome"] = AuditResultVocabulary.GetId(outcome),
            ["reasonCode"] = AuditResultVocabulary.GetId(reason),
        };

        if (selectedEvidence is null)
        {
            result["evidenceIds"] = new JsonArray();
            result["evidenceBundle"] = SerializeUnavailableBundle(
                reason == AuditReason.DocumentationUnavailable
                    ? EvidenceOmissionReason.SourceUnavailable
                    : EvidenceOmissionReason.NotProvided);
            return result;
        }

        result["evidenceIds"] = SerializeEvidenceIds(selectedEvidence.EvidenceIds);
        if (selectedEvidence.Authority is { } authority)
        {
            result["evidenceAuthority"] = SerializeAuthority(authority);
        }

        result["evidenceBundle"] = SerializeEvidenceBundle(selectedEvidence.Bundle);
        return result;
    }

    private static (AuditOutcome Outcome, AuditReason Reason) DeriveMatrix(
        PolicyExpectation expectation,
        DocumentationObservationValue observation) =>
        (expectation, observation) switch
        {
            (PolicyExpectation.Required, DocumentationObservationValue.Present) =>
                (AuditOutcome.Compliant, AuditReason.RequiredPresent),
            (PolicyExpectation.Required, DocumentationObservationValue.Absent) =>
                (AuditOutcome.Violation, AuditReason.RequiredAbsent),
            (PolicyExpectation.Optional, DocumentationObservationValue.Present) =>
                (AuditOutcome.Compliant, AuditReason.OptionalPresent),
            (PolicyExpectation.Optional, DocumentationObservationValue.Absent) =>
                (AuditOutcome.Compliant, AuditReason.OptionalAbsent),
            (PolicyExpectation.Forbidden, DocumentationObservationValue.Present) =>
                (AuditOutcome.Violation, AuditReason.ForbiddenPresent),
            (PolicyExpectation.Forbidden, DocumentationObservationValue.Absent) =>
                (AuditOutcome.Compliant, AuditReason.ForbiddenAbsent),
            _ => throw Failure(
                AuditResultValidationCode.InvalidOutcome,
                "The ordinary Policy/observation matrix combination is invalid."),
        };

    private static JsonObject SerializeClassification(object classification) => classification switch
    {
        TargetClassification target => SerializeTargetClassification(target),
        ComponentClassification component => SerializeComponentClassification(component),
        UnresolvedClassification unresolved => SerializeUnresolvedClassification(unresolved),
        _ => throw Failure(
            AuditResultValidationCode.InvalidClassification,
            "Unknown classification record type."),
    };

    private static JsonObject SerializeTargetClassification(TargetClassification target)
    {
        var traits = new JsonArray();
        foreach (var trait in target.Traits)
        {
            traits.Add(ClassificationVocabulary.GetId(trait));
        }

        var value = new JsonObject
        {
            ["recordType"] = "TargetClassification",
            ["symbolRef"] = SerializeSymbolRef(target.SymbolRef),
            ["primaryKind"] = ClassificationVocabulary.GetId(target.PrimaryKind),
            ["traits"] = traits,
            ["origin"] = ClassificationVocabulary.GetId(target.Origin),
            ["supportStatus"] = ClassificationVocabulary.GetId(target.SupportStatus),
        };
        if (target.SkipReason is { } skip)
        {
            value["skipReason"] = ClassificationVocabulary.GetId(skip);
        }

        return value;
    }

    private static JsonObject SerializeComponentClassification(ComponentClassification component)
    {
        var value = new JsonObject
        {
            ["recordType"] = "ComponentClassification",
            ["parentSymbolRef"] = SerializeSymbolRef(component.ParentSymbolRef),
            ["componentKind"] = ClassificationVocabulary.GetId(component.ComponentKind),
            ["identity"] = component.Identity,
            ["origin"] = ClassificationVocabulary.GetId(component.Origin),
            ["supportStatus"] = ClassificationVocabulary.GetId(component.SupportStatus),
        };
        if (component.SkipReason is { } skip)
        {
            value["skipReason"] = ClassificationVocabulary.GetId(skip);
        }

        return value;
    }

    private static JsonObject SerializeUnresolvedClassification(UnresolvedClassification unresolved) =>
        new()
        {
            ["recordType"] = "UnresolvedClassification",
            ["compilationContextRef"] = unresolved.CompilationContextRef,
            ["origin"] = ClassificationVocabulary.GetId(unresolved.Origin),
            ["supportStatus"] = ClassificationVocabulary.GetId(unresolved.SupportStatus),
            ["skipReason"] = ClassificationVocabulary.GetId(unresolved.SkipReason),
            ["candidateLocator"] = SerializeCandidateLocator(unresolved.CandidateLocator),
        };

    private static JsonObject SerializeCandidateLocator(CandidateLocator locator) => locator switch
    {
        RepositoryCandidateLocator repository => new JsonObject
        {
            ["repository"] = WithSpan(
                new JsonObject { ["path"] = repository.Path },
                repository.Span),
        },
        GeneratedSourceCandidateLocator generated => new JsonObject
        {
            ["generatedSource"] = WithSpan(
                new JsonObject
                {
                    ["generatorId"] = generated.GeneratorId,
                    ["hintNameId"] = generated.HintNameId,
                },
                generated.Span),
        },
        ToolGeneratedCandidateLocator generated => new JsonObject
        {
            ["toolGenerated"] = WithSpan(
                new JsonObject
                {
                    ["producerId"] = generated.ProducerId,
                    ["outputId"] = generated.OutputId,
                },
                generated.Span),
        },
        SyntheticCandidateLocator synthetic => new JsonObject
        {
            ["synthetic"] = new JsonObject { ["fixtureId"] = synthetic.FixtureId },
        },
        _ => throw Failure(
            AuditResultValidationCode.InvalidClassification,
            "Unknown candidate locator type."),
    };

    private static JsonArray SerializePolicyContributions(
        IEnumerable<PolicyContribution> contributions)
    {
        var values = new JsonArray();
        foreach (var contribution in contributions)
        {
            var value = new JsonObject
            {
                ["projectPath"] = contribution.ProjectPath,
            };
            switch (contribution)
            {
                case RepositoryPolicyContribution repository:
                    value["sourcePath"] = repository.SourcePath;
                    break;
                case GeneratedPolicyContribution generated:
                    value["generatedOutput"] = new JsonObject
                    {
                        ["producerKind"] = PolicyConfigurationVocabulary.GetId(
                            generated.GeneratedOutput.ProducerKind),
                        ["producerId"] = generated.GeneratedOutput.ProducerId,
                        ["outputId"] = generated.GeneratedOutput.OutputId,
                    };
                    break;
                default:
                    throw Failure(
                        AuditResultValidationCode.InvalidPolicy,
                        "Unknown Policy contribution type.");
            }

            value["policyExpectation"] = PolicyConfigurationVocabulary.GetId(
                contribution.Expectation);
            value["matchedRuleId"] = contribution.MatchedRuleId;
            values.Add(value);
        }

        return values;
    }

    private static JsonArray SerializeEvidenceIds(IEnumerable<string> evidenceIds)
    {
        var values = new JsonArray();
        foreach (var evidenceId in evidenceIds)
        {
            values.Add(evidenceId);
        }

        return values;
    }

    private static JsonObject SerializeAuthority(EvidenceAuthoritySet authority)
    {
        var declarations = new JsonArray();
        foreach (var row in authority.Declarations)
        {
            var declaration = new JsonObject
            {
                ["declarationId"] = row.DeclarationId,
                ["authorityRole"] = AuthorityRoleId(row.AuthorityRole),
                ["blockState"] = BlockStateId(row.BlockState),
                ["evidenceId"] = row.EvidenceId,
            };
            if (row.ComponentLocalName is not null)
            {
                declaration["componentLocalName"] = row.ComponentLocalName;
            }

            if (row.ComponentMatch is { } componentMatch)
            {
                declaration["componentMatch"] = componentMatch switch
                {
                    DocumentationComponentMatch.Present => "present",
                    DocumentationComponentMatch.Absent => "absent",
                    _ => throw Failure(
                        AuditResultValidationCode.InvalidAuthority,
                        "Unknown authority component match."),
                };
            }

            declarations.Add(declaration);
        }

        return new JsonObject
        {
            ["declarationSetId"] = authority.DeclarationSetId,
            ["completeness"] = authority.Completeness switch
            {
                EvidenceAuthorityCompleteness.Complete => "complete",
                EvidenceAuthorityCompleteness.PositiveOnly => "positive-only",
                _ => throw Failure(
                    AuditResultValidationCode.InvalidAuthority,
                    "Unknown evidence authority completeness."),
            },
            ["declarations"] = declarations,
        };
    }

    private static JsonObject SerializeEvidenceBundle(EvidenceBundle bundle)
    {
        var items = new JsonArray();
        foreach (var item in bundle.Items)
        {
            items.Add(SerializeEvidenceItem(item));
        }

        var value = new JsonObject
        {
            ["evidenceBundleVersion"] = bundle.EvidenceBundleVersion,
            ["availabilityStatus"] = EvidenceVocabulary.GetId(bundle.AvailabilityStatus),
        };
        if (bundle.OmissionReason is { } omission)
        {
            value["omissionReason"] = EvidenceVocabulary.GetId(omission);
        }

        value["items"] = items;
        if (bundle.ObservationSubject is { } observation)
        {
            value["observationSubject"] = new JsonObject
            {
                ["observationSubjectRef"] = observation.ObservationSubjectRef,
                ["compilationContextRef"] = observation.CompilationContextRef,
                ["subject"] = SerializeEvidenceSubject(observation.Subject),
                ["authoritativeDeclarationSetDigest"] =
                    observation.AuthoritativeDeclarationSetDigest,
                ["authoritativeDeclarationCount"] =
                    observation.AuthoritativeDeclarationCount,
            };
        }

        return value;
    }

    private static JsonObject SerializeUnavailableBundle(EvidenceOmissionReason reason) =>
        new()
        {
            ["evidenceBundleVersion"] = EvidenceVocabulary.BundleVersion,
            ["availabilityStatus"] = EvidenceVocabulary.GetId(
                EvidenceAvailabilityStatus.Unavailable),
            ["omissionReason"] = EvidenceVocabulary.GetId(reason),
            ["items"] = new JsonArray(),
        };

    private static JsonObject SerializeEvidenceItem(EvidenceItem item) =>
        new()
        {
            ["evidenceId"] = item.EvidenceId,
            ["subject"] = SerializeEvidenceSubject(item.Subject),
            ["kind"] = EvidenceVocabulary.GetId(item.Kind),
            ["relation"] = EvidenceVocabulary.GetId(item.Relation),
            ["excerpt"] = item.Excerpt,
            ["sha256"] = item.Sha256,
            ["originalUtf8ByteCount"] = item.OriginalUtf8ByteCount,
            ["includedUtf8ByteCount"] = item.IncludedUtf8ByteCount,
            ["omittedUtf8ByteCount"] = item.OmittedUtf8ByteCount,
            ["isTruncated"] = item.IsTruncated,
            ["locator"] = SerializeEvidenceLocator(item.Locator),
        };

    private static JsonObject SerializeEvidenceSubject(EvidenceSubject subject) => subject switch
    {
        TargetEvidenceSubject target => SerializeSymbolRef(target.ParentSymbolRef),
        ComponentEvidenceSubject component => new JsonObject
        {
            ["parentSymbolRef"] = SerializeSymbolRef(component.ParentSymbolRef),
            ["componentKind"] = ClassificationVocabulary.GetId(component.ComponentKind),
            ["identity"] = component.Identity,
        },
        _ => throw Failure(
            AuditResultValidationCode.InvalidEvidence,
            "Unknown evidence subject type."),
    };

    private static JsonObject SerializeEvidenceLocator(EvidenceLocator locator) => locator switch
    {
        RepositoryEvidenceLocator repository => new JsonObject
        {
            ["repository"] = WithSpan(
                new JsonObject { ["path"] = repository.Path },
                repository.Span),
        },
        GeneratedOutputEvidenceLocator generated => new JsonObject
        {
            ["generatedOutput"] = WithSpan(
                new JsonObject
                {
                    ["producerKind"] = PolicyConfigurationVocabulary.GetId(
                        generated.ProducerKind),
                    ["producerId"] = generated.ProducerId,
                    ["outputId"] = generated.OutputId,
                    ["sourceSha256"] = generated.SourceSha256,
                },
                generated.Span),
        },
        MetadataEvidenceLocator metadata => new JsonObject
        {
            ["metadata"] = new JsonObject
            {
                ["assemblyIdentity"] = metadata.AssemblyIdentity,
                ["documentationCommentId"] = metadata.DocumentationCommentId,
            },
        },
        SyntheticEvidenceLocator synthetic => new JsonObject
        {
            ["synthetic"] = new JsonObject { ["fixtureId"] = synthetic.FixtureId },
        },
        _ => throw Failure(
            AuditResultValidationCode.InvalidEvidence,
            "Unknown evidence locator type."),
    };

    private static JsonObject SerializeSymbolRef(SymbolRef symbolRef) =>
        new()
        {
            ["compilationContextRef"] = symbolRef.CompilationContextRef,
            ["documentationCommentId"] = symbolRef.DocumentationCommentId,
        };

    private static JsonObject WithSpan(JsonObject value, Utf16Span? span)
    {
        if (span is { } present)
        {
            value["span"] = new JsonObject
            {
                ["start"] = present.Start,
                ["end"] = present.End,
            };
        }

        return value;
    }

    private static Dictionary<string, object> ExpectedClassifications(
        ClassificationSet classifications)
    {
        var expected = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var classification in classifications.Targets.Cast<object>()
            .Concat(classifications.Components)
            .Concat(classifications.Unresolved))
        {
            Require(
                expected.TryAdd(SubjectKey(classification), classification),
                AuditResultValidationCode.InvalidClassification,
                "The accepted ClassificationSet contains duplicate result subjects.");
        }

        return expected;
    }

    private static string SubjectKey(object classification) => classification switch
    {
        TargetClassification target => string.Join(
            "\0",
            "target",
            target.SymbolRef.CompilationContextRef,
            target.SymbolRef.DocumentationCommentId),
        ComponentClassification component => string.Join(
            "\0",
            "component",
            component.ParentSymbolRef.CompilationContextRef,
            component.ParentSymbolRef.DocumentationCommentId,
            ClassificationVocabulary.GetId(component.ComponentKind),
            component.Identity),
        UnresolvedClassification unresolved => string.Join(
            "\0",
            "unresolved",
            unresolved.CompilationContextRef,
            CandidateLocatorKey(unresolved.CandidateLocator)),
        _ => throw Failure(
            AuditResultValidationCode.InvalidClassification,
            "Unknown classification record type."),
    };

    private static string CandidateLocatorKey(CandidateLocator locator) => locator switch
    {
        RepositoryCandidateLocator repository => string.Join(
            "\0",
            "repository",
            repository.Path,
            SpanKey(repository.Span)),
        GeneratedSourceCandidateLocator generated => string.Join(
            "\0",
            "generated-source",
            generated.GeneratorId,
            generated.HintNameId,
            SpanKey(generated.Span)),
        ToolGeneratedCandidateLocator generated => string.Join(
            "\0",
            "tool-generated",
            generated.ProducerId,
            generated.OutputId,
            SpanKey(generated.Span)),
        SyntheticCandidateLocator synthetic => "synthetic\0" + synthetic.FixtureId,
        _ => throw Failure(
            AuditResultValidationCode.InvalidClassification,
            "Unknown candidate locator type."),
    };

    private static string SpanKey(Utf16Span? span) => span is { } value
        ? string.Concat(
            value.Start.ToString(CultureInfo.InvariantCulture),
            "\0",
            value.End.ToString(CultureInfo.InvariantCulture))
        : "absent";

    private static string AuthorityRoleId(DocumentationAuthorityRole role) => role switch
    {
        DocumentationAuthorityRole.Ordinary => "ordinary",
        DocumentationAuthorityRole.PartialTypePart => "partial-type-part",
        DocumentationAuthorityRole.PartialMemberImplementing => "partial-member-implementing",
        DocumentationAuthorityRole.PartialMemberDefiningFallback =>
            "partial-member-defining-fallback",
        _ => throw Failure(
            AuditResultValidationCode.InvalidAuthority,
            "Unknown documentation authority role."),
    };

    private static string BlockStateId(DocumentationBlockState state) => state switch
    {
        DocumentationBlockState.NoBlock => "no-block",
        DocumentationBlockState.WhitespaceOnly => "whitespace-only",
        DocumentationBlockState.WellFormed => "well-formed",
        DocumentationBlockState.Malformed => "malformed",
        _ => throw Failure(
            AuditResultValidationCode.InvalidAuthority,
            "Unknown documentation block state."),
    };

    private static AuditResultValidationException Failure(
        AuditResultValidationCode code,
        string message) => AuditResultJsonModel.Failure(code, message);

    private static void Require(
        bool condition,
        AuditResultValidationCode code,
        string message)
    {
        if (!condition)
        {
            throw Failure(code, message);
        }
    }
}
