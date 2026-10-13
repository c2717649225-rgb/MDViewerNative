using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MDViewerNative;

// E14 / TD-11:HTML 导出过滤的影子对比(按需运行的评估工具,不在 Verify 里)。
// 用法(工作目录 selftest/):dotnet run -c Release --no-build -- --html-shadow <输出目录> <文件或目录>...
//   - 收集每份文档里会交给原始 HTML 过滤的片段,分别用黑名单与白名单过滤,按差异原因分类汇总;
//   - 输入含 spec.txt 时,另外比较两种策略下的规范示例通过数;
//   - 为攻击样例各写一对导出文件(old-/new-),供真实浏览器判定是否执行(P() 为标记函数)。
internal static class HtmlShadowReport
{
    // 攻击样例:P() 代替 alert。前两组为黑名单已覆盖的对照,其余为按浏览器解析规则推断的绕过
    internal static readonly (string Name, string Md)[] Attacks =
    {
        ("img-onerror", "<img src=x onerror=P()>\n"),
        ("js-href", "<a href=\"javascript:P()\">x</a>\n"),
        ("js-href-entity", "<a href=\"jav&#x09;ascript&colon;P()\">x</a>\n"),
        ("formaction", "<form><button formaction=\"javascript:P()\">x</button></form>\n"),
        ("comment-end-bang", "<div>\n<!-- --!><img src=x onerror=P()> -->\n</div>\n"),
        ("comment-quote", "<div>\n<!-- \" -->\n<img src=x onerror=P()>\n<!-- \" -->\n</div>\n"),
        ("unquoted-quote", "<div>\n<img title=x\"y src=x onerror=P()>\n</div>\n"),
        ("noscript-inline", "x <noscript><p title=\"</noscript><img src=x onerror=P()>\"></noscript>\n"),
        ("xmp-inline", "x <xmp><p title=\"</xmp><img src=x onerror=P()>\">\n"),
        ("textarea-inline", "x <textarea><p title=\"</textarea><img src=x onerror=P()>\">\n"),
        ("title-inline", "x <title><p title=\"</title><img src=x onerror=P()>\">\n"),
        ("noembed-inline", "x <noembed><p title=\"</noembed><img src=x onerror=P()>\">\n"),
        ("svg-set-href", "<svg><a><set attributeName=\"href\" to=\"javascript:P()\"/><text y=\"20\">x</text></a></svg>\n"),
        ("svg-animate-href", "<svg><a><animate attributeName=\"href\" values=\"javascript:P()\"/><text y=\"20\">x</text></a></svg>\n"),
    };

    internal static int Run(string[] args, Func<string, string> specNorm, Func<string, string> specRender)
    {
        if (args.Length < 3) { Console.WriteLine("usage: --html-shadow <outDir> <file|dir>..."); return 1; }
        string outDir = args[1];
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var examples = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        int fragments = 0, changed = 0, canonicalOnly = 0, docs = 0;

        void Record(string source, string frag)
        {
            fragments++;
            string old = HtmlExport.SanitizeRawHtml(frag);
            var notes = new List<string>();
            string neu = HtmlExport.SanitizeAllowlist(frag, notes);
            if (old == neu) return;
            changed++;
            if (notes.Count == 0) { canonicalOnly++; notes.Add("(规范化:引号/大小写/字面 <)"); }
            foreach (string note in notes.Distinct())
            {
                tally[note] = tally.GetValueOrDefault(note) + 1;
                if (!examples.TryGetValue(note, out var list)) examples[note] = list = new List<string>();
                if (list.Count < 3) list.Add($"    [{source}]\n      旧: {Clip(old)}\n      新: {Clip(neu)}");
            }
        }

        var files = new List<string>();
        foreach (string input in args.Skip(2))
        {
            if (Directory.Exists(input)) files.AddRange(Directory.GetFiles(input, "*.md", SearchOption.AllDirectories));
            else if (File.Exists(input)) files.Add(input);
            else report.AppendLine("缺少输入: " + input);
        }
        string specPath = null;
        foreach (string f in files.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (Path.GetFileName(f) == "spec.txt") { specPath = f; continue; }
            docs++;
            string md = File.ReadAllText(f);
            foreach (string frag in RawFragments(MdParser.Parse(md))) Record(Path.GetFileName(f), frag);
        }

        if (specPath != null)
        {
            int total = 0, passOld = 0, passNew = 0;
            var flipped = new List<string>();
            foreach (var (num, md, expected) in SpecExamples(specPath))
            {
                total++;
                foreach (string frag in RawFragments(MdParser.Parse(md))) Record("spec#" + num, frag);
                bool o = Pass(md, expected, false), nw = Pass(md, expected, true);
                if (o) passOld++;
                if (nw) passNew++;
                if (o != nw) flipped.Add($"#{num}:{(o ? "通过→失败" : "失败→通过")}");
            }
            report.AppendLine($"规范示例 {total} 个:黑名单通过 {passOld},白名单通过 {passNew}");
            if (flipped.Count > 0) report.AppendLine("  状态变化: " + string.Join(" ", flipped));
        }

        report.AppendLine($"文档 {docs} 份,原始 HTML 片段 {fragments} 个,输出不同 {changed} 个(其中仅规范化 {canonicalOnly} 个)");
        foreach (var (note, count) in tally)
        {
            report.AppendLine($"  {note}  ×{count}");
            foreach (string ex in examples[note]) report.AppendLine(ex);
        }

        // 攻击样例:导出两份,注入标记函数与自动点击,供浏览器判定
        foreach (var (name, md) in Attacks)
        {
            File.WriteAllText(Path.Combine(outDir, "old-" + name + ".html"), Probe(md, false));
            File.WriteAllText(Path.Combine(outDir, "new-" + name + ".html"), Probe(md, true));
        }
        File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
        Console.Write(report.ToString());
        return 0;

        bool Pass(string md, string expected, bool allow)
        {
            bool prev = HtmlExport.UseAllowlist;
            HtmlExport.UseAllowlist = allow;
            try { return specNorm(specRender(md)) == specNorm(expected); }
            catch (Exception) { return false; }
            finally { HtmlExport.UseAllowlist = prev; }
        }
    }

