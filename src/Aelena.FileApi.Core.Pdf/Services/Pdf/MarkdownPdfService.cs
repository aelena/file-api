using System.Globalization;
using System.Text;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Services.Documents;
using iText.IO.Font.Constants;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Draw;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Properties;

namespace Aelena.FileApi.Core.Services.Pdf;

/// <summary>
/// Renders Markdown to PDF, and converts EPUB, MOBI, DjVu and legacy DOC files
/// to PDF by way of their Markdown.
/// <para>
/// This is a typesetter for the Markdown the conversion family produces —
/// headings, paragraphs, lists, block quotes, code, rules, tables and inline
/// emphasis — not a full CommonMark implementation. Anything it does not
/// recognise is laid out as body text rather than dropped, so no content is
/// ever lost to an unsupported construct.
/// </para>
/// <para>
/// <b>Text is restricted to Windows-1252.</b> iText's built-in fonts carry no
/// Unicode glyphs, and embedding a font would mean shipping one. Characters
/// outside CP1252 are transliterated where an obvious equivalent exists — an
/// accented letter loses its accent — and replaced with '?' where none does, so
/// a book in a non-Latin script converts to a PDF of question marks. Use
/// <c>/convert/to-md</c> for those; Markdown is UTF-8 and loses nothing.
/// </para>
/// </summary>
public static class MarkdownPdfService
{
    private const float PageMargin = 50f;
    private const float BodySize = 11f;
    private const float CodeSize = 9.5f;

    /// <summary>Refuse to typeset more than this, rather than run a request out of memory.</summary>
    private const int MaxMarkdownChars = 16 * 1024 * 1024;

    // ── Entry points ─────────────────────────────────────────────────────

    /// <summary>
    /// Convert an EPUB, MOBI, PalmDOC, DjVu or legacy DOC file to PDF.
    /// </summary>
    /// <exception cref="FileApiException">
    /// Whatever the source format's reader raises — 415 for an unsupported
    /// format, 422 for a malformed one, 501 for DRM or an undecodable
    /// compression scheme.
    /// </exception>
    public static (string FileName, byte[] Data) ConvertDocument(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        // One parse, used for both the content and the document properties —
        // going through ExtractToMarkdown and GetMetadata would read the source
        // container twice.
        var document = DocumentConversionService.Extract(data, fileName);

        return FromMarkdown(
            DocumentConversionService.BuildMarkdown(document),
            Rename(fileName),
            title: document.Metadata.Title,
            author: document.Metadata.Authors is { Count: > 0 } authors
                ? string.Join(", ", authors)
                : null);
    }

    /// <summary>Typeset a Markdown string as a PDF.</summary>
    /// <exception cref="FileApiException">422 when the Markdown is empty or above the size limit.</exception>
    public static (string FileName, byte[] Data) FromMarkdown(
        string markdown, string fileName, string? title = null, string? author = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        if (markdown.Trim().Length == 0)
            throw new FileApiException(422, "There is no content to typeset.", title: "Empty Document");

        if (markdown.Length > MaxMarkdownChars)
        {
            throw new FileApiException(422,
                $"The document is {markdown.Length:N0} characters, above the {MaxMarkdownChars:N0} " +
                "character limit for PDF rendering.",
                title: "Document Too Large");
        }

        using var stream = new MemoryStream();
        using (var pdf = new PdfDocument(new PdfWriter(stream)))
        {
            var info = pdf.GetDocumentInfo();
            if (!string.IsNullOrWhiteSpace(title)) info.SetTitle(WinAnsi.Sanitise(title));
            if (!string.IsNullOrWhiteSpace(author)) info.SetAuthor(WinAnsi.Sanitise(author));
            info.SetCreator("Aelena.FileApi");

            using var document = new iText.Layout.Document(pdf, PageSize.A4);
            document.SetMargins(PageMargin, PageMargin, PageMargin, PageMargin);

            Render(document, markdown);
        }

        return (Rename(fileName), stream.ToArray());
    }

    // ── Rendering ────────────────────────────────────────────────────────

    private static void Render(iText.Layout.Document document, string markdown)
    {
        var fonts = Fonts.Create();
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var paragraph = new List<string>();
        var code = new List<string>();
        var table = new List<string>();
        var inCode = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            document.Add(Body(string.Join(' ', paragraph), fonts));
            paragraph.Clear();
        }

        void FlushTable()
        {
            if (table.Count == 0) return;
            document.Add(BuildTable(table, fonts));
            table.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                FlushTable();

                if (inCode)
                {
                    document.Add(CodeBlock(code, fonts));
                    code.Clear();
                }
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                code.Add(line);
                continue;
            }

