using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContractScribe.Core;

public static class DocumentationScribeValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static DocumentationScribeRequestParseResult ParseRequest(ReadOnlyMemory<byte> utf8Json)
    {
        var rawFailure = TryParseArtifact(
            utf8Json,
            "scribe.request",
            "scribeRequestVersion",
            out var document);
        if (rawFailure is not null)
        {
            return new DocumentationScribeRequestParseResult(
                null,
                new DocumentationScribeRequestValidationFailure(rawFailure.Code, rawFailure.Pointer));
        }

        using (document)
        {
            try
            {
                var root = document!.RootElement;
                ExpectProperties(
                    root,
                    string.Empty,
                    [
                        "scribeRequestVersion",
                        "context",
                        "target",
                        "styleProfile",
                        "contextReferences",
                        "evidenceReferences",
                        "evidenceConflicts",
                        "toolPolicyId",
                        "limits",
                    ]);

                var context = ParseContext(root.GetProperty("context"), "/context");
                var target = ParseTarget(root.GetProperty("target"), "/target");
                var style = ParseStyleProfile(
                    root.GetProperty("styleProfile"),
                    "/styleProfile",
                    target.ApplicableComponents);
                var contextReferences = ParseContextReferences(
                    root.GetProperty("contextReferences"),
                    "/contextReferences",
                    context.RepositoryContextRef);
                var evidenceReferences = ParseEvidenceReferences(
                    root.GetProperty("evidenceReferences"),
                    "/evidenceReferences",
                    context.RepositoryContextRef,
                    target,
                    style);
                var evidenceConflicts = ParseEvidenceConflicts(
                    root.GetProperty("evidenceConflicts"),
                    "/evidenceConflicts",
                    evidenceReferences);
                var toolPolicyId = ReadIdentifier(root, "toolPolicyId", string.Empty);
                var limits = ParseLimits(root.GetProperty("limits"), "/limits");

                if (contextReferences.Length > limits.MaximumContextReferences)
                {
                    throw Fail("over-budget", "/contextReferences");
                }

                var includedContextBytes = contextReferences.Aggregate(
                    0L,
                    (total, reference) => checked(total + reference.IncludedUtf8ByteCount));
                if (includedContextBytes > limits.MaximumContextUtf8Bytes)
                {
                    throw Fail("over-budget", "/contextReferences");
                }

                if (evidenceReferences.Length > limits.MaximumEvidenceReferences)
                {
                    throw Fail("over-budget", "/evidenceReferences");
                }

                var includedBytes = evidenceReferences.Aggregate(
                    0L,
                    (total, reference) => checked(total + reference.IncludedUtf8ByteCount));
                if (includedBytes > limits.MaximumEvidenceUtf8Bytes)
                {
                    throw Fail("over-budget", "/evidenceReferences");
                }

                return new DocumentationScribeRequestParseResult(
                    new DocumentationScribeRequest(
                        Convert.ToHexString(SHA256.HashData(utf8Json.Span)).ToLowerInvariant(),
                        context,
                        target,
                        style,
                        contextReferences,
                        evidenceReferences,
                        evidenceConflicts,
                        toolPolicyId,
                        limits),
                    null);
            }
            catch (ContractFailure failure)
            {
                return new DocumentationScribeRequestParseResult(
                    null,
                    new DocumentationScribeRequestValidationFailure(
                        "scribe.request." + failure.Category,
                        failure.Pointer));
            }
            catch (OverflowException)
            {
                return new DocumentationScribeRequestParseResult(
                    null,
                    new DocumentationScribeRequestValidationFailure(
                        "scribe.request.over-budget",
                        "/evidenceReferences"));
            }
        }
    }

    public static DocumentationScribeResultParseResult ParseRunResult(
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId expectedAttemptId,
        ReadOnlyMemory<byte> utf8Json)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(expectedAttemptId.Value))
        {
            throw new ArgumentException("A validated attempt identity is required.", nameof(expectedAttemptId));
        }

        var rawFailure = TryParseArtifact(
            utf8Json,
            "scribe.result",
            "scribeRunResultVersion",
            out var document);
        if (rawFailure is not null)
        {
            return new DocumentationScribeResultParseResult(
                null,
                new DocumentationScribeResultValidationFailure(rawFailure.Code, rawFailure.Pointer));
        }

        using (document)
        {
            try
            {
                var root = document!.RootElement;
                ExpectProperties(
                    root,
                    string.Empty,
                    ["scribeRunResultVersion", "scribeRequestSha256", "attemptId", "terminal", "runEnvelope"]);

                var requestSha256 = ReadSha256(root, "scribeRequestSha256", string.Empty);
                var attemptId = ParseAttemptId(ReadString(root, "attemptId", string.Empty, 64), "/attemptId");
                if (!string.Equals(requestSha256, request.ArtifactSha256, StringComparison.Ordinal)
                    || attemptId != expectedAttemptId)
                {
                    throw Fail("invalid-correlation", "/scribeRequestSha256");
                }

                var terminal = ParseTerminal(root.GetProperty("terminal"), "/terminal", request);
                var envelope = ParseRunEnvelope(
                    root.GetProperty("runEnvelope"),
                    "/runEnvelope",
                    request,
                    expectedAttemptId);

                return new DocumentationScribeResultParseResult(
                    new DocumentationScribeRunResult(requestSha256, attemptId, terminal, envelope),
                    null);
            }
            catch (ContractFailure failure)
            {
                return new DocumentationScribeResultParseResult(
                    null,
                    new DocumentationScribeResultValidationFailure(
                        "scribe.result." + failure.Category,
                        failure.Pointer));
            }
        }
    }

    public static DocumentationScribeRunResult CreateFailureResult(
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId,
        DocumentationScribeFailureCode code,
        DocumentationScribeRunEnvelopeInput envelope)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        return CreateRuntimeResult(
            request,
            attemptId,
            new DocumentationScribeFailureTerminal(code),
            envelope);
    }

    public static DocumentationScribeRunResult CreateCancelledResult(
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId,
        DocumentationScribeCancellationCode code,
        DocumentationScribeRunEnvelopeInput envelope)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        return CreateRuntimeResult(
            request,
            attemptId,
            new DocumentationScribeCancelledTerminal(code),
            envelope);
    }

    public static DocumentationScribeRunResult CreateSkipResult(
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId,
        DocumentationScribeSkipReason reason,
        IEnumerable<string> evidenceReferenceIds,
        DocumentationScribeRunEnvelopeInput envelope)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(evidenceReferenceIds);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        var ids = evidenceReferenceIds.ToImmutableArray();
        if (ids.Length > request.StyleProfile.MaximumEvidenceRefsPerUnit
            || !IsStrictlyIncreasing(ids)
            || ids.Any(id => !request.EvidenceReferences.Any(reference =>
                string.Equals(reference.EvidenceReferenceId, id, StringComparison.Ordinal))))
        {
            throw new ArgumentException("Evidence references are not valid for the parsed request.", nameof(evidenceReferenceIds));
        }

        return CreateRuntimeResult(
            request,
            attemptId,
            new DocumentationScribeSkipTerminal(reason, ids),
            envelope);
    }

    private static DocumentationScribeRunResult CreateRuntimeResult(
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId,
        DocumentationScribeTerminal terminal,
        DocumentationScribeRunEnvelopeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrEmpty(attemptId.Value))
        {
            throw new ArgumentException("A validated attempt identity is required.", nameof(attemptId));
        }

        var envelope = CreateRunEnvelope(request, attemptId, input);
        return new DocumentationScribeRunResult(request.ArtifactSha256, attemptId, terminal, envelope);
    }

    private static DocumentationScribeRunEnvelope CreateRunEnvelope(
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId,
        DocumentationScribeRunEnvelopeInput input)
    {
        if (!IsIdentifier(input.ProviderConfigurationId, allowSlash: false)
            || !IsIdentifier(input.ModelConfigurationId, allowSlash: false)
            || !IsIdentifier(input.ScribeProtocolId, allowSlash: false)
            || input.AttemptNumber < 1
            || input.ProviderRequestCount < 0
            || input.ProviderRequestCount > request.Limits.MaximumProviderRequests
            || input.ToolRoundCount < 0
            || input.ToolRoundCount > request.Limits.MaximumToolRounds
            || input.ToolCallCount < 0
            || input.ToolCallCount > request.Limits.MaximumToolCalls
            || input.ElapsedMilliseconds < 0
            || input.ElapsedMilliseconds > request.Limits.MaximumElapsedMilliseconds)
        {
            throw new ArgumentException("The run envelope input is outside the parsed request boundary.", nameof(input));
        }

        DocumentationScribeUsageObservation? usage = null;
        if (input.Usage is not null)
        {
            if (input.Usage.InputTokens is null
                && input.Usage.OutputTokens is null
                && input.Usage.CachedInputTokens is null
                && input.Usage.ReasoningTokens is null)
            {
                throw new ArgumentException("A usage observation must contain at least one field.", nameof(input));
            }

            ValidateOptionalObservation(input.Usage.InputTokens, request.Limits.MaximumInputTokens, nameof(input));
            ValidateOptionalObservation(input.Usage.OutputTokens, request.Limits.MaximumOutputTokens, nameof(input));
            ValidateOptionalObservation(input.Usage.CachedInputTokens, request.Limits.MaximumInputTokens, nameof(input));
            ValidateOptionalObservation(input.Usage.ReasoningTokens, request.Limits.MaximumOutputTokens, nameof(input));
            usage = new DocumentationScribeUsageObservation(
                input.Usage.InputTokens,
                input.Usage.OutputTokens,
                input.Usage.CachedInputTokens,
                input.Usage.ReasoningTokens);
        }

        DocumentationScribeCostObservation? cost = null;
        if (input.Cost is not null)
        {
            if (!IsIdentifier(input.Cost.CurrencyId, allowSlash: false)
                || input.Cost.AmountMicrounits < 0
                || input.Cost.AmountMicrounits > request.Limits.MaximumCostMicrounits)
            {
                throw new ArgumentException("The cost observation is outside the parsed request boundary.", nameof(input));
            }

            cost = new DocumentationScribeCostObservation(input.Cost.CurrencyId, input.Cost.AmountMicrounits);
        }

        if (input.Cache is { } cacheValue && !Enum.IsDefined(cacheValue))
        {
            throw new ArgumentException("The cache observation is not defined.", nameof(input));
        }

        if (input.Diagnostics.IsDefault
            || input.Diagnostics.Length > DocumentationScribeContract.MaximumDiagnostics)
        {
            throw new ArgumentException("The diagnostic collection is not bounded.", nameof(input));
        }

        var diagnostics = ImmutableArray.CreateBuilder<DocumentationScribeDiagnostic>();
        string? priorKey = null;
        foreach (var diagnostic in input.Diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostic);
            if (!IsFactoryDiagnosticValid(diagnostic))
            {
                throw new ArgumentException("A diagnostic does not match its allowlisted shape.", nameof(input));
            }

            var key = diagnostic.Code
                + "\0"
                + diagnostic.Stage
                + "\0"
                + diagnostic.ReferenceId
                + "\0"
                + diagnostic.ValidationCode;
            if (priorKey is not null && string.CompareOrdinal(priorKey, key) >= 0)
            {
                throw new ArgumentException("Diagnostics are not in stable order.", nameof(input));
            }

            priorKey = key;
            diagnostics.Add(new DocumentationScribeDiagnostic(
                diagnostic.Code,
                diagnostic.Stage,
                diagnostic.ReferenceId,
                diagnostic.ValidationCode));
        }

        return new DocumentationScribeRunEnvelope(
            request.ArtifactSha256,
            attemptId,
            input.ProviderConfigurationId,
            input.ModelConfigurationId,
            input.ScribeProtocolId,
            request.ToolPolicyId,
            request.StyleProfile.StyleProfileId,
            input.AttemptNumber,
            input.ProviderRequestCount,
            input.ToolRoundCount,
            input.ToolCallCount,
            input.ElapsedMilliseconds,
            usage,
            input.Cache,
            cost,
            diagnostics.ToImmutable());
    }

    private static bool IsFactoryDiagnosticValid(DocumentationScribeDiagnosticInput diagnostic)
    {
        if (diagnostic.ReferenceId is not null && !IsIdentifier(diagnostic.ReferenceId, allowSlash: false)
            || diagnostic.ValidationCode is not null && !IsIdentifier(diagnostic.ValidationCode, allowSlash: false))
        {
            return false;
        }

        return diagnostic.Code switch
        {
            "scribe.diagnostic.provider-failure" =>
                diagnostic.Stage == "provider" && diagnostic.ReferenceId is null && diagnostic.ValidationCode is null,
            "scribe.diagnostic.tool-failure" =>
                diagnostic.Stage == "tool" && diagnostic.ReferenceId is not null && diagnostic.ValidationCode is null,
            "scribe.diagnostic.result-rejected" =>
                diagnostic.Stage == "result" && diagnostic.ReferenceId is null && diagnostic.ValidationCode is not null,
            "scribe.diagnostic.runtime-failure" =>
                diagnostic.Stage == "runtime" && diagnostic.ReferenceId is null && diagnostic.ValidationCode is null,
            _ => false,
        };
    }

    private static void ValidateOptionalObservation(int? value, int maximum, string parameterName)
    {
        if (value is < 0 || value > maximum)
        {
            throw new ArgumentException("An observation is outside the parsed request boundary.", parameterName);
        }
    }

    private static bool IsStrictlyIncreasing(ImmutableArray<string> values)
    {
        string? prior = null;
        foreach (var value in values)
        {
            if (!IsIdentifier(value, allowSlash: false)
                || prior is not null && string.CompareOrdinal(prior, value) >= 0)
            {
                return false;
            }

            prior = value;
        }

        return true;
    }

    private static DocumentationScribeRequestContext ParseContext(JsonElement element, string pointer)
    {
        ExpectProperties(
            element,
            pointer,
            ["repositoryContextRef", "inputIdentity", "targetProfile", "auditOutcome"]);
        var rawContextRef = ReadString(element, "repositoryContextRef", pointer, 64);
        if (!RepositoryContextRef.TryParse(rawContextRef, out var contextRef))
        {
            throw Fail("invalid-vocabulary", pointer + "/repositoryContextRef");
        }

        var inputIdentity = ReadRepositoryRelativePath(element, "inputIdentity", pointer);
        var targetProfile = ReadString(element, "targetProfile", pointer, 64) switch
        {
            "profile.external-api" => TargetProfile.ExternalApi,
            "profile.assembly-visible" => TargetProfile.AssemblyVisible,
            _ => throw Fail("invalid-vocabulary", pointer + "/targetProfile"),
        };
        var auditOutcome = ReadString(element, "auditOutcome", pointer, 64) switch
        {
            "audit.outcome.compliant" => AuditOutcome.Compliant,
            "audit.outcome.violation" => AuditOutcome.Violation,
            "audit.outcome.skipped" => AuditOutcome.Skipped,
            _ => throw Fail("invalid-vocabulary", pointer + "/auditOutcome"),
        };

        return new DocumentationScribeRequestContext(contextRef, inputIdentity, targetProfile, auditOutcome);
    }

    private static DocumentationScribeTarget ParseTarget(JsonElement element, string pointer)
    {
        ExpectProperties(element, pointer, ["symbolRef", "sourceCommitment", "applicableComponents"]);
        var symbolRef = ParseSymbolRef(element.GetProperty("symbolRef"), pointer + "/symbolRef");
        var commitment = element.GetProperty("sourceCommitment");
        ExpectProperties(commitment, pointer + "/sourceCommitment", ["locator", "contentSha256"]);
        var locator = ParseEvidenceLocator(commitment.GetProperty("locator"), pointer + "/sourceCommitment/locator");
        var sha256 = ReadSha256(commitment, "contentSha256", pointer + "/sourceCommitment");
        var components = ParseApplicableComponents(
            element.GetProperty("applicableComponents"),
            pointer + "/applicableComponents");
        return new DocumentationScribeTarget(symbolRef, locator, sha256, components);
    }

    private static ImmutableArray<DocumentationPatchApplicableComponent> ParseApplicableComponents(
        JsonElement element,
        string pointer)
    {
        ExpectArray(element, pointer, 0, 128);
        var builder = ImmutableArray.CreateBuilder<DocumentationPatchApplicableComponent>();
        string? priorIdentity = null;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            ExpectProperties(item, itemPointer, ["kind", "identity"], ["name"]);
            var kind = ReadString(item, "kind", itemPointer, 32) switch
            {
                "typeParameter" => DocumentationPatchComponentKind.TypeParameter,
                "parameter" => DocumentationPatchComponentKind.Parameter,
                "return" => DocumentationPatchComponentKind.Return,
                "value" => DocumentationPatchComponentKind.Value,
                _ => throw Fail("invalid-vocabulary", itemPointer + "/kind"),
            };
            var identity = ReadIdentifier(item, "identity", itemPointer, allowSlash: true);
            var name = item.TryGetProperty("name", out _)
                ? ReadString(item, "name", itemPointer, 256)
                : null;
            if ((kind is DocumentationPatchComponentKind.Parameter or DocumentationPatchComponentKind.TypeParameter) != (name is not null))
            {
                throw Fail("invalid-shape", itemPointer + "/name");
            }

            if (priorIdentity is not null && string.CompareOrdinal(priorIdentity, identity) >= 0)
            {
                throw Fail("invalid-order", itemPointer + "/identity");
            }

            priorIdentity = identity;
            builder.Add(new DocumentationPatchApplicableComponent(kind, identity, name));
            index++;
        }

        return builder.ToImmutable();
    }

    private static DocumentationScribeStyleProfile ParseStyleProfile(
        JsonElement element,
        string pointer,
        ImmutableArray<DocumentationPatchApplicableComponent> components)
    {
        ExpectProperties(
            element,
            pointer,
            [
                "styleProfileId",
                "outputLanguageId",
                "summary",
                "remarks",
                "exceptions",
                "componentPolicies",
                "inheritDocDisposition",
                "allowedLiterals",
                "forbiddenLiterals",
                "claimPolicies",
                "maximumContentUnits",
                "maximumEvidenceRefsPerUnit",
            ]);

        var styleProfileId = ReadIdentifier(element, "styleProfileId", pointer);
        var languageId = ReadIdentifier(element, "outputLanguageId", pointer);
        var summary = ParseTextPolicy(element.GetProperty("summary"), pointer + "/summary");
        var remarks = ParseTextPolicy(element.GetProperty("remarks"), pointer + "/remarks");
        var exceptions = ParseTextPolicy(element.GetProperty("exceptions"), pointer + "/exceptions");
        var componentPolicies = ParseComponentPolicies(
            element.GetProperty("componentPolicies"),
            pointer + "/componentPolicies",
            components);
        var inheritDocDisposition = ReadString(element, "inheritDocDisposition", pointer, 16) switch
        {
            "allowed" => DocumentationScribeInheritDocDisposition.Allowed,
            "required" => DocumentationScribeInheritDocDisposition.Required,
            "forbidden" => DocumentationScribeInheritDocDisposition.Forbidden,
            _ => throw Fail("invalid-vocabulary", pointer + "/inheritDocDisposition"),
        };
        var allowedLiterals = ParseOrderedStrings(
            element.GetProperty("allowedLiterals"),
            pointer + "/allowedLiterals",
            0,
            128,
            256);
        var forbiddenLiterals = ParseOrderedStrings(
            element.GetProperty("forbiddenLiterals"),
            pointer + "/forbiddenLiterals",
            0,
            128,
            256);
        if (allowedLiterals.Any(value => forbiddenLiterals.BinarySearch(value, StringComparer.Ordinal) >= 0))
        {
            throw Fail("invalid-style", pointer + "/forbiddenLiterals");
        }

        var claimPolicies = ParseClaimPolicies(element.GetProperty("claimPolicies"), pointer + "/claimPolicies");
        var maximumContentUnits = ReadBoundedInt(
            element,
            "maximumContentUnits",
            pointer,
            1,
            DocumentationScribeContract.MaximumContentUnits);
        var maximumEvidenceRefsPerUnit = ReadBoundedInt(
            element,
            "maximumEvidenceRefsPerUnit",
            pointer,
            1,
            DocumentationScribeContract.MaximumReferences);

        if (inheritDocDisposition == DocumentationScribeInheritDocDisposition.Required
            && (summary.Disposition != DocumentationScribePolicyDisposition.Forbidden
                || remarks.Disposition != DocumentationScribePolicyDisposition.Forbidden
                || exceptions.Disposition != DocumentationScribePolicyDisposition.Forbidden
                || componentPolicies.Any(policy => policy.Disposition != DocumentationScribePolicyDisposition.Forbidden)))
        {
            throw Fail("invalid-style", pointer + "/inheritDocDisposition");
        }

        return new DocumentationScribeStyleProfile(
            styleProfileId,
            languageId,
            summary,
            remarks,
            exceptions,
            componentPolicies,
            inheritDocDisposition,
            allowedLiterals,
            forbiddenLiterals,
            claimPolicies,
            maximumContentUnits,
            maximumEvidenceRefsPerUnit);
    }

    private static DocumentationScribeTextPolicy ParseTextPolicy(JsonElement element, string pointer)
    {
        ExpectProperties(element, pointer, ["disposition", "maximumScalars"]);
        var disposition = ParsePolicyDisposition(ReadString(element, "disposition", pointer, 16), pointer + "/disposition");
        var maximumScalars = ReadBoundedInt(
            element,
            "maximumScalars",
            pointer,
            0,
            DocumentationScribeContract.MaximumTextScalars);
        if (disposition != DocumentationScribePolicyDisposition.Forbidden && maximumScalars == 0)
        {
            throw Fail("invalid-style", pointer + "/maximumScalars");
        }

        return new DocumentationScribeTextPolicy(disposition, maximumScalars);
    }

    private static ImmutableArray<DocumentationScribeComponentPolicy> ParseComponentPolicies(
        JsonElement element,
        string pointer,
        ImmutableArray<DocumentationPatchApplicableComponent> components)
    {
        ExpectArray(element, pointer, components.Length, components.Length);
        var builder = ImmutableArray.CreateBuilder<DocumentationScribeComponentPolicy>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            ExpectProperties(item, itemPointer, ["componentIdentity", "disposition", "maximumScalars"]);
            var identity = ReadIdentifier(item, "componentIdentity", itemPointer, allowSlash: true);
            if (!string.Equals(identity, components[index].Identity, StringComparison.Ordinal))
            {
                throw Fail("invalid-component", itemPointer + "/componentIdentity");
            }

            var disposition = ParsePolicyDisposition(
                ReadString(item, "disposition", itemPointer, 16),
                itemPointer + "/disposition");
            var maximumScalars = ReadBoundedInt(
                item,
                "maximumScalars",
                itemPointer,
                0,
                DocumentationScribeContract.MaximumTextScalars);
            if (disposition != DocumentationScribePolicyDisposition.Forbidden && maximumScalars == 0)
            {
                throw Fail("invalid-style", itemPointer + "/maximumScalars");
            }

            builder.Add(new DocumentationScribeComponentPolicy(identity, disposition, maximumScalars));
            index++;
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<DocumentationScribeClaimPolicy> ParseClaimPolicies(
        JsonElement element,
        string pointer)
    {
        ExpectArray(element, pointer, 1, 64);
        var builder = ImmutableArray.CreateBuilder<DocumentationScribeClaimPolicy>();
        string? priorId = null;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            ExpectProperties(item, itemPointer, ["claimCategoryId", "completeEvidenceRequired", "allowedAuthorities"]);
            var claimId = ReadIdentifier(item, "claimCategoryId", itemPointer);
            if (priorId is not null && string.CompareOrdinal(priorId, claimId) >= 0)
            {
                throw Fail("invalid-order", itemPointer + "/claimCategoryId");
            }

            priorId = claimId;
            var completeRequired = ReadBoolean(item, "completeEvidenceRequired", itemPointer);
            var authoritiesElement = item.GetProperty("allowedAuthorities");
            ExpectArray(authoritiesElement, itemPointer + "/allowedAuthorities", 1, 16);
            var authorities = ImmutableArray.CreateBuilder<DocumentationScribeEvidenceAuthority>();
            string? priorAuthority = null;
            var authorityIndex = 0;
            foreach (var authorityElement in authoritiesElement.EnumerateArray())
            {
                var authorityPointer = $"{itemPointer}/allowedAuthorities/{authorityIndex}";
                var rawAuthority = ReadStringValue(authorityElement, authorityPointer, 64);
                if (priorAuthority is not null && string.CompareOrdinal(priorAuthority, rawAuthority) >= 0)
                {
                    throw Fail("invalid-order", authorityPointer);
                }

                priorAuthority = rawAuthority;
                authorities.Add(ParseAuthority(rawAuthority, authorityPointer));
                authorityIndex++;
            }

            builder.Add(new DocumentationScribeClaimPolicy(claimId, completeRequired, authorities.ToImmutable()));
            index++;
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<DocumentationScribeContextReference> ParseContextReferences(
        JsonElement element,
        string pointer,
        RepositoryContextRef expectedContextRef)
    {
        ExpectArray(element, pointer, 0, DocumentationScribeContract.MaximumReferences);
        var builder = ImmutableArray.CreateBuilder<DocumentationScribeContextReference>();
        string? priorId = null;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            ExpectProperties(
                item,
                itemPointer,
                [
                    "contextReferenceId",
                    "kind",
                    "repositoryContextRef",
                    "path",
                    "contentSha256",
                    "originalUtf8ByteCount",
                    "includedUtf8ByteCount",
                    "isTruncated",
                ]);
            var id = ReadIdentifier(item, "contextReferenceId", itemPointer);
            EnsureIncreasing(ref priorId, id, itemPointer + "/contextReferenceId");
            var kind = ReadString(item, "kind", itemPointer, 64) switch
            {
                "context.project-instruction" => DocumentationScribeContextReferenceKind.ProjectInstruction,
                "context.repository-documentation" => DocumentationScribeContextReferenceKind.RepositoryDocumentation,
                "context.style-example" => DocumentationScribeContextReferenceKind.StyleExample,
                _ => throw Fail("invalid-vocabulary", itemPointer + "/kind"),
            };
            var contextRef = ParseRepositoryContextRef(item, "repositoryContextRef", itemPointer);
            if (contextRef != expectedContextRef)
            {
                throw Fail("stale-reference", itemPointer + "/repositoryContextRef");
            }

            var path = ReadRepositoryRelativePath(item, "path", itemPointer);
            var sha = ReadSha256(item, "contentSha256", itemPointer);
            var counts = ParseByteCounts(item, itemPointer);
            builder.Add(new DocumentationScribeContextReference(
                id,
                kind,
                contextRef,
                path,
                sha,
                counts.Original,
                counts.Included,
                counts.Truncated));
            index++;
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<DocumentationScribeEvidenceReference> ParseEvidenceReferences(
        JsonElement element,
        string pointer,
        RepositoryContextRef expectedContextRef,
        DocumentationScribeTarget target,
        DocumentationScribeStyleProfile style)
    {
        ExpectArray(element, pointer, 0, DocumentationScribeContract.MaximumReferences);
        var claimIds = style.ClaimPolicies.Select(policy => policy.ClaimCategoryId).ToHashSet(StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<DocumentationScribeEvidenceReference>();
        string? priorId = null;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            ExpectProperties(
                item,
                itemPointer,
                [
                    "evidenceReferenceId",
                    "repositoryContextRef",
                    "subject",
                    "kind",
                    "relation",
                    "authority",
                    "locator",
                    "contentSha256",
                    "originalUtf8ByteCount",
                    "includedUtf8ByteCount",
                    "isTruncated",
                    "claimCategoryIds",
                ]);
            var id = ReadIdentifier(item, "evidenceReferenceId", itemPointer);
            EnsureIncreasing(ref priorId, id, itemPointer + "/evidenceReferenceId");
            var contextRef = ParseRepositoryContextRef(item, "repositoryContextRef", itemPointer);
            if (contextRef != expectedContextRef)
            {
                throw Fail("stale-reference", itemPointer + "/repositoryContextRef");
            }

            var subject = ParseEvidenceSubject(item.GetProperty("subject"), itemPointer + "/subject");
            EnsureSubjectBelongsToTarget(subject, target, itemPointer + "/subject");
            var kind = ParseEvidenceKind(ReadString(item, "kind", itemPointer, 64), itemPointer + "/kind");
            var relation = ParseEvidenceRelation(
                ReadString(item, "relation", itemPointer, 64),
                itemPointer + "/relation");
            var authority = ParseAuthority(
                ReadString(item, "authority", itemPointer, 64),
                itemPointer + "/authority");
            if (!AuthorityMatchesKind(authority, kind))
            {
                throw Fail("invalid-vocabulary", itemPointer + "/authority");
            }

            var locator = ParseEvidenceLocator(item.GetProperty("locator"), itemPointer + "/locator");
            var sha = ReadSha256(item, "contentSha256", itemPointer);
            var counts = ParseByteCounts(item, itemPointer);
            var categories = ParseOrderedIdentifiers(
                item.GetProperty("claimCategoryIds"),
                itemPointer + "/claimCategoryIds",
                1,
                64);
            if (categories.Any(category => !claimIds.Contains(category)))
            {
                throw Fail("invalid-reference", itemPointer + "/claimCategoryIds");
            }

            builder.Add(new DocumentationScribeEvidenceReference(
                id,
                contextRef,
                subject,
                kind,
                relation,
                authority,
                locator,
                sha,
                counts.Original,
                counts.Included,
                counts.Truncated,
                categories));
            index++;
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<DocumentationScribeEvidenceConflict> ParseEvidenceConflicts(
        JsonElement element,
        string pointer,
        ImmutableArray<DocumentationScribeEvidenceReference> references)
    {
        ExpectArray(element, pointer, 0, DocumentationScribeContract.MaximumReferences);
        var byId = references.ToDictionary(reference => reference.EvidenceReferenceId, StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<DocumentationScribeEvidenceConflict>();
        string? priorKey = null;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            ExpectProperties(item, itemPointer, ["relation", "higherEvidenceReferenceId", "lowerEvidenceReferenceId"]);
            if (ReadString(item, "relation", itemPointer, 64) != "evidence-conflict.higher-authority-contradicts")
            {
                throw Fail("invalid-vocabulary", itemPointer + "/relation");
            }

            var higherId = ReadIdentifier(item, "higherEvidenceReferenceId", itemPointer);
            var lowerId = ReadIdentifier(item, "lowerEvidenceReferenceId", itemPointer);
            var key = higherId + "\0" + lowerId;
            EnsureIncreasing(ref priorKey, key, itemPointer);
            if (!byId.TryGetValue(higherId, out var higher) || !byId.TryGetValue(lowerId, out var lower))
            {
                throw Fail("invalid-reference", itemPointer);
            }

            if (!SubjectsEqual(higher.Subject, lower.Subject)
                || GetAuthorityRank(higher.Authority) <= GetAuthorityRank(lower.Authority))
            {
                throw Fail("invalid-reference", itemPointer);
            }

            builder.Add(new DocumentationScribeEvidenceConflict(higherId, lowerId));
            index++;
        }

        return builder.ToImmutable();
    }

    private static DocumentationScribeRunLimits ParseLimits(JsonElement element, string pointer)
    {
        ExpectProperties(
            element,
            pointer,
            [
                "maximumContextReferences",
                "maximumContextUtf8Bytes",
                "maximumEvidenceReferences",
                "maximumEvidenceUtf8Bytes",
                "maximumProviderRequests",
                "maximumToolRounds",
                "maximumToolCalls",
                "maximumInputTokens",
                "maximumOutputTokens",
                "maximumCostMicrounits",
                "maximumElapsedMilliseconds",
            ]);
        return new DocumentationScribeRunLimits(
            ReadBoundedInt(element, "maximumContextReferences", pointer, 0, 512),
            ReadBoundedInt(element, "maximumContextUtf8Bytes", pointer, 0, 4_194_304),
            ReadBoundedInt(element, "maximumEvidenceReferences", pointer, 0, 512),
            ReadBoundedInt(element, "maximumEvidenceUtf8Bytes", pointer, 0, 4_194_304),
            ReadBoundedInt(element, "maximumProviderRequests", pointer, 1, 128),
            ReadBoundedInt(element, "maximumToolRounds", pointer, 0, 128),
            ReadBoundedInt(element, "maximumToolCalls", pointer, 0, 1_024),
            ReadBoundedInt(element, "maximumInputTokens", pointer, 1, 16_777_216),
            ReadBoundedInt(element, "maximumOutputTokens", pointer, 1, 1_048_576),
            ReadBoundedLong(element, "maximumCostMicrounits", pointer, 0, 1_000_000_000_000),
            ReadBoundedInt(element, "maximumElapsedMilliseconds", pointer, 1, 86_400_000));
    }

    private static DocumentationScribeTerminal ParseTerminal(
        JsonElement element,
        string pointer,
        DocumentationScribeRequest request)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("kind", out _))
        {
            throw Fail("invalid-shape", pointer);
        }

        var kind = ReadString(element, "kind", pointer, 16);
        return kind switch
        {
            "proposal" => ParseProposalTerminal(element, pointer, request),
            "skip" => ParseSkipTerminal(element, pointer, request),
            "failure" => ParseFailureTerminal(element, pointer),
            "cancelled" => ParseCancelledTerminal(element, pointer),
            _ => throw Fail("invalid-vocabulary", pointer + "/kind"),
        };
    }

    private static DocumentationScribeProposalTerminal ParseProposalTerminal(
        JsonElement element,
        string pointer,
        DocumentationScribeRequest request)
    {
        ExpectProperties(element, pointer, ["kind", "target", "contentUnits"]);
        var target = ParseResultTarget(element.GetProperty("target"), pointer + "/target");
        if (target.RepositoryContextRef != request.Context.RepositoryContextRef
            || target.SymbolRef != request.Target.SymbolRef
            || target.SourceLocator != request.Target.SourceLocator
            || !string.Equals(target.SourceSha256, request.Target.SourceSha256, StringComparison.Ordinal))
        {
            throw Fail("invalid-correlation", pointer + "/target");
        }

        var units = ParseContentUnits(element.GetProperty("contentUnits"), pointer + "/contentUnits", request);
        var patchContent = ProjectPatchContent(units);
        return new DocumentationScribeProposalTerminal(target, units, patchContent);
    }

    private static DocumentationScribeSkipTerminal ParseSkipTerminal(
        JsonElement element,
        string pointer,
        DocumentationScribeRequest request)
    {
        ExpectProperties(element, pointer, ["kind", "reason", "evidenceReferenceIds"]);
        var reason = ReadString(element, "reason", pointer, 64) switch
        {
            "scribe.skip.insufficient-evidence" => DocumentationScribeSkipReason.InsufficientEvidence,
            "scribe.skip.unsupported-current-m3-domain" => DocumentationScribeSkipReason.UnsupportedCurrentM3Domain,
            _ => throw Fail("invalid-vocabulary", pointer + "/reason"),
        };
        var evidenceIds = ParseOrderedIdentifiers(
            element.GetProperty("evidenceReferenceIds"),
            pointer + "/evidenceReferenceIds",
            0,
            request.StyleProfile.MaximumEvidenceRefsPerUnit);
        var requestIds = request.EvidenceReferences.Select(reference => reference.EvidenceReferenceId).ToHashSet(StringComparer.Ordinal);
        if (evidenceIds.Any(id => !requestIds.Contains(id)))
        {
            throw Fail("invalid-reference", pointer + "/evidenceReferenceIds");
        }

        return new DocumentationScribeSkipTerminal(reason, evidenceIds);
    }

    private static DocumentationScribeFailureTerminal ParseFailureTerminal(JsonElement element, string pointer)
    {
        ExpectProperties(element, pointer, ["kind", "code"]);
        var code = ReadString(element, "code", pointer, 64) switch
        {
            "scribe.failure.provider" => DocumentationScribeFailureCode.Provider,
            "scribe.failure.tool-protocol" => DocumentationScribeFailureCode.ToolProtocol,
            "scribe.failure.validation" => DocumentationScribeFailureCode.Validation,
            "scribe.failure.timeout" => DocumentationScribeFailureCode.Timeout,
            "scribe.failure.budget" => DocumentationScribeFailureCode.Budget,
            "scribe.failure.internal" => DocumentationScribeFailureCode.Internal,
            _ => throw Fail("invalid-vocabulary", pointer + "/code"),
        };
        return new DocumentationScribeFailureTerminal(code);
    }

    private static DocumentationScribeCancelledTerminal ParseCancelledTerminal(JsonElement element, string pointer)
    {
        ExpectProperties(element, pointer, ["kind", "code"]);
        var code = ReadString(element, "code", pointer, 64) switch
        {
            "scribe.cancelled.caller" => DocumentationScribeCancellationCode.Caller,
            "scribe.cancelled.shutdown" => DocumentationScribeCancellationCode.Shutdown,
            _ => throw Fail("invalid-vocabulary", pointer + "/code"),
        };
        return new DocumentationScribeCancelledTerminal(code);
    }

    private static DocumentationScribeResultTarget ParseResultTarget(JsonElement element, string pointer)
    {
        ExpectProperties(element, pointer, ["repositoryContextRef", "symbolRef", "sourceCommitment"]);
        var commitment = element.GetProperty("sourceCommitment");
        ExpectProperties(commitment, pointer + "/sourceCommitment", ["locator", "contentSha256"]);
        return new DocumentationScribeResultTarget(
            ParseRepositoryContextRef(element, "repositoryContextRef", pointer),
            ParseSymbolRef(element.GetProperty("symbolRef"), pointer + "/symbolRef"),
            ParseEvidenceLocator(commitment.GetProperty("locator"), pointer + "/sourceCommitment/locator"),
            ReadSha256(commitment, "contentSha256", pointer + "/sourceCommitment"));
    }

    private static ImmutableArray<DocumentationScribeContentUnit> ParseContentUnits(
        JsonElement element,
        string pointer,
        DocumentationScribeRequest request)
    {
        ExpectArray(element, pointer, 1, request.StyleProfile.MaximumContentUnits);
        var requestEvidence = request.EvidenceReferences.ToDictionary(
            reference => reference.EvidenceReferenceId,
            StringComparer.Ordinal);
        var claimPolicies = request.StyleProfile.ClaimPolicies.ToDictionary(
            policy => policy.ClaimCategoryId,
            StringComparer.Ordinal);
        var componentPolicies = request.StyleProfile.ComponentPolicies.ToDictionary(
            policy => policy.ComponentIdentity,
            StringComparer.Ordinal);
        var lowerConflicts = request.EvidenceConflicts
            .Select(conflict => conflict.LowerEvidenceReferenceId)
            .ToHashSet(StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<DocumentationScribeContentUnit>();
        var observedComponentIds = new HashSet<string>(StringComparer.Ordinal);
        var observedKinds = new HashSet<DocumentationScribeContentUnitKind>();
        string? priorSortKey = null;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            ExpectProperties(
                item,
                itemPointer,
                ["kind", "lines", "claimCategoryId", "evidenceReferenceIds"],
                ["componentIdentity", "name", "typeDocumentationId"]);
            var kind = ParseContentKind(ReadString(item, "kind", itemPointer, 32), itemPointer + "/kind");
            var componentIdentity = item.TryGetProperty("componentIdentity", out _)
                ? ReadIdentifier(item, "componentIdentity", itemPointer, allowSlash: true)
                : null;
            var name = item.TryGetProperty("name", out _)
                ? ReadString(item, "name", itemPointer, 256)
                : null;
            var typeDocumentationId = item.TryGetProperty("typeDocumentationId", out _)
                ? ReadString(item, "typeDocumentationId", itemPointer, 512)
                : null;
            var lines = ParseLines(item.GetProperty("lines"), itemPointer + "/lines", kind == DocumentationScribeContentUnitKind.InheritDoc);
            var claimCategoryId = ReadIdentifier(item, "claimCategoryId", itemPointer);
            if (!claimPolicies.TryGetValue(claimCategoryId, out var claimPolicy))
            {
                throw Fail("invalid-reference", itemPointer + "/claimCategoryId");
            }

            var evidenceIds = ParseOrderedIdentifiers(
                item.GetProperty("evidenceReferenceIds"),
                itemPointer + "/evidenceReferenceIds",
                1,
                request.StyleProfile.MaximumEvidenceRefsPerUnit);
            ValidateUnitShape(kind, componentIdentity, name, typeDocumentationId, itemPointer);
            var sortKey = GetUnitSortKey(kind, componentIdentity, typeDocumentationId);
            EnsureIncreasing(ref priorSortKey, sortKey, itemPointer);

            if (kind is DocumentationScribeContentUnitKind.Summary
                or DocumentationScribeContentUnitKind.Return
                or DocumentationScribeContentUnitKind.Value
                or DocumentationScribeContentUnitKind.Remarks
                or DocumentationScribeContentUnitKind.InheritDoc)
            {
                if (!observedKinds.Add(kind))
                {
                    throw Fail("invalid-order", itemPointer + "/kind");
                }
            }

            if (componentIdentity is not null)
            {
                if (!componentPolicies.TryGetValue(componentIdentity, out var componentPolicy))
                {
                    throw Fail("invalid-component", itemPointer + "/componentIdentity");
                }

                if (!UnitMatchesApplicableComponent(
                    request.Target.ApplicableComponents,
                    kind,
                    componentIdentity,
                    name))
                {
                    throw Fail("invalid-component", itemPointer + "/componentIdentity");
                }

                if (componentPolicy.Disposition == DocumentationScribePolicyDisposition.Forbidden)
                {
                    throw Fail("invalid-style", itemPointer + "/componentIdentity");
                }

                if (!observedComponentIds.Add(componentIdentity))
                {
                    throw Fail("invalid-order", itemPointer + "/componentIdentity");
                }

                ValidateScalarLimit(lines, componentPolicy.MaximumScalars, itemPointer + "/lines");
            }
            else
            {
                ValidateNonComponentStyle(kind, lines, request.StyleProfile, itemPointer);
            }

            foreach (var literal in request.StyleProfile.ForbiddenLiterals)
            {
                if (lines.Any(line => line.Contains(literal, StringComparison.Ordinal)))
                {
                    throw Fail("invalid-style", itemPointer + "/lines");
                }
            }

            foreach (var evidenceId in evidenceIds)
            {
                if (!requestEvidence.TryGetValue(evidenceId, out var evidence))
                {
                    throw Fail("invalid-reference", itemPointer + "/evidenceReferenceIds");
                }

                if (!EvidenceSubjectMatchesUnit(evidence.Subject, request.Target, kind, componentIdentity)
                    || !evidence.ClaimCategoryIds.Contains(claimCategoryId, StringComparer.Ordinal)
                    || !claimPolicy.AllowedAuthorities.Contains(evidence.Authority)
                    || (claimPolicy.CompleteEvidenceRequired && evidence.IsTruncated)
                    || lowerConflicts.Contains(evidenceId))
                {
                    throw Fail("invalid-evidence", itemPointer + "/evidenceReferenceIds");
                }
            }

            builder.Add(new DocumentationScribeContentUnit(
                kind,
                componentIdentity,
                name,
                typeDocumentationId,
                lines,
                claimCategoryId,
                evidenceIds));
            index++;
        }

        if (observedKinds.Contains(DocumentationScribeContentUnitKind.InheritDoc))
        {
            if (builder.Count != 1 || request.StyleProfile.InheritDocDisposition == DocumentationScribeInheritDocDisposition.Forbidden)
            {
                throw Fail("invalid-style", pointer);
            }
        }
        else
        {
            if (request.StyleProfile.InheritDocDisposition == DocumentationScribeInheritDocDisposition.Required)
            {
                throw Fail("invalid-style", pointer);
            }

            EnsureRequiredPolicy(
                request.StyleProfile.Summary,
                observedKinds.Contains(DocumentationScribeContentUnitKind.Summary),
                pointer);
            EnsureRequiredPolicy(
                request.StyleProfile.Remarks,
                observedKinds.Contains(DocumentationScribeContentUnitKind.Remarks),
                pointer);
            EnsureRequiredPolicy(
                request.StyleProfile.Exceptions,
                builder.Any(unit => unit.Kind == DocumentationScribeContentUnitKind.Exception),
                pointer);
            foreach (var policy in request.StyleProfile.ComponentPolicies)
            {
                if (policy.Disposition == DocumentationScribePolicyDisposition.Required
                    && !observedComponentIds.Contains(policy.ComponentIdentity))
                {
                    throw Fail("invalid-style", pointer);
                }
            }
        }

        return builder.ToImmutable();
    }

    private static DocumentationPatchContent ProjectPatchContent(
        ImmutableArray<DocumentationScribeContentUnit> units)
    {
        if (units.Length == 1 && units[0].Kind == DocumentationScribeContentUnitKind.InheritDoc)
        {
            return new DocumentationPatchInheritDocContent();
        }

        var summary = units.SingleOrDefault(unit => unit.Kind == DocumentationScribeContentUnitKind.Summary)?.Lines
            ?? ImmutableArray<string>.Empty;
        var typeParameters = units
            .Where(unit => unit.Kind == DocumentationScribeContentUnitKind.TypeParameter)
            .Select(unit => new DocumentationPatchNamedContent(unit.ComponentIdentity!, unit.Name!, unit.Lines))
            .ToImmutableArray();
        var parameters = units
            .Where(unit => unit.Kind == DocumentationScribeContentUnitKind.Parameter)
            .Select(unit => new DocumentationPatchNamedContent(unit.ComponentIdentity!, unit.Name!, unit.Lines))
            .ToImmutableArray();
        var returnUnit = units.SingleOrDefault(unit => unit.Kind == DocumentationScribeContentUnitKind.Return);
        var valueUnit = units.SingleOrDefault(unit => unit.Kind == DocumentationScribeContentUnitKind.Value);
        var exceptions = units
            .Where(unit => unit.Kind == DocumentationScribeContentUnitKind.Exception)
            .Select(unit => new DocumentationPatchExceptionContent(unit.TypeDocumentationId!, unit.Lines))
            .ToImmutableArray();
        var remarksUnit = units.SingleOrDefault(unit => unit.Kind == DocumentationScribeContentUnitKind.Remarks);

        return new DocumentationPatchStructuredContent(
            summary,
            typeParameters,
            parameters,
            returnUnit is null ? null : new DocumentationPatchComponentContent(returnUnit.ComponentIdentity!, returnUnit.Lines),
            valueUnit is null ? null : new DocumentationPatchComponentContent(valueUnit.ComponentIdentity!, valueUnit.Lines),
            exceptions,
            remarksUnit?.Lines);
    }

    private static DocumentationScribeRunEnvelope ParseRunEnvelope(
        JsonElement element,
        string pointer,
        DocumentationScribeRequest request,
        DocumentationScribeAttemptId expectedAttemptId)
    {
        ExpectProperties(
            element,
            pointer,
            [
                "scribeRequestSha256",
                "attemptId",
                "providerConfigurationId",
                "modelConfigurationId",
                "scribeProtocolId",
                "toolPolicyId",
                "styleProfileId",
                "attemptNumber",
                "providerRequestCount",
                "toolRoundCount",
                "toolCallCount",
                "elapsedMilliseconds",
                "diagnostics",
            ],
            ["usage", "cacheObservation", "cost"]);

        var requestSha = ReadSha256(element, "scribeRequestSha256", pointer);
        var attemptId = ParseAttemptId(ReadString(element, "attemptId", pointer, 64), pointer + "/attemptId");
        if (!string.Equals(requestSha, request.ArtifactSha256, StringComparison.Ordinal)
            || attemptId != expectedAttemptId)
        {
            throw Fail("invalid-correlation", pointer + "/scribeRequestSha256");
        }

        var providerId = ReadIdentifier(element, "providerConfigurationId", pointer);
        var modelId = ReadIdentifier(element, "modelConfigurationId", pointer);
        var protocolId = ReadIdentifier(element, "scribeProtocolId", pointer);
        var toolPolicyId = ReadIdentifier(element, "toolPolicyId", pointer);
        var styleProfileId = ReadIdentifier(element, "styleProfileId", pointer);
        if (!string.Equals(toolPolicyId, request.ToolPolicyId, StringComparison.Ordinal)
            || !string.Equals(styleProfileId, request.StyleProfile.StyleProfileId, StringComparison.Ordinal))
        {
            throw Fail("invalid-correlation", pointer + "/toolPolicyId");
        }

        var attemptNumber = ReadBoundedInt(element, "attemptNumber", pointer, 1, 1_000_000);
        var providerRequestCount = ReadBoundedInt(
            element,
            "providerRequestCount",
            pointer,
            0,
            request.Limits.MaximumProviderRequests);
        var toolRoundCount = ReadBoundedInt(
            element,
            "toolRoundCount",
            pointer,
            0,
            request.Limits.MaximumToolRounds);
        var toolCallCount = ReadBoundedInt(
            element,
            "toolCallCount",
            pointer,
            0,
            request.Limits.MaximumToolCalls);
        var elapsed = ReadBoundedInt(
            element,
            "elapsedMilliseconds",
            pointer,
            0,
            request.Limits.MaximumElapsedMilliseconds);
        var usage = element.TryGetProperty("usage", out var usageElement)
            ? ParseUsage(usageElement, pointer + "/usage", request.Limits)
            : null;
        DocumentationScribeCacheObservation? cache = null;
        if (element.TryGetProperty("cacheObservation", out var cacheElement))
        {
            cache = ReadStringValue(cacheElement, pointer + "/cacheObservation", 32) switch
            {
                "cache.hit" => DocumentationScribeCacheObservation.Hit,
                "cache.miss" => DocumentationScribeCacheObservation.Miss,
                "cache.mixed" => DocumentationScribeCacheObservation.Mixed,
                "cache.not-reported" => DocumentationScribeCacheObservation.NotReported,
                _ => throw Fail("invalid-vocabulary", pointer + "/cacheObservation"),
            };
        }

        var cost = element.TryGetProperty("cost", out var costElement)
            ? ParseCost(costElement, pointer + "/cost", request.Limits)
            : null;
        var diagnostics = ParseDiagnostics(element.GetProperty("diagnostics"), pointer + "/diagnostics");

        return new DocumentationScribeRunEnvelope(
            requestSha,
            attemptId,
            providerId,
            modelId,
            protocolId,
            toolPolicyId,
            styleProfileId,
            attemptNumber,
            providerRequestCount,
            toolRoundCount,
            toolCallCount,
            elapsed,
            usage,
            cache,
            cost,
            diagnostics);
    }

    private static DocumentationScribeUsageObservation ParseUsage(
        JsonElement element,
        string pointer,
        DocumentationScribeRunLimits limits)
    {
        ExpectProperties(
            element,
            pointer,
            [],
            ["inputTokens", "outputTokens", "cachedInputTokens", "reasoningTokens"]);
        if (!element.EnumerateObject().Any())
        {
            throw Fail("invalid-shape", pointer);
        }

        return new DocumentationScribeUsageObservation(
            ReadOptionalBoundedInt(element, "inputTokens", pointer, limits.MaximumInputTokens),
            ReadOptionalBoundedInt(element, "outputTokens", pointer, limits.MaximumOutputTokens),
            ReadOptionalBoundedInt(element, "cachedInputTokens", pointer, limits.MaximumInputTokens),
            ReadOptionalBoundedInt(element, "reasoningTokens", pointer, limits.MaximumOutputTokens));
    }

    private static DocumentationScribeCostObservation ParseCost(
        JsonElement element,
        string pointer,
        DocumentationScribeRunLimits limits)
    {
        ExpectProperties(element, pointer, ["currencyId", "amountMicrounits"]);
        return new DocumentationScribeCostObservation(
            ReadIdentifier(element, "currencyId", pointer),
            ReadBoundedLong(element, "amountMicrounits", pointer, 0, limits.MaximumCostMicrounits));
    }

    private static ImmutableArray<DocumentationScribeDiagnostic> ParseDiagnostics(
        JsonElement element,
        string pointer)
    {
        ExpectArray(element, pointer, 0, DocumentationScribeContract.MaximumDiagnostics);
        var builder = ImmutableArray.CreateBuilder<DocumentationScribeDiagnostic>();
        string? priorKey = null;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            ExpectProperties(item, itemPointer, ["code", "stage"], ["referenceId", "validationCode"]);
            var code = ReadString(item, "code", itemPointer, 64);
            var stage = ReadString(item, "stage", itemPointer, 32);
            if (stage is not ("provider" or "tool" or "result" or "runtime"))
            {
                throw Fail("invalid-vocabulary", itemPointer + "/stage");
            }

            var referenceId = item.TryGetProperty("referenceId", out _)
                ? ReadIdentifier(item, "referenceId", itemPointer)
                : null;
            var validationCode = item.TryGetProperty("validationCode", out _)
                ? ReadIdentifier(item, "validationCode", itemPointer)
                : null;
            var shapeValid = code switch
            {
                "scribe.diagnostic.provider-failure" => referenceId is null && validationCode is null && stage == "provider",
                "scribe.diagnostic.tool-failure" => referenceId is not null && validationCode is null && stage == "tool",
                "scribe.diagnostic.result-rejected" => referenceId is null && validationCode is not null && stage == "result",
                "scribe.diagnostic.runtime-failure" => referenceId is null && validationCode is null && stage == "runtime",
                _ => false,
            };
            if (!shapeValid)
            {
                throw Fail("invalid-diagnostic", itemPointer);
            }

            var key = code + "\0" + stage + "\0" + referenceId + "\0" + validationCode;
            EnsureIncreasing(ref priorKey, key, itemPointer);
            builder.Add(new DocumentationScribeDiagnostic(code, stage, referenceId, validationCode));
            index++;
        }

        return builder.ToImmutable();
    }

    private static SymbolRef ParseSymbolRef(JsonElement element, string pointer)
    {
        ExpectProperties(element, pointer, ["compilationContextRef", "documentationCommentId"]);
        var compilationRef = ReadIdentifier(element, "compilationContextRef", pointer);
        var documentationId = ReadString(element, "documentationCommentId", pointer, 512);
        if (documentationId.Length < 3
            || documentationId[0] is < 'A' or > 'Z'
            || documentationId[1] != ':'
            || HasInvalidScalar(documentationId))
        {
            throw Fail("invalid-vocabulary", pointer + "/documentationCommentId");
        }

        return new SymbolRef(compilationRef, documentationId);
    }

    private static EvidenceSubject ParseEvidenceSubject(JsonElement element, string pointer)
    {
        ExpectProperties(element, pointer, [], ["symbolRef", "parentSymbolRef", "componentKind", "identity"]);
        if (element.TryGetProperty("symbolRef", out var symbolElement))
        {
            if (element.EnumerateObject().Count() != 1)
            {
                throw Fail("invalid-shape", pointer);
            }

            return new TargetEvidenceSubject(ParseSymbolRef(symbolElement, pointer + "/symbolRef"));
        }

        if (!element.TryGetProperty("parentSymbolRef", out var parentElement)
            || !element.TryGetProperty("componentKind", out _)
            || !element.TryGetProperty("identity", out _)
            || element.EnumerateObject().Count() != 3)
        {
            throw Fail("invalid-shape", pointer);
        }

        var componentKind = ReadString(element, "componentKind", pointer, 64) switch
        {
            "component.type-parameter" => ComponentKind.TypeParameter,
            "component.parameter" => ComponentKind.Parameter,
            "component.return" => ComponentKind.Return,
            "component.value" => ComponentKind.Value,
            _ => throw Fail("invalid-vocabulary", pointer + "/componentKind"),
        };
        return new ComponentEvidenceSubject(
            ParseSymbolRef(parentElement, pointer + "/parentSymbolRef"),
            componentKind,
            ReadIdentifier(element, "identity", pointer, allowSlash: true));
    }

    private static EvidenceLocator ParseEvidenceLocator(JsonElement element, string pointer)
    {
        ExpectProperties(element, pointer, [], ["repository", "metadata", "generatedOutput", "synthetic"]);
        if (element.EnumerateObject().Count() != 1)
        {
            throw Fail("invalid-shape", pointer);
        }

        if (element.TryGetProperty("repository", out var repository))
        {
            ExpectProperties(repository, pointer + "/repository", ["path"], ["span"]);
            return new RepositoryEvidenceLocator(
                ReadRepositoryRelativePath(repository, "path", pointer + "/repository"),
                repository.TryGetProperty("span", out var span) ? ParseSpan(span, pointer + "/repository/span") : null);
        }

        if (element.TryGetProperty("metadata", out var metadata))
        {
            ExpectProperties(metadata, pointer + "/metadata", ["assemblyIdentity", "documentationCommentId"]);
            return new MetadataEvidenceLocator(
                ReadIdentifier(metadata, "assemblyIdentity", pointer + "/metadata"),
                ReadString(metadata, "documentationCommentId", pointer + "/metadata", 512));
        }

        if (element.TryGetProperty("generatedOutput", out var generated))
        {
            ExpectProperties(
                generated,
                pointer + "/generatedOutput",
                ["producerKind", "producerId", "outputId", "sourceSha256"],
                ["span"]);
            var kind = ReadString(generated, "producerKind", pointer + "/generatedOutput", 32) switch
            {
                "source-generator" => GeneratedOutputKind.SourceGenerator,
                "tool-generated" => GeneratedOutputKind.ToolGenerated,
                _ => throw Fail("invalid-vocabulary", pointer + "/generatedOutput/producerKind"),
            };
            return new GeneratedOutputEvidenceLocator(
                kind,
                ReadIdentifier(generated, "producerId", pointer + "/generatedOutput"),
                ReadIdentifier(generated, "outputId", pointer + "/generatedOutput"),
                ReadSha256(generated, "sourceSha256", pointer + "/generatedOutput"),
                generated.TryGetProperty("span", out var span) ? ParseSpan(span, pointer + "/generatedOutput/span") : null);
        }

        var synthetic = element.GetProperty("synthetic");
        ExpectProperties(synthetic, pointer + "/synthetic", ["fixtureId"]);
        return new SyntheticEvidenceLocator(ReadIdentifier(synthetic, "fixtureId", pointer + "/synthetic"));
    }

    private static Utf16Span ParseSpan(JsonElement element, string pointer)
    {
        ExpectProperties(element, pointer, ["start", "end"]);
        var start = ReadBoundedInt(element, "start", pointer, 0, int.MaxValue);
        var end = ReadBoundedInt(element, "end", pointer, 1, int.MaxValue);
        if (start >= end)
        {
            throw Fail("invalid-vocabulary", pointer);
        }

        return new Utf16Span(start, end);
    }

    private static (int Original, int Included, bool Truncated) ParseByteCounts(JsonElement element, string pointer)
    {
        var original = ReadBoundedInt(element, "originalUtf8ByteCount", pointer, 0, 4_194_304);
        var included = ReadBoundedInt(element, "includedUtf8ByteCount", pointer, 0, 4_194_304);
        var truncated = ReadBoolean(element, "isTruncated", pointer);
        if (included > original || truncated != (included < original))
        {
            throw Fail("invalid-vocabulary", pointer + "/isTruncated");
        }

        return (original, included, truncated);
    }

    private static ImmutableArray<string> ParseLines(JsonElement element, string pointer, bool requireEmpty)
    {
        ExpectArray(element, pointer, requireEmpty ? 0 : 1, requireEmpty ? 0 : 128);
        var builder = ImmutableArray.CreateBuilder<string>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            var value = ReadStringValue(item, itemPointer, DocumentationScribeContract.MaximumTextScalars);
            if (value.Length == 0
                || value.Contains('\r')
                || value.Contains('\n')
                || HasInvalidScalar(value)
                || ContainsRawDocumentationSyntax(value))
            {
                throw Fail("invalid-content", itemPointer);
            }

            builder.Add(value);
            index++;
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<string> ParseOrderedIdentifiers(
        JsonElement element,
        string pointer,
        int minimum,
        int maximum)
    {
        ExpectArray(element, pointer, minimum, maximum);
        var builder = ImmutableArray.CreateBuilder<string>();
        string? prior = null;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            var value = ReadStringValue(item, itemPointer, DocumentationScribeContract.MaximumIdentifierScalars);
            if (!IsIdentifier(value, allowSlash: false))
            {
                throw Fail("invalid-vocabulary", itemPointer);
            }

            EnsureIncreasing(ref prior, value, itemPointer);
            builder.Add(value);
            index++;
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<string> ParseOrderedStrings(
        JsonElement element,
        string pointer,
        int minimum,
        int maximum,
        int maximumScalars)
    {
        ExpectArray(element, pointer, minimum, maximum);
        var builder = ImmutableArray.CreateBuilder<string>();
        string? prior = null;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPointer = $"{pointer}/{index}";
            var value = ReadStringValue(item, itemPointer, maximumScalars);
            if (value.Length == 0 || HasInvalidScalar(value))
            {
                throw Fail("invalid-vocabulary", itemPointer);
            }

            EnsureIncreasing(ref prior, value, itemPointer);
            builder.Add(value);
            index++;
        }

        return builder.ToImmutable();
    }

    private static RawFailure? TryParseArtifact(
        ReadOnlyMemory<byte> utf8Json,
        string prefix,
        string versionProperty,
        out JsonDocument? document)
    {
        document = null;
        if (utf8Json.Length > DocumentationScribeContract.MaximumArtifactUtf8Bytes)
        {
            return new RawFailure(prefix + ".document-too-large", null);
        }

        if (HasPrefix(utf8Json.Span, 0xef, 0xbb, 0xbf)
            || HasPrefix(utf8Json.Span, 0xff, 0xfe)
            || HasPrefix(utf8Json.Span, 0xfe, 0xff))
        {
            return new RawFailure(prefix + ".bom-not-allowed", null);
        }

        try
        {
            _ = StrictUtf8.GetString(utf8Json.Span);
        }
        catch (DecoderFallbackException)
        {
            return new RawFailure(prefix + ".invalid-utf8", null);
        }

        try
        {
            document = JsonDocument.Parse(
                utf8Json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = DocumentationScribeContract.MaximumJsonDepth,
                });
        }
        catch (JsonException)
        {
            return new RawFailure(prefix + ".invalid-json", null);
        }

        var duplicatePointer = FindDuplicateProperty(document.RootElement, string.Empty);
        if (duplicatePointer is not null)
        {
            document.Dispose();
            document = null;
            return new RawFailure(prefix + ".duplicate-property", duplicatePointer);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            document = null;
            return new RawFailure(prefix + ".invalid-shape", string.Empty);
        }

        if (!document.RootElement.TryGetProperty(versionProperty, out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var versionNumber))
        {
            document.Dispose();
            document = null;
            return new RawFailure(prefix + ".invalid-shape", "/" + versionProperty);
        }

        if (versionNumber != DocumentationScribeContract.Version)
        {
            document.Dispose();
            document = null;
            return new RawFailure(prefix + ".unsupported-version", "/" + versionProperty);
        }

        return null;
    }

    private static string? FindDuplicateProperty(JsonElement element, string pointer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPointer = pointer + "/" + EscapePointer(property.Name);
                if (!names.Add(property.Name))
                {
                    return propertyPointer;
                }

                var nested = FindDuplicateProperty(property.Value, propertyPointer);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindDuplicateProperty(item, pointer + "/" + index);
                if (nested is not null)
                {
                    return nested;
                }

                index++;
            }
        }

        return null;
    }

    private static void ExpectProperties(
        JsonElement element,
        string pointer,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string>? optional = null)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Fail("invalid-shape", pointer);
        }

        optional ??= Array.Empty<string>();
        var allowed = required.Concat(optional).ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw Fail("unknown-field", pointer + "/" + EscapePointer(property.Name));
            }
        }

        foreach (var property in required)
        {
            if (!element.TryGetProperty(property, out _))
            {
                throw Fail("invalid-shape", pointer + "/" + EscapePointer(property));
            }
        }
    }

    private static void ExpectArray(JsonElement element, string pointer, int minimum, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() < minimum
            || element.GetArrayLength() > maximum)
        {
            throw Fail("invalid-shape", pointer);
        }
    }

    private static string ReadString(
        JsonElement parent,
        string property,
        string pointer,
        int maximumScalars)
    {
        if (!parent.TryGetProperty(property, out var element))
        {
            throw Fail("invalid-shape", pointer + "/" + property);
        }

        return ReadStringValue(element, pointer + "/" + property, maximumScalars);
    }

    private static string ReadStringValue(JsonElement element, string pointer, int maximumScalars)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw Fail("invalid-shape", pointer);
        }

        var value = element.GetString()!;
        if (CountScalars(value) > maximumScalars || HasInvalidScalar(value))
        {
            throw Fail("invalid-vocabulary", pointer);
        }

        return value;
    }

    private static string ReadIdentifier(
        JsonElement parent,
        string property,
        string pointer,
        bool allowSlash = false)
    {
        var value = ReadString(parent, property, pointer, DocumentationScribeContract.MaximumIdentifierScalars);
        if (!IsIdentifier(value, allowSlash))
        {
            throw Fail("invalid-vocabulary", pointer + "/" + property);
        }

        return value;
    }

    private static string ReadSha256(JsonElement parent, string property, string pointer)
    {
        var value = ReadString(parent, property, pointer, 64);
        if (value.Length != 64 || value.AsSpan().IndexOfAnyExcept("0123456789abcdef") >= 0)
        {
            throw Fail("invalid-vocabulary", pointer + "/" + property);
        }

        return value;
    }

    private static bool ReadBoolean(JsonElement parent, string property, string pointer)
    {
        if (!parent.TryGetProperty(property, out var element)
            || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Fail("invalid-shape", pointer + "/" + property);
        }

        return element.GetBoolean();
    }

    private static int ReadBoundedInt(
        JsonElement parent,
        string property,
        string pointer,
        int minimum,
        int maximum)
    {
        if (!parent.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value))
        {
            throw Fail("invalid-shape", pointer + "/" + property);
        }

        if (value < minimum || value > maximum)
        {
            throw Fail("over-budget", pointer + "/" + property);
        }

        return value;
    }

    private static int? ReadOptionalBoundedInt(
        JsonElement parent,
        string property,
        string pointer,
        int maximum)
    {
        return parent.TryGetProperty(property, out _)
            ? ReadBoundedInt(parent, property, pointer, 0, maximum)
            : null;
    }

    private static long ReadBoundedLong(
        JsonElement parent,
        string property,
        string pointer,
        long minimum,
        long maximum)
    {
        if (!parent.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt64(out var value))
        {
            throw Fail("invalid-shape", pointer + "/" + property);
        }

        if (value < minimum || value > maximum)
        {
            throw Fail("over-budget", pointer + "/" + property);
        }

        return value;
    }

    private static RepositoryContextRef ParseRepositoryContextRef(
        JsonElement parent,
        string property,
        string pointer)
    {
        var value = ReadString(parent, property, pointer, 64);
        if (!RepositoryContextRef.TryParse(value, out var result))
        {
            throw Fail("invalid-vocabulary", pointer + "/" + property);
        }

        return result;
    }

    private static string ReadRepositoryRelativePath(JsonElement parent, string property, string pointer)
    {
        var value = ReadString(parent, property, pointer, 1_024);
        if (value.Length == 0
            || value.Contains('\\')
            || value.StartsWith('/')
            || value.Contains(':')
            || value.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw Fail("invalid-vocabulary", pointer + "/" + property);
        }

        return value;
    }

    private static DocumentationScribeAttemptId ParseAttemptId(string value, string pointer)
    {
        if (!DocumentationScribeAttemptId.TryParse(value, out var result))
        {
            throw Fail("invalid-vocabulary", pointer);
        }

        return result;
    }

    private static DocumentationScribePolicyDisposition ParsePolicyDisposition(string value, string pointer) => value switch
    {
        "required" => DocumentationScribePolicyDisposition.Required,
        "optional" => DocumentationScribePolicyDisposition.Optional,
        "forbidden" => DocumentationScribePolicyDisposition.Forbidden,
        _ => throw Fail("invalid-vocabulary", pointer),
    };

    private static DocumentationScribeEvidenceAuthority ParseAuthority(string value, string pointer) => value switch
    {
        "authority.source-implementation" => DocumentationScribeEvidenceAuthority.SourceImplementation,
        "authority.source-declaration" => DocumentationScribeEvidenceAuthority.SourceDeclaration,
        "authority.existing-documentation" => DocumentationScribeEvidenceAuthority.ExistingDocumentation,
        "authority.test" => DocumentationScribeEvidenceAuthority.Test,
        "authority.repository-documentation" => DocumentationScribeEvidenceAuthority.RepositoryDocumentation,
        "authority.public-contract" => DocumentationScribeEvidenceAuthority.PublicContract,
        _ => throw Fail("invalid-vocabulary", pointer),
    };

    private static EvidenceKind ParseEvidenceKind(string value, string pointer) => value switch
    {
        "evidence.source.declaration" => EvidenceKind.SourceDeclaration,
        "evidence.source.implementation" => EvidenceKind.SourceImplementation,
        "evidence.source.xml-documentation" => EvidenceKind.SourceXmlDocumentation,
        "evidence.source.attribute" => EvidenceKind.SourceAttribute,
        "evidence.test" => EvidenceKind.Test,
        "evidence.repository-documentation" => EvidenceKind.RepositoryDocumentation,
        "evidence.public-contract" => EvidenceKind.PublicContract,
        _ => throw Fail("invalid-vocabulary", pointer),
    };

    private static EvidenceRelation ParseEvidenceRelation(string value, string pointer) => value switch
    {
        "evidence.declares" => EvidenceRelation.Declares,
        "evidence.documents" => EvidenceRelation.Documents,
        "evidence.tests" => EvidenceRelation.Tests,
        "evidence.references" => EvidenceRelation.References,
        "evidence.constrains" => EvidenceRelation.Constrains,
        _ => throw Fail("invalid-vocabulary", pointer),
    };

    private static DocumentationScribeContentUnitKind ParseContentKind(string value, string pointer) => value switch
    {
        "content.summary" => DocumentationScribeContentUnitKind.Summary,
        "content.type-parameter" => DocumentationScribeContentUnitKind.TypeParameter,
        "content.parameter" => DocumentationScribeContentUnitKind.Parameter,
        "content.return" => DocumentationScribeContentUnitKind.Return,
        "content.value" => DocumentationScribeContentUnitKind.Value,
        "content.exception" => DocumentationScribeContentUnitKind.Exception,
        "content.remarks" => DocumentationScribeContentUnitKind.Remarks,
        "content.inherit-doc" => DocumentationScribeContentUnitKind.InheritDoc,
        _ => throw Fail("invalid-vocabulary", pointer),
    };

    private static void ValidateUnitShape(
        DocumentationScribeContentUnitKind kind,
        string? componentIdentity,
        string? name,
        string? typeDocumentationId,
        string pointer)
    {
        var valid = kind switch
        {
            DocumentationScribeContentUnitKind.TypeParameter or DocumentationScribeContentUnitKind.Parameter =>
                componentIdentity is not null && name is not null && typeDocumentationId is null,
            DocumentationScribeContentUnitKind.Return or DocumentationScribeContentUnitKind.Value =>
                componentIdentity is not null && name is null && typeDocumentationId is null,
            DocumentationScribeContentUnitKind.Exception =>
                componentIdentity is null
                && name is null
                && typeDocumentationId is { Length: > 2 }
                && typeDocumentationId.StartsWith("T:", StringComparison.Ordinal),
            _ => componentIdentity is null && name is null && typeDocumentationId is null,
        };
        if (!valid)
        {
            throw Fail("invalid-content", pointer);
        }
    }

    private static string GetUnitSortKey(
        DocumentationScribeContentUnitKind kind,
        string? componentIdentity,
        string? typeDocumentationId)
    {
        var rank = kind switch
        {
            DocumentationScribeContentUnitKind.Summary => 0,
            DocumentationScribeContentUnitKind.TypeParameter => 1,
            DocumentationScribeContentUnitKind.Parameter => 2,
            DocumentationScribeContentUnitKind.Return => 3,
            DocumentationScribeContentUnitKind.Value => 4,
            DocumentationScribeContentUnitKind.Exception => 5,
            DocumentationScribeContentUnitKind.Remarks => 6,
            DocumentationScribeContentUnitKind.InheritDoc => 7,
            _ => 99,
        };
        return rank.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)
            + "\0"
            + componentIdentity
            + "\0"
            + typeDocumentationId;
    }

    private static void ValidateNonComponentStyle(
        DocumentationScribeContentUnitKind kind,
        ImmutableArray<string> lines,
        DocumentationScribeStyleProfile style,
        string pointer)
    {
        var policy = kind switch
        {
            DocumentationScribeContentUnitKind.Summary => style.Summary,
            DocumentationScribeContentUnitKind.Remarks => style.Remarks,
            DocumentationScribeContentUnitKind.Exception => style.Exceptions,
            _ => null,
        };
        if (policy is not null)
        {
            if (policy.Disposition == DocumentationScribePolicyDisposition.Forbidden)
            {
                throw Fail("invalid-style", pointer + "/kind");
            }

            ValidateScalarLimit(lines, policy.MaximumScalars, pointer + "/lines");
        }
    }

    private static void ValidateScalarLimit(ImmutableArray<string> lines, int maximum, string pointer)
    {
        var total = lines.Sum(CountScalars);
        if (total > maximum)
        {
            throw Fail("over-budget", pointer);
        }
    }

    private static void EnsureRequiredPolicy(
        DocumentationScribeTextPolicy policy,
        bool observed,
        string pointer)
    {
        if ((policy.Disposition == DocumentationScribePolicyDisposition.Required && !observed)
            || (policy.Disposition == DocumentationScribePolicyDisposition.Forbidden && observed))
        {
            throw Fail("invalid-style", pointer);
        }
    }

    private static void EnsureSubjectBelongsToTarget(
        EvidenceSubject subject,
        DocumentationScribeTarget target,
        string pointer)
    {
        if (subject.ParentSymbolRef != target.SymbolRef)
        {
            throw Fail("wrong-subject", pointer);
        }

        if (subject is ComponentEvidenceSubject component
            && !target.ApplicableComponents.Any(candidate =>
                string.Equals(candidate.Identity, component.Identity, StringComparison.Ordinal)
                && MapComponentKind(candidate.Kind) == component.ComponentKind))
        {
            throw Fail("wrong-subject", pointer);
        }
    }

    private static bool EvidenceSubjectMatchesUnit(
        EvidenceSubject subject,
        DocumentationScribeTarget target,
        DocumentationScribeContentUnitKind kind,
        string? componentIdentity)
    {
        if (subject.ParentSymbolRef != target.SymbolRef)
        {
            return false;
        }

        if (componentIdentity is null)
        {
            return subject is TargetEvidenceSubject;
        }

        return subject is ComponentEvidenceSubject component
            && string.Equals(component.Identity, componentIdentity, StringComparison.Ordinal)
            && component.ComponentKind == kind switch
            {
                DocumentationScribeContentUnitKind.TypeParameter => ComponentKind.TypeParameter,
                DocumentationScribeContentUnitKind.Parameter => ComponentKind.Parameter,
                DocumentationScribeContentUnitKind.Return => ComponentKind.Return,
                DocumentationScribeContentUnitKind.Value => ComponentKind.Value,
                _ => ComponentKind.Unknown,
            };
    }

    private static ComponentKind MapComponentKind(DocumentationPatchComponentKind kind) => kind switch
    {
        DocumentationPatchComponentKind.TypeParameter => ComponentKind.TypeParameter,
        DocumentationPatchComponentKind.Parameter => ComponentKind.Parameter,
        DocumentationPatchComponentKind.Return => ComponentKind.Return,
        DocumentationPatchComponentKind.Value => ComponentKind.Value,
        _ => ComponentKind.Unknown,
    };

    private static bool UnitMatchesApplicableComponent(
        ImmutableArray<DocumentationPatchApplicableComponent> components,
        DocumentationScribeContentUnitKind kind,
        string componentIdentity,
        string? name)
    {
        var expectedKind = kind switch
        {
            DocumentationScribeContentUnitKind.TypeParameter => DocumentationPatchComponentKind.TypeParameter,
            DocumentationScribeContentUnitKind.Parameter => DocumentationPatchComponentKind.Parameter,
            DocumentationScribeContentUnitKind.Return => DocumentationPatchComponentKind.Return,
            DocumentationScribeContentUnitKind.Value => DocumentationPatchComponentKind.Value,
            _ => (DocumentationPatchComponentKind?)null,
        };
        return expectedKind is not null
            && components.Any(component =>
                component.Kind == expectedKind
                && string.Equals(component.Identity, componentIdentity, StringComparison.Ordinal)
                && string.Equals(component.Name, name, StringComparison.Ordinal));
    }

    private static bool SubjectsEqual(EvidenceSubject left, EvidenceSubject right)
    {
        return left switch
        {
            TargetEvidenceSubject when right is TargetEvidenceSubject => left.ParentSymbolRef == right.ParentSymbolRef,
            ComponentEvidenceSubject leftComponent when right is ComponentEvidenceSubject rightComponent =>
                leftComponent.ParentSymbolRef == rightComponent.ParentSymbolRef
                && leftComponent.ComponentKind == rightComponent.ComponentKind
                && string.Equals(leftComponent.Identity, rightComponent.Identity, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static int GetAuthorityRank(DocumentationScribeEvidenceAuthority authority) => authority switch
    {
        DocumentationScribeEvidenceAuthority.SourceImplementation => 0,
        DocumentationScribeEvidenceAuthority.SourceDeclaration => 1,
        DocumentationScribeEvidenceAuthority.ExistingDocumentation => 2,
        DocumentationScribeEvidenceAuthority.Test => 3,
        DocumentationScribeEvidenceAuthority.RepositoryDocumentation => 4,
        DocumentationScribeEvidenceAuthority.PublicContract => 5,
        _ => -1,
    };

    private static bool AuthorityMatchesKind(
        DocumentationScribeEvidenceAuthority authority,
        EvidenceKind kind) =>
        kind switch
        {
            EvidenceKind.SourceImplementation => authority == DocumentationScribeEvidenceAuthority.SourceImplementation,
            EvidenceKind.SourceDeclaration or EvidenceKind.SourceAttribute =>
                authority == DocumentationScribeEvidenceAuthority.SourceDeclaration,
            EvidenceKind.SourceXmlDocumentation => authority == DocumentationScribeEvidenceAuthority.ExistingDocumentation,
            EvidenceKind.Test => authority == DocumentationScribeEvidenceAuthority.Test,
            EvidenceKind.RepositoryDocumentation => authority == DocumentationScribeEvidenceAuthority.RepositoryDocumentation,
            EvidenceKind.PublicContract => authority == DocumentationScribeEvidenceAuthority.PublicContract,
            _ => false,
        };

    private static void EnsureIncreasing(ref string? prior, string current, string pointer)
    {
        if (prior is not null && string.CompareOrdinal(prior, current) >= 0)
        {
            throw Fail("invalid-order", pointer);
        }

        prior = current;
    }

    private static bool IsIdentifier(string value, bool allowSlash)
    {
        if (value.Length == 0 || value.Length > DocumentationScribeContract.MaximumIdentifierScalars)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!((character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9')
                || character is '.' or '-'
                || (allowSlash && character == '/')))
            {
                return false;
            }
        }

        return value[0] is >= 'a' and <= 'z'
            && value[^1] is not ('.' or '-' or '/');
    }

    private static int CountScalars(string value) => value.EnumerateRunes().Count();

    private static bool HasInvalidScalar(string value) => value.EnumerateRunes().Any(rune =>
        Rune.IsControl(rune) && rune.Value is not ('\t' or '\n' or '\r'));

    private static bool ContainsRawDocumentationSyntax(string value) =>
        value.Contains("///", StringComparison.Ordinal)
        || value.Contains("<summary", StringComparison.OrdinalIgnoreCase)
        || value.Contains("<inheritdoc", StringComparison.OrdinalIgnoreCase)
        || value.Contains("</", StringComparison.Ordinal);

    private static bool HasPrefix(ReadOnlySpan<byte> bytes, params byte[] prefix) =>
        bytes.Length >= prefix.Length && bytes[..prefix.Length].SequenceEqual(prefix);

    private static string EscapePointer(string value) =>
        value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static ContractFailure Fail(string category, string? pointer) => new(category, pointer);

    private sealed class ContractFailure(string category, string? pointer) : Exception
    {
        internal string Category { get; } = category;

        internal string? Pointer { get; } = pointer;
    }

    private sealed record RawFailure(string Code, string? Pointer);
}
