using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Models;

namespace Aelena.FileApi.Core.Services.Documents;

/// <summary>
/// Reads DjVu documents: walks the IFF container, reports its structure, and
/// extracts the text layer where one is present in a form this service can read.
/// <para>
/// A DjVu file is an AT&amp;T IFF container. A single page is a
/// <c>FORM:DJVU</c>; a book is a <c>FORM:DJVM</c> holding a <c>DIRM</c>
/// directory followed by one <c>FORM:DJVU</c> per page, with shared components
/// in <c>FORM:DJVI</c>. Page images live in <c>Sjbz</c> (JB2 bitonal) and
/// <c>BG44</c>/<c>FG44</c> (IW44 wavelet) chunks; the OCR text layer, when
/// there is one, lives in <c>TXTa</c> or <c>TXTz</c>.
/// </para>
/// <para>
/// <b>Text extraction is limited to <c>TXTa</c>.</b> <c>TXTz</c> holds the same
/// payload compressed with BZZ, and BZZ needs the ZP adaptive arithmetic coder,
/// whose only published implementation is DjVuLibre's — which is GPL, and
/// cannot be vendored into this MIT-licensed package. A <c>TXTz</c>-only
/// document is therefore reported honestly as a 501 rather than returning
/// nothing and calling it success. Most real-world DjVu files fall in this
/// category; <c>djvutxt</c> from DjVuLibre extracts their text.
/// </para>
/// </summary>
public static class DjvuService
{
    /// <summary>Chunk header: four-character id plus a big-endian 32-bit length.</summary>
    private const int ChunkHeaderSize = 8;

    /// <summary>Refuse to walk a container claiming more chunks than any real document has.</summary>
    private const int MaxChunks = 100_000;

    /// <summary>Cap on the text assembled out of a document's text layer.</summary>
    private const int MaxTextChars = 64 * 1024 * 1024;

    // ── Validation ───────────────────────────────────────────────────────

    /// <summary>Walk the container and report its structure and integrity.</summary>
    public static DocumentValidationResponse Validate(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        var issues = new List<HealthIssue>();
        var details = new Dictionary<string, object>(StringComparer.Ordinal);

        var document = Read(data, issues, details);
        var canExtract = document is not null
                      && document.UncompressedTextChunks > 0
                      && issues.TrueForAll(i => i.Severity != "error");

        return EpubService.Build(fileName, "DjVu", issues, canExtract, details);
    }

    // ── Metadata ─────────────────────────────────────────────────────────

    /// <summary>
    /// Describe the document from its container alone. A DjVu with no readable
    /// text layer — which is most of them — still has a page count, page
    /// geometry and a resolution worth reporting, so this path deliberately
    /// does not depend on there being any text.
    /// </summary>
    /// <exception cref="FileApiException">422 when the container is malformed.</exception>
    public static DocumentMetadataResponse GetMetadata(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        var issues = new List<HealthIssue>();
        var document = Read(data, issues, new Dictionary<string, object>(StringComparer.Ordinal));

        var error = issues.Find(i => i.Severity == "error");
        if (error is not null)
            throw new FileApiException(422, error.Message, title: "Invalid DjVu");

        if (document is null)
            throw new FileApiException(422, "This file is not a readable DjVu container.", title: "Invalid DjVu");

        return document.ToMetadata(fileName, document.Pages.Count);
    }

    // ── Extraction ───────────────────────────────────────────────────────

