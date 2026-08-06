using System.Text;
using System.Text.Json;

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
        ValidateOptions(command, options);
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
            case "lock-pending-review":
                {
                    var review = BundleValidator.CreatePendingReview(
                        Required(options, "--root"));
                    Console.WriteLine($"HV000_OK {review.ReviewId}");
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
            case "materialize-common":
                {
                    var root = Required(options, "--root");
                    var review = Required(options, "--review");
                    var output = Required(options, "--output");
                    var context = BundleValidator.Validate(root, requireReview: true, review);
                    OutputPathGuard.Validate(
                        context,
                        [review],
                        output);
                    CanonicalJson.InvalidateOutput(output);
                    var manifest = SubjectManifestMaterializer.MaterializeCommon(
                        root,
                        review,
                        output);
                    Console.WriteLine($"HV000_COMMON_MATERIALIZED {manifest.SourceConfiguration.SourceConfigurationId}");
                    return 0;
                }
            case "materialize-cell":
                {
                    var root = Required(options, "--root");
                    var review = Required(options, "--review");
                    var commonManifest = Required(options, "--common-manifest");
                    var output = Required(options, "--output");
                    var context = BundleValidator.Validate(root, requireReview: true, review);
                    OutputPathGuard.Validate(
                        context,
                        [review, commonManifest],
                        output);
                    CanonicalJson.InvalidateOutput(output);
                    var manifest = SubjectManifestMaterializer.MaterializeCell(
                        root,
                        review,
                        commonManifest,
                        Required(options, "--cell"),
                        output);
                    Console.WriteLine($"HV000_CELL_MATERIALIZED {manifest.CellId}");
                    return 0;
                }
            case "execute-cell":
                {
                    var root = Required(options, "--root");
                    var commonManifest = Required(options, "--common-manifest");
                    var cellManifest = Required(options, "--cell-manifest");
                    var review = Required(options, "--review");
                    var incompleteOutput = Required(options, "--incomplete-output");
                    var output = Required(options, "--output");
                    var context = BundleValidator.Validate(root, requireReview: true, review);
                    var reviewRecord = BundleValidator.ValidateReview(context.Root, review, context.Lock.BundleId);
                    var manifests = CellExecutor.ValidateSubjectManifests(
                        context,
                        commonManifest,
                        cellManifest);
                    OutputPathGuard.Validate(
                        context,
                        SubjectInputPaths(context.Root, manifests.Common)
                            .Append(commonManifest)
                            .Append(cellManifest)
                            .Append(review),
                        output,
                        incompleteOutput);
                    CanonicalJson.InvalidateOutput(output);
                    CanonicalJson.InvalidateOutput(incompleteOutput);
                    try
                    {
                        ProvisionFixtures(context, manifests.Cell);
                        CellExecutor.ValidateProvisionedFixtures(context.Root, manifests.Cell.Subject);
                        var evidence = await CellExecutor.RunAsync(
                            root,
                            commonManifest,
                            cellManifest,
                            review,
                            output).ConfigureAwait(false);
                        _ = EvidenceValidator.ValidateCell(
                            root,
                            output,
                            review,
                            commonManifest,
                            cellManifest);
                        Console.WriteLine($"HV000_CELL_EXECUTED {evidence.Cell.CellId} {evidence.Outcome}");
                        return 0;
                    }
                    catch (ProtocolException exception)
                    {
                        CanonicalJson.InvalidateOutput(output);
                        _ = IncompleteEvidenceWriter.WriteTrusted(
                            context,
                            reviewRecord,
                            manifests,
                            review,
                            commonManifest,
                            cellManifest,
                            incompleteOutput,
                            exception.Code);
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        CanonicalJson.InvalidateOutput(output);
                        _ = IncompleteEvidenceWriter.WriteTrusted(
                            context,
                            reviewRecord,
                            manifests,
                            review,
                            commonManifest,
                            cellManifest,
                            incompleteOutput,
                            "HV998_CANCELLED");
                        throw;
                    }
                    catch
                    {
                        CanonicalJson.InvalidateOutput(output);
                        _ = IncompleteEvidenceWriter.WriteTrusted(
                            context,
                            reviewRecord,
                            manifests,
                            review,
                            commonManifest,
                            cellManifest,
                            incompleteOutput,
                            "HV999_INTERNAL_ERROR");
                        throw;
                    }
                }
            case "validate-cell":
                {
                    var evidence = EvidenceValidator.ValidateCell(
                        Required(options, "--root"),
                        Required(options, "--evidence"),
                        Optional(options, "--review"),
                        Required(options, "--common-manifest"),
                        Required(options, "--cell-manifest"));
                    Console.WriteLine($"HV000_CELL_VALID {evidence.Cell.CellId}");
                    return 0;
                }
            case "validate-incomplete":
                {
                    _ = EvidenceValidator.ValidateIncomplete(
                        Required(options, "--root"),
                        Required(options, "--evidence"),
                        Optional(options, "--review"),
                        Required(options, "--common-manifest"),
                        Required(options, "--cell-manifest"));
                    Console.WriteLine("HV000_INCOMPLETE_VALID");
                    return 0;
                }
            case "aggregate":
                {
                    var root = Required(options, "--root");
                    var output = Required(options, "--output");
                    var review = Required(options, "--review");
                    var context = BundleValidator.Validate(root, requireReview: true, review);
                    OutputPathGuard.Validate(
                        context,
                        [review],
                        output);
                    CanonicalJson.InvalidateOutput(output);
                    var aggregate = EvidenceValidator.Aggregate(
                        root,
                        Required(options, "--artifact-root"),
                        output,
                        review,
                        Required(options, "--publication-base-revision"));
                    Console.WriteLine($"HV000_AGGREGATE {aggregate.Outcome}");
                    return 0;
                }
            case "validate-aggregate":
                {
                    var aggregate = EvidenceValidator.ValidateAggregate(
                        Required(options, "--root"),
                        Required(options, "--evidence"),
                        Required(options, "--artifact-root"),
                        Optional(options, "--review"));
                    Console.WriteLine($"HV000_AGGREGATE_VALID {aggregate.Outcome}");
                    return 0;
                }
            case "require-passing-aggregate":
                {
                    var aggregate = EvidenceValidator.RequirePassingAggregate(
                        Required(options, "--root"),
                        Required(options, "--evidence"),
                        Required(options, "--artifact-root"),
                        Optional(options, "--review"));
                    Console.WriteLine($"HV000_AGGREGATE_PASSING {aggregate.Outcome}");
                    return 0;
                }
            case "validate-publication-record":
                {
                    var record = EvidenceValidator.ValidatePublicationRecord(
                        Required(options, "--root"),
                        Required(options, "--record"),
                        Required(options, "--aggregate-evidence"),
                        Required(options, "--artifact-root"),
                        Optional(options, "--review"));
                    Console.WriteLine($"HV000_PUBLICATION_RECORD_VALID {record.EvidenceRecordRevision}");
                    return 0;
                }
            case "prepare-public":
                {
                    var root = Required(options, "--root");
                    var source = Required(options, "--source");
                    var output = Required(options, "--output");
                    var review = Required(options, "--review");
                    var aggregateEvidence = Optional(options, "--aggregate-evidence");
                    var context = BundleValidator.Validate(root, requireReview: true, review);
                    OutputPathGuard.Validate(
                        context,
                        new[] { source, review }
                            .Concat(aggregateEvidence is null ? [] : [aggregateEvidence]),
                        output);
                    CanonicalJson.InvalidateOutput(output);
                    EvidenceValidator.PreparePublicArtifact(
                        root,
                        Required(options, "--kind"),
                        source,
                        output,
                        review,
                        Required(options, "--artifact-root"),
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
            case "publication-failure-invalidation":
                ValidatePublicationSelfTestRequest(
                    request,
                    "failure.publication-invalidation");
                WriteTransitionLog(request.TransitionLogPath,
                [
                    "invalidation-attempt-failed",
                    "terminal-commit-publication-failure",
                    "late-terminal-attempt-rejected"
                ]);
                CanonicalJson.WriteCanonical(
                    request.ResponsePath,
                    PublicationFailureResponse(request, finalization: false));
                return 0;
            case "publication-failure-finalization":
                ValidatePublicationSelfTestRequest(
                    request,
                    "failure.publication-finalization");
                var stagingPath = Path.Join(
                    request.RepositoryRoot,
                    "TestResults",
                    ".audit-result.json.contractscribe-stage");
                Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
                File.Copy(
                    Path.Join(request.RepositoryRoot, ".contractscribe-publication-source.json"),
                    stagingPath,
                    overwrite: true);
                await ReachGateAsync(request).ConfigureAwait(false);
                File.Delete(stagingPath);
                WriteTransitionLog(request.TransitionLogPath,
                [
                    "invalidation-completed",
                    "failure-prone-stage-entered",
                    "staging-created-in-destination",
                    "atomic-replace-attempt-failed",
                    "staging-cleanup-completed",
                    "terminal-commit-publication-failure",
                    "late-terminal-attempt-rejected"
                ]);
                CanonicalJson.WriteCanonical(
                    request.ResponsePath,
                    PublicationFailureResponse(request, finalization: true));
                return 0;
            case "toolchain-cancelled-preselection":
                CanonicalJson.WriteCanonical(
                    request.ResponsePath,
                    ToolchainStateResponse(request, "cancelled", selected: false));
                return 0;
            case "toolchain-timeout-preselection":
                CanonicalJson.WriteCanonical(
                    request.ResponsePath,
                    ToolchainStateResponse(request, "timeout", selected: false));
                return 0;
            case "toolchain-cancelled-postselection":
                CanonicalJson.WriteCanonical(
                    request.ResponsePath,
                    ToolchainStateResponse(request, "cancelled", selected: true));
                return 0;
            case "toolchain-timeout-postselection":
                CanonicalJson.WriteCanonical(
                    request.ResponsePath,
                    ToolchainStateResponse(request, "timeout", selected: true));
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
                    ControlledCancellationResponse(response));
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

    private static SubjectResponse PublicationFailureResponse(
        SubjectRequest request,
        bool finalization)
    {
        const string Sha = "1111111111111111111111111111111111111111111111111111111111111111";
        const string HostRevision = "2222222222222222222222222222222222222222";
        var root = FindRepositoryRoot();
        var registrySha = CanonicalJson.Sha256File(Path.Join(
            root,
            "tests",
            "fixtures",
            "m1-host-validation",
            "v1",
            "self-test-host-failure-registry.json"));
        var failureCode = finalization
            ? "host.test-only.publication-finalization"
            : "host.test-only.publication-invalidation";
        var facts = new HostObservationFacts(
            $"source.{Sha}",
            HostRevision,
            Sha,
            registrySha,
            Sha,
            finalization ? "10.0.102" : null,
            finalization ? "10.0.0" : null,
            finalization ? "18.0.0" : null,
            [new NormalizedDiagnosticFact(failureCode, "publication")],
            new OutputCommitFact("not-committed", null),
            [],
            null,
            finalization ? "selected" : "not-selected");
        return new SubjectResponse(
            "contractscribe-m1-host-validation-subject-response-v1",
            request.VectorId,
            request.RunId,
            "started",
            "normal",
            null,
            "publication-failure",
            registrySha,
            failureCode,
            "publication",
            "committed",
            "invalidated",
            "internally-enforceable",
            finalization
                ? "publication.finalization-failure-committed"
                : "publication.invalidation-failure-committed",
            null,
            facts);
    }

    private static SubjectResponse ToolchainStateResponse(
        SubjectRequest request,
        string executionOutcome,
        bool selected)
    {
        const string Sha = "1111111111111111111111111111111111111111111111111111111111111111";
        const string HostRevision = "2222222222222222222222222222222222222222";
        var root = FindRepositoryRoot();
        var registrySha = CanonicalJson.Sha256File(Path.Join(
            root,
            "tests",
            "fixtures",
            "m1-host-validation",
            "v1",
            "self-test-host-failure-registry.json"));
        var failureCode = (executionOutcome, selected) switch
        {
            ("cancelled", false) => "host.test-only.cancelled-sdk-preselection",
            ("timeout", false) => "host.test-only.timeout-sdk-preselection",
            ("cancelled", true) => "host.test-only.cancelled-sdk-postselection",
            ("timeout", true) => "host.test-only.timeout-sdk-postselection",
            _ => throw new ProtocolException("HV923_SYNTHETIC_BEHAVIOR_UNKNOWN")
        };
        var facts = new HostObservationFacts(
            $"source.{Sha}",
            HostRevision,
            Sha,
            registrySha,
            Sha,
            selected ? "10.0.102" : null,
            selected ? "10.0.0" : null,
            selected ? "18.0.0" : null,
            [new NormalizedDiagnosticFact(failureCode, "sdk-discovery")],
            new OutputCommitFact("not-committed", null),
            [],
            null,
            selected ? "selected" : "not-selected");
        return new SubjectResponse(
            "contractscribe-m1-host-validation-subject-response-v1",
            request.VectorId,
            request.RunId,
            "started",
            "normal",
            null,
            executionOutcome,
            registrySha,
            failureCode,
            "sdk-discovery",
            "committed",
            "absent",
            "internally-enforceable",
            $"self-test.{executionOutcome}-sdk-{(selected ? "postselection" : "preselection")}",
            null,
            facts);
    }

    private static SubjectResponse ControlledCancellationResponse(
        SubjectResponse response)
    {
        const string Sha = "1111111111111111111111111111111111111111111111111111111111111111";
        const string HostRevision = "2222222222222222222222222222222222222222";
        var facts = new HostObservationFacts(
            $"source.{Sha}",
            HostRevision,
            Sha,
            Sha,
            Sha,
            "10.0.102",
            "10.0.0",
            "18.0.0",
            [new NormalizedDiagnosticFact("host.cancelled", "audit")],
            new OutputCommitFact("not-committed", null),
            [],
            null,
            "selected");
        return response with
        {
            AuditOutcome = null,
            ExecutionOutcome = "cancelled",
            FailureRegistryIdentity = Sha,
            FailureCode = "host.cancelled",
            FailureStage = "audit",
            TerminalState = "committed",
            ArtifactState = "absent",
            ObservationCode = "cancellation.cancelled-before-commit",
            HostFacts = facts
        };
    }

    private static void ValidatePublicationSelfTestRequest(
        SubjectRequest request,
        string expectedVectorId)
    {
        if (request.VectorId != expectedVectorId
            || request.PublicationFault is null
            || request.PostTerminalAttempt is not
            {
                ExecutionOutcome: "succeeded",
                Timing: "after-publication-failure-commit",
                Occurrence: 1
            })
        {
            throw new ProtocolException("HV972_SELF_TEST_PUBLICATION_REQUEST");
        }
    }

    private static void WriteTransitionLog(
        string? path,
        IReadOnlyList<string> events)
    {
        if (path is null)
        {
            throw new ProtocolException("HV972_SELF_TEST_PUBLICATION_REQUEST");
        }
        var lines = events.Select((item, index) =>
            JsonSerializer.Serialize(new { sequence = index + 1, @event = item }));
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "ContractScribe.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new ProtocolException("HV972_SELF_TEST_PUBLICATION_REQUEST");
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

    private static void ValidateOptions(
        string command,
        IReadOnlyDictionary<string, string?> options)
    {
        string[]? allowed = command switch
        {
            "lock-bundle" or "lock-protected-inputs" or "lock-pending-review" or "self-test" =>
                ["--root"],
            "validate-bundle" =>
                ["--root", "--require-review", "--review"],
            "dry-run" =>
                ["--root", "--cell"],
            "materialize-common" =>
                ["--root", "--review", "--output"],
            "materialize-cell" =>
                ["--root", "--review", "--common-manifest", "--cell", "--output"],
            "execute-cell" =>
                [
                    "--root",
                    "--common-manifest",
                    "--cell-manifest",
                    "--review",
                    "--incomplete-output",
                    "--output"
                ],
            "validate-cell" or "validate-incomplete" =>
                ["--root", "--evidence", "--review", "--common-manifest", "--cell-manifest"],
            "aggregate" =>
                [
                    "--root",
                    "--artifact-root",
                    "--output",
                    "--review",
                    "--publication-base-revision"
                ],
            "validate-aggregate" or "require-passing-aggregate" =>
                [
                    "--root",
                    "--evidence",
                    "--artifact-root",
                    "--review"
                ],
            "validate-publication-record" =>
                [
                    "--root",
                    "--record",
                    "--aggregate-evidence",
                    "--artifact-root",
                    "--review"
                ],
            "prepare-public" =>
                [
                    "--root",
                    "--source",
                    "--output",
                    "--review",
                    "--artifact-root",
                    "--aggregate-evidence",
                    "--kind"
                ],
            "fake-subject" =>
                ["--request", "--behavior"],
            "fake-child" or "fake-child-hang" =>
                [],
            _ => null
        };
        if (allowed is not null
            && options.Keys.Any(option =>
                !allowed.Contains(option, StringComparer.Ordinal)))
        {
            throw new ProtocolException("HV004_OPTION_INVALID");
        }
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
        CommonSourceManifest subject) =>
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

    private static void ProvisionFixtures(BundleContext context, CellSubjectManifest cellManifest)
    {
        var cell = cellManifest.Subject;
        foreach (var fixture in cell.Fixtures)
        {
            var vector = context.Vectors.Vectors.Single(candidate =>
                candidate.VectorId == fixture.VectorId);
            FixtureRecipeRegistry.Provision(
                RepositoryPaths.ResolveConfined(
                    context.Root,
                    fixture.RepositoryRoot,
                    mustExist: false),
                cellManifest.CellId,
                vector);
            Console.WriteLine(
                $"HV000_FIXTURE {cellManifest.CellId} {vector.VectorId} {fixture.RepositoryIdentitySha256}");
        }
    }
}
