using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Models;
using Aelena.FileApi.Core.Services.Common;
using iText.Forms.Fields;
using iText.IO.Font.Constants;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Extgstate;
using iText.Kernel.Pdf.Navigation;
using iText.Kernel.Utils;
using Path = System.IO.Path;

namespace Aelena.FileApi.Core.Services.Pdf;

/// <summary>
/// Stateless PDF processing service built on iText7 8.x.
/// Every public method is <c>static</c>, accepts raw <c>byte[]</c> data and a file name,
/// and returns immutable records or value tuples — no streams, no <c>IFormFile</c>.
/// Thread-safe by design: no shared mutable state.
/// </summary>
public static partial class PdfService
{
    // ────────────────────────────── Read Operations ──────────────────────────────

    /// <summary>
    /// Compute comprehensive metrics for a PDF file including page count, word/char/token
    /// counts, language detection, image count, table count (heuristic), corruption and
    /// digital-signature checks, and per-page OCR necessity.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name for the response.</param>
    /// <returns>A <see cref="PdfMetrics"/> record with all computed values.</returns>
    public static PdfMetrics GetMetrics(byte[] data, string fileName)
    {
        try
        {
            using var reader = SafeReader(data);
            using var doc = OpenDocument(reader);
            var info = doc.GetDocumentInfo();
            var pageCount = doc.GetNumberOfPages();
            var fullText = new StringBuilder();
            var imageCount = 0;
            var tableCount = 0;
            var ocrPages = new List<int>();
            var isCorrupt = false;

            for (var i = 1; i <= pageCount; i++)
            {
                try
                {
                    var page = doc.GetPage(i);
                    var text = ExtractPageText(page);
                    fullText.AppendLine(text);

                    if (string.IsNullOrWhiteSpace(text))
                        ocrPages.Add(i);

                    imageCount += CountPageImages(page);
                    tableCount += EstimateTableCount(text);
                }
                catch { isCorrupt = true; }
            }

            var allText = fullText.ToString();
            var charCount = TextAnalysis.CountChars(allText);
            var avgChars = pageCount > 0 ? (double)charCount / pageCount : 0;

            return new PdfMetrics(
                FileName: fileName,
                FileSizeBytes: data.Length,
                WordCount: TextAnalysis.CountWords(allText),
                CharCount: charCount,
                TokenCount: TextAnalysis.CountTokens(allText),
                Language: TextAnalysis.DetectLanguage(allText),
                CreationDate: info.GetMoreInfo("CreationDate"),
                LastModifiedDate: info.GetMoreInfo("ModDate"),
                PageCount: pageCount,
                AvgCharsPerPage: Math.Round(avgChars, 1),
                ImageCount: imageCount,
                TableCount: tableCount,
                IsCorrupt: isCorrupt,
                IsSigned: HasDigitalSignature(doc),
                OcrNeeded: ocrPages.Count > 0,
                OcrPages: ocrPages,
                PagesNeedingOcrCount: ocrPages.Count);
        }
        catch (FileApiException ex) when (ex.Title == "Unreadable PDF")
        {
            // Metrics is the one operation that answers for a broken file rather than
            // refusing it: callers use it to *ask* whether a document is usable.
            return new PdfMetrics(fileName, data.Length, 0, 0, 0, null, null, null,
                0, 0, 0, 0, true, false, false, [], 0);
        }
    }

    /// <summary>
    /// Extract embedded metadata from the PDF document info dictionary including title,
    /// author, subject, keywords, creator, producer, dates, PDF version, first-page size,
    /// and any custom (non-standard) metadata entries.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name for the response.</param>
    /// <returns>A <see cref="PdfMetadataResponse"/> record.</returns>
    public static PdfMetadataResponse GetMetadata(byte[] data, string fileName)
    {
        using var reader = SafeReader(data);
        using var doc = OpenDocument(reader);
        var info = doc.GetDocumentInfo();
        var pageCount = doc.GetNumberOfPages();

        string? pageSize = null;
        if (pageCount > 0)
        {
            var rect = doc.GetPage(1).GetPageSize();
            pageSize = $"{rect.GetWidth():F0}x{rect.GetHeight():F0}pt";
        }

        // Collect non-standard metadata from the info dictionary
        Dictionary<string, string>? custom = null;
        var infoPdfObj = doc.GetTrailer()?.GetAsDictionary(PdfName.Info);
        if (infoPdfObj is not null)
        {
            HashSet<string> standardKeys =
                ["Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate", "Trapped"];
            custom = [];
            foreach (var key in infoPdfObj.KeySet())
            {
                var name = key.GetValue();
                if (!standardKeys.Contains(name))
                {
                    var val = infoPdfObj.GetAsString(key)?.GetValue()
                              ?? infoPdfObj.Get(key)?.ToString() ?? "";
                    custom[name] = val;
                }
            }
            if (custom.Count == 0) custom = null;
        }

        return new PdfMetadataResponse(
            FileName: fileName,
            Title: info.GetTitle(),
            Author: info.GetAuthor(),
            Subject: info.GetSubject(),
            Keywords: info.GetKeywords(),
            Creator: info.GetCreator(),
            Producer: info.GetProducer(),
            CreationDate: info.GetMoreInfo("CreationDate"),
            ModificationDate: info.GetMoreInfo("ModDate"),
            Trapped: info.GetMoreInfo("Trapped"),
            PageCount: pageCount,
            PdfVersion: doc.GetPdfVersion()?.ToString(),
            PageSize: pageSize,
            CustomMetadata: custom);
    }

