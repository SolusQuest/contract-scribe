using System.Collections.Immutable;

namespace ContractScribe.Core;

public enum DocumentationScribeContextAuthority
{
    SystemRun,
    RepositoryInstruction,
    MaintainedDocumentation,
    Source,
    Test,
    Usage,
    Generated,
    ProviderObservation,
}

public enum DocumentationScribeContextRole
{
    AgentEntrypoint,
    ScopedInstruction,
    ProjectMetadata,
    MaintainedDocumentation,
    SourceDeclaration,
    TestEvidence,
    UsageEvidence,
    GeneratedEvidence,
    ProviderTelemetry,
}

public enum DocumentationScribeContextRouteSelection
{
    DeterministicBootstrap,
    ScribeSelected,
}

public enum DocumentationScribeContextProjectRole
{
    AuditRoot,
    DependencyOnly,
}

public enum DocumentationScribeContextBootstrapStatus
{
    Succeeded,
    Incomplete,
    Unavailable,
    Failed,
    Cancelled,
    TimedOut,
    BudgetExhausted,
}

public enum DocumentationScribeContextFailureCategory
{
    Correlation,
    AmbiguousScope,
    UnsafeRepositoryObject,
    Stale,
    InvalidEncoding,
    IdentityCollision,
    InvalidCursor,
    Internal,
}

public enum DocumentationScribeContextOmissionReason
{
    MissingOptional,
    RouteCycle,
    FileLimit,
    ByteLimit,
    DepthLimit,
    UnsupportedEncoding,
}

public enum DocumentationScribeContextDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public static class DocumentationScribeContextVocabulary
{
    public static string GetId(DocumentationScribeContextAuthority value) => value switch
    {
        DocumentationScribeContextAuthority.SystemRun => "authority.system-run",
        DocumentationScribeContextAuthority.RepositoryInstruction => "authority.repository-instruction",
        DocumentationScribeContextAuthority.MaintainedDocumentation => "authority.maintained-documentation",
        DocumentationScribeContextAuthority.Source => "authority.source",
        DocumentationScribeContextAuthority.Test => "authority.test",
        DocumentationScribeContextAuthority.Usage => "authority.usage",
        DocumentationScribeContextAuthority.Generated => "authority.generated",
        DocumentationScribeContextAuthority.ProviderObservation => "telemetry.provider-observation",
        _ => throw Unknown(value),
    };

    public static string GetId(DocumentationScribeContextRole value) => value switch
    {
        DocumentationScribeContextRole.AgentEntrypoint => "role.instruction.entrypoint",
        DocumentationScribeContextRole.ScopedInstruction => "role.instruction.scoped",
        DocumentationScribeContextRole.ProjectMetadata => "role.project-metadata",
        DocumentationScribeContextRole.MaintainedDocumentation => "role.maintained-documentation",
        DocumentationScribeContextRole.SourceDeclaration => "role.source-declaration",
        DocumentationScribeContextRole.TestEvidence => "role.test-evidence",
        DocumentationScribeContextRole.UsageEvidence => "role.usage-evidence",
        DocumentationScribeContextRole.GeneratedEvidence => "role.generated-evidence",
        DocumentationScribeContextRole.ProviderTelemetry => "role.provider-telemetry",
        _ => throw Unknown(value),
    };

    public static string GetId(DocumentationScribeContextRouteSelection value) => value switch
    {
        DocumentationScribeContextRouteSelection.DeterministicBootstrap => "route.deterministic-bootstrap",
        DocumentationScribeContextRouteSelection.ScribeSelected => "route.scribe-selected",
        _ => throw Unknown(value),
    };

    public static string GetId(DocumentationScribeContextProjectRole value) => value switch
    {
        DocumentationScribeContextProjectRole.AuditRoot => "project.audit-root",
        DocumentationScribeContextProjectRole.DependencyOnly => "project.dependency-only",
        _ => throw Unknown(value),
    };

    public static string GetId(DocumentationScribeContextOmissionReason value) => value switch
    {
        DocumentationScribeContextOmissionReason.MissingOptional => "omission.missing-optional",
        DocumentationScribeContextOmissionReason.RouteCycle => "omission.route-cycle",
        DocumentationScribeContextOmissionReason.FileLimit => "omission.file-limit",
        DocumentationScribeContextOmissionReason.ByteLimit => "omission.byte-limit",
        DocumentationScribeContextOmissionReason.DepthLimit => "omission.depth-limit",
        DocumentationScribeContextOmissionReason.UnsupportedEncoding => "omission.unsupported-encoding",
        _ => throw Unknown(value),
    };

