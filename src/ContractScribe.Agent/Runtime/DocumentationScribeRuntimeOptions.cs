using System.Collections.Immutable;
using System.Text;
using ContractScribe.Agent.Prompting;
using ContractScribe.Core;

namespace ContractScribe.Agent.Runtime;

public sealed class DocumentationScribeRuntimeOptions
{
    public DocumentationScribeRuntimeOptions(
        string providerConfigurationId,
        string modelConfigurationId,
        string scribeProtocolId)
    {
        ProviderConfigurationId = DocumentationScribeBoundary.ValidateIdentifier(
            providerConfigurationId,
            nameof(providerConfigurationId));
        ModelConfigurationId = DocumentationScribeBoundary.ValidateIdentifier(
            modelConfigurationId,
            nameof(modelConfigurationId));
        ScribeProtocolId = DocumentationScribeBoundary.ValidateIdentifier(
            scribeProtocolId,
            nameof(scribeProtocolId));
    }

    public string ProviderConfigurationId { get; }

    public string ModelConfigurationId { get; }

    public string ScribeProtocolId { get; }

    public override string ToString() => nameof(DocumentationScribeRuntimeOptions);
}
