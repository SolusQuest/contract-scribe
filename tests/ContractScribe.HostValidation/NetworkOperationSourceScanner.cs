using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace ContractScribe.HostValidation;

public static partial class NetworkOperationSourceScanner
{
    public static bool HasContractScribeInitiatedNetworkOperation(
        string root,
        SubjectSourceConfiguration source,
        CellMaterialization? materialization)
    {
        foreach (var input in source.SourceAndBuildInputs)
        {
            if (!input.Path.StartsWith("src/", StringComparison.Ordinal)
                || !input.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = StripCommentsAndLiterals(
                File.ReadAllText(RepositoryPaths.ResolveConfined(root, input.Path)));
            if (ForbiddenNamespace().IsMatch(text)
                || ForbiddenType().IsMatch(text)
                || ForbiddenFactory().IsMatch(text))
            {
                return true;
            }
        }

        if (materialization is null)
        {
            return false;
        }
        var declared = materialization.BuiltArtifacts
            .Where(artifact => artifact.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                artifact => Path.GetFullPath(RepositoryPaths.ResolveConfined(root, artifact.Path)),
                artifact => artifact.Sha256,
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
        var closure = BuildManagedClosure(declared.Keys);
        if (closure.Any(path => !declared.ContainsKey(path)))
        {
            throw new ProtocolException("HV244_PRODUCTION_DEPENDENCY_CLOSURE");
        }
        return closure.Any(HasForbiddenMemberReference);
    }

    public static void ValidateSyntheticSource(string sourceText)
    {
        var executableSource = StripCommentsAndLiterals(sourceText);
        if (ForbiddenNamespace().IsMatch(executableSource)
            || ForbiddenType().IsMatch(executableSource)
            || ForbiddenFactory().IsMatch(executableSource))
        {
            throw new ProtocolException("HV232_NETWORK_OPERATION_SOURCE");
        }
    }

    [GeneratedRegex(
        @"(?m)^\s*(?:global\s+)?using\s+(?:[A-Za-z_][A-Za-z0-9_]*\s*=\s*)?System\.Net(?:\.|\s*;)")]
    private static partial Regex ForbiddenNamespace();

    [GeneratedRegex(
        @"\b(?:System\.Net\.(?:Http\.)?)?(?:HttpClient|HttpClientHandler|SocketsHttpHandler|WebRequest|HttpWebRequest|FtpWebRequest|Socket|TcpClient|UdpClient|Dns|QuicConnection|WebSocket)\b")]
    private static partial Regex ForbiddenType();

    [GeneratedRegex(
        @"\b(?:ConnectAsync|GetHostAddressesAsync|GetHostEntryAsync|SendAsync|GetAsync|PostAsync|OpenReadAsync|CreateConnection)\s*\(")]
    private static partial Regex ForbiddenFactory();

    private static bool HasForbiddenMemberReference(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            return false;
        }
        var metadata = peReader.GetMetadataReader();
        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            string? @namespace = member.Parent.Kind switch
            {
                HandleKind.TypeReference => metadata.GetString(
                    metadata.GetTypeReference((TypeReferenceHandle)member.Parent).Namespace),
                HandleKind.TypeDefinition => metadata.GetString(
                    metadata.GetTypeDefinition((TypeDefinitionHandle)member.Parent).Namespace),
                _ => null
            };
            if (@namespace?.StartsWith("System.Net", StringComparison.Ordinal) == true)
            {
                return true;
            }
        }
        return false;
    }

    private static IReadOnlySet<string> BuildManagedClosure(IEnumerable<string> roots)
    {
        var rootArray = roots.ToArray();
        var directories = rootArray.Select(Path.GetDirectoryName)
            .Where(path => path is not null)
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToArray();
        var candidates = directories
            .SelectMany(directory => Directory.EnumerateFiles(directory!, "*.dll", SearchOption.AllDirectories))
            .Select(Path.GetFullPath)
            .ToDictionary(
                GetAssemblySimpleName,
                path => path,
                StringComparer.OrdinalIgnoreCase);
        var closure = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        var pending = new Stack<string>(rootArray);
        while (pending.TryPop(out var path))
        {
            if (!closure.Add(path))
            {
                continue;
            }
            foreach (var reference in GetAssemblyReferences(path))
            {
                if (candidates.TryGetValue(reference, out var dependency))
                {
                    pending.Push(dependency);
                }
            }
        }
        return closure;
    }

    private static string GetAssemblySimpleName(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            return Path.GetFileNameWithoutExtension(assemblyPath);
        }
        var metadata = peReader.GetMetadataReader();
        return metadata.IsAssembly
            ? metadata.GetString(metadata.GetAssemblyDefinition().Name)
            : Path.GetFileNameWithoutExtension(assemblyPath);
    }

    private static IEnumerable<string> GetAssemblyReferences(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            return [];
        }
        var metadata = peReader.GetMetadataReader();
        return metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToArray();
    }

    private static string StripCommentsAndLiterals(string source)
    {
        var builder = new StringBuilder(source.Length);
        var state = 0;
        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (state == 0 && current == '/' && next == '/')
            {
                state = 1;
                builder.Append("  ");
                index++;
            }
            else if (state == 0 && current == '/' && next == '*')
            {
                state = 2;
                builder.Append("  ");
                index++;
            }
            else if (state == 0 && current is '"' or '\'')
            {
                state = current == '"' ? 3 : 4;
                builder.Append(' ');
            }
            else if (state == 1 && current is '\r' or '\n'
                || state == 2 && current == '*' && next == '/')
            {
                state = 0;
                builder.Append(current);
                if (current == '*')
                {
                    builder.Append(' ');
                    index++;
                }
            }
            else if (state is 3 or 4 && current == '\\')
            {
                builder.Append("  ");
                index++;
            }
            else if (state == 3 && current == '"' || state == 4 && current == '\'')
            {
                state = 0;
                builder.Append(' ');
            }
            else
            {
                builder.Append(state == 0 ? current : current is '\r' or '\n' ? current : ' ');
            }
        }
        return builder.ToString();
    }
}
