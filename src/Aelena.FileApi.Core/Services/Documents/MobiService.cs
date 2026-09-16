using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Models;

namespace Aelena.FileApi.Core.Services.Documents;

/// <summary>
/// Reads Mobipocket and early Kindle books (<c>.mobi</c>, <c>.prc</c>, <c>.azw</c>)
/// and plain PalmDOC databases.
/// <para>
/// The file is a Palm database: a header, a record offset table, and a run of
/// records. Record 0 carries the PalmDOC and MOBI headers; records 1..n carry
/// the book text, PalmDOC-LZ77 compressed. Everything this reader needs is in
/// those headers, which is why a MOBI can be validated thoroughly without
/// decompressing a byte of it.
/// </para>
/// <para>
/// Two things are recognised but not decoded, and both answer 501 rather than
/// guessing: DRM (<c>encryptionType</c> 1 or 2) and HUFF/CDIC compression
/// (<c>compression</c> 17480), which later Kindle files use.
/// </para>
/// </summary>
public static class MobiService
{
    private const int PalmHeaderSize = 78;
    private const int RecordEntrySize = 8;

    /// <summary>A Palm database may declare at most this many records before it is treated as corrupt.</summary>
    private const int MaxRecords = 65535;

    /// <summary>Cap on the decompressed text of one book.</summary>
    private const int MaxTextBytes = 256 * 1024 * 1024;

    private const int CompressionNone = 1;
    private const int CompressionPalmDoc = 2;
    private const int CompressionHuffCdic = 17480;

    // ── Validation ───────────────────────────────────────────────────────

    /// <summary>Check the database, record table and headers without decompressing anything.</summary>
    public static DocumentValidationResponse Validate(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        var issues = new List<HealthIssue>();
        var details = new Dictionary<string, object>(StringComparer.Ordinal);
        var canExtract = false;

        var db = ReadDatabase(data, issues, details);
        if (db is not null)
        {
            var header = ReadPalmDocHeader(data, db, issues, details);
            if (header is not null)
            {
                canExtract = header.Compression is CompressionNone or CompressionPalmDoc
                          && header.EncryptionType == 0
                          && header.TextRecordCount > 0
                          && issues.TrueForAll(i => i.Severity != "error");
            }
        }

        return EpubService.Build(fileName, "MOBI", issues, canExtract, details);
    }

    // ── Metadata ─────────────────────────────────────────────────────────

    /// <summary>
    /// Read the PalmDOC and MOBI headers and the EXTH block, without
    /// decompressing anything. DRM and an undecodable compression scheme are
    /// deliberately not fatal here: the metadata of a locked book is still in
    /// the clear, and refusing to report it would help nobody.
    /// </summary>
    /// <exception cref="FileApiException">422 when the database itself is unreadable.</exception>
    public static DocumentMetadataResponse GetMetadata(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        var issues = new List<HealthIssue>();
        var details = new Dictionary<string, object>(StringComparer.Ordinal);

        var db = ReadDatabase(data, issues, details);
        ThrowStructural(issues);

        var header = db is null ? null : ReadPalmDocHeader(data, db, issues, details);
        ThrowStructural(issues);

        if (header is null)
            throw new FileApiException(422, "This file is not a readable Palm database.", title: "Invalid MOBI");

        return header.ToMetadata(fileName, header.TextRecordCount);
    }

    // ── Extraction ───────────────────────────────────────────────────────

