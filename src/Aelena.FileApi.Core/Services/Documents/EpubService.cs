using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Models;

namespace Aelena.FileApi.Core.Services.Documents;

/// <summary>
/// Reads EPUB 2 and EPUB 3 books: validates the container, reads the OPF
/// metadata, and walks the spine to produce text and Markdown.
/// <para>
/// Nothing here inflates an entry before the central directory has been checked
/// for it. An EPUB is a ZIP, and a ZIP that arrives over HTTP is untrusted
/// input: the size and entry-count limits below are what stop a 40 KB upload
/// from expanding into gigabytes of text.
/// </para>
/// </summary>
public static class EpubService
{
    /// <summary>An EPUB with more entries than this is not a book.</summary>
    private const int MaxEntries = 8192;

    /// <summary>Cap on the total declared uncompressed size of the archive.</summary>
    private const long MaxTotalUncompressed = 512L * 1024 * 1024;

    /// <summary>Cap on any single document read out of the spine.</summary>
    private const long MaxEntryUncompressed = 64L * 1024 * 1024;

    /// <summary>Compression ratio above which an entry is treated as hostile.</summary>
    private const long MaxCompressionRatio = 500;

    private const string DcNs = "http://purl.org/dc/elements/1.1/";
    private const string ContainerNs = "urn:oasis:names:tc:opendocument:xmlns:container";

    // ── Validation ───────────────────────────────────────────────────────

    /// <summary>
    /// Run every structural check without extracting content. Callers get the
    /// full issue list rather than a single first failure, because "this EPUB
    /// has no <c>dc:language</c>" and "this EPUB has no spine" need very
    /// different responses and only the caller can decide which matters.
    /// </summary>
    public static DocumentValidationResponse Validate(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        var issues = new List<HealthIssue>();
        var details = new Dictionary<string, object>(StringComparer.Ordinal);
        var canExtract = false;

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            CheckArchiveLimits(archive, issues, details);
            CheckMimetype(archive, issues);

            if (archive.GetEntry("META-INF/encryption.xml") is not null)
            {
                issues.Add(new HealthIssue("drm", "error",
                    "META-INF/encryption.xml is present: this book's content is encrypted (DRM). " +
                    "Nothing can be extracted from it without the rights key."));
            }

            var opfPath = ReadContainer(archive, issues);
            if (opfPath is not null)
            {
                details["opfPath"] = opfPath;
                var package = ReadPackage(archive, opfPath, issues);

                if (package is not null)
                {
                    details["spineItems"] = package.Spine.Count;
                    details["manifestItems"] = package.Manifest.Count;
                    details["epubVersion"] = package.Version ?? "unknown";

                    if (package.Title is null)
                        issues.Add(new HealthIssue("metadata", "warning", "No dc:title in the package metadata."));
                    if (package.Language is null)
                        issues.Add(new HealthIssue("metadata", "warning", "No dc:language in the package metadata."));

                    canExtract = package.Spine.Count > 0
                              && issues.TrueForAll(i => i.Severity != "error");
                }
            }
        }
        catch (InvalidDataException ex)
        {
            issues.Add(new HealthIssue("container", "error", $"Not a readable ZIP archive: {ex.Message}"));
        }

