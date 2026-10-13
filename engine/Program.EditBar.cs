using System;

namespace MDViewerNative;

// U9a：编辑条与编辑模式页脚的宿主接线（规格 §8、§5）。几何在 EditBar，绘制在 EditBarView。
// 编辑条只在 desk 编辑模式出现，占顶栏下 44 高（ContentTop 随之下移，编辑框与查找条在它下面）；
// 页脚在编辑模式显示「第 n 行」与「Markdown · 编码」，编辑框为它让出底部 34。
internal static unsafe partial class Program
{
    static EditBarLayout? _editBar;
    static EditBarPart _editBarHover;

    static bool EditBarShown => State.Appearance.Desk && _editing;
    static float EditBarHeight => EditBarShown ? EditBar.Height : 0f;
    static float EditorBottomInset => EditBarShown ? DeskMetrics.FooterHeight : 0f;

    static string CommandLabel(int id)
    {
        foreach (var c in Commands.All) if (c.Id == id) return c.Label;
        return "";
    }

    static bool EditBarSaveEnabled => _dirty && CmdEnabled(1001);

    static EditBarLayout EditBarNow() => EditBarView.Measure(_viewW / _scale, ToolH, _dirty, CommandLabel(1001), null, null);   // 试用反馈：按钮上不写快捷键

    static void PaintEditBar(IntPtr hdc, RECT cr)
    {
        _editBar = null;
        if (!EditBarShown || RenderResources.Rt == IntPtr.Zero) return;
        var l = EditBarNow();
        _editBar = l;
        EditBarView.Paint(hdc, cr, _scale, l, new EditBarPaintState(_dirty, EditBarSaveEnabled, _editBarHover, null, null, CommandLabel(1001)));
    }

    static void InvalidateEditBar()
    {
        if (!EditBarShown) return;
        var r = new RECT { L = 0, T = (int)(ToolH * _scale), R = _viewW, B = (int)MathF.Ceiling((ToolH + EditBar.Height) * _scale) };
        Externs.InvalidateRectRect(_hwnd, ref r, 0);
    }

    static bool OverEditBar(float x, float y) => EditBarShown && y >= ToolH && y < ToolH + EditBar.Height;

    // 按下：保存（有改动时）/ 完成；编辑条上的其余位置吞掉这一击。
    static bool EditBarPointerDown(float x, float y)
    {
        if (!OverEditBar(x, y)) return false;
        var part = _editBar is { } l ? EditBar.Hit(l, x, y) : EditBarPart.None;
        if (part == EditBarPart.Save && EditBarSaveEnabled) _ = SaveFile();
        else if (part == EditBarPart.Done) ToggleEdit();
        return true;
    }

    static void EditBarHoverAt(float x, float y)
    {
        var part = OverEditBar(x, y) && _editBar is { } l ? EditBar.Hit(l, x, y) : EditBarPart.None;
        if (part == _editBarHover) return;
        _editBarHover = part;
        InvalidateEditBar();
    }

    // 悬停提示：按钮上的说明文字（快捷键已画在按钮里）。
    static string EditBarHint(float x, float y, out OverlayBox box)
    {
        box = default;
        if (!OverEditBar(x, y) || _editBar is not { } l) return null;
        var part = EditBar.Hit(l, x, y);
        var b = part == EditBarPart.Save ? l.Save : l.Done;
        if (part == EditBarPart.None) return null;
        box = new OverlayBox(b.X, b.Y, b.W, b.H);
        return part == EditBarPart.Save ? EditBar.SaveTip : EditBar.DoneTip;
    }

    // 编辑模式页脚：第 n 行（源码行，不是折行后的显示行）· Markdown · 编码 · 缩放。
    static FooterContent EditFooterNow()
    {
        int line = Editor?.CaretSourceLine ?? 1;
        float zoom = _bodyFont / RenderResources.Reading.DefaultBodySize;
        return FooterModel.Editing(line, "Markdown · " + EncodingName(Session.Encoding), zoom, FooterNote.Current(Environment.TickCount64));
    }

    // U9b：desk 编辑框是信笺（格线、双线页边、镇尺）；经典界面与高对比度保持系统编辑框。
    static void ApplyLetter()
    {
        if (Editor == null) return;
        Editor.Letter = State.Appearance.Desk && !RenderResources.HighContrast && RenderResources.ActivePaper is PaperColors p ? LetterPaper.Style(p) : null;
    }

    // U9c：墨未干。时间线由宿主持有，顶栏每帧取姿态；系统关闭动画时不记（墨滴直接出现 / 消失）。
    static readonly InkDrops InkMarks = new();

    static void NoteInk(bool fresh)
    {
        if (!State.Appearance.Desk || !SystemAnimationEnabled()) return;
        Toolbar.Ink = InkMarks;
        long now = Environment.TickCount64;
        if (fresh) InkMarks.Fresh(Session.RecoveryId, now); else InkMarks.Dry(Session.RecoveryId, now);
        StartDeskAnimation(DeskAnim.WetInk, InkMarks.RemainingMs(now));
    }

    static string EncodingName(TextEncoding e) => e switch
    {
        TextEncoding.Gbk => "GBK",
        TextEncoding.Utf16LittleEndian => "UTF-16 LE",
        TextEncoding.Utf16BigEndian => "UTF-16 BE",
        _ => "UTF-8",
    };
}
