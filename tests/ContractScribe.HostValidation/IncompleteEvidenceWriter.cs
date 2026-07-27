namespace ContractScribe.HostValidation;

public static class IncompleteEvidenceWriter
{
    public static void TryWrite(
        string root,
        string subjectManifestPath,
        string reviewPath,
        string? cellId,
        string outputPath,
        string diagnosticCode,
        string classification)
    {
        try
        {
            var context = BundleValidator.Validate(
                root,
                requireReview: true,
                reviewPath,
                allowProtectedInputDrift: true);
            var review = BundleValidator.ValidateReview(context.Root, reviewPath, context.Lock.BundleId);
            var subject = CellExecutor.ValidateSubjectManifest(
                context,
                subjectManifestPath,
                allowMaterializationDrift: true);
            var evidence = new IncompleteEvidence(
                "contractscribe-m1-host-validation-incomplete-evidence-v1",
                context.Lock.BundleId,
                review.ReviewId,
                subject.SourceConfiguration.SourceConfigurationId,
                subject.ValidationAttempt,
                cellId,
                classification,
                [diagnosticCode],
                true);
            CanonicalJson.WriteCanonical(outputPath, evidence);
            SchemaValidation.Validate(
                outputPath,
                RepositoryPaths.ResolveConfined(context.Root, "schemas/validation/m1-host-validation-incomplete-evidence-v1.schema.json"),
                requireCanonical: true);
            PublicSafetyScanner.EnsureSafeBytes(File.ReadAllBytes(outputPath));
        }
        catch
        {
            // An untrusted or invalid identity cannot be used to manufacture evidence.
        }
    }

    public static string Classify(string diagnosticCode) =>
        diagnosticCode switch
        {
            "HV134_ARTIFACT_HASH_MISMATCH" or
            "HV165_PROTECTED_INPUT_DRIFT" or
            "HV180_FIXTURE_IDENTITY_MISMATCH" or
            "HV187_SUBJECT_ARTIFACT_DRIFT" => "protected-input-invalidated",
            "HV998_CANCELLED" => "harness-or-ci-cancelled",
            _ => "protocol-failure"
        };
}
