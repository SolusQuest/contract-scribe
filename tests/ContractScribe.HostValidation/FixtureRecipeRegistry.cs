using System.Text;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace ContractScribe.HostValidation;

public static class FixtureRecipeRegistry
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static IReadOnlyDictionary<string, byte[]> Files(
        string cellId,
        VectorDefinition vector)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [".contractscribe-fixture.json"] = CanonicalJson.SerializeCanonical(new
            {
                formatVersion = "contractscribe-m1-host-validation-fixture-recipe-v1",
                cellId,
                vectorId = vector.VectorId,
                vector.Fixture,
                vector.ExpectedObservation,
                vector.ExpectedEnforcementClass,
                vector.SupportDisposition
            }),
            [".contractscribe-fixture-platform.json"] = CanonicalJson.SerializeCanonical(
                PlatformRecipe(cellId, vector))
        };

        if (vector.ExecutorKind == "production-host")
        {
            AddProductionInput(files, vector);
        }
        else
        {
            AddProcessArrangement(files, vector);
        }
        return files;
    }

    public static string ExpectedRepositoryIdentity(
        string cellId,
        VectorDefinition vector)
    {
        var protectedFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var otherFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, bytes) in Files(cellId, vector))
        {
            var target = IsProtected(path) ? protectedFiles : otherFiles;
            target.Add(path, CanonicalJson.Sha256(bytes));
        }
        var reparse = CellMatchesCurrentPlatform(cellId)
            ? ReparseRecipe(cellId, vector)
            : null;
        if (reparse is not null)
        {
            otherFiles.Add(
                reparse.Value.Path,
                CanonicalJson.Sha256(Encoding.UTF8.GetBytes(
                    $"reparse\0{reparse.Value.Target}")));
        }
        return CellExecutor.ComputeRepositoryIdentity(
            new RepositorySnapshot(
                protectedFiles,
                otherFiles,
                new SortedDictionary<string, string>(StringComparer.Ordinal)));
    }

    public static void Provision(
        string repositoryRoot,
        string cellId,
        VectorDefinition vector)
    {
        Directory.CreateDirectory(repositoryRoot);
        if (vector.VectorId is
            "failure.publication-invalidation" or "failure.publication-finalization")
        {
            CellExecutor.ResetPublicationDirectoryForProvisioning(repositoryRoot);
        }
        foreach (var (relative, bytes) in Files(cellId, vector))
        {
            var path = Path.GetFullPath(Path.Join(repositoryRoot, relative));
            if (!path.StartsWith(
                    Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar)
                        + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                throw new ProtocolException("HV242_FIXTURE_RECIPE_PATH");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        ApplyAndValidatePlatformProperties(repositoryRoot, cellId, vector);
        var actual = CellExecutor.ComputeRepositoryIdentity(
            RepositoryObserver.Capture(repositoryRoot, ["obj"]));
        if (actual != ExpectedRepositoryIdentity(cellId, vector))
        {
            throw new ProtocolException("HV243_FIXTURE_RECIPE_DRIFT");
        }
    }

    public static void RemoveProvisionedReparsePoints(string repositoryRoot)
    {
        var fullRoot = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(fullRoot))
        {
            return;
        }
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        Directory.Delete(path, recursive: false);
                    }
                    else
                    {
                        File.Delete(path);
                    }
                }
                else if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                }
            }
        }
    }

    private static void AddProductionInput(
        IDictionary<string, byte[]> files,
        VectorDefinition vector)
    {
        var targetFrameworkProperty = vector.VectorId == "support.multi-targeting"
            ? "<TargetFrameworks>net8.0;net9.0</TargetFrameworks>"
            : "<TargetFramework>net8.0</TargetFramework>";
        var projectExtension = vector.VectorId == "support.non-csharp-project"
            ? "fsproj"
            : "csproj";
        files[$"Fixture.{projectExtension}"] = Bytes(
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{targetFrameworkProperty}<RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources></PropertyGroup></Project>\n");
        files[projectExtension == "csproj" ? "Fixture.cs" : "Fixture.fs"] = Bytes(
            projectExtension == "csproj"
                ? "namespace ContractScribe.ValidationFixture; public sealed class FixtureType { public void Undocumented(int value) { } }\n"
                : "namespace ContractScribe.ValidationFixture\ntype FixtureType() = class end\n");

        switch (vector.VectorId)
        {
            case "support.sln":
                files["Fixture.sln"] = Bytes(
                    "Microsoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 17\nProject(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Fixture\", \"Fixture.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\nEndProject\nGlobal\nEndGlobal\n");
                break;
            case "support.slnx":
                files["Fixture.slnx"] = Bytes(
                    "<Solution><Project Path=\"Fixture.csproj\" /></Solution>\n");
                break;
            case "support.slnf":
                files["Fixture.sln"] = Bytes(
                    "Microsoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 17\nGlobal\nEndGlobal\n");
                files["Fixture.slnf"] = Bytes(
                    "{\"solution\":{\"path\":\"Fixture.sln\",\"projects\":[\"Fixture.csproj\"]}}\n");
                break;
            case "support.custom-target":
                files["Directory.Build.targets"] = Bytes(
                    "<Project><Target Name=\"ContractScribeValidationMarker\" BeforeTargets=\"Compile\"><WriteLinesToFile File=\"$(BaseIntermediateOutputPath)custom-target.marker\" Lines=\"executed\" Overwrite=\"true\" /></Target></Project>\n");
                break;
            case "support.analyzer":
                files["analyzer.fixture.json"] = Bytes(
                    "{\"behavior\":\"trusted-analyzer-load\",\"version\":1}\n");
                break;
            case "support.generator":
                files["generator.fixture.json"] = Bytes(
                    "{\"behavior\":\"trusted-generator-load\",\"version\":1}\n");
                break;
        }
    }

    private static void AddProcessArrangement(
        IDictionary<string, byte[]> files,
        VectorDefinition vector)
    {
        var command = FrozenExecutorCommandRegistry.Get(vector.VectorId);
        foreach (var path in command.ArrangementPaths)
        {
            files[path] = path.EndsWith("invalid-entrypoint.dll", StringComparison.Ordinal)
                ? Bytes("not-a-managed-assembly\n")
                : path.EndsWith("denied-entrypoint", StringComparison.Ordinal)
                    ? Bytes("#!/bin/sh\nexit 0\n")
                    : CanonicalJson.SerializeCanonical(new
                    {
                        formatVersion = "contractscribe-m1-host-validation-arrangement-v1",
                        vectorId = vector.VectorId,
                        vector.Fixture
                    });
        }
    }

    private static object PlatformRecipe(string cellId, VectorDefinition vector)
    {
        var reparse = ReparseRecipe(cellId, vector);
        return new
        {
            formatVersion = "contractscribe-m1-host-validation-platform-recipe-v1",
            cellId,
            vectorId = vector.VectorId,
            permission = vector.VectorId == "failure.permission-before-entry"
                ? new
                {
                    path = ".contractscribe-validation/process.permission-denied/denied-entrypoint",
                    unixMode = cellId == "ubuntu-x64" ? "user-read-write" : null,
                    windowsAcl = cellId == "windows-x64" ? "deny-execute-everyone" : null,
                    expectedLaunchDisposition = "permission-failure"
                }
                : null,
            reparse = reparse is null
                ? null
                : new
                {
                    reparse.Value.Path,
                    reparse.Value.Target,
                    reparse.Value.Kind
                },
            volumeTopology = vector.VectorId switch
            {
                "publication.same-directory-atomic" =>
                    "staging-and-destination-same-directory",
                "publication.cross-volume-rejected" =>
                    "staging-and-destination-distinct-volume-required",
                _ => "not-applicable"
            }
        };
    }

    private static (string Path, string Target, string Kind)? ReparseRecipe(
        string cellId,
        VectorDefinition vector)
    {
        return (cellId, vector.VectorId) switch
        {
            ("ubuntu-x64", "path.symlink-escape") => (
                ".contractscribe-validation/path.unix-symlink/escape-link",
                "../../../../outside-unix-fixture",
                "symbolic-link"),
            ("windows-x64", "path.junction-reparse-escape") => (
                ".contractscribe-validation/path.windows-junction-reparse/escape-link",
                "..",
                "directory-reparse-link"),
            _ => null
        };
    }

    private static void ApplyAndValidatePlatformProperties(
        string repositoryRoot,
        string cellId,
        VectorDefinition vector)
    {
        if (!CellMatchesCurrentPlatform(cellId))
        {
            return;
        }
        if (vector.VectorId == "failure.permission-before-entry")
        {
            var path = Path.Join(
                repositoryRoot,
                ".contractscribe-validation",
                "process.permission-denied",
                "denied-entrypoint");
            if (OperatingSystem.IsLinux() && cellId == "ubuntu-x64")
            {
                ApplyUnixExecuteDeny(path);
            }
            else if (cellId == "windows-x64")
            {
                ApplyWindowsExecuteDeny(path);
            }
            ValidatePermissionDenied(path);
        }

        var reparse = ReparseRecipe(cellId, vector);
        if (reparse is not null)
        {
            var linkPath = Path.Join(
                repositoryRoot,
                reparse.Value.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            if (reparse.Value.Kind == "directory-reparse-link")
            {
                CreateWindowsJunction(
                    linkPath,
                    Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(repositoryRoot))!);
            }
            else
            {
                _ = File.CreateSymbolicLink(linkPath, reparse.Value.Target);
            }
            var info = reparse.Value.Kind == "directory-reparse-link"
                ? (FileSystemInfo)new DirectoryInfo(linkPath)
                : new FileInfo(linkPath);
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0
                || NormalizeReparseTarget(repositoryRoot, info)
                    != reparse.Value.Target)
            {
                throw new ProtocolException("HV246_FIXTURE_PLATFORM_DRIFT");
            }
        }
        ValidateVolumeTopology(repositoryRoot, vector);
    }

    private static void ValidateVolumeTopology(
        string repositoryRoot,
        VectorDefinition vector)
    {
        if (vector.VectorId == "publication.same-directory-atomic")
        {
            var topologyRoot = Path.Join(
                repositoryRoot,
                ".contractscribe-validation",
                "topology-probe");
            var source = Path.Join(topologyRoot, "same-volume-source");
            var destination = Path.Join(topologyRoot, "same-volume-destination");
            Directory.CreateDirectory(source);
            Directory.Move(source, destination);
            if (!Directory.Exists(destination) || Directory.Exists(source))
            {
                throw new ProtocolException("HV246_FIXTURE_PLATFORM_DRIFT");
            }
            Directory.Delete(destination);
            return;
        }
        if (vector.VectorId != "publication.cross-volume-rejected")
        {
            return;
        }

        var sourceRoot = Path.Join(
            repositoryRoot,
            ".contractscribe-validation",
            $"cross-volume-source-{Guid.NewGuid():N}");
        var destinationParent = CreateCrossVolumeProbeRoot(repositoryRoot);
        var destinationRoot = Path.Join(destinationParent, "moved");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            try
            {
                Directory.Move(sourceRoot, destinationRoot);
            }
            catch (IOException)
            {
                return;
            }

            if (Directory.Exists(destinationRoot))
            {
                Directory.Move(destinationRoot, sourceRoot);
            }
            throw new ProtocolException("HV246_FIXTURE_PLATFORM_DRIFT");
        }
        finally
        {
            if (Directory.Exists(sourceRoot))
            {
                Directory.Delete(sourceRoot);
            }
            if (Directory.Exists(destinationRoot))
            {
                Directory.Delete(destinationRoot);
            }
            if (Directory.Exists(destinationParent))
            {
                Directory.Delete(destinationParent);
            }
        }
    }

    private static string CreateCrossVolumeProbeRoot(string repositoryRoot)
    {
        var repositoryVolume = Path.GetPathRoot(Path.GetFullPath(repositoryRoot));
        var candidates = new List<string>();
        if (OperatingSystem.IsLinux() && Directory.Exists("/dev/shm"))
        {
            candidates.Add("/dev/shm");
        }
        candidates.Add(Path.GetTempPath());
        candidates.AddRange(
            DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName));
        foreach (var candidate in candidates
                     .Distinct(OperatingSystem.IsWindows()
                         ? StringComparer.OrdinalIgnoreCase
                         : StringComparer.Ordinal))
        {
            if (OperatingSystem.IsWindows()
                && string.Equals(
                    Path.GetPathRoot(Path.GetFullPath(candidate)),
                    repositoryVolume,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var root = Path.Join(
                candidate,
                $"contractscribe-hv-cross-volume-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(root);
                return root;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Try the next ready candidate; no path is emitted.
            }
        }
        throw new ProtocolException("HV246_FIXTURE_PLATFORM_DRIFT");
    }

    private static void CreateWindowsJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = Process.Start(startInfo)
            ?? throw new ProtocolException("HV246_FIXTURE_PLATFORM_DRIFT");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || output.Length + error.Length > 16 * 1024)
        {
            throw new ProtocolException("HV246_FIXTURE_PLATFORM_DRIFT");
        }
    }

    private static string NormalizeReparseTarget(
        string repositoryRoot,
        FileSystemInfo info)
    {
        var resolved = info.ResolveLinkTarget(returnFinalTarget: false);
        if (resolved is not null && resolved.Exists)
        {
            return Path.GetRelativePath(
                    Path.GetFullPath(repositoryRoot),
                    resolved.FullName)
                .Replace(Path.DirectorySeparatorChar, '/');
        }
        return info.LinkTarget ?? "unresolved";
    }

    private static void ApplyWindowsExecuteDeny(string path)
    {
        var startInfo = new ProcessStartInfo("icacls")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add("/inheritance:d");
        startInfo.ArgumentList.Add("/deny");
        startInfo.ArgumentList.Add("*S-1-1-0:(X)");
        using var process = Process.Start(startInfo)
            ?? throw new ProtocolException("HV246_FIXTURE_PLATFORM_DRIFT");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || output.Length + error.Length > 16 * 1024)
        {
            throw new ProtocolException("HV246_FIXTURE_PLATFORM_DRIFT");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ApplyUnixExecuteDeny(string path)
    {
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (File.GetUnixFileMode(path)
            != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
        {
            throw new ProtocolException("HV246_FIXTURE_PLATFORM_DRIFT");
        }
    }

    private static bool CellMatchesCurrentPlatform(string cellId) =>
        OperatingSystem.IsWindows()
            ? cellId == "windows-x64"
            : OperatingSystem.IsLinux() && cellId == "ubuntu-x64";

    private static void ValidatePermissionDenied(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            process?.Kill(entireProcessTree: true);
            throw new ProtocolException("HV246_FIXTURE_PLATFORM_DRIFT");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 5 or 13)
        {
            // The platform readback is the required pre-entry permission failure.
        }
        catch (UnauthorizedAccessException)
        {
            // The platform readback is the required pre-entry permission failure.
        }
    }

    private static byte[] Bytes(string value) => Utf8NoBom.GetBytes(value);

    private static bool IsProtected(string path)
    {
        var extension = Path.GetExtension(path);
        return extension is ".cs" or ".csproj" or ".fs" or ".fsproj" or ".vb" or ".vbproj" or ".props" or ".targets"
            or ".sln" or ".slnx" or ".slnf";
    }
}
