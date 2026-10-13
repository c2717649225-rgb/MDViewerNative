using System;
using System.Collections.Generic;

namespace MDViewerNative;

// 试用反馈：desk 下的“保存 / 不保存 / 取消”等选择框改为自绘（系统 TaskDialog 是浅色的，暗纸下刺眼、与整体不统一）。
// 纯几何：卡片尺寸、标题 / 正文 / 按钮的位置。单位 DIP，坐标相对卡片左上角。按钮左起为主按钮，最后一个是“取消”。
internal sealed record ChoiceFrame(float W, float H, PanelRect Heading, PanelRect Body, IReadOnlyList<PanelRect> Buttons);

internal static class ChoiceDialog
{
    public const float MinWidth = 380f, MaxWidth = 520f, Pad = 24f, Top = 22f, BodyGap = 8f, ButtonsGap = 22f, Bottom = 20f;
    public const float ButtonH = 32f, ButtonMinW = 84f, ButtonInsetX = 18f, ButtonGap = 8f, ButtonRadius = 7f, Radius = 10f;
    public const float HeadingSize = 15f, BodySize = 13f, ButtonTextSize = 13f;
    // 按钮窗口比可见按钮每边多出的一圈，留给键盘焦点环
    public const float RingRoom = 3f;
    public const int FirstId = 1000, CancelId = 2;

    // 按钮宽度 = 文字宽 + 左右 18，至少 84。
    public static float ButtonWidth(float textW) => MathF.Max(ButtonMinW, MathF.Ceiling(textW + ButtonInsetX * 2f));

    // 卡片宽度：按钮排得下，且在 380–520 之间（正文按这个宽度折行）。
    public static float Width(IReadOnlyList<float> buttonTextW)
    {
        float row = 0f;
        for (int i = 0; i < buttonTextW.Count; i++) row += ButtonWidth(buttonTextW[i]) + (i > 0 ? ButtonGap : 0f);
        return Math.Clamp(MathF.Ceiling(row + Pad * 2f), MinWidth, MaxWidth);
    }

    public static float TextWidth(float width) => width - Pad * 2f;

    // headingH / bodyH：按 TextWidth 折行后量得的高度；bodyH 为 0 表示没有正文。按钮行右对齐。
    public static ChoiceFrame Layout(float width, float headingH, float bodyH, IReadOnlyList<float> buttonTextW)
    {
        float text = TextWidth(width);
        var heading = new PanelRect(Pad, Top, text, headingH);
        float y = heading.Y + headingH;
        var body = new PanelRect(Pad, y + (bodyH > 0f ? BodyGap : 0f), text, bodyH);
        y = body.Y + bodyH + ButtonsGap;
        var widths = new float[buttonTextW.Count];
        float row = 0f;
        for (int i = 0; i < widths.Length; i++) { widths[i] = ButtonWidth(buttonTextW[i]); row += widths[i] + (i > 0 ? ButtonGap : 0f); }
        var buttons = new PanelRect[widths.Length];
        float x = MathF.Max(Pad, width - Pad - row);
        for (int i = 0; i < widths.Length; i++) { buttons[i] = new PanelRect(x, y, widths[i], ButtonH); x += widths[i] + ButtonGap; }
        return new ChoiceFrame(width, y + ButtonH + Bottom, heading, body, buttons);
    }

    // 第 index 个按钮的命令号：前面依次 1000、1001…（与 TaskDialog 自定义按钮同号），最后一个为 IDCANCEL。
    public static int IdOf(int index, int count) => index == count - 1 ? CancelId : FirstId + index;

    // 卡片放在所属窗口客户区的水平中央、垂直略偏上（上 2 : 下 3），不出客户区。
    public static (float X, float Y) Place(float ownerW, float ownerH, float w, float h) =>
        (MathF.Max(0f, (ownerW - w) / 2f), MathF.Max(0f, (ownerH - h) * 0.4f));
}
