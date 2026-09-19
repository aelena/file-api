# FileApi — Document Processing & AI Analysis Platform

[![CI](https://github.com/aelena/file-api/actions/workflows/ci.yml/badge.svg)](https://github.com/aelena/file-api/actions/workflows/ci.yml)
[![Tests](https://img.shields.io/badge/tests-1060%20passing-brightgreen)]()
[![.NET](https://img.shields.io/badge/.NET-10.0%20%7C%2011.0-512BD4)]()

| Package | Version | Downloads | Licence |
|---------|---------|-----------|---------|
| [`Aelena.FileApi.Core`](https://www.nuget.org/packages/Aelena.FileApi.Core) | [![NuGet](https://img.shields.io/nuget/v/Aelena.FileApi.Core.svg?logo=nuget)](https://www.nuget.org/packages/Aelena.FileApi.Core) | [![Downloads](https://img.shields.io/nuget/dt/Aelena.FileApi.Core.svg?logo=nuget)](https://www.nuget.org/packages/Aelena.FileApi.Core) | ![MIT](https://img.shields.io/badge/MIT-green) |
| [`Aelena.FileApi.Core.Pdf`](https://www.nuget.org/packages/Aelena.FileApi.Core.Pdf) | [![NuGet](https://img.shields.io/nuget/v/Aelena.FileApi.Core.Pdf.svg?logo=nuget)](https://www.nuget.org/packages/Aelena.FileApi.Core.Pdf) | [![Downloads](https://img.shields.io/nuget/dt/Aelena.FileApi.Core.Pdf.svg?logo=nuget)](https://www.nuget.org/packages/Aelena.FileApi.Core.Pdf) | ![AGPL-3.0](https://img.shields.io/badge/AGPL--3.0-red) |
| [`Aelena.FileApi.Cli`](https://www.nuget.org/packages/Aelena.FileApi.Cli) | [![NuGet](https://img.shields.io/nuget/v/Aelena.FileApi.Cli.svg?logo=nuget)](https://www.nuget.org/packages/Aelena.FileApi.Cli) | [![Downloads](https://img.shields.io/nuget/dt/Aelena.FileApi.Cli.svg?logo=nuget)](https://www.nuget.org/packages/Aelena.FileApi.Cli) | ![AGPL-3.0](https://img.shields.io/badge/AGPL--3.0-red) |

A comprehensive .NET 10 / C# 14 document processing platform. Four ports available:

- **HTTP API**
- **Rich CLI**
- **gRPC service**
- **NuGet library**

All powered by the same pure Core library with zero ASP.NET dependencies.

Builds and tests green on **.NET 10 (LTS)** and **.NET 11 preview**.

---

## ⚠️ Licensing — read this before taking a dependency

**This repository ships two libraries under two different licences, and the difference matters.**

| Package | Licence | Contains | Safe for closed-source use? |
|---------|---------|----------|------------------------------|
| `Aelena.FileApi.Core` | **MIT** | DOCX, XLSX, PPTX, CSV, EPUB, MOBI, DjVu, legacy `.doc`, `.msg`, images, email, hashing, PII, readability, text encoding, ZIP, share links, jobs | **Yes** |
| `Aelena.FileApi.Core.Pdf` | **AGPL-3.0-or-later** | All PDF operations, and Markdown → PDF typesetting | **No** — see below |
| `Aelena.FileApi.Cli` (`fileapi` tool) | **AGPL-3.0-or-later** | Everything, including PDF | **No** — see below |

`Aelena.FileApi.Core.Pdf` is built on [iText 7](https://itextpdf.com/), which is
licensed under the **AGPL**. The AGPL is a strong copyleft licence: if you use it in
a network-facing application, that obligation extends to **your** application's
source. iText sells a commercial licence if that is not acceptable — that is a
matter between you and iText, and installing this package does not grant it.

PDF lives in its own package precisely so that everything else can stay MIT.
**If you do not need PDF, depend on `Aelena.FileApi.Core` alone and no copyleft
code enters your build.** The split is enforced by the project structure: `Core`
has no reference to iText, direct or transitive.

```bash
# MIT, no copyleft anywhere in the graph
dotnet add package Aelena.FileApi.Core

# AGPL — only if you understand and accept the obligation
dotnet add package Aelena.FileApi.Core.Pdf
```

The self-hosted **HTTP API and gRPC service** in this repository include PDF by
default, so a deployment of either is likewise subject to the AGPL.

### If you clone this repository

The NuGet split does not help you here — a clone contains everything, including
the AGPL part. So there is a supported way to build without it:

```bash
dotnet build   -p:IncludePdf=false
dotnet publish src/Aelena.FileApi.Api -f net10.0 -c Release -p:IncludePdf=false
```

`-p:IncludePdf=false` removes the `Aelena.FileApi.Core.Pdf` project reference, the
`/pdf/*` endpoints, the `fileapi pdf` command group, and the PDF gRPC methods. The
result contains **no iText assembly at all** — not a disabled feature flag, an
absent dependency. What remains is MIT throughout.

| | Default build | `-p:IncludePdf=false` |
|---|---|---|
| Effective licence | AGPL-3.0-or-later | MIT |
| `/pdf/*` endpoints | 30+ routes | absent (404) |
| `/convert/to-pdf`, `/markdown/to-pdf` | available | `501` |
| `/convert/*` (text, Markdown, validate) | available | available |
| `fileapi pdf …` | available | absent from `--help` |
| gRPC PDF methods | available | `Unimplemented` status |
| Everything else | available | available |
| iText in output | yes | **no** |

CI publishes both ways on every push and fails if an iText assembly appears in the
opt-out output, so this stays true rather than drifting.

Full detail, including the terms of every dependency, is in
**[LICENSING.md](LICENSING.md)**.

## Architecture

```
         ┌──────────────────┐   ┌────────────────────────┐
         │  Core (MIT)      │◄──│  Core.Pdf (AGPL)       │
         │  DOCX, XLSX,     │   │  PDF only — iText 7    │
         │  PPTX, CSV,      │   │  plus Markdown → PDF   │
         │  EPUB, MOBI,     │   │  Separated so that     │
         │  DjVu, .doc,     │   │  Core stays MIT        │
         │  .msg, images,   │   │                        │
         │  email, hash,    │   │                        │
         │  PII, text, zip  │   │                        │
         └────────┬─────────┘   └───────────┬────────────┘
                  │                         │
                  └───────────┬─────────────┘
                             │
              ┌──────────────┼──────────────┐
              │              │              │
        ┌─────▼──────┐  ┌────▼────┐  ┌──────▼────┐
        │  HTTP API  │  │   CLI   │  │   gRPC    │
        │ (MinAPIs)  │  │ (rich)  │  │ (grpc/)   │
        └────────────┘  └─────────┘  └───────────┘
```

### Design Principles

- **Pure library** — Core has zero `Microsoft.Extensions.*` dependencies; usable in console apps, desktop apps, cloud functions, anywhere
- **Ports & adapters** — API and CLI are thin wrappers calling static Core services
- **Thread-safe** — All Core operations are static and proven safe under concurrent load
- **Terse & functional** — records, pattern matching, expression-bodied lambdas
- **Observability** — OpenTelemetry traces + metrics + logs (API layer), Serilog structured logging
- **Cloud-ready** — Docker multi-stage build, deployable to Azure Container Apps, App Service, AKS

## Features

| Category | Description | Status |
|----------|-------------|--------|
| **PDF Toolkit** | 30+ operations: metrics, metadata, extract text/pages/markdown/annotations/bookmarks, merge, split, rotate, reorder, delete pages, watermark, encrypt/decrypt, compress, page numbers, form fields, health check | Implemented |
| **DOCX Processing** | Metrics, metadata, paragraph extraction, markdown conversion, search, health check, metadata removal | Implemented |
| **Spreadsheets (XLSX)** | Metrics, sheets, cell data, CSV/Markdown conversion, metadata, search, plus two audits: what the workbook links to, and what it is hiding | Implemented |
| **Presentations (PPTX)** | Slides, **speaker notes**, Markdown outline, metrics, metadata, search | Implemented |
| **Delimited files (CSV)** | Dialect sniffing, load validation, column profiling, JSON/Markdown conversion | Implemented |
| **Text encoding** | Encoding and BOM detection, line endings, control-byte location, normalisation to clean UTF-8 | Implemented |
| **Ebook & Legacy Conversion** | EPUB, MOBI/PalmDOC, DjVu and legacy binary `.doc` — content-based detection, structural validation, metadata, text, Markdown and PDF | Implemented; DjVu text limited to uncompressed layers |
| **Markdown → PDF** | Headings, lists, tables, block quotes, code blocks, rules and inline emphasis, typeset with iText | Implemented |
| **Image Processing** | Resize, rotate, crop, convert (PNG/JPEG/WebP/BMP/GIF/TIFF), thumbnail, flip, blur, grayscale, compress, strip metadata, EXIF, auto-orient, invert, edge detect, equalize, color palette, base64 | Implemented |
| **PII Detection** | Regex-based scanning for emails, credit cards (Visa/MC/Amex), IBANs, SSNs, phone numbers, national IDs (US/ES/FR/DE/IT/UK/PT), dates of birth | Implemented |
| **Text Analysis** | Metrics, search (literal + regex), readability scores (Flesch, Gunning Fog, SMOG) | Implemented |
| **Email Parsing** | `.eml` (RFC 5322 / MIME) via MimeKit, and `.msg` (Outlook) read natively from its OLE2 container — headers, body, recipients, attachments | Implemented |
| **File Hashing** | SHA-256, MD5, SHA-1, composite hash | Implemented |
| **ZIP Inspection** | List entries with sizes, compression, CRC-32 | Implemented |
| **Share Links** | CRUD with SQLite persistence, password protection, expiry, recipient restrictions — all enforced on access | Implemented |
| **Async Jobs** | Compare, summarize, batch — async job pattern with in-memory store and polling | Job pattern ready |
| **Document Comparison** | Lexical, semantic, summary modes with cross-format support | Job pattern ready; LLM pipeline pending |
| **AI Analysis** | Summarization, classification, Q&A via LLM | Endpoints ready; LLM pipeline pending |
| **Image AI (LLM)** | Describe, tag, detect objects, moderate, extract data, visual Q&A | Endpoints ready; LLM pipeline pending |
| **Geospatial** | KML, KMZ, GeoJSON, Shapefile, DXF feature extraction | Endpoint stubs; NetTopologySuite integration pending |
| **Video** | Container/track metadata extraction | Stub; MediaInfo integration pending |

## Endpoint Families (~130 routes)

| Family | Prefix | Routes | Description |
|--------|--------|--------|-------------|
| Health | `/health` | 1 | Liveness check |
| Auth | `/api/auth/*` | 1 | JWT cookie management |
| PDF | `/pdf/*` | 30+ | Full PDF manipulation toolkit |
| DOCX | `/docx/*` | 10 | Word document processing |
| XLSX | `/xlsx/*` | 10 | Workbooks — sheets, cells, link and hidden-content audits |
| PPTX | `/pptx/*` | 7 | Presentations — slides, speaker notes, outline |
| CSV | `/csv/*` | 5 | Delimited files — dialect, validation, profiling |
| TXT | `/txt/*` | 4 | Metrics, search, encoding detection, normalisation |
| Image | `/image/*` | 13 | Image manipulation (ImageSharp) |
| Image AI | `/image-ai/*` | 14 | Local + LLM-powered image analysis |
| Hash | `/hash` | 1 | Multi-algorithm file hashing |
| PII | `/pii/detect` | 1 | PII detection (20+ regex patterns) |
| Search | `/search` | 1 | Universal cross-format search |
| Readability | `/readability` | 1 | Flesch, Gunning Fog, SMOG scores |
| ZIP | `/zip/inspect` | 1 | Archive inspection |
| Email | `/email/parse` | 1 | Parse `.eml` and `.msg` files |
| Compare | `/compare` | 2 | Async document comparison |
| Summarize | `/summarize` | 2 | Async document summarization |
| Batch | `/batch/*` | 2 | Parallel multi-file processing |
| Classify | `/classify` | 1 | Document type classification |
| Q&A | `/qa` | 1 | Document-grounded Q&A |
| Share | `/share/*` | 4 | Shareable report links |
| Geospatial | `/geospatial/*` | 4 | Feature extraction from geo formats |
| Video | `/video/metadata` | 1 | Container/track metadata |
| Markdown | `/markdown/to-pdf` | 1 | Markdown to PDF typesetting |
| Convert | `/convert/*` | 8 | EPUB, MOBI, DjVu and legacy `.doc` → text, Markdown, PDF |
| Strip | `/strip/images` | 1 | Remove images from documents |
| Redact | `/redact`, `/pdf/redact` | 2 | Text redaction — **not implemented, returns 501** |

## Spreadsheets, presentations and delimited files

Three formats that needed no new dependency: XLSX and PPTX reuse the Open XML
SDK that DOCX already brings in, and CSV needs nothing at all.

### XLSX — `/xlsx/*`

The ordinary operations are there — `metrics`, `sheets`, `sheet`, `metadata`,
`health`, `search`, `to-csv`, `to-md`, `remove-metadata`. Two are less ordinary
and are the reason this group exists:

| Route | Answers |
|---|---|
| `POST /xlsx/audit-links` | External workbook references, hyperlinks, formulas calling `WEBSERVICE`, `DDE`, `RTD`, `EXEC` and friends, and whether the file carries macros |
| `POST /xlsx/hidden` | Hidden sheets, rows and columns, by range |

Both answer questions a spreadsheet does not volunteer. External references and
`WEBSERVICE` are legitimate features and also the mechanism behind a familiar
class of phishing document — worth listing *before* the file is opened. And
hidden is not deleted: a hidden column travels with the workbook and reappears
with one right-click, which is a recurring way of sending data that was believed
to be gone.

Cell values come back as the text a reader would see. A workbook stores most
strings once in a shared table and refers to them by index, so a naive XML
scrape returns integers where the user sees words.

### PPTX — `/pptx/*`

`metrics`, `slides`, `notes`, `extract-markdown`, `metadata`, `search`,
`remove-metadata`.

**Speaker notes are extracted.** A deck's slides are headlines; the argument
behind them lives in the notes pane, and most extraction tools drop it. Slides
are read in presentation order — the slide id list, not the order the parts
happen to sit in the package, because reordering a deck rewrites the list and
leaves the parts where they were.

### CSV — `/csv/*`

`inspect`, `profile`, `rows`, `to-json`, `to-md`.

Nothing is told how the file is delimited. A file named `.csv` is
semicolon-separated about as often as it is comma-separated, and the caller
usually does not know which they have, so the dialect is inferred from the bytes
and reported back. `inspect` answers the real question — *will this load?* —
with ragged rows, duplicate or blank headers, and mixed line endings each named.
`profile` answers the next one: per column, the inferred type, how much of it is
empty, how many distinct values, and the range.

Delimiter detection scores consistency rather than frequency, so prose full of
commas inside one quoted field does not beat the actual separator.

### Text encoding — `/txt/detect-encoding`, `/txt/normalise`

Encoding, byte-order mark, line endings, and **any control byte that does not
belong in text, with the line and column of each**. `normalise` rewrites the
file as clean UTF-8 with consistent line endings.

This is not hypothetical. Version 0.4.1 of this project failed to publish
because a single `0x08` had reached a README as a raw byte: valid UTF-8,
invisible in every editor, and rejected by nuget.org at push time with nothing
more helpful than "the readme file must be plain text". `detect-encoding` is
the operation that would have found it in a second, and CI now runs the same
check over every source file and every packed README.

### Outlook `.msg`

`POST /email/parse` reads `.msg` as well as `.eml`. Outlook messages are OLE2
compound files, so this reuses the reader written for legacy `.doc` — no new
dependency. Subject, sender, recipients (resolved from their own storages, not
just the display names), submit time, body, and the attachment list all come
back in the same shape a `.eml` produces.

Until 0.4.4 this route advertised `.msg` support in the documentation and
answered `501` for it.

## Converting EPUB, MOBI, DjVu and legacy `.doc`

Four formats with no toolkit of their own, behind one group at `/convert`. The
endpoints sniff the upload, check its structure, and only then convert.

| Route | Returns |
|---|---|
| `POST /convert/detect` | What the file actually is, whether the extension agrees, and what can be done with it |
| `POST /convert/validate` | Every structural issue found, as errors, warnings and info — a report, never a refusal |
| `POST /convert/metadata` | Title, authors, language, publisher, identifier, subjects, plus per-format container detail |
| `POST /convert/text` | Extracted text, split into the sections the source format defines |
| `POST /convert/markdown` | The whole document as one Markdown string |
| `POST /convert/to-txt` | The same text as a `.txt` download |
| `POST /convert/to-md` | The same Markdown as a `.md` download |
| `POST /convert/to-pdf` | The document typeset as a PDF (absent under `-p:IncludePdf=false`) |

### Detection is by content, never by extension

A renamed file is the normal case, not the exception, and dispatching on the
extension is how a `.zip` renamed to `.epub` becomes a 500 from inside a parser.
Every route sniffs the magic bytes first. `/convert/detect` reports the
disagreement without refusing the file; the conversion routes act on what the
content says and name the mismatch in the error if they refuse.

### Validation runs before conversion, and reports rather than refuses

`POST /convert/validate` always answers `200` with a list of issues. What is
checked depends on the format:

- **EPUB** — the `mimetype` entry's content, position and compression;
  `META-INF/container.xml` and the rootfile it names; the OPF's manifest and
  spine, including items that do not resolve to a file in the archive; DRM
  (`META-INF/encryption.xml`); and entry-count, total-size and
  compression-ratio limits, so a decompression bomb is refused from the central
  directory without inflating anything.
- **MOBI / PalmDOC** — the Palm database type and creator; the record table's
  bounds and monotonicity; the PalmDOC header's compression, encryption and
  declared text length against the records that actually exist.
- **DjVu** — the `AT&TFORM` signature and form type; the declared container
  length against the file; every IFF chunk's bounds as the walk descends; and
  whether the pages carry a text layer, and in which form.
- **DOC** — the OLE2 header and FAT (every chain walk bounded, so a cyclic FAT
  answers `422` instead of hanging the request); the `WordDocument` stream; the
  FIB's magic, version and encryption flag; and the piece table's own bounds.

### Three different failures, three different status codes

The contract is that a caller can tell these apart from the response alone:

| Status | Means |
|---|---|
| `415` | Recognised, but not a format this group reads. The message names what the file is and points at `/pdf/*`, `/docx/*` or `/zip/inspect` where one of those is the right home |
| `422` | The right format, structurally broken — or well-formed with nothing to extract, such as a DjVu that is page images with no OCR |
| `501` | Well-formed and readable, but locked or compressed in a scheme this service does not decode |

### What is deliberately not supported

Each of these answers `501` with the reason, rather than returning an empty
success:

- **DjVu text layers in `TXTz` chunks.** `TXTz` is BZZ-compressed, and BZZ needs
  the ZP adaptive arithmetic coder whose only published implementation is
  DjVuLibre's — which is GPL, and cannot be vendored into an MIT package. The
  uncompressed `TXTa` form is read. Everything else about a DjVu — page count,
  geometry, resolution, structure — is available whatever the text layer looks
  like, and `/convert/validate` says which form a given file uses. For the text
  itself, `djvutxt` from DjVuLibre.
- **HUFF/CDIC-compressed MOBI**, which later Kindle files use. Uncompressed and
  PalmDOC-compressed MOBI are read.
- **DRM**, in both EPUB and MOBI. Metadata is still readable and still returned;
  only the content is refused.
- **Word 6.0/95 `.doc`** (`nFib` below `0x00C1`), whose FIB has a different
  layout. Re-saving in any later Word version produces a file this reads.

### PDF output is Windows-1252

iText's built-in fonts carry no Unicode glyphs and embedding a font would mean
shipping one, so `/convert/to-pdf` and `/markdown/to-pdf` transliterate:
accented letters outside CP1252 lose their accent, and characters with no ASCII
reading become `?`. A book in a non-Latin script will convert to a PDF of
question marks. `/convert/to-md` is UTF-8 and loses nothing.

### `.doc` Markdown is paragraph-level

The reader walks the piece table for the text, but does not read the style
sheet, so headings in the Markdown are inferred from shape — the same
compromise the PDF Markdown extractor makes. Word's in-band control characters
(paragraph and cell marks, field codes, picture placeholders) are resolved
rather than passed through.

## Authentication

All endpoints require a JWT token as an `auth_token` httpOnly cookie.

**Public paths** (no auth): `/health`, `/docs`, `/swagger`, `/openapi.json`, `/api/auth/set-cookie`

## Processing Modes

| Mode | Pattern | Description |
|------|---------|-------------|
| **Sync** | Direct response | Fast operations (<2s): metrics, hash, search, text extraction |
| **Async** | POST → 202 + job_id, GET → poll | Slow/LLM operations: compare, summarize |
| **Batch** | POST /batch/{op} → 202 | Parallel multi-file with per-file webhooks |

## Error Responses

All errors follow [RFC 9457 Problem Details](https://datatracker.ietf.org/doc/html/rfc9457):

```json
{
  "type": "about:blank",
  "title": "Bad Request",
  "status": 400,
  "detail": "File must be a PDF",
  "instance": "/pdf/metrics"
}
```

## Confidentiality Routing

| Level | Description |
|-------|-------------|
| `private` | Documents processed locally via OpenWebUI/Ollama (default) |
| `public` | Documents sent to cloud LLM (e.g. OpenAI GPT-4o) |
| `air_gapped` | Fully offline processing, no LLM calls |

## Quick Start

### Docker

```bash
docker-compose up --build
# API at http://localhost:9401
# Swagger UI at http://localhost:9401/swagger
```

### Local Development

```bash
dotnet restore
dotnet build
dotnet run --project src/Aelena.FileApi.Api -f net10.0
```

The projects multi-target `net10.0` and `net11.0`, so `run`, `publish`, and a
single-project `build` need `-f`. Without the .NET 11 SDK installed, build the
LTS target alone:

```bash
dotnet build -p:TargetFrameworks=net10.0
```

### Run Tests

```bash
dotnet test
```

`dotnet test` reports **580 passing**. That is 290 distinct tests — 193 unit plus
97 endpoint — run once against each target framework:

| Suite | Tests | Frameworks | Executions |
|-------|-------|-----------|-----------|
| `Aelena.FileApi.Tests` (unit, concurrency, conversion, office) | 387 | net10.0, net11.0 | 774 |
| `Aelena.FileApi.Api.Tests` (endpoint, error-contract, auth, share) | 143 | net10.0, net11.0 | 286 |
| **Main solution total** | **530** | | **1060** |
| `Aelena.FileApi.Grpc.Tests` (separate solution) | 8 | net10.0, net11.0 | 16 |

To run a single framework: `dotnet test -f net10.0`.

### Build NuGet Package

```bash
dotnet pack src/Aelena.FileApi.Core -c Release -o artifacts/
```

## CLI — Rich Console Interface

The `fileapi` CLI provides direct access to all Core operations from the terminal, with rich Spectre.Console output.

### Install / Run

```bash
# Run via dotnet
dotnet run --project src/Aelena.FileApi.Cli -f net10.0 -- <command> [options]

# Or build and use directly
dotnet build src/Aelena.FileApi.Cli -c Release -f net10.0
./src/Aelena.FileApi.Cli/bin/Release/net10.0/fileapi <command>
```

### Commands

```bash
# PDF operations
fileapi pdf metrics document.pdf          # Page count, words, OCR needs, signatures
fileapi pdf extract-text document.pdf     # Extract all text
fileapi pdf metadata document.pdf         # Title, author, dates, version
fileapi pdf health document.pdf           # Corruption, fonts, JavaScript checks
fileapi pdf merge -o merged.pdf a.pdf b.pdf  # Merge PDFs
fileapi pdf rotate --angle 90 doc.pdf     # Rotate pages
fileapi pdf encrypt --password s3cret doc.pdf  # Password protect
fileapi pdf decrypt --password s3cret doc.pdf  # Remove protection
fileapi pdf search --query "contract" doc.pdf  # Search text

# DOCX operations
fileapi docx metrics report.docx          # Paragraphs, words, tables, images
fileapi docx metadata report.docx         # Title, author, revision
fileapi docx markdown report.docx         # Convert to Markdown
fileapi docx health report.docx           # Tracked changes, macros

# Image operations
fileapi image exif photo.jpg              # EXIF metadata + GPS
fileapi image resize -w 800 photo.jpg     # Resize with aspect ratio
fileapi image rotate --angle 90 photo.jpg # Rotate
fileapi image convert --format webp photo.png  # Format conversion
fileapi image grayscale photo.jpg         # Grayscale
fileapi image blur --radius 5 photo.jpg   # Gaussian blur
fileapi image compress --quality 60 photo.jpg  # JPEG compression

# Spreadsheets, presentations, delimited files
fileapi xlsx sheets budget.xlsx                # Names, shape, visibility
fileapi xlsx hidden budget.xlsx                # Hidden sheets, rows, columns
fileapi xlsx audit-links budget.xlsx           # External refs, hyperlinks, risky formulas
fileapi xlsx sheet --sheet Q3 budget.xlsx      # One sheet as CSV on stdout
fileapi pptx slides deck.pptx                  # Titles, hidden flags, which have notes
fileapi pptx notes deck.pptx                   # Speaker notes only
fileapi pptx markdown deck.pptx -o deck.md     # Outline with notes as block quotes
fileapi csv inspect export.csv                 # Dialect, header, and why it will not load
fileapi csv profile export.csv                 # Per-column type, nulls, cardinality, range

# EPUB / MOBI / DjVu / legacy .doc
fileapi convert detect mystery-file            # Identify it from its content
fileapi convert validate book.epub             # Structural checks, every issue listed
fileapi convert metadata book.mobi             # Title, authors, language, publisher
fileapi convert text book.epub > book.txt      # Plain text to stdout
fileapi convert markdown report.doc -o out.md  # Markdown to a file
fileapi convert pdf book.epub -o book.pdf      # Typeset as PDF

# Utilities
fileapi hash invoice.pdf                  # SHA-256, MD5, SHA-1
fileapi readability essay.txt             # Flesch, Gunning Fog, SMOG scores
fileapi pii detect contract.pdf           # Detect emails, SSNs, credit cards
fileapi txt metrics notes.txt             # Line, word, token counts
fileapi txt search --query "TODO" notes.txt
fileapi zip archive.zip                   # List entries with sizes
fileapi email message.eml                 # Parse .eml or .msg
```

### Exit codes

Failures print one line on stderr — not a stack trace — and set a code you can
branch on:

| Code | Meaning |
|------|---------|
| `0` | Success |
| `1` | The operation failed unexpectedly |
| `2` | Bad input file or arguments |
| `3` | Operation not implemented for this format |
| `4` | Could not read or write a file |

Diagnostics go to stderr, so `fileapi convert markdown book.epub > book.md`
captures the document and not the warnings.

### Batch conversion

The codes are distinct enough to branch on, which is what makes the tool usable
from a script. Converting a folder of assorted documents to Markdown, keeping
aside the ones that cannot be converted and saying why:

```bash
#!/usr/bin/env bash
mkdir -p out skipped

for f in inbox/*; do
  name=$(basename "$f")
  stem="${name%.*}"; ext="${name##*.}"

  # The source extension goes into the output name: three formats of the same
  # document would otherwise all want to be "$stem.md".
  fileapi convert markdown "$f" > "out/$stem.$ext.md" 2>/dev/null
  status=$?

  if [ $status -eq 0 ]; then
    echo "converted  $name"
    continue
  fi

  rm -f "out/$stem.$ext.md"
  cp "$f" skipped/

  case $status in
    2) echo "unreadable $name — broken, or nothing to extract" ;;
    3) echo "locked     $name — DRM, or a codec this build cannot decode" ;;
    *) echo "failed     $name (exit $status)" ;;
  esac
done
```

Capture `$?` into a variable before testing it: after an `if`, `$?` is the
status of the branch that ran, not of the command in the condition.

Run against this repository's own test fixtures, that prints:

```
unreadable hr-report-94-1476-p249.djvu — broken, or nothing to extract
converted  public-domain-pieces.doc
converted  public-domain-pieces.epub
converted  public-domain-pieces.mobi
locked     un-resolution-1837.djvu — DRM, or a codec this build cannot decode
```

Both DjVu files are perfectly valid, and they fail differently on purpose. The
first is a page scan with no OCR layer at all, so there is no text to extract.
The second has one, BZZ-compressed, which this build does not decode — see
[LICENSING.md](LICENSING.md) for why. `fileapi convert validate` on either says
so in full, and the message on stderr says which happened.

## CI gates

Every push and pull request runs these; a release tag runs the same workflow
rather than a copy of it, so the release gate cannot drift from the merge gate.

| Gate | What it refuses |
|---|---|
| `build` (ubuntu + windows) | Anything that does not compile or test clean on net10.0 **and** net11.0 |
| `lint (format + style)` | Formatting that differs from `.editorconfig`, including using-directive order — `dotnet format --verify-no-changes`, the job ruff and black do elsewhere |
| `packages` | A packed README that is not plain UTF-8, a raw control byte in any source file, and any iText reference reaching the MIT package |
| `MIT-only build` | A `-p:IncludePdf=false` build that fails, or ships an iText assembly anyway |
| `docker` | An image that does not build |

`EnforceCodeStyleInBuild` and `TreatWarningsAsErrors` already fail the build on
the analyzer rules, so the `lint` job is not duplicating them — it catches what
the compiler never sees, which in practice is import ordering and whitespace in
files the analyzers skip.

### Security scanning

A separate `Security` workflow runs on push, on pull requests and weekly,
because a new advisory against an unchanged dependency is the normal case and
cannot wait for someone to push.

| Job | Covers |
|---|---|
| `dependency audit` | `dotnet list package --vulnerable --include-transitive`, failing on **any** severity. Deprecated and outdated packages are reported without blocking |
| `SBOM + Trivy` | A CycloneDX SBOM published as a build artifact, then scanned — findings to the Security tab. Reports rather than blocks: see below |
| `CodeQL (C#)` | Static analysis of the code itself with `security-extended`, which no dependency scanner covers |
| `dependency review` | Blocks a pull request that introduces a vulnerable or copyleft-licensed dependency before it merges |

**Why not OWASP Dependency-Check itself.** It needs an NVD API key and a
multi-gigabyte database cache to be anything other than slow, and its .NET
analyzer inspects compiled assemblies rather than the package graph — so on a
NuGet project it sees *less* than `dotnet list package --vulnerable` and Trivy
do between them, not more. The four jobs above cover what it would have told
you, need no secrets, and put their findings in the Security tab.

**Trivy reports; it does not block.** For a .NET-only repository it and
`dotnet list package --vulnerable` both resolve NuGet advisories from the same
GitHub Advisory Database, so it is a second opinion rather than extra coverage —
measured, both return 0 findings across the same 119 components. What it adds is
the SARIF feed into the Security tab and a scan of the published inventory,
neither of which is worth blocking a merge over when Trivy's database mirror is
rate-limiting. The blocking gate is the dependency audit.

The severity threshold on the dependency audit is deliberately "any, including
low". This library is handed untrusted files by design, so a low-severity
parsing bug in a dependency is not a low-severity problem here.

## Configuration

All settings via environment variables or `appsettings.json` (section `AppSettings`):

| Variable | Default | Description |
|----------|---------|-------------|
| `AppSettings__PublicLlmBaseUrl` | `https://api.openai.com/v1` | Cloud LLM endpoint |
| `AppSettings__PublicLlmApiKey` | | Cloud LLM API key |
| `AppSettings__PublicLlmModel` | `gpt-4o` | Cloud LLM model |
| `AppSettings__PrivateLlmBaseUrl` | `http://host.docker.internal:3000/api/v1` | Local LLM endpoint |
| `AppSettings__PrivateLlmApiKey` | | Local LLM API key |
| `AppSettings__JwtSecretKey` | `your-secret-key-change-in-production` | JWT signing key. **The default is a placeholder** — outside `Development` the app refuses to start until it is replaced with a random value of at least 32 bytes. |
| `AppSettings__JwtAlgorithm` | `HS256` | Signing algorithm; the only one accepted on validation. `HS256`, `HS384`, or `HS512`. |
| `AppSettings__CorsOrigins` | `http://localhost:9600` | Allowed CORS origins |
| `AppSettings__MaxRequestsPerDay` | `0` (unlimited) | Daily request cap per user |
| `AppSettings__MaxFileSizeBytes` | `0` (unlimited) | Max upload size |
| `OpenTelemetry__Endpoint` | | OTLP exporter endpoint |

## Solution Structure

```
file-api/
├── Aelena.FileApi.sln
├── Directory.Build.props          # net10.0, C# 14, nullable, TreatWarningsAsErrors
├── Directory.Packages.props       # Central Package Management: one pinned version per package
├── Directory.Build.targets        # Test-project settings (imports after each csproj)
├── docker-compose.yml
├── prompts/                       # Scriban templates for LLM prompts
│
├── src/
│   ├── Aelena.FileApi.Core/      # NuGet library — ALL business logic
│   │   ├── Models/                # 60+ C# record types
│   │   ├── Enums/                 # Confidentiality, CompareMode, DocumentType, etc.
│   │   ├── Errors/                # FileApiException → ProblemDetails
│   │   ├── Abstractions/          # ILlmClient, ILlmClientFactory
│   │   └── Services/
│   │       ├── Pdf/               # PdfService (iText7) — 23 static methods
│   │       ├── Docx/              # DocxService (Open XML SDK) — 10 methods
│   │       ├── Image/             # ImageService (ImageSharp) — 18 methods
│   │       ├── Llm/               # LlmClientFactory, PromptRenderer, OpenAiCompatibleClient
│   │       ├── Jobs/              # InMemoryJobStore<T>
│   │       ├── Persistence/       # ShareRepository (SQLite/Dapper)
│   │       └── Common/            # TextAnalysis, PageRangeParser, TextSearch, HashService,
│   │                              # TxtService, ZipService, ReadabilityService, PiiService,
│   │                              # EmailService, UserRegex
│   │
│   ├── Aelena.FileApi.Core.Pdf/  # AGPL — PDF only, the sole iText consumer
│   │   └── Services/Pdf/          # PdfService. Kept out of Core so Core is MIT.
│   │
│   ├── Aelena.FileApi.Api/       # HTTP wrapper (Minimal APIs)
│   │   ├── Program.cs             # Top-level: DI, Serilog, OpenTelemetry, all routes
│   │   ├── Endpoints/             # 22 endpoint files + FormFileExtensions
│   │   ├── Middleware/            # Exception, Audit, AuthRateLimit
│   │   ├── Logging/               # Source-generated LoggerMessage delegates
│   │   ├── Auth/                  # JwtCookieAuth
│   │   ├── Services/              # WebhookService
│   │   └── Configuration/         # AppSettings
│   │
│   └── Aelena.FileApi.Cli/       # `fileapi` console app (System.CommandLine 2.0)
│       ├── Commands/              # One file per command group
│       └── Helpers/               # Output, ExitCode, CommandExtensions, Format
│
├── grpc/                          # gRPC port — its own solution, same Core
│   ├── src/Aelena.FileApi.Grpc/
│   └── tests/
│
└── tests/
    ├── Aelena.FileApi.Tests/     # 193 unit tests (xUnit + AwesomeAssertions)
    └── Aelena.FileApi.Api.Tests/ # 97 endpoint, error-contract, auth, and share tests
```

## Tech Stack

| Component | Library |
|-----------|---------|
| PDF | iText7 9.x (**AGPL** — isolated in `Aelena.FileApi.Core.Pdf`) |
| DOCX/PPTX | DocumentFormat.OpenXml 3.x |
| Images | SixLabors.ImageSharp 3.x |
| Email | MimeKit 4.x |
| CLI | System.CommandLine 2.0 + Spectre.Console |
| gRPC | Grpc.AspNetCore 2.x |
| LLM | OpenAI-compatible HTTP client |
| Templates | Scriban 7.x |
| Tokens | SharpToken 2.x |
| SQLite | Microsoft.Data.Sqlite + Dapper |
| Logging | Serilog + OpenTelemetry |
| Testing | xUnit + AwesomeAssertions + NSubstitute |

Package versions are pinned centrally in `Directory.Packages.props`. Nothing
floats — `NuGetAudit` runs at `low` severity across the whole graph and fails
the build on a known advisory.

## Deferred to Separate Projects

| Dependency | Status | Notes |
|------------|--------|-------|
| `imagehash` | Separate NuGet | Perceptual hashing (aHash/pHash/dHash/wHash) |
| `docling` | Separate project | IBM ML document parser — no .NET equivalent |
| GDAL | Partial | Using NetTopologySuite + LibTiff.NET instead |

## Releasing

Publishing uses **NuGet Trusted Publishing** — nuget.org exchanges a short-lived
GitHub OIDC token for a one-hour API key, so no long-lived secret is stored. The
job needs `id-token: write`, which `release.yml` declares.

One-time setup on nuget.org (Account → Trusted Publishing), one policy per repo:

| Field | Value |
|-------|-------|
| Repository Owner | `aelena` |
| Repository | `file-api` |
| Workflow File | `release.yml` (file name only, no path) |
| Environment | `production` (the workflow declares it; the two must match) |
| Glob Patterns and Packages | `Aelena.FileApi.*` |

Create a GitHub environment named `production` in the repository, and add a
secret `NUGET_USER` holding the nuget.org profile name (not an email address).

A policy is bound to **one** repository, so each repository needs its own.

To cut a release: set `<Version>` in the three packable csproj files, commit, then

```bash
git tag v0.3.0 && git push origin v0.3.0
```

The workflow tests, packs, re-checks the MIT/AGPL boundary, refuses to continue
if the tag does not match the package version, publishes, and opens a GitHub
release. `workflow_dispatch` runs it as a dry run without pushing.

## Changelog

See [CHANGELOG.md](CHANGELOG.md). The 0.3.0 entry documents the modernization
pass: the .NET 10/11 retarget, and the bugs it turned up — including a redaction
endpoint that returned unredacted documents and share links that ignored their
own passwords and expiry.

## License

Two licences, by package — see the [licensing section](#️-licensing--read-this-before-taking-a-dependency) above.

- **`Aelena.FileApi.Core`** — MIT, see [LICENSE](LICENSE). No copyleft dependencies.
- **`Aelena.FileApi.Core.Pdf`** and the **`fileapi` CLI** — AGPL-3.0-or-later,
  inherited from iText 7. The repository's own source is MIT; the AGPL obligation
  comes from the dependency, and applies to anything that ships or serves it.

Other dependencies keep their own terms. `SixLabors.ImageSharp` is under the
Six Labors Split License, and the relevant clause is favourable: it grants
Apache 2.0 to anyone "consuming the Work as a **Transitive Package Dependency**".
Installing `Aelena.FileApi.Core` brings ImageSharp in indirectly, which is exactly
that — so consumers get it under Apache 2.0 whatever their size. The commercial
threshold applies to a *direct* dependency on ImageSharp, not to users of this
package.
