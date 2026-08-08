using System.Diagnostics;
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

        var blockingMarkers = context.AnalyzerConfigOptionsProvider.Select(
            static (options, _) =>
            {
                options.GlobalOptions.TryGetValue(
                    "build_property.ContractScribeTestGeneratorBlockingMarker",
                    out var markerPath);
                options.GlobalOptions.TryGetValue(
                    "build_property.ContractScribeTestGeneratorReleaseMarker",
                    out var releasePath);
                return (MarkerPath: markerPath, ReleasePath: releasePath);
            });
        context.RegisterSourceOutput(blockingMarkers, static (output, markers) =>
        {
#pragma warning disable RS1035 // Test-only real production workspace-load timeout seam.
            if (string.IsNullOrWhiteSpace(markers.MarkerPath))
            {
                return;
            }

            File.WriteAllText(markers.MarkerPath, "generator-entered");
            while (string.IsNullOrWhiteSpace(markers.ReleasePath)
                   || !File.Exists(markers.ReleasePath))
            {
                output.CancellationToken.ThrowIfCancellationRequested();
                _ = output.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(10));
            }
            output.AddSource(
                "Fixture.Blocking.g.cs",
                SourceText.From(
                    "public static class FixtureBlocking { }",
                    Encoding.UTF8));
#pragma warning restore RS1035
        });

        var consoleOutputSettings = context.AnalyzerConfigOptionsProvider.Select(
            static (options, _) =>
            {
                options.GlobalOptions.TryGetValue(
                    "build_property.ContractScribeTestGeneratorConsoleMarker",
                    out var marker);
                options.GlobalOptions.TryGetValue(
                    "build_property.ContractScribeTestGeneratorChildProgram",
                    out var childProgram);
                options.GlobalOptions.TryGetValue(
                    "build_property.ContractScribeTestGeneratorDotnetHost",
                    out var dotnetHost);
                return (Marker: marker, ChildProgram: childProgram, DotnetHost: dotnetHost);
            });
        context.RegisterSourceOutput(consoleOutputSettings, static (output, settings) =>
        {
#pragma warning disable RS1035 // Test-only process-stream isolation regression.
            if (string.IsNullOrWhiteSpace(settings.Marker))
            {
                return;
            }

            Console.Out.Write($"{settings.Marker}-managed-out");
            Console.Error.WriteLine($"{settings.Marker}-managed-error");
            if (!string.IsNullOrWhiteSpace(settings.ChildProgram))
            {
                if (string.IsNullOrWhiteSpace(settings.DotnetHost))
                {
                    throw new InvalidOperationException("The dotnet host path is unavailable.");
                }
                var startInfo = new ProcessStartInfo
                {
                    FileName = settings.DotnetHost,
                    Arguments = $"\"{settings.ChildProgram}\" emit-streams \"{settings.Marker}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var child = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("The stream-emitting child did not start.");
                child.WaitForExit();
                if (child.ExitCode != 0)
                {
                    throw new InvalidOperationException("The stream-emitting child failed.");
                }
            }

            output.AddSource(
                "Fixture.ConsoleOutput.g.cs",
                SourceText.From(
                    "public static class FixtureConsoleOutput { }",
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
