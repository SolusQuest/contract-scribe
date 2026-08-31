using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

internal static class CampaignScribeRequestBuilder
{
    internal static ReadOnlyMemory<byte> Build(
        ProductionRepositorySessionBundle bundle,
        CampaignPlanningWorkItem work,
        CampaignPlanningExecutionPolicy policy,
        CampaignConfigurationDocument configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        if (work.Targets.Length != 1
            || work.Targets[0] is not { M3Eligible: true, StyleProfile: { } style } target
            || target.Source is not CampaignPlanningRepositorySourceAuthority source)
        {
            throw new InvalidOperationException("campaign.request.target-invalid");
        }

        var classification = bundle.Classifications.Targets.Single(item =>
            item.SymbolRef == target.SymbolRef);
        var selection = DocumentationScribeContextValidation.CreateBootstrapSelection(
            bundle.Session.RepositoryContextRef,
            bundle.Session.InputIdentity,
            bundle.Classifications.TargetProfile,
            classification.SymbolRef,
            source.Path,
            source.RequestedDeclarationSpan.Start,
            source.RequestedDeclarationSpan.End,
            source.ContentSha256);
        var bootstrap = new DocumentationScribeContextBootstrapper().Bootstrap(
            bundle.Classified, selection, cancellationToken);
        if (bootstrap.Status is not (DocumentationScribeContextBootstrapStatus.Succeeded
                or DocumentationScribeContextBootstrapStatus.Incomplete)
            || bootstrap.Context is not { } context)
        {
            throw new InvalidOperationException("campaign.request.context-unavailable");
        }

        var sourceFact = context.Facts.Evidence.Single(item =>
            item.KindId == "source.target-declaration"
            && item.Commitment.RepositoryPath == source.Path
            && item.Range == source.RequestedDeclarationSpan);
        var sourceClaim = style.ClaimPolicies.FirstOrDefault(policyRow =>
            policyRow.AllowedAuthorities.Contains(
                DocumentationScribeEvidenceAuthority.SourceDeclaration));
        if (sourceClaim is null)
        {
            throw new InvalidOperationException("campaign.request.source-authority-forbidden");
        }

        var applicable = new JsonArray(target.ApplicableComponents.Select(component =>
        {
            var row = new JsonObject
            {
                ["kind"] = ComponentKind(component.Kind),
                ["identity"] = component.Identity,
            };
            if (component.Name is not null) row["name"] = component.Name;
            return (JsonNode)row;
        }).ToArray());
        var componentPolicies = new JsonArray(style.ComponentPolicies.Select(component =>
            (JsonNode)new JsonObject
            {
                ["componentIdentity"] = component.ComponentIdentity,
                ["disposition"] = Disposition(component.Disposition),
                ["maximumScalars"] = component.MaximumScalars,
            }).ToArray());
        var claimPolicies = new JsonArray(style.ClaimPolicies.Select(claim =>
            (JsonNode)new JsonObject
            {
                ["claimCategoryId"] = claim.ClaimCategoryId,
                ["completeEvidenceRequired"] = claim.CompleteEvidenceRequired,
                ["allowedAuthorities"] = new JsonArray(claim.AllowedAuthorities
                    .Select(authority => (JsonNode)JsonValue.Create(EvidenceAuthority(authority))!).ToArray()),
            }).ToArray());
        var contextReferences = new JsonArray(context.Facts.Instructions.Select(instruction =>
            (JsonNode)new JsonObject
            {
                ["contextReferenceId"] = instruction.InstructionId,
                ["kind"] = "context.project-instruction",
                ["repositoryContextRef"] = bundle.Session.RepositoryContextRef.Value,
                ["path"] = instruction.Commitment.RepositoryPath,
                ["contentSha256"] = instruction.Commitment.ContentSha256,
                ["originalUtf8ByteCount"] = instruction.Commitment.OriginalUtf8ByteCount,
                ["includedUtf8ByteCount"] = instruction.Commitment.IncludedUtf8ByteCount,
                ["isTruncated"] = instruction.Commitment.IsTruncated,
            }).ToArray());
        var symbol = Symbol(target.SymbolRef);
        var locator = RepositoryLocator(source.Path, source.RequestedDeclarationSpan);
        var evidence = new JsonArray
        {
            new JsonObject
            {
                ["evidenceReferenceId"] = sourceFact.EvidenceId,
                ["repositoryContextRef"] = bundle.Session.RepositoryContextRef.Value,
                ["subject"] = new JsonObject { ["symbolRef"] = symbol.DeepClone() },
                ["kind"] = "evidence.source.declaration",
                ["relation"] = "evidence.declares",
                ["authority"] = "authority.source-declaration",
                ["locator"] = locator.DeepClone(),
                ["contentSha256"] = sourceFact.Commitment.ContentSha256,
                ["originalUtf8ByteCount"] = sourceFact.Commitment.OriginalUtf8ByteCount,
                ["includedUtf8ByteCount"] = sourceFact.Commitment.IncludedUtf8ByteCount,
                ["isTruncated"] = sourceFact.Commitment.IsTruncated,
                ["claimCategoryIds"] = new JsonArray(sourceClaim.ClaimCategoryId),
            },
        };
        var limits = policy.ScribeRunLimits;
        var root = new JsonObject
        {
            ["scribeRequestVersion"] = 1,
            ["context"] = new JsonObject
            {
                ["repositoryContextRef"] = bundle.Session.RepositoryContextRef.Value,
                ["inputIdentity"] = bundle.Session.InputIdentity,
                ["targetProfile"] = TargetProfile(bundle.Classifications.TargetProfile),
                ["auditOutcome"] = AuditOutcome(target.AuditOutcome),
            },
            ["target"] = new JsonObject
            {
                ["symbolRef"] = symbol,
                ["sourceCommitment"] = new JsonObject
                {
                    ["locator"] = locator,
                    ["contentSha256"] = source.ContentSha256,
                },
                ["applicableComponents"] = applicable,
            },
            ["styleProfile"] = new JsonObject
            {
                ["styleProfileId"] = style.StyleProfileId,
                ["outputLanguageId"] = style.OutputLanguageId,
                ["summary"] = TextPolicy(style.Summary),
                ["remarks"] = TextPolicy(style.Remarks),
                ["exceptions"] = TextPolicy(style.Exceptions),
                ["componentPolicies"] = componentPolicies,
                ["inheritDocDisposition"] = InheritDoc(style.InheritDocDisposition),
                ["allowedLiterals"] = new JsonArray(style.AllowedLiterals
                    .Select(item => (JsonNode)JsonValue.Create(item)!).ToArray()),
                ["forbiddenLiterals"] = new JsonArray(style.ForbiddenLiterals
                    .Select(item => (JsonNode)JsonValue.Create(item)!).ToArray()),
                ["claimPolicies"] = claimPolicies,
                ["maximumContentUnits"] = style.MaximumContentUnits,
                ["maximumEvidenceRefsPerUnit"] = style.MaximumEvidenceRefsPerUnit,
            },
            ["contextReferences"] = contextReferences,
            ["evidenceReferences"] = evidence,
            ["evidenceConflicts"] = new JsonArray(),
            ["toolPolicyId"] = configuration.ScribeRequest.ToolPolicyId,
            ["limits"] = new JsonObject
            {
                ["maximumAttempts"] = limits.MaximumAttempts,
                ["maximumContextReferences"] = limits.MaximumContextReferences,
                ["maximumContextUtf8Bytes"] = limits.MaximumContextUtf8Bytes,
                ["maximumEvidenceReferences"] = limits.MaximumEvidenceReferences,
                ["maximumEvidenceUtf8Bytes"] = limits.MaximumEvidenceUtf8Bytes,
                ["maximumProviderRequests"] = limits.MaximumProviderRequests,
                ["maximumToolRounds"] = limits.MaximumToolRounds,
                ["maximumToolCalls"] = limits.MaximumToolCalls,
                ["maximumInputTokens"] = limits.MaximumInputTokens,
                ["maximumUncachedInputTokens"] = limits.MaximumUncachedInputTokens,
                ["maximumOutputTokens"] = limits.MaximumOutputTokens,
                ["maximumCostMicrounits"] = limits.MaximumCostMicrounits,
                ["maximumElapsedMilliseconds"] = limits.MaximumElapsedMilliseconds,
            },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        var parsed = DocumentationScribeValidation.ParseRequest(bytes);
        if (!parsed.IsValid)
        {
            throw new InvalidOperationException("campaign.request.invalid");
        }
        return bytes;
    }

    private static JsonObject Symbol(SymbolRef symbol) => new()
    {
        ["compilationContextRef"] = symbol.CompilationContextRef,
        ["documentationCommentId"] = symbol.DocumentationCommentId,
    };

    private static JsonObject RepositoryLocator(string path, Utf16Span span) => new()
    {
        ["repository"] = new JsonObject
        {
            ["path"] = path,
            ["span"] = new JsonObject { ["start"] = span.Start, ["end"] = span.End },
        },
    };

    private static JsonObject TextPolicy(DocumentationScribeTextPolicy policy) => new()
    {
        ["disposition"] = Disposition(policy.Disposition),
        ["maximumScalars"] = policy.MaximumScalars,
    };

    private static string ComponentKind(ComponentKind kind) => kind switch
    {
        ContractScribe.Core.ComponentKind.TypeParameter => "typeParameter",
        ContractScribe.Core.ComponentKind.Parameter => "parameter",
        ContractScribe.Core.ComponentKind.Return => "return",
        ContractScribe.Core.ComponentKind.Value => "value",
        _ => throw new InvalidOperationException(),
    };

    private static string Disposition(DocumentationScribePolicyDisposition value) => value switch
    {
        DocumentationScribePolicyDisposition.Required => "required",
        DocumentationScribePolicyDisposition.Optional => "optional",
        DocumentationScribePolicyDisposition.Forbidden => "forbidden",
        _ => throw new InvalidOperationException(),
    };

    private static string InheritDoc(DocumentationScribeInheritDocDisposition value) => value switch
    {
        DocumentationScribeInheritDocDisposition.Allowed => "allowed",
        DocumentationScribeInheritDocDisposition.Required => "required",
        DocumentationScribeInheritDocDisposition.Forbidden => "forbidden",
        _ => throw new InvalidOperationException(),
    };

    private static string EvidenceAuthority(DocumentationScribeEvidenceAuthority value) => value switch
    {
        DocumentationScribeEvidenceAuthority.SourceDeclaration => "authority.source-declaration",
        DocumentationScribeEvidenceAuthority.SourceImplementation => "authority.source-implementation",
        DocumentationScribeEvidenceAuthority.ExistingDocumentation => "authority.existing-documentation",
        DocumentationScribeEvidenceAuthority.PublicContract => "authority.public-contract",
        DocumentationScribeEvidenceAuthority.RepositoryDocumentation => "authority.repository-documentation",
        DocumentationScribeEvidenceAuthority.Test => "authority.test",
        _ => throw new InvalidOperationException(),
    };

    private static string TargetProfile(ContractScribe.Core.TargetProfile profile) => profile switch
    {
        ContractScribe.Core.TargetProfile.ExternalApi => "profile.external-api",
        ContractScribe.Core.TargetProfile.AssemblyVisible => "profile.assembly-visible",
        _ => throw new InvalidOperationException(),
    };

    private static string AuditOutcome(ContractScribe.Core.AuditOutcome outcome) => outcome switch
    {
        ContractScribe.Core.AuditOutcome.Compliant => "audit.outcome.compliant",
        ContractScribe.Core.AuditOutcome.Violation => "audit.outcome.violation",
        ContractScribe.Core.AuditOutcome.Skipped => "audit.outcome.skipped",
        _ => throw new InvalidOperationException(),
    };
}
