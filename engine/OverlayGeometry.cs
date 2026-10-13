using System;

namespace MDViewerNative;

// A3 覆盖层几何(目录标尺、展开目录、查找面板/条)的纯函数。
// 绘制、裁剪、命中与揭示共用这里的结果,保证“画在哪里”与“点在哪里”同一口径;
// 所有输入均为 DIP,不依赖窗口句柄,可在自检里逐像素穷举。
internal readonly record struct OverlayBox(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public bool IsEmpty => W <= 0f || H <= 0f;
    public bool Contains(float x, float y) => !IsEmpty && x >= X && x < Right && y >= Y && y < Bottom;
}

internal enum FindLayout { Hidden, Panel, Strip, StripTwoRows, Editing, EditingReplace, Desk, DeskEdit, DeskEditReplace }

internal static class OverlayGeometry
{
    public const float RulerWidth = 32f;      // D1:折叠目录标尺宽
    public const float TocMaxWidth = 240f;    // §4.3:展开目录 min(240, V−48)
    public const float TocMinContext = 48f;   // 展开目录右侧至少露出的正文上下文
    public const float PinnedReadableWidth = 480f; // S80:目录常驻并让位后,正文至少保留的宽度(约 30 字)
    public const float PanelMinView = 720f;   // §4.4:阅读态宽窗右上浮动面板
    public const float PanelWidth = 440f;
    public const float PanelHeight = 44f;
    public const float PanelInset = 12f;
    public const float PanelGap = 4f;
    public const float RowHeight = 44f;       // 窄窗条/编辑态单行
    public const float TwoRowHeight = 80f;    // 窄窗双行/编辑态含替换
    public const float StripInputMin = 180f;  // D4:单行条输入框最小可用宽

    // 条内固定占位(与 FindBar.Reposition 同口径):右边距、按钮组、标签区、间隙。
    public const float StripRightMargin = 12f;
    public const float StripButtons = 244f;
    public const float StripLabel = 64f;
    public const float StripGap = 16f;

    // D1:仅当左留白足以容纳标尺时显示,标尺永不压正文列。
    public static bool RulerShown(float colX) => colX >= RulerWidth;

    // 长文档刻度(自用走查 S69):刻度间距低于 8 DIP 时糊成"条形码"。一、二级按此间距放得下才两级都画,
    // 否则只画一级;一级也放不下时按 8 DIP 抽稀(常见文档 3 DIP 合并不变)。
    public const float RulerMinPitch = 8f;
    public static int RulerCapacity(float height) => (int)(MathF.Max(0f, height - 16f) / RulerMinPitch) + 1;
    public static int RulerDepth(int chapterCount, float height) => chapterCount <= RulerCapacity(height) ? 2 : 1;
    public static float RulerMinGap(int shownCount, float height) => shownCount <= RulerCapacity(height) ? 3f : RulerMinPitch;

    // D5:刻度按文档位置等比分布在标尺带内(上下各留 8 DIP),越界位置钳到两端。
    public static float RulerTickY(float docY, float totalH, float top, float height)
    {
        float t = totalH > 0f ? Math.Clamp(docY / totalH, 0f, 1f) : 0f;
        return top + 8f + t * MathF.Max(0f, height - 16f);
    }

    // §4.3:展开宽 min(240, V−48);视口不足以留出上下文时为 0(目录不可用)。
    public static float TocWidth(float view)
    {
        float w = MathF.Min(TocMaxWidth, view - TocMinContext);
        return w > 0f ? w : 0f;
    }

    // D2 / S80:让出目录宽度后正文仍不窄于 PinnedReadableWidth 时常驻(正文重排到目录右侧);否则是临时覆盖层。
    public static bool TocPinned(float view)
    {
        float w = TocWidth(view);
        return w > 0f && WindowFit.ColW(view, w) >= PinnedReadableWidth;
    }

    // S81:toc=1 表示“宽窗口常驻”。窗口尺寸变化后:放得下常驻时按偏好展开(含启动时第一次拿到真实宽度);
    // 放不下时,原先常驻的收起,不留覆盖层压住正文;窄窗口里临时打开的覆盖层不受影响。
    public static bool TocAfterResize(bool preferPinned, bool showing, bool wasPinned, bool pinnedNow) =>
        pinnedNow ? showing || preferPinned : showing && !wasPinned;

