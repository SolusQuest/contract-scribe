using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
                || ForbiddenFactory().IsMatch(text)
                || ForbiddenIndirection().IsMatch(text))
            {
                return true;
            }
        }

        if (materialization is null)
        {
            return false;
        }
        var declared = materialization.ProductionArtifacts
            .Concat(materialization.RuntimeDependencies)
            .Where(artifact => artifact.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(artifact => Path.GetFullPath(
                RepositoryPaths.ResolveConfined(root, artifact.Path)))
            .ToArray();
        _ = BuildManagedClosure(declared, materialization.SelectedRuntime);
        return materialization.ProductionArtifacts
            .Where(artifact => artifact.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(artifact => RepositoryPaths.ResolveConfined(root, artifact.Path))
            .Any(HasForbiddenMemberReference);
    }

    public static string SelectedRuntimeManifestIdentity(string selectedRuntime)
    {
        try
        {
            var runtimeAssemblies = TrustedPlatformAssemblies(selectedRuntime);
            return $"runtime.{CanonicalJson.Sha256(CanonicalJson.SerializeCanonical(
                runtimeAssemblies
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new
                    {
                        Identity = pair.Key,
                        FileName = Path.GetFileName(pair.Value),
                        Sha256 = CanonicalJson.Sha256File(pair.Value)
                    })
                    .ToArray()))}";
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or BadImageFormatException
                or ProtocolException
            {
                Code: "HV249_SELECTED_RUNTIME_MANIFEST"
            })
        {
            throw new ProtocolException("HV249_SELECTED_RUNTIME_MANIFEST");
        }
    }

    public static string SelectedRuntimeManifestInputIdentity(
        string selectedRuntime)
    {
        try
        {
            return SelectedRuntimeManifestIdentity(selectedRuntime);
        }
        catch (ProtocolException exception) when (
            exception.Code == "HV249_SELECTED_RUNTIME_MANIFEST")
        {
            return $"runtime-incomplete.{CanonicalJson.Sha256(
                Encoding.UTF8.GetBytes(selectedRuntime))}";
        }
    }

    public static void ValidateSyntheticSource(string sourceText)
    {
        var executableSource = StripCommentsAndLiterals(sourceText);
        if (ForbiddenNamespace().IsMatch(executableSource)
            || ForbiddenType().IsMatch(executableSource)
            || ForbiddenFactory().IsMatch(executableSource)
            || ForbiddenIndirection().IsMatch(executableSource))
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

    [GeneratedRegex(
        @"(?ix)
        \bType\s*\.\s*GetType\s*\(
        |\bActivator\s*\.\s*CreateInstance\s*\(
        |\.\s*GetMethods?\s*\(
        |\.\s*GetConstructors?\s*\(
        |\.\s*GetType\s*\(
        |\.\s*CreateDelegate\s*\(
        |\.\s*Compile\s*\(
        |\.\s*Invoke\s*\(
        |\bAssembly\s*\.\s*Load(?:From|File)?\s*\(
        |\bAssemblyLoadContext\b
        |\bNativeLibrary\b
        |\b(?:DllImport|LibraryImport)\s*\(
        |\bMarshal\s*\.\s*GetDelegateForFunctionPointer\s*\(
        |\bdelegate\s*\*\s*unmanaged\b
        |\bdynamic\b")]
    private static partial Regex ForbiddenIndirection();

    private static bool HasForbiddenMemberReference(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            throw new BadImageFormatException(
                "A declared managed artifact does not contain metadata.");
        }
        var metadata = peReader.GetMetadataReader();
        foreach (var handle in metadata.MethodDefinitions)
        {
            if ((metadata.GetMethodDefinition(handle).Attributes
                    & System.Reflection.MethodAttributes.PinvokeImpl) != 0)
            {
                return true;
            }
        }
        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            var (typeNamespace, typeName) = member.Parent.Kind switch
            {
                HandleKind.TypeReference => GetTypeIdentity(
                    metadata,
                    (TypeReferenceHandle)member.Parent),
                HandleKind.TypeDefinition => GetTypeIdentity(
                    metadata,
                    (TypeDefinitionHandle)member.Parent),
                _ => (null, null)
            };
            var memberName = metadata.GetString(member.Name);
            if (typeNamespace?.StartsWith("System.Net", StringComparison.Ordinal) == true
                || IsForbiddenIndirection(typeNamespace, typeName, memberName))
            {
                return true;
            }
        }
        foreach (var handle in metadata.TypeReferences)
        {
            var (typeNamespace, typeName) = GetTypeIdentity(metadata, handle);
            if (typeNamespace is "System.Runtime.Loader" or "Microsoft.CSharp.RuntimeBinder"
                || typeNamespace == "System.Runtime.InteropServices"
                    && typeName is "NativeLibrary")
            {
                return true;
            }
        }
        return false;
    }

    private static (string? Namespace, string? Name) GetTypeIdentity(
        MetadataReader metadata,
        TypeReferenceHandle handle)
    {
        var type = metadata.GetTypeReference(handle);
        return (metadata.GetString(type.Namespace), metadata.GetString(type.Name));
    }

    private static (string? Namespace, string? Name) GetTypeIdentity(
        MetadataReader metadata,
        TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        return (metadata.GetString(type.Namespace), metadata.GetString(type.Name));
    }

    private static bool IsForbiddenIndirection(
        string? typeNamespace,
        string? typeName,
        string memberName)
    {
        return typeNamespace == "System"
                && typeName == "Type"
                && memberName is "GetType" or "GetMethod" or "GetMethods"
                    or "GetConstructor" or "GetConstructors"
            || typeNamespace == "System"
                && typeName == "Activator"
                && memberName == "CreateInstance"
            || typeNamespace == "System"
                && typeName == "Delegate"
                && memberName == "CreateDelegate"
            || typeNamespace == "System.Reflection"
                && typeName == "Assembly"
                && (memberName.StartsWith("Load", StringComparison.Ordinal)
                    || memberName == "GetType")
            || typeNamespace == "System.Reflection"
                && typeName is "MethodBase" or "MethodInfo"
                && memberName is "Invoke" or "CreateDelegate"
            || typeNamespace == "System.Linq.Expressions"
                && typeName is "LambdaExpression" or "Expression`1"
                && memberName == "Compile"
            || typeNamespace == "System.Runtime.InteropServices"
                && typeName is "NativeLibrary" or "Marshal"
                && memberName is "Load" or "TryLoad" or "GetExport"
                    or "GetDelegateForFunctionPointer";
    }

    private static IReadOnlySet<string> BuildManagedClosure(
        IEnumerable<string> roots,
        string selectedRuntime)
    {
        var rootArray = roots.ToArray();
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        if (rootArray.Distinct(pathComparer).Count() != rootArray.Length)
        {
            throw new ProtocolException("HV244_PRODUCTION_DEPENDENCY_CLOSURE");
        }
        var candidateByIdentity = rootArray
            .GroupBy(
                path => GetAssemblyIdentity(path).Canonical,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
            group => group.Key,
            group =>
            {
                var candidates = group.Order(StringComparer.Ordinal).ToArray();
                if (candidates.Length != 1)
                {
                    throw new ProtocolException("HV244_PRODUCTION_DEPENDENCY_CLOSURE");
                }
                return candidates[0];
            },
            StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, string> trustedPlatformAssemblies;
        try
        {
            trustedPlatformAssemblies =
                TrustedPlatformAssemblies(selectedRuntime);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or BadImageFormatException)
        {
            throw new ProtocolException("HV249_SELECTED_RUNTIME_MANIFEST");
        }
        var closure = new HashSet<string>(
            pathComparer);
        var pending = new Stack<string>(rootArray);
        while (pending.TryPop(out var path))
        {
            if (!closure.Add(path))
            {
                continue;
            }
            foreach (var reference in GetAssemblyReferences(path))
            {
                if (candidateByIdentity.TryGetValue(
                        reference.Canonical,
                        out var dependency))
                {
                    pending.Push(dependency);
                }
                else if (!trustedPlatformAssemblies.ContainsKey(reference.Canonical))
                {
                    throw new ProtocolException(
                        "HV244_PRODUCTION_DEPENDENCY_CLOSURE");
                }
            }
        }
        return closure;
    }

    private static IReadOnlyDictionary<string, string> TrustedPlatformAssemblies(
        string selectedRuntime)
    {
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string paths)
        {
            throw new ProtocolException(
                "HV249_SELECTED_RUNTIME_MANIFEST");
        }
        var runtimeDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory()));
        if (!string.Equals(
                Environment.Version.ToString(),
                selectedRuntime,
                StringComparison.Ordinal))
        {
            throw new ProtocolException(
                "HV249_SELECTED_RUNTIME_MANIFEST");
        }
        var runtimePaths = paths
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFullPath)
            .Where(path => string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path)!),
                runtimeDirectory,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            .Distinct(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal)
            .ToArray();
        if (runtimePaths.Length == 0)
        {
            throw new ProtocolException(
                "HV249_SELECTED_RUNTIME_MANIFEST");
        }
        return runtimePaths
            .GroupBy(
                path => GetAssemblyIdentity(path).Canonical,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var candidates = group.Order(StringComparer.Ordinal).ToArray();
                    if (candidates.Length != 1)
                    {
                        throw new ProtocolException(
                            "HV249_SELECTED_RUNTIME_MANIFEST");
                    }
                    return candidates[0];
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private static ManagedAssemblyIdentity GetAssemblyIdentity(string assemblyPath)
    {
        var identity = AssemblyName.GetAssemblyName(assemblyPath);
        return new(
            identity.Name
                ?? throw new BadImageFormatException(
                    "A declared managed artifact has no assembly name."),
            identity.Version?.ToString() ?? "0.0.0.0",
            string.IsNullOrEmpty(identity.CultureName)
                ? "neutral"
                : identity.CultureName,
            FormatPublicKeyToken(identity.GetPublicKeyToken()));
    }

    private static IReadOnlyList<ManagedAssemblyIdentity> GetAssemblyReferences(
        string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            throw new BadImageFormatException(
                "A declared managed artifact does not contain metadata.");
        }
        var metadata = peReader.GetMetadataReader();
        return metadata.AssemblyReferences
            .Select(handle =>
            {
                var reference = metadata.GetAssemblyReference(handle);
                var keyOrToken = metadata.GetBlobBytes(reference.PublicKeyOrToken);
                var token = (reference.Flags & AssemblyFlags.PublicKey) != 0
                    ? ComputePublicKeyToken(keyOrToken)
                    : FormatPublicKeyToken(keyOrToken);
                var culture = reference.Culture.IsNil
                    ? "neutral"
                    : metadata.GetString(reference.Culture);
                return new ManagedAssemblyIdentity(
                    metadata.GetString(reference.Name),
                    reference.Version.ToString(),
                    string.IsNullOrEmpty(culture) ? "neutral" : culture,
                    token);
            })
            .ToArray();
    }

    private static string ComputePublicKeyToken(byte[] publicKey)
    {
        if (publicKey.Length == 0)
        {
            return "null";
        }
        var digest = SHA1.HashData(publicKey);
        return Convert.ToHexStringLower(digest[^8..].Reverse().ToArray());
    }

    private static string FormatPublicKeyToken(byte[]? token) =>
        token is null || token.Length == 0
            ? "null"
            : Convert.ToHexStringLower(token);

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

    private sealed record ManagedAssemblyIdentity(
        string Name,
        string Version,
        string Culture,
        string PublicKeyToken)
    {
        public string Canonical =>
            $"{Name}, Version={Version}, Culture={Culture}, PublicKeyToken={PublicKeyToken}";
    }
}
