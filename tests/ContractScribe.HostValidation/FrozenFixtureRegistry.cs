namespace ContractScribe.HostValidation;

public static class FrozenFixtureRegistry
{
    public const string ResultPath = "TestResults/audit-result.json";
    public const string StagingPath = "TestResults/.audit-result.json.contractscribe-stage";

    public static FixtureRealization Materialize(
        string root,
        string cellId,
        VectorDefinition vector)
    {
        var repositoryRoot = $"tests/fixtures/m1-host-validation/runtime/{cellId}/{vector.VectorId}";
        var files = FixtureRecipeRegistry.Files(cellId, vector);
        FrozenExecutorCommand? command = vector.ExecutorKind is "external-process" or "platform-fixture"
            ? FrozenExecutorCommandRegistry.Get(vector.VectorId)
            : null;
        var arrangementInputs = command is null
            ? []
            : command.ArrangementPaths
                .Select(path => new ArtifactIdentity(
                    $"{repositoryRoot}/{path}",
                    CanonicalJson.Sha256(files[path])))
                .ToArray();
        var executableSha256 = command?.Executable.StartsWith("repository:", StringComparison.Ordinal) == true
            ? CanonicalJson.Sha256(files[command.Executable["repository:".Length..]])
            : null;
        var requiresResultPath = vector.ObserverRequirements.Contains(
                "canonical-bytes",
                StringComparer.Ordinal)
            || vector.ObserverRequirements.Contains("artifact-state", StringComparer.Ordinal)
            || vector.VectorId == "bounds.temporary-disk";
        return new FixtureRealization(
            vector.VectorId,
            vector.ExecutorKind,
            repositoryRoot,
            FixtureRecipeRegistry.ExpectedRepositoryIdentity(cellId, vector),
            true,
            null,
            command?.Executable,
            command?.Arguments ?? [],
            executableSha256,
            arrangementInputs,
            ["obj"],
            RunSemantics.RequiresSynchronizedTree(vector.VectorId)
                ? "synchronized-tree"
                : "bounded-polling",
            requiresResultPath ? ResultPath : null,
            vector.VectorId switch
            {
                "publication.stale-invalidation" => "stale-invalid",
                "failure.publication-invalidation" => "prior-valid",
                _ => "absent"
            },
            vector.RunIds.Select(runId => new RunWorkingDirectory(
                runId,
                vector.VectorId == "path.working-directory-independent" && runId == "run-2"
                    ? "system-temp"
                    : "repository-root")).ToArray(),
            vector.VectorId switch
            {
                "failure.out-of-memory" => "out-of-memory",
                "failure.stack-overflow" => "stack-overflow",
                "failure.abort" => "abort",
                _ => null
            },
            []);
    }

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
