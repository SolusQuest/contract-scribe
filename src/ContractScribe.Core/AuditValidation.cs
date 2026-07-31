using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ContractScribe.Core;

internal static partial class AuditJsonModel
{
    private static readonly HashSet<string> TargetProfiles =
        ["profile.external-api", "profile.assembly-visible"];
    private static readonly HashSet<string> PolicyExpectations =
        ["required", "optional", "forbidden"];
    private static readonly HashSet<string> PolicyResolutions =
        ["single", "all-declarations-agree", "conflict", "unavailable"];
    private static readonly HashSet<string> DocumentationObservations =
        ["documentation.present", "documentation.absent", "documentation.unavailable"];
    private static readonly HashSet<string> Outcomes =
        ["audit.outcome.compliant", "audit.outcome.violation", "audit.outcome.skipped"];
    private static readonly HashSet<string> Reasons =
    [
        "audit.reason.required-present",
        "audit.reason.required-absent",
        "audit.reason.optional-present",
        "audit.reason.optional-absent",
        "audit.reason.forbidden-present",
        "audit.reason.forbidden-absent",
        "audit.reason.classification-skipped",
        "audit.reason.policy-conflict",
        "audit.reason.policy-unavailable",
        "audit.reason.documentation-unavailable",
        "audit.reason.documentation-unavailable.malformed-xml",
        "audit.reason.evidence-incomplete",
    ];
    private static readonly HashSet<string> PrimaryKinds =
    [
        "symbol.type.class", "symbol.type.struct", "symbol.type.interface",
        "symbol.type.enum", "symbol.type.delegate", "symbol.member.constructor",
        "symbol.member.method", "symbol.member.operator", "symbol.member.conversion",
        "symbol.member.property", "symbol.member.indexer", "symbol.member.field",
        "symbol.member.enum-member", "symbol.member.event", "symbol.unknown",
    ];
    private static readonly HashSet<string> ComponentKinds =
    [
        "component.parameter", "component.type-parameter", "component.return",
        "component.value", "component.accessor.get", "component.accessor.set",
        "component.accessor.init", "component.accessor.add", "component.accessor.remove",
        "component.backing-field", "component.synthesized.record-positional-property",
        "component.synthesized.implicit-constructor",
        "component.synthesized.record-copy-constructor",
        "component.synthesized.delegate-invoke",
        "component.synthesized.delegate-begin-invoke",
        "component.synthesized.delegate-end-invoke", "component.unknown",
    ];
    private static readonly HashSet<string> Traits =
    [
        "trait.generic", "trait.record-class", "trait.record-struct", "trait.ref-struct",
        "trait.static", "trait.abstract", "trait.virtual", "trait.sealed",
        "trait.extension", "trait.async", "trait.iterator", "trait.required",
        "trait.init-only", "trait.partial",
    ];
    private static readonly HashSet<string> Origins =
    [
        "origin.source", "origin.source-generator", "origin.tool-generated",
        "origin.compiler-synthesized", "origin.mixed", "origin.unknown",
    ];
    private static readonly HashSet<string> SupportStatuses =
    [
        "support.supported", "support.unsupported", "support.ambiguous",
        "support.not-applicable", "support.unavailable-context",
    ];
    private static readonly HashSet<string> SkipReasons =
    [
        "skip.unsupported.symbol-kind", "skip.unsupported.component-kind",
        "skip.ambiguous.partial-declaration", "skip.ambiguous.mixed-origin",
        "skip.not-applicable.synthesized-non-target",
        "skip.not-applicable.non-documentation-component",
        "skip.unavailable.documentation-comment-id",
        "skip.unavailable.generated-provenance", "skip.unavailable.semantic-context",
    ];
    private static readonly HashSet<string> EvidenceKinds =
    [
        "evidence.source.declaration", "evidence.source.implementation",
        "evidence.source.xml-documentation", "evidence.source.attribute",
        "evidence.test", "evidence.repository-documentation", "evidence.public-contract",
    ];
    private static readonly HashSet<string> EvidenceRelations =
    [
        "evidence.declares", "evidence.documents", "evidence.tests",
        "evidence.references", "evidence.constrains",
    ];
    private static readonly HashSet<string> BundleStatuses =
    [
        "evidence.bundle.complete", "evidence.bundle.partial",
        "evidence.bundle.unavailable",
    ];
    private static readonly HashSet<string> OmissionReasons =
    [
        "evidence.omission.access-not-permitted", "evidence.omission.source-unavailable",
        "evidence.omission.binary-content", "evidence.omission.budget-exhausted",
        "evidence.omission.not-provided",
    ];
    private static readonly HashSet<string> AuthorityRoles =
    [
        "ordinary", "partial-type-part", "partial-member-implementing",
        "partial-member-defining-fallback",
    ];
    private static readonly HashSet<string> BlockStates =
        ["no-block", "whitespace-only", "well-formed", "malformed"];

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static void Validate(
        JsonElement document,
        IReadOnlyDictionary<AuditEvidenceKey, string>? originalEvidence,
        bool requireOriginalEvidence,
        bool trustSourceValidatedTruncation = false)
    {
        try
        {
            ValidateCore(
                document,
                originalEvidence,
                requireOriginalEvidence,
                trustSourceValidatedTruncation);
        }
        catch (AuditValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or KeyNotFoundException
            or OverflowException
            or ArgumentException)
        {
            throw Failure(
                AuditValidationCode.InvalidShape,
                "The Audit Result has an invalid JSON shape.",
                exception);
        }
    }

    private static void ValidateCore(
        JsonElement document,
        IReadOnlyDictionary<AuditEvidenceKey, string>? originalEvidence,
        bool requireOriginalEvidence,
        bool trustSourceValidatedTruncation)
    {
        RequireObject(
            document,
            ["auditResultVersion", "policyConfigurationVersion", "taxonomyRegistryVersion", "targetProfile", "results"],
            [],
            AuditValidationCode.InvalidShape);
        Require(
            GetInt32(document, "auditResultVersion") == AuditVocabulary.AuditVersion
            && GetInt32(document, "policyConfigurationVersion") == AuditVocabulary.PolicyConfigurationVersion
            && GetInt32(document, "taxonomyRegistryVersion") == AuditVocabulary.TaxonomyRegistryVersion,
            AuditValidationCode.UnsupportedVersion,
            "The Audit Result contains an unsupported contract version.");
        ParseTargetProfile(GetString(document, "targetProfile"));
        var results = GetArray(document, "results");
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        var resultIndex = 0;
        foreach (var result in results.EnumerateArray())
        {
            RequireObject(
                result,
                ["classification", "policyContributions", "policyExpectation", "policyResolution", "documentationObservation", "auditOutcome", "reasonCode", "evidenceIds", "evidenceBundle"],
                ["evidenceAuthority"],
                AuditValidationCode.InvalidShape);
            var classification = result.GetProperty("classification");
            ValidateClassification(classification);
            Require(
                subjects.Add(GetSubjectKey(classification)),
                AuditValidationCode.InvalidClassification,
                "Audit Result subjects must be unique.");
            ValidatePolicy(result);
            ValidateEvidence(
                result,
                classification,
                resultIndex,
                originalEvidence,
                requireOriginalEvidence,
                trustSourceValidatedTruncation);
            ValidateEvidenceAuthority(result);
            ValidateOutcome(result);
            resultIndex++;
        }
    }

    internal static TargetProfile ParseTargetProfile(string? value) => value switch
    {
        "profile.external-api" => TargetProfile.ExternalApi,
        "profile.assembly-visible" => TargetProfile.AssemblyVisible,
        _ => throw Failure(
            AuditValidationCode.InvalidVocabulary,
            "The Audit Result target profile is absent or unknown."),
    };

