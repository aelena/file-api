using System.Text;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Models;

namespace Aelena.FileApi.Core.Services.Common;

/// <summary>
/// Tells you what a text file is really encoded as, what line endings it uses,
/// and what is in it that should not be — then rewrites it cleanly on request.
/// <para>
/// The control-byte report is the part that earns its keep. A single stray
/// <c>0x08</c> or <c>0x1B</c> in an otherwise valid UTF-8 file passes every
/// encoding check, renders as nothing in most editors, and is rejected by
/// whatever consumes the file later — at which point the only clue is a message
/// about the file "not being plain text". This says which byte, at which line
/// and column.
/// </para>
/// </summary>
public static class TextEncodingService
{
    /// <summary>Beyond this, a file is not something to be reading as text in one buffer.</summary>
    private const int MaxBytes = 256 * 1024 * 1024;

    /// <summary>Only ever report this many distinct control bytes; the rest are counted.</summary>
    private const int MaxReportedControls = 32;

    /// <summary>Bytes below 0x20 that belong in text.</summary>
    private static readonly byte[] Allowed = [0x09, 0x0A, 0x0D];

    // ── Detection ────────────────────────────────────────────────────────

    /// <summary>Report a file's encoding, line endings and control bytes.</summary>
    /// <exception cref="FileApiException">413 when the file is above the size limit.</exception>
    public static TextEncodingResponse Detect(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length > MaxBytes)
        {
            throw new FileApiException(413,
                $"File is {data.Length:N0} bytes, above the {MaxBytes:N0} byte limit for text analysis.",
                title: "Payload Too Large");
        }

        var (bomName, bomLength, bomEncoding) = ReadBom(data);
        var body = data.AsSpan(bomLength);

        var utf8Error = ValidateUtf8(body);
        var encoding = bomEncoding ?? (utf8Error is null ? GuessWithoutBom(body) : "unknown");
        var confidence = bomEncoding is not null ? "certain"
            : utf8Error is null ? (IsAscii(body) ? "certain" : "high")
            : "low";

        var (crlf, lf, cr) = CountLineEndings(body);
        var controls = FindControlBytes(body, bomLength);

        var text = utf8Error is null ? Encoding.UTF8.GetString(body) : null;

