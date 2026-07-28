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
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var claimBlock = NetworkClaimSetRegistry.RenderProtocolBlock();
        var firstBlock = normalized.IndexOf(claimBlock, StringComparison.Ordinal);
        if (firstBlock < 0
            || normalized.IndexOf(
                claimBlock,
                firstBlock + claimBlock.Length,
                StringComparison.Ordinal) >= 0
            || NetworkClaimSetRegistry.Members.Any(member =>
                CountOccurrences(normalized, member.Text) != 1))
        {
            throw new ProtocolException("HV199_PUBLIC_UNSUPPORTED_CLAIM");
        }

        foreach (var line in normalized.Split('\n'))
        {
            if (RepositoryBuildCannotAccessSecrets().IsMatch(line)
                || NetworkCapabilityAssertion().IsMatch(line)
                || PositiveNetworkAvailabilityClaim().IsMatch(line)
                    && !ExplicitNetworkLimitation().IsMatch(line))
            {
                throw new ProtocolException("HV199_PUBLIC_UNSUPPORTED_CLAIM");
            }
            if (!UnsupportedConcept().IsMatch(line)
                || ExplicitLimitation().IsMatch(line))
            {
                continue;
            }
            if (PositiveAssertion().IsMatch(line))
            {
                throw new ProtocolException("HV199_PUBLIC_UNSUPPORTED_CLAIM");
            }
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
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
            string.Concat("/", "home/runner/work"),
            string.Concat("/", "root"),
            string.Concat("/", "root/private.cs"),
            string.Concat("/", "srv"),
            string.Concat("/", "srv/build/output.log"),
            string.Concat("/", "data"),
            string.Concat("/", "data/agent/repository"),
            string.Concat("/", "github"),
            string.Concat("/", "github/workspace/source.cs")
        };
        foreach (var path in unsafePaths)
        {
            Expect("HV118_PUBLIC_MACHINE_PATH", () => EnsureSafeText(path));
        }
        EnsureSafeText("https://github.com/SolusQuest/contract-scribe");
        EnsureSafeText("https://json-schema.org/draft/2020-12/schema");
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
        EnsureSafeText("temporary-disk-high-water-gate");
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

    [GeneratedRegex(@"(?i)(?<![a-z0-9:/])(?:[a-z]:[\\/](?![\\/])|\\\\[^\\\s]+\\[^\\\s]+|/(?!/)(?:[a-z0-9._-]+/)*(?:[a-z0-9._-]+))")]
    private static partial Regex MachinePath();

    [GeneratedRegex(@"(?i)(?:authorization\s*:\s*bearer\s+\S+|(?:password|access[_-]?token|api[_-]?key|client[_-]?secret)\s*[=:]\s*\S+|(?<![a-z0-9])(?:ghp|github_pat|sk)-[a-z0-9_-]{12,})")]
    private static partial Regex CredentialMarker();

    [GeneratedRegex(@"(?i)-----begin [a-z ]+private key-----")]
    private static partial Regex PrivateKeyMarker();

    [GeneratedRegex(@"(?i)\b(?:network|egress|credential|secret|untrusted[- ]msbuild|transient[- ]write)[- ](?:isolation|sandbox(?:ing)?|prevention)\b|\b(?:offline|sandboxed)\b|\b(?:blocks?|prevents?|denies?|disables?)\b.{0,80}\b(?:outbound|egress)\b.{0,40}\b(?:access|connections?|network|traffic)\b|\b(?:outbound|egress)\b.{0,40}\b(?:access|connections?|network|traffic)\b.{0,80}\b(?:blocked|prevented|denied|disabled)\b")]
    private static partial Regex UnsupportedConcept();

    [GeneratedRegex(@"(?i)\b(?:does\s+not|do\s+not|cannot)\s+(?:claim|guarantee|provide|enforce|block|prevent|deny|disable)\b|\b(?:no|without|not(?:\s+an?)?)\s+(?:network|egress|credential|secret|offline|sandbox|sandboxed|untrusted[- ]msbuild|transient[- ]write)[- ]")]
    private static partial Regex ExplicitLimitation();

    [GeneratedRegex(@"(?i)\b(?:guarantees?|provides?|enforces?|ensures?|blocks?|prevents?|denies?|disables?)\b|\b(?:is|are|runs?|ran)\b.*\b(?:isolated|isolation|offline|sandbox|sandboxed|prevention|blocked|prevented|denied|disabled)\b|\b(?:isolation|sandbox(?:ing)?|prevention)\b\s+(?:is|are)\s+(?:provided|enforced|guaranteed)\b")]
    private static partial Regex PositiveAssertion();

    [GeneratedRegex(@"(?i)\brepository[- ]controlled\s+msbuild\s+cannot\s+access\s+(?:credentials?|secrets?)\b")]
    private static partial Regex RepositoryBuildCannotAccessSecrets();

    [GeneratedRegex(
        @"(?i)\b(?:contractscribe|host|validator|validation|tool|runtime|audit|execution)\b.{0,40}\b(?:cannot|can't|does\s+not)\b.{0,40}\b(?:reach|access|connect)\b.{0,20}\b(?:the\s+)?(?:internet|network|external)\b|\b(?:contractscribe|host|validator|validation|tool|runtime|audit|execution)\b.{0,24}\bhas\s+no\s+external\s+connectivity\b|\b(?:outbound|egress)\s+(?:connections?|traffic|access)\b.{0,24}\b(?:is|are)\s+(?:impossible|unavailable|disabled)\b")]
    private static partial Regex NetworkCapabilityAssertion();

    [GeneratedRegex(
        @"(?i)\b(?:internet|network|connectivity|outbound|external\s+connections?|egress)\b.{0,32}\b(?:is|are|was|were|remains?)\s+(?:disabled|unavailable|impossible|blocked|prevented|denied|absent|disconnected)\b|\b(?:has|have|with)\s+no\s+(?:internet|network|external)?\s*(?:access|connectivity|connections?)\b|\b(?:no|without)\s+(?:internet|network|external)\s+(?:access|connectivity|connections?)\b|\b(?:runtime|host|validator|validation|contractscribe|tool|ci)\b.{0,24}\b(?:is|runs?|remains?)\s+(?:air[- ]?gapped|disconnected)\b")]
    private static partial Regex PositiveNetworkAvailabilityClaim();

    [GeneratedRegex(
        @"(?i)\b(?:does\s+not|do\s+not|cannot)\s+(?:claim|guarantee|provide|enforce)\b|\bnot\s+(?:an?\s+)?(?:egress|network).{0,16}\bclaim\b|\bno\s+declared\s+network(?:-dependent)?\s+(?:dependency|operation)\b|\bcontractscribe\s+initiates\s+no\b")]
    private static partial Regex ExplicitNetworkLimitation();
}
