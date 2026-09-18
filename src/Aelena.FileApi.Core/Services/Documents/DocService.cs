using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Models;

namespace Aelena.FileApi.Core.Services.Documents;

/// <summary>
/// Reads the text of a legacy binary Word document (<c>.doc</c>, Word 97-2003).
/// <para>
/// The document's characters do not sit in one place. The <c>WordDocument</c>
/// stream opens with the File Information Block, which points into a table
/// stream — <c>1Table</c> or <c>0Table</c>, chosen by a bit in the FIB — where
/// a <em>piece table</em> lists runs of text by offset. Each piece says where
/// its characters are and whether they are one byte of CP1252 or two of
/// UTF-16LE. Reading a <c>.doc</c> means walking that table; reading the
/// <c>WordDocument</c> stream front to back, as a naive extractor does, returns
/// deleted text, field codes and formatting bytes mixed in with the content.
/// </para>
/// <para>
/// Character formatting is not read, so the Markdown this produces is
/// paragraph-level: headings are inferred from shape, the way the PDF
/// extractor infers them, not read from the style sheet.
/// </para>
/// </summary>
public static class DocService
{
    /// <summary>The <c>wIdent</c> every Word binary document starts with.</summary>
    private const ushort WordMagic = 0xA5EC;

    /// <summary>Offset of <c>fcClx</c> in the FIB: 154 for the FibRgFcLcb97 block, plus 33 pairs of 8 bytes.</summary>
    private const int FcClxOffset = 154 + 33 * 8;

    /// <summary>Offset of <c>ccpText</c>: the FibRgLw97 block starts at 64, and ccpText is 12 into it.</summary>
    private const int CcpTextOffset = 64 + 12;

    private const int MinFibSize = FcClxOffset + 8;

    /// <summary>Cap on the characters extracted from one document.</summary>
    private const int MaxTextChars = 64 * 1024 * 1024;

    // ── Validation ───────────────────────────────────────────────────────

    /// <summary>Check the compound file, the FIB and the piece table without decoding text.</summary>
    public static DocumentValidationResponse Validate(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        var issues = new List<HealthIssue>();
        var details = new Dictionary<string, object>(StringComparer.Ordinal);
        var canExtract = false;

        if (!CompoundFile.TryOpen(data, out var cfb, out var error))
        {
            issues.Add(new HealthIssue("container", "error", $"Not a readable compound file: {error}"));
            return EpubService.Build(fileName, "DOC", issues, canExtract, details);
        }

        var document = cfb!.ReadStream("WordDocument");
        if (document is null)
        {
            issues.Add(new HealthIssue("container", "error",
                "The compound file has no 'WordDocument' stream, so it is not a Word document. " +
                $"It holds: {string.Join(", ", cfb.StreamNames.Take(12))}."));
            return EpubService.Build(fileName, "DOC", issues, canExtract, details);
        }

        var fib = ReadFib(document, issues, details);
        if (fib is not null)
        {
            var table = cfb.ReadStream(fib.TableStreamName);
            if (table is null)
            {
                issues.Add(new HealthIssue("table", "error",
                    $"The FIB names '{fib.TableStreamName}' as its table stream, which the file does not hold."));
            }
            else
            {
                details["tableStream"] = fib.TableStreamName;
                var pieces = ReadPieceTable(table, fib, issues, details);
                canExtract = pieces.Count > 0 && issues.TrueForAll(i => i.Severity != "error");
            }
        }

        return EpubService.Build(fileName, "DOC", issues, canExtract, details);
    }

    // ── Metadata ─────────────────────────────────────────────────────────

    /// <summary>
    /// Describe the document from its FIB and piece table, without decoding a
    /// character of its text.
    /// </summary>
    /// <exception cref="FileApiException">422 for a malformed or encrypted file.</exception>
    public static DocumentMetadataResponse GetMetadata(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        var parsed = Parse(data);
        return Describe(fileName, parsed.Fib, parsed.Pieces.Count, paragraphs: null);
    }

    // ── Extraction ───────────────────────────────────────────────────────