    internal static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Require(
                    names.Add(property.Name),
                    AuditValidationCode.DuplicateProperty,
                    $"Duplicate JSON property '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static void ValidateClassification(JsonElement classification)
    {
        Require(
            classification.ValueKind == JsonValueKind.Object,
            AuditValidationCode.InvalidClassification,
            "Classification must be an object.");
        var recordType = GetString(classification, "recordType");
        var required = recordType switch
        {
            "TargetClassification" => new[]
            {
                "recordType", "symbolRef", "primaryKind", "traits", "origin", "supportStatus",
            },
            "ComponentClassification" => new[]
            {
                "recordType", "parentSymbolRef", "componentKind", "identity", "origin", "supportStatus",
            },
            "UnresolvedClassification" => new[]
            {
                "recordType", "compilationContextRef", "origin", "supportStatus", "skipReason", "candidateLocator",
            },
            _ => throw Failure(
                AuditValidationCode.InvalidClassification,
                "Audit Result v1 embeds only target, component, or unresolved classifications."),
        };
        RequireObject(
            classification,
            required,
            recordType == "UnresolvedClassification" ? [] : ["skipReason"],
            AuditValidationCode.InvalidClassification);

        var supportStatus = GetString(classification, "supportStatus");
        var origin = GetString(classification, "origin");
        RequireKnown(SupportStatuses, supportStatus, AuditValidationCode.InvalidClassification);
        RequireKnown(Origins, origin, AuditValidationCode.InvalidClassification);

        if (recordType == "TargetClassification")
        {
            ValidateSymbolRef(classification.GetProperty("symbolRef"));
            RequireKnown(
                PrimaryKinds,
                GetString(classification, "primaryKind"),
                AuditValidationCode.InvalidClassification);
            var traits = GetArray(classification, "traits").EnumerateArray()
                .Select(item => RequireStringValue(item, AuditValidationCode.InvalidClassification))
                .ToArray();
            Require(
                traits.All(Traits.Contains)
                && traits.Distinct(StringComparer.Ordinal).Count() == traits.Length,
                AuditValidationCode.InvalidClassification,
                "Classification traits are unknown or duplicated.");
        }
        else if (recordType == "ComponentClassification")
        {
            ValidateSymbolRef(classification.GetProperty("parentSymbolRef"));
            var componentKind = GetString(classification, "componentKind");
            RequireKnown(ComponentKinds, componentKind, AuditValidationCode.InvalidClassification);
            Require(
                IsValidComponentIdentity(componentKind, GetString(classification, "identity")),
                AuditValidationCode.InvalidClassification,
                "The component identity does not match its kind.");
        }
        else
        {
            RequireOpaqueId(
                GetString(classification, "compilationContextRef"),
                AuditValidationCode.InvalidClassification);
            Require(
                supportStatus == "support.unavailable-context",
                AuditValidationCode.InvalidClassification,
                "Unresolved classifications require unavailable context.");
            ValidateCandidateLocator(classification.GetProperty("candidateLocator"));
        }

        var hasSkip = classification.TryGetProperty("skipReason", out var skipElement);
        var skipReason = hasSkip ? RequireStringValue(skipElement, AuditValidationCode.InvalidClassification) : null;
        if (supportStatus == "support.supported")
        {
            Require(
                !hasSkip,
                AuditValidationCode.InvalidClassification,
                "Supported classifications cannot carry a skip reason.");
        }
        else
        {
            Require(
                hasSkip && SkipReasons.Contains(skipReason!),
                AuditValidationCode.InvalidClassification,
                "Non-supported classifications require a known skip reason.");
            var prefix = supportStatus switch
            {
                "support.unsupported" => "skip.unsupported.",
                "support.ambiguous" => "skip.ambiguous.",
                "support.not-applicable" => "skip.not-applicable.",
                "support.unavailable-context" => "skip.unavailable.",
                _ => string.Empty,
            };
            Require(
                skipReason!.StartsWith(prefix, StringComparison.Ordinal),
                AuditValidationCode.InvalidClassification,
                "Classification status and skip reason disagree.");
        }

        ValidateOriginCombination(supportStatus, origin, skipReason);
    }

    private static void ValidateOriginCombination(
        string supportStatus,
        string origin,
        string? skipReason)
    {
        if (supportStatus == "support.unavailable-context")
        {
            var valid = skipReason switch
            {
                "skip.unavailable.generated-provenance" => origin == "origin.unknown",
                "skip.unavailable.documentation-comment-id" or
                    "skip.unavailable.semantic-context" => origin is
                        "origin.source" or "origin.source-generator" or
                        "origin.tool-generated" or "origin.mixed",
                _ => false,
            };
            Require(
                valid,
                AuditValidationCode.InvalidClassification,
                "Unavailable classification origin and skip reason disagree.");
        }

        if (origin == "origin.unknown")
        {
            Require(
                supportStatus == "support.unavailable-context"
                && skipReason == "skip.unavailable.generated-provenance",
                AuditValidationCode.InvalidClassification,
                "Unknown origin requires unavailable generated provenance.");
        }

        if (origin == "origin.mixed")
        {
            Require(
                supportStatus == "support.ambiguous"
                    && skipReason is "skip.ambiguous.mixed-origin" or
                        "skip.ambiguous.partial-declaration"
                || supportStatus == "support.unavailable-context"
                    && skipReason is "skip.unavailable.documentation-comment-id" or
                        "skip.unavailable.semantic-context",
                AuditValidationCode.InvalidClassification,
                "Mixed origin has an invalid status or skip reason.");
        }

        if (skipReason == "skip.ambiguous.mixed-origin")
        {
            Require(
                origin == "origin.mixed",
                AuditValidationCode.InvalidClassification,
                "Mixed-origin skip requires mixed origin.");
        }
    }

    private static void ValidatePolicy(JsonElement result)
    {
        var contributions = GetArray(result, "policyContributions")
            .EnumerateArray()
            .ToArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var expectations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contribution in contributions)
        {
            var hasSource = contribution.TryGetProperty("sourcePath", out var source);
            var hasGenerated = contribution.TryGetProperty("generatedOutput", out var generated);
            Require(
                hasSource != hasGenerated,
                AuditValidationCode.InvalidPolicy,
                "A policy contribution requires exactly one source identity.");
            RequireObject(
                contribution,
                hasSource
                    ? ["projectPath", "sourcePath", "policyExpectation", "matchedRuleId"]
                    : ["projectPath", "generatedOutput", "policyExpectation", "matchedRuleId"],
                [],
                AuditValidationCode.InvalidPolicy);
            RequireCanonicalPath(
                GetString(contribution, "projectPath"),
                AuditValidationCode.InvalidPolicy);
            if (hasSource)
            {
                RequireCanonicalPath(
                    RequireStringValue(source, AuditValidationCode.InvalidPolicy),
                    AuditValidationCode.InvalidPolicy);
            }
            else
            {
                ValidateGeneratedOutput(generated, requireHashIdentifiers: false);
            }

            var expectation = GetString(contribution, "policyExpectation");
            RequireKnown(PolicyExpectations, expectation, AuditValidationCode.InvalidPolicy);
            expectations.Add(expectation);
            Require(
                keys.Add(PolicyContributionKey(contribution)),
                AuditValidationCode.InvalidPolicy,
                "Policy contribution keys must be unique.");
            var rule = contribution.GetProperty("matchedRuleId");
            Require(
                rule.ValueKind == JsonValueKind.Null
                || rule.ValueKind == JsonValueKind.String
                    && RuleIdPattern().IsMatch(rule.GetString()!),
                AuditValidationCode.InvalidPolicy,
                "The matched rule ID is invalid.");
        }

        var resolution = GetString(result, "policyResolution");
        RequireKnown(PolicyResolutions, resolution, AuditValidationCode.InvalidPolicy);
        var expectationValue = result.GetProperty("policyExpectation");
        Require(
            expectationValue.ValueKind is JsonValueKind.Null or JsonValueKind.String,
            AuditValidationCode.InvalidPolicy,
            "Policy expectation must be a known string or null.");
        if (expectationValue.ValueKind == JsonValueKind.String)
        {
            RequireKnown(
                PolicyExpectations,
                expectationValue.GetString()!,
                AuditValidationCode.InvalidPolicy);
        }

        var supported = result.GetProperty("classification")
            .GetProperty("supportStatus")
            .GetString() == "support.supported";
        var expectedResolution = !supported || contributions.Length == 0
            ? "unavailable"
            : expectations.Count > 1
                ? "conflict"
                : contributions.Length == 1
                    ? "single"
                    : "all-declarations-agree";
        Require(
            resolution == expectedResolution,
            AuditValidationCode.InvalidPolicy,
            "The aggregate policy resolution is incorrect.");
        if (resolution is "conflict" or "unavailable")
        {
            Require(
                expectationValue.ValueKind == JsonValueKind.Null,
                AuditValidationCode.InvalidPolicy,
                "Conflict and unavailable policy rows require a null expectation.");
        }
        else
        {
            Require(
                expectationValue.GetString() == expectations.Single(),
                AuditValidationCode.InvalidPolicy,
                "The aggregate policy expectation is incorrect.");
        }
    }

