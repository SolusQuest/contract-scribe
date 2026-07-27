namespace ContractScribe.HostValidation;

public static class EvidenceValidator
{
    public static CellEvidence ValidateCell(
        string root,
        string evidencePath,
        string? reviewPath,
        string subjectManifestPath)
    {
        var context = BundleValidator.Validate(root, requireReview: true, reviewPath);
        var review = BundleValidator.ValidateReview(
            context.Root,
            reviewPath ?? BundleValidator.ReviewRelativePath,
            context.Lock.BundleId);
        var subject = CellExecutor.ValidateSubjectManifest(context, subjectManifestPath);
        SchemaValidation.Validate(
            evidencePath,
            RepositoryPaths.ResolveConfined(context.Root, "schemas/validation/m1-host-validation-cell-evidence-v1.schema.json"),
            requireCanonical: true);
        var evidence = CanonicalJson.DeserializeStrict<CellEvidence>(
            evidencePath,
            context.Protocol.ExecutionContract.EvidenceByteLimit,
            requireCanonical: true);
        if (evidence.BundleId != context.Lock.BundleId)
        {
            throw new ProtocolException("HV150_EVIDENCE_BUNDLE_MISMATCH");
        }
        if (evidence.ReviewId != review.ReviewId)
        {
            throw new ProtocolException("HV151_EVIDENCE_REVIEW_MISMATCH");
        }
        if (evidence.SourceConfigurationId != subject.SourceConfiguration.SourceConfigurationId
            || evidence.SubjectManifestSha256 != CanonicalJson.Sha256File(subjectManifestPath)
            || !CanonicalJson.SerializeCanonical(evidence.ValidationAttempt).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(subject.ValidationAttempt)))
        {
            throw new ProtocolException("HV212_EVIDENCE_SUBJECT_BINDING");
        }

