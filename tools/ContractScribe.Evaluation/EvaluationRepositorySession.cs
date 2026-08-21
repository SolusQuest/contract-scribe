using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Core;
using ContractScribe.Roslyn;
using Microsoft.CodeAnalysis;

namespace ContractScribe.Evaluation;

internal sealed record PreparedEvaluationCase(
    EvaluationScenario Scenario,
    object SelectedAudit,
    ReadOnlyMemory<byte> RequestBytes,
    DocumentationScribeRequest Request,
    DocumentationScribeAttemptId AttemptId);

internal sealed class EvaluationRepositorySession : IAsyncDisposable
{
    private readonly LoadedRepositorySession session;
    private readonly string repositoryRoot;
    private readonly ClassifiedRepositorySession classified;
    private readonly ObservedRepositorySession observations;
    private readonly object authority;
    private readonly AuditDocument audit;
    private readonly ProductionCompositionAdapter adapter;

    private EvaluationRepositorySession(
        LoadedRepositorySession session,
        string repositoryRoot,
        ClassifiedRepositorySession classified,
        ObservedRepositorySession observations,
        object authority,
        AuditDocument audit,
        ProductionCompositionAdapter adapter)
    {
        this.session = session;
        this.repositoryRoot = repositoryRoot;
        this.classified = classified;
        this.observations = observations;
        this.authority = authority;
        this.audit = audit;
        this.adapter = adapter;
    }

