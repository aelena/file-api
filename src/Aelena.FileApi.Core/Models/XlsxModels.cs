namespace Aelena.FileApi.Core.Models;

/// <summary>Metrics for a workbook.</summary>
public sealed record XlsxMetrics(
    string FileName,
    long FileSizeBytes,
    int WordCount,
    int CharCount,
    int TokenCount,
    string? Language,
    string? CreationDate,
    string? LastModifiedDate,
    int SheetCount,
    int VisibleSheetCount,
    int HiddenSheetCount,
    int TotalRows,
    int TotalCells,
    int FormulaCount,
    int DefinedNameCount) : BaseMetrics(
        FileName, FileSizeBytes, WordCount, CharCount, TokenCount,
        Language, CreationDate, LastModifiedDate);

/// <summary>One worksheet, as the workbook describes it.</summary>
public sealed record XlsxSheetInfo(
    string Name,
    int Index,
    string Visibility,
    int RowCount,
    int ColumnCount,
    int CellCount,
    int FormulaCount);

/// <summary>The sheets a workbook holds, in workbook order.</summary>
public sealed record XlsxSheetsResponse(
    string FileName,
    int SheetCount,
    IReadOnlyList<XlsxSheetInfo> Sheets);

/// <summary>The cells of one worksheet, as text.</summary>
public sealed record XlsxSheetDataResponse(
    string FileName,
    string SheetName,
    int RowCount,
    int ColumnCount,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Core and custom properties from a workbook.</summary>
public sealed record XlsxMetadataResponse(
    string FileName,
    string? Title = null,
    string? Author = null,
    string? Subject = null,
    string? Keywords = null,
    string? Category = null,
    string? Comments = null,
    string? LastModifiedBy = null,
    string? Created = null,
    string? Modified = null,
    int SheetCount = 0,
    IReadOnlyList<string>? SheetNames = null,
    IReadOnlyDictionary<string, string>? CustomMetadata = null);

/// <summary>
/// Everything in a workbook that reaches outside it.
/// <para>
/// External workbook references, <c>WEBSERVICE</c>, <c>DDE</c> and friends are
/// how a spreadsheet phones home or pulls content from somewhere the recipient
/// did not choose. They are legitimate features and also the mechanism behind a
/// familiar class of phishing document, which is why they are worth listing
/// before a file is opened rather than after.
/// </para>
/// </summary>
public sealed record XlsxLinkAuditResponse(
    string FileName,
    int ExternalWorkbookCount,
    IReadOnlyList<string> ExternalWorkbooks,
    int HyperlinkCount,
    IReadOnlyList<XlsxHyperlink> Hyperlinks,
    int RiskyFormulaCount,
    IReadOnlyList<XlsxRiskyFormula> RiskyFormulas,
    bool HasMacros);

/// <summary>A hyperlink on a cell.</summary>
public sealed record XlsxHyperlink(
    string Sheet,
    string Cell,
    string Target);

/// <summary>A formula that reaches outside the workbook.</summary>
public sealed record XlsxRiskyFormula(
    string Sheet,
    string Cell,
    string Function,
    string Formula);

/// <summary>
/// What a workbook is hiding. Hidden is not deleted: a hidden column travels
/// with the file and opens with one right-click, which is a recurring way of
/// leaking data that was believed to be gone.
/// </summary>
public sealed record XlsxHiddenResponse(
    string FileName,
    int HiddenSheetCount,
    IReadOnlyList<XlsxHiddenSheet> HiddenSheets,
    int HiddenRowCount,
    int HiddenColumnCount,
    IReadOnlyList<XlsxHiddenRange> HiddenRanges);

/// <summary>A sheet the workbook does not show.</summary>
public sealed record XlsxHiddenSheet(
    string Name,
    int Index,
    string Visibility);

/// <summary>A run of hidden rows or columns on one sheet.</summary>
public sealed record XlsxHiddenRange(
    string Sheet,
    string Kind,
    string Range,
    int Count);

/// <summary>Health report for a workbook.</summary>
public sealed record XlsxHealthResponse(
    string FileName,
    bool Healthy,
    int IssueCount,
    int ErrorCount,
    int WarningCount,
    int InfoCount,
    IReadOnlyList<HealthIssue> Issues);
