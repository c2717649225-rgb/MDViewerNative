using System;
using MDViewerNative;

// U3：desk 正文版式的离屏基线（规格 §2）。classic 路径同一文档的几何不变。
internal static class DeskReadingTests
{
    const string Doc = "# 安静的书桌\n\n第一段正文。\n\n第二段正文。\n\n## 为什么要一张纸\n\n正文。\n\n> 引一\n>\n> 引二\n\n### 一看就会\n\n- 列表项\n\n## 1. 自带编号\n\n| 甲 | 乙 |\n|---|---|\n| 1 | 2 |\n\n```\ncode\n```\n\n## 细节\n\n末段。\n";

    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "") => check("DeskReading/" + name, ok, detail);
        using var scope = new RenderStateScope();
        RenderResources.Scale = 1f;
        PaperColors? keptPaper = RenderResources.DeskPaper;
        RenderResources.ApplyTheme(false);
        try
        {
            RenderResources.SetReading(ReadingStyle.Classic);
            RenderResources.SetBodySize(16f);
            using (var classic = Load())
            {
                bool noIndent = true;
                foreach (var e in classic.TocEntries) noIndent &= classic.BlockIndent(e.blockIdx) == 0f;   // classic 标题不位移
                C("classic geometry unchanged", classic.ColX == 48f && classic.ColW == 904f && classic.BlockBox(0).Top == 48f && noIndent,
                    $"x={classic.ColX} w={classic.ColW} top={classic.BlockBox(0).Top}");
            }

            RenderResources.SetReading(ReadingStyle.Desk);
            RenderResources.SetBodySize(17f);
            RenderResources.SetPaper(PaperPalette.Get(Paper.Xuan, false));
            RenderResources.ApplyTheme(false);
            using var desk = Load();
            C("margins 48 / 30, top 60", desk.ColX == 48f && MathF.Abs(desk.ColW - 922f) < 1e-3f && desk.BlockBox(0).Top == 60f, $"x={desk.ColX} w={desk.ColW}");
            var last = desk.BlockBox(desk.BlockCount - 1);
            C("bottom space 110", MathF.Abs(desk.TotalH - (last.Top + last.Height + 110f)) < 1e-3f);
            var p1 = desk.BlockBox(1); var p2 = desk.BlockBox(2);
            C("paragraph spacing 1.15em", MathF.Abs(p2.Top - (p1.Top + p1.Height) - 1.15f * 17f) < 1e-3f, (p2.Top - p1.Top - p1.Height).ToString("0.##"));
            C("line height 1.9", MathF.Abs(RenderResources.LineH(17f) - 32.3f) < 1e-3f);

            int h2 = desk.TocEntries.FindIndex(e => e.Item3 == "为什么要一张纸");
            int own = desk.TocEntries.FindIndex(e => e.Item3 == "1. 自带编号");
            int h3 = desk.TocEntries.FindIndex(e => e.Item3 == "一看就会");
            int b2 = desk.TocEntries[h2].Item1, bOwn = desk.TocEntries[own].Item1, b3 = desk.TocEntries[h3].Item1;
            C("H2 is indented for its seal", MathF.Abs(desk.BlockIndent(b2) - ReadingStyle.SealIndent(1.42f * 17f)) < 1e-3f, desk.BlockIndent(b2).ToString("0.##"));
            C("numbered H2 has no seal", desk.BlockIndent(bOwn) == 0f);
            C("H3 is indented for its dash", MathF.Abs(desk.BlockIndent(b3) - ReadingStyle.DashIndent(1.05f * 17f)) < 1e-3f);
            C("H2 top spacing 2.6em", MathF.Abs(desk.BlockBox(b2).Top - desk.BlockBox(b2 - 1).Top - desk.BlockBox(b2 - 1).Height - 2.6f * 1.42f * 17f) < 1e-3f);

            // U3b：同一段引用里的段落只隔 .4em（引用字号），引用前后 1.9em。
            var q1 = desk.BlockBox(b2 + 2); var q2 = desk.BlockBox(b2 + 3); var before = desk.BlockBox(b2 + 1);
            C("quote paragraphs sit close", MathF.Abs(q2.Top - q1.Top - q1.Height - 0.4f * 1.08f * 17f) < 1e-3f, (q2.Top - q1.Top - q1.Height).ToString("0.##"));
            C("quote margin 1.9em", MathF.Abs(q1.Top - before.Top - before.Height - 1.9f * 17f) < 1e-3f, (q1.Top - before.Top - before.Height).ToString("0.##"));

            // 方印画在标题文字左侧的列内：那一块里应当有朱色像素。
            using var frame = new OffscreenFrame(1000, 700);
            var box = desk.BlockBox(b2);
            float scroll = MathF.Max(0f, box.Top - 100f);
            frame.Paint(desk, scroll);
            int best = 0, bx = 0, by = 0;
            int red = 0, y0 = (int)(OffscreenFrame.Top + box.Top - scroll), side = (int)ReadingStyle.SealSide(1.42f * 17f);
            for (int y = y0; y < y0 + (int)box.Height; y++)
                for (int x = 48; x < 48 + side + 2; x++)
                {
                    uint c = frame.At(x, y);
                    int r = (int)(c & 0xFF), g = (int)((c >> 8) & 0xFF);
                    if (r > g + 40) red++;
                    if (r - g > best) { best = r - g; bx = x; by = y; }
                }
            C("seal is painted in accent", red > 20, $"{red} best={best}@{bx},{by} y0={y0} h={box.Height} side={side}");
        }
        finally { RenderResources.SetReading(ReadingStyle.Classic); RenderResources.SetPaper(keptPaper); RenderResources.ApplyTheme(false); }
    }

    static DocView Load()
    {
        var doc = new DocView();
        doc.Load(Doc);
        doc.OnWidth(1000f);
        doc.Relayout();
        return doc;
    }
}
