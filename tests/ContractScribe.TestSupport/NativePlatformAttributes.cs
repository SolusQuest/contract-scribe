using System.Runtime.InteropServices;
using Xunit;

namespace ContractScribe.TestSupport;

internal sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Requires the Linux filesystem or process interface.";
    }
}

internal sealed class NativeFileSystemFactAttribute : FactAttribute
{
    public NativeFileSystemFactAttribute()
    {
        if (!NativeFileSystemPlatform.IsSupported) Skip = "Requires Linux x64 or qualified local macOS arm64.";
    }
}

internal sealed class NativeFileSystemTheoryAttribute : TheoryAttribute
{
    public NativeFileSystemTheoryAttribute()
    {
        if (!NativeFileSystemPlatform.IsSupported) Skip = "Requires Linux x64 or qualified local macOS arm64.";
    }
}

internal static class NativeFileSystemPlatform
{
    internal static bool IsSupported =>
        OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64
        || OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
}
