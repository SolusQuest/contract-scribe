using ContractScribe.Core.Hosting;
using ContractScribe.Roslyn;

namespace ContractScribe.Cli;

internal sealed record CliDiagnostic(string Code, string Message)
{
    public string ToLine() => $"{Code}: {Message}\n";
}

internal static class CliDiagnostics
{
    private static readonly IReadOnlyDictionary<string, string> Messages =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cli.usage.unknown-command"] =
                "the command is not recognized; run 'contract-scribe --help' for usage",
            ["cli.usage.unknown-option"] = "the option is not recognized for this command",
            ["cli.usage.missing-required-option"] = "a required option is missing",
            ["cli.usage.duplicate-option"] = "an option was specified more than once",
            ["cli.usage.missing-option-value"] = "an option is missing its required value",
            ["cli.usage.invalid-option-value"] = "an option value is not permitted",
            ["cli.usage.unexpected-operand"] = "positional operands are not supported",
            ["cli.usage.forbidden-combination"] = "the argument combination is not permitted",
            ["cli.preflight.repository-root"] =
                "<repository-root> does not exist or is not a directory",
            ["cli.preflight.input"] =
                "<input> does not exist, is not a regular file, or has an unsupported extension",
            ["cli.preflight.input-escape"] =
                "<input> resolves outside <repository-root>",
            ["cli.preflight.policy"] =
                "<policy> does not exist or is not a regular file",
            ["cli.preflight.policy-escape"] =
                "<policy> resolves outside <repository-root>",
            ["cli.preflight.output-parent"] =
                "the parent directory of <output> does not exist",
            ["cli.preflight.output-inside-root"] =
                "<output> does not resolve outside <repository-root>",
            ["cli.preflight.output-reparse"] =
                "<output> is a symbolic link, junction, or reparse point",
            ["cli.cancel.requested"] =
                "a cancellation signal was received; cancelling",
            ["cli.host.unknown-terminal"] =
                "the host reported an unknown or unmapped terminal class",
        };

    public static CliDiagnostic Create(string code) =>
        new(code, Messages.TryGetValue(code, out var message)
            ? message
            : throw new ArgumentOutOfRangeException(nameof(code)));

    public static CliDiagnostic Host(
        HostDiagnosticFact diagnostic,
        LoaderFact? loaderFact = null) =>
        new(
            diagnostic.Code,
            loaderFact is null
                ? "the audit host reported a controlled failure"
                : $"the audit host reported a controlled failure ({loaderFact.Stage}:{loaderFact.Code})");

    public static CliDiagnostic SkippedSummary(string breakdown) =>
        new("cli.audit.skipped-summary", $"skipped results by reason: {breakdown}");

    public static void Write(TextWriter writer, string code) =>
        writer.Write(Create(code).ToLine());
}
