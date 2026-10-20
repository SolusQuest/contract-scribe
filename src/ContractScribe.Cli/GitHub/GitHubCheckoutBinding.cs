using ContractScribe.GitHub.GitData;
using ContractScribe.GitHub.Coordination;
using ContractScribe.GitHub.Transport;
using ContractScribe.Roslyn;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;

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
            var declaredInputs = DeclaredSemanticInputs(root, baseline, projectAreas, token);
            if (declaredInputs is null) return false;
            var excluded = preflight.Configuration.Sources.Select(source => Relative(source.Path)).ToHashSet(StringComparer.Ordinal);
            excluded.Add(Relative(preflight.PolicyPath));
            bool Relevant(string path)
            {
                token.ThrowIfCancellationRequested();
                if (excluded.Contains(path)) return false;
                if (path.Split('/').Any(part => part is "obj" or "bin" or ".git")) return false;
                if (semantic.Contains(path) || path == preflight.InputIdentity) return true;
                // A source/import glob can currently be empty outside every loaded
                // project directory. Remote-only members must still be visible here.
                if (Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".csproj" or ".props" or ".targets" or ".editorconfig" or ".globalconfig")
                    return true;
                if (declaredInputs.Any(pattern => pattern.IsMatch(path))) return true;
                var name = path[(path.LastIndexOf('/') + 1)..];
                return name is "Directory.Build.props" or "Directory.Build.targets" or "Directory.Packages.props"
                    or ".editorconfig" or "global.json" or "NuGet.Config" or "nuget.config" or "AGENTS.md";
            }
            var commit = GitHubObservedProposalIntegrity.Read(await client.GetCommitAsync(targetOid, token).ConfigureAwait(false));
            if (commit?.Oid != targetOid) return false;
            var allFiles = await GitHubObservedProposalIntegrity.ReadTreeAsync(client, commit.TreeOid, token).ConfigureAwait(false);
            if (allFiles is null) return false;
            // SDK outputs are reconstructed from the bound projects, not Git source.
            // A tracked input masquerading as such an output is not admitted.
            if (allFiles.Keys.Any(path =>
            {
                token.ThrowIfCancellationRequested();
                return path.Split('/').Any(part => part is "obj" or "bin")
                    && (semantic.Contains(path) || declaredInputs.Any(pattern => pattern.IsMatch(path)));
            }))
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
            var target = GitHubObservedProposalIntegrity.Read(await client.GetRefAsync(client.ReadAuthority.TargetRef, token).ConfigureAwait(false));
            return target?.Oid == targetOid
                && baseline.Rebind(token).Status == DocumentationPatchRepositoryRebindStatus.Unchanged;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (GitHubCoordinationException exception) when (exception.TransportFailure is not null) { throw; }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { return false; }
    }

    // A local load cannot enumerate an empty external item glob. Bound its
    // candidate paths from the unchanged project/import declaration, so
    // remote-only AdditionalFiles and analyzer configuration remain visible.
    // This does not evaluate MSBuild: unresolved dynamic item paths fail closed.
    private static List<Regex>? DeclaredSemanticInputs(string root,
        DocumentationPatchRepositoryBaseline baseline, string[] projectAreas, CancellationToken token)
    {
        var patterns = new List<Regex>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in baseline.Entries.Where(entry =>
            Path.GetExtension(entry.RepositoryPath).ToLowerInvariant() is ".csproj" or ".props" or ".targets"))
        {
            token.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(file.Bytes.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 16 * 1024 * 1024,
            });
            var document = XDocument.Load(reader);
            foreach (var item in document.Descendants().Where(element =>
                new[] { "Compile", "AdditionalFiles", "EditorConfigFiles", "GlobalAnalyzerConfigFiles", "AnalyzerConfigFiles" }
                    .Contains(element.Name.LocalName, StringComparer.OrdinalIgnoreCase)))
                foreach (var attribute in item.Attributes().Where(attribute =>
                    new[] { "Include", "Update" }.Contains(attribute.Name.LocalName, StringComparer.OrdinalIgnoreCase)))
                    foreach (var pattern in attribute.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        foreach (var projectArea in projectAreas)
                        {
                            token.ThrowIfCancellationRequested();
                            var value = pattern.Replace('\\', '/')
                                .Replace("$(MSBuildProjectDirectory)", Path.Join(root, projectArea), StringComparison.OrdinalIgnoreCase)
                                .Replace("$(MSBuildThisFileDirectory)", Path.GetDirectoryName(Path.Join(root, file.RepositoryPath)) + "/", StringComparison.OrdinalIgnoreCase);
                            if (value.IndexOfAny(['$', '@', '%']) >= 0) return null;
                            var wildcard = value.IndexOfAny(['*', '?']);
                            // Do not normalize parent traversal after a wildcard:
                            // its meaning depends on expansion rather than lexical paths.
                            if (wildcard >= 0 && value[wildcard..].Split('/').Contains("..", StringComparer.Ordinal)) return null;
                            var absolute = Path.GetFullPath(value, Path.GetFullPath(projectArea, root));
                            var relative = Path.GetRelativePath(root, absolute).Replace(Path.DirectorySeparatorChar, '/');
                            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return null;
                            if (relative.Length > 1024 || patterns.Count >= 1024) return null;
                            if (!names.Add(relative)) continue;
                            var expression = Regex.Escape(relative).Replace("\\*\\*/", "(?:.*/)?", StringComparison.Ordinal)
                                .Replace("\\*\\*", ".*", StringComparison.Ordinal)
                                .Replace("\\*", "[^/]*", StringComparison.Ordinal)
                                .Replace("\\?", "[^/]", StringComparison.Ordinal);
                            patterns.Add(new Regex("\\A" + expression + "\\z",
                                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)));
                        }
        }
        return patterns;
    }
}
