using System.Collections.Immutable;
using System.Security.Cryptography;
using ContractScribe.Core;
using ContractScribe.Patching.CandidateWorkspace;
using ContractScribe.Patching.Resolution;
using ContractScribe.Patching.Validation;
using ContractScribe.Roslyn;
using CandidateIdentity = ContractScribe.Patching.CandidateWorkspace.DocumentationPatchCandidateFileSystem.CandidatePhysicalIdentity;

namespace ContractScribe.Patching;

public enum DocumentationPatchExecutionStatus
{
    Result,
    HostFailure,
}

public sealed record DocumentationPatchAcceptedCandidateFile
{
    internal DocumentationPatchAcceptedCandidateFile(
        string repositoryPath,
        ImmutableArray<byte> bytes,
        string sha256)
    {
        RepositoryPath = repositoryPath;
        Bytes = bytes;
        Sha256 = sha256;
    }

    public string RepositoryPath { get; }

    public ImmutableArray<byte> Bytes { get; }

    public string Sha256 { get; }
}

public sealed class DocumentationPatchAcceptedCandidate
{
    internal DocumentationPatchAcceptedCandidate(
        DocumentationPatchRequest request,
        DocumentationPatchRepositoryBaseline baseline,
        DocumentationPatchValidationResult result,
        ImmutableArray<DocumentationPatchAcceptedCandidateFile> files,
        ImmutableArray<DocumentationPatchAcceptedCandidateIdentityEvidence> identityEvidence)
    {
        Request = request;
        Baseline = baseline;
        Result = result;
        Files = files;
        IdentityEvidence = identityEvidence;
    }

    public DocumentationPatchValidationResult Result { get; }

    public ImmutableArray<DocumentationPatchAcceptedCandidateFile> Files { get; }

    internal DocumentationPatchRequest Request { get; }

    internal DocumentationPatchRepositoryBaseline Baseline { get; }

    internal ImmutableArray<DocumentationPatchAcceptedCandidateIdentityEvidence> IdentityEvidence
    {
        get;
    }
}

public sealed record DocumentationPatchExecutionOutcome
{
    internal DocumentationPatchExecutionOutcome(
        DocumentationPatchExecutionStatus status,
        DocumentationPatchValidationResult? result,
        DocumentationPatchAcceptedCandidate? acceptedCandidate,
        string? failureCode)
    {
        Status = status;
        Result = result;
        AcceptedCandidate = acceptedCandidate;
        FailureCode = failureCode;
    }

    public DocumentationPatchExecutionStatus Status { get; }

    public DocumentationPatchValidationResult? Result { get; }

    public DocumentationPatchAcceptedCandidate? AcceptedCandidate { get; }

    public string? FailureCode { get; }
}

internal enum DocumentationPatchEngineStage
{
    BeforeCandidateTerminalPass,
    BeforeFinalOriginalRebind,
}

public sealed class DocumentationPatchEngine
{
    private readonly Func<string>? stagingParentFactory;
    private readonly Action<DocumentationPatchApplicationStage, string?>? applicationObserver;
    private readonly Action<DocumentationPatchEngineStage>? observer;

    public DocumentationPatchEngine()
    {
    }

    internal DocumentationPatchEngine(
        Func<string>? stagingParentFactory,
        Action<DocumentationPatchApplicationStage, string?>? applicationObserver,
        Action<DocumentationPatchEngineStage>? observer)
    {
        this.stagingParentFactory = stagingParentFactory;
        this.applicationObserver = applicationObserver;
        this.observer = observer;
    }

