// HTML 导出白名单过滤(TD-11 / E14):按 HTML5 数据态规则分词,只放行白名单标签与属性,并规范化重写。
// 与黑名单 SanitizeRawHtml 的根本区别:输出不再是"原文 + 局部修改",而是重新序列化的标签;
// 输出里只剩白名单标签(不含 script/style/textarea/title/xmp/noscript/svg/math 等原始文本或外来内容元素),
// 浏览器解析输出得到的结构与这里的分词结果一致,不存在"过滤器以为在属性里、浏览器以为在标签外"的解析差异。
// 白名单外的标签转义为可见字面(与阅读态一致),注释丢弃,危险 URL 中和为 #。
// E14b 起导出默认走白名单;UseAllowlist=false 可退回旧黑名单(回滚只需改默认值)。与导出同属 HtmlExport(partial),复用 Esc/SchemeCheckForm,不形成模块循环。
using System;
using System.Collections.Generic;
using System.Text;

namespace MDViewerNative;

internal static partial class HtmlExport
{
    // E14/TD-11 内部开关:true=白名单(E14b 起默认),false=旧黑名单 SanitizeRawHtml(保留供回滚与影子对比)。无用户配置
    internal static bool UseAllowlist = true;

    private static string FilterRawHtml(string t) => UseAllowlist ? SanitizeAllowlist(t) : SanitizeRawHtml(t);

    private static readonly HashSet<string> AllowTags = new(StringComparer.Ordinal)
    {
        "a", "abbr", "acronym", "address", "article", "aside", "b", "bdi", "bdo", "big", "blockquote", "br",
        "caption", "center", "cite", "code", "col", "colgroup", "data", "dd", "del", "details", "dfn", "div",
        "dl", "dt", "em", "figcaption", "figure", "font", "footer", "h1", "h2", "h3", "h4", "h5", "h6",
        "header", "hgroup", "hr", "i", "img", "ins", "kbd", "li", "main", "mark", "nav", "ol", "p", "picture",
        "pre", "q", "rp", "rt", "ruby", "s", "samp", "section", "small", "source", "span", "strike", "strong",
        "sub", "summary", "sup", "table", "tbody", "td", "tfoot", "th", "thead", "time", "tr", "tt", "u",
        "ul", "var", "wbr",
    };

    private static readonly HashSet<string> AllowGlobalAttrs = new(StringComparer.Ordinal)
    {
        "id", "class", "title", "lang", "dir", "align", "valign", "style", "width", "height",
    };

    private static readonly Dictionary<string, string[]> AllowTagAttrs = new(StringComparer.Ordinal)
    {
        ["a"] = new[] { "href", "name", "target", "rel" },
        ["img"] = new[] { "src", "alt", "srcset", "loading", "border" },
        ["source"] = new[] { "srcset", "media", "type" },
        ["ol"] = new[] { "start", "type", "reversed" },
        ["ul"] = new[] { "type" },
        ["li"] = new[] { "value" },
        ["table"] = new[] { "border", "cellpadding", "cellspacing", "summary" },
        ["td"] = new[] { "colspan", "rowspan", "scope", "nowrap" },
        ["th"] = new[] { "colspan", "rowspan", "scope", "nowrap" },
        ["col"] = new[] { "span" },
        ["colgroup"] = new[] { "span" },
        ["details"] = new[] { "open" },
        ["font"] = new[] { "color", "face", "size" },
        ["blockquote"] = new[] { "cite" },
        ["q"] = new[] { "cite" },
        ["del"] = new[] { "cite", "datetime" },
        ["ins"] = new[] { "cite", "datetime" },
        ["time"] = new[] { "datetime" },
        ["data"] = new[] { "value" },
    };

    private static readonly string[] AllowSchemes = { "http", "https", "mailto", "ftp", "file" };

