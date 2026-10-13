using System;
using System.Collections.Generic;
using MDViewerNative;

// U2：界面开关与纸张的决策、配置读写（ADR-0003 第 1、4、5 条）。
internal static class AppearanceTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "appearance contract") => check("Appearance/" + name, ok, detail);

        var a = new Appearance();
        C("desk by default", a.Chrome == ChromeStyle.Desk && a.Desk && a.Paper == Paper.Auto);   // U12

        a.LoadPaper(Paper.Celadon, true);
        C("configured paper wins over legacy theme", a.Paper == Paper.Celadon);
        a.LoadPaper(null, true);
        C("legacy dark theme maps to lamp", a.Paper == Paper.Lamp);
        a.LoadPaper(null, false);
        C("legacy light theme maps to xuan", a.Paper == Paper.Xuan);
        a.LoadPaper(null, null);
        C("nothing configured follows the system", a.Paper == Paper.Auto && a.Colors(true).Dark && !a.Colors(false).Dark);

        var t = new Appearance { Paper = Paper.Celadon };
        C("ctrl+d from a light paper goes to lamp", t.ToggleDark(false) == Paper.Lamp);
        C("ctrl+d from lamp returns to the previous light paper", t.ToggleDark(false) == Paper.Celadon);
        var s = new Appearance();
        C("ctrl+d under auto uses the resolved paper", s.ToggleDark(true) == Paper.Xuan && s.ToggleDark(true) == Paper.Lamp && s.ToggleDark(false) == Paper.Xuan);

        C("chrome tokens", Appearance.ParseChrome("desk") == true && Appearance.ParseChrome("classic") == false
            && Appearance.ParseChrome("Desk") == null && Appearance.ParseChrome("") == null
            && Appearance.ChromeToken(true) == "desk" && Appearance.ChromeToken(false) == "classic");

        // config.txt：paper= 与 chrome= 成为已知键，读写往返且不影响老键。
        AppConfigValues Parse(string text) => AppConfig.Parse(text.Split('\n'), _ => false, new List<string>(), new List<string>());
        var v = Parse("theme=dark\npaper=celadon\nchrome=desk");
        C("config reads paper and chrome", v.Paper == Paper.Celadon && v.ChromeDesk == true && v.ThemeDark == true && v.UnknownLines.Count == 0);
        var bad = Parse("paper=sepia\nchrome=maybe");
        C("invalid paper and chrome read as unset", bad.Paper == null && bad.ChromeDesk == null && bad.UnknownLines.Count == 0);
        C("later valid lines override", Parse("paper=white\npaper=lamp\nchrome=desk\nchrome=classic") is { Paper: Paper.Lamp, ChromeDesk: false });

        string Format(string paper, string chrome) => AppConfig.Format(false, false, 0, false, false, 16f, null, Array.Empty<string>(), 0,
            Array.Empty<string>(), new[] { "future=1" }, paper, chrome);
        string plain = Format(null, null), both = Format("celadon", "desk");
        C("format without paper or chrome is unchanged", !plain.Contains("paper=") && !plain.Contains("chrome="));
        C("format writes paper and chrome before unknown lines", both.Contains("paper=celadon\nchrome=desk\n")
            && both.IndexOf("chrome=desk", StringComparison.Ordinal) < both.IndexOf("future=1", StringComparison.Ordinal));
        var back = Parse(both);
        C("paper and chrome round-trip", back.Paper == Paper.Celadon && back.ChromeDesk == true && back.UnknownLines.Count == 1);
    }
}