    /// <summary>
    /// Extract all AcroForm fields from the PDF. For each field returns name, type
    /// (text / checkbox / dropdown / signature / unknown), current value, default value,
    /// choice options where applicable, read-only flag, and page number.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name for the response.</param>
    /// <returns>A <see cref="FormFieldsResponse"/> record.</returns>
    public static FormFieldsResponse ExtractFormFields(byte[] data, string fileName)
    {
        using var reader = SafeReader(data);
        using var doc = OpenDocument(reader);
        var form = PdfFormCreator.GetAcroForm(doc, false);

        if (form is null)
            return new FormFieldsResponse(fileName, 0, []);

        var fields = new List<FormField>();
        foreach (var (name, field) in form.GetAllFormFields())
        {
            // PdfName.GetValue() returns the name without its leading solidus,
            // so these are "Tx" and not "/Tx". Matching on the slashed form
            // silently reported every field in every document as "unknown";
            // the trim keeps both spellings working whichever iText returns.
            var formType = (field.GetFormType()?.GetValue() ?? "").TrimStart('/');
            var fieldType = formType switch
            {
                "Tx" => "text",
                "Btn" => "checkbox",
                "Ch" => "dropdown",
                "Sig" => "signature",
                _ => "unknown"
            };

            IReadOnlyList<string>? options = null;
            if (field is PdfChoiceFormField choiceField)
            {
                var opts = choiceField.GetOptions();
                if (opts is { } o && o.Size() > 0)
                    options = [.. o.Select(x => x.ToString() ?? "")];
            }

            int? page = null;
            var widgets = field.GetWidgets();
            if (widgets is { Count: > 0 })
            {
                var widgetPage = widgets[0].GetPage();
                if (widgetPage is not null)
                    page = doc.GetPageNumber(widgetPage);
            }

            fields.Add(new FormField(
                Name: name,
                FieldType: fieldType,
                Value: field.GetValueAsString(),
                DefaultValue: field.GetDefaultValue()?.ToString(),
                Options: options,
                IsReadOnly: field.IsReadOnly(),
                Page: page));
        }

        return new FormFieldsResponse(fileName, fields.Count, fields);
    }

