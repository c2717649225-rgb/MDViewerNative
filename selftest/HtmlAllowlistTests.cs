using System;
using System.Collections.Generic;
using MDViewerNative;

// E14 / TD-11:白名单过滤的安全性质与常见写法保留。E14b 起导出默认走白名单(HtmlExport.UseAllowlist=true)。
internal static class HtmlAllowlistTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "html allowlist") => check("HtmlAllowlist/" + name, ok, detail);

        // 不动点:输出再过滤一次必须原样且无任何删改记录。白名单里没有事件属性和原始文本类元素,
        // 所以通过不动点的输出在结构上不含可执行内容
        bool Fixpoint(string html, out string output)
        {
            output = HtmlExport.SanitizeAllowlist(html);
            var notes = new List<string>();
            return HtmlExport.SanitizeAllowlist(output, notes) == output && notes.Count == 0;
        }

        foreach (var (name, md) in HtmlShadowReport.Attacks)
        {
            var bad = new List<string>();
            foreach (string frag in HtmlShadowReport.RawFragments(MdParser.Parse(md)))
                if (!Fixpoint(frag, out string o)) bad.Add(o);
            C("attack " + name + " leaves no executable markup", bad.Count == 0, bad.Count == 0 ? "fixpoint" : string.Join(" | ", bad));
        }

        (string In, string Out)[] kept =
        {
            ("<p align=\"center\"><img src=\"logo.png\" width=\"120\" alt=\"logo\"></p>", "<p align=\"center\"><img src=\"logo.png\" width=\"120\" alt=\"logo\"></p>"),
            ("<a href=\"https://example.org/x?a=1&amp;b=2\" target=\"_blank\">x</a>", "<a href=\"https://example.org/x?a=1&amp;b=2\" target=\"_blank\">x</a>"),
            ("<details open><summary>more</summary>", "<details open><summary>more</summary>"),
            ("<picture><source media=\"(prefers-color-scheme: dark)\" srcset=\"d.png 1x, d@2x.png 2x\"><img src=\"l.png\"></picture>",
             "<picture><source media=\"(prefers-color-scheme: dark)\" srcset=\"d.png 1x, d@2x.png 2x\"><img src=\"l.png\"></picture>"),
            ("<kbd>Ctrl</kbd>+<kbd>C</kbd>", "<kbd>Ctrl</kbd>+<kbd>C</kbd>"),
            ("<img src='a.png' ALT=x>", "<img src=\"a.png\" alt=\"x\">"),
            ("<img src=\"C:/docs/a.png\"><img src=\"data:image/png;base64,AAAA\">", "<img src=\"C:/docs/a.png\"><img src=\"data:image/png;base64,AAAA\">"),
        };
        foreach (var (input, expected) in kept)
        {
            string got = HtmlExport.SanitizeAllowlist(input);
            C("keeps " + input, got == expected, got);
        }

        (string In, string Out)[] changed =
        {
            ("<a href=\"javascript&#58;x\" onclick=\"x\">y</a>", "<a href=\"#\">y</a>"),
            ("<a href=\"data:text/html,x\">y</a><img src=\"data:text/html,x\">", "<a href=\"#\">y</a><img src=\"#\">"),
            ("<foo bar>t</foo>", "&lt;foo bar&gt;t&lt;/foo&gt;"),
            ("a<!-- c -->b<!DOCTYPE html>c", "abc"),
            ("<img title=x\"y src=x onerror=z>", "<img title=\"x&quot;y\" src=\"x\">"),
            ("<span style=\"width:expression(alert(1))\">t</span>", "<span>t</span>"),
            ("<img src=\"x", "&lt;img src=&quot;x"),
        };
        foreach (var (input, expected) in changed)
        {
            string got = HtmlExport.SanitizeAllowlist(input);
            C("rewrites " + input, got == expected, got);
        }

        C("export uses the allowlist by default", HtmlExport.UseAllowlist, "UseAllowlist=true");
        string exported = HtmlExport.Build(MdParser.Parse("x <xmp><p title=\"</xmp><img src=x onerror=P()>\">\n"), "t", false);
        int body = exported.IndexOf("<body>", StringComparison.Ordinal);
        C("default export escapes raw-text element tricks", exported.Contains("&lt;xmp&gt;<p title=\"&lt;/xmp&gt;&lt;img src=x onerror=P()&gt;\">") && !exported.Contains("<xmp") && !exported.Contains("<img src=x"), body < 0 ? exported : exported[body..]);
    }
}
