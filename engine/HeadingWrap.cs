// 标题折行精修(S74,自用走查第三轮;docs/mcp-plan-s74.md 第 3、4 项)。
// 1) 末行过短(< 排版宽度 1/3)时平衡各行:行数不变,二分求最小的最大宽度(同 CSS text-wrap: balance)。
// 2) 折行后行首的全角开括号压去左半字空白:S75 起由 CjkPunct 统一负责,正文同样适用。
// 平衡只用于标题(单行标题只多一次行度量);正文由 KP 断行负责。
namespace MDViewerNative;

internal static class HeadingWrap
{
    public static void Refine(nint layout, string text, float width)
    {
        if (layout == 0 || string.IsNullOrEmpty(text)) return;
        var lines = Dw.Lines(layout);
        if (lines.Length < 2) return;
        if (LastLineWidth(layout, lines) < width / 3f) Balance(layout, lines.Length, width);
        CjkPunct.TrimLineStarts(layout, text);
    }

    private static float LastLineWidth(nint layout, DWRITE_LINE_METRICS[] lines)
    {
        uint start = 0;
        for (int i = 0; i < lines.Length - 1; i++) start += lines[i].length;
        var last = lines[^1];
        return Dw.RangeWidth(layout, start, last.length - last.trailingWhitespaceLength - last.newlineLength);
    }

    // 行数随宽度单调不增:hi 始终保持原行数,收敛到约 1 DIP。
    private static void Balance(nint layout, int count, float width)
    {
        float lo = width / 2f, hi = width;
        while (hi - lo > 1f)
        {
            float mid = (lo + hi) / 2f;
            Dw.SetMaxWidth(layout, mid);
            if (Dw.Lines(layout).Length <= count) hi = mid; else lo = mid;
        }
        Dw.SetMaxWidth(layout, hi);
    }
}
