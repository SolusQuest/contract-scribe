using System.Text;
using System.Text.Json.Nodes;
using ContractScribe.Cli;

namespace ContractScribe.Tests;

public sealed class CampaignCommandParserTests
{
    private static readonly string[] Complete =
    [
        "start", "--repository-root", "repo", "--input", "input.slnx",
        "--policy", "policy.json", "--snapshot", "snapshot.v1", "--state",
        "state.json", "--configuration", "campaign.json",
    ];

    [Fact]
    public void Parse_AcceptsOnlyTheSixRequiredOptionsInEitherFormAndAnyOrder()
    {
        var parsed = CampaignCommandParser.Parse(
        [
            "resume", "--configuration=c", "--state=s", "--snapshot=snapshot:v1",
            "--policy=p", "--input=i.slnx", "--repository-root=r",
        ]);

        Assert.Null(parsed.Failure);
        Assert.Equal(CampaignOperation.Resume, parsed.Arguments!.Operation);
        Assert.Equal("r", parsed.Arguments.RepositoryRoot);
        Assert.Equal("snapshot:v1", parsed.Arguments.Snapshot);
    }

    [Theory]
    [InlineData("forbidden-combination", "--help", "--unknown")]
    [InlineData("unknown-option", "--unknown", "value")]
    [InlineData("duplicate-option", "--state", "other.json")]
    [InlineData("unexpected-operand", "operand", "operand")]
    public void Parse_FreezesUsagePrecedence(string expected, string first, string second)
    {
        var tokens = Complete.Concat([first, second]).ToArray();
        var result = CampaignCommandParser.Parse(tokens);

        Assert.Equal(expected, result.Failure!.UsageClass);
        Assert.Equal("cli.usage." + expected, result.Failure.Code);
    }

    [Fact]
    public void Parse_DistinguishesMissingAndInvalidValues()
    {
        Assert.Equal("missing-option-value", CampaignCommandParser.Parse(["start", "--state"]).Failure!.UsageClass);
        Assert.Equal("invalid-option-value", Failure(ReplaceValue("--snapshot", "bad/value")));
    }

    [Fact]
    public void Parse_MissingRequiredOptionIsLastInPrecedence()
    {
        var result = CampaignCommandParser.Parse(
            Complete.Where(token => token is not "--configuration" and not "campaign.json").ToArray());

        Assert.Equal("missing-required-option", result.Failure!.UsageClass);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("start --help")]
    [InlineData("resume -h")]
    public void Parse_RecognizesOnlyStandaloneHelpForms(string command)
    {
        var result = CampaignCommandParser.Parse(command.Split(' '));

        Assert.True(result.HelpRequested);
        Assert.Null(result.Failure);
    }

    [Fact]
    public void Parse_BoundsSnapshotPathAndWholeArgv()
    {
        Assert.Equal("invalid-option-value", Failure(ReplaceValue("--snapshot", new string('a', 129))));
        Assert.Equal("invalid-option-value", Failure(ReplaceValue("--state", new string('a', 4097))));
        var overlongArgv = Complete.Concat(Enumerable.Repeat(new string('z', 4096), 8)).ToArray();
        Assert.Equal("invalid-option-value", Failure(overlongArgv));
    }

