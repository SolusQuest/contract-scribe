using System.Text.Json;
using ContractScribe.Core;

namespace ContractScribe.Cli;

internal sealed record CampaignM2ExecutionPolicy(long MaximumPatchElapsedMilliseconds)
{
    internal const int ProjectionVersion = 1;

    internal static bool TryCreate(
        JsonElement projection,
        CampaignPlanningExecutionPolicy acceptedExecutionPolicy,
        out CampaignM2ExecutionPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(acceptedExecutionPolicy);
        policy = null;
        if (projection.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        int? version = null;
        long? maximum = null;
        foreach (var property in projection.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                return false;
            }

            switch (property.Name)
            {
                case "m2ProjectionVersion" when property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out var parsedVersion):
                    version = parsedVersion;
                    break;
                case "maximumPatchElapsedMilliseconds" when property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt64(out var parsedMaximum):
                    maximum = parsedMaximum;
                    break;
                default:
                    return false;
            }
        }

        if (names.Count != 2
            || version != ProjectionVersion
            || maximum is not > 0
            || maximum > acceptedExecutionPolicy.CampaignBudget.MaximumElapsedMilliseconds
            || maximum > CampaignStateContract.MaximumObservation)
        {
            return false;
        }

        try
        {
            var accepted = acceptedExecutionPolicy.M2ProjectionPolicy;
            var current = CampaignPlanningContentAuthority.CreateValidatedJsonProjection(
                CampaignPlanningContentFamily.M2ProjectionPolicy,
                accepted.Id,
                projection);
            if (current.Family != accepted.Family
                || !string.Equals(current.Id, accepted.Id, StringComparison.Ordinal)
                || !string.Equals(current.ContentSha256, accepted.ContentSha256, StringComparison.Ordinal))
            {
                return false;
            }

            policy = new CampaignM2ExecutionPolicy(maximum.Value);
            return true;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }
}
