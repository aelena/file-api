using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Services.Docx;
using AwesomeAssertions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Aelena.FileApi.Tests.Services;

public class DocxServiceTests
{
    private static byte[] CreateSimpleDocx(int paragraphs = 3, string? prefix = null)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());

            for (var i = 0; i < paragraphs; i++)
            {
                var text = $"{prefix ?? "Paragraph"} {i + 1}. The quick brown fox jumps over the lazy dog.";
                mainPart.Document.Body!.Append(new Paragraph(new Run(new Text(text))));
            }

            doc.PackageProperties.Title = "Test Document";
            doc.PackageProperties.Creator = "Unit Test";
        }
        return ms.ToArray();
    }

    // ── Metrics ──────────────────────────────────────────────────────────

    [Fact]
    public void GetMetrics_SimpleDocx_ReturnsCorrectCounts()
    {
        var data = CreateSimpleDocx(5);
        var result = DocxService.GetMetrics(data, "test.docx");

        result.FileName.Should().Be("test.docx");
        result.ParagraphCount.Should().Be(5);
        result.WordCount.Should().BeGreaterThan(0);
        result.CharCount.Should().BeGreaterThan(0);
        result.TokenCount.Should().BeGreaterThan(0);
        result.FileSizeBytes.Should().BeGreaterThan(0);
        result.TableCount.Should().Be(0);
    }

    // ── Metadata ─────────────────────────────────────────────────────────

    [Fact]
    public void GetMetadata_ReturnsTitle()
    {
        var data = CreateSimpleDocx();
        var result = DocxService.GetMetadata(data, "test.docx");

        result.Title.Should().Be("Test Document");
        result.Author.Should().Be("Unit Test");
        result.ParagraphCount.Should().Be(3);
    }

    // ── Extract Pages ────────────────────────────────────────────────────

    [Fact]
    public void ExtractPages_SpecificParagraphs_ReturnsSubset()
    {
        var data = CreateSimpleDocx(5);
        var result = DocxService.ExtractPages(data, "test.docx", "1,3,5");

        result.Extracted.Should().HaveCount(3);
        result.Extracted[0].Page.Should().Be(1);
        result.Extracted[1].Page.Should().Be(3);
        result.Extracted[2].Page.Should().Be(5);
    }

    [Fact]
    public void ExtractPages_OutOfRange_IsBadRequestNamingTheParagraphCount()
    {
        var data = CreateSimpleDocx(3);

        var ex = FluentActions.Invoking(() => DocxService.ExtractPages(data, "test.docx", "1-5"))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(400);
        ex.Detail.Should().Contain("out of bounds");
    }

    // ── Search ───────────────────────────────────────────────────────────

    [Fact]
    public void Search_LiteralQuery_FindsMatches()
    {
        var data = CreateSimpleDocx(3);
        var (_, matches) = DocxService.Search(data, "test.docx", query: "fox");

        matches.Should().HaveCount(3); // one per paragraph
    }

    [Fact]
    public void Search_RegexPattern_FindsMatches()
    {
        var data = CreateSimpleDocx(2);
        var (_, matches) = DocxService.Search(data, "test.docx", pattern: @"Paragraph \d+");

        matches.Should().HaveCount(2);
    }

    // ── Markdown ─────────────────────────────────────────────────────────

    [Fact]
    public void ExtractToMarkdown_ContainsText()
    {
        var data = CreateSimpleDocx(2, "Content");
        var result = DocxService.ExtractToMarkdown(data, "test.docx");

        result.Markdown.Should().Contain("Content 1");
        result.Markdown.Should().Contain("Content 2");
        result.ParagraphCount.Should().Be(2);
    }

    // ── Health Check ─────────────────────────────────────────────────────

    [Fact]
    public void HealthCheck_SimpleDocx_IsHealthy()
    {
        var data = CreateSimpleDocx();
        var result = DocxService.HealthCheck(data, "test.docx");

        result.ErrorCount.Should().Be(0);
    }

    // ── Remove Metadata ──────────────────────────────────────────────────

    [Fact]
    public void RemoveMetadata_ClearsTitle()
    {
        var data = CreateSimpleDocx();
        var (name, cleaned) = DocxService.RemoveMetadata(data, "test.docx");

        name.Should().Contain("no_metadata");
        var meta = DocxService.GetMetadata(cleaned, "cleaned.docx");
        meta.Title.Should().BeNull();
        meta.Author.Should().BeNull();
    }
}
