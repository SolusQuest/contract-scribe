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
            || manifest.SourceConfiguration.SourceConfigurationId
                != BundleValidator.ComputeSourceConfigurationId(manifest.SourceConfiguration))
        {
            throw new ProtocolException("HV174_SUBJECT_IDENTITY_MISMATCH");
        }
        var expectedSourcePaths = BundleValidator.ExpandProtectedInputPaths(
            context.Root,
            manifest.SourceConfiguration.SourceRoots);
        var actualSourcePaths = manifest.SourceConfiguration.SourceAndBuildInputs
            .Select(identity => identity.Path)
            .ToArray();
        if (!actualSourcePaths.SequenceEqual(expectedSourcePaths, StringComparer.Ordinal)
            || actualSourcePaths.Distinct(StringComparer.Ordinal).Count() != actualSourcePaths.Length
            || !actualSourcePaths.Any(path => path.StartsWith("src/ContractScribe.", StringComparison.Ordinal))
            || manifest.SourceConfiguration.SourceAndBuildInputs.Any(identity =>
                identity.Path.Contains(".Experiment", StringComparison.Ordinal)
                || identity.Path.StartsWith("tests/ContractScribe.HostValidation/", StringComparison.Ordinal)))
        {
            throw new ProtocolException("HV190_SUBJECT_SOURCE_BOUNDARY");
        }

        if (!allowMaterializationDrift)
        {
            ValidateArtifactIdentities(context.Root, manifest.SourceConfiguration.SourceAndBuildInputs);
            ValidateArtifactIdentities(
                context.Root,
                [
                    manifest.SourceConfiguration.FailureRegistry,
                    manifest.SourceConfiguration.CalibratedBounds,
                    manifest.SourceConfiguration.BuildRecipe,
                    manifest.SourceConfiguration.CommandContract,
                    manifest.SourceConfiguration.ContractBaseline,
                    manifest.SourceConfiguration.EnvironmentPolicy,
                    manifest.SourceConfiguration.Workflow
                ]);
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
            foreach (var argument in cell.ArgumentPrefix)
            {
                PublicSafetyScanner.EnsureSafeText(argument);
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
                ValidateArtifactIdentities(context.Root, fixture.ArrangementInputs);
                foreach (var allowedRoot in fixture.AllowedDesignTimeRoots)
                {
                    _ = ResolveFixturePath(repositoryRoot, allowedRoot, mustExist: false);
                }
                if (fixture.CapabilityAvailable
                    && fixture.ExecutorKind is ("external-process" or "platform-fixture"))
                {
                    if (string.IsNullOrWhiteSpace(fixture.Executable)
                        || fixture.ArrangementInputs.Count == 0)
                    {
                        throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");
                    }
                    if (fixture.ExecutableSha256 is not null
                        && fixture.ExecutableSha256 != CanonicalJson.Sha256File(
                            ResolveExecutable(context.Root, repositoryRoot, fixture.Executable, mustExist: true)))
                    {
                        throw new ProtocolException("HV187_SUBJECT_ARTIFACT_DRIFT");
                    }
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
                if (vector.EqualityFields.Contains(
                        "subject.canonicalResultSha256",
                        StringComparer.Ordinal)
                    && string.IsNullOrWhiteSpace(fixture.ResultPath))
                {
                    throw new ProtocolException("HV222_CANONICAL_RESULT_PATH_REQUIRED");
                }
                var expectedExternalCause = fixture.VectorId switch
                {
                    "failure.out-of-memory" => "out-of-memory",
                    "failure.stack-overflow" => "stack-overflow",
                    "failure.abort" => "abort",
                    _ => null
                };
                if (fixture.CapabilityAvailable && fixture.ExternalCause != expectedExternalCause)
                {
                    throw new ProtocolException("HV195_EXTERNAL_CAUSE_MISMATCH");
                }
                if (fixture.ResultPath is not null)
                {
                    _ = ResolveFixturePath(repositoryRoot, fixture.ResultPath, mustExist: false);
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
        var before = RepositoryObserver.Capture(repositoryRoot, fixture.AllowedDesignTimeRoots);
        if (fixture.RepositoryIdentitySha256 != ComputeRepositoryIdentity(before))
        {
            throw new ProtocolException("HV180_FIXTURE_IDENTITY_MISMATCH");
        }

        var tempRoot = Path.Join(Path.GetTempPath(), $"contractscribe-hv-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
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
                repositoryRoot,
                context.Protocol.ExecutionContract.StandardOutputByteLimit,
                context.Protocol.ExecutionContract.StandardErrorByteLimit,
                TimeSpan.FromSeconds(context.Protocol.ExecutionContract.SubjectTimeoutSeconds),
                cancellationToken,
                control).ConfigureAwait(false);
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

            var resultCommitment = ObserveCanonicalResult(repositoryRoot, fixture.ResultPath);
            var process = new ProcessObservation(
                execution.ExitCode,
                execution.ProcessStart,
                fixture.ExternalCause is not null
                    && execution.ProcessTermination == "fatal-runtime-termination"
                    ? fixture.ExternalCause
                    : execution.ProcessTermination,
                execution.TimedOut,
                execution.ControlCompleted,
                execution.ObservationComplete);
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
                delta,
                execution.ObservedProcesses,
                diagnostics.Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal).ToArray());
            var derived = RunSemantics.Derive(
                context,
                vector,
                provisional,
                fixture,
                source);
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
            _ => argument
        }).ToArray();
        if (fixtureArguments.Any(string.IsNullOrEmpty))
        {
            throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");
        }
        return (fixtureExecutable, fixtureArguments);
    }

    private static CanonicalResultCommitment? ObserveCanonicalResult(
        string repositoryRoot,
        string? resultPath)
    {
        if (resultPath is null)
        {
            return null;
        }
        var fullPath = ResolveFixturePath(repositoryRoot, resultPath, mustExist: false);
        if (!File.Exists(fullPath))
        {
            return null;
        }
        var bytes = File.ReadAllBytes(fullPath);
        using var _ = CanonicalJson.ReadStrict(
            fullPath,
            4 * 1024 * 1024,
            requireCanonical: true);
        return new CanonicalResultCommitment(
            CanonicalJson.Sha256(bytes),
            bytes.LongLength,
            "canonical-json-utf8-no-bom-single-lf",
            true);
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
    }

    private static string ObserveToolVersion(string executable, IReadOnlyList<string> arguments)
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
        var value = output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (process.ExitCode != 0 || error.Length > 4096 || string.IsNullOrWhiteSpace(value))
        {
            throw new ProtocolException("HV211_EXECUTION_ENVIRONMENT_UNBOUND");
        }
        return value;
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
