using System.Buffers;
using System.Globalization;
using System.Text;

namespace Aelena.FileApi.Core.Services.Pdf;

/// <summary>
/// Reduces arbitrary text to what iText's built-in fonts can actually draw.
/// <para>
/// The standard PDF fonts are single-byte and WinAnsi-encoded: they have no
/// glyph for anything outside Windows-1252. Handing them a character they do
/// not carry produces a blank in the output, silently — the PDF renders, and
/// the text is simply missing. Transliterating first means the loss is visible
/// and as small as it can be made without embedding a font.
/// </para>
/// </summary>
internal static class WinAnsi
{
    /// <summary>The 27 characters CP1252 defines in the 0x80–0x9F range.</summary>
    private static readonly SearchValues<char> HighRange = SearchValues.Create(
        "€‚ƒ„…†‡ˆ‰Š‹ŒŽ" +
        "‘’“”•–—˜™š›œžŸ");

    /// <summary>
    /// Characters with no CP1252 equivalent that still have an obvious ASCII
    /// reading. Nothing CP1252 already covers belongs here — the ellipsis, the
    /// en and em dashes and the guillemets are all in the ranges above.
    /// </summary>
    private static readonly Dictionary<char, string> Transliterations = new()
    {
        // Dashes and quotation marks CP1252 does not carry.
        ['‐'] = "-",
        ['‑'] = "-",
        ['‒'] = "-",
        ['―'] = "--",
        ['′'] = "'",
        ['″'] = "\"",
        ['‵'] = "'",
        ['⁄'] = "/",
        ['−'] = "-",

        // Mathematical and arrow symbols with a conventional ASCII spelling.
        ['≈'] = "~",
        ['≠'] = "!=",
        ['≤'] = "<=",
        ['≥'] = ">=",
        ['←'] = "<-",
        ['→'] = "->",
        ['↔'] = "<->",

        // Exotic spaces become ordinary ones; zero-width marks become nothing.
        [' '] = " ",
        [' '] = " ",
        [' '] = " ",
        [' '] = " ",
        ['​'] = "",
        ['‌'] = "",
        ['‍'] = "",
        ['﻿'] = "",

        // List bullets other than the one CP1252 has.
        ['▪'] = "-",
        ['○'] = "-",
        ['●'] = "-"
    };

    /// <summary>Rewrite a string so every character in it has a WinAnsi glyph.</summary>
    public static string Sanitise(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        // The common case is text that is already representable, and walking it
        // twice is cheaper than building a StringBuilder for every run.
        if (IsRepresentable(text))
            return text;

        var sb = new StringBuilder(text.Length);

        foreach (var ch in text)
        {
            if (IsRepresentable(ch))
            {
                sb.Append(ch);
                continue;
            }

            if (Transliterations.TryGetValue(ch, out var replacement))
            {
                sb.Append(replacement);
                continue;
            }

            // A precomposed letter decomposes into a base letter plus combining
            // marks; keeping the base loses the accent but not the word.
            var stripped = StripDiacritics(ch);
            sb.Append(stripped.Length > 0 ? stripped : "?");
        }

        return sb.ToString();
    }

    private static bool IsRepresentable(string text)
    {
        foreach (var ch in text)
            if (!IsRepresentable(ch))
                return false;

        return true;
    }

    private static bool IsRepresentable(char ch) =>
        ch is '\n' or '\t'
        || ch is >= ' ' and <= '~'
        || ch is >= ' ' and <= 'ÿ'
        || HighRange.Contains(ch);

    private static string StripDiacritics(char ch)
    {
        var decomposed = ch.ToString().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (var part in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(part) == UnicodeCategory.NonSpacingMark)
                continue;
            if (IsRepresentable(part))
                sb.Append(part);
        }

        return sb.ToString();
    }
}