    /// <summary>Extract the text layer, one section per page that carries one.</summary>
    /// <exception cref="FileApiException">
    /// 422 for a malformed container or one with no text layer at all,
    /// 501 when the text layer is BZZ-compressed.
    /// </exception>
    public static ExtractedDocument Extract(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        var issues = new List<HealthIssue>();
        var document = Read(data, issues, new Dictionary<string, object>(StringComparer.Ordinal));

        var error = issues.Find(i => i.Severity == "error");
        if (error is not null)
            throw new FileApiException(422, error.Message, title: "Invalid DjVu");

        if (document is null)
            throw new FileApiException(422, "This file is not a readable DjVu container.", title: "Invalid DjVu");

        if (document.UncompressedTextChunks == 0)
        {
            throw new FileApiException(
                document.CompressedTextChunks > 0 ? 501 : 422,
                document.CompressedTextChunks > 0
                    ? $"This document's text layer is in {document.CompressedTextChunks} BZZ-compressed TXTz " +
                      "chunk(s). BZZ decoding is not implemented here: its only reference implementation is " +
                      "DjVuLibre's, which is GPL and cannot be vendored into this MIT-licensed package. " +
                      "Use `djvutxt` from DjVuLibre to extract the text, or convert the file to PDF first."
                    : "This DjVu has no text layer. It is a page image with no OCR results stored alongside it, " +
                      "so there is no text to extract — run it through an OCR tool first.",
                title: document.CompressedTextChunks > 0 ? "Compressed Text Layer" : "No Text Layer");
        }

        var sections = new List<DocumentSection>();
        var total = 0;

        foreach (var page in document.Pages)
        {
            if (page.Text is null || page.Text.Length == 0)
                continue;

            total += page.Text.Length;
            if (total > MaxTextChars)
            {
                throw new FileApiException(422,
                    $"This document's text layer exceeds the {MaxTextChars:N0} character extraction limit.",
                    title: "Document Too Large");
            }

            sections.Add(new DocumentSection(
                Index: sections.Count + 1,
                Title: null,
                Text: page.Text,
                Markdown: page.Text));
        }

        return new ExtractedDocument(
            DocumentFormat.Djvu, sections, document.ToMetadata(fileName, sections.Count));
    }

    // ── Container walk ───────────────────────────────────────────────────

    private static Document? Read(
        byte[] data, List<HealthIssue> issues, Dictionary<string, object> details)
    {
        if (data.Length < 16)
        {
            issues.Add(new HealthIssue("container", "error",
                $"File is {data.Length} bytes, too short to hold a DjVu container header."));
            return null;
        }

        if (!data.AsSpan(0, 8).SequenceEqual("AT&TFORM"u8))
        {
            issues.Add(new HealthIssue("container", "error",
                "The file does not start with the AT&TFORM signature every DjVu carries."));
            return null;
        }

        var declared = (long)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
        var available = data.Length - 12L;

        if (declared > available)
        {
            issues.Add(new HealthIssue("container", "error",
                $"The container declares {declared:N0} bytes of content but only {available:N0} follow. " +
                "The file is truncated."));
            return null;
        }

        if (declared < available)
        {
            issues.Add(new HealthIssue("container", "warning",
                $"{available - declared:N0} trailing byte(s) follow the declared end of the container."));
        }

        var formType = Encoding.ASCII.GetString(data, 12, 4);
        details["formType"] = formType;

        if (formType is not ("DJVU" or "DJVM" or "DJVI" or "THUM"))
        {
            issues.Add(new HealthIssue("container", "error",
                $"Form type '{formType}' is not a DjVu document type (DJVU, DJVM, DJVI or THUM)."));
            return null;
        }

        var document = new Document(formType);
        var end = (int)Math.Min(12 + declared, data.Length);

        if (formType == "DJVM")
        {
            WalkChunks(data, 16, end, document, issues, depth: 0);
        }
        else
        {
            // A single-page file is one FORM whose chunks sit directly at the
            // top level; synthesise the page so both shapes read the same.
            var page = new Page(formType);
            document.Pages.Add(page);
            ReadPageChunks(data, 16, end, page, document, issues);
        }

        details["pageCount"] = document.Pages.Count;
        details["componentCount"] = document.Components;
        details["textChunksUncompressed"] = document.UncompressedTextChunks;
        details["textChunksCompressed"] = document.CompressedTextChunks;
        details["hasTextLayer"] = document.UncompressedTextChunks + document.CompressedTextChunks > 0;

        if (document.Pages.Count == 0)
        {
            issues.Add(new HealthIssue("pages", "error",
                "The container holds no page forms, so it describes no document."));
        }

        if (document.CompressedTextChunks > 0 && document.UncompressedTextChunks == 0)
        {
            issues.Add(new HealthIssue("text", "info",
                $"The text layer is in {document.CompressedTextChunks} BZZ-compressed TXTz chunk(s), " +
                "which this service does not decode. See the /convert/text response for the detail."));
        }
        else if (document.CompressedTextChunks + document.UncompressedTextChunks == 0)
        {
            issues.Add(new HealthIssue("text", "info",
                "No text layer: this document holds page images only, with no OCR results."));
        }

        return document;
    }

