namespace ContractScribe.Roslyn;

public static class FailureClassifier
{
    public static string ClassifyWorkspaceDiagnostic(string message)
    {
        return message.Contains("SDK", StringComparison.OrdinalIgnoreCase)
            || message.Contains("targeting pack", StringComparison.OrdinalIgnoreCase)
            ? "msbuild.sdk-unavailable"
            : "workspace.solution-load-failed";
    }
}
