using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContractScribe.HostValidation;

public static class AuditResultSemanticValidator
{
    public static void Validate(string root, JsonElement document)
    {
        try
        {
            var results = document.GetProperty("results").EnumerateArray().ToArray();
            Require(results.Length > 0);
            foreach (var classification in results.Select(result => result.GetProperty("classification")))
            {
                if (classification.GetProperty("recordType").GetString() == "UnresolvedClassification")
                {
                    ValidateCandidateLocator(classification.GetProperty("candidateLocator"));
                }
            }
            Require(results.Select(ResultSortKey).SequenceEqual(
                results.Select(ResultSortKey).Order()));
            var targetKinds = results
                .Select(result => result.GetProperty("classification"))
                .Where(classification =>
                    classification.GetProperty("recordType").GetString() == "TargetClassification")
                .ToDictionary(
                    classification => classification.GetProperty("symbolRef").GetRawText(),
                    classification => classification.GetProperty("primaryKind").GetString(),
                    StringComparer.Ordinal);
            var subjects = new HashSet<string>(StringComparer.Ordinal);
            foreach (var result in results)
            {
                var classification = result.GetProperty("classification");
                ValidateClassification(root, classification, targetKinds);
                Require(subjects.Add(SubjectKey(classification)));
                ValidatePolicy(result, classification);
                ValidateEvidence(root, result);
                ValidateEvidenceAuthority(root, result, classification);
                ValidateOutcome(root, result, classification);
            }
        }
        catch (ProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or KeyNotFoundException
                or FormatException
                or JsonException)
        {
            throw new ProtocolException("HV230_AUDIT_RESULT_SEMANTICS", exception);
        }
    }

    private static void ValidateClassification(
        string root,
        JsonElement classification,
        IReadOnlyDictionary<string, string?> targetKinds)
    {
        var recordType = classification.GetProperty("recordType").GetString();
        var definition = recordType switch
        {
            "TargetClassification" => "targetClassification",
            "ComponentClassification" => "componentClassification",
            "UnresolvedClassification" => "unresolvedClassification",
            _ => throw new ProtocolException("HV230_AUDIT_RESULT_SEMANTICS")
        };
        SchemaValidation.ValidateElementDefinition(
            classification,
            RepositoryPaths.ResolveConfined(
                root,
                "schemas/symbol-evidence-taxonomy/v1.schema.json"),
            definition);

        using var registry = CanonicalJson.ReadStrict(
            RepositoryPaths.ResolveConfined(
                root,
                "schemas/symbol-evidence-taxonomy/v1.registry.json"),
            2 * 1024 * 1024);
        var support = classification.GetProperty("supportStatus").GetString();
        var origin = classification.GetProperty("origin").GetString();
        ValidateRegistryEntry(registry.RootElement, "supportStatuses", support, recordType);
        ValidateRegistryEntry(registry.RootElement, "origins", origin, recordType);
        JsonElement? classifiedEntry = null;
        if (recordType == "TargetClassification")
        {
            classifiedEntry = ValidateRegistryEntry(
                registry.RootElement,
                "primaryKinds",
                classification.GetProperty("primaryKind").GetString(),
                recordType);
        }
        else if (recordType == "ComponentClassification")
        {
            classifiedEntry = ValidateRegistryEntry(
                registry.RootElement,
                "componentKinds",
                classification.GetProperty("componentKind").GetString(),
                recordType);
        }
        if (classifiedEntry is JsonElement entry)
        {
            ValidateClassificationConstraints(classification, entry, support, origin, targetKinds);
        }
        if (support == "support.supported")
        {
            Require(!classification.TryGetProperty("skipReason", out _));
        }
        else
        {
            var skip = classification.GetProperty("skipReason").GetString();
            var skipEntry = ValidateRegistryEntry(
                registry.RootElement,
                "skipReasons",
                skip,
                recordType);
            ValidateAllowedSupportStatus(skipEntry, support);
            var expectedPrefix = support switch
            {
                "support.unsupported" => "skip.unsupported.",
                "support.ambiguous" => "skip.ambiguous.",
                "support.not-applicable" => "skip.not-applicable.",
                "support.unavailable-context" => "skip.unavailable.",
                _ => throw new ProtocolException("HV230_AUDIT_RESULT_SEMANTICS")
            };
            Require(skip?.StartsWith(expectedPrefix, StringComparison.Ordinal) == true);
        }
        ValidateOriginSpecificCombination(
            recordType,
            support,
            origin,
            classification.TryGetProperty("skipReason", out var skipReason)
                ? skipReason.GetString()
                : null);
    }

