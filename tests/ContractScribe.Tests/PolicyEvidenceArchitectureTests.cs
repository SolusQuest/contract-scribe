using System.Reflection;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class PolicyEvidenceArchitectureTests
{
    [Fact]
    public void PublicPolicyEvidenceContractGraph_RemainsPlatformNeutral()
    {
        var roots = new[]
        {
            typeof(PolicyDocumentV1),
            typeof(PolicyParseOutcome),
            typeof(PolicyContributionInput),
            typeof(PolicyContributionSet),
            typeof(PolicyEvaluationOutcome),
            typeof(EvidenceSubject),
            typeof(EvidenceLocator),
            typeof(EvidenceCandidateInput),
            typeof(EvidenceBundle),
            typeof(EvidenceObservationCommitment),
            typeof(EvidenceDeclarationBindingInput),
            typeof(EvidenceAuthoritySet),
            typeof(BoundObservationEvidence),
            typeof(EvidenceNormalizationOutcome),
            typeof(EvidenceBindingOutcome),
        };
        var exposed = roots
            .SelectMany(PublicSignatureTypes)
            .SelectMany(ExpandType)
            .Distinct()
            .ToArray();

        Assert.DoesNotContain(exposed, type =>
            type.Assembly.GetName().Name is { } assemblyName
            && (assemblyName.StartsWith(
                    "Microsoft.CodeAnalysis",
                    StringComparison.Ordinal)
                || assemblyName.StartsWith(
                    "Microsoft.Build",
                    StringComparison.Ordinal)));
        Assert.DoesNotContain(exposed, type =>
            type.Name.Contains("SyntaxTree", StringComparison.Ordinal)
            || type.Name.Contains("Compilation", StringComparison.Ordinal)
            || type.Name.Contains("Workspace", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizedTerminalPolicyEvidenceTypes_CannotBePubliclyForged()
    {
        var terminalTypes = new[]
        {
            typeof(PolicyDocumentV1),
            typeof(RepositoryPolicyContribution),
            typeof(GeneratedPolicyContribution),
            typeof(PolicyContributionSet),
            typeof(EvidenceItem),
            typeof(EvidenceObservationCommitment),
            typeof(EvidenceBundle),
            typeof(EvidenceAuthorityRow),
            typeof(EvidenceAuthoritySet),
            typeof(BoundObservationEvidence),
        };

        Assert.All(terminalTypes, type => Assert.Empty(type.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public)));
        Assert.Null(typeof(PolicyParseOutcome).GetMethod(
            "Success",
            BindingFlags.Static | BindingFlags.Public));
        Assert.Null(typeof(PolicyEvaluationOutcome).GetMethod(
            "Success",
            BindingFlags.Static | BindingFlags.Public));
        Assert.Null(typeof(EvidenceNormalizationOutcome).GetMethod(
            "Success",
            BindingFlags.Static | BindingFlags.Public));
        Assert.Null(typeof(EvidenceBindingOutcome).GetMethod(
            "Success",
            BindingFlags.Static | BindingFlags.Public));
    }

    [Fact]
    public void CoreInputsExposeRegionsAndNormalizedFacts_NotRoslynOrFullSourceSnapshots()
    {
        var propertyNames = typeof(EvidenceCandidateInput)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        Assert.Contains("OriginalRegion", propertyNames);
        Assert.DoesNotContain(propertyNames, name =>
            name.Contains("FullSource", StringComparison.Ordinal)
            || name.Contains("SyntaxTree", StringComparison.Ordinal)
            || name.Contains("Compilation", StringComparison.Ordinal));

        var contributionProperties = typeof(PolicyContribution)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        Assert.Contains("ProjectPath", contributionProperties);
        Assert.Contains("Expectation", contributionProperties);
        Assert.Contains("MatchedRuleId", contributionProperties);
        Assert.DoesNotContain(contributionProperties, name =>
            name.Contains("Reason", StringComparison.Ordinal)
            || name.Contains("Failure", StringComparison.Ordinal)
            || name.Contains("Resolution", StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionPolicyEvidenceCode_HasNoProviderMutationOrAggregationOwnership()
    {
        var root = FindRepositoryRoot();
        var core = string.Join(Environment.NewLine, new[]
        {
            "PolicyConfigurationContracts.cs",
            "PolicyConfigurationEvaluator.cs",
            "EvidenceContracts.cs",
            "EvidenceNormalization.cs",
        }.Select(file => File.ReadAllText(Path.Combine(
            root,
            "src",
            "ContractScribe.Core",
            file))));
        var roslyn = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ContractScribe.Roslyn",
            "PolicyEvidenceExtractor.cs"));
        var combined = core + Environment.NewLine + roslyn;

        Assert.DoesNotContain("Microsoft.CodeAnalysis", core, StringComparison.Ordinal);

        foreach (var forbidden in new[]
        {
            "HttpClient",
            "Octokit",
            "Process.Start",
            "Environment.SetEnvironmentVariable",
            "File.Write",
            "policyResolution",
            "PolicyResolution",
            "AuditResult",
        })
        {
            Assert.DoesNotContain(forbidden, combined, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<Type> PublicSignatureTypes(Type type)
    {
        yield return type;
        foreach (var property in type.GetProperties(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public))
        {
            yield return property.PropertyType;
        }

        foreach (var method in type.GetMethods(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
    }

    private static IEnumerable<Type> ExpandType(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var nested in ExpandType(element))
            {
                yield return nested;
            }
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in ExpandType(argument))
            {
                yield return nested;
            }
        }
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
