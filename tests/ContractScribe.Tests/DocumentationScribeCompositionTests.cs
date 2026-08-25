using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Patching;

namespace ContractScribe.Tests;

public sealed class DocumentationScribeCompositionTests
{
    private const string AttemptId = "scribe-attempt.0123456789abcdef0123456789abcdef";

    [Fact]
    public void Prepared_outcome_retains_bound_m3_while_withholding_m2_authority()
    {
        var proposal = Bind("proposal-result.json");
        foreach (var status in new[]
                 {
                     DocumentationScribeCompositionStatus.ProposalRejected,
                     DocumentationScribeCompositionStatus.PatchStale,
                     DocumentationScribeCompositionStatus.Cancelled,
                     DocumentationScribeCompositionStatus.Timeout,
                     DocumentationScribeCompositionStatus.BudgetExhausted,
                     DocumentationScribeCompositionStatus.RuntimeFailure,
                 })
        {
            var prepared = DocumentationScribePreparedOutcome.Create(status, "scribe.test.closed", proposal);
            Assert.Same(proposal, prepared.M3Outcome);
            Assert.Same(proposal.Request, prepared.M3Outcome!.Request);
            Assert.Same(proposal.RunResult, prepared.M3Outcome.RunResult);
            Assert.Null(prepared.PatchAuthorization);
            Assert.False(prepared.IsProposalReady);
        }

        foreach (var pair in new[]
                 {
                     ("skip-result.json", DocumentationScribeCompositionStatus.ProposalSkipped),
                     ("failure-result.json", DocumentationScribeCompositionStatus.ProviderFailure),
                     ("retryable-failure-result.json", DocumentationScribeCompositionStatus.ProviderFailure),
                     ("cancelled-result.json", DocumentationScribeCompositionStatus.Cancelled),
                 })
        {
            var bound = Bind(pair.Item1);
            var prepared = DocumentationScribePreparedOutcome.Create(pair.Item2, "scribe.test.closed", bound);
            Assert.Same(bound, prepared.M3Outcome);
            Assert.Null(prepared.PatchAuthorization);
            Assert.False(prepared.IsProposalReady);
        }
    }

    [Fact]
    public void Proposal_ready_is_the_only_prepared_shape_with_m2_authority()
    {
        var proposal = Bind("proposal-result.json");
        var patchBytes = File.ReadAllBytes(Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "fixtures",
            "documentation-patch",
            "v1",
            "valid",
            "repository-request.json"));
        var patchRequest = Assert.IsType<DocumentationPatchRequest>(
            DocumentationPatchValidator.ParseRequest(patchBytes).Request);

        var ready = DocumentationScribePreparedOutcome.Create(
            DocumentationScribeCompositionStatus.ProposalReady,
            "scribe.proposal.ready",
            proposal,
            patchRequest);
        Assert.True(ready.IsProposalReady);
        Assert.Same(proposal, ready.M3Outcome);
        Assert.Same(patchRequest, ready.PatchAuthorization);

        Assert.Throws<ArgumentException>(() => DocumentationScribePreparedOutcome.Create(
            DocumentationScribeCompositionStatus.ProposalReady,
            "scribe.proposal.ready",
            proposal));
        Assert.Throws<ArgumentException>(() => DocumentationScribePreparedOutcome.Create(
            DocumentationScribeCompositionStatus.ProposalRejected,
            "scribe.proposal.rejected",
            proposal,
            patchRequest));
        Assert.Throws<ArgumentException>(() => DocumentationScribePreparedOutcome.Create(
            DocumentationScribeCompositionStatus.ProposalReady,
            "scribe.proposal.ready",
            Bind("skip-result.json"),
            patchRequest));
    }

    [Fact]
    public void Pre_agent_failures_have_no_m3_and_prepared_authority_is_immutable()
    {
        foreach (var status in new[]
                 {
                     DocumentationScribeCompositionStatus.PreflightRejected,
                     DocumentationScribeCompositionStatus.Cancelled,
                     DocumentationScribeCompositionStatus.Timeout,
                     DocumentationScribeCompositionStatus.BudgetExhausted,
                     DocumentationScribeCompositionStatus.RuntimeFailure,
                 })
        {
            var prepared = DocumentationScribePreparedOutcome.Create(status, "scribe.test.closed");
            Assert.Null(prepared.M3Outcome);
            Assert.Null(prepared.PatchAuthorization);
        }

        Assert.True(typeof(DocumentationScribePreparedOutcome).IsSealed);
        Assert.DoesNotContain(
            typeof(DocumentationScribePreparedOutcome).GetProperties(),
            property => property.SetMethod is not null);
    }

    private static DocumentationScribeValidatedRunOutcome Bind(string fixture)
    {
        var root = FindRepositoryRoot();
        var requestBytes = File.ReadAllBytes(Path.Combine(
            root,
            "tests",
            "fixtures",
            "documentation-scribe",
            "v1",
            "valid",
            "request.json"));
        var request = Assert.IsType<DocumentationScribeRequest>(
            DocumentationScribeValidation.ParseRequest(requestBytes).Request);
        Assert.True(DocumentationScribeAttemptId.TryParse(AttemptId, out var attempt));
        var resultBytes = File.ReadAllBytes(Path.Combine(
            root,
            "tests",
            "fixtures",
            "documentation-scribe",
            "v1",
            "valid",
            fixture));
        var result = Assert.IsType<DocumentationScribeRunResult>(
            DocumentationScribeValidation.ParseRunResult(request, attempt, [], resultBytes).Result);
        return DocumentationScribeValidation.BindValidatedRunOutcome(request, attempt, result);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
