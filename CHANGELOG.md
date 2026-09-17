# Changelog

All notable changes to this project are documented here.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.3]

A one-line fix to a method that had been wrong since it shipped, and the tests
that found it.

### Fixed

- **`/pdf/form-fields` reported every field as `"unknown"`.** The type switch
  compared against `"/Tx"`, `"/Btn"`, `"/Ch"` and `"/Sig"`, but
  `PdfName.GetValue()` returns the name *without* its leading solidus, so no
  arm ever matched. Every text box, checkbox, dropdown and signature field in
  every document came back untyped. Found by writing the first real test for
  the method, which had 16% coverage.

### Tests

- `PdfServiceOperationTests` covers the half of `PdfService` that needs a
  document with structure in it — outlines, annotations, form fields, embedded
  images, encryption, mixed page geometry — plus page numbering, blank-page
  insertion, page extraction, metadata removal and unlocking. 30 new tests,
  386 → 416 in the suite.
- `PdfService.cs` line coverage 63.8% → 94.4%; hand-written code across the
  repository 75.5% → 79.3%.

## [0.4.2]

What 0.4.1 was meant to be. Same documentation changes, plus the fix for the
reason 0.4.1 did not ship, and a CI gate so the same class of failure cannot
reach a tag again.

### Fixed

- **`Aelena.FileApi.Core`'s README held a `0x08` byte** and nuget.org rejected
  the package with *"The readme file must be plain text using UTF-8 encoding."*
  A regex word boundary in a text-search example had reached the file as a
  literal backspace rather than the two characters it should have been. The C#
  compiled — a verbatim string accepts any character — so extracting and
  building the examples, which is what caught four other errors, could not catch
  this one. Only a byte-level check could.

### Added

- **CI now asserts that every packed README is plain UTF-8 text**, with no
  control bytes beyond tab, newline and carriage return. nuget.org enforces this
  at push time, which is after the tag and after every other gate has passed —
  the worst place to discover it. The check is in the `packages` job, so it runs
  on every push and pull request rather than only on a release.

## [0.4.1]

**Published `Aelena.FileApi.Cli` only.** The push is a single
`dotnet nuget push` over all three packages in glob order; the CLI went up, the
Core package was rejected on its README encoding, and the step aborted before
`Core.Pdf` was attempted. NuGet versions are immutable and the CLI's 0.4.1 is
live, so the other two skip to 0.4.2 rather than a pushed tag being moved.

Documentation only. No code changed; this exists because the README that appears
on a package's nuget.org listing page is the one packed at release time, and
0.4.0's was thinner than what it documented.

### Changed

- **Each package README now has examples for what the package actually does.**
  `Core` listed thirteen capability areas and had examples for four of them —
  DOCX, images, email and ZIP had none. `Core.Pdf` described its whole write
  half and the Markdown typesetting added in 0.4.0 in prose, and showed neither.
  The CLI documented five exit codes and never demonstrated branching on them,
  which is the one thing a flat list of commands cannot show.
- Corrected the `Core.Pdf` blurb, which still described `Core`'s contents as of
  0.3.x and did not mention the ebook and legacy formats.

### Fixed

- **Every C# example now compiles.** Extracting the twelve of them into a
  project built against the real assemblies found four errors that reading them
  had not: a parameter named `format` that is really `targetFormat`, a `Search`
  overload whose `query` and `pattern` have no defaults so neither can be
  omitted, and two blocks using variables they never declared. The `Core`
  example had been calling `PiiService.Detect(text, …)` in a block that only
  ever defined `bytes` — an error that predates 0.4.0.
- **The CLI example was run rather than written.** Doing so found two faults in
  it: `case $?` after an `if` reads the status of the branch that ran rather
  than of the command in the condition, so every failure fell through to the
  catch-all; and three fixtures sharing a file stem overwrote each other's
  output. The published script is the corrected one, its output is what it
  actually printed, and the `$?` trap is called out in prose.

## [0.4.0]

Four formats that had no home in this toolkit — EPUB, MOBI, DjVu and the legacy
binary `.doc` — now convert to text, Markdown and PDF through a single group at
`/convert`, and the Markdown-to-PDF route that had been a stub since it was
added now actually typesets.

