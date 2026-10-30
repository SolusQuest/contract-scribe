using System.Diagnostics;
using System.Text.Json;

internal static class ToolchainSnapshotDriver
{
    internal static async Task<int> RunAsync(string? marker)
    {
        if (marker is not null) await File.WriteAllTextAsync(marker + ".ready", "awaiting-parent-release");
        // No SDK command can execute, or the valid probe exit, before the
        // parent has bound this root's PID/start identity and released it.
        using var input = Console.OpenStandardInput();
        var release = new byte["toolchain-snapshot.release\n"u8.Length];
        try { await input.ReadExactlyAsync(release); }
        catch (EndOfStreamException) { return 64; }
        if (!release.AsSpan().SequenceEqual("toolchain-snapshot.release\n"u8)) return 64;
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("The SDK host is unavailable.");
        async Task<string> Query(string argument)
        {
            if (marker is not null) await File.AppendAllTextAsync(marker, argument + "\n");
            using var child = Process.Start(new ProcessStartInfo(host)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { argument }
            }) ?? throw new InvalidOperationException("The fixed SDK query did not start.");
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync();
            var text = await output;
            var diagnostic = await error;
            if (diagnostic.Length != 0) Console.Error.Write(diagnostic);
            if (child.ExitCode != 0) throw new InvalidOperationException("The fixed SDK query failed.");
            return text;
        }
        var version = await Query("--version");
        var sdks = await Query("--list-sdks");
        Console.Write(JsonSerializer.Serialize(new { sdkVersion = version.Trim(), installedSdks = sdks }));
        return 0;
    }
}