    // notes:可选,记录每处与原文不同的原因(影子对比分类用),形如 "tag:svg"、"attr:img.onerror"、"url:a.href"、"comment"
    internal static string SanitizeAllowlist(string t, List<string> notes = null)
    {
        var sb = new StringBuilder(t.Length + 16);
        int i = 0, n = t.Length;
        while (i < n)
        {
            char c = t[i];
            if (c != '<') { sb.Append(c); i++; continue; }
            if (string.CompareOrdinal(t, i, "<!--", 0, 4) == 0)
            {
                i = CommentEnd(t, i + 4);
                notes?.Add("comment");
                continue;
            }
            char c1 = i + 1 < n ? t[i + 1] : '\0';
            if (c1 == '!' || c1 == '?' || (c1 == '/' && i + 2 < n && !char.IsAsciiLetter(t[i + 2]) && t[i + 2] != '>'))
            {
                // 假注释(<!DOCTYPE、<![CDATA[、<?xml、</ 空白…):浏览器吞到下一个 >
                int gt = t.IndexOf('>', i + 2);
                i = gt < 0 ? n : gt + 1;
                notes?.Add("bogus");
                continue;
            }
            bool end = c1 == '/';
            int nameStart = i + (end ? 2 : 1);
            if (nameStart >= n || !char.IsAsciiLetter(t[nameStart]))
            {
                sb.Append("&lt;");   // 不构成标签的 <(含 </>):按字面
                i++;
                continue;
            }
            if (!TryParseTag(t, nameStart, out string name, out var attrs, out int tagEnd))
            {
                sb.Append(Esc(t[i..]));   // 标签在片段末尾仍未闭合:浏览器会丢弃,这里保留为可见字面
                notes?.Add("unterminated");
                break;
            }
            if (!AllowTags.Contains(name))
            {
                sb.Append(Esc(t[i..tagEnd]));
                notes?.Add("tag:" + name);
            }
            else if (end)
            {
                sb.Append("</").Append(name).Append('>');
            }
            else
            {
                sb.Append('<').Append(name);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (an, av) in attrs)
                {
                    if (!seen.Add(an)) continue;   // 重复属性:浏览器取第一个
                    if (!AttrAllowed(name, an)) { notes?.Add("attr:" + name + "." + an); continue; }
                    string v = av;
                    if (v != null && (an == "href" || an == "src" || an == "cite") && !SafeUrl(v, name == "img" && an == "src"))
                    {
                        v = "#";
                        notes?.Add("url:" + name + "." + an);
                    }
                    else if (v != null && an == "srcset" && !SafeSrcset(v))
                    {
                        notes?.Add("url:" + name + "." + an);
                        continue;
                    }
                    else if (v != null && an == "style" && LegacyScriptStyle(v))
                    {
                        notes?.Add("style:" + name);
                        continue;
                    }
                    sb.Append(' ').Append(an);
                    // 属性值内 < > 也转义:浏览器解码后语义相同,输出里除真正的标签外不再出现尖括号(复制为 HTML 时对老解析器更稳妥)
                    if (v != null) sb.Append("=\"").Append(v.Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;")).Append('"');
                }
                sb.Append('>');
            }
            i = tagEnd;
        }
        return sb.ToString();
    }

    // 注释终点(HTML5 注释态):<!--> 与 <!---> 立即结束;否则到 --> 或 --!>;未闭合则吞到末尾
    private static int CommentEnd(string t, int p)
    {
        if (p < t.Length && t[p] == '>') return p + 1;
        if (p + 1 < t.Length && t[p] == '-' && t[p + 1] == '>') return p + 2;
        for (int k = p; k + 2 < t.Length; k++)
        {
            if (t[k] != '-' || t[k + 1] != '-') continue;
            if (t[k + 2] == '>') return k + 3;
            if (k + 3 < t.Length && t[k + 2] == '!' && t[k + 3] == '>') return k + 4;
        }
        return t.Length;
    }

