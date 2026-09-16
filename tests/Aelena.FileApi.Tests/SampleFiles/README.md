# Test fixtures

Real files in real formats, all of them public domain. Every one is checked in
deliberately: EPUB, MOBI, DjVu and legacy `.doc` are container formats whose
edge cases live in bytes a synthesised fixture would never produce, and a parser
that only ever sees its own author's output is a parser that has not been
tested.

Nothing here is licensed more restrictively than the repository. Provenance for
each file is below so that stays checkable.

| File | What it is | Provenance | Licence |
|---|---|---|---|
| `public-domain-pieces.epub` | EPUB 2, 3 spine items, generated with calibre 7.21 | Shakespeare's Sonnet 18 (1609) and two Aesop fables in George Fyler Townsend's 1887 translation | Public domain — author died 1616 / translation published 1887 |
| `public-domain-pieces.mobi` | MOBI, PalmDOC-compressed (`compression 2`), UTF-8, with an EXTH block | Same text, same conversion run | Public domain |
| `public-domain-pieces.doc` | Word 97-2003 binary, OLE2 compound file, `nFib 0x00C1`, single-piece piece table in `1Table` | Same text, saved by Microsoft Word as `wdFormatDocument97` | Public domain |
| `un-resolution-1837.djvu` | DjVu `DJVM` bundle: 4 pages, `DIRM` directory, shared `Djbz`, per-page `INCL` and BZZ-compressed `TXTz` text layers | [UN Security Council Resolution 1837](https://commons.wikimedia.org/wiki/File:UN_Security_Council_Resolution_1837.djvu) (2008), via Wikimedia Commons | Public domain — UN document |
| `hr-report-94-1476-p249.djvu` | DjVu single-page `DJVU` form: `INFO`, `Sjbz`, `FG44`/`BG44`, **no text layer** | [H.R. Rep. No. 94-1476, page 249](https://commons.wikimedia.org/wiki/File:H.R._Rep._No._94-1476_(1976)_Page_249.djvu) (1976), via Wikimedia Commons | Public domain — work of the US federal government |

## Why these two DjVu files and not one

They exercise the two halves of the DjVu reader that behave differently.

`un-resolution-1837.djvu` is a multi-page bundle with a text layer, so it covers
the `DJVM` walk, the `DIRM` directory, the component forms and the reporting of
a `TXTz` layer this service cannot decode — the 501 path.

`hr-report-94-1476-p249.djvu` is a single-page form with page images and no OCR
results at all, which is the other, much more common shape, and has to be
distinguished from the first: "I cannot decode this text layer" and "there is no
text layer" are different answers and the caller needs to be able to tell them
apart.

## Regenerating the first three

The two DjVu files are downloads and cannot be regenerated. The other three were
produced from one HTML source:

```bash
# EPUB and MOBI
ebook-convert pd.html pd.epub --title "Three Public Domain Pieces" \
  --authors "Shakespeare and Aesop" --language en --no-default-epub-cover
ebook-convert pd.html pd.mobi --title "Three Public Domain Pieces" \
  --authors "Shakespeare and Aesop" --language en --mobi-file-type old

# DOC — Word's own writer, so the fixture is a genuine Word binary rather
# than something this repository's assumptions were baked into
$word = New-Object -ComObject Word.Application
$doc = $word.Documents.Open("$pwd\pd.html")
$doc.SaveAs2("$pwd\pd.doc", 0)   # 0 = wdFormatDocument97
```
