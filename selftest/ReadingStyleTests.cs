using System;
using MDViewerNative;

// U3：正文版式（规格 §2）。
internal static class ReadingStyleTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "reading style") => check("ReadingStyle/" + name, ok, detail);
        var c = ReadingStyle.Classic; var d = ReadingStyle.Desk;

        C("classic keeps v2.2 values", !c.IsDesk && c.DefaultBodySize == 16f && c.LineRatio == 1.75f && c.PageTop == 48f && c.PageBottom == 48f
            && c.RightPadRatio == 1f && !c.SerifHeadings && c.HeadingScale(1) == 1.625f && c.HeadingScale(2) == 1.3125f && c.HeadingScale(3) == 1.125f
            && c.HeadingScale(5) == 1f && c.HeadingTracking(1) == 0f);
        C("desk follows spec §2", d.IsDesk && d.DefaultBodySize == 17f && d.LineRatio == 1.9f && d.PageTop == 60f && d.PageBottom == 110f
            && MathF.Abs(48f * d.RightPadRatio - 30f) < 1e-4f && d.SerifHeadings && d.HeadingScale(1) == 2.1f && d.HeadingScale(2) == 1.42f);
        C("desk spacing", MathF.Abs(d.DeskHeadingBefore(2, 17f) - 2.6f * 1.42f * 17f) < 1e-3f && MathF.Abs(d.DeskParagraphAfter(17f) - 1.15f * 17f) < 1e-3f
            && d.DeskHeadingAfter(1, 10f) > d.DeskHeadingAfter(3, 10f) && d.HeadingTracking(1) == 0.05f);
        C("seal geometry", MathF.Abs(ReadingStyle.SealIndent(20f) - 32.6f) < 1e-3f && ReadingStyle.SealSide(20f) < ReadingStyle.SealIndent(20f));

        C("seal numbers", ReadingStyle.SealNumber(1) == "壹" && ReadingStyle.SealNumber(9) == "玖" && ReadingStyle.SealNumber(10) == "拾"
            && ReadingStyle.SealNumber(11) == "拾壹" && ReadingStyle.SealNumber(20) == "贰拾" && ReadingStyle.SealNumber(99) == "玖拾玖"
            && ReadingStyle.SealNumber(100) == "100");
        string[] numbered = { "1. 原则", "0. 概述", "第二章 书签绳", "三、页脚", "（一）总则", "(2) 细节", "IV. 附录", "①起步", "  12 月" };
        string[] plain = { "原则", "一看就会", "十分钟上手", "Introduction", "V8 引擎", "", "Index" };
        C("own numbering suppresses the seal", Array.TrueForAll(numbered, ReadingStyle.HasOwnNumber), string.Join("|", Array.FindAll(numbered, t => !ReadingStyle.HasOwnNumber(t))));
        C("plain headings get the seal", !Array.Exists(plain, ReadingStyle.HasOwnNumber), string.Join("|", Array.FindAll(plain, ReadingStyle.HasOwnNumber)));

        C("column: classic stays centred", WindowFit.ColW(1000f) == 904f && WindowFit.ColX(1000f, 904f) == 48f
            && WindowFit.ColW(1000f, 0f, 1f) == WindowFit.ColW(1000f) && WindowFit.ColX(1000f, 904f, 0f, 1f) == WindowFit.ColX(1000f, 904f));
        float r = d.RightPadRatio, w = WindowFit.ColW(1440f, 0f, r);
        C("column: desk 48 / 30", MathF.Abs(w - (1440f - 78f)) < 1e-3f && MathF.Abs(WindowFit.ColX(1440f, w, 0f, r) - 48f) < 1e-3f, w.ToString());
        float wn = WindowFit.ColW(500f, 0f, r), xn = WindowFit.ColX(500f, wn, 0f, r);
        C("column: narrow keeps the ratio", MathF.Abs(xn - WindowFit.PadFor(500f)) < 1e-3f && MathF.Abs(500f - wn - xn - xn * r) < 1e-3f);
        float wt = WindowFit.ColW(1440f, 240f, r);
        C("column: pinned contents", MathF.Abs(WindowFit.ColX(1440f, wt, 240f, r) - 288f) < 1e-3f);
    }
}
