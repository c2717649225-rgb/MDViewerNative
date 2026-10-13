// Markdown → 自包含 HTML 导出:walk 解析块生成语义标签,CSS 内嵌,正暗两套。
using System;
using System.Collections.Generic;
using System.Text;

namespace MDViewerNative;

internal static partial class HtmlExport
{
    // 选区复制为 HTML(41):与导出同一发射器,只取 <body> 内片段;样式由目标应用决定
    public static string Fragment(List<Block> blocks)
    {
        string html = Build(blocks, "", false);
        int start = html.IndexOf("<body>\n", StringComparison.Ordinal) + 7;
        int end = html.LastIndexOf("</body>", StringComparison.Ordinal);
        return html[start..end];
    }

    public static string Build(List<Block> blocks, string title, bool dark, string customCss = "")
    {
        var anchors = new DocumentAnchors(blocks);
        var sb = new StringBuilder(64 * 1024);
        sb.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>").Append(Esc(title)).Append("</title>\n<style>\n");
        sb.Append("body{margin:0 auto;max-width:820px;padding:40px 24px;line-height:1.75;");
        if (dark) sb.Append("color:#d4d4d4;background:#1e1e1e;}a{color:#6cb6ff}th{background:#2a2a2a}code,pre{background:#2a2a2a}mark{background:#6b5b1e;color:#d4d4d4}h2{border-color:#444}hr,td,th{border-color:#444}");
        else sb.Append("color:#1a1a1a;background:#f9f9f8;}a{color:#0b5cad}th{background:#eee}code,pre{background:#f0efec}mark{background:#ffe28c}h2{border-color:#ddd}hr,td,th{border-color:#ccc}");
        sb.Append("font-family:'Segoe UI',system-ui,'Microsoft YaHei',sans-serif;}");
        sb.Append("h1,h2,h3,h4,h5,h6{margin:1.3em 0 .5em}h2{border-bottom:1px solid;padding-bottom:6px}");
        sb.Append("blockquote{border-left:4px solid #8888;margin:8px 0;padding:4px 16px;color:inherit;opacity:.85}");
        sb.Append("pre{padding:14px;border-radius:6px;overflow:auto}code{padding:1px 5px;border-radius:3px;font-family:Consolas,monospace}pre code{background:none;padding:0}");
        sb.Append("table{border-collapse:collapse;margin:8px 0}th,td{border:1px solid;padding:6px 12px}");
        sb.Append("img{max-width:100%}mark{padding:0 2px}sub,sup{font-size:.72em}hr{border:none;border-top:1px solid;margin:24px 0}");
        sb.Append(".task{list-style:none;margin-left:-22px}.fnmark{font-size:.8em}");
        sb.Append(".frontmatter{opacity:.8;border-left:3px solid #8888}.alert{border-left-color:#3b82f6}.alert-warning,.alert-caution{border-left-color:#d97706}.alert-title{font-weight:600;margin:0 0 4px}");
        if (!string.IsNullOrEmpty(customCss)) sb.Append('\n').Append(NeutralizeStyleClose(customCss));
        sb.Append("\n</style>\n</head>\n<body>\n");
        // 递归发射:unwrap=已包裹引用层数*20;引用内代码/标题/引用段等按层级包 blockquote(#128)
        // ListItem 排除:其 XOff 同时承载列表嵌套缩进,不能当引用层级
        void EmitBlocks(List<Block> bs, int lo, int hi, int unwrap)
        {
            int i = lo;
            while (i < hi)
            {
                Block b = bs[i];
                if (b.QuoteDepth > unwrap)
                {
                    // QSeq 变化 = 空行分断的相邻引用运行,各自成块(#244/254);同运行内跨层(嵌套)不在此分断
                    int q0 = b.QSeq;
                    int j = i;
                    while (j < hi && bs[j].QuoteDepth > unwrap && bs[j].QSeq == q0) j++;
                    sb.Append(b.AlertTitle ? "<blockquote class=\"alert alert-" + b.Alert + "\">\n" : "<blockquote>\n");
                    EmitBlocks(bs, i, j, unwrap + 1);
                    sb.Append("</blockquote>\n");
                    i = j;
                    continue;
                }
                switch (b)
                {
                    case Heading h:
                        sb.Append("<h").Append(h.Level).Append(" id=\"").Append(Esc(anchors.IdOf(h))).Append("\">").Append(RunsHtml(h.Inlines, anchors)).Append("</h").Append(h.Level).Append(">\n");
                        break;
                    case CodeBlock cb when cb.Lang is "math" or "latex" or "tex":
                        sb.Append("<div style=\"text-align:center;font-style:italic;margin:12px 0\">").Append(Esc(MathUnicode.Convert(cb.Text.TrimEnd('\n')))).Append("</div>\n");
                        break;
                    case HtmlBlock hb:
                        EmitHtmlBlock(sb, hb.Text);
                        break;
                    case CodeBlock cb when cb.FrontMatter:
                        sb.Append("<pre class=\"frontmatter\"><code>").Append(Esc(cb.Text.TrimEnd('\n'))).Append("</code></pre>\n");
                        break;
                    case CodeBlock cb:
                        sb.Append("<pre><code>").Append(Esc(cb.Text.TrimEnd('\n'))).Append("</code></pre>\n");
                        break;
                    case Table t:
                        EmitTable(t, sb, anchors);
                        break;
                    case Image im:
                        EmitImage(im, sb);
                        break;
                    case HRule:
                        sb.Append("<hr>\n");
                        break;
                    case HtmlDetails dd:
                        EmitDetails(dd, sb, anchors);
                        break;
                    case ListItem li:
                        {
                            i = EmitListGroup(bs, i, hi, sb, anchors);
                            continue;
                        }
                    case Quote q2 when q2.AlertTitle:
                        sb.Append("<p class=\"alert-title\">").Append(RunsHtml(q2.Inlines, anchors)).Append("</p>\n");
                        i++;
                        continue;
                    case Quote q2 when q2.Inlines.Count == 0:
                        break;   // 空引用:仅外壳(#241/242)
                    case Quote q2:
                        // 引用段:blockquote 外壳由上方 XOff 分层包裹提供,这里只发段落
                        sb.Append("<p>").Append(RunsHtml(q2.Inlines, anchors)).Append("</p>\n");
                        i++;
                        continue;
                    case Para p2:
                        {
                            string anchor = "";
                            var firstText = p2.Inlines.Count > 0 ? p2.Inlines[0].Text : "";
                            if (firstText.StartsWith("^") && firstText.Contains('.'))
                                anchor = " id=\"fn-" + Esc(firstText[1..firstText.IndexOf('.')]) + "\"";
                            sb.Append("<p").Append(anchor).Append('>').Append(RunsHtml(p2.Inlines, anchors)).Append("</p>\n");
                            break;
                        }
                }
                i++;
            }
        }
        EmitBlocks(blocks, 0, blocks.Count, 0);
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    private static void EmitTable(Table t, StringBuilder sb, DocumentAnchors anchors)
    {
        sb.Append("<table>\n<thead><tr>");
        foreach (var hc in t.Header) sb.Append("<th>").Append(RunsHtml(hc, anchors)).Append("</th>");
        sb.Append("</tr></thead>\n<tbody>\n");
        for (int r = 0; r < t.Rows.Length; r++)
        {
            sb.Append("<tr>");
            for (int c = 0; c < t.Header.Length; c++)
            {
                var cell = c < t.Rows[r].Length ? t.Rows[r][c] : new List<Run>();
                string sty = t.Aligns != null && c < t.Aligns.Length ? t.Aligns[c] switch { 1 => " style=\"text-align:center\"", 2 => " style=\"text-align:right\"", _ => "" } : "";
                sb.Append("<td").Append(sty).Append('>').Append(RunsHtml(cell, anchors)).Append("</td>");
            }
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody>\n</table>\n");
    }

    private static void EmitImage(Image im, StringBuilder sb)
    {
        sb.Append("<img src=\"").Append(EscUrl(im.Src)).Append("\" alt=\"").Append(Esc(im.Alt)).Append('"');
        if (im.Title != null) sb.Append(" title=\"").Append(Esc(im.Title)).Append('"');
        sb.Append(">\n");
    }

    private static void EmitDetails(HtmlDetails dd, StringBuilder sb, DocumentAnchors anchors)
    {
        sb.Append("<details><summary>").Append(RunsHtml(dd.Summary, anchors)).Append("</summary>\n<pre style=\"white-space:pre-wrap;margin-left:16px\">").Append(Esc(dd.Body)).Append("</pre></details>\n");
    }

    // A child list can have a different kind from its parent. Only siblings split a group.
    private static int EmitListGroup(List<Block> bs, int lo, int hi, StringBuilder sb, DocumentAnchors anchors)
    {
        var first = (ListItem)bs[lo];
        bool ordered = first.Marker != "•";
        if (ordered)
        {
            int start = int.TryParse(first.Marker.TrimEnd('.'), out int n) ? n : 1;
            sb.Append("<ol start=\"").Append(start).Append("\">\n");
        }
        else sb.Append("<ul>\n");
        int end = lo + 1;
        while (end < hi && bs[end] is ListItem next && next.QuoteDepth == first.QuoteDepth
            && (next.Nest > first.Nest || (next.Nest == first.Nest && next.ListGroup == first.ListGroup && (next.Marker != "•") == ordered))) end++;
        EmitListItems(bs, lo, end, sb, anchors);
        sb.Append(ordered ? "</ol>\n" : "</ul>\n");
        return end;
    }

    // 平铺 ListItem 序列按 Nest 层深重组为嵌套列表:Nest 大于上一项 → 该段递归为上一项的子列表
    // (Nest=真实嵌套层深;XOff 是渲染缩进(lead/2*20),二者解耦——兄弟项可因缩进不同有不同 XOff 但同 Nest)
    private static void EmitListItems(List<Block> bs, int lo, int hi, StringBuilder sb, DocumentAnchors anchors)
    {
        int k = lo;
        while (k < hi)
        {
            var it = (ListItem)bs[k];
            EmitListItemOpen(it, sb, anchors);
            int k3 = k + 1;
            while (k3 < hi && bs[k3] is ListItem nx)
            {
                if (nx.Nest > it.Nest) k3 = EmitListGroup(bs, k3, hi, sb, anchors);
                else if (nx.Continuation && nx.Nest == it.Nest)
                { EmitListItemContent(nx, sb, anchors); k3++; }
                else break;
            }
            EmitListItemClose(sb);
            k = k3;
        }
    }

    // 开标签+内容;嵌套子列表在 Open 与 Close 之间发射(位于 </li> 内)
    private static void EmitListItemOpen(ListItem lj2, StringBuilder sb, DocumentAnchors anchors)
    {
        bool ordered = lj2.Marker != "•";
        sb.Append("<li");
        if (ordered) sb.Append(" value=\"").Append(Esc(lj2.Marker.TrimEnd('.'))).Append('"');
        if (lj2.Loose) sb.Append(" style=\"margin:.5em 0\"");
        if (lj2.Task > 0)
        {
            sb.Append(" class=\"task\">");
            sb.Append("<input type=\"checkbox\" disabled").Append(lj2.Task == 2 ? " checked" : "").Append("> ");
        }
        else sb.Append('>');
        EmitListItemContent(lj2, sb, anchors);
    }

    private static void EmitListItemContent(ListItem lj2, StringBuilder sb, DocumentAnchors anchors)
    {
        // Only list-level looseness wraps the first paragraph. Adjacent child quotes
        // or fences do not by themselves make a list loose.
        bool wrapP = lj2.Inlines.Count > 0 && lj2.Loose;
        if (wrapP) sb.Append("<p>");
        sb.Append(RunsHtml(lj2.Inlines, anchors));
        if (wrapP) sb.Append("</p>");
        // 项内子块:嵌套渲染(段落/代码/引用等走主逻辑同款输出)
        if (lj2.Sub != null)
        {
            sb.Append('\n');
            foreach (Block sbk in lj2.Sub)
            {
                switch (sbk)
                {
                    case CodeBlock scb:
                        sb.Append("<pre><code>").Append(Esc(scb.Text)).Append("</code></pre>\n");
                        break;
                    case Quote sq when sq.Inlines.Count == 0:
                        sb.Append("<blockquote>\n</blockquote>\n");
                        break;
                    case Quote sq:
                        sb.Append("<blockquote>\n<p>").Append(RunsHtml(sq.Inlines, anchors)).Append("</p>\n</blockquote>\n");
                        break;
                    case Para sp:
                        if (lj2.Loose) sb.Append("<p>");
                        sb.Append(RunsHtml(sp.Inlines, anchors));
                        if (lj2.Loose) sb.Append("</p>");
                        sb.Append('\n');
                        break;
                    case Table tableSub:
                        EmitTable(tableSub, sb, anchors);
                        break;
                    case Image imageSub:
                        EmitImage(imageSub, sb);
                        break;
                    case HtmlDetails detailsSub:
                        EmitDetails(detailsSub, sb, anchors);
                        break;
                    case Heading sh:
                        sb.Append("<h").Append(sh.Level).Append(" id=\"").Append(Esc(anchors.IdOf(sh))).Append("\">").Append(RunsHtml(sh.Inlines, anchors)).Append("</h").Append(sh.Level).Append(">\n");
                        break;
                    case HtmlBlock shb:
                        EmitHtmlBlock(sb, shb.Text);
                        break;
                    case HRule:
                        sb.Append("<hr>\n");
                        break;
                }
            }
        }
    }

    private static void EmitListItemClose(StringBuilder sb) => sb.Append("</li>\n");

    private static string RunsHtml(List<Run> runs, DocumentAnchors anchors)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < runs.Count;)
        {
            var r = runs[i];
            string pre = "", post = "";
            if (r.B) { pre += "<strong>"; post = "</strong>" + post; }
            if (r.I) { pre += "<em>"; post = "</em>" + post; }
            if (r.S) { pre += "<del>"; post = "</del>" + post; }
            if (r.Code) { pre += "<code>"; post = "</code>" + post; }
            if (r.Mark) { pre += "<mark>"; post = "</mark>" + post; }
            if (r.U) { pre += "<u>"; post = "</u>" + post; }
            if (r.SupSub == 1) { pre += "<sup>"; post = "</sup>" + post; }
            if (r.SupSub == 2) { pre += "<sub>"; post = "</sub>" + post; }
            if (r.Fn) pre += "<span class=\"fnmark\">";
            string body = r.Opaque ? FilterRawHtml(r.Text) : Esc(r.Text).Replace("\n", "<br>");
            if (r.Img)
            {
                // 行内图片:alt=文本形式(渲染同形),src 经百分号编码与危险 scheme 中和;外层链接包裹(#519)
                string img = "<img src=\"" + EscUrl(r.Link ?? "") + "\" alt=\"" + Esc(r.ImageAlt ?? r.Text) + "\"";
                if (r.LinkTitle != null) img += " title=\"" + Esc(r.LinkTitle) + "\"";
                img += ">";
                sb.Append(pre);
                if (r.LinkOuter != null)
                {
                    // 与 r.Link 的脚注分支同款:fid 来自链接 dest 原文,必须过转义
                    string oh = r.LinkOuter.StartsWith("#fn:") ? "#fn-" + Esc(EscUrl(r.LinkOuter[4..])) : Esc(EscUrl(anchors.ExportHref(r.LinkOuter)));
                    sb.Append("<a href=\"").Append(oh).Append("\">").Append(img).Append("</a>");
                }
                else sb.Append(img);
                sb.Append(post);
                if (r.Fn) sb.Append("</span>");
                i++;
                continue;
            }
            if (r.Link != null)
            {
                // 同一链接的连续 run 段合并为单个 <a>(label 内联解析的扁平流,#518/532)
                int j = i + 1;
                while (j < runs.Count && runs[j].Link == r.Link && runs[j].Fn == r.Fn && runs[j].Img == r.Img) j++;
                // 脚注 id 同样过 EscUrl+Esc:label 来自 [^…] 原文,不转义即可注入 href 属性
                string href = r.Link.StartsWith("#fn:") ? "#fn-" + Esc(EscUrl(r.Link[4..])) : Esc(EscUrl(anchors.ExportHref(r.Link)));
                sb.Append(pre).Append("<a href=\"").Append(href).Append('"');
                if (r.LinkTitle != null) sb.Append(" title=\"").Append(Esc(r.LinkTitle)).Append('"');
                sb.Append('>');
                for (int k = i; k < j; k++)
                {
                    var rk = runs[k];
                    string pk = "", qk = "";
                    if (rk.B) { pk += "<strong>"; qk = "</strong>" + qk; }
                    if (rk.I) { pk += "<em>"; qk = "</em>" + qk; }
                    if (rk.S) { pk += "<del>"; qk = "</del>" + qk; }
                    if (rk.Code) { pk += "<code>"; qk = "</code>" + qk; }
                    if (rk.Mark) { pk += "<mark>"; qk = "</mark>" + qk; }
                    if (rk.U) { pk += "<u>"; qk = "</u>" + qk; }
                    if (rk.SupSub == 1) { pk += "<sup>"; qk = "</sup>" + qk; }
                    if (rk.SupSub == 2) { pk += "<sub>"; qk = "</sub>" + qk; }
                    string bk = rk.Opaque ? FilterRawHtml(rk.Text) : Esc(rk.Text).Replace("\n", "<br>");
                    sb.Append(pk).Append(bk).Append(qk);
                }
                sb.Append("</a>").Append(post);
                if (r.Fn) sb.Append("</span>");
                i = j;
                continue;
            }
            sb.Append(pre).Append(body).Append(post);
            if (r.Fn) sb.Append("</span>");
            i++;
        }
        return sb.ToString();
    }

