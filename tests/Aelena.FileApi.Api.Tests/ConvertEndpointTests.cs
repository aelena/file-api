using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Aelena.FileApi.Api.Tests;

/// <summary>
/// End-to-end tests for <c>/convert/*</c> over the real HTTP surface, against
/// the public-domain fixtures in <c>SampleFiles/</c>.
/// <para>
/// The status codes are the point of most of these. This family's contract is
/// that a caller can tell "wrong endpoint" (415), "broken file" (422) and
/// "readable file, undecodable content" (501) apart from the response alone,
/// and that contract is only real if it is pinned here.
/// </para>
/// </summary>
[Collection("FileApi")]
public class ConvertEndpointTests(WebApplicationFactory<Program> factory) : FileApiFixture(factory)
{
    private const string Epub = "public-domain-pieces.epub";
    private const string Mobi = "public-domain-pieces.mobi";
    private const string Doc = "public-domain-pieces.doc";
    private const string DjvuWithText = "un-resolution-1837.djvu";
    private const string DjvuNoText = "hr-report-94-1476-p249.djvu";

    private static MultipartFormDataContent Upload(string fixture)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "SampleFiles", fixture));
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", fixture);
        return form;
    }

    private static MultipartFormDataContent Upload(byte[] bytes, string name)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", name);
        return form;
    }

    private static async Task<JsonDocument> JsonOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    // ── Detect and validate ──────────────────────────────────────────────

    [Theory]
    [InlineData(Epub, "EPUB")]
    [InlineData(Mobi, "MOBI")]
    [InlineData(Doc, "DOC (Word 97-2003)")]
    [InlineData(DjvuWithText, "DjVu")]
    public async Task Detect_NamesTheFormat(string fixture, string expected)
    {
        var response = await Client.PostAsync("/convert/detect", Upload(fixture));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await JsonOf(response);
        json.RootElement.GetProperty("format").GetString().Should().Be(expected);
        json.RootElement.GetProperty("supported").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(Epub)]
    [InlineData(Mobi)]
    [InlineData(Doc)]
    [InlineData(DjvuWithText)]
    [InlineData(DjvuNoText)]
    public async Task Validate_EveryFixtureIsStructurallySound(string fixture)
    {
        var response = await Client.PostAsync("/convert/validate", Upload(fixture));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await JsonOf(response);
        json.RootElement.GetProperty("valid").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("errorCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Validate_BrokenFile_Is200WithTheIssuesRatherThanAnError()
    {
        // Validation reports; it does not refuse. A caller asking "is this file
        // any good?" should get an answer, not an exception.
        var truncated = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "SampleFiles", Mobi))[..90];

        var response = await Client.PostAsync("/convert/validate", Upload(truncated, Mobi));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await JsonOf(response);
        json.RootElement.GetProperty("valid").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("errorCount").GetInt32().Should().BeGreaterThan(0);
    }

    // ── Extraction ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(Epub)]
    [InlineData(Mobi)]
    [InlineData(Doc)]
    public async Task Text_ReturnsTheDocumentContent(string fixture)
    {
        var response = await Client.PostAsync("/convert/text", Upload(fixture));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await JsonOf(response);
        json.RootElement.GetProperty("wordCount").GetInt32().Should().BeGreaterThan(100);

        var sections = json.RootElement.GetProperty("sections");
        sections.GetArrayLength().Should().BeGreaterThan(0);

        var all = string.Join("\n", sections.EnumerateArray().Select(s => s.GetProperty("text").GetString()));
        all.Should().Contain("Shall I compare thee to a summer's day?");
    }

    [Theory]
    [InlineData(Epub)]
    [InlineData(Mobi)]
    [InlineData(Doc)]
    public async Task Metadata_IsAvailableForEveryReadableFormat(string fixture)
    {
        var response = await Client.PostAsync("/convert/metadata", Upload(fixture));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await JsonOf(response);
        json.RootElement.GetProperty("format").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Metadata_WorksOnADjvuWithNoTextLayer()
    {
        // Metadata does not depend on there being text; this is the regression
        // guard for routing it through the extraction path.
        var response = await Client.PostAsync("/convert/metadata", Upload(DjvuNoText));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await JsonOf(response);
        json.RootElement.GetProperty("format").GetString().Should().Be("DjVu");
        json.RootElement.GetProperty("sectionCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Markdown_KeepsStructure()
    {
        var response = await Client.PostAsync("/convert/markdown", Upload(Epub));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await JsonOf(response);
        json.RootElement.GetProperty("markdown").GetString()
            .Should().Contain("# Three Public Domain Pieces");
    }

    // ── Downloads ────────────────────────────────────────────────────────

    [Fact]
    public async Task ToTxt_ReturnsAPlainTextDownload()
    {
        var response = await Client.PostAsync("/convert/to-txt", Upload(Mobi));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/plain");
        (await response.Content.ReadAsStringAsync()).Should().Contain("The grapes are sour");
    }

    [Fact]
    public async Task ToMd_ReturnsAMarkdownDownload()
    {
        var response = await Client.PostAsync("/convert/to-md", Upload(Epub));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/markdown");
        (await response.Content.ReadAsStringAsync()).Should().Contain("Sonnet XVIII");
    }

#if INCLUDE_PDF
    // PDF rendering ships in the AGPL package, so this route — and this test —
    // are compiled out by -p:IncludePdf=false. Everything above still runs.
    [Theory]
    [InlineData(Epub)]
    [InlineData(Mobi)]
    [InlineData(Doc)]
    public async Task ToPdf_ReturnsARenderedPdf(string fixture)
    {
        var response = await Client.PostAsync("/convert/to-pdf", Upload(fixture));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/pdf");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
    }
#else
    [Fact]
    public async Task ToPdf_IsAbsentFromTheMitOnlyBuild()
    {
        var response = await Client.PostAsync("/convert/to-pdf", Upload(Epub));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
#endif

    // ── Error contract ───────────────────────────────────────────────────

    [Fact]
    public async Task UnsupportedFormat_Is415()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n% not a real document\n");

        var response = await Client.PostAsync("/convert/text", Upload(pdf, "report.pdf"));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await response.Content.ReadAsStringAsync()).Should().Contain("/pdf/*");
    }

    [Fact]
    public async Task BrokenContainer_Is422()
    {
        var truncated = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "SampleFiles", DjvuWithText))[..5000];

        var response = await Client.PostAsync("/convert/text", Upload(truncated, DjvuWithText));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableContent);
    }

    [Fact]
    public async Task UndecodableTextLayer_Is501()
    {
        var response = await Client.PostAsync("/convert/text", Upload(DjvuWithText));

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        (await response.Content.ReadAsStringAsync()).Should().Contain("djvutxt");
    }

    [Fact]
    public async Task NoTextLayerAtAll_Is422AndSaysSo()
    {
        var response = await Client.PostAsync("/convert/text", Upload(DjvuNoText));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableContent);
        (await response.Content.ReadAsStringAsync()).Should().Contain("no text layer");
    }

    [Fact]
    public async Task EveryErrorIsProblemDetails()
    {
        var response = await Client.PostAsync("/convert/text", Upload([1, 2, 3, 4, 5, 6, 7, 8], "junk.bin"));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);

        using var json = await JsonOf(response);
        json.RootElement.GetProperty("status").GetInt32().Should().Be(415);
        json.RootElement.GetProperty("title").GetString().Should().Be("Unsupported Media Type");
        json.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrEmpty();
    }
}
