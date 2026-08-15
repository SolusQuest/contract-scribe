using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace ContractScribe.Roslyn;

public sealed record DocumentationPatchCandidateValidationFile
{
    public DocumentationPatchCandidateValidationFile(
        string repositoryPath,
        DocumentationPatchRepositoryEncoding encoding,
        ImmutableArray<byte> bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        if (bytes.IsDefault)
        {
            throw new ArgumentException("Candidate bytes must be initialized.", nameof(bytes));
        }

        RepositoryPath = repositoryPath;
        Encoding = encoding;
        Bytes = bytes;
    }

    public string RepositoryPath { get; }

    public DocumentationPatchRepositoryEncoding Encoding { get; }

    public ImmutableArray<byte> Bytes { get; }
}

internal sealed record DocumentationPatchValidatedSemanticInputFact(
    string RepositoryPath,
    string ProjectIdentity,
    string CompilationContextRef,
    DocumentationPatchSemanticInputRole Role,
    string LogicalPath,
    string CandidateSha256);

public sealed record DocumentationPatchCandidateValidationResult
{
    internal DocumentationPatchCandidateValidationResult(
        bool isValid,
        string? failureCode,
        int validatedCompilationContextCount,
        int validatedSemanticInputCount,
        int validatedGeneratedSourceCount,
        ImmutableArray<DocumentationPatchValidatedSemanticInputFact> validatedSemanticInputs)
    {
        IsValid = isValid;
        FailureCode = failureCode;
        ValidatedCompilationContextCount = validatedCompilationContextCount;
        ValidatedSemanticInputCount = validatedSemanticInputCount;
        ValidatedGeneratedSourceCount = validatedGeneratedSourceCount;
        ValidatedSemanticInputs = validatedSemanticInputs;
    }

    public bool IsValid { get; }

    public string? FailureCode { get; }

    public int ValidatedCompilationContextCount { get; }

    public int ValidatedSemanticInputCount { get; }

    public int ValidatedGeneratedSourceCount { get; }

    internal ImmutableArray<DocumentationPatchValidatedSemanticInputFact> ValidatedSemanticInputs
    {
        get;
    }
}

internal enum DocumentationPatchCandidateValidationCorruption
{
    None,
    ParseOptions,
    CompilationOptions,
    MetadataReferences,
    CompilationContext,
}

public enum DocumentationPatchSessionAuthorityStatus
{
    Available,
    RepositorySessionUnavailable,
    RepositoryContextMismatch,
    InputIdentityMismatch,
    TargetProfileMismatch,
}

public static class DocumentationPatchCandidateValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(false, false, true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(true, false, true);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static DocumentationPatchSessionAuthorityStatus PreflightSessionAuthority(
        ClassifiedRepositorySession session,
        DocumentationPatchContext context)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);

        if (!session.IsBoundToClassificationSession
            || session.Classification.Status != ClassificationRunStatus.Success
            || session.Classification.ClassificationSet is not { } classifications
            || session.RepositorySession.IsDisposed)
        {
            return DocumentationPatchSessionAuthorityStatus.RepositorySessionUnavailable;
        }

        var repository = session.RepositorySession;
        if (context.RepositoryContextRef != repository.RepositoryContextRef)
        {
            return DocumentationPatchSessionAuthorityStatus.RepositoryContextMismatch;
        }

        if (!string.Equals(
                context.InputIdentity,
                repository.InputIdentity,
                StringComparison.Ordinal))
        {
            return DocumentationPatchSessionAuthorityStatus.InputIdentityMismatch;
        }

        return context.TargetProfile != classifications.TargetProfile
            ? DocumentationPatchSessionAuthorityStatus.TargetProfileMismatch
            : DocumentationPatchSessionAuthorityStatus.Available;
    }

    public static DocumentationPatchCandidateValidationResult Validate(
        ClassifiedRepositorySession session,
        DocumentationPatchRepositoryBaseline baseline,
        IEnumerable<DocumentationPatchCandidateValidationFile> changedFiles,
        CancellationToken cancellationToken = default) => ValidateCore(
            session,
            baseline,
            changedFiles,
            DocumentationPatchCandidateValidationCorruption.None,
            cancellationToken);

    internal static DocumentationPatchCandidateValidationResult ValidateForTests(
        ClassifiedRepositorySession session,
        DocumentationPatchRepositoryBaseline baseline,
        IEnumerable<DocumentationPatchCandidateValidationFile> changedFiles,
        DocumentationPatchCandidateValidationCorruption corruption,
        CancellationToken cancellationToken = default) => ValidateCore(
            session,
            baseline,
            changedFiles,
            corruption,
            cancellationToken);

    private static DocumentationPatchCandidateValidationResult ValidateCore(
        ClassifiedRepositorySession session,
        DocumentationPatchRepositoryBaseline baseline,
        IEnumerable<DocumentationPatchCandidateValidationFile> changedFiles,
        DocumentationPatchCandidateValidationCorruption corruption,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(changedFiles);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!session.IsBoundToClassificationSession
                || session.Classification.Status != ClassificationRunStatus.Success
                || session.Classification.ClassificationSet is not { } originalClassification
                || !baseline.IsBoundTo(session.RepositorySession)
                || session.RepositorySession.IsDisposed)
            {
                return Invalid("patch.rejected.unsafe-change");
            }

            var files = changedFiles.ToImmutableArray();
            if (files.IsDefaultOrEmpty
                || files.Length > 512
                || files.Any(file => file is null
                    || file.Bytes.IsDefault
                    || !Enum.IsDefined(file.Encoding))
                || files.Select(file => file.RepositoryPath).Distinct(PathComparer).Count()
                    != files.Length)
            {
                return Invalid("patch.rejected.unsafe-change");
            }

            var candidateTexts = new Dictionary<string, CandidateText>(PathComparer);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryDecode(file, out var candidateText))
                {
                    return Invalid("patch.rejected.unsafe-change");
                }

                candidateTexts.Add(file.RepositoryPath, candidateText);
            }

            var affectedFacts = baseline.SemanticInputs
                .Where(fact => candidateTexts.ContainsKey(fact.RepositoryPath))
                .ToImmutableArray();
            if (affectedFacts.IsDefaultOrEmpty
                || candidateTexts.Keys.Any(path => !affectedFacts.Any(
                    fact => PathComparer.Equals(fact.RepositoryPath, path))))
            {
                return Invalid("patch.rejected.unsafe-change");
            }

            if (corruption == DocumentationPatchCandidateValidationCorruption.CompilationContext)
            {
                affectedFacts = affectedFacts.SetItem(
                    0,
                    new DocumentationPatchSemanticInputFact(
                        affectedFacts[0].RepositoryPath,
                        affectedFacts[0].ProjectIdentity,
                        "corrupted.compilation-context",
                        affectedFacts[0].Role,
                        affectedFacts[0].LogicalPath));
            }

            var observedFacts = EnumerateSemanticInputs(session.RepositorySession)
                .Where(fact => candidateTexts.ContainsKey(fact.RepositoryPath))
                .ToImmutableArray();
            if (!SemanticFactsEqual(affectedFacts, observedFacts))
            {
                return Invalid("patch.rejected.unsafe-change");
            }

            var projects = session.RepositorySession.Projects.ToImmutableArray();
            if (projects.IsDefaultOrEmpty
                || projects.Select(project => project.Project.Solution.Workspace)
                    .Distinct(ReferenceEqualityComparer.Instance).Count() != 1)
            {
                return Invalid("patch.rejected.unsafe-change");
            }

            var solution = projects[0].Project.Solution;
            var corruptedProject = projects[0].Project;
            if (corruption == DocumentationPatchCandidateValidationCorruption.ParseOptions)
            {
                var parseOptions = (CSharpParseOptions?)corruptedProject.ParseOptions
                    ?? throw new InvalidOperationException(
                        "Candidate parse options are unavailable.");
                solution = solution.WithProjectParseOptions(
                    corruptedProject.Id,
                    parseOptions.WithPreprocessorSymbols("CONTRACT_SCRIBE_CORRUPTED"));
            }
            else if (corruption
                == DocumentationPatchCandidateValidationCorruption.CompilationOptions)
            {
                var compilationOptions = (CSharpCompilationOptions?)corruptedProject.CompilationOptions
                    ?? throw new InvalidOperationException(
                        "Candidate compilation options are unavailable.");
                solution = solution.WithProjectCompilationOptions(
                    corruptedProject.Id,
                    compilationOptions.WithOverflowChecks(!compilationOptions.CheckOverflow));
            }
            else if (corruption
                == DocumentationPatchCandidateValidationCorruption.MetadataReferences)
            {
                solution = solution.WithProjectMetadataReferences(
                    corruptedProject.Id,
                    corruptedProject.MetadataReferences.Skip(1));
            }

            var representedFacts = new HashSet<SemanticFactKey>();
            foreach (var fact in affectedFacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var loaded = projects.SingleOrDefault(project =>
                    string.Equals(project.ProjectIdentity, fact.ProjectIdentity, StringComparison.Ordinal)
                    && string.Equals(
                        project.CompilationContextRef,
                        fact.CompilationContextRef,
                        StringComparison.Ordinal));
                var project = loaded is null ? null : solution.GetProject(loaded.Project.Id);
                if (project is null)
                {
                    return Invalid("patch.rejected.unsafe-change");
                }

                var text = candidateTexts[fact.RepositoryPath].SourceText;
                var document = FindDocument(
                    project,
                    session.RepositorySession.PhysicalRepositoryRoot,
                    fact);
                if (document is null)
                {
                    if (fact.Role != DocumentationPatchSemanticInputRole.Source
                        || HasWorkspaceSourceDocument(loaded!, fact))
                    {
                        return Invalid("patch.rejected.unsafe-change");
                    }

                    continue;
                }

                solution = fact.Role switch
                {
                    DocumentationPatchSemanticInputRole.Source => solution.WithDocumentText(
                        document.Id,
                        text,
                        PreservationMode.PreserveValue),
                    DocumentationPatchSemanticInputRole.AdditionalFile =>
                        solution.WithAdditionalDocumentText(
                            document.Id,
                            text,
                            PreservationMode.PreserveValue),
                    DocumentationPatchSemanticInputRole.AnalyzerConfig =>
                        solution.WithAnalyzerConfigDocumentText(
                            document.Id,
                            text,
                            PreservationMode.PreserveValue),
                    _ => throw new InvalidOperationException(
                        "The semantic-input role is not closed."),
                };
                TextDocument? replacedDocument = fact.Role switch
                {
                    DocumentationPatchSemanticInputRole.Source =>
                        solution.GetDocument(document.Id),
                    DocumentationPatchSemanticInputRole.AdditionalFile =>
                        solution.GetAdditionalDocument(document.Id),
                    DocumentationPatchSemanticInputRole.AnalyzerConfig =>
                        solution.GetAnalyzerConfigDocument(document.Id),
                    _ => null,
                };
                var replacedText = replacedDocument?.GetTextAsync(cancellationToken)
                    .GetAwaiter().GetResult();
                if (replacedText is null || !replacedText.ContentEquals(text))
                {
                    return Invalid("patch.rejected.unsafe-change");
                }

                representedFacts.Add(SemanticFactKey.From(fact));
            }

            var candidateProjects = ImmutableArray.CreateBuilder<LoadedProject>(projects.Length);
            var candidateGenerated = ImmutableArray.CreateBuilder<GeneratedSourceFact>();
            foreach (var loaded in projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidateProject = solution.GetProject(loaded.Project.Id);
                if (candidateProject is null)
                {
                    return Invalid("patch.rejected.unsafe-change");
                }

                if (!HasSameProjectConfiguration(loaded.Project, candidateProject))
                {
                    return Invalid("patch.rejected.unsafe-change");
                }

                var usesSyntheticCompilation = loaded.Project.Documents.Count() == 0
                    && loaded.SourceTrees.Values.Any(source =>
                        source.Kind == LoadedSourceKind.Repository);
                var candidateCompilation = usesSyntheticCompilation
                    ? loaded.Compilation
                    : candidateProject.GetCompilationAsync(cancellationToken)
                        .GetAwaiter().GetResult();
                if (candidateCompilation is null)
                {
                    return Invalid("patch.rejected.unsafe-change");
                }

                var generatedDocuments = candidateProject.GetSourceGeneratedDocumentsAsync(
                        cancellationToken)
                    .GetAwaiter().GetResult();
                foreach (var document in generatedDocuments)
                {
                    var tree = document.GetSyntaxTreeAsync(cancellationToken)
                        .GetAwaiter().GetResult();
                    if (tree is not null && candidateCompilation.ContainsSyntaxTree(tree))
                    {
                        candidateCompilation = candidateCompilation.RemoveSyntaxTrees(tree);
                    }
                }

                foreach (var originalGenerated in loaded.SourceTrees.Where(pair =>
                             pair.Value.Kind == LoadedSourceKind.SourceGenerator)
                         .Select(pair => pair.Key))
                {
                    if (candidateCompilation.ContainsSyntaxTree(originalGenerated))
                    {
                        candidateCompilation = candidateCompilation.RemoveSyntaxTrees(
                            originalGenerated);
                    }
                }

                var treeFacts = new Dictionary<SyntaxTree, LoadedSourceTree>(
                    ReferenceEqualityComparer.Instance);
                foreach (var document in candidateProject.Documents)
                {
                    var fact = FindSemanticFact(
                        baseline.SemanticInputs,
                        loaded,
                        session.RepositorySession.PhysicalRepositoryRoot,
                        document,
                        DocumentationPatchSemanticInputRole.Source);
                    var tree = document.GetSyntaxTreeAsync(cancellationToken)
                        .GetAwaiter().GetResult();
                    if (fact is null || tree is null)
                    {
                        continue;
                    }

                    treeFacts[tree] = new LoadedSourceTree(
                        LoadedSourceKind.Repository,
                        fact.RepositoryPath,
                        loaded.SourceTrees.Values.FirstOrDefault(source =>
                            source.Kind == LoadedSourceKind.Repository
                            && PathComparer.Equals(source.RepositoryPath, fact.RepositoryPath))
                            ?.PhysicalSourceIdentity,
                        null);
                }

                foreach (var originalPair in loaded.SourceTrees.Where(pair =>
                             pair.Value.Kind == LoadedSourceKind.Repository
                             && pair.Value.RepositoryPath is { } path
                             && !HasWorkspaceSourceDocument(loaded, path)))
                {
                    var originalTree = originalPair.Key;
                    var repositoryPath = originalPair.Value.RepositoryPath!;
                    var replacement = candidateTexts.TryGetValue(repositoryPath, out var candidateText)
                        ? CSharpSyntaxTree.ParseText(
                            candidateText.Text,
                            (CSharpParseOptions?)originalTree.Options,
                            originalTree.FilePath,
                            cancellationToken: cancellationToken)
                        : originalTree;
                    if (candidateCompilation.ContainsSyntaxTree(originalTree))
                    {
                        candidateCompilation = candidateCompilation.ReplaceSyntaxTree(
                            originalTree,
                            replacement);
                    }
                    else if (!candidateCompilation.ContainsSyntaxTree(replacement))
                    {
                        candidateCompilation = candidateCompilation.AddSyntaxTrees(replacement);
                    }

                    treeFacts[replacement] = originalPair.Value;
                    if (candidateTexts.ContainsKey(repositoryPath))
                    {
                        representedFacts.Add(new SemanticFactKey(
                            repositoryPath,
                            loaded.ProjectIdentity,
                            loaded.CompilationContextRef,
                            DocumentationPatchSemanticInputRole.Source,
                            repositoryPath));
                    }
                }

                foreach (var toolPair in loaded.SourceTrees.Where(pair =>
                             pair.Value.Kind == LoadedSourceKind.ToolGenerated))
                {
                    var originalTree = toolPair.Key;
                    if (candidateCompilation.ContainsSyntaxTree(originalTree))
                    {
                        candidateCompilation = candidateCompilation.RemoveSyntaxTrees(
                            originalTree);
                    }

                    var restored = CSharpSyntaxTree.ParseText(
                        originalTree.GetText(cancellationToken),
                        (CSharpParseOptions?)originalTree.Options,
                        originalTree.FilePath,
                        cancellationToken: cancellationToken);
                    candidateCompilation = candidateCompilation.AddSyntaxTrees(restored);
                    treeFacts[restored] = toolPair.Value;
                    if (toolPair.Value.GeneratedSource is { } fact)
                    {
                        candidateGenerated.Add(fact);
                    }
                }

                var generatorFacts = RunGeneratorsOnce(
                    candidateProject,
                    candidateCompilation,
                    loaded,
                    cancellationToken,
                    out var finalCompilation,
                    out var generatedTrees,
                    out var generatorDiagnostics);
                if (generatorDiagnostics.Any(diagnostic =>
                        diagnostic.Severity == DiagnosticSeverity.Error)
                    || !GeneratorFactsEqual(
                    session.RepositorySession.GeneratedSources.Where(fact =>
                        string.Equals(
                            fact.CompilationContextRef,
                            loaded.CompilationContextRef,
                            StringComparison.Ordinal)
                        && fact.ProducerId.StartsWith("sgp.", StringComparison.Ordinal)),
                    generatorFacts))
                {
                    return Invalid("patch.rejected.unsafe-change");
                }

                candidateGenerated.AddRange(generatorFacts);
                foreach (var pair in generatedTrees)
                {
                    treeFacts[pair.Tree] = new LoadedSourceTree(
                        LoadedSourceKind.SourceGenerator,
                        null,
                        null,
                        pair.Fact);
                }

                if (!ValidateSyntaxAndTokens(
                        loaded,
                        finalCompilation,
                        treeFacts,
                        candidateTexts.Keys,
                        cancellationToken)
                    || !SymbolProjection(loaded.Compilation, cancellationToken).SequenceEqual(
                        SymbolProjection(finalCompilation, cancellationToken),
                        StringComparer.Ordinal))
                {
                    return Invalid("patch.rejected.unsafe-change");
                }

                candidateProjects.Add(new LoadedProject(
                    loaded.ProjectIdentity,
                    loaded.TargetFramework,
                    loaded.CompilationContextRef,
                    loaded.Role,
                    loaded.ProjectReferences,
                    candidateProject,
                    finalCompilation,
                    treeFacts));
            }

            if (!affectedFacts.Select(SemanticFactKey.From).ToHashSet()
                    .SetEquals(representedFacts))
            {
                return Invalid("patch.rejected.unsafe-change");
            }

            using var candidateSession = new LoadedRepositorySession(
                session.RepositorySession.RepositoryContextRef,
                session.RepositorySession.PhysicalRepositoryRoot,
                session.RepositorySession.InputIdentity,
                session.RepositorySession.Toolchain,
                candidateProjects.ToImmutable(),
                candidateGenerated.OrderBy(fact => fact.ProjectIdentity, StringComparer.Ordinal)
                    .ThenBy(fact => fact.CompilationContextRef, StringComparer.Ordinal)
                    .ThenBy(fact => fact.ProducerId, StringComparer.Ordinal)
                    .ThenBy(fact => fact.OutputId, StringComparer.Ordinal)
                    .ToImmutableArray(),
                NoOpDisposable.Instance);
            var candidateClassification = new SymbolClassifier().ClassifySession(
                candidateSession,
                originalClassification.TargetProfile,
                cancellationToken);
            if (candidateClassification.Classification.Status != ClassificationRunStatus.Success
                || candidateClassification.Classification.ClassificationSet is not { } candidateSet
                || !ClassificationProjection(originalClassification)
                    .SequenceEqual(ClassificationProjection(candidateSet), StringComparer.Ordinal)
                || !session.Classification.Diagnostics.Select(DiagnosticProjection)
                    .SequenceEqual(
                        candidateClassification.Classification.Diagnostics.Select(
                            DiagnosticProjection),
                        StringComparer.Ordinal))
            {
                return Invalid("patch.rejected.unsafe-change");
            }

            return new DocumentationPatchCandidateValidationResult(
                true,
                null,
                candidateProjects.Count,
                affectedFacts.Length,
                candidateGenerated.Count,
                affectedFacts.Select(fact => new DocumentationPatchValidatedSemanticInputFact(
                        fact.RepositoryPath,
                        fact.ProjectIdentity,
                        fact.CompilationContextRef,
                        fact.Role,
                        fact.LogicalPath,
                        candidateTexts[fact.RepositoryPath].Sha256))
                    .OrderBy(fact => fact.RepositoryPath, StringComparer.Ordinal)
                    .ThenBy(fact => fact.ProjectIdentity, StringComparer.Ordinal)
                    .ThenBy(fact => fact.CompilationContextRef, StringComparer.Ordinal)
                    .ThenBy(fact => fact.Role)
                    .ThenBy(fact => fact.LogicalPath, StringComparer.Ordinal)
                    .ToImmutableArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or DecoderFallbackException
            or EncoderFallbackException)
        {
            return Invalid("patch.rejected.unsafe-change");
        }
    }

    private static DocumentationPatchCandidateValidationResult Invalid(string code) =>
        new(false, code, 0, 0, 0, []);

    private static bool HasSameProjectConfiguration(Project original, Project candidate) =>
        Equals(original.ParseOptions, candidate.ParseOptions)
        && HasSameFixedCompilationOptions(original.CompilationOptions, candidate.CompilationOptions)
        && original.ProjectReferences.SequenceEqual(candidate.ProjectReferences)
        && original.MetadataReferences.SequenceEqual(candidate.MetadataReferences)
        && original.AnalyzerReferences.SequenceEqual(candidate.AnalyzerReferences);

    private static bool HasSameFixedCompilationOptions(
        CompilationOptions? original,
        CompilationOptions? candidate)
    {
        if (Equals(original, candidate))
        {
            return true;
        }

        // Replacing an AnalyzerConfigDocument makes Roslyn rebuild this provider even when
        // the fixed compiler options are unchanged. Its observable effects are validated by
        // the candidate compilation, generator, diagnostic, symbol, and classifier checks.
        return original is CSharpCompilationOptions originalCSharp
            && candidate is CSharpCompilationOptions candidateCSharp
            && Equals(
                originalCSharp.WithSyntaxTreeOptionsProvider(null),
                candidateCSharp.WithSyntaxTreeOptionsProvider(null));
    }

    private static bool TryDecode(
        DocumentationPatchCandidateValidationFile file,
        out CandidateText result)
    {
        try
        {
            var bytes = file.Bytes.AsSpan();
            var (text, encoding) = file.Encoding switch
            {
                DocumentationPatchRepositoryEncoding.Utf8
                    when !HasBom(bytes) => (StrictUtf8.GetString(bytes), (Encoding)StrictUtf8),
                DocumentationPatchRepositoryEncoding.Utf8Bom
                    when HasPrefix(bytes, 0xef, 0xbb, 0xbf) =>
                    (StrictUtf8.GetString(bytes[3..]), (Encoding)StrictUtf8),
                DocumentationPatchRepositoryEncoding.Utf16LittleEndianBom
                    when HasPrefix(bytes, 0xff, 0xfe) =>
                    (StrictUtf16Le.GetString(bytes[2..]), (Encoding)StrictUtf16Le),
                DocumentationPatchRepositoryEncoding.Utf16BigEndianBom
                    when HasPrefix(bytes, 0xfe, 0xff) =>
                    (StrictUtf16Be.GetString(bytes[2..]), (Encoding)StrictUtf16Be),
                _ => throw new DecoderFallbackException(),
            };
            result = new CandidateText(
                text,
                SourceText.From(text, encoding),
                encoding,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            return true;
        }
        catch (DecoderFallbackException)
        {
            result = default!;
            return false;
        }
    }

    private static bool HasBom(ReadOnlySpan<byte> bytes) =>
        HasPrefix(bytes, 0xef, 0xbb, 0xbf)
        || HasPrefix(bytes, 0xff, 0xfe)
        || HasPrefix(bytes, 0xfe, 0xff);

    private static bool HasPrefix(
        ReadOnlySpan<byte> bytes,
        byte first,
        byte second,
        byte? third = null) =>
        bytes.Length >= (third.HasValue ? 3 : 2)
        && bytes[0] == first
        && bytes[1] == second
        && (!third.HasValue || bytes[2] == third.Value);

    private static ImmutableArray<DocumentationPatchSemanticInputFact> EnumerateSemanticInputs(
        LoadedRepositorySession session)
    {
        var result = ImmutableArray.CreateBuilder<DocumentationPatchSemanticInputFact>();
        foreach (var loaded in session.Projects)
        {
            var represented = new HashSet<string>(PathComparer);
            var repositorySources = loaded.SourceTrees.Values
                .Where(source => source.Kind == LoadedSourceKind.Repository
                    && source.RepositoryPath is not null)
                .Select(source => source.RepositoryPath!)
                .ToHashSet(PathComparer);
            foreach (var document in loaded.Project.Documents)
            {
                if (TryRepositoryPath(session.PhysicalRepositoryRoot, document.FilePath, out var path)
                    && repositorySources.Contains(path))
                {
                    represented.Add(path);
                    result.Add(CreateFact(loaded, document, path,
                        DocumentationPatchSemanticInputRole.Source));
                }
            }

            foreach (var path in repositorySources.Except(represented, PathComparer))
            {
                result.Add(new DocumentationPatchSemanticInputFact(
                    path,
                    loaded.ProjectIdentity,
                    loaded.CompilationContextRef,
                    DocumentationPatchSemanticInputRole.Source,
                    path));
            }

            AddDocuments(result, session.PhysicalRepositoryRoot, loaded,
                loaded.Project.AdditionalDocuments, DocumentationPatchSemanticInputRole.AdditionalFile);
            AddDocuments(result, session.PhysicalRepositoryRoot, loaded,
                loaded.Project.AnalyzerConfigDocuments, DocumentationPatchSemanticInputRole.AnalyzerConfig);
        }

        return result.Distinct().OrderBy(fact => fact.RepositoryPath, StringComparer.Ordinal)
            .ThenBy(fact => fact.ProjectIdentity, StringComparer.Ordinal)
            .ThenBy(fact => fact.CompilationContextRef, StringComparer.Ordinal)
            .ThenBy(fact => fact.Role)
            .ThenBy(fact => fact.LogicalPath, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static void AddDocuments(
        ImmutableArray<DocumentationPatchSemanticInputFact>.Builder result,
        string root,
        LoadedProject loaded,
        IEnumerable<TextDocument> documents,
        DocumentationPatchSemanticInputRole role)
    {
        foreach (var document in documents)
        {
            if (TryRepositoryPath(root, document.FilePath, out var path))
            {
                result.Add(CreateFact(loaded, document, path, role));
            }
        }
    }

    private static DocumentationPatchSemanticInputFact CreateFact(
        LoadedProject loaded,
        TextDocument document,
        string path,
        DocumentationPatchSemanticInputRole role) =>
        new(
            path,
            loaded.ProjectIdentity,
            loaded.CompilationContextRef,
            role,
            LogicalPath(document));

    private static bool TryRepositoryPath(string root, string? filePath, out string path)
    {
        if (filePath is null)
        {
            path = string.Empty;
            return false;
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(filePath);
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!normalizedPath.StartsWith(prefix, comparison))
        {
            path = string.Empty;
            return false;
        }

        path = Path.GetRelativePath(normalizedRoot, normalizedPath).Replace('\\', '/');
        return true;
    }

    private static bool SemanticFactsEqual(
        IEnumerable<DocumentationPatchSemanticInputFact> left,
        IEnumerable<DocumentationPatchSemanticInputFact> right) =>
        left.Select(SemanticFactKey.From).Order().SequenceEqual(
            right.Select(SemanticFactKey.From).Order());

    private static TextDocument? FindDocument(
        Project project,
        string repositoryRoot,
        DocumentationPatchSemanticInputFact fact)
    {
        IEnumerable<TextDocument> documents = fact.Role switch
        {
            DocumentationPatchSemanticInputRole.Source => project.Documents,
            DocumentationPatchSemanticInputRole.AdditionalFile => project.AdditionalDocuments,
            DocumentationPatchSemanticInputRole.AnalyzerConfig => project.AnalyzerConfigDocuments,
            _ => [],
        };
        return documents.SingleOrDefault(document =>
            TryRepositoryPath(repositoryRoot, document.FilePath, out var repositoryPath)
            && PathComparer.Equals(repositoryPath, fact.RepositoryPath)
            && string.Equals(LogicalPath(document), fact.LogicalPath, StringComparison.Ordinal));
    }

    private static DocumentationPatchSemanticInputFact? FindSemanticFact(
        IEnumerable<DocumentationPatchSemanticInputFact> facts,
        LoadedProject loaded,
        string repositoryRoot,
        TextDocument document,
        DocumentationPatchSemanticInputRole role) =>
        facts.SingleOrDefault(fact => fact.Role == role
            && string.Equals(fact.ProjectIdentity, loaded.ProjectIdentity, StringComparison.Ordinal)
            && string.Equals(
                fact.CompilationContextRef,
                loaded.CompilationContextRef,
                StringComparison.Ordinal)
            && TryRepositoryPath(repositoryRoot, document.FilePath, out var repositoryPath)
            && PathComparer.Equals(fact.RepositoryPath, repositoryPath)
            && string.Equals(fact.LogicalPath, LogicalPath(document), StringComparison.Ordinal));

    private static string LogicalPath(TextDocument document) => document.Folders.Count == 0
        ? document.Name
        : string.Join('/', document.Folders.Append(document.Name));

    private static bool HasWorkspaceSourceDocument(
        LoadedProject loaded,
        DocumentationPatchSemanticInputFact fact) =>
        loaded.Project.Documents.Any(document =>
        {
            var tree = document.GetSyntaxTreeAsync().GetAwaiter().GetResult();
            return tree is not null
                && loaded.SourceTrees.TryGetValue(tree, out var source)
                && source.Kind == LoadedSourceKind.Repository
                && PathComparer.Equals(source.RepositoryPath, fact.RepositoryPath)
                && string.Equals(LogicalPath(document), fact.LogicalPath, StringComparison.Ordinal);
        });

    private static bool HasWorkspaceSourceDocument(LoadedProject loaded, string path) =>
        loaded.Project.Documents.Any(document =>
        {
            var tree = document.GetSyntaxTreeAsync().GetAwaiter().GetResult();
            return tree is not null
                && loaded.SourceTrees.TryGetValue(tree, out var source)
                && source.Kind == LoadedSourceKind.Repository
                && PathComparer.Equals(source.RepositoryPath, path);
        });

    private static ImmutableArray<GeneratedSourceFact> RunGeneratorsOnce(
        Project project,
        Compilation cleanCompilation,
        LoadedProject loaded,
        CancellationToken cancellationToken,
        out Compilation outputCompilation,
        out ImmutableArray<GeneratedTreeFact> trees,
        out ImmutableArray<Diagnostic> diagnostics)
    {
        var generators = project.AnalyzerReferences
            .SelectMany(reference => reference.GetGenerators(LanguageNames.CSharp))
            .ToImmutableArray();
        if (generators.IsDefaultOrEmpty)
        {
            outputCompilation = cleanCompilation;
            trees = [];
            diagnostics = [];
            return [];
        }

        var parseOptions = project.ParseOptions as CSharpParseOptions
            ?? throw new InvalidOperationException("Candidate parse options are unavailable.");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators,
            project.AnalyzerOptions.AdditionalFiles,
            parseOptions,
            project.AnalyzerOptions.AnalyzerConfigOptionsProvider);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            cleanCompilation,
            out outputCompilation,
            out diagnostics,
            cancellationToken);
        var identities = new GeneratedIdentityHasher(memory => SHA256.HashData(memory.Span));
        var facts = ImmutableArray.CreateBuilder<GeneratedSourceFact>();
        var treeFacts = ImmutableArray.CreateBuilder<GeneratedTreeFact>();
        foreach (var result in driver.GetRunResult().Results)
        {
            if (result.Exception is not null)
            {
                throw new InvalidOperationException("Candidate generator execution failed.");
            }

            var type = result.Generator.GetGeneratorType();
            var assemblyName = type.Assembly.GetName();
            if (string.IsNullOrWhiteSpace(type.FullName)
                || string.IsNullOrWhiteSpace(assemblyName.Name))
            {
                throw new InvalidOperationException("Candidate generator identity is unavailable.");
            }

            var assemblyIdentity = new AssemblyIdentity(
                assemblyName.Name,
                assemblyName.Version,
                assemblyName.CultureName,
                assemblyName.GetPublicKeyToken().ToImmutableArray(),
                hasPublicKey: false).GetDisplayName(fullKey: false);
            var producerId = "sgp." + identities.Hash(
                "contract-scribe/sgp/v1",
                type.FullName,
                assemblyIdentity);
            foreach (var source in result.GeneratedSources)
            {
                var text = source.SourceText.ToString();
                var bytes = StrictUtf8.GetBytes(text);
                var fact = new GeneratedSourceFact(
                    loaded.ProjectIdentity,
                    loaded.CompilationContextRef,
                    producerId,
                    "sgo." + identities.Hash("contract-scribe/sgo/v1", source.HintName),
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    text);
                facts.Add(fact);
                treeFacts.Add(new GeneratedTreeFact(source.SyntaxTree, fact));
            }
        }

        trees = treeFacts.ToImmutable();
        return facts.ToImmutable();
    }

    private static bool GeneratorFactsEqual(
        IEnumerable<GeneratedSourceFact> left,
        IEnumerable<GeneratedSourceFact> right) =>
        left.OrderBy(GeneratedKey, StringComparer.Ordinal)
            .SequenceEqual(right.OrderBy(GeneratedKey, StringComparer.Ordinal));

    private static string GeneratedKey(GeneratedSourceFact fact) => string.Join(
        '\u001f',
        fact.ProjectIdentity,
        fact.CompilationContextRef,
        fact.ProducerId,
        fact.OutputId,
        fact.SourceSha256,
        fact.SourceText);

    private static bool ValidateSyntaxAndTokens(
        LoadedProject original,
        Compilation candidate,
        IReadOnlyDictionary<SyntaxTree, LoadedSourceTree> candidateFacts,
        IEnumerable<string> changedPaths,
        CancellationToken cancellationToken)
    {
        foreach (var path in changedPaths)
        {
            var originals = original.SourceTrees.Where(pair =>
                    pair.Value.Kind == LoadedSourceKind.Repository
                    && PathComparer.Equals(pair.Value.RepositoryPath, path))
                .Select(pair => pair.Key)
                .OrderBy(tree => tree.FilePath, StringComparer.Ordinal)
                .ToArray();
            var candidates = candidateFacts.Where(pair =>
                    pair.Value.Kind == LoadedSourceKind.Repository
                    && PathComparer.Equals(pair.Value.RepositoryPath, path))
                .Select(pair => pair.Key)
                .OrderBy(tree => tree.FilePath, StringComparer.Ordinal)
                .ToArray();
            if (originals.Length != candidates.Length)
            {
                return false;
            }

            for (var index = 0; index < originals.Length; index++)
            {
                if (!TokenProjection(originals[index], cancellationToken).SequenceEqual(
                        TokenProjection(candidates[index], cancellationToken),
                        StringComparer.Ordinal)
                    || !ParseDiagnosticProjection(originals[index], cancellationToken).SequenceEqual(
                        ParseDiagnosticProjection(candidates[index], cancellationToken),
                        StringComparer.Ordinal))
                {
                    return false;
                }
            }
        }

        return !candidate.GetParseDiagnostics(cancellationToken)
            .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error
                && !original.Compilation.GetParseDiagnostics(cancellationToken)
                    .Select(DiagnosticProjection)
                    .Contains(DiagnosticProjection(diagnostic), StringComparer.Ordinal));
    }

    private static ImmutableArray<string> TokenProjection(
        SyntaxTree tree,
        CancellationToken cancellationToken) =>
        tree.GetRoot(cancellationToken).DescendantTokens(descendIntoTrivia: false)
            .Select(token => token.RawKind.ToString(CultureInfo.InvariantCulture)
                + ":" + token.Text)
            .ToImmutableArray();

    private static ImmutableArray<string> ParseDiagnosticProjection(
        SyntaxTree tree,
        CancellationToken cancellationToken) =>
        tree.GetDiagnostics(cancellationToken).Select(DiagnosticProjection)
            .Order(StringComparer.Ordinal).ToImmutableArray();

    private static string DiagnosticProjection(Diagnostic diagnostic) => string.Join(
        '\u001f',
        diagnostic.Id,
        diagnostic.Severity,
        diagnostic.WarningLevel,
        diagnostic.GetMessage(CultureInfo.InvariantCulture));

    private static string DiagnosticProjection(ClassificationDiagnostic diagnostic) =>
        string.Join('\u001f', diagnostic.Stage, diagnostic.Code, diagnostic.Severity);

    private static ImmutableArray<string> SymbolProjection(
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        AddNamespace(compilation.Assembly.GlobalNamespace, result, cancellationToken);
        return result.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    internal static ImmutableArray<string> SymbolProjectionForTests(
        Compilation compilation,
        CancellationToken cancellationToken = default) =>
        SymbolProjection(compilation, cancellationToken);

    private static void AddNamespace(
        INamespaceSymbol @namespace,
        ImmutableArray<string>.Builder result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var member in @namespace.GetMembers())
        {
            if (member is INamespaceSymbol nested)
            {
                AddNamespace(nested, result, cancellationToken);
            }
            else if (member is INamedTypeSymbol type
                && type.Locations.Any(location => location.IsInSource))
            {
                AddSymbol(type, result, cancellationToken);
            }
        }
    }

    private static void AddSymbol(
        ISymbol symbol,
        ImmutableArray<string>.Builder result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!symbol.IsImplicitlyDeclared)
        {
            result.Add(SymbolProjection(symbol));
        }

        if (symbol is INamedTypeSymbol type)
        {
            foreach (var member in type.GetMembers().Where(member =>
                         member.Locations.Any(location => location.IsInSource)))
            {
                AddSymbol(member, result, cancellationToken);
            }
        }
    }

    private static string SymbolProjection(ISymbol symbol)
    {
        var typeDetails = symbol switch
        {
            INamedTypeSymbol type => NamedTypeProjection(type),
            IMethodSymbol method => MethodProjection(method),
            IPropertySymbol property => PropertyProjection(property),
            IEventSymbol @event => EventProjection(@event),
            IFieldSymbol field => "type=" + TypeProjection(field.Type)
                + ";const=" + (field.HasConstantValue
                    ? Convert.ToString(field.ConstantValue, CultureInfo.InvariantCulture)
                    : string.Empty)
                + ";readonly=" + field.IsReadOnly
                + ";volatile=" + field.IsVolatile
                + ";required=" + field.IsRequired
                + ";fixed=" + field.IsFixedSizeBuffer,
            IParameterSymbol parameter => ParameterProjection(parameter),
            ITypeParameterSymbol parameter => TypeParameterProjection(parameter),
            _ => string.Empty,
        };
        return string.Join(
            '\u001f',
            symbol.GetDocumentationCommentId() ?? string.Empty,
            symbol.Kind,
            symbol.ContainingSymbol?.GetDocumentationCommentId() ?? string.Empty,
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            symbol.DeclaredAccessibility,
            symbol.IsStatic,
            symbol.IsAbstract,
            symbol.IsVirtual,
            symbol.IsOverride,
            symbol.IsSealed,
            symbol.IsExtern,
            typeDetails,
            AttributesProjection(symbol.GetAttributes()));
    }

    private static string NamedTypeProjection(INamedTypeSymbol type) =>
        "type-kind=" + type.TypeKind
        + ";base=" + TypeProjection(type.BaseType)
        + ";interfaces="
        + string.Join(',', type.Interfaces.Select(TypeProjection).Order(StringComparer.Ordinal))
        + ";constraints=" + string.Join(',', type.TypeParameters.Select(TypeParameterProjection))
        + ";enum-underlying=" + TypeProjection(type.EnumUnderlyingType)
        + ";delegate-invoke=" + (type.DelegateInvokeMethod is null
            ? string.Empty
            : MethodShapeProjection(type.DelegateInvokeMethod))
        + ";record=" + type.IsRecord
        + ";readonly=" + type.IsReadOnly
        + ";ref-like=" + type.IsRefLikeType
        + ";implicit-components=" + string.Join(',', type.GetMembers()
            .Where(member => member.IsImplicitlyDeclared
                && member.Locations.Any(location => location.IsInSource))
            .Select(ImplicitComponentProjection)
            .Order(StringComparer.Ordinal));

    private static string MethodProjection(IMethodSymbol method) =>
        MethodShapeProjection(method)
        + ";overridden=" + SymbolIdentity(method.OverriddenMethod)
        + ";explicit=" + string.Join(',', method.ExplicitInterfaceImplementations
            .Select(InterfaceEndpointProjection).Order(StringComparer.Ordinal))
        + ";implemented=" + InterfaceImplementationProjection(method);

    private static string MethodShapeProjection(IMethodSymbol method) =>
        "kind=" + method.MethodKind
        + ";return=" + TypeProjection(method.ReturnType)
        + ";ref=" + method.RefKind
        + ";return-attributes=" + AttributesProjection(method.GetReturnTypeAttributes())
        + ";params=" + string.Join(',', method.Parameters.Select(ParameterProjection))
        + ";constraints=" + string.Join(',', method.TypeParameters.Select(TypeParameterProjection))
        + ";async=" + method.IsAsync
        + ";readonly=" + method.IsReadOnly
        + ";init-only=" + method.IsInitOnly
        + ";vararg=" + method.IsVararg
        + ";attributes=" + AttributesProjection(method.GetAttributes());

    private static string PropertyProjection(IPropertySymbol property) =>
        "type=" + TypeProjection(property.Type)
        + ";ref=" + property.RefKind
        + ";indexer=" + property.IsIndexer
        + ";required=" + property.IsRequired
        + ";params=" + string.Join(',', property.Parameters.Select(ParameterProjection))
        + ";get=" + AccessorProjection(property.GetMethod)
        + ";set=" + AccessorProjection(property.SetMethod)
        + ";overridden=" + SymbolIdentity(property.OverriddenProperty)
        + ";explicit=" + string.Join(',', property.ExplicitInterfaceImplementations
            .Select(InterfaceEndpointProjection).Order(StringComparer.Ordinal))
        + ";implemented=" + InterfaceImplementationProjection(property);

    private static string EventProjection(IEventSymbol @event) =>
        "type=" + TypeProjection(@event.Type)
        + ";add=" + AccessorProjection(@event.AddMethod)
        + ";remove=" + AccessorProjection(@event.RemoveMethod)
        + ";raise=" + AccessorProjection(@event.RaiseMethod)
        + ";overridden=" + SymbolIdentity(@event.OverriddenEvent)
        + ";explicit=" + string.Join(',', @event.ExplicitInterfaceImplementations
            .Select(InterfaceEndpointProjection).Order(StringComparer.Ordinal))
        + ";implemented=" + InterfaceImplementationProjection(@event);

    private static string AccessorProjection(IMethodSymbol? accessor) => accessor is null
        ? string.Empty
        : string.Join(
            ':',
            accessor.MethodKind,
            accessor.DeclaredAccessibility,
            accessor.IsStatic,
            accessor.IsAbstract,
            accessor.IsVirtual,
            accessor.IsOverride,
            accessor.IsSealed,
            accessor.IsExtern,
            MethodShapeProjection(accessor));

    private static string InterfaceImplementationProjection(ISymbol symbol)
    {
        if (symbol.ContainingType is not { } containingType)
        {
            return string.Empty;
        }

        return string.Join(',', containingType.AllInterfaces
            .SelectMany(@interface => @interface.GetMembers())
            .Where(member => SymbolEqualityComparer.Default.Equals(
                containingType.FindImplementationForInterfaceMember(member),
                symbol))
            .Select(InterfaceEndpointProjection)
            .Order(StringComparer.Ordinal));
    }

    private static string InterfaceEndpointProjection(ISymbol symbol) =>
        SymbolIdentity(symbol.OriginalDefinition) + "=>" + SymbolIdentity(symbol);

    private static string ImplicitComponentProjection(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => "method:" + MethodProjection(method),
        IPropertySymbol property => "property:" + PropertyProjection(property),
        IEventSymbol @event => "event:" + EventProjection(@event),
        IFieldSymbol field => "field:" + TypeProjection(field.Type),
        _ => symbol.Kind + ":" + SymbolIdentity(symbol),
    };

    private static string SymbolIdentity(ISymbol? symbol) => symbol is null
        ? string.Empty
        : symbol.GetDocumentationCommentId()
            ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string AttributesProjection(IEnumerable<AttributeData> attributes) =>
        string.Join(';', attributes.Select(attribute =>
                (attribute.AttributeClass?.GetDocumentationCommentId()
                    ?? attribute.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    ?? string.Empty)
                + "(" + string.Join(',', attribute.ConstructorArguments.Select(TypedConstantProjection)) + ")"
                + "{" + string.Join(',', attribute.NamedArguments.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => pair.Key + "=" + TypedConstantProjection(pair.Value))) + "}")
            .Order(StringComparer.Ordinal));

    private static string ParameterProjection(IParameterSymbol parameter) => string.Join(
        ':',
        parameter.Ordinal,
        parameter.Name,
        parameter.RefKind,
        parameter.IsParams,
        parameter.IsOptional,
        TypeProjection(parameter.Type),
        parameter.HasExplicitDefaultValue
            ? Convert.ToString(parameter.ExplicitDefaultValue, CultureInfo.InvariantCulture)
            : string.Empty,
        AttributesProjection(parameter.GetAttributes()));

    private static string TypeParameterProjection(ITypeParameterSymbol parameter) => string.Join(
        ':',
        parameter.Ordinal,
        parameter.Name,
        parameter.Variance,
        parameter.HasReferenceTypeConstraint,
        parameter.ReferenceTypeConstraintNullableAnnotation,
        parameter.HasValueTypeConstraint,
        parameter.HasUnmanagedTypeConstraint,
        parameter.HasNotNullConstraint,
        parameter.HasConstructorConstraint,
        string.Join(',', parameter.ConstraintTypes.Select(TypeProjection).Order(StringComparer.Ordinal)),
        AttributesProjection(parameter.GetAttributes()));

    private static string TypeProjection(ITypeSymbol? type) => type is null
        ? string.Empty
        : type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            + "?" + type.NullableAnnotation;

    private static string TypedConstantProjection(TypedConstant constant) =>
        constant.Kind == TypedConstantKind.Array
            ? "[" + string.Join(',', constant.Values.Select(TypedConstantProjection)) + "]"
            : TypeProjection(constant.Type) + "="
                + Convert.ToString(constant.Value, CultureInfo.InvariantCulture);

    private static ImmutableArray<string> ClassificationProjection(ClassificationSet set)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        result.Add("profile\u001f" + set.TargetProfile);
        result.AddRange(set.Targets.Select(target => string.Join(
            '\u001f',
            "target",
            target.SymbolRef.CompilationContextRef,
            target.SymbolRef.DocumentationCommentId,
            target.PrimaryKind,
            string.Join(',', target.Traits),
            target.Origin,
            target.SupportStatus,
            target.SkipReason)));
        result.AddRange(set.Components.Select(component => string.Join(
            '\u001f',
            "component",
            component.ParentSymbolRef.CompilationContextRef,
            component.ParentSymbolRef.DocumentationCommentId,
            component.ComponentKind,
            component.Identity,
            component.Origin,
            component.SupportStatus,
            component.SkipReason)));
        result.AddRange(set.Relations.Select(relation => string.Join(
            '\u001f',
            "relation",
            relation.RelationKind,
            relation.SourceSymbolRef.CompilationContextRef,
            relation.SourceSymbolRef.DocumentationCommentId,
            relation.TargetSymbolRef.CompilationContextRef,
            relation.TargetSymbolRef.DocumentationCommentId)));
        result.AddRange(set.Unresolved.Select(unresolved => string.Join(
            '\u001f',
            "unresolved",
            unresolved.CompilationContextRef,
            unresolved.Origin,
            unresolved.SupportStatus,
            unresolved.SkipReason,
            LocatorProjection(unresolved.CandidateLocator))));
        return result.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    private static string LocatorProjection(CandidateLocator locator) => locator switch
    {
        RepositoryCandidateLocator repository => "repository:" + repository.Path,
        GeneratedSourceCandidateLocator generated =>
            "source-generator:" + generated.GeneratorId + ":" + generated.HintNameId,
        ToolGeneratedCandidateLocator generated =>
            "tool-generated:" + generated.ProducerId + ":" + generated.OutputId,
        SyntheticCandidateLocator synthetic => "synthetic:" + synthetic.FixtureId,
        _ => locator.GetType().FullName ?? string.Empty,
    };

    private sealed record CandidateText(
        string Text,
        SourceText SourceText,
        Encoding Encoding,
        string Sha256);

    private sealed record GeneratedTreeFact(SyntaxTree Tree, GeneratedSourceFact Fact);

    private readonly record struct SemanticFactKey(
        string RepositoryPath,
        string ProjectIdentity,
        string CompilationContextRef,
        DocumentationPatchSemanticInputRole Role,
        string LogicalPath) : IComparable<SemanticFactKey>
    {
        public static SemanticFactKey From(DocumentationPatchSemanticInputFact fact) => new(
            fact.RepositoryPath,
            fact.ProjectIdentity,
            fact.CompilationContextRef,
            fact.Role,
            fact.LogicalPath);

        public int CompareTo(SemanticFactKey other)
        {
            var comparison = string.CompareOrdinal(RepositoryPath, other.RepositoryPath);
            if (comparison != 0) return comparison;
            comparison = string.CompareOrdinal(ProjectIdentity, other.ProjectIdentity);
            if (comparison != 0) return comparison;
            comparison = string.CompareOrdinal(CompilationContextRef, other.CompilationContextRef);
            if (comparison != 0) return comparison;
            comparison = Role.CompareTo(other.Role);
            return comparison != 0 ? comparison : string.CompareOrdinal(LogicalPath, other.LogicalPath);
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static NoOpDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
