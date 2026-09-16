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
you DOCX, images, email, hashing, PII detection, readability, text analysis and
ZIP — with no reference to iText, direct or transitive. PDF was split into this
separate package precisely so the rest could stay permissive.

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

```csharp
using Aelena.FileApi.Core.Services.Pdf;

var bytes = await File.ReadAllBytesAsync("report.pdf");

var metrics = PdfService.GetMetrics(bytes, "report.pdf");
Console.WriteLine($"{metrics.PageCount} pages, OCR needed: {metrics.OcrNeeded}");

var text = PdfService.ExtractText(bytes, "report.pdf", pages: "1,3,5-8");
foreach (var page in text.Pages)
    Console.WriteLine($"--- Page {page.Page} ---\n{page.Text}");
```

An unreadable or password-protected file raises `FileApiException` with a message
saying what is actually wrong, rather than surfacing an iText exception.
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