    // S81:只有常驻目录的开关记为偏好;窄窗口里开关临时覆盖层不改变“宽窗口常驻”的偏好。
    public static bool TocPreference(bool preferPinned, bool showing, bool pinnedNow) => pinnedNow ? showing : preferPinned;

    public static float StripCountWidth(float view) => view >= 620f ? 100f : 60f;

    // D4:单行放得下 ≥180 DIP 输入框才用单行,否则计数与选项落第二行。
    public static bool StripFitsOneRow(float view) =>
        view - StripRightMargin - StripButtons - StripCountWidth(view) - StripLabel - StripGap >= StripInputMin;

    public static FindLayout Find(bool visible, bool editing, bool replaceRow, float view, bool desk = false)
    {
        if (!visible) return FindLayout.Hidden;
        if (editing && desk) return replaceRow ? FindLayout.DeskEditReplace : FindLayout.DeskEdit;   // 试用修复：desk 编辑态也换装
        if (editing) return replaceRow ? FindLayout.EditingReplace : FindLayout.Editing;
        if (desk) return FindLayout.Desk;
        if (view >= PanelMinView) return FindLayout.Panel;
        return StripFitsOneRow(view) ? FindLayout.Strip : FindLayout.StripTwoRows;
    }

    // 顶部被推开的高度:浮动面板不占文档流,为 0;条/编辑态为其行高。
    public static float FindHeight(FindLayout layout) => layout switch
    {
        FindLayout.Strip or FindLayout.Editing or FindLayout.DeskEdit => RowHeight,
        FindLayout.StripTwoRows or FindLayout.EditingReplace or FindLayout.DeskEditReplace => TwoRowHeight,
        _ => 0f,
    };

    public static OverlayBox FindBox(FindLayout layout, float view, float toolH)
    {
        switch (layout)
        {
            case FindLayout.Hidden: return default;
            case FindLayout.Desk:
                float dw = DeskFindWidth(DeskFindInputWidth(view));
                return new OverlayBox(MathF.Max(DeskFindMinLeft, view - DeskFindRight - dw), toolH + DeskFindTop, dw, DeskFindHeight);
            case FindLayout.Panel:
                float w = MathF.Min(PanelWidth, view - 2f * PanelInset);
                return new OverlayBox(view - PanelInset - w, toolH + PanelGap, w, PanelHeight);
            default:
                return new OverlayBox(0f, toolH, MathF.Max(0f, view), FindHeight(layout));
        }
    }

    // D3:只有查找覆盖层与目录水平相交(窄窗全宽条)时,目录才从它下方开始。
    public static float TocTop(float contentTop, OverlayBox find, float tocWidth)
    {
        if (find.IsEmpty || tocWidth <= 0f || find.X >= tocWidth) return contentTop;
        return MathF.Max(contentTop, find.Bottom);
    }
    public const float ControlHeight = 28f;

    // U7：desk 查找框（演示稿 .findbar）：正文区右上浮动，距顶栏 10、距右 30；1 DIP 描边内是 5/6/5/12 内边距，
    // 依次为放大镜 15（右距 6）、输入 210、计数 48（右距 6）、28×28 图标钮（间距 2）。
    // 演示稿只有上一处/下一处/关闭；“选项”是既有功能（大小写/整词/正则/仅选区/历史），保留为第四个图标钮。
    public const float DeskFindTop = 10f, DeskFindRight = 30f, DeskFindMinLeft = 6f;
    public const float DeskFindHeight = 40f, DeskFindRadius = 12f, DeskFindIcon = 15f;
    public const float DeskFindInput = 210f, DeskFindInputMin = 60f, DeskFindCount = 48f, DeskFindButton = 28f;
    const float DeskFindFixed = 1f + 12f + DeskFindIcon + 6f + 2f + DeskFindCount + 6f + 4f * DeskFindButton + 3f * 2f + 6f + 1f;
    public static float DeskFindWidth(float input) => DeskFindFixed + input;
    public static float DeskFindInputWidth(float view) =>
        Math.Clamp(view - DeskFindRight - DeskFindMinLeft - DeskFindFixed, DeskFindInputMin, DeskFindInput);
    public static OverlayBox DeskFindIconBox(OverlayBox box) =>
        new(box.X + 13f, box.Y + (box.H - DeskFindIcon) / 2f, DeskFindIcon, DeskFindIcon);

