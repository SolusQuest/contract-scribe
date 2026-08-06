namespace ContractScribe.HostValidation;

public static class IncompleteEvidenceWriter
{
    public static IncompleteEvidence WriteTrusted(
        BundleContext context,
        ReviewRecord review,
        SubjectManifestSet manifests,
        string reviewPath,
        string commonManifestPath,
        string cellManifestPath,
        string outputPath,
        string diagnosticCode)
    {
        var classification = Classify(diagnosticCode);
        var evidence = new IncompleteEvidence(
            "contractscribe-m1-host-validation-incomplete-evidence-v1",
            context.Lock.BundleId,
            NetworkClaimSetRegistry.ClaimSetId,
            review.ReviewId,
            manifests.Common.SourceConfiguration.SourceConfigurationId,
            CanonicalJson.Sha256File(commonManifestPath),
            CanonicalJson.Sha256File(cellManifestPath),
            manifests.Common.ValidationAttempt,
            manifests.Cell.CellId,
            classification,
            [diagnosticCode],
            true);
        CanonicalJson.WriteCanonical(outputPath, evidence);
        _ = EvidenceValidator.ValidateIncomplete(
            context.Root,
            outputPath,
            reviewPath,
            commonManifestPath,
            cellManifestPath);
        return evidence;
    }

    public static string Classify(string diagnosticCode) =>
        diagnosticCode switch
        {
            "HV134_ARTIFACT_HASH_MISMATCH" or
            "HV165_PROTECTED_INPUT_DRIFT" or
            "HV180_FIXTURE_IDENTITY_MISMATCH" or
            "HV187_SUBJECT_ARTIFACT_DRIFT" or
            "HV246_NETWORK_PROTECTED_INPUT_INVALIDATED" => "protected-input-invalidated",
            "HV998_CANCELLED" => "harness-or-ci-cancelled",
            _ => "protocol-failure"
        };
}
