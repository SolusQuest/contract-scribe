using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace ContractScribe.Core;

public static partial class DocumentationScribeValidation
{
    public const int MaximumSubmissionBatchSize = 8;
    public const int MaximumInvalidSubmissions = 3;

    public static ImmutableArray<string> GetStyleProfileIds(DocumentationScribeRequest request) =>
        request.ExecutableTargetIds.Select(id => request.Manifest.Single(member => member.TargetId == id)
            .StyleProfile!.StyleProfileId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();

    public static DocumentationScribeSubmissionParseResult ValidateTargetSubmission(
        DocumentationScribeRequest request, string targetId,
        ImmutableArray<DocumentationScribeEvidenceReference> trustedDynamicEvidenceReferences,
        ReadOnlyMemory<byte> resultUtf8Json)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.ExecutableTargetIds.Contains(targetId, StringComparer.Ordinal))
            return new(null, new("scribe.result.invalid-correlation", "/targetId"));
        var rawFailure = TryParseArtifact(resultUtf8Json, "scribe.result", null, out var document);
        if (rawFailure is not null) return new(null, new(rawFailure.Code, rawFailure.Pointer));
        using (document)
        {
            try
            {
                var overlay = trustedDynamicEvidenceReferences.IsDefault ? [] : trustedDynamicEvidenceReferences;
                ValidateDynamicEvidenceOverlay(request, overlay, overlay);
                var view = WithEvidenceReferences(request, request.EvidenceReferences.AddRange(overlay)) with
                { ValidationTargetId = targetId };
                var terminal = ParseTerminal(document!.RootElement, string.Empty, view);
                if (terminal.Kind is not (DocumentationScribeTerminalKind.Proposal or DocumentationScribeTerminalKind.Skip))
                    throw Fail("invalid-terminal", "/kind");
                var cited = terminal is DocumentationScribeProposalTerminal proposal
                    ? proposal.ContentUnits.SelectMany(item => item.EvidenceReferenceIds).ToHashSet(StringComparer.Ordinal)
                    : ((DocumentationScribeSkipTerminal)terminal).EvidenceReferenceIds.ToHashSet(StringComparer.Ordinal);
                overlay = overlay.Where(reference => cited.Contains(reference.EvidenceReferenceId)).ToImmutableArray();
                var bytes = NormalizeSubmissionJson(document.RootElement);
                return new(new(request.ArtifactSha256, targetId,
                    Convert.ToHexString(SHA256.HashData(bytes.AsSpan())).ToLowerInvariant(), terminal, overlay, bytes), null);
            }
            catch (ContractFailure failure) { return new(null, new("scribe.result." + failure.Category, failure.Pointer)); }
        }
    }

    public static DocumentationScribeTargetSubmission AcknowledgeSubmission(
        DocumentationScribeValidatedSubmission validation, DocumentationScribeSubmissionAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (acknowledgement.ScribeRequestSha256 != validation.ScribeRequestSha256
            || acknowledgement.TargetId != validation.TargetId || acknowledgement.ContentSha256 != validation.ContentSha256
            || !Enum.IsDefined(acknowledgement.Stage)
            || acknowledgement.Stage == DocumentationScribeAcknowledgementStage.PatchAccepted
                && validation.Result is not DocumentationScribeProposalTerminal)
            throw new ArgumentException("The Host acknowledgement is not correlated to validated content.", nameof(acknowledgement));
        return new(validation, acknowledgement.Stage);
    }

    public static DocumentationScribeRunResult CreateSessionResult(DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId, DocumentationScribeTerminal stop,
        DocumentationScribeRunEnvelopeInput envelope, ImmutableArray<DocumentationScribeTargetSubmission> submissions,
        ImmutableArray<DocumentationScribeTargetFailure> targetFailures, int repairCount, int nextSequence)
    {
        var dynamic = submissions.SelectMany(item => item.Validation.DynamicEvidenceReferences)
            .GroupBy(reference => reference.EvidenceReferenceId, StringComparer.Ordinal)
            .Select(group => group.All(item => EvidenceReferenceEquivalent(item, group.First())) ? group.First()
                : throw new ArgumentException("Conflicting trusted evidence.", nameof(submissions)))
            .OrderBy(reference => reference.EvidenceReferenceId, StringComparer.Ordinal).ToImmutableArray();
        var result = new DocumentationScribeRunResult(request.ArtifactSha256, attemptId, dynamic, stop,
            CreateRunEnvelope(request, attemptId, envelope, stop), submissions.OrderBy(item => item.TargetId, StringComparer.Ordinal).ToImmutableArray(),
            targetFailures.OrderBy(item => item.TargetId, StringComparer.Ordinal).ToImmutableArray(), repairCount, nextSequence);
        ValidateIncrementalResult(request, attemptId, result);
        return result;
    }

    public static DocumentationScribeRunResult WithProgress(DocumentationScribeRequest request,
        DocumentationScribeRunResult result, ImmutableArray<DocumentationScribeTargetSubmission> submissions,
        ImmutableArray<DocumentationScribeTargetFailure> failures, int repairCount, int nextSequence) =>
        CreateSessionResult(request, result.AttemptId, result.Stop, EnvelopeInput(result.RunEnvelope),
            submissions, failures, repairCount, nextSequence);

    public static DocumentationScribeRunResult CreateFinishedResult(DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId, DocumentationScribeRunEnvelopeInput envelope,
        ImmutableArray<DocumentationScribeTargetSubmission> submissions, ImmutableArray<DocumentationScribeTargetFailure> failures,
        int repairCount, int nextSequence, bool contextStop = false)
    {
        var satisfied = request.Manifest.All(member =>
        {
            var submitted = submissions.SingleOrDefault(item => item.TargetId == member.TargetId);
            return submitted is not null ? submitted.Result is DocumentationScribeProposalTerminal
                && submitted.Stage >= DocumentationScribeAcknowledgementStage.Validated
                : member.AuditOutcome == AuditOutcome.Compliant && !failures.Any(item => item.TargetId == member.TargetId);
        });
        return CreateSessionResult(request, attemptId, new DocumentationScribeFinishedTerminal(contextStop
            ? DocumentationScribeFinishReason.Context : satisfied ? DocumentationScribeFinishReason.Completed
                : DocumentationScribeFinishReason.Unresolved), envelope, submissions, failures, repairCount, nextSequence);
    }

    public static ImmutableArray<DocumentationScribeCoverage> GetCoverage(
        DocumentationScribeRequest request, DocumentationScribeRunResult result) => request.Manifest.Select(member =>
        {
            var submission = result.Submissions.SingleOrDefault(item => item.TargetId == member.TargetId);
            var status = submission?.Result is DocumentationScribeSkipTerminal ? "skipped"
                : submission is not null ? StageId(submission.Stage)
                : result.TargetFailures.Any(item => item.TargetId == member.TargetId) ? "failed"
                : member.AuditOutcome == AuditOutcome.Compliant ? "compliant" : "remaining";
            return new DocumentationScribeCoverage(member.TargetId, member.AuditOutcome, status, submission?.Stage);
        }).ToImmutableArray();

    public static string StageId(DocumentationScribeAcknowledgementStage stage) => stage switch
    {
        DocumentationScribeAcknowledgementStage.Received => "received",
        DocumentationScribeAcknowledgementStage.Validated => "validated",
        DocumentationScribeAcknowledgementStage.DurablyRecorded => "durably-recorded",
        DocumentationScribeAcknowledgementStage.PatchAccepted => "patch-accepted",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    private static DocumentationScribeAcknowledgementStage ParseStage(JsonElement element, string pointer) =>
        ReadStringValue(element, pointer, 32) switch
        {
            "received" => DocumentationScribeAcknowledgementStage.Received,
            "validated" => DocumentationScribeAcknowledgementStage.Validated,
            "durably-recorded" => DocumentationScribeAcknowledgementStage.DurablyRecorded,
            "patch-accepted" => DocumentationScribeAcknowledgementStage.PatchAccepted,
            _ => throw Fail("invalid-vocabulary", pointer),
        };

    private static void ValidateIncrementalResult(DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId, DocumentationScribeRunResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        if (result.ScribeRequestSha256 != request.ArtifactSha256 || result.AttemptId != attemptId
            || result.RunEnvelope.ScribeRequestSha256 != request.ArtifactSha256 || result.RunEnvelope.AttemptId != attemptId
            || result.RunEnvelope.ToolPolicyId != request.ToolPolicyId
            || !result.RunEnvelope.StyleProfileIds.SequenceEqual(GetStyleProfileIds(request), StringComparer.Ordinal)
            || result.Stop.Kind is not (DocumentationScribeTerminalKind.Finished or DocumentationScribeTerminalKind.Failure
                or DocumentationScribeTerminalKind.Cancelled)
            || result.RepairCount is < 0 or > MaximumInvalidSubmissions
            || result.NextSequence < 1 || result.NextSequence > int.MaxValue
            || result.Submissions.Length > request.ExecutableTargetIds.Length || result.TargetFailures.Length > request.ExecutableTargetIds.Length)
            throw new ArgumentException("Invalid incremental result correlation.", nameof(result));
        if (result.Submissions.IsEmpty && !result.DynamicEvidenceReferences.IsEmpty)
            throw new ArgumentException("Unsubmitted evidence cannot enter the result.", nameof(result));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var submission in result.Submissions)
        {
            if (!ids.Add(submission.TargetId) || submission.Validation.ScribeRequestSha256 != request.ArtifactSha256
                || !request.ExecutableTargetIds.Contains(submission.TargetId, StringComparer.Ordinal)
                || !Enum.IsDefined(submission.Stage)
                || submission.Stage == DocumentationScribeAcknowledgementStage.PatchAccepted
                    && submission.Result is not DocumentationScribeProposalTerminal)
                throw new ArgumentException("Invalid submitted member.", nameof(result));
            var parsed = ValidateTargetSubmission(request, submission.TargetId,
                submission.Validation.DynamicEvidenceReferences, submission.Validation.ResultUtf8Json.AsMemory());
            if (parsed.Submission?.ContentSha256 != submission.ContentSha256)
                throw new ArgumentException("Invalid submitted content.", nameof(result));
        }
        var citedEvidence = result.Submissions.SelectMany(item => item.Validation.DynamicEvidenceReferences)
            .GroupBy(item => item.EvidenceReferenceId, StringComparer.Ordinal)
            .Select(group => group.First()).OrderBy(item => item.EvidenceReferenceId, StringComparer.Ordinal).ToArray();
        if (citedEvidence.Length != result.DynamicEvidenceReferences.Length
            || citedEvidence.Where((reference, index) => !EvidenceReferenceEquivalent(reference, result.DynamicEvidenceReferences[index])).Any())
            throw new ArgumentException("The result overlay must equal its retained cited evidence.", nameof(result));
        foreach (var failure in result.TargetFailures)
            if (!ids.Add(failure.TargetId) || !request.ExecutableTargetIds.Contains(failure.TargetId, StringComparer.Ordinal)
                || !IsIdentifier(failure.ValidationCode, false))
                throw new ArgumentException("Invalid failed member.", nameof(result));
        if (result.Stop is DocumentationScribeFinishedTerminal finished)
        {
            var complete = GetCoverage(request, result).All(item => item.Status is "compliant" or "validated" or "durably-recorded" or "patch-accepted");
            if (!Enum.IsDefined(finished.Reason) || finished.Reason != DocumentationScribeFinishReason.Context
                && (finished.Reason == DocumentationScribeFinishReason.Completed) != complete)
                throw new ArgumentException("Finish does not match actual coverage.", nameof(result));
        }
        _ = CreateRunEnvelope(request, attemptId, EnvelopeInput(result.RunEnvelope), result.Stop);
        ValidateProviderFailureMatrixForFactory(request, result.Stop, result.RunEnvelope);
    }

    private static DocumentationScribeRunEnvelopeInput EnvelopeInput(DocumentationScribeRunEnvelope envelope) =>
        new(envelope.ProviderConfigurationId, envelope.ModelConfigurationId, envelope.ScribeProtocolId,
            envelope.AttemptNumber, envelope.ProviderRequestCount, envelope.ToolRoundCount, envelope.ToolCallCount,
            envelope.ElapsedMilliseconds, envelope.Usage is { } usage ? new(usage.InputTokens, usage.OutputTokens,
                usage.CachedInputTokens, usage.UncachedInputTokens, usage.ReasoningTokens) : null,
            envelope.Cache, envelope.Cost is { } cost ? new(cost.CurrencyId, cost.AmountMicrounits) : null,
            envelope.Diagnostics.Select(item => new DocumentationScribeDiagnosticInput(item.Code, item.Stage, item.ReferenceId,
                item.ValidationCode)).ToImmutableArray())
        { RestoredRetryableProviderFailures = envelope.RestoredRetryableProviderFailures, UsageCoverage = envelope.UsageCoverage };

    private static DocumentationScribeUsageCoverage ParseUsageCoverage(JsonElement element, string pointer, int requests)
    {
        ExpectProperties(element, pointer, ["inputRequests", "outputRequests", "cachedInputRequests", "uncachedInputRequests",
            "reasoningRequests", "costRequests", "cacheRequests"]);
        return new(ReadBoundedInt(element, "inputRequests", pointer, 0, requests), ReadBoundedInt(element, "outputRequests", pointer, 0, requests),
            ReadBoundedInt(element, "cachedInputRequests", pointer, 0, requests), ReadBoundedInt(element, "uncachedInputRequests", pointer, 0, requests),
            ReadBoundedInt(element, "reasoningRequests", pointer, 0, requests), ReadBoundedInt(element, "costRequests", pointer, 0, requests),
            ReadBoundedInt(element, "cacheRequests", pointer, 0, requests));
    }

    private static void ValidateUsageCoverage(DocumentationScribeUsageCoverage? coverage, int requests, DocumentationScribeUsageObservation? usage)
    {
        if (coverage is null) return; // Reporting population is explicitly unknown, not an implied complete total.
        if (new[] { coverage.InputRequests, coverage.OutputRequests, coverage.CachedInputRequests, coverage.UncachedInputRequests,
                coverage.ReasoningRequests, coverage.CostRequests, coverage.CacheRequests }.Any(count => count < 0 || count > requests)
            || coverage.InputRequests == 0 && usage?.InputTokens is not null
            || coverage.OutputRequests == 0 && usage?.OutputTokens is not null
            || coverage.CachedInputRequests == 0 && usage?.CachedInputTokens is not null
            || coverage.UncachedInputRequests == 0 && usage?.UncachedInputTokens is not null
            || coverage.ReasoningRequests == 0 && usage?.ReasoningTokens is not null)
            throw new ArgumentException("Invalid usage reporting population.", nameof(coverage));
        if (requests > 0 && usage is not null)
        {
            if (coverage.InputRequests == requests && coverage.CachedInputRequests == requests && coverage.UncachedInputRequests == requests
                && usage.InputTokens is { } total && usage.CachedInputTokens is { } hit && usage.UncachedInputTokens is { } miss
                && (long)hit + miss != total)
                throw new ArgumentException("Complete input partitions do not match total input.", nameof(coverage));
            if (coverage.OutputRequests == requests && coverage.ReasoningRequests == requests
                && usage.OutputTokens is { } output && usage.ReasoningTokens > output)
                throw new ArgumentException("Reasoning is an output subset.", nameof(coverage));
        }
    }

    private static ImmutableArray<string> ParseIdentifierArray(JsonElement element, string pointer, int maximum)
    {
        var values = ParseOrderedStrings(element, pointer, 0, maximum, DocumentationScribeContract.MaximumIdentifierScalars);
        if (values.Any(value => !IsIdentifier(value, false))) throw Fail("invalid-vocabulary", pointer);
        return values;
    }

    private static ImmutableArray<byte> NormalizeSubmissionJson(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) Write(value, writer);
        return buffer.WrittenSpan.ToArray().ToImmutableArray();
        static void Write(JsonElement element, Utf8JsonWriter writer)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Write(property.Value, writer); }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            { writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(item, writer); writer.WriteEndArray(); }
            else element.WriteTo(writer);
        }
    }

    private static DocumentationScribeResultParseResult ParseIncrementalRunResult(DocumentationScribeRequest request,
        DocumentationScribeAttemptId attemptId, ImmutableArray<DocumentationScribeEvidenceReference> expectedDynamic,
        ReadOnlyMemory<byte> bytes)
    {
        if (expectedDynamic.IsDefault) throw new ArgumentException("Trusted evidence must be initialized.", nameof(expectedDynamic));
        var raw = TryParseArtifact(bytes, "scribe.result", "scribeRunResultVersion", out var document);
        if (raw is not null) return new(null, new(raw.Code, raw.Pointer));
        using (document)
        {
            try
            {
                var root = document!.RootElement;
                ExpectProperties(root, "", ["scribeRunResultVersion", "scribeRequestSha256", "attemptId",
                    "dynamicEvidenceReferences", "submissions", "targetFailures", "stop", "repairCount", "nextSequence", "coverage", "runEnvelope"]);
                var hash = ReadSha256(root, "scribeRequestSha256", "");
                if (hash != request.ArtifactSha256) throw Fail("invalid-correlation", "/scribeRequestSha256");
                var parsedAttempt = ParseAttemptId(ReadString(root, "attemptId", "", 64), "/attemptId");
                if (parsedAttempt != attemptId) throw Fail("invalid-correlation", "/attemptId");
                var overlay = ParseEvidenceReferences(root.GetProperty("dynamicEvidenceReferences"), "/dynamicEvidenceReferences",
                    request.Context.RepositoryContextRef, request.Manifest);
                ValidateDynamicEvidenceOverlay(request, overlay, expectedDynamic);
                var items = root.GetProperty("submissions");
                ExpectArray(items, "/submissions", 0, request.ExecutableTargetIds.Length);
                var submissions = ImmutableArray.CreateBuilder<DocumentationScribeTargetSubmission>();
                var index = 0;
                foreach (var item in items.EnumerateArray())
                {
                    var pointer = "/submissions/" + index++;
                    ExpectProperties(item, pointer, ["targetId", "contentSha256", "stage", "result"]);
                    var id = ReadIdentifier(item, "targetId", pointer);
                    var parsed = ValidateTargetSubmission(request, id, overlay, StrictUtf8.GetBytes(item.GetProperty("result").GetRawText()));
                    if (parsed.Submission is not { } validation)
                    {
                        var failure = parsed.Failure!;
                        throw Fail(failure.Code["scribe.result.".Length..], pointer + (failure.Pointer == "/targetId"
                            ? "/targetId" : "/result" + failure.Pointer));
                    }
                    if (validation.ContentSha256 != ReadSha256(item, "contentSha256", pointer)) throw Fail("invalid-correlation", pointer + "/contentSha256");
                    submissions.Add(AcknowledgeSubmission(validation, new(hash, id, validation.ContentSha256,
                        ParseStage(item.GetProperty("stage"), pointer + "/stage"))));
                }
                var failuresElement = root.GetProperty("targetFailures");
                ExpectArray(failuresElement, "/targetFailures", 0, request.ExecutableTargetIds.Length);
                var failures = ImmutableArray.CreateBuilder<DocumentationScribeTargetFailure>();
                foreach (var item in failuresElement.EnumerateArray())
                { ExpectProperties(item, "/targetFailures", ["targetId", "validationCode"]); failures.Add(new(ReadIdentifier(item, "targetId", "/targetFailures"), ReadIdentifier(item, "validationCode", "/targetFailures"))); }
                if (submissions.Count == 0 && !overlay.IsEmpty) throw Fail("invalid-evidence", "/dynamicEvidenceReferences");
                var stopElement = root.GetProperty("stop");
                DocumentationScribeTerminal stop;
                if (stopElement.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "finished")
                {
                    ExpectProperties(stopElement, "/stop", ["kind", "reason"]);
                    stop = new DocumentationScribeFinishedTerminal(ReadString(stopElement, "reason", "/stop", 32) switch
                    { "completed" => DocumentationScribeFinishReason.Completed, "unresolved" => DocumentationScribeFinishReason.Unresolved, "context" => DocumentationScribeFinishReason.Context, _ => throw Fail("invalid-vocabulary", "/stop/reason") });
                }
                else stop = ParseTerminal(stopElement, "/stop", request);
                var envelope = ParseRunEnvelope(root.GetProperty("runEnvelope"), "/runEnvelope", request, attemptId, stop);
                ValidateProviderFailureMatrixForParse(request, stop, envelope);
                var result = new DocumentationScribeRunResult(hash, attemptId, overlay, stop, envelope, submissions.ToImmutable(),
                    failures.ToImmutable(), ReadBoundedInt(root, "repairCount", "", 0, MaximumInvalidSubmissions),
                    ReadBoundedInt(root, "nextSequence", "", 1, int.MaxValue));
                ValidateIncrementalResult(request, attemptId, result);
                var coverage = root.GetProperty("coverage");
                var expected = GetCoverage(request, result);
                ExpectArray(coverage, "/coverage", expected.Length, expected.Length);
                index = 0;
                foreach (var row in coverage.EnumerateArray())
                {
                    var actual = expected[index++];
                    ExpectProperties(row, "/coverage", ["targetId", "auditOutcome", "status"], ["stage"]);
                    if (ReadIdentifier(row, "targetId", "/coverage") != actual.TargetId
                        || ReadString(row, "auditOutcome", "/coverage", 32) != AuditVocabulary.GetId(actual.AuditOutcome)
                        || ReadString(row, "status", "/coverage", 32) != actual.Status
                        || row.TryGetProperty("stage", out var stage) != actual.Stage.HasValue
                        || actual.Stage.HasValue && ParseStage(stage, "/coverage/stage") != actual.Stage.Value)
                        throw Fail("invalid-correlation", "/coverage");
                }
                return new(result, null);
            }
            catch (ContractFailure failure) { return new(null, new("scribe.result." + failure.Category, failure.Pointer)); }
            catch (ArgumentException) { return new(null, new("scribe.result.invalid-correlation", null)); }
        }
    }
}
