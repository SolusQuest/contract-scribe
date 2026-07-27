using System.Text;
using System.Text.RegularExpressions;

namespace ContractScribe.HostValidation;

public static partial class PublicSafetyScanner
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void EnsureSafeBytes(ReadOnlySpan<byte> bytes)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ProtocolException("HV117_PUBLIC_INVALID_UTF8", exception);
        }

        EnsureSafeText(text);
    }

    public static void EnsureSafeText(string text)
    {
        if (MachinePath().IsMatch(text))
        {
            throw new ProtocolException("HV118_PUBLIC_MACHINE_PATH");
        }

        if (CredentialMarker().IsMatch(text))
        {
            throw new ProtocolException("HV119_PUBLIC_CREDENTIAL_MARKER");
        }

        if (PrivateKeyMarker().IsMatch(text))
        {
            throw new ProtocolException("HV120_PUBLIC_PRIVATE_KEY");
        }
    }

    [GeneratedRegex(@"(?i)([a-z]:\\users\\[^\\\s]+|/users/[^/\s]+|/home/[^/\s]+)")]
    private static partial Regex MachinePath();

    [GeneratedRegex(@"(?i)(password|access[_-]?token|api[_-]?key|client[_-]?secret)\s*[=:]\s*\S+|(?:ghp|github_pat|sk)-[a-z0-9_-]{12,}")]
    private static partial Regex CredentialMarker();

    [GeneratedRegex(@"(?i)-----begin [a-z ]+private key-----")]
    private static partial Regex PrivateKeyMarker();
}
