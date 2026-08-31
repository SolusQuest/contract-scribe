using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContractScribe.Agent.Providers;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;
using ContractScribe.Core.Hosting;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

internal static class CampaignCommandRunner
{
    internal const string ProductRevisionId = "product.contract-scribe.campaign-v1";

    internal static async Task<CliExecutionResult> RunAsync(
        CliBuildIdentity identity,
        CampaignPreflightResult preflight,
        CancellationToken cancellationToken,
        Func<string, string?>? credentialAccessor = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(preflight);
        var configuration = preflight.Configuration.Document;
        if (!preflight.Configuration.Revalidate()
            || !MatchesProductRevision(identity, configuration.Planning))
        {
            return Present(identity, preflight.Operation, "preflight",
                "campaign.invalid-configuration", null);
        }

        FileCampaignCheckpointStore store;
        try
        {
            store = new FileCampaignCheckpointStore(preflight.StatePath, preflight.RepositoryRoot);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Present(identity, preflight.Operation, "state", "campaign.state-unsafe", null);
        }

        CampaignCheckpointReadResult read;
        try
        {
            read = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Present(identity, preflight.Operation, "state", "campaign.cancelled", null);
        }
        var stateFailure = ClassifyInitialRead(preflight.Operation, read);
        if (stateFailure is not null)
        {
            return Present(identity, preflight.Operation, "state", stateFailure, null);
        }

        CampaignAcceptedCheckpoint? existing = null;
        if (preflight.Operation == CampaignOperation.Resume)
        {
            var accepted = CampaignCheckpointAcceptance.AcceptCurrent(read);
            if (accepted.Kind != CampaignCheckpointAcceptanceKind.Accepted
                || accepted.AcceptedCheckpoint is not { } checkpoint)
            {
                return Present(identity, preflight.Operation, "state",
                    AcceptanceOutcome(accepted.Kind), null);
            }
            existing = checkpoint;
            if (!MatchesProductRevision(configuration.Planning, checkpoint.Artifact.State))
            {
                return Present(identity, preflight.Operation, "state",
                    "campaign.incompatible-snapshot", checkpoint.Artifact.CheckpointRevision);
            }
        }

        CampaignTerminal? campaignTerminal = null;
        var host = new ProductionRepositorySessionHost(new HostBuildProvenance(identity.SourceRevision));
        var hostOutcome = await host.RunAsync(
            new ProductionAuditRequest(
                preflight.RepositoryRoot,
                preflight.InputPath,
                preflight.PolicyBytes,
                PublicationTarget: null,
                PublishResult: false),
            new ProductionAuditHostControls(SessionConsumer: async (bundle, token) =>
            {
                campaignTerminal = await RunInSessionAsync(
                    preflight,
                    configuration,
                    store,
                    existing,
                    bundle,
                    credentialAccessor ?? Environment.GetEnvironmentVariable,
                    token).ConfigureAwait(false);
            }),
            cancellationToken).ConfigureAwait(false);

        if (campaignTerminal is not null)
        {
            // Issue #139 gives campaign terminal selection public precedence. In particular,
            // an exact-readback C3 terminal cannot be replaced by later host shutdown or process status.
            return CampaignCliPresentation.Present(identity, campaignTerminal);
        }

        var outcome = hostOutcome.Terminal.ExecutionOutcome switch
        {
            HostExecutionOutcome.Cancelled => "campaign.cancelled",
            HostExecutionOutcome.Timeout => "campaign.timeout",
            HostExecutionOutcome.LoadFailure or HostExecutionOutcome.EnvironmentUnavailable
                or HostExecutionOutcome.InvalidInput => "campaign.load-failure",
            _ => "campaign.host-contract-error",
        };
        return Present(identity, preflight.Operation, "execution", outcome, existing?.Artifact.CheckpointRevision);
    }

