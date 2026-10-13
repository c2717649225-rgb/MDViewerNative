using System;
using System.Linq;
using MDViewerNative;

// U6a：desk 顶栏工具排布（规格 §3）与外观选纸。
internal static class DeskToolbarTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "desk toolbar") => check("DeskToolbar/" + name, ok, detail);
        static float Measure(string s) => s.Length * 12.5f;
        string Order(System.Collections.Generic.List<DeskToolSlot> s) => string.Join(",", s.Select(x => x.Tool));

        var wide = DeskToolbarLayout.Layout(1400f, false, Measure);
        C("reading order", Order(wide) == "Find,Toc,Read,Edit,Appearance,More", Order(wide));
        var more = wide[^1];
        C("right padding 6, more is icon only", more.X + more.W == 1400f - 6f && !more.Label && more.W == 10f * 2f + 16f, $"{more.X}/{more.W}");
        var find = wide[0];
        C("icon + label width", find.Label && find.W == 10f * 2f + 16f + 6f + 25f, find.W.ToString());
        var read = wide.First(s => s.Tool == DeskTool.Read); var edit = wide.First(s => s.Tool == DeskTool.Edit);
        C("segment halves touch", read.X + read.W == edit.X && read.W == 25f + 22f);
        var seg = DeskToolbarLayout.Segment(wide);
        C("segment frame: 3 inside, gap 2 + margin 4 outside", seg.X == read.X - 3f && seg.X + seg.W == edit.X + edit.W + 3f
            && wide.First(s => s.Tool == DeskTool.Appearance).X == seg.X + seg.W + 4f + 2f
            && wide[1].X + wide[1].W + 2f + 4f == seg.X, $"{seg.X}/{seg.W}");
        C("gap 2 between plain tools", wide[0].X + wide[0].W + 2f == wide[1].X);
        C("left edge", DeskToolbarLayout.Left(wide) == wide[0].X);

        C("narrow below 1180", DeskToolbarLayout.Narrow(1179f) && !DeskToolbarLayout.Narrow(1180f));
        var narrow = DeskToolbarLayout.Layout(900f, true, Measure);
        C("narrow: icons only, segment keeps text",
            narrow.All(s => s.Label == (s.Tool is DeskTool.Read or DeskTool.Edit))
            && narrow.First(s => s.Tool == DeskTool.Find).W == 8f * 2f + 16f);
        C("narrow is narrower", DeskToolbarLayout.Left(narrow) > 900f - (1400f - DeskToolbarLayout.Left(wide)) - 1f);

        C("labels are two characters", new[] { DeskTool.Find, DeskTool.Toc, DeskTool.Read, DeskTool.Edit, DeskTool.Appearance }.All(t => DeskToolbarLayout.LabelOf(t).Length == 2) && DeskToolbarLayout.LabelOf(DeskTool.More) == "");

        var look = new Appearance { Chrome = ChromeStyle.Desk };
        look.Choose(Paper.Celadon);
        look.Choose(Paper.Lamp);
        C("choose paper remembers the light one", look.Paper == Paper.Lamp && look.ToggleDark(false) == Paper.Celadon);
        look.Choose(Paper.Auto);
        C("choose auto", look.Paper == Paper.Auto && look.LastLight == Paper.Celadon);

        // 提示（演示 showTip）
        C("tip: split key", DeskTip.Split("在文档中查找 · Ctrl+F") == ("在文档中查找", "Ctrl+F")
            && DeskTip.Split("阅读 / 编辑 · Ctrl+E") == ("阅读 / 编辑", "Ctrl+E")
            && DeskTip.Split("返回 · Alt+←") == ("返回", "Alt+←"));
        C("tip: no key", DeskTip.Split("纸张、字体、字号") == ("纸张、字体、字号", null)
            && DeskTip.Split("有未保存的修改 · 关闭") == ("有未保存的修改 · 关闭", null)
            && DeskTip.Split("壹 · 书签绳") == ("壹 · 书签绳", null) && DeskTip.Split(null).Key == null);
        C("tip: key on the first line only", DeskTip.Split("关闭 · Ctrl+W\nD:\\a.md") == ("关闭\nD:\\a.md", "Ctrl+W"));
        C("tip: key tokens", DeskTip.IsKey("Ctrl+Shift+O") && DeskTip.IsKey("F11") && DeskTip.IsKey("Ctrl+=") && DeskTip.IsKey("Esc")
            && !DeskTip.IsKey("书签") && !DeskTip.IsKey("F99") && !DeskTip.IsKey("Ctrl+") && !DeskTip.IsKey("Page Up"));
        var below = DeskTip.Place(100f, 0f, 60f, 36f, 80f, 28f, 1000f, 700f, TipSide.Below);
        C("tip: centred 8 below", below == (90f, 44f), below.ToString());
        C("tip: clamped 6 from edges", DeskTip.Place(960f, 0f, 36f, 36f, 120f, 28f, 1000f, 700f, TipSide.Below).X == 1000f - 120f - 6f
            && DeskTip.Place(0f, 0f, 20f, 36f, 120f, 28f, 1000f, 700f, TipSide.Below).X == 6f);
        C("tip: flips above near the bottom", DeskTip.Place(100f, 660f, 20f, 20f, 80f, 28f, 1000f, 700f, TipSide.Below).Y == 660f - 28f - 8f);
        var beside = DeskTip.Place(976f, 300f, 24f, 16f, 80f, 28f, 1000f, 700f, TipSide.Beside);
        C("tip: cord tips sit 12 to the left", beside == (976f - 80f - 12f, 300f + 8f - 14f), beside.ToString());
    }
}
