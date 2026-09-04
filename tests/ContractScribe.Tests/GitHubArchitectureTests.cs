using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using ContractScribe.Core;
using ContractScribe.GitHub.Coordination;
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
            .Where(method => method.IsAssembly && !method.IsSpecialName).Select(method => method.Name).Order(StringComparer.Ordinal);
        Assert.Equal(new[] { "CreateBlobAsync", "CreateCommitAsync", "CreatePullRequestAsync", "CreateTreeAsync",
            "GetAuthenticatedUserAsync", "GetBlobAsync", "GetCommitAsync", "GetPullRequestAsync", "GetRefAsync",
            "GetRepositoryAsync", "GetTreeAsync", "ListPullRequestsAsync", "UpdateRefAsync" }.Order(StringComparer.Ordinal), operations);
        Assert.DoesNotContain(client.GetInterfaces(), type => type.Namespace == typeof(ValidatedGitHubPublicationAuthority).Namespace);
        var authority = Assert.Single(client.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.Equal("Authority", authority.Name);
        Assert.Equal(typeof(ValidatedGitHubPublicationAuthority), authority.PropertyType);
        Assert.False(authority.CanWrite);
        var hook = typeof(GitHubTransportTestHook).GetMethod("Register", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True(hook.IsPrivate);
        Assert.False(hook.DeclaringType!.IsPublic);
    }

    [Fact]
    public void Coordination_authority_is_closed_unforgeable_and_not_publicly_exported()
    {
        var assembly = typeof(GitHubCoordinationStore).Assembly;
        Assert.Empty(assembly.GetExportedTypes());
        Assert.All(typeof(GitHubCoordinationStore).GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            constructor => Assert.True(constructor.IsPrivate));
        Assert.Equal(typeof(GitHubApiClient), Assert.Single(typeof(GitHubCoordinationStore)
            .GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters()).ParameterType);

        foreach (var capability in new[]
        {
            typeof(IGitHubCoordinationReadCapability),
            typeof(IGitHubCoordinationStateCapability),
            typeof(IGitHubCoordinationGuardCapability),
        })
        {
            Assert.True(capability.IsInterface);
            Assert.False(capability.IsPublic);
            Assert.Empty(capability.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        }

        var implementations = typeof(GitHubCoordinationStore).GetNestedTypes(BindingFlags.NonPublic)
            .Where(type => type.GetInterfaces().Any(capability => capability.Namespace == typeof(GitHubCoordinationStore).Namespace))
            .ToArray();
        Assert.Equal(3, implementations.Length);
        Assert.All(implementations, implementation =>
        {
            Assert.True(implementation.IsNestedPrivate);
            Assert.True(implementation.IsSealed);
            Assert.Empty(implementation.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        });
        var snapshotProperties = typeof(IGitHubCoordinationStateCapability)
            .GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var requiredSnapshotProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "Repository", "TargetRef", "TargetCommitOid", "SnapshotCommitmentSha256",
            "AuthorityCommitmentSha256", "PolicyCommitmentSha256", "OperationId",
            "OperationCommitmentSha256", "CurrentCandidateCommitmentSha256",
            "PrecedingOperationId", "PrecedingAuthorityCommitmentSha256",
            "PrecedingCandidateCommitmentSha256", "GenerationId", "Transition", "Stage",
            "HeadOid", "CoordinationPredecessorOid", "ContentCommitOid", "ProposalRefOid",
            "ProposalCommitOid", "ProposalTreeOid",
            "PullRequestCreationOperationCommitmentSha256", "PullRequestNumber",
            "ExpectedBaseOid", "ObservedBaseOid", "OwnershipMarkerSha256",
            "CumulativeDocumentationBlocks", "CumulativePatchBytes", "CumulativeChangedFiles",
        };
        Assert.Subset(requiredSnapshotProperties, snapshotProperties);
        var guardValidator = typeof(GitHubCoordinationStore).GetMethod(
            "ValidateGuard", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal(typeof(IGitHubCoordinationGuardCapability),
            Assert.Single(guardValidator.GetParameters()).ParameterType);
        Assert.True(GitHubCoordinationCodec.MaximumStateBytes < GitHubResponseReader.MaximumBlobBytes);
    }

    [Fact]
    public void Coordination_sources_have_one_GraphQL_ref_mutation_seam_and_no_issue_or_REST_ref_mutation()
    {
        var root = Root();
        var directory = Path.Join(root, "src/ContractScribe.GitHub/Coordination");
        var sources = Directory.GetFiles(directory, "*.cs").Select(File.ReadAllText).ToArray();
        foreach (var forbidden in new[]
        {
            "/issues", "HttpMethod.", "HttpRequestMessage", "Environment.", "File.",
            "Directory.", "Process.", "Console.", "ILogger", "IServiceProvider", "Octokit",
        })
            Assert.DoesNotContain(sources, source => source.Contains(forbidden, StringComparison.Ordinal));
        Assert.Equal(1, sources.Sum(source => Count(source, "client.UpdateRefAsync(")));
        Assert.Equal(0, sources.Sum(source => Count(source, "CreatePullRequestAsync(")));
        Assert.Equal(nameof(GitHubCoordinationFailure), new GitHubCoordinationFailure(
            GitHubCoordinationFailureKind.Transport, new(GitHubFailureCode.ResponseLost)).ToString());
        Assert.Equal(nameof(GitHubCoordinationResult), new GitHubCoordinationResult(
            GitHubCoordinationOutcome.Failed).ToString());
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

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0;
            index += value.Length)
            count++;
        return count;
    }
}
