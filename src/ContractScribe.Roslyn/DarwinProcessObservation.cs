using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ContractScribe.Roslyn;

/// <summary>Public Darwin metadata for the local process observer and its owned descendants.</summary>
internal static class DarwinProcessObservation
{
    internal readonly record struct Identity(int ProcessId, int ParentProcessId, long StartIdentity);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static Identity ReadIdentity(int processId)
    {
        if (!DarwinFileSystem.IsSupported || processId <= 0) throw new IOException("Unsupported process observation.");
        var bytes = new byte[136];
        if (ProcessInfo(processId, 3, 0, bytes, bytes.Length) != bytes.Length
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)) != (uint)processId)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var parent = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
        var seconds = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(120));
        var microseconds = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(128));
        if ((BinaryPrimitives.ReadUInt32LittleEndian(bytes) & 0x10) == 0
            || parent > int.MaxValue || seconds == 0 || microseconds >= 1_000_000)
            throw new IOException("The native process identity is unavailable.");
        var start = checked(DateTime.UnixEpoch.Ticks + checked((long)seconds * TimeSpan.TicksPerSecond)
            + checked((long)microseconds * 10));
        return new(processId, (int)parent, start);
    }

    internal static IReadOnlyList<string> ReadOwnedArguments(int processId, long expectedStart, int ownedRootProcessId)
    {
        RequireOwnedIdentity(processId, expectedStart, ownedRootProcessId);
        int[] query = [1, 49, processId]; // CTL_KERN / KERN_PROCARGS2
        nuint capacity = 0;
        if (Sysctl(query, 3, null, ref capacity, nint.Zero, 0) != 0 || capacity is < 8 or > 1024 * 1024)
            throw new IOException("The native process arguments are unavailable.");
        var bytes = new byte[checked((int)capacity)];
        try
        {
            var length = capacity;
            if (Sysctl(query, 3, bytes, ref length, nint.Zero, 0) != 0 || length > capacity || length < 8)
                throw new IOException("The native process arguments changed during capture.");
            RequireOwnedIdentity(processId, expectedStart, ownedRootProcessId);
            var argc = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            if (argc is < 0 or > 65536) throw new IOException("Invalid native argument count.");
            var end = checked((int)length);
            var at = 4;
            while (at < end && bytes[at] != 0) at++; // executable path, not argv
            if (at == end) throw new IOException("Invalid native executable path.");
            // XNU aligns the saved executable path to the target pointer size;
            // KERN_PROCARGS2 strips a 16-byte prefix and prepends the 4-byte argc.
            // Skip exactly that padding. Scanning all zeros loses empty argv[0]
            // and can then consume an environment string as an extra argument.
            var argvStart = checked(4 + ((at - 4 + 1 + 7) & ~7));
            if (argvStart > end || bytes.AsSpan(at + 1, argvStart - at - 1).IndexOfAnyExcept((byte)0) >= 0)
                throw new IOException("Invalid native argument alignment.");
            at = argvStart;
            var arguments = new string[argc];
            for (var index = 0; index < argc; index++)
            {
                if (at >= end) throw new IOException("Incomplete native argument vector.");
                var terminator = Array.IndexOf(bytes, (byte)0, at, end - at);
                if (terminator < 0) throw new IOException("Unterminated native argument.");
                arguments[index] = StrictUtf8.GetString(bytes, at, terminator - at);
                at = terminator + 1;
            }
            // KERN_PROCARGS2 also contains the environment. Never parse or retain
            // that tail, and erase the entire allocation, not only returned length.
            RequireOwnedIdentity(processId, expectedStart, ownedRootProcessId);
            return arguments;
        }
        catch (DecoderFallbackException exception) { throw new IOException("Invalid native argument encoding.", exception); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void RequireOwnedIdentity(int processId, long expectedStart, int rootProcessId)
    {
        var identity = ReadIdentity(processId);
        if (identity.StartIdentity != expectedStart || processId == rootProcessId)
            throw new IOException("The owned process identity changed.");
        var visited = new HashSet<int>();
        var current = identity;
        while (visited.Count < 256 && visited.Add(current.ProcessId))
        {
            if (current.ParentProcessId == rootProcessId)
            {
                if (ReadIdentity(processId) != identity) break;
                return;
            }
            if (current.ParentProcessId <= 1) break;
            current = ReadIdentity(current.ParentProcessId);
        }
        throw new IOException("Native argument capture requires a verified owned descendant.");
    }

    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pidinfo", SetLastError = true)]
    private static extern int ProcessInfo(int processId, int flavor, ulong argument, [Out] byte[] buffer, int size);

    [DllImport("libc", EntryPoint = "sysctl", SetLastError = true)]
    private static extern int Sysctl([In] int[] query, uint count, [Out] byte[]? value, ref nuint length,
        nint newValue, nuint newLength);
}
