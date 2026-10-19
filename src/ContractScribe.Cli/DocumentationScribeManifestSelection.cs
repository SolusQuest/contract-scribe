using System.Collections.Immutable;
using System.Text.Json;
using ContractScribe.Core;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

// Host-owned selection, established independently of the request it admits.
internal sealed class DocumentationScribeManifestSelection
{
    private DocumentationScribeManifestSelection(
        ImmutableArray<Member> members,
        ImmutableArray<SymbolRef> executableSymbols)
    {
        Members = members;
        ExecutableSymbols = executableSymbols;
    }

    internal sealed record Member(
        DocumentationScribeSelectedAudit Audit,
        CampaignPlanningTargetAuthority Declaration);

    internal ImmutableArray<Member> Members { get; }

    internal ImmutableArray<SymbolRef> ExecutableSymbols { get; }

    internal DocumentationScribeSelectedAudit PrimaryAudit => ExecutableSymbols.IsEmpty
        ? Members[0].Audit
        : Members.Single(member => member.Audit.Target.SymbolRef == ExecutableSymbols[0]).Audit;

    internal static DocumentationScribeManifestSelection CreateSingleton(
        DocumentationScribeSelectedAudit audit,
        DocumentationScribeStyleProfile? acceptedStyleProfile) =>
        Create([(audit, acceptedStyleProfile)], acceptedStyleProfile is null ? [] : [audit.Target.SymbolRef]);

    // Campaign callers already validated this work against the current accepted plan.
    // Retain that source commitment; fresh source checks belong to preflight.
    internal static DocumentationScribeManifestSelection FromAcceptedWork(
        DocumentationScribeSelectedAudit audit, CampaignPlanningWorkItem acceptedWork)
    {
        if (!audit.IsCurrent || acceptedWork.Disposition.Kind != CampaignPlanningDispositionKind.Executable
            || acceptedWork.Targets is not [{ M3Eligible: true, StyleProfile: not null } target]
            || target.SymbolRef != audit.Target.SymbolRef || target.AuditOutcome != audit.Outcome
            || target.Source is not CampaignPlanningRepositorySourceAuthority)
            throw new ArgumentException("scribe.manifest.selection-invalid", nameof(acceptedWork));
        var declaration = new CampaignPlanningTargetAuthority(audit.Target, target.Source, target.ApplicableComponents,
            target.OwnerSymbolRefs, false, false, false, target.StyleProfile)
        {
            GroupingAuthority = target.GroupingAuthority,
        };
        return new([new Member(audit, declaration)], [target.SymbolRef]);
    }

    internal static DocumentationScribeManifestSelection FromAcceptedBatch(DocumentationScribeAuditAuthority audit,
        CampaignPlanningInput planning, ImmutableArray<CampaignPlanningTargetFact> targets, ImmutableArray<SymbolRef> executable)
    {
        var members = targets.Select(target =>
        {
            var selected = audit.Select(planning.Classifications.Targets.Single(item => item.SymbolRef == target.SymbolRef));
            if (!selected.IsCurrent || selected.Outcome != target.AuditOutcome
                || target.Source is not CampaignPlanningRepositorySourceAuthority)
                throw new ArgumentException("scribe.manifest.selection-invalid");
            var declaration = new CampaignPlanningTargetAuthority(selected.Target, target.Source, target.ApplicableComponents,
                target.OwnerSymbolRefs, false, false, false, target.StyleProfile)
            { GroupingAuthority = target.GroupingAuthority };
            return new Member(selected, declaration);
        }).ToImmutableArray();
        return new(members, executable);
    }

    internal static DocumentationScribeManifestSelection Create(
        IEnumerable<(DocumentationScribeSelectedAudit Audit, DocumentationScribeStyleProfile? StyleProfile)> selected,
        ImmutableArray<SymbolRef> executableSymbols,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var inputs = selected.ToImmutableArray();
        if (inputs.IsEmpty || inputs.Length > DocumentationScribeContract.MaximumManifestTargets
            || executableSymbols.IsDefault || executableSymbols.Distinct().Count() != executableSymbols.Length)
            throw new ArgumentException("scribe.manifest.selection-invalid", nameof(selected));

        var members = ImmutableArray.CreateBuilder<Member>();
        var symbols = new HashSet<SymbolRef>();
        var projector = new DocumentationDeclarationAuthorityProjector();
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input.Audit is not { IsCurrent: true } audit
                || !ReferenceEquals(audit.Session, inputs[0].Audit.Session)
                || !ReferenceEquals(audit.Observations, inputs[0].Audit.Observations)
                || !symbols.Add(audit.Target.SymbolRef)
                || audit.Target.PrimaryKind != PrimarySymbolKind.Method && input.StyleProfile is not null)
                throw new ArgumentException("scribe.manifest.selection-invalid", nameof(selected));