    /// <summary>Decompress the book text and split it into sections.</summary>
    /// <exception cref="FileApiException">
    /// 422 for a corrupt database, 501 for DRM or HUFF/CDIC compression.
    /// </exception>
    public static ExtractedDocument Extract(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        var issues = new List<HealthIssue>();
        var details = new Dictionary<string, object>(StringComparer.Ordinal);

        var db = ReadDatabase(data, issues, details);
        ThrowStructural(issues);

        var header = db is null ? null : ReadPalmDocHeader(data, db, issues, details);
        ThrowStructural(issues);

        if (db is null || header is null)
        {
            throw new FileApiException(422,
                "This file is not a readable Palm database.", title: "Invalid MOBI");
        }

        // DRM and HUFF/CDIC are 501s — recognised, well-formed, not decodable —
        // so they are answered before the generic 422 for a corrupt file.
        if (header.EncryptionType != 0)
        {
            throw new FileApiException(501,
                $"This book is DRM-protected (encryptionType {header.EncryptionType}). " +
                "Its text cannot be extracted without the rights key, which this service does not handle.",
                title: "Encrypted MOBI");
        }

        if (header.Compression == CompressionHuffCdic)
        {
            throw new FileApiException(501,
                "This book uses HUFF/CDIC compression, which this service does not decode. " +
                "Only uncompressed and PalmDOC-compressed MOBI files are supported. " +
                "Converting the file to EPUB first will extract cleanly.",
                title: "Unsupported MOBI Compression");
        }

        if (header.Compression is not (CompressionNone or CompressionPalmDoc))
        {
            throw new FileApiException(422,
                $"Unrecognised MOBI compression type {header.Compression}. " +
                "Valid values are 1 (none), 2 (PalmDOC) and 17480 (HUFF/CDIC).",
                title: "Invalid MOBI");
        }

        var raw = DecompressText(data, db, header);
        var html = Cp1252.Decode(raw, header.TextEncoding);
        var sections = Split(html);

        if (sections.Count == 0)
        {
            throw new FileApiException(422,
                "The book's text records decompressed to no readable content.",
                title: "No Extractable Content");
        }

        return new ExtractedDocument(header.Format, sections, header.ToMetadata(fileName, sections.Count));
    }

    // ── Palm database ────────────────────────────────────────────────────

    private static Database? ReadDatabase(
        byte[] data, List<HealthIssue> issues, Dictionary<string, object> details)
    {
        if (data.Length < PalmHeaderSize)
        {
            issues.Add(new HealthIssue("header", "error",
                $"File is {data.Length} bytes, shorter than the {PalmHeaderSize}-byte Palm database header."));
            return null;
        }

        var type = Encoding.ASCII.GetString(data, 60, 4);
        var creator = Encoding.ASCII.GetString(data, 64, 4);
        var recordCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(76, 2));

        details["palmType"] = type;
        details["palmCreator"] = creator;
        details["recordCount"] = recordCount;

        var format = (type, creator) switch
        {
            ("BOOK", "MOBI") => DocumentFormat.Mobi,
            ("TEXt", "REAd") => DocumentFormat.PalmDoc,
            _ => DocumentFormat.Unknown
        };

        if (format == DocumentFormat.Unknown)
        {
            issues.Add(new HealthIssue("header", "error",
                $"Palm database declares type '{type}' / creator '{creator}'. " +
                "A MOBI is BOOK/MOBI and a PalmDOC is TEXt/REAd; this is neither."));
            return null;
        }

        if (recordCount == 0)
        {
            issues.Add(new HealthIssue("records", "error", "The database declares zero records."));
            return null;
        }

        if (recordCount > MaxRecords)
        {
            issues.Add(new HealthIssue("records", "error",
                $"The database declares {recordCount} records, above the {MaxRecords} limit."));
            return null;
        }

        var tableEnd = PalmHeaderSize + recordCount * RecordEntrySize;
        if (tableEnd > data.Length)
        {
            issues.Add(new HealthIssue("records", "error",
                $"The record offset table needs {tableEnd} bytes but the file is only {data.Length}. " +
                "The file is truncated."));
            return null;
        }

        var offsets = new int[recordCount];
        for (var i = 0; i < recordCount; i++)
        {
            var offset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(PalmHeaderSize + i * RecordEntrySize, 4));

            if (offset > (uint)data.Length)
            {
                issues.Add(new HealthIssue("records", "error",
                    $"Record {i} claims to start at byte {offset}, past the end of a {data.Length}-byte file."));
                return null;
            }

            if (i > 0 && offset < offsets[i - 1])
            {
                issues.Add(new HealthIssue("records", "error",
                    $"Record offsets are not monotonic: record {i} starts at {offset}, " +
                    $"before record {i - 1} at {offsets[i - 1]}."));
                return null;
            }

