using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Prompting;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

internal sealed record BuiltDocumentationScribeManifest(
    ReadOnlyMemory<byte> RequestUtf8Json,
    DocumentationScribeManifestSelection Selection);

internal static class CampaignScribeRequestBuilder
{
    internal static ReadOnlyMemory<byte> Build(
        ProductionRepositorySessionBundle bundle,
        CampaignPlanningWorkItem work,
        CampaignPlanningExecutionPolicy policy,
        CampaignConfigurationDocument configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (work.Targets.Length != 1 || work.Targets[0] is not { M3Eligible: true, StyleProfile: not null } target)
            throw new InvalidOperationException("campaign.request.target-invalid");
        return BuildManifest(bundle, work.Targets, [target.SymbolRef], policy, configuration, cancellationToken).RequestUtf8Json;
    }

    internal static ImmutableArray<CampaignPlanningTargetFact> SelectedMembers(CampaignCheckpointState state, CampaignWorkPlan plan) =>
        state.Batch.SelectedTargetKeys.Select(key => state.Batch.CompleteTargets.Single(target => target.TargetKey == key))
            .Select(target => plan.WorkItems.Single(work => work.WorkItemKey == target.WorkItemKey).Targets.Single(fact => fact.SymbolRef == target.SymbolRef))
            .ToImmutableArray();

    internal static ImmutableArray<SymbolRef> ExecutableMembers(CampaignCheckpointState state, CampaignWorkPlan plan,
        CampaignInvocationTargetAllowance allowance) => SelectedMembers(state, plan).Where(target =>
        {
            var batchTarget = state.Batch.CompleteTargets.Single(item => item.SymbolRef == target.SymbolRef);
            var work = state.WorkItems.Single(item => item.WorkItemKey == batchTarget.WorkItemKey);
            var prior = state.ScribeProgress?.Members.SingleOrDefault(item => item.WorkItemKey == work.WorkItemKey);
            return batchTarget.Dispatchable && (prior?.ContentSha256 is not null
                    && (work.Status is CampaignWorkStatus.ProposalComplete or CampaignWorkStatus.Accepted
                        || prior.Skip is not null && work.Status == CampaignWorkStatus.Closed)
                || CampaignStateFactory.AllowsTarget(state, work.WorkItemKey, allowance)
                    && work.AttemptDisposition == CampaignAttemptDisposition.Open
                    && (work.Status == CampaignWorkStatus.Planned || work.ClosedOutcome is
                    { Code: CampaignWorkOutcomeCode.ProviderFailure, ProviderDisposition: CampaignProviderFinalDisposition.Retryable }));
        }).Select(target => target.SymbolRef).ToImmutableArray();

