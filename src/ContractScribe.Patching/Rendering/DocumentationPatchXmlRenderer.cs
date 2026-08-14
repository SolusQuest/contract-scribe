using System.Collections.Immutable;
using ContractScribe.Core;

namespace ContractScribe.Patching.Rendering;

internal static class DocumentationPatchXmlRenderer
{
    public static ImmutableArray<string> Render(DocumentationPatchContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content is DocumentationPatchInheritDocContent)
        {
            return ["<inheritdoc/>"];
        }

        var structured = (DocumentationPatchStructuredContent)content;
        var result = ImmutableArray.CreateBuilder<string>();
        AddElement(result, "summary", null, null, structured.SummaryLines);
        foreach (var item in structured.TypeParameters)
        {
            AddElement(result, "typeparam", "name", item.Name, item.Lines);
        }

        foreach (var item in structured.Parameters)
        {
            AddElement(result, "param", "name", item.Name, item.Lines);
        }

        if (structured.Return is { } returnContent)
        {
            AddElement(result, "returns", null, null, returnContent.Lines);
        }
        else if (structured.Value is { } valueContent)
        {
            AddElement(result, "value", null, null, valueContent.Lines);
        }

        foreach (var item in structured.Exceptions.OrderBy(
                     exception => exception.TypeDocumentationId,
                     StringComparer.Ordinal))
        {
            AddElement(
                result,
                "exception",
                "cref",
                item.TypeDocumentationId,
                item.Lines);
        }

        if (structured.RemarksLines is { } remarks)
        {
            AddElement(result, "remarks", null, null, remarks);
        }

        return result.ToImmutable();
    }

    private static void AddElement(
        ImmutableArray<string>.Builder result,
        string element,
        string? attribute,
        string? attributeValue,
        ImmutableArray<string> logicalLines)
    {
        result.Add(attribute is null
            ? $"<{element}>"
            : $"<{element} {attribute}=\"{EscapeAttribute(attributeValue!)}\">");
        foreach (var line in logicalLines)
        {
            result.Add(EscapeText(line));
        }

        result.Add($"</{element}>");
    }

    internal static string EscapeText(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    internal static string EscapeAttribute(string value) =>
        EscapeText(value).Replace("\"", "&quot;", StringComparison.Ordinal);
}
