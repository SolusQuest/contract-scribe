namespace ContractScribe.HostValidation;

public static class CellExecutor
{
    public static async Task<CellEvidence> RunAsync(
        string root,
        string subjectManifestPath,
        string reviewPath,
        string cellId,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var context = BundleValidator.Validate(root, requireReview: true, reviewPath);
        var review = BundleValidator.ValidateReview(context.Root, reviewPath, context.Lock.BundleId);
        var manifest = ValidateSubjectManifest(context, subjectManifestPath);
        var cell = manifest.Cells.SingleOrDefault(cell => cell.Materialization.CellId == cellId)
            ?? throw new ProtocolException("HV173_EXECUTION_CELL_UNKNOWN");
        ValidateExecutionEnvironment(manifest, cell);
        var vectors = context.Vectors.Vectors
            .Where(vector => vector.Cells.Contains(cellId, StringComparer.Ordinal))
            .ToArray();
        var fixtures = cell.Fixtures.ToDictionary(fixture => fixture.VectorId, StringComparer.Ordinal);
        var runs = new List<RunEvidence>();
        foreach (var vector in vectors)
        {
            foreach (var runId in vector.RunIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                runs.Add(vector.ExecutorKind == "harness-static"
                    ? CreateStaticRun(context, vector, runId)
                    : await ExecuteSubjectRunAsync(
                        context,
                        cell,
                        vector,
                        runId,
                        fixtures[vector.VectorId],
                        manifest.SourceConfiguration,
                        cancellationToken).ConfigureAwait(false));
            }
        }

        var outcome = SelectCellOutcome(runs);
        var evidence = new CellEvidence(
            "contractscribe-m1-host-validation-cell-evidence-v1",
            context.Lock.BundleId,
            review.ReviewId,
            manifest.SourceConfiguration.SourceConfigurationId,
            CanonicalJson.Sha256File(subjectManifestPath),
            manifest.ValidationAttempt,
            cell.Materialization,
            runs
                .OrderBy(run => run.VectorId, StringComparer.Ordinal)
                .ThenBy(run => run.RunId, StringComparer.Ordinal)
                .ToArray(),
            outcome);
        CanonicalJson.WriteCanonical(outputPath, evidence);
        return EvidenceValidator.ValidateCell(context.Root, outputPath, reviewPath, subjectManifestPath);
    }

