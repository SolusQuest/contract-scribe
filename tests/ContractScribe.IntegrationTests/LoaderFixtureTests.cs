using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed class LoaderFixtureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryReusableCategoryPublishesOnceAndLoadsFromDistinctConsumers(
        bool withGenerator)
    {
        await using var cache = new LoaderFixtureCache();
        await using var first = await LoaderFixture.CreateAsync(
            withGenerator: withGenerator,
            cache: cache);
        await using var second = await LoaderFixture.CreateAsync(
            withGenerator: withGenerator,
            cache: cache);

        Assert.Equal(1, cache.PreparationCount);
        Assert.Equal(first.PreparationId, second.PreparationId);
        Assert.Equal(first.ShapeKey, second.ShapeKey);
        Assert.NotEqual(first.Root, second.Root);
        await AssertLoadsAsync(first);
        await AssertLoadsAsync(second);
    }

    [Fact]
    public async Task DifferentShapesDoNotSharePreparedOutput()
    {
        await using var ordinary = await LoaderFixture.CreateAsync();
        await using var generator = await LoaderFixture.CreateAsync(withGenerator: true);

        Assert.NotEqual(ordinary.ShapeKey, generator.ShapeKey);
        Assert.NotEqual(ordinary.PreparationId, generator.PreparationId);
    }

    [Fact]
    public async Task ConsumerMutationIsIsolatedFromAnotherConsumer()
    {
        await using var first = await LoaderFixture.CreateAsync();
        await using var second = await LoaderFixture.CreateAsync();
        var secondSource = Path.Join(second.Root, "App", "App.cs");
        var expected = await File.ReadAllTextAsync(secondSource);

        await File.WriteAllTextAsync(
            Path.Join(first.Root, "App", "App.cs"),
            "public static class App { public static string Value => \"mutated\"; }");

        Assert.Equal(expected, await File.ReadAllTextAsync(secondSource));
    }

    [Fact]
    public async Task ConcurrentSameShapeRequestsPerformOnePreparation()
    {
        await using var cache = new LoaderFixtureCache();
        var first = LoaderFixture.CreateAsync(cache: cache);
        var second = LoaderFixture.CreateAsync(cache: cache);
        var fixtures = await Task.WhenAll(first, second);
        await using var left = fixtures[0];
        await using var right = fixtures[1];

        Assert.Equal(1, cache.PreparationCount);
        Assert.Equal(left.PreparationId, right.PreparationId);
        Assert.NotEqual(left.Root, right.Root);
    }

    [Fact]
    public async Task CancelledPreparationCleansBeforeARequestRetries()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateCall = 0;
        await using var cache = new LoaderFixtureCache(
            beforePreparation: async cancellationToken =>
            {
                if (Interlocked.Increment(ref gateCall) != 1)
                {
                    return;
                }
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        using var cancellation = new CancellationTokenSource();
        var cancelled = GetTemplateAsync(
            cache,
            "retry-shape",
            (ownedRoot, token) => CreateFakeTemplateAsync(ownedRoot, "retry-shape", token),
            cancellation.Token);
        await started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var retried = await GetTemplateAsync(
            cache,
            "retry-shape",
            (ownedRoot, token) => CreateFakeTemplateAsync(ownedRoot, "retry-shape", token),
            CancellationToken.None);

        Assert.Equal(2, cache.PreparationCount);
        Assert.True(Directory.Exists(retried.Root));
    }

    [Fact]
    public async Task DisposingReusableStateDoesNotCorruptAnActiveConsumer()
    {
        var cache = new LoaderFixtureCache();
        await using var consumer = await LoaderFixture.CreateAsync(cache: cache);

        await cache.DisposeAsync();
        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(consumer.Root, "App/App.csproj"));

        Assert.True(
            outcome.Status == RepositoryLoadStatus.Success,
            $"{outcome.PrimaryFailure?.Stage}:{outcome.PrimaryFailure?.Code}; secondary={string.Join(',', outcome.SecondaryFacts.Select(fact => fact.Code))}");
        await Assert.IsType<LoadedRepositorySession>(outcome.Session).DisposeAsync();
        Assert.True(Directory.Exists(consumer.Root));
    }

    [Fact]
    public async Task OneCancelledWaiterDoesNotCancelAnotherWaiter()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new LoaderFixtureCache(
            beforePreparation: async cancellationToken =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            });
        using var cancellation = new CancellationTokenSource();
        var first = GetTemplateAsync(
            cache,
            "shared-shape",
            (ownedRoot, token) => CreateFakeTemplateAsync(ownedRoot, "shared-shape", token),
            cancellation.Token);
        var second = GetTemplateAsync(
            cache,
            "shared-shape",
            (ownedRoot, token) => CreateFakeTemplateAsync(ownedRoot, "shared-shape", token),
            CancellationToken.None);
        await started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.TrySetResult();
        var template = await second;

        Assert.Equal(1, cache.PreparationCount);
        Assert.True(Directory.Exists(template.Root));
    }

    [Fact]
    public async Task CancellationAfterPreparationOwnsAndDeletesTheUnpublishedRootBeforeRetry()
    {
        var publicationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? abandonedRoot = null;
        var publicationCall = 0;
        await using var cache = new LoaderFixtureCache(
            beforePublication: async (template, cancellationToken) =>
            {
                if (Interlocked.Increment(ref publicationCall) != 1)
                {
                    return;
                }
                abandonedRoot = template.Root;
                publicationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        using var cancellation = new CancellationTokenSource();
        var cancelled = GetTemplateAsync(
            cache,
            "publish-race",
            (ownedRoot, token) => CreateFakeTemplateAsync(ownedRoot, "publish-race", token),
            cancellation.Token);
        await publicationStarted.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var retried = await GetTemplateAsync(
            cache,
            "publish-race",
            (ownedRoot, token) => CreateFakeTemplateAsync(ownedRoot, "publish-race", token),
            CancellationToken.None);
        Assert.NotNull(abandonedRoot);
        Assert.False(Directory.Exists(abandonedRoot));
        Assert.True(Directory.Exists(retried.Root));
        Assert.Equal(2, cache.PreparationCount);
    }

    [Fact]
    public async Task DisposalWaitsForAnActiveMaterializationLease()
    {
        var useStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new LoaderFixtureCache();
        var use = cache.GetOrPrepareAndUseAsync(
            "dispose-race",
            (ownedRoot, token) => CreateFakeTemplateAsync(ownedRoot, "dispose-race", token),
            async (template, cancellationToken) =>
            {
                useStarted.TrySetResult();
                await releaseUse.Task.WaitAsync(cancellationToken);
                Assert.True(Directory.Exists(template.Root));
                return template.Root;
            },
            CancellationToken.None);
        await useStarted.Task;

        var disposal = cache.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        releaseUse.TrySetResult();

        var root = await use;
        await disposal;
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task FailedStrictDeletionFaultsTheCleanupBarrierAndBlocksRetry()
    {
        var publicationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? ownedRoot = null;
        var cache = new LoaderFixtureCache(
            beforePublication: async (template, cancellationToken) =>
            {
                ownedRoot = template.Root;
                publicationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            deleteDirectory: path => throw new IOException($"locked:{path}"));
        using var cancellation = new CancellationTokenSource();
        var cancelled = GetTemplateAsync(
            cache,
            "delete-failure",
            (ownedRoot, token) => CreateFakeTemplateAsync(ownedRoot, "delete-failure", token),
            cancellation.Token);
        await publicationStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var retryFailure = await Assert.ThrowsAsync<AggregateException>(() => GetTemplateAsync(
            cache,
            "delete-failure",
            (ownedRoot, token) => CreateFakeTemplateAsync(ownedRoot, "delete-failure", token),
            CancellationToken.None));
        Assert.Contains("strict cleanup", retryFailure.Message, StringComparison.Ordinal);
        Assert.Equal(1, cache.PreparationCount);
        Assert.NotNull(ownedRoot);
        Assert.True(Directory.Exists(ownedRoot));
        Directory.Delete(ownedRoot, recursive: true);
    }

    [Fact]
    public async Task FailedPreReturnCleanupFaultsTheBarrierAndBlocksSameShapeRetry()
    {
        string? candidateRoot = null;
        var cache = new LoaderFixtureCache(
            deleteDirectory: path => throw new IOException($"locked-before-return:{path}"));
        var first = GetTemplateAsync(
            cache,
            "pre-return-delete-failure",
            (ownedRoot, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidateRoot = ownedRoot;
                Directory.CreateDirectory(ownedRoot);
                File.WriteAllText(Path.Join(ownedRoot, "partial.txt"), "partial");
                return Task.FromException<LoaderFixtureTemplate>(
                    new InvalidOperationException("test-only preparation failure"));
            },
            CancellationToken.None);

        var firstFailure = await Assert.ThrowsAsync<AggregateException>(() => first);
        var retryFailure = await Assert.ThrowsAsync<AggregateException>(() => GetTemplateAsync(
            cache,
            "pre-return-delete-failure",
            (ownedRoot, token) => CreateFakeTemplateAsync(
                ownedRoot,
                "pre-return-delete-failure",
                token),
            CancellationToken.None));

        Assert.Contains("strict cleanup", firstFailure.Message, StringComparison.Ordinal);
        Assert.Contains("locked-before-return", retryFailure.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, cache.PreparationCount);
        Assert.NotNull(candidateRoot);
        Assert.True(Directory.Exists(candidateRoot));
        Directory.Delete(candidateRoot, recursive: true);
    }

    [Fact]
    public async Task DisabledShapeDoesNotReportFreshFallbackBeforeStrictCleanupCompletes()
    {
        using var deletionStarted = new ManualResetEventSlim();
        using var releaseDeletion = new ManualResetEventSlim();
        await using var cache = new LoaderFixtureCache(
            deleteDirectory: path =>
            {
                deletionStarted.Set();
                Assert.True(releaseDeletion.Wait(TimeSpan.FromSeconds(10)));
                Directory.Delete(path, recursive: true);
            });
        _ = await GetTemplateAsync(
            cache,
            "disable-barrier",
            (ownedRoot, token) => CreateFakeTemplateAsync(ownedRoot, "disable-barrier", token),
            CancellationToken.None);
        var disabling = Task.Run(() => cache.DisableAsync("disable-barrier"));
        Assert.True(deletionStarted.Wait(TimeSpan.FromSeconds(5)));

        var retry = GetTemplateAsync(
            cache,
            "disable-barrier",
            (ownedRoot, token) => CreateFakeTemplateAsync(ownedRoot, "disable-barrier", token),
            CancellationToken.None);
        await Task.Delay(100);
        Assert.False(retry.IsCompleted);
        releaseDeletion.Set();

        await disabling;
        await Assert.ThrowsAsync<LoaderFixtureCacheDisabledException>(() => retry);
        Assert.Equal(1, cache.PreparationCount);
    }

    [Fact]
    public void ExactInputBytesInvalidateTheShapeIdentity()
    {
        var first = LoaderFixture.ComputeFramedInputIdentity(
            [("packages/framework.nupkg", "first"u8.ToArray())]);
        var second = LoaderFixture.ComputeFramedInputIdentity(
            [("packages/framework.nupkg", "other"u8.ToArray())]);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void FixtureBuildCommandsDisableCrossCommandBuildProcesses()
    {
        Assert.Equal(
            ["restore", "Fixture.slnx", "-nodeReuse:false"],
            LoaderFixture.WithOwnedBuildProcessPolicy(["restore", "Fixture.slnx"]));
        Assert.Equal(
            [
                "build",
                "Fixture.slnx",
                "--no-restore",
                "-nodeReuse:false",
                "-property:UseSharedCompilation=false",
            ],
            LoaderFixture.WithOwnedBuildProcessPolicy(["build", "Fixture.slnx", "--no-restore"]));
        Assert.Equal(
            [
                "msbuild",
                "App/App.csproj",
                "-nodeReuse:false",
                "-property:UseSharedCompilation=false",
            ],
            LoaderFixture.WithOwnedBuildProcessPolicy(["msbuild", "App/App.csproj"]));
        Assert.Equal(
            ["restore", "Fixture.slnx", "-nodeReuse:false"],
            LoaderFixture.WithOwnedBuildProcessPolicy(
                ["restore", "Fixture.slnx", "-nodeReuse:false"]));
    }

    [Fact]
    public async Task CancellationTerminatesTheOwnedTreeAndDrainsBothStreams()
    {
        var marker = $"cancel-{Guid.NewGuid():N}";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunHoldTreeAsync(marker, TimeSpan.FromSeconds(30), cancellation.Token));

        await AssertOwnedTreeExitedAndOutputDrainedAsync(exception.Message, marker);
    }

    [Fact]
    public async Task TimeoutTerminatesTheOwnedTreeAndDrainsBothStreams()
    {
        var marker = $"timeout-{Guid.NewGuid():N}";

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            RunHoldTreeAsync(marker, TimeSpan.FromSeconds(3), CancellationToken.None));

        await AssertOwnedTreeExitedAndOutputDrainedAsync(exception.Message, marker);
    }

    [Fact]
    public async Task RootExitWithInheritedStreamChildTerminatesTheObservedTreeAndReportsDrainFailure()
    {
        var marker = $"root-exit-{Guid.NewGuid():N}";
        var hooks = new OwnedProcessTestHooks
        {
            PollingInterval = TimeSpan.FromMilliseconds(25),
            TerminationTimeout = TimeSpan.FromMilliseconds(500),
        };

        var exception = await Assert.ThrowsAsync<IOException>(() => OwnedProcessRunner.RunAsync(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            FindRepositoryRoot(),
            [LoaderProbePath(), "exit-root-hold-child", marker],
            TimeSpan.FromSeconds(30),
            CancellationToken.None,
            new Dictionary<string, string?>
            {
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_NOLOGO"] = "true",
            },
            hooks));

        Assert.Contains("after the command root exited", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(TimeoutException), exception.InnerException?.ToString(), StringComparison.Ordinal);
        await AssertOwnedTreeExitedAndOutputDrainedAsync(
            exception.Message,
            marker,
            requireChildError: false);
        await AssertObservedProcessesExitedAsync(exception.Message);
    }

    [Fact]
    public async Task ObserverEstablishmentFailureReapsTheDirectOwnedTree()
    {
        var marker = $"observer-start-{Guid.NewGuid():N}";
        var hooks = new OwnedProcessTestHooks
        {
            ReadStartIdentity = _ =>
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(750));
                throw new IOException("test-only identity failure");
            },
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunHoldTreeAsync(
                marker,
                TimeSpan.FromSeconds(30),
                CancellationToken.None,
                hooks));

        Assert.Contains("observation could not be established", exception.Message, StringComparison.Ordinal);
        await AssertOwnedTreeExitedAndOutputDrainedAsync(exception.Message, marker);
    }

    [Fact]
    public async Task UnavailableExitObservationFailsClosedAfterTheOwnedTreeIsKilled()
    {
        var marker = $"observer-unavailable-{Guid.NewGuid():N}";
        var hooks = new OwnedProcessTestHooks
        {
            ExitStateOverride = _ => OwnedProcessExitState.ObservationUnavailable,
            TerminationTimeout = TimeSpan.FromMilliseconds(500),
        };

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            RunHoldTreeAsync(marker, TimeSpan.FromSeconds(2), CancellationToken.None, hooks));

        Assert.Contains(
            nameof(OwnedProcessExitState.ObservationUnavailable),
            exception.InnerException?.ToString(),
            StringComparison.Ordinal);
        await AssertOwnedTreeExitedAndOutputDrainedAsync(exception.Message, marker);
    }

    [Fact]
    public async Task KillFailureIsPreservedAfterExactExitIsConfirmed()
    {
        var marker = $"kill-failure-{Guid.NewGuid():N}";
        var hooks = new OwnedProcessTestHooks
        {
            KillProcessTree = process =>
            {
                process.Kill(entireProcessTree: true);
                throw new Win32Exception("test-only kill failure");
            },
        };

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            RunHoldTreeAsync(marker, TimeSpan.FromSeconds(2), CancellationToken.None, hooks));

        Assert.Contains("test-only kill failure", exception.InnerException?.ToString(), StringComparison.Ordinal);
        await AssertOwnedTreeExitedAndOutputDrainedAsync(exception.Message, marker);
    }

    [Fact]
    public async Task ReaderFailureTerminatesTheOwnedTreeInsteadOfWaitingForCommandTimeout()
    {
        var marker = $"reader-failure-{Guid.NewGuid():N}";
        var rootProcessId = 0;
        var hooks = new OwnedProcessTestHooks
        {
            ProcessStarted = process => rootProcessId = process.Id,
            PollingInterval = TimeSpan.FromMilliseconds(25),
            ReadStandardOutput = async _ =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                throw new IOException("test-only reader failure");
            },
        };
        var elapsed = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            RunHoldTreeAsync(marker, TimeSpan.FromSeconds(30), CancellationToken.None, hooks));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), elapsed.Elapsed.ToString());
        Assert.Contains("test-only reader failure", exception.InnerException?.ToString(), StringComparison.Ordinal);
        await AssertProcessExitedAsync(rootProcessId);
        await AssertObservedProcessesExitedAsync(exception.Message);
    }

    [Fact]
    public async Task PrepareEditorConfigPropagatesCancellationToItsOwnedMsBuildTree()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var marker = $"editor-config-{Guid.NewGuid():N}";
        var command = string.Join(
            ' ',
            QuoteXmlCommand(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet"),
            QuoteXmlCommand(LoaderProbePath()),
            "hold-tree",
            marker);
        await File.WriteAllTextAsync(
            Path.Join(fixture.Root, "Directory.Build.targets"),
            $"""
             <Project>
               <Target Name="ContractScribeHoldEditorConfig" BeforeTargets="GenerateMSBuildEditorConfigFile">
                 <Exec Command="{command}" />
               </Target>
             </Project>
             """);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.PrepareEditorConfigAsync(cancellation.Token));

        await AssertOwnedTreeExitedAndOutputDrainedAsync(exception.Message, marker);
    }

    private static Task<LoaderFixtureTemplate> GetTemplateAsync(
        LoaderFixtureCache cache,
        string shapeKey,
        Func<string, CancellationToken, Task<LoaderFixtureTemplate>> prepare,
        CancellationToken cancellationToken) =>
        cache.GetOrPrepareAndUseAsync(
            shapeKey,
            prepare,
            static (template, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(template);
            },
            cancellationToken);

    private static async Task AssertLoadsAsync(LoaderFixture fixture)
    {
        var outcome = await new RepositoryLoader().LoadAsync(
            new RepositoryLoadRequest(fixture.Root, "App/App.csproj"));
        Assert.Equal(RepositoryLoadStatus.Success, outcome.Status);
        await Assert.IsType<LoadedRepositorySession>(outcome.Session).DisposeAsync();
    }

    private static string QuoteXmlCommand(string value) =>
        $"&quot;{System.Security.SecurityElement.Escape(value)}&quot;";

    private static Task<LoaderFixtureTemplate> CreateFakeTemplateAsync(
        string ownedRoot,
        string shapeKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(ownedRoot);
        File.WriteAllText(Path.Join(ownedRoot, "ready.txt"), "ready");
        return Task.FromResult(new LoaderFixtureTemplate(
            ownedRoot,
            Guid.NewGuid().ToString("N"),
            shapeKey,
            "test"));
    }

    private static async Task RunHoldTreeAsync(
        string marker,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        OwnedProcessTestHooks? testHooks = null)
    {
        await OwnedProcessRunner.RunAsync(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            FindRepositoryRoot(),
            [LoaderProbePath(), "hold-tree", marker],
            timeout,
            cancellationToken,
            new Dictionary<string, string?>
            {
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_NOLOGO"] = "true",
            },
            testHooks);
    }

    private static async Task AssertOwnedTreeExitedAndOutputDrainedAsync(
        string message,
        string marker,
        bool requireChildError = true)
    {
        Assert.True(
            message.Contains($"{marker}:root-error", StringComparison.Ordinal),
            message);
        if (requireChildError)
        {
            Assert.True(
                message.Contains($"{marker}:child-error", StringComparison.Ordinal),
                message);
        }
        var match = Regex.Match(
            message,
            $"{Regex.Escape(marker)}:root:(?<root>[0-9]+):child:(?<child>[0-9]+)",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, message);
        await Task.WhenAll(
            AssertProcessExitedAsync(int.Parse(match.Groups["root"].Value)),
            AssertProcessExitedAsync(int.Parse(match.Groups["child"].Value)));
    }

    private static async Task AssertProcessExitedAsync(int processId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                break;
            }
        }

        Assert.Fail($"Process {processId} is still active after the bounded cleanup wait.");
    }

    private static async Task AssertObservedProcessesExitedAsync(string message)
    {
        var match = Regex.Match(
            message,
            "observed descendants: (?<ids>[0-9,]*)\\.",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, message);
        await Task.WhenAll(match.Groups["ids"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(processId => AssertProcessExitedAsync(int.Parse(processId))));
    }

    private static string LoaderProbePath()
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var path = Path.Join(
            FindRepositoryRoot(),
            "tests",
            "ContractScribe.LoaderProbe",
            "bin",
            configuration,
            "net10.0",
            "ContractScribe.LoaderProbe.dll");
        return File.Exists(path)
            ? path
            : throw new InvalidOperationException("The loader probe was not built.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }
}
