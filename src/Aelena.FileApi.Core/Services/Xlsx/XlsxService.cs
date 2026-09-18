using System.Globalization;
using System.Text;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Models;
using Aelena.FileApi.Core.Services.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Aelena.FileApi.Core.Services.Xlsx;

/// <summary>
/// Stateless XLSX processing built on the Open XML SDK — the same dependency
/// <c>DocxService</c> uses, so spreadsheets cost this library nothing new.
/// <para>
/// Cell values are returned as the text a reader would see. A spreadsheet
/// stores most strings once in a shared table and refers to them by index, and
/// stores dates as numbers, so "read the cell" is a resolution step rather than
/// a lookup — which is why a naive XML scrape of a workbook returns integers
/// where the user sees words.
/// </para>
/// </summary>
public static class XlsxService
{
    /// <summary>Rows returned by a single sheet read, unless the caller asks for fewer.</summary>
    private const int DefaultRowLimit = 10_000;

    /// <summary>Hard cap on rows returned regardless of what the caller asks for.</summary>
    private const int MaxRowLimit = 100_000;

    /// <summary>
    /// Functions that reach outside the workbook. Legitimate features, and also
    /// the mechanism behind a familiar class of phishing document, so they are
    /// listed rather than left for the recipient to discover on open.
    /// </summary>
    private static readonly string[] RiskyFunctions =
    [
        "WEBSERVICE", "FILTERXML", "RTD", "DDE", "DDEAUTO", "CALL", "REGISTER.ID", "EXEC"
    ];

    // ── Metrics ──────────────────────────────────────────────────────────

    /// <summary>Counts across the whole workbook.</summary>
    public static XlsxMetrics GetMetrics(byte[] data, string fileName)
    {
        using var doc = Open(data);
        var workbookPart = WorkbookOf(doc);
        var sharedStrings = LoadSharedStrings(workbookPart);

        var text = new StringBuilder();
        int totalRows = 0, totalCells = 0, formulas = 0;
        int visible = 0, hidden = 0;

        foreach (var sheet in Sheets(workbookPart))
        {
            if (Visibility(sheet) == "visible") visible++; else hidden++;

            if (WorksheetPartFor(workbookPart, sheet) is not { } part)
                continue;

            foreach (var row in In<Row>(part))
            {
                totalRows++;
                foreach (var cell in row.Elements<Cell>())
                {
                    totalCells++;
                    if (cell.CellFormula is not null) formulas++;

                    var value = CellText(cell, sharedStrings);
                    if (value.Length > 0) text.Append(value).Append(' ');
                }
            }
        }

        var (created, modified) = Dates(doc);
        var body = text.ToString();

        return new XlsxMetrics(
            FileName: fileName,
            FileSizeBytes: data.Length,
            WordCount: TextAnalysis.CountWords(body),
            CharCount: TextAnalysis.CountChars(body),
            TokenCount: TextAnalysis.CountTokens(body),
            Language: TextAnalysis.DetectLanguage(body),
            CreationDate: created,
            LastModifiedDate: modified,
            SheetCount: visible + hidden,
            VisibleSheetCount: visible,
            HiddenSheetCount: hidden,
            TotalRows: totalRows,
            TotalCells: totalCells,
            FormulaCount: formulas,
            DefinedNameCount: workbookPart.Workbook?.DefinedNames?.Count() ?? 0);
    }

    // ── Sheets ───────────────────────────────────────────────────────────

