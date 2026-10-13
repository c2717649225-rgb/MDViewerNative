using System;

namespace MDViewerNative;

// U6b：desk 合并标题栏的宿主接线（docs/design/u6b-titlebar.md）。命中判定在 DeskFrame（纯），
// 这里只把 WM_NCCALCSIZE / WM_NCHITTEST / 非客户区鼠标消息翻译过去；classic 下全部直通 DefWindowProc。
internal static unsafe partial class Program
{
    static bool CustomFrame => State.Appearance.Desk;
    static bool _ncTracking;

    // 顶边调整大小带 / 最大化内缩：按窗口所在显示器的 DPI 取系统边框厚度（像素）。
    static int FrameThickness(IntPtr h)
    {
        uint dpi = Externs.GetDpiForWindow(h);
        if (dpi == 0) dpi = 96;
        return GetSystemMetricsForDpi(33 /*SM_CYFRAME*/, dpi) + GetSystemMetricsForDpi(92 /*SM_CXPADDEDBORDER*/, dpi);
    }

    // 去掉系统标题栏：左右下边框照旧由系统算，顶边还给客户区；最大化时顶部内缩，免得顶栏溢出屏幕。
    static IntPtr OnNcCalcSize(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (!CustomFrame || w == 0) return Externs.DefWindowProcW(h, m, w, l);
        var rect = (RECT*)l;
        int top = rect->T;
        var result = Externs.DefWindowProcW(h, m, w, l);
        if (result != 0) return result;
        rect->T = top + (IsZoomed(h) ? FrameThickness(h) : 0);
        return 0;
    }

    static IntPtr OnNcHitTest(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        var system = Externs.DefWindowProcW(h, m, w, l);
        if (!CustomFrame || system != (IntPtr)DeskFrame.HtClient || RenderResources.Rt == IntPtr.Zero) return system;
        var p = new POINT { X = (short)(long)l, Y = (short)((long)l >> 16) };
        if (!ScreenToClient(h, ref p)) return system;
        _ = Externs.GetClientRect(h, out var cr);
        PrepareToolbar();
        float x = p.X / _scale, y = p.Y / _scale;
        return DeskFrame.Hit(x, y, (cr.R - cr.L) / _scale, ToolH, IsZoomed(h), FrameThickness(h) / _scale,
            Toolbar.OverTarget(x, y), ZenHidden);
    }

    // 窗口按钮落在我们自己的顶栏里：它们的非客户区鼠标消息自己处理，交给 DefWindowProc 会画出旧式按钮。
    static bool OnNcMouse(IntPtr h, uint m, IntPtr w, IntPtr l, out IntPtr result)
    {
        result = default;
        if (!CustomFrame) return false;
        var part = DeskFrame.PartOf((int)w);
        switch (m)
        {
            case 0x00A0: // WM_NCMOUSEMOVE
                if (!_ncTracking)
                {
                    var tme = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = 0x02 | 0x10 /*TME_LEAVE | TME_NONCLIENT*/, hwndTrack = h };
                    _ncTracking = TrackMouseEvent(ref tme);
                }
                if (Toolbar.SetCaptionHover(part)) InvalidateToolbar(h);
                UpdateCaptionHint(h, part);
                WakeTitlebar();   // U6c：指针在顶栏空白或窗口按钮上
                return part != CaptionPart.None;
            case 0x02A2: // WM_NCMOUSELEAVE
                _ncTracking = false;
                if (Toolbar.SetCaptionHover(CaptionPart.None) | Toolbar.SetCaptionPressed(CaptionPart.None)) InvalidateToolbar(h);
                UpdateCaptionHint(h, CaptionPart.None);
                return false;
            case 0x00A1: // WM_NCLBUTTONDOWN
                Toolbar.HideTip();
                if (part == CaptionPart.None) return false;
                if (Toolbar.SetCaptionPressed(part)) InvalidateToolbar(h);
                return true;
            case 0x00A2: // WM_NCLBUTTONUP
                if (part == CaptionPart.None) return false;
                bool act = Toolbar.CaptionPressed == part;
                if (Toolbar.SetCaptionPressed(CaptionPart.None)) InvalidateToolbar(h);
                if (act) CaptionAction(h, part);
                return true;
            case 0x00A3: // WM_NCLBUTTONDBLCLK：按钮上的双击不交给系统（标题栏空白处的双击最大化照常由系统处理）
                return part != CaptionPart.None;
        }
        return false;
    }

    static void CaptionAction(IntPtr h, CaptionPart part)
    {
        switch (part)
        {
            case CaptionPart.Min: _ = Externs.SendMessageW(h, 0x0112 /*WM_SYSCOMMAND*/, 0xF020 /*SC_MINIMIZE*/, 0); break;
            case CaptionPart.Max: _ = Externs.SendMessageW(h, 0x0112, IsZoomed(h) ? 0xF120 /*SC_RESTORE*/ : 0xF030 /*SC_MAXIMIZE*/, 0); break;
            case CaptionPart.Close: _ = Externs.SendMessageW(h, 0x0112, 0xF060 /*SC_CLOSE*/, 0); break;
        }
    }

    // 最小化、关闭给提示；最大化不给——Win11 会在那里弹出贴靠布局，两者会叠在一起。
    static void UpdateCaptionHint(IntPtr h, CaptionPart part)
    {
        string hint = part switch { CaptionPart.Min => "最小化", CaptionPart.Close => "关闭 · Alt+F4", _ => null };
        _ = Externs.GetClientRect(h, out var cr);
        float left = DeskFrame.Left((cr.R - cr.L) / _scale) + ((int)part - 1) * DeskFrame.ButtonWidth;
        Toolbar.UpdateHint(h, -1, -1, _scale, BuildTabInfo(), hint, new OverlayBox(left, 0f, DeskFrame.ButtonWidth, ToolH));
    }

    // 创建后让系统按 WM_NCCALCSIZE 重算一次框架。
    static void ApplyCustomFrame(IntPtr h)
    {
        if (!CustomFrame) return;
        Toolbar.Caption = true;
        Toolbar.Maximized = IsZoomed(h);
        _ = Externs.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, 0x0020 | 0x0001 | 0x0002 | 0x0004 | 0x0010 /*FRAMECHANGED|NOSIZE|NOMOVE|NOZORDER|NOACTIVATE*/);
    }
}
