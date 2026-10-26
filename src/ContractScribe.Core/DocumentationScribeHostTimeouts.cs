namespace ContractScribe.Core;

/// <summary>Finite semantic subwindows; their maxima remain protocol safety bounds.</summary>
public sealed record CampaignSemanticDurations
{
    private CampaignSemanticDurations(int authorization, int query, int closure)
    { AuthorizationMilliseconds = authorization; QueryMilliseconds = query; ClosureMilliseconds = closure; }

    public int AuthorizationMilliseconds { get; }
    public int QueryMilliseconds { get; }
    public int ClosureMilliseconds { get; }
    public int EnvelopeMilliseconds => checked(AuthorizationMilliseconds + QueryMilliseconds + ClosureMilliseconds);
    public static CampaignSemanticDurations Default { get; } = Create();

    public static CampaignSemanticDurations Create(int authorizationMilliseconds = 30_000,
        int queryMilliseconds = 10_000, int closureMilliseconds = 30_000)
    {
        if (authorizationMilliseconds is < 1 or > CampaignSemanticCheckpointWindow.MaximumAuthorizationMilliseconds
            || queryMilliseconds is < 1 or > CampaignSemanticCheckpointWindow.MaximumQueryMilliseconds
            || closureMilliseconds is < 1 or > CampaignSemanticCheckpointWindow.MaximumClosureMilliseconds
            || checked(authorizationMilliseconds + queryMilliseconds + closureMilliseconds)
                > CampaignSemanticCheckpointWindow.MaximumEnvelopeMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(authorizationMilliseconds));
        return new(authorizationMilliseconds, queryMilliseconds, closureMilliseconds);
    }
}

/// <summary>Immutable execution policy, separate from provider-request and correctness authority.</summary>
public sealed record DocumentationScribeHostTimeouts
{
    private DocumentationScribeHostTimeouts(int terminal, int repository, CampaignSemanticDurations semantic)
    { TerminalSubmissionMilliseconds = terminal; RepositoryToolMilliseconds = repository; Semantic = semantic; }

    public int TerminalSubmissionMilliseconds { get; }
    public int RepositoryToolMilliseconds { get; }
    public CampaignSemanticDurations Semantic { get; }
    public static DocumentationScribeHostTimeouts Default { get; } = Create();

    public static DocumentationScribeHostTimeouts Create(int terminalSubmissionMilliseconds = 300_000,
        int repositoryToolMilliseconds = 30_000, CampaignSemanticDurations? semantic = null)
    {
        if (terminalSubmissionMilliseconds is < 1 or > DocumentationScribeContract.MaximumConfiguredElapsedMilliseconds
            || repositoryToolMilliseconds is < 1 or > DocumentationScribeContract.MaximumConfiguredElapsedMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(terminalSubmissionMilliseconds));
        return new(terminalSubmissionMilliseconds, repositoryToolMilliseconds, semantic ?? CampaignSemanticDurations.Default);
    }
}