    internal static string Probe(string md, bool allow)
    {
        bool prev = HtmlExport.UseAllowlist;
        HtmlExport.UseAllowlist = allow;
        string html;
        try { html = HtmlExport.Build(MdParser.Parse(md), "probe", false); }
        finally { HtmlExport.UseAllowlist = prev; }
        const string mark = "<script>function P(){document.documentElement.setAttribute('data-pwned','1')}</script>";
        const string click = "<script>addEventListener('load',function(){setTimeout(function(){document.querySelectorAll('a,button').forEach(function(e){try{e.dispatchEvent(new MouseEvent('click',{bubbles:true,cancelable:true}))}catch(_){}})},300)})</script>";
        return html.Replace("<head>\n", "<head>\n" + mark + "\n").Replace("</body>", click + "\n</body>");
    }

    // 与 HtmlExport 的发射路径一致:Opaque 行内片段 + 未被整块拦截的 HTML 块
    internal static IEnumerable<string> RawFragments(List<Block> blocks)
    {
        foreach (var b in blocks)
        {
            switch (b)
            {
                case Heading h: foreach (var s in Opaque(h.Inlines)) yield return s; break;
                case Para p: foreach (var s in Opaque(p.Inlines)) yield return s; break;
                case Quote q: foreach (var s in Opaque(q.Inlines)) yield return s; break;
                case HtmlDetails d: foreach (var s in Opaque(d.Summary)) yield return s; break;
                case ListItem li:
                    foreach (var s in Opaque(li.Inlines)) yield return s;
                    if (li.Sub != null) foreach (var s in RawFragments(li.Sub)) yield return s;
                    break;
                case Table t:
                    foreach (var cell in t.Header ?? Array.Empty<List<Run>>()) foreach (var s in Opaque(cell)) yield return s;
                    foreach (var row in t.Rows ?? Array.Empty<List<Run>[]>()) foreach (var cell in row) foreach (var s in Opaque(cell)) yield return s;
                    break;
                case HtmlBlock hb:
                    string text = hb.Text.TrimEnd('\n');
                    if (!new[] { "<script", "<style", "<iframe", "<object", "<embed" }.Any(tag => text.Contains(tag, StringComparison.OrdinalIgnoreCase)))
                        yield return text;
                    break;
            }
        }
    }

    private static IEnumerable<string> Opaque(List<Run> runs)
    {
        if (runs == null) yield break;
        foreach (var r in runs) if (r.Opaque) yield return r.Text;
    }

    private static IEnumerable<(int, string, string)> SpecExamples(string path)
    {
        var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');
        string fence = new string('`', 32);
        int num = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!(lines[i].StartsWith(fence) && lines[i].Contains(" example") && !lines[i].Contains('{'))) continue;
            var body = new List<string>();
            i++;
            while (i < lines.Length && !lines[i].StartsWith(fence)) { body.Add(lines[i]); i++; }
            string joined = string.Join("\n", body) + "\n";
            int sep = joined.IndexOf("\n.\n", StringComparison.Ordinal);
            if (sep < 0) continue;
            num++;
            yield return (num, joined[..(sep + 1)].Replace("→", "\t"), joined[(sep + 3)..].Replace("→", "\t"));
        }
    }

    private static string Clip(string s)
    {
        s = s.Replace("\n", "⏎");
        return s.Length <= 160 ? s : s[..160] + "…";
    }
}
