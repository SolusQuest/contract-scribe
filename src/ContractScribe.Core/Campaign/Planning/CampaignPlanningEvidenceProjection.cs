using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

namespace ContractScribe.Core;

internal static class CampaignPlanningEvidenceProjection
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static long EstimateCanonicalBytes(EvidenceBundle bundle)
    {
        long bytes = 256;
        bytes = Add(bytes, EvidenceVocabulary.GetId(bundle.AvailabilityStatus));
        if (bundle.OmissionReason is { } omission)
        {
            bytes = Add(bytes, EvidenceVocabulary.GetId(omission));
        }

        foreach (var item in bundle.Items)
        {
            bytes = checked(bytes + 256);
            bytes = Add(bytes, item.EvidenceId);
            bytes = AddSubject(bytes, item.Subject);
            bytes = Add(bytes, EvidenceVocabulary.GetId(item.Kind));
            bytes = Add(bytes, EvidenceVocabulary.GetId(item.Relation));
            bytes = Add(bytes, item.Excerpt);
            bytes = Add(bytes, item.Sha256);
            bytes = AddLocator(bytes, item.Locator);
        }

        if (bundle.ObservationSubject is { } observation)
        {
            bytes = checked(bytes + 128);
            bytes = Add(bytes, observation.ObservationSubjectRef);
            bytes = Add(bytes, observation.CompilationContextRef);
            bytes = AddSubject(bytes, observation.Subject);
            bytes = Add(bytes, observation.AuthoritativeDeclarationSetDigest);
        }

        return bytes;
    }

    internal static bool Matches(EvidenceBundle expected, JsonElement actual) =>
        JsonNode.DeepEquals(SerializeEvidenceBundle(expected), JsonNode.Parse(actual.GetRawText()));

    internal static bool Equivalent(
        BoundObservationEvidence left,
        BoundObservationEvidence right) =>
        left.ObservationValue == right.ObservationValue
        && left.SupportsOrdinaryResult == right.SupportsOrdinaryResult
        && left.EvidenceIds.SequenceEqual(right.EvidenceIds, StringComparer.Ordinal)
        && AuthorityEquivalent(left.Authority, right.Authority)
        && JsonNode.DeepEquals(
            SerializeEvidenceBundle(left.Bundle),
            SerializeEvidenceBundle(right.Bundle));

    internal static bool MatchesUnavailable(
        JsonElement actual,
        EvidenceOmissionReason reason) =>
        JsonNode.DeepEquals(
            new JsonObject
            {
                ["evidenceBundleVersion"] = EvidenceVocabulary.BundleVersion,
                ["availabilityStatus"] = EvidenceVocabulary.GetId(EvidenceAvailabilityStatus.Unavailable),
                ["omissionReason"] = EvidenceVocabulary.GetId(reason),
                ["items"] = new JsonArray(),
            },
            JsonNode.Parse(actual.GetRawText()));

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
                ["authoritativeDeclarationSetDigest"] = observation.AuthoritativeDeclarationSetDigest,
                ["authoritativeDeclarationCount"] = observation.AuthoritativeDeclarationCount,
            };
        }

        return value;
    }

    private static bool AuthorityEquivalent(
        EvidenceAuthoritySet? left,
        EvidenceAuthoritySet? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.DeclarationSetId == right.DeclarationSetId
            && left.Completeness == right.Completeness
            && left.Declarations.Length == right.Declarations.Length
            && left.Declarations.Zip(right.Declarations).All(pair =>
                pair.First.DeclarationId == pair.Second.DeclarationId
                && pair.First.AuthorityRole == pair.Second.AuthorityRole
                && pair.First.BlockState == pair.Second.BlockState
                && pair.First.EvidenceId == pair.Second.EvidenceId
                && pair.First.ComponentLocalName == pair.Second.ComponentLocalName
                && pair.First.ComponentMatch == pair.Second.ComponentMatch);
    }

    private static JsonObject SerializeEvidenceItem(EvidenceItem item) => new()
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
        _ => throw InvalidEvidence(),
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
                    ["producerKind"] = PolicyConfigurationVocabulary.GetId(generated.ProducerKind),
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
        _ => throw InvalidEvidence(),
    };

    private static JsonObject SerializeSymbolRef(SymbolRef symbolRef) => new()
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

    private static long AddSubject(long bytes, EvidenceSubject subject) => subject switch
    {
        TargetEvidenceSubject target => AddSymbolRef(bytes, target.ParentSymbolRef),
        ComponentEvidenceSubject component => Add(
            Add(
                AddSymbolRef(bytes, component.ParentSymbolRef),
                ClassificationVocabulary.GetId(component.ComponentKind)),
            component.Identity),
        _ => throw InvalidEvidence(),
    };

    private static long AddLocator(long bytes, EvidenceLocator locator) => locator switch
    {
        RepositoryEvidenceLocator repository => Add(bytes, repository.Path),
        GeneratedOutputEvidenceLocator generated => Add(
            Add(
                Add(
                    Add(bytes, PolicyConfigurationVocabulary.GetId(generated.ProducerKind)),
                    generated.ProducerId),
                generated.OutputId),
            generated.SourceSha256),
        MetadataEvidenceLocator metadata => Add(
            Add(bytes, metadata.AssemblyIdentity),
            metadata.DocumentationCommentId),
        SyntheticEvidenceLocator synthetic => Add(bytes, synthetic.FixtureId),
        _ => throw InvalidEvidence(),
    };

    private static long AddSymbolRef(long bytes, SymbolRef symbolRef) => Add(
        Add(bytes, symbolRef.CompilationContextRef),
        symbolRef.DocumentationCommentId);

    private static long Add(long bytes, string text) => checked(
        bytes + (long)StrictUtf8.GetByteCount(text) * 6 + 16);

    private static CampaignPlanningValidationException InvalidEvidence() => new(
        CampaignPlanningValidationCode.InvalidAuditAuthority,
        "Evidence authority contains an unsupported closed-vocabulary value.");
}
