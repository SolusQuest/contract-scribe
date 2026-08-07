using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ContractScribe.HostValidation;

public sealed class ProcessTreeObserver : IAsyncDisposable
{
    public const string RoslynBuildHostArgumentGrammar = "roslyn-buildhost-netcore-v1";

    private readonly int subjectProcessId;
    private readonly ProcessInstanceIdentity subjectIdentity;
    private readonly IReadOnlyList<ProcessIdentityRule> identityRegistry;
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim samplingGate = new(1, 1);
    private readonly Dictionary<int, ObservedProcess> observed = new();
    private readonly HashSet<ProcessInstanceIdentity> observedIdentities = [];
    private readonly Task sampler;
    private volatile bool complete = true;
    private long completedSampleGeneration;

    internal string DiagnosticCode { get; private set; } =
        "HV000_PROCESS_TREE_OBSERVATION_COMPLETE";

    public ProcessTreeObserver(
        Process subjectProcess,
        IReadOnlyList<ProcessIdentityRule> identityRegistry,
        ProcessInstanceIdentity? authoritativeSubjectIdentity = null)
    {
        subjectProcessId = subjectProcess.Id;
        this.identityRegistry = identityRegistry;
        subjectIdentity = authoritativeSubjectIdentity
            ?? new(subjectProcessId, 0);
        if (authoritativeSubjectIdentity is not null
            && authoritativeSubjectIdentity.ProcessId != subjectProcessId)
        {
            MarkIncomplete("HV948_PROCESS_TREE_ROOT_IDENTITY_CHANGED");
            sampler = SampleAsync(cancellation.Token);
            return;
        }
        try
        {
            var currentIdentity = new ProcessInstanceIdentity(
                subjectProcessId,
                GetStartIdentity(subjectProcess));
            if (authoritativeSubjectIdentity is not null
                && currentIdentity != authoritativeSubjectIdentity)
            {
                MarkIncomplete("HV948_PROCESS_TREE_ROOT_IDENTITY_CHANGED");
                sampler = SampleAsync(cancellation.Token);
                return;
            }
            subjectIdentity = currentIdentity;
            observed[subjectProcessId] = new ObservedProcess(
                subjectProcessId,
                GetParentProcessId(subjectProcess),
                "subject-runtime",
                SanitizeImageName(subjectProcess.ProcessName));
            observedIdentities.Add(subjectIdentity);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException
                or FormatException
                or OverflowException)
        {
            MarkIncomplete("HV947_PROCESS_TREE_ROOT_IDENTITY_UNAVAILABLE");
            subjectIdentity = authoritativeSubjectIdentity
                ?? new(subjectProcessId, 0);
        }
        sampler = SampleAsync(cancellation.Token);
    }

    public bool ObservationComplete
    {
        get
        {
            if (sampler.IsFaulted)
            {
                DiagnosticCode =
                    $"HV955_PROCESS_TREE_SAMPLER_{sampler.Exception?.GetBaseException().GetType().Name.ToUpperInvariant()}";
                return false;
            }
            return complete;
        }
    }

    public long CompletedSampleGeneration => Interlocked.Read(ref completedSampleGeneration);

