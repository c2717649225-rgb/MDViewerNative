using System;
using System.Collections.Generic;
using MDViewerNative;

// U8：目录卷目（演示稿 .juan）的条目、当前章、键盘移动与版式几何。
internal static class DeskJuanTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "desk juan") => check("DeskJuan/" + name, ok, detail);
        var heads = new List<(int Level, float Y, string Seal, string Title)>
        {
            (1, 0f, null, "书名"), (2, 400f, "一", "第一章"), (3, 900f, null, "小节"), (4, 1200f, null, "四级"), (2, 2070f, "二", "第二章"),
        };
        var items = Juan.Items(heads, 2000f);
        C("levels deeper than 3 are left out", items.Count == 4 && items.TrueForAll(i => i.Level <= 3), $"{items.Count}");
        C("percent uses the tick inset 70", items[1].Percent == 17 && items[0].Percent == 0 && items[3].Percent == 100, $"{items[1].Percent}");
        C("jump lands 60 above the head", items[1].JumpOffset == 340f && items[0].JumpOffset == 0f && items[3].JumpOffset == 2000f);
        C("seal null becomes empty", items[0].No == "" && items[1].No == "一");
        C("no scroll range gives zero percent", Juan.Percent(500f, 0f) == 0);

        var ys = new List<float> { 0f, 400f, 900f, 2070f };
        C("top of document: first entry is current", Juan.Current(ys, 0f) == 0);
        C("slack 94 counts a head just below the top", Juan.Current(ys, 306f) == 1 && Juan.Current(ys, 305f) == 0);
        C("far down picks the last passed head", Juan.Current(ys, 5000f) == 3);
        C("empty list has no current", Juan.Current(new List<float>(), 0f) == -1);

        C("down stops at the end", Juan.Move(3, 4, JuanKey.Down) == 3 && Juan.Move(1, 4, JuanKey.Down) == 2);
        C("up stops at the start", Juan.Move(0, 4, JuanKey.Up) == 0);
        C("home and end", Juan.Move(2, 4, JuanKey.Home) == 0 && Juan.Move(0, 4, JuanKey.End) == 3);
        C("nothing to move in an empty list", Juan.Move(0, 0, JuanKey.Down) == -1);

        var levels = new[] { 1, 2, 3, 2 };
        var text = new[] { 25f, 21f, 18f, 21f };
        var f = Juan.Layout(1000f, 800f, 32f, 17f, levels, text);
        C("column is 600 centered", f.ColumnW == 600f && f.ColumnX == 200f);
        C("narrow window keeps 40 side padding", Juan.Layout(500f, 800f, 32f, 17f, levels, text).ColumnW == 420f);
        C("title sits 9% down", f.TitleY == 72f);
        C("subtitle after title gap .4em", MathF.Abs(f.SubY - (72f + 32f + 9.6f)) < 0.01f, $"{f.SubY}");
        C("rows start after subtitle gap 2.6em", MathF.Abs(f.Rows[0].Y - (f.SubY + 17f + 32.5f)) < 0.01f, $"{f.Rows[0].Y}");
        C("row height is text plus 9 padding", f.Rows[1].H == 21f + 18f);
        C("level 1 leaves .5em below", MathF.Abs(f.Rows[1].Y - (f.Rows[0].Y + f.Rows[0].H + 9.5f)) < 0.01f);
        C("level 2 leaves nothing below", f.Rows[2].Y == f.Rows[1].Y + f.Rows[1].H);
        C("content ends with 80 padding", f.ContentHeight == f.Rows[3].Y + f.Rows[3].H + 80f);

        C("hit row inside the column", Juan.HitRow(f, 500f, f.Rows[2].Y + 2f, 0f) == 2);
        C("hit misses outside the column", Juan.HitRow(f, 150f, f.Rows[2].Y + 2f, 0f) == -1);
        C("hit follows scroll", Juan.HitRow(f, 500f, f.Rows[2].Y + 2f - 50f, 50f) == 2);
        C("gaps between level 1 and 2 are blank", Juan.HitRow(f, 500f, f.Rows[0].Y + f.Rows[0].H + 4f, 0f) == -1);

        C("short list does not scroll", Juan.MaxScroll(f, 800f) == 0f && Juan.InitialScroll(f, 3, 800f) == 0f);
        var many = new int[40]; var tall = new float[40];
        for (int i = 0; i < 40; i++) { many[i] = 2; tall[i] = 21f; }
        var g = Juan.Layout(1000f, 600f, 32f, 17f, many, tall);
        C("long list scrolls to its end", Juan.MaxScroll(g, 600f) == g.ContentHeight - 600f);
        float s = Juan.InitialScroll(g, 30, 600f);
        C("current row opens in view", g.Rows[30].Y >= s && g.Rows[30].Y + g.Rows[30].H <= s + 600f, $"{s}");
        float r = Juan.Reveal(g, 39, 0f, 600f);
        C("reveal brings the last row in with margin", MathF.Abs(g.Rows[39].Y + g.Rows[39].H + 16f - 600f - r) < 0.01f || r == Juan.MaxScroll(g, 600f), $"{r}");
        C("reveal keeps a visible row still", Juan.Reveal(g, 5, 0f, 600f) == 0f);

        var close = Juan.CloseBox(1000f, 50f, 17f);
        C("close button 16 from top, 22 from right", close.Y == 16f && close.X + close.W == 1000f - 22f);
        C("close button padding 6/12 with icon 10 gap 6", close.H == 29f && close.W == 24f + 10f + 6f + 50f);
        C("level 3 indents 2.4em plus 10, others after the number column", MathF.Abs(Juan.TitleIndent(3) - 42.4f) < 0.01f && MathF.Abs(Juan.TitleIndent(2) - 43.6f) < 0.01f);
    }
}
