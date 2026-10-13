using System;

namespace MDViewerNative;

// 窗口过程的视口消息：最小尺寸、绘制、尺寸变化、滚动条、横纵滚轮与 DPI 变化。由 WndProcInner 分派。
// E17b 从 switch 原样搬出（这些分支都以 return 结束，没有交给 DefWindowProcW 的路径）。
internal static unsafe partial class Program
{
    static IntPtr OnGetMinMaxInfo(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        var limits = (int*)l;
        (limits[6], limits[7]) = WindowGeometry.MinTrackSize(_scale);
        return 0;
    }

    static readonly PaintBuffer _paintBuffer = new();

    static IntPtr OnPaint(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        // 更新区域分工:工具栏与正文各自只在被波及时才重绘(选区/滚动不惊动工具栏)
        bool hasUpd = GetUpdateRect(h, out RECT ur, 0);
        if (!hasUpd) return default;
        var (toolHit, contentHit) = WindowGeometry.HitStrips(ur, ToolH, _scale);
        var ps = default(Externs.PAINTSTRUCT);
        var win = Externs.BeginPaint(h, out ps);
        if (win == IntPtr.Zero) return default;
        _ = Externs.GetClientRect(h, out var cr);
        // 试用修复：各层先画到离屏缓冲，最后一次贴出，DWM 不会在层与层之间露出半成品（闪烁）。
        var hdc = _paintBuffer.Begin(win, cr.R - cr.L, cr.B - cr.T, ps.rcPaint, out bool fresh);
        var shown = fresh ? new RECT { L = 0, T = 0, R = cr.R - cr.L, B = cr.B - cr.T } : ps.rcPaint;
        if (hdc == IntPtr.Zero) hdc = win;
        else if (fresh) { toolHit = true; contentHit = true; }   // 缓冲刚重建：整幅重画
        try
        {
            if (contentHit && HomeShown && RenderResources.Rt != IntPtr.Zero)
            {
                PaintHome(hdc, cr);   // U10：起始页代替正文
                if (FooterShown) FooterView.Paint(hdc, cr, _scale, HomeFooterNow());
            }
            else if (contentHit && !_editing && RenderResources.Rt != IntPtr.Zero)
            {
                Doc.Paint(cr, hdc, _offY, ContentHeight, ContentTop, _recent, TocTop - ContentTop, _editing || State.Appearance.Desk ? null : ReturnPrompt, ZenHidden);
                PaintCord(hdc, cr);   // U5a：书签绳
                if (FooterShown) FooterView.Paint(hdc, cr, _scale, FooterNow());   // U4：页脚浮在正文上
                ScheduleAnimation();   // 绘制后按最近到期帧安排下一次推进(无动画则清计时器)
            }
            if (contentHit && _editing && RenderResources.Rt != IntPtr.Zero && _hEdit != IntPtr.Zero)
            {
                Editor.PaintGutter(cr, hdc);
                PaintEditBar(hdc, cr);   // U9a：编辑条
                if (FooterShown) FooterView.Paint(hdc, cr, _scale, EditFooterNow());   // U9a：编辑模式页脚
            }
            if (contentHit && Find.Visible) Find.Paint(hdc, cr);
            if (contentHit && !_editing && RenderResources.Rt != IntPtr.Zero) PaintJuan(hdc, cr);   // U8：目录卷目盖住正文与页脚
            if (contentHit && !_editing && RenderResources.Rt != IntPtr.Zero) PaintSelectionBubble(hdc, cr);   // U7：选区气泡在最上层
            if (toolHit)
            {
                var tabInfo = ToolbarTabInfo();
                Toolbar.Quiet = State.Appearance.Desk ? QuietAmount() : 0f;
                Toolbar.Paint(hdc, cr, _scale, CmdStateNow(), tabInfo, _zenPainted = ZenHidden);
            }
            // 调试期曾在此覆盖标题为 SelDebugTitle();正式版保持 UpdateTitle 的文件名标题
        }
        catch (Exception ex)
        {
            Log("PAINT " + ex + Environment.NewLine + (ex.StackTrace ?? ""));
        }
        // 查找开关、编辑切换、焦点变化会改变沉浸隐藏条件:内容帧检查一次,需要时补刷工具栏带。
        if (ZenHidden != _zenPainted) InvalidateToolbar(h);
        PaintSpread(hdc);   // U11b：换纸晕开盖在最上层
        if (hdc != win) _paintBuffer.Present(win, shown);
        _ = Externs.EndPaint(h, in ps);
        return default;
    }

    static IntPtr OnSize(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (!WindowGeometry.UnpackSize(l, out _viewW, out _viewH)) return default; // 最小化:跳过,恢复时重算
        if (!_editing) KeepReadingPlace(() =>
        {
            bool wasPinned = Doc.ShowToc && Doc.TocPinned;
            Doc.OnWidth(ReadingWidth());
            bool toc = !State.Appearance.Desk && OverlayGeometry.TocAfterResize(_cfgToc, Doc.ShowToc, wasPinned, Doc.TocPinned);
            if (toc != Doc.ShowToc) Doc.SetToc(toc);   // S81:常驻目录跟随窗口宽度
        });
        LayoutControls();
        _offY = Snap(MathF.Min(_offY, MaxOff()));
        UpdateScroll();
        _ = Externs.InvalidateRect(h, IntPtr.Zero, 0);
        return default;
    }

