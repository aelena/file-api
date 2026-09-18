using System.Text;
using System.Text.Json;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Services.Pdf;
using AwesomeAssertions;
using iText.Forms;
using iText.Forms.Fields;
using iText.IO.Image;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Navigation;
using iText.Layout;
using iText.Layout.Element;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
// SixLabors and iText both define Rectangle; the PDF one wins here.
using Rectangle = iText.Kernel.Geom.Rectangle;

namespace Aelena.FileApi.Tests.Services;

/// <summary>
/// The half of <see cref="PdfService"/> that needs a document with structure in
/// it — outlines, annotations, form fields, embedded images, encryption, mixed
/// page geometry — plus the page-level write operations.
/// <para>
/// <see cref="PdfServiceTests"/> covers what a plain text document can exercise.
/// Everything here needs a fixture built for the purpose, which is why it lives
/// apart rather than swelling that file with builders nothing else uses.
/// </para>
/// </summary>
public class PdfServiceOperationTests
{
    private static byte[] SimplePdf(int pages = 1)
    {
        using var ms = new MemoryStream();
        using var writer = new PdfWriter(ms);
        using var doc = new PdfDocument(writer);
        using var layout = new Document(doc);

        for (var i = 0; i < pages; i++)
        {
            if (i > 0) layout.Add(new AreaBreak());
            layout.Add(new Paragraph($"Page {i + 1} content. The quick brown fox jumps over the lazy dog."));
        }

        layout.Close();
        return ms.ToArray();
    }

    // ── Extract pages ────────────────────────────────────────────────────

    [Fact]
    public void ExtractPages_SelectedRange_ReturnsOnlyThosePages()
    {
        var result = PdfService.ExtractPages(SimplePdf(5), "test.pdf", "2,4");

        result.TotalPages.Should().Be(5);
        result.Extracted.Should().HaveCount(2);
        result.Extracted[0].Page.Should().Be(2);
        result.Extracted[0].Text.Should().Contain("Page 2");
        result.Extracted[1].Page.Should().Be(4);
        result.Extracted[1].Text.Should().Contain("Page 4");
    }

    [Fact]
    public void ExtractPages_OutOfRange_IsBadRequest()
    {
        var ex = FluentActions.Invoking(() => PdfService.ExtractPages(SimplePdf(2), "test.pdf", "1-9"))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(400);
    }

    // ── Bookmarks ────────────────────────────────────────────────────────

    [Fact]
    public void ExtractBookmarks_NestedOutline_IsFlattenedWithLevels()
    {
        var json = Json(PdfService.ExtractBookmarks(PdfWithBookmarks(), "book.pdf"));

        json.GetProperty("totalBookmarks").GetInt32().Should().Be(3);

        var marks = json.GetProperty("bookmarks").EnumerateArray().ToList();

        marks[0].GetProperty("title").GetString().Should().Be("Chapter One");
        marks[0].GetProperty("level").GetInt32().Should().Be(0);

        // The nested child follows its parent and sits one level deeper.
        marks[1].GetProperty("title").GetString().Should().Be("Section 1.1");
        marks[1].GetProperty("level").GetInt32().Should().Be(1);

        marks[2].GetProperty("title").GetString().Should().Be("Chapter Two");
        marks[2].GetProperty("level").GetInt32().Should().Be(0);
    }

    [Fact]
    public void ExtractBookmarks_DestinationsResolveToPageNumbers()
    {
        var json = Json(PdfService.ExtractBookmarks(PdfWithBookmarks(), "book.pdf"));
        var marks = json.GetProperty("bookmarks").EnumerateArray().ToList();

        marks[0].GetProperty("page").GetInt32().Should().Be(1);
        marks[2].GetProperty("page").GetInt32().Should().Be(2);
    }

    [Fact]
    public void ExtractBookmarks_NoOutline_ReturnsEmpty()
    {
        var json = Json(PdfService.ExtractBookmarks(SimplePdf(2), "plain.pdf"));

        json.GetProperty("totalBookmarks").GetInt32().Should().Be(0);
    }

    // ── Annotations ──────────────────────────────────────────────────────

    [Fact]
    public void ExtractAnnotations_ReturnsTypeContentsAuthorAndPage()
    {
        var json = Json(PdfService.ExtractAnnotations(PdfWithAnnotation(), "annotated.pdf"));

        json.GetProperty("totalAnnotations").GetInt32().Should().Be(1);

        var annot = json.GetProperty("annotations")[0];
        annot.GetProperty("page").GetInt32().Should().Be(1);
        annot.GetProperty("type").GetString().Should().Be("Text");
        annot.GetProperty("contents").GetString().Should().Be("Check this clause");
        annot.GetProperty("author").GetString().Should().Be("Reviewer");
        annot.GetProperty("rect").GetArrayLength().Should().Be(4);
    }