    private static async Task<CampaignTerminal> RunInSessionAsync(
        CampaignPreflightResult preflight,
        CampaignConfigurationDocument configuration,
        ICampaignCheckpointStore store,
        CampaignAcceptedCheckpoint? existing,
        ProductionRepositorySessionBundle bundle,
        Func<string, string?> credentialAccessor,
        CancellationToken cancellationToken)
    {
        if (!preflight.Configuration.Revalidate())
        {
            return Terminal(preflight.Operation, "preflight", "campaign.invalid-configuration", existing);
        }

        CampaignPlanningInput planning;
        CampaignWorkPlan plan;
        CampaignPlanningExecutionPolicy policy;
        CampaignScribeExecutionCapability execution;
        try
        {
            policy = configuration.CreateExecutionPolicy();
            execution = configuration.CreateExecutionCapability(policy);
            planning = CreatePlanningInput(preflight, configuration, policy, bundle, cancellationToken);
            plan = CampaignPlanner.Plan(planning);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Terminal(preflight.Operation, "execution", "campaign.cancelled", existing);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return Terminal(preflight.Operation, "execution", "campaign.host-contract-error", existing);
        }

        CampaignAcceptedCheckpoint current;
        if (existing is null)
        {
            var state = CampaignStateFactory.CreateInitial(
                configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                configuration.ScribeRequest.StyleProfileTemplate.ExactProjection,
                execution,
                bundle.Session.InputIdentity,
                planning,
                plan);
            var artifact = CampaignStateJson.CreateArtifact(state);
            CampaignCheckpointAcceptanceResult accepted;
            CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.InitialBeforeCreate);
            using (CampaignProcessBoundaryHooks.EnterReplacementScope(
                       CampaignProcessBoundaryHooks.InitialReplacementScope))
            {
                accepted = await CampaignCheckpointAcceptance.AcceptInitialAsync(
                    store,
                    CampaignCheckpointAcceptance.CreateInitialAuthority(artifact),
                    cancellationToken).ConfigureAwait(false);
            }
            if (accepted.Kind != CampaignCheckpointAcceptanceKind.Accepted
                || accepted.AcceptedCheckpoint is not { } acceptedCheckpoint)
            {
                return Terminal(
                    preflight.Operation,
                    "state",
                    accepted.Kind == CampaignCheckpointAcceptanceKind.Conflict
                        ? "campaign.state-present"
                        : AcceptanceOutcome(accepted.Kind),
                    null);
            }
            current = acceptedCheckpoint;
        }
        else
        {
            current = existing;
            if (string.Equals(
                    current.Artifact.State.Snapshot.OpaqueSnapshotBinding,
                    preflight.SnapshotBinding,
                    StringComparison.Ordinal))
            {
                try
                {
                    CampaignStateFactory.ValidateCurrentContext(
                        current.Artifact.State,
                        execution,
                        configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                        configuration.ScribeRequest.StyleProfileTemplate.ExactProjection,
                        bundle.Session.InputIdentity,
                        planning,
                        plan);
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                {
                    return Terminal(preflight.Operation, "state",
                        "campaign.incompatible-snapshot", current);
                }
            }
            else
            {
                CampaignProcessBoundaryHooks.Reach(
                    CampaignProcessBoundaryHooks.ChangedBaseBeforeReconciliation);
                var reconciled = await ChangedBaseCampaignReconciler.ReconcileAsync(
                    current,
                    store,
                    execution,
                    configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                    configuration.ScribeRequest.StyleProfileTemplate.ExactProjection,
                    bundle.Session.InputIdentity,
                    planning,
                    plan,
                    preflight.Configuration.Revalidate,
                    cancellationToken).ConfigureAwait(false);
                if (reconciled.Kind != ChangedBaseCampaignReconciliationKind.Accepted
                    || reconciled.AcceptedCheckpoint is not { } acceptedSuccessor)
                {
                    var checkpoint = reconciled.AcceptedCheckpoint;
                    return reconciled.Kind switch
                    {
                        ChangedBaseCampaignReconciliationKind.Incompatible =>
                            Terminal(preflight.Operation, "state", "campaign.incompatible-snapshot", checkpoint),
                        ChangedBaseCampaignReconciliationKind.InvalidConfiguration =>
                            Terminal(preflight.Operation, "preflight", "campaign.invalid-configuration", checkpoint),
                        ChangedBaseCampaignReconciliationKind.Cancelled =>
                            Terminal(preflight.Operation, "execution", "campaign.cancelled", checkpoint),
                        _ => Terminal(
                            preflight.Operation,
                            "state",
                            AcceptanceOutcome(reconciled.CheckpointFailure
                                ?? CampaignCheckpointAcceptanceKind.InvalidRead),
                            checkpoint),
                    };
                }
                current = acceptedSuccessor;
            }
        }

        var m2Projection = JsonSerializer.SerializeToElement(new
        {
            m2ProjectionVersion = 1,
            maximumPatchElapsedMilliseconds = configuration.Planning.MaximumPatchElapsedMilliseconds,
        });
        var runtime = new DocumentationScribeRuntimeOptions(
            configuration.Provider.ProviderConfigurationId,
            configuration.Provider.ModelConfigurationId,
            configuration.Provider.ScribeProtocolId);
        var maximumIterations = checked(Math.Min(
            CampaignStateContract.MaximumWorkItems * 3,
            Math.Max(8, plan.WorkItems.Length * 4 + 4)));
        for (var iteration = 0; iteration < maximumIterations; iteration++)
        {
            var observed = await CampaignCheckpointAcceptance.AcceptCurrentAsync(
                store, cancellationToken).ConfigureAwait(false);
            if (observed.Kind != CampaignCheckpointAcceptanceKind.Accepted
                || observed.AcceptedCheckpoint is not { } acceptedCurrent)
            {
                return Terminal(preflight.Operation, "state", AcceptanceOutcome(observed.Kind), current);
            }
            current = acceptedCurrent;
            var stateNow = current.Artifact.State;
            if (stateNow.TerminalOutcome is
                { Kind: CampaignTerminalKind.Complete, Reason: CampaignTerminalReason.NoWork })
            {
                return Terminal(preflight.Operation, "campaign", "campaign.no-work", current);
            }
            if (stateNow.TerminalOutcome is { Kind: CampaignTerminalKind.Cancelled })
                return Terminal(preflight.Operation, "campaign", "campaign.cancelled", current);
            if (stateNow.TerminalOutcome is { Kind: CampaignTerminalKind.Timeout })
                return Terminal(preflight.Operation, "campaign", "campaign.timeout", current);
            if (stateNow.TerminalOutcome is { Kind: CampaignTerminalKind.Exhausted })
                return Terminal(preflight.Operation, "campaign", "campaign.budget-exhausted", current);

            var reconstructAcceptedTerminal = stateNow.TerminalOutcome is
            { Kind: CampaignTerminalKind.Complete, Reason: CampaignTerminalReason.AllWorkClosed }
                && stateNow.WorkItems.Any(item => item.Status == CampaignWorkStatus.Accepted);
            if (stateNow.ActiveReservation is CampaignPatchReservation
                || stateNow.WorkItems.Any(item => item.Status == CampaignWorkStatus.ProposalComplete)
                || reconstructAcceptedTerminal)
            {
                var patched = await DocumentationCampaignPatchExecutor.ExecuteAsync(new(
                    bundle.Classified,
                    bundle.Observed,
                    bundle.Policy,
                    bundle.AuditInputs.ToImmutableArray(),
                    bundle.Audit,
                    planning,
                    plan,
                    execution,
                    configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                    configuration.ScribeRequest.StyleProfileTemplate.ExactProjection,
                    m2Projection,
                    store,
                    cancellationToken,
                    cancellationToken,
                    DispatchGuard: preflight.Configuration.Revalidate)).ConfigureAwait(false);
                if (patched.Kind == DocumentationCampaignOutcomeKind.Reconstructed
                    && reconstructAcceptedTerminal)
                {
                    return Terminal(preflight.Operation, "campaign", "campaign.complete",
                        patched.Artifact is null ? current : AcceptedObservation(patched.Artifact));
                }
                if (patched.Kind == DocumentationCampaignOutcomeKind.Accepted
                    && patched.Artifact?.State.TerminalOutcome is
                    { Kind: CampaignTerminalKind.Complete, Reason: CampaignTerminalReason.AllWorkClosed })
                {
                    return Terminal(preflight.Operation, "campaign", "campaign.complete",
                        AcceptedObservation(patched.Artifact));
                }
                if (patched.Kind is DocumentationCampaignOutcomeKind.Accepted
                    or DocumentationCampaignOutcomeKind.Reconstructed
                    or DocumentationCampaignOutcomeKind.Reduced)
                {
                    continue;
                }
                return Terminal(preflight.Operation, "execution",
                    patched.CheckpointFailure is { } patchFailure
                        ? AcceptanceOutcome(patchFailure)
                        : PatchOutcome(patched.Kind),
                    patched.Artifact is null ? current : AcceptedObservation(patched.Artifact));
            }

            if (stateNow.TerminalOutcome is { Kind: CampaignTerminalKind.Complete })
            {
                return Terminal(preflight.Operation, "campaign", "campaign.complete", current);
            }

            var next = SelectNextWork(stateNow, plan);
            if (next is null)
            {
                return Terminal(preflight.Operation, "execution", "campaign.target-terminal", current);
            }

            ReadOnlyMemory<byte> request;
            try
            {
                request = CampaignScribeRequestBuilder.Build(
                    bundle, next, policy, configuration, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Terminal(preflight.Operation, "execution", "campaign.cancelled", current);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                return Terminal(preflight.Operation, "execution", "campaign.proposal-invalid", current);
            }

            var proposal = await DocumentationCampaignProposalExecutor.ExecuteAsync(new(
                bundle.Classified,
                bundle.Observed,
                bundle.Policy,
                bundle.AuditInputs.ToImmutableArray(),
                bundle.Audit,
                planning,
                plan,
                execution,
                configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                configuration.ScribeRequest.StyleProfileTemplate.ExactProjection,
                request,
                store,
                runtime,
                Exchange: null,
                ConfiguredAgentEntrypoint: null,
                ExecutionToken: cancellationToken,
                SettlementToken: cancellationToken,
                DeferredExchange: () => CreateExchange(configuration.Provider, credentialAccessor),
                DispatchGuard: preflight.Configuration.Revalidate)).ConfigureAwait(false);
            if (proposal.Kind == DocumentationCampaignProposalOutcomeKind.ProposalReady)
            {
                continue;
            }
            var proposalOutcome = proposal.CheckpointFailure is { } proposalFailure
                ? AcceptanceOutcome(proposalFailure)
                : proposal.Code == "campaign.credential.invalid"
                ? "campaign.invalid-configuration"
                : ProposalOutcome(proposal.Kind);
            return Terminal(preflight.Operation, "execution", proposalOutcome,
                proposal.Artifact is null ? current : AcceptedObservation(proposal.Artifact));
        }

        return Terminal(preflight.Operation, "execution", "campaign.host-contract-error", current);
    }

    private static CampaignPlanningWorkItem? SelectNextWork(
        CampaignCheckpointState state,
        CampaignWorkPlan plan) =>
        state.WorkItems.Zip(plan.WorkItems)
            .Where(pair => pair.First.Status == CampaignWorkStatus.Planned
                || pair.First.Status == CampaignWorkStatus.Closed
                && pair.First.ClosedOutcome is
                {
                    Code: CampaignWorkOutcomeCode.ProviderFailure,
                    ProviderDisposition: CampaignProviderFinalDisposition.Retryable,
                })
            .Select(pair => pair.Second)
            .FirstOrDefault();

    internal static IDocumentationScribeModelExchange? CreateExchange(
        CampaignProviderConfiguration provider,
        Func<string, string?> credentialAccessor)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(credentialAccessor);
        try
        {
            string? credential = null;
            if (string.Equals(provider.Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                credential = credentialAccessor("CONTRACTSCRIBE_PROVIDER_API_KEY");
                if (string.IsNullOrWhiteSpace(credential))
                {
                    return null;
                }
            }
            var options = new OpenAiCompatibleHttpTransportOptions(
                provider.Endpoint,
                provider.Model,
                provider.RequestProfile,
                networkEnabled: true,
                credential);
            return new CampaignHookedModelExchange(new OpenAiCompatibleHttpModelExchange(options));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static CampaignAcceptedCheckpoint AcceptedObservation(CampaignCheckpointArtifact artifact) =>
        CampaignCheckpointAcceptance.AcceptCurrent(CampaignCheckpointReadResult.Found(
            artifact.ExactUtf8Json.AsSpan(), artifact.CheckpointRevision, artifact.Sha256)).AcceptedCheckpoint!;

    private sealed class CampaignHookedModelExchange(IDocumentationScribeModelExchange inner)
        : IDocumentationScribeModelExchange, IDisposable
    {
        public async ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            var pending = inner.SendAsync(request, cancellationToken);
            CampaignProcessBoundaryHooks.Reach(CampaignProcessBoundaryHooks.ProposalDuringProviderDispatch);
            return await pending.ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (inner is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static string ProposalOutcome(DocumentationCampaignProposalOutcomeKind kind) => kind switch
    {
        DocumentationCampaignProposalOutcomeKind.NoWork => "campaign.no-work",
        DocumentationCampaignProposalOutcomeKind.UnsupportedOnly => "campaign.target-terminal",
        DocumentationCampaignProposalOutcomeKind.RetryableStop => "campaign.provider-retryable",
        DocumentationCampaignProposalOutcomeKind.Cancelled => "campaign.cancelled",
        DocumentationCampaignProposalOutcomeKind.TimedOut => "campaign.timeout",
        DocumentationCampaignProposalOutcomeKind.BudgetExhausted => "campaign.budget-exhausted",
        DocumentationCampaignProposalOutcomeKind.AmbiguousDispatch => "campaign.attempt-ambiguous",
        DocumentationCampaignProposalOutcomeKind.StateConflict => "campaign.state-conflict",
        DocumentationCampaignProposalOutcomeKind.TerminalStop => "campaign.provider-terminal",
        _ => "campaign.host-contract-error",
    };

    private static string PatchOutcome(DocumentationCampaignOutcomeKind kind) => kind switch
    {
        DocumentationCampaignOutcomeKind.NoWork => "campaign.no-work",
        DocumentationCampaignOutcomeKind.Rejected => "campaign.patch-rejected",
        DocumentationCampaignOutcomeKind.Stale => "campaign.patch-stale",
        DocumentationCampaignOutcomeKind.HostFailure => "campaign.patch-host-failure",
        DocumentationCampaignOutcomeKind.Cancelled => "campaign.cancelled",
        DocumentationCampaignOutcomeKind.TimedOut => "campaign.timeout",
        DocumentationCampaignOutcomeKind.BudgetExhausted => "campaign.budget-exhausted",
        DocumentationCampaignOutcomeKind.StateConflict => "campaign.state-conflict",
        DocumentationCampaignOutcomeKind.AmbiguousDispatch => "campaign.attempt-ambiguous",
        DocumentationCampaignOutcomeKind.TerminalStop => "campaign.target-terminal",
        _ => "campaign.host-contract-error",
    };

    internal static CampaignPlanningInput CreatePlanningInput(
        CampaignPreflightResult preflight,
        CampaignConfigurationDocument configuration,
        CampaignPlanningExecutionPolicy policy,
        ProductionRepositorySessionBundle bundle,
        CancellationToken cancellationToken)
    {
        var authorities = ImmutableArray.CreateBuilder<CampaignPlanningTargetAuthority>();
        var projector = new DocumentationDeclarationAuthorityProjector();
        foreach (var target in bundle.Classifications.Targets.Where(target =>
                     target.SupportStatus == SupportStatus.Supported
                     && target.Origin != ClassificationOrigin.Mixed))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projected = projector.Project(bundle.Observed, target, null, cancellationToken);
            if (!projected.IsSuccess || projected.Authority is not { } authority)
            {
                continue;
            }
            authorities.Add(authority);
        }

        var violationParents = CampaignPlanner.ReadViolationParentSymbols(bundle.Audit);
        var owners = authorities
            .GroupBy(authority => authority.Source switch
            {
                CampaignPlanningRepositorySourceAuthority repository =>
                    repository.PhysicalSourceCommitmentSha256 + "\0"
                    + repository.OwnerSpan.Start + "\0" + repository.OwnerSpan.End,
                _ => authority.Target.SymbolRef.ToString(),
            }, StringComparer.Ordinal)
            .Where(group => group.Any(authority =>
                violationParents.Contains(authority.Target.SymbolRef)))
            .Select(group => new CampaignPlanningOwnerAuthority(group.ToImmutableArray()))
            .ToImmutableArray();
        var executableStyleParents = CampaignPlanner.ReadExecutableStyleParentSymbols(
            bundle.Audit,
            owners);
        owners = owners.Select(owner => owner with
        {
            Targets = owner.Targets.Select(authority =>
                executableStyleParents.Contains(authority.Target.SymbolRef)
                    ? authority with
                    {
                        ExecutableStyleProfile = ExpandStyleProfile(configuration, authority),
                    }
                    : authority).ToImmutableArray(),
        }).ToImmutableArray();
        var evidence = bundle.Evidence.Bindings.Select(binding =>
            new CampaignPlanningEvidenceAuthority(
                bundle.Observed.ObservationSet!.Observations.Single(observation =>
                    observation.Subject == binding.Subject),
                binding.Evidence)).ToImmutableArray();
        var snapshot = new CampaignPlanningSnapshot(
            configuration.Planning.CampaignLineage,
            preflight.SnapshotBinding,
            Sha256(bundle.CanonicalAudit),
            Sha256(File.ReadAllBytes(bundle.ResolvedPaths.PhysicalInput)),
            Sha256(preflight.PolicyBytes),
            configuration.Planning.TargetProfile);
        return new CampaignPlanningInput(
            snapshot,
            policy,
            bundle.Classifications,
            bundle.Observed.ObservationSet!,
            evidence,
            bundle.Audit,
            new CampaignPlanningOwnerAuthoritySet(owners));
    }

    private static DocumentationScribeStyleProfile ExpandStyleProfile(
        CampaignConfigurationDocument configuration,
        CampaignPlanningTargetAuthority authority)
    {
        var components = authority.ApplicableComponents.Select(component =>
            new DocumentationPatchApplicableComponent(
                component.Kind switch
                {
                    ComponentKind.TypeParameter => DocumentationPatchComponentKind.TypeParameter,
                    ComponentKind.Parameter => DocumentationPatchComponentKind.Parameter,
                    ComponentKind.Return => DocumentationPatchComponentKind.Return,
                    ComponentKind.Value => DocumentationPatchComponentKind.Value,
                    _ => throw new InvalidOperationException(),
                },
                component.Identity,
                component.Name)).ToImmutableArray();
        return configuration.ScribeRequest.StyleProfileTemplate.Expand(components);
    }

    private static string? ClassifyInitialRead(CampaignOperation operation, CampaignCheckpointReadResult read)
    {
        if (read.Kind == CampaignCheckpointReadKind.Unsafe) return "campaign.state-unsafe";
        if (read.Kind == CampaignCheckpointReadKind.LeaseConflict) return "campaign.lease-conflict";
        if (read.Kind == CampaignCheckpointReadKind.LeaseUnverifiable) return "campaign.lease-unverifiable";
        if (operation == CampaignOperation.Start)
            return read.Kind == CampaignCheckpointReadKind.NotFound ? null : "campaign.state-present";
        return read.Kind switch
        {
            CampaignCheckpointReadKind.NotFound => "campaign.state-missing",
            CampaignCheckpointReadKind.Invalid when read.FailureCode == CampaignStateValidationCode.UnsupportedVersion =>
                "campaign.unsupported-revision",
            CampaignCheckpointReadKind.Invalid or CampaignCheckpointReadKind.Unreadable => "campaign.state-corrupt",
            CampaignCheckpointReadKind.Found => null,
            _ => "campaign.state-corrupt",
        };
    }

    private static string AcceptanceOutcome(CampaignCheckpointAcceptanceKind kind) => kind switch
    {
        CampaignCheckpointAcceptanceKind.Unsafe => "campaign.state-unsafe",
        CampaignCheckpointAcceptanceKind.LeaseConflict => "campaign.lease-conflict",
        CampaignCheckpointAcceptanceKind.LeaseUnverifiable => "campaign.lease-unverifiable",
        CampaignCheckpointAcceptanceKind.UnsupportedRevision => "campaign.unsupported-revision",
        CampaignCheckpointAcceptanceKind.PublicationFailure
            or CampaignCheckpointAcceptanceKind.ReadbackMismatch
            or CampaignCheckpointAcceptanceKind.WriteRejected => "campaign.state-publication-failure",
        CampaignCheckpointAcceptanceKind.Cancelled => "campaign.cancelled",
        CampaignCheckpointAcceptanceKind.Conflict => "campaign.state-conflict",
        _ => "campaign.state-corrupt",
    };

    private static bool MatchesProductRevision(
        CliBuildIdentity identity,
        CampaignPlanningConfiguration configuration) =>
        string.Equals(configuration.ProductContractRevisionId, ProductRevisionId, StringComparison.Ordinal)
        && string.Equals(configuration.ProductContractRevisionSha256,
            ProductRevisionSha256(identity), StringComparison.Ordinal);

    private static bool MatchesProductRevision(
        CampaignPlanningConfiguration configuration,
        CampaignCheckpointState state) =>
        string.Equals(state.ProductRevision.Id, configuration.ProductContractRevisionId, StringComparison.Ordinal)
        && string.Equals(state.ProductRevision.ContentSha256,
            configuration.ProductContractRevisionSha256, StringComparison.Ordinal);

    internal static string ProductRevisionSha256(CliBuildIdentity identity) =>
        Sha256(Encoding.UTF8.GetBytes(
            "contract-scribe/campaign-product-revision/v1\0" + identity.SourceRevision));

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static CliExecutionResult Present(
        CliBuildIdentity identity,
        CampaignOperation operation,
        string layer,
        string outcome,
        long? revision) =>
        CampaignCliPresentation.Present(identity, new CampaignTerminal(layer, operation, outcome, revision));

    private static CampaignTerminal Terminal(
        CampaignOperation operation,
        string layer,
        string outcome,
        CampaignAcceptedCheckpoint? checkpoint) =>
        new(layer, operation, outcome, checkpoint?.Artifact.CheckpointRevision);
}
