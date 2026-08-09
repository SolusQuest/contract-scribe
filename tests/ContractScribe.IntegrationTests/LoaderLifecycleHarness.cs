using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace ContractScribe.Roslyn.IntegrationTests;

internal sealed class LoaderLifecycleHarness : IAsyncDisposable
{
    internal const int SuccessExit = 0;
    internal const int SetupRejectedExit = 64;
    internal const int OutcomeMismatchExit = 65;
    internal const int ControlFailureExit = 66;
    internal const byte Cancel = 2;
    internal const byte InjectUnexpected = 3;
    internal const byte Result = 5;
    internal const byte ResultAcknowledged = 6;
    internal const byte SessionReady = 7;
    internal const byte ReleaseSession = 8;
    private const byte ProtocolVersion = 1;
    private const byte Hello = 1;
    private const byte CommandApplied = 4;
    private const byte SessionReleased = 9;
    private const byte TaskReady = 1;
    private const byte TaskRelease = 2;
    private const byte TaskCompleted = 3;
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(45);

    private readonly NamedPipeServerStream control;
    private readonly NamedPipeServerStream task;
    private readonly Task controlConnection;
    private readonly Task taskConnection;
    private readonly Task<string> stdout;
    private readonly Task<string> stderr;
    private readonly ProcessIdentity probeIdentity;
    private bool taskReleased;
    private bool disposed;

    private LoaderLifecycleHarness(
        Process probe,
        NamedPipeServerStream control,
        Task controlConnection,
        NamedPipeServerStream task,
        Task taskConnection,
        Guid controlToken,
        Guid taskToken)
    {
        Probe = probe;
        this.control = control;
        this.controlConnection = controlConnection;
        this.task = task;
        this.taskConnection = taskConnection;
        ControlToken = controlToken;
        TaskToken = taskToken;
        probeIdentity = new ProcessIdentity(
            probe.Id,
            probe.StartTime.ToUniversalTime().Ticks);
        stdout = probe.StandardOutput.ReadToEndAsync();
        stderr = probe.StandardError.ReadToEndAsync();
    }

    public Process Probe { get; }

    public Guid ControlToken { get; }

    public Guid TaskToken { get; }

    public ProcessIdentity? TaskIdentity { get; private set; }

    public ProcessIdentity? BuildHostIdentity { get; private set; }

