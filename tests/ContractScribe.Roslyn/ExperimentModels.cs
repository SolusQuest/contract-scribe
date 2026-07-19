namespace ContractScribe.Roslyn;

public enum ExperimentStatus
{
    Succeeded,
    ClassifiedFailure,
    InvalidInput,
    InternalError,
}

public enum FailurePhase
{
    Input,
    MsbuildEnvironment,
    WorkspaceLoad,
    Compilation,
    SymbolIdentity,
    Serialization,
}

public sealed record DiagnosticRecord(string Code, string Severity);

public sealed record SymbolRecord(string DocumentationCommentId, string Kind, string Name);

public sealed record ProjectPayload(string ProjectId, IReadOnlyList<SymbolRecord> Symbols);

public sealed record SemanticPayload(IReadOnlyList<ProjectPayload> Projects);

public sealed record ExperimentResult(
    string FormatVersion,
    ExperimentStatus Status,
    FailurePhase? FailurePhase,
    string? FailureCode,
    SemanticPayload? SemanticPayload,
    IReadOnlyList<DiagnosticRecord> Diagnostics)
{
    public int ExitCode => Status switch
    {
        ExperimentStatus.Succeeded => 0,
        ExperimentStatus.ClassifiedFailure => 1,
        ExperimentStatus.InvalidInput => 2,
        ExperimentStatus.InternalError => 3,
        _ => 3,
    };

    public static ExperimentResult Success(SemanticPayload payload)
    {
        return new(
            ExperimentFormat.Version,
            ExperimentStatus.Succeeded,
            null,
            null,
            payload,
            Array.Empty<DiagnosticRecord>());
    }

    public static ExperimentResult Failure(
        ExperimentStatus status,
        FailurePhase? phase,
        string? code,
        IReadOnlyList<DiagnosticRecord>? diagnostics = null)
    {
        return new(
            ExperimentFormat.Version,
            status,
            phase,
            code,
            null,
            diagnostics ?? Array.Empty<DiagnosticRecord>());
    }
}

public sealed record ExperimentExecution(ExperimentResult Result, byte[]? SemanticPayloadBytes);

public static class ExperimentFormat
{
    public const string Version = "m0.4-experiment-v1";
    public const string SemanticPayloadVersion = "m0.4-semantic-payload-v1";
}

public static class FailureRegistry
{
    private static readonly IReadOnlyDictionary<FailurePhase, IReadOnlySet<string>> Codes =
        new Dictionary<FailurePhase, IReadOnlySet<string>>
        {
            [FailurePhase.Input] = new HashSet<string>(StringComparer.Ordinal)
            {
                "input.solution-not-found",
                "input.solution-not-supported",
            },
            [FailurePhase.MsbuildEnvironment] = new HashSet<string>(StringComparer.Ordinal)
            {
                "msbuild.sdk-unavailable",
                "msbuild.registration-failed",
            },
            [FailurePhase.WorkspaceLoad] = new HashSet<string>(StringComparer.Ordinal)
            {
                "workspace.solution-load-failed",
                "workspace.project-graph-mismatch",
            },
            [FailurePhase.Compilation] = new HashSet<string>(StringComparer.Ordinal)
            {
                "compilation.errors",
                "compilation.reference-missing",
            },
            [FailurePhase.SymbolIdentity] = new HashSet<string>(StringComparer.Ordinal)
            {
                "symbol.missing-documentation-id",
                "symbol.duplicate-identity",
            },
            [FailurePhase.Serialization] = new HashSet<string>(StringComparer.Ordinal)
            {
                "serialization.semantic-payload-failed",
            },
        };

    public static bool IsKnown(FailurePhase phase, string code)
    {
        return Codes.TryGetValue(phase, out var phaseCodes) && phaseCodes.Contains(code);
    }

    public static IReadOnlyDictionary<FailurePhase, IReadOnlySet<string>> Snapshot()
    {
        return Codes;
    }
}

internal sealed class ExperimentFailureException : Exception
{
    public ExperimentFailureException(FailurePhase phase, string code)
    {
        Phase = phase;
        Code = code;
    }

    public FailurePhase Phase { get; }

    public string Code { get; }
}