    private static ArgumentOutOfRangeException Unknown<T>(T value) where T : struct, Enum =>
        new(nameof(value), value, "Unknown Documentation Scribe context vocabulary value.");
}

public sealed record DocumentationScribeContextBootstrapLimits
{
    internal DocumentationScribeContextBootstrapLimits(
        int maximumInstructionFiles,
        int maximumInstructionDepth,
        int maximumInstructionFileUtf8Bytes,
        int maximumSourceFileUtf8Bytes,
        int maximumIncludedSourceUtf8Bytes,
        int maximumTotalContextUtf8Bytes,
        int maximumElapsedMilliseconds)
    {
        MaximumInstructionFiles = maximumInstructionFiles;
        MaximumInstructionDepth = maximumInstructionDepth;
        MaximumInstructionFileUtf8Bytes = maximumInstructionFileUtf8Bytes;
        MaximumSourceFileUtf8Bytes = maximumSourceFileUtf8Bytes;
        MaximumIncludedSourceUtf8Bytes = maximumIncludedSourceUtf8Bytes;
        MaximumTotalContextUtf8Bytes = maximumTotalContextUtf8Bytes;
        MaximumElapsedMilliseconds = maximumElapsedMilliseconds;
    }

    public int MaximumInstructionFiles { get; }

    public int MaximumInstructionDepth { get; }

    public int MaximumInstructionFileUtf8Bytes { get; }

    public int MaximumSourceFileUtf8Bytes { get; }

    public int MaximumIncludedSourceUtf8Bytes { get; }

    public int MaximumTotalContextUtf8Bytes { get; }

    public int MaximumElapsedMilliseconds { get; }
}

public sealed record DocumentationScribeContextBootstrapSelection
{
    internal DocumentationScribeContextBootstrapSelection(
        RepositoryContextRef repositoryContextRef,
        string inputIdentity,
        TargetProfile targetProfile,
        SymbolRef symbolRef,
        RepositoryEvidenceLocator sourceLocator,
        string sourceSha256,
        string? configuredAgentEntrypoint,
        DocumentationScribeContextBootstrapLimits limits)
    {
        RepositoryContextRef = repositoryContextRef;
        InputIdentity = inputIdentity;
        TargetProfile = targetProfile;
        SymbolRef = symbolRef;
        SourceLocator = sourceLocator;
        SourceSha256 = sourceSha256;
        ConfiguredAgentEntrypoint = configuredAgentEntrypoint;
        Limits = limits;
    }

    public RepositoryContextRef RepositoryContextRef { get; }

    public string InputIdentity { get; }

    public TargetProfile TargetProfile { get; }

    public SymbolRef SymbolRef { get; }

    public string CompilationContextRef => SymbolRef.CompilationContextRef;

    public RepositoryEvidenceLocator SourceLocator { get; }

    public string SourceSha256 { get; }

    public string? ConfiguredAgentEntrypoint { get; }

    public DocumentationScribeContextBootstrapLimits Limits { get; }
}

public sealed record DocumentationScribeContextSourceCommitment
{
    internal DocumentationScribeContextSourceCommitment(
        string path,
        string contentSha256,
        int originalUtf8ByteCount,
        int includedUtf8ByteCount,
        bool isTruncated,
        bool hasUtf8Bom)
    {
        Path = path;
        ContentSha256 = contentSha256;
        OriginalUtf8ByteCount = originalUtf8ByteCount;
        IncludedUtf8ByteCount = includedUtf8ByteCount;
        IsTruncated = isTruncated;
        HasUtf8Bom = hasUtf8Bom;
    }

    public string Path { get; }

    public string ContentSha256 { get; }

    public int OriginalUtf8ByteCount { get; }

    public int IncludedUtf8ByteCount { get; }

    public bool IsTruncated { get; }

    public bool HasUtf8Bom { get; }
}

public sealed class DocumentationScribeInstructionContextFact
{
    internal DocumentationScribeInstructionContextFact(
        string instructionId,
        DocumentationScribeContextRole role,
        int depth,
        DocumentationScribeContextSourceCommitment commitment,
        string content)
    {
        InstructionId = instructionId;
        Role = role;
        Depth = depth;
        Commitment = commitment;
        Content = content;
    }

    public string InstructionId { get; }

