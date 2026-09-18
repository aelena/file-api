using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Aelena.FileApi.Api.Tests;

/// <summary>
/// End-to-end coverage for the groups added alongside the Open XML formats:
/// <c>/xlsx</c>, <c>/pptx</c>, <c>/csv</c>, the two <c>/txt</c> encoding routes,
/// and <c>/email/parse</c> now that it reads <c>.msg</c> instead of refusing it.
/// </summary>
[Collection("FileApi")]
public class OfficeAndDataEndpointTests(WebApplicationFactory<Program> factory) : FileApiFixture(factory)
{
    private const string Xlsx = "public-domain-pieces.xlsx";
    private const string Pptx = "public-domain-pieces.pptx";

    private static MultipartFormDataContent Upload(byte[] bytes, string name)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", name);
        return form;
    }

    private static MultipartFormDataContent Fixture(string name) =>
        Upload(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "SampleFiles", name)), name);

    private static MultipartFormDataContent Text(string content, string name) =>
        Upload(Encoding.UTF8.GetBytes(content), name);

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        JsonElement.Parse(await response.Content.ReadAsStringAsync());

    // ── XLSX ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Xlsx_Metrics_CountsSheets()
    {
        var response = await Client.PostAsync("/xlsx/metrics", Fixture(Xlsx));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await JsonOf(response)).GetProperty("sheetCount").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Xlsx_Sheet_ResolvesSharedStrings()
    {
        var response = await Client.PostAsync("/xlsx/sheet?sheet=Fables", Fixture(Xlsx));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await response.Content.ReadAsStringAsync()).Should().Contain("The Fox and the Grapes");
    }

    [Fact]
    public async Task Xlsx_UnknownSheet_Is404()
    {
        var response = await Client.PostAsync("/xlsx/sheet?sheet=Nope", Fixture(Xlsx));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Xlsx_Hidden_ReportsWhatTheWorkbookIsNotShowing()
    {
        var response = await Client.PostAsync("/xlsx/hidden", Fixture(Xlsx));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await JsonOf(response);
        json.GetProperty("hiddenSheetCount").GetInt32().Should().Be(1);
        json.GetProperty("hiddenColumnCount").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Xlsx_AuditLinks_ListsHyperlinks()
    {
        var response = await Client.PostAsync("/xlsx/audit-links", Fixture(Xlsx));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await JsonOf(response);
        json.GetProperty("hyperlinkCount").GetInt32().Should().BeGreaterThan(0);
        json.GetProperty("hasMacros").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Xlsx_ToCsv_ReturnsADownload()
    {
        var response = await Client.PostAsync("/xlsx/to-csv?sheet=Fables", Fixture(Xlsx));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");
        (await response.Content.ReadAsStringAsync()).Should().Contain("Title,Author");
    }

    [Fact]
    public async Task Xlsx_NotAWorkbook_Is422()
    {
        var response = await Client.PostAsync("/xlsx/metrics", Text("nope", "x.xlsx"));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableContent);
    }

    // ── PPTX ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pptx_Slides_IncludeSpeakerNotes()
    {
        var response = await Client.PostAsync("/pptx/slides", Fixture(Pptx));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await JsonOf(response);
        json.GetProperty("slideCount").GetInt32().Should().Be(4);
        (await response.Content.ReadAsStringAsync()).Should().Contain("out of copyright");
    }

    [Fact]
    public async Task Pptx_Notes_ReturnsOnlySlidesWithNotes()
    {
        var response = await Client.PostAsync("/pptx/notes", Fixture(Pptx));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await JsonOf(response)).GetProperty("slides").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Pptx_Markdown_OutlinesTheDeck()
    {
        var response = await Client.PostAsync("/pptx/extract-markdown", Fixture(Pptx));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await JsonOf(response)).GetProperty("markdown").GetString()
            .Should().Contain("## 1. Three Public Domain Pieces");
    }

    // ── CSV ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Csv_Inspect_DetectsTheDialect()
    {
        var response = await Client.PostAsync("/csv/inspect", Text("a;b;c\n1;2;3\n", "euro.csv"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await JsonOf(response);
        json.GetProperty("delimiterName").GetString().Should().Be("semicolon");
        json.GetProperty("columnCount").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Csv_Inspect_ReportsRaggedRowsAsAnIssueNotAnError()
    {
        var response = await Client.PostAsync("/csv/inspect", Text("a,b,c\n1,2\n", "bad.csv"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(response)).GetProperty("raggedRowCount").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Csv_Profile_InfersColumnTypes()
    {
        var response = await Client.PostAsync(
            "/csv/profile", Text("name,age\nAda,36\nAlan,41\n", "p.csv"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("integer");
    }

    [Fact]
    public async Task Csv_ToJson_ReturnsADownload()
    {
        var response = await Client.PostAsync("/csv/to-json", Text("a,b\n1,2\n", "x.csv"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    // ── TXT encoding ─────────────────────────────────────────────────────

    [Fact]
    public async Task Txt_DetectEncoding_FindsAControlByteAndLocatesIt()
    {
        var response = await Client.PostAsync(
            "/txt/detect-encoding", Text("line one\nsecond \u0008line\n", "README.md"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await JsonOf(response);
        json.GetProperty("isValidUtf8").GetBoolean().Should().BeTrue();
        json.GetProperty("hasControlBytes").GetBoolean().Should().BeTrue();

        var hit = json.GetProperty("controlBytes")[0];
        hit.GetProperty("byte").GetString().Should().Be("0x08");
        hit.GetProperty("line").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Txt_Normalise_ReturnsCleanText()
    {
        var response = await Client.PostAsync(
            "/txt/normalise", Text("a\u0008b\r\nc\r\n", "x.txt"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("ab\nc\n");
    }

    [Fact]
    public async Task Txt_Normalise_UnknownLineEnding_Is400()
    {
        var response = await Client.PostAsync(
            "/txt/normalise?lineEnding=wobbly", Text("a", "x.txt"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── MSG ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Email_Msg_NoLongerReturns501()
    {
        // A .doc is a valid compound file but not a message, so this is the
        // 422 path rather than the 501 the route used to answer for anything
        // named .msg.
        var response = await Client.PostAsync("/email/parse", Fixture("public-domain-pieces.doc"));

        response.StatusCode.Should().NotBe(HttpStatusCode.NotImplemented);
    }
}