    public static ExecutionSubjectManifest ValidateSubjectManifest(
        BundleContext context,
        string subjectManifestPath,
        bool allowMaterializationDrift = false)
    {
        SchemaValidation.Validate(
            subjectManifestPath,
            RepositoryPaths.ResolveConfined(context.Root, "schemas/validation/m1-host-validation-subject-v1.schema.json"),
            requireCanonical: true);
        var manifest = CanonicalJson.DeserializeStrict<ExecutionSubjectManifest>(
            subjectManifestPath,
            context.Protocol.ExecutionContract.EvidenceByteLimit,
            requireCanonical: true);
        if (manifest.FormatVersion != "contractscribe-m1-host-validation-execution-subject-v1"
            || manifest.BundleId != context.Lock.BundleId
            || manifest.SubjectKind != "production-host"
            || manifest.ImplementationOwner != "issue-24"
            || manifest.EntryPointContract != "prebuilt-in-process-test-entrypoint"
            || manifest.SourceConfiguration.HostRevision != manifest.ValidationAttempt.HostRevision
            || manifest.SourceConfiguration.ContractBaseline.Sha256 != context.Protocol.Baseline.ContractManifestSha256
            || manifest.SourceConfiguration.Workflow.Sha256 != manifest.ValidationAttempt.WorkflowRevision
            || manifest.ValidationAttempt.Workflow != manifest.SourceConfiguration.Workflow.Path
            || manifest.SourceConfiguration.SourceConfigurationId
                != BundleValidator.ComputeSourceConfigurationId(manifest.SourceConfiguration))
        {
            throw new ProtocolException("HV174_SUBJECT_IDENTITY_MISMATCH");
        }
        var sourceContract = context.Protocol.SubjectSourceContract;
        if (!manifest.SourceConfiguration.SourceRoots.SequenceEqual(
                sourceContract.SourceRoots,
                StringComparer.Ordinal)
            || manifest.SourceConfiguration.FailureRegistry.Path != sourceContract.FailureRegistry
            || manifest.SourceConfiguration.CalibratedBounds.Path != sourceContract.CalibratedBounds
            || manifest.SourceConfiguration.BuildRecipe.Path != sourceContract.BuildRecipe
            || manifest.SourceConfiguration.CommandContract.Path != sourceContract.CommandContract
            || manifest.SourceConfiguration.ContractBaseline.Path != sourceContract.ContractBaseline
            || manifest.SourceConfiguration.EnvironmentPolicy.Path != sourceContract.EnvironmentPolicy
            || manifest.SourceConfiguration.Workflow.Path != sourceContract.Workflow)
        {
            throw new ProtocolException("HV190_SUBJECT_SOURCE_BOUNDARY");
        }
        var expectedSourcePaths = BundleValidator.ExpandCommitBoundPaths(
            context.Root,
            manifest.SourceConfiguration.HostRevision,
            sourceContract.SourceRoots);
        var materializedSourcePaths = BundleValidator.ExpandProtectedInputPaths(
            context.Root,
            sourceContract.SourceRoots);
        var actualSourcePaths = manifest.SourceConfiguration.SourceAndBuildInputs
            .Select(identity => identity.Path)
            .ToArray();
        if (!actualSourcePaths.SequenceEqual(expectedSourcePaths, StringComparer.Ordinal)
            || !materializedSourcePaths.SequenceEqual(expectedSourcePaths, StringComparer.Ordinal)
            || actualSourcePaths.Distinct(StringComparer.Ordinal).Count() != actualSourcePaths.Length
            || !actualSourcePaths.Any(path => path.StartsWith("src/ContractScribe.", StringComparison.Ordinal))
            || manifest.SourceConfiguration.SourceAndBuildInputs.Any(identity =>
                identity.Path.Contains(".Experiment", StringComparison.Ordinal)
                || identity.Path.StartsWith("tests/ContractScribe.HostValidation/", StringComparison.Ordinal)))
        {
            throw new ProtocolException("HV190_SUBJECT_SOURCE_BOUNDARY");
        }

        var namedSourceArtifacts = new[]
        {
            manifest.SourceConfiguration.FailureRegistry,
            manifest.SourceConfiguration.CalibratedBounds,
            manifest.SourceConfiguration.BuildRecipe,
            manifest.SourceConfiguration.CommandContract,
            manifest.SourceConfiguration.ContractBaseline,
            manifest.SourceConfiguration.EnvironmentPolicy,
            manifest.SourceConfiguration.Workflow
        };
        BundleValidator.ValidateCommitAncestry(
            context.Root,
            manifest.SourceConfiguration.HostRevision,
            manifest.ValidationAttempt.ValidationExecutionSha);
        BundleValidator.ValidateCommitBoundArtifacts(
            context.Root,
            manifest.SourceConfiguration.HostRevision,
            manifest.SourceConfiguration.SourceAndBuildInputs.Concat(namedSourceArtifacts));
        if (!allowMaterializationDrift)
        {
            ValidateArtifactIdentities(context.Root, manifest.SourceConfiguration.SourceAndBuildInputs);
            ValidateArtifactIdentities(context.Root, namedSourceArtifacts);
            ValidateHostOwnedRegistryAndBounds(context.Root, manifest.SourceConfiguration);
        }
        var expectedCells = context.Protocol.RequiredCells.Select(cell => cell.CellId).Order(StringComparer.Ordinal).ToArray();
        var actualCells = manifest.Cells.Select(cell => cell.Materialization.CellId).Order(StringComparer.Ordinal).ToArray();
        if (!actualCells.SequenceEqual(expectedCells, StringComparer.Ordinal)
            || actualCells.Distinct(StringComparer.Ordinal).Count() != actualCells.Length)
        {
            throw new ProtocolException("HV175_SUBJECT_CELL_SET");
        }

        foreach (var cell in manifest.Cells)
        {
            var protocolCell = context.Protocol.RequiredCells.Single(required => required.CellId == cell.Materialization.CellId);
            if (cell.Materialization.Rid != protocolCell.Rid
                || cell.Materialization.Architecture != protocolCell.Architecture)
            {
                throw new ProtocolException("HV176_SUBJECT_CELL_MATERIALIZATION");
            }
            if (!allowMaterializationDrift)
            {
                ValidateArtifactIdentities(context.Root, cell.Materialization.BuiltArtifacts);
            }
            var entryPoint = RepositoryPaths.ToRepositoryRelative(
                context.Root,
                RepositoryPaths.ResolveConfined(
                    context.Root,
                    cell.EntryPoint,
                    mustExist: !allowMaterializationDrift));
            if (!cell.Materialization.BuiltArtifacts.Any(artifact => artifact.Path == entryPoint))
            {
                throw new ProtocolException("HV177_SUBJECT_ENTRYPOINT_UNBOUND");
            }
            if (entryPoint.Contains(".Experiment", StringComparison.Ordinal)
                || entryPoint.StartsWith("tests/ContractScribe.HostValidation/", StringComparison.Ordinal))
            {
                throw new ProtocolException("HV190_SUBJECT_SOURCE_BOUNDARY");
            }
            if (cell.ArgumentPrefix.Count != 0)
            {
                throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");
            }

            var expectedFixtures = context.Vectors.Vectors
                .Where(vector => vector.ExecutorKind != "harness-static"
                    && vector.Cells.Contains(cell.Materialization.CellId, StringComparer.Ordinal))
                .Select(vector => vector.VectorId)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var actualFixtures = cell.Fixtures.Select(fixture => fixture.VectorId).Order(StringComparer.Ordinal).ToArray();
            if (!actualFixtures.SequenceEqual(expectedFixtures, StringComparer.Ordinal)
                || actualFixtures.Distinct(StringComparer.Ordinal).Count() != actualFixtures.Length)
            {
                throw new ProtocolException("HV178_FIXTURE_REALIZATION_SET");
            }

            foreach (var fixture in cell.Fixtures)
            {
                var vector = context.Vectors.Vectors.Single(candidate =>
                    candidate.VectorId == fixture.VectorId);
                if (fixture.ExecutorKind != vector.ExecutorKind
                    || fixture.CapabilityAvailable == (fixture.BlockedReasonCode is not null)
                    || fixture.ExecutorKind == "harness-static")
                {
                    throw new ProtocolException("HV179_FIXTURE_CAPABILITY_STATE");
                }
                FrozenFixtureRegistry.Validate(
                    cell.Materialization.CellId,
                    vector,
                    fixture);
                var identityRegistry = fixture.ProcessIdentityRegistry ?? [];
                if (fixture.CapabilityAvailable && fixture.ProcessIdentityRegistry is null
                    || identityRegistry.Select(rule => rule.FingerprintSha256)
                        .Distinct(StringComparer.Ordinal).Count() != identityRegistry.Count
                    || identityRegistry.Any(rule =>
                        rule.ArtifactKind is not (
                            "production-subject"
                            or "fixture-helper"
                            or "selected-toolchain")))
                {
                    throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");
                }
                var repositoryRoot = RepositoryPaths.ResolveConfined(
                    context.Root,
                    fixture.RepositoryRoot,
                    mustExist: !allowMaterializationDrift);
                if (!allowMaterializationDrift
                    && fixture.CapabilityAvailable
                    && fixture.RepositoryIdentitySha256 != ComputeRepositoryIdentity(
                        RepositoryObserver.Capture(repositoryRoot, fixture.AllowedDesignTimeRoots)))
                {
                    throw new ProtocolException("HV180_FIXTURE_IDENTITY_MISMATCH");
                }
                if (!allowMaterializationDrift)
                {
                    ValidateArtifactIdentities(context.Root, fixture.ArrangementInputs);
                }
                foreach (var allowedRoot in fixture.AllowedDesignTimeRoots)
                {
                    _ = ResolveFixturePath(repositoryRoot, allowedRoot, mustExist: false);
                }
                if (fixture.CapabilityAvailable
                    && fixture.ExecutorKind is ("external-process" or "platform-fixture"))
                {
                    ValidateExecutorArrangement(
                        context.Root,
                        repositoryRoot,
                        cell,
                        fixture,
                        allowMaterializationDrift);
                }
                if (fixture.CapabilityAvailable
                    && RunSemantics.RequiresSynchronizedTree(vector.VectorId)
                    && fixture.ProcessObservationMode != "synchronized-tree")
                {
                    throw new ProtocolException("HV207_PROCESS_OBSERVATION_INCOMPLETE");
                }
                if (fixture.VectorId is "publication.kill-before-commit" or "publication.kill-after-commit"
                    && string.IsNullOrWhiteSpace(fixture.ResultPath))
                {
                    throw new ProtocolException("HV181_KILL_RESULT_PATH_REQUIRED");
                }
                if ((vector.EqualityFields.Contains(
                        "subject.canonicalResultSha256",
                        StringComparer.Ordinal)
                    || vector.ExecutorKind == "production-host"
                        && vector.ObserverRequirements.Contains("canonical-bytes", StringComparer.Ordinal))
                    && string.IsNullOrWhiteSpace(fixture.ResultPath))
                {
                    throw new ProtocolException("HV222_CANONICAL_RESULT_PATH_REQUIRED");
                }
                if (fixture.ResultPath is not null)
                {
                    EnsureFixtureResultPathSafe(
                        repositoryRoot,
                        ResolveFixturePath(repositoryRoot, fixture.ResultPath, mustExist: false));
                }
            }
        }

        return manifest;
    }

