using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using ContractScribe.Agent.Providers;
using ContractScribe.Core;

namespace ContractScribe.Cli;

internal sealed class CampaignConfigurationException : FormatException
{
    internal CampaignConfigurationException() : base("campaign.configuration.invalid") { }
}

internal sealed record CampaignConfigurationDocument(
    CampaignPlanningConfiguration Planning,
    CampaignBudgetConfiguration Budgets,
    CampaignRetryConfiguration Retry,
    CampaignScribeRequestConfiguration ScribeRequest,
    CampaignProviderConfiguration Provider,
    CampaignCostPolicyConfiguration? CostPolicy,
    JsonElement ExactProjection)
{
    internal CampaignPlanningExecutionPolicy CreateExecutionPolicy()
    {
        var proposal = Authority(
            CampaignPlanningContentFamily.ProposalContract,
            Planning.ProposalContractId,
            JsonSerializer.SerializeToElement(new { scribeRequestVersion = 1, scribeRunResultVersion = 1 }));
        var agent = Authority(
            CampaignPlanningContentFamily.AgentProtocol,
            ScribeRequest.AgentProtocolId,
            JsonSerializer.SerializeToElement(new { scribeProtocolId = Provider.ScribeProtocolId }));
        var context = Authority(
            CampaignPlanningContentFamily.ContextSelectionPolicy,
            Planning.ContextSelectionPolicyId,
            JsonSerializer.SerializeToElement(new
            {
                contextSelectionPolicyVersion = 1,
                selectionOrder = "current-m1-stable-order",
            }));
        var tools = Authority(
            CampaignPlanningContentFamily.ToolPolicyAndRegistry,
            ScribeRequest.ToolPolicyAndRegistryId,
            JsonSerializer.SerializeToElement(new { toolPolicyId = ScribeRequest.ToolPolicyId }));
        var provider = Authority(
            CampaignPlanningContentFamily.ProviderModelRequestProfile,
            Provider.ProviderConfigurationId,
            Provider.ExactProjection);
        var retry = Authority(
            CampaignPlanningContentFamily.RetryPolicy,
            Retry.RetryPolicyId,
            JsonSerializer.SerializeToElement(new { retryPolicyVersion = 1 }));
        var m2 = Authority(
            CampaignPlanningContentFamily.M2ProjectionPolicy,
            Planning.M2ProjectionPolicyId,
            JsonSerializer.SerializeToElement(new
            {
                m2ProjectionVersion = 1,
                maximumPatchElapsedMilliseconds = Planning.MaximumPatchElapsedMilliseconds,
            }));
        var product = CampaignPlanningContentAuthority.CreateValidatedCommitment(
            CampaignPlanningContentFamily.ProductContractRevision,
            Planning.ProductContractRevisionId,
            Planning.ProductContractRevisionSha256);

        CampaignPlanningContentAuthority? costAuthority = null;
        if (CostPolicy is not null)
        {
            costAuthority = Authority(
                CampaignPlanningContentFamily.CostRatePolicy,
                CostPolicy.RatePolicyId,
                CostPolicy.ExactProjection);
        }

        var campaign = Budgets.Campaign;
        var campaignBudget = new CampaignPlanningBudgetPolicy(
            campaign.MaximumBlocks,
            campaign.MaximumChangedFiles,
            campaign.MaximumPatchBytes,
            campaign.MaximumProviderRequests,
            campaign.MaximumAttemptsPerTarget,
            campaign.MaximumInputTokens,
            campaign.MaximumUncachedInputTokens,
            campaign.MaximumOutputTokens,
            campaign.MaximumCostMicrounits,
            campaign.MaximumElapsedMilliseconds,
            campaign.MaximumCandidatesPerBlock,
            CostPolicy is not null,
            CostPolicy?.CurrencyId,
            costAuthority);

        return new CampaignPlanningExecutionPolicy(
            Budgets.Scribe,
            campaignBudget,
            proposal,
            agent,
            context,
            tools,
            provider,
            retry,
            m2,
            product);
    }

    internal CampaignScribeExecutionCapability CreateExecutionCapability(
        CampaignPlanningExecutionPolicy policy) =>
        CampaignStateFactory.CreateScribeExecutionCapability(
            policy,
            JsonSerializer.SerializeToElement(new { scribeProtocolId = Provider.ScribeProtocolId }),
            JsonSerializer.SerializeToElement(new { toolPolicyId = ScribeRequest.ToolPolicyId }),
            Provider.ExactProjection);

    internal CampaignStyleConfigurationAuthority CreateStyleAuthority() =>
        CampaignStateFactory.CreateStyleConfigurationAuthority(
            ScribeRequest.StyleProfileTemplate.StyleProfileId,
            ScribeRequest.StyleProfileTemplate.ExactProjection);

    private static CampaignPlanningContentAuthority Authority(
        CampaignPlanningContentFamily family,
        string id,
        JsonElement projection) =>
        CampaignPlanningContentAuthority.CreateValidatedJsonProjection(family, id, projection);
}