        ValidateCellSemantics(context, subject, evidence);
        PublicSafetyScanner.EnsureSafeBytes(File.ReadAllBytes(evidencePath));
        return evidence;
    }

    public static void ValidateCellSemantics(
        BundleContext context,
        ExecutionSubjectManifest subject,
        CellEvidence evidence)
    {
        var executionCell = subject.Cells.SingleOrDefault(cell =>
            cell.Materialization.CellId == evidence.Cell.CellId)
            ?? throw new ProtocolException("HV152_EVIDENCE_CELL_UNKNOWN");
        if (!CanonicalJson.SerializeCanonical(executionCell.Materialization).AsSpan()
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
                subject.SourceConfiguration);
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
        ValidateSharedIdentities(cells);
        ValidateCrossCellEquality(context, cells);
    }

    public static void ValidateIncompleteSemantics(
        BundleContext context,
        ReviewRecord review,
        ExecutionSubjectManifest subject,
        IncompleteEvidence evidence) =>
        ValidateIncompleteBinding(context, review, subject, evidence, requireCurrentAttempt: true);

    public static void ValidateAggregateDerivation(
        AggregateEvidence evidence,
        CellEvidence baseline,
        IReadOnlyList<CellAggregate> expectedCells,
        IReadOnlyList<string> expectedSupersedes,
        string expectedOutcome)
    {
        if (evidence.BundleId != baseline.BundleId
            || evidence.ReviewId != baseline.ReviewId
            || evidence.SourceConfigurationId != baseline.SourceConfigurationId
            || !CanonicalJson.SerializeCanonical(evidence.ValidationAttempt).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(baseline.ValidationAttempt))
            || !CanonicalJson.SerializeCanonical(evidence.Cells).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(expectedCells))
            || !evidence.Supersedes.SequenceEqual(expectedSupersedes, StringComparer.Ordinal)
            || evidence.Outcome != expectedOutcome)
        {
            throw new ProtocolException("HV214_AGGREGATE_DERIVATION_MISMATCH");
        }
    }

    public static IncompleteEvidence ValidateIncomplete(
        string root,
        string evidencePath,
        string? reviewPath,
        string subjectManifestPath)
    {
        var context = BundleValidator.Validate(root, requireReview: true, reviewPath);
        var review = BundleValidator.ValidateReview(
            context.Root,
            reviewPath ?? BundleValidator.ReviewRelativePath,
            context.Lock.BundleId);
        var subject = CellExecutor.ValidateSubjectManifest(
            context,
            subjectManifestPath,
            allowMaterializationDrift: true);
        SchemaValidation.Validate(
            evidencePath,
            RepositoryPaths.ResolveConfined(context.Root, "schemas/validation/m1-host-validation-incomplete-evidence-v1.schema.json"),
            requireCanonical: true);
        var evidence = CanonicalJson.DeserializeStrict<IncompleteEvidence>(
            evidencePath,
            context.Protocol.ExecutionContract.EvidenceByteLimit,
            requireCanonical: true);
        ValidateIncompleteBinding(context, review, subject, evidence, requireCurrentAttempt: true);
        PublicSafetyScanner.EnsureSafeBytes(File.ReadAllBytes(evidencePath));
        return evidence;
    }

    public static AggregateEvidence Aggregate(
        string root,
        IReadOnlyList<string> cellEvidencePaths,
        string outputPath,
        string? reviewPath,
        string subjectManifestPath,
        AggregateFinalizationIdentity finalization,
        IReadOnlyList<string> supersededEvidencePaths)
    {
        var context = BundleValidator.Validate(root, requireReview: true, reviewPath);
        var cells = cellEvidencePaths
            .Select(path => ValidateCell(root, path, reviewPath, subjectManifestPath))
            .ToArray();
        ValidateCellSet(context, cells);
        ValidateSharedIdentities(cells);
        ValidateCrossCellEquality(context, cells);
        ValidateFinalization(finalization, cells[0].ValidationAttempt, cells);
        var review = BundleValidator.ValidateReview(
            context.Root,
            reviewPath ?? BundleValidator.ReviewRelativePath,
            context.Lock.BundleId);
        var subject = CellExecutor.ValidateSubjectManifest(
            context,
            subjectManifestPath,
            allowMaterializationDrift: true);
        var supersedes = ValidateSupersedes(
            supersededEvidencePaths,
            context,
            review,
            subject,
            cells[0].ValidationAttempt);
        var outcome = SelectOutcome(
            context.Protocol.Taxonomies.ValidationPrecedence,
            cells.Select(cell => cell.Outcome));
        var evidenceHashes = cells
            .Select((cell, index) => KeyValuePair.Create(
                cell.Cell.CellId,
                CanonicalJson.Sha256File(cellEvidencePaths[index])))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var aggregate = new AggregateEvidence(
            "contractscribe-m1-host-validation-aggregate-evidence-v1",
            cells[0].BundleId,
            cells[0].ReviewId,
            cells[0].SourceConfigurationId,
            cells[0].ValidationAttempt,
            finalization,
            cells.OrderBy(cell => cell.Cell.CellId, StringComparer.Ordinal)
                .Select(cell => new CellAggregate(
                    cell.Cell.CellId,
                    evidenceHashes[cell.Cell.CellId],
                    cell.Outcome))
                .ToArray(),
            outcome,
            supersedes);
        CanonicalJson.WriteCanonical(outputPath, aggregate);
        _ = ValidateAggregate(
            root,
            outputPath,
            cellEvidencePaths,
            reviewPath,
            subjectManifestPath,
            supersededEvidencePaths);
        return aggregate;
    }

    public static AggregateEvidence ValidateAggregate(
        string root,
        string evidencePath,
        IReadOnlyList<string> cellEvidencePaths,
        string? reviewPath,
        string subjectManifestPath,
        IReadOnlyList<string> supersededEvidencePaths)
    {
        var context = BundleValidator.Validate(root, requireReview: true, reviewPath);
        SchemaValidation.Validate(
            evidencePath,
            RepositoryPaths.ResolveConfined(context.Root, "schemas/validation/m1-host-validation-aggregate-evidence-v1.schema.json"),
            requireCanonical: true);
        var evidence = CanonicalJson.DeserializeStrict<AggregateEvidence>(
            evidencePath,
            context.Protocol.ExecutionContract.EvidenceByteLimit,
            requireCanonical: true);
        var cells = cellEvidencePaths
            .Select(path => ValidateCell(root, path, reviewPath, subjectManifestPath))
            .ToArray();
        ValidateCellSet(context, cells);
        ValidateSharedIdentities(cells);
        ValidateCrossCellEquality(context, cells);
        ValidateFinalization(evidence.Finalization, cells[0].ValidationAttempt, cells);
        var review = BundleValidator.ValidateReview(
            context.Root,
            reviewPath ?? BundleValidator.ReviewRelativePath,
            context.Lock.BundleId);
        var subject = CellExecutor.ValidateSubjectManifest(
            context,
            subjectManifestPath,
            allowMaterializationDrift: true);
        var supersedes = ValidateSupersedes(
            supersededEvidencePaths,
            context,
            review,
            subject,
            cells[0].ValidationAttempt);
        var expectedAggregates = cells
            .Select((cell, index) => new CellAggregate(
                cell.Cell.CellId,
                CanonicalJson.Sha256File(cellEvidencePaths[index]),
                cell.Outcome))
            .OrderBy(cell => cell.CellId, StringComparer.Ordinal)
            .ToArray();
        ValidateAggregateDerivation(
            evidence,
            cells[0],
            expectedAggregates,
            supersedes,
            SelectOutcome(
                context.Protocol.Taxonomies.ValidationPrecedence,
                cells.Select(cell => cell.Outcome)));
        PublicSafetyScanner.EnsureSafeBytes(File.ReadAllBytes(evidencePath));
        return evidence;
    }

    public static EvidencePublicationRecord ValidatePublicationRecord(
        string root,
        string recordPath,
        string aggregatePath,
        IReadOnlyList<string> cellEvidencePaths,
        string? reviewPath,
        string subjectManifestPath,
        IReadOnlyList<string> supersededEvidencePaths)
    {
        var aggregate = ValidateAggregate(
            root,
            aggregatePath,
            cellEvidencePaths,
            reviewPath,
            subjectManifestPath,
            supersededEvidencePaths);
        var context = BundleValidator.Validate(root, requireReview: true, reviewPath);
        SchemaValidation.Validate(
            recordPath,
            RepositoryPaths.ResolveConfined(
                context.Root,
                "schemas/validation/m1-host-validation-publication-record-v1.schema.json"),
            requireCanonical: true);
        var record = CanonicalJson.DeserializeStrict<EvidencePublicationRecord>(
            recordPath,
            context.Protocol.ExecutionContract.EvidenceByteLimit,
            requireCanonical: true);
        var expectedEvidence = cellEvidencePaths
            .Append(aggregatePath)
            .Select(path => new ArtifactIdentity(
                RepositoryPaths.ToRepositoryRelative(context.Root, Path.GetFullPath(path)),
                CanonicalJson.Sha256File(path)))
            .OrderBy(identity => identity.Path, StringComparer.Ordinal)
            .ToArray();
        if (record.FormatVersion != "contractscribe-m1-host-validation-publication-record-v1"
            || record.BundleId != aggregate.BundleId
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
            context.Root,
            record.ValidationAttempt.HostRevision,
            record.EvidenceRecordRevision);
        BundleValidator.ValidateCommitAncestry(
            context.Root,
            record.ValidationAttempt.ValidationExecutionSha,
            record.EvidenceRecordRevision);
        BundleValidator.ValidateCommitBoundArtifacts(
            context.Root,
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
        string subjectManifestPath,
        IReadOnlyList<string> cellEvidencePaths,
        IReadOnlyList<string> supersededEvidencePaths,
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

        switch (kind)
        {
            case "cell":
                _ = ValidateCell(root, sourcePath, reviewPath, subjectManifestPath);
                break;
            case "aggregate":
                _ = ValidateAggregate(
                    root,
                    sourcePath,
                    cellEvidencePaths,
                    reviewPath,
                    subjectManifestPath,
                    supersededEvidencePaths);
                break;
            case "incomplete":
                _ = ValidateIncomplete(root, sourcePath, reviewPath, subjectManifestPath);
                break;
            case "publication-record":
                _ = ValidatePublicationRecord(
                    root,
                    sourcePath,
                    aggregateEvidencePath
                        ?? throw new ProtocolException("HV173_PUBLIC_COMPANION_MISSING"),
                    cellEvidencePaths,
                    reviewPath,
                    subjectManifestPath,
                    supersededEvidencePaths);
                break;
        }
        var bytes = File.ReadAllBytes(sourcePath);
        PublicSafetyScanner.EnsureSafeBytes(bytes);
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))
            ?? throw new ProtocolException("HV172_PUBLIC_OUTPUT_INVALID");
        Directory.CreateDirectory(directory);
        CanonicalJson.WriteBytesAtomic(outputPath, bytes);
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

    private static void ValidateCrossCellEquality(
        BundleContext context,
        IReadOnlyList<CellEvidence> cells)
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
        foreach (var field in vector.EqualityFields)
        {
            var values = runs.Select(run => field switch
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
                "subject.processStart" => run.Subject?.ProcessStart ?? run.Process.ProcessStart,
                "subject.processTermination" => run.Subject?.ProcessTermination ?? run.Process.ProcessTermination,
                "subject.terminalState" => run.Subject?.TerminalState ?? "not-entered",
                "subject.artifactState" => run.Subject?.ArtifactState
                    ?? (run.ObservedCanonicalResult is null ? "absent" : "published"),
                "repositoryDelta.protectedChanged" => string.Join("\0", run.RepositoryDelta.ProtectedChanged),
                _ => throw new ProtocolException("HV215_EQUALITY_FIELD_UNKNOWN")
            }).ToArray();
            if (values.Any(string.IsNullOrWhiteSpace)
                || values.Distinct(StringComparer.Ordinal).Count() != 1)
            {
                throw new ProtocolException("HV217_DETERMINISM_MISMATCH");
            }
        }
    }

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

    private static void ValidateSharedIdentities(IReadOnlyList<CellEvidence> cells)
    {
        var first = cells[0];
        if (cells.Skip(1).Any(cell =>
            cell.BundleId != first.BundleId
            || cell.ReviewId != first.ReviewId
            || cell.SourceConfigurationId != first.SourceConfigurationId
            || cell.SubjectManifestSha256 != first.SubjectManifestSha256
            || !CanonicalJson.SerializeCanonical(cell.ValidationAttempt).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(first.ValidationAttempt))))
        {
            throw new ProtocolException("HV160_MIXED_EXECUTION_ATTEMPT");
        }
    }

    private static void ValidateFinalization(
        AggregateFinalizationIdentity finalization,
        ValidationAttemptIdentity attempt,
        IReadOnlyList<CellEvidence> cells)
    {
        var expectedMatrixResult = cells.All(cell => cell.Outcome == "passed")
            ? "passed"
            : cells.Any(cell => cell.Outcome is "protocol-failure" or "subject-nonconformance")
                ? "failed"
                : "incomplete";
        if (finalization.MatrixResult != expectedMatrixResult
            || finalization.EvidencePublicationBaseRevision.Length != 40
            || !finalization.EvidencePublicationBaseRevision.All(Uri.IsHexDigit)
            || finalization.EvidencePublicationBaseRevision != attempt.ValidationExecutionSha)
        {
            throw new ProtocolException("HV218_FINALIZATION_IDENTITY_INVALID");
        }
    }

    private static IReadOnlyList<string> ValidateSupersedes(
        IReadOnlyList<string> evidencePaths,
        BundleContext context,
        ReviewRecord review,
        ExecutionSubjectManifest subject,
        ValidationAttemptIdentity currentAttempt)
    {
        var identities = new List<string>();
        foreach (var path in evidencePaths)
        {
            SchemaValidation.Validate(
                path,
                RepositoryPaths.ResolveConfined(
                    context.Root,
                    "schemas/validation/m1-host-validation-incomplete-evidence-v1.schema.json"),
                requireCanonical: true);
            var evidence = CanonicalJson.DeserializeStrict<IncompleteEvidence>(
                path,
                context.Protocol.ExecutionContract.EvidenceByteLimit,
                requireCanonical: true);
            ValidateIncompleteBinding(
                context,
                review,
                subject,
                evidence,
                requireCurrentAttempt: false);
            if (!IsEarlierAttempt(evidence.ValidationAttempt, currentAttempt))
            {
                throw new ProtocolException("HV221_SUPERSEDES_INVALID");
            }
            PublicSafetyScanner.EnsureSafeBytes(File.ReadAllBytes(path));
            identities.Add($"evidence.{CanonicalJson.Sha256File(path)}");
        }
        var ordered = identities.Order(StringComparer.Ordinal).ToArray();
        if (ordered.Distinct(StringComparer.Ordinal).Count() != ordered.Length)
        {
            throw new ProtocolException("HV221_SUPERSEDES_INVALID");
        }
        return ordered;
    }

    private static void ValidateIncompleteBinding(
        BundleContext context,
        ReviewRecord review,
        ExecutionSubjectManifest subject,
        IncompleteEvidence evidence,
        bool requireCurrentAttempt)
    {
        if (evidence.BundleId != context.Lock.BundleId
            || evidence.ReviewId != review.ReviewId
            || evidence.SourceConfigurationId != subject.SourceConfiguration.SourceConfigurationId
            || requireCurrentAttempt
                && !CanonicalJson.SerializeCanonical(evidence.ValidationAttempt).AsSpan()
                    .SequenceEqual(CanonicalJson.SerializeCanonical(subject.ValidationAttempt))
            || !evidence.Immutable
            || evidence.DiagnosticCodes.Count == 0
            || evidence.Classification != IncompleteEvidenceWriter.Classify(evidence.DiagnosticCodes[0]))
        {
            throw new ProtocolException("HV158_INCOMPLETE_EVIDENCE_BINDING");
        }
    }

    private static bool IsEarlierAttempt(
        ValidationAttemptIdentity candidate,
        ValidationAttemptIdentity current)
    {
        if (!ulong.TryParse(candidate.WorkflowRunId, out var candidateRun)
            || !ulong.TryParse(current.WorkflowRunId, out var currentRun)
            || candidate.ValidationExecutionSha == current.ValidationExecutionSha)
        {
            return false;
        }
        return candidateRun < currentRun
            || candidateRun == currentRun && candidate.RunAttempt < current.RunAttempt;
    }

    private static string SelectOutcome(
        IReadOnlyList<string> precedence,
        IEnumerable<string> cellOutcomes)
    {
        var outcomes = cellOutcomes.ToHashSet(StringComparer.Ordinal);
        return precedence.FirstOrDefault(outcomes.Contains)
            ?? throw new ProtocolException("HV162_AGGREGATE_OUTCOME_UNKNOWN");
    }
}
