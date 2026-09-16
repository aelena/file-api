namespace Aelena.FileApi.Core.Services.Documents;

/// <summary>
/// PalmDOC LZ77 decompression — compression type 2 in a Palm database, and what
/// all but the most recent MOBI files use.
/// <para>
/// The scheme is a byte-oriented LZ77 with four instruction classes, chosen by
/// the value of the lead byte:
/// </para>
/// <list type="bullet">
/// <item><c>0x00</c> — a literal NUL.</item>
/// <item><c>0x01..0x08</c> — that many literal bytes follow.</item>
/// <item><c>0x09..0x7F</c> — the byte is itself a literal (printable ASCII).</item>
/// <item><c>0x80..0xBF</c> — a two-byte back-reference: 11 bits of distance, 3 bits of length.</item>
/// <item><c>0xC0..0xFF</c> — a space followed by the byte with bit 7 cleared.</item>
/// </list>
/// </summary>
internal static class PalmDoc
{
    /// <summary>Decompress one record, appending to <paramref name="output"/>.</summary>
    /// <remarks>
    /// Back-references reach into bytes this call has already produced <em>and</em>
    /// into earlier records, which is why the caller passes a single accumulating
    /// buffer rather than decompressing each record in isolation. A reference
    /// that points before the start of the buffer is a corrupt stream; it is
    /// skipped rather than thrown on, so one damaged record costs the text it
    /// held and not the whole book.
    /// </remarks>
    public static void Decompress(ReadOnlySpan<byte> input, List<byte> output)
    {
        var i = 0;

        while (i < input.Length)
        {
            var c = input[i++];

            switch (c)
            {
                case 0:
                    output.Add(0);
                    break;

                case >= 0x01 and <= 0x08:
                    {
                        var count = Math.Min(c, input.Length - i);
                        for (var n = 0; n < count; n++)
                            output.Add(input[i + n]);
                        i += count;
                        break;
                    }

                case <= 0x7F:
                    output.Add(c);
                    break;

                case <= 0xBF:
                    {
                        if (i >= input.Length)
                            return;

                        var pair = (c << 8) | input[i++];
                        var distance = (pair >> 3) & 0x07FF;
                        var length = (pair & 0x07) + 3;

                        if (distance == 0 || distance > output.Count)
                            break;

                        // Copied one byte at a time on purpose: the source range
                        // is allowed to overlap the destination, which is how a
                        // short run encodes a long repetition.
                        var start = output.Count - distance;
                        for (var n = 0; n < length; n++)
                            output.Add(output[start + n]);

                        break;
                    }

                default:
                    output.Add((byte)' ');
                    output.Add((byte)(c ^ 0x80));
                    break;
            }
        }
    }
}
