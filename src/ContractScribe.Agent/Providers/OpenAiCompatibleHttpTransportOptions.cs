using System.Text;

namespace ContractScribe.Agent.Providers;

public sealed class OpenAiCompatibleHttpTransportOptions
{
    internal const int MaximumEndpointUtf8Bytes = 2_048;
    internal const int MaximumAuthorityUtf8Bytes = 512;
    internal const int MaximumPathUtf8Bytes = 1_024;
    internal const int MaximumModelUtf8Bytes = 256;
    internal const int MaximumCredentialBytes = 8_192;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly OpenAiCompatibleChatCompletionsRequestProfile NonThinkingProfile = new(
        OpenAiCompatibleThinkingMode.Disabled,
        reasoningEffort: null,
        OpenAiCompatibleToolChoice.Required,
        OpenAiCompatibleContinuationPolicy.Optional,
        OpenAiCompatibleOutputTokenField.MaxTokens);

    public OpenAiCompatibleHttpTransportOptions(
        Uri endpoint,
        string model,
        bool networkEnabled,
        string? credential = null)
        : this(endpoint, model, NonThinkingProfile, networkEnabled, credential)
    {
    }

    public OpenAiCompatibleHttpTransportOptions(
        Uri endpoint,
        string model,
        OpenAiCompatibleChatCompletionsRequestProfile requestProfile,
        bool networkEnabled,
        string? credential = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(requestProfile);
        ValidateEndpoint(endpoint, credential);
        ValidateModel(model);
        ValidateCredential(credential);
        Endpoint = endpoint;
        Model = model;
        RequestProfile = requestProfile;
        NetworkEnabled = networkEnabled;
        Credential = credential;
    }

    public Uri Endpoint { get; }

    public string Model { get; }

    public OpenAiCompatibleChatCompletionsRequestProfile RequestProfile { get; }

    public bool NetworkEnabled { get; }

    internal string? Credential { get; }

    public override string ToString() => nameof(OpenAiCompatibleHttpTransportOptions);

    private static void ValidateEndpoint(Uri endpoint, string? credential)
    {
        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The endpoint is outside the selected transport boundary.", nameof(endpoint));
        }

        string original;
        string authority;
        string path;
        try
        {
            original = endpoint.OriginalString;
            authority = endpoint.GetComponents(UriComponents.HostAndPort, UriFormat.UriEscaped);
            path = endpoint.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
            ValidateStrictScalar(original, MaximumEndpointUtf8Bytes, nameof(endpoint));
            ValidateStrictScalar(authority, MaximumAuthorityUtf8Bytes, nameof(endpoint));
            ValidateStrictScalar(path, MaximumPathUtf8Bytes, nameof(endpoint));
        }
        catch (Exception exception) when (exception is UriFormatException or EncoderFallbackException)
        {
            throw new ArgumentException("The endpoint is outside the selected transport boundary.", nameof(endpoint));
        }

        if (string.IsNullOrEmpty(endpoint.Host)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || original.Contains('?')
            || original.Contains('#')
            || AuthorityContains(original, '@'))
        {
            throw new ArgumentException("The endpoint is outside the selected transport boundary.", nameof(endpoint));
        }

        if (string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || endpoint.IsDefaultPort
            || !string.Equals(endpoint.OriginalString, endpoint.AbsoluteUri, StringComparison.Ordinal)
            || endpoint.GetComponents(UriComponents.Host, UriFormat.UriEscaped) is not ("127.0.0.1" or "[::1]")
            || credential is not null)
        {
            throw new ArgumentException("The endpoint is outside the selected transport boundary.", nameof(endpoint));
        }
    }

    private static void ValidateModel(string model)
    {
        if (string.IsNullOrEmpty(model))
        {
            throw new ArgumentException("The model is outside the selected transport boundary.", nameof(model));
        }

        ValidateStrictScalar(model, MaximumModelUtf8Bytes, nameof(model));
    }

    private static void ValidateCredential(string? credential)
    {
        if (credential is null)
        {
            return;
        }

        if (credential.Length == 0 || credential.Length > MaximumCredentialBytes)
        {
            throw new ArgumentException("The credential is outside the selected transport boundary.", nameof(credential));
        }

        var padding = false;
        var hasPayload = false;
        foreach (var value in credential)
        {
            if (value == '=')
            {
                padding = true;
                continue;
            }

            if (padding || !char.IsAsciiLetterOrDigit(value) && value is not ('-' or '.' or '_' or '~' or '+' or '/'))
            {
                throw new ArgumentException("The credential is outside the selected transport boundary.", nameof(credential));
            }

            hasPayload = true;
        }

        if (!hasPayload)
        {
            throw new ArgumentException("The credential is outside the selected transport boundary.", nameof(credential));
        }
    }

    private static bool AuthorityContains(string original, char value)
    {
        var start = original.IndexOf("://", StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += 3;
        var end = original.IndexOf('/', start);
        end = end < 0 ? original.Length : end;
        return original.AsSpan(start, end - start).Contains(value);
    }

    private static void ValidateStrictScalar(string value, int maximumUtf8Bytes, string parameterName)
    {
        int bytes;
        try
        {
            bytes = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("The value is outside the selected transport boundary.", parameterName);
        }

        if (bytes == 0 || bytes > maximumUtf8Bytes || value.Any(char.IsControl))
        {
            throw new ArgumentException("The value is outside the selected transport boundary.", parameterName);
        }
    }
}
