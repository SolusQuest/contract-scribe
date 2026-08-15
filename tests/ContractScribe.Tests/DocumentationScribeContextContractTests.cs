using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;

namespace ContractScribe.Tests;

public sealed class DocumentationScribeContextContractTests
{
    [Fact]
    public void ContextVocabularyIsClosedAndProviderObservationIsTelemetryOnly()
    {
        Assert.Equal(
            Enum.GetValues<DocumentationScribeContextAuthority>().Length,
            Enum.GetValues<DocumentationScribeContextAuthority>()
                .Select(DocumentationScribeContextVocabulary.GetId)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(
            "telemetry.provider-observation",
            DocumentationScribeContextVocabulary.GetId(
                DocumentationScribeContextAuthority.ProviderObservation));
        Assert.Throws<ArgumentException>(() =>
            DocumentationScribeContextValidation.CreateEvidenceFact(
                DocumentationScribeContextAuthority.ProviderObservation,
                DocumentationScribeContextRole.ProviderTelemetry,
                "provider-observation",
                "telemetry.provider",
                Commitment("docs/telemetry.txt", "observation"),
                "observation"));
    }

    [Fact]
    public void ContentIdentityIsDeterministicAndExcludesLoadCorrelation()
    {
        var request = ParseRequest();
        var first = Selection(request, '1');
        var second = Selection(request, '2');
        var firstFacts = Facts(first);
        var secondFacts = Facts(second);

        Assert.NotEqual(first.RepositoryContextRef, second.RepositoryContextRef);
        Assert.Equal(firstFacts.ContentIdentity, secondFacts.ContentIdentity);
        Assert.Equal(
            firstFacts.Instructions.Select(item => item.InstructionId),
            secondFacts.Instructions.Select(item => item.InstructionId));
        Assert.Equal(
            firstFacts.Evidence.Select(item => item.EvidenceId),
            secondFacts.Evidence.Select(item => item.EvidenceId));
    }

    [Fact]
    public void EvidenceIdentityBindsAuthoritySubjectKindLocatorCommitmentRangeAndRole()
    {
        var baseline = Evidence(
            DocumentationScribeContextAuthority.Source,
            DocumentationScribeContextRole.SourceDeclaration,
            "subject-a",
            "source.kind",
            "src/Widget.cs",
            "content-a",
            10,
            20);
        var variants = new[]
        {
            Evidence(
                DocumentationScribeContextAuthority.Test,
                DocumentationScribeContextRole.TestEvidence,
                "subject-a",
                "source.kind",
                "src/Widget.cs",
                "content-a",
                10,
                20),
            Evidence(
                DocumentationScribeContextAuthority.Source,
                DocumentationScribeContextRole.SourceDeclaration,
                "subject-b",
                "source.kind",
                "src/Widget.cs",
                "content-a",
                10,
                20),
            Evidence(
                DocumentationScribeContextAuthority.Source,
                DocumentationScribeContextRole.SourceDeclaration,
                "subject-a",
                "source.other",
                "src/Widget.cs",
                "content-a",
                10,
                20),
            Evidence(
                DocumentationScribeContextAuthority.Source,
                DocumentationScribeContextRole.SourceDeclaration,
                "subject-a",
                "source.kind",
                "src/Other.cs",
                "content-a",
                10,
                20),
            Evidence(
                DocumentationScribeContextAuthority.Source,
                DocumentationScribeContextRole.SourceDeclaration,
                "subject-a",
                "source.kind",
                "src/Widget.cs",
                "content-b",
                10,
                20),
            Evidence(
                DocumentationScribeContextAuthority.Source,
                DocumentationScribeContextRole.SourceDeclaration,
                "subject-a",
                "source.kind",
                "src/Widget.cs",
                "content-a",
                11,
                20),
        };

        Assert.All(variants, variant => Assert.NotEqual(baseline.EvidenceId, variant.EvidenceId));

        var truncatedCommitment = DocumentationScribeContextValidation.CreateSourceCommitment(
            "src/Widget.cs",
            baseline.Commitment.ContentSha256,
            baseline.Commitment.OriginalUtf8ByteCount + 1,
            baseline.Commitment.IncludedUtf8ByteCount,
            true,
            false);
        var truncated = DocumentationScribeContextValidation.CreateEvidenceFact(
            baseline.Authority,
            baseline.Role,
            baseline.SubjectId,
            baseline.KindId,
            truncatedCommitment,
            baseline.Content,
            baseline.Range!.Value.Start,
            baseline.Range.Value.End);
        Assert.NotEqual(baseline.EvidenceId, truncated.EvidenceId);
    }

    [Fact]
    public void NonTruncatedContentMustMatchItsFullSourceCommitment()
    {
        var commitment = Commitment("src/Widget.cs", "same-size-a");

        Assert.Throws<ArgumentException>(() =>
            DocumentationScribeContextValidation.CreateEvidenceFact(
                DocumentationScribeContextAuthority.Source,
                DocumentationScribeContextRole.SourceDeclaration,
                "subject-a",
                "source.kind",
                commitment,
                "same-size-b"));
    }

    [Fact]
    public void IdenticalFactsDeduplicateAndSameIdentityWithDifferentContentFails()
    {
        var request = ParseRequest();
        var selection = Selection(request, '1');
        var evidence = Evidence(
            DocumentationScribeContextAuthority.Source,
            DocumentationScribeContextRole.SourceDeclaration,
            "subject-a",
            "source.kind",
            "src/Widget.cs",
            "content-a",
            10,
            20);
        var deduplicated = DocumentationScribeContextValidation.CreateFacts(
            selection,
            [],
            [],
            [evidence, evidence],
            []);
        Assert.Single(deduplicated.Evidence);

        var constructor = typeof(DocumentationScribeEvidenceContextFact)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        var forged = (DocumentationScribeEvidenceContextFact)constructor.Invoke(
        [
            evidence.EvidenceId,
            evidence.Authority,
            evidence.Role,
            evidence.SubjectId,
            evidence.KindId,
            evidence.Range,
            evidence.Commitment,
            "different-content",
        ]);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DocumentationScribeContextValidation.CreateFacts(
                selection,
                [],
                [],
                [evidence, forged],
                []));
        Assert.Equal("context.identity-collision", exception.Message);
    }

