using System.Collections.Immutable;

namespace ContractScribe.Core;

public enum PolicyExpectation
{
    Required,
    Optional,
    Forbidden,
}

public enum GeneratedOutputKind
{
    SourceGenerator,
    ToolGenerated,
}

public enum PolicyRunStatus
{
    Success,
    Failure,
    Cancelled,
}

public static class PolicyConfigurationVocabulary
{
    public const int SchemaVersion = 1;

    public static string GetId(PolicyExpectation value) => value switch
    {
        PolicyExpectation.Required => "required",
        PolicyExpectation.Optional => "optional",
        PolicyExpectation.Forbidden => "forbidden",
        _ => throw Unknown(value),
    };

    public static string GetId(GeneratedOutputKind value) => value switch
    {
        GeneratedOutputKind.SourceGenerator => "source-generator",
        GeneratedOutputKind.ToolGenerated => "tool-generated",
        _ => throw Unknown(value),
    };

    private static ArgumentOutOfRangeException Unknown<T>(T value)
        where T : struct, Enum =>
        new(
            nameof(value),
            value,
            "The value is outside the closed Policy/Configuration vocabulary.");
}

public sealed record PolicyFailure(
    string Code,
    string? Pointer = null,
    string? SchemaKeyword = null);

public sealed class PolicyDocumentV1
{
    internal PolicyDocumentV1(
        TargetProfile targetProfile,
        PolicyExpectation defaultExpectation,
        ImmutableArray<PolicyRuleV1> rules)
    {
        TargetProfile = targetProfile;
        DefaultExpectation = defaultExpectation;
        Rules = rules;
    }

    public TargetProfile TargetProfile { get; }

    public PolicyExpectation DefaultExpectation { get; }

    internal ImmutableArray<PolicyRuleV1> Rules { get; }
}

public sealed class PolicyParseOutcome
{
    private PolicyParseOutcome(
        PolicyRunStatus status,
        PolicyDocumentV1? document,
        PolicyFailure? primaryFailure)
    {
        Status = status;
        Document = document;
        PrimaryFailure = primaryFailure;
    }

    public PolicyRunStatus Status { get; }

    public PolicyDocumentV1? Document { get; }

    public PolicyFailure? PrimaryFailure { get; }

    internal static PolicyParseOutcome Success(PolicyDocumentV1 document) =>
        new(PolicyRunStatus.Success, document, null);

    internal static PolicyParseOutcome Failure(PolicyFailure failure) =>
        new(PolicyRunStatus.Failure, null, failure);

    internal static PolicyParseOutcome Cancelled() =>
        new(PolicyRunStatus.Cancelled, null, null);
}

public abstract class PolicyContributionInput
{
    private protected PolicyContributionInput(string projectPath)
    {
        ProjectPath = projectPath;
    }

    public string ProjectPath { get; }
}

public sealed class RepositoryPolicyContributionInput : PolicyContributionInput
{
    internal RepositoryPolicyContributionInput(
        string projectPath,
        string sourcePath)
        : base(projectPath)
    {
        SourcePath = sourcePath;
    }

    public string SourcePath { get; }
}

public sealed class GeneratedPolicyContributionInput : PolicyContributionInput
{
    internal GeneratedPolicyContributionInput(
        string projectPath,
        string? producerKind,
        string? producerId,
        string? outputId)
        : base(projectPath)
    {
        ProducerKind = producerKind;
        ProducerId = producerId;
        OutputId = outputId;
    }

    public string? ProducerKind { get; }

    public string? ProducerId { get; }

    public string? OutputId { get; }
}

public sealed class UnvalidatedPolicyContributionInput : PolicyContributionInput
{
    internal UnvalidatedPolicyContributionInput(
        string projectPath,
        string? sourcePath,
        string? producerKind,
        string? producerId,
        string? outputId)
        : base(projectPath)
    {
        SourcePath = sourcePath;
        ProducerKind = producerKind;
        ProducerId = producerId;
        OutputId = outputId;
    }

    public string? SourcePath { get; }

