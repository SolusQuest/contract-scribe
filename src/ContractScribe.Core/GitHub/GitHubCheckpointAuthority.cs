namespace ContractScribe.Core;

/// <summary>Candidate-free authority for one campaign's coordination ref and bound target.</summary>
public sealed class ValidatedGitHubCheckpointAuthority
{
    private ValidatedGitHubCheckpointAuthority(ValidatedGitHubCampaignReadAuthority read, string expectedBaseOid)
    {
        ReadAuthority = read;
        ExpectedBaseOid = expectedBaseOid;
    }

    public ValidatedGitHubCampaignReadAuthority ReadAuthority { get; }
    public string ExpectedBaseOid { get; }

    public static ValidatedGitHubCheckpointAuthority Create(string owner, string name, string targetRef,
        string lineage, string expectedBaseOid)
    {
        var read = GitHubPublicationFactory.CreateCampaignReadAuthority(owner, name, targetRef, lineage);
        if (!GitHubPublicationFactory.IsGitOid(expectedBaseOid, allowMissing: false))
            throw new ArgumentException("The checkpoint base must be a complete Git object identity.", nameof(expectedBaseOid));
        return new(read, expectedBaseOid);
    }

    public override string ToString() => nameof(ValidatedGitHubCheckpointAuthority);
}
