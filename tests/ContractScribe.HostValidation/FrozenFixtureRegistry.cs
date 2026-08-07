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
        var arrangementInputs = files
            .Select(pair => new ArtifactIdentity(
                $"{repositoryRoot}/{pair.Key}",
                CanonicalJson.Sha256(pair.Value)))
            .OrderBy(identity => identity.Path, StringComparer.Ordinal)
            .ToList();
        if (vector.ExecutorKind == "production-host"
            && vector.VectorId is not (
                "toolchain.missing-assets" or "toolchain.no-automatic-restore"))
        {
            arrangementInputs.Add(new ArtifactIdentity(
                $"{repositoryRoot}/obj/project.assets.json",
                CanonicalJson.Sha256(System.Text.Encoding.UTF8.GetBytes(
                    "synthetic-prepared-restore-assets"))));
            arrangementInputs = arrangementInputs
                .OrderBy(identity => identity.Path, StringComparer.Ordinal)
                .ToList();
        }
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

    public static FixtureRealization MaterializePrepared(
        string root,
        string cellId,
        VectorDefinition vector,
        IReadOnlyList<ProcessIdentityRule>? processIdentityRegistry = null)
    {
        var baseline = Materialize(root, cellId, vector);
        var repositoryRoot = RepositoryPaths.ResolveConfined(root, baseline.RepositoryRoot);
        var snapshot = RepositoryObserver.Capture(repositoryRoot, baseline.AllowedDesignTimeRoots);
        var arrangementInputs = snapshot.ProtectedFiles
            .Concat(snapshot.OtherFiles)
            .Concat(snapshot.AllowedDesignTimeFiles)
            .Where(pair =>
            {
                var path = Path.Join(
                    repositoryRoot,
                    pair.Key.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(path)
                    && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
            })
            .Select(pair => new ArtifactIdentity(
                $"{baseline.RepositoryRoot}/{pair.Key}",
                pair.Value))
            .OrderBy(identity => identity.Path, StringComparer.Ordinal)
            .ToArray();
        return baseline with
        {
            RepositoryIdentitySha256 = CellExecutor.ComputeRepositoryIdentity(snapshot),
            ArrangementInputs = arrangementInputs,
            ProcessIdentityRegistry = processIdentityRegistry ?? []
        };
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
            || fixture.RepositoryIdentitySha256.Length != 64
            || fixture.RepositoryIdentitySha256.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || !fixture.AllowedDesignTimeRoots.SequenceEqual(["obj"], StringComparer.Ordinal)
            || fixture.ProcessObservationMode != expectedObservationMode
            || fixture.ResultPath != (requiresResultPath ? ResultPath : null)
            || fixture.ResultPrestate != expectedPrestate
            || fixture.ExternalCause != expectedExternalCause
            || !fixture.RunWorkingDirectories.SequenceEqual(expectedWorkingDirectories))
        {
            throw new ProtocolException("HV234_FIXTURE_CONTRACT_MISMATCH");
        }
        var arrangementPaths = fixture.ArrangementInputs.Select(input => input.Path).ToArray();
        if (!arrangementPaths.SequenceEqual(
                arrangementPaths.Order(StringComparer.Ordinal),
                StringComparer.Ordinal)
            || arrangementPaths.Distinct(StringComparer.Ordinal).Count() != arrangementPaths.Length
            || fixture.ArrangementInputs.Any(input =>
                !input.Path.StartsWith($"{expectedRoot}/", StringComparison.Ordinal)))
        {
            throw new ProtocolException("HV234_FIXTURE_CONTRACT_MISMATCH");
        }
        foreach (var (path, bytes) in FixtureRecipeRegistry.Files(cellId, vector))
        {
            var identity = fixture.ArrangementInputs.SingleOrDefault(input =>
                input.Path == $"{expectedRoot}/{path}");
            if (identity is null || identity.Sha256 != CanonicalJson.Sha256(bytes))
            {
                throw new ProtocolException("HV234_FIXTURE_CONTRACT_MISMATCH");
            }
        }
        var hasRestoreAssets = fixture.ArrangementInputs.Any(input =>
            input.Path == $"{expectedRoot}/obj/project.assets.json");
        var requiresRestoreAssets = vector.ExecutorKind == "production-host"
            && vector.VectorId is not (
                "toolchain.missing-assets" or "toolchain.no-automatic-restore");
        if (hasRestoreAssets != requiresRestoreAssets)
        {
            throw new ProtocolException("HV234_FIXTURE_CONTRACT_MISMATCH");
        }
    }
}