    internal static async Task<EvaluationRepositorySession> CreateAsync(
        LoadedEvaluationManifest loaded,
        ProductionCompositionAdapter adapter,
        CancellationToken cancellationToken)
    {
        var projectPath = Path.GetFullPath(Path.Join(
            loaded.CorpusDirectory,
            loaded.Manifest.RepositoryProject));
        var repositoryRoot = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidDataException("evaluation.repository.path-invalid");
        LoadedRepositorySession? session = null;
        try
        {
            var load = await new RepositoryLoader().LoadAsync(
                new RepositoryLoadRequest(repositoryRoot, Path.GetFileName(projectPath)),
                cancellationToken).ConfigureAwait(false);
            if (load.Status != RepositoryLoadStatus.Success || load.Session is null)
            {
                var stage = load.PrimaryFailure?.Stage ?? "unknown";
                var code = load.PrimaryFailure?.Code ?? "unknown";
                throw new InvalidDataException(
                    "evaluation.repository.load-failed."
                    + NormalizeCode(stage)
                    + "."
                    + NormalizeCode(code));
            }

            session = load.Session;
            var classified = new SymbolClassifier().ClassifySession(
                session,
                TargetProfile.ExternalApi,
                cancellationToken);
            if (classified.Classification.Status != ClassificationRunStatus.Success
                || classified.Classification.ClassificationSet is null)
            {
                throw new InvalidDataException("evaluation.repository.classification-failed");
            }

            var observations = new DocumentationObserver().Observe(classified, cancellationToken);
            if (observations.Status != DocumentationObservationRunStatus.Success)
            {
                throw new InvalidDataException("evaluation.repository.observation-failed");
            }

            var policy = PolicyConfigurationEvaluator.Parse(Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\",\"defaultDecision\":\"required\"}"))
                .Document ?? throw new InvalidDataException("evaluation.policy.invalid");
            var extracted = new PolicyEvidenceExtractor().Extract(
                classified,
                observations,
                policy,
                cancellationToken);
            if (extracted.Status != PolicyEvidenceExtractionStatus.Success)
            {
                throw new InvalidDataException("evaluation.repository.evidence-failed");
            }

            var inputs = adapter.AssembleAuditInputs(
                classified.Classification.ClassificationSet,
                policy,
                extracted).ToImmutableArray();
            var audit = AuditAggregator.Aggregate(
                TargetProfile.ExternalApi,
                classified.Classification.ClassificationSet,
                policy,
                inputs);
            var authority = adapter.CreateAuthority(
                classified,
                observations,
                policy,
                inputs,
                audit);
            return new EvaluationRepositorySession(
                session,
                repositoryRoot,
                classified,
                observations,
                authority,
                audit,
                adapter);
        }
        catch
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    internal PreparedEvaluationCase Prepare(
        LoadedEvaluationManifest loaded,
        EvaluationScenario scenario)
    {
        var classifications = classified.Classification.ClassificationSet
            ?? throw new InvalidOperationException("evaluation.repository.session-stale");
        var target = classifications.Targets.SingleOrDefault(candidate =>
            candidate.SymbolRef.DocumentationCommentId == scenario.TargetDocumentationId
            && candidate.SupportStatus == SupportStatus.Supported)
            ?? throw new InvalidDataException("evaluation.scenario.target-invalid");
        var selected = adapter.Select(authority, target);
        var auditOutcome = SelectAuditOutcome(target);
        var project = session.Projects.Single(item =>
            item.CompilationContextRef == target.SymbolRef.CompilationContextRef);
        var compilation = adapter.GetCompilation(project);
        var symbol = DocumentationCommentId.GetSymbolsForDeclarationId(
            target.SymbolRef.DocumentationCommentId,
            compilation).Single();
        var syntax = symbol.DeclaringSyntaxReferences.Single();
        var observation = observations.ObservationSet!.Observations.Single(item =>
            item.Subject.ComponentKind is null
            && item.Subject.ParentSymbolRef == target.SymbolRef);
        var declaration = observation.Declarations.Single();
        var repositorySource = declaration.Source as RepositoryDocumentationSourceIdentity
            ?? throw new InvalidDataException("evaluation.scenario.source-invalid");
        var sourceRepositoryPath = repositorySource.Path;
        var sourcePath = Path.Join(repositoryRoot, sourceRepositoryPath);
        var sourceSha256 = Sha256(File.ReadAllBytes(sourcePath));
        var bootstrapSelection = DocumentationScribeContextValidation.CreateBootstrapSelection(
            session.RepositoryContextRef,
            session.InputIdentity,
            TargetProfile.ExternalApi,
            target.SymbolRef,
            sourceRepositoryPath,
            syntax.Span.Start,
            syntax.Span.End,
            sourceSha256);
        var targetSpan = ((RepositoryEvidenceLocator)bootstrapSelection.SourceLocator).Span!.Value;
        var bootstrap = new DocumentationScribeContextBootstrapper().Bootstrap(
            classified,
            bootstrapSelection);
        if (bootstrap.Status is not (DocumentationScribeContextBootstrapStatus.Succeeded
                or DocumentationScribeContextBootstrapStatus.Incomplete)
            || bootstrap.Context is null)
        {
            throw new InvalidDataException(
                "evaluation.scenario.context-failed."
                + NormalizeCode(bootstrap.Status.ToString())
                + "."
                + NormalizeCode(bootstrap.Failure?.Code ?? "unknown"));
        }

        var requestBytes = CreateRequest(
            target,
            sourceRepositoryPath,
            targetSpan,
            sourceSha256,
            bootstrap.Context,
            auditOutcome,
            scenario.AddEvidenceConflict,
            loaded.Selection.Limits,
            scenario.MaximumElapsedMillisecondsOverride);
        var parsed = DocumentationScribeValidation.ParseRequest(requestBytes);
        if (!parsed.IsValid || parsed.Request is null)
        {
            throw new InvalidDataException(
                "evaluation.scenario.request-invalid."
                + NormalizeCode(parsed.Failure?.Code ?? "unknown")
                + "."
                + NormalizeCode(parsed.Failure?.Pointer ?? "unknown"));
        }

        var suffix = Sha256(Encoding.UTF8.GetBytes(loaded.CorpusIdentity + "\0" + scenario.Id))[..32];
        if (!DocumentationScribeAttemptId.TryParse("scribe-attempt." + suffix, out var attemptId))
        {
            throw new InvalidDataException("evaluation.scenario.attempt-invalid");
        }

        return new PreparedEvaluationCase(
            scenario,
            selected,
            requestBytes,
            parsed.Request,
            attemptId);
    }

    public ValueTask DisposeAsync() => session.DisposeAsync();

    private string SelectAuditOutcome(TargetClassification target)
    {
        using var document = JsonDocument.Parse(AuditJson.Write(audit));
        return document.RootElement.GetProperty("results").EnumerateArray()
            .Single(row => row.GetProperty("classification") is { } classification
                && classification.TryGetProperty("symbolRef", out var symbolRef)
                && symbolRef.GetProperty("documentationCommentId").GetString()
                    == target.SymbolRef.DocumentationCommentId)
            .GetProperty("auditOutcome").GetString()
            ?? throw new InvalidDataException("evaluation.scenario.audit-invalid");
    }

