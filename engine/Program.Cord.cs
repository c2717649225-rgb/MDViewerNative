using System;
using System.Collections.Generic;

namespace MDViewerNative;

// U5a：书签绳的宿主接线（规格 §4）。几何与交互在 CordGeometry / CordInteraction，绘制在 CordView；
// 这里只管鼠标捕获、提示、平滑跳转、返回书签与失效区域。desk 下书签绳取代系统滚动条。
internal static unsafe partial class Program
{
    static readonly CordInteraction _cord = new();
    static readonly List<CordTick> _ticks = new();
    static bool _cordHot;
    static bool _cordProgressMuted;   // 试用修复：点绳跳转后进度还在滚动中，指针真正移动前不出「已读 n%」（否则停在跳转前的旧值）
    static int _nearTick = -1;

    static bool CordShown => State.Appearance.Desk && !_editing && !HomeShown && Doc.BlockCount > 0;

    static CordFrame CordFrameNow() => new(_viewW / _scale, ContentTop, ContentHeight);

    static float CordProgress() => CordGeometry.Progress(_offY, MaxOff());

    static CordRect CordRectNow() => CordGeometry.Cord(CordFrameNow(), CordProgress());

    static bool OverCord(float x, float y) => CordShown && !OverFind(x, y) && CordGeometry.InStrip(CordFrameNow(), x, y);

    // U5b：刻度随目录条目、滚动位置与视口重算（条目通常几十个，按需算比缓存失效简单可靠）。
    static List<CordTick> TicksNow()
    {
        var heads = ChapterHeads();
        CordGeometry.Ticks(_ticks, heads, FooterModel.CurrentChapter(heads, _offY), CordFrameNow(), MaxOff());
        return _ticks;
    }

    static void PaintCord(IntPtr hdc, RECT client)
    {
        if (!CordShown) return;
        var ticks = TicksNow();
        CordRect? flag = BackFlagShown ? CordGeometry.BackFlag(CordFrameNow(), BackFlagProgress()) : null;
        CordView.Paint(hdc, client, _scale, CordFrameNow(), CordRectNow(), _cordHot || _cord.Dragging, ticks, _nearTick < ticks.Count ? _nearTick : -1,
            flag, DeskAnimations.BackFlagPose(Anim.Progress(DeskAnim.BackFlag, Environment.TickCount64)));
    }

    // 悬停：靠近刻度（±7）时刻度放大并提示章节名；否则悬停绳子（列内、绳长范围内）提亮并提示「已读 n%」。只重画这一列。
    static string CordHint(float x, float y, out OverlayBox box)
    {
        box = default;
        if (OverBackFlag(x, y))
        {
            if (_cordHot || _nearTick >= 0) { _cordHot = false; _nearTick = -1; InvalidateCord(); }
            var b = CordGeometry.BackFlag(CordFrameNow(), BackFlagProgress());
            box = new OverlayBox(b.Left, b.Top, b.Width, b.Height);
            return "返回 · Alt+←";
        }
        bool over = OverCord(x, y);
        int near = over && !_cord.Dragging ? CordGeometry.NearTick(TicksNow(), y) : -1;
        bool hot = over && y <= CordRectNow().Bottom;
        if (hot != _cordHot || near != _nearTick) { _cordHot = hot; _nearTick = near; InvalidateCord(); }
        if (_cord.Pressed) return null;
        if (near >= 0 && _ticks[near].Entry < Doc.TocEntries.Count)
        {
            box = new OverlayBox(_viewW / _scale - DeskMetrics.StripWidth, _ticks[near].Y - DeskMetrics.TickNear, DeskMetrics.StripWidth, DeskMetrics.TickNear * 2f);
            return Doc.TocEntries[_ticks[near].Entry].text;
        }
        if (!hot || _cordProgressMuted) return null;
        var r = CordRectNow();
        box = new OverlayBox(r.Left, r.Top, r.Width, r.Height);
        return $"已读 {MathF.Round(CordProgress() * 100f):0}%";
    }

    static void ClearCordHover()
    {
        if (!_cordHot && _nearTick < 0) return;
        _cordHot = false; _nearTick = -1;
        InvalidateCord();
    }

    static bool CordPointerDown(IntPtr h, float x, float y)
    {
        if (!OverCord(x, y)) return false;
        StopSmoothScroll();
        _cord.Down(y, _offY);
        _ = Externs.SetCapture(h);
        Toolbar.UpdateHint(h, -1, -1, _scale, BuildTabInfo());
        return true;
    }

    static bool CordPointerMove(float y)
    {
        if (!_cord.Pressed) return false;
        bool wasDragging = _cord.Dragging;
        var r = _cord.Move(y, CordFrameNow(), MaxOff());
        if (r.Action == CordAction.Scroll) SetOffset(r.Offset);   // 拖动不留返回书签
        else if (_cord.Dragging != wasDragging) InvalidateCord();
        return true;
    }

