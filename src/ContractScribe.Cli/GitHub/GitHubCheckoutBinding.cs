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
    internal static async Task<string?> ValidateAsync(GitHubApiClient client, GitHubCoordinationStore coordination, string targetOid,
        CampaignPreflightResult preflight, ProductionRepositorySessionBundle bundle, CancellationToken token)
    {
        try
        {
            var capture = bundle.Session.CaptureDocumentationPatchRepositoryBaseline(token);
            if (capture.Baseline is not { } baseline || capture.Status != DocumentationPatchRepositoryBaselineStatus.Captured)
                return "checkout-mismatch";
            var root = preflight.RepositoryRoot;
            string Relative(string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            var semantic = baseline.SemanticInputs.Select(input => input.RepositoryPath)
                .Concat(baseline.ProtectedInputPaths).Concat(baseline.CompilerFileInputs.Select(response => response.RepositoryPath)).ToHashSet(StringComparer.Ordinal);
            var declaredInputs = DeclaredSemanticInputs(root, baseline, token, out var compilerInputsUnverifiable);
            if (declaredInputs is null) return compilerInputsUnverifiable ? "compiler-input-unverifiable" : "checkout-mismatch";
            if (!VerifiableCompilerFiles(baseline, token)) return "compiler-input-unverifiable";
            var excluded = preflight.Configuration.Sources.Select(source => Relative(source.Path)).ToHashSet(StringComparer.Ordinal);
            excluded.Add(Relative(preflight.PolicyPath));
            bool RepositoryDeclared(string path) => declaredInputs.Any(pattern => pattern(path))
                || baseline.RepositoryInputGlobs.Any(matcher => matcher(path));
            bool Declared(string path) => RepositoryDeclared(path) || baseline.DeclaredInputGlobs.Any(matcher => matcher(path));
            bool Relevant(string path)
            {
                token.ThrowIfCancellationRequested();
                if (excluded.Contains(path) && !semantic.Contains(path)
                    && !Declared(path)) return false;
                if (path.Split('/').Any(part => part is ".git")) return false;
                if (path.Split('/').Any(part => part is "obj" or "bin")) return RepositoryDeclared(path);
                if (semantic.Contains(path) || path == preflight.InputIdentity) return true;
                if (Declared(path)) return true;
                var name = path[(path.LastIndexOf('/') + 1)..];
                return name is "Directory.Build.props" or "Directory.Build.targets" or "Directory.Packages.props"
                    or ".editorconfig" or "global.json" or "NuGet.Config" or "nuget.config" or "AGENTS.md";
            }
            var allFiles = await coordination.ReadSourceFilesAsync(targetOid, token).ConfigureAwait(false);
            if (allFiles is null) return "checkout-mismatch";
            // SDK outputs are reconstructed from the bound projects, not Git source.
            // A tracked input masquerading as such an output is not admitted.
            if (allFiles.Keys.Any(path =>
            {
                token.ThrowIfCancellationRequested();
                return path.Split('/').Any(part => part is "obj" or "bin")
                    && (semantic.Contains(path) || Declared(path));
            }))
                return "checkout-mismatch";
            var remote = allFiles.Where(pair => Relevant(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var local = baseline.Entries.Where(entry => Relevant(entry.RepositoryPath))
                .ToDictionary(entry => entry.RepositoryPath, StringComparer.Ordinal);
            // Both directions matter: a missing remote-only Compile glob member is invisible to local Roslyn.
            if (!local.Keys.Order(StringComparer.Ordinal).SequenceEqual(remote.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                return "checkout-mismatch";
            foreach (var pair in remote)
            {
                var original = local[pair.Key];
                if (pair.Value.Mode is not (GitHubTreeMode.File or GitHubTreeMode.Executable)
                    || pair.Value.Size is { } size && size != original.Bytes.Length
                    || GitHubProposalObjects.ObjectOid("blob", original.Bytes.AsSpan()) != pair.Value.Oid)
                    return "checkout-mismatch";
            }
            var target = GitHubObservedProposalIntegrity.Read(await coordination.ReadDataRefAsync(client.ReadAuthority.TargetRef, token).ConfigureAwait(false));
            return target?.Oid == targetOid
                && baseline.Rebind(token).Status == DocumentationPatchRepositoryRebindStatus.Unchanged ? null : "checkout-mismatch";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (GitHubCoordinationException exception) when (exception.TransportFailure is not null) { throw; }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { return "checkout-mismatch"; }
    }

    // Roslyn4.14 response files are lexed per line, with hash comments. Only
    // flags with no file input semantics are admitted here. This inspects the
    // fd-captured bytes; it never feeds arbitrary @/file arguments to a decoder.
    private static readonly HashSet<string> ScalarResponseOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "define", "d", "langversion", "nullable", "warn", "nowarn", "warnaserror", "warnaserror+", "warnaserror-",
        "checked", "checked+", "checked-", "unsafe", "unsafe+", "unsafe-", "optimize", "optimize+", "optimize-",
        "deterministic", "deterministic+", "deterministic-", "target", "t", "platform", "debug", "debug+", "debug-",
        "features", "nologo", "utf8output", "fullpaths", "errorendlocation", "parallel", "parallel+", "parallel-",
    };

    private static bool VerifiableCompilerFiles(DocumentationPatchRepositoryBaseline baseline, CancellationToken token)
    {
        foreach (var response in baseline.CompilerFileInputs)
        {
            token.ThrowIfCancellationRequested();
            if (response.IsOutput || response.LoadSha256 is null
                || !baseline.TryGetEntry(response.RepositoryPath, out var file) || file.Sha256 != response.LoadSha256) return false;
            using var stream = new MemoryStream(file.Bytes.ToArray(), writable: false);
            if (response.Kind == DocumentationPatchCompilerInputKind.RuleSet)
            {
                using var xml = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 16 * 1024 * 1024,
                });
                try
                {
                    var rules = XDocument.Load(xml);
                    if (rules.Root?.Name.LocalName != "RuleSet"
                        || rules.Descendants().Any(element => element.Name.LocalName == "Include")) return false;
                }
                catch (XmlException) { return false; }
                continue;
            }
            if (response.Kind != DocumentationPatchCompilerInputKind.ResponseFile) return false;
            using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            try { if (!ScalarResponseArguments(reader, token)) return false; }
            catch (System.Text.DecoderFallbackException) { return false; }
        }
        return true;
    }

    internal static bool ScalarResponseArguments(TextReader reader, CancellationToken token = default)
    {
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            foreach (var argument in Microsoft.CodeAnalysis.CommandLineParser.SplitCommandLineIntoArguments(line, removeHashComments: true))
            {
                if (argument.Length < 2 || argument[0] is not ('/' or '-')) return false;
                var colon = argument.IndexOf(':');
                var name = colon < 0 ? argument[1..] : argument[1..colon];
                if (!ScalarResponseOptions.Contains(name)) return false;
            }
        }
        return true;
    }

    // Bind candidate members of the actual repository import closure, including
    // empty globs. This reads only fd-captured bytes and recorded evaluation
    // provenance; it neither evaluates MSBuild nor executes build logic.
    private static List<Func<string, bool>>? DeclaredSemanticInputs(string root,
        DocumentationPatchRepositoryBaseline baseline, CancellationToken token, out bool compilerInputsUnverifiable)
    {
        compilerInputsUnverifiable = false;
        var patterns = new List<Func<string, bool>>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var entries = baseline.Entries.ToDictionary(entry => entry.RepositoryPath, StringComparer.Ordinal);
        foreach (var declaration in baseline.BuildDeclarations)
        {
            // These generated imports are reconstructed by restore, not Git source.
            if (declaration.RepositoryPath.Split('/').Any(part => part is "obj" or "bin")) continue;
            if (!entries.TryGetValue(declaration.RepositoryPath, out var file)) return null;
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
                DocumentationPatchBuildDeclaration.IsCompilerInput(element.Name.LocalName)
                || element.Name.LocalName.Equals("Import", StringComparison.OrdinalIgnoreCase)
                || element.Name.LocalName.Equals("HintPath", StringComparison.OrdinalIgnoreCase)
                || declaration.ReferenceCandidateElements.Contains(ElementPath(element))))
            {
                var compilerProperty = item.Name.LocalName.Equals("CompilerResponseFile", StringComparison.OrdinalIgnoreCase)
                    || item.Name.LocalName.Equals("CodeAnalysisRuleSet", StringComparison.OrdinalIgnoreCase);
                if (compilerProperty
                    && item.Ancestors().Any(element => element.Name.LocalName == "Target"))
                {
                    compilerInputsUnverifiable = true;
                    return null;
                }
                var conditions = item.AncestorsAndSelf().Where(element => element.Attribute("Condition") is not null)
                    .Select(element => (Element: element, Text: element.Attribute("Condition")!.Value)).ToArray();
                var presenceDependent = false;
                foreach (var condition in conditions)
                {
                    // A false Exists condition can become true on a remote-only
                    // input. Bind its literal candidates as well as the item.
                    foreach (Match match in Regex.Matches(condition.Text, @"\bExists\s*\(\s*(['""])(.*?)\1\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    {
                        presenceDependent = true;
                        if (!AddPattern(match.Groups[2].Value, declaration, allowExternal: true,
                            relativeToDeclaration: condition.Element.Name.LocalName is "Import" or "ImportGroup" or "PropertyGroup")) return null;
                    }
                    if (condition.Text.Contains("Exists", StringComparison.OrdinalIgnoreCase)
                        && !Regex.IsMatch(condition.Text, @"\bExists\s*\(\s*(['""])(.*?)\1\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) return null;
                }
                var elementPath = ElementPath(item);
                var referenceCandidate = declaration.ReferenceCandidateElements.Contains(elementPath);
                if (conditions.Length > 0 && !presenceDependent
                    && !declaration.ActiveElements.Contains(elementPath)) continue;
                var active = declaration.ActiveElements.Contains(elementPath);
                var deferred = item.Ancestors().Any(element => element.Name.LocalName == "Target");
                // Effective compiler properties already have evaluated carrier
                // provenance. Only inactive presence-dependent alternatives need
                // a static candidate; overwritten values are not inputs.
                if (compilerProperty && (active || !presenceDependent)) continue;
                var values = compilerProperty || item.Name.LocalName.Equals("HintPath", StringComparison.OrdinalIgnoreCase) ? new[] { item.Value }
                    : item.Name.LocalName.Equals("Reference", StringComparison.OrdinalIgnoreCase)
                    ? item.Attributes("Include").Select(attribute => attribute.Value).Where(value => value.Contains('/') || value.Contains('\\')
                        || Path.GetExtension(value).ToLowerInvariant() is ".dll" or ".exe" or ".winmd")
                    : item.Attributes().Where(attribute => new[] { "Include", "Update", "Project" }
                        .Contains(attribute.Name.LocalName, StringComparer.OrdinalIgnoreCase)).Select(attribute => attribute.Value);
                var allowExternal = compilerProperty || item.Name.LocalName is "Analyzer" or "Import" or "Reference" or "HintPath";
                foreach (var value in values)
                    foreach (var pattern in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        var recordedGlob = pattern.IndexOfAny(['*', '?']) >= 0 && item.Name.LocalName is not ("Reference" or "Import") && active && !deferred;
                        // Current evaluated items/globs honor Remove/Exclude. An
                        // inactive literal Update/removed item is not an input.
                        var include = !recordedGlob && (active || deferred || item.Name.LocalName == "Import" || presenceDependent);
                        if (!AddPattern(pattern, declaration, allowExternal, include, referenceCandidate,
                            relativeToDeclaration: item.Name.LocalName == "Import")) return null;
                    }
            }
        }
        return patterns;

        static string ElementPath(XElement element) => string.Join("/", element.AncestorsAndSelf().Reverse().Skip(1)
            .Select(ancestor => ancestor.ElementsBeforeSelf().Count()));

        bool AddPattern(string pattern, DocumentationPatchBuildDeclaration declaration, bool allowExternal, bool include = true,
            bool referenceCandidate = false, bool relativeToDeclaration = false)
        {
            token.ThrowIfCancellationRequested();
            var value = pattern.Replace('\\', '/')
                .Replace("$(MSBuildProjectDirectory)", Path.Join(root, declaration.ProjectDirectory), StringComparison.OrdinalIgnoreCase)
                .Replace("$(MSBuildThisFileDirectory)", Path.GetDirectoryName(Path.Join(root, declaration.RepositoryPath)) + "/", StringComparison.OrdinalIgnoreCase);
            if (value.IndexOfAny(['$', '@', '%']) >= 0) return false;
            var wildcard = value.IndexOfAny(['*', '?']);
            if (wildcard >= 0 && value[wildcard..].Split('/').Contains("..", StringComparer.Ordinal)) return false;
            var baseDirectory = relativeToDeclaration
                ? Path.GetDirectoryName(Path.Join(root, declaration.RepositoryPath))! : Path.GetFullPath(declaration.ProjectDirectory, root);
            var absolute = Path.GetFullPath(value, baseDirectory);
            var relative = Path.GetRelativePath(root, absolute).Replace(Path.DirectorySeparatorChar, '/');
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return allowExternal; // Existing resolved SDK/package/compiler trust; never a Git blob.
            if (relative.Length > 1024 || patterns.Count >= 1024) return false;
            if (!include || !names.Add(relative)) return true;
            var expression = Regex.Escape(relative).Replace("\\*\\*/", "(?:.*/)?", StringComparison.Ordinal)
                .Replace("\\*\\*", ".*", StringComparison.Ordinal)
                .Replace("\\*", "[^/]*", StringComparison.Ordinal)
                .Replace("\\?", "[^/]", StringComparison.Ordinal);
            var matcher = new Regex("\\A" + expression + "\\z",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
            patterns.Add(path => matcher.IsMatch(path) && (!referenceCandidate || declaration.IsReferenceCandidate(path)));
            return true;
        }
    }
}
