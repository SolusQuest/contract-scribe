using ContractScribe.Cli;

namespace ContractScribe.Tests;

public sealed class GitHubCheckoutBindingTests
{
    [Theory]
    [InlineData("/define:LOCAL /nullable:enable", true)]
    [InlineData("# ignored @other.rsp\n/define:LOCAL # /ruleset:hidden\r\n/define:SECOND", true)]
    [InlineData("\"/define:LOCAL\"\n/d:\"SECOND\" /nowarn:\"0168,0219\"", true)]
    [InlineData("/unsafe+ /unsafe- /checked+ /optimize- /warnaserror:CS0168", true)]
    [InlineData("/define:LOCAL\n@\"nested options.rsp\"", false)]
    [InlineData("/define:LOCAL\n# first comment\n/ruleset:rules.ruleset", false)]
    [InlineData("/define:LOCAL \"/ruleset:rules.ruleset\"", false)]
    [InlineData("/define:LOCAL /r:../References/Dependency.dll", false)]
    [InlineData("/define:LOCAL ../Shared/*.cs", false)]
    [InlineData("/define:LOCAL /future-file-option:input.json", false)]
    [InlineData("/define+:LOCAL", false)]
    [InlineData("/de\"fine\":LOCAL", false)]
    public void Response_admission_uses_Roslyn_lexing_and_an_explicit_scalar_boundary(string contents, bool allowed)
    {
        using var reader = new StringReader(contents);
        Assert.Equal(allowed, GitHubCheckoutBinding.ScalarResponseArguments(reader));
    }
}