        return new TextEncodingResponse(
            FileName: fileName,
            FileSizeBytes: data.Length,
            Encoding: encoding,
            Confidence: confidence,
            HasBom: bomName is not null,
            Bom: bomName,
            IsValidUtf8: utf8Error is null,
            Utf8Error: utf8Error,
            LineEnding: DescribeLineEnding(crlf, lf, cr),
            CrlfCount: crlf,
            LfCount: lf,
            CrCount: cr,
            LineCount: crlf + lf + cr + (body.Length > 0 && !EndsWithNewline(body) ? 1 : 0),
            EndsWithNewline: body.Length > 0 && EndsWithNewline(body),
            HasControlBytes: controls.Count > 0,
            ControlBytes: controls,
            HasReplacementChar: text?.Contains('\uFFFD') ?? false);
    }

    // ── Normalisation ────────────────────────────────────────────────────

    /// <summary>
    /// Rewrite a file as UTF-8 with consistent line endings, optionally
    /// stripping the BOM and any control bytes.
    /// </summary>
    /// <param name="data">Raw file bytes.</param>
    /// <param name="fileName">Original file name, returned unchanged.</param>
    /// <param name="lineEnding">"lf", "crlf", "cr", or "keep".</param>
    /// <param name="stripBom">Drop a leading byte-order mark.</param>
    /// <param name="stripControls">Remove control bytes other than tab and newline.</param>
    /// <exception cref="FileApiException">
    /// 400 for an unknown line-ending name, 422 when the input is not decodable text.
    /// </exception>
    public static (string FileName, byte[] Data) Normalise(
        byte[] data, string fileName,
        string lineEnding = "lf", bool stripBom = true, bool stripControls = true)
    {
        ArgumentNullException.ThrowIfNull(data);

        var newline = lineEnding.ToLowerInvariant() switch
        {
            "lf" or "unix" => "\n",
            "crlf" or "windows" => "\r\n",
            "cr" or "mac" => "\r",
            "keep" => null,
            _ => throw new FileApiException(400,
                $"Unknown line ending '{lineEnding}'. Use lf, crlf, cr or keep.")
        };

        var (bomName, bomLength, _) = ReadBom(data);
        var body = data.AsSpan(bomLength);

        if (ValidateUtf8(body) is { } error)
        {
            throw new FileApiException(422,
                $"This file is not valid UTF-8, so it cannot be normalised without knowing its " +
                $"real encoding: {error}. Run /txt/detect-encoding on it first.",
                title: "Undecodable Text");
        }

        var text = Encoding.UTF8.GetString(body);

        if (newline is not null)
        {
            // Collapse to LF first so CRLF is not turned into CRCRLF.
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                       .Replace("\r", "\n", StringComparison.Ordinal);
            if (newline != "\n")
                text = text.Replace("\n", newline, StringComparison.Ordinal);
        }

        if (stripControls)
            text = StripControls(text);

        var output = new List<byte>(text.Length + 3);
        if (!stripBom && bomName is not null)
            output.AddRange(Encoding.UTF8.GetPreamble());
        output.AddRange(Encoding.UTF8.GetBytes(text));

        return (fileName, output.ToArray());
    }

    // ── Byte-order marks ─────────────────────────────────────────────────

    private static (string? Name, int Length, string? Encoding) ReadBom(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return ("UTF-8", 3, "utf-8");

        // UTF-32 must be tested before UTF-16: the little-endian UTF-32 mark
        // opens with the little-endian UTF-16 one.
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xFE && data[2] == 0x00 && data[3] == 0x00)
            return ("UTF-32 LE", 4, "utf-32le");
        if (data.Length >= 4 && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0xFE && data[3] == 0xFF)
            return ("UTF-32 BE", 4, "utf-32be");

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return ("UTF-16 LE", 2, "utf-16le");
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return ("UTF-16 BE", 2, "utf-16be");

        return (null, 0, null);
    }

    // ── Encoding ─────────────────────────────────────────────────────────

    /// <summary>
    /// Validate UTF-8 by hand rather than by catching a decoder exception, so
    /// the answer can name the offending byte and its offset.
    /// </summary>
    private static string? ValidateUtf8(ReadOnlySpan<byte> data)
    {
        var i = 0;
        while (i < data.Length)
        {
            var b = data[i];
            int extra;

            if (b < 0x80) { i++; continue; }
            else if (b is >= 0xC2 and <= 0xDF) extra = 1;
            else if (b is >= 0xE0 and <= 0xEF) extra = 2;
            else if (b is >= 0xF0 and <= 0xF4) extra = 3;
            else return $"byte 0x{b:X2} at offset {i} cannot start a UTF-8 sequence";

            if (i + extra >= data.Length)
                return $"truncated {extra + 1}-byte sequence starting at offset {i}";

            for (var n = 1; n <= extra; n++)
            {
                if ((data[i + n] & 0xC0) != 0x80)
                {
                    return $"byte 0x{data[i + n]:X2} at offset {i + n} is not a continuation byte";
                }
            }

            i += extra + 1;
        }

        return null;
    }

    private static bool IsAscii(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            if (b > 0x7F) return false;

        return true;
    }

    /// <summary>
    /// With no BOM and valid UTF-8, the only question left is whether anything
    /// outside ASCII is present. Anything else would be a guess dressed up as a
    /// result, so it is reported as UTF-8 with the confidence saying how sure.
    /// </summary>
    private static string GuessWithoutBom(ReadOnlySpan<byte> data) =>
        IsAscii(data) ? "us-ascii" : "utf-8";

    // ── Lines ────────────────────────────────────────────────────────────

    private static (int Crlf, int Lf, int Cr) CountLineEndings(ReadOnlySpan<byte> data)
    {
        int crlf = 0, lf = 0, cr = 0;

        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == 0x0D)
            {
                if (i + 1 < data.Length && data[i + 1] == 0x0A) { crlf++; i++; }
                else cr++;
            }
            else if (data[i] == 0x0A)
            {
                lf++;
            }
        }

        return (crlf, lf, cr);
    }

    private static bool EndsWithNewline(ReadOnlySpan<byte> data) =>
        data.Length > 0 && (data[^1] == 0x0A || data[^1] == 0x0D);

    private static string DescribeLineEnding(int crlf, int lf, int cr)
    {
        var kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        if (kinds == 0) return "none";
        if (kinds > 1) return "mixed";
        return crlf > 0 ? "crlf" : lf > 0 ? "lf" : "cr";
    }

    // ── Control bytes ────────────────────────────────────────────────────

    private static List<ControlByteHit> FindControlBytes(ReadOnlySpan<byte> data, int baseOffset)
    {
        var seen = new Dictionary<byte, (int Offset, int Line, int Column, int Count)>();
        var line = 1;
        var column = 1;

        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];

            if (b == 0x0A) { line++; column = 1; continue; }
            if (b == 0x0D) { continue; }

            var isControl = (b < 0x20 && Array.IndexOf(Allowed, b) < 0) || b == 0x7F;
            if (isControl)
            {
                if (seen.TryGetValue(b, out var prior))
                    seen[b] = prior with { Count = prior.Count + 1 };
                else if (seen.Count < MaxReportedControls)
                    seen[b] = (baseOffset + i, line, column, 1);
            }

            column++;
        }

        return [.. seen
            .OrderBy(kv => kv.Value.Offset)
            .Select(kv => new ControlByteHit(
                Byte: $"0x{kv.Key:X2}",
                Name: ControlName(kv.Key),
                Offset: kv.Value.Offset,
                Line: kv.Value.Line,
                Column: kv.Value.Column,
                Occurrences: kv.Value.Count))];
    }

    private static string StripControls(string text)
    {
        var sb = new StringBuilder(text.Length);

        foreach (var ch in text)
        {
            if (ch is '\n' or '\r' or '\t' || !char.IsControl(ch))
                sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>The ASCII control names, so a report says NUL and ESC rather than 0x00 and 0x1B.</summary>
    private static string ControlName(byte b) => b switch
    {
        0x00 => "NUL",
        0x01 => "SOH",
        0x02 => "STX",
        0x03 => "ETX",
        0x04 => "EOT",
        0x05 => "ENQ",
        0x06 => "ACK",
        0x07 => "BEL",
        0x08 => "BS",
        0x0B => "VT",
        0x0C => "FF",
        0x0E => "SO",
        0x0F => "SI",
        0x10 => "DLE",
        0x11 => "DC1",
        0x12 => "DC2",
        0x13 => "DC3",
        0x14 => "DC4",
        0x15 => "NAK",
        0x16 => "SYN",
        0x17 => "ETB",
        0x18 => "CAN",
        0x19 => "EM",
        0x1A => "SUB",
        0x1B => "ESC",
        0x1C => "FS",
        0x1D => "GS",
        0x1E => "RS",
        0x1F => "US",
        0x7F => "DEL",
        _ => "control"
    };
}