    private static void ValidateEvidence(
        JsonElement result,
        JsonElement classification,
        int resultIndex,
        IReadOnlyDictionary<AuditEvidenceKey, string>? originalEvidence,
        bool requireOriginalEvidence,
        bool trustSourceValidatedTruncation)
    {
        var bundle = result.GetProperty("evidenceBundle");
        RequireObject(
            bundle,
            ["evidenceBundleVersion", "availabilityStatus", "items"],
            ["omissionReason", "observationSubject"],
            AuditValidationCode.InvalidEvidence);
        Require(
            GetInt32(bundle, "evidenceBundleVersion") == 1,
            AuditValidationCode.UnsupportedVersion,
            "The evidence bundle version is unsupported.");
        var status = GetString(bundle, "availabilityStatus");
        RequireKnown(BundleStatuses, status, AuditValidationCode.InvalidEvidence);
        var items = GetArray(bundle, "items").EnumerateArray().ToArray();
        Require(
            items.Length <= 32,
            AuditValidationCode.InvalidEvidence,
            "An evidence bundle exceeds the item budget.");
        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        long includedTotal = 0;
        foreach (var item in items)
        {
            ValidateEvidenceItem(
                item,
                resultIndex,
                originalEvidence,
                requireOriginalEvidence,
                trustSourceValidatedTruncation);
            var id = GetString(item, "evidenceId");
            Require(
                byId.TryAdd(id, item),
                AuditValidationCode.InvalidEvidence,
                "Evidence IDs must be unique within a result.");
            includedTotal = checked(includedTotal + GetInt32(item, "includedUtf8ByteCount"));
        }

        Require(
            includedTotal <= 32768,
            AuditValidationCode.InvalidEvidence,
            "An evidence bundle exceeds the UTF-8 byte budget.");
        var referenced = GetArray(result, "evidenceIds").EnumerateArray()
            .Select(item => RequireStringValue(item, AuditValidationCode.InvalidEvidence))
            .ToArray();
        Require(
            referenced.Distinct(StringComparer.Ordinal).Count() == referenced.Length,
            AuditValidationCode.InvalidEvidence,
            "Evidence references must be unique.");
        foreach (var id in referenced)
        {
            Require(
                byId.TryGetValue(id, out var evidence)
                && !GetBoolean(evidence, "isTruncated"),
                AuditValidationCode.InvalidEvidence,
                "Evidence references must resolve to untruncated items.");
        }

        var hasOmission = bundle.TryGetProperty("omissionReason", out var omission);
        if (hasOmission)
        {
            RequireKnown(
                OmissionReasons,
                RequireStringValue(omission, AuditValidationCode.InvalidEvidence),
                AuditValidationCode.InvalidEvidence);
        }

        if (status == "evidence.bundle.unavailable")
        {
            Require(
                items.Length == 0 && hasOmission,
                AuditValidationCode.InvalidEvidence,
                "Unavailable evidence requires an omission and no items.");
        }
        else if (status == "evidence.bundle.complete")
        {
            Require(
                items.Length > 0 && !hasOmission
                && items.All(item => !GetBoolean(item, "isTruncated")),
                AuditValidationCode.InvalidEvidence,
                "Complete evidence requires non-empty untruncated items and no omission.");
        }
        else
        {
            Require(
                items.Length > 0
                && hasOmission
                && omission.GetString() == "evidence.omission.budget-exhausted"
                && referenced.Length == 0
                && GetString(result, "reasonCode") == "audit.reason.evidence-incomplete",
                AuditValidationCode.InvalidEvidence,
                "Partial evidence has an invalid result projection.");
        }

        var recordType = GetString(classification, "recordType");
        Require(
            recordType != "UnresolvedClassification" || referenced.Length == 0,
            AuditValidationCode.InvalidEvidence,
            "Unresolved classifications cannot reference evidence.");
        var outcome = GetString(result, "auditOutcome");
        if (outcome is "audit.outcome.compliant" or "audit.outcome.violation")
        {
            Require(
                status == "evidence.bundle.complete",
                AuditValidationCode.InvalidEvidence,
                "Compliant and violation rows require complete evidence.");
            var expectedSubject = GetExpectedEvidenceSubject(classification);
            var relevant = referenced.Select(id => byId[id]).ToArray();
            Require(
                relevant.All(item => JsonElement.DeepEquals(item.GetProperty("subject"), expectedSubject)),
                AuditValidationCode.InvalidEvidence,
                "Referenced evidence is bound to a different subject.");
            if (GetString(result, "documentationObservation") == "documentation.present")
            {
                Require(
                    relevant.Any(item => GetString(item, "kind") == "evidence.source.xml-documentation"
                        && GetString(item, "relation") == "evidence.documents"),
                    AuditValidationCode.InvalidEvidence,
                    "Present documentation requires direct XML-documentation evidence.");
            }
            else
            {
                Require(
                    relevant.Any(item => GetString(item, "kind") == "evidence.source.declaration"
                        && GetString(item, "relation") == "evidence.declares")
                    && !items.Any(item => JsonElement.DeepEquals(item.GetProperty("subject"), expectedSubject)
                        && GetString(item, "kind") == "evidence.source.xml-documentation"),
                    AuditValidationCode.InvalidEvidence,
                    "Absent documentation requires declaration evidence and forbids contradictory XML evidence.");
            }
        }
    }