    static bool CordPointerUp(float y)
    {
        if (!_cord.Pressed) return false;
        bool dragged = _cord.Dragging;
        int near = _cord.Dragging ? -1 : CordGeometry.NearTick(TicksNow(), y);
        var r = _cord.Up(y, CordFrameNow(), MaxOff(), _offY, near >= 0 ? _ticks[near].Target : null);   // U5b：点在刻度旁跳到该章
        _ = Externs.ReleaseCapture();
        if (r.Action == CordAction.Jump) { JumpWithinDocument(r.Offset); _cordProgressMuted = true; }
        else if (dragged) InvalidateCord();
        return true;
    }

    static void CancelCord()
    {
        if (!_cord.Pressed) return;
        _cord.Cancel();
        InvalidateCord();
    }

    static void InvalidateCord()
    {
        if (_hwnd == IntPtr.Zero) return;
        float left = _viewW / _scale - DeskMetrics.BackFlagRight - DeskMetrics.BackFlagWidth * 1.08f - 6f;   // 盖住从左侧探出的返回书签（含弹入放大）与投影
        var rc = new RECT { L = (int)MathF.Floor(left * _scale), T = (int)MathF.Floor(ContentTop * _scale), R = (int)_viewW, B = (int)_viewH };
        _ = Externs.InvalidateRectRect(_hwnd, ref rc, 0);
    }

    // ---------- U5c：返回书签 ----------
    // 书签只在跳转前后同一文档时画在绳上；跨文档跳转仍可 Alt+← 返回，页脚会提示。
    static NavigationPoint _returnPoint;

    static bool BackFlagShown => CordShown && ReturnPrompt.Visible && _returnPoint != null && _returnPoint.SessionId == Session.RecoveryId;

    static float BackFlagProgress() => _returnPoint == null ? 0f : CordGeometry.Progress(Doc.RestoreReading(_returnPoint.Location), MaxOff());

    static bool OverBackFlag(float x, float y) => BackFlagShown && !OverFind(x, y) && CordGeometry.InBackFlag(CordFrameNow(), BackFlagProgress(), x, y);

    // ---------- U5c：动画时钟（计划 §3.6：一个定时器，没有动画时停表） ----------
    const int TIMER_DESK_ANIMATION = 17;
    static readonly DeskAnimations Anim = new();

    static void StartDeskAnimation(DeskAnim a, int ms)
    {
        Anim.Enabled = SystemAnimationEnabled();
        Anim.Start(a, ms, Environment.TickCount64);
        if (Anim.AnyRunning(Environment.TickCount64)) _ = Externs.SetTimer(_hwnd, (IntPtr)TIMER_DESK_ANIMATION, DeskAnimations.FrameMs, IntPtr.Zero);
    }

    static void OnDeskAnimationTimer(IntPtr h, IntPtr w)
    {
        if (!Anim.AnyRunning(Environment.TickCount64)) _ = Externs.KillTimer(h, w);   // 停表前再画一帧终态
        long now = Environment.TickCount64;
        if (Anim.NeedsFrame(DeskAnim.BackFlag, now)) InvalidateCord();   // 返回书签
        if (Anim.NeedsFrame(DeskAnim.Quiet, now)) InvalidateToolbar(h);   // U6c：读时退后（顶栏很小，动画期间整条重画即可）
        if (Anim.NeedsFrame(DeskAnim.Ink, now)) TickInkCircle(now);   // U7：查找朱笔圈
        if (Anim.NeedsFrame(DeskAnim.Juan, now)) TickJuan(now);   // U8：目录卷目淡入淡出
        if (Anim.NeedsFrame(DeskAnim.WetInk, now)) InvalidateToolbar(h);   // U9c：墨未干
        if (Anim.NeedsFrame(DeskAnim.Spread, now)) TickSpread(now);   // U11b：换纸晕开
    }

    // 页内跳转：记下返回点；desk 下平滑滚到目标，系统关闭动画时直接跳（规格 §4「跳转一律平滑滚动」）。
    static void JumpWithinDocument(float y)
    {
        var before = CaptureNavigation();
        if (State.Appearance.Desk && SystemAnimationEnabled())
        {
            float to = ScrollGeometry.ClampOffset(y, MaxOff(), _scale);
            RecordNavigation(before, new NavigationPoint(Session.RecoveryId, Session.Path, Doc.CaptureReading(to)));
            GlideTo(to);
            return;
        }
        SetOffset(y);
        RecordNavigation(before);
    }

    static void GlideTo(float to)
    {
        if (_smooth.Begin(_offY)) _ = Externs.SetTimer(_hwnd, (IntPtr)TIMER_SMOOTH, 16, IntPtr.Zero);
        _smooth.Retarget(to, MaxOff());
    }
}