    /// <summary>Walk the top level of a DJVM bundle, which is a sequence of component FORMs.</summary>
    private static void WalkChunks(
        byte[] data, int start, int end, Document document, List<HealthIssue> issues, int depth)
    {
        var offset = start;
        var seen = 0;

        while (offset + ChunkHeaderSize <= end)
        {
            if (++seen > MaxChunks)
            {
                issues.Add(new HealthIssue("chunks", "error",
                    $"The container holds more than {MaxChunks:N0} chunks; refusing to walk further."));
                return;
            }

            var id = Encoding.ASCII.GetString(data, offset, 4);
            var length = (long)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4, 4));
            var body = offset + ChunkHeaderSize;

            if (body + length > end)
            {
                issues.Add(new HealthIssue("chunks", "error",
                    $"Chunk '{id}' at byte {offset} declares {length:N0} bytes, which runs past the end " +
                    "of the container. The file is truncated or corrupt."));
                return;
            }

            if (id == "FORM")
            {
                if (length < 4)
                {
                    issues.Add(new HealthIssue("chunks", "error",
                        $"FORM chunk at byte {offset} is too short to declare a form type."));
                    return;
                }

                var subType = Encoding.ASCII.GetString(data, body, 4);
                var subEnd = body + (int)length;

                if (subType is "DJVU" or "DJVI" or "THUM")
                {
                    if (subType == "DJVU")
                    {
                        var page = new Page(subType);
                        document.Pages.Add(page);
                        ReadPageChunks(data, body + 4, subEnd, page, document, issues);
                    }
                    else
                    {
                        // A DJVI holds components shared between pages and a THUM
                        // holds thumbnails; neither is a page. Their chunks are
                        // still walked — the text-layer tally and the bounds
                        // checks apply to them too — into a Page that is then
                        // discarded rather than added to the document.
                        document.Components++;
                        ReadPageChunks(data, body + 4, subEnd, new Page(subType), document, issues);
                    }
                }
                else if (depth < 4)
                {
                    WalkChunks(data, body + 4, subEnd, document, issues, depth + 1);
                }
            }
            else if (id == "DIRM")
            {
                ReadDirm(data, body, (int)length, document, issues);
            }

