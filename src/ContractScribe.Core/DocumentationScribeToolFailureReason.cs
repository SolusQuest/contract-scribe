namespace ContractScribe.Core;

/// <summary>A closed observation from the original typed tool boundary, without input values.</summary>
public enum DocumentationScribeToolFailureReason
{
    Unknown,
    ArgumentSchema,
    InvalidRequest,
    InvalidCursor,
    ScopeDenied,
    Stale,
    UnsafeObject,
    InvalidEncoding,
    InvalidResult,
    ResultEncoding,
    ResultCanonical,
    Evidence,
    ReleaseRejected,
}

/// <summary>Creates inert fixed-vocabulary failure references; it grants no tool authority.</summary>
public static class DocumentationScribeToolFailureDiagnostics
{
    public static string CreateReference(string operationId, DocumentationScribeToolFailureReason reason)
    {
        var operation = operationId switch
        {
            "repository.read-excerpt" => "read-excerpt",
            "repository.list-files" => "list-files",
            "repository.search-text" => "search-text",
            "get-target-evidence" => "get-target-evidence",
            _ => "unknown",
        };
        var category = reason switch
        {
            DocumentationScribeToolFailureReason.ArgumentSchema => "argument-schema",
            DocumentationScribeToolFailureReason.InvalidRequest => "invalid-request",
            DocumentationScribeToolFailureReason.InvalidCursor => "invalid-cursor",
            DocumentationScribeToolFailureReason.ScopeDenied => "scope-denied",
            DocumentationScribeToolFailureReason.Stale => "stale",
            DocumentationScribeToolFailureReason.UnsafeObject => "unsafe-object",
            DocumentationScribeToolFailureReason.InvalidEncoding => "invalid-encoding",
            DocumentationScribeToolFailureReason.InvalidResult => "invalid-result",
            DocumentationScribeToolFailureReason.ResultEncoding => "result-encoding",
            DocumentationScribeToolFailureReason.ResultCanonical => "result-canonical",
            DocumentationScribeToolFailureReason.Evidence => "evidence",
            DocumentationScribeToolFailureReason.ReleaseRejected => "release-rejected",
            _ => "unknown",
        };
        return "tool-failure." + operation + "." + category;
    }

    internal static string? FromResult(DocumentationScribeRunResult? result)
    {
        if (result?.Stop is not DocumentationScribeFailureTerminal { Code: DocumentationScribeFailureCode.ToolProtocol })
            return null;
        var diagnostics = result.RunEnvelope.Diagnostics;
        return diagnostics.Length == 1 && diagnostics[0].Code == "scribe.diagnostic.tool-failure"
            && diagnostics[0].Stage == "tool" ? FromReference(diagnostics[0].ReferenceId) : null;
    }

    internal static bool IsKnownCode(string? code) => code is not null && code.Length <= 96
        && code.StartsWith("campaign.", StringComparison.Ordinal) && FromReference(code[9..]) == code;

    private static string? FromReference(string? reference)
    {
        if (reference is null) return null;
        var parts = reference.Split('.');
        return parts.Length == 3 && parts[0] == "tool-failure"
            && parts[1] is "read-excerpt" or "list-files" or "search-text" or "get-target-evidence" or "unknown"
            && parts[2] is "argument-schema" or "invalid-request" or "invalid-cursor" or "scope-denied"
                or "stale" or "unsafe-object" or "invalid-encoding" or "invalid-result" or "result-encoding"
                or "result-canonical" or "evidence" or "release-rejected"
            ? "campaign." + reference : null;
    }
}
