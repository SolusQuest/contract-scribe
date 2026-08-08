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
                    return 0;
                }
                CancelOnce();
                return 1;
            };
            if (SetConsoleCtrlHandler(windowsHandler, add: 1) == 0)
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error());
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
            _ = SetConsoleCtrlHandler(windowsHandler, add: 0);
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
    private delegate int ConsoleControlHandler(uint controlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int SetConsoleCtrlHandler(
        ConsoleControlHandler handler,
        int add);

}
