namespace ContractScribe.Core;

internal sealed class CampaignPlanningSourceSessionIndex
{
    private readonly Dictionary<string, string> projectByContext = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> repositoryShaByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> generatedShaByScopedIdentity = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RepositoryBoundFact> repositoryByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RepositoryPhysicalFact> repositoryByPhysicalCommitment =
        new(StringComparer.Ordinal);

    private CampaignPlanningSourceSessionIndex()
    {
    }

    internal static CampaignPlanningSourceSessionIndex Build(
        IEnumerable<DocumentationObservation> observations)
    {
        var index = new CampaignPlanningSourceSessionIndex();
        foreach (var observation in observations)
        {
            var context = observation.Subject.ParentSymbolRef.CompilationContextRef;
            var declarationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var declaration in observation.Declarations)
            {
                Require(declaration is not null
                        && declaration.Source is not null
                        && declarationIds.Add(declaration.DeclarationId),
                    "Observation declarations must be non-null and unique within one subject.");
                index.AddObservedSource(context, declaration!.Source!);
            }
        }

        return index;
    }

    internal void BindSource(SymbolRef symbol, CampaignPlanningSourceAuthority source)
    {
        var context = symbol.CompilationContextRef;
        var project = ProjectIdentity(context);
        switch (source)
        {
            case CampaignPlanningRepositorySourceAuthority repository:
                Require(repositoryShaByPath.TryGetValue(repository.Path, out var observedSha)
                        && observedSha == repository.ContentSha256,
                    "Repository source authority must match one current observed lexical source exactly.");
                var bound = new RepositoryBoundFact(
                    repository.PhysicalSourceCommitmentSha256,
                    repository.ContentSha256,
                    repository.Encoding,
                    repository.Writable);
                Require(!repositoryByPath.TryGetValue(repository.Path, out var existingPath)
                        ? repositoryByPath.TryAdd(repository.Path, bound)
                        : existingPath == bound,
                    "One canonical repository path cannot map to conflicting current physical source authority.");
                var physical = new RepositoryPhysicalFact(
                    repository.ContentSha256,
                    repository.Encoding,
                    repository.Writable);
                Require(!repositoryByPhysicalCommitment.TryGetValue(
                            repository.PhysicalSourceCommitmentSha256,
                            out var existingPhysical)
                        ? repositoryByPhysicalCommitment.TryAdd(
                            repository.PhysicalSourceCommitmentSha256,
                            physical)
                        : existingPhysical == physical,
                    "One physical-source commitment cannot appear under conflicting exact source facts.");
                break;
            case CampaignPlanningGeneratedSourceAuthority generated:
                var generatedKey = GeneratedKey(
                    project,
                    context,
                    generated.Kind,
                    generated.ProducerId,
                    generated.OutputId);
                Require(generatedShaByScopedIdentity.TryGetValue(generatedKey, out var generatedSha)
                        && generatedSha == generated.ContentSha256,
                    "Generated source authority must match one project- and context-scoped current output exactly.");
                break;
            default:
                throw Failure("Unknown source authority type.");
        }
    }

    internal bool MatchesObservedSource(
        string compilationContextRef,
        DocumentationSourceIdentity observed,
        CampaignPlanningSourceAuthority supplied)
    {
        if (!projectByContext.TryGetValue(compilationContextRef, out var project)
            || observed.ProjectIdentity != project
            || observed.SourceSha256 != supplied.ContentSha256)
        {
            return false;
        }

        return (observed, supplied) switch
        {
            (
                RepositoryDocumentationSourceIdentity left,
                CampaignPlanningRepositorySourceAuthority right) =>
                left.Path == right.Path
                && repositoryShaByPath.TryGetValue(left.Path, out var sha)
                && sha == right.ContentSha256,
            (
                GeneratedDocumentationSourceIdentity left,
                CampaignPlanningGeneratedSourceAuthority right) =>
                MapSourceKind(left.Kind) == right.Kind
                && left.ProducerId == right.ProducerId
                && left.OutputId == right.OutputId
                && generatedShaByScopedIdentity.TryGetValue(
                    GeneratedKey(
                        project,
                        compilationContextRef,
                        right.Kind,
                        right.ProducerId,
                        right.OutputId),
                    out var sha)
                && sha == right.ContentSha256,
            _ => false,
        };
    }

    internal string PhysicalSourceKey(SymbolRef symbol, CampaignPlanningSourceAuthority source)
    {
        var project = ProjectIdentity(symbol.CompilationContextRef);
        return source switch
        {
            CampaignPlanningRepositorySourceAuthority repository =>
                "repository\u001f" + repository.PhysicalSourceCommitmentSha256,
            CampaignPlanningGeneratedSourceAuthority generated =>
                GeneratedKey(
                    project,
                    symbol.CompilationContextRef,
                    generated.Kind,
                    generated.ProducerId,
                    generated.OutputId)
                + "\u001f" + generated.ContentSha256,
            _ => throw Failure("Unknown source authority type."),
        };
    }

    private void AddObservedSource(string context, DocumentationSourceIdentity source)
    {
        Require(!projectByContext.TryGetValue(context, out var existingProject)
                ? projectByContext.TryAdd(context, source.ProjectIdentity)
                : existingProject == source.ProjectIdentity,
            "One compilation context cannot carry conflicting project identity authority.");
        switch (source)
        {
            case RepositoryDocumentationSourceIdentity repository:
                Require(!repositoryShaByPath.TryGetValue(repository.Path, out var repositorySha)
                        ? repositoryShaByPath.TryAdd(repository.Path, repository.SourceSha256)
                        : repositorySha == repository.SourceSha256,
                    "One canonical repository path cannot carry conflicting current source content.");
                break;
            case GeneratedDocumentationSourceIdentity generated:
                var key = GeneratedKey(
                    source.ProjectIdentity,
                    context,
                    MapSourceKind(source.Kind),
                    generated.ProducerId,
                    generated.OutputId);
                Require(!generatedShaByScopedIdentity.TryGetValue(key, out var generatedSha)
                        ? generatedShaByScopedIdentity.TryAdd(key, generated.SourceSha256)
                        : generatedSha == generated.SourceSha256,
                    "One project- and context-scoped generated output cannot carry conflicting source content.");
                break;
            default:
                throw Failure("Unknown observed source identity type.");
        }
    }

    private string ProjectIdentity(string context)
    {
        Require(projectByContext.TryGetValue(context, out var project),
            "Every accepted compilation context must map to exactly one observed project identity.");
        return project!;
    }

    private static string GeneratedKey(
        string project,
        string context,
        DocumentationPatchSourceKind kind,
        string producer,
        string output)
    {
        using var writer = new CampaignPlanningCommitmentWriter(
            "contract-scribe/campaign/generated-source-session/v1");
        writer.Add("project", project);
        writer.Add("context", context);
        writer.Add("kind", kind.ToString());
        writer.Add("producer", producer);
        writer.Add("output", output);
        return "generated." + writer.Complete();
    }

    private static DocumentationPatchSourceKind MapSourceKind(DocumentationSourceKind kind) =>
        kind switch
        {
            DocumentationSourceKind.Repository => DocumentationPatchSourceKind.Repository,
            DocumentationSourceKind.SourceGenerator => DocumentationPatchSourceKind.SourceGenerator,
            DocumentationSourceKind.ToolGenerated => DocumentationPatchSourceKind.ToolGenerated,
            _ => throw Failure("Observed source kind is outside the closed vocabulary."),
        };

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw Failure(message);
        }
    }

    private static CampaignPlanningValidationException Failure(string message) =>
        new(CampaignPlanningValidationCode.InvalidOwnerAuthority, message);

    private readonly record struct RepositoryBoundFact(
        string PhysicalSourceCommitmentSha256,
        string ContentSha256,
        DocumentationPatchRepositoryEncoding Encoding,
        bool Writable);

    private readonly record struct RepositoryPhysicalFact(
        string ContentSha256,
        DocumentationPatchRepositoryEncoding Encoding,
        bool Writable);
}
