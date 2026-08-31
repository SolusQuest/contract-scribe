using ContractScribe.Core.Hosting;

namespace ContractScribe.Roslyn;

/// <summary>
/// Preserves the audit-specific entry point while delegating the complete M1
/// lifecycle to the production repository-session host shared with campaign.
/// </summary>
internal sealed class ProductionAuditHost
{
    private readonly ProductionRepositorySessionHost sessionHost;

    internal ProductionAuditHost(HostBuildProvenance actualProvenance) =>
        sessionHost = new ProductionRepositorySessionHost(actualProvenance);

    internal Task<ProductionAuditOutcome> RunAsync(
        ProductionAuditRequest request,
        ProductionAuditHostControls controls,
        CancellationToken cancellationToken = default) =>
        sessionHost.RunAsync(request, controls, cancellationToken);
}