    private static void ValidateEvidenceItem(
        JsonElement item,
        int resultIndex,
        IReadOnlyDictionary<AuditEvidenceKey, string>? originalEvidence,
        bool requireOriginalEvidence,
        bool trustSourceValidatedTruncation)
    {
        RequireObject(
            item,
            ["evidenceId", "subject", "kind", "relation", "excerpt", "sha256", "originalUtf8ByteCount", "includedUtf8ByteCount", "omittedUtf8ByteCount", "isTruncated", "locator"],
            [],
            AuditValidationCode.InvalidEvidence);
        var evidenceId = GetString(item, "evidenceId");
        Require(
            EvidenceIdPattern().IsMatch(evidenceId),
            AuditValidationCode.InvalidEvidence,
            "The evidence ID is invalid.");
        ValidateEvidenceSubject(item.GetProperty("subject"));
        RequireKnown(EvidenceKinds, GetString(item, "kind"), AuditValidationCode.InvalidEvidence);
        RequireKnown(EvidenceRelations, GetString(item, "relation"), AuditValidationCode.InvalidEvidence);
        var excerpt = GetString(item, "excerpt");
        var hash = GetString(item, "sha256");
        Require(
            Sha256Pattern().IsMatch(hash),
            AuditValidationCode.InvalidEvidence,
            "The evidence SHA-256 is invalid.");
        var originalCount = GetNonNegativeInt32(item, "originalUtf8ByteCount");
        var includedCount = GetNonNegativeInt32(item, "includedUtf8ByteCount");
        var omittedCount = GetNonNegativeInt32(item, "omittedUtf8ByteCount");
        var truncated = GetBoolean(item, "isTruncated");
        Require(
            Encoding.UTF8.GetByteCount(excerpt) == includedCount
            && checked(includedCount + omittedCount) == originalCount
            && includedCount <= 4096
            && (omittedCount > 0) == truncated
            && (originalCount == 0 || excerpt.Length > 0),
            AuditValidationCode.InvalidEvidence,
            "Evidence byte counts, truncation, or excerpt budget are inconsistent.");

        string? originalText = truncated ? null : excerpt;
        if (truncated)
        {
            if (!trustSourceValidatedTruncation)
            {
                var found = originalEvidence?.TryGetValue(
                    new AuditEvidenceKey(resultIndex, evidenceId),
                    out originalText) == true;
                if (!found)
                {
                    if (requireOriginalEvidence)
                    {
                        throw Failure(
                            AuditValidationCode.MissingOriginalEvidence,
                            $"Truncated evidence '{evidenceId}' requires its original text.");
                    }

                    originalText = null;
                }
            }

            if (originalText is not null)
            {
                Require(
                    originalText.StartsWith(excerpt, StringComparison.Ordinal)
                    && !(originalText.Length > excerpt.Length
                        && excerpt.Length > 0
                        && char.IsHighSurrogate(excerpt[^1])),
                    AuditValidationCode.OriginalEvidenceMismatch,
                    "The original evidence does not match the canonical excerpt.");
            }
        }

        if (originalText is not null)
        {
            Require(
                Encoding.UTF8.GetByteCount(originalText) == originalCount
                && ComputeSha256(originalText) == hash,
                AuditValidationCode.OriginalEvidenceMismatch,
                "The original evidence does not match its byte count or SHA-256.");
        }

        ValidateEvidenceLocator(
            item.GetProperty("locator"),
            excerpt,
            originalText,
            truncated);
    }

    private static void ValidateEvidenceAuthority(JsonElement result)
    {
        var hasAuthority = result.TryGetProperty("evidenceAuthority", out var authority);
        var bundle = result.GetProperty("evidenceBundle");
        var hasObservationSubject = bundle.TryGetProperty("observationSubject", out var observation);
        var observationValue = result.GetProperty("documentationObservation");
        var requiresAuthority = observationValue.ValueKind == JsonValueKind.String
                && observationValue.GetString() is "documentation.present" or "documentation.absent"
            || GetString(result, "reasonCode") ==
                "audit.reason.documentation-unavailable.malformed-xml";
        Require(
            hasAuthority == requiresAuthority && hasObservationSubject == requiresAuthority,
            AuditValidationCode.InvalidAuthority,
            "Evidence authority and observation subject presence do not match the result.");
        if (!requiresAuthority)
        {
            return;
        }

        RequireObject(
            authority,
            ["declarationSetId", "completeness", "declarations"],
            [],
            AuditValidationCode.InvalidAuthority);
        var completeness = GetString(authority, "completeness");
        Require(
            completeness is "complete" or "positive-only",
            AuditValidationCode.InvalidAuthority,
            "Evidence authority completeness is invalid.");
        var declarations = GetArray(authority, "declarations");
        Require(
            declarations.GetArrayLength() > 0,
            AuditValidationCode.InvalidAuthority,
            "Evidence authority requires declarations.");
        ValidateObservationSubject(observation);
        var digest = ComputeDeclarationDigest(declarations);
        Require(
            GetString(authority, "declarationSetId") == $"dset.{digest}"
            && GetString(observation, "authoritativeDeclarationSetDigest") == digest
            && GetInt32(observation, "authoritativeDeclarationCount") == declarations.GetArrayLength(),
            AuditValidationCode.InvalidAuthority,
            "Declaration authority commitments do not match.");
        var classification = result.GetProperty("classification");
        Require(
            GetString(observation, "compilationContextRef") == GetClassificationContext(classification)
            && ObservationSubjectMatchesClassification(observation.GetProperty("subject"), classification)
            && GetString(observation, "observationSubjectRef") == ComputeObservationSubjectRef(observation),
            AuditValidationCode.InvalidAuthority,
            "The observation subject commitment does not match the classification.");

        var evidenceItems = bundle.GetProperty("items").EnumerateArray()
            .ToDictionary(item => GetString(item, "evidenceId"), StringComparer.Ordinal);
        var declarationIds = new HashSet<string>(StringComparer.Ordinal);
        var declarationEvidenceIds = new HashSet<string>(StringComparer.Ordinal);
        var malformedEvidenceIds = new HashSet<string>(StringComparer.Ordinal);
        var isComponent = GetString(classification, "recordType") == "ComponentClassification";
        var componentKind = isComponent ? GetString(classification, "componentKind") : null;
        foreach (var declaration in declarations.EnumerateArray())
        {
            RequireObject(
                declaration,
                ["declarationId", "authorityRole", "blockState", "evidenceId"],
                ["componentLocalName", "componentMatch"],
                AuditValidationCode.InvalidAuthority);
            var declarationId = GetString(declaration, "declarationId");
            var evidenceId = GetString(declaration, "evidenceId");
            Require(
                DeclarationIdPattern().IsMatch(declarationId)
                && declarationIds.Add(declarationId)
                && declarationEvidenceIds.Add(evidenceId),
                AuditValidationCode.InvalidAuthority,
                "Authority declaration identity or evidence reference is invalid.");
            Require(
                evidenceItems.TryGetValue(evidenceId, out var evidence),
                AuditValidationCode.InvalidAuthority,
                "Authority declaration evidence does not resolve within the result bundle.");
            RequireKnown(
                AuthorityRoles,
                GetString(declaration, "authorityRole"),
                AuditValidationCode.InvalidAuthority);
            var blockState = GetString(declaration, "blockState");
            RequireKnown(BlockStates, blockState, AuditValidationCode.InvalidAuthority);
            Require(
                JsonElement.DeepEquals(evidence.GetProperty("subject"), observation.GetProperty("subject")),
                AuditValidationCode.InvalidAuthority,
                "Authority evidence is bound to another subject.");
            var hasLocalName = declaration.TryGetProperty("componentLocalName", out var localName);
            var hasMatch = declaration.TryGetProperty("componentMatch", out var componentMatch);
            var malformed = blockState == "malformed";
            if (!isComponent)
            {
                Require(
                    !hasLocalName && !hasMatch,
                    AuditValidationCode.InvalidAuthority,
                    "Target authority rows forbid component fields.");
            }
            else if (componentKind is "component.parameter" or "component.type-parameter")
            {
                Require(
                    hasLocalName
                    && !string.IsNullOrEmpty(RequireStringValue(localName, AuditValidationCode.InvalidAuthority))
                    && hasMatch == !malformed,
                    AuditValidationCode.InvalidAuthority,
                    "Named component authority fields are inconsistent.");
            }
            else
            {
                Require(
                    !hasLocalName && hasMatch == !malformed,
                    AuditValidationCode.InvalidAuthority,
                    "Component authority fields are inconsistent.");
            }

            if (hasMatch)
            {
                Require(
                    componentMatch.ValueKind == JsonValueKind.String
                    && componentMatch.GetString() is "present" or "absent",
                    AuditValidationCode.InvalidAuthority,
                    "Component match is invalid.");
            }

            if (malformed)
            {
                malformedEvidenceIds.Add(evidenceId);
                Require(
                    GetString(evidence, "kind") == "evidence.source.xml-documentation"
                    && GetString(evidence, "relation") == "evidence.documents"
                    && !GetBoolean(evidence, "isTruncated"),
                    AuditValidationCode.InvalidAuthority,
                    "Malformed authority requires exact untruncated XML evidence.");
            }
        }

        Require(
            HasValidAuthorityMode(declarations.EnumerateArray().ToArray()),
            AuditValidationCode.InvalidAuthority,
            "Authority declarations do not form one closed selection mode.");
        var referenced = result.GetProperty("evidenceIds").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Require(
            referenced.SetEquals(declarationEvidenceIds)
            && malformedEvidenceIds.IsSubsetOf(referenced),
            AuditValidationCode.InvalidAuthority,
            "Evidence references do not exactly cover the authority declarations.");
        var derived = DeriveDocumentationObservation(observation.GetProperty("subject"), authority);
        Require(
            result.GetProperty("documentationObservation").GetString() == derived,
            AuditValidationCode.InvalidAuthority,
            "The claimed documentation observation contradicts authority.");
        var malformedReason = GetString(result, "reasonCode") ==
            "audit.reason.documentation-unavailable.malformed-xml";
        Require(
            malformedReason == (derived == "documentation.unavailable" && malformedEvidenceIds.Count > 0),
            AuditValidationCode.InvalidAuthority,
            "Malformed XML reason and authority disagree.");
        if (derived != "documentation.present")
        {
            Require(
                completeness == "complete",
                AuditValidationCode.InvalidAuthority,
                "Absence and malformed XML require complete authority.");
        }
    }