    private static void ValidatePolicy(JsonElement result, JsonElement classification)
    {
        var contributions = result.GetProperty("policyContributions").EnumerateArray().ToArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var expectations = new HashSet<string>(StringComparer.Ordinal);
        var orderedKeys = new List<string>(contributions.Length);
        foreach (var contribution in contributions)
        {
            var project = contribution.GetProperty("projectPath").GetString();
            Require(IsRepositoryPath(project));
            string key;
            if (contribution.TryGetProperty("sourcePath", out var source))
            {
                Require(IsRepositoryPath(source.GetString()));
                key = $"0\0{project}\0{source.GetString()}";
            }
            else
            {
                var generated = contribution.GetProperty("generatedOutput");
                key = $"1\0{project}\0{generated.GetProperty("producerKind").GetString()}\0{generated.GetProperty("producerId").GetString()}\0{generated.GetProperty("outputId").GetString()}";
            }
            Require(keys.Add(key));
            orderedKeys.Add(key);
            expectations.Add(contribution.GetProperty("policyExpectation").GetString()!);
        }
        Require(orderedKeys.SequenceEqual(
            orderedKeys.Order(StringComparer.Ordinal),
            StringComparer.Ordinal));

        var supported = classification.GetProperty("supportStatus").GetString() == "support.supported";
        var expectedResolution = !supported || contributions.Length == 0
            ? "unavailable"
            : expectations.Count > 1
                ? "conflict"
                : contributions.Length == 1
                    ? "single"
                    : "all-declarations-agree";
        Require(result.GetProperty("policyResolution").GetString() == expectedResolution);
        var expectation = result.GetProperty("policyExpectation");
        if (expectedResolution is "conflict" or "unavailable")
        {
            Require(expectation.ValueKind == JsonValueKind.Null);
        }
        else
        {
            Require(expectation.GetString() == expectations.Single());
        }
    }

    private static void ValidateEvidence(string root, JsonElement result)
    {
        var bundle = result.GetProperty("evidenceBundle");
        SchemaValidation.ValidateElementDefinition(
            bundle,
            RepositoryPaths.ResolveConfined(
                root,
                "schemas/audit-result/v1.schema.json"),
            "evidenceBundle");
        var items = bundle.GetProperty("items").EnumerateArray().ToArray();
        Require(items
            .Select(item => item.GetProperty("evidenceId").GetString())
            .SequenceEqual(
                items.Select(item => item.GetProperty("evidenceId").GetString())
                    .Order(StringComparer.Ordinal),
                StringComparer.Ordinal));
        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var item in items)
        {
            var id = item.GetProperty("evidenceId").GetString()
                ?? throw new ProtocolException("HV230_AUDIT_RESULT_SEMANTICS");
            Require(byId.TryAdd(id, item));
            var excerpt = item.GetProperty("excerpt").GetString() ?? string.Empty;
            var included = item.GetProperty("includedUtf8ByteCount").GetInt32();
            var original = item.GetProperty("originalUtf8ByteCount").GetInt32();
            var omitted = item.GetProperty("omittedUtf8ByteCount").GetInt32();
            var truncated = item.GetProperty("isTruncated").GetBoolean();
            Require(Encoding.UTF8.GetByteCount(excerpt) == included);
            Require(included + omitted == original);
            Require((omitted > 0) == truncated);
            Require(included <= 4096);
            totalBytes += included;
            if (!truncated)
            {
                Require(item.GetProperty("sha256").GetString()
                    == CanonicalJson.Sha256(Encoding.UTF8.GetBytes(excerpt)));
            }
        }
        Require(items.Length <= 32 && totalBytes <= 32768);

        var referenced = result.GetProperty("evidenceIds").EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
        Require(referenced.Distinct(StringComparer.Ordinal).Count() == referenced.Length);
        Require(referenced.SequenceEqual(referenced.Order(StringComparer.Ordinal), StringComparer.Ordinal));
        Require(referenced.All(id => byId.TryGetValue(id, out var item)
            && !item.GetProperty("isTruncated").GetBoolean()));

