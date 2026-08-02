using System.Collections.Immutable;

namespace ContractScribe.Core.Hosting;

public sealed class HostTerminalCoordinator
{
    private readonly object gate = new();
    private long nextSequence;
    private bool publicationDecisionAcquired;
    private HostTerminalRecord? registeredCause;
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

    public HostTerminalRecord? RegisteredCause
    {
        get
        {
            lock (gate)
            {
                return registeredCause;
            }
        }
    }

    public long NextCauseSequence() => Interlocked.Increment(ref nextSequence);

    public bool TryRegisterCause(
        HostTerminalRecord candidate,
        out HostTerminalRecord accepted)
    {
        ValidateNonSuccess(candidate);
        lock (gate)
        {
            if (terminal is not null || committedResult is not null || publicationDecisionAcquired)
            {
                accepted = terminal ?? candidate;
                return false;
            }
            if (registeredCause is null || CompareCause(candidate, registeredCause) < 0)
            {
                registeredCause = candidate;
            }
            accepted = registeredCause;
            return ReferenceEquals(registeredCause, candidate);
        }
    }

    public bool TryCommitRegisteredCause(
        HostTerminalRecord candidate,
        out HostTerminalRecord accepted)
    {
        return TryCommitRegisteredCause(candidate, candidate, out accepted);
    }

    public bool TryCommitRegisteredCause(
        HostTerminalRecord registered,
        HostTerminalRecord final,
        out HostTerminalRecord accepted)
    {
        ValidateNonSuccess(registered);
        ValidateNonSuccess(final);
        ValidateSameCause(registered, final);
        lock (gate)
        {
            if (terminal is not null || committedResult is not null || publicationDecisionAcquired)
            {
                accepted = terminal ?? registered;
                return false;
            }
            if (!ReferenceEquals(registeredCause, registered))
            {
                accepted = registeredCause ?? registered;
                return false;
            }
            terminal = final;
            registeredCause = null;
            accepted = final;
            return true;
        }
    }

    public bool TryCommitNonSuccess(
        HostTerminalRecord candidate,
        out HostTerminalRecord accepted)
    {
        ValidateNonSuccess(candidate);

        lock (gate)
        {
            if (terminal is not null || committedResult is not null || publicationDecisionAcquired)
            {
                accepted = terminal ?? candidate;
                return false;
            }
            if (registeredCause is not null
                && !ReferenceEquals(registeredCause, candidate)
                && CompareCause(candidate, registeredCause) >= 0)
            {
                accepted = registeredCause;
                return false;
            }
            terminal = candidate;
            registeredCause = null;
            accepted = candidate;
            return true;
        }
    }

    public bool TryAcquirePublicationDecision(out PublicationDecisionLease? lease)
    {
        return TryAcquirePublicationDecision(out lease, out _);
    }

    public bool TryAcquirePublicationDecision(
        out PublicationDecisionLease? lease,
        out HostTerminalRecord? winningCause)
    {
        lock (gate)
        {
            winningCause = terminal ?? registeredCause;
            if (winningCause is not null
                || committedResult is not null
                || publicationDecisionAcquired)
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
            if (terminal is not null
                || registeredCause is not null
                || committedResult is not null
                || publicationDecisionAcquired)
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
            if (committedResult is null || terminal is not null || registeredCause is not null)
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
        if (!publicationDecisionAcquired
            || terminal is not null
            || registeredCause is not null
            || committedResult is not null)
        {
            throw new InvalidOperationException("The publication decision is no longer authoritative.");
        }
    }

    private static void ValidateNonSuccess(HostTerminalRecord candidate)
    {
        if (candidate.ExecutionOutcome == HostExecutionOutcome.Succeeded
            || candidate.TerminalState != HostTerminalState.CommittedNonSuccess
            || candidate.Failure is null)
        {
            throw new ArgumentException(
                "A non-success cause requires one registry failure row.",
                nameof(candidate));
        }
    }

    private static int CompareCause(
        HostTerminalRecord candidate,
        HostTerminalRecord incumbent)
    {
        var sequence = candidate.AcceptedSequence.CompareTo(incumbent.AcceptedSequence);
        if (sequence != 0)
        {
            return sequence;
        }

        var stage = candidate.Failure!.Stage.CompareTo(incumbent.Failure!.Stage);
        if (stage != 0)
        {
            return stage;
        }

        var outcome = CauseTiePriority(candidate.ExecutionOutcome)
            .CompareTo(CauseTiePriority(incumbent.ExecutionOutcome));
        return outcome != 0
            ? outcome
            : StringComparer.Ordinal.Compare(candidate.Failure.Code, incumbent.Failure.Code);
    }

    private static int CauseTiePriority(HostExecutionOutcome outcome) => outcome switch
    {
        HostExecutionOutcome.Cancelled => 0,
        HostExecutionOutcome.Timeout => 1,
        HostExecutionOutcome.InvalidInput => 2,
        HostExecutionOutcome.EnvironmentUnavailable => 3,
        HostExecutionOutcome.LoadFailure => 4,
        HostExecutionOutcome.AuditError => 5,
        HostExecutionOutcome.PublicationFailure => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static void ValidateSameCause(
        HostTerminalRecord registered,
        HostTerminalRecord final)
    {
        if (registered.ExecutionOutcome != final.ExecutionOutcome
            || registered.TerminalState != final.TerminalState
            || !Equals(registered.Failure, final.Failure)
            || !Equals(registered.Provenance, final.Provenance)
            || !Equals(registered.Toolchain, final.Toolchain)
            || registered.AcceptedSequence != final.AcceptedSequence)
        {
            throw new ArgumentException(
                "A registered cause may gain supporting facts but its classification and sequence are immutable.",
                nameof(final));
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