### Added — the `/convert` family

Eight routes: `detect`, `validate`, `metadata`, `text`, `markdown`, `to-txt`,
`to-md`, `to-pdf`. A `fileapi convert` CLI command mirrors them.

- **Detection is by content, never by extension.** Dispatching on the extension
  is how a `.zip` renamed to `.epub` becomes a 500 from inside a parser.
  `/convert/detect` reports a disagreement between the two without refusing the
  file; the conversion routes act on the content and name the mismatch if they
  refuse.
- **Validation runs before conversion, and reports rather than refuses.**
  `/convert/validate` always answers `200` with the full issue list, because
  "this EPUB has no `dc:language`" and "this EPUB has no spine" need very
  different responses and only the caller can decide which matters. Checked:
  the EPUB `mimetype` entry, `container.xml`, and the OPF manifest and spine,
  with entry-count, total-size and compression-ratio limits read from the
  central directory so a decompression bomb costs nothing to refuse; the Palm
  record table's bounds and monotonicity and the PalmDOC header's declared
  lengths; the DjVu container length and every IFF chunk's bounds; and the OLE2
  FAT, the Word FIB's magic and version, and the piece table's own bounds.
- **Three failures, three status codes,** pinned by tests over the real HTTP
  surface: `415` for a format this group does not read (naming what the file
  actually is, and pointing at `/pdf/*`, `/docx/*` or `/zip/inspect`), `422` for
  the right format structurally broken, `501` for a file that is well-formed and
  simply cannot be decoded here.
- Every chain walk in the OLE2 reader is bounded by the table it indexes. A
  compound file arriving over HTTP can have a FAT whose entries point at each
  other in a cycle, and an unbounded walk would hang the request thread rather
  than returning a `422`.
- **DjVu text extraction is limited to uncompressed `TXTa` chunks, and says so.**
  `TXTz` is BZZ-compressed; BZZ needs the ZP adaptive arithmetic coder, whose
  only published implementation is DjVuLibre's, which is GPL and cannot be
  vendored into an MIT package. A `TXTz`-only document answers `501` naming the
  reason and pointing at `djvutxt`, rather than returning nothing and calling it
  success. Page count, geometry, resolution and structure are read either way.
  See [LICENSING.md](LICENSING.md).
- HUFF/CDIC-compressed MOBI, DRM-locked EPUB and MOBI, and Word 6.0/95 `.doc`
  are each recognised and each answer `501` (or `422` for Word 6.0/95, which is
  a different file format rather than a locked one) with the reason.
- Metadata does not go through the extraction. A scanned DjVu has no text and a
  DRM-locked MOBI has text nobody can read; both still have metadata sitting in
  the clear that a caller has every right to.

### Fixed

- **`/markdown/to-pdf` was a stub returning `501` "Coming soon".** It now
  typesets through `MarkdownPdfService` — headings, lists, tables, block quotes,
  code blocks, rules and inline emphasis. The `css` parameter is gone rather than
  accepted and ignored: there is no HTML stage for a stylesheet to apply to.
  Text is restricted to Windows-1252, because iText's built-in fonts carry no
  Unicode glyphs; characters outside it are transliterated where there is an
  obvious reading and replaced with `?` where there is not, which is documented
  on the endpoint rather than discovered in the output.

### Tests

290 → 387 (580 → 774 executions across both frameworks). The new fixtures are
real public-domain files in each format rather than synthesised ones — a parser
that only ever sees its own author's output is a parser that has not been
tested. Provenance for each is in
[`tests/Aelena.FileApi.Tests/SampleFiles/README.md`](tests/Aelena.FileApi.Tests/SampleFiles/README.md).

## [0.3.0]

A modernization pass over a codebase that had been sitting for six months.
It did not build on checkout: floating version ranges (`5.*`, `8.*`) had drifted
Scriban onto a release carrying a critical advisory, and `NuGetAudit` failed
restore before a line of C# was read.

### Security

