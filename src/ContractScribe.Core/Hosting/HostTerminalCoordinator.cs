using System.Collections.Immutable;

namespace ContractScribe.Core.Hosting;

public sealed class HostTerminalCoordinator
{
    private readonly object gate = new();
    private long nextSequence;
    private bool publicationDecisionAcquired;
    private HostTerminalRecord? terminal;
    private CommittedCanonicalResult? committedResult;

    public HostTerminalRecord? Terminal
    {
        get
        {
            lock (gate)
            {
                return terminal;
            }
        }
    }

    public CommittedCanonicalResult? CommittedResult
    {
        get
        {
            lock (gate)
            {
                return committedResult;
            }
        }
    }

    public long NextCauseSequence() => Interlocked.Increment(ref nextSequence);

    public bool TryCommitNonSuccess(
        HostTerminalRecord candidate,
        out HostTerminalRecord accepted)
    {
        if (candidate.ExecutionOutcome == HostExecutionOutcome.Succeeded
            || candidate.TerminalState != HostTerminalState.CommittedNonSuccess
            || candidate.Failure is null)
        {
            throw new ArgumentException("A non-success commit requires one registry failure row.", nameof(candidate));
        }

        lock (gate)
        {
            if (terminal is not null || committedResult is not null || publicationDecisionAcquired)
            {
                accepted = terminal ?? candidate;
                return false;
            }
            terminal = candidate;
            accepted = candidate;
            return true;
        }
    }

    public bool TryAcquirePublicationDecision(out PublicationDecisionLease? lease)
    {
        lock (gate)
        {
            if (terminal is not null || committedResult is not null || publicationDecisionAcquired)
            {
                lease = null;
                return false;
            }
            publicationDecisionAcquired = true;
            lease = new PublicationDecisionLease(this);
            return true;
        }
    }

    public bool TryBeginLatePublishedResultAttempt()
    {
        lock (gate)
        {
            if (terminal is not null || committedResult is not null || publicationDecisionAcquired)
            {
                return false;
            }
            throw new InvalidOperationException(
                "A late published-result attempt cannot precede an authoritative terminal decision.");
        }
    }

    private HostTerminalRecord CommitPublicationFailure(
        HostTerminalRecord candidate)
    {
        if (candidate.ExecutionOutcome != HostExecutionOutcome.PublicationFailure
            || candidate.TerminalState != HostTerminalState.CommittedNonSuccess
            || candidate.Failure is null
            || candidate.Failure.Stage != HostStage.Publication)
        {
            throw new ArgumentException("Publication failure completion requires one publication row.", nameof(candidate));
        }

        lock (gate)
        {
            RequireActivePublicationDecision();
            terminal = candidate;
            publicationDecisionAcquired = false;
            return candidate;
        }
    }

    private CommittedCanonicalResult CommitPublishedResult(
        CommittedCanonicalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (gate)
        {
            RequireActivePublicationDecision();
            committedResult = result;
            publicationDecisionAcquired = false;
            return result;
        }
    }

    public HostTerminalRecord DeriveSuccessRecord(
        AuditOutcome auditOutcome,
        IEnumerable<HostDiagnosticFact> diagnostics,
        IEnumerable<HostMeasuredBound> measuredBounds)
    {
        lock (gate)
        {
            if (committedResult is null || terminal is not null)
            {
                throw new InvalidOperationException(
                    "A success record can derive only from the current committed canonical result.");
            }

            terminal = new HostTerminalRecord(
                HostExecutionOutcome.Succeeded,
                auditOutcome,
                HostTerminalState.CommittedResult,
                null,
                committedResult.Provenance,
                committedResult.Toolchain,
                new HostOutputCommit(
                    HostArtifactState.Published,
                    committedResult.Sha256,
                    committedResult.Bytes.Length),
                diagnostics.ToImmutableArray(),
                measuredBounds.ToImmutableArray(),
                NextCauseSequence());
            return terminal;
        }
    }

    private void RequireActivePublicationDecision()
    {
        if (!publicationDecisionAcquired || terminal is not null || committedResult is not null)
        {
            throw new InvalidOperationException("The publication decision is no longer authoritative.");
        }
    }

    public sealed class PublicationDecisionLease
    {
        private HostTerminalCoordinator? owner;

        internal PublicationDecisionLease(HostTerminalCoordinator owner)
        {
            this.owner = owner;
        }

        public HostTerminalRecord CommitFailureAfterCleanup(HostTerminalRecord finalFailure)
        {
            var current = Interlocked.Exchange(ref owner, null)
                ?? throw new InvalidOperationException("The publication decision was already completed.");
            return current.CommitPublicationFailure(finalFailure);
        }

        public CommittedCanonicalResult CommitRename(CommittedCanonicalResult result)
        {
            var current = Interlocked.Exchange(ref owner, null)
                ?? throw new InvalidOperationException("The publication decision was already completed.");
            return current.CommitPublishedResult(result);
        }
    }
}
