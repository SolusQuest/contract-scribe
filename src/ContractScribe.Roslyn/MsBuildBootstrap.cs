using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Build.Locator;

namespace ContractScribe.Roslyn;

internal static class MsBuildBootstrap
{
    private static readonly object Gate = new();
    private static RegisteredToolchain? registered;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Maps bootstrap failures to a stable loader fact.")]
    public static async Task<RegisteredToolchain> EnsureRegisteredAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sdkVersion = await ProbeSdkVersionAsync(workingDirectory, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        lock (Gate)
        {
            if (registered is not null)
            {
                if (!string.Equals(registered.Identity.SdkVersion, sdkVersion, StringComparison.Ordinal))
                {
                    throw LoaderException.Toolchain("toolchain.registration-mismatch");
                }

                return registered;
            }

            try
            {
                if (HasPreloadedMsbuild() || MSBuildLocator.IsRegistered)
                {
                    throw LoaderException.Toolchain("toolchain.registration-preloaded");
                }

                MSBuildLocator.AllowQueryAllDotnetLocations = true;
                var instance = MSBuildLocator.QueryVisualStudioInstances()
                    .Where(candidate => candidate.DiscoveryType == DiscoveryType.DotNetSdk)
                    .Where(candidate => string.Equals(SdkVersion(candidate), sdkVersion, StringComparison.Ordinal))
                    .OrderByDescending(candidate => candidate.Version)
                    .FirstOrDefault()
                    ?? throw LoaderException.Toolchain("toolchain.sdk-unavailable");
                MSBuildLocator.RegisterInstance(instance);

                var assemblyPath = Path.Combine(instance.MSBuildPath, "Microsoft.Build.dll");
                var msbuildVersion = FileVersionInfo.GetVersionInfo(assemblyPath).FileVersion ?? "unknown";
                var identity = new ToolchainIdentity(
                    sdkVersion,
                    Environment.Version.ToString(),
                    msbuildVersion,
                    RuntimeInformation.ProcessArchitecture.ToString());
                registered = new RegisteredToolchain(identity, Path.GetFullPath(instance.MSBuildPath));
                return registered;
            }
            catch (LoaderException)
            {
                throw;
            }
            catch (Exception)
            {
                throw LoaderException.Toolchain("toolchain.registration-failed");
            }
        }
    }

    private static async Task<string> ProbeSdkVersionAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var probeToken = timeout.Token;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--version");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "true";

        using var process = Process.Start(startInfo)
            ?? throw LoaderException.Toolchain("toolchain.sdk-probe-failed");
        try
        {
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, 128, probeToken);
            var stderrTask = ReadBoundedAsync(process.StandardError, 4096, probeToken);
            await process.WaitForExitAsync(probeToken);
            var stdout = (await stdoutTask).Trim();
            _ = await stderrTask;
            if (process.ExitCode != 0 || !Regex.IsMatch(stdout, @"^\d+\.\d+\.\d+$"))
            {
                throw LoaderException.Toolchain("toolchain.sdk-probe-failed");
            }

            return stdout;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw LoaderException.Toolchain("toolchain.sdk-probe-failed");
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var buffer = new char[maxCharacters + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        if (count > maxCharacters)
        {
            throw LoaderException.Toolchain("toolchain.sdk-probe-output-invalid");
        }

        return new string(buffer, 0, count);
    }

    private static bool HasPreloadedMsbuild() =>
        AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            assembly.GetName().Name is { } name
            && name.StartsWith("Microsoft.Build", StringComparison.Ordinal)
            && name != "Microsoft.Build.Locator");

    private static string? SdkVersion(VisualStudioInstance instance)
    {
        foreach (var path in new[] { instance.VisualStudioRootPath, instance.MSBuildPath })
        {
            if (path is null)
            {
                continue;
            }

            var match = Regex.Match(path, @"(?:^|[\\/])(?<version>\d+\.\d+\.\d+)(?:[\\/]|$)");
            if (match.Success)
            {
                return match.Groups["version"].Value;
            }
        }

        return null;
    }
}

internal sealed record RegisteredToolchain(ToolchainIdentity Identity, string MsbuildPath);