    public DocumentationPatchExecutionOutcome Execute(
        ClassifiedRepositorySession session,
        DocumentationPatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        DocumentationPatchApplicationStage? lastStage = null;
        try
        {
            var resolver = new DocumentationPatchResolver();
            var application = new CandidatePatchApplicator(
                resolver,
                (stage, path) =>
                {
                    lastStage = stage;
                    applicationObserver?.Invoke(stage, path);
                },
                stagingParentFactory).Apply(session, request, cancellationToken);
            if (application.Status != DocumentationPatchApplicationStatus.Complete
                || application.Candidate is null)
            {
                return MapApplicationFailure(
                    session,
                    request,
                    resolver,
                    application,
                    lastStage,
                    cancellationToken);
            }

            using var handle = application.Candidate;
            var baseline = handle.Baseline;
            var resolution = resolver.Resolve(
                session,
                request,
                baseline,
                cancellationToken);
            if (resolution.Status != DocumentationPatchResolutionStatus.Resolved
                || resolution.Targets.Length != request.Blocks.Length)
            {
                return Result(RepositoryState(request));
            }

            using var consumption = handle.TryConsume();
            if (consumption is null)
            {
                return Result(CandidateState(request));
            }

            var expectedFiles = consumption.Files;
            var capture = consumption.CaptureCandidateForValidation(
                cancellationToken,
                () => observer?.Invoke(
                    DocumentationPatchEngineStage.BeforeCandidateTerminalPass));
            DocumentationPatchCandidateValidationDecision? decision = null;
            var candidateMismatch = capture.Status
                != DocumentationPatchCandidateCaptureStatus.Captured
                || !CandidateMatchesExpected(expectedFiles, capture.Files);
            if (!candidateMismatch)
            {
                decision = DocumentationPatchCandidateValidator.Validate(
                    session,
                    request,
                    baseline,
                    resolution,
                    capture.Files,
                    cancellationToken);
            }

            observer?.Invoke(DocumentationPatchEngineStage.BeforeFinalOriginalRebind);
            var finalRebind = baseline.Rebind(cancellationToken);
            if (finalRebind.Status != DocumentationPatchRepositoryRebindStatus.Unchanged)
            {
                return Result(RepositoryState(request));
            }

            if (candidateMismatch)
            {
                return Result(CandidateState(request));
            }

            if (decision is null || !decision.IsAccepted)
            {
                return Result(ProductRejection(
                    request,
                    decision?.FailureCode ?? "patch.rejected.unsafe-change",
                    decision?.FailureBlockId));
            }

            var acceptedResult = DocumentationPatchValidator.CreateResult(
                request,
                DocumentationPatchOutcome.Accepted,
                Enumerable.Repeat(
                    DocumentationPatchTargetStatus.Valid,
                    request.Blocks.Length),
                decision.ChangedFiles,
                decision.Invariants,
                []);
            var acceptedFiles = capture.Files.Select(file =>
                    new DocumentationPatchAcceptedCandidateFile(
                        file.RepositoryPath,
                        file.Bytes,
                        Sha256(file.Bytes.AsSpan())))
                .ToImmutableArray();
            var originalByPath = baseline.Entries.ToDictionary(
                entry => entry.RepositoryPath,
                PathComparer);
            var identities = capture.Files.Select(file =>
                new DocumentationPatchAcceptedCandidateIdentityEvidence(
                    file.RepositoryPath,
                    originalByPath[file.RepositoryPath].PhysicalIdentity,
                    file.Identity))
                .ToImmutableArray();
            var capability = new DocumentationPatchAcceptedCandidate(
                request,
                baseline,
                acceptedResult,
                acceptedFiles,
                identities);
            return new DocumentationPatchExecutionOutcome(
                DocumentationPatchExecutionStatus.Result,
                acceptedResult,
                capability,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException)
        {
            return HostFailure();
        }
    }

    private static DocumentationPatchExecutionOutcome MapApplicationFailure(
        ClassifiedRepositorySession session,
        DocumentationPatchRequest request,
        DocumentationPatchResolver resolver,
        DocumentationPatchApplicationResult application,
        DocumentationPatchApplicationStage? lastStage,
        CancellationToken cancellationToken)
    {
        if (lastStage == DocumentationPatchApplicationStage.BeforeOriginalRebind
            && application.Status is DocumentationPatchApplicationStatus.Stale
                or DocumentationPatchApplicationStatus.Rejected)
        {
            return Result(RepositoryState(request));
        }

        if (application.Status == DocumentationPatchApplicationStatus.Failure
            || lastStage is >= DocumentationPatchApplicationStage.RenderingCompleted)
        {
            return HostFailure();
        }

        if (application.PrimaryBlockId is { } blockId
            && application.PrimaryCode is { } blockCode)
        {
            return Result(TargetFailure(request, blockCode, blockId));
        }

        if (application.PrimaryCode == "patch.rejected.no-effective-change")
        {
            return Result(NoEffectiveChange(request));
        }

        if (application.PrimaryCode is "patch.stale.repository-context"
            or "patch.stale.input-identity"
            or "patch.stale.target-profile")
        {
            return Result(RootStale(request, application.PrimaryCode));
        }

        if (application.PrimaryCode == "patch.rejected.unsafe-change"
            && lastStage == DocumentationPatchApplicationStage.ResolutionCompleted)
        {
            var fresh = session.RepositorySession.CaptureDocumentationPatchRepositoryBaseline(
                cancellationToken);
            if (fresh.Baseline is not { } baseline)
            {
                return Result(RepositoryState(request));
            }

            var resolution = resolver.Resolve(session, request, baseline, cancellationToken);
            if (resolution.Status != DocumentationPatchResolutionStatus.Resolved)
            {
                return resolution.PrimaryBlockId is { } resolvedBlock
                    && resolution.PrimaryCode is { } resolvedCode
                    ? Result(TargetFailure(request, resolvedCode, resolvedBlock))
                    : Result(RepositoryState(request));
            }

            if (DocumentationPatchCandidateValidator.TryPlanRepresentationFailure(
                    request,
                    resolution,
                    baseline,
                    cancellationToken,
                    out var failingBlock)
                && failingBlock is not null)
            {
                return Result(ProductRejection(
                    request,
                    "patch.rejected.unsafe-change",
                    failingBlock));
            }

            return HostFailure();
        }

        return application.Status is DocumentationPatchApplicationStatus.Stale
            or DocumentationPatchApplicationStatus.Rejected
            ? Result(RepositoryState(request))
            : HostFailure();
    }

    private static DocumentationPatchValidationResult RepositoryState(
        DocumentationPatchRequest request) =>
        DocumentationPatchValidator.CreateResult(
            request,
            DocumentationPatchOutcome.Stale,
            Enumerable.Repeat(
                DocumentationPatchTargetStatus.NotEvaluated,
                request.Blocks.Length),
            [],
            DocumentationPatchCandidateValidator.RootFailureInvariants(),
            [RootDiagnostic("patch.stale.repository-state")]);

    private static DocumentationPatchValidationResult CandidateState(
        DocumentationPatchRequest request) =>
        DocumentationPatchValidator.CreateResult(
            request,
            DocumentationPatchOutcome.Rejected,
            Enumerable.Repeat(
                DocumentationPatchTargetStatus.Valid,
                request.Blocks.Length),
            [],
            DocumentationPatchCandidateValidator.RootFailureInvariants(),
            [RootDiagnostic("patch.rejected.candidate-state")]);

    private static DocumentationPatchValidationResult RootStale(
        DocumentationPatchRequest request,
        string code) =>
        DocumentationPatchValidator.CreateResult(
            request,
            DocumentationPatchOutcome.Stale,
            Enumerable.Repeat(
                DocumentationPatchTargetStatus.NotEvaluated,
                request.Blocks.Length),
            [],
            DocumentationPatchCandidateValidator.RootFailureInvariants(),
            [RootDiagnostic(code)]);

    private static DocumentationPatchValidationResult TargetFailure(
        DocumentationPatchRequest request,
        string code,
        string blockId)
    {
        var blockIndex = request.Blocks.FindIndex(block =>
            string.Equals(block.BlockId, blockId, StringComparison.Ordinal));
        if (blockIndex < 0)
        {
            return RepositoryState(request);
        }

        var stale = code.StartsWith("patch.stale.", StringComparison.Ordinal);
        var statuses = Enumerable.Repeat(
                DocumentationPatchTargetStatus.NotEvaluated,
                request.Blocks.Length)
            .ToArray();
        statuses[blockIndex] = stale
            ? DocumentationPatchTargetStatus.Stale
            : DocumentationPatchTargetStatus.Invalid;
        var locator = request.Blocks[blockIndex].Locator as DocumentationPatchRepositoryLocator;
        return DocumentationPatchValidator.CreateResult(
            request,
            stale ? DocumentationPatchOutcome.Stale : DocumentationPatchOutcome.Rejected,
            statuses,
            [],
            DocumentationPatchCandidateValidator.RootFailureInvariants(),
            [new DocumentationPatchDiagnostic(
                DocumentationPatchDiagnosticSeverity.Error,
                code,
                blockId,
                locator?.Path,
                null)]);
    }

    private static DocumentationPatchValidationResult ProductRejection(
        DocumentationPatchRequest request,
        string code,
        string? blockId)
    {
        if (blockId is not null)
        {
            return TargetFailure(request, code, blockId);
        }

        return DocumentationPatchValidator.CreateResult(
            request,
            DocumentationPatchOutcome.Rejected,
            Enumerable.Repeat(
                DocumentationPatchTargetStatus.Invalid,
                request.Blocks.Length),
            [],
            DocumentationPatchCandidateValidator.RootFailureInvariants(),
            request.Blocks.Select(block =>
            {
                var locator = block.Locator as DocumentationPatchRepositoryLocator;
                return new DocumentationPatchDiagnostic(
                    DocumentationPatchDiagnosticSeverity.Error,
                    code,
                    block.BlockId,
                    locator?.Path,
                    null);
            }));
    }

    private static DocumentationPatchValidationResult NoEffectiveChange(
        DocumentationPatchRequest request) =>
        DocumentationPatchValidator.CreateResult(
            request,
            DocumentationPatchOutcome.Rejected,
            Enumerable.Repeat(
                DocumentationPatchTargetStatus.Valid,
                request.Blocks.Length),
            [],
            DocumentationPatchCandidateValidator.PassedInvariants(),
            [RootDiagnostic("patch.rejected.no-effective-change")]);

    private static DocumentationPatchDiagnostic RootDiagnostic(string code) => new(
        DocumentationPatchDiagnosticSeverity.Error,
        code,
        null,
        null,
        null);

    private static bool CandidateMatchesExpected(
        ImmutableArray<CandidateWorkspaceFile> expected,
        ImmutableArray<CandidateWorkspaceFile> captured) =>
        expected.Length == captured.Length
        && expected.Zip(captured).All(pair =>
            string.Equals(pair.First.RepositoryPath, pair.Second.RepositoryPath, StringComparison.Ordinal)
            && pair.First.Bytes.AsSpan().SequenceEqual(pair.Second.Bytes.AsSpan()));

    private static DocumentationPatchExecutionOutcome Result(
        DocumentationPatchValidationResult result) =>
        new(DocumentationPatchExecutionStatus.Result, result, null, null);

    private static DocumentationPatchExecutionOutcome HostFailure() =>
        new(
            DocumentationPatchExecutionStatus.HostFailure,
            null,
            null,
            "patch.host.environment-failure");

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static StringComparer PathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

internal sealed record DocumentationPatchAcceptedCandidateIdentityEvidence(
    string RepositoryPath,
    DocumentationPatchPhysicalIdentity OriginalIdentity,
    CandidateIdentity CandidateIdentity);

internal static class ImmutableArrayExtensions
{
    public static int FindIndex<T>(this ImmutableArray<T> values, Func<T, bool> predicate)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (predicate(values[index]))
            {
                return index;
            }
        }

        return -1;
    }
}
