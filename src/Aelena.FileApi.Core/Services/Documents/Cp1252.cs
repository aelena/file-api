using System.Text;

namespace Aelena.FileApi.Core.Services.Documents;

/// <summary>
/// Windows-1252 decoding, which both legacy Word documents and pre-Unicode
/// MOBI files are written in.
/// <para>
/// .NET Core ships only a handful of encodings; CP1252 needs the
/// <c>System.Text.Encoding.CodePages</c> package and a provider registration.
/// The encoding is Latin-1 except for the 0x80–0x9F range, so the whole of it
/// is the 32-entry table below — cheaper than a dependency, and it cannot be
/// left unregistered by a consumer that only references this library.
/// </para>
/// </summary>
internal static class Cp1252
{
    /// <summary>Characters CP1252 places in the C1 control range that Latin-1 leaves undefined.</summary>
    private static ReadOnlySpan<char> HighRange =>
    [
        '€', '�', '‚', 'ƒ', '„', '…', '†', '‡',
        'ˆ', '‰', 'Š', '‹', 'Œ', '�', 'Ž', '�',
        '�', '‘', '’', '“', '”', '•', '–', '—',
        '˜', '™', 'š', '›', 'œ', '�', 'ž', 'Ÿ'
    ];

    /// <summary>Decode a CP1252 byte range to a string.</summary>
    public static string GetString(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);

        foreach (var b in bytes)
            sb.Append(b is >= 0x80 and <= 0x9F ? HighRange[b - 0x80] : (char)b);

        return sb.ToString();
    }

    /// <summary>
    /// Decode with the encoding a MOBI or DOC file declares, falling back to
    /// CP1252 when the declared code page is one the runtime does not carry.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes, int codePage) => codePage switch
    {
        65001 => Encoding.UTF8.GetString(bytes),
        1200 => Encoding.Unicode.GetString(bytes),
        _ => GetString(bytes)
    };
}
