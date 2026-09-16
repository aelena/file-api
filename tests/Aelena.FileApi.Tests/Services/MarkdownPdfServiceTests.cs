using System.Text;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Services.Pdf;
using AwesomeAssertions;
using Xunit;

namespace Aelena.FileApi.Tests.Services;

/// <summary>
/// PDF rendering tests. Each one reads its own output back with
/// <see cref="PdfService.ExtractText"/> rather than checking the byte length —
/// a PDF that renders and a PDF that contains the text are different claims,
/// and only the second one is worth making.
/// </summary>
public class MarkdownPdfServiceTests
{
    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "SampleFiles", name));

    private static string TextOf(byte[] pdf) =>
        string.Join("\n", PdfService.ExtractText(pdf, "out.pdf").Pages.Select(p => p.Text));

    // ── Markdown ─────────────────────────────────────────────────────────

    [Fact]
    public void FromMarkdown_TypesetsEveryBlockConstruct()
    {
        const string markdown = """
            # Chapter One

            A paragraph with **bold**, *italic* and `code` in it.

            - first bullet
            - second bullet

            1. first step
            2. second step

            > a quotation

            | Name | Value |
            | --- | --- |
            | alpha | 1 |

            ```
            var x = 1;
            ```

            ---

            The closing paragraph.
            """;

        var (name, bytes) = MarkdownPdfService.FromMarkdown(markdown, "notes.md", title: "Notes");

        name.Should().Be("notes.pdf");
        Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");

        var text = TextOf(bytes);
        text.Should().Contain("Chapter One");
        text.Should().Contain("bold").And.Contain("italic").And.Contain("code");
        text.Should().Contain("first bullet").And.Contain("second bullet");
        text.Should().Contain("first step").And.Contain("second step");
        text.Should().Contain("a quotation");
        text.Should().Contain("alpha");
        text.Should().Contain("var x = 1;");
        text.Should().Contain("The closing paragraph.");
    }

    [Fact]
    public void FromMarkdown_InlineMarkersAreConsumedNotPrinted()
    {
        var (_, bytes) = MarkdownPdfService.FromMarkdown("This is **emphatic** indeed.", "a.md");

        var text = TextOf(bytes);
        text.Should().Contain("emphatic");
        text.Should().NotContain("**");
    }

    [Fact]
    public void FromMarkdown_UnclosedMarkerStaysLiteral()
    {
        // Book text is full of stray asterisks. One that never closes must not
        // swallow the rest of the line.
        var (_, bytes) = MarkdownPdfService.FromMarkdown("A footnote marker * and more text.", "a.md");

        TextOf(bytes).Should().Contain("and more text");
    }

    [Fact]
    public void FromMarkdown_NonWinAnsiText_IsTransliteratedNotDropped()
    {
        var (_, bytes) = MarkdownPdfService.FromMarkdown("Přehled — ĉapitro ↔ 日本", "a.md");

        var text = TextOf(bytes);
        // Accents that CP1252 cannot carry lose the accent but keep the letter.
        text.Should().Contain("Prehled").And.Contain("capitro");
        // An arrow has an ASCII reading; a CJK glyph does not.
        text.Should().Contain("<->").And.Contain("?");
    }

    [Fact]
    public void FromMarkdown_Empty_Is422()
    {
        var ex = FluentActions.Invoking(() => MarkdownPdfService.FromMarkdown("   \n  ", "a.md"))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(422);
    }

    // ── Document conversion ──────────────────────────────────────────────

    [Fact]
    public void ConvertDocument_Epub_ProducesAPdfCarryingTheBookText()
    {
        var (name, bytes) = MarkdownPdfService.ConvertDocument(
            Fixture("public-domain-pieces.epub"), "public-domain-pieces.epub");

        name.Should().Be("public-domain-pieces.pdf");

        var text = TextOf(bytes);
        text.Should().Contain("Three Public Domain Pieces");
        text.Should().Contain("Shall I compare thee to a summer's day?");
        text.Should().Contain("Necessity is the mother of invention");
    }

    [Fact]
    public void ConvertDocument_Mobi_ProducesAPdfCarryingTheBookText()
    {
        var (_, bytes) = MarkdownPdfService.ConvertDocument(
            Fixture("public-domain-pieces.mobi"), "public-domain-pieces.mobi");

        TextOf(bytes).Should().Contain("A crow perishing with thirst saw a pitcher");
    }

    [Fact]
    public void ConvertDocument_Doc_ProducesAPdfCarryingTheDocumentText()
    {
        var (_, bytes) = MarkdownPdfService.ConvertDocument(
            Fixture("public-domain-pieces.doc"), "public-domain-pieces.doc");

        TextOf(bytes).Should().Contain("The grapes are sour");
    }

    [Fact]
    public void ConvertDocument_DjvuWithoutReadableText_FailsRatherThanEmittingABlankPdf()
    {
        var ex = FluentActions.Invoking(() => MarkdownPdfService.ConvertDocument(
            Fixture("un-resolution-1837.djvu"), "un-resolution-1837.djvu"))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(501);
    }
}
