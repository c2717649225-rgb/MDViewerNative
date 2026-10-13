namespace MDViewerNative;

// U6b：合并标题栏的命中判定（docs/design/u6b-titlebar.md）。纯函数，单位 DIP，返回 Win32 HT* 命中码。
// 宿主只负责把 WM_NCHITTEST 的屏幕坐标换成客户区 DIP，再把结果原样交回系统。
internal enum CaptionPart { None, Min, Max, Close }

internal static class DeskFrame
{
    public const float ButtonWidth = 46f;                    // 演示 .caption button
    public const float Width = ButtonWidth * 3f;
    public const float Glyph = 10f;
    public const uint CloseHover = 0xC42B1C;                 // 演示 .caption .close:hover

    public const int HtClient = 1, HtCaption = 2, HtMinButton = 8, HtMaxButton = 9, HtTop = 12, HtTopLeft = 13, HtTopRight = 14, HtClose = 20;

    public static float Left(float windowWidth) => windowWidth - Width;

    public static CaptionPart HitCaption(float x, float y, float windowWidth, float barHeight)
    {
        float left = Left(windowWidth);
        if (y < 0f || y >= barHeight || x < left || x >= windowWidth) return CaptionPart.None;
        return (CaptionPart)(1 + (int)((x - left) / ButtonWidth));
    }

    public static int CodeOf(CaptionPart part) => part switch
    {
        CaptionPart.Min => HtMinButton,
        CaptionPart.Max => HtMaxButton,
        CaptionPart.Close => HtClose,
        _ => HtClient,
    };

    public static CaptionPart PartOf(int code) => code switch
    {
        HtMinButton => CaptionPart.Min,
        HtMaxButton => CaptionPart.Max,
        HtClose => CaptionPart.Close,
        _ => CaptionPart.None,
    };

    // border：顶边调整大小带的高度（DIP，最大化时不存在）；overTarget：点在标签 / 工具 / 「+」上；
    // barHidden：沉浸模式隐藏顶栏时，除窗口按钮外都算客户区（保留“指针到顶召回”）。
    public static int Hit(float x, float y, float width, float barHeight, bool maximized, float border, bool overTarget, bool barHidden)
    {
        if (!maximized && y >= 0f && y < border)
            return x < border * 2f ? HtTopLeft : x >= width - border * 2f ? HtTopRight : HtTop;
        if (y < 0f || y >= barHeight) return HtClient;
        var part = HitCaption(x, y, width, barHeight);
        if (part != CaptionPart.None) return CodeOf(part);
        return overTarget || barHidden ? HtClient : HtCaption;
    }
}
