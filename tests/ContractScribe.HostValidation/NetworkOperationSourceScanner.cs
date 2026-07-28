using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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

            var text = File.ReadAllText(RepositoryPaths.ResolveConfined(root, input.Path));
            if (ForbiddenNamespace().IsMatch(text)
                || ForbiddenType().IsMatch(text)
                || ForbiddenFactory().IsMatch(text))
            {
                return true;
            }
        }

        return materialization?.BuiltArtifacts.Any(artifact =>
            artifact.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && HasForbiddenMemberReference(
                RepositoryPaths.ResolveConfined(root, artifact.Path))) == true;
    }

    public static void ValidateSyntheticSource(string sourceText)
    {
        if (ForbiddenNamespace().IsMatch(sourceText)
            || ForbiddenType().IsMatch(sourceText)
            || ForbiddenFactory().IsMatch(sourceText))
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
}
