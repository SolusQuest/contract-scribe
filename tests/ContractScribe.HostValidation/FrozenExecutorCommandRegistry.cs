namespace ContractScribe.HostValidation;

public sealed record FrozenExecutorCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> ArrangementPaths);

public static class FrozenExecutorCommandRegistry
{
    private static FrozenExecutorCommand SubjectEntryPoint(string fixture) =>
        new(
            "subject-entrypoint",
            [],
            [$".contractscribe-validation/{fixture}/arrangement.json"]);

    private static readonly IReadOnlyDictionary<string, FrozenExecutorCommand> Commands =
        new Dictionary<string, FrozenExecutorCommand>(StringComparer.Ordinal)
        {
            ["failure.launch-before-entry"] = new(
                "missing-executable",
                [],
                [".contractscribe-validation/process.launch-missing/arrangement.json"]),
            ["failure.runtime-load-before-entry"] = new(
                "dotnet",
                [
                    "repository:.contractscribe-validation/process.runtime-load-invalid/invalid-entrypoint.dll",
                    "{request}",
                    "{response}"
                ],
                [
                    ".contractscribe-validation/process.runtime-load-invalid/invalid-entrypoint.dll",
                    ".contractscribe-validation/process.runtime-load-invalid/arrangement.json"
                ]),
            ["failure.permission-before-entry"] = new(
                "repository:.contractscribe-validation/process.permission-denied/denied-entrypoint",
                [],
                [
                    ".contractscribe-validation/process.permission-denied/denied-entrypoint",
                    ".contractscribe-validation/process.permission-denied/arrangement.json"
                ]),
            ["failure.startup-timeout"] = SubjectEntryPoint("process.startup-gate"),
            ["failure.out-of-memory"] = SubjectEntryPoint("process.fatal-oom"),
            ["failure.stack-overflow"] = SubjectEntryPoint("process.fatal-stack"),
            ["failure.abort"] = SubjectEntryPoint("process.fatal-abort"),
            ["publication.kill-before-commit"] = SubjectEntryPoint("gate.publication-before-commit"),
            ["publication.kill-after-commit"] = SubjectEntryPoint("gate.publication-after-commit"),
            ["publication.cross-volume-rejected"] = SubjectEntryPoint("publication.cross-volume"),
            ["path.symlink-escape"] = SubjectEntryPoint("path.unix-symlink"),
            ["path.junction-reparse-escape"] = SubjectEntryPoint("path.windows-junction-reparse"),
            ["bounds.memory-runtime"] = SubjectEntryPoint("bounds.memory-runtime"),
            ["bounds.forced-termination"] = SubjectEntryPoint("bounds.external-kill")
        };

    public static FrozenExecutorCommand Get(string vectorId) =>
        Commands.TryGetValue(vectorId, out var command)
            ? command
            : throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");

    public static void ValidateCatalog(IReadOnlyList<VectorDefinition> vectors)
    {
        var expected = vectors
            .Where(vector => vector.ExecutorKind is "external-process" or "platform-fixture")
            .Select(vector => vector.VectorId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = Commands.Keys.Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new ProtocolException("HV206_EXECUTOR_ARRANGEMENT_MISMATCH");
        }
    }
}
