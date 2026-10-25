namespace ContractScribe.Core;

/// <summary>Revalidates the original read context before a delayed result is released.</summary>
public interface IDocumentationScribeToolResultReleaseValidator
{
    bool ValidateResultRelease(CancellationToken cancellationToken);
}
