using System.Text;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Models;
using Aelena.FileApi.Core.Services.Common;

namespace Aelena.FileApi.Core.Services.Documents;

/// <summary>
/// The single entry point for the conversion family: sniff the format, run its
/// sanity checks, then hand off to the reader that understands it.
/// <para>
/// Every public method here refuses before it converts. A caller that uploads a
/// renamed ZIP, a DRM-locked book or a truncated container gets a status code
/// that says which of those happened, instead of a 500 from somewhere deep in a
/// parser — and nothing is decompressed until the container's own headers have
/// been checked for consistency.
/// </para>
/// </summary>
public static class DocumentConversionService
{
    /// <summary>Formats this family can read text out of.</summary>
    private static readonly DocumentFormat[] Supported =
    [
        DocumentFormat.Epub, DocumentFormat.Mobi, DocumentFormat.PalmDoc,
        DocumentFormat.Djvu, DocumentFormat.Doc
    ];

    // ── Detection and validation ─────────────────────────────────────────

    /// <summary>Identify a file from its content and say what can be done with it.</summary>
    public static DocumentDetectionResponse Detect(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        var format = DocumentFormatSniffer.Detect(data);
        var declared = DocumentFormatSniffer.FromExtension(fileName);
        var supported = Array.IndexOf(Supported, format) >= 0;

        var capabilities = new List<string>();
        if (supported)
        {
            capabilities.Add("validate");
            capabilities.Add("metadata");
            capabilities.Add("text");
            capabilities.Add("markdown");
            capabilities.Add("pdf");
        }

        return new DocumentDetectionResponse(
            FileName: fileName,
            FileSizeBytes: data.Length,
            Format: DocumentFormatSniffer.Describe(format),
            DeclaredFormat: declared == DocumentFormat.Unknown
                ? null
                : DocumentFormatSniffer.Describe(declared),
            Supported: supported,
            ExtensionMismatch: DocumentFormatSniffer.IsExtensionMismatch(format, declared),
            Capabilities: capabilities);
    }

    /// <summary>Run the format's structural checks and report every issue found.</summary>
    /// <exception cref="FileApiException">415 when the format is not one this family reads.</exception>
    public static DocumentValidationResponse Validate(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        return Require(data, fileName) switch
        {
            DocumentFormat.Epub => EpubService.Validate(data, fileName),
            DocumentFormat.Mobi or DocumentFormat.PalmDoc => MobiService.Validate(data, fileName),
            DocumentFormat.Djvu => DjvuService.Validate(data, fileName),
            _ => DocService.Validate(data, fileName)
        };
    }

    // ── Conversion ───────────────────────────────────────────────────────

    /// <summary>
    /// Bibliographic metadata, normalised across the supported formats.
    /// <para>
    /// This goes to each format's header rather than through the extraction,
    /// because the two questions come apart: a scanned DjVu has no text and a
    /// DRM-locked MOBI has text nobody can read, and both still have metadata
    /// sitting in the clear that a caller has every right to.
    /// </para>
    /// </summary>
    public static DocumentMetadataResponse GetMetadata(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        return Require(data, fileName) switch
        {
            DocumentFormat.Epub => EpubService.GetMetadata(data, fileName),
            DocumentFormat.Mobi or DocumentFormat.PalmDoc => MobiService.GetMetadata(data, fileName),
            DocumentFormat.Djvu => DjvuService.GetMetadata(data, fileName),
            _ => DocService.GetMetadata(data, fileName)
        };
    }

    /// <summary>Extracted text, one entry per section the source format defines.</summary>
    public static DocumentTextResponse ExtractText(byte[] data, string fileName)
    {
        var document = Extract(data, fileName);
        var sections = document.Sections
            .Select(s => new PageContent(s.Index, s.Text))
            .ToList();

        var full = string.Join("\n\n", document.Sections.Select(s => s.Text));

        return new DocumentTextResponse(
            FileName: fileName,
            Format: document.Metadata.Format,
            SectionCount: sections.Count,
            WordCount: TextAnalysis.CountWords(full),
            CharCount: TextAnalysis.CountChars(full),
            TokenCount: TextAnalysis.CountTokens(full),
            Language: document.Metadata.Language ?? TextAnalysis.DetectLanguage(full),
            Sections: sections);
    }

    /// <summary>The whole document as one Markdown string.</summary>
    public static DocumentMarkdownResponse ExtractToMarkdown(byte[] data, string fileName)
    {
        var document = Extract(data, fileName);

        return new DocumentMarkdownResponse(
            FileName: fileName,
            Format: document.Metadata.Format,
            SectionCount: document.Sections.Count,
            Markdown: BuildMarkdown(document));
    }

