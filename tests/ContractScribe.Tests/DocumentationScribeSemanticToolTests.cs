using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
    public void ResultSourceReusesTheCoreClosedLocatorUnion()
    {
        Assert.True(typeof(DocumentationScribeSemanticSourceEvidence).IsSealed);
        Assert.Equal(
            typeof(DocumentationScribeEvidenceContextFact),
            typeof(DocumentationScribeSemanticSourceEvidence)
                .GetProperty(nameof(DocumentationScribeSemanticSourceEvidence.Fact))!
                .PropertyType);
        Assert.True(typeof(EvidenceLocator).IsAbstract);
        Assert.Equal(
            [
                typeof(GeneratedOutputEvidenceLocator),
                typeof(MetadataEvidenceLocator),
                typeof(RepositoryEvidenceLocator),
                typeof(SyntheticEvidenceLocator),
            ],
            typeof(EvidenceLocator).Assembly.GetExportedTypes()
                .Where(type => type.IsSubclassOf(typeof(EvidenceLocator)))
                .OrderBy(type => type.Name, StringComparer.Ordinal));
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
        var zeroResidual = new DocumentationScribeSemanticToolLimits(
            1,
            0,
            0,
            1024,
            32,
            1,
            1,
            1,
            1);
        Assert.Equal(0, zeroResidual.MaximumOptionalItems);
        Assert.Equal(0, zeroResidual.MaximumResultUtf8Bytes);
        Assert.InRange(
            DocumentationScribeSemanticToolLimits.Production.MaximumResultUtf8Bytes,
            0,
            DocumentationScribeContract.MaximumArtifactUtf8Bytes);
    }

    [Theory]
    [InlineData(
        DocumentationScribeSemanticAccessibility.Internal,
        DocumentationScribeSemanticAccessibility.Protected,
        DocumentationScribeSemanticAccessibility.PrivateProtected)]
    [InlineData(
        DocumentationScribeSemanticAccessibility.Internal,
        DocumentationScribeSemanticAccessibility.ProtectedInternal,
        DocumentationScribeSemanticAccessibility.Internal)]
    [InlineData(
        DocumentationScribeSemanticAccessibility.Protected,
        DocumentationScribeSemanticAccessibility.ProtectedInternal,
        DocumentationScribeSemanticAccessibility.Protected)]
    [InlineData(
        DocumentationScribeSemanticAccessibility.PrivateProtected,
        DocumentationScribeSemanticAccessibility.ProtectedInternal,
        DocumentationScribeSemanticAccessibility.PrivateProtected)]
    public void AccessibilityUsesDomainIntersectionInsteadOfNumericRank(
        DocumentationScribeSemanticAccessibility left,
        DocumentationScribeSemanticAccessibility right,
        DocumentationScribeSemanticAccessibility expected)
    {
        var method = typeof(DocumentationScribeSemanticToolPort).GetMethod(
            "IntersectAccessibility",
            BindingFlags.NonPublic | BindingFlags.Static);
        var actual = method!.Invoke(null, [left, right]);

        Assert.Equal(expected, actual);
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
        var markerBytes = Encoding.UTF8.GetBytes(marker);
        var markerSha256 = Convert.ToHexString(SHA256.HashData(markerBytes)).ToLowerInvariant();
        var locator = EvidenceInput.RepositoryLocator("src/Marker.cs", 2, 22);
        var range = locator.Span!.Value;
        var commitment = DocumentationScribeContextValidation.CreateEvidenceSourceCommitment(
            locator,
            markerSha256,
            markerSha256,
            markerBytes.Length,
            markerBytes.Length,
            false,
            false,
            false);
        var fact = DocumentationScribeContextValidation.CreateEvidenceFact(
            DocumentationScribeContextAuthority.Usage,
            DocumentationScribeContextRole.UsageEvidence,
            "symbol.marker",
            "semantic.usage.invocation",
            commitment,
            marker,
            range.Start,
            range.End,
            range.Start,
            range.End);
        var source = new DocumentationScribeSemanticSourceEvidence(
            fact,
            "compilation.marker",
            new string('d', 64));
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
