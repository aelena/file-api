using System.Text;

namespace Aelena.FileApi.Core.Services.Documents;

/// <summary>
/// Container formats this service family recognises from their magic bytes.
/// Sniffing is by content, never by extension: the extension is only ever used
/// to report a mismatch back to the caller.
/// </summary>
public enum DocumentFormat
{
    /// <summary>Nothing recognisable in the leading bytes.</summary>
    Unknown,

    /// <summary>ZIP container whose <c>mimetype</c> entry declares <c>application/epub+zip</c>.</summary>
    Epub,

    /// <summary>Palm database carrying a Mobipocket/Kindle book (<c>BOOK</c>/<c>MOBI</c>).</summary>
    Mobi,

    /// <summary>Palm database carrying plain PalmDOC text (<c>TEXt</c>/<c>REAd</c>).</summary>
    PalmDoc,

    /// <summary>AT&amp;T IFF container holding a DjVu document.</summary>
    Djvu,

    /// <summary>OLE2 compound file holding a <c>WordDocument</c> stream — legacy binary Word.</summary>
    Doc,

    /// <summary>OLE2 compound file that is not a Word document (XLS, PPT, MSG…).</summary>
    Ole2Other,

    /// <summary>OOXML word processing document.</summary>
    Docx,

    /// <summary>Portable Document Format.</summary>
    Pdf,

    /// <summary>Rich Text Format.</summary>
    Rtf,

    /// <summary>A ZIP archive that is neither an EPUB nor a DOCX.</summary>
    Zip
}