- **Share links ignored every restriction they recorded.** `GET /share/{token}`
  looked the row up, bumped the access counter, and returned the report. It never
  checked `expires_at`, never verified `password_hash`, and never consulted
  `allowed_emails`. An expired link worked indefinitely and a password-protected
  link opened for anyone holding the URL — the password was write-only. Expiry now
  answers `410`, a missing or wrong password `401` (compared in fixed time), and a
  link restricted to named recipients `403`.
- **The JWT signing algorithm was unpinned.** Neither validation path passed
  `ValidAlgorithms`, so the handler accepted whatever `alg` a token declared.
  `AppSettings.JwtAlgorithm` existed to name the algorithm in use and was read by
  nothing. It is now the single accepted algorithm, restricted to the HMAC family.
- **The placeholder JWT secret would have shipped.** Anyone who has read this
  repository could mint a valid session with it. Outside `Development` the
  application now refuses to start while it is still in place, or while the secret
  is too short to sign with.
- **Caller-supplied regexes ran without a match timeout.** Both the pattern and the
  text come from the caller, so `^(a+)+$` against a long run of `a`s pinned a
  thread until the process died. Every caller pattern now carries a 2s timeout and
  reports `400` when it expires.
- Scriban `5.12.1` → `7.2.6`, clearing the advisory that broke restore.

### Fixed — operations that silently did nothing

- **`/pdf/redact` returned unredacted documents.** It counted matches, copied the
  PDF through untouched, and returned it as `<name>_redacted.pdf` with a non-zero
  redaction count and a `200`. Text the caller asked to have removed was still
  selectable and extractable. Now `501`, matching the `/redact` endpoint that
  already answered that way.
- **`/pdf/extract-tables` always reported zero tables.** It opened the document,
  ignored it, and returned an empty list with a `200` — indistinguishable from a
  PDF that genuinely contains no tables. Now `501`, pointing at the text and
  Markdown extractors that do work.
- `CompressPdf` accepts `imageQuality` and `dpi` and applies neither. It does
  genuinely compress (object streams and the cross-reference table), so it still
  answers normally, but the limitation is documented on the method instead of
  living in a `TODO`.

### Fixed — error reporting

- **Caller mistakes were reported as `500`.** Core signalled expected failures with
  `ArgumentException`, `FormatException`, or whatever iText threw; only
  `FileApiException` was mapped to a status code. A mistyped page range answered
  `500 "An unexpected error occurred."` The CLI printed a raw stack trace for the
  same reason. `ErrorContractTests` now pins ten of these against the real HTTP
  surface.
- **RFC 9457 responses were not `application/problem+json`.** `WriteProblem`
  assigned `Response.ContentType` and then called `WriteAsJsonAsync`, which
  overwrites it, so every error went out as `application/json` — defeating the one
  thing the media type exists for.
- `PageRangeParser`: out-of-bounds pages and malformed tokens now answer `400`
  naming the problem; a reversed range like `8-5` is rejected instead of quietly
  selecting nothing; a selection matching no page is rejected rather than returning
  an empty document with a `200`.
- Only `GetMetrics` handled an unreadable PDF; the other 23 entry points let
  iText's exception escape. Both failure points now produce a `422` describing what
  is wrong with the file, and a password-protected PDF says so everywhere.
- `SearchEndpoints` told callers `query` and `pattern` could both be supplied,
  then rejected exactly that.
- An oversized upload answered `400` quoting `MAX_FILE_SIZE_BYTES`, a server
  environment variable the caller cannot see. Now `413` with both sizes in MB.

### Fixed — leaks, races, and resource use

- `AuthRateLimitMiddleware` kept a static per-date bucket that was never evicted:
  one permanent entry per day per caller, for the life of the process.
- `AuditLogMiddleware` had no `try/finally` and sat inside the exception handler,
  so no failing request was ever audited and rate-limited requests never reached
  it — the entries anyone reading an audit log is looking for. It is now outermost
  and logs in a `finally`.
- `InMemoryJobStore` trimmed without synchronisation: two concurrent `Set` calls at
  capacity each computed their own excess from a separate snapshot and could evict
  jobs a caller was still polling for.
- `ShareRepository` used double-checked locking on a non-volatile field, so a
  second thread could take a connection the first had not finished opening.