    public async Task<bool> WaitForSampleAfterAsync(
        long generation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero
            || !await samplingGate.WaitAsync(
                timeout,
                cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        try
        {
            CaptureSample();
            return CompletedSampleGeneration > generation
                && ObservationComplete;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException
                or FormatException
                or OverflowException)
        {
            MarkIncomplete("HV956_PROCESS_TREE_FRESH_SAMPLE_FAILED");
            return false;
        }
        finally
        {
            samplingGate.Release();
        }
    }

    public IReadOnlyList<ObservedProcess> Snapshot()
    {
        lock (observed)
        {
            return observed.Values
                .OrderBy(process => process.ProcessId)
                .ToArray();
        }
    }

    public ProcessTerminationPlan CaptureTerminationPlan()
    {
        var processes = OperatingSystem.IsWindows()
            ? CaptureWindowsProcesses()
            : CapturePortableProcesses();
        if (subjectIdentity.StartIdentity == 0
            || !processes.TryGetValue(subjectProcessId, out var root)
            || root.StartIdentity != subjectIdentity.StartIdentity)
        {
            MarkIncomplete("HV948_PROCESS_TREE_ROOT_IDENTITY_CHANGED");
            return new(subjectIdentity, [], false);
        }

        var descendants = new List<ProcessTerminationTarget>();
        foreach (var candidate in processes.Values)
        {
            if (candidate.ProcessId == subjectProcessId)
            {
                continue;
            }
            var depth = GetDescendantDepth(candidate, processes);
            if (depth is null)
            {
                continue;
            }
            if (candidate.StartIdentity == 0)
            {
                MarkIncomplete("HV949_PROCESS_TREE_DESCENDANT_IDENTITY_UNAVAILABLE");
                continue;
            }
            var identity = new ProcessInstanceIdentity(
                candidate.ProcessId,
                candidate.StartIdentity);
            descendants.Add(new(
                identity,
                candidate.ParentId,
                depth.Value));
            lock (observed)
            {
                if (!observedIdentities.Contains(identity))
                {
                    MarkIncomplete("HV950_PROCESS_TREE_DESCENDANT_NOT_SAMPLED");
                }
                observedIdentities.Add(identity);
                observed[candidate.ProcessId] = new(
                    candidate.ProcessId,
                    candidate.ParentId,
                    ClassifyProcess(candidate.ProcessId, candidate.Name),
                    candidate.Name);
            }
        }
        return new(
            subjectIdentity,
            descendants
                .OrderByDescending(target => target.Depth)
                .ThenBy(target => target.Identity.ProcessId)
                .ToArray(),
            ObservationComplete);
    }

    public bool IsCurrentTerminationTarget(ProcessTerminationTarget target)
    {
        var processes = OperatingSystem.IsWindows()
            ? CaptureWindowsProcesses()
            : CapturePortableProcesses();
        return IsCurrentDescendant(
            subjectIdentity,
            target.Identity,
            processes.Values
                .Where(process => process.StartIdentity != 0)
                .Select(process => new ProcessSnapshotIdentity(
                    new(
                        process.ProcessId,
                        process.StartIdentity),
                    process.ParentId))
                .ToArray());
    }

    public static bool IsCurrentDescendant(
        ProcessInstanceIdentity root,
        ProcessInstanceIdentity candidate,
        IReadOnlyList<ProcessSnapshotIdentity> processes)
    {
        var table = processes.ToDictionary(
            process => process.Identity.ProcessId,
            process => process);
        if (!table.TryGetValue(candidate.ProcessId, out var current)
            || current.Identity != candidate)
        {
            return false;
        }
        var visited = new HashSet<int>();
        while (visited.Add(current.Identity.ProcessId))
        {
            if (current.Identity.ProcessId == root.ProcessId)
            {
                return current.Identity == root;
            }
            if (current.ParentProcessId <= 0
                || !table.TryGetValue(current.ParentProcessId, out current))
            {
                return false;
            }
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        try
        {
            await sampler.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            MarkIncomplete("HV953_PROCESS_TREE_SAMPLER_FAULTED");
        }
        cancellation.Dispose();
        samplingGate.Dispose();
    }

    private async Task SampleAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await samplingGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                CaptureSample();
            }
            finally
            {
                samplingGate.Release();
            }

            await Task.Delay(20, token).ConfigureAwait(false);
        }
    }

    private void CaptureSample()
    {
        var processes = OperatingSystem.IsWindows()
            ? CaptureWindowsProcesses()
            : CapturePortableProcesses();

        foreach (var candidate in processes.Values)
        {
            if (GetDescendantDepth(candidate, processes) is null)
            {
                continue;
            }

            var role = candidate.ProcessId == subjectProcessId
                ? "subject-runtime"
                : ClassifyProcess(candidate.ProcessId, candidate.Name);
            lock (observed)
            {
                observed[candidate.ProcessId] = new ObservedProcess(
                    candidate.ProcessId,
                    candidate.ParentId,
                    role,
                    candidate.Name);
                if (candidate.StartIdentity != 0)
                {
                    observedIdentities.Add(new(
                        candidate.ProcessId,
                        candidate.StartIdentity));
                }
            }
        }

        Interlocked.Increment(ref completedSampleGeneration);
    }

    private int? GetDescendantDepth(
        CapturedProcess candidate,
        IReadOnlyDictionary<int, CapturedProcess> processes)
    {
        var current = candidate;
        var visited = new HashSet<int>();
        var depth = 0;
        while (visited.Add(current.ProcessId))
        {
            if (current.ProcessId == subjectProcessId)
            {
                return current.StartIdentity == subjectIdentity.StartIdentity
                    ? depth
                    : null;
            }
            if (current.ParentId <= 0
                || !processes.TryGetValue(current.ParentId, out current))
            {
                return null;
            }
            depth++;
        }
        return null;
    }

    public static string ClassifyIdentity(
        string imageName,
        string? entryPointPath,
        IReadOnlyList<string> commandArguments,
        IReadOnlyList<ProcessIdentityRule> identityRegistry)
    {
        return ClassifyIdentityCore(
            imageName,
            entryPointPath,
            [],
            commandArguments,
            identityRegistry);
    }

    public static string ClassifyDotnetIdentity(
        string entryPointPath,
        IReadOnlyList<string> hostArguments,
        IReadOnlyList<string> commandArguments,
        IReadOnlyList<ProcessIdentityRule> identityRegistry) =>
        ClassifyIdentityCore(
            "dotnet",
            entryPointPath,
            hostArguments,
            commandArguments,
            identityRegistry);

    private static string ClassifyIdentityCore(
        string imageName,
        string? entryPointPath,
        IReadOnlyList<string> hostArguments,
        IReadOnlyList<string> commandArguments,
        IReadOnlyList<ProcessIdentityRule> identityRegistry)
    {
        if (IsRestoreOrRuntimeDownload(hostArguments)
            || IsRestoreOrRuntimeDownload(commandArguments))
        {
            return "restore-or-runtime-download";
        }
        if (entryPointPath is not null && File.Exists(entryPointPath))
        {
            var fingerprint = ComputeIdentityFingerprint(
                imageName,
                entryPointPath,
                commandArguments);
            var matches = identityRegistry
                .Where(rule => rule.ArgumentGrammar is null
                    ? rule.FingerprintSha256 == fingerprint
                    : MatchesGrammarRule(
                        imageName,
                        entryPointPath,
                        hostArguments,
                        commandArguments,
                        rule))
                .ToArray();
            if (matches.Length == 1)
            {
                return ClassifyProtectedCommand(
                    imageName,
                    entryPointPath,
                    commandArguments,
                    matches[0]);
            }
        }

        if (entryPointPath is not null
            && identityRegistry.Any(rule =>
                rule.ArgumentGrammar == RoslynBuildHostArgumentGrammar)
            && Path.GetFileName(entryPointPath).Equals(
                "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll",
                StringComparison.OrdinalIgnoreCase))
        {
            return "unknown-descendant";
        }
        return imageName.StartsWith("ContractScribe", StringComparison.OrdinalIgnoreCase)
            || imageName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
             ? "contractscribe-worker"
             : "unknown-descendant";
    }

    private static bool MatchesGrammarRule(
        string imageName,
        string entryPointPath,
        IReadOnlyList<string> hostArguments,
        IReadOnlyList<string> commandArguments,
        ProcessIdentityRule rule)
    {
        if (rule.ArgumentGrammar != RoslynBuildHostArgumentGrammar
            || rule.EntryPointPath is null
            || !imageName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(entryPointPath).Replace('\\', '/').EndsWith(
                "/" + rule.EntryPointPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)
            || CanonicalJson.Sha256File(entryPointPath) != rule.EntryPointSha256
            || rule.FingerprintSha256 != ComputeGrammarFingerprint(
                "dotnet",
                rule.EntryPointPath,
                rule.EntryPointSha256,
                rule.ArgumentGrammar)
            || !hostArguments.SequenceEqual(
                new[] { "--roll-forward", "LatestMajor" },
                StringComparer.Ordinal))
        {
            return false;
        }
        return MatchesRoslynBuildHostArguments(commandArguments);
    }

    private static bool MatchesRoslynBuildHostArguments(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 2
            || arguments[0] != "--pipe"
            || !Guid.TryParseExact(arguments[1], "D", out _))
        {
            return false;
        }
        var requiredProperties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DesignTimeBuild"] = "true",
            ["NonExistentFile"] = @"__NonExistentSubDir__\__NonExistentFile__",
            ["BuildingInsideVisualStudio"] = "true",
            ["BuildProjectReferences"] = "false",
            ["BuildingProject"] = "false",
            ["ProvideCommandLineArgs"] = "true",
            ["SkipCompilerExecution"] = "true",
            ["ContinueOnError"] = "ErrorAndContinue",
            ["ShouldUnsetParentConfigurationAndPlatform"] = "false"
        };
        var observedProperties = new Dictionary<string, string>(StringComparer.Ordinal);
        string? locale = null;
        for (var index = 2; index < arguments.Count; index += 2)
        {
            if (index + 1 >= arguments.Count)
            {
                return false;
            }
            if (arguments[index] == "--property")
            {
                var separator = arguments[index + 1].IndexOf('=');
                if (separator <= 0
                    || !observedProperties.TryAdd(
                        arguments[index + 1][..separator],
                        arguments[index + 1][(separator + 1)..]))
                {
                    return false;
                }
            }
            else if (arguments[index] == "--locale" && locale is null)
            {
                locale = arguments[index + 1];
            }
            else
            {
                return false;
            }
        }
        if (locale is not null
            && (locale.Length is < 2 or > 32
                || locale.Any(character => !(char.IsAsciiLetterOrDigit(character)
                    || character == '-'))))
        {
            return false;
        }
        foreach (var (key, value) in requiredProperties)
        {
            if (!observedProperties.Remove(key, out var observed) || observed != value)
            {
                return false;
            }
        }
        if (observedProperties.Count == 0)
        {
            return true;
        }
        if (observedProperties.Count != 1
            || !observedProperties.TryGetValue("SolutionDir", out var solutionDir)
            || !Path.IsPathFullyQualified(solutionDir))
        {
            return false;
        }
        var normalized = Path.GetFullPath(solutionDir).Replace('\\', '/');
        return normalized.Contains(
                "/tests/fixtures/m1-host-validation/runtime/",
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)
            && normalized.EndsWith('/');
    }

    private static string ClassifyProtectedCommand(
        string imageName,
        string entryPointPath,
        IReadOnlyList<string> commandArguments,
        ProcessIdentityRule rule)
    {
        if (CanonicalJson.Sha256File(entryPointPath) != rule.EntryPointSha256)
        {
            return "unknown-descendant";
        }
        var entryPointName = Path.GetFileName(entryPointPath);
        if (rule.ArtifactKind is "production-subject" or "fixture-helper"
            && (imageName.StartsWith("ContractScribe", StringComparison.OrdinalIgnoreCase)
            || entryPointName.StartsWith("ContractScribe", StringComparison.OrdinalIgnoreCase))
            )
        {
            return "contractscribe-worker";
        }

        return rule.ArtifactKind == "selected-toolchain"
            ? entryPointName.ToLowerInvariant() switch
            {
                "msbuild" or "msbuild.exe" or "msbuild.dll"
                   or "microsoft.codeanalysis.workspaces.msbuild.buildhost.dll"
                   or "vbcscompiler" or "vbcscompiler.exe" or "vbcscompiler.dll"
                   or "csc" or "csc.exe" or "csc.dll"
                   or "vbc" or "vbc.exe" or "vbc.dll" => "toolchain-owned",
                _ => "unknown-descendant"
            }
            : "unknown-descendant";
    }

    private static bool IsRestoreOrRuntimeDownload(IReadOnlyList<string> arguments) =>
        arguments.Any(argument =>
        {
            var normalized = argument.Trim().ToLowerInvariant();
            return normalized is "restore" or "-restore" or "/restore"
                or "--runtime" or "--runtime-id"
                || normalized.StartsWith("-t:restore", StringComparison.Ordinal)
                || normalized.StartsWith("/t:restore", StringComparison.Ordinal)
                || normalized.StartsWith("--runtime=", StringComparison.Ordinal)
                || normalized.StartsWith("--runtime-id=", StringComparison.Ordinal);
        });

    public static string ComputeIdentityFingerprint(
        string imageName,
        string entryPointPath,
        IReadOnlyList<string> commandArguments)
    {
        var normalizedImage = SanitizeImageName(imageName).ToLowerInvariant();
        var entryPointSha256 = CanonicalJson.Sha256File(entryPointPath);
        using var preimage = new MemoryStream();
        foreach (var value in new[] { normalizedImage, entryPointSha256 }.Concat(commandArguments))
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            preimage.Write(bytes);
            preimage.WriteByte(0);
        }
        return Convert.ToHexStringLower(SHA256.HashData(preimage.ToArray()));
    }

    public static string ComputeGrammarFingerprint(
        string imageName,
        string entryPointPath,
        string entryPointSha256,
        string argumentGrammar) =>
        CanonicalJson.Sha256(CanonicalJson.SerializeCanonical(new
        {
            imageName = SanitizeImageName(imageName).ToLowerInvariant(),
            entryPointPath,
            entryPointSha256,
            argumentGrammar
        }));

    private string ClassifyProcess(int processId, string imageName)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var commandLine = GetCommandLineArguments(process);
            string? entryPoint;
            IReadOnlyList<string> arguments;
            if (imageName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var entryPointIndex = commandLine
                    .Select((argument, index) => (argument, index))
                    .FirstOrDefault(candidate =>
                        candidate.argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        && Path.IsPathFullyQualified(candidate.argument))
                    .index;
                entryPoint = entryPointIndex > 0 ? commandLine[entryPointIndex] : null;
                var hostArguments = entryPoint is null
                    ? []
                    : commandLine.Skip(1).Take(entryPointIndex - 1).ToArray();
                arguments = entryPoint is null ? [] : commandLine.Skip(entryPointIndex + 1).ToArray();
                return ClassifyIdentityCore(
                    imageName,
                    entryPoint,
                    hostArguments,
                    arguments,
                    identityRegistry);
            }
            else
            {
                entryPoint = process.MainModule?.FileName;
                arguments = commandLine.Skip(1).ToArray();
            }
            return ClassifyIdentityCore(
                imageName,
                entryPoint,
                [],
                arguments,
                identityRegistry);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException
                or FormatException
                or OverflowException)
        {
            return imageName.StartsWith("ContractScribe", StringComparison.OrdinalIgnoreCase)
                || imageName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? "contractscribe-worker"
                : "unknown-descendant";
        }
    }

    private static IReadOnlyList<string> GetCommandLineArguments(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            var bytes = File.ReadAllBytes($"/proc/{process.Id}/cmdline");
            return Encoding.UTF8.GetString(bytes)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        const int processCommandLineInformation = 60;
        _ = NtQueryInformationProcess(
            process.Handle,
            processCommandLineInformation,
            IntPtr.Zero,
            0,
            out var requiredLength);
        if (requiredLength <= Marshal.SizeOf<UnicodeString>())
        {
            return [];
        }
        var buffer = Marshal.AllocHGlobal(requiredLength);
        try
        {
            var status = NtQueryInformationProcess(
                process.Handle,
                processCommandLineInformation,
                buffer,
                requiredLength,
                out _);
            if (status != 0)
            {
                return [];
            }
            var commandLine = Marshal.PtrToStructure<UnicodeString>(buffer);
            var text = Marshal.PtrToStringUni(
                commandLine.Buffer,
                commandLine.Length / sizeof(char));
            return SplitWindowsCommandLine(text);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<string> SplitWindowsCommandLine(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
        {
            return [];
        }
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero || count <= 0)
        {
            return [];
        }
        try
        {
            var arguments = new string[count];
            for (var index = 0; index < count; index++)
            {
                arguments[index] = Marshal.PtrToStringUni(
                    Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? string.Empty;
            }
            return arguments;
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    private Dictionary<int, CapturedProcess> CapturePortableProcesses()
    {
        var processes = new Dictionary<int, CapturedProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    processes[process.Id] = new(
                        process.Id,
                        GetParentProcessId(process),
                        SanitizeImageName(process.ProcessName),
                        GetStartIdentity(process));
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                        or InvalidOperationException
                        or System.ComponentModel.Win32Exception
                        or NotSupportedException
                        or IOException
                        or UnauthorizedAccessException
                        or FormatException
                        or OverflowException)
                {
                    if (!HasDisappeared(process))
                    {
                        MarkIncomplete("HV954_PROCESS_TREE_PROCESS_READ_FAILED");
                    }
                }
            }
        }
        return processes;
    }

    private Dictionary<int, CapturedProcess> CaptureWindowsProcesses()
    {
        const uint snapshotProcesses = 0x00000002;
        var snapshot = CreateToolhelp32Snapshot(snapshotProcesses, 0);
        if (snapshot == new IntPtr(-1))
        {
            MarkIncomplete("HV951_PROCESS_TREE_SNAPSHOT_FAILED");
            return [];
        }
        try
        {
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>()
            };
            if (!Process32First(snapshot, ref entry))
            {
                MarkIncomplete("HV952_PROCESS_TREE_SNAPSHOT_EMPTY");
                return [];
            }
            var processes = new Dictionary<int, CapturedProcess>();
            do
            {
                var processId = checked((int)entry.ProcessId);
                var image = Path.GetFileNameWithoutExtension(entry.ExecutableFile);
                processes[processId] = new(
                    processId,
                    checked((int)entry.ParentProcessId),
                    SanitizeImageName(image),
                    TryGetStartIdentity(processId));
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));
            return processes;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    private static bool HasDisappeared(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            // The external-termination path reserves child-status consumption for
            // NativeTerminationObserver.waitpid. A managed HasExited probe here could
            // consume that status before the causal observer records the raw value.
            return !Directory.Exists($"/proc/{process.Id}");
        }
        try
        {
            if (process.HasExited)
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            return true;
        }

        return false;
    }

    private static string SanitizeImageName(string name)
    {
        if (name.Length is < 1 or > 128
            || name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            return "unclassified";
        }
        return name;
    }

    private void MarkIncomplete(string diagnosticCode)
    {
        DiagnosticCode = diagnosticCode;
        complete = false;
    }

    private static int GetParentProcessId(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            var stat = File.ReadAllText($"/proc/{process.Id}/stat");
            var closingParenthesis = stat.LastIndexOf(')');
            var fields = stat[(closingParenthesis + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
        }

        if (OperatingSystem.IsWindows())
        {
            var information = new ProcessBasicInformation();
            var status = NtQueryInformationProcess(
                process.Handle,
                0,
                ref information,
                Marshal.SizeOf<ProcessBasicInformation>(),
                out _);
            if (status != 0)
            {
                throw new System.ComponentModel.Win32Exception(status);
            }
            return information.InheritedFromUniqueProcessId.ToInt32();
        }

        return 0;
    }

    private static long GetStartIdentity(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            return ParseLinuxStartIdentity(
                File.ReadAllText($"/proc/{process.Id}/stat"));
        }
        if (OperatingSystem.IsWindows())
        {
            return process.StartTime.ToUniversalTime().ToFileTimeUtc();
        }
        return process.StartTime.ToUniversalTime().Ticks;
    }

    private static long TryGetStartIdentity(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return GetStartIdentity(process);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException
                or FormatException
                or OverflowException)
        {
            return 0;
        }
    }

    internal static long ParseLinuxStartIdentity(string stat)
    {
        var closingParenthesis = stat.LastIndexOf(')');
        if (closingParenthesis < 0)
        {
            throw new FormatException("Invalid /proc process stat.");
        }
        var fields = stat[(closingParenthesis + 2)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19)
        {
            throw new FormatException("Invalid /proc process stat.");
        }
        return long.Parse(
            fields[19],
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record CapturedProcess(
        int ProcessId,
        int ParentId,
        string Name,
        long StartIdentity);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2A;
        public IntPtr Reserved2B;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
