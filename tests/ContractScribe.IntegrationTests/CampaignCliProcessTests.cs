using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractScribe.Cli;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

[Collection("Integration process lane 1")]
public sealed class CampaignCliProcessTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string Configuration = AppContext.BaseDirectory.Contains(
        $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
        StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
    private static readonly string CliPath = Path.Join(
        RepositoryRoot, "src", "ContractScribe.Cli", "bin", Configuration, "net10.0", "ContractScribe.Cli.dll");
    private static readonly string StartupHookPath = Path.Join(
        RepositoryRoot, "tests", "ContractScribe.CampaignStartupHook", "bin", Configuration,
        "net10.0", "ContractScribe.CampaignStartupHook.dll");
    private const string OptionalPolicy =
        "{\"defaultDecision\":\"optional\",\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\"}\n";
    private const string RequiredPolicy =
        "{\"defaultDecision\":\"required\",\"schemaVersion\":1,\"targetProfile\":\"profile.external-api\"}\n";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SharedProductionSession_ComposesCampaignAuthority(
        bool required,
        bool executable)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        if (executable)
        {
            await SetSingleWorkItemSourceAsync(fixture.Root);
        }
        var policyBytes = Encoding.UTF8.GetBytes(required ? RequiredPolicy : OptionalPolicy);
        var configurationBytes = await File.ReadAllBytesAsync(Path.Join(
            RepositoryRoot, "tests", "fixtures", "campaign", "cli", "configuration-valid.json"));
        var configuration = CampaignConfiguration.Parse(configurationBytes);
        Exception? captured = null;
        CampaignCheckpointState? state = null;
        CancellationToken? consumerToken = null;
        using var callerLifetime = new CancellationTokenSource();
        var host = new ProductionRepositorySessionHost(
            new ContractScribe.Core.Hosting.HostBuildProvenance(new string('a', 40)));

        var outcome = await host.RunAsync(
            new ProductionAuditRequest(
                fixture.Root,
                "App/App.csproj",
                policyBytes,
                PublicationTarget: null,
                PublishResult: false),
            new ProductionAuditHostControls(SessionConsumer: (bundle, token) =>
            {
                consumerToken = token;
                try
                {
                    var policy = configuration.CreateExecutionPolicy();
                    var execution = configuration.CreateExecutionCapability(policy);
                    var preflight = new CampaignPreflightResult(
                        CampaignOperation.Start,
                        fixture.Root,
                        Path.Join(fixture.Root, "App", "App.csproj"),
                        "App/App.csproj",
                        policyBytes,
                        "snapshot.integration",
                        Path.Join(Path.GetTempPath(), "unused-checkpoint.json"),
                        new CampaignConfigurationSnapshot(
                            Path.Join(RepositoryRoot, "tests", "fixtures", "campaign", "cli", "configuration-valid.json"),
                            configurationBytes.Length,
                            DateTime.UnixEpoch,
                            new string('0', 64),
                            configuration));
                    var planning = CampaignCommandRunner.CreatePlanningInput(
                        preflight, configuration, policy, bundle, token);
                    var plan = CampaignPlanner.Plan(planning);
                    state = CampaignStateFactory.CreateInitial(
                        configuration.ScribeRequest.StyleProfileTemplate.StyleProfileId,
                        configuration.ScribeRequest.StyleProfileTemplate.ExactProjection,
                        execution,
                        bundle.Session.InputIdentity,
                        planning,
                        plan);
                }
                catch (Exception exception)
                {
                    captured = exception;
                }
                return Task.CompletedTask;
            }),
            callerLifetime.Token);

        Assert.Null(captured);
        Assert.True(consumerToken.HasValue);
        Assert.Equal(callerLifetime.Token, consumerToken.Value);
        Assert.Contains(
            "audit-deadline-retired-before-session-consumer",
            outcome.TransitionEvents);
        Assert.Equal(ContractScribe.Core.Hosting.HostExecutionOutcome.Succeeded,
            outcome.Terminal.ExecutionOutcome);
        Assert.NotNull(state);
        if (executable)
        {
            Assert.Null(state.TerminalOutcome);
            Assert.NotEmpty(state.WorkItems);
        }
        else if (required)
        {
            Assert.Equal(CampaignTerminalKind.Complete, state.TerminalOutcome!.Kind);
            Assert.Equal(CampaignTerminalReason.AllWorkClosed, state.TerminalOutcome.Reason);
            Assert.NotEmpty(state.WorkItems);
        }
        else
        {
            Assert.Equal(CampaignTerminalReason.NoWork, state.TerminalOutcome!.Reason);
        }
    }

    [Fact]
    public async Task SharedProductionSession_WithoutTemporaryRoots_DoesNotInspectRepositoryLinks()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var fixture = await LoaderFixture.CreateAsync();
        var target = Path.Join(fixture.Root, "unrelated-source.txt");
        await File.WriteAllTextAsync(target, "source repository content");
        File.CreateSymbolicLink(
            Path.Join(fixture.Root, "unrelated-source-link.txt"),
            Path.GetFileName(target));
        var consumed = false;
        var host = new ProductionRepositorySessionHost(
            new ContractScribe.Core.Hosting.HostBuildProvenance(new string('a', 40)));

        var outcome = await host.RunAsync(
            new ProductionAuditRequest(
                fixture.Root,
                "App/App.csproj",
                Encoding.UTF8.GetBytes(OptionalPolicy),
                PublicationTarget: null,
                PublishResult: false),
            new ProductionAuditHostControls(SessionConsumer: (_, _) =>
            {
                consumed = true;
                return Task.CompletedTask;
            }),
            CancellationToken.None);

        Assert.True(consumed);
        Assert.Equal(
            ContractScribe.Core.Hosting.HostExecutionOutcome.Succeeded,
            outcome.Terminal.ExecutionOutcome);
    }

    [Fact]
    public async Task StartAndResume_AreFreshProcessExpectedPresenceOperations()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var fixture = await LoaderFixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), OptionalPolicy);
        var outside = CreatePrivateDirectory("contract-scribe-campaign-process");
        var configurationPath = Path.Join(outside, "campaign.json");
        var stateDirectory = Path.Join(outside, "state");
        Directory.CreateDirectory(stateDirectory);
        File.SetUnixFileMode(stateDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var statePath = Path.Join(stateDirectory, "checkpoint.json");
        await WriteConfigurationAsync(configurationPath);

        try
        {
            var before = await RunAsync(Args("resume", fixture.Root, statePath, configurationPath, "snapshot.one"));
            AssertCampaign(before, 4, "campaign.state-missing", null);

            var started = await RunAsync(Args("start", fixture.Root, statePath, configurationPath, "snapshot.one"),
                timeout: TimeSpan.FromMinutes(3));
            AssertCampaign(started, 0, "campaign.no-work", 0);
            var initial = await File.ReadAllBytesAsync(statePath);
            var parsed = CampaignStateJson.Parse(initial);
            Assert.True(parsed.IsValid);

            var duplicate = await RunAsync(Args("start", fixture.Root, statePath, configurationPath, "snapshot.one"));
            AssertCampaign(duplicate, 4, "campaign.state-present", null);
            Assert.Equal(initial, await File.ReadAllBytesAsync(statePath));

            var resumed = await RunAsync(Args("resume", fixture.Root, statePath, configurationPath, "snapshot.one"),
                timeout: TimeSpan.FromMinutes(3));
            AssertCampaign(resumed, 0, "campaign.no-work", 0);
            Assert.Equal(initial, await File.ReadAllBytesAsync(statePath));

            var incompatible = await RunAsync(Args("resume", fixture.Root, statePath, configurationPath, "snapshot.two"));
            AssertCampaign(incompatible, 4, "campaign.incompatible-snapshot", 0);
            Assert.Equal(initial, await File.ReadAllBytesAsync(statePath));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentStarts_OnlyTheCreatingProcessOwnsTheCampaign(bool executable)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var fixture = await LoaderFixture.CreateAsync();
        if (executable)
        {
            await SetSingleWorkItemSourceAsync(fixture.Root);
        }
        await File.WriteAllTextAsync(
            Path.Join(fixture.Root, "policy.json"),
            executable ? RequiredPolicy : OptionalPolicy);
        await using var server = new ProposalLoopbackServer();
        var outside = CreatePrivateDirectory("contract-scribe-campaign-concurrent-start");
        var configurationPath = Path.Join(outside, "campaign.json");
        var stateDirectory = Path.Join(outside, "state");
        Directory.CreateDirectory(stateDirectory);
        File.SetUnixFileMode(stateDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var statePath = Path.Join(stateDirectory, "checkpoint.json");
        var firstAcknowledgement = Path.Join(outside, "first.ack");
        var firstRelease = Path.Join(outside, "first.release");
        var secondAcknowledgement = Path.Join(outside, "second.ack");
        var secondRelease = Path.Join(outside, "second.release");
        await WriteConfigurationAsync(configurationPath, server.Endpoint);

        using var first = Start(
            Args("start", fixture.Root, statePath, configurationPath, "snapshot.concurrent"),
            HookEnvironment(
                CampaignProcessBoundaryHooks.InitialBeforeCreate,
                firstAcknowledgement,
                firstRelease));
        using var second = Start(
            Args("start", fixture.Root, statePath, configurationPath, "snapshot.concurrent"),
            HookEnvironment(
                CampaignProcessBoundaryHooks.InitialBeforeCreate,
                secondAcknowledgement,
                secondRelease));
        try
        {
            await Task.WhenAll(
                WaitForFileAsync(firstAcknowledgement, first,
                    CampaignProcessBoundaryHooks.InitialBeforeCreate, TimeSpan.FromMinutes(3)),
                WaitForFileAsync(secondAcknowledgement, second,
                    CampaignProcessBoundaryHooks.InitialBeforeCreate, TimeSpan.FromMinutes(3)));

            await File.WriteAllTextAsync(firstRelease, "release\n");
            await first.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            var winner = await first.CompleteAsync();
            AssertCampaign(
                winner,
                0,
                executable ? "campaign.complete" : "campaign.no-work",
                CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath)).Artifact!.CheckpointRevision);
            var winnerBytes = await File.ReadAllBytesAsync(statePath);

            await File.WriteAllTextAsync(secondRelease, "release\n");
            await second.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            var loser = await second.CompleteAsync();
            AssertCampaign(loser, 4, "campaign.state-present", null);
            Assert.Equal(winnerBytes, await File.ReadAllBytesAsync(statePath));
            Assert.Equal(executable ? 1 : 0, server.RequestCount);
        }
        finally
        {
            await StopAsync(first);
            await StopAsync(second);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task ConfigurationReplacementAfterPatchReservation_PreventsDispatchAndRecovers()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var fixture = await LoaderFixture.CreateAsync();
        await SetSingleWorkItemSourceAsync(fixture.Root);
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), RequiredPolicy);
        var originalSource = await File.ReadAllBytesAsync(Path.Join(fixture.Root, "App", "App.cs"));
        await using var server = new ProposalLoopbackServer();
        var outside = CreatePrivateDirectory("contract-scribe-campaign-configuration-guard");
        var configurationPath = Path.Join(outside, "campaign.json");
        var stateDirectory = Path.Join(outside, "state");
        Directory.CreateDirectory(stateDirectory);
        File.SetUnixFileMode(stateDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var statePath = Path.Join(stateDirectory, "checkpoint.json");
        var acknowledgement = Path.Join(outside, "hook.ack");
        var release = Path.Join(outside, "hook.release");
        await WriteConfigurationAsync(configurationPath, server.Endpoint);
        var configurationBytes = await File.ReadAllBytesAsync(configurationPath);

        using var running = Start(
            Args("start", fixture.Root, statePath, configurationPath, "snapshot.configuration-guard"),
            HookEnvironment(
                CampaignProcessBoundaryHooks.PatchBeforeDispatch,
                acknowledgement,
                release));
        try
        {
            await WaitForFileAsync(
                acknowledgement,
                running,
                CampaignProcessBoundaryHooks.PatchBeforeDispatch,
                TimeSpan.FromMinutes(3));
            Assert.Equal(1, server.RequestCount);
            await File.WriteAllBytesAsync(configurationPath, [.. configurationBytes, (byte)'\n']);
            await File.WriteAllTextAsync(release, "release\n");
            await running.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            var guarded = await running.CompleteAsync();
            var reserved = CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath));
            Assert.True(reserved.IsValid);
            Assert.IsType<CampaignPatchReservation>(reserved.Artifact!.State.ActiveReservation);
            Assert.Null(reserved.Artifact.State.CandidateObservation);
            AssertCampaign(guarded, 4, "campaign.state-conflict", reserved.Artifact.CheckpointRevision);
            Assert.Equal(originalSource, await File.ReadAllBytesAsync(Path.Join(fixture.Root, "App", "App.cs")));

            await File.WriteAllBytesAsync(configurationPath, configurationBytes);
            var recovered = await RunAsync(
                Args("resume", fixture.Root, statePath, configurationPath, "snapshot.configuration-guard"),
                timeout: TimeSpan.FromMinutes(3));
            var completed = CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath));
            Assert.True(completed.IsValid);
            Assert.Equal(CampaignTerminalKind.Exhausted, completed.Artifact!.State.TerminalOutcome!.Kind);
            Assert.Null(completed.Artifact.State.CandidateObservation);
            AssertCampaign(recovered, 3, "campaign.budget-exhausted", completed.Artifact.CheckpointRevision);
            Assert.Equal(1, server.RequestCount);
        }
        finally
        {
            await StopAsync(running);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task ReplacementReadbackCrash_LeavesOnlyTheCommittedCheckpointAsAuthority()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var fixture = await LoaderFixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), OptionalPolicy);
        var outside = CreatePrivateDirectory("contract-scribe-campaign-crash");
        var configurationPath = Path.Join(outside, "campaign.json");
        var stateDirectory = Path.Join(outside, "state");
        Directory.CreateDirectory(stateDirectory);
        File.SetUnixFileMode(stateDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var statePath = Path.Join(stateDirectory, "checkpoint.json");
        var acknowledgement = Path.Join(outside, "hook.ack");
        await WriteConfigurationAsync(configurationPath);

        using var running = Start(
            Args("start", fixture.Root, statePath, configurationPath, "snapshot.crash"),
            new Dictionary<string, string?>
            {
                ["DOTNET_STARTUP_HOOKS"] = StartupHookPath,
                ["CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_NAME"] =
                    CampaignProcessBoundaryHooks.InitialReplacementScope + "."
                    + CampaignProcessBoundaryHooks.AfterReplacementBeforeReadback,
                ["CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_ACK"] = acknowledgement,
            });
        try
        {
            await WaitForFileAsync(
                acknowledgement,
                running,
                CampaignProcessBoundaryHooks.InitialReplacementScope + "."
                + CampaignProcessBoundaryHooks.AfterReplacementBeforeReadback,
                TimeSpan.FromMinutes(3));
            Assert.Equal(
                CampaignProcessBoundaryHooks.InitialReplacementScope + "."
                + CampaignProcessBoundaryHooks.AfterReplacementBeforeReadback + "\n",
                await File.ReadAllTextAsync(acknowledgement));
            running.Process.Kill(entireProcessTree: true);
            await running.Process.WaitForExitAsync();
            var interrupted = await running.CompleteAsync();
            Assert.Empty(interrupted.Stdout);
            Assert.Empty(interrupted.Stderr);

            var checkpoint = CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath));
            Assert.True(checkpoint.IsValid);
            Assert.Equal(0, checkpoint.Artifact!.CheckpointRevision);

            var resumed = await RunAsync(
                Args("resume", fixture.Root, statePath, configurationPath, "snapshot.crash"),
                timeout: TimeSpan.FromMinutes(3));
            AssertCampaign(resumed, 0, "campaign.no-work", 0);
        }
        finally
        {
            if (!running.Process.HasExited)
            {
                running.Process.Kill(entireProcessTree: true);
                await running.Process.WaitForExitAsync();
            }
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task AcceptedCampaign_ResumesInAFreshProcessWithoutProviderReplay()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var fixture = await LoaderFixture.CreateAsync();
        await SetSingleWorkItemSourceAsync(fixture.Root);
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), RequiredPolicy);
        await using var server = new ProposalLoopbackServer();
        var outside = CreatePrivateDirectory("contract-scribe-campaign-accepted");
        var configurationPath = Path.Join(outside, "campaign.json");
        var stateDirectory = Path.Join(outside, "state");
        Directory.CreateDirectory(stateDirectory);
        File.SetUnixFileMode(stateDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var statePath = Path.Join(stateDirectory, "checkpoint.json");
        await WriteConfigurationAsync(configurationPath, server.Endpoint);

        try
        {
            var started = await RunAsync(
                Args("start", fixture.Root, statePath, configurationPath, "snapshot.accepted"),
                timeout: TimeSpan.FromMinutes(5));
            Assert.True(File.Exists(statePath),
                $"exit={started.ExitCode} stdout={started.Stdout} stderr={started.Stderr}");
            var checkpoint = await File.ReadAllBytesAsync(statePath);
            var parsed = CampaignStateJson.Parse(checkpoint);
            Assert.True(parsed.IsValid);
            AssertCampaign(started, 0, "campaign.complete", parsed.Artifact!.CheckpointRevision);
            Assert.NotNull(parsed.Artifact!.State.CumulativeOutcome);
            var acceptedKeys = parsed.Artifact.State.WorkItems
                .Where(item => item.Status == CampaignWorkStatus.Accepted)
                .Select(item => item.WorkItemKey)
                .ToArray();
            Assert.All(parsed.Artifact.State.WorkItems.Where(item =>
                item.Status == CampaignWorkStatus.Accepted),
                item => Assert.Equal(1, item.CandidateAttemptCount));
            var requestCount = server.RequestCount;
            Assert.True(requestCount > 0);

            var resumed = await RunAsync(
                Args("resume", fixture.Root, statePath, configurationPath, "snapshot.accepted"),
                timeout: TimeSpan.FromMinutes(5));
            var resumedState = CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath));
            Assert.True(resumedState.IsValid);
            AssertCampaign(resumed, 0, "campaign.complete", resumedState.Artifact!.CheckpointRevision);
            Assert.Equal(requestCount, server.RequestCount);
            Assert.Equal(acceptedKeys, resumedState.Artifact.State.WorkItems
                .Where(item => item.Status == CampaignWorkStatus.Accepted)
                .Select(item => item.WorkItemKey));
            Assert.All(resumedState.Artifact.State.WorkItems.Where(item =>
                item.Status == CampaignWorkStatus.Accepted),
                item => Assert.Equal(2, item.CandidateAttemptCount));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task EveryFrozenBoundary_IsAcknowledgedTerminatedAndReinvokedAgainstTheSameCheckpoint()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var matrix = ReadBoundaryMatrix();
        Assert.Equal(CampaignProcessBoundaryHooks.Allowlist.Order(StringComparer.Ordinal),
            matrix.Select(item => item.Hook).Order(StringComparer.Ordinal));

        foreach (var vector in matrix)
        {
            await RunCrashVectorAsync(vector);
        }
    }

    private static string[] Args(
        string operation,
        string repository,
        string state,
        string configuration,
        string snapshot) =>
    [
        "campaign", operation,
        "--repository-root", repository,
        "--input", "App/App.csproj",
        "--policy", "policy.json",
        "--snapshot", snapshot,
        "--state", state,
        "--configuration", configuration,
    ];

    private static async Task WriteConfigurationAsync(string destination, Uri? endpoint = null)
        => await WriteConfigurationAsync(destination, endpoint, maximumPatchElapsedMilliseconds: null);

    private static async Task WriteConfigurationAsync(
        string destination,
        Uri? endpoint,
        int? maximumPatchElapsedMilliseconds)
    {
        var fixture = await File.ReadAllBytesAsync(Path.Join(
            RepositoryRoot, "tests", "fixtures", "campaign", "cli", "configuration-valid.json"));
        var root = JsonNode.Parse(fixture)!.AsObject();
        var revision = CommandLineApplication.ApplicationVersion.Split('+') is [_, var value]
            ? value
            : throw new InvalidOperationException("CLI version has no source revision.");
        var product = SHA256.HashData(Encoding.UTF8.GetBytes(
            "contract-scribe/campaign-product-revision/v1\0" + revision));
        root["planning"]!["productContractRevisionSha256"] =
            Convert.ToHexString(product).ToLowerInvariant();
        if (maximumPatchElapsedMilliseconds is { } maximumPatchElapsed)
        {
            root["planning"]!["maximumPatchElapsedMilliseconds"] = maximumPatchElapsed;
        }
        if (endpoint is not null)
        {
            root["provider"]!["endpoint"] = endpoint.AbsoluteUri;
        }
        await File.WriteAllTextAsync(destination, root.ToJsonString(), new UTF8Encoding(false, true));
    }

    [SupportedOSPlatform("linux")]
    private static async Task RunCrashVectorAsync(BoundaryVector vector)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        await SetSingleWorkItemSourceAsync(fixture.Root);
        await File.WriteAllTextAsync(Path.Join(fixture.Root, "policy.json"), RequiredPolicy);
        var sourcePath = Path.Join(fixture.Root, "App", "App.cs");
        if (vector.Scenario == "reduction")
        {
            var source = (await File.ReadAllTextAsync(sourcePath))
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            var firstBreak = source.IndexOf('\n');
            Assert.True(firstBreak >= 0);
            await File.WriteAllTextAsync(
                sourcePath,
                source[..firstBreak] + "\r\n" + source[(firstBreak + 1)..],
                new UTF8Encoding(false, true));
        }
        var originalSource = await File.ReadAllBytesAsync(sourcePath);
        var originalMode = File.GetUnixFileMode(sourcePath);
        await using var server = new ProposalLoopbackServer(vector.Scenario);
        var outside = CreatePrivateDirectory("contract-scribe-campaign-boundary");
        var configurationPath = Path.Join(outside, "campaign.json");
        var stateDirectory = Path.Join(outside, "state");
        Directory.CreateDirectory(stateDirectory);
        File.SetUnixFileMode(stateDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var statePath = Path.Join(stateDirectory, "checkpoint.json");
        var acknowledgement = Path.Join(outside, "hook.ack");
        await WriteConfigurationAsync(
            configurationPath,
            server.Endpoint,
            vector.Scenario == "closed-patch" ? 1 : null);

        using var running = Start(
            Args("start", fixture.Root, statePath, configurationPath, "snapshot.boundary"),
            new Dictionary<string, string?>
            {
                ["DOTNET_STARTUP_HOOKS"] = StartupHookPath,
                ["CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_NAME"] = vector.Hook,
                ["CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_ACK"] = acknowledgement,
            });
        try
        {
            await WaitForFileAsync(acknowledgement, running, vector.Hook, TimeSpan.FromMinutes(3));
            Assert.Equal(vector.Hook + "\n", await File.ReadAllTextAsync(acknowledgement));
            running.Process.Kill(entireProcessTree: true);
            await running.Process.WaitForExitAsync();
            var interrupted = await running.CompleteAsync();
            Assert.Empty(interrupted.Stdout);
            Assert.Empty(interrupted.Stderr);

            await File.WriteAllBytesAsync(sourcePath, originalSource);
            File.SetUnixFileMode(sourcePath, originalMode);
            if (File.Exists(statePath))
            {
                Assert.True(CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath)).IsValid,
                    vector.Hook);
            }

            var operation = File.Exists(statePath) ? "resume" : "start";
            var recovered = await RunAsync(
                Args(operation, fixture.Root, statePath, configurationPath, "snapshot.boundary"),
                timeout: TimeSpan.FromMinutes(5));
            AssertControlledCampaign(recovered, vector.Hook);
            if (File.Exists(statePath))
            {
                Assert.True(CampaignStateJson.Parse(await File.ReadAllBytesAsync(statePath)).IsValid,
                    vector.Hook);
            }
        }
        finally
        {
            if (!running.Process.HasExited)
            {
                running.Process.Kill(entireProcessTree: true);
                await running.Process.WaitForExitAsync();
            }
            if (File.Exists(sourcePath))
            {
                File.SetUnixFileMode(sourcePath, originalMode);
                await File.WriteAllBytesAsync(sourcePath, originalSource);
            }
            Directory.Delete(outside, recursive: true);
        }
    }

    private static void AssertControlledCampaign(ProcessResult result, string hook)
    {
        Assert.NotEqual(-1, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        var outcome = document.RootElement.GetProperty("outcome").GetString();
        Assert.False(
            string.Equals("campaign.host-contract-error", outcome, StringComparison.Ordinal),
            $"{hook}: exit={result.ExitCode} stdout={result.Stdout} stderr={result.Stderr}");
        Assert.Contains(document.RootElement.GetProperty("terminalLayer").GetString(),
            new[] { "preflight", "state", "execution", "campaign" });
        Assert.True(result.Stderr.Length <= 512, hook + ": " + result.Stderr);
    }

    private static IReadOnlyList<BoundaryVector> ReadBoundaryMatrix()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Join(
            RepositoryRoot, "tests", "fixtures", "campaign", "cli", "process-boundary-matrix.json")));
        return document.RootElement.EnumerateArray().Select(item => new BoundaryVector(
            item.GetProperty("hook").GetString()!,
            item.GetProperty("scenario").GetString()!)).ToArray();
    }

    private static Task SetSingleWorkItemSourceAsync(string repositoryRoot) =>
        File.WriteAllTextAsync(
            Path.Join(repositoryRoot, "App", "App.cs"),
            "namespace Fixture;\n/// <summary>Provides fixture operations.</summary>\npublic static class App\n{\n    public static void Run() { }\n}\n");

    private static void AssertCampaign(
        ProcessResult result,
        int exitCode,
        string outcome,
        long? checkpointRevision)
    {
        Assert.True(result.ExitCode == exitCode,
            $"exit={result.ExitCode} stdout={result.Stdout} stderr={result.Stderr}");
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        Assert.Equal(outcome, root.GetProperty("outcome").GetString());
        Assert.Equal(
            new[]
            {
                "campaignEnvelopeVersion", "terminalLayer", "cliContractBaseline", "toolVersion",
                "operation", "outcome", "diagnosticCodes", "checkpointRevision",
            },
            root.EnumerateObject().Select(property => property.Name));
        if (checkpointRevision is null)
        {
            Assert.Equal(JsonValueKind.Null, root.GetProperty("checkpointRevision").ValueKind);
        }
        else
        {
            Assert.Equal(checkpointRevision.Value, root.GetProperty("checkpointRevision").GetInt64());
        }
        Assert.Equal(exitCode == 0 ? string.Empty : $"{outcome}: campaign stopped: {outcome}\n", result.Stderr);
    }

    private static async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null)
    {
        using var process = Start(arguments, null);
        try
        {
            await process.Process.WaitForExitAsync().WaitAsync(timeout ?? TimeSpan.FromMinutes(1));
            return await process.CompleteAsync();
        }
        catch
        {
            if (!process.Process.HasExited)
            {
                process.Process.Kill(entireProcessTree: true);
                await process.Process.WaitForExitAsync();
            }
            throw;
        }
    }

    private static IReadOnlyDictionary<string, string?> HookEnvironment(
        string hook,
        string acknowledgement,
        string release) => new Dictionary<string, string?>
        {
            ["DOTNET_STARTUP_HOOKS"] = StartupHookPath,
            ["CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_NAME"] = hook,
            ["CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_ACK"] = acknowledgement,
            ["CONTRACTSCRIBE_TEST_CAMPAIGN_HOOK_RELEASE"] = release,
        };

    private static async Task StopAsync(RunningProcess running)
    {
        if (!running.Process.HasExited)
        {
            running.Process.Kill(entireProcessTree: true);
            await running.Process.WaitForExitAsync();
        }
    }

    private static RunningProcess Start(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(CliPath);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "true";
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            start.Environment[name] = value;
        }
        return new RunningProcess(Process.Start(start)
            ?? throw new InvalidOperationException("Campaign CLI process failed to start."));
    }

    private static async Task WaitForFileAsync(
        string path,
        RunningProcess running,
        string expectedHook,
        TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (running.Process.HasExited)
            {
                var result = await running.CompleteAsync();
                throw new Xunit.Sdk.XunitException(
                    $"Campaign CLI exited before hook '{expectedHook}' acknowledgement: exit={result.ExitCode} "
                    + $"stdout={result.Stdout} stderr={result.Stderr}");
            }
            if (elapsed.Elapsed > timeout)
            {
                throw new TimeoutException("Campaign hook acknowledgement was not observed.");
            }
            await Task.Delay(50);
        }
    }

    private static string CreatePrivateDirectory(string prefix)
    {
        var path = Path.Join(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Join(current.FullName, "ContractScribe.slnx"))) return current.FullName;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed record BoundaryVector(string Hook, string Scenario);

    private sealed class ProposalLoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource disposal = new();
        private readonly Task serverTask;
        private int requestCount;

        private readonly string scenario;
        internal ProposalLoopbackServer(string scenario = "accepted")
        {
            this.scenario = scenario;
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/v1/chat/completions");
            serverTask = ServeAsync();
        }

        internal Uri Endpoint { get; }

        internal int RequestCount => Volatile.Read(ref requestCount);

        public async ValueTask DisposeAsync()
        {
            disposal.Cancel();
            listener.Stop();
            try
            {
                await serverTask;
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Listener cancellation is the expected test cleanup path.
            }
            disposal.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!disposal.IsCancellationRequested)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(disposal.Token);
                    await using var stream = client.GetStream();
                    var body = await ReadHttpBodyAsync(stream, disposal.Token);
                    Interlocked.Increment(ref requestCount);
                    var response = scenario == "closed-proposal"
                        ? CreateSkipResponse()
                        : CreateProposalResponse(body);
                    var headers = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, disposal.Token);
                    await stream.WriteAsync(response, disposal.Token);
                }
                catch (IOException) when (!disposal.IsCancellationRequested)
                {
                    // An abruptly terminated client may reset its active loopback connection.
                }
            }
        }

        private static byte[] CreateSkipResponse()
        {
            var terminal = "{\"kind\":\"skip\",\"reason\":\"scribe.skip.insufficient-evidence\",\"evidenceReferenceIds\":[]}";
            return TerminalResponse(terminal);
        }

        private static byte[] CreateProposalResponse(byte[] body)
        {
            using var wire = JsonDocument.Parse(body);
            JsonElement? targetEvidence = null;
            foreach (var message in wire.RootElement.GetProperty("messages").EnumerateArray())
            {
                var content = message.GetProperty("content").GetString();
                if (content is null)
                {
                    continue;
                }
                using var parsed = JsonDocument.Parse(content);
                if (parsed.RootElement.TryGetProperty("authority", out var authority)
                    && authority.GetString() == "target-evidence")
                {
                    targetEvidence = parsed.RootElement.Clone();
                    break;
                }
            }
            var evidence = targetEvidence ?? throw new InvalidDataException("Target evidence message was not found.");
            var evidenceReferenceId = evidence.GetProperty("evidenceReferences")[0]
                .GetProperty("evidenceReferenceId").GetString()!;
            var units = new JsonArray
            {
                ContentUnit("content.summary", null, null, evidenceReferenceId),
            };
            foreach (var component in evidence.GetProperty("applicableComponents").EnumerateArray())
            {
                var kind = component.GetProperty("kind").GetString() switch
                {
                    "typeParameter" => "content.type-parameter",
                    "parameter" => "content.parameter",
                    "return" => "content.return",
                    "value" => "content.value",
                    _ => throw new InvalidDataException("Unknown component kind."),
                };
                units.Add(ContentUnit(
                    kind,
                    component.GetProperty("identity").GetString(),
                    component.TryGetProperty("name", out var name) ? name.GetString() : null,
                    evidenceReferenceId));
            }
            var terminal = new JsonObject
            {
                ["kind"] = "proposal",
                ["target"] = JsonNode.Parse(evidence.GetProperty("terminalTarget").GetRawText()),
                ["contentUnits"] = units,
            };
            return TerminalResponse(terminal.ToJsonString());
        }

        private static byte[] TerminalResponse(string terminal)
        {
            var response = new
            {
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new
                        {
                            role = "assistant",
                            tool_calls = new[]
                            {
                                new
                                {
                                    id = "call.terminal",
                                    type = "function",
                                    function = new
                                    {
                                        name = "cs_terminal",
                                        arguments = terminal,
                                    },
                                },
                            },
                        },
                        finish_reason = "tool_calls",
                    },
                },
            };
            return JsonSerializer.SerializeToUtf8Bytes(response);
        }

        private static JsonObject ContentUnit(
            string kind,
            string? componentIdentity,
            string? name,
            string evidenceReferenceId)
        {
            var unit = new JsonObject
            {
                ["kind"] = kind,
                ["lines"] = new JsonArray("Documents the selected contract."),
                ["claimCategoryId"] = "claim.behavior",
                ["evidenceReferenceIds"] = new JsonArray(evidenceReferenceId),
            };
            if (componentIdentity is not null) unit["componentIdentity"] = componentIdentity;
            if (name is not null) unit["name"] = name;
            return unit;
        }

        private static async Task<byte[]> ReadHttpBodyAsync(Stream stream, CancellationToken cancellationToken)
        {
            var headerBytes = new List<byte>();
            while (headerBytes.Count < 4
                   || headerBytes[^4] != '\r'
                   || headerBytes[^3] != '\n'
                   || headerBytes[^2] != '\r'
                   || headerBytes[^1] != '\n')
            {
                var one = new byte[1];
                if (await stream.ReadAsync(one, cancellationToken) != 1) throw new EndOfStreamException();
                headerBytes.Add(one[0]);
            }
            var headers = Encoding.ASCII.GetString([.. headerBytes]);
            var lengthLine = headers.Split("\r\n", StringSplitOptions.None)
                .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var length = int.Parse(lengthLine[(lengthLine.IndexOf(':') + 1)..],
                System.Globalization.CultureInfo.InvariantCulture);
            var body = new byte[length];
            await stream.ReadExactlyAsync(body, cancellationToken);
            return body;
        }
    }

    private sealed class RunningProcess : IDisposable
    {
        private readonly Task<string> stdout;
        private readonly Task<string> stderr;

        internal RunningProcess(Process process)
        {
            Process = process;
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
        }

        internal Process Process { get; }

        internal async Task<ProcessResult> CompleteAsync() =>
            new(Process.ExitCode,
                (await stdout).Replace("\r\n", "\n", StringComparison.Ordinal),
                (await stderr).Replace("\r\n", "\n", StringComparison.Ordinal));

        public void Dispose() => Process.Dispose();
    }
}
