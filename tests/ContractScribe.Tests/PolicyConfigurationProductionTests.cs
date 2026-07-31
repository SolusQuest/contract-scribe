using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class PolicyConfigurationProductionTests
{
    [Fact]
    public void ProductionEvaluator_MatchesTheEntireFrozenPolicyCorpus()
    {
        var root = FindRepositoryRoot();
        var fixtureRoot = Path.Combine(
            root,
            "tests",
            "fixtures",
            "policy-configuration",
            "v1");
        var manifest = JsonSerializer.Deserialize<ConformanceManifest>(
            File.ReadAllText(Path.Combine(fixtureRoot, "cases.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Policy manifest did not deserialize.");
        var schema = LoadPolicySchema(root);

        foreach (var conformanceCase in manifest.Cases)
        {
            var expected = PolicyConfigurationV1Conformance.Evaluate(
                fixtureRoot,
                schema,
                conformanceCase);
            var actual = EvaluateProduction(fixtureRoot, conformanceCase);
            Assert.Equal(
                expected,
                actual);
        }
    }

    [Fact]
    public void ProductionSchemaFailureSelection_MatchesTheIndependentOracle()
    {
        var schema = LoadPolicySchema(FindRepositoryRoot());
        var input = new EvaluationInput(
            "projects/App/App.csproj",
            "src/App/File.cs");
        var documents = new[]
        {
            "[]",
            """{"schemaVersion":1,"targetProfile":"profile.external-api"}""",
            """{"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":0}""",
            """{"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"optional","rules":{}}""",
            """{"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"optional","rules":[0]}""",
            """{"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"optional","rules":[{"priority":0,"decision":"required"}]}""",
            """{"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"optional","rules":[{"id":"-bad","priority":0,"decision":"required"}]}""",
            """{"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"optional","rules":[{"id":"rule","priority":-1,"decision":"required"}]}""",
            """{"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"optional","rules":[{"id":"rule","priority":2147483648,"decision":"required"}]}""",
            """{"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"optional","rules":[{"id":"rule","priority":1.5,"decision":"required"}]}""",
            """{"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"optional","rules":[{"id":"rule","priority":0,"decision":0}]}""",
            """{"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"optional","rules":[{"id":"rule","priority":0,"decision":"required","sourcePaths":{"unknown":[]}}]}""",
            """{"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"optional","rules":[{"id":"rule","priority":0,"decision":"required","sourcePaths":{"include":{}}}]}""",
            """{"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"optional","rules":[{"id":"rule","priority":0,"decision":"required","sourcePaths":{"include":[""]}}]}""",
            """{"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"optional","a":0,"rules":[{"id":"rule","priority":0,"decision":"required","sourcePaths":{}}]}""",
        };

        foreach (var document in documents)
        {
            var payload = System.Text.Encoding.UTF8.GetBytes(document);
            var expected = PolicyConfigurationV1Conformance.EvaluateBytes(
                payload,
                schema,
                input).Error;
            var actual = PolicyConfigurationEvaluator.Parse(payload);

            Assert.Equal(PolicyRunStatus.Failure, actual.Status);
            Assert.Equal(
                expected,
                new ConformanceError(
                    actual.PrimaryFailure!.Code,
                    actual.PrimaryFailure.Pointer,
                    actual.PrimaryFailure.SchemaKeyword));
        }
    }

    [Fact]
    public void Contributions_AreCanonicalAndRetainIndependentProvenance()
    {
        var policy = ParsePolicy(
            """
            {
              "schemaVersion": 1,
              "targetProfile": "profile.external-api",
              "defaultDecision": "optional",
              "rules": [
                {
                  "id": "repository-required",
                  "priority": 2,
                  "decision": "required",
                  "sourcePaths": { "include": ["src/**"] }
                },
                {
                  "id": "generated-forbidden",
                  "priority": 1,
                  "decision": "forbidden",
                  "projectPaths": { "include": ["projects/App/App.csproj"] }
                }
              ]
            }
            """);
        var producerId = "sgp." + new string('1', 64);
        var outputId = "sgo." + new string('2', 64);
        var inputs = new PolicyContributionInput[]
        {
            PolicyConfigurationInput.Generated(
                "projects/App/App.csproj",
                "source-generator",
                producerId,
                outputId),
            PolicyConfigurationInput.Repository(
                "projects/App/App.csproj",
                "src/App.cs"),
            PolicyConfigurationInput.Repository(
                "projects/App/App.csproj",
                "src/Other.cs"),
            PolicyConfigurationInput.Repository(
                ".\\projects\\App\\App.csproj",
                ".\\src\\App.cs"),
            PolicyConfigurationInput.Repository(
                "projects/Other/Other.csproj",
                "other/File.cs"),
        };

        var outcome = PolicyConfigurationEvaluator.Evaluate(policy, inputs.Reverse());

        Assert.Equal(PolicyRunStatus.Success, outcome.Status);
        var contributions = outcome.ContributionSet!.Contributions;
        Assert.Collection(
            contributions,
            first =>
            {
                var repository = Assert.IsType<RepositoryPolicyContribution>(first);
                Assert.Equal("projects/App/App.csproj", repository.ProjectPath);
                Assert.Equal("src/App.cs", repository.SourcePath);
                Assert.Equal(PolicyExpectation.Required, repository.Expectation);
                Assert.Equal("repository-required", repository.MatchedRuleId);
            },
            second =>
            {
                var repository = Assert.IsType<RepositoryPolicyContribution>(second);
                Assert.Equal("projects/App/App.csproj", repository.ProjectPath);
                Assert.Equal("src/Other.cs", repository.SourcePath);
                Assert.Equal(PolicyExpectation.Required, repository.Expectation);
                Assert.Equal("repository-required", repository.MatchedRuleId);
            },
            third =>
            {
                var repository = Assert.IsType<RepositoryPolicyContribution>(third);
                Assert.Equal("projects/Other/Other.csproj", repository.ProjectPath);
                Assert.Equal("other/File.cs", repository.SourcePath);
                Assert.Equal(PolicyExpectation.Optional, repository.Expectation);
                Assert.Null(repository.MatchedRuleId);
            },
            fourth =>
            {
                var generated = Assert.IsType<GeneratedPolicyContribution>(fourth);
                Assert.Equal("projects/App/App.csproj", generated.ProjectPath);
                Assert.Equal(PolicyExpectation.Forbidden, generated.Expectation);
                Assert.Equal("generated-forbidden", generated.MatchedRuleId);
                Assert.Equal(producerId, generated.GeneratedOutput.ProducerId);
                Assert.Equal(outputId, generated.GeneratedOutput.OutputId);
            });
    }

    [Fact]
    public void ParseAndEvaluationCancellation_ExposeNoPartialSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var parse = PolicyConfigurationEvaluator.Parse(
            "{}"u8.ToArray(),
            cancellation.Token);
        Assert.Equal(PolicyRunStatus.Cancelled, parse.Status);
        Assert.Null(parse.Document);
        Assert.Null(parse.PrimaryFailure);

        var policy = ParsePolicy(
            """
            {"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"optional"}
            """);
        var evaluation = PolicyConfigurationEvaluator.Evaluate(
            policy,
            PolicyConfigurationInput.Repository("a.csproj", "a.cs"),
            cancellation.Token);
        Assert.Equal(PolicyRunStatus.Cancelled, evaluation.Status);
        Assert.Null(evaluation.ContributionSet);
        Assert.Null(evaluation.PrimaryFailure);
    }

    private static ConformanceOutcome EvaluateProduction(
        string fixtureRoot,
        ConformanceCase conformanceCase)
    {
        byte[]? payload = null;
        if (conformanceCase.PayloadFile is not null)
        {
            var path = ResolveFixture(fixtureRoot, conformanceCase.PayloadFile);
            payload = conformanceCase.PayloadEncoding == "base64"
                ? Convert.FromBase64String(File.ReadAllText(path).Trim())
                : File.ReadAllBytes(path);
        }
        else if (conformanceCase.PolicyFile is not null)
        {
            payload = File.ReadAllBytes(ResolveFixture(
                fixtureRoot,
                conformanceCase.PolicyFile));
        }

        var parse = PolicyConfigurationEvaluator.Parse(payload);
        if (parse.Status != PolicyRunStatus.Success)
        {
            return ToConformance(parse.PrimaryFailure);
        }

        var input = conformanceCase.Input
            ?? throw new InvalidOperationException("Policy case input is missing.");
        PolicyContributionInput productionInput;
        if (input.SourcePath is not null && input.GeneratedOutput is null)
        {
            productionInput = PolicyConfigurationInput.Repository(
                input.ProjectPath,
                input.SourcePath);
        }
        else if (input.SourcePath is null && input.GeneratedOutput is not null)
        {
            productionInput = PolicyConfigurationInput.Generated(
                input.ProjectPath,
                input.GeneratedOutput.ProducerKind,
                input.GeneratedOutput.ProducerId,
                input.GeneratedOutput.OutputId);
        }
        else
        {
            productionInput = PolicyConfigurationInput.Raw(
                input.ProjectPath,
                input.SourcePath,
                input.GeneratedOutput?.ProducerKind,
                input.GeneratedOutput?.ProducerId,
                input.GeneratedOutput?.OutputId);
        }

        var evaluated = PolicyConfigurationEvaluator.Evaluate(
            parse.Document!,
            productionInput);
        if (evaluated.Status != PolicyRunStatus.Success)
        {
            return ToConformance(evaluated.PrimaryFailure);
        }

        var contribution = Assert.Single(evaluated.ContributionSet!.Contributions);
        return new ConformanceOutcome(
            PolicyConfigurationVocabulary.GetId(contribution.Expectation),
            contribution.MatchedRuleId);
    }

    private static ConformanceOutcome ToConformance(PolicyFailure? failure)
    {
        Assert.NotNull(failure);
        return new ConformanceOutcome(
            Error: new ConformanceError(
                failure.Code,
                failure.Pointer,
                failure.SchemaKeyword));
    }

    private static PolicyDocumentV1 ParsePolicy(string json)
    {
        var outcome = PolicyConfigurationEvaluator.Parse(
            System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal(PolicyRunStatus.Success, outcome.Status);
        return outcome.Document!;
    }

    private static string ResolveFixture(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static JsonSchema LoadPolicySchema(string root)
    {
        var schemaDocument = JsonNode.Parse(File.ReadAllText(Path.Combine(
            root,
            "schemas",
            "policy-configuration",
            "v1.schema.json")))!.AsObject();
        schemaDocument.Remove("$id");
        return JsonSchema.FromText(schemaDocument.ToJsonString());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }
}
