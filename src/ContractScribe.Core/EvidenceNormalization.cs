using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ContractScribe.Core;

public static class EvidenceNormalizer
{
    private const string InvalidEvidence = "evidence.normalization.invalid";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static EvidenceNormalizationOutcome Normalize(
        IEnumerable<EvidenceCandidateInput> candidates,
        IEnumerable<EvidenceOmissionReason>? encounteredOmissions = null,
        EvidenceBudgets? budgets = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        budgets ??= EvidenceBudgets.Production;
        if (budgets.MaximumItems <= 0
            || budgets.MaximumItemUtf8Bytes <= 0
            || budgets.MaximumBundleUtf8Bytes <= 0
            || budgets.MaximumItems > EvidenceBudgets.Production.MaximumItems
            || budgets.MaximumItemUtf8Bytes
                > EvidenceBudgets.Production.MaximumItemUtf8Bytes
            || budgets.MaximumBundleUtf8Bytes
                > EvidenceBudgets.Production.MaximumBundleUtf8Bytes)
        {
            return EvidenceNormalizationOutcome.Failure(InvalidEvidence);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var omissions = (encounteredOmissions ?? [])
                .ToImmutableArray();
            if (omissions.Any(reason => !Enum.IsDefined(reason)))
            {
                return EvidenceNormalizationOutcome.Failure(InvalidEvidence);
            }

            var normalizedCandidates = new List<NormalizedEvidenceCandidate>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate is null
                    || !ids.Add(candidate.EvidenceId)
                    || !TryNormalize(candidate, out var normalized))
                {
                    return EvidenceNormalizationOutcome.Failure(InvalidEvidence);
                }

                normalizedCandidates.Add(normalized!);
            }

            normalizedCandidates.Sort((left, right) =>
                string.CompareOrdinal(left.Input.EvidenceId, right.Input.EvidenceId));
            var omittedForItemLimit = normalizedCandidates.Count > budgets.MaximumItems;
            var selected = normalizedCandidates.Take(budgets.MaximumItems);
            var items = ImmutableArray.CreateBuilder<EvidenceItem>();
            var includedBundleBytes = 0;
            var truncated = false;
            foreach (var candidate in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remainingBundleBytes = Math.Max(
                    0,
                    budgets.MaximumBundleUtf8Bytes - includedBundleBytes);
                var maximumExcerptBytes = Math.Min(
                    budgets.MaximumItemUtf8Bytes,
                    remainingBundleBytes);
                var excerpt = Truncate(
                    candidate.Input.OriginalRegion,
                    maximumExcerptBytes,
                    cancellationToken);
                var includedBytes = StrictUtf8.GetByteCount(excerpt);
                if (candidate.OriginalBytes.Length > 0 && includedBytes == 0)
                {
                    truncated = true;
                    break;
                }

                includedBundleBytes += includedBytes;
                var isTruncated = includedBytes != candidate.OriginalBytes.Length;
                truncated |= isTruncated;
                items.Add(new EvidenceItem(
                    candidate.Input.EvidenceId,
                    candidate.Input.Subject,
                    candidate.Input.Kind,
                    candidate.Input.Relation,
                    excerpt,
                    candidate.Sha256,
                    candidate.OriginalBytes.Length,
                    includedBytes,
                    candidate.OriginalBytes.Length - includedBytes,
                    isTruncated,
                    candidate.Input.Locator));
            }

            var normalizedItems = items.ToImmutable();
            if (normalizedItems.IsEmpty)
            {
                if (truncated)
                {
                    return EvidenceNormalizationOutcome.Failure(InvalidEvidence);
                }

                var unavailableReason = SelectOmissionReason(
                    omissions.IsEmpty
                        ? [EvidenceOmissionReason.NotProvided]
                        : omissions);
                if (unavailableReason == EvidenceOmissionReason.BudgetExhausted)
                {
                    return EvidenceNormalizationOutcome.Failure(InvalidEvidence);
                }

                return EvidenceNormalizationOutcome.Success(new EvidenceBundle(
                    EvidenceAvailabilityStatus.Unavailable,
                    unavailableReason,
                    [],
                    null));
            }

            if (truncated || omittedForItemLimit)
            {
                return EvidenceNormalizationOutcome.Success(new EvidenceBundle(
                    EvidenceAvailabilityStatus.Partial,
                    EvidenceOmissionReason.BudgetExhausted,
                    normalizedItems,
                    null));
            }

