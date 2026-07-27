using System.Text;

namespace ContractScribe.HostValidation;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await DispatchAsync(args).ConfigureAwait(false);
        }
        catch (ProtocolException exception)
        {
            Console.Error.WriteLine(exception.Code);
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("HV998_CANCELLED");
            return 3;
        }
        catch
        {
            Console.Error.WriteLine("HV999_INTERNAL_ERROR");
            return 4;
        }
    }

    private static async Task<int> DispatchAsync(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ProtocolException("HV001_COMMAND_REQUIRED");
        }

        var command = args[0];
        var options = ParseOptions(args[1..]);
        switch (command)
        {
            case "lock-bundle":
                {
                    var artifactLock = BundleValidator.CreateLock(Required(options, "--root"));
                    Console.WriteLine($"HV000_OK {artifactLock.BundleId}");
                    return 0;
                }
            case "lock-protected-inputs":
                {
                    var manifest = BundleValidator.CreateProtectedInputs(Required(options, "--root"));
                    Console.WriteLine($"HV000_PROTECTED_INPUTS {manifest.Entries.Count}");
                    return 0;
                }
            case "validate-bundle":
                {
                    var context = BundleValidator.Validate(
                        Required(options, "--root"),
                        options.ContainsKey("--require-review"),
                        Optional(options, "--review"));
                    Console.WriteLine($"HV000_OK {context.Lock.BundleId}");
                    return 0;
                }
            case "dry-run":
                {
                    var context = BundleValidator.Validate(Required(options, "--root"));
                    var cellId = Required(options, "--cell");
                    if (!context.Protocol.RequiredCells.Any(cell => cell.CellId == cellId))
                    {
                        throw new ProtocolException("HV002_CELL_UNKNOWN");
                    }
                    var runCount = context.Vectors.ExpandExpectedRuns().Count(run => run.CellId == cellId);
                    Console.WriteLine($"HV000_DRY_RUN {cellId} {runCount}");
                    return 0;
                }
            case "run-cell":
                {
                    var root = Required(options, "--root");
                    var subjectManifest = Required(options, "--subject-manifest");
                    var review = Required(options, "--review");
                    var cell = Required(options, "--cell");
                    var incompleteOutput = Required(options, "--incomplete-output");
                    var output = Required(options, "--output");
                    if (Path.GetFullPath(output).Equals(
                        Path.GetFullPath(incompleteOutput),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    {
                        throw new ProtocolException("HV194_OUTPUT_PATH_COLLISION");
                    }
                    CanonicalJson.InvalidateOutput(output);
                    CanonicalJson.InvalidateOutput(incompleteOutput);
                    try
                    {
                        var evidence = await CellExecutor.RunAsync(
                            root,
                            subjectManifest,
                            review,
                            cell,
                            output).ConfigureAwait(false);
                        Console.WriteLine($"HV000_CELL_EXECUTED {evidence.Cell.CellId} {evidence.Outcome}");
                        return 0;
                    }
                    catch (ProtocolException exception)
                    {
                        IncompleteEvidenceWriter.TryWrite(
                            root,
                            subjectManifest,
                            review,
                            cell,
                            incompleteOutput,
                            exception.Code,
                            IncompleteEvidenceWriter.Classify(exception.Code));
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        IncompleteEvidenceWriter.TryWrite(
                            root,
                            subjectManifest,
                            review,
                            cell,
                            incompleteOutput,
                            "HV998_CANCELLED",
                            "harness-or-ci-cancelled");
                        throw;
                    }
                }
            case "validate-cell":
                {
                    var evidence = EvidenceValidator.ValidateCell(
                        Required(options, "--root"),
                        Required(options, "--evidence"),
                        Optional(options, "--review"));
                    Console.WriteLine($"HV000_CELL_VALID {evidence.Cell.CellId}");
                    return 0;
                }
            case "validate-incomplete":
                {
                    _ = EvidenceValidator.ValidateIncomplete(
                        Required(options, "--root"),
                        Required(options, "--evidence"),
                        Optional(options, "--review"));
                    Console.WriteLine("HV000_INCOMPLETE_VALID");
                    return 0;
                }
            case "aggregate":
                {
                    var evidencePaths = Required(options, "--evidence").Split(';', StringSplitOptions.RemoveEmptyEntries);
                    var output = Required(options, "--output");
                    EnsureDistinctOutput(output, evidencePaths);
                    CanonicalJson.InvalidateOutput(output);
                    var aggregate = EvidenceValidator.Aggregate(
                        Required(options, "--root"),
                        evidencePaths,
                        output,
                        Optional(options, "--review"));
                    Console.WriteLine($"HV000_AGGREGATE {aggregate.Outcome}");
                    return 0;
                }
            case "prepare-public":
                {
                    var source = Required(options, "--source");
                    var output = Required(options, "--output");
                    EnsureDistinctOutput(output, [source]);
                    CanonicalJson.InvalidateOutput(output);
                    EvidenceValidator.PreparePublicArtifact(
                        Required(options, "--root"),
                        Required(options, "--kind"),
                        source,
                        output,
                        Optional(options, "--review"));
                    Console.WriteLine("HV000_PUBLIC_PREPARED");
                    return 0;
                }
            case "self-test":
                {
                    await HarnessSelfTest.RunAsync(Required(options, "--root")).ConfigureAwait(false);
                    Console.WriteLine("HV000_SELF_TEST");
                    return 0;
                }
            case "fake-subject":
                return await RunFakeSubjectAsync(options).ConfigureAwait(false);
            case "fake-child":
                await Task.Delay(500).ConfigureAwait(false);
                return 0;
            default:
                throw new ProtocolException("HV003_COMMAND_UNKNOWN");
        }
    }

    private static async Task<int> RunFakeSubjectAsync(IReadOnlyDictionary<string, string?> options)
    {
        var requestPath = Required(options, "--request");
        var behavior = Required(options, "--behavior");
        var request = CanonicalJson.DeserializeStrict<SubjectRequest>(requestPath, 64 * 1024, requireCanonical: true);
        var response = new SubjectResponse(
            "contractscribe-m1-host-validation-subject-response-v1",
            request.VectorId,
            request.RunId,
            "started",
            "normal",
            "compliant",
            "succeeded",
            null,
            null,
            null,
            "committed",
            "published",
            "internally-enforceable",
            "self-test.observation.stable");

        switch (behavior)
        {
            case "success":
                CanonicalJson.WriteCanonical(request.ResponsePath, response);
                return 0;
            case "modify-protected":
                File.AppendAllText(Path.Join(request.RepositoryRoot, "Sample.cs"), "// changed\n", new UTF8Encoding(false));
                CanonicalJson.WriteCanonical(request.ResponsePath, response);
                return 0;
            case "allowed-obj":
                Directory.CreateDirectory(Path.Join(request.RepositoryRoot, "obj"));
                File.WriteAllText(Path.Join(request.RepositoryRoot, "obj", "design-time.marker"), "synthetic\n", new UTF8Encoding(false));
                CanonicalJson.WriteCanonical(request.ResponsePath, response);
                return 0;
            case "restore-marker":
                Directory.CreateDirectory(Path.Join(request.RepositoryRoot, "obj"));
                File.WriteAllText(Path.Join(request.RepositoryRoot, "obj", "project.assets.json"), "{}\n", new UTF8Encoding(false));
                CanonicalJson.WriteCanonical(request.ResponsePath, response);
                return 0;
            case "spawn-child":
                {
                    var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet")
                    {
                        UseShellExecute = false,
                        ArgumentList = { typeof(Program).Assembly.Location, "fake-child" }
                    }) ?? throw new ProtocolException("HV920_FAKE_CHILD_START");
                    await child.WaitForExitAsync().ConfigureAwait(false);
                    child.Dispose();
                    CanonicalJson.WriteCanonical(request.ResponsePath, response);
                    return 0;
                }
            case "unsafe-output":
                Console.Out.Write($"access_token={BuildSyntheticCredentialMarker()}");
                CanonicalJson.WriteCanonical(request.ResponsePath, response);
                return 0;
            case "stdout-overflow":
                Console.Out.Write(new string('x', 128 * 1024));
                CanonicalJson.WriteCanonical(request.ResponsePath, response);
                return 0;
            case "invalid-utf8":
                Console.OpenStandardOutput().Write([0xff, 0xfe, 0xfd]);
                CanonicalJson.WriteCanonical(request.ResponsePath, response);
                return 0;
            case "dual-stream":
                Console.Out.Write("synthetic-stdout");
                Console.Error.Write("synthetic-stderr");
                CanonicalJson.WriteCanonical(request.ResponsePath, response);
                return 0;
            case "exception-before-response":
                throw new ProtocolException("HV921_SYNTHETIC_PRE_RESPONSE");
            case "exception-after-response":
                CanonicalJson.WriteCanonical(request.ResponsePath, response);
                throw new ProtocolException("HV922_SYNTHETIC_POST_RESPONSE");
            case "controlled-cancel":
                await ReachGateAsync(request).ConfigureAwait(false);
                CanonicalJson.WriteCanonical(
                    request.ResponsePath,
                    response with
                    {
                        AuditOutcome = null,
                        ExecutionOutcome = "cancelled",
                        FailureRegistryIdentity = new string('1', 64),
                        FailureCode = "host.cancelled",
                        FailureStage = "audit",
                        TerminalState = "committed",
                        ArtifactState = "absent",
                        ObservationCode = "cancellation.cancelled-before-commit"
                    });
                return 0;
            case "controlled-kill":
                await ReachGateAsync(request).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                return 0;
            case "hang":
                await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                return 0;
            default:
                throw new ProtocolException("HV923_SYNTHETIC_BEHAVIOR_UNKNOWN");
        }
    }

    private static string BuildSyntheticCredentialMarker() =>
        string.Concat("ghp", "-", new string('a', 20));

    private static async Task ReachGateAsync(SubjectRequest request)
    {
        if (request.ControlRoot is null || request.SynchronizationGates.Count != 1)
        {
            throw new ProtocolException("HV924_SYNTHETIC_CONTROL_INVALID");
        }
        Directory.CreateDirectory(request.ControlRoot);
        var gateName = request.SynchronizationGates[0];
        File.WriteAllText(Path.Join(request.ControlRoot, $"{gateName}.reached"), string.Empty);
        if (request.ControlAction == "external-kill")
        {
            return;
        }
        var release = Path.Join(request.ControlRoot, $"{gateName}.release");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!File.Exists(release))
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new ProtocolException("HV925_SYNTHETIC_CONTROL_TIMEOUT");
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (!name.StartsWith("--", StringComparison.Ordinal) || !result.TryAdd(name, null))
            {
                throw new ProtocolException("HV004_OPTION_INVALID");
            }
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[name] = args[++index];
            }
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string?> options, string name)
    {
        if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ProtocolException("HV005_OPTION_REQUIRED");
        }
        return value;
    }

    private static string? Optional(IReadOnlyDictionary<string, string?> options, string name) =>
        options.TryGetValue(name, out var value) ? value : null;

    private static void EnsureDistinctOutput(string output, IEnumerable<string> inputs)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullOutput = Path.GetFullPath(output);
        if (inputs.Any(input => fullOutput.Equals(Path.GetFullPath(input), comparison)))
        {
            throw new ProtocolException("HV194_OUTPUT_PATH_COLLISION");
        }
    }
}