    /// <summary>Walk the piece table and decode the document's main text.</summary>
    /// <exception cref="FileApiException">422 for a malformed file, 422 for an encrypted one.</exception>
    public static ExtractedDocument Extract(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        var (fib, document, pieces) = Parse(data);

        var text = Decode(document, pieces, fib.CcpText);
        var clean = Clean(text);

        if (clean.Length == 0)
        {
            throw new FileApiException(422,
                "This document's piece table resolved to no text. It may hold only images or embedded objects.",
                title: "No Extractable Content");
        }

        var paragraphs = clean.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var section = new DocumentSection(
            Index: 1,
            Title: paragraphs.Count > 0 && IsLikelyHeading(paragraphs[0]) ? paragraphs[0] : null,
            Text: string.Join("\n\n", paragraphs),
            Markdown: ToMarkdown(paragraphs));

        return new ExtractedDocument(
            DocumentFormat.Doc, [section], Describe(fileName, fib, pieces.Count, paragraphs.Count));
    }

    /// <summary>
    /// Open the compound file and read the FIB and piece table — everything both
    /// the metadata and the extraction paths need, and nothing either does alone.
    /// </summary>
    private static (Fib Fib, byte[] Document, List<Piece> Pieces) Parse(byte[] data)
    {
        if (!CompoundFile.TryOpen(data, out var cfb, out var error))
            throw new FileApiException(422, $"This .doc is not a readable compound file: {error}",
                title: "Invalid DOC");

        var document = cfb!.ReadStream("WordDocument")
            ?? throw new FileApiException(422,
                "This compound file has no 'WordDocument' stream, so it is not a Word document.",
                title: "Invalid DOC");

        var issues = new List<HealthIssue>();
        var details = new Dictionary<string, object>(StringComparer.Ordinal);

        var fib = ReadFib(document, issues, details);
        Throw(issues);

        var table = cfb.ReadStream(fib!.TableStreamName)
            ?? throw new FileApiException(422,
                $"The FIB names '{fib.TableStreamName}' as its table stream, which this file does not hold.",
                title: "Invalid DOC");

        var pieces = ReadPieceTable(table, fib, issues, details);
        Throw(issues);

        return (fib, document, pieces);
    }

    private static DocumentMetadataResponse Describe(
        string fileName, Fib fib, int pieceCount, int? paragraphs)
    {
        var extra = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["nFib"] = $"0x{fib.NFib:X4}",
            ["tableStream"] = fib.TableStreamName,
            ["pieces"] = pieceCount.ToString(CultureInfo.InvariantCulture),
            ["complex"] = fib.IsComplex ? "true" : "false"
        };

        if (paragraphs is not null)
            extra["paragraphs"] = paragraphs.Value.ToString(CultureInfo.InvariantCulture);

