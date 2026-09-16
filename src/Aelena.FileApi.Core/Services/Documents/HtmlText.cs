using System.Net;
using System.Text;

namespace Aelena.FileApi.Core.Services.Documents;

/// <summary>
/// Converts the HTML that EPUB and MOBI books are made of into Markdown or
/// plain text.
/// <para>
/// This is a forgiving scanner rather than an XML parser, and deliberately so.
/// EPUB content is nominally XHTML but real books are full of undeclared
/// entities and stray tags, and MOBI content is not XML at all — it is HTML 3.2
/// with Mobipocket's own <c>&lt;mbp:pagebreak&gt;</c> markers. Anything strict
/// fails on a large fraction of real files, so unknown tags are dropped and
/// their content kept.
/// </para>
/// </summary>
public static class HtmlText
{
    /// <summary>Guard against a pathological document producing unbounded output.</summary>
    private const int MaxOutputChars = 64 * 1024 * 1024;

    /// <summary>Convert HTML to Markdown, preserving headings, lists, emphasis, links and code.</summary>
    public static string ToMarkdown(string html) => Convert(html, markdown: true);

    /// <summary>Convert HTML to plain text, keeping the block structure but dropping all markup.</summary>
    public static string ToPlainText(string html) => Convert(html, markdown: false);

    /// <summary>
    /// The contents of the first <c>&lt;title&gt;</c> element, or null. MOBI
    /// records carry no metadata of their own, so this is often the only title
    /// a section has.
    /// </summary>
    public static string? Title(string html)
    {
        var open = html.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return null;

        var gt = html.IndexOf('>', open);
        if (gt < 0) return null;

        var close = html.IndexOf("</title", gt, StringComparison.OrdinalIgnoreCase);
        if (close < 0) return null;

        var title = WebUtility.HtmlDecode(html[(gt + 1)..close]).Trim();
        return title.Length == 0 ? null : title;
    }

    // ── Conversion ───────────────────────────────────────────────────────

