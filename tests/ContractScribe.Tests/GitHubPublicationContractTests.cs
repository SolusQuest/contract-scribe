using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class GitHubPublicationContractTests
{
    private static readonly byte[] CandidateBytes = Encoding.UTF8.GetBytes("candidate\n");

    [Fact]
    public void Known_answer_commitments_and_ref_grammar_are_frozen()
    {
        var authority = GitHubPublicationFactory.CreateAuthority(CreateInput());
        using var fixture = ReadFixture("known-answer-v1.json");
        var root = fixture.RootElement;

        Assert.Equal(root.GetProperty("policyCommitmentSha256").GetString(),
            authority.PolicyCommitmentSha256);
        Assert.Equal(root.GetProperty("authorityCommitmentSha256").GetString(),
            authority.AuthorityCommitmentSha256);
        Assert.Equal(root.GetProperty("operationCommitmentSha256").GetString(),
            authority.OperationCommitmentSha256);
        Assert.Equal(root.GetProperty("coordinationRef").GetString(),
            GitHubPublicationFactory.CreateCoordinationRef(Hex('a')));
        Assert.Equal(root.GetProperty("proposalRef").GetString(),
            GitHubPublicationFactory.CreateProposalRef(Hex('a'), Hex('b')));
    }

    [Fact]
    public void Authority_is_order_stable_and_every_remote_selecting_fact_is_committed()
    {
        var first = Changed("docs/a.md", Encoding.UTF8.GetBytes("a"));
        var second = Changed("docs/b.md", Encoding.UTF8.GetBytes("b"));
        var ordered = GitHubPublicationFactory.CreateAuthority(CreateInput([first, second]));
        var reversed = GitHubPublicationFactory.CreateAuthority(CreateInput([second, first]));

        Assert.Equal(ordered.AuthorityCommitmentSha256, reversed.AuthorityCommitmentSha256);
        Assert.Equal(ordered.OperationCommitmentSha256, reversed.OperationCommitmentSha256);
        Assert.Equal(["docs/a.md", "docs/b.md"], ordered.ChangedFiles.Select(file => file.Path));

        var baseline = GitHubPublicationFactory.CreateAuthority(CreateInput());
        var mutations = new[]
        {
            CreateInput() with { RepositoryName = "contract-scribe-next" },
            CreateInput() with { TargetRef = "refs/heads/release" },
            CreateInput() with { ExpectedBaseCommitOid = GitOid('2') },
            CreateInput() with { SnapshotCommitmentSha256 = Hex('b') },
            CreateInput() with { WorkPlanCommitmentSha256 = Hex('d') },
            CreateInput() with { CheckpointRevision = 8 },
            CreateInput() with { CandidateCommitmentSha256 = Hex('e') },
            CreateInput() with { PatchRequestSha256 = Hex('f') },
            CreateInput() with { OperationId = "operation-2" },
            CreateInput() with { GenerationId = "generation-2" },
            CreateInput() with { Policy = new(9, 10, 1_000) },
            CreateInput([Changed("docs/readme.md", Encoding.UTF8.GetBytes("changed\n"))]),
        };
        Assert.All(mutations, mutation => Assert.NotEqual(
            baseline.OperationCommitmentSha256,
            GitHubPublicationFactory.CreateAuthority(mutation).OperationCommitmentSha256));
    }

    [Fact]
    public void Policy_uses_the_complete_M4_candidate_measure_and_fails_closed()
    {
        var bytes = Encoding.UTF8.GetBytes("candidate\n");
        var file = Changed("docs/readme.md", bytes) with
        {
            CandidateDocumentationByteCount = 10,
        };
        var exact = GitHubPublicationFactory.CreateAuthority(CreateInput([file]) with
        {
            Policy = new GitHubPublicationPolicy(1, 1, 10),
        });
        Assert.Equal(10, exact.CumulativePatchBytes);

        var over = Assert.Throws<GitHubPublicationValidationException>(() =>
            GitHubPublicationFactory.CreateAuthority(CreateInput([file]) with
            {
                Policy = new GitHubPublicationPolicy(1, 1, 9),
            }));
        Assert.Equal(GitHubPublicationValidationCode.InvalidPolicy, over.Code);

        var complete = GitHubPublicationFactory.CreateAuthority(CreateInput([
            file with { Path = "docs/a.md", CandidateFileSha256 = Sha(Encoding.UTF8.GetBytes("a")) },
            file with { Path = "docs/b.md", CandidateFileSha256 = Sha(Encoding.UTF8.GetBytes("b")) },
        ]) with
        {
            Policy = new GitHubPublicationPolicy(2, 2, 20),
        });
        Assert.Equal(20, complete.CumulativePatchBytes);

        var overflow = Assert.Throws<GitHubPublicationValidationException>(() =>
            GitHubPublicationFactory.CreateAuthority(CreateInput([
                file with { Path = "docs/a.md", ChangedDocumentationBlockCount = int.MaxValue },
                file with { Path = "docs/b.md", ChangedDocumentationBlockCount = int.MaxValue },
            ])));
        Assert.Equal(GitHubPublicationValidationCode.ArithmeticOverflow, overflow.Code);
    }

    [Theory]
    [InlineData("../secret.md", GitHubPublicationValidationCode.InvalidPath)]
    [InlineData("/absolute.md", GitHubPublicationValidationCode.InvalidPath)]
    [InlineData("docs\\readme.md", GitHubPublicationValidationCode.InvalidPath)]
    public void Paths_fail_closed_before_payload_or_network(
        string path,
        GitHubPublicationValidationCode code)
    {
        var failure = Assert.Throws<GitHubPublicationValidationException>(() =>
            GitHubPublicationFactory.CreateAuthority(CreateInput([Changed(path, CandidateBytes)])));
        Assert.Equal(code, failure.Code);
    }

    [Fact]
    public void Duplicate_case_colliding_and_payload_path_sets_fail_closed()
    {
        var duplicate = Assert.Throws<GitHubPublicationValidationException>(() =>
            GitHubPublicationFactory.CreateAuthority(CreateInput([
                Changed("docs/a.md", CandidateBytes),
                Changed("docs/a.md", CandidateBytes),
            ])));
        Assert.Equal(GitHubPublicationValidationCode.DuplicatePath, duplicate.Code);

        var collision = Assert.Throws<GitHubPublicationValidationException>(() =>
            GitHubPublicationFactory.CreateAuthority(CreateInput([
                Changed("docs/a.md", CandidateBytes),
                Changed("DOCS/A.md", CandidateBytes),
            ])));
        Assert.Equal(GitHubPublicationValidationCode.CaseCollidingPath, collision.Code);

        var authority = GitHubPublicationFactory.CreateAuthority(CreateInput());
        var missing = Assert.Throws<GitHubPublicationValidationException>(() =>
            GitHubPublicationFactory.CreatePayload(authority, []));
        Assert.Equal(GitHubPublicationValidationCode.PayloadMismatch, missing.Code);
        var extra = Assert.Throws<GitHubPublicationValidationException>(() =>
            GitHubPublicationFactory.CreatePayload(authority, [
                new("docs/readme.md", CandidateBytes),
                new("docs/extra.md", CandidateBytes),
            ]));
        Assert.Equal(GitHubPublicationValidationCode.PayloadMismatch, extra.Code);
    }

    [Fact]
    public void Payload_is_hash_correlated_defensively_copied_and_not_rendered()
    {
        var bytes = CandidateBytes.ToArray();
        var authority = GitHubPublicationFactory.CreateAuthority(CreateInput());
        var payload = GitHubPublicationFactory.CreatePayload(authority,
            [new GitHubChangedFilePayloadInput("docs/readme.md", bytes)]);
        bytes[0] ^= 0xff;

        Assert.True(payload.Files[0].CandidateBytes.AsSpan().SequenceEqual(CandidateBytes));
        Assert.Equal(authority.AuthorityCommitmentSha256, payload.AuthorityCommitmentSha256);
        Assert.Equal(nameof(ValidatedGitHubChangedFilePayload), payload.ToString());
        Assert.DoesNotContain("candidate", payload.ToString(), StringComparison.Ordinal);

        var mismatch = Assert.Throws<GitHubPublicationValidationException>(() =>
            GitHubPublicationFactory.CreatePayload(authority,
                [new GitHubChangedFilePayloadInput("docs/readme.md", bytes)]));
        Assert.Equal(GitHubPublicationValidationCode.PayloadMismatch, mismatch.Code);
    }

    [Fact]
    public void Transition_and_closed_successor_authority_are_exact_and_non_reusable()
    {
        var append = CreateInput() with
        {
            Transition = GitHubPublicationTransitionKind.SameSnapshotAppend,
            LogicalPredecessorId = "operation-previous",
            PrecedingCandidateCommitmentSha256 = Hex('a'),
            ChangedFiles = [Changed("docs/readme.md", CandidateBytes) with
            {
                PrecedingCandidateFileSha256 = Hex('b'),
            }],
        };
        Assert.NotNull(GitHubPublicationFactory.CreateAuthority(append));
        Assert.Equal(GitHubPublicationValidationCode.InvalidTransition,
            Assert.Throws<GitHubPublicationValidationException>(() =>
                GitHubPublicationFactory.CreateAuthority(append with
                {
                    LogicalPredecessorId = null,
                })).Code);

        var closed = CreateClosedSuccessorInput();
        Assert.NotNull(GitHubPublicationFactory.CreateAuthority(closed));
        Assert.Equal(GitHubPublicationValidationCode.InvalidAuthorization,
            Assert.Throws<GitHubPublicationValidationException>(() =>
                GitHubPublicationFactory.CreateAuthority(closed with
                {
                    OperationId = "different-operation",
                })).Code);
        Assert.Equal(GitHubPublicationValidationCode.InvalidTransition,
            Assert.Throws<GitHubPublicationValidationException>(() =>
                GitHubPublicationFactory.CreateAuthority(CreateInput() with
                {
                    ClosedUnmergedSuccessorAuthorization =
                        closed.ClosedUnmergedSuccessorAuthorization,
                })).Code);
    }

    [Fact]
    public void Caller_authority_cannot_accept_authenticated_remote_observations()
    {
        var properties = typeof(GitHubPublicationAuthorityInput).GetProperties()
            .Select(property => property.Name)
            .ToArray();
        var forbidden = new[]
        {
            "BaseTree", "EntryMode", "EntryType", "CoordinationRefOid",
            "ProposalRefOid", "ProposalCommit", "ProposalTree", "ActivePullRequest",
        };
        Assert.All(forbidden, fragment => Assert.DoesNotContain(properties,
            property => property.Contains(fragment, StringComparison.Ordinal)));

        var authority = GitHubPublicationFactory.CreateAuthority(CreateInput());
        Assert.Equal(GitOid('1'), authority.ExpectedBaseCommitOid);
    }

    [Fact]
    public void Authenticated_remote_preparation_checks_bytes_types_modes_and_commits_every_observation()
    {
        var authority = GitHubPublicationFactory.CreateAuthority(CreateInput());
        var observation = CreateRemoteObservation(authority);
        var coordination = CreateCommit('3');
        var proposal = CreateCommit('4');
        var pullRequest = CreatePullRequest();
        var prepared = GitHubPublicationFactory.PrepareRemoteOperation(
            authority, observation, coordination, proposal, pullRequest);
        var changedTree = GitHubPublicationFactory.PrepareRemoteOperation(
            authority, observation with { ObservedBaseTreeOid = GitOid('9') },
            coordination, proposal, pullRequest);

        Assert.NotEqual(prepared.CommitmentSha256, changedTree.CommitmentSha256);
        Assert.Equal(GitHubPublicationValidationCode.InvalidCorrelation,
            Assert.Throws<GitHubPublicationValidationException>(() =>
                GitHubPublicationFactory.PrepareRemoteOperation(
                    authority,
                    observation with
                    {
                        Entries = observation.Entries.Select(entry => entry with
                        {
                            Kind = GitHubRemoteEntryKind.SymbolicLink,
                            Mode = "120000",
                        }),
                    },
                    coordination,
                    proposal,
                    pullRequest)).Code);
        Assert.Equal(GitHubPublicationValidationCode.InvalidCorrelation,
            Assert.Throws<GitHubPublicationValidationException>(() =>
                GitHubPublicationFactory.PrepareRemoteOperation(
                    authority,
                    observation with { ObservedTargetCommitOid = GitOid('8') },
                    coordination,
                    proposal,
                    pullRequest)).Code);

        var mutationCommitments = new[]
        {
            GitHubPublicationFactory.PrepareRemoteOperation(authority,
                observation with { CoordinationRefOid = GitOid('8') }, coordination, proposal, pullRequest)
                .CommitmentSha256,
            GitHubPublicationFactory.PrepareRemoteOperation(authority,
                observation with { ProposalRefOid = GitOid('8') }, coordination, proposal, pullRequest)
                .CommitmentSha256,
            GitHubPublicationFactory.PrepareRemoteOperation(authority,
                observation with { ProposalTreeOid = GitOid('8') }, coordination, proposal, pullRequest)
                .CommitmentSha256,
            GitHubPublicationFactory.PrepareRemoteOperation(authority,
                observation, coordination with { AuthorTimestamp = "2026-09-01T00:00:01Z" }, proposal, pullRequest)
                .CommitmentSha256,
            GitHubPublicationFactory.PrepareRemoteOperation(authority,
                observation, coordination, proposal, pullRequest with { TitleSha256 = Hex('a') })
                .CommitmentSha256,
        };
        Assert.All(mutationCommitments, commitment => Assert.NotEqual(prepared.CommitmentSha256, commitment));
    }

    [Fact]
    public void Base_byte_vectors_are_binary_safe_and_append_checks_the_preceding_candidate()
    {
        using var fixture = ReadFixture("base-byte-vectors-v1.json");
        Assert.False(fixture.RootElement.GetProperty("transcoding").GetBoolean());
        Assert.Equal(["100644", "100755"], fixture.RootElement
            .GetProperty("acceptedModes").EnumerateArray().Select(item => item.GetString()));

        var appendInput = CreateInput() with
        {
            Transition = GitHubPublicationTransitionKind.SameSnapshotAppend,
            LogicalPredecessorId = "previous-operation",
            PrecedingCandidateCommitmentSha256 = Hex('a'),
            ChangedFiles = [Changed("docs/readme.md", CandidateBytes) with
            {
                PrecedingCandidateFileSha256 = Hex('b'),
            }],
        };
        var append = GitHubPublicationFactory.CreateAuthority(appendInput);
        var observation = CreateRemoteObservation(append) with
        {
            Entries = [new GitHubRemoteEntryObservation(
                "docs/readme.md", GitOid('7'), GitHubRemoteEntryKind.Blob,
                "100755", Hex('b'), WasPreviouslyPublished: true)],
        };
        Assert.NotNull(GitHubPublicationFactory.PrepareRemoteOperation(
            append, observation, CreateCommit('3'), CreateCommit('4'), CreatePullRequest()));
        Assert.Equal(GitHubPublicationValidationCode.InvalidCorrelation,
            Assert.Throws<GitHubPublicationValidationException>(() =>
                GitHubPublicationFactory.PrepareRemoteOperation(
                    append,
                    observation with
                    {
                        Entries = observation.Entries.Select(entry => entry with
                        {
                            FullFileSha256 = Hex('c'),
                        }),
                    },
                    CreateCommit('3'), CreateCommit('4'), CreatePullRequest())).Code);

        var arbitrary = new byte[] { 0xff, 0xfe, 0x00, 0x80, 0x41 };
        var binaryAuthority = GitHubPublicationFactory.CreateAuthority(
            CreateInput([Changed("docs/binary.md", arbitrary)]));
        var binaryPayload = GitHubPublicationFactory.CreatePayload(binaryAuthority,
            [new GitHubChangedFilePayloadInput("docs/binary.md", arbitrary)]);
        Assert.True(binaryPayload.Files[0].CandidateBytes.AsSpan().SequenceEqual(arbitrary));
    }

    [Fact]
    public void Result_union_has_exact_required_and_forbidden_detail_shapes()
    {
        var claim = new GitHubPublicationClaimIdentity(
            "refs/heads/contract-scribe/coordination/a", GitOid('2'), "operation-1", Hex('a'));
        var admitted = GitHubPublicationResult.Admitted(claim);
        Assert.Equal(GitHubPublicationResultKind.Admitted, admitted.Kind);
        Assert.Same(claim, admitted.Claim);
        Assert.Null(admitted.LocalFailure);
        Assert.Null(admitted.StaleDraft);

        var stale = GitHubPublicationResult.StaleBaseAfterCreate(
            new GitHubPublicationStaleDraftResidual(
                42, Hex('a'), "refs/heads/proposal", GitOid('2'), "refs/heads/main",
                GitOid('1'), GitOid('3'), "generation-1", "operation-1", Hex('b')));
        Assert.Equal(GitHubPublicationResultKind.StaleBaseAfterCreate, stale.Kind);
        Assert.Equal(42, stale.StaleDraft!.PullRequestNumber);
        Assert.Null(stale.Claim);
        Assert.Null(stale.RemoteFailure);

        foreach (var kind in Enum.GetValues<GitHubPublicationRemoteFailureKind>())
        {
            var failure = GitHubPublicationResult.FromRemoteFailure(kind);
            Assert.NotNull(failure.RemoteFailure);
            Assert.Null(failure.LocalFailure);
            Assert.Null(failure.ContentResidual);
            Assert.Null(failure.RefResidual);
            Assert.Null(failure.PullRequest);
            Assert.Null(failure.StaleDraft);
        }
        Assert.DoesNotContain(typeof(GitHubPublicationResult).GetProperties(), property =>
            property.PropertyType == typeof(Exception)
            || property.Name.Contains("ResponseBody", StringComparison.Ordinal)
            || property.Name.Contains("Credential", StringComparison.Ordinal)
            || property.Name.Contains("Payload", StringComparison.Ordinal));
    }

    [Fact]
    public void Selected_protocol_fixture_freezes_exact_CAS_and_step_gates()
    {
        using var fixture = ReadFixture("selected-protocol-vectors-v1.json");
        var root = fixture.RootElement;
        Assert.Equal("GraphQL.updateRefs", root.GetProperty("mutation").GetString());
        Assert.Equal(1, root.GetProperty("refUpdatesPerMutation").GetInt32());
        Assert.False(root.GetProperty("force").GetBoolean());
        Assert.False(root.GetProperty("afterOidMayBeZero").GetBoolean());
        Assert.Equal(GitHubPublicationContract.MissingGitObjectId,
            root.GetProperty("expectedAbsenceBeforeOid").GetString());
        Assert.Equal(4, root.GetProperty("resources").GetArrayLength());
        Assert.Equal(7, root.GetProperty("outcomes").GetArrayLength());
        Assert.Equal("pull-request-readback", root.GetProperty("stepOrder")[10].GetString());
        Assert.Equal("zero-further-mutation", root.GetProperty("visibleDrift").GetString());
        Assert.Equal("one-marker-owned-stale-draft",
            root.GetProperty("baseMovesDuringPullRequestCreate").GetString());
    }

    [Fact]
    public void Core_boundary_and_port_remain_platform_neutral_and_unique()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "src", "ContractScribe.Core", "ContractScribe.Core.csproj"));
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("ProjectReference"));

        var port = typeof(IGitHubPublicationPort);
        var method = Assert.Single(port.GetMethods());
        Assert.Equal("PublishAsync", method.Name);
        Assert.Equal(typeof(ValueTask<GitHubPublicationResult>), method.ReturnType);
        Assert.Equal([
            typeof(ValidatedGitHubPublicationAuthority),
            typeof(ValidatedGitHubChangedFilePayload),
            typeof(CancellationToken),
        ], method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Single(typeof(IGitHubPublicationPort).Assembly.GetExportedTypes(), type =>
            type.IsInterface && type.Name.Contains("GitHubPublication", StringComparison.Ordinal));

        var coreFiles = Directory.EnumerateFiles(
            Path.Combine(root, "src", "ContractScribe.Core", "GitHub"), "*.cs", SearchOption.AllDirectories);
        var source = string.Join("\n", coreFiles.Select(File.ReadAllText));
        foreach (var forbidden in new[]
        {
            "HttpClient", "AuthorizationHeaderValue", "Environment.Get", "File.Read",
            "File.Write", "Directory.", "Microsoft.CodeAnalysis", "ContractScribe.Agent",
            "ContractScribe.Patching", "Octokit", "GitHubClient",
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
        Assert.False(Directory.Exists(Path.Combine(root, "src", "ContractScribe.GitHub")));
        Assert.False(File.Exists(Path.Combine(root, "tests", "ContractScribe.Tests",
            "GitHubPublicationProtocolDecisionHarnessTests.cs")));
        Assert.False(Directory.Exists(Path.Combine(root, "tests", "fixtures", "github", "protocol-decision")));
    }

    private static GitHubPublicationAuthorityInput CreateInput(
        IEnumerable<GitHubChangedFileAuthority>? files = null) => new(
            RepositoryOwner: "SolusQuest",
            RepositoryName: "contract-scribe",
            TargetRef: "refs/heads/main",
            ExpectedBaseCommitOid: GitOid('1'),
            CampaignLineage: "campaign-1",
            SnapshotCommitmentSha256: Hex('1'),
            ExecutionCommitmentSha256: Hex('2'),
            WorkPlanCommitmentSha256: Hex('3'),
            CheckpointRevision: 7,
            CheckpointSha256: Hex('4'),
            CandidateCommitmentSha256: Hex('5'),
            PatchRequestSha256: Hex('6'),
            PatchResultCommitmentSha256: Hex('7'),
            AcceptedProjectionCommitmentSha256: Hex('8'),
            OperationId: "operation-1",
            GenerationId: "generation-1",
            LogicalPredecessorId: null,
            PrecedingCandidateCommitmentSha256: null,
            Transition: GitHubPublicationTransitionKind.Initial,
            Policy: new GitHubPublicationPolicy(10, 10, 1_000),
            ChangedFiles: files ?? [Changed("docs/readme.md", CandidateBytes)]);

    private static GitHubPublicationAuthorityInput CreateClosedSuccessorInput()
    {
        var input = CreateInput() with
        {
            Transition = GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged,
            LogicalPredecessorId = "closed-generation-1",
            GenerationId = "generation-2",
            OperationId = "operation-2",
            SnapshotCommitmentSha256 = Hex('a'),
            WorkPlanCommitmentSha256 = Hex('b'),
            CandidateCommitmentSha256 = Hex('c'),
        };
        return input with
        {
            ClosedUnmergedSuccessorAuthorization = new(
                "authorization-1", 42, "generation-1", GitOid('2'), Hex('a'), Hex('b'),
                Hex('c'), "generation-2", "operation-2"),
        };
    }

    private static GitHubChangedFileAuthority Changed(string path, byte[] candidateBytes) => new(
        path,
        OriginalFileSha256: Sha(Encoding.UTF8.GetBytes("original\n")),
        CandidateFileSha256: Sha(candidateBytes),
        ChangedDocumentationBlockCount: 1,
        OriginalDocumentationByteCount: 8,
        CandidateDocumentationByteCount: candidateBytes.Length,
        OriginalDocumentationLineCount: 1,
        CandidateDocumentationLineCount: 1);

    private static GitHubAuthenticatedRemoteObservation CreateRemoteObservation(
        ValidatedGitHubPublicationAuthority authority) => new(
            CanonicalRepositoryId: "R_repository",
            ObservedTargetCommitOid: authority.ExpectedBaseCommitOid,
            ObservedBaseTreeOid: GitOid('2'),
            CoordinationRefOid: GitHubPublicationContract.MissingGitObjectId,
            ProposalRefOid: GitOid('5'),
            ProposalCommitOid: GitOid('6'),
            ProposalParentOid: GitOid('1'),
            ProposalTreeOid: GitOid('7'),
            ActivePullRequestNumber: null,
            ActivePullRequestState: null,
            Entries: authority.ChangedFiles.Select(file => new GitHubRemoteEntryObservation(
                file.Path, GitOid('7'), GitHubRemoteEntryKind.Blob, "100644",
                file.OriginalFileSha256, WasPreviouslyPublished: false)));

    private static GitHubDeterministicCommitPayload CreateCommit(char oid) => new(
        TreeLayoutCommitmentSha256: Hex('1'),
        MessageSha256: Hex('2'),
        ParentOid: GitOid('1'),
        AuthorName: "ContractScribe",
        AuthorEmail: "contract-scribe@example.invalid",
        AuthorTimestamp: "2026-09-01T00:00:00Z",
        CommitterName: "ContractScribe",
        CommitterEmail: "contract-scribe@example.invalid",
        CommitterTimestamp: "2026-09-01T00:00:00Z",
        OwnershipMarkerSha256: Hex('3'),
        ExpectedCommitOid: GitOid(oid));

    private static GitHubDeterministicPullRequestPayload CreatePullRequest() => new(
        "refs/heads/contract-scribe/proposals/a/b",
        "refs/heads/main",
        Hex('4'),
        Hex('5'),
        Draft: true,
        MaintainerCanModify: false);

    private static JsonDocument ReadFixture(string name) => JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "github",
            "publication-contract", name)));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string Sha(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Hex(char value) => new(value, 64);
    private static string GitOid(char value) => new(value, 40);
}
