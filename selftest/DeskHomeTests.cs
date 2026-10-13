using System;
using System.Collections.Generic;
using System.Linq;
using MDViewerNative;

// U10：起始页（纯逻辑：固定标签、问候与日期、摘要、版面与命中、命令门禁、页脚）。
internal static class DeskHomeTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "desk home") => check("DeskHome/" + name, ok, detail);
        static bool Near(float a, float b) => MathF.Abs(a - b) < 0.01f;

        C("greeting by hour", Home.Greeting(4) == "夜深了" && Home.Greeting(5) == "早上好" && Home.Greeting(11) == "中午好" && Home.Greeting(13) == "下午好" && Home.Greeting(18) == "晚上好");
        C("chinese numbers", Home.ChineseNumber(9) == "九" && Home.ChineseNumber(10) == "十" && Home.ChineseNumber(15) == "十五" && Home.ChineseNumber(20) == "二十" && Home.ChineseNumber(29) == "二十九");
        string date = Home.ChineseDate(new DateTime(2026, 9, 29));
        C("chinese date", date == "二〇二六年九月二十九日\u3000星期二", date);
        var now = new DateTime(2026, 9, 29, 20, 0, 0);
        C("relative time", Home.RelativeTime(now.AddSeconds(-20), now) == "刚刚" && Home.RelativeTime(now.AddMinutes(-12), now) == "12 分钟前"
            && Home.RelativeTime(new DateTime(2026, 9, 29, 18, 2, 0), now) == "今天 18:02" && Home.RelativeTime(new DateTime(2026, 9, 28, 21, 15, 0), now) == "昨天 21:15"
            && Home.RelativeTime(new DateTime(2026, 9, 25, 9, 0, 0), now) == "9 月 25 日" && Home.RelativeTime(new DateTime(2025, 3, 1), now) == "2025 年 3 月 1 日");

        var (title, excerpt) = Home.Summarize("---\ntitle: x\n---\n\n# 读书笔记 2026\n\n惟江上之**清风**，与[山间](http://a)之明月。\n耳得之而为声。\n\n第二段");
        C("summary skips front matter, takes first heading and paragraph", title == "读书笔记 2026" && excerpt == "惟江上之清风，与山间之明月。耳得之而为声。", title + "|" + excerpt);
        var (t2, e2) = Home.Summarize("```\ncode\n```\n- 列表 `代码` 一项\n");
        C("summary skips fences and strips markers", t2 == null && e2 == "列表 代码 一项", e2);
        var (_, e3) = Home.Summarize(new string('字', 300));
        C("summary is capped", e3.Length == 121 && e3.EndsWith("…"));

        var recent = new List<string> { @"D:\a.md", @"D:\B.md", @"d:\a.md", @"D:\c.md", @"D:\gone.md", @"D:\d.md", @"D:\e.md", @"D:\f.md", @"D:\g.md", @"D:\h.md" };
        var picks = Home.RecentPicks(recent, @"d:\b.md", p => !p.Contains("gone"));
        C("recent picks skip continue, duplicates and missing, max 6", picks.Count == 6 && picks[0] == @"D:\a.md" && picks[1] == @"D:\c.md" && !picks.Contains(@"D:\gone.md"), string.Join(",", picks));

        C("placeholder hidden only on home", Home.HidesPlaceholder(true, 1, true, false) && !Home.HidesPlaceholder(false, 1, true, false) && !Home.HidesPlaceholder(true, 2, true, false) && !Home.HidesPlaceholder(true, 1, false, false) && !Home.HidesPlaceholder(true, 1, true, true));
        C("ctrl+tab cycles through home first", Home.Cycle(0, 3, 1) == 1 && Home.Cycle(2, 3, 1) == 0 && Home.Cycle(0, 3, -1) == 2 && Home.Cycle(0, 1, 1) == 0);

        var m = new HomeMeasure(SecH: 16f, ContTextH: 60f, WhereH: 16f, ButtonW: 120f, ButtonH: 34f, CardH: 144f, OpenW: 110f, GreetW: 162f);
        var f = Home.Layout(1440f, 44f, m, true, 6);
        C("wide: centered 1180 column with greeting on the right", f.ShowGreeting && Near(f.LeftX, 186f) && Near(f.LeftW, 850f) && Near(f.GreetX, 186f + 850f + 56f) && Near(f.GreetY, 44f + 56f + 6f), $"{f.LeftX} {f.LeftW} {f.GreetX}");
        C("continue card height and button", Near(f.ContinueCard.Y, 44f + 56f + 16f + 14f) && Near(f.ContinueCard.H, 26f + 60f + 6f + 14f + 2f + 6f + 12f + 16f + 22f)
            && Near(f.ContinueButton.X + f.ContinueButton.W, f.LeftX + f.LeftW - 28f), $"{f.ContinueCard.H}");
        C("cards: three columns of 230+ with 18 gaps", f.Columns == 3 && Near(f.Cards[0].W, (850f - 36f) / 3f) && Near(f.Cards[3].Y, f.Cards[0].Y + 144f + 18f) && Near(f.Cards[1].X, f.Cards[0].X + f.Cards[0].W + 18f));
        C("actions follow cards", Near(f.Actions.Y, f.Cards[5].Y + 144f + 34f) && Near(f.OpenButton.X, f.LeftX + 20f) && Near(f.Height, f.Actions.Y + f.Actions.H + 90f - 44f));
        var narrow = Home.Layout(800f, 44f, m, false, 2);
        C("narrow: no greeting, 28 side padding", !narrow.ShowGreeting && Near(narrow.LeftX, 28f) && Near(narrow.LeftW, 744f) && narrow.Columns == 3 && narrow.ContinueCard.W == 0f && Near(narrow.RecentHead.Y, 44f + 40f));
        C("columns", Home.Columns(229f) == 1 && Home.Columns(478f) == 2 && Home.Columns(477f) == 1);
        C("hit test", Home.Hit(f, f.ContinueButton.X + 2f, f.ContinueButton.Y + 2f).Kind == HomeTargetKind.Continue
            && Home.Hit(f, f.Cards[4].X + 5f, f.Cards[4].Y + 5f) == new HomeTarget(HomeTargetKind.Card, 4)
            && Home.Hit(f, f.OpenButton.X + 1f, f.OpenButton.Y + 1f).Kind == HomeTargetKind.Open
            && Home.Hit(f, f.Actions.X + f.Actions.W - 5f, f.Actions.Y + 5f).Kind == HomeTargetKind.None
            && Home.Hit(null, 0f, 0f).Kind == HomeTargetKind.None);
        C("scroll only when taller than the view", Home.MaxScroll(f, 2000f) == 0f && Near(Home.MaxScroll(f, 500f), f.Height - 500f));

        var onHome = new CmdState(false, false, false, false, false, 17f, Home: true);
        bool Enabled(int id, CmdState s) => Commands.All.First(c => c.Id == id).IsEnabled(s);
        C("document commands are unavailable on home", !Enabled(1002, onHome) && !Enabled(1005, onHome) && !Enabled(1006, onHome) && !Enabled(1008, onHome) && !Enabled(2232, onHome));
        C("app commands stay available on home", Enabled(1003, onHome) && Enabled(2200, onHome) && Enabled(1014, onHome) && Enabled(2204, onHome) && Enabled(2250, onHome));
        C("home disabled reason", Commands.DisabledReason(1006, onHome) == "起始页上不可用" && Commands.DisabledReason(1003, onHome) == null);
        C("documents keep their commands", Enabled(1002, onHome with { Home = false }));

        var footer = FooterModel.Home(3);
        C("home footer", footer.Left == "起始页" && footer.Right.Count == 1 && footer.Right[0].Text == "3 个文档已打开");
        var tab = new ToolbarTab(Home.TabId, Home.TabTitle, "", true, false) { IsHome = true };
        C("home tab hint", tab.DeskHint == "最近的文件" && tab.Hint.Contains("起始页"));
    }
}
