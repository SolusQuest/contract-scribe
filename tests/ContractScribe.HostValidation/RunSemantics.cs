namespace ContractScribe.HostValidation;

public sealed record DerivedRun(
    string Observation,
    string EnforcementClass,
    string Verdict,
    IReadOnlyList<string> DiagnosticCodes);

public static class RunSemantics
{
    private enum ToolchainSelectionRequirement
    {
        Either,
        NotSelected,
        Selected,
        Invalid
    }

    public static DerivedRun Derive(
        BundleContext context,
        VectorDefinition vector,
        RunEvidence run,
        FixtureRealization? fixture,
        SubjectSourceConfiguration source,
        CellMaterialization? materialization = null)
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
        if (vector.VectorId != "network.no-contractscribe-initiated-operation"
            && (run.Process.NetworkEvidence is not null
                || run.Process.NetworkOperationRecorderState is not null))
        {
            return new DerivedRun(
                "network.evidence-unexpected",
                vector.ExpectedEnforcementClass,
                "protocol-invalid-observation",
                ["HV247_NETWORK_EVIDENCE_PROTOCOL_FAILURE"]);
        }
        if (vector.VectorId != "bounds.temporary-disk"
            && run.Process.TemporaryDiskHighWater is not null)
        {
            return new DerivedRun(
                "bounds.temporary-disk-observation-unexpected",
                vector.ExpectedEnforcementClass,
                "protocol-invalid-observation",
                ["HV242_TEMPORARY_DISK_CONTRACT"]);
        }
        if (vector.VectorId is not (
                "failure.publication-invalidation"
                or "failure.publication-finalization")
            && run.PublicationArtifactObservation is not null)
        {
            return new DerivedRun(
                "publication.artifact-observation-unexpected",
                vector.ExpectedEnforcementClass,
                "protocol-invalid-observation",
                ["HV251_PUBLICATION_ARTIFACT_OBSERVATION"]);
        }
        if (!fixture.CapabilityAvailable)
        {
            return new DerivedRun(
                "vector.capability-unavailable",
                vector.ExpectedEnforcementClass,
                "vector-environment-blocked",
                [fixture.BlockedReasonCode!]);
        }
        if (vector.VectorId == "bounds.temporary-disk")
        {
            if (run.Process.TemporaryDiskHighWater is null)
            {
                return run.Process.ObservedControlOutcome is "gate-timeout" or "already-exited"
                    ? new DerivedRun(
                        "bounds.temporary-disk-retention-contract-missing",
                        vector.ExpectedEnforcementClass,
                        "subject-nonconformance",
                        [])
                    : new DerivedRun(
                        "bounds.temporary-disk-observer-defect",
                        vector.ExpectedEnforcementClass,
                        "protocol-invalid-observation",
                        ["HV242_TEMPORARY_DISK_CONTRACT"]);
            }
            if (!run.Process.TemporaryDiskHighWater.ObserverComplete)
            {
                return new DerivedRun(
                    "bounds.temporary-disk-observer-incomplete",
                    vector.ExpectedEnforcementClass,
                    "vector-infrastructure-incomplete",
                    ["HV243_TEMPORARY_DISK_OBSERVER_INCOMPLETE"]);
            }
            if (run.Process.TemporaryDiskHighWater.Quantity
                    != "peak-concurrent-logical-file-bytes"
                || run.Process.TemporaryDiskHighWater.GovernedRootsIdentity
                    != "contractscribe-temporary-work-and-output-staging.v1"
                || run.Process.TemporaryDiskHighWater.IntervalIdentity
                    != "pre-subject-to-temporary-disk-high-water.v1"
                || run.Process.TemporaryDiskHighWater.TotalBytes != checked(
                    run.Process.TemporaryDiskHighWater.TemporaryWorkBytes
                    + run.Process.TemporaryDiskHighWater.OutputStagingBytes))
            {
                return new DerivedRun(
                    "bounds.temporary-disk-observer-defect",
                    vector.ExpectedEnforcementClass,
                    "protocol-invalid-observation",
                    ["HV242_TEMPORARY_DISK_CONTRACT"]);
            }
            if (run.Process.TemporaryDiskHighWater.RetentionBreach)
            {
                return new DerivedRun(
                    "bounds.temporary-disk-retention-breach",
                    vector.ExpectedEnforcementClass,
                    "subject-nonconformance",
                []);
            }
        }
        if (vector.VectorId == "network.no-contractscribe-initiated-operation")
        {
            var exactMaterialization = materialization
                ?? throw new ProtocolException(
                    "HV246_NETWORK_PROTECTED_INPUT_INVALIDATED");
            var disposition = NetworkEvidenceEvaluator.Classify(
                context.NetworkEvidenceProfile,
                run.Process.NetworkEvidence,
                NetworkEvidenceEvaluator.ExpectedInputIdentities(
                    source,
                    exactMaterialization));
            var expectedEvidence = NetworkEvidenceEvaluator.Evaluate(
                context,
                source,
                exactMaterialization,
                run.Process.NetworkOperationRecorderState,
                run.Process.ObservationComplete,
                run.ObservedProcesses,
                run.RepositoryDelta);
            if (run.Process.NetworkEvidence is null
                || !CanonicalJson.SerializeCanonical(run.Process.NetworkEvidence)
                    .AsSpan()
                    .SequenceEqual(CanonicalJson.SerializeCanonical(expectedEvidence)))
            {
                return new DerivedRun(
                    "network.evidence-observation-mismatch",
                    vector.ExpectedEnforcementClass,
                    "protocol-invalid-observation",
                    ["HV247_NETWORK_EVIDENCE_PROTOCOL_FAILURE"]);
            }
            if (disposition.Verdict != "matched")
            {
                return new DerivedRun(
                    disposition.ObservationCode,
                    vector.ExpectedEnforcementClass,
                    disposition.Verdict,
                    disposition.DiagnosticCodes);
            }
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
        if (expectedControl?.Action == "external-kill"
            && run.Process.ObservedControlOutcome != "issued-and-observed")
        {
            diagnostics.Add("HV240_EXTERNAL_KILL_UNCONFIRMED");
        }
        if (vector.ExecutorKind == "production-host"
            && vector.ObserverRequirements.Contains("canonical-bytes", StringComparer.Ordinal)
            && vector.VectorId != "support.multi-targeting"
            && (run.ObservedCanonicalResult is null || run.ObservedAuditResult is null))
        {
            diagnostics.Add("HV223_AUDIT_RESULT_INVALID");
        }
        if (run.Subject is not null)
        {
            ValidateSubjectResponse(context, vector, run, source, materialization);
        }
        else if (run.Process.ProcessStart == "started"
            && vector.ExecutorKind == "production-host"
            && run.Process.ProcessTermination == "normal")
        {
            diagnostics.Add("HV208_SUBJECT_RESPONSE_MISSING");
        }

        var observation = DeriveObservation(
            context,
            vector,
            run,
            fixture,
            source,
            materialization);
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
            or "failure.publication-finalization"
            or "bounds.temporary-disk"
            or "bounds.forced-termination"
            || RequiresSynchronizedTree(vectorId);

    public static bool RequiresTransitionLog(string vectorId) =>
        vectorId is
            "cancellation.late-completion"
            or "cancellation.terminal-precedence"
            or "publication.stale-invalidation"
            or "publication.same-directory-atomic"
            or "failure.publication-invalidation"
            or "failure.publication-finalization";

    public static (string Gate, string Action)? ExpectedControl(string vectorId) =>
        vectorId switch
        {
            "cancellation.before-commit" => ("before-commit", "cancel"),
            "cancellation.after-commit" => ("after-commit", "cancel"),
            "cancellation.late-completion" => ("late-completion", "release-late-completion"),
            "cancellation.terminal-precedence" => ("before-commit", "cancel"),
            "publication.kill-before-commit" => ("publication-before-commit", "external-kill"),
            "publication.kill-after-commit" => ("publication-after-commit", "external-kill"),
            "failure.publication-finalization" => ("publication-staging-ready", "observe"),
            "bounds.forced-termination" => ("forced-termination", "external-kill"),
            "bounds.temporary-disk" => (
                "temporary-disk-high-water",
                "measure-temporary-disk"),
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
        SubjectSourceConfiguration source,
        CellMaterialization? materialization)
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
                && subject.ArtifactState != "published")
        {
            throw new ProtocolException("HV210_SUBJECT_OUTCOME_ILLEGAL");
        }
        ValidateOutcomeLegality(vector, run, subject);
        ValidateHostFacts(context.Root, vector, run, source, materialization, subject);
        ValidatePublicationFailureObservation(vector, run, subject);
        if (run.ObservedAuditResult is not null
            && subject.AuditOutcome is not null
            && !run.ObservedAuditResult.AuditOutcomes.Contains(
                $"audit.outcome.{subject.AuditOutcome}",
                StringComparer.Ordinal))
        {
            throw new ProtocolException("HV209_SUBJECT_OBSERVER_CONTRADICTION");
        }
    }

    private static void ValidateHostFacts(
        string root,
        VectorDefinition vector,
        RunEvidence run,
        SubjectSourceConfiguration source,
        CellMaterialization? materialization,
        SubjectResponse subject)
    {
        var facts = subject.HostFacts
            ?? throw new ProtocolException("HV235_HOST_FACTS_MISSING");
        if (facts.SourceConfigurationId != source.SourceConfigurationId
            || facts.HostRevision != source.HostRevision
            || facts.ContractBaselineSha256 != source.ContractBaseline.Sha256
            || facts.FailureRegistrySha256 != source.FailureRegistry.Sha256
            || facts.CalibratedBoundsSha256 != source.CalibratedBounds.Sha256)
        {
            throw new ProtocolException("HV236_HOST_PROVENANCE_MISMATCH");
        }

        ValidateToolchainSelectionState(
            vector.VectorId,
            subject.ExecutionOutcome,
            subject.FailureStage,
            facts,
            materialization);

        var handledFailure = subject.ExecutionOutcome is not null
            && subject.ExecutionOutcome != "succeeded";
        if (handledFailure
            ? facts.NormalizedDiagnosticFacts.Count != 1
                || facts.NormalizedDiagnosticFacts[0].Code != subject.FailureCode
                || facts.NormalizedDiagnosticFacts[0].Stage != subject.FailureStage
            : facts.NormalizedDiagnosticFacts.Count != 0)
        {
            throw new ProtocolException("HV237_HOST_DIAGNOSTIC_FACTS");
        }

        var expectedCommitStatus = subject.ArtifactState switch
        {
            "published" => "committed",
            "staged" => "staged",
            _ => "not-committed"
        };
        if (facts.OutputCommit.Status != expectedCommitStatus
            || expectedCommitStatus == "committed"
                && facts.OutputCommit.Sha256 != run.ObservedCanonicalResult?.Sha256
            || expectedCommitStatus != "committed" && facts.OutputCommit.Sha256 is not null)
        {
            throw new ProtocolException("HV238_OUTPUT_COMMIT_FACTS");
        }

        var requiredMeasurements = vector.VectorId switch
        {
            "bounds.temporary-disk" => new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["temporary-disk-bytes"] = MeasureTemporaryDiskBytes(run)
            },
            "diagnostics.bounded-sanitized" => new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["diagnostic-count"] = facts.NormalizedDiagnosticFacts.Count,
                ["diagnostic-utf8-bytes"] =
                    MeasureCanonicalDiagnosticBytes(facts.NormalizedDiagnosticFacts)
            },
            "toolchain.owned-subprocesses" => new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["toolchain-subprocess-count"] =
                    run.ObservedProcesses.Count(process => process.Role == "toolchain-owned")
            },
            _ => null
        };
        ValidateMeasuredBounds(root, source, facts, requiredMeasurements);

        if (vector.VectorId == "support.multi-targeting")
        {
            if (facts.LoaderFact is not
                {
                    Code: "loader.unsupported.multi-targeting",
                    Disposition: "whole-input-rejected",
                    SelectedOrDefaultTargetFramework: false,
                    PartialResultProduced: false
                })
            {
                throw new ProtocolException("HV241_LOADER_FACTS");
            }
        }
        else if (facts.LoaderFact is not null)
        {
            throw new ProtocolException("HV241_LOADER_FACTS");
        }
    }

    public static void ValidateToolchainSelectionState(
        string vectorId,
        string? executionOutcome,
        string? failureStage,
        HostObservationFacts facts,
        CellMaterialization? materialization)
    {
        var requirement = RequiredToolchainSelection(
            vectorId,
            executionOutcome,
            failureStage);
        if (requirement == ToolchainSelectionRequirement.Invalid
            || requirement == ToolchainSelectionRequirement.NotSelected
                && facts.ToolchainSelectionState != "not-selected"
            || requirement == ToolchainSelectionRequirement.Selected
                && facts.ToolchainSelectionState != "selected"
            || facts.ToolchainSelectionState == "not-selected"
                && (facts.SelectedSdk is not null
                    || facts.SelectedRuntime is not null
                    || facts.SelectedMsbuild is not null)
            || facts.ToolchainSelectionState == "selected"
                && (string.IsNullOrWhiteSpace(facts.SelectedSdk)
                    || string.IsNullOrWhiteSpace(facts.SelectedRuntime)
                    || string.IsNullOrWhiteSpace(facts.SelectedMsbuild)
                    || materialization is not null
                        && (facts.SelectedSdk != materialization.SelectedSdk
                            || facts.SelectedRuntime != materialization.SelectedRuntime
                            || facts.SelectedMsbuild != materialization.SelectedMsbuild))
            || facts.ToolchainSelectionState is not ("not-selected" or "selected"))
        {
            throw new ProtocolException("HV252_TOOLCHAIN_SELECTION_STATE");
        }
    }

    public static void ValidateSelfTestFailureRegistryRow(
        string root,
        SubjectResponse subject)
    {
        var registryPath = RepositoryPaths.ResolveConfined(
            root,
            "tests/fixtures/m1-host-validation/v1/self-test-host-failure-registry.json");
        var registrySha256 = CanonicalJson.Sha256File(registryPath);
        if (subject.FailureRegistryIdentity != registrySha256
            || subject.HostFacts?.FailureRegistrySha256 != registrySha256)
        {
            throw new ProtocolException("HV157_FAILURE_REGISTRY_BINDING");
        }
        ValidateFailureRegistryRow(registryPath, subject);
    }

    private static ToolchainSelectionRequirement RequiredToolchainSelection(
        string vectorId,
        string? executionOutcome,
        string? failureStage)
    {
        var genericRequirement = executionOutcome switch
        {
            "invalid-input"
                or "environment-unavailable"
                or "load-failure"
                or "audit-error"
                or "succeeded" => ToolchainSelectionRequirement.Either,
            "publication-failure" => ToolchainSelectionRequirement.Invalid,
            "cancelled" or "timeout" => failureStage switch
            {
                "input" or "environment" =>
                    ToolchainSelectionRequirement.NotSelected,
                "sdk-discovery" or "publication" =>
                    ToolchainSelectionRequirement.Either,
                "workspace-load"
                    or "classification"
                    or "documentation-observation"
                    or "policy-evidence"
                    or "audit"
                    or "result-validation"
                    or "shutdown"
                    or "internal" => ToolchainSelectionRequirement.Selected,
                _ => ToolchainSelectionRequirement.Invalid
            },
            _ => ToolchainSelectionRequirement.Either
        };

        if (vectorId is "cancellation.before-commit"
            or "cancellation.after-commit"
            or "cancellation.late-completion"
            or "cancellation.terminal-precedence")
        {
            return genericRequirement is ToolchainSelectionRequirement.Either
                or ToolchainSelectionRequirement.Selected
                    ? ToolchainSelectionRequirement.Selected
                    : ToolchainSelectionRequirement.Invalid;
        }
        if (vectorId == "failure.publication-invalidation")
        {
            return ToolchainSelectionRequirement.NotSelected;
        }
        if (vectorId == "failure.publication-finalization")
        {
            return ToolchainSelectionRequirement.Selected;
        }

        return genericRequirement;
    }

    public static void ValidateMeasuredBounds(
        string root,
        SubjectSourceConfiguration source,
        HostObservationFacts facts,
        IReadOnlyDictionary<string, long>? requiredMeasurements)
    {
        if (requiredMeasurements is not null)
        {
            using var bounds = CanonicalJson.ReadStrict(
                RepositoryPaths.ResolveConfined(root, source.CalibratedBounds.Path),
                1024 * 1024,
                requireCanonical: true);
            var expectedEntries = bounds.RootElement.GetProperty("entries").EnumerateArray()
                .Where(entry => requiredMeasurements.ContainsKey(entry.GetProperty("name").GetString()!))
                .ToDictionary(
                    entry => entry.GetProperty("name").GetString()!,
                    entry => entry.Clone(),
                    StringComparer.Ordinal);
            if (facts.MeasuredBounds.Select(item => item.Name)
                    .Distinct(StringComparer.Ordinal).Count() != facts.MeasuredBounds.Count)
            {
                throw new ProtocolException("HV239_MEASURED_BOUND_FACTS");
            }
            var observedEntries = facts.MeasuredBounds.ToDictionary(item => item.Name, StringComparer.Ordinal);
            if (expectedEntries.Count != requiredMeasurements.Count
                || observedEntries.Count != requiredMeasurements.Count
                || !expectedEntries.Keys.Order(StringComparer.Ordinal)
                    .SequenceEqual(observedEntries.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal)
                || requiredMeasurements.Any(pair =>
                {
                    var expected = expectedEntries[pair.Key];
                    var observed = observedEntries[pair.Key];
                    return observed.Unit != expected.GetProperty("unit").GetString()
                        || observed.Threshold != expected.GetProperty("limit").GetInt64()
                        || observed.EnforcementClass != expected.GetProperty("enforcementClass").GetString()
                        || observed.Measured != pair.Value
                        || observed.Measured < 0
                        || observed.Measured > observed.Threshold;
                }))
            {
                throw new ProtocolException("HV239_MEASURED_BOUND_FACTS");
            }
        }
        else if (facts.MeasuredBounds.Count != 0)
        {
            throw new ProtocolException("HV239_MEASURED_BOUND_FACTS");
        }
    }

    private static void ValidateOutcomeLegality(
        VectorDefinition vector,
        RunEvidence run,
        SubjectResponse subject)
    {
        var hasResult = run.ObservedCanonicalResult is not null;
        var processStarted = run.Process.ProcessStart == "started";
        var handledFailure = subject.ExecutionOutcome is
            "invalid-input"
            or "environment-unavailable"
            or "load-failure"
            or "audit-error"
            or "publication-failure"
            or "cancelled"
            or "timeout";
        var committedSuccess = subject.ExecutionOutcome == "succeeded";

        if (!processStarted)
        {
            RequireLegal(
                subject.ExecutionOutcome is null
                && subject.AuditOutcome is null
                && subject.TerminalState == "not-entered"
                && subject.ArtifactState is "absent" or "invalidated"
                && !hasResult);
            return;
        }

        if (handledFailure)
        {
            RequireLegal(
                subject.AuditOutcome is null
                && subject.TerminalState == "committed"
                && subject.ArtifactState is "absent" or "invalidated"
                && !hasResult);
            return;
        }

        if (committedSuccess)
        {
            RequireLegal(
                subject.AuditOutcome is "compliant" or "violation" or "skipped"
                && subject.TerminalState == "committed"
                && subject.ArtifactState == "published"
                && hasResult);
            return;
        }

        RequireLegal(
            subject.ExecutionOutcome is null
            && subject.AuditOutcome is null
            && subject.TerminalState is "not-entered" or "pending"
            && subject.ArtifactState is "absent" or "invalidated" or "staged"
            && !hasResult);
    }

    private static void RequireLegal(bool condition)
    {
        if (!condition)
        {
            throw new ProtocolException("HV210_SUBJECT_OUTCOME_ILLEGAL");
        }
    }

    private static void ValidatePublicationFailureObservation(
        VectorDefinition vector,
        RunEvidence run,
        SubjectResponse subject)
    {
        if (vector.VectorId is not (
                "failure.publication-invalidation"
                or "failure.publication-finalization"))
        {
            return;
        }

        var observation = run.PublicationArtifactObservation
            ?? throw new ProtocolException("HV251_PUBLICATION_ARTIFACT_OBSERVATION");
        RequireLegal(
            subject.ExecutionOutcome == "publication-failure"
            && subject.FailureStage == "publication"
            && subject.ProcessStart == "started"
            && subject.ProcessTermination == "normal"
            && subject.AuditOutcome is null
            && subject.TerminalState == "committed"
            && subject.ArtifactState == "invalidated"
            && subject.CanonicalResult is null
            && run.ObservedCanonicalResult is null
            && run.ObservedAuditResult is null
            && HasExactTransitionTrace(vector.VectorId, run.Process.TransitionEvents));

        if (vector.VectorId == "failure.publication-invalidation")
        {
            RequireLegal(
                observation.PreRunCanonical is not null
                && observation.PreRunCanonical == observation.PostRunCanonical
                && observation.PostRunAttribution == "pre-existing"
                && observation.StagedCanonical is null
                && observation.StagingDisposition == "not-created"
                && subject.HostFacts?.ToolchainSelectionState == "not-selected");
            return;
        }

        RequireLegal(
            observation.PreRunCanonical is null
            && observation.PostRunCanonical is null
            && observation.PostRunAttribution == "absent"
            && observation.StagedCanonical is not null
            && observation.StagingDisposition == "cleaned"
            && subject.HostFacts?.ToolchainSelectionState == "selected");
    }

    private static void ValidateFailureRegistryRow(
        string root,
        SubjectSourceConfiguration source,
        SubjectResponse subject)
    {
        ValidateFailureRegistryRow(
            RepositoryPaths.ResolveConfined(root, source.FailureRegistry.Path),
            subject);
    }

    private static void ValidateFailureRegistryRow(
        string registryPath,
        SubjectResponse subject)
    {
        using var registry = CanonicalJson.ReadStrict(
            registryPath,
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
        BundleContext context,
        VectorDefinition vector,
        RunEvidence run,
        FixtureRealization fixture,
        SubjectSourceConfiguration source,
        CellMaterialization? materialization)
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
            "bounds.forced-termination" when run.Process.ProcessTermination == "external-kill"
                && run.Process.ObservedControlOutcome == "issued-and-observed"
                && HasExactCausalNativeTermination(run.Process)
                && !resultExists => "bounds.forced-termination-external",
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
            "cancellation.late-completion" when HasExactTransitionTrace(
                vector.VectorId,
                run.Process.TransitionEvents) =>
                "terminal.late-completion-rejected",
            "cancellation.terminal-precedence" when HasExactTransitionTrace(
                vector.VectorId,
                run.Process.TransitionEvents) =>
                "terminal.precedence-closed",
            "publication.stale-invalidation" when HasExactTransitionTrace(
                vector.VectorId,
                run.Process.TransitionEvents) =>
                "publication.stale-invalidated-at-start",
            "publication.same-directory-atomic" when HasExactTransitionTrace(
                vector.VectorId,
                run.Process.TransitionEvents) =>
                "publication.same-directory-atomic-rename",
            "failure.publication-invalidation" when HasExactTransitionTrace(
                vector.VectorId,
                run.Process.TransitionEvents) =>
                "publication.invalidation-failure-committed",
            "failure.publication-finalization" when HasExactTransitionTrace(
                vector.VectorId,
                run.Process.TransitionEvents) =>
                "publication.finalization-failure-committed",
            "determinism.fresh-process-canonical" when result is not null =>
                "determinism.byte-identical",
            "path.working-directory-independent" when result is not null =>
                "path.working-directory-independent",
            "support.generator" when result is not null =>
                "support.generator-generated-facts",
            "support.multi-targeting" when result is null
                && run.Subject is
                {
                    ExecutionOutcome: "load-failure",
                    AuditOutcome: null,
                    ArtifactState: "absent"
                }
                && run.Subject.HostFacts?.LoaderFact is
                {
                    Code: "loader.unsupported.multi-targeting",
                    Disposition: "whole-input-rejected",
                    SelectedOrDefaultTargetFramework: false,
                    PartialResultProduced: false
                } =>
                "support.multi-targeting-rejected-no-partial-result",
            _ => run.Subject?.ObservationCode ?? "process.no-valid-subject-response"
        };
    }

    public static bool HasExactTransitionTrace(
        string vectorId,
        IReadOnlyList<string>? events)
    {
        if (events is null)
        {
            return false;
        }
        string[] expected = vectorId switch
        {
            "cancellation.late-completion" =>
                ["terminal-commit-cancelled", "late-terminal-attempt-rejected"],
            "cancellation.terminal-precedence" =>
                ["terminal-commit-cancelled", "competing-terminal-attempt-rejected"],
            "publication.stale-invalidation" =>
                ["invalidation-completed", "failure-prone-stage-entered"],
            "publication.same-directory-atomic" =>
                ["staging-created-in-destination", "atomic-rename-committed"],
            "failure.publication-invalidation" =>
                [
                    "invalidation-attempt-failed",
                    "terminal-commit-publication-failure",
                    "late-terminal-attempt-rejected"
                ],
            "failure.publication-finalization" =>
                [
                    "invalidation-completed",
                    "failure-prone-stage-entered",
                    "staging-created-in-destination",
                    "atomic-replace-attempt-failed",
                    "staging-cleanup-completed",
                    "terminal-commit-publication-failure",
                    "late-terminal-attempt-rejected"
                ],
            _ => []
        };
        return expected.Length != 0
            && events.SequenceEqual(expected, StringComparer.Ordinal);
    }

    public static long MeasureTemporaryDiskBytes(RunEvidence run) =>
        run.Process.TemporaryDiskHighWater?.TotalBytes
            ?? throw new ProtocolException("HV239_MEASURED_BOUND_FACTS");

    public static bool HasExactCausalNativeTermination(ProcessObservation process)
    {
        if (OperatingSystem.IsWindows())
        {
            return process.NativeTerminationKind == "windows-terminate-process"
                && process.NativeTerminationCode == NativeTerminationObserver.WindowsTerminationSentinel;
        }
        if (OperatingSystem.IsLinux())
        {
            return process.NativeTerminationKind == "unix-signal"
                && process.NativeTerminationCode == NativeTerminationObserver.UnixSigKill;
        }
        return false;
    }

    public static long MeasureCanonicalDiagnosticBytes(
        IReadOnlyList<NormalizedDiagnosticFact> diagnostics) =>
        CanonicalJson.SerializeCanonical(diagnostics).LongLength;
}
