using System;
using System.Collections.Generic;
using System.Linq;
using MDViewerNative;

// 阅读态 HTML 呈现(S71,docs/mcp-html-reading.md):白名单 HTML 块按等价 Markdown 显示,其余保持字面面板。
internal static class HtmlReadingTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        List<Block> View(string md) => HtmlReading.Apply(MdParser.Parse(md));
        string Kinds(List<Block> blocks) => string.Join(",", blocks.Select(b => b.GetType().Name));
        string Text(List<Run> runs) => string.Concat(runs.Select(r => r.Text));

        var comment = View("a\n\n<!-- 模板说明 -->\n\nb\n");
        check("HtmlReading/comment-only block is hidden", Kinds(comment) == "Para,Para", Kinds(comment));

        var header = View("<p align=\"center\">\n  <img src=\"logo.gif\"/>\n  <br>中文 | <a href=\"README_en.md\">English</a>\n</p>\n");
        check("HtmlReading/standalone image becomes image block", header.Count == 2 && header[0] is Image { Src: "logo.gif" }, Kinds(header));
        check("HtmlReading/text line becomes paragraph with link", header.Count == 2 && header[1] is Para hp && Text(hp.Inlines) == "中文 | English"
            && hp.Inlines.Any(r => r.Text == "English" && r.Link == "README_en.md"), header.Count == 2 && header[1] is Para hp2 ? Text(hp2.Inlines) : Kinds(header));

        var badges = View("<p align=\"center\">\n  <a href=\"https://s\"><img src=\"https://b.svg\" alt=\"GitHub stars\"></a>\n  <a href=\"https://i\"><img src=\"https://c.svg\" alt=\"issues\"></a>\n</p>\n");
        var imgs = badges.Count == 1 && badges[0] is Para bp ? bp.Inlines.Where(r => r.Img).ToList() : new List<Run>();
        check("HtmlReading/linked badges stay inline images", imgs.Count == 2 && imgs[0].Link == "https://b.svg" && imgs[0].LinkOuter == "https://s" && imgs[0].Text == "GitHub stars"
            && imgs[1].LinkOuter == "https://i", Kinds(badges) + " imgs=" + imgs.Count);

        var heading = View("<h1 align=\"center\">项目名</h1>\n");
        check("HtmlReading/html heading becomes heading", heading.Count == 1 && heading[0] is Heading { Level: 1 } h1 && Text(h1.Inlines) == "项目名", Kinds(heading));

        // S74:HTML 列表 → 列表项(Best-README-Template 目录写法);嵌套深度与 Markdown 列表一致(XOff = Nest × 20)。
        var toc = View("<ol start=\"3\">\n  <li><a href=\"#a\">About</a>\n    <ul>\n      <li><b>Built</b> With</li>\n    </ul>\n  </li>\n  <li>Usage</li>\n</ol>\n");
        string Items(List<Block> blocks) => string.Join(" | ", blocks.Select(b => b is ListItem li ? $"{li.Marker}/{li.Nest}/{li.XOff}/{(li.Continuation ? "c" : "")}{Text(li.Inlines)}" : b.GetType().Name));
        check("HtmlReading/html list becomes list items", Items(toc) == "3./0/0/About | •/1/20/Built With | 4./0/0/Usage"
            && toc[0] is ListItem ta && ta.Inlines.Any(r => r.Link == "#a") && toc.Count > 1 && toc[1] is ListItem tb && tb.Inlines.Any(r => r.B && r.Text == "Built"), Items(toc));
        var tail = View("<ul><li>a<ul><li>b</li></ul>tail</li></ul>\n");
        check("HtmlReading/text after sublist continues the item", Items(tail) == "•/0/0/a | •/1/20/b | •/0/0/ctail", Items(tail));
        check("HtmlReading/list with unknown block stays literal", View("<ul>\n<li><iframe src=\"x\"></iframe></li>\n</ul>\n") is [HtmlBlock], "outside whitelist");
        // S74:<details> 正文含 HTML 时按阅读态拍平为行(编号、缩进、链接);纯文本正文不动。
        const string detailsMd = "<details>\n<summary>目录</summary>\n<ol>\n<li><a href=\"#a\">About</a>\n<ul><li>Sub</li></ul>\n</li>\n</ol>\n</details>\n\n## A\n";
        var det = View(detailsMd);
        check("HtmlReading/details html body becomes reading lines", det.Count > 0 && det[0] is HtmlDetails { BodyRuns: not null } dd && Text(dd.BodyRuns) == "1. About\n    • Sub" && dd.BodyRuns.Any(r => r.Link == "#a"),
            det.Count > 0 && det[0] is HtmlDetails { BodyRuns: not null } dd2 ? Text(dd2.BodyRuns) : Kinds(det));
        check("HtmlReading/details plain body unchanged", View("<details>\n<summary>s</summary>\nline1\nline2\n</details>\n") is [HtmlDetails { BodyRuns: null }], "plain text keeps its lines");
        using (var dv = new DocView())
        {
            dv.Load(detailsMd);
            dv.OnWidth(800f);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            float colX = (float)typeof(DocView).GetField("_colX", flags).GetValue(dv);
            float y0 = ((float[])typeof(DocView).GetField("_ys", flags).GetValue(dv))[0];
            dv.ToggleDetailsAt(colX + 4f, y0 + 4f);
            bool hit = false;
            for (float y = y0; y < dv.TotalH && !hit; y += 3f)
                for (float x = colX; x < colX + 300f && !hit; x += 3f) hit = dv.LinkAt(x, y) == "#a";
            check("HtmlReading/details body link is clickable when open", hit, "anchor in expanded table of contents");
        }

        check("HtmlReading/iframe stays literal", View("<iframe src=\"about:blank\"></iframe>\n") is [HtmlBlock], "outside whitelist");

        // S72:HTML 表格转为表格块。赞助表(thead + th,无数据行)、贡献者表(无 th:首行占表头位但不加粗)、普通 th/td 表。
        var sponsor = View("<table>\n  <thead>\n    <tr>\n      <th align=\"center\"><a href=\"u\"><img src=\"a.png\" width=\"60px\"><br><sub>UCloud</sub><br><sub>聚合</sub></a></th>\n      <th align=\"center\">B</th>\n    </tr>\n  </thead>\n</table>\n");
        check("HtmlReading/header-only html table becomes table", sponsor is [Table { PlainHeader: false } st] && st.Header.Length == 2 && st.Rows.Length == 0 && st.Aligns[0] == 1
            && Text(st.Header[0]).Contains("UCloud") && st.Header[0].Any(r => r.Img && r.Link == "a.png" && r.LinkOuter == "u") && st.Header[0].Any(r => r.Text == "\n"), Kinds(sponsor));
        var people = View("<table>\n<tr>\n<td align=\"center\"><a href=\"https://g/a\"><img src=\"x.png\" alt=\"Alice\"/><br /><sub><b>Alice</b></sub></a></td>\n<td>Bob</td>\n</tr>\n<tr><td>c</td><td>d</td></tr>\n</table>\n");
        check("HtmlReading/headerless html table keeps first row plain", people is [Table { PlainHeader: true } pt] && pt.Header.Length == 2 && pt.Rows.Length == 1 && pt.Header[0].Any(r => r.Img && r.Text == "Alice")
            && Text(pt.Rows[0][1]) == "d", Kinds(people));
        var data = View("<table><tr><th>名</th><th>值</th></tr><tr><td>a</td><td>1</td></tr><tr><td>b</td></tr></table>\n");
        check("HtmlReading/th row is header and short rows are padded", data is [Table { PlainHeader: false } dt] && Text(dt.Header[1]) == "值" && dt.Rows.Length == 2 && dt.Rows[1].Length == 2 && Text(dt.Rows[1][1]) == "", Kinds(data));
        check("HtmlReading/colspan table stays literal", View("<table><tr><td colspan=\"2\">x</td></tr></table>\n") is [HtmlBlock], "spans need a grid model");
        check("HtmlReading/nested table stays literal", View("<table><tr><td><table><tr><td>x</td></tr></table></td></tr></table>\n") is [HtmlBlock], "nested");
        check("HtmlReading/table cell with script stays literal", View("<table><tr><td><script>x</script></td></tr></table>\n") is [HtmlBlock], "outside whitelist");
        using (var tableView = new DocView())
        {
            tableView.Load("<table><thead><tr><th>A</th></tr></thead></table>\n\n段落\n");
            tableView.OnWidth(960f);
            check("HtmlReading/header-only table lays out", tableView.BlockCount == 2 && tableView.TotalH > 0f, $"blocks={tableView.BlockCount} h={tableView.TotalH}");
        }
        check("HtmlReading/script stays literal", View("<div>\n<script>alert(1)</script>\n</div>\n") is [HtmlBlock], "outside whitelist");

        var literal = View("<div>\n  *此处不解析为斜体*\n</div>\n");
        check("HtmlReading/text inside html is not markdown", literal.Count == 1 && literal[0] is Para lp && Text(lp.Inlines) == "*此处不解析为斜体*" && !lp.Inlines.Any(r => r.I), Kinds(literal));

        var entity = View("<p>A &amp; B<b>粗</b></p>\n");
        check("HtmlReading/entities decoded and styles kept", entity.Count == 1 && entity[0] is Para ep && Text(ep.Inlines) == "A & B粗" && ep.Inlines.Any(r => r.B && r.Text == "粗"), Kinds(entity));

        var picture = View("<picture>\n  <source media=\"(prefers-color-scheme: dark)\" srcset=\"dark.png\">\n  <img src=\"light.png\" alt=\"Logo\">\n</picture>\n");
        check("HtmlReading/picture uses img fallback", picture.Count == 1 && picture[0] is Image { Src: "light.png", Alt: "Logo" }, Kinds(picture));

        var parsed = MdParser.Parse("> <div>\n> <b>引用内</b>\n> </div>\n");
        var quoted = HtmlReading.Apply(parsed);
        check("HtmlReading/quote depth and source kept", quoted.Count == 1 && quoted[0] is Para { QuoteDepth: 1, XOff: 20 } qp && parsed.Count == 1 && qp.Source.Equals(parsed[0].Source) && qp.QSeq == parsed[0].QSeq,
            Kinds(quoted));

        var inline = View("文字 <img src=\"a.png\" alt=\"小图\"> 结尾 <a><bab>\n");
        var ir = inline.Count == 1 && inline[0] is Para ip ? ip.Inlines : new List<Run>();
        check("HtmlReading/inline img tag becomes image run", ir.Any(r => r.Img && r.Link == "a.png" && r.Text == "小图") && ir.Count(r => r.Opaque) == 2, string.Join("|", ir.Select(r => (r.Img ? "I:" : r.Opaque ? "O:" : "") + r.Text)));
    }
}
