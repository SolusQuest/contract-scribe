namespace ContractScribe.Core;

/// <summary>Stable caller identity for authenticated observations before a candidate exists.</summary>
public sealed class ValidatedGitHubCampaignReadAuthority
{
    internal ValidatedGitHubCampaignReadAuthority(string owner, string name, string targetRef, string lineage)
    { RepositoryOwner = owner; RepositoryName = name; TargetRef = targetRef; CampaignLineage = lineage; }

    public string RepositoryOwner { get; }
    public string RepositoryName { get; }
    public string TargetRef { get; }
    public string CampaignLineage { get; }
    public override string ToString() => nameof(ValidatedGitHubCampaignReadAuthority);
}
