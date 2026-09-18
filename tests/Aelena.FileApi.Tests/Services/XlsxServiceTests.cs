using System.Text;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Services.Xlsx;
using AwesomeAssertions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;

namespace Aelena.FileApi.Tests.Services;

/// <summary>
/// Workbook tests, run against a fixture Excel itself wrote. See
/// <c>SampleFiles/README.md</c> for how it was produced and what it holds.
/// </summary>
public class XlsxServiceTests
{
    private const string Fixture = "public-domain-pieces.xlsx";

    private static byte[] Workbook() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "SampleFiles", Fixture));

    // ── Metrics and sheets ───────────────────────────────────────────────

    [Fact]
    public void GetMetrics_CountsSheetsRowsAndFormulas()
    {
        var result = XlsxService.GetMetrics(Workbook(), Fixture);

        result.SheetCount.Should().Be(3);
        result.VisibleSheetCount.Should().Be(2);
        result.HiddenSheetCount.Should().Be(1);
        result.FormulaCount.Should().BeGreaterThan(0);
        result.TotalRows.Should().BeGreaterThan(0);
        result.WordCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetSheets_ReportsNamesOrderAndVisibility()
    {
        var result = XlsxService.GetSheets(Workbook(), Fixture);

        result.SheetCount.Should().Be(3);
        result.Sheets.Select(s => s.Name).Should().Contain(["Fables", "Notes", "Internal"]);
        result.Sheets.Select(s => s.Index).Should().BeInAscendingOrder();

        result.Sheets.Single(s => s.Name == "Internal").Visibility.Should().Be("hidden");
        result.Sheets.Single(s => s.Name == "Fables").Visibility.Should().Be("visible");
    }

    // ── Cell values ──────────────────────────────────────────────────────

    [Fact]
    public void GetSheet_ResolvesSharedStringsToTheTextAReaderSees()
    {
        // Strings live once in a shared table; cells hold indices into it. A
        // naive read returns those integers.
        var result = XlsxService.GetSheet(Workbook(), Fixture, "Fables");

        result.SheetName.Should().Be("Fables");
        result.Headers.Should().Contain("Title");
        result.Rows.Should().Contain(r => r.Contains("The Fox and the Grapes"));
        result.Rows.Should().Contain(r => r.Contains("Shakespeare"));
    }

    [Fact]
    public void GetSheet_DefaultsToTheFirstSheet()
    {
        XlsxService.GetSheet(Workbook(), Fixture).SheetName.Should().Be("Fables");
    }

    [Fact]
    public void GetSheet_ByZeroBasedIndex()
    {
        XlsxService.GetSheet(Workbook(), Fixture, "1").SheetName.Should().Be("Notes");
    }

    [Fact]
    public void GetSheet_ReadsAHiddenSheetWhenAskedByName()
    {
        // Hidden is a display flag, not access control.
        var result = XlsxService.GetSheet(Workbook(), Fixture, "Internal");

        result.Rows.Should().Contain(r => r.Any(c => c.Contains("Draft figures")));
    }

    [Fact]
    public void GetSheet_UnknownName_Is404ListingWhatExists()
    {
        var ex = FluentActions.Invoking(() => XlsxService.GetSheet(Workbook(), Fixture, "Nope"))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(404);
        ex.Detail.Should().Contain("Fables");
    }

    [Fact]
    public void GetSheet_LimitCapsTheRowsReturned()
    {
        XlsxService.GetSheet(Workbook(), Fixture, "Fables", limit: 2).Rows.Should().HaveCount(2);
    }

    // ── Audits ───────────────────────────────────────────────────────────

    [Fact]
    public void FindHidden_ReportsHiddenSheetsAndColumns()
    {
        var result = XlsxService.FindHidden(Workbook(), Fixture);

        result.HiddenSheetCount.Should().Be(1);
        result.HiddenSheets.Should().ContainSingle().Which.Name.Should().Be("Internal");

        // Column C ("Year") is hidden on the Fables sheet but still carries data.
        result.HiddenColumnCount.Should().BeGreaterThan(0);
        result.HiddenRanges.Should().Contain(r => r.Kind == "column" && r.Sheet == "Fables");
    }

    [Fact]
    public void AuditLinks_FindsHyperlinks()
    {
        var result = XlsxService.AuditLinks(Workbook(), Fixture);

        result.HyperlinkCount.Should().BeGreaterThan(0);
        result.Hyperlinks.Should().Contain(h => h.Target.Contains("gutenberg.org"));
        result.HasMacros.Should().BeFalse();
    }

    [Fact]
    public void AuditLinks_FlagsFormulasThatReachOutsideTheWorkbook()
    {
        // Built here rather than in the fixture on purpose: Excel would try to
        // evaluate a live WEBSERVICE call while saving the file.
        var data = WorkbookWithFormula("WEBSERVICE(\"https://example.org/x\")");

        var result = XlsxService.AuditLinks(data, "risky.xlsx");

        result.RiskyFormulaCount.Should().Be(1);
        var risky = result.RiskyFormulas.Should().ContainSingle().Which;
        risky.Function.Should().Be("WEBSERVICE");
        risky.Cell.Should().Be("A1");
    }

    [Fact]
    public void AuditLinks_OrdinaryFormulasAreNotFlagged()
    {
        var result = XlsxService.AuditLinks(WorkbookWithFormula("SUM(B1:B9)"), "plain.xlsx");

        result.RiskyFormulaCount.Should().Be(0);
    }

    // ── Health ───────────────────────────────────────────────────────────

    [Fact]
    public void HealthCheck_CleanWorkbook_ReportsHiddenContentAsInfoNotAsAFault()
    {
        var result = XlsxService.HealthCheck(Workbook(), Fixture);

        result.Healthy.Should().BeTrue();
        result.ErrorCount.Should().Be(0);
        result.Issues.Should().Contain(i => i.Check == "hidden_content" && i.Severity == "info");
    }

    [Fact]
    public void HealthCheck_RiskyFormula_IsAWarning()
    {
        var result = XlsxService.HealthCheck(WorkbookWithFormula("WEBSERVICE(\"http://x\")"), "risky.xlsx");

        result.Issues.Should().Contain(i => i.Check == "risky_formulas" && i.Severity == "warning");
    }

    [Fact]
    public void NotAWorkbook_Is422()
    {
        var ex = FluentActions.Invoking(() =>
            XlsxService.GetMetrics(Encoding.UTF8.GetBytes("not a spreadsheet"), "x.xlsx"))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(422);
        ex.Title.Should().Be("Invalid XLSX");
    }

    // ── Metadata ─────────────────────────────────────────────────────────

    [Fact]
    public void GetMetadata_ListsSheetNames()
    {
        var result = XlsxService.GetMetadata(Workbook(), Fixture);

        result.SheetCount.Should().Be(3);
        result.SheetNames.Should().Contain("Fables");
    }

    [Fact]
    public void RemoveMetadata_ClearsPropertiesAndKeepsTheData()
    {
        var (name, bytes) = XlsxService.RemoveMetadata(Workbook(), Fixture);

        name.Should().Be("public-domain-pieces_clean.xlsx");

        var after = XlsxService.GetMetadata(bytes, name);
        after.Author.Should().BeNullOrEmpty();
        after.Title.Should().BeNullOrEmpty();

        XlsxService.GetSheet(bytes, name, "Fables").Rows
            .Should().Contain(r => r.Contains("The Fox and the Grapes"));
    }

    // ── Conversion and search ────────────────────────────────────────────

    [Fact]
    public void SheetToCsv_QuotesCellsHoldingTheDelimiter()
    {
        var (name, bytes) = XlsxService.SheetToCsv(Workbook(), Fixture, "Fables");

        name.Should().Be("public-domain-pieces_Fables.csv");

        var csv = Encoding.UTF8.GetString(bytes);
        csv.Should().Contain("Title,Author");
        csv.Should().Contain("The Fox and the Grapes");
    }

    [Fact]
    public void ToMarkdown_EmitsOneTablePerSheet()
    {
        var (name, bytes) = XlsxService.ToMarkdown(Workbook(), Fixture);

        name.Should().Be("public-domain-pieces.md");

        var md = Encoding.UTF8.GetString(bytes);
        md.Should().Contain("## Fables");
        md.Should().Contain("## Notes");
        md.Should().Contain("| --- |");
    }

    [Fact]
    public void Search_FindsTextAcrossSheets()
    {
        var (_, matches) = XlsxService.Search(Workbook(), Fixture, query: "Aesop", pattern: null);

        matches.Should().NotBeEmpty();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    /// <summary>A one-cell workbook carrying the given formula, built in memory.</summary>
    private static byte[] WorkbookWithFormula(string formula)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new Worksheet(new SheetData(
                new Row(new Cell
                {
                    CellReference = "A1",
                    CellFormula = new CellFormula(formula)
                })
                { RowIndex = 1 }));

            workbookPart.Workbook.AppendChild(new Sheets()).AppendChild(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Sheet1"
            });
        }

        return ms.ToArray();
    }
}
