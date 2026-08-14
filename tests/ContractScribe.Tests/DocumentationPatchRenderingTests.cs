using System.Collections.Immutable;
using System.Reflection;
using ContractScribe.Core;
using ContractScribe.Patching;
using ContractScribe.Patching.Rendering;

namespace ContractScribe.Tests;

public sealed class DocumentationPatchRenderingTests
{
    [Fact]
    public void InheritDocRendersAsTheExclusiveSingleElement()
    {
        var rendered = DocumentationPatchRenderer.Render(
            Construct<DocumentationPatchInheritDocContent>(),
            "\t",
            "\n");

        Assert.Equal("\t/// <inheritdoc/>\n", rendered);
    }

    [Fact]
    public void StructuredContentUsesFrozenOrderAndEscapesWithoutDecoding()
    {
        var content = Construct<DocumentationPatchStructuredContent>(
            ImmutableArray.Create("A &amp; B < C > D", ""),
            ImmutableArray.Create(Construct<DocumentationPatchNamedContent>(
                "tp:T",
                "T\"&<>",
                ImmutableArray.Create("type"))),
            ImmutableArray.Create(Construct<DocumentationPatchNamedContent>(
                "p:value",
                "value",
                ImmutableArray.Create("parameter"))),
            Construct<DocumentationPatchComponentContent>(
                "return",
                ImmutableArray.Create("return value")),
            null,
            ImmutableArray.Create(
                Construct<DocumentationPatchExceptionContent>(
                    "T:Z.Exception",
                    ImmutableArray.Create("z")),
                Construct<DocumentationPatchExceptionContent>(
                    "T:A.Exception",
                    ImmutableArray.Create("]]>")
                )),
            ImmutableArray.Create("remarks"));

        var rendered = DocumentationPatchXmlRenderer.Render(content);

        Assert.Equal(
            string.Join('\n', new[]
            {
                "<summary>",
                "A &amp;amp; B &lt; C &gt; D",
                "",
                "</summary>",
                "<typeparam name=\"T&quot;&amp;&lt;&gt;\">",
                "type",
                "</typeparam>",
                "<param name=\"value\">",
                "parameter",
                "</param>",
                "<returns>",
                "return value",
                "</returns>",
                "<exception cref=\"T:A.Exception\">",
                "]]&gt;",
                "</exception>",
                "<exception cref=\"T:Z.Exception\">",
                "z",
                "</exception>",
                "<remarks>",
                "remarks",
                "</remarks>",
            }),
            string.Join('\n', rendered));
    }

    [Fact]
    public void EmptyLogicalLinesHaveNoTrailingSpaceAndCrLfIsPreserved()
    {
        var content = Construct<DocumentationPatchStructuredContent>(
            ImmutableArray.Create("first", "", "last"),
            ImmutableArray<DocumentationPatchNamedContent>.Empty,
            ImmutableArray<DocumentationPatchNamedContent>.Empty,
            null,
            Construct<DocumentationPatchComponentContent>(
                "value",
                ImmutableArray.Create("v")),
            ImmutableArray<DocumentationPatchExceptionContent>.Empty,
            null);

        var rendered = DocumentationPatchRenderer.Render(content, "    ", "\r\n");

        Assert.Equal(
            "    /// <summary>\r\n"
            + "    /// first\r\n"
            + "    ///\r\n"
            + "    /// last\r\n"
            + "    /// </summary>\r\n"
            + "    /// <value>\r\n"
            + "    /// v\r\n"
            + "    /// </value>\r\n",
            rendered);
        Assert.DoesNotContain("/// \r\n", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RendererNeverWrapsOrNormalizesUnicode()
    {
        var line = "e\u0301 😀 " + new string('x', 300);
        var content = Construct<DocumentationPatchStructuredContent>(
            ImmutableArray.Create(line),
            ImmutableArray<DocumentationPatchNamedContent>.Empty,
            ImmutableArray<DocumentationPatchNamedContent>.Empty,
            null,
            null,
            ImmutableArray<DocumentationPatchExceptionContent>.Empty,
            null);

        var rendered = DocumentationPatchRenderer.Render(content, string.Empty, "\n");

        Assert.Contains("/// " + line + "\n", rendered, StringComparison.Ordinal);
        Assert.Equal(3, rendered.Count(character => character == '\n'));
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n\r")]
    [InlineData("x")]
    public void RendererRejectsUnknownNewlineRepresentations(string newline)
    {
        Assert.Throws<ArgumentException>(() => DocumentationPatchRenderer.Render(
            Construct<DocumentationPatchInheritDocContent>(),
            string.Empty,
            newline));
    }

    [Fact]
    public void RendererRejectsNonIndentationPrefixes()
    {
        Assert.Throws<ArgumentException>(() => DocumentationPatchRenderer.Render(
            Construct<DocumentationPatchInheritDocContent>(),
            " x",
            "\n"));
    }

    private static T Construct<T>(params object?[] arguments) where T : class =>
        Assert.IsType<T>(Activator.CreateInstance(
            typeof(T),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            arguments,
            culture: null));
}
