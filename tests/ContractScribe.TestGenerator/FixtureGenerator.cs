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

        var blockingMarkerPaths = context.AnalyzerConfigOptionsProvider.Select(
            static (options, _) =>
                options.GlobalOptions.TryGetValue(
                    "build_property.ContractScribeTestGeneratorBlockingMarker",
                    out var markerPath)
                    ? markerPath
                    : null);
        context.RegisterSourceOutput(blockingMarkerPaths, static (output, markerPath) =>
        {
#pragma warning disable RS1035 // Test-only real production workspace-load timeout seam.
            if (string.IsNullOrWhiteSpace(markerPath))
            {
                return;
            }

            File.WriteAllText(markerPath, "generator-entered");
            Thread.Sleep(TimeSpan.FromMinutes(3));
            output.AddSource(
                "Fixture.Blocking.g.cs",
                SourceText.From(
                    "public static class FixtureBlocking { }",
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

        var manyOutputCounts = context.AnalyzerConfigOptionsProvider.Select(
            static (options, _) =>
                options.GlobalOptions.TryGetValue(
                    "build_property.ContractScribeTestGeneratorManyOutputs",
                    out var value)
                && int.TryParse(value, out var count)
                    ? count
                    : 0);
        context.RegisterSourceOutput(
            manyOutputCounts,
            static (output, count) =>
            {
                for (var index = 0; index < count; index++)
                {
                    output.AddSource(
                        $"Fixture.Many.{index:D4}.g.cs",
                        SourceText.From(
                            $"public static class FixtureMany{index:D4} {{ }}",
                            Encoding.UTF8));
                }
            });

        var dynamicAdditionalValues = context.AdditionalTextsProvider
            .Where(static text =>
                string.Equals(
                    Path.GetFileName(text.Path),
                    "DynamicGeneratorInput.txt",
                    StringComparison.OrdinalIgnoreCase))
            .Select(static (text, cancellationToken) =>
                text.GetText(cancellationToken)?.ToString() ?? string.Empty);
        context.RegisterSourceOutput(dynamicAdditionalValues, static (output, value) =>
            output.AddSource(
                "Fixture.DynamicAdditional.g.cs",
                SourceText.From(
                    $$"""public static class FixtureDynamicAdditional { public const string Value = "{{value}}"; }""",
                    Encoding.UTF8)));

        var dynamicAnalyzerConfigValues = context.AnalyzerConfigOptionsProvider.Select(
            static (options, _) =>
                options.GlobalOptions.TryGetValue(
                    "contract_scribe_dynamic_option",
                    out var value)
                    ? value
                    : null);
        context.RegisterSourceOutput(dynamicAnalyzerConfigValues, static (output, value) =>
        {
            if (value is null)
            {
                return;
            }

            output.AddSource(
                "Fixture.DynamicAnalyzerConfig.g.cs",
                SourceText.From(
                    $$"""public static class FixtureDynamicAnalyzerConfig { public const string Value = "{{value}}"; }""",
                    Encoding.UTF8));
        });
    }
}

[Generator]
public sealed class CollisionGeneratorA : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        Register(context);

    internal static void Register(
        IncrementalGeneratorInitializationContext context)
    {
        var enabled = context.AnalyzerConfigOptionsProvider.Select(
            static (options, _) =>
                options.GlobalOptions.TryGetValue(
                    "build_property.ContractScribeTestGeneratorCollisions",
                    out var value)
                && string.Equals(
                    value,
                    "true",
                    StringComparison.OrdinalIgnoreCase));
        context.RegisterSourceOutput(enabled, static (output, shouldEmit) =>
        {
            if (shouldEmit)
            {
                output.AddSource(
                    "Shared.g.cs",
                    SourceText.From(
                        "// identical collision source",
                        Encoding.UTF8));
            }
        });
    }
}

[Generator]
public sealed class CollisionGeneratorB : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        CollisionGeneratorA.Register(context);
}