    [Fact]
    public void HelpFixture_IsExact()
    {
        Assert.Equal(
            CommandLineApplication.CampaignHelp,
            File.ReadAllText(Fixture("help-campaign.txt")).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Configuration_RejectsShapeOrderDuplicatesAndCrossFieldViolation()
    {
        var valid = File.ReadAllBytes(Fixture("configuration-valid.json"));
        var parsed = CampaignConfiguration.Parse(valid);
        Assert.NotNull(parsed.CreateExecutionPolicy());

        var reordered = JsonNode.Parse(valid)!.AsObject();
        var version = reordered["campaignConfigurationVersion"];
        reordered.Remove("campaignConfigurationVersion");
        reordered.Add("campaignConfigurationVersion", version);
        Assert.Throws<CampaignConfigurationException>(() => Parse(reordered));

        var crossed = JsonNode.Parse(valid)!.AsObject();
        crossed["planning"]!["maximumPatchElapsedMilliseconds"] = 120001;
        Assert.Throws<CampaignConfigurationException>(() => Parse(crossed));

        var duplicate = Encoding.UTF8.GetString(valid).Replace(
            "{\"campaignConfigurationVersion\":1,",
            "{\"campaignConfigurationVersion\":1,\"campaignConfigurationVersion\":1,",
            StringComparison.Ordinal);
        Assert.Throws<CampaignConfigurationException>(() =>
            CampaignConfiguration.Parse(Encoding.UTF8.GetBytes(duplicate)));
    }

    [Theory]
    [InlineData("campaign.complete", 0)]
    [InlineData("campaign.provider-retryable", 3)]
    [InlineData("campaign.state-conflict", 4)]
    [InlineData("campaign.patch-rejected", 5)]
    [InlineData("campaign.cancelled", 6)]
    [InlineData("campaign.timeout", 7)]
    public void Presentation_FreezesEnvelopeOrderStreamsAndExitMap(string outcome, int exit)
    {
        var result = CampaignCliPresentation.Present(
            new CliBuildIdentity("tool", "source", "baseline"),
            new CampaignTerminal("campaign", CampaignOperation.Start, outcome, 7));

        Assert.Equal(exit, result.ExitCode);
        Assert.Equal(
            "{\"campaignEnvelopeVersion\":1,\"terminalLayer\":\"campaign\",\"cliContractBaseline\":\"baseline\",\"toolVersion\":\"tool\",\"operation\":\"start\",\"outcome\":\""
            + outcome + "\",\"diagnosticCodes\":"
            + (exit == 0 ? "[]" : "[\"" + outcome + "\"]")
            + ",\"checkpointRevision\":7}\n",
            result.StandardOutput);
        Assert.Equal(exit == 0 ? 0 : 1, result.Diagnostics.Count);
    }

    [Fact]
    public void Presentation_FailsClosedWithoutEmittingUnknownLayerOrOutcome()
    {
        var result = CampaignCliPresentation.Present(
            new CliBuildIdentity("tool", "source", "baseline"),
            new CampaignTerminal("downstream.detail", CampaignOperation.Resume, "provider.raw-code", 9));

        Assert.Equal(5, result.ExitCode);
        Assert.Contains("\"terminalLayer\":\"execution\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"campaign.host-contract-error\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("downstream.detail", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("provider.raw-code", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessBoundaryHook_RejectsUnknownNamesAndPublishesClosedAllowlist()
    {
        Assert.Equal(42, CampaignProcessBoundaryHooks.Allowlist.Count);
        Assert.Contains("checkpoint.initial.before-create", CampaignProcessBoundaryHooks.Allowlist);
        Assert.Contains("changed-base.before-reconciliation", CampaignProcessBoundaryHooks.Allowlist);
        Assert.Contains("proposal.result.proposal.in-replacement", CampaignProcessBoundaryHooks.Allowlist);
        Assert.Contains("proposal.result.closed.after-replacement-before-readback", CampaignProcessBoundaryHooks.Allowlist);
        Assert.Contains("patch.result.accepted.in-replacement", CampaignProcessBoundaryHooks.Allowlist);
        Assert.Contains("patch.result.reduction.after-replacement-before-readback", CampaignProcessBoundaryHooks.Allowlist);
        Assert.Contains("patch.result.closed.after-replacement-before-readback", CampaignProcessBoundaryHooks.Allowlist);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CampaignProcessBoundaryHooks.Reach("not-allowlisted"));
    }

    [Fact]
    public void CredentialAccessor_IsClosedAndReadOnlyForAuthenticatedHttps()
    {
        var valid = CampaignConfiguration.Parse(File.ReadAllBytes(Fixture("configuration-valid.json")));
        using var loopback = Assert.IsAssignableFrom<IDisposable>(CampaignCommandRunner.CreateExchange(
            valid.Provider,
            _ => throw new InvalidOperationException("Loopback must not read a credential.")));

        var httpsJson = JsonNode.Parse(File.ReadAllBytes(Fixture("configuration-valid.json")))!.AsObject();
        httpsJson["provider"]!["endpoint"] = "https://provider.invalid/v1/chat/completions";
        var https = CampaignConfiguration.Parse(Encoding.UTF8.GetBytes(httpsJson.ToJsonString()));
        var reads = new List<string>();
        using var authenticated = Assert.IsAssignableFrom<IDisposable>(CampaignCommandRunner.CreateExchange(
            https.Provider,
            name =>
            {
                reads.Add(name);
                return "placeholder-credential";
            }));
        Assert.Equal(["CONTRACTSCRIBE_PROVIDER_API_KEY"], reads);
        Assert.Null(CampaignCommandRunner.CreateExchange(https.Provider, _ => "   "));
    }

    private static string Failure(string[] tokens) => CampaignCommandParser.Parse(tokens).Failure!.UsageClass;

    private static string[] ReplaceValue(string option, string value, string[]? source = null)
    {
        var result = (source ?? Complete).ToArray();
        var index = Array.IndexOf(result, option);
        result[index + 1] = value;
        return result;
    }

    private static void Parse(JsonObject value) =>
        CampaignConfiguration.Parse(Encoding.UTF8.GetBytes(value.ToJsonString()));

    private static string Fixture(string name) => Path.Join(
        RepositoryRoot(), "tests", "fixtures", "campaign", "cli", name);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }
        return Assert.IsType<DirectoryInfo>(directory).FullName;
    }
}
