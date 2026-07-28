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
        var expectedControl = ExpectedControl(vector.VectorId);
        if (expectedControl is not null
            && (!run.Process.ControlCompleted
                || run.Process.ObservedGateName != expectedControl.Value.Gate
                || run.Process.ObservedControlAction != expectedControl.Value.Action
                || expectedControl.Value.Action == "observe"
                    && !run.Process.PostGateSampleObserved))
        {
            diagnostics.Add("HV224_CONTROL_GATE_INCOMPLETE");
        }
        if (vector.ExecutorKind == "production-host"
            && vector.ObserverRequirements.Contains("canonical-bytes", StringComparer.Ordinal)
            && (run.ObservedCanonicalResult is null || run.ObservedAuditResult is null))
        {
            diagnostics.Add("HV223_AUDIT_RESULT_INVALID");
        }
        if (run.Subject is not null)
        {
            ValidateSubjectResponse(context, vector, run, source);
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

    public static bool RequiresControl(string vectorId) =>
        vectorId is
            "cancellation.before-commit"
            or "cancellation.after-commit"
            or "cancellation.late-completion"
            or "cancellation.terminal-precedence"
            or "publication.kill-before-commit"
            or "publication.kill-after-commit"
            || RequiresSynchronizedTree(vectorId);

    public static (string Gate, string Action)? ExpectedControl(string vectorId) =>
        vectorId switch
        {
            "cancellation.before-commit" => ("before-commit", "cancel"),
            "cancellation.after-commit" => ("after-commit", "cancel"),
            "cancellation.late-completion" => ("late-completion", "release-late-completion"),
            "cancellation.terminal-precedence" => ("before-commit", "cancel"),
            "publication.kill-before-commit" => ("publication-before-commit", "external-kill"),
            "publication.kill-after-commit" => ("publication-after-commit", "external-kill"),
            "toolchain.process-topology"
                or "toolchain.no-automatic-restore"
                or "network.no-contractscribe-initiated-operation"
                or "toolchain.owned-subprocesses" => ("process-observation", "observe"),
            _ => null
        };

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
        BundleContext context,
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
            ValidateFailureRegistryRow(context.Root, source, subject);
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
        if (run.ObservedAuditResult is not null
            && subject.AuditOutcome is not null
            && !run.ObservedAuditResult.AuditOutcomes.Contains(
                $"audit.outcome.{subject.AuditOutcome}",
                StringComparer.Ordinal))
        {
            throw new ProtocolException("HV209_SUBJECT_OBSERVER_CONTRADICTION");
        }
    }

    private static void ValidateFailureRegistryRow(
        string root,
        SubjectSourceConfiguration source,
        SubjectResponse subject)
    {
        using var registry = CanonicalJson.ReadStrict(
            RepositoryPaths.ResolveConfined(root, source.FailureRegistry.Path),
            1024 * 1024,
            requireCanonical: true);
        var matches = registry.RootElement.GetProperty("entries").EnumerateArray()
            .Count(entry =>
                entry.GetProperty("code").GetString() == subject.FailureCode
                && entry.GetProperty("stage").GetString() == subject.FailureStage
                && entry.GetProperty("executionOutcome").GetString() == subject.ExecutionOutcome
                && entry.GetProperty("terminalState").GetString() switch
                {
                    "committed-non-success" => subject.TerminalState == "committed",
                    _ => false
                });
        if (matches != 1)
        {
            throw new ProtocolException("HV157_FAILURE_REGISTRY_BINDING");
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
            && (delta.AllowedDesignTimeCreated.Concat(delta.AllowedDesignTimeChanged)
                    .Any(path => path.EndsWith("project.assets.json", StringComparison.Ordinal))
                || run.ObservedProcesses.Any(process =>
                    process.Role == "restore-or-runtime-download")))
        {
            return "toolchain.restore-or-runtime-download-marker-observed";
        }
        if (vector.VectorId == "toolchain.process-topology")
        {
            if (run.ObservedProcesses.Count(process => process.Role == "subject-runtime") != 1
                || run.ObservedProcesses.Any(process =>
                    process.Role is "contractscribe-worker"
                        or "restore-or-runtime-download"
                        or "unknown-descendant"))
            {
                return "process.contractscribe-worker-observed";
            }
        }
        if (vector.VectorId == "network.no-contractscribe-initiated-operation"
            && run.ObservedProcesses.Any(process =>
                process.Role is "contractscribe-worker"
                    or "restore-or-runtime-download"
                    or "unknown-descendant"))
        {
            return "network.contractscribe-initiated-operation-observed";
        }
        if (vector.VectorId == "toolchain.owned-subprocesses"
            && run.ObservedProcesses.Any(process =>
                process.Role is "contractscribe-worker"
                    or "restore-or-runtime-download"
                    or "unknown-descendant"))
        {
            return "process.unowned-subprocess-observed";
        }

        if (vector.ExecutorKind == "production-host")
        {
            return DeriveProductionObservation(vector, run);
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

    private static string DeriveProductionObservation(VectorDefinition vector, RunEvidence run)
    {
        var result = run.ObservedAuditResult;
        return vector.VectorId switch
        {
            "contracts.policy-conformance" when result?.PolicyConfigurationVersion == 1 =>
                "contracts.policy.amended-v1",
            "contracts.taxonomy-conformance" when result?.TaxonomyRegistryVersion == 1 =>
                "contracts.taxonomy.amended-v1",
            "contracts.audit-conformance" when result?.AuditResultVersion == 1 =>
                "contracts.audit.amended-v1",
            "contracts.profile-external-api" when result?.TargetProfile == "profile.external-api" =>
                "audit.profile.external-api",
            "contracts.profile-assembly-visible" when result?.TargetProfile == "profile.assembly-visible" =>
                "audit.profile.assembly-visible",
            "contracts.outcome-compliant" when result?.AuditOutcomes.Count > 0
                && result.AuditOutcomes.All(outcome => outcome == "audit.outcome.compliant") =>
                "audit.outcome.compliant",
            "contracts.outcome-violation" when result?.AuditOutcomes.Contains(
                    "audit.outcome.violation",
                    StringComparer.Ordinal) == true
                && run.Subject?.ExecutionOutcome == "succeeded" =>
                "audit.outcome.violation-success",
            "contracts.outcome-skipped" when result?.AuditOutcomes.Contains(
                    "audit.outcome.skipped",
                    StringComparer.Ordinal) == true =>
                "audit.outcome.skipped",
            "determinism.fresh-process-canonical" when result is not null =>
                "determinism.byte-identical",
            "path.working-directory-independent" when result is not null =>
                "path.working-directory-independent",
            "support.generator" when result is not null =>
                "support.generator-generated-facts",
            "support.multi-targeting" when result is not null =>
                "support.multi-targeting-owned-disposition",
            _ => run.Subject?.ObservationCode ?? "process.no-valid-subject-response"
        };
    }
}