    /// <summary>List the sheets with their shape and visibility.</summary>
    public static XlsxSheetsResponse GetSheets(byte[] data, string fileName)
    {
        using var doc = Open(data);
        var workbookPart = WorkbookOf(doc);
        var sheets = new List<XlsxSheetInfo>();
        var index = 0;

        foreach (var sheet in Sheets(workbookPart))
        {
            var part = WorksheetPartFor(workbookPart, sheet);
            int rows = 0, cells = 0, formulas = 0, columns = 0;

            if (part is not null)
            {
                foreach (var row in In<Row>(part))
                {
                    rows++;
                    var inRow = 0;
                    foreach (var cell in row.Elements<Cell>())
                    {
                        cells++; inRow++;
                        if (cell.CellFormula is not null) formulas++;
                    }
                    columns = Math.Max(columns, inRow);
                }
            }

            sheets.Add(new XlsxSheetInfo(
                Name: sheet.Name?.Value ?? $"Sheet{index + 1}",
                Index: index++,
                Visibility: Visibility(sheet),
                RowCount: rows,
                ColumnCount: columns,
                CellCount: cells,
                FormulaCount: formulas));
        }

        return new XlsxSheetsResponse(fileName, sheets.Count, sheets);
    }

    /// <summary>
    /// Read one sheet's cells as text, by name or by zero-based index.
    /// </summary>
    /// <exception cref="FileApiException">404 when no such sheet exists.</exception>
    public static XlsxSheetDataResponse GetSheet(
        byte[] data, string fileName, string? sheet = null, int? limit = null)
    {
        using var doc = Open(data);
        var workbookPart = WorkbookOf(doc);
        var sharedStrings = LoadSharedStrings(workbookPart);

        var target = ResolveSheet(workbookPart, sheet);
        var part = WorksheetPartFor(workbookPart, target)
            ?? throw new FileApiException(422,
                $"Sheet '{target.Name?.Value}' has no worksheet part; the workbook is malformed.",
                title: "Invalid XLSX");

        var take = Math.Clamp(limit ?? DefaultRowLimit, 1, MaxRowLimit);
        var rows = new List<IReadOnlyList<string>>();
        var width = 0;

        foreach (var row in In<Row>(part))
        {
            if (rows.Count >= take) break;

            // Cells are sparse: an empty cell is simply absent, so the column
            // index has to come from the reference rather than from position.
            var cells = new List<string>();
            foreach (var cell in row.Elements<Cell>())
            {
                var column = ColumnIndex(cell.CellReference?.Value);
                while (cells.Count < column) cells.Add("");
                cells.Add(CellText(cell, sharedStrings));
            }

            width = Math.Max(width, cells.Count);
            rows.Add(cells);
        }

        // Pad every row to the widest, so the caller gets a rectangle.
        var rectangular = rows
            .Select(r => (IReadOnlyList<string>)[.. r, .. Enumerable.Repeat("", width - r.Count)])
            .ToList();

        var headers = rectangular.Count > 0
            ? rectangular[0]
            : (IReadOnlyList<string>)[];

        return new XlsxSheetDataResponse(
            FileName: fileName,
            SheetName: target.Name?.Value ?? "",
            RowCount: rectangular.Count,
            ColumnCount: width,
            Headers: headers,
            Rows: rectangular);
    }

    // ── Conversion ───────────────────────────────────────────────────────

