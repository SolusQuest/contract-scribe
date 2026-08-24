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

    [Fact]
    public void KnownInvalidVectorCatalog_FreezesAdversarialCaseIdsAndCodes()
    {
        using var vector = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "tests",
            "fixtures",
            "campaign",
            "planning",
            "vectors.json")));

        var rows = vector.RootElement.GetProperty("invalidVectors")
            .EnumerateArray()
            .Select(value => $"{value.GetProperty("id").GetString()}={value.GetProperty("validationCode").GetString()}")
            .ToArray();
        Assert.Equal(
            [
                "split-shared-owner=InvalidOwnerAuthority",
                "merged-unrelated-owner=InvalidOwnerAuthority",
                "extra-owner-symbol=InvalidOwnerAuthority",
                "substituted-bound-evidence=InvalidAuditAuthority",
                "empty-declaration-span=InvalidOwnerAuthority",
                "opaque-machine-path=InvalidBound",
                "overbound-campaign-budget=InvalidConfiguration",
                "multi-error-owner-permutation=InvalidOwnerAuthority",
            ],
            rows);
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
                ProposalContract = Content(
                    CampaignPlanningContentFamily.ProposalContract,
                    "proposal",
                    "proposal-v2"),
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
            source.AuthoritativeDeclarationId,
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
            source.AuthoritativeDeclarationId,
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
    public void SharedPhysicalOwner_MustBeOneCanonicalTerminalWorkItem()
    {
        var scenario = CreateTwoTargetScenario(sharedPhysicalOwner: true, groupTogether: true);

        var item = Assert.Single(CampaignPlanner.Plan(scenario.Input).WorkItems);
        Assert.Equal(CampaignPlanningDispositionKind.Terminal, item.Disposition.Kind);
        Assert.Equal(CampaignPlanningTerminalReason.SharedOwner, item.Disposition.PrimaryTerminalReason);
        Assert.Equal(2, item.Targets.Length);
        Assert.Equal(2, item.ViolationCauses.Length);
        Assert.StartsWith("campaign-owner.", item.OwnerEquivalenceRef, StringComparison.Ordinal);
    }

    [Fact]
    public void SubstantiveMalformedTargetUnderForbiddenPolicy_IsTerminalWithExactReasons()
    {
        var scenario = CreateScenario(
            PolicyExpectation.Forbidden,
            malformedTarget: true);
        var plan = CampaignPlanner.Plan(scenario.Input);

        var item = Assert.Single(plan.WorkItems);
        Assert.Equal(CampaignPlanningDispositionKind.Terminal, item.Disposition.Kind);
        Assert.Equal(
            CampaignPlanningTerminalReason.UnsupportedRemoval,
            item.Disposition.PrimaryTerminalReason);
        Assert.Collection(
            item.Disposition.TerminalReasons,
            reason => Assert.Equal(CampaignPlanningTerminalReason.UnsupportedRemoval, reason),
            reason => Assert.Equal(CampaignPlanningTerminalReason.UnsupportedBlockState, reason));
        Assert.Single(item.ViolationCauses);
        Assert.All(
            scenario.Input.Observations.Observations.Where(value =>
                value.Subject.ComponentKind is not null),
            observation =>
            {
                Assert.Equal(DocumentationObservationValue.Unavailable, observation.Value);
                Assert.Equal(
                    DocumentationUnavailableCause.MalformedXml,
                    observation.UnavailableCause);
            });
    }

    [Fact]
    public void SplitSharedPhysicalOwner_FailsClosed()
    {
        var scenario = CreateTwoTargetScenario(sharedPhysicalOwner: true, groupTogether: false);

        var failure = Assert.Throws<CampaignPlanningValidationException>(() =>
            CampaignPlanner.Plan(scenario.Input));
        Assert.Equal(CampaignPlanningValidationCode.InvalidOwnerAuthority, failure.Code);
    }

    [Fact]
    public void UnrelatedPhysicalOwners_CannotBeMerged()
    {
        var scenario = CreateTwoTargetScenario(sharedPhysicalOwner: false, groupTogether: true);

        var failure = Assert.Throws<CampaignPlanningValidationException>(() =>
            CampaignPlanner.Plan(scenario.Input));
        Assert.Equal(CampaignPlanningValidationCode.InvalidOwnerAuthority, failure.Code);
    }

    [Fact]
    public void ExtraOwnerSymbol_FailsClosed()
    {
        var scenario = CreateTwoTargetScenario(sharedPhysicalOwner: false, groupTogether: false);
        var owners = scenario.Input.OwnerAuthority.Owners;
        var first = owners[0];
        var firstTarget = Assert.Single(first.Targets);
        var extraSymbol = Assert.Single(owners[1].Targets).Target.SymbolRef;
        var invalid = scenario.Input with
        {
            OwnerAuthority = new CampaignPlanningOwnerAuthoritySet([
                first with
                {
                    Targets = [firstTarget with
                    {
                        OwnerSymbolRefs = ImmutableArray.Create(
                                firstTarget.Target.SymbolRef,
                                extraSymbol)
                            .OrderBy(SymbolKeyForTest, StringComparer.Ordinal)
                            .ToImmutableArray(),
                    }],
                },
                owners[1],
            ]),
        };

        var failure = Assert.Throws<CampaignPlanningValidationException>(() =>
            CampaignPlanner.Plan(invalid));
        Assert.Equal(CampaignPlanningValidationCode.InvalidOwnerAuthority, failure.Code);
    }

    [Fact]
    public void MultipleOwnerInputPermutation_ProducesExactSamePlan()
    {
        var scenario = CreateTwoTargetScenario(sharedPhysicalOwner: false, groupTogether: false);
        var baseline = CampaignPlanner.Plan(scenario.Input);
        var replay = CampaignPlanner.Plan(scenario.Input with
        {
            OwnerAuthority = new CampaignPlanningOwnerAuthoritySet(
                scenario.Input.OwnerAuthority.Owners.Reverse().ToImmutableArray()),
            EvidenceAuthority = scenario.Input.EvidenceAuthority.Reverse().ToImmutableArray(),
        });

        Assert.Equal(baseline.ExecutionCommitment, replay.ExecutionCommitment);
        Assert.Equal(
            baseline.WorkItems.Select(item => item.WorkItemKey),
            replay.WorkItems.Select(item => item.WorkItemKey));
    }

    [Fact]
    public void RepositoryAndGeneratedOwners_UseCanonicalSourceOrdering()
    {
        var scenario = CreateTwoTargetScenario(
            sharedPhysicalOwner: false,
            groupTogether: false,
            secondGenerated: true);

        var plan = CampaignPlanner.Plan(scenario.Input);
        Assert.Equal(2, plan.WorkItems.Length);
        Assert.Equal(
            DocumentationPatchSourceKind.Repository,
            Assert.Single(plan.WorkItems[0].Targets).Source.Kind);
        Assert.Equal(
            DocumentationPatchSourceKind.SourceGenerator,
            Assert.Single(plan.WorkItems[1].Targets).Source.Kind);
        Assert.Equal(
            CampaignPlanningTerminalReason.NonRepositorySource,
            plan.WorkItems[1].Disposition.PrimaryTerminalReason);
    }

    [Fact]
    public void MultipleInvalidInputs_UseStableCategoryPrecedenceAcrossPermutation()
    {
        var scenario = CreateTwoTargetScenario(sharedPhysicalOwner: false, groupTogether: false);
        var owners = scenario.Input.OwnerAuthority.Owners;
        var sourceTarget = Assert.Single(owners[0].Targets);
        var source = Assert.IsType<CampaignPlanningRepositorySourceAuthority>(sourceTarget.Source);
        var invalidSource = new CampaignPlanningRepositorySourceAuthority(
            "C:\\private\\A.cs",
            source.AuthoritativeDeclarationId,
            source.ContentSha256,
            source.Encoding,
            source.RequestedDeclarationSpan,
            source.CanonicalDeclarationSpan,
            source.OwnerSpan,
            source.DocumentationSpan,
            source.BlockState);
        var styleTarget = Assert.Single(owners[1].Targets);
        var invalidOwners = new[]
        {
            owners[0] with
            {
                Targets = [sourceTarget with { Source = invalidSource }],
            },
            owners[1] with
            {
                Targets = [styleTarget with
                {
                    ExecutableStyleProfile = ReadScribeRequest().StyleProfile,
                }],
            },
        };

        var first = Assert.Throws<CampaignPlanningValidationException>(() =>
            CampaignPlanner.Plan(scenario.Input with
            {
                OwnerAuthority = new CampaignPlanningOwnerAuthoritySet(invalidOwners.ToImmutableArray()),
            }));
        var reversed = Assert.Throws<CampaignPlanningValidationException>(() =>
            CampaignPlanner.Plan(scenario.Input with
            {
                OwnerAuthority = new CampaignPlanningOwnerAuthoritySet(
                    invalidOwners.Reverse().ToImmutableArray()),
            }));

        Assert.Equal(CampaignPlanningValidationCode.InvalidOwnerAuthority, first.Code);
        Assert.Equal(first.Code, reversed.Code);
    }

    [Fact]
    public void BoundEvidenceAuthorityCannotBeSubstitutedAcrossSubjects()
    {
        var scenario = CreateTwoTargetScenario(sharedPhysicalOwner: false, groupTogether: false);
        var evidence = scenario.Input.EvidenceAuthority;
        var invalid = scenario.Input with
        {
            EvidenceAuthority = [
                evidence[0] with { Binding = evidence[1].Binding },
                evidence[1] with { Binding = evidence[0].Binding },
            ],
        };

        var failure = Assert.Throws<CampaignPlanningValidationException>(() =>
            CampaignPlanner.Plan(invalid));
        Assert.Equal(CampaignPlanningValidationCode.InvalidAuditAuthority, failure.Code);
    }

    [Fact]
    public void EmptyOrWrongDeclarationSpan_FailsClosed()
    {
        var scenario = CreateScenario();
        var owner = Assert.Single(scenario.Input.OwnerAuthority.Owners);
        var target = Assert.Single(owner.Targets);
        var source = Assert.IsType<CampaignPlanningRepositorySourceAuthority>(target.Source);
        foreach (var requestedSpan in new[]
                 {
                     DocumentationObservationInput.Span(0, 0),
                     DocumentationObservationInput.Span(
                         source.OwnerSpan.End,
                         source.OwnerSpan.End + 1),
                 })
        {
            var invalidSource = new CampaignPlanningRepositorySourceAuthority(
                source.Path,
                source.AuthoritativeDeclarationId,
                source.ContentSha256,
                source.Encoding,
                requestedSpan,
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
        }
    }

    [Fact]
    public void WrongAuthoritativeDeclarationId_FailsClosed()
    {
        var scenario = CreateScenario();
        var owner = Assert.Single(scenario.Input.OwnerAuthority.Owners);
        var target = Assert.Single(owner.Targets);
        var source = Assert.IsType<CampaignPlanningRepositorySourceAuthority>(target.Source);
        var invalidSource = new CampaignPlanningRepositorySourceAuthority(
            source.Path,
            "decl." + new string('f', 64),
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
    }

    [Fact]
    public void OpaqueOutputIdentifiersRejectMachinePaths()
    {
        var scenario = CreateScenario();
        var invalid = scenario.Input with
        {
            Snapshot = scenario.Input.Snapshot with
            {
                OpaqueSnapshotBinding = "/home/runner/private-repository",
            },
        };

        var failure = Assert.Throws<CampaignPlanningValidationException>(() =>
            CampaignPlanner.Plan(invalid));
        Assert.Equal(CampaignPlanningValidationCode.InvalidBound, failure.Code);
        Assert.DoesNotContain("private-repository", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationProjectionCanonicalizesJsonPropertyOrder()
    {
        using var first = JsonDocument.Parse("{\"alpha\":1,\"beta\":2}");
        using var second = JsonDocument.Parse("{\"beta\":2,\"alpha\":1}");
        using var orderedArray = JsonDocument.Parse("{\"values\":[1,2]}");
        using var reversedArray = JsonDocument.Parse("{\"values\":[2,1]}");
        var firstAuthority = CampaignPlanningContentAuthority.CreateValidatedJsonProjection(
            CampaignPlanningContentFamily.RetryPolicy,
            "retry",
            first.RootElement);
        var secondAuthority = CampaignPlanningContentAuthority.CreateValidatedJsonProjection(
            CampaignPlanningContentFamily.RetryPolicy,
            "retry",
            second.RootElement);
        var otherFamily = CampaignPlanningContentAuthority.CreateValidatedJsonProjection(
            CampaignPlanningContentFamily.AgentProtocol,
            "agent",
            second.RootElement);
        var orderedAuthority = CampaignPlanningContentAuthority.CreateValidatedJsonProjection(
            CampaignPlanningContentFamily.RetryPolicy,
            "retry",
            orderedArray.RootElement);
        var reversedAuthority = CampaignPlanningContentAuthority.CreateValidatedJsonProjection(
            CampaignPlanningContentFamily.RetryPolicy,
            "retry",
            reversedArray.RootElement);

        Assert.Equal(firstAuthority.ContentSha256, secondAuthority.ContentSha256);
        Assert.NotEqual(firstAuthority.ContentSha256, otherFamily.ContentSha256);
        Assert.NotEqual(orderedAuthority.ContentSha256, reversedAuthority.ContentSha256);
    }

    [Fact]
    public void OverBoundCampaignBudget_FailsClosed()
    {
        var scenario = CreateScenario();
        var budget = scenario.Input.ExecutionPolicy.CampaignBudget;
        var invalidBudget = new CampaignPlanningBudgetPolicy(
            budget.MaximumBlocks,
            budget.MaximumChangedFiles,
            long.MaxValue,
            budget.MaximumProviderRequests,
            budget.MaximumAttemptsPerTarget,
            budget.MaximumInputTokens,
            budget.MaximumUncachedInputTokens,
            budget.MaximumOutputTokens,
            budget.MaximumCostMicrounits,
            budget.MaximumElapsedMilliseconds,
            budget.MaximumCandidatesPerBlock,
            budget.CostEnforced,
            budget.CostCurrency,
            budget.CostRatePolicy);

        var failure = Assert.Throws<CampaignPlanningValidationException>(() =>
            CampaignPlanner.Plan(scenario.Input with
            {
                ExecutionPolicy = scenario.Input.ExecutionPolicy with
                {
                    CampaignBudget = invalidBudget,
                },
            }));
        Assert.Equal(CampaignPlanningValidationCode.InvalidConfiguration, failure.Code);
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
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                Assert.Fail("The fresh-process campaign probe timed out and was terminated.");
            }

            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
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
            source.AuthoritativeDeclarationId,
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
        PolicyExpectation expectation = PolicyExpectation.Required,
        bool malformedTarget = false)
    {
        const string Context = "synthetic.v1";
        const string DocumentationId = "M:Synthetic.Widget.Run(System.String)";
        const string Path = "src/Synthetic/Widget.cs";
        const string Body = "public void Run(string value) { }";
        const string MalformedDocumentation = "/// <summary>Malformed\n";
        const string DeclarationId = "decl.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var declarationText = malformedTarget ? MalformedDocumentation + Body : Body;
        var leadingTriviaText = malformedTarget ? MalformedDocumentation : string.Empty;
        var leadingTriviaSpan = DocumentationObservationInput.Span(0, leadingTriviaText.Length);
        var documentationSpan = malformedTarget
            ? DocumentationObservationInput.Span(0, MalformedDocumentation.Length)
            : (Utf16Span?)null;
        var blockState = malformedTarget
            ? DocumentationBlockState.Malformed
            : DocumentationBlockState.NoBlock;

        var classificationsBuffer = new ClassificationCandidateBuffer();
        classificationsBuffer.AddTarget(
            Context,
            DocumentationId,
            PrimarySymbolKind.Method,
            ImmutableArray<SymbolTrait>.Empty,
            ClassificationOrigin.Source,
            [ClassificationInput.RepositoryLocator(Path, 0, declarationText.Length)]);
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
            Sha256(declarationText),
            DocumentationObservationInput.Span(0, declarationText.Length),
            declarationText,
            leadingTriviaSpan,
            leadingTriviaText,
            documentationSpan,
            malformedTarget ? MalformedDocumentation : null,
            blockState,
            parentSubstantive: malformedTarget);
        var parameterDeclaration = DocumentationObservationInput.RepositoryDeclaration(
            DeclarationId,
            DocumentationAuthorityRole.Ordinary,
            "project.synthetic",
            Path,
            Sha256(declarationText),
            DocumentationObservationInput.Span(0, declarationText.Length),
            declarationText,
            leadingTriviaSpan,
            leadingTriviaText,
            documentationSpan,
            malformedTarget ? MalformedDocumentation : null,
            blockState,
            parentSubstantive: malformedTarget,
            componentLocalName: "value",
            componentMatch: malformedTarget ? null : DocumentationComponentMatch.Absent);
        var returnDeclaration = DocumentationObservationInput.RepositoryDeclaration(
            DeclarationId,
            DocumentationAuthorityRole.Ordinary,
            "project.synthetic",
            Path,
            Sha256(declarationText),
            DocumentationObservationInput.Span(0, declarationText.Length),
            declarationText,
            leadingTriviaSpan,
            leadingTriviaText,
            documentationSpan,
            malformedTarget ? MalformedDocumentation : null,
            blockState,
            parentSubstantive: malformedTarget,
            componentLocalName: null,
            componentMatch: malformedTarget ? null : DocumentationComponentMatch.Absent);
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
        var evidenceAuthority = ImmutableArray.CreateBuilder<CampaignPlanningEvidenceAuthority>();
        var targetObservation = observations.Observations.Single(
            value => value.Subject.ComponentKind is null);
        var targetBinding = BindEvidence(
            targetObservation,
            targetDeclaration,
            EvidenceInput.TargetSubject(Context, DocumentationId),
            "evidence.declaration");
        inputs.Add(AuditInput.Target(
            target,
            contribution,
            targetBinding));
        evidenceAuthority.Add(new CampaignPlanningEvidenceAuthority(
            targetObservation.Subject,
            targetBinding));
        foreach (var component in components)
        {
            var declaration = component.ComponentKind == ComponentKind.Parameter
                ? parameterDeclaration
                : returnDeclaration;
            var componentObservation = observations.Observations.Single(value =>
                value.Subject.ComponentKind == component.ComponentKind);
            var componentBinding = BindEvidence(
                componentObservation,
                declaration,
                EvidenceInput.ComponentSubject(
                    Context,
                    DocumentationId,
                    component.ComponentKind,
                    component.Identity),
                "evidence.declaration");
            inputs.Add(AuditInput.Component(
                component,
                contribution,
                componentBinding));
            evidenceAuthority.Add(new CampaignPlanningEvidenceAuthority(
                componentObservation.Subject,
                componentBinding));
        }

        var audit = AuditAggregator.Aggregate(
            TargetProfile.ExternalApi,
            classifications,
            policy,
            inputs);
        var request = ReadScribeRequest();
        var source = new CampaignPlanningRepositorySourceAuthority(
            Path,
            DeclarationId,
            new string('b', 64),
            DocumentationPatchRepositoryEncoding.Utf8,
            DocumentationObservationInput.Span(0, declarationText.Length),
            DocumentationObservationInput.Span(0, declarationText.Length),
            DocumentationObservationInput.Span(0, declarationText.Length),
            documentationSpan,
            blockState);
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
                Content(CampaignPlanningContentFamily.CostRatePolicy, "cost", "rates-v1")),
            Content(CampaignPlanningContentFamily.ProposalContract, "proposal", "proposal-v1"),
            Content(CampaignPlanningContentFamily.AgentProtocol, "agent", "agent-v1"),
            Content(CampaignPlanningContentFamily.ContextSelectionPolicy, "context", "context-v1"),
            Content(CampaignPlanningContentFamily.ToolPolicyAndRegistry, "tools", "tools-v1"),
            Content(CampaignPlanningContentFamily.ProviderModelRequestProfile, "provider", "provider-v1"),
            Content(CampaignPlanningContentFamily.RetryPolicy, "retry", "retry-v1"),
            Content(CampaignPlanningContentFamily.M2ProjectionPolicy, "m2", "m2-v1"),
            Content(
                CampaignPlanningContentFamily.ProductContractRevision,
                "product",
                "9853f5e234cd7c245b058e7573b8c53e51c188a9"));
        var input = new CampaignPlanningInput(
            snapshot,
            executionPolicy,
            classifications,
            observations,
            evidenceAuthority.ToImmutable(),
            audit,
            new CampaignPlanningOwnerAuthoritySet([
                new CampaignPlanningOwnerAuthority([targetAuthority]),
            ]));
        return new Scenario(input);
    }

    private static Scenario CreateTwoTargetScenario(
        bool sharedPhysicalOwner,
        bool groupTogether,
        bool secondGenerated = false)
    {
        const string Declaration = "public void Run() { }";
        var specifications = new[]
        {
            new
            {
                Context = "synthetic.a",
                DocumentationId = "M:Synthetic.Widget.RunA",
                Path = sharedPhysicalOwner ? "src/Synthetic/Shared.cs" : "src/Synthetic/A.cs",
                DeclarationId = "decl." + new string('a', 64),
                FileSha256 = new string('b', 64),
                Generated = false,
                ProducerId = "sgp." + new string('1', 64),
                OutputId = "sgo." + new string('2', 64),
            },
            new
            {
                Context = "synthetic.b",
                DocumentationId = "M:Synthetic.Widget.RunB",
                Path = sharedPhysicalOwner ? "src/Synthetic/Shared.cs" : "src/Synthetic/B.cs",
                DeclarationId = "decl." + new string('c', 64),
                FileSha256 = secondGenerated
                    ? Sha256(Declaration)
                    : sharedPhysicalOwner ? new string('b', 64) : new string('d', 64),
                Generated = secondGenerated,
                ProducerId = "sgp." + new string('3', 64),
                OutputId = "sgo." + new string('4', 64),
            },
        };

        var classificationBuffer = new ClassificationCandidateBuffer();
        foreach (var specification in specifications)
        {
            CandidateLocator[] locators = specification.Generated
                ? [ClassificationInput.GeneratedSourceLocator(
                    specification.ProducerId,
                    specification.OutputId,
                    0,
                    Declaration.Length)]
                : [ClassificationInput.RepositoryLocator(
                    specification.Path,
                    0,
                    Declaration.Length)];
            classificationBuffer.AddTarget(
                specification.Context,
                specification.DocumentationId,
                PrimarySymbolKind.Method,
                ImmutableArray<SymbolTrait>.Empty,
                specification.Generated
                    ? ClassificationOrigin.SourceGenerator
                    : ClassificationOrigin.Source,
                locators);
        }

        var classifications = Assert.IsType<ClassificationSet>(
            classificationBuffer.Normalize(TargetProfile.ExternalApi).ClassificationSet);
        var observationBuffer = new DocumentationObservationCandidateBuffer(classifications);
        var declarations = new Dictionary<SymbolRef, DocumentationDeclarationInput>();
        foreach (var target in classifications.Targets)
        {
            var specification = specifications.Single(value =>
                value.Context == target.SymbolRef.CompilationContextRef);
            var declaration = specification.Generated
                ? DocumentationObservationInput.GeneratedDeclaration(
                    specification.DeclarationId,
                    DocumentationAuthorityRole.Ordinary,
                    "project." + specification.Context,
                    DocumentationSourceKind.SourceGenerator,
                    specification.ProducerId,
                    specification.OutputId,
                    Sha256(Declaration),
                    DocumentationObservationInput.Span(0, Declaration.Length),
                    Declaration,
                    DocumentationObservationInput.Span(0, 0),
                    string.Empty,
                    null,
                    null,
                    DocumentationBlockState.NoBlock,
                    parentSubstantive: false)
                : DocumentationObservationInput.RepositoryDeclaration(
                    specification.DeclarationId,
                    DocumentationAuthorityRole.Ordinary,
                    "project." + specification.Context,
                    specification.Path,
                    Sha256(Declaration),
                    DocumentationObservationInput.Span(0, Declaration.Length),
                    Declaration,
                    DocumentationObservationInput.Span(0, 0),
                    string.Empty,
                    null,
                    null,
                    DocumentationBlockState.NoBlock,
                    parentSubstantive: false);
            declarations.Add(target.SymbolRef, declaration);
            observationBuffer.AddTarget(target, true, [declaration]);
        }

        var observations = Assert.IsType<DocumentationObservationSet>(
            observationBuffer.Normalize().ObservationSet);
        var policy = ParsePolicy(PolicyExpectation.Required);
        var contribution = Assert.IsType<PolicyContributionSet>(
            PolicyConfigurationEvaluator.Evaluate(
                policy,
                specifications.Select(value => value.Generated
                    ? (PolicyContributionInput)PolicyConfigurationInput.Generated(
                        "src/Synthetic.csproj",
                        "source-generator",
                        value.ProducerId,
                        value.OutputId)
                    : (PolicyContributionInput)PolicyConfigurationInput.Repository(
                        "src/Synthetic.csproj",
                        value.Path))).ContributionSet);
        var auditInputs = ImmutableArray.CreateBuilder<AuditRecordInput>();
        var evidenceAuthority = ImmutableArray.CreateBuilder<CampaignPlanningEvidenceAuthority>();
        foreach (var target in classifications.Targets)
        {
            var observation = observations.Observations.Single(value =>
                value.Subject.ParentSymbolRef == target.SymbolRef);
            var specification = specifications.Single(value =>
                value.Context == target.SymbolRef.CompilationContextRef);
            var subject = EvidenceInput.TargetSubject(
                target.SymbolRef.CompilationContextRef,
                target.SymbolRef.DocumentationCommentId);
            var binding = specification.Generated
                ? BindGeneratedEvidence(
                    observation,
                    declarations[target.SymbolRef],
                    subject,
                    specification.ProducerId,
                    specification.OutputId)
                : BindEvidence(
                    observation,
                    declarations[target.SymbolRef],
                    subject,
                    "evidence.declaration",
                    specification.Path);
            auditInputs.Add(AuditInput.Target(target, contribution, binding));
            evidenceAuthority.Add(new CampaignPlanningEvidenceAuthority(observation.Subject, binding));
        }

        var audit = AuditAggregator.Aggregate(
            TargetProfile.ExternalApi,
            classifications,
            policy,
            auditInputs);
        var zeroComponentStyle = ReadZeroComponentStyleProfile();
        var symbols = classifications.Targets.Select(target => target.SymbolRef).ToImmutableArray();
        var targetAuthorities = classifications.Targets.Select(target =>
        {
            var specification = specifications.Single(value =>
                value.Context == target.SymbolRef.CompilationContextRef);
            CampaignPlanningSourceAuthority source = specification.Generated
                ? new CampaignPlanningGeneratedSourceAuthority(
                    DocumentationPatchSourceKind.SourceGenerator,
                    specification.DeclarationId,
                    specification.ProducerId,
                    specification.OutputId,
                    specification.FileSha256,
                    DocumentationObservationInput.Span(0, Declaration.Length),
                    DocumentationObservationInput.Span(0, Declaration.Length),
                    DocumentationObservationInput.Span(0, Declaration.Length),
                    null,
                    DocumentationBlockState.NoBlock)
                : new CampaignPlanningRepositorySourceAuthority(
                    specification.Path,
                    specification.DeclarationId,
                    specification.FileSha256,
                    DocumentationPatchRepositoryEncoding.Utf8,
                    DocumentationObservationInput.Span(0, Declaration.Length),
                    DocumentationObservationInput.Span(0, Declaration.Length),
                    DocumentationObservationInput.Span(0, Declaration.Length),
                    null,
                    DocumentationBlockState.NoBlock);
            return new CampaignPlanningTargetAuthority(
                target,
                source,
                [],
                groupTogether ? symbols : [target.SymbolRef],
                multiDeclarator: false,
                primaryConstructor: false,
                primaryConstructorAlias: false,
                zeroComponentStyle);
        }).ToImmutableArray();
        var ownerAuthority = groupTogether
            ? new CampaignPlanningOwnerAuthoritySet([
                new CampaignPlanningOwnerAuthority(targetAuthorities),
            ])
            : new CampaignPlanningOwnerAuthoritySet(
                targetAuthorities.Select(target =>
                    new CampaignPlanningOwnerAuthority([target])).ToImmutableArray());
        var baseline = CreateScenario();
        return new Scenario(new CampaignPlanningInput(
            baseline.Input.Snapshot,
            baseline.Input.ExecutionPolicy,
            classifications,
            observations,
            evidenceAuthority.ToImmutable(),
            audit,
            ownerAuthority));
    }

    private static DocumentationScribeStyleProfile ReadZeroComponentStyleProfile()
    {
        var request = ReadScribeRequestJson();
        request["target"]!["applicableComponents"]!.AsArray().Clear();
        request["styleProfile"]!["componentPolicies"]!.AsArray().Clear();
        request["evidenceReferences"]!.AsArray().Clear();
        return ParseScribeRequest(request).StyleProfile;
    }

    private static BoundObservationEvidence BindEvidence(
        DocumentationObservation observation,
        DocumentationDeclarationInput declaration,
        EvidenceSubject subject,
        string evidenceId,
        string repositoryPath = "src/Synthetic/Widget.cs")
    {
        var present = observation.Value == DocumentationObservationValue.Present;
        var documentationEvidence = present
            || declaration.BlockState == DocumentationBlockState.Malformed;
        var evidenceText = documentationEvidence
            ? Assert.IsType<string>(declaration.DocumentationText)
            : declaration.DeclarationText;
        var evidenceSpan = documentationEvidence
            ? Assert.IsType<Utf16Span>(declaration.DocumentationSpan)
            : declaration.DeclarationSpan;
        var bundle = Assert.IsType<EvidenceBundle>(EvidenceNormalizer.Normalize([
            EvidenceInput.Candidate(
                evidenceId,
                subject,
                documentationEvidence ? EvidenceKind.SourceXmlDocumentation : EvidenceKind.SourceDeclaration,
                documentationEvidence ? EvidenceRelation.Documents : EvidenceRelation.Declares,
                evidenceText,
                EvidenceInput.RepositoryLocator(
                    repositoryPath,
                    evidenceSpan.Start,
                    evidenceSpan.End)),
        ]).Bundle);
        return Assert.IsType<BoundObservationEvidence>(EvidenceObservationBinder.Bind(
            observation,
            bundle,
            [EvidenceBindingInput.Declaration(
                declaration.DeclarationId,
                documentationEvidence ? null : evidenceId,
                documentationEvidence ? evidenceId : null)]).Binding);
    }

    private static BoundObservationEvidence BindGeneratedEvidence(
        DocumentationObservation observation,
        DocumentationDeclarationInput declaration,
        EvidenceSubject subject,
        string producerId,
        string outputId)
    {
        const string EvidenceId = "evidence.generated-declaration";
        var bundle = Assert.IsType<EvidenceBundle>(EvidenceNormalizer.Normalize([
            EvidenceInput.Candidate(
                EvidenceId,
                subject,
                EvidenceKind.SourceDeclaration,
                EvidenceRelation.Declares,
                declaration.DeclarationText,
                EvidenceInput.GeneratedOutputLocator(
                    GeneratedOutputKind.SourceGenerator,
                    producerId,
                    outputId,
                    declaration.Source.SourceSha256,
                    declaration.DeclarationSpan.Start,
                    declaration.DeclarationSpan.End)),
        ]).Bundle);
        return Assert.IsType<BoundObservationEvidence>(EvidenceObservationBinder.Bind(
            observation,
            bundle,
            [EvidenceBindingInput.Declaration(
                declaration.DeclarationId,
                EvidenceId,
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

    private static CampaignPlanningContentAuthority Content(
        CampaignPlanningContentFamily family,
        string id,
        string content) =>
        CampaignPlanningContentAuthority.CreateValidatedJsonProjection(
            family,
            id,
            JsonSerializer.SerializeToElement(new { value = content }));

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

    private static string SymbolKeyForTest(SymbolRef symbol) =>
        symbol.CompilationContextRef + "\u001f" + symbol.DocumentationCommentId;

    private sealed record Scenario(CampaignPlanningInput Input);
}
