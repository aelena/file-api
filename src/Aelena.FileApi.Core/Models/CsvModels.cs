namespace Aelena.FileApi.Core.Models;

/// <summary>
/// What a delimited file turned out to be, and whether it will load cleanly.
/// <para>
/// Every field here is inferred from the bytes rather than assumed. A file
/// called <c>.csv</c> is semicolon-delimited about as often as it is
/// comma-delimited, and the caller usually does not know which they have.
/// </para>
/// </summary>
public sealed record CsvInspectResponse(
    string FileName,
    long FileSizeBytes,
    string Delimiter,
    string DelimiterName,
    string QuoteChar,
    string LineEnding,
    string Encoding,
    bool HasBom,
    bool HasHeader,
    int RowCount,
    int DataRowCount,
    int ColumnCount,
    IReadOnlyList<string> Headers,
    int RaggedRowCount,
    int IssueCount,
    IReadOnlyList<HealthIssue> Issues);

/// <summary>What one column holds, inferred from its values.</summary>
public sealed record CsvColumnProfile(
    int Index,
    string Name,
    string InferredType,
    int NonEmptyCount,
    int EmptyCount,
    int DistinctCount,
    string? Min,
    string? Max,
    IReadOnlyList<string> SampleValues);

/// <summary>Column-by-column profile of a delimited file.</summary>
public sealed record CsvProfileResponse(
    string FileName,
    int RowCount,
    int DataRowCount,
    int ColumnCount,
    IReadOnlyList<CsvColumnProfile> Columns);

/// <summary>Parsed rows of a delimited file.</summary>
public sealed record CsvRowsResponse(
    string FileName,
    int RowCount,
    int ColumnCount,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows);
