namespace ContractScribe.HostValidation;

public sealed record DerivedRun(
    string Observation,
    string EnforcementClass,
    string Verdict,
    IReadOnlyList<string> DiagnosticCodes);

public static class RunSemantics
{
    public static DerivedRun Derive(
        BundleContext context,
        VectorDefinition vector,
        RunEvidence run,
        FixtureRealization? fixture,
        SubjectSourceConfiguration source)
    {
        if (run.VectorId != vector.VectorId
            || run.ExpectedObservation != vector.ExpectedObservation
            || run.ExpectedEnforcementClass != vector.ExpectedEnforcementClass)
        {
            throw new ProtocolException("HV155_EVIDENCE_ORACLE_MISMATCH");
        }

        if (vector.ExecutorKind == "harness-static")
        {
            var result = StaticValidatorRegistry.Execute(context.Root, vector);
            return new DerivedRun(result.ObservationCode, result.EnforcementClass, "matched", result.DiagnosticCodes);
        }
        if (fixture is null || fixture.ExecutorKind != vector.ExecutorKind)
        {
            throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");
        }
        if (!fixture.CapabilityAvailable)
        {
            return new DerivedRun(
                "vector.capability-unavailable",
                vector.ExpectedEnforcementClass,
                "vector-environment-blocked",
                [fixture.BlockedReasonCode!]);
        }

        var diagnostics = run.DiagnosticCodes.ToHashSet(StringComparer.Ordinal);
        if (!run.Process.ObservationComplete
            || RequiresSynchronizedTree(vector.VectorId)
                && fixture.ProcessObservationMode != "synchronized-tree")
        {
            diagnostics.Add("HV207_PROCESS_OBSERVATION_INCOMPLETE");
        }
        if (run.Subject is not null)
        {
            ValidateSubjectResponse(vector, run, source);
        }
        else if (run.Process.ProcessStart == "started"
            && vector.ExecutorKind == "production-host"
            && run.Process.ProcessTermination == "normal")
        {
            diagnostics.Add("HV208_SUBJECT_RESPONSE_MISSING");
        }

        var observation = DeriveObservation(vector, run, fixture);
        var enforcement = run.Subject?.EnforcementClass
            ?? (vector.ExpectedEnforcementClass == "caller-or-os-enforced"
                ? "caller-or-os-enforced"
                : "observable-only");
        var verdict = diagnostics.Count != 0
            ? "protocol-invalid-observation"
            : observation == vector.ExpectedObservation
                && enforcement == vector.ExpectedEnforcementClass
                ? "matched"
                : "subject-nonconformance";
        return new DerivedRun(
            observation,
            enforcement,
            verdict,
            diagnostics.Order(StringComparer.Ordinal).ToArray());
    }

    public static bool RequiresSynchronizedTree(string vectorId) =>
        vectorId is
            "toolchain.process-topology"
            or "toolchain.no-automatic-restore"
            or "network.no-contractscribe-initiated-operation"
            or "toolchain.owned-subprocesses";

    public static string DeriveCellOutcome(IEnumerable<RunEvidence> runs)
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

