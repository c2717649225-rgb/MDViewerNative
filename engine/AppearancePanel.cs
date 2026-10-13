using System;
using System.Collections.Generic;

namespace MDViewerNative;

// U11b：外观面板（演示 #lookPop：.pop / .opt / .prow / .stepper）。纯几何与状态：纸张五选一、正文字体（黑体 / 宋体）、字号步进。
// 单位 DIP，坐标相对面板左上角；文字高度由调用方量好传入。
internal enum AppearanceTargetKind { None, Paper, Font, ZoomOut, ZoomIn }

internal readonly record struct AppearanceTarget(AppearanceTargetKind Kind, int Index = 0)
{
    public static readonly AppearanceTarget None = default;
}

internal readonly record struct PanelRect(float X, float Y, float W, float H)
{
    public bool Contains(float x, float y) => x >= X && x < X + W && y >= Y && y < Y + H;
}

internal readonly record struct AppearanceMeasure(float HeadLine, float NameLine, float DescLine, float NoteH, float RowLine, float SansW, float SerifW, float SegLine, float ValueLine);

internal sealed record AppearanceFrame(float W, float H, PanelRect Head, IReadOnlyList<PanelRect> Options, PanelRect Note,
    float FontRuleY, PanelRect FontLabel, PanelRect Segment, PanelRect Sans, PanelRect Serif,
    float SizeRuleY, PanelRect SizeLabel, PanelRect Minus, PanelRect Value, PanelRect Plus);

// 面板此刻要显示的状态（宿主从偏好与命令状态取）。
internal readonly record struct AppearanceState(Paper Paper, bool Serif, bool SerifAvailable, int ZoomPercent, bool CanZoomIn, bool CanZoomOut);

internal static class AppearancePanel
{
    public const float Width = 330f, Pad = 16f, Radius = 14f, Gap = 2f;
    public const float HeadSize = 12f, HeadSpacing = 0.2f, HeadInsetX = 4f, HeadTop = 2f, HeadBottom = 10f;
    public const float OptionPad = 8f, OptionRadius = 10f, OptionGap = 12f, SwatchW = 44f, SwatchH = 32f, SwatchRadius = 7f, Check = 18f;
    public const float NameSize = 13.5f, DescSize = 12f, NoteSize = 12f, NoteInset = 6f, NoteTop = 6f, NoteBottom = 2f;
    public const float RowTop = 8f, RowPadTop = 11f, RowPadBottom = 4f, RowInset = 6f, RowSize = 13f;
    public const float SegPad = 3f, SegButtonPad = 11f, SegButtonPadY = 3f, SegRadius = 9f, SegButtonRadius = 6f, SegSize = 12.5f;
    public const float StepButton = 28f, StepRadius = 7f, StepGap = 4f, ValueMinW = 46f, ValueSize = 12.5f;
    public const string Title = "纸张", Note = "Windows 开启高对比度时，会自动改用系统配色。", FontRow = "正文字体", SizeRow = "字号", Sans = "黑体", SerifLabel = "宋体";

    public static readonly string[] Names = { "跟随系统", "宣纸", "青瓷", "素白", "灯下" };
    public static readonly string[] Notes = { "Windows 浅色用宣纸，深色用灯下", "白天、室内、日常阅读", "长时间阅读：更暗、更绿，不刺眼", "投屏、截图、强光；同打印效果", "夜里、暗处" };

    public static float NoteWidth => Width - Pad * 2f - NoteInset * 2f;

