namespace ContractScribe.HostValidation;

public static class NetworkClaimSetRegistry
{
    public const string ClaimSetId = "m1.network-behavior-and-bounded-evidence-claims.v1";

    public static readonly IReadOnlyList<NetworkClaimDefinition> Members =
    [
        new(
            "m1.no-declared-network-operation.no-contractscribe-initiation.v1",
            "The deterministic audit declares no network-dependent operation, and ContractScribe initiates no provider, GitHub, update, telemetry, restore, runtime-download, or other declared network-dependent operation."),
        new(
            "m1.exact-revision-bounded-network-conformance.v1",
            "The recorded evidence is bounded to one exact reviewed production revision and the frozen checks."),
        new(
            "m1.no-egress-sandbox-or-adversarial-completeness.v1",
            "The recorded evidence does not establish egress isolation, sandboxing, capability security, secret isolation from repository-controlled build logic, whole-program non-reachability, or the absence of every possible network path in arbitrary or adversarial .NET code. Repository-controlled MSBuild targets, analyzers, generators, and SDK logic execute as trusted input with caller privileges and are outside this ContractScribe behavior claim.")
    ];

    public static void Validate(PublicSafetyPolicy policy)
    {
        EnsureClaimSetId(policy.NetworkClaimSetId);
        if (!policy.NetworkClaimSetMembers.SequenceEqual(Members))
        {
            throw new ProtocolException("HV131_PUBLIC_SAFETY_POLICY");
        }
    }

    public static void EnsureClaimSetId(string claimSetId)
    {
        if (!string.Equals(claimSetId, ClaimSetId, StringComparison.Ordinal))
        {
            throw new ProtocolException("HV131_PUBLIC_SAFETY_POLICY");
        }
    }

    public static string RenderProtocolBlock() => string.Join(
        '\n',
        [
            "### Frozen public network claim set",
            string.Empty,
            $"Claim set: `{ClaimSetId}`",
            string.Empty,
            .. Members.Select((member, index) =>
                $"{index + 1}. `{member.ClaimId}`: {member.Text}")
        ]);
}
