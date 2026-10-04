using System.Text.Json;

namespace ContractScribe.Core;

public static partial class CampaignStateJson
{
    private static void WriteBatch(Utf8JsonWriter writer, CampaignCheckpointState state)
    {
        writer.WritePropertyName("batch");
        writer.WriteStartObject();
        writer.WriteString("identity", state.Batch.Identity);
        writer.WriteString("planCommitment", state.Batch.PlanCommitment);
        writer.WriteNumber("creationQuota", state.Batch.CreationQuota);
        writer.WritePropertyName("selectedTargetKeys");
        WriteStrings(writer, state.Batch.SelectedTargetKeys);
        writer.WriteEndObject();
        writer.WritePropertyName("targetProgress");
        writer.WriteStartArray();
        foreach (var progress in state.TargetProgress)
        {
            var target = progress.Target;
            writer.WriteStartObject();
            writer.WriteString("targetKey", target.TargetKey);
            writer.WriteString("workItemKey", target.WorkItemKey);
            WriteSymbol(writer, "symbolRef", target.SymbolRef);
            writer.WriteBoolean("batchEligible", target.BatchEligible);
            writer.WriteBoolean("dispatchable", target.Dispatchable);
            writer.WriteString("groupIdentity", target.GroupIdentity);
            writer.WriteString("memberFamilyIdentity", target.MemberFamilyIdentity);
            writer.WriteString("status", TargetProgressId(progress.Kind));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static CampaignFixedBatch ParseBatch(JsonElement root)
    {
        var batch = root.GetProperty("batch");
        ExpectObject(batch, "identity", "planCommitment", "creationQuota", "selectedTargetKeys");
        var targets = ParseArray(root.GetProperty("targetProgress"), ParseBatchTarget, CampaignStateContract.MaximumCompleteTargets);
        return new CampaignFixedBatch(ReadString(batch, "planCommitment"), ReadInt32(batch, "creationQuota"),
            targets, ParseStrings(batch.GetProperty("selectedTargetKeys"), CampaignStateContract.MaximumWorkItems),
            ReadString(batch, "identity"));
    }

    private static CampaignBatchTarget ParseBatchTarget(JsonElement element)
    {
        ExpectObject(element, "targetKey", "workItemKey", "symbolRef", "batchEligible", "dispatchable",
            "groupIdentity", "memberFamilyIdentity", "status");
        // Canonical readback must also equal the status derived from the durable owner state.
        _ = ParseTargetProgress(ReadString(element, "status"));
        return new(ReadString(element, "targetKey"), ReadString(element, "workItemKey"),
            ParseSymbol(element.GetProperty("symbolRef")), ReadBoolean(element, "batchEligible"),
            ReadBoolean(element, "dispatchable"), ReadNullableString(element, "groupIdentity"),
            ReadNullableString(element, "memberFamilyIdentity"));
    }

    private static string TargetProgressId(CampaignTargetProgressKind kind) => kind switch
    {
        CampaignTargetProgressKind.Pending => "pending",
        CampaignTargetProgressKind.Deferred => "deferred",
        CampaignTargetProgressKind.Active => "active",
        CampaignTargetProgressKind.Retryable => "retryable",
        CampaignTargetProgressKind.ProposalComplete => "proposal-complete",
        CampaignTargetProgressKind.AcceptedProposal => "accepted-proposal",
        CampaignTargetProgressKind.Skipped => "skipped",
        CampaignTargetProgressKind.Failed => "failed",
        CampaignTargetProgressKind.Suppressed => "suppressed",
        CampaignTargetProgressKind.InfrastructureBlocked => "infrastructure-blocked",
        CampaignTargetProgressKind.UnsupportedCurrentExecutor => "unsupported-current-executor",
        CampaignTargetProgressKind.Excluded => "excluded",
        _ => throw Vocabulary(),
    };

    private static CampaignTargetProgressKind ParseTargetProgress(string value) => value switch
    {
        "pending" => CampaignTargetProgressKind.Pending,
        "deferred" => CampaignTargetProgressKind.Deferred,
        "active" => CampaignTargetProgressKind.Active,
        "retryable" => CampaignTargetProgressKind.Retryable,
        "proposal-complete" => CampaignTargetProgressKind.ProposalComplete,
        "accepted-proposal" => CampaignTargetProgressKind.AcceptedProposal,
        "skipped" => CampaignTargetProgressKind.Skipped,
        "failed" => CampaignTargetProgressKind.Failed,
        "suppressed" => CampaignTargetProgressKind.Suppressed,
        "infrastructure-blocked" => CampaignTargetProgressKind.InfrastructureBlocked,
        "unsupported-current-executor" => CampaignTargetProgressKind.UnsupportedCurrentExecutor,
        "excluded" => CampaignTargetProgressKind.Excluded,
        _ => throw Vocabulary(),
    };

    private static string AttemptDispositionId(CampaignAttemptDisposition disposition) => disposition switch
    {
        CampaignAttemptDisposition.Open => "open",
        CampaignAttemptDisposition.SuppressedAtAttemptLimit => "suppressed-at-attempt-limit",
        _ => throw Vocabulary(),
    };

    private static CampaignAttemptDisposition ParseAttemptDisposition(string value) => value switch
    {
        "open" => CampaignAttemptDisposition.Open,
        "suppressed-at-attempt-limit" => CampaignAttemptDisposition.SuppressedAtAttemptLimit,
        _ => throw Vocabulary(),
    };
}
