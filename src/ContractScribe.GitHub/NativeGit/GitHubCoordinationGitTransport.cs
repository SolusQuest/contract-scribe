using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.GitHub.NativeGit;

// Supplied only by the trusted CLI's private-storage owner. It owns neither
// validation rules nor arbitrary filesystem discovery in this assembly.
internal sealed class GitHubPrivateStorage
{
    internal string Directory { get; }
    internal CancellationToken Lifetime { get; }
    private readonly Action validate;
    internal GitHubPrivateStorage(string directory, Action validate, CancellationToken lifetime)
    {
        if (!Path.IsPathFullyQualified(directory) || !lifetime.CanBeCanceled)
            throw new GitHubProtocolException(GitHubFailureCode.InvalidRequest);
        Directory = directory;
        this.validate = validate ?? throw new ArgumentNullException(nameof(validate));
        Lifetime = lifetime;
        RequireCurrent();
    }
    internal void RequireCurrent()
    {
        Lifetime.ThrowIfCancellationRequested();
        validate();
    }
    internal void RequireCleanupRoot() => validate();
}

// A data observation is not a claim, a Core Written receipt, or a PR-send grant.
internal sealed record GitHubGitCheckpointObservation(string HeadOid,
    GitHubCoordinationDocument Document, GitHubCommit? Commit, GitHubTree Root, GitHubTree Leaf,
    ImmutableDictionary<string, GitHubBlob> Blobs);

internal enum GitHubGitAdvance { ActualUpdated, Observer }

internal sealed record GitHubGitAdvanceResult(GitHubGitAdvance Outcome,
    GitHubGitCheckpointObservation? Observation);

