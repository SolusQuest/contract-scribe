using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace ContractScribe.Core;

public static class GitHubPublicationFactory
{
    public static ValidatedGitHubPublicationAuthority CreateAuthority(
        GitHubPublicationAuthorityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateRepositoryPart(input.RepositoryOwner);
        ValidateRepositoryPart(input.RepositoryName);
        Require(IsTargetRef(input.TargetRef), GitHubPublicationValidationCode.InvalidVocabulary);
        RequireGitOid(input.ExpectedBaseCommitOid, allowMissing: false);
        RequireOpaque(input.CampaignLineage);
        RequireSha256(input.SnapshotCommitmentSha256);
        RequireSha256(input.ExecutionCommitmentSha256);
        RequireSha256(input.WorkPlanCommitmentSha256);
        Require(input.CheckpointRevision is >= 0 and <= CampaignStateContract.MaximumObservation,
            GitHubPublicationValidationCode.InvalidBound);
        RequireSha256(input.CheckpointSha256);
        RequireSha256(input.CandidateCommitmentSha256);
        RequireSha256(input.PatchRequestSha256);
        RequireSha256(input.PatchResultCommitmentSha256);
        RequireSha256(input.AcceptedProjectionCommitmentSha256);
        RequireOpaque(input.OperationId);
        RequireOpaque(input.GenerationId);
        ValidatePolicy(input.Policy);

        var files = CollectBounded(input.ChangedFiles, GitHubPublicationContract.MaximumChangedFiles)
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        Require(!files.IsEmpty, GitHubPublicationValidationCode.InvalidBound);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var foldedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cumulativeBlocks = 0;
        long cumulativePatchBytes = 0;
        try
        {
            foreach (var file in files)
            {
                ArgumentNullException.ThrowIfNull(file);
                Require(IsRepositoryPath(file.Path), GitHubPublicationValidationCode.InvalidPath);
                Require(paths.Add(file.Path), GitHubPublicationValidationCode.DuplicatePath);
                Require(foldedPaths.Add(file.Path), GitHubPublicationValidationCode.CaseCollidingPath);
                RequireSha256(file.OriginalFileSha256);
                RequireSha256(file.CandidateFileSha256);
                if (file.PrecedingCandidateFileSha256 is not null)
                {
                    RequireSha256(file.PrecedingCandidateFileSha256);
                }
                Require(file.ChangedDocumentationBlockCount > 0,
                    GitHubPublicationValidationCode.InvalidBound);
                RequireObservation(file.OriginalDocumentationByteCount);
                RequireObservation(file.CandidateDocumentationByteCount);
                RequireObservation(file.OriginalDocumentationLineCount);
                RequireObservation(file.CandidateDocumentationLineCount);
                cumulativeBlocks = checked(cumulativeBlocks + file.ChangedDocumentationBlockCount);
                // This is intentionally the exact M4 complete-candidate measure.
                cumulativePatchBytes = checked(
                    cumulativePatchBytes + file.CandidateDocumentationByteCount);
            }
        }
        catch (OverflowException)
        {
            throw Fail(GitHubPublicationValidationCode.ArithmeticOverflow);
        }

        Require(cumulativeBlocks <= input.Policy.MaximumDocumentationBlocks,
            GitHubPublicationValidationCode.InvalidPolicy);
        Require(files.Length <= input.Policy.MaximumDistinctChangedFiles,
            GitHubPublicationValidationCode.InvalidPolicy);
        Require(cumulativePatchBytes <= input.Policy.MaximumCumulativePatchBytes,
            GitHubPublicationValidationCode.InvalidPolicy);
        ValidateTransition(input, files);

        var policyCommitment = GitHubPublicationCommitments.CreatePolicy(input.Policy);
        var authorityCommitment = GitHubPublicationCommitments.CreateAuthority(
            input,
            files,
            cumulativeBlocks,
            cumulativePatchBytes,
            policyCommitment);
        var operationCommitment = GitHubPublicationCommitments.CreateOperation(
            input,
            authorityCommitment,
            policyCommitment);
        return new ValidatedGitHubPublicationAuthority(
            input,
            files,
            cumulativeBlocks,
            cumulativePatchBytes,
            policyCommitment,
            authorityCommitment,
            operationCommitment);
    }

