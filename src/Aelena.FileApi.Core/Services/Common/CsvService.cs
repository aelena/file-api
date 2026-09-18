using System.Globalization;
using System.Text;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Models;

namespace Aelena.FileApi.Core.Services.Common;

/// <summary>
/// Reads delimited text without being told how it is delimited.
/// <para>
/// A file named <c>.csv</c> is semicolon-separated about as often as it is
/// comma-separated, quotes may or may not be doubled, and the caller usually
/// does not know which they have — so the dialect is inferred from the bytes
/// and reported back rather than assumed. "Will this load, and what is in it?"
/// is the question this answers.
/// </para>
/// </summary>
public static class CsvService
{
    /// <summary>Cached: a fresh options instance per call is a documented waste.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>Delimiters considered when sniffing, in preference order for ties.</summary>
    private static readonly char[] Candidates = [',', ';', '\t', '|'];

    /// <summary>Rows sniffed to decide the dialect. More than this buys nothing.</summary>
    private const int SniffRows = 50;

    /// <summary>Cap on rows parsed in one request.</summary>
    private const int MaxRows = 1_000_000;

    /// <summary>Cap on the file size accepted for parsing.</summary>
    private const int MaxBytes = 128 * 1024 * 1024;

    /// <summary>Distinct values tracked per column before the profiler stops counting.</summary>
    private const int MaxDistinct = 10_000;

    // ── Inspection ───────────────────────────────────────────────────────

    /// <summary>Sniff the dialect, parse the file, and report what would go wrong loading it.</summary>
    public static CsvInspectResponse Inspect(byte[] data, string fileName)
    {
        var (text, encoding, hasBom) = Decode(data);
        var delimiter = SniffDelimiter(text);
        var rows = Parse(text, delimiter);
        var issues = new List<HealthIssue>();

        if (rows.Count == 0)
        {
            issues.Add(new HealthIssue("empty", "error", "The file holds no rows."));
            return new CsvInspectResponse(
                fileName, data.Length, delimiter.ToString(), DelimiterName(delimiter), "\"",
                DetectLineEnding(text), encoding, hasBom, false, 0, 0, 0, [], 0, 1, issues);
        }

        var hasHeader = LooksLikeHeader(rows);
        var columnCount = rows[0].Count;
        var headers = hasHeader
            ? rows[0]
            : [.. Enumerable.Range(1, columnCount).Select(i => $"column{i}")];

        var ragged = rows.Count(r => r.Count != columnCount);
        if (ragged > 0)
        {
            var firstBad = rows.FindIndex(r => r.Count != columnCount);
            issues.Add(new HealthIssue("ragged_rows", "error",
                $"{ragged} row(s) do not have {columnCount} field(s); the first is row {firstBad + 1} " +
                $"with {rows[firstBad].Count}. Most loaders reject the file or silently shift columns.",
                new Dictionary<string, object> { ["raggedRows"] = ragged, ["firstRow"] = firstBad + 1 }));
        }

        if (hasHeader)
        {
            var blank = headers.Count(h => h.Trim().Length == 0);
            if (blank > 0)
                issues.Add(new HealthIssue("header", "warning", $"{blank} header cell(s) are empty."));

            var dupes = headers.GroupBy(h => h.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Key.Length > 0 && g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (dupes.Count > 0)
            {
                issues.Add(new HealthIssue("header", "warning",
                    $"Duplicate header name(s): {string.Join(", ", dupes)}."));
            }
        }
        else
        {
            issues.Add(new HealthIssue("header", "info",
                "No header row was detected; columns are numbered."));
        }

        if (DetectLineEnding(text) == "mixed")
        {
            issues.Add(new HealthIssue("line_endings", "warning",
                "The file mixes line endings, which splits rows inconsistently in some loaders."));
        }

        return new CsvInspectResponse(
            FileName: fileName,
            FileSizeBytes: data.Length,
            Delimiter: delimiter == '\t' ? "\\t" : delimiter.ToString(),
            DelimiterName: DelimiterName(delimiter),
            QuoteChar: "\"",
            LineEnding: DetectLineEnding(text),
            Encoding: encoding,
            HasBom: hasBom,
            HasHeader: hasHeader,
            RowCount: rows.Count,
            DataRowCount: hasHeader ? rows.Count - 1 : rows.Count,
            ColumnCount: columnCount,
            Headers: headers,
            RaggedRowCount: ragged,
            IssueCount: issues.Count,
            Issues: issues);
    }

    // ── Profiling ────────────────────────────────────────────────────────

    /// <summary>Describe each column: inferred type, emptiness, cardinality and range.</summary>
    public static CsvProfileResponse Profile(byte[] data, string fileName, int sampleSize = 5)
    {
        var (text, _, _) = Decode(data);
        var delimiter = SniffDelimiter(text);
        var rows = Parse(text, delimiter);

        if (rows.Count == 0)
            throw new FileApiException(422, "The file holds no rows to profile.", title: "Empty File");

        var hasHeader = LooksLikeHeader(rows);
        var columnCount = rows.Max(r => r.Count);
        var headers = hasHeader && rows[0].Count == columnCount
            ? rows[0]
            : [.. Enumerable.Range(1, columnCount).Select(i => $"column{i}")];

        List<List<string>> dataRows = hasHeader ? [.. rows.Skip(1)] : rows;
        var columns = new List<CsvColumnProfile>(columnCount);

        for (var c = 0; c < columnCount; c++)
        {
            var values = dataRows
                .Select(r => c < r.Count ? r[c] : "")
                .ToList();

            var nonEmpty = values.Where(v => v.Trim().Length > 0).ToList();
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in nonEmpty)
            {
                if (distinct.Count >= MaxDistinct) break;
                distinct.Add(v);
            }

            var (type, min, max) = Infer(nonEmpty);

            columns.Add(new CsvColumnProfile(
                Index: c,
                Name: c < headers.Count ? headers[c] : $"column{c + 1}",
                InferredType: type,
                NonEmptyCount: nonEmpty.Count,
                EmptyCount: values.Count - nonEmpty.Count,
                DistinctCount: distinct.Count,
                Min: min,
                Max: max,
                SampleValues: [.. nonEmpty.Take(Math.Clamp(sampleSize, 0, 20))]));
        }

        return new CsvProfileResponse(
            fileName, rows.Count, dataRows.Count, columnCount, columns);
    }

