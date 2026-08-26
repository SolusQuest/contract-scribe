using System.Collections.Immutable;

namespace ContractScribe.Core;

public enum DocumentationScribeContentUnitKind
{
    Summary,
    TypeParameter,
    Parameter,
    Return,
    Value,
    Exception,
    Remarks,
    InheritDoc,
}

public enum DocumentationScribePolicyDisposition
{
    Required,
    Optional,
    Forbidden,
}

public enum DocumentationScribeInheritDocDisposition
{
    Allowed,
    Required,
    Forbidden,
}

public enum DocumentationScribeEvidenceAuthority
{
    SourceImplementation,
    SourceDeclaration,
    ExistingDocumentation,
    Test,
    RepositoryDocumentation,
    PublicContract,
}

public enum DocumentationScribeContextReferenceKind
{
    ProjectInstruction,
    RepositoryDocumentation,
    StyleExample,
}

public enum DocumentationScribeTerminalKind
{
    Proposal,
    Skip,
    Failure,
    Cancelled,
}

public enum DocumentationScribeSkipReason
{
    InsufficientEvidence,
    UnsupportedCurrentM3Domain,
}

public enum DocumentationScribeFailureCode
{
    Provider,
    ToolProtocol,
    Validation,
    Timeout,
    Budget,
    Internal,
}

public enum DocumentationScribeProviderFinalDisposition
{
    Retryable,
    Terminal,
}

public enum DocumentationScribeCancellationCode
{
    Caller,
    Shutdown,
}

public enum DocumentationScribeCacheObservation
{
    Hit,
    Miss,
    Mixed,
    NotReported,
}

public static class DocumentationScribeContract
{
    public const int Version = 1;
    public const int MaximumArtifactUtf8Bytes = 1_048_576;
    public const int MaximumJsonDepth = 64;
    public const int MaximumIdentifierScalars = 128;
    public const int MaximumTextScalars = 16_384;
    public const int MaximumContentUnits = 256;
    public const int MaximumReferences = 512;
    public const int MaximumDiagnostics = 64;
    public const int MaximumAttempts = 1_000_000;
    public const int MaximumObservedInputTokens = 16_777_216;
    public const int MaximumObservedOutputTokens = 1_048_576;
    public const long MaximumObservedCostMicrounits = 1_000_000_000_000;
    public const int MaximumObservedElapsedMilliseconds = 86_400_000;
    public const int MaximumConfiguredInputTokens = MaximumObservedInputTokens - 1;
    public const int MaximumConfiguredOutputTokens = MaximumObservedOutputTokens - 1;
    public const long MaximumConfiguredCostMicrounits = MaximumObservedCostMicrounits - 1;
    public const int MaximumConfiguredElapsedMilliseconds = MaximumObservedElapsedMilliseconds - 1;
}