    private static string Convert(string html, bool markdown)
    {
        ArgumentNullException.ThrowIfNull(html);

        var output = new StringBuilder();
        var block = new StringBuilder();
        var listStack = new List<ListLevel>();
        var quoteDepth = 0;
        var preDepth = 0;
        var skipDepth = 0;
        string? skipTag = null;
        string? blockPrefix = null;
        var anchorStart = -1;
        string? anchorHref = null;
        var pendingTableRow = new List<string>();
        var inTableRow = false;
        var tableHeaderPending = false;

        void FlushBlock()
        {
            var text = block.ToString();
            block.Clear();

            if (preDepth == 0)
                text = text.Trim();

            if (text.Length == 0)
            {
                blockPrefix = null;
                return;
            }

            var prefix = blockPrefix ?? "";
            if (quoteDepth > 0)
                prefix = string.Concat(Enumerable.Repeat("> ", quoteDepth)) + prefix;

            if (output.Length > 0)
                output.Append("\n\n");
            output.Append(prefix).Append(text);

            blockPrefix = null;
        }

        void AppendText(string text)
        {
            if (output.Length + block.Length > MaxOutputChars)
                return;

            if (preDepth > 0)
            {
                block.Append(text);
                return;
            }

            // Collapse inter-word whitespace. HTML sources wrap lines wherever
            // they like, so keeping the source line breaks would leave the
            // Markdown ragged for no gain.
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (block.Length > 0 && block[^1] != ' ' && block[^1] != '\n')
                        block.Append(' ');
                }
                else
                {
                    block.Append(ch);
                }
            }
        }

        var i = 0;
        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                AppendText(WebUtility.HtmlDecode(html[i..]));
                break;
            }

            if (lt > i && skipDepth == 0)
                AppendText(WebUtility.HtmlDecode(html[i..lt]));

            var gt = FindTagEnd(html, lt);
            if (gt < 0)
            {
                // An unterminated '<' is literal text, not a tag.
                if (skipDepth == 0)
                    AppendText(WebUtility.HtmlDecode(html[lt..]));
                break;
            }

            var tag = ParseTag(html.AsSpan(lt + 1, gt - lt - 1));
            i = gt + 1;

            // <script>, <style> and <head> bodies are markup, not content.
            if (skipDepth > 0)
            {
                if (tag.Name == skipTag)
                    skipDepth += tag.IsClosing ? -1 : 1;
                if (skipDepth == 0)
                    skipTag = null;
                continue;
            }

            switch (tag.Name)
            {
                case "script" or "style" or "head" when !tag.IsClosing && !tag.IsSelfClosing:
                    skipTag = tag.Name;
                    skipDepth = 1;
                    continue;

                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    FlushBlock();
                    if (!tag.IsClosing && markdown)
                        blockPrefix = new string('#', tag.Name[1] - '0') + " ";
                    break;

                case "p" or "div" or "section" or "article" or "figure" or "figcaption"
                   or "header" or "footer" or "dd" or "dt" or "dl" or "center" or "body":
                    FlushBlock();
                    break;

                case "br":
                    block.Append('\n');
                    break;

                case "hr":
                    FlushBlock();
                    if (markdown)
                    {
                        if (output.Length > 0) output.Append("\n\n");
                        output.Append("---");
                    }
                    break;

                case "ul" or "ol":
                    FlushBlock();
                    if (tag.IsClosing)
                    {
                        if (listStack.Count > 0)
                            listStack.RemoveAt(listStack.Count - 1);
                    }
                    else if (listStack.Count < 16)
                    {
                        listStack.Add(new ListLevel(Ordered: tag.Name == "ol", Counter: 0));
                    }
                    break;

                case "li":
                    FlushBlock();
                    if (!tag.IsClosing)
                    {
                        var depth = Math.Max(0, listStack.Count - 1);
                        var indent = new string(' ', depth * 2);

                        if (listStack.Count == 0)
                        {
                            blockPrefix = markdown ? "- " : "";
                        }
                        else
                        {
                            var level = listStack[^1];
                            listStack[^1] = level with { Counter = level.Counter + 1 };
                            blockPrefix = markdown
                                ? indent + (level.Ordered ? $"{level.Counter + 1}. " : "- ")
                                : indent;
                        }
                    }
                    break;

                case "blockquote":
                    FlushBlock();
                    if (markdown)
                        quoteDepth = tag.IsClosing ? Math.Max(0, quoteDepth - 1) : Math.Min(8, quoteDepth + 1);
                    break;

                case "pre":
                    FlushBlock();
                    if (tag.IsClosing)
                    {
                        preDepth = Math.Max(0, preDepth - 1);
                        if (markdown)
                        {
                            var code = block.ToString().Trim('\n');
                            block.Clear();
                            if (output.Length > 0) output.Append("\n\n");
                            output.Append("```\n").Append(code).Append("\n```");
                        }
                    }
                    else
                    {
                        preDepth++;
                    }
                    break;

                case "code" or "tt" or "kbd" or "samp" when preDepth == 0 && markdown:
                    block.Append('`');
                    break;

                case "strong" or "b" when markdown:
                    block.Append("**");
                    break;

                case "em" or "i" or "cite" or "dfn" when markdown:
                    block.Append('*');
                    break;

                case "img" when markdown:
                    {
                        var alt = tag.Attribute("alt") ?? "";
                        var src = tag.Attribute("src");
                        if (src is not null)
                            block.Append("![").Append(Escape(alt)).Append("](").Append(src).Append(')');
                        else if (alt.Length > 0)
                            AppendText(alt);
                        break;
                    }

                case "a" when markdown:
                    if (tag.IsClosing)
                    {
                        // Only emit a link if the anchor had visible text and a
                        // destination that is not a bare in-document fragment.
                        if (anchorStart >= 0 && anchorHref is not null && block.Length > anchorStart)
                        {
                            block.Insert(anchorStart, '[');
                            block.Append("](").Append(anchorHref).Append(')');
                        }
                        anchorStart = -1;
                        anchorHref = null;
                    }
                    else
                    {
                        var href = tag.Attribute("href");
                        anchorHref = href is null || href.StartsWith('#') ? null : href;
                        anchorStart = anchorHref is null ? -1 : block.Length;
                    }
                    break;

                case "table":
                    FlushBlock();
                    inTableRow = false;
                    pendingTableRow.Clear();
                    break;

                case "tr":
                    if (tag.IsClosing)
                    {
                        FlushCell(block, pendingTableRow);
                        if (pendingTableRow.Count > 0 && markdown)
                        {
                            if (output.Length > 0) output.Append(tableHeaderPending ? "\n\n" : "\n");
                            output.Append("| ").Append(string.Join(" | ", pendingTableRow)).Append(" |");

                            if (tableHeaderPending)
                            {
                                output.Append("\n|").Append(string.Concat(
                                    Enumerable.Repeat(" --- |", pendingTableRow.Count)));
                                tableHeaderPending = false;
                            }
                        }
                        else if (pendingTableRow.Count > 0)
                        {
                            if (output.Length > 0) output.Append('\n');
                            output.Append(string.Join("\t", pendingTableRow));
                        }
                        pendingTableRow.Clear();
                        inTableRow = false;
                    }
                    else
                    {
                        FlushBlock();
                        inTableRow = true;
                        pendingTableRow.Clear();
                        tableHeaderPending = false;
                    }
                    break;

                case "th" or "td":
                    if (inTableRow)
                    {
                        if (tag.IsClosing)
                            FlushCell(block, pendingTableRow);
                        else if (tag.Name == "th")
                            tableHeaderPending = markdown;
                    }
                    break;
            }
        }

        FlushBlock();
        return output.ToString().Trim();
    }

    private static void FlushCell(StringBuilder block, List<string> row)
    {
        var cell = block.ToString().Trim().Replace("|", @"\|", StringComparison.Ordinal);
        block.Clear();
        if (cell.Length > 0 || row.Count > 0)
            row.Add(cell);
    }

    private static string Escape(string text) =>
        text.Replace("]", @"\]", StringComparison.Ordinal);

    /// <summary>
    /// Locate the '&gt;' that closes a tag, skipping over any that appear inside
    /// a quoted attribute value — <c>&lt;a title="a &gt; b"&gt;</c> is one tag,
    /// not two.
    /// </summary>
    private static int FindTagEnd(string html, int start)
    {
        var quote = '\0';
        for (var i = start + 1; i < html.Length; i++)
        {
            var ch = html[i];
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (ch == '>')
            {
                return i;
            }
        }
        return -1;
    }

    private static Tag ParseTag(ReadOnlySpan<char> inner)
    {
        var isClosing = inner.Length > 0 && inner[0] == '/';
        if (isClosing) inner = inner[1..];

        var isSelfClosing = inner.Length > 0 && inner[^1] == '/';
        if (isSelfClosing) inner = inner[..^1];

        var nameEnd = 0;
        while (nameEnd < inner.Length && !char.IsWhiteSpace(inner[nameEnd]))
            nameEnd++;

        var name = inner[..nameEnd].ToString().ToLowerInvariant();

        // Namespaced tags (mbp:pagebreak, epub:switch) match on the local name.
        var colon = name.IndexOf(':');
        if (colon >= 0) name = name[(colon + 1)..];

        return new Tag(name, isClosing, isSelfClosing, inner[nameEnd..].ToString());
    }

    private readonly record struct ListLevel(bool Ordered, int Counter);

    private readonly record struct Tag(string Name, bool IsClosing, bool IsSelfClosing, string Attributes)
    {
        /// <summary>Read one attribute value, tolerating single, double and unquoted forms.</summary>
        public string? Attribute(string name)
        {
            var span = Attributes.AsSpan();
            var i = 0;

            while (i < span.Length)
            {
                while (i < span.Length && (char.IsWhiteSpace(span[i]) || span[i] == '/')) i++;

                var keyStart = i;
                while (i < span.Length && span[i] != '=' && !char.IsWhiteSpace(span[i])) i++;
                if (i == keyStart) break;

                var key = span[keyStart..i];
                while (i < span.Length && char.IsWhiteSpace(span[i])) i++;

                if (i >= span.Length || span[i] != '=')
                    continue;

                i++;
                while (i < span.Length && char.IsWhiteSpace(span[i])) i++;
                if (i >= span.Length) break;

                ReadOnlySpan<char> value;
                if (span[i] is '"' or '\'')
                {
                    var quote = span[i++];
                    var end = span[i..].IndexOf(quote);
                    if (end < 0) break;
                    value = span.Slice(i, end);
                    i += end + 1;
                }
                else
                {
                    var start = i;
                    while (i < span.Length && !char.IsWhiteSpace(span[i])) i++;
                    value = span[start..i];
                }

                if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return WebUtility.HtmlDecode(value.ToString());
            }

            return null;
        }
    }
}
