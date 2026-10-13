using System;
using System.Linq;
using MDViewerNative;

// U1：v3 纸张色表与尺寸常量的契约（docs/design/quiet-desk-spec.md §1；ADR-0003）。
// 对比度红线验证的是规格本身；读写往返保证 paper= 与老 theme= 互通。
internal static class DeskDesignTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "desk design contract") => check("DeskDesign/" + name, ok, detail);

        foreach (var (name, p) in new[] { ("xuan", PaperPalette.Xuan), ("celadon", PaperPalette.Celadon), ("white", PaperPalette.White), ("lamp", PaperPalette.Lamp) })
        {
            string R(uint a, uint b) => Contrast(a, b).ToString("0.00");
            C(name + " ink >= 10:1", Contrast(p.Ink, p.Canvas) >= 10, R(p.Ink, p.Canvas));
            C(name + " ink-2 >= 6:1", Contrast(p.Ink2, p.Canvas) >= 6, R(p.Ink2, p.Canvas));
            C(name + " muted >= 4.5:1 on paper and desk", Contrast(p.Muted, p.Canvas) >= 4.5 && Contrast(p.Muted, p.Desk) >= 4.5, R(p.Muted, p.Desk));
            C(name + " accent >= 4.5:1 on paper and surface", Contrast(p.Accent, p.Canvas) >= 4.5 && Contrast(p.Accent, p.Surface) >= 4.5, R(p.Accent, p.Surface));
            C(name + " warn text >= 4.5:1", Contrast(p.WarnText, p.Canvas) >= 4.5, R(p.WarnText, p.Canvas));
            C(name + " control border >= 3:1", Contrast(p.ControlBorder, p.Canvas) >= 3, R(p.ControlBorder, p.Canvas));
            var code = new[] { p.Keyword, p.Type, p.String, p.Number, p.Comment };
            C(name + " code tokens >= 4.5:1 on surface", code.All(c => Contrast(c, p.Surface) >= 4.5), string.Join(",", code.Select(c => R(c, p.Surface))));
            var washes = new[] { p.Selection, p.Hit, p.Mark, p.AccentSoft, p.Surface, p.SurfaceAlt };
            C(name + " ink stays >= 7:1 on every wash", washes.All(w => Contrast(p.Ink, w) >= 7), string.Join(",", washes.Select(w => R(p.Ink, w))));
            C(name + " dark flag matches luminance", p.Dark == (Luminance(p.Canvas) < 0.2));
        }

        C("over blends in sRGB", PaperPalette.Over(0xFF0000, 0.5f, 0x0000FF) == 0x800080 && PaperPalette.Over(0x123456, 1f, 0xFFFFFF) == 0x123456
            && PaperPalette.Over(0x123456, 0f, 0xABCDEF) == 0xABCDEF);
        C("auto follows system", PaperPalette.Resolve(Paper.Auto, false) == Paper.Xuan && PaperPalette.Resolve(Paper.Auto, true) == Paper.Lamp
            && PaperPalette.Resolve(Paper.Celadon, true) == Paper.Celadon);
        C("tokens round-trip", PaperPalette.All.All(p => PaperPalette.Parse(PaperPalette.Token(p)) == p));
        C("unknown or cased tokens are rejected", PaperPalette.Parse("") == null && PaperPalette.Parse("Xuan") == null && PaperPalette.Parse("sepia") == null);
        C("legacy theme maps to nearest paper", PaperPalette.FromLegacy(true) == Paper.Lamp && PaperPalette.FromLegacy(false) == Paper.Xuan
            && PaperPalette.FromLegacy(null) == Paper.Auto);
        C("legacy dark flag written back", !PaperPalette.LegacyDark(Paper.Celadon, true) && PaperPalette.LegacyDark(Paper.Lamp, false)
            && PaperPalette.LegacyDark(Paper.Auto, true) && !PaperPalette.LegacyDark(Paper.Auto, false));
        C("display names are distinct", PaperPalette.All.Select(PaperPalette.DisplayName).Distinct().Count() == PaperPalette.All.Length);

        C("metrics: cord fits inside the strip", DeskMetrics.CordRight + DeskMetrics.CordWidth <= DeskMetrics.StripWidth
            && DeskMetrics.TickWidth >= DeskMetrics.CordWidth && DeskMetrics.StripWidth <= DeskMetrics.BodyMarginRight);
        C("metrics: tab fits inside the title bar", DeskMetrics.TabHeight + DeskMetrics.TabAccentLine < DeskMetrics.TitleBarHeight);
        C("metrics: footer fade within footer band", DeskMetrics.FooterFade <= DeskMetrics.FooterHeight && DeskMetrics.CordBottomReserve > DeskMetrics.FooterHeight);
    }

    static double Contrast(uint a, uint b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    // 输入为 0xRRGGBB。
    static double Luminance(uint rgb)
    {
        static double Lin(uint c) { double v = c / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin((rgb >> 16) & 0xFF) + 0.7152 * Lin((rgb >> 8) & 0xFF) + 0.0722 * Lin(rgb & 0xFF);
    }
}