    public DocumentationScribeContextAuthority Authority =>
        DocumentationScribeContextAuthority.RepositoryInstruction;

    public DocumentationScribeContextRole Role { get; }

    public int Depth { get; }

    public DocumentationScribeContextSourceCommitment Commitment { get; }

    public string Content { get; }

    public override string ToString() =>
        $"{nameof(DocumentationScribeInstructionContextFact)} {{ InstructionId = {InstructionId}, Role = {Role}, Depth = {Depth}, Path = {Commitment.Path}, Content = <authorized-content> }}";
}

public sealed record DocumentationScribeProjectContextFact
{
    internal DocumentationScribeProjectContextFact(
        string projectIdentity,
        string targetFramework,
        string compilationContextRef,
        DocumentationScribeContextProjectRole role,
        ImmutableArray<string> projectReferences,
        string projectFactId)
    {
        ProjectIdentity = projectIdentity;
        TargetFramework = targetFramework;
        CompilationContextRef = compilationContextRef;
        Role = role;
        ProjectReferences = projectReferences;
        ProjectFactId = projectFactId;
    }

    public string ProjectIdentity { get; }

    public string TargetFramework { get; }

    public string CompilationContextRef { get; }

    public DocumentationScribeContextProjectRole Role { get; }

    public ImmutableArray<string> ProjectReferences { get; }

    public string ProjectFactId { get; }
}

public sealed class DocumentationScribeEvidenceContextFact
{
    internal DocumentationScribeEvidenceContextFact(
        string evidenceId,
        DocumentationScribeContextAuthority authority,
        DocumentationScribeContextRole role,
        string subjectId,
        string kindId,
        Utf16Span? range,
        DocumentationScribeContextSourceCommitment commitment,
        string content)
    {
        EvidenceId = evidenceId;
        Authority = authority;
        Role = role;
        SubjectId = subjectId;
        KindId = kindId;
        Range = range;
        Commitment = commitment;
        Content = content;
    }

    public string EvidenceId { get; }

    public DocumentationScribeContextAuthority Authority { get; }

    public DocumentationScribeContextRole Role { get; }

    public string SubjectId { get; }

    public string KindId { get; }

    public Utf16Span? Range { get; }

    public DocumentationScribeContextSourceCommitment Commitment { get; }

    public string Content { get; }

    public override string ToString() =>
        $"{nameof(DocumentationScribeEvidenceContextFact)} {{ EvidenceId = {EvidenceId}, Authority = {Authority}, Role = {Role}, SubjectId = {SubjectId}, KindId = {KindId}, Path = {Commitment.Path}, Content = <authorized-content> }}";
}

public sealed record DocumentationScribeInstructionRouteFact
{
    internal DocumentationScribeInstructionRouteFact(
        string routeId,
        string originInstructionId,
        string destinationPath,
        DocumentationScribeContextRole role,
        DocumentationScribeContextRouteSelection selection,
        int depth,
        DocumentationScribeContextSourceCommitment sourceCommitment)
    {
        RouteId = routeId;
        OriginInstructionId = originInstructionId;
        DestinationPath = destinationPath;
        Role = role;
        Selection = selection;
        Depth = depth;
        SourceCommitment = sourceCommitment;
    }

    public string RouteId { get; }

    public string OriginInstructionId { get; }

    public string DestinationPath { get; }

    public DocumentationScribeContextRole Role { get; }

    public DocumentationScribeContextRouteSelection Selection { get; }

    public int Depth { get; }

    public DocumentationScribeContextSourceCommitment SourceCommitment { get; }
}

public sealed record DocumentationScribeContextOmissionFact
{
    internal DocumentationScribeContextOmissionFact(
        DocumentationScribeContextRole role,
        string? path,
        DocumentationScribeContextOmissionReason reason)
    {
        Role = role;
        Path = path;
        Reason = reason;
    }

    public DocumentationScribeContextRole Role { get; }

    public string? Path { get; }

    public DocumentationScribeContextOmissionReason Reason { get; }
}

public sealed record DocumentationScribeContextDiagnostic
{
    internal DocumentationScribeContextDiagnostic(
        string stage,
        string code,
        DocumentationScribeContextDiagnosticSeverity severity)
    {
        Stage = stage;
        Code = code;
        Severity = severity;
    }

    public string Stage { get; }

    public string Code { get; }

    public DocumentationScribeContextDiagnosticSeverity Severity { get; }
}

