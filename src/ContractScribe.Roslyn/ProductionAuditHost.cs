using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ContractScribe.Core;
using ContractScribe.Core.Hosting;

namespace ContractScribe.Roslyn;

internal sealed class ProductionAuditHost
{
    private readonly HostBuildProvenance actualProvenance;

    public ProductionAuditHost(HostBuildProvenance? actualProvenance = null)
    {
        this.actualProvenance = actualProvenance
            ?? HostBuildMetadata.Read(typeof(ProductionAuditHost).Assembly)?.ToProvenance()
            ?? throw new InvalidOperationException(
                "The production validation Host is not materialized in this artifact.");
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The selected-toolchain boundary maps unexpected managed failures to one bounded internal row.")]
    public async Task<ProductionAuditOutcome> RunAsync(
        ProductionAuditRequest request,
        ProductionAuditHostControls controls,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(controls);
        var transitions = new List<string>();
        var coordinator = new HostTerminalCoordinator();
        var toolchain = HostToolchainFact.NotSelected;
        LoaderFact? loaderFact = null;
        AtomicResultPublisher publisher;
        try
        {
            publisher = AtomicResultPublisher.Prepare(
                request.PublicationTarget,
                controls);
            Record(controls, transitions, "invalidation-completed");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Record(controls, transitions, "invalidation-attempt-failed");
            return await CommitFailureAsync(
                coordinator,
                actualProvenance,
                toolchain,
                "host.publication.invalidation-failed",
                HostArtifactState.Invalidated,
                controls,
                transitions,
                loaderFact).ConfigureAwait(false);
        }

        using (publisher)
        {

            using var meter = new TemporaryDiskMeter(
                request.AuditTemporaryRoot,
                request.OutputStagingRoot ?? Path.GetDirectoryName(publisher.StagingPath));
            using var processMeter = new ToolchainProcessMeter();
            using var totalDeadline = new CancellationTokenSource();
            totalDeadline.CancelAfter(controls.Deadline("total-audit-timeout"));
            using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                totalDeadline.Token);
            var totalToken = totalTimeout.Token;

            Record(controls, transitions, "failure-prone-stage-entered");
            PolicyDocumentV1 policy;
            ResolvedRepositoryPaths resolvedPaths;
            try
            {
                await controls.ReachStageAsync(HostStage.Input, totalToken).ConfigureAwait(false);
                totalToken.ThrowIfCancellationRequested();
                if (!Equals(request.ProvenanceAssertion, actualProvenance))
                {
                    return await CommitFailureAsync(
                        coordinator,
                        actualProvenance,
                        toolchain,
                        "host.input.provenance-mismatch",
                        HostArtifactState.Invalidated,
                        controls,
                        transitions,
                        loaderFact).ConfigureAwait(false);
                }
                resolvedPaths = new RepositoryPathResolver().Resolve(
                    request.RepositoryRoot,
                    request.InputPath);
                var parsedPolicy = PolicyConfigurationEvaluator.Parse(
                    request.PolicyBytes,
                    totalToken);
                if (parsedPolicy.Status != PolicyRunStatus.Success
                    || parsedPolicy.Document is null)
                {
                    return await CommitFailureAsync(
                        coordinator,
                        actualProvenance,
                        toolchain,
                        "host.input.invalid-request",
                        HostArtifactState.Invalidated,
                        controls,
                        transitions,
                        loaderFact).ConfigureAwait(false);
                }
                policy = parsedPolicy.Document;
            }
            catch (OperationCanceledException)
            {
                return await CommitInterruptionAsync(
                    coordinator,
                    actualProvenance,
                    toolchain,
                    HostStage.Input,
                    cancellationToken.IsCancellationRequested
                        ? HostExecutionOutcome.Cancelled
                        : HostExecutionOutcome.Timeout,
                    controls,
                    transitions,
                    loaderFact).ConfigureAwait(false);
            }
            catch (LoaderException)
            {
                return await CommitFailureAsync(
                    coordinator,
                    actualProvenance,
                    toolchain,
                    "host.input.invalid-request",
                    HostArtifactState.Invalidated,
                    controls,
                    transitions,
                    loaderFact).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await CommitFailureAsync(
                    coordinator,
                    actualProvenance,
                    toolchain,
                    "host.internal.unexpected",
                    HostArtifactState.Invalidated,
                    controls,
                    transitions,
                    loaderFact).ConfigureAwait(false);
            }

            RegisteredToolchain registered;
            try
            {
                if (controls.Fault == ProductionHostFault.EnvironmentUnavailable)
                {
                    throw LoaderException.Toolchain("toolchain.sdk-unavailable");
                }
                using var sdkTimeout = CancellationTokenSource.CreateLinkedTokenSource(totalToken);
                sdkTimeout.CancelAfter(controls.Deadline("sdk-discovery-timeout"));
                await controls.ReachStageAsync(HostStage.SdkDiscovery, sdkTimeout.Token)
                    .ConfigureAwait(false);
                sdkTimeout.Token.ThrowIfCancellationRequested();
                var sdkTask = Task.Run(
                    () => controls.SdkDiscovery?.Invoke(sdkTimeout.Token)
                        ?? MsBuildBootstrap.EnsureRegisteredForProductionHostAsync(
                            Path.GetDirectoryName(resolvedPaths.PhysicalInput)!,
                            sdkTimeout.Token),
                    CancellationToken.None);
                try
                {
                    registered = await sdkTask.WaitAsync(sdkTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _ = ObserveLateSdkDiscoveryAsync(sdkTask);
                    throw;
                }
                toolchain = HostToolchainFact.Selected(
                    registered.Identity.SdkVersion,
                    registered.Identity.RuntimeVersion,
                    registered.Identity.MsbuildVersion,
                    registered.Identity.Architecture);
            }
            catch (OperationCanceledException)
            {
                return await CommitInterruptionAsync(
                    coordinator,
                    actualProvenance,
                    toolchain,
                    HostStage.SdkDiscovery,
                    cancellationToken.IsCancellationRequested
                        ? HostExecutionOutcome.Cancelled
                        : HostExecutionOutcome.Timeout,
                    controls,
                    transitions,
                    loaderFact).ConfigureAwait(false);
            }
            catch (LoaderException)
            {
                return await CommitFailureAsync(
                    coordinator,
                    actualProvenance,
                    toolchain,
                    "host.sdk-discovery.unavailable",
                    HostArtifactState.Invalidated,
                    controls,
                    transitions,
                    loaderFact).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await CommitFailureAsync(
                    coordinator,
                    actualProvenance,
                    toolchain,
                    "host.internal.unexpected",
                    HostArtifactState.Invalidated,
                    controls,
                    transitions,
                    loaderFact).ConfigureAwait(false);
            }

            RepositoryLoadOutcome load;
            Task<RepositoryLoadOutcome>? loaderTask = null;
            using var workspaceTimeout = CancellationTokenSource.CreateLinkedTokenSource(totalToken);
            workspaceTimeout.CancelAfter(controls.Deadline("workspace-load-timeout"));
            try
            {
                await controls.ReachStageAsync(HostStage.WorkspaceLoad, workspaceTimeout.Token)
                    .ConfigureAwait(false);
                workspaceTimeout.Token.ThrowIfCancellationRequested();
                if (controls.Fault == ProductionHostFault.LoadFailure)
                {
                    return await CommitFailureAsync(
                        coordinator,
                        actualProvenance,
                        toolchain,
                        "host.workspace-load.failed",
                        HostArtifactState.Invalidated,
                        controls,
                        transitions,
                        new LoaderFact("workspace", "loader.test-stimulus")).ConfigureAwait(false);
                }
                var loader = new RepositoryLoader(
                    observer: stage =>
                    {
                        _ = stage;
                    },
                    preselectedToolchain: registered);
                var loadRequest = new RepositoryLoadRequest(
                    request.RepositoryRoot,
                    request.InputPath,
                    request.ToolGeneratedSources);
                loaderTask = Task.Run(
                    () => controls.RepositoryLoad?.Invoke(loadRequest, workspaceTimeout.Token)
                        ?? loader.LoadAsync(loadRequest, workspaceTimeout.Token),
                    CancellationToken.None);
                load = await loaderTask.WaitAsync(workspaceTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (loaderTask is not null)
                {
                    _ = ObserveLateLoadAsync(loaderTask);
                }
                return await CommitInterruptionAsync(
                    coordinator,
                    actualProvenance,
                    toolchain,
                    HostStage.WorkspaceLoad,
                    cancellationToken.IsCancellationRequested
                        ? HostExecutionOutcome.Cancelled
                        : HostExecutionOutcome.Timeout,
                    controls,
                    transitions,
                    loaderFact).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await CommitFailureAsync(
                    coordinator,
                    actualProvenance,
                    toolchain,
                    "host.workspace-load.failed",
                    HostArtifactState.Invalidated,
                    controls,
                    transitions,
                    loaderFact).ConfigureAwait(false);
            }

            var hostDiagnostics = MapLoaderDiagnostics(load.Diagnostics);
            if (load.Status == RepositoryLoadStatus.Cancelled)
            {
                loaderFact = load.PrimaryFailure;
                return await CommitInterruptionAsync(
                    coordinator,
                    actualProvenance,
                    toolchain,
                    HostStage.WorkspaceLoad,
                    cancellationToken.IsCancellationRequested
                        ? HostExecutionOutcome.Cancelled
                        : HostExecutionOutcome.Timeout,
                    controls,
                    transitions,
                    loaderFact,
                    hostDiagnostics).ConfigureAwait(false);
            }
            if (load.Status != RepositoryLoadStatus.Success || load.Session is null)
            {
                loaderFact = NormalizeLoaderFact(load.PrimaryFailure);
                return await CommitFailureAsync(
                    coordinator,
                    actualProvenance,
                    toolchain,
                    "host.workspace-load.failed",
                    HostArtifactState.Invalidated,
                    controls,
                    transitions,
                    loaderFact,
                    diagnostics: hostDiagnostics).ConfigureAwait(false);
            }

            var session = load.Session;
            ClassifiedRepositorySession classified;
            ClassificationSet classifications;
            try
            {
                await controls.ReachStageAsync(HostStage.Classification, totalToken)
                    .ConfigureAwait(false);
                totalToken.ThrowIfCancellationRequested();
                classified = new SymbolClassifier().ClassifySession(
                    session,
                    policy.TargetProfile,
                    totalToken);
                if (classified.Classification.Status == ClassificationRunStatus.Cancelled)
                {
                    throw new OperationCanceledException(totalToken);
                }
                if (classified.Classification.Status != ClassificationRunStatus.Success
                    || classified.Classification.ClassificationSet is not { } accepted)
                {
                    return await CompleteComponentFailureAsync(
                        session,
                        coordinator,
                        toolchain,
                        "host.classification.failed",
                        controls,
                        transitions,
                        loaderFact,
                        hostDiagnostics).ConfigureAwait(false);
                }
                classifications = accepted;
            }
            catch (OperationCanceledException)
            {
                return await CompleteComponentInterruptionAsync(
                    session,
                    coordinator,
                    toolchain,
                    HostStage.Classification,
                    cancellationToken,
                    controls,
                    transitions,
                    loaderFact,
                    hostDiagnostics).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await CompleteComponentFailureAsync(
                    session,
                    coordinator,
                    toolchain,
                    "host.classification.failed",
                    controls,
                    transitions,
                    loaderFact,
                    hostDiagnostics).ConfigureAwait(false);
            }

            ObservedRepositorySession observed;
            try
            {
                await controls.ReachStageAsync(HostStage.DocumentationObservation, totalToken)
                    .ConfigureAwait(false);
                totalToken.ThrowIfCancellationRequested();
                observed = new DocumentationObserver().Observe(classified, totalToken);
                if (observed.Status == DocumentationObservationRunStatus.Cancelled)
                {
                    throw new OperationCanceledException(totalToken);
                }
                if (observed.Status != DocumentationObservationRunStatus.Success)
                {
                    return await CompleteComponentFailureAsync(
                        session,
                        coordinator,
                        toolchain,
                        "host.documentation-observation.failed",
                        controls,
                        transitions,
                        loaderFact,
                        hostDiagnostics).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return await CompleteComponentInterruptionAsync(
                    session,
                    coordinator,
                    toolchain,
                    HostStage.DocumentationObservation,
                    cancellationToken,
                    controls,
                    transitions,
                    loaderFact,
                    hostDiagnostics).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await CompleteComponentFailureAsync(
                    session,
                    coordinator,
                    toolchain,
                    "host.documentation-observation.failed",
                    controls,
                    transitions,
                    loaderFact,
                    hostDiagnostics).ConfigureAwait(false);
            }

            PolicyEvidenceExtractionOutcome extracted;
            try
            {
                await controls.ReachStageAsync(HostStage.PolicyEvidence, totalToken)
                    .ConfigureAwait(false);
                totalToken.ThrowIfCancellationRequested();
                extracted = new PolicyEvidenceExtractor().Extract(
                    classified,
                    observed,
                    policy,
                    totalToken);
                if (extracted.Status == PolicyEvidenceExtractionStatus.Cancelled)
                {
                    throw new OperationCanceledException(totalToken);
                }
                if (extracted.Status != PolicyEvidenceExtractionStatus.Success)
                {
                    return await CompleteComponentFailureAsync(
                        session,
                        coordinator,
                        toolchain,
                        "host.policy-evidence.failed",
                        controls,
                        transitions,
                        loaderFact,
                        hostDiagnostics).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return await CompleteComponentInterruptionAsync(
                    session,
                    coordinator,
                    toolchain,
                    HostStage.PolicyEvidence,
                    cancellationToken,
                    controls,
                    transitions,
                    loaderFact,
                    hostDiagnostics).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await CompleteComponentFailureAsync(
                    session,
                    coordinator,
                    toolchain,
                    "host.policy-evidence.failed",
                    controls,
                    transitions,
                    loaderFact,
                    hostDiagnostics).ConfigureAwait(false);
            }

            byte[] canonical;
            try
            {
                await controls.ReachStageAsync(HostStage.Audit, totalToken).ConfigureAwait(false);
                totalToken.ThrowIfCancellationRequested();
                if (controls.Fault == ProductionHostFault.AuditError)
                {
                    throw new InvalidOperationException(
                        "The validation control stimulated an aggregation failure.");
                }
                var inputs = AuditInputAssembler.Assemble(classifications, policy, extracted);
                var audit = AuditAggregator.Aggregate(
                    policy.TargetProfile,
                    classifications,
                    policy,
                    inputs);
                canonical = AuditJson.Write(audit);
                totalToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                return await CompleteComponentInterruptionAsync(
                    session,
                    coordinator,
                    toolchain,
                    HostStage.Audit,
                    cancellationToken,
                    controls,
                    transitions,
                    loaderFact,
                    hostDiagnostics).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await CompleteComponentFailureAsync(
                    session,
                    coordinator,
                    toolchain,
                    "host.audit.aggregation-failed",
                    controls,
                    transitions,
                    loaderFact,
                    hostDiagnostics).ConfigureAwait(false);
            }

            AuditOutcome auditOutcome;
            try
            {
                await controls.ReachStageAsync(HostStage.ResultValidation, totalToken)
                    .ConfigureAwait(false);
                totalToken.ThrowIfCancellationRequested();
                _ = AuditParser.Parse(canonical);
                auditOutcome = SummarizeOutcome(canonical);
                _ = processMeter.Reconcile();
                await controls.ReachAsync(
                    ProductionHostControlPoint.ProcessObservation,
                    totalToken).ConfigureAwait(false);
                _ = processMeter.Reconcile();
            }
            catch (OperationCanceledException)
            {
                return await CompleteComponentInterruptionAsync(
                    session,
                    coordinator,
                    toolchain,
                    HostStage.ResultValidation,
                    cancellationToken,
                    controls,
                    transitions,
                    loaderFact,
                    diagnostics: hostDiagnostics).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await CompleteComponentFailureAsync(
                    session,
                    coordinator,
                    toolchain,
                    "host.result-validation.failed",
                    controls,
                    transitions,
                    loaderFact,
                    hostDiagnostics).ConfigureAwait(false);
            }

            Task? shutdownTask = null;
            try
            {
                using var shutdownTimeout = CancellationTokenSource.CreateLinkedTokenSource(totalToken);
                shutdownTimeout.CancelAfter(controls.Deadline("graceful-shutdown-timeout"));
                await controls.ReachStageAsync(HostStage.Shutdown, shutdownTimeout.Token)
                    .ConfigureAwait(false);
                shutdownTimeout.Token.ThrowIfCancellationRequested();
                shutdownTask = DisposeSessionOnWorkerAsync(session, controls.Shutdown);
                try
                {
                    await shutdownTask.WaitAsync(shutdownTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _ = ObserveLateDisposalAsync(shutdownTask);
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                if (shutdownTask is null)
                {
                    shutdownTask = DisposeSessionOnWorkerAsync(session, controls.Shutdown);
                    _ = ObserveLateDisposalAsync(shutdownTask);
                }
                return await CommitInterruptionAsync(
                    coordinator,
                    actualProvenance,
                    toolchain,
                    HostStage.Shutdown,
                    cancellationToken.IsCancellationRequested
                        ? HostExecutionOutcome.Cancelled
                        : HostExecutionOutcome.Timeout,
                    controls,
                    transitions,
                    loaderFact,
                    hostDiagnostics).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await CommitFailureAsync(
                    coordinator,
                    actualProvenance,
                    toolchain,
                    "host.shutdown.failed",
                    HostArtifactState.Invalidated,
                    controls,
                    transitions,
                    loaderFact,
                    diagnostics: hostDiagnostics).ConfigureAwait(false);
            }

            using var callerPublicationRegistration = cancellationToken.Register(() =>
                RegisterPublicationInterruption(
                    coordinator,
                    actualProvenance,
                    toolchain,
                    HostExecutionOutcome.Cancelled));
            using var timeoutPublicationRegistration = totalDeadline.Token.Register(() =>
                RegisterPublicationInterruption(
                    coordinator,
                    actualProvenance,
                    toolchain,
                    HostExecutionOutcome.Timeout));
            try
            {
                await controls.ReachStageAsync(HostStage.Publication, totalToken)
                    .ConfigureAwait(false);
                totalToken.ThrowIfCancellationRequested();
                var existingBytes = meter.Reconcile();
                var bound = HostContractResources.RequireBound("temporary-disk-bytes");
                if (checked(existingBytes + canonical.LongLength) > bound)
                {
                    return await CommitTemporaryDiskBoundAsync(
                        publisher,
                        meter,
                        coordinator,
                        actualProvenance,
                        toolchain,
                        controls,
                        transitions,
                        loaderFact,
                        hostDiagnostics).ConfigureAwait(false);
                }
                publisher.Stage(canonical);
                Record(controls, transitions, "staging-created-in-destination");
                meter.Reconcile();
                if (meter.HighWater > bound)
                {
                    return await CommitTemporaryDiskBoundAsync(
                        publisher,
                        meter,
                        coordinator,
                        actualProvenance,
                        toolchain,
                        controls,
                        transitions,
                        loaderFact,
                        hostDiagnostics).ConfigureAwait(false);
                }
                await controls.ReachAsync(
                    ProductionHostControlPoint.TemporaryDiskHighWater,
                    totalToken).ConfigureAwait(false);
                meter.Reconcile();
                if (meter.HighWater > bound)
                {
                    return await CommitTemporaryDiskBoundAsync(
                        publisher,
                        meter,
                        coordinator,
                        actualProvenance,
                        toolchain,
                        controls,
                        transitions,
                        loaderFact,
                        hostDiagnostics).ConfigureAwait(false);
                }
                await controls.ReachAsync(
                    ProductionHostControlPoint.PublicationStagingReady,
                    totalToken).ConfigureAwait(false);
                await controls.ReachAsync(
                    ProductionHostControlPoint.LateCompletion,
                    totalToken).ConfigureAwait(false);
                await controls.ReachAsync(
                    ProductionHostControlPoint.BeforeCommit,
                    totalToken).ConfigureAwait(false);
                totalToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                var cleanupSucceeded = publisher.TryCleanupStaging();
                return await CommitRegisteredInterruptionAsync(
                    coordinator,
                    actualProvenance,
                    toolchain,
                    HostStage.Publication,
                    cancellationToken.IsCancellationRequested
                        ? HostExecutionOutcome.Cancelled
                        : HostExecutionOutcome.Timeout,
                    controls,
                    transitions,
                    loaderFact,
                    hostDiagnostics,
                    cleanupSucceeded,
                    MeasuredBoundsWithinThreshold(meter).ToArray()).ConfigureAwait(false);
            }
            catch (Exception)
            {
                var cleanupSucceeded = publisher.TryCleanupStaging();
                return await CommitFailureAsync(
                    coordinator,
                    actualProvenance,
                    toolchain,
                    cleanupSucceeded
                        ? "host.publication.finalization-failed"
                        : "host.publication.cleanup-failed",
                    HostArtifactState.Invalidated,
                    controls,
                    transitions,
                    loaderFact,
                    diagnostics: hostDiagnostics,
                    measuredBounds: MeasuredBoundsWithinThreshold(meter)).ConfigureAwait(false);
            }

            await controls.ReachAsync(
                ProductionHostControlPoint.BeforePublicationDecision,
                CancellationToken.None).ConfigureAwait(false);
            if (!coordinator.TryAcquirePublicationDecision(
                    out var publicationDecision,
                    out var winningCause)
                || publicationDecision is null)
            {
                var cleanupSucceeded = publisher.TryCleanupStaging();
                if (winningCause is not null
                    && TryCommitRegisteredCause(
                        coordinator,
                        winningCause,
                        cleanupSucceeded,
                        hostDiagnostics,
                        MeasuredBoundsWithinThreshold(meter),
                        out var accepted))
                {
                    RecordAcceptedFailure(controls, transitions, accepted);
                    await RunRejectedLateAttemptAsync(
                        coordinator,
                        controls,
                        transitions).ConfigureAwait(false);
                    return new ProductionAuditOutcome(
                        accepted,
                        null,
                        loaderFact,
                        transitions);
                }
                throw new InvalidOperationException("A terminal cause won before publication linearization.");
            }

            string committedSha256;
            try
            {
                committedSha256 = publisher.CommitRename();
            }
            catch (Exception)
            {
                Record(controls, transitions, "atomic-replace-attempt-failed");
                var cleanupSucceeded = publisher.TryCleanupStaging();
                if (cleanupSucceeded)
                {
                    Record(controls, transitions, "staging-cleanup-completed");
                }
                var code = cleanupSucceeded
                    ? "host.publication.finalization-failed"
                    : "host.publication.cleanup-failed";
                var failure = CreateFailureRecord(
                    coordinator,
                    actualProvenance,
                    toolchain,
                    code,
                    HostArtifactState.Invalidated,
                    diagnostics: hostDiagnostics,
                    measuredBounds: MeasuredBoundsWithinThreshold(meter));
                publicationDecision.CommitFailureAfterCleanup(failure);
                Record(controls, transitions, "terminal-commit-publication-failure");
                await RunRejectedLateAttemptAsync(
                    coordinator,
                    controls,
                    transitions).ConfigureAwait(false);
                return new ProductionAuditOutcome(failure, null, loaderFact, transitions);
            }

            publicationDecision.CommitRename(new CommittedCanonicalResult(
                canonical,
                committedSha256,
                actualProvenance,
                toolchain));
            Record(controls, transitions, "atomic-rename-committed");

            await controls.ReachAsync(
                ProductionHostControlPoint.AfterCommit,
                CancellationToken.None).ConfigureAwait(false);
            var success = coordinator.DeriveSuccessRecord(
                auditOutcome,
                diagnostics: hostDiagnostics,
                measuredBounds: ResourceFactsWithinThreshold(meter, processMeter));
            return new ProductionAuditOutcome(success, canonical, loaderFact, transitions);
        }
    }

    private static async Task<ProductionAuditOutcome> CommitTemporaryDiskBoundAsync(
        AtomicResultPublisher publisher,
        TemporaryDiskMeter meter,
        HostTerminalCoordinator coordinator,
        HostBuildProvenance provenance,
        HostToolchainFact toolchain,
        ProductionAuditHostControls controls,
        List<string> transitions,
        LoaderFact? loaderFact,
        IReadOnlyList<HostDiagnosticFact> diagnostics)
    {
        var cleanupSucceeded = publisher.TryCleanupStaging();
        return await CommitFailureAsync(
            coordinator,
            provenance,
            toolchain,
            cleanupSucceeded
                ? "host.result-validation.temporary-disk-bound"
                : "host.publication.cleanup-failed",
            HostArtifactState.Invalidated,
            controls,
            transitions,
            loaderFact,
            diagnostics: diagnostics,
            measuredBounds: MeasuredBoundsWithinThreshold(meter)).ConfigureAwait(false);
    }

    private static IReadOnlyList<HostMeasuredBound> MeasuredBoundsWithinThreshold(
        TemporaryDiskMeter meter) =>
        meter.HighWater <= HostContractResources.RequireBound("temporary-disk-bytes")
            ? [meter.ToFact()]
            : [];

    private static IReadOnlyList<HostMeasuredBound> ResourceFactsWithinThreshold(
        TemporaryDiskMeter meter,
        ToolchainProcessMeter processMeter)
    {
        _ = processMeter.Reconcile();
        return MeasuredBoundsWithinThreshold(meter)
            .Append(processMeter.ToFact())
            .ToArray();
    }

    private async Task<ProductionAuditOutcome> CompleteComponentFailureAsync(
        LoadedRepositorySession session,
        HostTerminalCoordinator coordinator,
        HostToolchainFact toolchain,
        string code,
        ProductionAuditHostControls controls,
        List<string> transitions,
        LoaderFact? loaderFact,
        IReadOnlyList<HostDiagnosticFact> diagnostics)
    {
        await DisposeLateSafeAsync(session).ConfigureAwait(false);
        return await CommitFailureAsync(
            coordinator,
            actualProvenance,
            toolchain,
            code,
            HostArtifactState.Invalidated,
            controls,
            transitions,
            loaderFact,
            diagnostics: diagnostics).ConfigureAwait(false);
    }

    private async Task<ProductionAuditOutcome> CompleteComponentInterruptionAsync(
        LoadedRepositorySession session,
        HostTerminalCoordinator coordinator,
        HostToolchainFact toolchain,
        HostStage stage,
        CancellationToken callerCancellationToken,
        ProductionAuditHostControls controls,
        List<string> transitions,
        LoaderFact? loaderFact,
        IReadOnlyList<HostDiagnosticFact> diagnostics)
    {
        await DisposeLateSafeAsync(session).ConfigureAwait(false);
        return await CommitInterruptionAsync(
            coordinator,
            actualProvenance,
            toolchain,
            stage,
            callerCancellationToken.IsCancellationRequested
                ? HostExecutionOutcome.Cancelled
                : HostExecutionOutcome.Timeout,
            controls,
            transitions,
            loaderFact,
            diagnostics).ConfigureAwait(false);
    }

    private static async Task<ProductionAuditOutcome> CommitInterruptionAsync(
        HostTerminalCoordinator coordinator,
        HostBuildProvenance provenance,
        HostToolchainFact toolchain,
        HostStage stage,
        HostExecutionOutcome outcome,
        ProductionAuditHostControls controls,
        List<string> transitions,
        LoaderFact? loaderFact,
        IEnumerable<HostDiagnosticFact>? diagnostics = null,
        params HostMeasuredBound[] measuredBounds)
    {
        var suffix = outcome == HostExecutionOutcome.Cancelled ? "cancelled" : "timeout";
        return await CommitFailureAsync(
            coordinator,
            provenance,
            toolchain,
            $"host.{HostVocabulary.GetId(stage)}.{suffix}",
            HostArtifactState.Invalidated,
            controls,
            transitions,
            loaderFact,
            diagnostics,
            measuredBounds: measuredBounds).ConfigureAwait(false);
    }

    private static async Task<ProductionAuditOutcome> CommitRegisteredInterruptionAsync(
        HostTerminalCoordinator coordinator,
        HostBuildProvenance provenance,
        HostToolchainFact toolchain,
        HostStage stage,
        HostExecutionOutcome outcome,
        ProductionAuditHostControls controls,
        List<string> transitions,
        LoaderFact? loaderFact,
        IEnumerable<HostDiagnosticFact>? diagnostics,
        bool cleanupSucceeded,
        params HostMeasuredBound[] measuredBounds)
    {
        var suffix = outcome == HostExecutionOutcome.Cancelled ? "cancelled" : "timeout";
        var candidate = CreateFailureRecord(
            coordinator,
            provenance,
            toolchain,
            $"host.{HostVocabulary.GetId(stage)}.{suffix}",
            HostArtifactState.Invalidated,
            diagnostics,
            measuredBounds: measuredBounds);
        _ = coordinator.TryRegisterCause(candidate, out var winningCause);
        if (!TryCommitRegisteredCause(
                coordinator,
                winningCause,
                cleanupSucceeded,
                diagnostics,
                measuredBounds,
                out var accepted))
        {
            throw new InvalidOperationException("The accepted interruption cause could not be committed.");
        }
        RecordAcceptedFailure(controls, transitions, accepted);
        await RunRejectedLateAttemptAsync(
            coordinator,
            controls,
            transitions).ConfigureAwait(false);
        return new ProductionAuditOutcome(accepted, null, loaderFact, transitions);
    }

    private static bool TryCommitRegisteredCause(
        HostTerminalCoordinator coordinator,
        HostTerminalRecord registered,
        bool cleanupSucceeded,
        IEnumerable<HostDiagnosticFact>? diagnostics,
        IEnumerable<HostMeasuredBound>? measuredBounds,
        out HostTerminalRecord accepted)
    {
        var supportingDiagnostics = diagnostics ?? [];
        if (!cleanupSucceeded)
        {
            supportingDiagnostics = supportingDiagnostics.Append(new HostDiagnosticFact(
                "host.publication.cleanup-failed",
                HostStage.Publication,
                HostDiagnosticSeverity.Error,
                "host.publication.cleanup-failed"));
        }
        var final = registered with
        {
            Diagnostics = HostDiagnosticEnvelope.Normalize(
                registered.Diagnostics.Concat(supportingDiagnostics),
                checked((int)HostContractResources.RequireBound("diagnostic-count")),
                checked((int)HostContractResources.RequireBound("diagnostic-utf8-bytes"))),
            MeasuredBounds = (measuredBounds ?? []).ToImmutableArray(),
        };
        return coordinator.TryCommitRegisteredCause(registered, final, out accepted);
    }

    private static async Task<ProductionAuditOutcome> CommitFailureAsync(
        HostTerminalCoordinator coordinator,
        HostBuildProvenance provenance,
        HostToolchainFact toolchain,
        string code,
        HostArtifactState artifactState,
        ProductionAuditHostControls controls,
        List<string> transitions,
        LoaderFact? loaderFact,
        IEnumerable<HostDiagnosticFact>? diagnostics = null,
        IEnumerable<HostMeasuredBound>? measuredBounds = null)
    {
        var failure = CreateFailureRecord(
            coordinator,
            provenance,
            toolchain,
            code,
            artifactState,
            diagnostics,
            measuredBounds);
        if (!coordinator.TryCommitNonSuccess(failure, out var accepted)
            || !ReferenceEquals(accepted, failure))
        {
            throw new InvalidOperationException("A stale terminal failure attempt was rejected.");
        }
        RecordAcceptedFailure(controls, transitions, failure);
        await RunRejectedLateAttemptAsync(
            coordinator,
            controls,
            transitions).ConfigureAwait(false);
        return new ProductionAuditOutcome(failure, null, loaderFact, transitions);
    }

    private static HostTerminalRecord CreateFailureRecord(
        HostTerminalCoordinator coordinator,
        HostBuildProvenance provenance,
        HostToolchainFact toolchain,
        string code,
        HostArtifactState artifactState,
        IEnumerable<HostDiagnosticFact>? diagnostics = null,
        IEnumerable<HostMeasuredBound>? measuredBounds = null)
    {
        var row = HostContractResources.RequireFailure(code);
        var primaryDiagnostic = new HostDiagnosticFact(
            row.Code,
            row.Stage,
            HostDiagnosticSeverity.Error,
            row.Code);
        var normalizedDiagnostics = HostDiagnosticEnvelope.Normalize(
            new[] { primaryDiagnostic }.Concat(diagnostics ?? []),
            checked((int)HostContractResources.RequireBound("diagnostic-count")),
            checked((int)HostContractResources.RequireBound("diagnostic-utf8-bytes")));
        return new HostTerminalRecord(
            row.ExecutionOutcome,
            null,
            HostTerminalState.CommittedNonSuccess,
            row,
            provenance,
            toolchain,
            new HostOutputCommit(artifactState, null, 0),
            normalizedDiagnostics,
            (measuredBounds ?? []).ToImmutableArray(),
            coordinator.NextCauseSequence());
    }

    private static void RegisterPublicationInterruption(
        HostTerminalCoordinator coordinator,
        HostBuildProvenance provenance,
        HostToolchainFact toolchain,
        HostExecutionOutcome outcome)
    {
        var suffix = outcome == HostExecutionOutcome.Cancelled ? "cancelled" : "timeout";
        var candidate = CreateFailureRecord(
            coordinator,
            provenance,
            toolchain,
            $"host.publication.{suffix}",
            HostArtifactState.Invalidated);
        _ = coordinator.TryRegisterCause(candidate, out _);
    }

    private static void RecordAcceptedFailure(
        ProductionAuditHostControls controls,
        List<string> transitions,
        HostTerminalRecord failure)
    {
        if (failure.ExecutionOutcome == HostExecutionOutcome.PublicationFailure)
        {
            Record(controls, transitions, "terminal-commit-publication-failure");
        }
        else if (failure.ExecutionOutcome == HostExecutionOutcome.Cancelled)
        {
            Record(controls, transitions, "terminal-commit-cancelled");
        }
    }

    private static async Task RunRejectedLateAttemptAsync(
        HostTerminalCoordinator coordinator,
        ProductionAuditHostControls controls,
        List<string> transitions)
    {
        if (controls.LateCompletion is null)
        {
            return;
        }
        await controls.LateCompletion(CancellationToken.None).ConfigureAwait(false);
        if (coordinator.TryBeginLatePublishedResultAttempt())
        {
            throw new InvalidOperationException(
                "A late terminal attempt replaced an authoritative terminal record.");
        }
        Record(
            controls,
            transitions,
            controls.LateAttemptKind == ProductionLateAttemptKind.CompetingTerminal
                ? "competing-terminal-attempt-rejected"
                : "late-terminal-attempt-rejected");
    }

    private static async Task DisposeLateSafeAsync(LoadedRepositorySession session)
    {
        var disposal = DisposeSessionOnWorkerAsync(session);
        try
        {
            await disposal.WaitAsync(TimeSpan.FromMilliseconds(
                    HostContractResources.RequireBound("graceful-shutdown-timeout")))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A bounded late disposal failure cannot replace the earlier accepted stage cause.
            _ = ObserveLateDisposalAsync(disposal);
        }
    }

    private static Task DisposeSessionOnWorkerAsync(
        LoadedRepositorySession session,
        Func<LoadedRepositorySession, Task>? shutdown = null) =>
        Task.Run(async () =>
        {
            if (shutdown is not null)
            {
                await shutdown(session).ConfigureAwait(false);
                return;
            }
            await session.DisposeAsync().ConfigureAwait(false);
        });

    private static async Task ObserveLateDisposalAsync(Task disposal)
    {
        try
        {
            await disposal.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The authoritative bounded shutdown outcome is already fixed; observe late faults.
        }
    }

    private static async Task ObserveLateLoadAsync(Task<RepositoryLoadOutcome> loaderTask)
    {
        try
        {
            var late = await loaderTask.ConfigureAwait(false);
            if (late.Session is not null)
            {
                await DisposeSessionOnWorkerAsync(late.Session).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The authoritative workspace timeout/cancellation is already fixed; observe late faults.
        }
    }

    private static async Task ObserveLateSdkDiscoveryAsync(
        Task<RegisteredToolchain> sdkDiscovery)
    {
        try
        {
            _ = await sdkDiscovery.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The authoritative SDK timeout/cancellation is already fixed; observe late faults.
        }
    }

    private static AuditOutcome SummarizeOutcome(byte[] canonical)
    {
        using var document = JsonDocument.Parse(canonical);
        var outcomes = document.RootElement.GetProperty("results")
            .EnumerateArray()
            .Select(result => result.GetProperty("auditOutcome").GetString())
            .ToArray();
        if (outcomes.Contains("audit.outcome.violation", StringComparer.Ordinal))
        {
            return AuditOutcome.Violation;
        }
        if (outcomes.Contains("audit.outcome.compliant", StringComparer.Ordinal))
        {
            return AuditOutcome.Compliant;
        }
        return AuditOutcome.Skipped;
    }

    private static LoaderFact? NormalizeLoaderFact(LoaderFact? fact) =>
        fact?.Code == "graph.target-framework-not-single"
            ? new LoaderFact("workspace", "loader.unsupported.multi-targeting")
            : fact;

    private static IReadOnlyList<HostDiagnosticFact> MapLoaderDiagnostics(
        IEnumerable<LoaderDiagnostic> diagnostics) => diagnostics
        .Select(diagnostic => new HostDiagnosticFact(
            diagnostic.Code,
            diagnostic.Stage == "workspace"
                ? HostStage.WorkspaceLoad
                : HostStage.Internal,
            diagnostic.Severity switch
            {
                "error" => HostDiagnosticSeverity.Error,
                "warning" => HostDiagnosticSeverity.Warning,
                _ => HostDiagnosticSeverity.Information,
            },
            diagnostic.Code))
        .ToArray();

    private static void Record(
        ProductionAuditHostControls controls,
        ICollection<string> transitions,
        string transition)
    {
        transitions.Add(transition);
        controls.Record(transition);
    }
}