    public static ValidatedGitHubChangedFilePayload CreatePayload(
        ValidatedGitHubPublicationAuthority authority,
        IEnumerable<GitHubChangedFilePayloadInput> files)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var collected = CollectBounded(files, GitHubPublicationContract.MaximumChangedFiles)
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
        Require(collected.Length == authority.ChangedFiles.Length,
            GitHubPublicationValidationCode.PayloadMismatch);
        var validated = ImmutableArray.CreateBuilder<GitHubValidatedChangedFilePayload>(collected.Length);
        for (var index = 0; index < collected.Length; index++)
        {
            var payload = collected[index];
            var expected = authority.ChangedFiles[index];
            Require(string.Equals(payload.Path, expected.Path, StringComparison.Ordinal),
                GitHubPublicationValidationCode.PayloadMismatch);
            var bytes = payload.CandidateBytes.ToArray();
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Require(string.Equals(sha256, expected.CandidateFileSha256, StringComparison.Ordinal),
                GitHubPublicationValidationCode.PayloadMismatch);
            validated.Add(new GitHubValidatedChangedFilePayload(
                payload.Path,
                ImmutableArray.CreateRange(bytes)));
        }

        return new ValidatedGitHubChangedFilePayload(
            validated.MoveToImmutable(),
            authority.AuthorityCommitmentSha256);
    }

    public static ValidatedGitHubPreparedRemoteOperation PrepareRemoteOperation(
        ValidatedGitHubPublicationAuthority authority,
        GitHubAuthenticatedRemoteObservation observation,
        GitHubDeterministicCommitPayload coordinationCommit,
        GitHubDeterministicCommitPayload proposalCommit,
        GitHubDeterministicPullRequestPayload pullRequest)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(coordinationCommit);
        ArgumentNullException.ThrowIfNull(proposalCommit);
        ArgumentNullException.ThrowIfNull(pullRequest);
        RequireOpaque(observation.CanonicalRepositoryId);
        RequireGitOid(observation.ObservedTargetCommitOid, allowMissing: false);
        Require(string.Equals(
                observation.ObservedTargetCommitOid,
                authority.ExpectedBaseCommitOid,
                StringComparison.Ordinal),
            GitHubPublicationValidationCode.InvalidCorrelation);
        RequireGitOid(observation.ObservedBaseTreeOid, allowMissing: false);
        RequireGitOid(observation.CoordinationRefOid, allowMissing: true);
        RequireOptionalGitOid(observation.ProposalRefOid);
        RequireOptionalGitOid(observation.ProposalCommitOid);
        RequireOptionalGitOid(observation.ProposalParentOid);
        RequireOptionalGitOid(observation.ProposalTreeOid);
        Require(observation.ActivePullRequestNumber is null or > 0,
            GitHubPublicationValidationCode.InvalidBound);
        if (observation.ActivePullRequestState is not null)
        {
            RequireOpaque(observation.ActivePullRequestState);
        }

        var entries = CollectBounded(observation.Entries, GitHubPublicationContract.MaximumChangedFiles)
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        Require(entries.Length == authority.ChangedFiles.Length,
            GitHubPublicationValidationCode.InvalidCorrelation);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var file = authority.ChangedFiles[index];
            Require(string.Equals(entry.Path, file.Path, StringComparison.Ordinal),
                GitHubPublicationValidationCode.InvalidCorrelation);
            RequireGitOid(entry.ObjectOid, allowMissing: false);
            Require(entry.Kind == GitHubRemoteEntryKind.Blob,
                GitHubPublicationValidationCode.InvalidCorrelation);
            Require(entry.Mode is "100644" or "100755",
                GitHubPublicationValidationCode.InvalidCorrelation);
            RequireSha256(entry.FullFileSha256);
            if (entry.WasPreviouslyPublished)
            {
                Require(authority.Transition == GitHubPublicationTransitionKind.SameSnapshotAppend
                        && file.PrecedingCandidateFileSha256 is not null
                        && string.Equals(
                            entry.FullFileSha256,
                            file.PrecedingCandidateFileSha256,
                            StringComparison.Ordinal),
                    GitHubPublicationValidationCode.InvalidCorrelation);
            }
            else
            {
                Require(string.Equals(
                        entry.FullFileSha256,
                        file.OriginalFileSha256,
                        StringComparison.Ordinal),
                    GitHubPublicationValidationCode.InvalidCorrelation);
            }
        }

        ValidateCommitPayload(coordinationCommit);
        ValidateCommitPayload(proposalCommit);
        Require(IsTargetRef(pullRequest.HeadRef) && IsTargetRef(pullRequest.BaseRef),
            GitHubPublicationValidationCode.InvalidVocabulary);
        RequireSha256(pullRequest.TitleSha256);
        RequireSha256(pullRequest.BodyMarkerSha256);
        Require(pullRequest.Draft && !pullRequest.MaintainerCanModify,
            GitHubPublicationValidationCode.InvalidCorrelation);

        var commitment = GitHubPublicationCommitments.CreatePreparedRemoteOperation(
            authority,
            observation,
            entries,
            coordinationCommit,
            proposalCommit,
            pullRequest);
        return new ValidatedGitHubPreparedRemoteOperation(
            observation,
            entries,
            coordinationCommit,
            proposalCommit,
            pullRequest,
            commitment);
    }

    public static string CreateCoordinationRef(string campaignCommitmentSha256)
    {
        RequireSha256(campaignCommitmentSha256);
        return $"refs/heads/contract-scribe/coordination/{campaignCommitmentSha256}";
    }

    public static string CreateProposalRef(
        string campaignCommitmentSha256,
        string generationCommitmentSha256)
    {
        RequireSha256(campaignCommitmentSha256);
        RequireSha256(generationCommitmentSha256);
        return $"refs/heads/contract-scribe/proposals/{campaignCommitmentSha256}/{generationCommitmentSha256}";
    }

    internal static bool IsOpaqueIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.EnumerateRunes().Count() > GitHubPublicationContract.MaximumIdentifierScalars)
        {
            return false;
        }
        return !value.Any(character => char.IsControl(character)
            || character is '\r' or '\n' or '\0');
    }

    internal static bool IsSha256(string? value) =>
        IsLowerHex(value, 64);

    internal static bool IsGitOid(string? value, bool allowMissing) =>
        IsLowerHex(value, 40)
        && (allowMissing || !string.Equals(value, GitHubPublicationContract.MissingGitObjectId,
            StringComparison.Ordinal));

    internal static bool IsRefName(string? value) => IsTargetRef(value);

    private static void ValidateTransition(
        GitHubPublicationAuthorityInput input,
        ImmutableArray<GitHubChangedFileAuthority> files)
    {
        var hasPredecessor = input.LogicalPredecessorId is not null;
        var hasPrecedingCandidate = input.PrecedingCandidateCommitmentSha256 is not null;
        var hasAuthorization = input.ClosedUnmergedSuccessorAuthorization is not null;
        if (hasPredecessor)
        {
            RequireOpaque(input.LogicalPredecessorId!);
        }
        if (hasPrecedingCandidate)
        {
            RequireSha256(input.PrecedingCandidateCommitmentSha256!);
        }

        switch (input.Transition)
        {
            case GitHubPublicationTransitionKind.Initial:
                Require(!hasPredecessor && !hasPrecedingCandidate && !hasAuthorization,
                    GitHubPublicationValidationCode.InvalidTransition);
                Require(files.All(file => file.PrecedingCandidateFileSha256 is null),
                    GitHubPublicationValidationCode.InvalidTransition);
                break;
            case GitHubPublicationTransitionKind.SameSnapshotAppend:
                Require(hasPredecessor && hasPrecedingCandidate && !hasAuthorization,
                    GitHubPublicationValidationCode.InvalidTransition);
                break;
            case GitHubPublicationTransitionKind.SuccessorAfterMerge:
                Require(hasPredecessor && !hasPrecedingCandidate && !hasAuthorization,
                    GitHubPublicationValidationCode.InvalidTransition);
                Require(files.All(file => file.PrecedingCandidateFileSha256 is null),
                    GitHubPublicationValidationCode.InvalidTransition);
                break;
            case GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged:
                Require(hasPredecessor && !hasPrecedingCandidate && hasAuthorization,
                    GitHubPublicationValidationCode.InvalidTransition);
                ValidateClosedAuthorization(input, input.ClosedUnmergedSuccessorAuthorization!);
                Require(files.All(file => file.PrecedingCandidateFileSha256 is null),
                    GitHubPublicationValidationCode.InvalidTransition);
                break;
            default:
                throw Fail(GitHubPublicationValidationCode.InvalidVocabulary);
        }
    }

    private static void ValidateClosedAuthorization(
        GitHubPublicationAuthorityInput input,
        GitHubClosedUnmergedSuccessorAuthorization authorization)
    {
        RequireOpaque(authorization.AuthorizationId);
        Require(authorization.ClosedPullRequestNumber > 0,
            GitHubPublicationValidationCode.InvalidAuthorization);
        RequireOpaque(authorization.ClosedGenerationId);
        RequireGitOid(authorization.ClosedHeadOid, allowMissing: false);
        RequireSha256(authorization.FreshSnapshotCommitmentSha256);
        RequireSha256(authorization.FreshWorkPlanCommitmentSha256);
        RequireSha256(authorization.FreshCandidateCommitmentSha256);
        RequireOpaque(authorization.NewGenerationId);
        RequireOpaque(authorization.OperationId);
        Require(string.Equals(authorization.FreshSnapshotCommitmentSha256,
                    input.SnapshotCommitmentSha256, StringComparison.Ordinal)
                && string.Equals(authorization.FreshWorkPlanCommitmentSha256,
                    input.WorkPlanCommitmentSha256, StringComparison.Ordinal)
                && string.Equals(authorization.FreshCandidateCommitmentSha256,
                    input.CandidateCommitmentSha256, StringComparison.Ordinal)
                && string.Equals(authorization.NewGenerationId,
                    input.GenerationId, StringComparison.Ordinal)
                && string.Equals(authorization.OperationId,
                    input.OperationId, StringComparison.Ordinal),
            GitHubPublicationValidationCode.InvalidAuthorization);
    }

    private static void ValidatePolicy(GitHubPublicationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Require(policy.MaximumDocumentationBlocks is > 0
                and <= CampaignStateContract.MaximumActivePatchBlocks,
            GitHubPublicationValidationCode.InvalidPolicy);
        Require(policy.MaximumDistinctChangedFiles is > 0
                and <= CampaignStateContract.MaximumChangedFiles,
            GitHubPublicationValidationCode.InvalidPolicy);
        Require(policy.MaximumCumulativePatchBytes is > 0
                and <= CampaignStateContract.MaximumPatchBytes,
            GitHubPublicationValidationCode.InvalidPolicy);
    }

    private static void ValidateCommitPayload(GitHubDeterministicCommitPayload payload)
    {
        RequireSha256(payload.TreeLayoutCommitmentSha256);
        RequireSha256(payload.MessageSha256);
        RequireGitOid(payload.ParentOid, allowMissing: true);
        RequireOpaque(payload.AuthorName);
        RequireOpaque(payload.AuthorEmail);
        RequireOpaque(payload.AuthorTimestamp);
        RequireOpaque(payload.CommitterName);
        RequireOpaque(payload.CommitterEmail);
        RequireOpaque(payload.CommitterTimestamp);
        RequireSha256(payload.OwnershipMarkerSha256);
        RequireGitOid(payload.ExpectedCommitOid, allowMissing: false);
    }

    private static ImmutableArray<T> CollectBounded<T>(IEnumerable<T> source, int maximum)
    {
        ArgumentNullException.ThrowIfNull(source);
        var builder = ImmutableArray.CreateBuilder<T>();
        using var enumerator = source.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (builder.Count == maximum)
            {
                throw Fail(GitHubPublicationValidationCode.InvalidBound);
            }
            builder.Add(enumerator.Current);
        }
        return builder.ToImmutable();
    }

    private static void ValidateRepositoryPart(string value)
    {
        Require(!string.IsNullOrEmpty(value)
                && value.Length <= GitHubPublicationContract.MaximumIdentifierScalars
                && value.All(character => char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '_' or '.'),
            GitHubPublicationValidationCode.InvalidVocabulary);
    }

    private static bool IsTargetRef(string? value) =>
        value is { Length: > 11 }
        && value.Length <= GitHubPublicationContract.MaximumPathScalars
        && value.StartsWith("refs/heads/", StringComparison.Ordinal)
        && !value.Contains("..", StringComparison.Ordinal)
        && !value.Contains("//", StringComparison.Ordinal)
        && !value.EndsWith("/", StringComparison.Ordinal)
        && !value.Any(character => char.IsControl(character)
            || char.IsWhiteSpace(character)
            || character is '~' or '^' or ':' or '?' or '*' or '[' or '\\');

    private static bool IsRepositoryPath(string? value) =>
        value is { Length: > 0 }
        && value.EnumerateRunes().Count() <= GitHubPublicationContract.MaximumPathScalars
        && !value.StartsWith("/", StringComparison.Ordinal)
        && !value.StartsWith('\\')
        && !value.Contains('\\')
        && !value.Contains("//", StringComparison.Ordinal)
        && !value.Split('/').Any(segment => segment is "" or "." or "..")
        && !value.Any(character => character is '\0' or '\r' or '\n');

    private static bool IsLowerHex(string? value, int length) =>
        value is not null
        && value.Length == length
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RequireOpaque(string value) =>
        Require(IsOpaqueIdentifier(value), GitHubPublicationValidationCode.InvalidVocabulary);

    private static void RequireSha256(string value) =>
        Require(IsSha256(value), GitHubPublicationValidationCode.InvalidHash);

    private static void RequireGitOid(string value, bool allowMissing) =>
        Require(IsGitOid(value, allowMissing), GitHubPublicationValidationCode.InvalidHash);

    private static void RequireOptionalGitOid(string? value)
    {
        if (value is not null)
        {
            RequireGitOid(value, allowMissing: false);
        }
    }

    private static void RequireObservation(int value) =>
        Require(value is >= 0 and <= int.MaxValue, GitHubPublicationValidationCode.InvalidBound);

    private static void Require(
        [DoesNotReturnIf(false)] bool condition,
        GitHubPublicationValidationCode code)
    {
        if (!condition)
        {
            throw Fail(code);
        }
    }

    private static GitHubPublicationValidationException Fail(
        GitHubPublicationValidationCode code) =>
        new(code, "GitHub publication input violates a closed contract invariant.");
}

