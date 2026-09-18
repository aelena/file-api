# Aelena.FileApi.Core.Pdf

PDF operations for [`Aelena.FileApi.Core`](https://www.nuget.org/packages/Aelena.FileApi.Core),
built on iText 7.

> 📖 **Full documentation is in the
> [repository README](https://github.com/aelena/file-api#readme); the licence
> position is explained in full in
> [LICENSING.md](https://github.com/aelena/file-api/blob/main/LICENSING.md).**

---

## ⚠️ Read this before installing: this package is AGPL-3.0-or-later

It depends on [iText 7](https://itextpdf.com/), licensed under the **GNU Affero
General Public License v3** or, at your option, under a commercial licence sold
by iText Software.

The AGPL is a **strong copyleft** licence, and unlike the GPL its obligation is
triggered by **running the software as a network service**, not only by
distributing it. If you use this package in a web application or an API, the
obligation reaches your application's source.

Your options:

| If you… | Then… |
|---------|-------|
| Are building open source under a compatible licence | Use this package freely |
| Hold a commercial iText licence | Use this package; iText's terms govern, not the AGPL |
| Cannot accept the AGPL | **Do not install this package** — use `Aelena.FileApi.Core`, which is MIT |

Installing this package does **not** grant you a commercial iText licence. That
is an agreement between you and iText Software.

**Everything except PDF is MIT.**
[`Aelena.FileApi.Core`](https://www.nuget.org/packages/Aelena.FileApi.Core) gives
you DOCX, XLSX, PPTX, EPUB, MOBI, DjVu, legacy `.doc`, CSV, images, email,
hashing, PII detection, readability, text analysis and ZIP — with no reference
to iText, direct or transitive. PDF was split into this separate package precisely so the rest could
stay permissive.

## Install

```bash
dotnet add package Aelena.FileApi.Core.Pdf
```

Targets `net10.0` and `net11.0`. Brings in `Aelena.FileApi.Core`.

## What it does

**Read** — metrics (pages, words, tokens, images, OCR need, signatures,
corruption), metadata, text extraction by page range, Markdown conversion,
annotations, bookmarks, form fields, search with page numbers, health check.

**Write** — merge, split, rotate, reorder, delete pages, insert blank pages,
watermark, page numbers, encrypt, decrypt, unlock, compress, remove metadata.

**Typeset** — `MarkdownPdfService` renders Markdown to PDF (headings, lists,
tables, block quotes, code blocks, rules, inline emphasis), and converts the
EPUB, MOBI, DjVu and legacy `.doc` files that `Aelena.FileApi.Core` reads
straight through to PDF. Text is restricted to Windows-1252, because iText's
built-in fonts carry no Unicode glyphs and embedding a font would mean shipping
one: characters outside CP1252 are transliterated where there is an obvious
reading and replaced with `?` where there is not.

### Reading

```csharp
using Aelena.FileApi.Core.Services.Pdf;

var bytes = await File.ReadAllBytesAsync("report.pdf");

var metrics = PdfService.GetMetrics(bytes, "report.pdf");
Console.WriteLine($"{metrics.PageCount} pages, OCR needed: {metrics.OcrNeeded}");

// Page ranges are 1-based and accept lists, spans and mixtures of the two.
var text = PdfService.ExtractText(bytes, "report.pdf", pages: "1,3,5-8");
foreach (var page in text.Pages)
    Console.WriteLine($"--- Page {page.Page} ---\n{page.Text}");

// Search reports the page each hit landed on, with context either side.
// Pass a literal query or a regex pattern; the other stays null.
var (_, hits) = PdfService.Search(bytes, "report.pdf", query: "indemnity", pattern: null);
foreach (var hit in hits)
    Console.WriteLine($"p{hit.Page}: {hit.Context}");

// Headings are inferred from layout; columns and tables are not reconstructed.
Console.WriteLine(PdfService.ExtractToMarkdown(bytes, "report.pdf").Markdown);
```

### Writing

Write operations return the new file name and bytes, so they compose without
touching the disk in between:

```csharp
using Aelena.FileApi.Core.Services.Pdf;

var cover = await File.ReadAllBytesAsync("cover.pdf");
var body = await File.ReadAllBytesAsync("body.pdf");

// Merge, stamp, number, then lock — each step feeding the next.
var (_, merged) = PdfService.MergePdfs([(cover, "cover.pdf"), (body, "body.pdf")]);

var (_, stamped) = PdfService.AddWatermark(
    merged, "merged.pdf", text: "DRAFT", color: "gray",
    opacity: 0.3f, fontSize: 60, angle: 45, position: "center");

// {n} is the page number, {total} the page count.
var (_, numbered) = PdfService.AddPageNumbers(
    stamped, "merged.pdf", position: "bottom-center", fontSize: 12,
    start: 1, margin: 36, fontColor: "black", fmt: "{n} / {total}");

var (name, finished) = PdfService.EncryptPdf(
    numbered, "merged.pdf", userPassword: "open-me", ownerPassword: null);

await File.WriteAllBytesAsync(name, finished);
```

`SplitPdf` takes semicolon-separated page groups and returns a ZIP of the
pieces:

```csharp
var report = await File.ReadAllBytesAsync("report.pdf");

// Three documents out of one: pages 1-4, 5-9, and 10 to the end.
var (zipName, zipBytes) = PdfService.SplitPdf(report, "report.pdf", "1-4;5-9;10-");
await File.WriteAllBytesAsync(zipName, zipBytes);
```

`RotatePages`, `ReorderPages`, `DeletePages`, `InsertBlankPages`, `CompressPdf`,
`RemoveMetadata`, `DecryptPdf` and `UnlockPdf` all follow the same shape.

### Typesetting

`MarkdownPdfService` renders Markdown, and converts anything
`Aelena.FileApi.Core` can read to PDF in one call:

```csharp
using Aelena.FileApi.Core.Services.Pdf;

// Markdown in, PDF out.
var (name, pdf) = MarkdownPdfService.FromMarkdown(
    await File.ReadAllTextAsync("notes.md"), "notes.md",
    title: "Release notes", author: "aelena");

await File.WriteAllBytesAsync(name, pdf);

// Or an EPUB, MOBI, DjVu or legacy .doc straight through: the source is
// detected, validated and extracted, then typeset. Its title and authors
// become the PDF's document properties.
var book = await File.ReadAllBytesAsync("book.epub");
var (bookName, bookPdf) = MarkdownPdfService.ConvertDocument(book, "book.epub");
```

A source this service cannot decode — a DRM-locked book, a DjVu whose text layer
is BZZ-compressed — raises rather than producing a blank PDF.

### Failures

An unreadable or password-protected file raises `FileApiException` with a
message saying what is actually wrong, rather than surfacing an iText exception:

```csharp
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Services.Pdf;

var locked = await File.ReadAllBytesAsync("locked.pdf");

try
{
    PdfService.ExtractText(locked, "locked.pdf");
}
catch (FileApiException ex) when (ex.StatusCode == 422)
{
    // "This PDF is password-protected. Decrypt it with its password before
    //  running this operation."
    Console.Error.WriteLine(ex.Detail);
}
```

`GetMetrics` is the deliberate exception: it answers for a broken document with
`IsCorrupt = true`, because asking whether a file is usable is its job.

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


## Not implemented

`ExtractTables` and `RedactText` throw `FileApiException` with status 501.

Both previously returned a plausible-looking success: table extraction always
reported zero tables, and redaction returned the document **unmodified** with a
non-zero redaction count, so text the caller asked to remove was still fully
extractable. Refusing is safer than a wrong answer that looks right. See the
[changelog](https://github.com/aelena/file-api/blob/main/CHANGELOG.md).

## More

- 📖 [Repository and full documentation](https://github.com/aelena/file-api#readme)
- ⚖️ [Licensing explained in detail](https://github.com/aelena/file-api/blob/main/LICENSING.md)
- 📝 [Changelog](https://github.com/aelena/file-api/blob/main/CHANGELOG.md)
- 🐛 [Issues](https://github.com/aelena/file-api/issues)