    public static AppearanceFrame Layout(AppearanceMeasure m)
    {
        float inner = Width - Pad * 2f, y = Pad + HeadTop;
        var head = new PanelRect(Pad + HeadInsetX, y, inner - HeadInsetX * 2f, m.HeadLine);
        y += m.HeadLine + HeadBottom;
        float optH = OptionPad * 2f + MathF.Max(SwatchH, m.NameLine + 1f + m.DescLine);
        var options = new PanelRect[Names.Length];
        for (int i = 0; i < options.Length; i++) { options[i] = new PanelRect(Pad, y, inner, optH); y += optH; }
        y += NoteTop;
        var note = new PanelRect(Pad + NoteInset, y, NoteWidth, m.NoteH);
        y += m.NoteH + NoteBottom;

        // 正文字体：左标签，右分段按钮（外框 3 的内边距）
        y += RowTop;
        float fontRule = y;
        float segH = SegPad * 2f + SegButtonPadY * 2f + m.SegLine;
        float rowY = y + RowPadTop, rowH = MathF.Max(segH, m.RowLine);
        float sansW = m.SansW + SegButtonPad * 2f, serifW = m.SerifW + SegButtonPad * 2f;
        float segW = SegPad * 2f + sansW + serifW, segX = Width - Pad - RowInset - segW, segY = rowY + (rowH - segH) / 2f;
        var segment = new PanelRect(segX, segY, segW, segH);
        var sans = new PanelRect(segX + SegPad, segY + SegPad, sansW, segH - SegPad * 2f);
        var serif = new PanelRect(sans.X + sansW, sans.Y, serifW, sans.H);
        var fontLabel = new PanelRect(Pad + RowInset, rowY + (rowH - m.RowLine) / 2f, segX - Pad - RowInset, m.RowLine);
        y = rowY + rowH + RowPadBottom;

        // 字号：左标签，右步进（− 值 ＋）
        y += RowTop;
        float sizeRule = y;
        rowY = y + RowPadTop; rowH = MathF.Max(StepButton, m.RowLine);
        float by = rowY + (rowH - StepButton) / 2f;
        var plus = new PanelRect(Width - Pad - RowInset - StepButton, by, StepButton, StepButton);
        var value = new PanelRect(plus.X - StepGap - ValueMinW, rowY + (rowH - m.ValueLine) / 2f, ValueMinW, m.ValueLine);
        var minus = new PanelRect(value.X - StepGap - StepButton, by, StepButton, StepButton);
        var sizeLabel = new PanelRect(Pad + RowInset, rowY + (rowH - m.RowLine) / 2f, minus.X - Pad - RowInset, m.RowLine);
        y = rowY + rowH + RowPadBottom + Pad;

        return new AppearanceFrame(Width, y, head, options, note, fontRule, fontLabel, segment, sans, serif, sizeRule, sizeLabel, minus, value, plus);
    }

    // 面板右缘对齐按钮右缘外 6（演示 right = innerWidth − r.right − 6），不出窗口左右 8。
    public static float PlaceX(float buttonRight, float viewW) =>
        Math.Clamp(buttonRight + 6f - Width, 8f, MathF.Max(8f, viewW - 8f - Width));

    public static AppearanceTarget Hit(AppearanceFrame f, float x, float y)
    {
        if (f == null) return AppearanceTarget.None;
        for (int i = 0; i < f.Options.Count; i++) if (f.Options[i].Contains(x, y)) return new(AppearanceTargetKind.Paper, i);
        if (f.Sans.Contains(x, y)) return new(AppearanceTargetKind.Font, 0);
        if (f.Serif.Contains(x, y)) return new(AppearanceTargetKind.Font, 1);
        if (f.Minus.Contains(x, y)) return new(AppearanceTargetKind.ZoomOut);
        if (f.Plus.Contains(x, y)) return new(AppearanceTargetKind.ZoomIn);
        return AppearanceTarget.None;
    }

    // 键盘顺序：五种纸、黑体、宋体、缩小、放大。
    public static readonly AppearanceTarget[] Order =
    {
        new(AppearanceTargetKind.Paper, 0), new(AppearanceTargetKind.Paper, 1), new(AppearanceTargetKind.Paper, 2), new(AppearanceTargetKind.Paper, 3), new(AppearanceTargetKind.Paper, 4),
        new(AppearanceTargetKind.Font, 0), new(AppearanceTargetKind.Font, 1), new(AppearanceTargetKind.ZoomOut), new(AppearanceTargetKind.ZoomIn),
    };

    public static bool Enabled(AppearanceTarget t, AppearanceState s) => t.Kind switch
    {
        AppearanceTargetKind.Paper => true,
        AppearanceTargetKind.Font => t.Index == 0 || s.SerifAvailable,
        AppearanceTargetKind.ZoomOut => s.CanZoomOut,
        AppearanceTargetKind.ZoomIn => s.CanZoomIn,
        _ => false,
    };

    // 从 current 起按方向找下一个可用目标（循环）；current 不在序列里时从当前纸张开始。
    public static AppearanceTarget Step(AppearanceTarget current, int direction, AppearanceState s)
    {
        int i = Array.IndexOf(Order, current);
        if (i < 0) return new(AppearanceTargetKind.Paper, (int)s.Paper);
        for (int k = 1; k <= Order.Length; k++)
        {
            var next = Order[((i + direction * k) % Order.Length + Order.Length) % Order.Length];
            if (Enabled(next, s)) return next;
        }
        return current;
    }

    public static bool IsOn(AppearanceTarget t, AppearanceState s) => t.Kind switch
    {
        AppearanceTargetKind.Paper => (int)s.Paper == t.Index,
        AppearanceTargetKind.Font => (t.Index == 1) == s.Serif,
        _ => false,
    };

    public static int ZoomPercent(float bodyFont, float defaultFont) =>
        defaultFont <= 0f ? 100 : (int)MathF.Round(bodyFont / defaultFont * 100f);
}