    [Fact]
    public void InstructionRoutesRequireAcceptedOriginAndRejectCycles()
    {
        var request = ParseRequest();
        var selection = Selection(request, '1');
        var root = Instruction(
            DocumentationScribeContextRole.AgentEntrypoint,
            0,
            "AGENTS.md",
            "root");
        var nested = Instruction(
            DocumentationScribeContextRole.ScopedInstruction,
            1,
            "src/AGENTS.md",
            "nested");
        var forward = DocumentationScribeContextValidation.CreateInstructionRoute(
            root.InstructionId,
            nested.Commitment.Path,
            nested.Role,
            DocumentationScribeContextRouteSelection.DeterministicBootstrap,
            1,
            nested.Commitment);
        var reverse = DocumentationScribeContextValidation.CreateInstructionRoute(
            nested.InstructionId,
            root.Commitment.Path,
            DocumentationScribeContextRole.ScopedInstruction,
            DocumentationScribeContextRouteSelection.ScribeSelected,
            2,
            root.Commitment);

        Assert.Throws<ArgumentException>(() =>
            DocumentationScribeContextValidation.CreateFacts(
                selection,
                [root, nested],
                [],
                [],
                [forward, reverse]));

        var unknown = DocumentationScribeContextValidation.CreateInstructionRoute(
            "ctxinst-unknown",
            nested.Commitment.Path,
            nested.Role,
            DocumentationScribeContextRouteSelection.DeterministicBootstrap,
            1,
            nested.Commitment);
        Assert.Throws<ArgumentException>(() =>
            DocumentationScribeContextValidation.CreateFacts(
                selection,
                [root, nested],
                [],
                [],
                [unknown]));
    }

