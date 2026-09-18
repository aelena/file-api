# Aelena.FileApi.Core

Pure document processing for .NET — DOCX, XLSX, PPTX, EPUB, MOBI, DjVu, legacy
`.doc`, CSV, images, email, hashing, PII detection, readability, text analysis
and ZIP inspection.

No ASP.NET dependencies and no copyleft dependencies, so it works equally well in
a console app, a desktop app, an Azure Function, or behind an HTTP or gRPC host.
Every operation is a static method taking `byte[]` and returning an immutable
record — no streams to manage, no `IFormFile`, no shared mutable state.

**MIT licensed.** Nothing in this package's dependency graph is copyleft.

> 📖 **Full documentation, examples and the complete endpoint list are in the
> [repository README](https://github.com/aelena/file-api#readme).**

---

## Install

```bash
dotnet add package Aelena.FileApi.Core
```

Targets `net10.0` and `net11.0`.

## What it does

| Area | Operations |
|------|-----------|
| **DOCX** | Metrics, metadata, paragraph extraction, Markdown conversion, search, health check, metadata removal |
| **XLSX** | Metrics, sheets, cell values with shared strings resolved, CSV and Markdown conversion, metadata, search, plus audits for external links, risky formulas and hidden sheets/rows/columns |
| **PPTX** | Slides in presentation order, speaker notes, Markdown outline, metrics, metadata, search |
| **CSV** | Dialect sniffing, load validation, column profiling, JSON and Markdown conversion |
| **EPUB** | Container and OPF validation, metadata, spine walk to text and Markdown; DRM and decompression bombs refused |
| **MOBI / PalmDOC** | Palm database and MOBI header validation, EXTH metadata, PalmDOC LZ77 decompression to text and Markdown |
| **DjVu** | IFF container walk, page count and geometry, text-layer detection, text extraction from uncompressed `TXTa` chunks |
| **Legacy `.doc`** | OLE2 compound file reader, FIB and piece-table walk, text and Markdown from Word 97-2003 binaries |
| **Images** | Resize, rotate, crop, convert (PNG/JPEG/WebP/BMP/GIF/TIFF), thumbnail, flip, blur, grayscale, compress, strip metadata, EXIF, auto-orient, invert, edge detect, equalize, colour palette, base64 |
| **Email** | `.eml` (RFC 5322 / MIME) via MimeKit, and `.msg` (Outlook) read from its OLE2 container — headers, body, recipients, attachments |
| **Hashing** | SHA-256, MD5, SHA-1, and a composite hash that folds in filename and size |
| **PII** | Emails, credit cards, IBANs, SSNs, phone numbers, national IDs (US, ES, FR, DE, IT, UK, PT), dates of birth |
| **Text** | Word/char/token counts, language detection, literal and regex search with context |
| **Encoding** | Encoding and BOM detection, line endings, control-byte location with line and column, normalisation to clean UTF-8 |
| **Readability** | Flesch Reading Ease, Flesch-Kincaid, Gunning Fog, SMOG |
| **ZIP** | Entry listing with sizes, compression and CRC-32, without extracting |
| **Persistence** | SQLite-backed share links; a bounded in-memory job store |

## Examples

Every operation is a static method taking `byte[]` and a file name, and
returning an immutable record. There is nothing to construct, register or
dispose.

### Word documents

```csharp
using Aelena.FileApi.Core.Services.Docx;

var docx = await File.ReadAllBytesAsync("report.docx");

var metrics = DocxService.GetMetrics(docx, "report.docx");
Console.WriteLine($"{metrics.ParagraphCount} paragraphs, {metrics.WordCount} words, "
                + $"{metrics.TableCount} tables, {metrics.ImageCount} images");

// Headings, lists, tables and emphasis survive the conversion.
Console.WriteLine(DocxService.ExtractToMarkdown(docx, "report.docx").Markdown);

// Tracked changes, comments, macros — things you want to know about before
// a document leaves the building.
var health = DocxService.HealthCheck(docx, "report.docx");
foreach (var issue in health.Issues)
    Console.WriteLine($"[{issue.Severity}] {issue.Check}: {issue.Message}");

// Strip authorship and revision history, keeping the content.
var (cleanName, cleanBytes) = DocxService.RemoveMetadata(docx, "report.docx");
await File.WriteAllBytesAsync(cleanName, cleanBytes);
```

### Ebooks and legacy office formats

One façade for EPUB, MOBI/PalmDOC, DjVu and legacy binary `.doc`. It identifies
the file from its content rather than its name, and validates it before
converting anything.

```csharp
using Aelena.FileApi.Core.Services.Documents;

var book = await File.ReadAllBytesAsync("book.epub");

// What is it, really? A renamed file is the normal case, not the exception.
var detected = DocumentConversionService.Detect(book, "book.epub");
Console.WriteLine($"{detected.Format}, extension disagrees: {detected.ExtensionMismatch}");

// Every structural issue at once — a report, not a refusal.
var report = DocumentConversionService.Validate(book, "book.epub");
foreach (var issue in report.Issues)
    Console.WriteLine($"[{issue.Severity}] {issue.Check}: {issue.Message}");

if (report.CanExtractText)
{
    var meta = DocumentConversionService.GetMetadata(book, "book.epub");
    Console.WriteLine($"{meta.Title} — {string.Join(", ", meta.Authors ?? [])}");

    var (mdName, mdBytes) = DocumentConversionService.ToMarkdownFile(book, "book.epub");
    await File.WriteAllBytesAsync(mdName, mdBytes);
}
```

`Validate` answers for any supported format, so it doubles as a triage pass over
a mixed directory:

```csharp
foreach (var path in Directory.EnumerateFiles("inbox"))
{
    var bytes = await File.ReadAllBytesAsync(path);
    var name = Path.GetFileName(path);

    if (!DocumentConversionService.Detect(bytes, name).Supported)
        continue;

    var check = DocumentConversionService.Validate(bytes, name);
    Console.WriteLine($"{name,-30} {check.Format,-8} "
                    + $"valid={check.Valid} text={check.CanExtractText} "
                    + $"({check.ErrorCount} errors, {check.WarningCount} warnings)");
}
```

Recognised-but-undecodable content raises a `501`, never an empty success: DRM
in EPUB and MOBI, HUFF/CDIC-compressed MOBI, and BZZ-compressed DjVu text layers
each say so, and say what to do instead.

### Spreadsheets and presentations

Both reuse the Open XML SDK that DOCX already brings in, so neither costs a new
dependency.

```csharp
using Aelena.FileApi.Core.Services.Xlsx;
using Aelena.FileApi.Core.Services.Pptx;

var workbook = await File.ReadAllBytesAsync("budget.xlsx");

// Cell values come back as text a reader would see: a workbook stores most
// strings once in a shared table, so a naive XML scrape returns indices.
var sheet = XlsxService.GetSheet(workbook, "budget.xlsx", sheet: "Q3");
foreach (var row in sheet.Rows.Take(5))
    Console.WriteLine(string.Join(" | ", row));

// What does this workbook reach out to, and what is it not showing?
var links = XlsxService.AuditLinks(workbook, "budget.xlsx");
foreach (var formula in links.RiskyFormulas)
    Console.WriteLine($"{formula.Sheet}!{formula.Cell} calls {formula.Function}");

var hidden = XlsxService.FindHidden(workbook, "budget.xlsx");
Console.WriteLine($"{hidden.HiddenSheetCount} hidden sheet(s), "
                + $"{hidden.HiddenColumnCount} hidden column(s) — still in the file");

// Speaker notes, which most extraction tools drop.
var deck = await File.ReadAllBytesAsync("deck.pptx");
foreach (var slide in PptxService.GetSlides(deck, "deck.pptx").Slides)
    Console.WriteLine($"{slide.Number}. {slide.Title}
   notes: {slide.Notes}");
```

### Delimited files

Nothing here is told how the file is delimited; the dialect is inferred and
reported back.

```csharp
using Aelena.FileApi.Core.Services.Common;

var csv = await File.ReadAllBytesAsync("export.csv");

var shape = CsvService.Inspect(csv, "export.csv");
Console.WriteLine($"{shape.DelimiterName}, header: {shape.HasHeader}, "
                + $"{shape.ColumnCount} columns, {shape.RaggedRowCount} ragged row(s)");

foreach (var issue in shape.Issues)
    Console.WriteLine($"[{issue.Severity}] {issue.Message}");

foreach (var column in CsvService.Profile(csv, "export.csv").Columns)
    Console.WriteLine($"{column.Name}: {column.InferredType}, "
                    + $"{column.EmptyCount} empty, {column.DistinctCount} distinct");
```

### Text encoding

```csharp
using Aelena.FileApi.Core.Services.Common;

var file = await File.ReadAllBytesAsync("README.md");
var encoding = TextEncodingService.Detect(file, "README.md");

Console.WriteLine($"{encoding.Encoding}, {encoding.LineEnding} line endings");

// A stray control byte is valid UTF-8, invisible in an editor, and fatal to
// whatever consumes the file next. This says which byte and where.
foreach (var hit in encoding.ControlBytes)
    Console.WriteLine($"{hit.Byte} ({hit.Name}) at line {hit.Line}, column {hit.Column}");

var (_, clean) = TextEncodingService.Normalise(file, "README.md");
```

### Images

Every transform returns the new bytes with the media type to serve them under,
so the result can go straight into a response or onto disk.

```csharp
using Aelena.FileApi.Core.Services.Image;

var photo = await File.ReadAllBytesAsync("photo.jpg");

// EXIF, including GPS where the camera recorded it.
var exif = ImageService.GetExif(photo, "photo.jpg");
Console.WriteLine($"{exif.Width}x{exif.Height} {exif.Format}");
foreach (var (key, value) in exif.Gps ?? new Dictionary<string, string>())
    Console.WriteLine($"  {key}: {value}");

// Width only: the height follows from the aspect ratio.
var (name, resized, mediaType) = ImageService.Resize(photo, "photo.jpg", width: 800, height: null);
await File.WriteAllBytesAsync(name, resized);

// Format conversion, and metadata removal before publishing.
var (webpName, webp, _) = ImageService.Convert(photo, "photo.jpg", targetFormat: "webp");
var (strippedName, stripped, _) = ImageService.StripMetadata(photo, "photo.jpg");

var palette = ImageService.ExtractColorPalette(photo, "photo.jpg", numColors: 5);
Console.WriteLine($"dominant {palette.DominantColor}");
foreach (var colour in palette.Palette)
    Console.WriteLine($"  {colour.Hex} {colour.Percentage:F1}%");
```

### Text, hashing, PII and readability

```csharp
using Aelena.FileApi.Core.Services.Common;

var bytes = await File.ReadAllBytesAsync("contract.txt");
var text = Encoding.UTF8.GetString(bytes);

// Three digests plus a composite that folds in the name and size, so two
// files with identical content but different names hash differently.
var hashes = HashService.ComputeHash(bytes, "contract.txt");
Console.WriteLine($"{hashes.Sha256}  (composite {hashes.CompositeSha256})");

// Personal data, with position and surrounding context.
var pii = PiiService.Detect(text, "contract.txt");
foreach (var match in pii.Matches)
    Console.WriteLine($"{match.PiiType}: {match.Value} at {match.Start} — \"{match.Context}\"");

var score = ReadabilityService.Analyse(text, "contract.txt", language: "en");
Console.WriteLine($"Flesch {score.FleschReadingEase:F1} — {score.Interpretation}");

// Literal or regex search, with context around each hit.
var (_, matches) = TxtService.Search(bytes, "contract.txt", pattern: @"\b[A-Z]{2,}\b");
Console.WriteLine($"{matches.Count} acronym(s)");
```

### Email and archives

```csharp
using Aelena.FileApi.Core.Services.Common;

var eml = await File.ReadAllBytesAsync("message.eml");
var mail = EmailService.Parse(eml, "message.eml");

Console.WriteLine($"{mail.FromAddress} -> {string.Join(", ", mail.To ?? [])}");
Console.WriteLine($"{mail.Subject} ({mail.Date})");
foreach (var attachment in mail.Attachments ?? [])
    Console.WriteLine($"  {attachment.Filename} {attachment.ContentType} {attachment.SizeBytes:N0} bytes");

// Entries come from the central directory, so a zip bomb costs no more to
// inspect than a well-behaved archive of the same size.
var zip = await File.ReadAllBytesAsync("archive.zip");
var listing = ZipService.Inspect(zip, "archive.zip");

Console.WriteLine($"{listing.TotalFiles} files, {listing.TotalUncompressedSize:N0} bytes unpacked");
foreach (var entry in listing.Entries.Where(e => !e.IsDir))
    Console.WriteLine($"  {entry.Filename,-40} {entry.FileSize,10:N0} {entry.CompressionMethod}");
```

### Failures

Expected failures — an unsupported format, an out-of-range page, a malformed
regex, a password-protected file — throw `FileApiException`, which carries an
HTTP status code and a message written for the caller rather than the
maintainer:

```csharp
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Services.Documents;

var scan = await File.ReadAllBytesAsync("scan.djvu");

try
{
    DocumentConversionService.ExtractText(scan, "scan.djvu");
}
catch (FileApiException ex)
{
    // 415 wrong format for this call, 422 broken file,
    // 501 readable but not decodable here.
    Console.Error.WriteLine($"{ex.StatusCode} {ex.Title}: {ex.Detail}");
}
```

Caller-supplied regular expressions run with a match timeout, so a pathological
pattern reports a `400` instead of pinning a thread.

## PDF is a separate package, deliberately

PDF operations live in
**[`Aelena.FileApi.Core.Pdf`](https://www.nuget.org/packages/Aelena.FileApi.Core.Pdf)**,
which is **AGPL-3.0-or-later** because it is built on iText 7.

That separation is the reason this package can be MIT: it has no reference to
iText, direct or transitive. CI asserts it on every push by inspecting both the
declared dependencies and the files inside the packed `.nupkg`, so the boundary
cannot quietly erode. Add the PDF package only if you have read the AGPL and
accept it, or hold a commercial iText licence.

## The three packages

| Package | Licence | Depends on | Contains |
|---------|---------|-----------|----------|
| [`Aelena.FileApi.Core`](https://www.nuget.org/packages/Aelena.FileApi.Core) | MIT | — | Everything except PDF |
| [`Aelena.FileApi.Core.Pdf`](https://www.nuget.org/packages/Aelena.FileApi.Core.Pdf) | AGPL-3.0-or-later | `Core`, iText 7 | PDF only |
| [`Aelena.FileApi.Cli`](https://www.nuget.org/packages/Aelena.FileApi.Cli) | AGPL-3.0-or-later | bundled, not declared | Both, as a `dotnet tool` |

```
Aelena.FileApi.Core          MIT, no iText anywhere in its graph
        ▲
        │ depends on
        │
Aelena.FileApi.Core.Pdf      AGPL — adds iText 7, and only this package does
```

`Core.Pdf` depends on `Core`, never the reverse: installing the PDF package gives
you the whole toolkit, while installing `Core` alone keeps every copyleft
dependency out of your build. The CLI is a tool package, so it bundles its
dependencies rather than declaring them — its empty dependency list does not mean
iText is absent from it.


## Note on ImageSharp

Imaging goes through `SixLabors.ImageSharp`, under the Six Labors Split License.
The clause that matters grants Apache 2.0 to anyone "consuming the Work as a
**Transitive Package Dependency**" — which is what installing this package makes
it. So you receive ImageSharp under Apache 2.0 regardless of your organisation's
size; the commercial threshold applies to a *direct* dependency on ImageSharp,
not to users of this package.

## More

- 📖 [Repository and full documentation](https://github.com/aelena/file-api#readme)
- ⚖️ [Licensing explained in detail](https://github.com/aelena/file-api/blob/main/LICENSING.md)
- 📝 [Changelog](https://github.com/aelena/file-api/blob/main/CHANGELOG.md)
- 🐛 [Issues](https://github.com/aelena/file-api/issues)