    public static async Task<LoaderLifecycleHarness> StartAsync(
        string repositoryRoot,
        string mode,
        bool configureControl = true,
        bool missingControlReceiver = false,
        bool injectSerializationFailure = false)
    {
        var controlPipeName = $"contract-scribe-81-control-{Guid.NewGuid():N}";
        var taskPipeName = $"contract-scribe-81-task-{Guid.NewGuid():N}";
        var controlToken = Guid.NewGuid();
        var taskToken = Guid.NewGuid();
        var control = CreatePipe(controlPipeName);
        var task = CreatePipe(taskPipeName);
        var controlConnection = control.WaitForConnectionAsync();
        var taskConnection = task.WaitForConnectionAsync();
        var startInfo = CreateStartInfo(repositoryRoot, mode);
        startInfo.Environment["ContractScribeBuildHostProbeAssembly"] = BuildHostProbePath();
        startInfo.Environment["ContractScribeBuildHostProbePipe"] = taskPipeName;
        startInfo.Environment["ContractScribeBuildHostProbeToken"] = taskToken.ToString("N");
        if (configureControl)
        {
            startInfo.Environment["ContractScribeLoaderControlPipe"] = missingControlReceiver
                ? $"{controlPipeName}-missing"
                : controlPipeName;
            startInfo.Environment["ContractScribeLoaderControlToken"] = controlToken.ToString("N");
        }
        if (injectSerializationFailure)
        {
            startInfo.Environment["ContractScribeLoaderInjectSerializationFailure"] = "1";
        }

        var probe = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Loader lifecycle probe failed to start.");
        var harness = new LoaderLifecycleHarness(
            probe,
            control,
            controlConnection,
            task,
            taskConnection,
            controlToken,
            taskToken);
        if (!configureControl || missingControlReceiver)
        {
            return harness;
        }

        try
        {
            await controlConnection.WaitAsync(BarrierTimeout);
            var hello = await Task.Run(() => ReadHello(control)).WaitAsync(BarrierTimeout);
            Assert.Equal(controlToken, hello);
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    public async Task WaitForTaskReadyAsync()
    {
        await taskConnection.WaitAsync(BarrierTimeout);
        var ready = await Task.Run(() => ReadTaskReady(task)).WaitAsync(BarrierTimeout);
        Assert.Equal(TaskToken, ready.Token);
        Assert.Equal(ready.StartIdentity, ProcessStartIdentity(ready.ProcessId));
        TaskIdentity = new ProcessIdentity(ready.ProcessId, ready.StartIdentity);
        BuildHostIdentity = FindBuildHostIdentity(ready.ProcessId, Probe.Id);
    }

    public async Task SendCommandAsync(byte command)
    {
        await control.WriteAsync(new[] { command });
        await control.FlushAsync();
        var acknowledgement = await ReadExactlyAsync(control, 2).WaitAsync(BarrierTimeout);
        Assert.Equal(ProtocolVersion, acknowledgement[0]);
        Assert.Equal(CommandApplied, acknowledgement[1]);
    }

    public async Task WriteControlByteAsync(byte value)
    {
        await control.WriteAsync(new[] { value });
        await control.FlushAsync();
    }

    public async Task ReleaseTaskAsync()
    {
        if (taskReleased)
        {
            return;
        }
        await task.WriteAsync(new[] { TaskRelease });
        await task.FlushAsync();
        var completed = await ReadExactlyAsync(task, 2).WaitAsync(BarrierTimeout);
        Assert.Equal(ProtocolVersion, completed[0]);
        Assert.Equal(TaskCompleted, completed[1]);
        taskReleased = true;
    }

    public async Task<LifecycleResult> ReadResultAsync(
        byte expectedKind = Result,
        byte acknowledgement = ResultAcknowledged)
    {
        var result = await Task.Run(() => ReadResult(control)).WaitAsync(BarrierTimeout);
        Assert.Equal(expectedKind, result.Kind);
        if (expectedKind == Result)
        {
            await control.WriteAsync(new[] { acknowledgement });
            await control.FlushAsync();
        }
        return result;
    }

    public async Task ReleaseSessionAsync()
    {
        await control.WriteAsync(new[] { ReleaseSession });
        await control.FlushAsync();
        var released = await ReadExactlyAsync(control, 2).WaitAsync(BarrierTimeout);
        Assert.Equal(ProtocolVersion, released[0]);
        Assert.Equal(SessionReleased, released[1]);
    }

    public async Task<ProcessResult> WaitForExitAsync()
    {
        await Probe.WaitForExitAsync().WaitAsync(ProcessTimeout);
        return new ProcessResult(Probe.ExitCode, await stdout, await stderr);
    }

    public void KillProbeAbruptly()
    {
        if (!Probe.HasExited)
        {
            Probe.Kill(entireProcessTree: false);
        }
    }

    public bool OwnedProcessesHaveExited() =>
        (TaskIdentity is null || ProcessIdentityHasExited(TaskIdentity.Value))
        && (BuildHostIdentity is null || ProcessIdentityHasExited(BuildHostIdentity.Value));

    public bool AllProcessesHaveExited() =>
        ProcessIdentityHasExited(probeIdentity) && OwnedProcessesHaveExited();

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;

        if (task.IsConnected && !taskReleased)
        {
            try
            {
                await task.WriteAsync(new[] { TaskRelease });
                await task.FlushAsync();
            }
            catch (Exception exception) when (IsProcessCleanupException(exception))
            {
            }
        }

        control.Dispose();
        task.Dispose();
        if (!Probe.HasExited)
        {
            try
            {
                await Probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                Probe.Kill(entireProcessTree: true);
                await Probe.WaitForExitAsync();
            }
        }

        var identities = new[] { TaskIdentity, BuildHostIdentity }
            .Where(identity => identity is not null)
            .Select(identity => identity!.Value)
            .Distinct()
            .ToArray();
        foreach (var identity in identities)
        {
            await TerminateIdentityAsync(identity);
        }
        Probe.Dispose();
    }

    public static ProcessStartInfo CreateStartInfo(string repositoryRoot, string mode)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(LoaderProbePath());
        startInfo.ArgumentList.Add(repositoryRoot);
        startInfo.ArgumentList.Add("App/App.csproj");
        startInfo.ArgumentList.Add(mode);
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "true";
        return startInfo;
    }