    /// <summary>
    /// Run a comprehensive health check on the PDF. Checks performed:
    /// corruption, missing (non-embedded) fonts, embedded JavaScript,
    /// mixed page sizes, encryption status, and pages needing OCR.
    /// Each issue is classified as error, warning, or info.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name for the response.</param>
    /// <returns>A <see cref="PdfHealthResponse"/> with all discovered issues.</returns>
    public static PdfHealthResponse HealthCheck(byte[] data, string fileName)
    {
        var issues = new List<HealthIssue>();

        try
        {
            using var reader = SafeReader(data);
            using var doc = OpenDocument(reader);
            var pageCount = doc.GetNumberOfPages();

            // Encryption
            if (reader.IsEncrypted())
                issues.Add(new HealthIssue("encryption", "info", "Document is encrypted."));

            // Mixed page sizes
            if (pageCount > 1)
            {
                var first = doc.GetPage(1).GetPageSize();
                for (var i = 2; i <= pageCount; i++)
                {
                    var sz = doc.GetPage(i).GetPageSize();
                    if (Math.Abs(sz.GetWidth() - first.GetWidth()) > 1 ||
                        Math.Abs(sz.GetHeight() - first.GetHeight()) > 1)
                    {
                        issues.Add(new HealthIssue("mixed_page_sizes", "warning",
                            "Document contains pages of different sizes."));
                        break;
                    }
                }
            }

            // Embedded JavaScript
            var names = doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Names);
            if (names?.Get(PdfName.JavaScript) is not null)
                issues.Add(new HealthIssue("javascript", "warning",
                    "Document contains embedded JavaScript."));

            // OCR needs
            var ocrPages = new List<int>();
            for (var i = 1; i <= pageCount; i++)
            {
                var text = ExtractPageText(doc.GetPage(i));
                if (string.IsNullOrWhiteSpace(text))
                    ocrPages.Add(i);
            }
            if (ocrPages.Count > 0)
                issues.Add(new HealthIssue("ocr_needed", "warning",
                    $"{ocrPages.Count} page(s) contain no extractable text and may need OCR.",
                    new Dictionary<string, object> { ["pages"] = ocrPages }));

            // Missing (non-embedded) fonts
            for (var i = 1; i <= pageCount; i++)
            {
                var fonts = doc.GetPage(i).GetResources()?.GetResource(PdfName.Font);
                if (fonts is null) continue;
                foreach (var key in fonts.KeySet())
                {
                    var fontDict = fonts.GetAsDictionary(key);
                    var descriptor = fontDict?.GetAsDictionary(PdfName.FontDescriptor);
                    if (descriptor is null) continue;
                    var embedded = descriptor.Get(PdfName.FontFile)
                                   ?? descriptor.Get(PdfName.FontFile2)
                                   ?? descriptor.Get(PdfName.FontFile3);
                    if (embedded is null)
                    {
                        var fontName = fontDict!.GetAsName(PdfName.BaseFont)?.GetValue() ?? "unknown";
                        issues.Add(new HealthIssue("missing_font", "warning",
                            $"Font '{fontName}' on page {i} is not embedded.",
                            new Dictionary<string, object> { ["font"] = fontName, ["page"] = i }));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not FileApiException)
        {
            issues.Add(new HealthIssue("corruption", "error", $"Cannot fully parse PDF: {ex.Message}"));
        }

        var errors = issues.Count(i => i.Severity == "error");
        var warnings = issues.Count(i => i.Severity == "warning");
        var infos = issues.Count(i => i.Severity == "info");

        return new PdfHealthResponse(
            FileName: fileName,
            Healthy: errors == 0 && warnings == 0,
            IssueCount: issues.Count,
            ErrorCount: errors,
            WarningCount: warnings,
            InfoCount: infos,
            Issues: issues);
    }

    /// <summary>
    /// Extract text from every page (or a specified subset) using iText7's
    /// <see cref="LocationTextExtractionStrategy"/>. Returns structured per-page content.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name for the response.</param>
    /// <param name="pages">Optional page range (e.g. "1,3,5-8"). All pages if null.</param>
    /// <returns>A <see cref="PdfTextResponse"/> with per-page text.</returns>
    public static PdfTextResponse ExtractText(byte[] data, string fileName, string? pages = null)
    {
        using var reader = SafeReader(data);
        using var doc = OpenDocument(reader);
        var totalPages = doc.GetNumberOfPages();
        var indices = pages is not null
            ? PageRangeParser.Parse(pages, totalPages)
            : [.. Enumerable.Range(0, totalPages)];

        var result = indices
            .Select(i => new PageContent(i + 1, ExtractPageText(doc.GetPage(i + 1))))
            .ToList();

        return new PdfTextResponse(fileName, totalPages, result);
    }

    /// <summary>
    /// Extract text from specific pages identified by a range string (e.g. "1,3,5-8").
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name for the response.</param>
    /// <param name="pages">Page range specification (1-based).</param>
    /// <returns>An <see cref="ExtractionResponse"/> with extracted page contents.</returns>
    public static ExtractionResponse ExtractPages(byte[] data, string fileName, string pages)
    {
        using var reader = SafeReader(data);
        using var doc = OpenDocument(reader);
        var totalPages = doc.GetNumberOfPages();
        var indices = PageRangeParser.Parse(pages, totalPages);

        var extracted = indices
            .Select(i => new PageContent(i + 1, ExtractPageText(doc.GetPage(i + 1))))
            .ToList();

        return new ExtractionResponse(fileName, totalPages, extracted);
    }

    /// <summary>
    /// Convert PDF text to basic Markdown. Uses heuristics: short all-caps lines become
    /// <c>##</c> headings, short title-case or colon-terminated lines become <c>###</c>
    /// subheadings, numbered/bulleted lines are preserved, everything else is a paragraph.
    /// Page breaks are rendered as horizontal rules.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name for the response.</param>
    /// <param name="pages">Optional page range. All pages if null.</param>
    /// <returns>A <see cref="MarkdownResponse"/> containing the Markdown string.</returns>
    public static MarkdownResponse ExtractToMarkdown(byte[] data, string fileName, string? pages = null)
    {
        using var reader = SafeReader(data);
        using var doc = OpenDocument(reader);
        var totalPages = doc.GetNumberOfPages();
        var indices = pages is not null
            ? PageRangeParser.Parse(pages, totalPages)
            : [.. Enumerable.Range(0, totalPages)];

        var md = new StringBuilder();
        foreach (var idx in indices)
        {
            if (md.Length > 0)
                md.AppendLine().AppendLine("---").AppendLine();

            md.AppendLine(CultureInfo.InvariantCulture, $"<!-- Page {idx + 1} -->").AppendLine();

            var text = ExtractPageText(doc.GetPage(idx + 1));
            foreach (var line in text.Split('\n'))
            {
                var t = line.Trim();
                if (string.IsNullOrEmpty(t)) { md.AppendLine(); continue; }

                if (IsLikelyHeading(t))
                    md.Append("## ").AppendLine(t);
                else if (IsLikelySubheading(t))
                    md.Append("### ").AppendLine(t);
                else
                    md.AppendLine(t);
            }
        }

        return new MarkdownResponse(fileName, totalPages, md.ToString().TrimEnd());
    }

    /// <summary>
    /// Search across all pages for a literal query (case-insensitive) or regex pattern.
    /// Each <see cref="SearchMatch"/> includes the page number where it was found.
    /// Exactly one of <paramref name="query"/> or <paramref name="pattern"/> must be provided.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="query">Literal search string (case-insensitive).</param>
    /// <param name="pattern">Regex pattern.</param>
    /// <returns>Tuple of (fileName, matches).</returns>
    public static (string FileName, IReadOnlyList<SearchMatch> Matches) Search(
        byte[] data, string fileName, string? query, string? pattern)
    {
        if (query is null && pattern is null)
            throw new FileApiException(400, "Provide either 'query' or 'pattern'.");

        using var reader = SafeReader(data);
        using var doc = OpenDocument(reader);
        var allMatches = new List<SearchMatch>();

        for (var i = 1; i <= doc.GetNumberOfPages(); i++)
        {
            var text = ExtractPageText(doc.GetPage(i));
            if (string.IsNullOrEmpty(text)) continue;

            var pageMatches = TextSearch.Search(text, query: query, pattern: pattern);
            allMatches.AddRange(pageMatches.Select(m => m with { Page = i }));
        }

        return (fileName, allMatches);
    }

    /// <summary>
    /// Not implemented: always throws <see cref="FileApiException"/> with status 501.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="pages">Optional page range.</param>
    /// <exception cref="FileApiException">Always, with status 501.</exception>
    /// <remarks>
    /// This opened the document, ignored it, and returned a response reporting zero
    /// tables and an empty list — with a 200. A caller had no way to tell "this PDF
    /// contains no tables" from "table detection was never written", which is the
    /// worse of the two answers to get silently. Refusing is honest; the heuristic
    /// the summary once promised needs real layout analysis.
    /// </remarks>
    [SuppressMessage("Style", "IDE0060:Remove unused parameter",
        Justification = "Signature is the shipped public API; the method is a declared 501 stub.")]
    public static PdfTableResponse ExtractTables(byte[] data, string fileName, string? pages = null) =>
        throw new FileApiException(501,
            "PDF table extraction is not implemented. Use /pdf/extract-text or "
            + "/pdf/extract-markdown, which preserve the document's text layout.",
            title: "Not Implemented");

    /// <summary>
    /// Remove owner-password restrictions from a PDF without needing the owner password.
    /// Uses unethical reading mode to bypass restrictions.
    /// </summary>
    public static (string FileName, byte[] Data) UnlockPdf(byte[] data, string fileName)
    {
        using var outStream = new MemoryStream();
        using var reader = SafeReader(data);
        using var writer = new PdfWriter(outStream);
        using var doc = OpenDocument(reader, writer);
        doc.Close();

        var stem = Path.GetFileNameWithoutExtension(fileName);
        return ($"{stem}_unlocked.pdf", outStream.ToArray());
    }

    // ────────────────────────────── Write Operations ──────────────────────────────

    /// <summary>
    /// Merge 2-10 PDF files into a single document. Adds an outline bookmark at the
    /// start of each source file's pages for easy navigation.
    /// </summary>
    /// <param name="files">List of (raw bytes, file name) tuples.</param>
    /// <returns>Tuple of (output file name, merged PDF bytes).</returns>
    public static (string FileName, byte[] Data) MergePdfs(IReadOnlyList<(byte[] Data, string Name)> files)
    {
        if (files.Count < 2)
            throw new FileApiException(400, "At least 2 PDF files are required for merge.");
        if (files.Count > 10)
            throw new FileApiException(400, "Maximum 10 PDF files allowed for merge.");

        using var outStream = new MemoryStream();
        using var writer = new PdfWriter(outStream);
        using var merged = new PdfDocument(writer);
        var merger = new PdfMerger(merged);
        var outlines = merged.GetOutlines(true);
        var currentPage = 1;

        foreach (var (fileData, name) in files)
        {
            using var srcReader = SafeReader(fileData);
            using var srcDoc = OpenDocument(srcReader);
            var srcPages = srcDoc.GetNumberOfPages();
            merger.Merge(srcDoc, 1, srcPages);

            // Bookmark pointing to first page of this source
            var bookmark = outlines.AddOutline(name);
            bookmark.AddDestination(PdfExplicitDestination.CreateFit(merged.GetPage(currentPage)));
            currentPage += srcPages;
        }

        merged.Close();
        return ("merged.pdf", outStream.ToArray());
    }

    /// <summary>
    /// Split a PDF into multiple documents by semicolon-separated page ranges
    /// (e.g. "1-3;4-6;7"). Returns the parts bundled in a ZIP archive.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name (used for naming parts).</param>
    /// <param name="ranges">Semicolon-separated page ranges.</param>
    /// <returns>Tuple of (ZIP file name, ZIP bytes).</returns>
    public static (string FileName, byte[] Data) SplitPdf(byte[] data, string fileName, string ranges)
    {
        var groups = ranges.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (groups.Length == 0)
            throw new FileApiException(400, "No page ranges specified.");

        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);

        using var srcReader = SafeReader(data);
        using var srcDoc = OpenDocument(srcReader);
        var totalPages = srcDoc.GetNumberOfPages();

        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var g = 0; g < groups.Length; g++)
            {
                var indices = PageRangeParser.Parse(groups[g], totalPages);
                byte[] partBytes;
                {
                    var partStream = new MemoryStream();
                    using (var partWriter = new PdfWriter(partStream))
                    using (var partDoc = new PdfDocument(partWriter))
                    {
                        foreach (var idx in indices)
                            srcDoc.CopyPagesTo(idx + 1, idx + 1, partDoc);
                    }
                    partBytes = partStream.ToArray();
                }

                var entry = archive.CreateEntry($"{stem}_part{g + 1}.pdf");
                using var entryStream = entry.Open();
                entryStream.Write(partBytes);
            }
        }

