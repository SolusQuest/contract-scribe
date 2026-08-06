namespace ContractScribe.HostValidation;

public static class SubjectManifestMaterializer
{
    public const string CommonFileName = "common-source-manifest.json";
    public const string CellFileName = "cell-subject-manifest.json";

    private const string EntryPoint =
        "src/ContractScribe.Cli/bin/Release/net10.0/ContractScribe.Cli.dll";
    private const string ProductionOutputRoot =
        "src/ContractScribe.Cli/bin/Release/net10.0";
    private const string HarnessOutputRoot =
        "tests/ContractScribe.HostValidation/bin/Release/net10.0";

    public static CommonSourceManifest MaterializeCommon(
        string root,
        string reviewPath,
        string outputPath)
    {
        var context = BundleValidator.Validate(root, requireReview: true, reviewPath);
        var review = BundleValidator.ValidateReview(context.Root, reviewPath, context.Lock.BundleId);
        var executionSha = RequireLowerCommit(Environment.GetEnvironmentVariable("GITHUB_SHA"));
        var hostRevision = RequireLowerCommit(review.ReviewedSourceRevision);
        RequireGitHubActions();
        BundleValidator.ValidateCommitAncestry(context.Root, hostRevision, executionSha);

        var contract = context.Protocol.SubjectSourceContract;
        var sourcePaths = BundleValidator.ExpandCommitBoundPaths(
            context.Root,
            hostRevision,
            contract.SourceRoots);
        var materializedPaths = BundleValidator.ExpandProtectedInputPaths(
            context.Root,
            contract.SourceRoots);
        if (!sourcePaths.SequenceEqual(materializedPaths, StringComparer.Ordinal))
        {
            throw new ProtocolException("HV190_SUBJECT_SOURCE_BOUNDARY");
        }

        var sourceInputs = sourcePaths.Select(path => Identity(context.Root, path)).ToArray();
        var draft = new SubjectSourceConfiguration(
            "source." + new string('0', 64),
            hostRevision,
            "operations." + new string('0', 64),
            contract.SourceRoots,
            sourceInputs,
            Identity(context.Root, contract.FailureRegistry),
            Identity(context.Root, contract.CalibratedBounds),
            Identity(context.Root, contract.BuildRecipe),
            Identity(context.Root, contract.CommandContract),
            Identity(context.Root, contract.ContractBaseline),
            Identity(context.Root, contract.EnvironmentPolicy),
            Identity(context.Root, contract.Workflow));
        var withInventory = draft with
        {
            DeclaredOperationInventoryId = BundleValidator.ComputeDeclaredOperationInventoryId(draft)
        };
        var source = withInventory with
        {
            SourceConfigurationId = BundleValidator.ComputeSourceConfigurationId(withInventory)
        };
        var attempt = new ValidationAttemptIdentity(
            contract.Workflow,
            source.Workflow.Sha256,
            RequireRunId(Environment.GetEnvironmentVariable("GITHUB_RUN_ID")),
            RequireRunAttempt(Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT")),
            executionSha,
            hostRevision);
        var manifest = new CommonSourceManifest(
            "contractscribe-m1-host-validation-common-source-v1",
            context.Lock.BundleId,
            "production-host",
            "issue-24",
            "prebuilt-in-process-test-entrypoint",
            source,
            attempt);
        CanonicalJson.WriteCanonical(outputPath, manifest);
        _ = CellExecutor.ValidateCommonManifest(context, outputPath);
        return manifest;
    }

    public static CellSubjectManifest MaterializeCell(
        string root,
        string reviewPath,
        string commonManifestPath,
        string cellId,
        string outputPath)
    {
        var context = BundleValidator.Validate(root, requireReview: true, reviewPath);
        _ = BundleValidator.ValidateReview(context.Root, reviewPath, context.Lock.BundleId);
        var common = CellExecutor.ValidateCommonManifest(context, commonManifestPath);
        RequireGitHubActions();
        ValidateRunnerCell(context, cellId);
        var inventories = MaterializationInventories(context.Root);
        ValidateArtifactSet(context.Root, inventories, allowDrift: false);
        var protocolCell = context.Protocol.RequiredCells.Single(cell => cell.CellId == cellId);
        var materialization = new CellMaterialization(
            cellId,
            RequireToken(Environment.GetEnvironmentVariable("GITHUB_JOB")),
            CellExecutor.BuildWorkflowRunUrl(),
            CellExecutor.BuildRunnerImageIdentity(),
            protocolCell.Rid,
            protocolCell.Architecture,
            CellExecutor.ObserveToolVersion("dotnet", ["--version"]),
            Environment.Version.ToString(),
            CellExecutor.ObserveToolVersion("dotnet", ["msbuild", "-version", "-nologo"]),
            inventories.ProductionArtifacts,
            inventories.RuntimeDependencies,
            inventories.HarnessArtifacts);
        var fixtures = context.Vectors.Vectors
            .Where(vector => vector.ExecutorKind != "harness-static"
                && vector.Cells.Contains(cellId, StringComparer.Ordinal))
            .OrderBy(vector => vector.VectorId, StringComparer.Ordinal)
            .Select(vector => FrozenFixtureRegistry.Materialize(context.Root, cellId, vector))
            .ToArray();
        var cell = new CellSubjectManifest(
            "contractscribe-m1-host-validation-cell-subject-v1",
            CanonicalJson.Sha256File(commonManifestPath),
            cellId,
            new ExecutionCell(
                materialization,
                "dotnet-dll",
                EntryPoint,
                [],
                fixtures));
        CanonicalJson.WriteCanonical(outputPath, cell);
        _ = CellExecutor.ValidateSubjectManifests(context, commonManifestPath, outputPath);
        if (common.ValidationAttempt.ValidationExecutionSha
            != RequireLowerCommit(Environment.GetEnvironmentVariable("GITHUB_SHA")))
        {
            throw new ProtocolException("HV211_EXECUTION_ENVIRONMENT_UNBOUND");
        }
        return cell;
    }

    public static void ValidateArtifactSet(
        string root,
        CellMaterialization materialization,
        bool allowDrift)
    {
        if (!IsCanonicalInventory(materialization.ProductionArtifacts)
            || !IsCanonicalInventory(materialization.RuntimeDependencies)
            || !IsCanonicalInventory(materialization.HarnessArtifacts)
            || materialization.ProductionArtifacts
                .Concat(materialization.RuntimeDependencies)
                .Concat(materialization.HarnessArtifacts)
                .Select(identity => identity.Path)
                .Distinct(StringComparer.Ordinal).Count()
                != materialization.ProductionArtifacts.Count
                    + materialization.RuntimeDependencies.Count
                    + materialization.HarnessArtifacts.Count
            || !materialization.ProductionArtifacts.Any(identity => identity.Path == EntryPoint))
        {
            throw new ProtocolException("HV176_SUBJECT_CELL_MATERIALIZATION");
        }
        if (allowDrift)
        {
            return;
        }
        var expected = MaterializationInventories(root);
        if (!CanonicalJson.SerializeCanonical(expected.ProductionArtifacts).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(materialization.ProductionArtifacts))
            || !CanonicalJson.SerializeCanonical(expected.RuntimeDependencies).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(materialization.RuntimeDependencies))
            || !CanonicalJson.SerializeCanonical(expected.HarnessArtifacts).AsSpan()
                .SequenceEqual(CanonicalJson.SerializeCanonical(materialization.HarnessArtifacts)))
        {
            throw new ProtocolException("HV187_SUBJECT_ARTIFACT_DRIFT");
        }
    }

    private static CellMaterialization MaterializationInventories(string root)
    {
        var production = Inventory(root, ProductionOutputRoot);
        var productionArtifacts = production
            .Where(identity => Path.GetFileName(identity.Path)
                .StartsWith("ContractScribe.", StringComparison.Ordinal))
            .ToArray();
        var runtimeDependencies = production
            .Where(identity => !Path.GetFileName(identity.Path)
                .StartsWith("ContractScribe.", StringComparison.Ordinal))
            .ToArray();
        return new CellMaterialization(
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            productionArtifacts,
            runtimeDependencies,
            Inventory(root, HarnessOutputRoot));
    }

    private static IReadOnlyList<ArtifactIdentity> Inventory(string root, string relativeRoot)
    {
        var directory = RepositoryPaths.ResolveConfined(root, relativeRoot);
        if (!Directory.Exists(directory))
        {
            throw new ProtocolException("HV187_SUBJECT_ARTIFACT_DRIFT");
        }
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => new ArtifactIdentity(
                RepositoryPaths.ToRepositoryRelative(root, path),
                CanonicalJson.Sha256File(path)))
            .OrderBy(identity => identity.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsCanonicalInventory(IReadOnlyList<ArtifactIdentity> identities)
    {
        var ordered = identities.OrderBy(identity => identity.Path, StringComparer.Ordinal).ToArray();
        return identities.SequenceEqual(ordered)
            && identities.Select(identity => identity.Path).Distinct(StringComparer.Ordinal).Count()
                == identities.Count
            && identities.All(identity => identity.Sha256.Length == 64
                && identity.Sha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    private static ArtifactIdentity Identity(string root, string path) =>
        new(path, CanonicalJson.Sha256File(RepositoryPaths.ResolveConfined(root, path)));

    private static void RequireGitHubActions()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ProtocolException("HV211_EXECUTION_ENVIRONMENT_UNBOUND");
        }
    }

    private static void ValidateRunnerCell(BundleContext context, string cellId)
    {
        var runnerOs = Environment.GetEnvironmentVariable("RUNNER_OS");
        var runnerArch = Environment.GetEnvironmentVariable("RUNNER_ARCH");
        var expectedId = runnerOs switch
        {
            "Linux" when runnerArch == "X64" => "ubuntu-x64",
            "Windows" when runnerArch == "X64" => "windows-x64",
            _ => throw new ProtocolException("HV211_EXECUTION_ENVIRONMENT_UNBOUND")
        };
        if (cellId != expectedId
            || !context.Protocol.RequiredCells.Any(cell => cell.CellId == cellId))
        {
            throw new ProtocolException("HV211_EXECUTION_ENVIRONMENT_UNBOUND");
        }
    }

    private static string RequireLowerCommit(string? value)
    {
        if (value is null || value.Length != 40
            || !value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ProtocolException("HV211_EXECUTION_ENVIRONMENT_UNBOUND");
        }
        return value;
    }

    private static string RequireRunId(string? value)
    {
        if (value is null || !ulong.TryParse(value, out var parsed) || parsed == 0)
        {
            throw new ProtocolException("HV211_EXECUTION_ENVIRONMENT_UNBOUND");
        }
        return value;
    }

    private static int RequireRunAttempt(string? value)
    {
        if (!int.TryParse(value, out var parsed) || parsed is < 1 or > 1000)
        {
            throw new ProtocolException("HV211_EXECUTION_ENVIRONMENT_UNBOUND");
        }
        return parsed;
    }

    private static string RequireToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.')))
        {
            throw new ProtocolException("HV211_EXECUTION_ENVIRONMENT_UNBOUND");
        }
        return value;
    }
}