            if (!omissions.IsEmpty)
            {
                return EvidenceNormalizationOutcome.Success(new EvidenceBundle(
                    EvidenceAvailabilityStatus.Partial,
                    SelectOmissionReason(omissions),
                    normalizedItems,
                    null));
            }

            return EvidenceNormalizationOutcome.Success(new EvidenceBundle(
                EvidenceAvailabilityStatus.Complete,
                null,
                normalizedItems,
                null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return EvidenceNormalizationOutcome.Cancelled();
        }
        catch (EncoderFallbackException)
        {
            return EvidenceNormalizationOutcome.Failure(InvalidEvidence);
        }
    }

    private static bool TryNormalize(
        EvidenceCandidateInput candidate,
        out NormalizedEvidenceCandidate? normalized)
    {
        normalized = null;
        if (candidate.OriginalRegion is null
            || !IsEvidenceId(candidate.EvidenceId)
            || !Enum.IsDefined(candidate.Kind)
            || !Enum.IsDefined(candidate.Relation)
            || !IsSubjectValid(candidate.Subject)
            || !IsLocatorValid(candidate.Locator, candidate.OriginalRegion))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(candidate.OriginalRegion);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (candidate.ExpectedSha256 is not null
            && !string.Equals(
                candidate.ExpectedSha256,
                sha256,
                StringComparison.Ordinal))
        {
            return false;
        }

        normalized = new NormalizedEvidenceCandidate(candidate, bytes, sha256);
        return true;
    }

    private static string Truncate(
        string value,
        int maximumUtf8Bytes,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(value.Length);
        var consumedBytes = 0;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = Rune.DecodeFromUtf16(
                remaining,
                out var rune,
                out var consumedCharacters);
            if (status != OperationStatus.Done)
            {
                throw new EncoderFallbackException("Evidence contains invalid UTF-16.");
            }

            if (consumedBytes + rune.Utf8SequenceLength > maximumUtf8Bytes)
            {
                break;
            }

            builder.Append(rune.ToString());
            consumedBytes += rune.Utf8SequenceLength;
            remaining = remaining[consumedCharacters..];
        }

