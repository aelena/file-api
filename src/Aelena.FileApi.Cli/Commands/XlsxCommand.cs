using System.CommandLine;
using System.Text;
using Aelena.FileApi.Cli.Helpers;
using Aelena.FileApi.Core.Services.Xlsx;

namespace Aelena.FileApi.Cli.Commands;

/// <summary><c>fileapi xlsx</c> — sheets, cells, conversion, and the link and hidden-content audits.</summary>
public static class XlsxCommand
{
    public static Command Create() =>
        new("xlsx", "Workbook operations — sheets, cells, audits, conversion")
        {
            Metrics(), Sheets(), Sheet(), Hidden(), Links(), Markdown()
        };

    private static Command Metrics()
    {
        var fileArg = CommandExtensions.FileArgument("XLSX file");
        var cmd = new Command("metrics", "Sheet, row, cell and formula counts") { fileArg };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var m = XlsxService.GetMetrics(Output.ReadFile(file), file.Name);

            Output.Properties($"XLSX Metrics: {file.Name}",
                ("Sheets", m.SheetCount.Display()),
                ("Visible / hidden", $"{m.VisibleSheetCount} / {m.HiddenSheetCount}"),
                ("Rows", m.TotalRows.Display("N0")),
                ("Cells", m.TotalCells.Display("N0")),
                ("Formulas", m.FormulaCount.Display("N0")),
                ("Defined names", m.DefinedNameCount.Display()),
                ("Words", m.WordCount.Display("N0")),
                ("Size", $"{m.FileSizeBytes:N0} bytes"));
        });
    }

    private static Command Sheets()
    {
        var fileArg = CommandExtensions.FileArgument("XLSX file");
        var cmd = new Command("sheets", "List the sheets with their shape and visibility") { fileArg };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var result = XlsxService.GetSheets(Output.ReadFile(file), file.Name);

            Output.List($"{result.SheetCount} sheet(s)", result.Sheets.Select(s =>
                $"[{s.Index}] {s.Name} — {s.Visibility}, {s.RowCount} rows, "
                + $"{s.ColumnCount} cols, {s.FormulaCount} formulas"));
        });
    }

    private static Command Sheet()
    {
        var fileArg = CommandExtensions.FileArgument("XLSX file");
        var nameOpt = new Option<string?>("--sheet") { Description = "Sheet name or zero-based index" };
        var limitOpt = new Option<int?>("--limit") { Description = "Maximum rows to read" };
        var outOpt = CommandExtensions.OutputOption("Write CSV here instead of stdout");
        var cmd = new Command("sheet", "Print one sheet as CSV") { fileArg, nameOpt, limitOpt, outOpt };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var (name, bytes) = XlsxService.SheetToCsv(
                Output.ReadFile(file), file.Name, parse.GetValue(nameOpt));

            var path = parse.GetValue(outOpt);
            if (path is null)
            {
                Console.Out.Write(Encoding.UTF8.GetString(bytes));
                return;
            }

            Output.WriteFile(path, bytes);
            Output.FileWritten(path, bytes.Length);
            _ = name;
        });
    }

    private static Command Hidden()
    {
        var fileArg = CommandExtensions.FileArgument("XLSX file");
        var cmd = new Command("hidden", "Hidden sheets, rows and columns") { fileArg };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var result = XlsxService.FindHidden(Output.ReadFile(file), file.Name);

            Output.Properties($"Hidden content: {file.Name}",
                ("Hidden sheets", result.HiddenSheetCount.Display()),
                ("Hidden rows", result.HiddenRowCount.Display()),
                ("Hidden columns", result.HiddenColumnCount.Display()));

            if (result.HiddenRanges.Count > 0)
            {
                Output.List("Ranges", result.HiddenRanges.Select(r =>
                    $"{r.Sheet}: {r.Kind} {r.Range} ({r.Count})"));
            }

            if (result.HiddenSheetCount + result.HiddenRowCount + result.HiddenColumnCount > 0)
                Output.Hint("Hidden is not removed — this data travels with the file.");
        });
    }

    private static Command Links()
    {
        var fileArg = CommandExtensions.FileArgument("XLSX file");
        var cmd = new Command("audit-links", "External references, hyperlinks and risky formulas") { fileArg };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var result = XlsxService.AuditLinks(Output.ReadFile(file), file.Name);

            Output.Properties($"Link audit: {file.Name}",
                ("External workbooks", result.ExternalWorkbookCount.Display()),
                ("Hyperlinks", result.HyperlinkCount.Display()),
                ("Risky formulas", result.RiskyFormulaCount.Display()),
                ("Macros", result.HasMacros ? "yes" : "no"));

            if (result.RiskyFormulas.Count > 0)
            {
                Output.List("Formulas reaching outside the workbook", result.RiskyFormulas.Select(f =>
                    $"{f.Sheet}!{f.Cell} [{f.Function}] {f.Formula}"));
            }

            if (result.Hyperlinks.Count > 0)
                Output.List("Hyperlinks", result.Hyperlinks.Select(h => $"{h.Sheet}!{h.Cell} -> {h.Target}"));
        });
    }

    private static Command Markdown()
    {
        var fileArg = CommandExtensions.FileArgument("XLSX file");
        var outOpt = CommandExtensions.OutputOption("Write Markdown here instead of stdout");
        var cmd = new Command("markdown", "Convert every sheet to Markdown tables") { fileArg, outOpt };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var (name, bytes) = XlsxService.ToMarkdown(Output.ReadFile(file), file.Name);

            var path = parse.GetValue(outOpt);
            if (path is null)
            {
                Console.Out.Write(Encoding.UTF8.GetString(bytes));
                return;
            }

            Output.WriteFile(path, bytes);
            Output.FileWritten(path, bytes.Length);
            _ = name;
        });
    }
}
