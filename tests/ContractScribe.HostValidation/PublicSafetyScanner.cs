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

    public static void EnsureNoUnsupportedClaims(string text)
    {
        if (UnsupportedPositiveClaim().IsMatch(text))
        {
            throw new ProtocolException("HV199_PUBLIC_UNSUPPORTED_CLAIM");
        }
    }

    public static void SelfTestMachinePaths()
    {
        var unsafePaths = new[]
        {
            string.Concat("C:", @"\Users\runner\work"),
            string.Concat("D:", @"\a\contract-scribe"),
            string.Concat("C:", @"\agent\_work"),
            string.Concat(@"\\server", @"\share\artifact"),
            string.Concat("/", "tmp/contractscribe"),
            string.Concat("/", "var/lib/runner"),
            string.Concat("/", "opt/hostedtoolcache"),
            string.Concat("/", "home/runner/work")
        };
        foreach (var path in unsafePaths)
        {
            Expect("HV118_PUBLIC_MACHINE_PATH", () => EnsureSafeText(path));
        }
        EnsureSafeText("https://github.com/SolusQuest/contract-scribe");
        EnsureSafeText("tests/fixtures/m1-host-validation/v1/protocol.json");
    }

    public static void SelfTestCredentialMarkers()
    {
        var unsafeMarkers = new[]
        {
            string.Concat("access_", "token=synthetic-value"),
            string.Concat("Authorization:", " Bearer synthetic-value"),
            string.Concat("api-", "key: synthetic-value"),
            string.Concat("client_", "secret=synthetic-value"),
            string.Concat("ghp", "-", new string('a', 20))
        };
        foreach (var marker in unsafeMarkers)
        {
            Expect("HV119_PUBLIC_CREDENTIAL_MARKER", () => EnsureSafeText(marker));
        }
    }

    private static void Expect(string code, Action action)
    {
        try
        {
            action();
        }
        catch (ProtocolException exception) when (exception.Code == code)
        {
            return;
        }
        throw new ProtocolException("HV200_PUBLIC_SCANNER_SELF_TEST");
    }

    [GeneratedRegex(@"(?i)(?<![a-z0-9:/])(?:[a-z]:[\\/](?![\\/])|\\\\[^\\\s]+\\[^\\\s]+|/(?:tmp|var|opt|home|users|mnt|private|run|etc|usr|workspace|__w)(?:/|\b))")]
    private static partial Regex MachinePath();

    [GeneratedRegex(@"(?i)(?:authorization\s*:\s*bearer\s+\S+|(?:password|access[_-]?token|api[_-]?key|client[_-]?secret)\s*[=:]\s*\S+|(?:ghp|github_pat|sk)-[a-z0-9_-]{12,})")]
    private static partial Regex CredentialMarker();

    [GeneratedRegex(@"(?i)-----begin [a-z ]+private key-----")]
    private static partial Regex PrivateKeyMarker();

    [GeneratedRegex(@"(?im)^\s*(?:this|the\s+(?:harness|protocol|ci|runner))\s+(?:provides|enforces|guarantees)\s+(?:network[- ]isolation|an?\s+offline\s+sandbox|untrusted[- ]msbuild\s+sandboxing|transient[- ]write\s+prevention)\b")]
    private static partial Regex UnsupportedPositiveClaim();
}
