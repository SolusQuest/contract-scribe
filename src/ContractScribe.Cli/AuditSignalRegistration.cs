using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ContractScribe.Cli;

internal sealed class AuditSignalRegistration : IDisposable
{
    private readonly CancellationTokenSource cancellation;
    private readonly ConsoleCancelEventHandler cancelHandler;
    private readonly ConsoleControlHandler? windowsHandler;
    private readonly PosixSignalRegistration? terminateRegistration;
    private int disposed;

    private AuditSignalRegistration(CancellationTokenSource cancellation)
    {
        this.cancellation = cancellation;
        cancelHandler = HandleCancel;
        if (OperatingSystem.IsWindows())
        {
            windowsHandler = controlType =>
            {
                if (controlType is not (0u or 1u))
                {
                    return false;
                }
                CancelOnce();
                return true;
            };
            if (!SetConsoleCtrlHandler(windowsHandler, add: true))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        else
        {
            Console.CancelKeyPress += cancelHandler;
            terminateRegistration = PosixSignalRegistration.Create(
                PosixSignal.SIGTERM,
                context =>
                {
                    context.Cancel = true;
                    CancelOnce();
                });
        }
    }

    public static AuditSignalRegistration Install(CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        return new AuditSignalRegistration(cancellation);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        if (windowsHandler is not null)
        {
            var removed = SetConsoleCtrlHandler(windowsHandler, add: false);
            Debug.Assert(removed, "The Windows console control handler was not removed.");
            GC.KeepAlive(windowsHandler);
        }
        else
        {
            Console.CancelKeyPress -= cancelHandler;
        }
        terminateRegistration?.Dispose();
    }

    private void HandleCancel(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        CancelOnce();
    }

    private void CancelOnce()
    {
        if (!cancellation.IsCancellationRequested)
        {
            cancellation.Cancel();
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ConsoleControlHandler(uint controlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(
        ConsoleControlHandler handler,
        [MarshalAs(UnmanagedType.Bool)] bool add);
}
