using System.Text;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Models;
using Aelena.FileApi.Core.Services.Common;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace Aelena.FileApi.Core.Services.Pptx;

/// <summary>
/// Stateless PPTX processing on the Open XML SDK — the same dependency DOCX and
/// XLSX use.
/// <para>
/// Speaker notes are extracted alongside the slide bodies. A deck's slides are
/// usually headlines; the argument behind them lives in the notes pane, and
/// most extraction tools drop it entirely.
/// </para>
/// <para>
/// Slides are read in presentation order, which is the order of the
/// <c>sldIdLst</c> rather than the order the parts happen to sit in the
/// package — reordering a deck in PowerPoint rewrites the list and leaves the
/// parts where they were.
/// </para>
/// </summary>
public static class PptxService
{
    // ── Metrics ──────────────────────────────────────────────────────────

    /// <summary>Counts across the whole presentation.</summary>
    public static PptxMetrics GetMetrics(byte[] data, string fileName)
    {
        using var doc = Open(data);
        var slides = SlidesInOrder(doc).ToList();

        var text = new StringBuilder();
        int shapes = 0, images = 0, tables = 0, notes = 0, hidden = 0;

        foreach (var (part, slide) in slides)
        {
            if (part.Slide?.Show is { Value: false }) hidden++;

            shapes += part.Slide?.Descendants<Shape>().Count() ?? 0;
            images += part.Slide?.Descendants<Picture>().Count() ?? 0;
            tables += part.Slide?.Descendants<A.Table>().Count() ?? 0;

            text.Append(TextOf(part.Slide)).Append('\n');

            if (NotesOf(part) is { Length: > 0 } note)
            {
                notes++;
                text.Append(note).Append('\n');
            }
        }

        var body = text.ToString();
        var props = doc.PackageProperties;

        return new PptxMetrics(
            FileName: fileName,
            FileSizeBytes: data.Length,
            WordCount: TextAnalysis.CountWords(body),
            CharCount: TextAnalysis.CountChars(body),
            TokenCount: TextAnalysis.CountTokens(body),
            Language: TextAnalysis.DetectLanguage(body),
            CreationDate: props.Created?.ToString("o"),
            LastModifiedDate: props.Modified?.ToString("o"),
            SlideCount: slides.Count,
            HiddenSlideCount: hidden,
            ShapeCount: shapes,
            ImageCount: images,
            TableCount: tables,
            SlidesWithNotes: notes);
    }

    // ── Slides ───────────────────────────────────────────────────────────

    /// <summary>Every slide's title, body text and speaker notes, in running order.</summary>
    public static PptxSlidesResponse GetSlides(byte[] data, string fileName)
    {
        using var doc = Open(data);
        var result = new List<PptxSlide>();
        var number = 0;

        foreach (var (part, slide) in SlidesInOrder(doc))
        {
            number++;
            var body = TextOf(part.Slide);

            result.Add(new PptxSlide(
                Number: number,
                Title: TitleOf(part.Slide),
                Text: body,
                Notes: NotesOf(part) is { Length: > 0 } note ? note : null,
                Hidden: part.Slide?.Show is { Value: false },
                ShapeCount: part.Slide?.Descendants<Shape>().Count() ?? 0,
                ImageCount: part.Slide?.Descendants<Picture>().Count() ?? 0,
                TableCount: part.Slide?.Descendants<A.Table>().Count() ?? 0));
        }

        return new PptxSlidesResponse(fileName, result.Count, result);
    }

    /// <summary>
    /// The deck as a Markdown outline: one heading per slide, the body beneath
    /// it, and the notes as a block quote so they stay distinguishable from
    /// what was actually on screen.
    /// </summary>
    public static PptxMarkdownResponse ExtractToMarkdown(byte[] data, string fileName)
    {
        var slides = GetSlides(data, fileName);
        var sb = new StringBuilder();

        foreach (var slide in slides.Slides)
        {
            sb.Append("## ").Append(slide.Number).Append(". ")
              .Append(slide.Title ?? "(untitled)");

            if (slide.Hidden) sb.Append(" _(hidden)_");
            sb.Append("\n\n");

            var body = slide.Title is { } title && slide.Text.StartsWith(title, StringComparison.Ordinal)
                ? slide.Text[title.Length..].TrimStart('\n')
                : slide.Text;

            foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                sb.Append("- ").Append(line.Trim()).Append('\n');

            if (slide.Notes is { Length: > 0 } notes)
            {
                sb.Append("\n> **Notes:** ");
                sb.Append(string.Join("\n> ", notes.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())));
                sb.Append('\n');
            }

            sb.Append('\n');
        }

