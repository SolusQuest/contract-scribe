using System.Text.Json;

namespace ContractScribe.HostValidation;

public static class EvidenceValidator
{
    public static CellEvidence ValidateCell(
        string root,
        string evidencePath,
        string? reviewPath = null)
    {
        var context = BundleValidator.Validate(root, requireReview: true, reviewPath);
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

        var review = BundleValidator.ValidateReview(
            context.Root,
            reviewPath ?? BundleValidator.ReviewRelativePath,
            context.Lock.BundleId);
        if (evidence.ReviewId != review.ReviewId)
        {
            throw new ProtocolException("HV151_EVIDENCE_REVIEW_MISMATCH");
        }

        var cell = context.Protocol.RequiredCells.SingleOrDefault(cell => cell.CellId == evidence.Cell.CellId)
            ?? throw new ProtocolException("HV152_EVIDENCE_CELL_UNKNOWN");
        if (cell.Rid != evidence.Cell.Rid || cell.Architecture != evidence.Cell.Architecture)
        {
            throw new ProtocolException("HV153_EVIDENCE_MATERIALIZATION_MISMATCH");
        }

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

        var vectors = context.Vectors.Vectors.ToDictionary(vector => vector.VectorId, StringComparer.Ordinal);
        foreach (var run in evidence.Runs)
        {
            var vector = vectors[run.VectorId];
            if (run.ExpectedObservation != vector.ExpectedObservation
                || run.ExpectedEnforcementClass != vector.ExpectedEnforcementClass
                || run.ObservedEnforcementClass != run.Subject.EnforcementClass)
            {
                throw new ProtocolException("HV155_EVIDENCE_ORACLE_MISMATCH");
            }
            if (run.Verdict == "matched"
                && (run.ObservedObservation != run.ExpectedObservation
                    || run.ObservedEnforcementClass != run.ExpectedEnforcementClass))
            {
                throw new ProtocolException("HV156_FALSE_MATCH");
            }
            if (run.Subject.ExecutionOutcome is not null
                && run.Subject.ExecutionOutcome != "succeeded"
                && (string.IsNullOrWhiteSpace(run.Subject.FailureRegistryIdentity)
                    || string.IsNullOrWhiteSpace(run.Subject.FailureCode)
                    || string.IsNullOrWhiteSpace(run.Subject.FailureStage)))
            {
                throw new ProtocolException("HV157_FAILURE_REGISTRY_BINDING");
            }
        }

        PublicSafetyScanner.EnsureSafeBytes(File.ReadAllBytes(evidencePath));
        return evidence;
    }

    public static IncompleteEvidence ValidateIncomplete(
        string root,
        string evidencePath,
        string? reviewPath = null)
    {
        var context = BundleValidator.Validate(root, requireReview: true, reviewPath);
        SchemaValidation.Validate(
            evidencePath,
            RepositoryPaths.ResolveConfined(context.Root, "schemas/validation/m1-host-validation-incomplete-evidence-v1.schema.json"),
            requireCanonical: true);
        var evidence = CanonicalJson.DeserializeStrict<IncompleteEvidence>(
            evidencePath,
            context.Protocol.ExecutionContract.EvidenceByteLimit,
            requireCanonical: true);
        if (evidence.BundleId != context.Lock.BundleId || !evidence.Immutable)
        {
            throw new ProtocolException("HV158_INCOMPLETE_EVIDENCE_BINDING");
        }
        PublicSafetyScanner.EnsureSafeBytes(File.ReadAllBytes(evidencePath));
        return evidence;
    }

