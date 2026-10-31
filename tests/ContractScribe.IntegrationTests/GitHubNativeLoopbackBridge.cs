using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ContractScribe.Roslyn.IntegrationTests;

// Both existing API effects and real receive-pack effects use one synthetic
// object/ref store. Every import/export checks actual public Git preimages.
internal sealed class GitHubNativeLoopbackBridge : IAsyncDisposable
{
    private readonly GitHubProposalLoopbackServer api;
    private readonly string directory;
    private readonly string repository;
    private readonly CancellationToken token;
    private readonly HashSet<string> knownObjects = new(StringComparer.Ordinal);
    private readonly HashSet<string> knownRefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> exportedRefs = new(StringComparer.Ordinal);
    private int importedMutationCompletions;
    private readonly GitHubCoordinationGitTransportTests.NativeServer server;
    internal Uri Endpoint => server.Endpoint;
    internal int GitHttpRequests => server.RequestCount;
    internal int GitRemoteCommands => server.Advertisements;
    internal string? DeniedObjectOid { set => server.DeniedObjectOid = value; }
    internal int DeniedObjectRequests => server.DeniedObjectRequests;
    internal int GitMutations => server.ReceivePackRequests;
    internal int? GitMutationsAtObjectDenial => server.MutationsAtObjectDenial;

    internal GitHubNativeLoopbackBridge(GitHubProposalLoopbackServer api, string parent, CancellationToken token)
    {
        this.api = api; this.token = token;
        directory = Path.Join(parent, "native-remote"); Directory.CreateDirectory(directory);
        repository = Path.Join(directory, "remote.git");
        Run(["init", "--bare", "--object-format=sha1", repository], root: directory);
        Run(["config", "http.receivepack", "true"]);
        ExportApiEffects();
        server = new(directory, token);
        Assert.Null(api.Before); Assert.Null(api.After);
        api.Before = _ => ImportNativeEffects();
        api.After = _ => ExportApiEffects();
    }

    internal void ApplyExternalApiChange(Action change)
    { ImportNativeEffects(); change(); ExportApiEffects(); }

    private void ExportApiEffects()
    {
        foreach (var (oid, bytes) in api.Blobs) WriteObject("blob", oid, bytes);
        foreach (var (oid, entries) in api.Trees)
        {
            if (knownObjects.Contains(oid)) continue;
            using var bytes = new MemoryStream();
            foreach (var entry in entries.OrderBy(item => item.Path + (item.Mode is "040000" or "40000" ? "/" : "\0"), StringComparer.Ordinal))
            {
                bytes.Write(Encoding.UTF8.GetBytes((entry.Mode == "040000" ? "40000" : entry.Mode) + " " + entry.Path + "\0"));
                bytes.Write(Convert.FromHexString(entry.Oid));
            }
            WriteObject("tree", oid, bytes.ToArray());
        }
        foreach (var (oid, commit) in api.Commits)
        {
            if (knownObjects.Contains(oid)) continue;
            string raw;
            if (commit["checkpointSignature"] is { } signed)
            {
                var payload = signed["payload"]!.GetValue<string>();
                var split = payload.IndexOf("\n\n", StringComparison.Ordinal);
                raw = payload[..(split + 1)] + "gpgsig " + signed["signature"]!.GetValue<string>().TrimEnd('\n')
                    .Replace("\n", "\n ", StringComparison.Ordinal) + "\n" + payload[(split + 1)..];
            }
            else raw = "tree " + commit["tree"]!["sha"]!.GetValue<string>() + "\n"
                + string.Concat(commit["parents"]!.AsArray().Select(parent => "parent " + parent!["sha"]!.GetValue<string>() + "\n"))
                + ActorLine("author", commit["author"]!) + ActorLine("committer", commit["committer"]!) + "\n"
                + commit["message"]!.GetValue<string>();
            WriteObject("commit", oid, Encoding.UTF8.GetBytes(raw));
        }
        foreach (var (name, oid) in api.Refs)
        {
            if (exportedRefs.GetValueOrDefault(name) != oid)
            { Run(["update-ref", name, oid]); exportedRefs[name] = oid; }
            knownRefs.Add(name);
        }
        foreach (var removed in knownRefs.Except(api.Refs.Keys).ToArray())
        { Run(["update-ref", "-d", removed]); knownRefs.Remove(removed); exportedRefs.Remove(removed); }
    }

    private void WriteObject(string kind, string oid, byte[] bytes)
    {
        if (!knownObjects.Add(oid)) return;
        Assert.Equal(oid, Text(Run(["hash-object", "-w", "-t", kind, "--stdin"], bytes)));
    }