    [Fact]
    public void ExtractAnnotations_NoAnnotations_ReturnsEmpty()
    {
        var json = Json(PdfService.ExtractAnnotations(SimplePdf(1), "plain.pdf"));

        json.GetProperty("totalAnnotations").GetInt32().Should().Be(0);
    }

    // ── Form fields ──────────────────────────────────────────────────────

    [Fact]
    public void ExtractFormFields_ReportsEveryFieldTypeItRecognises()
    {
        var result = PdfService.ExtractFormFields(PdfWithForm(), "form.pdf");

        result.TotalFields.Should().Be(3);

        var byName = result.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

        byName["fullName"].FieldType.Should().Be("text");
        byName["fullName"].Value.Should().Be("Ada Lovelace");
        byName["fullName"].Page.Should().Be(1);

        byName["agreed"].FieldType.Should().Be("checkbox");

        byName["country"].FieldType.Should().Be("dropdown");
        byName["country"].Options.Should().Contain("Spain");
    }

    [Fact]
    public void ExtractFormFields_NoAcroForm_ReturnsEmpty()
    {
        var result = PdfService.ExtractFormFields(SimplePdf(1), "plain.pdf");

        result.TotalFields.Should().Be(0);
        result.Fields.Should().BeEmpty();
    }

    // ── Page numbers ─────────────────────────────────────────────────────

    [Fact]
    public void AddPageNumbers_StampsEveryPageAndSubstitutesPlaceholders()
    {
        var (name, bytes) = PdfService.AddPageNumbers(
            SimplePdf(3), "test.pdf", "bottom-center", 10, 1, 36, "black", "{n} of {total}");

        name.Should().Be("test_numbered.pdf");

        var text = PdfService.ExtractText(bytes, name);
        text.Pages[0].Text.Should().Contain("1 of 3");
        text.Pages[2].Text.Should().Contain("3 of 3");
    }

    [Fact]
    public void AddPageNumbers_StartOffsetShiftsTheFirstLabel()
    {
        var (_, bytes) = PdfService.AddPageNumbers(
            SimplePdf(2), "test.pdf", "bottom-center", 10, 7, 36, "black", "{n}");

        var text = PdfService.ExtractText(bytes, "numbered.pdf");
        text.Pages[0].Text.Should().Contain("7");
        text.Pages[1].Text.Should().Contain("8");
    }

    /// <summary>
    /// Every branch of the position resolver, and the named, hex and fallback
    /// branches of the colour parser. The assertion is deliberately weak —
    /// exactly where iText lays the glyphs down is its business — but no
    /// combination may throw or put the label somewhere unreadable.
    /// </summary>
    [Theory]
    [InlineData("top-left", "red")]
    [InlineData("top-center", "blue")]
    [InlineData("top-right", "green")]
    [InlineData("bottom-left", "grey")]
    [InlineData("bottom-right", "gray")]
    [InlineData("bottom-center", "#3366ff")]
    [InlineData("nonsense", "not-a-colour")]
    public void AddPageNumbers_EveryPositionAndColour_ProducesReadableOutput(string position, string colour)
    {
        var (_, bytes) = PdfService.AddPageNumbers(
            SimplePdf(1), "test.pdf", position, 10, 1, 24, colour, "{n}");

        PdfService.ExtractText(bytes, "numbered.pdf").Pages[0].Text.Should().Contain("1");
    }

    // ── Insert blank pages ───────────────────────────────────────────────