    // D3/§4.4:查找覆盖层挡住视口顶端的高度(DIP,相对内容顶)。命中落在这段里要主动露出;
    // 文档开头无法再往上滚时,宿主允许在查找打开期间向上越界同样的高度。
    public static float RevealInset(float contentTop, OverlayBox find) =>
        find.IsEmpty ? 0f : MathF.Max(0f, find.Bottom - contentTop);

    // 查找控件排布(DIP)。浮动面板去掉“查找”标签(输入框提示已说明),按钮收窄以保证输入宽;
    // 窄窗单行/编辑态沿用既有排布;窄窗两行时计数与选项落第二行(D4)。
    // 试用修复：desk 编辑态的查找 / 替换条（仍是预留的 44 / 80 高条，不盖住编辑区）。换成与 desk 查找框一致的样子：
    // 左侧放大镜 15，输入框画成 28 高、7 圆角的描边框（左距 37），内嵌 20 高无边框 EDIT；右侧计数 48 + 四个 28 图标钮（间距 2，右距 16）；
    // 替换行与查找行对齐，「替换 / 全部替换」两个文字钮从计数处开始。没有「查找 / 替换为」文字标签（提示文字已说明）。
    public const float DeskEditLeft = 14f, DeskEditRight = 16f, DeskEditFieldX = DeskEditLeft + DeskFindIcon + 8f, DeskEditFieldRadius = 7f;
    public static OverlayBox DeskEditField(float view, float toolH, bool replaceRow)
    {
        float y = toolH + 8f + (replaceRow ? 36f : 0f);
        float countX = view - DeskEditRight - 4f * DeskFindButton - 3f * 2f - 6f - DeskFindCount;
        return new OverlayBox(DeskEditFieldX, y, MathF.Max(60f, countX - 8f - DeskEditFieldX), ControlHeight);
    }

    public static OverlayBox DeskEditIconBox(float toolH) => new(DeskEditLeft, toolH + 8f + (ControlHeight - DeskFindIcon) / 2f, DeskFindIcon, DeskFindIcon);