        return new PptxMarkdownResponse(fileName, slides.SlideCount, sb.ToString().TrimEnd() + "\n");
    }

    /// <summary>Just the speaker notes, slide by slide.</summary>
    public static PptxSlidesResponse GetNotes(byte[] data, string fileName)
    {
        var slides = GetSlides(data, fileName);

        return slides with
        {
            Slides = [.. slides.Slides.Where(s => s.Notes is { Length: > 0 })]
        };
    }

    // ── Metadata ─────────────────────────────────────────────────────────

    /// <summary>Core and custom properties.</summary>
    public static PptxMetadataResponse GetMetadata(byte[] data, string fileName)
    {
        using var doc = Open(data);
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

        return new PptxMetadataResponse(
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
            SlideCount: SlidesInOrder(doc).Count(),
            CustomMetadata: custom.Count > 0 ? custom : null);
    }

    /// <summary>Strip authorship and revision metadata, keeping the slides.</summary>
    public static (string FileName, byte[] Data) RemoveMetadata(byte[] data, string fileName)
    {
        using var stream = new MemoryStream();
        stream.Write(data);
        stream.Position = 0;

        using (var doc = PresentationDocument.Open(stream, true))
        {
            var props = doc.PackageProperties;
            props.Title = props.Creator = props.Subject = props.Keywords = "";
            props.Category = props.Description = props.LastModifiedBy = "";
            props.Created = props.Modified = null;

            if (doc.CustomFilePropertiesPart is not null)
                doc.DeletePart(doc.CustomFilePropertiesPart);
        }

        return (Rename(fileName, "_clean.pptx"), stream.ToArray());
    }

    // ── Search ───────────────────────────────────────────────────────────

    /// <summary>Search slide bodies and speaker notes for literal text or a regex.</summary>
    public static (string FileName, IReadOnlyList<SearchMatch> Matches) Search(
        byte[] data, string fileName, string? query = null, string? pattern = null)
    {
        using var doc = Open(data);
        var text = new StringBuilder();

        foreach (var (part, _) in SlidesInOrder(doc))
        {
            text.Append(TextOf(part.Slide)).Append('\n');
            text.Append(NotesOf(part)).Append('\n');
        }

        return (fileName, TextSearch.Search(text.ToString(), query: query, pattern: pattern));
    }

    // ── Plumbing ─────────────────────────────────────────────────────────

    private static PresentationDocument Open(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        try
        {
            return PresentationDocument.Open(new MemoryStream(data, writable: false), false);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException
                                      or ArgumentException or FileFormatException)
        {
            throw new FileApiException(422,
                $"This file is not a readable PPTX presentation: {ex.Message}",
                title: "Invalid PPTX");
        }
    }

    /// <summary>
    /// Slide parts in presentation order. The slide id list is authoritative:
    /// reordering a deck rewrites that list and leaves the parts where they
    /// were, so enumerating <c>SlideParts</c> gives the original order rather
    /// than the current one.
    /// </summary>
    private static IEnumerable<(SlidePart Part, SlideId? Id)> SlidesInOrder(PresentationDocument doc)
    {
        var presentationPart = doc.PresentationPart;
        if (presentationPart?.Presentation?.SlideIdList is not { } list)
        {
            foreach (var part in presentationPart?.SlideParts ?? [])
                yield return (part, null);

            yield break;
        }

        foreach (var slideId in list.Elements<SlideId>())
        {
            if (slideId.RelationshipId?.Value is not { } id) continue;
            if (presentationPart.GetPartById(id) is SlidePart part)
                yield return (part, slideId);
        }
    }

    /// <summary>
    /// All text on a slide, one text body per line. Runs are joined without a
    /// separator because PowerPoint splits a single word across runs whenever
    /// its formatting changes mid-word.
    /// </summary>
    private static string TextOf(Slide? slide)
    {
        if (slide is null) return "";

        var lines = new List<string>();

        foreach (var shape in slide.Descendants<Shape>())
        {
            foreach (var paragraph in shape.Descendants<A.Paragraph>())
            {
                var line = string.Concat(paragraph.Descendants<A.Text>().Select(t => t.Text)).Trim();
                if (line.Length > 0) lines.Add(line);
            }
        }

        foreach (var table in slide.Descendants<A.Table>())
        {
            foreach (var row in table.Elements<A.TableRow>())
            {
                var cells = row.Elements<A.TableCell>()
                    .Select(c => string.Concat(c.Descendants<A.Text>().Select(t => t.Text)).Trim());

                var line = string.Join("\t", cells).Trim();
                if (line.Length > 0) lines.Add(line);
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// The slide's title, taken from the shape the layout marks as the title
    /// placeholder rather than from whichever text happens to come first.
    /// </summary>
    private static string? TitleOf(Slide? slide)
    {
        if (slide is null) return null;

        foreach (var shape in slide.Descendants<Shape>())
        {
            var placeholder = shape.NonVisualShapeProperties
                ?.ApplicationNonVisualDrawingProperties
                ?.PlaceholderShape;

            if (placeholder?.Type?.Value is not { } type) continue;

            if (type == PlaceholderValues.Title || type == PlaceholderValues.CenteredTitle)
            {
                var title = string.Concat(shape.Descendants<A.Text>().Select(t => t.Text)).Trim();
                if (title.Length > 0) return title;
            }
        }

        // No title placeholder: the first non-empty line is the best available
        // answer, and is what a reader would call the title.
        var text = TextOf(slide);
        var first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return first is { Length: > 0 } ? first : null;
    }

    private static string NotesOf(SlidePart part)
    {
        if (part.NotesSlidePart?.NotesSlide is not { } notes) return "";

        var lines = new List<string>();
        foreach (var shape in notes.Descendants<Shape>())
        {
            // The notes slide also carries a thumbnail of the slide itself,
            // whose placeholder repeats the body text; only the notes body is
            // wanted here.
            var placeholder = shape.NonVisualShapeProperties
                ?.ApplicationNonVisualDrawingProperties
                ?.PlaceholderShape;

            if (placeholder?.Type?.Value is { } type && type == PlaceholderValues.SlideImage)
                continue;

            foreach (var paragraph in shape.Descendants<A.Paragraph>())
            {
                var line = string.Concat(paragraph.Descendants<A.Text>().Select(t => t.Text)).Trim();
                if (line.Length > 0) lines.Add(line);
            }
        }

        return string.Join('\n', lines);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Rename(string fileName, string suffix) =>
        Path.GetFileNameWithoutExtension(fileName) is { Length: > 0 } stem
            ? stem + suffix
            : "presentation" + suffix;
}
