using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JiraTaskBar.Core.Markup;

/// <summary>
/// Best-effort Jira wiki markup to HTML conversion for the comment preview.
/// Jira renders the real thing; this covers the common syntax so the preview is close.
/// </summary>
public static partial class WikiPreview
{
    /// <param name="resolveImage">Maps an image filename to a URL the preview can load, or null if unknown.</param>
    public static string ToHtml(string markup, Func<string, string?>? resolveImage = null)
    {
        resolveImage ??= _ => null;
        var lines = (markup ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        var html = new StringBuilder();
        var listStack = new List<char>(); // '*' => ul, '#' => ol
        var paragraph = new List<string>();
        var inTable = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            html.Append("<p>").Append(string.Join("<br>", paragraph.Select(l => Inline(l, resolveImage)))).Append("</p>\n");
            paragraph.Clear();
        }

        void CloseLists(int depth = 0)
        {
            while (listStack.Count > depth)
            {
                html.Append(listStack[^1] == '#' ? "</li></ol>\n" : "</li></ul>\n");
                listStack.RemoveAt(listStack.Count - 1);
            }
        }

        void CloseTable()
        {
            if (!inTable) return;
            html.Append("</table>\n");
            inTable = false;
        }

        void CloseBlocks()
        {
            FlushParagraph();
            CloseLists();
            CloseTable();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            // {code}, {code:lang}, {noformat} blocks: raw content.
            var codeOpen = CodeOpenRegex().Match(trimmed);
            if (codeOpen.Success)
            {
                CloseBlocks();
                var tag = codeOpen.Groups["tag"].Value;
                var rest = trimmed[codeOpen.Length..];
                var body = new List<string>();
                var closeToken = "{" + tag + "}";
                var closeIndex = rest.IndexOf(closeToken, StringComparison.OrdinalIgnoreCase);
                if (closeIndex >= 0)
                {
                    body.Add(rest[..closeIndex]);
                }
                else
                {
                    if (rest.Length > 0) body.Add(rest);
                    for (i++; i < lines.Length; i++)
                    {
                        var idx = lines[i].IndexOf(closeToken, StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            if (idx > 0) body.Add(lines[i][..idx]);
                            break;
                        }
                        body.Add(lines[i]);
                    }
                }
                html.Append("<pre><code>").Append(WebUtility.HtmlEncode(string.Join("\n", body))).Append("</code></pre>\n");
                continue;
            }

            if (trimmed.Equals("{quote}", StringComparison.OrdinalIgnoreCase))
            {
                CloseBlocks();
                var body = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().Equals("{quote}", StringComparison.OrdinalIgnoreCase); i++)
                    body.Add(lines[i]);
                html.Append("<blockquote>").Append(ToHtml(string.Join("\n", body), resolveImage)).Append("</blockquote>\n");
                continue;
            }

            if (trimmed.Length == 0)
            {
                CloseBlocks();
                continue;
            }

            if (trimmed == "----")
            {
                CloseBlocks();
                html.Append("<hr>\n");
                continue;
            }

            var heading = HeadingRegex().Match(trimmed);
            if (heading.Success)
            {
                CloseBlocks();
                var level = heading.Groups[1].Value;
                html.Append($"<h{level}>").Append(Inline(heading.Groups[2].Value, resolveImage)).Append($"</h{level}>\n");
                continue;
            }

            if (trimmed.StartsWith("bq. ", StringComparison.Ordinal))
            {
                CloseBlocks();
                html.Append("<blockquote>").Append(Inline(trimmed[4..], resolveImage)).Append("</blockquote>\n");
                continue;
            }

            var list = ListRegex().Match(trimmed);
            if (list.Success)
            {
                FlushParagraph();
                CloseTable();
                var markers = list.Groups[1].Value;
                // Lists at the same depth but a different type start a new list.
                var common = 0;
                while (common < listStack.Count && common < markers.Length && listStack[common] == markers[common]) common++;
                if (common < listStack.Count)
                {
                    CloseLists(common);
                    if (common == markers.Length) html.Append("</li>");
                }
                else if (listStack.Count == markers.Length && listStack.Count > 0)
                {
                    html.Append("</li>");
                }
                while (listStack.Count < markers.Length)
                {
                    var type = markers[listStack.Count];
                    html.Append(type == '#' ? "<ol>" : "<ul>");
                    listStack.Add(type);
                }
                html.Append("<li>").Append(Inline(list.Groups[2].Value, resolveImage));
                continue;
            }

            if (trimmed.StartsWith('|'))
            {
                FlushParagraph();
                CloseLists();
                if (!inTable)
                {
                    html.Append("<table>\n");
                    inTable = true;
                }
                html.Append("<tr>");
                foreach (var (text, isHeader) in TableCells(trimmed))
                {
                    var tag = isHeader ? "th" : "td";
                    html.Append($"<{tag}>").Append(Inline(text.Trim(), resolveImage)).Append($"</{tag}>");
                }
                html.Append("</tr>\n");
                continue;
            }

            CloseLists();
            CloseTable();
            paragraph.Add(trimmed);
        }

