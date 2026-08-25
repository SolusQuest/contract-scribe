using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Agent.Runtime;
using ContractScribe.Cli;
using ContractScribe.Core;
using ContractScribe.Patching;
using ContractScribe.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ContractScribe.Tests;

public sealed class DocumentationScribeCompositionTests
{
    [Fact]
    public async Task Production_prepare_and_consume_preserve_the_exact_bound_outcome()
    {
        await using var fixture = await CompositionFixture.CreateAsync();
        var prepared = await PrepareAsync(fixture, new ProposalExchange(fixture.Request));
        Assert.True(prepared.IsProposalReady);
        var bound = Assert.IsType<DocumentationScribeValidatedRunOutcome>(prepared.M3Outcome);
        Assert.Same(bound.Request, prepared.M3Outcome!.Request);
        Assert.Same(bound.RunResult, prepared.M3Outcome.RunResult);
        Assert.DoesNotContain(
            typeof(IDocumentationScribePreparedOutcome).GetProperties(),
            property => property.PropertyType == typeof(DocumentationPatchRequest));
        Assert.True(prepared.GetType().IsNestedPrivate);

        var consumed = DocumentationScribeComposition.ConsumePreparedOutcome(fixture.SelectedAudit, prepared);
        Assert.Same(bound, consumed.M3Outcome);
        Assert.Same(bound.RunResult, consumed.RunResult);
        var patchRequest = Assert.IsType<DocumentationPatchRequest>(consumed.PatchRequest);
        Assert.NotNull(consumed.PatchOutcome);
        Assert.Equal(bound.Request.Context.RepositoryContextRef, patchRequest.Context.RepositoryContextRef);
        Assert.Equal(bound.Request.Context.InputIdentity, patchRequest.Context.InputIdentity);
        Assert.Equal(bound.Request.Context.TargetProfile, patchRequest.Context.TargetProfile);
        var block = Assert.Single(patchRequest.Blocks);
        Assert.Equal(bound.Request.Target.SymbolRef, block.SymbolRef);
        var boundLocator = Assert.IsType<RepositoryEvidenceLocator>(bound.Request.Target.SourceLocator);
        var patchLocator = Assert.IsType<DocumentationPatchRepositoryLocator>(block.Locator);
        Assert.Equal(boundLocator.Path, patchLocator.Path);
        Assert.Equal(boundLocator.Span, patchLocator.DeclarationSpan);
        Assert.Equal(bound.Request.Target.SourceSha256, patchLocator.OriginalFileSha256);
        Assert.Equal(DocumentationPatchEditKind.Insert, block.EditKind);
        Assert.Equal(
            bound.Request.Target.ApplicableComponents.Select(component => component.Identity),
            block.ApplicableComponents.Select(component => component.Identity));
        Assert.Equal(new[] { "evidence.source" }, patchRequest.ProvenanceCatalog.ToArray());
        var content = Assert.IsType<DocumentationPatchStructuredContent>(block.Content);
        Assert.Equal(new[] { "Runs the selected operation." }, content.SummaryLines.ToArray());

        var consumedAgain = DocumentationScribeComposition.ConsumePreparedOutcome(fixture.SelectedAudit, prepared);
        Assert.Equal(DocumentationScribeCompositionStatus.ProposalRejected, consumedAgain.Status);
        Assert.Equal("scribe.proposal.already-consumed", consumedAgain.Code);
        Assert.Same(bound, consumedAgain.M3Outcome);
        Assert.Null(consumedAgain.PatchRequest);
        Assert.Null(consumedAgain.PatchOutcome);

        foreach (var scenario in new (IDocumentationScribeModelExchange Exchange, DocumentationScribeCompositionStatus Status)[]
                 {
                     (new SkipExchange(), DocumentationScribeCompositionStatus.ProposalSkipped),
                     (new ProviderFailureExchange(), DocumentationScribeCompositionStatus.ProviderFailure),
                     (new ProtocolFailureExchange(), DocumentationScribeCompositionStatus.RuntimeFailure),
                 })
        {
            var closed = await PrepareAsync(fixture, scenario.Exchange);
            Assert.Equal(scenario.Status, closed.Status);
            var closedBound = Assert.IsType<DocumentationScribeValidatedRunOutcome>(closed.M3Outcome);
            var closedOutcome = DocumentationScribeComposition.ConsumePreparedOutcome(fixture.SelectedAudit, closed);
            Assert.Same(closedBound, closedOutcome.M3Outcome);
            Assert.Same(closedBound.Request, closedOutcome.M3Outcome!.Request);
            Assert.Same(closedBound.RunResult, closedOutcome.RunResult);
            Assert.Null(closedOutcome.PatchRequest);
            Assert.Null(closedOutcome.PatchOutcome);
        }

        var budgetBytes = WithLimit(fixture.RequestBytes, "maximumOutputTokens", 1);
        var budget = await DocumentationScribeComposition.PrepareAsync(
            fixture.SelectedAudit,
            budgetBytes,
            fixture.AttemptId,
            configuredAgentEntrypoint: null,
            RuntimeOptions(),
            new ObservedSkipExchange(new DocumentationScribeModelUsage(outputTokens: 2)));
        Assert.Equal(DocumentationScribeCompositionStatus.BudgetExhausted, budget.Status);
        Assert.NotNull(budget.M3Outcome);
        Assert.Null(DocumentationScribeComposition.ConsumePreparedOutcome(
            fixture.SelectedAudit, budget).PatchRequest);

        var timeoutBytes = WithLimit(fixture.RequestBytes, "maximumElapsedMilliseconds", 1);
        var timeout = await DocumentationScribeComposition.PrepareAsync(
            fixture.SelectedAudit,
            timeoutBytes,
            fixture.AttemptId,
            configuredAgentEntrypoint: null,
            RuntimeOptions(),
            new DelayedSkipExchange());
        Assert.Equal(DocumentationScribeCompositionStatus.Timeout, timeout.Status);
        Assert.NotNull(timeout.M3Outcome);
        Assert.Null(DocumentationScribeComposition.ConsumePreparedOutcome(
            fixture.SelectedAudit, timeout).PatchRequest);

        using var callerCancellation = new CancellationTokenSource();
        var cancelled = await PrepareAsync(
            fixture,
            new CancellingExchange(callerCancellation),
            callerCancellation.Token);
        Assert.Equal(DocumentationScribeCompositionStatus.Cancelled, cancelled.Status);
        Assert.NotNull(cancelled.M3Outcome);
        Assert.Null(DocumentationScribeComposition.ConsumePreparedOutcome(
            fixture.SelectedAudit, cancelled).PatchRequest);

        var execute = await DocumentationScribeComposition.ExecuteAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            configuredAgentEntrypoint: null,
            RuntimeOptions(),
            new SkipExchange());
        Assert.Equal(DocumentationScribeCompositionStatus.ProposalSkipped, execute.Status);
        Assert.NotNull(execute.M3Outcome);
        Assert.Null(execute.PatchRequest);
        Assert.Null(execute.PatchOutcome);
    }

    [Fact]
    public async Task Pre_agent_and_post_bind_closures_never_mint_patch_authority()
    {
        await using var fixture = await CompositionFixture.CreateAsync();
        var counting = new CountingExchange();
        var invalid = await DocumentationScribeComposition.PrepareAsync(
            fixture.SelectedAudit,
            "{}"u8.ToArray(),
            fixture.AttemptId,
            configuredAgentEntrypoint: null,
            RuntimeOptions(),
            counting);
        Assert.Equal(DocumentationScribeCompositionStatus.PreflightRejected, invalid.Status);
        Assert.Null(invalid.M3Outcome);
        Assert.False(invalid.IsProposalReady);
        Assert.Equal(0, counting.RequestCount);

        var original = await File.ReadAllTextAsync(fixture.SourcePath);
        var stalePrepared = await PrepareAsync(
            fixture,
            new MutatingProposalExchange(fixture.Request, fixture.SourcePath));
        var staleBound = Assert.IsType<DocumentationScribeValidatedRunOutcome>(stalePrepared.M3Outcome);
        Assert.Equal(DocumentationScribeCompositionStatus.PatchStale, stalePrepared.Status);
        var staleOutcome = DocumentationScribeComposition.ConsumePreparedOutcome(fixture.SelectedAudit, stalePrepared);
        Assert.Same(staleBound, staleOutcome.M3Outcome);
        Assert.Null(staleOutcome.PatchRequest);
        Assert.Null(staleOutcome.PatchOutcome);
        await File.WriteAllTextAsync(fixture.SourcePath, original, new UTF8Encoding(false));

        var sourceSubstitution = await PrepareAsync(fixture, new ProposalExchange(fixture.Request));
        var sourceBound = Assert.IsType<DocumentationScribeValidatedRunOutcome>(sourceSubstitution.M3Outcome);
        await File.AppendAllTextAsync(fixture.SourcePath, Environment.NewLine, new UTF8Encoding(false));
        var sourceRejected = DocumentationScribeComposition.ConsumePreparedOutcome(
            fixture.SelectedAudit,
            sourceSubstitution);
        Assert.Equal(DocumentationScribeCompositionStatus.PatchStale, sourceRejected.Status);
        Assert.Equal("scribe.patch.prepared-authority-mismatch", sourceRejected.Code);
        Assert.Same(sourceBound, sourceRejected.M3Outcome);
        Assert.Null(sourceRejected.PatchRequest);
        Assert.Null(sourceRejected.PatchOutcome);
        await File.WriteAllTextAsync(fixture.SourcePath, original, new UTF8Encoding(false));

        var selectionSubstitution = await PrepareAsync(fixture, new ProposalExchange(fixture.Request));
        var selectionBound = Assert.IsType<DocumentationScribeValidatedRunOutcome>(selectionSubstitution.M3Outcome);
        var selectionRejected = DocumentationScribeComposition.ConsumePreparedOutcome(
            fixture.NonMethodSelectedAudit,
            selectionSubstitution);
        Assert.Equal(DocumentationScribeCompositionStatus.PatchStale, selectionRejected.Status);
        Assert.Same(selectionBound, selectionRejected.M3Outcome);
        Assert.Null(selectionRejected.PatchRequest);
        Assert.Null(selectionRejected.PatchOutcome);

        var cancelledPrepared = await PrepareAsync(fixture, new ProposalExchange(fixture.Request));
        var cancelledBound = Assert.IsType<DocumentationScribeValidatedRunOutcome>(cancelledPrepared.M3Outcome);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = DocumentationScribeComposition.ConsumePreparedOutcome(
            fixture.SelectedAudit,
            cancelledPrepared,
            cancellation.Token);
        Assert.Equal(DocumentationScribeCompositionStatus.Cancelled, cancelled.Status);
        Assert.Same(cancelledBound, cancelled.M3Outcome);
        Assert.Null(cancelled.PatchRequest);
        Assert.Null(cancelled.PatchOutcome);
    }

    private static Task<IDocumentationScribePreparedOutcome> PrepareAsync(
        CompositionFixture fixture,
        IDocumentationScribeModelExchange exchange,
        CancellationToken cancellationToken = default) =>
        DocumentationScribeComposition.PrepareAsync(
            fixture.SelectedAudit,
            fixture.RequestBytes,
            fixture.AttemptId,
            configuredAgentEntrypoint: null,
            RuntimeOptions(),
            exchange,
            cancellationToken);

    private static DocumentationScribeRuntimeOptions RuntimeOptions() => new(
        "provider.synthetic.v1",
        "model.synthetic.v1",
        "scribe-protocol.v1");

    private sealed class ProposalExchange(DocumentationScribeRequest request) : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest modelRequest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(ProposalTerminal(request))]));
        }
    }

    private sealed class SkipExchange : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]));
    }

    private sealed class ProviderFailureExchange : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [],
                new DocumentationScribeModelFailure(DocumentationScribeModelFailureCode.PermanentUnavailable)));
    }

    private sealed class ObservedSkipExchange(DocumentationScribeModelUsage usage)
        : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())],
                usage: usage));
    }

    private sealed class DelayedSkipExchange : IDocumentationScribeModelExchange
    {
        public async ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(20, cancellationToken);
            return new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]);
        }
    }

    private sealed class CancellingExchange(CancellationTokenSource cancellation)
        : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled<DocumentationScribeModelResponse>(cancellationToken);
        }
    }

    private sealed class ProtocolFailureExchange : IDocumentationScribeModelExchange
    {
        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DocumentationScribeModelResponse(
                [new DocumentationScribeModelToolCall(
                    0,
                    "call.conflict",
                    DocumentationScribeRepositoryToolOperationIds.SearchText,
                    "{}"u8.ToArray())],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]));
    }

    private sealed class CountingExchange : IDocumentationScribeModelExchange
    {
        internal int RequestCount { get; private set; }

        public ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return ValueTask.FromResult(new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(SkipTerminal())]));
        }
    }

    private sealed class MutatingProposalExchange(
        DocumentationScribeRequest request,
        string sourcePath) : IDocumentationScribeModelExchange
    {
        public async ValueTask<DocumentationScribeModelResponse> SendAsync(
            DocumentationScribeModelRequest modelRequest,
            CancellationToken cancellationToken)
        {
            await File.AppendAllTextAsync(
                sourcePath,
                Environment.NewLine,
                new UTF8Encoding(false),
                cancellationToken);
            return new DocumentationScribeModelResponse(
                [],
                [new DocumentationScribeModelTerminalSubmission(ProposalTerminal(request))]);
        }
    }

    private static ReadOnlyMemory<byte> ProposalTerminal(DocumentationScribeRequest request)
    {
        var locator = Assert.IsType<RepositoryEvidenceLocator>(request.Target.SourceLocator);
        return JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["kind"] = "proposal",
            ["target"] = new JsonObject
            {
                ["repositoryContextRef"] = request.Context.RepositoryContextRef.Value,
                ["symbolRef"] = Symbol(request.Target.SymbolRef),
                ["sourceCommitment"] = new JsonObject
                {
                    ["locator"] = RepositoryLocator(locator.Path, locator.Span!.Value),
                    ["contentSha256"] = request.Target.SourceSha256,
                },
            },
            ["contentUnits"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "content.summary",
                    ["lines"] = new JsonArray("Runs the selected operation."),
                    ["claimCategoryId"] = "claim.purpose",
                    ["evidenceReferenceIds"] = new JsonArray("evidence.source"),
                },
            },
        });
    }

    private static ReadOnlyMemory<byte> SkipTerminal() =>
        "{\"kind\":\"skip\",\"reason\":\"scribe.skip.insufficient-evidence\",\"evidenceReferenceIds\":[]}"u8.ToArray();

    private static ReadOnlyMemory<byte> WithLimit(
        ReadOnlyMemory<byte> requestBytes,
        string name,
        int value)
    {
        var root = JsonNode.Parse(requestBytes.Span)!.AsObject();
        root["limits"]![name] = value;
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    private sealed class CompositionFixture : IAsyncDisposable
    {
        private CompositionFixture(
            string root,
            string sourcePath,
            LoadedRepositorySession session,
            DocumentationScribeSelectedAudit selectedAudit,
            DocumentationScribeSelectedAudit nonMethodSelectedAudit,
            ReadOnlyMemory<byte> requestBytes,
            DocumentationScribeRequest request,
            DocumentationScribeAttemptId attemptId)
        {
            Root = root;
            SourcePath = sourcePath;
            Session = session;
            SelectedAudit = selectedAudit;
            NonMethodSelectedAudit = nonMethodSelectedAudit;
            RequestBytes = requestBytes;
            Request = request;
            AttemptId = attemptId;
        }

        internal string Root { get; }
        internal string SourcePath { get; }
        internal LoadedRepositorySession Session { get; }
        internal DocumentationScribeSelectedAudit SelectedAudit { get; }
        internal DocumentationScribeSelectedAudit NonMethodSelectedAudit { get; }
        internal ReadOnlyMemory<byte> RequestBytes { get; }
        internal DocumentationScribeRequest Request { get; }
        internal DocumentationScribeAttemptId AttemptId { get; }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }

        internal static async Task<CompositionFixture> CreateAsync()
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            var root = Descendant(tempRoot, "contract-scribe-issue-138-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixtureRoot = Descendant(
                FindRepositoryRoot(),
                "tests", "fixtures", "documentation-scribe", "end-to-end");
            foreach (var file in Directory.EnumerateFiles(fixtureRoot))
            {
                File.Copy(file, Descendant(root, Path.GetFileName(file)));
            }

            LoadedRepositorySession? session = null;
            try
            {
                const string repositoryPath = "Fixture.cs";
                var sourcePath = Descendant(root, repositoryPath);
                var sourceText = await File.ReadAllTextAsync(sourcePath);
                var syntaxTree = CSharpSyntaxTree.ParseText(
                    sourceText,
                    CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
                    repositoryPath,
                    Encoding.UTF8);
                var compilation = CSharpCompilation.Create(
                    "Fixture",
                    [syntaxTree],
                    PlatformReferences,
                    new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary,
                        deterministic: true));
                var workspace = new AdhocWorkspace();
                var project = workspace.AddProject("Fixture", LanguageNames.CSharp);
                var loadedProject = new LoadedProject(
                    "Fixture.csproj",
                    "net10.0",
                    "fixture.net10.0",
                    LoadedProjectRole.AuditRoot,
                    [],
                    project,
                    compilation,
                    new Dictionary<SyntaxTree, LoadedSourceTree>(ReferenceEqualityComparer.Instance)
                    {
                        [syntaxTree] = new(
                            LoadedSourceKind.Repository,
                            repositoryPath,
                            new RepositoryPathResolver().PhysicalIdentity(root, sourcePath),
                            null),
                    });
                Assert.True(RepositoryContextRef.TryParse(
                    "repoctx-0123456789abcdef0123456789abcdef",
                    out var repositoryContextRef));
                session = new LoadedRepositorySession(
                    repositoryContextRef,
                    root,
                    "Fixture.csproj",
                    new ToolchainIdentity("test", "test", "test", "test"),
                    [loadedProject],
                    [],
                    workspace,
                    DocumentationScribeContextStableFileReader.ReadDirectoryIdentity(root));
                session.SealDocumentationPatchRepositoryPolicyForTests();
                var classified = new SymbolClassifier().ClassifySession(session, TargetProfile.ExternalApi);
                Assert.Equal(ClassificationRunStatus.Success, classified.Classification.Status);
                var classifications = classified.Classification.ClassificationSet!;
                var target = Assert.Single(
                    classifications.Targets,
                    candidate => candidate.SymbolRef.DocumentationCommentId.StartsWith(
                            "M:EndToEnd.Fixture.Run", StringComparison.Ordinal)
                        && candidate.SupportStatus == SupportStatus.Supported);
                var nonMethod = Assert.Single(
                    classifications.Targets,
                    candidate => candidate.SymbolRef.DocumentationCommentId == "T:EndToEnd.BaseFixture");
                var observed = new DocumentationObserver().Observe(classified);
                Assert.Equal(DocumentationObservationRunStatus.Success, observed.Status);
                var policy = PolicyConfigurationEvaluator.Parse(
                    "{\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\",\"defaultDecision\":\"required\"}"u8.ToArray())
                    .Document ?? throw new InvalidOperationException("policy");
                var extracted = new PolicyEvidenceExtractor().Extract(classified, observed, policy);
                var inputs = AuditInputAssembler.Assemble(classifications, policy, extracted);
                var audit = AuditAggregator.Aggregate(TargetProfile.ExternalApi, classifications, policy, inputs);
                using var auditJson = JsonDocument.Parse(AuditJson.Write(audit));
                var auditOutcome = Assert.Single(
                    auditJson.RootElement.GetProperty("results").EnumerateArray(),
                    row => row.GetProperty("classification") is { } classification
                        && classification.TryGetProperty("symbolRef", out var symbolRef)
                        && symbolRef.GetProperty("documentationCommentId").GetString()
                            == target.SymbolRef.DocumentationCommentId)
                    .GetProperty("auditOutcome").GetString()!;
                var authority = DocumentationScribeAuditAuthority.Create(
                    classified, observed, policy, inputs, audit);
                var selected = authority.Select(target);
                var nonMethodSelected = authority.Select(nonMethod);

                var symbol = Assert.Single(Microsoft.CodeAnalysis.DocumentationCommentId
                    .GetSymbolsForDeclarationId(target.SymbolRef.DocumentationCommentId, compilation));
                var syntaxReference = Assert.Single(symbol.DeclaringSyntaxReferences);
                var sourceSha256 = Sha256(File.ReadAllBytes(sourcePath));
                var bootstrapSelection = DocumentationScribeContextValidation.CreateBootstrapSelection(
                    session.RepositoryContextRef,
                    session.InputIdentity,
                    TargetProfile.ExternalApi,
                    target.SymbolRef,
                    repositoryPath,
                    syntaxReference.Span.Start,
                    syntaxReference.Span.End,
                    sourceSha256);
                var bootstrap = new DocumentationScribeContextBootstrapper().Bootstrap(classified, bootstrapSelection);
                Assert.True(bootstrap.Status is DocumentationScribeContextBootstrapStatus.Succeeded
                    or DocumentationScribeContextBootstrapStatus.Incomplete);
                var context = Assert.IsType<DocumentationScribeLoadedContext>(bootstrap.Context);
                var evidence = Assert.Single(context.Facts.Evidence, item => item.KindId == "source.target-declaration");
                var span = Assert.IsType<Utf16Span>(evidence.Range);
                var requestBytes = CreateRequest(
                    session, target, repositoryPath, span, sourceSha256, context, evidence, auditOutcome);
                var request = Assert.IsType<DocumentationScribeRequest>(
                    DocumentationScribeValidation.ParseRequest(requestBytes).Request);
                Assert.True(DocumentationScribeAttemptId.TryParse(
                    "scribe-attempt." + Guid.NewGuid().ToString("N"), out var attempt));
                return new CompositionFixture(
                    root, sourcePath, session, selected, nonMethodSelected, requestBytes, request, attempt);
            }
            catch
            {
                if (session is not null)
                {
                    await session.DisposeAsync();
                }

                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        private static ReadOnlyMemory<byte> CreateRequest(
            LoadedRepositorySession session,
            TargetClassification target,
            string sourcePath,
            Utf16Span targetSpan,
            string sourceSha256,
            DocumentationScribeLoadedContext context,
            DocumentationScribeEvidenceContextFact evidence,
            string auditOutcome)
        {
            var contextReferences = new JsonArray();
            foreach (var instruction in context.Facts.Instructions)
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
                    ["applicableComponents"] = new JsonArray(),
                },
                ["styleProfile"] = new JsonObject
                {
                    ["styleProfileId"] = "style.public-api.v1",
                    ["outputLanguageId"] = "language.en",
                    ["summary"] = Policy("required", 400),
                    ["remarks"] = Policy("forbidden", 400),
                    ["exceptions"] = Policy("forbidden", 400),
                    ["componentPolicies"] = new JsonArray(),
                    ["inheritDocDisposition"] = "forbidden",
                    ["allowedLiterals"] = new JsonArray(),
                    ["forbiddenLiterals"] = new JsonArray(),
                    ["claimPolicies"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["claimCategoryId"] = "claim.purpose",
                            ["completeEvidenceRequired"] = false,
                            ["allowedAuthorities"] = new JsonArray("authority.source-declaration"),
                        },
                    },
                    ["maximumContentUnits"] = 8,
                    ["maximumEvidenceRefsPerUnit"] = 4,
                },
                ["contextReferences"] = contextReferences,
                ["evidenceReferences"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["evidenceReferenceId"] = "evidence.source",
                        ["repositoryContextRef"] = session.RepositoryContextRef.Value,
                        ["subject"] = new JsonObject { ["symbolRef"] = Symbol(target.SymbolRef) },
                        ["kind"] = "evidence.source.declaration",
                        ["relation"] = "evidence.declares",
                        ["authority"] = "authority.source-declaration",
                        ["locator"] = RepositoryLocator(sourcePath, targetSpan),
                        ["contentSha256"] = evidence.Commitment.ContentSha256,
                        ["originalUtf8ByteCount"] = evidence.Commitment.OriginalUtf8ByteCount,
                        ["includedUtf8ByteCount"] = evidence.Commitment.IncludedUtf8ByteCount,
                        ["isTruncated"] = evidence.Commitment.IsTruncated,
                        ["claimCategoryIds"] = new JsonArray("claim.purpose"),
                    },
                },
                ["evidenceConflicts"] = new JsonArray(),
                ["toolPolicyId"] = "tool-policy.read-only.v1",
                ["limits"] = new JsonObject
                {
                    ["maximumAttempts"] = 2,
                    ["maximumContextReferences"] = 8,
                    ["maximumContextUtf8Bytes"] = 65536,
                    ["maximumEvidenceReferences"] = 32,
                    ["maximumEvidenceUtf8Bytes"] = 65536,
                    ["maximumProviderRequests"] = 8,
                    ["maximumToolRounds"] = 4,
                    ["maximumToolCalls"] = 16,
                    ["maximumInputTokens"] = 65536,
                    ["maximumUncachedInputTokens"] = 32768,
                    ["maximumOutputTokens"] = 8192,
                    ["maximumCostMicrounits"] = 5000000,
                    ["maximumElapsedMilliseconds"] = 120000,
                },
            };
            return JsonSerializer.SerializeToUtf8Bytes(root);
        }
    }

    private static ImmutableArray<MetadataReference> PlatformReferences { get; } =
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Platform assemblies are unavailable."))
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToImmutableArray<MetadataReference>();

    private static string Descendant(string root, params string[] parts)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (parts.Any(Path.IsPathRooted))
        {
            throw new ArgumentException("Path components must be relative.", nameof(parts));
        }

        var candidate = Path.GetFullPath(Path.Join([fullRoot, .. parts]));
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Path escaped its fixture root.");
        }

        return candidate;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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
}
