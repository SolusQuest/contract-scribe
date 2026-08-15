using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace ContractScribe.Core;

public static class DocumentationScribeContextValidation
{
    private const int MaximumPathLength = 1024;
    private const int MaximumIdLength = 256;
    private const int MaximumConfiguredInstructionFiles = 64;
    private const int MaximumConfiguredInstructionDepth = 64;
    private const int MaximumConfiguredFileBytes = 16 * 1024 * 1024;
    private const int MaximumConfiguredTotalBytes = 32 * 1024 * 1024;
    private const int MaximumConfiguredElapsedMilliseconds = 30 * 60 * 1000;
    private const int MaximumPageSize = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static DocumentationScribeContextBootstrapLimits CreateProductionLimits() =>
        new(
            maximumInstructionFiles: 16,
            maximumInstructionDepth: 16,
            maximumInstructionFileUtf8Bytes: 256 * 1024,
            maximumSourceFileUtf8Bytes: 4 * 1024 * 1024,
            maximumIncludedSourceUtf8Bytes: 256 * 1024,
            maximumTotalContextUtf8Bytes: 2 * 1024 * 1024,
            maximumElapsedMilliseconds: 30_000);

    public static DocumentationScribeContextBootstrapLimits CreateLimits(
        int maximumInstructionFiles,
        int maximumInstructionDepth,
        int maximumInstructionFileUtf8Bytes,
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
        if (repositoryContextRef == default)
        {
            throw new ArgumentException("A repository context reference is required.", nameof(repositoryContextRef));
        }

        var normalizedInput = NormalizeRepositoryPath(inputIdentity);
        ValidateSymbolRef(symbolRef);
        var normalizedSource = NormalizeRepositoryPath(sourcePath);
        if (sourceSpanStart < 0 || sourceSpanEnd <= sourceSpanStart)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSpanStart));
        }

        ValidateSha256(sourceSha256, nameof(sourceSha256));
        var entrypoint = configuredAgentEntrypoint is null
            ? null
            : NormalizeRepositoryPath(configuredAgentEntrypoint);
        return new DocumentationScribeContextBootstrapSelection(
            repositoryContextRef,
            normalizedInput,
            targetProfile,
            symbolRef,
            new RepositoryEvidenceLocator(
                normalizedSource,
                new Utf16Span(sourceSpanStart, sourceSpanEnd)),
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
        bool hasUtf8Bom)
    {
        var normalizedPath = NormalizeRepositoryPath(path);
        ValidateSha256(contentSha256, nameof(contentSha256));
        if (originalUtf8ByteCount < 0
            || includedUtf8ByteCount < 0
            || includedUtf8ByteCount > originalUtf8ByteCount
            || isTruncated != (includedUtf8ByteCount < originalUtf8ByteCount)
            || hasUtf8Bom && (originalUtf8ByteCount < 3 || includedUtf8ByteCount < 3))
        {
            throw new ArgumentOutOfRangeException(nameof(originalUtf8ByteCount));
        }

        return new DocumentationScribeContextSourceCommitment(
            normalizedPath,
            contentSha256,
            originalUtf8ByteCount,
            includedUtf8ByteCount,
            isTruncated,
            hasUtf8Bom);
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
            || depth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        ValidateContent(commitment, content);
        var instructionId = Identity(
            "contract-scribe.documentation-scribe-context.instruction",
            DocumentationScribeContextVocabulary.GetId(role),
            depth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            commitment.Path,
            commitment.ContentSha256,
            commitment.OriginalUtf8ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            commitment.IncludedUtf8ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            commitment.IsTruncated ? "1" : "0",
            commitment.HasUtf8Bom ? "1" : "0");
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
        ValidateClosedId(compilationContextRef, nameof(compilationContextRef));
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
        int? rangeEnd = null)
    {
        ArgumentNullException.ThrowIfNull(commitment);
        ArgumentNullException.ThrowIfNull(content);
        if (authority == DocumentationScribeContextAuthority.ProviderObservation
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

        var evidenceId = "ctxevidence-" + Identity(
            "contract-scribe.documentation-scribe-context.evidence",
            DocumentationScribeContextVocabulary.GetId(authority),
            subjectId,
            kindId,
            commitment.Path,
            commitment.ContentSha256,
            commitment.OriginalUtf8ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            commitment.IncludedUtf8ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            commitment.IsTruncated ? "1" : "0",
            commitment.HasUtf8Bom ? "1" : "0",
            rangeStart?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            rangeEnd?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            DocumentationScribeContextVocabulary.GetId(role));
        return new DocumentationScribeEvidenceContextFact(
            evidenceId,
            authority,
            role,
            subjectId,
            kindId,
            range,
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
        if (role is DocumentationScribeContextRole.AgentEntrypoint
            or DocumentationScribeContextRole.ProjectMetadata
            or DocumentationScribeContextRole.ProviderTelemetry
            || depth <= 0
            || !string.Equals(
                normalizedDestination,
                sourceCommitment.Path,
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
            sourceCommitment.HasUtf8Bom ? "1" : "0");
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
        DocumentationScribeContextOmissionReason reason) =>
        new(role, path is null ? null : NormalizeRepositoryPath(path), reason);

    public static DocumentationScribeContextDiagnostic CreateDiagnostic(
        string stage,
        string code,
        DocumentationScribeContextDiagnosticSeverity severity)
    {
        ValidateClosedId(stage, nameof(stage));
        ValidateClosedId(code, nameof(code));
        return new DocumentationScribeContextDiagnostic(stage, code, severity);
    }

    public static DocumentationScribeContextFailure CreateFailure(
        DocumentationScribeContextFailureCategory category,
        string code)
    {
        ValidateClosedId(code, nameof(code));
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
            .ThenBy(item => item.Commitment.Path, StringComparer.Ordinal)
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
            .ThenBy(item => item.Commitment.Path, StringComparer.Ordinal)
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
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.ContentSha256, StringComparer.Ordinal)
            .SelectMany(item => new[]
            {
                item.Path,
                item.ContentSha256,
                item.OriginalUtf8ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.IncludedUtf8ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.IsTruncated ? "1" : "0",
                item.HasUtf8Bom ? "1" : "0",
            });
        return Identity(
            "contract-scribe.documentation-scribe-context.commitments",
            fields);
    }

    public static string NormalizeRepositoryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > MaximumPathLength
            || path[0] == '/'
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains(':', StringComparison.Ordinal)
            || path.Any(char.IsControl))
        {
            throw new ArgumentException("A normalized repository-relative path is required.", nameof(path));
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0
            || segment is "." or ".."
            || segment[^1] is '.' or ' '))
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
            byteCount = StrictUtf8.GetByteCount(content) + (commitment.HasUtf8Bom ? 3 : 0);
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
        var byPath = instructions.ToDictionary(item => item.Commitment.Path, StringComparer.Ordinal);
        foreach (var route in routes)
        {
            if (!byId.ContainsKey(route.OriginInstructionId))
            {
                throw new ArgumentException("An instruction route must originate from an accepted instruction.");
            }
        }

        var edges = routes
            .Where(route => byPath.ContainsKey(route.DestinationPath))
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
        && left.Commitment == right.Commitment
        && string.Equals(left.Content, right.Content, StringComparison.Ordinal);

    private static void ValidateSymbolRef(SymbolRef symbolRef)
    {
        ValidateClosedId(symbolRef.CompilationContextRef, nameof(symbolRef));
        if (string.IsNullOrWhiteSpace(symbolRef.DocumentationCommentId)
            || symbolRef.DocumentationCommentId.Length > MaximumIdLength
            || symbolRef.DocumentationCommentId.Any(char.IsControl))
        {
            throw new ArgumentException("A valid SymbolRef is required.", nameof(symbolRef));
        }
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
