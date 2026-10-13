using System;

namespace MDViewerNative;

// 编辑模式行号栏宽度：按实际位数收紧（用户反馈旧的固定 44 DIP 空地太多）。纯计算，DIP。
internal static class EditorGutter
{
    public const float LeftPad = 6f, NumberGap = 4f;
    public const int MinDigits = 2;

    public static int Digits(int lines)
    {
        int digits = 1;
        for (int n = Math.Max(1, lines); n >= 10; n /= 10) digits++;
        return Math.Max(MinDigits, digits);
    }

    public static float Width(int lines, float digitWidth) => MathF.Ceiling(LeftPad + Digits(lines) * digitWidth + NumberGap);

    // 行号右缘（右对齐的基准）
    public static float NumberRight(float width) => width - NumberGap;
}
