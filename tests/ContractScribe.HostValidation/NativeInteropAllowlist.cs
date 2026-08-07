using System.Reflection;
using System.Reflection.Metadata;

namespace ContractScribe.HostValidation;

public static class NativeInteropAllowlist
{
    private sealed record SourceBoundary(string Path, string Sha256);

    private sealed record NativeInteropRule(
        string Assembly,
        string Type,
        string ManagedMethod,
        string NativeLibrary,
        string EntryPoint,
        int Attributes,
        string Signature,
        string RuleId);

    private static readonly IReadOnlyList<SourceBoundary> Sources =
    [
        new(
            "src/ContractScribe.Roslyn/AtomicResultPublisher.cs",
            "e7d80c3b058de9250139cf88a9c5ae78ee6eb9627714bb1026f94275d91583b1"),
        new(
            "src/ContractScribe.Roslyn/ToolchainProcessMeter.cs",
            "44eb075bf18b5be7bda6fd23f4e5433050f77f8840e4d49df237a4db21d8f906"),
        new(
            "src/ContractScribe.Roslyn/DotnetSdkResolver.cs",
            "ec59985b097887973187e03792df231ed76247da40fa5aaff5f92bbdb78ee31a")
    ];

    private static readonly IReadOnlyList<NativeInteropRule> Rules =
    [
        Rule("StablePublicationDirectory", "CreateFileW", "kernel32.dll", "CreateFileW", 324, "00071280950e090918090918", "publication.windows.open-no-follow"),
        Rule("StablePublicationDirectory", "GetFileInformationByHandle", "kernel32.dll", "GetFileInformationByHandle", 320, "00020212809510118124", "publication.windows.file-identity"),
        Rule("StablePublicationDirectory", "GetFileType", "kernel32.dll", "GetFileType", 320, "000109128095", "publication.windows.file-kind"),
        Rule("StablePublicationDirectory", "SetFileInformationByHandle", "kernel32.dll", "SetFileInformationByHandle", 320, "000402128095081011812008", "publication.windows.delete-by-handle"),
        Rule("StablePublicationDirectory", "NtSetInformationFile", "ntdll.dll", "NtSetInformationFile", 256, "00050812809510118128180908", "publication.windows-delete-fallback"),
        Rule("StablePublicationDirectory", "RtlNtStatusToDosError", "ntdll.dll", "RtlNtStatusToDosError", 256, "00010908", "publication.windows-status"),
        Rule("StablePublicationDirectory", "Open", "libc", "open", 320, "0002080e08", "publication.unix.open-no-follow"),
        Rule("StablePublicationDirectory", "OpenAt", "libc", "openat", 320, "000408080e0809", "publication.unix.openat-no-follow"),
        Rule("StablePublicationDirectory", "FStat", "libc", "fstat", 320, "0002081280951011812c", "publication.unix.file-identity"),
        Rule("StablePublicationDirectory", "FStatAt", "libc", "fstatat", 320, "000408080e1011812c08", "publication.unix.file-identity-at"),
        Rule("StablePublicationDirectory", "UnlinkAt", "libc", "unlinkat", 320, "000308080e08", "publication.unix.unlink-at"),
        Rule("StablePublicationDirectory", "RenameAt2", "libc", "renameat2", 320, "000508080e080e09", "publication.unix.atomic-rename"),
        Rule("ToolchainProcessMeter", "NtQueryInformationProcess", "ntdll.dll", "NtQueryInformationProcess", 256, "00050812845d0810118314081008", "process.windows-parent-query-safe-handle"),
        Rule("ToolchainProcessMeter", "NtQueryInformationProcess", "ntdll.dll", "NtQueryInformationProcess", 256, "000508180818081008", "process.windows-command-line-query"),
        Rule("ToolchainProcessMeter", "CommandLineToArgvW", "shell32.dll", "CommandLineToArgvW", 324, "0002180e1008", "process.windows-command-line-parse"),
        Rule("ToolchainProcessMeter", "LocalFree", "kernel32.dll", "LocalFree", 256, "00011818", "process.windows-command-line-free")
    ];

    public static bool IsAllowedSource(string path, string sha256) =>
        Sources.Any(source => source.Path == path && source.Sha256 == sha256);

    public static bool IsAllowedMetadataInterop(
        string assembly,
        string type,
        string managedMethod,
        string nativeLibrary,
        string entryPoint,
        MethodImportAttributes attributes,
        string signature) =>
        Rules.Count(rule =>
            rule.Assembly == assembly
            && rule.Type == type
            && rule.ManagedMethod == managedMethod
            && rule.NativeLibrary == nativeLibrary
            && rule.EntryPoint == entryPoint
            && rule.Attributes == (int)attributes
            && rule.Signature == signature) == 1;

    public static bool IsAllowedMetadataIndirection(
        string assembly,
        string? typeNamespace,
        string? typeName,
        string memberName) =>
        assembly == "ContractScribe.Roslyn"
        && (typeNamespace, typeName, memberName) is
            ("System.Runtime.InteropServices", "NativeLibrary", "Load")
            or ("System.Runtime.InteropServices", "NativeLibrary", "GetExport")
            or ("System.Runtime.InteropServices", "Marshal", "GetDelegateForFunctionPointer");

    private static NativeInteropRule Rule(
        string type,
        string managedMethod,
        string nativeLibrary,
        string entryPoint,
        int attributes,
        string signature,
        string ruleId) =>
        new(
            "ContractScribe.Roslyn",
            $"ContractScribe.Roslyn.{type}",
            managedMethod,
            nativeLibrary,
            entryPoint,
            attributes,
            signature,
            ruleId);
}