public sealed record DocumentationScribeContextFailure
{
    internal DocumentationScribeContextFailure(
        DocumentationScribeContextFailureCategory category,
        string code)
    {
        Category = category;
        Code = code;
    }

    public DocumentationScribeContextFailureCategory Category { get; }

    public string Code { get; }
}

public sealed class DocumentationScribeContextFacts
{
    internal DocumentationScribeContextFacts(
        RepositoryContextRef repositoryContextRef,
        string inputIdentity,
        TargetProfile targetProfile,
        SymbolRef symbolRef,
        string contentIdentity,
        ImmutableArray<DocumentationScribeInstructionContextFact> instructions,
        ImmutableArray<DocumentationScribeProjectContextFact> projects,
        ImmutableArray<DocumentationScribeEvidenceContextFact> evidence,
        ImmutableArray<DocumentationScribeInstructionRouteFact> routes,
        ImmutableArray<DocumentationScribeContextOmissionFact> omissions,
        ImmutableArray<DocumentationScribeContextDiagnostic> diagnostics)
    {
        RepositoryContextRef = repositoryContextRef;
        InputIdentity = inputIdentity;
        TargetProfile = targetProfile;
        SymbolRef = symbolRef;
        ContentIdentity = contentIdentity;
        Instructions = instructions;
        Projects = projects;
        Evidence = evidence;
        Routes = routes;
        Omissions = omissions;
        Diagnostics = diagnostics;
    }

    public RepositoryContextRef RepositoryContextRef { get; }

    public string InputIdentity { get; }

    public TargetProfile TargetProfile { get; }

    public SymbolRef SymbolRef { get; }

    public string CompilationContextRef => SymbolRef.CompilationContextRef;

    public string ContentIdentity { get; }

    public ImmutableArray<DocumentationScribeInstructionContextFact> Instructions { get; }

    public ImmutableArray<DocumentationScribeProjectContextFact> Projects { get; }

    public ImmutableArray<DocumentationScribeEvidenceContextFact> Evidence { get; }

    public ImmutableArray<DocumentationScribeInstructionRouteFact> Routes { get; }

    public ImmutableArray<DocumentationScribeContextOmissionFact> Omissions { get; }

    public ImmutableArray<DocumentationScribeContextDiagnostic> Diagnostics { get; }

    public override string ToString() =>
        $"{nameof(DocumentationScribeContextFacts)} {{ RepositoryContextRef = {RepositoryContextRef}, InputIdentity = {InputIdentity}, TargetProfile = {TargetProfile}, SymbolRef = {SymbolRef}, ContentIdentity = {ContentIdentity}, Instructions = {Instructions.Length}, Projects = {Projects.Length}, Evidence = {Evidence.Length}, Routes = {Routes.Length}, Omissions = {Omissions.Length}, Diagnostics = {Diagnostics.Length} }}";
}

public readonly record struct DocumentationScribeContextCursor
{
    private DocumentationScribeContextCursor(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static bool TryParse(string? value, out DocumentationScribeContextCursor cursor)
    {
        if (value is { Length: >= 32 and <= 4096 }
            && value.StartsWith("ctxcur.", StringComparison.Ordinal)
            && value.AsSpan().IndexOfAnyExcept(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.") < 0)
        {
            cursor = new DocumentationScribeContextCursor(value);
            return true;
        }

        cursor = default;
        return false;
    }

    public override string ToString() => Value is null ? string.Empty : "ctxcur.<opaque>";
}

public sealed record DocumentationScribeContextCursorScope
{
    internal DocumentationScribeContextCursorScope(
        string toolKindId,
        string normalizedRequestSha256,
        RepositoryContextRef repositoryContextRef,
        SymbolRef symbolRef,
        string orderingId,
        int pageSize,
        string sourceCommitmentsSha256)
    {
        ToolKindId = toolKindId;
        NormalizedRequestSha256 = normalizedRequestSha256;
        RepositoryContextRef = repositoryContextRef;
        SymbolRef = symbolRef;
        OrderingId = orderingId;
        PageSize = pageSize;
        SourceCommitmentsSha256 = sourceCommitmentsSha256;
    }

    public string ToolKindId { get; }

    public string NormalizedRequestSha256 { get; }

    public RepositoryContextRef RepositoryContextRef { get; }

    public SymbolRef SymbolRef { get; }

    public string CompilationContextRef => SymbolRef.CompilationContextRef;

    public string OrderingId { get; }

    public int PageSize { get; }

    public string SourceCommitmentsSha256 { get; }
}
