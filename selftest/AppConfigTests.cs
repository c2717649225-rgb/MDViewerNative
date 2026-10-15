using System;
using System.Collections.Generic;
using System.Linq;
using MDViewerNative;

// E8：config.txt 解析与格式化的契约。文件是否存在由测试注入，不碰磁盘。
internal static class AppConfigTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "config.txt contract") => check("AppConfig/" + name, ok, detail);
        var existing = new HashSet<string>(StringComparer.Ordinal) { @"C:\a.md", @"C:\b.md", @"C:\A.md", @"C:\c.md" };
        bool Exists(string path) => existing.Contains(path);
        AppConfigValues Parse(string text, List<string> tabs = null, List<string> recent = null) =>
            AppConfig.Parse(text.Split('\n'), Exists, tabs ?? new(), recent ?? new());

        var empty = Parse("");
        C("empty file leaves every value unset", empty.ThemeDark == null && empty.Toc == null && empty.Hints == null && empty.Serif == null
            && empty.Grain == null && empty.Zen == null && empty.Font == null && empty.Bounds == null);

        var basic = Parse("theme=dark\ntoc=1\nhints=2\nserif=1\ngrain=0\nzen=1\nfont=18\nwin=140,90,766,900,1");
        C("known keys are read", basic.ThemeDark == true && basic.Toc == true && basic.Hints == 2 && basic.Serif == true && basic.Grain == false && basic.Zen == true
            && basic.Font == 18f && basic.Bounds is { L: 140, T: 90, R: 906, B: 990 } && basic.Maximized);
        var off = Parse("theme=light\ntoc=0\nserif=0\ngrain=1\nzen=0\ntheme=Dark");
        C("non-matching values read as false", off.ThemeDark == false && off.Toc == false && off.Serif == false && off.Grain == true && off.Zen == false);

        C("later lines override earlier ones", Parse("toc=1\ntoc=0\nfont=14\nfont=20").Toc == false && Parse("font=14\nfont=20").Font == 20f);
        C("invalid hints keep the earlier valid value", Parse("hints=1\nhints=4\nhints=-1\nhints=x").Hints == 1 && Parse("hints=9").Hints == null);
        C("font is clamped and non-finite values are dropped", Parse("font=8").Font == 12f && Parse("font=40").Font == 28f
            && Parse("font=14\nfont=NaN\nfont=Infinity\nfont=abc").Font == 14f);
        var win = Parse("win=10,20,800,600,0\nwin=1,2,100,50,1\nwin=bad");
        C("invalid window bounds keep the earlier valid value", win.Bounds is { L: 10, T: 20, R: 810, B: 620 } && !win.Maximized);
        C("lines without a key are skipped", Parse("=dark\nnoequals\n\ntoc=1").Toc == true && Parse("=dark").ThemeDark == null);
        C("value keeps everything after the first equals sign", Parse("theme=dark=x").ThemeDark == false);
        C("unknown keys are ignored", Parse("future=1\ntoc=1").Toc == true);

        var tabs = new List<string> { @"C:\c.md" };
        Parse("tabs=C:\\a.md||C:\\missing.md|C:\\A.md|C:\\C.MD\ntabs=C:\\b.md|C:\\a.md", tabs);
        C("tabs keep existing files, skip blanks, dedupe case-insensitively and accumulate", tabs.SequenceEqual(new[] { @"C:\c.md", @"C:\a.md", @"C:\b.md" }),
            string.Join("|", tabs));
        var recent = new List<string> { @"C:\c.md" };
        Parse("recent1=C:\\a.md\nrecent2=C:\\A.md\nrecent3=C:\\missing.md\nrecentX=C:\\a.md\nrecent9=\nrecent4=C:\\c.md\nrecentfoo=C:\\b.md", recent: recent);
        C("recent reads only recentN keys and dedupes case-insensitively", recent.SequenceEqual(new[] { @"C:\c.md", @"C:\a.md" }),
            string.Join("|", recent));

        string text = AppConfig.Format(true, false, 3, true, false, 16.5f, "140,90,766,900,0",
            new[] { @"C:\a.md", @"C:\b.md", @"C:\c.md" }, 2, new[] { @"C:\a.md", @"C:\b.md" });
        string expected = "theme=dark\ntoc=0\nhints=3\nserif=1\nzen=0\nfont=16.5\nwin=140,90,766,900,0\nrecent1=C:\\a.md\nrecent2=C:\\b.md\ntabs=C:\\a.md|C:\\b.md\n";
        C("format writes keys in the fixed order", text == expected, text.Replace("\n", "\\n"));
        string grainOff = AppConfig.Format(true, false, 3, true, false, 16.5f, "140,90,766,900,0",
            new[] { @"C:\a.md", @"C:\b.md", @"C:\c.md" }, 2, new[] { @"C:\a.md", @"C:\b.md" }, null, null, null, false);
        C("format writes grain=0 only when the texture is turned off",
            grainOff.Contains("grain=0") && !text.Contains("grain="), grainOff.Replace("\n", "\\n"));
        // E22：文件里的数字固定用 '.' 作小数点、ASCII '-' 作负号，与进程区域设置无关（多显示器下窗口坐标可为负）。
        string negative = WindowGeometry.FormatBounds(new RECT { L = -1280, T = -8, R = -280, B = 792 }, false);
        C("config numbers are culture independent", negative == "-1280,-8,1000,800,0"
            && WindowGeometry.TryParseBounds("-1280,-8,1000,800,1", out var nb, out bool nm) && nb.L == -1280 && nb.T == -8 && nb.R == -280 && nb.B == 792 && nm
            && Parse("font=16.5").Font == 16.5f, negative);
        string bare = AppConfig.Format(false, true, 0, false, true, 16f, null, Array.Empty<string>(), 20, Array.Empty<string>());
        C("format omits win without a window and keeps an empty tabs line", bare == "theme=light\ntoc=1\nhints=0\nserif=0\nzen=1\nfont=16\ntabs=\n", bare.Replace("\n", "\\n"));

        // E9：不认识的键原样保留，写回时放在最后，旧版不会因为少一行而丢掉新版写入的设置。
        var unknown = Parse("future=1\ntoc=1\nTheme=dark\nx=a=b\n=x\nnoequals\n\nhints=9\nrecentX=\nfuture=2\nzz=");
        C("unknown key lines are kept verbatim and in order", unknown.UnknownLines.SequenceEqual(new[] { "future=1", "Theme=dark", "x=a=b", "recentX=", "future=2", "zz=" }),
            string.Join("|", unknown.UnknownLines));
        string withUnknown = AppConfig.Format(false, true, 0, false, true, 16f, null, Array.Empty<string>(), 20, Array.Empty<string>(), new[] { "future=1", "x=y" });
        C("format appends unknown lines after the known keys", withUnknown == "theme=light\ntoc=1\nhints=0\nserif=0\nzen=1\nfont=16\ntabs=\nfuture=1\nx=y\n",
            withUnknown.Replace("\n", "\\n"));
        var kept = Parse(text + "future=1\n");
        string rewritten = AppConfig.Format(true, false, 3, true, false, 16.5f, "140,90,766,900,0",
            new[] { @"C:\a.md", @"C:\b.md", @"C:\c.md" }, 2, new[] { @"C:\a.md", @"C:\b.md" }, kept.UnknownLines);
        C("unknown lines survive a load and save", rewritten == text + "future=1\n", rewritten.Replace("\n", "\\n"));

        // E10：文件所在的盘或共享暂时不在时保留条目；根还在而文件不在时照旧删除。
        var rootProbes = new List<string>();
        bool RootPresent(string root) { rootProbes.Add(root); return root == @"C:\"; }
        var offRecent = new List<string>(); var offTabs = new List<string>();
        var offline = AppConfig.Parse(("recent1=Q:\\x.md\nrecent2=C:\\a.md\nrecent3=C:\\missing.md\nrecent4=\\\\srv\\share\\n.md\nrecent5=Q:\\x.md\n"
            + "tabs=Q:\\y.md|C:\\a.md|q:\\Y.md|C:\\missing.md|C:\\a.md\ntabs=Q:\\z.md").Split('\n'), Exists, offTabs, offRecent, RootPresent);
        C("recent entries on an unavailable volume are kept", offRecent.SequenceEqual(new[] { @"Q:\x.md", @"C:\a.md", @"\\srv\share\n.md" }), string.Join("|", offRecent));
        C("tabs on an unavailable volume are set aside, not restored", offTabs.SequenceEqual(new[] { @"C:\a.md" })
            && offline.OfflineTabs.SequenceEqual(new[] { @"Q:\y.md", @"Q:\z.md" }), string.Join("|", offTabs) + " / " + string.Join("|", offline.OfflineTabs));
        C("unavailable paths are reported and each root is probed once", offline.UnavailablePaths.SetEquals(new[] { @"Q:\x.md", @"\\srv\share\n.md", @"Q:\y.md", @"Q:\z.md" })
            && rootProbes.OrderBy(r => r, StringComparer.Ordinal).SequenceEqual(new[] { @"C:\", @"Q:\", @"\\srv\share" }), string.Join("|", rootProbes));
        var relRecent = new List<string>();
        AppConfig.Parse(new[] { "recent1=Q:x.md", "recent2=relative.md" }, Exists, new(), relRecent, _ => false);
        C("paths that are not fully qualified still need the file to exist", relRecent.Count == 0, string.Join("|", relRecent));
        var merged = AppConfig.MergeSessionTabs(new[] { @"C:\a.md", @"Q:\Y.md" }, new[] { @"Q:\y.md", @"R:\z.md" });
        C("offline tabs are saved after the open tabs unless reopened", merged.SequenceEqual(new[] { @"C:\a.md", @"Q:\Y.md", @"R:\z.md" }), string.Join("|", merged));
        var reopened = new List<string> { @"Q:\y.md", @"R:\z.md" };
        AppConfig.ForgetOffline(reopened, @"q:\Y.md");
        var afterClose = AppConfig.MergeSessionTabs(new[] { @"C:\a.md" }, reopened);
        C("an offline tab reopened and then closed is not restored", afterClose.SequenceEqual(new[] { @"C:\a.md", @"R:\z.md" }), string.Join("|", afterClose));

        var backTabs = new List<string>(); var backRecent = new List<string>();
        var back = AppConfig.Parse(text.Split('\n'), Exists, backTabs, backRecent);
        C("format then parse round-trips", back.ThemeDark == true && back.Toc == false && back.Hints == 3 && back.Serif == true && back.Zen == false && back.Grain == null
            && back.Font == 16.5f && back.Bounds is { L: 140, T: 90, R: 906, B: 990 } && !back.Maximized
            && backRecent.SequenceEqual(new[] { @"C:\a.md", @"C:\b.md" }) && backTabs.SequenceEqual(new[] { @"C:\a.md", @"C:\b.md" }));
    }
}
