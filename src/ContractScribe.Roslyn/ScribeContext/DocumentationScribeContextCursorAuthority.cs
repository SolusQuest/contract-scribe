using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ContractScribe.Core;

namespace ContractScribe.Roslyn;

internal sealed class DocumentationScribeContextCursorAuthority
{
    private const int Format = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] key;

    internal DocumentationScribeContextCursorAuthority(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
        {
            throw new ArgumentException("A 256-bit cursor key is required.", nameof(key));
        }

        this.key = key.ToArray();
    }

    internal DocumentationScribeContextCursor Issue(
        DocumentationScribeContextCursorScope scope,
        int nextPosition)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (nextPosition < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nextPosition));
        }

        var payload = Serialize(scope, nextPosition);
        var signature = HMACSHA256.HashData(key, payload);
        var token = "ctxcur."
            + Base64Url(payload)
            + "."
            + Base64Url(signature);
        if (!DocumentationScribeContextCursor.TryParse(token, out var cursor))
        {
            throw new InvalidOperationException("context.cursor.internal");
        }

        return cursor;
    }

    internal bool TryValidate(
        DocumentationScribeContextCursor cursor,
        DocumentationScribeContextCursorScope expectedScope,
        out int nextPosition)
    {
        ArgumentNullException.ThrowIfNull(expectedScope);
        nextPosition = 0;
        try
        {
            var parts = cursor.Value.Split('.');
            if (parts.Length != 3 || !string.Equals(parts[0], "ctxcur", StringComparison.Ordinal))
            {
                return false;
            }

            var payload = FromBase64Url(parts[1]);
            var supplied = FromBase64Url(parts[2]);
            if (supplied.Length != 32)
            {
                return false;
            }

            var expectedSignature = HMACSHA256.HashData(key, payload);
            if (!CryptographicOperations.FixedTimeEquals(expectedSignature, supplied))
            {
                return false;
            }

            if (!TryDeserialize(payload, expectedScope, out nextPosition))
            {
                nextPosition = 0;
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is FormatException
            or DecoderFallbackException
            or OverflowException
            or ArgumentException)
        {
            nextPosition = 0;
            return false;
        }
    }

    private static byte[] Serialize(
        DocumentationScribeContextCursorScope scope,
        int nextPosition)
    {
        using var stream = new MemoryStream();
        WriteInt32(stream, Format);
        WriteString(stream, scope.ToolKindId);
        WriteString(stream, scope.NormalizedRequestSha256);
        WriteString(stream, scope.RepositoryContextRef.ToString());
        WriteString(stream, scope.SymbolRef.CompilationContextRef);
        WriteString(stream, scope.SymbolRef.DocumentationCommentId);
        WriteString(stream, scope.OrderingId);
        WriteInt32(stream, scope.PageSize);
        WriteInt32(stream, nextPosition);
        WriteString(stream, scope.SourceCommitmentsSha256);
        return stream.ToArray();
    }

    private static bool TryDeserialize(
        ReadOnlySpan<byte> payload,
        DocumentationScribeContextCursorScope expectedScope,
        out int nextPosition)
    {
        nextPosition = 0;
        var offset = 0;
        if (!TryReadInt32(payload, ref offset, out var format)
            || format != Format
            || !TryReadString(payload, ref offset, out var toolKind)
            || !TryReadString(payload, ref offset, out var requestSha)
            || !TryReadString(payload, ref offset, out var repositoryContext)
            || !TryReadString(payload, ref offset, out var compilationContext)
            || !TryReadString(payload, ref offset, out var documentationId)
            || !TryReadString(payload, ref offset, out var ordering)
            || !TryReadInt32(payload, ref offset, out var pageSize)
            || !TryReadInt32(payload, ref offset, out nextPosition)
            || nextPosition < 0
            || !TryReadString(payload, ref offset, out var commitmentsSha)
            || offset != payload.Length
            || !RepositoryContextRef.TryParse(repositoryContext, out var contextRef))
        {
            nextPosition = 0;
            return false;
        }

        return string.Equals(toolKind, expectedScope.ToolKindId, StringComparison.Ordinal)
            && string.Equals(
                requestSha,
                expectedScope.NormalizedRequestSha256,
                StringComparison.Ordinal)
            && contextRef == expectedScope.RepositoryContextRef
            && string.Equals(
                compilationContext,
                expectedScope.SymbolRef.CompilationContextRef,
                StringComparison.Ordinal)
            && string.Equals(
                documentationId,
                expectedScope.SymbolRef.DocumentationCommentId,
                StringComparison.Ordinal)
            && string.Equals(ordering, expectedScope.OrderingId, StringComparison.Ordinal)
            && pageSize == expectedScope.PageSize
            && string.Equals(
                commitmentsSha,
                expectedScope.SourceCommitmentsSha256,
                StringComparison.Ordinal);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static bool TryReadString(
        ReadOnlySpan<byte> payload,
        ref int offset,
        out string value)
    {
        value = string.Empty;
        if (!TryReadInt32(payload, ref offset, out var length)
            || length < 0
            || length > 4096
            || offset > payload.Length - length)
        {
            return false;
        }

        value = StrictUtf8.GetString(payload.Slice(offset, length));
        offset += length;
        return true;
    }

    private static bool TryReadInt32(
        ReadOnlySpan<byte> payload,
        ref int offset,
        out int value)
    {
        value = 0;
        if (offset > payload.Length - 4)
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt32BigEndian(payload[offset..]);
        offset += 4;
        return true;
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid cursor encoding."),
        };
        return Convert.FromBase64String(padded);
    }
}
