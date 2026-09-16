# fileapi — command-line document processing

A .NET global tool for PDF, DOCX, image, email, hashing, PII, readability and ZIP
operations, with rich terminal output.

> 📖 **Full documentation and the complete command list are in the
> [repository README](https://github.com/aelena/file-api#readme).**

---

## Install

```bash
dotnet tool install -g Aelena.FileApi.Cli
```

That puts **`fileapi`** on your PATH. Requires the .NET 10 runtime.

```bash
dotnet tool update -g Aelena.FileApi.Cli     # upgrade
dotnet tool uninstall -g Aelena.FileApi.Cli  # remove
```

## Commands

```bash
fileapi hash invoice.pdf                        # SHA-256, MD5, SHA-1, composite
fileapi pdf metrics report.pdf                  # pages, words, OCR need, signatures
fileapi pdf extract-text report.pdf --pages 1,3,5-8
fileapi pdf merge -o merged.pdf a.pdf b.pdf
fileapi pdf encrypt --password s3cret doc.pdf
fileapi docx markdown notes.docx                # convert to Markdown
fileapi docx metrics notes.docx
fileapi convert detect mystery-file             # identify from content, not extension
fileapi convert validate book.epub              # structural checks, every issue listed
fileapi convert markdown book.mobi > book.md    # EPUB/MOBI/DjVu/.doc to Markdown
fileapi convert pdf report.doc -o report.pdf    # and to PDF
fileapi image resize --width 800 photo.jpg      # aspect ratio preserved
fileapi image convert --format webp photo.png
fileapi image exif photo.jpg
fileapi pii detect contract.txt                 # emails, cards, national IDs
fileapi readability essay.txt                   # Flesch, Gunning Fog, SMOG
fileapi txt search --query TODO notes.txt
fileapi zip archive.zip                         # list entries without extracting
fileapi email message.eml
```

`fileapi --help`, or `fileapi <command> --help`, for the full set.

## Exit codes

Failures print one line on stderr — not a stack trace — and set a code you can
branch on:

| Code | Meaning |
|------|---------|
| `0` | Success |
| `1` | The operation failed unexpectedly |
| `2` | Bad input file or arguments |
| `3` | Operation not implemented for this format |
| `4` | Could not read or write a file |

Diagnostics go to stderr, so `fileapi docx markdown x.docx > out.md` captures the
document and not the warnings.

## ⚠️ This tool is AGPL-3.0-or-later

It includes PDF support, which is built on [iText 7](https://itextpdf.com/) under
the AGPL. Because a .NET tool package **bundles its dependencies rather than
declaring them**, iText ships inside this package — the empty dependency list on
this page does not mean otherwise.

Using the tool on your own machine is unproblematic. Redistributing it, or
building it into a service you offer over a network, carries the AGPL obligation.

If you want the library rather than the tool, and cannot accept the AGPL, use
**[`Aelena.FileApi.Core`](https://www.nuget.org/packages/Aelena.FileApi.Core)**,
which is MIT and has no iText anywhere in its graph.

## The three packages

| Package | Licence | Depends on | Contains |
|---------|---------|-----------|----------|
| [`Aelena.FileApi.Core`](https://www.nuget.org/packages/Aelena.FileApi.Core) | MIT | — | Everything except PDF |
| [`Aelena.FileApi.Core.Pdf`](https://www.nuget.org/packages/Aelena.FileApi.Core.Pdf) | AGPL-3.0-or-later | `Core`, iText 7 | PDF only |
| `Aelena.FileApi.Cli` (this one) | AGPL-3.0-or-later | bundled, not declared | Both, as a tool |

## More

- 📖 [Repository and full documentation](https://github.com/aelena/file-api#readme)
- ⚖️ [Licensing explained in detail](https://github.com/aelena/file-api/blob/main/LICENSING.md)
- 📝 [Changelog](https://github.com/aelena/file-api/blob/main/CHANGELOG.md)
- 🐛 [Issues](https://github.com/aelena/file-api/issues)
