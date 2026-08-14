using System.Collections.Immutable;
using ContractScribe.Core;
using ContractScribe.Patching.Rendering;

namespace ContractScribe.Patching;

internal static class DocumentationPatchRenderer
{
    public static string Render(
        DocumentationPatchContent content,
        string indentation,
        string newline)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(indentation);
        if (newline is not "\n" and not "\r\n")
        {
            throw new ArgumentException("The documentation newline must be LF or CRLF.", nameof(newline));
        }

        if (indentation.Any(character => character is not ' ' and not '\t'))
        {
            throw new ArgumentException("The declaration indentation is not representable.", nameof(indentation));
        }

        var logicalLines = DocumentationPatchXmlRenderer.Render(content);
        var rendered = ImmutableArray.CreateBuilder<string>(logicalLines.Length);
        foreach (var line in logicalLines)
        {
            rendered.Add(string.IsNullOrEmpty(line)
                ? indentation + "///"
                : indentation + "/// " + line);
        }

        return string.Join(newline, rendered) + newline;
    }
}
