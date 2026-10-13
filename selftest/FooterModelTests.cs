using System;
using System.Collections.Generic;
using System.Linq;
using MDViewerNative;

// U4：页脚内容（规格 §5）。
internal static class FooterModelTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "footer") => check("Footer/" + name, ok, detail);
        string R(FooterContent f) => string.Join(" · ", f.Right.Select(i => (i.Accent ? "*" : "") + i.Text));

        var a = FooterModel.Reading(null, 834, 0f, 1f);
        C("opening", a.Lead == FooterLead.Fish && a.Left == "开篇" && a.LeftSerif && R(a) == "834 字 · 已读 0%", R(a));
        var b = FooterModel.Reading("叁 · 朱笔圈点", 12345, 0.555f, 1.1f, null, "已保存");
        C("chapter, status, zoom", b.Left == "叁 · 朱笔圈点" && R(b) == "*已保存 · 12,345 字 · 已读 56% · 110%", R(b));
        C("no reading time", !R(b).Contains("分钟"));
        var c = FooterModel.Reading("壹 · 书签绳", 10, 1f, 1f, new FooterHover(true, "https://example.com/a"));
        C("external link replaces the chapter", c.Lead == FooterLead.External && c.Left == "https://example.com/a" && !c.LeftSerif && R(c) == "10 字 · 已读 100%");
        var d = FooterModel.Reading("壹", 10, 0f, 1f, new FooterHover(false, "贰 · 页脚"));
        C("internal link shows its chapter", d.Lead == FooterLead.Internal && d.Left == "贰 · 页脚");
        C("zoom 100% is hidden", !R(FooterModel.Reading(null, 1, 0f, 1.002f)).Contains("100%") && R(FooterModel.Reading(null, 1, 0f, 0.8f)).EndsWith("80%"));
        C("percent clamps", FooterModel.Percent(-1f) == "0%" && FooterModel.Percent(float.NaN) == "0%" && FooterModel.Percent(0.004f) == "0%");

        C("words: han + latin runs", FooterModel.CountWords("书签绳 Windows 11 用户，abc-def") == 3 + 1 + 1 + 2 + 2, FooterModel.CountWords("书签绳 Windows 11 用户，abc-def").ToString());
        C("words: empty", FooterModel.CountWords("") == 0 && FooterModel.CountWords(null) == 0 && FooterModel.CountWords("，。！") == 0);

        var heads = new List<(int, float)> { (1, 60f), (2, 400f), (3, 700f), (2, 1200f) };
        C("chapter: before the first section", FooterModel.CurrentChapter(heads, 0f) == -1 && FooterModel.CurrentChapter(heads, 305f) == -1);
        C("chapter: crossing the top edge", FooterModel.CurrentChapter(heads, 306f) == 1 && FooterModel.CurrentChapter(heads, 375f, 24f) == -1 && FooterModel.CurrentChapter(heads, 690f) == 2 && FooterModel.CurrentChapter(heads, 5000f) == 3);
        C("chapter label", FooterModel.ChapterLabel("叁", "朱笔圈点") == "叁 · 朱笔圈点" && FooterModel.ChapterLabel(null, "附录") == "附录");
        C("read ratio", FooterModel.ReadRatio(0f, 100f, 400f) == 0f && FooterModel.ReadRatio(300f, 1000f, 400f) == 0.5f && FooterModel.ReadRatio(900f, 1000f, 400f) == 1f);

        var s = new FooterStatus();
        s.Show("已保存", 1000);
        C("status lasts 3 s", s.Current(3999) == "已保存" && s.Current(4000) == null && s.DueMs(1500) == 2500 && s.DueMs(4000) == -1);
        s.Show("已复制", 5000, 100); s.Clear();
        C("status clears", s.Current(5001) == null && s.DueMs(5001) == -1);
    }
}
