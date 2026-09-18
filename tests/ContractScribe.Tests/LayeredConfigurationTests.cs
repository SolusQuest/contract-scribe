using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Providers;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class LayeredConfigurationTests : IDisposable
{
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "contract-scribe-layered-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_DefaultsOnly_InjectsLineageAndProductIdentity()
    {
        var defaults = Defaults();
        var snapshot = Resolve(defaults, lineage: "campaign.test");

        var document = snapshot.Document;
        Assert.Equal("campaign.test", document.Planning.CampaignLineage);
        Assert.Equal(
            ProductSha(Identity()),
            document.Planning.ProductContractRevisionSha256);
        Assert.Equal("product.contract-scribe.campaign-v1", document.Planning.ProductContractRevisionId);
        Assert.Equal("https://api.deepseek.com/chat/completions", document.Provider.Endpoint.AbsoluteUri);
        Assert.Equal("deepseek-v4-flash", document.Provider.Model);
        Assert.Equal(128, document.Budgets.Campaign.MaximumProviderRequests);
        Assert.Single(snapshot.Sources);
        Assert.True(snapshot.Revalidate());
    }

    [Fact]
    public void Resolve_Precedence_OverrideWinsDownstreamWinsDefaults()
    {
        var defaults = Defaults();
        var downstream = Layer(
            "budgets", new JsonObject
            {
                ["campaign"] = new JsonObject { ["maximumProviderRequests"] = 64 },
            });
        var overlay = Layer(
            "budgets", new JsonObject
            {
                ["campaign"] = new JsonObject { ["maximumProviderRequests"] = 32 },
            });
        var both = Resolve(defaults, downstream, overlay);
        Assert.Equal(32, both.Document.Budgets.Campaign.MaximumProviderRequests);
        Assert.Equal(3, both.Sources.Length);

        var downstreamOnly = Resolve(defaults, downstream);
        Assert.Equal(64, downstreamOnly.Document.Budgets.Campaign.MaximumProviderRequests);

        var overrideOnly = Resolve(defaults, overlay: overlay);
        Assert.Equal(32, overrideOnly.Document.Budgets.Campaign.MaximumProviderRequests);
        Assert.Equal(2, overrideOnly.Sources.Length);
    }

    [Fact]
    public void Resolve_MissingFields_InheritAndExplicitFalsyValuesApply()
    {
        var layer = Layer(
            "budgets", new JsonObject
            {
                ["scribe"] = new JsonObject
                {
                    ["maximumToolRounds"] = 0,
                    ["maximumToolCalls"] = 0,
                },
            },
            "scribeRequest", new JsonObject
            {
                ["styleProfileTemplate"] = new JsonObject
                {
                    ["inheritDocDisposition"] = "allowed",
                },
            });
        var document = Resolve(Defaults(), layer).Document;

        Assert.Equal(0, document.Budgets.Scribe.MaximumToolRounds);
        Assert.Equal(0, document.Budgets.Scribe.MaximumToolCalls);
        Assert.Equal(128, document.Budgets.Campaign.MaximumProviderRequests);
        Assert.Equal(
            DocumentationScribeInheritDocDisposition.Allowed,
            document.ScribeRequest.StyleProfileTemplate.InheritDocDisposition);
    }

    [Fact]
    public void Resolve_ExplicitFalseAndEmptyArray_AreRealOverrides()
    {
        var layer = Layer(
            "scribeRequest", new JsonObject
            {
                ["styleProfileTemplate"] = new JsonObject
                {
                    ["allowedLiterals"] = new JsonArray(),
                    ["claimPolicies"] = new JsonArray(
                        new JsonObject
                        {
                            ["claimCategoryId"] = "claim.behavior",
                            ["completeEvidenceRequired"] = false,
                            ["allowedAuthorities"] = new JsonArray("authority.source-declaration"),
                        }),
                },
            });
        var resolved = Resolve(Defaults(), layer);
        var style = resolved.Document.ScribeRequest.StyleProfileTemplate;
        Assert.Empty(style.AllowedLiterals);
        var claim = Assert.Single(style.ClaimPolicies);
        Assert.False(claim.CompleteEvidenceRequired);
        Assert.Equal(DocumentationScribeEvidenceAuthority.SourceDeclaration, Assert.Single(claim.AllowedAuthorities));
    }

    [Fact]
    public void Resolve_LayerPropertyOrder_IsOrderIndependent()
    {
        var ordered = Write("ordered.json", """
            {"consumerConfigurationVersion":1,"costPolicy":{
                "currencyId":"currency.test","ratePolicyId":"rate.test",
                "cachedInputMicrounitsPerMillion":1,"uncachedInputMicrounitsPerMillion":2,
                "outputMicrounitsPerMillion":3,"reasoningMicrounitsPerMillion":4}}
            """);
        var reordered = Write("reordered.json", """
            {"consumerConfigurationVersion":1,"costPolicy":{
                "reasoningMicrounitsPerMillion":4,"outputMicrounitsPerMillion":3,
                "uncachedInputMicrounitsPerMillion":2,"cachedInputMicrounitsPerMillion":1,
                "ratePolicyId":"rate.test","currencyId":"currency.test"}}
            """);
        var first = Resolve(Defaults(), ordered);
        var second = Resolve(Defaults(), reordered);
        Assert.Equal(
            JsonSerializer.SerializeToUtf8Bytes(first.Document),
            JsonSerializer.SerializeToUtf8Bytes(second.Document));
        Assert.NotNull(first.Document.CostPolicy);
        Assert.Equal(4, first.Document.CostPolicy!.ReasoningMicrounitsPerMillion);
    }

    [Fact]
    public void Resolve_RejectsDuplicatesInsideArraysAndDefaults()
    {
        var nestedDuplicates = Write("nested-dup.json",
            "{\"consumerConfigurationVersion\":1,\"scribeRequest\":{\"styleProfileTemplate\":"
            + "{\"claimPolicies\":[{\"claimCategoryId\":\"claim.behavior\","
            + "\"claimCategoryId\":\"claim.purpose\",\"completeEvidenceRequired\":true,"
            + "\"allowedAuthorities\":[\"authority.source-declaration\"]}]}}}");
        AssertInvalid(Defaults(), nestedDuplicates);

        var defaultsDuplicates = Write("defaults-dup.json",
            File.ReadAllText(Path.Join(RepositoryRoot(), "config", "defaults.json")).Replace(
                "\"providerConfigurationId\": \"provider.configured.v1\"",
                "\"providerConfigurationId\": \"a.v1\",\"providerConfigurationId\":\"b.v1\""));
        Assert.Throws<CampaignConfigurationException>(() => Resolve(defaultsDuplicates));
    }

    [Fact]
    public void Resolve_CorruptDefaultsCannotBeRepairedByAnUpperLayer()
    {
        var corrupt = Defaults(root =>
            ((JsonObject)((JsonObject)root["budgets"]!)["campaign"]!)["maximumBlocks"] = "corrupt");
        var repairing = Layer("budgets", new JsonObject
        {
            ["campaign"] = new JsonObject { ["maximumBlocks"] = 128 },
        });
        AssertInvalid(corrupt, repairing);
    }

    [Fact]
    public void Resolve_ArraysReplaceRatherThanMerge()
    {
        var layer = Layer(
            "scribeRequest", new JsonObject
            {
                ["styleProfileTemplate"] = new JsonObject
                {
                    ["allowedLiterals"] = new JsonArray("Only", "These"),
                },
            });
        var document = Resolve(Defaults(), layer).Document;

        Assert.Equal(
            ["Only", "These"],
            document.ScribeRequest.StyleProfileTemplate.AllowedLiterals.ToArray());
    }

    [Fact]
    public void Resolve_NullableTransitions_FollowDeclaredUnionSemantics()
    {
        var cost = new JsonObject
        {
            ["currencyId"] = "currency.usd",
            ["ratePolicyId"] = "rate.test.v1",
            ["cachedInputMicrounitsPerMillion"] = 1,
            ["uncachedInputMicrounitsPerMillion"] = 2,
            ["outputMicrounitsPerMillion"] = 3,
            ["reasoningMicrounitsPerMillion"] = 4,
        };

        // null -> object: legal full replacement with no inherited children.
        var toObject = Resolve(Defaults(), Layer("costPolicy", cost.DeepClone()));
        Assert.NotNull(toObject.Document.CostPolicy);
        Assert.Equal("currency.usd", toObject.Document.CostPolicy!.CurrencyId);
        Assert.Equal(4, toObject.Document.CostPolicy.ReasoningMicrounitsPerMillion);

        // object -> null: legal explicit clear.
        var defaultsWithCost = Defaults(mutate: root => root["costPolicy"] = cost.DeepClone());
        var cleared = Resolve(defaultsWithCost, Layer("costPolicy", null));
        Assert.Null(cleared.Document.CostPolicy);

        // object -> partial object: recursive inheritance from the base object.
        var partial = Resolve(
            defaultsWithCost,
            Layer("costPolicy", new JsonObject { ["currencyId"] = "currency.eur" }));
        Assert.Equal("currency.eur", partial.Document.CostPolicy!.CurrencyId);
        Assert.Equal(1, partial.Document.CostPolicy.CachedInputMicrounitsPerMillion);

        // null -> partial object cannot satisfy the required leaves.
        AssertInvalid(Defaults(), Layer(
            "costPolicy", new JsonObject { ["currencyId"] = "currency.eur" }));

        // Nullable scalar: string -> null and null -> string.
        var effort = Resolve(Defaults(), Layer(
            "provider", new JsonObject
            {
                ["requestProfile"] = new JsonObject { ["reasoningEffort"] = null },
            }));
        Assert.Null(effort.Document.Provider.RequestProfile.ReasoningEffort);
        var restored = Resolve(
            Defaults(mutate: root =>
                ((JsonObject)root["provider"]!["requestProfile"]!)["reasoningEffort"] = null),
            Layer("provider", new JsonObject
            {
                ["requestProfile"] = new JsonObject { ["reasoningEffort"] = "high" },
            }));
        Assert.Equal(
            OpenAiCompatibleReasoningEffort.High,
            restored.Document.Provider.RequestProfile.ReasoningEffort);
        AssertInvalid(Defaults(mutate: root =>
            ((JsonObject)root["provider"]!["requestProfile"]!)["reasoningEffort"] = null),
            Layer("provider", new JsonObject
            {
                ["requestProfile"] = new JsonObject { ["reasoningEffort"] = 5 },
            }));
    }

    [Fact]
    public void Resolve_RejectsUndeclaredNullWrongTypeAndTypeChange()
    {
        AssertInvalid(Defaults(), Layer("provider", new JsonObject { ["endpoint"] = null }));
        AssertInvalid(Defaults(), Layer("budgets", null));
        AssertInvalid(Defaults(), Layer("budgets", new JsonObject
        {
            ["campaign"] = new JsonObject { ["maximumBlocks"] = "128" },
        }));
        AssertInvalid(Defaults(), Layer("budgets", new JsonObject
        {
            ["campaign"] = new JsonObject { ["maximumBlocks"] = 1.5 },
        }));
        AssertInvalid(Defaults(), Layer("planning", new JsonObject { ["targetProfile"] = 7 }));
        AssertInvalid(Defaults(), Layer("provider", new JsonObject { ["requestProfile"] = "x" }));
    }

    [Fact]
    public void Resolve_RejectsEveryNonConsumerField()
    {
        foreach (var (path, value) in NonConsumerFields())
        {
            var layer = new JsonObject { ["consumerConfigurationVersion"] = 1 };
            var cursor = layer;
            for (var index = 0; index < path.Length - 1; index++)
            {
                cursor = (JsonObject)(cursor[path[index]] ??= new JsonObject());
            }
            cursor[path[^1]] = value;
            AssertInvalid(Defaults(), Write("layer.json", layer.ToJsonString()));
        }
    }

    [Fact]
    public void Resolve_RejectsUnknownDuplicateAndCaseDifferingFields()
    {
        AssertInvalid(Defaults(), Layer("bogus", 1));
        AssertInvalid(Defaults(), Layer("Budgets", new JsonObject()));
        AssertInvalid(Defaults(), Write("layer.json",
            "{\"consumerConfigurationVersion\":1,\"bogus\":1,\"bogus\":2}"));
        AssertInvalid(Defaults(), Write("layer.json",
            "{\"consumerConfigurationVersion\":1,\"budgets\":{\"campaign\":{\"maximumBlocks\":1,\"maximumBlocks\":2}}}"));
    }

    [Fact]
    public void Resolve_RequiresVersionOneInEachLayer()
    {
        AssertInvalid(Defaults(), Write("layer.json", "{\"planning\":{}}"));
        AssertInvalid(Defaults(), Layer("consumerConfigurationVersion", 2));
        AssertInvalid(Defaults(), Write("layer.json",
            "{\"consumerConfigurationVersion\":1,\"consumerConfigurationVersion\":1}"));
        AssertInvalid(Defaults(), Write("layer.json",
            "{\"consumerConfigurationVersion\":\"1\"}"));
    }

    [Fact]
    public void Resolve_EnforcesSourceHygieneAndBounds()
    {
        AssertInvalid(Defaults(), Write("layer.json", new string(' ', 262_145)));
        AssertInvalidBytes(Defaults(), [0xef, 0xbb, 0xbf, (byte)'{', (byte)'}']);
        AssertInvalidBytes(Defaults(), [0xff, 0xfe]);
        AssertInvalidBytes(Defaults(), Encoding.UTF8.GetBytes("[1,2]"));

        var deep = new StringBuilder("{\"consumerConfigurationVersion\":1,\"budgets\":");
        deep.Append("{\"campaign\":{\"maximumBlocks\":1}");
        for (var index = 0; index < 70; index++)
        {
            deep.Insert(deep.Length - 1, "{\"x\":".AsSpan());
            deep.Append('}');
        }
        deep.Append('}');
        AssertInvalid(Defaults(), Write("layer.json", deep.ToString()));

        var directory = Path.Join(_root, "as-directory");
        Directory.CreateDirectory(directory);
        Assert.Throws<CampaignConfigurationException>(() =>
            Resolve(Defaults(), downstream: directory));
        Assert.Throws<CampaignConfigurationException>(() =>
            Resolve(Defaults(), downstream: Path.Join(_root, "missing.json")));
    }

    [Fact]
    public void Resolve_EqualEffectiveConfiguration_HasStableCanonicalBytes()
    {
        var defaults = Defaults();
        var ordered = Layer(
            "provider", new JsonObject { ["model"] = "model.x", ["endpoint"] = "https://x.invalid/v1" });
        var reordered = Write("layer.json",
            "{\"consumerConfigurationVersion\":1,\"provider\":{\"endpoint\":\"https://x.invalid/v1\",\"model\":\"model.x\"}}");

        var first = Resolve(defaults, ordered).Document.ExactProjection.GetRawText();
        var second = Resolve(defaults, reordered).Document.ExactProjection.GetRawText();
        Assert.Equal(first, second);
    }

    [Fact]
    public void Resolve_InjectedFields_CannotBeReadFromAnyLayer()
    {
        var layer = new JsonObject
        {
            ["consumerConfigurationVersion"] = 1,
            ["planning"] = new JsonObject
            {
                ["campaignLineage"] = "layer.smuggled",
                ["productContractRevisionSha256"] = new string('0', 64),
            },
        };
        AssertInvalid(Defaults(), Write("layer.json", layer.ToJsonString()));
    }

    [Fact]
    public void Resolve_ProductIdentity_DerivesFromTheRunningPayload()
    {
        var first = Resolve(Defaults(), lineage: "l", identity: Identity("a"));
        var second = Resolve(Defaults(), lineage: "l", identity: Identity("b"));
        Assert.NotEqual(
            first.Document.Planning.ProductContractRevisionSha256,
            second.Document.Planning.ProductContractRevisionSha256);
        Assert.Equal(
            ProductSha(Identity("a")),
            first.Document.Planning.ProductContractRevisionSha256);
    }

    [Fact]
    public void Resolve_MergedCrossFieldViolation_FailsClosed()
    {
        var layer = Layer(
            "planning", new JsonObject { ["maximumPatchElapsedMilliseconds"] = 120_001 });
        AssertInvalid(Defaults(), layer);
    }

    [Fact]
    public void Resolve_DefaultsFailures_AreBounded()
    {
        Assert.Throws<CampaignConfigurationException>(() =>
            Resolve(Path.Join(_root, "missing-defaults.json"), lineage: "l"));
        var corrupt = Write("defaults.json", "{\"planning\":{}");
        Assert.Throws<CampaignConfigurationException>(() =>
            Resolve(corrupt, lineage: "l"));
        var directory = Path.Join(_root, "defaults-dir");
        Directory.CreateDirectory(directory);
        Assert.Throws<CampaignConfigurationException>(() =>
            Resolve(directory, lineage: "l"));
    }

    [Fact]
    public void Snapshot_Revalidate_DetectsMutationOfEveryAdmittedSource()
    {
        var defaults = Defaults();
        var downstream = Layer("provider", new JsonObject { ["model"] = "model.d" });
        var overlay = Layer("provider", new JsonObject { ["model"] = "model.o" });
        var snapshot = Resolve(defaults, downstream, overlay);
        Assert.Equal(3, snapshot.Sources.Length);
        Assert.True(snapshot.Revalidate());

        foreach (var index in Enumerable.Range(0, 3))
        {
            var fresh = Resolve(defaults, downstream, overlay);
            Assert.True(fresh.Revalidate());
            var source = fresh.Sources[index];
            File.WriteAllBytes(source.Path, [.. File.ReadAllBytes(source.Path), (byte)' ']);
            Assert.False(fresh.Revalidate(), $"source {index} mutation must fail revalidation");
        }
    }

    [Fact]
    public void Snapshot_Revalidate_FailsOnRemovalOrReplacement()
    {
        var layer = Layer("provider", new JsonObject { ["model"] = "model.x" });
        var snapshot = Resolve(Defaults(), layer);
        File.Delete(snapshot.Sources[1].Path);
        Assert.False(snapshot.Revalidate());
    }

    [Fact]
    public void Schema_PublishedDraftMatchesTheDeclaredConsumerShape()
    {
        var schemaPath = Path.Join(
            RepositoryRoot(), "schemas", "consumer-configuration", "v1.schema.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(schemaPath));
        var properties = document.RootElement.GetProperty("properties");
        var declared = ConsumerConfigurationNode.Root.Children
            .Select(child => child.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var published = properties.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(declared, published);
        Assert.Equal("consumer-configuration/v1", document.RootElement.GetProperty("$id").GetString());
    }

    [Fact]
    public void Schema_EveryDeclaredFieldIsPublishedWithItsKind()
    {
        var schemaPath = Path.Join(
            RepositoryRoot(), "schemas", "consumer-configuration", "v1.schema.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(schemaPath));

        var mismatches = new List<string>();
        Walk(
            document.RootElement.GetProperty("properties"),
            document.RootElement,
            ConsumerConfigurationNode.Root,
            "root",
            mismatches);
        Assert.Empty(mismatches);
    }

    private static void Walk(
        JsonElement properties,
        JsonElement schemaRoot,
        ConsumerConfigurationNode schema,
        string path,
        List<string> mismatches)
    {
        foreach (var child in schema.Children)
        {
            var childPath = path + "." + child.Name;
            if (!properties.TryGetProperty(child.Name, out var published))
            {
                mismatches.Add(childPath + " missing");
                continue;
            }
            if (published.TryGetProperty("$ref", out var reference)
                && reference.GetString() is { } definition
                && definition.StartsWith("#/$defs/", StringComparison.Ordinal)
                && schemaRoot.TryGetProperty("$defs", out var defs)
                && defs.TryGetProperty(definition["#/$defs/".Length..], out var resolved))
            {
                published = resolved;
            }
            var type = published.TryGetProperty("type", out var typeNode)
                ? typeNode
                : default;
            var expected = child.Leaf switch
            {
                ConsumerValueKind.Integer => "integer",
                ConsumerValueKind.Text => "string",
                ConsumerValueKind.Boolean => "boolean",
                ConsumerValueKind.Array => "array",
                null => "object",
                _ => "?",
            };
            var matchesType = child.Nullable
                ? type.ValueKind == JsonValueKind.Array
                    && type.EnumerateArray().Any(item => item.GetString() == expected)
                    && type.EnumerateArray().Any(item => item.GetString() == "null")
                : type.ValueKind == JsonValueKind.String && type.GetString() == expected;
            if (!matchesType)
            {
                mismatches.Add(childPath + $" type expected {expected}{(child.Nullable ? " or null" : "")}");
            }
            if (child.Leaf == ConsumerValueKind.Array)
            {
                if (!published.TryGetProperty("items", out var items))
                {
                    mismatches.Add(childPath + " items missing");
                }
                else if (child.Name == "claimPolicies")
                {
                    var itemType = items.TryGetProperty("type", out var it) ? it.GetString() : null;
                    if (itemType != "object")
                    {
                        mismatches.Add(childPath + " items must be objects");
                    }
                    foreach (var required in new[]
                        { "claimCategoryId", "completeEvidenceRequired", "allowedAuthorities" })
                    {
                        if (!items.TryGetProperty("properties", out var itemProps)
                            || !itemProps.TryGetProperty(required, out _))
                        {
                            mismatches.Add(childPath + " items missing " + required);
                        }
                    }
                }
                else if (!(items.TryGetProperty("type", out var itemType) && itemType.GetString() == "string"))
                {
                    mismatches.Add(childPath + " items must be strings");
                }
            }
            if (child.Leaf is null && child.Children.Length > 0
                && published.TryGetProperty("properties", out var nested))
            {
                Walk(nested, schemaRoot, child, childPath, mismatches);
            }
            else if (child.Leaf is null && child.Children.Length > 0)
            {
                mismatches.Add(childPath + " properties missing");
            }
        }
    }

    private static IEnumerable<(string[] Path, JsonNode? Value)> NonConsumerFields()
    {
        yield return (["campaignConfigurationVersion"], 1);
        yield return (["planning", "campaignLineage"], "lineage.layer");
        yield return (["planning", "productContractRevisionSha256"], new string('0', 64));
        yield return (["planning", "productContractRevisionId"], "product.layer");
        yield return (["planning", "proposalContractId"], "proposal.layer");
        yield return (["planning", "contextSelectionPolicyId"], "context.layer");
        yield return (["planning", "m2ProjectionPolicyId"], "m2.layer");
        yield return (["planning", "m2ProjectionVersion"], 1);
        yield return (["retry"], new JsonObject { ["retryPolicyId"] = "retry.layer" });
        yield return (["scribeRequest", "scribeRequestVersion"], 1);
        yield return (["scribeRequest", "agentProtocolId"], "agent.layer");
        yield return (["scribeRequest", "toolPolicyAndRegistryId"], "tools.layer");
        yield return (["scribeRequest", "toolPolicyId"], "tool.layer");
        yield return (["provider", "providerConfigurationId"], "provider.layer");
        yield return (["provider", "modelConfigurationId"], "model.layer");
        yield return (["provider", "scribeProtocolId"], "scribe.layer");
        yield return (["github"], new JsonObject());
        yield return (["credentials"], new JsonObject());
    }

    private string Defaults(Action<JsonObject>? mutate = null)
    {
        var bytes = File.ReadAllBytes(Path.Join(
            RepositoryRoot(), "config", "defaults.json"));
        if (mutate is not null)
        {
            var node = JsonNode.Parse(bytes)!.AsObject();
            mutate(node);
            return Write("defaults.json", node.ToJsonString());
        }
        return Write("defaults.json", bytes);
    }

    private string Layer(string name, JsonNode? value) =>
        Write("layer-" + Guid.NewGuid().ToString("N") + ".json",
            new JsonObject
            {
                ["consumerConfigurationVersion"] = 1,
                [name] = value,
            }.ToJsonString());

    private string Layer(string first, JsonNode? firstValue, string second, JsonNode? secondValue) =>
        Write("layer-" + Guid.NewGuid().ToString("N") + ".json",
            new JsonObject
            {
                ["consumerConfigurationVersion"] = 1,
                [first] = firstValue,
                [second] = secondValue,
            }.ToJsonString());

    private string Write(string name, string content) =>
        Write(name, Encoding.UTF8.GetBytes(content));

    private string Write(string name, byte[] content)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Join(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static CampaignCommandArguments Arguments(
        string? downstream,
        string? overlay,
        string lineage) =>
        new(
            CampaignOperation.Start,
            "repository",
            "input.slnx",
            "policy.json",
            "snapshot.x",
            "state.json",
            downstream,
            overlay,
            lineage,
            CampaignConfigurationKind.Layered);

    private static CliBuildIdentity Identity(string marker = "a") =>
        CliBuildIdentity.Create("0.1.0-test+" + marker + Revision[..^marker.Length]);

    private static string ProductSha(CliBuildIdentity identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "contract-scribe/campaign-product-revision/v1\0" + identity.SourceRevision)))
            .ToLowerInvariant();

    private CampaignResolvedConfigurationSnapshot Resolve(
        string defaultsPath,
        string? downstream = null,
        string? overlay = null,
        string lineage = "campaign.layered",
        CliBuildIdentity? identity = null) =>
        LayeredCampaignConfigurationResolver.Resolve(
            Arguments(downstream, overlay, lineage),
            _root,
            defaultsPath,
            identity ?? Identity());

    private void AssertInvalid(string defaults, string layer)
    {
        Assert.Throws<CampaignConfigurationException>(() => Resolve(defaults, layer));
    }

    private void AssertInvalidBytes(string defaults, byte[] layerBytes)
    {
        AssertInvalid(defaults, Write("layer-invalid-" + Guid.NewGuid().ToString("N") + ".json", layerBytes));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }
        return Assert.IsType<DirectoryInfo>(directory).FullName;
    }
}
