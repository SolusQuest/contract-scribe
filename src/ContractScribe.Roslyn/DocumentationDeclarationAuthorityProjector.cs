using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;

namespace ContractScribe.Roslyn;

public sealed record DocumentationDeclarationAuthorityProjection(
    CampaignPlanningTargetAuthority? Authority,
    string? FailureCode)
{
    public bool IsSuccess => Authority is not null && FailureCode is null;
}

/// <summary>
/// Projects one selected live M1 target through the same Roslyn declaration
/// resolution primitive used by M2. It does not construct an M2 patch request.
/// </summary>
public sealed class DocumentationDeclarationAuthorityProjector
{
    private readonly DocumentationPatchDeclarationResolver resolver = new();

    public DocumentationDeclarationAuthorityProjection Project(
        ObservedRepositorySession observed,
        TargetClassification target,
        DocumentationScribeStyleProfile? executableStyleProfile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        if (observed.ObservationSet is not { } observations)
        {
            return Failure("patch.stale.repository-context");
        }

        var parent = observations.Observations.Where(item =>
                item.Subject.ParentSymbolRef == target.SymbolRef
                && item.Subject.ComponentKind is null)
            .ToImmutableArray();
        if (parent.Length != 1)
        {
            return Failure("patch.rejected.ambiguous-target");
        }

        var declarations = parent[0].Declarations.Where(item =>
                item.Source is RepositoryDocumentationSourceIdentity)
            .ToImmutableArray();
        if (declarations.Length != 1)
        {
            return Failure("patch.rejected.ambiguous-target");
        }

        var observation = declarations[0];
        var resolved = resolver.ResolveSelectedTarget(
            observed, target, observation, cancellationToken);
        if (resolved.Declaration is not { } declaration)
        {
            return Failure(resolved.FailureCode ?? "patch.rejected.unsupported-target");
        }

        var source = new CampaignPlanningRepositorySourceAuthority(
            declaration.RepositoryPath,
            Sha256(Encoding.UTF8.GetBytes(declaration.PhysicalSourceIdentity)),
            observation.Source.SourceSha256,
            observation.DeclarationId,
            declaration.SourceSha256,
            declaration.Encoding,
            observation.DeclarationSpan,
            declaration.RequestedDeclarationSpan,
            declaration.CanonicalDeclarationSpan,
            declaration.OwnerSpan,
            declaration.DocumentationSpan,
            declaration.BlockState);
        var components = declaration.ApplicableComponents.Select(component =>
            new CampaignPlanningApplicableComponent(
                component.Kind switch
                {
                    DocumentationPatchComponentKind.TypeParameter => ComponentKind.TypeParameter,
                    DocumentationPatchComponentKind.Parameter => ComponentKind.Parameter,
                    DocumentationPatchComponentKind.Return => ComponentKind.Return,
                    DocumentationPatchComponentKind.Value => ComponentKind.Value,
                    _ => throw new InvalidOperationException("Unknown documentation component kind."),
                },
                component.Identity,
                component.Name)).ToImmutableArray();
        return new DocumentationDeclarationAuthorityProjection(
            new CampaignPlanningTargetAuthority(
                target,
                source,
                components,
                declaration.OwnerSymbolRefs,
                declaration.IsMultiDeclarator,
                declaration.IsPrimaryConstructor,
                declaration.HasPrimaryConstructorAlias,
                executableStyleProfile),
            null);
    }

    private static DocumentationDeclarationAuthorityProjection Failure(string code) =>
        new(null, code);

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