        return builder.ToString();
    }

    private static EvidenceOmissionReason SelectOmissionReason(
        IEnumerable<EvidenceOmissionReason> reasons)
    {
        var values = reasons.ToHashSet();
        foreach (var candidate in new[]
        {
            EvidenceOmissionReason.AccessNotPermitted,
            EvidenceOmissionReason.SourceUnavailable,
            EvidenceOmissionReason.BinaryContent,
            EvidenceOmissionReason.BudgetExhausted,
            EvidenceOmissionReason.NotProvided,
        })
        {
            if (values.Contains(candidate))
            {
                return candidate;
            }
        }

        return EvidenceOmissionReason.NotProvided;
    }

    private static bool IsEvidenceId(string value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 128
        && value.Split('.').All(segment =>
            segment.Length > 0
            && segment[0] is >= 'a' and <= 'z'
            && segment.AsSpan(1).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789-") < 0);

    internal static bool IsSubjectValid(EvidenceSubject subject)
    {
        if (subject is null
            || !IsClosedId(subject.ParentSymbolRef.CompilationContextRef)
            || string.IsNullOrEmpty(subject.ParentSymbolRef.DocumentationCommentId))
        {
            return false;
        }

        return subject switch
        {
            TargetEvidenceSubject => true,
            ComponentEvidenceSubject component =>
                Enum.IsDefined(component.ComponentKind)
                && IsComponentIdentity(component.ComponentKind, component.Identity),
            _ => false,
        };
    }

    private static bool IsComponentIdentity(ComponentKind kind, string identity) =>
        !string.IsNullOrEmpty(identity)
        && kind switch
        {
            ComponentKind.Parameter => HasOrdinal(identity, "parameter/"),
            ComponentKind.TypeParameter => HasOrdinal(identity, "type-parameter/"),
            ComponentKind.Return => identity == "return",
            ComponentKind.Value => identity == "value",
            ComponentKind.AccessorGet => identity == "accessor/get",
            ComponentKind.AccessorSet => identity == "accessor/set",
            ComponentKind.AccessorInit => identity == "accessor/init",
            ComponentKind.AccessorAdd => identity == "accessor/add",
            ComponentKind.AccessorRemove => identity == "accessor/remove",
            ComponentKind.BackingField => identity == "backing-field",
            ComponentKind.SynthesizedRecordPositionalProperty =>
                HasOrdinal(identity, "synthesized/record-positional-property/"),
            ComponentKind.SynthesizedImplicitConstructor =>
                identity == "synthesized/implicit-constructor",
            ComponentKind.SynthesizedRecordCopyConstructor =>
                identity == "synthesized/record-copy-constructor",
            ComponentKind.SynthesizedDelegateInvoke =>
                identity == "synthesized/delegate-invoke",
            ComponentKind.SynthesizedDelegateBeginInvoke =>
                identity == "synthesized/delegate-begin-invoke",
            ComponentKind.SynthesizedDelegateEndInvoke =>
                identity == "synthesized/delegate-end-invoke",
            ComponentKind.Unknown => HasOrdinal(identity, "unknown/"),
            _ => false,
        };

    private static bool HasOrdinal(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal)
        && value.Length > prefix.Length
        && value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789") < 0;

    private static bool IsLocatorValid(EvidenceLocator locator, string originalRegion) =>
        locator switch
        {
            RepositoryEvidenceLocator repository =>
                IsRepositoryPath(repository.Path)
                && IsRegionSpanValid(repository.Span, originalRegion),
            GeneratedOutputEvidenceLocator generated =>
                IsGeneratedOutputLocatorValid(generated)
                && IsRegionSpanValid(generated.Span, originalRegion),
            MetadataEvidenceLocator metadata =>
                IsClosedId(metadata.AssemblyIdentity)
                && !string.IsNullOrEmpty(metadata.DocumentationCommentId),
            SyntheticEvidenceLocator synthetic => IsClosedId(synthetic.FixtureId),
            _ => false,
        };

    private static bool IsGeneratedOutputLocatorValid(
        GeneratedOutputEvidenceLocator locator)
    {
        if (!IsSha256(locator.SourceSha256))
        {
            return false;
        }

        return locator.ProducerKind switch
        {
            GeneratedOutputKind.SourceGenerator =>
                IsGeneratedId(locator.ProducerId, "sgp.")
                && IsGeneratedId(locator.OutputId, "sgo."),
            GeneratedOutputKind.ToolGenerated =>
                IsGeneratedId(locator.ProducerId, "tgp.")
                && IsGeneratedId(locator.OutputId, "tgo."),
            _ => false,
        };
    }

    private static bool IsRegionSpanValid(Utf16Span? span, string originalRegion) =>
        span is null
        || span.Value.Start >= 0
            && span.Value.End >= span.Value.Start
            && span.Value.End - span.Value.Start == originalRegion.Length;

    private static bool IsRepositoryPath(string value) =>
        !string.IsNullOrEmpty(value)
        && !value.Contains('\0')
        && !value.Contains('\\')
        && !value.StartsWith('/')
        && !(value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
        && value.Split('/').All(segment => segment is not "" and not "." and not "..");

    private static bool IsClosedId(string value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 128
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.AsSpan(1).IndexOfAnyExcept(
            "abcdefghijklmnopqrstuvwxyz0123456789._-") < 0;

    private static bool IsGeneratedId(string value, string prefix) =>
        value is not null
        && value.Length == prefix.Length + 64
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool IsSha256(string value) =>
        value is not null
        && value.Length == 64
        && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private sealed record NormalizedEvidenceCandidate(
        EvidenceCandidateInput Input,
        byte[] OriginalBytes,
        string Sha256);
}

public static class EvidenceObservationBinder
{
    private const string InvalidBinding = "evidence.binding.invalid";

    public static EvidenceBindingOutcome Bind(
        DocumentationObservation observation,
        EvidenceBundle bundle,
        IEnumerable<EvidenceDeclarationBindingInput> declarationBindings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(declarationBindings);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (observation.Value == DocumentationObservationValue.Unavailable
                && observation.UnavailableCause != DocumentationUnavailableCause.MalformedXml)
            {
                if (observation.UnavailableCause
                        != DocumentationUnavailableCause.SourceUnavailable
                    || observation.Completeness
                        != DocumentationAuthorityCompleteness.Incomplete)
                {
                    return EvidenceBindingOutcome.Failure(InvalidBinding);
                }

                return EvidenceBindingOutcome.Success(new BoundObservationEvidence(
                    observation.Value,
                    new EvidenceBundle(
                        EvidenceAvailabilityStatus.Unavailable,
                        EvidenceOmissionReason.SourceUnavailable,
                        [],
                        null),
                    [],
                    null,
                    supportsOrdinaryResult: false));
            }

            if (bundle.AvailabilityStatus != EvidenceAvailabilityStatus.Complete
                || bundle.OmissionReason is not null)
            {
                return EvidenceBindingOutcome.Success(new BoundObservationEvidence(
                    observation.Value,
                    WithoutObservationSubject(bundle),
                    [],
                    null,
                    supportsOrdinaryResult: false));
            }

            if (!TryCreateSubject(observation.Subject, out var subject)
                || !IsEligibleObservation(observation)
                || !HasValidAuthorityMode(observation.Declarations))
            {
                return EvidenceBindingOutcome.Failure(InvalidBinding);
            }

            var bindings = new Dictionary<
                string,
                EvidenceDeclarationBindingInput>(StringComparer.Ordinal);
            foreach (var binding in declarationBindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (binding is null || !bindings.TryAdd(binding.DeclarationId, binding))
                {
                    return EvidenceBindingOutcome.Failure(InvalidBinding);
                }
            }

            if (bindings.Count != observation.Declarations.Length
                || bindings.Keys.Any(id => observation.Declarations.All(
                    declaration => declaration.DeclarationId != id)))
            {
                return EvidenceBindingOutcome.Failure(InvalidBinding);
            }

            var items = bundle.Items.ToDictionary(
                item => item.EvidenceId,
                StringComparer.Ordinal);
            var rows = ImmutableArray.CreateBuilder<EvidenceAuthorityRow>();
            var hasPositive = false;
            foreach (var declaration in observation.Declarations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!bindings.TryGetValue(declaration.DeclarationId, out var binding))
                {
                    return EvidenceBindingOutcome.Failure(InvalidBinding);
                }

                var useDocumentation = RequiresDocumentationEvidence(
                    observation,
                    declaration);
                hasPositive |= useDocumentation
                    && observation.Value == DocumentationObservationValue.Present;
                var evidenceId = useDocumentation
                    ? binding.DocumentationEvidenceId
                    : binding.DeclarationEvidenceId;
                if (evidenceId is null
                    || !items.TryGetValue(evidenceId, out var item)
                    || item.IsTruncated
                    || !Equals(item.Subject, subject)
                    || !MatchesObservedFact(item, declaration, useDocumentation)
                    || useDocumentation
                        && (item.Kind != EvidenceKind.SourceXmlDocumentation
                            || item.Relation != EvidenceRelation.Documents)
                    || !useDocumentation
                        && (item.Kind != EvidenceKind.SourceDeclaration
                            || item.Relation != EvidenceRelation.Declares))
                {
                    return EvidenceBindingOutcome.Failure(InvalidBinding);
                }

                rows.Add(new EvidenceAuthorityRow(
                    declaration.DeclarationId,
                    declaration.AuthorityRole,
                    declaration.BlockState,
                    evidenceId,
                    declaration.ComponentLocalName,
                    declaration.ComponentMatch));
            }

            if (observation.Value == DocumentationObservationValue.Present
                && !hasPositive)
            {
                return EvidenceBindingOutcome.Failure(InvalidBinding);
            }

            var orderedRows = rows
                .OrderBy(row => row.DeclarationId, StringComparer.Ordinal)
                .ToImmutableArray();
            var digest = ComputeDeclarationDigest(orderedRows);
            var completeness = observation.Completeness switch
            {
                DocumentationAuthorityCompleteness.Complete =>
                    EvidenceAuthorityCompleteness.Complete,
                DocumentationAuthorityCompleteness.PositiveOnly
                    when observation.Value == DocumentationObservationValue.Present =>
                    EvidenceAuthorityCompleteness.PositiveOnly,
                _ => throw new InvalidOperationException(
                    "The observation cannot form an evidence authority set."),
            };
            var authority = new EvidenceAuthoritySet(
                "dset." + digest,
                completeness,
                orderedRows);
            var commitment = CreateObservationCommitment(subject!, digest, orderedRows.Length);
            var committedBundle = new EvidenceBundle(
                bundle.AvailabilityStatus,
                bundle.OmissionReason,
                bundle.Items,
                commitment);
            var evidenceIds = orderedRows
                .Select(row => row.EvidenceId)
                .Order(StringComparer.Ordinal)
                .ToImmutableArray();
            return EvidenceBindingOutcome.Success(new BoundObservationEvidence(
                observation.Value,
                committedBundle,
                evidenceIds,
                authority,
                supportsOrdinaryResult:
                    observation.Value is DocumentationObservationValue.Present
                        or DocumentationObservationValue.Absent));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return EvidenceBindingOutcome.Cancelled();
        }
        catch (InvalidOperationException)
        {
            return EvidenceBindingOutcome.Failure(InvalidBinding);
        }
    }

    private static bool MatchesObservedFact(
        EvidenceItem item,
        DocumentationDeclarationFact declaration,
        bool useDocumentation)
    {
        var expectedText = useDocumentation
            ? declaration.DocumentationText
            : declaration.DeclarationText;
        var expectedSha256 = useDocumentation
            ? declaration.DocumentationSha256
            : declaration.DeclarationSha256;
        var expectedSpan = useDocumentation
            ? declaration.DocumentationSpan
            : declaration.DeclarationSpan;
        if (expectedText is null
            || expectedSha256 is null
            || expectedSpan is null
            || !string.Equals(item.Excerpt, expectedText, StringComparison.Ordinal)
            || !string.Equals(item.Sha256, expectedSha256, StringComparison.Ordinal))
        {
            return false;
        }

        return (declaration.Source, item.Locator) switch
        {
            (
                RepositoryDocumentationSourceIdentity repository,
                RepositoryEvidenceLocator locator) =>
                string.Equals(repository.Path, locator.Path, StringComparison.Ordinal)
                && locator.Span == expectedSpan,
            (
                GeneratedDocumentationSourceIdentity generated,
                GeneratedOutputEvidenceLocator locator) =>
                locator.ProducerKind == GeneratedKind(generated.Kind)
                && string.Equals(
                    locator.ProducerId,
                    generated.ProducerId,
                    StringComparison.Ordinal)
                && string.Equals(
                    locator.OutputId,
                    generated.OutputId,
                    StringComparison.Ordinal)
                && string.Equals(
                    locator.SourceSha256,
                    generated.SourceSha256,
                    StringComparison.Ordinal)
                && locator.Span == expectedSpan,
            _ => false,
        };
    }

    private static GeneratedOutputKind GeneratedKind(
        DocumentationSourceKind sourceKind) => sourceKind switch
        {
            DocumentationSourceKind.SourceGenerator =>
                GeneratedOutputKind.SourceGenerator,
            DocumentationSourceKind.ToolGenerated =>
                GeneratedOutputKind.ToolGenerated,
            _ => throw new InvalidOperationException(
                "Unknown documentation source kind."),
        };

    private static bool IsEligibleObservation(DocumentationObservation observation) =>
        observation.Value is DocumentationObservationValue.Present
            or DocumentationObservationValue.Absent
        || observation.Value == DocumentationObservationValue.Unavailable
            && observation.UnavailableCause == DocumentationUnavailableCause.MalformedXml;

    private static bool RequiresDocumentationEvidence(
        DocumentationObservation observation,
        DocumentationDeclarationFact declaration)
    {
        if (observation.Value == DocumentationObservationValue.Absent)
        {
            if (declaration.ParentSubstantive
                || declaration.ComponentMatch == DocumentationComponentMatch.Present)
            {
                throw new InvalidOperationException(
                    "Absent authority contradicts applicable documentation evidence.");
            }

            return false;
        }

        if (observation.Value == DocumentationObservationValue.Unavailable)
        {
            return declaration.BlockState == DocumentationBlockState.Malformed;
        }

        return observation.Subject.ComponentKind is null
            ? declaration.ParentSubstantive
            : declaration.BlockState == DocumentationBlockState.WellFormed
                && declaration.ComponentMatch == DocumentationComponentMatch.Present;
    }

    private static bool TryCreateSubject(
        DocumentationObservationSubject observationSubject,
        out EvidenceSubject? subject)
    {
        subject = observationSubject.ComponentKind is { } componentKind
            && observationSubject.ComponentIdentity is { } identity
            ? new ComponentEvidenceSubject(
                observationSubject.ParentSymbolRef,
                componentKind,
                identity)
            : observationSubject.ComponentKind is null
                && observationSubject.ComponentIdentity is null
                ? new TargetEvidenceSubject(observationSubject.ParentSymbolRef)
                : null;
        return subject is not null && EvidenceNormalizer.IsSubjectValid(subject);
    }

    private static bool HasValidAuthorityMode(
        ImmutableArray<DocumentationDeclarationFact> declarations)
    {
        if (declarations.IsEmpty)
        {
            return false;
        }

        var roles = declarations.Select(declaration => declaration.AuthorityRole).ToArray();
        return roles.Length == 1
                && roles[0] is DocumentationAuthorityRole.Ordinary
                    or DocumentationAuthorityRole.PartialMemberImplementing
                    or DocumentationAuthorityRole.PartialMemberDefiningFallback
            || roles.All(role => role == DocumentationAuthorityRole.PartialTypePart);
    }

    private static EvidenceBundle WithoutObservationSubject(EvidenceBundle bundle) =>
        bundle.ObservationSubject is null
            ? bundle
            : new EvidenceBundle(
                bundle.AvailabilityStatus,
                bundle.OmissionReason,
                bundle.Items,
                null);

    private static string ComputeDeclarationDigest(
        ImmutableArray<EvidenceAuthorityRow> rows)
    {
        using var stream = new MemoryStream();
        using (var writer = CreateWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writer.WriteString("declarationId", row.DeclarationId);
                writer.WriteString("authorityRole", AuthorityRole(row.AuthorityRole));
                writer.WriteString("blockState", BlockState(row.BlockState));
                writer.WriteString("evidenceId", row.EvidenceId);
                if (row.ComponentLocalName is not null)
                {
                    writer.WriteString("componentLocalName", row.ComponentLocalName);
                }

                if (row.ComponentMatch is { } componentMatch)
                {
                    writer.WriteString("componentMatch", ComponentMatch(componentMatch));
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static EvidenceObservationCommitment CreateObservationCommitment(
        EvidenceSubject subject,
        string declarationDigest,
        int declarationCount)
    {
        var context = subject.ParentSymbolRef.CompilationContextRef;
        using var stream = new MemoryStream();
        using (var writer = CreateWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("compilationContextRef", context);
            writer.WritePropertyName("subject");
            WriteSubject(writer, subject);
            writer.WriteString(
                "authoritativeDeclarationSetDigest",
                declarationDigest);
            writer.WriteNumber("authoritativeDeclarationCount", declarationCount);
            writer.WriteEndObject();
        }

        var reference = "obs."
            + Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
        return new EvidenceObservationCommitment(
            reference,
            context,
            subject,
            declarationDigest,
            declarationCount);
    }

    private static void WriteSubject(Utf8JsonWriter writer, EvidenceSubject subject)
    {
        writer.WriteStartObject();
        if (subject is ComponentEvidenceSubject component)
        {
            writer.WritePropertyName("parentSymbolRef");
            WriteSymbolRef(writer, component.ParentSymbolRef);
            writer.WriteString(
                "componentKind",
                ClassificationVocabulary.GetId(component.ComponentKind));
            writer.WriteString("identity", component.Identity);
        }
        else
        {
            writer.WriteString(
                "compilationContextRef",
                subject.ParentSymbolRef.CompilationContextRef);
            writer.WriteString(
                "documentationCommentId",
                subject.ParentSymbolRef.DocumentationCommentId);
        }

        writer.WriteEndObject();
    }

    private static void WriteSymbolRef(Utf8JsonWriter writer, SymbolRef symbolRef)
    {
        writer.WriteStartObject();
        writer.WriteString("compilationContextRef", symbolRef.CompilationContextRef);
        writer.WriteString("documentationCommentId", symbolRef.DocumentationCommentId);
        writer.WriteEndObject();
    }

    private static Utf8JsonWriter CreateWriter(Stream stream) =>
        new(
            stream,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Indented = false,
                SkipValidation = false,
            });

    private static string AuthorityRole(DocumentationAuthorityRole value) => value switch
    {
        DocumentationAuthorityRole.Ordinary => "ordinary",
        DocumentationAuthorityRole.PartialTypePart => "partial-type-part",
        DocumentationAuthorityRole.PartialMemberImplementing =>
            "partial-member-implementing",
        DocumentationAuthorityRole.PartialMemberDefiningFallback =>
            "partial-member-defining-fallback",
        _ => throw new InvalidOperationException("Unknown authority role."),
    };

    private static string BlockState(DocumentationBlockState value) => value switch
    {
        DocumentationBlockState.NoBlock => "no-block",
        DocumentationBlockState.WhitespaceOnly => "whitespace-only",
        DocumentationBlockState.WellFormed => "well-formed",
        DocumentationBlockState.Malformed => "malformed",
        _ => throw new InvalidOperationException("Unknown block state."),
    };

    private static string ComponentMatch(DocumentationComponentMatch value) => value switch
    {
        DocumentationComponentMatch.Present => "present",
        DocumentationComponentMatch.Absent => "absent",
        _ => throw new InvalidOperationException("Unknown component match."),
    };
}
