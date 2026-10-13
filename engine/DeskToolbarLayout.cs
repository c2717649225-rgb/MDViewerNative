using System;
using System.Collections.Generic;

namespace MDViewerNative;

// U6a：desk 顶栏右侧工具的排布（规格 §3）。纯函数，单位 DIP；文字宽度由调用方量好传入。
// 顺序：查找、目录、[阅读|编辑]、外观、⋯；编辑时的「保存」在编辑条上（U9a）。
// 窗口宽 < 1180 时工具只显示图标，分段按钮保留文字（演示 @media max-width:1180px）。
internal enum DeskTool { Find, Toc, Read, Edit, Appearance, More }

internal readonly record struct DeskToolSlot(DeskTool Tool, float X, float W, bool Label);

internal static class DeskToolbarLayout
{
    public const float ButtonHeight = 30f, ButtonRadius = 8f, Icon = 16f, IconGap = 6f, Gap = 2f, RightPad = 6f;
    public const float SegPad = 3f, SegRadius = 9f, SegButtonPad = 11f, SegButtonRadius = 6f, SegMargin = 4f;

    public static float Pad(bool narrow) => narrow ? 8f : 10f;

    public static bool Narrow(float windowWidth) => windowWidth < DeskMetrics.IconOnlyBelowWidth;

    public static string LabelOf(DeskTool t) => t switch
    {
        DeskTool.Find => "查找",
        DeskTool.Toc => "目录",
        DeskTool.Read => "阅读",
        DeskTool.Edit => "编辑",
        DeskTool.Appearance => "外观",
        _ => "",
    };

    // ⋯ 只有图标。

    // right：工具区右缘（窗口宽）。返回的槽位按从左到右排列；分段按钮的两半各占一个槽位（不含外层 3 的内边距）。
    public static List<DeskToolSlot> Layout(float right, bool narrow, Func<string, float> measure)
    {
        var order = new List<DeskTool> { DeskTool.Find, DeskTool.Toc };
        order.AddRange(new[] { DeskTool.Read, DeskTool.Edit, DeskTool.Appearance, DeskTool.More });
        var widths = new float[order.Count];
        var labels = new bool[order.Count];
        for (int i = 0; i < order.Count; i++)
        {
            var t = order[i];
            if (t is DeskTool.Read or DeskTool.Edit) { widths[i] = measure(LabelOf(t)) + SegButtonPad * 2f; labels[i] = true; continue; }
            bool label = !narrow && t != DeskTool.More;
            labels[i] = label;
            widths[i] = Pad(narrow) * 2f + Icon + (label ? IconGap + measure(LabelOf(t)) : 0f);
        }
        // 从右往左排。
        var slots = new DeskToolSlot[order.Count];
        float x = right - RightPad;
        for (int i = order.Count - 1; i >= 0; i--)
        {
            var t = order[i];
            if (t == DeskTool.Edit) x -= SegPad + SegMargin;                 // 分段按钮右侧：外边距 + 内边距
            x -= widths[i];
            slots[i] = new(t, x, widths[i], labels[i]);
            if (t == DeskTool.Read) x -= SegPad + SegMargin;                 // 分段按钮左侧
            if (i > 0 && t != DeskTool.Edit) x -= Gap;                       // 阅读与编辑紧挨，其余间距 2
        }
        return new List<DeskToolSlot>(slots);
    }

    // 分段按钮外框（含 3 的内边距）。
    public static (float X, float W) Segment(IReadOnlyList<DeskToolSlot> slots)
    {
        foreach (var s in slots)
            if (s.Tool == DeskTool.Read)
            {
                float end = s.X + s.W;
                foreach (var e in slots) if (e.Tool == DeskTool.Edit) end = e.X + e.W;
                return (s.X - SegPad, end - s.X + SegPad * 2f);
            }
        return default;
    }

    public static float Left(IReadOnlyList<DeskToolSlot> slots)
    {
        if (slots.Count == 0) return 0f;
        var first = slots[0];
        return first.Tool == DeskTool.Read ? first.X - SegPad - SegMargin : first.X;
    }
}