internal static class GitHubPublicationCommitments
{
    public static string CreatePolicy(GitHubPublicationPolicy policy)
    {
        using var writer = new Writer("contract-scribe/github-publication-policy/v1");
        writer.Add("maximum-documentation-blocks", policy.MaximumDocumentationBlocks);
        writer.Add("maximum-distinct-changed-files", policy.MaximumDistinctChangedFiles);
        writer.Add("maximum-cumulative-patch-bytes", policy.MaximumCumulativePatchBytes);
        writer.Add("counting", "complete-candidate-candidate-documentation-byte-count");
        writer.Add("generation-lifetime", "immutable");
        writer.Add("closed-unmerged", "terminal-no-automatic-retry");
        return writer.Complete();
    }

    public static string CreateAuthority(
        GitHubPublicationAuthorityInput input,
        ImmutableArray<GitHubChangedFileAuthority> files,
        int cumulativeBlocks,
        long cumulativePatchBytes,
        string policyCommitment)
    {
        using var writer = new Writer("contract-scribe/github-publication-authority/v1");
        AddInput(writer, input);
        writer.Add("policy-commitment", policyCommitment);
        writer.Add("cumulative-blocks", cumulativeBlocks);
        writer.Add("cumulative-patch-bytes", cumulativePatchBytes);
        writer.Add("changed-files.count", files.Length);
        foreach (var file in files)
        {
            writer.Add("file.path", file.Path);
            writer.Add("file.original-sha256", file.OriginalFileSha256);
            writer.Add("file.candidate-sha256", file.CandidateFileSha256);
            writer.AddOptional("file.preceding-candidate-sha256", file.PrecedingCandidateFileSha256);
            writer.Add("file.blocks", file.ChangedDocumentationBlockCount);
            writer.Add("file.original-documentation-bytes", file.OriginalDocumentationByteCount);
            writer.Add("file.candidate-documentation-bytes", file.CandidateDocumentationByteCount);
            writer.Add("file.original-documentation-lines", file.OriginalDocumentationLineCount);
            writer.Add("file.candidate-documentation-lines", file.CandidateDocumentationLineCount);
        }
        AddAuthorization(writer, input.ClosedUnmergedSuccessorAuthorization);
        return writer.Complete();
    }