    /// <summary>Render one sheet as CSV.</summary>
    public static (string FileName, byte[] Data) SheetToCsv(
        byte[] data, string fileName, string? sheet = null)
    {
        var parsed = GetSheet(data, fileName, sheet);
        var sb = new StringBuilder();

        foreach (var row in parsed.Rows)
            sb.Append(string.Join(',', row.Select(CsvEscape))).Append('\n');

        return (Rename(fileName, $"_{Slug(parsed.SheetName)}.csv"), Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>Render every sheet as one Markdown document, a table per sheet.</summary>
    public static (string FileName, byte[] Data) ToMarkdown(byte[] data, string fileName)
    {
        using var doc = Open(data);
        var workbookPart = WorkbookOf(doc);
        var sb = new StringBuilder();

        foreach (var sheet in Sheets(workbookPart))
        {
            var name = sheet.Name?.Value ?? "Sheet";
            var parsed = GetSheet(data, fileName, name);

            sb.Append("## ").Append(name).Append("\n\n");

            if (parsed.Rows.Count == 0)
            {
                sb.Append("_(empty)_\n\n");
                continue;
            }

            sb.Append("| ").Append(string.Join(" | ", parsed.Headers.Select(MarkdownEscape))).Append(" |\n");
            sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", parsed.ColumnCount))).Append('\n');

            foreach (var row in parsed.Rows.Skip(1))
                sb.Append("| ").Append(string.Join(" | ", row.Select(MarkdownEscape))).Append(" |\n");

            sb.Append('\n');
        }

        return (Rename(fileName, ".md"), Encoding.UTF8.GetBytes(sb.ToString().TrimEnd() + "\n"));
    }

    // ── Metadata ─────────────────────────────────────────────────────────

    /// <summary>Core and custom properties.</summary>
    public static XlsxMetadataResponse GetMetadata(byte[] data, string fileName)
    {
        using var doc = Open(data);
        var workbookPart = WorkbookOf(doc);
        var props = doc.PackageProperties;

        var custom = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.CustomFilePropertiesPart?.Properties is { } customProps)
        {
            foreach (var p in customProps.OfType<DocumentFormat.OpenXml.CustomProperties.CustomDocumentProperty>())
            {
                if (p.Name?.Value is { Length: > 0 } key)
                    custom[key] = p.InnerText;
            }
        }

        return new XlsxMetadataResponse(
            FileName: fileName,
            Title: NullIfEmpty(props.Title),
            Author: NullIfEmpty(props.Creator),
            Subject: NullIfEmpty(props.Subject),
            Keywords: NullIfEmpty(props.Keywords),
            Category: NullIfEmpty(props.Category),
            Comments: NullIfEmpty(props.Description),
            LastModifiedBy: NullIfEmpty(props.LastModifiedBy),
            Created: props.Created?.ToString("o"),
            Modified: props.Modified?.ToString("o"),
            SheetCount: Sheets(workbookPart).Count(),
            SheetNames: [.. Sheets(workbookPart).Select(s => s.Name?.Value ?? "")],
            CustomMetadata: custom.Count > 0 ? custom : null);
    }

    /// <summary>Strip authorship and revision metadata, keeping the data.</summary>
    public static (string FileName, byte[] Data) RemoveMetadata(byte[] data, string fileName)
    {
        var copy = data.ToArray();
        using var stream = new MemoryStream();
        stream.Write(copy);
        stream.Position = 0;

        using (var doc = SpreadsheetDocument.Open(stream, true))
        {
            var props = doc.PackageProperties;
            props.Title = props.Creator = props.Subject = props.Keywords = "";
            props.Category = props.Description = props.LastModifiedBy = "";
            props.Created = props.Modified = null;

            if (doc.CustomFilePropertiesPart is not null)
                doc.DeletePart(doc.CustomFilePropertiesPart);
        }

        return (Rename(fileName, "_clean.xlsx"), stream.ToArray());
    }

    // ── Link and formula audit ───────────────────────────────────────────

    /// <summary>
    /// Everything in the workbook that reaches outside it: external workbook
    /// references, hyperlinks, formulas calling out to the network or the shell,
    /// and whether the file carries macros.
    /// </summary>
    public static XlsxLinkAuditResponse AuditLinks(byte[] data, string fileName)
    {
        using var doc = Open(data);
        var workbookPart = WorkbookOf(doc);

        var external = new List<string>();
        foreach (var part in workbookPart.ExternalWorkbookParts)
        {
            foreach (var rel in part.ExternalRelationships)
                external.Add(rel.Uri.ToString());
        }

        var hyperlinks = new List<XlsxHyperlink>();
        var risky = new List<XlsxRiskyFormula>();

        foreach (var sheet in Sheets(workbookPart))
        {
            var name = sheet.Name?.Value ?? "";
            if (WorksheetPartFor(workbookPart, sheet) is not { } part) continue;

            foreach (var link in In<Hyperlink>(part))
            {
                var target = link.Id?.Value is { } id
                    && part.HyperlinkRelationships.FirstOrDefault(r => r.Id == id) is { } rel
                        ? rel.Uri.ToString()
                        : link.Location?.Value ?? "";

                hyperlinks.Add(new XlsxHyperlink(name, link.Reference?.Value ?? "", target));
            }

            foreach (var cell in In<Cell>(part))
            {
                if (cell.CellFormula?.Text is not { Length: > 0 } formula) continue;

                foreach (var fn in RiskyFunctions)
                {
                    if (formula.Contains(fn, StringComparison.OrdinalIgnoreCase))
                    {
                        risky.Add(new XlsxRiskyFormula(
                            name, cell.CellReference?.Value ?? "", fn, Truncate(formula, 500)));
                        break;
                    }
                }
            }
        }

        return new XlsxLinkAuditResponse(
            FileName: fileName,
            ExternalWorkbookCount: external.Count,
            ExternalWorkbooks: external,
            HyperlinkCount: hyperlinks.Count,
            Hyperlinks: hyperlinks,
            RiskyFormulaCount: risky.Count,
            RiskyFormulas: risky,
            HasMacros: doc.WorkbookPart?.VbaProjectPart is not null);
    }

    // ── Hidden content ───────────────────────────────────────────────────

    /// <summary>
    /// What the workbook is not showing. Hidden is not deleted: a hidden column
    /// travels with the file and reappears with one right-click, which is a
    /// recurring way of sending data that was believed to be gone.
    /// </summary>
    public static XlsxHiddenResponse FindHidden(byte[] data, string fileName)
    {
        using var doc = Open(data);
        var workbookPart = WorkbookOf(doc);

        var hiddenSheets = new List<XlsxHiddenSheet>();
        var ranges = new List<XlsxHiddenRange>();
        int hiddenRows = 0, hiddenColumns = 0, index = 0;

        foreach (var sheet in Sheets(workbookPart))
        {
            var name = sheet.Name?.Value ?? "";
            var visibility = Visibility(sheet);

            if (visibility != "visible")
                hiddenSheets.Add(new XlsxHiddenSheet(name, index, visibility));

            index++;

            if (WorksheetPartFor(workbookPart, sheet) is not { } part) continue;

            foreach (var row in In<Row>(part))
            {
                if (row.Hidden?.Value == true)
                {
                    hiddenRows++;
                    ranges.Add(new XlsxHiddenRange(
                        name, "row",
                        row.RowIndex?.Value.ToString(CultureInfo.InvariantCulture) ?? "?", 1));
                }
            }

            foreach (var column in In<Column>(part))
            {
                if (column.Hidden?.Value != true) continue;

                var min = column.Min?.Value ?? 0;
                var max = column.Max?.Value ?? min;
                var count = (int)(max - min + 1);

                hiddenColumns += count;
                ranges.Add(new XlsxHiddenRange(
                    name, "column", $"{ColumnName(min)}:{ColumnName(max)}", count));
            }
        }

        return new XlsxHiddenResponse(
            FileName: fileName,
            HiddenSheetCount: hiddenSheets.Count,
            HiddenSheets: hiddenSheets,
            HiddenRowCount: hiddenRows,
            HiddenColumnCount: hiddenColumns,
            HiddenRanges: ranges);
    }

    // ── Health ───────────────────────────────────────────────────────────

    /// <summary>Everything worth knowing before opening or forwarding a workbook.</summary>
    public static XlsxHealthResponse HealthCheck(byte[] data, string fileName)
    {
        var issues = new List<HealthIssue>();

        try
        {
            using var doc = Open(data);
            var workbookPart = WorkbookOf(doc);

            if (workbookPart.VbaProjectPart is not null)
            {
                issues.Add(new HealthIssue("macros", "warning",
                    "The workbook carries a VBA project. Macro content runs on open if the "
                    + "recipient enables it."));
            }

            var audit = AuditLinks(data, fileName);
            if (audit.ExternalWorkbookCount > 0)
            {
                issues.Add(new HealthIssue("external_links", "warning",
                    $"{audit.ExternalWorkbookCount} external workbook reference(s); values may be "
                    + "pulled from files the recipient does not have."));
            }

            if (audit.RiskyFormulaCount > 0)
            {
                issues.Add(new HealthIssue("risky_formulas", "warning",
                    $"{audit.RiskyFormulaCount} formula(s) call out to the network or the shell "
                    + $"({string.Join(", ", audit.RiskyFormulas.Select(f => f.Function).Distinct())})."));
            }

            var hidden = FindHidden(data, fileName);
            if (hidden.HiddenSheetCount > 0 || hidden.HiddenRowCount > 0 || hidden.HiddenColumnCount > 0)
            {
                issues.Add(new HealthIssue("hidden_content", "info",
                    $"{hidden.HiddenSheetCount} hidden sheet(s), {hidden.HiddenRowCount} hidden row(s) "
                    + $"and {hidden.HiddenColumnCount} hidden column(s). Hidden is not removed."));
            }

            if (!Sheets(workbookPart).Any())
                issues.Add(new HealthIssue("structure", "error", "The workbook declares no sheets."));
        }
        catch (FileApiException)
        {
            throw;
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidOperationException
                                      or ArgumentException or FormatException)
        {
            issues.Add(new HealthIssue("corruption", "error", $"Cannot fully parse workbook: {ex.Message}"));
        }

        var errors = issues.Count(i => i.Severity == "error");
        var warnings = issues.Count(i => i.Severity == "warning");

        return new XlsxHealthResponse(
            FileName: fileName,
            Healthy: errors == 0,
            IssueCount: issues.Count,
            ErrorCount: errors,
            WarningCount: warnings,
            InfoCount: issues.Count(i => i.Severity == "info"),
            Issues: issues);
    }

    // ── Search ───────────────────────────────────────────────────────────

    /// <summary>Search every cell of every sheet for literal text or a regex.</summary>
    public static (string FileName, IReadOnlyList<SearchMatch> Matches) Search(
        byte[] data, string fileName, string? query = null, string? pattern = null)
    {
        using var doc = Open(data);
        var workbookPart = WorkbookOf(doc);
        var sharedStrings = LoadSharedStrings(workbookPart);
        var text = new StringBuilder();

        foreach (var sheet in Sheets(workbookPart))
        {
            if (WorksheetPartFor(workbookPart, sheet) is not { } part) continue;

            foreach (var row in In<Row>(part))
            {
                foreach (var cell in row.Elements<Cell>())
                {
                    var value = CellText(cell, sharedStrings);
                    if (value.Length > 0) text.Append(value).Append('\n');
                }
            }
        }

        return (fileName, TextSearch.Search(text.ToString(), query: query, pattern: pattern));
    }

    // ── Plumbing ─────────────────────────────────────────────────────────

    private static SpreadsheetDocument Open(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        try
        {
            return SpreadsheetDocument.Open(new MemoryStream(data, writable: false), false);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException
                                      or ArgumentException or FileFormatException)
        {
            throw new FileApiException(422,
                $"This file is not a readable XLSX workbook: {ex.Message}", title: "Invalid XLSX");
        }
    }

    private static WorkbookPart WorkbookOf(SpreadsheetDocument doc) =>
        doc.WorkbookPart
        ?? throw new FileApiException(422,
            "The package has no workbook part, so it is not a spreadsheet.", title: "Invalid XLSX");

    private static IEnumerable<Sheet> Sheets(WorkbookPart part) =>
        part.Workbook?.Sheets?.Elements<Sheet>() ?? [];

    private static WorksheetPart? WorksheetPartFor(WorkbookPart workbookPart, Sheet sheet) =>
        sheet.Id?.Value is { } id && workbookPart.GetPartById(id) is WorksheetPart part ? part : null;

    /// <summary>Descendants of a worksheet, tolerating a part with no worksheet on it.</summary>
    private static IEnumerable<T> In<T>(WorksheetPart? part) where T : OpenXmlElement =>
        part?.Worksheet is { } sheet ? sheet.Descendants<T>() : [];

    private static Sheet ResolveSheet(WorkbookPart part, string? sheet)
    {
        var all = Sheets(part).ToList();

        if (all.Count == 0)
            throw new FileApiException(422, "The workbook declares no sheets.", title: "Invalid XLSX");

        if (string.IsNullOrWhiteSpace(sheet))
            return all[0];

        var byName = all.Find(s =>
            string.Equals(s.Name?.Value, sheet, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;

        if (int.TryParse(sheet, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < all.Count)
        {
            return all[index];
        }

        throw new FileApiException(404,
            $"No sheet named '{sheet}'. The workbook holds: "
            + string.Join(", ", all.Select(s => s.Name?.Value)) + ".",
            title: "Sheet Not Found");
    }

    /// <summary>
    /// The shared string table, resolved once. Most text in a workbook lives
    /// here and the cells only carry indices into it.
    /// </summary>
    private static List<string> LoadSharedStrings(WorkbookPart part)
    {
        if (part.SharedStringTablePart?.SharedStringTable is not { } table)
            return [];

        return [.. table.Elements<SharedStringItem>().Select(i => i.InnerText)];
    }

    private static string CellText(Cell cell, List<string> sharedStrings)
    {
        var raw = cell.CellValue?.InnerText ?? "";

        if (cell.DataType?.Value == CellValues.SharedString)
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                && i >= 0 && i < sharedStrings.Count
                    ? sharedStrings[i]
                    : "";
        }

        if (cell.DataType?.Value == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? "";

        if (cell.DataType?.Value == CellValues.Boolean)
            return raw == "1" ? "TRUE" : "FALSE";

        // Everything else — numbers, dates, errors — is returned as stored.
        // Applying number formats would mean interpreting the style sheet and
        // guessing a locale, which changes the value the caller gets back.
        return raw.Length > 0 ? raw : cell.InnerText;
    }

    /// <summary>Zero-based column index from a cell reference such as "BQ12".</summary>
    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return 0;

        var index = 0;
        foreach (var ch in reference)
        {
            if (!char.IsAsciiLetter(ch)) break;
            index = index * 26 + char.ToUpperInvariant(ch) - 'A' + 1;
        }

        return Math.Max(0, index - 1);
    }

    /// <summary>Spreadsheet column name from a one-based index: 1 is A, 27 is AA.</summary>
    private static string ColumnName(uint index)
    {
        if (index == 0) return "?";

        var name = new StringBuilder();
        while (index > 0)
        {
            var remainder = (int)((index - 1) % 26);
            name.Insert(0, (char)('A' + remainder));
            index = (index - 1) / 26;
        }

        return name.ToString();
    }

    private static string Visibility(Sheet sheet)
    {
        var state = sheet.State?.Value;
        if (state is null) return "visible";
        if (state == SheetStateValues.Hidden) return "hidden";
        if (state == SheetStateValues.VeryHidden) return "veryHidden";
        return "visible";
    }

    private static (string? Created, string? Modified) Dates(SpreadsheetDocument doc)
    {
        var props = doc.PackageProperties;
        return (props.Created?.ToString("o"), props.Modified?.ToString("o"));
    }

    private static string CsvEscape(string cell) =>
        cell.Contains(',', StringComparison.Ordinal)
        || cell.Contains('"', StringComparison.Ordinal)
        || cell.Contains('\n', StringComparison.Ordinal)
            ? '"' + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
            : cell;

    private static string MarkdownEscape(string cell) =>
        cell.Replace("|", @"\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string Slug(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');

        return sb.Length > 0 ? sb.ToString() : "sheet";
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Rename(string fileName, string suffix) =>
        Path.GetFileNameWithoutExtension(fileName) is { Length: > 0 } stem
            ? stem + suffix
            : "workbook" + suffix;
}
