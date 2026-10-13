// 正文列宽的纯函数真相源(DocView.RecomputeColumn 与自检共用)。
// S80 用户决定:正文随窗口宽度排版——列宽 = V − 2P,不再设 40em 上限;宽窗口左右与上下
// 固定留白 48 DIP。窄窗口留白按可用宽连续过渡 12 → 20 → 48,两段斜率均 < 1/2,
// 因此 V − 2P 随 V 单调不减、没有跳变。窗口大小完全由用户决定,程序不再自动改窗口宽。
using System;

namespace MDViewerNative;

public static class WindowFit
{
    public const float PadWide = 48f;           // 宽窗口左右留白(用户选定)
    public const float PadMid = 20f;            // 收窄档留白
    public const float PadNarrow = 12f;         // 极窄档留白
    public const float PadNarrowThrough = 360f; // 12 DIP 留白保持到此,随后线性升到 20
    public const float PadNarrowBelow = 400f;   // 第一段过渡终点
    public const float PadMidThrough = 640f;    // 20 DIP 留白保持到此,随后线性升到 48
    public const float PadMidBelow = 752f;      // 第二段过渡终点(斜率 28/112 = 0.25)
    public const float PageTop = 48f;           // 第一块之上的留白(与工具栏、查找条高度无关)
    public const float PageBottom = 48f;        // 最后一块之下的留白

    public static float PadFor(float v)
    {
        if (v <= PadNarrowThrough) return PadNarrow;
        if (v < PadNarrowBelow) return PadNarrow + (PadMid - PadNarrow) * (v - PadNarrowThrough) / (PadNarrowBelow - PadNarrowThrough);
        if (v <= PadMidThrough) return PadMid;
        if (v < PadMidBelow) return PadMid + (PadWide - PadMid) * (v - PadMidThrough) / (PadMidBelow - PadMidThrough);
        return PadWide;
    }

    // 正文列宽 = V − reserved − 2P;reserved 是常驻目录占用的左侧宽度。留白按整个窗口取,
    // 开关目录时两侧留白不变(用户要求固定边距)。异常极小宽度下非负。
    // U3:rightRatio = 右留白 / 左留白(desk 为 30/48,右侧留白里放书签绳);classic 为 1,两侧相等。
    public static float ColW(float v, float reserved = 0f, float rightRatio = 1f)
        => MathF.Max(0f, v - reserved - (rightRatio == 1f ? 2f : 1f + rightRatio) * PadFor(v));

    // 左边界:在目录右侧的剩余区域内,左右留白按 1 : rightRatio 分配(classic 为居中)。
    public static float ColX(float v, float colW, float reserved = 0f, float rightRatio = 1f)
        => reserved + MathF.Max(0f, rightRatio == 1f ? (v - reserved - colW) * 0.5f : (v - reserved - colW) / (1f + rightRatio));
}
