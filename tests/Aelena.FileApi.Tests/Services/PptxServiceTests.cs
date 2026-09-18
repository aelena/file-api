using System.Text;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Services.Pptx;
using AwesomeAssertions;
using Xunit;

namespace Aelena.FileApi.Tests.Services;

/// <summary>
/// Presentation tests, run against a fixture PowerPoint itself wrote — three
/// slides with speaker notes on two of them, plus a hidden fourth. See
/// <c>SampleFiles/README.md</c>.
/// </summary>
public class PptxServiceTests
{
    private const string Fixture = "public-domain-pieces.pptx";

    private static byte[] Deck() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "SampleFiles", Fixture));

    // ── Metrics ──────────────────────────────────────────────────────────

    [Fact]
    public void GetMetrics_CountsSlidesNotesAndHiddenSlides()
    {
        var result = PptxService.GetMetrics(Deck(), Fixture);

        result.SlideCount.Should().Be(4);
        result.HiddenSlideCount.Should().Be(1);
        result.SlidesWithNotes.Should().Be(2);
        result.ShapeCount.Should().BeGreaterThan(0);
        result.WordCount.Should().BeGreaterThan(0);
    }

    // ── Slides ───────────────────────────────────────────────────────────

    [Fact]
    public void GetSlides_ReturnsThemInPresentationOrder()
    {
        var result = PptxService.GetSlides(Deck(), Fixture);

        result.SlideCount.Should().Be(4);
        result.Slides.Select(s => s.Number).Should().Equal(1, 2, 3, 4);
        result.Slides[0].Title.Should().Be("Three Public Domain Pieces");
        result.Slides[1].Title.Should().Be("The Fox and the Grapes");
        result.Slides[2].Title.Should().Be("The Crow and the Pitcher");
    }

    [Fact]
    public void GetSlides_ReadsTitleFromThePlaceholderNotTheFirstText()
    {
        var slide = PptxService.GetSlides(Deck(), Fixture).Slides[1];

        slide.Title.Should().Be("The Fox and the Grapes");
        slide.Text.Should().Contain("The grapes are sour");
    }

    [Fact]
    public void GetSlides_ExtractsSpeakerNotes()
    {
        // The notes pane is where a deck's actual argument lives, and is what
        // most extraction tools drop.
        var slides = PptxService.GetSlides(Deck(), Fixture).Slides;

        slides[0].Notes.Should().Contain("out of copyright");
        slides[1].Notes.Should().Contain("pause before the last line");
        slides[2].Notes.Should().BeNull();
    }

    [Fact]
    public void GetSlides_NotesDoNotRepeatTheSlideBody()
    {
        // A notes page also carries a thumbnail of the slide, whose placeholder
        // repeats the body text; it must not be reported as notes.
        var slide = PptxService.GetSlides(Deck(), Fixture).Slides[1];

        slide.Notes.Should().NotContain("The grapes are sour");
    }

    [Fact]
    public void GetSlides_FlagsTheHiddenSlide()
    {
        var slides = PptxService.GetSlides(Deck(), Fixture).Slides;

        slides.Count(s => s.Hidden).Should().Be(1);
        slides.Single(s => s.Hidden).Title.Should().Be("Cut for time");
    }

    [Fact]
    public void GetNotes_ReturnsOnlySlidesThatHaveThem()
    {
        var result = PptxService.GetNotes(Deck(), Fixture);

        result.Slides.Should().HaveCount(2);
        result.Slides.Should().OnlyContain(s => s.Notes != null);
    }

    // ── Markdown ─────────────────────────────────────────────────────────

    [Fact]
    public void ExtractToMarkdown_OutlinesTheDeckWithNotesAsBlockQuotes()
    {
        var result = PptxService.ExtractToMarkdown(Deck(), Fixture);

        result.SlideCount.Should().Be(4);
        result.Markdown.Should().Contain("## 1. Three Public Domain Pieces");
        result.Markdown.Should().Contain("## 2. The Fox and the Grapes");
        result.Markdown.Should().Contain("> **Notes:**");
        result.Markdown.Should().Contain("_(hidden)_");
    }

    // ── Metadata and search ──────────────────────────────────────────────

    [Fact]
    public void GetMetadata_CountsSlides()
    {
        PptxService.GetMetadata(Deck(), Fixture).SlideCount.Should().Be(4);
    }

    [Fact]
    public void RemoveMetadata_ClearsPropertiesAndKeepsTheSlides()
    {
        var (name, bytes) = PptxService.RemoveMetadata(Deck(), Fixture);

        name.Should().Be("public-domain-pieces_clean.pptx");

        PptxService.GetMetadata(bytes, name).Author.Should().BeNullOrEmpty();
        PptxService.GetSlides(bytes, name).SlideCount.Should().Be(4);
    }

    [Fact]
    public void Search_CoversSpeakerNotesAsWellAsSlides()
    {
        var (_, onSlide) = PptxService.Search(Deck(), Fixture, query: "grapes are sour", pattern: null);
        var (_, inNotes) = PptxService.Search(Deck(), Fixture, query: "out of copyright", pattern: null);

        onSlide.Should().NotBeEmpty();
        inNotes.Should().NotBeEmpty();
    }

    [Fact]
    public void NotAPresentation_Is422()
    {
        var ex = FluentActions.Invoking(() =>
            PptxService.GetMetrics(Encoding.UTF8.GetBytes("not a deck"), "x.pptx"))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(422);
        ex.Title.Should().Be("Invalid PPTX");
    }
}