    private static void ValidateSubjectResponse(
        VectorDefinition vector,
        RunEvidence run,
        SubjectSourceConfiguration source)
    {
        var subject = run.Subject!;
        if (subject.VectorId != run.VectorId
            || subject.RunId != run.RunId
            || subject.ProcessStart != run.Process.ProcessStart
            || subject.ProcessTermination != run.Process.ProcessTermination
            || subject.CanonicalResult != run.ObservedCanonicalResult)
        {
            throw new ProtocolException("HV209_SUBJECT_OBSERVER_CONTRADICTION");
        }
        if (subject.ExecutionOutcome is not null && subject.ExecutionOutcome != "succeeded")
        {
            if (subject.AuditOutcome is not null
                || subject.FailureRegistryIdentity != source.FailureRegistry.Sha256
                || string.IsNullOrWhiteSpace(subject.FailureCode)
                || string.IsNullOrWhiteSpace(subject.FailureStage))
            {
                throw new ProtocolException("HV157_FAILURE_REGISTRY_BINDING");
            }
        }
        else if (subject.ExecutionOutcome == "succeeded"
            && subject.FailureRegistryIdentity is not null)
        {
            throw new ProtocolException("HV210_SUBJECT_OUTCOME_ILLEGAL");
        }
        if (run.Process.TimedOut && subject.ExecutionOutcome != "timeout"
            || run.Process.ProcessStart != "started" && subject.ExecutionOutcome is not null
            || run.Process.ProcessTermination != "normal"
                && subject.ExecutionOutcome == "succeeded"
                && (subject.ArtifactState != "published"
                    || vector.VectorId is not (
                        "publication.kill-after-commit"
                        or "cancellation.after-commit")))
        {
            throw new ProtocolException("HV210_SUBJECT_OUTCOME_ILLEGAL");
        }
    }

    private static string DeriveObservation(
        VectorDefinition vector,
        RunEvidence run,
        FixtureRealization fixture)
    {
        var delta = run.RepositoryDelta;
        if (RepositoryObserver.HasProtectedMutation(delta))
        {
            return vector.VectorId == "repository-write.protected-files"
                ? "repository.protected-files-changed"
                : "repository.protected-mutation-unexpected";
        }
        if (delta.OtherCreated.Count != 0
            || delta.OtherDeleted.Count != 0
            || delta.OtherChanged.Count != 0
            || delta.AllowedDesignTimeDeleted.Count != 0)
        {
            return "repository.unexpected-output-observed";
        }
        if (vector.VectorId == "toolchain.no-automatic-restore"
            && delta.AllowedDesignTimeCreated.Concat(delta.AllowedDesignTimeChanged)
                .Any(path => path.EndsWith("project.assets.json", StringComparison.Ordinal)))
        {
            return "toolchain.restore-or-runtime-download-marker-observed";
        }
        if (vector.VectorId == "toolchain.process-topology")
        {
            if (run.ObservedProcesses.Count(process => process.Role == "subject-runtime") != 1
                || run.ObservedProcesses.Any(process => process.Role == "contractscribe-worker"))
            {
                return "process.contractscribe-worker-observed";
            }
        }

        if (vector.ExecutorKind == "production-host")
        {
            return run.Subject?.ObservationCode ?? "process.no-valid-subject-response";
        }

        var resultExists = run.ObservedCanonicalResult is not null;
        return vector.VectorId switch
        {
            "failure.launch-before-entry" when run.Process.ProcessStart == "launch-failure" =>
                "process.launch-failure-no-terminal",
            "failure.runtime-load-before-entry" when run.Process.ProcessStart == "runtime-load-failure" =>
                "process.runtime-load-failure-no-terminal",
            "failure.permission-before-entry" when run.Process.ProcessStart == "permission-failure" =>
                "process.permission-failure-no-terminal",
            "failure.startup-timeout" when run.Process.TimedOut =>
                "process.startup-timeout-no-terminal",
            "failure.out-of-memory" when run.Process.ProcessTermination == "out-of-memory"
                && fixture.ExternalCause == "out-of-memory" => "process.out-of-memory-external",
            "failure.stack-overflow" when run.Process.ProcessTermination == "stack-overflow"
                && fixture.ExternalCause == "stack-overflow" => "process.stack-overflow-external",
            "failure.abort" when run.Process.ProcessTermination == "abort"
                && fixture.ExternalCause == "abort" => "process.abort-external",
            "publication.kill-before-commit" when !resultExists => "publication.kill-leaves-no-valid-result",
            "publication.kill-before-commit" => "publication.kill-left-valid-result",
            "publication.kill-after-commit" when resultExists => "publication.committed-result-remains-valid",
            "publication.kill-after-commit" => "publication.committed-result-missing",
            _ => run.Subject?.ObservationCode ?? "process.no-valid-subject-response"
        };
    }
}