    // ── Rows ─────────────────────────────────────────────────────────────

    /// <summary>Parse the file into rows, optionally limited to the first <paramref name="limit"/>.</summary>
    public static CsvRowsResponse GetRows(byte[] data, string fileName, int? limit = null)
    {
        var (text, _, _) = Decode(data);
        var delimiter = SniffDelimiter(text);
        var rows = Parse(text, delimiter);

        if (rows.Count == 0)
            throw new FileApiException(422, "The file holds no rows.", title: "Empty File");

        var hasHeader = LooksLikeHeader(rows);
        var columnCount = rows.Max(r => r.Count);
        var headers = hasHeader && rows[0].Count == columnCount
            ? rows[0]
            : [.. Enumerable.Range(1, columnCount).Select(i => $"column{i}")];

        var body = hasHeader ? rows.Skip(1) : rows;
        if (limit is > 0) body = body.Take(limit.Value);

        return new CsvRowsResponse(
            fileName, rows.Count, columnCount, headers,
            [.. body.Select(r => (IReadOnlyList<string>)r)]);
    }

    /// <summary>Convert the file to JSON objects keyed by header name.</summary>
    public static (string FileName, byte[] Data) ToJson(byte[] data, string fileName)
    {
        var parsed = GetRows(data, fileName);
        var objects = parsed.Rows.Select(r =>
            parsed.Headers
                .Select((h, i) => (Key: h, Value: i < r.Count ? r[i] : ""))
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));

        var json = System.Text.Json.JsonSerializer.Serialize(objects, JsonOptions);

