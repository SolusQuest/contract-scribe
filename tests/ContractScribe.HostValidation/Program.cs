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
            case "provision-fixtures":
                {
                    var root = Required(options, "--root");
                    var subjectPath = Required(options, "--subject-manifest");
                    var context = BundleValidator.Validate(root);
                    var subject = CanonicalJson.DeserializeStrict<ExecutionSubjectManifest>(
                        subjectPath,
                        4 * 1024 * 1024,
                        requireCanonical: true);
                    foreach (var cell in subject.Cells)
                    {
                        foreach (var fixture in cell.Fixtures)
                        {
                            var vector = context.Vectors.Vectors.Single(candidate =>
                                candidate.VectorId == fixture.VectorId);
                            var expectedRoot =
                                $"tests/fixtures/m1-host-validation/runtime/{cell.Materialization.CellId}/{vector.VectorId}";
                            if (fixture.RepositoryRoot != expectedRoot)
                            {
                                throw new ProtocolException("HV234_FIXTURE_CONTRACT_MISMATCH");
                            }
                            FixtureRecipeRegistry.Provision(
                                RepositoryPaths.ResolveConfined(root, expectedRoot, mustExist: false),
                                cell.Materialization.CellId,
                                vector);
                            Console.WriteLine(
                                $"HV000_FIXTURE {cell.Materialization.CellId} {vector.VectorId} {FixtureRecipeRegistry.ExpectedRepositoryIdentity(cell.Materialization.CellId, vector)}");
                        }
                    }
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
                    var context = BundleValidator.Validate(root, requireReview: true, review);
                    _ = BundleValidator.ValidateReview(context.Root, review, context.Lock.BundleId);
                    var subject = CellExecutor.ValidateSubjectManifest(context, subjectManifest);
                    OutputPathGuard.Validate(
                        context,
                        SubjectInputPaths(context.Root, subject).Append(subjectManifest).Append(review),
                        output,
                        incompleteOutput);
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
                    catch
                    {
                        IncompleteEvidenceWriter.TryWrite(
                            root,
                            subjectManifest,
                            review,
                            cell,
                            incompleteOutput,
                            "HV999_INTERNAL_ERROR",
                            "protocol-failure");
                        throw;
                    }
                }
            case "validate-cell":
                {
                    var evidence = EvidenceValidator.ValidateCell(
                        Required(options, "--root"),
                        Required(options, "--evidence"),
                        Optional(options, "--review"),
                        Required(options, "--subject-manifest"));
                    Console.WriteLine($"HV000_CELL_VALID {evidence.Cell.CellId}");
                    return 0;
                }
            case "validate-incomplete":
                {
                    _ = EvidenceValidator.ValidateIncomplete(
                        Required(options, "--root"),
                        Required(options, "--evidence"),
                        Optional(options, "--review"),
                        Required(options, "--subject-manifest"));
                    Console.WriteLine("HV000_INCOMPLETE_VALID");
                    return 0;
                }
            case "aggregate":
                {
                    var root = Required(options, "--root");
                    var evidencePaths = Required(options, "--evidence").Split(';', StringSplitOptions.RemoveEmptyEntries);
                    var output = Required(options, "--output");
                    var review = Required(options, "--review");
                    var subjectManifest = Required(options, "--subject-manifest");
                    var supersedes = Optional(options, "--supersedes")?
                        .Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];
                    var context = BundleValidator.Validate(root, requireReview: true, review);
                    var subject = CellExecutor.ValidateSubjectManifest(context, subjectManifest);
                    OutputPathGuard.Validate(
                        context,
                        evidencePaths.Concat(supersedes).Append(review).Append(subjectManifest)
                            .Concat(SubjectInputPaths(context.Root, subject)),
                        output);
                    CanonicalJson.InvalidateOutput(output);
                    var aggregate = EvidenceValidator.Aggregate(
                        root,
                        evidencePaths,
                        output,
                        review,
                        subjectManifest,
                        new AggregateFinalizationIdentity(
                            Required(options, "--matrix-result"),
                            Required(options, "--publication-base-revision")),
                        supersedes);
                    Console.WriteLine($"HV000_AGGREGATE {aggregate.Outcome}");
                    return 0;
                }
            case "validate-aggregate":
                {
                    var evidencePaths = Required(options, "--cell-evidence")
                        .Split(';', StringSplitOptions.RemoveEmptyEntries);
                    var supersedes = Optional(options, "--supersedes")?
                        .Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];
                    var aggregate = EvidenceValidator.ValidateAggregate(
                        Required(options, "--root"),
                        Required(options, "--evidence"),
                        evidencePaths,
                        Optional(options, "--review"),
                        Required(options, "--subject-manifest"),
                        supersedes);
                    Console.WriteLine($"HV000_AGGREGATE_VALID {aggregate.Outcome}");
                    return 0;
                }
            case "validate-publication-record":
                {
                    var cellEvidence = Required(options, "--cell-evidence")
                        .Split(';', StringSplitOptions.RemoveEmptyEntries);
                    var supersedes = Optional(options, "--supersedes")?
                        .Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];
                    var record = EvidenceValidator.ValidatePublicationRecord(
                        Required(options, "--root"),
                        Required(options, "--record"),
                        Required(options, "--aggregate-evidence"),
                        cellEvidence,
                        Optional(options, "--review"),
                        Required(options, "--subject-manifest"),
                        supersedes);
                    Console.WriteLine($"HV000_PUBLICATION_RECORD_VALID {record.EvidenceRecordRevision}");
                    return 0;
                }
            case "prepare-public":
                {
                    var root = Required(options, "--root");
                    var source = Required(options, "--source");
                    var output = Required(options, "--output");
                    var review = Required(options, "--review");
                    var subjectManifest = Required(options, "--subject-manifest");
                    var cells = Optional(options, "--cell-evidence")?
                        .Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];
                    var supersedes = Optional(options, "--supersedes")?
                        .Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];
                    var aggregateEvidence = Optional(options, "--aggregate-evidence");
                    var context = BundleValidator.Validate(root, requireReview: true, review);
                    var subject = CellExecutor.ValidateSubjectManifest(context, subjectManifest);
                    OutputPathGuard.Validate(
                        context,
                        cells.Concat(supersedes).Append(source).Append(review).Append(subjectManifest)
                            .Concat(aggregateEvidence is null ? [] : [aggregateEvidence])
                            .Concat(SubjectInputPaths(context.Root, subject)),
                        output);
                    CanonicalJson.InvalidateOutput(output);
                    EvidenceValidator.PreparePublicArtifact(
                        root,
                        Required(options, "--kind"),
                        source,
                        output,
                        review,
                        subjectManifest,
                        cells,
                        supersedes,
                        aggregateEvidence);
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
                await Task.Delay(1_000).ConfigureAwait(false);
                return 0;
            case "fake-child-hang":
                await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
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
        if (request.NetworkOperationLogPath is not null)
        {
            File.WriteAllText(
                request.NetworkOperationLogPath,
                "{\"formatVersion\":\"contractscribe-network-operation-recorder-v1\",\"state\":\"active\"}\n",
                new UTF8Encoding(false));
        }
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
            "self-test.observation.stable",
            null);

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
            case "temporary-over-limit":
                var temporaryPath = Path.Join(Path.GetTempPath(), "synthetic-temporary.bin");
                File.WriteAllBytes(temporaryPath, new byte[8 * 1024]);
                await ReachGateAsync(request).ConfigureAwait(false);
                File.Delete(temporaryPath);
                CanonicalJson.WriteCanonical(request.ResponsePath, response);
                return 0;
            case "temporary-cleanup-before-gate":
                var earlyCleanupPath = Path.Join(
                    Path.GetTempPath(),
                    "synthetic-early-cleanup.bin");
                File.WriteAllBytes(earlyCleanupPath, new byte[8 * 1024]);
                File.Delete(earlyCleanupPath);
                await ReachGateAsync(request).ConfigureAwait(false);
                CanonicalJson.WriteCanonical(request.ResponsePath, response);
                return 0;
            case "temporary-final-write":
                File.WriteAllBytes(
                    Path.Join(Path.GetTempPath(), "synthetic-temporary.bin"),
                    new byte[8 * 1024]);
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
            case "controlled-kill-with-child":
                {
                    using var child = System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo("dotnet")
                        {
                            UseShellExecute = false,
                            ArgumentList =
                            {
                                typeof(Program).Assembly.Location,
                                "fake-child-hang"
                            }
                        }) ?? throw new ProtocolException("HV920_FAKE_CHILD_START");
                    await ReachGateAsync(request).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                    return 0;
                }
            case "controlled-kill-race":
                await ReachGateAsync(
                    request,
                    waitForExternalKillRelease: true).ConfigureAwait(false);
                return 137;
            case "controlled-gate-timeout":
                await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                return 0;
            case "controlled-natural-exit-timeout":
                await ReachGateAsync(
                    request,
                    waitForExternalKillRelease: true).ConfigureAwait(false);
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

    private static async Task ReachGateAsync(
        SubjectRequest request,
        bool waitForExternalKillRelease = false)
    {
        if (request.ControlRoot is null || request.SynchronizationGates.Count != 1)
        {
            throw new ProtocolException("HV924_SYNTHETIC_CONTROL_INVALID");
        }
        Directory.CreateDirectory(request.ControlRoot);
        var gateName = request.SynchronizationGates[0];
        if (request.ControlAction == "measure-temporary-disk")
        {
            WriteTemporaryDiskBoundary(request, freeze: true);
        }
        File.WriteAllText(Path.Join(request.ControlRoot, $"{gateName}.reached"), string.Empty);
        if (request.ControlAction == "external-kill"
            && !waitForExternalKillRelease)
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
        if (request.ControlAction == "measure-temporary-disk")
        {
            WriteTemporaryDiskBoundary(request, freeze: false);
        }
    }

    private static void WriteTemporaryDiskBoundary(
        SubjectRequest request,
        bool freeze)
    {
        var contract = request.TemporaryDiskGate
            ?? throw new ProtocolException("HV924_SYNTHETIC_CONTROL_INVALID");
        if (request.AuditTemporaryRoot is null
            || !string.Equals(
                Path.GetFullPath(request.AuditTemporaryRoot),
                Path.GetFullPath(contract.TemporaryWorkRoot),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new ProtocolException("HV924_SYNTHETIC_CONTROL_INVALID");
        }
        var sentinel = freeze
            ? contract.FreezeSentinelName
            : contract.ReleaseSentinelName;
        foreach (var root in new[]
                 {
                     contract.TemporaryWorkRoot,
                     contract.OutputStagingRoot
                 })
        {
            Directory.CreateDirectory(root);
            using var stream = new FileStream(
                Path.Join(root, sentinel),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Flush(flushToDisk: true);
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

    private static IEnumerable<string> SubjectInputPaths(
        string root,
        ExecutionSubjectManifest subject) =>
        subject.SourceConfiguration.SourceAndBuildInputs
            .Concat(
            [
                subject.SourceConfiguration.FailureRegistry,
                subject.SourceConfiguration.CalibratedBounds,
                subject.SourceConfiguration.BuildRecipe,
                subject.SourceConfiguration.CommandContract,
                subject.SourceConfiguration.ContractBaseline,
                subject.SourceConfiguration.EnvironmentPolicy,
                subject.SourceConfiguration.Workflow
            ])
            .Select(identity => RepositoryPaths.ResolveConfined(root, identity.Path));
}
