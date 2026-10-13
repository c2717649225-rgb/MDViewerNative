using System;

namespace MDViewerNative;

// U6c：读时退后的宿主接线。状态机在 TitlebarQuiet（纯），过渡由 DeskAnimations 统一时钟驱动（600 ms）。
internal static unsafe partial class Program
{
    static readonly TitlebarQuiet _quiet = new();
    static float _quietFrom;

    // 用户滚动（SetOffset）后调用。
    static void ObserveQuiet()
    {
        if (!State.Appearance.Desk) return;
        bool engaged = Toolbar.HasFocus || PointerInBar();
        float from = QuietAmount();
        if (_quiet.Observe(_offY, !_editing && Doc.BlockCount > 0, engaged)) QuietChanged(from);
    }

    // 指针进入顶栏、焦点进入顶栏、切换标签或模式。
    static void WakeTitlebar()
    {
        if (!State.Appearance.Desk) return;
        float from = QuietAmount();
        if (_quiet.Wake(_offY)) QuietChanged(from);
    }

    static void QuietChanged(float from)
    {
        _quietFrom = from;
        StartDeskAnimation(DeskAnim.Quiet, DeskMetrics.QuietFadeMs);
        InvalidateToolbar(_hwnd);
    }

    static float QuietAmount() =>
        _quiet.Amount(_quietFrom, DeskAnimations.Ease(Anim.Progress(DeskAnim.Quiet, Environment.TickCount64)));

    static bool PointerInBar()
    {
        if (!GetCursorPos(out POINT p) || !ScreenToClient(_hwnd, ref p)) return false;
        _ = Externs.GetClientRect(_hwnd, out var cr);
        return p.X >= 0 && p.X < cr.R - cr.L && p.Y >= 0 && p.Y < ToolH * _scale;
    }
}
