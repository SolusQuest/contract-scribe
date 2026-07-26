using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContractScribe.Tests;

public sealed class M1AuditCliContractTests
{
    private const string BaselineCommit = "0806a98609b0eb97014496dcc4f4c8083ec57533";
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string AnnexPath = Path.Join(Root, "tests", "fixtures", "m1-audit-cli", "cli-contract-v1.json");
    private static readonly string DocPath = Path.Join(Root, "docs", "20_architecture", "audit-cli.md");

    private static readonly string[] ExpectedProtectedInputs =
    [
        "docs/20_architecture/contracts/policy-configuration-v1.md",
        "docs/20_architecture/contracts/symbol-evidence-taxonomy-v1.md",
        "docs/20_architecture/contracts/audit-result-v1.md",
        "schemas/policy-configuration/v1.schema.json",
        "schemas/symbol-evidence-taxonomy/v1.schema.json",
        "schemas/symbol-evidence-taxonomy/v1.registry.json",
        "schemas/symbol-evidence-taxonomy/v1.manifest.schema.json",
        "schemas/audit-result/v1.schema.json",
        "schemas/audit-result/v1.registry.json",
        "tests/fixtures/audit-result/v1/payloads/empty-results.json",
        "tests/fixtures/audit-result/v1/payloads/required-present.json",
        "tests/fixtures/audit-result/v1/payloads/required-absent.json",
        "tests/fixtures/audit-result/v1/payloads/classification-skipped.json",
        "tests/fixtures/audit-result/v1/payloads/policy-unavailable.json",
        "tests/fixtures/audit-result/v1/payloads/documentation-unavailable.json",
        "tests/fixtures/audit-result/v1/payloads/evidence-incomplete.json",
        "tests/fixtures/audit-result/v1/payloads/policy-conflict.json",
        "tests/ContractScribe.Tests/AuditResultConformance.cs"
    ];

    private static readonly string[] ExpectedTerminalLayers = ["usage", "preflight", "execution", "audit", "host-contract-error"];
    private static readonly string[] ExpectedUsageClasses = ["unknown-option", "missing-required-option", "duplicate-option", "missing-option-value", "invalid-option-value", "unexpected-operand", "forbidden-combination"];
    private static readonly string[] ExpectedExecutionClasses = ["invalid-input", "environment-unavailable", "load-failure", "audit-error", "publication-failure", "cancelled", "timeout"];
    private static readonly string[] ExpectedDispositions = ["compliant", "compliant-with-skipped", "violations", "violations-with-skipped", "skipped-only", "no-results"];
    private static readonly string[] ExpectedCliCodes =
    [
        "cli.usage.unknown-command",
        "cli.usage.unknown-option",
        "cli.usage.missing-required-option",
        "cli.usage.duplicate-option",
        "cli.usage.missing-option-value",
        "cli.usage.invalid-option-value",
        "cli.usage.unexpected-operand",
        "cli.usage.forbidden-combination",
        "cli.preflight.repository-root",
        "cli.preflight.input",
        "cli.preflight.input-escape",
        "cli.preflight.policy",
        "cli.preflight.policy-escape",
        "cli.preflight.output-parent",
        "cli.preflight.output-inside-root",
        "cli.preflight.output-reparse",
        "cli.audit.skipped-summary",
        "cli.cancel.requested",
        "cli.host.unknown-terminal"
    ];

    private static readonly string[] CommonEnvelopeFields = ["envelopeVersion", "terminalLayer", "cliContractBaseline", "toolVersion", "diagnosticCodes"];
    private static readonly string[] HostProvenanceFields = ["terminalState", "auditContractBaseline", "sourceRevision", "toolchain", "executionClass", "disposition", "counts", "resultDigest", "outputCommit"];