    internal static BuiltDocumentationScribeManifest BuildManifest(
        ProductionRepositorySessionBundle bundle,
        ImmutableArray<CampaignPlanningTargetFact> selected,
        ImmutableArray<SymbolRef> executableSymbols,
        CampaignPlanningExecutionPolicy policy,
        CampaignConfigurationDocument configuration,
        CancellationToken cancellationToken,
        SymbolRef? bootstrapSymbol = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(policy);
        if (selected.IsDefaultOrEmpty) throw new ArgumentException("campaign.request.target-invalid", nameof(selected));
        var audit = DocumentationScribeAuditAuthority.Create(bundle.Classified, bundle.Observed,
            bundle.Policy, bundle.AuditInputs, bundle.Audit);
        var selection = DocumentationScribeManifestSelection.Create(selected.Select(target => (
            audit.Select(bundle.Classifications.Targets.Single(item => item.SymbolRef == target.SymbolRef)),
            target.StyleProfile)), executableSymbols, cancellationToken);
        var bootstrapSelections = selection.BootstrapSelections(null);
        var instructions = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var evidence = new JsonArray();
        var manifest = new JsonArray();
        var executableIds = new Dictionary<SymbolRef, string>();
        var limits = policy.ScribeRunLimits;
        for (var index = 0; index < selected.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = selected[index];
            var accepted = bootstrapSelections[index];
            var source = target.Source as CampaignPlanningRepositorySourceAuthority
                ?? throw new InvalidOperationException("campaign.request.target-invalid");
            var seed = !executableSymbols.IsEmpty && target.SymbolRef == (bootstrapSymbol ?? executableSymbols[0]);
            var l = accepted.Limits;
            var bootstrapLimits = DocumentationScribeContextValidation.CreateLimits(l.MaximumInstructionFiles,
                l.MaximumInstructionDepth, l.MaximumInstructionFileUtf8Bytes, l.MaximumDeclarationReferences,
                l.MaximumDeclarationFiles, l.MaximumInspectedSourceUtf8Bytes, l.MaximumSourceFileUtf8Bytes,
                seed ? l.MaximumIncludedSourceUtf8Bytes : 1, l.MaximumTotalContextUtf8Bytes, l.MaximumElapsedMilliseconds);
            var bootstrapSelection = DocumentationScribeContextValidation.CreateBootstrapSelection(
                accepted.RepositoryContextRef, accepted.InputIdentity, accepted.TargetProfile, accepted.SymbolRef,
                accepted.SourceLocator, accepted.SourceSha256, accepted.ConfiguredAgentEntrypoint, bootstrapLimits);
            var bootstrap = new DocumentationScribeContextBootstrapper().Bootstrap(bundle.Classified, bootstrapSelection, cancellationToken);
            if (bootstrap.Context is not { } context)
                throw new InvalidOperationException("campaign.request.context-unavailable");
            if (target.GroupingAuthority?.InstructionsSha256 is { } commitment
                && commitment != CampaignCommandRunner.InstructionCommitment(context.Facts))
                throw new InvalidOperationException("campaign.request.instruction-authority-changed");
            foreach (var instruction in context.Facts.Instructions)
            {
                var row = new JsonObject
                {
                    ["contextReferenceId"] = instruction.InstructionId,
                    ["kind"] = "context.project-instruction",
                    ["repositoryContextRef"] = bundle.Session.RepositoryContextRef.Value,
                    ["path"] = instruction.Commitment.RepositoryPath,
                    ["contentSha256"] = instruction.Commitment.ContentSha256,
                    ["originalUtf8ByteCount"] = instruction.Commitment.OriginalUtf8ByteCount,
                    ["includedUtf8ByteCount"] = instruction.Commitment.IncludedUtf8ByteCount,
                    ["isTruncated"] = instruction.Commitment.IsTruncated,
                };
                if (instructions.TryGetValue(instruction.InstructionId, out var previous) && !JsonNode.DeepEquals(previous, row))
                    throw new InvalidOperationException("campaign.request.instruction-authority-changed");
                instructions[instruction.InstructionId] = row;
            }
            var id = TargetId(target);
            executableIds.Add(target.SymbolRef, id);
            var evidenceIds = new JsonArray();
            var sourceFact = context.Facts.Evidence.Single(item => item.KindId == "source.target-declaration");
            var claim = target.StyleProfile?.ClaimPolicies.FirstOrDefault(row =>
                row.AllowedAuthorities.Contains(DocumentationScribeEvidenceAuthority.SourceDeclaration));
            if (seed && claim is not null && limits.MaximumEvidenceReferences > 0
                && sourceFact.Commitment.IncludedUtf8ByteCount <= limits.MaximumEvidenceUtf8Bytes
                && (!claim.CompleteEvidenceRequired || !sourceFact.Commitment.IsTruncated))
            {
                evidenceIds.Add(sourceFact.EvidenceId);
                evidence.Add(new JsonObject
                {
                    ["evidenceReferenceId"] = sourceFact.EvidenceId,
                    ["repositoryContextRef"] = bundle.Session.RepositoryContextRef.Value,
                    ["subject"] = new JsonObject { ["symbolRef"] = Symbol(target.SymbolRef) },
                    ["kind"] = "evidence.source.declaration",
                    ["relation"] = "evidence.declares",
                    ["authority"] = "authority.source-declaration",
                    ["locator"] = RepositoryLocator(source.Path, source.RequestedDeclarationSpan),
                    ["contentSha256"] = sourceFact.Commitment.ContentSha256,
                    ["originalUtf8ByteCount"] = sourceFact.Commitment.OriginalUtf8ByteCount,
                    ["includedUtf8ByteCount"] = sourceFact.Commitment.IncludedUtf8ByteCount,
                    ["isTruncated"] = sourceFact.Commitment.IsTruncated,
                    ["claimCategoryIds"] = new JsonArray(claim.ClaimCategoryId),
                });
            }
            manifest.Add(new JsonObject
            {
                ["targetId"] = id,
                ["target"] = new JsonObject
                {
                    ["symbolRef"] = Symbol(target.SymbolRef),
                    ["sourceCommitment"] = new JsonObject
                    {
                        ["locator"] = RepositoryLocator(source.Path, source.RequestedDeclarationSpan),
                        ["contentSha256"] = source.ContentSha256,
                    },
                    ["applicableComponents"] = new JsonArray(target.ApplicableComponents.Select(component =>
                    {
                        var row = new JsonObject { ["kind"] = ComponentKind(component.Kind), ["identity"] = component.Identity };
                        if (component.Name is not null) row["name"] = component.Name;
                        return (JsonNode)row;
                    }).ToArray()),
                },
                ["auditOutcome"] = AuditOutcome(target.AuditOutcome),
                ["styleProfile"] = target.StyleProfile is null ? null : Node(target.StyleProfile),
                ["contextReferenceIds"] = Strings(context.Facts.Instructions.OrderBy(item => item.Depth)
                    .ThenBy(item => item.Commitment.RepositoryPath, StringComparer.Ordinal).Select(item => item.InstructionId)),
                ["evidenceReferenceIds"] = evidenceIds,
                ["grouping"] = Grouping(selection.Members[index].Declaration.GroupingAuthority, target.SymbolRef.CompilationContextRef),
            });
        }
        var root = new JsonObject
        {
            ["scribeRequestVersion"] = 1,
            ["context"] = new JsonObject
            {
                ["repositoryContextRef"] = bundle.Session.RepositoryContextRef.Value,
                ["inputIdentity"] = bundle.Session.InputIdentity,
                ["targetProfile"] = TargetProfile(bundle.Classifications.TargetProfile),
            },
            ["manifest"] = manifest,
            ["executableTargetIds"] = Strings(executableSymbols.Select(symbol => executableIds[symbol])),
            ["contextReferences"] = new JsonArray(instructions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (JsonNode)pair.Value).ToArray()),
            ["evidenceReferences"] = evidence,
            ["evidenceConflicts"] = new JsonArray(),
            ["toolPolicyId"] = configuration.ScribeRequest.ToolPolicyId,
            ["limits"] = Node(limits),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        var parsed = DocumentationScribeValidation.ParseRequest(bytes);
        if (parsed.Request is not { } request || !selection.Matches(request))
            throw new InvalidOperationException("campaign.request.invalid");
        return new(bytes, selection);
    }