    public static string ProbeAppProject() =>
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <ItemGroup><ProjectReference Include="../Library/Library.csproj" /></ItemGroup>
          <UsingTask TaskName="ContractScribe.BuildHostProbe.BuildHostProbeTask"
                     AssemblyFile="$(ContractScribeBuildHostProbeAssembly)"
                     Condition="'$(DesignTimeBuild)' == 'true' And '$(ContractScribeBuildHostProbeAssembly)' != '' And '$(ContractScribeBuildHostProbePipe)' != '' And '$(ContractScribeBuildHostProbeToken)' != ''" />
          <Target Name="ContractScribeBuildHostProbe"
                  BeforeTargets="CoreCompile"
                  Condition="'$(DesignTimeBuild)' == 'true' And '$(ContractScribeBuildHostProbeAssembly)' != '' And '$(ContractScribeBuildHostProbePipe)' != '' And '$(ContractScribeBuildHostProbeToken)' != ''">
            <BuildHostProbeTask PipeName="$(ContractScribeBuildHostProbePipe)"
                                Token="$(ContractScribeBuildHostProbeToken)"
                                ContinueOnError="WarnAndContinue" />
          </Target>
        </Project>
        """;

    private static NamedPipeServerStream CreatePipe(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static Guid ReadHello(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        Assert.Equal(ProtocolVersion, reader.ReadByte());
        Assert.Equal(Hello, reader.ReadByte());
        var token = reader.ReadBytes(16);
        Assert.Equal(16, token.Length);
        return new Guid(token);
    }

    private static TaskReadyMessage ReadTaskReady(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        Assert.Equal(ProtocolVersion, reader.ReadByte());
        Assert.Equal(TaskReady, reader.ReadByte());
        var token = reader.ReadBytes(16);
        Assert.Equal(16, token.Length);
        return new TaskReadyMessage(
            new Guid(token),
            reader.ReadInt32(),
            reader.ReadInt64());
    }

    private static LifecycleResult ReadResult(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        Assert.Equal(ProtocolVersion, reader.ReadByte());
        var kind = reader.ReadByte();
        var status = (RepositoryLoadStatus)reader.ReadByte();
        Assert.Equal(1, reader.ReadByte());
        var code = ReadBoundedString(reader, 128);
        var phase = (LoaderExecutionPhase)reader.ReadByte();
        var lifecycle = reader.ReadUInt16();
        var exceptionCount = reader.ReadByte();
        Assert.InRange(exceptionCount, 0, LoaderExecutionTrace.MaximumExceptionRecords);
        var exceptions = new List<LifecycleExceptionResult>(exceptionCount);
        for (var index = 0; index < exceptionCount; index++)
        {
            var role = (LoaderExceptionRole)reader.ReadByte();
            var boundary = (LoaderExceptionBoundary)reader.ReadByte();
            var exceptionPhase = (LoaderExecutionPhase)reader.ReadByte();
            var hResult = reader.ReadInt32();
            var native = reader.ReadInt32();
            var typeCount = reader.ReadByte();
            Assert.InRange(typeCount, 0, LoaderExecutionTrace.MaximumTypeDepth);
            var types = new List<string>(typeCount);
            for (var typeIndex = 0; typeIndex < typeCount; typeIndex++)
            {
                types.Add(ReadBoundedString(
                    reader,
                    LoaderExecutionTrace.MaximumTypeNameLength));
            }
            exceptions.Add(new LifecycleExceptionResult(
                role,
                boundary,
                exceptionPhase,
                hResult,
                native == int.MinValue ? null : native,
                types));
        }
        return new LifecycleResult(kind, status, code, phase, lifecycle, exceptions);
    }

    private static string ReadBoundedString(BinaryReader reader, int maximumLength)
    {
        var value = reader.ReadString();
        Assert.True(value.Length <= maximumLength);
        return value;
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length)
    {
        var bytes = new byte[length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset));
            if (read == 0)
            {
                throw new EndOfStreamException("The lifecycle pipe closed before the frame completed.");
            }
            offset += read;
        }
        return bytes;
    }

    private static ProcessIdentity FindBuildHostIdentity(int taskProcessId, int probeProcessId)
    {
        var expectedPath = ExpectedBuildHostPath();
        var visited = new HashSet<int>();
        var current = taskProcessId;
        while (current != probeProcessId && visited.Add(current))
        {
            using var process = Process.GetProcessById(current);
            var identity = new ProcessIdentity(
                current,
                process.StartTime.ToUniversalTime().Ticks);
            if (IsExpectedBuildHostProcess(process, expectedPath))
            {
                Assert.True(IsDescendantOf(identity.ProcessId, probeProcessId));
                return identity;
            }
            current = ParentProcessId(current)
                ?? throw new InvalidOperationException("The task process lost its BuildHost ancestry.");
        }
        throw new InvalidOperationException("The task was not attributable to the expected BuildHost module.");
    }

    private static bool IsExpectedBuildHostProcess(Process process, string expectedPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            var commandLine = File.ReadAllBytes($"/proc/{process.Id}/cmdline");
            return Encoding.UTF8.GetString(commandLine)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Any(argument => string.Equals(
                    Path.GetFullPath(argument),
                    Path.GetFullPath(expectedPath),
                    StringComparison.Ordinal));
        }
        return process.Modules.Cast<ProcessModule>().Any(module =>
            string.Equals(
                Path.GetFullPath(module.FileName),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDescendantOf(int processId, int ancestorProcessId)
    {
        var visited = new HashSet<int>();
        var current = processId;
        while (visited.Add(current))
        {
            var parent = ParentProcessId(current);
            if (parent == ancestorProcessId)
            {
                return true;
            }
            if (parent is null or <= 1)
            {
                return false;
            }
            current = parent.Value;
        }
        return false;
    }

    private static int? ParentProcessId(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
        }
        using var process = Process.GetProcessById(processId);
        var information = new ProcessBasicInformation();
        var status = NtQueryInformationProcess(
            process.Handle,
            0,
            ref information,
            Marshal.SizeOf<ProcessBasicInformation>(),
            out _);
        return status == 0
            ? checked((int)information.InheritedFromUniqueProcessId)
            : null;
    }

    private static long ProcessStartIdentity(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return process.StartTime.ToUniversalTime().Ticks;
    }

    private static bool ProcessIdentityHasExited(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return process.HasExited
                || process.StartTime.ToUniversalTime().Ticks != identity.StartIdentity;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return true;
        }
    }

    private static async Task TerminateIdentityAsync(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited
                || process.StartTime.ToUniversalTime().Ticks != identity.StartIdentity)
            {
                return;
            }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (IsProcessCleanupException(exception))
        {
        }
    }

    private static bool IsProcessCleanupException(Exception exception) => exception is
        ArgumentException
        or IOException
        or InvalidOperationException
        or System.ComponentModel.Win32Exception
        or TimeoutException;

    private static string LoaderProbePath() => Path.Combine(
        FindRepositoryRoot(),
        "tests",
        "ContractScribe.LoaderProbe",
        "bin",
        Configuration(),
        "net10.0",
        "ContractScribe.LoaderProbe.dll");

    private static string BuildHostProbePath() => Path.Combine(
        FindRepositoryRoot(),
        "tests",
        "ContractScribe.BuildHostProbe",
        "bin",
        Configuration(),
        "netstandard2.0",
        "ContractScribe.BuildHostProbe.dll");

    private static string ExpectedBuildHostPath() => Path.Combine(
        Path.GetDirectoryName(LoaderProbePath())!,
        "BuildHost-netcore",
        "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll");

    private static string Configuration() => AppContext.BaseDirectory.Contains(
        $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
        StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ContractScribe.slnx")))
        {
            current = current.Parent;
        }
        return current?.FullName
            ?? throw new InvalidOperationException("The repository root could not be located.");
    }

    private readonly record struct TaskReadyMessage(
        Guid Token,
        int ProcessId,
        long StartIdentity);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);
}

internal readonly record struct ProcessIdentity(int ProcessId, long StartIdentity);

internal sealed record LifecycleExceptionResult(
    LoaderExceptionRole Role,
    LoaderExceptionBoundary Boundary,
    LoaderExecutionPhase Phase,
    int HResult,
    int? NativeErrorCode,
    IReadOnlyList<string> TypeChain);

internal sealed record LifecycleResult(
    byte Kind,
    RepositoryLoadStatus Status,
    string Code,
    LoaderExecutionPhase Phase,
    ushort Lifecycle,
    IReadOnlyList<LifecycleExceptionResult> Exceptions);

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
