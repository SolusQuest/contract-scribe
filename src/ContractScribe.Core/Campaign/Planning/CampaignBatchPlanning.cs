using System.Collections.Immutable;

namespace ContractScribe.Core;

/// <summary>A host invocation limit, independent of snapshot correctness and patch capacity.</summary>
public readonly record struct CampaignInvocationTargetLimit
{
    public CampaignInvocationTargetLimit(int maximumTargets)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumTargets);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumTargets, 4_096);
        MaximumTargets = maximumTargets;
    }

    public int MaximumTargets { get; }
}

/// <summary>Bound read-only semantic and applicable-context facts supplied by the composition owner.</summary>
public sealed record CampaignPlanningGroupingAuthority(
    string ProjectIdentity,
    string ContainingTypeDocumentationCommentId,
    string MemberFamilySha256,
    string? InstructionsSha256,
    string? StyleConfigurationSha256);

public sealed record CampaignBatchTarget(
    string TargetKey,
    string WorkItemKey,
    SymbolRef SymbolRef,
    bool BatchEligible,
    bool Dispatchable,
    string? GroupIdentity,
    string? MemberFamilyIdentity);

public sealed record CampaignFixedBatch
{
    internal CampaignFixedBatch(
        string planCommitment,
        int creationQuota,
        ImmutableArray<CampaignBatchTarget> completeTargets,
        ImmutableArray<string> selectedTargetKeys,
        string? identity = null)
    {
        PlanCommitment = planCommitment;
        CreationQuota = creationQuota;
        CompleteTargets = completeTargets;
        SelectedTargetKeys = selectedTargetKeys;
        Identity = identity ?? CampaignPlanner.ComputeBatchIdentity(this);
    }

    public string PlanCommitment { get; }
    public int CreationQuota { get; }
    public ImmutableArray<CampaignBatchTarget> CompleteTargets { get; }
    public ImmutableArray<string> SelectedTargetKeys { get; }
    public string Identity { get; }
}

public static partial class CampaignPlanner
{
    internal static string CreateInstructionStackCommitment(IEnumerable<DocumentationScribeContextReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        var instructions = references.Where(reference => reference.Kind == DocumentationScribeContextReferenceKind.ProjectInstruction)
            .Take(513).ToImmutableArray();
        Require(instructions.Length <= 512, CampaignPlanningValidationCode.InvalidBound,
            "Instruction commitments exceed the finite context-reference bound.");
        using var writer = new CampaignPlanningCommitmentWriter("contract-scribe/campaign-instruction-stack/v1");
        writer.Add("count", instructions.Length);
        foreach (var instruction in instructions)
        {
            RequireOpaqueIdentifier(instruction.ContextReferenceId, nameof(instruction.ContextReferenceId));
            RequireCanonicalPath(instruction.Path);
            RequireSha256(instruction.ContentSha256, nameof(instruction.ContentSha256));
            Require(instruction.OriginalUtf8ByteCount >= 0 && instruction.IncludedUtf8ByteCount >= 0,
                CampaignPlanningValidationCode.InvalidBound, "Instruction byte observations must be nonnegative.");
            writer.Add("id", instruction.ContextReferenceId);
            writer.Add("path", instruction.Path);
            writer.Add("sha", instruction.ContentSha256);
            writer.Add("original-bytes", instruction.OriginalUtf8ByteCount);
            writer.Add("included-bytes", instruction.IncludedUtf8ByteCount);
            writer.Add("truncated", instruction.IsTruncated);
        }
        return writer.Complete();
    }

    private static CampaignFixedBatch SelectBatch(
        string commitment,
        ImmutableArray<CampaignPlanningWorkItem> workItems,
        CampaignInvocationTargetLimit limit)
    {
        var complete = workItems.SelectMany(work => work.Targets.Select(target =>
        {
            var eligible = IsBatchEligible(work);
            var group = eligible ? GroupIdentity(target) : null;
            return new CampaignBatchTarget(
                ComputeTargetKey(commitment, work.WorkItemKey, target.SymbolRef), work.WorkItemKey, target.SymbolRef,
                eligible, work.Disposition.Kind == CampaignPlanningDispositionKind.Executable,
                group, eligible ? target.GroupingAuthority!.MemberFamilySha256 : null);
        })).ToImmutableArray();

        var selected = ImmutableArray.CreateBuilder<string>();
        if (limit.MaximumTargets > 0)
        {
            // GroupBy preserves first occurrence and member order from the canonical complete plan.
            foreach (var group in complete.Where(target => target.BatchEligible)
                         .GroupBy(target => target.GroupIdentity, StringComparer.Ordinal))
            {
                var members = group.ToImmutableArray();
                ImmutableArray<ImmutableArray<CampaignBatchTarget>> subdivisions = members.Length <= limit.MaximumTargets
                    ? [members]
                    : members.GroupBy(target => target.MemberFamilyIdentity, StringComparer.Ordinal)
                        .SelectMany(family => family.Chunk(limit.MaximumTargets)
                            .Select(chunk => chunk.ToImmutableArray())).ToImmutableArray();
                foreach (var subdivision in subdivisions)
                {
                    if (selected.Count + subdivision.Length > limit.MaximumTargets)
                    {
                        return new CampaignFixedBatch(commitment, limit.MaximumTargets, complete, selected.ToImmutable());
                    }
                    selected.AddRange(subdivision.Select(target => target.TargetKey));
                }
            }
        }
        return new CampaignFixedBatch(commitment, limit.MaximumTargets, complete, selected.ToImmutable());
    }

