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
                    ? CreateStaticRun(vector, runId)
                    : await ExecuteSubjectRunAsync(
                        context,
                        cell,
                        vector,
                        runId,
                        fixtures[vector.VectorId],
                        cancellationToken).ConfigureAwait(false));
            }
        }

        var outcome = SelectCellOutcome(runs);
        var evidence = new CellEvidence(
            "contractscribe-m1-host-validation-cell-evidence-v1",
            context.Lock.BundleId,
            review.ReviewId,
            manifest.ValidationExecution,
            cell.Materialization,
            runs
                .OrderBy(run => run.VectorId, StringComparer.Ordinal)
                .ThenBy(run => run.RunId, StringComparer.Ordinal)
                .ToArray(),
            outcome);
        CanonicalJson.WriteCanonical(outputPath, evidence);
        return EvidenceValidator.ValidateCell(context.Root, outputPath, reviewPath);
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
            || manifest.SourceConfiguration.HostRevision != manifest.ValidationExecution.HostRevision
            || manifest.SourceConfiguration.ContractBaselineSha256 != context.Protocol.Baseline.ContractManifestSha256
            || manifest.SourceConfiguration.WorkflowSha256 != manifest.ValidationExecution.WorkflowRevision)
        {
            throw new ProtocolException("HV174_SUBJECT_IDENTITY_MISMATCH");
        }
        if (!manifest.SourceConfiguration.SourceAndBuildInputs.Any(identity =>
                identity.Path.StartsWith("src/ContractScribe.", StringComparison.Ordinal))
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
                [manifest.SourceConfiguration.FailureRegistry, manifest.SourceConfiguration.CalibratedBounds]);
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
                if (fixture.CapabilityAvailable == (fixture.BlockedReasonCode is not null))
                {
                    throw new ProtocolException("HV179_FIXTURE_CAPABILITY_STATE");
                }
                var repositoryRoot = RepositoryPaths.ResolveConfined(
                    context.Root,
                    fixture.RepositoryRoot,
                    mustExist: !allowMaterializationDrift);
                if (!allowMaterializationDrift
                    && fixture.CapabilityAvailable
                    && fixture.RepositoryIdentitySha256 != ComputeRepositoryIdentity(RepositoryObserver.Capture(repositoryRoot)))
                {
                    throw new ProtocolException("HV180_FIXTURE_IDENTITY_MISMATCH");
                }
                if (fixture.VectorId is "publication.kill-before-commit" or "publication.kill-after-commit"
                    && string.IsNullOrWhiteSpace(fixture.ResultPath))
                {
                    throw new ProtocolException("HV181_KILL_RESULT_PATH_REQUIRED");
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
        CancellationToken cancellationToken)
    {
        if (!fixture.CapabilityAvailable)
        {
            return CreateBlockedRun(vector, runId, fixture.BlockedReasonCode!);
        }

        var repositoryRoot = RepositoryPaths.ResolveConfined(context.Root, fixture.RepositoryRoot);
        var before = RepositoryObserver.Capture(repositoryRoot);
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
            var entryPoint = RepositoryPaths.ResolveConfined(context.Root, cell.EntryPoint);
            var executable = cell.LaunchKind == "dotnet-dll" ? "dotnet" : entryPoint;
            var arguments = new List<string>();
            if (cell.LaunchKind == "dotnet-dll")
            {
                arguments.Add(entryPoint);
            }
            arguments.AddRange(cell.ArgumentPrefix);
            arguments.Add("--request");
            arguments.Add(requestPath);
            arguments.Add("--response");
            arguments.Add(responsePath);
            var execution = await SubjectProcessRunner.RunAsync(
                executable,
                arguments,
                repositoryRoot,
                context.Protocol.ExecutionContract.StandardOutputByteLimit,
                context.Protocol.ExecutionContract.StandardErrorByteLimit,
                TimeSpan.FromSeconds(context.Protocol.ExecutionContract.SubjectTimeoutSeconds),
                cancellationToken,
                control).ConfigureAwait(false);
            var after = RepositoryObserver.Capture(repositoryRoot);
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

            var resultExists = fixture.ResultPath is not null
                && File.Exists(ResolveFixturePath(repositoryRoot, fixture.ResultPath, mustExist: false));
            var observed = response?.ObservationCode
                ?? MapExternalObservation(vector.VectorId, execution, resultExists, fixture.ExternalCause);
            observed = ApplyIndependentObserver(vector.VectorId, observed, delta, execution);
            var enforcement = response?.EnforcementClass
                ?? (vector.ExpectedEnforcementClass == "caller-or-os-enforced"
                    ? "caller-or-os-enforced"
                    : "observable-only");
            var verdict = diagnostics.Count != 0 || !execution.ControlCompleted
                ? "protocol-invalid-observation"
                : observed == vector.ExpectedObservation && enforcement == vector.ExpectedEnforcementClass
                    ? "matched"
                    : "subject-nonconformance";
            var subject = response ?? CreateExternalSubject(
                vector,
                runId,
                execution,
                resultExists,
                enforcement,
                observed,
                fixture.ExternalCause);
            return new RunEvidence(
                vector.VectorId,
                runId,
                verdict,
                vector.ExpectedObservation,
                observed,
                vector.ExpectedEnforcementClass,
                enforcement,
                subject,
                delta,
                execution.ObservedProcesses,
                diagnostics.Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal).ToArray());
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static RunEvidence CreateStaticRun(VectorDefinition vector, string runId)
    {
        var subject = new SubjectResponse(
            "contractscribe-m1-host-validation-subject-response-v1",
            vector.VectorId,
            runId,
            "started",
            "normal",
            null,
            "succeeded",
            null,
            null,
            null,
            "committed",
            "absent",
            vector.ExpectedEnforcementClass,
            vector.ExpectedObservation);
        return new RunEvidence(
            vector.VectorId,
            runId,
            "matched",
            vector.ExpectedObservation,
            vector.ExpectedObservation,
            vector.ExpectedEnforcementClass,
            vector.ExpectedEnforcementClass,
            subject,
            EmptyDelta(),
            [],
            []);
    }

    private static RunEvidence CreateBlockedRun(VectorDefinition vector, string runId, string reasonCode)
    {
        var subject = new SubjectResponse(
            "contractscribe-m1-host-validation-subject-response-v1",
            vector.VectorId,
            runId,
            "not-started",
            "not-started",
            null,
            null,
            null,
            null,
            null,
            "not-entered",
            "absent",
            vector.ExpectedEnforcementClass,
            "vector.capability-unavailable");
        return new RunEvidence(
            vector.VectorId,
            runId,
            "vector-environment-blocked",
            vector.ExpectedObservation,
            "vector.capability-unavailable",
            vector.ExpectedEnforcementClass,
            vector.ExpectedEnforcementClass,
            subject,
            EmptyDelta(),
            [],
            [reasonCode]);
    }

    private static SubjectResponse CreateExternalSubject(
        VectorDefinition vector,
        string runId,
        ProcessExecutionResult execution,
        bool resultExists,
        string enforcement,
        string observation,
        string? externalCause) =>
        new(
            "contractscribe-m1-host-validation-subject-response-v1",
            vector.VectorId,
            runId,
            execution.ProcessStart,
            externalCause ?? execution.ProcessTermination,
            null,
            null,
            null,
            null,
            null,
            "not-entered",
            resultExists ? "published" : "absent",
            enforcement,
            observation);

    private static SubjectControl? CreateControl(VectorDefinition vector, string tempRoot, int timeoutSeconds) =>
        vector.VectorId switch
        {
            "cancellation.before-commit" => new(Path.Join(tempRoot, "control"), "before-commit", "cancel", TimeSpan.FromSeconds(timeoutSeconds)),
            "cancellation.after-commit" => new(Path.Join(tempRoot, "control"), "after-commit", "cancel", TimeSpan.FromSeconds(timeoutSeconds)),
            "cancellation.late-completion" => new(Path.Join(tempRoot, "control"), "late-completion", "release-late-completion", TimeSpan.FromSeconds(timeoutSeconds)),
            "cancellation.terminal-precedence" => new(Path.Join(tempRoot, "control"), "before-commit", "cancel", TimeSpan.FromSeconds(timeoutSeconds)),
            "publication.kill-before-commit" => new(Path.Join(tempRoot, "control"), "publication-before-commit", "external-kill", TimeSpan.FromSeconds(timeoutSeconds)),
            "publication.kill-after-commit" => new(Path.Join(tempRoot, "control"), "publication-after-commit", "external-kill", TimeSpan.FromSeconds(timeoutSeconds)),
            _ => null
        };

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

    private static string ApplyIndependentObserver(
        string vectorId,
        string observed,
        RepositoryDelta delta,
        ProcessExecutionResult execution)
    {
        if (vectorId == "repository-write.protected-files" && RepositoryObserver.HasProtectedMutation(delta))
        {
            return "repository.protected-files-changed";
        }
        if (vectorId == "toolchain.no-automatic-restore"
            && (delta.AllowedDesignTimeCreated.Any(path => path.EndsWith("project.assets.json", StringComparison.Ordinal))
                || execution.ObservedProcesses.Any(process =>
                    process.Role is "toolchain-owned" or "unknown-descendant")))
        {
            return "toolchain.restore-or-runtime-download-marker-observed";
        }
        if (vectorId == "toolchain.process-topology"
            && execution.ObservedProcesses.Any(process => process.Role == "contractscribe-worker"))
        {
            return "process.contractscribe-worker-observed";
        }
        if (vectorId == "repository-write.allowed-design-time"
            && RepositoryObserver.HasUnexpectedMutation(delta))
        {
            return "repository.unexpected-output-observed";
        }
        return observed;
    }

    private static string MapExternalObservation(
        string vectorId,
        ProcessExecutionResult execution,
        bool resultExists,
        string? externalCause) =>
        vectorId switch
        {
            "failure.launch-before-entry" when execution.ProcessStart == "launch-failure" => "process.launch-failure-no-terminal",
            "failure.runtime-load-before-entry" when execution.ProcessStart is "runtime-load-failure" or "started" => "process.runtime-load-failure-no-terminal",
            "failure.permission-before-entry" when execution.ProcessStart == "permission-failure" => "process.permission-failure-no-terminal",
            "failure.startup-timeout" when execution.TimedOut => "process.startup-timeout-no-terminal",
            "failure.out-of-memory" when externalCause == "out-of-memory" => "process.out-of-memory-external",
            "failure.stack-overflow" when externalCause == "stack-overflow" => "process.stack-overflow-external",
            "failure.abort" when externalCause == "abort" => "process.abort-external",
            "publication.kill-before-commit" when !resultExists => "publication.kill-leaves-no-valid-result",
            "publication.kill-before-commit" => "publication.kill-left-valid-result",
            "publication.kill-after-commit" when resultExists => "publication.committed-result-remains-valid",
            "publication.kill-after-commit" => "publication.committed-result-missing",
            _ => "process.no-valid-subject-response"
        };

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
        new([], [], [], [], [], [], [], []);

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
