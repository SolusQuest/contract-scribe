using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace ContractScribe.TestGenerator;

[Generator]
public sealed class FixtureGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(output =>
            output.AddSource(
                "Fixture.Generated.g.cs",
                SourceText.From(
                    "public static class FixtureGenerated { public const string Value = \"generated\"; }",
                    Encoding.UTF8)));
    }
}
