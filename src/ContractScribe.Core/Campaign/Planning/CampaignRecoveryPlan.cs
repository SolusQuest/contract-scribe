using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace ContractScribe.Core;

/// <summary>Durable planning facts. This projection cannot create executable source or evidence authority.</summary>
public sealed class CampaignRecoveryPlan
{
    internal CampaignRecoveryPlan(CampaignWorkPlan plan, byte[] bytes)
    {
        CampaignLineage = plan.CampaignLineage;
        ExecutionCommitmentSha256 = plan.ExecutionCommitment;
        BatchIdentity = plan.Batch.Identity;
        ExactUtf8Json = ImmutableArray.CreateRange(bytes);
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public string CampaignLineage { get; }
    public string ExecutionCommitmentSha256 { get; }
    public string BatchIdentity { get; }
    public ImmutableArray<byte> ExactUtf8Json { get; }
    public string Sha256 { get; }

    /// <summary>Checks every durable fact against independently validated current planning inputs.</summary>
    public bool Matches(ReadOnlySpan<byte> savedProjection) => savedProjection.SequenceEqual(ExactUtf8Json.AsSpan());

    public override string ToString() => nameof(CampaignRecoveryPlan);
}

public static partial class CampaignPlanner
{
    /// <summary>Projects a validated plan without source text, style literals or runtime capabilities.</summary>
    public static CampaignRecoveryPlan CreateRecoveryProjection(CampaignWorkPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "campaign-recovery-plan-v1",
            plan.CampaignLineage,
            plan.OpaqueSnapshotBinding,
            plan.AuditDocumentSha256,
            plan.ExecutionCommitment,
            plan.TargetProfile,
            workItems = plan.WorkItems.Select(item => new
            {
                item.WorkItemKey,
                item.OwnerEquivalenceRef,
                targets = item.Targets.Select(target => new
                {
                    target.SymbolRef,
                    target.PrimaryKind,
                    target.Origin,
                    source = RecoverySource(target.Source),
                    target.AuthoritativeDeclarationId,
                    target.ApplicableComponents,
                    target.OwnerSymbolRefs,
                    target.AuditOutcome,
                    target.AuditReason,
                    target.AuditRowSha256,
                    target.M3Eligible,
                    styleCommitment = RecoveryStyle(target.StyleProfile),
                    target.GroupingAuthority,
                }),
                item.ViolationCauses,
                item.Disposition,
            }),
            plan.Summary,
            plan.Batch,
        });
        Require(bytes.Length <= CampaignStateContract.MaximumArtifactUtf8Bytes,
            CampaignPlanningValidationCode.InvalidBound, "The recovery plan exceeds its byte bound.");
        return new(plan, bytes);
    }

    private static object RecoverySource(CampaignPlanningSourceAuthority source) => new
    {
        source.Kind,
        source.AuthoritativeDeclarationId,
        source.ContentSha256,
        source.ObservationDeclarationSpan,
        source.RequestedDeclarationSpan,
        source.CanonicalDeclarationSpan,
        source.OwnerSpan,
        source.DocumentationSpan,
        source.BlockState,
        source.Writable,
        path = (source as CampaignPlanningRepositorySourceAuthority)?.Path,
        physicalSourceCommitment = (source as CampaignPlanningRepositorySourceAuthority)?.PhysicalSourceCommitmentSha256,
        observedSourceTextSha256 = (source as CampaignPlanningRepositorySourceAuthority)?.ObservedSourceTextSha256,
        encoding = (source as CampaignPlanningRepositorySourceAuthority)?.Encoding,
        producerId = (source as CampaignPlanningGeneratedSourceAuthority)?.ProducerId,
        outputId = (source as CampaignPlanningGeneratedSourceAuthority)?.OutputId,
    };

    private static string? RecoveryStyle(DocumentationScribeStyleProfile? style)
    {
        if (style is null) return null;
        using var writer = new CampaignPlanningCommitmentWriter("contract-scribe/recovery-style/v1");
        AddStyleProfile(writer, style);
        return writer.Complete();
    }
}

/// <summary>The remote checkpoint consumer binds a complete plan before accepting execution state.</summary>
public interface ICampaignRecoveryPlanStore
{
    ValueTask<bool> BindPlanAsync(CampaignRecoveryPlan plan, CancellationToken cancellationToken);
}
