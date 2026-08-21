using System.Globalization;

namespace ContractScribe.Evaluation;

internal enum EvaluationMode
{
    Offline,
    LiveSafetyGate,
    LiveAll,
}

internal sealed record EvaluationOptions(
    EvaluationMode Mode,
    string CorpusDirectory,
    string? OutputDirectory,
    Uri? Endpoint,
    string? Model,
    string? SecretEnvironmentVariable,
    EvaluationCostPolicy CostPolicy)
{
    internal bool IsLive => Mode is EvaluationMode.LiveSafetyGate or EvaluationMode.LiveAll;

    internal static bool TryParse(string[] args, out EvaluationOptions? options, out string code)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = null;
        code = "evaluation.arguments.invalid";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--offline" or "--live" or "--safety-gate" or "--all")
            {
                if (!flags.Add(argument))
                {
                    return false;
                }

                continue;
            }

            if (argument is not ("--corpus" or "--output" or "--endpoint" or "--model"
                or "--secret-env" or "--currency" or "--cached-input-rate"
                or "--uncached-input-rate" or "--output-rate")
                || index + 1 >= args.Length
                || !values.TryAdd(argument, args[++index]))
            {
                return false;
            }
        }

        var live = flags.Contains("--live");
        if (flags.Contains("--offline") && live
            || !live && (flags.Contains("--safety-gate") || flags.Contains("--all"))
            || live && flags.Contains("--safety-gate") == flags.Contains("--all")
            || !values.TryGetValue("--corpus", out var corpus)
            || string.IsNullOrWhiteSpace(corpus))
        {
            return false;
        }

        var mode = !live
            ? EvaluationMode.Offline
            : flags.Contains("--safety-gate")
                ? EvaluationMode.LiveSafetyGate
                : EvaluationMode.LiveAll;
        Uri? endpoint = null;
        string? model = null;
        string? secretName = null;
        string? output = values.GetValueOrDefault("--output");
        if (live)
        {
            if (!values.TryGetValue("--endpoint", out var endpointText)
                || !Uri.TryCreate(endpointText, UriKind.Absolute, out endpoint)
                || !values.TryGetValue("--model", out model)
                || string.IsNullOrWhiteSpace(model)
                || !values.TryGetValue("--secret-env", out secretName)
                || !IsEnvironmentName(secretName)
                || string.IsNullOrWhiteSpace(output))
            {
                return false;
            }
        }
        else if (values.Keys.Any(key => key is "--endpoint" or "--model" or "--secret-env"))
        {
            return false;
        }

        if (!TryCost(values, out var costPolicy)
            || live && costPolicy?.IsPriced != true)
        {
            return false;
        }

        options = new EvaluationOptions(
            mode,
            corpus,
            output,
            endpoint,
            model,
            secretName,
            costPolicy!);
        code = string.Empty;
        return true;
    }

    private static bool TryCost(
        IReadOnlyDictionary<string, string> values,
        out EvaluationCostPolicy? policy)
    {
        policy = null;
        var supplied = new[]
        {
            "--currency", "--cached-input-rate", "--uncached-input-rate", "--output-rate",
        }.Count(values.ContainsKey);
        if (supplied is not (0 or 4))
        {
            return false;
        }

        if (supplied == 0)
        {
            policy = EvaluationCostPolicy.Unpriced;
            return true;
        }

        return TryRate(values["--cached-input-rate"], out var cached)
            && TryRate(values["--uncached-input-rate"], out var uncached)
            && TryRate(values["--output-rate"], out var output)
            && EvaluationCostPolicy.TryCreate(values["--currency"], cached, uncached, output, out policy);
    }

    private static bool TryRate(string value, out long result) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
        && result >= 0;

    private static bool IsEnvironmentName(string value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 128
        && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
