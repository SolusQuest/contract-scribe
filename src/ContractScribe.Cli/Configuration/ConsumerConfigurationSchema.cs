using System.Collections.Immutable;

namespace ContractScribe.Cli;

internal enum ConsumerValueKind
{
    Integer,
    Text,
    Boolean,
    Array,
}

// The declared consumer-layer shape. Leaf nodes replace wholesale; object nodes
// merge recursively by declared field. Nullable marks the only fields where a
// JSON null is a legal layer value. This tree drives only the strict layer
// validation and ordered merge in this directory; it is not a configuration
// framework and must not gain provider, environment, or include behaviors.
internal sealed record ConsumerConfigurationNode(
    string Name,
    bool Nullable,
    ConsumerValueKind? Leaf,
    ConsumerConfigurationNode? Item,
    ImmutableArray<ConsumerConfigurationNode> Children)
{
    private static ConsumerConfigurationNode Obj(
        string name,
        bool nullable,
        params ConsumerConfigurationNode[] children) =>
        new(name, nullable, null, null, children.ToImmutableArray());

    private static ConsumerConfigurationNode Field(
        string name,
        ConsumerValueKind kind,
        bool nullable = false,
        ConsumerConfigurationNode? item = null) =>
        new(name, nullable, kind, item, ImmutableArray<ConsumerConfigurationNode>.Empty);

    private static ConsumerConfigurationNode Integers(string name, params string[] fields) =>
        Obj(name, false, fields.Select(field => Field(field, ConsumerValueKind.Integer)).ToArray());

    internal static readonly ConsumerConfigurationNode Root = Obj(
        string.Empty,
        nullable: false,
        Field("consumerConfigurationVersion", ConsumerValueKind.Integer),
        Obj("planning", false,
            Field("targetProfile", ConsumerValueKind.Text),
            Field("maximumPatchElapsedMilliseconds", ConsumerValueKind.Integer)),
        Obj("budgets", false,
            Integers("campaign",
                "maximumBlocks",
                "maximumChangedFiles",
                "maximumPatchBytes",
                "maximumProviderRequests",
                "maximumAttemptsPerTarget",
                "maximumInputTokens",
                "maximumUncachedInputTokens",
                "maximumOutputTokens",
                "maximumCostMicrounits",
                "maximumElapsedMilliseconds",
                "maximumCandidatesPerBlock"),
            Integers("scribe",
                "maximumContextReferences",
                "maximumContextUtf8Bytes",
                "maximumEvidenceReferences",
                "maximumEvidenceUtf8Bytes",
                "maximumProviderRequests",
                "maximumToolRounds",
                "maximumToolCalls",
                "maximumAttempts",
                "maximumInputTokens",
                "maximumUncachedInputTokens",
                "maximumOutputTokens",
                "maximumCostMicrounits",
                "maximumElapsedMilliseconds")),
        Obj("scribeRequest", false,
            Obj("styleProfileTemplate", false,
                Field("styleProfileId", ConsumerValueKind.Text),
                Field("outputLanguageId", ConsumerValueKind.Text),
                Obj("summary", false,
                    Field("disposition", ConsumerValueKind.Text),
                    Field("maximumScalars", ConsumerValueKind.Integer)),
                Obj("remarks", false,
                    Field("disposition", ConsumerValueKind.Text),
                    Field("maximumScalars", ConsumerValueKind.Integer)),
                Obj("exceptions", false,
                    Field("disposition", ConsumerValueKind.Text),
                    Field("maximumScalars", ConsumerValueKind.Integer)),
                Obj("componentPolicy", false,
                    Field("disposition", ConsumerValueKind.Text),
                    Field("maximumScalars", ConsumerValueKind.Integer)),
                Field("inheritDocDisposition", ConsumerValueKind.Text),
                Field("allowedLiterals", ConsumerValueKind.Array,
                    item: Field("item", ConsumerValueKind.Text)),
                Field("forbiddenLiterals", ConsumerValueKind.Array,
                    item: Field("item", ConsumerValueKind.Text)),
                Field("claimPolicies", ConsumerValueKind.Array, item: Obj("item", false,
                    Field("claimCategoryId", ConsumerValueKind.Text),
                    Field("completeEvidenceRequired", ConsumerValueKind.Boolean),
                    Field("allowedAuthorities", ConsumerValueKind.Array,
                        item: Field("item", ConsumerValueKind.Text)))),
                Field("maximumContentUnits", ConsumerValueKind.Integer),
                Field("maximumEvidenceRefsPerUnit", ConsumerValueKind.Integer))),
        Obj("provider", false,
            Field("endpoint", ConsumerValueKind.Text),
            Field("model", ConsumerValueKind.Text),
            Obj("requestProfile", false,
                Field("thinkingMode", ConsumerValueKind.Text),
                Field("reasoningEffort", ConsumerValueKind.Text, nullable: true),
                Field("toolChoice", ConsumerValueKind.Text),
                Field("continuationPolicy", ConsumerValueKind.Text),
                Field("outputTokenField", ConsumerValueKind.Text))),
        Obj("costPolicy", nullable: true,
            Field("currencyId", ConsumerValueKind.Text),
            Field("ratePolicyId", ConsumerValueKind.Text),
            Field("cachedInputMicrounitsPerMillion", ConsumerValueKind.Integer),
            Field("uncachedInputMicrounitsPerMillion", ConsumerValueKind.Integer),
            Field("outputMicrounitsPerMillion", ConsumerValueKind.Integer),
            Field("reasoningMicrounitsPerMillion", ConsumerValueKind.Integer)));
}
