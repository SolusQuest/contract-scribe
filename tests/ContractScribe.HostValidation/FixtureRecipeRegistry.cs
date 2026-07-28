using System.Text;

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
            })
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
        var actual = CellExecutor.ComputeRepositoryIdentity(
            RepositoryObserver.Capture(repositoryRoot, ["obj"]));
        if (actual != ExpectedRepositoryIdentity(cellId, vector))
        {
            throw new ProtocolException("HV243_FIXTURE_RECIPE_DRIFT");
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

    private static byte[] Bytes(string value) => Utf8NoBom.GetBytes(value);

    private static bool IsProtected(string path)
    {
        var extension = Path.GetExtension(path);
        return extension is ".cs" or ".csproj" or ".fs" or ".fsproj" or ".vb" or ".vbproj" or ".props" or ".targets"
            or ".sln" or ".slnx" or ".slnf";
    }
}