/// <summary>Private native Git data transport for one exact coordination ref.</summary>
internal sealed class GitHubCoordinationGitTransport : IAsyncDisposable
{
    private const int MaximumOutputBytes = 16 * 1024 * 1024;
    private const long MaximumStoreBytes = 128 * 1024 * 1024;
    private readonly string directory;
    private readonly string writer;
    private readonly string observer;
    private readonly Uri remote;
    private readonly string reference;
    private readonly string target;
    private readonly ValidatedGitHubCampaignReadAuthority authority;
    [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
    private string? authentication;
    private readonly GitHubPrivateStorage? privateStorage;
    private readonly Func<CancellationToken>? finalizationDeadline;
    private readonly Action? mutationAttempt;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    private GitHubCoordinationGitTransport(ValidatedGitHubCampaignReadAuthority authority,
        string directory, Uri remote, string? authentication, Func<CancellationToken>? finalizationDeadline, Action? mutationAttempt,
        GitHubPrivateStorage? privateStorage)
    {
        this.directory = directory;
        this.authority = authority;
        writer = Path.Join(directory, "writer.git");
        observer = Path.Join(directory, "observer.git");
        this.remote = remote;
        this.authentication = authentication;
        this.finalizationDeadline = finalizationDeadline;
        this.mutationAttempt = mutationAttempt;
        this.privateStorage = privateStorage;
        reference = GitHubPublicationFactory.CreateCoordinationRef(authority);
        target = authority.TargetRef;
    }

    // Counts native remote commands, not HTTP exchanges or GitHub API requests.
    internal int RemoteCommands { get; private set; }

    internal static ValueTask<GitHubCoordinationGitTransport> CreateAsync(
        ValidatedGitHubCampaignReadAuthority authority, GitHubPrivateStorage storage, string credential,
        CancellationToken token, Func<CancellationToken>? finalizationDeadline = null, Action? mutationAttempt = null) => CreateOwnedAsync(authority, storage.Directory,
            new Uri("https://github.com/" + authority.RepositoryOwner + "/" + authority.RepositoryName + ".git"),
            "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + credential)), token, finalizationDeadline, mutationAttempt, storage);

    // The caller is an in-process synthetic test, never an environment option.
    internal static ValueTask<GitHubCoordinationGitTransport> CreateLoopbackAsync(
        ValidatedGitHubCampaignReadAuthority authority, string privateParent, Uri remote, CancellationToken token,
        Func<CancellationToken>? finalizationDeadline = null, Action? mutationAttempt = null, GitHubPrivateStorage? privateStorage = null)
    {
        if (remote.Scheme != "http" || remote.Host != "127.0.0.1" || remote.IsDefaultPort
            || remote.UserInfo.Length != 0 || remote.Query.Length != 0 || remote.Fragment.Length != 0)
            throw new GitHubProtocolException(GitHubFailureCode.InvalidRequest);
        return CreateOwnedAsync(authority, privateParent, remote, null, token, finalizationDeadline, mutationAttempt, privateStorage);
    }

    private static async ValueTask<GitHubCoordinationGitTransport> CreateOwnedAsync(
        ValidatedGitHubCampaignReadAuthority authority, string privateParent, Uri remote,
        string? authentication, CancellationToken token, Func<CancellationToken>? finalizationDeadline, Action? mutationAttempt,
        GitHubPrivateStorage? privateStorage)
    {
        ArgumentNullException.ThrowIfNull(authority);
        privateStorage?.RequireCurrent();
        if (privateStorage is not null && privateStorage.Directory != privateParent)
            throw new GitHubProtocolException(GitHubFailureCode.InvalidRequest);
        RequireExecutable("/usr/bin/git");
        if (OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(privateParent))
            throw new GitHubProtocolException(GitHubFailureCode.InvalidRequest);
        for (var part = new DirectoryInfo(privateParent); part is not null; part = part.Parent)
            if (!part.Exists || (part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new GitHubProtocolException(GitHubFailureCode.InvalidRequest);
        var directory = Path.Join(privateParent, "contract-scribe-coordination-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var transport = new GitHubCoordinationGitTransport(authority, directory, remote, authentication, finalizationDeadline, mutationAttempt, privateStorage);
        try
        {
            foreach (var store in new[] { transport.writer, transport.observer })
                transport.Require(await transport.RunAsync(null,
                    ["init", "--bare", "--object-format=sha1", store], null, false, token).ConfigureAwait(false));
            return transport;
        }
        catch { await transport.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal async ValueTask<GitHubGitCheckpointObservation?> ObserveAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await ObserveCoreAsync(token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private static void RequireExecutable(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new GitHubProtocolException(GitHubFailureCode.InvalidRequest);
    }

    private async ValueTask<GitHubGitCheckpointObservation?> ObserveCoreAsync(CancellationToken token)
    {
        // Exact named ref only. Its independent store never sees pending objects.
        var fetched = await RunAsync(observer, ["fetch", "--depth=1", "--no-filter", "--no-tags", "--no-write-fetch-head", remote.AbsoluteUri,
            "+" + reference + ":refs/observed/current"], null, true, token).ConfigureAwait(false);
        if (fetched.Exit != 0)
        {
            // A failed read is never retried. Only an independently confirmed
            // missing ref supplies expected absence during cold acquisition.
            var listed = Require(await RunAsync(observer,
                ["ls-remote", "--refs", remote.AbsoluteUri, reference], null, true, token).ConfigureAwait(false));
            if (listed.Length == 0) return null;
            throw new GitHubProtocolException();
        }
        var head = Text(Require(await RunAsync(observer, ["rev-parse", "refs/observed/current"], null, false, token).ConfigureAwait(false)));
        if (!IsOid(head)) throw new GitHubProtocolException();
        var observation = await ReadObservedCoreAsync(head, token).ConfigureAwait(false);
        // Import only the independently observed coordination commit/tree, not
        // the unrelated source ancestry behind the first coordination parent.
        Require(await RunAsync(writer, ["fetch", "--depth=1", "--no-filter", "--update-shallow", "--no-tags", "--no-write-fetch-head", observer,
            "+refs/observed/current:refs/observed/current"], null, false, token).ConfigureAwait(false));
        return observation;
    }

    internal async ValueTask<string?> ReadHeadAsync(bool targetHead, CancellationToken token)
        => await ReadDataHeadAsync(targetHead ? target : reference, token).ConfigureAwait(false);

    internal async ValueTask<string?> ReadDataHeadAsync(string name, CancellationToken token)
    {
        var prefix = "refs/heads/contract-scribe/proposals/";
        var proposal = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..].Split('/') : [];
        if (name != target && name != reference
            && !(proposal is [var campaign, var generation] && campaign.Length == 64 && generation.Length == 64
                && campaign.Concat(generation).All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new GitHubProtocolException(GitHubFailureCode.InvalidRequest);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var bytes = Require(await RunAsync(observer, ["ls-remote", "--refs", remote.AbsoluteUri, name], null, true, token).ConfigureAwait(false));
            if (bytes.Length == 0) return null;
            var lines = Encoding.ASCII.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines is not [var row] || row.Split('\t') is not [var oid, var actual] || actual != name || !IsOid(oid))
                throw new GitHubProtocolException();
            return oid;
        }
        finally { gate.Release(); }
    }

    internal async ValueTask<string?> ReadObservedCommitTreeAsync(string oid, CancellationToken token)
    {
        if (!IsOid(oid)) throw new GitHubProtocolException();
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await ReadObservedCommitTreeCoreAsync(oid, token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    internal async ValueTask<string> AcquireObservedBaseTreeAsync(string oid, CancellationToken token)
    {
        if (!IsOid(oid)) throw new GitHubProtocolException();
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var existing = await ReadObservedCommitTreeCoreAsync(oid, token).ConfigureAwait(false);
            if (existing is not null) return existing;
            await FetchCommitOnlyAsync(observer, oid, oid, "refs/observed/source-parent", token).ConfigureAwait(false);
            return await ReadObservedCommitTreeCoreAsync(oid, token).ConfigureAwait(false) ?? throw new GitHubProtocolException();
        }
        finally { gate.Release(); }
    }

    private async ValueTask<string?> ReadObservedCommitTreeCoreAsync(string oid, CancellationToken token)
    {
        var bytes = Require(await RunAsync(observer, ["cat-file", "--batch"], Encoding.ASCII.GetBytes(oid + "\n"), false, token).ConfigureAwait(false));
        var newline = Array.IndexOf(bytes, (byte)'\n');
        if (newline < 0) throw new GitHubProtocolException();
        var header = Encoding.ASCII.GetString(bytes, 0, newline);
        if (header == oid + " missing" && bytes.Length == newline + 1) return null;
        var fields = header.Split(' ');
        if (fields is not [var actual, "commit", var size] || actual != oid
            || !int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 46
            || bytes.Length != newline + count + 2 || bytes[^1] != '\n') throw new GitHubProtocolException();
        var content = bytes.AsSpan(newline + 1, count);
        if (GitHubCoordinationObjects.ObjectOid("commit", content) != oid
            || !content[..5].SequenceEqual("tree "u8) || content[45] != '\n') throw new GitHubProtocolException();
        var tree = Encoding.ASCII.GetString(content.Slice(5, 40));
        if (!IsOid(tree)) throw new GitHubProtocolException();
        return tree;
    }

    // Already-fetched immutable history only: no implicit remote request and no
    // access to unpublished objects in the independent writer store.
    internal async ValueTask<GitHubGitCheckpointObservation> ReadObservedAsync(string head, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!IsOid(head)) throw new GitHubProtocolException();
            return await ReadObservedCoreAsync(head, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // Explicit bounded history acquisition at the caller's authentication step.
    // A shallow boundary never supplies proof that an ancestor is absent.
    internal async ValueTask<bool> AcquireObservedHistoryAsync(string head, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!IsOid(head)) throw new GitHubProtocolException();
            if ((await RunAsync(observer, ["cat-file", "-e", head + "^{commit}"], null, false, token).ConfigureAwait(false)).Exit == 0)
                return true;
            Require(await RunAsync(observer, ["fetch", "--depth=1", "--no-filter", "--no-tags", "--no-write-fetch-head", remote.AbsoluteUri,
                "+" + head + ":refs/observed/history"], null, true, token).ConfigureAwait(false));
            if (Text(Require(await RunAsync(observer, ["rev-parse", "refs/observed/history"], null, false, token).ConfigureAwait(false))) != head)
                throw new GitHubProtocolException();
            Require(await RunAsync(observer, ["cat-file", "-e", head + "^{commit}"], null, false, token).ConfigureAwait(false));
            return true;
        }
        finally { gate.Release(); }
    }

    private async ValueTask<GitHubGitCheckpointObservation> ReadObservedCoreAsync(string head, CancellationToken token)
    {
        var state = Require(await RunAsync(observer, ["cat-file", "blob", head + ":"
            + GitHubCoordinationObjects.RootPath + "/" + GitHubCoordinationObjects.StatePath], null, false, token).ConfigureAwait(false));
        var document = GitHubCoordinationDocument.Decode(state);
        var prepared = GitHubCoordinationObjects.Prepare(document);
        var rawCommit = await ReadObjectAsync(observer, "commit", head, token).ConfigureAwait(false);
        // Signed API-era history needs its existing real principal metadata.
        // Never manufacture Author.user or GitHub signature evidence from Git.
        CheckDocument(document);
        var root = await ReadTreeAsync(prepared.RootTreeOid, token).ConfigureAwait(false);
        var leaf = await ReadTreeAsync(prepared.LeafTreeOid, token).ConfigureAwait(false);
        var request = GitHubCoordinationObjects.CommitRequest(prepared);
        var commit = head == prepared.CommitOid
            ? new GitHubCommit(head, request.TreeOid, [request.ParentOid], request.Message, request.Author, request.Committer) : null;
        var blobs = ImmutableDictionary.CreateBuilder<string, GitHubBlob>(StringComparer.Ordinal);
        foreach (var entry in leaf.Entries)
        {
            var bytes = await ReadObjectAsync(observer, "blob", entry.Oid, token).ConfigureAwait(false);
            blobs.Add(entry.Path, new(entry.Oid, bytes.ToImmutableArray()));
        }
        if (commit is not null)
            GitHubCoordinationObjects.Authenticate(prepared, commit, root, leaf, blobs[GitHubCoordinationObjects.StatePath]);
        _ = rawCommit;
        // Null commit explicitly requires the existing independent platform
        // principal/signature reader. Raw Git never fabricates that authority.
        return new(head, document, commit, root, leaf, blobs.ToImmutable());
    }

    internal async ValueTask<GitHubGitAdvanceResult> AdvanceAsync(GitHubCheckpointWrite write, CancellationToken token,
        Action? validateBeforePush = null)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var prepared = write.Prepared;
            await MaterializeCoreAsync(write, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            validateBeforePush?.Invoke();
            // The owner binds this invalidation at construction. It runs once
            // per application push, not per identical transport retransmission.
            mutationAttempt?.Invoke();
            var pushed = await RunAsync(writer, ["push", "--porcelain", "--force-with-lease=" + reference + ":"
                + prepared.Document.PredecessorOid, remote.AbsoluteUri, prepared.CommitOid + ":" + reference], null, true, token).ConfigureAwait(false);
            var rows = Encoding.UTF8.GetString(pushed.Output).Split('\n').Where(line => line.Contains('\t')).ToArray();
            var updated = pushed.Exit == 0 && rows is [var row] && row.Length > 2
                && row[0] is ' ' or '+' or '*' && row[1] == '\t'
                && row.Split('\t') is [_, var mapping, _]
                && mapping == prepared.CommitOid + ":" + reference;
            // One application push only. Reconciliation does not refresh deadlines
            // or turn '=' / a lost acknowledgement into this writer's success.
            var observed = await ObserveCoreAsync(token).ConfigureAwait(false);
            return new(updated && observed?.HeadOid == prepared.CommitOid
                ? GitHubGitAdvance.ActualUpdated : GitHubGitAdvance.Observer, observed);
        }
        finally { gate.Release(); }
    }

    internal async ValueTask MaterializeAsync(GitHubCheckpointWrite write, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { await MaterializeCoreAsync(write, token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async ValueTask MaterializeCoreAsync(GitHubCheckpointWrite write, CancellationToken token)
    {
        var prepared = write.Prepared;
        CheckDocument(prepared.Document);
        if (prepared.Document.PredecessorOid == prepared.CommitOid)
            throw new GitHubProtocolException(GitHubFailureCode.InvalidRequest);
        // The caller still owns fresh identity, lease, parent and send gates.
        // The initial base may be absent from this store when claiming a new ref.
        if (prepared.Document.PredecessorOid == GitHubCoordinationDocument.ZeroOid)
        {
            await FetchCommitOnlyAsync(writer, target, prepared.ParentOid, "refs/observed/base", token).ConfigureAwait(false);
        }
        else if ((await RunAsync(writer, ["cat-file", "-e", prepared.ParentOid + "^{commit}"], null, false, token).ConfigureAwait(false)).Exit != 0)
        {
            // Strict platform publication can advance this ref without using
            // either native object store. Import its observed history before
            // materializing the next native renewal/checkpoint. This supplies
            // objects only: the caller's proof/lease gates and the exact push
            // lease still authorize the write, including if the head moved.
            _ = await ObserveCoreAsync(token).ConfigureAwait(false);
            // A moved head's depth-one observation need not contain this exact
            // predecessor. Acquire its commit explicitly, without its history.
            if ((await RunAsync(writer, ["cat-file", "-e", prepared.ParentOid + "^{commit}"], null, false, token).ConfigureAwait(false)).Exit != 0)
                await FetchCommitOnlyAsync(writer, prepared.ParentOid, prepared.ParentOid, "refs/observed/predecessor", token).ConfigureAwait(false);
            Require(await RunAsync(writer, ["cat-file", "-e", prepared.ParentOid + "^{commit}"], null, false, token).ConfigureAwait(false));
        }
        foreach (var blob in write.Blobs)
        {
            var oid = Text(Require(await RunAsync(writer, ["hash-object", "-w", "--stdin"], blob.Value.ToArray(), false, token).ConfigureAwait(false)));
            if (oid != blob.Key) throw new GitHubProtocolException();
        }
        await WriteTreeAsync(GitHubCoordinationObjects.LeafEntries(prepared), prepared.LeafTreeOid, token).ConfigureAwait(false);
        await WriteTreeAsync(GitHubCoordinationObjects.RootEntries(prepared), prepared.RootTreeOid, token).ConfigureAwait(false);
        var oidCommit = Text(Require(await RunAsync(writer,
            ["commit-tree", prepared.RootTreeOid, "-p", prepared.ParentOid], Encoding.UTF8.GetBytes(prepared.Message), false, token).ConfigureAwait(false)));
        if (oidCommit != prepared.CommitOid) throw new GitHubProtocolException();
    }

    private async ValueTask WriteTreeAsync(ImmutableArray<GitHubTreeEntry> entries, string expected, CancellationToken token)
    {
        var input = string.Concat(entries.Select(entry => (entry.Mode == GitHubTreeMode.Directory ? "040000 tree " : "100644 blob ")
            + entry.Oid + "\t" + entry.Path + "\n"));
        var actual = Text(Require(await RunAsync(writer, ["mktree"], Encoding.UTF8.GetBytes(input), false, token).ConfigureAwait(false)));
        if (actual != expected) throw new GitHubProtocolException();
    }

    private async ValueTask<byte[]> ReadObjectAsync(string store, string kind, string oid, CancellationToken token)
    {
        if (!IsOid(oid)) throw new GitHubProtocolException();
        var bytes = Require(await RunAsync(store, ["cat-file", kind, oid], null, false, token).ConfigureAwait(false));
        if (GitHubCoordinationObjects.ObjectOid(kind, bytes) != oid) throw new GitHubProtocolException();
        return bytes;
    }

    private async ValueTask<GitHubTree> ReadTreeAsync(string oid, CancellationToken token)
    {
        var bytes = await ReadObjectAsync(observer, "tree", oid, token).ConfigureAwait(false);
        var entries = ImmutableArray.CreateBuilder<GitHubTreeEntry>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var space = Array.IndexOf(bytes, (byte)' ', offset);
            var zero = Array.IndexOf(bytes, (byte)0, space + 1);
            if (space < offset || zero <= space || zero + 21 > bytes.Length || entries.Count >= 5)
                throw new GitHubProtocolException();
            var mode = Encoding.ASCII.GetString(bytes, offset, space - offset);
            var path = Encoding.UTF8.GetString(bytes, space + 1, zero - space - 1);
            var entryOid = Convert.ToHexStringLower(bytes.AsSpan(zero + 1, 20));
            entries.Add(new(path, mode switch
            {
                "40000" => GitHubTreeMode.Directory,
                "100644" => GitHubTreeMode.File,
                _ => throw new GitHubProtocolException()
            }, entryOid, null));
            offset = zero + 21;
        }
        return new(oid, entries.ToImmutable());
    }

    private async ValueTask FetchCommitOnlyAsync(string store, string requested, string expected, string localRef, CancellationToken token)
    {
        var before = await ObjectInventoryAsync(store, token).ConfigureAwait(false);
        Require(await RunAsync(store, ["fetch", "--depth=1", "--filter=tree:0", "--no-tags", "--no-write-fetch-head", remote.AbsoluteUri,
            requested + ":" + localRef], null, true, token).ConfigureAwait(false));
        var after = await ObjectInventoryAsync(store, token).ConfigureAwait(false);
        // Git can include the canonical empty tree even with tree:0. It has no
        // source data; no other tree, blob or source ancestor is accepted.
        const string emptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        if (after.Except(before).Any(row => row != expected + " commit" && row != emptyTree + " tree")
            || Text(Require(await RunAsync(store, ["rev-parse", localRef], null, false, token).ConfigureAwait(false))) != expected)
            throw new GitHubProtocolException();
        _ = await ReadObjectAsync(store, "commit", expected, token).ConfigureAwait(false);
        if (after.Contains(emptyTree + " tree") && (await ReadObjectAsync(store, "tree", emptyTree, token).ConfigureAwait(false)).Length != 0)
            throw new GitHubProtocolException();
    }

    private async ValueTask<HashSet<string>> ObjectInventoryAsync(string store, CancellationToken token)
    {
        var raw = Require(await RunAsync(store, ["cat-file", "--batch-all-objects", "--batch-check=%(objectname) %(objecttype)"],
            null, false, token).ConfigureAwait(false));
        return Encoding.ASCII.GetString(raw).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    }

    private sealed record CommandResult(int Exit, byte[] Output, bool FilterRejected);

    private async ValueTask<CommandResult> RunAsync(string? store, string[] arguments, byte[]? input,
        bool network, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        privateStorage?.RequireCurrent();
        foreach (var path in store is null ? new[] { directory } : new[] { directory, store })
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new GitHubProtocolException(GitHubFailureCode.InvalidRequest);
        }
        token.ThrowIfCancellationRequested();
        var caller = token;
        // The original adapter deadline is observed for every command. Linking
        // its token does not start a new clock or extend the Host allowance.
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token, finalizationDeadline?.Invoke() ?? default, privateStorage?.Lifetime ?? default);
        token = bounded.Token;
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("/usr/bin/git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";
        foreach (var (key, value) in new[] { ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_CONFIG_GLOBAL", "/dev/null"),
            ("GIT_CONFIG_SYSTEM", "/dev/null"), ("GIT_TERMINAL_PROMPT", "0"), ("GIT_NO_REPLACE_OBJECTS", "1"),
            ("GIT_NO_LAZY_FETCH", "1"), ("GIT_AUTHOR_NAME", GitHubCoordinationObjects.ActorName),
            ("GIT_COMMITTER_NAME", GitHubCoordinationObjects.ActorName), ("GIT_AUTHOR_EMAIL", GitHubCoordinationObjects.ActorEmail),
            ("GIT_COMMITTER_EMAIL", GitHubCoordinationObjects.ActorEmail),
            ("GIT_AUTHOR_DATE", "@" + GitHubCoordinationObjects.ActorUnixSeconds.ToString(CultureInfo.InvariantCulture) + " +0000"),
            ("GIT_COMMITTER_DATE", "@" + GitHubCoordinationObjects.ActorUnixSeconds.ToString(CultureInfo.InvariantCulture) + " +0000") })
            start.Environment[key] = value;
        start.Environment["GIT_CEILING_DIRECTORIES"] = directory;
        start.Environment["LC_ALL"] = "C";
        if (network && authentication is not null)
        {
            start.Environment["GIT_CONFIG_COUNT"] = "1";
            start.Environment["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraHeader";
            start.Environment["GIT_CONFIG_VALUE_0"] = authentication;
        }
        if (store is not null) { start.ArgumentList.Add("--git-dir"); start.ArgumentList.Add(store); }
        foreach (var setting in new[] { "core.hooksPath=/dev/null", "init.templateDir=", "maintenance.auto=false", "gc.auto=0", "gc.autoPackLimit=0", "credential.helper=", "http.followRedirects=false",
            "http.maxRetries=0", "http.proxy=", "protocol.allow=never", "protocol.https.allow=always",
            "protocol.http.allow=" + (remote.Scheme == "http" ? "always" : "never"), "protocol.file.allow=always",
            "commit.gpgSign=false", "transfer.fsckObjects=true" })
        { start.ArgumentList.Add("-c"); start.ArgumentList.Add(setting); }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (network) RemoteCommands++;
        if (!process.Start()) throw new GitHubProtocolException(GitHubFailureCode.HostFailure);
        using var cancellation = token.UnsafeRegister(static state =>
        {
            var child = (Process)state!;
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }, process);
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, token);
            var stderr = ReadBoundedAsync(process.StandardError.BaseStream, token);
            if (input is not null) await process.StandardInput.BaseStream.WriteAsync(input, token).ConfigureAwait(false);
            process.StandardInput.Close();
            var exited = process.WaitForExitAsync(token);
            while (!exited.IsCompleted)
            {
                if (stdout.IsFaulted) await stdout.ConfigureAwait(false);
                if (stderr.IsFaulted) await stderr.ConfigureAwait(false);
                CheckStoreBound();
                await Task.WhenAny(exited, Task.Delay(100, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            await exited.ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            // Retain one closed fact, never raw transport/credential diagnostics.
            var filterRejected = Encoding.UTF8.GetString(error).Contains("filtering not recognized by server", StringComparison.Ordinal);
            CheckStoreBound();
            return new(process.ExitCode, output, filterRejected);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        { throw new OperationCanceledException(caller); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > MaximumOutputBytes) throw new GitHubProtocolException();
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }

    private byte[] Require(CommandResult result) => result.Exit == 0 && !result.FilterRejected ? result.Output : throw new GitHubProtocolException();

    private void CheckDocument(GitHubCoordinationDocument document)
    {
        if (!document.RepositoryId.Equals(authority.RepositoryOwner + "/" + authority.RepositoryName, StringComparison.OrdinalIgnoreCase)
            || document.TargetRef != target || document.CampaignLineage != authority.CampaignLineage)
            throw new GitHubProtocolException();
    }

    private void CheckStoreBound()
    {
        long bytes = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            long length;
            try { length = new FileInfo(file).Length; }
            // Git atomically replaces temporary packs while this live monitor
            // samples sizes. A vanished temporary is not a transport failure.
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if (++count > 8192 || (bytes += length) > MaximumStoreBytes)
                throw new GitHubProtocolException();
        }
    }
    private static string Text(byte[] bytes) => Encoding.ASCII.GetString(bytes).TrimEnd('\n');
    private static bool IsOid(string value) => value.Length == 40 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            authentication = null;
            try
            {
                privateStorage?.RequireCleanupRoot();
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            { }
            finally { gate.Dispose(); }
        }
        return ValueTask.CompletedTask;
    }
}