            var projection = projector.Project(audit.Observations, audit.Target, input.StyleProfile, cancellationToken);
            if (projection.Authority is not { Source: CampaignPlanningRepositorySourceAuthority } declaration)
                throw new ArgumentException("scribe.manifest.source-unavailable", nameof(selected));
            members.Add(new Member(audit, declaration));
        }

        foreach (var symbol in executableSymbols)
        {
            var member = members.SingleOrDefault(item => item.Audit.Target.SymbolRef == symbol);
            if (member is null || member.Declaration.ExecutableStyleProfile is null
                || member.Audit.Target.PrimaryKind != PrimarySymbolKind.Method
                || member.Audit.Target.SupportStatus != SupportStatus.Supported
                || !member.Declaration.Source.Writable || member.Declaration.MultiDeclarator
                || member.Declaration.PrimaryConstructor || member.Declaration.PrimaryConstructorAlias)
                throw new ArgumentException("scribe.manifest.execution-invalid", nameof(executableSymbols));
        }

        return new DocumentationScribeManifestSelection(members.ToImmutable(), executableSymbols);
    }

    internal bool Matches(DocumentationScribeRequest request)
    {
        var session = Members[0].Audit.Session;
        if (request.Manifest.Length != Members.Length
            || request.Context.RepositoryContextRef != session.RepositorySession.RepositoryContextRef
            || request.Context.InputIdentity != session.RepositorySession.InputIdentity
            || request.Context.TargetProfile != session.Classification.ClassificationSet!.TargetProfile)
            return false;

        for (var index = 0; index < Members.Length; index++)
        {
            var authority = Members[index];
            var member = request.Manifest[index];
            var source = (CampaignPlanningRepositorySourceAuthority)authority.Declaration.Source;
            if (!authority.Audit.IsCurrent
                || member.Target.SymbolRef != authority.Audit.Target.SymbolRef
                || member.AuditOutcome != authority.Audit.Outcome
                || member.Target.SourceLocator is not RepositoryEvidenceLocator locator
                || locator.Path != source.Path || locator.Span != source.RequestedDeclarationSpan
                || member.Target.SourceSha256 != source.ContentSha256
                || !StyleMatches(member.StyleProfile, authority.Declaration.ExecutableStyleProfile)
                || member.Target.ApplicableComponents.Length != authority.Declaration.ApplicableComponents.Length
                || !member.Target.ApplicableComponents.Zip(authority.Declaration.ApplicableComponents).All(pair =>
                    pair.First.Identity == pair.Second.Identity && pair.First.Name == pair.Second.Name
                    && pair.First.Kind == MapComponent(pair.Second.Kind))
                || !GroupingMatches(member, authority.Declaration.GroupingAuthority))
                return false;
        }

        return request.ExecutableTargetIds.Select(id => request.Manifest.Single(member => member.TargetId == id).Target.SymbolRef)
            .SequenceEqual(ExecutableSymbols);
    }

    internal ImmutableArray<DocumentationScribeContextBootstrapSelection> BootstrapSelections(
        string? configuredAgentEntrypoint) => Members.Select(member =>
        {
            var audit = member.Audit;
            var source = (CampaignPlanningRepositorySourceAuthority)member.Declaration.Source;
            return DocumentationScribeContextValidation.CreateBootstrapSelection(
                audit.Session.RepositorySession.RepositoryContextRef,
                audit.Session.RepositorySession.InputIdentity,
                audit.Session.Classification.ClassificationSet!.TargetProfile,
                audit.Target.SymbolRef, source.Path,
                source.RequestedDeclarationSpan.Start, source.RequestedDeclarationSpan.End,
                source.ContentSha256, configuredAgentEntrypoint);
        }).ToImmutableArray();

    private static bool StyleMatches(DocumentationScribeStyleProfile? actual, DocumentationScribeStyleProfile? expected) =>
        JsonSerializer.Serialize(actual) == JsonSerializer.Serialize(expected);

    private static bool GroupingMatches(DocumentationScribeManifestTarget member, CampaignPlanningGroupingAuthority? expected) =>
        expected is null ? member.Grouping is null : member.Grouping is { } actual
            && actual.ProjectIdentity == expected.ProjectIdentity
            && actual.ContainingTypeSymbolRef.CompilationContextRef == member.Target.SymbolRef.CompilationContextRef
            && actual.ContainingTypeSymbolRef.DocumentationCommentId == expected.ContainingTypeDocumentationCommentId
            && actual.MemberFamilySha256 == expected.MemberFamilySha256;

    private static DocumentationPatchComponentKind MapComponent(ComponentKind kind) => kind switch
    {
        ComponentKind.TypeParameter => DocumentationPatchComponentKind.TypeParameter,
        ComponentKind.Parameter => DocumentationPatchComponentKind.Parameter,
        ComponentKind.Return => DocumentationPatchComponentKind.Return,
        ComponentKind.Value => DocumentationPatchComponentKind.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public override string ToString() =>
        $"{nameof(DocumentationScribeManifestSelection)} {{ Members = {Members.Length}, Executable = {ExecutableSymbols.Length} }}";
}