    public static FindControlBoxes FindControls(FindLayout layout, float view, float toolH)
    {
        const float h = ControlHeight;
        switch (layout)
        {
            case FindLayout.Hidden:
                return default;
            case FindLayout.DeskEdit or FindLayout.DeskEditReplace:
                {
                    float y = toolH + 8f, b = DeskFindButton, right = view - DeskEditRight;
                    var close = new OverlayBox(right - b, y, b, b);
                    var options = new OverlayBox(close.X - 2f - b, y, b, b);
                    var next = new OverlayBox(options.X - 2f - b, y, b, b);
                    var prev = new OverlayBox(next.X - 2f - b, y, b, b);
                    var count = new OverlayBox(prev.X - 6f - DeskFindCount, y, DeskFindCount, b);
                    OverlayBox Inset(OverlayBox f) => new(f.X + 8f, f.Y + 4f, MathF.Max(20f, f.W - 16f), 20f);
                    var input = Inset(DeskEditField(view, toolH, false));
                    if (layout == FindLayout.DeskEdit)
                        return new FindControlBoxes(default, input, count, prev, next, close, options, default, default, default, default);
                    float y2 = y + 36f;
                    return new FindControlBoxes(default, input, count, prev, next, close, options, default,
                        Inset(DeskEditField(view, toolH, true)), new OverlayBox(count.X, y2, 60f, h), new OverlayBox(count.X + 64f, y2, right - count.X - 64f, h));
                }
            case FindLayout.Desk:
                {
                    var box = FindBox(layout, view, toolH);
                    float y = box.Y + 6f, x = DeskFindIconBox(box).Right + 6f, b = DeskFindButton;
                    // 无边框单行 EDIT 文字顶对齐：给 20 高并在框内垂直居中，光标与放大镜同一中线。
                    var input = new OverlayBox(x, box.Y + (box.H - 20f) / 2f, DeskFindInputWidth(view), 20f);
                    var count = new OverlayBox(input.Right + 2f, y, DeskFindCount, b);
                    var prev = new OverlayBox(count.Right + 6f, y, b, b);
                    var next = new OverlayBox(prev.Right + 2f, y, b, b);
                    var options = new OverlayBox(next.Right + 2f, y, b, b);
                    var close = new OverlayBox(options.Right + 2f, y, b, b);
                    return new FindControlBoxes(default, input, count, prev, next, close, options, default, default, default, default);
                }
            case FindLayout.Panel:
                {
                    var box = FindBox(layout, view, toolH);
                    float y = box.Y + 8f, right = box.Right - 8f;
                    var options = new OverlayBox(right - 60f, y, 60f, h);
                    var close = new OverlayBox(options.X - 4f - 44f, y, 44f, h);
                    var next = new OverlayBox(close.X - 4f - 48f, y, 48f, h);
                    var prev = new OverlayBox(next.X - 4f - 48f, y, 48f, h);
                    var count = new OverlayBox(prev.X - 4f - 56f, y, 56f, h);
                    var input = new OverlayBox(box.X + 8f, y, MathF.Max(40f, count.X - 4f - (box.X + 8f)), h);
                    return new FindControlBoxes(default, input, count, prev, next, close, options, default, default, default, default);
                }
            case FindLayout.StripTwoRows:
                {
                    float y = toolH + 8f, y2 = y + 36f, right = view - StripRightMargin, bx = right - 184f;
                    var input = new OverlayBox(64f, y, MathF.Max(60f, bx - 4f - 64f), h);
                    return new FindControlBoxes(new OverlayBox(12f, y, 48f, h), input, new OverlayBox(64f, y2, 100f, h),
                        new OverlayBox(bx, y, 60f, h), new OverlayBox(bx + 64f, y, 60f, h), new OverlayBox(bx + 128f, y, 56f, h),
                        new OverlayBox(right - 56f, y2, 56f, h), default, default, default, default);
                }
            case FindLayout.Editing or FindLayout.EditingReplace when !StripFitsOneRow(view):
                {
                    // 窄窗编辑态:高度仍是 44/80 预留,去掉“查找/替换为”标签(输入框提示已说明),按钮用面板的紧凑宽度。
                    float y = toolH + 8f, right = view - StripRightMargin;
                    var options = new OverlayBox(right - 60f, y, 60f, h);
                    var close = new OverlayBox(options.X - 4f - 44f, y, 44f, h);
                    var next = new OverlayBox(close.X - 4f - 48f, y, 48f, h);
                    var prev = new OverlayBox(next.X - 4f - 48f, y, 48f, h);
                    var count = new OverlayBox(prev.X - 4f - 56f, y, 56f, h);
                    var input = new OverlayBox(12f, y, MathF.Max(60f, count.X - 4f - 12f), h);
                    bool replace = layout == FindLayout.EditingReplace;
                    return new FindControlBoxes(default, input, count, prev, next, close, options, default,
                        replace ? new OverlayBox(12f, y + 36f, input.W, h) : default,
                        replace ? new OverlayBox(prev.X, y + 36f, 76f, h) : default,
                        replace ? new OverlayBox(prev.X + 80f, y + 36f, 104f, h) : default);
                }
            default:
                {
                    float y = toolH + 8f, right = view - StripRightMargin, bx = right - StripButtons, cw = StripCountWidth(view);
                    var input = new OverlayBox(64f, y, MathF.Max(60f, bx - cw - 64f - StripGap), h);
                    bool replace = layout == FindLayout.EditingReplace;
                    return new FindControlBoxes(new OverlayBox(12f, y, 48f, h), input, new OverlayBox(input.Right + 4f, y, cw, h),
                        new OverlayBox(bx, y, 60f, h), new OverlayBox(bx + 64f, y, 60f, h), new OverlayBox(bx + 128f, y, 56f, h),
                        new OverlayBox(bx + 188f, y, 56f, h),
                        replace ? new OverlayBox(12f, y + 36f, 48f, h) : default,
                        replace ? new OverlayBox(64f, y + 36f, input.W, h) : default,
                        replace ? new OverlayBox(bx, y + 36f, 76f, h) : default,
                        replace ? new OverlayBox(bx + 80f, y + 36f, 104f, h) : default);
                }
        }
    }
}

internal readonly record struct FindControlBoxes(OverlayBox Label, OverlayBox Input, OverlayBox Count, OverlayBox Prev, OverlayBox Next,
    OverlayBox Close, OverlayBox Options, OverlayBox ReplaceLabel, OverlayBox Replace, OverlayBox ReplaceOne, OverlayBox ReplaceAll)
{
    public OverlayBox[] All => new[] { Label, Input, Count, Prev, Next, Close, Options, ReplaceLabel, Replace, ReplaceOne, ReplaceAll };
}