        return new DocumentMetadataResponse(
            FileName: fileName,
            Format: "DOC",
            SectionCount: 1,
            Extra: extra);
    }

    // ── File Information Block ───────────────────────────────────────────

    private static Fib? ReadFib(byte[] document, List<HealthIssue> issues, Dictionary<string, object> details)
    {
        if (document.Length < 32)
        {
            issues.Add(new HealthIssue("fib", "error",
                $"The WordDocument stream is {document.Length} bytes, too short to hold a FIB."));
            return null;
        }

        var ident = BinaryPrimitives.ReadUInt16LittleEndian(document.AsSpan(0, 2));
        if (ident != WordMagic)
        {
            issues.Add(new HealthIssue("fib", "error",
                $"The WordDocument stream opens with 0x{ident:X4}, not the 0x{WordMagic:X4} every Word " +
                "binary document carries."));
            return null;
        }

        var nFib = BinaryPrimitives.ReadUInt16LittleEndian(document.AsSpan(2, 2));
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(document.AsSpan(10, 2));

        var isComplex = (flags & 0x0004) != 0;
        var isEncrypted = (flags & 0x0100) != 0;
        var whichTable = (flags & 0x0200) != 0;

        details["nFib"] = $"0x{nFib:X4}";
        details["encrypted"] = isEncrypted;
        details["complex"] = isComplex;

        if (isEncrypted)
        {
            issues.Add(new HealthIssue("encryption", "error",
                "The FIB's fEncrypted bit is set: this document is password-protected. " +
                "Remove the protection in Word before extracting its text."));
            return null;
        }

        // nFib below 0x00C1 is Word 6 or Word 95, whose FIB has a different
        // layout — the offsets below would read unrelated bytes.
        if (nFib < 0x00C1)
        {
            issues.Add(new HealthIssue("fib", "error",
                $"nFib is 0x{nFib:X4}, identifying a Word 6.0/95 document. Only the Word 97-2003 " +
                "format (nFib 0x00C1 and above) is supported; re-save the file in a later Word version."));
            return null;
        }

        if (document.Length < MinFibSize)
        {
            issues.Add(new HealthIssue("fib", "error",
                $"The WordDocument stream is {document.Length} bytes, too short to hold the FIB's " +
                $"file-offset block (needs {MinFibSize})."));
            return null;
        }

        var fcMin = BinaryPrimitives.ReadInt32LittleEndian(document.AsSpan(24, 4));
        var ccpText = BinaryPrimitives.ReadInt32LittleEndian(document.AsSpan(CcpTextOffset, 4));
        var fcClx = BinaryPrimitives.ReadInt32LittleEndian(document.AsSpan(FcClxOffset, 4));
        var lcbClx = BinaryPrimitives.ReadInt32LittleEndian(document.AsSpan(FcClxOffset + 4, 4));

        details["ccpText"] = ccpText;

        if (ccpText is < 0 or > MaxTextChars)
        {
            issues.Add(new HealthIssue("fib", "error",
                $"The FIB declares {ccpText:N0} characters of main text, outside the supported range."));
            return null;
        }

        return new Fib(nFib, isComplex, whichTable ? "1Table" : "0Table", ccpText, fcMin, fcClx, lcbClx);
    }

    // ── Piece table ──────────────────────────────────────────────────────

    /// <summary>
    /// Read the CLX at <c>fcClx</c>. A CLX is a sequence of records: type 1 is a
    /// formatting run to skip over, type 2 is the <c>PlcPcd</c> — the piece
    /// table itself, laid out as n+1 character positions followed by n 8-byte
    /// piece descriptors.
    /// </summary>
    private static List<Piece> ReadPieceTable(
        byte[] table, Fib fib, List<HealthIssue> issues, Dictionary<string, object> details)
    {
        var pieces = new List<Piece>();

        if (fib.LcbClx <= 0 || fib.FcClx < 0 || (long)fib.FcClx + fib.LcbClx > table.Length)
        {
            // No piece table. Pre-complex documents keep their text in one run
            // at the front of the WordDocument stream; that is handled by the
            // caller falling back to a single implicit piece.
            issues.Add(new HealthIssue("pieces", "warning",
                "The FIB declares no piece table; the document's text will be read as a single " +
                "CP1252 run from the start of the text area."));

            // fcMin is where the FIB says the main text starts; 0x200 is the
            // conventional value but it is not guaranteed, so read it rather
            // than assume it.
            var start = fib.FcMin > 0 ? fib.FcMin : 0x200;
            pieces.Add(new Piece(CharStart: 0, CharEnd: fib.CcpText, Offset: start, Compressed: true));
            return pieces;
        }

        var offset = fib.FcClx;
        var end = fib.FcClx + fib.LcbClx;

        while (offset < end)
        {
            var type = table[offset++];

            if (type == 1)
            {
                if (offset + 2 > end) break;
                var size = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(offset, 2));
                offset += 2 + size;
                continue;
            }

            if (type != 2)
                break;

            if (offset + 4 > end)
                break;

            var lcb = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(offset, 4));
            offset += 4;

            if (lcb < 4 || offset + lcb > end)
            {
                issues.Add(new HealthIssue("pieces", "error",
                    $"The piece table declares {lcb} bytes, which do not fit in the table stream."));
                return [];
            }

            // n pieces need n+1 character positions (4 bytes each) and n
            // descriptors (8 bytes each): lcb = 4(n+1) + 8n = 12n + 4.
            var count = (lcb - 4) / 12;
            if (count <= 0)
            {
                issues.Add(new HealthIssue("pieces", "error",
                    "The piece table holds no pieces, so the document declares no text."));
                return [];
            }

            var cpBase = offset;
            var pcdBase = offset + (count + 1) * 4;

            for (var i = 0; i < count; i++)
            {
                var cpStart = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(cpBase + i * 4, 4));
                var cpEnd = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(cpBase + (i + 1) * 4, 4));
                var fc = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(pcdBase + i * 8 + 2, 4));

                if (cpEnd <= cpStart)
                    continue;

                // Bit 30 of fc means "this run is single-byte CP1252"; the real
                // byte offset is then the remaining 30 bits halved.
                var compressed = (fc & 0x40000000) != 0;
                var position = (int)(fc & 0x3FFFFFFF);
                if (compressed) position /= 2;

                pieces.Add(new Piece(cpStart, cpEnd, position, compressed));
            }

            offset += lcb;
        }

        details["pieceCount"] = pieces.Count;

        if (pieces.Count == 0)
        {
            issues.Add(new HealthIssue("pieces", "error",
                "No usable pieces were found in the piece table."));
        }

        return pieces;
    }

    private static string Decode(byte[] document, List<Piece> pieces, int ccpText)
    {
        var sb = new StringBuilder(Math.Min(ccpText, 1 << 20));

        foreach (var piece in pieces)
        {
            if (sb.Length >= ccpText)
                break;

            var chars = Math.Min(piece.CharEnd - piece.CharStart, ccpText - sb.Length);
            if (chars <= 0)
                continue;

            var bytes = piece.Compressed ? chars : chars * 2;
            if (piece.Offset < 0 || (long)piece.Offset + bytes > document.Length)
                continue;

            var span = document.AsSpan(piece.Offset, bytes);
            sb.Append(piece.Compressed ? Cp1252.GetString(span) : Encoding.Unicode.GetString(span));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Turn Word's in-band control characters into text. The document stream is
    /// full of them: paragraph and cell marks, field code delimiters, and
    /// placeholders where a picture or annotation sits. Left in, they reach the
    /// caller as stray glyphs and as field instructions that were never meant
    /// to be visible.
    /// </summary>
    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        var inFieldInstruction = false;

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\u0013':  // field begin — everything up to the separator
                    inFieldInstruction = true;   // is the instruction, not the result
                    continue;
                case '\u0014':  // field separator
                    inFieldInstruction = false;
                    continue;
                case '\u0015':  // field end
                    inFieldInstruction = false;
                    continue;
            }

            if (inFieldInstruction)
                continue;

            switch (ch)
            {
                case '\r' or '\u0007':          // paragraph mark, cell/row mark
                    sb.Append('\n');
                    break;
                case '\u000B':                  // line break
                    sb.Append('\n');
                    break;
                case '\u000C':                  // page break
                    sb.Append('\n');
                    break;
                case '\u001E':                  // non-breaking hyphen
                    sb.Append('-');
                    break;
                case ' ':                  // non-breaking space
                    sb.Append(' ');
                    break;
                case '\u001F':                  // optional hyphen — invisible unless it breaks
                case '\u0001':                  // picture placeholder
                case '\u0002':                  // auto-numbered footnote reference
                case '\u0005':                  // annotation reference
                case '\u0008':                  // drawn object
                case '￾' or '￿':
                    break;
                default:
                    if (!char.IsControl(ch) || ch == '\t' || ch == '\n')
                        sb.Append(ch);
                    break;
            }
        }

        return sb.ToString().Trim();
    }

    private static string ToMarkdown(List<string> paragraphs)
    {
        var sb = new StringBuilder();

        foreach (var paragraph in paragraphs)
        {
            if (sb.Length > 0)
                sb.Append("\n\n");

            // Without the style sheet there is no authoritative heading level,
            // so this infers one from shape — the same compromise the PDF
            // Markdown extractor makes, and documented as such.
            sb.Append(IsLikelyHeading(paragraph) ? "## " : "").Append(paragraph);
        }

        return sb.ToString();
    }

    private static bool IsLikelyHeading(string line) =>
        line.Length is > 0 and < 80
        && !line.EndsWith('.')
        && !line.EndsWith(',')
        && line.Split(' ').Length <= 12;

    private static void Throw(List<HealthIssue> issues)
    {
        var error = issues.Find(i => i.Severity == "error");
        if (error is not null)
            throw new FileApiException(422, error.Message, title: "Invalid DOC");
    }

    private sealed record Fib(
        ushort NFib, bool IsComplex, string TableStreamName, int CcpText, int FcMin, int FcClx, int LcbClx);

    private readonly record struct Piece(int CharStart, int CharEnd, int Offset, bool Compressed);
}