    private static readonly IReadOnlyDictionary<string, string[]> EnvelopeFieldOrder = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["usage"] = ["envelopeVersion", "terminalLayer", "cliContractBaseline", "toolVersion", "diagnosticCodes", "usageClass"],
        ["preflight"] = ["envelopeVersion", "terminalLayer", "cliContractBaseline", "toolVersion", "diagnosticCodes", "executionClass"],
        ["execution"] = ["envelopeVersion", "terminalLayer", "cliContractBaseline", "toolVersion", "diagnosticCodes", "terminalState", "auditContractBaseline", "sourceRevision", "toolchain", "executionClass"],
        ["audit"] = ["envelopeVersion", "terminalLayer", "cliContractBaseline", "toolVersion", "diagnosticCodes", "terminalState", "auditContractBaseline", "sourceRevision", "toolchain", "disposition", "counts", "resultDigest", "outputCommit"],
        ["host-contract-error"] = ["envelopeVersion", "terminalLayer", "cliContractBaseline", "toolVersion", "diagnosticCodes"]
    };

    private static readonly IReadOnlyDictionary<string, string[]> PermittedTokens = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["usage"] = ["${CLI_CONTRACT_BASELINE}", "${TOOL_VERSION}"],
        ["preflight"] = ["${CLI_CONTRACT_BASELINE}", "${TOOL_VERSION}"],
        ["host-contract-error"] = ["${CLI_CONTRACT_BASELINE}", "${TOOL_VERSION}"],
        ["execution"] = ["${CLI_CONTRACT_BASELINE}", "${TOOL_VERSION}", "${AUDIT_CONTRACT_BASELINE}", "${SOURCE_REVISION}", "${TOOLCHAIN_IDENTITY}"],
        ["audit"] = ["${CLI_CONTRACT_BASELINE}", "${TOOL_VERSION}", "${AUDIT_CONTRACT_BASELINE}", "${SOURCE_REVISION}", "${TOOLCHAIN_IDENTITY}", "${RESULT_DIGEST}", "${OUTPUT_COMMIT_IDENTITY}"]
    };

    private static readonly IReadOnlyDictionary<string, int?> ExpectedExitCodes = new Dictionary<string, int?>(StringComparer.Ordinal)
    {
        ["compliant"] = 0,
        ["compliant-with-skipped"] = 0,
        ["help"] = 0,
        ["version"] = 0,
        ["doctor"] = 0,
        ["violations"] = 1,
        ["violations-with-skipped"] = 1,
        ["top-level-usage-failure"] = 2,
        ["audit-usage-failure"] = 2,
        ["no-results"] = 3,
        ["skipped-only"] = 3,
        ["invalid-input"] = 4,
        ["environment-unavailable"] = 4,
        ["load-failure"] = 5,
        ["audit-error"] = 5,
        ["publication-failure"] = 5,
        ["host-contract-error"] = 5,
        ["cancelled"] = 6,
        ["timeout"] = 7,
        ["process.pre-entry"] = null,
        ["process.pre-commit-crash"] = null,
        ["process.post-commit-abnormal"] = null
    };

    private static readonly IReadOnlyDictionary<string, string[]> ExecutionToolchainStates = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["invalid-input"] = ["not-selected"],
        ["environment-unavailable"] = ["not-selected"],
        ["load-failure"] = ["selected"],
        ["audit-error"] = ["selected"],
        ["publication-failure"] = ["selected", "not-selected"],
        ["cancelled"] = ["selected", "not-selected"],
        ["timeout"] = ["selected", "not-selected"]
    };

    private static readonly string[][] StagePrecedenceOrders =
    [
        ["forbidden-combination", "unknown-option", "duplicate-option", "missing-option-value", "invalid-option-value", "unexpected-operand", "missing-required-option"],
        ["input-escape", "input-nonexistence", "input-not-regular-file", "input-unsupported-extension"],
        ["policy-escape", "policy-nonexistence"],
        ["output-inside-root", "output-missing-parent", "output-final-reparse"]
    ];

    private static readonly IReadOnlyDictionary<string, string> FaultCodes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["forbidden-combination"] = "cli.usage.forbidden-combination",
        ["unknown-option"] = "cli.usage.unknown-option",
        ["duplicate-option"] = "cli.usage.duplicate-option",
        ["missing-option-value"] = "cli.usage.missing-option-value",
        ["invalid-option-value"] = "cli.usage.invalid-option-value",
        ["unexpected-operand"] = "cli.usage.unexpected-operand",
        ["missing-required-option"] = "cli.usage.missing-required-option",
        ["input-escape"] = "cli.preflight.input-escape",
        ["input-nonexistence"] = "cli.preflight.input",
        ["input-not-regular-file"] = "cli.preflight.input",
        ["input-unsupported-extension"] = "cli.preflight.input",
        ["policy-escape"] = "cli.preflight.policy-escape",
        ["policy-nonexistence"] = "cli.preflight.policy",
        ["output-inside-root"] = "cli.preflight.output-inside-root",
        ["output-missing-parent"] = "cli.preflight.output-parent",
        ["output-final-reparse"] = "cli.preflight.output-reparse"
    };

    [Fact]
    public void Annex_IsStrictPublicSafeAndPinnedToBaseline()
    {
        var bytes = File.ReadAllBytes(AnnexPath);
        AssertNoDuplicateProperties(bytes);
        using var annex = JsonDocument.Parse(bytes);
        var root = annex.RootElement;

        Assert.Equal("contract-scribe.audit-cli/v1", root.GetProperty("contractId").GetString());
        var meta = root.GetProperty("meta");
        Assert.Equal(BaselineCommit, meta.GetProperty("baselineCommit").GetString());
        Assert.Equal(1, meta.GetProperty("envelopeVersion").GetInt32());

        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotMatch(@"(?i)([a-z]:\\users\\|/users/[^/]+/|password\s*[=:]|access[_-]?token\s*[=:]|api[_-]?key\s*[=:]|client[_-]?secret\s*[=:]|-----begin [a-z ]+private key-----)", text);
        Assert.DoesNotContain("file://", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", text, StringComparison.OrdinalIgnoreCase);

        var maxStringLength = root.GetProperty("publicSafetyConstraints").GetProperty("maxStringLength").GetInt32();
        var declaredTokens = Strings(root.GetProperty("publicSafetyConstraints"), "pathTemplates")
            .Concat(Strings(root.GetProperty("publicSafetyConstraints"), "envelopeTokens"))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var value in AllStrings(root))
        {
            Assert.True(value.Length <= maxStringLength, $"Unbounded annex string: {value[..Math.Min(value.Length, 40)]}");
            foreach (var token in System.Text.RegularExpressions.Regex.Matches(value, @"\$\{[^}]*\}").Select(match => match.Value))
            {
                Assert.Contains(token, declaredTokens);
            }
        }

        var binding = meta.GetProperty("oracleBindings").EnumerateArray().Single();
        Assert.Equal("tests/ContractScribe.Tests/AuditResultConformance.cs", binding.GetProperty("path").GetString());
        Assert.Equal("shared-usage", binding.GetProperty("binding").GetString());
        Assert.True(File.Exists(Path.Join(Root, "tests", "ContractScribe.Tests", "AuditResultConformance.cs")));
        Assert.Contains("#35", meta.GetProperty("reconciliationGate").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedInputs_MatchCurrentFilesExactly()
    {
        using var annex = LoadAnnex();
        var pins = annex.RootElement.GetProperty("meta").GetProperty("protectedInputs");
        var paths = pins.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(ExpectedProtectedInputs.Order(StringComparer.Ordinal), paths.Order(StringComparer.Ordinal));
        foreach (var property in pins.EnumerateObject())
        {
            var path = Path.Join(Root, property.Name.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Missing protected input: {property.Name}");
            Assert.Equal(property.Value.GetString(), Sha256(path));
        }
    }

    [Fact]
    public void HelpFixtures_MatchDeclaredDigestsAndAreEmbeddedInDoc()
    {
        using var annex = LoadAnnex();
        var doc = File.ReadAllText(DocPath);
        var fixtureCases = annex.RootElement.GetProperty("helpCases").EnumerateArray()
            .Where(row => row.TryGetProperty("stdoutFixture", out _))
            .ToArray();
        Assert.Equal(2, fixtureCases.Length);
        foreach (var row in fixtureCases)
        {
            var fixture = row.GetProperty("stdoutFixture");
            var relative = fixture.GetProperty("path").GetString()!;
            var bytes = File.ReadAllBytes(Path.Join(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(fixture.GetProperty("sha256").GetString(), Sha256(bytes));
            Assert.NotEqual((byte)0xEF, bytes[0]);
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.Equal((byte)'\n', bytes[^1]);
            Assert.NotEqual((byte)'\n', bytes[^2]);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.All(text.Split('\n'), line => Assert.False(line.EndsWith(' ') || line.EndsWith('\t'), "Trailing whitespace in help fixture."));
            Assert.Contains(text + "```", doc, StringComparison.Ordinal);
        }

        var version = annex.RootElement.GetProperty("helpCases").EnumerateArray().Single(row => row.GetProperty("caseId").GetString() == "help.version");
        Assert.Equal("ContractScribe ${TOOL_VERSION}\n", version.GetProperty("stdoutTemplate").GetString());
        Assert.Contains("ContractScribe ${TOOL_VERSION}", doc, StringComparison.Ordinal);

        var doctor = annex.RootElement.GetProperty("helpCases").EnumerateArray().Single(row => row.GetProperty("caseId").GetString() == "help.doctor");
        Assert.Equal(
            ["application_version", "runtime_description", "process_architecture", "runtime_identifier", "network_access", "credential_access"],
            Strings(doctor, "stdoutKeyOrder"));
        Assert.Equal("not performed", doctor.GetProperty("stdoutFixedValues").GetProperty("network_access").GetString());
        Assert.Equal("not performed", doctor.GetProperty("stdoutFixedValues").GetProperty("credential_access").GetString());
        foreach (var key in Strings(doctor, "stdoutKeyOrder"))
        {
            Assert.Contains(key, doc, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ClosedVocabularies_AreExactAndPresentInDoc()
    {
        using var annex = LoadAnnex();
        var vocabularies = annex.RootElement.GetProperty("vocabularies");
        Assert.Equal(ExpectedTerminalLayers, Strings(vocabularies, "terminalLayers"));
        Assert.Equal(ExpectedUsageClasses, Strings(vocabularies, "usageClasses"));
        Assert.Equal(ExpectedExecutionClasses, Strings(vocabularies, "executionClasses"));
        Assert.Equal(ExpectedDispositions, Strings(vocabularies, "dispositions"));
        Assert.Equal(ExpectedCliCodes, Strings(vocabularies, "cliDiagnosticCodes"));

        var doc = File.ReadAllText(DocPath);
        foreach (var code in ExpectedCliCodes)
        {
            Assert.Contains("`" + code + "`", doc, StringComparison.Ordinal);
        }
        foreach (var name in ExpectedTerminalLayers.Concat(ExpectedUsageClasses).Concat(ExpectedExecutionClasses).Concat(ExpectedDispositions))
        {
            Assert.Contains("`" + name + "`", doc, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GrammarTables_AreClosedConsistentAndCovered()
    {
        using var annex = LoadAnnex();
        var root = annex.RootElement;
        var doc = File.ReadAllText(DocPath);

        Assert.Equal(["--repository-root", "--input", "--policy", "--output"], root.GetProperty("options").EnumerateArray().Select(row => row.GetProperty("name").GetString()!));
        foreach (var option in root.GetProperty("options").EnumerateArray())
        {
            Assert.True(option.GetProperty("required").GetBoolean());
            Assert.Equal(1, option.GetProperty("valueCount").GetInt32());
            Assert.True(option.GetProperty("caseSensitive").GetBoolean());
            Assert.Equal(["space", "equals"], Strings(option, "forms"));
            Assert.Contains(option.GetProperty("name").GetString()!, doc, StringComparison.Ordinal);
        }

        var parseCases = root.GetProperty("parseCases").EnumerateArray().ToArray();
        AssertUniqueCaseIds(root);
        var exercisedUsageClasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in parseCases)
        {
            var outcome = row.GetProperty("outcome").GetString();
            Assert.Contains(outcome, new[] { "accepted", "usage-failure" });
            var hasCode = row.TryGetProperty("code", out var codeElement);
            var code = hasCode ? codeElement.GetString() : null;
            if (outcome == "accepted")
            {
                Assert.False(hasCode);
                var argv = Strings(row, "argv");
                Assert.Equal("audit", argv[0]);
                foreach (var option in new[] { "--repository-root", "--input", "--policy", "--output" })
                {
                    Assert.Contains(argv, argument => argument == option || argument.StartsWith(option + "=", StringComparison.Ordinal));
                }
                continue;
            }

            Assert.Contains(code, ExpectedCliCodes);
            var envelope = row.GetProperty("envelope").GetString();
            if (envelope == "usage")
            {
                var usageClass = row.GetProperty("usageClass").GetString()!;
                Assert.Contains(usageClass, ExpectedUsageClasses);
                Assert.Equal("cli.usage." + usageClass, code);
                exercisedUsageClasses.Add(usageClass);
            }
            else
            {
                Assert.Equal("none", envelope);
                Assert.False(row.TryGetProperty("usageClass", out _));
            }
        }

        Assert.Equal(ExpectedUsageClasses.Order(StringComparer.Ordinal), exercisedUsageClasses.Order(StringComparer.Ordinal));

        var commands = root.GetProperty("commands").EnumerateArray().ToArray();
        Assert.Equal(8, commands.Length);
        foreach (var command in commands)
        {
            Assert.Equal(0, command.GetProperty("exitCode").GetInt32());
            Assert.Equal("empty", command.GetProperty("stderr").GetString());
        }

        var pathCases = root.GetProperty("pathCases").EnumerateArray().ToArray();
        foreach (var row in pathCases)
        {
            Assert.Contains(row.GetProperty("platformScope").GetString(), new[] { "all", "windows", "unix" });
            var outcome = row.GetProperty("outcome").GetString();
            Assert.Contains(outcome, new[] { "accepted", "preflight-failure" });
            if (outcome == "preflight-failure")
            {
                Assert.StartsWith("cli.preflight.", row.GetProperty("code").GetString());
            }
        }
    }

    [Fact]
    public void ClassificationTruthTable_IsTotalExclusiveAndMatchesDoc()
    {
        using var annex = LoadAnnex();
        var rows = annex.RootElement.GetProperty("classificationCases").EnumerateArray().ToArray();
        Assert.Equal(8, rows.Length);
        var doc = File.ReadAllText(DocPath);
        var mixtures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var mixture = row.GetProperty("mixture");
            var violation = mixture.GetProperty("violation").GetBoolean();
            var compliant = mixture.GetProperty("compliant").GetBoolean();
            var skipped = mixture.GetProperty("skipped").GetBoolean();
            Assert.True(mixtures.Add($"{violation}:{compliant}:{skipped}"));
            var (disposition, exitCode) = Classify(violation, compliant, skipped);
            Assert.Equal(disposition, row.GetProperty("disposition").GetString());
            Assert.Equal(exitCode, row.GetProperty("exitCode").GetInt32());
            Assert.Contains("`" + disposition + "` | " + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + " |", doc, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ClassificationPayloads_PassSharedOracleAndMatchDeclaredMixtures()
    {
        using var annex = LoadAnnex();
        var root = annex.RootElement;
        foreach (var row in root.GetProperty("classificationCases").EnumerateArray())
        {
            using var payload = LoadPinnedPayload(row.GetProperty("payload"));
            var counts = CountOutcomes(payload.RootElement);
            var declared = row.GetProperty("counts");
            Assert.Equal(declared.GetProperty("compliant").GetInt32(), counts.Compliant);
            Assert.Equal(declared.GetProperty("violation").GetInt32(), counts.Violation);
            Assert.Equal(declared.GetProperty("skipped").GetInt32(), counts.Skipped);
            var mixture = row.GetProperty("mixture");
            Assert.Equal(counts.Violation > 0, mixture.GetProperty("violation").GetBoolean());
            Assert.Equal(counts.Compliant > 0, mixture.GetProperty("compliant").GetBoolean());
            Assert.Equal(counts.Skipped > 0, mixture.GetProperty("skipped").GetBoolean());
        }

        var reasonRows = root.GetProperty("skippedReasonCases").EnumerateArray().ToArray();
        Assert.Equal(5, reasonRows.Length);
        var declaredReasonSets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in reasonRows)
        {
            Assert.Equal("skipped-only", row.GetProperty("disposition").GetString());
            Assert.Equal(3, row.GetProperty("exitCode").GetInt32());
            using var payload = LoadPinnedPayload(row.GetProperty("payload"));
            var results = payload.RootElement.GetProperty("results").EnumerateArray().ToArray();
            Assert.NotEmpty(results);
            Assert.All(results, result => Assert.Equal("audit.outcome.skipped", result.GetProperty("auditOutcome").GetString()));
            var reasons = results.Select(result => result.GetProperty("reasonCode").GetString()!).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(reasons, Strings(row, "reasonCodes").Order(StringComparer.Ordinal));
            Assert.True(declaredReasonSets.Add(string.Join("|", reasons)));
        }

        Assert.All(reasonRows, row => Assert.Equal(reasonRows[0].GetProperty("disposition").GetString(), row.GetProperty("disposition").GetString()));
    }

    [Fact]
    public void ExitCodes_MapEveryControlledClassWithNoUndeclaredSharing()
    {
        using var annex = LoadAnnex();
        var rows = annex.RootElement.GetProperty("exitCodeCases").EnumerateArray().ToArray();
        var mapping = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var controlledClass = row.GetProperty("controlledClass").GetString()!;
            var exitCode = row.GetProperty("exitCode").ValueKind == JsonValueKind.Null ? (int?)null : row.GetProperty("exitCode").GetInt32();
            if (mapping.TryGetValue(controlledClass, out var existing))
            {
                Assert.Equal(existing, exitCode);
                continue;
            }
            mapping[controlledClass] = exitCode;
            if (exitCode is not null)
            {
                Assert.InRange(exitCode.Value, 0, 7);
            }
        }

        Assert.Equal(ExpectedExitCodes.OrderBy(pair => pair.Key, StringComparer.Ordinal), mapping.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.All(ExpectedDispositions.Concat(ExpectedExecutionClasses), name => Assert.True(mapping.ContainsKey(name), $"Missing exit-code row: {name}"));

        var doc = File.ReadAllText(DocPath);
        var namedClasses = ExpectedDispositions.Concat(ExpectedExecutionClasses).Concat(["host-contract-error"]);
        foreach (var pair in mapping)
        {
            if (pair.Value is null || !namedClasses.Contains(pair.Key, StringComparer.Ordinal))
            {
                continue;
            }
            var row = doc.Split('\n').Single(line => line.StartsWith("| " + pair.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + " |", StringComparison.Ordinal));
            Assert.Contains("`" + pair.Key + "`", row, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EnvelopeTemplates_FollowClosedFieldOrdersAndSubstitutionGrammar()
    {
        using var annex = LoadAnnex();
        var doc = File.ReadAllText(DocPath);
        var rows = annex.RootElement.GetProperty("streamCases").EnumerateArray().Where(row => row.GetProperty("variant").GetString() != "none").ToArray();
        Assert.Equal(19, rows.Length);
        var cliCodes = ExpectedCliCodes.Concat(["<verbatim-host-code>"]).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var variant = row.GetProperty("variant").GetString()!;
            var template = row.GetProperty("stdoutTemplate").GetString()!;
            Assert.EndsWith("\n", template);
            Assert.DoesNotContain("\r", template, StringComparison.Ordinal);
            if (row.TryGetProperty("representative", out var representative) && representative.GetBoolean())
            {
                Assert.Contains(template + "```", doc, StringComparison.Ordinal);
            }

            var tokens = System.Text.RegularExpressions.Regex.Matches(template, @"\$\{[A-Z_]+\}").Select(match => match.Value).ToArray();
            Assert.Equal(tokens.Length, tokens.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(Strings(row, "tokens"), tokens);
            Assert.All(tokens, token => Assert.Contains(token, PermittedTokens[variant]));

            var substituted = tokens.Aggregate(template, (current, token) => current.Replace(token, "x", StringComparison.Ordinal));
            using var envelope = AuditResultConformance.ParseStrict(Encoding.UTF8.GetBytes(substituted));
            var fields = envelope.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            var expectedOrder = EnvelopeFieldOrder[variant];
            if (!fields.Contains("toolchain", StringComparer.Ordinal))
            {
                expectedOrder = expectedOrder.Where(field => field != "toolchain").ToArray();
            }
            Assert.Equal(expectedOrder, fields);
            Assert.Equal(1, envelope.RootElement.GetProperty("envelopeVersion").GetInt32());
            Assert.Equal(variant, envelope.RootElement.GetProperty("terminalLayer").GetString());
            if (variant == "execution")
            {
                var toolchainState = row.GetProperty("toolchainState").GetString()!;
                Assert.Contains(toolchainState, new[] { "selected", "not-selected" });
                var executionClass = envelope.RootElement.GetProperty("executionClass").GetString()!;
                Assert.Contains(toolchainState, ExecutionToolchainStates[executionClass]);
                Assert.Equal(toolchainState == "selected", template.Contains("\"toolchain\"", StringComparison.Ordinal));
                Assert.Equal(toolchainState == "selected", tokens.Contains("${TOOLCHAIN_IDENTITY}", StringComparer.Ordinal));
            }
            else
            {
                Assert.False(row.TryGetProperty("toolchainState", out _));
            }
            foreach (var code in envelope.RootElement.GetProperty("diagnosticCodes").EnumerateArray())
            {
                Assert.Contains(code.GetString()!, cliCodes);
            }

            var forbidden = variant switch
            {
                "usage" => HostProvenanceFields,
                "preflight" => HostProvenanceFields.Where(field => field != "executionClass").Concat(["usageClass"]).ToArray(),
                "execution" => new[] { "usageClass", "disposition", "counts", "resultDigest", "outputCommit" },
                "audit" => new[] { "usageClass", "executionClass" },
                _ => HostProvenanceFields.Concat(["usageClass"]).ToArray()
            };
            Assert.Empty(fields.Intersect(forbidden, StringComparer.Ordinal));
            if (variant == "host-contract-error")
            {
                Assert.Equal(CommonEnvelopeFields, fields);
                Assert.Equal("cli.host.unknown-terminal", envelope.RootElement.GetProperty("diagnosticCodes")[0].GetString());
            }
            if (variant == "audit")
            {
                Assert.Equal(["compliant", "violation", "skipped"], envelope.RootElement.GetProperty("counts").EnumerateObject().Select(property => property.Name));
            }
        }

        var environmentUnavailable = rows.Single(row => row.GetProperty("caseId").GetString() == "stream.execution.environment-unavailable");
        Assert.DoesNotContain("toolchain", environmentUnavailable.GetProperty("stdoutTemplate").GetString()!, StringComparison.Ordinal);
        Assert.Contains(rows, row =>
        {
            using var envelope = AuditResultConformance.ParseStrict(Encoding.UTF8.GetBytes(SubstituteTokens(row.GetProperty("stdoutTemplate").GetString()!)));
            return row.GetProperty("variant").GetString() == "audit" && envelope.RootElement.GetProperty("diagnosticCodes").GetArrayLength() == 0;
        });
    }

    [Fact]
    public void EnvelopeCoverage_MapsEveryControlledClassToExactlyOneStreamForm()
    {
        using var annex = LoadAnnex();
        var root = annex.RootElement;
        var claims = root.GetProperty("streamCases").EnumerateArray()
            .Where(row => row.TryGetProperty("controlledClasses", out _))
            .SelectMany(row => Strings(row, "controlledClasses"))
            .ToArray();
        Assert.Equal(claims.Length, claims.Distinct(StringComparer.Ordinal).Count());

        var expected = new List<string>();
        foreach (var row in root.GetProperty("exitCodeCases").EnumerateArray())
        {
            var layer = row.GetProperty("layer").GetString()!;
            if (layer is "retained" or "process")
            {
                continue;
            }
            var controlledClass = row.GetProperty("controlledClass").GetString()!;
            if (layer == "execution")
            {
                foreach (var state in ExecutionToolchainStates[controlledClass])
                {
                    expected.Add("execution:" + controlledClass + ":" + state);
                }
                continue;
            }
            expected.Add(layer + ":" + controlledClass);
        }

        Assert.Equal(expected.Order(StringComparer.Ordinal), claims.Order(StringComparer.Ordinal));
        Assert.Equal(ExpectedExecutionClasses.Order(StringComparer.Ordinal), ExecutionToolchainStates.Keys.Order(StringComparer.Ordinal));
        foreach (var row in root.GetProperty("streamCases").EnumerateArray().Where(row => row.GetProperty("variant").GetString() == "execution"))
        {
            var claim = Assert.Single(Strings(row, "controlledClasses"));
            var parts = claim.Split(':');
            Assert.Equal(3, parts.Length);
            Assert.Equal("execution", parts[0]);
            Assert.Equal(row.GetProperty("toolchainState").GetString(), parts[2]);
            Assert.Contains(parts[2], ExecutionToolchainStates[parts[1]]);
        }
        Assert.DoesNotContain(claims, claim => claim.StartsWith("process:", StringComparison.Ordinal));

        var helpCaseIds = root.GetProperty("helpCases").EnumerateArray().Select(row => row.GetProperty("caseId").GetString()).ToArray();
        Assert.Contains("help.top-level", helpCaseIds);
        Assert.Contains("help.version", helpCaseIds);
        Assert.Contains("help.doctor", helpCaseIds);

        var auditClaims = claims.Where(claim => claim.StartsWith("audit:", StringComparison.Ordinal)).Select(claim => claim["audit:".Length..]).ToArray();
        Assert.Equal(ExpectedDispositions.Order(StringComparer.Ordinal), auditClaims.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PrecedenceCases_SelectFirstFailureCodeAndStreamForm()
    {
        using var annex = LoadAnnex();
        var doc = File.ReadAllText(DocPath);
        var rows = annex.RootElement.GetProperty("precedenceCases").EnumerateArray().ToArray();
        Assert.Equal(
            [
                "precedence.duplicate-option-vs-missing-required-option",
                "precedence.forbidden-combination-vs-unknown-option",
                "precedence.input-escape-vs-nonexistence",
                "precedence.input-escape-vs-unsupported-extension",
                "precedence.output-inside-root-vs-final-reparse",
                "precedence.output-inside-root-vs-missing-parent",
                "precedence.policy-escape-vs-nonexistence",
                "precedence.unknown-option-vs-duplicate-option"
            ],
            rows.Select(row => row.GetProperty("caseId").GetString()!).Order(StringComparer.Ordinal));
        Assert.Subset(ExpectedCliCodes.ToHashSet(StringComparer.Ordinal), FaultCodes.Values.ToHashSet(StringComparer.Ordinal));
        foreach (var row in rows)
        {
            Assert.NotEqual(row.TryGetProperty("argv", out _), row.TryGetProperty("argument", out _));
            var faults = Strings(row, "faults");
            Assert.True(faults.Length >= 2, $"Precedence row needs at least two faults: {row.GetProperty("caseId")}");
            Assert.Equal(faults.Length, faults.Distinct(StringComparer.Ordinal).Count());
            Assert.All(faults, fault => Assert.True(FaultCodes.ContainsKey(fault), $"Unknown fault: {fault}"));
            var order = Assert.Single(StagePrecedenceOrders, stage => faults.All(stage.Contains));
            var winner = faults.MinBy(fault => Array.IndexOf(order, fault))!;
            var selectedCode = row.GetProperty("selectedCode").GetString()!;
            Assert.Equal(FaultCodes[winner], selectedCode);
            var expectedForm = selectedCode.StartsWith("cli.usage.", StringComparison.Ordinal) ? "usage" : "preflight";
            Assert.Equal(expectedForm, row.GetProperty("streamForm").GetString());
        }

        foreach (var stage in StagePrecedenceOrders)
        {
            var chain = string.Join(" → ", stage.Select(fault => $"`{fault}`"));
            Assert.Contains(chain, doc, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StderrTemplates_MatchDocMessageTableAndDiagnosticCodes()
    {
        using var annex = LoadAnnex();
        var doc = File.ReadAllText(DocPath);
        var messageTemplates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in doc.Split('\n'))
        {
            if (!line.StartsWith("| `cli.", StringComparison.Ordinal))
            {
                continue;
            }
            var cells = line.Split('|');
            var code = cells[1].Trim().Trim('`');
            var message = cells[2].Trim().Replace("`", string.Empty, StringComparison.Ordinal);
            Assert.True(messageTemplates.TryAdd(code, message), $"Duplicate message template: {code}");
        }

        Assert.Equal(ExpectedCliCodes.Order(StringComparer.Ordinal), messageTemplates.Keys.Order(StringComparer.Ordinal));

        foreach (var row in annex.RootElement.GetProperty("streamCases").EnumerateArray())
        {
            var stderr = row.GetProperty("stderrTemplate").GetString()!;
            Assert.DoesNotContain("\r", stderr, StringComparison.Ordinal);
            Assert.True(stderr.Length == 0 || stderr.EndsWith("\n", StringComparison.Ordinal));
            var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string[] codes;
            if (row.TryGetProperty("stdoutTemplate", out var stdoutTemplate))
            {
                using var envelope = AuditResultConformance.ParseStrict(Encoding.UTF8.GetBytes(SubstituteTokens(stdoutTemplate.GetString()!)));
                codes = envelope.RootElement.GetProperty("diagnosticCodes").EnumerateArray().Select(code => code.GetString()!).ToArray();
            }
            else
            {
                codes = lines.Select(line => line.Split(": ", 2)[0]).ToArray();
            }

            Assert.Equal(codes.Length, lines.Length);
            foreach (var (code, line) in codes.Zip(lines))
            {
                var expectedMessage = code == "<verbatim-host-code>" ? "<bounded host message>" : messageTemplates[code];
                Assert.Equal(code + ": " + expectedMessage, line);
            }
        }
    }

    [Fact]
    public void LifecycleValidationAndCancellationRows_AreClosedAndComplete()
    {
        using var annex = LoadAnnex();
        var root = annex.RootElement;

        var lifecycle = root.GetProperty("terminalLifecycleCases").EnumerateArray().ToArray();
        Assert.Equal(
            [
                "lifecycle.abrupt-termination-during-invalidation",
                "lifecycle.cancellation-during-invalidation",
                "lifecycle.crash-before-any-commit",
                "lifecycle.invalidation-failure-while-alive",
                "lifecycle.non-success-commit-then-crash",
                "lifecycle.non-success-commit-then-stdout-failure",
                "lifecycle.orphan-staging",
                "lifecycle.pre-entry-managed-bootstrap-failure",
                "lifecycle.pre-entry-os-launch-failure",
                "lifecycle.success-commit-then-crash",
                "lifecycle.success-commit-then-stdout-failure"
            ],
            lifecycle.Select(row => row.GetProperty("caseId").GetString()!).Order(StringComparer.Ordinal));
        foreach (var row in lifecycle)
        {
            Assert.Contains(row.GetProperty("authoritative").GetString(), new[] { "committed-non-success-outcome", "committed-result", "platform-status-only" });
            Assert.Contains(row.GetProperty("envelope").GetString(), new[] { "may-never-exist", "not-authoritative", "none", "execution" });
            if (!row.TryGetProperty("invalidationCompleted", out var invalidationCompleted))
            {
                continue;
            }

            Assert.Contains(invalidationCompleted.GetString(), new[] { "completed", "not-completed", "may-be-partial" });
            Assert.Contains(row.GetProperty("priorArtifactState").GetString(), new[] { "may-remain-not-evidence", "may-be-removed-or-remain-not-evidence" });
            Assert.Contains(row.GetProperty("currentResultState").GetString(), new[] { "none", "none-readable" });
            Assert.Contains(row.GetProperty("terminalCommit").GetString(), new[] { "none", "publication-failure", "cancelled" });
            Assert.Contains(row.GetProperty("diagnosticOwnership").GetString(), new[] { "platform", "verbatim-host-code", "cli-cancel-requested-first" });
            if (row.GetProperty("exitCode").ValueKind != JsonValueKind.Null)
            {
                Assert.Contains(row.GetProperty("exitCode").GetInt32(), new[] { 5, 6 });
            }
        }

        var invalidationFailure = lifecycle.Single(row => row.GetProperty("caseId").GetString() == "lifecycle.invalidation-failure-while-alive");
        Assert.Equal("publication-failure", invalidationFailure.GetProperty("terminalCommit").GetString());
        Assert.Equal(5, invalidationFailure.GetProperty("exitCode").GetInt32());
        var cancellationDuringInvalidation = lifecycle.Single(row => row.GetProperty("caseId").GetString() == "lifecycle.cancellation-during-invalidation");
        Assert.Equal("cancelled", cancellationDuringInvalidation.GetProperty("terminalCommit").GetString());
        Assert.Equal(6, cancellationDuringInvalidation.GetProperty("exitCode").GetInt32());

        var resultValidation = root.GetProperty("resultValidationCases").EnumerateArray().ToArray();
        Assert.Equal(8, resultValidation.Length);
        foreach (var row in resultValidation)
        {
            Assert.Equal("audit-error", row.GetProperty("hostClass").GetString());
            Assert.Equal(5, row.GetProperty("exitCode").GetInt32());
        }

        var unsupportedVersion = resultValidation.Single(row => row.GetProperty("caseId").GetString() == "result.unsupported-artifact-version");
        var baselineMismatch = resultValidation.Single(row => row.GetProperty("caseId").GetString() == "result.baseline-mismatch");
        Assert.NotEqual(unsupportedVersion.GetProperty("mutation").GetString(), baselineMismatch.GetProperty("mutation").GetString());
        Assert.True(unsupportedVersion.GetProperty("canonicalMutation").GetBoolean());
        Assert.False(baselineMismatch.GetProperty("canonicalMutation").GetBoolean());

        var cancellation = root.GetProperty("cancellationCases").EnumerateArray().ToArray();
        Assert.Equal(12, cancellation.Length);
        foreach (var row in cancellation)
        {
            Assert.Contains(row.GetProperty("platformScope").GetString(), new[] { "all", "windows", "unix" });
            Assert.Contains(row.GetProperty("handling").GetString(), new[] { "cooperative", "unhandleable", "unregistered", "host-timeout", "none" });
            if (row.TryGetProperty("exitCode", out var exitCode))
            {
                Assert.Contains(exitCode.GetInt32(), new[] { 6, 7 });
            }
        }

        var preCommit = cancellation.Single(row => row.GetProperty("caseId").GetString() == "cancel.pre-commit");
        Assert.Equal("cli.cancel.requested", preCommit.GetProperty("firstRecord").GetString());
        Assert.Equal(6, preCommit.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void SyntheticPayloads_AreSelfContainedUnderTheM1FixtureDirectory()
    {
        using var annex = LoadAnnex();
        var synthetic = annex.RootElement.GetProperty("classificationCases").EnumerateArray()
            .Select(row => row.GetProperty("payload").GetProperty("path").GetString()!)
            .Where(path => path.StartsWith("tests/fixtures/m1-audit-cli/", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(4, synthetic.Length);
        Assert.Equal(
            Directory.EnumerateFiles(Path.Join(Root, "tests", "fixtures", "m1-audit-cli", "payloads"), "*.json", SearchOption.TopDirectoryOnly)
                .Select(path => "tests/fixtures/m1-audit-cli/payloads/" + Path.GetFileName(path))
                .Order(StringComparer.Ordinal),
            synthetic.Order(StringComparer.Ordinal));
    }

    private static string SubstituteTokens(string template)
        => System.Text.RegularExpressions.Regex.Replace(template, @"\$\{[A-Z_]+\}", "x");

    private static (string Disposition, int ExitCode) Classify(bool violation, bool compliant, bool skipped)
    {
        if (violation) return skipped ? ("violations-with-skipped", 1) : ("violations", 1);
        if (compliant) return skipped ? ("compliant-with-skipped", 0) : ("compliant", 0);
        return skipped ? ("skipped-only", 3) : ("no-results", 3);
    }

    private static JsonDocument LoadPinnedPayload(JsonElement payload)
    {
        var relative = payload.GetProperty("path").GetString()!;
        var bytes = File.ReadAllBytes(Path.Join(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(payload.GetProperty("sha256").GetString(), Sha256(bytes));
        using var document = AuditResultConformance.ParseStrict(bytes);
        Assert.True(AuditResultConformance.AuditSchema.Value.Evaluate(document.RootElement).IsValid, relative);
        Assert.True(AuditResultConformance.IsSemanticallyValid(document.RootElement), relative);
        return JsonDocument.Parse(bytes);
    }

    private static (int Compliant, int Violation, int Skipped) CountOutcomes(JsonElement document)
    {
        var compliant = 0;
        var violation = 0;
        var skipped = 0;
        foreach (var result in document.GetProperty("results").EnumerateArray())
        {
            switch (result.GetProperty("auditOutcome").GetString())
            {
                case "audit.outcome.compliant": compliant++; break;
                case "audit.outcome.violation": violation++; break;
                case "audit.outcome.skipped": skipped++; break;
                default: throw new InvalidOperationException("Unknown audit outcome.");
            }
        }
        return (compliant, violation, skipped);
    }

    private static void AssertUniqueCaseIds(JsonElement root)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in property.Value.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("caseId", out var caseId))
                {
                    Assert.True(ids.Add(caseId.GetString()!), $"Duplicate caseId: {caseId.GetString()}");
                }
            }
        }
    }

    private static IEnumerable<string> AllStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString()!;
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var value in AllStrings(item)) yield return value;
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var value in AllStrings(property.Value)) yield return value;
                }
                break;
        }
    }

    private static void AssertNoDuplicateProperties(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        var scopes = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                scopes.Push(new HashSet<string>(StringComparer.Ordinal));
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                scopes.Pop();
            }
            else if (reader.TokenType == JsonTokenType.PropertyName && !scopes.Peek().Add(reader.GetString()!))
            {
                throw new InvalidOperationException($"Duplicate JSON property: {reader.GetString()}");
            }
        }
    }

    private static JsonDocument LoadAnnex() => JsonDocument.Parse(File.ReadAllText(AnnexPath));

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string[] Strings(JsonElement element, string property)
        => element.GetProperty(property).EnumerateArray().Select(value => value.GetString()!).ToArray();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