/// <summary>
/// Content-based format detection for the conversion endpoints.
/// <para>
/// Every reader in this namespace starts here. Dispatching on the extension is
/// how a renamed <c>.zip</c> ends up inside a DOCX reader and surfaces as a 500;
/// sniffing first means the caller gets a 415 that names what was actually sent.
/// </para>
/// </summary>
public static class DocumentFormatSniffer
{
    /// <summary>OLE2/CFB compound file signature.</summary>
    private static ReadOnlySpan<byte> Ole2Signature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Identify a buffer by its leading bytes and container layout.</summary>
    public static DocumentFormat Detect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < 8)
            return DocumentFormat.Unknown;

        if (data.AsSpan(0, 8).SequenceEqual(Ole2Signature))
            return DetectOle2(data);

        if (StartsWith(data, "%PDF-"))
            return DocumentFormat.Pdf;

        if (StartsWith(data, @"{\rtf"))
            return DocumentFormat.Rtf;

        if (StartsWith(data, "AT&TFORM"))
            return DetectIff(data);

        if (data[0] == 'P' && data[1] == 'K' && data[2] == 3 && data[3] == 4)
            return DetectZip(data);

        // A Palm database declares its type and creator 60 bytes in, after the
        // 32-byte name and the date/offset block. There is no signature at the
        // start of the file, so this check has to come last.
        if (data.Length >= 78)
        {
            var type = Ascii(data, 60, 4);
            var creator = Ascii(data, 64, 4);

            if (type == "BOOK" && creator == "MOBI")
                return DocumentFormat.Mobi;
            if (type == "TEXt" && creator == "REAd")
                return DocumentFormat.PalmDoc;
        }

        return DocumentFormat.Unknown;
    }

    /// <summary>Human-readable name for a format, as it appears in responses and errors.</summary>
    public static string Describe(DocumentFormat format) => format switch
    {
        DocumentFormat.Epub => "EPUB",
        DocumentFormat.Mobi => "MOBI",
        DocumentFormat.PalmDoc => "PalmDOC",
        DocumentFormat.Djvu => "DjVu",
        DocumentFormat.Doc => "DOC (Word 97-2003)",
        DocumentFormat.Ole2Other => "OLE2 compound file (not a Word document)",
        DocumentFormat.Docx => "DOCX",
        DocumentFormat.Pdf => "PDF",
        DocumentFormat.Rtf => "RTF",
        DocumentFormat.Zip => "ZIP",
        _ => "unrecognised"
    };

    /// <summary>The format a file name claims to be, or <see cref="DocumentFormat.Unknown"/>.</summary>
    public static DocumentFormat FromExtension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ext switch
        {
            ".epub" => DocumentFormat.Epub,
            ".mobi" or ".azw" or ".azw3" or ".prc" => DocumentFormat.Mobi,
            ".pdb" => DocumentFormat.PalmDoc,
            ".djvu" or ".djv" => DocumentFormat.Djvu,
            ".doc" => DocumentFormat.Doc,
            ".docx" => DocumentFormat.Docx,
            ".pdf" => DocumentFormat.Pdf,
            ".rtf" => DocumentFormat.Rtf,
            ".zip" => DocumentFormat.Zip,
            _ => DocumentFormat.Unknown
        };
    }

    /// <summary>
    /// True when a sniffed format and an extension disagree in a way worth
    /// reporting. Unknown on either side is silence, not a mismatch, and MOBI
    /// and PalmDOC are close enough relatives to let the pairing through.
    /// </summary>
    public static bool IsExtensionMismatch(DocumentFormat detected, DocumentFormat declared)
    {
        if (detected == DocumentFormat.Unknown || declared == DocumentFormat.Unknown)
            return false;
        if (detected == declared)
            return false;

        var mobiFamily = detected is DocumentFormat.Mobi or DocumentFormat.PalmDoc
                      && declared is DocumentFormat.Mobi or DocumentFormat.PalmDoc;

        return !mobiFamily;
    }

    // ── Container probes ─────────────────────────────────────────────────

    private static DocumentFormat DetectIff(byte[] data)
    {
        // AT&TFORM <BE32 length> <form type>. DJVU is a single page, DJVM a
        // multi-page bundle, DJVI a shared-component file, THUM a thumbnail
        // bundle — all four are DjVu containers as far as validation goes.
        if (data.Length < 16)
            return DocumentFormat.Unknown;

        var formType = Ascii(data, 12, 4);
        return formType is "DJVU" or "DJVM" or "DJVI" or "THUM"
            ? DocumentFormat.Djvu
            : DocumentFormat.Unknown;
    }

    private static DocumentFormat DetectZip(byte[] data)
    {
        // An EPUB is required to store `mimetype` first and uncompressed, so
        // its content sits at a fixed offset in a conforming file and can be
        // read without inflating anything. Files that break that rule still
        // reach EpubService, which reopens them properly and reports it.
        const string marker = "mimetypeapplication/epub+zip";
        if (data.Length >= 30 + marker.Length && Ascii(data, 30, marker.Length) == marker)
            return DocumentFormat.Epub;

        // Otherwise fall back to the entry names, which appear uncompressed in
        // both the local headers and the central directory — enough to tell an
        // EPUB from a DOCX from a plain ZIP without inflating anything. The
        // central directory lives at the end of the file, so this scans the
        // whole buffer rather than a prefix of it.
        if (Contains(data, "META-INF/container.xml"u8))
            return DocumentFormat.Epub;
        if (Contains(data, "word/document.xml"u8))
            return DocumentFormat.Docx;

        return DocumentFormat.Zip;
    }

    private static DocumentFormat DetectOle2(byte[] data)
    {
        // Directory entry names are UTF-16LE inside the compound file, so the
        // stream name appears with interleaved NUL bytes in the raw buffer.
        // Walking the FAT just to classify the file would mean parsing it twice;
        // the marker is unambiguous enough here, and CompoundFile does the real
        // work once a reader has been chosen.
        return Contains(data, "W\0o\0r\0d\0D\0o\0c\0u\0m\0e\0n\0t"u8)
            ? DocumentFormat.Doc
            : DocumentFormat.Ole2Other;
    }

    private static bool Contains(byte[] data, ReadOnlySpan<byte> needle) =>
        data.AsSpan().IndexOf(needle) >= 0;

    // ── Byte helpers ─────────────────────────────────────────────────────

    private static bool StartsWith(byte[] data, string prefix)
    {
        if (data.Length < prefix.Length)
            return false;

        for (var i = 0; i < prefix.Length; i++)
            if (data[i] != prefix[i])
                return false;

        return true;
    }

    private static string Ascii(byte[] data, int offset, int length) =>
        offset + length > data.Length ? "" : Encoding.ASCII.GetString(data, offset, length);
}
