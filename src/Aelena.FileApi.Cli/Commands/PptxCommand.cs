using System.CommandLine;
using System.Text;
using Aelena.FileApi.Cli.Helpers;
using Aelena.FileApi.Core.Services.Pptx;

namespace Aelena.FileApi.Cli.Commands;

/// <summary><c>fileapi pptx</c> — slides, speaker notes and Markdown outline.</summary>
public static class PptxCommand
{
    public static Command Create() =>
        new("pptx", "Presentation operations — slides, notes, markdown")
        {
            Metrics(), Slides(), Notes(), Markdown()
        };

    private static Command Metrics()
    {
        var fileArg = CommandExtensions.FileArgument("PPTX file");
        var cmd = new Command("metrics", "Slide, shape, image and notes counts") { fileArg };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var m = PptxService.GetMetrics(Output.ReadFile(file), file.Name);

            Output.Properties($"PPTX Metrics: {file.Name}",
                ("Slides", m.SlideCount.Display()),
                ("Hidden slides", m.HiddenSlideCount.Display()),
                ("Slides with notes", m.SlidesWithNotes.Display()),
                ("Shapes", m.ShapeCount.Display()),
                ("Images", m.ImageCount.Display()),
                ("Tables", m.TableCount.Display()),
                ("Words", m.WordCount.Display("N0")));
        });
    }

    private static Command Slides()
    {
        var fileArg = CommandExtensions.FileArgument("PPTX file");
        var cmd = new Command("slides", "List the slides with their titles") { fileArg };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var result = PptxService.GetSlides(Output.ReadFile(file), file.Name);

            Output.List($"{result.SlideCount} slide(s)", result.Slides.Select(s =>
                $"{s.Number}. {s.Title ?? "(untitled)"}"
                + (s.Hidden ? " [hidden]" : "")
                + (s.Notes is null ? "" : " [has notes]")));
        });
    }

    private static Command Notes()
    {
        var fileArg = CommandExtensions.FileArgument("PPTX file");
        var cmd = new Command("notes", "Print the speaker notes") { fileArg };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var result = PptxService.GetNotes(Output.ReadFile(file), file.Name);

            foreach (var slide in result.Slides)
            {
                Console.Out.WriteLine($"--- Slide {slide.Number}: {slide.Title ?? "(untitled)"} ---");
                Console.Out.WriteLine(slide.Notes);
                Console.Out.WriteLine();
            }
        });
    }

    private static Command Markdown()
    {
        var fileArg = CommandExtensions.FileArgument("PPTX file");
        var outOpt = CommandExtensions.OutputOption("Write Markdown here instead of stdout");
        var cmd = new Command("markdown", "Convert the deck to a Markdown outline") { fileArg, outOpt };

        return cmd.WithAction(parse =>
        {
            var file = parse.GetRequiredValue(fileArg);
            var markdown = PptxService.ExtractToMarkdown(Output.ReadFile(file), file.Name).Markdown;

            var path = parse.GetValue(outOpt);
            if (path is null)
            {
                Console.Out.Write(markdown);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(markdown);
            Output.WriteFile(path, bytes);
            Output.FileWritten(path, bytes.Length);
        });
    }
}