    public static string CreateOperation(
        GitHubPublicationAuthorityInput input,
        string authorityCommitment,
        string policyCommitment)
    {
        using var writer = new Writer("contract-scribe/github-publication-operation/v1");
        writer.Add("authority-commitment", authorityCommitment);
        writer.Add("policy-commitment", policyCommitment);
        writer.Add("operation-id", input.OperationId);
        writer.Add("generation-id", input.GenerationId);
        writer.AddOptional("logical-predecessor-id", input.LogicalPredecessorId);
        writer.Add("transition", TransitionId(input.Transition));
        return writer.Complete();
    }

    public static string CreatePreparedRemoteOperation(
        ValidatedGitHubPublicationAuthority authority,
        GitHubAuthenticatedRemoteObservation observation,
        ImmutableArray<GitHubRemoteEntryObservation> entries,
        GitHubDeterministicCommitPayload coordinationCommit,
        GitHubDeterministicCommitPayload proposalCommit,
        GitHubDeterministicPullRequestPayload pullRequest)
    {
        using var writer = new Writer("contract-scribe/github-prepared-remote-operation/v1");
        writer.Add("authority-commitment", authority.AuthorityCommitmentSha256);
        writer.Add("operation-commitment", authority.OperationCommitmentSha256);
        writer.Add("repository-id", observation.CanonicalRepositoryId);
        writer.Add("target-commit", observation.ObservedTargetCommitOid);
        writer.Add("base-tree", observation.ObservedBaseTreeOid);
        writer.Add("coordination-ref", observation.CoordinationRefOid);
        writer.AddOptional("proposal-ref", observation.ProposalRefOid);
        writer.AddOptional("proposal-commit", observation.ProposalCommitOid);
        writer.AddOptional("proposal-parent", observation.ProposalParentOid);
        writer.AddOptional("proposal-tree", observation.ProposalTreeOid);
        writer.AddOptional("active-pr-number", observation.ActivePullRequestNumber?.ToString());
        writer.AddOptional("active-pr-state", observation.ActivePullRequestState);
        writer.Add("entries.count", entries.Length);
        foreach (var entry in entries)
        {
            writer.Add("entry.path", entry.Path);
            writer.Add("entry.oid", entry.ObjectOid);
            writer.Add("entry.kind", entry.Kind.ToString());
            writer.Add("entry.mode", entry.Mode);
            writer.Add("entry.sha256", entry.FullFileSha256);
            writer.Add("entry.previously-published", entry.WasPreviouslyPublished);
        }
        AddCommit(writer, "coordination", coordinationCommit);
        AddCommit(writer, "proposal", proposalCommit);
        writer.Add("pr.head", pullRequest.HeadRef);
        writer.Add("pr.base", pullRequest.BaseRef);
        writer.Add("pr.title-sha256", pullRequest.TitleSha256);
        writer.Add("pr.body-marker-sha256", pullRequest.BodyMarkerSha256);
        writer.Add("pr.draft", pullRequest.Draft);
        writer.Add("pr.maintainer-can-modify", pullRequest.MaintainerCanModify);
        return writer.Complete();
    }

