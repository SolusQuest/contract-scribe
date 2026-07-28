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

        var markerPaths = context.AnalyzerConfigOptionsProvider.Select(
            static (options, _) =>
                options.GlobalOptions.TryGetValue(
                    "build_property.ContractScribeTestGeneratorMarker",
                    out var markerPath)
                    ? markerPath
                    : null);
        context.RegisterSourceOutput(markerPaths, static (output, markerPath) =>
        {
#pragma warning disable RS1035 // Test-only process-sensitive generator regression.
            if (string.IsNullOrWhiteSpace(markerPath) || File.Exists(markerPath))
            {
                return;
            }

            File.WriteAllText(markerPath, "workspace");
            output.AddSource(
                "Fixture.WorkspaceOnly.g.cs",
                SourceText.From(
                    "public static class FixtureWorkspaceOnly { }",
                    Encoding.UTF8));
#pragma warning restore RS1035
        });

        var selfObservingEnabled = context.AnalyzerConfigOptionsProvider.Select(
            static (options, _) =>
                options.GlobalOptions.TryGetValue(
                    "build_property.ContractScribeTestGeneratorSelfObserving",
                    out var enabled)
                && string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase));
        context.RegisterSourceOutput(
            context.CompilationProvider.Combine(selfObservingEnabled),
            static (output, input) =>
            {
                if (!input.Right)
                {
                    return;
                }

                var value = input.Left.GetTypeByMetadataName("FixtureSelfAware") is null
                    ? "clean"
                    : "contaminated";
                output.AddSource(
                    "Fixture.SelfAware.g.cs",
                    SourceText.From(
                        $$"""public static class FixtureSelfAware { public const string Value = "{{value}}"; }""",
                        Encoding.UTF8));
            });
    }
}