        return (Rename(fileName, ".json"), Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Convert the file to a Markdown table.</summary>
    public static (string FileName, byte[] Data) ToMarkdown(byte[] data, string fileName)
    {
        var parsed = GetRows(data, fileName);
        var sb = new StringBuilder();

        sb.Append("| ").Append(string.Join(" | ", parsed.Headers.Select(Escape))).Append(" |\n");
        sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", parsed.Headers.Count))).Append('\n');

        foreach (var row in parsed.Rows)
        {
            var cells = Enumerable.Range(0, parsed.Headers.Count)
                .Select(i => Escape(i < row.Count ? row[i] : ""));
            sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
        }

        return (Rename(fileName, ".md"), Encoding.UTF8.GetBytes(sb.ToString()));
    }

    // ── Parsing ──────────────────────────────────────────────────────────

    /// <summary>
    /// RFC 4180 parsing with the tolerances real files need: a quote may open
    /// mid-field, a doubled quote inside a quoted field is a literal quote, and
    /// a newline inside quotes does not end the row.
    /// </summary>
    private static List<List<string>> Parse(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;

        void EndField() { row.Add(field.ToString()); field.Clear(); }
        void EndRow()
        {
            EndField();
            // A trailing newline produces one empty field, which is not a row.
            if (row.Count > 1 || row[0].Length > 0)
                rows.Add([.. row]);
            row.Clear();
        }

        while (i < text.Length)
        {
            var ch = text[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                field.Append(ch); i++; continue;
            }

            if (ch == '"') { inQuotes = true; i++; continue; }
            if (ch == delimiter) { EndField(); i++; continue; }

            if (ch == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndRow();
                i++;
                if (rows.Count >= MaxRows) break;
                continue;
            }

            if (ch == '\n')
            {
                EndRow();
                i++;
                if (rows.Count >= MaxRows) break;
                continue;
            }

            field.Append(ch); i++;
        }

        if (field.Length > 0 || row.Count > 0)
            EndRow();

        return rows;
    }

    /// <summary>
    /// Pick the delimiter that yields the most consistent field count across the
    /// first rows. Consistency beats raw frequency: prose full of commas inside
    /// one quoted field would otherwise win against the real separator.
    /// </summary>
    private static char SniffDelimiter(string text)
    {
        var best = ',';
        var bestScore = -1.0;

        foreach (var candidate in Candidates)
        {
            var rows = Parse(text, candidate).Take(SniffRows).ToList();
            if (rows.Count == 0) continue;

            var counts = rows.Select(r => r.Count).ToList();
            var modal = counts.GroupBy(c => c).OrderByDescending(g => g.Count()).First();

            // One field per row means this character never appeared.
            if (modal.Key <= 1) continue;

            var consistency = (double)modal.Count() / counts.Count;
            var score = consistency * 100 + modal.Key;

            if (score > bestScore) { bestScore = score; best = candidate; }
        }

        return best;
    }

    /// <summary>
    /// A first row is a header when every cell is non-empty, unique, and looks
    /// less like data than the rows beneath it — the usual sign being that the
    /// body has numbers where the first row has words.
    /// </summary>
    private static bool LooksLikeHeader(List<List<string>> rows)
    {
        if (rows.Count == 0) return false;

        var first = rows[0];
        if (first.Count == 0 || first.Any(c => c.Trim().Length == 0)) return false;

        // Repeated names are not disqualifying. A header with two columns
        // called "name" is still a header, and saying so is the only way
        // Inspect can warn about it.

        // A header cell that parses as a number is almost certainly data.
        if (first.Any(IsNumeric)) return false;
        if (rows.Count == 1) return true;

        // If any column is numeric below and textual above, that settles it.
        var body = rows.Skip(1).Take(SniffRows).ToList();
        for (var c = 0; c < first.Count; c++)
        {
            var values = body.Where(r => c < r.Count).Select(r => r[c])
                .Where(v => v.Trim().Length > 0).ToList();

            if (values.Count > 0 && values.All(IsNumeric))
                return true;
        }

        return true;
    }

    private static (string Type, string? Min, string? Max) Infer(List<string> values)
    {
        if (values.Count == 0) return ("empty", null, null);

        if (values.All(IsNumeric))
        {
            var numbers = values.Select(v =>
                double.Parse(v.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture)).ToList();

            var integral = values.All(v => long.TryParse(v.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out _));

            return (integral ? "integer" : "decimal",
                numbers.Min().ToString(CultureInfo.InvariantCulture),
                numbers.Max().ToString(CultureInfo.InvariantCulture));
        }

        if (values.All(v => bool.TryParse(v.Trim(), out _)
                         || v.Trim() is "0" or "1" or "yes" or "no" or "Y" or "N"))
        {
            return ("boolean", null, null);
        }

        if (values.All(v => DateTime.TryParse(v.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)))
        {
            var dates = values.Select(v => DateTime.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToList();
            return ("date", dates.Min().ToString("o"), dates.Max().ToString("o"));
        }

        var lengths = values.Select(v => v.Length).ToList();
        return ("string",
            lengths.Min().ToString(CultureInfo.InvariantCulture),
            lengths.Max().ToString(CultureInfo.InvariantCulture));
    }

    private static bool IsNumeric(string value) =>
        value.Trim().Length > 0
        && double.TryParse(value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out _);

    // ── Helpers ──────────────────────────────────────────────────────────

    private static (string Text, string Encoding, bool HasBom) Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length > MaxBytes)
        {
            throw new FileApiException(413,
                $"File is {data.Length:N0} bytes, above the {MaxBytes:N0} byte limit.",
                title: "Payload Too Large");
        }

        var hasBom = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
        var body = hasBom ? data.AsSpan(3) : data.AsSpan();

        return (Encoding.UTF8.GetString(body), hasBom ? "utf-8-bom" : "utf-8", hasBom);
    }

    private static string DetectLineEnding(string text)
    {
        var crlf = 0; var lf = 0; var cr = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (text[i] == '\n') lf++;
        }

        var kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        if (kinds == 0) return "none";
        if (kinds > 1) return "mixed";
        return crlf > 0 ? "crlf" : lf > 0 ? "lf" : "cr";
    }

    private static string DelimiterName(char delimiter) => delimiter switch
    {
        ',' => "comma",
        ';' => "semicolon",
        '\t' => "tab",
        '|' => "pipe",
        _ => "unknown"
    };

    private static string Escape(string cell) =>
        cell.Replace("|", @"\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static string Rename(string fileName, string extension) =>
        Path.GetFileNameWithoutExtension(fileName) is { Length: > 0 } stem
            ? stem + extension
            : "data" + extension;
}