    internal static JsonNode? Grouping(CampaignPlanningGroupingAuthority? grouping, string compilation) => grouping is null
        ? null : Node(new
        {
            grouping.ProjectIdentity,
            containingTypeSymbolRef = new
            {
                compilationContextRef = compilation,
                documentationCommentId = grouping.ContainingTypeDocumentationCommentId
            },
            grouping.MemberFamilySha256
        });

    private static JsonNode Node<T>(T value) => JsonNode.Parse(CanonicalJson.Serialize(value).AsSpan())!;

    private static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static string TargetId(CampaignPlanningTargetFact target) => "target." + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(target.SymbolRef.CompilationContextRef + "\0" + target.SymbolRef.DocumentationCommentId
            + "\0" + ((CampaignPlanningRepositorySourceAuthority)target.Source).Path
            + "\0" + target.Source.RequestedDeclarationSpan.Start.ToString(CultureInfo.InvariantCulture) + ":" + target.Source.RequestedDeclarationSpan.End.ToString(CultureInfo.InvariantCulture)
            + "\0" + target.Source.ContentSha256))).ToLowerInvariant();

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

    private static string ComponentKind(ComponentKind kind) => kind switch
    {
        ContractScribe.Core.ComponentKind.TypeParameter => "typeParameter",
        ContractScribe.Core.ComponentKind.Parameter => "parameter",
        ContractScribe.Core.ComponentKind.Return => "return",
        ContractScribe.Core.ComponentKind.Value => "value",
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