internal sealed record CampaignPlanningConfiguration(
    string CampaignLineage,
    TargetProfile TargetProfile,
    string ProposalContractId,
    string ContextSelectionPolicyId,
    string M2ProjectionPolicyId,
    int MaximumPatchElapsedMilliseconds,
    string ProductContractRevisionId,
    string ProductContractRevisionSha256);

internal sealed record CampaignBudgetConfiguration(
    CampaignAggregateBudgetConfiguration Campaign,
    DocumentationScribeRunLimits Scribe);

internal sealed record CampaignAggregateBudgetConfiguration(
    int MaximumBlocks,
    int MaximumChangedFiles,
    long MaximumPatchBytes,
    int MaximumProviderRequests,
    int MaximumAttemptsPerTarget,
    long MaximumInputTokens,
    long MaximumUncachedInputTokens,
    long MaximumOutputTokens,
    long MaximumCostMicrounits,
    long MaximumElapsedMilliseconds,
    int MaximumCandidatesPerBlock);

internal sealed record CampaignRetryConfiguration(string RetryPolicyId);

internal sealed record CampaignScribeRequestConfiguration(
    string AgentProtocolId,
    string ToolPolicyAndRegistryId,
    string ToolPolicyId,
    CampaignStyleProfileTemplate StyleProfileTemplate);

internal sealed record CampaignTextPolicyTemplate(
    DocumentationScribePolicyDisposition Disposition,
    int MaximumScalars);

internal sealed record CampaignStyleProfileTemplate(
    string StyleProfileId,
    string OutputLanguageId,
    CampaignTextPolicyTemplate Summary,
    CampaignTextPolicyTemplate Remarks,
    CampaignTextPolicyTemplate Exceptions,
    CampaignTextPolicyTemplate ComponentPolicy,
    DocumentationScribeInheritDocDisposition InheritDocDisposition,
    ImmutableArray<string> AllowedLiterals,
    ImmutableArray<string> ForbiddenLiterals,
    ImmutableArray<DocumentationScribeClaimPolicy> ClaimPolicies,
    int MaximumContentUnits,
    int MaximumEvidenceRefsPerUnit,
    JsonElement ExactProjection)
{
    internal DocumentationScribeStyleProfile Expand(
        ImmutableArray<DocumentationPatchApplicableComponent> components) =>
        new(
            StyleProfileId,
            OutputLanguageId,
            new DocumentationScribeTextPolicy(Summary.Disposition, Summary.MaximumScalars),
            new DocumentationScribeTextPolicy(Remarks.Disposition, Remarks.MaximumScalars),
            new DocumentationScribeTextPolicy(Exceptions.Disposition, Exceptions.MaximumScalars),
            components.Select(component => new DocumentationScribeComponentPolicy(
                component.Identity,
                ComponentPolicy.Disposition,
                ComponentPolicy.MaximumScalars)).ToImmutableArray(),
            InheritDocDisposition,
            AllowedLiterals,
            ForbiddenLiterals,
            ClaimPolicies,
            MaximumContentUnits,
            MaximumEvidenceRefsPerUnit);
}

internal sealed record CampaignProviderConfiguration(
    string ProviderConfigurationId,
    string ModelConfigurationId,
    string ScribeProtocolId,
    Uri Endpoint,
    string Model,
    OpenAiCompatibleChatCompletionsRequestProfile RequestProfile,
    JsonElement ExactProjection);

internal sealed record CampaignCostPolicyConfiguration(
    string CurrencyId,
    string RatePolicyId,
    long CachedInputMicrounitsPerMillion,
    long UncachedInputMicrounitsPerMillion,
    long OutputMicrounitsPerMillion,
    long ReasoningMicrounitsPerMillion,
    JsonElement ExactProjection);

