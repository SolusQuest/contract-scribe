using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ContractScribe.HostValidation;

public static class CanonicalJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static JsonDocument ReadStrict(string path, int maximumBytes, bool requireCanonical = false)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProtocolException("HV101_ARTIFACT_UNREADABLE", exception);
        }

        if (bytes.Length == 0 || bytes.Length > maximumBytes)
        {
            throw new ProtocolException("HV102_ARTIFACT_SIZE");
        }

        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            throw new ProtocolException("HV103_UTF8_BOM");
        }

        try
        {
            _ = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ProtocolException("HV104_INVALID_UTF8", exception);
        }

        RejectDuplicateProperties(bytes);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        }
        catch (JsonException exception)
        {
            throw new ProtocolException("HV105_INVALID_JSON", exception);
        }

        if (requireCanonical)
        {
            var canonical = SerializeCanonical(document.RootElement);
            if (!bytes.AsSpan().SequenceEqual(canonical))
            {
                document.Dispose();
                throw new ProtocolException("HV106_NONCANONICAL_JSON");
            }
        }

        return document;
    }

    public static T DeserializeStrict<T>(string path, int maximumBytes, bool requireCanonical = false)
    {
        using var document = ReadStrict(path, maximumBytes, requireCanonical);
        try
        {
            return document.RootElement.Deserialize<T>(SerializerOptions)
                ?? throw new ProtocolException("HV107_NULL_DOCUMENT");
        }
        catch (JsonException exception)
        {
            throw new ProtocolException("HV108_MODEL_MISMATCH", exception);
        }
    }

    public static byte[] SerializeCanonical<T>(T value) =>
        SerializeCanonical(JsonSerializer.SerializeToElement(value, SerializerOptions));

    public static byte[] SerializeCanonical(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
            SkipValidation = false
        }))
        {
            WriteSorted(writer, value);
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    public static void WriteCanonical<T>(string path, T value)
    {
        WriteBytesAtomic(path, SerializeCanonical(value));
    }

    public static void WriteBytesAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ProtocolException("HV191_OUTPUT_PATH_INVALID");
        }

        Directory.CreateDirectory(directory);
        var stagingPath = Path.Join(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.staging");
        try
        {
            using (var stream = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(stagingPath, fullPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProtocolException("HV192_ATOMIC_PUBLICATION_FAILED", exception);
        }
        finally
        {
            try
            {
                File.Delete(stagingPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Publication already succeeded or failed; staging cleanup is non-authoritative.
            }
        }
    }

    public static void InvalidateOutput(string path)
    {
        try
        {
            File.Delete(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProtocolException("HV193_STALE_OUTPUT_INVALIDATION_FAILED", exception);
        }
    }

    public static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256File(string path) => Sha256(File.ReadAllBytes(path));

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        var objectProperties = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    objectProperties.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var property = reader.GetString() ?? string.Empty;
                    if (objectProperties.Count == 0 || !objectProperties.Peek().Add(property))
                    {
                        throw new ProtocolException("HV109_DUPLICATE_PROPERTY");
                    }
                    break;
            }
        }
    }

    private static void WriteSorted(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteSorted(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteSorted(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
