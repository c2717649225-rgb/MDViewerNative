using System;
using MDViewerNative;

// U9b：信笺的颜色、镇尺动画与格线位置；段落边界（行号栏按源码行编号）。
internal static class DeskLetterTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "desk letter") => check("DeskLetter/" + name, ok, detail);
        var xuan = LetterPaper.Style(PaperPalette.Xuan);
        C("xuan rule is accent 14% on paper", xuan.Rule == PaperPalette.Over(0xB0361F, 0.14f, 0xF6F1E7), xuan.Rule.ToString("X6"));
        C("celadon rule uses its own red at 16%", LetterPaper.Style(PaperPalette.Celadon).Rule == PaperPalette.Over(0x9C3522, 0.16f, 0xCDDCC9));
        C("lamp rule stays faint on dark paper", LetterPaper.Style(PaperPalette.Lamp).Rule == PaperPalette.Over(0xE0805F, 0.13f, 0x1C1A18));
        C("rule is lighter than ink and darker than paper on xuan", ((xuan.Rule >> 8) & 0xFF) < 0xF1 && ((xuan.Rule >> 8) & 0xFF) > 0x80);
        C("ruler ends at 80% accent", LetterPaper.RulerColor(xuan, 1f) == PaperPalette.Over(0xB0361F, 0.8f, 0xF6F1E7));
        C("ruler starts invisible", LetterPaper.RulerColor(xuan, 0f) == 0xF6F1E7);
        var (l0, r0) = LetterPaper.RulerSpan(0f, 100f, 0f);
        var (l1, r1) = LetterPaper.RulerSpan(0f, 100f, 1f);
        C("ruler grows from 96% around the middle", MathF.Abs(l0 - 2f) < 0.01f && MathF.Abs(r0 - 98f) < 0.01f && l1 == 0f && r1 == 100f);
        C("text inset leaves margin and gap", LetterPaper.TextInset == 8f + 3f + 10f);
        C("rule sits 1 above the next line", LetterPaper.RuleY(100, 124, 24) == 123);
        C("last line extrapolates the pitch", LetterPaper.RuleY(100, 100, 24) == 123);
        C("gutter keeps at least two digits", EditorGutter.Digits(0) == 2 && EditorGutter.Digits(99) == 2 && EditorGutter.Digits(100) == 3 && EditorGutter.Digits(12345) == 5);
        C("gutter hugs the digits", EditorGutter.Width(30, 7.5f) == 25f && EditorGutter.Width(300, 7.5f) == 33f && EditorGutter.NumberRight(25f) == 21f);

        var c = new EditorCoordinates("ab\r\ncd\r\n\r\nef");   // 原生：ab␍cd␍␍ef
        C("paragraph ends", c.ParagraphEndNative(0) == 2 && c.ParagraphEndNative(3) == 5 && c.ParagraphEndNative(6) == 6 && c.ParagraphEndNative(7) == -1);
        C("paragraph starts", c.IsParagraphStart(0) && c.IsParagraphStart(3) && c.IsParagraphStart(6) && c.IsParagraphStart(7) && !c.IsParagraphStart(1) && !c.IsParagraphStart(4));
    }
}