    private static bool IsBatchEligible(CampaignPlanningWorkItem work) =>
        work.Targets.Length == 1
        && work.Targets[0].PrimaryKind != PrimarySymbolKind.Unknown
        && (work.Disposition.Kind == CampaignPlanningDispositionKind.Executable
            || work.Disposition.TerminalReasons.SequenceEqual([CampaignPlanningTerminalReason.UnsupportedTargetKind]));

    private static string GroupIdentity(CampaignPlanningTargetFact target)
    {
        var authority = target.GroupingAuthority!;
        using var writer = new CampaignPlanningCommitmentWriter("contract-scribe/campaign-semantic-group/v1");
        writer.Add("project", authority.ProjectIdentity);
        writer.Add("compilation", target.SymbolRef.CompilationContextRef);
        writer.Add("containing-type", authority.ContainingTypeDocumentationCommentId);
        writer.Add("instructions", authority.InstructionsSha256!);
        writer.Add("style", authority.StyleConfigurationSha256!);
        return writer.Complete();
    }

    internal static string ComputeBatchIdentity(CampaignFixedBatch batch)
    {
        using var writer = new CampaignPlanningCommitmentWriter("contract-scribe/campaign-fixed-batch/v1");
        writer.Add("plan", batch.PlanCommitment);
        writer.Add("creation-quota", batch.CreationQuota);
        writer.Add("selected-count", batch.SelectedTargetKeys.Length);
        foreach (var key in batch.SelectedTargetKeys)
        {
            writer.Add("target-key", key);
        }
        return writer.Complete();
    }

    internal static string ComputeTargetKey(string planCommitment, string workItemKey, SymbolRef symbol)
    {
        using var writer = new CampaignPlanningCommitmentWriter("contract-scribe/campaign-target/v1");
        writer.Add("plan", planCommitment);
        writer.Add("work", workItemKey);
        AddSymbolRef(writer, "target", symbol);
        return "campaign-target." + writer.Complete();
    }

    private static void AddGrouping(CampaignPlanningCommitmentWriter writer, CampaignPlanningGroupingAuthority? authority)
    {
        writer.Add("grouping.present", authority is not null);
        if (authority is null) return;
        writer.Add("grouping.project", authority.ProjectIdentity);
        writer.Add("grouping.containing-type", authority.ContainingTypeDocumentationCommentId);
        writer.Add("grouping.member-family", authority.MemberFamilySha256);
        writer.AddOptional("grouping.instructions", authority.InstructionsSha256);
        writer.AddOptional("grouping.style", authority.StyleConfigurationSha256);
    }

    private static void ValidateGrouping(
        CampaignPlanningTargetAuthority authority,
        CampaignPlanningSourceSessionIndex sourceSession)
    {
        if (authority.GroupingAuthority is not { } grouping) return;
        RequireIdentifier(grouping.ProjectIdentity, nameof(grouping.ProjectIdentity));
        Require(grouping.ProjectIdentity == sourceSession.ProjectIdentity(authority.Target.SymbolRef.CompilationContextRef)
                && grouping.ContainingTypeDocumentationCommentId is { Length: >= 3 and <= 1_024 }
                && grouping.ContainingTypeDocumentationCommentId.StartsWith("T:", StringComparison.Ordinal),
            CampaignPlanningValidationCode.InvalidOwnerAuthority,
            "Semantic grouping must bind the current project and a named-type authority.");
        RequireText(grouping.ContainingTypeDocumentationCommentId, nameof(grouping.ContainingTypeDocumentationCommentId));
        RequireSha256(grouping.MemberFamilySha256, nameof(grouping.MemberFamilySha256));
        if (grouping.InstructionsSha256 is not null) RequireSha256(grouping.InstructionsSha256, nameof(grouping.InstructionsSha256));
        if (grouping.StyleConfigurationSha256 is not null) RequireSha256(grouping.StyleConfigurationSha256, nameof(grouping.StyleConfigurationSha256));
        if (authority.Target.PrimaryKind is PrimarySymbolKind.Class or PrimarySymbolKind.Struct
            or PrimarySymbolKind.Interface or PrimarySymbolKind.Enum or PrimarySymbolKind.Delegate)
        {
            Require(grouping.ContainingTypeDocumentationCommentId == authority.Target.SymbolRef.DocumentationCommentId,
                CampaignPlanningValidationCode.InvalidOwnerAuthority,
                "Named-type targets must own independent semantic groups.");
        }
    }
}
