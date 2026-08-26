using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContractScribe.Core;

public static class CampaignStateJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static CampaignCheckpointArtifact CreateArtifact(CampaignCheckpointState state)
    {
        var bytes = Write(state);
        return new CampaignCheckpointArtifact(
            state,
            bytes,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    public static byte[] Write(CampaignCheckpointState state)
    {
        CampaignStateFactory.Validate(state);
        using var stream = new BoundedMemoryStream(CampaignStateContract.MaximumArtifactUtf8Bytes);
        WriteValidatedState(stream, state);
        return stream.ToArray();
    }

    internal static void ValidateEncodedSize(CampaignCheckpointState state)
    {
        using var stream = new BoundedMemoryStream(CampaignStateContract.MaximumArtifactUtf8Bytes);
        WriteValidatedState(stream, state);
    }

    private static void WriteValidatedState(Stream stream, CampaignCheckpointState state)
    {
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            }))
        {
            WriteState(writer, state);
        }

        stream.WriteByte((byte)'\n');
    }

    public static CampaignCheckpointParseResult Parse(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            if (utf8Json.Length > CampaignStateContract.MaximumArtifactUtf8Bytes)
            {
                return Invalid(CampaignStateValidationCode.DocumentTooLarge);
            }

            if (utf8Json.Length >= 3
                && utf8Json.Span[..3].SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }))
            {
                return Invalid(CampaignStateValidationCode.BomNotAllowed);
            }

            try
            {
                _ = StrictUtf8.GetString(utf8Json.Span);
            }
            catch (DecoderFallbackException)
            {
                return Invalid(CampaignStateValidationCode.InvalidUtf8);
            }

            if (HasDuplicateProperty(utf8Json.Span))
            {
                return Invalid(CampaignStateValidationCode.DuplicateProperty);
            }

            using var document = JsonDocument.Parse(
                utf8Json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = CampaignStateContract.MaximumJsonDepth,
                });
            var state = ParseState(document.RootElement);
            CampaignStateFactory.Validate(state);
            var canonical = Write(state);
            if (!utf8Json.Span.SequenceEqual(canonical))
            {
                return Invalid(CampaignStateValidationCode.InvalidCanonicalBytes);
            }

            return new CampaignCheckpointParseResult(
                new CampaignCheckpointArtifact(
                    state,
                    canonical,
                    Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant()),
                null);
        }
        catch (CampaignStateValidationException failure)
        {
            return Invalid(failure.Code);
        }
        catch (JsonException)
        {
            return Invalid(CampaignStateValidationCode.InvalidJson);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            return Invalid(CampaignStateValidationCode.InvalidShape);
        }
    }

    internal static byte[] WritePatchRequest(
        DocumentationPatchContext context,
        ImmutableArray<string> provenanceCatalog,
        ImmutableArray<DocumentationPatchBlockRequest> blocks)
    {
        using var stream = new BoundedMemoryStream(
            DocumentationPatchValidator.MaximumArtifactUtf8Bytes,
            CampaignStateValidationCode.InvalidBound,
            "Active patch request exceeds the M2 byte bound.");
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("patchRequestVersion", 1);
            writer.WritePropertyName("context");
            writer.WriteStartObject();
            writer.WriteString("repositoryContextRef", context.RepositoryContextRef.Value);
            writer.WriteString("inputIdentity", context.InputIdentity);
            writer.WriteString("targetProfile", ClassificationVocabulary.GetId(context.TargetProfile));
            writer.WriteEndObject();
            writer.WritePropertyName("provenanceCatalog");
            WriteStrings(writer, provenanceCatalog);
            writer.WritePropertyName("blocks");
            writer.WriteStartArray();
            foreach (var block in blocks)
            {
                WritePatchBlock(writer, block);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void WriteState(Utf8JsonWriter writer, CampaignCheckpointState state)
    {
        writer.WriteStartObject();
        writer.WriteNumber("campaignStateVersion", CampaignStateContract.Version);
        WriteProduct(writer, "productRevision", state.ProductRevision);
        writer.WriteString("campaignLineage", state.CampaignLineage);
        WriteSnapshot(writer, "snapshot", state.Snapshot);
        writer.WriteNumber("checkpointRevision", state.CheckpointRevision);
        WriteCeilings(writer, state.ConfiguredCeilings);
        WriteCharges(writer, state.LineageCharges);
        writer.WritePropertyName("workItems");
        writer.WriteStartArray();
        foreach (var work in state.WorkItems)
        {
            WriteWork(writer, work);
        }

        writer.WriteEndArray();
        writer.WritePropertyName("activeReservation");
        WriteReservation(writer, state.ActiveReservation);
        writer.WritePropertyName("candidateObservation");
        WriteCandidate(writer, state.CandidateObservation);
        writer.WritePropertyName("cumulativeOutcome");
        WriteCumulativeOutcome(writer, state.CumulativeOutcome);
        writer.WritePropertyName("terminalOutcome");
        WriteTerminal(writer, state.TerminalOutcome);
        writer.WritePropertyName("predecessor");
        WritePredecessor(writer, state.Predecessor);
        writer.WriteEndObject();
    }

    private static void WriteProduct(
        Utf8JsonWriter writer,
        string propertyName,
        CampaignStateProductRevision product)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WriteString("id", product.Id);
        writer.WriteString("contentSha256", product.ContentSha256);
        writer.WriteEndObject();
    }

    private static void WriteSnapshot(
        Utf8JsonWriter writer,
        string propertyName,
        CampaignStateSnapshotAuthority snapshot)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WriteString("opaqueSnapshotBinding", snapshot.OpaqueSnapshotBinding);
        writer.WriteString("repositoryCommitmentSha256", snapshot.RepositoryCommitmentSha256);
        writer.WriteString("inputCommitmentSha256", snapshot.InputCommitmentSha256);
        writer.WriteString("inputIdentityCommitmentSha256", snapshot.InputIdentityCommitmentSha256);
        writer.WriteString("policyAuthorityCommitmentSha256", snapshot.PolicyAuthorityCommitmentSha256);
        writer.WriteString("targetProfile", ClassificationVocabulary.GetId(snapshot.TargetProfile));
        writer.WriteString("executionCommitmentSha256", snapshot.ExecutionCommitmentSha256);
        writer.WriteEndObject();
    }

    private static void WriteCeilings(
        Utf8JsonWriter writer,
        CampaignStateConfiguredCeilings ceilings)
    {
        writer.WritePropertyName("configuredCeilings");
        writer.WriteStartObject();
        writer.WritePropertyName("campaignBudget");
        writer.WriteStartObject();
        var budget = ceilings.CampaignBudget;
        writer.WriteNumber("maximumBlocks", budget.MaximumBlocks);
        writer.WriteNumber("maximumChangedFiles", budget.MaximumChangedFiles);
        writer.WriteNumber("maximumPatchBytes", budget.MaximumPatchBytes);
        writer.WriteNumber("maximumProviderRequests", budget.MaximumProviderRequests);
        writer.WriteNumber("maximumAttemptsPerTarget", budget.MaximumAttemptsPerTarget);
        writer.WriteNumber("maximumInputTokens", budget.MaximumInputTokens);
        writer.WriteNumber("maximumUncachedInputTokens", budget.MaximumUncachedInputTokens);
        writer.WriteNumber("maximumOutputTokens", budget.MaximumOutputTokens);
        writer.WriteNumber("maximumCostMicrounits", budget.MaximumCostMicrounits);
        writer.WriteNumber("maximumElapsedMilliseconds", budget.MaximumElapsedMilliseconds);
        writer.WriteNumber("maximumCandidatesPerBlock", budget.MaximumCandidatesPerBlock);
        writer.WriteBoolean("costEnforced", budget.CostEnforced);
        WriteNullableString(writer, "costCurrency", budget.CostCurrency);
        WriteNullableString(writer, "costRatePolicyId", budget.CostRatePolicyId);
        WriteNullableString(writer, "costRatePolicySha256", budget.CostRatePolicySha256);
        writer.WriteEndObject();
        writer.WritePropertyName("scribeRunLimits");
        writer.WriteStartObject();
        var limits = ceilings.ScribeRunLimits;
        writer.WriteNumber("maximumContextReferences", limits.MaximumContextReferences);
        writer.WriteNumber("maximumContextUtf8Bytes", limits.MaximumContextUtf8Bytes);
        writer.WriteNumber("maximumEvidenceReferences", limits.MaximumEvidenceReferences);
        writer.WriteNumber("maximumEvidenceUtf8Bytes", limits.MaximumEvidenceUtf8Bytes);
        writer.WriteNumber("maximumProviderRequests", limits.MaximumProviderRequests);
        writer.WriteNumber("maximumToolRounds", limits.MaximumToolRounds);
        writer.WriteNumber("maximumToolCalls", limits.MaximumToolCalls);
        writer.WriteNumber("maximumAttempts", limits.MaximumAttempts);
        writer.WriteNumber("maximumInputTokens", limits.MaximumInputTokens);
        writer.WriteNumber("maximumUncachedInputTokens", limits.MaximumUncachedInputTokens);
        writer.WriteNumber("maximumOutputTokens", limits.MaximumOutputTokens);
        writer.WriteNumber("maximumCostMicrounits", limits.MaximumCostMicrounits);
        writer.WriteNumber("maximumElapsedMilliseconds", limits.MaximumElapsedMilliseconds);
        writer.WriteEndObject();
        writer.WritePropertyName("styleConfigurationAuthority");
        writer.WriteStartObject();
        writer.WriteString("id", ceilings.StyleConfigurationAuthority.Id);
        writer.WriteString("contentSha256", ceilings.StyleConfigurationAuthority.ContentSha256);
        writer.WriteEndObject();
        writer.WriteString(
            "campaignConfigurationCommitmentSha256",
            ceilings.CampaignConfigurationCommitmentSha256);
        writer.WriteEndObject();
    }

    private static void WriteCharges(Utf8JsonWriter writer, CampaignLineageCharges charges)
    {
        writer.WritePropertyName("lineageCharges");
        writer.WriteStartObject();
        writer.WriteNumber("outerInvocations", charges.OuterInvocations);
        WriteCharge(writer, "providerRequests", charges.ProviderRequests);
        WriteCharge(writer, "inputTokens", charges.InputTokens);
        WriteCharge(writer, "cachedInputTokens", charges.CachedInputTokens);
        WriteCharge(writer, "uncachedInputTokens", charges.UncachedInputTokens);
        WriteCharge(writer, "outputTokens", charges.OutputTokens);
        WriteCharge(writer, "reasoningTokens", charges.ReasoningTokens);
        WriteCharge(writer, "costMicrounits", charges.CostMicrounits);
        WriteCharge(writer, "activeElapsedMilliseconds", charges.ActiveElapsedMilliseconds);
        writer.WriteNumber("patchValidationInvocations", charges.PatchValidationInvocations);
        writer.WriteEndObject();
    }

    private static void WriteCharge(
        Utf8JsonWriter writer,
        string propertyName,
        CampaignChargeObservation charge)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        if (charge.Observed is { } observed)
        {
            writer.WriteNumber("observed", observed);
        }
        else
        {
            writer.WriteNull("observed");
        }

        writer.WriteNumber("conservativeUnobserved", charge.ConservativeUnobserved);
        writer.WriteNumber("totalCharged", charge.TotalCharged);
        writer.WriteEndObject();
    }

    private static void WriteWork(Utf8JsonWriter writer, CampaignWorkItemState work)
    {
        writer.WriteStartObject();
        writer.WriteString("workItemKey", work.WorkItemKey);
        writer.WriteNumber("outerAttemptCount", work.OuterAttemptCount);
        writer.WriteNumber("candidateAttemptCount", work.CandidateAttemptCount);
        writer.WriteString("status", WorkStatusId(work.Status));
        writer.WritePropertyName("trustedProposal");
        WriteProposal(writer, work.TrustedProposal);
        writer.WritePropertyName("closedOutcome");
        WriteClosedOutcome(writer, work.ClosedOutcome);
        writer.WriteEndObject();
    }

    private static void WriteProposal(Utf8JsonWriter writer, CampaignTrustedProposal? proposal)
    {
        if (proposal is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("historicalScribeRequestSha256", proposal.HistoricalScribeRequestSha256);
        writer.WriteString("historicalAttemptId", proposal.HistoricalAttemptId.Value);
        writer.WriteString("providerConfigurationId", proposal.ProviderConfigurationId);
        writer.WriteString("modelConfigurationId", proposal.ModelConfigurationId);
        writer.WriteString("scribeProtocolId", proposal.ScribeProtocolId);
        writer.WritePropertyName("patchBlock");
        WritePatchBlock(writer, proposal.PatchBlock);
        writer.WritePropertyName("evidence");
        writer.WriteStartArray();
        foreach (var evidence in proposal.Evidence)
        {
            WriteEvidence(writer, evidence);
        }

        writer.WriteEndArray();
        writer.WriteString("styleProfileCommitmentSha256", proposal.StyleProfileCommitmentSha256);
        writer.WriteString("toolPolicyId", proposal.ToolPolicyId);
        writer.WriteString("proposalCommitmentSha256", proposal.ProposalCommitmentSha256);
        writer.WriteEndObject();
    }

    private static void WritePatchBlock(
        Utf8JsonWriter writer,
        DocumentationPatchBlockRequest block)
    {
        writer.WriteStartObject();
        writer.WriteString("blockId", block.BlockId);
        WriteSymbol(writer, "symbolRef", block.SymbolRef);
        writer.WritePropertyName("locator");
        WritePatchLocator(writer, block.Locator);
        writer.WriteString("editKind", block.EditKind == DocumentationPatchEditKind.Insert ? "insert" : "replace");
        writer.WritePropertyName("applicableComponents");
        writer.WriteStartArray();
        foreach (var component in block.ApplicableComponents)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", PatchComponentKindId(component.Kind));
            writer.WriteString("identity", component.Identity);
            if (component.Kind is DocumentationPatchComponentKind.TypeParameter
                or DocumentationPatchComponentKind.Parameter)
            {
                writer.WriteString("name", component.Name);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("content");
        WritePatchContent(writer, block.Content);
        writer.WritePropertyName("provenanceRefs");
        WriteStrings(writer, block.ProvenanceRefs);
        writer.WriteEndObject();
    }

    private static void WritePatchLocator(
        Utf8JsonWriter writer,
        DocumentationPatchSourceLocator locator)
    {
        writer.WriteStartObject();
        switch (locator)
        {
            case DocumentationPatchRepositoryLocator repository:
                writer.WriteString("kind", "repository");
                writer.WriteString("path", repository.Path);
                writer.WriteString("originalFileSha256", repository.OriginalFileSha256);
                writer.WriteString("encoding", EncodingId(repository.Encoding));
                WriteSpan(writer, "declarationSpan", repository.DeclarationSpan);
                break;
            case DocumentationPatchSourceGeneratorLocator generated:
                writer.WriteString("kind", "sourceGenerator");
                WriteGeneratedPatchLocator(writer, generated);
                break;
            case DocumentationPatchToolGeneratedLocator generated:
                writer.WriteString("kind", "toolGenerated");
                WriteGeneratedPatchLocator(writer, generated);
                break;
            default:
                throw CampaignStateFactory.Fail(
                    CampaignStateValidationCode.InvalidVocabulary,
                    "Unknown patch locator kind.");
        }

        writer.WriteEndObject();
    }

    private static void WriteGeneratedPatchLocator(
        Utf8JsonWriter writer,
        DocumentationPatchGeneratedLocator locator)
    {
        writer.WriteString("producerId", locator.ProducerId);
        writer.WriteString("outputId", locator.OutputId);
        writer.WriteString("sourceSha256", locator.SourceSha256);
        WriteSpan(writer, "declarationSpan", locator.DeclarationSpan);
    }

    private static void WritePatchContent(Utf8JsonWriter writer, DocumentationPatchContent content)
    {
        writer.WriteStartObject();
        switch (content)
        {
            case DocumentationPatchInheritDocContent:
                writer.WriteString("kind", "inheritDoc");
                break;
            case DocumentationPatchStructuredContent structured:
                writer.WriteString("kind", "structured");
                writer.WritePropertyName("summaryLines");
                WriteStrings(writer, structured.SummaryLines);
                writer.WritePropertyName("typeParameters");
                writer.WriteStartArray();
                foreach (var item in structured.TypeParameters)
                {
                    WriteNamedContent(writer, item);
                }

                writer.WriteEndArray();
                writer.WritePropertyName("parameters");
                writer.WriteStartArray();
                foreach (var item in structured.Parameters)
                {
                    WriteNamedContent(writer, item);
                }

                writer.WriteEndArray();
                writer.WritePropertyName("return");
                WriteComponentContent(writer, structured.Return);
                writer.WritePropertyName("value");
                WriteComponentContent(writer, structured.Value);
                writer.WritePropertyName("exceptions");
                writer.WriteStartArray();
                foreach (var item in structured.Exceptions)
                {
                    writer.WriteStartObject();
                    writer.WriteString("typeDocumentationId", item.TypeDocumentationId);
                    writer.WritePropertyName("lines");
                    WriteStrings(writer, item.Lines);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WritePropertyName("remarksLines");
                if (structured.RemarksLines is { } remarks)
                {
                    WriteStrings(writer, remarks);
                }
                else
                {
                    writer.WriteNullValue();
                }

                break;
            default:
                throw CampaignStateFactory.Fail(
                    CampaignStateValidationCode.InvalidVocabulary,
                    "Unknown patch content kind.");
        }

        writer.WriteEndObject();
    }

    private static void WriteNamedContent(
        Utf8JsonWriter writer,
        DocumentationPatchNamedContent content)
    {
        writer.WriteStartObject();
        writer.WriteString("componentIdentity", content.ComponentIdentity);
        writer.WriteString("name", content.Name);
        writer.WritePropertyName("lines");
        WriteStrings(writer, content.Lines);
        writer.WriteEndObject();
    }

    private static void WriteComponentContent(
        Utf8JsonWriter writer,
        DocumentationPatchComponentContent? content)
    {
        if (content is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("componentIdentity", content.ComponentIdentity);
        writer.WritePropertyName("lines");
        WriteStrings(writer, content.Lines);
        writer.WriteEndObject();
    }

    private static void WriteEvidence(Utf8JsonWriter writer, CampaignEvidenceProjection evidence)
    {
        writer.WriteStartObject();
        writer.WriteString("evidenceReferenceId", evidence.EvidenceReferenceId);
        writer.WritePropertyName("subject");
        WriteEvidenceSubject(writer, evidence.Subject);
        writer.WriteString("kind", EvidenceVocabulary.GetId(evidence.Kind));
        writer.WriteString("relation", EvidenceVocabulary.GetId(evidence.Relation));
        writer.WriteString("authority", DocumentationScribeVocabulary.GetId(evidence.Authority));
        writer.WritePropertyName("locator");
        WriteEvidenceLocator(writer, evidence.Locator);
        writer.WriteString("contentSha256", evidence.ContentSha256);
        writer.WriteNumber("originalUtf8ByteCount", evidence.OriginalUtf8ByteCount);
        writer.WriteNumber("includedUtf8ByteCount", evidence.IncludedUtf8ByteCount);
        writer.WriteBoolean("isTruncated", evidence.IsTruncated);
        writer.WritePropertyName("claimCategoryIds");
        WriteStrings(writer, evidence.ClaimCategoryIds);
        writer.WriteEndObject();
    }

    private static void WriteEvidenceSubject(Utf8JsonWriter writer, EvidenceSubject subject)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", subject is TargetEvidenceSubject ? "target" : "component");
        WriteSymbol(writer, "parentSymbolRef", subject.ParentSymbolRef);
        if (subject is ComponentEvidenceSubject component)
        {
            writer.WriteString("componentKind", ComponentKindId(component.ComponentKind));
            writer.WriteString("identity", component.Identity);
        }
        else
        {
            writer.WriteNull("componentKind");
            writer.WriteNull("identity");
        }

        writer.WriteEndObject();
    }

    private static void WriteEvidenceLocator(Utf8JsonWriter writer, EvidenceLocator locator)
    {
        writer.WriteStartObject();
        switch (locator)
        {
            case RepositoryEvidenceLocator repository:
                writer.WriteString("kind", "repository");
                writer.WriteString("path", repository.Path);
                writer.WritePropertyName("span");
                WriteNullableSpan(writer, repository.Span);
                break;
            case MetadataEvidenceLocator metadata:
                writer.WriteString("kind", "metadata");
                writer.WriteString("assemblyIdentity", metadata.AssemblyIdentity);
                writer.WriteString("documentationCommentId", metadata.DocumentationCommentId);
                break;
            case GeneratedOutputEvidenceLocator generated:
                writer.WriteString("kind", "generated");
                writer.WriteString("producerKind", generated.ProducerKind switch
                {
                    GeneratedOutputKind.SourceGenerator => "sourceGenerator",
                    GeneratedOutputKind.ToolGenerated => "toolGenerated",
                    _ => throw Vocabulary(),
                });
                writer.WriteString("producerId", generated.ProducerId);
                writer.WriteString("outputId", generated.OutputId);
                writer.WriteString("sourceSha256", generated.SourceSha256);
                writer.WritePropertyName("span");
                WriteNullableSpan(writer, generated.Span);
                break;
            case SyntheticEvidenceLocator synthetic:
                writer.WriteString("kind", "synthetic");
                writer.WriteString("fixtureId", synthetic.FixtureId);
                break;
            default:
                throw CampaignStateFactory.Fail(
                    CampaignStateValidationCode.InvalidVocabulary,
                    "Unknown evidence locator kind.");
        }

        writer.WriteEndObject();
    }

    private static void WriteClosedOutcome(
        Utf8JsonWriter writer,
        CampaignWorkClosedOutcome? outcome)
    {
        if (outcome is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("stage", outcome.Stage switch
        {
            CampaignWorkOutcomeStage.Planning => "planning",
            CampaignWorkOutcomeStage.Scribe => "scribe",
            CampaignWorkOutcomeStage.Patch => "patch",
            _ => throw Vocabulary(),
        });
        writer.WriteString("code", WorkOutcomeCodeId(outcome.Code));
        WriteNullableString(
            writer,
            "providerDisposition",
            outcome.ProviderDisposition switch
            {
                CampaignProviderFinalDisposition.Retryable => "retryable",
                CampaignProviderFinalDisposition.Terminal => "terminal",
                null => null,
                _ => throw Vocabulary(),
            });
        WriteNullableString(writer, "scribeRequestSha256", outcome.ScribeRequestSha256);
        WriteNullableString(
            writer,
            "attemptId",
            outcome.AttemptId is { } attempt ? attempt.Value : null);
        WriteNullableString(writer, "patchRequestSha256", outcome.PatchRequestSha256);
        WriteNullableString(writer, "patchResultCommitmentSha256", outcome.PatchResultCommitmentSha256);
        writer.WriteEndObject();
    }

    private static void WriteReservation(Utf8JsonWriter writer, CampaignActiveReservation? reservation)
    {
        switch (reservation)
        {
            case null:
                writer.WriteNullValue();
                break;
            case CampaignProviderReservation provider:
                writer.WriteStartObject();
                writer.WriteString("kind", "provider");
                writer.WriteString("workItemKey", provider.WorkItemKey);
                writer.WriteString("scribeRequestSha256", provider.ScribeRequestSha256);
                writer.WriteString("attemptId", provider.AttemptId.Value);
                writer.WritePropertyName("exposure");
                writer.WriteStartObject();
                writer.WriteNumber("providerRequests", provider.Exposure.ProviderRequests);
                writer.WriteNumber("inputTokens", provider.Exposure.InputTokens);
                writer.WriteNumber("uncachedInputTokens", provider.Exposure.UncachedInputTokens);
                writer.WriteNumber("outputTokens", provider.Exposure.OutputTokens);
                writer.WriteNumber("costMicrounits", provider.Exposure.CostMicrounits);
                writer.WriteNumber("elapsedMilliseconds", provider.Exposure.ElapsedMilliseconds);
                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
            case CampaignPatchReservation patch:
                writer.WriteStartObject();
                writer.WriteString("kind", "patch");
                writer.WriteString("patchRequestSha256", patch.PatchRequestSha256);
                writer.WriteNumber("expectedCheckpointRevision", patch.ExpectedCheckpointRevision);
                writer.WriteNumber("patchAttemptCount", patch.PatchAttemptCount);
                writer.WriteNumber("elapsedMilliseconds", patch.ElapsedMilliseconds);
                writer.WriteEndObject();
                break;
            default:
                throw CampaignStateFactory.Fail(
                    CampaignStateValidationCode.InvalidVocabulary,
                    "Unknown reservation kind.");
        }
    }

    private static void WriteCandidate(
        Utf8JsonWriter writer,
        CampaignCandidateObservation? candidate)
    {
        if (candidate is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("acceptedWorkItemKeys");
        WriteStrings(writer, candidate.AcceptedWorkItemKeys);
        writer.WriteString(
            "acceptedProjectionCommitmentSha256",
            candidate.AcceptedProjectionCommitmentSha256);
        writer.WritePropertyName("changedFiles");
        writer.WriteStartArray();
        foreach (var file in candidate.ChangedFiles)
        {
            writer.WriteStartObject();
            writer.WriteString("path", file.Path);
            writer.WriteString("originalFileSha256", file.OriginalFileSha256);
            writer.WriteString("candidateFileSha256", file.CandidateFileSha256);
            writer.WriteNumber("changedDocumentationBlockCount", file.ChangedDocumentationBlockCount);
            writer.WriteNumber("originalDocumentationByteCount", file.OriginalDocumentationByteCount);
            writer.WriteNumber("candidateDocumentationByteCount", file.CandidateDocumentationByteCount);
            writer.WriteNumber("originalDocumentationLineCount", file.OriginalDocumentationLineCount);
            writer.WriteNumber("candidateDocumentationLineCount", file.CandidateDocumentationLineCount);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteString("patchRequestSha256", candidate.PatchRequestSha256);
        writer.WriteString("patchResultCommitmentSha256", candidate.PatchResultCommitmentSha256);
        writer.WriteEndObject();
    }

    private static void WriteCumulativeOutcome(
        Utf8JsonWriter writer,
        CampaignCumulativeOutcome? outcome)
    {
        if (outcome is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("kind", CumulativeOutcomeId(outcome.Kind));
        writer.WriteString("patchRequestSha256", outcome.PatchRequestSha256);
        WriteNullableString(writer, "patchResultCommitmentSha256", outcome.PatchResultCommitmentSha256);
        writer.WriteNumber("completedFromCheckpointRevision", outcome.CompletedFromCheckpointRevision);
        writer.WriteEndObject();
    }

    private static void WriteTerminal(
        Utf8JsonWriter writer,
        CampaignTerminalOutcome? outcome)
    {
        if (outcome is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("kind", TerminalKindId(outcome.Kind));
        writer.WriteString("reason", TerminalReasonId(outcome.Reason));
        writer.WriteEndObject();
    }

    private static void WritePredecessor(
        Utf8JsonWriter writer,
        CampaignPredecessorSummary? predecessor)
    {
        if (predecessor is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        WriteProduct(writer, "productRevision", predecessor.ProductRevision);
        WriteSnapshot(writer, "snapshot", predecessor.Snapshot);
        writer.WriteString(
            "campaignConfigurationCommitmentSha256",
            predecessor.CampaignConfigurationCommitmentSha256);
        writer.WriteNumber("finalCheckpointRevision", predecessor.FinalCheckpointRevision);
        writer.WriteString("finalCheckpointSha256", predecessor.FinalCheckpointSha256);
        writer.WriteString("terminalKind", TerminalKindId(predecessor.TerminalKind));
        writer.WritePropertyName("reservation");
        if (predecessor.Reservation is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("kind", predecessor.Reservation.Kind);
            writer.WriteString("correlationSha256", predecessor.Reservation.CorrelationSha256);
            writer.WriteNumber("conservativeCharge", predecessor.Reservation.ConservativeCharge);
            writer.WriteEndObject();
        }

        writer.WritePropertyName("candidate");
        writer.WriteStartObject();
        writer.WriteNumber("acceptedCount", predecessor.Candidate.AcceptedCount);
        writer.WriteNumber("distinctFileCount", predecessor.Candidate.DistinctFileCount);
        writer.WriteNumber(
            "originalDocumentationByteCount",
            predecessor.Candidate.OriginalDocumentationByteCount);
        writer.WriteNumber(
            "candidateDocumentationByteCount",
            predecessor.Candidate.CandidateDocumentationByteCount);
        writer.WriteNumber(
            "originalDocumentationLineCount",
            predecessor.Candidate.OriginalDocumentationLineCount);
        writer.WriteNumber(
            "candidateDocumentationLineCount",
            predecessor.Candidate.CandidateDocumentationLineCount);
        WriteNullableString(writer, "patchRequestSha256", predecessor.Candidate.PatchRequestSha256);
        WriteNullableString(
            writer,
            "patchResultCommitmentSha256",
            predecessor.Candidate.PatchResultCommitmentSha256);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static CampaignCheckpointState ParseState(JsonElement root)
    {
        ExpectObject(
            root,
            "campaignStateVersion",
            "productRevision",
            "campaignLineage",
            "snapshot",
            "checkpointRevision",
            "configuredCeilings",
            "lineageCharges",
            "workItems",
            "activeReservation",
            "candidateObservation",
            "cumulativeOutcome",
            "terminalOutcome",
            "predecessor");
        if (ReadInt32(root, "campaignStateVersion") != CampaignStateContract.Version)
        {
            throw CampaignStateFactory.Fail(
                CampaignStateValidationCode.UnsupportedVersion,
                "Campaign checkpoint version is unsupported.");
        }

        return new CampaignCheckpointState(
            ParseProduct(root.GetProperty("productRevision")),
            ReadString(root, "campaignLineage"),
            ParseSnapshot(root.GetProperty("snapshot")),
            ReadInt64(root, "checkpointRevision"),
            ParseCeilings(root.GetProperty("configuredCeilings")),
            ParseCharges(root.GetProperty("lineageCharges")),
            ParseArray(root.GetProperty("workItems"), ParseWork, CampaignStateContract.MaximumWorkItems),
            ParseReservation(root.GetProperty("activeReservation")),
            ParseCandidate(root.GetProperty("candidateObservation")),
            ParseCumulativeOutcome(root.GetProperty("cumulativeOutcome")),
            ParseTerminal(root.GetProperty("terminalOutcome")),
            ParsePredecessor(root.GetProperty("predecessor")));
    }

    private static CampaignStateProductRevision ParseProduct(JsonElement element)
    {
        ExpectObject(element, "id", "contentSha256");
        return new CampaignStateProductRevision(
            ReadString(element, "id"),
            ReadString(element, "contentSha256"));
    }

    private static CampaignStateSnapshotAuthority ParseSnapshot(JsonElement element)
    {
        ExpectObject(
            element,
            "opaqueSnapshotBinding",
            "repositoryCommitmentSha256",
            "inputCommitmentSha256",
            "inputIdentityCommitmentSha256",
            "policyAuthorityCommitmentSha256",
            "targetProfile",
            "executionCommitmentSha256");
        return new CampaignStateSnapshotAuthority(
            ReadString(element, "opaqueSnapshotBinding"),
            ReadString(element, "repositoryCommitmentSha256"),
            ReadString(element, "inputCommitmentSha256"),
            ReadString(element, "inputIdentityCommitmentSha256"),
            ReadString(element, "policyAuthorityCommitmentSha256"),
            ParseTargetProfile(ReadString(element, "targetProfile")),
            ReadString(element, "executionCommitmentSha256"));
    }

    private static CampaignStateConfiguredCeilings ParseCeilings(JsonElement element)
    {
        ExpectObject(
            element,
            "campaignBudget",
            "scribeRunLimits",
            "styleConfigurationAuthority",
            "campaignConfigurationCommitmentSha256");
        var budget = element.GetProperty("campaignBudget");
        ExpectObject(
            budget,
            "maximumBlocks",
            "maximumChangedFiles",
            "maximumPatchBytes",
            "maximumProviderRequests",
            "maximumAttemptsPerTarget",
            "maximumInputTokens",
            "maximumUncachedInputTokens",
            "maximumOutputTokens",
            "maximumCostMicrounits",
            "maximumElapsedMilliseconds",
            "maximumCandidatesPerBlock",
            "costEnforced",
            "costCurrency",
            "costRatePolicyId",
            "costRatePolicySha256");
        var limits = element.GetProperty("scribeRunLimits");
        ExpectObject(
            limits,
            "maximumContextReferences",
            "maximumContextUtf8Bytes",
            "maximumEvidenceReferences",
            "maximumEvidenceUtf8Bytes",
            "maximumProviderRequests",
            "maximumToolRounds",
            "maximumToolCalls",
            "maximumAttempts",
            "maximumInputTokens",
            "maximumUncachedInputTokens",
            "maximumOutputTokens",
            "maximumCostMicrounits",
            "maximumElapsedMilliseconds");
        var style = element.GetProperty("styleConfigurationAuthority");
        ExpectObject(style, "id", "contentSha256");
        return new CampaignStateConfiguredCeilings(
            new CampaignStateCampaignBudget(
                ReadInt32(budget, "maximumBlocks"),
                ReadInt32(budget, "maximumChangedFiles"),
                ReadInt64(budget, "maximumPatchBytes"),
                ReadInt32(budget, "maximumProviderRequests"),
                ReadInt32(budget, "maximumAttemptsPerTarget"),
                ReadInt64(budget, "maximumInputTokens"),
                ReadInt64(budget, "maximumUncachedInputTokens"),
                ReadInt64(budget, "maximumOutputTokens"),
                ReadInt64(budget, "maximumCostMicrounits"),
                ReadInt64(budget, "maximumElapsedMilliseconds"),
                ReadInt32(budget, "maximumCandidatesPerBlock"),
                ReadBoolean(budget, "costEnforced"),
                ReadNullableString(budget, "costCurrency"),
                ReadNullableString(budget, "costRatePolicyId"),
                ReadNullableString(budget, "costRatePolicySha256")),
            new CampaignStateScribeLimits(
                ReadInt32(limits, "maximumContextReferences"),
                ReadInt32(limits, "maximumContextUtf8Bytes"),
                ReadInt32(limits, "maximumEvidenceReferences"),
                ReadInt32(limits, "maximumEvidenceUtf8Bytes"),
                ReadInt32(limits, "maximumProviderRequests"),
                ReadInt32(limits, "maximumToolRounds"),
                ReadInt32(limits, "maximumToolCalls"),
                ReadInt32(limits, "maximumAttempts"),
                ReadInt32(limits, "maximumInputTokens"),
                ReadInt32(limits, "maximumUncachedInputTokens"),
                ReadInt32(limits, "maximumOutputTokens"),
                ReadInt64(limits, "maximumCostMicrounits"),
                ReadInt32(limits, "maximumElapsedMilliseconds")),
            new CampaignStyleConfigurationAuthority(
                ReadString(style, "id"),
                ReadString(style, "contentSha256")),
            ReadString(element, "campaignConfigurationCommitmentSha256"));
    }

    private static CampaignLineageCharges ParseCharges(JsonElement element)
    {
        ExpectObject(
            element,
            "outerInvocations",
            "providerRequests",
            "inputTokens",
            "cachedInputTokens",
            "uncachedInputTokens",
            "outputTokens",
            "reasoningTokens",
            "costMicrounits",
            "activeElapsedMilliseconds",
            "patchValidationInvocations");
        return new CampaignLineageCharges(
            ReadInt64(element, "outerInvocations"),
            ParseCharge(element.GetProperty("providerRequests")),
            ParseCharge(element.GetProperty("inputTokens")),
            ParseCharge(element.GetProperty("cachedInputTokens")),
            ParseCharge(element.GetProperty("uncachedInputTokens")),
            ParseCharge(element.GetProperty("outputTokens")),
            ParseCharge(element.GetProperty("reasoningTokens")),
            ParseCharge(element.GetProperty("costMicrounits")),
            ParseCharge(element.GetProperty("activeElapsedMilliseconds")),
            ReadInt64(element, "patchValidationInvocations"));
    }

    private static CampaignChargeObservation ParseCharge(JsonElement element)
    {
        ExpectObject(element, "observed", "conservativeUnobserved", "totalCharged");
        return new CampaignChargeObservation(
            ReadNullableInt64(element, "observed"),
            ReadInt64(element, "conservativeUnobserved"),
            ReadInt64(element, "totalCharged"));
    }

    private static CampaignWorkItemState ParseWork(JsonElement element)
    {
        ExpectObject(
            element,
            "workItemKey",
            "outerAttemptCount",
            "candidateAttemptCount",
            "status",
            "trustedProposal",
            "closedOutcome");
        var workItemKey = ReadString(element, "workItemKey");
        return new CampaignWorkItemState(
            workItemKey,
            ReadInt32(element, "outerAttemptCount"),
            ReadInt32(element, "candidateAttemptCount"),
            ParseWorkStatus(ReadString(element, "status")),
            ParseProposal(element.GetProperty("trustedProposal")),
            ParseClosedOutcome(element.GetProperty("closedOutcome"), workItemKey));
    }

    private static CampaignTrustedProposal? ParseProposal(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        ExpectObject(
            element,
            "historicalScribeRequestSha256",
            "historicalAttemptId",
            "providerConfigurationId",
            "modelConfigurationId",
            "scribeProtocolId",
            "patchBlock",
            "evidence",
            "styleProfileCommitmentSha256",
            "toolPolicyId",
            "proposalCommitmentSha256");
        if (!DocumentationScribeAttemptId.TryParse(
                ReadString(element, "historicalAttemptId"),
                out var attempt))
        {
            throw Vocabulary();
        }

        return new CampaignTrustedProposal(
            ReadString(element, "historicalScribeRequestSha256"),
            attempt,
            ReadString(element, "providerConfigurationId"),
            ReadString(element, "modelConfigurationId"),
            ReadString(element, "scribeProtocolId"),
            ParsePatchBlock(element.GetProperty("patchBlock")),
            ParseArray(
                element.GetProperty("evidence"),
                ParseEvidence,
                CampaignStateContract.MaximumEvidenceReferences),
            ReadString(element, "styleProfileCommitmentSha256"),
            ReadString(element, "toolPolicyId"),
            ReadString(element, "proposalCommitmentSha256"));
    }

    private static DocumentationPatchBlockRequest ParsePatchBlock(JsonElement element)
    {
        ExpectObject(
            element,
            "blockId",
            "symbolRef",
            "locator",
            "editKind",
            "applicableComponents",
            "content",
            "provenanceRefs");
        return new DocumentationPatchBlockRequest(
            ReadString(element, "blockId"),
            ParseSymbol(element.GetProperty("symbolRef")),
            ParsePatchLocator(element.GetProperty("locator")),
            ReadString(element, "editKind") switch
            {
                "insert" => DocumentationPatchEditKind.Insert,
                "replace" => DocumentationPatchEditKind.Replace,
                _ => throw Vocabulary(),
            },
            ParseArray(element.GetProperty("applicableComponents"), ParsePatchComponent, 512),
            ParsePatchContent(element.GetProperty("content")),
            ParseStrings(element.GetProperty("provenanceRefs"), 64));
    }

    private static DocumentationPatchApplicableComponent ParsePatchComponent(JsonElement element)
    {
        var kind = ParsePatchComponentKind(ReadString(element, "kind"));
        var named = kind is DocumentationPatchComponentKind.TypeParameter
            or DocumentationPatchComponentKind.Parameter;
        if (named)
        {
            ExpectObject(element, "kind", "identity", "name");
        }
        else
        {
            ExpectObject(element, "kind", "identity");
        }

        return new DocumentationPatchApplicableComponent(
            kind,
            ReadString(element, "identity"),
            named ? ReadString(element, "name") : null);
    }

    private static DocumentationPatchSourceLocator ParsePatchLocator(JsonElement element)
    {
        var kind = ReadString(element, "kind");
        return kind switch
        {
            "repository" => ParseRepositoryPatchLocator(element),
            "sourceGenerator" => ParseGeneratedPatchLocator(element, sourceGenerator: true),
            "toolGenerated" => ParseGeneratedPatchLocator(element, sourceGenerator: false),
            _ => throw Vocabulary(),
        };
    }

    private static DocumentationPatchRepositoryLocator ParseRepositoryPatchLocator(JsonElement element)
    {
        ExpectObject(element, "kind", "path", "originalFileSha256", "encoding", "declarationSpan");
        return new DocumentationPatchRepositoryLocator(
            ReadString(element, "path"),
            ReadString(element, "originalFileSha256"),
            ParseEncoding(ReadString(element, "encoding")),
            ParseSpan(element.GetProperty("declarationSpan")));
    }

    private static DocumentationPatchGeneratedLocator ParseGeneratedPatchLocator(
        JsonElement element,
        bool sourceGenerator)
    {
        ExpectObject(element, "kind", "producerId", "outputId", "sourceSha256", "declarationSpan");
        var producer = ReadString(element, "producerId");
        var output = ReadString(element, "outputId");
        var sha = ReadString(element, "sourceSha256");
        var span = ParseSpan(element.GetProperty("declarationSpan"));
        return sourceGenerator
            ? new DocumentationPatchSourceGeneratorLocator(producer, output, sha, span)
            : new DocumentationPatchToolGeneratedLocator(producer, output, sha, span);
    }

    private static DocumentationPatchContent ParsePatchContent(JsonElement element)
    {
        var kind = ReadString(element, "kind");
        if (kind == "inheritDoc")
        {
            ExpectObject(element, "kind");
            return new DocumentationPatchInheritDocContent();
        }

        if (kind != "structured")
        {
            throw Vocabulary();
        }

        ExpectObject(
            element,
            "kind",
            "summaryLines",
            "typeParameters",
            "parameters",
            "return",
            "value",
            "exceptions",
            "remarksLines");
        var remarks = element.GetProperty("remarksLines");
        return new DocumentationPatchStructuredContent(
            ParseStrings(element.GetProperty("summaryLines"), 256),
            ParseArray(element.GetProperty("typeParameters"), ParseNamedContent, 512),
            ParseArray(element.GetProperty("parameters"), ParseNamedContent, 512),
            ParseComponentContent(element.GetProperty("return")),
            ParseComponentContent(element.GetProperty("value")),
            ParseArray(element.GetProperty("exceptions"), ParseExceptionContent, 256),
            remarks.ValueKind == JsonValueKind.Null ? null : ParseStrings(remarks, 256));
    }

    private static DocumentationPatchNamedContent ParseNamedContent(JsonElement element)
    {
        ExpectObject(element, "componentIdentity", "name", "lines");
        return new DocumentationPatchNamedContent(
            ReadString(element, "componentIdentity"),
            ReadString(element, "name"),
            ParseStrings(element.GetProperty("lines"), 256));
    }

    private static DocumentationPatchComponentContent? ParseComponentContent(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        ExpectObject(element, "componentIdentity", "lines");
        return new DocumentationPatchComponentContent(
            ReadString(element, "componentIdentity"),
            ParseStrings(element.GetProperty("lines"), 256));
    }

    private static DocumentationPatchExceptionContent ParseExceptionContent(JsonElement element)
    {
        ExpectObject(element, "typeDocumentationId", "lines");
        return new DocumentationPatchExceptionContent(
            ReadString(element, "typeDocumentationId"),
            ParseStrings(element.GetProperty("lines"), 256));
    }

    private static CampaignEvidenceProjection ParseEvidence(JsonElement element)
    {
        ExpectObject(
            element,
            "evidenceReferenceId",
            "subject",
            "kind",
            "relation",
            "authority",
            "locator",
            "contentSha256",
            "originalUtf8ByteCount",
            "includedUtf8ByteCount",
            "isTruncated",
            "claimCategoryIds");
        return new CampaignEvidenceProjection(
            ReadString(element, "evidenceReferenceId"),
            ParseEvidenceSubject(element.GetProperty("subject")),
            ParseEvidenceKind(ReadString(element, "kind")),
            ParseEvidenceRelation(ReadString(element, "relation")),
            ParseEvidenceAuthority(ReadString(element, "authority")),
            ParseEvidenceLocator(element.GetProperty("locator")),
            ReadString(element, "contentSha256"),
            ReadInt32(element, "originalUtf8ByteCount"),
            ReadInt32(element, "includedUtf8ByteCount"),
            ReadBoolean(element, "isTruncated"),
            ParseStrings(element.GetProperty("claimCategoryIds"), 64));
    }

    private static EvidenceSubject ParseEvidenceSubject(JsonElement element)
    {
        ExpectObject(element, "kind", "parentSymbolRef", "componentKind", "identity");
        var symbol = ParseSymbol(element.GetProperty("parentSymbolRef"));
        return ReadString(element, "kind") switch
        {
            "target" when element.GetProperty("componentKind").ValueKind == JsonValueKind.Null
                && element.GetProperty("identity").ValueKind == JsonValueKind.Null =>
                new TargetEvidenceSubject(symbol),
            "component" => new ComponentEvidenceSubject(
                symbol,
                ParseComponentKind(ReadString(element, "componentKind")),
                ReadString(element, "identity")),
            _ => throw Vocabulary(),
        };
    }

    private static EvidenceLocator ParseEvidenceLocator(JsonElement element)
    {
        return ReadString(element, "kind") switch
        {
            "repository" => ParseRepositoryEvidenceLocator(element),
            "metadata" => ParseMetadataEvidenceLocator(element),
            "generated" => ParseGeneratedEvidenceLocator(element),
            "synthetic" => ParseSyntheticEvidenceLocator(element),
            _ => throw Vocabulary(),
        };
    }

    private static EvidenceLocator ParseRepositoryEvidenceLocator(JsonElement element)
    {
        ExpectObject(element, "kind", "path", "span");
        return new RepositoryEvidenceLocator(
            ReadString(element, "path"),
            ParseNullableSpan(element.GetProperty("span")));
    }

    private static EvidenceLocator ParseMetadataEvidenceLocator(JsonElement element)
    {
        ExpectObject(element, "kind", "assemblyIdentity", "documentationCommentId");
        return new MetadataEvidenceLocator(
            ReadString(element, "assemblyIdentity"),
            ReadString(element, "documentationCommentId"));
    }

    private static EvidenceLocator ParseGeneratedEvidenceLocator(JsonElement element)
    {
        ExpectObject(element, "kind", "producerKind", "producerId", "outputId", "sourceSha256", "span");
        var producerKind = ReadString(element, "producerKind") switch
        {
            "sourceGenerator" => GeneratedOutputKind.SourceGenerator,
            "toolGenerated" => GeneratedOutputKind.ToolGenerated,
            _ => throw Vocabulary(),
        };

        return new GeneratedOutputEvidenceLocator(
            producerKind,
            ReadString(element, "producerId"),
            ReadString(element, "outputId"),
            ReadString(element, "sourceSha256"),
            ParseNullableSpan(element.GetProperty("span")));
    }

    private static EvidenceLocator ParseSyntheticEvidenceLocator(JsonElement element)
    {
        ExpectObject(element, "kind", "fixtureId");
        return new SyntheticEvidenceLocator(ReadString(element, "fixtureId"));
    }

    private static CampaignWorkClosedOutcome? ParseClosedOutcome(
        JsonElement element,
        string workItemKey)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        ExpectObject(
            element,
            "stage",
            "code",
            "providerDisposition",
            "scribeRequestSha256",
            "attemptId",
            "patchRequestSha256",
            "patchResultCommitmentSha256");
        var attemptText = ReadNullableString(element, "attemptId");
        DocumentationScribeAttemptId? attempt = null;
        if (attemptText is not null)
        {
            if (!DocumentationScribeAttemptId.TryParse(attemptText, out var parsed))
            {
                throw Vocabulary();
            }

            attempt = parsed;
        }

        return new CampaignWorkClosedOutcome(
            ReadString(element, "stage") switch
            {
                "planning" => CampaignWorkOutcomeStage.Planning,
                "scribe" => CampaignWorkOutcomeStage.Scribe,
                "patch" => CampaignWorkOutcomeStage.Patch,
                _ => throw Vocabulary(),
            },
            ParseWorkOutcomeCode(ReadString(element, "code")),
            ReadNullableString(element, "providerDisposition") switch
            {
                "retryable" => CampaignProviderFinalDisposition.Retryable,
                "terminal" => CampaignProviderFinalDisposition.Terminal,
                null => null,
                _ => throw Vocabulary(),
            },
            ReadNullableString(element, "scribeRequestSha256"),
            attempt,
            ReadNullableString(element, "patchRequestSha256"),
            ReadNullableString(element, "patchResultCommitmentSha256"),
            workItemKey);
    }

    private static CampaignActiveReservation? ParseReservation(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ReadString(element, "kind") switch
        {
            "provider" => ParseProviderReservation(element),
            "patch" => ParsePatchReservation(element),
            _ => throw Vocabulary(),
        };
    }

    private static CampaignProviderReservation ParseProviderReservation(JsonElement element)
    {
        ExpectObject(element, "kind", "workItemKey", "scribeRequestSha256", "attemptId", "exposure");
        if (!DocumentationScribeAttemptId.TryParse(ReadString(element, "attemptId"), out var attempt))
        {
            throw Vocabulary();
        }

        var exposure = element.GetProperty("exposure");
        ExpectObject(
            exposure,
            "providerRequests",
            "inputTokens",
            "uncachedInputTokens",
            "outputTokens",
            "costMicrounits",
            "elapsedMilliseconds");
        return new CampaignProviderReservation(
            ReadString(element, "workItemKey"),
            ReadString(element, "scribeRequestSha256"),
            attempt,
            new CampaignProviderReservationExposure(
                ReadInt32(exposure, "providerRequests"),
                ReadInt32(exposure, "inputTokens"),
                ReadInt32(exposure, "uncachedInputTokens"),
                ReadInt32(exposure, "outputTokens"),
                ReadInt64(exposure, "costMicrounits"),
                ReadInt32(exposure, "elapsedMilliseconds")));
    }

    private static CampaignPatchReservation ParsePatchReservation(JsonElement element)
    {
        ExpectObject(
            element,
            "kind",
            "patchRequestSha256",
            "expectedCheckpointRevision",
            "patchAttemptCount",
            "elapsedMilliseconds");
        return new CampaignPatchReservation(
            ReadString(element, "patchRequestSha256"),
            ReadInt64(element, "expectedCheckpointRevision"),
            ReadInt32(element, "patchAttemptCount"),
            ReadInt64(element, "elapsedMilliseconds"));
    }

    private static CampaignCandidateObservation? ParseCandidate(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        ExpectObject(
            element,
            "acceptedWorkItemKeys",
            "acceptedProjectionCommitmentSha256",
            "changedFiles",
            "patchRequestSha256",
            "patchResultCommitmentSha256");
        return new CampaignCandidateObservation(
            ParseStrings(element.GetProperty("acceptedWorkItemKeys"), CampaignStateContract.MaximumActivePatchBlocks),
            ReadString(element, "acceptedProjectionCommitmentSha256"),
            ParseArray(
                element.GetProperty("changedFiles"),
                ParseChangedFile,
                CampaignStateContract.MaximumChangedFiles),
            ReadString(element, "patchRequestSha256"),
            ReadString(element, "patchResultCommitmentSha256"));
    }

    private static CampaignChangedFileObservation ParseChangedFile(JsonElement element)
    {
        ExpectObject(
            element,
            "path",
            "originalFileSha256",
            "candidateFileSha256",
            "changedDocumentationBlockCount",
            "originalDocumentationByteCount",
            "candidateDocumentationByteCount",
            "originalDocumentationLineCount",
            "candidateDocumentationLineCount");
        return new CampaignChangedFileObservation(
            ReadString(element, "path"),
            ReadString(element, "originalFileSha256"),
            ReadString(element, "candidateFileSha256"),
            ReadInt32(element, "changedDocumentationBlockCount"),
            ReadInt32(element, "originalDocumentationByteCount"),
            ReadInt32(element, "candidateDocumentationByteCount"),
            ReadInt32(element, "originalDocumentationLineCount"),
            ReadInt32(element, "candidateDocumentationLineCount"));
    }

    private static CampaignCumulativeOutcome? ParseCumulativeOutcome(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        ExpectObject(
            element,
            "kind",
            "patchRequestSha256",
            "patchResultCommitmentSha256",
            "completedFromCheckpointRevision");
        return new CampaignCumulativeOutcome(
            ParseCumulativeOutcomeKind(ReadString(element, "kind")),
            ReadString(element, "patchRequestSha256"),
            ReadNullableString(element, "patchResultCommitmentSha256"),
            ReadInt64(element, "completedFromCheckpointRevision"));
    }

    private static CampaignTerminalOutcome? ParseTerminal(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        ExpectObject(element, "kind", "reason");
        return new CampaignTerminalOutcome(
            ParseTerminalKind(ReadString(element, "kind")),
            ParseTerminalReason(ReadString(element, "reason")));
    }

    private static CampaignPredecessorSummary? ParsePredecessor(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        ExpectObject(
            element,
            "productRevision",
            "snapshot",
            "campaignConfigurationCommitmentSha256",
            "finalCheckpointRevision",
            "finalCheckpointSha256",
            "terminalKind",
            "reservation",
            "candidate");
        var reservationElement = element.GetProperty("reservation");
        CampaignPredecessorReservationSummary? reservation = null;
        if (reservationElement.ValueKind != JsonValueKind.Null)
        {
            ExpectObject(reservationElement, "kind", "correlationSha256", "conservativeCharge");
            reservation = new CampaignPredecessorReservationSummary(
                ReadString(reservationElement, "kind"),
                ReadString(reservationElement, "correlationSha256"),
                ReadInt64(reservationElement, "conservativeCharge"));
        }

        var candidate = element.GetProperty("candidate");
        ExpectObject(
            candidate,
            "acceptedCount",
            "distinctFileCount",
            "originalDocumentationByteCount",
            "candidateDocumentationByteCount",
            "originalDocumentationLineCount",
            "candidateDocumentationLineCount",
            "patchRequestSha256",
            "patchResultCommitmentSha256");
        return new CampaignPredecessorSummary(
            ParseProduct(element.GetProperty("productRevision")),
            ParseSnapshot(element.GetProperty("snapshot")),
            ReadString(element, "campaignConfigurationCommitmentSha256"),
            ReadInt64(element, "finalCheckpointRevision"),
            ReadString(element, "finalCheckpointSha256"),
            ParseTerminalKind(ReadString(element, "terminalKind")),
            reservation,
            new CampaignPredecessorCandidateSummary(
                ReadInt32(candidate, "acceptedCount"),
                ReadInt32(candidate, "distinctFileCount"),
                ReadInt64(candidate, "originalDocumentationByteCount"),
                ReadInt64(candidate, "candidateDocumentationByteCount"),
                ReadInt64(candidate, "originalDocumentationLineCount"),
                ReadInt64(candidate, "candidateDocumentationLineCount"),
                ReadNullableString(candidate, "patchRequestSha256"),
                ReadNullableString(candidate, "patchResultCommitmentSha256")));
    }

    private static bool HasDuplicateProperty(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(
            json,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = CampaignStateContract.MaximumJsonDepth,
            });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objects.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    objects.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (objects.Count == 0 || !objects.Peek().Add(reader.GetString()!))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private static void ExpectObject(JsonElement element, params string[] properties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw CampaignStateFactory.Fail(
                CampaignStateValidationCode.InvalidShape,
                "Campaign checkpoint member has an invalid shape.");
        }

        var expected = properties.ToHashSet(StringComparer.Ordinal);
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Count || actual.Any(name => !expected.Contains(name)))
        {
            throw CampaignStateFactory.Fail(
                actual.Any(name => !expected.Contains(name))
                    ? CampaignStateValidationCode.UnknownProperty
                    : CampaignStateValidationCode.InvalidShape,
                "Campaign checkpoint object has an invalid member set.");
        }
    }

    private static ImmutableArray<T> ParseArray<T>(
        JsonElement element,
        Func<JsonElement, T> parser,
        int maximum)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > maximum)
        {
            throw CampaignStateFactory.Fail(
                CampaignStateValidationCode.InvalidBound,
                "Campaign checkpoint array has an invalid bound.");
        }

        return element.EnumerateArray().Select(parser).ToImmutableArray();
    }

    private static ImmutableArray<string> ParseStrings(JsonElement element, int maximum)
    {
        return ParseArray(element, item => item.ValueKind == JsonValueKind.String
            ? item.GetString()!
            : throw CampaignStateFactory.Fail(
                CampaignStateValidationCode.InvalidShape,
                "Campaign checkpoint string array has an invalid item."), maximum);
    }

    private static string ReadString(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Shape();
        }

        return value.GetString()!;
    }

    private static string? ReadNullableString(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw Shape(),
        };
    }

    private static int ReadInt32(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw Shape();
        }

        return result;
    }

    private static long ReadInt64(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw Shape();
        }

        return result;
    }

    private static long? ReadNullableInt64(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw Shape();
        }

        return result;
    }

    private static bool ReadBoolean(JsonElement element, string property)
    {
        return element.GetProperty(property).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Shape(),
        };
    }

    private static SymbolRef ParseSymbol(JsonElement element)
    {
        ExpectObject(element, "compilationContextRef", "documentationCommentId");
        return new SymbolRef(
            ReadString(element, "compilationContextRef"),
            ReadString(element, "documentationCommentId"));
    }

    private static Utf16Span ParseSpan(JsonElement element)
    {
        ExpectObject(element, "start", "end");
        return new Utf16Span(ReadInt32(element, "start"), ReadInt32(element, "end"));
    }

    private static Utf16Span? ParseNullableSpan(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : ParseSpan(element);

    private static void WriteSymbol(Utf8JsonWriter writer, string propertyName, SymbolRef symbol)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WriteString("compilationContextRef", symbol.CompilationContextRef);
        writer.WriteString("documentationCommentId", symbol.DocumentationCommentId);
        writer.WriteEndObject();
    }

    private static void WriteSpan(Utf8JsonWriter writer, string propertyName, Utf16Span span)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WriteNumber("start", span.Start);
        writer.WriteNumber("end", span.End);
        writer.WriteEndObject();
    }

    private static void WriteNullableSpan(Utf8JsonWriter writer, Utf16Span? span)
    {
        if (span is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteNumber("start", span.Value.Start);
            writer.WriteNumber("end", span.Value.End);
            writer.WriteEndObject();
        }
    }

    private static void WriteStrings(Utf8JsonWriter writer, ImmutableArray<string> values)
    {
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static TargetProfile ParseTargetProfile(string value) => value switch
    {
        "profile.external-api" => TargetProfile.ExternalApi,
        "profile.assembly-visible" => TargetProfile.AssemblyVisible,
        _ => throw Vocabulary(),
    };

    private static CampaignWorkStatus ParseWorkStatus(string value) => value switch
    {
        "planned" => CampaignWorkStatus.Planned,
        "proposal-complete" => CampaignWorkStatus.ProposalComplete,
        "accepted" => CampaignWorkStatus.Accepted,
        "closed" => CampaignWorkStatus.Closed,
        _ => throw Vocabulary(),
    };

    private static string WorkStatusId(CampaignWorkStatus value) => value switch
    {
        CampaignWorkStatus.Planned => "planned",
        CampaignWorkStatus.ProposalComplete => "proposal-complete",
        CampaignWorkStatus.Accepted => "accepted",
        CampaignWorkStatus.Closed => "closed",
        _ => throw Vocabulary(),
    };

    private static DocumentationPatchComponentKind ParsePatchComponentKind(string value) => value switch
    {
        "typeParameter" => DocumentationPatchComponentKind.TypeParameter,
        "parameter" => DocumentationPatchComponentKind.Parameter,
        "return" => DocumentationPatchComponentKind.Return,
        "value" => DocumentationPatchComponentKind.Value,
        _ => throw Vocabulary(),
    };

    private static string PatchComponentKindId(DocumentationPatchComponentKind value) => value switch
    {
        DocumentationPatchComponentKind.TypeParameter => "typeParameter",
        DocumentationPatchComponentKind.Parameter => "parameter",
        DocumentationPatchComponentKind.Return => "return",
        DocumentationPatchComponentKind.Value => "value",
        _ => throw Vocabulary(),
    };

    private static ComponentKind ParseComponentKind(string value)
    {
        var matches = Enum.GetValues<ComponentKind>()
            .Where(kind => string.Equals(
                ClassificationVocabulary.GetId(kind),
                value,
                StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1 ? matches[0] : throw Vocabulary();
    }

    private static string ComponentKindId(ComponentKind value) => ClassificationVocabulary.GetId(value);

    private static DocumentationPatchRepositoryEncoding ParseEncoding(string value) => value switch
    {
        "utf-8" => DocumentationPatchRepositoryEncoding.Utf8,
        "utf-8-bom" => DocumentationPatchRepositoryEncoding.Utf8Bom,
        "utf-16le-bom" => DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom,
        "utf-16be-bom" => DocumentationPatchRepositoryEncoding.Utf16BigEndianBom,
        _ => throw Vocabulary(),
    };

    private static string EncodingId(DocumentationPatchRepositoryEncoding value) => value switch
    {
        DocumentationPatchRepositoryEncoding.Utf8 => "utf-8",
        DocumentationPatchRepositoryEncoding.Utf8Bom => "utf-8-bom",
        DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom => "utf-16le-bom",
        DocumentationPatchRepositoryEncoding.Utf16BigEndianBom => "utf-16be-bom",
        _ => throw Vocabulary(),
    };

    private static EvidenceKind ParseEvidenceKind(string value) => value switch
    {
        "evidence.source.declaration" => EvidenceKind.SourceDeclaration,
        "evidence.source.implementation" => EvidenceKind.SourceImplementation,
        "evidence.source.xml-documentation" => EvidenceKind.SourceXmlDocumentation,
        "evidence.source.attribute" => EvidenceKind.SourceAttribute,
        "evidence.test" => EvidenceKind.Test,
        "evidence.repository-documentation" => EvidenceKind.RepositoryDocumentation,
        "evidence.public-contract" => EvidenceKind.PublicContract,
        _ => throw Vocabulary(),
    };

    private static EvidenceRelation ParseEvidenceRelation(string value) => value switch
    {
        "evidence.declares" => EvidenceRelation.Declares,
        "evidence.documents" => EvidenceRelation.Documents,
        "evidence.tests" => EvidenceRelation.Tests,
        "evidence.references" => EvidenceRelation.References,
        "evidence.constrains" => EvidenceRelation.Constrains,
        _ => throw Vocabulary(),
    };

    private static DocumentationScribeEvidenceAuthority ParseEvidenceAuthority(string value) => value switch
    {
        "authority.source-implementation" => DocumentationScribeEvidenceAuthority.SourceImplementation,
        "authority.source-declaration" => DocumentationScribeEvidenceAuthority.SourceDeclaration,
        "authority.existing-documentation" => DocumentationScribeEvidenceAuthority.ExistingDocumentation,
        "authority.test" => DocumentationScribeEvidenceAuthority.Test,
        "authority.repository-documentation" => DocumentationScribeEvidenceAuthority.RepositoryDocumentation,
        "authority.public-contract" => DocumentationScribeEvidenceAuthority.PublicContract,
        _ => throw Vocabulary(),
    };

    private static string WorkOutcomeCodeId(CampaignWorkOutcomeCode value) => value switch
    {
        CampaignWorkOutcomeCode.PlanningTerminal => "planning-terminal",
        CampaignWorkOutcomeCode.InsufficientEvidence => "insufficient-evidence",
        CampaignWorkOutcomeCode.UnsupportedDomain => "unsupported-domain",
        CampaignWorkOutcomeCode.ProviderFailure => "provider-failure",
        CampaignWorkOutcomeCode.ToolProtocolFailure => "tool-protocol-failure",
        CampaignWorkOutcomeCode.ValidationFailure => "validation-failure",
        CampaignWorkOutcomeCode.InternalFailure => "internal-failure",
        CampaignWorkOutcomeCode.CancelledByCaller => "cancelled-by-caller",
        CampaignWorkOutcomeCode.CancelledByShutdown => "cancelled-by-shutdown",
        CampaignWorkOutcomeCode.Timeout => "timeout",
        CampaignWorkOutcomeCode.BudgetExhausted => "budget-exhausted",
        CampaignWorkOutcomeCode.PatchRejected => "patch-rejected",
        _ => throw Vocabulary(),
    };

    private static CampaignWorkOutcomeCode ParseWorkOutcomeCode(string value) => value switch
    {
        "planning-terminal" => CampaignWorkOutcomeCode.PlanningTerminal,
        "insufficient-evidence" => CampaignWorkOutcomeCode.InsufficientEvidence,
        "unsupported-domain" => CampaignWorkOutcomeCode.UnsupportedDomain,
        "provider-failure" => CampaignWorkOutcomeCode.ProviderFailure,
        "tool-protocol-failure" => CampaignWorkOutcomeCode.ToolProtocolFailure,
        "validation-failure" => CampaignWorkOutcomeCode.ValidationFailure,
        "internal-failure" => CampaignWorkOutcomeCode.InternalFailure,
        "cancelled-by-caller" => CampaignWorkOutcomeCode.CancelledByCaller,
        "cancelled-by-shutdown" => CampaignWorkOutcomeCode.CancelledByShutdown,
        "timeout" => CampaignWorkOutcomeCode.Timeout,
        "budget-exhausted" => CampaignWorkOutcomeCode.BudgetExhausted,
        "patch-rejected" => CampaignWorkOutcomeCode.PatchRejected,
        _ => throw Vocabulary(),
    };

    private static string CumulativeOutcomeId(CampaignCumulativeOutcomeKind value) => value switch
    {
        CampaignCumulativeOutcomeKind.Accepted => "accepted",
        CampaignCumulativeOutcomeKind.Rejected => "rejected",
        CampaignCumulativeOutcomeKind.Stale => "stale",
        CampaignCumulativeOutcomeKind.HostFailure => "host-failure",
        CampaignCumulativeOutcomeKind.Cancelled => "cancelled",
        CampaignCumulativeOutcomeKind.Timeout => "timeout",
        _ => throw Vocabulary(),
    };

    private static CampaignCumulativeOutcomeKind ParseCumulativeOutcomeKind(string value) => value switch
    {
        "accepted" => CampaignCumulativeOutcomeKind.Accepted,
        "rejected" => CampaignCumulativeOutcomeKind.Rejected,
        "stale" => CampaignCumulativeOutcomeKind.Stale,
        "host-failure" => CampaignCumulativeOutcomeKind.HostFailure,
        "cancelled" => CampaignCumulativeOutcomeKind.Cancelled,
        "timeout" => CampaignCumulativeOutcomeKind.Timeout,
        _ => throw Vocabulary(),
    };

    private static string TerminalKindId(CampaignTerminalKind value) => value switch
    {
        CampaignTerminalKind.Complete => "complete",
        CampaignTerminalKind.Exhausted => "exhausted",
        CampaignTerminalKind.Cancelled => "cancelled",
        CampaignTerminalKind.Timeout => "timeout",
        CampaignTerminalKind.Failed => "failed",
        CampaignTerminalKind.Superseded => "superseded",
        _ => throw Vocabulary(),
    };

    private static CampaignTerminalKind ParseTerminalKind(string value) => value switch
    {
        "complete" => CampaignTerminalKind.Complete,
        "exhausted" => CampaignTerminalKind.Exhausted,
        "cancelled" => CampaignTerminalKind.Cancelled,
        "timeout" => CampaignTerminalKind.Timeout,
        "failed" => CampaignTerminalKind.Failed,
        "superseded" => CampaignTerminalKind.Superseded,
        _ => throw Vocabulary(),
    };

    private static string TerminalReasonId(CampaignTerminalReason value) => value switch
    {
        CampaignTerminalReason.NoWork => "no-work",
        CampaignTerminalReason.AllWorkClosed => "all-work-closed",
        CampaignTerminalReason.Budget => "budget",
        CampaignTerminalReason.Caller => "caller",
        CampaignTerminalReason.Deadline => "deadline",
        CampaignTerminalReason.Host => "host",
        CampaignTerminalReason.NewSnapshot => "new-snapshot",
        _ => throw Vocabulary(),
    };

    private static CampaignTerminalReason ParseTerminalReason(string value) => value switch
    {
        "no-work" => CampaignTerminalReason.NoWork,
        "all-work-closed" => CampaignTerminalReason.AllWorkClosed,
        "budget" => CampaignTerminalReason.Budget,
        "caller" => CampaignTerminalReason.Caller,
        "deadline" => CampaignTerminalReason.Deadline,
        "host" => CampaignTerminalReason.Host,
        "new-snapshot" => CampaignTerminalReason.NewSnapshot,
        _ => throw Vocabulary(),
    };

    private static CampaignCheckpointParseResult Invalid(CampaignStateValidationCode code) =>
        new(null, code);

    private static CampaignStateValidationException Shape() => CampaignStateFactory.Fail(
        CampaignStateValidationCode.InvalidShape,
        "Campaign checkpoint member has an invalid shape.");

    private static CampaignStateValidationException Vocabulary() => CampaignStateFactory.Fail(
        CampaignStateValidationCode.InvalidVocabulary,
        "Campaign checkpoint member has an invalid closed value.");

    private sealed class BoundedMemoryStream : MemoryStream
    {
        private readonly int maximumBytes;
        private readonly CampaignStateValidationCode failureCode;
        private readonly string failureMessage;

        internal BoundedMemoryStream(int maximumBytes)
            : this(
                maximumBytes,
                CampaignStateValidationCode.DocumentTooLarge,
                "Campaign checkpoint exceeds its byte bound.")
        {
        }

        internal BoundedMemoryStream(
            int maximumBytes,
            CampaignStateValidationCode failureCode,
            string failureMessage)
            : base(Math.Min(maximumBytes, 16_384))
        {
            this.maximumBytes = maximumBytes;
            this.failureCode = failureCode;
            this.failureMessage = failureMessage;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacityFor(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacityFor(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacityFor(1);
            base.WriteByte(value);
        }

        private void EnsureCapacityFor(int additionalBytes)
        {
            if (additionalBytes < 0 || Position > maximumBytes - additionalBytes)
            {
                throw CampaignStateFactory.Fail(
                    failureCode,
                    failureMessage);
            }
        }
    }
}
