using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContractScribe.Core;

namespace ContractScribe.Agent.Runtime;

internal static class DocumentationScribeBoundary
{
    internal const string TerminalOperationId = "scribe.submit-terminal";
    internal const int MaximumPromptBlockUtf8Bytes = 4_194_304;
    internal const int MaximumLogicalRequestUtf8Bytes = 33_554_432;
    internal const int MaximumNormalizedResponseUtf8Bytes = DocumentationScribeContract.MaximumArtifactUtf8Bytes;
    internal const int MaximumToolSchemaUtf8Bytes = 65_536;
    internal const int MaximumToolDescriptionUtf8Bytes = 16_384;
    internal const int MaximumRetryHintMilliseconds = 300_000;
    internal const int MaximumTerminalSubmissions = 8;
    internal const int MaximumToolCallsPerResponse = 1_024;
    internal const int MaximumCorrelationIdUtf8Bytes = 1_024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string NormalizeText(string value, string parameterName, int maximumUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(normalized);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("The normalized text is outside the product boundary.", parameterName);
        }

        if (byteCount > maximumUtf8Bytes)
        {
            throw new ArgumentException("The normalized text is outside the product boundary.", parameterName);
        }

        return normalized;
    }

    internal static string ValidateIdentifier(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length is < 1 or > DocumentationScribeContract.MaximumIdentifierScalars
            || value[0] is not (>= 'a' and <= 'z')
            || value[^1] is '.' or '-'
            || value.Any(character => character is not (>= 'a' and <= 'z')
                && character is not (>= '0' and <= '9')
                && character is not ('.' or '-')))
        {
            throw new ArgumentException("A bounded product identifier is required.", parameterName);
        }

        return value;
    }

    internal static string ValidateCorrelationId(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0
            || value.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException("A bounded opaque correlation identifier is required.", parameterName);
        }

        try
        {
            if (StrictUtf8.GetByteCount(value) > MaximumCorrelationIdUtf8Bytes)
            {
                throw new ArgumentException("A bounded opaque correlation identifier is required.", parameterName);
            }
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("A bounded opaque correlation identifier is required.", parameterName);
        }

        return value;
    }

    internal static string ValidateSha256(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != 64 || value.Any(character => !IsLowerHex(character)))
        {
            throw new ArgumentException("A lowercase SHA-256 commitment is required.", parameterName);
        }

        return value;
    }

    internal static bool MatchesNormalizedContentSha256(string content, string expectedSha256) =>
        string.Equals(
            Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(content))).ToLowerInvariant(),
            expectedSha256,
            StringComparison.Ordinal);

    internal static ImmutableArray<byte> ValidateJson(
        ReadOnlyMemory<byte> utf8Json,
        string parameterName,
        int maximumUtf8Bytes)
    {
        if (utf8Json.Length is < 2 || utf8Json.Length > maximumUtf8Bytes)
        {
            throw new ArgumentException("The JSON payload is outside the product boundary.", parameterName);
        }

        var copy = utf8Json.ToArray().ToImmutableArray();
        try
        {
            using var document = JsonDocument.Parse(
                copy.AsMemory(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = DocumentationScribeContract.MaximumJsonDepth,
                });
            _ = document.RootElement.ValueKind;
        }
        catch (JsonException)
        {
            throw new ArgumentException("A bounded JSON value is required.", parameterName);
        }

        return copy;
    }

    internal static bool IsKnownOutcome(DocumentationScribeToolOutcome outcome) =>
        outcome == DocumentationScribeToolOutcome.Complete
        || outcome == DocumentationScribeToolOutcome.Incomplete
        || outcome == DocumentationScribeToolOutcome.Unavailable
        || outcome == DocumentationScribeToolOutcome.Failure
        || outcome == DocumentationScribeToolOutcome.Cancelled
        || outcome == DocumentationScribeToolOutcome.TimedOut
        || outcome == DocumentationScribeToolOutcome.BudgetExhausted;

    private static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';
}
