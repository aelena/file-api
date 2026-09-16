# Licensing

This repository is **MIT**, but one of its dependencies is not, and that changes
what you can do with part of it. This document says exactly which part, and how to
avoid it if you need to.

## The short version

| What you take | Effective licence | Closed-source friendly? |
|---------------|-------------------|-------------------------|
| `Aelena.FileApi.Core` from NuGet | MIT | **Yes** |
| `Aelena.FileApi.Core.Pdf` from NuGet | AGPL-3.0-or-later | No |
| `Aelena.FileApi.Cli` (`fileapi` tool) | AGPL-3.0-or-later | No |
| This repository, built normally | AGPL-3.0-or-later | No |
| This repository, built `-p:IncludePdf=false` | MIT | **Yes** |

## Why

All source in this repository is written by the project and offered under the MIT
licence in [LICENSE](LICENSE). The complication is **iText 7**, the library the PDF
operations are built on. iText 7 is dual-licensed: AGPL, or a commercial licence
sold by iText Software.

The AGPL is a strong copyleft licence. Combining AGPL code with your own generally
obliges you to offer your combined work's source under the AGPL too — and unlike
the GPL, the AGPL's obligation is triggered by *running the software as a network
service*, not only by distributing it. For a document-processing HTTP API, that is
precisely the case that matters.

So a package that requires iText cannot honestly be labelled MIT, however the
project's own code is licensed. Publishing one would be telling consumers they had
permissive terms while quietly pulling copyleft code into their build.

## What was done about it

PDF lives in its own project and its own package.

```
src/Aelena.FileApi.Core/         MIT     no reference to iText, direct or transitive
src/Aelena.FileApi.Core.Pdf/     AGPL    the only project that references iText
```

The boundary is structural, not a convention: `Aelena.FileApi.Core` has no project
or package reference that reaches iText, and CI asserts this on every push by
inspecting the packed `.nuspec` and failing if an iText dependency appears in the
MIT package.

## If you consume the NuGet packages

Take `Aelena.FileApi.Core` alone and no copyleft code enters your build. You get
DOCX, images, email, hashing, PII detection, readability, text analysis, ZIP
inspection, share links, and the job store.

```bash
dotnet add package Aelena.FileApi.Core
```

Add `Aelena.FileApi.Core.Pdf` only if you have read the AGPL and accept it, or hold
a commercial iText licence. NuGet will prompt for licence acceptance.

## If you clone this repository

A clone contains everything, so the default build is AGPL-encumbered. To build
without it:

```bash
dotnet build -p:IncludePdf=false
dotnet test  -p:IncludePdf=false
dotnet publish src/Aelena.FileApi.Api -f net10.0 -c Release -p:IncludePdf=false
```

This removes the `Aelena.FileApi.Core.Pdf` project reference, `PdfEndpoints.cs`,
`PdfCommand.cs`, and the PDF gRPC methods. The published output contains no iText
assembly — the dependency is absent, not merely unused. The gRPC PDF methods are
then simply not overridden, so the generated base class answers them with the
standard `Unimplemented` status rather than failing at startup.

The test suite is a supported configuration too, not just the build: the 21 PDF
endpoint tests are compiled out and the remaining 269 pass. CI runs it this way on
every push alongside the default build.

CI publishes both ways on every push and fails if an iText assembly appears in the
opt-out output.

## Buying your way out instead

If you want PDF support without the AGPL obligation, buy a commercial iText
licence from [itextpdf.com](https://itextpdf.com/). That is an agreement between
you and iText Software; nothing in this repository grants it, and using
`Aelena.FileApi.Core.Pdf` does not convey it.

Under a commercial iText licence you may use the PDF package as you would any MIT
component — this project's own contribution to it is MIT, and only iText's terms
are what restrict it.

## Other dependencies

None of these are copyleft, but two are worth knowing about.

| Dependency | Licence | Note |
|------------|---------|------|
| iText 7 | **AGPL-3.0** or commercial | Isolated in `Aelena.FileApi.Core.Pdf`. |
| SixLabors.ImageSharp | Six Labors Split License | Free for open-source and for organisations under a revenue threshold; a commercial licence is required above it. Check your position. |
| DocumentFormat.OpenXml | MIT | |
| MimeKit | MIT | |
| Scriban | BSD-2-Clause | |
| SharpToken | MIT | |
| Dapper, Microsoft.Data.Sqlite | MIT / Apache-2.0 | |
| Serilog, OpenTelemetry | Apache-2.0 | |
| System.CommandLine, Spectre.Console | MIT | |
| Grpc.AspNetCore, Google.Protobuf | Apache-2.0 / BSD-3-Clause | |
| xunit, AwesomeAssertions, NSubstitute, coverlet | Apache-2.0 / MIT / BSD-3-Clause | Test-only; not distributed. |

## One feature the licence boundary costs us

`Aelena.FileApi.Core` reads DjVu documents, but only extracts text from the
uncompressed `TXTa` chunk. The compressed `TXTz` form that most real files use
is BZZ-encoded, and BZZ needs the ZP adaptive arithmetic coder. The only
published implementation of that coder is DjVuLibre's, which is **GPL-2.0** —
strong copyleft, and not compatible with shipping this package as MIT.

So the code is not there, and `/convert/text` answers `501` naming the reason
rather than quietly returning nothing. The alternative was to vendor GPL code
into a package this repository advertises as free of copyleft, which would have
made the MIT claim false for every consumer of it. `djvutxt` from DjVuLibre
extracts that text, under its own licence, on the caller's own machine.

Everything else about a DjVu — page count, geometry, resolution, structure,
validation — is read and returned normally.

## Not legal advice

This is a description of the licences involved and of how the code is arranged,
written by the project for its own users. It is not legal advice. If the AGPL
question is material to your business, talk to a lawyer.
