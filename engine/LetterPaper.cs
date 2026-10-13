using System;

namespace MDViewerNative;

// U9b：信笺（演示稿 pre.src，规格 §8）。纯层：颜色、页边与镇尺几何、镇尺出现动画。
// 绘制在 EditorControl.Letter（RichEdit 画完之后用 GDI 叠画）；这里不依赖平台。
internal readonly record struct LetterStyle(uint Rule, uint Ruler, uint Background);   // 0xRRGGBB，均已与纸面合成为不透明色

internal static class LetterPaper
{
    // 页边：左侧 3 宽双线（两道 1 线夹 1 空），离编辑框左缘 8；正文再离双线 14（演示 padding-left:1.2em）。
    public const float MarginX = 8f, MarginW = 3f, TextGap = 10f;
    public const float RulerHeight = 1.5f, RulerOpacity = 0.8f, RulerFrom = 0.96f;   // 镇尺：1.5 高朱线，80%；出现时横向 96%→100%
    public const int RulerInMs = 250;

    public static float TextInset => MarginX + MarginW + TextGap;

    // 演示 --letter：各纸张的淡朱格线色（半透明，这里预先合成到纸面上）。
    public static uint RuleColor(PaperColors p) => p.Canvas switch
    {
        0xCDDCC9 => PaperPalette.Over(0x9C3522, 0.16f, p.Canvas),   // 青瓷
        0xFFFFFF => PaperPalette.Over(0xB0301A, 0.13f, p.Canvas),   // 素白
        0x1C1A18 => PaperPalette.Over(0xE0805F, 0.13f, p.Canvas),   // 灯下
        _ => PaperPalette.Over(0xB0361F, 0.14f, p.Canvas),          // 宣纸
    };

    public static LetterStyle Style(PaperColors p) => new(RuleColor(p), p.Accent, p.Canvas);

    // 镇尺在动画进度 t（0..1）时的颜色与左右端（从中间向两侧展开）。
    public static uint RulerColor(LetterStyle s, float t) => PaperPalette.Over(s.Ruler, RulerOpacity * Ease(t), s.Background);

    public static (float Left, float Right) RulerSpan(float left, float right, float t)
    {
        float w = right - left, k = RulerFrom + (1f - RulerFrom) * Ease(t), c = (left + right) / 2f;
        return (c - w * k / 2f, c + w * k / 2f);
    }

    static float Ease(float t) => DeskAnimations.Ease(Math.Clamp(t, 0f, 1f));

    // 显示行 n 的格线纵坐标（像素，含）：下一行的顶减 1；最后一行用上一行的行距外推。
    public static int RuleY(int top, int nextTop, int pitch) => (nextTop > top ? nextTop : top + Math.Max(1, pitch)) - 1;
}
