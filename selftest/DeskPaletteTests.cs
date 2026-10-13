using System;
using System.Collections.Generic;
using System.Linq;
using MDViewerNative;

// U11：命令面板（纯逻辑：条目目录、匹配、分组版面、命中、滚动）。
internal static class DeskPaletteTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "desk palette") => check("DeskPalette/" + name, ok, detail);
        static bool Near(float a, float b) => MathF.Abs(a - b) < 0.01f;

        var state = new CmdState(false, false, false, false, false, 17f);
        var catalog = CommandSearch.Catalog(Commands.All, state, null, new[] { new PaletteEntry(PaletteKind.Command, 2301, "纸张：宣纸", "", "白天") });
        C("catalog skips here-and-now commands and itself", !catalog.Any(e => e.Id is 2001 or 2005 or 2002 or 2003 or CommandSearch.PaletteCommand) && catalog.Any(e => e.Id == 1005) && catalog.Any(e => e.Id == CommandSearch.HomeCommand));
        C("catalog keeps extras last", catalog[^1].Id == 2301 && catalog[^1].Sub == "白天");
        var home = CommandSearch.Catalog(Commands.All, state with { Home = true }, null, null);
        var find = home.First(e => e.Id == 1005);
        C("disabled commands carry their reason", !find.Enabled && find.Sub == "起始页上不可用" && home.First(e => e.Id == 1003).Enabled && home.First(e => e.Id == 1003).Sub == null);
        C("label override", CommandSearch.Catalog(Commands.All, state, c => c.Id == 1002 ? "回到阅读" : c.Label, null).First(e => e.Id == 1002).Label == "回到阅读");
        C("palette command is registered with Ctrl+K", Commands.All.Any(c => c.Id == CommandSearch.PaletteCommand && c.Accel == "Ctrl+K" && c.Face == CmdFace.None));

        var cmds = new List<PaletteEntry>
        {
            new(PaletteKind.Command, 1, "打开文件", "Ctrl+O"),
            new(PaletteKind.Command, 2, "查找", "Ctrl+F"),
            new(PaletteKind.Command, 3, "目录", "Ctrl+T"),
            new(PaletteKind.Command, 4, "导出 HTML", ""),
        };
        var recent = Enumerable.Range(0, 8).Select(i => new PaletteEntry(PaletteKind.Recent, i, $"note{i}.md", "", @"D:\docs", true, $@"D:\docs\note{i}.md")).ToList();
        C("empty query: all commands + 3 recent", CommandSearch.Filter(cmds, recent, "").Count == 4 + 3);
        C("query caps recent at 6", CommandSearch.Filter(cmds, recent, "note").Count == 6);
        C("name substring, case-insensitive", CommandSearch.Filter(cmds, recent, "html").Single().Id == 4);
        C("shortcut match ignores + and spaces", CommandSearch.Filter(cmds, recent, "ctrl f").Single().Id == 2 && CommandSearch.Filter(cmds, recent, "Ctrl+T").Single().Id == 3 && CommandSearch.Filter(cmds, recent, "ctrlo").Single().Id == 1);
        C("a bare letter does not match shortcuts", CommandSearch.Filter(cmds, recent, "x").Count == 0 && CommandSearch.LooksLikeKey("F11") && !CommandSearch.LooksLikeKey("find") && CommandSearch.LooksLikeKey("+s"));
        C("recent entries match by name only", CommandSearch.Filter(cmds, recent, "docs").Count == 0);
        C("highlight", CommandSearch.Highlight("导出 HTML", "html") == (3, 4) && CommandSearch.Highlight("查找", "ctrl") == (-1, 0) && CommandSearch.Highlight("查找", " ") == (-1, 0));
        C("step wraps", CommandSearch.Step(0, 5, -1) == 4 && CommandSearch.Step(4, 5, 1) == 0 && CommandSearch.Step(0, 0, 1) == 0);

        var m = new PaletteMeasure(InputLine: 21f, RowLine: 20f, GroupLine: 16f, EmptyLine: 18f);
        var items = CommandSearch.Filter(cmds, recent, "");
        var f = CommandSearch.Layout(1440f, 900f, items, m);
        C("panel: 580 wide, centered, 13% down", Near(f.W, 580f) && Near(f.X, 430f) && Near(f.Y, 117f) && Near(f.InputH, 15f * 2f + 21f + 1f));
        float gh = 10f + 16f + 6f, rh = 9f * 2f + 20f;
        C("groups and rows", f.Rows.Count == 2 + 7 && f.Rows[0].Header == "命令" && f.Rows[5].Header == "最近文件" && Near(f.Rows[1].Y, 8f + gh) && Near(f.Rows[6].Y, 8f + gh * 2f + rh * 4f) && f.Rows[6].Entry == 4);
        C("list fits content under half the height", Near(f.ContentH, 8f + gh * 2f + rh * 7f + 8f) && Near(f.ListH, f.ContentH));
        var narrow = CommandSearch.Layout(500f, 400f, items, m);
        C("narrow: 92% wide, list capped at half height", Near(narrow.W, 460f) && Near(narrow.ListH, 200f));
        C("reveal scrolls down to the last row and back to top", Near(CommandSearch.Reveal(narrow, 6, 0f), narrow.ContentH - 200f) && Near(CommandSearch.Reveal(narrow, 0, 80f), 0f));
        C("reveal keeps a visible row still", Near(CommandSearch.Reveal(narrow, 1, 0f), 0f));
        C("hit rows, not headers", CommandSearch.Hit(f, f.X + 20f, f.ListY + f.Rows[1].Y + 3f, 0f) == 0 && CommandSearch.Hit(f, f.X + 20f, f.ListY + f.Rows[0].Y + 3f, 0f) == -1
            && CommandSearch.Hit(f, f.X - 1f, f.ListY + f.Rows[1].Y + 3f, 0f) == -1 && CommandSearch.Hit(narrow, narrow.X + 5f, narrow.ListY + 190f, narrow.ContentH - 200f) == 6);
        C("in panel", CommandSearch.InPanel(f, f.X + 1f, f.Y + 1f) && !CommandSearch.InPanel(f, f.X + 1f, f.Y + f.H + 1f));
        var none = CommandSearch.Layout(1440f, 900f, new List<PaletteEntry>(), m);
        C("empty result", none.Empty && none.Rows.Count == 0 && Near(none.ContentH, 26f * 2f + 18f));
    }
}