public static class DocumentationScribeVocabulary
{
    public static string GetId(DocumentationScribeContentUnitKind value) => value switch
    {
        DocumentationScribeContentUnitKind.Summary => "content.summary",
        DocumentationScribeContentUnitKind.TypeParameter => "content.type-parameter",
        DocumentationScribeContentUnitKind.Parameter => "content.parameter",
        DocumentationScribeContentUnitKind.Return => "content.return",
        DocumentationScribeContentUnitKind.Value => "content.value",
        DocumentationScribeContentUnitKind.Exception => "content.exception",
        DocumentationScribeContentUnitKind.Remarks => "content.remarks",
        DocumentationScribeContentUnitKind.InheritDoc => "content.inherit-doc",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string GetId(DocumentationScribePolicyDisposition value) => value switch
    {
        DocumentationScribePolicyDisposition.Required => "required",
        DocumentationScribePolicyDisposition.Optional => "optional",
        DocumentationScribePolicyDisposition.Forbidden => "forbidden",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string GetId(DocumentationScribeInheritDocDisposition value) => value switch
    {
        DocumentationScribeInheritDocDisposition.Allowed => "allowed",
        DocumentationScribeInheritDocDisposition.Required => "required",
        DocumentationScribeInheritDocDisposition.Forbidden => "forbidden",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string GetId(DocumentationScribeEvidenceAuthority value) => value switch
    {
        DocumentationScribeEvidenceAuthority.SourceImplementation => "authority.source-implementation",
        DocumentationScribeEvidenceAuthority.SourceDeclaration => "authority.source-declaration",
        DocumentationScribeEvidenceAuthority.ExistingDocumentation => "authority.existing-documentation",
        DocumentationScribeEvidenceAuthority.Test => "authority.test",
        DocumentationScribeEvidenceAuthority.RepositoryDocumentation => "authority.repository-documentation",
        DocumentationScribeEvidenceAuthority.PublicContract => "authority.public-contract",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string GetId(DocumentationScribeContextReferenceKind value) => value switch
    {
        DocumentationScribeContextReferenceKind.ProjectInstruction => "context.project-instruction",
        DocumentationScribeContextReferenceKind.RepositoryDocumentation => "context.repository-documentation",
        DocumentationScribeContextReferenceKind.StyleExample => "context.style-example",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string GetId(DocumentationScribeTerminalKind value) => value switch
    {
        DocumentationScribeTerminalKind.Proposal => "proposal",
        DocumentationScribeTerminalKind.Skip => "skip",
        DocumentationScribeTerminalKind.Failure => "failure",
        DocumentationScribeTerminalKind.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string GetId(DocumentationScribeSkipReason value) => value switch
    {
        DocumentationScribeSkipReason.InsufficientEvidence => "scribe.skip.insufficient-evidence",
        DocumentationScribeSkipReason.UnsupportedCurrentM3Domain => "scribe.skip.unsupported-current-m3-domain",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string GetId(DocumentationScribeFailureCode value) => value switch
    {
        DocumentationScribeFailureCode.Provider => "scribe.failure.provider",
        DocumentationScribeFailureCode.ToolProtocol => "scribe.failure.tool-protocol",
        DocumentationScribeFailureCode.Validation => "scribe.failure.validation",
        DocumentationScribeFailureCode.Timeout => "scribe.failure.timeout",
        DocumentationScribeFailureCode.Budget => "scribe.failure.budget",
        DocumentationScribeFailureCode.Internal => "scribe.failure.internal",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string GetId(DocumentationScribeProviderFinalDisposition value) => value switch
    {
        DocumentationScribeProviderFinalDisposition.Retryable => "retryable",
        DocumentationScribeProviderFinalDisposition.Terminal => "terminal",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string GetId(DocumentationScribeCancellationCode value) => value switch
    {
        DocumentationScribeCancellationCode.Caller => "scribe.cancelled.caller",
        DocumentationScribeCancellationCode.Shutdown => "scribe.cancelled.shutdown",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string GetId(DocumentationScribeCacheObservation value) => value switch
    {
        DocumentationScribeCacheObservation.Hit => "cache.hit",
        DocumentationScribeCacheObservation.Miss => "cache.miss",
        DocumentationScribeCacheObservation.Mixed => "cache.mixed",
        DocumentationScribeCacheObservation.NotReported => "cache.not-reported",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}

public readonly record struct DocumentationScribeAttemptId
{
    private DocumentationScribeAttemptId(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? value, out DocumentationScribeAttemptId result)
    {
        result = default;
        const string Prefix = "scribe-attempt.";
        if (value is null || value.Length != Prefix.Length + 32 || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = Prefix.Length; index < value.Length; index++)
        {
            var character = value[index];
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
            {
                return false;
            }
        }

        result = new DocumentationScribeAttemptId(value);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;
}

public sealed record DocumentationScribeRequestContext
{
    internal DocumentationScribeRequestContext(
        RepositoryContextRef repositoryContextRef,
        string inputIdentity,
        TargetProfile targetProfile,
        AuditOutcome auditOutcome)
    {
        RepositoryContextRef = repositoryContextRef;
        InputIdentity = inputIdentity;
        TargetProfile = targetProfile;
        AuditOutcome = auditOutcome;
    }

    public RepositoryContextRef RepositoryContextRef { get; }

    public string InputIdentity { get; }

    public TargetProfile TargetProfile { get; }

    public AuditOutcome AuditOutcome { get; }
}

public sealed record DocumentationScribeTarget
{
    internal DocumentationScribeTarget(
        SymbolRef symbolRef,
        EvidenceLocator sourceLocator,
        string sourceSha256,
        ImmutableArray<DocumentationPatchApplicableComponent> applicableComponents)
    {
        SymbolRef = symbolRef;
        SourceLocator = sourceLocator;
        SourceSha256 = sourceSha256;
        ApplicableComponents = applicableComponents;
    }

    public SymbolRef SymbolRef { get; }

    public EvidenceLocator SourceLocator { get; }

    public string SourceSha256 { get; }

    public ImmutableArray<DocumentationPatchApplicableComponent> ApplicableComponents { get; }
}

public sealed record DocumentationScribeTextPolicy
{
    internal DocumentationScribeTextPolicy(
        DocumentationScribePolicyDisposition disposition,
        int maximumScalars)
    {
        Disposition = disposition;
        MaximumScalars = maximumScalars;
    }

    public DocumentationScribePolicyDisposition Disposition { get; }

    public int MaximumScalars { get; }
}

public sealed record DocumentationScribeComponentPolicy
{
    internal DocumentationScribeComponentPolicy(
        string componentIdentity,
        DocumentationScribePolicyDisposition disposition,
        int maximumScalars)
    {
        ComponentIdentity = componentIdentity;
        Disposition = disposition;
        MaximumScalars = maximumScalars;
    }

    public string ComponentIdentity { get; }

    public DocumentationScribePolicyDisposition Disposition { get; }

    public int MaximumScalars { get; }
}

public sealed record DocumentationScribeClaimPolicy
{
    internal DocumentationScribeClaimPolicy(
        string claimCategoryId,
        bool completeEvidenceRequired,
        ImmutableArray<DocumentationScribeEvidenceAuthority> allowedAuthorities)
    {
        ClaimCategoryId = claimCategoryId;
        CompleteEvidenceRequired = completeEvidenceRequired;
        AllowedAuthorities = allowedAuthorities;
    }

    public string ClaimCategoryId { get; }

    public bool CompleteEvidenceRequired { get; }

    public ImmutableArray<DocumentationScribeEvidenceAuthority> AllowedAuthorities { get; }
}

public sealed record DocumentationScribeStyleProfile
{
    internal DocumentationScribeStyleProfile(
        string styleProfileId,
        string outputLanguageId,
        DocumentationScribeTextPolicy summary,
        DocumentationScribeTextPolicy remarks,
        DocumentationScribeTextPolicy exceptions,
        ImmutableArray<DocumentationScribeComponentPolicy> componentPolicies,
        DocumentationScribeInheritDocDisposition inheritDocDisposition,
        ImmutableArray<string> allowedLiterals,
        ImmutableArray<string> forbiddenLiterals,
        ImmutableArray<DocumentationScribeClaimPolicy> claimPolicies,
        int maximumContentUnits,
        int maximumEvidenceRefsPerUnit)
    {
        StyleProfileId = styleProfileId;
        OutputLanguageId = outputLanguageId;
        Summary = summary;
        Remarks = remarks;
        Exceptions = exceptions;
        ComponentPolicies = componentPolicies;
        InheritDocDisposition = inheritDocDisposition;
        AllowedLiterals = allowedLiterals;
        ForbiddenLiterals = forbiddenLiterals;
        ClaimPolicies = claimPolicies;
        MaximumContentUnits = maximumContentUnits;
        MaximumEvidenceRefsPerUnit = maximumEvidenceRefsPerUnit;
    }

    public string StyleProfileId { get; }

    public string OutputLanguageId { get; }

    public DocumentationScribeTextPolicy Summary { get; }

    public DocumentationScribeTextPolicy Remarks { get; }

    public DocumentationScribeTextPolicy Exceptions { get; }

    public ImmutableArray<DocumentationScribeComponentPolicy> ComponentPolicies { get; }

    public DocumentationScribeInheritDocDisposition InheritDocDisposition { get; }

    public ImmutableArray<string> AllowedLiterals { get; }

    public ImmutableArray<string> ForbiddenLiterals { get; }

    public ImmutableArray<DocumentationScribeClaimPolicy> ClaimPolicies { get; }

    public int MaximumContentUnits { get; }

    public int MaximumEvidenceRefsPerUnit { get; }
}

public sealed record DocumentationScribeContextReference
{
    internal DocumentationScribeContextReference(
        string contextReferenceId,
        DocumentationScribeContextReferenceKind kind,
        RepositoryContextRef repositoryContextRef,
        string path,
        string contentSha256,
        int originalUtf8ByteCount,
        int includedUtf8ByteCount,
        bool isTruncated)
    {
        ContextReferenceId = contextReferenceId;
        Kind = kind;
        RepositoryContextRef = repositoryContextRef;
        Path = path;
        ContentSha256 = contentSha256;
        OriginalUtf8ByteCount = originalUtf8ByteCount;
        IncludedUtf8ByteCount = includedUtf8ByteCount;
        IsTruncated = isTruncated;
    }

    public string ContextReferenceId { get; }

    public DocumentationScribeContextReferenceKind Kind { get; }

    public RepositoryContextRef RepositoryContextRef { get; }

    public string Path { get; }

    public string ContentSha256 { get; }

    public int OriginalUtf8ByteCount { get; }

    public int IncludedUtf8ByteCount { get; }

    public bool IsTruncated { get; }
}

public sealed class DocumentationScribeDynamicEvidenceInput
{
    public DocumentationScribeDynamicEvidenceInput(
        EvidenceSubject subject,
        EvidenceKind kind,
        EvidenceRelation relation,
        DocumentationScribeEvidenceAuthority authority,
        EvidenceLocator locator,
        string contentSha256,
        int originalUtf8ByteCount,
        int includedUtf8ByteCount,
        bool isTruncated,
        ImmutableArray<string> claimCategoryIds)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(contentSha256);
        if (claimCategoryIds.IsDefault || claimCategoryIds.Any(value => value is null))
        {
            throw new ArgumentException("Claim categories must be initialized.", nameof(claimCategoryIds));
        }

        Subject = subject;
        Kind = kind;
        Relation = relation;
        Authority = authority;
        Locator = locator;
        ContentSha256 = contentSha256;
        OriginalUtf8ByteCount = originalUtf8ByteCount;
        IncludedUtf8ByteCount = includedUtf8ByteCount;
        IsTruncated = isTruncated;
        ClaimCategoryIds = claimCategoryIds;
    }

    public EvidenceSubject Subject { get; }

    public EvidenceKind Kind { get; }

    public EvidenceRelation Relation { get; }

    public DocumentationScribeEvidenceAuthority Authority { get; }

    public EvidenceLocator Locator { get; }

    public string ContentSha256 { get; }

    public int OriginalUtf8ByteCount { get; }

    public int IncludedUtf8ByteCount { get; }

    public bool IsTruncated { get; }

    public ImmutableArray<string> ClaimCategoryIds { get; }

    public override string ToString() => nameof(DocumentationScribeDynamicEvidenceInput);
}

public sealed record DocumentationScribeEvidenceReference
{
    internal DocumentationScribeEvidenceReference(
        string evidenceReferenceId,
        RepositoryContextRef repositoryContextRef,
        EvidenceSubject subject,
        EvidenceKind kind,
        EvidenceRelation relation,
        DocumentationScribeEvidenceAuthority authority,
        EvidenceLocator locator,
        string contentSha256,
        int originalUtf8ByteCount,
        int includedUtf8ByteCount,
        bool isTruncated,
        ImmutableArray<string> claimCategoryIds)
    {
        EvidenceReferenceId = evidenceReferenceId;
        RepositoryContextRef = repositoryContextRef;
        Subject = subject;
        Kind = kind;
        Relation = relation;
        Authority = authority;
        Locator = locator;
        ContentSha256 = contentSha256;
        OriginalUtf8ByteCount = originalUtf8ByteCount;
        IncludedUtf8ByteCount = includedUtf8ByteCount;
        IsTruncated = isTruncated;
        ClaimCategoryIds = claimCategoryIds;
    }

    public string EvidenceReferenceId { get; }

    public RepositoryContextRef RepositoryContextRef { get; }

    public EvidenceSubject Subject { get; }

    public EvidenceKind Kind { get; }

    public EvidenceRelation Relation { get; }

    public DocumentationScribeEvidenceAuthority Authority { get; }

    public EvidenceLocator Locator { get; }

    public string ContentSha256 { get; }

    public int OriginalUtf8ByteCount { get; }

    public int IncludedUtf8ByteCount { get; }

    public bool IsTruncated { get; }

    public ImmutableArray<string> ClaimCategoryIds { get; }
}

public sealed record DocumentationScribeEvidenceConflict
{
    internal DocumentationScribeEvidenceConflict(
        string higherEvidenceReferenceId,
        string lowerEvidenceReferenceId)
    {
        HigherEvidenceReferenceId = higherEvidenceReferenceId;
        LowerEvidenceReferenceId = lowerEvidenceReferenceId;
    }

    public string HigherEvidenceReferenceId { get; }

    public string LowerEvidenceReferenceId { get; }
}

public sealed record DocumentationScribeRunLimits
{
    internal DocumentationScribeRunLimits(
        int maximumContextReferences,
        int maximumContextUtf8Bytes,
        int maximumEvidenceReferences,
        int maximumEvidenceUtf8Bytes,
        int maximumProviderRequests,
        int maximumToolRounds,
        int maximumToolCalls,
        int maximumAttempts,
        int maximumInputTokens,
        int maximumUncachedInputTokens,
        int maximumOutputTokens,
        long maximumCostMicrounits,
        int maximumElapsedMilliseconds)
    {
        MaximumContextReferences = maximumContextReferences;
        MaximumContextUtf8Bytes = maximumContextUtf8Bytes;
        MaximumEvidenceReferences = maximumEvidenceReferences;
        MaximumEvidenceUtf8Bytes = maximumEvidenceUtf8Bytes;
        MaximumProviderRequests = maximumProviderRequests;
        MaximumToolRounds = maximumToolRounds;
        MaximumToolCalls = maximumToolCalls;
        MaximumAttempts = maximumAttempts;
        MaximumInputTokens = maximumInputTokens;
        MaximumUncachedInputTokens = maximumUncachedInputTokens;
        MaximumOutputTokens = maximumOutputTokens;
        MaximumCostMicrounits = maximumCostMicrounits;
        MaximumElapsedMilliseconds = maximumElapsedMilliseconds;
    }

    public int MaximumContextReferences { get; }

    public int MaximumContextUtf8Bytes { get; }

    public int MaximumEvidenceReferences { get; }

    public int MaximumEvidenceUtf8Bytes { get; }

    public int MaximumProviderRequests { get; }

    public int MaximumToolRounds { get; }

    public int MaximumToolCalls { get; }

    public int MaximumAttempts { get; }

    public int MaximumInputTokens { get; }

    public int MaximumUncachedInputTokens { get; }

    public int MaximumOutputTokens { get; }

    public long MaximumCostMicrounits { get; }

    public int MaximumElapsedMilliseconds { get; }
}

public sealed record DocumentationScribeRequest
{
    internal DocumentationScribeRequest(
        string artifactSha256,
        DocumentationScribeRequestContext context,
        DocumentationScribeTarget target,
        DocumentationScribeStyleProfile styleProfile,
        ImmutableArray<DocumentationScribeContextReference> contextReferences,
        ImmutableArray<DocumentationScribeEvidenceReference> evidenceReferences,
        ImmutableArray<DocumentationScribeEvidenceConflict> evidenceConflicts,
        string toolPolicyId,
        DocumentationScribeRunLimits limits)
    {
        ArtifactSha256 = artifactSha256;
        Context = context;
        Target = target;
        StyleProfile = styleProfile;
        ContextReferences = contextReferences;
        EvidenceReferences = evidenceReferences;
        EvidenceConflicts = evidenceConflicts;
        ToolPolicyId = toolPolicyId;
        Limits = limits;
    }

    public int ScribeRequestVersion => DocumentationScribeContract.Version;

    public string ArtifactSha256 { get; }

    public DocumentationScribeRequestContext Context { get; }

    public DocumentationScribeTarget Target { get; }

    public DocumentationScribeStyleProfile StyleProfile { get; }

    public ImmutableArray<DocumentationScribeContextReference> ContextReferences { get; }

    public ImmutableArray<DocumentationScribeEvidenceReference> EvidenceReferences { get; }

    public ImmutableArray<DocumentationScribeEvidenceConflict> EvidenceConflicts { get; }

    public string ToolPolicyId { get; }

    public DocumentationScribeRunLimits Limits { get; }
}

public sealed record DocumentationScribeResultTarget
{
    internal DocumentationScribeResultTarget(
        RepositoryContextRef repositoryContextRef,
        SymbolRef symbolRef,
        EvidenceLocator sourceLocator,
        string sourceSha256)
    {
        RepositoryContextRef = repositoryContextRef;
        SymbolRef = symbolRef;
        SourceLocator = sourceLocator;
        SourceSha256 = sourceSha256;
    }

    public RepositoryContextRef RepositoryContextRef { get; }

    public SymbolRef SymbolRef { get; }

    public EvidenceLocator SourceLocator { get; }

    public string SourceSha256 { get; }
}

public sealed record DocumentationScribeContentUnit
{
    internal DocumentationScribeContentUnit(
        DocumentationScribeContentUnitKind kind,
        string? componentIdentity,
        string? name,
        string? typeDocumentationId,
        ImmutableArray<string> lines,
        string claimCategoryId,
        ImmutableArray<string> evidenceReferenceIds)
    {
        Kind = kind;
        ComponentIdentity = componentIdentity;
        Name = name;
        TypeDocumentationId = typeDocumentationId;
        Lines = lines;
        ClaimCategoryId = claimCategoryId;
        EvidenceReferenceIds = evidenceReferenceIds;
    }

    public DocumentationScribeContentUnitKind Kind { get; }

    public string? ComponentIdentity { get; }

    public string? Name { get; }

    public string? TypeDocumentationId { get; }

    public ImmutableArray<string> Lines { get; }

    public string ClaimCategoryId { get; }

    public ImmutableArray<string> EvidenceReferenceIds { get; }
}

public abstract record DocumentationScribeTerminal
{
    private protected DocumentationScribeTerminal(DocumentationScribeTerminalKind kind) => Kind = kind;

    public DocumentationScribeTerminalKind Kind { get; }
}

public sealed record DocumentationScribeProposalTerminal : DocumentationScribeTerminal
{
    internal DocumentationScribeProposalTerminal(
        DocumentationScribeResultTarget target,
        ImmutableArray<DocumentationScribeContentUnit> contentUnits,
        DocumentationPatchContent patchContent)
        : base(DocumentationScribeTerminalKind.Proposal)
    {
        Target = target;
        ContentUnits = contentUnits;
        PatchContent = patchContent;
    }

    public DocumentationScribeResultTarget Target { get; }

    public ImmutableArray<DocumentationScribeContentUnit> ContentUnits { get; }

    public DocumentationPatchContent PatchContent { get; }
}

public sealed record DocumentationScribeSkipTerminal : DocumentationScribeTerminal
{
    internal DocumentationScribeSkipTerminal(
        DocumentationScribeSkipReason reason,
        ImmutableArray<string> evidenceReferenceIds)
        : base(DocumentationScribeTerminalKind.Skip)
    {
        Reason = reason;
        EvidenceReferenceIds = evidenceReferenceIds;
    }

    public DocumentationScribeSkipReason Reason { get; }

    public ImmutableArray<string> EvidenceReferenceIds { get; }
}

public sealed record DocumentationScribeFailureTerminal : DocumentationScribeTerminal
{
    internal DocumentationScribeFailureTerminal(
        DocumentationScribeFailureCode code,
        DocumentationScribeProviderFinalDisposition? providerFinalDisposition)
        : base(DocumentationScribeTerminalKind.Failure)
    {
        Code = code;
        ProviderFinalDisposition = providerFinalDisposition;
    }

    public DocumentationScribeFailureCode Code { get; }

    public DocumentationScribeProviderFinalDisposition? ProviderFinalDisposition { get; }
}

public sealed record DocumentationScribeCancelledTerminal : DocumentationScribeTerminal
{
    internal DocumentationScribeCancelledTerminal(DocumentationScribeCancellationCode code)
        : base(DocumentationScribeTerminalKind.Cancelled) => Code = code;

    public DocumentationScribeCancellationCode Code { get; }
}

public sealed record DocumentationScribeUsageObservation
{
    internal DocumentationScribeUsageObservation(
        int? inputTokens,
        int? outputTokens,
        int? cachedInputTokens,
        int? uncachedInputTokens,
        int? reasoningTokens)
    {
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CachedInputTokens = cachedInputTokens;
        UncachedInputTokens = uncachedInputTokens;
        ReasoningTokens = reasoningTokens;
    }

    public int? InputTokens { get; }

    public int? OutputTokens { get; }

    public int? CachedInputTokens { get; }

    public int? UncachedInputTokens { get; }

    public int? ReasoningTokens { get; }
}

public sealed record DocumentationScribeCostObservation
{
    internal DocumentationScribeCostObservation(string currencyId, long amountMicrounits)
    {
        CurrencyId = currencyId;
        AmountMicrounits = amountMicrounits;
    }

    public string CurrencyId { get; }

    public long AmountMicrounits { get; }
}

public sealed record DocumentationScribeDiagnostic
{
    internal DocumentationScribeDiagnostic(
        string code,
        string stage,
        string? referenceId,
        string? validationCode)
    {
        Code = code;
        Stage = stage;
        ReferenceId = referenceId;
        ValidationCode = validationCode;
    }

    public string Code { get; }

    public string Stage { get; }

    public string? ReferenceId { get; }

    public string? ValidationCode { get; }
}

public sealed record DocumentationScribeUsageObservationInput(
    int? InputTokens,
    int? OutputTokens,
    int? CachedInputTokens,
    int? UncachedInputTokens,
    int? ReasoningTokens);

public sealed record DocumentationScribeCostObservationInput(
    string CurrencyId,
    long AmountMicrounits);

public sealed record DocumentationScribeDiagnosticInput(
    string Code,
    string Stage,
    string? ReferenceId = null,
    string? ValidationCode = null);

public sealed record DocumentationScribeRunEnvelopeInput(
    string ProviderConfigurationId,
    string ModelConfigurationId,
    string ScribeProtocolId,
    int AttemptNumber,
    int ProviderRequestCount,
    int ToolRoundCount,
    int ToolCallCount,
    int ElapsedMilliseconds,
    DocumentationScribeUsageObservationInput? Usage,
    DocumentationScribeCacheObservation? Cache,
    DocumentationScribeCostObservationInput? Cost,
    ImmutableArray<DocumentationScribeDiagnosticInput> Diagnostics);

public sealed record DocumentationScribeRunEnvelope
{
    internal DocumentationScribeRunEnvelope(
        string scribeRequestSha256,
        DocumentationScribeAttemptId attemptId,
        string providerConfigurationId,
        string modelConfigurationId,
        string scribeProtocolId,
        string toolPolicyId,
        string styleProfileId,
        int attemptNumber,
        int providerRequestCount,
        int toolRoundCount,
        int toolCallCount,
        int elapsedMilliseconds,
        DocumentationScribeUsageObservation? usage,
        DocumentationScribeCacheObservation? cache,
        DocumentationScribeCostObservation? cost,
        ImmutableArray<DocumentationScribeDiagnostic> diagnostics)
    {
        ScribeRequestSha256 = scribeRequestSha256;
        AttemptId = attemptId;
        ProviderConfigurationId = providerConfigurationId;
        ModelConfigurationId = modelConfigurationId;
        ScribeProtocolId = scribeProtocolId;
        ToolPolicyId = toolPolicyId;
        StyleProfileId = styleProfileId;
        AttemptNumber = attemptNumber;
        ProviderRequestCount = providerRequestCount;
        ToolRoundCount = toolRoundCount;
        ToolCallCount = toolCallCount;
        ElapsedMilliseconds = elapsedMilliseconds;
        Usage = usage;
        Cache = cache;
        Cost = cost;
        Diagnostics = diagnostics;
    }

    public string ScribeRequestSha256 { get; }

    public DocumentationScribeAttemptId AttemptId { get; }

    public string ProviderConfigurationId { get; }

    public string ModelConfigurationId { get; }

    public string ScribeProtocolId { get; }

    public string ToolPolicyId { get; }

    public string StyleProfileId { get; }

    public int AttemptNumber { get; }

    public int ProviderRequestCount { get; }

    public int ToolRoundCount { get; }

    public int ToolCallCount { get; }

    public int ElapsedMilliseconds { get; }

    public DocumentationScribeUsageObservation? Usage { get; }

    public DocumentationScribeCacheObservation? Cache { get; }

    public DocumentationScribeCostObservation? Cost { get; }

    public ImmutableArray<DocumentationScribeDiagnostic> Diagnostics { get; }
}

public sealed record DocumentationScribeRunResult
{
    internal DocumentationScribeRunResult(
        string scribeRequestSha256,
        DocumentationScribeAttemptId attemptId,
        ImmutableArray<DocumentationScribeEvidenceReference> dynamicEvidenceReferences,
        DocumentationScribeTerminal terminal,
        DocumentationScribeRunEnvelope runEnvelope)
    {
        ScribeRequestSha256 = scribeRequestSha256;
        AttemptId = attemptId;
        DynamicEvidenceReferences = dynamicEvidenceReferences;
        Terminal = terminal;
        RunEnvelope = runEnvelope;
    }

    public int ScribeRunResultVersion => DocumentationScribeContract.Version;

    public string ScribeRequestSha256 { get; }

    public DocumentationScribeAttemptId AttemptId { get; }

    public ImmutableArray<DocumentationScribeEvidenceReference> DynamicEvidenceReferences { get; }

    public DocumentationScribeTerminal Terminal { get; }

    public DocumentationScribeRunEnvelope RunEnvelope { get; }
}

public sealed class DocumentationScribeValidatedRunOutcome
{
    internal DocumentationScribeValidatedRunOutcome(
        DocumentationScribeRequest request,
        DocumentationScribeRunResult runResult)
    {
        Request = request;
        RunResult = runResult;
    }

    public DocumentationScribeRequest Request { get; }

    public DocumentationScribeRunResult RunResult { get; }

    public override string ToString() => nameof(DocumentationScribeValidatedRunOutcome);
}

public sealed record DocumentationScribeRequestValidationFailure(string Code, string? Pointer);

public sealed record DocumentationScribeResultValidationFailure(string Code, string? Pointer);

public sealed class DocumentationScribeRequestParseResult
{
    internal DocumentationScribeRequestParseResult(
        DocumentationScribeRequest? request,
        DocumentationScribeRequestValidationFailure? failure)
    {
        Request = request;
        Failure = failure;
    }

    public DocumentationScribeRequest? Request { get; }

    public DocumentationScribeRequestValidationFailure? Failure { get; }

    public bool IsValid => Request is not null;
}

public sealed class DocumentationScribeResultParseResult
{
    internal DocumentationScribeResultParseResult(
        DocumentationScribeRunResult? result,
        DocumentationScribeResultValidationFailure? failure)
    {
        Result = result;
        Failure = failure;
    }

    public DocumentationScribeRunResult? Result { get; }

    public DocumentationScribeResultValidationFailure? Failure { get; }

    public bool IsValid => Result is not null;
}

public sealed record DocumentationScribeToolOutcome
{
    private DocumentationScribeToolOutcome(string id) => Id = id;

    public string Id { get; }

    public static DocumentationScribeToolOutcome Complete { get; } = new("tool.outcome.complete");

    public static DocumentationScribeToolOutcome Incomplete { get; } = new("tool.outcome.incomplete");

    public static DocumentationScribeToolOutcome Unavailable { get; } = new("tool.outcome.unavailable");

    public static DocumentationScribeToolOutcome Failure { get; } = new("tool.outcome.failure");

    public static DocumentationScribeToolOutcome Cancelled { get; } = new("tool.outcome.cancelled");

    public static DocumentationScribeToolOutcome TimedOut { get; } = new("tool.outcome.timed-out");

    public static DocumentationScribeToolOutcome BudgetExhausted { get; } = new("tool.outcome.budget-exhausted");
}

public interface IDocumentationScribeToolResult
{
    DocumentationScribeToolOutcome Outcome { get; }
}

public interface IDocumentationScribeToolRequest<TResult>
    where TResult : IDocumentationScribeToolResult
{
}

public interface IDocumentationScribeToolDescriptor<TRequest, TResult>
    where TRequest : IDocumentationScribeToolRequest<TResult>
    where TResult : IDocumentationScribeToolResult
{
    string OperationId { get; }
}

public interface IDocumentationScribeToolPort<TRequest, TResult>
    where TRequest : IDocumentationScribeToolRequest<TResult>
    where TResult : IDocumentationScribeToolResult
{
    ValueTask<TResult> InvokeAsync(TRequest request, CancellationToken cancellationToken);
}
