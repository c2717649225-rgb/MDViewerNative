namespace MDViewerNative;

// v3「安静的书桌」界面尺寸的唯一来源（docs/design/quiet-desk-spec.md；ADR-0003 第 2 条）。
// 单位 DIP。绘制、命中、布局一律引用这里，不在各处写字面量。纯常量，不依赖任何模块。
internal static class DeskMetrics
{
    // 顶栏（§3）
    public const float TitleBarHeight = 44f;
    public const float TabHeight = 36f;
    public const float TabMaxWidth = 220f;
    public const float TabAccentLine = 2f;
    public const float IconOnlyBelowWidth = 1180f;   // 窗口宽度低于此值时工具只显示图标
    public const int TooltipDelayMs = 380;
    public const float ToolbarTextSize = 12.5f;       // 工具与标签文字

    // 正文（§2）
    public const float BodyFontSize = 17f;
    public const float BodyLineHeight = 1.9f;
    public const float BodyMarginLeft = 48f;
    public const float BodyMarginRight = 30f;
    public const float BodyTopSpace = 60f;
    public const float BodyBottomSpace = 110f;
    public const float H1Scale = 2.1f;
    public const float H2Scale = 1.42f;
    public const float ParagraphSpacing = 1.15f;     // em

    // 书签绳（§4）
    public const float StripWidth = 24f;
    public const float CordRight = 5f;
    public const float CordWidth = 11f;
    public const float CordTop = 36f;
    public const float CordBottomReserve = 60f;
    public const float CordNotch = 9f;
    public const float TickWidth = 13f;
    public const float TickWidthMinor = 7f;
    public const float TickHeight = 1.5f;
    public const float TickNear = 7f;                // 刻度靠近判定半径
    public const float TickNearScale = 1.35f;        // 靠近的刻度横向放大
    public const float ChapterTickInset = 70f;       // 刻度 = 标题停在视口上方 70 处时的进度（演示 OFF）
    public const float ChapterJumpInset = 60f;       // 刻度跳转后标题停在视口上方 60 处（演示 OFF-10）
    public const float ChapterSlack = 94f;           // 标题进入视口上方 94 以内即成为当前章节（演示 OFF+24）
    public const float ClickSlop = 4f;               // 按下后移动小于此值算点击
    public const float BackFlagRight = 12f;
    public const float BackFlagWidth = 24f;
    public const float BackFlagHeight = 16f;
    public const float BackFlagHitHalf = 12f;        // 可点区域竖向半高（演示 .back 高 24）

    // 页脚（§5）
    public const float FooterHeight = 34f;
    public const float FooterFade = 26f;
    public const float FooterTextSize = 12f;

    // 读时退后（§3）
    public const float QuietAfterY = 120f;
    public const float QuietDownStep = 6f;
    public const float WakeUpStep = 30f;
    public const float WakeNearTop = 60f;
    public const int QuietFadeMs = 600;

    // 焦点框（§11）
    public const float FocusRingWidth = 1.5f;
    public const float FocusRingWidthHighContrast = 2f;

    // 动画（§1、§4、§6、§8）
    public const int PaperWipeMs = 900;
    public const int BackFlagPopMs = 600;
    public const int InkCircleMs = 500;
    public const int InkDropAppearMs = 450;
    public const int InkDropDryMs = 1200;
    public const int WeightLineMs = 250;
    public const int BackHintMs = 3000;
}