    public string? ProducerKind { get; }

    public string? ProducerId { get; }

    public string? OutputId { get; }
}

public static class PolicyConfigurationInput
{
    public static RepositoryPolicyContributionInput Repository(
        string projectPath,
        string sourcePath) =>
        new(projectPath, sourcePath);

    public static GeneratedPolicyContributionInput Generated(
        string projectPath,
        string? producerKind,
        string? producerId,
        string? outputId) =>
        new(projectPath, producerKind, producerId, outputId);

    public static UnvalidatedPolicyContributionInput Raw(
        string projectPath,
        string? sourcePath,
        string? producerKind,
        string? producerId,
        string? outputId) =>
        new(projectPath, sourcePath, producerKind, producerId, outputId);
}

public sealed record GeneratedOutputIdentity
{
    internal GeneratedOutputIdentity(
        GeneratedOutputKind producerKind,
        string producerId,
        string outputId)
    {
        ProducerKind = producerKind;
        ProducerId = producerId;
        OutputId = outputId;
    }

    public GeneratedOutputKind ProducerKind { get; }

    public string ProducerId { get; }

    public string OutputId { get; }
}

public abstract record PolicyContribution
{
    private protected PolicyContribution(
        string projectPath,
        PolicyExpectation expectation,
        string? matchedRuleId)
    {
        ProjectPath = projectPath;
        Expectation = expectation;
        MatchedRuleId = matchedRuleId;
    }

    public string ProjectPath { get; }

    public PolicyExpectation Expectation { get; }

    public string? MatchedRuleId { get; }
}

public sealed record RepositoryPolicyContribution : PolicyContribution
{
    internal RepositoryPolicyContribution(
        string projectPath,
        string sourcePath,
        PolicyExpectation expectation,
        string? matchedRuleId)
        : base(projectPath, expectation, matchedRuleId)
    {
        SourcePath = sourcePath;
    }

    public string SourcePath { get; }
}

public sealed record GeneratedPolicyContribution : PolicyContribution
{
    internal GeneratedPolicyContribution(
        string projectPath,
        GeneratedOutputIdentity generatedOutput,
        PolicyExpectation expectation,
        string? matchedRuleId)
        : base(projectPath, expectation, matchedRuleId)
    {
        GeneratedOutput = generatedOutput;
    }

    public GeneratedOutputIdentity GeneratedOutput { get; }
}

public sealed record PolicyContributionSet
{
    internal PolicyContributionSet(
        TargetProfile targetProfile,
        ImmutableArray<PolicyContribution> contributions)
    {
        TargetProfile = targetProfile;
        Contributions = contributions;
    }

    public TargetProfile TargetProfile { get; }

    public ImmutableArray<PolicyContribution> Contributions { get; }
}

public sealed class PolicyEvaluationOutcome
{
    private PolicyEvaluationOutcome(
        PolicyRunStatus status,
        PolicyContributionSet? contributionSet,
        PolicyFailure? primaryFailure)
    {
        Status = status;
        ContributionSet = contributionSet;
        PrimaryFailure = primaryFailure;
    }

    public PolicyRunStatus Status { get; }

    public PolicyContributionSet? ContributionSet { get; }

    public PolicyFailure? PrimaryFailure { get; }

    internal static PolicyEvaluationOutcome Success(
        PolicyContributionSet contributionSet) =>
        new(PolicyRunStatus.Success, contributionSet, null);

    internal static PolicyEvaluationOutcome Failure(PolicyFailure failure) =>
        new(PolicyRunStatus.Failure, null, failure);

    internal static PolicyEvaluationOutcome Cancelled() =>
        new(PolicyRunStatus.Cancelled, null, null);
}

internal sealed record PolicyRuleV1(
    string Id,
    int Priority,
    PolicyExpectation Expectation,
    PolicyPathSelectorV1? ProjectPaths,
    PolicyPathSelectorV1? SourcePaths);

internal sealed record PolicyPathSelectorV1(
    ImmutableArray<string> Include,
    ImmutableArray<string> Exclude);
