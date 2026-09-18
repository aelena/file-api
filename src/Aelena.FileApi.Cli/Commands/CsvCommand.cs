using System.CommandLine;
using System.Text;
using Aelena.FileApi.Cli.Helpers;
using Aelena.FileApi.Core.Services.Common;

namespace Aelena.FileApi.Cli.Commands;

/// <summary><c>fileapi csv</c> — dialect detection, load validation and column profiling.</summary>
public static class CsvCommand
{
    public static Command Create() =>
        new("csv", "Delimited file operations — inspect, profile, convert")
        {
            Inspect(), Profile(), Json(), Markdown()
        };

    private static Command Inspect()
    {
        var fileArg = CommandExtensions.FileArgument("Delimited file");
        var cmd = new Command("inspect", "Detect the dialect and report load problems") { fileArg };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var result = CsvService.Inspect(Output.ReadFile(file), file.Name);

            Output.Properties($"CSV: {file.Name}",
                ("Delimiter", $"{result.DelimiterName} ({result.Delimiter})"),
                ("Line ending", result.LineEnding),
                ("Encoding", result.Encoding + (result.HasBom ? " with BOM" : "")),
                ("Header row", result.HasHeader ? "yes" : "no"),
                ("Rows", result.RowCount.Display("N0")),
                ("Data rows", result.DataRowCount.Display("N0")),
                ("Columns", result.ColumnCount.Display()),
                ("Ragged rows", result.RaggedRowCount.Display()));

            if (result.Headers.Count > 0)
                Output.List("Columns", result.Headers);

            if (result.Issues.Count > 0)
                Output.List("Issues", result.Issues.Select(i => $"[{i.Severity}] {i.Check}: {i.Message}"));
        });
    }

    private static Command Profile()
    {
        var fileArg = CommandExtensions.FileArgument("Delimited file");
        var samplesOpt = new Option<int?>("--samples") { Description = "Sample values per column (default 5)" };
        var cmd = new Command("profile", "Per-column type, emptiness, cardinality and range")
        {
            fileArg, samplesOpt
        };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var result = CsvService.Profile(
                Output.ReadFile(file), file.Name, parse.GetValue(samplesOpt) ?? 5);

            Output.Properties($"Profile: {file.Name}",
                ("Rows", result.RowCount.Display("N0")),
                ("Data rows", result.DataRowCount.Display("N0")),
                ("Columns", result.ColumnCount.Display()));

            Output.List("Columns", result.Columns.Select(c =>
                $"{c.Name} — {c.InferredType}, {c.NonEmptyCount} set / {c.EmptyCount} empty, "
                + $"{c.DistinctCount} distinct"
                + (c.Min is null ? "" : $", range {c.Min}..{c.Max}")));
        });
    }

    private static Command Json()
    {
        var fileArg = CommandExtensions.FileArgument("Delimited file");
        var outOpt = CommandExtensions.OutputOption("Write JSON here instead of stdout");
        var cmd = new Command("json", "Convert to JSON objects keyed by header") { fileArg, outOpt };

        return cmd.WithAction(parse => Convert(parse, fileArg, outOpt, CsvService.ToJson));
    }

    private static Command Markdown()
    {
        var fileArg = CommandExtensions.FileArgument("Delimited file");
        var outOpt = CommandExtensions.OutputOption("Write Markdown here instead of stdout");
        var cmd = new Command("markdown", "Convert to a Markdown table") { fileArg, outOpt };

        return cmd.WithAction(parse => Convert(parse, fileArg, outOpt, CsvService.ToMarkdown));
    }

    private static void Convert(
        ParseResult parse,
        Argument<FileInfo> fileArg,
        Option<string?> outOpt,
        Func<byte[], string, (string FileName, byte[] Data)> convert)
    {
        var file = parse.GetRequiredValue(fileArg);
        var (name, bytes) = convert(Output.ReadFile(file), file.Name);

        var path = parse.GetValue(outOpt);
        if (path is null)
        {
            Console.Out.Write(Encoding.UTF8.GetString(bytes));
            return;
        }

        var target = Directory.Exists(path)
            ? Path.Combine(path, name)
            : path;

        Output.WriteFile(target, bytes);
        Output.FileWritten(target, bytes.Length);
    }
}