        return Build(fileName, "EPUB", issues, canExtract, details);
    }

    // ── Metadata ─────────────────────────────────────────────────────────

    /// <summary>
    /// Read the OPF metadata without touching the content documents, so a book
    /// whose spine is broken still answers with its title and author.
    /// </summary>
    /// <exception cref="FileApiException">422 when the package document cannot be read.</exception>
    public static DocumentMetadataResponse GetMetadata(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var stream = new MemoryStream(data, writable: false);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new FileApiException(422, $"This EPUB is not a readable ZIP archive: {ex.Message}",
                title: "Unreadable EPUB");
        }

        using (archive)
        {
            var issues = new List<HealthIssue>();

            var opfPath = ReadContainer(archive, issues);
            Throw(issues);

            var package = ReadPackage(archive, opfPath!, issues);
            Throw(issues);

            return package!.ToMetadata(fileName, package.Spine.Count);
        }
    }

    // ── Extraction ───────────────────────────────────────────────────────

    /// <summary>
    /// Read the whole book: metadata from the OPF, one section per spine item.
    /// </summary>
    /// <exception cref="FileApiException">
    /// 422 when the container is structurally unusable, 501 when it is encrypted.
    /// </exception>
    public static ExtractedDocument Extract(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var stream = new MemoryStream(data, writable: false);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new FileApiException(422, $"This EPUB is not a readable ZIP archive: {ex.Message}",
                title: "Unreadable EPUB");
        }

        using (archive)
        {
            if (archive.GetEntry("META-INF/encryption.xml") is not null)
            {
                throw new FileApiException(501,
                    "This EPUB is DRM-encrypted (META-INF/encryption.xml is present). " +
                    "Its content cannot be extracted without the rights key, which this service does not handle.",
                    title: "Encrypted EPUB");
            }

            var fatal = new List<HealthIssue>();
            CheckArchiveLimits(archive, fatal, new Dictionary<string, object>(StringComparer.Ordinal));
            Throw(fatal);

            var opfPath = ReadContainer(archive, fatal);
            Throw(fatal);

            var package = ReadPackage(archive, opfPath!, fatal);
            Throw(fatal);

            if (package!.Spine.Count == 0)
            {
                throw new FileApiException(422,
                    "This EPUB's spine is empty, so it declares no reading order and there is nothing to extract.",
                    title: "Empty EPUB");
            }

            var sections = new List<DocumentSection>(package.Spine.Count);
            var index = 0;

            foreach (var href in package.Spine)
            {
                var entry = archive.GetEntry(href);
                if (entry is null || entry.Length > MaxEntryUncompressed)
                    continue;

                var html = ReadEntry(entry);
                var text = HtmlText.ToPlainText(html);
                if (text.Length == 0)
                    continue;

                index++;
                sections.Add(new DocumentSection(
                    Index: index,
                    Title: HtmlText.Title(html),
                    Text: text,
                    Markdown: HtmlText.ToMarkdown(html)));
            }

            if (sections.Count == 0)
            {
                throw new FileApiException(422,
                    "Every document in this EPUB's spine was missing from the archive or empty, " +
                    "so no text could be extracted. Run /convert/validate on it for the details.",
                    title: "No Extractable Content");
            }

            return new ExtractedDocument(
                DocumentFormat.Epub,
                sections,
                package.ToMetadata(fileName, sections.Count));
        }
    }

    // ── Container ────────────────────────────────────────────────────────

    private static void CheckArchiveLimits(
        ZipArchive archive, List<HealthIssue> issues, Dictionary<string, object> details)
    {
        if (archive.Entries.Count > MaxEntries)
        {
            issues.Add(new HealthIssue("limits", "error",
                $"Archive holds {archive.Entries.Count} entries, above the {MaxEntries} limit for an EPUB."));
            return;
        }

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            total += entry.Length;

            if (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxCompressionRatio)
            {
                issues.Add(new HealthIssue("limits", "error",
                    $"Entry '{entry.FullName}' expands {entry.Length / entry.CompressedLength}× on decompression, " +
                    $"above the {MaxCompressionRatio}× limit. This archive is refused as a decompression bomb."));
                return;
            }
        }

        details["totalUncompressedBytes"] = total;
        details["entryCount"] = archive.Entries.Count;

        if (total > MaxTotalUncompressed)
        {
            issues.Add(new HealthIssue("limits", "error",
                $"Archive expands to {total:N0} bytes, above the {MaxTotalUncompressed:N0} byte limit."));
        }
    }

    private static void CheckMimetype(ZipArchive archive, List<HealthIssue> issues)
    {
        var entry = archive.GetEntry("mimetype");
        if (entry is null)
        {
            issues.Add(new HealthIssue("mimetype", "error",
                "No 'mimetype' entry. Every EPUB is required to carry one declaring application/epub+zip."));
            return;
        }

        var declared = ReadEntry(entry).Trim();
        if (declared != "application/epub+zip")
        {
            issues.Add(new HealthIssue("mimetype", "error",
                $"The 'mimetype' entry declares '{declared}', not 'application/epub+zip'."));
        }

        // Ordering and compression are a conformance matter, not a readability
        // one — plenty of shipped books get this wrong and open fine everywhere.
        if (archive.Entries[0] != entry)
        {
            issues.Add(new HealthIssue("mimetype", "warning",
                "The 'mimetype' entry is not first in the archive, which OCF requires. The book is still readable."));
        }

        if (entry.CompressedLength != entry.Length)
        {
            issues.Add(new HealthIssue("mimetype", "warning",
                "The 'mimetype' entry is compressed; OCF requires it to be stored. The book is still readable."));
        }
    }

    private static string? ReadContainer(ZipArchive archive, List<HealthIssue> issues)
    {
        var entry = archive.GetEntry("META-INF/container.xml");
        if (entry is null)
        {
            issues.Add(new HealthIssue("container", "error",
                "No META-INF/container.xml, so the archive does not say where its package document is."));
            return null;
        }

        var doc = ParseXml(ReadEntry(entry), "META-INF/container.xml", issues);
        if (doc is null)
            return null;

        var rootfile = doc.Descendants(XName.Get("rootfile", ContainerNs))
            .Concat(doc.Descendants().Where(e => e.Name.LocalName == "rootfile"))
            .FirstOrDefault();

        var path = rootfile?.Attribute("full-path")?.Value;
        if (string.IsNullOrWhiteSpace(path))
        {
            issues.Add(new HealthIssue("container", "error",
                "META-INF/container.xml names no rootfile, so the package document cannot be located."));
            return null;
        }

        return NormalisePath(Uri.UnescapeDataString(path));
    }

    private static Package? ReadPackage(ZipArchive archive, string opfPath, List<HealthIssue> issues)
    {
        var entry = archive.GetEntry(opfPath);
        if (entry is null)
        {
            issues.Add(new HealthIssue("package", "error",
                $"container.xml points at '{opfPath}', which is not in the archive."));
            return null;
        }

        var doc = ParseXml(ReadEntry(entry), opfPath, issues);
        if (doc?.Root is null)
            return null;

        var root = doc.Root;
        var baseDir = opfPath.Contains('/', StringComparison.Ordinal)
            ? opfPath[..opfPath.LastIndexOf('/')]
            : "";

        // Manifest: id → resolved archive path.
        var manifest = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = 0;

        foreach (var item in Elements(root, "manifest").SelectMany(m => Elements(m, "item")))
        {
            var id = item.Attribute("id")?.Value;
            var href = item.Attribute("href")?.Value;
            if (id is null || string.IsNullOrWhiteSpace(href))
                continue;

            // Fragment identifiers and query strings are not part of the path.
            var clean = href.Split('#')[0].Split('?')[0];
            var resolved = NormalisePath(Combine(baseDir, Uri.UnescapeDataString(clean)));
            manifest[id] = resolved;

            if (archive.GetEntry(resolved) is null)
                missing++;
        }

        if (missing > 0)
        {
            issues.Add(new HealthIssue("manifest", "warning",
                $"{missing} manifest item(s) reference files that are not in the archive.",
                new Dictionary<string, object> { ["missingItems"] = missing }));
        }

        // Spine: reading order, restricted to documents that actually exist.
        var spine = new List<string>();
        var unresolved = 0;

        foreach (var itemref in Elements(root, "spine").SelectMany(s => Elements(s, "itemref")))
        {
            var idref = itemref.Attribute("idref")?.Value;
            if (idref is null || !manifest.TryGetValue(idref, out var path))
            {
                unresolved++;
                continue;
            }

            if (archive.GetEntry(path) is not null)
                spine.Add(path);
            else
                unresolved++;
        }

        if (unresolved > 0)
        {
            issues.Add(new HealthIssue("spine", "warning",
                $"{unresolved} spine entr(ies) do not resolve to a document in the archive.",
                new Dictionary<string, object> { ["unresolvedItems"] = unresolved }));
        }

        if (spine.Count == 0)
        {
            issues.Add(new HealthIssue("spine", "error",
                "The spine is empty or resolves to nothing, so the book declares no readable content."));
        }

        var metadata = Elements(root, "metadata").FirstOrDefault();

        return new Package(
            Version: root.Attribute("version")?.Value,
            Title: Dc(metadata, "title"),
            Authors: DcAll(metadata, "creator"),
            Language: Dc(metadata, "language"),
            Publisher: Dc(metadata, "publisher"),
            Identifier: Dc(metadata, "identifier"),
            Published: Dc(metadata, "date"),
            Description: Dc(metadata, "description"),
            Rights: Dc(metadata, "rights"),
            Subjects: DcAll(metadata, "subject"),
            Manifest: manifest,
            Spine: spine);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static XDocument? ParseXml(string xml, string where, List<HealthIssue> issues)
    {
        try
        {
            // DtdProcessing.Prohibit is the point of going through XmlReader here:
            // XDocument.Parse would happily follow an external DTD reference in an
            // uploaded file and turn this into an XXE / billion-laughs vector.
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = true
            });
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            issues.Add(new HealthIssue("xml", "error", $"{where} is not well-formed XML: {ex.Message}"));
            return null;
        }
    }

    private static IEnumerable<XElement> Elements(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName == localName);

    private static string? Dc(XElement? metadata, string localName) =>
        DcAll(metadata, localName).FirstOrDefault();

    private static List<string> DcAll(XElement? metadata, string localName)
    {
        if (metadata is null)
            return [];

        return
        [
            .. metadata.Elements()
                .Where(e => e.Name.LocalName == localName
                         && (e.Name.NamespaceName == DcNs || e.Name.NamespaceName.Length == 0))
                .Select(e => e.Value.Trim())
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.Ordinal)
        ];
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        var limit = (int)Math.Min(entry.Length, MaxEntryUncompressed);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var buffer = new char[limit == 0 ? 0 : limit];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = reader.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }

        return new string(buffer, 0, read);
    }

    private static string Combine(string baseDir, string href) =>
        baseDir.Length == 0 ? href : $"{baseDir}/{href}";

    /// <summary>
    /// Collapse '.' and '..' segments and normalise separators, so a relative
    /// href that climbs out of the OPF directory still resolves to the ZIP
    /// entry name it means — and cannot climb above the archive root.
    /// </summary>
    private static string NormalisePath(string path)
    {
        var segments = new List<string>();

        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    private static void Throw(List<HealthIssue> issues)
    {
        var error = issues.Find(i => i.Severity == "error");
        if (error is not null)
            throw new FileApiException(422, error.Message, title: "Invalid EPUB");
    }

    internal static DocumentValidationResponse Build(
        string fileName, string format, List<HealthIssue> issues,
        bool canExtract, Dictionary<string, object> details)
    {
        var errors = issues.Count(i => i.Severity == "error");
        var warnings = issues.Count(i => i.Severity == "warning");

        return new DocumentValidationResponse(
            FileName: fileName,
            Format: format,
            Valid: errors == 0,
            CanExtractText: canExtract,
            IssueCount: issues.Count,
            ErrorCount: errors,
            WarningCount: warnings,
            Issues: issues,
            Details: details.Count > 0 ? details : null);
    }

    private sealed record Package(
        string? Version,
        string? Title,
        List<string> Authors,
        string? Language,
        string? Publisher,
        string? Identifier,
        string? Published,
        string? Description,
        string? Rights,
        List<string> Subjects,
        Dictionary<string, string> Manifest,
        List<string> Spine)
    {
        public DocumentMetadataResponse ToMetadata(string fileName, int sectionCount)
        {
            var extra = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Version is not null)
                extra["epubVersion"] = Version;
            extra["spineItems"] = Spine.Count.ToString(CultureInfo.InvariantCulture);
            extra["manifestItems"] = Manifest.Count.ToString(CultureInfo.InvariantCulture);

            return new DocumentMetadataResponse(
                FileName: fileName,
                Format: "EPUB",
                Title: Title,
                Authors: Authors.Count > 0 ? Authors : null,
                Language: Language,
                Publisher: Publisher,
                Identifier: Identifier,
                Published: Published,
                Description: Description,
                Rights: Rights,
                Subjects: Subjects.Count > 0 ? Subjects : null,
                SectionCount: sectionCount,
                Extra: extra);
        }
    }
}
