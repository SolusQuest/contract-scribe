using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;

namespace ContractScribe.Tests;

public sealed class GitHubProposalConfigurationTests
{
    [Theory]
    [InlineData("\"\\uD800\"")]
    [InlineData("\"\\uDC00\"")]
    [InlineData("\"\\uD800x\"")]
    [InlineData("\"\\uD800\\u0041\"")]
    [InlineData("\"\\uD800\\uD800\"")]
    [InlineData("property")]
    public void Malformed_escaped_scalars_are_configuration_failures(string value)
    {
        var json = value == "property" ? Configuration()[..^1] + ",\"\\uD800\":1}"
            : Configuration().Replace("\"operation.initial\"", value, StringComparison.Ordinal);
        var directory = Path.Join(Path.GetTempPath(), "github-unicode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Join(directory, "github.json");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
            Assert.ThrowsAny<JsonException>(() => GitHubProposalConfigurationReader.Read(path, directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Valid_escaped_ASCII_configuration_values_keep_their_decoded_identity()
    {
        var directory = Path.Join(Path.GetTempPath(), "github-unicode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Join(directory, "github.json");
            File.WriteAllText(path, Configuration().Replace("operation.initial", "operation.\\u0061", StringComparison.Ordinal));
            Assert.Equal("operation.a", GitHubProposalConfigurationReader.Read(path, directory).Configuration.OperationId);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string Configuration() => JsonSerializer.Serialize(new
    {
        repositoryOwner = "Owner",
        repositoryName = "repo",
        targetRef = "refs/heads/main",
        expectedBaseCommitOid = new string('a', 40),
        operationId = "operation.initial",
        generationId = "generation.initial",
        policy = new { maximumDocumentationBlocks = 12, maximumDistinctChangedFiles = 8, maximumCumulativePatchBytes = 4096 },
        transition = "initial",
    });

    [Theory]
    [InlineData("null")]
    [InlineData("array")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("token")]
    [InlineData("endpoint")]
    [InlineData("missing")]
    [InlineData("nested-missing")]
    [InlineData("nested-unknown")]
    [InlineData("null-required")]
    [InlineData("transition-case")]
    [InlineData("transition-number")]
    [InlineData("predecessor-type")]
    [InlineData("authorization-boolean")]
    [InlineData("authorization-partial")]
    [InlineData("predecessor-empty")]
    [InlineData("bom")]
    [InlineData("trailing")]
    [InlineData("over-bound")]
    public void Configuration_rejects_missing_duplicate_unknown_wrong_type_and_noncanonical_enum_inputs(string mutation)
    {
        var json = JsonNode.Parse(Configuration())!.AsObject();
        string? replacement = null;
        switch (mutation)
        {
            case "null": replacement = "null"; break;
            case "array": replacement = "[]"; break;
            case "duplicate": replacement = Configuration()[..^1] + ",\"operationId\":\"other\"}"; break;
            case "unknown": json["extension"] = true; break;
            case "token": json["token"] = "private-token"; break;
            case "endpoint": json["endpoint"] = "http://127.0.0.1:1234/"; break;
            case "missing": json.Remove("generationId"); break;
            case "nested-missing": json["policy"]!.AsObject().Remove("maximumDocumentationBlocks"); break;
            case "nested-unknown": json["policy"]!["extension"] = true; break;
            case "null-required": json["operationId"] = null; break;
            case "transition-case": json["transition"] = "Initial"; break;
            case "transition-number": json["transition"] = 0; break;
            case "predecessor-type": json["terminalPredecessor"] = true; break;
            case "authorization-boolean": json["closedUnmergedSuccessorAuthorization"] = true; break;
            case "authorization-partial": json["closedUnmergedSuccessorAuthorization"] = new JsonObject { ["authorizationId"] = "auth" }; break;
            case "predecessor-empty": json["appendPredecessor"] = new JsonObject(); break;
            case "bom": replacement = "\uFEFF" + Configuration(); break;
            case "trailing": replacement = Configuration() + "{}"; break;
            case "over-bound": replacement = new string(' ', GitHubProposalConfigurationReader.MaximumBytes + 1); break;
        }
        var directory = Path.Join(Path.GetTempPath(), "github-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Join(directory, "github.json");
            File.WriteAllText(path, replacement ?? json.ToJsonString(), new UTF8Encoding(false));
            Assert.ThrowsAny<JsonException>(() => GitHubProposalConfigurationReader.Read(path, directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Accepted_configuration_snapshot_detects_later_changes()
    {
        var directory = Path.Join(Path.GetTempPath(), "github-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Join(directory, "github.json");
            File.WriteAllText(path, Configuration());
            var snapshot = GitHubProposalConfigurationReader.Read(path, directory);
            Assert.True(snapshot.Revalidate());
            File.AppendAllText(path, " ");
            Assert.False(snapshot.Revalidate());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
