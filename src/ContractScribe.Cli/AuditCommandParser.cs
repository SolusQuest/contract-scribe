namespace ContractScribe.Cli;

internal sealed record AuditCommandArguments(
    string RepositoryRoot,
    string Input,
    string Policy,
    string Output);

internal sealed record AuditUsageFailure(string UsageClass, string Code);

internal sealed record AuditParseResult(
    AuditCommandArguments? Arguments,
    AuditUsageFailure? Failure);

internal static class AuditCommandParser
{
    private static readonly string[] RequiredOptions =
    [
        "--repository-root",
        "--input",
        "--policy",
        "--output",
    ];

    public static AuditParseResult Parse(ReadOnlySpan<string> tokens)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hasHelp = false;
        var unknownOption = false;
        var duplicateOption = false;
        var missingValue = false;
        var invalidValue = false;
        var unexpectedOperand = false;

        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token is "--help" or "-h")
            {
                hasHelp = true;
                continue;
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
            {
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
                if (value.Any(char.IsControl))
                {
                    invalidValue = true;
                    continue;
                }
                values.TryAdd(option, value);
                continue;
            }

            unexpectedOperand = true;
        }

        if (hasHelp && tokens.Length != 1)
        {
            return Failure("forbidden-combination");
        }
        if (unknownOption)
        {
            return Failure("unknown-option");
        }
        if (duplicateOption)
        {
            return Failure("duplicate-option");
        }
        if (missingValue)
        {
            return Failure("missing-option-value");
        }
        if (invalidValue)
        {
            return Failure("invalid-option-value");
        }
        if (unexpectedOperand)
        {
            return Failure("unexpected-operand");
        }
        if (RequiredOptions.Any(option => !values.ContainsKey(option)))
        {
            return Failure("missing-required-option");
        }

        return new AuditParseResult(
            new AuditCommandArguments(
                values["--repository-root"],
                values["--input"],
                values["--policy"],
                values["--output"]),
            null);
    }

    private static AuditParseResult Failure(string usageClass) =>
        new(null, new AuditUsageFailure(usageClass, $"cli.usage.{usageClass}"));
}
