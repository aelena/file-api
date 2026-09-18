using System.Text;
using System.Text.Json;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Services.Common;
using AwesomeAssertions;
using Xunit;

namespace Aelena.FileApi.Tests.Services;

/// <summary>
/// Delimited-file handling. The dialect is never supplied by the caller, so
/// most of these check that it is inferred correctly from the bytes — including
/// the cases that trip a naive "split on comma" reader.
/// </summary>
public class CsvServiceTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private const string Commas = """
        Title,Author,Year
        The Fox and the Grapes,Aesop,1887
        Sonnet XVIII,Shakespeare,1609
        """;

    // ── Dialect ──────────────────────────────────────────────────────────

    [Fact]
    public void Inspect_CommaDelimited()
    {
        var result = CsvService.Inspect(Bytes(Commas), "books.csv");

        result.Delimiter.Should().Be(",");
        result.DelimiterName.Should().Be("comma");
        result.HasHeader.Should().BeTrue();
        result.ColumnCount.Should().Be(3);
        result.RowCount.Should().Be(3);
        result.DataRowCount.Should().Be(2);
        result.Headers.Should().Equal("Title", "Author", "Year");
        result.RaggedRowCount.Should().Be(0);
    }

    [Fact]
    public void Inspect_SemicolonDelimited_IsNotMistakenForOneColumn()
    {
        // The European default. A reader that assumes commas sees one column.
        var result = CsvService.Inspect(Bytes("a;b;c\n1;2;3\n"), "euro.csv");

        result.DelimiterName.Should().Be("semicolon");
        result.ColumnCount.Should().Be(3);
    }

    [Fact]
    public void Inspect_TabDelimited()
    {
        var result = CsvService.Inspect(Bytes("a\tb\tc\n1\t2\t3\n"), "data.tsv");

        result.DelimiterName.Should().Be("tab");
        result.Delimiter.Should().Be("\\t");
        result.ColumnCount.Should().Be(3);
    }

    [Fact]
    public void Inspect_PipeDelimited()
    {
        var result = CsvService.Inspect(Bytes("a|b|c\n1|2|3\n"), "data.txt");

        result.DelimiterName.Should().Be("pipe");
        result.ColumnCount.Should().Be(3);
    }

    [Fact]
    public void Inspect_CommasInsideQuotedProse_DoNotWinOverTheRealDelimiter()
    {
        // Consistency has to beat raw frequency here: the commas appear more
        // often than the semicolons, but only inside one quoted field.
        var csv = "name;note\n" +
                  "Aesop;\"A fable, short, pointed, and, above all, old\"\n" +
                  "Shakespeare;\"A sonnet, in fourteen lines\"\n";

        var result = CsvService.Inspect(Bytes(csv), "notes.csv");

        result.DelimiterName.Should().Be("semicolon");
        result.ColumnCount.Should().Be(2);
    }

    // ── Parsing ──────────────────────────────────────────────────────────

    [Fact]
    public void GetRows_QuotedFieldsWithCommasAndNewlines()
    {
        var csv = "a,b\n\"one, two\",\"line1\nline2\"\n";

        var result = CsvService.GetRows(Bytes(csv), "x.csv");

        result.Rows.Should().ContainSingle();
        result.Rows[0][0].Should().Be("one, two");
        result.Rows[0][1].Should().Be("line1\nline2");
    }

    [Fact]
    public void GetRows_DoubledQuoteIsALiteralQuote()
    {
        var result = CsvService.GetRows(Bytes("a\n\"she said \"\"hi\"\"\"\n"), "x.csv");

        result.Rows[0][0].Should().Be("she said \"hi\"");
    }

    [Fact]
    public void GetRows_LimitAppliesToDataRows()
    {
        var csv = "a\n1\n2\n3\n4\n";

        CsvService.GetRows(Bytes(csv), "x.csv", limit: 2).Rows.Should().HaveCount(2);
    }

    [Fact]
    public void GetRows_TrailingNewlineDoesNotProduceAnEmptyRow()
    {
        CsvService.GetRows(Bytes(Commas + "\n"), "books.csv").Rows.Should().HaveCount(2);
    }

    // ── Validation ───────────────────────────────────────────────────────

    [Fact]
    public void Inspect_RaggedRows_AreAnErrorNamingTheFirst()
    {
        var result = CsvService.Inspect(Bytes("a,b,c\n1,2,3\n4,5\n6,7,8\n"), "x.csv");

        result.RaggedRowCount.Should().Be(1);
        result.Issues.Should().Contain(i =>
            i.Check == "ragged_rows" && i.Severity == "error" && i.Message.Contains("row 3"));
    }

    [Fact]
    public void Inspect_DuplicateHeaders_AreAWarning()
    {
        var result = CsvService.Inspect(Bytes("id,name,name\n1,a,b\n"), "x.csv");

        result.Issues.Should().Contain(i =>
            i.Check == "header" && i.Severity == "warning" && i.Message.Contains("Duplicate"));
    }

    [Fact]
    public void Inspect_NoHeader_NumbersTheColumns()
    {
        // All-numeric first row is data, not a header.
        var result = CsvService.Inspect(Bytes("1,2,3\n4,5,6\n"), "x.csv");

        result.HasHeader.Should().BeFalse();
        result.Headers.Should().Equal("column1", "column2", "column3");
        result.DataRowCount.Should().Be(2);
    }

    [Fact]
    public void Inspect_MixedLineEndings_AreAWarning()
    {
        var result = CsvService.Inspect(Bytes("a,b\r\n1,2\n3,4\r\n"), "x.csv");

        result.LineEnding.Should().Be("mixed");
        result.Issues.Should().Contain(i => i.Check == "line_endings");
    }

    [Fact]
    public void Inspect_EmptyFile_IsAnErrorNotACrash()
    {
        var result = CsvService.Inspect([], "empty.csv");

        result.RowCount.Should().Be(0);
        result.Issues.Should().Contain(i => i.Check == "empty" && i.Severity == "error");
    }

    [Fact]
    public void Inspect_BomIsDetectedAndNotTreatedAsData()
    {
        var data = Encoding.UTF8.GetPreamble().Concat(Bytes(Commas)).ToArray();

        var result = CsvService.Inspect(data, "books.csv");

        result.HasBom.Should().BeTrue();
        result.Headers[0].Should().Be("Title");   // not "﻿Title"
    }

    // ── Profiling ────────────────────────────────────────────────────────

    [Fact]
    public void Profile_InfersColumnTypes()
    {
        var csv = """
            name,age,joined,active,score
            Ada,36,1815-12-10,true,9.5
            Alan,41,1912-06-23,false,8.25
            Grace,45,1906-12-09,true,9.75
            """;

        var columns = CsvService.Profile(Bytes(csv), "people.csv").Columns;

        columns[0].InferredType.Should().Be("string");
        columns[1].InferredType.Should().Be("integer");
        columns[2].InferredType.Should().Be("date");
        columns[3].InferredType.Should().Be("boolean");
        columns[4].InferredType.Should().Be("decimal");
    }

    [Fact]
    public void Profile_CountsEmptyAndDistinctValues()
    {
        // An empty field, not an empty line: a blank line is conventionally
        // skipped by CSV readers and never becomes a row at all.
        var csv = "city,country\nLisbon,PT\nPorto,PT\nLisbon,PT\n,PT\nPorto,PT\n";

        var column = CsvService.Profile(Bytes(csv), "cities.csv").Columns[0];

        column.NonEmptyCount.Should().Be(4);
        column.EmptyCount.Should().Be(1);
        column.DistinctCount.Should().Be(2);
    }

    [Fact]
    public void Profile_NumericRangeIsMinAndMax()
    {
        var column = CsvService.Profile(Bytes("n\n5\n-3\n12\n"), "n.csv").Columns[0];

        column.InferredType.Should().Be("integer");
        column.Min.Should().Be("-3");
        column.Max.Should().Be("12");
    }

    [Fact]
    public void Profile_EmptyFile_Is422()
    {
        FluentActions.Invoking(() => CsvService.Profile([], "empty.csv"))
            .Should().Throw<FileApiException>()
            .Which.StatusCode.Should().Be(422);
    }

    // ── Conversion ───────────────────────────────────────────────────────

    [Fact]
    public void ToJson_KeysByHeader()
    {
        var (name, bytes) = CsvService.ToJson(Bytes(Commas), "books.csv");

        name.Should().Be("books.json");

        var json = JsonSerializer.Deserialize<JsonElement>(bytes);
        json.GetArrayLength().Should().Be(2);
        json[0].GetProperty("Title").GetString().Should().Be("The Fox and the Grapes");
        json[1].GetProperty("Author").GetString().Should().Be("Shakespeare");
    }

    [Fact]
    public void ToMarkdown_ProducesAPipeTableWithAHeaderRule()
    {
        var (name, bytes) = CsvService.ToMarkdown(Bytes(Commas), "books.csv");

        name.Should().Be("books.md");

        var md = Encoding.UTF8.GetString(bytes);
        md.Should().Contain("| Title | Author | Year |");
        md.Should().Contain("| --- | --- | --- |");
        md.Should().Contain("| Sonnet XVIII | Shakespeare | 1609 |");
    }

    [Fact]
    public void ToMarkdown_EscapesPipesInsideCells()
    {
        var (_, bytes) = CsvService.ToMarkdown(Bytes("a,b\n\"x|y\",z\n"), "x.csv");

        Encoding.UTF8.GetString(bytes).Should().Contain(@"x\|y");
    }
}
