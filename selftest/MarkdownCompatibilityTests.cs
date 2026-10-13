using System;
using System.Linq;
using MDViewerNative;

internal static class MarkdownCompatibilityTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        string[] Links(string md) => MdParser.Parse(md).OfType<Para>().SelectMany(p => p.Inlines)
            .Where(r => r.Link != null).Select(r => r.Link).ToArray();
        string Plain(string md) => string.Join("\n", MdParser.Parse(md).OfType<Para>()
            .Select(p => string.Concat(p.Inlines.Select(r => r.Text))));
        // 软换行(自用走查 S75:d2l 一句一行曾逐句另起一行):显示为空格,两侧都是中日文字符时不补;
        // 硬换行(行尾两个以上空格、行尾反斜杠、<br>)保持换行。同 GitHub 渲染 .md 与 CSS Text 3 换行变换。
        foreach (var (md, want) in new[] {
            ("aaa\nbbb\n", "aaa bbb"), ("a \nb\n", "a b"), ("中文\n继续\n", "中文继续"), ("房龄\n（年）来估算\n", "房龄（年）来估算"),
            ("set）\n或\n", "set）或"), ("set)\n或\n", "set) 或"), ("English\n中文\n", "English 中文"), ("**粗**\n中文\n", "粗中文"),
            ("a  \nb\n", "a\nb"), ("a\\\nb\n", "a\nb"), ("a<br>b\n", "a\nb"), ("中  \n文\n", "中\n文") })
            check("Markdown/soft break " + md.Replace("\n", "⏎"), Plain(md) == want, Plain(md).Replace("\n", "⏎"));
        string ItemText(string md) => string.Join("|", MdParser.Parse(md).OfType<ListItem>().Select(li => string.Concat(li.Inlines.Select(r => r.Text))));
        check("Markdown/soft break in list item", ItemText("- 中\n  文\n- a\n  b\n") == "中文|a b", ItemText("- 中\n  文\n- a\n  b\n"));
        check("Markdown/hard break in list item", ItemText("- a  \n  b\n") == "a\nb", ItemText("- a  \n  b\n").Replace("\n", "⏎"));
        check("Markdown/soft break export", HtmlExport.Build(MdParser.Parse("aaa\nbbb\n"), "t", false).Contains("<p>aaa bbb</p>"), "not <br>");
        // Obsidian 提示框(S75,自用走查 Obsidian 帮助文档):类型大小写不敏感,可带 +/- 折叠标记与自定义标题;类型归并到现有五种。
        string Callout(string md) => string.Join(" / ", MdParser.Parse(md).OfType<Quote>().Select(q =>
            (q.AlertTitle ? "T:" : "B:") + q.Alert + ":" + string.Concat(q.Inlines.Select(r => r.Text)) + (q.AlertTitle && q.Inlines.All(r => r.B) ? "" : q.AlertTitle ? "(not bold)" : "")));
        foreach (var (md, want) in new[] {
            ("> [!info] Here's a callout title\n> Body\n", "T:note:Here's a callout title / B:note:Body"),
            ("> [!tip]\n> x\n", "T:tip:Tip / B:tip:x"),
            ("> [!faq]- Are callouts foldable?\n> Yes\n", "T:note:Are callouts foldable? / B:note:Yes"),
            ("> [!WARNING]\n> x\n", "T:warning:Warning / B:warning:x"),
            ("> [!danger]+ Boom\n> x\n", "T:caution:Boom / B:caution:x"),
            ("> [!success] Title with **bold**\n", "T:tip:Title with bold"),
            ("> [x] not a callout\n", "B::[x] not a callout"),
            ("> [!] nope\n", "B::[!] nope") })
            check("Markdown/callout " + md.Replace("\n", "⏎"), Callout(md) == want, Callout(md));
        var nested = MdParser.Parse("> [!question] Q\n> > [!todo] T\n> > body\n").OfType<Quote>().Where(q => q.AlertTitle).Select(q => string.Concat(q.Inlines.Select(r => r.Text))).ToArray();
        check("Markdown/nested callouts", nested.SequenceEqual(new[] { "Q", "T" }), string.Join("|", nested));
        // Obsidian 双链(S75):显示别名或页面名,目标为 页面.md(无扩展名补 .md)加标题锚点;![[图片]] 为本地图片。
        string sec = DocumentAnchors.Slug("Section One"), sup = DocumentAnchors.Slug("Supported types");
        foreach (var (md, link, text) in new[] {
            ("[[Page]]\n", "Page.md", "Page"),
            ("[[Page|Alias]]\n", "Page.md", "Alias"),
            ("[[My Note#Section One]]\n", "My%20Note.md#" + sec, "My Note › Section One"),
            ("[[#Supported types]]\n", "#" + sup, "Supported types"),
            ("[[folder/doc.pdf]]\n", "folder/doc.pdf", "folder/doc.pdf"),
            ("![[Other note]]\n", "Other%20note.md", "Other note"),
            ("see [[中文 页面|别名]] ok\n", "%E4%B8%AD%E6%96%87%20%E9%A1%B5%E9%9D%A2.md", "see 别名 ok") })
            check("Markdown/wikilink " + md.TrimEnd(), Links(md).SequenceEqual(new[] { link }) && Plain(md) == text, string.Join(",", Links(md)) + " | " + Plain(md));
        foreach (var (md, src) in new[] { ("![[Engelbart.jpg]]\n", "Engelbart.jpg"), ("![[pic.png|300]]\n", "pic.png") })
        {
            var img = MdParser.Parse(md).OfType<Para>().SelectMany(p => p.Inlines).Where(r => r.Img).Select(r => r.Link).ToArray();
            check("Markdown/wikilink embed image " + src, img.SequenceEqual(new[] { src }), string.Join(",", img));
        }
        foreach (string md in new[] { "`[[x]]`\n", "[[]]\n", "[[a\nb]]\n" })
            check("Markdown/not a wikilink " + md.Replace("\n", "⏎"), Links(md).Length == 0, string.Join(",", Links(md)));
        check("Markdown/defined reference wins over wikilink", Links("[[foo]]\n\n[foo]: /url\n").SequenceEqual(new[] { "/url" }), string.Join(",", Links("[[foo]]\n\n[foo]: /url\n")));
        // 引用式链接包引用式图片(徽章常见写法;S74 走查 Best-README-Template):内层引用同样替换。
        var badge = MdParser.Parse("[![A][s]][u]\n\n[s]: http://x/s.svg\n[u]: http://x/u\n").OfType<Para>().SelectMany(p => p.Inlines).ToList();
        check("Markdown/reference link wraps reference image", badge.Count == 1 && badge[0].Img && badge[0].ImageAlt == "A" && badge[0].Link == "http://x/s.svg" && badge[0].LinkOuter == "http://x/u",
            string.Join(" | ", badge.Select(r => (r.Img ? "IMG " : "T ") + r.Text + " " + r.Link + " " + r.LinkOuter)));
        // 斜体公式紧跟收尾标点:补极细空格作斜体校正(S74;mc², 的上标曾压住逗号)。空格或文字之后不补。
        check("Markdown/inline math gets italic correction before punctuation", Plain("$E=mc^2$, $a$。$b$ c\n") == "E=mc\u00B2\u2006, a\u2006。b c", Plain("$E=mc^2$, $a$。$b$ c\n").Replace("\u2006", "<hair>"));
        // emoji 短码按 GitHub gemoji(自用走查 S73:HelloGitHub 表头 :card_index: 等曾为字面)。
        check("Markdown/emoji shortcodes cover GitHub gemoji", Plain(":card_index: :jack_o_lantern: :fish_cake: :woman_technologist:\n") == "\U0001F4C7 \U0001F383 \U0001F365 \U0001F469\u200D\U0001F4BB", Plain(":card_index: :jack_o_lantern: :fish_cake: :woman_technologist:\n"));
        check("Markdown/emoji names match GitHub", Plain(":sweat: :pencil: :cake:\n") == "\U0001F613 \U0001F4DD \U0001F370", Plain(":sweat: :pencil: :cake:\n"));
        check("Markdown/non-GitHub emoji names stay literal", Plain(":check: :octocat:\n") == ":check: :octocat:", "octocat is a GitHub image, not Unicode");
        check("Markdown/emoji shortcode not in code span", Plain("`:smile:` :smile:\n") == ":smile: \U0001F604", Plain("`:smile:` :smile:\n"));
        // 链接文字本身是网址时仍是一个链接(GFM);裸网址不算“链接内嵌链接”。
        check("Markdown/url as link text forms one link", Links("[https://a.com/x](https://a.com/x)\n").SequenceEqual(new[] { "https://a.com/x" }) && Plain("[https://a.com/x](https://a.com/x)\n") == "https://a.com/x", "no literal brackets");
        // GFM 自动链接:末尾标点不属于链接。
        check("Markdown/autolink excludes trailing punctuation", Links("see https://a.com/x. and https://b.org/y?, ok\n").SequenceEqual(new[] { "https://a.com/x", "https://b.org/y" }), string.Join(" ", Links("see https://a.com/x. and https://b.org/y?, ok\n")));
        check("Markdown/label whitespace does not merge words", Links("[a b] [ab]\n\n[a b]: /spaced\n[ab]: /joined\n").SequenceEqual(new[] { "/spaced", "/joined" }), "distinct destinations");
        check("Markdown/label whitespace collapses and trims", Links("[x][ A\t  B ]\n\n[a b]: /match\n").SequenceEqual(new[] { "/match" }), "case and whitespace normalization only");
        check("Markdown/escaped label spelling stays distinct", Links("[bar][foo\\!]\n\n[foo!]: /url\n").Length == 0 && Plain("[bar][foo\\!]\n\n[foo!]: /url\n") == "[bar][foo!]", "CommonMark 547; escape decoded for display, not lookup");
        check("Markdown/matching escaped label resolves", Links("[foo\\!]\n\n[foo\\!]: /escaped\n").SequenceEqual(new[] { "/escaped" }), "identical raw label spelling");
        check("Markdown/entity labels stay distinct", Links("[a&amp;b] [a&b]\n\n[a&amp;b]: /entity\n[a&b]: /literal\n").SequenceEqual(new[] { "/entity", "/literal" }), "do not decode entities during reference matching");
        foreach (string label in new[] { "ref[bar]", "[[foo]]", "[foo]" })
        {
            string md = "[text][" + label + "]\n\n[" + label + "]: /url\n";
            // 无效定义不生效、不被吞掉;S75 起行首 [[foo]] 按 Obsidian 双链显示,故不再要求“无任何链接”。
            check("Markdown/invalid nested label " + label, !Links(md).Contains("/url") && Plain(md).Contains(": /url"), "invalid definition remains visible, not silently consumed");
        }
        check("Markdown/escaped brackets remain valid", Links("[text][a\\[b\\]]\n\n[a\\[b\\]]: /bracket\n").SequenceEqual(new[] { "/bracket" }), "escaped brackets are label content");
        foreach (int length in new[] { 999, 1000 })
        {
            string label = new string('a', length);
            check("Markdown/label length " + length, Links("[x][" + label + "]\n\n[" + label + "]: /limit\n").Length == (length == 999 ? 1 : 0), "999-character boundary");
        }
        string astralLabel = string.Concat(Enumerable.Repeat("😀", 999));
        check("Markdown/label length counts Unicode scalars", Links("[x][" + astralLabel + "]\n\n[" + astralLabel + "]: /scalar\n").Length == 1, "surrogate pairs count once");
        check("Markdown/blank label is not consumed", Plain("[   ]: /url\n").Contains(": /url"), "definition requires non-whitespace content");
        string symbol = char.ConvertFromUtf32(0x1E2FF);
        foreach (string value in new[] { "*" + symbol + "*alpha.", "a_𐐀_b", "*\uD800*tail" })
        {
            var runs = MdParser.Parse(value).OfType<Para>().SelectMany(p => p.Inlines).ToArray();
            check("Markdown/Unicode delimiter boundary " + value, runs.Length > 0 && !runs.Any(r => r.I || r.B) && string.Concat(runs.Select(r => r.Text)) == value, "scalar-aware flanking; malformed UTF16 does not crash");
        }
        check("Markdown/ordinary emphasis retained", MdParser.Parse("*中文 😀* **bold**").OfType<Para>().SelectMany(p => p.Inlines).Any(r => r.I)
            && MdParser.Parse("**bold**").OfType<Para>().SelectMany(p => p.Inlines).Any(r => r.B), "positive formatting control");
        string output = HtmlExport.Build(MdParser.Parse("*" + symbol + "*alpha."), "test", false);
        check("Markdown/export preserves literal symbol delimiters", output.Contains("<p>*" + symbol + "*alpha.</p>"), "not only a normalized score");
    }
}