    private static bool IsWs(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\f' || c == '\r';

    // HTML5 标签态分词:名字到空白 / > 为止;属性名首字符可为 =;值按真正的引号规则读取(可跨 >)
    private static bool TryParseTag(string t, int p, out string name, out List<(string, string)> attrs, out int tagEnd)
    {
        attrs = new List<(string, string)>();
        tagEnd = 0;
        int n = t.Length;
        int s0 = p;
        while (p < n && !IsWs(t[p]) && t[p] != '/' && t[p] != '>') p++;
        name = AsciiLower(t[s0..p]);
        while (true)
        {
            while (p < n && (IsWs(t[p]) || t[p] == '/')) p++;
            if (p >= n) return false;
            if (t[p] == '>') { tagEnd = p + 1; return true; }
            int a0 = p++;
            while (p < n && !IsWs(t[p]) && t[p] != '/' && t[p] != '>' && t[p] != '=') p++;
            string an = AsciiLower(t[a0..p]);
            int q = p;
            while (q < n && IsWs(t[q])) q++;
            if (q >= n || t[q] != '=') { attrs.Add((an, null)); p = q; continue; }
            p = q + 1;
            while (p < n && IsWs(t[p])) p++;
            if (p >= n) return false;
            if (t[p] == '"' || t[p] == '\'')
            {
                int close = t.IndexOf(t[p], p + 1);
                if (close < 0) return false;
                attrs.Add((an, t[(p + 1)..close]));
                p = close + 1;
            }
            else if (t[p] == '>')
            {
                attrs.Add((an, ""));
            }
            else
            {
                int v0 = p;
                while (p < n && !IsWs(t[p]) && t[p] != '>') p++;
                attrs.Add((an, t[v0..p]));
            }
        }
    }

    // 浏览器对标签名/属性名只做 ASCII 小写(ToLowerInvariant 会把 U+212A 开尔文符号变成 k,与浏览器不一致)
    private static string AsciiLower(string s)
    {
        var cs = s.ToCharArray();
        for (int k = 0; k < cs.Length; k++) if (cs[k] >= 'A' && cs[k] <= 'Z') cs[k] = (char)(cs[k] + 32);
        return new string(cs);
    }

    private static bool AttrAllowed(string tag, string attr)
        => AllowGlobalAttrs.Contains(attr) || (AllowTagAttrs.TryGetValue(tag, out var list) && Array.IndexOf(list, attr) >= 0);

    // URL 协议白名单:相对地址、常见协议、单字母盘符(C:/…);图片额外允许 data:image/。
    // 先做与黑名单相同的窄集实体解码,再去掉浏览器会忽略的 ASCII 空白与控制符
    internal static bool SafeUrl(string raw, bool image)
    {
        string v = SchemeCheckForm(raw);
        var cs = new StringBuilder(v.Length);
        foreach (char ch in v) if (ch > ' ') cs.Append(ch);
        string u = cs.ToString();
        int colon = u.IndexOf(':');
        if (colon < 0) return true;
        int delim = u.IndexOfAny(new[] { '/', '?', '#' });
        if (delim >= 0 && delim < colon) return true;   // 冒号在路径/查询/片段里,不是协议
        string scheme = AsciiLower(u[..colon]);
        if (scheme.Length == 1 && char.IsAsciiLetter(scheme[0])) return true;
        if (Array.IndexOf(AllowSchemes, scheme) >= 0) return true;
        return image && scheme == "data" && u.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SafeSrcset(string v)
    {
        foreach (string cand in v.Split(','))
        {
            string c = cand.Trim();
            int sp = c.IndexOfAny(new[] { ' ', '\t', '\n', '\f', '\r' });
            if (c.Length > 0 && !SafeUrl(sp < 0 ? c : c[..sp], true)) return false;
        }
        return true;
    }

    // 旧浏览器会执行的 CSS 写法(expression / behavior / -moz-binding / javascript:):整条 style 丢弃
    private static bool LegacyScriptStyle(string v)
    {
        var cs = new StringBuilder(v.Length);
        foreach (char ch in v) if (ch > ' ') cs.Append(char.ToLowerInvariant(ch));
        string s = cs.ToString();
        return s.Contains("expression(") || s.Contains("behavior:") || s.Contains("-moz-binding") || s.Contains("javascript:");
    }
}
