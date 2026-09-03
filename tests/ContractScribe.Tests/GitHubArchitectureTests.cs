using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using ContractScribe.Core;
using ContractScribe.GitHub.Transport;

namespace ContractScribe.Tests;

public sealed class GitHubArchitectureTests
{
    [Fact]
    public void Adapter_has_one_real_Core_edge_no_packages_no_reverse_edges_and_only_test_friend()
    {
        var root = Root();
        var project = XDocument.Load(Path.Join(root, "src/ContractScribe.GitHub/ContractScribe.GitHub.csproj"));
        Assert.Equal("../ContractScribe.Core/ContractScribe.Core.csproj",
            Assert.Single(project.Descendants("ProjectReference")).Attribute("Include")!.Value);
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Equal("ContractScribe.Tests", Assert.Single(project.Descendants("InternalsVisibleTo")).Attribute("Include")!.Value);
        var assembly = typeof(GitHubApiClient).Assembly;
        Assert.Equal("ContractScribe.Tests", Assert.Single(assembly.GetCustomAttributes<InternalsVisibleToAttribute>()).AssemblyName);
        Assert.Empty(assembly.GetExportedTypes());
        var referenced = assembly.GetReferencedAssemblies().Select(item => item.Name!).ToArray();
        Assert.Contains("ContractScribe.Core", referenced);
        Assert.All(referenced, name => Assert.True(name == "ContractScribe.Core" || name.StartsWith("System", StringComparison.Ordinal)));
        foreach (var name in new[] { "Core", "Roslyn", "Patching", "Agent", "Cli" })
        {
            var other = XDocument.Load(Path.Join(root, "src", "ContractScribe." + name, "ContractScribe." + name + ".csproj"));
            Assert.DoesNotContain(other.Descendants("ProjectReference"), item => item.Attribute("Include")!.Value.Contains("ContractScribe.GitHub", StringComparison.Ordinal));
        }
        var solution = XDocument.Load(Path.Join(root, "ContractScribe.slnx"));
        Assert.Single(solution.Descendants("Project"), item => item.Attribute("Path")!.Value.EndsWith("ContractScribe.GitHub.csproj", StringComparison.Ordinal));
        var tests = XDocument.Load(Path.Join(root, "tests/ContractScribe.Tests/ContractScribe.Tests.csproj"));
        Assert.Single(tests.Descendants("ProjectReference"), item => item.Attribute("Include")!.Value.Contains("ContractScribe.GitHub", StringComparison.Ordinal));
    }

    [Fact]
    public void Closed_factory_and_allowlist_exclude_generic_endpoint_handler_and_publication_capabilities()
    {
        var client = typeof(GitHubApiClient);
        Assert.All(client.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), ctor => Assert.True(ctor.IsPrivate));
        var factory = client.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(new[] { typeof(ValidatedGitHubPublicationAuthority), typeof(string) }, factory.GetParameters().Select(parameter => parameter.ParameterType));
        var operations = client.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.IsAssembly).Select(method => method.Name).Order(StringComparer.Ordinal);
        Assert.Equal(new[] { "CreateBlobAsync", "CreateCommitAsync", "CreatePullRequestAsync", "CreateTreeAsync",
            "GetAuthenticatedUserAsync", "GetBlobAsync", "GetCommitAsync", "GetPullRequestAsync", "GetRefAsync",
            "GetRepositoryAsync", "GetTreeAsync", "ListPullRequestsAsync", "UpdateRefAsync" }.Order(StringComparer.Ordinal), operations);
        Assert.DoesNotContain(client.GetInterfaces(), type => type.Namespace == typeof(ValidatedGitHubPublicationAuthority).Namespace);
        var hook = typeof(GitHubTransportTestHook).GetMethod("Register", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True(hook.IsPrivate);
        Assert.False(hook.DeclaringType!.IsPublic);
    }

    [Fact]
    public void Production_handler_cannot_redirect_downgrade_or_acquire_ambient_authority()
    {
        using var handler = GitHubApiClient.CreateProductionHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.ActivityHeadersPropagator);
        Assert.Equal(System.Net.DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(64, handler.MaxResponseHeadersLength);
        Assert.Equal(0, handler.MaxResponseDrainSize);
        Assert.Equal(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
        Assert.Equal("https://api.github.com/", GitHubApiClient.ProductionOrigin);
    }

    [Fact]
    public void Failure_and_recovery_types_are_closed_non_source_bearing_values()
    {
        var contextTypes = new[] { typeof(GitHubObjectContext), typeof(GitHubRefContext), typeof(GitHubPullRequestContext) };
        var properties = new Dictionary<Type, string[]>
        {
            [typeof(GitHubObjectContext)] = ["Repository", "OperationCommitment", "Kind", "ExpectedOid"],
            [typeof(GitHubRefContext)] = ["Repository", "OperationCommitment", "Ref", "BeforeOid", "AfterOid", "ClientMutationId"],
            [typeof(GitHubPullRequestContext)] = ["Repository", "OperationCommitment", "CreationCommitment", "HeadRef", "HeadOid",
                "BaseRef", "ExpectedBaseOid", "TitleSha256", "BodySha256"],
        };
        foreach (var type in contextTypes)
        {
            Assert.True(type.IsSealed);
            Assert.Equal(properties[type].Order(StringComparer.Ordinal), type.GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        }
        foreach (var type in contextTypes.Append(typeof(GitHubFailure)).Append(typeof(GitHubApiResult<GitHubBlob>)))
        {
            Assert.True(type.GetMethod("ToString", Type.EmptyTypes)!.IsFinal);
            Assert.DoesNotContain(type.GetProperties(), property => property.PropertyType == typeof(Exception)
                || property.PropertyType == typeof(HttpRequestMessage) || property.PropertyType == typeof(HttpResponseMessage)
                || property.PropertyType == typeof(Uri) || property.PropertyType == typeof(Stream));
        }
        Assert.Equal(new[] { "Code", "HttpStatus", "Retry" }, typeof(GitHubFailure).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Transport_sources_have_no_ambient_io_generic_send_surface_or_hook_registration_callsite()
    {
        var directory = Path.Join(Root(), "src/ContractScribe.GitHub/Transport");
        var sources = Directory.GetFiles(directory, "*.cs").Select(File.ReadAllText).ToArray();
        foreach (var forbidden in new[] { "Environment.", "File.", "Directory.", "Process.", "Console.", "ILogger", "IServiceProvider",
            "LoadIntoBuffer", "ReadAsStringAsync", "ReadAsByteArrayAsync", "HttpMethod.Put", "HttpMethod.Patch", "HttpMethod.Delete", "/issues", "Octokit" })
            Assert.DoesNotContain(sources, source => source.Contains(forbidden, StringComparison.Ordinal));
        foreach (var path in Directory.GetFiles(Path.Join(Root(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            Assert.DoesNotContain("GitHubTransportTestHook.Register(", File.ReadAllText(path), StringComparison.Ordinal);
        var docs = File.ReadAllText(Path.Join(Root(), "docs/20_architecture/project-structure.md"));
        Assert.Contains("`GitHub -> Core`", docs, StringComparison.Ordinal);
        Assert.Contains("no `Cli -> GitHub`", docs, StringComparison.Ordinal);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
