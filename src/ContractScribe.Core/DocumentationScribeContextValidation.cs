using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace ContractScribe.Core;

public static class DocumentationScribeContextValidation
{
    private const int MaximumPathScalars = 512;
    private const int MaximumIdLength = 256;
    private const int MaximumConfiguredInstructionFiles = 64;
    private const int MaximumConfiguredInstructionDepth = 64;
    private const int MaximumConfiguredDeclarationReferences = 4096;
    private const int MaximumConfiguredDeclarationFiles = 1024;
    private const int MaximumConfiguredFileBytes = 16 * 1024 * 1024;
    private const int MaximumConfiguredTotalBytes = 32 * 1024 * 1024;
    private const int MaximumConfiguredElapsedMilliseconds = 30 * 60 * 1000;
    private const int MaximumPageSize = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly IReadOnlyDictionary<string, DocumentationScribeContextFailureCategory>
        FailureVocabulary = new Dictionary<string, DocumentationScribeContextFailureCategory>(
            StringComparer.Ordinal)
        {
            ["context.correlation.session"] = DocumentationScribeContextFailureCategory.Correlation,
            ["context.correlation.request"] = DocumentationScribeContextFailureCategory.Correlation,
            ["context.correlation.compilation"] = DocumentationScribeContextFailureCategory.Correlation,
            ["context.scope.target-unavailable"] = DocumentationScribeContextFailureCategory.AmbiguousScope,
            ["context.scope.symbol-ambiguous"] = DocumentationScribeContextFailureCategory.AmbiguousScope,
            ["context.scope.physical-alias"] = DocumentationScribeContextFailureCategory.AmbiguousScope,
            ["context.scope.not-unique"] = DocumentationScribeContextFailureCategory.AmbiguousScope,
            ["context.unsafe.source-alias"] = DocumentationScribeContextFailureCategory.UnsafeRepositoryObject,
            ["context.unsafe.physical-alias"] = DocumentationScribeContextFailureCategory.UnsafeRepositoryObject,
            ["context.unsafe.physical-identity"] = DocumentationScribeContextFailureCategory.UnsafeRepositoryObject,
            ["context.unsafe.repository-object"] = DocumentationScribeContextFailureCategory.UnsafeRepositoryObject,
            ["context.unsafe.path"] = DocumentationScribeContextFailureCategory.UnsafeRepositoryObject,
            ["context.stale.source-text"] = DocumentationScribeContextFailureCategory.Stale,
            ["context.stale.source-commitment"] = DocumentationScribeContextFailureCategory.Stale,
            ["context.stale.configured-entrypoint"] = DocumentationScribeContextFailureCategory.Stale,
            ["context.stale.repository-root"] = DocumentationScribeContextFailureCategory.Stale,
            ["context.stale.repository-object"] = DocumentationScribeContextFailureCategory.Stale,
            ["context.stale.publication"] = DocumentationScribeContextFailureCategory.Stale,
            ["context.invalid-encoding"] = DocumentationScribeContextFailureCategory.InvalidEncoding,
            ["context.identity-collision"] = DocumentationScribeContextFailureCategory.IdentityCollision,
            ["context.cursor.invalid"] = DocumentationScribeContextFailureCategory.InvalidCursor,
            ["context.budget.instruction-depth"] = DocumentationScribeContextFailureCategory.Internal,
            ["context.budget.instruction-files"] = DocumentationScribeContextFailureCategory.Internal,
            ["context.budget.declaration-references"] = DocumentationScribeContextFailureCategory.Internal,
            ["context.budget.declaration-files"] = DocumentationScribeContextFailureCategory.Internal,
            ["context.budget.inspected-source-bytes"] = DocumentationScribeContextFailureCategory.Internal,
            ["context.budget.file-bytes"] = DocumentationScribeContextFailureCategory.Internal,
            ["context.budget.total-bytes"] = DocumentationScribeContextFailureCategory.Internal,
            ["context.timeout.operation"] = DocumentationScribeContextFailureCategory.Internal,
            ["context.internal-error"] = DocumentationScribeContextFailureCategory.Internal,
        };

    private static readonly HashSet<string> DiagnosticStages = new(StringComparer.Ordinal)
    {
        "bootstrap.correlation",
        "bootstrap.scope-resolution",
        "bootstrap.instruction-discovery",
        "bootstrap.source-context",
        "bootstrap.cursor",
        "bootstrap.publication",
    };

    private static readonly HashSet<string> DiagnosticCodes = new(StringComparer.Ordinal)
    {
        "context.optional-absent",
        "context.source-truncated",
        "context.route-cycle-omitted",
        "context.limit-reached",
    };

    public static DocumentationScribeContextBootstrapLimits CreateProductionLimits() =>
        new(
            maximumInstructionFiles: 16,
            maximumInstructionDepth: 16,
            maximumInstructionFileUtf8Bytes: 256 * 1024,
            maximumDeclarationReferences: 256,
            maximumDeclarationFiles: 64,
            maximumInspectedSourceUtf8Bytes: 16 * 1024 * 1024,
            maximumSourceFileUtf8Bytes: 4 * 1024 * 1024,
            maximumIncludedSourceUtf8Bytes: 256 * 1024,
            maximumTotalContextUtf8Bytes: 2 * 1024 * 1024,
            maximumElapsedMilliseconds: 30_000);

