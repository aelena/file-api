using Aelena.FileApi.Core.Services.Documents;
using AwesomeAssertions;
using Xunit;

namespace Aelena.FileApi.Tests.Services;

/// <summary>
/// Tests for the HTML converter shared by the EPUB and MOBI readers. It is
/// deliberately forgiving — real books are not well-formed — so most of these
/// check that malformed input still yields the content rather than an exception.
/// </summary>
public class HtmlTextTests
{
    // ── Markdown ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("<h1>Title</h1>", "# Title")]
    [InlineData("<h3>Sub</h3>", "### Sub")]
    [InlineData("<p>Plain text.</p>", "Plain text.")]
    [InlineData("<p>a <strong>bold</strong> word</p>", "a **bold** word")]
    [InlineData("<p>an <em>italic</em> word</p>", "an *italic* word")]
    [InlineData("<p>some <code>code</code></p>", "some `code`")]
    [InlineData("<hr/>", "---")]
    public void ToMarkdown_MapsBlockAndInlineElements(string html, string expected) =>
        HtmlText.ToMarkdown(html).Should().Be(expected);

    [Fact]
    public void ToMarkdown_UnorderedListBecomesDashes()
    {
        var markdown = HtmlText.ToMarkdown("<ul><li>one</li><li>two</li></ul>");

        markdown.Should().Be("- one\n\n- two");
    }

    [Fact]
    public void ToMarkdown_OrderedListIsNumberedFromOne()
    {
        var markdown = HtmlText.ToMarkdown("<ol><li>first</li><li>second</li><li>third</li></ol>");

        markdown.Should().Contain("1. first").And.Contain("2. second").And.Contain("3. third");
    }

    [Fact]
    public void ToMarkdown_LinkKeepsBothTextAndDestination()
    {
        HtmlText.ToMarkdown("""<p>See <a href="https://example.org">the site</a>.</p>""")
            .Should().Be("See [the site](https://example.org).");
    }

    [Fact]
    public void ToMarkdown_InDocumentAnchorIsNotRenderedAsALink()
    {
        // EPUBs are full of <a href="#note1"> wrappers whose destination means
        // nothing outside the book.
        HtmlText.ToMarkdown("""<p>See <a href="#note1">note</a>.</p>""")
            .Should().Be("See note.");
    }

    [Fact]
    public void ToMarkdown_TableBecomesAPipeTableWithAHeaderRule()
    {
        var markdown = HtmlText.ToMarkdown(
            "<table><tr><th>Name</th><th>Value</th></tr><tr><td>alpha</td><td>1</td></tr></table>");

        markdown.Should().Contain("| Name | Value |");
        markdown.Should().Contain("| --- | --- |");
        markdown.Should().Contain("| alpha | 1 |");
    }

    [Fact]
    public void ToMarkdown_PreservesWhitespaceInsidePre()
    {
        var markdown = HtmlText.ToMarkdown("<pre>line one\n    indented</pre>");

        markdown.Should().Contain("```");
        markdown.Should().Contain("    indented");
    }

    [Fact]
    public void ToMarkdown_BlockquoteIsPrefixed() =>
        HtmlText.ToMarkdown("<blockquote><p>quoted</p></blockquote>").Should().Be("> quoted");

    // ── Robustness ───────────────────────────────────────────────────────

    [Fact]
    public void ScriptAndStyleBodiesAreNotContent() =>
        HtmlText.ToPlainText("<style>p{color:red}</style><script>alert(1)</script><p>real</p>")
            .Should().Be("real");

    [Fact]
    public void EntitiesAreDecoded() =>
        HtmlText.ToPlainText("<p>Caf&eacute; &amp; cr&egrave;me &#8212; 5&nbsp;euros</p>")
            .Should().Be("Café & crème — 5 euros");

    [Fact]
    public void EscapedAngleBracketsStayLiteral() =>
        HtmlText.ToPlainText("<p>compare &lt;p&gt; with &lt;div&gt;</p>")
            .Should().Be("compare <p> with <div>");

    [Fact]
    public void AngleBracketInsideAnAttributeDoesNotEndTheTag() =>
        HtmlText.ToPlainText("""<p title="a > b">content</p>""").Should().Be("content");

    [Fact]
    public void UnterminatedTagIsTreatedAsText() =>
        HtmlText.ToPlainText("<p>before</p><p>after").Should().Contain("after");

    [Fact]
    public void UnknownTagsAreDroppedButTheirContentIsKept() =>
        HtmlText.ToPlainText("<p>Mobipocket <mbp:nobreak>keeps</mbp:nobreak> going</p>")
            .Should().Be("Mobipocket keeps going");

    [Fact]
    public void SourceLineBreaksAreCollapsed() =>
        HtmlText.ToPlainText("<p>a sentence\n    wrapped\n    across lines</p>")
            .Should().Be("a sentence wrapped across lines");

    [Fact]
    public void BrIsALineBreakWithinTheBlock() =>
        HtmlText.ToPlainText("<p>first<br/>second</p>").Should().Be("first\nsecond");

    [Fact]
    public void PlainTextDropsMarkdownMarkers() =>
        HtmlText.ToPlainText("<h1>Title</h1><p>and <strong>bold</strong></p>")
            .Should().Be("Title\n\nand bold");

    [Fact]
    public void Title_ReadsTheHeadElement() =>
        HtmlText.Title("<html><head><title>Chapter 3</title></head><body>x</body></html>")
            .Should().Be("Chapter 3");

    [Fact]
    public void Title_IsNullWhenAbsent() =>
        HtmlText.Title("<html><body>x</body></html>").Should().BeNull();

    [Fact]
    public void EmptyInputIsEmptyOutput() =>
        HtmlText.ToMarkdown("").Should().BeEmpty();
}
