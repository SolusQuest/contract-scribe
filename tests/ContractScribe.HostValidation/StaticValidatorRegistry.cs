namespace ContractScribe.HostValidation;

public sealed record StaticValidationResult(
    string ObservationCode,
    string EnforcementClass,
    IReadOnlyList<string> DiagnosticCodes);

public static class StaticValidatorRegistry
{
    private static readonly HashSet<string> ImplementedValidators = new(
        [
            "bundle.identity",
            "bundle.inventory",
            "bundle.schema",
            "crosswalk.bidirectional",
            "evidence.artifact-set",
            "evidence.canonical",
            "evidence.execution-set",
            "evidence.public-safety",
            "evidence.review-binding",
            "evidence.incomplete-retention",
            "evidence.network-bounded-profile",
            "evidence.passing-gate",
            "execution.stream-boundary",
            "execution.vector-runner",
            "observer.process-tree",
            "observer.repository-delta",
            "observer.temporary-disk-high-water",
            "observer.native-termination-cause",
            "project.boundary",
            "subject.materialization",
            "validation.attempt-identity"
        ],
        StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, Func<string, StaticValidationResult>> VectorValidators =
        new Dictionary<string, Func<string, StaticValidationResult>>(StringComparer.Ordinal)
        {
            ["project.graph-boundary"] = root =>
            {
                BundleValidator.ValidateProjectBoundary(root);
                return Matched("project.graph.valid");
            },
            ["diagnostics.machine-path-scan"] = root =>
            {
                _ = root;
                PublicSafetyScanner.SelfTestMachinePaths();
                return Matched("public.machine-path-rejected");
            },
            ["diagnostics.credential-marker-scan"] = root =>
            {
                _ = root;
                PublicSafetyScanner.SelfTestCredentialMarkers();
                return Matched("public.credential-marker-rejected");
            },
            ["network.no-declared-dependency"] = root =>
            {
                BundleValidator.ValidateProjectBoundary(root);
                return Matched("network.no-declared-dependency");
            },
            ["network.egress-isolation-limitation"] = root =>
            {
                BundleValidator.ValidateProhibitedClaims(root);
                return Matched(
                    "network.egress-isolation-not-claimed",
                    "not-enforceable-selected-topology");
            }
        };

    public static void ValidateRegistry(
        IReadOnlyList<string> requiredValidators,
        IReadOnlyList<VectorDefinition> vectors)
    {
        if (!ImplementedValidators.SetEquals(requiredValidators)
            || requiredValidators.Count != requiredValidators.Distinct(StringComparer.Ordinal).Count())
        {
            throw new ProtocolException("HV195_VALIDATOR_REGISTRY_MISMATCH");
        }

        var staticVectorIds = vectors
            .Where(vector => vector.ExecutorKind == "harness-static")
            .Select(vector => vector.VectorId)
            .ToHashSet(StringComparer.Ordinal);
        if (!staticVectorIds.SetEquals(VectorValidators.Keys))
        {
            throw new ProtocolException("HV196_STATIC_VALIDATOR_OWNERSHIP");
        }
    }

    public static StaticValidationResult Execute(string root, VectorDefinition vector)
    {
        if (vector.ExecutorKind != "harness-static"
            || !VectorValidators.TryGetValue(vector.VectorId, out var validator))
        {
            throw new ProtocolException("HV197_STATIC_VALIDATOR_UNKNOWN");
        }

        var result = validator(root);
        if (result.ObservationCode != vector.ExpectedObservation
            || result.EnforcementClass != vector.ExpectedEnforcementClass)
        {
            throw new ProtocolException("HV198_STATIC_VALIDATOR_ORACLE_MISMATCH");
        }
        return result;
    }

    private static StaticValidationResult Matched(
        string observation,
        string enforcementClass = "internally-enforceable") =>
        new(observation, enforcementClass, []);
}
