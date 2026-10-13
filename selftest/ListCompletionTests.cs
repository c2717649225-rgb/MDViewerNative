using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MDViewerNative;

internal static unsafe class ListCompletionTests
{
    private static string Html(string md) => HtmlExport.Build(MdParser.Parse(md), "list tests", false);
    private static int Count(string text, string needle) => text.Split(needle).Length - 1;

    internal static void Run(Action<string, bool, string> check)
    {
        check("Lists/structured body table export retains content", Html("- # title\n\n  | key | value |\n  | --- | --- |\n  | a | b |\n").Contains("<td>b</td>"), "shared table exporter, not a dropped child block");
        check("Lists/structured body image export retains content", Html("- # title\n\n  ![alt](image.png)\n").Contains("alt=\"alt\""), "shared image exporter");
        var heading = MdParser.Parse("- # heading\n- next\n").OfType<ListItem>().ToArray();
        check("Lists/first ATX heading", heading.Length == 2 && heading[0].Inlines.Count == 0 && heading[0].Sub?[0] is Heading { Level: 1 }, "native model, not export-only");
        check("Lists/first Setext heading", Html("- title\n  -----\n- next\n").Contains("<h2 id=\"title\">title</h2>"), "underline belongs to item");
        check("Lists/first nested marker", Html("- - child\n  - second\n- peer\n").Contains("<li><ul>\n<li>child"), "empty parent retains nested group");
        var tail = MdParser.Parse("- parent\n  - child\n\n  tail\n- peer\n").OfType<ListItem>().ToArray();
        check("Lists/parent tail stays in source order", tail.Length == 4 && tail[1].Nest == 1 && tail[2].Continuation && tail[2].Nest == 0, "flat model retains parent continuation after child");
        string tailHtml = Html("- parent\n  - child\n\n  tail\n- peer\n");
        check("Lists/parent tail shares li", Count(tailHtml, "<li") == 3 && tailHtml.Contains("</ul>\n<p>tail</p>"), "no synthetic list item for continuation");
        var groups = MdParser.Parse("- a\n\n- b\n+ c\n* d\n1. e\n2) f\n").OfType<ListItem>().ToArray();
        check("Lists/marker identity splits groups", groups.Select(x => x.ListGroup).Distinct().Count() == 5 && groups[0].ListGroup == groups[1].ListGroup, "bullet symbol and ordered delimiter identity");
        check("Lists/looseness is group-local", groups.Take(2).All(x => x.Loose) && groups.Skip(2).All(x => !x.Loose), "separate groups do not inherit blank separators");
        check("Lists/group boundary spacing", groups.Take(5).Count(x => x.GroupBreakAfter) == 4, "one visual boundary after each finished group");
        check("Lists/numeric values preserved", Html("3) three\n7) seven\n1. one\n").Contains("<li value=\"7\">"), "delimiter grouping does not normalize item values");
        foreach (string marker in new[] { "```", "~~~~" })
        {
            string md = "[r]: https://example.org/ref\n\n- " + marker + "text\n  [local]: /literal\n  [r]\n  - literal marker\n\n  " + marker + "\n- sibling\n";
            var list = MdParser.Parse(md).OfType<ListItem>().ToArray();
            var code = list.FirstOrDefault()?.Sub?.OfType<CodeBlock>().FirstOrDefault();
            check("Lists/first fence protects source " + marker, code?.Text == "[local]: /literal\n[r]\n- literal marker\n\n", "definitions, links, markers and terminal blank retained");
            check("Lists/fence blank is not looseness " + marker, list.Length == 2 && list.All(x => !x.Loose), "literal blank is not a separator");
            check("Lists/export keeps fence terminal blank " + marker, Html(md).Contains("- literal marker\n\n</code>"), "do not TrimEnd code payload");
        }
        string longer = "[r]: /target\n\n- ````\n  ```\n  [r]\n  ````` not a close\n  [x]: /literal\n  `````\n- end\n";
        var longCode = MdParser.Parse(longer).OfType<ListItem>().First().Sub.OfType<CodeBlock>().Single();
        check("Lists/fence length and closing syntax", longCode.Text.Contains("[r]\n````` not a close\n[x]: /literal"), "shorter fence and trailing text do not close");
        string quoted = "[r]: /target\n\n> - ```\n>   [r]\n>   [x]: /literal\n>   ```\n";
        check("Lists/quoted first fence reference opacity", MdParser.Parse(quoted).OfType<ListItem>().Single().Sub.OfType<CodeBlock>().Single().Text == "[r]\n[x]: /literal\n", "quote prefixes stripped only for scanning");
        string nested = "[r]: /target\n\n- - ```\n    [r]\n    [x]: /literal\n    ```\n";
        check("Lists/nested first fence reference opacity", MdParser.Parse(nested).OfType<ListItem>().Last().Sub.OfType<CodeBlock>().Single().Text == "[r]\n[x]: /literal\n", "multiple same-line markers");
        var unclosed = MdParser.Parse("- ```\n  code\n\noutside\n");
        check("Lists/unclosed fence stops at container dedent", unclosed.Count == 2 && unclosed[1] is Para, "outer paragraph remains outside item");
        check("Lists/fence EOF has no phantom blank", MdParser.Parse("- ```\n  code\n").OfType<ListItem>().Single().Sub.OfType<CodeBlock>().Single().Text == "code\n", "terminal split sentinel is not code data");
        string manyBlanks = "- ```\n  a\n" + new string('\n', 12000) + "  ```\n- next\n";
        check("Lists/long blank run preserves content", MdParser.Parse(manyBlanks).OfType<ListItem>().First().Sub.OfType<CodeBlock>().Single().Text.Length == 12002, "blank scan consumes each run once");
        string deep = string.Concat(Enumerable.Repeat("- ", 130)) + "end\n";
        check("Lists/structured nesting is bounded", MdParser.Parse(deep).Count <= 102, "same depth guard as existing parser");
        using var view = new DocView();
        view.Load("- # [Heading](https://example.org/h)\n\n  text [link](https://example.org/p)\n\n  ```\n  code\n\n  ```\n- peer\n");
        view.OnWidth(596f);
        string all = view.SearchText();
        view.SelectRange(0, view.TotalChars);
        check("Lists/native sub-only copy has no phantom header", all.StartsWith("Heading\ntext link\ncode\n") && view.CopyText() == all, "shared search/copy text domain");
        NativeSubHit(view, 0, 1, "https://example.org/h", check);
        NativeSubHit(view, 1, 6, "https://example.org/p", check);
        NativeSubHit(view, 2, 1, null, check);
        // 自用走查:项内代码块底板只外扩 2 DIP、文字距左缘 6 DIP,和正文代码块(r6、约 20/20、左 22)相比局促;
        // 统一为上下 10、左右 14 的内边距。子块纵向位置由 SubYs 单一来源给出(绘制/命中/选区/锚点共用)。
        var subYs = EntryField<List<float>>(view, 0, "SubYs");
        var subHs = EntryField<List<float>>(view, 0, "SubHeights");
        var subXs = EntryField<List<float>>(view, 0, "SubXs");
        check("Lists/nested code horizontal padding", subXs != null && subXs[2] == 14f, subXs == null ? "none" : string.Join(",", subXs));
        check("Lists/nested code vertical padding", subYs != null && MathF.Abs(subYs[2] - (subYs[1] + subHs[1]) - 16f) < 0.01f, subYs == null ? "no SubYs" : $"gap={subYs[2] - (subYs[1] + subHs[1])}");
        check("Lists/item height covers nested code padding", subYs != null && EntryField<float>(view, 0, "H") >= subYs[2] + subHs[2] + 10f - 0.01f, subYs == null ? "no SubYs" : $"H={EntryField<float>(view, 0, "H")} need={subYs[2] + subHs[2] + 10f}");
        view.Load("- parent\n  - child\n\n  tail\n- peer\n");
        view.OnWidth(360f);
        all = view.SearchText();
        check("Lists/native parent tail search order", all == "parent\nchild\ntail\npeer", all.Replace('\n', '|'));
        int start = all.IndexOf("child", StringComparison.Ordinal);
        view.SelectRange(start, all.IndexOf("peer", StringComparison.Ordinal) - 1);
        check("Lists/native cross-child selection", view.CopyText() == "child\ntail", "continuation is markerless, selectable text");
        view.Load("- - child\n  - second\n- peer\n");
        view.OnWidth(360f);
        check("Lists/native empty parent shares child row", view.SearchText() == "child\nsecond\npeer" && view.PosAtText(view.ColX + 52f, view.YAtOffset(0) + 10f).block == 1, "no empty header row or copy newline");
        var last = MdParser.Parse("- # title\n  - a\n\n  - b\n- next\n").OfType<ListItem>().ToArray();
        check("Lists/heading then loose child does not loosen parent", last.Where(x => x.Nest == 0).All(x => !x.Loose) && last.Where(x => x.Nest == 1).All(x => x.Loose), "blank ownership follows child group");
    }