    private ReadOnlyMemory<byte> CreateRequest(
        TargetClassification target,
        string sourcePath,
        Utf16Span targetSpan,
        string sourceSha256,
        DocumentationScribeLoadedContext context,
        string auditOutcome,
        bool addEvidenceConflict,
        EvaluationSelectionLimits selectedLimits,
        int? maximumElapsedMillisecondsOverride)
    {
        var evidence = context.Facts.Evidence.Single(item =>
            item.KindId == "source.target-declaration");
        var components = classified.Classification.ClassificationSet!.Components
            .Where(item => item.ParentSymbolRef == target.SymbolRef
                && item.SupportStatus == SupportStatus.Supported
                && item.ComponentKind is ComponentKind.TypeParameter
                    or ComponentKind.Parameter
                    or ComponentKind.Return
                    or ComponentKind.Value)
            .OrderBy(item => item.ComponentKind switch
            {
                ComponentKind.TypeParameter => 0,
                ComponentKind.Parameter => 1,
                ComponentKind.Return => 2,
                ComponentKind.Value => 3,
                _ => throw new InvalidOperationException(),
            })
            .ThenBy(item => item.Identity, StringComparer.Ordinal)
            .Select(item => new
            {
                Kind = item.ComponentKind switch
                {
                    ComponentKind.TypeParameter => "typeParameter",
                    ComponentKind.Parameter => "parameter",
                    ComponentKind.Return => "return",
                    ComponentKind.Value => "value",
                    _ => throw new InvalidOperationException(),
                },
                item.Identity,
                Name = ComponentName(item),
            }).ToArray();
        var contextReferences = new JsonArray();
        foreach (var instruction in context.Facts.Instructions.OrderBy(
            instruction => instruction.InstructionId,
            StringComparer.Ordinal))
        {
            contextReferences.Add(new JsonObject
            {
                ["contextReferenceId"] = instruction.InstructionId,
                ["kind"] = "context.project-instruction",
                ["repositoryContextRef"] = session.RepositoryContextRef.Value,
                ["path"] = instruction.Commitment.RepositoryPath,
                ["contentSha256"] = instruction.Commitment.ContentSha256,
                ["originalUtf8ByteCount"] = instruction.Commitment.OriginalUtf8ByteCount,
                ["includedUtf8ByteCount"] = instruction.Commitment.IncludedUtf8ByteCount,
                ["isTruncated"] = instruction.Commitment.IsTruncated,
            });
        }

        var subject = new JsonObject { ["symbolRef"] = Symbol(target.SymbolRef) };
        var sourceReference = new JsonObject
        {
            ["evidenceReferenceId"] = "evidence.source",
            ["repositoryContextRef"] = session.RepositoryContextRef.Value,
            ["subject"] = subject.DeepClone(),
            ["kind"] = "evidence.source.declaration",
            ["relation"] = "evidence.declares",
            ["authority"] = "authority.source-declaration",
            ["locator"] = RepositoryLocator(sourcePath, targetSpan),
            ["contentSha256"] = evidence.Commitment.ContentSha256,
            ["originalUtf8ByteCount"] = evidence.Commitment.OriginalUtf8ByteCount,
            ["includedUtf8ByteCount"] = evidence.Commitment.IncludedUtf8ByteCount,
            ["isTruncated"] = evidence.Commitment.IsTruncated,
            ["claimCategoryIds"] = new JsonArray("claim.purpose"),
        };
        var evidenceReferences = new JsonArray();
        var conflicts = new JsonArray();
        if (addEvidenceConflict)
        {
            evidenceReferences.Add(new JsonObject
            {
                ["evidenceReferenceId"] = "evidence.public-contract",
                ["repositoryContextRef"] = session.RepositoryContextRef.Value,
                ["subject"] = subject.DeepClone(),
                ["kind"] = "evidence.public-contract",
                ["relation"] = "evidence.constrains",
                ["authority"] = "authority.public-contract",
                ["locator"] = RepositoryLocator(sourcePath, targetSpan),
                ["contentSha256"] = evidence.Commitment.ContentSha256,
                ["originalUtf8ByteCount"] = evidence.Commitment.OriginalUtf8ByteCount,
                ["includedUtf8ByteCount"] = evidence.Commitment.IncludedUtf8ByteCount,
                ["isTruncated"] = evidence.Commitment.IsTruncated,
                ["claimCategoryIds"] = new JsonArray("claim.purpose"),
            });
            conflicts.Add(new JsonObject
            {
                ["relation"] = "evidence-conflict.higher-authority-contradicts",
                ["higherEvidenceReferenceId"] = "evidence.public-contract",
                ["lowerEvidenceReferenceId"] = "evidence.source",
            });
        }

        evidenceReferences.Add(sourceReference);
        var allowedAuthorities = addEvidenceConflict
            ? new JsonArray("authority.source-declaration", "authority.public-contract")
            : new JsonArray("authority.source-declaration");
        var root = new JsonObject
        {
            ["scribeRequestVersion"] = 1,
            ["context"] = new JsonObject
            {
                ["repositoryContextRef"] = session.RepositoryContextRef.Value,
                ["inputIdentity"] = session.InputIdentity,
                ["targetProfile"] = "profile.external-api",
                ["auditOutcome"] = auditOutcome,
            },
            ["target"] = new JsonObject
            {
                ["symbolRef"] = Symbol(target.SymbolRef),
                ["sourceCommitment"] = new JsonObject
                {
                    ["locator"] = RepositoryLocator(sourcePath, targetSpan),
                    ["contentSha256"] = sourceSha256,
                },
                ["applicableComponents"] = new JsonArray(components.Select(item =>
                {
                    var component = new JsonObject
                    {
                        ["kind"] = item.Kind,
                        ["identity"] = item.Identity,
                    };
                    if (item.Name is not null)
                    {
                        component["name"] = item.Name;
                    }

                    return (JsonNode?)component;
                }).ToArray()),
            },
            ["styleProfile"] = new JsonObject
            {
                ["styleProfileId"] = "style.evaluation.public-api.v1",
                ["outputLanguageId"] = "language.en",
                ["summary"] = Policy("required", 400),
                ["remarks"] = Policy("forbidden", 400),
                ["exceptions"] = Policy("forbidden", 400),
                ["componentPolicies"] = new JsonArray(components.Select(item =>
                    (JsonNode?)new JsonObject
                    {
                        ["componentIdentity"] = item.Identity,
                        ["disposition"] = "required",
                        ["maximumScalars"] = 300,
                    }).ToArray()),
                ["inheritDocDisposition"] = "forbidden",
                ["allowedLiterals"] = new JsonArray(),
                ["forbiddenLiterals"] = new JsonArray(),
                ["claimPolicies"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["claimCategoryId"] = "claim.purpose",
                        ["completeEvidenceRequired"] = false,
                        ["allowedAuthorities"] = allowedAuthorities,
                    },
                },
                ["maximumContentUnits"] = 8,
                ["maximumEvidenceRefsPerUnit"] = 4,
            },
            ["contextReferences"] = contextReferences,
            ["evidenceReferences"] = evidenceReferences,
            ["evidenceConflicts"] = conflicts,
            ["toolPolicyId"] = "tool-policy.read-only.v1",
            ["limits"] = new JsonObject
            {
                ["maximumAttempts"] = selectedLimits.MaximumAttempts,
                ["maximumContextReferences"] = selectedLimits.MaximumContextReferences,
                ["maximumContextUtf8Bytes"] = selectedLimits.MaximumContextUtf8Bytes,
                ["maximumEvidenceReferences"] = selectedLimits.MaximumEvidenceReferences,
                ["maximumEvidenceUtf8Bytes"] = selectedLimits.MaximumEvidenceUtf8Bytes,
                ["maximumProviderRequests"] = selectedLimits.MaximumProviderRequests,
                ["maximumToolRounds"] = selectedLimits.MaximumToolRounds,
                ["maximumToolCalls"] = selectedLimits.MaximumToolCalls,
                ["maximumInputTokens"] = selectedLimits.MaximumInputTokens,
                ["maximumUncachedInputTokens"] = selectedLimits.MaximumUncachedInputTokens,
                ["maximumOutputTokens"] = selectedLimits.MaximumOutputTokens,
                ["maximumCostMicrounits"] = selectedLimits.MaximumCostMicrounits,
                ["maximumElapsedMilliseconds"] = maximumElapsedMillisecondsOverride
                    ?? selectedLimits.MaximumElapsedMilliseconds,
            },
        };
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    private string? ComponentName(ComponentClassification component)
    {
        if (component.ComponentKind is ComponentKind.Return or ComponentKind.Value)
        {
            return null;
        }

        return observations.ObservationSet!.Observations.Single(item =>
            item.Subject.ParentSymbolRef == component.ParentSymbolRef
            && item.Subject.ComponentKind == component.ComponentKind
            && item.Subject.ComponentIdentity == component.Identity)
            .Declarations.Select(item => item.ComponentLocalName)
            .FirstOrDefault(name => name is not null);
    }

    private static JsonObject Policy(string disposition, int maximumScalars) => new()
    {
        ["disposition"] = disposition,
        ["maximumScalars"] = maximumScalars,
    };

    private static JsonObject Symbol(SymbolRef symbol) => new()
    {
        ["compilationContextRef"] = symbol.CompilationContextRef,
        ["documentationCommentId"] = symbol.DocumentationCommentId,
    };

    private static JsonObject RepositoryLocator(string path, Utf16Span span) => new()
    {
        ["repository"] = new JsonObject
        {
            ["path"] = path,
            ["span"] = new JsonObject
            {
                ["start"] = span.Start,
                ["end"] = span.End,
            },
        },
    };

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string NormalizeCode(string value) => string.Concat(value
        .Select(character => char.IsAsciiLetterOrDigit(character) || character is ('.' or '-')
            ? char.ToLowerInvariant(character)
            : '-'));
}
