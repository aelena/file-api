using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using iText.Kernel.Pdf;
using Microsoft.AspNetCore.Mvc.Testing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Aelena.FileApi.Api.Tests;

/// <summary>
/// Smoke tests that exercise every endpoint family with real file formats.
/// Verifies HTTP status codes and basic response structure.
/// </summary>
[Collection("FileApi")]
public class AllEndpointSmokeTests(WebApplicationFactory<Program> factory) : FileApiFixture(factory)
{
    // ── Helpers ───────────────────────────────────────────────────────────

    private static MultipartFormDataContent TextFile(string content, string name) =>
        FileContent(Encoding.UTF8.GetBytes(content), name);

    private static MultipartFormDataContent FileContent(byte[] data, string name, string field = "file")
    {
        var form = new MultipartFormDataContent();
        var fc = new ByteArrayContent(data);
        fc.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fc, field, name);
        return form;
    }

    private static byte[] MiniDocx()
    {
        using var ms = new MemoryStream();
        using var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
            new Body(new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                new Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Hello DOCX world.")))));
        doc.PackageProperties.Title = "Test";
        doc.Dispose();
        return ms.ToArray();
    }

    private static byte[] MiniPng()
    {
        using var img = new Image<Rgba32>(50, 50, new Rgba32(0, 128, 255));
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static byte[] MiniZip()
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var e = z.CreateEntry("hello.txt");
            using var sw = new StreamWriter(e.Open());
            sw.Write("hi");
        }
        return ms.ToArray();
    }

    private static byte[] MiniEml() => Encoding.UTF8.GetBytes(
        "From: a@b.com\r\nTo: c@d.com\r\nSubject: Test\r\n\r\nBody text.");

#if INCLUDE_PDF

    private static byte[] MiniPdf()
    {
        using var ms = new MemoryStream();
        using var w = new PdfWriter(ms);
        using var pdfDoc = new PdfDocument(w);
        using var layout = new iText.Layout.Document(pdfDoc);
        layout.Add(new iText.Layout.Element.Paragraph("Hello PDF world. The quick brown fox."));
        layout.Close();
        return ms.ToArray();
    }

    // ── PDF ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/pdf/metrics")]
    [InlineData("/pdf/metadata")]
    [InlineData("/pdf/form-fields")]
    [InlineData("/pdf/health")]
    [InlineData("/pdf/extract-text")]
    [InlineData("/pdf/extract-markdown")]
    [InlineData("/pdf/extract-annotations")]
    [InlineData("/pdf/extract-bookmarks")]
    public async Task Pdf_ReadEndpoints_ReturnOk(string path)
    {
        using var form = FileContent(MiniPdf(), "test.pdf");
        var r = await Client.PostAsync(path, form);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Pdf_Search_FindsText()
    {
        using var form = FileContent(MiniPdf(), "test.pdf");
        var r = await Client.PostAsync("/pdf/search?query=fox", form);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("totalMatches").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Pdf_Rotate_ReturnsFile()
    {
        using var form = FileContent(MiniPdf(), "test.pdf");
        var r = await Client.PostAsync("/pdf/rotate?angle=90", form);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentType?.MediaType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task Pdf_Encrypt_ReturnsFile()
    {
        using var form = FileContent(MiniPdf(), "test.pdf");
        var r = await Client.PostAsync("/pdf/encrypt?userPassword=test123", form);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
    }
#endif

    // ── DOCX ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/docx/metrics")]
    [InlineData("/docx/metadata")]
    [InlineData("/docx/health")]
    [InlineData("/docx/extract-markdown")]
    public async Task Docx_ReadEndpoints_ReturnOk(string path)
    {
        using var form = FileContent(MiniDocx(), "test.docx");
        var r = await Client.PostAsync(path, form);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Docx_RemoveMetadata_ReturnsFile()
    {
        using var form = FileContent(MiniDocx(), "test.docx");
        var r = await Client.PostAsync("/docx/remove-metadata", form);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentType?.MediaType.Should().Contain("wordprocessingml");
    }

    // ── Image ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/image/exif")]
    [InlineData("/image-ai/color-palette")]
    [InlineData("/image-ai/base64")]
    public async Task Image_JsonEndpoints_ReturnOk(string path)
    {
        using var form = FileContent(MiniPng(), "test.png");
        var r = await Client.PostAsync(path, form);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/image/resize?width=25")]
    [InlineData("/image/rotate?angle=90")]
    [InlineData("/image/grayscale")]
    [InlineData("/image/blur?radius=2")]
    [InlineData("/image/flip")]
    [InlineData("/image/thumbnail")]
    [InlineData("/image/compress")]
    [InlineData("/image/convert?format=jpeg")]
    [InlineData("/image/strip-metadata")]
    [InlineData("/image-ai/auto-orient")]
    [InlineData("/image-ai/edge-detect")]
    [InlineData("/image-ai/invert")]
    [InlineData("/image-ai/equalize")]
    public async Task Image_BinaryEndpoints_ReturnFile(string path)
    {
        using var form = FileContent(MiniPng(), "test.png");
        var r = await Client.PostAsync(path, form);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(0);
    }

    // ── Text / Utility ───────────────────────────────────────────────────

    [Fact]
    public async Task Hash_ReturnsOk()
    {
        using var form = TextFile("hello", "test.bin");
        (await Client.PostAsync("/hash", form)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TxtMetrics_ReturnsOk()
    {
        using var form = TextFile("line one\nline two", "doc.txt");
        (await Client.PostAsync("/txt/metrics", form)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ZipInspect_ReturnsOk()
    {
        using var form = FileContent(MiniZip(), "test.zip");
        (await Client.PostAsync("/zip/inspect", form)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readability_ReturnsOk()
    {
        using var form = TextFile("The cat sat on the mat. The dog barked loudly.", "doc.txt");
        (await Client.PostAsync("/readability", form)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PiiDetect_ReturnsOk()
    {
        using var form = TextFile("Email: a@b.com SSN: 123-45-6789", "doc.txt");
        (await Client.PostAsync("/pii/detect", form)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task EmailParse_ReturnsOk()
    {
        using var form = FileContent(MiniEml(), "msg.eml");
        var r = await Client.PostAsync("/email/parse", form);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("subject").GetString().Should().Be("Test");
    }

    [Fact]
    public async Task Search_ReturnsOk()
    {
        using var form = TextFile("Find the needle in the haystack.", "doc.txt");
        (await Client.PostAsync("/search?query=needle", form)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Async Jobs ───────────────────────────────────────────────────────

    [Fact]
    public async Task Compare_And_Poll_ReturnsProcessing()
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent("doc A"u8.ToArray()), "file_a", "a.txt" },
            { new ByteArrayContent("doc B"u8.ToArray()), "file_b", "b.txt" }
        };

        var r = await Client.PostAsync("/compare", form);
        r.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var json = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync());
        var jobId = json.RootElement.GetProperty("jobId").GetString()!;

        // Poll
        var poll = await Client.GetAsync($"/compare/{jobId}");
        poll.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Compare_UnknownJob_Returns404()
    {
        var r = await Client.GetAsync("/compare/nonexistent");
        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