    /// <summary>The whole document as a downloadable <c>.txt</c>.</summary>
    public static (string FileName, byte[] Data) ToTextFile(byte[] data, string fileName)
    {
        var document = Extract(data, fileName);
        var text = string.Join("\n\n", document.Sections.Select(s => s.Text));

        return (Rename(fileName, ".txt"), Encoding.UTF8.GetBytes(text));
    }

    /// <summary>The whole document as a downloadable <c>.md</c>.</summary>
    public static (string FileName, byte[] Data) ToMarkdownFile(byte[] data, string fileName)
    {
        var document = Extract(data, fileName);

        return (Rename(fileName, ".md"), Encoding.UTF8.GetBytes(BuildMarkdown(document)));
    }

    /// <summary>
    /// Read a document down to its sections. Public so that the PDF package can
    /// render the same extraction without duplicating the dispatch — and without
    /// this MIT assembly taking a dependency on the AGPL one to do it.
    /// </summary>
    /// <exception cref="FileApiException">
    /// 415 for an unsupported format, 422 for a malformed one, 501 for a
    /// recognised container this service cannot decode (DRM, HUFF/CDIC, BZZ).
    /// </exception>
    public static ExtractedDocument Extract(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        return Require(data, fileName) switch
        {
            DocumentFormat.Epub => EpubService.Extract(data, fileName),
            DocumentFormat.Mobi or DocumentFormat.PalmDoc => MobiService.Extract(data, fileName),
            DocumentFormat.Djvu => DjvuService.Extract(data, fileName),
            _ => DocService.Extract(data, fileName)
        };
    }

    // ── Plumbing ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sniff the format and refuse anything this family does not read. The
    /// message names what the file actually is, and points at the endpoint that
    /// does handle it where there is one — a PDF or a DOCX arriving here is far
    /// more often a wrong URL than a wrong file.
    /// </summary>
    private static DocumentFormat Require(byte[] data, string fileName)
    {
        var format = DocumentFormatSniffer.Detect(data);

        if (Array.IndexOf(Supported, format) >= 0)
            return format;

        var redirect = format switch
        {
            DocumentFormat.Pdf => " Use /pdf/* for PDF files.",
            DocumentFormat.Docx => " Use /docx/* for Word .docx files.",
            DocumentFormat.Zip => " Use /zip/inspect for plain archives.",
            _ => ""
        };

        var declared = DocumentFormatSniffer.FromExtension(fileName);
        var mismatch = DocumentFormatSniffer.IsExtensionMismatch(format, declared)
            ? $" The file is named '{fileName}' but its content is not " +
              $"{DocumentFormatSniffer.Describe(declared)}."
            : "";

        throw new FileApiException(415,
            $"This endpoint reads EPUB, MOBI, PalmDOC, DjVu and legacy .doc files. " +
            $"The uploaded file is {DocumentFormatSniffer.Describe(format)}.{mismatch}{redirect}",
            title: "Unsupported Media Type");
    }

    /// <summary>
    /// Assemble an extraction into one Markdown document, front matter first.
    /// Public so that the PDF renderer can typeset exactly what
    /// <c>/convert/to-md</c> returns, from a single parse of the source.
    /// </summary>
    public static string BuildMarkdown(ExtractedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var sb = new StringBuilder();

        if (document.Metadata.Title is { Length: > 0 } title)
        {
            // Books commonly open with an <h1> of their own title. Emitting the
            // front-matter heading as well would print it twice.
            var firstSection = document.Sections.Count > 0 ? document.Sections[0].Markdown : "";
            if (!firstSection.StartsWith($"# {title}", StringComparison.Ordinal))
                sb.Append("# ").Append(title).Append('\n');

            if (document.Metadata.Authors is { Count: > 0 } authors)
                sb.Append(sb.Length > 0 ? "\n" : "").Append('*')
                  .Append(string.Join(", ", authors)).Append("*\n");
        }

        foreach (var section in document.Sections)
        {
            if (section.Markdown.Length == 0)
                continue;

            if (sb.Length > 0)
                sb.Append('\n');

            sb.Append(section.Markdown).Append('\n');
        }

        return sb.ToString().Trim();
    }

    private static string Rename(string fileName, string extension) =>
        Path.GetFileNameWithoutExtension(fileName) is { Length: > 0 } stem
            ? stem + extension
            : "document" + extension;
}