- Uploads were copied three times, through eleven duplicated
  `CopyToAsync`-then-`ToArray` helpers, in a service built for large documents.
  One shared reader sizes the buffer from `IFormFile.Length`, and threads the
  request's cancellation token — which none of the copies did.
- The composite file hash allocated a second copy of the whole file to concatenate
  a prefix; it is now hashed incrementally.
- `ZipService` caught failures from the `ZipArchive` constructor only, so an
  archive with a good header and a damaged central directory escaped as a `500`.

### Changed — licensing

- **PDF moved into its own package, `Aelena.FileApi.Core.Pdf`, licensed
  AGPL-3.0-or-later.** `Aelena.FileApi.Core` declared MIT while depending on
  iText 7, which is AGPL with `requireLicenseAcceptance`. Publishing that would
  have told consumers they had permissive terms while pulling copyleft code into
  their build. `Core` now has no reference to iText, direct or transitive, and CI
  asserts it by inspecting the packed `.nuspec` on every push.
- **`-p:IncludePdf=false` builds the repository without the AGPL part.** A clone
  contains everything, so the package split alone does not help someone who clones.
  The switch drops the project reference, the `/pdf/*` endpoints, the `fileapi pdf`
  commands and the PDF gRPC methods; the published output contains no iText
  assembly at all. CI builds, tests and publishes both ways and fails if iText
  appears in the opt-out output.
- `LICENSING.md` documents which package is which, what the AGPL means for a
  network service, and how to opt out; the README leads with it.
- **The CLI is packaged as a .NET global tool**: `dotnet tool install -g
  Aelena.FileApi.Cli` puts `fileapi` on the PATH. It carries PDF, so it is AGPL.

### Changed


- **Retargeted to .NET 10 / C# 14**, and multi-targeted `net10.0;net11.0` — both
  build and test green. `publish` and `run` need `-f`; see the README.
- **Central Package Management** (`Directory.Packages.props`) with exact pins,
  covering the `grpc/` minirepo from the same file. Floating ranges are how the
  Scriban drift happened silently.
- **CLI ported to System.CommandLine 2.0.** The pinned `2.0.0-beta4` dated from
  2022 and its API is gone in the GA release. Along the way: `image resize -h 100`
  never set a height (`-h` is bound to `--help`); failures now reach the user as
  one line on stderr with a meaningful exit code; and a helper that called
  `Environment.Exit(1)` was removed in favour of parser-level validation.
- **FluentAssertions → AwesomeAssertions.** FluentAssertions 8 moved to a paid
  licence for commercial use; this is the MIT-licensed community fork.
- **Culture correctness.** Markdown export, ZIP CRCs, PDF page labels, and the
  rate-limit bucket key were formatted in the ambient culture, so their output
  changed shape with the machine. Those are pinned to invariant; CLI display is
  explicitly current-culture.
- **The `.editorconfig` made the build depend on the installed SDK.** Its legacy
  `value:severity` syntax was not read by the newer analyzers, which fell back to
  opposite defaults — installing the .NET 11 SDK turned a clean build into 550
  style errors on unchanged code. Options and severities are now declared
  separately.
- Source-generated `LoggerMessage` delegates in the API layer; source-generated
  regexes in `PiiService`; `ShareRepository` returns records instead of `dynamic`
  (converting its two callers surfaced 16 previously runtime-only errors).
- Language detection counted markers by scanning for `" the "` — a space either
  side — which cannot see a marker opening the text, closing it, or against
  punctuation or a newline. It now counts whole words.

### Added

- GitHub Actions: CI builds and tests on Linux and Windows across both target
  frameworks, packs all three packages, asserts the MIT/AGPL boundary, builds the
  Docker image, and runs the MIT-only configuration. A tag-driven release workflow
  packs, re-checks the licence boundary, verifies the tag matches the package
  version, and publishes to NuGet.

- `ErrorContractTests`, `ShareAccessTests`, and `JwtCookieAuthTests` — the last
  covering a component that had no tests at all, despite being the only thing
  between a request and an identity.
- Test count: **223 → 298 distinct tests**. Because every project multi-targets,
  `dotnet test` on the main solution reports **580 passing** — 290 tests run once
  on net10.0 and once on net11.0. The gRPC solution adds 8 more, so 596 executions
  in total.
