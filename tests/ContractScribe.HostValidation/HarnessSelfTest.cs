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
            Ensure(first.Execution.ExitCode == 0 && second.Execution.ExitCode == 0, "HV901_SELF_TEST_SUCCESS");
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

            var killed = await RunFakeAsync(context, temp, "controlled-kill", "run-1", cancellationToken).ConfigureAwait(false);
            Ensure(
                killed.Execution.ControlCompleted
                && killed.Execution.ProcessTermination == "external-kill"
                && killed.Response is null,
                "HV919_SELF_TEST_CONTROL_KILL");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
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
        SubjectControl? control = behavior switch
        {
            "controlled-cancel" => new(controlRoot, "before-commit", "cancel", TimeSpan.FromSeconds(5)),
            "controlled-kill" => new(controlRoot, "publication-before-commit", "external-kill", TimeSpan.FromSeconds(5)),
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
            control?.Action ?? "continue");
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
                control).ConfigureAwait(false);
            var after = RepositoryObserver.Capture(repository, allowedDesignTimeRoots);
            var delta = RepositoryObserver.Compare(before, after);
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
            return new FakeRun(execution, response, delta);
        }
        finally
        {
            File.Delete(requestPath);
            File.Delete(responsePath);
            if (Directory.Exists(controlRoot))
            {
                Directory.Delete(controlRoot, recursive: true);
            }
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
        RepositoryDelta Delta);
}
