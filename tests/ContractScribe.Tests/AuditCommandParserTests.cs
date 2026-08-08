using ContractScribe.Cli;

namespace ContractScribe.Tests;

public sealed class AuditCommandParserTests
{
    [Theory]
    [InlineData("--bogus", "unknown-option")]
    [InlineData("--input a.csproj --input a.csproj", "duplicate-option")]
    [InlineData("operand", "unexpected-operand")]
    [InlineData("--help --bogus", "forbidden-combination")]
    public void Parse_SelectsTheClosedUsagePrecedence(string suffix, string expectedClass)
    {
        var tokens = (
            "--repository-root . --input a.csproj --policy policy.json --output ../result.json "
            + suffix).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var parsed = AuditCommandParser.Parse(tokens);

        Assert.Null(parsed.Arguments);
        Assert.Equal(expectedClass, parsed.Failure!.UsageClass);
        Assert.Equal($"cli.usage.{expectedClass}", parsed.Failure.Code);
    }

    [Fact]
    public void Parse_AcceptsEqualsFormInAnyOrder()
    {
        var parsed = AuditCommandParser.Parse(
            ["--output=o", "--policy=p", "--input=i", "--repository-root=r"]);

        Assert.Null(parsed.Failure);
        Assert.Equal(new AuditCommandArguments("r", "i", "p", "o"), parsed.Arguments);
    }

    [Fact]
    public void Parse_DoesNotConsumeFollowingLongOptionAsAValue()
    {
        var parsed = AuditCommandParser.Parse(
            ["--repository-root", ".", "--input", "--policy", "p", "--output", "o"]);

        Assert.Equal("missing-option-value", parsed.Failure!.UsageClass);
    }
}
