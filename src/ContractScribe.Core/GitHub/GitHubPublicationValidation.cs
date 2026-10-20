using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ContractScribe.Core;

public static class GitHubPublicationFactory
{
    public static ValidatedGitHubCampaignReadAuthority CreateCampaignReadAuthority(
        string repositoryOwner, string repositoryName, string targetRef, string campaignLineage)
    {
        ValidateRepositoryPart(repositoryOwner);
        ValidateRepositoryPart(repositoryName);
        Require(IsRefName(targetRef), GitHubPublicationValidationCode.InvalidVocabulary);
        RequireCampaignLineage(campaignLineage);
        return new(repositoryOwner, repositoryName, targetRef, campaignLineage);
    }

    public static ValidatedGitHubCampaignReadAuthority CreateCampaignReadAuthority(
        ValidatedGitHubPublicationAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return CreateCampaignReadAuthority(authority.RepositoryOwner, authority.RepositoryName,
            authority.TargetRef, authority.CampaignLineage);
    }

    public static ValidatedGitHubPublicationAuthority CreateAuthority(
        GitHubPublicationAuthorityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateRepositoryPart(input.RepositoryOwner);
        ValidateRepositoryPart(input.RepositoryName);
        Require(IsRefName(input.TargetRef), GitHubPublicationValidationCode.InvalidVocabulary);
        RequireGitOid(input.ExpectedBaseCommitOid, allowMissing: false);
        RequireCampaignLineage(input.CampaignLineage);
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
        var batch = CreateBatch(input.Batch);

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
                ValidatePathAndHashes(file.Path, file.OriginalFileSha256, file.CandidateFileSha256);
                Require(!string.Equals(
                        file.OriginalFileSha256,
                        file.CandidateFileSha256,
                        StringComparison.Ordinal),
                    GitHubPublicationValidationCode.InvalidCorrelation);
                Require(paths.Add(file.Path), GitHubPublicationValidationCode.DuplicatePath);
                Require(foldedPaths.Add(file.Path), GitHubPublicationValidationCode.CaseCollidingPath);
                Require(file.ChangedDocumentationBlockCount is > 0
                        and <= CampaignStateContract.MaximumActivePatchBlocks,
                    GitHubPublicationValidationCode.InvalidBound);
                RequireObservation(file.OriginalDocumentationByteCount);
                RequirePositiveObservation(file.CandidateDocumentationByteCount);
                RequireObservation(file.OriginalDocumentationLineCount);
                RequirePositiveObservation(file.CandidateDocumentationLineCount);
                cumulativeBlocks = checked(cumulativeBlocks + file.ChangedDocumentationBlockCount);
                cumulativePatchBytes = checked(
                    cumulativePatchBytes + file.CandidateDocumentationByteCount);
            }
        }
        catch (OverflowException)
        {
            throw Fail(GitHubPublicationValidationCode.ArithmeticOverflow);
        }

        Require(cumulativeBlocks == batch.AcceptedWorkItemKeys.Length,
            GitHubPublicationValidationCode.InvalidCorrelation);
        ValidateTransition(input);
        var authorityCommitment = GitHubPublicationCommitments.CreateAuthority(
            input, files, batch, cumulativeBlocks, cumulativePatchBytes);
        var operationCommitment = GitHubPublicationCommitments.CreateOperation(input, authorityCommitment);
        return new ValidatedGitHubPublicationAuthority(input, files, batch,
            cumulativeBlocks, cumulativePatchBytes, authorityCommitment, operationCommitment);
    }

    public static GitHubPublicationBatchInput CreateBatchInput(CampaignCheckpointState state)
    {
        CampaignStateFactory.Validate(state);
        Require(state.CandidateObservation is not null,
            GitHubPublicationValidationCode.InvalidCorrelation);
        var targets = state.Batch.CompleteTargets.ToDictionary(target => target.TargetKey, StringComparer.Ordinal);
        return new(state.Batch.Identity, state.Batch.CreationQuota,
            state.Batch.SelectedTargetKeys.Select(key => new GitHubPublicationBatchTarget(key, targets[key].WorkItemKey)).ToImmutableArray(),
            state.CandidateObservation!.AcceptedWorkItemKeys);
    }

    public static ValidatedGitHubPublicationBatch CreateBatch(GitHubPublicationBatchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireSha256(input.BatchIdentity);
        Require(input.CreationQuota is > 0 and <= CampaignStateContract.MaximumWorkItems,
            GitHubPublicationValidationCode.InvalidBound);
        var targets = CollectBounded(input.SelectedTargets, CampaignStateContract.MaximumWorkItems);
        var accepted = CollectBounded(input.AcceptedWorkItemKeys, CampaignStateContract.MaximumActivePatchBlocks)
            .Order(StringComparer.Ordinal).ToImmutableArray();
        Require(!targets.IsEmpty && targets.Length <= input.CreationQuota && !accepted.IsEmpty,
            GitHubPublicationValidationCode.InvalidBound);
        var targetKeys = new HashSet<string>(StringComparer.Ordinal);
        var workKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            Require(target is not null
                && CampaignStateFactory.IsOpaqueId(target.TargetKey, CampaignStateContract.MaximumIdentifierScalars)
                && CampaignStateFactory.IsOpaqueId(target.WorkItemKey, CampaignStateContract.MaximumIdentifierScalars)
                && targetKeys.Add(target.TargetKey), GitHubPublicationValidationCode.InvalidCorrelation);
            workKeys.Add(target.WorkItemKey);
        }
        Require(accepted.Distinct(StringComparer.Ordinal).Count() == accepted.Length
            && accepted.All(workKeys.Contains), GitHubPublicationValidationCode.InvalidCorrelation);
        using var writer = new GitHubPublicationCommitmentWriter("contract-scribe/github-publication-result-set/v1");
        writer.Add("batch", input.BatchIdentity);
        writer.Add("creation-quota", input.CreationQuota);
        writer.Add("selected.count", targets.Length);
        foreach (var target in targets)
        {
            writer.Add("target", target.TargetKey);
            writer.Add("work", target.WorkItemKey);
        }
        writer.Add("accepted.count", accepted.Length);
        foreach (var key in accepted) writer.Add("accepted", key);
        return new(input.BatchIdentity, input.CreationQuota, targets, accepted, writer.Complete());
    }

    public static bool MatchesFrozenResult(CampaignCheckpointState state, string batchIdentity,
        string resultSetCommitmentSha256, string originalCandidateCommitmentSha256)
    {
        CampaignStateFactory.Validate(state);
        if (state.CandidateObservation is null || state.Batch.Identity != batchIdentity
            || state.AcceptedCandidateOrigin?.CandidateObservation.PatchResultCommitmentSha256
                != originalCandidateCommitmentSha256) return false;
        return CreateBatch(CreateBatchInput(state)).ResultSetCommitmentSha256 == resultSetCommitmentSha256;
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

        long aggregateBytes = 0;
        try
        {
            for (var index = 0; index < collected.Length; index++)
            {
                var payload = collected[index];
                var expected = authority.ChangedFiles[index];
                Require(string.Equals(payload.Path, expected.Path, StringComparison.Ordinal),
                    GitHubPublicationValidationCode.PayloadMismatch);
                Require(payload.CandidateBytes.Length <= GitHubPublicationContract.MaximumPayloadBytesPerFile,
                    GitHubPublicationValidationCode.InvalidBound);
                aggregateBytes = checked(aggregateBytes + payload.CandidateBytes.Length);
            }
        }
        catch (OverflowException)
        {
            throw Fail(GitHubPublicationValidationCode.ArithmeticOverflow);
        }
        Require(aggregateBytes <= GitHubPublicationContract.MaximumAggregatePayloadBytes,
            GitHubPublicationValidationCode.InvalidBound);

        var validated = ImmutableArray.CreateBuilder<GitHubValidatedChangedFilePayload>(collected.Length);
        for (var index = 0; index < collected.Length; index++)
        {
            var payload = collected[index];
            var expected = authority.ChangedFiles[index];
            var owned = payload.CandidateBytes.ToArray();
            var sha256 = Convert.ToHexString(SHA256.HashData(owned)).ToLowerInvariant();
            Require(string.Equals(sha256, expected.CandidateFileSha256, StringComparison.Ordinal),
                GitHubPublicationValidationCode.PayloadMismatch);
            validated.Add(new GitHubValidatedChangedFilePayload(
                payload.Path,
                ImmutableCollectionsMarshal.AsImmutableArray(owned)));
        }
        return new ValidatedGitHubChangedFilePayload(
            validated.MoveToImmutable(),
            authority.AuthorityCommitmentSha256);
    }

    public static string CreateCoordinationRef(ValidatedGitHubPublicationAuthority authority)
        => CreateCoordinationRef(CreateCampaignReadAuthority(authority));

    public static string CreateCoordinationRef(ValidatedGitHubCampaignReadAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var key = GitHubPublicationCommitments.CreateIdentityKey(
            "coordination-ref", CanonicalRepositoryPart(authority.RepositoryOwner),
            CanonicalRepositoryPart(authority.RepositoryName),
            authority.TargetRef, authority.CampaignLineage);
        return $"refs/heads/contract-scribe/coordination/{key}";
    }

    public static string CreateProposalRef(ValidatedGitHubPublicationAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return CreateProposalRef(CreateCampaignReadAuthority(authority), authority.GenerationId,
            authority.SnapshotCommitmentSha256, authority.Batch.Identity);
    }

    public static string CreateProposalRefPrefix(ValidatedGitHubCampaignReadAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var campaign = GitHubPublicationCommitments.CreateIdentityKey(
            "proposal-campaign", CanonicalRepositoryPart(authority.RepositoryOwner),
            CanonicalRepositoryPart(authority.RepositoryName),
            authority.TargetRef, authority.CampaignLineage);
        return $"refs/heads/contract-scribe/proposals/{campaign}/";
    }

    public static string CreateProposalRef(ValidatedGitHubCampaignReadAuthority authority,
        string generationId, string snapshotCommitmentSha256, string batchIdentity)
    {
        ArgumentNullException.ThrowIfNull(authority);
        RequireOpaque(generationId);
        RequireSha256(snapshotCommitmentSha256);
        RequireSha256(batchIdentity);
        var generation = GitHubPublicationCommitments.CreateIdentityKey(
            "proposal-generation", authority.CampaignLineage, generationId,
            snapshotCommitmentSha256, batchIdentity);
        return CreateProposalRefPrefix(authority) + generation;
    }

    internal static bool IsOpaqueIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.EnumerateRunes().Count() > GitHubPublicationContract.MaximumIdentifierScalars)
        {
            return false;
        }
        return char.IsAsciiLetterOrDigit(value[0])
            && value[1..].All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.');
    }

    internal static bool IsCampaignLineage(string? value) =>
        CampaignStateFactory.IsOpaqueId(
            value,
            CampaignStateContract.MaximumIdentifierScalars);

    internal static bool IsSha256(string? value) => IsLowerHex(value, 64);
    internal static bool IsGitOid(string? value, bool allowMissing) =>
        IsLowerHex(value, 40)
        && (allowMissing || !string.Equals(value, GitHubPublicationContract.MissingGitObjectId,
            StringComparison.Ordinal));

    internal static bool IsRefName(string? value)
    {
        if (value is not { Length: > 11 }
            || value.Length > GitHubPublicationContract.MaximumPathScalars
            || !value.StartsWith("refs/heads/", StringComparison.Ordinal)
            || value.EndsWith("/", StringComparison.Ordinal)
            || value.EndsWith(".", StringComparison.Ordinal)
            || value.Contains("..", StringComparison.Ordinal)
            || value.Contains("//", StringComparison.Ordinal)
            || value.Contains("@{", StringComparison.Ordinal)
            || value.Any(character => char.IsControl(character)
                || char.IsWhiteSpace(character)
                || character is '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
        {
            return false;
        }
        return value[11..].Split('/').All(segment =>
            segment.Length > 0
            && !segment.StartsWith(".", StringComparison.Ordinal)
            && !segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            && !segment.EndsWith(".", StringComparison.Ordinal));
    }

    private static void ValidateTransition(GitHubPublicationAuthorityInput input)
    {
        if (input.TerminalPredecessor is { } predecessor)
        {
            ValidatePredecessor(predecessor);
            Require(predecessor.GenerationId != input.GenerationId,
                GitHubPublicationValidationCode.InvalidTransition);
        }
        Require(input.Transition switch
        {
            GitHubPublicationTransitionKind.Initial => input.TerminalPredecessor is null,
            GitHubPublicationTransitionKind.SuccessorAfterMerge =>
                input.TerminalPredecessor?.Disposition == GitHubPublicationPredecessorDisposition.Merged,
            GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged =>
                input.TerminalPredecessor?.Disposition == GitHubPublicationPredecessorDisposition.ClosedUnmerged,
            _ => false,
        }, GitHubPublicationValidationCode.InvalidTransition);
    }

    private static void ValidatePredecessor(GitHubPublicationPredecessorAuthority predecessor)
    {
        RequireOpaque(predecessor.LogicalPredecessorId);
        Require(predecessor.PullRequestNumber > 0, GitHubPublicationValidationCode.InvalidTransition);
        RequireOpaque(predecessor.GenerationId);
        RequireGitOid(predecessor.HeadOid, allowMissing: false);
        Require(Enum.IsDefined(predecessor.Disposition), GitHubPublicationValidationCode.InvalidVocabulary);
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

    private static void ValidatePathAndHashes(string path, string firstSha, string secondSha)
    {
        Require(IsRepositoryPath(path), GitHubPublicationValidationCode.InvalidPath);
        RequireSha256(firstSha);
        RequireSha256(secondSha);
    }

    private static void ValidateRepositoryPart(string value) =>
        Require(!string.IsNullOrEmpty(value)
                && value.Length <= GitHubPublicationContract.MaximumIdentifierScalars
                && value.All(character => char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '_' or '.'),
            GitHubPublicationValidationCode.InvalidVocabulary);

    private static string CanonicalRepositoryPart(string value) => value.ToLowerInvariant();

    private static bool IsRepositoryPath(string? value) =>
        value is { Length: > 0 }
        && value.EnumerateRunes().Count() <= GitHubPublicationContract.MaximumPathScalars
        && !value.StartsWith("/", StringComparison.Ordinal)
        && !value.StartsWith('\\')
        && !(value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
        && !value.Contains('\\')
        && !value.Contains("//", StringComparison.Ordinal)
        && !value.Split('/').Any(segment => segment is "" or "." or "..")
        && !value.Any(character => character is '\0' or '\r' or '\n');

    private static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RequireOpaque(string value) =>
        Require(IsOpaqueIdentifier(value), GitHubPublicationValidationCode.InvalidVocabulary);
    private static void RequireCampaignLineage(string value) =>
        Require(IsCampaignLineage(value), GitHubPublicationValidationCode.InvalidVocabulary);
    private static void RequireSha256(string value) =>
        Require(IsSha256(value), GitHubPublicationValidationCode.InvalidHash);
    private static void RequireGitOid(string value, bool allowMissing) =>
        Require(IsGitOid(value, allowMissing), GitHubPublicationValidationCode.InvalidHash);
    private static void RequireObservation(int value) =>
        Require(value >= 0, GitHubPublicationValidationCode.InvalidBound);
    private static void RequirePositiveObservation(int value) =>
        Require(value > 0, GitHubPublicationValidationCode.InvalidBound);
    private static void Require(
        [DoesNotReturnIf(false)] bool condition,
        GitHubPublicationValidationCode code)
    {
        if (!condition) throw Fail(code);
    }
    private static GitHubPublicationValidationException Fail(GitHubPublicationValidationCode code) =>
        new(code, "GitHub publication input violates a closed contract invariant.");
}

internal static class GitHubPublicationCommitments
{
    public static string CreateIdentityKey(string domain, params string[] values)
    {
        using var writer = new GitHubPublicationCommitmentWriter(
            $"contract-scribe/github-{domain}/v1");
        foreach (var value in values) writer.Add("value", value);
        return writer.Complete();
    }

    public static string CreateAuthority(
        GitHubPublicationAuthorityInput input,
        ImmutableArray<GitHubChangedFileAuthority> files,
        ValidatedGitHubPublicationBatch batch,
        int cumulativeBlocks,
        long cumulativePatchBytes)
    {
        using var writer = new GitHubPublicationCommitmentWriter(
            "contract-scribe/github-publication-authority/v1");
        AddInput(writer, input);
        writer.Add("batch-identity", batch.Identity);
        writer.Add("result-set", batch.ResultSetCommitmentSha256);
        writer.Add("cumulative-blocks", cumulativeBlocks);
        writer.Add("cumulative-patch-bytes", cumulativePatchBytes);
        writer.Add("changed-files.count", files.Length);
        foreach (var file in files)
        {
            writer.Add("file.path", file.Path);
            writer.Add("file.original-sha256", file.OriginalFileSha256);
            writer.Add("file.candidate-sha256", file.CandidateFileSha256);
            writer.Add("file.blocks", file.ChangedDocumentationBlockCount);
            writer.Add("file.original-documentation-bytes", file.OriginalDocumentationByteCount);
            writer.Add("file.candidate-documentation-bytes", file.CandidateDocumentationByteCount);
            writer.Add("file.original-documentation-lines", file.OriginalDocumentationLineCount);
            writer.Add("file.candidate-documentation-lines", file.CandidateDocumentationLineCount);
        }
        AddPredecessor(writer, input.TerminalPredecessor);
        return writer.Complete();
    }

    public static string CreateOperation(
        GitHubPublicationAuthorityInput input,
        string authorityCommitment)
    {
        using var writer = new GitHubPublicationCommitmentWriter(
            "contract-scribe/github-publication-operation/v1");
        writer.Add("authority-commitment", authorityCommitment);
        writer.Add("operation-id", input.OperationId);
        writer.Add("generation-id", input.GenerationId);
        writer.Add("transition", TransitionId(input.Transition));
        return writer.Complete();
    }

    private static void AddInput(
        GitHubPublicationCommitmentWriter writer,
        GitHubPublicationAuthorityInput input)
    {
        writer.Add("version", GitHubPublicationContract.Version);
        writer.Add("repository-owner", input.RepositoryOwner);
        writer.Add("repository-name", input.RepositoryName);
        writer.Add("target-ref", input.TargetRef);
        writer.Add("expected-base", input.ExpectedBaseCommitOid);
        writer.Add("campaign", input.CampaignLineage);
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
        writer.Add("transition", TransitionId(input.Transition));
    }

    private static void AddPredecessor(
        GitHubPublicationCommitmentWriter writer,
        GitHubPublicationPredecessorAuthority? predecessor)
    {
        writer.Add("predecessor.present", predecessor is not null);
        if (predecessor is null) return;
        writer.Add("predecessor.id", predecessor.LogicalPredecessorId);
        writer.Add("predecessor.pr", predecessor.PullRequestNumber);
        writer.Add("predecessor.generation", predecessor.GenerationId);
        writer.Add("predecessor.head", predecessor.HeadOid);
        writer.Add("predecessor.disposition", predecessor.Disposition.ToString());
    }

    private static string TransitionId(GitHubPublicationTransitionKind transition) => transition switch
    {
        GitHubPublicationTransitionKind.Initial => "initial",
        GitHubPublicationTransitionKind.SuccessorAfterMerge => "successor-after-merge",
        GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged => "successor-after-closed-unmerged",
        _ => throw new ArgumentOutOfRangeException(nameof(transition)),
    };
}

internal sealed class GitHubPublicationCommitmentWriter : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool completed;

    public GitHubPublicationCommitmentWriter(string domain) => Add("domain", domain);
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
        if (value is not null) Add(label, value);
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