    public static AggregateEvidence Aggregate(
        string root,
        IReadOnlyList<string> cellEvidencePaths,
        string outputPath,
        string? reviewPath = null)
    {
        var context = BundleValidator.Validate(root, requireReview: true, reviewPath);
        var cells = cellEvidencePaths.Select(path => ValidateCell(root, path, reviewPath)).ToArray();
        var expectedCells = context.Protocol.RequiredCells.Select(cell => cell.CellId).Order(StringComparer.Ordinal).ToArray();
        var actualCells = cells.Select(cell => cell.Cell.CellId).Order(StringComparer.Ordinal).ToArray();
        if (!actualCells.SequenceEqual(expectedCells, StringComparer.Ordinal)
            || actualCells.Distinct(StringComparer.Ordinal).Count() != actualCells.Length)
        {
            throw new ProtocolException("HV159_AGGREGATE_CELL_SET");
        }

        var executionIdentity = CanonicalJson.SerializeCanonical(cells[0].ValidationExecution.ToElement());
        if (cells.Skip(1).Any(cell =>
                !CanonicalJson.SerializeCanonical(cell.ValidationExecution.ToElement()).AsSpan().SequenceEqual(executionIdentity)))
        {
            throw new ProtocolException("HV160_MIXED_EXECUTION_ATTEMPT");
        }

        if (cells.Any(cell => cell.BundleId != cells[0].BundleId || cell.ReviewId != cells[0].ReviewId))
        {
            throw new ProtocolException("HV161_MIXED_IDENTITY");
        }

        var outcome = SelectOutcome(context.Protocol.Taxonomies.ValidationPrecedence, cells.Select(cell => cell.Outcome));
        var evidenceHashes = cells
            .Select((cell, index) => KeyValuePair.Create(
                cell.Cell.CellId,
                CanonicalJson.Sha256File(cellEvidencePaths[index])))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var aggregate = new AggregateEvidence(
            "contractscribe-m1-host-validation-aggregate-evidence-v1",
            cells[0].BundleId,
            cells[0].ReviewId,
            cells[0].ValidationExecution,
            cells.OrderBy(cell => cell.Cell.CellId, StringComparer.Ordinal)
                .Select((cell, index) => new CellAggregate(
                    cell.Cell.CellId,
                    evidenceHashes[cell.Cell.CellId],
                    cell.Outcome))
                .ToArray(),
            outcome,
            []);
        CanonicalJson.WriteCanonical(outputPath, aggregate);
        SchemaValidation.Validate(
            outputPath,
            RepositoryPaths.ResolveConfined(context.Root, "schemas/validation/m1-host-validation-aggregate-evidence-v1.schema.json"),
            requireCanonical: true);
        PublicSafetyScanner.EnsureSafeBytes(File.ReadAllBytes(outputPath));
        return aggregate;
    }

    public static AggregateEvidence ValidateAggregate(
        string root,
        string evidencePath,
        string? reviewPath = null)
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
        var review = BundleValidator.ValidateReview(
            context.Root,
            reviewPath ?? BundleValidator.ReviewRelativePath,
            context.Lock.BundleId);
        if (evidence.BundleId != context.Lock.BundleId || evidence.ReviewId != review.ReviewId)
        {
            throw new ProtocolException("HV169_AGGREGATE_IDENTITY_MISMATCH");
        }

        var expectedCells = context.Protocol.RequiredCells.Select(cell => cell.CellId).Order(StringComparer.Ordinal).ToArray();
        var actualCells = evidence.Cells.Select(cell => cell.CellId).Order(StringComparer.Ordinal).ToArray();
        if (!actualCells.SequenceEqual(expectedCells, StringComparer.Ordinal)
            || actualCells.Distinct(StringComparer.Ordinal).Count() != actualCells.Length)
        {
            throw new ProtocolException("HV159_AGGREGATE_CELL_SET");
        }
        PublicSafetyScanner.EnsureSafeBytes(File.ReadAllBytes(evidencePath));
        return evidence;
    }

    public static void PreparePublicArtifact(
        string root,
        string kind,
        string sourcePath,
        string outputPath,
        string? reviewPath = null)
    {
        var expectedFileName = kind switch
        {
            "cell" => "cell-evidence.json",
            "aggregate" => "aggregate-evidence.json",
            "incomplete" => "incomplete-evidence.json",
            _ => throw new ProtocolException("HV170_PUBLIC_KIND_UNKNOWN")
        };
        if (!Path.GetFileName(outputPath).Equals(expectedFileName, StringComparison.Ordinal))
        {
            throw new ProtocolException("HV171_PUBLIC_ALLOWLIST");
        }

        switch (kind)
        {
            case "cell":
                _ = ValidateCell(root, sourcePath, reviewPath);
                break;
            case "aggregate":
                _ = ValidateAggregate(root, sourcePath, reviewPath);
                break;
            case "incomplete":
                _ = ValidateIncomplete(root, sourcePath, reviewPath);
                break;
        }

        var bytes = File.ReadAllBytes(sourcePath);
        PublicSafetyScanner.EnsureSafeBytes(bytes);
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))
            ?? throw new ProtocolException("HV172_PUBLIC_OUTPUT_INVALID");
        Directory.CreateDirectory(directory);
        CanonicalJson.WriteBytesAtomic(outputPath, bytes);
    }

    private static string SelectOutcome(
        IReadOnlyList<string> precedence,
        IEnumerable<string> cellOutcomes)
    {
        var outcomes = cellOutcomes.ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in precedence)
        {
            if (outcomes.Contains(candidate))
            {
                return candidate;
            }
        }
        throw new ProtocolException("HV162_AGGREGATE_OUTCOME_UNKNOWN");
    }
}
