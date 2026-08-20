using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Runtime;
using ContractScribe.Core;
using ContractScribe.Patching;
using ContractScribe.Roslyn;

namespace ContractScribe.IntegrationTests;

[Collection("Integration process lane 2")]
public sealed class DocumentationScribeEndToEndIntegrationTests
{
    private const string FreshProcessOutputVariable = "CONTRACTSCRIBE_ISSUE_108_PROBE_OUTPUT";
    private const string FreshProcessReuseVariable = "CONTRACTSCRIBE_ISSUE_108_REUSE_INPUT";
    private const string FreshProcessSource = """
        namespace EndToEnd;

        public class BaseFixture
        {
            public virtual void Run()
            {
            }
        }

        public sealed class Fixture : BaseFixture
        {
            public override void Run()
            {
            }
        }

        public static class FixtureUsage
        {
            public static void Invoke(Fixture fixture)
            {
                fixture.Run();
                fixture.Run();
            }
        }
        """;

    [Fact]
    public async Task ExactSelectedTargetTraversesRealScribeAndM2WithoutChangingOriginal()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var exchange = new ProposalExchange(fixture.Request);
        var original = File.ReadAllBytes(fixture.SourcePath);
        var baseline = CaptureRepositoryFiles(fixture.Root);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal(2, exchange.RequestCount);
        Assert.Equal(2, exchange.CompletedToolCount);
        Assert.Equal(
            new[]
            {
                DocumentationScribeToolOutcome.Complete.Id,
                DocumentationScribeToolOutcome.Complete.Id,
            },
            exchange.CompletedOutcomes.ToArray());
        Assert.Equal(original, File.ReadAllBytes(fixture.SourcePath));
        Assert.DoesNotContain(fixture.Root, outcome.ToString(), StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("PatchAccepted", outcome.Status);
            var candidate = outcome.AcceptedCandidate;
            Assert.NotNull(candidate);
            var file = Assert.Single(
                candidate.Files,
                item => item.RepositoryPath == "Fixture.cs");
            Assert.Contains("/// <summary>", Encoding.UTF8.GetString(file.Bytes.AsSpan()), StringComparison.Ordinal);
            Assert.Contains("Runs the selected operation.", Encoding.UTF8.GetString(file.Bytes.AsSpan()), StringComparison.Ordinal);
            Assert.Equal(
                new[] { "Fixture.cs" },
                outcome.PatchOutcome!.Result!.ChangedFiles.Select(item => item.Path).ToArray());
            Assert.All(candidate.Files, item =>
                Assert.True(
                    baseline.ContainsKey(item.RepositoryPath),
                    $"Candidate introduced unexpected path '{item.RepositoryPath}'."));
            foreach (var retained in candidate.Files.Where(item => item.RepositoryPath != "Fixture.cs"))
            {
                Assert.Equal(baseline[retained.RepositoryPath], retained.Bytes.AsSpan().ToArray());
            }
        }
        else
        {
            Assert.Contains(outcome.Status, new[] { "RuntimeFailure", "PatchStale" });
            Assert.Null(outcome.AcceptedCandidate);
        }
    }

    [Fact]
    public async Task StructuredSkipReturnsNoCandidateAndNeverInvokesM2()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var exchange = new SkipExchange(fixture.Request);
        var original = File.ReadAllBytes(fixture.SourcePath);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            exchange);

        Assert.True(
            outcome.Status == "ProposalSkipped",
            $"{outcome.Status}:{outcome.Code}");
        Assert.Null(outcome.AcceptedCandidate);
        Assert.Null(outcome.PatchOutcome);
        Assert.Equal(original, File.ReadAllBytes(fixture.SourcePath));
    }

    [Fact]
    public async Task CrossSessionRequestIsRejectedBeforeProviderInvocation()
    {
        await using var first = await EndToEndFixture.CreateAsync();
        await using var second = await EndToEndFixture.CreateAsync();
        var exchange = new CountingExchange();

        var outcome = await CliHarness.ExecuteAsync(
            first.SelectedAudit,
            second.RequestBytes,
            first.AttemptId,
            exchange);

        Assert.Equal("PreflightRejected", outcome.Status);
        Assert.Equal(0, exchange.RequestCount);
        Assert.Null(outcome.AcceptedCandidate);
        Assert.Null(outcome.PatchOutcome);
    }

    [Fact]
    public async Task ExactCompliantAuditOutcomeIsEligibleWithoutViolationOnlyScheduling()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var selected = fixture.SelectAuditForPolicy("optional");
        var compliant = WithAuditOutcome(fixture.RequestBytes, "audit.outcome.compliant");

        var outcome = await CliHarness.ExecuteAsync(
            selected,
            compliant.Bytes,
            fixture.AttemptId,
            new SkipExchange(compliant.Request));

        Assert.True(
            outcome.Status == "ProposalSkipped",
            $"{outcome.Status}:{outcome.Code}");
        Assert.Null(outcome.PatchOutcome);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task AuditAuthorityRejectsPolicyAndAcceptedDocumentMismatch()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var required = ParsePolicy("required");
        var extracted = new PolicyEvidenceExtractor().Extract(
            fixture.Classified,
            fixture.Observations,
            required);
        var inputs = AuditInputAssembler.Assemble(
            fixture.Classified.Classification.ClassificationSet!,
            required,
            extracted);
        var accepted = AuditAggregator.Aggregate(
            TargetProfile.ExternalApi,
            fixture.Classified.Classification.ClassificationSet!,
            required,
            inputs);

        var thrown = Assert.Throws<TargetInvocationException>(() =>
            CliHarness.CreateAuditAuthority(
                fixture.Classified,
                fixture.Observations,
                ParsePolicy("optional"),
                inputs,
                accepted));

        Assert.True(thrown.InnerException is ArgumentException or AuditValidationException);
    }

    [Fact]
    public async Task AuditAuthorityRejectsCrossSessionObservationEvidenceSubstitution()
    {
        await using var first = await EndToEndFixture.CreateAsync();
        const string documentedSource = """
            namespace EndToEnd;

            public class BaseFixture
            {
                public virtual void Run()
                {
                }
            }

            public sealed class Fixture : BaseFixture
            {
                /// <summary>Already documented in the second session.</summary>
                public override void Run()
                {
                }
            }
            """;
        await using var second = await EndToEndFixture.CreateAsync(documentedSource);
        var policy = ParsePolicy("required");
        var firstExtraction = new PolicyEvidenceExtractor().Extract(
            first.Classified,
            first.Observations,
            policy);
        var firstInputs = AuditInputAssembler.Assemble(
            first.Classified.Classification.ClassificationSet!,
            policy,
            firstExtraction);
        var secondExtraction = new PolicyEvidenceExtractor().Extract(
            second.Classified,
            second.Observations,
            policy);
        var substituted = Assert.Single(secondExtraction.Bindings, item =>
            item.Subject.ComponentKind is null
            && item.Subject.ParentSymbolRef == second.Target.SymbolRef);
        var mixedInputs = firstInputs.Select(input =>
            input is TargetAuditInput targetInput
                && ReferenceEquals(targetInput.Classification, first.Target)
                ? AuditInput.Target(
                    first.Target,
                    substituted.PolicyContributions,
                    substituted.Evidence)
                : input).ToArray();
        var mixedDocument = AuditAggregator.Aggregate(
            TargetProfile.ExternalApi,
            first.Classified.Classification.ClassificationSet!,
            policy,
            mixedInputs);

        var thrown = Assert.Throws<TargetInvocationException>(() =>
            CliHarness.CreateAuditAuthority(
                first.Classified,
                first.Observations,
                policy,
                mixedInputs,
                mixedDocument));

        Assert.IsType<ArgumentException>(thrown.InnerException);
    }

    [Fact]
    public async Task NonMethodSelectedCapabilityIsRejectedBeforeProviderInvocation()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var exchange = new CountingExchange();

        var outcome = await CliHarness.ExecuteAsync(
            fixture.NonMethodSelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("PreflightRejected", outcome.Status);
        Assert.Equal(0, exchange.RequestCount);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task CallerCancellationReturnsStableNoCandidateOutcome()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            new CountingExchange(),
            cancellation.Token);

        Assert.Equal("Cancelled", outcome.Status);
        Assert.Equal("scribe.cancelled.caller", outcome.Code);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task InvalidToolArgumentsStopAtTheClosedRuntimeBoundary()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var original = File.ReadAllBytes(fixture.SourcePath);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            new InvalidToolExchange());

        Assert.Equal("RuntimeFailure", outcome.Status);
        Assert.Equal("scribe.failure.tool-protocol", outcome.Code);
        Assert.Null(outcome.PatchOutcome);
        Assert.Null(outcome.AcceptedCandidate);
        Assert.Equal(original, File.ReadAllBytes(fixture.SourcePath));
    }

    [Fact]
    public async Task ArbitrarySourceRangeCannotPublishSelectedDeclarationEvidence()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var exchange = new ReadThenSkipExchange("evidence.source");

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            exchange);

        Assert.True(
            outcome.Status == "ProposalSkipped",
            $"{outcome.Status}:{outcome.Code}");
        var completed = Assert.Single(exchange.Completed);
        Assert.Empty(completed.EvidenceReferences);
    }

    [Fact]
    public async Task CompleteEvidenceRequirementKeepsPartialSourceReadInformational()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var request = WithCompleteEvidenceRequirement(fixture.RequestBytes);
        var exchange = new ReadThenSkipExchange("evidence.source");
        var original = File.ReadAllBytes(fixture.SourcePath);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            request.Bytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("ProposalSkipped", outcome.Status);
        Assert.Equal(2, exchange.RequestCount);
        var completed = Assert.Single(exchange.Completed);
        Assert.Contains(
            completed.OutcomeId,
            new[]
            {
                DocumentationScribeToolOutcome.Complete.Id,
                DocumentationScribeToolOutcome.Incomplete.Id,
            });
        Assert.Empty(completed.EvidenceReferences);
        Assert.Null(outcome.PatchOutcome);
        Assert.Null(outcome.AcceptedCandidate);
        Assert.Equal(original, File.ReadAllBytes(fixture.SourcePath));
    }

    [Fact]
    public async Task CompleteEvidenceRequirementKeepsSourceSearchInformational()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var request = WithCompleteEvidenceRequirement(fixture.RequestBytes);
        var exchange = new SearchThenSkipExchange("evidence.source", "public override void Run");
        var original = File.ReadAllBytes(fixture.SourcePath);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            request.Bytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("ProposalSkipped", outcome.Status);
        Assert.Equal(2, exchange.RequestCount);
        var completed = Assert.Single(exchange.Completed);
        Assert.Contains(
            completed.OutcomeId,
            new[]
            {
                DocumentationScribeToolOutcome.Complete.Id,
                DocumentationScribeToolOutcome.Incomplete.Id,
            });
        Assert.Empty(completed.EvidenceReferences);
        Assert.Null(outcome.PatchOutcome);
        Assert.Null(outcome.AcceptedCandidate);
        Assert.Equal(original, File.ReadAllBytes(fixture.SourcePath));
    }

    [Fact]
    public async Task AcceptedInstructionHasAnExactInformationalReadScope()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var contextReference = Assert.Single(fixture.Request.ContextReferences);
        var exchange = new ReadThenSkipExchange(
            contextReference.ContextReferenceId,
            contextReference.Path);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            exchange);

        Assert.True(
            outcome.Status == "ProposalSkipped",
            $"{outcome.Status}:{outcome.Code}");
        var completed = Assert.Single(exchange.Completed);
        Assert.Empty(completed.EvidenceReferences);
    }

    [Fact]
    public async Task ListIsNotAdvertisedWithoutAnAcceptedDirectoryRoute()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var contextReference = Assert.Single(fixture.Request.ContextReferences);
        var exchange = new ListThenSkipExchange(contextReference.ContextReferenceId);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("RuntimeFailure", outcome.Status);
        Assert.Equal("scribe.failure.tool-protocol", outcome.Code);
        Assert.Empty(exchange.Completed);
        Assert.Null(outcome.PatchOutcome);
    }

    [Fact]
    public async Task ZeroToolBudgetAllowsADirectTerminal()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var limited = WithLimit(fixture.RequestBytes, "maximumToolCalls", 0);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            limited.Bytes,
            fixture.AttemptId,
            new SkipExchange(limited.Request));

        Assert.Equal("ProposalSkipped", outcome.Status);
        Assert.Null(outcome.PatchOutcome);
    }

    [Fact]
    public async Task ZeroToolBudgetRejectsTheFirstAttemptedToolCall()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var limited = WithLimit(fixture.RequestBytes, "maximumToolCalls", 0);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            limited.Bytes,
            fixture.AttemptId,
            new ProposalExchange(limited.Request));

        Assert.Equal("BudgetExhausted", outcome.Status);
        Assert.Equal("scribe.failure.budget", outcome.Code);
        Assert.Null(outcome.PatchOutcome);
    }

    [Fact]
    public async Task InvalidTerminalOutputStopsBeforePatchComposition()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            new InvalidTerminalExchange());

        Assert.Equal("RuntimeFailure", outcome.Status);
        Assert.Equal("scribe.failure.validation", outcome.Code);
        Assert.Null(outcome.PatchOutcome);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task ProviderFailureIsDistinctAndCreatesNoPatchCandidate()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            new ProviderFailureExchange());

        Assert.Equal("ProviderFailure", outcome.Status);
        Assert.Equal("scribe.failure.provider", outcome.Code);
        Assert.Null(outcome.PatchOutcome);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task ProviderRequestBudgetStopsAfterTheLastAuthorizedToolRound()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var limited = WithLimit(fixture.RequestBytes, "maximumProviderRequests", 1);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            limited.Bytes,
            fixture.AttemptId,
            new ProposalExchange(limited.Request));

        Assert.Equal("BudgetExhausted", outcome.Status);
        Assert.Equal("scribe.failure.budget", outcome.Code);
        Assert.Null(outcome.PatchOutcome);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task IntrinsicRequestEvidenceBudgetStopsBeforeProviderInvocation()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var root = JsonNode.Parse(fixture.RequestBytes.Span)!.AsObject();
        var included = fixture.Request.EvidenceReferences
            .Sum(reference => reference.IncludedUtf8ByteCount);
        root["limits"]!["maximumEvidenceUtf8Bytes"] = included - 1;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        Assert.Null(DocumentationScribeValidation.ParseRequest(bytes).Request);
        var exchange = new CountingExchange();

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            bytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("PreflightRejected", outcome.Status);
        Assert.Equal("scribe.preflight.request-invalid", outcome.Code);
        Assert.Equal(0, exchange.RequestCount);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task DynamicEvidenceReferenceBudgetStopsBeforeASecondProviderRequest()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var limited = WithLimit(fixture.RequestBytes, "maximumEvidenceReferences", 1);
        var exchange = new ProposalExchange(limited.Request);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            limited.Bytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("BudgetExhausted", outcome.Status);
        Assert.Equal("scribe.failure.budget", outcome.Code);
        Assert.Equal(1, exchange.RequestCount);
        Assert.Null(outcome.PatchOutcome);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task SemanticHardReferenceBudgetMapsToTheGenericBudgetOutcome()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var limited = WithLimit(fixture.RequestBytes, "maximumEvidenceReferences", 1);
        var exchange = new SemanticOnlyExchange();

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            limited.Bytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("BudgetExhausted", outcome.Status);
        Assert.Equal("scribe.failure.budget", outcome.Code);
        Assert.Equal(1, exchange.RequestCount);
        Assert.Null(outcome.PatchOutcome);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task RepositoryHardFileBudgetMapsToTheGenericBudgetOutcome()
    {
        var source = "// " + new string('x', 270_000) + "\n" + """
            namespace EndToEnd;

            public class BaseFixture
            {
                public virtual void Run()
                {
                }
            }

            public sealed class Fixture : BaseFixture
            {
                public override void Run()
                {
                }
            }
            """;
        await using var fixture = await EndToEndFixture.CreateAsync(source);
        var exchange = new ProposalExchange(fixture.Request);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("BudgetExhausted", outcome.Status);
        Assert.Equal("scribe.failure.budget", outcome.Code);
        Assert.Equal(1, exchange.RequestCount);
        Assert.Null(outcome.PatchOutcome);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task DynamicEvidenceByteBudgetStopsBeforeASecondProviderRequest()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var initialEvidenceBytes = fixture.Request.EvidenceReferences
            .Sum(reference => reference.IncludedUtf8ByteCount);
        var limited = WithLimit(
            fixture.RequestBytes,
            "maximumEvidenceUtf8Bytes",
            initialEvidenceBytes);
        var exchange = new ProposalExchange(limited.Request);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            limited.Bytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("BudgetExhausted", outcome.Status);
        Assert.Equal("scribe.failure.budget", outcome.Code);
        Assert.Equal(1, exchange.RequestCount);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task SuccessfulExchangeByteBudgetIncludesSemanticResultsWithoutEvidencePromotion()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var initialEvidenceBytes = fixture.Request.EvidenceReferences
            .Sum(reference => reference.IncludedUtf8ByteCount);
        var limited = WithLimit(
            fixture.RequestBytes,
            "maximumEvidenceUtf8Bytes",
            initialEvidenceBytes);
        var exchange = new SemanticOnlyExchange();

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            limited.Bytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("BudgetExhausted", outcome.Status);
        Assert.Equal("scribe.failure.budget", outcome.Code);
        Assert.Equal(1, exchange.RequestCount);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task SemanticSoftIncompleteRemainsUsableAndPublishesNoDynamicEvidence()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var limited = WithLimit(fixture.RequestBytes, "maximumEvidenceReferences", 2);
        var exchange = new SemanticContinuationExchange();

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            limited.Bytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("ProposalSkipped", outcome.Status);
        var completed = Assert.Single(exchange.Completed);
        Assert.Equal(DocumentationScribeToolOutcome.Incomplete.Id, completed.OutcomeId);
        Assert.Empty(completed.EvidenceReferences);
        Assert.Null(outcome.PatchOutcome);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task ElapsedBudgetMapsToTimeoutWithoutCreatingAPatchCandidate()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var limited = WithLimit(fixture.RequestBytes, "maximumElapsedMilliseconds", 1);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            limited.Bytes,
            fixture.AttemptId,
            new DelayedExchange());

        Assert.Equal("Timeout", outcome.Status);
        Assert.Equal("scribe.failure.timeout", outcome.Code);
        Assert.Null(outcome.PatchOutcome);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task StaleSourceCommitmentStopsBeforeProviderInvocation()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        await File.AppendAllTextAsync(fixture.SourcePath, Environment.NewLine);
        var exchange = new CountingExchange();

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("PreflightRejected", outcome.Status);
        Assert.Equal(0, exchange.RequestCount);
        Assert.Null(outcome.PatchOutcome);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task SourceMutationAfterProposalReturnsPatchStaleWithoutCandidate()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var exchange = new MutatingProposalExchange(fixture.Request, fixture.SourcePath);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("PatchStale", outcome.Status);
        Assert.Null(outcome.PatchOutcome);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task WellFormedExistingDocumentationUsesTheM2ReplaceAuthorization()
    {
        const string source = """
            namespace EndToEnd;

            public class BaseFixture
            {
                public virtual void Run()
                {
                }
            }

            public sealed class Fixture : BaseFixture
            {
                /// <summary>Old documentation.</summary>
                public override void Run()
                {
                }
            }
            """;
        await using var fixture = await EndToEndFixture.CreateAsync(source);
        var original = File.ReadAllBytes(fixture.SourcePath);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            new ProposalExchange(fixture.Request));

        var block = Assert.Single(outcome.PatchRequest!.Blocks);
        Assert.Equal(DocumentationPatchEditKind.Replace, block.EditKind);
        Assert.Equal(original, File.ReadAllBytes(fixture.SourcePath));
    }

    [Fact]
    public async Task ParameterAndReturnComponentsProduceAValidM2Probe()
    {
        const string source = """
            namespace EndToEnd;

            public class BaseFixture
            {
                public virtual int Run(string value)
                {
                    return value.Length;
                }
            }

            public sealed class Fixture : BaseFixture
            {
                public override int Run(string value)
                {
                    return value.Length;
                }
            }
            """;
        await using var fixture = await EndToEndFixture.CreateAsync(source);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            new SkipExchange(fixture.Request));

        Assert.Equal("ProposalSkipped", outcome.Status);
        Assert.Contains(fixture.Request.Target.ApplicableComponents, item =>
            item.Kind == DocumentationPatchComponentKind.Parameter && item.Name == "value");
        Assert.Contains(fixture.Request.Target.ApplicableComponents, item =>
            item.Kind == DocumentationPatchComponentKind.Return && item.Name is null);
        Assert.Null(outcome.PatchOutcome);
    }

    [Fact]
    public async Task MismatchedSourceEvidenceScopeStopsBeforeProviderInvocation()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var root = JsonNode.Parse(fixture.RequestBytes.Span)!.AsObject();
        var evidence = root["evidenceReferences"]!.AsArray()[0]!.AsObject();
        var span = evidence["locator"]!["repository"]!["span"]!.AsObject();
        span["start"] = span["start"]!.GetValue<int>() + 1;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        Assert.NotNull(DocumentationScribeValidation.ParseRequest(bytes).Request);
        var exchange = new CountingExchange();

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            bytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("PreflightRejected", outcome.Status);
        Assert.Equal("scribe.preflight.source-evidence-mismatch", outcome.Code);
        Assert.Equal(0, exchange.RequestCount);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Fact]
    public async Task ProjectInstructionBytesCannotBeRelabeledAsAStyleExample()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var root = JsonNode.Parse(fixture.RequestBytes.Span)!.AsObject();
        var original = root["contextReferences"]!.AsArray()[0]!.AsObject();
        var alias = original.DeepClone().AsObject();
        alias["contextReferenceId"] = "zzzz.context.style-alias";
        alias["kind"] = "context.style-example";
        root["contextReferences"]!.AsArray().Add(alias);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        Assert.NotNull(DocumentationScribeValidation.ParseRequest(bytes).Request);
        var exchange = new CountingExchange();

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            bytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("PreflightRejected", outcome.Status);
        Assert.Equal("scribe.preflight.prompt-evidence-mismatch", outcome.Code);
        Assert.Equal(0, exchange.RequestCount);
    }

    [Theory]
    [InlineData(
        "context.repository-documentation",
        "RepositoryContext.md",
        "Repository context materialization marker.")]
    [InlineData(
        "context.style-example",
        "StyleExample.md",
        "Style example materialization marker.")]
    public async Task ConfinedFilesSupplyEveryNonInstructionContextKind(
        string kind,
        string repositoryPath,
        string marker)
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var baseline = CaptureRepositoryFiles(fixture.Root);
        var request = WithFileContextReference(
            fixture.RequestBytes,
            fixture.Root,
            repositoryPath,
            kind);
        var exchange = new CountingExchange();

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            request.Bytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("ProposalSkipped", outcome.Status);
        Assert.Equal(1, exchange.RequestCount);
        var maintained = Assert.Single(
            Assert.Single(exchange.Requests).Messages,
            message => message.Kind == DocumentationScribeMessageKind.MaintainedContext);
        Assert.Contains("zzzz.context.confined-file", maintained.Content, StringComparison.Ordinal);
        Assert.Contains(kind, maintained.Content, StringComparison.Ordinal);
        Assert.Contains(marker, maintained.Content, StringComparison.Ordinal);
        Assert.Null(outcome.AcceptedCandidate);
        var after = CaptureRepositoryFiles(fixture.Root);
        Assert.Equal(baseline.Keys, after.Keys);
        foreach (var path in baseline.Keys)
        {
            Assert.Equal(baseline[path], after[path]);
        }
    }

    [Theory]
    [InlineData("context.repository-documentation", "RepositoryContext.md")]
    [InlineData("context.style-example", "StyleExample.md")]
    public async Task CrLfNonInstructionContextIsAcceptedInItsRawFileCommitmentDomain(
        string kind,
        string repositoryPath)
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var path = Path.Join(fixture.Root, repositoryPath);
        var content = ToCrLf(await File.ReadAllTextAsync(path));
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
        var request = WithFileContextReference(
            fixture.RequestBytes,
            fixture.Root,
            repositoryPath,
            kind);
        var exchange = new CountingExchange();

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            request.Bytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("ProposalSkipped", outcome.Status);
        Assert.Equal(1, exchange.RequestCount);
    }

    [Fact]
    public async Task CrLfProjectInstructionIsAcceptedInItsRawFileCommitmentDomain()
    {
        const string instruction = "# Fixture instructions\r\n\r\nKeep documentation concise.\r\n";
        await using var fixture = await EndToEndFixture.CreateAsync(instructionOverride: instruction);
        var exchange = new CountingExchange();

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("ProposalSkipped", outcome.Status);
        Assert.Equal(1, exchange.RequestCount);
    }

    [Fact]
    public async Task CrLfSelectedSourceTraversesScribeAndM2WithoutChangingOriginal()
    {
        var source = ToCrLf(FreshProcessSource);
        await using var fixture = await EndToEndFixture.CreateAsync(source);
        var original = await File.ReadAllBytesAsync(fixture.SourcePath);
        var exchange = new ProposalExchange(fixture.Request);

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal(2, exchange.RequestCount);
        Assert.NotNull(outcome.PatchRequest);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.SourcePath));
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("PatchAccepted", outcome.Status);
        }
    }

    [Theory]
    [InlineData("path")]
    [InlineData("contentSha256")]
    [InlineData("originalUtf8ByteCount")]
    [InlineData("includedUtf8ByteCountAndTruncation")]
    public async Task NonInstructionContextRequiresAnExactCurrentFileCommitment(
        string substitution)
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var request = WithFileContextReference(
            fixture.RequestBytes,
            fixture.Root,
            "RepositoryContext.md",
            "context.repository-documentation");
        var root = JsonNode.Parse(request.Bytes.Span)!.AsObject();
        var reference = root["contextReferences"]!.AsArray()[^1]!.AsObject();
        switch (substitution)
        {
            case "path":
                reference["path"] = "StyleExample.md";
                break;
            case "contentSha256":
                reference["contentSha256"] = new string('b', 64);
                break;
            case "originalUtf8ByteCount":
                reference["originalUtf8ByteCount"] =
                    reference["originalUtf8ByteCount"]!.GetValue<int>() + 1;
                reference["isTruncated"] = true;
                break;
            case "includedUtf8ByteCountAndTruncation":
                reference["includedUtf8ByteCount"] =
                    reference["includedUtf8ByteCount"]!.GetValue<int>() - 1;
                reference["isTruncated"] = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(substitution));
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        Assert.NotNull(DocumentationScribeValidation.ParseRequest(bytes).Request);
        var exchange = new CountingExchange();

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            bytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("PreflightRejected", outcome.Status);
        Assert.Equal("scribe.preflight.prompt-evidence-mismatch", outcome.Code);
        Assert.Equal(0, exchange.RequestCount);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Theory]
    [InlineData("context.repository-documentation")]
    [InlineData("context.style-example")]
    public async Task SelectedSourceBytesCannotBeRelabeledAsNonInstructionContext(string kind)
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var request = WithSourceContextReference(fixture.RequestBytes, kind);
        var exchange = new CountingExchange();

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            request.Bytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("PreflightRejected", outcome.Status);
        Assert.Equal("scribe.preflight.prompt-evidence-mismatch", outcome.Code);
        Assert.Equal(0, exchange.RequestCount);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Theory]
    [InlineData("context.repository-documentation", "RepositoryContext.md")]
    [InlineData("context.style-example", "StyleExample.md")]
    public async Task NonInstructionContextScopeCannotEscapeItsExactFile(
        string kind,
        string repositoryPath)
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var request = WithFileContextReference(
            fixture.RequestBytes,
            fixture.Root,
            repositoryPath,
            kind);
        var exchange = new ReadThenSkipExchange(
            "zzzz.context.confined-file",
            "Fixture.cs");

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            request.Bytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("RuntimeFailure", outcome.Status);
        Assert.Equal("scribe.failure.tool-protocol", outcome.Code);
        Assert.Equal(1, exchange.RequestCount);
        Assert.Empty(exchange.Completed);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Theory]
    [InlineData("context.repository-documentation", "RepositoryContext.md")]
    [InlineData("context.style-example", "StyleExample.md")]
    public async Task NonInstructionContextSearchCannotSeeAnotherFile(
        string kind,
        string repositoryPath)
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var request = WithFileContextReference(
            fixture.RequestBytes,
            fixture.Root,
            repositoryPath,
            kind);
        var exchange = new SearchThenSkipExchange(
            "zzzz.context.confined-file",
            "public sealed class Fixture");

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            request.Bytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("ProposalSkipped", outcome.Status);
        Assert.Equal(2, exchange.RequestCount);
        var completed = Assert.Single(exchange.Completed);
        Assert.Equal(DocumentationScribeToolOutcome.Complete.Id, completed.OutcomeId);
        using var result = JsonDocument.Parse(completed.ResultUtf8Json);
        Assert.Empty(result.RootElement.GetProperty("items").EnumerateArray());
        Assert.Empty(completed.EvidenceReferences);
        Assert.Null(outcome.AcceptedCandidate);
    }

    [Theory]
    [InlineData(DocumentationScribeRepositoryToolOperationIds.ReadExcerpt)]
    [InlineData(DocumentationScribeRepositoryToolOperationIds.SearchText)]
    public async Task MaterializedContextAndRuntimeToolsShareOneRepositoryObservation(
        string operationId)
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        const string repositoryPath = "RepositoryContext.md";
        var path = Path.Join(fixture.Root, repositoryPath);
        var original = await File.ReadAllTextAsync(path);
        var request = WithFileContextReference(
            fixture.RequestBytes,
            fixture.Root,
            repositoryPath,
            "context.repository-documentation");
        var exchange = new MutateThenRepositoryToolExchange(
            path,
            original,
            operationId,
            "zzzz.context.confined-file");

        try
        {
            var outcome = await CliHarness.ExecuteAsync(
                fixture.SelectedAudit,
                request.Bytes,
                fixture.AttemptId,
                exchange);

            Assert.Equal("RuntimeFailure", outcome.Status);
            Assert.Equal("scribe.failure.tool-protocol", outcome.Code);
            Assert.Equal(1, exchange.RequestCount);
            Assert.Null(outcome.AcceptedCandidate);
            Assert.Null(outcome.PatchRequest);
        }
        finally
        {
            await File.WriteAllTextAsync(path, original, new UTF8Encoding(false));
        }
    }

    [Fact]
    public async Task SourceBytesCannotBeRelabeledAsPublicContractEvidence()
    {
        await using var fixture = await EndToEndFixture.CreateAsync();
        var root = JsonNode.Parse(fixture.RequestBytes.Span)!.AsObject();
        var alias = root["evidenceReferences"]!.AsArray()[0]!.DeepClone().AsObject();
        alias["evidenceReferenceId"] = "zzzz.evidence.public-contract-alias";
        alias["kind"] = "evidence.public-contract";
        alias["relation"] = "evidence.constrains";
        alias["authority"] = "authority.public-contract";
        root["evidenceReferences"]!.AsArray().Add(alias);
        root["styleProfile"]!["claimPolicies"]![0]!["allowedAuthorities"]!
            .AsArray().Add("authority.public-contract");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        Assert.NotNull(DocumentationScribeValidation.ParseRequest(bytes).Request);
        var exchange = new CountingExchange();

        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            bytes,
            fixture.AttemptId,
            exchange);

        Assert.Equal("PreflightRejected", outcome.Status);
        Assert.Equal("scribe.preflight.prompt-evidence-mismatch", outcome.Code);
        Assert.Equal(0, exchange.RequestCount);
    }

    [Fact]
    public async Task StableScribeAndPatchProjectionIsDeterministicAcrossFreshProcesses()
    {
        var childOutput = Environment.GetEnvironmentVariable(FreshProcessOutputVariable);
        if (!string.IsNullOrWhiteSpace(childOutput))
        {
            await using var fixture = await EndToEndFixture.CreateAsync(FreshProcessSource);
            var exchange = new ProposalExchange(fixture.Request);
            var outcome = await CliHarness.ExecuteAsync(
                fixture.SelectedAudit,
                fixture.RequestBytes,
                fixture.AttemptId,
                exchange);
            var patchRequest = outcome.PatchRequest;
            Assert.NotNull(patchRequest);
            if (OperatingSystem.IsLinux())
            {
                Assert.Equal("PatchAccepted", outcome.Status);
            }

            bool? repositoryCursorReuseRejected = null;
            bool? semanticCursorReuseRejected = null;
            var reuseInput = Environment.GetEnvironmentVariable(FreshProcessReuseVariable);
            if (!string.IsNullOrWhiteSpace(reuseInput))
            {
                using var prior = JsonDocument.Parse(await File.ReadAllBytesAsync(reuseInput));
                var priorEphemeral = prior.RootElement.GetProperty("Ephemeral");
                repositoryCursorReuseRejected = await CursorReuseRejected(
                    fixture,
                    DocumentationScribeRepositoryToolOperationIds.SearchText,
                    priorEphemeral.GetProperty("RepositoryCursor").GetString()!);
                semanticCursorReuseRejected = await CursorReuseRejected(
                    fixture,
                    DocumentationScribeSemanticToolSelection.OperationId,
                    priorEphemeral.GetProperty("SemanticCursor").GetString()!);
            }

            var cursorExchange = new CursorCaptureExchange();
            var cursorOutcome = await CliHarness.ExecuteAsync(
                fixture.SelectedAudit,
                fixture.RequestBytes,
                fixture.AttemptId,
                cursorExchange);
            Assert.Equal("ProposalSkipped", cursorOutcome.Status);
            var repositoryCursor = ResultCursor(
                cursorExchange.Completed,
                DocumentationScribeRepositoryToolOperationIds.SearchText);
            var semanticCursor = ResultCursor(
                cursorExchange.Completed,
                DocumentationScribeSemanticToolSelection.OperationId);
            Assert.NotNull(repositoryCursor);
            Assert.NotNull(semanticCursor);

            var stable = new
            {
                Target = fixture.Request.Target.SymbolRef,
                Tools = exchange.Completed.Select(item => new
                {
                    item.OperationId,
                    Arguments = JsonNode.Parse(item.ArgumentsUtf8Json.Span),
                    item.OutcomeId,
                    Result = Scrub(JsonNode.Parse(item.ResultUtf8Json.Span)),
                    Evidence = item.EvidenceReferences.Select(reference => Scrub(
                        JsonSerializer.SerializeToNode(reference))),
                }),
                Terminal = Scrub(JsonSerializer.SerializeToNode(outcome.RunResult!.Terminal)),
                Patch = patchRequest.Blocks.Select(block => new
                {
                    block.BlockId,
                    block.SymbolRef,
                    block.Locator,
                    block.EditKind,
                    block.ApplicableComponents,
                    block.Content,
                    block.ProvenanceRefs,
                }),
                Candidate = outcome.AcceptedCandidate?.Files
                    .OrderBy(file => file.RepositoryPath, StringComparer.Ordinal)
                    .Select(file => new
                    {
                        file.RepositoryPath,
                        file.Sha256,
                        Bytes = Convert.ToBase64String(file.Bytes.AsSpan()),
                    }),
                Validation = outcome.PatchOutcome?.Result is { } result
                    ? new
                    {
                        result.Outcome,
                        result.ChangedDocumentationBlockCount,
                        result.Targets,
                        result.ChangedFiles,
                        result.Invariants,
                        result.Diagnostics,
                    }
                    : null,
                outcome.Status,
                outcome.Code,
            };
            var ephemeral = new
            {
                RepositoryContextRef = fixture.Session.RepositoryContextRef.Value,
                RequestSha256 = fixture.Request.ArtifactSha256,
                AttemptId = fixture.AttemptId.Value,
                PatchRequestSha256 = patchRequest.ArtifactSha256,
                ResultPatchRequestSha256 = outcome.PatchOutcome?.Result?.PatchRequestSha256,
                RepositoryCursor = repositoryCursor,
                SemanticCursor = semanticCursor,
                RepositoryCursorReuseRejected = repositoryCursorReuseRejected,
                SemanticCursorReuseRejected = semanticCursorReuseRejected,
            };
            await File.WriteAllTextAsync(
                childOutput,
                JsonSerializer.Serialize(new { Stable = stable, Ephemeral = ephemeral }),
                new UTF8Encoding(false));
            return;
        }

        var first = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-issue-108-probe-" + Guid.NewGuid().ToString("N") + ".json");
        var second = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-issue-108-probe-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await RunFreshProcessProbeAsync(first, FindRepositoryRoot(), null);
            await RunFreshProcessProbeAsync(second, Path.Join(FindRepositoryRoot(), "tests"), first);
            using var firstDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(first));
            using var secondDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(second));
            Assert.Equal(
                firstDocument.RootElement.GetProperty("Stable").GetRawText(),
                secondDocument.RootElement.GetProperty("Stable").GetRawText());
            var firstEphemeral = firstDocument.RootElement.GetProperty("Ephemeral");
            var secondEphemeral = secondDocument.RootElement.GetProperty("Ephemeral");
            foreach (var name in new[]
                     {
                         "RepositoryContextRef",
                         "RequestSha256",
                         "AttemptId",
                         "PatchRequestSha256",
                         "ResultPatchRequestSha256",
                     })
            {
                var left = firstEphemeral.GetProperty(name).GetString();
                var right = secondEphemeral.GetProperty(name).GetString();
                if (left is not null && right is not null)
                {
                    Assert.NotEqual(left, right);
                }
            }

            Assert.NotEqual(
                firstEphemeral.GetProperty("RepositoryCursor").GetString(),
                secondEphemeral.GetProperty("RepositoryCursor").GetString());
            Assert.NotEqual(
                firstEphemeral.GetProperty("SemanticCursor").GetString(),
                secondEphemeral.GetProperty("SemanticCursor").GetString());
            Assert.True(secondEphemeral.GetProperty("RepositoryCursorReuseRejected").GetBoolean());
            Assert.True(secondEphemeral.GetProperty("SemanticCursorReuseRejected").GetBoolean());
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    private static JsonNode? Scrub(JsonNode? node)
    {
        if (node is JsonObject item)
        {
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "repositoryContextRef",
                "correlationIdentity",
                "cursor",
                "nextCursor",
                "artifactSha256",
                "patchRequestSha256",
            };
            foreach (var name in item.Select(property => property.Key)
                .Where(excluded.Contains)
                .ToArray())
            {
                item.Remove(name);
            }

            foreach (var child in item.ToArray())
            {
                Scrub(child.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                Scrub(child);
            }
        }

        return node;
    }

    private static IReadOnlyDictionary<string, byte[]> CaptureRepositoryFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);

    private static async Task<bool> CursorReuseRejected(
        EndToEndFixture fixture,
        string operationId,
        string cursor)
    {
        var outcome = await CliHarness.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            new CursorReuseExchange(operationId, cursor));
        return outcome.Status == "RuntimeFailure"
            && outcome.Code == "scribe.failure.tool-protocol"
            && outcome.PatchOutcome is null;
    }

    private static string? ResultCursor(
        ImmutableArray<DocumentationScribeCompletedToolExchange> completed,
        string operationId)
    {
        var exchange = Assert.Single(completed, item => item.OperationId == operationId);
        using var result = JsonDocument.Parse(exchange.ResultUtf8Json);
        if (operationId == DocumentationScribeRepositoryToolOperationIds.SearchText)
        {
            return result.RootElement.TryGetProperty("cursor", out var cursor)
                && cursor.ValueKind == JsonValueKind.String
                ? cursor.GetString()
                : null;
        }

        return result.RootElement.TryGetProperty("page", out var page)
            && page.TryGetProperty("nextCursor", out var nextCursor)
            && nextCursor.TryGetProperty("value", out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static async Task RunFreshProcessProbeAsync(
        string outputPath,
        string workingDirectory,
        string? reuseInput)
    {
        var root = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Cannot determine test configuration.");
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("test");
        start.ArgumentList.Add(Path.Join(
            root,
            "tests",
            "ContractScribe.IntegrationTests",
            "ContractScribe.IntegrationTests.csproj"));
        start.ArgumentList.Add("--configuration");
        start.ArgumentList.Add(configuration);
        start.ArgumentList.Add("--no-build");
        start.ArgumentList.Add("--no-restore");
        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add(
            "FullyQualifiedName=ContractScribe.IntegrationTests.DocumentationScribeEndToEndIntegrationTests.StableScribeAndPatchProjectionIsDeterministicAcrossFreshProcesses");
        start.Environment[FreshProcessOutputVariable] = outputPath;
        if (reuseInput is not null)
        {
            start.Environment[FreshProcessReuseVariable] = reuseInput;
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Fresh-process Scribe probe did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Fresh-process Scribe probe timed out.");
        }

        Assert.True(
            process.ExitCode == 0,
            $"Fresh-process Scribe probe failed.\nstdout:\n{await output}\nstderr:\n{await error}");
        Assert.True(File.Exists(outputPath), "Fresh-process Scribe probe did not publish output.");
    }

    private static (ReadOnlyMemory<byte> Bytes, DocumentationScribeRequest Request) WithLimit(
        ReadOnlyMemory<byte> requestBytes,
        string name,
        int value)
    {
        var root = JsonNode.Parse(requestBytes.Span)!.AsObject();
        root["limits"]![name] = value;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        var parsed = DocumentationScribeValidation.ParseRequest(bytes);
        return (bytes, Assert.IsType<DocumentationScribeRequest>(parsed.Request));
    }

    private static (ReadOnlyMemory<byte> Bytes, DocumentationScribeRequest Request)
        WithCompleteEvidenceRequirement(ReadOnlyMemory<byte> requestBytes)
    {
        var root = JsonNode.Parse(requestBytes.Span)!.AsObject();
        root["styleProfile"]!["claimPolicies"]![0]!["completeEvidenceRequired"] = true;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        var parsed = DocumentationScribeValidation.ParseRequest(bytes);
        return (bytes, Assert.IsType<DocumentationScribeRequest>(parsed.Request));
    }

    private static (ReadOnlyMemory<byte> Bytes, DocumentationScribeRequest Request)
        WithFileContextReference(
            ReadOnlyMemory<byte> requestBytes,
            string repositoryRoot,
            string repositoryPath,
            string kind)
    {
        var root = JsonNode.Parse(requestBytes.Span)!.AsObject();
        var fileBytes = File.ReadAllBytes(Path.Join(repositoryRoot, repositoryPath));
        root["contextReferences"]!.AsArray().Add(new JsonObject
        {
            ["contextReferenceId"] = "zzzz.context.confined-file",
            ["kind"] = kind,
            ["repositoryContextRef"] = root["context"]!["repositoryContextRef"]!.DeepClone(),
            ["path"] = repositoryPath,
            ["contentSha256"] = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant(),
            ["originalUtf8ByteCount"] = fileBytes.Length,
            ["includedUtf8ByteCount"] = fileBytes.Length,
            ["isTruncated"] = false,
        });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        var parsed = DocumentationScribeValidation.ParseRequest(bytes);
        Assert.True(parsed.IsValid, parsed.Failure?.Code + "|" + parsed.Failure?.Pointer);
        return (bytes, Assert.IsType<DocumentationScribeRequest>(parsed.Request));
    }

    private static (ReadOnlyMemory<byte> Bytes, DocumentationScribeRequest Request)
        WithSourceContextReference(ReadOnlyMemory<byte> requestBytes, string kind)
    {
        var root = JsonNode.Parse(requestBytes.Span)!.AsObject();
        var source = root["evidenceReferences"]!.AsArray()[0]!.AsObject();
        var locator = source["locator"]!["repository"]!.AsObject();
        root["contextReferences"]!.AsArray().Add(new JsonObject
        {
            ["contextReferenceId"] = "zzzz.context.source-relabel",
            ["kind"] = kind,
            ["repositoryContextRef"] = source["repositoryContextRef"]!.DeepClone(),
            ["path"] = locator["path"]!.DeepClone(),
            ["contentSha256"] = source["contentSha256"]!.DeepClone(),
            ["originalUtf8ByteCount"] = source["originalUtf8ByteCount"]!.DeepClone(),
            ["includedUtf8ByteCount"] = source["includedUtf8ByteCount"]!.DeepClone(),
            ["isTruncated"] = source["isTruncated"]!.DeepClone(),
        });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        var parsed = DocumentationScribeValidation.ParseRequest(bytes);
        Assert.True(parsed.IsValid, parsed.Failure?.Code + "|" + parsed.Failure?.Pointer);
        return (bytes, Assert.IsType<DocumentationScribeRequest>(parsed.Request));
    }

    private static string ToCrLf(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r\n", StringComparison.Ordinal);

    private static PolicyDocumentV1 ParsePolicy(string decision) =>
        PolicyConfigurationEvaluator.Parse(Encoding.UTF8.GetBytes(
            $"{{\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\",\"defaultDecision\":\"{decision}\"}}"))
            .Document ?? throw new InvalidOperationException("policy");

    private static (ReadOnlyMemory<byte> Bytes, DocumentationScribeRequest Request) WithAuditOutcome(
        ReadOnlyMemory<byte> requestBytes,
        string outcome)
    {
        var root = JsonNode.Parse(requestBytes.Span)!.AsObject();
        root["context"]!["auditOutcome"] = outcome;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        return (
            bytes,
            Assert.IsType<DocumentationScribeRequest>(
                DocumentationScribeValidation.ParseRequest(bytes).Request));
    }

    private sealed class EndToEndFixture : IAsyncDisposable
    {
        private EndToEndFixture(
            string root,
            string sourcePath,
            LoadedRepositorySession session,
            ClassifiedRepositorySession classified,
            ObservedRepositorySession observations,
            TargetClassification target,
            object selectedAudit,
            object nonMethodSelectedAudit,
            ReadOnlyMemory<byte> requestBytes,
            DocumentationScribeRequest request,
            DocumentationScribeAttemptId attemptId)
        {
            Root = root;
            SourcePath = sourcePath;
            Session = session;
            Classified = classified;
            Observations = observations;
            Target = target;
            SelectedAudit = selectedAudit;
            NonMethodSelectedAudit = nonMethodSelectedAudit;
            RequestBytes = requestBytes;
            Request = request;
            AttemptId = attemptId;
        }

        internal string Root { get; }

        internal string SourcePath { get; }

        internal LoadedRepositorySession Session { get; }

        internal ClassifiedRepositorySession Classified { get; }

        internal ObservedRepositorySession Observations { get; }

        internal TargetClassification Target { get; }

        internal object SelectedAudit { get; }

        internal object NonMethodSelectedAudit { get; }

        internal ReadOnlyMemory<byte> RequestBytes { get; }

        internal DocumentationScribeRequest Request { get; }

        internal DocumentationScribeAttemptId AttemptId { get; }

        internal object SelectAuditForPolicy(string decision)
        {
            var policy = PolicyConfigurationEvaluator.Parse(Encoding.UTF8.GetBytes(
                $"{{\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\",\"defaultDecision\":\"{decision}\"}}"))
                .Document ?? throw new InvalidOperationException("policy");
            var extracted = new PolicyEvidenceExtractor().Extract(
                Classified,
                Observations,
                policy);
            var inputs = AuditInputAssembler.Assemble(
                Classified.Classification.ClassificationSet!,
                policy,
                extracted);
            var audit = AuditAggregator.Aggregate(
                TargetProfile.ExternalApi,
                Classified.Classification.ClassificationSet!,
                policy,
                inputs);
            return CliHarness.SelectAudit(
                CliHarness.CreateAuditAuthority(
                    Classified,
                    Observations,
                    policy,
                    inputs,
                    audit),
                Target);
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }

        internal static async Task<EndToEndFixture> CreateAsync(
            string? sourceOverride = null,
            string? instructionOverride = null)
        {
            var deterministicProbe = !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(FreshProcessOutputVariable));
            var root = Path.Join(
                Path.GetTempPath(),
                deterministicProbe
                    ? "contract-scribe-issue-108-fresh-process"
                    : "contract-scribe-issue-108-" + Guid.NewGuid().ToString("N"));
            if (deterministicProbe && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            Directory.CreateDirectory(root);
            var fixtureRoot = Path.Join(
                FindRepositoryRoot(),
                "tests",
                "fixtures",
                "documentation-scribe",
                "end-to-end");
            foreach (var file in Directory.EnumerateFiles(fixtureRoot))
            {
                File.Copy(file, Path.Join(root, Path.GetFileName(file)));
            }

            if (sourceOverride is not null)
            {
                await File.WriteAllTextAsync(
                    Path.Join(root, "Fixture.cs"),
                    sourceOverride,
                    new UTF8Encoding(false));
            }

            if (instructionOverride is not null)
            {
                await File.WriteAllTextAsync(
                    Path.Join(root, "AGENTS.md"),
                    instructionOverride,
                    new UTF8Encoding(false));
            }

            await RestoreAsync(root);

            LoadedRepositorySession? session = null;
            try
            {
                var load = await new RepositoryLoader().LoadAsync(
                    new RepositoryLoadRequest(root, "Fixture.csproj"));
                Assert.True(
                    load.Status == RepositoryLoadStatus.Success,
                    $"{load.PrimaryFailure?.Stage}:{load.PrimaryFailure?.Code}");
                session = Assert.IsType<LoadedRepositorySession>(load.Session);
                var classified = new SymbolClassifier().ClassifySession(
                    session,
                    TargetProfile.ExternalApi);
                Assert.Equal(ClassificationRunStatus.Success, classified.Classification.Status);
                var target = Assert.Single(
                    classified.Classification.ClassificationSet!.Targets,
                    candidate => candidate.SymbolRef.DocumentationCommentId.StartsWith(
                            "M:EndToEnd.Fixture.Run",
                            StringComparison.Ordinal)
                        && candidate.SupportStatus == SupportStatus.Supported);
                var observed = new DocumentationObserver().Observe(classified);
                Assert.Equal(DocumentationObservationRunStatus.Success, observed.Status);

                var policy = PolicyConfigurationEvaluator.Parse(Encoding.UTF8.GetBytes(
                    "{\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\",\"defaultDecision\":\"required\"}"))
                    .Document ?? throw new InvalidOperationException("policy");
                var extracted = new PolicyEvidenceExtractor().Extract(classified, observed, policy);
                Assert.Equal(PolicyEvidenceExtractionStatus.Success, extracted.Status);
                var inputs = AuditInputAssembler.Assemble(
                    classified.Classification.ClassificationSet,
                    policy,
                    extracted);
                var audit = AuditAggregator.Aggregate(
                    TargetProfile.ExternalApi,
                    classified.Classification.ClassificationSet,
                    policy,
                    inputs);
                using var auditJson = JsonDocument.Parse(AuditJson.Write(audit));
                var selectedAuditOutcome = Assert.Single(
                    auditJson.RootElement.GetProperty("results").EnumerateArray(),
                    row => row.GetProperty("classification") is { } classification
                        && classification.TryGetProperty("symbolRef", out var symbolRef)
                        && symbolRef.GetProperty("documentationCommentId").GetString()
                            == target.SymbolRef.DocumentationCommentId)
                    .GetProperty("auditOutcome")
                    .GetString()!;

                var authority = CliHarness.CreateAuditAuthority(
                    classified,
                    observed,
                    policy,
                    inputs,
                    audit);
                var selected = CliHarness.SelectAudit(authority, target);
                var nonMethodTarget = Assert.Single(
                    classified.Classification.ClassificationSet.Targets,
                    candidate => candidate.SymbolRef.DocumentationCommentId
                        == "T:EndToEnd.BaseFixture");
                var nonMethodSelected = CliHarness.SelectAudit(authority, nonMethodTarget);
                var observation = Assert.Single(
                    observed.ObservationSet!.Observations,
                    item => item.Subject.ComponentKind is null
                        && item.Subject.ParentSymbolRef == target.SymbolRef);
                var declaration = Assert.Single(observation.Declarations);
                var repositorySource = Assert.IsType<RepositoryDocumentationSourceIdentity>(
                    declaration.Source);
                var project = Assert.Single(session.Projects, item =>
                    item.CompilationContextRef == target.SymbolRef.CompilationContextRef);
                var symbol = Assert.Single(Microsoft.CodeAnalysis.DocumentationCommentId
                    .GetSymbolsForDeclarationId(
                        target.SymbolRef.DocumentationCommentId,
                        project.Compilation));
                var syntaxReference = Assert.Single(symbol.DeclaringSyntaxReferences);
                var loadedSource = project.SourceTrees[syntaxReference.SyntaxTree];
                var sourceRepositoryPath = Assert.IsType<string>(loadedSource.RepositoryPath);
                var targetSpan = new Utf16Span(
                    syntaxReference.Span.Start,
                    syntaxReference.Span.End);
                var sourcePath = Path.Join(root, sourceRepositoryPath);
                var sourceSha256 = Sha256(File.ReadAllBytes(sourcePath));
                var selection = DocumentationScribeContextValidation.CreateBootstrapSelection(
                    session.RepositoryContextRef,
                    session.InputIdentity,
                    TargetProfile.ExternalApi,
                    target.SymbolRef,
                    sourceRepositoryPath,
                    targetSpan.Start,
                    targetSpan.End,
                    sourceSha256);
                var bootstrap = new DocumentationScribeContextBootstrapper().Bootstrap(
                    classified,
                    selection);
                Assert.True(
                    bootstrap.Status is DocumentationScribeContextBootstrapStatus.Succeeded
                        or DocumentationScribeContextBootstrapStatus.Incomplete,
                    $"{bootstrap.Status}:{bootstrap.Failure?.Category}:{bootstrap.Failure?.Code}");
                var context = Assert.IsType<DocumentationScribeLoadedContext>(bootstrap.Context);
                var requestBytes = CreateRequest(
                    session,
                    classified.Classification.ClassificationSet,
                    target,
                    symbol as Microsoft.CodeAnalysis.IMethodSymbol,
                    sourceRepositoryPath,
                    targetSpan,
                    sourceSha256,
                    context,
                    selectedAuditOutcome);
                var parsed = DocumentationScribeValidation.ParseRequest(requestBytes);
                var request = Assert.IsType<DocumentationScribeRequest>(parsed.Request);
                Assert.True(DocumentationScribeAttemptId.TryParse(
                    "scribe-attempt." + Guid.NewGuid().ToString("N"),
                    out var attempt));
                return new EndToEndFixture(
                    root,
                    sourcePath,
                    session,
                    classified,
                    observed,
                    target,
                    selected,
                    nonMethodSelected,
                    requestBytes,
                    request,
                    attempt);
            }
            catch
            {
                if (session is not null)
                {
                    await session.DisposeAsync();
                }

                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        private static ReadOnlyMemory<byte> CreateRequest(
            LoadedRepositorySession session,
            ClassificationSet classifications,
            TargetClassification target,
            Microsoft.CodeAnalysis.IMethodSymbol? method,
            string sourcePath,
            Utf16Span targetSpan,
            string sourceSha256,
            DocumentationScribeLoadedContext context,
            string auditOutcome)
        {
            var evidence = Assert.Single(context.Facts.Evidence, item =>
                item.KindId == "source.target-declaration");
            var components = classifications.Components
                .Where(item => item.ParentSymbolRef == target.SymbolRef
                    && item.SupportStatus == SupportStatus.Supported
                    && item.ComponentKind is ComponentKind.TypeParameter
                        or ComponentKind.Parameter
                        or ComponentKind.Return
                        or ComponentKind.Value)
                .OrderBy(item => item.ComponentKind switch
                {
                    ComponentKind.TypeParameter => 0,
                    ComponentKind.Parameter => 1,
                    ComponentKind.Return => 2,
                    ComponentKind.Value => 3,
                    _ => throw new InvalidOperationException(),
                })
                .ThenBy(item => item.Identity, StringComparer.Ordinal)
                .Select(item => new
                {
                    Kind = item.ComponentKind switch
                    {
                        ComponentKind.TypeParameter => "typeParameter",
                        ComponentKind.Parameter => "parameter",
                        ComponentKind.Return => "return",
                        ComponentKind.Value => "value",
                        _ => throw new InvalidOperationException(),
                    },
                    item.Identity,
                    Name = ComponentName(item, method),
                })
                .ToArray();
            var contextReferences = new JsonArray();
            foreach (var instruction in context.Facts.Instructions)
            {
                contextReferences.Add(new JsonObject
                {
                    ["contextReferenceId"] = instruction.InstructionId,
                    ["kind"] = "context.project-instruction",
                    ["repositoryContextRef"] = session.RepositoryContextRef.Value,
                    ["path"] = instruction.Commitment.RepositoryPath,
                    ["contentSha256"] = instruction.Commitment.ContentSha256,
                    ["originalUtf8ByteCount"] = instruction.Commitment.OriginalUtf8ByteCount,
                    ["includedUtf8ByteCount"] = instruction.Commitment.IncludedUtf8ByteCount,
                    ["isTruncated"] = instruction.Commitment.IsTruncated,
                });
            }

            var root = new JsonObject
            {
                ["scribeRequestVersion"] = 1,
                ["context"] = new JsonObject
                {
                    ["repositoryContextRef"] = session.RepositoryContextRef.Value,
                    ["inputIdentity"] = session.InputIdentity,
                    ["targetProfile"] = "profile.external-api",
                    ["auditOutcome"] = auditOutcome,
                },
                ["target"] = new JsonObject
                {
                    ["symbolRef"] = Symbol(target.SymbolRef),
                    ["sourceCommitment"] = new JsonObject
                    {
                        ["locator"] = RepositoryLocator(
                            sourcePath,
                            targetSpan),
                        ["contentSha256"] = sourceSha256,
                    },
                    ["applicableComponents"] = new JsonArray(components.Select(item =>
                    {
                        var component = new JsonObject
                        {
                            ["kind"] = item.Kind,
                            ["identity"] = item.Identity,
                        };
                        if (item.Name is { } name)
                        {
                            component["name"] = name;
                        }

                        return component;
                    }).ToArray<JsonNode?>()),
                },
                ["styleProfile"] = new JsonObject
                {
                    ["styleProfileId"] = "style.public-api.v1",
                    ["outputLanguageId"] = "language.en",
                    ["summary"] = Policy("required", 400),
                    ["remarks"] = Policy("forbidden", 400),
                    ["exceptions"] = Policy("forbidden", 400),
                    ["componentPolicies"] = new JsonArray(components.Select(item =>
                        (JsonNode?)new JsonObject
                        {
                            ["componentIdentity"] = item.Identity,
                            ["disposition"] = "required",
                            ["maximumScalars"] = 300,
                        }).ToArray()),
                    ["inheritDocDisposition"] = "forbidden",
                    ["allowedLiterals"] = new JsonArray(),
                    ["forbiddenLiterals"] = new JsonArray(),
                    ["claimPolicies"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["claimCategoryId"] = "claim.purpose",
                            ["completeEvidenceRequired"] = false,
                            ["allowedAuthorities"] = new JsonArray("authority.source-declaration"),
                        },
                    },
                    ["maximumContentUnits"] = 8,
                    ["maximumEvidenceRefsPerUnit"] = 4,
                },
                ["contextReferences"] = contextReferences,
                ["evidenceReferences"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["evidenceReferenceId"] = "evidence.source",
                        ["repositoryContextRef"] = session.RepositoryContextRef.Value,
                        ["subject"] = new JsonObject { ["symbolRef"] = Symbol(target.SymbolRef) },
                        ["kind"] = "evidence.source.declaration",
                        ["relation"] = "evidence.declares",
                        ["authority"] = "authority.source-declaration",
                        ["locator"] = RepositoryLocator(
                            sourcePath,
                            targetSpan),
                        ["contentSha256"] = evidence.Commitment.ContentSha256,
                        ["originalUtf8ByteCount"] = evidence.Commitment.OriginalUtf8ByteCount,
                        ["includedUtf8ByteCount"] = evidence.Commitment.IncludedUtf8ByteCount,
                        ["isTruncated"] = evidence.Commitment.IsTruncated,
                        ["claimCategoryIds"] = new JsonArray("claim.purpose"),
                    },
                },
                ["evidenceConflicts"] = new JsonArray(),
                ["toolPolicyId"] = "tool-policy.read-only.v1",
                ["limits"] = new JsonObject
                {
                    ["maximumAttempts"] = 2,
                    ["maximumContextReferences"] = 8,
                    ["maximumContextUtf8Bytes"] = 65536,
                    ["maximumEvidenceReferences"] = 32,
                    ["maximumEvidenceUtf8Bytes"] = 65536,
                    ["maximumProviderRequests"] = 8,
                    ["maximumToolRounds"] = 4,
                    ["maximumToolCalls"] = 16,
                    ["maximumInputTokens"] = 65536,
                    ["maximumUncachedInputTokens"] = 32768,
                    ["maximumOutputTokens"] = 8192,
                    ["maximumCostMicrounits"] = 5000000,
                    ["maximumElapsedMilliseconds"] = 120000,
                },
            };
            return JsonSerializer.SerializeToUtf8Bytes(root);
        }

        private static string? ComponentName(
            ComponentClassification component,
            Microsoft.CodeAnalysis.IMethodSymbol? method)
        {
            if (method is null || component.ComponentKind is ComponentKind.Return or ComponentKind.Value)
            {
                return null;
            }

            var separator = component.Identity.LastIndexOf('/');
            if (separator < 0 || !int.TryParse(component.Identity[(separator + 1)..], out var ordinal))
            {
                return null;
            }

            return component.ComponentKind == ComponentKind.TypeParameter
                ? method.TypeParameters[ordinal].Name
                : method.Parameters[ordinal].Name;
        }

        private static string Sha256(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static async Task RestoreAsync(string root)
        {
            await RunDotNetAsync(root, "restore", "Fixture.csproj", "-nodeReuse:false");
            await RunDotNetAsync(
                root,
                "build",
                "Fixture.csproj",
                "--no-restore",
                "-nodeReuse:false");
        }

        private static async Task RunDotNetAsync(string root, params string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Fixture restore did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(
                process.ExitCode == 0,
                (await output) + Environment.NewLine + (await error));
        }

        private static JsonObject Policy(string disposition, int maximumScalars) => new()
        {
            ["disposition"] = disposition,
            ["maximumScalars"] = maximumScalars,
        };

        private static JsonObject Symbol(SymbolRef symbol) => new()
        {
            ["compilationContextRef"] = symbol.CompilationContextRef,
            ["documentationCommentId"] = symbol.DocumentationCommentId,
        };

        private static JsonObject RepositoryLocator(string path, Utf16Span span) => new()
        {
            ["repository"] = new JsonObject
            {
                ["path"] = path,
                ["span"] = new JsonObject
                {
                    ["start"] = span.Start,
                    ["end"] = span.End,
                },
            },
        };
    }

    private sealed class ProposalExchange : IDocumentationScribeModelExchange
    {
        private readonly DocumentationScribeRequest request;

        internal ProposalExchange(DocumentationScribeRequest request) => this.request = request;

        internal int RequestCount { get; private set; }

        internal int CompletedToolCount { get; private set; }

        internal ImmutableArray<string> CompletedOutcomes { get; private set; } = [];

        internal ImmutableArray<DocumentationScribeCompletedToolExchange> Completed { get; private set; } = [];

        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest modelRequest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            if (modelRequest.ProviderRequestNumber == 1)
            {
                return ValueTask.FromResult(new DocumentationScribeModelResponse(
                    [
                        new DocumentationScribeModelToolCall(
                            0,
                            "call.repository",
                            DocumentationScribeRepositoryToolOperationIds.SearchText,
                            JsonSerializer.SerializeToUtf8Bytes(new
                            {
                                scopeId = "evidence.source",
                                literal = "public override void Run",
                                pageSize = 1,
                            })),
                        new DocumentationScribeModelToolCall(
                            1,
                            "call.semantic",
                            DocumentationScribeSemanticToolSelection.OperationId,
                            JsonSerializer.SerializeToUtf8Bytes(new { pageSize = 1 })),
                    ],
                    []));
            }

            CompletedToolCount = modelRequest.CompletedToolExchanges.Length;
            Completed = modelRequest.CompletedToolExchanges;
            CompletedOutcomes = modelRequest.CompletedToolExchanges
                .Select(item => item.OutcomeId)
                .ToImmutableArray();
            return ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(ProposalTerminal(request))]));
        }
    }

    private sealed class ReadThenSkipExchange : IDocumentationScribeModelExchange
    {
        private readonly string scopeId;
        private readonly string? repositoryPath;

        internal ReadThenSkipExchange(string scopeId, string? repositoryPath = null)
        {
            this.scopeId = scopeId;
            this.repositoryPath = repositoryPath;
        }

        internal ImmutableArray<DocumentationScribeCompletedToolExchange> Completed { get; private set; } = [];

        internal int RequestCount { get; private set; }

        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.ProviderRequestNumber == 1)
            {
                var arguments = new JsonObject
                {
                    ["scopeId"] = scopeId,
                    ["startLine"] = 1,
                    ["endLine"] = 1,
                };
                if (repositoryPath is not null)
                {
                    arguments["repositoryPath"] = repositoryPath;
                }

                return ValueTask.FromResult(new DocumentationScribeModelResponse(
                    [
                        new DocumentationScribeModelToolCall(
                            0,
                            "call.read-informational",
                            DocumentationScribeRepositoryToolOperationIds.ReadExcerpt,
                            JsonSerializer.SerializeToUtf8Bytes(arguments)),
                    ],
                    []));
            }

            Completed = request.CompletedToolExchanges;
            return ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]));
        }
    }

    private sealed class SearchThenSkipExchange : IDocumentationScribeModelExchange
    {
        private readonly string scopeId;
        private readonly string literal;

        internal SearchThenSkipExchange(string scopeId, string literal)
        {
            this.scopeId = scopeId;
            this.literal = literal;
        }

        internal ImmutableArray<DocumentationScribeCompletedToolExchange> Completed { get; private set; } = [];

        internal int RequestCount { get; private set; }

        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.ProviderRequestNumber == 1)
            {
                return ValueTask.FromResult(new DocumentationScribeModelResponse(
                    [
                        new DocumentationScribeModelToolCall(
                            0,
                            "call.search-informational",
                            DocumentationScribeRepositoryToolOperationIds.SearchText,
                            JsonSerializer.SerializeToUtf8Bytes(new
                            {
                                scopeId,
                                literal,
                                pageSize = 1,
                            })),
                    ],
                    []));
            }

            Completed = request.CompletedToolExchanges;
            return ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]));
        }
    }

    private sealed class MutateThenRepositoryToolExchange : IDocumentationScribeModelExchange
    {
        private readonly string path;
        private readonly string original;
        private readonly string operationId;
        private readonly string scopeId;

        internal MutateThenRepositoryToolExchange(
            string path,
            string original,
            string operationId,
            string scopeId)
        {
            this.path = path;
            this.original = original;
            this.operationId = operationId;
            this.scopeId = scopeId;
        }

        internal int RequestCount { get; private set; }

        public async ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.ProviderRequestNumber == 1)
            {
                await File.WriteAllTextAsync(
                    path,
                    original + "changed after prompt materialization\n",
                    new UTF8Encoding(false),
                    cancellationToken);
                var arguments = operationId == DocumentationScribeRepositoryToolOperationIds.ReadExcerpt
                    ? JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        scopeId,
                        startLine = 1,
                        endLine = 1,
                    })
                    : JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        scopeId,
                        literal = "changed after prompt materialization",
                        pageSize = 1,
                    });
                return new DocumentationScribeModelResponse(
                    [new DocumentationScribeModelToolCall(0, "call.context-after-mutation", operationId, arguments)],
                    []);
            }

            await File.WriteAllTextAsync(path, original, new UTF8Encoding(false), cancellationToken);
            return new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]);
        }
    }

    private sealed class ListThenSkipExchange : IDocumentationScribeModelExchange
    {
        private readonly string scopeId;

        internal ListThenSkipExchange(string scopeId) => this.scopeId = scopeId;

        internal ImmutableArray<DocumentationScribeCompletedToolExchange> Completed { get; private set; } = [];

        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            if (request.ProviderRequestNumber == 1)
            {
                return ValueTask.FromResult(new DocumentationScribeModelResponse(
                    [
                        new DocumentationScribeModelToolCall(
                            0,
                            "call.list-context",
                            DocumentationScribeRepositoryToolOperationIds.ListFiles,
                            JsonSerializer.SerializeToUtf8Bytes(new { scopeId })),
                    ],
                    []));
            }

            Completed = request.CompletedToolExchanges;
            return ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]));
        }
    }

    private sealed class SkipExchange : IDocumentationScribeModelExchange
    {
        private readonly DocumentationScribeRequest request;

        internal SkipExchange(DocumentationScribeRequest request) => this.request = request;

        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest modelRequest,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]));
    }

    private sealed class MutatingProposalExchange : IDocumentationScribeModelExchange
    {
        private readonly DocumentationScribeRequest request;
        private readonly string sourcePath;

        internal MutatingProposalExchange(DocumentationScribeRequest request, string sourcePath)
        {
            this.request = request;
            this.sourcePath = sourcePath;
        }

        public async ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest modelRequest,
            CancellationToken cancellationToken)
        {
            if (modelRequest.ProviderRequestNumber == 1)
            {
                return new DocumentationScribeModelResponse(
                    [
                        new DocumentationScribeModelToolCall(
                            0,
                            "call.semantic",
                            DocumentationScribeSemanticToolSelection.OperationId,
                            JsonSerializer.SerializeToUtf8Bytes(new { pageSize = 1 })),
                    ],
                    []);
            }

            await File.AppendAllTextAsync(sourcePath, Environment.NewLine, cancellationToken);
            return new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(ProposalTerminal(request))]);
        }
    }

    private sealed class CountingExchange : IDocumentationScribeModelExchange
    {
        internal int RequestCount { get; private set; }

        internal List<DocumentationScribeModelRequest> Requests { get; } = [];

        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Requests.Add(request);
            return ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]));
        }
    }

    private sealed class InvalidToolExchange : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DocumentationScribeModelResponse(
                [
                    new DocumentationScribeModelToolCall(
                        0,
                        "call.invalid",
                        DocumentationScribeRepositoryToolOperationIds.ReadExcerpt,
                        Encoding.UTF8.GetBytes(
                            "{\"scopeId\":\"evidence.source\",\"scopeId\":\"evidence.source\"}")),
                ],
                []));
    }

    private sealed class SemanticOnlyExchange : IDocumentationScribeModelExchange
    {
        internal int RequestCount { get; private set; }

        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return ValueTask.FromResult(new DocumentationScribeModelResponse(
                [
                    new DocumentationScribeModelToolCall(
                        0,
                        "call.semantic",
                        DocumentationScribeSemanticToolSelection.OperationId,
                        JsonSerializer.SerializeToUtf8Bytes(new { pageSize = 1 })),
                ],
                []));
        }
    }

    private sealed class SemanticContinuationExchange : IDocumentationScribeModelExchange
    {
        internal ImmutableArray<DocumentationScribeCompletedToolExchange> Completed { get; private set; } = [];

        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            if (request.ProviderRequestNumber == 1)
            {
                return ValueTask.FromResult(new DocumentationScribeModelResponse(
                    [
                        new DocumentationScribeModelToolCall(
                            0,
                            "call.semantic",
                            DocumentationScribeSemanticToolSelection.OperationId,
                            JsonSerializer.SerializeToUtf8Bytes(new { pageSize = 1 })),
                    ],
                    []));
            }

            Completed = request.CompletedToolExchanges;
            return ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]));
        }
    }

    private sealed class CursorCaptureExchange : IDocumentationScribeModelExchange
    {
        internal ImmutableArray<DocumentationScribeCompletedToolExchange> Completed { get; private set; } = [];

        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.ProviderRequestNumber == 1)
            {
                return ValueTask.FromResult(new DocumentationScribeModelResponse(
                    [
                        new DocumentationScribeModelToolCall(
                            0,
                            "call.repository-cursor",
                            DocumentationScribeRepositoryToolOperationIds.SearchText,
                            JsonSerializer.SerializeToUtf8Bytes(new
                            {
                                scopeId = "evidence.source",
                                literal = " ",
                                pageSize = 1,
                            })),
                        new DocumentationScribeModelToolCall(
                            1,
                            "call.semantic-cursor",
                            DocumentationScribeSemanticToolSelection.OperationId,
                            JsonSerializer.SerializeToUtf8Bytes(new { pageSize = 1 })),
                    ],
                    []));
            }

            Completed = request.CompletedToolExchanges;
            return ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]));
        }
    }

    private sealed class CursorReuseExchange : IDocumentationScribeModelExchange
    {
        private readonly string operationId;
        private readonly string cursor;

        internal CursorReuseExchange(string operationId, string cursor)
        {
            this.operationId = operationId;
            this.cursor = cursor;
        }

        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.ProviderRequestNumber == 1)
            {
                var arguments = operationId == DocumentationScribeRepositoryToolOperationIds.SearchText
                    ? JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        scopeId = "evidence.source",
                        literal = " ",
                        pageSize = 1,
                        cursor,
                    })
                    : JsonSerializer.SerializeToUtf8Bytes(new { pageSize = 1, cursor });
                return ValueTask.FromResult(new DocumentationScribeModelResponse(
                    [
                        new DocumentationScribeModelToolCall(
                            0,
                            "call.cursor-reuse",
                            operationId,
                            arguments),
                    ],
                    []));
            }

            return ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]));
        }
    }

    private sealed class DelayedExchange : IDocumentationScribeModelExchange
    {
        public async ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            return new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]);
        }
    }

    private sealed class InvalidTerminalExchange : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [
                    new DocumentationScribeModelTerminalSubmission(Encoding.UTF8.GetBytes(
                        "{\"kind\":\"skip\",\"reason\":\"scribe.skip.insufficient-evidence\",\"evidenceReferenceIds\":[],\"sourceBytes\":\"forbidden\"}")),
                ]));
    }

    private sealed class ProviderFailureExchange : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [],
                new DocumentationScribeModelFailure(
                    DocumentationScribeModelFailureCode.PermanentUnavailable)));
    }

    private static ReadOnlyMemory<byte> ProposalTerminal(DocumentationScribeRequest request)
    {
        var locator = (RepositoryEvidenceLocator)request.Target.SourceLocator;
        return JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["kind"] = "proposal",
            ["target"] = new JsonObject
            {
                ["repositoryContextRef"] = request.Context.RepositoryContextRef.Value,
                ["symbolRef"] = new JsonObject
                {
                    ["compilationContextRef"] = request.Target.SymbolRef.CompilationContextRef,
                    ["documentationCommentId"] = request.Target.SymbolRef.DocumentationCommentId,
                },
                ["sourceCommitment"] = new JsonObject
                {
                    ["locator"] = new JsonObject
                    {
                        ["repository"] = new JsonObject
                        {
                            ["path"] = locator.Path,
                            ["span"] = new JsonObject
                            {
                                ["start"] = locator.Span!.Value.Start,
                                ["end"] = locator.Span.Value.End,
                            },
                        },
                    },
                    ["contentSha256"] = request.Target.SourceSha256,
                },
            },
            ["contentUnits"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "content.summary",
                    ["lines"] = new JsonArray("Runs the selected operation."),
                    ["claimCategoryId"] = "claim.purpose",
                    ["evidenceReferenceIds"] = new JsonArray("evidence.source"),
                },
            },
        });
    }

    private static ReadOnlyMemory<byte> SkipTerminal() =>
        Encoding.UTF8.GetBytes(
            "{\"kind\":\"skip\",\"reason\":\"scribe.skip.insufficient-evidence\",\"evidenceReferenceIds\":[]}");

    private sealed record CliOutcome(
        string Status,
        string Code,
        DocumentationScribeRunResult? RunResult,
        DocumentationPatchRequest? PatchRequest,
        DocumentationPatchExecutionOutcome? PatchOutcome,
        DocumentationPatchAcceptedCandidate? AcceptedCandidate)
    {
        public override string ToString() => nameof(CliOutcome);
    }

    private static class CliHarness
    {
        private static readonly Assembly CliAssembly = LoadCliAssembly();

        internal static object CreateAuditAuthority(
            ClassifiedRepositorySession session,
            ObservedRepositorySession observations,
            PolicyDocumentV1 policy,
            IEnumerable<AuditRecordInput> inputs,
            AuditDocument audit)
        {
            var type = RequireType("ContractScribe.Cli.DocumentationScribeAuditAuthority");
            var method = RequireMethod(type, "Create", BindingFlags.Static | BindingFlags.NonPublic);
            return method.Invoke(null, [session, observations, policy, inputs, audit])
                ?? throw new InvalidOperationException("CLI audit authority returned null.");
        }

        internal static object SelectAudit(object authority, TargetClassification target)
        {
            var method = RequireMethod(
                authority.GetType(),
                "Select",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return method.Invoke(authority, [target])
                ?? throw new InvalidOperationException("CLI selected audit returned null.");
        }

        internal static async Task<CliOutcome> ExecuteAsync(
            object selectedAudit,
            ReadOnlyMemory<byte> requestBytes,
            DocumentationScribeAttemptId attemptId,
            IDocumentationScribeModelExchange exchange,
            CancellationToken cancellationToken = default)
        {
            var type = RequireType("ContractScribe.Cli.DocumentationScribeComposition");
            var method = RequireMethod(type, "ExecuteAsync", BindingFlags.Static | BindingFlags.NonPublic);
            var invoked = method.Invoke(null,
            [
                selectedAudit,
                requestBytes,
                attemptId,
                null,
                new DocumentationScribeRuntimeOptions(
                    "provider.synthetic.v1",
                    "model.synthetic.v1",
                    "scribe-protocol.v1"),
                exchange,
                cancellationToken,
            ]) ?? throw new InvalidOperationException("CLI composition returned null.");
            var task = Assert.IsAssignableFrom<Task>(invoked);
            await task;
            var result = task.GetType().GetProperty("Result")?.GetValue(task)
                ?? throw new InvalidOperationException("CLI composition task has no result.");
            var status = Property(result, "Status")!.ToString()!;
            var code = Assert.IsType<string>(Property(result, "Code"));
            var runResult = Property(result, "RunResult") as DocumentationScribeRunResult;
            var patchRequest = Property(result, "PatchRequest") as DocumentationPatchRequest;
            var patchOutcome = Property(result, "PatchOutcome") as DocumentationPatchExecutionOutcome;
            var acceptedCandidate = Property(result, "AcceptedCandidate") as DocumentationPatchAcceptedCandidate;
            return new CliOutcome(
                status,
                code,
                runResult,
                patchRequest,
                patchOutcome,
                acceptedCandidate);
        }

        private static object? Property(object instance, string name) =>
            instance.GetType().GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);

        private static Type RequireType(string name) =>
            CliAssembly.GetType(name, throwOnError: true, ignoreCase: false)!;

        private static MethodInfo RequireMethod(Type type, string name, BindingFlags flags) =>
            type.GetMethod(name, flags)
                ?? throw new MissingMethodException(type.FullName, name);

        private static Assembly LoadCliAssembly()
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                ?? throw new InvalidOperationException("Cannot determine test configuration.");
            var path = Path.Join(
                FindRepositoryRoot(),
                "src",
                "ContractScribe.Cli",
                "bin",
                configuration,
                "net10.0",
                "ContractScribe.Cli.dll");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Configuration-matched CLI assembly is missing.", path);
            }

            return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "ContractScribe.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
