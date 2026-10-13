using System;
using MDViewerNative;

// 两端对齐判定:空隙摊到可伸展位置上不得明显可见(汉字间 ≤ 0.2em、词间空格 ≤ 0.5em)。
internal static class JustificationTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        const float em = 16f, width = 640f;
        // 自用走查(docs/mcp-repository-map.md):长行内代码占去大半行,空隙只能摊到几个汉字之间。
        check("Justify/sparse CJK beside long inline code stays ragged",
            !Justification.ShouldJustify("全部测试语料——edge_torture.md/edge_empty.md/edge_oneline.md/edge_noeol.md 由", 560f, width, em),
            "80 DIP over 7 CJK gaps ≈ 0.7em each");
        check("Justify/two CJK words around code stay ragged",
            !Justification.ShouldJustify("由 selftest/Program.cs 引用，test_png.png/test_jpg.jpg 由 ArchitectureTests.cs、", 590f, width, em),
            "50 DIP over 6 CJK + 4 spaces; CJK line allows 0.2em per gap = 32 DIP");
        check("Justify/English line with too few spaces stays ragged",
            !Justification.ShouldJustify("see configuration_reference_for_the_renderer_and_viewer_options now", 540f, width, em),
            "100 DIP over 2 spaces");
        check("Justify/ordinary CJK line is justified",
            Justification.ShouldJustify("一段普通的中文正文会在行尾留下不到一个字宽的空隙这时应当两端对齐以保持右缘整齐", 628f, width, em),
            "12 DIP over ~38 gaps");
        check("Justify/ordinary English line is justified",
            Justification.ShouldJustify("The quick brown fox jumps over the lazy dog while the reader keeps going on", 600f, width, em),
            "40 DIP over 14 spaces");
        check("Justify/A5 rules kept: single token and underfull line",
            !Justification.ShouldJustify("https://example.com/a/very/long/path", 600f, width, em)
            && !Justification.ShouldJustify("两个词 然后很短", 400f, width, em)
            && Justification.ShouldJustify("略微超出栏宽的一行仍按原逻辑处理不会被改成左齐或者折行显示出来的", 650f, width, em),
            "no inner break / < 80% / overfull");
    }
}
