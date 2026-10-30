using System.Text.Json;

namespace ContractScribe.Roslyn.IntegrationTests;

internal sealed record ToolchainSnapshotResult(string SdkVersion, string InstalledSdks);

internal static class ToolchainSnapshotProbe
{
    internal static async Task<ToolchainSnapshotResult> RunAsync(string host, string workingDirectory, string probe,
        TimeSpan timeout, CancellationToken cancellation = default, OwnedProcessTestHooks? hooks = null, string? marker = null)
    {
        // One original deadline includes identity binding, release and both
        // fixed commands; the runner retains its identity-aware cleanup.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        var suppliedRelease = hooks?.ReleaseStandardInputAfterObservation;
        var controlled = (hooks ?? new()) with
        {
            ReleaseStandardInputAfterObservation = async (process, token) =>
            {
                if (suppliedRelease is not null) await suppliedRelease(process, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                // No StreamWriter buffer may flush a release during cancelled
                // disposal; the child requires the complete fixed byte frame.
                await process.StandardInput.BaseStream.WriteAsync("toolchain-snapshot.release\n"u8.ToArray(), token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
            }
        };
        try
        {
            var result = await OwnedProcessRunner.RunAsync(host, workingDirectory,
                marker is null ? [probe, "toolchain-snapshot"] : [probe, "toolchain-snapshot", marker],
                timeout, deadline.Token, new Dictionary<string, string?> { ["DOTNET_NOLOGO"] = "true", ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" }, controlled).ConfigureAwait(false);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (root.EnumerateObject().Count() != 2) throw new InvalidOperationException("The toolchain snapshot shape is invalid.");
            return new(root.GetProperty("sdkVersion").GetString() ?? throw new InvalidOperationException(),
                root.GetProperty("installedSdks").GetString() ?? throw new InvalidOperationException());
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested)
        { throw new TimeoutException("The original toolchain snapshot deadline expired.", error); }
    }
}
