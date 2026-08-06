namespace ContractScribe.HostValidation;

public static class EvidenceValidator
{
    private sealed record ValidatedTerminal(
        string CellId,
        CellSubjectManifest Manifest,
        string ManifestPath,
        string TerminalKind,
        string TerminalPath,
        string Outcome,
        CellEvidence? Cell,
        IncompleteEvidence? Incomplete);

    private sealed record ValidatedArtifactSet(
        BundleContext Context,
        ReviewRecord Review,
        CommonSourceManifest Common,
        HostValidationArtifactSet Paths,
        IReadOnlyList<ValidatedTerminal> Cells);

    public static CellEvidence ValidateCell(
        string root,
        string evidencePath,
        string? reviewPath,
        string commonManifestPath,
        string cellManifestPath) =>
        ValidateCellCore(
            BundleValidator.Validate(root, requireReview: true, reviewPath),
            evidencePath,
            reviewPath,
            commonManifestPath,
            cellManifestPath,
            allowMaterializationDrift: false);

    public static void ValidateCellSemantics(
        BundleContext context,
        CommonSourceManifest common,
        CellSubjectManifest cellManifest,
        CellEvidence evidence)
    {
        var executionCell = cellManifest.Subject;
        if (executionCell.Materialization.CellId != evidence.Cell.CellId
            || !CanonicalJson.SerializeCanonical(executionCell.Materialization).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(evidence.Cell)))
        {
            throw new ProtocolException("HV153_EVIDENCE_MATERIALIZATION_MISMATCH");
        }
        ValidateRunSet(context, evidence);
        var vectors = context.Vectors.Vectors.ToDictionary(vector => vector.VectorId, StringComparer.Ordinal);
        var fixtures = executionCell.Fixtures.ToDictionary(fixture => fixture.VectorId, StringComparer.Ordinal);
        foreach (var run in evidence.Runs)
        {
            var vector = vectors[run.VectorId];
            fixtures.TryGetValue(run.VectorId, out var fixture);
            var derived = RunSemantics.Derive(
                context,
                vector,
                run,
                fixture,
                common.SourceConfiguration,
                executionCell.Materialization);
            if (run.ObservedObservation != derived.Observation
                || run.ObservedEnforcementClass != derived.EnforcementClass
                || run.Verdict != derived.Verdict
                || !run.DiagnosticCodes.SequenceEqual(derived.DiagnosticCodes, StringComparer.Ordinal))
            {
                throw new ProtocolException("HV156_FALSE_MATCH");
            }
        }
        ValidateWithinCellEquality(context, evidence);
        if (evidence.Outcome != RunSemantics.DeriveCellOutcome(evidence.Runs))
        {
            throw new ProtocolException("HV213_FALSE_CELL_OUTCOME");
        }
    }

    public static void ValidateAggregateCellSemantics(
        BundleContext context,
        IReadOnlyList<CellEvidence> cells)
    {
        ValidateCellSet(context, cells);
        ValidateSharedCellIdentities(cells);
        ValidateCrossCellEquality(context, cells);
    }

    public static void ValidateIncompleteSemantics(
        BundleContext context,
        ReviewRecord review,
        CommonSourceManifest common,
        CellSubjectManifest cell,
        IncompleteEvidence evidence)
    {
        if (evidence.BundleId != context.Lock.BundleId
            || evidence.NetworkClaimSetId != NetworkClaimSetRegistry.ClaimSetId
            || evidence.ReviewId != review.ReviewId
            || evidence.SourceConfigurationId != common.SourceConfiguration.SourceConfigurationId
            || evidence.CommonManifestSha256
                != CanonicalJson.Sha256(CanonicalJson.SerializeCanonical(common))
            || evidence.CellManifestSha256
                != CanonicalJson.Sha256(CanonicalJson.SerializeCanonical(cell))
            || evidence.CellId != cell.CellId
            || !CanonicalJson.SerializeCanonical(evidence.ValidationAttempt).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(common.ValidationAttempt))
            || !evidence.Immutable
            || evidence.DiagnosticCodes.Count != 1
            || evidence.Classification != IncompleteEvidenceWriter.Classify(evidence.DiagnosticCodes[0]))
        {
            throw new ProtocolException("HV158_INCOMPLETE_EVIDENCE_BINDING");
        }
    }

    public static void ValidateAggregateDerivation(
        AggregateEvidence evidence,
        CellEvidence baseline,
        IReadOnlyList<CellAggregate> expectedCells,
        string expectedOutcome)
    {
        if (evidence.BundleId != baseline.BundleId
            || evidence.ReviewId != baseline.ReviewId
            || evidence.SourceConfigurationId != baseline.SourceConfigurationId
            || evidence.CommonManifestSha256 != baseline.CommonManifestSha256
            || !CanonicalJson.SerializeCanonical(evidence.ValidationAttempt).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(baseline.ValidationAttempt))
            || !CanonicalJson.SerializeCanonical(evidence.Cells).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(expectedCells))
            || evidence.Outcome != expectedOutcome)
        {
            throw new ProtocolException("HV214_AGGREGATE_DERIVATION_MISMATCH");
        }
    }

    public static IncompleteEvidence ValidateIncomplete(
        string root,
        string evidencePath,
        string? reviewPath,
        string commonManifestPath,
        string cellManifestPath)
    {
        var context = BundleValidator.Validate(root, requireReview: true, reviewPath);
        var review = BundleValidator.ValidateReview(
            context.Root,
            reviewPath ?? BundleValidator.ReviewRelativePath,
            context.Lock.BundleId);
        var manifests = CellExecutor.ValidateSubjectManifests(
            context,
            commonManifestPath,
            cellManifestPath,
            allowMaterializationDrift: true);
        return ValidateIncompleteCore(
            context,
            review,
            manifests,
            evidencePath,
            commonManifestPath,
            cellManifestPath);
    }

    public static AggregateEvidence Aggregate(
        string root,
        string artifactRoot,
        string outputPath,
        string? reviewPath,
        string evidencePublicationBaseRevision)
    {
        var validated = ValidateArtifactSet(root, artifactRoot, reviewPath);
        var finalization = new AggregateFinalizationIdentity(
            DeriveMatrixResult(validated.Cells),
            evidencePublicationBaseRevision);
        ValidateFinalization(finalization, validated.Common.ValidationAttempt, validated.Cells);
        var outcome = SelectOutcome(
            validated.Context.Protocol.Taxonomies.ValidationPrecedence,
            validated.Cells.Select(cell => cell.Outcome));
        var aggregate = new AggregateEvidence(
            "contractscribe-m1-host-validation-aggregate-evidence-v1",
            validated.Common.BundleId,
            NetworkClaimSetRegistry.ClaimSetId,
            validated.Review.ReviewId,
            validated.Common.SourceConfiguration.SourceConfigurationId,
            CanonicalJson.Sha256File(validated.Paths.CommonManifestPath),
            validated.Common.ValidationAttempt,
            finalization,
            ExpectedCellAggregates(validated.Cells),
            outcome);
        CanonicalJson.WriteCanonical(outputPath, aggregate);
        _ = ValidateAggregate(root, outputPath, artifactRoot, reviewPath);
        return aggregate;
    }

    public static AggregateEvidence ValidateAggregate(
        string root,
        string evidencePath,
        string artifactRoot,
        string? reviewPath)
    {
        var validated = ValidateArtifactSet(root, artifactRoot, reviewPath);
        SchemaValidation.Validate(
            evidencePath,
            RepositoryPaths.ResolveConfined(
                validated.Context.Root,
                "schemas/validation/m1-host-validation-aggregate-evidence-v1.schema.json"),
            requireCanonical: true);
        var evidence = CanonicalJson.DeserializeStrict<AggregateEvidence>(
            evidencePath,
            validated.Context.Protocol.ExecutionContract.EvidenceByteLimit,
            requireCanonical: true);
        ValidateFinalization(evidence.Finalization, validated.Common.ValidationAttempt, validated.Cells);
        var expectedOutcome = SelectOutcome(
            validated.Context.Protocol.Taxonomies.ValidationPrecedence,
            validated.Cells.Select(cell => cell.Outcome));
        if (evidence.FormatVersion != "contractscribe-m1-host-validation-aggregate-evidence-v1"
            || evidence.BundleId != validated.Common.BundleId
            || evidence.NetworkClaimSetId != NetworkClaimSetRegistry.ClaimSetId
            || evidence.ReviewId != validated.Review.ReviewId
            || evidence.SourceConfigurationId != validated.Common.SourceConfiguration.SourceConfigurationId
            || evidence.CommonManifestSha256 != CanonicalJson.Sha256File(validated.Paths.CommonManifestPath)
            || !CanonicalJson.SerializeCanonical(evidence.ValidationAttempt).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(validated.Common.ValidationAttempt))
            || !CanonicalJson.SerializeCanonical(evidence.Cells).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(ExpectedCellAggregates(validated.Cells)))
            || evidence.Outcome != expectedOutcome)
        {
            throw new ProtocolException("HV214_AGGREGATE_DERIVATION_MISMATCH");
        }
        PublicSafetyScanner.EnsureSafeBytes(File.ReadAllBytes(evidencePath));
        return evidence;
    }

    public static AggregateEvidence RequirePassingAggregate(
        string root,
        string aggregatePath,
        string artifactRoot,
        string? reviewPath)
    {
        var aggregate = ValidateAggregate(root, aggregatePath, artifactRoot, reviewPath);
        if (aggregate.Outcome != "passed"
            || aggregate.Cells.Any(cell =>
                cell.TerminalKind != "cell-evidence" || cell.Outcome != "passed"))
        {
            throw new ProtocolException("HV252_AGGREGATE_NOT_PASSING");
        }
        return aggregate;
    }

    public static EvidencePublicationRecord ValidatePublicationRecord(
        string root,
        string recordPath,
        string aggregatePath,
        string artifactRoot,
        string? reviewPath)
    {
        var aggregate = ValidateAggregate(root, aggregatePath, artifactRoot, reviewPath);
        var validated = ValidateArtifactSet(root, artifactRoot, reviewPath);
        SchemaValidation.Validate(
            recordPath,
            RepositoryPaths.ResolveConfined(
                validated.Context.Root,
                "schemas/validation/m1-host-validation-publication-record-v1.schema.json"),
            requireCanonical: true);
        var record = CanonicalJson.DeserializeStrict<EvidencePublicationRecord>(
            recordPath,
            validated.Context.Protocol.ExecutionContract.EvidenceByteLimit,
            requireCanonical: true);
        var expectedEvidence = validated.Cells
            .Select(cell => cell.TerminalPath)
            .Append(aggregatePath)
            .Select(path => new ArtifactIdentity(
                RepositoryPaths.ToRepositoryRelative(validated.Context.Root, Path.GetFullPath(path)),
                CanonicalJson.Sha256File(path)))
            .OrderBy(identity => identity.Path, StringComparer.Ordinal)
            .ToArray();
        if (record.FormatVersion != "contractscribe-m1-host-validation-publication-record-v1"
            || record.BundleId != aggregate.BundleId
            || record.NetworkClaimSetId != NetworkClaimSetRegistry.ClaimSetId
            || record.ReviewId != aggregate.ReviewId
            || record.SourceConfigurationId != aggregate.SourceConfigurationId
            || !CanonicalJson.SerializeCanonical(record.ValidationAttempt).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(aggregate.ValidationAttempt))
            || record.EvidenceRecordRevision == aggregate.ValidationAttempt.ValidationExecutionSha
            || !CanonicalJson.SerializeCanonical(record.PublishedEvidence).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(expectedEvidence)))
        {
            throw new ProtocolException("HV227_PUBLICATION_RECORD_INVALID");
        }
        BundleValidator.ValidateCommitAncestry(
            validated.Context.Root,
            record.ValidationAttempt.HostRevision,
            record.EvidenceRecordRevision);
        BundleValidator.ValidateCommitAncestry(
            validated.Context.Root,
            record.ValidationAttempt.ValidationExecutionSha,
            record.EvidenceRecordRevision);
        BundleValidator.ValidateCommitBoundArtifacts(
            validated.Context.Root,
            record.EvidenceRecordRevision,
            record.PublishedEvidence);
        PublicSafetyScanner.EnsureSafeBytes(File.ReadAllBytes(recordPath));
        return record;
    }

    public static void PreparePublicArtifact(
        string root,
        string kind,
        string sourcePath,
        string outputPath,
        string? reviewPath,
        string artifactRoot,
        string? aggregateEvidencePath = null)
    {
        var expectedFileName = kind switch
        {
            "cell" => "cell-evidence.json",
            "aggregate" => "aggregate-evidence.json",
            "incomplete" => "incomplete-evidence.json",
            "publication-record" => "publication-record.json",
            _ => throw new ProtocolException("HV170_PUBLIC_KIND_UNKNOWN")
        };
        if (!Path.GetFileName(outputPath).Equals(expectedFileName, StringComparison.Ordinal))
        {
            throw new ProtocolException("HV171_PUBLIC_ALLOWLIST");
        }
        var validated = ValidateArtifactSet(root, artifactRoot, reviewPath);
        switch (kind)
        {
            case "cell":
                if (!validated.Cells.Any(cell =>
                    cell.TerminalKind == "cell-evidence"
                    && SamePath(cell.TerminalPath, sourcePath)))
                {
                    throw new ProtocolException("HV250_ARTIFACT_SET_INVALID");
                }
                break;
            case "incomplete":
                if (!validated.Cells.Any(cell =>
                    cell.TerminalKind == "incomplete-evidence"
                    && SamePath(cell.TerminalPath, sourcePath)))
                {
                    throw new ProtocolException("HV250_ARTIFACT_SET_INVALID");
                }
                break;
            case "aggregate":
                _ = ValidateAggregate(root, sourcePath, artifactRoot, reviewPath);
                break;
            case "publication-record":
                _ = ValidatePublicationRecord(
                    root,
                    sourcePath,
                    aggregateEvidencePath
                        ?? throw new ProtocolException("HV173_PUBLIC_COMPANION_MISSING"),
                    artifactRoot,
                    reviewPath);
                break;
        }
        var bytes = File.ReadAllBytes(sourcePath);
        PublicSafetyScanner.EnsureSafeBytes(bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))
            ?? throw new ProtocolException("HV172_PUBLIC_OUTPUT_INVALID"));
        CanonicalJson.WriteBytesAtomic(outputPath, bytes);
    }

    private static CellEvidence ValidateCellCore(
        BundleContext context,
        string evidencePath,
        string? reviewPath,
        string commonManifestPath,
        string cellManifestPath,
        bool allowMaterializationDrift)
    {
        var review = BundleValidator.ValidateReview(
            context.Root,
            reviewPath ?? BundleValidator.ReviewRelativePath,
            context.Lock.BundleId);
        var manifests = CellExecutor.ValidateSubjectManifests(
            context,
            commonManifestPath,
            cellManifestPath,
            allowMaterializationDrift);
        SchemaValidation.Validate(
            evidencePath,
            RepositoryPaths.ResolveConfined(
                context.Root,
                "schemas/validation/m1-host-validation-cell-evidence-v1.schema.json"),
            requireCanonical: true);
        var evidence = CanonicalJson.DeserializeStrict<CellEvidence>(
            evidencePath,
            context.Protocol.ExecutionContract.EvidenceByteLimit,
            requireCanonical: true);
        if (evidence.FormatVersion != "contractscribe-m1-host-validation-cell-evidence-v1"
            || evidence.BundleId != context.Lock.BundleId
            || evidence.NetworkClaimSetId != NetworkClaimSetRegistry.ClaimSetId)
        {
            throw new ProtocolException("HV150_EVIDENCE_BUNDLE_MISMATCH");
        }
        if (evidence.ReviewId != review.ReviewId)
        {
            throw new ProtocolException("HV151_EVIDENCE_REVIEW_MISMATCH");
        }
        if (evidence.SourceConfigurationId != manifests.Common.SourceConfiguration.SourceConfigurationId
            || evidence.CommonManifestSha256 != CanonicalJson.Sha256File(commonManifestPath)
            || evidence.CellManifestSha256 != CanonicalJson.Sha256File(cellManifestPath)
            || !CanonicalJson.SerializeCanonical(evidence.ValidationAttempt).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(manifests.Common.ValidationAttempt)))
        {
            throw new ProtocolException("HV212_EVIDENCE_SUBJECT_BINDING");
        }
        ValidateCellSemantics(context, manifests.Common, manifests.Cell, evidence);
        PublicSafetyScanner.EnsureSafeBytes(File.ReadAllBytes(evidencePath));
        return evidence;
    }

    private static IncompleteEvidence ValidateIncompleteCore(
        BundleContext context,
        ReviewRecord review,
        SubjectManifestSet manifests,
        string evidencePath,
        string commonManifestPath,
        string cellManifestPath)
    {
        SchemaValidation.Validate(
            evidencePath,
            RepositoryPaths.ResolveConfined(
                context.Root,
                "schemas/validation/m1-host-validation-incomplete-evidence-v1.schema.json"),
            requireCanonical: true);
        var evidence = CanonicalJson.DeserializeStrict<IncompleteEvidence>(
            evidencePath,
            context.Protocol.ExecutionContract.EvidenceByteLimit,
            requireCanonical: true);
        if (evidence.FormatVersion != "contractscribe-m1-host-validation-incomplete-evidence-v1"
            || evidence.BundleId != context.Lock.BundleId
            || evidence.NetworkClaimSetId != NetworkClaimSetRegistry.ClaimSetId
            || evidence.ReviewId != review.ReviewId
            || evidence.SourceConfigurationId != manifests.Common.SourceConfiguration.SourceConfigurationId
            || evidence.CommonManifestSha256 != CanonicalJson.Sha256File(commonManifestPath)
            || evidence.CellManifestSha256 != CanonicalJson.Sha256File(cellManifestPath)
            || evidence.CellId != manifests.Cell.CellId
            || !CanonicalJson.SerializeCanonical(evidence.ValidationAttempt).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(manifests.Common.ValidationAttempt))
            || !evidence.Immutable
            || evidence.DiagnosticCodes.Count != 1
            || evidence.Classification != IncompleteEvidenceWriter.Classify(evidence.DiagnosticCodes[0]))
        {
            throw new ProtocolException("HV158_INCOMPLETE_EVIDENCE_BINDING");
        }
        PublicSafetyScanner.EnsureSafeBytes(File.ReadAllBytes(evidencePath));
        return evidence;
    }

    private static ValidatedArtifactSet ValidateArtifactSet(
        string root,
        string artifactRoot,
        string? reviewPath)
    {
        var context = BundleValidator.Validate(root, requireReview: true, reviewPath);
        var review = BundleValidator.ValidateReview(
            context.Root,
            reviewPath ?? BundleValidator.ReviewRelativePath,
            context.Lock.BundleId);
        var paths = HostValidationArtifactSet.Load(context, artifactRoot);
        var common = CellExecutor.ValidateCommonManifest(
            context,
            paths.CommonManifestPath,
            allowMaterializationDrift: true);
        var terminals = new List<ValidatedTerminal>();
        foreach (var artifact in paths.Cells)
        {
            var manifests = CellExecutor.ValidateSubjectManifests(
                context,
                paths.CommonManifestPath,
                artifact.CellManifestPath,
                allowMaterializationDrift: true);
            if (manifests.Cell.CellId != artifact.CellId)
            {
                throw new ProtocolException("HV250_ARTIFACT_SET_INVALID");
            }
            if (artifact.TerminalKind == "cell-evidence")
            {
                var cell = ValidateCellCore(
                    context,
                    artifact.TerminalPath,
                    reviewPath,
                    paths.CommonManifestPath,
                    artifact.CellManifestPath,
                    allowMaterializationDrift: true);
                terminals.Add(new(
                    artifact.CellId,
                    manifests.Cell,
                    artifact.CellManifestPath,
                    artifact.TerminalKind,
                    artifact.TerminalPath,
                    cell.Outcome,
                    cell,
                    null));
            }
            else
            {
                var incomplete = ValidateIncompleteCore(
                    context,
                    review,
                    manifests,
                    artifact.TerminalPath,
                    paths.CommonManifestPath,
                    artifact.CellManifestPath);
                terminals.Add(new(
                    artifact.CellId,
                    manifests.Cell,
                    artifact.CellManifestPath,
                    artifact.TerminalKind,
                    artifact.TerminalPath,
                    incomplete.Classification,
                    null,
                    incomplete));
            }
        }
        ValidateSharedTerminalIdentities(common, review.ReviewId, terminals);
        var completeCells = terminals.Where(cell => cell.Cell is not null).Select(cell => cell.Cell!).ToArray();
        if (completeCells.Length == context.Protocol.RequiredCells.Count)
        {
            ValidateAggregateCellSemantics(context, completeCells);
        }
        return new(context, review, common, paths, terminals);
    }

    private static IReadOnlyList<CellAggregate> ExpectedCellAggregates(
        IReadOnlyList<ValidatedTerminal> cells) =>
        cells.OrderBy(cell => cell.CellId, StringComparer.Ordinal)
            .Select(cell => new CellAggregate(
                cell.CellId,
                CanonicalJson.Sha256File(cell.ManifestPath),
                cell.TerminalKind,
                CanonicalJson.Sha256File(cell.TerminalPath),
                cell.Outcome))
            .ToArray();

    private static void ValidateSharedTerminalIdentities(
        CommonSourceManifest common,
        string expectedReviewId,
        IReadOnlyList<ValidatedTerminal> cells)
    {
        foreach (var terminal in cells)
        {
            var bundleId = terminal.Cell?.BundleId ?? terminal.Incomplete!.BundleId;
            var reviewId = terminal.Cell?.ReviewId ?? terminal.Incomplete!.ReviewId;
            var sourceId = terminal.Cell?.SourceConfigurationId ?? terminal.Incomplete!.SourceConfigurationId;
            var commonSha = terminal.Cell?.CommonManifestSha256 ?? terminal.Incomplete!.CommonManifestSha256;
            var attempt = terminal.Cell?.ValidationAttempt ?? terminal.Incomplete!.ValidationAttempt;
            if (bundleId != common.BundleId
                || reviewId != expectedReviewId
                || sourceId != common.SourceConfiguration.SourceConfigurationId
                || commonSha != terminal.Manifest.CommonManifestSha256
                || !CanonicalJson.SerializeCanonical(attempt).AsSpan()
                    .SequenceEqual(CanonicalJson.SerializeCanonical(common.ValidationAttempt)))
            {
                throw new ProtocolException("HV160_MIXED_EXECUTION_ATTEMPT");
            }
        }
    }

    private static void ValidateRunSet(BundleContext context, CellEvidence evidence)
    {
        var expected = context.Vectors.ExpandExpectedRuns()
            .Where(run => run.CellId == evidence.Cell.CellId)
            .Select(run => $"{run.CellId}\0{run.VectorId}\0{run.RunId}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = evidence.Runs
            .Select(run => $"{evidence.Cell.CellId}\0{run.VectorId}\0{run.RunId}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal)
            || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length)
        {
            throw new ProtocolException("HV154_EVIDENCE_EXECUTION_SET");
        }
    }

    private static void ValidateWithinCellEquality(BundleContext context, CellEvidence evidence)
    {
        var permittedFields = new HashSet<string>(
            [
                "observedObservation",
                "subject.observationCode",
                "subject.canonicalResultSha256",
                "observedAuditResult.targetProfile",
                "observedAuditResult.auditOutcomes",
                "subject.auditOutcome",
                "subject.executionOutcome",
                "subject.failureStage",
                "subject.failureCode",
                "subject.processStart",
                "subject.processTermination",
                "subject.terminalState",
                "subject.artifactState",
                "subject.hostFacts.toolchainSelectionState",
                "repositoryDelta.protectedChanged"
            ],
            StringComparer.Ordinal);
        foreach (var vector in context.Vectors.Vectors.Where(vector =>
            vector.Cells.Contains(evidence.Cell.CellId, StringComparer.Ordinal)))
        {
            if (vector.EqualityFields.Any(field => !permittedFields.Contains(field)))
            {
                throw new ProtocolException("HV215_EQUALITY_FIELD_UNKNOWN");
            }
            if (vector.FreshProcessPerInvocation)
            {
                var runs = evidence.Runs.Where(run => run.VectorId == vector.VectorId).ToArray();
                if (runs.Any(run => run.Verdict != "matched"))
                {
                    continue;
                }
                var processIds = runs.Select(run =>
                    run.ObservedProcesses.SingleOrDefault(process => process.Role == "subject-runtime")?.ProcessId)
                    .ToArray();
                if (processIds.Any(id => id is null)
                    || processIds.Distinct().Count() != processIds.Length)
                {
                    throw new ProtocolException("HV216_FRESH_PROCESS_NOT_PROVEN");
                }
                EnsureEquality(vector, runs);
            }
        }
    }

    private static void ValidateCrossCellEquality(BundleContext context, IReadOnlyList<CellEvidence> cells)
    {
        foreach (var vector in context.Vectors.Vectors.Where(vector => vector.CrossCellEquality))
        {
            var runs = cells.SelectMany(cell => cell.Runs)
                .Where(run => run.VectorId == vector.VectorId)
                .ToArray();
            if (runs.Any(run => run.Verdict != "matched"))
            {
                continue;
            }
            EnsureEquality(vector, runs);
        }
    }

    private static void EnsureEquality(VectorDefinition vector, IReadOnlyList<RunEvidence> runs)
    {
        var mismatch = vector.EqualityFields
            .Select(field => runs.Select(run => EqualityValue(field, run)).ToArray())
            .Any(values => values.Any(string.IsNullOrWhiteSpace)
                || values.Distinct(StringComparer.Ordinal).Count() != 1);
        if (mismatch)
        {
            throw new ProtocolException("HV217_DETERMINISM_MISMATCH");
        }
    }

    private static string? EqualityValue(string field, RunEvidence run) =>
        field switch
        {
            "observedObservation" => run.ObservedObservation,
            "subject.observationCode" => run.Subject?.ObservationCode ?? run.ObservedObservation,
            "subject.canonicalResultSha256" => run.ObservedCanonicalResult?.Sha256,
            "observedAuditResult.targetProfile" => run.ObservedAuditResult?.TargetProfile,
            "observedAuditResult.auditOutcomes" => run.ObservedAuditResult is null
                ? null
                : string.Join("\0", run.ObservedAuditResult.AuditOutcomes),
            "subject.auditOutcome" => run.Subject?.AuditOutcome,
            "subject.executionOutcome" => run.Subject?.ExecutionOutcome,
            "subject.failureStage" => run.Subject?.FailureStage,
            "subject.failureCode" => run.Subject?.FailureCode,
            "subject.hostFacts.toolchainSelectionState" => run.Subject?.HostFacts?.ToolchainSelectionState,
            "subject.processStart" => run.Subject?.ProcessStart ?? run.Process.ProcessStart,
            "subject.processTermination" => run.Subject?.ProcessTermination ?? run.Process.ProcessTermination,
            "subject.terminalState" => run.Subject?.TerminalState ?? "not-entered",
            "subject.artifactState" => run.Subject?.ArtifactState
                ?? (run.ObservedCanonicalResult is null ? "absent" : "published"),
            "repositoryDelta.protectedChanged" => string.Join("\0", run.RepositoryDelta.ProtectedChanged),
            _ => throw new ProtocolException("HV215_EQUALITY_FIELD_UNKNOWN")
        };

    private static void ValidateCellSet(BundleContext context, IReadOnlyList<CellEvidence> cells)
    {
        var expected = context.Protocol.RequiredCells.Select(cell => cell.CellId).Order(StringComparer.Ordinal).ToArray();
        var actual = cells.Select(cell => cell.Cell.CellId).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal)
            || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length)
        {
            throw new ProtocolException("HV159_AGGREGATE_CELL_SET");
        }
    }

    private static void ValidateSharedCellIdentities(IReadOnlyList<CellEvidence> cells)
    {
        var first = cells[0];
        if (cells.Skip(1).Any(cell =>
            cell.BundleId != first.BundleId
            || cell.NetworkClaimSetId != first.NetworkClaimSetId
            || cell.ReviewId != first.ReviewId
            || cell.SourceConfigurationId != first.SourceConfigurationId
            || cell.CommonManifestSha256 != first.CommonManifestSha256
            || !CanonicalJson.SerializeCanonical(cell.ValidationAttempt).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(first.ValidationAttempt))))
        {
            throw new ProtocolException("HV160_MIXED_EXECUTION_ATTEMPT");
        }
    }

    private static void ValidateFinalization(
        AggregateFinalizationIdentity finalization,
        ValidationAttemptIdentity attempt,
        IReadOnlyList<ValidatedTerminal> cells)
    {
        if (finalization.MatrixResult != DeriveMatrixResult(cells)
            || finalization.EvidencePublicationBaseRevision != attempt.ValidationExecutionSha)
        {
            throw new ProtocolException("HV218_FINALIZATION_IDENTITY_INVALID");
        }
    }

    private static string DeriveMatrixResult(IReadOnlyList<ValidatedTerminal> cells) =>
        cells.All(cell => cell.TerminalKind == "cell-evidence" && cell.Outcome == "passed")
            ? "passed"
            : cells.Any(cell => cell.Outcome is
                "protected-input-invalidated" or "protocol-failure" or "subject-nonconformance")
                ? "failed"
                : "incomplete";

    private static string SelectOutcome(IReadOnlyList<string> precedence, IEnumerable<string> outcomes)
    {
        var set = outcomes.ToHashSet(StringComparer.Ordinal);
        return precedence.FirstOrDefault(set.Contains)
            ?? throw new ProtocolException("HV162_AGGREGATE_OUTCOME_UNKNOWN");
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
