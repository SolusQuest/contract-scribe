using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ContractScribe.Core;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed class LoaderFixtureTests
{
    [Fact]
    public async Task SameShapeReusesOnePreparedTemplate()
    {
        await using var first = await LoaderFixture.CreateAsync();
        await using var second = await LoaderFixture.CreateAsync();

        Assert.Equal(first.PreparationId, second.PreparationId);
        Assert.Equal(first.ShapeKey, second.ShapeKey);
        Assert.NotEqual(first.Root, second.Root);
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
        var cancelled = cache.GetOrPrepareAsync(
            "retry-shape",
            token => CreateFakeTemplateAsync("retry-shape", token),
            cancellation.Token);
        await started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var retried = await cache.GetOrPrepareAsync(
            "retry-shape",
            token => CreateFakeTemplateAsync("retry-shape", token),
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
        var first = cache.GetOrPrepareAsync(
            "shared-shape",
            token => CreateFakeTemplateAsync("shared-shape", token),
            cancellation.Token);
        var second = cache.GetOrPrepareAsync(
            "shared-shape",
            token => CreateFakeTemplateAsync("shared-shape", token),
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
    public void ExactInputBytesInvalidateTheShapeIdentity()
    {
        var first = LoaderFixture.ComputeFramedInputIdentity(
            [("packages/framework.nupkg", "first"u8.ToArray())]);
        var second = LoaderFixture.ComputeFramedInputIdentity(
            [("packages/framework.nupkg", "other"u8.ToArray())]);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task CancellationTerminatesTheOwnedTreeAndDrainsBothStreams()
    {
        var marker = $"cancel-{Guid.NewGuid():N}";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunHoldTreeAsync(marker, TimeSpan.FromSeconds(30), cancellation.Token));

        AssertOwnedTreeExitedAndOutputDrained(exception.Message, marker);
    }

    [Fact]
    public async Task TimeoutTerminatesTheOwnedTreeAndDrainsBothStreams()
    {
        var marker = $"timeout-{Guid.NewGuid():N}";

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            RunHoldTreeAsync(marker, TimeSpan.FromSeconds(3), CancellationToken.None));

        AssertOwnedTreeExitedAndOutputDrained(exception.Message, marker);
    }

    private static Task<LoaderFixtureTemplate> CreateFakeTemplateAsync(
        string shapeKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.Join(
            Path.GetTempPath(),
            "contract-scribe-issue80-cache-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Join(root, "ready.txt"), "ready");
        return Task.FromResult(new LoaderFixtureTemplate(
            root,
            Guid.NewGuid().ToString("N"),
            shapeKey,
            "test"));
    }

    private static async Task RunHoldTreeAsync(
        string marker,
        TimeSpan timeout,
        CancellationToken cancellationToken)
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
            });
    }

    private static void AssertOwnedTreeExitedAndOutputDrained(
        string message,
        string marker)
    {
        Assert.Contains($"{marker}:root-error", message, StringComparison.Ordinal);
        Assert.Contains($"{marker}:child-error", message, StringComparison.Ordinal);
        var match = Regex.Match(
            message,
            $"{Regex.Escape(marker)}:root:(?<root>[0-9]+):child:(?<child>[0-9]+)",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, message);
        AssertProcessExited(int.Parse(match.Groups["root"].Value));
        AssertProcessExited(int.Parse(match.Groups["child"].Value));
    }

    private static void AssertProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Assert.True(process.HasExited, $"Process {processId} is still active.");
        }
        catch (ArgumentException)
        {
        }
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