            offsets[i] = (int)offset;
        }

        return new Database(format, offsets, data.Length);
    }

    private static PalmDocHeader? ReadPalmDocHeader(
        byte[] data, Database db, List<HealthIssue> issues, Dictionary<string, object> details)
    {
        var record0 = db.Record(data, 0);
        if (record0.Length < 16)
        {
            issues.Add(new HealthIssue("header", "error",
                $"Record 0 is {record0.Length} bytes, too short to hold the 16-byte PalmDOC header."));
            return null;
        }

        var compression = BinaryPrimitives.ReadUInt16BigEndian(record0[..2]);
        var textLength = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(record0.Slice(4, 4)), int.MaxValue);
        var textRecordCount = BinaryPrimitives.ReadUInt16BigEndian(record0.Slice(8, 2));
        var recordSize = BinaryPrimitives.ReadUInt16BigEndian(record0.Slice(10, 2));
        var encryption = BinaryPrimitives.ReadUInt16BigEndian(record0.Slice(12, 2));

        details["compression"] = compression switch
        {
            CompressionNone => "none",
            CompressionPalmDoc => "palmdoc",
            CompressionHuffCdic => "huff/cdic",
            _ => compression.ToString(CultureInfo.InvariantCulture)
        };
        details["textLength"] = textLength;
        details["textRecordCount"] = textRecordCount;
        details["encryptionType"] = encryption;

        if (compression is not (CompressionNone or CompressionPalmDoc or CompressionHuffCdic))
        {
            issues.Add(new HealthIssue("compression", "error",
                $"Unrecognised compression type {compression}; valid values are 1, 2 and 17480."));
        }
        else if (compression == CompressionHuffCdic)
        {
            issues.Add(new HealthIssue("compression", "warning",
                "HUFF/CDIC compression: the file is well-formed but this service cannot decode its text."));
        }

        if (encryption != 0)
        {
            issues.Add(new HealthIssue("drm", "error",
                $"encryptionType is {encryption}: the book is DRM-protected and its text cannot be read."));
        }

        if (textLength > MaxTextBytes)
        {
            issues.Add(new HealthIssue("limits", "error",
                $"The book declares {textLength:N0} bytes of text, above the {MaxTextBytes:N0} byte limit."));
        }

        if (textRecordCount >= db.RecordCount)
        {
            issues.Add(new HealthIssue("records", "error",
                $"The header claims {textRecordCount} text records but the database holds only " +
                $"{db.RecordCount} records in total."));
        }

        // The MOBI header is optional: a bare PalmDOC database has none.
        var header = new PalmDocHeader(
            Format: db.Format,
            Compression: compression,
            TextLength: textLength,
            TextRecordCount: textRecordCount,
            RecordSize: recordSize == 0 ? 4096 : recordSize,
            EncryptionType: encryption,
            TextEncoding: 1252,
            ExtraDataFlags: 0,
            Title: PalmName(data),
            Exth: []);

        if (record0.Length < 24 || !record0.Slice(16, 4).SequenceEqual("MOBI"u8))
        {
            if (db.Format == DocumentFormat.Mobi)
            {
                issues.Add(new HealthIssue("header", "warning",
                    "Record 0 carries no MOBI header, only the PalmDOC one. " +
                    "The text is still readable but no bibliographic metadata is available."));
            }
            return header;
        }

        var headerLength = (int)BinaryPrimitives.ReadUInt32BigEndian(record0.Slice(20, 4));
        if (headerLength < 24 || 16 + headerLength > record0.Length)
        {
            issues.Add(new HealthIssue("header", "warning",
                $"The MOBI header declares a length of {headerLength}, which does not fit in record 0. " +
                "Falling back to the PalmDOC header alone."));
            return header;
        }

        var encoding = (int)BinaryPrimitives.ReadUInt32BigEndian(record0.Slice(28, 4));
        details["textEncoding"] = encoding;

        // Trailing-entry flags only exist on headers long enough to hold them.
        var extraFlags = headerLength >= 228 && record0.Length >= 16 + 244
            ? BinaryPrimitives.ReadUInt16BigEndian(record0.Slice(16 + 242, 2))
            : 0;

        var exth = ReadExth(record0, headerLength, encoding, issues);
        var fullName = ReadFullName(record0, headerLength, encoding);

        return header with
        {
            TextEncoding = encoding is 1252 or 65001 or 1200 ? encoding : 1252,
            ExtraDataFlags = extraFlags,
            Title = fullName ?? header.Title,
            Exth = exth
        };
    }

    private static Dictionary<int, List<string>> ReadExth(
        ReadOnlySpan<byte> record0, int headerLength, int encoding, List<HealthIssue> issues)
    {
        Dictionary<int, List<string>> result = [];
        var start = 16 + headerLength;

        if (start + 12 > record0.Length || !record0.Slice(start, 4).SequenceEqual("EXTH"u8))
            return result;

        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(record0.Slice(start + 4, 4));
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(record0.Slice(start + 8, 4));

        if (length < 12 || start + length > record0.Length || count is < 0 or > 4096)
        {
            issues.Add(new HealthIssue("metadata", "warning",
                "The EXTH metadata block is malformed and was skipped; the book text is unaffected."));
            return result;
        }

        var offset = start + 12;
        var end = start + length;

        for (var i = 0; i < count && offset + 8 <= end; i++)
        {
            var type = (int)BinaryPrimitives.ReadUInt32BigEndian(record0.Slice(offset, 4));
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(record0.Slice(offset + 4, 4));

            if (size < 8 || offset + size > end)
                break;

            var value = Cp1252.Decode(record0.Slice(offset + 8, size - 8), encoding).Trim();
            if (value.Length > 0)
            {
                if (!result.TryGetValue(type, out var list))
                    result[type] = list = [];
                list.Add(value);
            }

            offset += size;
        }

        return result;
    }

    private static string? ReadFullName(ReadOnlySpan<byte> record0, int headerLength, int encoding)
    {
        if (headerLength < 92 || record0.Length < 16 + 92)
            return null;

        var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record0.Slice(16 + 84, 4));
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(record0.Slice(16 + 88, 4));

        if (length <= 0 || length > 1024 || offset < 0 || offset + length > record0.Length)
            return null;

        var name = Cp1252.Decode(record0.Slice(offset, length), encoding).Trim();
        return name.Length == 0 ? null : name;
    }

    private static string? PalmName(byte[] data)
    {
        var end = Array.IndexOf(data, (byte)0, 0, 32);
        if (end < 0) end = 32;

        var name = Cp1252.GetString(data.AsSpan(0, end)).Trim();
        return name.Length == 0 ? null : name.Replace('_', ' ');
    }

    // ── Text ─────────────────────────────────────────────────────────────

    private static byte[] DecompressText(byte[] data, Database db, PalmDocHeader header)
    {
        var output = new List<byte>(Math.Min(header.TextLength, 1 << 20));
        var last = Math.Min(header.TextRecordCount, db.RecordCount - 1);

        for (var i = 1; i <= last; i++)
        {
            var record = db.Record(data, i);
            record = StripTrailingEntries(record, header.ExtraDataFlags);

            if (header.Compression == CompressionNone)
                output.AddRange(record);
            else
                PalmDoc.Decompress(record, output);

            if (output.Count > MaxTextBytes)
            {
                throw new FileApiException(422,
                    $"The book's text exceeds the {MaxTextBytes:N0} byte extraction limit.",
                    title: "Document Too Large");
            }
        }

        // textLength is authoritative: the final record is padded out to the
        // record size, and without this trim that padding reaches the caller.
        var result = output.ToArray();
        return header.TextLength > 0 && header.TextLength < result.Length
            ? result[..header.TextLength]
            : result;
    }

    /// <summary>
    /// Remove the trailing entries appended to each text record — inline index
    /// data, and a multibyte-character overlap byte — as declared by the MOBI
    /// header's extra-data flags. Left in place they decompress into visible
    /// rubbish at every 4 KB boundary.
    /// </summary>
    private static ReadOnlySpan<byte> StripTrailingEntries(ReadOnlySpan<byte> record, int flags)
    {
        if (flags == 0)
            return record;

        var size = 0;

        for (var flag = flags >> 1; flag != 0; flag >>= 1)
        {
            if ((flag & 1) == 0)
                continue;

            var entry = BackwardsVarint(record, record.Length - size);
            if (entry <= 0 || size + entry > record.Length)
                return record;

            size += entry;
        }

        if ((flags & 1) != 0)
        {
            if (size >= record.Length)
                return record;
            size += (record[record.Length - size - 1] & 0x03) + 1;
        }

        return size >= record.Length ? [] : record[..(record.Length - size)];
    }

    /// <summary>
    /// Read Mobipocket's backwards variable-length integer: up to four bytes
    /// ending at <paramref name="end"/>, with 0x80 marking the first byte of
    /// the value. The result includes the bytes the length itself occupies.
    /// </summary>
    private static int BackwardsVarint(ReadOnlySpan<byte> record, int end)
    {
        if (end <= 0 || end > record.Length)
            return 0;

        var value = 0;
        for (var i = 1; i <= 4 && end - i >= 0; i++)
        {
            var b = record[end - i];
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) != 0)
                return value;
        }

        return 0;
    }

    private static List<DocumentSection> Split(string html)
    {
        // MOBI has no chapter structure of its own; Mobipocket marks page breaks
        // with <mbp:pagebreak/>, which is the closest thing to a section
        // boundary the format offers. Files without them convert as one section.
        var parts = html.Split("<mbp:pagebreak", StringSplitOptions.None);
        var sections = new List<DocumentSection>();

        for (var i = 0; i < parts.Length; i++)
        {
            var body = parts[i];

            // Every part after the first opens with the remainder of the tag
            // that was split on — its attributes and closing '>'.
            if (i > 0)
            {
                var gt = body.IndexOf('>');
                if (gt is >= 0 and < 256)
                    body = body[(gt + 1)..];
            }

            var text = HtmlText.ToPlainText(body);
            if (text.Length == 0)
                continue;

            sections.Add(new DocumentSection(
                Index: sections.Count + 1,
                Title: HtmlText.Title(body),
                Text: text,
                Markdown: HtmlText.ToMarkdown(body)));
        }

        return sections;
    }

    // ── Plumbing ─────────────────────────────────────────────────────────

    /// <summary>
    /// Raise 422 for a structurally broken database. DRM and compression are
    /// skipped: they are recorded as errors for <c>Validate</c>, where they do
    /// mean "you cannot get text out of this", but they are not corruption and
    /// each has its own 501 on the extraction path.
    /// </summary>
    private static void ThrowStructural(List<HealthIssue> issues)
    {
        var error = issues.Find(i =>
            i.Severity == "error" && i.Check is not ("drm" or "compression"));

        if (error is not null)
            throw new FileApiException(422, error.Message, title: "Invalid MOBI");
    }

    private sealed record Database(DocumentFormat Format, int[] Offsets, int FileLength)
    {
        public int RecordCount => Offsets.Length;

        /// <summary>
        /// One record, bounded by the next record's offset — or the end of the
        /// file for the last one. Offsets were checked for monotonicity and
        /// bounds when the table was read, so this cannot slice out of range.
        /// </summary>
        public ReadOnlySpan<byte> Record(byte[] data, int index)
        {
            var start = Offsets[index];
            var end = index + 1 < Offsets.Length ? Offsets[index + 1] : FileLength;
            return end <= start ? [] : data.AsSpan(start, end - start);
        }
    }

    private sealed record PalmDocHeader(
        DocumentFormat Format,
        int Compression,
        int TextLength,
        int TextRecordCount,
        int RecordSize,
        int EncryptionType,
        int TextEncoding,
        int ExtraDataFlags,
        string? Title,
        Dictionary<int, List<string>> Exth)
    {
        // EXTH record types, from the Mobipocket format documentation.
        private const int ExthAuthor = 100;
        private const int ExthPublisher = 101;
        private const int ExthDescription = 103;
        private const int ExthIsbn = 104;
        private const int ExthSubject = 105;
        private const int ExthPublished = 106;
        private const int ExthRights = 109;
        private const int ExthUpdatedTitle = 503;
        private const int ExthLanguage = 524;

        public DocumentMetadataResponse ToMetadata(string fileName, int sectionCount)
        {
            var extra = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["compression"] = Compression == CompressionNone ? "none" : "palmdoc",
                ["textEncoding"] = TextEncoding.ToString(CultureInfo.InvariantCulture),
                ["textRecords"] = TextRecordCount.ToString(CultureInfo.InvariantCulture)
            };

            return new DocumentMetadataResponse(
                FileName: fileName,
                Format: Format == DocumentFormat.PalmDoc ? "PalmDOC" : "MOBI",
                Title: First(ExthUpdatedTitle) ?? Title,
                Authors: Exth.GetValueOrDefault(ExthAuthor),
                Language: First(ExthLanguage),
                Publisher: First(ExthPublisher),
                Identifier: First(ExthIsbn),
                Published: First(ExthPublished),
                Description: First(ExthDescription),
                Rights: First(ExthRights),
                Subjects: Exth.GetValueOrDefault(ExthSubject),
                SectionCount: sectionCount,
                Extra: extra);
        }

        private string? First(int type) =>
            Exth.TryGetValue(type, out var values) ? values[0] : null;
    }
}
