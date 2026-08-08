using System.Runtime.InteropServices;
using System.Text;

if (!OperatingSystem.IsWindows()
    || args.Length < 5
    || args[0] != "harness"
    || args[1] is not ("ctrl-c" or "ctrl-break"))
{
    return 2;
}

var inheritedStandardInput = GetStdHandle(-10);
var inheritedStandardOutput = GetStdHandle(-11);
var inheritedStandardError = GetStdHandle(-12);
_ = FreeConsole();
if (AllocConsole() == 0)
{
    Console.Error.WriteLine($"AllocConsole failed: {Marshal.GetLastWin32Error()}");
    return 3;
}
var consoleWindow = GetConsoleWindow();
if (consoleWindow != IntPtr.Zero)
{
    _ = ShowWindow(consoleWindow, 0);
}

try
{
    ConsoleCtrlDelegate harnessHandler = _ => true;
    if (SetConsoleCtrlHandler(harnessHandler, add: true) == 0)
    {
        Console.Error.WriteLine(
            $"SetConsoleCtrlHandler failed: {Marshal.GetLastWin32Error()}");
        return 4;
    }

    var commandLine = new StringBuilder();
    AppendQuoted(commandLine, args[3]);
    for (var index = 5; index < args.Length; index++)
    {
        commandLine.Append(' ');
        AppendQuoted(commandLine, args[index]);
    }
    var startup = new StartupInfo
    {
        Size = Marshal.SizeOf<StartupInfo>(),
        Flags = 0x00000100,
        StandardInput = inheritedStandardInput,
        StandardOutput = inheritedStandardOutput,
        StandardError = inheritedStandardError,
    };
    var creationFlags = args[1] == "ctrl-break" ? 0x00000200u : 0u;
    if (CreateProcess(
            null,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            inheritHandles: true,
            creationFlags,
            IntPtr.Zero,
            args[2],
            ref startup,
            out var process) == 0)
    {
        Console.Error.WriteLine($"CreateProcess failed: {Marshal.GetLastWin32Error()}");
        return 5;
    }

    _ = CloseHandle(process.Thread);
    try
    {
        if (!int.TryParse(args[4], out var signalDelayMilliseconds)
            || signalDelayMilliseconds < 1)
        {
            return 6;
        }
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < signalDelayMilliseconds)
        {
            if (WaitForSingleObject(process.Process, 0) == 0)
            {
                Console.Error.WriteLine("CLI exited before the signal delay elapsed.");
                return 7;
            }
            Thread.Sleep(50);
        }

        var controlEvent = args[1] == "ctrl-c" ? 0u : 1u;
        var processGroup = controlEvent == 0 ? 0u : process.ProcessId;
        if (GenerateConsoleCtrlEvent(controlEvent, processGroup) == 0)
        {
            Console.Error.WriteLine(
                $"GenerateConsoleCtrlEvent failed: {Marshal.GetLastWin32Error()}");
            return 8;
        }
        if (WaitForSingleObject(process.Process, 30000) != 0)
        {
            _ = TerminateProcess(process.Process, 0xffffffff);
            Console.Error.WriteLine("CLI did not exit after the signal.");
            return 9;
        }
        if (GetExitCodeProcess(process.Process, out var exitCode) == 0)
        {
            Console.Error.WriteLine($"GetExitCodeProcess failed: {Marshal.GetLastWin32Error()}");
            return 10;
        }
        GC.KeepAlive(harnessHandler);
        return unchecked((int)exitCode);
    }
    finally
    {
        _ = CloseHandle(process.Process);
    }
}
finally
{
    _ = FreeConsole();
}

static void AppendQuoted(StringBuilder target, string value)
{
    target.Append('"');
    var backslashes = 0;
    foreach (var character in value)
    {
        if (character == '\\')
        {
            backslashes++;
            continue;
        }
        if (character == '"')
        {
            target.Append('\\', backslashes * 2 + 1);
            target.Append('"');
            backslashes = 0;
            continue;
        }
        target.Append('\\', backslashes);
        backslashes = 0;
        target.Append(character);
    }
    target.Append('\\', backslashes * 2);
    target.Append('"');
}

[DllImport("kernel32.dll", SetLastError = true)]
static extern int AllocConsole();

[DllImport("kernel32.dll", SetLastError = true)]
static extern int FreeConsole();

[DllImport("kernel32.dll")]
static extern IntPtr GetConsoleWindow();

[DllImport("user32.dll")]
static extern int ShowWindow(IntPtr window, int command);

[DllImport("kernel32.dll", SetLastError = true)]
static extern int SetConsoleCtrlHandler(ConsoleCtrlDelegate? handler, bool add);

[DllImport("kernel32.dll", SetLastError = true)]
static extern int GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr GetStdHandle(int standardHandle);

[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern int CreateProcess(
    string? applicationName,
    StringBuilder commandLine,
    IntPtr processAttributes,
    IntPtr threadAttributes,
    bool inheritHandles,
    uint creationFlags,
    IntPtr environment,
    string currentDirectory,
    ref StartupInfo startupInfo,
    out ProcessInformation processInformation);

[DllImport("kernel32.dll", SetLastError = true)]
static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

[DllImport("kernel32.dll", SetLastError = true)]
static extern int GetExitCodeProcess(IntPtr process, out uint exitCode);

[DllImport("kernel32.dll", SetLastError = true)]
static extern int TerminateProcess(IntPtr process, uint exitCode);

[DllImport("kernel32.dll", SetLastError = true)]
static extern int CloseHandle(IntPtr handle);

delegate bool ConsoleCtrlDelegate(uint controlType);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct StartupInfo
{
    public int Size;
    public string? Reserved;
    public string? Desktop;
    public string? Title;
    public int X;
    public int Y;
    public int XSize;
    public int YSize;
    public int XCountChars;
    public int YCountChars;
    public int FillAttribute;
    public uint Flags;
    public ushort ShowWindow;
    public ushort Reserved2;
    public IntPtr ReservedPointer;
    public IntPtr StandardInput;
    public IntPtr StandardOutput;
    public IntPtr StandardError;
}

[StructLayout(LayoutKind.Sequential)]
struct ProcessInformation
{
    public IntPtr Process;
    public IntPtr Thread;
    public uint ProcessId;
    public uint ThreadId;
}
