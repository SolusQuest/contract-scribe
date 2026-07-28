using System.Reflection;
using System.Text;

namespace ContractScribe.HostValidation;

public static class HarnessSelfTest
{
    public static async Task RunAsync(string root, CancellationToken cancellationToken = default)
    {
        var context = BundleValidator.Validate(root);
        TestStrictJson();
        await TestProcessAndObserverAsync(context, cancellationToken).ConfigureAwait(false);
        await TestStreamsAsync(context, cancellationToken).ConfigureAwait(false);
        await TestFailureAndTimeoutAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static void TestStrictJson()
    {
        var temp = Path.Join(Path.GetTempPath(), $"contractscribe-hv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var duplicate = Path.Join(temp, "duplicate.json");
            File.WriteAllText(duplicate, "{\"a\":1,\"a\":2}", new UTF8Encoding(false));
            ExpectCode("HV109_DUPLICATE_PROPERTY", () =>
            {
                using var _ = CanonicalJson.ReadStrict(duplicate, 1024);
            });

            var noncanonical = Path.Join(temp, "noncanonical.json");
            File.WriteAllText(noncanonical, "{ \"b\": 1, \"a\": 2 }\n", new UTF8Encoding(false));
            ExpectCode("HV106_NONCANONICAL_JSON", () =>
            {
                using var _ = CanonicalJson.ReadStrict(noncanonical, 1024, requireCanonical: true);
            });
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static async Task TestProcessAndObserverAsync(
        BundleContext context,
        CancellationToken cancellationToken)
    {
        var temp = CreateSyntheticRepository();
        try
        {
            var first = await RunFakeAsync(context, temp, "success", "run-1", cancellationToken).ConfigureAwait(false);
            var second = await RunFakeAsync(context, temp, "success", "run-2", cancellationToken).ConfigureAwait(false);
            EnsureSuccessfulSubject(first, "HV901A_SELF_TEST_FIRST_SUCCESS");
            EnsureSuccessfulSubject(second, "HV901B_SELF_TEST_SECOND_SUCCESS");
            Ensure(first.Response?.ObservationCode == second.Response?.ObservationCode, "HV902_SELF_TEST_DETERMINISM");
            var firstPid = first.Execution.ObservedProcesses.Single(process => process.Role == "subject-runtime").ProcessId;
            var secondPid = second.Execution.ObservedProcesses.Single(process => process.Role == "subject-runtime").ProcessId;
            Ensure(firstPid != secondPid, "HV903_SELF_TEST_FRESH_PROCESS");

            var protectedMutation = await RunFakeAsync(context, temp, "modify-protected", "run-1", cancellationToken).ConfigureAwait(false);
            Ensure(RepositoryObserver.HasProtectedMutation(protectedMutation.Delta), "HV904_SELF_TEST_PROTECTED_WRITE");

            var designTime = await RunFakeAsync(context, temp, "allowed-obj", "run-1", cancellationToken).ConfigureAwait(false);
            Ensure(
                !RepositoryObserver.HasUnexpectedMutation(designTime.Delta)
                && designTime.Delta.AllowedDesignTimeCreated.Count != 0,
                "HV905_SELF_TEST_DESIGN_TIME");

            var worker = await RunFakeAsync(context, temp, "spawn-child", "run-1", cancellationToken).ConfigureAwait(false);
            Ensure(worker.Execution.ObservedProcesses.Count >= 2, "HV906_SELF_TEST_PROCESS_TREE");

            var restore = await RunFakeAsync(context, temp, "restore-marker", "run-1", cancellationToken).ConfigureAwait(false);
            Ensure(
                restore.Delta.AllowedDesignTimeCreated.Any(path => path.EndsWith("project.assets.json", StringComparison.Ordinal)),
                "HV907_SELF_TEST_RESTORE_MARKER");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static async Task TestStreamsAsync(
        BundleContext context,
        CancellationToken cancellationToken)
    {
        var temp = CreateSyntheticRepository();
        try
        {
            var unsafeOutput = await RunFakeAsync(context, temp, "unsafe-output", "run-1", cancellationToken).ConfigureAwait(false);
            ExpectCode("HV119_PUBLIC_CREDENTIAL_MARKER", () =>
                PublicSafetyScanner.EnsureSafeBytes(unsafeOutput.Execution.StandardOutput));

            var overflow = await RunFakeAsync(context, temp, "stdout-overflow", "run-1", cancellationToken).ConfigureAwait(false);
            Ensure(overflow.Execution.StandardOutputOverflow, "HV908_SELF_TEST_STDOUT_LIMIT");

            var invalidUtf8 = await RunFakeAsync(context, temp, "invalid-utf8", "run-1", cancellationToken).ConfigureAwait(false);
            Ensure(!invalidUtf8.Execution.StandardOutputValidUtf8, "HV909_SELF_TEST_STREAM_UTF8");

            var dual = await RunFakeAsync(context, temp, "dual-stream", "run-1", cancellationToken).ConfigureAwait(false);
            Ensure(dual.Execution.StandardOutput.Length != 0 && dual.Execution.StandardError.Length != 0, "HV910_SELF_TEST_DUAL_STREAM");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static async Task TestFailureAndTimeoutAsync(
        BundleContext context,
        CancellationToken cancellationToken)
    {
        var temp = CreateSyntheticRepository();
        try
        {
            var launchFailure = await SubjectProcessRunner.RunAsync(
                Path.Join(temp, "missing-subject"),
                [],
                temp,
                1024,
                1024,
                TimeSpan.FromSeconds(1),
                cancellationToken).ConfigureAwait(false);
            Ensure(launchFailure.ProcessStart == "launch-failure" && launchFailure.ExitCode is null, "HV911_SELF_TEST_LAUNCH");

            var noResponse = await RunFakeAsync(context, temp, "exception-before-response", "run-1", cancellationToken).ConfigureAwait(false);
            Ensure(noResponse.Response is null && noResponse.Execution.ExitCode != 0, "HV912_SELF_TEST_PRE_RESPONSE");
            Ensure(
                !Encoding.UTF8.GetString(noResponse.Execution.StandardError).Contains(" at ", StringComparison.Ordinal),
                "HV913_SELF_TEST_NO_STACK");

            var afterResponse = await RunFakeAsync(context, temp, "exception-after-response", "run-1", cancellationToken).ConfigureAwait(false);
            Ensure(afterResponse.Response is not null && afterResponse.Execution.ExitCode != 0, "HV914_SELF_TEST_POST_RESPONSE");

            var timedOut = await RunFakeAsync(context, temp, "hang", "run-1", cancellationToken, TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            Ensure(timedOut.Execution.TimedOut && timedOut.Execution.ProcessTermination == "external-kill", "HV915_SELF_TEST_TIMEOUT");

            var cancelled = await RunFakeAsync(context, temp, "controlled-cancel", "run-1", cancellationToken).ConfigureAwait(false);
            Ensure(
                cancelled.Execution.ControlCompleted
                && cancelled.Response?.ExecutionOutcome == "cancelled",
                "HV918_SELF_TEST_CONTROL_CANCEL");

            var temporary = await RunFakeAsync(
                context,
                temp,
                "temporary-over-limit",
                "run-1",
                cancellationToken).ConfigureAwait(false);
            Ensure(
                temporary.Execution.TemporaryDiskHighWater is
                {
                    Quantity: "peak-concurrent-logical-file-bytes",
                    TemporaryWorkBytes: 8 * 1024,
                    TotalBytes: 8 * 1024,
                    ObserverComplete: true,
                    RetentionBreach: false
                }
                && temporary.AuditTemporaryFinalBytes == 0,
                "HV928_SELF_TEST_TEMPORARY_HIGH_WATER");
            var temporaryCleanup = await RunFakeAsync(
                context,
                temp,
                "temporary-cleanup-before-gate",
                "run-1",
                cancellationToken).ConfigureAwait(false);
            Ensure(
                temporaryCleanup.Execution.TemporaryDiskHighWater is
                {
                    ObserverComplete: true,
                    RetentionBreach: true
                }
                && temporaryCleanup.AuditTemporaryFinalBytes == 0,
                "HV929_SELF_TEST_TEMPORARY_RETENTION");

            var killed = await RunFakeAsync(context, temp, "controlled-kill", "run-1", cancellationToken).ConfigureAwait(false);
            if (!HasExactNativeKill(killed.Execution))
            {
                throw new ProtocolException(
                    NativeTerminationObserver.LastDiagnosticCode);
            }
            if (killed.Execution.KillRequestOutcome != "issued")
            {
                throw new ProtocolException(
                    NativeTerminationObserver.LastDiagnosticCode);
            }
            Ensure(killed.Execution.ControlCompleted, "HV919A_SELF_TEST_CONTROL_INCOMPLETE");
            Ensure(
                killed.Execution.ProcessTermination == "external-kill",
                "HV919B_SELF_TEST_CONTROL_TERMINATION");
            if (!killed.Execution.ObservationComplete)
            {
                throw new ProtocolException(
                    SubjectProcessRunner.LastObservationDiagnosticCode);
            }
            Ensure(killed.Response is null, "HV919E_SELF_TEST_CONTROL_RESPONSE");
            var killedTree = await RunFakeAsync(
                context,
                temp,
                "controlled-kill-with-child",
                "run-1",
                cancellationToken).ConfigureAwait(false);
            Ensure(
                killedTree.Execution.ObservedProcesses.Count >= 2,
                "HV943A_SELF_TEST_DESCENDANT_NOT_OBSERVED");
            if (!HasExactNativeKill(killedTree.Execution))
            {
                throw new ProtocolException(
                    NativeTerminationObserver.LastDiagnosticCode);
            }
            if (killedTree.Execution.KillRequestOutcome != "issued")
            {
                throw new ProtocolException(
                    NativeTerminationObserver.LastDiagnosticCode);
            }
            Ensure(
                killedTree.Execution.ControlCompleted,
                "HV943D_SELF_TEST_TREE_CONTROL_INCOMPLETE");
            Ensure(
                killedTree.Execution.ProcessTermination == "external-kill",
                "HV943E_SELF_TEST_TREE_TERMINATION");
            if (!killedTree.Execution.ObservationComplete)
            {
                throw new ProtocolException(
                    SubjectProcessRunner.LastObservationDiagnosticCode);
            }

            var killRace = await RunFakeAsync(context, temp, "controlled-kill-race", "run-1", cancellationToken).ConfigureAwait(false);
            Ensure(
                !killRace.Execution.ControlCompleted
                && killRace.Execution.KillRequestOutcome == "already-exited"
                && killRace.Execution.ProcessTermination != "external-kill"
                && killRace.Execution.ControlOutcome != "issued-and-observed"
                && !HasExactNativeKill(killRace.Execution)
                && killRace.Execution.NativeTerminationKind
                    == (OperatingSystem.IsWindows()
                        ? "windows-terminate-process"
                        : "unsupported")
                && killRace.Execution.NativeTerminationCode == 137,
                "HV926_SELF_TEST_KILL_RACE");
            Ensure(
                NativeTerminationObserver.IsExited(137 << 8)
                && NativeTerminationObserver.ExitStatus(137 << 8) == 137
                && !NativeTerminationObserver.IsSignaled(137 << 8)
                && NativeTerminationObserver.IsSignaled(NativeTerminationObserver.UnixSigKill)
                && NativeTerminationObserver.TermSignal(NativeTerminationObserver.UnixSigKill)
                    == NativeTerminationObserver.UnixSigKill
                && NativeTerminationObserver.IsExitedProcStat(
                    "42 (subject) Z 1 2 3")
                && NativeTerminationObserver.IsExitedProcStat(
                    "42 (subject with ) marker) X 1 2 3")
                && !NativeTerminationObserver.IsExitedProcStat(
                    "42 (subject) R 1 2 3"),
                "HV927_SELF_TEST_NATIVE_WAIT_STATUS");
            var rootIdentity = new ProcessInstanceIdentity(100, 1000);
            var originalDescendant = new ProcessInstanceIdentity(200, 2000);
            Ensure(
                ProcessTreeObserver.IsCurrentDescendant(
                    rootIdentity,
                    originalDescendant,
                    [
                        new(rootIdentity, 1),
                        new(originalDescendant, 100)
                    ])
                && !ProcessTreeObserver.IsCurrentDescendant(
                    rootIdentity,
                    originalDescendant,
                    [
                        new(rootIdentity, 1),
                        new(new ProcessInstanceIdentity(200, 3000), 100)
                    ])
                && !ProcessTreeObserver.IsCurrentDescendant(
                    rootIdentity,
                    originalDescendant,
                    [new(rootIdentity, 1)]),
                "HV930_SELF_TEST_PROCESS_IDENTITY_REUSE");
            var issued = new NativeTerminationEvidence(
                OperatingSystem.IsWindows()
                    ? "windows-terminate-process"
                    : "unix-signal",
                null,
                OperatingSystem.IsWindows()
                    ? NativeTerminationObserver.WindowsTerminationSentinel
                    : NativeTerminationObserver.UnixSigKill,
                "issued",
                true);
            Ensure(
                NativeTerminationObserver.IsTerminationFullyObserved(
                    issued,
                    streamsComplete: true)
                && !NativeTerminationObserver.IsTerminationFullyObserved(
                    issued with
                    {
                        KillRequestOutcome = "indeterminate",
                        CausalMatch = false
                    },
                    streamsComplete: true)
                && !NativeTerminationObserver.IsTerminationFullyObserved(
                    issued,
                    streamsComplete: false)
                && NativeTerminationObserver.CombineTerminationFailuresForSelfTest(
                    planComplete: true,
                    null,
                    null) is null
                && NativeTerminationObserver.CombineTerminationFailuresForSelfTest(
                    planComplete: true,
                    "indeterminate") == "indeterminate"
                && NativeTerminationObserver.CombineTerminationFailuresForSelfTest(
                    planComplete: false) == "indeterminate",
                "HV931_SELF_TEST_BOUNDED_TREE_TERMINATION");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static bool HasExactNativeKill(ProcessExecutionResult execution)
    {
        if (OperatingSystem.IsWindows())
        {
            return execution.NativeTerminationKind == "windows-terminate-process"
                && execution.NativeTerminationCode
                    == NativeTerminationObserver.WindowsTerminationSentinel;
        }
        if (OperatingSystem.IsLinux())
        {
            return execution.NativeTerminationKind == "unix-signal"
                && execution.NativeTerminationCode == NativeTerminationObserver.UnixSigKill;
        }
        return false;
    }

    private static void EnsureSuccessfulSubject(FakeRun run, string fallbackCode)
    {
        if (run.Execution.ExitCode == 0)
        {
            return;
        }
        if (run.Execution.ProcessStart != "started")
        {
            throw new ProtocolException(
                $"{fallbackCode}_PROCESS_START");
        }
        if (run.Execution.TimedOut)
        {
            throw new ProtocolException(
                $"{fallbackCode}_TIMEOUT");
        }
        var subjectCode = Encoding.UTF8.GetString(
                run.Execution.StandardError)
            .Trim();
        if (string.IsNullOrEmpty(subjectCode))
        {
            throw new ProtocolException(
                $"{fallbackCode}_NO_SUBJECT_DIAGNOSTIC");
        }
        throw new ProtocolException(
            subjectCode.StartsWith("HV", StringComparison.Ordinal)
            && subjectCode.All(character =>
                char.IsAsciiLetterOrDigit(character) || character == '_')
                ? subjectCode
                : fallbackCode);
    }

    private static async Task<FakeRun> RunFakeAsync(
        BundleContext context,
        string repository,
        string behavior,
        string runId,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var requestPath = Path.Join(repository, $".request-{Guid.NewGuid():N}.json");
        var responsePath = Path.Join(Path.GetTempPath(), $"contractscribe-hv-response-{Guid.NewGuid():N}.json");
        var controlRoot = Path.Join(Path.GetTempPath(), $"contractscribe-hv-control-{Guid.NewGuid():N}");
        var auditTemporaryRoot = Path.Join(Path.GetTempPath(), $"contractscribe-hv-audit-temp-{Guid.NewGuid():N}");
        var stagingRoot = Path.Join(repository, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(auditTemporaryRoot);
        Directory.CreateDirectory(stagingRoot);
        TemporaryDiskHighWaterObserver? temporaryDiskObserver =
            behavior is "temporary-over-limit" or "temporary-cleanup-before-gate"
                ? new(auditTemporaryRoot, stagingRoot)
                : null;
        SubjectControl? control = behavior switch
        {
            "controlled-cancel" => new(controlRoot, "before-commit", "cancel", TimeSpan.FromSeconds(5)),
            "controlled-kill" or "controlled-kill-with-child" => new(
                controlRoot,
                "publication-before-commit",
                "external-kill",
                TimeSpan.FromSeconds(5)),
            "controlled-kill-race" => new(
                controlRoot,
                "publication-before-commit",
                "external-kill",
                TimeSpan.FromSeconds(5),
                WaitForExitBeforeAction: true),
            "temporary-over-limit" or "temporary-cleanup-before-gate" => new(
                controlRoot,
                "temporary-disk-high-water",
                "measure-temporary-disk",
                TimeSpan.FromSeconds(5),
                MeasureTemporaryDisk: temporaryDiskObserver!.CaptureAndRelease),
            _ => null
        };
        var request = new SubjectRequest(
            "contractscribe-m1-host-validation-subject-request-v1",
            "self-test",
            "self-test.fake-subject",
            runId,
            repository,
            responsePath,
            control?.ControlRoot,
            control is null ? [] : [control.GateName],
            control?.Action ?? "continue",
            null,
            null,
            auditTemporaryRoot,
            temporaryDiskObserver?.GateContract);
        CanonicalJson.WriteCanonical(requestPath, request);
        SchemaValidation.ValidateDefinition(
            requestPath,
            RepositoryPaths.ResolveConfined(
                context.Root,
                "schemas/validation/m1-host-validation-subject-v1.schema.json"),
            "subjectRequest",
            requireCanonical: true);
        var allowedDesignTimeRoots = behavior is "allowed-obj" or "restore-marker"
            ? new[] { "obj" }
            : [];
        var before = RepositoryObserver.Capture(repository, allowedDesignTimeRoots);
        try
        {
            var assembly = Assembly.GetExecutingAssembly().Location;
            var execution = await SubjectProcessRunner.RunAsync(
                "dotnet",
                [assembly, "fake-subject", "--request", requestPath, "--behavior", behavior],
                repository,
                context.Protocol.ExecutionContract.StandardOutputByteLimit,
                context.Protocol.ExecutionContract.StandardErrorByteLimit,
                timeout ?? TimeSpan.FromSeconds(5),
                cancellationToken,
                control,
                auditTemporaryRoot: auditTemporaryRoot).ConfigureAwait(false);
            if (temporaryDiskObserver is not null
                && execution.TemporaryDiskHighWater is not null)
            {
                execution = execution with
                {
                    TemporaryDiskHighWater = temporaryDiskObserver.Complete(
                        execution.TemporaryDiskHighWater)
                };
            }
            var after = RepositoryObserver.Capture(repository, allowedDesignTimeRoots);
            var delta = RepositoryObserver.Compare(before, after);
            var auditTemporaryFinalBytes = Directory
                .EnumerateFiles(auditTemporaryRoot, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            SubjectResponse? response = null;
            if (File.Exists(responsePath))
            {
                SchemaValidation.ValidateDefinition(
                    responsePath,
                    RepositoryPaths.ResolveConfined(
                        context.Root,
                        "schemas/validation/m1-host-validation-subject-v1.schema.json"),
                    "subjectResponse",
                    requireCanonical: true);
                response = CanonicalJson.DeserializeStrict<SubjectResponse>(
                    responsePath,
                    context.Protocol.ExecutionContract.ResponseByteLimit,
                    requireCanonical: true);
            }
            return new FakeRun(execution, response, delta, auditTemporaryFinalBytes);
        }
        finally
        {
            temporaryDiskObserver?.Dispose();
            File.Delete(requestPath);
            File.Delete(responsePath);
            if (Directory.Exists(controlRoot))
            {
                Directory.Delete(controlRoot, recursive: true);
            }
            Directory.Delete(auditTemporaryRoot, recursive: true);
            Directory.Delete(stagingRoot, recursive: true);
        }
    }

    private static string CreateSyntheticRepository()
    {
        var root = Path.Join(Path.GetTempPath(), $"contractscribe-hv-repository-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Join(root, "Sample.cs"), "internal sealed class Sample { }\n", new UTF8Encoding(false));
        File.WriteAllText(
            Path.Join(root, "Sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n",
            new UTF8Encoding(false));
        return root;
    }

    private static void ExpectCode(string expectedCode, Action action)
    {
        var exception = AssertThrows(action);
        Ensure(exception.Code == expectedCode, "HV916_SELF_TEST_EXPECTED_CODE");
    }

    private static ProtocolException AssertThrows(Action action)
    {
        try
        {
            action();
        }
        catch (ProtocolException exception)
        {
            return exception;
        }
        throw new ProtocolException("HV917_SELF_TEST_EXPECTED_FAILURE");
    }

    private static void Ensure(bool condition, string code)
    {
        if (!condition)
        {
            throw new ProtocolException(code);
        }
    }

    private sealed record FakeRun(
        ProcessExecutionResult Execution,
        SubjectResponse? Response,
        RepositoryDelta Delta,
        long AuditTemporaryFinalBytes);
}