    [Fact]
    public void CursorScopeBindsEveryRequiredCommitmentAndCursorValueIsOpaque()
    {
        var request = ParseRequest();
        var selection = Selection(request, '1');
        var scope = DocumentationScribeContextValidation.CreateCursorScope(
            "tool.repository.read",
            Sha("request"),
            selection.RepositoryContextRef,
            selection.SymbolRef,
            "order.path-ordinal",
            10,
            Sha("commitments"));
        var changed = DocumentationScribeContextValidation.CreateCursorScope(
            "tool.repository.read",
            Sha("other-request"),
            selection.RepositoryContextRef,
            selection.SymbolRef,
            "order.path-ordinal",
            10,
            Sha("commitments"));

        Assert.NotEqual(scope, changed);
        Assert.True(DocumentationScribeContextCursor.TryParse(
            "ctxcur.abcdefghijklmnopqrstuvwxyz012345.abcdefghijklmnopqrstuvwxyz012345",
            out var cursor));
        Assert.Equal("ctxcur.<opaque>", cursor.ToString());
    }

    [Fact]
    public async Task CoreFactsFitTheAcceptedTypedPortWithoutGrantingRepositoryAuthority()
    {
        var request = ParseRequest();
        var facts = Facts(Selection(request, '1'));
        var scope = DocumentationScribeContextValidation.CreateCursorScope(
            "tool.synthetic",
            Sha("request"),
            facts.RepositoryContextRef,
            facts.SymbolRef,
            "order.synthetic",
            1,
            Sha("commitments"));
        var descriptor = new SyntheticDescriptor();
        var port = new SyntheticPort();
        var result = await Invoke(descriptor, port, new SyntheticRequest(facts, scope));

        Assert.Same(DocumentationScribeToolOutcome.Complete, result.Outcome);
        Assert.Equal(facts.ContentIdentity, result.ContentIdentity);
        Assert.DoesNotContain(
            typeof(DocumentationScribeContextFacts).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            method => method.Name.Contains("Read", StringComparison.Ordinal));
    }