    public static DocumentationScribeContextBootstrapLimits CreateLimits(
        int maximumInstructionFiles,
        int maximumInstructionDepth,
        int maximumInstructionFileUtf8Bytes,
        int maximumDeclarationReferences,
        int maximumDeclarationFiles,
        int maximumInspectedSourceUtf8Bytes,
        int maximumSourceFileUtf8Bytes,
        int maximumIncludedSourceUtf8Bytes,
        int maximumTotalContextUtf8Bytes,
        int maximumElapsedMilliseconds)
    {
        if (maximumInstructionFiles <= 0
            || maximumInstructionFiles > MaximumConfiguredInstructionFiles
            || maximumInstructionDepth <= 0
            || maximumInstructionDepth > MaximumConfiguredInstructionDepth
            || maximumInstructionFileUtf8Bytes <= 0
            || maximumInstructionFileUtf8Bytes > MaximumConfiguredFileBytes
            || maximumDeclarationReferences <= 0
            || maximumDeclarationReferences > MaximumConfiguredDeclarationReferences
            || maximumDeclarationFiles <= 0
            || maximumDeclarationFiles > MaximumConfiguredDeclarationFiles
            || maximumInspectedSourceUtf8Bytes <= 0
            || maximumInspectedSourceUtf8Bytes > MaximumConfiguredTotalBytes
            || maximumSourceFileUtf8Bytes <= 0
            || maximumSourceFileUtf8Bytes > MaximumConfiguredFileBytes
            || maximumIncludedSourceUtf8Bytes <= 0
            || maximumIncludedSourceUtf8Bytes > maximumSourceFileUtf8Bytes
            || maximumTotalContextUtf8Bytes <= 0
            || maximumTotalContextUtf8Bytes > MaximumConfiguredTotalBytes
            || maximumInstructionFileUtf8Bytes > maximumTotalContextUtf8Bytes
            || maximumIncludedSourceUtf8Bytes > maximumTotalContextUtf8Bytes
            || maximumElapsedMilliseconds <= 0
            || maximumElapsedMilliseconds > MaximumConfiguredElapsedMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumInstructionFiles),
                "Documentation Scribe context limits are invalid or exceed current bounds.");
        }

        return new DocumentationScribeContextBootstrapLimits(
            maximumInstructionFiles,
            maximumInstructionDepth,
            maximumInstructionFileUtf8Bytes,
            maximumDeclarationReferences,
            maximumDeclarationFiles,
            maximumInspectedSourceUtf8Bytes,
            maximumSourceFileUtf8Bytes,
            maximumIncludedSourceUtf8Bytes,
            maximumTotalContextUtf8Bytes,
            maximumElapsedMilliseconds);
    }

    public static DocumentationScribeContextBootstrapSelection CreateBootstrapSelection(
        RepositoryContextRef repositoryContextRef,
        string inputIdentity,
        TargetProfile targetProfile,
        SymbolRef symbolRef,
        string sourcePath,
        int sourceSpanStart,
        int sourceSpanEnd,
        string sourceSha256,
        string? configuredAgentEntrypoint = null,
        DocumentationScribeContextBootstrapLimits? limits = null)
    {
        var normalizedSource = NormalizeRepositoryPath(sourcePath);
        if (sourceSpanStart < 0 || sourceSpanEnd <= sourceSpanStart)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSpanStart));
        }

        return CreateBootstrapSelection(
            repositoryContextRef,
            inputIdentity,
            targetProfile,
            symbolRef,
            new RepositoryEvidenceLocator(
                normalizedSource,
                new Utf16Span(sourceSpanStart, sourceSpanEnd)),
            sourceSha256,
            configuredAgentEntrypoint,
            limits);
    }

    public static DocumentationScribeContextBootstrapSelection CreateBootstrapSelection(
        RepositoryContextRef repositoryContextRef,
        string inputIdentity,
        TargetProfile targetProfile,
        SymbolRef symbolRef,
        EvidenceLocator sourceLocator,
        string sourceSha256,
        string? configuredAgentEntrypoint = null,
        DocumentationScribeContextBootstrapLimits? limits = null)
    {
        if (repositoryContextRef == default)
        {
            throw new ArgumentException("A repository context reference is required.", nameof(repositoryContextRef));
        }

        var normalizedInput = NormalizeRepositoryPath(inputIdentity);
        if (!Enum.IsDefined(targetProfile))
        {
            throw new ArgumentOutOfRangeException(nameof(targetProfile));
        }

        ValidateSymbolRef(symbolRef);
        ArgumentNullException.ThrowIfNull(sourceLocator);
        var normalizedLocator = ValidateEvidenceLocator(sourceLocator);
        ValidateSha256(sourceSha256, nameof(sourceSha256));
        if (normalizedLocator is GeneratedOutputEvidenceLocator generated
            && !string.Equals(generated.SourceSha256, sourceSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Generated source selection hashes must match exactly.",
                nameof(sourceSha256));
        }
        var entrypoint = configuredAgentEntrypoint is null
            ? null
            : NormalizeRepositoryPath(configuredAgentEntrypoint);
        return new DocumentationScribeContextBootstrapSelection(
            repositoryContextRef,
            normalizedInput,
            targetProfile,
            symbolRef,
            normalizedLocator,
            sourceSha256,
            entrypoint,
            limits ?? CreateProductionLimits());
    }

    public static DocumentationScribeContextSourceCommitment CreateSourceCommitment(
        string path,
        string contentSha256,
        int originalUtf8ByteCount,
        int includedUtf8ByteCount,
        bool isTruncated,
        bool hasUtf8Bom,
        bool includedHasUtf8Bom = false)
    {
        var normalizedPath = NormalizeRepositoryPath(path);
        return CreateEvidenceSourceCommitment(
            new RepositoryEvidenceLocator(normalizedPath, null),
            contentSha256,
            originalUtf8ByteCount,
            includedUtf8ByteCount,
            isTruncated,
            hasUtf8Bom,
            includedHasUtf8Bom);
    }

    public static DocumentationScribeContextSourceCommitment CreateEvidenceSourceCommitment(
        EvidenceLocator locator,
        string contentSha256,
        int originalUtf8ByteCount,
        int includedUtf8ByteCount,
        bool isTruncated,
        bool hasUtf8Bom,
        bool includedHasUtf8Bom = false)
    {
        ArgumentNullException.ThrowIfNull(locator);
        var normalizedLocator = ValidateEvidenceLocator(locator);
        ValidateSha256(contentSha256, nameof(contentSha256));
        if (normalizedLocator is GeneratedOutputEvidenceLocator generated
            && !string.Equals(
                generated.SourceSha256,
                contentSha256,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A generated locator must commit to the same source bytes.",
                nameof(locator));
        }

        if (originalUtf8ByteCount < 0
            || includedUtf8ByteCount < 0
            || includedUtf8ByteCount > originalUtf8ByteCount
            || isTruncated != (includedUtf8ByteCount < originalUtf8ByteCount)
            || hasUtf8Bom && originalUtf8ByteCount < 3
            || includedHasUtf8Bom && (!hasUtf8Bom || includedUtf8ByteCount < 3)
            || !isTruncated && hasUtf8Bom != includedHasUtf8Bom)
        {
            throw new ArgumentOutOfRangeException(nameof(originalUtf8ByteCount));
        }

        return new DocumentationScribeContextSourceCommitment(
            normalizedLocator,
            contentSha256,
            originalUtf8ByteCount,
            includedUtf8ByteCount,
            isTruncated,
            hasUtf8Bom,
            includedHasUtf8Bom);
    }

    public static DocumentationScribeInstructionContextFact CreateInstructionFact(
        DocumentationScribeContextRole role,
        int depth,
        DocumentationScribeContextSourceCommitment commitment,
        string content)
    {
        ArgumentNullException.ThrowIfNull(commitment);
        ArgumentNullException.ThrowIfNull(content);
        if (role is not DocumentationScribeContextRole.AgentEntrypoint
            and not DocumentationScribeContextRole.ScopedInstruction
            || !Enum.IsDefined(role)
            || depth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        var repositoryPath = RequireRepositoryCommitment(commitment);
        ValidateContent(commitment, content);
        var instructionId = Identity(
            "contract-scribe.documentation-scribe-context.instruction",
            DocumentationScribeContextVocabulary.GetId(role),
            depth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            repositoryPath,
            commitment.ContentSha256,
            commitment.OriginalUtf8ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            commitment.IncludedUtf8ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            commitment.IsTruncated ? "1" : "0",
            commitment.HasUtf8Bom ? "1" : "0",
            commitment.IncludedHasUtf8Bom ? "1" : "0");
        return new DocumentationScribeInstructionContextFact(
            "ctxinst-" + instructionId,
            role,
            depth,
            commitment,
            content);
    }

    public static DocumentationScribeProjectContextFact CreateProjectFact(
        string projectIdentity,
        string targetFramework,
        string compilationContextRef,
        DocumentationScribeContextProjectRole role,
        IEnumerable<string>? projectReferences = null)
    {
        var normalizedProject = NormalizeRepositoryPath(projectIdentity);
        ValidateClosedId(targetFramework, nameof(targetFramework));
        if (!IsCompilationContextRef(compilationContextRef)
            || !Enum.IsDefined(role))
        {
            throw new ArgumentException("A valid compilation context and project role are required.");
        }
        var references = (projectReferences ?? [])
            .Select(NormalizeRepositoryPath)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var fields = new List<string>
        {
            normalizedProject,
            targetFramework,
            compilationContextRef,
            DocumentationScribeContextVocabulary.GetId(role),
        };
        fields.AddRange(references);
        return new DocumentationScribeProjectContextFact(
            normalizedProject,
            targetFramework,
            compilationContextRef,
            role,
            references,
            "ctxproject-" + Identity(
                "contract-scribe.documentation-scribe-context.project",
                fields));
    }

    public static DocumentationScribeEvidenceContextFact CreateEvidenceFact(
        DocumentationScribeContextAuthority authority,
        DocumentationScribeContextRole role,
        string subjectId,
        string kindId,
        DocumentationScribeContextSourceCommitment commitment,
        string content,
        int? rangeStart = null,
        int? rangeEnd = null,
        int? includedRangeStart = null,
        int? includedRangeEnd = null)
    {
        ArgumentNullException.ThrowIfNull(commitment);
        ArgumentNullException.ThrowIfNull(content);
        if (!Enum.IsDefined(authority)
            || !Enum.IsDefined(role)
            || authority == DocumentationScribeContextAuthority.ProviderObservation
            || role == DocumentationScribeContextRole.ProviderTelemetry
            || !RoleMatchesAuthority(authority, role))
        {
            throw new ArgumentException(
                "Provider telemetry is not evidence authority and roles cannot be promoted.",
                nameof(authority));
        }

        ValidateClosedId(subjectId, nameof(subjectId));
        ValidateClosedId(kindId, nameof(kindId));
        ValidateContent(commitment, content);
        Utf16Span? range = null;
        if (rangeStart.HasValue != rangeEnd.HasValue
            || rangeStart is < 0
            || rangeEnd <= rangeStart)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeStart));
        }

        if (rangeStart is { } start && rangeEnd is { } end)
        {
            range = new Utf16Span(start, end);
        }

        Utf16Span? includedRange = null;
        if (includedRangeStart.HasValue != includedRangeEnd.HasValue
            || includedRangeStart is < 0
            || includedRangeEnd <= includedRangeStart)
        {
            throw new ArgumentOutOfRangeException(nameof(includedRangeStart));
        }

        if (includedRangeStart is { } includedStart
            && includedRangeEnd is { } includedEnd)
        {
            if (includedEnd - includedStart != content.Length
                || range is { } targetRange
                    && (targetRange.Start < includedStart || targetRange.End > includedEnd))
            {
                throw new ArgumentException("Included source range does not contain the target range.");
            }

            includedRange = new Utf16Span(includedStart, includedEnd);
        }
        var evidenceFields = new List<string>
        {
            "contract-scribe.documentation-scribe-context.evidence",
            DocumentationScribeContextVocabulary.GetId(authority),
            subjectId,
            kindId,
        };
        evidenceFields.AddRange(LocatorIdentityFields(commitment.Locator));
        evidenceFields.AddRange([
            commitment.ContentSha256,
            commitment.OriginalUtf8ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            commitment.IncludedUtf8ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            commitment.IsTruncated ? "1" : "0",
            commitment.HasUtf8Bom ? "1" : "0",
            commitment.IncludedHasUtf8Bom ? "1" : "0",
            rangeStart?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            rangeEnd?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            includedRangeStart?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            includedRangeEnd?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            DocumentationScribeContextVocabulary.GetId(role),
        ]);
        var evidenceId = "ctxevidence-" + Identity(evidenceFields[0], evidenceFields.Skip(1));
        return new DocumentationScribeEvidenceContextFact(
            evidenceId,
            authority,
            role,
            subjectId,
            kindId,
            range,
            includedRange,
            commitment,
            content);
    }

    public static DocumentationScribeInstructionRouteFact CreateInstructionRoute(
        string originInstructionId,
        string destinationPath,
        DocumentationScribeContextRole role,
        DocumentationScribeContextRouteSelection selection,
        int depth,
        DocumentationScribeContextSourceCommitment sourceCommitment)
    {
        ValidateClosedId(originInstructionId, nameof(originInstructionId));
        ArgumentNullException.ThrowIfNull(sourceCommitment);
        var normalizedDestination = NormalizeRepositoryPath(destinationPath);
        var sourcePath = RequireRepositoryCommitment(sourceCommitment);
        var deterministicInstruction =
            selection == DocumentationScribeContextRouteSelection.DeterministicBootstrap
            && role == DocumentationScribeContextRole.ScopedInstruction;
        var scribeEvidence = selection == DocumentationScribeContextRouteSelection.ScribeSelected
            && role is DocumentationScribeContextRole.MaintainedDocumentation
                or DocumentationScribeContextRole.SourceDeclaration
                or DocumentationScribeContextRole.TestEvidence
                or DocumentationScribeContextRole.UsageEvidence
                or DocumentationScribeContextRole.GeneratedEvidence;
        if (!Enum.IsDefined(role)
            || !Enum.IsDefined(selection)
            || !deterministicInstruction && !scribeEvidence
            || depth <= 0
            || !string.Equals(
                normalizedDestination,
                sourcePath,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Instruction route provenance is invalid.");
        }

        var routeId = "ctxroute-" + Identity(
            "contract-scribe.documentation-scribe-context.route",
            originInstructionId,
            normalizedDestination,
            DocumentationScribeContextVocabulary.GetId(role),
            DocumentationScribeContextVocabulary.GetId(selection),
            depth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sourceCommitment.ContentSha256,
            sourceCommitment.OriginalUtf8ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sourceCommitment.IncludedUtf8ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sourceCommitment.IsTruncated ? "1" : "0",
            sourceCommitment.HasUtf8Bom ? "1" : "0",
            sourceCommitment.IncludedHasUtf8Bom ? "1" : "0");
        return new DocumentationScribeInstructionRouteFact(
            routeId,
            originInstructionId,
            normalizedDestination,
            role,
            selection,
            depth,
            sourceCommitment);
    }

    public static DocumentationScribeContextOmissionFact CreateOmission(
        DocumentationScribeContextRole role,
        string? path,
        DocumentationScribeContextOmissionReason reason)
    {
        if (!Enum.IsDefined(role)
            || !Enum.IsDefined(reason)
            || role == DocumentationScribeContextRole.ProviderTelemetry)
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        return new(role, path is null ? null : NormalizeRepositoryPath(path), reason);
    }

    public static DocumentationScribeContextDiagnostic CreateDiagnostic(
        string stage,
        string code,
        DocumentationScribeContextDiagnosticSeverity severity)
    {
        if (!DiagnosticStages.Contains(stage)
            || !DiagnosticCodes.Contains(code)
            || !Enum.IsDefined(severity))
        {
            throw new ArgumentException("Documentation Scribe diagnostics use a closed vocabulary.");
        }

        return new DocumentationScribeContextDiagnostic(stage, code, severity);
    }

    public static DocumentationScribeContextFailure CreateFailure(
        DocumentationScribeContextFailureCategory category,
        string code)
    {
        if (!Enum.IsDefined(category)
            || !FailureVocabulary.TryGetValue(code, out var expectedCategory)
            || expectedCategory != category)
        {
            throw new ArgumentException("Documentation Scribe failures use a closed category/code vocabulary.");
        }

        return new DocumentationScribeContextFailure(category, code);
    }

    public static DocumentationScribeContextFacts CreateFacts(
        DocumentationScribeContextBootstrapSelection selection,
        IEnumerable<DocumentationScribeInstructionContextFact>? instructions,
        IEnumerable<DocumentationScribeProjectContextFact>? projects,
        IEnumerable<DocumentationScribeEvidenceContextFact>? evidence,
        IEnumerable<DocumentationScribeInstructionRouteFact>? routes,
        IEnumerable<DocumentationScribeContextOmissionFact>? omissions = null,
        IEnumerable<DocumentationScribeContextDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var normalizedInstructions = NormalizeByIdentity(
            instructions ?? [],
            item => item.InstructionId,
            InstructionEquivalent,
            "context.identity-collision")
            .OrderBy(item => item.Depth)
            .ThenBy(item => RequireRepositoryCommitment(item.Commitment), StringComparer.Ordinal)
            .ThenBy(item => item.InstructionId, StringComparer.Ordinal)
            .ToImmutableArray();
        var normalizedProjects = NormalizeByIdentity(
            projects ?? [],
            item => item.ProjectFactId,
            (left, right) => left == right,
            "context.identity-collision")
            .OrderBy(item => item.CompilationContextRef, StringComparer.Ordinal)
            .ThenBy(item => item.ProjectIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
        var normalizedEvidence = NormalizeByIdentity(
            evidence ?? [],
            item => item.EvidenceId,
            EvidenceEquivalent,
            "context.identity-collision")
            .OrderBy(item => DocumentationScribeContextVocabulary.GetId(item.Authority), StringComparer.Ordinal)
            .ThenBy(item => item.SubjectId, StringComparer.Ordinal)
            .ThenBy(item => item.KindId, StringComparer.Ordinal)
            .ThenBy(item => LocatorSortKey(item.Commitment.Locator), StringComparer.Ordinal)
            .ThenBy(item => item.EvidenceId, StringComparer.Ordinal)
            .ToImmutableArray();
        var normalizedRoutes = NormalizeByIdentity(
            routes ?? [],
            item => item.RouteId,
            (left, right) => left == right,
            "context.identity-collision")
            .OrderBy(item => item.Depth)
            .ThenBy(item => item.DestinationPath, StringComparer.Ordinal)
            .ThenBy(item => item.OriginInstructionId, StringComparer.Ordinal)
            .ToImmutableArray();
        var normalizedOmissions = (omissions ?? [])
            .Distinct()
            .OrderBy(item => DocumentationScribeContextVocabulary.GetId(item.Reason), StringComparer.Ordinal)
            .ThenBy(item => item.Path ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => DocumentationScribeContextVocabulary.GetId(item.Role), StringComparer.Ordinal)
            .ToImmutableArray();
        var normalizedDiagnostics = (diagnostics ?? [])
            .Distinct()
            .OrderBy(item => item.Stage, StringComparer.Ordinal)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Severity)
            .ToImmutableArray();

        ValidateRoutes(normalizedInstructions, normalizedRoutes);
        var identityFields = new List<string>
        {
            selection.InputIdentity,
            ClassificationVocabulary.GetId(selection.TargetProfile),
            selection.SymbolRef.CompilationContextRef,
            selection.SymbolRef.DocumentationCommentId,
        };
        identityFields.AddRange(normalizedInstructions.Select(item => item.InstructionId));
        identityFields.AddRange(normalizedProjects.Select(item => item.ProjectFactId));
        identityFields.AddRange(normalizedEvidence.Select(item => item.EvidenceId));
        identityFields.AddRange(normalizedRoutes.Select(item => item.RouteId));
        foreach (var omission in normalizedOmissions)
        {
            identityFields.Add("omission");
            identityFields.Add(DocumentationScribeContextVocabulary.GetId(omission.Reason));
            identityFields.Add(omission.Path ?? string.Empty);
            identityFields.Add(DocumentationScribeContextVocabulary.GetId(omission.Role));
        }

        foreach (var diagnostic in normalizedDiagnostics)
        {
            identityFields.Add("diagnostic");
            identityFields.Add(diagnostic.Stage);
            identityFields.Add(diagnostic.Code);
            identityFields.Add(((int)diagnostic.Severity).ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }
        var contentIdentity = "ctxcontent-" + Identity(
            "contract-scribe.documentation-scribe-context.content",
            identityFields);
        return new DocumentationScribeContextFacts(
            selection.RepositoryContextRef,
            selection.InputIdentity,
            selection.TargetProfile,
            selection.SymbolRef,
            contentIdentity,
            normalizedInstructions,
            normalizedProjects,
            normalizedEvidence,
            normalizedRoutes,
            normalizedOmissions,
            normalizedDiagnostics);
    }

    public static DocumentationScribeContextCursorScope CreateCursorScope(
        string toolKindId,
        string normalizedRequestSha256,
        RepositoryContextRef repositoryContextRef,
        SymbolRef symbolRef,
        string orderingId,
        int pageSize,
        string sourceCommitmentsSha256)
    {
        ValidateClosedId(toolKindId, nameof(toolKindId));
        ValidateSha256(normalizedRequestSha256, nameof(normalizedRequestSha256));
        if (repositoryContextRef == default)
        {
            throw new ArgumentException("A repository context reference is required.", nameof(repositoryContextRef));
        }

        ValidateSymbolRef(symbolRef);
        ValidateClosedId(orderingId, nameof(orderingId));
        ValidateSha256(sourceCommitmentsSha256, nameof(sourceCommitmentsSha256));
        if (pageSize <= 0 || pageSize > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        return new DocumentationScribeContextCursorScope(
            toolKindId,
            normalizedRequestSha256,
            repositoryContextRef,
            symbolRef,
            orderingId,
            pageSize,
            sourceCommitmentsSha256);
    }

    public static string ComputeCommitmentsSha256(
        IEnumerable<DocumentationScribeContextSourceCommitment> commitments)
    {
        ArgumentNullException.ThrowIfNull(commitments);
        var fields = commitments
            .OrderBy(item => LocatorSortKey(item.Locator), StringComparer.Ordinal)
            .ThenBy(item => item.ContentSha256, StringComparer.Ordinal)
            .SelectMany(item => new[]
            {
                LocatorSortKey(item.Locator),
                item.ContentSha256,
                item.OriginalUtf8ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.IncludedUtf8ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.IsTruncated ? "1" : "0",
                item.HasUtf8Bom ? "1" : "0",
                item.IncludedHasUtf8Bom ? "1" : "0",
            });
        return Identity(
            "contract-scribe.documentation-scribe-context.commitments",
            fields);
    }

    public static string ComputeSymbolRefSha256(SymbolRef symbolRef)
    {
        ValidateSymbolRef(symbolRef);
        return Identity(
            "contract-scribe.documentation-scribe-context.symbol-ref",
            symbolRef.CompilationContextRef,
            symbolRef.DocumentationCommentId);
    }

    public static string NormalizeRepositoryPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var driveLike = path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
        if (path.Length == 0
            || path[0] == '/'
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains('\0', StringComparison.Ordinal)
            || driveLike
            || !TryCountScalars(path, out var scalarCount)
            || scalarCount > MaximumPathScalars)
        {
            throw new ArgumentException("A normalized repository-relative path is required.", nameof(path));
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0
            || segment is "." or ".."))
        {
            throw new ArgumentException("A normalized repository-relative path is required.", nameof(path));
        }

        return string.Join('/', segments);
    }

    private static bool RoleMatchesAuthority(
        DocumentationScribeContextAuthority authority,
        DocumentationScribeContextRole role) => (authority, role) switch
        {
            (DocumentationScribeContextAuthority.MaintainedDocumentation,
                DocumentationScribeContextRole.MaintainedDocumentation) => true,
            (DocumentationScribeContextAuthority.Source,
                DocumentationScribeContextRole.SourceDeclaration) => true,
            (DocumentationScribeContextAuthority.Test,
                DocumentationScribeContextRole.TestEvidence) => true,
            (DocumentationScribeContextAuthority.Usage,
                DocumentationScribeContextRole.UsageEvidence) => true,
            (DocumentationScribeContextAuthority.Generated,
                DocumentationScribeContextRole.GeneratedEvidence) => true,
            _ => false,
        };

    private static void ValidateContent(
        DocumentationScribeContextSourceCommitment commitment,
        string content)
    {
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(content)
                + (commitment.IncludedHasUtf8Bom ? 3 : 0);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Content must be valid UTF-8 text.", nameof(content), exception);
        }

        if (byteCount != commitment.IncludedUtf8ByteCount)
        {
            throw new ArgumentException("Included content does not match its byte commitment.", nameof(content));
        }

        if (!commitment.IsTruncated)
        {
            var contentBytes = StrictUtf8.GetBytes(content);
            var committedBytes = commitment.HasUtf8Bom
                ? new byte[] { 0xef, 0xbb, 0xbf }.Concat(contentBytes).ToArray()
                : contentBytes;
            var actual = Convert.ToHexString(SHA256.HashData(committedBytes)).ToLowerInvariant();
            if (!string.Equals(actual, commitment.ContentSha256, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Included content does not match its source commitment.",
                    nameof(content));
            }
        }
    }

    private static void ValidateRoutes(
        ImmutableArray<DocumentationScribeInstructionContextFact> instructions,
        ImmutableArray<DocumentationScribeInstructionRouteFact> routes)
    {
        var byId = instructions.ToDictionary(item => item.InstructionId, StringComparer.Ordinal);
        var byPath = instructions.ToDictionary(
            item => RequireRepositoryCommitment(item.Commitment),
            StringComparer.Ordinal);
        foreach (var route in routes)
        {
            if (!byId.TryGetValue(route.OriginInstructionId, out var origin)
                || route.Depth != origin.Depth + 1)
            {
                throw new ArgumentException("An instruction route must originate from an accepted instruction.");
            }

            if (route.Selection == DocumentationScribeContextRouteSelection.DeterministicBootstrap)
            {
                if (!byPath.TryGetValue(route.DestinationPath, out var destination)
                    || route.Role != DocumentationScribeContextRole.ScopedInstruction
                    || destination.Role != DocumentationScribeContextRole.ScopedInstruction
                    || destination.Depth != route.Depth
                    || destination.Commitment != route.SourceCommitment)
                {
                    throw new ArgumentException(
                        "A deterministic instruction route must name the exact accepted scoped instruction.");
                }
            }
        }

        var edges = routes
            .Where(route => route.Selection
                    == DocumentationScribeContextRouteSelection.DeterministicBootstrap
                && byPath.ContainsKey(route.DestinationPath))
            .GroupBy(route => route.OriginInstructionId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(route => byPath[route.DestinationPath].InstructionId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instruction in instructions)
        {
            if (HasCycle(instruction.InstructionId, edges, visiting, visited))
            {
                throw new ArgumentException("Instruction routes contain a cycle.");
            }
        }
    }

    private static bool HasCycle(
        string current,
        IReadOnlyDictionary<string, string[]> edges,
        HashSet<string> visiting,
        HashSet<string> visited)
    {
        if (visited.Contains(current))
        {
            return false;
        }

        if (!visiting.Add(current))
        {
            return true;
        }

        if (edges.TryGetValue(current, out var targets)
            && targets.Any(target => HasCycle(target, edges, visiting, visited)))
        {
            return true;
        }

        visiting.Remove(current);
        visited.Add(current);
        return false;
    }

    private static IEnumerable<T> NormalizeByIdentity<T>(
        IEnumerable<T> values,
        Func<T, string> identity,
        Func<T, T, bool> equivalent,
        string collisionCode)
        where T : class
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            var id = identity(value);
            ValidateClosedId(id, nameof(values));
            if (result.TryGetValue(id, out var existing))
            {
                if (!equivalent(existing, value))
                {
                    throw new InvalidOperationException(collisionCode);
                }

                continue;
            }

            result.Add(id, value);
        }

        return result.Values;
    }

    private static bool InstructionEquivalent(
        DocumentationScribeInstructionContextFact left,
        DocumentationScribeInstructionContextFact right) =>
        left.Role == right.Role
        && left.Depth == right.Depth
        && left.Commitment == right.Commitment
        && string.Equals(left.Content, right.Content, StringComparison.Ordinal);

    private static bool EvidenceEquivalent(
        DocumentationScribeEvidenceContextFact left,
        DocumentationScribeEvidenceContextFact right) =>
        left.Authority == right.Authority
        && left.Role == right.Role
        && string.Equals(left.SubjectId, right.SubjectId, StringComparison.Ordinal)
        && string.Equals(left.KindId, right.KindId, StringComparison.Ordinal)
        && left.Range == right.Range
        && left.IncludedRange == right.IncludedRange
        && left.Commitment == right.Commitment
        && string.Equals(left.Content, right.Content, StringComparison.Ordinal);

    private static void ValidateSymbolRef(SymbolRef symbolRef)
    {
        if (!IsCompilationContextRef(symbolRef.CompilationContextRef)
            || !IsDocumentationCommentId(symbolRef.DocumentationCommentId))
        {
            throw new ArgumentException("A valid SymbolRef is required.", nameof(symbolRef));
        }
    }

    private static EvidenceLocator ValidateEvidenceLocator(EvidenceLocator locator) => locator switch
    {
        RepositoryEvidenceLocator repository when repository.Span is null
            || repository.Span.Value.Start >= 0
                && repository.Span.Value.End > repository.Span.Value.Start =>
            new RepositoryEvidenceLocator(NormalizeRepositoryPath(repository.Path), repository.Span),
        MetadataEvidenceLocator metadata
            when IsCompilationContextRef(metadata.AssemblyIdentity)
                && IsDocumentationCommentId(metadata.DocumentationCommentId) =>
            new MetadataEvidenceLocator(metadata.AssemblyIdentity, metadata.DocumentationCommentId),
        GeneratedOutputEvidenceLocator generated
            when Enum.IsDefined(generated.ProducerKind)
                && IsPrefixedSha256(
                    generated.ProducerId,
                    generated.ProducerKind == GeneratedOutputKind.SourceGenerator ? "sgp." : "tgp.")
                && IsPrefixedSha256(
                    generated.OutputId,
                    generated.ProducerKind == GeneratedOutputKind.SourceGenerator ? "sgo." : "tgo.")
                && IsSha256(generated.SourceSha256)
                && (generated.Span is null
                    || generated.Span.Value.Start >= 0
                        && generated.Span.Value.End > generated.Span.Value.Start) =>
            new GeneratedOutputEvidenceLocator(
                generated.ProducerKind,
                generated.ProducerId,
                generated.OutputId,
                generated.SourceSha256,
                generated.Span),
        SyntheticEvidenceLocator synthetic when IsCompilationContextRef(synthetic.FixtureId) =>
            new SyntheticEvidenceLocator(synthetic.FixtureId),
        _ => throw new ArgumentException("A valid evidence locator is required.", nameof(locator)),
    };

    private static string RequireRepositoryCommitment(
        DocumentationScribeContextSourceCommitment commitment)
    {
        if (commitment.Locator is not RepositoryEvidenceLocator
            {
                Span: null,
            } repository)
        {
            throw new ArgumentException(
                "Instruction and route commitments require a repository path without a span.",
                nameof(commitment));
        }

        return repository.Path;
    }

    private static string LocatorSortKey(EvidenceLocator locator) =>
        Identity(
            "contract-scribe.documentation-scribe-context.locator",
            LocatorIdentityFields(locator));

    private static IEnumerable<string> LocatorIdentityFields(EvidenceLocator locator) => locator switch
    {
        RepositoryEvidenceLocator repository =>
        [
            "repository",
            repository.Path,
            repository.Span?.Start.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty,
            repository.Span?.End.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty,
        ],
        MetadataEvidenceLocator metadata =>
        [
            "metadata",
            metadata.AssemblyIdentity,
            metadata.DocumentationCommentId,
        ],
        GeneratedOutputEvidenceLocator generated =>
        [
            generated.ProducerKind == GeneratedOutputKind.SourceGenerator
                ? "generated.source-generator"
                : "generated.tool-generated",
            generated.ProducerId,
            generated.OutputId,
            generated.SourceSha256,
            generated.Span?.Start.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty,
            generated.Span?.End.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty,
        ],
        SyntheticEvidenceLocator synthetic =>
        [
            "synthetic",
            synthetic.FixtureId,
        ],
        _ => throw new ArgumentException("Unknown evidence locator.", nameof(locator)),
    };

    private static bool IsCompilationContextRef(string value) =>
        value is { Length: >= 1 and <= 128 }
        && IsLowerAlphaNumeric(value[0])
        && value.All(character =>
            IsLowerAlphaNumeric(character) || character is '.' or '_' or '-');

    private static bool IsDocumentationCommentId(string value) =>
        value.Length >= 3
        && value[0] is 'T' or 'M' or 'P' or 'F' or 'E' or 'N'
        && value[1] == ':'
        && TryCountXmlScalars(value, out var scalarCount)
        && scalarCount <= 1_024
        && !value.EnumerateRunes().Any(Rune.IsControl);

    private static bool IsPrefixedSha256(string value, string prefix) =>
        value.Length == prefix.Length + 64
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool IsSha256(string value) =>
        value is { Length: 64 }
        && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool IsLowerAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool TryCountXmlScalars(string value, out int count)
    {
        if (!TryCountScalars(value, out count))
        {
            return false;
        }

        foreach (var rune in value.EnumerateRunes())
        {
            var scalar = rune.Value;
            if (scalar != 0x09
                && scalar is not (>= 0x20 and <= 0xd7ff)
                && scalar is not (>= 0xe000 and <= 0xfffd)
                && scalar is not (>= 0x10000 and <= 0x10ffff))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCountScalars(string value, out int count)
    {
        count = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index]))
                {
                    return false;
                }
            }
            else if (char.IsLowSurrogate(character))
            {
                return false;
            }

            count++;
        }

        return true;
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (value is not { Length: 64 }
            || value.AsSpan().IndexOfAnyExcept("0123456789abcdef") >= 0)
        {
            throw new ArgumentException("A lowercase SHA-256 value is required.", parameterName);
        }
    }

    private static void ValidateClosedId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumIdLength
            || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("A bounded closed identifier is required.", parameterName);
        }
    }

    private static string Identity(string domain, params string[] fields) =>
        Identity(domain, (IEnumerable<string>)fields);

    private static string Identity(string domain, IEnumerable<string> fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, domain);
        foreach (var field in fields)
        {
            Append(hash, field);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string field)
    {
        var bytes = StrictUtf8.GetBytes(field);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