internal static class CampaignConfiguration
{
    private const int MaximumBytes = 262_144;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static CampaignConfigurationDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            if (bytes.Length is 0 or > MaximumBytes
                || bytes.Length >= 3 && bytes.Span[..3].SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }))
            {
                throw Invalid();
            }
            _ = StrictUtf8.GetString(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            var root = document.RootElement;
            Expect(root,
            [
                "campaignConfigurationVersion",
                "planning",
                "budgets",
                "retry",
                "scribeRequest",
                "provider",
                "costPolicy",
            ]);
            if (Int(root, "campaignConfigurationVersion", 1, 1) != 1)
            {
                throw Invalid();
            }

            var planning = ParsePlanning(root.GetProperty("planning"));
            var budgets = ParseBudgets(root.GetProperty("budgets"));
            if (planning.MaximumPatchElapsedMilliseconds > budgets.Campaign.MaximumElapsedMilliseconds)
            {
                throw Invalid();
            }

            return new CampaignConfigurationDocument(
                planning,
                budgets,
                ParseRetry(root.GetProperty("retry")),
                ParseScribeRequest(root.GetProperty("scribeRequest")),
                ParseProvider(root.GetProperty("provider")),
                ParseCostPolicy(root.GetProperty("costPolicy")),
                root.Clone());
        }
        catch (CampaignConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw Invalid();
        }
    }

    private static CampaignPlanningConfiguration ParsePlanning(JsonElement value)
    {
        Expect(value,
        [
            "campaignLineage",
            "targetProfile",
            "proposalContractId",
            "contextSelectionPolicyId",
            "m2ProjectionPolicyId",
            "m2ProjectionVersion",
            "maximumPatchElapsedMilliseconds",
            "productContractRevisionId",
            "productContractRevisionSha256",
        ]);
        if (Int(value, "m2ProjectionVersion", 1, 1) != 1)
        {
            throw Invalid();
        }
        var target = String(value, "targetProfile", 64) switch
        {
            "profile.external-api" => TargetProfile.ExternalApi,
            "profile.assembly-visible" => TargetProfile.AssemblyVisible,
            _ => throw Invalid(),
        };
        return new CampaignPlanningConfiguration(
            Id(value, "campaignLineage", 512),
            target,
            Id(value, "proposalContractId", 512),
            Id(value, "contextSelectionPolicyId", 512),
            Id(value, "m2ProjectionPolicyId", 512),
            Int(value, "maximumPatchElapsedMilliseconds", 1, int.MaxValue),
            Id(value, "productContractRevisionId", 512),
            Sha(value, "productContractRevisionSha256"));
    }

    private static CampaignBudgetConfiguration ParseBudgets(JsonElement value)
    {
        Expect(value, ["campaign", "scribe"]);
        var campaign = value.GetProperty("campaign");
        Expect(campaign,
        [
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
            "maximumCandidatesPerBlock",
        ]);
        var campaignInput = Long(campaign, "maximumInputTokens", 1, 1_000_000_000_000);
        var campaignUncached = Long(campaign, "maximumUncachedInputTokens", 0, campaignInput);
        var aggregate = new CampaignAggregateBudgetConfiguration(
            Int(campaign, "maximumBlocks", 1, 16_384),
            Int(campaign, "maximumChangedFiles", 1, 16_384),
            Long(campaign, "maximumPatchBytes", 1, CampaignStateContract.MaximumPatchBytes),
            Int(campaign, "maximumProviderRequests", 1, 1_000_000),
            Int(campaign, "maximumAttemptsPerTarget", 1, 1_000),
            campaignInput,
            campaignUncached,
            Long(campaign, "maximumOutputTokens", 1, 1_000_000_000_000),
            Long(campaign, "maximumCostMicrounits", 0, 1_000_000_000_000_000),
            Long(campaign, "maximumElapsedMilliseconds", 1, CampaignStateContract.MaximumCampaignElapsedMilliseconds),
            Int(campaign, "maximumCandidatesPerBlock", 1, 1_000));

        var scribe = value.GetProperty("scribe");
        Expect(scribe,
        [
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
            "maximumElapsedMilliseconds",
        ]);
        var scribeInput = Int(scribe, "maximumInputTokens", 1, DocumentationScribeContract.MaximumConfiguredInputTokens);
        var limits = new DocumentationScribeRunLimits(
            Int(scribe, "maximumContextReferences", 0, 512),
            Int(scribe, "maximumContextUtf8Bytes", 0, 4_194_304),
            Int(scribe, "maximumEvidenceReferences", 0, 512),
            Int(scribe, "maximumEvidenceUtf8Bytes", 0, 4_194_304),
            Int(scribe, "maximumProviderRequests", 1, 128),
            Int(scribe, "maximumToolRounds", 0, 128),
            Int(scribe, "maximumToolCalls", 0, 1_024),
            Int(scribe, "maximumAttempts", 1, DocumentationScribeContract.MaximumAttempts),
            scribeInput,
            Int(scribe, "maximumUncachedInputTokens", 0, scribeInput),
            Int(scribe, "maximumOutputTokens", 1, DocumentationScribeContract.MaximumConfiguredOutputTokens),
            Long(scribe, "maximumCostMicrounits", 0, DocumentationScribeContract.MaximumConfiguredCostMicrounits),
            Int(scribe, "maximumElapsedMilliseconds", 1, DocumentationScribeContract.MaximumConfiguredElapsedMilliseconds));
        return new CampaignBudgetConfiguration(aggregate, limits);
    }

    private static CampaignRetryConfiguration ParseRetry(JsonElement value)
    {
        Expect(value, ["retryPolicyId", "retryPolicyVersion"]);
        if (Int(value, "retryPolicyVersion", 1, 1) != 1)
        {
            throw Invalid();
        }
        return new CampaignRetryConfiguration(Id(value, "retryPolicyId", 512));
    }

    private static CampaignScribeRequestConfiguration ParseScribeRequest(JsonElement value)
    {
        Expect(value,
        [
            "scribeRequestVersion",
            "agentProtocolId",
            "toolPolicyAndRegistryId",
            "toolPolicyId",
            "styleProfileTemplate",
        ]);
        if (Int(value, "scribeRequestVersion", 1, 1) != 1)
        {
            throw Invalid();
        }
        return new CampaignScribeRequestConfiguration(
            Id(value, "agentProtocolId", 512),
            Id(value, "toolPolicyAndRegistryId", 512),
            Id(value, "toolPolicyId", 128),
            ParseStyle(value.GetProperty("styleProfileTemplate")));
    }

    private static CampaignStyleProfileTemplate ParseStyle(JsonElement value)
    {
        Expect(value,
        [
            "styleProfileId",
            "outputLanguageId",
            "summary",
            "remarks",
            "exceptions",
            "componentPolicy",
            "inheritDocDisposition",
            "allowedLiterals",
            "forbiddenLiterals",
            "claimPolicies",
            "maximumContentUnits",
            "maximumEvidenceRefsPerUnit",
        ]);
        var summary = TextPolicy(value.GetProperty("summary"));
        var remarks = TextPolicy(value.GetProperty("remarks"));
        var exceptions = TextPolicy(value.GetProperty("exceptions"));
        var component = TextPolicy(value.GetProperty("componentPolicy"));
        var inherit = String(value, "inheritDocDisposition", 16) switch
        {
            "allowed" => DocumentationScribeInheritDocDisposition.Allowed,
            "required" => DocumentationScribeInheritDocDisposition.Required,
            "forbidden" => DocumentationScribeInheritDocDisposition.Forbidden,
            _ => throw Invalid(),
        };
        if (inherit == DocumentationScribeInheritDocDisposition.Required
            && new[] { summary, remarks, exceptions, component }.Any(policy =>
                policy.Disposition != DocumentationScribePolicyDisposition.Forbidden
                || policy.MaximumScalars != 0))
        {
            throw Invalid();
        }
        var allowed = OrderedStrings(value.GetProperty("allowedLiterals"), 0, 128, 256);
        var forbidden = OrderedStrings(value.GetProperty("forbiddenLiterals"), 0, 128, 256);
        if (allowed.Intersect(forbidden, StringComparer.Ordinal).Any())
        {
            throw Invalid();
        }
        return new CampaignStyleProfileTemplate(
            Id(value, "styleProfileId", 128),
            Id(value, "outputLanguageId", 128),
            summary,
            remarks,
            exceptions,
            component,
            inherit,
            allowed,
            forbidden,
            ClaimPolicies(value.GetProperty("claimPolicies")),
            Int(value, "maximumContentUnits", 1, DocumentationScribeContract.MaximumContentUnits),
            Int(value, "maximumEvidenceRefsPerUnit", 1, DocumentationScribeContract.MaximumReferences),
            value.Clone());
    }

    private static CampaignTextPolicyTemplate TextPolicy(JsonElement value)
    {
        Expect(value, ["disposition", "maximumScalars"]);
        var disposition = String(value, "disposition", 16) switch
        {
            "required" => DocumentationScribePolicyDisposition.Required,
            "optional" => DocumentationScribePolicyDisposition.Optional,
            "forbidden" => DocumentationScribePolicyDisposition.Forbidden,
            _ => throw Invalid(),
        };
        var maximum = Int(value, "maximumScalars", 0, DocumentationScribeContract.MaximumTextScalars);
        if (disposition != DocumentationScribePolicyDisposition.Forbidden && maximum == 0)
        {
            throw Invalid();
        }
        return new CampaignTextPolicyTemplate(disposition, maximum);
    }

    private static ImmutableArray<DocumentationScribeClaimPolicy> ClaimPolicies(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 64)
        {
            throw Invalid();
        }
        var builder = ImmutableArray.CreateBuilder<DocumentationScribeClaimPolicy>();
        string? prior = null;
        foreach (var row in value.EnumerateArray())
        {
            Expect(row, ["claimCategoryId", "completeEvidenceRequired", "allowedAuthorities"]);
            var id = Id(row, "claimCategoryId", 128);
            if (prior is not null && string.CompareOrdinal(prior, id) >= 0)
            {
                throw Invalid();
            }
            prior = id;
            var authoritiesNode = row.GetProperty("allowedAuthorities");
            if (authoritiesNode.ValueKind != JsonValueKind.Array
                || authoritiesNode.GetArrayLength() is < 1 or > 6)
            {
                throw Invalid();
            }
            var authorities = ImmutableArray.CreateBuilder<DocumentationScribeEvidenceAuthority>();
            DocumentationScribeEvidenceAuthority? priorAuthority = null;
            foreach (var node in authoritiesNode.EnumerateArray())
            {
                var authority = StringValue(node, 64) switch
                {
                    "authority.source-implementation" => DocumentationScribeEvidenceAuthority.SourceImplementation,
                    "authority.source-declaration" => DocumentationScribeEvidenceAuthority.SourceDeclaration,
                    "authority.existing-documentation" => DocumentationScribeEvidenceAuthority.ExistingDocumentation,
                    "authority.test" => DocumentationScribeEvidenceAuthority.Test,
                    "authority.repository-documentation" => DocumentationScribeEvidenceAuthority.RepositoryDocumentation,
                    "authority.public-contract" => DocumentationScribeEvidenceAuthority.PublicContract,
                    _ => throw Invalid(),
                };
                if (priorAuthority is not null && priorAuthority.Value >= authority)
                {
                    throw Invalid();
                }
                priorAuthority = authority;
                authorities.Add(authority);
            }
            var complete = row.GetProperty("completeEvidenceRequired");
            if (complete.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                throw Invalid();
            }
            builder.Add(new DocumentationScribeClaimPolicy(id, complete.GetBoolean(), authorities.ToImmutable()));
        }
        return builder.ToImmutable();
    }

    private static CampaignProviderConfiguration ParseProvider(JsonElement value)
    {
        Expect(value,
        [
            "providerConfigurationId",
            "modelConfigurationId",
            "scribeProtocolId",
            "endpoint",
            "model",
            "requestProfile",
        ]);
        var profile = value.GetProperty("requestProfile");
        Expect(profile,
        [
            "thinkingMode",
            "reasoningEffort",
            "toolChoice",
            "continuationPolicy",
            "outputTokenField",
        ]);
        var thinking = String(profile, "thinkingMode", 16) switch
        {
            "enabled" => OpenAiCompatibleThinkingMode.Enabled,
            "disabled" => OpenAiCompatibleThinkingMode.Disabled,
            _ => throw Invalid(),
        };
        OpenAiCompatibleReasoningEffort? reasoning = profile.GetProperty("reasoningEffort").ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String when profile.GetProperty("reasoningEffort").GetString() == "high" =>
                OpenAiCompatibleReasoningEffort.High,
            _ => throw Invalid(),
        };
        var toolChoice = String(profile, "toolChoice", 16) switch
        {
            "omitted" => OpenAiCompatibleToolChoice.Omitted,
            "auto" => OpenAiCompatibleToolChoice.Auto,
            "required" => OpenAiCompatibleToolChoice.Required,
            _ => throw Invalid(),
        };
        var continuation = String(profile, "continuationPolicy", 32) switch
        {
            "optional" => OpenAiCompatibleContinuationPolicy.Optional,
            "required-for-tool-calls" => OpenAiCompatibleContinuationPolicy.RequiredForToolCalls,
            _ => throw Invalid(),
        };
        var output = String(profile, "outputTokenField", 32) switch
        {
            "max_tokens" => OpenAiCompatibleOutputTokenField.MaxTokens,
            "max_completion_tokens" => OpenAiCompatibleOutputTokenField.MaxCompletionTokens,
            _ => throw Invalid(),
        };
        var endpoint = new Uri(String(value, "endpoint", 2_048), UriKind.Absolute);
        var requestProfile = new OpenAiCompatibleChatCompletionsRequestProfile(
            thinking, reasoning, toolChoice, continuation, output);
        _ = new OpenAiCompatibleHttpTransportOptions(
            endpoint,
            String(value, "model", 256),
            requestProfile,
            networkEnabled: true);
        return new CampaignProviderConfiguration(
            Id(value, "providerConfigurationId", 128),
            Id(value, "modelConfigurationId", 128),
            Id(value, "scribeProtocolId", 128),
            endpoint,
            String(value, "model", 256),
            requestProfile,
            value.Clone());
    }

    private static CampaignCostPolicyConfiguration? ParseCostPolicy(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        Expect(value,
        [
            "currencyId",
            "ratePolicyId",
            "cachedInputMicrounitsPerMillion",
            "uncachedInputMicrounitsPerMillion",
            "outputMicrounitsPerMillion",
            "reasoningMicrounitsPerMillion",
        ]);
        return new CampaignCostPolicyConfiguration(
            Id(value, "currencyId", 128),
            Id(value, "ratePolicyId", 512),
            Long(value, "cachedInputMicrounitsPerMillion", 0, CampaignStateContract.MaximumObservation),
            Long(value, "uncachedInputMicrounitsPerMillion", 0, CampaignStateContract.MaximumObservation),
            Long(value, "outputMicrounitsPerMillion", 0, CampaignStateContract.MaximumObservation),
            Long(value, "reasoningMicrounitsPerMillion", 0, CampaignStateContract.MaximumObservation),
            value.Clone());
    }

    private static void Expect(JsonElement value, IReadOnlyList<string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid();
        }
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Count
            || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length
            || !actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw Invalid();
        }
    }

    private static int Int(JsonElement parent, string name, int minimum, int maximum)
    {
        var value = parent.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            && number >= minimum
            && number <= maximum
            ? number
            : throw Invalid();
    }

    private static long Long(JsonElement parent, string name, long minimum, long maximum)
    {
        var value = parent.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
            && number >= minimum
            && number <= maximum
            ? number
            : throw Invalid();
    }

    private static string Id(JsonElement parent, string name, int maximum) =>
        ValidateString(String(parent, name, maximum), maximum, allowSlash: false);

    private static string Sha(JsonElement parent, string name)
    {
        var value = String(parent, name, 64);
        return value.Length == 64
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw Invalid();
    }

    private static string String(JsonElement parent, string name, int maximum) =>
        StringValue(parent.GetProperty(name), maximum);

    private static string StringValue(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid();
        }
        return ValidateString(value.GetString()!, maximum, allowSlash: true);
    }

    private static string ValidateString(string value, int maximum, bool allowSlash)
    {
        if (value.Length is 0 || value.EnumerateRunes().Count() > maximum || value.Any(char.IsControl))
        {
            throw Invalid();
        }
        if (!allowSlash && value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
        {
            throw Invalid();
        }
        return value;
    }

    private static ImmutableArray<string> OrderedStrings(
        JsonElement value,
        int minimum,
        int maximum,
        int maximumScalars)
    {
        if (value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() < minimum
            || value.GetArrayLength() > maximum)
        {
            throw Invalid();
        }
        var builder = ImmutableArray.CreateBuilder<string>();
        string? prior = null;
        foreach (var item in value.EnumerateArray())
        {
            var text = StringValue(item, maximumScalars);
            if (prior is not null && string.CompareOrdinal(prior, text) >= 0)
            {
                throw Invalid();
            }
            prior = text;
            builder.Add(text);
        }
        return builder.ToImmutable();
    }

    private static CampaignConfigurationException Invalid() => new();
}
