using System.CommandLine;
using System.Globalization;
using System.Text;
using Aelena.FileApi.Cli.Helpers;
using Aelena.FileApi.Core.Services.Documents;
#if INCLUDE_PDF
using Aelena.FileApi.Core.Services.Pdf;
#endif

namespace Aelena.FileApi.Cli.Commands;

/// <summary>
/// <c>fileapi convert</c> — EPUB, MOBI/PalmDOC, DjVu and legacy <c>.doc</c> to
/// text, Markdown and PDF, plus the detection and validation these all run first.
/// </summary>
public static class ConvertCommand
{
    public static Command Create()
    {
        var command = new Command("convert",
            "Convert EPUB, MOBI, DjVu and legacy .doc files to text, Markdown or PDF")
        {
            Detect(), Validate(), Metadata(), Text(), Markdown()
        };

#if INCLUDE_PDF
        command.Add(Pdf());
#endif

        return command;
    }

    private static Command Detect()
    {
        var fileArg = CommandExtensions.FileArgument("Document file");
        var cmd = new Command("detect", "Identify a file from its content, not its extension") { fileArg };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var result = DocumentConversionService.Detect(Output.ReadFile(file), file.Name);

            Output.Properties($"Detected: {file.Name}",
                ("Format", result.Format),
                ("Declared by extension", result.DeclaredFormat),
                ("Extension matches content", result.ExtensionMismatch ? "no" : "yes"),
                ("Supported here", result.Supported ? "yes" : "no"),
                ("Size", $"{result.FileSizeBytes:N0} bytes"));

            if (result.Capabilities.Count > 0)
                Output.List("Available operations", result.Capabilities);
        });
    }

    private static Command Validate()
    {
        var fileArg = CommandExtensions.FileArgument("Document file");
        var cmd = new Command("validate", "Run the format's structural checks and list every issue")
        {
            fileArg
        };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var result = DocumentConversionService.Validate(Output.ReadFile(file), file.Name);

            Output.Properties($"Validation: {file.Name}",
                ("Format", result.Format),
                ("Structurally valid", result.Valid ? "yes" : "no"),
                ("Text extractable", result.CanExtractText ? "yes" : "no"),
                ("Errors", result.ErrorCount.Display()),
                ("Warnings", result.WarningCount.Display()));

            if (result.Issues.Count > 0)
                Output.List("Issues", result.Issues.Select(i => $"[{i.Severity}] {i.Check}: {i.Message}"));

            if (result.Details is not null)
            {
                Output.Properties("Container details",
                    [.. result.Details.Select(d =>
                        (d.Key, (string?)Convert.ToString(d.Value, CultureInfo.InvariantCulture)))]);
            }
        });
    }

    private static Command Metadata()
    {
        var fileArg = CommandExtensions.FileArgument("Document file");
        var cmd = new Command("metadata", "Read the document's bibliographic metadata") { fileArg };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var meta = DocumentConversionService.GetMetadata(Output.ReadFile(file), file.Name);

            Output.Properties($"Metadata: {file.Name}",
                ("Format", meta.Format),
                ("Title", meta.Title),
                ("Authors", meta.Authors is { Count: > 0 } a ? string.Join(", ", a) : null),
                ("Language", meta.Language),
                ("Publisher", meta.Publisher),
                ("Identifier", meta.Identifier),
                ("Published", meta.Published),
                ("Rights", meta.Rights),
                ("Sections", meta.SectionCount.Display()));

            if (meta.Subjects is { Count: > 0 } subjects)
                Output.List("Subjects", subjects);
        });
    }

    private static Command Text()
    {
        var fileArg = CommandExtensions.FileArgument("Document file");
        var outOpt = CommandExtensions.OutputOption("Write the text to this file instead of stdout");
        var cmd = new Command("text", "Extract plain text") { fileArg, outOpt };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var (name, bytes) = DocumentConversionService.ToTextFile(Output.ReadFile(file), file.Name);

            Write(parse.GetValue(outOpt), name, bytes);
        });
    }

    private static Command Markdown()
    {
        var fileArg = CommandExtensions.FileArgument("Document file");
        var outOpt = CommandExtensions.OutputOption("Write the Markdown to this file instead of stdout");
        var cmd = new Command("markdown", "Convert to Markdown") { fileArg, outOpt };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var (name, bytes) = DocumentConversionService.ToMarkdownFile(Output.ReadFile(file), file.Name);

            Write(parse.GetValue(outOpt), name, bytes);
        });
    }

#if INCLUDE_PDF
    private static Command Pdf()
    {
        var fileArg = CommandExtensions.FileArgument("Document file");
        var outOpt = CommandExtensions.OutputOption("Output PDF path");
        var cmd = new Command("pdf", "Convert to PDF") { fileArg, outOpt };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var (name, bytes) = MarkdownPdfService.ConvertDocument(Output.ReadFile(file), file.Name);
            var path = parse.GetValue(outOpt) ?? name;

            Output.WriteFile(path, bytes);
            Output.FileWritten(path, bytes.Length);
        });
    }
#endif

    /// <summary>
    /// Text output goes to stdout unless a path was given, so that
    /// <c>fileapi convert markdown book.epub &gt; book.md</c> works the way the
    /// other text-producing commands in this tool already do.
    /// </summary>
    private static void Write(string? path, string defaultName, byte[] bytes)
    {
        if (path is null)
        {
            Console.Out.Write(Encoding.UTF8.GetString(bytes));
            return;
        }

        var target = Directory.Exists(path) ? Path.Combine(path, defaultName) : path;
        Output.WriteFile(target, bytes);
        Output.FileWritten(target, bytes.Length);
    }
}