    private static void ValidateOutcome(JsonElement result)
    {
        var outcome = GetString(result, "auditOutcome");
        var reason = GetString(result, "reasonCode");
        RequireKnown(Outcomes, outcome, AuditValidationCode.InvalidOutcome);
        RequireKnown(Reasons, reason, AuditValidationCode.InvalidOutcome);
        var expectationElement = result.GetProperty("policyExpectation");
        var observationElement = result.GetProperty("documentationObservation");
        Require(
            observationElement.ValueKind is JsonValueKind.Null or JsonValueKind.String,
            AuditValidationCode.InvalidOutcome,
            "Documentation observation must be a known string or null.");
        if (observationElement.ValueKind == JsonValueKind.String)
        {
            RequireKnown(
                DocumentationObservations,
                observationElement.GetString()!,
                AuditValidationCode.InvalidOutcome);
        }

        Require(
            DerivePrimaryReason(result) == reason,
            AuditValidationCode.InvalidOutcome,
            "The selected primary reason violates precedence.");
        if (reason == "audit.reason.classification-skipped")
        {
            RequireSkippedProjection(result, "unavailable", "evidence.omission.not-provided");
            return;
        }

        if (reason == "audit.reason.policy-conflict")
        {
            Require(
                result.GetProperty("policyContributions").GetArrayLength() > 0
                && GetString(result, "policyResolution") == "conflict",
                AuditValidationCode.InvalidOutcome,
                "Policy conflict projection is invalid.");
            RequireSkippedProjection(result, "conflict", "evidence.omission.not-provided");
            return;
        }

        if (reason == "audit.reason.policy-unavailable")
        {
            Require(
                result.GetProperty("policyContributions").GetArrayLength() == 0,
                AuditValidationCode.InvalidOutcome,
                "Policy unavailable projection retains contributions.");
            RequireSkippedProjection(result, "unavailable", "evidence.omission.not-provided");
            return;
        }

        if (reason == "audit.reason.documentation-unavailable")
        {
            Require(
                outcome == "audit.outcome.skipped"
                && expectationElement.ValueKind == JsonValueKind.String
                && observationElement.GetString() == "documentation.unavailable"
                && result.GetProperty("evidenceIds").GetArrayLength() == 0,
                AuditValidationCode.InvalidOutcome,
                "Documentation unavailable projection is invalid.");
            RequireUnavailableBundle(result, "evidence.omission.source-unavailable");
            return;
        }

        if (reason == "audit.reason.documentation-unavailable.malformed-xml")
        {
            Require(
                outcome == "audit.outcome.skipped"
                && observationElement.GetString() == "documentation.unavailable"
                && result.GetProperty("evidenceIds").GetArrayLength() > 0
                && GetString(result.GetProperty("evidenceBundle"), "availabilityStatus") ==
                    "evidence.bundle.complete",
                AuditValidationCode.InvalidOutcome,
                "Malformed XML projection is invalid.");
            return;
        }

        if (reason == "audit.reason.evidence-incomplete")
        {
            Require(
                outcome == "audit.outcome.skipped"
                && expectationElement.ValueKind == JsonValueKind.String
                && observationElement.GetString() == "documentation.unavailable"
                && result.GetProperty("evidenceIds").GetArrayLength() == 0
                && GetString(result.GetProperty("evidenceBundle"), "availabilityStatus") ==
                    "evidence.bundle.partial"
                && GetString(result.GetProperty("evidenceBundle"), "omissionReason") ==
                    "evidence.omission.budget-exhausted",
                AuditValidationCode.InvalidOutcome,
                "Evidence incomplete projection is invalid.");
            return;
        }

        Require(
            expectationElement.ValueKind == JsonValueKind.String
            && observationElement.ValueKind == JsonValueKind.String,
            AuditValidationCode.InvalidOutcome,
            "Ordinary audit rows require policy and observation values.");
        var matrix = (expectationElement.GetString(), observationElement.GetString()) switch
        {
            ("required", "documentation.present") =>
                ("audit.outcome.compliant", "audit.reason.required-present"),
            ("required", "documentation.absent") =>
                ("audit.outcome.violation", "audit.reason.required-absent"),
            ("optional", "documentation.present") =>
                ("audit.outcome.compliant", "audit.reason.optional-present"),
            ("optional", "documentation.absent") =>
                ("audit.outcome.compliant", "audit.reason.optional-absent"),
            ("forbidden", "documentation.present") =>
                ("audit.outcome.violation", "audit.reason.forbidden-present"),
            ("forbidden", "documentation.absent") =>
                ("audit.outcome.compliant", "audit.reason.forbidden-absent"),
            _ => throw Failure(
                AuditValidationCode.InvalidOutcome,
                "The policy/observation matrix combination is invalid."),
        };
        Require(
            matrix.Item1 == outcome
            && matrix.Item2 == reason
            && result.GetProperty("evidenceIds").GetArrayLength() > 0,
            AuditValidationCode.InvalidOutcome,
            "The audit outcome matrix projection is invalid.");
    }

