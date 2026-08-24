using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class CampaignPlanningTests
{
    [Fact]
    public void RequiredTargetAndComponents_CollapseToOneExecutableCompleteBlock()
    {
        var scenario = CreateScenario();
        var plan = CampaignPlanner.Plan(scenario.Input);

        var item = Assert.Single(plan.WorkItems);
        Assert.Equal(CampaignPlanningDispositionKind.Executable, item.Disposition.Kind);
        Assert.Equal(CampaignPlanningEditCapability.Insert, item.Disposition.EditCapability);
        Assert.Equal(3, item.ViolationCauses.Length);
        Assert.Equal(
            ["parameter/0", "return"],
            Assert.Single(item.Targets).ApplicableComponents.Select(component => component.Identity));
        Assert.Equal(1, plan.Summary.ExecutableWorkItems);
        Assert.Equal(0, plan.Summary.TerminalWorkItems);
        Assert.Equal(64, plan.ExecutionCommitment.Length);
        Assert.StartsWith("campaign-work.", item.WorkItemKey, StringComparison.Ordinal);
        Assert.DoesNotContain("public void", JsonSerializer.Serialize(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void OpaqueSnapshotCollision_ChangesExecutionCommitmentAndEveryKey()
    {
        var scenario = CreateScenario();
        var first = CampaignPlanner.Plan(scenario.Input);
        var changed = CampaignPlanner.Plan(scenario.Input with
        {
            Snapshot = new CampaignPlanningSnapshot(
                scenario.Input.Snapshot.CampaignLineage,
                "snapshot.second",
                scenario.Input.Snapshot.RepositoryCommitmentSha256,
                scenario.Input.Snapshot.InputCommitmentSha256,
                scenario.Input.Snapshot.PolicyAuthorityCommitmentSha256,
                scenario.Input.Snapshot.TargetProfile),
        });

        Assert.Equal(first.AuditDocumentSha256, changed.AuditDocumentSha256);
        Assert.NotEqual(first.ExecutionCommitment, changed.ExecutionCommitment);
        Assert.NotEqual(
            Assert.Single(first.WorkItems).WorkItemKey,
            Assert.Single(changed.WorkItems).WorkItemKey);
    }

    [Fact]
    public void KnownVector_MatchesExactCommitmentsAndKeys()
    {
        using var vector = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "tests",
            "fixtures",
            "campaign",
            "planning",
            "vectors.json")));
        var plan = CampaignPlanner.Plan(CreateScenario().Input);
        var root = vector.RootElement;

        Assert.Equal(
            CampaignPlanningVocabulary.PlanningContractRevision,
            root.GetProperty("contractRevision").GetString());
        Assert.Equal(root.GetProperty("auditResultSha256").GetString(), plan.AuditDocumentSha256);
        Assert.Equal(root.GetProperty("executionCommitment").GetString(), plan.ExecutionCommitment);
        Assert.Equal(
            root.GetProperty("workItemKeys").EnumerateArray().Select(value => value.GetString()),
            plan.WorkItems.Select(item => item.WorkItemKey));
    }

    [Theory]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf8)]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf8Bom)]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom)]
    [InlineData(DocumentationPatchRepositoryEncoding.Utf16BigEndianBom)]
    public void ExactSourceRepresentation_IsCommitted(
        DocumentationPatchRepositoryEncoding encoding)
    {
        var baseline = CreateScenario();
        var changed = ReplaceSource(
            baseline,
            encoding,
            new string('b', 64));

        var plan = CampaignPlanner.Plan(changed.Input);
        if (encoding == DocumentationPatchRepositoryEncoding.Utf8)
        {
            Assert.Equal(
                CampaignPlanner.Plan(baseline.Input).ExecutionCommitment,
                plan.ExecutionCommitment);
        }
        else
        {
            Assert.NotEqual(
                CampaignPlanner.Plan(baseline.Input).ExecutionCommitment,
                plan.ExecutionCommitment);
        }
    }

    [Fact]
    public void SameStyleIdentityWithChangedContent_ChangesCommitment()
    {
        var baseline = CreateScenario();
        var requestJson = ReadScribeRequestJson();
        requestJson["styleProfile"]!["summary"]!["maximumScalars"] = 401;
        var changedProfile = ParseScribeRequest(requestJson).StyleProfile;
        var changed = ReplaceStyle(baseline, changedProfile);

        Assert.NotEqual(
            CampaignPlanner.Plan(baseline.Input).ExecutionCommitment,
            CampaignPlanner.Plan(changed.Input).ExecutionCommitment);
    }

    [Fact]
    public void SameConfigurationIdentityWithChangedContent_ChangesCommitment()
    {
        var scenario = CreateScenario();
        var changed = scenario.Input with
        {
            ExecutionPolicy = scenario.Input.ExecutionPolicy with
            {
                ProposalContract = Content("proposal", "proposal-v2"),
            },
        };

        Assert.NotEqual(
            CampaignPlanner.Plan(scenario.Input).ExecutionCommitment,
            CampaignPlanner.Plan(changed).ExecutionCommitment);
    }

    [Fact]
    public void MissingOwnerAuthority_FailsClosedWithStableCode()
    {
        var scenario = CreateScenario();
        var invalid = scenario.Input with
        {
            OwnerAuthority = new CampaignPlanningOwnerAuthoritySet([]),
        };

        var failure = Assert.Throws<CampaignPlanningValidationException>(() =>
            CampaignPlanner.Plan(invalid));
        Assert.Equal(CampaignPlanningValidationCode.InvalidOwnerAuthority, failure.Code);
        Assert.DoesNotContain("public void", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StyleComponentAssociationMismatch_FailsClosed()
    {
        var scenario = CreateScenario();
        var requestJson = ReadScribeRequestJson();
        var targetComponents = requestJson["target"]!["applicableComponents"]!.AsArray();
        var styleComponents = requestJson["styleProfile"]!["componentPolicies"]!.AsArray();
        var evidenceReferences = requestJson["evidenceReferences"]!.AsArray();
        targetComponents.RemoveAt(0);
        styleComponents.RemoveAt(0);
        evidenceReferences.RemoveAt(0);
        var incomplete = ParseScribeRequest(requestJson).StyleProfile;

        var failure = Assert.Throws<CampaignPlanningValidationException>(() =>
            CampaignPlanner.Plan(ReplaceStyle(scenario, incomplete).Input));
        Assert.Equal(CampaignPlanningValidationCode.InvalidStyleAuthority, failure.Code);
    }

    [Fact]
    public void CultureAndInputPermutation_ProduceExactSameOutput()
    {
        var scenario = CreateScenario();
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var baseline = CampaignPlanner.Plan(scenario.Input);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var owner = Assert.Single(scenario.Input.OwnerAuthority.Owners);
            var permuted = scenario.Input with
            {
                OwnerAuthority = new CampaignPlanningOwnerAuthoritySet([
                    owner with { Targets = owner.Targets.Reverse().ToImmutableArray() },
                ]),
            };
            var replay = CampaignPlanner.Plan(permuted);

            Assert.Equal(baseline.ExecutionCommitment, replay.ExecutionCommitment);
            Assert.Equal(
                baseline.WorkItems.Select(item => item.WorkItemKey),
                replay.WorkItems.Select(item => item.WorkItemKey));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void FreshProcessesWithDifferentCultures_ProduceExactSameOutput()
    {
        var english = RunFreshProcessProbe("en-US");
        var turkish = RunFreshProcessProbe("tr-TR");

        Assert.Equal(english, turkish);
    }

    [Fact]
    public void FreshProcessProbe()
    {
        var outputPath = Environment.GetEnvironmentVariable("CONTRACTSCRIBE_CAMPAIGN_PROBE_OUTPUT");
        if (string.IsNullOrEmpty(outputPath))
        {
            return;
        }

        var cultureName = Environment.GetEnvironmentVariable("CONTRACTSCRIBE_CAMPAIGN_PROBE_CULTURE")
            ?? "en-US";
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
        var plan = CampaignPlanner.Plan(CreateScenario().Input);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(plan), new UTF8Encoding(false));
    }

    [Fact]
    public void EmptyViolationSet_ProducesValidEmptyPlanWithoutStyleProfile()
    {
        var scenario = CreateScenario(PolicyExpectation.Optional);
        var owner = Assert.Single(scenario.Input.OwnerAuthority.Owners);
        var noStyle = scenario.Input with
        {
            OwnerAuthority = new CampaignPlanningOwnerAuthoritySet([
                owner with
                {
                    Targets = owner.Targets.Select(target =>
                        target with { ExecutableStyleProfile = null }).ToImmutableArray(),
                },
            ]),
        };

        var plan = CampaignPlanner.Plan(noStyle);
        Assert.Empty(plan.WorkItems);
        Assert.Equal(0, plan.Summary.TotalWorkItems);
        Assert.Equal(64, plan.ExecutionCommitment.Length);
    }

    [Fact]
    public void MultipleTerminalCauses_AreOrderedAndUseFixedPrimaryReason()
    {
        var scenario = CreateScenario();
        var owner = Assert.Single(scenario.Input.OwnerAuthority.Owners);
        var target = Assert.Single(owner.Targets);
        var source = Assert.IsType<CampaignPlanningRepositorySourceAuthority>(target.Source);
        var terminalSource = new CampaignPlanningRepositorySourceAuthority(
            source.Path,
            source.ContentSha256,
            source.Encoding,
            source.RequestedDeclarationSpan,
            source.CanonicalDeclarationSpan,
            source.OwnerSpan,
            source.DocumentationSpan,
            source.BlockState,
            writable: false);
        var input = scenario.Input with
        {
            OwnerAuthority = new CampaignPlanningOwnerAuthoritySet([
                owner with
                {
                    AmbiguousOwner = true,
                    Targets = [target with { Source = terminalSource, MultiDeclarator = true }],
                },
            ]),
        };

        var item = Assert.Single(CampaignPlanner.Plan(input).WorkItems);
        Assert.Equal(CampaignPlanningDispositionKind.Terminal, item.Disposition.Kind);
        Assert.Equal(CampaignPlanningTerminalReason.AmbiguousOwner, item.Disposition.PrimaryTerminalReason);
        Assert.True(item.Disposition.TerminalReasons.SequenceEqual(
            [
                CampaignPlanningTerminalReason.AmbiguousOwner,
                CampaignPlanningTerminalReason.MultiDeclarator,
                CampaignPlanningTerminalReason.NonWritableSource,
            ]));
        Assert.Null(item.Disposition.EditCapability);
    }

    [Fact]
    public void MachineAbsoluteRepositoryPath_FailsClosedWithoutEchoingPath()
    {
        var scenario = CreateScenario();
        var owner = Assert.Single(scenario.Input.OwnerAuthority.Owners);
        var target = Assert.Single(owner.Targets);
        var source = Assert.IsType<CampaignPlanningRepositorySourceAuthority>(target.Source);
        const string SecretPath = "C:\\private\\Synthetic.cs";
        var invalidSource = new CampaignPlanningRepositorySourceAuthority(
            SecretPath,
            source.ContentSha256,
            source.Encoding,
            source.RequestedDeclarationSpan,
            source.CanonicalDeclarationSpan,
            source.OwnerSpan,
            source.DocumentationSpan,
            source.BlockState);

        var failure = Assert.Throws<CampaignPlanningValidationException>(() =>
            CampaignPlanner.Plan(scenario.Input with
            {
                OwnerAuthority = new CampaignPlanningOwnerAuthoritySet([
                    owner with { Targets = [target with { Source = invalidSource }] },
                ]),
            }));
        Assert.Equal(CampaignPlanningValidationCode.InvalidOwnerAuthority, failure.Code);
        Assert.DoesNotContain(SecretPath, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanningNamespace_HasNoInfrastructureDependencySurface()
    {
        var root = RepositoryRoot();
        var sources = Directory.GetFiles(
                Path.Combine(root, "src", "ContractScribe.Core", "Campaign", "Planning"),
                "*.cs",
                SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();

        Assert.DoesNotContain(sources, source => source.Contains("Microsoft.CodeAnalysis", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Contains("ContractScribe.Agent", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Contains("ContractScribe.Patching", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Contains("System.IO", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Contains("System.Diagnostics", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Contains("GitHub", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Contains("Credential", StringComparison.Ordinal));
    }

    private static Scenario ReplaceStyle(
        Scenario scenario,
        DocumentationScribeStyleProfile style)
    {
        var owner = Assert.Single(scenario.Input.OwnerAuthority.Owners);
        var target = Assert.Single(owner.Targets);
        return scenario with
        {
            Input = scenario.Input with
            {
                OwnerAuthority = new CampaignPlanningOwnerAuthoritySet([
                    owner with
                    {
                        Targets = [target with { ExecutableStyleProfile = style }],
                    },
                ]),
            },
        };
    }

    private static string RunFreshProcessProbe(string cultureName)
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"contract-scribe-campaign-probe-{Guid.NewGuid():N}.json");
        try
        {
            var assemblyPath = typeof(CampaignPlanningTests).Assembly.Location;
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("vstest");
            process.StartInfo.ArgumentList.Add(assemblyPath);
            process.StartInfo.ArgumentList.Add(
                "--Tests:ContractScribe.Tests.CampaignPlanningTests.FreshProcessProbe");
            process.StartInfo.Environment["CONTRACTSCRIBE_CAMPAIGN_PROBE_OUTPUT"] = outputPath;
            process.StartInfo.Environment["CONTRACTSCRIBE_CAMPAIGN_PROBE_CULTURE"] = cultureName;

            Assert.True(process.Start(), "The fresh-process campaign probe did not start.");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "The fresh-process campaign probe timed out.");
            Assert.True(
                process.ExitCode == 0,
                $"The fresh-process campaign probe failed.{Environment.NewLine}{standardOutput}{Environment.NewLine}{standardError}");
            return File.ReadAllText(outputPath, Encoding.UTF8);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    private static Scenario ReplaceSource(
        Scenario scenario,
        DocumentationPatchRepositoryEncoding encoding,
        string sha256)
    {
        var owner = Assert.Single(scenario.Input.OwnerAuthority.Owners);
        var target = Assert.Single(owner.Targets);
        var source = Assert.IsType<CampaignPlanningRepositorySourceAuthority>(target.Source);
        var changedSource = new CampaignPlanningRepositorySourceAuthority(
            source.Path,
            sha256,
            encoding,
            source.RequestedDeclarationSpan,
            source.CanonicalDeclarationSpan,
            source.OwnerSpan,
            source.DocumentationSpan,
            source.BlockState,
            source.Writable);
        return scenario with
        {
            Input = scenario.Input with
            {
                OwnerAuthority = new CampaignPlanningOwnerAuthoritySet([
                    owner with { Targets = [target with { Source = changedSource }] },
                ]),
            },
        };
    }

    private static Scenario CreateScenario(
        PolicyExpectation expectation = PolicyExpectation.Required)
    {
        const string Context = "synthetic.v1";
        const string DocumentationId = "M:Synthetic.Widget.Run(System.String)";
        const string Path = "src/Synthetic/Widget.cs";
        const string Declaration = "public void Run(string value) { }";
        const string DeclarationId = "decl.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        var classificationsBuffer = new ClassificationCandidateBuffer();
        classificationsBuffer.AddTarget(
            Context,
            DocumentationId,
            PrimarySymbolKind.Method,
            ImmutableArray<SymbolTrait>.Empty,
            ClassificationOrigin.Source,
            [ClassificationInput.RepositoryLocator(Path, 0, Declaration.Length)]);
        classificationsBuffer.AddComponent(
            Context,
            DocumentationId,
            ComponentKind.Parameter,
            "parameter/0",
            ClassificationOrigin.Source);
        classificationsBuffer.AddComponent(
            Context,
            DocumentationId,
            ComponentKind.Return,
            "return",
            ClassificationOrigin.Source);
        var classifications = Assert.IsType<ClassificationSet>(
            classificationsBuffer.Normalize(TargetProfile.ExternalApi).ClassificationSet);
        var target = Assert.Single(classifications.Targets);
        var components = classifications.Components;

        var targetDeclaration = DocumentationObservationInput.RepositoryDeclaration(
            DeclarationId,
            DocumentationAuthorityRole.Ordinary,
            "project.synthetic",
            Path,
            Sha256(Declaration),
            DocumentationObservationInput.Span(0, Declaration.Length),
            Declaration,
            DocumentationObservationInput.Span(0, 0),
            string.Empty,
            null,
            null,
            DocumentationBlockState.NoBlock,
            parentSubstantive: false);
        var parameterDeclaration = DocumentationObservationInput.RepositoryDeclaration(
            DeclarationId,
            DocumentationAuthorityRole.Ordinary,
            "project.synthetic",
            Path,
            Sha256(Declaration),
            DocumentationObservationInput.Span(0, Declaration.Length),
            Declaration,
            DocumentationObservationInput.Span(0, 0),
            string.Empty,
            null,
            null,
            DocumentationBlockState.NoBlock,
            parentSubstantive: false,
            componentLocalName: "value",
            componentMatch: DocumentationComponentMatch.Absent);
        var returnDeclaration = DocumentationObservationInput.RepositoryDeclaration(
            DeclarationId,
            DocumentationAuthorityRole.Ordinary,
            "project.synthetic",
            Path,
            Sha256(Declaration),
            DocumentationObservationInput.Span(0, Declaration.Length),
            Declaration,
            DocumentationObservationInput.Span(0, 0),
            string.Empty,
            null,
            null,
            DocumentationBlockState.NoBlock,
            parentSubstantive: false,
            componentLocalName: null,
            componentMatch: DocumentationComponentMatch.Absent);
        var observationBuffer = new DocumentationObservationCandidateBuffer(classifications);
        observationBuffer.AddTarget(target, true, [targetDeclaration]);
        observationBuffer.AddComponent(components[0], true, [parameterDeclaration]);
        observationBuffer.AddComponent(components[1], true, [returnDeclaration]);
        var observations = Assert.IsType<DocumentationObservationSet>(
            observationBuffer.Normalize().ObservationSet);

        var policy = ParsePolicy(expectation);
        var contribution = Assert.IsType<PolicyContributionSet>(
            PolicyConfigurationEvaluator.Evaluate(
                policy,
                [PolicyConfigurationInput.Repository("src/Synthetic.csproj", Path)]).ContributionSet);
        var inputs = ImmutableArray.CreateBuilder<AuditRecordInput>();
        inputs.Add(AuditInput.Target(
            target,
            contribution,
            BindEvidence(
                observations.Observations.Single(value => value.Subject.ComponentKind is null),
                targetDeclaration,
                EvidenceInput.TargetSubject(Context, DocumentationId),
                    "evidence.declaration")));
        foreach (var component in components)
        {
            var declaration = component.ComponentKind == ComponentKind.Parameter
                ? parameterDeclaration
                : returnDeclaration;
            inputs.Add(AuditInput.Component(
                component,
                contribution,
                BindEvidence(
                    observations.Observations.Single(value =>
                        value.Subject.ComponentKind == component.ComponentKind),
                    declaration,
                    EvidenceInput.ComponentSubject(
                        Context,
                        DocumentationId,
                        component.ComponentKind,
                        component.Identity),
                    "evidence.declaration")));
        }

        var audit = AuditAggregator.Aggregate(
            TargetProfile.ExternalApi,
            classifications,
            policy,
            inputs);
        var request = ReadScribeRequest();
        var source = new CampaignPlanningRepositorySourceAuthority(
            Path,
            new string('b', 64),
            DocumentationPatchRepositoryEncoding.Utf8,
            DocumentationObservationInput.Span(0, Declaration.Length),
            DocumentationObservationInput.Span(0, Declaration.Length),
            DocumentationObservationInput.Span(0, Declaration.Length),
            null,
            DocumentationBlockState.NoBlock);
        var targetAuthority = new CampaignPlanningTargetAuthority(
            target,
            source,
            [
                new CampaignPlanningApplicableComponent(ComponentKind.Parameter, "parameter/0", "value"),
                new CampaignPlanningApplicableComponent(ComponentKind.Return, "return", null),
            ],
            [target.SymbolRef],
            multiDeclarator: false,
            primaryConstructor: false,
            primaryConstructorAlias: false,
            request.StyleProfile);
        var snapshot = new CampaignPlanningSnapshot(
            "campaign.synthetic",
            "snapshot.first",
            new string('1', 64),
            new string('2', 64),
            new string('3', 64),
            TargetProfile.ExternalApi);
        var executionPolicy = new CampaignPlanningExecutionPolicy(
            request.Limits,
            new CampaignPlanningBudgetPolicy(
                100,
                20,
                1_000_000,
                100,
                3,
                1_000_000,
                500_000,
                100_000,
                5_000_000,
                120_000,
                8,
                costEnforced: true,
                "USD",
                Content("cost", "rates-v1")),
            Content("proposal", "proposal-v1"),
            Content("agent", "agent-v1"),
            Content("context", "context-v1"),
            Content("tools", "tools-v1"),
            Content("provider", "provider-v1"),
            Content("retry", "retry-v1"),
            Content("m2", "m2-v1"),
            Content("product", "9853f5e234cd7c245b058e7573b8c53e51c188a9"));
        var input = new CampaignPlanningInput(
            snapshot,
            executionPolicy,
            classifications,
            observations,
            audit,
            new CampaignPlanningOwnerAuthoritySet([
                new CampaignPlanningOwnerAuthority(
                    "owner.synthetic.widget.run",
                    [targetAuthority]),
            ]));
        return new Scenario(input);
    }

    private static BoundObservationEvidence BindEvidence(
        DocumentationObservation observation,
        DocumentationDeclarationInput declaration,
        EvidenceSubject subject,
        string evidenceId)
    {
        var bundle = Assert.IsType<EvidenceBundle>(EvidenceNormalizer.Normalize([
            EvidenceInput.Candidate(
                evidenceId,
                subject,
                EvidenceKind.SourceDeclaration,
                EvidenceRelation.Declares,
                declaration.DeclarationText,
                EvidenceInput.RepositoryLocator(
                    "src/Synthetic/Widget.cs",
                    declaration.DeclarationSpan.Start,
                    declaration.DeclarationSpan.End)),
        ]).Bundle);
        return Assert.IsType<BoundObservationEvidence>(EvidenceObservationBinder.Bind(
            observation,
            bundle,
            [EvidenceBindingInput.Declaration(
                declaration.DeclarationId,
                evidenceId,
                documentationEvidenceId: null)]).Binding);
    }

    private static PolicyDocumentV1 ParsePolicy(PolicyExpectation expectation)
    {
        var json = $$"""
            {"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"{{PolicyConfigurationVocabulary.GetId(expectation)}}"}
            """;
        return Assert.IsType<PolicyDocumentV1>(
            PolicyConfigurationEvaluator.Parse(Encoding.UTF8.GetBytes(json)).Document);
    }

    private static CampaignPlanningContentAuthority Content(string id, string content) =>
        CampaignPlanningContentAuthority.Create(id, Encoding.UTF8.GetBytes(content));

    private static DocumentationScribeRequest ReadScribeRequest()
    {
        return ParseScribeRequest(ReadScribeRequestJson());
    }

    private static JsonObject ReadScribeRequestJson()
    {
        var json = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "tests",
            "fixtures",
            "documentation-scribe",
            "v1",
            "valid",
            "request.json"));
        return Assert.IsType<JsonObject>(JsonNode.Parse(json));
    }

    private static DocumentationScribeRequest ParseScribeRequest(JsonObject request)
    {
        var parsed = DocumentationScribeValidation.ParseRequest(
            Encoding.UTF8.GetBytes(request.ToJsonString()));
        Assert.Null(parsed.Failure);
        return Assert.IsType<DocumentationScribeRequest>(parsed.Request);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return Assert.IsType<DirectoryInfo>(directory).FullName;
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record Scenario(CampaignPlanningInput Input);
}