    private static void AddInput(Writer writer, GitHubPublicationAuthorityInput input)
    {
        writer.Add("version", GitHubPublicationContract.Version);
        writer.Add("repository-owner", input.RepositoryOwner);
        writer.Add("repository-name", input.RepositoryName);
        writer.Add("target-ref", input.TargetRef);
        writer.Add("expected-base-commit", input.ExpectedBaseCommitOid);
        writer.Add("campaign-lineage", input.CampaignLineage);
        writer.Add("snapshot", input.SnapshotCommitmentSha256);
        writer.Add("execution", input.ExecutionCommitmentSha256);
        writer.Add("work-plan", input.WorkPlanCommitmentSha256);
        writer.Add("checkpoint-revision", input.CheckpointRevision);
        writer.Add("checkpoint", input.CheckpointSha256);
        writer.Add("candidate", input.CandidateCommitmentSha256);
        writer.Add("patch-request", input.PatchRequestSha256);
        writer.Add("patch-result", input.PatchResultCommitmentSha256);
        writer.Add("accepted-projection", input.AcceptedProjectionCommitmentSha256);
        writer.Add("operation-id", input.OperationId);
        writer.Add("generation-id", input.GenerationId);
        writer.AddOptional("logical-predecessor-id", input.LogicalPredecessorId);
        writer.AddOptional("preceding-candidate", input.PrecedingCandidateCommitmentSha256);
        writer.Add("transition", TransitionId(input.Transition));
    }

