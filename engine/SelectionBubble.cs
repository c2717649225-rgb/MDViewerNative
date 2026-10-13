using System;

namespace MDViewerNative;

// U7：选区气泡（演示稿 .bubble）。纯几何与规则：选中文字松开鼠标后，在选区上方居中浮出「复制 / 查找」。
// 墨色底、10 圆角、4 内边距；按钮 12.5 号字、图标 13、左右 11 上下 5；下方 10×10 的小三角指向选区。
// 选区超过 300 字不出；「查找」取去掉首尾空白后的前 30 字。靠近顶栏放不下时翻到选区下方（演示稿没有这种情况）。
internal readonly record struct BubbleBox(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public bool Contains(float x, float y) => x >= X && x < Right && y >= Y && y < Bottom;
}

internal readonly record struct BubbleLayout(BubbleBox Box, BubbleBox Copy, BubbleBox Find, float ArrowX, bool Below);

internal enum BubblePart { None, Box, Copy, Find }

internal static class SelectionBubble
{
    public const int MaxChars = 300, FindChars = 30;
    public const float Pad = 4f, Radius = 10f, ButtonRadius = 7f, Gap = 2f;
    public const float ButtonInsetX = 11f, ButtonInsetY = 5f, Icon = 13f, IconGap = 6f, TextSize = 12.5f;
    public const float Lift = 46f, Arrow = 10f, Edge = 8f;

    public static bool Shows(string selected)
    {
        string t = selected?.Trim();
        return !string.IsNullOrEmpty(t) && t.Length <= MaxChars;
    }

    public static string Query(string selected)
    {
        string t = (selected ?? "").Trim();
        return t.Length <= FindChars ? t : t[..FindChars];
    }

    public static float ButtonWidth(float textW) => ButtonInsetX + Icon + IconGap + textW + ButtonInsetX;

    // cx：选区水平中心；top/bottom：选区上下缘（均为窗口 DIP）；minTop：顶栏下缘，气泡不压顶栏。
    public static BubbleLayout Place(float cx, float top, float bottom, float copyTextW, float findTextW, float textH, float viewW, float minTop)
    {
        float bh = ButtonInsetY * 2f + MathF.Max(textH, Icon);
        float w1 = ButtonWidth(copyTextW), w2 = ButtonWidth(findTextW);
        float w = Pad + w1 + Gap + w2 + Pad, h = Pad + bh + Pad;
        float x = Math.Clamp(cx - w / 2f, Edge, MathF.Max(Edge, viewW - Edge - w));
        float y = top - Lift;
        bool below = y < minTop + 4f;
        if (below) y = bottom + Lift - h;
        var box = new BubbleBox(x, y, w, h);
        var copy = new BubbleBox(x + Pad, y + Pad, w1, bh);
        var find = new BubbleBox(copy.Right + Gap, y + Pad, w2, bh);
        float arrowX = Math.Clamp(cx, x + Radius + Arrow / 2f, x + w - Radius - Arrow / 2f);
        return new BubbleLayout(box, copy, find, arrowX, below);
    }

    public static BubblePart Hit(BubbleLayout l, float x, float y) =>
        l.Copy.Contains(x, y) ? BubblePart.Copy : l.Find.Contains(x, y) ? BubblePart.Find : l.Box.Contains(x, y) ? BubblePart.Box : BubblePart.None;
}
