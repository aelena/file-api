using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Aelena.FileApi.Api.Tests;

[Collection("FileApi")]
public class Phase6EndpointTests(WebApplicationFactory<Program> factory) : FileApiFixture(factory)
{
    private static MultipartFormDataContent CreateFile(string content, string name, string field = "file")
    {
        var form = new MultipartFormDataContent();
        var fc = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        fc.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fc, field, name);
        return form;
    }

    [Fact]
    public async Task PiiDetect_FindsEmail()
    {
        using var form = CreateFile("Contact john@example.com for details.", "doc.txt");
        var response = await Client.PostAsync("/pii/detect", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("totalMatches").GetInt32().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Compare_ReturnsAccepted()
    {
        var form = new MultipartFormDataContent();
        var f1 = new ByteArrayContent("doc A content"u8.ToArray());
        f1.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(f1, "file_a", "a.txt");
        var f2 = new ByteArrayContent("doc B content"u8.ToArray());
        f2.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(f2, "file_b", "b.txt");

        var response = await Client.PostAsync("/compare", form);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("jobId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Summarize_ReturnsAccepted()
    {
        using var form = CreateFile("Long document to summarize.", "doc.txt");
        var response = await Client.PostAsync("/summarize", form);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task GeospatialFormats_ReturnsOk()
    {
        var response = await Client.GetAsync("/geospatial/formats");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Classify_ReturnsNotImplemented()
    {
        using var form = CreateFile("Some content", "doc.txt");
        var response = await Client.PostAsync("/classify", form);
        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task VideoMetadata_ReturnsNotImplemented()
    {
        using var form = CreateFile("fake video", "clip.mp4");
        var response = await Client.PostAsync("/video/metadata", form);
        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

#if INCLUDE_PDF
    [Fact]
    public async Task MarkdownToPdf_RendersRatherThanReturning501()
    {
        // This pinned the stub the route used to be. It now typesets through
        // MarkdownPdfService; the rendering itself is covered by
        // MarkdownPdfServiceTests.
        using var form = CreateFile("# Hello", "doc.md");
        var response = await Client.PostAsync("/markdown/to-pdf", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/pdf");
    }
#else
    [Fact]
    public async Task MarkdownToPdf_Is501WithoutThePdfPackage()
    {
        // The renderer is built on iText. Without it the route stays and says
        // what is missing, rather than 404-ing as if it had never existed.
        using var form = CreateFile("# Hello", "doc.md");
        var response = await Client.PostAsync("/markdown/to-pdf", form);

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        (await response.Content.ReadAsStringAsync()).Should().Contain("IncludePdf=false");
    }
#endif
}
