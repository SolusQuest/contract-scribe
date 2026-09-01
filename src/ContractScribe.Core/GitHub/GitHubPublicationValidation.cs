using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContractScribe.Core;

public static class GitHubPublicationFactory
{
    public static ValidatedGitHubPublicationAuthority CreateAuthority(
        GitHubPublicationAuthorityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateRepositoryPart(input.RepositoryOwner);
        ValidateRepositoryPart(input.RepositoryName);
        Require(IsRefName(input.TargetRef), GitHubPublicationValidationCode.InvalidVocabulary);
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
        ValidatePolicy(input.AcceptedM4Ceilings, input.Policy);

        var files = CollectBounded(input.ChangedFiles, GitHubPublicationContract.MaximumChangedFiles)
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        var precedingFiles = CollectBounded(
                input.PrecedingChangedFiles,
                GitHubPublicationContract.MaximumChangedFiles)
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
                Require(paths.Add(file.Path), GitHubPublicationValidationCode.DuplicatePath);
                Require(foldedPaths.Add(file.Path), GitHubPublicationValidationCode.CaseCollidingPath);
                Require(file.ChangedDocumentationBlockCount is > 0
                        and <= CampaignStateContract.MaximumActivePatchBlocks,
                    GitHubPublicationValidationCode.InvalidBound);
                RequireObservation(file.OriginalDocumentationByteCount);
                RequireObservation(file.CandidateDocumentationByteCount);
                RequireObservation(file.OriginalDocumentationLineCount);
                RequireObservation(file.CandidateDocumentationLineCount);
                cumulativeBlocks = checked(cumulativeBlocks + file.ChangedDocumentationBlockCount);
                cumulativePatchBytes = checked(
                    cumulativePatchBytes + file.CandidateDocumentationByteCount);
            }
        }
        catch (OverflowException)
        {
            throw Fail(GitHubPublicationValidationCode.ArithmeticOverflow);
        }

        var precedingPaths = new HashSet<string>(StringComparer.Ordinal);
        var precedingFolded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in precedingFiles)
        {
            ArgumentNullException.ThrowIfNull(file);
            ValidatePathAndHashes(file.Path, file.CandidateFileSha256, file.CandidateFileSha256);
            Require(precedingPaths.Add(file.Path), GitHubPublicationValidationCode.DuplicatePath);
            Require(precedingFolded.Add(file.Path), GitHubPublicationValidationCode.CaseCollidingPath);
            Require(paths.Contains(file.Path), GitHubPublicationValidationCode.InvalidCorrelation);
        }

        Require(cumulativeBlocks <= input.Policy.MaximumDocumentationBlocks,
            GitHubPublicationValidationCode.InvalidPolicy);
        Require(files.Length <= input.Policy.MaximumDistinctChangedFiles,
            GitHubPublicationValidationCode.InvalidPolicy);
        Require(cumulativePatchBytes <= input.Policy.MaximumCumulativePatchBytes,
            GitHubPublicationValidationCode.InvalidPolicy);
        ValidateTransition(input, precedingFiles);

        var policyCommitment = GitHubPublicationCommitments.CreatePolicy(
            input.AcceptedM4Ceilings,
            input.Policy);
        var authorityCommitment = GitHubPublicationCommitments.CreateAuthority(
            input,
            files,
            precedingFiles,
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
            precedingFiles,
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

    public static ValidatedGitHubPreparedRemoteOperation PrepareRemoteOperation(
        ValidatedGitHubPublicationAuthority authority,
        ValidatedGitHubChangedFilePayload payload,
        GitHubAuthenticatedRemoteObservation observation)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(observation);
        Require(string.Equals(payload.AuthorityCommitmentSha256,
                authority.AuthorityCommitmentSha256, StringComparison.Ordinal),
            GitHubPublicationValidationCode.InvalidCorrelation);
        Require(string.Equals(observation.RepositoryOwner, authority.RepositoryOwner, StringComparison.Ordinal)
                && string.Equals(observation.RepositoryName, authority.RepositoryName, StringComparison.Ordinal),
            GitHubPublicationValidationCode.InvalidCorrelation);
        RequireOpaque(observation.CanonicalRepositoryId);
        RequireGitOid(observation.ObservedTargetCommitOid, allowMissing: false);
        Require(string.Equals(observation.ObservedTargetCommitOid,
                authority.ExpectedBaseCommitOid, StringComparison.Ordinal),
            GitHubPublicationValidationCode.InvalidCorrelation);
        RequireGitOid(observation.ObservedBaseTreeOid, allowMissing: false);

        var baseEntries = ValidateTreeEntries(observation.BaseTreeEntries);
        Require(string.Equals(GitHubPublicationCodec.CreateTreeOid(baseEntries),
                observation.ObservedBaseTreeOid, StringComparison.Ordinal),
            GitHubPublicationValidationCode.InvalidCorrelation);
        var proposalEntries = observation.Proposal is null
            ? ImmutableArray<GitHubRemoteEntryObservation>.Empty
            : ValidateTreeEntries(observation.Proposal.Entries);
        var pullRequests = CollectBounded(observation.PullRequests, 2);
        var coordinationFiles = ValidateRemoteTransition(
            authority, observation, proposalEntries, pullRequests);
        ValidateSourceEntries(authority, baseEntries, proposalEntries);
        var normalizedObservation = observation with
        {
            BaseTreeEntries = baseEntries,
            Coordination = observation.Coordination is null
                ? null
                : observation.Coordination with { CumulativeChangedFiles = coordinationFiles },
            Proposal = observation.Proposal is null
                ? null
                : observation.Proposal with { Entries = proposalEntries },
            PullRequests = pullRequests,
        };

        var markerBytes = GitHubPublicationCodec.CreateOwnershipMarker(authority);
        var stateBytes = GitHubPublicationCodec.CreateCoordinationState(authority, normalizedObservation);
        var markerSha = Sha256(markerBytes.AsSpan());
        var coordinationTree = ImmutableArray.Create(
            new GitHubRemoteEntryObservation(
                GitHubPublicationContract.CoordinationStatePath,
                GitHubPublicationCodec.CreateBlobOid(stateBytes.AsSpan()),
                GitHubRemoteEntryKind.Blob,
                "100644",
                Sha256(stateBytes.AsSpan())),
            new GitHubRemoteEntryObservation(
                GitHubPublicationContract.OwnershipMarkerPath,
                GitHubPublicationCodec.CreateBlobOid(markerBytes.AsSpan()),
                GitHubRemoteEntryKind.Blob,
                "100644",
                markerSha));
        var coordinationTreeOid = GitHubPublicationCodec.CreateTreeOid(coordinationTree);
        var coordinationParent = normalizedObservation.Coordination?.CommitOid
            ?? normalizedObservation.ObservedTargetCommitOid;
        var coordinationCommit = GitHubPublicationCodec.CreateCommit(
            coordinationTreeOid,
            coordinationParent,
            $"ContractScribe coordination {authority.OperationCommitmentSha256}\n",
            markerSha);

        var proposalSource = authority.Transition == GitHubPublicationTransitionKind.SameSnapshotAppend
            ? proposalEntries
            : baseEntries;
        var proposalOverlay = GitHubPublicationCodec.CreateProposalOverlay(
            proposalSource,
            authority,
            payload,
            markerBytes);
        var proposalTreeOid = GitHubPublicationCodec.CreateTreeOid(proposalOverlay);
        var proposalParent = normalizedObservation.Proposal?.CommitOid
            ?? normalizedObservation.ObservedTargetCommitOid;
        var proposalCommit = GitHubPublicationCodec.CreateCommit(
            proposalTreeOid,
            proposalParent,
            $"ContractScribe proposal {authority.OperationCommitmentSha256}\n",
            markerSha);
        var pullRequest = GitHubPublicationCodec.CreatePullRequest(authority, markerSha);

        if (authority.Transition == GitHubPublicationTransitionKind.SameSnapshotAppend)
        {
            var active = pullRequests[0];
            Require(string.Equals(active.OwnershipMarkerSha256, markerSha, StringComparison.Ordinal)
                    && string.Equals(active.HeadRef, pullRequest.HeadRef, StringComparison.Ordinal)
                    && string.Equals(active.BaseRef, authority.TargetRef, StringComparison.Ordinal)
                    && string.Equals(active.HeadOid, normalizedObservation.Proposal!.CommitOid,
                        StringComparison.Ordinal)
                    && string.Equals(active.BaseOid, normalizedObservation.ObservedTargetCommitOid,
                        StringComparison.Ordinal),
                GitHubPublicationValidationCode.InvalidCorrelation);
        }

        var commitment = GitHubPublicationCommitments.CreatePreparedRemoteOperation(
            authority,
            normalizedObservation,
            baseEntries,
            proposalEntries,
            pullRequests,
            stateBytes,
            markerBytes,
            coordinationCommit,
            proposalCommit,
            pullRequest);
        return new ValidatedGitHubPreparedRemoteOperation(
            normalizedObservation,
            baseEntries,
            proposalEntries,
            proposalOverlay,
            pullRequests,
            stateBytes,
            markerBytes,
            coordinationCommit,
            proposalCommit,
            pullRequest,
            commitment);
    }

    public static string CreateCoordinationRef(ValidatedGitHubPublicationAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var key = GitHubPublicationCodec.CreateIdentityKey(
            "coordination-ref", authority.RepositoryOwner, authority.RepositoryName,
            authority.TargetRef, authority.CampaignLineage);
        return $"refs/heads/contract-scribe/coordination/{key}";
    }

    public static string CreateProposalRef(ValidatedGitHubPublicationAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var campaign = GitHubPublicationCodec.CreateIdentityKey(
            "proposal-campaign", authority.RepositoryOwner, authority.RepositoryName,
            authority.TargetRef, authority.CampaignLineage);
        var generation = GitHubPublicationCodec.CreateIdentityKey(
            "proposal-generation", authority.CampaignLineage, authority.GenerationId,
            authority.SnapshotCommitmentSha256, authority.PolicyCommitmentSha256);
        return $"refs/heads/contract-scribe/proposals/{campaign}/{generation}";
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

    private static ImmutableArray<GitHubPrecedingChangedFileAuthority> ValidateRemoteTransition(
        ValidatedGitHubPublicationAuthority authority,
        GitHubAuthenticatedRemoteObservation observation,
        ImmutableArray<GitHubRemoteEntryObservation> proposalEntries,
        ImmutableArray<GitHubPullRequestObservation> pullRequests)
    {
        var coordinationRef = CreateCoordinationRef(authority);
        var proposalRef = CreateProposalRef(authority);
        var coordinationFiles = ImmutableArray<GitHubPrecedingChangedFileAuthority>.Empty;
        if (observation.Coordination is { } coordination)
        {
            coordinationFiles = ValidateCoordination(coordination, coordinationRef);
        }
        if (observation.Proposal is { } proposal)
        {
            Require(string.Equals(proposal.RefName, proposalRef, StringComparison.Ordinal)
                    && string.Equals(proposal.RefOid, proposal.CommitOid, StringComparison.Ordinal),
                GitHubPublicationValidationCode.InvalidCorrelation);
            RequireGitOid(proposal.RefOid, allowMissing: false);
            RequireGitOid(proposal.CommitOid, allowMissing: false);
            RequireGitOid(proposal.ParentOid, allowMissing: false);
            RequireGitOid(proposal.TreeOid, allowMissing: false);
            Require(string.Equals(GitHubPublicationCodec.CreateTreeOid(proposalEntries),
                    proposal.TreeOid, StringComparison.Ordinal),
                GitHubPublicationValidationCode.InvalidCorrelation);
        }
        foreach (var pullRequest in pullRequests)
        {
            ValidatePullRequestObservation(authority, pullRequest);
        }

        switch (authority.Transition)
        {
            case GitHubPublicationTransitionKind.Initial:
                Require(observation.Coordination is null
                        && observation.Proposal is null
                        && pullRequests.IsEmpty,
                    GitHubPublicationValidationCode.InvalidTransition);
                break;
            case GitHubPublicationTransitionKind.SameSnapshotAppend:
                Require(observation.Coordination is { } current
                        && observation.Proposal is not null
                        && pullRequests.Length == 1
                        && pullRequests[0].State == GitHubPullRequestState.DraftOpen
                        && pullRequests[0].BotOwned
                        && string.Equals(current.AuthorityCommitmentSha256,
                            authority.PrecedingAuthorityCommitmentSha256, StringComparison.Ordinal)
                        && string.Equals(current.PolicyCommitmentSha256,
                            authority.PolicyCommitmentSha256, StringComparison.Ordinal)
                        && string.Equals(current.GenerationId,
                            authority.GenerationId, StringComparison.Ordinal)
                        && string.Equals(current.OperationId,
                            authority.PrecedingOperationId, StringComparison.Ordinal)
                        && string.Equals(current.ProposalCommitOid,
                            observation.Proposal.CommitOid, StringComparison.Ordinal)
                        && string.Equals(current.ProposalParentOid,
                            observation.Proposal.ParentOid, StringComparison.Ordinal)
                        && string.Equals(current.ProposalTreeOid,
                            observation.Proposal.TreeOid, StringComparison.Ordinal)
                        && coordinationFiles.SequenceEqual(authority.PrecedingChangedFiles),
                    GitHubPublicationValidationCode.InvalidTransition);
                break;
            case GitHubPublicationTransitionKind.SuccessorAfterMerge:
            case GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged:
                Require(observation.Coordination is { } terminal
                        && authority.TerminalPredecessor is { } predecessor
                        && observation.Proposal is null
                        && pullRequests.IsEmpty
                        && string.Equals(terminal.GenerationId,
                            predecessor.GenerationId, StringComparison.Ordinal)
                        && string.Equals(terminal.ProposalCommitOid,
                            predecessor.HeadOid, StringComparison.Ordinal),
                    GitHubPublicationValidationCode.InvalidTransition);
                break;
            default:
                throw Fail(GitHubPublicationValidationCode.InvalidVocabulary);
        }
        return coordinationFiles;
    }

    private static ImmutableArray<GitHubPrecedingChangedFileAuthority> ValidateCoordination(
        GitHubCoordinationObservation observation,
        string expectedRef)
    {
        Require(string.Equals(observation.RefName, expectedRef, StringComparison.Ordinal)
                && string.Equals(observation.RefOid, observation.CommitOid, StringComparison.Ordinal),
            GitHubPublicationValidationCode.InvalidCorrelation);
        RequireGitOid(observation.RefOid, allowMissing: false);
        RequireGitOid(observation.CommitOid, allowMissing: false);
        RequireGitOid(observation.ParentOid, allowMissing: false);
        RequireGitOid(observation.TreeOid, allowMissing: false);
        RequireSha256(observation.AuthorityCommitmentSha256);
        RequireSha256(observation.PolicyCommitmentSha256);
        RequireOpaque(observation.GenerationId);
        RequireOpaque(observation.OperationId);
        RequireOptionalGitOid(observation.ProposalCommitOid);
        RequireOptionalGitOid(observation.ProposalParentOid);
        RequireOptionalGitOid(observation.ProposalTreeOid);
        var files = CollectBounded(
                observation.CumulativeChangedFiles,
                GitHubPublicationContract.MaximumChangedFiles)
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            ArgumentNullException.ThrowIfNull(file);
            ValidatePathAndHashes(file.Path, file.CandidateFileSha256, file.CandidateFileSha256);
            Require(paths.Add(file.Path), GitHubPublicationValidationCode.DuplicatePath);
            Require(folded.Add(file.Path), GitHubPublicationValidationCode.CaseCollidingPath);
        }
        return files;
    }

    private static void ValidatePullRequestObservation(
        ValidatedGitHubPublicationAuthority authority,
        GitHubPullRequestObservation observation)
    {
        Require(observation.Number > 0
                && string.Equals(observation.RepositoryOwner, authority.RepositoryOwner, StringComparison.Ordinal)
                && string.Equals(observation.RepositoryName, authority.RepositoryName, StringComparison.Ordinal)
                && IsRefName(observation.HeadRef)
                && string.Equals(observation.BaseRef, authority.TargetRef, StringComparison.Ordinal)
                && IsGitOid(observation.HeadOid, allowMissing: false)
                && IsGitOid(observation.BaseOid, allowMissing: false)
                && IsSha256(observation.OwnershipMarkerSha256)
                && Enum.IsDefined(observation.State),
            GitHubPublicationValidationCode.InvalidCorrelation);
    }

    private static void ValidateSourceEntries(
        ValidatedGitHubPublicationAuthority authority,
        ImmutableArray<GitHubRemoteEntryObservation> baseEntries,
        ImmutableArray<GitHubRemoteEntryObservation> proposalEntries)
    {
        var baseByPath = baseEntries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var proposalByPath = proposalEntries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var precedingByPath = authority.PrecedingChangedFiles.ToDictionary(
            entry => entry.Path, entry => entry.CandidateFileSha256, StringComparer.Ordinal);
        foreach (var file in authority.ChangedFiles)
        {
            Require(baseByPath.TryGetValue(file.Path, out var baseEntry),
                GitHubPublicationValidationCode.InvalidCorrelation);
            ValidateWritableBlob(baseEntry!);
            if (precedingByPath.TryGetValue(file.Path, out var precedingSha))
            {
                Require(proposalByPath.TryGetValue(file.Path, out var proposalEntry),
                    GitHubPublicationValidationCode.InvalidCorrelation);
                ValidateWritableBlob(proposalEntry!);
                Require(string.Equals(proposalEntry!.FullFileSha256, precedingSha, StringComparison.Ordinal),
                    GitHubPublicationValidationCode.InvalidCorrelation);
            }
            else
            {
                var source = authority.Transition == GitHubPublicationTransitionKind.SameSnapshotAppend
                    ? proposalByPath.GetValueOrDefault(file.Path)
                    : baseEntry;
                Require(source is not null, GitHubPublicationValidationCode.InvalidCorrelation);
                ValidateWritableBlob(source!);
                Require(string.Equals(source!.FullFileSha256,
                        file.OriginalFileSha256, StringComparison.Ordinal),
                    GitHubPublicationValidationCode.InvalidCorrelation);
            }
        }
    }

    private static void ValidateWritableBlob(GitHubRemoteEntryObservation entry) =>
        Require(entry.Kind == GitHubRemoteEntryKind.Blob && entry.Mode is "100644" or "100755",
            GitHubPublicationValidationCode.InvalidCorrelation);

    private static ImmutableArray<GitHubRemoteEntryObservation> ValidateTreeEntries(
        IEnumerable<GitHubRemoteEntryObservation> source)
    {
        var entries = CollectBounded(source, GitHubPublicationContract.MaximumRemoteTreeEntries)
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            Require(IsRepositoryPath(entry.Path), GitHubPublicationValidationCode.InvalidPath);
            Require(paths.Add(entry.Path), GitHubPublicationValidationCode.DuplicatePath);
            Require(folded.Add(entry.Path), GitHubPublicationValidationCode.CaseCollidingPath);
            RequireGitOid(entry.ObjectOid, allowMissing: false);
            RequireSha256(entry.FullFileSha256);
            Require(entry.Kind switch
            {
                GitHubRemoteEntryKind.Blob => entry.Mode is "100644" or "100755",
                GitHubRemoteEntryKind.SymbolicLink => entry.Mode == "120000",
                GitHubRemoteEntryKind.Submodule => entry.Mode == "160000",
                _ => false,
            }, GitHubPublicationValidationCode.InvalidCorrelation);
        }
        return entries;
    }

    private static void ValidateTransition(
        GitHubPublicationAuthorityInput input,
        ImmutableArray<GitHubPrecedingChangedFileAuthority> precedingFiles)
    {
        var hasPrecedingOperation = input.PrecedingOperationId is not null;
        var hasPrecedingAuthority = input.PrecedingAuthorityCommitmentSha256 is not null;
        var hasPrecedingCandidate = input.PrecedingCandidateCommitmentSha256 is not null;
        if (hasPrecedingOperation)
        {
            RequireOpaque(input.PrecedingOperationId!);
        }
        if (hasPrecedingCandidate)
        {
            RequireSha256(input.PrecedingCandidateCommitmentSha256!);
        }
        if (hasPrecedingAuthority)
        {
            RequireSha256(input.PrecedingAuthorityCommitmentSha256!);
        }
        if (input.TerminalPredecessor is { } predecessor)
        {
            ValidatePredecessor(predecessor);
            Require(!string.Equals(predecessor.GenerationId, input.GenerationId, StringComparison.Ordinal),
                GitHubPublicationValidationCode.InvalidTransition);
        }

        switch (input.Transition)
        {
            case GitHubPublicationTransitionKind.Initial:
                Require(!hasPrecedingOperation && !hasPrecedingAuthority && !hasPrecedingCandidate
                        && precedingFiles.IsEmpty && input.TerminalPredecessor is null
                        && input.ClosedUnmergedSuccessorAuthorization is null,
                    GitHubPublicationValidationCode.InvalidTransition);
                break;
            case GitHubPublicationTransitionKind.SameSnapshotAppend:
                Require(hasPrecedingOperation && hasPrecedingAuthority && hasPrecedingCandidate
                        && !precedingFiles.IsEmpty && input.TerminalPredecessor is null
                        && input.ClosedUnmergedSuccessorAuthorization is null,
                    GitHubPublicationValidationCode.InvalidTransition);
                break;
            case GitHubPublicationTransitionKind.SuccessorAfterMerge:
                Require(!hasPrecedingOperation && !hasPrecedingAuthority && !hasPrecedingCandidate
                        && precedingFiles.IsEmpty
                        && input.TerminalPredecessor?.Disposition == GitHubPublicationPredecessorDisposition.Merged
                        && input.ClosedUnmergedSuccessorAuthorization is null,
                    GitHubPublicationValidationCode.InvalidTransition);
                break;
            case GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged:
                Require(!hasPrecedingOperation && !hasPrecedingAuthority && !hasPrecedingCandidate
                        && precedingFiles.IsEmpty
                        && input.TerminalPredecessor?.Disposition ==
                            GitHubPublicationPredecessorDisposition.ClosedUnmerged
                        && input.ClosedUnmergedSuccessorAuthorization is not null,
                    GitHubPublicationValidationCode.InvalidTransition);
                ValidateClosedAuthorization(
                    input,
                    input.TerminalPredecessor!,
                    input.ClosedUnmergedSuccessorAuthorization!);
                break;
            default:
                throw Fail(GitHubPublicationValidationCode.InvalidVocabulary);
        }
    }

    private static void ValidatePredecessor(GitHubPublicationPredecessorAuthority predecessor)
    {
        RequireOpaque(predecessor.LogicalPredecessorId);
        Require(predecessor.PullRequestNumber > 0, GitHubPublicationValidationCode.InvalidTransition);
        RequireOpaque(predecessor.GenerationId);
        RequireGitOid(predecessor.HeadOid, allowMissing: false);
        Require(Enum.IsDefined(predecessor.Disposition), GitHubPublicationValidationCode.InvalidVocabulary);
    }

    private static void ValidateClosedAuthorization(
        GitHubPublicationAuthorityInput input,
        GitHubPublicationPredecessorAuthority predecessor,
        GitHubClosedUnmergedSuccessorAuthorization authorization)
    {
        RequireOpaque(authorization.AuthorizationId);
        RequireOpaque(authorization.LogicalPredecessorId);
        Require(authorization.ClosedPullRequestNumber > 0,
            GitHubPublicationValidationCode.InvalidAuthorization);
        RequireOpaque(authorization.ClosedGenerationId);
        RequireGitOid(authorization.ClosedHeadOid, allowMissing: false);
        RequireSha256(authorization.FreshSnapshotCommitmentSha256);
        RequireSha256(authorization.FreshWorkPlanCommitmentSha256);
        RequireSha256(authorization.FreshCandidateCommitmentSha256);
        RequireOpaque(authorization.NewGenerationId);
        RequireOpaque(authorization.OperationId);
        Require(string.Equals(authorization.LogicalPredecessorId,
                    predecessor.LogicalPredecessorId, StringComparison.Ordinal)
                && authorization.ClosedPullRequestNumber == predecessor.PullRequestNumber
                && string.Equals(authorization.ClosedGenerationId,
                    predecessor.GenerationId, StringComparison.Ordinal)
                && string.Equals(authorization.ClosedHeadOid,
                    predecessor.HeadOid, StringComparison.Ordinal)
                && !string.Equals(authorization.ClosedGenerationId,
                    authorization.NewGenerationId, StringComparison.Ordinal)
                && string.Equals(authorization.FreshSnapshotCommitmentSha256,
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

    private static void ValidatePolicy(
        GitHubPublicationM4Ceilings accepted,
        GitHubPublicationPolicy requested)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(requested);
        Require(accepted.MaximumDocumentationBlocks is > 0
                and <= CampaignStateContract.MaximumActivePatchBlocks
                && accepted.MaximumDistinctChangedFiles is > 0
                and <= CampaignStateContract.MaximumChangedFiles
                && accepted.MaximumCumulativePatchBytes is > 0
                and <= CampaignStateContract.MaximumPatchBytes,
            GitHubPublicationValidationCode.InvalidPolicy);
        Require(requested.MaximumDocumentationBlocks is > 0
                && requested.MaximumDocumentationBlocks <= accepted.MaximumDocumentationBlocks
                && requested.MaximumDistinctChangedFiles is > 0
                && requested.MaximumDistinctChangedFiles <= accepted.MaximumDistinctChangedFiles
                && requested.MaximumCumulativePatchBytes is > 0
                && requested.MaximumCumulativePatchBytes <= accepted.MaximumCumulativePatchBytes,
            GitHubPublicationValidationCode.InvalidPolicy);
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
        value is not null && value.Length == length
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

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
        Require(value >= 0, GitHubPublicationValidationCode.InvalidBound);
    private static void Require(
        [DoesNotReturnIf(false)] bool condition,
        GitHubPublicationValidationCode code)
    {
        if (!condition)
        {
            throw Fail(code);
        }
    }
    private static GitHubPublicationValidationException Fail(GitHubPublicationValidationCode code) =>
        new(code, "GitHub publication input violates a closed contract invariant.");
}

internal static class GitHubPublicationCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string CreateIdentityKey(string domain, params string[] values)
    {
        using var writer = new GitHubPublicationCommitmentWriter(
            $"contract-scribe/github-{domain}/v1");
        foreach (var value in values)
        {
            writer.Add("value", value);
        }
        return writer.Complete();
    }

    public static ImmutableArray<byte> CreateOwnershipMarker(
        ValidatedGitHubPublicationAuthority authority) => ToImmutable(StrictUtf8.GetBytes(
            "contract-scribe-publication-v1\n"
            + $"campaign={authority.CampaignLineage}\n"
            + $"generation={authority.GenerationId}\n"
            + $"policy={authority.PolicyCommitmentSha256}\n"));

    public static ImmutableArray<byte> CreateCoordinationState(
        ValidatedGitHubPublicationAuthority authority,
        GitHubAuthenticatedRemoteObservation observation)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            json.WriteStartObject();
            json.WriteNumber("version", GitHubPublicationContract.Version);
            json.WriteString("authority", authority.AuthorityCommitmentSha256);
            json.WriteString("operation", authority.OperationCommitmentSha256);
            json.WriteString("policy", authority.PolicyCommitmentSha256);
            json.WriteString("repository", observation.CanonicalRepositoryId);
            json.WriteString("targetRef", authority.TargetRef);
            json.WriteString("targetCommit", observation.ObservedTargetCommitOid);
            json.WriteString("baseTree", observation.ObservedBaseTreeOid);
            json.WriteString("generation", authority.GenerationId);
            json.WriteString("transition", authority.Transition.ToString());
            json.WriteString("coordinationPredecessor",
                observation.Coordination?.CommitOid ?? GitHubPublicationContract.MissingGitObjectId);
            json.WriteString("proposalPredecessor",
                observation.Proposal?.CommitOid ?? GitHubPublicationContract.MissingGitObjectId);
            json.WriteNumber("changedFileCount", authority.ChangedFiles.Length);
            json.WriteNumber("documentationBlocks", authority.CumulativeDocumentationBlocks);
            json.WriteNumber("patchBytes", authority.CumulativePatchBytes);
            json.WriteStartArray("changedFiles");
            foreach (var file in authority.ChangedFiles)
            {
                json.WriteStartObject();
                json.WriteString("path", file.Path);
                json.WriteString("candidateSha256", file.CandidateFileSha256);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteString("status", "prepared");
            json.WriteEndObject();
        }
        var bytes = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(bytes);
        bytes[^1] = (byte)'\n';
        return ToImmutable(bytes);
    }

    public static GitHubDeterministicCommitPayload CreateCommit(
        string treeOid,
        string parentOid,
        string message,
        string ownershipMarkerSha256)
    {
        var text = $"tree {treeOid}\nparent {parentOid}\n"
            + $"author {GitHubPublicationContract.CommitActorName} <{GitHubPublicationContract.CommitActorEmail}> {GitHubPublicationContract.CommitTimestampSeconds} +0000\n"
            + $"committer {GitHubPublicationContract.CommitActorName} <{GitHubPublicationContract.CommitActorEmail}> {GitHubPublicationContract.CommitTimestampSeconds} +0000\n\n"
            + message;
        var bytes = ToImmutable(StrictUtf8.GetBytes(text));
        return new GitHubDeterministicCommitPayload(
            treeOid,
            message,
            parentOid,
            GitHubPublicationContract.CommitActorName,
            GitHubPublicationContract.CommitActorEmail,
            GitHubPublicationContract.CommitTimestampSeconds,
            GitHubPublicationContract.CommitActorName,
            GitHubPublicationContract.CommitActorEmail,
            GitHubPublicationContract.CommitTimestampSeconds,
            ownershipMarkerSha256,
            bytes,
            CreateObjectOid("commit", bytes.AsSpan()));
    }

    public static GitHubDeterministicPullRequestPayload CreatePullRequest(
        ValidatedGitHubPublicationAuthority authority,
        string ownershipMarkerSha256)
    {
        var title = $"ContractScribe documentation generation {authority.GenerationId}";
        var body = "<!-- contract-scribe-publication-v1\n"
            + $"campaign={authority.CampaignLineage}\n"
            + $"generation={authority.GenerationId}\n"
            + $"snapshot={authority.SnapshotCommitmentSha256}\n"
            + $"policy={authority.PolicyCommitmentSha256}\n"
            + $"operation={authority.OperationCommitmentSha256}\n"
            + $"marker={ownershipMarkerSha256}\n-->";
        return new GitHubDeterministicPullRequestPayload(
            GitHubPublicationFactory.CreateProposalRef(authority),
            authority.TargetRef,
            title,
            body,
            ownershipMarkerSha256,
            Draft: true,
            MaintainerCanModify: false);
    }

    public static ImmutableArray<GitHubRemoteEntryObservation> CreateProposalOverlay(
        ImmutableArray<GitHubRemoteEntryObservation> source,
        ValidatedGitHubPublicationAuthority authority,
        ValidatedGitHubChangedFilePayload payload,
        ImmutableArray<byte> markerBytes)
    {
        var entries = source.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var markerSha = Convert.ToHexString(SHA256.HashData(markerBytes.AsSpan())).ToLowerInvariant();
        if (entries.ContainsKey(GitHubPublicationContract.CoordinationStatePath)
            || (entries.TryGetValue(GitHubPublicationContract.OwnershipMarkerPath, out var marker)
                && (authority.Transition != GitHubPublicationTransitionKind.SameSnapshotAppend
                    || marker.Kind != GitHubRemoteEntryKind.Blob
                    || marker.Mode != "100644"
                    || !string.Equals(marker.FullFileSha256, markerSha, StringComparison.Ordinal))))
        {
            throw new GitHubPublicationValidationException(
                GitHubPublicationValidationCode.InvalidCorrelation,
                "Reserved publication paths already exist.");
        }
        foreach (var file in payload.Files)
        {
            if (!entries.TryGetValue(file.Path, out var sourceEntry))
            {
                throw new GitHubPublicationValidationException(
                    GitHubPublicationValidationCode.InvalidCorrelation,
                    "Publication source entry is missing.");
            }
            var bytes = file.CandidateBytes.AsSpan();
            entries[file.Path] = sourceEntry with
            {
                ObjectOid = CreateBlobOid(bytes),
                Kind = GitHubRemoteEntryKind.Blob,
                FullFileSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            };
        }
        entries[GitHubPublicationContract.OwnershipMarkerPath] = new(
            GitHubPublicationContract.OwnershipMarkerPath,
            CreateBlobOid(markerBytes.AsSpan()),
            GitHubRemoteEntryKind.Blob,
            "100644",
            markerSha);
        return entries.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToImmutableArray();
    }

    public static string CreateBlobOid(ReadOnlySpan<byte> bytes) => CreateObjectOid("blob", bytes);

    public static string CreateTreeOid(ImmutableArray<GitHubRemoteEntryObservation> entries)
    {
        var root = new TreeNode();
        foreach (var entry in entries)
        {
            root.Add(entry.Path.Split('/'), 0, entry);
        }
        return root.CreateOid();
    }

    private static string CreateObjectOid(string kind, ReadOnlySpan<byte> body)
    {
        var header = Encoding.ASCII.GetBytes($"{kind} {body.Length}\0");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(header);
        hash.AppendData(body);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static ImmutableArray<byte> ToImmutable(byte[] bytes) =>
        ImmutableCollectionsMarshal.AsImmutableArray(bytes);

    private sealed class TreeNode
    {
        private readonly Dictionary<string, TreeNode> directories = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GitHubRemoteEntryObservation> files = new(StringComparer.Ordinal);

        public void Add(string[] segments, int index, GitHubRemoteEntryObservation entry)
        {
            var name = segments[index];
            if (index == segments.Length - 1)
            {
                if (directories.ContainsKey(name) || !files.TryAdd(name, entry))
                {
                    throw new GitHubPublicationValidationException(
                        GitHubPublicationValidationCode.InvalidPath,
                        "Git tree path conflicts with another entry.");
                }
                return;
            }
            if (files.ContainsKey(name))
            {
                throw new GitHubPublicationValidationException(
                    GitHubPublicationValidationCode.InvalidPath,
                    "Git tree path conflicts with another entry.");
            }
            if (!directories.TryGetValue(name, out var child))
            {
                child = new TreeNode();
                directories.Add(name, child);
            }
            child.Add(segments, index + 1, entry);
        }

        public string CreateOid()
        {
            var encoded = new List<byte>();
            var items = files.Select(pair => new TreeItem(
                    pair.Key, pair.Value.Mode, pair.Value.ObjectOid, IsDirectory: false))
                .Concat(directories.Select(pair => new TreeItem(
                    pair.Key, "40000", pair.Value.CreateOid(), IsDirectory: true)))
                .OrderBy(item => item.Name + (item.IsDirectory ? "/" : string.Empty), StringComparer.Ordinal);
            foreach (var item in items)
            {
                encoded.AddRange(Encoding.ASCII.GetBytes(item.Mode));
                encoded.Add((byte)' ');
                encoded.AddRange(StrictUtf8.GetBytes(item.Name));
                encoded.Add(0);
                encoded.AddRange(Convert.FromHexString(item.Oid));
            }
            return CreateObjectOid("tree", CollectionsMarshal.AsSpan(encoded));
        }

        private sealed record TreeItem(string Name, string Mode, string Oid, bool IsDirectory);
    }
}

internal static class GitHubPublicationCommitments
{
    public static string CreatePolicy(
        GitHubPublicationM4Ceilings accepted,
        GitHubPublicationPolicy requested)
    {
        using var writer = new GitHubPublicationCommitmentWriter(
            "contract-scribe/github-publication-policy/v1");
        writer.Add("m4.maximum-documentation-blocks", accepted.MaximumDocumentationBlocks);
        writer.Add("m4.maximum-distinct-changed-files", accepted.MaximumDistinctChangedFiles);
        writer.Add("m4.maximum-cumulative-patch-bytes", accepted.MaximumCumulativePatchBytes);
        writer.Add("m5.maximum-documentation-blocks", requested.MaximumDocumentationBlocks);
        writer.Add("m5.maximum-distinct-changed-files", requested.MaximumDistinctChangedFiles);
        writer.Add("m5.maximum-cumulative-patch-bytes", requested.MaximumCumulativePatchBytes);
        writer.Add("counting", "complete-candidate-candidate-documentation-byte-count");
        writer.Add("generation-lifetime", "immutable");
        writer.Add("closed-unmerged", "terminal-no-automatic-retry");
        return writer.Complete();
    }

    public static string CreateAuthority(
        GitHubPublicationAuthorityInput input,
        ImmutableArray<GitHubChangedFileAuthority> files,
        ImmutableArray<GitHubPrecedingChangedFileAuthority> precedingFiles,
        int cumulativeBlocks,
        long cumulativePatchBytes,
        string policyCommitment)
    {
        using var writer = new GitHubPublicationCommitmentWriter(
            "contract-scribe/github-publication-authority/v1");
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
            writer.Add("file.blocks", file.ChangedDocumentationBlockCount);
            writer.Add("file.original-documentation-bytes", file.OriginalDocumentationByteCount);
            writer.Add("file.candidate-documentation-bytes", file.CandidateDocumentationByteCount);
            writer.Add("file.original-documentation-lines", file.OriginalDocumentationLineCount);
            writer.Add("file.candidate-documentation-lines", file.CandidateDocumentationLineCount);
        }
        writer.Add("preceding-files.count", precedingFiles.Length);
        foreach (var file in precedingFiles)
        {
            writer.Add("preceding-file.path", file.Path);
            writer.Add("preceding-file.candidate-sha256", file.CandidateFileSha256);
        }
        AddPredecessor(writer, input.TerminalPredecessor);
        AddAuthorization(writer, input.ClosedUnmergedSuccessorAuthorization);
        return writer.Complete();
    }

    public static string CreateOperation(
        GitHubPublicationAuthorityInput input,
        string authorityCommitment,
        string policyCommitment)
    {
        using var writer = new GitHubPublicationCommitmentWriter(
            "contract-scribe/github-publication-operation/v1");
        writer.Add("authority-commitment", authorityCommitment);
        writer.Add("policy-commitment", policyCommitment);
        writer.Add("operation-id", input.OperationId);
        writer.Add("generation-id", input.GenerationId);
        writer.AddOptional("preceding-operation-id", input.PrecedingOperationId);
        writer.AddOptional("preceding-authority", input.PrecedingAuthorityCommitmentSha256);
        writer.Add("transition", TransitionId(input.Transition));
        return writer.Complete();
    }

    public static string CreatePreparedRemoteOperation(
        ValidatedGitHubPublicationAuthority authority,
        GitHubAuthenticatedRemoteObservation observation,
        ImmutableArray<GitHubRemoteEntryObservation> baseEntries,
        ImmutableArray<GitHubRemoteEntryObservation> proposalEntries,
        ImmutableArray<GitHubPullRequestObservation> pullRequests,
        ImmutableArray<byte> stateBytes,
        ImmutableArray<byte> markerBytes,
        GitHubDeterministicCommitPayload coordinationCommit,
        GitHubDeterministicCommitPayload proposalCommit,
        GitHubDeterministicPullRequestPayload pullRequest)
    {
        using var writer = new GitHubPublicationCommitmentWriter(
            "contract-scribe/github-prepared-remote-operation/v1");
        writer.Add("authority", authority.AuthorityCommitmentSha256);
        writer.Add("operation", authority.OperationCommitmentSha256);
        writer.Add("repository-id", observation.CanonicalRepositoryId);
        writer.Add("target", observation.ObservedTargetCommitOid);
        writer.Add("base-tree", observation.ObservedBaseTreeOid);
        AddEntries(writer, "base", baseEntries);
        writer.Add("coordination.present", observation.Coordination is not null);
        if (observation.Coordination is { } coordination)
        {
            writer.Add("coordination.ref", coordination.RefName);
            writer.Add("coordination.commit", coordination.CommitOid);
            writer.Add("coordination.parent", coordination.ParentOid);
            writer.Add("coordination.tree", coordination.TreeOid);
            writer.Add("coordination.authority", coordination.AuthorityCommitmentSha256);
            writer.Add("coordination.policy", coordination.PolicyCommitmentSha256);
            writer.Add("coordination.generation", coordination.GenerationId);
            writer.Add("coordination.operation", coordination.OperationId);
            writer.AddOptional("coordination.proposal-commit", coordination.ProposalCommitOid);
            writer.AddOptional("coordination.proposal-parent", coordination.ProposalParentOid);
            writer.AddOptional("coordination.proposal-tree", coordination.ProposalTreeOid);
            var cumulativeFiles = coordination.CumulativeChangedFiles
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .ToImmutableArray();
            writer.Add("coordination.changed-files.count", cumulativeFiles.Length);
            foreach (var file in cumulativeFiles)
            {
                writer.Add("coordination.changed-file.path", file.Path);
                writer.Add("coordination.changed-file.sha256", file.CandidateFileSha256);
            }
        }
        writer.Add("proposal.present", observation.Proposal is not null);
        if (observation.Proposal is { } proposal)
        {
            writer.Add("proposal.ref", proposal.RefName);
            writer.Add("proposal.commit", proposal.CommitOid);
            writer.Add("proposal.parent", proposal.ParentOid);
            writer.Add("proposal.tree", proposal.TreeOid);
        }
        AddEntries(writer, "proposal-observed", proposalEntries);
        writer.Add("pull-requests.count", pullRequests.Length);
        foreach (var pr in pullRequests)
        {
            writer.Add("pr.number", pr.Number);
            writer.Add("pr.head", pr.HeadRef);
            writer.Add("pr.head-oid", pr.HeadOid);
            writer.Add("pr.base", pr.BaseRef);
            writer.Add("pr.base-oid", pr.BaseOid);
            writer.Add("pr.marker", pr.OwnershipMarkerSha256);
            writer.Add("pr.state", pr.State.ToString());
            writer.Add("pr.bot-owned", pr.BotOwned);
        }
        writer.Add("coordination-state-sha256", Sha256(stateBytes.AsSpan()));
        writer.Add("marker-sha256", Sha256(markerBytes.AsSpan()));
        AddCommit(writer, "coordination", coordinationCommit);
        AddCommit(writer, "proposal", proposalCommit);
        writer.Add("request.head", pullRequest.HeadRef);
        writer.Add("request.base", pullRequest.BaseRef);
        writer.Add("request.title", pullRequest.Title);
        writer.Add("request.body", pullRequest.Body);
        writer.Add("request.marker", pullRequest.OwnershipMarkerSha256);
        writer.Add("request.draft", pullRequest.Draft);
        writer.Add("request.maintainer-can-modify", pullRequest.MaintainerCanModify);
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
        writer.AddOptional("preceding-operation-id", input.PrecedingOperationId);
        writer.AddOptional("preceding-authority", input.PrecedingAuthorityCommitmentSha256);
        writer.AddOptional("preceding-candidate", input.PrecedingCandidateCommitmentSha256);
        writer.Add("transition", TransitionId(input.Transition));
    }

    private static void AddPredecessor(
        GitHubPublicationCommitmentWriter writer,
        GitHubPublicationPredecessorAuthority? predecessor)
    {
        writer.Add("predecessor.present", predecessor is not null);
        if (predecessor is null)
        {
            return;
        }
        writer.Add("predecessor.id", predecessor.LogicalPredecessorId);
        writer.Add("predecessor.pr", predecessor.PullRequestNumber);
        writer.Add("predecessor.generation", predecessor.GenerationId);
        writer.Add("predecessor.head", predecessor.HeadOid);
        writer.Add("predecessor.disposition", predecessor.Disposition.ToString());
    }

    private static void AddAuthorization(
        GitHubPublicationCommitmentWriter writer,
        GitHubClosedUnmergedSuccessorAuthorization? authorization)
    {
        writer.Add("closed-authorization.present", authorization is not null);
        if (authorization is null)
        {
            return;
        }
        writer.Add("closed-authorization.id", authorization.AuthorizationId);
        writer.Add("closed-authorization.predecessor", authorization.LogicalPredecessorId);
        writer.Add("closed-authorization.pr", authorization.ClosedPullRequestNumber);
        writer.Add("closed-authorization.generation", authorization.ClosedGenerationId);
        writer.Add("closed-authorization.head", authorization.ClosedHeadOid);
        writer.Add("closed-authorization.snapshot", authorization.FreshSnapshotCommitmentSha256);
        writer.Add("closed-authorization.work-plan", authorization.FreshWorkPlanCommitmentSha256);
        writer.Add("closed-authorization.candidate", authorization.FreshCandidateCommitmentSha256);
        writer.Add("closed-authorization.new-generation", authorization.NewGenerationId);
        writer.Add("closed-authorization.operation", authorization.OperationId);
    }

    private static void AddEntries(
        GitHubPublicationCommitmentWriter writer,
        string prefix,
        ImmutableArray<GitHubRemoteEntryObservation> entries)
    {
        writer.Add(prefix + ".count", entries.Length);
        foreach (var entry in entries)
        {
            writer.Add(prefix + ".path", entry.Path);
            writer.Add(prefix + ".oid", entry.ObjectOid);
            writer.Add(prefix + ".kind", entry.Kind.ToString());
            writer.Add(prefix + ".mode", entry.Mode);
            writer.Add(prefix + ".sha256", entry.FullFileSha256);
        }
    }

    private static void AddCommit(
        GitHubPublicationCommitmentWriter writer,
        string prefix,
        GitHubDeterministicCommitPayload payload)
    {
        writer.Add(prefix + ".tree", payload.TreeOid);
        writer.Add(prefix + ".message", payload.Message);
        writer.Add(prefix + ".parent", payload.ParentOid);
        writer.Add(prefix + ".author-name", payload.AuthorName);
        writer.Add(prefix + ".author-email", payload.AuthorEmail);
        writer.Add(prefix + ".author-time", payload.AuthorTimestampSeconds);
        writer.Add(prefix + ".committer-name", payload.CommitterName);
        writer.Add(prefix + ".committer-email", payload.CommitterEmail);
        writer.Add(prefix + ".committer-time", payload.CommitterTimestampSeconds);
        writer.Add(prefix + ".marker", payload.OwnershipMarkerSha256);
        writer.Add(prefix + ".bytes-sha256", Sha256(payload.ExactCommitBytes.AsSpan()));
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

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
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