            var trimmed = line.TrimStart();

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                FlushTable();
                continue;
            }

            // Tables are the one construct that spans lines without a fence, so
            // consecutive pipe rows are gathered and emitted as a unit.
            if (trimmed.StartsWith('|'))
            {
                FlushParagraph();
                table.Add(trimmed);
                continue;
            }

            FlushTable();

            if (trimmed.StartsWith('#'))
            {
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#') level++;

                if (level <= 6 && level < trimmed.Length && trimmed[level] == ' ')
                {
                    FlushParagraph();
                    document.Add(Heading(trimmed[(level + 1)..].Trim(), level, fonts));
                    continue;
                }
            }

            if (trimmed is "---" or "***" or "___" or "- - -")
            {
                FlushParagraph();
                document.Add(new LineSeparator(new SolidLine(0.5f))
                    .SetMarginTop(8).SetMarginBottom(8));
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph();
                document.Add(Quote(trimmed[2..].Trim(), fonts));
                continue;
            }

            if (IsBullet(trimmed, out var bulletText))
            {
                FlushParagraph();
                document.Add(Bullet(bulletText, line.Length - trimmed.Length, fonts, ordered: false));
                continue;
            }

            if (IsOrdered(trimmed, out var orderedText, out var number))
            {
                FlushParagraph();
                document.Add(Bullet(orderedText, line.Length - trimmed.Length, fonts,
                    ordered: true, marker: number));
                continue;
            }

            paragraph.Add(trimmed);
        }

        if (inCode && code.Count > 0)
            document.Add(CodeBlock(code, fonts));

        FlushParagraph();
        FlushTable();
    }

    // ── Block elements ───────────────────────────────────────────────────

    private static Paragraph Heading(string text, int level, Fonts fonts)
    {
        var size = level switch
        {
            1 => 20f,
            2 => 15.5f,
            3 => 13f,
            _ => 11.5f
        };

        var paragraph = new Paragraph()
            .SetFont(fonts.Bold)
            .SetFontSize(size)
            .SetMarginTop(level == 1 ? 4f : 12f)
            .SetMarginBottom(5f)
            .SetKeepWithNext(true);

        AppendInline(paragraph, text, fonts, baseFont: fonts.Bold);
        return paragraph;
    }

    private static Paragraph Body(string text, Fonts fonts)
    {
        var paragraph = new Paragraph()
            .SetFont(fonts.Regular)
            .SetFontSize(BodySize)
            .SetMarginBottom(7f)
            .SetMultipliedLeading(1.25f);

        AppendInline(paragraph, text, fonts, baseFont: fonts.Regular);
        return paragraph;
    }

    private static Paragraph Quote(string text, Fonts fonts)
    {
        var paragraph = new Paragraph()
            .SetFont(fonts.Italic)
            .SetFontSize(BodySize)
            .SetMarginLeft(18f)
            .SetMarginBottom(7f)
            .SetPaddingLeft(8f)
            .SetBorderLeft(new SolidBorder(ColorConstants.LIGHT_GRAY, 2f));

        AppendInline(paragraph, text, fonts, baseFont: fonts.Italic);
        return paragraph;
    }

    private static Paragraph Bullet(
        string text, int indent, Fonts fonts, bool ordered, int marker = 0)
    {
        var depth = Math.Clamp(indent / 2, 0, 5);

        var paragraph = new Paragraph()
            .SetFont(fonts.Regular)
            .SetFontSize(BodySize)
            .SetMarginLeft(14f + depth * 14f)
            .SetMarginBottom(2f)
            .SetMultipliedLeading(1.2f);

        paragraph.Add(new Text(ordered
            ? $"{marker.ToString(CultureInfo.InvariantCulture)}. "
            : "• "));

        AppendInline(paragraph, text, fonts, baseFont: fonts.Regular);
        return paragraph;
    }

    private static Paragraph CodeBlock(List<string> lines, Fonts fonts)
    {
        var paragraph = new Paragraph(WinAnsi.Sanitise(string.Join('\n', lines)))
            .SetFont(fonts.Mono)
            .SetFontSize(CodeSize)
            .SetBackgroundColor(new DeviceRgb(246, 246, 246))
            .SetPadding(7f)
            .SetMarginBottom(8f)
            .SetMultipliedLeading(1.15f);

        return paragraph;
    }

    private static Table BuildTable(List<string> rows, Fonts fonts)
    {
        // A Markdown alignment row ("|---|---|") is layout, not content, and its
        // presence is what marks the row before it as the header.
        var cells = rows
            .Select(r => r.Trim('|').Split('|').Select(c => c.Trim()).ToList())
            .ToList();

        var separator = cells.FindIndex(r => r.Count > 0 && r.TrueForAll(IsAlignmentCell));
        var hasHeader = separator == 1;
        if (separator >= 0) cells.RemoveAt(separator);

        var columns = cells.Count == 0 ? 1 : cells.Max(r => r.Count);
        var table = new Table(UnitValue.CreatePercentArray(columns))
            .UseAllAvailableWidth()
            .SetFontSize(BodySize - 1.5f)
            .SetMarginBottom(9f);

        for (var r = 0; r < cells.Count; r++)
        {
            var isHeader = hasHeader && r == 0;

            for (var c = 0; c < columns; c++)
            {
                var text = c < cells[r].Count ? cells[r][c] : "";
                var paragraph = new Paragraph()
                    .SetFont(isHeader ? fonts.Bold : fonts.Regular)
                    .SetMultipliedLeading(1.15f);
                AppendInline(paragraph, text, fonts, isHeader ? fonts.Bold : fonts.Regular);

                var cell = new Cell().Add(paragraph).SetPadding(4f);
                if (isHeader) cell.SetBackgroundColor(new DeviceRgb(240, 240, 240));
                table.AddCell(cell);
            }
        }

        return table;
    }

    private static bool IsAlignmentCell(string cell) =>
        cell.Length > 0 && cell.All(c => c is '-' or ':' or ' ');

    // ── Inline spans ─────────────────────────────────────────────────────

    /// <summary>
    /// Walk a line of Markdown and append it as styled runs. Emphasis markers
    /// only take effect when their closing marker is on the same line, so an
    /// asterisk used as a literal — which is common in extracted book text —
    /// stays visible instead of swallowing the rest of the paragraph.
    /// </summary>
    private static void AppendInline(Paragraph paragraph, string text, Fonts fonts, PdfFont baseFont)
    {
        var buffer = new StringBuilder();
        var i = 0;

        void Flush()
        {
            if (buffer.Length == 0) return;
            paragraph.Add(new Text(WinAnsi.Sanitise(buffer.ToString())).SetFont(baseFont));
            buffer.Clear();
        }

        void Emit(string content, PdfFont font, float? size = null)
        {
            Flush();
            var run = new Text(WinAnsi.Sanitise(content)).SetFont(font);
            if (size is not null) run.SetFontSize(size.Value);
            paragraph.Add(run);
        }

        while (i < text.Length)
        {
            var ch = text[i];

            // Escaped punctuation is literal.
            if (ch == '\\' && i + 1 < text.Length && !char.IsLetterOrDigit(text[i + 1]))
            {
                buffer.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (ch == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i + 1)
                {
                    Emit(text[(i + 1)..close], fonts.Mono, CodeSize);
                    i = close + 1;
                    continue;
                }
            }

            if (ch == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    Emit(text[(i + 2)..close], fonts.Bold);
                    i = close + 2;
                    continue;
                }
            }

            if (ch is '*' or '_')
            {
                var close = text.IndexOf(ch, i + 1);
                if (close > i + 1)
                {
                    Emit(text[(i + 1)..close], fonts.Italic);
                    i = close + 1;
                    continue;
                }
            }

            // Images become their alt text; a PDF cannot reach back into the
            // source container for the bytes, and an empty box helps nobody.
            if (ch == '!' && i + 1 < text.Length && text[i + 1] == '[')
            {
                if (TryLink(text, i + 1, out var alt, out _, out var next))
                {
                    if (alt.Length > 0) buffer.Append(alt);
                    i = next;
                    continue;
                }
            }

            if (ch == '[' && TryLink(text, i, out var label, out var href, out var after))
            {
                Emit(label, baseFont);
                if (href.Length > 0 && !href.StartsWith('#'))
                    Emit($" ({href})", fonts.Regular, BodySize - 2f);
                i = after;
                continue;
            }

            buffer.Append(ch);
            i++;
        }

        Flush();
    }

    private static bool TryLink(string text, int start, out string label, out string href, out int next)
    {
        label = href = "";
        next = start;

        var close = text.IndexOf(']', start + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
            return false;

        var end = text.IndexOf(')', close + 2);
        if (end < 0)
            return false;

        label = text[(start + 1)..close];
        href = text[(close + 2)..end];
        next = end + 1;
        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static bool IsBullet(string line, out string text)
    {
        text = "";
        if (line.Length < 2 || line[0] is not ('-' or '*' or '+') || line[1] != ' ')
            return false;

        text = line[2..].Trim();
        return true;
    }

    private static bool IsOrdered(string line, out string text, out int number)
    {
        text = "";
        number = 0;

        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits])) digits++;

        if (digits == 0 || digits > 9 || digits + 1 >= line.Length
            || line[digits] != '.' || line[digits + 1] != ' ')
            return false;

        number = int.Parse(line[..digits], CultureInfo.InvariantCulture);
        text = line[(digits + 2)..].Trim();
        return true;
    }

    private static string Rename(string fileName) =>
        // Fully qualified: iText.Kernel.Geom also defines a Path.
        System.IO.Path.GetFileNameWithoutExtension(fileName) is { Length: > 0 } stem
            ? stem + ".pdf"
            : "document.pdf";

    /// <summary>The four standard faces this renderer uses, created once per document.</summary>
    private sealed record Fonts(PdfFont Regular, PdfFont Bold, PdfFont Italic, PdfFont Mono)
    {
        public static Fonts Create() => new(
            PdfFontFactory.CreateFont(StandardFonts.HELVETICA),
            PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD),
            PdfFontFactory.CreateFont(StandardFonts.HELVETICA_OBLIQUE),
            PdfFontFactory.CreateFont(StandardFonts.COURIER));
    }
}
