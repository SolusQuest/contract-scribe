using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class GitHubPublicationContractTests
{
    private static readonly byte[] OriginalBytes = Encoding.UTF8.GetBytes("original\n");
    private static readonly byte[] CandidateBytes = Encoding.UTF8.GetBytes("candidate\n");

    [Fact]
    public void Known_answer_source_free_authority_and_ref_identities_are_frozen()
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
            GitHubPublicationFactory.CreateCoordinationRef(authority));
        Assert.Equal(root.GetProperty("proposalRef").GetString(),
            GitHubPublicationFactory.CreateProposalRef(authority));
    }

    [Fact]
    public void Authority_is_order_stable_and_every_remote_selecting_fact_is_committed()
    {
        var a = Changed("docs/a.md", OriginalBytes, Encoding.UTF8.GetBytes("a"));
        var b = Changed("docs/b.md", OriginalBytes, Encoding.UTF8.GetBytes("b"));
        var ordered = GitHubPublicationFactory.CreateAuthority(CreateInput([a, b]) with
        {
            AcceptedM4Ceilings = new(10, 2, 1_000),
            Policy = new(10, 2, 1_000),
        });
        var reversed = GitHubPublicationFactory.CreateAuthority(CreateInput([b, a]) with
        {
            AcceptedM4Ceilings = new(10, 2, 1_000),
            Policy = new(10, 2, 1_000),
        });
        Assert.Equal(ordered.AuthorityCommitmentSha256, reversed.AuthorityCommitmentSha256);
        Assert.Equal(["docs/a.md", "docs/b.md"], ordered.ChangedFiles.Select(file => file.Path));

        var baseline = GitHubPublicationFactory.CreateAuthority(CreateInput());
        var mutations = new[]
        {
            CreateInput() with { RepositoryName = "contract-scribe-next" },
            CreateInput() with { TargetRef = "refs/heads/release" },
            CreateInput() with { ExpectedBaseCommitOid = GitOid('2') },
            CreateInput() with { SnapshotCommitmentSha256 = Hex('a') },
            CreateInput() with { WorkPlanCommitmentSha256 = Hex('b') },
            CreateInput() with { CheckpointRevision = 8 },
            CreateInput() with { CandidateCommitmentSha256 = Hex('c') },
            CreateInput() with { OperationId = "operation-2" },
            CreateInput() with { GenerationId = "generation-2" },
            CreateInput() with { AcceptedM4Ceilings = new(11, 10, 1_000) },
            CreateInput() with { Policy = new(9, 10, 1_000) },
            CreateInput([Changed("docs/readme.md", OriginalBytes,
                Encoding.UTF8.GetBytes("changed\n"))]),
        };
        Assert.All(mutations, mutation => Assert.NotEqual(
            baseline.OperationCommitmentSha256,
            GitHubPublicationFactory.CreateAuthority(mutation).OperationCommitmentSha256));
    }

    [Fact]
    public void M5_policy_is_subordinate_to_actual_accepted_M4_ceilings()
    {
        var exact = GitHubPublicationFactory.CreateAuthority(CreateInput() with
        {
            AcceptedM4Ceilings = new(2, 1, 500),
            Policy = new(2, 1, 500),
        });
        Assert.Equal(2, exact.AcceptedM4Ceilings.MaximumDocumentationBlocks);

        var overBlocks = CreateInput() with
        {
            AcceptedM4Ceilings = new(2, 1, 500),
            Policy = new(3, 1, 500),
        };
        var overFiles = overBlocks with { Policy = new(2, 2, 500) };
        var overBytes = overBlocks with { Policy = new(2, 1, 501) };
        Assert.All([overBlocks, overFiles, overBytes], input => Assert.Equal(
            GitHubPublicationValidationCode.InvalidPolicy,
            Assert.Throws<GitHubPublicationValidationException>(() =>
                GitHubPublicationFactory.CreateAuthority(input)).Code));

        var substituted = GitHubPublicationFactory.CreateAuthority(CreateInput() with
        {
            AcceptedM4Ceilings = new(11, 10, 1_000),
        });
        Assert.NotEqual(exact.PolicyCommitmentSha256, substituted.PolicyCommitmentSha256);
    }

    [Fact]
    public void Cumulative_patch_bytes_are_the_complete_M4_candidate_measure()
    {
        var file = Changed("docs/readme.md", OriginalBytes, CandidateBytes) with
        {
            CandidateDocumentationByteCount = 10,
        };
        var exact = GitHubPublicationFactory.CreateAuthority(CreateInput([file]) with
        {
            AcceptedM4Ceilings = new(1, 1, 10),
            Policy = new(1, 1, 10),
        });
        Assert.Equal(10, exact.CumulativePatchBytes);
        Assert.Equal(GitHubPublicationValidationCode.InvalidPolicy,
            Assert.Throws<GitHubPublicationValidationException>(() =>
                GitHubPublicationFactory.CreateAuthority(CreateInput([file]) with
                {
                    AcceptedM4Ceilings = new(1, 1, 10),
                    Policy = new(1, 1, 9),
                })).Code);
    }

    [Theory]
    [InlineData("../secret.md")]
    [InlineData("/absolute.md")]
    [InlineData("docs\\readme.md")]
    public void Repository_paths_fail_closed(string path)
    {
        Assert.Equal(GitHubPublicationValidationCode.InvalidPath,
            Assert.Throws<GitHubPublicationValidationException>(() =>
                GitHubPublicationFactory.CreateAuthority(
                    CreateInput([Changed(path, OriginalBytes, CandidateBytes)]))).Code);
    }

    [Theory]
    [InlineData("refs/heads/.hidden")]
    [InlineData("refs/heads/release.lock")]
    [InlineData("refs/heads/topic.")]
    [InlineData("refs/heads/a@{b")]
    [InlineData("refs/heads/a//b")]
    [InlineData("refs/heads/a b")]
    public void Git_invalid_refs_fail_during_local_admission(string targetRef)
    {
        Assert.Equal(GitHubPublicationValidationCode.InvalidVocabulary,
            Assert.Throws<GitHubPublicationValidationException>(() =>
                GitHubPublicationFactory.CreateAuthority(CreateInput() with
                {
                    TargetRef = targetRef,
                })).Code);
    }

    [Theory]
    [InlineData("generation=2")]
    [InlineData("generation-->2")]
    [InlineData("<!--generation")]
    [InlineData(" generation")]
    [InlineData("generation ")]
    [InlineData("generation\u202e2")]
    public void Marker_unsafe_identifiers_fail_before_commitment_or_transport(string identifier)
    {
        Assert.Equal(GitHubPublicationValidationCode.InvalidVocabulary,
            Assert.Throws<GitHubPublicationValidationException>(() =>
                GitHubPublicationFactory.CreateAuthority(CreateInput() with
                {
                    GenerationId = identifier,
                })).Code);
    }

    [Fact]
    public void Exact_payload_set_hash_copy_and_byte_bounds_are_enforced_before_copy()
    {
        var authority = GitHubPublicationFactory.CreateAuthority(CreateInput());
        var bytes = CandidateBytes.ToArray();
        var payload = GitHubPublicationFactory.CreatePayload(authority,
            [new GitHubChangedFilePayloadInput("docs/readme.md", bytes)]);
        bytes[0] ^= 0xff;
        Assert.True(payload.Files[0].CandidateBytes.AsSpan().SequenceEqual(CandidateBytes));
        Assert.Equal(nameof(ValidatedGitHubChangedFilePayload), payload.ToString());

        Assert.Equal(GitHubPublicationValidationCode.PayloadMismatch,
            Assert.Throws<GitHubPublicationValidationException>(() =>
                GitHubPublicationFactory.CreatePayload(authority, [])).Code);
        Assert.Equal(GitHubPublicationValidationCode.InvalidBound,
            Assert.Throws<GitHubPublicationValidationException>(() =>
                GitHubPublicationFactory.CreatePayload(authority,
                    [new("docs/readme.md",
                        new byte[GitHubPublicationContract.MaximumPayloadBytesPerFile + 1])])).Code);

        var shared = new byte[GitHubPublicationContract.MaximumPayloadBytesPerFile];
        var files = Enumerable.Range(0, 5).Select(index =>
            Changed($"docs/{index}.bin", OriginalBytes, shared) with
            {
                CandidateDocumentationByteCount = 1,
            }).ToArray();
        var aggregateAuthority = GitHubPublicationFactory.CreateAuthority(CreateInput(files) with
        {
            AcceptedM4Ceilings = new(10, 5, 1_000),
            Policy = new(10, 5, 1_000),
        });
        var aggregatePayload = files.Select(file =>
            new GitHubChangedFilePayloadInput(file.Path, shared));
        Assert.Equal(GitHubPublicationValidationCode.InvalidBound,
            Assert.Throws<GitHubPublicationValidationException>(() =>
                GitHubPublicationFactory.CreatePayload(aggregateAuthority, aggregatePayload)).Code);
    }

    [Fact]
    public void Closed_successor_authorization_binds_exact_predecessor_and_new_generation()
    {
        var closed = CreateClosedSuccessorInput();
        Assert.NotNull(GitHubPublicationFactory.CreateAuthority(closed));
        var predecessor = closed.TerminalPredecessor!;
        var authorization = closed.ClosedUnmergedSuccessorAuthorization!;
        var substitutions = new[]
        {
            closed with { TerminalPredecessor = predecessor with { LogicalPredecessorId = "other" } },
            closed with { TerminalPredecessor = predecessor with { PullRequestNumber = 43 } },
            closed with { TerminalPredecessor = predecessor with { HeadOid = GitOid('9') } },
            closed with { GenerationId = predecessor.GenerationId,
                ClosedUnmergedSuccessorAuthorization = authorization with
                { NewGenerationId = predecessor.GenerationId } },
        };
        Assert.All(substitutions, input => Assert.Throws<GitHubPublicationValidationException>(() =>
            GitHubPublicationFactory.CreateAuthority(input)));
    }

    [Fact]
    public void Append_binds_complete_preceding_map_and_distinct_candidate_commitment()
    {
        var append = CreateAppendInput();
        var authority = GitHubPublicationFactory.CreateAuthority(append);
        Assert.Equal(["docs/a.md"], authority.PrecedingChangedFiles.Select(file => file.Path));

        var omittedPrior = append with
        {
            ChangedFiles = append.ChangedFiles.Where(file => file.Path != "docs/a.md"),
        };
        Assert.Throws<GitHubPublicationValidationException>(() =>
            GitHubPublicationFactory.CreateAuthority(omittedPrior));

        var substitutedCandidate = GitHubPublicationFactory.CreateAuthority(append with
        {
            PrecedingCandidateCommitmentSha256 = Hex('c'),
        });
        Assert.NotEqual(authority.AuthorityCommitmentSha256,
            substitutedCandidate.AuthorityCommitmentSha256);
        Assert.NotEqual(authority.OperationCommitmentSha256,
            substitutedCandidate.OperationCommitmentSha256);
    }

    [Fact]
    public void Result_union_uses_closed_field_ids_and_exact_structured_residuals()
    {
        var local = GitHubPublicationResult.LocalInvalid(
            GitHubPublicationValidationCode.InvalidCorrelation,
            GitHubPublicationFieldId.Candidate);
        Assert.Equal(GitHubPublicationFieldId.Candidate, local.LocalFailure!.Field);
        Assert.Equal(typeof(GitHubPublicationFieldId?),
            typeof(GitHubPublicationLocalFailure).GetProperty("Field")!.PropertyType);
        Assert.DoesNotContain(typeof(GitHubPublicationLocalFailure).GetProperties(), property =>
            property.PropertyType == typeof(string));

        var stale = GitHubPublicationResult.StaleBaseAfterCreate(
            new GitHubPublicationStaleDraftResidual(
                42, Hex('a'), "refs/heads/proposal", GitOid('2'), "refs/heads/main",
                GitOid('1'), GitOid('3'), "generation-1", "operation-1", Hex('b')));
        Assert.Equal(42, stale.StaleDraft!.PullRequestNumber);
        Assert.Null(stale.RemoteFailure);
        foreach (var kind in Enum.GetValues<GitHubPublicationRemoteFailureKind>())
        {
            var failure = GitHubPublicationResult.FromRemoteFailure(kind);
            Assert.NotNull(failure.RemoteFailure);
            Assert.Null(failure.LocalFailure);
            Assert.Null(failure.ContentResidual);
            Assert.Null(failure.StaleDraft);
        }
    }

    [Fact]
    public void Selected_protocol_fixture_freezes_CAS_step_gates_and_zero_after_rejection()
    {
        using var fixture = ReadFixture("selected-protocol-vectors-v1.json");
        var root = fixture.RootElement;
        Assert.Equal("GraphQL.updateRefs", root.GetProperty("mutation").GetString());
        Assert.Equal(1, root.GetProperty("refUpdatesPerMutation").GetInt32());
        Assert.False(root.GetProperty("force").GetBoolean());
        Assert.False(root.GetProperty("afterOidMayBeZero").GetBoolean());
        Assert.Equal(4, root.GetProperty("resources").GetArrayLength());
        Assert.Equal("zero-further-mutation", root.GetProperty("visibleDrift").GetString());
    }

    [Fact]
    public void Core_boundary_has_one_port_and_no_R3_through_R6_implementation()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Join(root, "src", "ContractScribe.Core",
            "ContractScribe.Core.csproj"));
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("ProjectReference"));

        var method = Assert.Single(typeof(IGitHubPublicationPort).GetMethods());
        Assert.Equal([
            typeof(ValidatedGitHubPublicationAuthority),
            typeof(ValidatedGitHubChangedFilePayload),
            typeof(CancellationToken),
        ], method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Single(typeof(IGitHubPublicationPort).Assembly.GetExportedTypes(), type =>
            type.IsInterface && type.Name.Contains("GitHubPublication", StringComparison.Ordinal));

        var exportedNames = typeof(IGitHubPublicationPort).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == typeof(IGitHubPublicationPort).Namespace)
            .Select(type => type.Name).ToArray();
        foreach (var forbiddenType in new[]
        {
            "AuthenticatedRemote", "RemoteEntryObservation", "CoordinationObservation",
            "ProposalObservation", "PullRequestObservation", "PreparedRemoteOperation",
            "DeterministicCommitPayload", "DeterministicPullRequestPayload",
        })
        {
            Assert.DoesNotContain(exportedNames,
                name => name.Contains(forbiddenType, StringComparison.Ordinal));
        }

        var source = string.Join("\n", Directory.EnumerateFiles(
            Path.Join(root, "src", "ContractScribe.Core", "GitHub"), "*.cs",
            SearchOption.AllDirectories).Select(File.ReadAllText));
        foreach (var forbidden in new[]
        {
            "HttpClient", "AuthorizationHeaderValue", "Environment.Get", "File.Read",
            "File.Write", "Directory.", "Microsoft.CodeAnalysis", "ContractScribe.Agent",
            "ContractScribe.Patching", "Octokit", "GitHubClient", "CreateBlobOid",
            "CreateTreeOid", "CreateCommit", "CreatePullRequest", "PrepareRemoteOperation",
            "SHA1", "Utf8JsonWriter", "ownership-v1",
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
        Assert.False(Directory.Exists(Path.Join(root, "src", "ContractScribe.GitHub")));
        Assert.False(File.Exists(Path.Join(root, "tests", "ContractScribe.Tests",
            "GitHubPublicationProtocolDecisionHarnessTests.cs")));
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
            PrecedingOperationId: null,
            PrecedingAuthorityCommitmentSha256: null,
            PrecedingCandidateCommitmentSha256: null,
            TerminalPredecessor: null,
            Transition: GitHubPublicationTransitionKind.Initial,
            AcceptedM4Ceilings: new GitHubPublicationM4Ceilings(10, 10, 1_000),
            Policy: new GitHubPublicationPolicy(10, 10, 1_000),
            ChangedFiles: files ?? [Changed("docs/readme.md", OriginalBytes, CandidateBytes)],
            PrecedingChangedFiles: []);

    private static GitHubPublicationAuthorityInput CreateAppendInput()
    {
        var previous = Encoding.UTF8.GetBytes("previous-a\n");
        return CreateInput([
            Changed("docs/a.md", OriginalBytes, Encoding.UTF8.GetBytes("current-a\n")),
            Changed("docs/b.md", OriginalBytes, Encoding.UTF8.GetBytes("current-b\n")),
        ]) with
        {
            Transition = GitHubPublicationTransitionKind.SameSnapshotAppend,
            PrecedingOperationId = "previous-operation",
            PrecedingAuthorityCommitmentSha256 = Hex('a'),
            PrecedingCandidateCommitmentSha256 = Hex('b'),
            AcceptedM4Ceilings = new(10, 2, 1_000),
            Policy = new(10, 2, 1_000),
            PrecedingChangedFiles = [new GitHubPrecedingChangedFileAuthority(
                "docs/a.md", Sha(previous))],
        };
    }

    private static GitHubPublicationAuthorityInput CreateClosedSuccessorInput()
    {
        var predecessor = new GitHubPublicationPredecessorAuthority(
            "closed-pr-42", 42, "generation-1", GitOid('2'),
            GitHubPublicationPredecessorDisposition.ClosedUnmerged);
        var input = CreateInput() with
        {
            Transition = GitHubPublicationTransitionKind.SuccessorAfterClosedUnmerged,
            TerminalPredecessor = predecessor,
            GenerationId = "generation-2",
            OperationId = "operation-2",
            SnapshotCommitmentSha256 = Hex('a'),
            WorkPlanCommitmentSha256 = Hex('b'),
            CandidateCommitmentSha256 = Hex('c'),
        };
        return input with
        {
            ClosedUnmergedSuccessorAuthorization = new(
                "authorization-1", predecessor.LogicalPredecessorId,
                predecessor.PullRequestNumber, predecessor.GenerationId, predecessor.HeadOid,
                Hex('a'), Hex('b'), Hex('c'), "generation-2", "operation-2"),
        };
    }

    private static GitHubChangedFileAuthority Changed(
        string path,
        byte[] original,
        byte[] candidate) => new(
            path,
            Sha(original),
            Sha(candidate),
            ChangedDocumentationBlockCount: 1,
            OriginalDocumentationByteCount: original.Length,
            CandidateDocumentationByteCount: candidate.Length,
            OriginalDocumentationLineCount: 1,
            CandidateDocumentationLineCount: 1);

    private static JsonDocument ReadFixture(string name) => JsonDocument.Parse(
        File.ReadAllBytes(Path.Join(FindRepositoryRoot(), "tests", "fixtures", "github",
            "publication-contract", name)));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string Sha(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Hex(char value) => new(value, 64);
    private static string GitOid(char value) => new(value, 40);
}
