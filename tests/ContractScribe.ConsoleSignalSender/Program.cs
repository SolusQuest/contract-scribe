using System.Runtime.InteropServices;
using System.Text;

if (args.Length >= 4 && args[0] == "broken-stdout")
{
    return OperatingSystem.IsWindows()
        ? RunBrokenStdoutWindows(args)
        : RunBrokenStdoutUnix(args);
}

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
        var started = Environment.TickCount64;
        while (!File.Exists(args[4]))
        {
            if (WaitForSingleObject(process.Process, 0) == 0)
            {
                Console.Error.WriteLine("CLI exited before the synchronization marker appeared.");
                return 7;
            }
            if (Environment.TickCount64 - started >= 60000)
            {
                _ = TerminateProcess(process.Process, 0xffffffff);
                Console.Error.WriteLine("The synchronization marker did not appear.");
                return 6;
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
        if (GenerateConsoleCtrlEvent(controlEvent, processGroup) == 0)
        {
            Console.Error.WriteLine(
                $"Second GenerateConsoleCtrlEvent failed: {Marshal.GetLastWin32Error()}");
            return 11;
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

static int RunBrokenStdoutWindows(string[] arguments)
{
    var security = new SecurityAttributes
    {
        Length = Marshal.SizeOf<SecurityAttributes>(),
        InheritHandle = 1,
    };
    if (CreatePipe(out var brokenRead, out var brokenWrite, ref security, 0) == 0)
    {
        Console.Error.WriteLine($"CreatePipe failed: {Marshal.GetLastWin32Error()}");
        return 20;
    }

    _ = CloseHandle(brokenRead);
    var input = CreateFile(
        "NUL",
        0x80000000,
        0x00000001 | 0x00000002,
        ref security,
        3,
        0x00000080,
        IntPtr.Zero);
    if (input == new IntPtr(-1))
    {
        _ = CloseHandle(brokenWrite);
        Console.Error.WriteLine($"Opening NUL failed: {Marshal.GetLastWin32Error()}");
        return 21;
    }

    var currentProcess = GetCurrentProcess();
    if (DuplicateHandle(
            currentProcess,
            GetStdHandle(-12),
            currentProcess,
            out var error,
            0,
            inheritHandle: true,
            0x00000002) == 0)
    {
        _ = CloseHandle(input);
        _ = CloseHandle(brokenWrite);
        Console.Error.WriteLine($"Duplicating stderr failed: {Marshal.GetLastWin32Error()}");
        return 22;
    }

    var commandLine = new StringBuilder();
    for (var index = 2; index < arguments.Length; index++)
    {
        if (index > 2)
        {
            commandLine.Append(' ');
        }
        AppendQuoted(commandLine, arguments[index]);
    }

    var startup = new StartupInfo
    {
        Size = Marshal.SizeOf<StartupInfo>(),
        Flags = 0x00000100,
        StandardInput = input,
        StandardOutput = brokenWrite,
        StandardError = error,
    };
    var created = CreateProcess(
        null,
        commandLine,
        IntPtr.Zero,
        IntPtr.Zero,
        inheritHandles: true,
        creationFlags: 0,
        IntPtr.Zero,
        arguments[1],
        ref startup,
        out var process);
    _ = CloseHandle(input);
    _ = CloseHandle(error);
    _ = CloseHandle(brokenWrite);
    if (created == 0)
    {
        Console.Error.WriteLine($"CreateProcess failed: {Marshal.GetLastWin32Error()}");
        return 23;
    }

    _ = CloseHandle(process.Thread);
    try
    {
        if (WaitForSingleObject(process.Process, 120000) != 0)
        {
            _ = TerminateProcess(process.Process, 0xffffffff);
            Console.Error.WriteLine("The broken-stdout child did not exit.");
            return 24;
        }
        if (GetExitCodeProcess(process.Process, out var exitCode) == 0)
        {
            Console.Error.WriteLine($"GetExitCodeProcess failed: {Marshal.GetLastWin32Error()}");
            return 25;
        }

        Console.Out.WriteLine($"exit:{unchecked((int)exitCode)}");
        return 0;
    }
    finally
    {
        _ = CloseHandle(process.Process);
    }
}

static int RunBrokenStdoutUnix(string[] arguments)
{
    var nativeArguments = new IntPtr[arguments.Length - 2];
    var argumentVector = IntPtr.Zero;
    var currentDirectory = IntPtr.Zero;
    try
    {
        for (var index = 0; index < nativeArguments.Length; index++)
        {
            nativeArguments[index] = Marshal.StringToCoTaskMemUTF8(arguments[index + 2]);
        }

        argumentVector = Marshal.AllocHGlobal((nativeArguments.Length + 1) * IntPtr.Size);
        for (var index = 0; index < nativeArguments.Length; index++)
        {
            Marshal.WriteIntPtr(argumentVector, index * IntPtr.Size, nativeArguments[index]);
        }
        Marshal.WriteIntPtr(argumentVector, nativeArguments.Length * IntPtr.Size, IntPtr.Zero);
        currentDirectory = Marshal.StringToCoTaskMemUTF8(arguments[1]);

        var pipe = new int[2];
        if (UnixPipe(pipe) != 0)
        {
            Console.Error.WriteLine($"pipe failed: {Marshal.GetLastWin32Error()}");
            return 30;
        }

        var child = UnixFork();
        if (child == 0)
        {
            _ = UnixClose(pipe[0]);
            if (UnixDuplicateTo(pipe[1], 1) < 0
                || UnixClose(pipe[1]) != 0
                || UnixChdir(currentDirectory) != 0)
            {
                UnixExit(127);
            }
            _ = UnixExecvp(nativeArguments[0], argumentVector);
            UnixExit(127);
        }
        if (child < 0)
        {
            _ = UnixClose(pipe[0]);
            _ = UnixClose(pipe[1]);
            Console.Error.WriteLine($"fork failed: {Marshal.GetLastWin32Error()}");
            return 31;
        }

        _ = UnixClose(pipe[0]);
        _ = UnixClose(pipe[1]);
        var started = Environment.TickCount64;
        while (true)
        {
            var waited = UnixWaitPid(child, out var status, 1);
            if (waited == child)
            {
                var signal = status & 0x7f;
                if (signal == 0)
                {
                    Console.Out.WriteLine($"exit:{(status >> 8) & 0xff}");
                }
                else
                {
                    Console.Out.WriteLine($"signal:{signal}");
                }
                return 0;
            }
            if (waited < 0)
            {
                var waitError = Marshal.GetLastWin32Error();
                if (waitError == 4)
                {
                    continue;
                }
                _ = UnixKill(child, 9);
                _ = UnixWaitPid(child, out _, 0);
                Console.Error.WriteLine($"waitpid failed: {waitError}");
                return 32;
            }
            if (Environment.TickCount64 - started >= 120000)
            {
                _ = UnixKill(child, 9);
                _ = UnixWaitPid(child, out _, 0);
                Console.Error.WriteLine("The broken-stdout child did not exit.");
                return 33;
            }

            Thread.Sleep(10);
        }
    }
    finally
    {
        foreach (var nativeArgument in nativeArguments)
        {
            Marshal.FreeCoTaskMem(nativeArgument);
        }
        if (argumentVector != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(argumentVector);
        }
        Marshal.FreeCoTaskMem(currentDirectory);
    }
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

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr GetCurrentProcess();

[DllImport("kernel32.dll", SetLastError = true)]
static extern int DuplicateHandle(
    IntPtr sourceProcess,
    IntPtr sourceHandle,
    IntPtr targetProcess,
    out IntPtr targetHandle,
    uint desiredAccess,
    bool inheritHandle,
    uint options);

[DllImport("kernel32.dll", SetLastError = true)]
static extern int CreatePipe(
    out IntPtr readPipe,
    out IntPtr writePipe,
    ref SecurityAttributes attributes,
    uint size);

[DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
static extern IntPtr CreateFile(
    string fileName,
    uint desiredAccess,
    uint shareMode,
    ref SecurityAttributes securityAttributes,
    uint creationDisposition,
    uint flagsAndAttributes,
    IntPtr templateFile);

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

[DllImport("libc", EntryPoint = "pipe", SetLastError = true)]
static extern int UnixPipe([Out] int[] descriptors);

[DllImport("libc", EntryPoint = "fork", SetLastError = true)]
static extern int UnixFork();

[DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
static extern int UnixDuplicateTo(int source, int destination);

[DllImport("libc", EntryPoint = "close", SetLastError = true)]
static extern int UnixClose(int descriptor);

[DllImport("libc", EntryPoint = "chdir", SetLastError = true)]
static extern int UnixChdir(IntPtr path);

[DllImport("libc", EntryPoint = "execvp", SetLastError = true)]
static extern int UnixExecvp(IntPtr file, IntPtr arguments);

[DllImport("libc", EntryPoint = "_exit")]
static extern void UnixExit(int status);

[DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
static extern int UnixWaitPid(int processId, out int status, int options);

[DllImport("libc", EntryPoint = "kill", SetLastError = true)]
static extern int UnixKill(int processId, int signal);

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

[StructLayout(LayoutKind.Sequential)]
struct SecurityAttributes
{
    public int Length;
    public IntPtr SecurityDescriptor;
    public int InheritHandle;
}
