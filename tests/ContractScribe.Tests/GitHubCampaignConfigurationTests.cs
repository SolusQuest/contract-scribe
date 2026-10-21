using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;

namespace ContractScribe.Tests;

public sealed class GitHubCampaignConfigurationTests
{
    private static string Configuration() => JsonSerializer.Serialize(new
    {
        githubCampaignVersion = 1,
        campaignLineage = "campaign.github.configuration",
        github = new { repositoryOwner = "Owner", repositoryName = "repo", targetRef = "refs/heads/main" },
    });

    private static void WithFile(string json, Action<string, string> check)
    {
        var directory = Path.Join(Path.GetTempPath(), "github-campaign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Join(directory, "campaign.json");
            File.WriteAllText(path, json, new UTF8Encoding(false));
            check(path, directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Accepted_configuration_contains_only_stable_campaign_identity() => WithFile(Configuration(), (path, root) =>
    {
        var configuration = GitHubCampaignConfigurationReader.Read(path, root).Configuration;
        Assert.Equal(1, configuration.Version);
        Assert.Equal("campaign.github.configuration", configuration.CampaignLineage);
        Assert.Equal(new GitHubCampaignDestination("Owner", "repo", "refs/heads/main"), configuration.GitHub);
    });

    [Theory]
    [InlineData("\"\\uD800\"")]
    [InlineData("\"\\uDC00\"")]
    [InlineData("\"\\uD800x\"")]
    [InlineData("\"\\uD800\\u0041\"")]
    [InlineData("\"\\uD800\\uD800\"")]
    [InlineData("property")]
    public void Malformed_escaped_scalars_fail_closed(string value)
    {
        var json = value == "property" ? Configuration()[..^1] + ",\"\\uD800\":1}"
            : Configuration().Replace("\"campaign.github.configuration\"", value, StringComparison.Ordinal);
        WithFile(json, (path, root) => Assert.ThrowsAny<JsonException>(() => GitHubCampaignConfigurationReader.Read(path, root)));
    }

    [Fact]
    public void Valid_escaped_ASCII_keeps_its_decoded_identity() =>
        WithFile(Configuration().Replace("campaign.github.configuration", "campaign.\\u0061", StringComparison.Ordinal),
            (path, root) => Assert.Equal("campaign.a", GitHubCampaignConfigurationReader.Read(path, root).Configuration.CampaignLineage));

    [Theory]
    [InlineData("null")]
    [InlineData("array")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("token")]
    [InlineData("endpoint")]
    [InlineData("owner-missing")]
    [InlineData("owner-null")]
    [InlineData("owner-path")]
    [InlineData("ref-missing")]
    [InlineData("ref-null")]
    [InlineData("ref-tag")]
    [InlineData("ref-malformed")]
    [InlineData("version-missing")]
    [InlineData("version-zero")]
    [InlineData("version-string")]
    [InlineData("lineage-missing")]
    [InlineData("lineage-null")]
    [InlineData("lineage-empty")]
    [InlineData("lineage-punctuation")]
    [InlineData("lineage-slash")]
    [InlineData("lineage-long")]
    [InlineData("github-missing")]
    [InlineData("github-null")]
    [InlineData("github-string")]
    [InlineData("github-token")]
    [InlineData("snapshot")]
    [InlineData("checkpointStore")]
    [InlineData("operationId")]
    [InlineData("generationId")]
    [InlineData("expectedBaseCommitOid")]
    [InlineData("transition")]
    [InlineData("terminalPredecessor")]
    [InlineData("appendPredecessor")]
    [InlineData("closedUnmergedSuccessorAuthorization")]
    [InlineData("bom")]
    [InlineData("trailing")]
    [InlineData("over-bound")]
    public void Missing_malformed_secret_and_retired_authority_fields_are_rejected(string mutation)
    {
        var json = JsonNode.Parse(Configuration())!.AsObject();
        var github = json["github"]!.AsObject();
        string? replacement = null;
        switch (mutation)
        {
            case "null": replacement = "null"; break;
            case "array": replacement = "[]"; break;
            case "duplicate": replacement = Configuration()[..^1] + ",\"campaignLineage\":\"other\"}"; break;
            case "unknown": json["extension"] = true; break;
            case "token": json["token"] = "private-token"; break;
            case "endpoint": github["endpoint"] = "http://127.0.0.1:1234/"; break;
            case "owner-missing": github.Remove("repositoryOwner"); break;
            case "owner-null": github["repositoryOwner"] = null; break;
            case "owner-path": github["repositoryOwner"] = "../Owner"; break;
            case "ref-missing": github.Remove("targetRef"); break;
            case "ref-null": github["targetRef"] = null; break;
            case "ref-tag": github["targetRef"] = "refs/tags/main"; break;
            case "ref-malformed": github["targetRef"] = "refs/heads/../main"; break;
            case "version-missing": json.Remove("githubCampaignVersion"); break;
            case "version-zero": json["githubCampaignVersion"] = 0; break;
            case "version-string": json["githubCampaignVersion"] = "1"; break;
            case "lineage-missing": json.Remove("campaignLineage"); break;
            case "lineage-null": json["campaignLineage"] = null; break;
            case "lineage-empty": json["campaignLineage"] = ""; break;
            case "lineage-punctuation": json["campaignLineage"] = ".leading"; break;
            case "lineage-slash": json["campaignLineage"] = "has/slash"; break;
            case "lineage-long": json["campaignLineage"] = new string('a', 513); break;
            case "github-missing": json.Remove("github"); break;
            case "github-null": json["github"] = null; break;
            case "github-string": json["github"] = "github"; break;
            case "github-token": github["token"] = "private-token"; break;
            case "snapshot": json[mutation] = "retired"; break;
            case "checkpointStore": json[mutation] = "coordination-ref"; break;
            case "bom": replacement = "\uFEFF" + Configuration(); break;
            case "trailing": replacement = Configuration() + "{}"; break;
            case "over-bound": replacement = new string(' ', GitHubCampaignConfigurationReader.MaximumBytes + 1); break;
            default: github[mutation] = "retired"; break;
        }
        WithFile(replacement ?? json.ToJsonString(),
            (path, root) => Assert.ThrowsAny<JsonException>(() => GitHubCampaignConfigurationReader.Read(path, root)));
    }

    [Fact]
    public void A_complete_old_manual_request_is_not_a_current_campaign_configuration() => WithFile(
        "{\"githubProposalRequestVersion\":1,\"campaignLineage\":\"campaign.old\",\"snapshot\":\"snapshot.old\","
        + "\"checkpointStore\":\"coordination-ref\",\"github\":{\"repositoryOwner\":\"Owner\",\"repositoryName\":\"repo\","
        + "\"targetRef\":\"refs/heads/main\",\"expectedBaseCommitOid\":\"" + new string('a', 40)
        + "\",\"operationId\":\"operation.old\",\"generationId\":\"generation.old\",\"transition\":\"initial\"}}",
        (path, root) => Assert.ThrowsAny<JsonException>(() => GitHubCampaignConfigurationReader.Read(path, root)));

    [Fact]
    public void Accepted_configuration_detects_later_byte_changes() => WithFile(Configuration(), (path, root) =>
    {
        var snapshot = GitHubCampaignConfigurationReader.Read(path, root);
        Assert.True(snapshot.Revalidate());
        File.AppendAllText(path, " ");
        Assert.False(snapshot.Revalidate());
    });
}