    private static string DerivePrimaryReason(JsonElement result)
    {
        if (GetString(result.GetProperty("classification"), "supportStatus") != "support.supported")
        {
            return "audit.reason.classification-skipped";
        }

        var contributions = result.GetProperty("policyContributions").EnumerateArray().ToArray();
        var expectations = contributions
            .Select(contribution => GetString(contribution, "policyExpectation"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (expectations.Length > 1)
        {
            return "audit.reason.policy-conflict";
        }

        if (contributions.Length == 0)
        {
            return "audit.reason.policy-unavailable";
        }

        var observation = result.GetProperty("documentationObservation").GetString();
        var bundleStatus = GetString(result.GetProperty("evidenceBundle"), "availabilityStatus");
        if (observation == "documentation.unavailable"
            && bundleStatus == "evidence.bundle.unavailable")
        {
            return "audit.reason.documentation-unavailable";
        }

        if (observation == "documentation.unavailable"
            && result.TryGetProperty("evidenceAuthority", out var authority)
            && authority.GetProperty("declarations").EnumerateArray()
                .Any(declaration => GetString(declaration, "blockState") == "malformed"))
        {
            return "audit.reason.documentation-unavailable.malformed-xml";
        }

        if (bundleStatus == "evidence.bundle.partial")
        {
            return "audit.reason.evidence-incomplete";
        }

        return (expectations.Single(), observation) switch
        {
            ("required", "documentation.present") => "audit.reason.required-present",
            ("required", "documentation.absent") => "audit.reason.required-absent",
            ("optional", "documentation.present") => "audit.reason.optional-present",
            ("optional", "documentation.absent") => "audit.reason.optional-absent",
            ("forbidden", "documentation.present") => "audit.reason.forbidden-present",
            ("forbidden", "documentation.absent") => "audit.reason.forbidden-absent",
            _ => throw Failure(
                AuditValidationCode.InvalidOutcome,
                "A primary audit reason cannot be derived."),
        };
    }

    private static void RequireSkippedProjection(
        JsonElement result,
        string policyResolution,
        string omissionReason)
    {
        Require(
            GetString(result, "auditOutcome") == "audit.outcome.skipped"
            && result.GetProperty("policyExpectation").ValueKind == JsonValueKind.Null
            && GetString(result, "policyResolution") == policyResolution
            && result.GetProperty("documentationObservation").ValueKind == JsonValueKind.Null
            && result.GetProperty("evidenceIds").GetArrayLength() == 0,
            AuditValidationCode.InvalidOutcome,
            "The skipped-result projection is invalid.");
        RequireUnavailableBundle(result, omissionReason);
    }

    private static void RequireUnavailableBundle(JsonElement result, string omissionReason)
    {
        var bundle = result.GetProperty("evidenceBundle");
        Require(
            GetString(bundle, "availabilityStatus") == "evidence.bundle.unavailable"
            && GetString(bundle, "omissionReason") == omissionReason
            && bundle.GetProperty("items").GetArrayLength() == 0,
            AuditValidationCode.InvalidOutcome,
            "The unavailable evidence bundle projection is invalid.");
    }

    private static void ValidateEvidenceSubject(JsonElement subject)
    {
        if (subject.TryGetProperty("parentSymbolRef", out _))
        {
            RequireObject(
                subject,
                ["parentSymbolRef", "componentKind", "identity"],
                [],
                AuditValidationCode.InvalidEvidence);
            ValidateSymbolRef(subject.GetProperty("parentSymbolRef"));
            var componentKind = GetString(subject, "componentKind");
            RequireKnown(ComponentKinds, componentKind, AuditValidationCode.InvalidEvidence);
            Require(
                IsValidComponentIdentity(componentKind, GetString(subject, "identity")),
                AuditValidationCode.InvalidEvidence,
                "The evidence component subject is invalid.");
        }
        else
        {
            ValidateSymbolRef(subject);
        }
    }

    private static void ValidateSymbolRef(JsonElement symbolRef)
    {
        RequireObject(
            symbolRef,
            ["compilationContextRef", "documentationCommentId"],
            [],
            AuditValidationCode.InvalidClassification);
        RequireOpaqueId(
            GetString(symbolRef, "compilationContextRef"),
            AuditValidationCode.InvalidClassification);
        Require(
            !string.IsNullOrEmpty(GetString(symbolRef, "documentationCommentId")),
            AuditValidationCode.InvalidClassification,
            "Documentation comment IDs cannot be empty.");
    }

    private static void ValidateObservationSubject(JsonElement observation)
    {
        RequireObject(
            observation,
            ["observationSubjectRef", "compilationContextRef", "subject", "authoritativeDeclarationSetDigest", "authoritativeDeclarationCount"],
            [],
            AuditValidationCode.InvalidAuthority);
        Require(
            ObservationRefPattern().IsMatch(GetString(observation, "observationSubjectRef"))
            && Sha256Pattern().IsMatch(GetString(observation, "authoritativeDeclarationSetDigest"))
            && GetNonNegativeInt32(observation, "authoritativeDeclarationCount") > 0,
            AuditValidationCode.InvalidAuthority,
            "The observation subject commitment has an invalid identity, digest, or count.");
        RequireOpaqueId(
            GetString(observation, "compilationContextRef"),
            AuditValidationCode.InvalidAuthority);
        ValidateEvidenceSubject(observation.GetProperty("subject"));
    }

    private static void ValidateEvidenceLocator(
        JsonElement locator,
        string excerpt,
        string? originalText,
        bool truncated)
    {
        Require(
            locator.ValueKind == JsonValueKind.Object,
            AuditValidationCode.InvalidEvidence,
            "Evidence locator must be an object.");
        var variants = new[] { "repository", "generatedOutput", "metadata", "synthetic" }
            .Where(name => locator.TryGetProperty(name, out _))
            .ToArray();
        Require(
            variants.Length == 1 && locator.EnumerateObject().Count() == 1,
            AuditValidationCode.InvalidEvidence,
            "Evidence locator requires exactly one variant.");
        if (variants[0] == "repository")
        {
            var repository = locator.GetProperty("repository");
            RequireObject(
                repository,
                ["path"],
                ["span"],
                AuditValidationCode.InvalidEvidence);
            RequireCanonicalPath(GetString(repository, "path"), AuditValidationCode.InvalidEvidence);
            ValidateSpan(repository, excerpt, originalText, truncated);
        }
        else if (variants[0] == "generatedOutput")
        {
            ValidateGeneratedOutput(locator.GetProperty("generatedOutput"), requireHashIdentifiers: true);
        }
        else if (variants[0] == "metadata")
        {
            var metadata = locator.GetProperty("metadata");
            RequireObject(
                metadata,
                ["assemblyIdentity", "documentationCommentId"],
                [],
                AuditValidationCode.InvalidEvidence);
            RequireOpaqueId(GetString(metadata, "assemblyIdentity"), AuditValidationCode.InvalidEvidence);
            Require(
                !string.IsNullOrEmpty(GetString(metadata, "documentationCommentId")),
                AuditValidationCode.InvalidEvidence,
                "Metadata documentation comment ID cannot be empty.");
        }
        else
        {
            var synthetic = locator.GetProperty("synthetic");
            RequireObject(synthetic, ["fixtureId"], [], AuditValidationCode.InvalidEvidence);
            RequireOpaqueId(GetString(synthetic, "fixtureId"), AuditValidationCode.InvalidEvidence);
        }
    }

    private static void ValidateGeneratedOutput(JsonElement generated, bool requireHashIdentifiers)
    {
        RequireObject(
            generated,
            requireHashIdentifiers
                ? ["producerKind", "producerId", "outputId", "sourceSha256"]
                : ["producerKind", "producerId", "outputId"],
            requireHashIdentifiers ? ["span"] : [],
            AuditValidationCode.InvalidEvidence);
        var kind = GetString(generated, "producerKind");
        Require(
            kind is "source-generator" or "tool-generated",
            AuditValidationCode.InvalidEvidence,
            "Generated-output producer kind is invalid.");
        var producerPrefix = kind == "source-generator" ? "sgp." : "tgp.";
        var outputPrefix = kind == "source-generator" ? "sgo." : "tgo.";
        var producer = GetString(generated, "producerId");
        var output = GetString(generated, "outputId");
        Require(
            requireHashIdentifiers
                ? HashIdPattern(producerPrefix).IsMatch(producer)
                    && HashIdPattern(outputPrefix).IsMatch(output)
                : producer.StartsWith(producerPrefix, StringComparison.Ordinal)
                    && output.StartsWith(outputPrefix, StringComparison.Ordinal),
            AuditValidationCode.InvalidEvidence,
            "Generated-output identifiers do not match the producer kind.");
        if (generated.TryGetProperty("sourceSha256", out var hash))
        {
            Require(
                hash.ValueKind == JsonValueKind.String
                && Sha256Pattern().IsMatch(hash.GetString()!),
                AuditValidationCode.InvalidEvidence,
                "Generated-output source hash is invalid.");
        }

        ValidateSpan(generated, null, null, false);
    }

    private static void ValidateCandidateLocator(JsonElement locator)
    {
        Require(
            locator.ValueKind == JsonValueKind.Object,
            AuditValidationCode.InvalidClassification,
            "Candidate locator must be an object.");
        var variants = new[] { "repository", "generatedSource", "toolGenerated", "synthetic" }
            .Where(name => locator.TryGetProperty(name, out _))
            .ToArray();
        Require(
            variants.Length == 1 && locator.EnumerateObject().Count() == 1,
            AuditValidationCode.InvalidClassification,
            "Candidate locator requires exactly one variant.");
        var value = locator.GetProperty(variants[0]);
        if (variants[0] == "repository")
        {
            RequireObject(value, ["path"], ["span"], AuditValidationCode.InvalidClassification);
            RequireCanonicalPath(GetString(value, "path"), AuditValidationCode.InvalidClassification);
            ValidateSpan(value, null, null, false);
        }
        else if (variants[0] is "generatedSource" or "toolGenerated")
        {
            var generatedSource = variants[0] == "generatedSource";
            var producerName = generatedSource ? "generatorId" : "producerId";
            var outputName = generatedSource ? "hintNameId" : "outputId";
            RequireObject(
                value,
                [producerName, outputName],
                ["span"],
                AuditValidationCode.InvalidClassification);
            Require(
                HashIdPattern(generatedSource ? "sgp." : "tgp.").IsMatch(GetString(value, producerName))
                && HashIdPattern(generatedSource ? "sgo." : "tgo.").IsMatch(GetString(value, outputName)),
                AuditValidationCode.InvalidClassification,
                "Candidate generated identifiers are invalid.");
            ValidateSpan(value, null, null, false);
        }
        else
        {
            RequireObject(value, ["fixtureId"], [], AuditValidationCode.InvalidClassification);
            RequireOpaqueId(GetString(value, "fixtureId"), AuditValidationCode.InvalidClassification);
        }
    }

    private static void ValidateSpan(
        JsonElement parent,
        string? excerpt,
        string? originalText,
        bool truncated)
    {
        if (!parent.TryGetProperty("span", out var span))
        {
            return;
        }

        RequireObject(span, ["start", "end"], [], AuditValidationCode.InvalidEvidence);
        var start = GetNonNegativeInt32(span, "start");
        var end = GetNonNegativeInt32(span, "end");
        Require(
            start <= end,
            AuditValidationCode.InvalidEvidence,
            "A UTF-16 span is reversed.");
        if (excerpt is not null && (!truncated || originalText is not null))
        {
            Require(
                end <= (truncated ? originalText!.Length : excerpt.Length),
                AuditValidationCode.OriginalEvidenceMismatch,
                "The evidence span exceeds its original text.");
        }
    }

    private static JsonElement GetExpectedEvidenceSubject(JsonElement classification)
    {
        if (GetString(classification, "recordType") != "ComponentClassification")
        {
            return classification.GetProperty("symbolRef");
        }

        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["parentSymbolRef"] = JsonNode.Parse(
                classification.GetProperty("parentSymbolRef").GetRawText()),
            ["componentKind"] = GetString(classification, "componentKind"),
            ["identity"] = GetString(classification, "identity"),
        });
    }