    private static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    // URL 百分号编码:空格、保留字符与非 ASCII(UTF-8 大写十六进制)
    private static string EscUrl(string s)
    {
        if (IsDangerScheme(s)) return "#";   // javascript:/vbscript:/data:text/html 链接中和
        var sb = new StringBuilder(s.Length);
        var bytes = new List<byte>();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c > '~' || c <= ' ' || c == '"' || c == '<' || c == '>' || c == '\\' || c == '^' || c == '`'
                || c == '{' || c == '|' || c == '}' || c == '[' || c == ']')
            {
                // 代理对成对编码;孤立代理替换为 U+FFFD(逐 char 走 ConvertFromUtf32 会抛异常,导出整体静默失败)
                bytes.Clear();
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsSurrogatePair(c, s[i + 1]))
                {
                    bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(s.Substring(i, 2)));
                    i++;
                }
                else if (char.IsSurrogate(c))
                {
                    bytes.AddRange(System.Text.Encoding.UTF8.GetBytes("\uFFFD"));
                }
                else
                {
                    bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(s, i, 1));
                }
                foreach (byte b in bytes) sb.Append('%').Append(b.ToString("X2"));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // customCss 里 "</style" 会提前闭合 <style> 块注入脚本;中性化为 <\/style(CSS 内 \/ 即 /)
    internal static string NeutralizeStyleClose(string css)
    {
        if (css.IndexOf("</style", StringComparison.OrdinalIgnoreCase) < 0) return css;
        var sb = new StringBuilder(css.Length + 16);
        for (int i = 0; i < css.Length; i++)
        {
            if (i + 7 <= css.Length && string.Compare(css, i, "</style", 0, 7, true) == 0) { sb.Append("<\\/style"); i += 6; }
            else sb.Append(css[i]);
        }
        return sb.ToString();
    }

    // 原始 HTML 块导出:脚本类块整块转义为字面(导出文件不可执行);其余经 FilterRawHtml(E14b 起默认白名单,见 ADR-0002)
    private static void EmitHtmlBlock(StringBuilder sb, string text)
    {
        text = text.TrimEnd('\n');
        if (HasBlockedTag(text))
        {
            sb.Append("<pre style=\"white-space:pre-wrap\">").Append(Esc(text)).Append("</pre>\n");
            return;
        }
        sb.Append(FilterRawHtml(text)).Append('\n');
    }

    private static bool HasBlockedTag(string t)
    {
        foreach (string tag in new[] { "<script", "<style", "<iframe", "<object", "<embed" })
            if (t.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    // 直通 HTML 消毒:on* 事件属性中和为 xon*(未知属性,浏览器不执行),href/src 危险 scheme 中和为 #。
    // 引号感知,不进入属性值;只改属性名/URL 值,不动文本内容。
    internal static string SanitizeRawHtml(string t)
    {
        var sb = new StringBuilder(t.Length + 8);
        bool inTag = false, expectAttr = false;
        char quote = '\0';
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (!inTag)
            {
                if (c == '<' && i + 1 < t.Length && (char.IsAsciiLetter(t[i + 1]) || t[i + 1] == '/' || t[i + 1] == '!')) { inTag = true; expectAttr = false; }
                sb.Append(c);
                continue;
            }
            if (quote != '\0')
            {
                if (c == quote) { quote = '\0'; expectAttr = true; }
                sb.Append(c);
                continue;
            }
            if (c == '"' || c == '\'')
            {
                if (IsUrlAttrTail(sb, true))
                {
                    int q = t.IndexOf(c, i + 1);
                    if (q > i && IsDangerScheme(SchemeCheckForm(t[(i + 1)..q]))) { sb.Append(c).Append('#').Append(c); i = q; continue; }
                }
                quote = c; sb.Append(c);
                continue;
            }
            if (c == '>') { inTag = false; expectAttr = false; sb.Append(c); continue; }
            if (c == '=' && IsUrlAttrTail(sb, false))
            {
                sb.Append(c);
                int m = i + 1;
                while (m < t.Length && (t[m] == ' ' || t[m] == '\t' || t[m] == '\n' || t[m] == '\f' || t[m] == '\r')) m++;
                if (m < t.Length && t[m] != '"' && t[m] != '\'')
                {
                    int e2 = m;
                    while (e2 < t.Length && t[e2] != ' ' && t[e2] != '\t' && t[e2] != '\n' && t[e2] != '>') e2++;
                    if (IsDangerScheme(SchemeCheckForm(t[m..e2]))) { sb.Append(t[(i + 1)..m]).Append('#'); i = e2 - 1; continue; }
                }
                continue;
            }
            if ((c == ' ' || c == '\t' || c == '\n' || c == '\f' || c == '\r' || c == '/')) { expectAttr = true; sb.Append(c); continue; }   // 空白与/均为属性分隔符(HTML5 tag 内 \f \r 亦为空白)
            if (expectAttr && (c == 'o' || c == 'O') && i + 1 < t.Length && (t[i + 1] == 'n' || t[i + 1] == 'N') && i + 2 < t.Length && char.IsAsciiLetter(t[i + 2]))
            {
                int k = i + 2;
                while (k < t.Length && (char.IsAsciiLetterOrDigit(t[k]) || t[k] == '-')) k++;
                while (k < t.Length && (t[k] == ' ' || t[k] == '\t' || t[k] == '\n' || t[k] == '\f' || t[k] == '\r')) k++;   // 属性名与 = 间空白浏览器照常执行,必须同中和
                if (k < t.Length && t[k] == '=') { sb.Append("xo"); expectAttr = false; continue; }   // onxxx= → xonxxx=
            }
            expectAttr = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    // StringBuilder 末尾是否刚写了 URL 属性(含等号与否):忽略空白(浏览器允许 href = "…"),
    // action/formaction 同为 javascript: 执行点,一并中和;data-href 类误伤仅多查一次 scheme,方向安全
    private static bool IsUrlAttrTail(StringBuilder sb, bool withEq)
    {
        int n = Math.Min(sb.Length, 32);
        var tail = new System.Text.StringBuilder(sb.ToString(sb.Length - n, n).ToLowerInvariant());
        for (int i = tail.Length - 1; i >= 0; i--)
            if (tail[i] == ' ' || tail[i] == '\t' || tail[i] == '\n' || tail[i] == '\f' || tail[i] == '\r') tail.Remove(i, 1);
        string s2 = tail.ToString();
        return withEq
            ? s2.EndsWith("href=") || s2.EndsWith("src=") || s2.EndsWith("action=")
            : s2.EndsWith("href") || s2.EndsWith("src") || s2.EndsWith("action");
    }

    // 危险 URL scheme:压缩全部空白/控制符后前缀比对(覆盖 java script:/JaVaScRiPt:/前导空白)
    // scheme 危险性判定用的归一形态:窄集实体解码(浏览器属性值先解码实体再解析协议)。
    // 数字实体:浏览器【不要求分号】且允许补零(&#58alert / &#0000058; 均解码),故吃满数字串后按值界解码;
    // 命名实体:非 legacy 名必须带分号,维持要求 ';';解码单遍进行(&amp;colon; → "&" + "colon;"),与浏览器一致。
    // 只用于检查,不改写原文,避免破坏正常含 & 的 URL;控制符/空格剥离 IsDangerScheme 内已有
    internal static string SchemeCheckForm(string v)
    {
        if (v.IndexOf('&') < 0) return v;
        var sb = new StringBuilder(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] == '&' && i + 2 < v.Length)
            {
                if (v[i + 1] == '#')
                {
                    // 数字实体:浏览器不要求分号、允许补零且位数无上限,必须吃满数字串并饱和累加。
                    // 只判截断会漏过补零长形(&#00000000058; 浏览器解出 ':');溢出后继续吃数字但不再解码(浏览器映射 U+FFFD,同样不成 scheme,字面保持一致惰性)
                    int j = i + 2;
                    bool hex = j < v.Length && (v[j] == 'x' || v[j] == 'X');
                    if (hex) j++;
                    int start = j;
                    long val = 0;
                    bool any = false, ovf = false;
                    while (j < v.Length)
                    {
                        int d;
                        if (hex)
                        {
                            char hc = v[j];
                            if (hc >= '0' && hc <= '9') d = hc - '0';
                            else if (hc >= 'a' && hc <= 'f') d = hc - 'a' + 10;
                            else if (hc >= 'A' && hc <= 'F') d = hc - 'A' + 10;
                            else d = -1;
                        }
                        else d = char.IsAsciiDigit(v[j]) ? v[j] - '0' : -1;
                        if (d < 0) break;
                        j++;
                        any = true;
                        if (!ovf)
                        {
                            val = val * (hex ? 16 : 10) + d;
                            if (val > 0x10FFFF) { ovf = true; }
                        }
                    }
                    int digitsEnd = j;   // 先记数字串终点再吃分号,切片才不会把 ';' 带进去
                    if (j < v.Length && v[j] == ';') j++;   // 分号是引用的一部分,必须随解码一并消费(否则 j;avascript 形不成 scheme)
                    if (any && !ovf && val >= 1 && (val < 0xD800 || val > 0xDFFF))
                    {
                        sb.Append(char.ConvertFromUtf32((int)val));
                        i = j - 1;
                        continue;
                    }
                }
                else
                {
                    int semi = v.IndexOf(';', i + 1);
                    if (semi > i + 1 && semi - i <= 12)
                    {
                        string ent = v[i..(semi + 1)];
                        string dec = ent switch
                        {
                            "&colon;" => ":",
                            "&sol;" => "/",
                            "&semi;" => ";",
                            "&Tab;" => "\t",
                            "&NewLine;" => "\n",
                            "&amp;" => "&",
                            _ => null,
                        };
                        if (dec != null) { sb.Append(dec); i = semi; continue; }
                    }
                }
            }
            sb.Append(v[i]);
        }
        return sb.ToString();
    }

    private static bool IsDangerScheme(string url)
    {
        var cs = new StringBuilder(16);
        foreach (char ch in url)
        {
            if (ch > ' ') cs.Append(char.ToLowerInvariant(ch));
            if (cs.Length >= 16) break;
        }
        string s = cs.ToString();
        return s.StartsWith("javascript:") || s.StartsWith("vbscript:") || s.StartsWith("data:text/html");
    }
}