    private static void AddAuthorization(
        Writer writer,
        GitHubClosedUnmergedSuccessorAuthorization? authorization)
    {
        writer.Add("closed-authorization.present", authorization is not null);
        if (authorization is null)
        {
            return;
        }
        writer.Add("closed-authorization.id", authorization.AuthorizationId);
        writer.Add("closed-authorization.pr", authorization.ClosedPullRequestNumber);
        writer.Add("closed-authorization.generation", authorization.ClosedGenerationId);
        writer.Add("closed-authorization.head", authorization.ClosedHeadOid);
        writer.Add("closed-authorization.snapshot", authorization.FreshSnapshotCommitmentSha256);
        writer.Add("closed-authorization.work-plan", authorization.FreshWorkPlanCommitmentSha256);
        writer.Add("closed-authorization.candidate", authorization.FreshCandidateCommitmentSha256);
        writer.Add("closed-authorization.new-generation", authorization.NewGenerationId);
        writer.Add("closed-authorization.operation", authorization.OperationId);
    }

    private static void AddCommit(Writer writer, string prefix, GitHubDeterministicCommitPayload payload)
    {
        writer.Add(prefix + ".tree", payload.TreeLayoutCommitmentSha256);
        writer.Add(prefix + ".message", payload.MessageSha256);
        writer.Add(prefix + ".parent", payload.ParentOid);
        writer.Add(prefix + ".author-name", payload.AuthorName);
        writer.Add(prefix + ".author-email", payload.AuthorEmail);
        writer.Add(prefix + ".author-timestamp", payload.AuthorTimestamp);
        writer.Add(prefix + ".committer-name", payload.CommitterName);
        writer.Add(prefix + ".committer-email", payload.CommitterEmail);
        writer.Add(prefix + ".committer-timestamp", payload.CommitterTimestamp);
        writer.Add(prefix + ".marker", payload.OwnershipMarkerSha256);
        writer.Add(prefix + ".expected-oid", payload.ExpectedCommitOid);
    }

