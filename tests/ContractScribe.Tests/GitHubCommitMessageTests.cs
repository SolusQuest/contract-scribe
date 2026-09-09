using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.GitData;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Tests;

public sealed partial class GitHubCoordinationRefTests
{
    public static TheoryData<string> CommitResponseCases => new()
    {
        "raw", "projected", "raw-without-lf", "raw-extra-lf", "tree", "parent", "extra-parent",
        "author", "committer", "author-date", "committer-date", "author-offset", "committer-offset",
        "crlf", "terminal-crlf", "extra-lf", "trailing-space", "trailing-tab", "leading", "interior", "operation", "stage-or-generation",
    };

    [Theory]
    [MemberData(nameof(CommitResponseCases))]
    public void Coordination_commit_readback_and_state_authentication_preserve_raw_identity(string response)
    {
        var readback = typeof(GitHubCoordinationStore).GetMethod("ExactCommit", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var path in new[] { Fixture(), StageFixture() })
        {
            using var fixture = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
            {
                var prepared = GitHubCoordinationObjects.Prepare(GitHubCoordinationCodec.Decode(
                    Convert.FromBase64String(vector.GetProperty("canonicalStateUtf8Base64").GetString()!)));
                var expected = GitHubCoordinationObjects.CommitRequest(prepared);
                Assert.Equal(vector.GetProperty("commitOid").GetString(), RawCommitOid(expected));
                var actual = CommitObservation(expected, response);
                var accepted = response is "raw" or "projected";
                Assert.Equal(accepted, (bool)readback.Invoke(null, [actual, prepared])!);
                void Authenticate() => GitHubCoordinationObjects.Authenticate(prepared, actual,
                    new(prepared.RootTreeOid, GitHubCoordinationObjects.RootEntries(prepared)),
                    new(prepared.LeafTreeOid, GitHubCoordinationObjects.LeafEntries(prepared)),
                    new(prepared.BlobOid, prepared.StateBytes));
                if (accepted) Authenticate();
                else Assert.Throws<GitHubCoordinationException>(Authenticate);
            }
        }
    }

    [Theory]
    [MemberData(nameof(CommitResponseCases))]
    public void Proposal_commit_verification_preserves_raw_identity(string response)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Join(Root(),
            "tests/fixtures/github/git-data/known-answers.json")));
        foreach (var vector in fixture.RootElement.GetProperty("objects").EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "commit"))
        {
            var expected = GitHubProposalObjects.Commit(vector.GetProperty("tree").GetString()!,
                vector.GetProperty("parent").GetString()!, vector.GetProperty("operation").GetString()!,
                vector.GetProperty("generation").GetString()!);
            Assert.Equal(vector.GetProperty("oid").GetString(), RawCommitOid(expected));
            Assert.Equal(response is "raw" or "projected",
                GitHubProposalObjects.ExactCommit(CommitObservation(expected, response), expected));
        }
    }

    private static GitHubCommit CommitObservation(GitHubCreateCommit expected, string response)
    {
        Assert.EndsWith("\n", expected.Message);
        Assert.False(expected.Message.EndsWith("\n\n", StringComparison.Ordinal));
        Assert.Equal(expected.ExpectedOid, RawCommitOid(expected));
        var actual = new GitHubCommit(expected.ExpectedOid, expected.TreeOid, [expected.ParentOid],
            expected.Message[..^1], expected.Author, expected.Committer);
        if (response is "raw-without-lf" or "raw-extra-lf")
        {
            // Alternative raw commits can display an accepted message representation but have different OIDs.
            var different = expected with { Message = response == "raw-without-lf" ? actual.Message : expected.Message + "\n" };
            var oid = RawCommitOid(different);
            Assert.NotEqual(expected.ExpectedOid, oid);
            return actual with { Oid = oid, Message = response == "raw-extra-lf" ? expected.Message : actual.Message };
        }
        return response switch
        {
            "raw" => actual with { Message = expected.Message },
            "projected" => actual,
            "tree" => actual with { TreeOid = Oid('f') },
            "parent" => actual with { Parents = [Oid('f')] },
            "extra-parent" => actual with { Parents = [expected.ParentOid, Oid('f')] },
            "author" => actual with { Author = actual.Author with { Name = "Someone else" } },
            "committer" => actual with { Committer = actual.Committer with { Email = "other@example.test" } },
            "author-date" => actual with { Author = actual.Author with { Date = actual.Author.Date.AddSeconds(1) } },
            "committer-date" => actual with { Committer = actual.Committer with { Date = actual.Committer.Date.AddSeconds(1) } },
            "author-offset" => actual with { Author = actual.Author with { Date = actual.Author.Date.ToOffset(TimeSpan.FromHours(1)) } },
            "committer-offset" => actual with { Committer = actual.Committer with { Date = actual.Committer.Date.ToOffset(TimeSpan.FromHours(1)) } },
            "crlf" => actual with { Message = expected.Message.Replace("\n", "\r\n", StringComparison.Ordinal) },
            "terminal-crlf" => actual with { Message = actual.Message + "\r\n" },
            "extra-lf" => actual with { Message = expected.Message + "\n" },
            "trailing-space" => actual with { Message = actual.Message + " " },
            "trailing-tab" => actual with { Message = actual.Message + "\t" },
            "leading" => actual with { Message = " " + actual.Message },
            "interior" => actual with { Message = actual.Message.Replace("v1\n", "v1\n\n", StringComparison.Ordinal) },
            "operation" => actual with { Message = actual.Message.Replace("operation=", "operation=0", StringComparison.Ordinal) },
            "stage-or-generation" => actual with { Message = actual.Message.Insert(actual.Message.LastIndexOf('=') + 1, "0") },
            _ => throw new ArgumentOutOfRangeException(nameof(response)),
        };
    }

    private static string RawCommitOid(GitHubCreateCommit commit)
    {
        // Independent raw-object oracle, pinned above to the existing Git-generated known answers.
        static string ActorLine(string kind, GitHubCommitActor actor) => kind + " " + actor.Name + " <" + actor.Email + "> "
            + actor.Date.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + " +0000\n";
        return GitObjectOid("commit", Encoding.UTF8.GetBytes("tree " + commit.TreeOid + "\nparent " + commit.ParentOid + "\n"
            + ActorLine("author", commit.Author) + ActorLine("committer", commit.Committer) + "\n" + commit.Message));
    }
}
