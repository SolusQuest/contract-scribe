using System.Collections.Immutable;
using System.Text.Json;

namespace ContractScribe.Core;

public enum AuditOutcome
{
    Compliant,
    Violation,
    Skipped,
}

public enum AuditPolicyResolution
{
    Single,
    AllDeclarationsAgree,
    Conflict,
    Unavailable,
}

public enum AuditReason
{
    RequiredPresent,
    RequiredAbsent,
    OptionalPresent,
    OptionalAbsent,
    ForbiddenPresent,
    ForbiddenAbsent,
    ClassificationSkipped,
    PolicyConflict,
    PolicyUnavailable,
    DocumentationUnavailable,
    DocumentationUnavailableMalformedXml,
    EvidenceIncomplete,
}

public enum AuditResultValidationCode
{
    InvalidUtf8OrJson,
    DuplicateProperty,
    InvalidShape,
    UnsupportedVersion,
    InvalidVocabulary,
    InvalidClassification,
    InvalidPolicy,
    InvalidEvidence,
    InvalidAuthority,
    InvalidOutcome,
    NonCanonicalBytes,
    MissingOriginalEvidence,
    OriginalEvidenceMismatch,
    TargetProfileMismatch,
}

public sealed class AuditResultValidationException : FormatException
{
    internal AuditResultValidationException(
        AuditResultValidationCode code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public AuditResultValidationCode Code { get; }
}

public readonly record struct AuditEvidenceKey(int ResultIndex, string EvidenceId);

public static class AuditResultVocabulary
{
    public const int AuditResultVersion = 1;
    public const int PolicyConfigurationVersion = 1;
    public const int TaxonomyRegistryVersion = 1;

    public static string GetId(AuditOutcome value) => value switch
    {
        AuditOutcome.Compliant => "audit.outcome.compliant",
        AuditOutcome.Violation => "audit.outcome.violation",
        AuditOutcome.Skipped => "audit.outcome.skipped",
        _ => throw Unknown(value),
    };

    public static string GetId(AuditPolicyResolution value) => value switch
    {
        AuditPolicyResolution.Single => "single",
        AuditPolicyResolution.AllDeclarationsAgree => "all-declarations-agree",
        AuditPolicyResolution.Conflict => "conflict",
        AuditPolicyResolution.Unavailable => "unavailable",
        _ => throw Unknown(value),
    };

    public static string GetId(AuditReason value) => value switch
    {
        AuditReason.RequiredPresent => "audit.reason.required-present",
        AuditReason.RequiredAbsent => "audit.reason.required-absent",
        AuditReason.OptionalPresent => "audit.reason.optional-present",
        AuditReason.OptionalAbsent => "audit.reason.optional-absent",
        AuditReason.ForbiddenPresent => "audit.reason.forbidden-present",
        AuditReason.ForbiddenAbsent => "audit.reason.forbidden-absent",
        AuditReason.ClassificationSkipped => "audit.reason.classification-skipped",
        AuditReason.PolicyConflict => "audit.reason.policy-conflict",
        AuditReason.PolicyUnavailable => "audit.reason.policy-unavailable",
        AuditReason.DocumentationUnavailable => "audit.reason.documentation-unavailable",
        AuditReason.DocumentationUnavailableMalformedXml =>
            "audit.reason.documentation-unavailable.malformed-xml",
        AuditReason.EvidenceIncomplete => "audit.reason.evidence-incomplete",
        _ => throw Unknown(value),
    };

    private static ArgumentOutOfRangeException Unknown<T>(T value)
        where T : struct, Enum =>
        new(nameof(value), value, "The value is outside the closed Audit Result vocabulary.");
}

public sealed class IntrinsicAuditResultDocument
{
    private readonly JsonElement root;

    internal IntrinsicAuditResultDocument(JsonElement root)
    {
        this.root = root;
        TargetProfile = AuditResultJsonModel.ParseTargetProfile(
            root.GetProperty("targetProfile").GetString());
        ResultCount = root.GetProperty("results").GetArrayLength();
    }

    public TargetProfile TargetProfile { get; }

    public int ResultCount { get; }

    internal JsonElement Root => root;
}

public sealed class AuditResultDocument
{
    private readonly JsonElement root;

    internal AuditResultDocument(JsonElement root)
    {
        this.root = root;
        TargetProfile = AuditResultJsonModel.ParseTargetProfile(
            root.GetProperty("targetProfile").GetString());
        ResultCount = root.GetProperty("results").GetArrayLength();
    }

    public TargetProfile TargetProfile { get; }

    public int ResultCount { get; }

    internal JsonElement Root => root;
}

public static class AuditResultParser
{
    public static IntrinsicAuditResultDocument Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 3
            && payload[0] == 0xef
            && payload[1] == 0xbb
            && payload[2] == 0xbf)
        {
            throw AuditResultJsonModel.Failure(
                AuditResultValidationCode.NonCanonicalBytes,
                "A UTF-8 BOM is not canonical.");
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(
                payload.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                });
        }
        catch (JsonException exception)
        {
            throw AuditResultJsonModel.Failure(
                AuditResultValidationCode.InvalidUtf8OrJson,
                "The Audit Result is not valid strict UTF-8 JSON.",
                exception);
        }

        using (parsed)
        {
            var root = parsed.RootElement.Clone();
            AuditResultJsonModel.RejectDuplicateProperties(root);
            AuditResultJsonModel.Validate(root, null, requireOriginalEvidence: false);
            var canonical = AuditResultCanonicalJson.Canonicalize(root);
            if (!payload.SequenceEqual(canonical))
            {
                throw AuditResultJsonModel.Failure(
                    AuditResultValidationCode.NonCanonicalBytes,
                    "The Audit Result byte stream is not canonical.");
            }

            return new IntrinsicAuditResultDocument(root);
        }
    }

    public static AuditResultDocument Promote(
        IntrinsicAuditResultDocument document,
        IReadOnlyDictionary<AuditEvidenceKey, string> originalEvidence)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(originalEvidence);
        AuditResultJsonModel.Validate(
            document.Root,
            originalEvidence,
            requireOriginalEvidence: true);
        return new AuditResultDocument(document.Root.Clone());
    }
}

public static class AuditResultJson
{
    public static byte[] Write(AuditResultDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        AuditResultJsonModel.Validate(
            document.Root,
            ImmutableDictionary<AuditEvidenceKey, string>.Empty,
            requireOriginalEvidence: false,
            trustSourceValidatedTruncation: true);
        return AuditResultCanonicalJson.Canonicalize(document.Root);
    }
}