        CloseBlocks();
        return html.ToString();
    }

    /// <summary>Wraps the fragment in a styled HTML page for the WebView preview.</summary>
    /// <param name="imageOrigin">The only origin images may load from (e.g. https://attachments.local); null blocks all images.</param>
    public static string ToDocument(string markup, Func<string, string?>? resolveImage = null, bool dark = false, string? imageOrigin = null)
    {
        // Defence in depth on top of encoding: no scripts, frames, fetches or remote resources at all.
        var imgSrc = imageOrigin is null ? "'none'" : WebUtility.HtmlEncode(imageOrigin);
        var csp = $"default-src 'none'; style-src 'unsafe-inline'; img-src {imgSrc}; form-action 'none'; base-uri 'none'";
        var fg = dark ? "#e6e6e6" : "#172b4d";
        var bg = dark ? "#202020" : "#ffffff";
        var border = dark ? "#444" : "#c1c7d0";
        var codeBg = dark ? "#2b2b2b" : "#f4f5f7";
        var link = dark ? "#579dff" : "#0c66e4";
        return $$"""
            <!doctype html><html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{{csp}}">
            <style>
            body{font-family:"Segoe UI",system-ui,sans-serif;font-size:14px;line-height:1.5;color:{{fg}};background:{{bg}};margin:12px;}
            a{color:{{link}}} h1,h2,h3,h4,h5,h6{margin:.8em 0 .3em;font-weight:600}
            table{border-collapse:collapse;margin:8px 0} th,td{border:1px solid {{border}};padding:4px 8px;text-align:left;vertical-align:top}
            th{background:{{codeBg}}} pre{background:{{codeBg}};padding:8px;border-radius:4px;overflow:auto}
            code{font-family:Consolas,monospace;background:{{codeBg}};padding:0 3px;border-radius:3px} pre code{padding:0}
            blockquote{border-left:3px solid {{border}};margin:8px 0;padding-left:10px;opacity:.85}
            img{max-width:100%;border:1px solid {{border}};border-radius:3px} img.thumb{max-width:240px}
            .missing{display:inline-block;border:1px dashed {{border}};padding:2px 6px;border-radius:3px;opacity:.7}
            </style></head><body>{{ToHtml(markup, resolveImage)}}</body></html>
            """;
    }

    private static IEnumerable<(string Text, bool IsHeader)> TableCells(string row)
    {
        var i = 0;
        while (i < row.Length)
        {
            var isHeader = row.AsSpan(i).StartsWith("||");
            i += isHeader ? 2 : 1;
            if (i >= row.Length) yield break;
            var next = FindCellEnd(row, i);
            yield return (row[i..next], isHeader);
            i = next;
        }
    }

    /// <summary>Finds the next unescaped '|' that is not inside [link|url] or !image|opts!.</summary>
    private static int FindCellEnd(string row, int start)
    {
        var bracket = 0;
        var inImage = false;
        for (var i = start; i < row.Length; i++)
        {
            var c = row[i];
            if (c == '\\') { i++; continue; }
            if (c == '[') bracket++;
            else if (c == ']' && bracket > 0) bracket--;
            else if (c == '!') inImage = !inImage;
            else if (c == '|' && bracket == 0 && !inImage) return i;
        }
        return row.Length;
    }

    private static string Inline(string text, Func<string, string?> resolveImage)
    {
        // Protect tokens whose contents must not be formatted by replacing them with placeholders.
        var tokens = new List<string>();
        string Hold(string html) { tokens.Add(html); return $"\u0001{tokens.Count - 1}\u0002"; }

        text = MonospaceRegex().Replace(text, m => Hold("<code>" + WebUtility.HtmlEncode(m.Groups[1].Value) + "</code>"));
        text = ImageRegex().Replace(text, m =>
        {
            var name = m.Groups["name"].Value.Trim();
            var options = m.Groups["opts"].Value;
            // Remote images are never fetched by the preview: they could be tracking pixels.
            if (name.Contains("://", StringComparison.Ordinal))
                return Hold($"<span class=\"missing\">🖼 remote image (not loaded in preview): {WebUtility.HtmlEncode(name)}</span>");
            var url = resolveImage(name);
            if (url is null)
                return Hold($"<span class=\"missing\">🖼 {WebUtility.HtmlEncode(name)}</span>");
            var cls = options.Contains("thumbnail", StringComparison.OrdinalIgnoreCase) ? " class=\"thumb\"" : string.Empty;
            return Hold($"<img src=\"{WebUtility.HtmlEncode(url)}\" alt=\"{WebUtility.HtmlEncode(name)}\"{cls}>");
        });
        text = LinkRegex().Replace(text, m =>
        {
            var label = m.Groups["label"].Success ? m.Groups["label"].Value : null;
            var target = m.Groups["target"].Value.Trim();
            if (target.StartsWith('^'))
                return Hold($"<span class=\"missing\">📎 {WebUtility.HtmlEncode(target[1..])}</span>");
            if (target.StartsWith('~'))
                return Hold($"<b>@{WebUtility.HtmlEncode(target[1..])}</b>");
            var href = target.Contains("://") || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? target : "#";
            return Hold($"<a href=\"{WebUtility.HtmlEncode(href)}\">{WebUtility.HtmlEncode(label ?? target)}</a>");
        });

        var encoded = WebUtility.HtmlEncode(text);
        encoded = BoldRegex().Replace(encoded, "<b>$1</b>");
        encoded = ItalicRegex().Replace(encoded, "<i>$1</i>");
        encoded = StrikeRegex().Replace(encoded, "<s>$1</s>");
        encoded = UnderlineRegex().Replace(encoded, "<u>$1</u>");
        encoded = SuperRegex().Replace(encoded, "<sup>$1</sup>");
        encoded = SubRegex().Replace(encoded, "<sub>$1</sub>");
        encoded = ColorRegex().Replace(encoded, "<span style=\"color:$1\">$2</span>");
        encoded = encoded.Replace(@"\\", "<br>");

        return PlaceholderRegex().Replace(encoded, m => tokens[int.Parse(m.Groups[1].Value)]);
    }

    [GeneratedRegex(@"^\{(?<tag>code|noformat)(?::[^}]*)?\}", RegexOptions.IgnoreCase)]
    private static partial Regex CodeOpenRegex();

    [GeneratedRegex(@"^h([1-6])\.\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^([*#-]+)\s+(.*)$")]
    private static partial Regex ListRegex();

    [GeneratedRegex(@"\{\{(.+?)\}\}")]
    private static partial Regex MonospaceRegex();

    [GeneratedRegex(@"!(?<name>[^!|\s][^!|]*?)(?:\|(?<opts>[^!]*))?!")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[(?:(?<label>[^\]|]+)\|)?(?<target>[^\]|]+)\]")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"(?<![\w*])\*(?=\S)(.+?)(?<=\S)\*(?![\w*])")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"(?<![\w_])_(?=\S)(.+?)(?<=\S)_(?![\w_])")]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"(?<![\w-])-(?=[^\s-])(.+?)(?<=[^\s-])-(?![\w-])")]
    private static partial Regex StrikeRegex();

    [GeneratedRegex(@"(?<![\w+])\+(?=\S)(.+?)(?<=\S)\+(?![\w+])")]
    private static partial Regex UnderlineRegex();

    [GeneratedRegex(@"(?<![\w^])\^(?=\S)(.+?)(?<=\S)\^(?![\w^])")]
    private static partial Regex SuperRegex();

    [GeneratedRegex(@"(?<![\w~])~(?=\S)(.+?)(?<=\S)~(?![\w~])")]
    private static partial Regex SubRegex();

    [GeneratedRegex(@"\{color:([#\w]+)\}(.*?)\{color\}")]
    private static partial Regex ColorRegex();

    [GeneratedRegex("\u0001(\\d+)\u0002")]
    private static partial Regex PlaceholderRegex();
}