    private static string GetClassificationContext(JsonElement classification) =>
        GetString(classification, "recordType") == "ComponentClassification"
            ? GetString(classification.GetProperty("parentSymbolRef"), "compilationContextRef")
            : GetString(classification.GetProperty("symbolRef"), "compilationContextRef");

    private static bool ObservationSubjectMatchesClassification(
        JsonElement subject,
        JsonElement classification)
    {
        if (GetString(classification, "recordType") == "TargetClassification")
        {
            return JsonElement.DeepEquals(subject, classification.GetProperty("symbolRef"));
        }

        return GetString(subject, "componentKind") == GetString(classification, "componentKind")
            && GetString(subject, "identity") == GetString(classification, "identity")
            && JsonElement.DeepEquals(
                subject.GetProperty("parentSymbolRef"),
                classification.GetProperty("parentSymbolRef"));
    }

    private static string ComputeDeclarationDigest(JsonElement declarations) =>
        Convert.ToHexString(
            SHA256.HashData(AuditCanonicalJson.CanonicalizeDeclarations(declarations)))
        .ToLowerInvariant();

    private static string ComputeObservationSubjectRef(JsonElement observation)
    {
        var subject = observation.GetProperty("subject");
        var canonicalSubject = subject.TryGetProperty("parentSymbolRef", out var parentSymbolRef)
            ? new JsonObject
            {
                ["parentSymbolRef"] = new JsonObject
                {
                    ["compilationContextRef"] = GetString(parentSymbolRef, "compilationContextRef"),
                    ["documentationCommentId"] = GetString(parentSymbolRef, "documentationCommentId"),
                },
                ["componentKind"] = GetString(subject, "componentKind"),
                ["identity"] = GetString(subject, "identity"),
            }
            : new JsonObject
            {
                ["compilationContextRef"] = GetString(subject, "compilationContextRef"),
                ["documentationCommentId"] = GetString(subject, "documentationCommentId"),
            };
        var preimage = new JsonObject
        {
            ["compilationContextRef"] = GetString(observation, "compilationContextRef"),
            ["subject"] = canonicalSubject,
            ["authoritativeDeclarationSetDigest"] =
                GetString(observation, "authoritativeDeclarationSetDigest"),
            ["authoritativeDeclarationCount"] =
                GetInt32(observation, "authoritativeDeclarationCount"),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(preimage, CompactJson);
        return "obs." + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string DeriveDocumentationObservation(
        JsonElement subject,
        JsonElement authority)
    {
        var declarations = authority.GetProperty("declarations").EnumerateArray().ToArray();
        Require(
            HasValidAuthorityMode(declarations),
            AuditValidationCode.InvalidAuthority,
            "Authority declarations do not form one closed selection mode.");
        var component = subject.TryGetProperty("parentSymbolRef", out _);
        if (!component)
        {
            if (declarations.Any(declaration =>
                GetString(declaration, "blockState") is "well-formed" or "malformed"))
            {
                return "documentation.present";
            }

            if (GetString(authority, "completeness") == "complete"
                && declarations.All(declaration =>
                    GetString(declaration, "blockState") is "no-block" or "whitespace-only"))
            {
                return "documentation.absent";
            }

            return "documentation.unavailable";
        }

        if (declarations.Any(declaration =>
            GetString(declaration, "blockState") == "well-formed"
            && declaration.TryGetProperty("componentMatch", out var match)
            && match.GetString() == "present"))
        {
            return "documentation.present";
        }

        if (declarations.Any(declaration => GetString(declaration, "blockState") == "malformed"))
        {
            return "documentation.unavailable";
        }

        if (GetString(authority, "completeness") == "complete"
            && declarations.All(declaration =>
                GetString(declaration, "blockState") is "no-block" or "whitespace-only"
                || declaration.TryGetProperty("componentMatch", out var match)
                    && match.GetString() == "absent"))
        {
            return "documentation.absent";
        }

        return "documentation.unavailable";
    }

    private static bool HasValidAuthorityMode(IReadOnlyCollection<JsonElement> declarations)
    {
        var roles = declarations
            .Select(declaration => GetString(declaration, "authorityRole"))
            .ToArray();
        return roles.Length == 1
                && roles[0] is "ordinary" or "partial-member-implementing" or
                    "partial-member-defining-fallback"
            || roles.Length > 0 && roles.All(role => role == "partial-type-part");
    }

    private static string GetSubjectKey(JsonElement classification) =>
        GetString(classification, "recordType") switch
        {
            "TargetClassification" => "target\0"
                + GetString(classification.GetProperty("symbolRef"), "compilationContextRef")
                + "\0"
                + GetString(classification.GetProperty("symbolRef"), "documentationCommentId"),
            "ComponentClassification" => "component\0"
                + GetString(classification.GetProperty("parentSymbolRef"), "compilationContextRef")
                + "\0"
                + GetString(classification.GetProperty("parentSymbolRef"), "documentationCommentId")
                + "\0"
                + GetString(classification, "componentKind")
                + "\0"
                + GetString(classification, "identity"),
            "UnresolvedClassification" => "unresolved\0"
                + GetString(classification, "compilationContextRef")
                + "\0"
                + CandidateLocatorKey(classification.GetProperty("candidateLocator")),
            _ => throw Failure(
                AuditValidationCode.InvalidClassification,
                "Unknown classification record type."),
        };

    private static string CandidateLocatorKey(JsonElement locator)
    {
        if (locator.TryGetProperty("repository", out var repository))
        {
            return "repository\0" + GetString(repository, "path") + "\0" + SpanKey(repository);
        }

        if (locator.TryGetProperty("generatedSource", out var generated))
        {
            return "generatedSource\0" + GetString(generated, "generatorId") + "\0"
                + GetString(generated, "hintNameId") + "\0" + SpanKey(generated);
        }

        if (locator.TryGetProperty("toolGenerated", out var toolGenerated))
        {
            return "toolGenerated\0" + GetString(toolGenerated, "producerId") + "\0"
                + GetString(toolGenerated, "outputId") + "\0" + SpanKey(toolGenerated);
        }

        return "synthetic\0" + GetString(locator.GetProperty("synthetic"), "fixtureId");
    }

    private static string SpanKey(JsonElement parent) =>
        !parent.TryGetProperty("span", out var span)
            ? "absent"
            : string.Concat(
                "present\0",
                GetInt32(span, "start").ToString(CultureInfo.InvariantCulture),
                "\0",
                GetInt32(span, "end").ToString(CultureInfo.InvariantCulture));

    internal static string PolicyContributionKey(JsonElement contribution)
    {
        var project = GetString(contribution, "projectPath");
        if (contribution.TryGetProperty("sourcePath", out var source))
        {
            return $"A\0{project}\0{source.GetString()}";
        }

        var generated = contribution.GetProperty("generatedOutput");
        return $"B\0{project}\0{GetString(generated, "producerKind")}\0{GetString(generated, "producerId")}\0{GetString(generated, "outputId")}";
    }

    internal static string NormalizeRepositoryPath(string value) =>
        string.Join(
            '/',
            value.Replace('\\', '/').Split('/')
                .Where(segment => segment is not "" and not "."));

    internal static void RejectUnpairedSurrogates(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                Require(
                    index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]),
                    AuditValidationCode.InvalidUtf8OrJson,
                    "The Audit Result contains an unpaired UTF-16 surrogate.");
                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                throw Failure(
                    AuditValidationCode.InvalidUtf8OrJson,
                    "The Audit Result contains an unpaired UTF-16 surrogate.");
            }
        }
    }

    internal static void ValidateCanonicalInteger(string raw)
    {
        if (raw == "0")
        {
            return;
        }

        var start = raw[0] == '-' ? 1 : 0;
        Require(
            start < raw.Length
            && raw[start] != '0'
            && raw[start..].All(character => character is >= '0' and <= '9'),
            AuditValidationCode.NonCanonicalBytes,
            "JSON numbers must be canonical integers.");
    }

    private static bool IsValidComponentIdentity(string kind, string identity) => kind switch
    {
        "component.parameter" => ParameterIdentityPattern().IsMatch(identity),
        "component.type-parameter" => TypeParameterIdentityPattern().IsMatch(identity),
        "component.return" => identity == "return",
        "component.value" => identity == "value",
        "component.accessor.get" => identity == "accessor/get",
        "component.accessor.set" => identity == "accessor/set",
        "component.accessor.init" => identity == "accessor/init",
        "component.accessor.add" => identity == "accessor/add",
        "component.accessor.remove" => identity == "accessor/remove",
        "component.backing-field" => identity == "backing-field",
        "component.synthesized.record-positional-property" =>
            SynthesizedPropertyIdentityPattern().IsMatch(identity),
        "component.synthesized.implicit-constructor" =>
            identity == "synthesized/implicit-constructor",
        "component.synthesized.record-copy-constructor" =>
            identity == "synthesized/record-copy-constructor",
        "component.synthesized.delegate-invoke" => identity == "synthesized/delegate-invoke",
        "component.synthesized.delegate-begin-invoke" =>
            identity == "synthesized/delegate-begin-invoke",
        "component.synthesized.delegate-end-invoke" =>
            identity == "synthesized/delegate-end-invoke",
        "component.unknown" => UnknownIdentityPattern().IsMatch(identity),
        _ => false,
    };

    private static void RequireObject(
        JsonElement value,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> optional,
        AuditValidationCode code)
    {
        Require(value.ValueKind == JsonValueKind.Object, code, "A required object is absent.");
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        Require(
            required.All(name => names.Contains(name, StringComparer.Ordinal))
            && names.All(name => required.Contains(name) || optional.Contains(name))
            && names.Length == names.Distinct(StringComparer.Ordinal).Count(),
            code,
            "An object has missing or unknown properties.");
    }

    private static JsonElement GetArray(JsonElement parent, string property)
    {
        var value = parent.GetProperty(property);
        Require(
            value.ValueKind == JsonValueKind.Array,
            AuditValidationCode.InvalidShape,
            $"'{property}' must be an array.");
        return value;
    }

    private static string GetString(JsonElement parent, string property) =>
        RequireStringValue(parent.GetProperty(property), AuditValidationCode.InvalidShape);

    private static string RequireStringValue(JsonElement value, AuditValidationCode code)
    {
        Require(value.ValueKind == JsonValueKind.String, code, "A required string is absent.");
        var text = value.GetString()!;
        RejectUnpairedSurrogates(text);
        return text;
    }

    private static int GetInt32(JsonElement parent, string property)
    {
        var value = parent.GetProperty(property);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw Failure(
                AuditValidationCode.InvalidShape,
                $"'{property}' must be a 32-bit integer.");
        }

        ValidateCanonicalInteger(value.GetRawText());
        return number;
    }

    private static int GetNonNegativeInt32(JsonElement parent, string property)
    {
        var value = GetInt32(parent, property);
        Require(
            value >= 0,
            AuditValidationCode.InvalidShape,
            $"'{property}' cannot be negative.");
        return value;
    }

    private static bool GetBoolean(JsonElement parent, string property)
    {
        var value = parent.GetProperty(property);
        Require(
            value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            AuditValidationCode.InvalidShape,
            $"'{property}' must be a boolean.");
        return value.GetBoolean();
    }

    private static void RequireKnown(
        IReadOnlySet<string> values,
        string value,
        AuditValidationCode code) =>
        Require(values.Contains(value), code, $"Unknown closed-vocabulary value '{value}'.");

    private static void RequireOpaqueId(string value, AuditValidationCode code) =>
        Require(
            OpaqueIdPattern().IsMatch(value),
            code,
            "A closed-boundary opaque identifier is invalid.");

    private static void RequireCanonicalPath(string value, AuditValidationCode code)
    {
        var driveLike = value.Length >= 2
            && value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
            && value[1] == ':';
        var segments = value.Replace('\\', '/').Split('/');
        Require(
            value.Length > 0
            && !value.Contains('\0')
            && value[0] is not '/' and not '\\'
            && !driveLike
            && !value.Contains('\\')
            && segments.All(segment => segment is not "" and not "." and not "..")
            && value == NormalizeRepositoryPath(value),
            code,
            "A repository-relative path is not canonical.");
    }

    private static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static AuditValidationException Failure(
        AuditValidationCode code,
        string message,
        Exception? innerException = null) =>
        new(code, message, innerException);

    private static void Require(
        bool condition,
        AuditValidationCode code,
        string message)
    {
        if (!condition)
        {
            throw Failure(code, message);
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueIdPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex RuleIdPattern();

    [GeneratedRegex("^[a-z][a-z0-9-]*(\\.[a-z][a-z0-9-]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex EvidenceIdPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^decl\\.[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DeclarationIdPattern();

    [GeneratedRegex("^obs\\.[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ObservationRefPattern();

    [GeneratedRegex("^parameter/[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterIdentityPattern();

    [GeneratedRegex("^type-parameter/[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeParameterIdentityPattern();

    [GeneratedRegex("^synthesized/record-positional-property/[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SynthesizedPropertyIdentityPattern();

    [GeneratedRegex("^unknown/[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex UnknownIdentityPattern();

    private static Regex HashIdPattern(string prefix) =>
        new($"^{Regex.Escape(prefix)}[0-9a-f]{{64}}$", RegexOptions.CultureInvariant);
}