        var status = bundle.GetProperty("availabilityStatus").GetString();
        if (status == "evidence.bundle.complete")
        {
            Require(items.Length > 0);
            Require(!bundle.TryGetProperty("omissionReason", out _));
            Require(items.All(item => !item.GetProperty("isTruncated").GetBoolean()));
        }
        else if (status == "evidence.bundle.partial")
        {
            Require(items.Length > 0);
            Require(bundle.GetProperty("omissionReason").GetString()
                == "evidence.omission.budget-exhausted");
            Require(referenced.Length == 0);
        }
        else
        {
            Require(items.Length == 0);
        }
    }

    private static void ValidateOutcome(
        string root,
        JsonElement result,
        JsonElement classification)
    {
        var contributions = result.GetProperty("policyContributions").EnumerateArray().ToArray();
        var reason = result.GetProperty("reasonCode").GetString();
        using var registry = CanonicalJson.ReadStrict(
            RepositoryPaths.ResolveConfined(root, "schemas/audit-result/v1.registry.json"),
            1024 * 1024);
        var reasonEntry = registry.RootElement.GetProperty("sections").GetProperty("reasons")
            .EnumerateArray()
            .SingleOrDefault(entry => entry.GetProperty("id").GetString() == reason);
        Require(reasonEntry.ValueKind == JsonValueKind.Object);
        var legal = reasonEntry.GetProperty("legal");
        var expectedOutcome = reasonEntry.GetProperty("outcome").GetString();
        Require(expectedOutcome is not null);
        Require(result.GetProperty("auditOutcome").GetString() == expectedOutcome);
        Require(ContainsNullable(
            legal.GetProperty("policyExpectation"),
            result.GetProperty("policyExpectation")));
        Require(ContainsNullable(
            legal.GetProperty("documentationObservation"),
            result.GetProperty("documentationObservation")));
        Require(legal.GetProperty("policyResolution").EnumerateArray()
            .Any(value => value.GetString() == result.GetProperty("policyResolution").GetString()));
        var bundle = result.GetProperty("evidenceBundle");
        Require(legal.GetProperty("bundleStatus").EnumerateArray()
            .Any(value => value.GetString() == bundle.GetProperty("availabilityStatus").GetString()));
        var contributionCount = legal.GetProperty("contributionCount");
        Require(contributionCount.ValueKind == JsonValueKind.Number
            ? contributions.Length == contributionCount.GetInt32()
            : contributionCount.GetString() switch
            {
                "any" => true,
                "at-least-1" => contributions.Length >= 1,
                "at-least-2" => contributions.Length >= 2,
                _ => false
            });
        var evidenceIds = result.GetProperty("evidenceIds");
        Require((evidenceIds.GetArrayLength() > 0)
            == legal.GetProperty("requiresEvidence").GetBoolean());

        var support = classification.GetProperty("supportStatus").GetString();
        var expectedPrimaryReason = support != "support.supported"
            ? "audit.reason.classification-skipped"
            : result.GetProperty("policyResolution").GetString() == "conflict"
                ? "audit.reason.policy-conflict"
                : contributions.Length == 0
                    ? "audit.reason.policy-unavailable"
                    : result.GetProperty("documentationObservation").GetString()
                        == "documentation.unavailable"
                        ? bundle.GetProperty("availabilityStatus").GetString() switch
                        {
                            "evidence.bundle.partial" => "audit.reason.evidence-incomplete",
                            "evidence.bundle.complete" =>
                                "audit.reason.documentation-unavailable.malformed-xml",
                            _ => "audit.reason.documentation-unavailable"
                        }
                        : reason;
        Require(reason == expectedPrimaryReason);

        var status = bundle.GetProperty("availabilityStatus").GetString();
        var omission = bundle.TryGetProperty("omissionReason", out var omissionValue)
            ? omissionValue.GetString()
            : null;
        Require(status switch
        {
            "evidence.bundle.complete" => omission is null,
            "evidence.bundle.partial" => omission == "evidence.omission.budget-exhausted",
            "evidence.bundle.unavailable" => reason switch
            {
                "audit.reason.classification-skipped" or "audit.reason.policy-conflict"
                    or "audit.reason.policy-unavailable" =>
                    omission == "evidence.omission.not-provided",
                "audit.reason.documentation-unavailable" =>
                    omission == "evidence.omission.source-unavailable",
                _ => false
            },
            _ => false
        });
        if (expectedOutcome is "audit.outcome.compliant" or "audit.outcome.violation")
        {
            Require(status == "evidence.bundle.complete");
        }
    }

    private static void ValidateEvidenceAuthority(
        string root,
        JsonElement result,
        JsonElement classification)
    {
        var observationValue = result.GetProperty("documentationObservation");
        var requiresAuthority =
            observationValue.ValueKind == JsonValueKind.String
            && observationValue.GetString() is "documentation.present" or "documentation.absent"
            || result.GetProperty("reasonCode").GetString()
                == "audit.reason.documentation-unavailable.malformed-xml";
        var hasAuthority = result.TryGetProperty("evidenceAuthority", out var authority);
        var bundle = result.GetProperty("evidenceBundle");
        var hasObservation = bundle.TryGetProperty("observationSubject", out var observation);
        Require(hasAuthority == requiresAuthority && hasObservation == requiresAuthority);
        if (!requiresAuthority)
        {
            return;
        }

        SchemaValidation.ValidateElementDefinition(
            authority,
            RepositoryPaths.ResolveConfined(root, "schemas/audit-result/v1.schema.json"),
            "evidenceAuthority");
        SchemaValidation.ValidateElementDefinition(
            observation,
            RepositoryPaths.ResolveConfined(
                root,
                "schemas/symbol-evidence-taxonomy/v1.schema.json"),
            "observationSubject");

        var declarations = authority.GetProperty("declarations").EnumerateArray()
            .ToArray();
        Require(declarations
            .Select(item => item.GetProperty("declarationId").GetString())
            .SequenceEqual(
                declarations.Select(item => item.GetProperty("declarationId").GetString())
                    .Order(StringComparer.Ordinal),
                StringComparer.Ordinal));
        var declarationDigest = ComputeDeclarationDigest(declarations);
        Require(authority.GetProperty("declarationSetId").GetString()
                == $"dset.{declarationDigest}"
            && observation.GetProperty("authoritativeDeclarationSetDigest").GetString()
                == declarationDigest
            && observation.GetProperty("authoritativeDeclarationCount").GetInt32()
                == declarations.Length
            && observation.GetProperty("observationSubjectRef").GetString()
                == ComputeObservationSubjectRef(observation));

        var expectedSubject = classification.GetProperty("recordType").GetString()
            == "ComponentClassification"
            ? JsonSerializer.SerializeToElement(new JsonObject
            {
                ["parentSymbolRef"] = JsonNode.Parse(
                    classification.GetProperty("parentSymbolRef").GetRawText()),
                ["componentKind"] = classification.GetProperty("componentKind").GetString(),
                ["identity"] = classification.GetProperty("identity").GetString()
            })
            : classification.GetProperty("symbolRef");
        Require(JsonElement.DeepEquals(observation.GetProperty("subject"), expectedSubject));
        var expectedContext = expectedSubject.TryGetProperty("parentSymbolRef", out var parent)
            ? parent.GetProperty("compilationContextRef").GetString()
            : expectedSubject.GetProperty("compilationContextRef").GetString();
        Require(observation.GetProperty("compilationContextRef").GetString() == expectedContext);

        var declarationIds = new HashSet<string>(StringComparer.Ordinal);
        var declarationEvidenceIds = new HashSet<string>(StringComparer.Ordinal);
        var malformedEvidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            Require(declarationIds.Add(declaration.GetProperty("declarationId").GetString()!));
            var evidenceId = declaration.GetProperty("evidenceId").GetString()!;
            Require(declarationEvidenceIds.Add(evidenceId));
            if (declaration.GetProperty("blockState").GetString() == "malformed")
            {
                malformedEvidenceIds.Add(evidenceId);
            }
        }
        var roles = declarations
            .Select(declaration => declaration.GetProperty("authorityRole").GetString())
            .ToArray();
        Require(roles.Length == 1
                && roles[0] is "ordinary"
                    or "partial-member-implementing"
                    or "partial-member-defining-fallback"
            || roles.Length > 0 && roles.All(role => role == "partial-type-part"));

        var evidenceItems = bundle.GetProperty("items").EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("evidenceId").GetString()!,
                StringComparer.Ordinal);
        foreach (var evidenceId in declarationEvidenceIds)
        {
            Require(evidenceItems.TryGetValue(evidenceId, out var evidence)
                && JsonElement.DeepEquals(evidence.GetProperty("subject"), expectedSubject));
        }
        foreach (var evidenceId in malformedEvidenceIds)
        {
            var evidence = evidenceItems[evidenceId];
            Require(evidence.GetProperty("kind").GetString()
                    == "evidence.source.xml-documentation"
                && evidence.GetProperty("relation").GetString() == "evidence.documents"
                && !evidence.GetProperty("isTruncated").GetBoolean());
        }

        var referenced = result.GetProperty("evidenceIds").EnumerateArray()
            .Select(value => value.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Require(referenced.SetEquals(declarationEvidenceIds));
        var derivedObservation = DeriveDocumentationObservation(
            expectedSubject,
            authority,
            declarations);
        Require(result.GetProperty("documentationObservation").GetString()
                == derivedObservation
            && (result.GetProperty("reasonCode").GetString()
                    == "audit.reason.documentation-unavailable.malformed-xml")
                == (derivedObservation == "documentation.unavailable"
                    && malformedEvidenceIds.Count > 0));
    }

    private static string ComputeDeclarationDigest(IReadOnlyList<JsonElement> declarations)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartArray();
            foreach (var declaration in declarations)
            {
                writer.WriteStartObject();
                foreach (var name in new[]
                {
                    "declarationId",
                    "authorityRole",
                    "blockState",
                    "evidenceId",
                    "componentLocalName",
                    "componentMatch"
                })
                {
                    if (declaration.TryGetProperty(name, out var value))
                    {
                        writer.WritePropertyName(name);
                        value.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static string ComputeObservationSubjectRef(JsonElement observation)
    {
        var subject = observation.GetProperty("subject");
        var canonicalSubject = subject.TryGetProperty("parentSymbolRef", out var parent)
            ? new JsonObject
            {
                ["parentSymbolRef"] = new JsonObject
                {
                    ["compilationContextRef"] =
                        parent.GetProperty("compilationContextRef").GetString(),
                    ["documentationCommentId"] =
                        parent.GetProperty("documentationCommentId").GetString()
                },
                ["componentKind"] = subject.GetProperty("componentKind").GetString(),
                ["identity"] = subject.GetProperty("identity").GetString()
            }
            : new JsonObject
            {
                ["compilationContextRef"] =
                    subject.GetProperty("compilationContextRef").GetString(),
                ["documentationCommentId"] =
                    subject.GetProperty("documentationCommentId").GetString()
            };
        var preimage = new JsonObject
        {
            ["compilationContextRef"] =
                observation.GetProperty("compilationContextRef").GetString(),
            ["subject"] = canonicalSubject,
            ["authoritativeDeclarationSetDigest"] =
                observation.GetProperty("authoritativeDeclarationSetDigest").GetString(),
            ["authoritativeDeclarationCount"] =
                observation.GetProperty("authoritativeDeclarationCount").GetInt32()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(preimage, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        return $"obs.{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }

    private static string DeriveDocumentationObservation(
        JsonElement subject,
        JsonElement authority,
        IReadOnlyList<JsonElement> declarations)
    {
        var component = subject.TryGetProperty("parentSymbolRef", out _);
        if (!component)
        {
            if (declarations.Any(declaration =>
                declaration.GetProperty("blockState").GetString()
                    is "well-formed" or "malformed"))
            {
                return "documentation.present";
            }
            return authority.GetProperty("completeness").GetString() == "complete"
                && declarations.All(declaration =>
                    declaration.GetProperty("blockState").GetString()
                        is "no-block" or "whitespace-only")
                ? "documentation.absent"
                : "documentation.unavailable";
        }
        if (declarations.Any(declaration =>
            declaration.GetProperty("blockState").GetString() == "well-formed"
            && declaration.TryGetProperty("componentMatch", out var match)
            && match.GetString() == "present"))
        {
            return "documentation.present";
        }
        if (declarations.Any(declaration =>
            declaration.GetProperty("blockState").GetString() == "malformed"))
        {
            return "documentation.unavailable";
        }
        return authority.GetProperty("completeness").GetString() == "complete"
            && declarations.All(declaration =>
                declaration.GetProperty("blockState").GetString()
                    is "no-block" or "whitespace-only"
                || declaration.TryGetProperty("componentMatch", out var match)
                    && match.GetString() == "absent")
            ? "documentation.absent"
            : "documentation.unavailable";
    }

    private static JsonElement ValidateRegistryEntry(
        JsonElement registry,
        string section,
        string? id,
        string? recordType)
    {
        var entry = registry.GetProperty("sections").GetProperty(section).EnumerateArray()
            .SingleOrDefault(candidate => candidate.GetProperty("id").GetString() == id);
        Require(entry.ValueKind == JsonValueKind.Object);
        Require(entry.GetProperty("recordTypes").EnumerateArray()
            .Any(value => value.GetString() == recordType));
        return entry;
    }

    private static void ValidateClassificationConstraints(
        JsonElement classification,
        JsonElement entry,
        string? support,
        string? origin,
        IReadOnlyDictionary<string, string?> targetKinds)
    {
        ValidateAllowedSupportStatus(entry, support);
        if (entry.TryGetProperty("requiredOrigin", out var requiredOrigin))
        {
            Require(origin == requiredOrigin.GetString());
        }
        if (entry.TryGetProperty("requiredSkip", out var requiredSkip))
        {
            Require(classification.TryGetProperty("skipReason", out var skip)
                && skip.GetString() == requiredSkip.GetString());
        }

        var recordType = classification.GetProperty("recordType").GetString();
        if (recordType == "TargetClassification")
        {
            Require((classification.GetProperty("primaryKind").GetString() == "symbol.unknown")
                == (support == "support.unsupported"));
        }
        else if (recordType == "ComponentClassification")
        {
            Require((classification.GetProperty("componentKind").GetString() == "component.unknown")
                == (support == "support.unsupported"));
            if (entry.TryGetProperty("parentKinds", out var parentKinds))
            {
                var parentRef = classification.GetProperty("parentSymbolRef").GetRawText();
                var possibleParentKinds = targetKinds.TryGetValue(parentRef, out var exactParentKind)
                    ? new HashSet<string?>([exactParentKind], StringComparer.Ordinal)
                    : ParseParentKinds(classification
                        .GetProperty("parentSymbolRef")
                        .GetProperty("documentationCommentId")
                        .GetString());
                Require(parentKinds.EnumerateArray()
                    .Select(value => value.GetString())
                    .Any(possibleParentKinds.Contains));
            }
        }
    }

    private static void ValidateAllowedSupportStatus(JsonElement entry, string? support)
    {
        if (entry.TryGetProperty("allowedSupportStatuses", out var statuses))
        {
            Require(statuses.EnumerateArray().Any(value => value.GetString() == support));
        }
    }

    private static IReadOnlySet<string?> ParseParentKinds(string? documentationId)
    {
        if (string.IsNullOrWhiteSpace(documentationId) || documentationId.Length < 3
            || documentationId[1] != ':')
        {
            throw new ProtocolException("HV230_AUDIT_RESULT_SEMANTICS");
        }
        if (documentationId[0] == 'M')
        {
            var member = documentationId[(documentationId.LastIndexOf('.') + 1)..];
            return member.StartsWith("#ctor", StringComparison.Ordinal)
                    || member.StartsWith("#cctor", StringComparison.Ordinal)
                ? new HashSet<string?>(["symbol.member.constructor"], StringComparer.Ordinal)
                : member.StartsWith("op_Implicit", StringComparison.Ordinal)
                    || member.StartsWith("op_Explicit", StringComparison.Ordinal)
                    ? new HashSet<string?>(["symbol.member.conversion"], StringComparer.Ordinal)
                    : member.StartsWith("op_", StringComparison.Ordinal)
                        ? new HashSet<string?>(["symbol.member.operator"], StringComparer.Ordinal)
                        : new HashSet<string?>(["symbol.member.method"], StringComparer.Ordinal);
        }
        return documentationId[0] switch
        {
            'T' => new HashSet<string?>([
                "symbol.type.class",
                "symbol.type.struct",
                "symbol.type.interface",
                "symbol.type.enum",
                "symbol.type.delegate"
            ], StringComparer.Ordinal),
            'P' when documentationId.Contains('(', StringComparison.Ordinal) =>
                new HashSet<string?>(["symbol.member.indexer"], StringComparer.Ordinal),
            'P' => new HashSet<string?>(["symbol.member.property"], StringComparer.Ordinal),
            'F' => new HashSet<string?>([
                "symbol.member.field",
                "symbol.member.enum-member"
            ], StringComparer.Ordinal),
            'E' => new HashSet<string?>(["symbol.member.event"], StringComparer.Ordinal),
            _ => new HashSet<string?>(["symbol.unknown"], StringComparer.Ordinal)
        };
    }

    private static void ValidateOriginSpecificCombination(
        string? recordType,
        string? support,
        string? origin,
        string? skip)
    {
        if (support == "support.unavailable-context")
        {
            if (skip == "skip.unavailable.generated-provenance")
            {
                Require(origin == "origin.unknown");
            }
            else if (skip is "skip.unavailable.documentation-comment-id"
                     or "skip.unavailable.semantic-context")
            {
                Require(origin is "origin.source"
                    or "origin.source-generator"
                    or "origin.tool-generated"
                    or "origin.mixed");
            }
            else
            {
                throw new ProtocolException("HV230_AUDIT_RESULT_SEMANTICS");
            }
        }
        if (origin == "origin.unknown")
        {
            Require(support == "support.unavailable-context");
            Require(skip == "skip.unavailable.generated-provenance");
        }
        if (origin == "origin.mixed")
        {
            Require(
                support == "support.ambiguous"
                    && skip is "skip.ambiguous.mixed-origin" or "skip.ambiguous.partial-declaration"
                || support == "support.unavailable-context"
                    && skip is "skip.unavailable.documentation-comment-id" or "skip.unavailable.semantic-context");
        }
        if (skip == "skip.ambiguous.mixed-origin")
        {
            Require(origin == "origin.mixed");
        }
    }

    private static string SubjectKey(JsonElement classification) =>
        classification.GetProperty("recordType").GetString() switch
        {
            "TargetClassification" =>
                $"target\0{classification.GetProperty("symbolRef").GetRawText()}",
            "ComponentClassification" =>
                $"component\0{classification.GetProperty("parentSymbolRef").GetRawText()}\0{classification.GetProperty("componentKind").GetString()}\0{classification.GetProperty("identity").GetString()}",
            "UnresolvedClassification" =>
                $"unresolved\0{classification.GetProperty("compilationContextRef").GetString()}\0{CandidateLocatorKey(classification.GetProperty("candidateLocator"))}",
            _ => throw new ProtocolException("HV230_AUDIT_RESULT_SEMANTICS")
        };

    private static ResultOrderKey ResultSortKey(JsonElement result)
    {
        var classification = result.GetProperty("classification");
        return classification.GetProperty("recordType").GetString() switch
        {
            "TargetClassification" => new(
                0,
                classification.GetProperty("symbolRef").GetProperty("compilationContextRef").GetString()!,
                0,
                classification.GetProperty("symbolRef").GetProperty("documentationCommentId").GetString()!,
                string.Empty,
                string.Empty,
                false,
                0,
                0),
            "ComponentClassification" => new(
                1,
                classification.GetProperty("parentSymbolRef").GetProperty("compilationContextRef").GetString()!,
                0,
                classification.GetProperty("parentSymbolRef").GetProperty("documentationCommentId").GetString()!,
                classification.GetProperty("componentKind").GetString()!,
                classification.GetProperty("identity").GetString()!,
                false,
                0,
                0),
            "UnresolvedClassification" => UnresolvedResultSortKey(classification),
            _ => throw new ProtocolException("HV230_AUDIT_RESULT_SEMANTICS")
        };
    }

    private static ResultOrderKey UnresolvedResultSortKey(JsonElement classification)
    {
        var context = classification.GetProperty("compilationContextRef").GetString()!;
        var locator = classification.GetProperty("candidateLocator");
        if (locator.TryGetProperty("repository", out var repository))
        {
            return LocatorKey(context, 0, repository.GetProperty("path").GetString()!, string.Empty, repository);
        }
        if (locator.TryGetProperty("generatedSource", out var generatedSource))
        {
            return LocatorKey(
                context,
                1,
                generatedSource.GetProperty("generatorId").GetString()!,
                generatedSource.GetProperty("hintNameId").GetString()!,
                generatedSource);
        }
        if (locator.TryGetProperty("toolGenerated", out var toolGenerated))
        {
            return LocatorKey(
                context,
                2,
                toolGenerated.GetProperty("producerId").GetString()!,
                toolGenerated.GetProperty("outputId").GetString()!,
                toolGenerated);
        }
        return new(
            2,
            context,
            3,
            locator.GetProperty("synthetic").GetProperty("fixtureId").GetString()!,
            string.Empty,
            string.Empty,
            false,
            0,
            0);
    }

    private static ResultOrderKey LocatorKey(
        string context,
        int variantRank,
        string field1,
        string field2,
        JsonElement locator)
    {
        var hasSpan = locator.TryGetProperty("span", out var span);
        return new(
            2,
            context,
            variantRank,
            field1,
            field2,
            string.Empty,
            hasSpan,
            hasSpan ? span.GetProperty("start").GetInt32() : 0,
            hasSpan ? span.GetProperty("end").GetInt32() : 0);
    }

    private static void ValidateCandidateLocator(JsonElement locator)
    {
        var variants = new[] { "repository", "generatedSource", "toolGenerated", "synthetic" }
            .Where(name => locator.TryGetProperty(name, out _))
            .ToArray();
        Require(variants.Length == 1);
        if (variants[0] == "repository")
        {
            var repository = locator.GetProperty("repository");
            Require(IsRepositoryPath(repository.GetProperty("path").GetString()));
            ValidateSpan(repository);
        }
        else if (variants[0] == "generatedSource")
        {
            var generatedSource = locator.GetProperty("generatedSource");
            Require(IsGeneratedId(generatedSource.GetProperty("generatorId").GetString(), "sgp."));
            Require(IsGeneratedId(generatedSource.GetProperty("hintNameId").GetString(), "sgo."));
            ValidateSpan(generatedSource);
        }
        else if (variants[0] == "toolGenerated")
        {
            var toolGenerated = locator.GetProperty("toolGenerated");
            Require(IsGeneratedId(toolGenerated.GetProperty("producerId").GetString(), "tgp."));
            Require(IsGeneratedId(toolGenerated.GetProperty("outputId").GetString(), "tgo."));
            ValidateSpan(toolGenerated);
        }
    }

    private static string CandidateLocatorKey(JsonElement locator)
    {
        if (locator.TryGetProperty("repository", out var repository))
        {
            return $"repository\0{repository.GetProperty("path").GetString()}\0{SpanKey(repository)}";
        }
        if (locator.TryGetProperty("generatedSource", out var generatedSource))
        {
            return $"generatedSource\0{generatedSource.GetProperty("generatorId").GetString()}\0{generatedSource.GetProperty("hintNameId").GetString()}\0{SpanKey(generatedSource)}";
        }
        if (locator.TryGetProperty("toolGenerated", out var toolGenerated))
        {
            return $"toolGenerated\0{toolGenerated.GetProperty("producerId").GetString()}\0{toolGenerated.GetProperty("outputId").GetString()}\0{SpanKey(toolGenerated)}";
        }
        return $"synthetic\0{locator.GetProperty("synthetic").GetProperty("fixtureId").GetString()}";
    }

    private static string SpanKey(JsonElement locator) =>
        locator.TryGetProperty("span", out var span)
            ? $"{span.GetProperty("start").GetInt32()}\0{span.GetProperty("end").GetInt32()}"
            : string.Empty;

    private static void ValidateSpan(JsonElement locator)
    {
        if (!locator.TryGetProperty("span", out var span))
        {
            return;
        }
        Require(span.GetProperty("start").GetInt32() <= span.GetProperty("end").GetInt32());
    }

    private static bool IsGeneratedId(string? value, string prefix) =>
        value is not null
        && value.Length == prefix.Length + 64
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value[prefix.Length..].All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private readonly record struct ResultOrderKey(
        int TypeRank,
        string Primary,
        int VariantRank,
        string Field1,
        string Field2,
        string Field3,
        bool HasSpan,
        int Start,
        int End) : IComparable<ResultOrderKey>
    {
        public int CompareTo(ResultOrderKey other)
        {
            var comparison = TypeRank.CompareTo(other.TypeRank);
            if (comparison != 0) return comparison;
            comparison = string.CompareOrdinal(Primary, other.Primary);
            if (comparison != 0) return comparison;
            comparison = VariantRank.CompareTo(other.VariantRank);
            if (comparison != 0) return comparison;
            comparison = string.CompareOrdinal(Field1, other.Field1);
            if (comparison != 0) return comparison;
            comparison = string.CompareOrdinal(Field2, other.Field2);
            if (comparison != 0) return comparison;
            comparison = string.CompareOrdinal(Field3, other.Field3);
            if (comparison != 0) return comparison;
            comparison = HasSpan.CompareTo(other.HasSpan);
            if (comparison != 0) return comparison;
            comparison = Start.CompareTo(other.Start);
            return comparison != 0 ? comparison : End.CompareTo(other.End);
        }
    }

    private static bool ContainsNullable(JsonElement values, JsonElement actual) =>
        values.EnumerateArray().Any(value =>
            value.ValueKind == JsonValueKind.Null && actual.ValueKind == JsonValueKind.Null
            || value.ValueKind == JsonValueKind.String
                && actual.ValueKind == JsonValueKind.String
                && value.GetString() == actual.GetString());

    private static bool IsRepositoryPath(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !Path.IsPathRooted(value)
        && !(value.Length >= 2
            && value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
            && value[1] == ':')
        && !value.Contains('\\', StringComparison.Ordinal)
        && !value.Split('/').Any(segment => segment is "" or "." or "..");

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new ProtocolException("HV230_AUDIT_RESULT_SEMANTICS");
        }
    }
}
