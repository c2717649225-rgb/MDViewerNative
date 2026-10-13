using System;
using System.Collections.Generic;
using System.Linq;
using MDViewerNative;

// E18b：块的阅读态文本（排版、选区、复制、查找共用的文本域）从 DocView 搬到 BlockText 后的特征测试。
// 期望值按搬迁前的实现逐行推出；表格行长必须与 BlockTextOf 的表格行一致（选区按行长定位）。
internal static class BlockTextTests
{
    static List<Run> R(params string[] parts) => parts.Select(p => new Run(p)).ToList();

    internal static void Run(Action<string, bool, string> check)
    {
        void Eq(string name, string actual, string expected) =>
            check("BlockText/" + name, actual == expected, "actual=" + (actual ?? "null").Replace("\n", "\\n"));

        Eq("paragraph joins runs", BlockText.BlockTextOf(new Para { Inlines = R("a", "b") }), "ab");
        Eq("heading joins runs", BlockText.BlockTextOf(new Heading { Level = 2, Inlines = R("H", "1") }), "H1");
        Eq("quote joins runs", BlockText.BlockTextOf(new Quote { Inlines = R("q") }), "q");
        Eq("code expands tabs and trims every trailing newline", BlockText.BlockTextOf(new CodeBlock { Text = "x\ty\n\n" }), "x    y");
        Eq("literal html trims trailing newlines", BlockText.BlockTextOf(new HtmlBlock { Text = "<b>\tb</b>\n\n" }), "<b>    b</b>");
        Eq("image uses alt", BlockText.BlockTextOf(new Image { Alt = "pic", Src = "a.png" }), "pic");
        Eq("details without reading runs uses expanded source body", BlockText.BlockTextOf(new HtmlDetails { Summary = R("S"), Body = "b\tc" }), "S\nb    c");
        Eq("details prefers reading runs", BlockText.BlockTextOf(new HtmlDetails { Summary = R("S"), Body = "raw", BodyRuns = R("x", "y") }), "S\nxy");
        Eq("list item without children", BlockText.BlockTextOf(new ListItem { Inlines = R("item") }), "item");
        Eq("list item appends children on new lines", BlockText.BlockTextOf(new ListItem { Inlines = R("item"), Sub = new List<Block> { new CodeBlock { Text = "c\n" }, new Para { Inlines = R("p") } } }), "item\nc\np");
        Eq("empty list item starts with its first child", BlockText.BlockTextOf(new ListItem { Inlines = R(), Sub = new List<Block> { new Para { Inlines = R("p") }, new Para { Inlines = R("q") } } }), "p\nq");
        Eq("nested code drops only one trailing newline", BlockText.BlockTextOf(new ListItem { Inlines = R("i"), Sub = new List<Block> { new CodeBlock { Text = "c\t\n\n" } } }), "i\nc    \n");

        var table = new Table { Header = new[] { R("h1"), R("h", "2") }, Rows = new[] { new[] { R("a"), R("b") }, new[] { R("c") } } };
        string tableText = BlockText.BlockTextOf(table);
        Eq("table rows join cells and pad missing cells", tableText, "h1 | h2\na | b\nc | ");
        int[] lens = BlockText.TableRowLens(table);
        check("BlockText/table row lengths match the table text", lens.SequenceEqual(tableText.Split('\n').Select(l => l.Length)), string.Join(",", lens));
        Eq("header cell", BlockText.CellText(table, 0, 1), "h2");
        Eq("body cell", BlockText.CellText(table, 1, 0), "a");
        Eq("missing cell is empty", BlockText.CellText(table, 2, 1), "");
        Eq("tab expansion", BlockText.Vt("\ta\t"), "    a    ");
    }
}