    internal void ImportNativeEffects()
    {
        // API reads do not trigger redundant Git processes. Only an actual
        // completed receive-pack can add objects or change native remote refs.
        if (importedMutationCompletions == server.MutationCompletions) return;
        importedMutationCompletions = server.MutationCompletions;
        var current = Text(Run(["for-each-ref", "--format=%(refname) %(objectname)"]));
        var refs = current.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(' ')).ToDictionary(row => row[0], row => row[1]);
        var objects = Text(Run(["cat-file", "--batch-all-objects", "--batch-check=%(objectname) %(objecttype)"]));
        foreach (var line in objects.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' '); var oid = fields[0]; var kind = fields[1];
            if (knownObjects.Contains(oid)) continue;
            var bytes = Run(["cat-file", kind, oid]);
            switch (kind)
            {
                case "blob": Assert.Equal(oid, api.AddBlob(bytes)); break;
                case "tree":
                    var entries = new List<GitHubProposalLoopbackServer.Entry>();
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        var space = Array.IndexOf(bytes, (byte)' ', offset); var zero = Array.IndexOf(bytes, (byte)0, space + 1);
                        var mode = Encoding.ASCII.GetString(bytes, offset, space - offset);
                        var path = Encoding.UTF8.GetString(bytes, space + 1, zero - space - 1);
                        entries.Add(new(path, mode == "40000" ? "040000" : mode, Convert.ToHexStringLower(bytes.AsSpan(zero + 1, 20))));
                        offset = zero + 21;
                    }
                    Assert.Equal(oid, api.AddTree(entries.ToArray())); break;
                case "commit":
                    var raw = Encoding.UTF8.GetString(bytes); var split = raw.IndexOf("\n\n", StringComparison.Ordinal);
                    var header = raw[..split].Split('\n');
                    Assert.DoesNotContain(header, item => item.StartsWith("gpgsig ", StringComparison.Ordinal));
                    var input = new JsonObject
                    {
                        ["tree"] = header.Single(item => item.StartsWith("tree ", StringComparison.Ordinal))[5..],
                        ["parents"] = new JsonArray(header.Where(item => item.StartsWith("parent ", StringComparison.Ordinal)).Select(item => (JsonNode)JsonValue.Create(item[7..])!).ToArray()),
                        ["message"] = raw[(split + 2)..],
                        ["author"] = ReadActor(header.Single(item => item.StartsWith("author ", StringComparison.Ordinal))[7..]),
                        ["committer"] = ReadActor(header.Single(item => item.StartsWith("committer ", StringComparison.Ordinal))[10..]),
                    };
                    Assert.Equal(oid, api.AddCommit(input)); break;
                default: Assert.Fail("Unexpected synthetic native object kind"); break;
            }
            knownObjects.Add(oid);
        }
        api.Refs.Clear();
        exportedRefs.Clear();
        foreach (var pair in refs)
        { api.Refs.Add(pair.Key, pair.Value); knownRefs.Add(pair.Key); exportedRefs.Add(pair.Key, pair.Value); }
    }

    private static JsonObject ReadActor(string value)
    {
        var left = value.LastIndexOf(" <", StringComparison.Ordinal); var right = value.LastIndexOf("> ", StringComparison.Ordinal);
        var fields = value[(right + 2)..].Split(' '); Assert.Equal("+0000", fields[1]);
        return new()
        {
            ["name"] = value[..left],
            ["email"] = value[(left + 2)..right],
            ["date"] = DateTimeOffset.FromUnixTimeSeconds(long.Parse(fields[0], CultureInfo.InvariantCulture))
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
        };
    }

    private static string ActorLine(string role, JsonNode actor) => role + " " + actor["name"]!.GetValue<string>()
        + " <" + actor["email"]!.GetValue<string>() + "> " + DateTimeOffset.Parse(actor["date"]!.GetValue<string>(),
            CultureInfo.InvariantCulture).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + " +0000\n";

    private byte[] Run(string[] arguments, byte[]? input = null, string? root = null)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("/usr/bin/git")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory
        };
        start.Environment.Clear();
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null"; start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (root is null) { start.ArgumentList.Add("--git-dir"); start.ArgumentList.Add(repository); }
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.hooksPath=/dev/null");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var output = new MemoryStream();
        var copying = process.StandardOutput.BaseStream.CopyToAsync(output, token); var error = process.StandardError.ReadToEndAsync(token);
        if (input is not null) process.StandardInput.BaseStream.Write(input);
        process.StandardInput.Close();
        try { process.WaitForExitAsync(token).GetAwaiter().GetResult(); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
        copying.GetAwaiter().GetResult(); Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
        return output.ToArray();
    }

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimEnd('\n');
    public async ValueTask DisposeAsync()
    { api.Before = api.After = null; await server.DisposeAsync(); Directory.Delete(directory, recursive: true); }
}