            // Chunks are word-aligned: an odd-length chunk is followed by a pad byte.
            offset = body + (int)length + (int)(length & 1);
        }
    }

    /// <summary>Read the chunks of one page form.</summary>
    private static void ReadPageChunks(
        byte[] data, int start, int end, Page page, Document document, List<HealthIssue> issues)
    {
        var offset = start;
        var seen = 0;

        while (offset + ChunkHeaderSize <= end)
        {
            if (++seen > MaxChunks)
                return;

            var id = Encoding.ASCII.GetString(data, offset, 4);
            var length = (long)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4, 4));
            var body = offset + ChunkHeaderSize;

            if (body + length > end)
            {
                issues.Add(new HealthIssue("chunks", "error",
                    $"Chunk '{id}' at byte {offset} declares {length:N0} bytes, which runs past the end " +
                    "of its enclosing form."));
                return;
            }

            switch (id)
            {
                case "INFO" when length >= 10:
                    page.Width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(body, 2));
                    page.Height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(body + 2, 2));
                    page.MinorVersion = data[body + 4];
                    page.MajorVersion = data[body + 5];
                    // The DPI field is the one little-endian value in the format.
                    page.Dpi = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 6, 2));
                    break;

                case "TXTa":
                    document.UncompressedTextChunks++;
                    page.Text = ReadTextChunk(data, body, (int)length, issues);
                    break;

                case "TXTz":
                    document.CompressedTextChunks++;
                    break;
            }

            offset = body + (int)length + (int)(length & 1);
        }
    }

    /// <summary>
    /// A decoded DjVu text chunk: a 24-bit big-endian length, that many bytes of
    /// UTF-8 text, and then the zone hierarchy — which describes where on the
    /// page each word sits and is of no use to a text extractor.
    /// </summary>
    private static string? ReadTextChunk(byte[] data, int offset, int length, List<HealthIssue> issues)
    {
        if (length < 3)
        {
            issues.Add(new HealthIssue("text", "warning",
                "A TXTa chunk is too short to declare a text length and was skipped."));
            return null;
        }

        var textLength = (data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2];

        if (textLength > length - 3)
        {
            issues.Add(new HealthIssue("text", "warning",
                $"A TXTa chunk declares {textLength:N0} bytes of text but holds only {length - 3:N0}; " +
                "the available bytes were used."));
            textLength = length - 3;
        }

        var text = Encoding.UTF8.GetString(data, offset + 3, textLength).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Read what the DJVM directory can be read without BZZ. The file count and
    /// bundling flag are in the clear; the per-file names, ids and titles are in
    /// the BZZ-compressed remainder.
    /// </summary>
    private static void ReadDirm(
        byte[] data, int offset, int length, Document document, List<HealthIssue> issues)
    {
        if (length < 3)
        {
            issues.Add(new HealthIssue("directory", "warning",
                "The DIRM chunk is too short to read; the page list was taken from the component forms."));
            return;
        }

        var flags = data[offset];
        document.DirmVersion = flags & 0x7F;
        document.Bundled = (flags & 0x80) != 0;
        document.DirmFileCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 1, 2));

        if (document.DirmVersion != 1)
        {
            issues.Add(new HealthIssue("directory", "warning",
                $"The DIRM chunk declares version {document.DirmVersion}; only version 1 is documented."));
        }
    }

    // ── Model ────────────────────────────────────────────────────────────

    private sealed class Page(string formType)
    {
        public string FormType { get; } = formType;
        public int Width { get; set; }
        public int Height { get; set; }
        public int Dpi { get; set; }
        public int MajorVersion { get; set; }
        public int MinorVersion { get; set; }
        public string? Text { get; set; }
    }

    private sealed class Document(string formType)
    {
        public string FormType { get; } = formType;
        public List<Page> Pages { get; } = [];
        public int Components { get; set; }
        public int UncompressedTextChunks { get; set; }
        public int CompressedTextChunks { get; set; }
        public int DirmFileCount { get; set; }
        public int DirmVersion { get; set; }
        public bool Bundled { get; set; }

        public DocumentMetadataResponse ToMetadata(string fileName, int sectionCount)
        {
            var first = Pages.Count > 0 ? Pages[0] : null;

            var extra = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["formType"] = FormType,
                ["pageCount"] = Pages.Count.ToString(CultureInfo.InvariantCulture),
                ["sharedComponents"] = Components.ToString(CultureInfo.InvariantCulture),
                ["textChunksUncompressed"] = UncompressedTextChunks.ToString(CultureInfo.InvariantCulture),
                ["textChunksCompressed"] = CompressedTextChunks.ToString(CultureInfo.InvariantCulture)
            };

            if (DirmFileCount > 0)
            {
                extra["directoryFiles"] = DirmFileCount.ToString(CultureInfo.InvariantCulture);
                extra["bundled"] = Bundled ? "true" : "false";
            }

            if (first is not null && first.Width > 0)
            {
                extra["pageWidth"] = first.Width.ToString(CultureInfo.InvariantCulture);
                extra["pageHeight"] = first.Height.ToString(CultureInfo.InvariantCulture);
                extra["dpi"] = first.Dpi.ToString(CultureInfo.InvariantCulture);
                extra["djvuVersion"] = $"{first.MajorVersion}.{first.MinorVersion}";
            }

            return new DocumentMetadataResponse(
                FileName: fileName,
                Format: "DjVu",
                SectionCount: sectionCount,
                Extra: extra);
        }
    }
}