    public static string ComputeRepositoryIdentity(RepositorySnapshot snapshot)
    {
        var identity = new
        {
            protectedFiles = snapshot.ProtectedFiles.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            otherFiles = snapshot.OtherFiles.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            allowedDesignTimeFiles = snapshot.AllowedDesignTimeFiles.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        };
        return CanonicalJson.Sha256(CanonicalJson.SerializeCanonical(identity));
    }

    private static async Task<RunEvidence> ExecuteSubjectRunAsync(
        BundleContext context,
        ExecutionCell cell,
        VectorDefinition vector,
        string runId,
        FixtureRealization fixture,
        SubjectSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        if (!fixture.CapabilityAvailable)
        {
            return CreateBlockedRun(vector, runId, fixture.BlockedReasonCode!);
        }

        var repositoryRoot = RepositoryPaths.ResolveConfined(context.Root, fixture.RepositoryRoot);
        var tempRoot = Path.Join(Path.GetTempPath(), $"contractscribe-hv-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var resultPath = fixture.ResultPath is null
            ? null
            : ResolveFixturePath(repositoryRoot, fixture.ResultPath, mustExist: false);
        if (resultPath is not null)
        {
            EnsureFixtureResultPathSafe(repositoryRoot, resultPath);
        }
        PrepareResultPrestate(resultPath, fixture.ResultPrestate);
        var before = RepositoryObserver.Capture(repositoryRoot, fixture.AllowedDesignTimeRoots);
        if (fixture.RepositoryIdentitySha256 != ComputeRepositoryIdentity(before))
        {
            throw new ProtocolException("HV180_FIXTURE_IDENTITY_MISMATCH");
        }

        var requestPath = Path.Join(tempRoot, "request.json");
        var responsePath = Path.Join(tempRoot, "response.json");
        var control = CreateControl(vector, tempRoot, context.Protocol.ExecutionContract.SubjectTimeoutSeconds);
        var request = new SubjectRequest(
            "contractscribe-m1-host-validation-subject-request-v1",
            "production-host",
            vector.VectorId,
            runId,
            repositoryRoot,
            responsePath,
            control?.ControlRoot,
            control is null ? [] : [control.GateName],
            control?.Action ?? "continue");
        CanonicalJson.WriteCanonical(requestPath, request);
        SchemaValidation.ValidateDefinition(
            requestPath,
            RepositoryPaths.ResolveConfined(
                context.Root,
                "schemas/validation/m1-host-validation-subject-v1.schema.json"),
            "subjectRequest",
            requireCanonical: true);

        try
        {
            var (executable, arguments) = BuildInvocation(
                context.Root,
                repositoryRoot,
                cell,
                vector,
                fixture,
                requestPath,
                responsePath,
                control?.ControlRoot);
            var execution = await SubjectProcessRunner.RunAsync(
                executable,
                arguments,
                fixture.RunWorkingDirectories.Single(item => item.RunId == runId).Mode
                    == "system-temp"
                    ? tempRoot
                    : repositoryRoot,
                context.Protocol.ExecutionContract.StandardOutputByteLimit,
                context.Protocol.ExecutionContract.StandardErrorByteLimit,
                TimeSpan.FromSeconds(context.Protocol.ExecutionContract.SubjectTimeoutSeconds),
                cancellationToken,
                control,
                fixture.ProcessIdentityRegistry).ConfigureAwait(false);
            var after = RepositoryObserver.Capture(repositoryRoot, fixture.AllowedDesignTimeRoots);
            var delta = RepositoryObserver.Compare(before, after);
            var diagnostics = ValidateStreams(execution);
            SubjectResponse? response = null;
            if (File.Exists(responsePath))
            {
                try
                {
                    SchemaValidation.ValidateDefinition(
                        responsePath,
                        RepositoryPaths.ResolveConfined(
                            context.Root,
                            "schemas/validation/m1-host-validation-subject-v1.schema.json"),
                        "subjectResponse",
                        requireCanonical: true);
                    response = CanonicalJson.DeserializeStrict<SubjectResponse>(
                        responsePath,
                        context.Protocol.ExecutionContract.ResponseByteLimit,
                        requireCanonical: true);
                    if (response.VectorId != vector.VectorId || response.RunId != runId)
                    {
                        diagnostics.Add("HV182_SUBJECT_RESPONSE_IDENTITY");
                        response = null;
                    }
                }
                catch (ProtocolException exception)
                {
                    diagnostics.Add(exception.Code);
                }
            }

            var (resultCommitment, resultFacts) = ObserveCanonicalResult(
                context.Root,
                repositoryRoot,
                fixture.ResultPath);
            var process = new ProcessObservation(
                execution.ExitCode,
                execution.ProcessStart,
                fixture.ExternalCause is not null
                    && execution.ProcessTermination == "fatal-runtime-termination"
                    ? fixture.ExternalCause
                    : execution.ProcessTermination,
                execution.TimedOut,
                execution.ControlCompleted,
                execution.ObservationComplete,
                execution.ControlCompleted ? control?.GateName : null,
                execution.ControlCompleted ? control?.Action : null,
                execution.ControlCompleted && control?.Action == "observe");
            var provisional = new RunEvidence(
                vector.VectorId,
                runId,
                "protocol-invalid-observation",
                vector.ExpectedObservation,
                "unvalidated",
                vector.ExpectedEnforcementClass,
                response?.EnforcementClass ?? vector.ExpectedEnforcementClass,
                response,
                process,
                resultCommitment,
                resultFacts,
                delta,
                execution.ObservedProcesses,
                diagnostics.Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal).ToArray());
            var derived = RunSemantics.Derive(
                context,
                vector,
                provisional,
                fixture,
                source,
                cell.Materialization);
            return provisional with
            {
                Verdict = derived.Verdict,
                ObservedObservation = derived.Observation,
                ObservedEnforcementClass = derived.EnforcementClass,
                DiagnosticCodes = derived.DiagnosticCodes
            };
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Evidence records the run outcome; temporary workspace cleanup is best-effort.
            }
        }
    }

    private static void PrepareResultPrestate(string? resultPath, string prestate)
    {
        if (resultPath is null)
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        if (File.Exists(resultPath))
        {
            File.Delete(resultPath);
        }
        if (prestate == "stale-invalid")
        {
            File.WriteAllText(resultPath, "stale-invalid\n");
        }
    }

    private static void EnsureFixtureResultPathSafe(string repositoryRoot, string resultPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var current = new FileInfo(resultPath) as FileSystemInfo;
        while (current is not null
            && !string.Equals(
                Path.TrimEndingDirectorySeparator(current.FullName),
                root,
                StringComparison.OrdinalIgnoreCase))
        {
            if (current.Exists
                && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ProtocolException("HV188_FIXTURE_PATH_INVALID");
            }
            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null
            };
        }
        if (current is null)
        {
            throw new ProtocolException("HV188_FIXTURE_PATH_INVALID");
        }
    }

    private static RunEvidence CreateStaticRun(
        BundleContext context,
        VectorDefinition vector,
        string runId)
    {
        var result = StaticValidatorRegistry.Execute(context.Root, vector);
        return new RunEvidence(
            vector.VectorId,
            runId,
            "matched",
            vector.ExpectedObservation,
            result.ObservationCode,
            vector.ExpectedEnforcementClass,
            result.EnforcementClass,
            null,
            new ProcessObservation(null, "not-started", "not-started", false, true, true),
            null,
            null,
            EmptyDelta(),
            [],
            result.DiagnosticCodes);
    }

    private static RunEvidence CreateBlockedRun(VectorDefinition vector, string runId, string reasonCode)
    {
        return new RunEvidence(
            vector.VectorId,
            runId,
            "vector-environment-blocked",
            vector.ExpectedObservation,
            "vector.capability-unavailable",
            vector.ExpectedEnforcementClass,
            vector.ExpectedEnforcementClass,
            null,
            new ProcessObservation(null, "not-started", "not-started", false, true, true),
            null,
            null,
            EmptyDelta(),
            [],
            [reasonCode]);
    }

    private static SubjectControl? CreateControl(VectorDefinition vector, string tempRoot, int timeoutSeconds) =>
        vector.VectorId switch
        {
            "cancellation.before-commit" => new(Path.Join(tempRoot, "control"), "before-commit", "cancel", TimeSpan.FromSeconds(timeoutSeconds)),
            "cancellation.after-commit" => new(Path.Join(tempRoot, "control"), "after-commit", "cancel", TimeSpan.FromSeconds(timeoutSeconds)),
            "cancellation.late-completion" => new(Path.Join(tempRoot, "control"), "late-completion", "release-late-completion", TimeSpan.FromSeconds(timeoutSeconds)),
            "cancellation.terminal-precedence" => new(Path.Join(tempRoot, "control"), "before-commit", "cancel", TimeSpan.FromSeconds(timeoutSeconds)),
            "publication.kill-before-commit" => new(Path.Join(tempRoot, "control"), "publication-before-commit", "external-kill", TimeSpan.FromSeconds(timeoutSeconds)),
            "publication.kill-after-commit" => new(Path.Join(tempRoot, "control"), "publication-after-commit", "external-kill", TimeSpan.FromSeconds(timeoutSeconds)),
            "bounds.forced-termination" => new(Path.Join(tempRoot, "control"), "forced-termination", "external-kill", TimeSpan.FromSeconds(timeoutSeconds)),
            "toolchain.process-topology"
                or "toolchain.no-automatic-restore"
                or "network.no-contractscribe-initiated-operation"
                or "toolchain.owned-subprocesses" =>
                new(Path.Join(tempRoot, "control"), "process-observation", "observe", TimeSpan.FromSeconds(timeoutSeconds)),
            _ => null
        };

    private static (string Executable, IReadOnlyList<string> Arguments) BuildInvocation(
        string root,
        string repositoryRoot,
        ExecutionCell cell,
        VectorDefinition vector,
        FixtureRealization fixture,
        string requestPath,
        string responsePath,
        string? controlRoot)
    {
        if (vector.ExecutorKind == "production-host")
        {
            return BuildSubjectInvocation(root, cell, requestPath, responsePath);
        }
        if (fixture.Executable == "subject-entrypoint")
        {
            return BuildSubjectInvocation(root, cell, requestPath, responsePath);
        }

        var fixtureExecutable = ResolveExecutable(
            root,
            repositoryRoot,
            fixture.Executable!,
            mustExist: vector.VectorId != "failure.launch-before-entry");
        var fixtureArguments = fixture.Arguments.Select(argument => argument switch
        {
            "{request}" => requestPath,
            "{response}" => responsePath,
            "{repository}" => repositoryRoot,
            "{control}" => controlRoot ?? string.Empty,
            _ when argument.StartsWith("repository:", StringComparison.Ordinal) =>
                ResolveFixturePath(
                    repositoryRoot,
                    argument["repository:".Length..],
                    mustExist: true),
            _ => argument
        }).ToArray();
        if (fixtureArguments.Any(string.IsNullOrEmpty))
        {
            throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");
        }
        return (fixtureExecutable, fixtureArguments);
    }

    private static (string Executable, IReadOnlyList<string> Arguments) BuildSubjectInvocation(
        string root,
        ExecutionCell cell,
        string requestPath,
        string responsePath)
    {
        var entryPoint = RepositoryPaths.ResolveConfined(root, cell.EntryPoint);
        var arguments = new List<string>();
        var executable = cell.LaunchKind == "dotnet-dll" ? "dotnet" : entryPoint;
        if (cell.LaunchKind == "dotnet-dll")
        {
            arguments.Add(entryPoint);
        }
        arguments.AddRange(cell.ArgumentPrefix);
        arguments.Add("--request");
        arguments.Add(requestPath);
        arguments.Add("--response");
        arguments.Add(responsePath);
        return (executable, arguments);
    }

    public static void ValidateExecutorArrangement(
        string root,
        string repositoryRoot,
        ExecutionCell cell,
        FixtureRealization fixture,
        bool allowMaterializationDrift)
    {
        if (string.IsNullOrWhiteSpace(fixture.Executable))
        {
            throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");
        }

        var command = FrozenExecutorCommandRegistry.Get(fixture.VectorId);
        if (fixture.Executable != command.Executable
            || !fixture.Arguments.SequenceEqual(command.Arguments, StringComparer.Ordinal))
        {
            throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");
        }
        var expectedArrangementPaths = command.ArrangementPaths
            .Select(path => RepositoryPaths.ToRepositoryRelative(
                root,
                ResolveFixturePath(
                    repositoryRoot,
                    path,
                    mustExist: !allowMaterializationDrift)))
            .ToArray();
        if (!fixture.ArrangementInputs.Select(identity => identity.Path)
            .SequenceEqual(expectedArrangementPaths, StringComparer.Ordinal))
        {
            throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");
        }

        if (command.Executable == "subject-entrypoint")
        {
            if (fixture.ExecutableSha256 is not null)
            {
                throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");
            }
            return;
        }

        if (fixture.VectorId == "failure.launch-before-entry")
        {
            if (fixture.Executable != "missing-executable"
                || fixture.ExecutableSha256 is not null
                || fixture.Arguments.Count != 0)
            {
                throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");
            }
            return;
        }

        var boundPaths = fixture.ArrangementInputs
            .Concat(cell.Materialization.BuiltArtifacts)
            .Select(identity => identity.Path)
            .ToHashSet(StringComparer.Ordinal);
        if (fixture.Executable == "dotnet")
        {
            if (fixture.ExecutableSha256 is not null
                || fixture.Arguments.Count == 0
                || !fixture.Arguments[0].StartsWith("repository:", StringComparison.Ordinal)
                || !fixture.Arguments[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");
            }
        }
        else
        {
            if (!fixture.Executable.StartsWith("repository:", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(fixture.ExecutableSha256))
            {
                throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");
            }
            var executablePath = ResolveFixturePath(
                repositoryRoot,
                fixture.Executable["repository:".Length..],
                mustExist: !allowMaterializationDrift);
            var relativeExecutable = RepositoryPaths.ToRepositoryRelative(root, executablePath);
            if (!boundPaths.Contains(relativeExecutable))
            {
                throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");
            }
            if (!allowMaterializationDrift
                && fixture.ExecutableSha256 != CanonicalJson.Sha256File(executablePath))
            {
                throw new ProtocolException("HV187_SUBJECT_ARTIFACT_DRIFT");
            }
        }

        foreach (var argument in fixture.Arguments)
        {
            if (argument is "{request}" or "{response}" or "{repository}" or "{control}")
            {
                continue;
            }
            if (argument.StartsWith("repository:", StringComparison.Ordinal))
            {
                var argumentPath = ResolveFixturePath(
                    repositoryRoot,
                    argument["repository:".Length..],
                    mustExist: !allowMaterializationDrift);
                if (!boundPaths.Contains(RepositoryPaths.ToRepositoryRelative(root, argumentPath)))
                {
                    throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");
                }
                continue;
            }
            throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");
        }
    }

    private static (CanonicalResultCommitment? Commitment, ObservedAuditResultFacts? Facts) ObserveCanonicalResult(
        string root,
        string repositoryRoot,
        string? resultPath)
    {
        if (resultPath is null)
        {
            return (null, null);
        }
        var fullPath = ResolveFixturePath(repositoryRoot, resultPath, mustExist: false);
        if (!File.Exists(fullPath))
        {
            return (null, null);
        }
        var bytes = File.ReadAllBytes(fullPath);
        using var document = CanonicalJson.ReadStrict(
            fullPath,
            4 * 1024 * 1024,
            requireCanonical: true);
        SchemaValidation.Validate(
            fullPath,
            RepositoryPaths.ResolveConfined(root, "schemas/audit-result/v1.schema.json"),
            requireCanonical: true);
        var rootElement = document.RootElement;
        AuditResultSemanticValidator.Validate(root, rootElement);
        var facts = new ObservedAuditResultFacts(
            rootElement.GetProperty("auditResultVersion").GetInt32(),
            rootElement.GetProperty("policyConfigurationVersion").GetInt32(),
            rootElement.GetProperty("taxonomyRegistryVersion").GetInt32(),
            rootElement.GetProperty("targetProfile").GetString()
                ?? throw new ProtocolException("HV223_AUDIT_RESULT_INVALID"),
            rootElement.GetProperty("results").EnumerateArray()
                .Select(result => result.GetProperty("auditOutcome").GetString()
                    ?? throw new ProtocolException("HV223_AUDIT_RESULT_INVALID"))
                .Order(StringComparer.Ordinal)
                .ToArray());
        return (new CanonicalResultCommitment(
            CanonicalJson.Sha256(bytes),
            bytes.LongLength,
            "canonical-json-utf8-no-bom-single-lf",
            true), facts);
    }

    private static string ResolveExecutable(
        string root,
        string repositoryRoot,
        string executable,
        bool mustExist)
    {
        if (executable == "dotnet")
        {
            return executable;
        }
        if (executable == "missing-executable")
        {
            return Path.Join(repositoryRoot, ".contractscribe-validation-missing-executable");
        }
        if (executable.StartsWith("repository:", StringComparison.Ordinal))
        {
            return ResolveFixturePath(repositoryRoot, executable["repository:".Length..], mustExist);
        }
        return RepositoryPaths.ResolveConfined(root, executable, mustExist);
    }

    private static void ValidateExecutionEnvironment(
        ExecutionSubjectManifest manifest,
        ExecutionCell cell)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProtocolException("HV211_EXECUTION_ENVIRONMENT_UNBOUND");
        }

        var attempt = manifest.ValidationAttempt;
        var expectedRunnerOs = cell.Materialization.CellId == "windows-x64" ? "Windows" : "Linux";
        var observedSdk = ObserveToolVersion("dotnet", ["--version"]);
        var observedMsbuild = ObserveToolVersion("dotnet", ["msbuild", "-version", "-nologo"]);
        var observedRuntime = Environment.Version.ToString();
        if (Environment.GetEnvironmentVariable("GITHUB_RUN_ID") != attempt.WorkflowRunId
            || !int.TryParse(Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"), out var runAttempt)
            || runAttempt != attempt.RunAttempt
            || Environment.GetEnvironmentVariable("GITHUB_SHA") != attempt.ValidationExecutionSha
            || Environment.GetEnvironmentVariable("CONTRACTSCRIBE_VALIDATION_JOB_ID") != cell.Materialization.JobId
            || Environment.GetEnvironmentVariable("CONTRACTSCRIBE_VALIDATION_JOB_URL") != cell.Materialization.JobUrl
            || Environment.GetEnvironmentVariable("CONTRACTSCRIBE_RUNNER_IMAGE") != cell.Materialization.RunnerImage
            || Environment.GetEnvironmentVariable("RUNNER_OS") != expectedRunnerOs
            || Environment.GetEnvironmentVariable("RUNNER_ARCH") != cell.Materialization.Architecture
            || cell.Materialization.SelectedSdk != observedSdk
            || cell.Materialization.SelectedRuntime != observedRuntime
            || cell.Materialization.SelectedMsbuild != observedMsbuild)
        {
            throw new ProtocolException("HV211_EXECUTION_ENVIRONMENT_UNBOUND");
        }

        var builtArtifactHashes = cell.Materialization.BuiltArtifacts
            .Select(artifact => artifact.Sha256)
            .ToHashSet(StringComparer.Ordinal);
        var fixtureArtifactHashes = cell.Fixtures
            .SelectMany(fixture => fixture.ArrangementInputs)
            .Select(artifact => artifact.Sha256)
            .ToHashSet(StringComparer.Ordinal);
        var selectedToolchainHashes = ObserveSelectedToolchainHashes();
        foreach (var rule in cell.Fixtures.SelectMany(fixture =>
                     fixture.ProcessIdentityRegistry ?? []))
        {
            var bound = rule.ArtifactKind switch
            {
                "production-subject" => builtArtifactHashes.Contains(rule.EntryPointSha256),
                "fixture-helper" => fixtureArtifactHashes.Contains(rule.EntryPointSha256),
                "selected-toolchain" => selectedToolchainHashes.Contains(rule.EntryPointSha256),
                _ => false
            };
            if (!bound)
            {
                throw new ProtocolException("HV233_PROCESS_IDENTITY_UNBOUND");
            }
        }
    }

    private static IReadOnlySet<string> ObserveSelectedToolchainHashes()
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        if (Environment.ProcessPath is { } processPath && File.Exists(processPath))
        {
            hashes.Add(CanonicalJson.Sha256File(processPath));
        }

        var info = ObserveToolOutput("dotnet", ["--info"]);
        var basePath = info
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.StartsWith("Base Path:", StringComparison.OrdinalIgnoreCase)
                ? line["Base Path:".Length..].Trim()
                : null)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (basePath is null || !Path.IsPathFullyQualified(basePath))
        {
            throw new ProtocolException("HV211_EXECUTION_ENVIRONMENT_UNBOUND");
        }

        foreach (var relative in new[]
                 {
                     "MSBuild.dll",
                     "Roslyn/bincore/csc.dll",
                     "Roslyn/bincore/vbc.dll",
                     "Roslyn/bincore/VBCSCompiler.dll"
                 })
        {
            var path = Path.Join(basePath, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                hashes.Add(CanonicalJson.Sha256File(path));
            }
        }
        return hashes;
    }

    private static string ObserveToolVersion(string executable, IReadOnlyList<string> arguments)
    {
        var output = ObserveToolOutput(executable, arguments);
        var value = output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ProtocolException("HV211_EXECUTION_ENVIRONMENT_UNBOUND");
        }
        return value;
    }

    private static string ObserveToolOutput(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new ProtocolException("HV211_EXECUTION_ENVIRONMENT_UNBOUND");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || error.Length > 4096 || output.Length > 64 * 1024)
        {
            throw new ProtocolException("HV211_EXECUTION_ENVIRONMENT_UNBOUND");
        }
        return output;
    }

    private static List<string> ValidateStreams(ProcessExecutionResult execution)
    {
        var diagnostics = new List<string>();
        if (execution.StandardOutputOverflow) diagnostics.Add("HV183_STDOUT_OVERFLOW");
        if (execution.StandardErrorOverflow) diagnostics.Add("HV184_STDERR_OVERFLOW");
        if (!execution.StandardOutputValidUtf8) diagnostics.Add("HV185_STDOUT_INVALID_UTF8");
        if (!execution.StandardErrorValidUtf8) diagnostics.Add("HV186_STDERR_INVALID_UTF8");
        try
        {
            PublicSafetyScanner.EnsureSafeBytes(execution.StandardOutput);
            PublicSafetyScanner.EnsureSafeBytes(execution.StandardError);
        }
        catch (ProtocolException exception)
        {
            diagnostics.Add(exception.Code);
        }
        return diagnostics;
    }

    private static void ValidateArtifactIdentities(string root, IEnumerable<ArtifactIdentity> identities)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            if (!paths.Add(identity.Path)
                || identity.Sha256 != CanonicalJson.Sha256File(RepositoryPaths.ResolveConfined(root, identity.Path)))
            {
                throw new ProtocolException("HV187_SUBJECT_ARTIFACT_DRIFT");
            }
        }
    }

    private static void ValidateHostOwnedRegistryAndBounds(
        string root,
        SubjectSourceConfiguration source)
    {
        var registryPath = RepositoryPaths.ResolveConfined(root, source.FailureRegistry.Path);
        SchemaValidation.Validate(
            registryPath,
            RepositoryPaths.ResolveConfined(
                root,
                "schemas/validation/m1-host-failure-registry-v1.schema.json"),
            requireCanonical: true);
        using var registry = CanonicalJson.ReadStrict(registryPath, 1024 * 1024, requireCanonical: true);
        var failureCodes = registry.RootElement.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("code").GetString())
            .ToArray();
        if (failureCodes.Distinct(StringComparer.Ordinal).Count() != failureCodes.Length)
        {
            throw new ProtocolException("HV228_HOST_REGISTRY_INVALID");
        }

        var boundsPath = RepositoryPaths.ResolveConfined(root, source.CalibratedBounds.Path);
        SchemaValidation.Validate(
            boundsPath,
            RepositoryPaths.ResolveConfined(
                root,
                "schemas/validation/m1-host-calibrated-bounds-v1.schema.json"),
            requireCanonical: true);
        using var bounds = CanonicalJson.ReadStrict(boundsPath, 1024 * 1024, requireCanonical: true);
        var names = bounds.RootElement.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString())
            .ToArray();
        var expectedNames = new[]
        {
            "diagnostic-count",
            "diagnostic-utf8-bytes",
            "graceful-shutdown-timeout",
            "sdk-discovery-timeout",
            "temporary-disk-bytes",
            "toolchain-subprocess-count",
            "total-audit-timeout",
            "workspace-load-timeout"
        };
        if (!names.Order(StringComparer.Ordinal).SequenceEqual(expectedNames, StringComparer.Ordinal)
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
        {
            throw new ProtocolException("HV229_HOST_BOUNDS_INVALID");
        }
    }

    private static string ResolveFixturePath(string repositoryRoot, string relativePath, bool mustExist)
    {
        if (Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || relativePath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ProtocolException("HV188_FIXTURE_PATH_INVALID");
        }
        var path = Path.GetFullPath(Path.Join(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(repositoryRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
        {
            throw new ProtocolException("HV188_FIXTURE_PATH_INVALID");
        }
        if (mustExist && !File.Exists(path) && !Directory.Exists(path))
        {
            throw new ProtocolException("HV189_FIXTURE_PATH_MISSING");
        }
        return path;
    }

    private static RepositoryDelta EmptyDelta() =>
        new([], [], [], [], [], [], [], [], []);

    private static string SelectCellOutcome(IEnumerable<RunEvidence> runs)
    {
        var verdicts = runs.Select(run => run.Verdict).ToHashSet(StringComparer.Ordinal);
        if (verdicts.Contains("protocol-invalid-observation")) return "protocol-failure";
        if (verdicts.Contains("subject-nonconformance")) return "subject-nonconformance";
        if (verdicts.Contains("vector-environment-blocked")
            || verdicts.Contains("vector-infrastructure-incomplete"))
        {
            return "environment-or-infrastructure-incomplete";
        }
        return "passed";
    }
}
