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
            var subjects = new HashSet<string>(StringComparer.Ordinal);
            foreach (var result in results)
            {
                var classification = result.GetProperty("classification");
                ValidateClassification(root, classification);
                Require(subjects.Add(SubjectKey(classification)));
                ValidatePolicy(result, classification);
                ValidateEvidence(root, result);
                ValidateEvidenceAuthority(root, result, classification);
                ValidateOutcome(result, classification);
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

    private static void ValidateClassification(string root, JsonElement classification)
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
        if (recordType == "TargetClassification")
        {
            ValidateRegistryEntry(
                registry.RootElement,
                "primaryKinds",
                classification.GetProperty("primaryKind").GetString(),
                recordType);
        }
        else if (recordType == "ComponentClassification")
        {
            ValidateRegistryEntry(
                registry.RootElement,
                "componentKinds",
                classification.GetProperty("componentKind").GetString(),
                recordType);
        }
        if (support == "support.supported")
        {
            Require(!classification.TryGetProperty("skipReason", out _));
        }
        else
        {
            var skip = classification.GetProperty("skipReason").GetString();
            ValidateRegistryEntry(registry.RootElement, "skipReasons", skip, recordType);
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
    }

    private static void ValidatePolicy(JsonElement result, JsonElement classification)
    {
        var contributions = result.GetProperty("policyContributions").EnumerateArray().ToArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var expectations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contribution in contributions)
        {
            var project = contribution.GetProperty("projectPath").GetString();
            Require(IsRepositoryPath(project));
            string key;
            if (contribution.TryGetProperty("sourcePath", out var source))
            {
                Require(IsRepositoryPath(source.GetString()));
                key = $"{project}\0source\0{source.GetString()}";
            }
            else
            {
                var generated = contribution.GetProperty("generatedOutput");
                key = $"{project}\0generated\0{generated.GetProperty("producerId").GetString()}\0{generated.GetProperty("outputId").GetString()}";
            }
            Require(keys.Add(key));
            expectations.Add(contribution.GetProperty("policyExpectation").GetString()!);
        }

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

    private static void ValidateOutcome(JsonElement result, JsonElement classification)
    {
        var contributions = result.GetProperty("policyContributions").EnumerateArray().ToArray();
        var expectations = contributions
            .Select(item => item.GetProperty("policyExpectation").GetString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var observation = result.GetProperty("documentationObservation").ValueKind == JsonValueKind.Null
            ? null
            : result.GetProperty("documentationObservation").GetString();
        var bundleStatus = result.GetProperty("evidenceBundle")
            .GetProperty("availabilityStatus").GetString();
        var derivedReason = classification.GetProperty("supportStatus").GetString() != "support.supported"
            ? "audit.reason.classification-skipped"
            : expectations.Length > 1
                ? "audit.reason.policy-conflict"
                : contributions.Length == 0
                    ? "audit.reason.policy-unavailable"
                    : observation == "documentation.unavailable" && bundleStatus == "evidence.bundle.partial"
                        ? "audit.reason.evidence-incomplete"
                        : observation == "documentation.unavailable"
                            ? "audit.reason.documentation-unavailable"
                            : (expectations.Single(), observation) switch
                            {
                                ("required", "documentation.present") => "audit.reason.required-present",
                                ("required", "documentation.absent") => "audit.reason.required-absent",
                                ("optional", "documentation.present") => "audit.reason.optional-present",
                                ("optional", "documentation.absent") => "audit.reason.optional-absent",
                                ("forbidden", "documentation.present") => "audit.reason.forbidden-present",
                                ("forbidden", "documentation.absent") => "audit.reason.forbidden-absent",
                                _ => throw new ProtocolException("HV230_AUDIT_RESULT_SEMANTICS")
                            };
        var reason = result.GetProperty("reasonCode").GetString();
        Require(reason == derivedReason
            || reason == "audit.reason.documentation-unavailable.malformed-xml"
                && derivedReason == "audit.reason.documentation-unavailable");
        var expectedOutcome = reason switch
        {
            "audit.reason.required-absent" or "audit.reason.forbidden-present" =>
                "audit.outcome.violation",
            "audit.reason.required-present"
                or "audit.reason.optional-present"
                or "audit.reason.optional-absent"
                or "audit.reason.forbidden-absent" => "audit.outcome.compliant",
            _ => "audit.outcome.skipped"
        };
        Require(result.GetProperty("auditOutcome").GetString() == expectedOutcome);
        if (expectedOutcome is "audit.outcome.compliant" or "audit.outcome.violation")
        {
            Require(result.GetProperty("evidenceIds").GetArrayLength() > 0);
            Require(bundleStatus == "evidence.bundle.complete");
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
            .OrderBy(item => item.GetProperty("declarationId").GetString(), StringComparer.Ordinal)
            .ToArray();
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

    private static void ValidateRegistryEntry(
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
    }

    private static string SubjectKey(JsonElement classification) =>
        classification.GetProperty("recordType").GetString() switch
        {
            "TargetClassification" =>
                $"target\0{classification.GetProperty("symbolRef").GetRawText()}",
            "ComponentClassification" =>
                $"component\0{classification.GetProperty("parentSymbolRef").GetRawText()}\0{classification.GetProperty("componentKind").GetString()}\0{classification.GetProperty("identity").GetString()}",
            "UnresolvedClassification" =>
                $"unresolved\0{classification.GetProperty("compilationContextRef").GetString()}\0{classification.GetProperty("candidateLocator").GetRawText()}",
            _ => throw new ProtocolException("HV230_AUDIT_RESULT_SEMANTICS")
        };

    private static bool IsRepositoryPath(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !Path.IsPathRooted(value)
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
