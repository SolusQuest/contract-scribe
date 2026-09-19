using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class GitHubProposalRequestTests
{
    [Theory]
    [InlineData("\"\\uD800\"")]
    [InlineData("\"\\uDC00\"")]
    [InlineData("\"\\uD800x\"")]
    [InlineData("\"\\uD800\\u0041\"")]
    [InlineData("\"\\uD800\\uD800\"")]
    [InlineData("property")]
    public void Malformed_escaped_scalars_are_request_failures(string value)
    {
        var json = value == "property" ? Request()[..^1] + ",\"\\uD800\":1}"
            : Request().Replace("\"operation.initial\"", value, StringComparison.Ordinal);
        var directory = Path.Join(Path.GetTempPath(), "github-unicode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Join(directory, "request.json");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
            Assert.ThrowsAny<JsonException>(() => GitHubProposalRequestReader.Read(path, directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Valid_escaped_ASCII_request_values_keep_their_decoded_identity()
    {
        var directory = Path.Join(Path.GetTempPath(), "github-unicode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Join(directory, "request.json");
            File.WriteAllText(path, Request().Replace("operation.initial", "operation.\\u0061", StringComparison.Ordinal));
            Assert.Equal("operation.a", GitHubProposalRequestReader.Read(path, directory).Request.GitHub.OperationId);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Accepted_request_exposes_lexical_invocation_authority()
    {
        var directory = Path.Join(Path.GetTempPath(), "github-request-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Join(directory, "request.json");
            File.WriteAllText(path, Request());
            var request = GitHubProposalRequestReader.Read(path, directory).Request;
            Assert.Equal(1, request.Version);
            Assert.Equal("campaign.github.request", request.CampaignLineage);
            Assert.Equal("snapshot.github.request", request.Snapshot);
            Assert.Equal("checkpoint/checkpoint.json", request.State);
            Assert.Equal(GitHubPublicationTransitionKind.Initial, request.GitHub.Transition);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string Request() => JsonSerializer.Serialize(new
    {
        githubProposalRequestVersion = 1,
        campaignLineage = "campaign.github.request",
        snapshot = "snapshot.github.request",
        state = "checkpoint/checkpoint.json",
        github = new
        {
            repositoryOwner = "Owner",
            repositoryName = "repo",
            targetRef = "refs/heads/main",
            expectedBaseCommitOid = new string('a', 40),
            operationId = "operation.initial",
            generationId = "generation.initial",
            policy = new { maximumDocumentationBlocks = 12, maximumDistinctChangedFiles = 8, maximumCumulativePatchBytes = 4096 },
            transition = "initial",
        },
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
    public void Request_rejects_missing_duplicate_unknown_wrong_type_and_noncanonical_enum_inputs(string mutation)
    {
        var json = JsonNode.Parse(Request())!.AsObject();
        var github = json["github"]!.AsObject();
        string? replacement = null;
        switch (mutation)
        {
            case "null": replacement = "null"; break;
            case "array": replacement = "[]"; break;
            case "duplicate": replacement = Request()[..^1] + ",\"snapshot\":\"other\"}"; break;
            case "unknown": json["extension"] = true; break;
            case "token": json["token"] = "private-token"; break;
            case "endpoint": github["endpoint"] = "http://127.0.0.1:1234/"; break;
            case "missing": github.Remove("generationId"); break;
            case "nested-missing": github["policy"]!.AsObject().Remove("maximumDocumentationBlocks"); break;
            case "nested-unknown": github["policy"]!["extension"] = true; break;
            case "null-required": github["operationId"] = null; break;
            case "transition-case": github["transition"] = "Initial"; break;
            case "transition-number": github["transition"] = 0; break;
            case "predecessor-type": github["terminalPredecessor"] = true; break;
            case "authorization-boolean": github["closedUnmergedSuccessorAuthorization"] = true; break;
            case "authorization-partial": github["closedUnmergedSuccessorAuthorization"] = new JsonObject { ["authorizationId"] = "auth" }; break;
            case "predecessor-empty": github["appendPredecessor"] = new JsonObject(); break;
            case "bom": replacement = "﻿" + Request(); break;
            case "trailing": replacement = Request() + "{}"; break;
            case "over-bound": replacement = new string(' ', GitHubProposalRequestReader.MaximumBytes + 1); break;
        }
        var directory = Path.Join(Path.GetTempPath(), "github-request-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Join(directory, "request.json");
            File.WriteAllText(path, replacement ?? json.ToJsonString(), new UTF8Encoding(false));
            Assert.ThrowsAny<JsonException>(() => GitHubProposalRequestReader.Read(path, directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("version-missing")]
    [InlineData("version-zero")]
    [InlineData("version-string")]
    [InlineData("lineage-missing")]
    [InlineData("lineage-null")]
    [InlineData("lineage-empty")]
    [InlineData("lineage-punctuation")]
    [InlineData("lineage-slash")]
    [InlineData("lineage-long")]
    [InlineData("snapshot-missing")]
    [InlineData("snapshot-null")]
    [InlineData("snapshot-punctuation")]
    [InlineData("snapshot-space")]
    [InlineData("snapshot-long")]
    [InlineData("state-missing")]
    [InlineData("state-null")]
    [InlineData("state-empty")]
    [InlineData("state-control")]
    [InlineData("state-number")]
    [InlineData("github-missing")]
    [InlineData("github-null")]
    [InlineData("github-string")]
    [InlineData("github-token")]
    [InlineData("github-snapshot")]
    public void Invocation_authority_rejects_missing_wrong_type_and_noncanonical_claims(string mutation)
    {
        var json = JsonNode.Parse(Request())!.AsObject();
        switch (mutation)
        {
            case "version-missing": json.Remove("githubProposalRequestVersion"); break;
            case "version-zero": json["githubProposalRequestVersion"] = 0; break;
            case "version-string": json["githubProposalRequestVersion"] = "1"; break;
            case "lineage-missing": json.Remove("campaignLineage"); break;
            case "lineage-null": json["campaignLineage"] = null; break;
            case "lineage-empty": json["campaignLineage"] = ""; break;
            case "lineage-punctuation": json["campaignLineage"] = ".leading"; break;
            case "lineage-slash": json["campaignLineage"] = "has/slash"; break;
            case "lineage-long": json["campaignLineage"] = new string('a', 513); break;
            case "snapshot-missing": json.Remove("snapshot"); break;
            case "snapshot-null": json["snapshot"] = null; break;
            case "snapshot-punctuation": json["snapshot"] = "-leading"; break;
            case "snapshot-space": json["snapshot"] = "has space"; break;
            case "snapshot-long": json["snapshot"] = new string('a', 129); break;
            case "state-missing": json.Remove("state"); break;
            case "state-null": json["state"] = null; break;
            case "state-empty": json["state"] = ""; break;
            case "state-control": json["state"] = "checkpoint\n.json"; break;
            case "state-number": json["state"] = 7; break;
            case "github-missing": json.Remove("github"); break;
            case "github-null": json["github"] = null; break;
            case "github-string": json["github"] = "github"; break;
            case "github-token": json["github"]!["token"] = "private-token"; break;
            case "github-snapshot": json["github"]!["snapshot"] = "snapshot.other"; break;
        }
        var directory = Path.Join(Path.GetTempPath(), "github-request-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Join(directory, "request.json");
            File.WriteAllText(path, json.ToJsonString(), new UTF8Encoding(false));
            Assert.ThrowsAny<JsonException>(() => GitHubProposalRequestReader.Read(path, directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Accepted_request_snapshot_detects_later_changes()
    {
        var directory = Path.Join(Path.GetTempPath(), "github-request-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Join(directory, "request.json");
            File.WriteAllText(path, Request());
            var snapshot = GitHubProposalRequestReader.Read(path, directory);
            Assert.True(snapshot.Revalidate());
            File.AppendAllText(path, " ");
            Assert.False(snapshot.Revalidate());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