    [Fact]
    public void InsertBlankPages_AddsThemAfterTheNamedPages()
    {
        var (name, bytes) = PdfService.InsertBlankPages(SimplePdf(3), "test.pdf", "1,3", 2);

        name.Should().Be("test_expanded.pdf");

        // 3 original, plus 2 after page 1 and 2 after page 3.
        var text = PdfService.ExtractText(bytes, name);
        text.TotalPages.Should().Be(7);
        text.Pages[0].Text.Should().Contain("Page 1");
        text.Pages[1].Text.Trim().Should().BeEmpty();
        text.Pages[2].Text.Trim().Should().BeEmpty();
        text.Pages[3].Text.Should().Contain("Page 2");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void InsertBlankPages_CountOutOfRange_IsBadRequest(int count)
    {
        var ex = FluentActions.Invoking(() =>
            PdfService.InsertBlankPages(SimplePdf(2), "test.pdf", "1", count))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(400);
        ex.Detail.Should().Contain("between 1 and 20");
    }

    // ── Remove metadata ──────────────────────────────────────────────────

    [Fact]
    public void RemoveMetadata_ClearsTheFieldsButKeepsTheContent()
    {
        var before = PdfService.GetMetadata(PdfWithMetadata(), "meta.pdf");
        before.Title.Should().Be("Quarterly Report");
        before.Author.Should().Be("Ada Lovelace");

        var (name, bytes) = PdfService.RemoveMetadata(PdfWithMetadata(), "meta.pdf");
        name.Should().Be("meta_clean.pdf");

        var after = PdfService.GetMetadata(bytes, name);
        after.Title.Should().BeNullOrEmpty();
        after.Author.Should().BeNullOrEmpty();
        after.Subject.Should().BeNullOrEmpty();
        after.Keywords.Should().BeNullOrEmpty();

        PdfService.ExtractText(bytes, name).Pages[0].Text.Should().Contain("Page 1");
    }

    // ── Unlock ───────────────────────────────────────────────────────────

    [Fact]
    public void UnlockPdf_DropsOwnerRestrictionsAndKeepsTheContent()
    {
        // Owner password only: the document opens for anyone but is marked
        // restricted. That is what "unlock" removes. A user password would make
        // the bytes genuinely unreadable, which no amount of unlocking fixes.
        var (_, encrypted) = PdfService.EncryptPdf(SimplePdf(2), "test.pdf", "", "ownerpw");

        var (name, unlocked) = PdfService.UnlockPdf(encrypted, "test.pdf");

        name.Should().Be("test_unlocked.pdf");

        var text = PdfService.ExtractText(unlocked, name);
        text.TotalPages.Should().Be(2);
        text.Pages[0].Text.Should().Contain("Page 1");
    }

    // ── Health check ─────────────────────────────────────────────────────

    [Fact]
    public void HealthCheck_MixedPageSizes_IsAWarning()
    {
        var result = PdfService.HealthCheck(PdfWithMixedPageSizes(), "mixed.pdf");

        result.Issues.Should().Contain(i => i.Check == "mixed_page_sizes" && i.Severity == "warning");
        result.WarningCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void HealthCheck_PageWithNoTextLayer_SuggestsOcr()
    {
        var result = PdfService.HealthCheck(PdfWithBlankPage(), "scan.pdf");

        result.Issues.Should().Contain(i => i.Check == "ocr_needed" && i.Severity == "warning");
    }

    [Fact]
    public void HealthCheck_EncryptedDocument_IsInfoNotAFault()
    {
        var (_, encrypted) = PdfService.EncryptPdf(SimplePdf(1), "test.pdf", "", "ownerpw");

        var result = PdfService.HealthCheck(encrypted, "encrypted.pdf");

        result.Issues.Should().Contain(i => i.Check == "encryption" && i.Severity == "info");
        result.ErrorCount.Should().Be(0);
    }

    [Fact]
    public void HealthCheck_CorruptDocument_IsAnError()
    {
        var result = PdfService.HealthCheck(Encoding.UTF8.GetBytes("%PDF-1.7 truncated"), "broken.pdf");

        result.Healthy.Should().BeFalse();
        result.ErrorCount.Should().BeGreaterThan(0);
        result.Issues.Should().Contain(i => i.Check == "corruption" && i.Severity == "error");
    }

    // ── Images ───────────────────────────────────────────────────────────

    [Fact]
    public void GetMetrics_CountsEmbeddedImages()
    {
        var result = PdfService.GetMetrics(PdfWithImage(), "illustrated.pdf");

        result.ImageCount.Should().BeGreaterThan(0);
    }

    // ── Declared 501 stubs ───────────────────────────────────────────────

    [Fact]
    public void ExtractTables_Is501AndPointsAtWhatDoesWork()
    {
        var ex = FluentActions.Invoking(() => PdfService.ExtractTables(SimplePdf(1), "test.pdf"))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(501);
        ex.Detail.Should().Contain("extract-markdown");
    }

    [Fact]
    public void RedactText_Is501RatherThanDrawingBoxesOverTheWords()
    {
        var ex = FluentActions.Invoking(() =>
            PdfService.RedactText(SimplePdf(1), "test.pdf", "secret", null, null, null))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(501);
        ex.Detail.Should().Contain("selectable");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    /// <summary>
    /// Round-trip one of the anonymous response objects through JSON, which is
    /// how the endpoints serialise it and therefore the shape callers see.
    /// </summary>
    private static JsonElement Json(object response) =>
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(response));

    private static byte[] PdfWithBookmarks()
    {
        using var ms = new MemoryStream();
        using (var writer = new PdfWriter(ms))
        using (var doc = new PdfDocument(writer))
        {
            var layout = new Document(doc);
            layout.Add(new Paragraph("Page 1 content."));
            layout.Add(new AreaBreak());
            layout.Add(new Paragraph("Page 2 content."));
            layout.Flush();

            doc.GetCatalog().SetPageMode(PdfName.UseOutlines);
            var root = doc.GetOutlines(false);

            var one = root.AddOutline("Chapter One");
            one.AddDestination(PdfExplicitDestination.CreateFit(doc.GetPage(1)));

            var nested = one.AddOutline("Section 1.1");
            nested.AddDestination(PdfExplicitDestination.CreateFit(doc.GetPage(1)));

            var two = root.AddOutline("Chapter Two");
            two.AddDestination(PdfExplicitDestination.CreateFit(doc.GetPage(2)));
        }
        return ms.ToArray();
    }

    private static byte[] PdfWithAnnotation()
    {
        using var ms = new MemoryStream();
        using (var writer = new PdfWriter(ms))
        using (var doc = new PdfDocument(writer))
        {
            var layout = new Document(doc);
            layout.Add(new Paragraph("Page 1 content."));
            layout.Flush();

            var annotation = new PdfTextAnnotation(new Rectangle(100, 700, 24, 24))
                .SetContents("Check this clause");
            annotation.Put(PdfName.T, new PdfString("Reviewer"));
            doc.GetPage(1).AddAnnotation(annotation);
        }
        return ms.ToArray();
    }

    private static byte[] PdfWithForm()
    {
        using var ms = new MemoryStream();
        using (var writer = new PdfWriter(ms))
        using (var doc = new PdfDocument(writer))
        {
            doc.AddNewPage();
            var form = PdfAcroForm.GetAcroForm(doc, true);

            var text = new TextFormFieldBuilder(doc, "fullName")
                .SetWidgetRectangle(new Rectangle(100, 700, 200, 24)).CreateText();
            text.SetValue("Ada Lovelace");
            form.AddField(text);

            var check = new CheckBoxFormFieldBuilder(doc, "agreed")
                .SetWidgetRectangle(new Rectangle(100, 650, 20, 20)).CreateCheckBox();
            form.AddField(check);

            var choice = new ChoiceFormFieldBuilder(doc, "country")
                .SetWidgetRectangle(new Rectangle(100, 600, 200, 24))
                .SetOptions(["Spain", "Portugal", "France"])
                .CreateComboBox();
            form.AddField(choice);
        }
        return ms.ToArray();
    }

    private static byte[] PdfWithMetadata()
    {
        using var ms = new MemoryStream();
        using (var writer = new PdfWriter(ms))
        using (var doc = new PdfDocument(writer))
        {
            var info = doc.GetDocumentInfo();
            info.SetTitle("Quarterly Report");
            info.SetAuthor("Ada Lovelace");
            info.SetSubject("Numbers");
            info.SetKeywords("finance,quarterly");
            info.SetMoreInfo("InternalRef", "ACME-1234");

            var layout = new Document(doc);
            layout.Add(new Paragraph("Page 1 content."));
            layout.Flush();
        }
        return ms.ToArray();
    }

    private static byte[] PdfWithMixedPageSizes()
    {
        using var ms = new MemoryStream();
        using (var writer = new PdfWriter(ms))
        using (var doc = new PdfDocument(writer))
        {
            doc.AddNewPage(PageSize.A4);
            doc.AddNewPage(PageSize.A3);
        }
        return ms.ToArray();
    }

    private static byte[] PdfWithBlankPage()
    {
        using var ms = new MemoryStream();
        using (var writer = new PdfWriter(ms))
        using (var doc = new PdfDocument(writer))
        {
            var layout = new Document(doc);
            layout.Add(new Paragraph("Page 1 has text."));
            layout.Flush();

            doc.AddNewPage();   // deliberately empty: nothing for a text layer to find
        }
        return ms.ToArray();
    }

    private static byte[] PdfWithImage()
    {
        using var png = new MemoryStream();
        using (var img = new Image<Rgba32>(40, 40, new Rgba32(10, 120, 220)))
            img.Save(png, new PngEncoder());

        using var ms = new MemoryStream();
        using (var writer = new PdfWriter(ms))
        using (var doc = new PdfDocument(writer))
        {
            var layout = new Document(doc);
            layout.Add(new Paragraph("Illustrated page."));
            layout.Add(new iText.Layout.Element.Image(ImageDataFactory.Create(png.ToArray())));
            layout.Flush();
        }
        return ms.ToArray();
    }
}
