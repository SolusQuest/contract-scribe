using System.Reflection;
using System.Runtime.Versioning;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Roslyn;
using ContractScribe.GitHub.NativeGit;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Tests;

[Collection("GitHub transport hook")]
public sealed class GitHubPrivateStorageTests
{
    [Fact]
    public async Task Real_construction_requires_the_host_root_and_disposal_revokes_lazy_native_creation()
    {
        var authority = GitHubPublicationFactory.CreateCampaignReadAuthority("Owner", "repo", "refs/heads/main", "campaign.root.test");
        Assert.Throws<GitHubProtocolException>(() => GitHubApiClient.CreateReadOnly(authority, "synthetic-no-live-token"));
        if (!OperatingSystem.IsLinux() && !DarwinFileSystem.IsSupported) return;
        using var owner = GitHubPrivateStorageOwner.Create(RepositoryRoot(), CancellationToken.None);
        using var registration = (IDisposable)typeof(GitHubTransportTestHook).GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new Uri("http://127.0.0.1:18765/"), new HttpClientHandler(), null])!;
        typeof(GitHubTransportTestHook).GetMethod("RegisterGitOrigin", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new Uri("http://127.0.0.1:18766/remote.git")]);
        var client = GitHubApiClient.CreateReadOnly(authority, GitHubTransportTestHook.Placeholder, storage: owner.Storage);
        var factory = Assert.IsType<Func<CancellationToken, ValueTask<GitHubCoordinationGitTransport>>>(client.NativeCoordinationFactory);
        client.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await factory(CancellationToken.None));
    }

    [Fact]
    public void CLI_owner_holds_a_dedicated_root_until_bounded_recovery_finishes_then_revokes_it()
    {
        if (!OperatingSystem.IsLinux() && !DarwinFileSystem.IsSupported) return;
        using var caller = new CancellationTokenSource();
        var owner = GitHubPrivateStorageOwner.Create(RepositoryRoot(), caller.Token);
        var storage = owner.Storage;
        var path = storage.Directory;
        Assert.True(Directory.Exists(path));
        Assert.False(path.StartsWith(RepositoryRoot() + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        storage.RequireCurrent();
        caller.Cancel();
        // The actual operation token stops work; the already-owned root must
        // remain available to the original bounded closing/recovery scope.
        storage.RequireCurrent();
        owner.Dispose();
        Assert.False(Directory.Exists(path));
        Assert.ThrowsAny<OperationCanceledException>(storage.RequireCurrent);
    }

    [Theory]
    [UnsupportedOSPlatform("windows")]
    [InlineData("mode")]
    [InlineData("replacement")]
    public void Dedicated_Git_storage_reuses_private_metadata_and_path_binding_rejection(string fault)
    {
        if (!OperatingSystem.IsLinux() && !DarwinFileSystem.IsSupported) return;
        var owner = GitHubPrivateStorageOwner.Create(RepositoryRoot(), CancellationToken.None);
        var path = owner.Storage.Directory;
        var moved = path + "-retained";
        var victim = Directory.CreateTempSubdirectory("contract-scribe-git-victim-").FullName;
        try
        {
            File.WriteAllText(Path.Join(victim, "sentinel"), "synthetic keep");
            if (fault == "mode")
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
            else
            {
                Directory.Move(path, moved);
                Directory.CreateSymbolicLink(path, victim);
            }
            Assert.ThrowsAny<Exception>(owner.Storage.RequireCurrent);
            owner.Dispose();
            Assert.True(File.Exists(Path.Join(victim, "sentinel")));
            if (fault == "mode") Assert.True(Directory.Exists(path));
            else Assert.True(Directory.Exists(moved));
        }
        finally
        {
            owner.Dispose();
            if (fault == "replacement" && Directory.Exists(path)) Directory.Delete(path);
            else if (Directory.Exists(path))
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(path, recursive: true);
            }
            if (Directory.Exists(moved)) Directory.Delete(moved, recursive: true);
            Directory.Delete(victim, recursive: true);
        }
    }

    [Fact]
    public void Dedicated_Git_storage_cannot_use_the_target_or_a_missing_executable()
    {
        if (!OperatingSystem.IsLinux() && !DarwinFileSystem.IsSupported) return;
        Assert.Throws<ArgumentException>(() =>
            FileCampaignCheckpointStore.RetainGitStorageDirectory(RepositoryRoot(), RepositoryRoot()));
        var before = Directory.GetFileSystemEntries(RepositoryRoot()).Order(StringComparer.Ordinal).ToArray();
        Assert.ThrowsAny<Exception>(() =>
            FileCampaignCheckpointStore.CreateGitStorageDirectory(RepositoryRoot(), RepositoryRoot()));
        Assert.Equal(before, Directory.GetFileSystemEntries(RepositoryRoot()).Order(StringComparer.Ordinal));
        var method = typeof(GitHubCoordinationGitTransport).GetMethod("RequireExecutable", BindingFlags.Static | BindingFlags.NonPublic)!;
        var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(null,
            [Path.Join(Path.GetTempPath(), "contract-scribe-missing-git-" + Guid.NewGuid().ToString("N"))]));
        Assert.Equal(GitHubFailureCode.InvalidRequest, Assert.IsType<GitHubProtocolException>(exception.InnerException).Code);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Missing repository.");
    }
}
