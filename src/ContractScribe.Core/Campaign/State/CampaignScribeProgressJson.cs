using System.Text.Json;

namespace ContractScribe.Core;

public static partial class CampaignStateJson
{
    private static void WriteScribeProgress(Utf8JsonWriter writer, CampaignScribeProgress? progress)
    {
        if (progress is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("batchIdentity", progress.BatchIdentity);
        writer.WriteNumber("repairCount", progress.RepairCount);
        writer.WriteNumber("nextSequence", progress.NextSequence);
        writer.WriteString("lastProgressSha256", progress.LastProgressSha256);
        writer.WriteString("lastProgressRequestSha256", progress.LastProgressRequestSha256);
        writer.WriteString("lastProgressAttemptId", progress.LastProgressAttemptId?.Value);
        writer.WriteBoolean("lastProgressStopped", progress.LastProgressStopped);
        writer.WriteBoolean("lastProgressFinished", progress.LastProgressFinished);
        writer.WritePropertyName("targetFailures"); writer.WriteStartArray();
        foreach (var failure in progress.TargetFailures)
        {
            writer.WriteStartObject(); writer.WriteString("targetId", failure.TargetId);
            writer.WriteString("validationCode", failure.ValidationCode); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("session");
        if (progress.Session is not { } session) writer.WriteNullValue();
        else
        {
            writer.WriteStartObject();
            writer.WriteString("anchorWorkItemKey", session.AnchorWorkItemKey);
            writer.WriteNumber("anchorOuterAttemptOrdinal", session.AnchorOuterAttemptOrdinal);
            writer.WriteString("attemptId", session.AttemptId.Value);
            writer.WritePropertyName("admittedWorkItemKeys"); WriteStrings(writer, session.AdmittedWorkItemKeys);
            writer.WriteBoolean("ended", session.Ended);
            writer.WriteEndObject();
        }
        writer.WritePropertyName("members"); writer.WriteStartArray();
        foreach (var member in progress.Members)
        {
            writer.WriteStartObject();
            writer.WriteString("workItemKey", member.WorkItemKey);
            writer.WriteString("targetId", member.TargetId);
            writer.WriteNumber("outerAttemptOrdinal", member.OuterAttemptOrdinal);
            writer.WriteString("sessionAnchorWorkItemKey", member.SessionAnchorWorkItemKey);
            writer.WriteNumber("sessionAnchorOuterAttemptOrdinal", member.SessionAnchorOuterAttemptOrdinal);
            writer.WriteString("attemptId", member.AttemptId.Value);
            writer.WriteString("scribeRequestSha256", member.ScribeRequestSha256);
            writer.WriteString("contentSha256", member.ContentSha256);
            writer.WritePropertyName("skip");
            if (member.Skip is not { } skip) writer.WriteNullValue();
            else
            {
                writer.WriteStartObject(); writer.WriteString("reason", DocumentationScribeVocabulary.GetId(skip.Reason));
                writer.WritePropertyName("evidence"); writer.WriteStartArray();
                foreach (var evidence in skip.Evidence) WriteEvidence(writer, evidence);
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject();
    }

    private static CampaignScribeProgress? ParseScribeProgress(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        ExpectObject(element, "batchIdentity", "repairCount", "nextSequence", "lastProgressSha256", "lastProgressRequestSha256",
            "lastProgressAttemptId", "lastProgressStopped", "lastProgressFinished", "targetFailures", "session", "members");
        DocumentationScribeAttemptId? attempt = null;
        if (ReadNullableString(element, "lastProgressAttemptId") is { } attemptText)
        {
            if (!DocumentationScribeAttemptId.TryParse(attemptText, out var parsed)) throw Vocabulary();
            attempt = parsed;
        }
        return new(ReadString(element, "batchIdentity"), ReadInt32(element, "repairCount"), ReadInt32(element, "nextSequence"),
            ReadNullableString(element, "lastProgressSha256"), ReadNullableString(element, "lastProgressRequestSha256"), attempt,
            ReadBoolean(element, "lastProgressStopped"), ParseScribeSession(element.GetProperty("session")),
            ParseArray(element.GetProperty("members"), ParseScribeMember, CampaignStateContract.MaximumWorkItems))
        {
            LastProgressFinished = ReadBoolean(element, "lastProgressFinished"),
            TargetFailures = ParseArray(element.GetProperty("targetFailures"), ParseScribeTargetFailure, CampaignStateContract.MaximumWorkItems),
        };
    }

    private static DocumentationScribeTargetFailure ParseScribeTargetFailure(JsonElement element)
    {
        ExpectObject(element, "targetId", "validationCode");
        return new(ReadString(element, "targetId"), ReadString(element, "validationCode"));
    }

    private static CampaignScribeSessionProgress? ParseScribeSession(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        ExpectObject(element, "anchorWorkItemKey", "anchorOuterAttemptOrdinal", "attemptId", "admittedWorkItemKeys", "ended");
        if (!DocumentationScribeAttemptId.TryParse(ReadString(element, "attemptId"), out var attempt)) throw Vocabulary();
        return new(ReadString(element, "anchorWorkItemKey"), ReadInt32(element, "anchorOuterAttemptOrdinal"), attempt,
            ParseStrings(element.GetProperty("admittedWorkItemKeys"), CampaignStateContract.MaximumWorkItems), ReadBoolean(element, "ended"));
    }

    private static CampaignScribeMemberProgress ParseScribeMember(JsonElement element)
    {
        ExpectObject(element, "workItemKey", "targetId", "outerAttemptOrdinal", "sessionAnchorWorkItemKey",
            "sessionAnchorOuterAttemptOrdinal", "attemptId", "scribeRequestSha256", "contentSha256", "skip");
        if (!DocumentationScribeAttemptId.TryParse(ReadString(element, "attemptId"), out var attempt)) throw Vocabulary();
        return new(ReadString(element, "workItemKey"), ReadString(element, "targetId"), ReadInt32(element, "outerAttemptOrdinal"),
            ReadString(element, "sessionAnchorWorkItemKey"), ReadInt32(element, "sessionAnchorOuterAttemptOrdinal"), attempt,
            ReadString(element, "scribeRequestSha256"), ReadNullableString(element, "contentSha256"), ParseScribeSkip(element.GetProperty("skip")));
    }

    private static CampaignScribeSkipProjection? ParseScribeSkip(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        ExpectObject(element, "reason", "evidence");
        return new(ReadString(element, "reason") switch
        {
            "scribe.skip.insufficient-evidence" => DocumentationScribeSkipReason.InsufficientEvidence,
            "scribe.skip.unsupported-current-m3-domain" => DocumentationScribeSkipReason.UnsupportedCurrentM3Domain,
            _ => throw Vocabulary(),
        }, ParseArray(element.GetProperty("evidence"), ParseEvidence, CampaignStateContract.MaximumEvidenceReferencesPerBlock));
    }
}