        return ($"{stem}_split.zip", zipStream.ToArray());
    }

    /// <summary>
    /// Rotate pages by 90, 180, or 270 degrees. Rotation is additive to the current
    /// page rotation. If <paramref name="pages"/> is null all pages are rotated.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="angle">Rotation angle: 90, 180, or 270.</param>
    /// <param name="pages">Optional page range. All pages if null.</param>
    /// <returns>Tuple of (output file name, rotated PDF bytes).</returns>
    public static (string FileName, byte[] Data) RotatePages(
        byte[] data, string fileName, int angle, string? pages = null)
    {
        if (angle is not (90 or 180 or 270))
            throw new FileApiException(400, "Angle must be 90, 180, or 270.");

        using var outStream = new MemoryStream();
        using var reader = SafeReader(data);
        using var writer = new PdfWriter(outStream);
        using var doc = OpenDocument(reader, writer);
        var totalPages = doc.GetNumberOfPages();
        var indices = pages is not null
            ? PageRangeParser.Parse(pages, totalPages)
            : [.. Enumerable.Range(0, totalPages)];

        foreach (var idx in indices)
        {
            var page = doc.GetPage(idx + 1);
            page.SetRotation((page.GetRotation() + angle) % 360);
        }

        doc.Close();
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return ($"{stem}_rotated.pdf", outStream.ToArray());
    }

    /// <summary>
    /// Reorder pages according to a comma-separated list of 1-based page numbers
    /// (e.g. "3,1,2,4"). Every original page must appear exactly once.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="order">Comma-separated 1-based page numbers defining the new order.</param>
    /// <returns>Tuple of (output file name, reordered PDF bytes).</returns>
    public static (string FileName, byte[] Data) ReorderPages(byte[] data, string fileName, string order)
    {
        var pageNums = order
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToList();

        using var srcReader = SafeReader(data);
        using var srcDoc = OpenDocument(srcReader);
        var totalPages = srcDoc.GetNumberOfPages();

        if (pageNums.Count != totalPages)
            throw new FileApiException(400, $"Order must list exactly {totalPages} pages; got {pageNums.Count}.");
        if (pageNums.Any(p => p < 1 || p > totalPages))
            throw new FileApiException(400, $"Page numbers must be between 1 and {totalPages}.");
        if (pageNums.Distinct().Count() != totalPages)
            throw new FileApiException(400, "Each page must appear exactly once.");

        using var outStream = new MemoryStream();
        using var outWriter = new PdfWriter(outStream);
        using var outDoc = new PdfDocument(outWriter);

        foreach (var num in pageNums)
            srcDoc.CopyPagesTo(num, num, outDoc);

        outDoc.Close();
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return ($"{stem}_reordered.pdf", outStream.ToArray());
    }

    /// <summary>
    /// Delete specified pages from the PDF. At least one page must remain after deletion.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="pages">Page range to delete (e.g. "2,4-6").</param>
    /// <returns>Tuple of (output file name, trimmed PDF bytes).</returns>
    public static (string FileName, byte[] Data) DeletePages(byte[] data, string fileName, string pages)
    {
        using var srcReader = SafeReader(data);
        using var srcDoc = OpenDocument(srcReader);
        var totalPages = srcDoc.GetNumberOfPages();
        var toDelete = PageRangeParser.Parse(pages, totalPages).ToHashSet();

        if (toDelete.Count >= totalPages)
            throw new FileApiException(400, "Cannot delete all pages; at least one must remain.");

        using var outStream = new MemoryStream();
        using var outWriter = new PdfWriter(outStream);
        using var outDoc = new PdfDocument(outWriter);

        for (var i = 0; i < totalPages; i++)
        {
            if (!toDelete.Contains(i))
                srcDoc.CopyPagesTo(i + 1, i + 1, outDoc);
        }

        outDoc.Close();
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return ($"{stem}_trimmed.pdf", outStream.ToArray());
    }

    /// <summary>
    /// Add a diagonal text watermark on every page. The watermark is rendered with the
    /// specified colour, opacity, font size, rotation angle, and vertical position.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="text">Watermark text.</param>
    /// <param name="color">Colour name ("gray","red","blue","green","black","white") or hex "#RRGGBB".</param>
    /// <param name="opacity">Fill opacity 0.0-1.0.</param>
    /// <param name="fontSize">Font size in points.</param>
    /// <param name="angle">Rotation angle in degrees.</param>
    /// <param name="position">"center", "top", or "bottom".</param>
    /// <returns>Tuple of (output file name, watermarked PDF bytes).</returns>
    public static (string FileName, byte[] Data) AddWatermark(
        byte[] data, string fileName, string text, string color,
        float opacity, int fontSize, int angle, string position)
    {
        using var outStream = new MemoryStream();
        using var reader = SafeReader(data);
        using var writer = new PdfWriter(outStream);
        using var doc = OpenDocument(reader, writer);
        var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);
        var pdfColor = ParseColor(color);

        for (var i = 1; i <= doc.GetNumberOfPages(); i++)
        {
            var page = doc.GetPage(i);
            var sz = page.GetPageSize();
            var canvas = new PdfCanvas(page);
            var gs = new PdfExtGState().SetFillOpacity(opacity);

            var cx = sz.GetWidth() / 2;
            var cy = position.ToLowerInvariant() switch
            {
                "top" => sz.GetHeight() * 0.8f,
                "bottom" => sz.GetHeight() * 0.2f,
                _ => sz.GetHeight() / 2
            };

            var rad = angle * Math.PI / 180.0;
            var cos = (float)Math.Cos(rad);
            var sin = (float)Math.Sin(rad);
            // Centre the text roughly on the computed origin
            var offsetX = cx - fontSize * text.Length * 0.15f;

            canvas.SaveState();
            canvas.SetExtGState(gs);
            canvas.BeginText();
            canvas.SetFontAndSize(font, fontSize);
            canvas.SetColor(pdfColor, true);
            canvas.SetTextMatrix(cos, sin, -sin, cos, offsetX, cy);
            canvas.ShowText(text);
            canvas.EndText();
            canvas.RestoreState();
        }

        doc.Close();
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return ($"{stem}_watermarked.pdf", outStream.ToArray());
    }

    /// <summary>
    /// Strip all metadata from the PDF document info dictionary, producing a clean copy.
    /// Removes title, author, subject, keywords, creator, producer, dates, and custom entries.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name.</param>
    /// <returns>Tuple of (output file name, cleaned PDF bytes).</returns>
    public static (string FileName, byte[] Data) RemoveMetadata(byte[] data, string fileName)
    {
        using var outStream = new MemoryStream();
        using var reader = SafeReader(data);
        using var writer = new PdfWriter(outStream);
        using var doc = OpenDocument(reader, writer);

        // Clear standard fields
        var info = doc.GetDocumentInfo();
        info.SetTitle("");
        info.SetAuthor("");
        info.SetSubject("");
        info.SetKeywords("");
        info.SetCreator("");
        info.SetProducer("");

        // Clear the entire info dictionary to remove custom keys
        var infoDict = doc.GetTrailer()?.GetAsDictionary(PdfName.Info);
        if (infoDict is not null)
        {
            foreach (var key in infoDict.KeySet().ToList())
                infoDict.Remove(key);
        }

        doc.Close();
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return ($"{stem}_clean.pdf", outStream.ToArray());
    }

    /// <summary>
    /// Encrypt the PDF with AES-256 standard encryption. Sets a user password (required to
    /// open) and an optional owner password (for unrestricted access). Allows printing by default.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="userPassword">Password required to open the document.</param>
    /// <param name="ownerPassword">Owner password for full permissions; defaults to userPassword.</param>
    /// <returns>Tuple of (output file name, encrypted PDF bytes).</returns>
    public static (string FileName, byte[] Data) EncryptPdf(
        byte[] data, string fileName, string userPassword, string? ownerPassword)
    {
        using var outStream = new MemoryStream();
        using var reader = SafeReader(data);
        var wp = new WriterProperties()
            .SetStandardEncryption(
                Encoding.UTF8.GetBytes(userPassword),
                Encoding.UTF8.GetBytes(ownerPassword ?? userPassword),
                EncryptionConstants.ALLOW_PRINTING,
                EncryptionConstants.ENCRYPTION_AES_256);
        using var writer = new PdfWriter(outStream, wp);
        using var doc = OpenDocument(reader, writer);
        doc.Close();

        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return ($"{stem}_encrypted.pdf", outStream.ToArray());
    }

    /// <summary>
    /// Decrypt a password-protected PDF using the supplied password.
    /// Produces an unencrypted copy.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="password">Password to decrypt the PDF.</param>
    /// <returns>Tuple of (output file name, decrypted PDF bytes).</returns>
    public static (string FileName, byte[] Data) DecryptPdf(byte[] data, string fileName, string password)
    {
        try
        {
            var rp = new ReaderProperties().SetPassword(Encoding.UTF8.GetBytes(password));
            using var reader = new PdfReader(new MemoryStream(data), rp);
            using var outStream = new MemoryStream();
            using var writer = new PdfWriter(outStream);
            using var doc = OpenDocument(reader, writer);
            doc.Close();

            var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
            return ($"{stem}_decrypted.pdf", outStream.ToArray());
        }
        catch (iText.Kernel.Exceptions.BadPasswordException)
        {
            throw new FileApiException(422, "Invalid password for encrypted PDF.");
        }
    }

    /// <summary>
    /// Compress the PDF by enabling full cross-reference stream compression (level 9).
    /// Returns original and compressed sizes for comparison.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="imageQuality">Target JPEG quality 1-100 (reserved for future image resampling).</param>
    /// <param name="dpi">Target DPI for images (reserved for future image resampling).</param>
    /// <returns>Tuple of (file name, compressed bytes, original size, compressed size).</returns>
    /// <remarks>
    /// <para>
    /// <paramref name="imageQuality"/> and <paramref name="dpi"/> are accepted but not
    /// applied: this compresses object streams and the cross-reference table, and does
    /// not resample embedded images. Unlike the table and redaction stubs the operation
    /// does do what its name says and returns a genuinely smaller file, so it answers
    /// normally rather than 501 — but a caller asking for quality 10 should not expect
    /// image recompression.
    /// </para>
    /// <para>
    /// Implementing them means decoding each image XObject, resampling, and re-encoding.
    /// </para>
    /// </remarks>
    [SuppressMessage("Style", "IDE0060:Remove unused parameter",
        Justification = "Signature is the shipped public API; image resampling is documented as not applied.")]
    public static (string FileName, byte[] Data, long OriginalSize, long CompressedSize) CompressPdf(
        byte[] data, string fileName, int imageQuality, int dpi)
    {
        using var outStream = new MemoryStream();
        using var reader = SafeReader(data);
        var wp = new WriterProperties()
            .SetFullCompressionMode(true)
            .SetCompressionLevel(9);
        using var writer = new PdfWriter(outStream, wp);
        using var doc = OpenDocument(reader, writer);
        doc.Close();

        var compressed = outStream.ToArray();
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return ($"{stem}_compressed.pdf", compressed, data.Length, compressed.Length);
    }

    /// <summary>
    /// Insert blank pages after each of the specified page positions. Blank pages inherit
    /// the size of the page they follow.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="afterPages">Comma-separated 1-based page numbers after which to insert blanks.</param>
    /// <param name="count">Number of blank pages to insert at each position (1-20).</param>
    /// <returns>Tuple of (output file name, expanded PDF bytes).</returns>
    public static (string FileName, byte[] Data) InsertBlankPages(
        byte[] data, string fileName, string afterPages, int count)
    {
        if (count is < 1 or > 20)
            throw new FileApiException(400, "Count must be between 1 and 20.");

        using var srcReader = SafeReader(data);
        using var srcDoc = OpenDocument(srcReader);
        var totalPages = srcDoc.GetNumberOfPages();
        var insertAfter = PageRangeParser.Parse(afterPages, totalPages)
            .Select(i => i + 1) // convert 0-based to 1-based
            .ToHashSet();

        using var outStream = new MemoryStream();
        using var outWriter = new PdfWriter(outStream);
        using var outDoc = new PdfDocument(outWriter);

        for (var i = 1; i <= totalPages; i++)
        {
            srcDoc.CopyPagesTo(i, i, outDoc);
            if (insertAfter.Contains(i))
            {
                var refSize = srcDoc.GetPage(i).GetPageSize();
                for (var c = 0; c < count; c++)
                    outDoc.AddNewPage(new PageSize(refSize));
            }
        }

        outDoc.Close();
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return ($"{stem}_expanded.pdf", outStream.ToArray());
    }

    /// <summary>
    /// Add page numbers to every page at the specified position. Supports format placeholders
    /// <c>{n}</c> (current page number) and <c>{total}</c> (total pages).
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="position">Position: "top-left","top-center","top-right","bottom-left","bottom-center","bottom-right".</param>
    /// <param name="fontSize">Font size in points.</param>
    /// <param name="start">Starting page number (applied to first page).</param>
    /// <param name="margin">Margin from page edge in points.</param>
    /// <param name="fontColor">Colour name or hex "#RRGGBB".</param>
    /// <param name="fmt">Format string with {n} and {total} placeholders.</param>
    /// <returns>Tuple of (output file name, numbered PDF bytes).</returns>
    public static (string FileName, byte[] Data) AddPageNumbers(
        byte[] data, string fileName, string position, int fontSize,
        int start, int margin, string fontColor, string fmt)
    {
        using var outStream = new MemoryStream();
        using var reader = SafeReader(data);
        using var writer = new PdfWriter(outStream);
        using var doc = OpenDocument(reader, writer);
        var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        var color = ParseColor(fontColor);
        var totalPages = doc.GetNumberOfPages();

        for (var i = 1; i <= totalPages; i++)
        {
            var page = doc.GetPage(i);
            var sz = page.GetPageSize();
            var label = fmt
                .Replace("{n}", (start + i - 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{total}", totalPages.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

            var textWidth = font.GetWidth(label, fontSize);
            var (x, y) = ResolvePosition(position, sz, margin, textWidth);

            var canvas = new PdfCanvas(page);
            canvas.BeginText();
            canvas.SetFontAndSize(font, fontSize);
            canvas.SetColor(color, true);
            canvas.MoveText(x, y);
            canvas.ShowText(label);
            canvas.EndText();
        }

        doc.Close();
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return ($"{stem}_numbered.pdf", outStream.ToArray());
    }

    /// <summary>
    /// Extract all annotations from every page. Returns an anonymous object with
    /// file name, total count, and a list of annotation dictionaries containing
    /// page number, subtype, contents, bounding rectangle, and author.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name.</param>
    /// <returns>Anonymous object serialisable to JSON.</returns>
    public static object ExtractAnnotations(byte[] data, string fileName)
    {
        using var reader = SafeReader(data);
        using var doc = OpenDocument(reader);
        var annotations = new List<Dictionary<string, object?>>();

        for (var i = 1; i <= doc.GetNumberOfPages(); i++)
        {
            foreach (var annot in doc.GetPage(i).GetAnnotations())
            {
                var rect = annot.GetRectangle();
                annotations.Add(new Dictionary<string, object?>
                {
                    ["page"] = i,
                    ["type"] = annot.GetSubtype()?.GetValue(),
                    ["contents"] = annot.GetContents()?.GetValue(),
                    ["rect"] = rect?.ToFloatArray(),
                    ["author"] = annot.GetPdfObject().GetAsString(PdfName.T)?.GetValue()
                });
            }
        }

        return new { fileName, totalAnnotations = annotations.Count, annotations };
    }

    /// <summary>
    /// Extract the bookmark (outline) tree from the PDF. Returns a flat list with each
    /// bookmark's title, target page number (if resolvable), and nesting level.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name.</param>
    /// <returns>Anonymous object serialisable to JSON.</returns>
    public static object ExtractBookmarks(byte[] data, string fileName)
    {
        using var reader = SafeReader(data);
        using var doc = OpenDocument(reader);
        var outlines = doc.GetOutlines(false);
        var bookmarks = new List<Dictionary<string, object?>>();

        if (outlines is not null)
            WalkOutlines(doc, outlines, bookmarks, 0);

        return new { fileName, totalBookmarks = bookmarks.Count, bookmarks };
    }

    /// <summary>
    /// Not implemented: always throws <see cref="FileApiException"/> with status 501.
    /// </summary>
    /// <param name="data">Raw PDF bytes.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="terms">Comma-separated literal terms to redact (case-insensitive).</param>
    /// <param name="regex">Optional regex pattern to redact.</param>
    /// <param name="pages">Optional page range. All pages if null.</param>
    /// <param name="replacement">Replacement string (default "***").</param>
    /// <exception cref="FileApiException">Always, with status 501.</exception>
    /// <remarks>
    /// <para>
    /// This used to count matches, copy the document through unchanged, and return it as
    /// <c>&lt;name&gt;_redacted.pdf</c> with a non-zero redaction count — so callers received
    /// a 200 and a file whose "redacted" text was still fully selectable and extractable.
    /// A redaction tool that silently does nothing is worse than one that refuses, so it
    /// now refuses, matching the <c>/redact</c> endpoint that already answered 501.
    /// </para>
    /// <para>
    /// Implementing it properly means rewriting page content streams (iText's pdfSweep
    /// add-on, or equivalent). Drawing black rectangles over the words is not sufficient:
    /// the text layer underneath survives copy-paste and text extraction.
    /// </para>
    /// </remarks>
    public static (string FileName, byte[] Data, int TotalRedactions) RedactText(
        byte[] data, string fileName, string terms, string? regex,
        string? pages, string? replacement) =>
        throw new FileApiException(501,
            "PDF redaction is not implemented. Removing text from a PDF means rewriting the "
            + "page content streams; drawing boxes over the words leaves them selectable and "
            + "extractable, which is worse than refusing. Use /redact once it ships.",
            title: "Not Implemented");

    // ────────────────────────────── Private Helpers ──────────────────────────────

    /// <summary>
    /// Create a <see cref="PdfReader"/> with unethical reading enabled for broad compatibility,
    /// reporting an unreadable file as a client error rather than an iText exception.
    /// </summary>
    /// <remarks>
    /// Only <see cref="GetMetrics"/> used to handle this. Every other entry point let
    /// iText's own exception escape, so posting a renamed .txt to /pdf/metadata answered
    /// 500 "An unexpected error occurred" — and the CLI printed a stack trace. Both now
    /// get a 422 that says what is actually wrong with the file.
    /// </remarks>
    private static PdfReader SafeReader(byte[] data)
    {
        try
        {
            var reader = new PdfReader(new MemoryStream(data));
            reader.SetUnethicalReading(true);
            return reader;
        }
        catch (Exception ex) when (ex is iText.IO.Exceptions.IOException or InvalidOperationException)
        {
            throw new FileApiException(422, UnreadableDetail(ex), title: "Unreadable PDF");
        }
    }

    /// <summary>
    /// Open the document, translating iText's failures the same way <see cref="SafeReader"/> does.
    /// </summary>
    /// <remarks>
    /// Decryption happens here rather than in the reader, so a password-protected file
    /// fails at this point and not the previous one.
    /// </remarks>
    private static PdfDocument OpenDocument(PdfReader reader, PdfWriter? writer = null)
    {
        try
        {
            return writer is null ? new PdfDocument(reader) : new PdfDocument(reader, writer);
        }
        catch (iText.Kernel.Exceptions.BadPasswordException)
        {
            throw new FileApiException(422,
                "This PDF is password-protected. Decrypt it with its password before running this operation.",
                title: "Password Required");
        }
        catch (Exception ex) when (ex is iText.IO.Exceptions.IOException or InvalidOperationException)
        {
            throw new FileApiException(422, UnreadableDetail(ex), title: "Unreadable PDF");
        }
    }

    private static string UnreadableDetail(Exception ex) =>
        $"The file could not be read as a PDF ({ex.Message}). "
        + "Check that it is not truncated, and that it is really a PDF rather than another "
        + "format that has been given a .pdf name.";

    /// <summary>
    /// A run of three or more spaces, the usual sign of a column boundary in
    /// text extracted from a table.
    /// </summary>
    [GeneratedRegex(@"\s{3,}")]
    private static partial Regex ColumnGap();

    /// <summary>Extract text from a single page using <see cref="LocationTextExtractionStrategy"/>.</summary>
    private static string ExtractPageText(PdfPage page) =>
        PdfTextExtractor.GetTextFromPage(page, new LocationTextExtractionStrategy());

    /// <summary>Count image XObjects on a single page.</summary>
    private static int CountPageImages(PdfPage page)
    {
        var xObjects = page.GetResources()?.GetResource(PdfName.XObject);
        if (xObjects is null) return 0;

        var count = 0;
        foreach (var key in xObjects.KeySet())
        {
            var stream = xObjects.GetAsStream(key);
            if (stream is not null && PdfName.Image.Equals(stream.GetAsName(PdfName.Subtype)))
                count++;
        }
        return count;
    }

    /// <summary>
    /// Heuristic table detection: counts groups of consecutive lines that contain
    /// tab-separated, pipe-separated, or multi-space-separated columnar data.
    /// </summary>
    private static int EstimateTableCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var tableCount = 0;
        var consecutiveRows = 0;

        foreach (var line in text.Split('\n'))
        {
            var isTabular = line.Count(c => c == '\t') >= 2
                            || line.Count(c => c == '|') >= 2
                            || ColumnGap().Count(line) >= 2;

            if (isTabular)
            {
                consecutiveRows++;
            }
            else
            {
                if (consecutiveRows >= 2) tableCount++;
                consecutiveRows = 0;
            }
        }
        if (consecutiveRows >= 2) tableCount++;

        return tableCount;
    }

    /// <summary>Check whether the PDF contains any digital signature form fields.</summary>
    private static bool HasDigitalSignature(PdfDocument doc)
    {
        try
        {
            var form = PdfFormCreator.GetAcroForm(doc, false);
            return form?.GetAllFormFields().Values.Any(f =>
                f.GetFormType()?.GetValue() == "/Sig") ?? false;
        }
        catch { return false; }
    }

    /// <summary>Heuristic: short all-caps lines with letters are likely headings.</summary>
    private static bool IsLikelyHeading(string line) =>
        line.Length is > 2 and < 80
        && !line.Any(char.IsLower)
        && line.Any(char.IsLetter)
        && !line.Contains('\t');

    /// <summary>Heuristic: short lines ending with colon or in title case may be subheadings.</summary>
    private static bool IsLikelySubheading(string line) =>
        line.Length is > 2 and < 100
        && !line.EndsWith('.')
        && (line.EndsWith(':') || (char.IsUpper(line[0]) && line.Count(c => c == ' ') <= 6));

    /// <summary>Parse a colour name or hex string into an iText <see cref="Color"/>.</summary>
    private static Color ParseColor(string color) => color.ToLowerInvariant() switch
    {
        "gray" or "grey" => ColorConstants.GRAY,
        "red" => ColorConstants.RED,
        "blue" => ColorConstants.BLUE,
        "green" => ColorConstants.GREEN,
        "black" => ColorConstants.BLACK,
        "white" => ColorConstants.WHITE,
        _ when color.StartsWith('#') && color.Length == 7 =>
            new DeviceRgb(
                Convert.ToInt32(color[1..3], 16),
                Convert.ToInt32(color[3..5], 16),
                Convert.ToInt32(color[5..7], 16)),
        _ => ColorConstants.GRAY
    };

    /// <summary>Compute (x, y) coordinates for a given position name and page geometry.</summary>
    private static (float X, float Y) ResolvePosition(
        string position, Rectangle sz, float margin, float textWidth) =>
        position.ToLowerInvariant() switch
        {
            "top-left" => (margin, sz.GetHeight() - margin),
            "top-center" => (sz.GetWidth() / 2 - textWidth / 2, sz.GetHeight() - margin),
            "top-right" => (sz.GetWidth() - margin - textWidth, sz.GetHeight() - margin),
            "bottom-left" => (margin, margin),
            "bottom-right" => (sz.GetWidth() - margin - textWidth, margin),
            _ => (sz.GetWidth() / 2 - textWidth / 2, margin) // bottom-center
        };

    /// <summary>Recursively walk the outline tree and collect bookmarks into a flat list.</summary>
    private static void WalkOutlines(
        PdfDocument doc, PdfOutline outline,
        List<Dictionary<string, object?>> result, int level)
    {
        foreach (var child in outline.GetAllChildren())
        {
            int? pageNum = null;
            var dest = child.GetDestination();
            if (dest is not null)
            {
                try
                {
                    var destPage = dest.GetDestinationPage(
                        doc.GetCatalog().GetNameTree(PdfName.Dests));
                    if (destPage is not null)
                    {
                        for (var i = 1; i <= doc.GetNumberOfPages(); i++)
                        {
                            if (doc.GetPage(i).GetPdfObject() == destPage)
                            { pageNum = i; break; }
                        }
                    }
                }
                catch { /* destination resolution can fail for malformed outlines */ }
            }

            result.Add(new Dictionary<string, object?>
            {
                ["title"] = child.GetTitle(),
                ["page"] = pageNum,
                ["level"] = level
            });

            WalkOutlines(doc, child, result, level + 1);
        }
    }
}
