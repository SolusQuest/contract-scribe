namespace ContractScribe.HostValidation;

public static class FrozenFixtureRegistry
{
    public const string ResultPath = "TestResults/audit-result.json";
    public const string StagingPath = "TestResults/.audit-result.json.contractscribe-stage";

    public static void Validate(
        string cellId,
        VectorDefinition vector,
        FixtureRealization fixture)
    {
        var expectedRoot = $"tests/fixtures/m1-host-validation/runtime/{cellId}/{vector.VectorId}";
        var expectedObservationMode = RunSemantics.RequiresSynchronizedTree(vector.VectorId)
            ? "synchronized-tree"
            : "bounded-polling";
        var requiresResultPath = vector.ObserverRequirements.Contains(
                "canonical-bytes",
                StringComparer.Ordinal)
            || vector.ObserverRequirements.Contains("artifact-state", StringComparer.Ordinal)
            || vector.VectorId == "bounds.temporary-disk";
        var expectedPrestate = vector.VectorId switch
        {
            "publication.stale-invalidation" => "stale-invalid",
            "failure.publication-invalidation" => "prior-valid",
            _ => "absent"
        };
        var expectedWorkingDirectories = vector.RunIds
            .Select(runId => new RunWorkingDirectory(
                runId,
                vector.VectorId == "path.working-directory-independent" && runId == "run-2"
                    ? "system-temp"
                    : "repository-root"))
            .ToArray();
        var expectedExternalCause = vector.VectorId switch
        {
            "failure.out-of-memory" => "out-of-memory",
            "failure.stack-overflow" => "stack-overflow",
            "failure.abort" => "abort",
            _ => null
        };

        if (fixture.RepositoryRoot != expectedRoot
            || fixture.CapabilityAvailable
                && fixture.RepositoryIdentitySha256
                    != FixtureRecipeRegistry.ExpectedRepositoryIdentity(cellId, vector)
            || !fixture.AllowedDesignTimeRoots.SequenceEqual(["obj"], StringComparer.Ordinal)
            || fixture.ProcessObservationMode != expectedObservationMode
            || fixture.ResultPath != (requiresResultPath ? ResultPath : null)
            || fixture.ResultPrestate != expectedPrestate
            || fixture.ExternalCause != expectedExternalCause
            || !fixture.RunWorkingDirectories.SequenceEqual(expectedWorkingDirectories))
        {
            throw new ProtocolException("HV234_FIXTURE_CONTRACT_MISMATCH");
        }
    }
}
