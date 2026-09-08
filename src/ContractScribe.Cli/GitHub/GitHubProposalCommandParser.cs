using System.Text;

namespace ContractScribe.Cli;

internal sealed record GitHubProposalCommandArguments(
    CampaignOperation Operation,
    string RepositoryRoot,
    string Input,
    string Policy,
    string Snapshot,
    string State,
    string Configuration,
    string GitHubConfiguration)
{
    internal CampaignCommandArguments Campaign => new(Operation, RepositoryRoot, Input, Policy, Snapshot, State, Configuration);
}

internal sealed record GitHubProposalParseResult(
    GitHubProposalCommandArguments? Arguments,
    CampaignUsageFailure? Failure,
    bool HelpRequested);

internal static class GitHubProposalCommandParser
{
    private const int MaximumArgumentUtf8Bytes = 32 * 1024;
    private const int MaximumPathUtf8Bytes = 4_096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] RequiredOptions =
    [
        "--repository-root",
        "--input",
        "--policy",
        "--snapshot",
        "--state",
        "--configuration",
        "--github-configuration",
    ];

    internal static GitHubProposalParseResult Parse(ReadOnlySpan<string> tokens)
    {
        CampaignOperation? operation = null;
        if (tokens.Length > 0)
        {
            operation = tokens[0] switch
            {
                "start" => CampaignOperation.Start,
                "resume" => CampaignOperation.Resume,
                _ => null,
            };
        }

        if (tokens.Length is 1 && tokens[0] is "--help" or "-h"
            || tokens.Length is 2
                && operation is not null
                && tokens[1] is "--help" or "-h")
        {
            return new GitHubProposalParseResult(null, null, HelpRequested: true);
        }

        if (tokens.Length == 0 || operation is null)
        {
            return Failure("unknown-command", operation);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hasHelp = false;
        var unknownOption = false;
        var duplicateOption = false;
        var missingValue = false;
        var invalidValue = !WithinArgumentBound(tokens);
        var unexpectedOperand = false;

        for (var index = 1; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token is "--help" or "-h")
            {
                hasHelp = true;
                continue;
            }

            if (!token.StartsWith("-", StringComparison.Ordinal))
            {
                unexpectedOperand = true;
                continue;
            }

            var equals = token.IndexOf('=');
            var option = equals >= 0 ? token[..equals] : token;
            if (!RequiredOptions.Contains(option, StringComparer.Ordinal))
            {
                unknownOption = true;
                continue;
            }
            if (!seen.Add(option))
            {
                duplicateOption = true;
            }

            string? value;
            if (equals >= 0)
            {
                value = token[(equals + 1)..];
            }
            else if (index + 1 >= tokens.Length
                     || tokens[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = null;
            }
            else
            {
                value = tokens[++index];
            }

            if (string.IsNullOrEmpty(value))
            {
                missingValue = true;
                continue;
            }
            if (!IsValidValue(option, value))
            {
                invalidValue = true;
                continue;
            }
            values.TryAdd(option, value);
        }

        if (hasHelp)
        {
            return Failure("forbidden-combination", operation);
        }
        if (unknownOption)
        {
            return Failure("unknown-option", operation);
        }
        if (duplicateOption)
        {
            return Failure("duplicate-option", operation);
        }
        if (missingValue)
        {
            return Failure("missing-option-value", operation);
        }
        if (invalidValue)
        {
            return Failure("invalid-option-value", operation);
        }
        if (unexpectedOperand)
        {
            return Failure("unexpected-operand", operation);
        }
        if (RequiredOptions.Any(option => !values.ContainsKey(option)))
        {
            return Failure("missing-required-option", operation);
        }

        return new GitHubProposalParseResult(
            new GitHubProposalCommandArguments(
                operation.Value,
                values["--repository-root"],
                values["--input"],
                values["--policy"],
                values["--snapshot"],
                values["--state"],
                values["--configuration"],
                values["--github-configuration"]),
            null,
            HelpRequested: false);
    }

    private static bool WithinArgumentBound(ReadOnlySpan<string> tokens)
    {
        try
        {
            var bytes = 0;
            foreach (var token in tokens)
            {
                bytes = checked(bytes + StrictUtf8.GetByteCount(token) + 1);
            }
            return bytes <= MaximumArgumentUtf8Bytes;
        }
        catch (Exception exception) when (exception is EncoderFallbackException or OverflowException)
        {
            return false;
        }
    }

    private static bool IsValidValue(string option, string value)
    {
        if (value.Any(char.IsControl))
        {
            return false;
        }
        if (option == "--snapshot")
        {
            return value.Length is >= 1 and <= 128
                && IsSnapshotStart(value[0])
                && value.All(character => IsSnapshotStart(character) || character is '.' or '_' or ':' or '-');
        }

        try
        {
            return StrictUtf8.GetByteCount(value) is >= 1 and <= MaximumPathUtf8Bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsSnapshotStart(char character) =>
        char.IsAsciiLetterOrDigit(character);

    private static GitHubProposalParseResult Failure(string usageClass, CampaignOperation? operation) =>
        new(null, new CampaignUsageFailure(usageClass, $"cli.usage.{usageClass}", operation), HelpRequested: false);
}