    static IntPtr OnVScroll(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        int cmd = (int)(w & 0xFFFF);
        if (ScrollGeometry.TryScrollBarTarget(cmd, _offY, _viewH / _scale, out float barTarget)) SetOffset(barTarget);   // 行/页/顶/底
        else if (cmd is 4 or 5)
        {
            var si = new SCROLLINFO { cbSize = (uint)sizeof(SCROLLINFO), fMask = SIF_TRACKPOS };
            _ = Externs.GetScrollInfo(h, SB_VERT, ref si);
            SetOffset(si.nTrackPos / _scale);   // nTrackPos 是物理像素,SetOffset 收 DIP
        }
        return default;
    }

    static IntPtr OnMouseHWheel(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        // Tilt wheels and trackpad horizontal gestures. Positive delta means
        // "toward the right" here, the opposite sign convention from the
        // vertical wheel, so it is applied directly rather than negated (30).
        int hDelta = (short)((long)w >> 16);
        if (GetCursorPos(out POINT hp) && ScreenToClient(_hwnd, ref hp) && OverBody(hp.X / _scale, hp.Y / _scale)
            && Doc.ScrollCodeAt(hp.X / _scale, hp.Y / _scale - ContentTop + _offY, hDelta / 120f * 60f))
        {
            _ = Externs.InvalidateRect(h, IntPtr.Zero, 0);
        }
        return default;
    }

    static IntPtr OnMouseWheel(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        int delta = (short)((long)w >> 16);
        if (JuanOpen) { JuanWheel(delta); return default; }
        if (HomeShown) { if ((Externs.GetKeyState(0x11) & 0x8000) == 0) HomeWheel(delta); return default; }   // U10   // U8：卷目打开时滚轮只滚条目
        // TOC 侧栏内滚轮:独立滚动目录
        if (GetCursorPos(out POINT wp) && ScreenToClient(_hwnd, ref wp) && OverToc(wp.X / _scale, wp.Y / _scale))
        {
            Doc.TocScroll = Snap(MathF.Max(0f, Doc.TocScroll - delta / 120f * 64f));   // 侧栏行独立像素对齐:不量化会逐行 ±1px 抖
            Doc.TocUserScrolled = true;   // 手动滚动后暂停自动跟随,文档再次滚动时恢复
            float maxToc = MathF.Max(0f, Doc.TocEntries.Count * 26f + 16f - MathF.Max(1f, _viewH / _scale - TocTop));   // 视口统一用 DIP(原混用物理像素,150% 下尾部条目不可达)
            if (Doc.TocScroll > maxToc) Doc.TocScroll = MathF.Floor(maxToc * _scale) / _scale;   // 上限口径统一向下落网格(Snap 向上取整会越 maxToc 半格)
            _ = Externs.InvalidateRect(h, IntPtr.Zero, 0);
            return default;
        }
        if ((Externs.GetKeyState(0x11) & 0x8000) != 0)   // Ctrl+滚轮 = 字号缩放
        {
            ZoomFont(_bodyFont + MathF.Sign(delta), h);
            return default;
        }
        // 代码块横向滚动(30):代码行不折行,超出的部分靠左右滚动看到。
        // Shift+滚轮是通用的"横向滚动"约定;不按 Shift 时,只有指针正好
        // 停在一个确实溢出的代码块上才横向滚,否则照常整页纵向滚动。
        if (GetCursorPos(out POINT cp) && ScreenToClient(_hwnd, ref cp))
        {
            bool shift = (Externs.GetKeyState(0x10) & 0x8000) != 0;
            float docX = cp.X / _scale, docY = cp.Y / _scale - ContentTop + _offY;
            if (OverBody(cp.X / _scale, cp.Y / _scale)
                && Doc.ScrollCodeAt(docX, docY, -delta / 120f * 60f))
            {
                _ = Externs.InvalidateRect(h, IntPtr.Zero, 0);
                return default;
            }
            if (shift) return default;   // Shift 表明用户要横向滚,别退化成纵向
        }
        // 平滑滚动:目标累积 + 指数缓出
        if (_smooth.Begin(_offY)) _ = Externs.SetTimer(h, (IntPtr)TIMER_SMOOTH, 16, IntPtr.Zero);
        _smooth.Accumulate(delta, MaxOff());
        return default;
    }

    static IntPtr OnDpiChanged(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (Toolbar.CancelPointer()) Externs.ReleaseCapture();
        float ns = WindowGeometry.DpiToScale((int)w & 0xFFFF);
        if (WindowGeometry.HasScaleChanged(ns, _scale))
        {
            _scale = ns;
            RenderResources.Scale = ns;   // Paint/DrawToolbar 每帧按 96*Scale 重设 RT DPI
            Editor?.SetScale(ns);
        }
        var sug = *(RECT*)(IntPtr)l;
        _ = Externs.SetWindowPos(h, IntPtr.Zero, sug.L, sug.T, sug.R - sug.L, sug.B - sug.T, 0x0004 /*SWP_NOZORDER*/);
        // 随后的 WM_SIZE 会用新 _scale 重算 Doc.OnWidth / 滚动 / EDIT 位置
        return default;
    }
}
