using System.Collections.Generic;
using System.Text;

namespace MDViewerNative;

// 阅读态 HTML 呈现(S71,docs/mcp-html-reading.md)。只在视图层生效(DocView.Load):
// 导出、规范输出与编辑源码仍用原始 HTML。白名单 HTML 块按“等价 Markdown”显示,
// 白名单外任何标签 → 保持字面面板;纯注释块不显示。HTML 内文字不按 Markdown 解析(同 GitHub)。
internal static class HtmlReading
{
    public static List<Block> Apply(List<Block> blocks)
    {
        List<Block> result = null;
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            List<Block> converted = block is HtmlBlock html ? Convert(html.Text) : null;
            if (converted == null)
            {
                InlineImages(block);
                if (block is HtmlDetails details && details.BodyRuns == null) details.BodyRuns = DetailsBody(details.Body);
                result?.Add(block);
                continue;
            }
            result ??= blocks.GetRange(0, i);
            foreach (var c in converted)
            {
                c.XOff = block.XOff + c.XOff;   // 列表项自带嵌套缩进(S74)
                c.Source = block.Source; c.QuoteDepth = block.QuoteDepth; c.QSeq = block.QSeq; c.Alert = block.Alert;
                result.Add(c);
            }
        }
        return result ?? blocks;
    }

    // S74:<details> 正文含 HTML 时拍平为阅读行(列表编号与缩进、链接保留;标题加粗)。
    // 纯文本正文、或含无法拍平的块(表格、图片、代码、嵌套 details…)时返回 null,保持原文。
    private static List<Run> DetailsBody(string body)
    {
        if (string.IsNullOrEmpty(body) || !HasTag(body)) return null;
        var runs = new List<Run>();
        foreach (var block in Apply(MdParser.Parse(body)))
        {
            List<Run> line;
            string prefix = null;
            switch (block)
            {
                case Para para: line = para.Inlines; break;
                case Heading heading: line = heading.Inlines; foreach (var r in line) r.B = true; break;
                case ListItem item when item.Sub == null:
                    line = item.Inlines;
                    prefix = new string(' ', item.Nest * 4) + (item.Continuation ? "   " : item.Marker + " ");
                    break;
                default: return null;
            }
            if (runs.Count > 0) runs.Add(new Run("\n"));
            if (prefix != null) runs.Add(new Run(prefix));
            foreach (var r in line) { r.Text = r.Text.Replace("\t", "    "); runs.Add(r); }   // 与 BlockText.Vt 同域
        }
        return runs.Count > 0 ? runs : null;
    }

    private static bool HasTag(string s)
    {
        for (int i = s.IndexOf('<'); i >= 0 && i + 1 < s.Length; i = s.IndexOf('<', i + 1))
            if (char.IsAsciiLetter(s[i + 1]) || s[i + 1] == '/') return true;
        return false;
    }

    // 段落里的行内 <img>:解析器留作一个完整标签的 Opaque run(外层 <a> 链接已挂上),换成图片 run。
    private static void InlineImages(Block block)
    {
        switch (block)
        {
            case Para p: InlineImages(p.Inlines); break;
            case Heading h: InlineImages(h.Inlines); break;
            case Quote q: InlineImages(q.Inlines); break;
            case ListItem li:
                InlineImages(li.Inlines);
                if (li.Sub != null) foreach (var sub in li.Sub) InlineImages(sub);
                break;
        }
    }

    private static void InlineImages(List<Run> runs)
    {
        if (runs == null) return;
        for (int i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
            if (!r.Opaque || r.Text.Length < 6 || !r.Text.StartsWith("<img", System.StringComparison.OrdinalIgnoreCase) || r.Text[^1] != '>'
                || r.Text.IndexOf('<', 1) >= 0 || !TagName(r.Text, 1, out string name, out _) || name != "img") continue;
            string src = Attr(r.Text, "src");
            if (string.IsNullOrWhiteSpace(src)) continue;
            runs[i] = new Run(Attr(r.Text, "alt") ?? "", r.B, r.I, r.S, r.Code, src.Trim(), img: true) { LinkOuter = r.Link, U = r.U, Mark = r.Mark, SupSub = r.SupSub };
        }
    }

    private static readonly HashSet<string> Containers = new() { "p", "div", "center", "picture", "figure", "figcaption" };
    private static readonly HashSet<string> Ignored = new() { "source", "wbr" };

    // null = 含白名单外标签(保持字面);空表 = 无可见内容(不显示)。
    internal static List<Block> Convert(string html)
    {
        if (StartsWithTable(html)) return ConvertTable(html);
        var output = new List<Block>();
        var runs = new List<Run>();
        var text = new StringBuilder();
        var links = new List<string>();
        int bold = 0, italic = 0, strike = 0, under = 0, mark = 0, code = 0, sub = 0, sup = 0, heading = 0;
        // S74:HTML 列表层级。项内文字输出为 ListItem;子列表之后同一项的文字为续行项。
        var lists = new List<ListLevel>();
        int groupSeq = 0;

        void FlushText()
        {
            if (text.Length == 0) return;
            string t = Collapse(HtmlEntity.Decode(text.ToString()));
            text.Clear();
            if (t.Length == 0) return;
            runs.Add(new Run(t, bold > 0, italic > 0, strike > 0, code > 0, links.Count > 0 ? links[^1] : null,
                mark: mark > 0, supsub: sup > 0 ? 1 : sub > 0 ? 2 : 0, u: under > 0));
        }

        void FlushBlock()
        {
            FlushText();
            var line = new List<Run>();
            var para = new List<Run>();
            void EndPara()
            {
                Trim(para);
                var level = lists.Count > 0 && lists[^1].InItem ? lists[^1] : null;
                if (para.Count > 0 && heading == 0 && level != null)
                {
                    output.Add(new ListItem { Marker = level.Marker, Inlines = para, Nest = lists.Count - 1, XOff = (lists.Count - 1) * 20, ListGroup = level.Group, Continuation = level.Emitted });
                    level.Emitted = true;
                }
                else if (para.Count > 0) output.Add(heading > 0 ? new Heading { Level = heading, Inlines = para } : new Para { Inlines = para });
                para = new List<Run>();
            }
            void EndLine()
            {
                Trim(line);
                // 独占一行、无外层链接的图片 = Markdown 独立图片 ![alt](src)
                if (heading == 0 && lists.Count == 0 && line.Count == 1 && line[0].Img && line[0].LinkOuter == null)
                {
                    EndPara();
                    output.Add(new Image { Alt = line[0].ImageAlt ?? "", Src = line[0].Link });
                }
                else if (line.Count > 0)
                {
                    if (para.Count > 0) para.Add(new Run(heading > 0 ? " " : "\n"));
                    para.AddRange(line);
                }
                line = new List<Run>();
            }
            foreach (var r in runs)
                if (r.Text == "\n" && !r.Img) EndLine(); else line.Add(r);
            EndLine();
            EndPara();
            runs.Clear();
        }

        int pos = 0, n = html.Length;
        while (pos < n)
        {
            char c = html[pos];
            if (c != '<') { text.Append(c); pos++; continue; }
            if (string.CompareOrdinal(html, pos, "<!--", 0, 4) == 0)
            {
                int end = html.IndexOf("-->", pos + 4, System.StringComparison.Ordinal);
                if (end < 0) return null;
                pos = end + 3;
                continue;
            }
            bool closing = pos + 1 < n && html[pos + 1] == '/';
            if (!TagName(html, pos + (closing ? 2 : 1), out string name, out _)) { text.Append(c); pos++; continue; }
            int gt = TagEnd(html, pos + 1);
            if (gt < 0) return null;
            string tag = html.Substring(pos, gt - pos + 1);
            pos = gt + 1;

            if (Containers.Contains(name)) { FlushBlock(); continue; }
            if (name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6')
            {
                FlushBlock();
                heading = closing ? 0 : name[1] - '0';
                continue;
            }
            if (Ignored.Contains(name)) continue;
            switch (name)
            {
                case "br":
                    FlushText(); runs.Add(new Run("\n")); continue;
                case "ul":
                case "ol":
                    FlushBlock();
                    if (closing) { if (lists.Count > 0) lists.RemoveAt(lists.Count - 1); }
                    else lists.Add(new ListLevel { Ordered = name == "ol", Next = int.TryParse(Attr(tag, "start"), out int start) ? start : 1, Group = --groupSeq });
                    continue;
                case "li":
                    FlushBlock();
                    if (lists.Count == 0) return null;   // 列表外的 li:不猜
                    var top = lists[^1];
                    top.InItem = !closing;
                    if (!closing) { top.Emitted = false; top.Marker = top.Ordered ? top.Next++ + "." : "•"; }
                    continue;
                case "hr":
                    FlushBlock(); output.Add(new HRule()); continue;
                case "img":
                    if (closing) continue;   // 常见的 <img ...></img>
                    FlushText();
                    string src = Attr(tag, "src");
                    if (!string.IsNullOrWhiteSpace(src))
                        runs.Add(new Run(Attr(tag, "alt") ?? "", img: true, link: src.Trim()) { LinkOuter = links.Count > 0 ? links[^1] : null });
                    continue;
                case "a":
                    FlushText();
                    if (closing) { if (links.Count > 0) links.RemoveAt(links.Count - 1); }
                    else { string href = Attr(tag, "href"); links.Add(string.IsNullOrWhiteSpace(href) ? (links.Count > 0 ? links[^1] : null) : href.Trim()); }
                    continue;
            }
            int d = closing ? -1 : 1;
            FlushText();
            switch (name)
            {
                case "b": case "strong": bold = System.Math.Max(0, bold + d); break;
                case "i": case "em": case "cite": case "var": italic = System.Math.Max(0, italic + d); break;
                case "del": case "s": case "strike": strike = System.Math.Max(0, strike + d); break;
                case "u": case "ins": under = System.Math.Max(0, under + d); break;
                case "mark": mark = System.Math.Max(0, mark + d); break;
                case "code": case "kbd": case "samp": case "tt": code = System.Math.Max(0, code + d); break;
                case "sub": sub = System.Math.Max(0, sub + d); break;
                case "sup": sup = System.Math.Max(0, sup + d); break;
                case "span": case "font": case "small": case "big": case "abbr": case "time": break;   // 只剥标签
                default: return null;   // 白名单外(table/script/style/iframe/未知…):整块保持字面
            }
        }
        FlushBlock();
        return output;
    }

    // S72:HTML 表格 → 表格块。thead 或首行全 th 时作表头;否则首行占表头位但不加粗(PlainHeader)。
    // colspan/rowspan、嵌套表、caption、单元格外文字、单元格内白名单外标签 → null(整块保持字面)。
    private sealed class ListLevel { public bool Ordered, InItem, Emitted; public int Next, Group; public string Marker; }

    private static List<Block> ConvertTable(string html)
    {
        var rows = new List<(List<(string Html, bool Th, int Align)> Cells, bool Head)>();
        List<(string Html, bool Th, int Align)> row = null;
        bool inHead = false, rowHead = false, cellTh = false, done = false;
        int depth = 0, cellStart = -1, cellAlign = 0, pos = 0, n = html.Length;
        void CloseCell(int at)
        {
            if (cellStart < 0) return;
            row ??= new();
            row.Add((html[cellStart..at], cellTh, cellAlign));
            cellStart = -1;
        }
        void CloseRow()
        {
            if (row != null) rows.Add((row, rowHead));
            row = null;
        }
        while (pos < n)
        {
            char c = html[pos];
            if (c != '<')
            {
                if (cellStart < 0 && !char.IsWhiteSpace(c)) return null;
                pos++;
                continue;
            }
            if (string.CompareOrdinal(html, pos, "<!--", 0, 4) == 0)
            {
                int end = html.IndexOf("-->", pos + 4, System.StringComparison.Ordinal);
                if (end < 0) return null;
                pos = end + 3;
                continue;
            }
            bool closing = pos + 1 < n && html[pos + 1] == '/';
            if (!TagName(html, pos + (closing ? 2 : 1), out string name, out _))
            {
                if (cellStart < 0) return null;
                pos++;
                continue;
            }
            int gt = TagEnd(html, pos + 1);
            if (gt < 0 || done) return null;
            string tag = html.Substring(pos, gt - pos + 1);
            switch (name)
            {
                case "table":
                    if (!closing) { if (++depth > 1) return null; break; }
                    CloseCell(pos); CloseRow();
                    done = --depth == 0;
                    break;
                case "thead": CloseCell(pos); CloseRow(); inHead = !closing; break;
                case "tbody": case "tfoot": CloseCell(pos); CloseRow(); inHead = false; break;
                case "tr":
                    CloseCell(pos); CloseRow();
                    if (!closing) { row = new(); rowHead = inHead; }
                    break;
                case "th":
                case "td":
                    CloseCell(pos);
                    if (closing) break;
                    if (Span(tag, "colspan") || Span(tag, "rowspan")) return null;
                    if (row == null) { row = new(); rowHead = inHead; }
                    cellStart = gt + 1; cellTh = name == "th"; cellAlign = Align(tag);
                    break;
                default:
                    if (cellStart < 0) return null;   // 单元格外只允许表格结构标签
                    break;
            }
            pos = gt + 1;
        }
        CloseCell(n); CloseRow();
        if (rows.Count == 0) return null;
        int columns = 0;
        foreach (var r in rows) columns = System.Math.Max(columns, r.Cells.Count);
        if (columns == 0) return null;
        var runs = new List<Run>[rows.Count][];
        for (int r = 0; r < rows.Count; r++)
        {
            runs[r] = new List<Run>[columns];
            for (int col = 0; col < columns; col++)
            {
                runs[r][col] = col < rows[r].Cells.Count ? CellRuns(rows[r].Cells[col].Html) : new List<Run>();
                if (runs[r][col] == null) return null;
            }
        }
        bool header = rows[0].Head || rows[0].Cells.TrueForAll(cell => cell.Th);
        var aligns = new int[columns];
        for (int col = 0; col < rows[0].Cells.Count; col++) aligns[col] = rows[0].Cells[col].Align;
        return new List<Block> { new Table { Header = runs[0], Rows = runs[1..], Aligns = aligns, PlainHeader = !header } };
    }

    // 单元格内容沿用块转换;块之间(段落、独立图片、标题)在格内换行。
    private static List<Run> CellRuns(string html)
    {
        var blocks = Convert(html);
        if (blocks == null) return null;
        var runs = new List<Run>();
        foreach (var block in blocks)
        {
            List<Run> part = block switch
            {
                Para p => p.Inlines,
                Heading h => h.Inlines.ConvertAll(r => { r.B = true; return r; }),
                Image im => new List<Run> { new Run(im.Alt, img: true, link: im.Src) },
                _ => null
            };
            if (part == null || part.Count == 0) continue;
            if (runs.Count > 0) runs.Add(new Run("\n"));
            runs.AddRange(part);
        }
        return runs;
    }

    private static bool StartsWithTable(string html)
    {
        int i = 0;
        while (true)
        {
            while (i < html.Length && char.IsWhiteSpace(html[i])) i++;
            if (string.CompareOrdinal(html, i, "<!--", 0, 4) != 0) break;
            int end = html.IndexOf("-->", i + 4, System.StringComparison.Ordinal);
            if (end < 0) return false;
            i = end + 3;
        }
        return i < html.Length && html[i] == '<' && TagName(html, i + 1, out string name, out _) && name == "table";
    }

    private static bool Span(string tag, string name)
        => int.TryParse(Attr(tag, name)?.Trim(), out int span) && span > 1;

    private static int Align(string tag)
    {
        string align = Attr(tag, "align")?.Trim().ToLowerInvariant();
        return align == "center" ? 1 : align == "right" ? 2 : 0;
    }

    // 行首/行尾与相邻空白收拢:行首尾去空白,相邻文本 run 之间不留双空格,空 run 丢弃。
    private static void Trim(List<Run> line)
    {
        bool lastSpace = true;
        for (int i = 0; i < line.Count; i++)
        {
            var r = line[i];
            if (r.Img) { lastSpace = false; continue; }
            if (lastSpace && r.Text.Length > 0 && r.Text[0] == ' ') r.Text = r.Text[1..];
            if (r.Text.Length == 0) { line.RemoveAt(i--); continue; }
            lastSpace = r.Text[^1] == ' ';
        }
        while (line.Count > 0 && !line[^1].Img)
        {
            var r = line[^1];
            r.Text = r.Text.TrimEnd(' ');
            if (r.Text.Length > 0) break;
            line.RemoveAt(line.Count - 1);
        }
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char ch in s)
        {
            if (ch is ' ' or '\t' or '\n' or '\r' or '\f') { if (!space) sb.Append(' '); space = true; }
            else { sb.Append(ch); space = false; }
        }
        return sb.ToString();
    }

    private static bool TagName(string s, int start, out string name, out int end)
    {
        end = start;
        while (end < s.Length && (char.IsAsciiLetterOrDigit(s[end]) || s[end] == '-')) end++;
        name = end > start && char.IsAsciiLetter(s[start]) ? s[start..end].ToLowerInvariant() : null;
        return name != null && (end >= s.Length || s[end] is ' ' or '\t' or '\n' or '\r' or '/' or '>');
    }

    private static int TagEnd(string s, int from)
    {
        char quote = '\0';
        for (int i = from; i < s.Length; i++)
        {
            char ch = s[i];
            if (quote != '\0') { if (ch == quote) quote = '\0'; }
            else if (ch is '"' or '\'') quote = ch;
            else if (ch == '>') return i;
        }
        return -1;
    }

    private static string Attr(string tag, string name)
    {
        int p = 0;
        while ((p = tag.IndexOf(name, p, System.StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int q = p + name.Length;
            bool boundary = p > 0 && tag[p - 1] is ' ' or '\t' or '\n' or '\r';
            while (q < tag.Length && tag[q] is ' ' or '\t') q++;
            if (!boundary || q >= tag.Length || tag[q] != '=') { p += name.Length; continue; }
            q++;
            while (q < tag.Length && tag[q] is ' ' or '\t') q++;
            if (q >= tag.Length) return null;
            string value;
            if (tag[q] is '"' or '\'')
            {
                int e = tag.IndexOf(tag[q], q + 1);
                if (e < 0) return null;
                value = tag[(q + 1)..e];
            }
            else
            {
                int e = q;
                while (e < tag.Length && tag[e] is not (' ' or '\t' or '\n' or '>')) e++;
                value = tag[q..e];
                if (value.EndsWith('/')) value = value[..^1];
            }
            return HtmlEntity.Decode(value);
        }
        return null;
    }
}