    [Fact]
    public void PublicContextSurfaceCarriesNoAmbientOrMutableAuthority()
    {
        var assembly = typeof(DocumentationScribeContextFacts).Assembly;
        var contextTypes = assembly.GetExportedTypes()
            .Where(type => type.Name.Contains("DocumentationScribeContext", StringComparison.Ordinal)
                || type.Name.Contains("DocumentationScribeInstruction", StringComparison.Ordinal))
            .ToArray();
        var prohibited = new[]
        {
            typeof(FileInfo),
            typeof(DirectoryInfo),
            typeof(Stream),
            typeof(IServiceProvider),
            typeof(Delegate),
        };
        foreach (var type in contextTypes)
        {
            var publicTypes = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(property => property.PropertyType)
                .Concat(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Select(method => method.ReturnType))
                .Concat(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .SelectMany(method => method.GetParameters())
                    .Select(parameter => parameter.ParameterType));
            Assert.DoesNotContain(publicTypes, candidate => prohibited.Contains(candidate));
            Assert.DoesNotContain(
                new[] { "Snapshot", "Manifest", "Ledger", "Migration", "Compatibility", "Resume" },
                term => type.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Equal(
            ["Code", "Severity", "Stage"],
            typeof(DocumentationScribeContextDiagnostic)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            ["Category", "Code"],
            typeof(DocumentationScribeContextFailure)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ContentBearingToStringDoesNotExposeAuthorizedText()
    {
        const string marker = "credential-marker-never-log";
        var instruction = Instruction(
            DocumentationScribeContextRole.AgentEntrypoint,
            0,
            "AGENTS.md",
            marker);
        var evidence = Evidence(
            DocumentationScribeContextAuthority.Source,
            DocumentationScribeContextRole.SourceDeclaration,
            "subject-a",
            "source.kind",
            "src/Widget.cs",
            marker,
            1,
            2);

        Assert.DoesNotContain(marker, instruction.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, evidence.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../AGENTS.md")]
    [InlineData("/AGENTS.md")]
    [InlineData("C:/AGENTS.md")]
    [InlineData("src\\AGENTS.md")]
    [InlineData("src//AGENTS.md")]
    [InlineData("./AGENTS.md")]
    [InlineData("src/AGENTS.md.")]
    [InlineData("src/AGENTS.md ")]
    public void RepositoryPathsFailClosed(string path)
    {
        Assert.Throws<ArgumentException>(() =>
            DocumentationScribeContextValidation.NormalizeRepositoryPath(path));
    }

    private static DocumentationScribeContextFacts Facts(
        DocumentationScribeContextBootstrapSelection selection)
    {
        var instruction = Instruction(
            DocumentationScribeContextRole.AgentEntrypoint,
            0,
            "AGENTS.md",
            "root instruction");
        var project = DocumentationScribeContextValidation.CreateProjectFact(
            selection.InputIdentity,
            "net10.0",
            selection.CompilationContextRef,
            DocumentationScribeContextProjectRole.AuditRoot);
        var evidence = Evidence(
            DocumentationScribeContextAuthority.Source,
            DocumentationScribeContextRole.SourceDeclaration,
            selection.CompilationContextRef + "|" + selection.SymbolRef.DocumentationCommentId,
            "source.target-declaration",
            selection.SourceLocator.Path,
            "source content",
            selection.SourceLocator.Span!.Value.Start,
            selection.SourceLocator.Span.Value.End);
        return DocumentationScribeContextValidation.CreateFacts(
            selection,
            [instruction],
            [project],
            [evidence],
            []);
    }

    private static DocumentationScribeContextBootstrapSelection Selection(
        DocumentationScribeRequest request,
        char contextValue)
    {
        Assert.True(RepositoryContextRef.TryParse(
            "repoctx-" + new string(contextValue, 32),
            out var contextRef));
        var locator = Assert.IsType<RepositoryEvidenceLocator>(request.Target.SourceLocator);
        return DocumentationScribeContextValidation.CreateBootstrapSelection(
            contextRef,
            request.Context.InputIdentity,
            request.Context.TargetProfile,
            request.Target.SymbolRef,
            locator.Path,
            locator.Span!.Value.Start,
            locator.Span.Value.End,
            request.Target.SourceSha256);
    }

    private static DocumentationScribeInstructionContextFact Instruction(
        DocumentationScribeContextRole role,
        int depth,
        string path,
        string content) =>
        DocumentationScribeContextValidation.CreateInstructionFact(
            role,
            depth,
            Commitment(path, content),
            content);

    private static DocumentationScribeEvidenceContextFact Evidence(
        DocumentationScribeContextAuthority authority,
        DocumentationScribeContextRole role,
        string subject,
        string kind,
        string path,
        string content,
        int start,
        int end) =>
        DocumentationScribeContextValidation.CreateEvidenceFact(
            authority,
            role,
            subject,
            kind,
            Commitment(path, content),
            content,
            start,
            end);

    private static DocumentationScribeContextSourceCommitment Commitment(
        string path,
        string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return DocumentationScribeContextValidation.CreateSourceCommitment(
            path,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.Length,
            bytes.Length,
            false,
            false);
    }

    private static string Sha(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static DocumentationScribeRequest ParseRequest()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "fixtures",
            "documentation-scribe",
            "v1",
            "valid",
            "request.json");
        var parsed = DocumentationScribeValidation.ParseRequest(File.ReadAllBytes(path));
        Assert.True(parsed.IsValid, parsed.Failure?.Code);
        return Assert.IsType<DocumentationScribeRequest>(parsed.Request);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed record SyntheticRequest(
        DocumentationScribeContextFacts Facts,
        DocumentationScribeContextCursorScope Scope)
        : IDocumentationScribeToolRequest<SyntheticResult>;

    private sealed record SyntheticResult(
        DocumentationScribeToolOutcome Outcome,
        string ContentIdentity)
        : IDocumentationScribeToolResult;

    private sealed class SyntheticDescriptor
        : IDocumentationScribeToolDescriptor<SyntheticRequest, SyntheticResult>
    {
        public string OperationId => "synthetic.context-read";
    }

    private sealed class SyntheticPort
        : IDocumentationScribeToolPort<SyntheticRequest, SyntheticResult>
    {
        public ValueTask<SyntheticResult> InvokeAsync(
            SyntheticRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SyntheticResult(
                DocumentationScribeToolOutcome.Complete,
                request.Facts.ContentIdentity));
    }

    private static ValueTask<TResult> Invoke<TRequest, TResult>(
        IDocumentationScribeToolDescriptor<TRequest, TResult> descriptor,
        IDocumentationScribeToolPort<TRequest, TResult> port,
        TRequest request)
        where TRequest : IDocumentationScribeToolRequest<TResult>
        where TResult : IDocumentationScribeToolResult
    {
        _ = descriptor;
        return port.InvokeAsync(request, CancellationToken.None);
    }
}
