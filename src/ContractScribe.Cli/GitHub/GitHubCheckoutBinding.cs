using ContractScribe.GitHub.GitData;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

// A run-local comparison, not a persisted manifest or an authority to update the checkout.
internal static class GitHubCheckoutBinding
{
    internal static async Task<bool> ValidateAsync(GitHubApiClient client, string targetOid,
        CampaignPreflightResult preflight, ProductionRepositorySessionBundle bundle, CancellationToken token)
    {
        try
        {
            var capture = bundle.Session.CaptureDocumentationPatchRepositoryBaseline(token);
            if (capture.Baseline is not { } baseline || capture.Status != DocumentationPatchRepositoryBaselineStatus.Captured)
                return false;
            var root = preflight.RepositoryRoot;
            string Relative(string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            var projectAreas = bundle.Session.Projects.Select(project =>
                Relative(Path.GetDirectoryName(project.Project.FilePath!)!)).Distinct(StringComparer.Ordinal).ToArray();
            var semantic = baseline.SemanticInputs.Select(input => input.RepositoryPath).ToHashSet(StringComparer.Ordinal);
            var semanticAreas = semantic.Select(path => path.Contains('/') ? path[..path.LastIndexOf('/')] : ".").ToArray();
            var excluded = preflight.Configuration.Sources.Select(source => Relative(source.Path)).ToHashSet(StringComparer.Ordinal);
            excluded.Add(Relative(preflight.PolicyPath));
            bool InArea(string path, string area) => area == "." || path.StartsWith(area + "/", StringComparison.Ordinal);
            bool Relevant(string path)
            {
                if (excluded.Contains(path)) return false;
                if (path.Split('/').Any(part => part is "obj" or "bin" or ".git")) return false;
                if (semantic.Contains(path) || path == preflight.InputIdentity) return true;
                // A source/import glob can currently be empty outside every loaded
                // project directory. Remote-only members must still be visible here.
                if (Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".csproj" or ".props" or ".targets" or ".editorconfig" or ".globalconfig")
                    return true;
                if (projectAreas.Concat(semanticAreas).Any(area => InArea(path, area))) return true;
                var name = path[(path.LastIndexOf('/') + 1)..];
                return name is "Directory.Build.props" or "Directory.Build.targets" or "Directory.Packages.props"
                    or ".editorconfig" or "global.json" or "NuGet.Config" or "nuget.config" or "AGENTS.md";
            }
            var commit = (await client.GetCommitAsync(targetOid, token).ConfigureAwait(false)).Value;
            if (commit?.Oid != targetOid) return false;
            var allFiles = await GitHubObservedProposalIntegrity.ReadTreeAsync(client, commit.TreeOid, token).ConfigureAwait(false);
            if (allFiles is null) return false;
            // SDK outputs are reconstructed from the bound projects, not Git source.
            // A tracked input masquerading as such an output is not admitted.
            if (allFiles.Keys.Any(path => semantic.Contains(path) && path.Split('/').Any(part => part is "obj" or "bin")))
                return false;
            var remote = allFiles.Where(pair => Relevant(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var local = baseline.Entries.Where(entry => Relevant(entry.RepositoryPath))
                .ToDictionary(entry => entry.RepositoryPath, StringComparer.Ordinal);
            // Both directions matter: a missing remote-only Compile glob member is invisible to local Roslyn.
            if (!local.Keys.Order(StringComparer.Ordinal).SequenceEqual(remote.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                return false;
            foreach (var pair in remote)
            {
                var original = local[pair.Key];
                if (pair.Value.Mode is not (GitHubTreeMode.File or GitHubTreeMode.Executable)
                    || pair.Value.Size is { } size && size != original.Bytes.Length
                    || GitHubProposalObjects.ObjectOid("blob", original.Bytes.AsSpan()) != pair.Value.Oid)
                    return false;
            }
            var target = await client.GetRefAsync(client.ReadAuthority.TargetRef, token).ConfigureAwait(false);
            return target.Value?.Oid == targetOid
                && baseline.Rebind(token).Status == DocumentationPatchRepositoryRebindStatus.Unchanged;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { return false; }
    }
}
