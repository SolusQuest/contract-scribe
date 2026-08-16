using System.Collections.Immutable;
using System.Reflection;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Tests;

public sealed class DocumentationScribeSemanticToolTests
{
    [Fact]
    public void SelectionFreezesOneRegistrableMethodOperation()
    {
        Assert.Equal(
            "get-target-evidence",
            Assert.Single(DocumentationScribeSemanticToolSelection.OperationIds));
        Assert.DoesNotContain('_', DocumentationScribeSemanticToolSelection.OperationId);
        Assert.Matches(
            "^[a-z](?:[a-z0-9.-]*[a-z0-9])?$",
            DocumentationScribeSemanticToolSelection.OperationId);
        Assert.Equal(
            PrimarySymbolKind.Method,
            Assert.Single(DocumentationScribeSemanticToolSelection.SupportedTargetKinds));
        Assert.Equal(
            DocumentationScribeSemanticToolSelection.OperationId,
            new DocumentationScribeSemanticToolDescriptor().OperationId);
    }

    [Fact]
    public void ResultSourceUnionContainsOnlyTheThreeAdmittedCases()
    {
        var cases = typeof(DocumentationScribeSemanticSourceEvidence).Assembly
            .GetExportedTypes()
            .Where(type => type.IsSubclassOf(typeof(DocumentationScribeSemanticSourceEvidence)))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                typeof(DocumentationScribeSemanticRepositoryEvidence),
                typeof(DocumentationScribeSemanticSourceGeneratorEvidence),
                typeof(DocumentationScribeSemanticToolGeneratedEvidence),
            ],
            cases);
        Assert.DoesNotContain(cases, type => type.Name.Contains("Metadata", StringComparison.Ordinal));
        Assert.DoesNotContain(cases, type => type.Name.Contains("Synthetic", StringComparison.Ordinal));
    }

    [Fact]
    public void PublicSemanticSurfaceDoesNotExposeCapabilitiesOrOpenPayloads()
    {
        var semanticTypes = typeof(DocumentationScribeSemanticToolDescriptor).Assembly
            .GetExportedTypes()
            .Where(type => type.Name.StartsWith("DocumentationScribeSemantic", StringComparison.Ordinal))
            .ToArray();
        var exposed = semanticTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.PropertyType)
                .Concat(type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Where(method => method.Name is not ("Equals" or "ToString" or "Deconstruct")
                        && !method.IsSpecialName)
                    .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)))
                .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                    .SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType))))
            .Select(Unwrap)
            .ToArray();

        Assert.DoesNotContain(exposed, type => type == typeof(object));
        Assert.DoesNotContain(exposed, type => type == typeof(IServiceProvider));
        Assert.DoesNotContain(exposed, type => typeof(Delegate).IsAssignableFrom(type));
        Assert.DoesNotContain(exposed, type => type == typeof(byte[]));
        Assert.DoesNotContain(exposed, type => type.Namespace?.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(exposed, type => type.Namespace?.StartsWith("System.IO", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(exposed, type => type.Namespace?.StartsWith("System.Net", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(exposed, type => type.Namespace?.StartsWith("System.Text.Json", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(exposed, type => type.Namespace?.StartsWith("ContractScribe.Agent", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(exposed, type => type.Namespace?.StartsWith("ContractScribe.Patching", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void LimitsAndRequestsRejectUnboundedInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DocumentationScribeSemanticToolRequest.Create(0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DocumentationScribeSemanticToolRequest.Create(101));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentationScribeSemanticToolLimits(
            0,
            1,
            1024,
            1024,
            32,
            1,
            1,
            1,
            1));
        Assert.InRange(
            DocumentationScribeSemanticToolLimits.Production.MaximumElapsedMilliseconds,
            1,
            300_000);
    }

    [Fact]
    public void TerminalResultsCarryNoPageAndUseClosedReasons()
    {
        foreach (var pair in new[]
                 {
                     (DocumentationScribeToolOutcome.Failure, DocumentationScribeSemanticFailureReason.UnsupportedTargetKind),
                     (DocumentationScribeToolOutcome.Cancelled, DocumentationScribeSemanticFailureReason.Cancelled),
                     (DocumentationScribeToolOutcome.TimedOut, DocumentationScribeSemanticFailureReason.TimedOut),
                     (DocumentationScribeToolOutcome.BudgetExhausted, DocumentationScribeSemanticFailureReason.BudgetExhausted),
                 })
        {
            var result = new DocumentationScribeSemanticToolResult(pair.Item1, null, pair.Item2);
            Assert.Null(result.Page);
            Assert.Equal(pair.Item2, result.FailureReason);
            Assert.DoesNotContain("Content =", result.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SourceAndResultDumpChannelsHideAuthorizedContent()
    {
        const string marker = "SECRET-SOURCE-MARKER";
        var range = EvidenceInput.RepositoryLocator("src/Marker.cs", 2, 22).Span!.Value;
        var source = new DocumentationScribeSemanticRepositoryEvidence(
            "src/Marker.cs",
            new string('a', 64),
            new string('b', 64),
            64,
            20,
            true,
            false,
            range,
            range,
            marker);
        var item = new DocumentationScribeSemanticEvidenceItem(
            new string('c', 64),
            DocumentationScribeSemanticEvidenceKind.Usage,
            source,
            DocumentationScribeSemanticUsageKind.Invocation,
            null,
            null,
            null);

        Assert.DoesNotContain(marker, source.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, item.ToString(), StringComparison.Ordinal);
        Assert.Contains("authorized-content", source.ToString(), StringComparison.Ordinal);
        Assert.Contains("authorized-content", item.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TestMarkerAllowlistIsFiniteAndDoesNotUseNamesOrPaths()
    {
        Assert.Equal(6, DocumentationScribeSemanticToolSelection.TestMarkers.Length);
        Assert.All(DocumentationScribeSemanticToolSelection.TestMarkers, marker =>
        {
            Assert.EndsWith("Attribute", marker.AttributeMetadataName, StringComparison.Ordinal);
            Assert.DoesNotContain('/', marker.AttributeMetadataName);
            Assert.DoesNotContain('\\', marker.AttributeMetadataName);
            Assert.NotEmpty(marker.AssemblySimpleName);
        });
        Assert.Equal(
            [
                DocumentationScribeSemanticUsageKind.NameOf,
                DocumentationScribeSemanticUsageKind.Invocation,
                DocumentationScribeSemanticUsageKind.MemberReference,
            ],
            Enum.GetValues<DocumentationScribeSemanticUsageKind>());
    }

    private static Type Unwrap(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType()!;
        }
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Nullable<>)
                || definition == typeof(ImmutableArray<>))
            {
                return Unwrap(type.GetGenericArguments()[0]);
            }
        }
        return type;
    }
}
