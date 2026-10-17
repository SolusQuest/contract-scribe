using System.Collections.Immutable;
using System.Text;
using ContractScribe.Agent.Prompting;
using ContractScribe.Core;

namespace ContractScribe.Agent.Runtime;

internal sealed class DocumentationScribeTerminalReducer
{
    private readonly object gate = new();
    private DocumentationScribeRunResult? committed;

    internal DocumentationScribeRunResult? TryCommitPriority(
        RunState state,
        CancellationToken cancellationToken, bool allowReturnedTerminal = false)
    {
        lock (gate)
        {
            if (committed is not null)
            {
                return committed;
            }

            var elapsed = state.ElapsedMilliseconds;
            committed = CreatePriorityResult(state, cancellationToken, elapsed, allowReturnedTerminal);
            return committed;
        }
    }

    internal DocumentationScribeRunResult CommitCancelled(
        RunState state,
        CancellationToken cancellationToken) =>
        CommitFailureCore(state, cancellationToken, DocumentationScribeFailureCode.Internal, cancelled: true);

    internal DocumentationScribeRunResult CommitFailure(
        RunState state,
        CancellationToken cancellationToken,
        DocumentationScribeFailureCode code) =>
        CommitFailureCore(state, cancellationToken, code);

    internal DocumentationScribeRunResult CommitProvider(
        RunState state,
        CancellationToken cancellationToken,
        DocumentationScribeProviderFinalDisposition providerFinalDisposition) =>
        CommitFailureCore(
            state,
            cancellationToken,
            DocumentationScribeFailureCode.Provider,
            providerFinalDisposition: providerFinalDisposition);

    internal DocumentationScribeRunResult CommitToolProtocol(
        RunState state,
        CancellationToken cancellationToken,
        string referenceId) =>
        CommitFailureCore(
            state,
            cancellationToken,
            DocumentationScribeFailureCode.ToolProtocol,
            detail: referenceId);

    internal DocumentationScribeRunResult CommitValidation(
        RunState state,
        CancellationToken cancellationToken,
        string validationCode) =>
        CommitFailureCore(
            state,
            cancellationToken,
            DocumentationScribeFailureCode.Validation,
            detail: validationCode);

    internal DocumentationScribeRunResult CommitInternal(
        RunState state,
        CancellationToken cancellationToken) =>
        CommitFailureCore(state, cancellationToken, DocumentationScribeFailureCode.Internal);

    internal DocumentationScribeRunResult CommitTerminal(
        RunState state,
        CancellationToken cancellationToken,
        ReadOnlyMemory<byte> terminalUtf8Json)
    {
        DocumentationScribeResultParseResult? candidate = null;
        string? validationCode = null;
        try
        {
            var validationElapsed = state.ElapsedMilliseconds;
            var validationEnvelope = state.CreateEnvelope(
                ImmutableArray<DocumentationScribeDiagnosticInput>.Empty,
                validationElapsed);
            var validationBytes = DocumentationScribeRunResultWriter.Write(
                state.Request,
                state.AttemptId,
                state.ActiveDynamicEvidenceReferences,
                terminalUtf8Json,
                validationEnvelope);
            candidate = DocumentationScribeValidation.ParseRunResult(
                state.Request,
                state.AttemptId,
                state.ActiveDynamicEvidenceReferences,
                validationBytes.AsMemory());
            if (candidate.Result is null
                || candidate.Result.Terminal.Kind is not (DocumentationScribeTerminalKind.Proposal
                    or DocumentationScribeTerminalKind.Skip))
            {
                validationCode = candidate.Failure?.Code ?? "scribe.result.invalid-terminal";
            }
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            validationCode = "scribe.result.invalid-terminal";
        }

        lock (gate)
        {
            if (committed is not null)
            {
                return committed;
            }

            var elapsed = state.ElapsedMilliseconds;
            committed = CreatePriorityResult(state, cancellationToken, elapsed,
                allowReturnedTerminal: validationCode is null && candidate?.Result?.Terminal is DocumentationScribeProposalTerminal);
            if (committed is not null)
            {
                return committed;
            }

            committed = validationCode is null && candidate?.Result is { } validated
                ? DocumentationScribeValidation.CreateResultFromValidatedTerminal(
                    state.Request,
                    state.AttemptId,
                    validated,
                    state.CreateEnvelope(
                        ImmutableArray<DocumentationScribeDiagnosticInput>.Empty,
                        elapsed))
                : state.CreateValidationFailure(
                    validationCode ?? "scribe.result.invalid-terminal",
                    elapsed);

            return committed;
        }
    }

    private DocumentationScribeRunResult CommitFailureCore(
        RunState state,
        CancellationToken cancellationToken,
        DocumentationScribeFailureCode code,
        string? detail = null,
        bool cancelled = false,
        DocumentationScribeProviderFinalDisposition? providerFinalDisposition = null)
    {
        lock (gate)
        {
            if (committed is not null)
            {
                return committed;
            }

            var elapsed = state.ElapsedMilliseconds;
            committed = CreatePriorityResult(state, cancellationToken, elapsed);
            if (committed is not null)
            {
                return committed;
            }

            committed = cancelled
                ? state.CreateCancelled(elapsed)
                : code switch
                {
                    DocumentationScribeFailureCode.Provider =>
                        state.CreateProviderFailure(
                            providerFinalDisposition
                            ?? DocumentationScribeProviderFinalDisposition.Terminal,
                            elapsed),
                    DocumentationScribeFailureCode.ToolProtocol =>
                        state.CreateToolProtocolFailure(detail ?? "tool", elapsed),
                    DocumentationScribeFailureCode.Validation =>
                        state.CreateValidationFailure(detail ?? "scribe.result.invalid-terminal", elapsed),
                    DocumentationScribeFailureCode.Internal => state.CreateInternalFailure(elapsed),
                    _ => state.CreateFailure(code, elapsed),
                };
            return committed;
        }
    }

    private static DocumentationScribeRunResult? CreatePriorityResult(
        RunState state,
        CancellationToken cancellationToken,
        int elapsedMilliseconds, bool allowReturnedTerminal = false)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return state.CreateCancelled(elapsedMilliseconds);
        }

        if (state.Scope.ResourceBudgetReached
            && !(allowReturnedTerminal && state.Scope.SettledLifetimeBudgetExceeded
                && !state.Scope.LifetimeDeadlineReached && !state.Scope.LifetimeAdmissionDenied && !state.Scope.Allowance.HasCheckedStop))
        {
            return state.CreateFailure(DocumentationScribeFailureCode.Budget, elapsedMilliseconds);
        }

        if (elapsedMilliseconds >= state.Request.Limits.MaximumElapsedMilliseconds || state.Scope.StandaloneDeadlineReached)
        {
            return state.CreateFailure(DocumentationScribeFailureCode.Timeout, elapsedMilliseconds);
        }

        return state.IsObservedBudgetExceeded
            ? state.CreateFailure(DocumentationScribeFailureCode.Budget, elapsedMilliseconds)
            : null;
    }
}