    private static string TransitionId(GitHubPublicationTransitionKind transition) => transition switch
    {
        GitHubPublicationTransitionKind.Initial => "initial",
        GitHubPublicationTransitionKind.SameSnapshotAppend => "same-snapshot-append",
        GitHubPublicationTransitionKind.SuccessorAfterMerge => "successor-after-merge",
        GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged => "successor-after-closed-unmerged",
        _ => throw new ArgumentOutOfRangeException(nameof(transition)),
    };

    private sealed class Writer : IDisposable
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool completed;

        public Writer(string domain) => Add("domain", domain);
        public void Add(string label, string value)
        {
            ArgumentNullException.ThrowIfNull(label);
            ArgumentNullException.ThrowIfNull(value);
            Append(StrictUtf8.GetBytes(label));
            Append(StrictUtf8.GetBytes(value));
        }
        public void Add(string label, bool value) => Add(label, value ? "1" : "0");
        public void Add(string label, int value) => Add(label, (long)value);
        public void Add(string label, long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            Append(StrictUtf8.GetBytes(label));
            Append(bytes);
        }
        public void AddOptional(string label, string? value)
        {
            Add(label + ".present", value is not null);
            if (value is not null)
            {
                Add(label, value);
            }
        }
        public string Complete()
        {
            ObjectDisposedException.ThrowIf(completed, this);
            completed = true;
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        public void Dispose()
        {
            hash.Dispose();
            completed = true;
        }
        private void Append(ReadOnlySpan<byte> value)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
            hash.AppendData(length);
            hash.AppendData(value);
        }
    }
}
