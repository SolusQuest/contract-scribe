using System.IO.Pipes;
using System.Text;
using ContractScribe.Core;
using ContractScribe.Roslyn;

internal static class LoaderLifecycleDriver
{
    internal const int SuccessExit = 0;
    internal const int SetupRejectedExit = 64;
    internal const int OutcomeMismatchExit = 65;
    internal const int ControlFailureExit = 66;

    private const byte ProtocolVersion = 1;
    private const byte Hello = 1;
    private const byte Cancel = 2;
    private const byte InjectUnexpected = 3;
    private const byte CommandApplied = 4;
    private const byte Result = 5;
    private const byte ResultAcknowledged = 6;
    private const byte SessionReady = 7;
    private const byte ReleaseSession = 8;
    private const byte SessionReleased = 9;
    private const byte PreTaskHangReached = 10;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResultAcknowledgementTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SessionReleaseTimeout = TimeSpan.FromMinutes(2);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3
            || !TryMode(args[2], out var mode)
            || !Guid.TryParseExact(
                Environment.GetEnvironmentVariable("ContractScribeLoaderControlToken"),
                "N",
                out var token)
            || string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("ContractScribeLoaderControlPipe")))
        {
            return SetupRejectedExit;
        }

        var pipeName = Environment.GetEnvironmentVariable("ContractScribeLoaderControlPipe")!;
        NamedPipeClientStream? control = null;
        Exception? controlFailure = null;
        try
        {
            control = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var connectDeadline = new CancellationTokenSource(ConnectTimeout);
            await control.ConnectAsync(connectDeadline.Token).ConfigureAwait(false);
            await WriteHelloAsync(control, token, connectDeadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsControlException(exception))
        {
            controlFailure = exception;
            control?.Dispose();
            control = null;
        }

        using var cancellation = new CancellationTokenSource();
        using var preTaskHangReached = new ManualResetEventSlim(false);
        using var preTaskHangRelease = new ManualResetEventSlim(false);
        var injectUnexpected = 0;
        var command = mode switch
        {
            LifecycleMode.Cancellation or LifecycleMode.Unexpected => ReadCommandAsync(
                control,
                cancellation,
                () => Volatile.Write(ref injectUnexpected, 1)),
            LifecycleMode.PreTaskHang => ReportPreTaskHangAsync(
                control,
                preTaskHangReached),
            _ => Task.FromResult<Exception?>(null),
        };
        var trace = new LoaderExecutionTrace();
        var loader = new RepositoryLoader(
            stage =>
            {
                if (stage == LoaderStage.GraphEvaluation
                    && mode == LifecycleMode.PreTaskUnexpected)
                {
                    throw new InvalidOperationException("Injected test-only pre-task loader failure.");
                }
                if (stage == LoaderStage.GraphEvaluation
                    && mode == LifecycleMode.PreTaskHang)
                {
                    preTaskHangReached.Set();
                    if (!preTaskHangRelease.Wait(SessionReleaseTimeout))
                    {
                        throw new InvalidOperationException("The bounded pre-task hang expired.");
                    }
                }
                if (stage == LoaderStage.WorkspaceLoad
                    && Volatile.Read(ref injectUnexpected) != 0)
                {
                    throw new InvalidOperationException("Injected test-only unexpected loader failure.");
                }
            },
            trace: trace);
        IReadOnlyList<ToolGeneratedSourceInput>? generated = mode == LifecycleMode.Failure
            ?
            [
                new(
                    "App/App.csproj",
                    "ContractScribe",
                    "FixtureTool",
                    "Broken",
                    "public class {"),
            ]
            : null;

        RepositoryLoadOutcome? outcome = null;
        var sessionDisposed = false;
        var loadInvocationCount = 0;
        try
        {
            Interlocked.Increment(ref loadInvocationCount);
            outcome = await loader.LoadAsync(
                new RepositoryLoadRequest(args[0], args[1], generated),
                cancellation.Token).ConfigureAwait(false);
            var commandFailure = await command.ConfigureAwait(false);
            controlFailure ??= commandFailure;

            if (mode == LifecycleMode.HeldSession
                && outcome.Status == RepositoryLoadStatus.Success
                && outcome.Session is not null
                && control is not null)
            {
                controlFailure ??= await WriteResultAndReadCommandAsync(
                    control,
                    SessionReady,
                    outcome,
                    trace.Snapshot(),
                    loadInvocationCount,
                    ReleaseSession).ConfigureAwait(false);
                await outcome.Session.DisposeAsync().ConfigureAwait(false);
                sessionDisposed = true;
                if (controlFailure is null)
                {
                    controlFailure = await WriteMarkerAsync(control, SessionReleased)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                if (outcome.Session is not null)
                {
                    await outcome.Session.DisposeAsync().ConfigureAwait(false);
                    sessionDisposed = true;
                }

                if (control is not null)
                {
                    controlFailure ??= await WriteResultAndReadCommandAsync(
                        control,
                        Result,
                        outcome,
                        trace.Snapshot(),
                        loadInvocationCount,
                        ResultAcknowledged).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (!sessionDisposed && outcome?.Session is not null)
            {
                await outcome.Session.DisposeAsync().ConfigureAwait(false);
            }
            control?.Dispose();
        }

        Console.WriteLine($"{outcome?.Status}:{outcome?.PrimaryFailure?.Code}");
        if (controlFailure is not null)
        {
            return ControlFailureExit;
        }

        return outcome?.Status == ExpectedStatus(mode)
            ? SuccessExit
            : OutcomeMismatchExit;
    }

    private static async Task<Exception?> ReadCommandAsync(
        NamedPipeClientStream? control,
        CancellationTokenSource cancellation,
        Action injectUnexpected)
    {
        if (control is null)
        {
            return new IOException("The test control pipe is unavailable.");
        }

        try
        {
            using var deadline = new CancellationTokenSource(CommandTimeout);
            var command = await ReadByteAsync(control, deadline.Token).ConfigureAwait(false);
            switch (command)
            {
                case Cancel:
                    cancellation.Cancel();
                    break;
                case InjectUnexpected:
                    injectUnexpected();
                    break;
                default:
                    throw new InvalidDataException("Unknown lifecycle control command.");
            }

            await control.WriteAsync(
                new[] { ProtocolVersion, CommandApplied },
                deadline.Token).ConfigureAwait(false);
            await control.FlushAsync(deadline.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (IsControlException(exception))
        {
            return exception;
        }
    }

    private static async Task<Exception?> ReportPreTaskHangAsync(
        NamedPipeClientStream? control,
        ManualResetEventSlim reached)
    {
        if (control is null)
        {
            return new IOException("The test control pipe is unavailable.");
        }

        try
        {
            var reachedBeforeDeadline = await Task.Run(
                    () => reached.Wait(CommandTimeout))
                .ConfigureAwait(false);
            if (!reachedBeforeDeadline)
            {
                return new TimeoutException("The pre-task stage was not reached before its deadline.");
            }
            using var deadline = new CancellationTokenSource(CommandTimeout);
            await control.WriteAsync(
                new[] { ProtocolVersion, PreTaskHangReached },
                deadline.Token).ConfigureAwait(false);
            await control.FlushAsync(deadline.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (IsControlException(exception))
        {
            return exception;
        }
    }

    private static async Task<Exception?> WriteResultAndReadCommandAsync(
        NamedPipeClientStream control,
        byte kind,
        RepositoryLoadOutcome outcome,
        LoaderExecutionSnapshot snapshot,
        int loadInvocationCount,
        byte expectedCommand)
    {
        try
        {
            using var deadline = new CancellationTokenSource(
                expectedCommand == ReleaseSession
                    ? SessionReleaseTimeout
                    : ResultAcknowledgementTimeout);
            await WriteResultAsync(
                    control,
                    kind,
                    outcome,
                    snapshot,
                    loadInvocationCount,
                    deadline.Token)
                .ConfigureAwait(false);
            var command = await ReadByteAsync(control, deadline.Token).ConfigureAwait(false);
            return command == expectedCommand
                ? null
                : new InvalidDataException("Unexpected lifecycle result acknowledgement.");
        }
        catch (Exception exception) when (IsControlException(exception))
        {
            return exception;
        }
    }

    private static async Task<Exception?> WriteMarkerAsync(
        NamedPipeClientStream control,
        byte marker)
    {
        try
        {
            using var deadline = new CancellationTokenSource(ResultAcknowledgementTimeout);
            await control.WriteAsync(
                new[] { ProtocolVersion, marker },
                deadline.Token).ConfigureAwait(false);
            await control.FlushAsync(deadline.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (IsControlException(exception))
        {
            return exception;
        }
    }

    private static async Task WriteHelloAsync(
        NamedPipeClientStream control,
        Guid token,
        CancellationToken cancellationToken)
    {
        var payload = new byte[18];
        payload[0] = ProtocolVersion;
        payload[1] = Hello;
        token.TryWriteBytes(payload.AsSpan(2));
        await control.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await control.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteResultAsync(
        NamedPipeClientStream control,
        byte kind,
        RepositoryLoadOutcome outcome,
        LoaderExecutionSnapshot snapshot,
        int loadInvocationCount,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("ContractScribeLoaderInjectSerializationFailure"),
                "1",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Injected test-only result serialization failure.");
        }

        using var payload = new MemoryStream(capacity: 2048);
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ProtocolVersion);
            writer.Write(kind);
            writer.Write((byte)outcome.Status);
            writer.Write(checked((byte)loadInvocationCount));
            WriteBoundedString(writer, outcome.PrimaryFailure?.Code ?? string.Empty, 128);
            writer.Write((byte)snapshot.Phase);
            writer.Write(LifecycleBits(snapshot.Lifecycle));
            writer.Write((byte)snapshot.Exceptions.Count);
            foreach (var exception in snapshot.Exceptions)
            {
                writer.Write((byte)exception.Role);
                writer.Write((byte)exception.Boundary);
                writer.Write((byte)exception.Phase);
                writer.Write(exception.HResult);
                writer.Write(exception.NativeErrorCode ?? int.MinValue);
                writer.Write((byte)exception.TypeChain.Count);
                foreach (var type in exception.TypeChain)
                {
                    WriteBoundedString(
                        writer,
                        type,
                        LoaderExecutionTrace.MaximumTypeNameLength);
                }
            }
        }

        if (payload.Length > 4096)
        {
            throw new InvalidDataException("The bounded lifecycle result exceeded its frame limit.");
        }
        await control.WriteAsync(payload.GetBuffer().AsMemory(0, checked((int)payload.Length)), cancellationToken)
            .ConfigureAwait(false);
        await control.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var value = new byte[1];
        var read = await stream.ReadAsync(value, cancellationToken).ConfigureAwait(false);
        if (read != 1)
        {
            throw new EndOfStreamException("The lifecycle control pipe closed.");
        }
        return value[0];
    }

    private static void WriteBoundedString(BinaryWriter writer, string value, int maximumLength)
    {
        if (value.Length > maximumLength)
        {
            throw new InvalidDataException("A lifecycle result field exceeded its bound.");
        }
        writer.Write(value);
    }

    private static ushort LifecycleBits(LoaderLifecycleObservation lifecycle)
    {
        ushort bits = 0;
        SetBit(ref bits, 0, lifecycle.PathsResolved);
        SetBit(ref bits, 1, lifecycle.BaselineCaptured);
        SetBit(ref bits, 2, lifecycle.ToolchainSelected);
        SetBit(ref bits, 3, lifecycle.WorkspaceCreated);
        SetBit(ref bits, 4, lifecycle.WorkspaceOpenStarted);
        SetBit(ref bits, 5, lifecycle.WorkspaceOpenCompleted);
        SetBit(ref bits, 6, lifecycle.SessionConstructed);
        SetBit(ref bits, 7, lifecycle.SessionTransferred);
        SetBit(ref bits, 8, lifecycle.CleanupStarted);
        SetBit(ref bits, 9, lifecycle.CleanupCompleted);
        return bits;
    }

    private static void SetBit(ref ushort bits, int index, bool value)
    {
        if (value)
        {
            bits |= checked((ushort)(1 << index));
        }
    }

    private static RepositoryLoadStatus ExpectedStatus(LifecycleMode mode) => mode switch
    {
        LifecycleMode.Success or LifecycleMode.HeldSession => RepositoryLoadStatus.Success,
        LifecycleMode.Failure
            or LifecycleMode.Unexpected
            or LifecycleMode.PreTaskUnexpected
            or LifecycleMode.ExpectFailure => RepositoryLoadStatus.Failure,
        LifecycleMode.PreTaskHang => RepositoryLoadStatus.Cancelled,
        LifecycleMode.Cancellation => RepositoryLoadStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static bool TryMode(string value, out LifecycleMode mode)
    {
        mode = value switch
        {
            "lifecycle-success" => LifecycleMode.Success,
            "lifecycle-failure" => LifecycleMode.Failure,
            "lifecycle-cancellation" => LifecycleMode.Cancellation,
            "lifecycle-unexpected" => LifecycleMode.Unexpected,
            "lifecycle-pre-task-unexpected" => LifecycleMode.PreTaskUnexpected,
            "lifecycle-pre-task-hang" => LifecycleMode.PreTaskHang,
            "lifecycle-held-session" => LifecycleMode.HeldSession,
            "lifecycle-expect-failure" => LifecycleMode.ExpectFailure,
            _ => (LifecycleMode)(-1),
        };
        return Enum.IsDefined(mode);
    }

    private static bool IsControlException(Exception exception) => exception is
        IOException
        or InvalidDataException
        or TimeoutException
        or OperationCanceledException
        or UnauthorizedAccessException
        or InvalidOperationException
        or NotSupportedException;

    private enum LifecycleMode
    {
        Success,
        Failure,
        Cancellation,
        Unexpected,
        PreTaskUnexpected,
        PreTaskHang,
        HeldSession,
        ExpectFailure,
    }
}