    private static T EntryField<T>(DocView view, int block, string name)
    {
        object entry = typeof(DocView).GetMethod("GetEnt", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, new object[] { block });
        var field = entry.GetType().GetField(name);
        return field == null ? default : (T)field.GetValue(entry);
    }

    private static void NativeSubHit(DocView view, int sub, int pos, string link, Action<string, bool, string> check)
    {
        object entry = typeof(DocView).GetMethod("GetEnt", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, new object[] { 0 });
        T Field<T>(string name) => (T)entry.GetType().GetField(name).GetValue(entry);
        nint layout = Field<List<nint>>("SubLayouts")[sub];
        var offsets = Field<List<int>>("SubStarts");
        var heights = Field<List<float>>("SubHeights");
        var texts = Field<List<string>>("SubTexts");
        float top = Field<float>("SubTop");
        check("Lists/native first sub starts at item top " + sub, top == 0f, "no empty paragraph row");
        DWRITE_HIT_TEST_METRICS metric = default;
        uint count = 0;
        int hr = Dw.HitRange(layout, (uint)pos, 1u, 0f, 0f, &metric, 1u, &count);
        float x = view.ColX + view.Blocks[0].XOff + 26f + Field<List<float>>("SubXs")[sub] + metric.left + metric.width * 0.25f;
        float y = view.YAtOffset(0) + Field<List<float>>("SubYs")[sub] + metric.top + metric.height * 0.5f;
        var hit = view.PosAtText(x, y);
        check("Lists/native sub glyph hit " + sub, hr >= 0 && count == 1 && hit.block == 0 && hit.pos == offsets[sub] + pos, $"hr={hr} count={count} hit={hit} expected={offsets[sub] + pos}; x={x} y={y}");
        check("Lists/native sub link hit " + sub, view.LinkAt(x, y) == link, "sub offsets are global within item; code cannot inherit links");
        view.SelectRange(offsets[sub] + pos, offsets[sub] + pos + 2);
        check("Lists/native sub precise selection " + sub, view.CopyText() == texts[sub].Substring(pos, 2), "no clipboard mutation");
    }
}
