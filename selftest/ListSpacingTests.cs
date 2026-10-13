using System;
using System.Linq;
using MDViewerNative;

internal static class ListSpacingTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        string loose = "- a\n- b\n\n  c\n- d\n";
        var items = MdParser.Parse(loose).OfType<ListItem>().ToArray();
        check("Spacing/second paragraph loosens all siblings", items.Length == 3 && items.All(x => x.Loose), "CommonMark 318");
        var nested = MdParser.Parse("- a\n  - b\n  - c\n\n- d\n  - e\n  - f\n").OfType<ListItem>().ToArray();
        check("Spacing/parent separator survives child parsing", nested.Length == 6 && nested.Where(x => x.Nest == 0).All(x => x.Loose), "CommonMark 328");
        check("Spacing/parent looseness does not leak into children", nested.Where(x => x.Nest == 1).All(x => !x.Loose), "child lists remain tight");
        foreach (string md in new[] { "* a\n  > b\n  >\n* c\n", "- a\n  > b\n  ```\n  c\n  ```\n- d\n", "- a\n  ```\n  b\n\n  ```\n- c\n" })
        {
            var list = MdParser.Parse(md).OfType<ListItem>().ToArray();
            check("Spacing/child block is not a blank separator", list.Length == 2 && list.All(x => !x.Loose), "quote/code internal blanks do not loosen parent");
            string html = HtmlExport.Build(list.Cast<Block>().ToList(), "t", false);
            check("Spacing/tight parent first paragraph stays unwrapped", html.Contains("<li>a\n") && !html.Contains("<li><p>a</p>"), "CommonMark 322/323 and fenced blank control");
        }
        var separated = MdParser.Parse("- a\n  > b\n\n- c\n").OfType<ListItem>().ToArray();
        check("Spacing/blank after child separates siblings", separated.Length == 2 && separated.All(x => x.Loose), "trailing child collection blank belongs to list");
        var outside = MdParser.Parse("- a\n  > b\n\noutside\n").OfType<ListItem>().ToArray();
        check("Spacing/blank before outside paragraph is not a list separator", outside.Length == 1 && !outside[0].Loose, "end-of-list negative control");
        using var view = new DocView();
        var afterNested = MdParser.Parse("- parent\n  - child\n\nparagraph\n\n    SELECT * FROM users\n    WHERE active = 1\n");
        check("Spacing/dedent restores top-level code parsing", afterNested.Last() is CodeBlock sql && sql.Text.TrimEnd('\n') == "SELECT * FROM users\nWHERE active = 1", "do not parse the rest of the document in a child-list context");
        view.Load(loose);
        view.OnWidth(596f);
        float height = view.TotalH;
        string text = view.SearchText();
        view.SelectRange(0, view.TotalChars);
        check("Spacing/native copy agrees with search", view.CopyText() == text && text.Contains("c"), "no clipboard mutation");
        foreach (var item in view.Blocks.OfType<ListItem>()) item.Loose = false;
        view.OnWidth(595f);
        check("Spacing/native layout applies all sibling gaps", MathF.Abs(height - view.TotalH - 27f) < 0.1f, "three list items, nine DIP each");
        check("Spacing/spacing changes do not change text", view.SearchText() == text, "layout metadata only");

        // 列表结束后的段落与列表的距离 = 段落间距(0.5L),不得小于列表项之间的距离(走查:设计方针 0.2 节)。
        float H(string md) { using var v = new DocView(); v.Load(md); v.OnWidth(596f); return v.TotalH; }
        const string twoItems = "- a\n- b\n", para = "after\n";
        float endGap = H(twoItems + "\n" + para) - H(twoItems) - H(para + "\n" + para) + H(para);
        check("Spacing/paragraph after a list keeps paragraph spacing", MathF.Abs(endGap) < 0.1f, $"list-end minus paragraph gap = {endGap:0.##} DIP");
        float itemGap = H("- a\n- b\n- c\n") - H("- a\n- b\n") - (H("- a\n- b\n") - H("- a\n"));
        check("Spacing/tight item gap unchanged by the list-end rule", MathF.Abs(itemGap) < 0.1f, $"third item adds the same as the second: {itemGap:0.##}");
    }
}
